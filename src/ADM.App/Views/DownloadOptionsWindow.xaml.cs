using System.Windows;
using Adm.Core.Models;
using Adm.Core.Util;

namespace Adm.App.Views;

/// <summary>
/// Per-download overrides: speed limit, connection count, priority and an
/// optional checksum to verify the finished file against.
/// </summary>
public partial class DownloadOptionsWindow : Window
{
    private readonly DownloadItem _item;

    public DownloadOptionsWindow(DownloadItem item)
    {
        InitializeComponent();
        _item = item;

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
