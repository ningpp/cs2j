#!/bin/bash
# convert-and-compile.sh
# Automates: build converter -> install compat -> convert C# project -> maven package Java output
# Usage: ./scripts/convert-and-compile.sh [run_label]
# Logs are written to iteration-logs/<run_label>/

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"

CS_SOURCE="E:/agl-master/GraphLayout/GraphLayout.sln"
JAVA_DEST="E:/jagl521"
RUN_LABEL="${1:-$(date +%Y%m%d_%H%M%S)}"
LOG_DIR="$REPO_ROOT/iteration-logs/$RUN_LABEL"

mkdir -p "$LOG_DIR"

echo "============================================"
echo " Run: $RUN_LABEL"
echo " Source: $CS_SOURCE"
echo " Dest:   $JAVA_DEST"
echo " Logs:   $LOG_DIR"
echo "============================================"

# Step 1: Build the converter
echo "[1/5] Building converter..."
if dotnet build "$REPO_ROOT" --configuration Release > "$LOG_DIR/01-build-converter.log" 2>&1; then
    echo "  OK - converter built"
else
    echo "  FAILED - see $LOG_DIR/01-build-converter.log"
    exit 1
fi

# Step 2: Install compat library
COMPAT_DIR="$REPO_ROOT/java/csharptojava-compat"
echo "[2/5] Installing compat library..."
if [ -d "$COMPAT_DIR" ]; then
    if (cd "$COMPAT_DIR" && mvn -DskipTests install) > "$LOG_DIR/02-install-compat.log" 2>&1; then
        echo "  OK - compat installed"
    else
        echo "  FAILED - see $LOG_DIR/02-install-compat.log"
        exit 1
    fi
else
    echo "  SKIP - no compat directory found"
fi

# Step 3: Clean destination and convert
echo "[3/5] Cleaning destination..."
rm -rf "$JAVA_DEST"

echo "[4/5] Converting C# project..."
if dotnet run --project "$REPO_ROOT/src/CSharpToJava.CLI/CSharpToJava.CLI.csproj" \
    --configuration Release -- \
    convert-project \
    -s "$CS_SOURCE" \
    -d "$JAVA_DEST" \
    --prefer-procedural \
    --linq-report \
    -v \
    > "$LOG_DIR/03-convert.log" 2>&1; then
    echo "  OK - conversion complete"
else
    echo "  WARN - conversion had issues (see log), continuing to compile..."
fi

# Step 4: Maven clean package
echo "[5/5] Maven clean package..."
if [ -f "$JAVA_DEST/pom.xml" ]; then
    cd "$JAVA_DEST"
    if mvn clean package -e > "$LOG_DIR/04-maven-package.log" 2>&1; then
        echo "  OK - maven package SUCCESS"
        echo "SUCCESS" > "$LOG_DIR/RESULT.txt"
    else
        echo "  FAILED - see $LOG_DIR/04-maven-package.log"
        echo "FAILED" > "$LOG_DIR/RESULT.txt"
    fi
else
    echo "  ERROR - no pom.xml found in $JAVA_DEST"
    echo "NO_POM" > "$LOG_DIR/RESULT.txt"
    exit 1
fi

# Step 5: Extract error summary
echo ""
echo "============ Error Summary ============"
if [ -f "$LOG_DIR/04-maven-package.log" ]; then
    grep -c "^\[ERROR\]" "$LOG_DIR/04-maven-package.log" | xargs -I{} echo "Total [ERROR] lines: {}" || true
    echo ""
    echo "--- Unique error patterns (top 40) ---"
    grep "^\[ERROR\]" "$LOG_DIR/04-maven-package.log" \
        | grep "\.java:" \
        | sed 's/\[ERROR\] .*\.java:\[[0-9]*,[0-9]*\] //' \
        | sort | uniq -c | sort -rn \
        > "$LOG_DIR/05-error-summary.log" 2>/dev/null || true
    head -40 "$LOG_DIR/05-error-summary.log" 2>/dev/null || echo "(no errors)"
fi
echo "======================================="
echo "Logs written to: $LOG_DIR/"
