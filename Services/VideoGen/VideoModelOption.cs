using System;
using System.Collections.Generic;

namespace WallTrek.Services.VideoGen
{
    /// <summary>Which fal endpoint family the option drives (they take different input fields).</summary>
    public enum VideoEndpointKind
    {
        /// <summary>fal-ai/pixverse/v5/transition — first_image_url + end_image_url.</summary>
        PixVerseTransition,
        /// <summary>fal-ai/kling-video/o1/image-to-video — start_image_url + end_image_url.</summary>
        KlingStartEnd,
    }

    /// <summary>
    /// A selectable image-to-video option shown in the Generate Video dialog. All options accept a
    /// start and end frame, so passing the wallpaper as both produces a seamless loop.
    /// Prices are approximate (US cents) and display-only — edit freely.
    /// </summary>
    public class VideoModelOption
    {
        /// <summary>Stable key, e.g. "pixverse-v5-transition@720p".</summary>
        public string Id { get; init; } = "";

        public VideoEndpointKind Kind { get; init; }

        /// <summary>fal endpoint id, e.g. "fal-ai/pixverse/v5/transition".</summary>
        public string Endpoint { get; init; } = "";

        // --- Display columns ---
        public string Name { get; init; } = "";
        public string Quality { get; init; } = "";

        /// <summary>Resolution parameter value for endpoints that take one (PixVerse); "" = not sent.</summary>
        public string Resolution { get; init; } = "";

        /// <summary>Durations (seconds) the endpoint supports.</summary>
        public int[] Durations { get; init; } = Array.Empty<int>();

        /// <summary>Per-second price in cents; used when no exact per-clip price is listed.</summary>
        public decimal CentsPerSecond { get; init; }

        /// <summary>Exact per-clip prices in cents keyed by duration (overrides CentsPerSecond).</summary>
        public Dictionary<int, decimal> CentsByDuration { get; init; } = new();

        public string DisplayName => $"{Name} · {Quality}";

        public decimal GetCents(int durationSeconds) =>
            CentsByDuration.TryGetValue(durationSeconds, out var cents)
                ? cents
                : CentsPerSecond * durationSeconds;
    }

    /// <summary>
    /// Video upscale tiers (fal-ai/bytedance-upscaler, billed per second of video at 30fps).
    /// </summary>
    public class VideoUpscaleOption
    {
        public string Id { get; init; } = "";
        public string Name { get; init; } = "";

        /// <summary>target_resolution parameter value; "" = skip upscaling.</summary>
        public string TargetResolution { get; init; } = "";

        public decimal CentsPerSecond { get; init; }

        public decimal GetCents(int durationSeconds) => CentsPerSecond * durationSeconds;
    }
}
