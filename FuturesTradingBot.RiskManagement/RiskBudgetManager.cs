namespace FuturesTradingBot.RiskManagement;

using FuturesTradingBot.Core.Models;

/// <summary>
/// Manages risk budget - how much can we afford to lose?
/// </summary>
public class RiskBudgetManager
{
    private readonly AccountMode accountMode;
    private readonly decimal startingBalance;
    private decimal currentBalance;
    private readonly decimal hardCap; // For post-payout accounts

    public RiskBudgetManager(
        AccountMode mode,
        decimal startingBalance,
        decimal currentBalance,
        decimal hardCap = 0)
    {
        this.accountMode = mode;
        this.startingBalance = startingBalance;
        this.currentBalance = currentBalance;
        this.hardCap = hardCap;
    }

    /// <summary>
    /// Get maximum total loss allowed
    /// </summary>
    public decimal GetMaxTotalLoss()
    {
        switch (accountMode)
        {
            case AccountMode.Challenge:
                // FundedNext Challenge: $1000 max loss from start
                return 1000m;

            case AccountMode.FundedPrePayout:
                // Funded pre-payout: typically similar to challenge
                return 1000m;

            case AccountMode.FundedPostPayout:
                // Post-payout: max DD = current balance - hard cap
                return currentBalance - hardCap;

            default:
                return 1000m;
        }
    }

    /// <summary>
    /// Get remaining loss buffer (how much more can we lose?)
    /// </summary>
    public decimal GetRemainingLossBuffer()
    {
        var maxLoss = GetMaxTotalLoss();
        var currentDrawdown = startingBalance - currentBalance;
        var remaining = maxLoss - currentDrawdown;

        // Can't be negative
        return Math.Max(0, remaining);
    }

    /// <summary>
    /// How many "average" losses can we afford?
    /// </summary>
    public decimal GetAffordableLossCount(decimal avgLossSize)
    {
        if (avgLossSize <= 0)
            return 0;

        var buffer = GetRemainingLossBuffer();
        return buffer / avgLossSize;
    }

    /// <summary>
    /// Get recommended risk percentage for next trade
    /// Based on Kelly Criterion-like approach
    /// </summary>
    public decimal GetTradeRiskMultiplier()
    {
        var buffer = GetRemainingLossBuffer();
        var maxLoss = GetMaxTotalLoss();

        // If buffer is getting low, be more conservative
        var bufferPercentage = maxLoss > 0 ? buffer / maxLoss : 0;

        switch (accountMode)
        {
            case AccountMode.Challenge:
                // Challenge: Need profit, but can't blow account
                // MGC risk/contract is ~$230, so need at least 25% of $1000 buffer
                if (bufferPercentage > 0.7m) return 0.25m;      // >70% buffer: aggressive
                if (bufferPercentage > 0.4m) return 0.15m;      // 40-70%: moderate
                if (bufferPercentage > 0.2m) return 0.08m;      // 20-40%: conservative
                return 0.03m;                                    // <20%: very conservative

            case AccountMode.FundedPrePayout:
                // Funded: Steady approach
                if (bufferPercentage > 0.7m) return 0.10m;
                if (bufferPercentage > 0.5m) return 0.08m;
                return 0.05m;

            case AccountMode.FundedPostPayout:
                // Post-payout with hard cap: VERY conservative
                if (bufferPercentage > 0.5m) return 0.05m;
                if (bufferPercentage > 0.3m) return 0.03m;
                return 0.02m;

            default:
                return 0.10m;
        }
    }

    /// <summary>
    /// Update current balance (called after trades)
    /// </summary>
    public void UpdateBalance(decimal newBalance)
    {
        currentBalance = newBalance;
    }

    /// <summary>
    /// Get current status
    /// </summary>
    public RiskBudgetStatus GetStatus()
    {
        return new RiskBudgetStatus
        {
            AccountMode = accountMode,
            StartingBalance = startingBalance,
            CurrentBalance = currentBalance,
            MaxTotalLoss = GetMaxTotalLoss(),
            RemainingBuffer = GetRemainingLossBuffer(),
            BufferUsedPercentage = GetBufferUsedPercentage(),
            TradeRiskMultiplier = GetTradeRiskMultiplier()
        };
    }

    private decimal GetBufferUsedPercentage()
    {
        var maxLoss = GetMaxTotalLoss();
        if (maxLoss == 0) return 0;

        var used = maxLoss - GetRemainingLossBuffer();
        return (used / maxLoss) * 100m;
    }
}

/// <summary>
/// Risk budget status snapshot
/// </summary>
public class RiskBudgetStatus
{
    public AccountMode AccountMode { get; set; }
    public decimal StartingBalance { get; set; }
    public decimal CurrentBalance { get; set; }
    public decimal MaxTotalLoss { get; set; }
    public decimal RemainingBuffer { get; set; }
    public decimal BufferUsedPercentage { get; set; }
    public decimal TradeRiskMultiplier { get; set; }

    public override string ToString()
    {
        return $"Mode: {AccountMode}, Balance: ${CurrentBalance:F2}, " +
               $"Buffer: ${RemainingBuffer:F2} ({100 - BufferUsedPercentage:F1}% remaining), " +
               $"Risk Multiplier: {TradeRiskMultiplier:P1}";
    }
}
