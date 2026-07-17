using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.IO;
using System.Linq;
using System.Threading;
using WallTrek.Services;
using WallTrek.Services.VideoGen;

namespace WallTrek.Views
{
    /// <summary>
    /// Options dialog for generating a seamless loop video from a generated image via fal.ai.
    /// Shown from the image grid's right-click menu; hosts model/duration/upscale pickers with a
    /// live price estimate, then runs the generation in-dialog with progress and cancellation.
    /// </summary>
    public sealed partial class GenerateVideoDialog : UserControl
    {
        private string imagePath = string.Empty;
        private string imagePrompt = string.Empty;
        private string imageLlmModel = string.Empty;
        private string imageImgModel = string.Empty;
        private Action? onVideoCreated;
        private ContentDialog? dialog;
        private CancellationTokenSource? cts;
        private bool isGenerating;

        public GenerateVideoDialog()
        {
            this.InitializeComponent();

            foreach (var option in VideoModelCatalog.Options)
            {
                ModelComboBox.Items.Add(new ComboBoxItem { Content = option.DisplayName, Tag = option });
            }
            ModelComboBox.SelectedIndex = VideoModelCatalog.Options.ToList().IndexOf(VideoModelCatalog.Default);
        }

        public static async void Show(ImageGridItemViewModel viewModel, XamlRoot xamlRoot, Action? onVideoCreated = null)
        {
            var content = new GenerateVideoDialog
            {
                imagePath = viewModel.ImagePath,
                imagePrompt = viewModel.PromptText,
                imageLlmModel = viewModel.LlmModel,
                imageImgModel = viewModel.ImgModel,
                onVideoCreated = onVideoCreated,
            };
            var dialog = new ContentDialog
            {
                Title = "Generate Video",
                Content = content,
                PrimaryButtonText = "Generate",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = xamlRoot
            };
            content.dialog = dialog;
            dialog.PrimaryButtonClick += content.OnGenerateClick;
            dialog.Closing += content.OnDialogClosing;

            // The motion prompt starts empty; Generate stays disabled until one is entered
            // (typed, or suggested via the button).
            content.UpdateGenerateEnabled();

            await dialog.ShowAsync();
        }

        /// <summary>
        /// Asks the configured LLM for a loop-friendly motion prompt based on the image's original
        /// generation prompt, replacing whatever is in the box.
        /// </summary>
        private async void SuggestPromptButton_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(imagePrompt) || isGenerating)
            {
                return;
            }

            SuggestPromptButton.IsEnabled = false;
            try
            {
                var service = new Services.TextGen.MotionPromptService();
                var suggestion = await service.GenerateMotionPromptAsync(imagePrompt);
                if (!string.IsNullOrWhiteSpace(suggestion) && !isGenerating)
                {
                    MotionPromptTextBox.Text = suggestion;
                }
            }
            catch (Exception ex)
            {
                Logger.Error("Motion prompt suggestion failed", ex);
                ProgressPanel.Visibility = Visibility.Visible;
                StatusText.Text = $"Prompt suggestion failed: {ex.Message}";
            }
            finally
            {
                SuggestPromptButton.IsEnabled = !isGenerating;
            }
        }

        private void MotionPromptTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            UpdateGenerateEnabled();
        }

        private void UpdateGenerateEnabled()
        {
            if (dialog != null && !isGenerating)
            {
                dialog.IsPrimaryButtonEnabled = !string.IsNullOrWhiteSpace(MotionPromptTextBox.Text);
            }
        }

        private VideoModelOption SelectedModel =>
            (ModelComboBox.SelectedItem as ComboBoxItem)?.Tag as VideoModelOption ?? VideoModelCatalog.Default;

        private int SelectedDuration =>
            (DurationComboBox.SelectedItem as ComboBoxItem)?.Tag is int duration ? duration : SelectedModel.Durations[0];

        private VideoUpscaleOption SelectedUpscale =>
            (UpscaleComboBox.SelectedItem as ComboBoxItem)?.Tag as VideoUpscaleOption ?? VideoModelCatalog.UpscaleOptions[0];

        private void ModelComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            RebuildDurationOptions();
            RebuildUpscaleOptions();
            UpdatePriceEstimate();
        }

        private void Option_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // Upscale prices scale with duration, so refresh those labels too.
            if (ReferenceEquals(sender, DurationComboBox))
            {
                RebuildUpscaleOptions();
            }
            UpdatePriceEstimate();
        }

        private void RebuildDurationOptions()
        {
            var previous = (DurationComboBox.SelectedItem as ComboBoxItem)?.Tag as int?;
            DurationComboBox.Items.Clear();
            foreach (var duration in SelectedModel.Durations)
            {
                DurationComboBox.Items.Add(new ComboBoxItem { Content = $"{duration} seconds", Tag = duration });
            }
            var index = previous.HasValue ? Array.IndexOf(SelectedModel.Durations, previous.Value) : -1;
            DurationComboBox.SelectedIndex = index >= 0 ? index : 0;
        }

        private void RebuildUpscaleOptions()
        {
            var previousId = ((UpscaleComboBox.SelectedItem as ComboBoxItem)?.Tag as VideoUpscaleOption)?.Id;
            var duration = SelectedDuration;

            UpscaleComboBox.Items.Clear();
            foreach (var option in VideoModelCatalog.UpscaleOptions)
            {
                var label = option.CentsPerSecond > 0
                    ? $"{option.Name} (+{FormatDollars(option.GetCents(duration))})"
                    : option.Name;
                UpscaleComboBox.Items.Add(new ComboBoxItem { Content = label, Tag = option });
            }

            var targetId = previousId ?? VideoModelCatalog.DefaultUpscale.Id;
            var index = VideoModelCatalog.UpscaleOptions.ToList().FindIndex(o => o.Id == targetId);
            UpscaleComboBox.SelectedIndex = index >= 0 ? index : 0;
        }

        private void UpdatePriceEstimate()
        {
            if (DurationComboBox.SelectedItem == null || UpscaleComboBox.SelectedItem == null)
            {
                return;
            }

            var duration = SelectedDuration;
            var videoCents = SelectedModel.GetCents(duration);
            var upscaleCents = SelectedUpscale.GetCents(duration);
            var total = FormatDollars(videoCents + upscaleCents);

            PriceEstimateText.Text = upscaleCents > 0
                ? $"Estimated cost: ~{total} (video {FormatDollars(videoCents)} + upscale {FormatDollars(upscaleCents)})"
                : $"Estimated cost: ~{total}";
        }

        private static string FormatDollars(decimal cents) => $"${cents / 100m:0.00}";

        private async void OnGenerateClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
        {
            // Keep the dialog open; it becomes the progress surface.
            args.Cancel = true;

            if (isGenerating)
            {
                return;
            }

            var apiKey = Settings.Instance.FalApiKey;
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                ProgressPanel.Visibility = Visibility.Visible;
                StatusText.Text = "No fal.ai API key set. Add one in Settings → API.";
                return;
            }

            isGenerating = true;
            SetOptionsEnabled(false);
            sender.IsPrimaryButtonEnabled = false;
            ProgressPanel.Visibility = Visibility.Visible;
            ProgressSpinner.IsActive = true;
            cts = new CancellationTokenSource();

            var request = new VideoGenerationRequest
            {
                ImagePath = imagePath,
                Model = SelectedModel,
                DurationSeconds = SelectedDuration,
                Prompt = MotionPromptTextBox.Text,
                Upscale = SelectedUpscale.CentsPerSecond > 0 ? SelectedUpscale : null,
            };

            try
            {
                Logger.Info($"Generating loop video — model: {request.Model.Id}, duration: {request.DurationSeconds}s, upscale: {request.Upscale?.Id ?? "none"}");
                using var service = new FalVideoService(apiKey);
                var progress = new Progress<string>(message => StatusText.Text = message);
                var outputPath = await service.GenerateLoopVideoAsync(request, progress, cts.Token);

                try
                {
                    // The video inherits the source image's models; the fal model goes in VideoModel.
                    var databaseService = new DatabaseService();
                    await databaseService.AddGeneratedVideoAsync(
                        outputPath,
                        imageLlmModel,
                        imageImgModel,
                        request.Model.Id,
                        request.Prompt,
                        imagePath);
                    onVideoCreated?.Invoke();
                }
                catch (Exception ex)
                {
                    // The video is on disk either way; a failed registration only means it
                    // won't show in the gallery.
                    Logger.Error("Failed to register generated video in database", ex);
                }

                ProgressSpinner.IsActive = false;
                StatusText.Text = $"Saved: {Path.GetFileName(outputPath)}";
                sender.CloseButtonText = "Close";
            }
            catch (OperationCanceledException)
            {
                ProgressSpinner.IsActive = false;
                StatusText.Text = "Cancelled.";
            }
            catch (Exception ex)
            {
                Logger.Error("Video generation failed", ex);
                ProgressSpinner.IsActive = false;
                StatusText.Text = $"Error: {ex.Message}";
                // Let the user tweak options and retry.
                SetOptionsEnabled(true);
                sender.IsPrimaryButtonEnabled = true;
            }
            finally
            {
                isGenerating = false;
                cts.Dispose();
                cts = null;
            }
        }

        private void SetOptionsEnabled(bool enabled)
        {
            ModelComboBox.IsEnabled = enabled;
            DurationComboBox.IsEnabled = enabled;
            UpscaleComboBox.IsEnabled = enabled;
            MotionPromptTextBox.IsEnabled = enabled;
            SuggestPromptButton.IsEnabled = enabled;
        }

        private void OnDialogClosing(ContentDialog sender, ContentDialogClosingEventArgs args)
        {
            // Closing while a job is running cancels it (the fal job itself keeps running server-side,
            // but nothing is billed extra for abandoning the result).
            cts?.Cancel();
        }
    }
}
