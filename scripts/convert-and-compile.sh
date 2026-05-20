#!/bin/bash
# convert-and-compile.sh
# Automates: build converter -> convert C# project -> maven compile Java output
# Usage: ./scripts/convert-and-compile.sh [run_label]
# Logs are written to scripts/logs/<run_label>/

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"

CS_SOURCE="E:/agl-master/GraphLayout/Drawing"
JAVA_DEST="E:/jmsagl519"
RUN_LABEL="${1:-$(date +%Y%m%d_%H%M%S)}"
LOG_DIR="$SCRIPT_DIR/logs/$RUN_LABEL"

mkdir -p "$LOG_DIR"

echo "============================================"
echo " Run: $RUN_LABEL"
echo " Log dir: $LOG_DIR"
echo "============================================"

# Step 1: Build the converter
echo "[1/3] Building converter..."
if dotnet build "$REPO_ROOT" > "$LOG_DIR/01-build-converter.log" 2>&1; then
    echo "  OK - converter built"
else
    echo "  FAILED - see $LOG_DIR/01-build-converter.log"
    exit 1
fi

# Step 2: Convert C# project to Java
echo "[2/3] Converting C# project..."
rm -rf "$JAVA_DEST"

if dotnet run --project "$REPO_ROOT/src/CSharpToJava.CLI/CSharpToJava.CLI.csproj" -- \
    convert-project \
    -s "$CS_SOURCE" \
    -d "$JAVA_DEST" \
    -v \
    > "$LOG_DIR/02-convert.log" 2>&1; then
    echo "  OK - conversion complete"
else
    echo "  WARN - conversion had issues (see log), continuing to compile..."
fi

# Step 3: Maven compile
echo "[3/3] Maven compiling Java project..."
if [ -f "$JAVA_DEST/pom.xml" ]; then
    cd "$JAVA_DEST"
    if mvn compile > "$LOG_DIR/03-maven-compile.log" 2>&1; then
        echo "  OK - maven compile SUCCESS"
        echo "SUCCESS" > "$LOG_DIR/RESULT.txt"
    else
        echo "  FAILED - see $LOG_DIR/03-maven-compile.log"
        echo "FAILED" > "$LOG_DIR/RESULT.txt"
    fi
else
    # Check for multi-module structure
    FOUND_POM=""
    for pom in "$JAVA_DEST"/*/pom.xml; do
        if [ -f "$pom" ]; then
            FOUND_POM="$pom"
            break
        fi
    done
    if [ -n "$FOUND_POM" ]; then
        POM_DIR="$(dirname "$FOUND_POM")"
        echo "  Found pom.xml at $POM_DIR"
        cd "$POM_DIR"
        if mvn compile > "$LOG_DIR/03-maven-compile.log" 2>&1; then
            echo "  OK - maven compile SUCCESS"
            echo "SUCCESS" > "$LOG_DIR/RESULT.txt"
        else
            echo "  FAILED - see $LOG_DIR/03-maven-compile.log"
            echo "FAILED" > "$LOG_DIR/RESULT.txt"
        fi
    else
        echo "  ERROR - no pom.xml found in $JAVA_DEST"
        echo "NO_POM" > "$LOG_DIR/RESULT.txt"
    fi
fi

# Step 4: Extract error summary
echo ""
echo "============ Error Summary ============"
if [ -f "$LOG_DIR/03-maven-compile.log" ]; then
    grep -c "^\[ERROR\]" "$LOG_DIR/03-maven-compile.log" | xargs -I{} echo "Total [ERROR] lines: {}" || true
    echo ""
    echo "--- Unique error patterns ---"
    grep "^\[ERROR\]" "$LOG_DIR/03-maven-compile.log" \
        | sed 's/\[ERROR\] .*\.java:\[[0-9]*,[0-9]*\] //' \
        | sort | uniq -c | sort -rn \
        > "$LOG_DIR/04-error-summary.log" 2>/dev/null || true
    cat "$LOG_DIR/04-error-summary.log" 2>/dev/null || echo "(no errors)"
fi
echo "======================================="
echo "Logs written to: $LOG_DIR/"
