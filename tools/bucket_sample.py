"""Print one sample per named bucket. Usage: python tools/bucket_sample.py <triage.json> [n] [bucket ...]"""
import json
import sys

data = json.load(open(sys.argv[1], encoding="utf-8"))
n = int(sys.argv[2]) if len(sys.argv) > 2 else 3
wanted = sys.argv[3:] if len(sys.argv) > 3 else sorted(
    {r["bucket"] for r in data["records"]}, key=lambda b: -sum(1 for r in data["records"] if r["bucket"] == b)
)
for want in wanted:
    hits = [r for r in data["records"] if r["bucket"] == want]
    print("=== %s (%d)" % (want, len(hits)))
    for r in hits[:n]:
        print("  # %s %s" % (r["cls"].split(".")[-1], r["method"]))
        print("    " + (r.get("msg") or "")[:220].replace("\n", " / "))
        print("     frames: " + " <- ".join(r["frames"][:6]))
