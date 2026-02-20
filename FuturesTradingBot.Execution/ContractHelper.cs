namespace FuturesTradingBot.Execution;

using IBApi;

/// <summary>
/// Helper to create IBKR contract definitions
/// </summary>
public static class ContractHelper
{
    /// <summary>
    /// Create MGC (Gold Micro Futures) contract
    /// Gold futures: monthly contracts (every month is valid)
    /// </summary>
    public static Contract CreateMGC()
    {
        return new Contract
        {
            Symbol = "MGC",
            SecType = "FUT",
            Currency = "USD",
            Exchange = "COMEX",
            LastTradeDateOrContractMonth = GetNextMonthlyExpiry()
        };
    }

    /// <summary>
    /// Create MES (Micro E-mini S&P 500 Futures) contract
    /// Index futures: quarterly contracts (Mar, Jun, Sep, Dec)
    /// </summary>
    public static Contract CreateMES()
    {
        return new Contract
        {
            Symbol = "MES",
            SecType = "FUT",
            Currency = "USD",
            Exchange = "CME",
            LastTradeDateOrContractMonth = GetNextQuarterlyExpiry()
        };
    }

    /// <summary>
    /// Next monthly expiry (for gold, metals)
    /// </summary>
    private static string GetNextMonthlyExpiry()
    {
        var now = DateTime.Now;
        var targetMonth = now.Day > 15 ? now.AddMonths(1) : now;
        return targetMonth.ToString("yyyyMM");
    }

    /// <summary>
    /// Next quarterly expiry (for index futures: MES, MNQ, MYM)
    /// Months: March(3), June(6), September(9), December(12)
    /// </summary>
    private static string GetNextQuarterlyExpiry()
    {
        var now = DateTime.Now;
        int[] quarterMonths = { 3, 6, 9, 12 };

        foreach (var month in quarterMonths)
        {
            var expiry = new DateTime(now.Year, month, 1);
            // Third Friday of the month is typical expiry
            if (expiry > now.AddDays(-15))
                return expiry.ToString("yyyyMM");
        }

        // Roll to March next year
        return new DateTime(now.Year + 1, 3, 1).ToString("yyyyMM");
    }
}
