using System.Buffers.Binary;
using System.Text;

namespace WallTrek.Services.ImageGen.Codex;

public enum CodexImageFailure
{
    NotInstalled, NotSignedIn, UnsupportedAccount, ToolUnavailable, RateLimited,
    Timeout, InvalidImage, BackendChanged, Cancelled
}

public sealed class CodexImageException : Exception
{
    public CodexImageFailure Reason { get; }
    public CodexImageException(CodexImageFailure reason) : base(reason switch
    {
        CodexImageFailure.NotInstalled => "Install Codex CLI and the separately reviewed codex-imagegen helper.",
        CodexImageFailure.NotSignedIn => "Sign in locally with 'codex login' (ChatGPT account).",
        CodexImageFailure.UnsupportedAccount => "This Codex account or credential-store mode does not support the experimental image helper.",
        CodexImageFailure.ToolUnavailable => "The experimental helper is unavailable. Install it separately and enable it in Settings.",
        CodexImageFailure.RateLimited => "Codex image usage is limited. Try again later; do not switch accounts to bypass limits.",
        CodexImageFailure.Timeout => "The image operation timed out. It may have consumed account usage.",
        CodexImageFailure.InvalidImage => "The image helper did not return a valid PNG.",
        CodexImageFailure.BackendChanged => "The unofficial Codex image backend failed or changed. Check the helper version.",
        CodexImageFailure.Cancelled => "Image generation was cancelled.",
        _ => "Codex image operation failed."
    }) => Reason = reason;
}

public enum CodexReferenceRole { Identity, Content, Style }
public sealed record CodexReference(string Path, CodexReferenceRole Role);
public sealed record CodexImageRequest(
    string Prompt,
    IReadOnlyList<CodexReference>? References = null,
    string Size = "1536x1024",
    string Quality = "high");
public sealed record CodexImageOutput(byte[] Png, int Width, int Height, string? HelperVersion);
public sealed record CodexProbe(
    bool CodexInstalled,
    bool SignedIn,
    bool HelperInstalled,
    string? CodexVersion,
    string? HelperVersion,
    CodexImageFailure? Problem)
{
    // Install and sign-in DO NOT establish image-generation eligibility.
    public string ImageCapability => "Unverified (requires a real generate/edit test)";
}

public sealed record CodexCommandResult(int ExitCode, string ProbeText, CodexImageFailure? FailureHint);
public interface ICodexCommandRunner
{
    Task<CodexCommandResult> RunAsync(
        string executable, IReadOnlyList<string> args, string workingDirectory,
        TimeSpan timeout, bool probeOutput, CancellationToken cancellationToken);
}

/// <summary>
/// Experimental, explicitly opted-in bridge. WallTrek never opens Codex auth.json.
/// The third-party helper does access local Codex credentials and sends images to ChatGPT.
/// </summary>
public sealed class CodexImageBridge
{
    private readonly ICodexCommandRunner _runner;
    private readonly bool _enabled;
    private readonly string? _codexPath;
    private readonly string? _helperPath;
    private readonly string _tempRoot;
    private const long MaxReferenceBytes = 20 * 1024 * 1024;
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(12);
    private static readonly TimeSpan ImageTimeout = TimeSpan.FromMinutes(5);

    public CodexImageBridge(
        bool experimentalEnabled,
        string? codexExecutable = null,
        string? helperExecutable = null,
        ICodexCommandRunner? runner = null,
        string? tempRoot = null)
    {
        _enabled = experimentalEnabled;
        _codexPath = codexExecutable;
        _helperPath = helperExecutable;
        _runner = runner ?? new CodexProcessRunner();
        _tempRoot = tempRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WallTrek", "CodexTemp");
    }

    public async Task<CodexProbe> ProbeAsync(CancellationToken ct = default)
    {
        var codex = CodexExecutableResolver.Find("codex", _codexPath, allowCmd: true);
        var helper = CodexExecutableResolver.Find("codex-imagegen", _helperPath, allowCmd: false)
            ?? (_helperPath is null ? CodexExecutableResolver.Find("codex-imagegen-cli", null, false) : null);
        var installed = codex is not null;
        var helperInstalled = helper is not null;
        string? codexVersion = null, helperVersion = null;
        bool signedIn = false;
        CodexImageFailure? issue = null;
        var directory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (installed)
        {
            try
            {
                var version = await _runner.RunAsync(codex!, new[] { "--version" }, directory,
                    ProbeTimeout, true, ct);
                if (version.ExitCode == 0) codexVersion = SafeVersion(version.ProbeText);
                var login = await _runner.RunAsync(codex!, new[] { "login", "status" }, directory,
                    ProbeTimeout, true, ct);
                signedIn = login.ExitCode == 0 &&
                    login.ProbeText.Contains("logged in", StringComparison.OrdinalIgnoreCase) &&
                    !login.ProbeText.Contains("not logged in", StringComparison.OrdinalIgnoreCase);
                if (!signedIn) issue = CodexImageFailure.NotSignedIn;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (CodexImageException e) { issue = e.Reason; }
        }
        else issue = CodexImageFailure.NotInstalled;

        if (helperInstalled)
        {
            try
            {
                var result = await _runner.RunAsync(helper!, new[] { "--version" }, directory,
                    ProbeTimeout, true, ct);
                if (result.ExitCode == 0) helperVersion = SafeVersion(result.ProbeText);
                else { helperInstalled = false; issue ??= CodexImageFailure.ToolUnavailable; }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (CodexImageException)
            {
                helperInstalled = false;
                issue ??= CodexImageFailure.ToolUnavailable;
            }
        }
        else issue ??= CodexImageFailure.ToolUnavailable;

        return new(installed, signedIn, helperInstalled, codexVersion, helperVersion, issue);
    }

    private static string? SafeVersion(string text)
    {
        // Return only digits/dots from a CLI version, never untrusted command output.
        var match = System.Text.RegularExpressions.Regex.Match(text,
            @"(?<!\d)\d{1,4}\.\d{1,4}(?:\.\d{1,4})?");
        return match.Success ? match.Value : null;
    }

    public async Task<CodexImageOutput> GenerateAsync(CodexImageRequest request, CancellationToken ct = default)
    {
        if (!_enabled) throw new CodexImageException(CodexImageFailure.ToolUnavailable);
        if (string.IsNullOrWhiteSpace(request.Prompt) || request.Prompt.Length > 16_000)
            throw new ArgumentException("Prompt must contain 1–16000 characters.", nameof(request));
        if (request.Quality is not ("low" or "medium" or "high" or "auto"))
            throw new ArgumentException("Unsupported image quality.", nameof(request));
        if (request.Size is not ("auto" or "1024x1024" or "1536x1024" or "1024x1536"))
            throw new ArgumentException("Unsupported image size.", nameof(request));

        var refs = request.References ?? Array.Empty<CodexReference>();
        if (refs.Count > 5) throw new ArgumentException("The helper supports at most five reference images.", nameof(request));
        var style = refs.Where(r => r.Role == CodexReferenceRole.Style).ToArray();
        var content = refs.Where(r => r.Role != CodexReferenceRole.Style).ToArray();
        if (style.Length > 1 || (style.Length > 0 && content.Length == 0))
            throw new ArgumentException("Style requires at least one content image; at most one style image.", nameof(request));
        foreach (var reference in refs)
        {
            if (!Path.IsPathFullyQualified(reference.Path) || !File.Exists(reference.Path) ||
                new FileInfo(reference.Path).Length is <= 0 or > MaxReferenceBytes ||
                !new[] { ".png", ".jpeg", ".jpg", ".webp" }.Contains(Path.GetExtension(reference.Path).ToLowerInvariant()))
                throw new ArgumentException("References must be existing PNG, JPEG or WebP files under 20 MiB.", nameof(request));
        }

        var probe = await ProbeAsync(ct);
        if (!probe.CodexInstalled) throw new CodexImageException(CodexImageFailure.NotInstalled);
        if (!probe.SignedIn) throw new CodexImageException(probe.Problem ?? CodexImageFailure.NotSignedIn);
        if (!probe.HelperInstalled) throw new CodexImageException(CodexImageFailure.ToolUnavailable);

        var helper = CodexExecutableResolver.Find("codex-imagegen", _helperPath, false)
            ?? (_helperPath is null ? CodexExecutableResolver.Find("codex-imagegen-cli", null, false) : null);
        if (helper is null) throw new CodexImageException(CodexImageFailure.ToolUnavailable);

        Directory.CreateDirectory(_tempRoot);
        var workDir = Path.Combine(_tempRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);
        var promptFile = Path.Combine(workDir, "prompt.txt");
        var output = Path.Combine(workDir, "result.png");
        try
        {
            var prompt = request.Prompt;
            if (refs.Any(r => r.Role == CodexReferenceRole.Identity))
                prompt = "Preserve the recognizable identity, facial features, and proportions of the person in the content references. " + prompt;
            // The helper supports a prompt file, so private prompts never enter process arguments.
            await File.WriteAllTextAsync(promptFile, prompt, new UTF8Encoding(false), ct);
            var args = new List<string> { content.Length > 0 ? "edit" : "generate",
                "--prompt-file", promptFile, "--out", output, "--output-format", "png",
                "--size", request.Size, "--quality", request.Quality, "--size-policy", "warn" };
            foreach (var reference in content) { args.Add("--image"); args.Add(reference.Path); }
            if (style.Length == 1) { args.Add("--style-image"); args.Add(style[0].Path); }
            var result = await _runner.RunAsync(helper, args, workDir, ImageTimeout, false, ct);
            if (result.ExitCode != 0)
                throw new CodexImageException(result.FailureHint ?? CodexImageFailure.BackendChanged);
            // Do not trust text saying "saved"; require bytes at the exact private output path.
            var outputInfo = new FileInfo(output);
            if (!outputInfo.Exists || outputInfo.Length < 45 || outputInfo.Length > CodexPngValidator.MaxBytes)
                throw new CodexImageException(CodexImageFailure.InvalidImage);
            var bytes = await File.ReadAllBytesAsync(output, ct);
            var (width, height) = CodexPngValidator.Validate(bytes);
            return new CodexImageOutput(bytes, width, height, probe.HelperVersion);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw new CodexImageException(CodexImageFailure.Cancelled);
        }
        finally
        {
            try { Directory.Delete(workDir, recursive: true); } catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}

public static class CodexPngValidator
{
    public const long MaxBytes = 24 * 1024 * 1024;
    private static readonly byte[] Signature = { 137, 80, 78, 71, 13, 10, 26, 10 };

    public static (int Width, int Height) Validate(byte[] bytes)
    {
        if (bytes.Length < 57 || bytes.LongLength > MaxBytes ||
            !bytes.AsSpan(0, 8).SequenceEqual(Signature))
            throw new CodexImageException(CodexImageFailure.InvalidImage);
        var position = 8;
        var hasIdat = false;
        var hasIend = false;
        int width = 0, height = 0;
        var first = true;
        while (position <= bytes.Length - 12)
        {
            var length = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(position, 4));
            if (length > bytes.Length - position - 12)
                throw new CodexImageException(CodexImageFailure.InvalidImage);
            var type = Encoding.ASCII.GetString(bytes, position + 4, 4);
            var payload = position + 8;
            if (first)
            {
                if (type != "IHDR" || length != 13)
                    throw new CodexImageException(CodexImageFailure.InvalidImage);
                var w = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(payload, 4));
                var h = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(payload + 4, 4));
                if (w == 0 || h == 0 || w > 8192 || h > 8192 || (long)w * h > 36_000_000 ||
                    bytes[payload + 8] is not (8 or 16) ||
                    bytes[payload + 9] is not (0 or 2 or 4 or 6) ||
                    bytes[payload + 10] != 0 || bytes[payload + 11] != 0)
                    throw new CodexImageException(CodexImageFailure.InvalidImage);
                width = (int)w; height = (int)h;
                first = false;
            }
            if (type == "IDAT" && length > 0) hasIdat = true;
            position += checked((int)length) + 12;
            if (type == "IEND") { hasIend = length == 0 && position == bytes.Length; break; }
        }
        if (first || !hasIdat || !hasIend)
            throw new CodexImageException(CodexImageFailure.InvalidImage);
#if WINDOWS
        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            using var bitmap = System.Drawing.Image.FromStream(stream, useEmbeddedColorManagement: false, validateImageData: true);
            if (bitmap.Width != width || bitmap.Height != height)
                throw new CodexImageException(CodexImageFailure.InvalidImage);
        }
        catch (CodexImageException) { throw; }
        catch (Exception) { throw new CodexImageException(CodexImageFailure.InvalidImage); }
#endif
        return (width, height);
    }
}
