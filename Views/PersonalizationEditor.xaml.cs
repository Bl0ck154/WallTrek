using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Text;
using WallTrek.Services.Personalization;
using WallTrek.Services.ImageGen.Codex;
using Windows.Storage.Streams;
using WallTrek.Services.Providers;
using Windows.Storage.Pickers;

namespace WallTrek.Views;

public sealed partial class PersonalizationEditor : UserControl
{
    private readonly PersonalizationLibrary _library = PersonalizationLibrary.Default;
    private Guid _currentScenarioId = Guid.NewGuid();
    private bool _loading;
    private sealed record ProviderChoice(Guid Id, string Label);
    public PersonalizationEditor()
    {
        InitializeComponent();
        RefreshAll();
    }

    private void RefreshAll()
    {
        var state = _library.Snapshot();
        _loading = true;
        PhotoList.ItemsSource = state.Photos;
        ScenarioList.ItemsSource = state.Scenarios;
        var destinations = ProviderProfileStore.Default.List()
            .Where(p => p.Enabled && p.EnableEdits)
            .Select(p => new ProviderChoice(p.Id, p.Name + " → " +
                new Uri(p.BaseUrl).GetLeftPart(UriPartial.Authority))).ToList();
        if (WallTrek.Services.Settings.Instance.CodexExperimentalEnabled)
            destinations.Add(new ProviderChoice(CodexPersonalConsent.ProviderId,
                "Codex experimental → " + CodexPersonalConsent.Origin));
        DestinationProfiles.ItemsSource = destinations;
        var active = state.Scenarios.FirstOrDefault(s => s.Id == state.ActiveScenarioId);
        if (active != null)
        {
            var index = state.Scenarios.FindIndex(s => s.Id == active.Id);
            ScenarioList.SelectedIndex = index;
            SetScenario(active);
        }
        else NewScenarioFields();
        ScenarioActiveLabel.Text = active is null
            ? "Personalization is OFF. Existing wallpaper generation is unchanged."
            : "Active: " + active.Name + ". The next wallpaper uses this scenario.";
        _loading = false;
        _ = LoadHistoryAsync();
    }

    private void SetScenario(WallpaperScenario scenario)
    {
        _currentScenarioId = scenario.Id;
        ScenarioName.Text = scenario.Name;
        ScenarioTemplate.Text = scenario.Template;
        ScenarioStyle.Text = scenario.Style;
        ScenarioAvoid.Text = scenario.Avoid;
        ScenarioPools.Text = string.Join(Environment.NewLine,
            scenario.Pools.Select(p => p.Key + " = " + string.Join(" | ", p.Value)));
        FinalPrompt.Text = "";
    }

    private void NewScenarioFields()
    {
        SetScenario(new WallpaperScenario
        {
            Pools = new Dictionary<string, List<string>>(StringComparer.Ordinal)
            {
                ["place"] = new() { "Prague", "Vienna", "Lisbon", "Rome", "Tokyo", "Oslo", "Paris" }
            }
        });
    }

    private void ScenarioList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loading && ScenarioList.SelectedItem is WallpaperScenario scene)
            SetScenario(scene);
    }
    private void NewScenario_Click(object sender, RoutedEventArgs e)
    {
        ScenarioList.SelectedIndex = -1;
        NewScenarioFields();
        EditorStatus.Text = "New scenario. Save and activate it when ready.";
    }

    private static Dictionary<string, List<string>> ParsePools(string text)
    {
        var pools = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var raw in text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            var separator = line.IndexOf('=');
            if (separator < 1) throw new InvalidDataException("Variables must use: name = value | value");
            var key = line[..separator].Trim();
            if (pools.ContainsKey(key)) throw new InvalidDataException("Variable names must be unique.");
            pools.Add(key, line[(separator + 1)..].Split('|')
                .Select(s => s.Trim()).Where(s => s.Length > 0).ToList());
        }
        return pools;
    }

    private void SaveScenario_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var scene = new WallpaperScenario
            {
                Id = _currentScenarioId, Name = ScenarioName.Text.Trim(),
                Template = ScenarioTemplate.Text.Trim(), Style = ScenarioStyle.Text.Trim(),
                Avoid = ScenarioAvoid.Text.Trim(), Pools = ParsePools(ScenarioPools.Text)
            };
            // Retain successful variation memory on unchanged scenarios.
            var previous = _library.Snapshot().Scenarios.FirstOrDefault(s => s.Id == scene.Id);
            if (previous != null)
            {
                scene.UsedCombinationKeys = previous.UsedCombinationKeys;
                scene.LastCombinationKey = previous.LastCombinationKey;
                scene.Cycle = previous.Cycle;
            }
            _library.SaveScenario(scene, true);
            RefreshAll();
            EditorStatus.Text = "Scenario saved and activated. Preview it before generating.";
        }
        catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException
            or ArgumentException or OverflowException)
        { EditorStatus.Text = ex.Message; }
    }
    private void DisableScenario_Click(object sender, RoutedEventArgs e)
    {
        _library.ActivateScenario(null);
        RefreshAll();
        EditorStatus.Text = "Personalization off. Original wallpaper workflow restored.";
    }

    private void DeleteScenario_Click(object sender, RoutedEventArgs e)
    {
        if (_library.Snapshot().Scenarios.All(s => s.Id != _currentScenarioId)) return;
        if (ConfirmDeleteScenario.IsChecked != true)
        {
            EditorStatus.Text = "Check the deletion confirmation box first.";
            return;
        }
        _library.DeleteScenario(_currentScenarioId);
        ConfirmDeleteScenario.IsChecked = false;
        RefreshAll();
    }

    private async void ImportPhotos_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        picker.FileTypeFilter.Add(".jpg");
        picker.FileTypeFilter.Add(".jpeg");
        picker.FileTypeFilter.Add(".png");
        picker.SuggestedStartLocation = PickerLocationId.PicturesLibrary;
        var window = ((App)Application.Current).GetMainWindow();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(window));
        var selected = await picker.PickMultipleFilesAsync();
        if (selected is null || selected.Count == 0) return;
        if (selected.Count > 5)
        {
            EditorStatus.Text = "Import at most five files at a time.";
            return;
        }
        var imported = 0;
        foreach (var file in selected)
        {
            try { _library.Import(file.Path, ReferenceRole.Identity); imported++; }
            catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException
                or System.Runtime.InteropServices.ExternalException)
            { EditorStatus.Text = $"Imported {imported}; another file was rejected: {ex.Message}"; break; }
        }
        RefreshAll();
        if (imported == selected.Count) EditorStatus.Text = $"Imported {imported} photos. Select at most five to use.";
    }

    private void PhotoList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || PhotoList.SelectedItem is not ReferencePhoto photo) return;
        PhotoLabel.Text = photo.Label;
        PhotoRole.SelectedIndex = (int)photo.Role;
        PhotoEnabled.IsChecked = photo.Enabled;
        PhotoPrimary.IsChecked = photo.Primary;
        var uri = new Uri(_library.ImagePreviewPath(photo.Id), UriKind.Absolute);
        PhotoPreview.Source = new BitmapImage(uri);
    }
    private void SavePhoto_Click(object sender, RoutedEventArgs e)
    {
        if (PhotoList.SelectedItem is not ReferencePhoto photo) return;
        try
        {
            _library.UpdatePhoto(photo.Id, PhotoLabel.Text,
                (ReferenceRole)Math.Clamp(PhotoRole.SelectedIndex, 0, 2),
                PhotoEnabled.IsChecked == true, PhotoPrimary.IsChecked == true);
            RefreshAll();
            EditorStatus.Text = "Photo settings saved; previous upload approval cleared.";
        }
        catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException)
        { EditorStatus.Text = ex.Message; }
    }

    private void DeletePhoto_Click(object sender, RoutedEventArgs e)
    {
        if (PhotoList.SelectedItem is not ReferencePhoto photo) return;
        if (ConfirmDeletePhoto.IsChecked != true)
        {
            EditorStatus.Text = "Check the photo deletion confirmation box first.";
            return;
        }
        _library.DeletePhoto(photo.Id);
        ConfirmDeletePhoto.IsChecked = false;
        PhotoPreview.Source = null;
        RefreshAll();
        EditorStatus.Text = "Photo removed from library.";
    }

    private void PreviewNext_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var prepared = _library.Prepare();
            if (prepared is null)
            {
                EditorStatus.Text = "Save and activate a scenario first.";
                return;
            }
            FinalPrompt.Text = prepared.FinalPrompt;
            VariationLabel.Text = prepared.Variables.Count == 0
                ? "Fixed prompt · " + prepared.ReferenceIds.Count + " selected photos"
                : string.Join(" · ", prepared.Variables.Select(p => p.Key + ": " + p.Value)) +
                    " · " + prepared.ReferenceIds.Count + " selected photos";
            EditorStatus.Text = "Preview is stable until a wallpaper is successfully generated.";
        }
        catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException)
        { EditorStatus.Text = ex.Message; }
    }

    private void DestinationProfiles_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ConfirmUpload is not null) ConfirmUpload.IsChecked = false;
    }

    private void ApproveDestination_Click(object sender, RoutedEventArgs e)
    {
        if (DestinationProfiles.SelectedItem is not ProviderChoice item)
        {
            EditorStatus.Text = "Select the destination provider first.";
            return;
        }
        var prepared = _library.Prepare();
        if (prepared is null || prepared.ReferenceIds.Count == 0)
        {
            EditorStatus.Text = "Activate a scenario and select 1–5 enabled reference photos.";
            return;
        }
        if (item.Id == CodexPersonalConsent.ProviderId)
        {
            if (!WallTrek.Services.Settings.Instance.CodexExperimentalEnabled)
            {
                EditorStatus.Text = "First enable Codex experimental mode in Settings.";
                return;
            }
            var selected = _library.Snapshot().Photos.Where(p => prepared.ReferenceIds.Contains(p.Id)).ToArray();
            var styleCount = selected.Count(p => p.Role == ReferenceRole.Style);
            if (styleCount > 1 || styleCount == selected.Length)
            {
                EditorStatus.Text = "Codex requires 1–5 photos, at most one Style, and at least one Identity/Content photo.";
                return;
            }
            if (ConfirmUpload.IsChecked != true)
            {
                EditorStatus.Text = "Review the experimental helper privacy notice and explicitly consent to ChatGPT/Codex photo upload.";
                return;
            }
            _library.ApproveReferences(prepared.ScenarioId,
                CodexPersonalConsent.ProviderId, CodexPersonalConsent.Origin, prepared.ReferenceIds);
            ConfirmUpload.IsChecked = false;
            EditorStatus.Text = "Approved these sanitized photos for " +
                CodexPersonalConsent.Origin + " via the unofficial helper. " +
                "Select Codex (experimental) as the image model on Main.";
            return;
        }
        var profile = ProviderProfileStore.Default.Find(item.Id);
        if (profile is null)
        {
            EditorStatus.Text = "The selected provider was removed.";
            return;
        }
        var origin = new Uri(profile.BaseUrl).GetLeftPart(UriPartial.Authority);
        if (ConfirmUpload.IsChecked != true)
        {
            EditorStatus.Text = $"Review {origin} above and tick the photo upload consent checkbox first.";
            return;
        }
        _library.ApproveReferences(prepared.ScenarioId, profile.Id, origin, prepared.ReferenceIds);
        ConfirmUpload.IsChecked = false;
        EditorStatus.Text = $"Photos approved for {origin} only. Select this provider on the main screen.";
    }

    private async void ProbeCodex_Click(object sender, RoutedEventArgs e)
    {
        EditorStatus.Text = "Checking local Codex CLI and the unofficial image helper...";
        try
        {
            var result = await CodexPersonalizedImageProvider.FromSettings().ProbeAsync();
            EditorStatus.Text = result.Status +
                " A successful CLI sign-in is not proof that image generation or edits are permitted.";
        }
        catch (CodexImageException ex) { EditorStatus.Text = ex.Message; }
        catch (Exception) { EditorStatus.Text = "Codex availability check failed; do not assume image access."; }
    }

    private async void TestCodexEdit_Click(object sender, RoutedEventArgs e)
    {
        // This is a deliberate, potentially billed IMAGE operation, not a passive probe.
        if (!WallTrek.Services.Settings.Instance.CodexExperimentalEnabled)
        {
            EditorStatus.Text = "Enable Codex experimental mode in Settings first.";
            return;
        }
        var prepared = _library.Prepare();
        if (prepared is null || prepared.ReferenceIds.Count == 0 ||
            !_library.HasReferenceApproval(prepared,
                CodexPersonalConsent.ProviderId, CodexPersonalConsent.Origin))
        {
            EditorStatus.Text = "First approve the exact selected photos for Codex/ChatGPT above.";
            return;
        }
        if (DestinationProfiles.SelectedItem is not ProviderChoice item ||
            item.Id != CodexPersonalConsent.ProviderId)
        {
            EditorStatus.Text = "Choose the Codex experimental destination before testing.";
            return;
        }
        var testButton = sender as Button;
        if (testButton is not null) testButton.IsEnabled = false;
        EditorStatus.Text = "Performing a real Codex reference edit (account limits may apply)...";
        try
        {
            var provider = CodexPersonalizedImageProvider.FromSettings();
            var state = await provider.ProbeAsync();
            if (!state.ConnectionOk)
            {
                EditorStatus.Text = state.Status;
                return;
            }
            var images = _library.ReadForProvider(prepared.ReferenceIds);
            var result = await provider.EditAsync(new ProviderImageRequest(prepared.FinalPrompt, images));
            // Existing Windows BitmapImage path: require actual decodable PNG bytes.
            using var stream = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(stream))
            {
                writer.WriteBytes(result.ImageData);
                await writer.StoreAsync();
                writer.DetachStream();
            }
            stream.Seek(0);
            var preview = new BitmapImage();
            await preview.SetSourceAsync(stream);
            CodexEditPreview.Source = preview;
            EditorStatus.Text = $"Real experimental Codex EDIT returned a valid " +
                $"{result.Width}×{result.Height} image. Review face fidelity visually. " +
                "This confirms only this test, not future account eligibility.";
        }
        catch (CodexImageException ex) { EditorStatus.Text = $"{ex.Reason}: {ex.Message}"; }
        catch (ProviderException ex) { EditorStatus.Text = $"{ex.Reason}: {ex.Message}"; }
        catch (InvalidOperationException ex) { EditorStatus.Text = ex.Message; }
        catch (Exception)
        { EditorStatus.Text = "Codex reference edit failed. No image was saved or wallpaper changed."; }
        finally { if (testButton is not null) testButton.IsEnabled = true; }
    }

    private async Task LoadHistoryAsync()
    {
        try
        {
            RunHistoryList.ItemsSource = await new PersonalizationHistory().RecentAsync();
        }
        catch (Exception)
        { EditorStatus.Text = "Recent history is currently unavailable."; }
    }
    private async void RefreshHistory_Click(object sender, RoutedEventArgs e) =>
        await LoadHistoryAsync();
    private void HistoryList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (RunHistoryList.SelectedItem is PersonalizedRunSummary run)
        {
            FinalPrompt.Text = run.Prompt;
            VariationLabel.Text = run.Display + " · " + run.VariablesJson;
        }
    }
}
