namespace FuturesTradingBot.App.Backtesting;

using FuturesTradingBot.Core.Models;

/// <summary>
/// Post-hoc analysis of backtest results: costs, slippage scenarios,
/// worst-case analysis, and fill quality simulation
/// </summary>
public static class RealityCheckAnalyzer
{
    // Commission per contract per side (IBKR Tiered)
    private const decimal CommissionPerSide = 0.85m;
    private const decimal CommissionRoundTrip = CommissionPerSide * 2;

    // Slippage per fill (entry + exit = round trip)
    private static readonly Dictionary<string, decimal> SlippagePerFill = new()
    {
        { "MGC", 10m },   // 1 tick × $10 multiplier... user-specified $10/fill
        { "MES", 1.25m }, // 1 tick × $5 × 0.25pt = $1.25/fill
    };

    /// <summary>
    /// Calculate total execution cost per round-trip trade
    /// slippageMultiplier: 1.0 = normal, 2.0 = worst case, etc.
    /// </summary>
    public static decimal GetRoundTripCost(string asset, decimal slippageMultiplier = 1.0m)
    {
        var slippagePerFill = SlippagePerFill.GetValueOrDefault(asset, 10m);
        var totalSlippage = slippagePerFill * 2 * slippageMultiplier; // entry + exit
        return totalSlippage + CommissionRoundTrip;
    }

    /// <summary>
    /// Apply costs to a backtest result and return adjusted metrics
    /// </summary>
    public static CostAdjustedResult ApplyCosts(BacktestResult result, string asset, decimal slippageMultiplier = 1.0m)
    {
        var costPerTrade = GetRoundTripCost(asset, slippageMultiplier);
        var totalCosts = costPerTrade * result.TotalTrades;

        // Recalculate with costs deducted per trade
        var adjustedPnLs = result.Trades
            .Select(t => t.PnL - costPerTrade)
            .ToList();

        // Recalculate equity curve
        decimal equity = 0;
        decimal peak = 0;
        decimal maxDD = 0;
        foreach (var pnl in adjustedPnLs)
        {
            equity += pnl;
            if (equity > peak) peak = equity;
            var dd = peak - equity;
            if (dd > maxDD) maxDD = dd;
        }

        var wins = adjustedPnLs.Count(p => p > 0);
        var losses = adjustedPnLs.Count(p => p < 0);
        var avgWin = wins > 0 ? adjustedPnLs.Where(p => p > 0).Average() : 0;
        var avgLoss = losses > 0 ? adjustedPnLs.Where(p => p < 0).Average() : 0;

        return new CostAdjustedResult
        {
            SlippageMultiplier = slippageMultiplier,
            CostPerTrade = costPerTrade,
            TotalCosts = totalCosts,
            GrossPnL = result.TotalPnL,
            NetPnL = adjustedPnLs.Sum(),
            NetWinRate = result.TotalTrades > 0 ? (decimal)wins / result.TotalTrades * 100 : 0,
            NetProfitFactor = Math.Abs(avgLoss) > 0 ? Math.Abs(avgWin / avgLoss) : 0,
            NetMaxDrawdown = maxDD,
            NetAvgWin = avgWin,
            NetAvgLoss = avgLoss,
            StillProfitable = adjustedPnLs.Sum() > 0
        };
    }

    /// <summary>
    /// Run 4 slippage scenarios and find break-even slippage
    /// </summary>
    public static SlippageSensitivityResult RunSlippageScenarios(BacktestResult result, string asset)
    {
        var scenarios = new (string Name, decimal Multiplier)[]
        {
            ("No slippage", 0m),
            ("Normal (1 tick)", 1.0m),
            ("Worst (2 ticks)", 2.0m),
            ("Extreme (3 ticks)", 3.0m),
        };

        var results = scenarios
            .Select(s => (s.Name, Result: ApplyCosts(result, asset, s.Multiplier)))
            .ToList();

        // Find break-even slippage multiplier (binary search)
        decimal lo = 0, hi = 20;
        for (int i = 0; i < 20; i++)
        {
            var mid = (lo + hi) / 2;
            var adj = ApplyCosts(result, asset, mid);
            if (adj.NetPnL > 0)
                lo = mid;
            else
                hi = mid;
        }

        return new SlippageSensitivityResult
        {
            Scenarios = results,
            BreakEvenSlippageMultiplier = (lo + hi) / 2,
            BreakEvenCostPerTrade = GetRoundTripCost(asset, (lo + hi) / 2)
        };
    }

    /// <summary>
    /// Analyze worst-case scenarios from trade history
    /// </summary>
    public static WorstCaseResult AnalyzeWorstCase(BacktestResult result)
    {
        var trades = result.Trades.OrderBy(t => t.EntryTime).ToList();
        if (trades.Count == 0) return new WorstCaseResult();

        // Find top 3 worst losing streaks
        var streaks = new List<LosingStreak>();
        int streakStart = -1;
        int streakCount = 0;
        decimal streakLoss = 0;

        for (int i = 0; i < trades.Count; i++)
        {
            if (trades[i].PnL < 0)
            {
                if (streakCount == 0) streakStart = i;
                streakCount++;
                streakLoss += trades[i].PnL;
            }
            else
            {
                if (streakCount > 0)
                {
                    streaks.Add(new LosingStreak
                    {
                        Count = streakCount,
                        TotalLoss = Math.Abs(streakLoss),
                        StartDate = trades[streakStart].EntryTime,
                        EndDate = trades[i - 1].ExitTime
                    });
                }
                streakCount = 0;
                streakLoss = 0;
            }
        }
        // Capture streak at end
        if (streakCount > 0)
        {
            streaks.Add(new LosingStreak
            {
                Count = streakCount,
                TotalLoss = Math.Abs(streakLoss),
                StartDate = trades[streakStart].EntryTime,
                EndDate = trades.Last().ExitTime
            });
        }

        var topStreaks = streaks.OrderByDescending(s => s.TotalLoss).Take(3).ToList();

        // Worst day
        var dailyPnL = trades.GroupBy(t => t.EntryTime.Date)
            .Select(g => (Date: g.Key, PnL: g.Sum(t => t.PnL)))
            .OrderBy(d => d.PnL)
            .ToList();

        // Worst week (ISO week)
        var weeklyPnL = trades.GroupBy(t =>
            {
                var d = t.EntryTime;
                return new { d.Year, Week = System.Globalization.CultureInfo.InvariantCulture.Calendar.GetWeekOfYear(d, System.Globalization.CalendarWeekRule.FirstDay, DayOfWeek.Monday) };
            })
            .Select(g => (Key: $"{g.Key.Year}-W{g.Key.Week:D2}", PnL: g.Sum(t => t.PnL)))
            .OrderBy(w => w.PnL)
            .ToList();

        return new WorstCaseResult
        {
            TopLosingStreaks = topStreaks,
            WorstDayPnL = dailyPnL.First().PnL,
            WorstDayDate = dailyPnL.First().Date,
            WorstWeekPnL = weeklyPnL.First().PnL,
            WorstWeekLabel = weeklyPnL.First().Key,
            BestDayPnL = dailyPnL.Last().PnL,
            BestDayDate = dailyPnL.Last().Date
        };
    }

    /// <summary>
    /// Simulate realistic fill quality
    /// 70% limit fill, 20% miss, 10% market order (worse fill)
    /// </summary>
    public static FillQualityResult SimulateFillQuality(BacktestResult result, string asset, int iterations = 500)
    {
        var costPerTrade = GetRoundTripCost(asset, 1.0m);
        var marketOrderExtraCost = SlippagePerFill.GetValueOrDefault(asset, 10m); // 1 extra tick

        var adjustedPnLs = new List<decimal>(iterations);
        var adjustedCounts = new List<int>(iterations);
        var adjustedWinRates = new List<decimal>(iterations);

        for (int iter = 0; iter < iterations; iter++)
        {
            decimal totalPnL = 0;
            int filled = 0;
            int wins = 0;

            foreach (var trade in result.Trades)
            {
                var roll = Random.Shared.NextDouble();

                if (roll < 0.70)
                {
                    // Limit fill at EMA (normal cost)
                    var net = trade.PnL - costPerTrade;
                    totalPnL += net;
                    filled++;
                    if (net > 0) wins++;
                }
                else if (roll < 0.90)
                {
                    // Missed fill - skip trade
                }
                else
                {
                    // Market order fill - extra slippage
                    var net = trade.PnL - costPerTrade - marketOrderExtraCost;
                    totalPnL += net;
                    filled++;
                    if (net > 0) wins++;
                }
            }

            adjustedPnLs.Add(totalPnL);
            adjustedCounts.Add(filled);
            adjustedWinRates.Add(filled > 0 ? (decimal)wins / filled * 100 : 0);
        }

        return new FillQualityResult
        {
            AvgFillRate = adjustedCounts.Average() / result.TotalTrades * 100,
            AvgTradeCount = (int)adjustedCounts.Average(),
            AvgNetPnL = adjustedPnLs.Average(),
            AvgWinRate = adjustedWinRates.Average(),
            WorstCasePnL = adjustedPnLs.Min(),
            BestCasePnL = adjustedPnLs.Max()
        };
    }
}

// ═══ Result Types ═══

public class CostAdjustedResult
{
    public decimal SlippageMultiplier { get; set; }
    public decimal CostPerTrade { get; set; }
    public decimal TotalCosts { get; set; }
    public decimal GrossPnL { get; set; }
    public decimal NetPnL { get; set; }
    public decimal NetWinRate { get; set; }
    public decimal NetProfitFactor { get; set; }
    public decimal NetMaxDrawdown { get; set; }
    public decimal NetAvgWin { get; set; }
    public decimal NetAvgLoss { get; set; }
    public bool StillProfitable { get; set; }
}

public class SlippageSensitivityResult
{
    public List<(string Name, CostAdjustedResult Result)> Scenarios { get; set; } = new();
    public decimal BreakEvenSlippageMultiplier { get; set; }
    public decimal BreakEvenCostPerTrade { get; set; }
}

public class WorstCaseResult
{
    public List<LosingStreak> TopLosingStreaks { get; set; } = new();
    public decimal WorstDayPnL { get; set; }
    public DateTime WorstDayDate { get; set; }
    public decimal WorstWeekPnL { get; set; }
    public string WorstWeekLabel { get; set; } = "";
    public decimal BestDayPnL { get; set; }
    public DateTime BestDayDate { get; set; }
}

public class LosingStreak
{
    public int Count { get; set; }
    public decimal TotalLoss { get; set; }
    public DateTime StartDate { get; set; }
    public DateTime EndDate { get; set; }

    public override string ToString() =>
        $"{Count} losses, -${TotalLoss:F2} ({StartDate:MMM dd} - {EndDate:MMM dd})";
}

public class FillQualityResult
{
    public double AvgFillRate { get; set; }
    public int AvgTradeCount { get; set; }
    public decimal AvgNetPnL { get; set; }
    public decimal AvgWinRate { get; set; }
    public decimal WorstCasePnL { get; set; }
    public decimal BestCasePnL { get; set; }
}
