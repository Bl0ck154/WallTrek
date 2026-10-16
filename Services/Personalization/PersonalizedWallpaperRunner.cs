using WallTrek.Services.ImageGen;
using WallTrek.Services.Providers;

namespace WallTrek.Services.Personalization;

public sealed record PersonalizedRenderResult(
    ImageGenerationResult Image, string ProviderId, string ModelId);

/// <summary>Deliberate provider dispatch. No silent cross-vendor reference fallback.</summary>
public static class PersonalizedWallpaperRunner
{
    public static async Task<PersonalizedRenderResult> RunAsync(ImageModelOption option,
        PreparedWallpaper prepared, PersonalizationLibrary library, CancellationToken ct,
        CodexPersonalizedImageProvider? codexForTesting = null)
    {
        if (prepared.ReferenceIds.Count > 0 && option.Provider == ImageProvider.CodexExperimental)
            return await RunCodexReferencesAsync(prepared, library,
                codexForTesting ?? CodexPersonalizedImageProvider.FromSettings(), ct);

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

    private static async Task<PersonalizedRenderResult> RunCodexReferencesAsync(
        PreparedWallpaper prepared, PersonalizationLibrary library,
        CodexPersonalizedImageProvider codex, CancellationToken ct)
    {
        // Explicit opt-in and per-host/per-photo approval must precede reading bytes
        // or probing/spawning the experimental helper.
        if (!WallTrek.Services.Settings.Instance.CodexExperimentalEnabled)
            throw new InvalidOperationException(
                "Enable experimental Codex ImageGen in Settings before using personal photos.");

        if (!library.HasReferenceApproval(prepared, CodexPersonalConsent.ProviderId,
                CodexPersonalConsent.Origin))
            throw new InvalidOperationException(
                "Codex photo upload not approved. In Personalize select Codex/ChatGPT and approve these photos.");

        if (prepared.ReferenceIds.Count is < 1 or > 5)
            throw new InvalidOperationException("Select 1–5 references for Codex editing.");

        var probe = await codex.ProbeAsync(ct);
        if (!probe.ConnectionOk)
            throw new InvalidOperationException(probe.Status);

        // No custom-provider fallback. A failed Codex edit leaves the previous wallpaper
        // untouched and does not consume the scenario's next variation.
        var sanitized = library.ReadForProvider(prepared.ReferenceIds);
        var coordinator = new PersonalizedGenerationCoordinator(codex, codex.EditAsync);
        var image = await coordinator.GenerateAsync(prepared, sanitized, ct);
        return new(image, codex.ProviderId, "codex-imagegen-experimental");
    }
}
