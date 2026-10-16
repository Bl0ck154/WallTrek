using System.Text.Json;

namespace WallTrek.Services.Scheduling;

public enum ScheduledRunOutcome { Applied, Failed, Busy }
public sealed record DueSlot(string Key, DateOnly LocalDate, DateTimeOffset DueUtc, string TimeZoneId);
public sealed record DailyScheduleConfig(string LocalTime, string TimeZoneId, DateTimeOffset EnabledAtUtc)
{
    public string Signature => LocalTime + "|" + TimeZoneId + "|" + EnabledAtUtc.ToUniversalTime().ToString("O");
}

public sealed class DailyScheduleJournal
{
    public int SchemaVersion { get; set; } = 1;
    public string? ConfigSignature { get; set; }
    public string? SettledSlot { get; set; }
    public string? PendingSlot { get; set; }
    public DateTimeOffset? PendingStartedUtc { get; set; }
    public string? DeferredSlot { get; set; }
    public int DeferredCount { get; set; }
    public DateTimeOffset? DeferredUntilUtc { get; set; }
    public string LastStatus { get; set; } = "Not run";
    public DateTimeOffset? LastFinishedUtc { get; set; }
}

/// <summary>
/// JSON state written atomically BEFORE any potentially billed image request.
/// Last pending slot remains a tombstone if Windows crashes, to avoid re-billing
/// an operation whose outcome is unknown. All paths are local/current-user only.
/// </summary>
public sealed class DailyScheduleJournalStore
{
    private readonly string _path;
    public DailyScheduleJournalStore(string? path = null)
    {
        _path = path ?? Path.Combine(Environment.GetFolderPath(
            Environment.SpecialFolder.ApplicationData), "WallTrek", "daily-schedule.json");
    }
    public string LockPath => _path + ".lock";

    public DailyScheduleJournal Load()
    {
        if (!File.Exists(_path)) return new();
        try
        {
            var journal = JsonSerializer.Deserialize<DailyScheduleJournal>(File.ReadAllText(_path))
                ?? throw new InvalidDataException("Empty daily schedule state.");
            if (journal.SchemaVersion != 1)
                throw new InvalidDataException("Unknown daily schedule journal schema.");
            return journal;
        }
        catch (JsonException ex)
        { throw new InvalidDataException("Daily schedule journal is corrupted; original preserved.", ex); }
    }

    public void Save(DailyScheduleJournal journal)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temp = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(journal, new JsonSerializerOptions
            { WriteIndented = true }));
            File.Move(temp, _path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    public FileStream? TryLock()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        try { return new FileStream(LockPath, FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { return null; } // Other WallTrek process executing the slot.
    }
}

public static class DailyWallClock
{
    public static TimeZoneInfo ResolveZone(string id)
    {
        if (id == "Local") return TimeZoneInfo.Local;
        if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("Choose a valid time zone.");
        return TimeZoneInfo.FindSystemTimeZoneById(id);
    }

    public static TimeSpan ParseTime(string localTime)
    {
        if (localTime?.Length != 5 || localTime[2] != ':' ||
            !int.TryParse(localTime[..2], out var hh) ||
            !int.TryParse(localTime[3..], out var mm) ||
            hh is < 0 or > 23 || mm is < 0 or > 59)
            throw new ArgumentException("Daily time must use HH:mm, 00:00–23:59.");
        return new TimeSpan(hh, mm, 0);
    }

    public static DateTimeOffset SlotUtc(DateOnly date, DailyScheduleConfig config)
    {
        var zone = ResolveZone(config.TimeZoneId);
        var wall = date.ToDateTime(TimeOnly.MinValue).Add(ParseTime(config.LocalTime));
        // Spring DST gap: use the first valid minute after the nonexistent local time.
        // Fall DST overlap: pick the EARLIER real occurrence, only once for the date.
        for (var n = 0; n < 180 && zone.IsInvalidTime(wall); n++)
            wall = wall.AddMinutes(1);
        if (zone.IsInvalidTime(wall))
            throw new InvalidOperationException("Could not resolve DST gap.");
        var offset = zone.IsAmbiguousTime(wall)
            ? zone.GetAmbiguousTimeOffsets(wall).Max()
            : zone.GetUtcOffset(wall);
        return new DateTimeOffset(wall, offset).ToUniversalTime();
    }

    public static DueSlot? LatestDue(DailyScheduleConfig config, DateTimeOffset nowUtc)
    {
        var local = TimeZoneInfo.ConvertTime(nowUtc, ResolveZone(config.TimeZoneId));
        var date = DateOnly.FromDateTime(local.DateTime);
        var due = SlotUtc(date, config);
        if (due > nowUtc)
        {
            date = date.AddDays(-1);
            due = SlotUtc(date, config);
        }
        if (due < config.EnabledAtUtc.ToUniversalTime()) return null;
        return new(DueKey(config, date), date, due, ResolveZone(config.TimeZoneId).Id);
    }

    public static DateTimeOffset NextUtc(DailyScheduleConfig config, DateTimeOffset nowUtc)
    {
        var local = TimeZoneInfo.ConvertTime(nowUtc, ResolveZone(config.TimeZoneId));
        var date = DateOnly.FromDateTime(local.DateTime);
        var today = SlotUtc(date, config);
        return today > nowUtc ? today : SlotUtc(date.AddDays(1), config);
    }

    public static string DueKey(DailyScheduleConfig config, DateOnly date) =>
        config.Signature + "|" + ResolveZone(config.TimeZoneId).Id + "|" +
        date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>
/// Single-flight daily schedule coordinator. Never retries an UNKNOWN outcome
/// after crash. Busy/preflight results get bounded backoff; an AI failure consumes
/// the slot to prevent inadvertent double charges/duplicate wallpaper changes.
/// </summary>
public sealed class DailyScheduleCoordinator
{
    private readonly DailyScheduleJournalStore _store;
    private readonly Func<DateTimeOffset> _clock;
    private readonly SemaphoreSlim _flight = new(1, 1);
    public DailyScheduleCoordinator(DailyScheduleJournalStore store,
        Func<DateTimeOffset>? clock = null)
    { _store = store; _clock = clock ?? (() => DateTimeOffset.UtcNow); }

    public async Task<DailyScheduleJournal> CheckAsync(DailyScheduleConfig config,
        Func<CancellationToken, Task<ScheduledRunOutcome>> execute,
        CancellationToken ct = default)
    {
        _ = DailyWallClock.NextUtc(config, _clock()); // validate before touching the journal
        if (!await _flight.WaitAsync(0, ct)) return _store.Load();
        try
        {
            // File lock is held for entire async generation; cross-process instances
            // cannot both claim the same daily slot.
            using var exclusive = _store.TryLock();
            if (exclusive is null) return _store.Load();
            var state = _store.Load();
            if (state.ConfigSignature != config.Signature)
            {
                // New/edited schedule starts a new generation series. Existing journal
                // remains persisted until the new one has been safely written.
                state = new DailyScheduleJournal { ConfigSignature = config.Signature };
                _store.Save(state);
            }
            var now = _clock();
            var due = DailyWallClock.LatestDue(config, now);
            if (due is null) return state;

            if (state.PendingSlot is not null)
            {
                // Startup after crash/forced termination: outcome unknown. Do NOT
                // automatically repeat a potentially billed or already-applied image.
                state.SettledSlot = state.PendingSlot;
                state.PendingSlot = null;
                state.PendingStartedUtc = null;
                state.LastStatus = "Interrupted: result unknown, skipped to avoid duplicates";
                state.LastFinishedUtc = now;
                _store.Save(state);
            }
            if (state.SettledSlot == due.Key) return state;
            if (state.DeferredSlot == due.Key && state.DeferredUntilUtc > now) return state;

            state.PendingSlot = due.Key;
            state.PendingStartedUtc = now;
            _store.Save(state); // DURABLE CLAIM, before any network/UI generation

            ScheduledRunOutcome outcome;
            try { outcome = await execute(ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Cancellation could happen AFTER provider billed. Settle conservatively.
                outcome = ScheduledRunOutcome.Failed;
            }
            catch (Exception)
            {
                // No sensitive exception/log strings persisted. Unknown failures never
                // trigger another costly image generation automatically.
                outcome = ScheduledRunOutcome.Failed;
            }

            state.PendingSlot = null;
            state.PendingStartedUtc = null;
            state.LastFinishedUtc = _clock();
            if (outcome == ScheduledRunOutcome.Busy)
            {
                state.DeferredCount = state.DeferredSlot == due.Key
                    ? state.DeferredCount + 1 : 1;
                state.DeferredSlot = due.Key;
                if (state.DeferredCount >= 3)
                {
                    state.SettledSlot = due.Key;
                    state.LastStatus = "Skipped after 3 busy/deferred attempts";
                    state.DeferredUntilUtc = null;
                }
                else
                {
                    state.DeferredUntilUtc = state.LastFinishedUtc.Value.AddMinutes(
                        state.DeferredCount == 1 ? 1 : 5);
                    state.LastStatus = "Busy; safe deferred retry";
                }
            }
            else
            {
                state.SettledSlot = due.Key;
                state.DeferredSlot = null;
                state.DeferredCount = 0;
                state.DeferredUntilUtc = null;
                state.LastStatus = outcome == ScheduledRunOutcome.Applied
                    ? "Wallpaper applied successfully"
                    : "Generation failed; previous wallpaper retained";
            }
            _store.Save(state);
            return state;
        }
        finally { _flight.Release(); }
    }

    public DailyScheduleJournal Snapshot() => _store.Load();
}
