namespace FuturesTradingBot.App.LiveTrading;

using System.Text.Json;
using FuturesTradingBot.Core.Models;

/// <summary>
/// Append-only JSONL trade logger for live trading
/// Logs signals, orders, fills, exits, and status messages
/// </summary>
public class TradeLogger : IDisposable
{
    private readonly string asset;
    private readonly StreamWriter writer;
    private readonly string logPath;
    private readonly string historyPath;

    // Static lock so MGC and MES bots don't interleave writes to the shared CSV
    private static readonly object HistoryLock = new();

    private static readonly string[] HistoryCsvHeader =
        ["date", "time", "asset", "direction", "setup",
         "entry_price", "exit_price", "exit_reason", "exit_quality", "pnl"];

    // These exit reasons don't represent completed trades worth tracking
    private static readonly HashSet<string> SkipReasons =
        ["LIMIT_EXPIRED", "RECONCILE_NO_FILL", "BAR_EXPIRED", "RECONCILE_MISMATCH"];

    public string LogPath => logPath;

    public TradeLogger(string asset)
    {
        this.asset = asset;

        // Prefer a persistent logs folder next to the solution root, outside bin/Debug.
        // Falls back to the old bin/Debug/logs location if the solution root can't be found.
        var solutionRoot = FindSolutionRoot(AppDomain.CurrentDomain.BaseDirectory);
        var logDir = solutionRoot != null
            ? Path.Combine(solutionRoot, "logs")
            : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs");
        Directory.CreateDirectory(logDir);

        logPath     = Path.Combine(logDir, $"trades_{asset}_{DateTime.Now:yyyy-MM-dd}.jsonl");
        historyPath = Path.Combine(logDir, "trades_history.csv");
        writer = new StreamWriter(logPath, append: true) { AutoFlush = true };

        // Write CSV header if the file doesn't exist yet
        lock (HistoryLock)
        {
            if (!File.Exists(historyPath))
                File.WriteAllText(historyPath, string.Join(",", HistoryCsvHeader) + "\n");
        }

        LogStatus(DateTime.Now, $"Logger started for {asset}");
    }

    // Walk up from baseDir until we find the .slnx file — that's the solution root.
    private static string? FindSolutionRoot(string baseDir)
    {
        var dir = new DirectoryInfo(baseDir);
        while (dir != null)
        {
            if (dir.GetFiles("*.slnx").Length > 0 || dir.GetFiles("*.sln").Length > 0)
                return dir.FullName;
            dir = dir.Parent;
        }
        return null;
    }

    public void LogSignal(DateTime time, TradeSetup setup, bool taken, string reason)
    {
        setup.Metadata.TryGetValue("histogram_color", out var histColor);
        setup.Metadata.TryGetValue("distance_from_ema_atr", out var distEma);
        setup.Metadata.TryGetValue("trend_strength_atr", out var trendStr);

        var entry = new
        {
            type = "SIGNAL",
            time = time.ToString("yyyy-MM-dd HH:mm:ss"),
            asset,
            direction = setup.Direction.ToString(),
            setupType = setup.SetupType,
            entryPrice = setup.EntryPrice,
            stopLoss = setup.StopLoss,
            target = setup.Target,
            riskPerShare = setup.RiskPerShare,
            histogramColor = histColor?.ToString(),
            distanceEmaAtr = distEma is double d ? Math.Round(d, 3) : (object?)null,
            trendStrengthAtr = trendStr is double t ? Math.Round(t, 3) : (object?)null,
            taken,
            reason
        };

        WriteAndPrint(entry, taken
            ? $"SIGNAL {setup.Direction} {asset} @ ${setup.EntryPrice:F2} → WATCH ARMED (waiting for EMA touch)"
            : $"SIGNAL {setup.Direction} {asset} @ ${setup.EntryPrice:F2} → SKIPPED: {reason}");
    }

    public void LogOrder(DateTime time, int orderId, string action, string orderType, decimal price, int qty)
    {
        var entry = new
        {
            type = "ORDER",
            time = time.ToString("yyyy-MM-dd HH:mm:ss"),
            asset,
            orderId,
            action,
            orderType,
            price,
            qty
        };

        WriteAndPrint(entry, $"ORDER #{orderId}: {action} {qty}x {asset} {orderType} @ ${price:F2}");
    }

    public void LogFill(DateTime time, int orderId, decimal fillPrice, decimal filledQty)
    {
        var entry = new
        {
            type = "FILL",
            time = time.ToString("yyyy-MM-dd HH:mm:ss"),
            asset,
            orderId,
            fillPrice,
            filledQty
        };

        WriteAndPrint(entry, $"FILL #{orderId}: {filledQty}x @ ${fillPrice:F2}");
    }

    public void LogExit(DateTime time, Position pos, decimal exitPrice, decimal pnl, string reason)
    {
        var exitQuality = reason is "STOP" or "TARGET" ? "real" : "estimated";
        var entry = new
        {
            type = "EXIT",
            time = time.ToString("yyyy-MM-dd HH:mm:ss"),
            asset,
            direction = pos.Direction.ToString(),
            entryPrice = pos.EntryPrice,
            exitPrice,
            contracts = pos.Contracts,
            pnl,
            reason,
            exitQuality,
            strategy = pos.EntryStrategy
        };

        var icon = pnl >= 0 ? "+" : "";
        WriteAndPrint(entry, $"EXIT {pos.Direction} {asset}: {icon}${pnl:F2} ({reason})");

        // Append to persistent history CSV (skips non-trade exits like LIMIT_EXPIRED)
        if (!SkipReasons.Contains(reason))
            AppendHistoryCsv(time, pos, exitPrice, pnl, reason);
    }

    private void AppendHistoryCsv(DateTime time, Position pos, decimal exitPrice, decimal pnl, string reason)
    {
        // STOP and TARGET are real fills; RECONCILE is a synthetic estimate
        var quality = reason is "STOP" or "TARGET" ? "real" : "estimated";
        var row = string.Join(",",
            time.ToString("yyyy-MM-dd"),
            time.ToString("HH:mm:ss"),
            asset,
            pos.Direction.ToString(),
            pos.EntryStrategy ?? "",
            pos.EntryPrice.ToString("F2", System.Globalization.CultureInfo.InvariantCulture),
            exitPrice.ToString("F2", System.Globalization.CultureInfo.InvariantCulture),
            reason,
            quality,
            pnl.ToString("F2", System.Globalization.CultureInfo.InvariantCulture));

        lock (HistoryLock)
            File.AppendAllText(historyPath, row + "\n");
    }

    public void LogStopUpdate(DateTime time, decimal oldStop, decimal newStop)
    {
        var entry = new
        {
            type = "STOP_UPDATE",
            time = time.ToString("yyyy-MM-dd HH:mm:ss"),
            asset,
            oldStop,
            newStop
        };

        WriteAndPrint(entry, $"STOP UPDATE: ${oldStop:F2} → ${newStop:F2}");
    }

    public void LogStatus(DateTime time, string message)
    {
        var entry = new
        {
            type = "STATUS",
            time = time.ToString("yyyy-MM-dd HH:mm:ss"),
            asset,
            message
        };

        WriteAndPrint(entry, message);
    }

    public void LogError(DateTime time, string error)
    {
        var entry = new
        {
            type = "ERROR",
            time = time.ToString("yyyy-MM-dd HH:mm:ss"),
            asset,
            error
        };

        WriteAndPrint(entry, $"ERROR: {error}");
    }

    public void LogBar(DateTime time, int barCount15m, int barCount1h, decimal lastClose)
    {
        var entry = new
        {
            type = "BAR",
            time = time.ToString("yyyy-MM-dd HH:mm:ss"),
            asset,
            bars15m = barCount15m,
            bars1h = barCount1h,
            lastClose
        };

        Write(entry); // Don't print bar events to console (too noisy)
    }

    private void WriteAndPrint(object entry, string consoleMessage)
    {
        Write(entry);
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {consoleMessage}");
    }

    private void Write(object entry)
    {
        var json = JsonSerializer.Serialize(entry);
        writer.WriteLine(json);
    }

    public void Dispose()
    {
        LogStatus(DateTime.Now, "Logger stopped");
        writer.Dispose();
    }
}
