"""Group failures by a normalized message signature, to find mechanical clusters.

Usage: python tools/sig_group.py <triage.json> [topN]

The signature keeps the message's leading assertion/exception shape and strips the
parts that vary per test (numbers, quoted text, class names), so "same bug, many
tests" shows up as one high-count row.
"""
import json
import re
import sys
from collections import Counter, defaultdict

data = json.load(open(sys.argv[1], encoding="utf-8"))
top = int(sys.argv[2]) if len(sys.argv) > 2 else 25


def signature(record):
    msg = (record.get("msg") or "").strip()
    first = msg.split("\n")[0].strip()
    first = re.sub(r"'(?:[^']*)'", "'X'", first)
    first = re.sub(r"\b\d+\b", "N", first)
    first = re.sub(r"\b(?:[a-z][\w]*\.){2,}[A-Z]\w*", "T", first)
    first = re.sub(r"\bjava\.lang\.\w+", "J", first)
    first = re.sub(r"\bio\.github\.ningpp\.compat\.\w+", "C", first)
    first = re.sub(r"\s+", " ", first)
    if not first:
        frames = record.get("frames") or []
        return "EMPTY MSG @ " + (frames[0] if frames else "?")
    return first[:150]


groups = defaultdict(list)
for record in data["records"]:
    groups[signature(record)].append(record)

print("total:", len(data["records"]), " signatures:", len(groups))
for sig, records in sorted(groups.items(), key=lambda kv: -len(kv[1]))[:top]:
    sample = records[0]
    frame = (sample.get("frames") or ["?"])[0]
    classes = Counter(r["cls"].split(".")[-1] for r in records)
    print("\n%3d  %s" % (len(records), sig))
    print("      e.g. %s#%s" % (sample["cls"].split(".")[-1], sample["method"]))
    print("      at   %s" % frame)
    print("      classes: %s" % ", ".join("%s(%d)" % kv for kv in classes.most_common(4)))
