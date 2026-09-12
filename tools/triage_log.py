"""Parse a Maven surefire log into a per-test failure triage table.

Each per-class block looks like:
    [INFO] Running com.foo.BarTests
    [ERROR] Tests run: 3, Failures: 1, Errors: 1, Skipped: 0, ... <<< FAILURE! -- in com.foo.BarTests
    [ERROR] com.foo.BarTests.method -- Time elapsed: 0.001 s <<< ERROR!
    com.foo.SomeException: message
    \tat com.foo.Frame(File.java:12)
Surefire truncates those blocks, so the trailing aggregate list
    [ERROR]   com.foo.BarTests.method:12 » Kind message
sees the rest; both are merged here.
"""
import json
import re
import sys
from collections import Counter

LOG = sys.argv[1] if len(sys.argv) > 1 else r"D:\code\cs2j\mvn-build-r140.log"
OUT = sys.argv[2] if len(sys.argv) > 2 else r"D:\code\cs2j\triage-r140.json"

BLOCK = re.compile(r"^\[ERROR\] (?P<t>[\w.$]+(?::\d+)?(?:->[\w.$:()]+)?) -- Time elapsed: [\d.]+ s <<< (?P<kind>ERROR|FAILURE)!")
RUNNING = re.compile(r"^\[INFO\] Running (?P<c>[\w.$]+)")
CLASS_SUMMARY = re.compile(r"^\[ERROR\] Tests run: .* -- in (?P<c>[\w.$]+)")
FRAME = re.compile(r"^\s+at ([\w.$]+)\(([^:()]*)(?::(\d+))?\)")
AGG = re.compile(r"^\[ERROR\]   (?P<t>\S+?) » (?P<k>\S+)(?: (?P<m>.*))?$")
CAUSE = re.compile(r"^(?:Caused by: )?([A-Za-z_$][\w.$]*(?:Exception|Error|Throwable)):?(.*)$")


def open_log(path):
    """PowerShell redirection writes UTF-16LE; a POSIX shell writes UTF-8."""
    with open(path, "rb") as fh:
        head = fh.read(2)
    enc = "utf-16" if head in (b"\xff\xfe", b"\xfe\xff") else "utf-8"
    return open(path, "r", encoding=enc, errors="replace")


blocks, agg, class_of = {}, {}, {}
with open_log(LOG) as fh:
    cur = None
    running = None
    for raw in fh:
        line = raw.rstrip("\n")
        rm = RUNNING.match(line)
        if rm:
            running = rm.group("c")
            cur = None
            continue
        cm = CLASS_SUMMARY.match(line)
        if cm:
            running = cm.group("c")
            cur = None
            continue
        if line.startswith("[INFO] ") or line.startswith("[WARNING] "):
            cur = None
            continue
        m = BLOCK.match(line)
        if m:
            cur = {"test": m.group("t"), "outcome": m.group("kind"), "kind": "", "msg": "",
                   "cause": "", "frames": [], "cls": running}
            blocks.setdefault(m.group("t"), cur)
            if running:
                class_of[m.group("t")] = running
            continue
        if cur is not None:
            if line.startswith("\tat "):
                fm = FRAME.match(line)
                if fm and len(cur["frames"]) < 40:
                    cur["frames"].append(fm.group(1) + (":" + fm.group(3) if fm.group(3) else ""))
                continue
            if not line.strip():
                cur = None
                continue
            cm2 = CAUSE.match(line.strip())
            if cm2:
                if line.strip().startswith("Caused by:"):
                    cur["cause"] = cm2.group(1) + (": " + cm2.group(2).strip() if cm2.group(2).strip() else "")
                elif not cur["kind"]:
                    cur["kind"] = cm2.group(1)
                    cur["msg"] = cm2.group(2).strip()
                continue
            if cur["kind"] and not cur["msg"]:
                cur["msg"] = line.strip()
        am = AGG.match(line)
        if am:
            agg.setdefault(am.group("t"), {"kind": am.group("k"), "msg": (am.group("m") or "").strip(), "cls": running})

print(f"per-class blocks: {len(blocks)}   aggregate lines: {len(agg)}   only-in-blocks: {len(set(blocks)-set(agg))}   only-in-agg: {len(set(agg)-set(blocks))}")


def class_of_name(t):
    """com.foo.BarTests.method -> com.foo.BarTests (parameterised names included).

    A package segment may itself be called "Tests", so a candidate must look like a
    class name (UpperCamelCase) and the segment right after it must look like a
    method (lowerCamelCase) or a parameterised suffix.
    """
    parts = t.split(".")
    for i, p in enumerate(parts[:-1]):
        if not p[:1].isupper():
            continue
        after = parts[i + 1]
        looks_like_method = after[:1].islower() or after.startswith("[") or after[:1].isdigit()
        if looks_like_method and (p.endswith("Tests") or p.endswith("Test")):
            return ".".join(parts[: i + 1])
    return ".".join(parts[:-1])

records = {}
for t, b in blocks.items():
    a = agg.get(t)
    kind = b["kind"] or (a["kind"] if a else "")
    msg = b["msg"] or (a["msg"] if a else "")
    if a and a["kind"] not in ("Json", "ClassCast", "IllegalState", "NullPointer", "UnsupportedOperation",
                              "IllegalArgument", "TargetInvocation", "Runtime", "KeyNotFound",
                              "ArgumentOutOfRange", "ArrayIndexOutOfBounds", "Format", "StackOverflow",
                              "UndeclaredThrowable", "JsonReader", "NotSupported"):
        kind = a["kind"]
    records[t] = {"test": t, "outcome": b["outcome"], "kind": kind, "msg": msg, "cause": b["cause"],
                  "frames": b["frames"], "cls": b["cls"] or (a["cls"] if a else "") or class_of_name(t)}
for t, a in agg.items():
    if t in records:
        if not records[t]["msg"]:
            records[t]["msg"] = a["msg"]
        if not records[t]["kind"]:
            records[t]["kind"] = a["kind"]
        continue
    records[t] = {"test": t, "outcome": "AGG", "kind": a["kind"], "msg": a["msg"], "cause": "",
                  "frames": [], "cls": a["cls"] or class_of_name(t)}
for t, r in records.items():
    r["cls"] = class_of_name(t)
    r["method"] = t[len(r["cls"]) + 1:] if t.startswith(r["cls"]) else t

print(f"total failures: {len(records)}")


def bucket(r):
    msg = r["msg"]
    m = re.match(r"^(?:class |interface )?([\w.$]+) cannot be cast to (?:class |interface )?([\w.$]+)", msg)
    if m:
        return f"ClassCast {m.group(1).split('.')[-1]} -> {m.group(2).split('.')[-1]}"
    m = re.match(r"The JSON value could not be converted to (?:class |interface )?([\w.$]+)", msg)
    if m:
        return f"JsonConvert {m.group(1).split('.')[-1]}"
    m = re.match(r"The type '([^']+)' is not supported", msg)
    if m:
        return f"NotSupportedType {m.group(1)}"
    m = re.match(r"The collection type '([^']+)' is abstract", msg)
    if m:
        return f"AbstractCollection {m.group(1).split('.')[-1]}"
    m = re.match(r"The data extension property '([^']+)' does not match", msg)
    if m:
        return "ExtensionData signature"
    m = re.match(r"The type '([^']+)' is not a supported Dictionary key type", msg)
    if m:
        return "DictKeyNotSupported"
    if not msg:
        return (r["kind"] or "?") + " (no message)"
    return f"{r['kind']}: " + re.sub(r"\s+", " ", msg)[:80]


buckets = Counter(bucket(r) for r in records.values())
kinds = Counter(r["kind"] or "?" for r in records.values())
byclass = Counter(r["cls"] or "?" for r in records.values())

print("\n=== exception kind ===")
for k, c in kinds.most_common(24):
    print(f"{c:5}  {k}")
print("\n=== bucket (top 55) ===")
for k, c in buckets.most_common(55):
    print(f"{c:5}  {k}")
print("\n=== worst test classes ===")
for k, c in byclass.most_common(25):
    print(f"{c:5}  {k.split('.')[-1]}")

for r in records.values():
    r["bucket"] = bucket(r)

with open(OUT, "w", encoding="utf-8") as fh:
    json.dump({"total": len(records), "byKind": kinds.most_common(), "byBucket": buckets.most_common(),
               "byClass": byclass.most_common(), "records": list(records.values())},
              fh, ensure_ascii=False, indent=1)
print(f"\nwrote {OUT}")
