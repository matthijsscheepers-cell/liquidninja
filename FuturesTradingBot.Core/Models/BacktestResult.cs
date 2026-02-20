namespace FuturesTradingBot.Core.Models;

/// <summary>
/// Complete backtest results with challenge-relevant metrics
/// </summary>
public class BacktestResult
{
    public DateTime StartDate { get; set; }
    public DateTime EndDate { get; set; }
    public string Asset { get; set; } = string.Empty;

    public List<BacktestTrade> Trades { get; set; } = new();
    public int TotalTrades => Trades.Count;
    public int WinningTrades => Trades.Count(t => t.PnL > 0);
    public int LosingTrades => Trades.Count(t => t.PnL < 0);
    public int BreakevenTrades => Trades.Count(t => t.PnL == 0);

    public decimal TotalPnL => Trades.Sum(t => t.PnL);
    public decimal WinRate => TotalTrades > 0 ? (decimal)WinningTrades / TotalTrades * 100 : 0;
    public decimal AverageWin => WinningTrades > 0 ? Trades.Where(t => t.PnL > 0).Average(t => t.PnL) : 0;
    public decimal AverageLoss => LosingTrades > 0 ? Trades.Where(t => t.PnL < 0).Average(t => t.PnL) : 0;
    public decimal ProfitFactor => Math.Abs(AverageLoss) > 0 ? Math.Abs(AverageWin / AverageLoss) : 0;

    public int TradesApproved { get; set; }
    public int TradesRejected { get; set; }
    public Dictionary<string, int> RejectionReasons { get; set; } = new();

    public List<decimal> EquityCurve { get; set; } = new();
    public decimal MaxDrawdown { get; set; }

    // Challenge-relevant metrics
    public int MaxIdleDays { get; set; }
    public int PullbackTrades { get; set; }
    public int TrendRideTrades { get; set; }
    public decimal LargestDailyLoss { get; set; }
    public decimal LargestSingleLoss { get; set; }
    public int ConsecutiveLossMax { get; set; }

    /// <summary>
    /// How many calendar days to reach a profit target
    /// </summary>
    public int? DaysToTarget(decimal target)
    {
        decimal cumPnL = 0;
        DateTime? firstTradeDate = null;

        foreach (var trade in Trades.OrderBy(t => t.EntryTime))
        {
            firstTradeDate ??= trade.EntryTime;
            cumPnL += trade.PnL;
            if (cumPnL >= target)
                return (trade.ExitTime - firstTradeDate.Value).Days;
        }

        return null;
    }

    public void CalculateMaxDrawdown()
    {
        if (EquityCurve.Count == 0) return;

        decimal peak = EquityCurve[0];
        decimal maxDD = 0;

        foreach (var equity in EquityCurve)
        {
            if (equity > peak) peak = equity;
            decimal dd = peak - equity;
            if (dd > maxDD) maxDD = dd;
        }

        MaxDrawdown = maxDD;
    }

    public void CalculateChallengeMetrics()
    {
        // Largest single loss
        LargestSingleLoss = Trades.Count > 0
            ? Math.Abs(Trades.Where(t => t.PnL < 0).DefaultIfEmpty().Min(t => t?.PnL ?? 0))
            : 0;

        // Largest daily loss
        var dailyLosses = Trades
            .GroupBy(t => t.EntryTime.Date)
            .Select(g => g.Sum(t => t.PnL))
            .Where(pnl => pnl < 0);
        LargestDailyLoss = dailyLosses.Any() ? Math.Abs(dailyLosses.Min()) : 0;

        // Max consecutive losses
        int currentStreak = 0;
        int maxStreak = 0;
        foreach (var trade in Trades.OrderBy(t => t.EntryTime))
        {
            if (trade.PnL < 0)
            {
                currentStreak++;
                if (currentStreak > maxStreak) maxStreak = currentStreak;
            }
            else
            {
                currentStreak = 0;
            }
        }
        ConsecutiveLossMax = maxStreak;

        // Trade type counts
        PullbackTrades = Trades.Count(t => t.SetupType.Contains("PULLBACK"));
        TrendRideTrades = Trades.Count(t => t.SetupType.Contains("TREND_RIDE"));
    }
}

/// <summary>
/// Single backtest trade
/// </summary>
public class BacktestTrade
{
    public int TradeNumber { get; set; }
    public DateTime EntryTime { get; set; }
    public DateTime ExitTime { get; set; }
    public SignalDirection Direction { get; set; }
    public decimal EntryPrice { get; set; }
    public decimal ExitPrice { get; set; }
    public int Contracts { get; set; }
    public decimal PnL { get; set; }
    public ExitAction ExitReason { get; set; }
    public string SetupType { get; set; } = string.Empty;
}
