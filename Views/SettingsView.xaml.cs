// SettingsView.xaml.cs
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Streams;
using WallTrek.Services.Providers;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using WallTrek.Services;
using WallTrek.Services.Profiles;
using WallTrek.Services.TextGen;
using WallTrek.Utilities;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace WallTrek.Views
{
    public sealed partial class SettingsView : UserControl
    {
        public event EventHandler? NavigateToMain;

        // The profile / word list whose contents are currently shown in each editor.
        private string? _currentCategoryProfile;
        private string? _currentWordList;

        // Guards the combo SelectionChanged handlers while we populate them in code.
        private bool _suppressProfileEvents;

        public SettingsView()
        {
            this.InitializeComponent();
            LoadSettingsToUI();
        }

        public void ClearStatus()
        {
            StatusTextBlock.Text = "";
        }

        private sealed record ProfileChoice(Guid Id, string Label);
        private Guid? _editingProviderId;
        private bool _loadingProviderProfile;

        private void RefreshProviderProfiles(Guid? select = null)
        {
            try
            {
                _loadingProviderProfile = true;
                var profiles = ProviderProfileStore.Default.List();
                ProviderProfileComboBox.ItemsSource = profiles
                    .Select(p => new ProfileChoice(p.Id, p.Name + " — " + p.ImageModel)).ToList();
                var match = profiles.FirstOrDefault(p => p.Id == select);
                if (match != null)
                {
                    ProviderProfileComboBox.SelectedIndex = profiles.ToList().FindIndex(p => p.Id == match.Id);
                    DisplayProfile(match);
                }
                else if (profiles.Count > 0)
                {
                    ProviderProfileComboBox.SelectedIndex = 0;
                    DisplayProfile(profiles[0]);
                }
                else DisplayProfile(new ProviderProfile());
            }
            catch (Exception)
            {
                ProviderStatusTextBlock.Text = "Provider configuration could not be loaded. Check providers.json; no data was overwritten.";
            }
            finally { _loadingProviderProfile = false; }
        }

        private void DisplayProfile(ProviderProfile profile)
        {
            _editingProviderId = profile.Id;
            ProviderNameBox.Text = profile.Name;
            ProviderBaseUrlBox.Text = profile.BaseUrl;
            ProviderImageModelBox.Text = profile.ImageModel;
            ProviderEditModelBox.Text = profile.EditModel;
            ProviderTextModelBox.Text = profile.TextModel;
            ProviderGeneratePathBox.Text = profile.GeneratePath;
            ProviderEditPathBox.Text = profile.EditPath;
            ProviderModelsPathBox.Text = profile.ModelsPath;
            ProviderLoopbackBox.IsChecked = profile.AllowLoopbackHttp;
            ProviderEnabledBox.IsChecked = profile.Enabled;
            ProviderEnableEditsBox.IsChecked = profile.EnableEdits;
            ProviderAuthCombo.SelectedIndex = (int)profile.AuthMode;
            ProviderEditShapeCombo.SelectedIndex = (int)profile.EditShape;
            ProviderKeyBox.Password = "";
            ProviderTestPreview.Source = null;
            ProviderStatusTextBlock.Text =
                $"Generation verified: {profile.GenerateVerified} ({profile.GenerateVerifiedAt?.ToString("u") ?? "never"}); " +
                $"edit verified: {profile.EditVerified} ({profile.EditVerifiedAt?.ToString("u") ?? "never"}).";
        }

        private ProviderProfile CaptureProfile()
        {
            var profile = new ProviderProfile
            {
                Id = _editingProviderId ?? Guid.NewGuid(),
                Name = ProviderNameBox.Text.Trim(),
                BaseUrl = ProviderBaseUrlBox.Text.Trim(),
                ImageModel = ProviderImageModelBox.Text.Trim(),
                EditModel = ProviderEditModelBox.Text.Trim(),
                TextModel = ProviderTextModelBox.Text.Trim(),
                GeneratePath = ProviderGeneratePathBox.Text.Trim(),
                EditPath = ProviderEditPathBox.Text.Trim(),
                ModelsPath = ProviderModelsPathBox.Text.Trim(),
                AllowLoopbackHttp = ProviderLoopbackBox.IsChecked == true,
                Enabled = ProviderEnabledBox.IsChecked == true,
                EnableEdits = ProviderEnableEditsBox.IsChecked == true,
                AuthMode = (ProviderAuthMode)Math.Max(0, ProviderAuthCombo.SelectedIndex),
                EditShape = (ProviderEditShape)Math.Max(0, ProviderEditShapeCombo.SelectedIndex)
            };
            ProviderProfileValidation.Validate(profile);
            return profile;
        }

        private void ProviderProfileComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loadingProviderProfile || ProviderProfileComboBox.SelectedItem is not ProfileChoice choice) return;
            var profile = ProviderProfileStore.Default.Find(choice.Id);
            if (profile != null) DisplayProfile(profile);
        }

        private void ProviderNew_Click(object sender, RoutedEventArgs e)
        {
            ProviderProfileComboBox.SelectedIndex = -1;
            DisplayProfile(new ProviderProfile());
        }

        private void ProviderDuplicate_Click(object sender, RoutedEventArgs e)
        {
            if (_editingProviderId is not Guid id) return;
            var copy = ProviderProfileStore.Default.Find(id);
            if (copy == null) return;
            copy.Id = Guid.NewGuid();
            copy.Name += " copy";
            copy.GenerateVerified = false; copy.EditVerified = false;
            copy.GenerateVerifiedAt = null; copy.EditVerifiedAt = null;
            ProviderProfileComboBox.SelectedIndex = -1;
            DisplayProfile(copy);
            ProviderStatusTextBlock.Text = "Enter this new profile's API key; credentials are not copied.";
        }

        private void ProviderSave_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var profile = CaptureProfile();
                ProviderProfileStore.Default.Save(profile, ProviderKeyBox.Password);
                RefreshProviderProfiles(profile.Id);
                ProviderStatusTextBlock.Text = "Provider saved. Choose it in the image-model dropdown to use it.";
            }
            catch (ProviderException ex) { ProviderStatusTextBlock.Text = ex.Message; }
            catch (Exception) { ProviderStatusTextBlock.Text = "Unable to save profile or encrypted credentials."; }
        }

        private async void ProviderDelete_Click(object sender, RoutedEventArgs e)
        {
            if (_editingProviderId is not Guid id || ProviderProfileStore.Default.Find(id) is null) return;
            var dialog = new ContentDialog
            {
                Title = "Delete provider?",
                Content = "Delete this profile and its saved API key? Existing image history remains untouched.",
                PrimaryButtonText = "Delete",
                CloseButtonText = "Cancel",
                XamlRoot = XamlRoot
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            try
            {
                ProviderProfileStore.Default.Delete(id);
                RefreshProviderProfiles();
                ProviderStatusTextBlock.Text = "Provider and its key deleted.";
            }
            catch (Exception) { ProviderStatusTextBlock.Text = "Unable to delete provider safely."; }
        }

        private bool TryGetSavedProvider(out ProviderProfile? profile)
        {
            profile = _editingProviderId is Guid id ? ProviderProfileStore.Default.Find(id) : null;
            if (profile == null) ProviderStatusTextBlock.Text = "Save the provider before testing it.";
            return profile != null;
        }

        private async void ProviderTestConnection_Click(object sender, RoutedEventArgs e)
        {
            if (!TryGetSavedProvider(out var profile)) return;
            ProviderStatusTextBlock.Text = "Checking configured models endpoint (no image generated)...";
            try
            {
                var provider = ProviderRegistry.Create(profile!.Id);
                var result = await provider.ProbeAsync();
                ProviderStatusTextBlock.Text = result.Status +
                    ". Generate and edit support are NOT established by this probe.";
            }
            catch (ProviderException ex) { ProviderStatusTextBlock.Text = ex.Message; }
            catch (Exception) { ProviderStatusTextBlock.Text = "Connection test failed. No remote diagnostic text was logged."; }
        }

        private async void ProviderTestGenerate_Click(object sender, RoutedEventArgs e) =>
            await RunProviderImageTestAsync(false);
        private async void ProviderTestEdit_Click(object sender, RoutedEventArgs e) =>
            await RunProviderImageTestAsync(true);

        private async Task RunProviderImageTestAsync(bool edit)
        {
            if (!TryGetSavedProvider(out var profile)) return;
            var origin = new Uri(profile!.BaseUrl).GetLeftPart(UriPartial.Authority);
            if (edit && !profile.EnableEdits)
            {
                ProviderStatusTextBlock.Text = "Enable the edit endpoint and save before testing edits.";
                return;
            }
            // Explicit consent for every test, especially uploading private reference images.
            var confirm = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = edit ? "Upload reference photo?" : "Start paid image test?",
                Content = edit
                    ? $"The selected reference photo will be sent to {origin} using your saved API key. Continue?"
                    : $"Test image generation at {origin}? Provider usage may be billed.",
                PrimaryButtonText = "Run test", CloseButtonText = "Cancel"
            };
            if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;
            ProviderStatusTextBlock.Text = "Testing image endpoint; this may consume provider credits...";
            try
            {
                ProviderImageReference[] refs = Array.Empty<ProviderImageReference>();
                if (edit)
                {
                    var path = ProviderReferencePathBox.Text.Trim();
                    if (!File.Exists(path)) { ProviderStatusTextBlock.Text = "Reference file does not exist."; return; }
                    var length = new FileInfo(path).Length;
                    if (length is < 16 or > 20 * 1024 * 1024)
                    { ProviderStatusTextBlock.Text = "Reference exceeds 20 MiB or is empty."; return; }
                    refs = new[] { new ProviderImageReference(await File.ReadAllBytesAsync(path),
                        Path.GetFileName(path), ProviderReferenceRole.Identity) };
                }
                var provider = new OpenAiCompatibleProvider(profile,
                    new WindowsProviderSecretStore());
                var result = edit
                    ? await provider.EditAsync(new ProviderImageRequest(ProviderTestPromptBox.Text, refs))
                    : await provider.GenerateAsync(new ProviderImageRequest(ProviderTestPromptBox.Text));
                using var stream = new InMemoryRandomAccessStream();
                using (var writer = new DataWriter(stream))
                {
                    writer.WriteBytes(result.ImageData);
                    await writer.StoreAsync();
                    writer.DetachStream();
                }
                stream.Seek(0);
                var bitmap = new BitmapImage();
                await bitmap.SetSourceAsync(stream);
                ProviderTestPreview.Source = bitmap;
                ProviderProfileStore.Default.RecordVerified(profile.Id, edit, true);
                ProviderStatusTextBlock.Text = $"Verified real {(edit ? "edit" : "generation")} " +
                    $"image: {result.Width}×{result.Height} ({result.MimeType}). " +
                    (edit ? "Inspect whether the person's identity was preserved." : "");
            }
            catch (ProviderException ex)
            {
                ProviderStatusTextBlock.Text = $"{ex.Reason}: {ex.Message}" +
                    (ex.RetryAfter is null ? "" : $" Retry after {ex.RetryAfter.Value.TotalSeconds:0} seconds.");
            }
            catch (Exception) { ProviderStatusTextBlock.Text = "Test failed. Check provider configuration; no private error details are shown."; }
        }

        private void LoadSettingsToUI()
        {
            var settings = Settings.Instance;
            ApiKeyTextBox.Text = settings.ApiKey ?? string.Empty;
            AnthropicApiKeyTextBox.Text = settings.AnthropicApiKey ?? string.Empty;
            GoogleApiKeyTextBox.Text = settings.GoogleApiKey ?? string.Empty;
            StabilityApiKeyTextBox.Text = settings.StabilityApiKey ?? string.Empty;
            FalApiKeyTextBox.Text = settings.FalApiKey ?? string.Empty;
            DeviantArtClientIdTextBox.Text = settings.DeviantArtClientId ?? string.Empty;
            DeviantArtClientSecretPasswordBox.Password = settings.DeviantArtClientSecret ?? string.Empty;
            OutputDirectoryTextBox.Text = settings.OutputDirectory;
            AutoGenerateCheckBox.IsChecked = settings.AutoGenerateEnabled;
            AutoGenerateHoursNumberBox.Value = settings.AutoGenerateHours > 0 ? settings.AutoGenerateHours : 6.0;
            MinimizeToTrayCheckBox.IsChecked = settings.MinimizeToTray;

            // Set auto-generate source dropdown
            foreach (ComboBoxItem item in AutoGenerateSourceComboBox.Items)
            {
                if (item.Tag?.ToString() == settings.AutoGenerateSource)
                {
                    AutoGenerateSourceComboBox.SelectedItem = item;
                    break;
                }
            }

            // Default to first item if nothing was selected
            if (AutoGenerateSourceComboBox.SelectedItem == null)
            {
                AutoGenerateSourceComboBox.SelectedIndex = 0;
            }

            // Show/hide auto-generate options based on checkbox state
            UpdateAutoGenerateOptionsVisibility();

            // Load startup setting from both settings and registry to ensure sync
            RunOnStartupCheckBox.IsChecked = StartupManager.IsStartupEnabled();

            // Load the file-based category profiles and word lists into their dropdowns/editors.
            PopulateCategoryProfiles();
            PopulateWordLists();
            RefreshProviderProfiles();
        }

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            // Flush the open category editor to its file (must be valid JSON).
            if (_currentCategoryProfile != null)
            {
                if (!CategoryProfileService.TryValidate(JsonTextBox.Text, out string catError))
                {
                    ShowStatus(catError, Microsoft.UI.Colors.Red);
                    return;
                }
                CategoryProfileService.WriteText(_currentCategoryProfile, JsonTextBox.Text);
                Settings.Instance.SelectedCategoryProfile = _currentCategoryProfile;
            }

            // Flush the open word-list editor to its file (any text is allowed).
            if (_currentWordList != null)
            {
                RandomWordService.WriteText(_currentWordList, WordListTextBox.Text);
                Settings.Instance.SelectedWordList = _currentWordList;
            }

            var settings = Settings.Instance;
            var previousHours = settings.AutoGenerateHours;

            settings.ApiKey = ApiKeyTextBox.Text;
            settings.AnthropicApiKey = AnthropicApiKeyTextBox.Text;
            settings.GoogleApiKey = GoogleApiKeyTextBox.Text;
            settings.StabilityApiKey = StabilityApiKeyTextBox.Text;
            settings.FalApiKey = FalApiKeyTextBox.Text;
            settings.DeviantArtClientId = DeviantArtClientIdTextBox.Text;
            settings.DeviantArtClientSecret = DeviantArtClientSecretPasswordBox.Password;
            settings.OutputDirectory = OutputDirectoryTextBox.Text;
            settings.AutoGenerateEnabled = AutoGenerateCheckBox.IsChecked ?? false;
            settings.AutoGenerateHours = AutoGenerateHoursNumberBox.Value;
            settings.MinimizeToTray = MinimizeToTrayCheckBox.IsChecked ?? true;

            // Save auto-generate source
            if (AutoGenerateSourceComboBox.SelectedItem is ComboBoxItem selectedItem)
            {
                settings.AutoGenerateSource = selectedItem.Tag?.ToString() ?? "current";
            }

            // Random word count is set on the main page now (0 = off), not here.

            // Handle startup setting - only update registry, no need to store in settings
            var runOnStartup = RunOnStartupCheckBox.IsChecked ?? false;
            StartupManager.SetStartupEnabled(runOnStartup);

            settings.Save();

            // Refresh auto-generate service based on new settings
            if (settings.AutoGenerateEnabled && settings.AutoGenerateHours > 0 &&
                Math.Abs(previousHours - settings.AutoGenerateHours) > 0.001)
            {
                AutoGenerateService.Instance.Start(settings.AutoGenerateHours);
            }
            else
            {
                AutoGenerateService.Instance.RefreshFromSettings();
            }

            ShowStatus("Settings saved successfully!", Microsoft.UI.Colors.LimeGreen);

            // Navigate back to main view after saving
            NavigateToMain?.Invoke(this, EventArgs.Empty);
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            // Reload settings to discard unsaved editor changes
            LoadSettingsToUI();
            StatusTextBlock.Text = "";

            // Navigate back to main view
            NavigateToMain?.Invoke(this, EventArgs.Empty);
        }

        private async void BrowseButton_Click(object sender, RoutedEventArgs e)
        {
            var picker = new FolderPicker();
            picker.SuggestedStartLocation = PickerLocationId.PicturesLibrary;
            picker.FileTypeFilter.Add("*");

            // Get the current window handle for the picker
            var window = ((App)Application.Current).GetMainWindow();
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

            var folder = await picker.PickSingleFolderAsync();
            if (folder != null)
            {
                OutputDirectoryTextBox.Text = folder.Path;
            }
        }

        private void AutoGenerateCheckBox_CheckChanged(object sender, RoutedEventArgs e)
        {
            UpdateAutoGenerateOptionsVisibility();
        }

        private void UpdateAutoGenerateOptionsVisibility()
        {
            AutoGenerateOptionsPanel.Visibility = AutoGenerateCheckBox.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        }

        private void ClearTokensButton_Click(object sender, RoutedEventArgs e)
        {
            var settings = Settings.Instance;
            settings.DeviantArtAccessToken = null;
            settings.DeviantArtRefreshToken = null;
            settings.DeviantArtTokenExpiry = null;
            settings.Save();

            StatusTextBlock.Text = "DeviantArt tokens cleared successfully!";
        }

        // ---- Category profiles ------------------------------------------------

        private void PopulateCategoryProfiles()
        {
            _suppressProfileEvents = true;
            var profiles = CategoryProfileService.ListProfiles().ToList();
            CategoryProfileComboBox.ItemsSource = profiles;

            var active = profiles.FirstOrDefault(p =>
                            string.Equals(p, Settings.Instance.SelectedCategoryProfile, StringComparison.OrdinalIgnoreCase))
                        ?? profiles.FirstOrDefault();
            CategoryProfileComboBox.SelectedItem = active;
            _suppressProfileEvents = false;

            _currentCategoryProfile = active;
            JsonTextBox.Text = active != null ? CategoryProfileService.LoadText(active) : string.Empty;
        }

        private void CategoryProfileComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressProfileEvents) return;
            if (CategoryProfileComboBox.SelectedItem is not string target) return;
            if (string.Equals(target, _currentCategoryProfile, StringComparison.Ordinal)) return;

            // Auto-save the editor to the previously selected profile (must be valid JSON).
            if (_currentCategoryProfile != null)
            {
                if (!CategoryProfileService.TryValidate(JsonTextBox.Text, out var err))
                {
                    ShowStatus($"Can't switch — fix the JSON first: {err}", Microsoft.UI.Colors.Red);
                    _suppressProfileEvents = true;
                    CategoryProfileComboBox.SelectedItem = _currentCategoryProfile;
                    _suppressProfileEvents = false;
                    return;
                }
                CategoryProfileService.WriteText(_currentCategoryProfile, JsonTextBox.Text);
            }

            _currentCategoryProfile = target;
            JsonTextBox.Text = CategoryProfileService.LoadText(target);
            Settings.Instance.SelectedCategoryProfile = target;
            Settings.Instance.Save();
        }

        private async void NewCategoryProfile_Click(object sender, RoutedEventArgs e)
        {
            var input = await DialogHelper.ShowInputAsync(XamlRoot, "New Category Profile", "Profile name");
            if (input == null) return;

            var name = CategoryProfileService.SanitizeName(input);
            if (name.Length == 0) { ShowStatus("Please enter a valid name.", Microsoft.UI.Colors.Red); return; }
            if (CategoryProfileService.Exists(name)) { ShowStatus($"A profile named '{name}' already exists.", Microsoft.UI.Colors.Red); return; }

            // Preserve any valid edits to the current profile before switching away.
            if (_currentCategoryProfile != null && CategoryProfileService.TryValidate(JsonTextBox.Text, out _))
                CategoryProfileService.WriteText(_currentCategoryProfile, JsonTextBox.Text);

            try { CategoryProfileService.Create(name); }
            catch (Exception ex) { ShowStatus(ex.Message, Microsoft.UI.Colors.Red); return; }

            Settings.Instance.SelectedCategoryProfile = name;
            Settings.Instance.Save();
            PopulateCategoryProfiles();
            ShowStatus($"Created profile '{name}'.", Microsoft.UI.Colors.LimeGreen);
        }

        private async void RenameCategoryProfile_Click(object sender, RoutedEventArgs e)
        {
            if (_currentCategoryProfile == null) return;

            var input = await DialogHelper.ShowInputAsync(XamlRoot, "Rename Category Profile", "New name", _currentCategoryProfile);
            if (input == null) return;

            var name = CategoryProfileService.SanitizeName(input);
            if (name.Length == 0) { ShowStatus("Please enter a valid name.", Microsoft.UI.Colors.Red); return; }
            if (string.Equals(name, _currentCategoryProfile, StringComparison.OrdinalIgnoreCase)) return;
            if (CategoryProfileService.Exists(name)) { ShowStatus($"A profile named '{name}' already exists.", Microsoft.UI.Colors.Red); return; }

            // Persist the open editor into the existing file before moving it.
            if (!CategoryProfileService.TryValidate(JsonTextBox.Text, out var err))
            {
                ShowStatus($"Fix the JSON before renaming: {err}", Microsoft.UI.Colors.Red);
                return;
            }
            CategoryProfileService.WriteText(_currentCategoryProfile, JsonTextBox.Text);

            try { CategoryProfileService.Rename(_currentCategoryProfile, name); }
            catch (Exception ex) { ShowStatus(ex.Message, Microsoft.UI.Colors.Red); return; }

            Settings.Instance.SelectedCategoryProfile = name;
            Settings.Instance.Save();
            PopulateCategoryProfiles();
            ShowStatus($"Renamed to '{name}'.", Microsoft.UI.Colors.LimeGreen);
        }

        private async void DeleteCategoryProfile_Click(object sender, RoutedEventArgs e)
        {
            if (_currentCategoryProfile == null) return;
            if (CategoryProfileService.ListProfiles().Count <= 1)
            {
                ShowStatus("At least one category profile is required.", Microsoft.UI.Colors.Red);
                return;
            }

            var confirmed = await DialogHelper.ShowConfirmationAsync(XamlRoot, "Delete Profile",
                $"Delete category profile '{_currentCategoryProfile}'? This cannot be undone.",
                "Delete", "Cancel");
            if (!confirmed) return;

            var deleted = _currentCategoryProfile;
            CategoryProfileService.Delete(deleted);
            Settings.Instance.SelectedCategoryProfile =
                CategoryProfileService.ListProfiles().FirstOrDefault() ?? "Default";
            Settings.Instance.Save();
            PopulateCategoryProfiles();
            ShowStatus($"Deleted profile '{deleted}'.", Microsoft.UI.Colors.LimeGreen);
        }

        private void RestorePromptDefaultsButton_Click(object sender, RoutedEventArgs e)
        {
            var options = new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            };

            JsonTextBox.Text = JsonSerializer.Serialize(new DefaultRandomPrompts(), options);

            ShowStatus("Default categories loaded into the editor. Click 'Save Settings' to apply.", Microsoft.UI.Colors.LimeGreen);
        }

        private async void GenerateCategoryProfile_Click(object sender, RoutedEventArgs e)
        {
            var request = await DialogHelper.ShowInputAsync(XamlRoot,
                "Generate Category Set",
                "Describe the theme, e.g. 'deep-sea bioluminescent creatures in moody painterly styles'",
                acceptsReturn: true);
            if (string.IsNullOrWhiteSpace(request)) return;

            var button = (Button)sender;
            button.IsEnabled = false;
            ShowStatus("Generating categories…", Microsoft.UI.Colors.Gray);
            try
            {
                var json = await new ProfileContentGenerator()
                    .GenerateCategoryJsonAsync(CategoryLlmSelector.SelectedModelId, request);
                JsonTextBox.Text = json;
                ShowStatus("Generated categories loaded into the editor. Click 'Save Settings' to apply.", Microsoft.UI.Colors.LimeGreen);
            }
            catch (Exception ex)
            {
                ShowStatus($"Generation failed: {ex.Message}", Microsoft.UI.Colors.Red);
            }
            finally
            {
                button.IsEnabled = true;
            }
        }

        // ---- Word lists -------------------------------------------------------

        private void PopulateWordLists()
        {
            _suppressProfileEvents = true;
            var lists = RandomWordService.ListWordLists().ToList();
            WordListComboBox.ItemsSource = lists;

            var active = lists.FirstOrDefault(l =>
                            string.Equals(l, Settings.Instance.SelectedWordList, StringComparison.OrdinalIgnoreCase))
                        ?? lists.FirstOrDefault();
            WordListComboBox.SelectedItem = active;
            _suppressProfileEvents = false;

            _currentWordList = active;
            WordListTextBox.Text = active != null ? RandomWordService.LoadText(active) : string.Empty;
        }

        private void WordListComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressProfileEvents) return;
            if (WordListComboBox.SelectedItem is not string target) return;
            if (string.Equals(target, _currentWordList, StringComparison.Ordinal)) return;

            // Auto-save the editor to the previously selected list.
            if (_currentWordList != null)
                RandomWordService.WriteText(_currentWordList, WordListTextBox.Text);

            _currentWordList = target;
            WordListTextBox.Text = RandomWordService.LoadText(target);
            Settings.Instance.SelectedWordList = target;
            Settings.Instance.Save();
        }

        private async void NewWordList_Click(object sender, RoutedEventArgs e)
        {
            var input = await DialogHelper.ShowInputAsync(XamlRoot, "New Word List", "Word list name");
            if (input == null) return;

            var name = RandomWordService.SanitizeName(input);
            if (name.Length == 0) { ShowStatus("Please enter a valid name.", Microsoft.UI.Colors.Red); return; }
            if (RandomWordService.Exists(name)) { ShowStatus($"A word list named '{name}' already exists.", Microsoft.UI.Colors.Red); return; }

            // Preserve edits to the current list before switching away.
            if (_currentWordList != null)
                RandomWordService.WriteText(_currentWordList, WordListTextBox.Text);

            try { RandomWordService.Create(name); }
            catch (Exception ex) { ShowStatus(ex.Message, Microsoft.UI.Colors.Red); return; }

            Settings.Instance.SelectedWordList = name;
            Settings.Instance.Save();
            PopulateWordLists();
            ShowStatus($"Created word list '{name}'.", Microsoft.UI.Colors.LimeGreen);
        }

        private async void RenameWordList_Click(object sender, RoutedEventArgs e)
        {
            if (_currentWordList == null) return;

            var input = await DialogHelper.ShowInputAsync(XamlRoot, "Rename Word List", "New name", _currentWordList);
            if (input == null) return;

            var name = RandomWordService.SanitizeName(input);
            if (name.Length == 0) { ShowStatus("Please enter a valid name.", Microsoft.UI.Colors.Red); return; }
            if (string.Equals(name, _currentWordList, StringComparison.OrdinalIgnoreCase)) return;
            if (RandomWordService.Exists(name)) { ShowStatus($"A word list named '{name}' already exists.", Microsoft.UI.Colors.Red); return; }

            RandomWordService.WriteText(_currentWordList, WordListTextBox.Text);

            try { RandomWordService.Rename(_currentWordList, name); }
            catch (Exception ex) { ShowStatus(ex.Message, Microsoft.UI.Colors.Red); return; }

            Settings.Instance.SelectedWordList = name;
            Settings.Instance.Save();
            PopulateWordLists();
            ShowStatus($"Renamed to '{name}'.", Microsoft.UI.Colors.LimeGreen);
        }

        private async void DeleteWordList_Click(object sender, RoutedEventArgs e)
        {
            if (_currentWordList == null) return;
            if (RandomWordService.ListWordLists().Count <= 1)
            {
                ShowStatus("At least one word list is required.", Microsoft.UI.Colors.Red);
                return;
            }

            var confirmed = await DialogHelper.ShowConfirmationAsync(XamlRoot, "Delete Word List",
                $"Delete word list '{_currentWordList}'? This cannot be undone.",
                "Delete", "Cancel");
            if (!confirmed) return;

            var deleted = _currentWordList;
            RandomWordService.Delete(deleted);
            Settings.Instance.SelectedWordList =
                RandomWordService.ListWordLists().FirstOrDefault() ?? "Default";
            Settings.Instance.Save();
            PopulateWordLists();
            ShowStatus($"Deleted word list '{deleted}'.", Microsoft.UI.Colors.LimeGreen);
        }

        private void RestoreWordsDefaultsButton_Click(object sender, RoutedEventArgs e)
        {
            WordListTextBox.Text = RandomWordService.GetDefaultWordListText();

            ShowStatus("Default word list loaded into the editor. Click 'Save Settings' to apply.", Microsoft.UI.Colors.LimeGreen);
        }

        private async void GenerateWordList_Click(object sender, RoutedEventArgs e)
        {
            var request = await DialogHelper.ShowInputAsync(XamlRoot,
                "Generate Word List",
                "Describe the theme, e.g. 'cozy autumn cabin moods and textures'",
                acceptsReturn: true);
            if (string.IsNullOrWhiteSpace(request)) return;

            var button = (Button)sender;
            button.IsEnabled = false;
            ShowStatus("Generating word list…", Microsoft.UI.Colors.Gray);
            try
            {
                var words = await new ProfileContentGenerator()
                    .GenerateWordListAsync(WordListLlmSelector.SelectedModelId, request);
                WordListTextBox.Text = words;
                ShowStatus("Generated word list loaded into the editor. Click 'Save Settings' to apply.", Microsoft.UI.Colors.LimeGreen);
            }
            catch (Exception ex)
            {
                ShowStatus($"Generation failed: {ex.Message}", Microsoft.UI.Colors.Red);
            }
            finally
            {
                button.IsEnabled = true;
            }
        }

        private void OpenDataFolderButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var appDataFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WallTrek");

                // Create the directory if it doesn't exist
                if (!Directory.Exists(appDataFolder))
                {
                    Directory.CreateDirectory(appDataFolder);
                }

                // Open the folder in Windows Explorer
                Process.Start(new ProcessStartInfo
                {
                    FileName = appDataFolder,
                    UseShellExecute = true,
                    Verb = "open"
                });
            }
            catch (Exception ex)
            {
                ShowStatus($"Error opening folder: {ex.Message}", Microsoft.UI.Colors.Red);
            }
        }

        private void ShowStatus(string message, Windows.UI.Color color)
        {
            StatusTextBlock.Text = message;
            StatusTextBlock.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(color);
        }
    }
}
