using System.Collections.Generic;
using System.Linq;
using WallTrek.Services;

namespace WallTrek.Services.ImageGen
{
    /// <summary>
    /// The catalog of image-generation options shown in the model dropdown.
    /// Prices are approximate per-image (in cents) and display-only — edit freely.
    /// </summary>
    public static class ImageModelCatalog
    {
        private static readonly IReadOnlyList<ImageModelOption> AllOptions = new List<ImageModelOption>
        {
            new() { Id = "codex-imagegen@experimental", Provider = ImageProvider.CodexExperimental,
                    ModelId = "codex-imagegen-experimental", Model = "Codex (experimental)",
                    Quality = "High", Dimensions = "1536×1024 requested",
                    Price = "Account quota; price unknown", Cents = 0m },
            new() { Id = "gemini-3-pro-image@4K", Provider = ImageProvider.Google, ModelId = "gemini-3-pro-image",
                    Model = "Nano Banana Pro", Quality = "4K", Dimensions = "5504×3072", Price = "~24¢", Cents = 24m,
                    ImageSize = "4K", AspectRatio = "16:9" },
            new() { Id = "gemini-3-pro-image@2K", Provider = ImageProvider.Google, ModelId = "gemini-3-pro-image",
                    Model = "Nano Banana Pro", Quality = "2K", Dimensions = "2752×1536", Price = "~13.4¢", Cents = 13.4m,
                    ImageSize = "2K", AspectRatio = "16:9" },
            new() { Id = "gemini-3.1-flash-image@4K", Provider = ImageProvider.Google, ModelId = "gemini-3.1-flash-image",
                    Model = "Nano Banana 2", Quality = "4K", Dimensions = "5504×3072", Price = "~15.1¢", Cents = 15.1m,
                    ImageSize = "4K", AspectRatio = "16:9" },
            new() { Id = "gemini-3.1-flash-image@2K", Provider = ImageProvider.Google, ModelId = "gemini-3.1-flash-image",
                    Model = "Nano Banana 2", Quality = "2K", Dimensions = "2752×1536", Price = "~10.1¢", Cents = 10.1m,
                    ImageSize = "2K", AspectRatio = "16:9" },
            new() { Id = "gemini-3.1-flash-lite-image@1K", Provider = ImageProvider.Google, ModelId = "gemini-3.1-flash-lite-image",
                    Model = "Nano Banana 2 Lite", Quality = "1K", Dimensions = "1376×768", Price = "~3.4¢", Cents = 3.4m,
                    ImageSize = "1K", AspectRatio = "16:9" },
            // GPT Image 2.5 is billed by actual output-token usage, so these are rough
            // estimates using GPT Image 2's 1536×1024 token counts at the 2.5 token rate.
            new() { Id = "gpt-image-2.5-sunburst@high", Provider = ImageProvider.OpenAI, ModelId = "gpt-image-2.5-sunburst",
                    Model = "GPT Image 2.5 Sunburst", Quality = "High", Dimensions = "1536×1024", Price = "~33¢", Cents = 33m,
                    OpenAiQuality = "high" },
            new() { Id = "gpt-image-2.5-sunburst@medium", Provider = ImageProvider.OpenAI, ModelId = "gpt-image-2.5-sunburst",
                    Model = "GPT Image 2.5 Sunburst", Quality = "Medium", Dimensions = "1536×1024", Price = "~8.2¢", Cents = 8.2m,
                    OpenAiQuality = "medium" },
            new() { Id = "gpt-image-2.5-sunburst@low", Provider = ImageProvider.OpenAI, ModelId = "gpt-image-2.5-sunburst",
                    Model = "GPT Image 2.5 Sunburst", Quality = "Low", Dimensions = "1536×1024", Price = "~1¢", Cents = 1m,
                    OpenAiQuality = "low" },
            new() { Id = "gpt-image-2.5-flare@high", Provider = ImageProvider.OpenAI, ModelId = "gpt-image-2.5-flare",
                    Model = "GPT Image 2.5 Flare", Quality = "High", Dimensions = "1536×1024", Price = "~33¢", Cents = 33m,
                    OpenAiQuality = "high" },
            new() { Id = "gpt-image-2.5-flare@medium", Provider = ImageProvider.OpenAI, ModelId = "gpt-image-2.5-flare",
                    Model = "GPT Image 2.5 Flare", Quality = "Medium", Dimensions = "1536×1024", Price = "~8.2¢", Cents = 8.2m,
                    OpenAiQuality = "medium" },
            new() { Id = "gpt-image-2.5-flare@low", Provider = ImageProvider.OpenAI, ModelId = "gpt-image-2.5-flare",
                    Model = "GPT Image 2.5 Flare", Quality = "Low", Dimensions = "1536×1024", Price = "~1¢", Cents = 1m,
                    OpenAiQuality = "low" },
            new() { Id = "gpt-image-2@high", Provider = ImageProvider.OpenAI, ModelId = "gpt-image-2",
                    Model = "GPT Image 2", Quality = "High", Dimensions = "1536×1024", Price = "~16.5¢", Cents = 16.5m,
                    OpenAiQuality = "high" },
            new() { Id = "gpt-image-2@medium", Provider = ImageProvider.OpenAI, ModelId = "gpt-image-2",
                    Model = "GPT Image 2", Quality = "Medium", Dimensions = "1536×1024", Price = "~4.1¢", Cents = 4.1m,
                    OpenAiQuality = "medium" },
            new() { Id = "gpt-image-2@low", Provider = ImageProvider.OpenAI, ModelId = "gpt-image-2",
                    Model = "GPT Image 2", Quality = "Low", Dimensions = "1536×1024", Price = "~0.5¢", Cents = 0.5m,
                    OpenAiQuality = "low" },
        };

        // The model is not selectable until the user acknowledges risk in Settings.
        public static IReadOnlyList<ImageModelOption> Options =>
            Settings.Instance.CodexExperimentalEnabled
                ? AllOptions
                : AllOptions.Where(o => o.Provider != ImageProvider.CodexExperimental).ToArray();

        /// <summary>Default selection for fresh installs — good quality, low cost, native 16:9.</summary>
        public static ImageModelOption Default =>
            Options.FirstOrDefault(o => o.Id == "gemini-3.1-flash-image@2K") ?? Options[0];

        public static ImageModelOption? FindById(string? id)
        {
            var normalizedId = id switch
            {
                "gemini-3-pro-image-preview@4K" => "gemini-3-pro-image@4K",
                "gemini-3-pro-image-preview@2K" => "gemini-3-pro-image@2K",
                "imagen-4.0-ultra-generate-001@2K" => "gemini-3-pro-image@2K",
                "imagen-4.0-generate-001@2K" => "gemini-3.1-flash-image@2K",
                _ => id,
            };

            return Options.FirstOrDefault(o => o.Id == normalizedId);
        }

        /// <summary>Back-compat: match a legacy settings value that stored only a bare model id.</summary>
        public static ImageModelOption? FindByModelId(string? modelId) => modelId switch
        {
            "gemini-3-pro-image-preview" => FindById("gemini-3-pro-image@2K"),
            "imagen-4.0-ultra-generate-001" => FindById("gemini-3-pro-image@2K"),
            "imagen-4.0-generate-001" => FindById("gemini-3.1-flash-image@2K"),
            _ => Options.FirstOrDefault(o => o.ModelId == modelId),
        };
    }
}
