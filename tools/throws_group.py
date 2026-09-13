"""Group Assert.throws failures by (expected, actual) exception pair.

Usage: python tools/throws_group.py <triage.json>
"""
import json
import re
import sys
from collections import Counter, defaultdict

data = json.load(open(sys.argv[1], encoding="utf-8"))
rows = [r for r in data["records"] if (r.get("msg") or "").startswith("Assert.throws")]
print("Assert.throws total:", len(rows))

pairs = Counter()
groups = defaultdict(list)
for record in rows:
    msg = record.get("msg") or ""
    expected = re.search(r"Expected: ([\w.$]+)", msg)
    actual = re.search(r"Actual: ([\w.$]+)", msg)
    key = (expected.group(1).split(".")[-1] if expected else "?",
           actual.group(1).split(".")[-1] if actual else "NONE(no exception)")
    pairs[key] += 1
    groups[key].append(record)

for key, count in pairs.most_common():
    print("\n%3d  expected=%s  actual=%s" % (count, key[0], key[1]))
    for record in groups[key][:4]:
        print("      %s#%s" % (record["cls"].split(".")[-1], record["method"]))
