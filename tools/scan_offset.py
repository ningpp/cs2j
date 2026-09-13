"""Count failures whose Expected/Actual differ by a DateTimeOffset offset rendering.

Usage: python tools/scan_offset.py <triage.json>
"""
import json
import re
import sys

data = json.load(open(sys.argv[1], encoding="utf-8"))
hits = []
for record in data["records"]:
    msg = record.get("msg") or ""
    if "+00:00" not in msg:
        continue
    if "Z" not in msg:
        continue
    hits.append(record)
print("tests mentioning both +00:00 and Z:", len(hits))
for record in hits[:25]:
    msg = record.get("msg") or ""
    expected = re.search(r"Expected: (.*)", msg)
    actual = re.search(r"Actual: (.*)", msg)
    print("  %-50s" % ((record["cls"].split(".")[-1] + "#" + str(record["method"]))[:48]))
    if expected:
        print("      E: " + expected.group(1)[:100])
    if actual:
        print("      A: " + actual.group(1)[:100])
