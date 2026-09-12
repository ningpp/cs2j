"""Authoritative triage from surefire XML reports.

The console log truncates the aggregate failure list, so the XML reports in
*/target/surefire-reports/TEST-*.xml are the ground truth: every testcase with
its failure/error message, type and full stack trace.
"""
import glob
import json
import re
import sys
import xml.etree.ElementTree as ET
from collections import Counter

ROOT = sys.argv[1] if len(sys.argv) > 1 else r"D:\cs-json-0911-sdk"
OUT = sys.argv[2] if len(sys.argv) > 2 else r"D:\code\cs2j\triage-xml.json"
FULL = " ".join(sys.argv[3:]) if len(sys.argv) > 3 else ""


def short(name):
    return re.sub(r"^(class |interface )", "", name.strip().rstrip(".")).split(".")[-1].strip()


def bucket(r):
    blob = r["msg"] + " " + r["full"]
    m = re.search(r"(?:class |interface )?([\w.$]+) cannot be cast to (?:class |interface )?([\w.$]+)", blob)
    if m:
        return f"ClassCast {short(m.group(1))} -> {short(m.group(2))}"
    m = re.search(r"The JSON value could not be converted to (?:class |interface )?([\w.$]+)", blob)
    if m:
        return f"JsonConvert {short(m.group(1))}"
    m = re.search(r"The type '([^']+)' is not supported", blob)
    if m:
        return f"NotSupportedType {short(m.group(1))}"
    m = re.search(r"The collection type '([^']+)' is abstract", blob)
    if m:
        return f"AbstractCollection {short(m.group(1))}"
    m = re.search(r"The type '([^']+)' is not a supported Dictionary key type", blob)
    if m:
        return "DictKeyNotSupported"
    m = re.search(r"The data extension property '([^']+)' does not match", blob)
    if m:
        return "ExtensionDataSignature"
    m = re.search(r"Expected: (\S+)\s*\nActual: (\S+)", blob)
    if m:
        e, a = short(m.group(1)), short(m.group(2))
        if re.fullmatch(r"[\w.$]+(?:Exception|Error)", e) or re.fullmatch(r"[\w.$]+(?:Exception|Error)", a):
            return f"Throws {e} -> {a}"
        return f"Expect {m.group(1)[:50]} -> {m.group(2)[:50]}"
    m = re.search(r"Assert\.(\w+)\(\) failure", blob)
    if m:
        return f"Assert.{m.group(1)}"
    if not r["msg"]:
        return (r["kind"] or "?").split(".")[-1] + " (no message)"
    return (r["kind"] or "?").split(".")[-1] + ": " + re.sub(r"\s+", " ", r["msg"])[:70]


records = []
for path in glob.glob(ROOT + r"\**\TEST-*.xml", recursive=True):
    try:
        suite = ET.parse(path).getroot()
    except ET.ParseError as exc:
        print(f"skip {path}: {exc}")
        continue
    cls = suite.get("name") or ""
    for case in suite.iter("testcase"):
        bad = case.find("failure")
        if bad is None:
            bad = case.find("error")
        if bad is None:
            continue
        text = (bad.text or "").strip()
        frames = [m.group(1) + (":" + m.group(3) if m.group(3) else "")
                  for m in re.finditer(r"^\s+at ([\w.$]+)\(([^:()]*)(?::(\d+))?\)", text, re.M)]
        records.append({
            "cls": case.get("classname") or cls,
            "method": case.get("name") or "",
            "outcome": "ERROR" if bad.tag == "error" else "FAILURE",
            "kind": bad.get("type") or "",
            "msg": (bad.get("message") or "").strip()[:600],
            "full": text[:2500],
            "frames": frames[:60],
        })

print(f"parsed {len(records)} failing testcases from {ROOT}")

buckets = Counter(bucket(r) for r in records)
byclass = Counter(r["cls"] for r in records)
bykind = Counter(r["kind"] or "?" for r in records)

print("\n=== exception kind ===")
for k, c in bykind.most_common(20):
    print(f"{c:5}  {k}")
print("\n=== bucket (top 60) ===")
for k, c in buckets.most_common(60):
    print(f"{c:5}  {k}")
print("\n=== worst classes ===")
for k, c in byclass.most_common(45):
    print(f"{c:5}  {k.split('.')[-1]}")

for r in records:
    r["bucket"] = bucket(r)
    r.pop("full", None)
json.dump({"total": len(records), "byBucket": buckets.most_common(), "byClass": byclass.most_common(),
           "byKind": bykind.most_common(), "records": records},
          open(OUT, "w", encoding="utf-8"), ensure_ascii=False, indent=1)
print(f"\nwrote {OUT}")
