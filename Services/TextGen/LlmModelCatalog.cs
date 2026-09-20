using System.Collections.Generic;
using System.Linq;

namespace WallTrek.Services.TextGen
{
    /// <summary>
    /// The catalog of text/LLM models shown in the LLM dropdown.
    /// The per-prompt estimate assumes a paragraph-sized generation of roughly 700 input +
    /// 300 output tokens (prompt + title + tags) at list prices. Approximate and display-only.
    /// </summary>
    public static class LlmModelCatalog
    {
        public static IReadOnlyList<LlmModelOption> Options { get; } = new List<LlmModelOption>
        {
            new() { ModelId = "gpt-6-astra",   Model = "OpenAI GPT-6 Astra",   PerPrompt = "~2.2¢",  Cents = 2.2m },
            new() { ModelId = "gpt-5.6-sol",   Model = "OpenAI GPT-5.6 Sol",   PerPrompt = "~0.9¢",  Cents = 0.9m },
            new() { ModelId = "gpt-5.6-terra", Model = "OpenAI GPT-5.6 Terra", PerPrompt = "~0.5¢",  Cents = 0.5m },
            new() { ModelId = "gpt-5.6-luna",  Model = "OpenAI GPT-5.6 Luna",  PerPrompt = "~0.05¢", Cents = 0.05m },
            new() { ModelId = "gpt-5.4",      Model = "OpenAI GPT-5.4",      PerPrompt = "~0.6¢", Cents = 0.6m },
            new() { ModelId = "gpt-5.4-mini", Model = "OpenAI GPT-5.4 mini", PerPrompt = "~0.2¢", Cents = 0.2m },
            new() { ModelId = "claude-opus-5",             Model = "Claude Opus 5",     PerPrompt = "~1.1¢", Cents = 1.1m },
            new() { ModelId = "claude-sonnet-5",           Model = "Claude Sonnet 5",   PerPrompt = "~0.4¢", Cents = 0.4m },
            new() { ModelId = "claude-haiku-4-5-20251001", Model = "Claude Haiku 4.5",  PerPrompt = "~0.2¢", Cents = 0.2m },
            // Gemini 3.8 Flash's introductory pricing runs through December 31, 2026.
            new() { ModelId = "gemini-3.8-flash",       Model = "Gemini 3.8 Flash", PerPrompt = "~0.2¢", Cents = 0.2m },
            new() { ModelId = "gemini-3.1-pro-preview", Model = "Gemini 3.1 Pro",   PerPrompt = "~0.5¢", Cents = 0.5m },
        };

        public static LlmModelOption Default =>
            Options.FirstOrDefault(o => o.ModelId == "gpt-5.6-sol") ?? Options[0];

        public static LlmModelOption? FindById(string? modelId) =>
            Options.FirstOrDefault(o => o.ModelId == NormalizeId(modelId));

        /// <summary>Maps model IDs saved by older WallTrek versions to their current replacement.</summary>
        public static string? NormalizeId(string? modelId) => modelId switch
        {
            "gpt-5.5" => "gpt-5.6-sol",
            "claude-opus-4-8" => "claude-opus-5",
            "claude-sonnet-4-6" => "claude-sonnet-5",
            "gemini-3.6-flash" => "gemini-3.8-flash",
            "gemini-3-flash-preview" => "gemini-3.8-flash",
            _ => modelId,
        };
    }
}
