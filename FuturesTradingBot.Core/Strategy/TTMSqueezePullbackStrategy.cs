namespace FuturesTradingBot.Core.Strategy;

using FuturesTradingBot.Core.Models;
using FuturesTradingBot.Core.Indicators;

/// <summary>
/// Multi-timeframe EMA pullback strategy (LONG + SHORT)
///
/// MODE 1 - EMA 21 Pullback (normal trends):
///   LONG: pullback to 21-EMA in uptrend (1H Close > EMA)
///   SHORT: pullback to 21-EMA in downtrend (1H Close < EMA)
///
/// MODE 2 - Trend Ride (strong trends):
///   When 1H is 1.5+ ATR from EMA, switch to 9-EMA pullback
///   Uses trailing stop instead of fixed target to let winners run
///
/// Key features:
/// - Asset-specific parameters (gold needs wider tolerance)
/// - Close OR Low/High for pullback detection
/// - Trend strength filter (1H must be 0.3+ ATR from EMA)
/// - Breakeven stop once price moves 1 ATR in favor
/// - Trailing stop for trend ride trades
/// - Cooldown: skip 3 bars after closing a trade
/// </summary>
public class TTMSqueezePullbackStrategy : BaseStrategy
{
    private int lastExitBar = -999;

    public TTMSqueezePullbackStrategy(string asset)
        : base(asset, "TTMSqueezePullback")
    {
    }

    public void SetLastExitBar(int bar) => lastExitBar = bar;

    protected override void InitializeParameters()
    {
        switch (Asset)
        {
            case "MGC":
                Parameters = new Dictionary<string, object>
                {
                    { "ema_period", 21 },
                    { "atr_period", 20 },
                    { "entry_tolerance", 1.2m },
                    { "stop_atr", 1.5m },
                    { "target_atr", 2.0m },
                    { "breakeven_atr", 1.0m },
                    { "trend_strength", 0.3m },
                    { "cooldown_bars", 3 },
                    // Trend ride parameters (strong trend mode)
                    { "trend_ride_threshold", 1.5m },  // Activate when 1H is 1.5+ ATR from EMA
                    { "trend_ride_tolerance", 0.8m },   // Pullback tolerance to 9 EMA
                    { "trend_ride_stop_atr", 1.0m },    // Tighter stop in strong trend
                    { "trend_ride_trail_atr", 1.5m }    // Trailing stop distance
                };
                break;

            case "MES":
                Parameters = new Dictionary<string, object>
                {
                    { "ema_period", 21 },
                    { "atr_period", 20 },
                    { "entry_tolerance", 1.0m },
                    { "stop_atr", 1.5m },
                    { "target_atr", 2.0m },
                    { "breakeven_atr", 1.0m },
                    { "trend_strength", 0.3m },
                    { "cooldown_bars", 3 },
                    { "trend_ride_threshold", 1.5m },
                    { "trend_ride_tolerance", 0.8m },
                    { "trend_ride_stop_atr", 1.0m },
                    { "trend_ride_trail_atr", 1.5m }
                };
                break;

            default:
                Parameters = new Dictionary<string, object>
                {
                    { "ema_period", 21 },
                    { "atr_period", 20 },
                    { "entry_tolerance", 1.0m },
                    { "stop_atr", 1.5m },
                    { "target_atr", 2.0m },
                    { "breakeven_atr", 1.0m },
                    { "trend_strength", 0.3m },
                    { "cooldown_bars", 3 },
                    { "trend_ride_threshold", 1.5m },
                    { "trend_ride_tolerance", 0.8m },
                    { "trend_ride_stop_atr", 1.0m },
                    { "trend_ride_trail_atr", 1.5m }
                };
                break;
        }
    }

    public override TradeSetup? CheckEntry(
        List<Bar> bars15min,
        List<Bar> bars1h,
        string regime,
        decimal confidence)
    {
        if (bars15min.Count < 50 || bars1h.Count < 22)
            return null;

        // Cooldown check: skip if we exited too recently
        int currentBarIndex = bars15min.Count - 1;
        int cooldownBars = Convert.ToInt32(Parameters.GetValueOrDefault("cooldown_bars", 3));
        if (currentBarIndex - lastExitBar < cooldownBars)
            return null;

        var current15m = bars15min[^1];
        var prev15m = bars15min[^2];

        // Use the second-to-last 1H bar for trend direction — the last bar may be
        // an in-progress (incomplete) bar with unreliable EMA values
        if (bars1h.Count < 3) return null;
        var current1h = bars1h[^2];

        // Validate indicators
        try
        {
            ValidateIndicators(current15m, "ema_21", "ema_9", "atr_20", "ttm_momentum");
            ValidateIndicators(current1h, "ema_21", "atr_20");
            ValidateIndicators(prev15m, "ttm_momentum");
        }
        catch (InvalidOperationException)
        {
            return null;
        }

        var close1h = current1h.Close;
        var ema1h = GetIndicator(current1h, "ema_21");
        var atr1h = GetIndicator(current1h, "atr_20");
        if (!ema1h.HasValue || !atr1h.HasValue || atr1h.Value == 0) return null;

        var momentum15m = GetIndicator(current15m, "ttm_momentum");
        var prevMomentum15m = GetIndicator(prev15m, "ttm_momentum");
        if (!momentum15m.HasValue || !prevMomentum15m.HasValue) return null;

        var color = TTMMomentum.GetHistogramColor(momentum15m, prevMomentum15m);

        var ema21 = GetIndicator(current15m, "ema_21");
        var ema9 = GetIndicator(current15m, "ema_9");
        var atr = GetIndicator(current15m, "atr_20");
        if (!ema21.HasValue || !ema9.HasValue || !atr.HasValue || atr.Value == 0) return null;

        var tolerance = (decimal)Parameters["entry_tolerance"];
        var stopAtr = (decimal)Parameters["stop_atr"];
        var targetAtr = (decimal)Parameters["target_atr"];
        var trendStrength = (decimal)Parameters["trend_strength"];
        var trendRideThreshold = (decimal)Parameters["trend_ride_threshold"];

        // Calculate 1H trend distance from EMA (in ATRs)
        decimal trendDist1h;
        bool is1hUptrend = close1h > ema1h.Value;

        if (is1hUptrend)
            trendDist1h = (close1h - ema1h.Value) / atr1h.Value;
        else
            trendDist1h = (ema1h.Value - close1h) / atr1h.Value;

        // Check minimum trend strength
        if (trendDist1h < trendStrength)
            return null;

        // ── MODE 1: Standard EMA 21 Pullback ──
        var pullbackSetup = CheckPullbackEntry(
            current15m, is1hUptrend, ema21.Value, atr.Value,
            tolerance, stopAtr, targetAtr, trendDist1h,
            color, confidence, regime);

        if (pullbackSetup != null)
            return pullbackSetup;

        // ── MODE 2: Trend Ride (9 EMA pullback in strong trends) ──
        if (trendDist1h >= trendRideThreshold)
        {
            return CheckTrendRideEntry(
                current15m, is1hUptrend, ema9.Value, atr.Value,
                trendDist1h, color, confidence, regime,
                close1h, ema1h.Value);
        }

        return null;
    }

    /// <summary>
    /// Standard EMA 21 pullback entry
    /// </summary>
    private TradeSetup? CheckPullbackEntry(
        Bar current15m, bool isUptrend, decimal ema21, decimal atr,
        decimal tolerance, decimal stopAtr, decimal targetAtr,
        decimal trendDist, string color, decimal confidence, string regime)
    {
        if (isUptrend)
        {
            if (color == "red") return null;

            var distLow = (current15m.Low - ema21) / atr;
            var distClose = (current15m.Close - ema21) / atr;
            var signedDistance = Math.Abs(distLow) < Math.Abs(distClose) ? distLow : distClose;

            if (signedDistance > tolerance || signedDistance < -tolerance)
                return null;

            var entryPrice = ema21;
            var stopLoss = ema21 - (stopAtr * atr);
            var target = ema21 + (targetAtr * atr);
            var riskPerShare = entryPrice - stopLoss;
            if (riskPerShare <= 0) return null;

            return new TradeSetup
            {
                Direction = SignalDirection.LONG,
                EntryPrice = entryPrice,
                StopLoss = stopLoss,
                Target = target,
                RiskPerShare = riskPerShare,
                Confidence = confidence,
                SetupType = "TTM_PULLBACK_LONG",
                Asset = Asset,
                Metadata = new Dictionary<string, object>
                {
                    { "ema_21", ema21 }, { "atr", atr },
                    { "trend_strength_atr", trendDist },
                    { "histogram_color", color },
                    { "regime", regime },
                    { "distance_from_ema_atr", signedDistance }
                }
            };
        }
        else
        {
            if (color == "light_blue") return null;

            var distHigh = (current15m.High - ema21) / atr;
            var distClose = (current15m.Close - ema21) / atr;
            var signedDistance = Math.Abs(distHigh) < Math.Abs(distClose) ? distHigh : distClose;

            if (signedDistance > tolerance || signedDistance < -tolerance)
                return null;

            var entryPrice = ema21;
            var stopLoss = ema21 + (stopAtr * atr);
            var target = ema21 - (targetAtr * atr);
            var riskPerShare = stopLoss - entryPrice;
            if (riskPerShare <= 0) return null;

            return new TradeSetup
            {
                Direction = SignalDirection.SHORT,
                EntryPrice = entryPrice,
                StopLoss = stopLoss,
                Target = target,
                RiskPerShare = riskPerShare,
                Confidence = confidence,
                SetupType = "TTM_PULLBACK_SHORT",
                Asset = Asset,
                Metadata = new Dictionary<string, object>
                {
                    { "ema_21", ema21 }, { "atr", atr },
                    { "trend_strength_atr", trendDist },
                    { "histogram_color", color },
                    { "regime", regime },
                    { "distance_from_ema_atr", signedDistance }
                }
            };
        }
    }

    /// <summary>
    /// Trend Ride: 9 EMA pullback in strong trends
    /// Uses trailing stop instead of fixed target
    /// </summary>
    private TradeSetup? CheckTrendRideEntry(
        Bar current15m, bool isUptrend, decimal ema9, decimal atr,
        decimal trendDist, string color, decimal confidence, string regime,
        decimal close1h, decimal ema1h)
    {
        var trendRideTolerance = (decimal)Parameters["trend_ride_tolerance"];
        var trendRideStopAtr = (decimal)Parameters["trend_ride_stop_atr"];
        var trendRideTrailAtr = (decimal)Parameters["trend_ride_trail_atr"];

        if (isUptrend)
        {
            if (color == "red") return null;

            // Check pullback to 9 EMA
            var distLow = (current15m.Low - ema9) / atr;
            var distClose = (current15m.Close - ema9) / atr;
            var signedDistance = Math.Abs(distLow) < Math.Abs(distClose) ? distLow : distClose;

            if (signedDistance > trendRideTolerance || signedDistance < -trendRideTolerance)
                return null;

            // Price must still be above 9 EMA (don't enter if broken below)
            if (current15m.Close < ema9)
                return null;

            var entryPrice = ema9;
            var stopLoss = ema9 - (trendRideStopAtr * atr);
            // Initial target far out - trailing stop will manage exit
            var target = ema9 + (trendRideTrailAtr * 3m * atr);
            var riskPerShare = entryPrice - stopLoss;
            if (riskPerShare <= 0) return null;

            return new TradeSetup
            {
                Direction = SignalDirection.LONG,
                EntryPrice = entryPrice,
                StopLoss = stopLoss,
                Target = target,
                RiskPerShare = riskPerShare,
                Confidence = confidence,
                SetupType = "TTM_TREND_RIDE_LONG",
                Asset = Asset,
                Metadata = new Dictionary<string, object>
                {
                    { "ema_9", ema9 }, { "atr", atr },
                    { "trend_strength_atr", trendDist },
                    { "histogram_color", color },
                    { "regime", regime },
                    { "distance_from_ema9_atr", signedDistance },
                    { "close_1h", close1h },
                    { "ema_1h", ema1h }
                }
            };
        }
        else // Downtrend
        {
            if (color == "light_blue") return null;

            var distHigh = (current15m.High - ema9) / atr;
            var distClose = (current15m.Close - ema9) / atr;
            var signedDistance = Math.Abs(distHigh) < Math.Abs(distClose) ? distHigh : distClose;

            if (signedDistance > trendRideTolerance || signedDistance < -trendRideTolerance)
                return null;

            if (current15m.Close > ema9)
                return null;

            var entryPrice = ema9;
            var stopLoss = ema9 + (trendRideStopAtr * atr);
            var target = ema9 - (trendRideTrailAtr * 3m * atr);
            var riskPerShare = stopLoss - entryPrice;
            if (riskPerShare <= 0) return null;

            return new TradeSetup
            {
                Direction = SignalDirection.SHORT,
                EntryPrice = entryPrice,
                StopLoss = stopLoss,
                Target = target,
                RiskPerShare = riskPerShare,
                Confidence = confidence,
                SetupType = "TTM_TREND_RIDE_SHORT",
                Asset = Asset,
                Metadata = new Dictionary<string, object>
                {
                    { "ema_9", ema9 }, { "atr", atr },
                    { "trend_strength_atr", trendDist },
                    { "histogram_color", color },
                    { "regime", regime },
                    { "distance_from_ema9_atr", signedDistance },
                    { "close_1h", close1h },
                    { "ema_1h", ema1h }
                }
            };
        }
    }

    /// <summary>
    /// Manage exit with breakeven stop and trailing stop for trend rides
    /// </summary>
    public override (ExitAction action, decimal? price) ManageExit(
        List<Bar> bars,
        Position position)
    {
        if (bars.Count == 0)
            return (ExitAction.HOLD, null);

        var current = bars[^1];
        var breakevenAtr = (decimal)Parameters["breakeven_atr"];
        var atr = GetIndicator(current, "atr_20");

        bool isTrendRide = position.EntryStrategy.Contains("TREND_RIDE");

        if (position.Direction == SignalDirection.LONG)
        {
            // Breakeven stop: move stop to entry after 1 ATR profit
            if (atr.HasValue && current.High >= position.EntryPrice + (breakevenAtr * atr.Value))
            {
                if (position.StopLoss < position.EntryPrice)
                    position.StopLoss = position.EntryPrice;
            }

            // Trailing stop for trend ride trades
            if (isTrendRide && atr.HasValue)
            {
                var trailAtr = (decimal)Parameters["trend_ride_trail_atr"];

                // Track highest high since entry
                decimal highestHigh;
                if (position.Metadata.TryGetValue("highest_high", out var hh))
                    highestHigh = Convert.ToDecimal(hh);
                else
                    highestHigh = position.EntryPrice;

                if (current.High > highestHigh)
                {
                    highestHigh = current.High;
                    position.Metadata["highest_high"] = highestHigh;
                }

                // Trail stop below highest high
                var trailStop = highestHigh - (trailAtr * atr.Value);

                // Only move stop upward, never back down
                if (trailStop > position.StopLoss)
                    position.StopLoss = trailStop;
            }

            if (current.Low <= position.StopLoss)
                return (ExitAction.STOP, position.StopLoss);
            if (current.High >= position.Target)
                return (ExitAction.TARGET, position.Target);
        }
        else if (position.Direction == SignalDirection.SHORT)
        {
            // Breakeven stop for shorts
            if (atr.HasValue && current.Low <= position.EntryPrice - (breakevenAtr * atr.Value))
            {
                if (position.StopLoss > position.EntryPrice)
                    position.StopLoss = position.EntryPrice;
            }

            // Trailing stop for trend ride shorts
            if (isTrendRide && atr.HasValue)
            {
                var trailAtr = (decimal)Parameters["trend_ride_trail_atr"];

                decimal lowestLow;
                if (position.Metadata.TryGetValue("lowest_low", out var ll))
                    lowestLow = Convert.ToDecimal(ll);
                else
                    lowestLow = position.EntryPrice;

                if (current.Low < lowestLow)
                {
                    lowestLow = current.Low;
                    position.Metadata["lowest_low"] = lowestLow;
                }

                var trailStop = lowestLow + (trailAtr * atr.Value);

                if (trailStop < position.StopLoss)
                    position.StopLoss = trailStop;
            }

            if (current.High >= position.StopLoss)
                return (ExitAction.STOP, position.StopLoss);
            if (current.Low <= position.Target)
                return (ExitAction.TARGET, position.Target);
        }

        return (ExitAction.HOLD, null);
    }
}
