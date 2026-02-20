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

    public string LogPath => logPath;

    public TradeLogger(string asset)
    {
        this.asset = asset;

        var logDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs");
        Directory.CreateDirectory(logDir);

        logPath = Path.Combine(logDir, $"trades_{asset}_{DateTime.Now:yyyy-MM-dd}.jsonl");
        writer = new StreamWriter(logPath, append: true) { AutoFlush = true };

        LogStatus(DateTime.Now, $"Logger started for {asset}");
    }

    public void LogSignal(DateTime time, TradeSetup setup, bool taken, string reason)
    {
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
            taken,
            reason
        };

        WriteAndPrint(entry, taken
            ? $"SIGNAL {setup.Direction} {asset} @ ${setup.EntryPrice:F2} → TAKEN"
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
            strategy = pos.EntryStrategy
        };

        var icon = pnl >= 0 ? "+" : "";
        WriteAndPrint(entry, $"EXIT {pos.Direction} {asset}: {icon}${pnl:F2} ({reason})");
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
