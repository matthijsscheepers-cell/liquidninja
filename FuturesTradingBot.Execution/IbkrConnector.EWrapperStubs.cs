namespace FuturesTradingBot.Execution;

using IBApi;

/// <summary>
/// EWrapper stub implementations (partial class)
/// These are callback methods that IBKR calls
/// Signatures match IBApi 1.0.0-preview-975
/// </summary>
public partial class IbkrConnector
{
    // Most of these we don't need yet - just stub them with empty implementations

    public void accountDownloadEnd(string account) { }
    public void bondContractDetails(int reqId, ContractDetails contract) { }
    public void commissionReport(CommissionReport commissionReport) { }
    public void contractDetails(int reqId, ContractDetails contractDetails)
    {
        lock (_contractDetailsResults)
        {
            if (_contractDetailsResults.TryGetValue(reqId, out var list))
                list.Add(contractDetails);
        }
    }

    public void contractDetailsEnd(int reqId)
    {
        lock (_contractDetailsEvents)
        {
            if (_contractDetailsEvents.TryGetValue(reqId, out var evt))
                evt.Set();
        }
    }
    public void currentTime(long time) { }
    public void deltaNeutralValidation(int reqId, DeltaNeutralContract deltaNeutralContract) { }
    public void displayGroupList(int reqId, string groups) { }
    public void displayGroupUpdated(int reqId, string contractInfo) { }
    public void execDetails(int reqId, Contract contract, Execution execution)
    {
        Console.WriteLine($"📋 Exec: {execution.Side} {execution.Shares}x {contract.Symbol} @ ${execution.Price:F2} (order #{execution.OrderId})");
    }
    public void execDetailsEnd(int reqId) { }
    public void fundamentalData(int reqId, string data) { }
    public void histogramData(int reqId, HistogramEntry[] data) { }
    public void historicalData(int reqId, Bar bar)
    {
        // Parse time (shared logic)
        DateTime time = ParseBarTime(bar.Time);

        // If this is a streaming subscription, forward to the streaming event too
        if (streamingBarSymbols.ContainsKey(reqId))
        {
            var symbol = streamingBarSymbols[reqId];
            OnStreamingBarUpdate?.Invoke(symbol, time, (decimal)bar.Open, (decimal)bar.High, (decimal)bar.Low, (decimal)bar.Close, bar.Volume);
            return; // Don't store in historicalBars — aggregator handles it
        }

        if (!historicalBars.ContainsKey(reqId))
            historicalBars[reqId] = new List<HistoricalBar>();

        historicalBars[reqId].Add(new HistoricalBar
        {
            Time = time,
            Open = (decimal)bar.Open,
            High = (decimal)bar.High,
            Low = (decimal)bar.Low,
            Close = (decimal)bar.Close,
            Volume = bar.Volume
        });
    }
    public void historicalDataEnd(int reqId, string start, string end)
    {
        // For streaming subscriptions, don't mark complete — they keep going
        if (streamingBarSymbols.ContainsKey(reqId))
        {
            var symbol = streamingBarSymbols[reqId];
            Console.WriteLine($"✅ Streaming {symbol}: initial batch loaded (from {start} to {end}), now streaming updates...");
            return;
        }

        historicalDataComplete[reqId] = true;
        int barCount = historicalBars.GetValueOrDefault(reqId)?.Count ?? 0;
        Console.WriteLine($"✅ Received {barCount} historical bars (from {start} to {end})");
    }

    private static DateTime ParseBarTime(string rawTime)
    {
        var timeStr = rawTime.Trim();
        if (timeStr.Length == 8)
            return DateTime.ParseExact(timeStr, "yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture);
        else if (timeStr.Contains("  "))
            return DateTime.ParseExact(timeStr, "yyyyMMdd  HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
        else if (timeStr.Contains(" "))
            return DateTime.ParseExact(timeStr, "yyyyMMdd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
        else
            return DateTimeOffset.FromUnixTimeSeconds(long.Parse(timeStr)).DateTime;
    }
    public void historicalDataUpdate(int reqId, Bar bar)
    {
        if (!streamingBarSymbols.ContainsKey(reqId)) return;

        var symbol = streamingBarSymbols[reqId];
        var time = ParseBarTime(bar.Time);

        OnStreamingBarUpdate?.Invoke(symbol, time, (decimal)bar.Open, (decimal)bar.High, (decimal)bar.Low, (decimal)bar.Close, bar.Volume);
    }
    public void historicalNews(int requestId, string time, string providerCode, string articleId, string headline) { }
    public void historicalNewsEnd(int requestId, bool hasMore) { }
    public void historicalTicks(int reqId, HistoricalTick[] ticks, bool done) { }
    public void historicalTicksBidAsk(int reqId, HistoricalTickBidAsk[] ticks, bool done) { }
    public void historicalTicksLast(int reqId, HistoricalTickLast[] ticks, bool done) { }
    public void marketDataType(int reqId, int marketDataType) { }
    public void marketRule(int marketRuleId, PriceIncrement[] priceIncrements) { }
    public void mktDepthExchanges(DepthMktDataDescription[] depthMktDataDescriptions) { }
    public void newsArticle(int requestId, int articleType, string articleText) { }
    public void newsProviders(NewsProvider[] newsProviders) { }
    public void pnl(int reqId, double dailyPnL, double unrealizedPnL, double realizedPnL) { }
    public void pnlSingle(int reqId, int pos, double dailyPnL, double unrealizedPnL, double realizedPnL, double value) { }
    public void position(string account, Contract contract, double pos, double avgCost)
    {
        var symbol = contract.Symbol;
        var qty = (int)pos;
        positionTracking[symbol] = qty;
        OnPositionUpdate?.Invoke(symbol, qty);

        if (qty != 0)
            Console.WriteLine($"📊 Position: {symbol} = {qty} contracts @ ${avgCost:F2}");
    }
    public void positionEnd() { OnPositionEnd?.Invoke(); }
    public void positionMulti(int reqId, string account, string modelCode, Contract contract, double pos, double avgCost) { }
    public void positionMultiEnd(int reqId) { }
    public void realtimeBar(int reqId, long date, double open, double high, double low, double close, long volume, double WAP, int count)
    {
        if (!realtimeBarSymbols.ContainsKey(reqId)) return;

        var symbol = realtimeBarSymbols[reqId];
        var time = DateTimeOffset.FromUnixTimeSeconds(date).DateTime;

        OnRealtimeBar?.Invoke(symbol, time, (decimal)open, (decimal)high, (decimal)low, (decimal)close, volume);
    }
    public void receiveFA(int faDataType, string faXmlData) { }
    public void replaceFAEnd(int reqId, string text) { }
    public void rerouteMktDataReq(int reqId, int conId, string exchange) { }
    public void rerouteMktDepthReq(int reqId, int conId, string exchange) { }
    public void scannerData(int reqId, int rank, ContractDetails contractDetails, string distance, string benchmark, string projection, string legsStr) { }
    public void scannerDataEnd(int reqId) { }
    public void scannerParameters(string xml) { }
    public void securityDefinitionOptionParameter(int reqId, string exchange, int underlyingConId, string tradingClass, string multiplier, HashSet<string> expirations, HashSet<double> strikes) { }
    public void securityDefinitionOptionParameterEnd(int reqId) { }
    public void smartComponents(int reqId, Dictionary<int, KeyValuePair<string, char>> theMap) { }
    public void softDollarTiers(int reqId, SoftDollarTier[] tiers) { }
    public void symbolSamples(int reqId, ContractDescription[] contractDescriptions) { }
    public void tickByTickAllLast(int reqId, int tickType, long time, double price, int size, TickAttribLast tickAttriblast, string exchange, string specialConditions) { }
    public void tickByTickBidAsk(int reqId, long time, double bidPrice, double askPrice, int bidSize, int askSize, TickAttribBidAsk tickAttribBidAsk) { }
    public void tickByTickMidPoint(int reqId, long time, double midPoint) { }
    public void tickNews(int tickerId, long timeStamp, string providerCode, string articleId, string headline, string extraData) { }
    public void tickOptionComputation(int tickerId, int field, double impliedVolatility, double delta, double optPrice, double pvDividend, double gamma, double vega, double theta, double undPrice) { }
    public void tickReqParams(int tickerId, double minTick, string bboExchange, int snapshotPermissions) { }
    public void tickSnapshotEnd(int tickerId) { }
    public void updateMktDepth(int tickerId, int position, int operation, int side, double price, int size) { }
    public void updateMktDepthL2(int tickerId, int position, string marketMaker, int operation, int side, double price, int size, bool isSmartDepth) { }
    public void updateNewsBulletin(int msgId, int msgType, string message, string origExchange) { }
    public void verifyAndAuthCompleted(bool isSuccessful, string errorText) { }
    public void verifyAndAuthMessageAPI(string apiData, string xyzChallenge) { }
    public void verifyCompleted(bool isSuccessful, string errorText) { }
    public void verifyMessageAPI(string apiData) { }
    public void wshEventData(int reqId, string dataJson) { }
    public void wshMetaData(int reqId, string dataJson) { }
    public void headTimestamp(int reqId, string headTimestamp) { }
    public void tickPrice(int tickerId, int field, double price, TickAttrib attribs)
    {
        if (!tickerSymbols.ContainsKey(tickerId))
            return;

        var symbol = tickerSymbols[tickerId];

        if (field == 4) // LAST price
        {
            lastPrices[symbol] = (decimal)price;
            Console.WriteLine($"💰 {symbol}: ${price:F2}");
        }
        else if (field == 1) // BID
        {
            Console.WriteLine($"   {symbol} BID: ${price:F2}");
        }
        else if (field == 2) // ASK
        {
            Console.WriteLine($"   {symbol} ASK: ${price:F2}");
        }
    }
    public void tickSize(int tickerId, int field, int size)
    {
        if (!tickerSymbols.ContainsKey(tickerId))
            return;

        var symbol = tickerSymbols[tickerId];

        if (field == 8) // VOLUME
        {
            Console.WriteLine($"   {symbol} Volume: {size}");
        }
    }
    public void tickString(int tickerId, int tickType, string value) { }
    public void tickGeneric(int tickerId, int field, double value) { }
    public void tickEFP(int tickerId, int tickType, double basisPoints, string formattedBasisPoints, double impliedFuture, int holdDays, string futureLastTradeDate, double dividendImpact, double dividendsToLastTradeDate) { }
    public void orderStatus(int orderId, string status, double filled, double remaining, double avgFillPrice, int permId, int parentId, double lastFillPrice, int clientId, string whyHeld, double mktCapPrice)
    {
        if (orderTracking.ContainsKey(orderId))
        {
            var info = orderTracking[orderId];
            var prevStatus = info.Status;
            info.Status = status;
            info.FilledQty = (decimal)filled;
            info.AvgFillPrice = (decimal)avgFillPrice;

            if (prevStatus != status)
            {
                Console.WriteLine($"📋 Order #{orderId} ({info.Type}): {status} | Filled: {filled} @ ${avgFillPrice:F2}");
            }
        }

        OnOrderStatusChanged?.Invoke(orderId, status, (decimal)filled, (decimal)avgFillPrice);
    }
    public void openOrder(int orderId, Contract contract, Order order, OrderState orderState)
    {
        if (orderTracking.ContainsKey(orderId))
        {
            orderTracking[orderId].Status = orderState.Status;
        }
    }
    public void openOrderEnd() { }
    public void orderBound(long orderId, int apiClientId, int apiOrderId) { }
    public void updateAccountValue(string key, string value, string currency, string accountName) { }
    public void updatePortfolio(Contract contract, double position, double marketPrice, double marketValue, double averageCost, double unrealizedPNL, double realizedPNL, string accountName) { }
    public void updateAccountTime(string timestamp) { }
    public void accountUpdateMulti(int reqId, string account, string modelCode, string key, string value, string currency) { }
    public void accountUpdateMultiEnd(int reqId) { }
    public void familyCodes(FamilyCode[] familyCodes) { }
    public void completedOrder(Contract contract, Order order, OrderState orderState) { }
    public void completedOrdersEnd() { }
    public void userInfo(int reqId, string whiteBrandingId) { }
}
