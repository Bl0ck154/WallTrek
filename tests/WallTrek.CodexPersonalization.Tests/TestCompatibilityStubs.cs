// Test-only substitutes for UI/config and non-Codex providers. The actual production
// PersonalizedWallpaperRunner and CodexPersonalizedImageProvider source files are linked
// unchanged into this fake-test project. None of these types build into WallTrek.csproj.
namespace WallTrek.Services
{
    public sealed class Settings
    {
        public static Settings Instance { get; } = new();
        public bool CodexExperimentalEnabled { get; set; }
        public string? CodexExecutablePath { get; set; }
        public string? CodexHelperPath { get; set; }
    }
}

namespace WallTrek.Services.ImageGen
{
    public static class ImageGenerationServiceFactory
    {
        public static IImageGenerationService CreateService(ImageModelOption _) =>
            throw new InvalidOperationException("Built-in provider not used by Codex fake tests.");
    }
}

namespace WallTrek.Services.Providers
{
    public sealed class ProviderProfileStore
    {
        public static ProviderProfileStore Default { get; } = new();
        public ProviderProfile? Find(Guid _) => null;
    }
    public static class ProviderRegistry
    {
        public static IImageProvider Create(Guid _) =>
            throw new InvalidOperationException("Custom host not used by Codex fake tests.");
    }
    public sealed class OpenAiCompatibleProvider : IImageProvider
    {
        public string ProviderId => "custom";
        public Task<ProviderCapabilities> ProbeAsync(CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<ProviderImageResult> GenerateAsync(ProviderImageRequest request,
            CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ProviderImageResult> EditAsync(ProviderImageRequest request,
            CancellationToken ct = default) => throw new NotSupportedException();
    }
}
