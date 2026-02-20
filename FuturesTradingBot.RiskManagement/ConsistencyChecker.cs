namespace FuturesTradingBot.RiskManagement;

using FuturesTradingBot.Core.Models;

/// <summary>
/// Checks 40% consistency rule (Challenge accounts only)
/// No single day can contribute more than 40% of total profit
/// </summary>
public class ConsistencyChecker
{
    private Dictionary<DateTime, decimal> dailyProfits;
    private decimal totalProfit;
    private bool isEnabled; // Only for Challenge mode

    public decimal TotalProfit => totalProfit;
    public decimal ConsistencyLimit => 0.40m; // 40%

    public ConsistencyChecker(AccountMode mode)
    {
        this.dailyProfits = new Dictionary<DateTime, decimal>();
        this.totalProfit = 0m;
        this.isEnabled = mode == AccountMode.Challenge;
    }

    /// <summary>
    /// Would this profit violate the 40% rule?
    /// </summary>
    public bool WouldViolateConsistency(decimal potentialProfit, DateTime currentTime)
    {
        if (!isEnabled) return false;
        if (potentialProfit <= 0) return false;
        if (totalProfit < 500m) return false;

        var todayProfit = GetDayProfit(currentTime.Date) + potentialProfit;
        var newTotalProfit = totalProfit + potentialProfit;

        if (newTotalProfit <= 0) return false;

        var percentage = (todayProfit / newTotalProfit) * 100m;
        return percentage >= 40m;
    }

    /// <summary>
    /// Can we take more profit today without violating rule?
    /// </summary>
    public bool CanTakeMoreProfit(DateTime currentTime)
    {
        if (!isEnabled) return true;
        if (totalProfit < 500m) return true;
        if (totalProfit <= 0) return true;

        var todayProfit = GetDayProfit(currentTime.Date);
        var currentPercentage = (todayProfit / totalProfit) * 100m;

        return currentPercentage < 40m;
    }

    /// <summary>
    /// Record a trade result
    /// </summary>
    public void RecordTrade(decimal profit, DateTime currentTime)
    {
        if (!isEnabled) return;

        var day = currentTime.Date;

        if (!dailyProfits.ContainsKey(day))
            dailyProfits[day] = 0m;

        dailyProfits[day] += profit;
        totalProfit += profit;

        if (profit > 0 && totalProfit > 0)
        {
            var todayPercentage = (dailyProfits[day] / totalProfit) * 100m;

            if (todayPercentage >= 35m && todayPercentage < 40m)
            {
                Console.WriteLine($"⚠️  WARNING: Today's profit is {todayPercentage:F1}% of total. " +
                                $"Approaching 40% consistency limit!");
            }
            else if (todayPercentage >= 40m)
            {
                Console.WriteLine($"⛔ CONSISTENCY LIMIT HIT! Today: {todayPercentage:F1}% of total profit. " +
                                "Trading blocked for rest of day.");
            }
        }
    }

    /// <summary>
    /// Get profit for a specific day
    /// </summary>
    public decimal GetDayProfit(DateTime date)
    {
        return dailyProfits.GetValueOrDefault(date, 0m);
    }

    /// <summary>
    /// Get largest single day percentage
    /// </summary>
    public decimal GetLargestDayPercentage()
    {
        if (!isEnabled || totalProfit <= 0 || dailyProfits.Count == 0) return 0m;

        var largestDayProfit = dailyProfits.Values.Max();
        return (largestDayProfit / totalProfit) * 100m;
    }

    /// <summary>
    /// Get status for monitoring
    /// </summary>
    public CircuitBreakerStatus GetStatus()
    {
        return new CircuitBreakerStatus
        {
            IsActive = false,
            Name = "ConsistencyChecker",
            Reason = isEnabled ? "OK" : "Not enabled (not Challenge mode)",
            Details = $"Total profit: ${totalProfit:F2}, Largest day: {GetLargestDayPercentage():F1}%"
        };
    }
}
