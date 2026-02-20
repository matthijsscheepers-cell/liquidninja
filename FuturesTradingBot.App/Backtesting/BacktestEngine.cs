namespace FuturesTradingBot.App.Backtesting;

using FuturesTradingBot.Core.Models;
using FuturesTradingBot.Core.Strategy;
using FuturesTradingBot.Core.Indicators;
using FuturesTradingBot.RiskManagement;

/// <summary>
/// Backtest engine - runs strategy on historical data
/// Fixed 1 contract per trade, tracks challenge-relevant metrics
/// </summary>
public class BacktestEngine
{
    private readonly BaseStrategy strategy;
    private readonly RiskManager riskManager;
    private readonly string asset;
    private readonly decimal multiplier;

    private static readonly Dictionary<string, decimal> Multipliers = new()
    {
        { "MGC", 10m },   // Micro Gold: $10 per $1 move
        { "MES", 5m },    // Micro E-mini S&P 500: $5 per $1 move
        { "MNQ", 2m },    // Micro E-mini Nasdaq: $2 per $1 move
        { "MYM", 0.5m },  // Micro E-mini Dow: $0.50 per $1 move
    };

    public BacktestEngine(
        BaseStrategy strategy,
        RiskManager riskManager,
        string asset)
    {
        this.strategy = strategy;
        this.riskManager = riskManager;
        this.asset = asset;
        this.multiplier = Multipliers.GetValueOrDefault(asset, 10m);
    }

    public BacktestResult Run(List<Bar> bars15Min, List<Bar> bars1Hour)
    {
        var result = new BacktestResult
        {
            Asset = asset,
            StartDate = bars15Min.First().Timestamp,
            EndDate = bars15Min.Last().Timestamp
        };

        Position? openPosition = null;
        decimal balance = 25000m;
        result.EquityCurve.Add(balance);

        int tradeNumber = 0;
        DateTime? lastTradeDate = null;
        int maxIdleDays = 0;

        Console.WriteLine($"\n  Running backtest: {bars15Min.Count} bars, {asset}, ${multiplier}/pt, 1 contract\n");

        for (int i = 0; i < bars15Min.Count; i++)
        {
            var currentBar = bars15Min[i];

            // Track max idle days (calendar days between trades)
            if (lastTradeDate.HasValue)
            {
                var idle = (currentBar.Timestamp.Date - lastTradeDate.Value.Date).Days;
                if (idle > maxIdleDays) maxIdleDays = idle;
            }

            var current1H = bars1Hour.LastOrDefault(b => b.Timestamp <= currentBar.Timestamp);
            if (current1H == null) continue;

            // Check for exit if we have open position
            if (openPosition != null)
            {
                var (exitAction, exitPrice) = strategy.ManageExit(
                    bars15Min.Take(i + 1).ToList(),
                    openPosition
                );

                if (exitAction != ExitAction.HOLD && exitPrice.HasValue)
                {
                    var trade = ClosePosition(openPosition, exitPrice.Value, exitAction, tradeNumber, currentBar.Timestamp);
                    result.Trades.Add(trade);

                    balance += trade.PnL;
                    result.EquityCurve.Add(balance);

                    riskManager.RecordTradeResult(trade.PnL, trade.PnL > 0, currentBar.Timestamp);
                    lastTradeDate = currentBar.Timestamp.Date;

                    // Set cooldown so strategy waits before re-entering
                    if (strategy is TTMSqueezePullbackStrategy ttmStrategy)
                        ttmStrategy.SetLastExitBar(i);

                    openPosition = null;
                }
            }

            // Check for new entry if no position
            if (openPosition == null)
            {
                var setup = strategy.CheckEntry(
                    bars15Min.Take(i + 1).ToList(),
                    bars1Hour.Where(b => b.Timestamp <= currentBar.Timestamp).ToList(),
                    "BACKTEST",
                    85m
                );

                if (setup != null && setup.IsValid())
                {
                    var decision = riskManager.EvaluateTrade(
                        setup,
                        currentBar.Timestamp,
                        balance
                    );

                    if (decision.Approved)
                    {
                        tradeNumber++;
                        openPosition = new Position
                        {
                            Asset = asset,
                            Direction = setup.Direction,
                            EntryPrice = setup.EntryPrice,
                            StopLoss = setup.StopLoss,
                            Target = setup.Target,
                            Contracts = decision.Contracts,
                            RiskPerContract = setup.RiskPerShare * multiplier,
                            EntryTime = currentBar.Timestamp,
                            EntryBar = i,
                            EntryStrategy = setup.SetupType
                        };

                        result.TradesApproved++;
                        lastTradeDate = currentBar.Timestamp.Date;
                    }
                    else
                    {
                        result.TradesRejected++;
                        var reason = decision.BlockedBy?.Count > 0
                            ? string.Join("; ", decision.BlockedBy)
                            : decision.Reasons?.Count > 0
                                ? decision.Reasons[0]
                                : "Unknown";
                        var key = reason.Length > 60 ? reason[..60] : reason;
                        if (!result.RejectionReasons.ContainsKey(key))
                            result.RejectionReasons[key] = 0;
                        result.RejectionReasons[key]++;
                    }
                }
            }
        }

        // Close any remaining position at last bar
        if (openPosition != null)
        {
            var lastBar = bars15Min.Last();
            var trade = ClosePosition(openPosition, lastBar.Close, ExitAction.TIME_EXIT, tradeNumber, lastBar.Timestamp);
            result.Trades.Add(trade);
            balance += trade.PnL;
            result.EquityCurve.Add(balance);
        }

        result.MaxIdleDays = maxIdleDays;
        result.CalculateMaxDrawdown();
        result.CalculateChallengeMetrics();

        return result;
    }

    private BacktestTrade ClosePosition(Position position, decimal exitPrice, ExitAction exitReason, int tradeNumber, DateTime exitTime)
    {
        decimal priceMove = position.Direction == SignalDirection.LONG
            ? exitPrice - position.EntryPrice
            : position.EntryPrice - exitPrice;

        decimal pnl = priceMove * multiplier * position.Contracts;

        return new BacktestTrade
        {
            TradeNumber = tradeNumber,
            EntryTime = position.EntryTime,
            ExitTime = exitTime,
            Direction = position.Direction,
            EntryPrice = position.EntryPrice,
            ExitPrice = exitPrice,
            Contracts = position.Contracts,
            PnL = pnl,
            ExitReason = exitReason,
            SetupType = position.EntryStrategy
        };
    }
}
