"""Secondary triage views: bucket x class matrix, and samples per bucket."""
import json
import sys
from collections import Counter, defaultdict

FILE = sys.argv[1] if len(sys.argv) > 1 else r"D:\code\cs2j\triage-r140.json"
data = json.load(open(FILE, encoding="utf-8"))
recs = data["records"]


def cls(r):
    return (r.get("cls") or "?").split(".")[-1]


mat = defaultdict(Counter)
for r in recs:
    mat[cls(r)][r["bucket"]] += 1

print("=== bucket x class (top buckets) ===")
for bucket, count in data["byBucket"][:22]:
    spread = Counter()
    for r in recs:
        if r["bucket"] == bucket:
            spread[cls(r)] += 1
    top = ", ".join(f"{c}:{n}" for c, n in spread.most_common(6))
    print(f"\n[{count:4}] {bucket}\n        {top}")

print("\n\n=== samples for the biggest buckets ===")
for bucket, count in data["byBucket"][:18]:
    print(f"\n--- {bucket} ({count}) ---")
    shown = 0
    for r in recs:
        if r["bucket"] == bucket and shown < 4:
            top = next((f for f in r["frames"] if f.startswith("dotnet.") or f.startswith("io.github")), "")
            print(f"  {cls(r)}.{r['test'].split('.')[-1][:60]}")
            print(f"      msg: {r['msg'][:150]}")
            if r.get("cause"):
                print(f"      cause: {r['cause'][:130]}")
            if top:
                print(f"      at: {top}")
            shown += 1
