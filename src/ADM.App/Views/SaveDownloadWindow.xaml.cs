using System.IO;
using System.Windows;
using Adm.Core.Models;
using Adm.Core.Settings;
using Adm.Core.Util;
using Microsoft.Win32;

namespace Adm.App.Views;

/// <summary>Collects a destination and optional transfer requirements before queueing a file.</summary>
public partial class SaveDownloadWindow : Window
{
    public SaveDownloadWindow(string url, string? suggestedName, AppSettings settings,
        DownloadTypePreference? preference)
    {
        InitializeComponent();
        UrlText.Text = url;

        string name = string.IsNullOrWhiteSpace(suggestedName)
            ? FileNameResolver.FromUrl(url)
            : Path.GetFileName(suggestedName);
        FileNameBox.Text = name;
        FolderBox.Text = preference?.Folder is { Length: > 0 } savedFolder
            ? savedFolder
            : settings.DownloadFolder;
        ConnectionsBox.Text = (preference?.Connections ?? settings.MaxConnections).ToString();
        SpeedLimitBox.Text = ((preference?.SpeedLimitBytesPerSecond ?? settings.SpeedLimitBytesPerSecond) / 1024).ToString();
        ChecksumAlgorithmBox.ItemsSource = new[] { "None", "MD5", "SHA1", "SHA256" };
        ChecksumAlgorithmBox.SelectedIndex = 0;
        UpdateTypeText();
        FileNameBox.TextChanged += (_, _) => UpdateTypeText();
    }

    public AddOptions? Options { get; private set; }
    public bool RememberForFileType => RememberCheck.IsChecked == true;
    public string FileTypeExtension => Path.GetExtension(FileNameBox.Text.Trim()).ToLowerInvariant();

    private void UpdateTypeText()
    {
        string extension = FileTypeExtension;
        TypeText.Text = string.IsNullOrWhiteSpace(extension)
            ? "A file extension in the name lets ADM save preferences for this type."
            : $"Preferences will apply to {extension} files.";
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Choose where to save this download" };
        if (Directory.Exists(FolderBox.Text))
            dialog.InitialDirectory = FolderBox.Text;
        if (dialog.ShowDialog(this) == true)
            FolderBox.Text = dialog.FolderName;
    }

    private void Start_Click(object sender, RoutedEventArgs e)
    {
        string name = FileNameBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(name) || Path.GetFileName(name) != name)
        {
            MessageBox.Show(this, "Enter a file name without a folder path.", "Review download",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        string folder = FolderBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(folder))
        {
            MessageBox.Show(this, "Choose a folder for this download.", "Review download",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!int.TryParse(ConnectionsBox.Text.Trim(), out int connections) || connections is < 1 or > 32)
        {
            MessageBox.Show(this, "Connections must be a number from 1 to 32.", "Review download",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!long.TryParse(SpeedLimitBox.Text.Trim(), out long limitKb) || limitKb < 0 || limitKb > 10_485_760)
        {
            MessageBox.Show(this, "Speed cap must be between 0 and 10,485,760 KB/s.", "Review download",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            folder = Path.GetFullPath(folder);
            Directory.CreateDirectory(folder);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "This folder cannot be used.\n\n" + ex.Message, "Review download",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        string checksum = ChecksumBox.Text.Trim();
        ChecksumAlgorithm algorithm = Checksum.ParseAlgorithm(ChecksumAlgorithmBox.SelectedItem as string);
        if (string.IsNullOrWhiteSpace(checksum))
            algorithm = ChecksumAlgorithm.None;
        else if (algorithm == ChecksumAlgorithm.None)
        {
            MessageBox.Show(this, "Choose the checksum algorithm for the expected checksum.", "Review download",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (algorithm != ChecksumAlgorithm.None && !IsValidChecksum(checksum, algorithm))
        {
            MessageBox.Show(this, $"Enter a valid {algorithm} checksum or choose None.", "Review download",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        Options = new AddOptions
        {
            Folder = folder,
            FileName = FileNameResolver.Sanitize(name),
            Connections = connections,
            SpeedLimitBytesPerSecond = limitKb * 1024,
            ExpectedChecksum = string.IsNullOrWhiteSpace(checksum) ? null : checksum,
            ChecksumAlgorithm = algorithm
        };
        DialogResult = true;
    }

    private static bool IsValidChecksum(string value, ChecksumAlgorithm algorithm)
    {
        int expectedLength = algorithm switch
        {
            ChecksumAlgorithm.Md5 => 32,
            ChecksumAlgorithm.Sha1 => 40,
            ChecksumAlgorithm.Sha256 => 64,
            _ => 0
        };
        return value.Length == expectedLength && value.All(Uri.IsHexDigit);
    }
}
