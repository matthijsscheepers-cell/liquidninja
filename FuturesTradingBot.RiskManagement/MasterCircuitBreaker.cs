namespace FuturesTradingBot.RiskManagement;

using FuturesTradingBot.Core.Models;

/// <summary>
/// Master circuit breaker - orchestrates all safety checks
/// </summary>
public class MasterCircuitBreaker
{
    public readonly DailyLossCircuitBreaker dailyLoss;
    public readonly ConsistencyChecker consistency;
    public readonly MarketHoursCircuitBreaker marketHours;
    public readonly InactivityMonitor inactivity;

    // Strategy circuit breaker (2 consecutive stops → 2h cooldown)
    private int consecutiveStops = 0;
    private DateTime lastStopTime;
    private DateTime cooldownUntil = DateTime.MinValue;

    public MasterCircuitBreaker(AccountMode mode, decimal maxDailyLoss = 400m)
    {
        dailyLoss = new DailyLossCircuitBreaker(maxDailyLoss);
        consistency = new ConsistencyChecker(mode);
        marketHours = new MarketHoursCircuitBreaker();
        inactivity = new InactivityMonitor(mode);
    }

    /// <summary>
    /// Can we trade right now? (checks all circuit breakers)
    /// </summary>
    public MasterCircuitBreakerResult CanTrade(DateTime currentTime)
    {
        var result = new MasterCircuitBreakerResult
        {
            CanTrade = true,
            BlockedBy = new List<string>()
        };

        // Check 1: Strategy cooldown (2 stops)
        if (currentTime < cooldownUntil)
        {
            var remaining = cooldownUntil - currentTime;
            result.CanTrade = false;
            result.BlockedBy.Add($"Strategy cooldown active (2 consecutive stops). " +
                                $"Resume in {remaining.TotalMinutes:F0} minutes.");
        }

        // Check 2: Daily loss limit
        if (!dailyLoss.CanTrade(currentTime))
        {
            result.CanTrade = false;
            result.BlockedBy.Add($"Daily loss limit hit: ${dailyLoss.TodayLoss:F2} / ${dailyLoss.MaxDailyLoss:F2}");
        }

        // Check 3: Market hours blackout
        if (!marketHours.CanTrade(currentTime))
        {
            var blackout = marketHours.GetActiveBlackout(currentTime);
            result.CanTrade = false;
            result.BlockedBy.Add($"Market hours blackout: {blackout?.Name} ({blackout?.Reason})");
        }

        // Check 4: Consistency rule (Challenge only)
        if (!consistency.CanTakeMoreProfit(currentTime))
        {
            result.CanTrade = false;
            result.BlockedBy.Add($"40% consistency limit reached for today");
        }

        // Check 5: Inactivity warning (not blocking, just warning)
        if (inactivity.IsInDanger(currentTime))
        {
            var status = inactivity.GetStatus(currentTime);
            result.Warnings.Add($"Inactivity warning: {status}");
        }

        return result;
    }

    /// <summary>
    /// Record a stop loss (triggers cooldown after 2 consecutive)
    /// </summary>
    public void RecordStopLoss(decimal lossAmount, DateTime? timestamp = null)
    {
        var now = timestamp ?? DateTime.Now;
        consecutiveStops++;
        lastStopTime = now;

        // Record in daily loss tracker
        dailyLoss.RecordLoss(lossAmount, now);

        // Record in consistency checker (negative profit)
        consistency.RecordTrade(-lossAmount, now);

        // Record trade activity
        inactivity.RecordTrade(now);

        Console.WriteLine($"❌ Stop loss #{consecutiveStops} recorded. Loss: ${lossAmount:F2}");

        // Trigger cooldown after 2 consecutive stops
        if (consecutiveStops >= 2)
        {
            cooldownUntil = now.AddHours(2);
            Console.WriteLine($"⛔ CIRCUIT BREAKER ACTIVATED! 2 consecutive stops. " +
                            $"Trading paused until {cooldownUntil:HH:mm}");
        }
    }

    /// <summary>
    /// Record a winning trade (resets consecutive stops)
    /// </summary>
    public void RecordWin(decimal profitAmount, DateTime? timestamp = null)
    {
        var now = timestamp ?? DateTime.Now;

        if (consecutiveStops > 0)
        {
            Console.WriteLine($"✅ Winning trade! Consecutive stop counter reset (was {consecutiveStops})");
        }

        consecutiveStops = 0;

        // Record in consistency checker
        consistency.RecordTrade(profitAmount, now);

        // Record trade activity
        inactivity.RecordTrade(now);
    }

    /// <summary>
    /// Record a breakeven trade
    /// </summary>
    public void RecordBreakeven(DateTime? timestamp = null)
    {
        var now = timestamp ?? DateTime.Now;

        if (consecutiveStops > 0)
        {
            Console.WriteLine($"➖ Breakeven trade. Consecutive stop counter reset (was {consecutiveStops})");
        }

        consecutiveStops = 0;
        inactivity.RecordTrade(now);
    }

    /// <summary>
    /// Get comprehensive status of all circuit breakers
    /// </summary>
    public MasterCircuitBreakerStatus GetStatus(DateTime currentTime)
    {
        return new MasterCircuitBreakerStatus
        {
            StrategyCooldown = new StrategyCooldownStatus
            {
                IsActive = currentTime < cooldownUntil,
                ConsecutiveStops = consecutiveStops,
                CooldownUntil = cooldownUntil,
                MinutesRemaining = currentTime < cooldownUntil
                    ? (cooldownUntil - currentTime).TotalMinutes
                    : 0
            },
            DailyLoss = dailyLoss.GetStatus(),
            Consistency = consistency.GetStatus(),
            MarketHours = marketHours.GetStatus(currentTime),
            Inactivity = inactivity.GetStatus(currentTime)
        };
    }
}

public class MasterCircuitBreakerResult
{
    public bool CanTrade { get; set; }
    public List<string> BlockedBy { get; set; } = new();
    public List<string> Warnings { get; set; } = new();
}

public class MasterCircuitBreakerStatus
{
    public StrategyCooldownStatus StrategyCooldown { get; set; } = new();
    public CircuitBreakerStatus DailyLoss { get; set; } = new();
    public CircuitBreakerStatus Consistency { get; set; } = new();
    public CircuitBreakerStatus MarketHours { get; set; } = new();
    public InactivityStatus Inactivity { get; set; } = new();
}

public class StrategyCooldownStatus
{
    public bool IsActive { get; set; }
    public int ConsecutiveStops { get; set; }
    public DateTime CooldownUntil { get; set; }
    public double MinutesRemaining { get; set; }
}
