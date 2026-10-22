namespace Adm.Core.Licensing;

/// <summary>
/// The licence settings that ship with the app.
/// </summary>
public static class LicenseConfiguration
{
    /// <summary>
    /// The app key sent in the x-app-key header. It identifies this build to
    /// the licence server and is not a secret that the user ever sees.
    /// </summary>
    public const string AppKey = "appk_IzyQz0vxSltfXCdjHIH8yGZorb5XKwT9";

    /// <summary>The address of the licence API.</summary>
    public const string BaseUrl = LicenseClient.DefaultBaseUrl;

    /// <summary>
    /// The offline verification key. It can confirm a token but cannot create
    /// one, so it is safe to ship inside the app.
    /// </summary>
    public const string PublicKeyPem =
        "-----BEGIN PUBLIC KEY-----\n" +
        "MCowBQYDK2VwAyEAR0bqZuQw+019bOdFLy/d73sSs1k7I6s30vXOFNN9F68=\n" +
        "-----END PUBLIC KEY-----";

    /// <summary>
    /// The app key that is actually used. The value below can be overridden
    /// with the ADM_APP_KEY environment variable or a file called appkey.txt
    /// next to the executable, so the key can be completed without a rebuild.
    /// </summary>
    public static string EffectiveAppKey
    {
        get
        {
            string? fromEnvironment = Environment.GetEnvironmentVariable("ADM_APP_KEY");
            if (!string.IsNullOrWhiteSpace(fromEnvironment))
                return fromEnvironment.Trim();

            try
            {
                string file = Path.Combine(AppContext.BaseDirectory, "appkey.txt");
                if (File.Exists(file))
                {
                    string text = File.ReadAllText(file).Trim();
                    if (text.Length > 0)
                        return text;
                }
            }
            catch (Exception)
            {
                // A missing or unreadable file just means the default is used.
            }

            return AppKey;
        }
    }

    /// <summary>
    /// True when a full app key is available. A key that is shorter than this
    /// was left incomplete, and the licence server will refuse every request.
    /// </summary>
    public static bool AppKeyLooksComplete => EffectiveAppKey.Length >= 20;
}
