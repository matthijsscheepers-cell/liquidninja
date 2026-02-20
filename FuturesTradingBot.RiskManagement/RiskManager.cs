namespace FuturesTradingBot.RiskManagement;

using FuturesTradingBot.Core.Models;

/// <summary>
/// Master risk manager - orchestrates all risk management components
/// This is the single entry point for all risk decisions
/// </summary>
public class RiskManager
{
    private readonly MasterCircuitBreaker circuitBreaker;
    private readonly RiskBudgetManager budgetManager;
    private readonly FuturesPositionSizer positionSizer;

    public RiskManager(
        AccountMode accountMode,
        decimal startingBalance,
        decimal currentBalance,
        decimal maxDailyLoss = 400m,
        decimal hardCap = 0m)
    {
        circuitBreaker = new MasterCircuitBreaker(accountMode, maxDailyLoss);
        budgetManager = new RiskBudgetManager(accountMode, startingBalance, currentBalance, hardCap);
        positionSizer = new FuturesPositionSizer(budgetManager);
    }

    /// <summary>
    /// Evaluate a trade setup and make final go/no-go decision
    /// This is the main method - checks everything!
    /// </summary>
    public TradeDecision EvaluateTrade(
        TradeSetup setup,
        DateTime currentTime,
        decimal currentBalance)
    {
        var decision = new TradeDecision
        {
            Setup = setup,
            Approved = false,
            Contracts = 0
        };

        // Update budget manager with current balance
        budgetManager.UpdateBalance(currentBalance);

        // STEP 1: Circuit Breaker Checks
        var circuitResult = circuitBreaker.CanTrade(currentTime);

        if (!circuitResult.CanTrade)
        {
            decision.Approved = false;
            decision.BlockedBy = circuitResult.BlockedBy;
            decision.Reasons.Add("Trade blocked by circuit breakers");
            return decision;
        }

        // Add any warnings
        decision.Warnings.AddRange(circuitResult.Warnings);

        // STEP 2: Position Sizing
        var sizeResult = positionSizer.CalculatePositionSize(setup, currentBalance);

        decision.Contracts = sizeResult.Contracts;
        decision.RiskPerContract = sizeResult.RiskPerContract;
        decision.TotalRisk = sizeResult.TotalRisk;
        decision.TotalReward = sizeResult.TotalReward;
        decision.RiskRewardRatio = sizeResult.RiskRewardRatio;

        if (!sizeResult.ShouldTrade)
        {
            decision.Approved = false;
            decision.Reasons.AddRange(sizeResult.Reasons);
            return decision;
        }

        // STEP 3: Final Validations

        // Check if setup itself is valid
        if (!setup.IsValid())
        {
            decision.Approved = false;
            decision.Reasons.Add("Trade setup is invalid (check entry/stop/target levels)");
            return decision;
        }

        // Cap contracts to fit within remaining daily loss budget
        var remainingDaily = circuitBreaker.dailyLoss.RemainingDailyBuffer;
        if (sizeResult.TotalRisk > remainingDaily && sizeResult.RiskPerContract > 0)
        {
            var maxByDaily = (int)(remainingDaily / sizeResult.RiskPerContract);
            if (maxByDaily <= 0)
            {
                decision.Approved = false;
                decision.Reasons.Add($"Would breach daily loss limit. " +
                    $"Current: ${circuitBreaker.dailyLoss.TodayLoss:F2}, " +
                    $"Risk/contract: ${sizeResult.RiskPerContract:F2}, " +
                    $"Remaining: ${remainingDaily:F2}");
                return decision;
            }
            // Reduce contracts to fit daily limit
            decision.Contracts = maxByDaily;
            decision.TotalRisk = sizeResult.RiskPerContract * maxByDaily;
            decision.TotalReward = (sizeResult.TotalReward / sizeResult.Contracts) * maxByDaily;
        }

        // Check potential profit against consistency rule (if Challenge)
        if (sizeResult.TotalReward > 0 &&
            circuitBreaker.consistency.WouldViolateConsistency(sizeResult.TotalReward, currentTime))
        {
            decision.Approved = false;
            decision.Reasons.Add("Would violate 40% consistency rule if trade wins");
            return decision;
        }

        // STEP 4: Populate Details
        var budgetStatus = budgetManager.GetStatus();

        decision.Details = new RiskManagementDetails
        {
            CurrentBalance = currentBalance,
            RemainingBuffer = budgetStatus.RemainingBuffer,
            TradeRiskBudget = sizeResult.AvailableRiskBudget,
            BufferUsedPercentage = budgetStatus.BufferUsedPercentage,
            MaxContractsByRisk = sizeResult.MaxContractsByRisk,
            MaxContractsByMargin = sizeResult.MaxContractsByMargin,
            MaxContractsByRules = sizeResult.MaxContractsByRules,
            AccountMode = budgetStatus.AccountMode
        };

        // ALL CHECKS PASSED!
        decision.Approved = true;
        decision.Reasons.Add($"Trade approved: {decision.Contracts} contracts");
        decision.Reasons.Add($"Risk: ${decision.TotalRisk:F2}, Reward: ${decision.TotalReward:F2}, RRR: {decision.RiskRewardRatio:F2}");

        return decision;
    }

    /// <summary>
    /// Record a trade result (must be called after trade closes!)
    /// </summary>
    public void RecordTradeResult(decimal pnl, bool isWin, DateTime? timestamp = null)
    {
        if (isWin)
        {
            circuitBreaker.RecordWin(pnl, timestamp);
        }
        else if (pnl == 0)
        {
            circuitBreaker.RecordBreakeven(timestamp);
        }
        else
        {
            circuitBreaker.RecordStopLoss(Math.Abs(pnl), timestamp);
        }
    }

    /// <summary>
    /// Get comprehensive risk status
    /// </summary>
    public RiskManagerStatus GetStatus(DateTime currentTime)
    {
        var budgetStatus = budgetManager.GetStatus();
        var circuitStatus = circuitBreaker.GetStatus(currentTime);

        return new RiskManagerStatus
        {
            Budget = budgetStatus,
            CircuitBreakers = circuitStatus,
            CanTradeNow = circuitBreaker.CanTrade(currentTime).CanTrade
        };
    }

    /// <summary>
    /// Update account mode (e.g., Challenge → Funded)
    /// </summary>
    public void UpdateAccountMode(AccountMode newMode, decimal newBalance, decimal hardCap = 0m)
    {
        // Would need to recreate components with new mode
        // For now, log a warning
        Console.WriteLine($"⚠️  Account mode change to {newMode} requires RiskManager restart");
    }

    // Expose internal components for advanced usage
    public MasterCircuitBreaker CircuitBreaker => circuitBreaker;
    public RiskBudgetManager BudgetManager => budgetManager;
    public FuturesPositionSizer PositionSizer => positionSizer;
}

/// <summary>
/// Complete risk manager status
/// </summary>
public class RiskManagerStatus
{
    public RiskBudgetStatus Budget { get; set; } = new();
    public MasterCircuitBreakerStatus CircuitBreakers { get; set; } = new();
    public bool CanTradeNow { get; set; }

    public override string ToString()
    {
        return $"Can Trade: {(CanTradeNow ? "✅ YES" : "❌ NO")}, {Budget}";
    }
}
