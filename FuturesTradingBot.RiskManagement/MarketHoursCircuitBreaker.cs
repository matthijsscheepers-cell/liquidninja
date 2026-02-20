namespace FuturesTradingBot.RiskManagement;

/// <summary>
/// Circuit breaker for market hours blackout windows
/// Blocks trading during volatile periods (US open/close)
/// Uses US Eastern Time since IBKR data is in ET
/// </summary>
public class MarketHoursCircuitBreaker
{
    private readonly List<BlackoutWindow> blackoutWindows;

    public MarketHoursCircuitBreaker()
    {
        // Times in US Eastern (IBKR historical data uses ET)
        blackoutWindows = new List<BlackoutWindow>
        {
            // US Market Open - highest volatility
            new BlackoutWindow
            {
                Name = "US Market Open",
                Start = new TimeSpan(9, 20, 0),   // 9:20 AM ET (10 min before open)
                End = new TimeSpan(9, 50, 0),      // 9:50 AM ET (20 min after open)
                Reason = "Extreme volatility during US market open"
            },

            // US Market Close - end of day volatility
            new BlackoutWindow
            {
                Name = "US Market Close",
                Start = new TimeSpan(15, 45, 0),   // 3:45 PM ET (15 min before close)
                End = new TimeSpan(16, 05, 0),     // 4:05 PM ET (5 min after close)
                Reason = "Increased volatility during market close"
            }
        };
    }

    /// <summary>
    /// Can we trade at this time?
    /// currentTime should be in US Eastern Time (as IBKR provides)
    /// </summary>
    public bool CanTrade(DateTime currentTime)
    {
        var timeOfDay = currentTime.TimeOfDay;

        foreach (var window in blackoutWindows)
        {
            if (timeOfDay >= window.Start && timeOfDay < window.End)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Get active blackout window (if any)
    /// </summary>
    public BlackoutWindow? GetActiveBlackout(DateTime currentTime)
    {
        var timeOfDay = currentTime.TimeOfDay;

        foreach (var window in blackoutWindows)
        {
            if (timeOfDay >= window.Start && timeOfDay < window.End)
            {
                return window;
            }
        }

        return null;
    }

    /// <summary>
    /// Get status for monitoring
    /// </summary>
    public CircuitBreakerStatus GetStatus(DateTime currentTime)
    {
        var activeBlackout = GetActiveBlackout(currentTime);

        if (activeBlackout != null)
        {
            return new CircuitBreakerStatus
            {
                IsActive = true,
                Name = "MarketHoursCircuitBreaker",
                Reason = $"{activeBlackout.Name} blackout active",
                Details = activeBlackout.Reason
            };
        }

        return new CircuitBreakerStatus
        {
            IsActive = false,
            Name = "MarketHoursCircuitBreaker",
            Reason = "OK - No active blackout",
            Details = "Currently in safe trading window"
        };
    }
}

public class BlackoutWindow
{
    public string Name { get; set; } = string.Empty;
    public TimeSpan Start { get; set; }
    public TimeSpan End { get; set; }
    public string Reason { get; set; } = string.Empty;
}
