namespace FuturesTradingBot.App.LiveTrading;

using System.Globalization;
using System.Xml.Linq;

/// <summary>
/// Fetches the ForexFactory economic calendar XML feed and exposes a fast
/// IsBlackout() check for use in the live trading engine.
///
/// Coverage: all USD High-impact events (NFP, FOMC, CPI, PPI, Retail Sales, …).
/// Refresh: at startup and whenever the cached data is more than 12 hours old
///          (auto-triggered from the main trading loop).
///
/// On any HTTP or parse failure the service logs a warning and returns
/// IsBlackout = false — the bot continues trading without a news filter
/// rather than halting entirely.
/// </summary>
public class NewsCalendarService
{
    // ── Feed URLs ────────────────────────────────────────────────────────────
    private static readonly string[] FeedUrls =
    [
        "https://nfs.faireconomy.media/ff_calendar_thisweek.xml",
        "https://nfs.faireconomy.media/ff_calendar_nextweek.xml",
    ];

    // ── State ────────────────────────────────────────────────────────────────
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private readonly TradeLogger _logger;
    private List<NewsEvent> _events = [];
    private DateTime _lastRefresh = DateTime.MinValue;

    // Blackout window: enter 5 min before release, exit 30 min after.
    private const int PreMinutes  = 5;
    private const int PostMinutes = 30;

    public NewsCalendarService(TradeLogger logger)
    {
        _logger = logger;
    }

    // ── Public API ───────────────────────────────────────────────────────────

    /// <summary>
    /// Returns true when the current UTC wall-clock time falls inside a
    /// high-impact USD news window.  Sets <paramref name="reason"/> to the
    /// event title (e.g. "Non-Farm Employment Change") when returning true.
    /// </summary>
    public bool IsBlackout(out string reason)
    {
        var utcNow = DateTime.UtcNow;
        foreach (var ev in _events)
        {
            if (utcNow >= ev.UtcTime.AddMinutes(-PreMinutes) &&
                utcNow <  ev.UtcTime.AddMinutes(PostMinutes))
            {
                reason = ev.Title;
                return true;
            }
        }
        reason = "";
        return false;
    }

    /// <summary>
    /// Refresh the calendar if the cached data is more than 12 hours old.
    /// Call this from the main trading loop — it is a no-op most of the time.
    /// </summary>
    public async Task RefreshIfStaleAsync()
    {
        if ((DateTime.Now - _lastRefresh).TotalHours >= 12)
            await RefreshAsync();
    }

    /// <summary>Force-fetch both this-week and next-week feeds.</summary>
    public async Task RefreshAsync()
    {
        var allEvents = new List<NewsEvent>();

        foreach (var url in FeedUrls)
        {
            try
            {
                var xml = await _http.GetStringAsync(url);
                var parsed = ParseXml(xml);
                allEvents.AddRange(parsed);
            }
            catch (Exception ex)
            {
                _logger.LogStatus(DateTime.Now,
                    $"NEWS_CALENDAR: fetch failed for {url} — {ex.Message}");
            }
        }

        _events      = allEvents;
        _lastRefresh = DateTime.Now;

        int highUsd = _events.Count;
        _logger.LogStatus(DateTime.Now,
            $"NEWS_CALENDAR: {highUsd} high-impact USD events loaded" +
            (highUsd > 0
                ? $" (next: {_events.OrderBy(e => e.UtcTime).FirstOrDefault(e => e.UtcTime > DateTime.UtcNow)?.Title ?? "none"})"
                : " — calendar empty, blackout disabled"));
    }

    // ── XML parsing ──────────────────────────────────────────────────────────

    private static List<NewsEvent> ParseXml(string xml)
    {
        var events = new List<NewsEvent>();

        XDocument doc;
        try { doc = XDocument.Parse(xml); }
        catch { return events; }   // malformed XML — skip silently

        foreach (var el in doc.Descendants("event"))
        {
            string country = (el.Element("country")?.Value ?? "").Trim();
            string impact  = (el.Element("impact")?.Value ?? "").Trim();

            // Only USD high-impact events — covers NFP, FOMC, CPI, PPI, Retail Sales, …
            if (!country.Equals("USD", StringComparison.OrdinalIgnoreCase)) continue;
            if (!impact.Equals("High",  StringComparison.OrdinalIgnoreCase)) continue;

            string title   = (el.Element("title")?.Value   ?? "").Trim();
            string dateStr = (el.Element("date")?.Value    ?? "").Trim();  // MM-DD-YYYY
            string timeStr = (el.Element("time")?.Value    ?? "").Trim();  // e.g. "1:30pm" (UTC)

            if (!TryParseUtcDateTime(dateStr, timeStr, out DateTime utcTime)) continue;

            events.Add(new NewsEvent(title, utcTime));
        }

        return events;
    }

    private static bool TryParseUtcDateTime(string dateStr, string timeStr, out DateTime utcTime)
    {
        utcTime = default;

        // Parse date: MM-DD-YYYY
        if (!DateTime.TryParseExact(dateStr, "MM-dd-yyyy",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            return false;

        // Parse time: "1:30pm", "2:00pm", "12:00am", etc.
        // The faireconomy.media feed publishes times in UTC.
        // Normalise to uppercase so C# format "h:mmtt" matches.
        if (!TryParseTime(timeStr.ToUpperInvariant(), out var time))
            return false;

        // Combine date + time directly as UTC — no timezone conversion needed.
        utcTime = DateTime.SpecifyKind(date + time, DateTimeKind.Utc);
        return true;
    }

    private static bool TryParseTime(string s, out TimeSpan result)
    {
        result = default;
        if (string.IsNullOrWhiteSpace(s)) return false;

        // Formats: "8:30AM", "2:00PM", "12:00AM", "9AM", "All Day", "Tentative"
        if (DateTime.TryParseExact(s,
                new[] { "h:mmtt", "h:mmAM", "h:mmPM", "htt", "hAM", "hPM" },
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
        {
            result = dt.TimeOfDay;
            return true;
        }
        // "Tentative", "All Day", unknown strings — skip
        return false;
    }
}

/// <summary>A single high-impact USD news event with its UTC release time.</summary>
internal record NewsEvent(string Title, DateTime UtcTime);
