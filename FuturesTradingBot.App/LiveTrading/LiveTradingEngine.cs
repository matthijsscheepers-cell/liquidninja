namespace FuturesTradingBot.App.LiveTrading;

using IBApi;
using System.Text.Json;
using FuturesTradingBot.Core.Models;
using Bar = FuturesTradingBot.Core.Models.Bar;
using FuturesTradingBot.Core.Indicators;
using FuturesTradingBot.Core.Strategy;
using FuturesTradingBot.Execution;
using FuturesTradingBot.RiskManagement;

/// <summary>
/// Live paper trading engine - connects to IBKR, runs strategy in real-time
/// </summary>
public class LiveTradingEngine
{
    private readonly string asset;
    private readonly decimal startingBalance;
    private readonly decimal maxDailyLoss;

    private IbkrConnector connector = null!;
    private TTMSqueezePullbackStrategy strategy = null!;
    private RiskManager riskManager = null!;
    private BarAggregator aggregator = null!;
    private TradeLogger logger = null!;
    private Contract contract = null!;

    private Position? openPosition;
    private int? entryOrderId;
    private int? stopOrderId;
    private int? targetOrderId;
    private bool _entryConfirmed;     // true once IBKR confirms the entry LMT was filled
    private bool _positionSeenInReconcile; // true if IBKR reported our asset in this reconcile cycle
    private DateTime _entryOrderPlacedAt; // when the current bracket was submitted (for grace-period protection)
    private decimal currentBalance;
    private bool isRunning;
    private int barIndex;
    private DateTime _lastBarPoll = DateTime.MinValue;
    private int _consecutivePollFailures = 0;

    // Stats
    private int totalTrades;
    private int wins;
    private int losses;
    private decimal totalPnL;

    private static readonly Dictionary<string, decimal> Multipliers = new()
    {
        { "MGC", 10m },
        { "MES", 5m },
    };

    private static readonly Dictionary<string, decimal> TickSizes = new()
    {
        { "MGC", 0.10m },
        { "MES", 0.25m },
    };

    private decimal RoundToTick(decimal price)
    {
        var tick = TickSizes.GetValueOrDefault(asset, 0.01m);
        return Math.Round(price / tick, MidpointRounding.AwayFromZero) * tick;
    }

    public LiveTradingEngine(string asset, decimal balance = 25000m, decimal maxDailyLoss = 1250m)
    {
        this.asset = asset;
        this.startingBalance = balance;
        this.currentBalance = balance;
        this.maxDailyLoss = maxDailyLoss;
    }

    public async Task Start()
    {
        logger = new TradeLogger(asset);

        logger.LogStatus(DateTime.Now, $"Starting live engine for {asset}");
        logger.LogStatus(DateTime.Now, $"Balance: ${startingBalance:F2}, Max daily loss: ${maxDailyLoss:F2}");

        // 1. Connect to IBKR (unique clientId per asset to allow parallel instances)
        int clientId = asset == "MGC" ? 100 : 101;
        connector = new IbkrConnector("127.0.0.1", 7497, clientId);
        if (!connector.Connect())
        {
            logger.LogError(DateTime.Now, "Failed to connect to IBKR!");
            return;
        }
        Thread.Sleep(3000);

        // 2. Resolve front-month contract by asking IBKR directly (avoids hard-coded expiry dates)
        string exchange = asset == "MGC" ? "COMEX" : "CME";
        var resolved = connector.ResolveFrontMonthContract(asset, "FUT", exchange);
        if (resolved == null)
        {
            logger.LogError(DateTime.Now, $"Could not resolve front-month contract for {asset} — cannot trade safely. Exiting.");
            connector.Disconnect();
            return;
        }
        contract = resolved;
        logger.LogStatus(DateTime.Now,
            $"Front-month contract: {contract.Symbol} {contract.LastTradeDateOrContractMonth} (conId={contract.ConId}, exchange={contract.Exchange})");

        // 3. Create strategy + risk manager
        strategy = new TTMSqueezePullbackStrategy(asset);
        riskManager = new RiskManager(
            AccountMode.Challenge,
            startingBalance: startingBalance,
            currentBalance: currentBalance,
            maxDailyLoss: maxDailyLoss
        );

        // 4. Wait for HMDS (historical data farm) then fetch warmup bars
        // Retry every 5 minutes if market is closed / HMDS unavailable (e.g. Saturday)
        List<Bar>? bars15m = null;
        List<Bar>? bars1h = null;

        while (bars15m == null || bars1h == null || bars15m.Count < 50 || bars1h.Count < 20)
        {
            connector.WaitForHmds(timeoutSeconds: 30);
            logger.LogStatus(DateTime.Now, "Fetching warmup bars...");
            (bars15m, bars1h) = FetchWarmupBars();

            if (bars15m == null || bars1h == null || bars15m.Count < 50 || bars1h.Count < 20)
            {
                int got15m = bars15m?.Count ?? 0;
                int got1h  = bars1h?.Count  ?? 0;
                logger.LogStatus(DateTime.Now,
                    $"Warmup data insufficient (15m={got15m}, 1h={got1h}) — market may be closed. Retrying in 5 min...");
                await Task.Delay(TimeSpan.FromMinutes(5));

                // Reconnect before retrying (IBKR may have timed out the session)
                if (!connector.IsConnected)
                {
                    connector.Disconnect();
                    await Task.Delay(3000);
                    connector = new IbkrConnector("127.0.0.1", 7497, clientId);
                    if (!connector.Connect()) { logger.LogError(DateTime.Now, "Reconnect failed, giving up."); return; }
                    await Task.Delay(3000);
                }
            }
        }

        // 5. Add indicators to warmup bars
        IndicatorHelper.AddAllIndicators(bars15m);
        IndicatorHelper.AddAllIndicators(bars1h);

        // 6. Seed aggregator
        aggregator = new BarAggregator(asset);
        aggregator.SeedBars(bars15m, bars1h);

        // IBKR includes the current in-progress bar in historical data responses.
        // Move it out of bars15Min into _currentStreamingBar so the polling fallback
        // can refresh its OHLCV data and then force-seal it correctly.
        if (bars15m.Count > 0 && DateTime.Now < bars15m.Last().Timestamp.AddMinutes(15))
            aggregator.MarkLastBarAsInProgress();

        barIndex = bars15m.Count;

        logger.LogStatus(DateTime.Now, $"Warmup complete: {bars15m.Count} 15m bars, {bars1h.Count} 1h bars");
        logger.LogStatus(DateTime.Now, $"Last 15m bar: {bars15m.Last().Timestamp:yyyy-MM-dd HH:mm} Close: ${bars15m.Last().Close:F2}");

        // 7. Wire up order events (before state restore so reconcile callbacks work immediately)
        connector.OnOrderStatusChanged += HandleOrderStatus;
        connector.OnPositionUpdate += HandlePositionUpdate;
        connector.OnPositionEnd += HandlePositionEnd;

        // 8. Restore position state from today's log (if bot was restarted mid-session)
        bool stateRestored = RestoreStateFromLog();
        if (stateRestored)
        {
            // Immediately validate against IBKR - if position was closed while we were
            // down, HandlePositionUpdate will fire and log a RECONCILE exit.
            // If IBKR has no position for our asset, positionEnd fires → HandlePositionEnd.
            logger.LogStatus(DateTime.Now, "Reconciling restored state with IBKR...");
            _positionSeenInReconcile = false;
            connector.RequestPositions();
            await Task.Delay(3000);
        }

        // CONTFUT contract for streaming (follows front-month automatically)
        var streamingContract = new Contract
        {
            Symbol = asset,
            SecType = "CONTFUT",
            Currency = "USD",
            Exchange = asset == "MGC" ? "COMEX" : "CME"
        };

        // 8. Wire streaming bar updates → aggregator
        connector.OnStreamingBarUpdate += (_, t, o, h, l, c, v) =>
            aggregator.UpdateStreamingBar(t, o, h, l, c, v);

        // Subscribe to streaming bars (keepUpToDate=true) — delivers updates within seconds
        connector.SubscribeStreamingBars(asset, streamingContract, "15 mins");
        logger.LogStatus(DateTime.Now, "Streaming bars subscribed (keepUpToDate=true)...");

        // 9. Handle reconnects — re-subscribe to streaming
        connector.OnReconnected += () =>
        {
            logger.LogStatus(DateTime.Now, "IBKR reconnected - re-subscribing to streaming bars...");
            connector.SubscribeStreamingBars(asset, streamingContract, "15 mins");
        };

        logger.LogStatus(DateTime.Now, "Live trading started (streaming mode)...");

        // 10. Main loop — bar processing driven by streaming events
        isRunning = true;
        var lastReconcile = DateTime.Now;

        while (isRunning)
        {
            await Task.Delay(5000);

            if (!connector.IsConnected) continue;

            // Polling fallback: historicalDataUpdate doesn't fire reliably for 15-min bars
            // on IBKR Gateway — poll every 90 seconds to detect new completed bars.
            if ((DateTime.Now - _lastBarPoll).TotalSeconds >= 90)
            {
                _lastBarPoll = DateTime.Now;
                bool pollOk = await Task.Run(() => PollLatestBars());
                if (!pollOk)
                    throw new Exception($"IBKR data feed lost ({_consecutivePollFailures} consecutive poll timeouts) — auto-restart triggered");
            }

            // Process a newly completed 15-min bar (flag auto-resets after read)
            if (aggregator.New15MinBarCompleted)
            {
                barIndex = aggregator.Bars15Min.Count;
                OnNew15MinBar();
            }

            // 5-minute limit expiry: if price hasn't returned to EMA within 5 min, cancel the stale entry.
            // EMA taps are fast — if it hasn't filled in 5 min the setup is gone.
            // BAR_EXPIRED at the next bar close is the safety net for any edge cases.
            if (openPosition != null && !_entryConfirmed && entryOrderId != null &&
                (DateTime.Now - _entryOrderPlacedAt).TotalMinutes >= 5)
            {
                var expireNow = DateTime.Now;
                logger.LogStatus(expireNow, $"LIMIT_EXPIRED: entry #{entryOrderId} cancelled — price did not return to EMA within 5 minutes");
                logger.LogExit(expireNow, openPosition, openPosition.EntryPrice, 0m, "LIMIT_EXPIRED");

                if (entryOrderId.HasValue)  connector.CancelOrder(entryOrderId.Value);
                if (stopOrderId.HasValue)   connector.CancelOrder(stopOrderId.Value);
                if (targetOrderId.HasValue) connector.CancelOrder(targetOrderId.Value);

                if (strategy is TTMSqueezePullbackStrategy ttmExpired)
                    ttmExpired.SetLastExitBar(barIndex);

                openPosition = null;
                entryOrderId = null;
                stopOrderId = null;
                targetOrderId = null;
                _entryConfirmed = false;
            }

            // Position reconciliation every 60 seconds
            if ((DateTime.Now - lastReconcile).TotalSeconds >= 60)
            {
                _positionSeenInReconcile = false;
                connector.RequestPositions();
                lastReconcile = DateTime.Now;
            }
        }

        // Shutdown
        logger.LogStatus(DateTime.Now, "Shutting down...");

        if (openPosition != null)
        {
            logger.LogStatus(DateTime.Now, "Flattening open position...");
            string exitAction = openPosition.Direction == SignalDirection.LONG ? "SELL" : "BUY";
            connector.PlaceMarketOrder(contract, exitAction, openPosition.Contracts);
            Thread.Sleep(3000); // Wait for fill
        }

        connector.CancelAllOrders();
        Thread.Sleep(2000);
        connector.Disconnect();

        logger.LogStatus(DateTime.Now, "Engine stopped");
        logger.Dispose();
    }

    public void Stop()
    {
        isRunning = false;
    }

    public void PrintSummary()
    {
        Console.WriteLine($"\n══════════════════════════════════════════");
        Console.WriteLine($"  SESSION SUMMARY - {asset}");
        Console.WriteLine($"══════════════════════════════════════════\n");
        Console.WriteLine($"  Trades: {totalTrades} ({wins}W / {losses}L)");
        Console.WriteLine($"  Win Rate: {(totalTrades > 0 ? (decimal)wins / totalTrades * 100 : 0):F1}%");
        Console.WriteLine($"  Total P&L: ${totalPnL:F2}");
        Console.WriteLine($"  Final Balance: ${currentBalance:F2}");
        Console.WriteLine($"  Log: {logger?.LogPath ?? "N/A"}");
    }

    private void OnNew15MinBar()
    {
        var bars15m = aggregator.Bars15Min;
        var bars1h = aggregator.Bars1Hour;

        if (bars15m.Count < 50) return;

        var lastBar = bars15m.Last();
        var now = lastBar.Timestamp;

        // BAR_EXPIRED: when a new bar fires, cancel any unconfirmed limit entry from the prior bar.
        // The price did not return to the EMA level in time — the setup is stale.
        if (openPosition != null && !_entryConfirmed && entryOrderId != null)
        {
            logger.LogStatus(now, $"BAR_EXPIRED: entry #{entryOrderId} cancelled — price did not return to EMA in the prior bar");
            logger.LogExit(now, openPosition, openPosition.EntryPrice, 0m, "BAR_EXPIRED");

            if (entryOrderId.HasValue)  connector.CancelOrder(entryOrderId.Value);
            if (stopOrderId.HasValue)   connector.CancelOrder(stopOrderId.Value);
            if (targetOrderId.HasValue) connector.CancelOrder(targetOrderId.Value);

            if (strategy is TTMSqueezePullbackStrategy ttmExpired)
                ttmExpired.SetLastExitBar(barIndex);

            openPosition = null;
            entryOrderId = null;
            stopOrderId = null;
            targetOrderId = null;
            _entryConfirmed = false;
        }

        // Recalculate indicators (safe - skips existing via ContainsKey)
        IndicatorHelper.AddAllIndicators(bars15m);
        IndicatorHelper.AddAllIndicators(bars1h);

        logger.LogBar(now, bars15m.Count, bars1h.Count, lastBar.Close);

        // Check for exit if position open
        if (openPosition != null)
        {
            var (exitAction, exitPrice) = strategy.ManageExit(bars15m, openPosition);

            if (exitAction == ExitAction.BREAKEVEN || exitAction == ExitAction.TRAIL)
            {
                // Update stop on IBKR
                if (stopOrderId.HasValue && exitPrice.HasValue)
                {
                    var oldStop = openPosition.StopLoss;
                    decimal newStop = RoundToTick(exitPrice.Value);
                    if (newStop != oldStop)
                    {
                        string action = openPosition.Direction == SignalDirection.LONG ? "SELL" : "BUY";
                        connector.ModifyStopOrder(stopOrderId.Value, contract, action, newStop, openPosition.Contracts);
                        logger.LogStopUpdate(now, oldStop, newStop);
                        openPosition.StopLoss = newStop;
                    }
                }
            }
        }

        // Check for new entry if no position and no pending entry
        if (openPosition == null && entryOrderId == null)
        {
            var current1h = bars1h.LastOrDefault(b => b.Timestamp <= now);
            if (current1h == null) return;

            var setup = strategy.CheckEntry(bars15m, bars1h, "LIVE", 85m);

            if (setup != null && setup.IsValid())
            {
                var decision = riskManager.EvaluateTrade(setup, now, currentBalance);

                if (decision.Approved)
                {
                    logger.LogSignal(now, setup, true, $"Approved: {decision.Contracts} contracts");

                    // Round prices to contract minimum tick before placing (Error 110 prevention)
                    decimal roundedEntry  = RoundToTick(setup.EntryPrice);
                    decimal roundedStop   = RoundToTick(setup.StopLoss);
                    decimal roundedTarget = RoundToTick(setup.Target);

                    // Place bracket order on IBKR
                    var parentId = connector.PlaceBracketOrder(
                        contract,
                        setup.Direction,
                        roundedEntry,
                        roundedStop,
                        roundedTarget,
                        decision.Contracts
                    );

                    entryOrderId = parentId;
                    stopOrderId = parentId + 1;
                    targetOrderId = parentId + 2;
                    _entryOrderPlacedAt = DateTime.Now; // wall-clock time (not bar timestamp)

                    logger.LogOrder(now, parentId, setup.Direction == SignalDirection.LONG ? "BUY" : "SELL", "LMT", roundedEntry, decision.Contracts);
                    logger.LogOrder(now, parentId + 1, setup.Direction == SignalDirection.LONG ? "SELL" : "BUY", "STP", roundedStop, decision.Contracts);
                    logger.LogOrder(now, parentId + 2, setup.Direction == SignalDirection.LONG ? "SELL" : "BUY", "LMT", roundedTarget, decision.Contracts);

                    // Create position object (will be confirmed on fill)
                    openPosition = new Position
                    {
                        Asset = asset,
                        Direction = setup.Direction,
                        EntryPrice = roundedEntry,
                        StopLoss = roundedStop,
                        Target = roundedTarget,
                        Contracts = decision.Contracts,
                        RiskPerContract = setup.RiskPerShare * Multipliers.GetValueOrDefault(asset, 10m),
                        EntryTime = now,
                        EntryBar = barIndex,
                        EntryStrategy = setup.SetupType
                    };

                    if (strategy is TTMSqueezePullbackStrategy ttm)
                        ttm.SetLastExitBar(-999); // Reset cooldown
                }
                else
                {
                    var reason = decision.BlockedBy?.Count > 0
                        ? string.Join("; ", decision.BlockedBy)
                        : decision.Reasons?.Count > 0
                            ? decision.Reasons[0]
                            : "Unknown";
                    logger.LogSignal(now, setup, false, reason);
                }
            }
        }
    }

    private void HandleOrderStatus(int orderId, string status, decimal filled, decimal avgPrice)
    {
        var now = DateTime.Now;

        // Entry order filled — guard !_entryConfirmed so IBKR's duplicate "Filled" callbacks don't double-log
        if (orderId == entryOrderId && status == "Filled" && filled > 0 && !_entryConfirmed)
        {
            logger.LogFill(now, orderId, avgPrice, filled);
            _entryConfirmed = true;

            if (openPosition != null)
            {
                openPosition.EntryPrice = avgPrice; // Update with actual fill price
                logger.LogStatus(now, $"Position opened: {openPosition.Direction} {asset} {filled}x @ ${avgPrice:F2}");
            }
        }

        // Stop or target filled = position closed
        if ((orderId == stopOrderId || orderId == targetOrderId) && status == "Filled" && filled > 0)
        {
            logger.LogFill(now, orderId, avgPrice, filled);

            if (openPosition != null)
            {
                var multiplier = Multipliers.GetValueOrDefault(asset, 10m);
                decimal priceMove = openPosition.Direction == SignalDirection.LONG
                    ? avgPrice - openPosition.EntryPrice
                    : openPosition.EntryPrice - avgPrice;

                decimal pnl = priceMove * multiplier * openPosition.Contracts;

                string exitReason = orderId == stopOrderId ? "STOP" : "TARGET";
                logger.LogExit(now, openPosition, avgPrice, pnl, exitReason);

                // Update stats
                totalTrades++;
                totalPnL += pnl;
                currentBalance += pnl;
                if (pnl > 0) wins++;
                else if (pnl < 0) losses++;

                riskManager.RecordTradeResult(pnl, pnl > 0, now);

                if (strategy is TTMSqueezePullbackStrategy ttm)
                    ttm.SetLastExitBar(barIndex);

                // Reset position state
                openPosition = null;
                entryOrderId = null;
                stopOrderId = null;
                targetOrderId = null;
                _entryConfirmed = false;
            }
        }

        // Entry order cancelled or rejected
        if (orderId == entryOrderId && (status == "Cancelled" || status == "Inactive"))
        {
            logger.LogStatus(now, $"Entry order #{orderId} {status}");
            openPosition = null;
            entryOrderId = null;
            stopOrderId = null;
            targetOrderId = null;
            _entryConfirmed = false;
        }
    }

    private void HandlePositionUpdate(string symbol, int ibkrQty)
    {
        // Only handle our asset (IBKR reports the underlying symbol, e.g. "MGC" or "MES")
        if (!symbol.Equals(asset, StringComparison.OrdinalIgnoreCase)) return;

        _positionSeenInReconcile = true; // IBKR reported this asset in the current cycle

        var now = DateTime.Now;

        // Case A: IBKR says flat but bot thinks a position is open
        if (ibkrQty == 0 && openPosition != null)
        {
            if (_entryConfirmed)
            {
                // Entry was confirmed filled → position was closed while we were offline
                // (stop or target hit, or IBKR Mobile disconnected Gateway session)
                var lastClose = aggregator.Bars15Min.LastOrDefault()?.Close ?? openPosition.EntryPrice;

                var multiplier = Multipliers.GetValueOrDefault(asset, 10m);
                decimal priceMove = openPosition.Direction == SignalDirection.LONG
                    ? lastClose - openPosition.EntryPrice
                    : openPosition.EntryPrice - lastClose;

                decimal pnl = priceMove * multiplier * openPosition.Contracts;

                logger.LogStatus(now, $"RECONCILE: IBKR flat but bot had {openPosition.Direction} open @ ${openPosition.EntryPrice:F2} — synthetic exit @ ${lastClose:F2} (~${pnl:F2})");
                logger.LogExit(now, openPosition, lastClose, pnl, "RECONCILE");

                // Update stats
                totalTrades++;
                totalPnL += pnl;
                currentBalance += pnl;
                if (pnl > 0) wins++;
                else if (pnl < 0) losses++;

                riskManager.RecordTradeResult(pnl, pnl > 0, now);

                if (strategy is TTMSqueezePullbackStrategy ttm)
                    ttm.SetLastExitBar(barIndex);
            }
            else
            {
                // Entry fill was never confirmed → LMT never executed, or fill callback was missed.
                // Guard: if order was placed very recently, give it time to fill (avoid false positives
                // when the reconcile timer happens to fire right after order placement).
                var pendingSecs = (now - _entryOrderPlacedAt).TotalSeconds;
                if (pendingSecs < 960) // 16-minute grace period (one full bar — BAR_EXPIRED handles stale entries sooner)
                {
                    logger.LogStatus(now,
                        $"RECONCILE: entry #{entryOrderId} pending ({pendingSecs:F0}s, grace=960s) — skipping RECONCILE_NO_FILL");
                    return;
                }

                // Order has been pending > 16 minutes with no fill — treat as stale.
                logger.LogStatus(now,
                    $"RECONCILE: IBKR flat, entry for {openPosition.Direction} @ ${openPosition.EntryPrice:F2} unconfirmed — clearing stale position");

                // Log EXIT with pnl=0 so the status monitor sees the position as closed
                logger.LogExit(now, openPosition, openPosition.EntryPrice, 0m, "RECONCILE_NO_FILL");

                if (entryOrderId.HasValue)  connector.CancelOrder(entryOrderId.Value);
                if (stopOrderId.HasValue)   connector.CancelOrder(stopOrderId.Value);
                if (targetOrderId.HasValue) connector.CancelOrder(targetOrderId.Value);

                // Set cooldown so strategy doesn't immediately re-enter on the next bar
                if (strategy is TTMSqueezePullbackStrategy ttmNoFill)
                    ttmNoFill.SetLastExitBar(barIndex);
            }

            // Reset position state in both cases
            openPosition = null;
            entryOrderId = null;
            stopOrderId = null;
            targetOrderId = null;
            _entryConfirmed = false;
            return;
        }

        // Case B: IBKR has a position but bot has none — unexpected (ghost position)
        if (ibkrQty != 0 && openPosition == null)
        {
            logger.LogStatus(now, $"RECONCILE WARNING: IBKR shows {asset} qty={ibkrQty} but bot has no open position — possible ghost position, check TWS manually");
            return;
        }

        // Case C: Both IBKR and bot have a position — check sign matches
        if (ibkrQty != 0 && openPosition != null)
        {
            int expectedSign = openPosition.Direction == SignalDirection.LONG ? 1 : -1;
            if (Math.Sign(ibkrQty) != expectedSign)
            {
                // IBKR position direction contradicts our internal state — something went wrong
                // (e.g. stop hit + OCA failed → extra buy turned SHORT into LONG)
                logger.LogStatus(now,
                    $"RECONCILE MISMATCH: IBKR {asset} qty={ibkrQty} but bot expects {(openPosition.Direction == SignalDirection.LONG ? "+1 LONG" : "-1 SHORT")} — clearing bot state, check TWS");
                logger.LogExit(now, openPosition, openPosition.EntryPrice, 0m, "RECONCILE_MISMATCH");

                if (entryOrderId.HasValue)  connector.CancelOrder(entryOrderId.Value);
                if (stopOrderId.HasValue)   connector.CancelOrder(stopOrderId.Value);
                if (targetOrderId.HasValue) connector.CancelOrder(targetOrderId.Value);

                if (strategy is TTMSqueezePullbackStrategy ttmMismatch)
                    ttmMismatch.SetLastExitBar(barIndex);

                openPosition = null;
                entryOrderId = null;
                stopOrderId = null;
                targetOrderId = null;
                _entryConfirmed = false;
            }
            // else: sign matches — position is as expected, nothing to do
        }
    }

    private void HandlePositionEnd()
    {
        // IBKR finished reporting all positions. If our asset was NOT in the list,
        // it means we have no position at IBKR — treat it as qty=0.
        if (!_positionSeenInReconcile && openPosition != null)
        {
            HandlePositionUpdate(asset, 0);
        }
    }

    /// <summary>
    /// Reads today's JSONL log to reconstruct open position + today's stats after a restart.
    /// Returns true if an open position was restored.
    /// </summary>
    private bool RestoreStateFromLog()
    {
        var logDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs");
        var logPath = Path.Combine(logDir, $"trades_{asset}_{DateTime.Now:yyyy-MM-dd}.jsonl");

        if (!File.Exists(logPath)) return false;

        string? posDir = null;
        decimal posEntry = 0, posStop = 0, posTarget = 0, posRisk = 0;
        int posContracts = 0;
        string posSetup = "";
        DateTime posTime = default;
        int logEntryId = 0, logStopId = 0, logTargetId = 0;
        bool logEntryConfirmed = false;

        // Today's closed-trade stats
        int recoveredTrades = 0, recoveredWins = 0, recoveredLosses = 0;
        decimal recoveredPnL = 0;

        foreach (var line in File.ReadLines(logPath))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;

            JsonElement doc;
            try { doc = JsonDocument.Parse(line).RootElement; }
            catch { continue; }

            switch (doc.GetStringOrEmpty("type"))
            {
                case "SIGNAL":
                    if (doc.TryGetProperty("taken", out var tp) && tp.GetBoolean())
                    {
                        posDir = doc.GetStringOrEmpty("direction");
                        posEntry = doc.GetDecimalOrZero("entryPrice");
                        posStop = doc.GetDecimalOrZero("stopLoss");
                        posTarget = doc.GetDecimalOrZero("target");
                        posSetup = doc.GetStringOrEmpty("setupType");
                        posRisk = doc.GetDecimalOrZero("riskPerShare");
                        posContracts = 1;
                        var ts = doc.GetStringOrEmpty("time");
                        posTime = DateTime.TryParse(ts, out var pt) ? pt : DateTime.Now;
                        logEntryId = 0; logStopId = 0; logTargetId = 0;
                        logEntryConfirmed = false;
                    }
                    break;

                case "ORDER":
                    if (posDir != null)
                    {
                        var oType = doc.GetStringOrEmpty("orderType");
                        var oId = doc.TryGetProperty("orderId", out var oid) ? oid.GetInt32() : 0;
                        var oAction = doc.GetStringOrEmpty("action");

                        if (oType == "STP")
                            logStopId = oId;
                        else if (oType == "LMT")
                        {
                            bool isEntryDir = (posDir == "LONG" && oAction == "BUY") ||
                                             (posDir == "SHORT" && oAction == "SELL");
                            if (isEntryDir) logEntryId = oId;
                            else            logTargetId = oId;
                        }
                    }
                    break;

                case "FILL":
                    if (posDir != null && logEntryId != 0)
                    {
                        var fId = doc.TryGetProperty("orderId", out var fo) ? fo.GetInt32() : 0;
                        if (fId == logEntryId)
                        {
                            posEntry = doc.GetDecimalOrZero("fillPrice");
                            logEntryConfirmed = true;
                        }
                    }
                    break;

                case "STOP_UPDATE":
                    if (posDir != null)
                        posStop = doc.GetDecimalOrZero("newStop");
                    break;

                case "EXIT":
                    var pnl = doc.GetDecimalOrZero("pnl");
                    recoveredTrades++;
                    recoveredPnL += pnl;
                    if (pnl > 0) recoveredWins++;
                    else if (pnl < 0) recoveredLosses++;
                    // Clear position state
                    posDir = null;
                    logEntryId = 0; logStopId = 0; logTargetId = 0;
                    logEntryConfirmed = false;
                    break;
            }
        }

        // Restore today's closed-trade stats
        if (recoveredTrades > 0)
        {
            totalTrades = recoveredTrades;
            wins = recoveredWins;
            losses = recoveredLosses;
            totalPnL = recoveredPnL;
            currentBalance = startingBalance + recoveredPnL;
            logger.LogStatus(DateTime.Now,
                $"Restored today's stats: {recoveredTrades} trades ({recoveredWins}W/{recoveredLosses}L), P&L=${recoveredPnL:F2}");
        }

        // Restore open position if one was in flight
        if (posDir == null || logEntryId == 0) return false;

        var direction = posDir == "LONG" ? SignalDirection.LONG : SignalDirection.SHORT;
        openPosition = new Position
        {
            Asset = asset,
            Direction = direction,
            EntryPrice = posEntry,
            StopLoss = posStop,
            Target = posTarget,
            Contracts = posContracts,
            RiskPerContract = posRisk * Multipliers.GetValueOrDefault(asset, 10m),
            EntryTime = posTime,
            EntryBar = barIndex,
            EntryStrategy = posSetup
        };

        entryOrderId = logEntryId;
        stopOrderId  = logStopId  != 0 ? logStopId  : null;
        targetOrderId = logTargetId != 0 ? logTargetId : null;
        _entryConfirmed = logEntryConfirmed;
        _entryOrderPlacedAt = DateTime.Now; // treat restored pending order as just placed

        logger.LogStatus(DateTime.Now,
            $"Restored position: {posDir} {asset} @ ${posEntry:F2} " +
            $"stop=${posStop:F2} target=${posTarget:F2} " +
            $"entryFilled={logEntryConfirmed} " +
            $"orders: entry=#{logEntryId} stop=#{logStopId} target=#{logTargetId}");

        return true;
    }

    /// <summary>
    /// Polling fallback: requests the last 2 hours of 15-min bars and feeds any new
    /// completed bars through the aggregator. Handles the case where IBKR Gateway
    /// doesn't send historicalDataUpdate callbacks for 15-min bars.
    /// Returns false if consecutive timeouts indicate the data feed is dead (triggers restart).
    /// </summary>
    private bool PollLatestBars()
    {
        var contfutContract = new Contract
        {
            Symbol = asset,
            SecType = "CONTFUT",
            Currency = "USD",
            Exchange = asset == "MGC" ? "COMEX" : "CME"
        };

        connector.RequestHistoricalBarsDirect(asset, contfutContract, "7200 S", "15 mins", tag: "_poll");
        var rawBars = connector.GetHistoricalBars(asset, timeoutSeconds: 15, tag: "_poll");

        if (rawBars != null)
        {
            _consecutivePollFailures = 0;
            if (rawBars.Count > 0)
            {
                foreach (var hb in rawBars)
                    aggregator.UpdateStreamingBar(hb.Time, hb.Open, hb.High, hb.Low, hb.Close, hb.Volume);
            }
        }
        else // null = timeout — connection may be dead
        {
            _consecutivePollFailures++;
            logger.LogStatus(DateTime.Now,
                $"Poll timeout ({_consecutivePollFailures}/3) — IBKR data feed may be unavailable");
            if (_consecutivePollFailures >= 3)
            {
                logger.LogError(DateTime.Now,
                    $"IBKR data feed lost ({_consecutivePollFailures} consecutive poll timeouts) — triggering auto-restart");
                return false;
            }
        }

        // Force-seal the current in-progress bar if its 15-min window has elapsed.
        // This fires when historicalDataUpdate doesn't push updates (IBKR Gateway limitation
        // with keepUpToDate=true on 15-min bars). The poll above refreshes the bar's data
        // before sealing so we always close with the latest available OHLCV.
        if (aggregator.CurrentBarEndTime.HasValue && DateTime.Now >= aggregator.CurrentBarEndTime.Value)
            aggregator.ForceCompleteCurrentBar();

        return true;
    }

    private (List<Bar>? bars15m, List<Bar>? bars1h) FetchWarmupBars()
    {
        var contfutContract = new Contract
        {
            Symbol = asset,
            SecType = "CONTFUT",
            Currency = "USD",
            Exchange = asset == "MGC" ? "COMEX" : "CME"
        };

        // Fetch 15-min bars (5 days to cover holidays/weekends)
        connector.RequestHistoricalBarsDirect(asset, contfutContract, "5 D", "15 mins", tag: "_warmup_15m");
        var raw15m = connector.GetHistoricalBars(asset, timeoutSeconds: 30, tag: "_warmup_15m");

        Thread.Sleep(2000);

        // Fetch 1-hour bars (10 days for warmup)
        connector.RequestHistoricalBarsDirect(asset, contfutContract, "10 D", "1 hour", tag: "_warmup_1h");
        var raw1h = connector.GetHistoricalBars(asset, timeoutSeconds: 30, tag: "_warmup_1h");

        if (raw15m == null || raw1h == null)
            return (null, null);

        var bars15m = raw15m.Select(hb => new Bar
        {
            Timestamp = hb.Time, Open = hb.Open, High = hb.High,
            Low = hb.Low, Close = hb.Close, Volume = hb.Volume, Symbol = asset
        }).ToList();

        var bars1h = raw1h.Select(hb => new Bar
        {
            Timestamp = hb.Time, Open = hb.Open, High = hb.High,
            Low = hb.Low, Close = hb.Close, Volume = hb.Volume, Symbol = asset
        }).ToList();

        return (bars15m, bars1h);
    }
}
