using FuturesTradingBot.Core.Models;
using FuturesTradingBot.Core.Indicators;
using FuturesTradingBot.Core.Strategy;
using FuturesTradingBot.App.Backtesting;
using FuturesTradingBot.App.LiveTrading;
using FuturesTradingBot.RiskManagement;
using FuturesTradingBot.Execution;

// ════════════════════════════════════════════════════════════════
//  LIQUIDNINJA - Live Paper Trading / Reality Check
// ════════════════════════════════════════════════════════════════

// Emergency flatten: close any open position for an asset via market order
// Usage: dotnet run --project FuturesTradingBot.App -- --flatten MGC
if (args.Contains("--flatten"))
{
    var flattenIdx = Array.IndexOf(args, "--flatten");
    var flattenAsset = flattenIdx + 1 < args.Length ? args[flattenIdx + 1] : "MGC";
    string flattenExchange = flattenAsset == "MGC" ? "COMEX" : "CME";
    int flattenClientId = 99; // dedicated clientId to avoid conflicts

    Console.WriteLine($"⚠️  FLATTEN: closing {flattenAsset} position via market order...");

    var flattenConnector = new IbkrConnector("127.0.0.1", 7497, flattenClientId);
    if (!flattenConnector.Connect())
    {
        Console.WriteLine("❌ Failed to connect to IBKR");
        return;
    }
    Thread.Sleep(3000);

    // Resolve the front-month contract
    var flattenContract = flattenConnector.ResolveFrontMonthContract(flattenAsset, "FUT", flattenExchange);
    if (flattenContract == null)
    {
        Console.WriteLine($"❌ Could not resolve {flattenAsset} contract");
        flattenConnector.Disconnect();
        return;
    }
    Console.WriteLine($"✅ Contract: {flattenContract.Symbol} {flattenContract.LastTradeDateOrContractMonth}");

    // Request current positions first to know direction
    int ibkrQty = 0;
    var posReceived = new System.Threading.ManualResetEventSlim(false);
    flattenConnector.OnPositionUpdate += (sym, qty) =>
    {
        if (sym.Equals(flattenAsset, StringComparison.OrdinalIgnoreCase))
            ibkrQty = qty;
    };
    flattenConnector.OnPositionEnd += () => posReceived.Set();
    flattenConnector.RequestPositions();
    posReceived.Wait(TimeSpan.FromSeconds(5));

    Console.WriteLine($"📊 IBKR {flattenAsset} position: {ibkrQty}");

    if (ibkrQty == 0)
    {
        Console.WriteLine("✅ Already flat — nothing to do");
        flattenConnector.Disconnect();
        return;
    }

    // Place market order in opposite direction to flatten
    string flattenAction = ibkrQty > 0 ? "SELL" : "BUY";
    int flattenQty = Math.Abs(ibkrQty);
    Console.WriteLine($"📤 Placing {flattenAction} MKT {flattenQty}x {flattenAsset}...");
    flattenConnector.PlaceMarketOrder(flattenContract, flattenAction, flattenQty);

    // Wait for fill confirmation
    bool fillReceived = false;
    flattenConnector.OnOrderStatusChanged += (orderId, status, filled, avgPrice) =>
    {
        Console.WriteLine($"  Order #{orderId}: {status} filled={filled} @ ${avgPrice:F2}");
        if (status == "Filled") fillReceived = true;
    };

    int waited = 0;
    while (!fillReceived && waited < 15)
    {
        Thread.Sleep(1000);
        waited++;
    }

    if (fillReceived)
        Console.WriteLine($"✅ {flattenAsset} flattened successfully");
    else
        Console.WriteLine($"⚠️  No fill confirmation within 15s — check TWS/Gateway");

    Thread.Sleep(1000);
    flattenConnector.Disconnect();
    return;
}

// Live trading mode
if (args.Contains("--live"))
{
    var asset = args.Contains("--asset")
        ? args[Array.IndexOf(args, "--asset") + 1]
        : "MGC";
    await LiveProgram.Run(asset);
    return;
}

// Status dashboard
// Usage: --status          → single snapshot
//        --status 5        → watch mode, refresh every 5 seconds
//        --status --watch  → watch mode, 30s default
if (args.Contains("--status"))
{
    var statusIdx = Array.IndexOf(args, "--status");
    int watchInterval = 0;

    // --status <N>  → watch with N-second interval
    if (statusIdx + 1 < args.Length && int.TryParse(args[statusIdx + 1], out var parsed))
        watchInterval = parsed;
    // --status --watch [--interval N]
    else if (args.Contains("--watch"))
        watchInterval = args.Contains("--interval")
            ? int.Parse(args[Array.IndexOf(args, "--interval") + 1])
            : 30;

    if (watchInterval > 0)
        StatusMonitor.RunWatch(watchInterval);
    else
        StatusMonitor.Run();
    return;
}

// ════════════════════════════════════════════════════════════════
//  Backtest / Reality Check Mode
//  Strategy: TTM Squeeze Pullback + Trend Ride
//  Mode: 1 contract, $25K challenge, $1,250 daily loss limit
// ════════════════════════════════════════════════════════════════

int clientId = 1;
decimal startingBalance = 25000m;
decimal maxDailyLoss = 1250m;

// ══════════════════════════════════════════════════════
//  STEP 1: FETCH DATA (once per asset)
// ══════════════════════════════════════════════════════

Console.WriteLine("LIQUIDNINJA REALITY CHECK");
Console.WriteLine("════════════════════════════════════════════\n");

Console.WriteLine("STEP 1: Fetching historical data...\n");

var (mgcBars15m, mgcBars1h) = FetchData("MGC", "COMEX", ref clientId);
var (mesBars15m, mesBars1h) = FetchData("MES", "CME", ref clientId);

// ══════════════════════════════════════════════════════
//  STEP 2: RUN FULL ANALYSIS PER ASSET
// ══════════════════════════════════════════════════════

BacktestResult? mgcResult = null;
BacktestResult? mesResult = null;

if (mgcBars15m != null && mgcBars1h != null)
{
    Console.WriteLine("\n\n════════════════════════════════════════════");
    Console.WriteLine("FULL ANALYSIS - MGC GOLD");
    Console.WriteLine("════════════════════════════════════════════\n");
    mgcResult = RunFullAnalysis("MGC", mgcBars15m, mgcBars1h);
}

if (mesBars15m != null && mesBars1h != null)
{
    Console.WriteLine("\n\n════════════════════════════════════════════");
    Console.WriteLine("FULL ANALYSIS - MES S&P 500");
    Console.WriteLine("════════════════════════════════════════════\n");
    mesResult = RunFullAnalysis("MES", mesBars15m, mesBars1h);
}

// ══════════════════════════════════════════════════════
//  STEP 3: SIDE-BY-SIDE COMPARISON
// ══════════════════════════════════════════════════════

Console.WriteLine("\n════════════════════════════════════════════");
Console.WriteLine("SIDE-BY-SIDE COMPARISON");
Console.WriteLine("════════════════════════════════════════════\n");

if (mgcResult != null && mesResult != null)
{
    Console.WriteLine($"{"",24} {"MGC (Gold)",15} {"MES (S&P)",15}");
    Console.WriteLine($"{"─────────────────────────────────────────────────────────"}");
    Console.WriteLine($"{"Trades",-24} {mgcResult.TotalTrades,15} {mesResult.TotalTrades,15}");
    Console.WriteLine($"{"Win Rate",-24} {mgcResult.WinRate,14:F1}% {mesResult.WinRate,14:F1}%");
    Console.WriteLine($"{"Profit Factor",-24} {mgcResult.ProfitFactor,15:F2} {mesResult.ProfitFactor,15:F2}");
    Console.WriteLine($"{"Gross P&L",-24} {"$" + mgcResult.TotalPnL.ToString("F2"),15} {"$" + mesResult.TotalPnL.ToString("F2"),15}");

    var mgcNet = RealityCheckAnalyzer.ApplyCosts(mgcResult, "MGC");
    var mesNet = RealityCheckAnalyzer.ApplyCosts(mesResult, "MES");
    Console.WriteLine($"{"Net P&L (realistic)",-24} {"$" + mgcNet.NetPnL.ToString("F2"),15} {"$" + mesNet.NetPnL.ToString("F2"),15}");
    Console.WriteLine($"{"Cost per trade",-24} {"$" + mgcNet.CostPerTrade.ToString("F2"),15} {"$" + mesNet.CostPerTrade.ToString("F2"),15}");
    Console.WriteLine($"{"Total costs",-24} {"$" + mgcNet.TotalCosts.ToString("F2"),15} {"$" + mesNet.TotalCosts.ToString("F2"),15}");

    Console.WriteLine($"{"Max Drawdown",-24} {"$" + mgcResult.MaxDrawdown.ToString("F2"),15} {"$" + mesResult.MaxDrawdown.ToString("F2"),15}");
    Console.WriteLine($"{"Max Idle Days",-24} {mgcResult.MaxIdleDays,15} {mesResult.MaxIdleDays,15}");
    Console.WriteLine($"{"Max Consec. Losses",-24} {mgcResult.ConsecutiveLossMax,15} {mesResult.ConsecutiveLossMax,15}");
    Console.WriteLine($"{"Largest Single Loss",-24} {"$" + mgcResult.LargestSingleLoss.ToString("F2"),15} {"$" + mesResult.LargestSingleLoss.ToString("F2"),15}");
    Console.WriteLine($"{"Largest Daily Loss",-24} {"$" + mgcResult.LargestDailyLoss.ToString("F2"),15} {"$" + mesResult.LargestDailyLoss.ToString("F2"),15}");
    Console.WriteLine($"{"Pullback / Trend Ride",-24} {mgcResult.PullbackTrades + "/" + mgcResult.TrendRideTrades,15} {mesResult.PullbackTrades + "/" + mesResult.TrendRideTrades,15}");

    var mgcDays = mgcResult.DaysToTarget(2500m);
    var mesDays = mesResult.DaysToTarget(2500m);
    Console.WriteLine($"{"Days to $2,500 target",-24} {(mgcDays.HasValue ? mgcDays + " days" : "N/A"),15} {(mesDays.HasValue ? mesDays + " days" : "N/A"),15}");
}
else
{
    if (mgcResult != null) Console.WriteLine("  MGC completed, MES had no data.");
    if (mesResult != null) Console.WriteLine("  MES completed, MGC had no data.");
}

// ══════════════════════════════════════════════════════
//  STEP 3b: HISTOGRAM COLOR BREAKDOWN
// ══════════════════════════════════════════════════════

Console.WriteLine("\n════════════════════════════════════════════");
Console.WriteLine("HISTOGRAM COLOR BREAKDOWN (per direction)");
Console.WriteLine("════════════════════════════════════════════\n");

if (mgcResult != null)
{
    Console.WriteLine("── MGC ──");
    Console.Write(mgcResult.HistogramColorBreakdown());
}
if (mesResult != null)
{
    Console.WriteLine("\n── MES ──");
    Console.Write(mesResult.HistogramColorBreakdown());
}

// ══════════════════════════════════════════════════════
//  STEP 4: CHALLENGE SIMULATION
// ══════════════════════════════════════════════════════

Console.WriteLine("\n════════════════════════════════════════════");
Console.WriteLine("FUNDEDNEXT CHALLENGE SIMULATION");
Console.WriteLine("════════════════════════════════════════════\n");

decimal challengeTarget = 1250m;
decimal challengeMaxDD = 1000m;

Console.WriteLine($"  Rules: +${challengeTarget:F0} profit to pass, -${challengeMaxDD:F0} max DD = breach");
Console.WriteLine($"  Daily loss limit: ${maxDailyLoss:F0}\n");

if (mgcResult != null)
{
    Console.WriteLine("── MGC GOLD ──\n");
    SimulateChallenges(mgcResult, "MGC", challengeTarget, challengeMaxDD, maxDailyLoss);
}

if (mesResult != null)
{
    Console.WriteLine("\n── MES S&P 500 ──\n");
    SimulateChallenges(mesResult, "MES", challengeTarget, challengeMaxDD, maxDailyLoss);
}

Console.WriteLine($"\n═══════════════════════════════════════");
Console.WriteLine($"REALITY CHECK COMPLETE");
Console.WriteLine($"═══════════════════════════════════════");


// ════════════════════════════════════════════════════════════════
//  HELPER FUNCTIONS
// ════════════════════════════════════════════════════════════════

(List<Bar>? bars15m, List<Bar>? bars1h) FetchData(string symbol, string exchange, ref int nextClientId)
{
    var contract = new IBApi.Contract
    {
        Symbol = symbol,
        SecType = "CONTFUT",
        Currency = "USD",
        Exchange = exchange
    };

    // ── Fetch 1-hour bars ──
    // Try progressively shorter durations. If IBKR supports it, data arrives in seconds.
    // Use 60s timeout: if data doesn't come in 60s, it won't come at all.
    Console.WriteLine($"Fetching 1-hour bars for {symbol}...\n");
    List<HistoricalBar>? bars1hRaw = null;
    foreach (var dur1h in new[] { "2 Y", "1 Y", "60 D" })
    {
        Console.WriteLine($"  Trying {dur1h} of CONTFUT 1-hour bars...");
        var conn = new IbkrConnector("127.0.0.1", 7497, nextClientId++);
        if (!conn.Connect()) { Console.WriteLine("Connection failed!"); return (null, null); }
        Thread.Sleep(3000);
        conn.WaitForHmds(timeoutSeconds: 30);

        conn.RequestHistoricalBarsDirect(symbol, contract, dur1h, "1 hour", tag: $"_1h_{dur1h}");
        bars1hRaw = conn.GetHistoricalBars(symbol, timeoutSeconds: 60, tag: $"_1h_{dur1h}");
        conn.Disconnect();

        if (bars1hRaw != null && bars1hRaw.Count > 20)
        {
            Console.WriteLine($"  Got {bars1hRaw.Count} 1-hour bars ({bars1hRaw.First().Time:yyyy-MM-dd} to {bars1hRaw.Last().Time:yyyy-MM-dd})\n");
            break;
        }
        Console.WriteLine($"  {dur1h} returned no data, trying shorter...");
        bars1hRaw = null;
        Thread.Sleep(3000);
    }

    if (bars1hRaw == null || bars1hRaw.Count == 0)
    {
        Console.WriteLine($"No 1-hour data received for {symbol}!");
        return (null, null);
    }

    Thread.Sleep(3000);

    // ── Fetch 15-min bars ──
    // CONTFUT hard limit for 15-min: 60 D max (longer durations silently return nothing).
    // endDateTime must stay "" — CONTFUT error 10339 if set explicitly.
    Console.WriteLine($"Fetching 15-min bars for {symbol} (CONTFUT max ~60 D)...\n");
    var all15mBars = new List<HistoricalBar>();

    foreach (var duration in new[] { "60 D", "30 D", "10 D" })
    {
        Console.WriteLine($"  Trying {duration} of CONTFUT 15-min bars...");

        var conn = new IbkrConnector("127.0.0.1", 7497, nextClientId++);
        if (!conn.Connect()) { Console.WriteLine("  Connection failed"); break; }
        Thread.Sleep(3000);
        conn.WaitForHmds(timeoutSeconds: 30);

        conn.RequestHistoricalBarsDirect15m(symbol, contract, duration, "15 mins", "", tag: $"_15m_{duration}");
        var bars = conn.GetHistoricalBars(symbol, timeoutSeconds: 60, tag: $"_15m_{duration}");
        conn.Disconnect();

        if (bars != null && bars.Count > 50)
        {
            Console.WriteLine($"  Got {bars.Count} bars ({bars.First().Time:yyyy-MM-dd} to {bars.Last().Time:yyyy-MM-dd})");
            all15mBars.AddRange(bars);
            break;
        }

        Console.WriteLine($"  {duration} returned no data, trying shorter...");
        Thread.Sleep(3000);
    }

    if (all15mBars.Count == 0)
    {
        Console.WriteLine($"No 15-min data received for {symbol}!");
        return (null, null);
    }

    // Sort and deduplicate
    all15mBars = all15mBars
        .OrderBy(b => b.Time)
        .GroupBy(b => b.Time)
        .Select(g => g.First())
        .ToList();

    Console.WriteLine($"\n  Total 15-min bars: {all15mBars.Count} ({all15mBars.First().Time:yyyy-MM-dd} to {all15mBars.Last().Time:yyyy-MM-dd})\n");

    // ── Convert to Bar objects ──
    var bars15Min = all15mBars.Select(hb => new Bar
    {
        Timestamp = hb.Time, Open = hb.Open, High = hb.High,
        Low = hb.Low, Close = hb.Close, Volume = hb.Volume, Symbol = symbol
    }).ToList();

    var bars1Hour = bars1hRaw.Select(hb => new Bar
    {
        Timestamp = hb.Time, Open = hb.Open, High = hb.High,
        Low = hb.Low, Close = hb.Close, Volume = hb.Volume, Symbol = symbol
    }).ToList();

    Console.WriteLine($"  15-min: {bars15Min.Count} bars | 1-hour: {bars1Hour.Count} bars");
    Console.WriteLine($"  Price range: ${bars15Min.Min(b => b.Low):F2} - ${bars15Min.Max(b => b.High):F2}\n");

    // ── Add indicators (once, reusable) ──
    IndicatorHelper.AddAllIndicators(bars15Min);
    IndicatorHelper.AddAllIndicators(bars1Hour);

    return (bars15Min, bars1Hour);
}

BacktestResult RunFullAnalysis(string symbol, List<Bar> bars15m, List<Bar> bars1h)
{
    // ── A) BASELINE BACKTEST ──
    Console.WriteLine("── A) BASELINE BACKTEST ──\n");

    var baseResult = RunBacktest(symbol, bars15m, bars1h);
    PrintBaselineResults(baseResult, symbol);

    // ── A-2) DMI FILTER IMPACT ──
    Console.WriteLine("\n── A-2) DMI FILTER IMPACT (15m +DI/-DI alignment) ──\n");

    var noDmiStrategy = new TTMSqueezePullbackStrategy(symbol);
    noDmiStrategy.SetParameter("use_dmi_filter", false);
    var noDmiEngine = new BacktestEngine(noDmiStrategy,
        new RiskManager(AccountMode.Challenge, startingBalance, startingBalance, maxDailyLoss), symbol);
    var noDmiResult = noDmiEngine.Run(bars15m, bars1h);

    // Summary table: WITH vs WITHOUT
    int dmiTradesDelta = baseResult.TotalTrades - noDmiResult.TotalTrades;
    decimal dmiPnlDelta = baseResult.TotalPnL - noDmiResult.TotalPnL;
    decimal dmiWrDelta  = baseResult.WinRate   - noDmiResult.WinRate;

    Console.WriteLine($"  {"",22} {"WITHOUT DMI",12} {"WITH DMI",12} {"Delta",10}");
    Console.WriteLine($"  {"─────────────────────────────────────────────────────────"}");
    Console.WriteLine($"  {"Trades",-22} {noDmiResult.TotalTrades,12} {baseResult.TotalTrades,12} {dmiTradesDelta,+10}");
    Console.WriteLine($"  {"Win Rate",-22} {noDmiResult.WinRate,11:F1}% {baseResult.WinRate,11:F1}% {dmiWrDelta,+9:F1}%");
    Console.WriteLine($"  {"Gross P&L",-22} {"$"+noDmiResult.TotalPnL.ToString("F0"),12} {"$"+baseResult.TotalPnL.ToString("F0"),12} {"$"+dmiPnlDelta.ToString("+#;-#;0"),10}");
    Console.WriteLine($"  {"Profit Factor",-22} {noDmiResult.ProfitFactor,12:F2} {baseResult.ProfitFactor,12:F2}");
    Console.WriteLine($"  {"Max Drawdown",-22} {"$"+noDmiResult.MaxDrawdown.ToString("F0"),12} {"$"+baseResult.MaxDrawdown.ToString("F0"),12}");

    // Per-direction breakdown (what the filter removed)
    var noDmiLongs  = noDmiResult.Trades.Where(t => t.Direction == SignalDirection.LONG).ToList();
    var noDmiShorts = noDmiResult.Trades.Where(t => t.Direction == SignalDirection.SHORT).ToList();
    var dmiLongs    = baseResult.Trades.Where(t => t.Direction == SignalDirection.LONG).ToList();
    var dmiShorts   = baseResult.Trades.Where(t => t.Direction == SignalDirection.SHORT).ToList();

    decimal noDmiLongWr  = noDmiLongs.Count  > 0 ? (decimal)noDmiLongs.Count(t  => t.PnL > 0) / noDmiLongs.Count  * 100 : 0;
    decimal noDmiShortWr = noDmiShorts.Count > 0 ? (decimal)noDmiShorts.Count(t => t.PnL > 0) / noDmiShorts.Count * 100 : 0;
    decimal dmiLongWr    = dmiLongs.Count    > 0 ? (decimal)dmiLongs.Count(t    => t.PnL > 0) / dmiLongs.Count    * 100 : 0;
    decimal dmiShortWr   = dmiShorts.Count   > 0 ? (decimal)dmiShorts.Count(t   => t.PnL > 0) / dmiShorts.Count   * 100 : 0;

    Console.WriteLine($"\n  Direction breakdown:");
    Console.WriteLine($"  {"",22} {"WITHOUT DMI",12} {"WITH DMI",12} {"Delta",10}");
    Console.WriteLine($"  {"─────────────────────────────────────────────────────────"}");
    Console.WriteLine($"  {"LONG  trades",-22} {noDmiLongs.Count,12} {dmiLongs.Count,12} {dmiLongs.Count - noDmiLongs.Count,+10}");
    Console.WriteLine($"  {"LONG  WR",-22} {noDmiLongWr,11:F1}% {dmiLongWr,11:F1}% {dmiLongWr - noDmiLongWr,+9:F1}%");
    Console.WriteLine($"  {"LONG  P&L",-22} {"$"+noDmiLongs.Sum(t => t.PnL).ToString("F0"),12} {"$"+dmiLongs.Sum(t => t.PnL).ToString("F0"),12}");
    Console.WriteLine($"  {"SHORT trades",-22} {noDmiShorts.Count,12} {dmiShorts.Count,12} {dmiShorts.Count - noDmiShorts.Count,+10}");
    Console.WriteLine($"  {"SHORT WR",-22} {noDmiShortWr,11:F1}% {dmiShortWr,11:F1}% {dmiShortWr - noDmiShortWr,+9:F1}%");
    Console.WriteLine($"  {"SHORT P&L",-22} {"$"+noDmiShorts.Sum(t => t.PnL).ToString("F0"),12} {"$"+dmiShorts.Sum(t => t.PnL).ToString("F0"),12}");

    string dmiVerdict = dmiPnlDelta >= 0 && baseResult.WinRate >= noDmiResult.WinRate
        ? "IMPROVES strategy (higher P&L + WR)"
        : dmiPnlDelta >= 0
            ? "IMPROVES P&L but reduces trade count"
            : "REDUCES P&L — filter too aggressive for this dataset";

    Console.WriteLine($"\n  DMI verdict: {dmiVerdict}");

    // ── B) COST ANALYSIS ──
    Console.WriteLine("\n── B) COST ANALYSIS (Slippage + Commissions) ──\n");

    var slippageResult = RealityCheckAnalyzer.RunSlippageScenarios(baseResult, symbol);

    Console.WriteLine($"  {"Scenario",-22} {"Net P&L",12} {"Win Rate",10} {"PF",8} {"Still OK?",10}");
    Console.WriteLine($"  {"──────────────────────────────────────────────────────────────────"}");

    foreach (var (name, result) in slippageResult.Scenarios)
    {
        Console.WriteLine($"  {name,-22} {"$" + result.NetPnL.ToString("F2"),12} {result.NetWinRate,9:F1}% {result.NetProfitFactor,8:F2} {(result.StillProfitable ? "YES" : "NO"),10}");
    }

    Console.WriteLine($"\n  Break-even slippage: {slippageResult.BreakEvenSlippageMultiplier:F1}x normal");
    Console.WriteLine($"  Break-even cost/trade: ${slippageResult.BreakEvenCostPerTrade:F2}");

    var realistic = RealityCheckAnalyzer.ApplyCosts(baseResult, symbol, 1.0m);
    Console.WriteLine($"\n  REALISTIC SUMMARY:");
    Console.WriteLine($"    Gross P&L:    ${realistic.GrossPnL:F2}");
    Console.WriteLine($"    Total costs:  ${realistic.TotalCosts:F2} ({baseResult.TotalTrades} trades x ${realistic.CostPerTrade:F2})");
    Console.WriteLine($"    Net P&L:      ${realistic.NetPnL:F2}");
    Console.WriteLine($"    Net Win Rate: {realistic.NetWinRate:F1}%");
    Console.WriteLine($"    Net PF:       {realistic.NetProfitFactor:F2}");
    Console.WriteLine($"    Net Max DD:   ${realistic.NetMaxDrawdown:F2}");

    // ── C) WORST-CASE ANALYSIS ──
    Console.WriteLine("\n── C) WORST-CASE ANALYSIS ──\n");

    var worstCase = RealityCheckAnalyzer.AnalyzeWorstCase(baseResult);

    Console.WriteLine($"  Worst Day:  ${worstCase.WorstDayPnL:F2} on {worstCase.WorstDayDate:yyyy-MM-dd}");
    Console.WriteLine($"  Best Day:   ${worstCase.BestDayPnL:F2} on {worstCase.BestDayDate:yyyy-MM-dd}");
    Console.WriteLine($"  Worst Week: ${worstCase.WorstWeekPnL:F2} ({worstCase.WorstWeekLabel})");

    if (worstCase.TopLosingStreaks.Count > 0)
    {
        Console.WriteLine($"\n  Top Losing Streaks:");
        for (int i = 0; i < worstCase.TopLosingStreaks.Count; i++)
        {
            var streak = worstCase.TopLosingStreaks[i];
            Console.WriteLine($"    #{i + 1}: {streak}");
        }
    }

    bool worstDaySafe = Math.Abs(worstCase.WorstDayPnL) < maxDailyLoss;
    Console.WriteLine($"\n  Daily loss limit safe? {(worstDaySafe ? "YES" : "NO")} (worst: ${Math.Abs(worstCase.WorstDayPnL):F2} vs limit: ${maxDailyLoss:F2})");

    // ── D) PARAMETER SENSITIVITY ──
    Console.WriteLine("\n── D) PARAMETER SENSITIVITY ──\n");

    var paramTests = new (string Param, decimal[] Values)[]
    {
        ("trend_strength",  new[] { 0.10m, 0.15m, 0.20m, 0.25m, 0.30m }),
        ("entry_tolerance", new[] { 0.8m, 1.0m, 1.2m, 1.4m }),
        ("stop_atr",        new[] { 1.3m, 1.5m, 1.7m }),
        ("target_atr",      new[] { 1.8m, 2.0m, 2.2m }),
        ("trend_ride_threshold", new[] { 1.3m, 1.5m, 1.7m }),
        ("trend_ride_stop_atr",  new[] { 0.8m, 1.0m, 1.2m }),
    };

    var baselineDefault = symbol == "MGC"
        ? new Dictionary<string, decimal> { {"trend_strength", 0.30m}, {"entry_tolerance", 1.2m}, {"stop_atr", 1.5m}, {"target_atr", 2.0m}, {"trend_ride_threshold", 1.5m}, {"trend_ride_stop_atr", 1.0m} }
        : new Dictionary<string, decimal> { {"trend_strength", 0.30m}, {"entry_tolerance", 1.0m}, {"stop_atr", 1.5m}, {"target_atr", 2.0m}, {"trend_ride_threshold", 1.5m}, {"trend_ride_stop_atr", 1.0m} };

    int stableCount = 0;
    int totalParams = paramTests.Length;

    foreach (var (param, values) in paramTests)
    {
        var defaultVal = baselineDefault[param];
        Console.WriteLine($"  {param} (default: {defaultVal}):");

        decimal maxPnLChange = 0;

        foreach (var val in values)
        {
            var strategy = new TTMSqueezePullbackStrategy(symbol);
            strategy.SetParameter(param, val);

            var riskMgr = new RiskManager(
                AccountMode.Challenge,
                startingBalance: startingBalance,
                currentBalance: startingBalance,
                maxDailyLoss: maxDailyLoss
            );

            var engine = new BacktestEngine(strategy, riskMgr, symbol);
            var paramResult = engine.Run(bars15m, bars1h);

            var marker = val == defaultVal ? " <-- default" : "";
            var pnlChange = baseResult.TotalPnL != 0
                ? (paramResult.TotalPnL - baseResult.TotalPnL) / Math.Abs(baseResult.TotalPnL) * 100
                : 0;

            if (val != defaultVal)
                maxPnLChange = Math.Max(maxPnLChange, Math.Abs(pnlChange));

            Console.WriteLine($"    {val,5}: {paramResult.TotalTrades,4} trades, {paramResult.WinRate,5:F1}% WR, ${paramResult.TotalPnL,10:F2} ({(pnlChange >= 0 ? "+" : "")}{pnlChange:F1}%){marker}");
        }

        if (maxPnLChange > 30)
        {
            Console.WriteLine($"    ** SENSITIVE - P&L swings >{maxPnLChange:F0}% **");
        }
        else
        {
            Console.WriteLine($"    Stable (max change: {maxPnLChange:F1}%)");
            stableCount++;
        }
        Console.WriteLine();
    }

    Console.WriteLine($"  PARAMETER STABILITY: {stableCount}/{totalParams} stable");

    // ── E) MONTE CARLO SIMULATION ──
    Console.WriteLine("\n── E) MONTE CARLO SIMULATION ──\n");

    var tradePnLs = baseResult.Trades.Select(t => t.PnL).ToList();
    var monteCarlo = MonteCarloSimulator.Run(tradePnLs, 1000);

    Console.WriteLine($"  Iterations: {monteCarlo.Iterations}");
    Console.WriteLine($"  Actual backtest DD:   ${monteCarlo.ActualBacktestDD:F2}");
    Console.WriteLine($"  Average simulated DD: ${monteCarlo.AverageMaxDD:F2}");
    Console.WriteLine($"  Median simulated DD:  ${monteCarlo.MedianMaxDD:F2}");
    Console.WriteLine($"  95th percentile DD:   ${monteCarlo.Percentile95:F2}");
    Console.WriteLine($"  99th percentile DD:   ${monteCarlo.Percentile99:F2}");
    Console.WriteLine($"  Worst case DD:        ${monteCarlo.WorstCase:F2}");
    Console.WriteLine($"  Best case DD:         ${monteCarlo.BestCase:F2}");
    Console.WriteLine($"\n  {monteCarlo.Verdict}");

    bool mc95Safe = monteCarlo.Percentile95 < 2000m;
    Console.WriteLine($"  95th% DD vs $2,000 limit: {(mc95Safe ? "SAFE" : "RISKY")}");

    // ── F) FILL QUALITY SIMULATION ──
    Console.WriteLine("\n── F) FILL QUALITY SIMULATION ──\n");

    var fillQuality = RealityCheckAnalyzer.SimulateFillQuality(baseResult, symbol, 500);

    Console.WriteLine($"  Simulation: 500 iterations");
    Console.WriteLine($"  Fill model: 70% limit fill, 20% miss, 10% market order (+1 tick)");
    Console.WriteLine($"  Avg fill rate:   {fillQuality.AvgFillRate:F1}%");
    Console.WriteLine($"  Avg trade count: {fillQuality.AvgTradeCount} (vs {baseResult.TotalTrades} baseline)");
    Console.WriteLine($"  Avg net P&L:     ${fillQuality.AvgNetPnL:F2}");
    Console.WriteLine($"  Avg win rate:    {fillQuality.AvgWinRate:F1}%");
    Console.WriteLine($"  Worst case P&L:  ${fillQuality.WorstCasePnL:F2}");
    Console.WriteLine($"  Best case P&L:   ${fillQuality.BestCasePnL:F2}");

    bool fillStillProfitable = fillQuality.AvgNetPnL > 0;
    Console.WriteLine($"\n  Still profitable with realistic fills? {(fillStillProfitable ? "YES" : "NO")}");

    // ── G) FINAL VERDICT ──
    Console.WriteLine("\n══════════════════════════════════════════");
    Console.WriteLine($"  FINAL VERDICT - {symbol}");
    Console.WriteLine("══════════════════════════════════════════\n");

    Console.WriteLine($"  BASELINE:        {baseResult.TotalTrades} trades, {baseResult.WinRate:F1}% WR, ${baseResult.TotalPnL:F2} gross");
    Console.WriteLine($"  REALISTIC:       Net ${realistic.NetPnL:F2}, PF {realistic.NetProfitFactor:F2} (was {baseResult.ProfitFactor:F2})");

    var worstCaseCost = RealityCheckAnalyzer.ApplyCosts(baseResult, symbol, 2.0m);
    Console.WriteLine($"  WORST CASE:      Net ${worstCaseCost.NetPnL:F2}, PF {worstCaseCost.NetProfitFactor:F2}, {(worstCaseCost.StillProfitable ? "still profitable" : "UNPROFITABLE")}");
    Console.WriteLine($"  MONTE CARLO:     Avg DD ${monteCarlo.AverageMaxDD:F2}, 95th% ${monteCarlo.Percentile95:F2}");
    Console.WriteLine($"  PARAM STABILITY: {stableCount}/{totalParams} stable");
    Console.WriteLine($"  FILL QUALITY:    ~{fillQuality.AvgFillRate:F0}% fill rate, adj P&L ${fillQuality.AvgNetPnL:F2}");

    // Compute confidence
    bool edgeRobust = realistic.StillProfitable && worstCaseCost.StillProfitable;
    bool mcSafe = mc95Safe;
    bool paramStable = stableCount >= 4;
    bool fillOk = fillStillProfitable;
    bool challengeSafe = worstDaySafe && baseResult.MaxDrawdown < 2000m && baseResult.MaxIdleDays < 7;

    int score = (edgeRobust ? 1 : 0) + (mcSafe ? 1 : 0) + (paramStable ? 1 : 0) + (fillOk ? 1 : 0) + (challengeSafe ? 1 : 0);
    string confidence = score >= 5 ? "HIGH" : score >= 3 ? "MEDIUM" : "LOW";

    Console.WriteLine($"\n  Edge robust:     {(edgeRobust ? "YES" : "NO")}");
    Console.WriteLine($"  MC safe:         {(mcSafe ? "YES" : "NO")}");
    Console.WriteLine($"  Params stable:   {(paramStable ? "YES" : "NO")}");
    Console.WriteLine($"  Fill quality OK: {(fillOk ? "YES" : "NO")}");
    Console.WriteLine($"  Challenge safe:  {(challengeSafe ? "YES" : "NO")}");
    Console.WriteLine($"\n  VERDICT: {(edgeRobust ? "EDGE ROBUST" : "EDGE QUESTIONABLE")} | Confidence: {confidence} ({score}/5)");

    return baseResult;
}

BacktestResult RunBacktest(string symbol, List<Bar> bars15m, List<Bar> bars1h)
{
    var strategy = new TTMSqueezePullbackStrategy(symbol);
    var riskMgr = new RiskManager(
        AccountMode.Challenge,
        startingBalance: startingBalance,
        currentBalance: startingBalance,
        maxDailyLoss: maxDailyLoss
    );

    var engine = new BacktestEngine(strategy, riskMgr, symbol);
    return engine.Run(bars15m, bars1h);
}

void PrintBaselineResults(BacktestResult result, string symbol)
{
    var tradingDays = (result.EndDate - result.StartDate).TotalDays;
    var tradesPerDay = tradingDays > 0 ? result.TotalTrades / (decimal)tradingDays : 0;

    Console.WriteLine($"\n  PERIOD: {result.StartDate:yyyy-MM-dd} to {result.EndDate:yyyy-MM-dd} ({tradingDays:F0} days)\n");

    Console.WriteLine($"  TRADES:");
    Console.WriteLine($"    Total: {result.TotalTrades} ({tradesPerDay:F2}/day)");
    Console.WriteLine($"    Win/Loss/BE: {result.WinningTrades}W / {result.LosingTrades}L / {result.BreakevenTrades}BE");
    Console.WriteLine($"    Win Rate: {result.WinRate:F1}%");
    Console.WriteLine($"    Pullback: {result.PullbackTrades} | Trend Ride: {result.TrendRideTrades}");
    Console.WriteLine($"    Approved: {result.TradesApproved} | Rejected: {result.TradesRejected}\n");

    if (result.RejectionReasons.Count > 0)
    {
        Console.WriteLine($"    Top rejections:");
        foreach (var kvp in result.RejectionReasons.OrderByDescending(r => r.Value).Take(5))
            Console.WriteLine($"      {kvp.Value}x {kvp.Key}");
        Console.WriteLine();
    }

    Console.WriteLine($"  P&L:");
    Console.WriteLine($"    Total: ${result.TotalPnL:F2}");
    Console.WriteLine($"    Avg Win: ${result.AverageWin:F2} | Avg Loss: ${result.AverageLoss:F2}");
    Console.WriteLine($"    Profit Factor: {result.ProfitFactor:F2}");
    Console.WriteLine($"    Final Balance: ${startingBalance + result.TotalPnL:F2} ({result.TotalPnL / startingBalance * 100:F1}% return)\n");

    Console.WriteLine($"  CHALLENGE SAFETY:");
    Console.WriteLine($"    Max Drawdown: ${result.MaxDrawdown:F2} (limit: $2,000)");
    Console.WriteLine($"    Largest Single Loss: ${result.LargestSingleLoss:F2} (daily limit: $1,250)");
    Console.WriteLine($"    Largest Daily Loss: ${result.LargestDailyLoss:F2} (daily limit: $1,250)");
    Console.WriteLine($"    Max Consecutive Losses: {result.ConsecutiveLossMax}");
    Console.WriteLine($"    Max Idle Days: {result.MaxIdleDays} (limit: 7 days)");

    var daysTo2500 = result.DaysToTarget(2500m);
    Console.WriteLine($"    Days to $2,500 target: {(daysTo2500.HasValue ? daysTo2500 + " days" : "not reached")}");

    bool drawdownSafe = result.MaxDrawdown < 2000m;
    bool dailyLossSafe = result.LargestDailyLoss < 1250m;
    bool inactivitySafe = result.MaxIdleDays < 7;
    bool allSafe = drawdownSafe && dailyLossSafe && inactivitySafe;

    Console.WriteLine($"\n    VERDICT: {(allSafe ? "CHALLENGE SAFE" : "REVIEW NEEDED")}");
    if (!drawdownSafe) Console.WriteLine($"      Max drawdown ${result.MaxDrawdown:F2} exceeds $2,000 limit!");
    if (!dailyLossSafe) Console.WriteLine($"      Daily loss ${result.LargestDailyLoss:F2} exceeds $1,250 limit!");
    if (!inactivitySafe) Console.WriteLine($"      Idle streak {result.MaxIdleDays} days exceeds 7-day limit!");

    // Monthly breakdown
    Console.WriteLine("\n  MONTHLY:");
    var monthlyGroups = result.Trades
        .GroupBy(t => new { t.EntryTime.Year, t.EntryTime.Month })
        .OrderBy(g => g.Key.Year).ThenBy(g => g.Key.Month);

    foreach (var month in monthlyGroups)
    {
        var monthPnL = month.Sum(t => t.PnL);
        var monthWins = month.Count(t => t.PnL > 0);
        var monthLosses = month.Count(t => t.PnL < 0);
        var monthTotal = month.Count();
        var icon = monthPnL >= 0 ? "+" : "-";
        Console.WriteLine($"    {month.Key.Year}-{month.Key.Month:D2}: {monthTotal,3} trades  {icon}${Math.Abs(monthPnL):F2}  ({monthWins}W/{monthLosses}L)");
    }
}

void SimulateChallenges(BacktestResult result, string asset, decimal target, decimal maxDD, decimal dailyLimit)
{
    var costPerTrade = RealityCheckAnalyzer.GetRoundTripCost(asset, 1.0m);
    var trades = result.Trades.OrderBy(t => t.EntryTime).ToList();

    int challengeNum = 0;
    int passed = 0;
    int breached = 0;
    var challengeResults = new List<(int Num, string Outcome, int Trades, decimal PeakPnL, decimal FinalPnL, int Days, string Detail)>();

    int tradeIndex = 0;

    while (tradeIndex < trades.Count)
    {
        challengeNum++;
        decimal pnl = 0;
        decimal peak = 0;
        decimal maxDrawdown = 0;
        int challengeTrades = 0;
        DateTime challengeStart = trades[tradeIndex].EntryTime;
        string outcome = "";
        string detail = "";

        // Track daily P&L for daily limit check
        var dailyPnL = new Dictionary<DateTime, decimal>();

        while (tradeIndex < trades.Count)
        {
            var trade = trades[tradeIndex];
            var netPnL = trade.PnL - costPerTrade;

            pnl += netPnL;
            challengeTrades++;

            // Track daily P&L
            var day = trade.EntryTime.Date;
            if (!dailyPnL.ContainsKey(day))
                dailyPnL[day] = 0;
            dailyPnL[day] += netPnL;

            if (pnl > peak) peak = pnl;
            var dd = peak - pnl;
            if (dd > maxDrawdown) maxDrawdown = dd;

            tradeIndex++;

            // Check daily loss breach
            if (dailyPnL[day] < -dailyLimit)
            {
                outcome = "BREACH";
                detail = $"Daily loss ${Math.Abs(dailyPnL[day]):F2} > ${dailyLimit:F0} on {day:MMM dd}";
                breached++;
                break;
            }

            // Check max drawdown breach
            if (maxDrawdown >= maxDD)
            {
                outcome = "BREACH";
                detail = $"Max DD ${maxDrawdown:F2} > ${maxDD:F0} (peak: +${peak:F2})";
                breached++;
                break;
            }

            // Check profit target reached
            if (pnl >= target)
            {
                outcome = "PASSED";
                int days = (trade.ExitTime - challengeStart).Days;
                detail = $"Target hit in {days} days";
                passed++;
                break;
            }
        }

        // If we ran out of trades before pass/breach
        if (outcome == "")
        {
            outcome = "INCOMPLETE";
            detail = $"Ran out of data at +${pnl:F2}";
        }

        int challengeDays = (trades[Math.Min(tradeIndex, trades.Count) - 1].ExitTime - challengeStart).Days;

        challengeResults.Add((challengeNum, outcome, challengeTrades, peak, pnl, challengeDays, detail));
    }

    // Print results
    Console.WriteLine($"  {"#",-4} {"Result",-12} {"Trades",7} {"Peak P&L",10} {"Final P&L",11} {"Days",6}  Detail");
    Console.WriteLine($"  {"────────────────────────────────────────────────────────────────────────────────────"}");

    foreach (var (num, outcome, numTrades, peakPnl, finalPnl, days, detail) in challengeResults)
    {
        var outcomeStr = outcome switch
        {
            "PASSED" => "PASSED",
            "BREACH" => "BREACH",
            _ => "INCOMPLETE"
        };
        Console.WriteLine($"  {num,-4} {outcomeStr,-12} {numTrades,7} {"$" + peakPnl.ToString("F2"),10} {"$" + finalPnl.ToString("F2"),11} {days,6}  {detail}");
    }

    int total = passed + breached;
    decimal passRate = total > 0 ? (decimal)passed / total * 100 : 0;

    Console.WriteLine($"\n  SUMMARY:");
    Console.WriteLine($"    Challenges attempted: {challengeResults.Count}");
    Console.WriteLine($"    Passed: {passed}");
    Console.WriteLine($"    Breached: {breached}");
    if (challengeResults.Any(c => c.Outcome == "INCOMPLETE"))
        Console.WriteLine($"    Incomplete: {challengeResults.Count(c => c.Outcome == "INCOMPLETE")} (ran out of trade data)");
    Console.WriteLine($"    Pass rate: {passRate:F0}% ({passed}/{total})");

    if (passed > 0)
    {
        var passedChallenges = challengeResults.Where(c => c.Outcome == "PASSED").ToList();
        Console.WriteLine($"    Avg days to pass: {passedChallenges.Average(c => c.Days):F0} days");
        Console.WriteLine($"    Avg trades to pass: {passedChallenges.Average(c => c.Trades):F0} trades");
    }

    // Challenge fee analysis (assuming $50 challenge fee based on typical prop firm)
    if (total > 0)
    {
        decimal challengeFee = 50m; // Approximate FundedNext challenge fee
        decimal fundedPayout = 1250m * 0.80m; // 80% payout of profit
        decimal totalFees = challengeResults.Count(c => c.Outcome != "INCOMPLETE") * challengeFee;
        decimal totalPayouts = passed * fundedPayout;
        decimal netFromChallenges = totalPayouts - totalFees;

        Console.WriteLine($"\n    ECONOMICS (est. ${challengeFee:F0} fee, 80% payout):");
        Console.WriteLine($"      Total fees paid:   ${totalFees:F0}");
        Console.WriteLine($"      Total payouts:     ${totalPayouts:F0} ({passed} x ${fundedPayout:F0})");
        Console.WriteLine($"      Net profit:        ${netFromChallenges:F0}");
    }
}
