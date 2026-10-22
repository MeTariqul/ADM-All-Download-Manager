using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Adm.Core.Media;
using Adm.Core.Settings;

namespace Adm.App.Views;

/// <summary>Fetches yt-dlp formats for one page and lets the user choose one.</summary>
public partial class VideoDownloadWindow : Window
{
    private readonly string _url;
    private readonly YtDlpService _service;
    private readonly AppSettings _settings;

    private CancellationTokenSource? _cts;
    private VideoInfo? _info;
    private bool _closed;

    public VideoDownloadWindow(string url, YtDlpService service, AppSettings settings)
    {
        InitializeComponent();

        _url = url;
        _service = service;
        _settings = settings;

        SourceText.Text = url;
        FolderText.Text = "Save to " + settings.DownloadFolder;
        SetLoading(true);
    }

    public VideoInfo? SelectedInfo { get; private set; }

    public VideoFormat? SelectedFormat { get; private set; }

    /// <summary>Set when the page turned out to be a playlist.</summary>
    public PlaylistInfo? SelectedPlaylist { get; private set; }

    private async void OnLoaded(object sender, RoutedEventArgs e) =>
        await LoadFormatsAsync();

    private async Task LoadFormatsAsync()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = new CancellationTokenSource();

        _info = null;
        SelectedInfo = null;
        SelectedFormat = null;
        SelectedPlaylist = null;
        FormatList.ItemsSource = null;
        ErrorPanel.Visibility = Visibility.Collapsed;
        DownloadButton.IsEnabled = false;
        SetLoading(true);

        try
        {
            var info = await _service.GetInfoAsync(_url, _cts.Token, allowPlaylist: true);
            if (_closed || _cts.IsCancellationRequested)
                return;

            _info = info;
            TitleText.Text = info.Title;
            SourceText.Text = info.PageUrl;
            SummaryText.Text = info.SummaryText;

            if (info.IsPlaylist)
            {
                // A playlist has no single quality, so it gets its own flow.
                ShowPlaylist(info.Playlist!);
                return;
            }

            FormatList.ItemsSource = info.Formats;
            FormatCountText.Text = info.Formats.Count == 1 ? "1 option" : $"{info.Formats.Count} options";

            VideoFormat? selected = info.Formats.FirstOrDefault(format => format.IsRecommended) ??
                                    info.Formats.FirstOrDefault();
            FormatList.SelectedItem = selected;
            DownloadButton.IsEnabled = selected is not null;
            if (selected is not null)
                FormatList.ScrollIntoView(selected);

            _ = LoadThumbnailAsync(info.ThumbnailUrl);
        }
        catch (OperationCanceledException)
        {
            // The window is closing.
        }
        catch (Exception ex)
        {
            if (_closed)
                return;

            ErrorText.Text = ex.Message;
            ErrorPanel.Visibility = Visibility.Visible;
            TitleText.Text = "Download video";
            SummaryText.Text = string.Empty;
        }
        finally
        {
            SetLoading(false);
        }
    }

    /// <summary>Switches the picker into playlist mode.</summary>
    private void ShowPlaylist(PlaylistInfo playlist)
    {
        SelectedPlaylist = playlist;

        FormatList.Visibility = Visibility.Collapsed;
        FormatCountText.Text = $"{playlist.Count} videos";
        PlaylistText.Text = $"This link points to a playlist with {playlist.Count} videos. " +
                            "You can pick the ones you want to download.";
        PlaylistPanel.Visibility = Visibility.Visible;
        DownloadButton.IsEnabled = true;
        DownloadButton.Content = "Choose videos...";
    }

    private void OnDownload(object sender, RoutedEventArgs e)
    {
        if (_info is null)
            return;

        if (_info.IsPlaylist)
        {
            // The main window opens the playlist picker with this list.
            DialogResult = true;
            return;
        }

        if (FormatList.SelectedItem is not VideoFormat format)
            return;

        SelectedInfo = _info;
        SelectedFormat = format;
        DialogResult = true;
    }

    /// <summary>
    /// Loads the cover image off the UI thread. A missing or slow thumbnail is
    /// simply ignored; the picker stays usable without it.
    /// </summary>
    private async Task LoadThumbnailAsync(string? thumbnailUrl)
    {
        if (string.IsNullOrWhiteSpace(thumbnailUrl) ||
            !Uri.TryCreate(thumbnailUrl, UriKind.Absolute, out var uri))
        {
            return;
        }

        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            byte[] data = await client.GetByteArrayAsync(uri);
            if (_closed)
                return;

            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = new MemoryStream(data);
            image.EndInit();

            ThumbnailImage.Source = image;
            ThumbnailImage.Clip = new RectangleGeometry(new Rect(0, 0, 132, 74), 6, 6);
            ThumbnailImage.Visibility = Visibility.Visible;
        }
        catch (Exception)
        {
            // The cover image is optional.
        }
    }

    private void SetLoading(bool loading)
    {
        LoadingPanel.Visibility = loading ? Visibility.Visible : Visibility.Collapsed;
        FormatList.Visibility = loading ? Visibility.Hidden : Visibility.Visible;

        if (loading)
        {
            ErrorPanel.Visibility = Visibility.Collapsed;
            FormatCountText.Text = string.Empty;
        }
    }

    private void OnFormatDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (!DownloadButton.IsEnabled || e.OriginalSource is not DependencyObject source)
            return;

        if (ItemsControl.ContainerFromElement(FormatList, source) is ListBoxItem { DataContext: VideoFormat })
            OnDownload(sender, e);
    }

    private void OnRetry(object sender, RoutedEventArgs e) =>
        _ = LoadFormatsAsync();

    protected override void OnClosed(EventArgs e)
    {
        _closed = true;
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        base.OnClosed(e);
    }
}
