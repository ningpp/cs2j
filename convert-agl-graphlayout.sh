#!/usr/bin/env bash
# Convert the agl-master GraphLayout C# project to Java and build with Maven.
# Usage:
#   ./convert-agl-graphlayout.sh [--print-only]
#
# Environment variables (override defaults):
#   SOURCE_PATH  – path to GraphLayout.sln (default: ~/agl-master/GraphLayout/GraphLayout.sln)
#   OUTPUT_DIR   – output directory for generated Java project (default: ~/agl-java)

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

SOURCE_PATH="${SOURCE_PATH:-${HOME}/agl-master/GraphLayout/GraphLayout.sln}"
OUTPUT_DIR="${OUTPUT_DIR:-${HOME}/agl-java}"

echo "Source: ${SOURCE_PATH}"
echo "Output: ${OUTPUT_DIR}"
echo

if [[ "${1:-}" == "--print-only" ]]; then
    echo "dotnet run --project \"src/CSharpToJava.CLI/CSharpToJava.CLI.csproj\" --configuration Release -- convert-project -s \"${SOURCE_PATH}\" -d \"${OUTPUT_DIR}\" --mode multi-module --prefer-procedural --verbose"
    echo "cd \"${OUTPUT_DIR}\""
    echo "mvn clean package -e"
    exit 0
fi

if [[ ! -e "${SOURCE_PATH}" ]]; then
    echo "[ERROR] Source path not found: ${SOURCE_PATH}" >&2
    exit 1
fi

mkdir -p "${OUTPUT_DIR}"

pushd "${SCRIPT_DIR}" > /dev/null
dotnet run \
    --project "src/CSharpToJava.CLI/CSharpToJava.CLI.csproj" \
    --configuration Release \
    -- convert-project \
        -s "${SOURCE_PATH}" \
        -d "${OUTPUT_DIR}" \
        --mode multi-module \
        --prefer-procedural \
        --verbose
popd > /dev/null

if ! command -v mvn > /dev/null 2>&1; then
    echo
    echo "[ERROR] Maven (mvn) not found in PATH." >&2
    exit 1
fi

echo "Running Maven build in ${OUTPUT_DIR}"
pushd "${OUTPUT_DIR}" > /dev/null
mvn clean package -e
popd > /dev/null

echo
echo "[OK] Conversion and Maven build completed: ${OUTPUT_DIR}"
