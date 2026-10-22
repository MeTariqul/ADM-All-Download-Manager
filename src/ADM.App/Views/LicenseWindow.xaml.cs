using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using Adm.Core.Licensing;

namespace Adm.App.Views;

/// <summary>
/// The activation screen. It is shown when ADM starts without a valid licence
/// and lets the user enter a key, see the device id and read the state.
/// </summary>
public partial class LicenseWindow : Window
{
    private readonly LicenseService _license;
    private bool _busy;

    public LicenseWindow(LicenseService license)
    {
        InitializeComponent();
        _license = license;

        DeviceIdText.Text = license.DeviceId;
        ShortIdText.Text = license.DeviceId.Length > 12
            ? license.DeviceId[..12].ToUpperInvariant()
            : license.DeviceId.ToUpperInvariant();

        // A build without a full app key can never be activated, so say so
        // instead of letting the user guess why the key is refused.
        AppKeyWarning.Visibility = LicenseConfiguration.AppKeyLooksComplete
            ? Visibility.Collapsed
            : Visibility.Visible;

        Loaded += OnLoaded;
    }

    /// <summary>True when the user asked to close the window without a licence.</summary>
    public bool UserCancelled { get; private set; }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        await RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        var status = await _license.CheckAsync();
        Apply(status);
    }

    private void Apply(LicenseStatus status)
    {
        StatusText.Text = status.Message;
        StatusText.Foreground = status.IsLicensed
            ? (System.Windows.Media.Brush)FindResource("SuccessBrush")
            : (System.Windows.Media.Brush)FindResource("DangerBrush");

        if (status.ExpiresAt is { } expiry)
            ExpiryText.Text = "Valid until " + expiry.LocalDateTime.ToString("d MMM yyyy");
        else
            ExpiryText.Text = string.Empty;

        ActivateButton.IsEnabled = !_busy;
    }

    private async void OnActivate(object sender, RoutedEventArgs e)
    {
        if (_busy)
            return;

        string key = KeyBox.Text.Trim();
        if (key.Length == 0)
        {
            StatusText.Text = "Enter a licence key.";
            return;
        }

        _busy = true;
        ActivateButton.IsEnabled = false;
        ActivateButton.Content = "Activating...";
        StatusText.Text = "Contacting the licence server...";

        try
        {
            var status = await _license.ActivateAsync(key);
            Apply(status);

            if (status.IsLicensed)
            {
                DialogResult = true;
                return;
            }
        }
        catch (Exception ex)
        {
            StatusText.Text = "The licence could not be activated: " + ex.Message;
        }
        finally
        {
            _busy = false;
            ActivateButton.IsEnabled = true;
            ActivateButton.Content = "Activate";
        }
    }

    private void OnCopyDeviceId(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(_license.DeviceId);
            StatusText.Text = "The device id was copied to the clipboard.";
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
        }
    }

    private void OnOpenWebsite(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(LicenseConfiguration.BaseUrl.Replace("/api/license", string.Empty))
            {
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
        }
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            OnActivate(sender, e);
            e.Handled = true;
        }
    }

    private void OnClose(object sender, RoutedEventArgs e)
    {
        UserCancelled = true;
        Close();
    }
}
