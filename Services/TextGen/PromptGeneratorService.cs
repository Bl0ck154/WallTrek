using System;
using System.Threading;
using System.Threading.Tasks;
using WallTrek.Services;
using WallTrek.Services.Profiles;

namespace WallTrek.Services.TextGen
{
  public class PromptGeneratorService
  {
    // Persisted in the multi-selection list to distinguish an explicit None choice from an
    // empty legacy/uninitialized list, which still falls back to the active source.
    public const string NoSourceSelection = "";

    private const string ResultSchema = @"{
  ""type"": ""object"",
  ""properties"": {
    ""prompt"": {
      ""type"": ""string"",
      ""description"": ""The detailed wallpaper generation prompt (1-2 sentences)""
    },
    ""title"": {
      ""type"": ""string"",
      ""description"": ""A catchy, descriptive title for the wallpaper""
    },
    ""tags"": {
      ""type"": ""array"",
      ""items"": {
        ""type"": ""string""
      },
      ""description"": ""Array of 5-10 relevant tags for categorization""
    }
  },
  ""required"": [""prompt"", ""title"", ""tags""]
}";

    public async Task<PromptGenerationResult?> GenerateRandomPromptAsync(
      CancellationToken cancellationToken = default,
      Func<string>? llmModelSelector = null,
      Action<int, int, string>? iterationStarted = null)
    {
      var settings = Settings.Instance;
      var random = new Random();
      llmModelSelector ??= () => settings.SelectedLlmModel;

      // Each generation randomly picks one of the checked category sets / word lists /
      // word counts from the main page (falling back to the legacy single settings).
      var categorySelection = PickRandom(settings.SelectedCategoryProfiles, random);
      var randomPrompts = categorySelection == NoSourceSelection
          ? new Dictionary<string, string[]>()
          : CategoryProfileService.Load(categorySelection);

      var categoryProperties = new List<string>();
      foreach (var category in randomPrompts)
      {
        if (category.Value?.Length > 0)
        {
          var selectedValue = category.Value[random.Next(category.Value.Length)];
          categoryProperties.Add($"{category.Key}: {selectedValue}");
        }
      }

      // Sample the word count and word list once. Iterative mode then consumes these words
      // in order, using one fresh LLM selection per word.
      var wordCount = settings.RandomWordCounts.Count > 0
          ? settings.RandomWordCounts[random.Next(settings.RandomWordCounts.Count)]
          : settings.RandomWordCount;
      var randomWords = new List<string>();

      if (wordCount > 0)
      {
        var wordListSelection = PickRandom(settings.SelectedWordLists, random);
        if (wordListSelection != NoSourceSelection)
        {
          var randomWordService = new RandomWordService();
          randomWords.AddRange(await randomWordService.GetRandomWordsAsync(wordCount, cancellationToken, wordListSelection));
        }
      }

      PromptGenerationResult? result;
      if (settings.IterativePromptGeneration && randomWords.Count > 0)
      {
        result = await GenerateIterativelyAsync(
          randomWords, categoryProperties, llmModelSelector, iterationStarted, cancellationToken);
      }
      else
      {
        var selectedProperties = randomWords.Concat(categoryProperties).ToList();
        result = await GenerateOnceAsync(
          selectedProperties, llmModelSelector(), iterationStarted, cancellationToken);
      }

      if (result != null)
      {
        result.SelectedProperties = randomWords.Concat(categoryProperties).ToList();
      }

      return result;
    }

    private static async Task<PromptGenerationResult?> GenerateOnceAsync(
      List<string> selectedProperties,
      string modelId,
      Action<int, int, string>? iterationStarted,
      CancellationToken cancellationToken)
    {
      iterationStarted?.Invoke(1, 1, modelId);

      var hasSelections = selectedProperties.Count > 0;
      var selections = string.Join(", ", selectedProperties);
      var inspiration = hasSelections
          ? $"taking inspiration from these properties: {selections}. "
          : "from an original concept of your choice. ";

      var systemPrompt = $"You are a creative assistant that generates diverse desktop wallpaper descriptions. " +
        $"Create a unique wallpaper prompt, title, and tags {inspiration}" +
        $"Be creative and avoid generic descriptions. The prompt should include specific details about colors, composition, and visual elements. " +
        $"Keep the prompt concise (1-2 sentences). The title should be catchy and descriptive, less than {Settings.TitleCharacterMax} characters. Provide 5-10 relevant tags for categorization.";

      var userPrompt = hasSelections
          ? $"Generate a creative desktop wallpaper prompt incorporating these elements: {selections}. Make it visually striking and unique. Also provide a suitable title and relevant tags."
          : "Generate a creative desktop wallpaper prompt from an original concept of your choice. Make it visually striking and unique. Also provide a suitable title and relevant tags.";

      return await GenerateStructuredAsync(modelId, systemPrompt, userPrompt, cancellationToken);
    }

    private static async Task<PromptGenerationResult?> GenerateIterativelyAsync(
      List<string> words,
      List<string> categoryProperties,
      Func<string> llmModelSelector,
      Action<int, int, string>? iterationStarted,
      CancellationToken cancellationToken)
    {
      PromptGenerationResult? result = null;
      var categoryContext = string.Join(", ", categoryProperties);

      for (var index = 0; index < words.Count; index++)
      {
        cancellationToken.ThrowIfCancellationRequested();

        var modelId = llmModelSelector();
        iterationStarted?.Invoke(index + 1, words.Count, modelId);

        string systemPrompt;
        string userPrompt;
        if (result == null)
        {
          var categoryInstruction = categoryProperties.Count > 0
              ? $" Also draw inspiration from this category context: {categoryContext}."
              : string.Empty;
          systemPrompt = $"You iteratively develop concise desktop wallpaper prompts. Create the initial prompt, title, and tags around the first seed word.{categoryInstruction} " +
            $"Make the concept visually specific, creative, and suitable for a desktop wallpaper. Keep the prompt to 1-2 sentences, the title under {Settings.TitleCharacterMax} characters, and provide 5-10 tags.";
          userPrompt = $"First seed word: {words[index]}. Generate the initial wallpaper prompt, title, and tags.";
        }
        else
        {
          systemPrompt = $"You iteratively develop concise desktop wallpaper prompts. Revise the existing prompt to meaningfully incorporate the next seed word while preserving its strongest existing ideas. " +
            $"Return a complete replacement prompt, title, and tags—not commentary about the revision. Keep the prompt to 1-2 sentences, the title under {Settings.TitleCharacterMax} characters, and provide 5-10 tags.";
          userPrompt = $"Existing prompt:\n{result.Prompt}\n\nNext seed word: {words[index]}\n\nGenerate the revised wallpaper prompt, title, and tags.";
        }

        result = await GenerateStructuredAsync(modelId, systemPrompt, userPrompt, cancellationToken);
        if (result == null)
        {
          return null;
        }
      }

      return result;
    }

    private static async Task<PromptGenerationResult?> GenerateStructuredAsync(
      string modelId,
      string systemPrompt,
      string userPrompt,
      CancellationToken cancellationToken)
    {
      var settings = Settings.Instance;
      var llmService = LlmServiceFactory.CreateService(
        modelId,
        settings.ApiKey ?? string.Empty,
        settings.AnthropicApiKey ?? string.Empty,
        settings.GoogleApiKey ?? string.Empty);

      var result = await llmService.GenerateStructuredResponseAsync<PromptGenerationResult>(
        systemPrompt, userPrompt, ResultSchema, cancellationToken);
      if (result != null)
      {
        result.LlmModelId = modelId;
      }

      return result;
    }

    private static string? PickRandom(List<string> items, Random random) =>
        items.Count > 0 ? items[random.Next(items.Count)] : null;
  }
}
