namespace FuturesTradingBot.Execution;

using FuturesTradingBot.Core.Models;

/// <summary>
/// Aggregates 5-second bars → 1-minute → 15-minute → 1-hour bars
/// </summary>
public class BarAggregator
{
    private readonly string symbol;

    private List<Bar> bars1Min = new();
    private List<Bar> bars15Min = new();
    private List<Bar> bars1Hour = new();

    private Bar? current1Min = null;
    private Bar? current15Min = null;
    private Bar? current1Hour = null;

    private bool new15MinBarCompleted;
    private bool new1HourBarCompleted;

    public List<Bar> Bars1Min => bars1Min;
    public List<Bar> Bars15Min => bars15Min;
    public List<Bar> Bars1Hour => bars1Hour;

    /// <summary>
    /// True when a new 15-min bar just completed. Resets to false after reading.
    /// </summary>
    public bool New15MinBarCompleted
    {
        get
        {
            var val = new15MinBarCompleted;
            new15MinBarCompleted = false;
            return val;
        }
    }

    /// <summary>
    /// True when a new 1-hour bar just completed. Resets to false after reading.
    /// </summary>
    public bool New1HourBarCompleted
    {
        get
        {
            var val = new1HourBarCompleted;
            new1HourBarCompleted = false;
            return val;
        }
    }

    public BarAggregator(string symbol)
    {
        this.symbol = symbol;
    }

    /// <summary>
    /// Seed with historical bars (for warmup). Does not trigger completion flags.
    /// </summary>
    public void SeedBars(List<Bar> historicalBars15m, List<Bar> historicalBars1h)
    {
        bars15Min.AddRange(historicalBars15m);
        bars1Hour.AddRange(historicalBars1h);
    }

    /// <summary>
    /// Add a 5-second real-time bar, aggregates up through all timeframes
    /// </summary>
    public void Add5SecBar(DateTime time, decimal open, decimal high, decimal low, decimal close, long volume)
    {
        bool isNew1Min = current1Min == null || time.Minute != current1Min.Timestamp.Minute;

        if (isNew1Min)
        {
            // Complete the previous 1-min bar
            if (current1Min != null)
            {
                Add1MinBar(current1Min);
            }

            current1Min = new Bar
            {
                Timestamp = RoundTo1Min(time),
                Open = open,
                High = high,
                Low = low,
                Close = close,
                Volume = volume,
                Symbol = symbol
            };
        }
        else
        {
            current1Min!.High = Math.Max(current1Min.High, high);
            current1Min.Low = Math.Min(current1Min.Low, low);
            current1Min.Close = close;
            current1Min.Volume += volume;
        }
    }

    /// <summary>
    /// Add 1-minute bar and aggregate to higher timeframes
    /// </summary>
    public void Add1MinBar(Bar bar)
    {
        bars1Min.Add(bar);
        Aggregate15Min(bar);
        Aggregate1Hour(bar);
    }

    private void Aggregate15Min(Bar bar)
    {
        bool isNew15Min = bar.Timestamp.Minute % 15 == 0;

        if (isNew15Min || current15Min == null)
        {
            if (current15Min != null)
            {
                bars15Min.Add(current15Min);
                new15MinBarCompleted = true;
            }

            current15Min = new Bar
            {
                Timestamp = RoundTo15Min(bar.Timestamp),
                Open = bar.Open,
                High = bar.High,
                Low = bar.Low,
                Close = bar.Close,
                Volume = bar.Volume,
                Symbol = symbol
            };
        }
        else
        {
            current15Min!.High = Math.Max(current15Min.High, bar.High);
            current15Min.Low = Math.Min(current15Min.Low, bar.Low);
            current15Min.Close = bar.Close;
            current15Min.Volume += bar.Volume;
        }
    }

    private void Aggregate1Hour(Bar bar)
    {
        bool isNew1Hour = bar.Timestamp.Minute == 0;

        if (isNew1Hour || current1Hour == null)
        {
            if (current1Hour != null)
            {
                bars1Hour.Add(current1Hour);
                new1HourBarCompleted = true;
            }

            current1Hour = new Bar
            {
                Timestamp = RoundTo1Hour(bar.Timestamp),
                Open = bar.Open,
                High = bar.High,
                Low = bar.Low,
                Close = bar.Close,
                Volume = bar.Volume,
                Symbol = symbol
            };
        }
        else
        {
            current1Hour!.High = Math.Max(current1Hour.High, bar.High);
            current1Hour.Low = Math.Min(current1Hour.Low, bar.Low);
            current1Hour.Close = bar.Close;
            current1Hour.Volume += bar.Volume;
        }
    }

    private DateTime RoundTo1Min(DateTime timestamp)
    {
        return new DateTime(timestamp.Year, timestamp.Month, timestamp.Day,
                          timestamp.Hour, timestamp.Minute, 0);
    }

    private DateTime RoundTo15Min(DateTime timestamp)
    {
        int minute = (timestamp.Minute / 15) * 15;
        return new DateTime(timestamp.Year, timestamp.Month, timestamp.Day,
                          timestamp.Hour, minute, 0);
    }

    private DateTime RoundTo1Hour(DateTime timestamp)
    {
        return new DateTime(timestamp.Year, timestamp.Month, timestamp.Day,
                          timestamp.Hour, 0, 0);
    }

    // ========================================
    // STREAMING BAR SUPPORT (15-min bars from keepUpToDate)
    // ========================================

    private Bar? _currentStreamingBar; // In-progress bar — NOT yet in bars15Min

    /// <summary>
    /// Handle a streaming 15-min bar update from IBKR (keepUpToDate=true).
    /// IBKR sends partial (cumulative) updates while the bar is forming.
    /// When the timestamp advances, the previous bar is sealed into bars15Min
    /// and New15MinBarCompleted is set — so bars15Min[^1] is always the COMPLETED bar.
    /// </summary>
    public void UpdateStreamingBar(DateTime time, decimal open, decimal high, decimal low, decimal close, long volume)
    {
        var roundedTime = RoundTo15Min(time);

        // Skip any bar at or before the last warmup bar (initial batch overlap)
        if (bars15Min.Count > 0 && roundedTime <= bars15Min.Last().Timestamp)
            return;

        if (_currentStreamingBar != null && _currentStreamingBar.Timestamp == roundedTime)
        {
            // Same 15-min window — update in-place (IBKR sends cumulative state)
            _currentStreamingBar.High  = Math.Max(_currentStreamingBar.High, high);
            _currentStreamingBar.Low   = Math.Min(_currentStreamingBar.Low, low);
            _currentStreamingBar.Close = close;
            _currentStreamingBar.Volume = volume; // cumulative, not additive
            return;
        }

        // New timestamp → seal the current in-progress bar (if any)
        if (_currentStreamingBar != null)
        {
            bars15Min.Add(_currentStreamingBar);
            AggregateStreamingToHour(_currentStreamingBar);
            new15MinBarCompleted = true;
        }

        // Start tracking the new in-progress bar
        _currentStreamingBar = new Bar
        {
            Timestamp = roundedTime,
            Open = open,
            High = high,
            Low = low,
            Close = close,
            Volume = volume,
            Symbol = symbol
        };
    }

    private void AggregateStreamingToHour(Bar bar15m)
    {
        var hourTime = RoundTo1Hour(bar15m.Timestamp);

        if (bars1Hour.Count > 0 && bars1Hour.Last().Timestamp == hourTime)
        {
            // Update existing hour bar
            var hourBar = bars1Hour.Last();
            hourBar.High = Math.Max(hourBar.High, bar15m.High);
            hourBar.Low = Math.Min(hourBar.Low, bar15m.Low);
            hourBar.Close = bar15m.Close;
            hourBar.Volume += bar15m.Volume;
        }
        else
        {
            // New hour bar
            bars1Hour.Add(new Bar
            {
                Timestamp = hourTime,
                Open = bar15m.Open,
                High = bar15m.High,
                Low = bar15m.Low,
                Close = bar15m.Close,
                Volume = bar15m.Volume,
                Symbol = symbol
            });
        }
    }

    /// <summary>
    /// The wall-clock time at which the current in-progress streaming bar ends.
    /// Returns null if no bar is being tracked.
    /// </summary>
    public DateTime? CurrentBarEndTime =>
        _currentStreamingBar?.Timestamp.AddMinutes(15);

    /// <summary>
    /// Called after SeedBars when the last warmup bar is still in-progress (IBKR includes
    /// the current partial bar in historical data responses). Moves the last bar from
    /// bars15Min into _currentStreamingBar so streaming/poll updates can refresh its OHLCV
    /// before it is sealed via ForceCompleteCurrentBar.
    /// </summary>
    public void MarkLastBarAsInProgress()
    {
        if (bars15Min.Count == 0) return;
        _currentStreamingBar = bars15Min.Last();
        bars15Min.RemoveAt(bars15Min.Count - 1);
    }

    /// <summary>
    /// Force-seals the current in-progress streaming bar into bars15Min.
    /// Call this when the wall clock has passed CurrentBarEndTime and
    /// historicalDataUpdate hasn't fired (IBKR Gateway streaming limitation).
    /// </summary>
    public void ForceCompleteCurrentBar()
    {
        if (_currentStreamingBar == null) return;
        bars15Min.Add(_currentStreamingBar);
        AggregateStreamingToHour(_currentStreamingBar);
        new15MinBarCompleted = true;
        _currentStreamingBar = null;
    }

    public (int min1, int min15, int hour1) GetBarCounts()
    {
        return (bars1Min.Count, bars15Min.Count, bars1Hour.Count);
    }
}
