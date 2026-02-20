namespace FuturesTradingBot.Core.Indicators;

using FuturesTradingBot.Core.Models;

/// <summary>
/// TTM Momentum (Histogram) calculator
/// Used for determining histogram color (light_blue, dark_blue, yellow, red)
/// </summary>
public class TTMMomentum
{
    /// <summary>
    /// Calculate TTM Momentum values
    /// </summary>
    /// <param name="bars">List of price bars</param>
    /// <param name="length">Period for calculation (default 34)</param>
    /// <returns>List of momentum values</returns>
    public static List<decimal?> Calculate(List<Bar> bars, int length = 34)
    {
        // Validation
        if (bars == null || bars.Count == 0)
            throw new ArgumentException("Bars list cannot be null or empty");

        if (length < 1)
            throw new ArgumentException("Length must be greater than 0");

        var result = new List<decimal?>();

        // Not enough data
        if (bars.Count < length)
        {
            for (int i = 0; i < bars.Count; i++)
                result.Add(null);
            return result;
        }

        // TTM Momentum calculation
        for (int i = 0; i < bars.Count; i++)
        {
            if (i < length)
            {
                result.Add(null); // Not enough data yet
                continue;
            }

            // Get highest high and lowest low over period
            decimal highestHigh = decimal.MinValue;
            decimal lowestLow = decimal.MaxValue;

            for (int j = i - length + 1; j <= i; j++)
            {
                if (bars[j].High > highestHigh)
                    highestHigh = bars[j].High;
                if (bars[j].Low < lowestLow)
                    lowestLow = bars[j].Low;
            }

            // Calculate midpoint (Donchian Channel middle)
            decimal midpoint = (highestHigh + lowestLow) / 2.0m;

            // Momentum = Close - Midpoint
            decimal momentum = bars[i].Close - midpoint;

            result.Add(momentum);
        }

        return result;
    }

    /// <summary>
    /// Determine histogram bar color based on momentum
    /// </summary>
    /// <param name="currentMomentum">Current bar momentum</param>
    /// <param name="previousMomentum">Previous bar momentum</param>
    /// <returns>Color string: "light_blue", "dark_blue", "yellow", or "red"</returns>
    public static string GetHistogramColor(decimal? currentMomentum, decimal? previousMomentum)
    {
        // If either is null, default to red (most conservative)
        if (!currentMomentum.HasValue || !previousMomentum.HasValue)
            return "red";

        var current = currentMomentum.Value;
        var previous = previousMomentum.Value;

        // Light Blue: momentum > 0 AND rising
        if (current > 0 && current > previous)
            return "light_blue";

        // Dark Blue: momentum > 0 AND falling
        if (current > 0)
            return "dark_blue";

        // Yellow: momentum <= 0 AND rising
        if (current > previous)
            return "yellow";

        // Red: momentum <= 0 AND falling
        return "red";
    }

    /// <summary>
    /// Calculate TTM Momentum and add to Bar metadata
    /// </summary>
    public static void AddToBarList(List<Bar> bars, int length = 34, string columnName = "ttm_momentum")
    {
        var momentumValues = Calculate(bars, length);

        for (int i = 0; i < bars.Count; i++)
        {
            if (!bars[i].Metadata.ContainsKey(columnName))
                bars[i].Metadata[columnName] = momentumValues[i];
        }
    }
}
