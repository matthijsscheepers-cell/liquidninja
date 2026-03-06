namespace FuturesTradingBot.App.LiveTrading;

using FuturesTradingBot.Core.Models;

/// <summary>
/// Accumulates context for a single trade from signal → fill → exit.
/// Written to trades_history.csv as one enriched row when the trade closes.
/// </summary>
public class TradeRecord
{
    // ── Identity ──────────────────────────────────────────────────────────────
    public string Asset { get; set; } = "";
    public SignalDirection Direction { get; set; }
    public string SetupType { get; set; } = "";

    // ── Entry context (captured at order-placement time) ──────────────────────
    public DateTime SignalTime { get; set; }
    public string OrderType { get; set; } = "";      // MARKET | LIMIT
    public decimal IntendedEma { get; set; }         // 15m EMA21 = intended limit price
    public decimal StopPrice { get; set; }
    public decimal TargetPrice { get; set; }
    public decimal Atr15m { get; set; }              // 15m ATR20 at signal time (price units)
    public decimal? Ema1h { get; set; }              // 1H EMA21 at signal time
    public string HistColor { get; set; } = "";      // TTM histogram color (dark_blue/blue/red/dark_red)
    public decimal? DistEmaAtr { get; set; }         // 1H price distance from EMA in ATR units
    public int Contracts { get; set; }

    // ── Session context ────────────────────────────────────────────────────────
    public string Session { get; set; } = "";        // ASIA | LONDON | NY | OFF
    public string DayOfWeek { get; set; } = "";
    public int UtcHour { get; set; }

    // ── Fill (set on confirmed entry fill) ─────────────────────────────────────
    public DateTime? EntryTime { get; set; }
    public decimal? FillPrice { get; set; }
    public decimal? OffsetPct { get; set; }          // (fill − intended_ema) / intended_ema × 100

    // ── Exit ───────────────────────────────────────────────────────────────────
    public DateTime? ExitTime { get; set; }
    public string? ExitType { get; set; }            // STOP_OUT | TARGET_HIT | EOD_CLOSE | RECONCILE_EST
    public decimal? ExitPrice { get; set; }
    public string? ExitQuality { get; set; }         // real | estimated
    public decimal? Pnl { get; set; }

    // ── Performance ────────────────────────────────────────────────────────────
    public decimal? RMultiple { get; set; }          // P&L / initial_risk_dollars
    public int? DurationMins { get; set; }           // minutes from entry fill to exit
    public int ConsecutiveStopsAtEntry { get; set; } // consecutive STOP_OUT streak before this trade

    // ── Helpers ────────────────────────────────────────────────────────────────

    /// <summary>True when both entry fill and exit price are recorded.</summary>
    public bool IsComplete => FillPrice.HasValue && ExitPrice.HasValue;

    /// <summary>Classify a UTC DateTime into a session label.</summary>
    public static string GetSession(DateTime utcTime)
    {
        int h = utcTime.Hour;
        if (h < 7)  return "ASIA";
        if (h < 13) return "LONDON";
        if (h < 22) return "NY";
        return "OFF";
    }

    /// <summary>Map internal exit reason strings to CSV exit_type labels.</summary>
    public static string MapExitType(string reason) => reason switch
    {
        "STOP"       => "STOP_OUT",
        "TARGET"     => "TARGET_HIT",
        "RECONCILE"  => "RECONCILE_EST",
        "EOD"        => "EOD_CLOSE",
        _            => reason
    };
}
