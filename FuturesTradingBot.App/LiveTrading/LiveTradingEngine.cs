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
    private bool _eodFlattenDone;     // true after EOD flatten fired today — blocks new entries
    private bool _positionSeenInReconcile; // true if IBKR reported our asset in this reconcile cycle
    private DateTime _entryOrderPlacedAt; // when the current bracket was submitted (for grace-period protection)
    private TradeSetup? _intrabarWatch;   // pending signal: place order when forming bar touches EMA zone
    private int _intrabarWatchContracts;  // contracts approved for the pending intrabar watch
    private decimal _intrabarWatchAtr;    // ATR at watch-set time — for 0.5/0.1 ATR thresholds
    private decimal currentBalance;
    private bool isRunning;
    private int barIndex;
    private DateTime _lastBarPoll = DateTime.MinValue;
    private int _consecutivePollFailures = 0;
    private DateTime _lastConnectedAt = DateTime.Now; // track last time socket was alive
    private CancellationTokenSource? _reconcilePendingCts; // debounce: wait for execDetails before logging RECONCILE
    private int? _ghostCloseOrderId; // tracks a market-close order placed for an unrecognised ghost position

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
            throw new Exception("Failed to connect to IBKR — triggering restart");
        }
        Thread.Sleep(3000);

        // 2. Resolve front-month contract by asking IBKR directly (avoids hard-coded expiry dates)
        // Wait briefly for the IBKR server connection to stabilise after the TCP handshake.
        // Error 2110 ("connectivity broken") can appear immediately after connect if the Gateway
        // is still re-establishing its own link to IB servers — give it up to 15 extra seconds.
        string exchange = asset == "MGC" ? "COMEX" : "CME";
        connector.WaitForHmds(timeoutSeconds: 15);

        // Retry loop — do NOT disconnect/throw on failure. During TWS daily reconnect the data
        // farms take several minutes to come back. Staying connected and waiting avoids the
        // crash-restart cycle (which causes Error 326 clientId conflicts and log spam).
        Contract? resolved = null;
        int contractAttempt = 0;
        while (resolved == null)
        {
            resolved = connector.ResolveFrontMonthContract(asset, "FUT", exchange);
            if (resolved == null)
            {
                contractAttempt++;
                logger.LogStatus(DateTime.Now,
                    $"Contract resolution attempt {contractAttempt} failed — IBKR data farms not ready. Waiting 90s...");
                for (int i = 0; i < 90 && isRunning; i++)
                    await Task.Delay(1000);
                if (!isRunning) return;
                connector.WaitForHmds(timeoutSeconds: 30);
            }
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
        connector.OnExecution += HandleExecution;
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

        // 8b. Cancel any orphan GTC orders for this asset left over from previous sessions.
        // Must run AFTER state restoration so we can exclude the current position's live orders.
        // Also tracks which keepIds are actually confirmed live in IBKR (to detect if a previous
        // failed restart already cancelled our stop/target — so we can re-place them).
        var liveOrderIds = new HashSet<int>(); // keepIds that IBKR confirmed as active
        {
            var orphanIds = new List<int>();
            var tcsOrders = new TaskCompletionSource<bool>();

            // IDs that belong to the current restored position — do NOT cancel these
            var keepIds = new HashSet<int>();
            if (entryOrderId.HasValue)  keepIds.Add(entryOrderId.Value);
            if (stopOrderId.HasValue)   keepIds.Add(stopOrderId.Value);
            if (targetOrderId.HasValue) keepIds.Add(targetOrderId.Value);

            connector.OnOpenOrder += (orderId, symbol) =>
            {
                if (symbol.Equals(asset, StringComparison.OrdinalIgnoreCase))
                {
                    if (!keepIds.Contains(orderId))
                        orphanIds.Add(orderId);
                    else
                        liveOrderIds.Add(orderId); // one of our position's orders is still active
                }
            };
            connector.OnOpenOrderEnd += () => tcsOrders.TrySetResult(true);

            connector.RequestAllOpenOrders();
            await Task.WhenAny(tcsOrders.Task, Task.Delay(3000)); // 3s timeout

            if (orphanIds.Count > 0)
            {
                logger.LogStatus(DateTime.Now, $"Cancelling {orphanIds.Count} orphan GTC order(s) from previous session: [{string.Join(", ", orphanIds)}]");
                foreach (var id in orphanIds)
                    connector.CancelOrder(id);
                await Task.Delay(500); // let cancellations process
            }
        }

        // 8c. If we restored a confirmed open position whose stop/target orders were already
        // cancelled by a previous failed restart, re-place them so the position is protected.
        if (stateRestored && _entryConfirmed && openPosition != null)
        {
            bool stopMissing   = !stopOrderId.HasValue   || !liveOrderIds.Contains(stopOrderId.Value);
            bool targetMissing = !targetOrderId.HasValue || !liveOrderIds.Contains(targetOrderId.Value);

            if (stopMissing || targetMissing)
            {
                logger.LogStatus(DateTime.Now,
                    $"Stop/target orders missing from IBKR (stop live={!stopMissing}, target live={!targetMissing}) " +
                    $"— re-placing exit orders to protect restored {openPosition.Direction} position");

                var (newStop, newTarget) = connector.PlaceExitOrders(
                    contract, openPosition.Direction,
                    openPosition.StopLoss, openPosition.Target);

                if (newStop > 0)
                {
                    stopOrderId   = newStop;
                    targetOrderId = newTarget;
                    string exitAction = openPosition.Direction == SignalDirection.LONG ? "SELL" : "BUY";
                    logger.LogOrder(DateTime.Now, newStop,   exitAction, "STP", openPosition.StopLoss, 1);
                    logger.LogOrder(DateTime.Now, newTarget, exitAction, "LMT", openPosition.Target,   1);
                }
            }
        }

        // CONTFUT contract for streaming (follows front-month automatically)
        var streamingContract = new Contract
        {
            Symbol = asset,
            SecType = "CONTFUT",
            Currency = "USD",
            Exchange = asset == "MGC" ? "COMEX" : "CME"
        };

        // 8. Wire streaming bar updates → aggregator + forming-bar EMA scan
        connector.OnStreamingBarUpdate += (_, t, o, h, l, c, v) =>
        {
            aggregator.UpdateStreamingBar(t, o, h, l, c, v);
            CheckIntrabarTrigger((decimal)h, (decimal)l, (decimal)c);
        };

        // 5-sec real-time bars: much more reliable than keepUpToDate 15-min updates.
        // Used exclusively for the intrabar EMA scan — gives a new tick every 5 seconds.
        connector.OnRealtimeBar += (_, t, o, h, l, c, v) =>
            CheckIntrabarTrigger((decimal)h, (decimal)l, (decimal)c);

        // Subscribe to streaming bars (keepUpToDate=true) — delivers updates within seconds
        connector.SubscribeStreamingBars(asset, streamingContract, "15 mins");
        // Subscribe to 5-sec real-time bars for the intrabar scan (uses specific front-month contract)
        connector.SubscribeRealtimeBars(asset, contract);
        logger.LogStatus(DateTime.Now, "Streaming bars subscribed (keepUpToDate=true + 5-sec realtime)...");

        // 9. Handle reconnects — re-subscribe to streaming and immediately re-check positions
        // so any fills that arrived during the outage are confirmed before LIMIT_EXPIRED fires.
        connector.OnReconnected += () =>
        {
            logger.LogStatus(DateTime.Now, "IBKR reconnected - re-subscribing to streaming bars...");
            connector.SubscribeStreamingBars(asset, streamingContract, "15 mins");
            connector.SubscribeRealtimeBars(asset, contract);
            _positionSeenInReconcile = false;
            connector.RequestPositions();
        };

        logger.LogStatus(DateTime.Now, "Live trading started (streaming mode)...");

        // 10. Main loop — bar processing driven by streaming events
        isRunning = true;
        var lastReconcile = DateTime.Now;
        // EOD flatten: prop firm rules — no overnight/weekend positions.
        // Close all positions and cancel pending orders by 21:50 CET (5 min before 21:55 deadline).
        var eodCutoff = new TimeOnly(21, 50, 0);

        while (isRunning)
        {
            await Task.Delay(5000);

            if (!connector.IsConnected)
            {
                // If socket has been dead for > 5 minutes, auto-restart (reconnect loop won't help)
                if ((DateTime.Now - _lastConnectedAt).TotalMinutes >= 5)
                {
                    logger.LogError(DateTime.Now, "IBKR socket disconnected for 5+ minutes — triggering auto-restart");
                    connector.Disconnect();
                    await Task.Delay(5000);
                    throw new Exception("IBKR socket disconnected for 5+ minutes — auto-restart triggered");
                }
                continue;
            }
            _lastConnectedAt = DateTime.Now; // reset disconnect timer

            // Bar-seal check: if the current forming bar's 15-min window has elapsed, trigger
            // a poll immediately — don't wait for the regular 90s cycle.  This closes the gap
            // where a poll fires at e.g. 10:14:30 and the next one wouldn't fire until 10:16:00,
            // causing the 10:15 bar to be evaluated a full bar late.
            // We add a 20s buffer so IBKR has time to include the freshly-completed bar.
            bool barOverdue = aggregator.CurrentBarEndTime.HasValue &&
                              DateTime.Now >= aggregator.CurrentBarEndTime.Value.AddSeconds(20) &&
                              (DateTime.Now - _lastBarPoll).TotalSeconds >= 20;

            // Polling fallback: historicalDataUpdate doesn't fire reliably for 15-min bars
            // on IBKR Gateway — poll every 90 seconds to detect new completed bars.
            if (barOverdue || (DateTime.Now - _lastBarPoll).TotalSeconds >= 90)
            {
                _lastBarPoll = DateTime.Now;
                bool pollOk = await Task.Run(() => PollLatestBars());
                if (!pollOk)
                    throw new Exception($"IBKR data feed lost ({_consecutivePollFailures} consecutive poll timeouts) — auto-restart triggered");
            }

            // EOD flatten: close all positions before prop firm overnight/weekend deadline
            var timeNow = TimeOnly.FromDateTime(DateTime.Now);
            var dayNow  = DateTime.Now.DayOfWeek;
            // Reset flag each new trading day (after midnight)
            if (timeNow < new TimeOnly(6, 0, 0)) _eodFlattenDone = false;
            // Trigger at cutoff if not yet done (also covers Friday = no weekend positions)
            bool isWeekend = dayNow == DayOfWeek.Saturday || dayNow == DayOfWeek.Sunday;
            bool pastCutoff = timeNow >= eodCutoff;
            if ((pastCutoff || isWeekend) && !_eodFlattenDone)
            {
                _eodFlattenDone = true;
                if (entryOrderId.HasValue || openPosition != null)
                {
                    logger.LogStatus(DateTime.Now, $"EOD_FLATTEN: market close approaching ({eodCutoff}) — cancelling orders and closing position");

                    if (entryOrderId.HasValue)  connector.CancelOrder(entryOrderId.Value);
                    if (stopOrderId.HasValue)   connector.CancelOrder(stopOrderId.Value);
                    if (targetOrderId.HasValue) connector.CancelOrder(targetOrderId.Value);

                    if (openPosition != null && _entryConfirmed)
                    {
                        string exitAction = openPosition.Direction == SignalDirection.LONG ? "SELL" : "BUY";
                        connector.PlaceMarketOrder(contract, exitAction, openPosition.Contracts);
                        await Task.Delay(3000); // wait for fill callback
                        logger.LogStatus(DateTime.Now, $"EOD_FLATTEN: market order placed to close {openPosition.Direction} position");
                    }

                    if (strategy is TTMSqueezePullbackStrategy ttmEod)
                        ttmEod.SetLastExitBar(barIndex);

                    openPosition = null;
                    entryOrderId = null;
                    stopOrderId = null;
                    targetOrderId = null;
                    _entryConfirmed = false;
                }
            }

            // Block new entries after EOD cutoff (including weekends)
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

        // WATCH_EXPIRED: if last bar's intrabar watch never triggered, clear it.
        if (_intrabarWatch != null && openPosition == null && entryOrderId == null)
        {
            logger.LogStatus(now,
                $"WATCH_EXPIRED: {_intrabarWatch.Direction} EMA {_intrabarWatch.EntryPrice:F2} not touched in prior bar");
            _intrabarWatch = null;
        }

        // BAR_EXPIRED: cancel any unconfirmed limit entry that was placed intrabar but not filled.
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

        // Check for new entry if no position and no pending entry or watch
        if (openPosition == null && entryOrderId == null && _intrabarWatch == null && !_eodFlattenDone)
        {
            var current1h = bars1h.LastOrDefault(b => b.Timestamp <= now);
            if (current1h == null) return;

            static decimal? GetMeta(Bar b, string key) =>
                b.Metadata != null && b.Metadata.TryGetValue(key, out var v) && v is decimal d ? d : null;

            var curBar = bars15m[^1];
            var ema15  = GetMeta(curBar, "ema_21");
            var atr15  = GetMeta(curBar, "atr_20");

            // ── Path A: bar closed at EMA → signal fires via normal CheckEntry ──
            var setup = strategy.CheckEntry(bars15m, bars1h, "LIVE", 85m);

            if (setup != null && setup.IsValid())
            {
                var decision = riskManager.EvaluateTrade(setup, now, currentBalance);
                if (decision.Approved)
                {
                    logger.LogSignal(now, setup, true, $"Approved: {decision.Contracts} contracts — intrabar watch active");
                    _intrabarWatch         = setup;
                    _intrabarWatchContracts = decision.Contracts;
                    _intrabarWatchAtr      = atr15 ?? 10m;
                }
                else
                {
                    var reason = decision.BlockedBy?.Count > 0
                        ? string.Join("; ", decision.BlockedBy)
                        : decision.Reasons?.Count > 0 ? decision.Reasons[0] : "Unknown";
                    logger.LogSignal(now, setup, false, reason);
                }
            }
            // ── Path B: bar did NOT close at EMA, but other conditions may be met ──
            // Create a synthetic bar with close = EMA21 to test pre-conditions without
            // requiring the actual close to have been at EMA. This lets us watch the NEXT
            // forming bar for an intrabar EMA touch even when the closed bar's close was above.
            else if (ema15.HasValue && atr15.HasValue && bars15m.Count >= 2 && bars1h.Count >= 3)
            {
                var syntheticBar = new Bar
                {
                    Timestamp = curBar.Timestamp,
                    Open      = curBar.Open,
                    High      = curBar.High,
                    Low       = Math.Min(curBar.Low, ema15.Value),
                    Close     = ema15.Value,   // force close = EMA so the entry condition passes
                    Volume    = curBar.Volume,
                    Metadata  = new Dictionary<string, object?>()
                };
                var testBars = bars15m.Take(bars15m.Count - 1).ToList();
                testBars.Add(syntheticBar);
                IndicatorHelper.AddAllIndicators(testBars); // only computes the new synthetic bar

                var preSetup = strategy.CheckEntry(testBars, bars1h, "LIVE", 85m);
                if (preSetup != null && preSetup.IsValid())
                {
                    var preDecision = riskManager.EvaluateTrade(preSetup, now, currentBalance);
                    if (preDecision.Approved)
                    {
                        logger.LogStatus(now,
                            $"PRECON_WATCH: {preSetup.Direction} pre-conditions met, EMA {preSetup.EntryPrice:F2} — scanning forming bar");
                        _intrabarWatch         = preSetup;
                        _intrabarWatchContracts = preDecision.Contracts;
                        _intrabarWatchAtr      = atr15.Value;
                    }
                }
                else
                {
                    // Log why no signal (neither path A nor B fired)
                    var d1h = bars1h[^2];
                    var ema1h  = GetMeta(d1h, "ema_21");
                    var atr1h  = GetMeta(d1h, "atr_20");
                    var mom15  = GetMeta(curBar, "ttm_momentum");
                    var pmom15 = GetMeta(bars15m[^2], "ttm_momentum");
                    string color = (mom15.HasValue && pmom15.HasValue)
                        ? FuturesTradingBot.Core.Indicators.TTMMomentum.GetHistogramColor(mom15, pmom15)
                        : "n/a";
                    bool uptrend = ema1h.HasValue && d1h.Close > ema1h.Value;
                    decimal dist = (ema1h.HasValue && atr1h.HasValue && atr1h.Value != 0)
                        ? Math.Abs(d1h.Close - ema1h.Value) / atr1h.Value : -1;
                    decimal pullDist = (atr15.Value != 0)
                        ? Math.Abs(curBar.Close - ema15.Value) / atr15.Value : -1;
                    logger.LogStatus(now,
                        $"NO_SIGNAL: 1h close={d1h.Close:F2} ema21={ema1h:F2} trend={(uptrend ? "UP" : "DN")} dist={dist:F2}ATR | " +
                        $"15m close={curBar.Close:F2} ema21={ema15:F2} pullDist={pullDist:F2}ATR mom_color={color}");
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

    /// <summary>
    /// Check whether the forming bar has touched the EMA zone and place an entry order if so.
    /// Called from both OnStreamingBarUpdate (15-min keepUpToDate) and OnRealtimeBar (5-sec).
    /// h/l/c are the bar's high, low, and last price for this update tick.
    /// </summary>
    private void CheckIntrabarTrigger(decimal h, decimal l, decimal c)
    {
        // Forming-bar EMA scan:
        //   Step 1a — bar_low ≤ EMA + 0.5 ATR  (LONG)  or  bar_high ≥ EMA - 0.5 ATR (SHORT)
        //             → EMA zone has been touched at some point during this bar
        //   Step 1b — bar_low ≥ EMA - 0.5 ATR  (LONG)  or  bar_high ≤ EMA + 0.5 ATR (SHORT)
        //             → price didn't crash THROUGH the EMA zone (too-deep pullback = bad quality)
        //   Step 2  — check current offer (≈ last traded price ≈ close of this tick):
        //             offer ≤ EMA + 0.1 ATR  → MARKET ORDER  (price is right at EMA now)
        //             offer > EMA            → LIMIT ORDER at EMA  (price bounced, wait for return)
        if (_intrabarWatch == null || openPosition != null || entryOrderId != null || _eodFlattenDone)
            return;

        decimal formingHigh  = h;
        decimal formingLow   = l;
        decimal currentOffer = c;
        decimal ema          = _intrabarWatch.EntryPrice;
        decimal atr          = _intrabarWatchAtr;

        bool emaTouched = _intrabarWatch.Direction == SignalDirection.LONG
            ? formingLow  <= ema + 0.5m * atr && formingLow  >= ema - 0.5m * atr
            : formingHigh >= ema - 0.5m * atr && formingHigh <= ema + 0.5m * atr;

        if (!emaTouched) return;

        var watch          = _intrabarWatch;
        var watchContracts = _intrabarWatchContracts;
        _intrabarWatch     = null;   // clear before placing to prevent re-entry
        var triggerTime    = DateTime.Now;

        bool priceAtEma = watch.Direction == SignalDirection.LONG
            ? currentOffer <= ema + 0.1m * atr
            : currentOffer >= ema - 0.1m * atr;

        if (priceAtEma)
        {
            logger.LogStatus(triggerTime,
                $"INTRABAR_MARKET: {watch.Direction} offer {currentOffer:F2} at EMA {ema:F2} (±0.1 ATR) — market order");
            PlaceEntryOrder(watch, watchContracts, triggerTime, isMarket: true);
        }
        else
        {
            logger.LogStatus(triggerTime,
                $"INTRABAR_LIMIT: {watch.Direction} bar touched EMA zone (low {formingLow:F2} / high {formingHigh:F2}) " +
                $"but offer {currentOffer:F2} above EMA {ema:F2} — limit order");
            PlaceEntryOrder(watch, watchContracts, triggerTime, isMarket: false);
        }
    }

    private void PlaceEntryOrder(TradeSetup setup, int contracts, DateTime barTime, bool isMarket = false)
    {
        decimal roundedEntry  = RoundToTick(setup.EntryPrice);
        decimal roundedStop   = RoundToTick(setup.StopLoss);
        decimal roundedTarget = RoundToTick(setup.Target);

        int parentId = isMarket
            ? connector.PlaceMarketBracketOrder(contract, setup.Direction, roundedStop, roundedTarget, contracts)
            : connector.PlaceBracketOrder(contract, setup.Direction, roundedEntry, roundedStop, roundedTarget, contracts);

        entryOrderId  = parentId;
        stopOrderId   = parentId + 1;
        targetOrderId = parentId + 2;
        _entryOrderPlacedAt = DateTime.Now;

        string entryAction = setup.Direction == SignalDirection.LONG ? "BUY"  : "SELL";
        string exitAction  = setup.Direction == SignalDirection.LONG ? "SELL" : "BUY";
        logger.LogOrder(barTime, parentId,     entryAction, isMarket ? "MKT" : "LMT", roundedEntry, contracts);
        logger.LogOrder(barTime, parentId + 1, exitAction,  "STP", roundedStop,   contracts);
        logger.LogOrder(barTime, parentId + 2, exitAction,  "LMT", roundedTarget, contracts);

        openPosition = new Position
        {
            Asset           = asset,
            Direction       = setup.Direction,
            EntryPrice      = roundedEntry,
            StopLoss        = roundedStop,
            Target          = roundedTarget,
            Contracts       = contracts,
            RiskPerContract = setup.RiskPerShare * Multipliers.GetValueOrDefault(asset, 10m),
            EntryTime       = barTime,
            EntryBar        = barIndex,
            EntryStrategy   = setup.SetupType
        };

        if (strategy is TTMSqueezePullbackStrategy ttm)
            ttm.SetLastExitBar(-999);
    }

    // execDetails fires on every fill — more reliable than orderStatus for bracket child orders.
    // Used as primary exit detector; HandleOrderStatus is a secondary fallback.
    private void HandleExecution(int orderId, decimal fillPrice, decimal qty)
    {
        var now = DateTime.Now;

        // Entry fill
        if (orderId == entryOrderId && qty > 0 && !_entryConfirmed)
        {
            logger.LogFill(now, orderId, fillPrice, qty);
            _entryConfirmed = true;
            if (openPosition != null)
            {
                openPosition.EntryPrice = fillPrice;
                logger.LogStatus(now, $"Position opened (exec): {openPosition.Direction} {asset} {qty}x @ ${fillPrice:F2}");
            }
            return;
        }

        // Stop or target fill
        if ((orderId == stopOrderId || orderId == targetOrderId) && qty > 0 && openPosition != null)
        {
            // Cancel any pending RECONCILE debounce — real fill is being handled now
            _reconcilePendingCts?.Cancel();
            _reconcilePendingCts = null;

            var multiplier = Multipliers.GetValueOrDefault(asset, 10m);
            decimal priceMove = openPosition.Direction == SignalDirection.LONG
                ? fillPrice - openPosition.EntryPrice
                : openPosition.EntryPrice - fillPrice;
            decimal pnl = priceMove * multiplier * openPosition.Contracts;

            string exitReason = orderId == stopOrderId ? "STOP" : "TARGET";
            logger.LogFill(now, orderId, fillPrice, qty);
            logger.LogExit(now, openPosition, fillPrice, pnl, exitReason);

            totalTrades++;
            totalPnL += pnl;
            currentBalance += pnl;
            if (pnl > 0) wins++;
            else if (pnl < 0) losses++;

            riskManager.RecordTradeResult(pnl, pnl > 0, now);

            if (strategy is TTMSqueezePullbackStrategy ttm)
                ttm.SetLastExitBar(barIndex);

            openPosition = null;
            entryOrderId = null;
            stopOrderId = null;
            targetOrderId = null;
            _entryConfirmed = false;
        }
    }

    // Called by the RECONCILE debounce (5s after flat detected) if no real fill arrived in time.
    private void ProcessReconcileExit(Position pos, int? snapStop, int? snapTarget, DateTime now)
    {
        var lastClose = aggregator.Bars15Min.LastOrDefault()?.Close ?? pos.EntryPrice;
        var multiplier = Multipliers.GetValueOrDefault(asset, 10m);
        decimal priceMove = pos.Direction == SignalDirection.LONG
            ? lastClose - pos.EntryPrice
            : pos.EntryPrice - lastClose;
        decimal pnl = priceMove * multiplier * pos.Contracts;

        logger.LogStatus(now, $"RECONCILE: IBKR flat but bot had {pos.Direction} open @ ${pos.EntryPrice:F2} — synthetic exit @ ${lastClose:F2} (~${pnl:F2})");
        logger.LogExit(now, pos, lastClose, pnl, "RECONCILE");

        if (snapStop.HasValue)   connector.CancelOrder(snapStop.Value);
        if (snapTarget.HasValue) connector.CancelOrder(snapTarget.Value);

        totalTrades++;
        totalPnL += pnl;
        currentBalance += pnl;
        if (pnl > 0) wins++;
        else if (pnl < 0) losses++;

        riskManager.RecordTradeResult(pnl, pnl > 0, now);

        if (strategy is TTMSqueezePullbackStrategy ttm)
            ttm.SetLastExitBar(barIndex);

        openPosition = null;
        entryOrderId = null;
        stopOrderId = null;
        targetOrderId = null;
        _entryConfirmed = false;
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
                // IBKR's position callback can arrive before execDetails for the fill that caused it.
                // Debounce: give execDetails 5 seconds to arrive and handle the exit properly.
                // If the real fill arrives in time, it cancels this timer and logs STOP/TARGET.
                // Otherwise, fall back to a synthetic RECONCILE exit using the last bar close.
                if (_reconcilePendingCts != null) return; // already waiting — do nothing

                var cts = new CancellationTokenSource();
                _reconcilePendingCts = cts;

                // Snapshot current state — the delayed task captures it even if fields change
                var snapPos    = openPosition;
                var snapStop   = stopOrderId;
                var snapTarget = targetOrderId;

                Task.Delay(5000, cts.Token).ContinueWith(t =>
                {
                    if (t.IsCanceled) return; // real fill arrived and handled it
                    _reconcilePendingCts = null;
                    if (openPosition == null) return; // real fill already cleared it
                    ProcessReconcileExit(snapPos!, snapStop, snapTarget, DateTime.Now);
                }, TaskScheduler.Default);

                return; // don't clear state yet — let the debounce decide
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

        // Case B: IBKR has a position but bot has none — ghost position, auto-close it
        if (ibkrQty != 0 && openPosition == null)
        {
            // If we already placed a close order, wait for it to fill before placing another
            if (_ghostCloseOrderId.HasValue)
            {
                logger.LogStatus(now, $"RECONCILE: ghost close order #{_ghostCloseOrderId} pending — waiting for fill");
                return;
            }

            string closeAction = ibkrQty > 0 ? "SELL" : "BUY";
            int absQty = Math.Abs(ibkrQty);
            logger.LogStatus(now,
                $"RECONCILE: ghost position detected ({asset} qty={ibkrQty}) — placing market {closeAction} {absQty}x to flatten");
            int closeId = connector.PlaceMarketOrder(contract, closeAction, absQty);
            if (closeId > 0)
                _ghostCloseOrderId = closeId;
            return;
        }

        // Clear ghost-close tracker once IBKR confirms flat
        if (ibkrQty == 0 && _ghostCloseOrderId.HasValue)
            _ghostCloseOrderId = null;

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
            else if (!_entryConfirmed)
            {
                // IBKR has a matching position but we never received the fill callback.
                // This happens when a fill arrives during a reconnect. Confirm now so
                // LIMIT_EXPIRED / BAR_EXPIRED don't cancel what is actually an open trade.
                _entryConfirmed = true;
                logger.LogFill(now, entryOrderId!.Value, openPosition.EntryPrice, 1m);
                logger.LogStatus(now,
                    $"RECONCILE: confirmed {openPosition.Direction} fill for entry #{entryOrderId} via position check (fill callback lost during reconnect)");
            }
            // else: sign matches and fill confirmed — position is as expected, nothing to do
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
        // Use the same path the logger is writing to — avoids mismatch between
        // bin/Debug/logs (old path) and solution-root/logs (current path).
        var logPath = logger.LogPath;

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
                        var oPrice = doc.GetDecimalOrZero("price"); // already tick-rounded

                        if (oType == "STP")
                        {
                            logStopId = oId;
                            if (oPrice > 0) posStop = oPrice; // prefer rounded ORDER price over raw SIGNAL price
                        }
                        else if (oType == "MKT")
                        {
                            // Market bracket entry — always the entry order
                            bool isEntryDir = (posDir == "LONG" && oAction == "BUY") ||
                                             (posDir == "SHORT" && oAction == "SELL");
                            if (isEntryDir) logEntryId = oId;
                        }
                        else if (oType == "LMT")
                        {
                            bool isEntryDir = (posDir == "LONG" && oAction == "BUY") ||
                                             (posDir == "SHORT" && oAction == "SELL");
                            if (isEntryDir)
                                logEntryId = oId;
                            else
                            {
                                logTargetId = oId;
                                if (oPrice > 0) posTarget = oPrice; // prefer rounded ORDER price
                            }
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

        // Restore intrabar watch if the last signal was taken but no order was placed yet.
        // This means the watch was armed at bar-close but the bot restarted before the
        // EMA zone was touched. The watch is valid until the NEXT bar closes:
        //   posTime (bar open) + 15 min (bar close) + 15 min (next bar) = posTime + 30 min.
        if (posDir != null && logEntryId == 0 && openPosition == null)
        {
            var watchExpiry = posTime.AddMinutes(30);
            if (DateTime.Now < watchExpiry)
            {
                var watchDir = posDir == "LONG" ? SignalDirection.LONG : SignalDirection.SHORT;
                _intrabarWatch = new TradeSetup
                {
                    Direction    = watchDir,
                    EntryPrice   = posEntry,
                    StopLoss     = posStop,
                    Target       = posTarget,
                    RiskPerShare = posRisk,
                    SetupType    = posSetup,
                    Asset        = asset
                };
                _intrabarWatchAtr       = posRisk;      // riskPerShare ≈ stop distance ≈ ATR
                _intrabarWatchContracts = posContracts; // 1 contract (default)
                logger.LogStatus(DateTime.Now,
                    $"Restored intrabar watch: {posDir} EMA {posEntry:F2} ATR {posRisk:F2} " +
                    $"(valid until {watchExpiry:HH:mm})");
            }
            return false; // no open position to restore
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

                // Intrabar scan on the forming bar's running OHLC.
                // The last bar returned by IBKR is the current in-progress bar; its running
                // low/high captures every price touch since the bar opened — so even a brief
                // EMA dip that lasted only seconds will show up here on the next poll.
                var forming = rawBars.Last();
                CheckIntrabarTrigger(forming.High, forming.Low, forming.Close);

                // EMA drift: if watch is still armed (no order placed), recompute the EMA
                // from the last closed bar's EMA + one incremental step using the forming bar's
                // current close. In a trending market the EMA drifts 0.05–0.15 ATR per bar;
                // keeping the watch EMA current prevents misses where price touches the
                // *visible* EMA but doesn't reach our stale (lower) limit price.
                if (_intrabarWatch != null && entryOrderId == null)
                {
                    var completedBars = aggregator.Bars15Min;
                    if (completedBars.Count > 0)
                    {
                        var lastClosed = completedBars[^1];
                        if (lastClosed.Metadata != null &&
                            lastClosed.Metadata.TryGetValue("ema_21", out var emaObj) && emaObj is decimal lastEma)
                        {
                            const decimal alpha = 2m / 22m; // EMA(21) smoothing factor
                            decimal liveEma = lastEma + alpha * (forming.Close - lastEma);
                            decimal drift   = liveEma - _intrabarWatch.EntryPrice;

                            if (Math.Abs(drift) > 0.05m * _intrabarWatchAtr)
                            {
                                _intrabarWatch.EntryPrice += drift;
                                _intrabarWatch.StopLoss   += drift;
                                _intrabarWatch.Target     += drift;
                                logger.LogStatus(DateTime.Now,
                                    $"WATCH_DRIFT: EMA moved {drift:+0.00;-0.00} → watch updated to {_intrabarWatch.EntryPrice:F2}");
                            }
                        }
                    }
                }
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
