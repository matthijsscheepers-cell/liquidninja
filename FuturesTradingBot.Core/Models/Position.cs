namespace FuturesTradingBot.Core.Models;

/// <summary>
/// Represents an open trading position
/// </summary>
public class Position
{
    /// <summary>
    /// Unique position ID
    /// </summary>
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>
    /// Asset symbol (MGC, MES, etc.)
    /// </summary>
    public string Asset { get; set; } = string.Empty;

    /// <summary>
    /// Direction (LONG/SHORT)
    /// </summary>
    public SignalDirection Direction { get; set; }

    /// <summary>
    /// Entry price
    /// </summary>
    public decimal EntryPrice { get; set; }

    /// <summary>
    /// Current stop loss price
    /// </summary>
    public decimal StopLoss { get; set; }

    /// <summary>
    /// Target price
    /// </summary>
    public decimal Target { get; set; }

    /// <summary>
    /// Number of contracts
    /// </summary>
    public int Contracts { get; set; }

    /// <summary>
    /// Risk per contract in dollars
    /// </summary>
    public decimal RiskPerContract { get; set; }

    /// <summary>
    /// Entry timestamp
    /// </summary>
    public DateTime EntryTime { get; set; }

    /// <summary>
    /// Entry bar index (for tracking)
    /// </summary>
    public int EntryBar { get; set; }

    /// <summary>
    /// Maximum R-multiple achieved (for tracking)
    /// </summary>
    public decimal MaxR { get; set; }

    /// <summary>
    /// Which strategy opened this position
    /// </summary>
    public string EntryStrategy { get; set; } = string.Empty;

    /// <summary>
    /// Additional metadata
    /// </summary>
    public Dictionary<string, object> Metadata { get; set; } = new();

    /// <summary>
    /// Calculate current P&L in dollars
    /// </summary>
    public decimal CalculatePnL(decimal currentPrice)
    {
        var priceMove = Direction == SignalDirection.LONG
            ? currentPrice - EntryPrice
            : EntryPrice - currentPrice;

        // For futures: multiply by contract multiplier
        // MGC: $10 per $1 move
        // MES: $5 per point move
        var multiplier = Asset == "MGC" ? 10m : 5m;

        return priceMove * multiplier * Contracts;
    }

    /// <summary>
    /// Calculate current R-multiple
    /// </summary>
    public decimal CalculateRMultiple(decimal currentPrice)
    {
        var pnl = CalculatePnL(currentPrice);
        var totalRisk = RiskPerContract * Contracts;

        if (totalRisk == 0)
            return 0;

        return pnl / totalRisk;
    }

    /// <summary>
    /// Should stop loss be hit at this price?
    /// </summary>
    public bool IsStopHit(decimal currentPrice)
    {
        if (Direction == SignalDirection.LONG)
            return currentPrice <= StopLoss;
        else
            return currentPrice >= StopLoss;
    }

    /// <summary>
    /// Should target be hit at this price?
    /// </summary>
    public bool IsTargetHit(decimal currentPrice)
    {
        if (Direction == SignalDirection.LONG)
            return currentPrice >= Target;
        else
            return currentPrice <= Target;
    }

    public override string ToString()
    {
        return $"Position {Id} - {Asset} {Direction} {Contracts}x @ {EntryPrice:F2} " +
               $"(Stop: {StopLoss:F2}, Target: {Target:F2})";
    }
}
