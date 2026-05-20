#!/bin/bash
# extract-errors.sh
# Parse maven compile log and produce categorized error report
# Usage: ./scripts/extract-errors.sh <maven-compile-log>

set -euo pipefail

LOG_FILE="${1:?Usage: extract-errors.sh <maven-compile-log>}"
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
echo "Total [ERROR] lines: $TOTAL"
echo ""

# Extract unique errors with file info
echo "--- Errors by file ---"
grep "^\[ERROR\]" "$LOG_FILE" \
    | grep "\.java:" \
    | sed 's/\[ERROR\] //' \
    | sed 's#.*/src/main/java/##' \
    > "$OUT_DIR/errors-by-file.log" 2>/dev/null || true
cat "$OUT_DIR/errors-by-file.log" 2>/dev/null | head -100
echo ""

# Extract unique error messages (strip file/line info)
echo "--- Unique error patterns (top 30) ---"
grep "^\[ERROR\]" "$LOG_FILE" \
    | grep "\.java:" \
    | sed 's/\[ERROR\] .*\.java:\[[0-9]*,[0-9]*\] //' \
    | sort | uniq -c | sort -rn \
    > "$OUT_DIR/error-patterns.log" 2>/dev/null || true
head -30 "$OUT_DIR/error-patterns.log" 2>/dev/null || echo "(none)"
echo ""

# Extract errors by Java error type
echo "--- Errors by Java compiler error type ---"
grep "^\[ERROR\]" "$LOG_FILE" \
    | grep -oP '(?<=\] ).*' \
    | grep -oP '^[^:]*' \
    | sort | uniq -c | sort -rn \
    > "$OUT_DIR/error-types.log" 2>/dev/null || true
head -20 "$OUT_DIR/error-types.log" 2>/dev/null || echo "(none)"
echo ""

echo "Detailed logs in: $OUT_DIR/"
