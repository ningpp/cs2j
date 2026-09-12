"""Count how many failures are the DateTimeOffset `Z` vs `+00:00` formatting difference."""
import glob
import json
import re
import xml.etree.ElementTree as ET
from collections import Counter

pairs = []
for path in glob.glob(r"D:\cs-json-0911-sdk\**\TEST-*.xml", recursive=True):
    try:
        suite = ET.parse(path).getroot()
    except ET.ParseError:
        continue
    for case in suite.iter("testcase"):
        bad = case.find("failure")
        if bad is None:
            bad = case.find("error")
        if bad is None:
            continue
        text = (bad.text or "")
        m = re.search(r"^Expected: (.*)\nActual: (.*)$", text, re.M)
        if m:
            pairs.append({"cls": (case.get("classname") or "").split(".")[-1], "method": case.get("name"),
                          "exp": m.group(1), "act": m.group(2)})

print(f"expected/actual pairs: {len(pairs)}")


def kind(p):
    e, a = p["exp"], p["act"]
    if e == a:
        return "identical"
    if e.replace("+00:00", "Z") == a:
        return "OFFSET_Z"
    if re.fullmatch(r"[\w.$]+(?:Exception|Error)", e.strip()) or re.fullmatch(r"[\w.$]+(?:Exception|Error)", a.strip()):
        return "exception-type"
    return "other"


c = Counter(kind(p) for p in pairs)
print("\n=== pair kinds ===")
for k, v in c.most_common():
    print(f"{v:5}  {k}")

off = [p for p in pairs if kind(p) == "OFFSET_Z"]
print(f"\n=== OFFSET_Z by class === ({len(off)})")
for k, v in Counter(p["cls"] for p in off).most_common():
    print(f"{v:5}  {k}")
print("\n=== OFFSET_Z samples ===")
for p in off[:6]:
    print(f"  {p['cls']}.{p['method']}")
    print(f"     E: {p['exp'][:120]}")
    print(f"     A: {p['act'][:120]}")

# How many Utf8JsonWriterTests failures are NOT in the pairs list at all?
xml = glob.glob(r"D:\cs-json-0911-sdk\**\TEST-*Utf8JsonWriterTests.xml", recursive=True)
if xml:
    suite = ET.parse(xml[0]).getroot()
    tot = 0
    nopair = 0
    kinds = Counter()
    for case in suite.iter("testcase"):
        bad = case.find("failure")
        if bad is None:
            bad = case.find("error")
        if bad is None:
            continue
        tot += 1
        text = bad.text or ""
        m = re.search(r"^Expected: (.*)\nActual: (.*)$", text, re.M)
        if m:
            kinds["offset_z" if m.group(1).replace("+00:00", "Z") == m.group(2) else "pair-other"] += 1
        else:
            first = (bad.get("message") or text).split("\n")[0]
            kinds[(bad.get("type") or "?").split(".")[-1] + " | " + re.sub(r"\s+", " ", first)[:60]] += 1
            nopair += 1
    print(f"\n=== Utf8JsonWriterTests: {tot} failures, {nopair} without a pair ===")
    for k, v in kinds.most_common(14):
        print(f"{v:5}  {k}")
