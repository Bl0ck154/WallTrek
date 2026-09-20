using System.Collections.Generic;
using System.Linq;

namespace WallTrek.Services.VideoGen
{
    /// <summary>
    /// Catalog of image-to-video options for the Generate Video dialog. All entries support a
    /// start + end frame so the source image can be used as both, producing a seamless loop.
    /// Prices are approximate per-clip and display-only (fal.ai list prices, September 2026).
    /// </summary>
    public static class VideoModelCatalog
    {
        public static IReadOnlyList<VideoModelOption> Options { get; } = new List<VideoModelOption>
        {
            new() { Id = "pixverse-v6-transition@540p", Kind = VideoEndpointKind.PixVerseTransition,
                    Endpoint = "fal-ai/pixverse/v6/transition", Name = "PixVerse V6", Quality = "540p",
                    Resolution = "540p", Durations = new[] { 5, 8 },
                    CentsPerSecond = 3.5m },
            new() { Id = "pixverse-v6-transition@720p", Kind = VideoEndpointKind.PixVerseTransition,
                    Endpoint = "fal-ai/pixverse/v6/transition", Name = "PixVerse V6", Quality = "720p",
                    Resolution = "720p", Durations = new[] { 5, 8 },
                    CentsPerSecond = 4.5m },
            new() { Id = "pixverse-v6-transition@1080p", Kind = VideoEndpointKind.PixVerseTransition,
                    Endpoint = "fal-ai/pixverse/v6/transition", Name = "PixVerse V6", Quality = "1080p",
                    Resolution = "1080p", Durations = new[] { 5, 8 },
                    CentsPerSecond = 9m },
            new() { Id = "kling-v3-pro", Kind = VideoEndpointKind.KlingV3StartEnd,
                    Endpoint = "fal-ai/kling-video/v3/pro/image-to-video", Name = "Kling 3.0", Quality = "Pro",
                    Durations = new[] { 5, 8, 10 }, CentsPerSecond = 11.2m },
            new() { Id = "kling-o1@1080p", Kind = VideoEndpointKind.KlingStartEnd,
                    Endpoint = "fal-ai/kling-video/o1/image-to-video", Name = "Kling O1", Quality = "1080p",
                    Durations = new[] { 5, 8, 10 }, CentsPerSecond = 11.2m },
        };

        /// <summary>Default = Kling O1 1080p.</summary>
        public static VideoModelOption Default =>
            Options.FirstOrDefault(o => o.Id == "kling-o1@1080p") ?? Options[0];

        public static VideoModelOption? FindById(string? id) =>
            Options.FirstOrDefault(o => o.Id == id);

        /// <summary>Upscale tiers via fal-ai/bytedance-upscaler (standard tier, 30fps).</summary>
        public static IReadOnlyList<VideoUpscaleOption> UpscaleOptions { get; } = new List<VideoUpscaleOption>
        {
            new() { Id = "none", Name = "None", TargetResolution = "",   CentsPerSecond = 0m },
            new() { Id = "2k",   Name = "2K",   TargetResolution = "2k", CentsPerSecond = 1.44m },
            new() { Id = "4k",   Name = "4K",   TargetResolution = "4k", CentsPerSecond = 2.88m },
        };

        public static VideoUpscaleOption DefaultUpscale =>
            UpscaleOptions.FirstOrDefault(o => o.Id == "2k") ?? UpscaleOptions[0];
    }
}
