using System;
using System.Threading;
using System.Threading.Tasks;
using WallTrek.Services;

namespace WallTrek.Services.TextGen
{
    /// <summary>
    /// Generates a motion prompt for loop-video generation from an image's original generation
    /// prompt, using the configured LLM (same selection/keys as the rest of the app).
    /// </summary>
    public class MotionPromptService
    {
        private const int MaxPromptLength = 1000;

        public async Task<string?> GenerateMotionPromptAsync(string imagePrompt, CancellationToken cancellationToken = default)
        {
            var settings = Settings.Instance;
            var llmService = LlmServiceFactory.CreateService(
                settings.SelectedLlmModel,
                settings.ApiKey ?? string.Empty,
                settings.AnthropicApiKey ?? string.Empty,
                settings.GoogleApiKey ?? string.Empty
            );

            var systemPrompt =
                "You write motion prompts for image-to-video AI models. The video uses the same image as its " +
                "first and last frame, so the motion must loop seamlessly. Reply with only the motion prompt text.";

            var userPrompt =
                $"This wallpaper image was generated with the prompt: \"{imagePrompt}\". " +
                "Write a motion prompt to animate it as a seamless ambient loop. Describe subtle, cyclical motion " +
                "suited to the specific scene (for example drifting clouds, rippling water, flickering light, " +
                "swaying foliage, shimmering stars). Prefer motion that naturally returns to its starting state. " +
                "Avoid one-directional movement, new objects, scene changes, and camera movement. 1-3 sentences.";

            var response = await llmService.GenerateTextAsync(systemPrompt, userPrompt, cancellationToken);

            var prompt = response?.Trim().Trim('"');
            if (string.IsNullOrWhiteSpace(prompt))
            {
                return null;
            }
            if (prompt.Length > MaxPromptLength)
            {
                prompt = prompt.Substring(0, MaxPromptLength);
            }
            return prompt;
        }
    }
}
