namespace FuturesTradingBot.RiskManagement;

using FuturesTradingBot.Core.Models;

/// <summary>
/// Final trade decision after all risk checks
/// Contains approval status, position sizing, and reasoning
/// </summary>
public class TradeDecision
{
    /// <summary>
    /// Is this trade approved?
    /// </summary>
    public bool Approved { get; set; }

    /// <summary>
    /// Number of contracts to trade (0 if rejected)
    /// </summary>
    public int Contracts { get; set; }

    /// <summary>
    /// Total risk in dollars
    /// </summary>
    public decimal TotalRisk { get; set; }

    /// <summary>
    /// Total potential reward in dollars
    /// </summary>
    public decimal TotalReward { get; set; }

    /// <summary>
    /// Risk per contract
    /// </summary>
    public decimal RiskPerContract { get; set; }

    /// <summary>
    /// Risk/Reward ratio
    /// </summary>
    public decimal RiskRewardRatio { get; set; }

    /// <summary>
    /// Original trade setup
    /// </summary>
    public TradeSetup Setup { get; set; } = null!;

    /// <summary>
    /// Reasons for approval/rejection
    /// </summary>
    public List<string> Reasons { get; set; } = new();

    /// <summary>
    /// Warnings (non-blocking issues)
    /// </summary>
    public List<string> Warnings { get; set; } = new();

    /// <summary>
    /// Which circuit breakers blocked (if any)
    /// </summary>
    public List<string> BlockedBy { get; set; } = new();

    /// <summary>
    /// Risk management details
    /// </summary>
    public RiskManagementDetails Details { get; set; } = new();

    public override string ToString()
    {
        if (!Approved)
        {
            var blockers = BlockedBy.Any()
                ? string.Join(", ", BlockedBy)
                : string.Join(", ", Reasons);
            return $"❌ REJECTED - {blockers}";
        }

        return $"✅ APPROVED - {Contracts} contracts, " +
               $"Risk: ${TotalRisk:F2}, " +
               $"Reward: ${TotalReward:F2}, " +
               $"RRR: {RiskRewardRatio:F2}";
    }
}

/// <summary>
/// Detailed risk management information
/// </summary>
public class RiskManagementDetails
{
    public decimal CurrentBalance { get; set; }
    public decimal RemainingBuffer { get; set; }
    public decimal TradeRiskBudget { get; set; }
    public decimal BufferUsedPercentage { get; set; }
    public int MaxContractsByRisk { get; set; }
    public int MaxContractsByMargin { get; set; }
    public int MaxContractsByRules { get; set; }
    public AccountMode AccountMode { get; set; }

    public override string ToString()
    {
        return $"Balance: ${CurrentBalance:F2}, " +
               $"Buffer: ${RemainingBuffer:F2} ({100 - BufferUsedPercentage:F1}% remaining), " +
               $"Trade Budget: ${TradeRiskBudget:F2}, " +
               $"Mode: {AccountMode}";
    }
}
