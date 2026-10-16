using System.Drawing.Imaging;
using WallTrek.Services.ImageGen;

namespace WallTrek.Services.Providers;

/// <summary>Registry bridges new profile providers into the stable upstream image pipeline.</summary>
public static class ProviderRegistry
{
    public static IImageProvider Create(Guid profileId, ProviderProfileStore? store = null,
        IProviderSecretStore? secretStore = null)
    {
        store ??= ProviderProfileStore.Default;
        secretStore ??= new WindowsProviderSecretStore();
        var profile = store.Find(profileId);
        if (profile is null || !profile.Enabled)
            throw new ProviderException(ProviderFailure.InvalidConfiguration);
        return new OpenAiCompatibleProvider(profile, secretStore);
    }
}

public sealed class CustomImageGenerationService : IImageGenerationService
{
    private readonly IImageProvider _provider;
    public CustomImageGenerationService(IImageProvider provider) => _provider = provider;

    public async Task<ImageGenerationResult> GenerateImage(string prompt,
        CancellationToken cancellationToken = default)
    {
        // No optional vendor-specific quality or size is sent without a declaration.
        var output = await _provider.GenerateAsync(new ProviderImageRequest(prompt), cancellationToken);
        var format = output.MimeType switch
        {
            "image/png" => ImageFormat.Png,
            "image/jpeg" => ImageFormat.Jpeg,
            // WinUI/legacy FileService uses System.Drawing; WebP needs a dedicated decoder.
            _ => throw new ProviderException(ProviderFailure.UnsupportedOperation)
        };
        return new ImageGenerationResult { ImageData = output.ImageData, Format = format };
    }
}
