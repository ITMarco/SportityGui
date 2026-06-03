using AngleSharp;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Web;
using SportityGui.Models;

namespace SportityGui.Services;

public partial class ScraperService(IHttpClientFactory httpClientFactory)
{
    private static readonly Regex ChannelPattern =
        new(@"webapp\.sportity\.com/channel/([^/?#]+)", RegexOptions.Compiled);
    private static readonly Regex EventPattern =
        new(@"webapp\.sportity\.com/event/(?!text/)([^/?#]+)/([^/?#]+)", RegexOptions.Compiled);
    private static readonly Regex TextItemPattern =
        new(@"webapp\.sportity\.com/event/text/([^/?#]+)/([^/?#]+)/([^/?#]+)", RegexOptions.Compiled);

    public enum UrlMode { Unknown, Channel, Event }

    public static UrlMode DetectMode(string url)
    {
        if (EventPattern.IsMatch(url)) return UrlMode.Event;
        if (ChannelPattern.IsMatch(url)) return UrlMode.Channel;
        return UrlMode.Unknown;
    }

    public static (string channelCode, string eventId) ParseEventUrl(string url)
    {
        var m = EventPattern.Match(url);
        return m.Success ? (m.Groups[1].Value, m.Groups[2].Value) : (string.Empty, string.Empty);
    }

    public static string ParseChannelCode(string url)
    {
        var m = ChannelPattern.Match(url);
        return m.Success ? m.Groups[1].Value : string.Empty;
    }

    // ── Channel page ────────────────────────────────────────────────────────

    public async Task<SportityChannel> ScrapeChannelAsync(string url, CancellationToken ct = default)
    {
        var html = await FetchHtmlAsync(url, ct);
        var doc = await ParseHtmlAsync(html, url, ct);

        var channelCode = ParseChannelCode(url);
        var title = doc.Title?.Trim() ?? channelCode;

        var seen = new HashSet<string>();
        var events = new List<SportityEvent>();

        foreach (var anchor in doc.QuerySelectorAll("a").OfType<IHtmlAnchorElement>())
        {
            var href = anchor.Href ?? string.Empty;
            var m = EventPattern.Match(href);
            if (!m.Success) continue;

            var eventId = m.Groups[2].Value;
            if (!seen.Add(eventId)) continue;

            // The anchor itself may carry the name, or it may be a bare "View" button.
            // Walk up to the nearest card/section container and find the first heading inside it.
            var name = CleanText(anchor.TextContent);
            if (string.IsNullOrEmpty(name) || name.Length < 4)
                name = FindNearestHeading(anchor) ?? eventId;

            events.Add(new SportityEvent
            {
                Id = eventId,
                Name = name,
                ChannelCode = m.Groups[1].Value,
                Url = $"https://webapp.sportity.com/event/{m.Groups[1].Value}/{eventId}"
            });
        }

        // Single-event channel: page has no /event/ links but contains file/folder content directly.
        // Examples: channels that serve one event's documents at the channel URL itself.
        if (events.Count == 0)
        {
            var directItems = ParseLevel(FindContentRoot(doc), doc, channelCode, string.Empty);
            if (directItems.Count > 0)
            {
                events.Add(new SportityEvent
                {
                    Id = channelCode,
                    Name = title,
                    ChannelCode = channelCode,
                    Url = url,
                    Items = directItems
                });
            }
        }

        return new SportityChannel { Code = channelCode, Name = title, Url = url, Events = events };
    }

    /// Walk up the DOM from <paramref name="anchor"/> looking for the nearest h1-h4 sibling or ancestor heading.
    private static string? FindNearestHeading(IElement anchor)
    {
        // Walk up to a container element (div, section, article, li)
        var container = anchor.ParentElement;
        while (container != null)
        {
            var tag = container.TagName.ToUpperInvariant();
            if (tag is "DIV" or "SECTION" or "ARTICLE" or "LI" or "TD")
            {
                // Look for a heading inside this container
                var heading = container.QuerySelector("h1,h2,h3,h4");
                var text = CleanText(heading?.TextContent ?? string.Empty);
                if (!string.IsNullOrEmpty(text)) return text;
            }
            container = container.ParentElement;
        }
        return null;
    }

    // ── Event page ───────────────────────────────────────────────────────────

    public async Task<(List<TreeItem> Items, string PageTitle)> ScrapeEventAsync(string url, CancellationToken ct = default)
    {
        var html = await FetchHtmlAsync(url, ct);
        var doc = await ParseHtmlAsync(html, url, ct);
        var (channelCode, eventId) = ParseEventUrl(url);
        var items = ParseLevel(FindContentRoot(doc), doc, channelCode, eventId);
        var title = CleanText(doc.Title ?? string.Empty);
        return (items, title);
    }

    // ── Text content ─────────────────────────────────────────────────────────

    public async Task<string?> ExtractTextContentAsync(string html)
    {
        var doc = await ParseHtmlAsync(html, null);
        var body = doc.QuerySelector(".message-body, .content, article, main");
        return CleanText(body?.TextContent ?? doc.Body?.TextContent ?? string.Empty);
    }

    // ── Core recursive parser ────────────────────────────────────────────────

    /// <summary>
    /// Recursively parse items from a container element.
    /// scope == null means root (body/main); scope.Id starting with "parent-" means a collapse div.
    /// </summary>
    private static List<TreeItem> ParseLevel(
        IElement? scope, IDocument doc, string channelCode, string eventId)
    {
        if (scope == null) return [];

        var items = new List<TreeItem>();
        var processedCollapseIds = new HashSet<string>();

        foreach (var anchor in scope.QuerySelectorAll("a").OfType<IHtmlAnchorElement>())
        {
            // Determine whether this anchor belongs to our scope level.
            // An anchor "belongs" here if its nearest [id^="parent-"] ancestor IS scope
            // (or null, when scope is the root container).
            var nearestParentCollapse = anchor.Closest("[id^='parent-']");

            bool isAtOurLevel = scope.Id?.StartsWith("parent-") == true
                ? nearestParentCollapse?.Id == scope.Id
                : nearestParentCollapse == null;

            if (!isAtOurLevel) continue;

            var href = anchor.GetAttribute("href") ?? string.Empty;

            if (href.StartsWith("#parent-"))
            {
                // ── Folder toggle ──
                var collapseId = href[1..];                       // "parent-{uuid}"
                if (!processedCollapseIds.Add(collapseId)) continue;

                var uuid = collapseId["parent-".Length..];
                var name = CleanText(anchor.TextContent);
                if (string.IsNullOrEmpty(name)) name = uuid;

                var collapseDiv = doc.QuerySelector("#" + collapseId);
                var folder = new FolderItem { Id = uuid, Name = name };
                folder.Children.AddRange(ParseLevel(collapseDiv, doc, channelCode, eventId));
                items.Add(folder);
            }
            else if (anchor.Href.Contains("app-cdn.sportity.com", StringComparison.OrdinalIgnoreCase))
            {
                // ── File item ──
                items.Add(MakeFileItem(anchor));
            }
            else if (anchor.Href.Contains("/event/text/", StringComparison.OrdinalIgnoreCase)
                     || (!href.StartsWith("#") && !href.StartsWith("mailto")
                         && !string.IsNullOrEmpty(eventId)
                         && href.Contains(eventId, StringComparison.OrdinalIgnoreCase)))
            {
                // ── Text / message item ──
                var name = CleanText(anchor.TextContent);
                if (string.IsNullOrEmpty(name)) continue;
                items.Add(new TextItem
                {
                    Id = ExtractLastSegment(href),
                    Name = name,
                    ContentUrl = anchor.Href
                });
            }
        }

        return items;
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static IElement FindContentRoot(IDocument doc)
    {
        // Prefer the element that contains the most folder toggle links
        var candidates = new[] { "main", ".container", ".content", "#content", "body" };
        IElement best = doc.Body!;
        int bestCount = 0;

        foreach (var sel in candidates)
        {
            var el = doc.QuerySelector(sel);
            if (el == null) continue;
            var count = el.QuerySelectorAll("a[href^='#parent-']").Length
                      + el.QuerySelectorAll("a[href*='app-cdn.sportity.com']").Length;
            if (count > bestCount) { best = el; bestCount = count; }
        }

        return best;
    }

    private static FileItem MakeFileItem(IHtmlAnchorElement anchor)
    {
        var displayName = CleanText(anchor.TextContent);
        var cdnUrl = anchor.Href;

        // Extract UUID and filename from CDN URL:
        // https://app-cdn.sportity.com/{org-uuid}/{file-uuid}_{filename}.ext
        var lastSegment = cdnUrl.Split('/').LastOrDefault() ?? string.Empty;
        var underscoreIdx = lastSegment.IndexOf('_');

        var fileUuid = underscoreIdx > 0 ? lastSegment[..underscoreIdx] : Guid.NewGuid().ToString();
        var rawFileName = underscoreIdx > 0
            ? HttpUtility.UrlDecode(lastSegment[(underscoreIdx + 1)..])
            : lastSegment;

        if (string.IsNullOrEmpty(displayName))
            displayName = System.IO.Path.GetFileNameWithoutExtension(rawFileName);

        var ext = System.IO.Path.GetExtension(rawFileName).TrimStart('.').ToLowerInvariant();

        return new FileItem
        {
            Id = fileUuid,
            Name = displayName,
            DownloadUrl = cdnUrl,
            FileExtension = ext
        };
    }

    private static string ExtractLastSegment(string url) =>
        url.TrimEnd('/').Split('/').LastOrDefault() ?? Guid.NewGuid().ToString();

    private static string CleanText(string raw) =>
        System.Text.RegularExpressions.Regex.Replace(raw.Trim(), @"\s+", " ");

    private static async Task<IDocument> ParseHtmlAsync(string html, string? baseUrl = null, CancellationToken ct = default)
    {
        var context = BrowsingContext.New(Configuration.Default);
        return await context.OpenAsync(req =>
        {
            if (baseUrl != null) req.Address(baseUrl);
            req.Content(html);
        }, ct);
    }

    public Task<string> FetchPublicAsync(string url, CancellationToken ct = default) =>
        FetchHtmlAsync(url, ct);

    private async Task<string> FetchHtmlAsync(string url, CancellationToken ct)
    {
        var client = httpClientFactory.CreateClient("sportity");
        var response = await client.GetAsync(url, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(ct);
    }
}
