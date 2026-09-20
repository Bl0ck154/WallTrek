using Anthropic.SDK;
using Anthropic.SDK.Common;
using Anthropic.SDK.Messaging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Tool = Anthropic.SDK.Common.Tool;

namespace WallTrek.Services.TextGen
{
    public class AnthropicLlmService : ILlmService
    {
        private readonly AnthropicClient client;
        private readonly string model;

        public AnthropicLlmService(string apiKey, string model)
        {
            client = new AnthropicClient(apiKey);
            this.model = model;
        }

        public async Task<string> GenerateTextAsync(string systemPrompt, string userPrompt, CancellationToken cancellationToken = default)
        {
            var messages = new List<Message>
            {
                new Message(RoleType.User, userPrompt)
            };

            var parameters = new MessageParameters
            {
                Messages = messages,
                Model = model,
                // Claude 5 enables adaptive thinking by default, and thinking tokens count
                // against this ceiling. Leave enough room for its short visible response.
                MaxTokens = IsClaude5 ? 4096 : 1000,
                System = new List<SystemMessage> { new SystemMessage(systemPrompt) }
            };

            ConfigureModelParameters(parameters);

            var result = await client.Messages.GetClaudeMessageAsync(parameters, cancellationToken);

            // Extract text from the response content
            var textContent = result.Content?.FirstOrDefault(c => c is TextContent) as TextContent;
            return textContent?.Text?.Trim() ?? string.Empty;
        }

        public async Task<T?> GenerateStructuredResponseAsync<T>(string systemPrompt, string userPrompt, string jsonSchema, CancellationToken cancellationToken = default) where T : class
        {
            // Parse the provided JSON schema
            var schemaNode = JsonNode.Parse(jsonSchema);
            if (schemaNode == null)
            {
                throw new ArgumentException("Invalid JSON schema provided", nameof(jsonSchema));
            }

            // Use the schema directly as a tool input schema
            var toolName = $"Generate{typeof(T).Name}";
            var toolDescription = $"Generate structured data for {typeof(T).Name}";

            var tools = new List<Tool>
            {
                (Tool)new Function(toolName, toolDescription, schemaNode)
            };

            var messages = new List<Message>
            {
                new Message(RoleType.User, userPrompt)
            };

            var parameters = new MessageParameters
            {
                Messages = messages,
                Model = model,
                MaxTokens = 10000,
                System = new List<SystemMessage> { new SystemMessage(systemPrompt) },
                Tools = tools,
                // Force a tool call so we reliably get structured JSON back. There is exactly one
                // tool in the list, so "any" forces it — and unlike "tool", "any" must NOT carry a
                // name (the API rejects tool_choice.any.name). We deliberately do NOT enable
                // thinking: the Anthropic API forbids combining forced tool choice with thinking,
                // and newer models (e.g. Claude Opus 4.8) reject the legacy "thinking.type.enabled"
                // parameter outright (they use adaptive thinking + effort instead).
                ToolChoice = new ToolChoice
                {
                    Type = ToolChoiceType.Any
                }
            };

            ConfigureModelParameters(parameters);

            var result = await client.Messages.GetClaudeMessageAsync(parameters, cancellationToken);

            // Look for the tool use in the response
            var toolUse = result.Content?
                .OfType<ToolUseContent>()
                .FirstOrDefault(t => string.Equals(t.Name, toolName, StringComparison.Ordinal));

            if (toolUse != null)
            {
                var json = toolUse.Input.ToJsonString();
                return JsonSerializer.Deserialize<T>(json, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });
            }

            return null;
        }

        private bool IsClaude5 =>
            string.Equals(model, "claude-opus-5", StringComparison.OrdinalIgnoreCase)
            || string.Equals(model, "claude-sonnet-5", StringComparison.OrdinalIgnoreCase);

        private void ConfigureModelParameters(MessageParameters parameters)
        {
            if (IsClaude5)
            {
                // Claude 5 defaults to adaptive thinking. Low effort fits WallTrek's short
                // prompt/metadata tasks and keeps latency and token usage bounded.
                parameters.OutputConfig = new OutputConfig { Effort = ThinkingEffort.low };
                return;
            }

            // Claude Opus 4.7+ rejects non-default sampling parameters. Older models retain
            // WallTrek's established 0.7 temperature.
            if (!model.StartsWith("claude-opus-4-7", StringComparison.OrdinalIgnoreCase)
                && !model.StartsWith("claude-opus-4-8", StringComparison.OrdinalIgnoreCase))
            {
                parameters.Temperature = 0.7m;
            }
        }
    }
}
