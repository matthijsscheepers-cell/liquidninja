namespace FuturesTradingBot.Core.Indicators;

using FuturesTradingBot.Core.Models;

/// <summary>
/// Helper class to add multiple indicators to bar data
/// </summary>
public static class IndicatorHelper
{
    /// <summary>
    /// Add all indicators needed for TTM Pullback Strategy
    /// </summary>
    public static void AddAllIndicators(List<Bar> bars)
    {
        // EMA 9 (fast EMA for trend ride entries)
        EMA.AddToBarList(bars, 9, "ema_9");

        // EMA 21 (entry level)
        EMA.AddToBarList(bars, 21, "ema_21");

        // ATR 20 (for stops/targets)
        ATR.AddToBarList(bars, 20, "atr_20");

        // TTM Momentum (for histogram color)
        TTMMomentum.AddToBarList(bars, 34, "ttm_momentum");
    }

    /// <summary>
    /// Check if all required indicators are present
    /// </summary>
    public static bool HasAllIndicators(Bar bar)
    {
        return bar.Metadata.ContainsKey("ema_21") &&
               bar.Metadata.ContainsKey("atr_20") &&
               bar.Metadata.ContainsKey("ttm_momentum");
    }

    /// <summary>
    /// Get indicator value from bar metadata
    /// </summary>
    public static decimal? GetIndicatorValue(Bar bar, string indicatorName)
    {
        if (!bar.Metadata.ContainsKey(indicatorName))
            return null;

        var value = bar.Metadata[indicatorName];

        if (value == null)
            return null;

        if (value is decimal decimalValue)
            return decimalValue;

        // decimal? is already covered by the decimal pattern above
        // since nullable decimals unbox to decimal

        // Try to convert
        if (decimal.TryParse(value.ToString(), out var parsed))
            return parsed;

        return null;
    }
}
