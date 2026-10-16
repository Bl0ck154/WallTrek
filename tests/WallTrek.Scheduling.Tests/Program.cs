using WallTrek.Services.Scheduling;

var tests = new (string Name, Func<Task> Run)[]
{
    ("Spring-forward gap resolves once to first valid minute", SpringDst),
    ("Fall-back repeated hour chooses first occurrence", FallDst),
    ("Time zone changes recompute UTC due time", TimeZones),
    ("New schedule does not backfill earlier today", Activation),
    ("Missed days recover one latest due slot after reboot", MissedAndRestart),
    ("Sleep/wake recovery stays idempotent on repeated polls", Wake),
    ("Crash while image is in flight is not re-billed", Interrupted),
    ("Failed image is settled and cannot change wallpaper twice", Failure),
    ("Busy UI gets bounded 1/5 minute preflight retry", BusyBackoff),
    ("Multi-process file lock prevents parallel image dispatch", Concurrent),
    ("Invalid settings/journal never overwrite valid state", Validation)
};
int failed = 0;
foreach (var (name, test) in tests)
{
    try { await test(); Console.WriteLine("PASS " + name); }
    catch (Exception ex)
    {
        ++failed;
        Console.Error.WriteLine("FAIL " + name + ": " + ex.GetType().Name + " / " + ex.Message);
    }
}
Environment.ExitCode = failed == 0 ? 0 : 1;

static void Require(bool ok, string message)
{
    if (!ok) throw new Exception(message);
}
static DailyScheduleConfig Config(string time = "09:00", string zone = "Local",
    DateTimeOffset? activated = null) =>
    new(time, zone, activated ?? new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
static DateTimeOffset Utc(int y,int m,int d,int hh,int mm = 0) =>
    new(y,m,d,hh,mm,0,TimeSpan.Zero);
static string TempPath() =>
    Path.Combine(Path.GetTempPath(), "walltrek-daily-ci-" + Guid.NewGuid().ToString("N"));
static async Task SpringDst()
{
    var c = Config("02:30", "Eastern Standard Time");
    var slot = DailyWallClock.SlotUtc(new DateOnly(2026, 3, 8), c);
    Require(slot == Utc(2026,3,8,7), "Spring gap should be 03:00 EDT = 07:00 UTC.");
    await Task.CompletedTask;
}
static async Task FallDst()
{
    var c = Config("01:30", "Eastern Standard Time");
    var slot = DailyWallClock.SlotUtc(new DateOnly(2026,11,1),c);
    Require(slot == Utc(2026,11,1,5,30), "Fall overlap must pick first 01:30 (UTC-4).");
    var id1 = DailyWallClock.LatestDue(c, Utc(2026,11,1,5,31))!.Key;
    var id2 = DailyWallClock.LatestDue(c, Utc(2026,11,1,6,31))!.Key;
    Require(id1 == id2, "The second 01:30 has an identical daily slot identity.");
    await Task.CompletedTask;
}
static async Task TimeZones()
{
    var day = new DateOnly(2026, 10, 16);
    var ny = DailyWallClock.SlotUtc(day, Config("09:00", "Eastern Standard Time"));
    var utc = DailyWallClock.SlotUtc(day, Config("09:00", "UTC"));
    Require(ny == Utc(2026,10,16,13), "Eastern in October should be UTC-4");
    Require(utc == Utc(2026,10,16,9), "UTC zone was not respected");
    Require(DailyWallClock.NextUtc(Config("09:00","UTC"), Utc(2026,10,16,10)) ==
        Utc(2026,10,17,9), "Next slot not next local day");
    await Task.CompletedTask;
}
static async Task Activation()
{
    var c = Config("09:00","UTC",Utc(2026,10,16,11));
    Require(DailyWallClock.LatestDue(c, Utc(2026,10,16,12)) is null,
        "Enabling schedule at 11AM must not run an earlier 9AM slot");
    Require(DailyWallClock.LatestDue(c, Utc(2026,10,17,9,1))?.LocalDate ==
        new DateOnly(2026,10,17), "Tomorrow must run");
    await Task.CompletedTask;
}
static async Task MissedAndRestart()
{
    var path = TempPath();
    var now = Utc(2026,10,16,12);
    var store = new DailyScheduleJournalStore(path);
    var calls = 0;
    try
    {
        var engine = new DailyScheduleCoordinator(store, () => now);
        var result = await engine.CheckAsync(Config("09:00","UTC"),
            _ => { ++calls; return Task.FromResult(ScheduledRunOutcome.Applied); });
        Require(calls == 1 && result.LastStatus.Contains("successfully"),
            "Boot after 4 missed days should generate latest due exactly once");
        var restarted = new DailyScheduleCoordinator(new DailyScheduleJournalStore(path), () => now);
        await restarted.CheckAsync(Config("09:00","UTC"),
            _ => { ++calls; return Task.FromResult(ScheduledRunOutcome.Applied); });
        Require(calls == 1, "Restart regenerated already-settled slot");
        now = Utc(2026,10,17,10);
        await restarted.CheckAsync(Config("09:00","UTC"),
            _ => { ++calls; return Task.FromResult(ScheduledRunOutcome.Applied); });
        Require(calls == 2, "Next daily slot never ran");
    }
    finally { Cleanup(path); }
}
static async Task Wake()
{
    var path = TempPath();
    var now = Utc(2026,10,16,8,30);
    var calls = 0;
    try
    {
        var engine = new DailyScheduleCoordinator(new DailyScheduleJournalStore(path), () => now);
        // One earlier day is already handled (so 08:30 polls don't catch up yesterday).
        var config = Config("09:00","UTC",Utc(2026,10,16,7));
        await engine.CheckAsync(config, _ => { ++calls; return Task.FromResult(ScheduledRunOutcome.Applied); });
        Require(calls == 0, "Generated before due time");
        now = Utc(2026,10,16,11); // PC woke 2h after scheduled time.
        for (int i=0;i<8;i++)
            await engine.CheckAsync(config, _ =>
            { ++calls; return Task.FromResult(ScheduledRunOutcome.Applied); });
        Require(calls == 1, "Polling after wake duplicated generation");
    }
    finally { Cleanup(path); }
}
static async Task Interrupted()
{
    var path = TempPath();
    var now = Utc(2026,10,16,12);
    var config = Config("09:00","UTC");
    var calls = 0;
    try
    {
        var store = new DailyScheduleJournalStore(path);
        var due = DailyWallClock.LatestDue(config, now)!;
        store.Save(new DailyScheduleJournal
        {
            ConfigSignature = config.Signature,
            PendingSlot = due.Key,
            PendingStartedUtc = Utc(2026,10,16,9)
        });
        var restarted = new DailyScheduleCoordinator(store, () => now);
        var result = await restarted.CheckAsync(config, _ =>
        { ++calls; return Task.FromResult(ScheduledRunOutcome.Applied); });
        Require(calls == 0 && result.SettledSlot == due.Key &&
                result.LastStatus.Contains("unknown"), "Unknown crashed request got re-billed");
    }
    finally { Cleanup(path); }
}
static async Task Failure()
{
    var path = TempPath(); var now = Utc(2026,10,16,12); var calls = 0;
    try
    {
        var engine = new DailyScheduleCoordinator(new DailyScheduleJournalStore(path), () => now);
        await engine.CheckAsync(Config("09:00","UTC"), _ =>
        { ++calls; return Task.FromResult(ScheduledRunOutcome.Failed); });
        await engine.CheckAsync(Config("09:00","UTC"), _ =>
        { ++calls; return Task.FromResult(ScheduledRunOutcome.Applied); });
        Require(calls == 1 && engine.Snapshot().LastStatus.Contains("previous wallpaper"),
            "Failed attempt repeated a costly slot");
    }
    finally { Cleanup(path); }
}
static async Task BusyBackoff()
{
    var path = TempPath(); var now = Utc(2026,10,16,12); var calls=0;
    try
    {
        var engine = new DailyScheduleCoordinator(new DailyScheduleJournalStore(path), () => now);
        async Task<ScheduledRunOutcome> Busy(CancellationToken _) { ++calls; await Task.Yield(); return ScheduledRunOutcome.Busy; }
        await engine.CheckAsync(Config("09:00","UTC"), Busy);
        await engine.CheckAsync(Config("09:00","UTC"), Busy);
        Require(calls == 1, "Unbounded repeated busy dispatch");
        now = now.AddMinutes(1);
        await engine.CheckAsync(Config("09:00","UTC"), Busy);
        Require(calls == 2, "First bounded retry missing");
        now = now.AddMinutes(5);
        await engine.CheckAsync(Config("09:00","UTC"), Busy);
        Require(calls == 3 && engine.Snapshot().LastStatus.Contains("Skipped"),
            "Busy retries not bounded at 3");
        now = now.AddHours(2);
        await engine.CheckAsync(Config("09:00","UTC"), Busy);
        Require(calls == 3, "Exhausted slot was dispatched again");
    }
    finally { Cleanup(path); }
}
static async Task Concurrent()
{
    var path=TempPath(); var now=Utc(2026,10,16,12); var count=0;
    var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    try
    {
        var c = Config("09:00","UTC");
        var first = new DailyScheduleCoordinator(new DailyScheduleJournalStore(path),()=>now);
        var other = new DailyScheduleCoordinator(new DailyScheduleJournalStore(path),()=>now);
        var task = first.CheckAsync(c,async _=> {
            ++count; entered.TrySetResult(true); await release.Task; return ScheduledRunOutcome.Applied;
        });
        await entered.Task;
        await other.CheckAsync(c,_=>{++count; return Task.FromResult(ScheduledRunOutcome.Applied);});
        Require(count==1,"Two independent instances dispatched image concurrently");
        release.TrySetResult(true);
        await task;
        await other.CheckAsync(c,_=>{++count; return Task.FromResult(ScheduledRunOutcome.Applied);});
        Require(count==1,"Second instance re-dispatched completed slot");
    }
    finally { release.TrySetResult(true); Cleanup(path); }
}
static async Task Validation()
{
    try { DailyWallClock.ParseTime("25:00"); throw new Exception("Accepted invalid time"); }
    catch (ArgumentException) { }
    var path=TempPath(); 
    try
    {
        File.WriteAllText(path,"{broken json");
        try { new DailyScheduleJournalStore(path).Load(); throw new Exception("Overwrote bad journal"); }
        catch (InvalidDataException) { }
        Require(File.ReadAllText(path)=="{broken json","Corrupted state was overwritten");
    }
    finally { Cleanup(path); }
    await Task.CompletedTask;
}
static void Cleanup(string path)
{
    if (File.Exists(path)) File.Delete(path);
    if (File.Exists(path+".lock")) File.Delete(path+".lock");
    var directory=Path.GetDirectoryName(path)!;
    foreach (var file in Directory.GetFiles(directory, Path.GetFileName(path)+".*.tmp"))
        File.Delete(file);
}
