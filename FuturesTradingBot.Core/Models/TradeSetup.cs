namespace FuturesTradingBot.Core.Models;

/// <summary>
/// Represents a trading setup with entry, stop, and target
/// </summary>
public class TradeSetup
{
    /// <summary>
    /// Direction of the trade (LONG/SHORT)
    /// </summary>
    public SignalDirection Direction { get; set; }

    /// <summary>
    /// Entry price
    /// </summary>
    public decimal EntryPrice { get; set; }

    /// <summary>
    /// Stop loss price
    /// </summary>
    public decimal StopLoss { get; set; }

    /// <summary>
    /// Target/take profit price
    /// </summary>
    public decimal Target { get; set; }

    /// <summary>
    /// Risk per share/contract (entry - stop)
    /// </summary>
    public decimal RiskPerShare { get; set; }

    /// <summary>
    /// Confidence level (0-100)
    /// </summary>
    public decimal Confidence { get; set; }

    /// <summary>
    /// Setup type (e.g., "TTM_PULLBACK_LONG")
    /// </summary>
    public string SetupType { get; set; } = string.Empty;

    /// <summary>
    /// Asset symbol
    /// </summary>
    public string Asset { get; set; } = string.Empty;

    /// <summary>
    /// Additional metadata
    /// </summary>
    public Dictionary<string, object> Metadata { get; set; } = new();

    /// <summary>
    /// Calculate reward-to-risk ratio
    /// </summary>
    public decimal RewardRiskRatio()
    {
        if (RiskPerShare <= 0)
            return 0;

        var reward = Math.Abs(Target - EntryPrice);
        return reward / RiskPerShare;
    }

    /// <summary>
    /// Is this a valid setup?
    /// </summary>
    public bool IsValid()
    {
        // Basic validations
        if (Direction == SignalDirection.NONE)
            return false;

        if (EntryPrice <= 0 || StopLoss <= 0 || Target <= 0)
            return false;

        if (RiskPerShare <= 0)
            return false;

        // LONG: stop should be below entry, target above
        if (Direction == SignalDirection.LONG)
        {
            if (StopLoss >= EntryPrice) return false;
            if (Target <= EntryPrice) return false;
        }

        // SHORT: stop should be above entry, target below
        if (Direction == SignalDirection.SHORT)
        {
            if (StopLoss <= EntryPrice) return false;
            if (Target >= EntryPrice) return false;
        }

        return true;
    }

    public override string ToString()
    {
        return $"{SetupType} {Direction} {Asset} @ {EntryPrice:F2} " +
               $"(Stop: {StopLoss:F2}, Target: {Target:F2}, RRR: {RewardRiskRatio():F2})";
    }
}
