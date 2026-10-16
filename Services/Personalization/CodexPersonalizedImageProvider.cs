using System.Security.AccessControl;
using System.Security.Principal;
using WallTrek.Services.ImageGen.Codex;
using WallTrek.Services.Providers;

namespace WallTrek.Services.Personalization;

/// <summary>
/// Adapts the existing CodexImageBridge to the shared IImageProvider request contract.
/// It never reads Codex credentials. Only sanitized image bytes reach temporary files,
/// whose paths are passed to the separately installed, experimental helper.
/// </summary>
public interface ICodexPersonalImageClient
{
    Task<CodexProbe> ProbeAsync(CancellationToken ct);
    Task<CodexImageOutput> GenerateAsync(CodexImageRequest request, CancellationToken ct);
}

public sealed class CodexBridgeImageClient : ICodexPersonalImageClient
{
    private readonly CodexImageBridge _bridge;
    public CodexBridgeImageClient(CodexImageBridge bridge) => _bridge = bridge;
    public Task<CodexProbe> ProbeAsync(CancellationToken ct) => _bridge.ProbeAsync(ct);
    public Task<CodexImageOutput> GenerateAsync(CodexImageRequest request, CancellationToken ct) =>
        _bridge.GenerateAsync(request, ct);
}

public static class CodexPersonalConsent
{
    // A stable, non-credential identifier to reuse the scenario library's existing
    // (scenario, provider ID, origin, reference fingerprint) consent mechanism.
    public static readonly Guid ProviderId = Guid.Parse("faf56da9-9292-4327-aa8a-c9bd507fd2b9");
    public const string Origin = "https://chatgpt.com";
    public const string DestinationDescription =
        "ChatGPT/Codex via the UNOFFICIAL codex-imagegen helper. " +
        "The helper reads this Windows user's local Codex sign-in and calls undocumented ChatGPT endpoints. " +
        "WallTrek never opens or copies login tokens. Account eligibility is not guaranteed.";
}

public sealed class CodexPersonalizedImageProvider : IImageProvider
{
    private readonly ICodexPersonalImageClient _client;
    private readonly string _temporaryRoot;
    private readonly bool _enabled;
    public string ProviderId => "codex:experimental";
    public const int MaxReferences = 5;
    private const int MaxBytes = 20 * 1024 * 1024;

    public CodexPersonalizedImageProvider(ICodexPersonalImageClient client, bool enabled,
        string? temporaryRoot = null)
    {
        _client = client;
        _enabled = enabled;
        _temporaryRoot = temporaryRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WallTrek", "CodexReferences");
    }

    public static CodexPersonalizedImageProvider FromSettings()
    {
        var settings = WallTrek.Services.Settings.Instance;
        var bridge = new CodexImageBridge(settings.CodexExperimentalEnabled,
            settings.CodexExecutablePath, settings.CodexHelperPath);
        return new(new CodexBridgeImageClient(bridge), settings.CodexExperimentalEnabled);
    }

    /// <summary>Installed + signed-in is NOT a verified image entitlement.</summary>
    public async Task<ProviderCapabilities> ProbeAsync(CancellationToken ct = default)
    {
        if (!_enabled)
            return new(false, false, false,
                "Codex experimental images disabled in Settings; no photos were sent.",
                DateTimeOffset.UtcNow);
        var probe = await _client.ProbeAsync(ct);
        return new(probe.Problem is null && probe.CodexInstalled && probe.SignedIn && probe.HelperInstalled,
            false, false,
            probe.Problem is not null ? new CodexImageException(probe.Problem.Value).Message :
            "Codex and helper detected and locally signed in. Image generation/edit capability UNVERIFIED: a real output test is required.",
            DateTimeOffset.UtcNow);
    }

    public Task<ProviderImageResult> GenerateAsync(ProviderImageRequest request, CancellationToken ct = default)
    {
        if (request.References is { Count: > 0 })
            throw new ProviderException(ProviderFailure.UnsupportedOperation);
        return ExecuteAsync(request, edit: false, ct);
    }

    public Task<ProviderImageResult> EditAsync(ProviderImageRequest request, CancellationToken ct = default) =>
        ExecuteAsync(request, edit: true, ct);

    private async Task<ProviderImageResult> ExecuteAsync(ProviderImageRequest request, bool edit, CancellationToken ct)
    {
        if (!_enabled) throw new CodexImageException(CodexImageFailure.ToolUnavailable);
        var refs = request.References ?? Array.Empty<ProviderImageReference>();
        if (edit && refs.Count is < 1 or > MaxReferences)
            throw new ProviderException(ProviderFailure.InvalidConfiguration);
        if (!edit && refs.Count > 0)
            throw new ProviderException(ProviderFailure.UnsupportedOperation);
        var styleCount = refs.Count(r => r.Role == ProviderReferenceRole.Style);
        if (styleCount > 1 || (styleCount == refs.Count && refs.Count != 0))
            throw new InvalidOperationException(
                "Experimental Codex helper accepts at most one style photo, plus at least one identity/content photo.");
        // The bridge supports exactly this size/quality. Defaults can be changed later
        // only when helper support for additional values has been verified on Windows.
        var size = request.Size ?? "1536x1024";
        var quality = request.Quality ?? "high";
        if (size is not ("auto" or "1024x1024" or "1536x1024" or "1024x1536") ||
            quality is not ("low" or "medium" or "high" or "auto"))
            throw new ProviderException(ProviderFailure.InvalidConfiguration);

        var paths = new List<CodexReference>();
        string? workDirectory = null;
        try
        {
            ct.ThrowIfCancellationRequested();
            if (refs.Count > 0)
            {
                Directory.CreateDirectory(_temporaryRoot);
                workDirectory = Path.Combine(_temporaryRoot, Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(workDirectory);
                RestrictToCurrentWindowsUser(workDirectory);
                for (int i = 0; i < refs.Count; ++i)
                {
                    ct.ThrowIfCancellationRequested();
                    var item = refs[i];
                    if (item.Bytes.Length is < 64 or > MaxBytes)
                        throw new ProviderException(ProviderFailure.InvalidImage);
                    var (mime, _, _) = ProviderImageValidator.Check(item.Bytes);
                    // Our reference library encodes EXIF-free JPEG. We only allow the
                    // sanitized form here, not arbitrary third-party filenames/paths.
                    if (mime != "image/jpeg")
                        throw new ProviderException(ProviderFailure.InvalidImage);
                    var path = Path.Combine(workDirectory, $"ref-{i + 1:D2}-{Guid.NewGuid():N}.jpg");
                    using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write,
                        FileShare.None, 81920, FileOptions.Asynchronous))
                        await file.WriteAsync(item.Bytes, ct);
                    paths.Add(new CodexReference(path, item.Role switch
                    {
                        ProviderReferenceRole.Identity => CodexReferenceRole.Identity,
                        ProviderReferenceRole.Content => CodexReferenceRole.Content,
                        ProviderReferenceRole.Style => CodexReferenceRole.Style,
                        _ => throw new ProviderException(ProviderFailure.InvalidConfiguration)
                    }));
                }
            }

            // CodexImageBridge handles signed-in CLI/helper detection before dispatch,
            // timeout/cancellation, prompt-file handoff, typed error mapping and PNG validation.
            // The original files and tokens are never passed to it.
            var output = await _client.GenerateAsync(
                new CodexImageRequest(request.Prompt, paths, size, quality), ct);
            var (width, height) = CodexPngValidator.Validate(output.Png);
            if (width != output.Width || height != output.Height)
                throw new CodexImageException(CodexImageFailure.InvalidImage);
            return new(output.Png, "image/png", width, height, ProviderId, "codex-imagegen-experimental");
        }
        finally
        {
            // No temp file, including a cancelled/failed upload, survives a normal call.
            if (workDirectory is not null)
            {
                try { Directory.Delete(workDirectory, recursive: true); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    private static void RestrictToCurrentWindowsUser(string directory)
    {
        if (!OperatingSystem.IsWindows()) return;
        var identity = WindowsIdentity.GetCurrent().User
            ?? throw new UnauthorizedAccessException("Current Windows identity unavailable.");
        var acl = new DirectorySecurity();
        acl.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        acl.AddAccessRule(new FileSystemAccessRule(identity, FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(directory).SetAccessControl(acl);
    }
}
