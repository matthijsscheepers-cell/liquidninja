namespace FuturesTradingBot.App.LiveTrading;

/// <summary>
/// Entry point for live paper trading mode
/// </summary>
public static class LiveProgram
{
    public static async Task Run(string asset)
    {
        Console.WriteLine("══════════════════════════════════════════");
        Console.WriteLine("  LIQUIDNINJA - LIVE PAPER TRADING");
        Console.WriteLine("══════════════════════════════════════════");
        Console.WriteLine($"  Asset: {asset}");
        Console.WriteLine($"  Mode: IBKR Paper Trading (port 7497)");
        Console.WriteLine($"  Press Ctrl+C to stop gracefully");
        Console.WriteLine("══════════════════════════════════════════\n");

        var engine = new LiveTradingEngine(asset, balance: 25000m, maxDailyLoss: 1250m);

        Console.CancelKeyPress += (s, e) =>
        {
            e.Cancel = true;
            Console.WriteLine("\n[!] Shutdown requested - finishing gracefully...");
            engine.Stop();
        };

        await engine.Start();

        engine.PrintSummary();
    }
}
