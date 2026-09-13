"""Group Assert.equal failures by their Expected/Actual SHAPE.

Usage: python tools/equal_group.py <triage.json> [topN]

"Values differ" says nothing on its own; the pair is what identifies a mechanical
bug (e.g. Number/N vs String/N is a type mismatch, not a wrong value).
"""
import json
import re
import sys
from collections import Counter, defaultdict

data = json.load(open(sys.argv[1], encoding="utf-8"))
top = int(sys.argv[2]) if len(sys.argv) > 2 else 20


def shape(text):
    text = text.strip()
    text = re.sub(r"'(?:[^']*)'", "'X'", text)
    text = re.sub(r"\b\d+(?:\.\d+)?\b", "N", text)
    text = re.sub(r"\b(?:[a-z][\w]*\.){2,}[A-Z]\w*", "T", text)
    text = re.sub(r"\{[^{}]*\}", "{...}", text)
    text = re.sub(r"\[[^\[\]]*\]", "[...]", text)
    if re.fullmatch(r"N", text):
        kind = "Number"
    elif re.fullmatch(r"T", text):
        kind = "TypeName"
    elif text.startswith("{"):
        kind = "Object"
    elif text.startswith("["):
        kind = "Array"
    else:
        kind = "Text"
    return "%s: %s" % (kind, text[:60])


groups = defaultdict(list)
for record in data["records"]:
    msg = record.get("msg") or ""
    if not msg.startswith("Assert.equal"):
        continue
    expected = re.search(r"Expected: (.*)", msg)
    actual = re.search(r"Actual: (.*)", msg)
    sig = "E[%s]  A[%s]" % (
        shape(expected.group(1)) if expected else "?",
        shape(actual.group(1)) if actual else "?")
    groups[sig].append(record)

print("Assert.equal failures:", sum(len(v) for v in groups.values()), " shapes:", len(groups))
for sig, records in sorted(groups.items(), key=lambda kv: -len(kv[1]))[:top]:
    sample = records[0]
    classes = Counter(r["cls"].split(".")[-1] for r in records)
    print("\n%3d  %s" % (len(records), sig))
    print("      e.g. %s#%s  @ %s" % (sample["cls"].split(".")[-1], sample["method"],
                                      (sample.get("frames") or ["?"])[0]))
    print("      classes: %s" % ", ".join("%s(%d)" % kv for kv in classes.most_common(4)))
