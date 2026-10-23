using System.Diagnostics;
using System.IO;
using System.Windows;

namespace Adm.App.Views;

public partial class ExtensionSetupWindow : Window
{
    private readonly string _extensionDirectory;
    private readonly string _guidePath;

    public ExtensionSetupWindow(string extensionDirectory)
    {
        InitializeComponent();
        _extensionDirectory = extensionDirectory;
        _guidePath = Path.Combine(extensionDirectory, "README.md");
        FolderPathBox.Text = extensionDirectory;
        FolderStatusText.Text = Directory.Exists(extensionDirectory)
            ? "Extension files are available in this folder."
            : "The extension folder is not present yet. Reinstall ADM or locate the project's extension folder.";
    }

    public static string ResolveExtensionDirectory()
    {
        string besideApp = Path.Combine(AppContext.BaseDirectory, "extension");
        if (Directory.Exists(besideApp))
            return Path.GetFullPath(besideApp);

        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "ADM.sln")))
            {
                string sourceExtension = Path.Combine(directory.FullName, "extension");
                if (Directory.Exists(sourceExtension))
                    return Path.GetFullPath(sourceExtension);
            }

            directory = directory.Parent;
        }

        return Path.GetFullPath(besideApp);
    }

    private void CopyPath_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(_extensionDirectory);
            FolderStatusText.Text = "Folder path copied to the clipboard.";
        }
        catch (Exception ex)
        {
            FolderStatusText.Text = "Could not copy the folder path: " + ex.Message;
        }
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e) => OpenPath(_extensionDirectory);

    private void OpenGuide_Click(object sender, RoutedEventArgs e)
    {
        if (File.Exists(_guidePath))
            OpenPath(_guidePath);
        else
            FolderStatusText.Text = "The setup guide was not found in the extension folder.";
    }

    private static void OpenPath(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show("Could not open this location.\n\n" + ex.Message, "ADM",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void Done_Click(object sender, RoutedEventArgs e) => Close();
}
