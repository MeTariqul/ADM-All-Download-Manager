using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Adm.Core.Models;
using Adm.Core.Util;

namespace Adm.App.Views;

public sealed class SegmentViewModel
{
    public string Label { get; set; } = string.Empty;
    public double ProgressPercent { get; set; }
    public string StatusText { get; set; } = string.Empty;
}

/// <summary>
/// Per-download overrides and live connection/segment visualizer.
/// </summary>
public partial class DownloadOptionsWindow : Window
{
    private static readonly SolidColorBrush[] ConnectionColors =
    [
        new(Color.FromRgb(59, 130, 246)),  // Blue
        new(Color.FromRgb(16, 185, 129)),  // Emerald
        new(Color.FromRgb(139, 92, 246)),  // Violet
        new(Color.FromRgb(245, 158, 11)),  // Amber
        new(Color.FromRgb(236, 72, 153)),  // Pink
        new(Color.FromRgb(6, 182, 212)),   // Cyan
        new(Color.FromRgb(249, 115, 22)),  // Orange
        new(Color.FromRgb(99, 102, 241))   // Indigo
    ];

    private readonly DownloadItem _item;
    private readonly DispatcherTimer _timer;

    public DownloadOptionsWindow(DownloadItem item)
    {
        InitializeComponent();
        _item = item;

        FileText.Text = string.IsNullOrWhiteSpace(item.FileName) ? item.Url : item.FileName;
        ConnectionsBox.Text = item.MaxConnections.ToString();
        SpeedLimitBox.Text = (item.SpeedLimitBytesPerSecond / 1024).ToString();
        PriorityBox.Text = item.Priority.ToString();

        ChecksumBox.Text = item.ExpectedChecksum ?? string.Empty;
        ChecksumAlgorithmBox.ItemsSource = new[] { "None", "MD5", "SHA1", "SHA256" };
        ChecksumAlgorithmBox.SelectedItem = item.ChecksumAlgorithm switch
        {
            ChecksumAlgorithm.Md5 => "MD5",
            ChecksumAlgorithm.Sha1 => "SHA1",
            ChecksumAlgorithm.Sha256 => "SHA256",
            _ => "None"
        };

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _timer.Tick += (_, _) => RenderSegments();
        _timer.Start();

        Closed += (_, _) => _timer.Stop();
        RenderSegments();
    }

    private void OnCanvasSizeChanged(object sender, SizeChangedEventArgs e) => RenderSegments();

    private void RenderSegments()
    {
        FileText.Text = string.IsNullOrWhiteSpace(_item.FileName)
            ? _item.Url
            : $"{_item.FileName}  ·  {_item.DetailText}";

        SegmentCanvas.Children.Clear();
        double canvasWidth = SegmentCanvas.ActualWidth > 0 ? SegmentCanvas.ActualWidth : 440;
        double canvasHeight = SegmentCanvas.ActualHeight > 0 ? SegmentCanvas.ActualHeight : 22;

        var segments = _item.Segments.ToList();
        if (segments.Count == 0)
        {
            if (_item.Status == DownloadStatus.Completed && _item.TotalBytes > 0)
            {
                SegmentsSummaryText.Text = $"Completed ({Format.Bytes(_item.TotalBytes)})";
                var rect = new Rectangle
                {
                    Width = canvasWidth,
                    Height = canvasHeight,
                    Fill = new SolidColorBrush(Color.FromRgb(16, 185, 129))
                };
                SegmentCanvas.Children.Add(rect);
            }
            else if (_item.TotalBytes > 0)
            {
                SegmentsSummaryText.Text = _item.SupportsRanges
                    ? "Connecting and preparing parallel segments..."
                    : "Single-stream sequential transfer (Server does not support Range requests).";

                double progressWidth = Math.Clamp(_item.Progress * canvasWidth, 0, canvasWidth);
                if (progressWidth > 0)
                {
                    var rect = new Rectangle
                    {
                        Width = progressWidth,
                        Height = canvasHeight,
                        Fill = ConnectionColors[0]
                    };
                    SegmentCanvas.Children.Add(rect);
                }
            }
            else
            {
                SegmentsSummaryText.Text = "Unknown file size or single connection.";
            }

            SegmentsList.ItemsSource = null;
            return;
        }

        long total = _item.TotalBytes > 0 ? _item.TotalBytes : segments.Sum(s => Math.Max(0, s.End - s.Start + 1));
        if (total <= 0)
            total = 1;

        SegmentsSummaryText.Text = $"{segments.Count} connections  ·  {Format.Bytes(_item.BytesDownloaded)} of {Format.Bytes(_item.TotalBytes)} ({_item.ProgressPercent:F1}%)" +
            (string.IsNullOrWhiteSpace(_item.SpeedText) ? string.Empty : $"  ·  {_item.SpeedText}");

        var viewModels = new List<SegmentViewModel>(segments.Count);

        for (int i = 0; i < segments.Count; i++)
        {
            var seg = segments[i];
            long segSize = Math.Max(1, seg.End - seg.Start + 1);
            long downloaded = Math.Clamp(seg.Position - seg.Start, 0, segSize);
            double percent = Math.Clamp((double)downloaded / segSize * 100.0, 0, 100);

            double startRatio = (double)seg.Start / total;
            double endRatio = (double)(seg.End + 1) / total;
            double posRatio = (double)Math.Clamp(seg.Position, seg.Start, seg.End + 1) / total;

            double segLeft = startRatio * canvasWidth;
            double segWidth = Math.Max(1, (endRatio - startRatio) * canvasWidth);
            double downloadedWidth = Math.Clamp((posRatio - startRatio) * canvasWidth, 0, segWidth);

            var brush = ConnectionColors[i % ConnectionColors.Length];

            var bgRect = new Rectangle
            {
                Width = segWidth,
                Height = canvasHeight,
                Fill = new SolidColorBrush(Color.FromArgb(35, brush.Color.R, brush.Color.G, brush.Color.B))
            };
            Canvas.SetLeft(bgRect, segLeft);
            SegmentCanvas.Children.Add(bgRect);

            if (downloadedWidth > 0)
            {
                var fgRect = new Rectangle
                {
                    Width = downloadedWidth,
                    Height = canvasHeight,
                    Fill = brush
                };
                Canvas.SetLeft(fgRect, segLeft);
                SegmentCanvas.Children.Add(fgRect);
            }

            if (i > 0)
            {
                var divider = new Rectangle
                {
                    Width = 1,
                    Height = canvasHeight,
                    Fill = new SolidColorBrush(Color.FromArgb(120, 255, 255, 255))
                };
                Canvas.SetLeft(divider, segLeft);
                SegmentCanvas.Children.Add(divider);
            }

            string rangeStr = $"[{Format.Bytes(seg.Start)} - {Format.Bytes(seg.End)}]";
            string statusStr = seg.Position > seg.End
                ? $"Done ({Format.Bytes(segSize)})"
                : $"{Format.Bytes(downloaded)} / {Format.Bytes(segSize)} ({percent:F0}%)";

            viewModels.Add(new SegmentViewModel
            {
                Label = $"Part #{i + 1}",
                ProgressPercent = percent,
                StatusText = $"{statusStr}  {rangeStr}"
            });
        }

        SegmentsList.ItemsSource = viewModels;
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(ConnectionsBox.Text.Trim(), out int connections) || connections is < 1 or > 32)
        {
            MessageBox.Show(this, "Connections must be a number between 1 and 32.",
                "Download options", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!long.TryParse(SpeedLimitBox.Text.Trim(), out long limitKb) || limitKb < 0)
        {
            MessageBox.Show(this, "Speed limit must be 0 (unlimited) or a positive number of KB/s.",
                "Download options", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!int.TryParse(PriorityBox.Text.Trim(), out int priority))
        {
            MessageBox.Show(this, "Priority must be a number. Higher numbers start first.",
                "Download options", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _item.MaxConnections = connections;
        _item.SpeedLimitBytesPerSecond = limitKb * 1024;
        _item.Priority = priority;

        string checksum = ChecksumBox.Text.Trim();
        _item.ExpectedChecksum = string.IsNullOrWhiteSpace(checksum) ? null : checksum;
        _item.ChecksumAlgorithm = Checksum.ParseAlgorithm(ChecksumAlgorithmBox.SelectedItem as string);

        DialogResult = true;
    }
}
