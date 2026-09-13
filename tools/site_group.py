"""Group failures by the deepest non-test frame — the shared failure SITE.

Usage: python tools/site_group.py <triage.json> [topN]

A message says what went wrong; the deepest library frame says WHERE. Many tests
that die at the same line usually share one root cause, which is the cheapest kind
of cluster to fix. Frames in csharp.xunit.* (assertion helpers) and in the test class
itself are skipped, since every failure has those.
"""
import json
import sys
from collections import Counter, defaultdict

data = json.load(open(sys.argv[1], encoding="utf-8"))
top = int(sys.argv[2]) if len(sys.argv) > 2 else 25


def site(record):
    cls = record["cls"]
    for frame in record.get("frames") or []:
        if frame.startswith("csharp.xunit"):
            continue
        owner = frame.split(":")[0]
        if cls.endswith(owner) or owner.endswith("Tests") or owner.endswith("Test"):
            continue
        return frame
    frames = record.get("frames") or []
    return frames[0] if frames else "?"


groups = defaultdict(list)
for record in data["records"]:
    key = site(record).split(":")[0]
    groups[key].append(record)

print("total:", len(data["records"]), " sites:", len(groups))
for key, records in sorted(groups.items(), key=lambda kv: -len(kv[1]))[:top]:
    sample = records[0]
    buckets = Counter(r["bucket"] for r in records)
    print("\n%3d  %s" % (len(records), key))
    print("      e.g. %s#%s" % (sample["cls"].split(".")[-1], sample["method"]))
    print("      line %s" % site(sample).split(":")[-1])
    print("      buckets: %s" % ", ".join("%s(%d)" % kv for kv in buckets.most_common(3)))
