"""Per-test set diff between two triage-xml snapshots.

Usage: python tools/cmp_triage.py <before.json> <after.json>
"""
import json
import sys


def load(path):
    with open(path, encoding="utf-8") as fh:
        data = json.load(fh)
    return {(r["cls"], r["method"]): r for r in data["records"]}


before = load(sys.argv[1])
after = load(sys.argv[2])
fixed = sorted(k for k in before if k not in after)
regressed = sorted(k for k in after if k not in before)
print(f"{len(before)} -> {len(after)}  net {len(before) - len(after)}")
print(f"FIXED {len(fixed)} REGRESSED {len(regressed)}")
for k in fixed:
    print("  FIXED     " + k[0].split(".")[-1] + "#" + (k[1] or "?"))
for k in regressed:
    print("  REGRESSED " + k[0].split(".")[-1] + "#" + (k[1] or "?"))
