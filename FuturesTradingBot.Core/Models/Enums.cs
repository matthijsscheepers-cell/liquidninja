namespace FuturesTradingBot.Core.Models;

/// <summary>
/// Trading signal direction
/// </summary>
public enum SignalDirection
{
    LONG,   // Buy signal
    SHORT,  // Sell signal
    NONE    // No signal
}

/// <summary>
/// Exit action for position management
/// </summary>
public enum ExitAction
{
    HOLD,           // Keep position open
    STOP,           // Stop loss hit
    TARGET,         // Target hit
    BREAKEVEN,      // Move stop to breakeven
    TRAIL,          // Trail stop
    TIME_EXIT       // Max holding time exceeded
}

/// <summary>
/// Trading account mode
/// </summary>
public enum AccountMode
{
    Challenge,          // FundedNext challenge (need $1250 profit)
    FundedPrePayout,    // Funded account before first payout
    FundedPostPayout    // Funded account after first payout (hard cap active)
}

/// <summary>
/// Circuit breaker severity level
/// </summary>
public enum BlackoutSeverity
{
    Low,      // Advisory only
    Medium,   // Recommended to avoid
    High      // Mandatory avoid
}

/// <summary>
/// Economic news event impact level
/// </summary>
public enum EventImpact
{
    Low,
    Medium,
    High
}
