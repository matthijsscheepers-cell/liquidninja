namespace FuturesTradingBot.App.LiveTrading;

/// <summary>
/// Entry point for live paper trading mode.
/// Auto-restarts the engine on crash (e.g. IBKR socket drop).
/// Ctrl+C exits cleanly without restarting.
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

        bool userRequestedStop = false;
        LiveTradingEngine? engine = null;

        Console.CancelKeyPress += (s, e) =>
        {
            e.Cancel = true;
            userRequestedStop = true;
            Console.WriteLine("\n[!] Shutdown requested - finishing gracefully...");
            engine?.Stop();
        };

        int attempt = 0;
        while (!userRequestedStop)
        {
            attempt++;
            if (attempt > 1)
                Console.WriteLine($"\n[{DateTime.Now:HH:mm:ss}] ♻️  Auto-restart attempt #{attempt}...\n");

            try
            {
                engine = new LiveTradingEngine(asset, balance: 25000m, maxDailyLoss: 1250m);
                await engine.Start();

                // engine.Start() returned normally → Stop() was called (Ctrl+C)
                break;
            }
            catch (Exception ex)
            {
                if (userRequestedStop) break;

                Console.WriteLine($"\n[{DateTime.Now:HH:mm:ss}] 💥 ENGINE CRASH: {ex.GetType().Name}: {ex.Message}");
                Console.WriteLine($"  Restarting in 30 seconds... (Ctrl+C to abort)");

                // Wait 30s but bail early if user presses Ctrl+C
                for (int i = 0; i < 30 && !userRequestedStop; i++)
                    await Task.Delay(1000);
            }
        }

        engine?.PrintSummary();
    }
}
