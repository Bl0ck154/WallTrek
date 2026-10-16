using System.Reflection.Metadata;
using System.Text.Json;
using System.Text.Json.Serialization;
using WallTrek.Services.Providers;

namespace WallTrek.Services
{
    [JsonSerializable(typeof(SettingsModel))]
    internal partial class SettingsJsonContext : JsonSerializerContext
    {
    }

    public class AutoGenerateSettings
    {
        public bool Enabled { get; set; }
        public double Hours { get; set; } = 6.0;
        public DateTime? NextGenerateTime { get; set; }
        public string Source { get; set; } = "current";
    }

    public class DefaultRandomPrompts : Dictionary<string, string[]>
    {
        public DefaultRandomPrompts()
        {
            this["Category"] = new[]
            {
                "abstract geometric patterns and mathematical art",
                "natural landscapes and organic forms",
                "futuristic cyberpunk and sci-fi environments",
                "minimalist design and negative space",
                "fantasy worlds and magical creatures",
                "architectural marvels and urban scenes",
                "cosmic and astronomical phenomena",
                "vintage and retro aesthetic designs",
                "surreal and dreamlike compositions",
                "seasonal and weather-based imagery"
            };
            this["Style"] = new[]
            {
                "oil painting", "watercolor", "digital art", "photography", "3D render",
                "vector illustration", "pixel art", "paper cut-out", "neon lighting",
                "pencil sketch", "abstract expressionism", "art nouveau", "bauhaus design"
            };
            this["Mood"] = new[]
            {
                "serene and calming", "energetic and vibrant", "mysterious and moody",
                "bright and cheerful", "dramatic and intense", "peaceful and meditative",
                "bold and striking", "subtle and elegant", "warm and cozy", "cool and refreshing"
            };
        }
    }

    public class SettingsModel
    {
        public string? ApiKey { get; set; }
        public string? AnthropicApiKey { get; set; }
        public string? LastPrompt { get; set; }
        public AutoGenerateSettings AutoGenerate { get; set; } = new();
        public Dictionary<string, string[]> RandomPrompts { get; set; } = new();
        public bool MinimizeToTray { get; set; } = true;
        public bool IsFirstRun { get; set; } = true;
        public string? OutputDirectory { get; set; }
        public string? DeviantArtClientId { get; set; }
        public string? DeviantArtClientSecret { get; set; }
        public string? DeviantArtAccessToken { get; set; }
        public string? DeviantArtRefreshToken { get; set; }
        public DateTime? DeviantArtTokenExpiry { get; set; }
        public string SelectedLlmModel { get; set; } = "gpt-5.6-sol";
        public string SelectedImageModel { get; set; } = "gemini-3.1-flash-image@2K";
        public Guid? SelectedCustomImageProfileId { get; set; }
        public List<string> SelectedLlmModels { get; set; } = new();
        public List<string> SelectedImageModels { get; set; } = new();
        public string SelectedCategoryProfile { get; set; } = "Default";
        public string SelectedWordList { get; set; } = "Default";
        public List<string> SelectedCategoryProfiles { get; set; } = new();
        public List<string> SelectedWordLists { get; set; } = new();
        public List<int> RandomWordCounts { get; set; } = new();
        public bool IterativePromptGeneration { get; set; }
        public string SelectedUpscaler { get; set; } = "fast";
        public string? GoogleApiKey { get; set; }
        public string? StabilityApiKey { get; set; }
        public string? FalApiKey { get; set; }
        public int RandomWordCount { get; set; } = 0;
        // Opt-in only. No tokens/credentials are stored by WallTrek.
        public bool CodexExperimentalEnabled { get; set; }
        public string? CodexExecutablePath { get; set; }
        public string? CodexHelperPath { get; set; }
    }

    public class Settings
    {
        public static readonly int TitleCharacterMax = 50;
        public static readonly int MaxTags = 15;

        private static readonly string SettingsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "WallTrek",
            "settings.json");

        public static Settings Instance { get; private set; } = new Settings();

        private SettingsModel _model = new();

        public string? ApiKey
        {
            get => ReadSecret("openai", _model.ApiKey);
            set { StoreSecret("openai", value); _model.ApiKey = null; }
        }

        public string? LastPrompt
        {
            get => _model.LastPrompt;
            set => _model.LastPrompt = value;
        }

        public bool AutoGenerateEnabled
        {
            get => _model.AutoGenerate.Enabled;
            set => _model.AutoGenerate.Enabled = value;
        }

        public double AutoGenerateHours
        {
            get => _model.AutoGenerate.Hours;
            set => _model.AutoGenerate.Hours = value;
        }

        public DateTime? NextAutoGenerateTime
        {
            get => _model.AutoGenerate.NextGenerateTime;
            set => _model.AutoGenerate.NextGenerateTime = value;
        }

        public bool MinimizeToTray
        {
            get => _model.MinimizeToTray;
            set => _model.MinimizeToTray = value;
        }

        public bool IsFirstRun
        {
            get => _model.IsFirstRun;
            set => _model.IsFirstRun = value;
        }

        public string OutputDirectory
        {
            get => _model.OutputDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "WallTrek");
            set => _model.OutputDirectory = value;
        }

        public string AutoGenerateSource
        {
            get => _model.AutoGenerate.Source;
            set => _model.AutoGenerate.Source = value;
        }

        public Dictionary<string, string[]> RandomPrompts
        {
            get => _model.RandomPrompts;
            set => _model.RandomPrompts = value;
        }

        public string? DeviantArtClientId
        {
            get => _model.DeviantArtClientId;
            set => _model.DeviantArtClientId = value;
        }

        public string? DeviantArtClientSecret
        {
            get => ReadSecret("deviantart-client", _model.DeviantArtClientSecret);
            set { StoreSecret("deviantart-client", value); _model.DeviantArtClientSecret = null; }
        }

        public string? DeviantArtAccessToken
        {
            get => ReadSecret("deviantart-access", _model.DeviantArtAccessToken);
            set { StoreSecret("deviantart-access", value); _model.DeviantArtAccessToken = null; }
        }

        public string? DeviantArtRefreshToken
        {
            get => ReadSecret("deviantart-refresh", _model.DeviantArtRefreshToken);
            set { StoreSecret("deviantart-refresh", value); _model.DeviantArtRefreshToken = null; }
        }

        public DateTime? DeviantArtTokenExpiry
        {
            get => _model.DeviantArtTokenExpiry;
            set => _model.DeviantArtTokenExpiry = value;
        }

        public Guid? SelectedCustomImageProfileId
        {
            get => _model.SelectedCustomImageProfileId;
            set => _model.SelectedCustomImageProfileId = value;
        }

        public string SelectedLlmModel
        {
            get => _model.SelectedLlmModel;
            set => _model.SelectedLlmModel = value;
        }

        public string? AnthropicApiKey
        {
            get => ReadSecret("anthropic", _model.AnthropicApiKey);
            set { StoreSecret("anthropic", value); _model.AnthropicApiKey = null; }
        }

        public string SelectedImageModel
        {
            get => _model.SelectedImageModel;
            set => _model.SelectedImageModel = value;
        }

        public List<string> SelectedLlmModels
        {
            get => _model.SelectedLlmModels;
            set => _model.SelectedLlmModels = value;
        }

        public List<string> SelectedImageModels
        {
            get => _model.SelectedImageModels;
            set => _model.SelectedImageModels = value;
        }

        // Name (file stem) of the active random-prompt category profile in the
        // categories folder. See WallTrek.Services.Profiles.CategoryProfileService.
        public string SelectedCategoryProfile
        {
            get => _model.SelectedCategoryProfile;
            set => _model.SelectedCategoryProfile = value;
        }

        // Name (file stem) of the active word list in the wordlists folder.
        // See WallTrek.Services.TextGen.RandomWordService.
        public string SelectedWordList
        {
            get => _model.SelectedWordList;
            set => _model.SelectedWordList = value;
        }

        // Checked sets from the main page's multi-select dropdowns. Each generation randomly
        // picks one entry from each; the legacy single settings above remain as fallbacks.
        public List<string> SelectedCategoryProfiles
        {
            get => _model.SelectedCategoryProfiles;
            set => _model.SelectedCategoryProfiles = value;
        }

        public List<string> SelectedWordLists
        {
            get => _model.SelectedWordLists;
            set => _model.SelectedWordLists = value;
        }

        public List<int> RandomWordCounts
        {
            get => _model.RandomWordCounts;
            set => _model.RandomWordCounts = value;
        }

        public bool IterativePromptGeneration
        {
            get => _model.IterativePromptGeneration;
            set => _model.IterativePromptGeneration = value;
        }

        public string SelectedUpscaler
        {
            get => _model.SelectedUpscaler;
            set => _model.SelectedUpscaler = value;
        }

        public string? GoogleApiKey
        {
            get => ReadSecret("google", _model.GoogleApiKey);
            set { StoreSecret("google", value); _model.GoogleApiKey = null; }
        }

        public string? StabilityApiKey
        {
            get => ReadSecret("stability", _model.StabilityApiKey);
            set { StoreSecret("stability", value); _model.StabilityApiKey = null; }
        }

        public string? FalApiKey
        {
            get => ReadSecret("fal", _model.FalApiKey);
            set { StoreSecret("fal", value); _model.FalApiKey = null; }
        }

        public int RandomWordCount
        {
            get => _model.RandomWordCount;
            set => _model.RandomWordCount = value;
        }

        // Secret references are stable per-user; values are never written to settings.json.
        private static readonly IProviderSecretStore Vault = new WindowsProviderSecretStore();
        private static string? ReadSecret(string id, string? legacyValue) =>
            Vault.Read("builtin:" + id) ?? legacyValue;
        private static void StoreSecret(string id, string? value)
        {
            if (string.IsNullOrEmpty(value)) Vault.Delete("builtin:" + id);
            else Vault.Write("builtin:" + id, value);
        }

        private bool MigrateLegacySecrets()
        {
            bool changed = false;
            // Write all secrets first; only clear legacy fields after every vault write succeeds.
            var legacy = new (string Id, string? Value)[]
            {
                ("openai", _model.ApiKey),
                ("anthropic", _model.AnthropicApiKey),
                ("google", _model.GoogleApiKey),
                ("stability", _model.StabilityApiKey),
                ("fal", _model.FalApiKey),
                ("deviantart-client", _model.DeviantArtClientSecret),
                ("deviantart-access", _model.DeviantArtAccessToken),
                ("deviantart-refresh", _model.DeviantArtRefreshToken)
            };
            foreach (var (id, value) in legacy)
                if (!string.IsNullOrEmpty(value)) { Vault.Write("builtin:" + id, value); changed = true; }
            if (!changed) return false;
            _model.ApiKey = null; _model.AnthropicApiKey = null;
            _model.GoogleApiKey = null; _model.StabilityApiKey = null;
            _model.FalApiKey = null; _model.DeviantArtClientSecret = null;
            _model.DeviantArtAccessToken = null; _model.DeviantArtRefreshToken = null;
            return true;
        }


        public bool CodexExperimentalEnabled
        {
            get => _model.CodexExperimentalEnabled;
            set => _model.CodexExperimentalEnabled = value;
        }

        public string? CodexExecutablePath
        {
            get => _model.CodexExecutablePath;
            set => _model.CodexExecutablePath = value;
        }

        public string? CodexHelperPath
        {
            get => _model.CodexHelperPath;
            set => _model.CodexHelperPath = value;
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
                var options = new JsonSerializerOptions
                {
                    WriteIndented = true,
                    TypeInfoResolver = SettingsJsonContext.Default
                };
                string jsonString = JsonSerializer.Serialize(_model, options);
                var temp = SettingsPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    File.WriteAllText(temp, jsonString);
                    File.Move(temp, SettingsPath, true);
                }
                finally { if (File.Exists(temp)) File.Delete(temp); }
            }
            catch
            {
                // Never swallow a failed security migration or credential/settings update.
                throw new IOException("WallTrek settings could not be persisted.");
            }
        }

        public void Load()
        {
            try
            {
                if (File.Exists(SettingsPath))
                {
                    string jsonString = File.ReadAllText(SettingsPath);
                    _model = JsonSerializer.Deserialize(jsonString, SettingsJsonContext.Default.SettingsModel) ?? new SettingsModel();
                    if (MigrateLegacySecrets()) Save();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error loading settings: {ex.Message}");
                _model = new SettingsModel();
            }
        }
    }
}
