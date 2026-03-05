#!/bin/bash
# LIQUIDNINJA — Stop both bots gracefully
# Called by launchd on Friday 22:00 CET (bots also do EOD flatten at 21:50)

PROJECT="/Users/matthijs/Documents/Project LIQUIDNINJA"
LOG_DIR="$PROJECT/FuturesTradingBot.App/bin/Debug/net10.0/logs"
SCHED_LOG="$LOG_DIR/scheduler.log"

mkdir -p "$LOG_DIR"

echo "[$(date '+%Y-%m-%d %H:%M:%S')] ========== STOP_BOTS CALLED ==========" >> "$SCHED_LOG"

# Send SIGTERM first — bots will catch this and do graceful shutdown
# (EOD flatten should already have closed positions at 21:50)
pkill -TERM -f "FuturesTradingBot.App.*MGC" 2>/dev/null && echo "[$(date '+%Y-%m-%d %H:%M:%S')] SIGTERM → MGC" >> "$SCHED_LOG"
pkill -TERM -f "FuturesTradingBot.App.*MES" 2>/dev/null && echo "[$(date '+%Y-%m-%d %H:%M:%S')] SIGTERM → MES" >> "$SCHED_LOG"

# Wait up to 15s for graceful shutdown
sleep 15

# Force-kill anything still running
pkill -9 -f "FuturesTradingBot.App.*MGC" 2>/dev/null && echo "[$(date '+%Y-%m-%d %H:%M:%S')] SIGKILL → MGC (was still alive)" >> "$SCHED_LOG"
pkill -9 -f "FuturesTradingBot.App.*MES" 2>/dev/null && echo "[$(date '+%Y-%m-%d %H:%M:%S')] SIGKILL → MES (was still alive)" >> "$SCHED_LOG"

# Also kill any stray dotnet build processes
pkill -9 -f "dotnet.*FuturesTradingBot" 2>/dev/null

# Stop caffeinate
if [ -f /tmp/liquidninja_caffeinate.pid ]; then
    kill $(cat /tmp/liquidninja_caffeinate.pid) 2>/dev/null
    rm /tmp/liquidninja_caffeinate.pid
fi
pkill caffeinate 2>/dev/null

# Clean up pid files
rm -f /tmp/liquidninja_mgc.pid /tmp/liquidninja_mes.pid

echo "[$(date '+%Y-%m-%d %H:%M:%S')] Both bots stopped. Weekend." >> "$SCHED_LOG"
