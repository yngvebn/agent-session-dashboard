using System.Globalization;
using System.Xml.Linq;

namespace AgentSessionDashboard.Services;

public record ClaudeIncident(string Id, string Title, string Status, string Link, DateTime PublishedAt);

/// <summary>
/// Holds the latest snapshot of the Claude status feed (status.claude.com).
/// The ClaudeStatusWorker refreshes it; /api/claude-status serves it. The feed
/// is fetched server-side because status.claude.com does not send CORS headers.
/// </summary>
public class ClaudeStatusService(IHttpClientFactory httpClientFactory, ILogger<ClaudeStatusService> logger)
{
    private const string FeedUrl = "https://status.claude.com/history.atom";
    private static readonly XNamespace Atom = "http://www.w3.org/2005/Atom";

    // Statuspage never leaves incidents unresolved for long; anything older than
    // this without a Resolved marker is feed noise, not an active incident.
    private static readonly TimeSpan MaxIncidentAge = TimeSpan.FromDays(7);

    // Ordered: when both appear, the one occurring FIRST in the content is the
    // most recent update (Statuspage lists updates newest-first).
    private static readonly string[] ActiveMarkers = ["Identified", "Investigating"];

    private readonly Lock _gate = new();
    private IReadOnlyList<ClaudeIncident> _incidents = [];
    private DateTime? _checkedAt;

    public object Snapshot()
    {
        lock (_gate)
        {
            return new
            {
                operational = _incidents.Count == 0,
                incidents = _incidents,
                checkedAt = _checkedAt,
            };
        }
    }

    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient("claude-status");
        var xml = await client.GetStringAsync(FeedUrl, cancellationToken);
        var feed = XDocument.Parse(xml);

        var now = DateTime.UtcNow;
        var active = new List<ClaudeIncident>();

        foreach (var entry in feed.Root?.Elements(Atom + "entry") ?? [])
        {
            var content = entry.Element(Atom + "content")?.Value ?? "";

            // An entry's content carries the incident's full update history, so a
            // Resolved marker anywhere means the incident is over.
            if (content.Contains("<strong>Resolved</strong>", StringComparison.OrdinalIgnoreCase))
                continue;

            var status = MostRecentMarker(content);
            if (status is null)
                continue;

            var published = DateTime.TryParse(
                entry.Element(Atom + "published")?.Value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                out var p) ? p : now;

            if (now - published > MaxIncidentAge)
                continue;

            active.Add(new ClaudeIncident(
                Id: entry.Element(Atom + "id")?.Value ?? "",
                Title: entry.Element(Atom + "title")?.Value?.Trim() ?? "Claude incident",
                Status: status,
                Link: entry.Elements(Atom + "link")
                    .FirstOrDefault(l => (string?)l.Attribute("rel") is null or "alternate")
                    ?.Attribute("href")?.Value ?? "https://status.claude.com",
                PublishedAt: published));
        }

        lock (_gate)
        {
            _incidents = active;
            _checkedAt = now;
        }

        if (active.Count > 0)
            logger.LogInformation("Claude status: {Count} active incident(s).", active.Count);
    }

    /// <summary>Matched on the &lt;strong&gt; status labels, not prose, so an update
    /// that merely mentions "investigating" in a sentence can't flip the state.</summary>
    private static string? MostRecentMarker(string content)
    {
        string? best = null;
        var bestIndex = int.MaxValue;
        foreach (var marker in ActiveMarkers)
        {
            var index = content.IndexOf($"<strong>{marker}</strong>", StringComparison.OrdinalIgnoreCase);
            if (index >= 0 && index < bestIndex)
            {
                bestIndex = index;
                best = marker;
            }
        }
        return best;
    }
}
