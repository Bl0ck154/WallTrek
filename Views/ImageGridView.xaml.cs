using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using WallTrek.Services;
using WallTrek.Services.DeviantArt;
using WallTrek.Utilities;
using Windows.Storage;
using Windows.Storage.FileProperties;
using Windows.System;

namespace WallTrek.Views
{
    public sealed partial class ImageGridView : UserControl
    {
        public event EventHandler? NavigateBack;
        private ObservableCollection<ImageGridItemViewModel> _displayedImages = new ObservableCollection<ImageGridItemViewModel>();
        private List<ImageGridItemViewModel> _allImages = new List<ImageGridItemViewModel>();
        private List<ImageGridItemViewModel> _filteredImages = new List<ImageGridItemViewModel>();
        private int _displayCount = 100;
        private int _currentDisplayed = 0;
        private readonly DeviantArtUploadService deviantArtUploadService;

        public ImageGridView()
        {
            this.InitializeComponent();
            deviantArtUploadService = new DeviantArtUploadService();
        }

        private void BackButton_Click(object sender, RoutedEventArgs e)
        {
            NavigateBack?.Invoke(this, EventArgs.Empty);
        }

        public async void LoadImages()
        {
            if (SelectModeButton.IsChecked == true)
            {
                SelectModeButton.IsChecked = false;
            }

            _displayedImages.Clear();
            _allImages.Clear();
            _filteredImages.Clear();
            _currentDisplayed = 0;

            var databaseService = new DatabaseService();
            var images = await databaseService.GetAllImagesAsync();

            foreach (var image in images)
            {
                if (File.Exists(image.ImagePath))
                {
                    var viewModel = new ImageGridItemViewModel
                    {
                        ImagePath = image.ImagePath,
                        MediaType = image.MediaType,
                        SourceImagePath = image.SourceImagePath,
                        VideoModel = image.VideoModel,
                        PromptText = image.PromptText,
                        GeneratedDate = image.GeneratedDate,
                        IsFavorite = image.IsFavorite,
                        IsUploaded = image.IsUploaded,
                        IsArchived = image.IsArchived,
                        DeviantArtUrl = image.DeviantArtUrl,
                        LlmModel = image.LlmModel,
                        ImgModel = image.ImgModel,
                        Title = image.Title,
                        Tags = image.Tags
                    };

                    _allImages.Add(viewModel);
                }
            }

            ApplyFilters();
        }

        private void ApplyFilters()
        {
            var favoritesOnly = FavoritesOnlyCheckBox.IsChecked == true;
            var notUploadedOnly = NotUploadedOnlyCheckBox.IsChecked == true;
            var showArchived = ShowArchivedCheckBox.IsChecked == true;

            _filteredImages = _allImages.Where(img =>
            {
                if (favoritesOnly && !img.IsFavorite)
                    return false;
                if (notUploadedOnly && img.IsUploaded)
                    return false;
                if (!showArchived && img.IsArchived)
                    return false;
                return true;
            }).ToList();

            _currentDisplayed = 0;
            _displayedImages.Clear();
            ImagesGrid.ItemsSource = _displayedImages;

            LoadNextBatch();
        }

        private void LoadNextBatch()
        {
            int remaining = _filteredImages.Count - _currentDisplayed;
            int toLoad = Math.Min(_displayCount, remaining);

            for (int i = 0; i < toLoad; i++)
            {
                var viewModel = _filteredImages[_currentDisplayed + i];

                // Load thumbnail asynchronously only if not already loaded
                if (viewModel.ThumbnailImage == null)
                {
                    _ = LoadThumbnailAsync(viewModel);
                }

                _displayedImages.Add(viewModel);
            }

            _currentDisplayed += toLoad;
            UpdateUI();
        }

        private void UpdateUI()
        {
            ImageCountTextBlock.Text = $"{_currentDisplayed}/{_filteredImages.Count} Images";
            var hasMore = _currentDisplayed < _filteredImages.Count;
            LoadMoreButton.Visibility = hasMore ? Visibility.Visible : Visibility.Collapsed;
            LoadAllButton.Visibility = hasMore ? Visibility.Visible : Visibility.Collapsed;
        }

        private void FilterCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            if (_allImages.Count > 0)
            {
                ApplyFilters();
            }
        }

        private void LoadMoreButton_Click(object sender, RoutedEventArgs e)
        {
            LoadNextBatch();
        }

        private void LoadAllButton_Click(object sender, RoutedEventArgs e)
        {
            // Load all remaining images
            while (_currentDisplayed < _filteredImages.Count)
            {
                LoadNextBatch();
            }
        }

        private static readonly string ThumbnailCacheDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "WallTrek", "thumbnails");

        private async Task LoadThumbnailAsync(ImageGridItemViewModel viewModel)
        {
            // Videos show their source image (the loop's first frame); if that image is gone,
            // let the shell extract a frame from the video file itself.
            var thumbnailPath = viewModel.ImagePath;
            var thumbnailMode = ThumbnailMode.PicturesView;
            if (viewModel.IsVideo)
            {
                if (File.Exists(viewModel.SourceImagePath))
                {
                    thumbnailPath = viewModel.SourceImagePath;
                }
                else
                {
                    thumbnailMode = ThumbnailMode.VideosView;
                }
            }

            // Try StorageFile thumbnail (works when packaged)
            try
            {
                var file = await StorageFile.GetFileFromPathAsync(thumbnailPath);
                var thumbnail = await file.GetThumbnailAsync(thumbnailMode, 200);

                if (thumbnail != null)
                {
                    var bitmapImage = new BitmapImage();
                    await bitmapImage.SetSourceAsync(thumbnail);
                    viewModel.ThumbnailImage = bitmapImage;
                    return;
                }
            }
            catch { }

            // Fallback: disk-cached thumbnail (System.Drawing can only decode images)
            if (thumbnailMode == ThumbnailMode.VideosView)
            {
                return;
            }

            try
            {
                var cachePath = GetThumbnailCachePath(thumbnailPath);

                if (!File.Exists(cachePath))
                {
                    await Task.Run(() => GenerateThumbnail(thumbnailPath, cachePath));
                }

                var bitmapImage = new BitmapImage();
                bitmapImage.DecodePixelWidth = 400;
                bitmapImage.UriSource = new Uri(cachePath);
                viewModel.ThumbnailImage = bitmapImage;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Thumbnail failed for {viewModel.ImagePath}: {ex.Message}");
            }
        }

        private static string GetThumbnailCachePath(string imagePath)
        {
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(imagePath)));
            return Path.Combine(ThumbnailCacheDir, hash + ".jpg");
        }

        private static void GenerateThumbnail(string sourcePath, string cachePath)
        {
            Directory.CreateDirectory(ThumbnailCacheDir);

            using var original = new Bitmap(sourcePath);
            int targetWidth = 400;
            int targetHeight = (int)(original.Height * (targetWidth / (double)original.Width));

            using var thumb = new Bitmap(original, targetWidth, targetHeight);
            thumb.Save(cachePath, ImageFormat.Jpeg);
        }

        private void ImagesGrid_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is ImageGridItemViewModel viewModel)
            {
                ShowFullScreenItem(viewModel);
            }
        }

        private void SelectModeButton_Checked(object sender, RoutedEventArgs e)
        {
            SelectModeButton.Content = "Cancel";
            ImagesGrid.IsItemClickEnabled = false;
            ImagesGrid.SelectionMode = ListViewSelectionMode.Multiple;
            FiltersPanel.Visibility = Visibility.Collapsed;
            SelectionActionsPanel.Visibility = Visibility.Visible;
            UpdateSelectionUI();
        }

        private void SelectModeButton_Unchecked(object sender, RoutedEventArgs e)
        {
            SelectModeButton.Content = "Select";
            ImagesGrid.SelectionMode = ListViewSelectionMode.None;
            ImagesGrid.IsItemClickEnabled = true;
            SelectionActionsPanel.Visibility = Visibility.Collapsed;
            FiltersPanel.Visibility = Visibility.Visible;
        }

        private void ImagesGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateSelectionUI();
        }

        private void UpdateSelectionUI()
        {
            var count = ImagesGrid.SelectedItems.Count;
            SelectionCountText.Text = $"{count} selected";
            var hasSelection = count > 0;
            FavoriteSelectedButton.IsEnabled = hasSelection;
            UnfavoriteSelectedButton.IsEnabled = hasSelection;
            ArchiveSelectedButton.IsEnabled = hasSelection;
            UnarchiveSelectedButton.IsEnabled = hasSelection;
            DeleteSelectedButton.IsEnabled = hasSelection;
        }

        private void ImagesGrid_KeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key == VirtualKey.Escape && SelectModeButton.IsChecked == true)
            {
                SelectModeButton.IsChecked = false;
                e.Handled = true;
            }
        }

        private void SelectAllButton_Click(object sender, RoutedEventArgs e)
        {
            ImagesGrid.SelectAll();
        }

        private async void FavoriteSelected_Click(object sender, RoutedEventArgs e)
        {
            await SetFavoriteForSelectionAsync(true);
        }

        private async void UnfavoriteSelected_Click(object sender, RoutedEventArgs e)
        {
            await SetFavoriteForSelectionAsync(false);
        }

        private async Task SetFavoriteForSelectionAsync(bool isFavorite)
        {
            var selected = ImagesGrid.SelectedItems.OfType<ImageGridItemViewModel>().ToList();
            if (selected.Count == 0)
            {
                return;
            }

            var databaseService = new DatabaseService();
            foreach (var item in selected)
            {
                try
                {
                    await databaseService.SetImageFavoriteAsync(item.ImagePath, isFavorite);
                    item.IsFavorite = isFavorite;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Error setting favorite status: {ex.Message}");
                }
            }

            // Unfavorited items no longer belong in a favorites-only view.
            if (!isFavorite && FavoritesOnlyCheckBox.IsChecked == true)
            {
                ApplyFilters();
            }
        }

        private async void ArchiveSelected_Click(object sender, RoutedEventArgs e)
        {
            await SetArchivedForSelectionAsync(true);
        }

        private async void UnarchiveSelected_Click(object sender, RoutedEventArgs e)
        {
            await SetArchivedForSelectionAsync(false);
        }

        private async Task SetArchivedForSelectionAsync(bool archive)
        {
            var selected = ImagesGrid.SelectedItems.OfType<ImageGridItemViewModel>().ToList();
            if (selected.Count == 0)
            {
                return;
            }

            foreach (var item in selected)
            {
                await SetArchivedAsync(item, archive);
            }

            // Newly archived items drop out of view unless archived items are shown.
            if (archive && ShowArchivedCheckBox.IsChecked != true)
            {
                ApplyFilters();
            }
        }

        private async Task SetArchivedAsync(ImageGridItemViewModel item, bool archive)
        {
            try
            {
                item.ImagePath = await ImageArchiveService.SetArchivedAsync(item.ImagePath, archive);
                item.IsArchived = archive;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error setting archive status: {ex.Message}");
            }
        }

        private async void DeleteSelected_Click(object sender, RoutedEventArgs e)
        {
            var selected = ImagesGrid.SelectedItems.OfType<ImageGridItemViewModel>().ToList();
            if (selected.Count == 0)
            {
                return;
            }

            if (!await ConfirmDeleteAsync(selected.Count))
            {
                return;
            }

            foreach (var item in selected)
            {
                try
                {
                    await ImageDeleteService.DeleteAsync(item.ImagePath);
                    RemoveImageFromCollections(item);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Error deleting image: {ex.Message}");
                }
            }

            UpdateUI();
        }

        private async Task<bool> ConfirmDeleteAsync(int count)
        {
            var itemText = count == 1 ? "this item" : $"these {count} items";
            var confirmDialog = new ContentDialog
            {
                Title = "Confirm Delete",
                Content = $"Are you sure you want to delete {itemText}? This action cannot be undone.\n\nThe files will be permanently deleted from your computer.",
                PrimaryButtonText = "Yes, Delete",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = this.XamlRoot
            };

            confirmDialog.PrimaryButtonStyle = new Style(typeof(Button));
            confirmDialog.PrimaryButtonStyle.Setters.Add(new Setter(Button.BackgroundProperty, new SolidColorBrush(Windows.UI.Color.FromArgb(255, 196, 43, 28))));
            confirmDialog.PrimaryButtonStyle.Setters.Add(new Setter(Button.ForegroundProperty, new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 255, 255))));

            return await confirmDialog.ShowAsync() == ContentDialogResult.Primary;
        }

        private ImageGridItemViewModel? _fullScreenItem;

        private void ShowFullScreenItem(ImageGridItemViewModel item)
        {
            _fullScreenItem = item;

            if (item.IsVideo)
            {
                ShowFullScreenVideo(item.ImagePath);
            }
            else
            {
                ShowFullScreenImage(item.ImagePath);
            }

            UpdateFullScreenToolbar();
        }

        private void UpdateFullScreenToolbar()
        {
            if (_fullScreenItem == null)
            {
                return;
            }

            var index = _filteredImages.IndexOf(_fullScreenItem);
            FullScreenPositionText.Text = index >= 0 ? $"{index + 1} / {_filteredImages.Count}" : string.Empty;
            FullScreenPrevButton.IsEnabled = index > 0;
            FullScreenNextButton.IsEnabled = index >= 0 && index < _filteredImages.Count - 1;

            FullScreenFavoriteIcon.Foreground = new SolidColorBrush(
                _fullScreenItem.IsFavorite ? Microsoft.UI.Colors.Gold : Microsoft.UI.Colors.White);
            ToolTipService.SetToolTip(FullScreenFavoriteButton,
                _fullScreenItem.IsFavorite ? "Remove from Favorites" : "Add to Favorites");

            FullScreenArchiveIcon.Foreground = new SolidColorBrush(
                _fullScreenItem.IsArchived ? Microsoft.UI.Colors.DodgerBlue : Microsoft.UI.Colors.White);
            ToolTipService.SetToolTip(FullScreenArchiveButton,
                _fullScreenItem.IsArchived ? "Unarchive" : "Archive");
        }

        private void NavigateFullScreen(int delta)
        {
            if (_fullScreenItem == null)
            {
                return;
            }

            var index = _filteredImages.IndexOf(_fullScreenItem);
            if (index < 0)
            {
                return;
            }

            var newIndex = index + delta;
            if (newIndex < 0 || newIndex >= _filteredImages.Count)
            {
                return;
            }

            ShowFullScreenItem(_filteredImages[newIndex]);
        }

        // After the current item leaves the filtered view (unfavorited, archived), re-filter the
        // grid and land on the item that took its place.
        private void RefreshFullScreenAfterRemoval(int previousIndex)
        {
            ApplyFilters();

            if (_filteredImages.Count == 0)
            {
                HideFullScreenImage();
                return;
            }

            var newIndex = Math.Min(Math.Max(previousIndex, 0), _filteredImages.Count - 1);
            ShowFullScreenItem(_filteredImages[newIndex]);
        }

        private void FullScreenPrev_Click(object sender, RoutedEventArgs e)
        {
            NavigateFullScreen(-1);
        }

        private void FullScreenNext_Click(object sender, RoutedEventArgs e)
        {
            NavigateFullScreen(1);
        }

        private async void FullScreenFavorite_Click(object sender, RoutedEventArgs e)
        {
            if (_fullScreenItem == null)
            {
                return;
            }

            var item = _fullScreenItem;
            var index = _filteredImages.IndexOf(item);
            await ToggleFavoriteAsync(item);

            if (!item.IsFavorite && FavoritesOnlyCheckBox.IsChecked == true)
            {
                RefreshFullScreenAfterRemoval(index);
            }
            else
            {
                UpdateFullScreenToolbar();
            }
        }

        private async void FullScreenArchive_Click(object sender, RoutedEventArgs e)
        {
            if (_fullScreenItem == null)
            {
                return;
            }

            var item = _fullScreenItem;
            var index = _filteredImages.IndexOf(item);
            var archive = !item.IsArchived;

            // Release the playing video's file handle so the file can be moved.
            if (item.IsVideo)
            {
                ReleaseFullScreenMedia();
            }

            await SetArchivedAsync(item, archive);

            if (archive && ShowArchivedCheckBox.IsChecked != true)
            {
                RefreshFullScreenAfterRemoval(index);
            }
            else
            {
                ShowFullScreenItem(item); // reload from the new path
            }
        }

        private async void FullScreenSetBackground_Click(object sender, RoutedEventArgs e)
        {
            if (_fullScreenItem == null)
            {
                return;
            }

            if (_fullScreenItem.IsVideo)
            {
                await SetVideoAsBackground(_fullScreenItem.ImagePath);
            }
            else
            {
                SetAsBackground(_fullScreenItem.ImagePath);
            }
        }

        private async void FullScreenDelete_Click(object sender, RoutedEventArgs e)
        {
            if (_fullScreenItem == null)
            {
                return;
            }

            var item = _fullScreenItem;
            if (!await ConfirmDeleteAsync(1))
            {
                return;
            }

            var index = _filteredImages.IndexOf(item);

            // Release file handles so the file can be deleted.
            FullScreenImage.Source = null;
            ReleaseFullScreenMedia();

            try
            {
                await ImageDeleteService.DeleteAsync(item.ImagePath);
                RemoveImageFromCollections(item);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error deleting image: {ex.Message}");
            }

            UpdateUI();

            if (_filteredImages.Count == 0)
            {
                HideFullScreenImage();
                return;
            }

            ShowFullScreenItem(_filteredImages[Math.Min(Math.Max(index, 0), _filteredImages.Count - 1)]);
        }

        private void FullScreenOverlay_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            var properties = e.GetCurrentPoint(FullScreenOverlay).Properties;
            if (properties.IsXButton1Pressed)
            {
                NavigateFullScreen(-1);
                e.Handled = true;
            }
            else if (properties.IsXButton2Pressed)
            {
                NavigateFullScreen(1);
                e.Handled = true;
            }
        }

        private void ShowFullScreenImage(string imagePath)
        {
            try
            {
                if (File.Exists(imagePath))
                {
                    ReleaseFullScreenMedia();
                    var bitmap = new BitmapImage(new Uri(imagePath));
                    FullScreenImage.Source = bitmap;
                    FullScreenImage.Visibility = Visibility.Visible;
                    FullScreenVideo.Visibility = Visibility.Collapsed;
                    FullScreenOverlay.Visibility = Visibility.Visible;
                    FullScreenOverlay.Focus(FocusState.Programmatic);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error showing fullscreen image: {ex.Message}");
            }
        }

        private Windows.Media.Core.MediaSource? fullScreenVideoSource;

        private void ShowFullScreenVideo(string videoPath)
        {
            try
            {
                if (File.Exists(videoPath))
                {
                    ReleaseFullScreenMedia();
                    fullScreenVideoSource = Windows.Media.Core.MediaSource.CreateFromUri(new Uri(videoPath));
                    FullScreenVideo.Source = fullScreenVideoSource;
                    FullScreenVideo.MediaPlayer.IsLoopingEnabled = true;
                    FullScreenImage.Visibility = Visibility.Collapsed;
                    FullScreenVideo.Visibility = Visibility.Visible;
                    FullScreenOverlay.Visibility = Visibility.Visible;
                    FullScreenOverlay.Focus(FocusState.Programmatic);
                }
            }
            catch (Exception ex)
            {
                Logger.Error("Error showing fullscreen video", ex);
            }
        }

        private void HideFullScreenImage()
        {
            FullScreenOverlay.Visibility = Visibility.Collapsed;
            FullScreenImage.Source = null;
            ReleaseFullScreenMedia();
            _fullScreenItem = null;
        }

        // Release the video file handle so the mp4 can be deleted or moved while the app runs.
        private void ReleaseFullScreenMedia()
        {
            FullScreenVideo.MediaPlayer?.Pause();
            FullScreenVideo.Source = null;
            fullScreenVideoSource?.Dispose();
            fullScreenVideoSource = null;
        }

        private void FullScreenOverlay_KeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key == VirtualKey.Escape)
            {
                HideFullScreenImage();
                e.Handled = true;
            }
            else if (e.Key == VirtualKey.Left)
            {
                NavigateFullScreen(-1);
                e.Handled = true;
            }
            else if (e.Key == VirtualKey.Right)
            {
                NavigateFullScreen(1);
                e.Handled = true;
            }
            else if (e.Key == VirtualKey.F)
            {
                FullScreenFavorite_Click(this, new RoutedEventArgs());
                e.Handled = true;
            }
            else if (e.Key == VirtualKey.A)
            {
                FullScreenArchive_Click(this, new RoutedEventArgs());
                e.Handled = true;
            }
            else if (e.Key == VirtualKey.B)
            {
                FullScreenSetBackground_Click(this, new RoutedEventArgs());
                e.Handled = true;
            }
            else if (e.Key == VirtualKey.Delete)
            {
                FullScreenDelete_Click(this, new RoutedEventArgs());
                e.Handled = true;
            }
        }

        private void FullScreenOverlay_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
        {
            HideFullScreenImage();
        }

        private void FullScreenImage_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
        {
            HideFullScreenImage();
        }

        private async void SetAsBackground_Click(object sender, RoutedEventArgs e)
        {
            if (sender is MenuFlyoutItem menuItem && menuItem.Tag is ImageGridItemViewModel viewModel)
            {
                if (viewModel.IsVideo)
                {
                    await SetVideoAsBackground(viewModel.ImagePath);
                }
                else
                {
                    SetAsBackground(viewModel.ImagePath);
                }
            }
        }

        private async Task SetVideoAsBackground(string videoPath)
        {
            // Video wallpapers only work through Lively; there is no native Windows fallback.
            if (!LivelyService.IsInstalled)
            {
                var dialog = new ContentDialog
                {
                    Title = "Lively Wallpaper required",
                    Content = "Video wallpapers require Lively Wallpaper. Install it from the Microsoft Store, then try again.",
                    CloseButtonText = "OK",
                    XamlRoot = this.XamlRoot
                };
                await dialog.ShowAsync();
                return;
            }

            try
            {
                LivelyService.SetWallpaper(videoPath);
            }
            catch (Exception ex)
            {
                Logger.Error("Error setting video wallpaper via Lively", ex);
            }
        }

        private void SetAsBackground(string imagePath)
        {
            try
            {
                Wallpaper.Set(imagePath);
                // Could show a success message or notification here
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error setting wallpaper: {ex.Message}");
            }
        }

        private async void UploadToDeviantArt_Click(object sender, RoutedEventArgs e)
        {
            if (sender is MenuFlyoutItem menuItem && menuItem.Tag is ImageGridItemViewModel viewModel)
            {
                viewModel.IsUploading = true;

                try
                {
                    // Create a temporary ImageHistoryItem for the upload service
                    var imageItem = new ImageHistoryItem
                    {
                        ImagePath = viewModel.ImagePath,
                        IsUploadedToDeviantArt = viewModel.IsUploaded,
                        DeviantArtUrl = viewModel.DeviantArtUrl,
                        LlmModel = viewModel.LlmModel,
                        ImgModel = viewModel.ImgModel,
                        VideoModel = viewModel.VideoModel
                    };

                    // Parse tags from comma-separated string to array
                    string[]? tags = null;
                    if (!string.IsNullOrWhiteSpace(viewModel.Tags))
                    {
                        tags = viewModel.Tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    }

                    string? title = !string.IsNullOrWhiteSpace(viewModel.Title) ? viewModel.Title : null;

                    await deviantArtUploadService.UploadImageAsync(imageItem, viewModel.PromptText, this.XamlRoot, () =>
                    {
                        // Update the view model after successful upload
                        viewModel.IsUploaded = imageItem.IsUploadedToDeviantArt;
                        viewModel.DeviantArtUrl = imageItem.DeviantArtUrl ?? string.Empty;
                        LoadImages(); // Refresh the grid
                    }, title, tags);
                }
                finally
                {
                    viewModel.IsUploading = false;
                }
            }
        }

        private async void ViewOnDeviantArt_Click(object sender, RoutedEventArgs e)
        {
            if (sender is MenuFlyoutItem menuItem && menuItem.Tag is ImageGridItemViewModel viewModel)
            {
                if (!string.IsNullOrEmpty(viewModel.DeviantArtUrl))
                {
                    try
                    {
                        await Launcher.LaunchUriAsync(new Uri(viewModel.DeviantArtUrl));
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Error opening DeviantArt URL: {ex.Message}");
                    }
                }
            }
        }

        private void OpenImage_Click(object sender, RoutedEventArgs e)
        {
            if (sender is MenuFlyoutItem menuItem && menuItem.Tag is ImageGridItemViewModel viewModel)
            {
                OpenImage(viewModel.ImagePath);
            }
        }

        private async void OpenImage(string imagePath)
        {
            try
            {
                if (File.Exists(imagePath))
                {
                    await Launcher.LaunchUriAsync(new Uri($"file:///{imagePath.Replace('\\', '/')}"));
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error opening image: {ex.Message}");
            }
        }

        private async void OpenInExplorer_Click(object sender, RoutedEventArgs e)
        {
            if (sender is MenuFlyoutItem menuItem && menuItem.Tag is ImageGridItemViewModel viewModel)
            {
                try
                {
                    var folderPath = Path.GetDirectoryName(viewModel.ImagePath);
                    if (Directory.Exists(folderPath))
                    {
                        await Launcher.LaunchUriAsync(new Uri($"file:///{folderPath.Replace('\\', '/')}"));
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Error opening explorer: {ex.Message}");
                }
            }
        }

        private void ImageContextMenu_Opening(object sender, object e)
        {
            if (sender is MenuFlyout menuFlyout)
            {
                foreach (var item in menuFlyout.Items)
                {
                    if (item is not MenuFlyoutItem menuItem || menuItem.Tag is not ImageGridItemViewModel viewModel)
                    {
                        continue;
                    }

                    switch (menuItem.Name)
                    {
                        case "ToggleFavoriteItem":
                            menuItem.Text = viewModel.IsFavorite ? "Remove from Favorites" : "Add to Favorites";
                            break;
                        case "ToggleArchiveItem":
                            menuItem.Text = viewModel.IsArchived ? "Unarchive" : "Archive";
                            break;
                        case "GenerateVideoItem":
                            menuItem.Visibility = viewModel.IsVideo ? Visibility.Collapsed : Visibility.Visible;
                            break;
                    }
                }
            }
        }

        private async void ToggleFavorite_Click(object sender, RoutedEventArgs e)
        {
            if (sender is MenuFlyoutItem menuItem && menuItem.Tag is ImageGridItemViewModel viewModel)
            {
                await ToggleFavoriteAsync(viewModel);
            }
        }

        private async Task ToggleFavoriteAsync(ImageGridItemViewModel viewModel)
        {
            try
            {
                var newFavoriteStatus = !viewModel.IsFavorite;

                // Update database
                var databaseService = new DatabaseService();
                await databaseService.SetImageFavoriteAsync(viewModel.ImagePath, newFavoriteStatus);

                // Update view model
                viewModel.IsFavorite = newFavoriteStatus;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error toggling favorite status: {ex.Message}");
            }
        }

        private async void ToggleArchive_Click(object sender, RoutedEventArgs e)
        {
            if (sender is MenuFlyoutItem menuItem && menuItem.Tag is ImageGridItemViewModel viewModel)
            {
                var archive = !viewModel.IsArchived;
                await SetArchivedAsync(viewModel, archive);

                if (archive && ShowArchivedCheckBox.IsChecked != true)
                {
                    ApplyFilters();
                }
            }
        }

        private void GenerateVideo_Click(object sender, RoutedEventArgs e)
        {
            if (sender is MenuFlyoutItem menuItem && menuItem.Tag is ImageGridItemViewModel viewModel)
            {
                GenerateVideoDialog.Show(viewModel, this.XamlRoot, onVideoCreated: () => LoadImages());
            }
        }

        private void Details_Click(object sender, RoutedEventArgs e)
        {
            if (sender is MenuFlyoutItem menuItem && menuItem.Tag is ImageGridItemViewModel viewModel)
            {
                ImageDetailsDialog.Show(viewModel, this.XamlRoot, OnImageDeleted);
            }
        }

        private void OnImageDeleted(string imagePath)
        {
            // Remove the deleted image from all collections
            var imageToRemove = _allImages.FirstOrDefault(img => img.ImagePath == imagePath);
            if (imageToRemove != null)
            {
                RemoveImageFromCollections(imageToRemove);
                UpdateUI();
            }
        }

        private void RemoveImageFromCollections(ImageGridItemViewModel image)
        {
            _allImages.Remove(image);
            _filteredImages.Remove(image);
            if (_displayedImages.Remove(image))
            {
                _currentDisplayed--;
            }
        }
    }

    public class ImageGridItemViewModel : System.ComponentModel.INotifyPropertyChanged
    {
        public string ImagePath { get; set; } = string.Empty;
        public string MediaType { get; set; } = "image";
        public string SourceImagePath { get; set; } = string.Empty;
        public string VideoModel { get; set; } = string.Empty;
        public bool IsVideo => MediaType == "video";
        public string PromptText { get; set; } = string.Empty;
        public DateTime GeneratedDate { get; set; }
        public string DeviantArtUrl { get; set; } = string.Empty;
        public string LlmModel { get; set; } = string.Empty;
        public string ImgModel { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string Tags { get; set; } = string.Empty;

        private bool _isFavorite;
        public bool IsFavorite
        {
            get => _isFavorite;
            set
            {
                if (_isFavorite != value)
                {
                    _isFavorite = value;
                    PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(IsFavorite)));
                }
            }
        }

        private bool _isArchived;
        public bool IsArchived
        {
            get => _isArchived;
            set
            {
                if (_isArchived != value)
                {
                    _isArchived = value;
                    PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(IsArchived)));
                }
            }
        }

        private bool _isUploaded;
        public bool IsUploaded
        {
            get => _isUploaded;
            set
            {
                if (_isUploaded != value)
                {
                    _isUploaded = value;
                    PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(IsUploaded)));
                }
            }
        }

        private bool _isUploading;
        public bool IsUploading
        {
            get => _isUploading;
            set
            {
                if (_isUploading != value)
                {
                    _isUploading = value;
                    PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(IsUploading)));
                }
            }
        }

        private BitmapImage? _thumbnailImage;
        public BitmapImage? ThumbnailImage
        {
            get => _thumbnailImage;
            set
            {
                if (_thumbnailImage != value)
                {
                    _thumbnailImage = value;
                    PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(ThumbnailImage)));
                }
            }
        }

        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    }

    public class FavoriteColorConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            if (value is bool isFavorite && isFavorite)
            {
                return new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Gold);
            }
            return new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Gray);
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }

    public class BooleanNegationConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            if (value is bool boolValue)
            {
                return !boolValue;
            }
            return true;
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            if (value is bool boolValue)
            {
                return !boolValue;
            }
            return false;
        }
    }

    public class BooleanToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            if (value is bool boolValue)
            {
                bool shouldShow = boolValue;

                // Check if we should invert the logic
                if (parameter?.ToString()?.ToLowerInvariant() == "invert")
                {
                    shouldShow = !boolValue;
                }

                return shouldShow ? Visibility.Visible : Visibility.Collapsed;
            }
            return Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            if (value is Visibility visibility)
            {
                bool isVisible = visibility == Visibility.Visible;

                // Check if we should invert the logic
                if (parameter?.ToString()?.ToLowerInvariant() == "invert")
                {
                    return !isVisible;
                }

                return isVisible;
            }
            return false;
        }
    }
}
