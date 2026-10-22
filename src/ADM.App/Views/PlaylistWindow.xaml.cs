using System.Windows;
using Adm.Core.Media;
using Adm.Core.Settings;

namespace Adm.App.Views;

/// <summary>
/// Lists the videos of a playlist and lets the user pick the ones to queue.
/// The quality is chosen later, per video, in the normal format picker.
/// </summary>
public partial class PlaylistWindow : Window
{
    private readonly string _url;
    private readonly YtDlpService _service;
    private readonly AppSettings _settings;

    private CancellationTokenSource? _cts;
    private PlaylistInfo? _playlist;
    private bool _closed;

    public PlaylistWindow(string url, YtDlpService service, AppSettings settings)
    {
        InitializeComponent();

        _url = url;
        _service = service;
        _settings = settings;

        SourceText.Text = url;
        FolderText.Text = "Save to " + settings.DownloadFolder;
        SetLoading(true);
    }

    /// <summary>The videos the user selected.</summary>
    public IReadOnlyList<PlaylistEntry> SelectedEntries { get; private set; } = Array.Empty<PlaylistEntry>();

    private async void OnLoaded(object sender, RoutedEventArgs e) =>
        await LoadAsync();

    private async Task LoadAsync()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = new CancellationTokenSource();

        _playlist = null;
        SelectedEntries = Array.Empty<PlaylistEntry>();
        EntryList.ItemsSource = null;
        ErrorPanel.Visibility = Visibility.Collapsed;
        DownloadButton.IsEnabled = false;
        SetLoading(true);

        try
        {
            var playlist = await _service.GetPlaylistAsync(_url, _cts.Token);
            if (_closed || _cts.IsCancellationRequested)
                return;

            _playlist = playlist;
            TitleText.Text = playlist.Title;
            SourceText.Text = playlist.Url;
            SummaryText.Text = playlist.SummaryText;
            EntryList.ItemsSource = playlist.Entries;
            EntryList.SelectAll();

            DownloadButton.IsEnabled = playlist.Entries.Count > 0;
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
            TitleText.Text = "Download playlist";
            SummaryText.Text = string.Empty;
        }
        finally
        {
            SetLoading(false);
        }
    }

    private void SetLoading(bool loading)
    {
        LoadingPanel.Visibility = loading ? Visibility.Visible : Visibility.Collapsed;
        EntryList.Visibility = loading ? Visibility.Hidden : Visibility.Visible;

        if (loading)
        {
            ErrorPanel.Visibility = Visibility.Collapsed;
            SummaryText.Text = string.Empty;
        }
    }

    private void OnDownload(object sender, RoutedEventArgs e)
    {
        var selected = EntryList.SelectedItems.Cast<PlaylistEntry>().ToList();
        if (selected.Count == 0)
        {
            MessageBox.Show(this, "Select at least one video to download.",
                "Download playlist", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        SelectedEntries = selected;
        DialogResult = true;
    }

    private void OnSelectAll(object sender, RoutedEventArgs e) => EntryList.SelectAll();

    private void OnSelectNone(object sender, RoutedEventArgs e) => EntryList.UnselectAll();

    private void OnRetry(object sender, RoutedEventArgs e) => _ = LoadAsync();

    protected override void OnClosed(EventArgs e)
    {
        _closed = true;
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        base.OnClosed(e);
    }
}
