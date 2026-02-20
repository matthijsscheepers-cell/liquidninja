# LIQUIDNINJA — Backtest Results (Preserved)

**Strategy:** TTM Squeeze Pullback + Trend Ride (dual-mode)
**Period:** Feb 2025 – Feb 2026 (13 months, ~1 year)
**Data:** IBKR CONTFUT historical bars, `useRTH=0` (24/7)
**Account type:** FundedNext $25,000 Challenge, fixed 1 contract per trade

---

## Final Strategy Parameters

| Parameter | MGC | MES |
|---|---|---|
| EMA pullback period | 21 | 21 |
| ATR period | 20 | 20 |
| Entry tolerance | 1.2 ATR | 1.0 ATR |
| Stop size | 1.5 ATR | 1.5 ATR |
| Target size | 2.0 ATR | 2.0 ATR |
| Breakeven trigger | 1.0 ATR profit | 1.0 ATR profit |
| Trend ride stop | 1.0 ATR | 1.0 ATR |
| Trend activation | 1.5 ATR from EMA (1H) | 1.5 ATR from EMA (1H) |
| Post-trade cooldown | 3 bars (45 min) | 3 bars (45 min) |

**Risk settings:**
- Daily loss limit: $1,250 (5%)
- Max drawdown: $2,000 (8%)
- Consistency rule: no single day >40% of total profit
- Market hours blackout: 9:20–9:50 ET, 15:45–16:05 ET
- Consecutive stop cooldown: 2 stops → 2h pause
- Max idle days: 7
- TradeRiskMultiplier: **0.25** (Challenge mode) ← was 0.15 (bug fixed)

---

## Gross Backtest Results (No Costs)

| Metric | MGC | MES |
|---|---|---|
| 15m bars | 7,475 | 7,469 |
| Total trades | 629 (1.74/day) | 747 (2.07/day) |
| Win rate | **61.2%** | **52.9%** |
| Gross P&L | **$60,051.58** | **$38,383.86** |
| Profit Factor | 1.55 | 1.29 |
| Max Drawdown | $859.39 | $528.61 |
| Max Idle Days | 4 | 4 |
| Max Consec. Losses | 3 | 3 |
| Largest Daily Loss | $468.76 | $528.61 |
| Profitable Months | 13/13 | 13/13 |
| Pullback / TrendRide | 451 / 178 | 583 / 164 |

---

## Realistic Net P&L (With Execution Costs)

**Cost model:**
- MGC: $10/fill slippage (1 tick) × 2 + $1.70 commission = **$21.70/trade**
- MES: $1.25/fill slippage (0.25pt) × 2 + $1.70 commission = **$4.20/trade**

| Scenario | MGC Net P&L | MGC PF | MES Net P&L | MES PF |
|---|---|---|---|---|
| No slippage | $58,982 | 3.75 | $37,114 | 3.83 |
| **Normal (1 tick MGC / 0.25pt MES)** | **$46,402** | **2.68** | **$35,246** | **3.52** |
| Worst (2x slippage) | $33,822 | 1.96 | $33,379 | 3.26 |
| Extreme (3x slippage) | $21,242 | 1.52 | $31,511 | 3.04 |
| Break-even slippage | 4.7× normal | — | 19.9× normal | — |

---

## Monte Carlo (1,000 shuffles of actual trades)

| Metric | MGC | MES |
|---|---|---|
| Avg simulated DD | $946.71 | $646.25 |
| Median simulated DD | $884.41 | $614.66 |
| **95th percentile DD** | **$1,217.71** | **$897.99** |
| 99th percentile DD | $1,400.29 | $1,150.08 |
| Worst case DD | $1,608.14 | $1,457.80 |
| $2,000 limit safe? | **YES** | **YES** |

---

## Fill Quality Simulation (500 iterations, 80% fill rate)

| Metric | MGC | MES |
|---|---|---|
| Avg fill rate | 80.1% | 80.1% |
| Avg trade count | 503 vs 629 | 598 vs 747 |
| **Avg net P&L** | **$36,607** | **$28,184** |
| Worst case P&L | $30,396 | $23,925 |
| Best case P&L | $42,465 | $32,786 |

---

## Parameter Sensitivity (±20% of defaults)

**MGC — max P&L change: −16.4% (entry tolerance at 0.8) → STABLE 5/5**
**MES — max P&L change: +23.0% (entry tolerance at 1.4) → STABLE 5/5**

Notable: Widening MES entry_tolerance to 1.4 ATR would add +$8,835 — worth retesting.

---

## Reality Check Verdicts

| Check | MGC | MES |
|---|---|---|
| Edge robust (realistic costs) | YES | YES |
| MC safe (95th% < $2,000) | YES | YES |
| Params stable (<30% swing) | YES | YES |
| Fill quality (80% fill) | YES | YES |
| Challenge rules safe | YES | YES |
| **VERDICT** | **HIGH 5/5** | **HIGH 5/5** |

---

## FundedNext Challenge Simulation

**Rules:** +$1,250 = pass, −$1,000 DD = breach, $1,250 daily limit
**Fee:** $50/attempt, payout: $1,000/pass (80%)

| Metric | MGC | MES |
|---|---|---|
| Challenges attempted | 32 | 27 |
| **Passed** | **31** | **26** |
| Breached | 0 | 0 |
| Incomplete (data ended) | 1 | 1 |
| **Pass rate** | **100% (31/31)** | **100% (26/26)** |
| Avg days to pass | 11 days | 13 days |
| Avg trades to pass | 20 trades | 28 trades |
| Fastest pass | 0 days (1 trade!) | 1 day (4 trades) |
| Slowest pass | 80 days | 27 days |
| Total fees paid | $1,550 | $1,300 |
| Total payouts | $31,000 | $26,000 |
| **Net profit challenges** | **$29,450** | **$24,700** |

---

## Monthly P&L Breakdown

### MGC

| Month | Trades | P&L | W/L |
|---|---|---|---|
| 2025-02 | 17 | +$1,215 | 13W/3L |
| 2025-03 | 55 | +$3,361 | 33W/6L |
| 2025-04 | 57 | +$5,245 | 35W/11L |
| 2025-05 | 46 | +$3,875 | 28W/7L |
| 2025-06 | 41 | +$1,504 | 26W/9L |
| 2025-07 | 50 | +$1,417 | 27W/11L |
| 2025-08 | 56 | +$2,568 | 35W/6L |
| 2025-09 | 52 | +$3,158 | 29W/11L |
| 2025-10 | 68 | +$7,479 | 46W/8L |
| 2025-11 | 48 | +$5,507 | 30W/5L |
| 2025-12 | 59 | +$6,101 | 35W/6L |
| 2026-01 | 56 | +$11,735 | 32W/13L |
| 2026-02 | 24 | +$6,885 | 16W/2L |

### MES

| Month | Trades | P&L | W/L |
|---|---|---|---|
| 2025-02 | 25 | +$1,080 | 11W/2L |
| 2025-03 | 63 | +$4,931 | 34W/9L |
| 2025-04 | 64 | +$7,004 | 32W/10L |
| 2025-05 | 59 | +$1,702 | 27W/13L |
| 2025-06 | 66 | +$2,688 | 33W/6L |
| 2025-07 | 63 | +$2,334 | 38W/7L |
| 2025-08 | 63 | +$1,860 | 34W/12L |
| 2025-09 | 64 | +$1,844 | 32W/11L |
| 2025-10 | 72 | +$4,651 | 47W/9L |
| 2025-11 | 56 | +$4,821 | 36W/6L |
| 2025-12 | 62 | +$2,311 | 29W/8L |
| 2026-01 | 61 | +$1,774 | 29W/13L |
| 2026-02 | 29 | +$1,386 | 13W/5L |

---

## Bugs Fixed During Live Trading (Feb 2026)

1. **TradeRiskMultiplier 0.15 → 0.25**: Challenge mode was blocking MGC trades (budget $150 < $230 needed). Feb 17 cost ~$1,070 in missed profit (4 SHORT signals on gold, all missed).
2. **bars1h[^1] → bars1h[^2]**: Strategy was using in-progress hourly bar instead of completed one. Caused wrong MES SHORT on Feb 19. Fixed.
3. **useRTH=1 → useRTH=0**: Was showing only pit session bars (~150). Now shows 24/7 electronic bars (~491 warmup bars). Fixed Feb 20.

---

## Potential Improvement Ideas (Not Yet Tested)

- MES entry_tolerance 1.0 → 1.4 ATR: sensitivity test showed +23% P&L (+$8,835) — worth a full backtest
- London/Asian session filtering: now that useRTH=0, analyze whether overnight bars (18:00–09:20 ET) have different signal quality
- Worst month (MGC July 2025: +$1,417 on 50 trades) — investigate if a low-volatility filter would help skip choppy markets
