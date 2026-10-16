using System.Drawing.Imaging;
using WallTrek.Services;

namespace WallTrek.Services.ImageGen.Codex;

/// <summary>
/// Opt-in, unsupported account-backed image provider. The legacy generation pipeline
/// can use text-to-image now; callers with reference sets can use EditAsync explicitly.
/// </summary>
public sealed class CodexExperimentalImageGenerator : IImageGenerationService
{
    private readonly CodexImageBridge _bridge;

    public CodexExperimentalImageGenerator()
    {
        var settings = Settings.Instance;
        _bridge = new CodexImageBridge(settings.CodexExperimentalEnabled,
            settings.CodexExecutablePath, settings.CodexHelperPath);
    }

    public CodexExperimentalImageGenerator(CodexImageBridge bridge) => _bridge = bridge;

    public async Task<ImageGenerationResult> GenerateImage(string prompt, CancellationToken cancellationToken = default)
    {
        var output = await _bridge.GenerateAsync(new CodexImageRequest(prompt), cancellationToken);
        return new ImageGenerationResult { ImageData = output.Png, Format = ImageFormat.Png };
    }

    public async Task<ImageGenerationResult> EditAsync(
        string prompt, IReadOnlyList<CodexReference> references, CancellationToken cancellationToken = default)
    {
        if (references.Count == 0)
            throw new ArgumentException("At least one reference is required for an edit.", nameof(references));
        var output = await _bridge.GenerateAsync(new CodexImageRequest(prompt, references), cancellationToken);
        return new ImageGenerationResult { ImageData = output.Png, Format = ImageFormat.Png };
    }

    public Task<CodexProbe> ProbeAsync(CancellationToken ct = default) => _bridge.ProbeAsync(ct);
}
