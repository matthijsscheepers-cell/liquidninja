namespace FuturesTradingBot.Core.Models;

/// <summary>
/// Represents a candlestick bar with OHLCV data
/// </summary>
public class Bar
{
    /// <summary>
    /// Timestamp of this bar
    /// </summary>
    public DateTime Timestamp { get; set; }

    /// <summary>
    /// Opening price
    /// </summary>
    public decimal Open { get; set; }

    /// <summary>
    /// Highest price during bar
    /// </summary>
    public decimal High { get; set; }

    /// <summary>
    /// Lowest price during bar
    /// </summary>
    public decimal Low { get; set; }

    /// <summary>
    /// Closing price
    /// </summary>
    public decimal Close { get; set; }

    /// <summary>
    /// Volume (number of contracts traded)
    /// </summary>
    public long Volume { get; set; }

    /// <summary>
    /// Symbol/Asset (e.g., "MGC", "MES")
    /// </summary>
    public string Symbol { get; set; } = string.Empty;

    /// <summary>
    /// Additional metadata (indicator values, etc.)
    /// </summary>
    public Dictionary<string, object?> Metadata { get; set; } = new();

    /// <summary>
    /// Constructor
    /// </summary>
    public Bar()
    {
    }

    /// <summary>
    /// Constructor with all values
    /// </summary>
    public Bar(DateTime timestamp, decimal open, decimal high,
               decimal low, decimal close, long volume, string symbol)
    {
        Timestamp = timestamp;
        Open = open;
        High = high;
        Low = low;
        Close = close;
        Volume = volume;
        Symbol = symbol;
    }

    /// <summary>
    /// Is this a bullish bar? (Close > Open)
    /// </summary>
    public bool IsBullish() => Close > Open;

    /// <summary>
    /// Is this a bearish bar? (Close < Open)
    /// </summary>
    public bool IsBearish() => Close < Open;

    /// <summary>
    /// Get the body size of the candle
    /// </summary>
    public decimal BodySize() => Math.Abs(Close - Open);

    /// <summary>
    /// Get the total range of the bar (High - Low)
    /// </summary>
    public decimal Range() => High - Low;

    public override string ToString()
    {
        return $"{Symbol} {Timestamp:yyyy-MM-dd HH:mm} " +
               $"O:{Open} H:{High} L:{Low} C:{Close} V:{Volume}";
    }
}
