"""Drill into one test class or bucket."""
import json
import sys
from collections import Counter

FILE = r"D:\code\cs2j\triage-r140.json"
data = json.load(open(FILE, encoding="utf-8"))
recs = data["records"]
needle = sys.argv[1]
mode = sys.argv[2] if len(sys.argv) > 2 else "class"
limit = int(sys.argv[3]) if len(sys.argv) > 3 else 8

sel = [r for r in recs if (needle in (r.get("cls") or "")) if mode == "class"] if mode == "class" \
    else [r for r in recs if needle in r["bucket"]]

print(f"selected {len(sel)}")
print("\n-- bucket spread --")
for b, c in Counter(r["bucket"] for r in sel).most_common(15):
    print(f"{c:5}  {b}")
print("\n-- identical messages --")
for m, c in Counter(r["msg"][:120] for r in sel).most_common(12):
    print(f"{c:5}  {m}")
print("\n-- deepest sdk frames --")
for f, c in Counter(next((fr.split("(")[0] for fr in r["frames"] if fr.startswith(("dotnet.", "io.github"))), "-") for r in sel).most_common(12):
    print(f"{c:5}  {f}")
print("\n-- samples --")
for r in sel[:limit]:
    print(f"  {r['test']}")
    print(f"    msg  : {r['msg'][:200]}")
    if r.get("cause"):
        print(f"    cause: {r['cause'][:160]}")
    for fr in r["frames"][:5]:
        print(f"    at {fr}")
