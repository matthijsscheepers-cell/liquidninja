#!/bin/bash
# LIQUIDNINJA — Start both bots (MGC + MES)
# Called by launchd on Monday 00:20 CET

PROJECT="/Users/matthijs/Documents/Project LIQUIDNINJA"
APP_PROJECT="$PROJECT/FuturesTradingBot.App"
LOG_DIR="$APP_PROJECT/bin/Debug/net10.0/logs"
SCHED_LOG="$LOG_DIR/scheduler.log"

mkdir -p "$LOG_DIR"

echo "[$(date '+%Y-%m-%d %H:%M:%S')] ========== START_BOTS CALLED ==========" >> "$SCHED_LOG"

# Kill any stale bot processes
pkill -f "FuturesTradingBot.App.*MGC" 2>/dev/null && echo "[$(date '+%Y-%m-%d %H:%M:%S')] Killed stale MGC process" >> "$SCHED_LOG"
pkill -f "FuturesTradingBot.App.*MES" 2>/dev/null && echo "[$(date '+%Y-%m-%d %H:%M:%S')] Killed stale MES process" >> "$SCHED_LOG"
sleep 3

# Keep Mac awake while bots are running
pkill caffeinate 2>/dev/null
caffeinate -s &
echo $! > /tmp/liquidninja_caffeinate.pid
echo "[$(date '+%Y-%m-%d %H:%M:%S')] caffeinate started (pid $(cat /tmp/liquidninja_caffeinate.pid))" >> "$SCHED_LOG"

# Build once before starting both bots
echo "[$(date '+%Y-%m-%d %H:%M:%S')] Building project..." >> "$SCHED_LOG"
dotnet build "$APP_PROJECT" -c Debug --nologo -v quiet >> "$SCHED_LOG" 2>&1
if [ $? -ne 0 ]; then
    echo "[$(date '+%Y-%m-%d %H:%M:%S')] BUILD FAILED — bots not started" >> "$SCHED_LOG"
    exit 1
fi
echo "[$(date '+%Y-%m-%d %H:%M:%S')] Build OK" >> "$SCHED_LOG"

# Start MGC bot
MGC_LOG="$LOG_DIR/mgc_console_$(date '+%Y-%m-%d').log"
nohup dotnet run --project "$APP_PROJECT" --no-build -- --live --asset MGC \
    >> "$MGC_LOG" 2>&1 &
MGC_PID=$!
echo $MGC_PID > /tmp/liquidninja_mgc.pid
echo "[$(date '+%Y-%m-%d %H:%M:%S')] MGC bot started (pid $MGC_PID) → $MGC_LOG" >> "$SCHED_LOG"

# Short delay so both bots don't collide on initial IBKR connection
sleep 5

# Start MES bot
MES_LOG="$LOG_DIR/mes_console_$(date '+%Y-%m-%d').log"
nohup dotnet run --project "$APP_PROJECT" --no-build -- --live --asset MES \
    >> "$MES_LOG" 2>&1 &
MES_PID=$!
echo $MES_PID > /tmp/liquidninja_mes.pid
echo "[$(date '+%Y-%m-%d %H:%M:%S')] MES bot started (pid $MES_PID) → $MES_LOG" >> "$SCHED_LOG"

echo "[$(date '+%Y-%m-%d %H:%M:%S')] Both bots running. Done." >> "$SCHED_LOG"
