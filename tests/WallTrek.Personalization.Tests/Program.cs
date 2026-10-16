using System.Drawing;
using System.Drawing.Imaging;
using WallTrek.Services.Personalization;
using WallTrek.Services.Providers;

var tests = new (string, Func<Task>)[]
{
    ("Seven variants without repetition and stable preview", NoRepeat),
    ("Scenario validates unknown placeholders", ScenarioValidation),
    ("Local references, role updates and 5-photo limit", PhotoLibrary),
    ("Destination approval is scoped to exact origin and references", Consent),
    ("Edits use typed IImageProvider input with identity", EditContract),
    ("Unsupported edit never invokes text-only generator", EditUnsupported),
    ("Provenance persists in SQLite without image bytes", History)
};
var failures = 0;
foreach (var (name, test) in tests)
{
    try { await test(); Console.WriteLine("PASS: " + name); }
    catch (Exception ex)
    {
        failures++;
        Console.Error.WriteLine("FAIL: " + name + ": " + ex.Message);
    }
}
Environment.ExitCode = failures == 0 ? 0 : 1;

static WallpaperScenario MakeScenario()
{
    var s = new WallpaperScenario
    {
        Name = "Explore cities",
        Template = "Portrait in {{place}}, {{weather}}",
        Style = "cinematic wallpaper",
        Avoid = "watermarks",
        Pools = new Dictionary<string, List<string>>(StringComparer.Ordinal)
        {
            ["place"] = new() { "Prague", "Vienna", "Lisbon", "Oslo", "Rome", "Tokyo", "Paris" },
            ["weather"] = new() { "sunny" }
        }
    };
    return s;
}
static void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}
static Task NoRepeat()
{
    var scene = MakeScenario();
    var seen = new HashSet<string>();
    for (int i = 0; i < 7; i++)
    {
        var preview = ScenarioComposer.Compose(scene, Array.Empty<ReferencePhoto>());
        Check(ScenarioComposer.Compose(scene, Array.Empty<ReferencePhoto>()).FinalPrompt == preview.FinalPrompt,
            "preview changed without a successful run");
        Check(seen.Add(preview.CombinationKey), "combination repeated before all seven were consumed");
        Check(preview.FinalPrompt.Contains("cinematic wallpaper"), "style not preserved");
        ScenarioComposer.CommitSuccess(scene, preview);
    }
    var previous = scene.LastCombinationKey;
    var next = ScenarioComposer.Compose(scene, Array.Empty<ReferencePhoto>());
    Check(next.CombinationKey != previous, "cycle repeated immediately");
    ScenarioComposer.CommitSuccess(scene, next);
    Check(scene.Cycle == 1, "cycle not restarted");
    return Task.CompletedTask;
}
static Task ScenarioValidation()
{
    var scenario = MakeScenario();
    scenario.Template = "Portrait in {{unknown}}";
    try { ScenarioComposer.Compose(scenario, Array.Empty<ReferencePhoto>()); throw new Exception("accepted missing variable"); }
    catch (InvalidOperationException ex) when (ex.Message.Contains("pool")) { }
    scenario = MakeScenario();
    scenario.Pools["new"] = new() { "unused" };
    try { ScenarioComposer.Compose(scenario, Array.Empty<ReferencePhoto>()); throw new Exception("accepted unused pool"); }
    catch (InvalidOperationException ex) when (ex.Message.Contains("pool")) { }
    return Task.CompletedTask;
}
static string Temporary() { var dir = Path.Combine(Path.GetTempPath(), "walltrek-pers-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir); return dir; }
static string MakeImage(string dir, string name)
{
    var path = Path.Combine(dir, name);
    using var bmp = new Bitmap(64, 64);
    using (var graphics = Graphics.FromImage(bmp)) graphics.Clear(Color.CadetBlue);
    bmp.Save(path, ImageFormat.Png);
    return path;
}
static Task PhotoLibrary()
{
    var dir = Temporary();
    try
    {
        var image = MakeImage(dir, "sample.png");
        var library = new PersonalizationLibrary(Path.Combine(dir, "library"));
        var photos = Enumerable.Range(0, 6).Select(i => library.Import(image,
            i == 1 ? ReferenceRole.Style : ReferenceRole.Identity)).ToArray();
        Check(library.Snapshot().Photos.Count == 6, "library cannot retain more than 5 stored images");
        Check(library.SelectedPhotos().Count == 5 && !photos[5].Enabled, "active limit not enforced");
        library.UpdatePhoto(photos[0].Id, "Primary photo", ReferenceRole.Identity, true, true);
        Check(library.Snapshot().Photos.Single(p => p.Id == photos[0].Id).Primary, "primary flag not saved");
        var refs = library.ReadForProvider(library.SelectedPhotos().Select(p => p.Id).ToArray());
        Check(refs.Count == 5 && refs[1].Role == ProviderReferenceRole.Style, "roles or photos dropped");
        Check(refs.All(r => r.FileName.EndsWith(".jpg") && r.Bytes.Length > 0), "not sanitized JPEG");
        var original = MakeImage(dir, "does-not-erase.png");
        var removed = photos[0].Id;
        library.DeletePhoto(removed);
        Check(!library.Snapshot().Photos.Any(p => p.Id == removed), "deleted photo remains in library");
        Check(File.Exists(image) && File.Exists(original), "user originals were touched");
        var restarted = new PersonalizationLibrary(Path.Combine(dir, "library"));
        Check(restarted.Snapshot().Photos.Count == 5, "reference metadata not persistent");
    }
    finally { Directory.Delete(dir, true); }
    return Task.CompletedTask;
}
static Task Consent()
{
    var dir = Temporary();
    try
    {
        var image = MakeImage(dir, "reference.png");
        var library = new PersonalizationLibrary(Path.Combine(dir, "library"));
        var photo = library.Import(image, ReferenceRole.Identity);
        var scenario = MakeScenario();
        library.SaveScenario(scenario, true);
        var prepared = library.Prepare()!;
        Check(prepared.FinalPrompt.Contains("Preserve the recognizable identity"), "identity protection missing");
        var profile = Guid.NewGuid();
        var first = "https://host-one.example";
        var second = "https://host-two.example";
        Check(!library.HasReferenceApproval(prepared, profile, first), "implicit photo upload permission");
        library.ApproveReferences(prepared.ScenarioId, profile, first, prepared.ReferenceIds);
        Check(library.HasReferenceApproval(prepared, profile, first), "approved destination rejected");
        Check(!library.HasReferenceApproval(prepared, profile, second), "approval leaked across hosts");
        Check(!library.HasReferenceApproval(prepared, Guid.NewGuid(), first), "approval leaked across API profiles");
        library.UpdatePhoto(photo.Id, "Modified role", ReferenceRole.Style, true, false);
        Check(!library.HasReferenceApproval(prepared, profile, first), "stale consent survived photo edit");
    }
    finally { Directory.Delete(dir, true); }
    return Task.CompletedTask;
}
static async Task EditContract()
{
    var photo = new ReferencePhoto { Role = ReferenceRole.Identity };
    var prepared = ScenarioComposer.Compose(MakeScenario(), new[] { photo });
    var generator = new FakeProvider();
    var edits = 0;
    var coordinator = new PersonalizedGenerationCoordinator(generator,
        (request, ct) =>
        {
            edits++;
            Check(request.References?.Count == 1, "reference silently omitted");
            Check(request.References![0].Role == ProviderReferenceRole.Identity, "identity role omitted");
            Check(request.Prompt.Contains("Preserve the recognizable identity"), "identity guard omitted");
            return Task.FromResult(new ProviderImageResult(Fixtures.SamplePng(), "image/png", 64, 64, "fake", "edit"));
        });
    var result = await coordinator.GenerateAsync(prepared,
        new[] { new ProviderImageReference(Fixtures.SamplePng(), "photo.png", ProviderReferenceRole.Identity) });
    Check(result.ImageData.Length > 0 && edits == 1 && generator.GenerateCount == 0, "edit not routed separately");
}
static async Task EditUnsupported()
{
    var photo = new ReferencePhoto { Role = ReferenceRole.Identity };
    var prepared = ScenarioComposer.Compose(MakeScenario(), new[] { photo });
    var fake = new FakeProvider();
    try
    {
        await new PersonalizedGenerationCoordinator(fake).GenerateAsync(prepared,
            new[] { new ProviderImageReference(Fixtures.SamplePng(), "photo.png", ProviderReferenceRole.Identity) });
        throw new Exception("unsupported edit accepted");
    }
    catch (ProviderException ex) when (ex.Reason == ProviderFailure.UnsupportedOperation) { }
    Check(fake.GenerateCount == 0, "sent personal data to text-only API");
}
static async Task History()
{
    var dir = Temporary();
    try
    {
        var history = new PersonalizationHistory(Path.Combine(dir, "history.db"));
        var prepared = ScenarioComposer.Compose(MakeScenario(), Array.Empty<ReferencePhoto>());
        var imagePath = Path.Combine(dir, "wallpaper.jpg");
        await history.RecordAsync(imagePath, prepared, "Travel", "provider123", "image-model");
        var rows = await new PersonalizationHistory(Path.Combine(dir, "history.db")).RecentAsync();
        Check(rows.Count == 1 && rows[0].Prompt == prepared.FinalPrompt, "resolved prompt history missing");
        Check(rows[0].VariablesJson.Contains("place"), "variable metadata lost");
        Check(rows[0].ProviderId == "provider123", "provider provenance missing");
    }
    finally { Directory.Delete(dir, true); }
}
sealed class FakeProvider : IImageProvider
{
    public string ProviderId => "fake";
    public int GenerateCount { get; private set; }
    public Task<ProviderCapabilities> ProbeAsync(CancellationToken ct = default) =>
        Task.FromResult(new ProviderCapabilities(true, false, false, "test", DateTimeOffset.UtcNow));
    public Task<ProviderImageResult> GenerateAsync(ProviderImageRequest request, CancellationToken ct = default)
    {
        GenerateCount++;
        return Task.FromResult(new ProviderImageResult(Fixtures.SamplePng(), "image/png", 64, 64, "fake", "gen"));
    }
}

static class Fixtures
{
public static byte[] SamplePng()
{
    using var bitmap = new Bitmap(64, 64);
    using var buffer = new MemoryStream();
    bitmap.Save(buffer, ImageFormat.Png);
    return buffer.ToArray();
}
}
