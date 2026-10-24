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
    private bool _refused;
    private bool _requestBusy;

    public LicenseWindow(LicenseService license, LicenseTrialStatus? trial = null)
    {
        InitializeComponent();
        _license = license;

        DeviceIdText.Text = license.DeviceId;
        ShortIdText.Text = license.DeviceId.Length > 12
            ? license.DeviceId[..12].ToUpperInvariant()
            : license.DeviceId.ToUpperInvariant();

        AppKeyBox.Text = AppKeyStore.Current;

        // The panel stays hidden until the server actually refuses a key. The
        // length of the app key says nothing about whether it is right, so a
        // warning based on it would be a guess.
        AppKeyWarning.Visibility = Visibility.Collapsed;

        if (trial is not null)
        {
            TrialPanel.Visibility = Visibility.Visible;
            if (trial.IsActive)
            {
                int days = Math.Max(1, (int)Math.Ceiling(trial.Remaining.TotalDays));
                TrialText.Text = $"Your seven-day trial is active. {days} day(s) remain, until " +
                                 $"{trial.EndsAt?.LocalDateTime.ToString("d MMM yyyy h:mm tt")}. " +
                                 "Activate a key any time, or choose Continue trial to use ADM.";
                CloseButton.Content = "Continue trial";
            }
            else
            {
                TrialText.Text = "Your seven-day trial has ended. Request a key below to keep using ADM.";
            }

            Closing += (_, _) =>
            {
                if (trial.IsActive)
                    UserCancelled = true;
            };
        }

        Loaded += OnLoaded;
    }

    /// <summary>True when the user asked to close the window without a licence.</summary>
    public bool UserCancelled { get; private set; }

    private async void OnLoaded(object sender, RoutedEventArgs e) =>
        await RefreshAsync();

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

        if (status.IsLicensed)
        {
            // A licence that the server confirmed needs no key box, no email
            // request and no app key editor. Only a thank you and the option to
            // remove the licence so a different key can be entered.
            ThankTitleText.Text = "Thank you for activating ADM.";
            ThankBodyText.Text = "Your licence is active and confirmed by the server.";
            ThankExpiryText.Text = status.ExpiresAt is { } exp
                ? "This licence is valid until " + exp.LocalDateTime.ToString("d MMM yyyy") + "."
                : "This licence does not expire.";
            ThankPanel.Visibility = Visibility.Visible;
            ActivatePanel.Visibility = Visibility.Collapsed;
            TrialPanel.Visibility = Visibility.Collapsed;
            AppKeyWarning.Visibility = Visibility.Collapsed;
            RemoveButton.Visibility = Visibility.Visible;
            ActivateButton.IsEnabled = false;
            return;
        }

        // Not licensed: show the activation panel again.
        ThankPanel.Visibility = Visibility.Collapsed;
        ActivatePanel.Visibility = Visibility.Visible;
        RemoveButton.Visibility = Visibility.Collapsed;

        // A refusal is the only reliable sign that the app key may be wrong,
        // because the server answers a bad licence key and a bad app key in
        // the same way.
        if (_refused)
        {
            AppKeyWarning.Visibility = Visibility.Visible;
            AppKeyWarningText.Text =
                "The licence server refused the key. It answers a wrong licence key and a " +
                "wrong app key in the same way, so check the app key of this build as well.";
        }

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
            _refused = status.State == LicenseState.Invalid;
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

    private async void OnRequestKey(object sender, RoutedEventArgs e)
    {
        if (_requestBusy)
            return;

        string email = EmailBox.Text.Trim();
        if (!System.Net.Mail.MailAddress.TryCreate(email, out _))
        {
            StatusText.Text = "Enter a valid email address to request a key.";
            return;
        }

        _requestBusy = true;
        RequestKeyButton.IsEnabled = false;
        RequestKeyButton.Content = "Sending...";
        try
        {
            LicenseRequestResult result = await _license.RequestKeyAsync(email);
            if (!result.Ok)
            {
                StatusText.Text = result.Error ?? "The key request could not be completed.";
            }
            else if (!string.IsNullOrWhiteSpace(result.Key))
            {
                KeyBox.Text = result.Key;
                StatusText.Text = "Your key is ready. Review it above and select Activate.";
            }
            else
            {
                StatusText.Text = "Request received. Check your email for the licence key.";
            }
        }
        catch (Exception ex)
        {
            StatusText.Text = "The key request failed: " + ex.Message;
        }
        finally
        {
            _requestBusy = false;
            RequestKeyButton.IsEnabled = true;
            RequestKeyButton.Content = "Request key";
        }
    }

    /// <summary>Stores the app key next to the executable.</summary>
    private void OnSaveAppKey(object sender, RoutedEventArgs e)
    {
        if (AppKeyStore.TrySave(AppKeyBox.Text, out string? error))
        {
            _refused = false;
            AppKeyWarning.Visibility = Visibility.Collapsed;
            StatusText.Text = "The app key was saved. Enter the licence key and activate.";
        }
        else
        {
            StatusText.Text = "The app key could not be saved: " + error;
        }
    }

    /// <summary>Removes the stored app key and falls back to the built in one.</summary>
    private void OnResetAppKey(object sender, RoutedEventArgs e)
    {
        if (AppKeyStore.TryClear(out string? error))
        {
            AppKeyBox.Text = AppKeyStore.Current;
            StatusText.Text = "The stored app key was removed.";
        }
        else
        {
            StatusText.Text = "The app key could not be removed: " + error;
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

    private async void OnRemoveLicence(object sender, RoutedEventArgs e)
    {
        _license.Clear();
        StatusText.Text = "The licence was removed. Enter a new key to activate.";
        ExpiryText.Text = string.Empty;
        ActivateButton.IsEnabled = true;
        ActivateButton.Content = "Activate";
        await RefreshAsync();
    }
}
