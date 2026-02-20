namespace FuturesTradingBot.RiskManagement;

using FuturesTradingBot.Core.Models;

/// <summary>
/// Monitors trading inactivity to prevent breach
/// FundedNext rules: Challenge = 7 days max, Funded = 30 days max
/// </summary>
public class InactivityMonitor
{
    private DateTime lastTradeDate;
    private readonly AccountMode accountMode;
    private int maxIdleDaysObserved;

    public DateTime LastTradeDate => lastTradeDate;
    public int MaxIdleDaysObserved => maxIdleDaysObserved;

    public InactivityMonitor(AccountMode mode)
    {
        this.accountMode = mode;
        this.lastTradeDate = DateTime.MinValue;
        this.maxIdleDaysObserved = 0;
    }

    public int GetMaxInactiveDays()
    {
        return accountMode == AccountMode.Challenge ? 7 : 30;
    }

    public int GetDaysSinceLastTrade(DateTime currentTime)
    {
        if (lastTradeDate == DateTime.MinValue) return 0;
        return (currentTime.Date - lastTradeDate).Days;
    }

    /// <summary>
    /// Are we in danger of breaching due to inactivity?
    /// </summary>
    public bool IsInDanger(DateTime currentTime)
    {
        if (lastTradeDate == DateTime.MinValue) return false;
        var daysSince = GetDaysSinceLastTrade(currentTime);
        return daysSince >= GetMaxInactiveDays() - 1;
    }

    /// <summary>
    /// Has the inactivity limit been breached?
    /// </summary>
    public bool IsBreach(DateTime currentTime)
    {
        if (lastTradeDate == DateTime.MinValue) return false;
        return GetDaysSinceLastTrade(currentTime) >= GetMaxInactiveDays();
    }

    /// <summary>
    /// Record a trade (resets inactivity counter)
    /// </summary>
    public void RecordTrade(DateTime currentTime)
    {
        var daysSince = GetDaysSinceLastTrade(currentTime);
        if (daysSince > maxIdleDaysObserved)
            maxIdleDaysObserved = daysSince;

        lastTradeDate = currentTime.Date;
    }

    public InactivityStatus GetStatus(DateTime currentTime)
    {
        var daysSince = GetDaysSinceLastTrade(currentTime);
        var maxDays = GetMaxInactiveDays();
        var daysRemaining = maxDays - daysSince;

        string severity;
        if (daysRemaining <= 0)
            severity = "BREACH";
        else if (daysRemaining <= 2)
            severity = "CRITICAL";
        else if (daysRemaining <= 5)
            severity = "WARNING";
        else
            severity = "OK";

        return new InactivityStatus
        {
            DaysSinceLastTrade = daysSince,
            MaxAllowedDays = maxDays,
            DaysRemaining = Math.Max(0, daysRemaining),
            Severity = severity,
            LastTradeDate = lastTradeDate,
            MaxIdleDaysObserved = maxIdleDaysObserved
        };
    }
}

public class InactivityStatus
{
    public int DaysSinceLastTrade { get; set; }
    public int MaxAllowedDays { get; set; }
    public int DaysRemaining { get; set; }
    public string Severity { get; set; } = string.Empty;
    public DateTime LastTradeDate { get; set; }
    public int MaxIdleDaysObserved { get; set; }

    public override string ToString()
    {
        return $"{Severity}: {DaysSinceLastTrade}/{MaxAllowedDays} days inactive " +
               $"({DaysRemaining} remaining). Max idle streak: {MaxIdleDaysObserved} days";
    }
}
