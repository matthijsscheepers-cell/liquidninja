namespace FuturesTradingBot.Core.Strategy;

using FuturesTradingBot.Core.Models;

/// <summary>
/// Abstract base class for all trading strategies
/// </summary>
public abstract class BaseStrategy
{
    /// <summary>
    /// Asset this strategy trades (MGC, MES, etc.)
    /// </summary>
    public string Asset { get; protected set; }

    /// <summary>
    /// Strategy name for logging/tracking
    /// </summary>
    public string StrategyName { get; protected set; }

    /// <summary>
    /// Strategy-specific parameters
    /// </summary>
    protected Dictionary<string, object> Parameters { get; set; }

    /// <summary>
    /// Constructor
    /// </summary>
    protected BaseStrategy(string asset, string strategyName)
    {
        Asset = asset;
        StrategyName = strategyName;
        Parameters = new Dictionary<string, object>();
        InitializeParameters();
    }

    /// <summary>
    /// Initialize strategy parameters (override in derived classes)
    /// </summary>
    protected abstract void InitializeParameters();

    /// <summary>
    /// Check for entry setup
    /// </summary>
    public abstract TradeSetup? CheckEntry(
        List<Bar> bars15min,
        List<Bar> bars1h,
        string regime,
        decimal confidence);

    /// <summary>
    /// Manage exit for open position
    /// </summary>
    public abstract (ExitAction action, decimal? price) ManageExit(
        List<Bar> bars,
        Position position);

    /// <summary>
    /// Set a strategy parameter (for sensitivity testing)
    /// </summary>
    public void SetParameter(string key, object value)
    {
        Parameters[key] = value;
    }

    /// <summary>
    /// Validate that required indicators are present
    /// </summary>
    protected void ValidateIndicators(Bar bar, params string[] requiredIndicators)
    {
        foreach (var indicator in requiredIndicators)
        {
            if (!bar.Metadata.ContainsKey(indicator))
            {
                throw new InvalidOperationException(
                    $"Required indicator '{indicator}' not found in bar data. " +
                    $"Make sure to call IndicatorHelper.AddAllIndicators() first.");
            }
        }
    }

    /// <summary>
    /// Get indicator value from bar
    /// </summary>
    protected decimal? GetIndicator(Bar bar, string name)
    {
        if (!bar.Metadata.ContainsKey(name))
            return null;

        var value = bar.Metadata[name];

        if (value == null)
            return null;

        if (value is decimal decValue)
            return decValue;

        // Try to convert
        if (decimal.TryParse(value.ToString(), out var parsed))
            return parsed;

        return null;
    }
}
