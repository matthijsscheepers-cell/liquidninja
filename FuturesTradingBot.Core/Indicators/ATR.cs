namespace FuturesTradingBot.Core.Indicators;

using FuturesTradingBot.Core.Models;

/// <summary>
/// Average True Range (ATR) calculator
/// </summary>
public class ATR
{
    /// <summary>
    /// Calculate True Range for each bar
    /// </summary>
    private static List<decimal> CalculateTrueRange(List<Bar> bars)
    {
        var trueRange = new List<decimal>();

        for (int i = 0; i < bars.Count; i++)
        {
            if (i == 0)
            {
                // First bar: just High - Low
                trueRange.Add(bars[i].High - bars[i].Low);
            }
            else
            {
                // True Range = max of:
                // 1. High - Low
                // 2. |High - Previous Close|
                // 3. |Low - Previous Close|

                var highLow = bars[i].High - bars[i].Low;
                var highPrevClose = Math.Abs(bars[i].High - bars[i - 1].Close);
                var lowPrevClose = Math.Abs(bars[i].Low - bars[i - 1].Close);

                var tr = Math.Max(highLow, Math.Max(highPrevClose, lowPrevClose));
                trueRange.Add(tr);
            }
        }

        return trueRange;
    }

    /// <summary>
    /// Calculate ATR for a list of bars
    /// </summary>
    /// <param name="bars">List of price bars</param>
    /// <param name="period">ATR period (e.g., 20)</param>
    /// <returns>List of ATR values (same length as input, with nulls for warmup period)</returns>
    public static List<decimal?> Calculate(List<Bar> bars, int period)
    {
        // Validation
        if (bars == null || bars.Count == 0)
            throw new ArgumentException("Bars list cannot be null or empty");

        if (period < 1)
            throw new ArgumentException("Period must be greater than 0");

        var result = new List<decimal?>();

        // Not enough data
        if (bars.Count < period)
        {
            for (int i = 0; i < bars.Count; i++)
                result.Add(null);
            return result;
        }

        // Calculate True Range for all bars
        var trueRange = CalculateTrueRange(bars);

        // First ATR is simple average of first N true ranges
        decimal atrSum = 0;
        for (int i = 0; i < period; i++)
        {
            result.Add(null); // No ATR yet during warmup
            atrSum += trueRange[i];
        }

        decimal atr = atrSum / period;
        result[period - 1] = atr; // First ATR value

        // Calculate rest using smoothed average (like EMA)
        // ATR = ((ATR_previous * (period - 1)) + TR_current) / period
        for (int i = period; i < bars.Count; i++)
        {
            atr = ((atr * (period - 1)) + trueRange[i]) / period;
            result.Add(atr);
        }

        return result;
    }

    /// <summary>
    /// Calculate ATR and add to Bar metadata
    /// </summary>
    public static void AddToBarList(List<Bar> bars, int period, string columnName = null)
    {
        if (columnName == null)
            columnName = $"atr_{period}";

        var atrValues = Calculate(bars, period);

        for (int i = 0; i < bars.Count; i++)
        {
            if (!bars[i].Metadata.ContainsKey(columnName))
                bars[i].Metadata[columnName] = atrValues[i];
        }
    }
}
