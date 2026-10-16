using WallTrek.Services.ImageGen;
using WallTrek.Services.Providers;

namespace WallTrek.Services.Personalization;

public sealed record PersonalizedRenderResult(
    ImageGenerationResult Image, string ProviderId, string ModelId);

/// <summary>Deliberate provider dispatch. No silent cross-vendor reference fallback.</summary>
public static class PersonalizedWallpaperRunner
{
    public static async Task<PersonalizedRenderResult> RunAsync(ImageModelOption option,
        PreparedWallpaper prepared, PersonalizationLibrary library, CancellationToken ct)
    {
        if (prepared.ReferenceIds.Count > 0 && option.Provider != ImageProvider.CustomOpenAiCompatible)
            throw new ProviderException(ProviderFailure.UnsupportedOperation);

        if (option.Provider != ImageProvider.CustomOpenAiCompatible)
        {
            var legacy = ImageGenerationServiceFactory.CreateService(option);
            var image = await legacy.GenerateImage(prepared.FinalPrompt, ct);
            return new PersonalizedRenderResult(image, option.Provider.ToString(), option.ModelId);
        }

        if (!option.Id.StartsWith("custom:", StringComparison.Ordinal) ||
            !Guid.TryParseExact(option.Id.AsSpan(7), "N", out var id))
            throw new ProviderException(ProviderFailure.InvalidConfiguration);
        var profile = ProviderProfileStore.Default.Find(id);
        if (profile is null || !profile.Enabled)
            throw new ProviderException(ProviderFailure.InvalidConfiguration);
        var provider = ProviderRegistry.Create(id);
        var references = Array.Empty<ProviderImageReference>();
        Func<ProviderImageRequest, CancellationToken, Task<ProviderImageResult>>? edit = null;
        var model = profile.ImageModel;
        if (prepared.ReferenceIds.Count > 0)
        {
            if (!profile.EnableEdits || !profile.EditVerified)
                throw new InvalidOperationException(
                    "Test this provider's real image edit in Settings → Providers before using personal photos.");
            var origin = new Uri(profile.BaseUrl).GetLeftPart(UriPartial.Authority);
            if (!library.HasReferenceApproval(prepared, id, origin))
                throw new InvalidOperationException(
                    "Photo transfer not approved for this provider/host. Open Personalize and approve the destination.");
            // All references are validated/sanitized by the local library before HTTP.
            references = library.ReadForProvider(prepared.ReferenceIds).ToArray();
            if (provider is not OpenAiCompatibleProvider openAi)
                throw new ProviderException(ProviderFailure.UnsupportedOperation);
            edit = openAi.EditAsync;
            model = string.IsNullOrWhiteSpace(profile.EditModel) ? profile.ImageModel : profile.EditModel;
        }
        var coordinator = new PersonalizedGenerationCoordinator(provider, edit);
        var imageResult = await coordinator.GenerateAsync(prepared, references, ct);
        return new PersonalizedRenderResult(imageResult, provider.ProviderId, model);
    }
}
