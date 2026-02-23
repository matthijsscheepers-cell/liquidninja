namespace FuturesTradingBot.Execution;

using IBApi;
using FuturesTradingBot.Core.Models;
using System.Threading;

/// <summary>
/// Connector to Interactive Brokers API
/// Handles connection, market data, and order execution
/// </summary>
public partial class IbkrConnector : EWrapper
{
    private readonly EClientSocket clientSocket;
    private readonly EReaderSignal signal;
    private EReader? reader;

    private int nextOrderId = -1;
    private bool isConnected = false;

    // Connection settings
    private readonly string host;
    private readonly int port;
    private readonly int clientId;

    public bool IsConnected => isConnected;
    public int NextOrderId => nextOrderId;

    /// <summary>
    /// Constructor
    /// </summary>
    /// <param name="host">Usually "127.0.0.1" (localhost)</param>
    /// <param name="port">7497 for paper, 7496 for live</param>
    /// <param name="clientId">Unique ID for this connection (0-32)</param>
    public IbkrConnector(string host = "127.0.0.1", int port = 7497, int clientId = 0)
    {
        this.host = host;
        this.port = port;
        this.clientId = clientId;

        signal = new EReaderMonitorSignal();
        clientSocket = new EClientSocket(this, signal);
    }

    /// <summary>
    /// Connect to TWS/Gateway
    /// </summary>
    public bool Connect()
    {
        if (isConnected)
        {
            Console.WriteLine("Already connected");
            return true;
        }

        Console.WriteLine($"Connecting to IBKR at {host}:{port} (clientId: {clientId})...");

        clientSocket.eConnect(host, port, clientId);

        // Wait a bit for connection
        Thread.Sleep(1000);

        if (!clientSocket.IsConnected())
        {
            Console.WriteLine("❌ Failed to connect to TWS/Gateway");
            Console.WriteLine("Make sure IB Gateway is running and API is enabled!");
            return false;
        }

        // Start message reader
        reader = new EReader(clientSocket, signal);
        reader.Start();

        // Start processing messages in separate thread
        new Thread(() => ProcessMessages()) { IsBackground = true }.Start();

        Console.WriteLine("✅ Connected to IBKR!");

        return true;
    }

    /// <summary>
    /// Disconnect from TWS/Gateway
    /// </summary>
    public void Disconnect()
    {
        if (clientSocket.IsConnected())
        {
            clientSocket.eDisconnect();
            isConnected = false;
            Console.WriteLine("Disconnected from IBKR");
        }
    }

    /// <summary>
    /// Process incoming messages from IBKR
    /// </summary>
    private void ProcessMessages()
    {
        while (clientSocket.IsConnected())
        {
            signal.waitForSignal();
            reader?.processMsgs();
        }
    }

    /// <summary>
    /// Request account summary
    /// </summary>
    public void RequestAccountSummary()
    {
        if (!clientSocket.IsConnected())
        {
            Console.WriteLine("Not connected!");
            return;
        }

        // Request account summary
        clientSocket.reqAccountSummary(
            9001, // request ID
            "All", // group (all accounts)
            "NetLiquidation,TotalCashValue,BuyingPower,GrossPositionValue"
        );
    }

    // ========================================
    // EWrapper Interface - Key callbacks
    // ========================================

    public void error(Exception e)
    {
        Console.WriteLine($"Exception: {e.Message}");
    }

    public void error(string str)
    {
        Console.WriteLine($"Error: {str}");
    }

    public void error(int id, int errorCode, string errorMsg)
    {
        error(id, errorCode, errorMsg, "");
    }

    public bool IsHmdsConnected { get; private set; }

    public void error(int id, int errorCode, string errorMsg, string advancedOrderRejectJson)
    {
        // Some "errors" are just informational
        if (errorCode == 2104 || errorCode == 2106 || errorCode == 2158)
        {
            // Market data farm connection messages - not real errors
            Console.WriteLine($"Info: {errorMsg}");
            // 2106 = HMDS data farm connection is OK
            if (errorCode == 2106 && errorMsg.Contains("hmds", StringComparison.OrdinalIgnoreCase))
                IsHmdsConnected = true;
        }
        else if (errorCode == 2107)
        {
            // HMDS data farm connection is inactive
            Console.WriteLine($"Info: {errorMsg}");
            if (errorMsg.Contains("hmds", StringComparison.OrdinalIgnoreCase) ||
                errorMsg.Contains("HDMS", StringComparison.OrdinalIgnoreCase))
                IsHmdsConnected = false;
        }
        else if (errorCode == 1100)
        {
            // Connection lost
            Console.WriteLine($"⚠️ IBKR connection lost - waiting for reconnect...");
        }
        else if (errorCode == 1102)
        {
            // Connection restored after disconnect
            Console.WriteLine($"✅ IBKR connection restored");
            IsHmdsConnected = true;
            OnReconnected?.Invoke();
        }
        else
        {
            Console.WriteLine($"Error {errorCode} (reqId={id}): {errorMsg}");
        }
    }

    /// <summary>
    /// Wait for HMDS (historical data) farm to connect, with timeout
    /// </summary>
    public bool WaitForHmds(int timeoutSeconds = 30)
    {
        if (IsHmdsConnected) return true;

        Console.WriteLine("⏳ Waiting for HMDS (historical data) connection...");
        var start = DateTime.Now;
        while (!IsHmdsConnected && (DateTime.Now - start).TotalSeconds < timeoutSeconds)
        {
            Thread.Sleep(500);
        }

        if (IsHmdsConnected)
            Console.WriteLine("✅ HMDS connected!");
        else
            Console.WriteLine("⚠️ HMDS still not connected after timeout - trying anyway...");

        return IsHmdsConnected;
    }

    public void connectionClosed()
    {
        Console.WriteLine("Connection closed");
        isConnected = false;
    }

    public void connectAck()
    {
        Console.WriteLine("Connection acknowledged");
        isConnected = true;

        if (clientSocket.AsyncEConnect)
            clientSocket.startApi();
    }

    public void nextValidId(int orderId)
    {
        Console.WriteLine($"Next valid order ID: {orderId}");
        nextOrderId = orderId;
    }

    public void accountSummary(int reqId, string account, string tag, string value, string currency)
    {
        Console.WriteLine($"Account {account}: {tag} = {value} {currency}");
    }

    public void accountSummaryEnd(int reqId)
    {
        Console.WriteLine("Account summary complete");
    }

    public void managedAccounts(string accountsList)
    {
        Console.WriteLine($"Managed accounts: {accountsList}");
    }

    // ========================================
    // ORDER EXECUTION
    // ========================================

    private Dictionary<int, OrderInfo> orderTracking = new();
    private Dictionary<string, int> positionTracking = new(); // symbol → qty

    public event Action<int, string, decimal, decimal>? OnOrderStatusChanged; // orderId, status, filled, avgPrice
    public event Action<string, int>? OnPositionUpdate; // symbol, qty
    public event Action? OnPositionEnd;                  // fired when all positions have been reported
    public event Action? OnReconnected; // fired when IBKR connection is restored

    /// <summary>
    /// Place a bracket order: LMT entry + STP stop + LMT target
    /// Returns the parent (entry) order ID
    /// </summary>
    public int PlaceBracketOrder(
        Contract contract,
        SignalDirection direction,
        decimal entryPrice,
        decimal stopPrice,
        decimal targetPrice,
        int quantity = 1)
    {
        if (!clientSocket.IsConnected())
        {
            Console.WriteLine("Not connected - cannot place order!");
            return -1;
        }

        int parentId = nextOrderId++;
        int stopId = nextOrderId++;
        int targetId = nextOrderId++;

        string action = direction == SignalDirection.LONG ? "BUY" : "SELL";
        string exitAction = direction == SignalDirection.LONG ? "SELL" : "BUY";
        string ocaGroup = $"LIQUIDNINJA_{parentId}";

        // Parent order: LMT entry
        var parentOrder = new Order
        {
            OrderId = parentId,
            Action = action,
            OrderType = "LMT",
            TotalQuantity = quantity,
            LmtPrice = (double)entryPrice,
            Transmit = false, // Don't transmit until children are attached
            Tif = "GTC"
        };

        // Stop loss child
        var stopOrder = new Order
        {
            OrderId = stopId,
            Action = exitAction,
            OrderType = "STP",
            TotalQuantity = quantity,
            AuxPrice = (double)stopPrice,
            ParentId = parentId,
            OcaGroup = ocaGroup,
            OcaType = 1, // Cancel on fill
            Transmit = false,
            Tif = "GTC"
        };

        // Target child
        var targetOrder = new Order
        {
            OrderId = targetId,
            Action = exitAction,
            OrderType = "LMT",
            TotalQuantity = quantity,
            LmtPrice = (double)targetPrice,
            ParentId = parentId,
            OcaGroup = ocaGroup,
            OcaType = 1,
            Transmit = true, // Transmit all orders
            Tif = "GTC"
        };

        // Track orders
        orderTracking[parentId] = new OrderInfo { OrderId = parentId, Type = "ENTRY", Status = "PendingSubmit" };
        orderTracking[stopId] = new OrderInfo { OrderId = stopId, Type = "STOP", Status = "PendingSubmit", ParentId = parentId };
        orderTracking[targetId] = new OrderInfo { OrderId = targetId, Type = "TARGET", Status = "PendingSubmit", ParentId = parentId };

        Console.WriteLine($"📤 Placing bracket order #{parentId}: {action} {quantity}x {contract.Symbol}");
        Console.WriteLine($"   Entry: ${entryPrice:F2} (LMT) | Stop: ${stopPrice:F2} (STP) | Target: ${targetPrice:F2} (LMT)");

        clientSocket.placeOrder(parentId, contract, parentOrder);
        clientSocket.placeOrder(stopId, contract, stopOrder);
        clientSocket.placeOrder(targetId, contract, targetOrder);

        return parentId;
    }

    /// <summary>
    /// Modify an existing stop order price
    /// </summary>
    public void ModifyStopOrder(int orderId, Contract contract, string action, decimal newStopPrice, int quantity)
    {
        if (!clientSocket.IsConnected()) return;

        var order = new Order
        {
            OrderId = orderId,
            Action = action,
            OrderType = "STP",
            TotalQuantity = quantity,
            AuxPrice = (double)newStopPrice,
            Tif = "GTC"
        };

        Console.WriteLine($"📝 Modifying stop #{orderId} → ${newStopPrice:F2}");
        clientSocket.placeOrder(orderId, contract, order);
    }

    /// <summary>
    /// Place a market order to flatten a position
    /// </summary>
    public int PlaceMarketOrder(Contract contract, string action, int quantity)
    {
        if (!clientSocket.IsConnected()) return -1;

        int orderId = nextOrderId++;
        var order = new Order
        {
            OrderId = orderId,
            Action = action,
            OrderType = "MKT",
            TotalQuantity = quantity,
            Transmit = true,
            Tif = "GTC"
        };

        orderTracking[orderId] = new OrderInfo { OrderId = orderId, Type = "FLATTEN", Status = "PendingSubmit" };

        Console.WriteLine($"📤 Placing market order #{orderId}: {action} {quantity}x {contract.Symbol}");
        clientSocket.placeOrder(orderId, contract, order);

        return orderId;
    }

    /// <summary>
    /// Cancel a specific order
    /// </summary>
    public void CancelOrder(int orderId)
    {
        if (!clientSocket.IsConnected()) return;
        Console.WriteLine($"❌ Cancelling order #{orderId}");
        clientSocket.cancelOrder(orderId);
    }

    /// <summary>
    /// Cancel all open orders
    /// </summary>
    public void CancelAllOrders()
    {
        if (!clientSocket.IsConnected()) return;
        Console.WriteLine("❌ Cancelling all open orders");
        clientSocket.reqGlobalCancel();
    }

    /// <summary>
    /// Request current positions from IBKR
    /// </summary>
    public void RequestPositions()
    {
        if (!clientSocket.IsConnected()) return;
        clientSocket.reqPositions();
    }

    /// <summary>
    /// Get tracked order info
    /// </summary>
    public OrderInfo? GetOrderInfo(int orderId)
    {
        return orderTracking.GetValueOrDefault(orderId);
    }

    /// <summary>
    /// Get position for a symbol
    /// </summary>
    public int GetPosition(string symbol)
    {
        return positionTracking.GetValueOrDefault(symbol, 0);
    }

    // ========================================
    // REAL-TIME BARS (5-sec, requires live market data)
    // ========================================

    private Dictionary<int, string> realtimeBarSymbols = new();
    public event Action<string, DateTime, decimal, decimal, decimal, decimal, long>? OnRealtimeBar;

    /// <summary>
    /// Subscribe to 5-second real-time bars (requires live market data subscription)
    /// </summary>
    public void SubscribeRealtimeBars(string symbol, Contract contract)
    {
        if (!clientSocket.IsConnected())
        {
            Console.WriteLine("Not connected!");
            return;
        }

        int reqId = Math.Abs((symbol + "_rt").GetHashCode()) % 10000 + 20000;
        realtimeBarSymbols[reqId] = symbol;

        Console.WriteLine($"📡 Subscribing to {symbol} 5-sec real-time bars (reqId: {reqId})...");
        clientSocket.reqRealTimeBars(reqId, contract, 5, "TRADES", false, null);
    }

    /// <summary>
    /// Unsubscribe from real-time bars
    /// </summary>
    public void UnsubscribeRealtimeBars(string symbol)
    {
        int reqId = Math.Abs((symbol + "_rt").GetHashCode()) % 10000 + 20000;
        if (clientSocket.IsConnected())
        {
            clientSocket.cancelRealTimeBars(reqId);
            Console.WriteLine($"Unsubscribed from {symbol} real-time bars");
        }
        realtimeBarSymbols.Remove(reqId);
    }

    // ========================================
    // STREAMING BARS (keepUpToDate, works with delayed data)
    // ========================================

    private Dictionary<int, string> streamingBarSymbols = new();

    /// <summary>
    /// Event fired when a streaming bar updates (partial or complete).
    /// Parameters: symbol, time, open, high, low, close, volume
    /// </summary>
    public event Action<string, DateTime, decimal, decimal, decimal, decimal, long>? OnStreamingBarUpdate;

    /// <summary>
    /// Subscribe to streaming historical bars using keepUpToDate=true.
    /// Works with delayed data — no live market data subscription required.
    /// </summary>
    public void SubscribeStreamingBars(string symbol, Contract contract, string barSize = "15 mins")
    {
        if (!clientSocket.IsConnected())
        {
            Console.WriteLine("Not connected!");
            return;
        }

        // Request delayed data if live isn't available
        clientSocket.reqMarketDataType(3); // 3 = delayed

        int reqId = Math.Abs((symbol + "_stream").GetHashCode()) % 10000 + 30000;
        streamingBarSymbols[reqId] = symbol;

        Console.WriteLine($"📡 Subscribing to {symbol} streaming {barSize} bars (reqId: {reqId}, keepUpToDate=true)...");

        // Request historical data with keepUpToDate=true for streaming
        clientSocket.reqHistoricalData(
            reqId, contract, "", "2 D", barSize,
            "TRADES", 0, 1, true, null  // useRTH=0: all hours
        );
    }

    /// <summary>
    /// Unsubscribe from streaming bars
    /// </summary>
    public void UnsubscribeStreamingBars(string symbol)
    {
        int reqId = Math.Abs((symbol + "_stream").GetHashCode()) % 10000 + 30000;
        if (clientSocket.IsConnected())
        {
            clientSocket.cancelHistoricalData(reqId);
            Console.WriteLine($"Unsubscribed from {symbol} streaming bars");
        }
        streamingBarSymbols.Remove(reqId);
    }

    // ========================================
    // MARKET DATA METHODS
    // ========================================

    private Dictionary<int, string> tickerSymbols = new();
    private Dictionary<string, decimal> lastPrices = new();

    /// <summary>
    /// Subscribe to real-time market data
    /// </summary>
    public void SubscribeMarketData(string symbol, Contract contract)
    {
        if (!clientSocket.IsConnected())
        {
            Console.WriteLine("Not connected!");
            return;
        }

        int tickerId = Math.Abs(symbol.GetHashCode()) % 10000;
        tickerSymbols[tickerId] = symbol;

        Console.WriteLine($"📡 Subscribing to {symbol} market data (tickerId: {tickerId})...");

        clientSocket.reqMktData(tickerId, contract, "", false, false, null);
    }

    /// <summary>
    /// Unsubscribe from market data
    /// </summary>
    public void UnsubscribeMarketData(string symbol)
    {
        int tickerId = Math.Abs(symbol.GetHashCode()) % 10000;

        if (clientSocket.IsConnected())
        {
            clientSocket.cancelMktData(tickerId);
            Console.WriteLine($"Unsubscribed from {symbol}");
        }

        tickerSymbols.Remove(tickerId);
    }

    /// <summary>
    /// Get last price for symbol
    /// </summary>
    public decimal? GetLastPrice(string symbol)
    {
        return lastPrices.ContainsKey(symbol) ? lastPrices[symbol] : null;
    }

    // ========================================
    // HISTORICAL DATA
    // ========================================

    private Dictionary<int, List<HistoricalBar>> historicalBars = new();
    private Dictionary<int, bool> historicalDataComplete = new();
    private Dictionary<string, int> _histReqIdByKey = new(); // Maps symbol+tag → latest reqId
    private int _histReqIdCounter = 0; // Monotonically increasing — avoids Error 322 (duplicate ticker ID) on reconnect

    /// <summary>
    /// Request historical bars (auto-calculates duration)
    /// </summary>
    public void RequestHistoricalBars(
        string symbol,
        Contract contract,
        int numBars,
        string barSize = "1 min")
    {
        string duration = CalculateDuration(numBars, barSize);
        RequestHistoricalBarsDirect(symbol, contract, duration, barSize);
    }

    /// <summary>
    /// Request historical bars with explicit duration (e.g. "3 Y", "6 M")
    /// Use tag to differentiate multiple requests for the same symbol
    /// </summary>
    public void RequestHistoricalBarsDirect(
        string symbol,
        Contract contract,
        string duration,
        string barSize,
        string tag = "")
    {
        RequestHistoricalBarsDirect15m(symbol, contract, duration, barSize, "", tag);
    }

    /// <summary>
    /// Request historical bars with explicit duration and end date
    /// endDateTime: "yyyyMMdd HH:mm:ss" or "" for now
    /// </summary>
    public void RequestHistoricalBarsDirect15m(
        string symbol,
        Contract contract,
        string duration,
        string barSize,
        string endDateTime,
        string tag = "")
    {
        // Use a unique incrementing reqId so reconnects never cause Error 322 (duplicate ticker ID)
        int reqId = 5000 + Interlocked.Increment(ref _histReqIdCounter);

        // Store the latest reqId for this key so GetHistoricalBars can look it up
        string key = symbol + tag;
        _histReqIdByKey[key] = reqId;

        historicalBars[reqId] = new List<HistoricalBar>();
        historicalDataComplete[reqId] = false;

        Console.WriteLine($"📊 Requesting {duration} of {barSize} bars for {symbol} (reqId: {reqId}, end: {(string.IsNullOrEmpty(endDateTime) ? "now" : endDateTime)})...");

        clientSocket.reqHistoricalData(
            reqId, contract, endDateTime, duration, barSize,
            "TRADES", 0, 1, false, null  // useRTH=0: include all electronic trading hours (24/7)
        );
    }

    private string CalculateDuration(int numBars, string barSize)
    {
        if (barSize.Contains("min"))
        {
            int minutes = int.Parse(barSize.Split(' ')[0]);
            int totalMinutes = numBars * minutes;

            // IBKR accepts: S (seconds), D (days), W (weeks), M (months), Y (years)
            // For intraday data, use seconds
            if (totalMinutes < 1440) // Less than 1 day
                return $"{totalMinutes * 60} S"; // Convert to seconds
            else
                return $"{(totalMinutes / 1440) + 1} D"; // Convert to days
        }
        else if (barSize.Contains("hour"))
        {
            int hours = int.Parse(barSize.Split(' ')[0]);
            int totalHours = numBars * hours;

            if (totalHours < 24)
                return $"{totalHours * 3600} S"; // Seconds
            else
                return $"{(totalHours / 24) + 1} D"; // Days
        }

        return "1 D"; // Default
    }

    public List<HistoricalBar>? GetHistoricalBars(string symbol, int timeoutSeconds = 10, string tag = "")
    {
        string key = symbol + tag;
        if (!_histReqIdByKey.TryGetValue(key, out int reqId))
        {
            Console.WriteLine($"⚠️  No pending request found for key '{key}'");
            return null;
        }

        var startTime = DateTime.Now;
        while (!historicalDataComplete.GetValueOrDefault(reqId) &&
               (DateTime.Now - startTime).TotalSeconds < timeoutSeconds)
        {
            Thread.Sleep(100);
        }

        if (!historicalDataComplete.GetValueOrDefault(reqId))
        {
            Console.WriteLine($"⚠️  Timeout waiting for {symbol} historical data");
            return null;
        }

        return historicalBars.GetValueOrDefault(reqId);
    }
}

/// <summary>
/// Historical bar from IBKR
/// </summary>
public class HistoricalBar
{
    public DateTime Time { get; set; }
    public decimal Open { get; set; }
    public decimal High { get; set; }
    public decimal Low { get; set; }
    public decimal Close { get; set; }
    public long Volume { get; set; }
}

/// <summary>
/// Tracks order state
/// </summary>
public class OrderInfo
{
    public int OrderId { get; set; }
    public string Type { get; set; } = string.Empty; // ENTRY, STOP, TARGET, FLATTEN
    public string Status { get; set; } = string.Empty;
    public decimal FilledQty { get; set; }
    public decimal AvgFillPrice { get; set; }
    public int ParentId { get; set; }
}
