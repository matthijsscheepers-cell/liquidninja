namespace FuturesTradingBot.RiskManagement;

using FuturesTradingBot.Core.Models;

/// <summary>
/// Calculate position sizes for futures contracts
/// Based on dynamic risk allocation and contract specifications
/// </summary>
public class FuturesPositionSizer
{
    private readonly RiskBudgetManager budgetManager;

    // Contract specifications
    private readonly Dictionary<string, ContractSpec> contractSpecs = new()
    {
        {
            "MGC", new ContractSpec
            {
                Symbol = "MGC",
                Name = "Gold Micro Futures",
                Multiplier = 10m,              // $10 per $1 move
                TickSize = 0.10m,              // $0.10 minimum move
                TickValue = 1.00m,             // $1 per tick
                TypicalMargin = 700m           // ~$700 margin per contract
            }
        },
        {
            "MES", new ContractSpec
            {
                Symbol = "MES",
                Name = "E-mini S&P Micro Futures",
                Multiplier = 5m,               // $5 per point
                TickSize = 0.25m,              // 0.25 point minimum move
                TickValue = 1.25m,             // $1.25 per tick
                TypicalMargin = 1100m          // ~$1100 margin per contract
            }
        }
    };

    public FuturesPositionSizer(RiskBudgetManager budgetManager)
    {
        this.budgetManager = budgetManager;
    }

    /// <summary>
    /// Calculate risk per contract in dollars
    /// </summary>
    public decimal CalculateRiskPerContract(string asset, decimal stopDistance)
    {
        if (!contractSpecs.ContainsKey(asset))
            throw new ArgumentException($"Unknown asset: {asset}");

        var spec = contractSpecs[asset];

        // Risk = Stop Distance × Multiplier
        // MGC: $20 stop × $10/$ = $200 risk
        // MES: 40 points stop × $5/pt = $200 risk
        return stopDistance * spec.Multiplier;
    }

    /// <summary>
    /// Calculate reward per contract in dollars
    /// </summary>
    public decimal CalculateRewardPerContract(string asset, decimal targetDistance)
    {
        if (!contractSpecs.ContainsKey(asset))
            throw new ArgumentException($"Unknown asset: {asset}");

        var spec = contractSpecs[asset];
        return targetDistance * spec.Multiplier;
    }

    /// <summary>
    /// Calculate position size (number of contracts)
    /// </summary>
    public PositionSizeResult CalculatePositionSize(
        TradeSetup setup,
        decimal currentBalance)
    {
        var result = new PositionSizeResult
        {
            Asset = setup.Asset,
            ShouldTrade = false,
            Contracts = 0,
            Reasons = new List<string>()
        };

        // Step 1: Calculate risk per contract
        var stopDistance = Math.Abs(setup.EntryPrice - setup.StopLoss);
        var riskPerContract = CalculateRiskPerContract(setup.Asset, stopDistance);

        result.RiskPerContract = riskPerContract;

        // Step 2: Get risk budget for this trade
        var budgetStatus = budgetManager.GetStatus();
        var riskMultiplier = budgetStatus.TradeRiskMultiplier;
        var maxRiskForTrade = budgetStatus.RemainingBuffer * riskMultiplier;

        result.AvailableRiskBudget = maxRiskForTrade;

        // Step 3: Calculate max contracts by risk
        var maxContractsByRisk = (int)(maxRiskForTrade / riskPerContract);

        // Step 4: Calculate max contracts by margin
        var spec = contractSpecs[setup.Asset];
        var availableMargin = currentBalance * 0.5m; // Use max 50% of balance for margin
        var maxContractsByMargin = (int)(availableMargin / spec.TypicalMargin);

        // Step 5: Apply position limits (Challenge: 20 micros, Funded: 30 micros)
        var maxContractsByRules = budgetStatus.AccountMode == AccountMode.Challenge ? 20 : 30;

        // Step 6: Take minimum of all constraints
        var contracts = Math.Min(maxContractsByRisk,
                        Math.Min(maxContractsByMargin, maxContractsByRules));

        // Fixed 1 contract - consistency over compounding
        // Challenge: pass safely without risking daily limit breach
        // Funded: steady payouts, no need to over-leverage
        contracts = Math.Min(contracts, 1);

        result.Contracts = Math.Max(0, contracts);
        result.MaxContractsByRisk = maxContractsByRisk;
        result.MaxContractsByMargin = maxContractsByMargin;
        result.MaxContractsByRules = maxContractsByRules;

        // Step 7: Calculate actual risk and reward
        if (result.Contracts > 0)
        {
            result.TotalRisk = riskPerContract * result.Contracts;

            var targetDistance = Math.Abs(setup.Target - setup.EntryPrice);
            var rewardPerContract = CalculateRewardPerContract(setup.Asset, targetDistance);
            result.TotalReward = rewardPerContract * result.Contracts;

            result.RiskRewardRatio = result.TotalRisk > 0
                ? result.TotalReward / result.TotalRisk
                : 0;
        }

        // Step 8: Validation checks
        if (result.Contracts == 0)
        {
            result.Reasons.Add($"Cannot afford even 1 contract. " +
                $"Risk per contract: ${riskPerContract:F2}, " +
                $"Available budget: ${maxRiskForTrade:F2}");
            return result;
        }

        // Check if single contract risk is reasonable
        var riskPercentOfBuffer = riskPerContract / budgetStatus.RemainingBuffer;
        if (riskPercentOfBuffer > 0.40m)
        {
            result.ShouldTrade = false;
            result.Contracts = 0;
            result.Reasons.Add($"Single contract risk too high: " +
                $"{riskPercentOfBuffer:P1} of remaining buffer. " +
                $"Max acceptable: 40%");
            return result;
        }

        // Check RRR
        if (result.RiskRewardRatio < 0.8m)
        {
            result.ShouldTrade = false;
            result.Contracts = 0;
            result.Reasons.Add($"Risk/Reward ratio too low: {result.RiskRewardRatio:F2}. " +
                "Minimum: 0.8");
            return result;
        }

        // All checks passed!
        result.ShouldTrade = true;
        result.Reasons.Add($"Trade approved: {result.Contracts} contracts, " +
            $"Risk: ${result.TotalRisk:F2}, " +
            $"Reward: ${result.TotalReward:F2}, " +
            $"RRR: {result.RiskRewardRatio:F2}");

        return result;
    }
}

/// <summary>
/// Contract specification
/// </summary>
public class ContractSpec
{
    public string Symbol { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public decimal Multiplier { get; set; }      // Dollar value per point/dollar move
    public decimal TickSize { get; set; }        // Minimum price movement
    public decimal TickValue { get; set; }       // Dollar value per tick
    public decimal TypicalMargin { get; set; }   // Typical margin requirement
}

/// <summary>
/// Position size calculation result
/// </summary>
public class PositionSizeResult
{
    public string Asset { get; set; } = string.Empty;
    public bool ShouldTrade { get; set; }
    public int Contracts { get; set; }
    public decimal RiskPerContract { get; set; }
    public decimal TotalRisk { get; set; }
    public decimal TotalReward { get; set; }
    public decimal RiskRewardRatio { get; set; }
    public decimal AvailableRiskBudget { get; set; }
    public int MaxContractsByRisk { get; set; }
    public int MaxContractsByMargin { get; set; }
    public int MaxContractsByRules { get; set; }
    public List<string> Reasons { get; set; } = new();

    public override string ToString()
    {
        if (!ShouldTrade)
            return $"❌ SKIP - {string.Join(", ", Reasons)}";

        return $"✅ TRADE - {Contracts} contracts, " +
               $"Risk: ${TotalRisk:F2}, " +
               $"Reward: ${TotalReward:F2}, " +
               $"RRR: {RiskRewardRatio:F2}";
    }
}
