using WallTrek.Services.ImageGen;
using WallTrek.Services.Providers;
using System.Drawing.Imaging;

namespace WallTrek.Services.Personalization;

/// <summary>
/// Adapt a shared IImageProvider to scenario references without changing provider implementations.
/// Editing is explicitly injected as a capability; reference bytes are NEVER discarded.
/// </summary>
public sealed class PersonalizedGenerationCoordinator
{
    private readonly IImageProvider _provider;
    private readonly Func<ProviderImageRequest, CancellationToken, Task<ProviderImageResult>>? _edit;

    public PersonalizedGenerationCoordinator(IImageProvider provider,
        Func<ProviderImageRequest, CancellationToken, Task<ProviderImageResult>>? edit = null)
    {
        _provider = provider;
        _edit = edit;
    }

    public async Task<ImageGenerationResult> GenerateAsync(PreparedWallpaper prepared,
        IReadOnlyList<ProviderImageReference> references, CancellationToken ct = default)
    {
        if (references.Count != prepared.ReferenceIds.Count)
            throw new InvalidOperationException("Reference set changed during preparation.");
        if (references.Count is > 5)
            throw new InvalidOperationException("Too many references.");
        var request = new ProviderImageRequest(prepared.FinalPrompt, references);
        // Never fall back to text generation if edit is unsupported.
        var output = references.Count == 0
            ? await _provider.GenerateAsync(request, ct)
            : _edit is null
                ? throw new ProviderException(ProviderFailure.UnsupportedOperation)
                : await _edit(request, ct);
        var (mime, width, height) = ProviderImageValidator.Check(output.ImageData);
        if (mime != output.MimeType || width != output.Width || height != output.Height)
            throw new ProviderException(ProviderFailure.InvalidImage);
        var format = mime switch
        {
            "image/png" => ImageFormat.Png,
            "image/jpeg" => ImageFormat.Jpeg,
            _ => throw new ProviderException(ProviderFailure.UnsupportedOperation)
        };
        return new ImageGenerationResult { ImageData = output.ImageData, Format = format };
    }
}
