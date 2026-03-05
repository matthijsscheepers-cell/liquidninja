namespace FuturesTradingBot.App.LiveTrading;

using System.Text.Json;

/// <summary>
/// Reads today's trade logs and prints a live status dashboard.
/// Run with: dotnet run --project FuturesTradingBot.App -- --status
/// </summary>
public static class StatusMonitor
{
    public static void Run()
    {
        Console.Clear();
        var logDir = FindLogDir();
        var today = DateTime.Now.Date;

        var assets = new[] { "MGC", "MES" };
        var states = assets.Select(a => ParseLog(logDir, a, today)).ToList();

        PrintDashboard(states, today);
    }

    public static void RunWatch(int intervalSeconds = 30)
    {
        while (true)
        {
            Run();
            Console.WriteLine($"\n  [Refreshing every {intervalSeconds}s — Ctrl+C to exit]");
            Thread.Sleep(intervalSeconds * 1000);
        }
    }

    // ─────────────────────────────────────────────────────────
    //  Log parsing
    // ─────────────────────────────────────────────────────────

    private static AssetState ParseLog(string logDir, string asset, DateTime date)
    {
        var state = new AssetState { Asset = asset };
        var logPath = Path.Combine(logDir, $"trades_{asset}_{date:yyyy-MM-dd}.jsonl");

        if (!File.Exists(logPath))
        {
            state.LogFound = false;
            return state;
        }

        state.LogFound = true;
        state.LogPath = logPath;

        var lines = File.ReadAllLines(logPath);
        var recentEvents = new List<string>();

        // Watch state (signal seen, no order yet)
        string? watchDirection = null;
        decimal watchEntry = 0;

        // Pending order state (order placed, not yet filled)
        int pendingOrderId = 0;
        string? pendingDirection = null;
        decimal pendingEntry = 0;
        decimal pendingStop = 0;
        decimal pendingTarget = 0;
        string? pendingSetup = null;
        DateTime pendingTime = default;

        // Confirmed position state (filled)
        string? posDirection = null;
        decimal posEntry = 0;
        decimal posStop = 0;
        decimal posTarget = 0;
        int posContracts = 0;
        string? posSetup = null;
        DateTime posTime = default;

        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;

            JsonElement doc;
            try { doc = JsonDocument.Parse(line).RootElement; }
            catch { continue; }

            var type = doc.GetStringOrEmpty("type");
            var time = doc.GetStringOrEmpty("time");

            switch (type)
            {
                case "SIGNAL":
                    bool taken = doc.TryGetProperty("taken", out var takenProp) && takenProp.GetBoolean();
                    if (taken)
                    {
                        watchDirection = doc.GetStringOrEmpty("direction");
                        watchEntry = doc.GetDecimalOrZero("entryPrice");
                        state.SignalsTaken++;
                        recentEvents.Add($"[{time[11..]}] SIGNAL {watchDirection} @ ${watchEntry:F2} → WATCH ARMED");
                    }
                    else
                    {
                        watchDirection = null;
                        state.SignalsSkipped++;
                        var reason = doc.GetStringOrEmpty("reason");
                        var dir = doc.GetStringOrEmpty("direction");
                        var ep = doc.GetDecimalOrZero("entryPrice");
                        recentEvents.Add($"[{time[11..]}] SIGNAL {dir} @ ${ep:F2} → SKIP: {reason}");
                    }
                    break;

                case "ORDER":
                    var orderId = doc.TryGetProperty("orderId", out var oid) ? oid.GetInt32() : 0;
                    var orderType = doc.GetStringOrEmpty("orderType");
                    var action = doc.GetStringOrEmpty("action");
                    var price = doc.GetDecimalOrZero("price");
                    // Entry order only (BUY for LONG, SELL for SHORT).
                    // Stop and target children have the opposite action, so they're excluded.
                    bool isEntryOrder = (watchDirection == "LONG" && action == "BUY") ||
                                        (watchDirection == "SHORT" && action == "SELL");
                    if (isEntryOrder)
                    {
                        pendingOrderId = orderId;
                        pendingDirection = watchDirection;
                        pendingEntry = price > 0 ? price : watchEntry;
                        pendingStop = doc.GetDecimalOrZero("stopLoss");
                        pendingTarget = doc.GetDecimalOrZero("target");
                        pendingSetup = doc.GetStringOrEmpty("setupType");
                        pendingTime = DateTime.TryParse(time, out var ot) ? ot : DateTime.Now;
                        watchDirection = null;
                    }
                    recentEvents.Add($"[{time[11..]}] ORDER #{orderId} {action} {orderType} @ ${price:F2}");
                    break;

                case "FILL":
                    var fillPrice = doc.GetDecimalOrZero("fillPrice");
                    var fillId = doc.TryGetProperty("orderId", out var fo) ? fo.GetInt32() : 0;
                    if (fillId == pendingOrderId && pendingDirection != null)
                    {
                        // Entry fill → open position
                        posDirection = pendingDirection;
                        posEntry = fillPrice > 0 ? fillPrice : pendingEntry;
                        posStop = pendingStop;
                        posTarget = pendingTarget;
                        posSetup = pendingSetup;
                        posContracts = 1;
                        posTime = pendingTime;
                        pendingOrderId = 0;
                        pendingDirection = null;
                    }
                    recentEvents.Add($"[{time[11..]}] FILL #{fillId} @ ${fillPrice:F2}");
                    break;

                case "STOP_UPDATE":
                    var newStop = doc.GetDecimalOrZero("newStop");
                    var oldStop = doc.GetDecimalOrZero("oldStop");
                    if (posDirection != null) posStop = newStop;
                    recentEvents.Add($"[{time[11..]}] STOP ${oldStop:F2} → ${newStop:F2}");
                    break;

                case "EXIT":
                    var pnl = doc.GetDecimalOrZero("pnl");
                    var exitReason = doc.GetStringOrEmpty("reason");
                    var exitQuality = doc.GetStringOrEmpty("exitQuality");
                    // Fall back to deriving quality from reason for older log entries without the field
                    bool isRealExit = exitQuality == "real" ||
                                      (exitQuality == "" && (exitReason == "TARGET" || exitReason == "STOP"));
                    if (isRealExit)
                    {
                        state.RealPnL += pnl;
                        state.RealTrades++;
                        if (pnl > 0) state.RealWins++;
                        else if (pnl < 0) state.RealLosses++;
                    }
                    else
                    {
                        state.EstimatedPnL += pnl;
                        state.EstimatedTrades++;
                    }
                    var exitPrice = doc.GetDecimalOrZero("exitPrice");
                    recentEvents.Add($"[{time[11..]}] EXIT {exitReason} @ ${exitPrice:F2} → {(pnl >= 0 ? "+" : "")}${pnl:F2}{(isRealExit ? "" : " (est.)")}");
                    // Clear both confirmed position and pending order state
                    posDirection = null;
                    posEntry = 0; posStop = 0; posTarget = 0;
                    pendingOrderId = 0;
                    pendingDirection = null;
                    pendingEntry = 0; pendingStop = 0; pendingTarget = 0;
                    break;

                case "BAR":
                    state.LastBarTime = doc.GetStringOrEmpty("time");
                    state.LastClose = doc.GetDecimalOrZero("lastClose");
                    state.Bars15m = doc.TryGetProperty("bars15m", out var b15) ? b15.GetInt32() : 0;
                    state.Bars1h = doc.TryGetProperty("bars1h", out var b1h) ? b1h.GetInt32() : 0;
                    break;

                case "STATUS":
                    var msg = doc.GetStringOrEmpty("message");
                    if (!msg.StartsWith("Logger") && !msg.StartsWith("Poll got") && !msg.StartsWith("New bar"))
                        recentEvents.Add($"[{time[11..]}] {msg}");
                    break;

                case "ERROR":
                    var err = doc.GetStringOrEmpty("error");
                    recentEvents.Add($"[{time[11..]}] ERROR: {err}");
                    break;
            }
        }

        // Capture open position (confirmed fill) or pending order
        if (posDirection != null)
        {
            state.OpenPosition = new OpenPosition
            {
                Direction = posDirection,
                EntryPrice = posEntry,
                StopLoss = posStop,
                Target = posTarget,
                Contracts = posContracts,
                Setup = posSetup ?? "",
                EntryTime = posTime,
                Confirmed = true
            };
        }
        else if (pendingDirection != null)
        {
            state.OpenPosition = new OpenPosition
            {
                Direction = pendingDirection,
                EntryPrice = pendingEntry,
                StopLoss = pendingStop,
                Target = pendingTarget,
                Contracts = 1,
                Setup = pendingSetup ?? "",
                EntryTime = pendingTime,
                Confirmed = false  // order placed, not yet filled
            };
        }
        else if (watchDirection != null)
        {
            state.WatchDirection = watchDirection;
            state.WatchEntry = watchEntry;
        }

        // Keep last 8 non-trivial events
        state.RecentEvents = recentEvents.TakeLast(8).ToList();

        return state;
    }

    // ─────────────────────────────────────────────────────────
    //  Dashboard rendering
    // ─────────────────────────────────────────────────────────

    private static void PrintDashboard(List<AssetState> states, DateTime date)
    {
        var width = 60;
        var line = new string('═', width);
        var thin = new string('─', width);

        Console.WriteLine($"\n  {line}");
        Console.WriteLine($"  ██ LIQUIDNINJA — STATUS DASHBOARD");
        Console.WriteLine($"  {line}");
        Console.WriteLine($"  {date:dddd dd MMM yyyy}  {DateTime.Now:HH:mm:ss} {TimeZoneInfo.Local.Id}");
        Console.WriteLine($"  {thin}");

        foreach (var state in states)
        {
            Console.WriteLine();
            Console.WriteLine($"  ── {state.Asset} ─────────────────────────────────────────");

            if (!state.LogFound)
            {
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine($"     No log file found — bot not running today");
                Console.ResetColor();
                continue;
            }

            // Position block
            if (state.OpenPosition != null)
            {
                var pos = state.OpenPosition;
                var dirColor = pos.Direction == "LONG" ? ConsoleColor.Green : ConsoleColor.Red;
                var label = pos.Confirmed ? "POSITION" : "PENDING ";
                Console.Write($"  {label}: ");
                Console.ForegroundColor = dirColor;
                Console.Write($"{pos.Direction}");
                Console.ResetColor();
                var statusSuffix = pos.Confirmed ? "" : "  ⏳ awaiting fill";
                Console.WriteLine($"  {state.Asset} {pos.Contracts}x  ({pos.Setup})  since {pos.EntryTime:HH:mm}{statusSuffix}");
                Console.WriteLine($"     Entry:  ${pos.EntryPrice:F2}");
                Console.WriteLine($"     Stop:   ${pos.StopLoss:F2}");
                Console.WriteLine($"     Target: ${pos.Target:F2}");

                if (pos.Confirmed && state.LastClose > 0)
                {
                    decimal unrealized = pos.Direction == "LONG"
                        ? state.LastClose - pos.EntryPrice
                        : pos.EntryPrice - state.LastClose;
                    var multiplier = state.Asset == "MGC" ? 10m : 5m;
                    decimal unrealizedPnL = unrealized * multiplier * pos.Contracts;
                    var uColor = unrealizedPnL >= 0 ? ConsoleColor.Green : ConsoleColor.Red;
                    Console.Write($"     Unreal: ");
                    Console.ForegroundColor = uColor;
                    Console.WriteLine($"{(unrealizedPnL >= 0 ? "+" : "")}${unrealizedPnL:F2}  (last ${state.LastClose:F2})");
                    Console.ResetColor();
                }
            }
            else if (state.WatchDirection != null)
            {
                Console.ForegroundColor = ConsoleColor.DarkYellow;
                Console.WriteLine($"  WATCH:    {state.WatchDirection} scanning forming bar for EMA ${state.WatchEntry:F2}");
                Console.ResetColor();
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine($"  POSITION: None");
                Console.ResetColor();
            }

            // Today's stats — real (STOP/TARGET fills) shown prominently
            Console.WriteLine();
            Console.Write($"  TODAY:    {state.RealTrades} real  ({state.RealWins}W / {state.RealLosses}L)   P&L: ");
            Console.ForegroundColor = state.RealPnL >= 0 ? ConsoleColor.Green : ConsoleColor.Red;
            Console.Write($"{(state.RealPnL >= 0 ? "+" : "")}${state.RealPnL:F2}");
            Console.ResetColor();
            Console.WriteLine();

            if (state.EstimatedTrades > 0)
            {
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.Write($"            {state.EstimatedTrades} reconcile (estimated)  P&L: ");
                Console.Write($"{(state.EstimatedPnL >= 0 ? "+" : "")}${state.EstimatedPnL:F2}  (synthetic, unreliable)");
                Console.ResetColor();
                Console.WriteLine();
            }

            Console.WriteLine($"  SIGNALS:  {state.SignalsTaken} taken / {state.SignalsSkipped} skipped");

            if (!string.IsNullOrEmpty(state.LastBarTime))
            {
                Console.WriteLine($"  LAST BAR: {state.LastBarTime[11..16]}  close ${state.LastClose:F2}  ({state.Bars15m} bars 15m / {state.Bars1h} bars 1h)");
            }

            // Recent events
            if (state.RecentEvents.Count > 0)
            {
                Console.WriteLine();
                Console.WriteLine($"  RECENT:");
                foreach (var ev in state.RecentEvents)
                {
                    Console.ForegroundColor = ConsoleColor.DarkGray;
                    Console.Write("    ");
                    Console.ResetColor();
                    Console.WriteLine(ev);
                }
            }
        }

        Console.WriteLine();
        Console.WriteLine($"  {line}");
        Console.WriteLine();
    }

    // ─────────────────────────────────────────────────────────
    //  Data structures
    // ─────────────────────────────────────────────────────────

    private class AssetState
    {
        public string Asset { get; set; } = "";
        public bool LogFound { get; set; }
        public string? LogPath { get; set; }
        public OpenPosition? OpenPosition { get; set; }
        public string? WatchDirection { get; set; }
        public decimal WatchEntry { get; set; }
        public decimal RealPnL { get; set; }
        public int RealTrades { get; set; }
        public int RealWins { get; set; }
        public int RealLosses { get; set; }
        public decimal EstimatedPnL { get; set; }
        public int EstimatedTrades { get; set; }
        public int SignalsTaken { get; set; }
        public int SignalsSkipped { get; set; }
        public string LastBarTime { get; set; } = "";
        public decimal LastClose { get; set; }
        public int Bars15m { get; set; }
        public int Bars1h { get; set; }
        public List<string> RecentEvents { get; set; } = new();
    }

    private class OpenPosition
    {
        public string Direction { get; set; } = "";
        public decimal EntryPrice { get; set; }
        public decimal StopLoss { get; set; }
        public decimal Target { get; set; }
        public int Contracts { get; set; }
        public string Setup { get; set; } = "";
        public DateTime EntryTime { get; set; }
        public bool Confirmed { get; set; }  // false = order placed, not yet filled
    }

    private static string FindLogDir()
    {
        // Prefer persistent logs/ at solution root over bin/Debug/logs
        var dir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
        while (dir != null)
        {
            if (dir.GetFiles("*.slnx").Length > 0 || dir.GetFiles("*.sln").Length > 0)
            {
                var persistent = Path.Combine(dir.FullName, "logs");
                if (Directory.Exists(persistent)) return persistent;
            }
            dir = dir.Parent;
        }
        return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs");
    }
}

// Extension helpers for JsonElement
internal static class JsonElementExtensions
{
    public static string GetStringOrEmpty(this JsonElement el, string property)
        => el.TryGetProperty(property, out var p) && p.ValueKind == JsonValueKind.String
            ? p.GetString() ?? ""
            : "";

    public static decimal GetDecimalOrZero(this JsonElement el, string property)
        => el.TryGetProperty(property, out var p) && p.TryGetDecimal(out var d) ? d : 0m;
}
