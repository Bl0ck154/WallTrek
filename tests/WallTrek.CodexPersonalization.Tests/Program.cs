using System.Drawing;
using System.Drawing.Imaging;
using WallTrek.Services.ImageGen;
using WallTrek.Services.ImageGen.Codex;
using WallTrek.Services.Personalization;
using WallTrek.Services.Providers;

if (args.Contains("--live", StringComparer.OrdinalIgnoreCase))
{
    await LiveSmokeTest();
    return;
}

var tests = new (string Name, Func<Task> Run)[]
{
    ("Setup probe never claims image entitlement", Probe),
    ("One-to-five sanitized reference handoffs preserve roles", References),
    ("Unsupported style-only and multiple styles fail before dispatch", Styles),
    ("Explicit scenario/host consent and disabled flag stop uploads", ConsentAndRouter),
    ("Rate limit/error and cancellation clean transient images", ErrorsAndCleanup),
    ("Scenario stays unchanged on Codex failure", NoRepeatAfterFailure)
};
int failures = 0;
foreach (var (name, run) in tests)
{
    try { await run(); Console.WriteLine("PASS " + name); }
    catch (Exception ex)
    {
        failures++;
        Console.Error.WriteLine("FAIL " + name + ": " + ex.Message);
    }
}
Environment.ExitCode = failures == 0 ? 0 : 1;

static void Assert(bool condition, string error)
{
    if (!condition) throw new Exception(error);
}
static string NewFolder()
{
    var folder = Path.Combine(Path.GetTempPath(), "walltrek-codex-personal-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(folder);
    return folder;
}
static byte[] Picture(ImageFormat format)
{
    using var bitmap = new Bitmap(96, 64);
    using (var graphics = Graphics.FromImage(bitmap)) graphics.Clear(Color.SteelBlue);
    using var stream = new MemoryStream();
    bitmap.Save(stream, format);
    return stream.ToArray();
}
static ProviderImageReference Jpeg(ProviderReferenceRole role) =>
    new(Picture(ImageFormat.Jpeg), "PERSONAL-PHOTO-DO-NOT-LOG.jpeg", role);
static WallpaperScenario Scenario() => new()
{
    Template = "A portrait by the {{city}} canal",
    Pools = new Dictionary<string, List<string>> { ["city"] = new() { "Venice", "Amsterdam", "Bruges" } },
    Style = "cinematic photography",
    Avoid = "distorted facial features"
};
static CodexProbe Ready() => new(true, true, true, "0.1.0", "0.2.0", null);

static async Task Probe()
{
    var fake = new FakeClient();
    var provider = new CodexPersonalizedImageProvider(fake, true);
    var probe = await provider.ProbeAsync();
    Assert(probe.ConnectionOk && !probe.GenerateVerified && !probe.EditVerified, "Probe claimed actual image support");
    Assert(fake.RunCalls == 0, "Probe launched image generation");
    var disabled = new CodexPersonalizedImageProvider(fake, false);
    var denied = await disabled.ProbeAsync();
    Assert(!denied.ConnectionOk && fake.ProbeCalls == 1, "Disabled mode contacted helper");
    fake.Session = new CodexProbe(true, false, true, "0.1.0", "0.2.0", CodexImageFailure.NotSignedIn);
    Assert(!(await provider.ProbeAsync()).ConnectionOk, "Missing login accepted");
}

static async Task References()
{
    var folder = NewFolder();
    try
    {
        for (int n = 1; n <= 5; n++)
        {
            var fake = new FakeClient();
            var provider = new CodexPersonalizedImageProvider(fake, true, Path.Combine(folder, "h" + n));
            var refs = Enumerable.Range(0, n).Select(i => Jpeg(i == n - 1 && n > 1
                ? ProviderReferenceRole.Style : i == 0
                    ? ProviderReferenceRole.Identity : ProviderReferenceRole.Content)).ToArray();
            var result = await provider.EditAsync(new ProviderImageRequest(
                "A sunset scene with the same recognizable person", refs));
            Assert(result.MimeType == "image/png" && result.Width == 96 && result.Height == 64,
                "Actual output was not validated");
            Assert(fake.RunCalls == 1 && fake.LastPaths.Count == n, "Reference count was lost");
            Assert(fake.LastRoles.Count(r => r == CodexReferenceRole.Style) == (n > 1 ? 1 : 0),
                "Style role lost");
            Assert(fake.LastRoles[0] == CodexReferenceRole.Identity, "Identity role lost");
            Assert(fake.LastPaths.All(p => !File.Exists(p)), "Sensitive temporary JPEG remained");
            Assert(fake.LastPaths.All(p => !p.Contains("PERSONAL-PHOTO", StringComparison.Ordinal)),
                "Original private filename leaked to helper");
        }
    }
    finally { Directory.Delete(folder, true); }
}
static async Task Styles()
{
    var fake = new FakeClient();
    var provider = new CodexPersonalizedImageProvider(fake, true);
    foreach (var refs in new[]
    {
        new[] { Jpeg(ProviderReferenceRole.Style) },
        new[] { Jpeg(ProviderReferenceRole.Identity), Jpeg(ProviderReferenceRole.Style),
            Jpeg(ProviderReferenceRole.Style) }
    })
    {
        try
        {
            await provider.EditAsync(new ProviderImageRequest("style test", refs));
            throw new Exception("Unsafe style combination was accepted");
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("at most one style")) { }
    }
    Assert(fake.RunCalls == 0, "Style validation performed billed image call");
}
static async Task ConsentAndRouter()
{
    var folder = NewFolder();
    try
    {
        var pngPath = Path.Combine(folder, "portrait.png");
        File.WriteAllBytes(pngPath, Picture(ImageFormat.Png));
        var lib = new PersonalizationLibrary(Path.Combine(folder, "library"));
        lib.Import(pngPath, ReferenceRole.Identity);
        lib.SaveScenario(Scenario(), activate: true);
        var prepared = lib.Prepare()!;
        var option = new ImageModelOption { Id = "codex-experimental", Provider = ImageProvider.CodexExperimental, ModelId = "codex" };
        var fake = new FakeClient();
        var provider = new CodexPersonalizedImageProvider(fake, true, Path.Combine(folder, "handoff"));
        WallTrek.Services.Settings.Instance.CodexExperimentalEnabled = true;
        try
        {
            await PersonalizedWallpaperRunner.RunAsync(option, prepared, lib, default, provider);
            throw new Exception("References uploaded without user consent");
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("not approved")) { }
        Assert(fake.RunCalls == 0 && fake.ProbeCalls == 0, "Probe/dispatched before consent");
        lib.ApproveReferences(prepared.ScenarioId, CodexPersonalConsent.ProviderId,
            CodexPersonalConsent.Origin, prepared.ReferenceIds);
        Assert(!lib.HasReferenceApproval(prepared, CodexPersonalConsent.ProviderId,
            "https://another.example"), "Consent leaked to another origin");
        WallTrek.Services.Settings.Instance.CodexExperimentalEnabled = false;
        try
        {
            await PersonalizedWallpaperRunner.RunAsync(option, prepared, lib, default, provider);
            throw new Exception("Opt-out bypassed");
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("Enable experimental Codex")) { }
        WallTrek.Services.Settings.Instance.CodexExperimentalEnabled = true;
        var result = await PersonalizedWallpaperRunner.RunAsync(option, prepared, lib, default, provider);
        Assert(result.Image.ImageData.Length > 200 && result.ProviderId == "codex:experimental",
            "Personalization runner did not use existing Codex adapter");
        Assert(fake.RunCalls == 1, "Wrong number of Codex edit calls");
        Assert(lib.Prepare()!.CombinationKey == prepared.CombinationKey,
            "Scenario advanced before existing wallpaper persistence");

        var photo = lib.Snapshot().Photos.Single();
        lib.UpdatePhoto(photo.Id, "Changed role", ReferenceRole.Content, enabled: true, primary: false);
        Assert(!lib.HasReferenceApproval(prepared, CodexPersonalConsent.ProviderId,
            CodexPersonalConsent.Origin), "Old photo approval survived role change");
    }
    finally { Directory.Delete(folder, true); }
}
static async Task ErrorsAndCleanup()
{
    var folder = NewFolder();
    try
    {
        var fake = new FakeClient { Failure = CodexImageFailure.RateLimited };
        var provider = new CodexPersonalizedImageProvider(fake, true, folder);
        try
        {
            await provider.EditAsync(new ProviderImageRequest("test", new[] { Jpeg(ProviderReferenceRole.Identity) }));
            throw new Exception("Rate limit should propagate");
        }
        catch (CodexImageException ex) when (ex.Reason == CodexImageFailure.RateLimited) { }
        Assert(fake.RunCalls == 1 && fake.LastPaths.All(p => !File.Exists(p)),
            "Failed calls leaked reference JPEGs");
        fake.Failure = CodexImageFailure.Cancelled;
        try
        {
            await provider.EditAsync(new ProviderImageRequest("test", new[] { Jpeg(ProviderReferenceRole.Identity) }));
            throw new Exception("Cancellation should propagate");
        }
        catch (CodexImageException ex) when (ex.Reason == CodexImageFailure.Cancelled) { }
        Assert(fake.LastPaths.All(p => !File.Exists(p)), "Cancelled call left private images");
    }
    finally { Directory.Delete(folder, true); }
}
static async Task NoRepeatAfterFailure()
{
    var scene = Scenario();
    var before = ScenarioComposer.Compose(scene, new[] { new ReferencePhoto { Role = ReferenceRole.Identity } });
    var fake = new FakeClient { Failure = CodexImageFailure.BackendChanged };
    var provider = new CodexPersonalizedImageProvider(fake, true);
    try
    {
        await provider.EditAsync(new ProviderImageRequest(before.FinalPrompt, new[] { Jpeg(ProviderReferenceRole.Identity) }));
        throw new Exception("Expected backend error");
    }
    catch (CodexImageException ex) when (ex.Reason == CodexImageFailure.BackendChanged) { }
    var after = ScenarioComposer.Compose(scene, new[] { new ReferencePhoto { Role = ReferenceRole.Identity } });
    Assert(scene.UsedCombinationKeys.Count == 0 && before.CombinationKey == after.CombinationKey,
        "Failed edit consumed scenario variation");
}

static async Task LiveSmokeTest()
{
    // Explicit local opt-in only: never run on unattended CI or infer consent from credentials.
    if (Environment.GetEnvironmentVariable("WALLTREK_CODEX_LIVE_CONSENT") != "I_APPROVE_CHATGPT_UPLOAD")
        throw new InvalidOperationException("Set WALLTREK_CODEX_LIVE_CONSENT=I_APPROVE_CHATGPT_UPLOAD before --live.");
    var source = Environment.GetEnvironmentVariable("WALLTREK_CODEX_LIVE_REFERENCE_PATH");
    if (string.IsNullOrWhiteSpace(source) || !File.Exists(source))
        throw new InvalidOperationException("Select a disposable local photo via WALLTREK_CODEX_LIVE_REFERENCE_PATH.");
    var dir = NewFolder();
    try
    {
        var library = new PersonalizationLibrary(Path.Combine(dir, "photos"));
        var photo = library.Import(source, ReferenceRole.Identity);
        var client = new CodexBridgeImageClient(new CodexImageBridge(true));
        var provider = new CodexPersonalizedImageProvider(client, true, Path.Combine(dir, "handoff"));
        var status = await provider.ProbeAsync();
        if (!status.ConnectionOk) throw new InvalidOperationException(status.Status);
        var image = await provider.EditAsync(new ProviderImageRequest(
            "Make a photorealistic 16:9 wallpaper of the same recognizable person in a sunny European city; preserve face shape, eyes and proportions",
            library.ReadForProvider(new[] { photo.Id })));
        Assert(image.Width > 0 && image.Height > 0, "No real output pixels");
        Console.WriteLine($"REAL CODEX IMAGE EDIT verified locally: {image.Width}x{image.Height} pixels.");
        Console.WriteLine("Inspect actual photo fidelity manually. No image or credential has been uploaded to GitHub.");
    }
    finally { Directory.Delete(dir, recursive: true); }
}

sealed class FakeClient : ICodexPersonalImageClient
{
    public CodexProbe Session { get; set; } = new(true, true, true, "0.1.0", "0.2.0", null);
    public CodexImageFailure? Failure { get; set; }
    public int ProbeCalls { get; private set; }
    public int RunCalls { get; private set; }
    public List<string> LastPaths { get; private set; } = new();
    public List<CodexReferenceRole> LastRoles { get; private set; } = new();
    public Task<CodexProbe> ProbeAsync(CancellationToken ct)
    {
        ProbeCalls++;
        return Task.FromResult(Session);
    }
    public Task<CodexImageOutput> GenerateAsync(CodexImageRequest request, CancellationToken ct)
    {
        RunCalls++;
        LastPaths = request.References?.Select(r => r.Path).ToList() ?? new();
        LastRoles = request.References?.Select(r => r.Role).ToList() ?? new();
        Fixtures.Assert(LastPaths.All(File.Exists), "Helper did not see an accessible reference file");
        Fixtures.Assert(LastPaths.All(path => ProviderImageValidator.Check(File.ReadAllBytes(path)).Mime == "image/jpeg"),
            "Temporary reference was not an EXIF-free JPEG");
        if (Failure.HasValue) throw new CodexImageException(Failure.Value);
        return Task.FromResult(new CodexImageOutput(Fixtures.Picture(ImageFormat.Png), 96, 64, Session.HelperVersion));
    }
}

static class Fixtures
{
    public static void Assert(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
    public static byte[] Picture(ImageFormat format)
    {
        using var bitmap = new Bitmap(96, 64);
        using (var graphics = Graphics.FromImage(bitmap)) graphics.Clear(Color.SteelBlue);
        using var stream = new MemoryStream();
        bitmap.Save(stream, format);
        return stream.ToArray();
    }
}
