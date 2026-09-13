"""Group the Assert.throws bucket by the exception the test expects.

Usage: python tools/throws_group.py <triage.json>
"""
import json
import re
import sys
from collections import Counter

data = json.load(open(sys.argv[1], encoding="utf-8"))
rows = [r for r in data["records"] if r["bucket"] == "Assert.throws"]
print("Assert.throws total:", len(rows))
kinds = Counter()
for r in rows:
    m = re.search(r"Expected: ([\w.$]+)", r.get("msg") or "")
    kinds[m.group(1).split(".")[-1] if m else "?"] += 1
for kind, count in kinds.most_common():
    print("  %-40s %d" % (kind, count))
print()
for r in rows[:14]:
    m = re.search(r"Expected: ([\w.$]+)", r.get("msg") or "")
    print("  %-30s %-46s %s" % (
        m.group(1).split(".")[-1] if m else "?",
        (r["cls"].split(".")[-1] + "#" + str(r["method"]))[:44],
        " <- ".join(r["frames"][1:3])))
