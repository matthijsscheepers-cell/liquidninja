namespace FuturesTradingBot.RiskManagement;

/// <summary>
/// Circuit breaker that stops trading when daily loss limit is hit
/// </summary>
public class DailyLossCircuitBreaker
{
    private decimal maxDailyLoss;
    private decimal todayLoss;
    private DateTime lastResetDate;

    public decimal MaxDailyLoss => maxDailyLoss;
    public decimal TodayLoss => todayLoss;
    public decimal RemainingDailyBuffer => Math.Max(0, maxDailyLoss - todayLoss);

    public DailyLossCircuitBreaker(decimal maxDailyLoss = 400m)
    {
        this.maxDailyLoss = maxDailyLoss;
        this.todayLoss = 0m;
        this.lastResetDate = DateTime.MinValue;
    }

    /// <summary>
    /// Can we trade? (checks if daily loss limit not hit)
    /// </summary>
    public bool CanTrade(DateTime currentTime)
    {
        CheckAndResetIfNewDay(currentTime);
        return todayLoss < maxDailyLoss;
    }

    /// <summary>
    /// Would this potential loss cause us to breach?
    /// </summary>
    public bool WouldBreach(decimal potentialLoss, DateTime currentTime)
    {
        CheckAndResetIfNewDay(currentTime);
        return (todayLoss + potentialLoss) >= maxDailyLoss;
    }

    /// <summary>
    /// Record a loss (call after trade closes)
    /// </summary>
    public void RecordLoss(decimal loss, DateTime currentTime)
    {
        if (loss <= 0) return;

        CheckAndResetIfNewDay(currentTime);
        todayLoss += loss;

        if (todayLoss >= maxDailyLoss)
        {
            Console.WriteLine($"⛔ DAILY LOSS LIMIT HIT! ${todayLoss:F2} / ${maxDailyLoss:F2}");
            Console.WriteLine("Trading blocked for rest of day.");
        }
    }

    /// <summary>
    /// Reset counter at start of new day
    /// </summary>
    private void CheckAndResetIfNewDay(DateTime currentTime)
    {
        if (currentTime.Date > lastResetDate)
        {
            todayLoss = 0m;
            lastResetDate = currentTime.Date;
        }
    }

    /// <summary>
    /// Get status for monitoring
    /// </summary>
    public CircuitBreakerStatus GetStatus()
    {
        return new CircuitBreakerStatus
        {
            IsActive = todayLoss >= maxDailyLoss,
            Name = "DailyLossCircuitBreaker",
            Reason = todayLoss >= maxDailyLoss
                ? $"Daily loss limit hit: ${todayLoss:F2} / ${maxDailyLoss:F2}"
                : "OK",
            Details = $"Today's loss: ${todayLoss:F2}, Limit: ${maxDailyLoss:F2}, Remaining: ${RemainingDailyBuffer:F2}"
        };
    }
}

public class CircuitBreakerStatus
{
    public bool IsActive { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    public string Details { get; set; } = string.Empty;
}
