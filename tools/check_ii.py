"""Check remaining '[[I' failures after round 72. Usage: python tools/check_ii.py <triage.json>"""
import json
import sys

data = json.load(open(sys.argv[1], encoding="utf-8"))
hits = [r for r in data["records"] if "[[I" in (r.get("msg") or "")]
print("failures mentioning [[I:", len(hits))
for record in hits:
    print("  #", record["cls"].split(".")[-1], record["method"])
    print("    ", (record.get("msg") or "")[:150].replace("\n", " "))
    print("     ", " <- ".join(record["frames"][:3]))
