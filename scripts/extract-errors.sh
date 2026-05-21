#!/bin/bash
# extract-errors.sh
# Parse maven compile/package log and produce categorized error report
# Usage: ./scripts/extract-errors.sh <maven-log>

set -euo pipefail

LOG_FILE="${1:?Usage: extract-errors.sh <maven-log>}"
OUT_DIR="$(dirname "$LOG_FILE")"

if [ ! -f "$LOG_FILE" ]; then
    echo "Log file not found: $LOG_FILE"
    exit 1
fi

echo "========================================"
echo " Error Analysis: $(basename "$LOG_FILE")"
echo "========================================"

# Total error count
TOTAL=$(grep -c "^\[ERROR\]" "$LOG_FILE" 2>/dev/null || echo "0")
JAVA_TOTAL=$(grep "^\[ERROR\]" "$LOG_FILE" 2>/dev/null | grep -c "\.java:" 2>/dev/null || echo "0")
echo "Total [ERROR] lines: $TOTAL"
echo "Java compiler errors: $JAVA_TOTAL"
echo ""

# Extract errors by file
echo "--- Errors by file ---"
grep "^\[ERROR\]" "$LOG_FILE" \
    | grep "\.java:" \
    | sed 's/\[ERROR\] //' \
    | sed 's#.*/src/main/java/##' \
    > "$OUT_DIR/errors-by-file.log" 2>/dev/null || true
cat "$OUT_DIR/errors-by-file.log" 2>/dev/null | head -100
echo ""

# Extract unique error messages (strip file/line info)
echo "--- Unique error patterns (top 40) ---"
grep "^\[ERROR\]" "$LOG_FILE" \
    | grep "\.java:" \
    | sed 's/\[ERROR\] .*\.java:\[[0-9]*,[0-9]*\] //' \
    | sort | uniq -c | sort -rn \
    > "$OUT_DIR/error-patterns.log" 2>/dev/null || true
head -40 "$OUT_DIR/error-patterns.log" 2>/dev/null || echo "(none)"
echo ""

# Extract errors by Java file (count per file)
echo "--- Error count by file (top 20) ---"
grep "^\[ERROR\]" "$LOG_FILE" \
    | grep "\.java:" \
    | sed 's/\[ERROR\] //' \
    | sed 's/\[[0-9]*,[0-9]*\].*//' \
    | sort | uniq -c | sort -rn \
    > "$OUT_DIR/error-count-by-file.log" 2>/dev/null || true
head -20 "$OUT_DIR/error-count-by-file.log" 2>/dev/null || echo "(none)"
echo ""

echo "Detailed logs in: $OUT_DIR/"
