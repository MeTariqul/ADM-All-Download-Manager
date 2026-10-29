using System.Text.RegularExpressions;

namespace Adm.Core.Util;

public static class SiteGrabber
{
    private static readonly Regex LinkPattern = new(@"<a[^>]+href\s*=\s*[""']([^""']+)[""']", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex FilePattern = new(@"\.(zip|rar|7z|mp4|mkv|avi|mp3|mpg|mpeg|exe|msi|pdf|jpg|jpeg|png|gif|webm|flv|mov|wmv)(\?|$)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static List<string> ExtractLinks(string html, string baseUrl)
    {
        var links = new List<string>();
        var matches = LinkPattern.Matches(html);
        foreach (Match match in matches)
        {
            string href = match.Groups[1].Value;
            if (string.IsNullOrWhiteSpace(href))
                continue;
            if (href.StartsWith("#") || href.StartsWith("javascript:") || href.StartsWith("mailto:"))
                continue;
            
            try
            {
                Uri uri;
                if (Uri.TryCreate(href, UriKind.Absolute, out uri))
                {
                    links.Add(uri.ToString());
                }
                else if (Uri.TryCreate(new Uri(baseUrl), href, out uri))
                {
                    links.Add(uri.ToString());
                }
            }
            catch { }
        }
        return links.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static List<string> FilterFiles(List<string> links)
    {
        return links.Where(l => FilePattern.IsMatch(l)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }
}
