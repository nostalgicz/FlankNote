using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace FlankNote;

sealed record GitHubRelease(string TagName, string Name, string Body, string HtmlUrl, DateTimeOffset? PublishedAt);

static class GitHubUpdateService
{
    const string Endpoint = "https://api.github.cgom/repos/nostalgicz/FlankNote/releases/latest";
    const string FeedEndpoint = "https://github.com/nostalgicz/FlankNote/releases.atom";
    const string InstallerAssetName = "FlankNote-Setup-x64.exe";
    static readonly HttpClient Client = CreateClient();
    static readonly HttpClient DownloadClient = CreateDownloadClient();

    static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue(AppIdentity.EnglishName, "0.1"));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }

    static HttpClient CreateDownloadClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(20) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue(AppIdentity.EnglishName, "0.1"));
        return client;
    }

    public static async Task<GitHubRelease?> GetLatestAsync(CancellationToken cancellationToken = default)
    {
        Exception? apiFailure = null;
        try
        {
            using var response = await Client.GetAsync(Endpoint, cancellationToken).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
                return ParseApiRelease(json.RootElement);
            }

            apiFailure = new HttpRequestException(
                $"GitHub Releases API returned {(int)response.StatusCode} ({response.StatusCode}).");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            apiFailure = ex;
        }

        // The API is rate-limited for anonymous clients. The public Atom feed
        // provides the latest tag and release URL without an API token.
        try
        {
            return await GetLatestFromFeedAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception feedFailure)
        {
            throw apiFailure == null
                ? feedFailure
                : new AggregateException("Both GitHub release sources failed.", apiFailure, feedFailure);
        }
    }

    internal static GitHubRelease? ParseApiRelease(JsonElement root)
    {
        if (root.TryGetProperty("draft", out var draft) && draft.GetBoolean()) return null;
        if (root.TryGetProperty("prerelease", out var prerelease) && prerelease.GetBoolean()) return null;
        return new GitHubRelease(
            root.GetProperty("tag_name").GetString() ?? "",
            root.GetProperty("name").GetString() ?? "",
            root.GetProperty("body").GetString() ?? "",
            root.GetProperty("html_url").GetString() ?? "",
            root.TryGetProperty("published_at", out var published) && published.ValueKind == JsonValueKind.String
                ? published.GetDateTimeOffset() : null);
    }

    static async Task<GitHubRelease?> GetLatestFromFeedAsync(CancellationToken cancellationToken)
    {
        using var response = await Client.GetAsync(FeedEndpoint, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var document = await XDocument.LoadAsync(stream, LoadOptions.None, cancellationToken).ConfigureAwait(false);
        return ParseFeed(document);
    }

    internal static GitHubRelease? ParseFeed(XDocument document)
    {
        XNamespace atom = "http://www.w3.org/2005/Atom";
        var entry = document.Root?.Element(atom + "entry");
        if (entry == null) return null;

        var link = entry.Elements(atom + "link")
            .FirstOrDefault(item => string.Equals((string?)item.Attribute("rel"), "alternate", StringComparison.OrdinalIgnoreCase))
            ?.Attribute("href")?.Value ?? "";
        var tag = ExtractTag(entry.Element(atom + "id")?.Value, link);
        if (string.IsNullOrWhiteSpace(tag)) return null;

        var updated = entry.Element(atom + "updated")?.Value;
        return new GitHubRelease(
            tag,
            entry.Element(atom + "title")?.Value ?? tag,
            ToMarkdown(entry.Element(atom + "content")?.Value ?? ""),
            link,
            DateTimeOffset.TryParse(updated, out var date) ? date : null);
    }

    static string ExtractTag(string? id, string link)
    {
        var source = !string.IsNullOrWhiteSpace(link) ? link : id ?? "";
        var value = source.TrimEnd('/').Split('/').LastOrDefault() ?? "";
        return Uri.UnescapeDataString(value);
    }

    // The Atom fallback exposes rendered HTML rather than the original
    // release Markdown. Preserve the block structure that the in-app
    // renderer understands so rate-limit fallback does not flatten notes.
    static string ToMarkdown(string html)
    {
        var decoded = WebUtility.HtmlDecode(html);
        decoded = Regex.Replace(decoded, @"<br\s*/?>", "\n", RegexOptions.IgnoreCase);
        for (int level = 1; level <= 6; level++)
        {
            string heading = new string('#', level);
            decoded = Regex.Replace(decoded, $@"<h{level}\b[^>]*>", heading + " ", RegexOptions.IgnoreCase);
            decoded = Regex.Replace(decoded, $@"</h{level}\s*>", "\n", RegexOptions.IgnoreCase);
        }
        decoded = Regex.Replace(decoded, @"<li\b[^>]*>", "- ", RegexOptions.IgnoreCase);
        decoded = Regex.Replace(decoded, @"</(p|div|li|ul|ol|blockquote|pre)\s*>", "\n", RegexOptions.IgnoreCase);
        decoded = Regex.Replace(decoded, @"<hr\b[^>]*>", "\n---\n", RegexOptions.IgnoreCase);
        decoded = Regex.Replace(decoded, "<[^>]+>", "");
        decoded = WebUtility.HtmlDecode(decoded);

        var lines = decoded.Replace("\r\n", "\n").Replace('\r', '\n')
            .Split('\n')
            .Select(line => line.TrimEnd())
            .ToArray();
        return string.Join("\n", lines).Trim();
    }

    public static async Task<string> DownloadInstallerAsync(
        GitHubRelease release,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!Version.TryParse(release.TagName.Trim().TrimStart('v', 'V'), out _))
            throw new InvalidOperationException($"Unsupported release tag: {release.TagName}");

        var tag = Uri.EscapeDataString(release.TagName.Trim());
        var uri = new Uri($"https://github.com/nostalgicz/FlankNote/releases/download/{tag}/{InstallerAssetName}");
        var version = Regex.Replace(release.TagName.Trim(), "[^0-9A-Za-z._-]", "-");
        var destination = Path.Combine(Path.GetTempPath(), $"FlankNote-{version}-Setup-x64.exe");

        using var response = await DownloadClient.GetAsync(
            uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await SaveInstallerAsync(
            source, destination, response.Content.Headers.ContentLength, progress, cancellationToken).ConfigureAwait(false);
        return destination;
    }

    internal static async Task SaveInstallerAsync(
        Stream source,
        string destination,
        long? total,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var partial = destination + ".download";
        try
        {
            {
                await using var target = new FileStream(
                    partial, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
                var buffer = new byte[81920];
                long received = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    received += read;
                    if (total is > 0) progress?.Report((double)received / total.Value);
                }
                await target.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            // Windows cannot rename an open file. The nested await using above
            // must finish before promoting the completed partial download.
            File.Move(partial, destination, overwrite: true);
            progress?.Report(1);
        }
        catch
        {
            try { if (File.Exists(partial)) File.Delete(partial); }
            catch { }
            throw;
        }
    }

    public static bool IsNewer(string tagName)
    {
        var tag = tagName.Trim().TrimStart('v', 'V');
        var current = typeof(GitHubUpdateService).Assembly.GetName().Version ?? new Version(0, 0, 0);
        return Version.TryParse(tag, out var latest) && latest > current;
    }
}
