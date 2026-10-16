using WallTrek.Services.ImageGen.Codex;

var root = Path.Combine(Path.GetTempPath(), "walltrek-bridge-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    var exe = Path.Combine(root, "codex.exe");
    var helper = Path.Combine(root, "codex-imagegen.exe");
    File.WriteAllText(exe, "fake");
    File.WriteAllText(helper, "fake");
    var runner = new FakeRunner();
    var bridge = new CodexImageBridge(true, exe, helper, runner, Path.Combine(root, "temp"));
    var probe = await bridge.ProbeAsync();
    Check(probe.CodexInstalled && probe.SignedIn && probe.HelperInstalled, "probe");
    Check(probe.ImageCapability.Contains("Unverified"), "capability cannot be inferred from login");

    var output = await bridge.GenerateAsync(new CodexImageRequest("landscape"));
    Check(output.Width == 1 && output.Height == 1, "generation PNG dimensions");
    Check(runner.ImageArgs![0] == "generate" && !runner.ImageArgs.Contains("landscape"), "prompt-file mode");
    Check(!Directory.EnumerateFiles(Path.Combine(root, "temp"), "*", SearchOption.AllDirectories).Any(), "cleanup");

    var reference = Path.Combine(root, "reference.png");
    var style = Path.Combine(root, "style.png");
    File.WriteAllBytes(reference, FakeRunner.ValidPng);
    File.WriteAllBytes(style, FakeRunner.ValidPng);
    await bridge.GenerateAsync(new CodexImageRequest("autumn",
        new[] { new CodexReference(reference, CodexReferenceRole.Identity),
                new CodexReference(style, CodexReferenceRole.Style) }));
    Check(runner.ImageArgs![0] == "edit", "edit command");
    Check(runner.ImageArgs.Contains("--image") && runner.ImageArgs.Contains(reference), "content image");
    Check(runner.ImageArgs.Contains("--style-image") && runner.ImageArgs.Contains(style), "style image");

    runner.BadImage = true;
    await ExpectFailure(bridge, CodexImageFailure.InvalidImage);
    runner.BadImage = false;
    runner.Error = CodexImageFailure.RateLimited;
    await ExpectFailure(bridge, CodexImageFailure.RateLimited);

    var disabled = new CodexImageBridge(false, exe, helper, runner);
    try { await disabled.GenerateAsync(new CodexImageRequest("lake")); throw new Exception("disabled backend ran"); }
    catch (CodexImageException e) when (e.Reason == CodexImageFailure.ToolUnavailable) { }
    Console.WriteLine("PASS: probe, opt-in, generation, edit roles, cleanup, invalid image and typed errors");
}
finally { Directory.Delete(root, true); }

static void Check(bool value, string label)
{
    if (!value) throw new Exception("FAIL: " + label);
}
static async Task ExpectFailure(CodexImageBridge bridge, CodexImageFailure failure)
{
    try { await bridge.GenerateAsync(new CodexImageRequest("lake")); throw new Exception("Error was not thrown"); }
    catch (CodexImageException e) when (e.Reason == failure) { }
}
sealed class FakeRunner : ICodexCommandRunner
{
    public static readonly byte[] ValidPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/xU4AAAAASUVORK5CYII=");
    public bool BadImage { get; set; }
    public CodexImageFailure? Error { get; set; }
    public string[]? ImageArgs { get; private set; }

    public Task<CodexCommandResult> RunAsync(string exe, IReadOnlyList<string> args,
        string workingDirectory, TimeSpan timeout, bool probeOutput, CancellationToken ct)
    {
        if (args.Contains("--version")) return Task.FromResult(new CodexCommandResult(0, "0.2.0", null));
        if (args.Contains("status")) return Task.FromResult(new CodexCommandResult(0, "Logged in using ChatGPT", null));
        ImageArgs = args.ToArray();
        if (Error.HasValue) return Task.FromResult(new CodexCommandResult(1, "", Error));
        var output = args[Array.IndexOf(ImageArgs, "--out") + 1];
        File.WriteAllBytes(output, BadImage ? new byte[] { 1, 2, 3 } : ValidPng);
        return Task.FromResult(new CodexCommandResult(0, "", null));
    }
}
