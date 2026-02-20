namespace FuturesTradingBot.Core.Indicators;

using FuturesTradingBot.Core.Models;

/// <summary>
/// Exponential Moving Average (EMA) calculator
/// </summary>
public class EMA
{
    /// <summary>
    /// Calculate EMA for a list of bars
    /// </summary>
    /// <param name="bars">List of price bars</param>
    /// <param name="period">EMA period (e.g., 21)</param>
    /// <returns>List of EMA values (same length as input, with nulls for warmup period)</returns>
    public static List<decimal?> Calculate(List<Bar> bars, int period)
    {
        // Validation
        if (bars == null || bars.Count == 0)
            throw new ArgumentException("Bars list cannot be null or empty");

        if (period < 1)
            throw new ArgumentException("Period must be greater than 0");

        var result = new List<decimal?>();

        // Not enough data for EMA
        if (bars.Count < period)
        {
            // Return all nulls
            for (int i = 0; i < bars.Count; i++)
                result.Add(null);
            return result;
        }

        // Calculate multiplier: 2 / (period + 1)
        decimal multiplier = 2.0m / (period + 1);

        // First EMA value is SMA (Simple Moving Average)
        decimal smaSum = 0;
        for (int i = 0; i < period; i++)
        {
            result.Add(null); // No EMA yet during warmup
            smaSum += bars[i].Close;
        }

        decimal ema = smaSum / period;
        result[period - 1] = ema; // First EMA value

        // Calculate rest of EMA values
        for (int i = period; i < bars.Count; i++)
        {
            // EMA = (Close - EMA_previous) * multiplier + EMA_previous
            ema = (bars[i].Close - ema) * multiplier + ema;
            result.Add(ema);
        }

        return result;
    }

    /// <summary>
    /// Calculate EMA and add to Bar metadata (in-place modification)
    /// </summary>
    public static void AddToBarList(List<Bar> bars, int period, string columnName = null)
    {
        if (columnName == null)
            columnName = $"ema_{period}";

        var emaValues = Calculate(bars, period);

        for (int i = 0; i < bars.Count; i++)
        {
            if (!bars[i].Metadata.ContainsKey(columnName))
                bars[i].Metadata[columnName] = emaValues[i];
        }
    }
}
