namespace FuturesTradingBot.Core.Indicators;

using FuturesTradingBot.Core.Models;

/// <summary>
/// Directional Movement Index (Wilder, period 14)
///
/// +DI = 100 × Wilder_smooth(+DM) / Wilder_smooth(TR)
/// -DI = 100 × Wilder_smooth(-DM) / Wilder_smooth(TR)
///
/// Wilder smoothing: new = old - old/period + raw
///   (equivalent to EMA with alpha = 1/period, seeded with SMA)
///
/// +DI > -DI → bullish directional pressure dominates
/// -DI > +DI → bearish directional pressure dominates
/// </summary>
public class DMI
{
    /// <summary>
    /// Calculate +DI and -DI for a list of bars.
    /// Returns two lists of length bars.Count, with nulls during the warmup period.
    /// </summary>
    public static (List<decimal?> plusDI, List<decimal?> minusDI) Calculate(List<Bar> bars, int period = 14)
    {
        int n = bars.Count;
        var plusDI  = new List<decimal?>(new decimal?[n]);
        var minusDI = new List<decimal?>(new decimal?[n]);

        if (n < period + 1)
            return (plusDI, minusDI);

        // ── Raw +DM, -DM, TR per bar ──────────────────────────────
        var rawPlusDM  = new decimal[n];
        var rawMinusDM = new decimal[n];
        var rawTR      = new decimal[n];

        // First bar: no previous bar — TR = High - Low, DMs = 0
        rawTR[0] = bars[0].High - bars[0].Low;

        for (int i = 1; i < n; i++)
        {
            decimal upMove   = bars[i].High - bars[i - 1].High;
            decimal downMove = bars[i - 1].Low - bars[i].Low;

            rawPlusDM[i]  = (upMove > downMove && upMove > 0)   ? upMove   : 0m;
            rawMinusDM[i] = (downMove > upMove && downMove > 0) ? downMove : 0m;

            decimal highLow       = bars[i].High - bars[i].Low;
            decimal highPrevClose = Math.Abs(bars[i].High - bars[i - 1].Close);
            decimal lowPrevClose  = Math.Abs(bars[i].Low  - bars[i - 1].Close);
            rawTR[i] = Math.Max(highLow, Math.Max(highPrevClose, lowPrevClose));
        }

        // ── Seed: SMA of first `period` raw values (bars 1..period) ──
        decimal smPlusDM = 0m, smMinusDM = 0m, smTR = 0m;
        for (int i = 1; i <= period; i++)
        {
            smPlusDM  += rawPlusDM[i];
            smMinusDM += rawMinusDM[i];
            smTR      += rawTR[i];
        }

        if (smTR > 0)
        {
            plusDI[period]  = 100m * smPlusDM  / smTR;
            minusDI[period] = 100m * smMinusDM / smTR;
        }

        // ── Wilder smooth: new = old - old/period + raw ───────────
        for (int i = period + 1; i < n; i++)
        {
            smPlusDM  = smPlusDM  - smPlusDM  / period + rawPlusDM[i];
            smMinusDM = smMinusDM - smMinusDM / period + rawMinusDM[i];
            smTR      = smTR      - smTR      / period + rawTR[i];

            if (smTR > 0)
            {
                plusDI[i]  = 100m * smPlusDM  / smTR;
                minusDI[i] = 100m * smMinusDM / smTR;
            }
        }

        return (plusDI, minusDI);
    }

    /// <summary>
    /// Calculate DMI and add plus_di_{period} / minus_di_{period} to Bar metadata.
    /// Skips bars that already have the indicator (safe for incremental live updates).
    /// </summary>
    public static void AddToBarList(List<Bar> bars, int period = 14)
    {
        string plusKey  = $"plus_di_{period}";
        string minusKey = $"minus_di_{period}";

        var (plusDI, minusDI) = Calculate(bars, period);

        for (int i = 0; i < bars.Count; i++)
        {
            if (!bars[i].Metadata.ContainsKey(plusKey))
                bars[i].Metadata[plusKey] = plusDI[i];

            if (!bars[i].Metadata.ContainsKey(minusKey))
                bars[i].Metadata[minusKey] = minusDI[i];
        }
    }
}
