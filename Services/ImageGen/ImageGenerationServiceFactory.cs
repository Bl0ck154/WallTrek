using WallTrek.Services;
using WallTrek.Services.Providers;
using WallTrek.Services.ImageGen.Codex;

namespace WallTrek.Services.ImageGen
{
    public static class ImageGenerationServiceFactory
    {
        public static IImageGenerationService CreateService(ImageModelOption option)
        {
            var settings = Settings.Instance;

            switch (option.Provider)
            {
                // OpenAI gpt-image family.
                case ImageProvider.OpenAI:
                    if (string.IsNullOrEmpty(settings.ApiKey))
                        throw new InvalidOperationException("OpenAI API key is required for OpenAI image models");
                    return new OpenAiImageGenerator(settings.ApiKey, option.ModelId, option.OpenAiQuality);

                // Google — Imagen (predict endpoint) and Gemini (generateContent endpoint).
                case ImageProvider.Google:
                    if (string.IsNullOrEmpty(settings.GoogleApiKey))
                        throw new InvalidOperationException("Google API key is required for Imagen and Gemini image models");
                    return new GoogleImagenService(settings.GoogleApiKey, option.ModelId, option.ImageSize, option.AspectRatio);

                case ImageProvider.CustomOpenAiCompatible:
                    if (!option.Id.StartsWith("custom:", StringComparison.Ordinal) ||
                        !Guid.TryParseExact(option.Id.AsSpan(7), "N", out var profileId))
                        throw new ProviderException(ProviderFailure.InvalidConfiguration);
                    return new CustomImageGenerationService(ProviderRegistry.Create(profileId));

                case ImageProvider.CodexExperimental:
                    if (!settings.CodexExperimentalEnabled)
                        throw new InvalidOperationException("Enable the experimental Codex image backend in Settings first.");
                    return new CodexExperimentalImageGenerator();

                default:
                    throw new ArgumentException($"Unsupported image provider: {option.Provider}");
            }
        }
    }
}