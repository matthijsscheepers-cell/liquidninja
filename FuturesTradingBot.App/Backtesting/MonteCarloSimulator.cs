namespace FuturesTradingBot.App.Backtesting;

/// <summary>
/// Monte Carlo simulation to estimate realistic max drawdown distribution
/// Shuffles actual trade P&Ls to find how lucky/unlucky the backtest sequence was
/// </summary>
public static class MonteCarloSimulator
{
    public static MonteCarloResult Run(List<decimal> tradePnLs, int iterations = 1000)
    {
        var maxDrawdowns = new List<decimal>(iterations);

        for (int i = 0; i < iterations; i++)
        {
            var shuffled = tradePnLs.OrderBy(_ => Random.Shared.Next()).ToList();
            maxDrawdowns.Add(CalculateMaxDrawdown(shuffled));
        }

        maxDrawdowns.Sort();

        var p95Index = (int)(iterations * 0.95);
        var p99Index = (int)(iterations * 0.99);

        return new MonteCarloResult
        {
            Iterations = iterations,
            AverageMaxDD = maxDrawdowns.Average(),
            MedianMaxDD = maxDrawdowns[iterations / 2],
            Percentile95 = maxDrawdowns[Math.Min(p95Index, iterations - 1)],
            Percentile99 = maxDrawdowns[Math.Min(p99Index, iterations - 1)],
            WorstCase = maxDrawdowns.Last(),
            BestCase = maxDrawdowns.First(),
            ActualBacktestDD = CalculateMaxDrawdown(tradePnLs)
        };
    }

    private static decimal CalculateMaxDrawdown(List<decimal> pnls)
    {
        decimal equity = 0;
        decimal peak = 0;
        decimal maxDD = 0;

        foreach (var pnl in pnls)
        {
            equity += pnl;
            if (equity > peak) peak = equity;
            var dd = peak - equity;
            if (dd > maxDD) maxDD = dd;
        }

        return maxDD;
    }
}

public class MonteCarloResult
{
    public int Iterations { get; set; }
    public decimal AverageMaxDD { get; set; }
    public decimal MedianMaxDD { get; set; }
    public decimal Percentile95 { get; set; }
    public decimal Percentile99 { get; set; }
    public decimal WorstCase { get; set; }
    public decimal BestCase { get; set; }
    public decimal ActualBacktestDD { get; set; }

    public string Verdict =>
        ActualBacktestDD < AverageMaxDD
            ? "Your backtest DD was BETTER than average - expect worse live"
            : "Your backtest DD was WORSE than average - typical or unlucky sequence";
}
