"""Show the actual expected/actual text for a test class, uncapped."""
import glob
import re
import sys
import xml.etree.ElementTree as ET
from collections import Counter

name = sys.argv[1] if len(sys.argv) > 1 else "Utf8JsonWriterTests"
limit = int(sys.argv[2]) if len(sys.argv) > 2 else 10

xml = glob.glob(rf"D:\cs-json-0911-sdk\**\TEST-*{name}.xml", recursive=True)
if not xml:
    print("no report for", name)
    sys.exit(1)
suite = ET.parse(xml[0]).getroot()

rows = []
for case in suite.iter("testcase"):
    bad = case.find("failure")
    if bad is None:
        bad = case.find("error")
    if bad is None:
        continue
    text = bad.text or ""
    m = re.search(r"^Expected: (.*)\nActual: (.*)$", text, re.M)
    rows.append({"method": case.get("name"), "type": (bad.get("type") or "").split(".")[-1],
                 "msg": (bad.get("message") or "").split("\n")[0][:110],
                 "exp": m.group(1) if m else None, "act": m.group(2) if m else None})

print(f"{name}: {len(rows)} failures")
print("\n-- by exception type --")
for k, v in Counter(r["type"] for r in rows).most_common():
    print(f"{v:5}  {k}")
print("\n-- expected/actual samples --")
n = 0
for r in rows:
    if r["exp"] is not None:
        print(f"### {r['method']}")
        print(f"    E: {r['exp'][:200]}")
        print(f"    A: {r['act'][:200]}")
        n += 1
        if n >= limit:
            break
print("\n-- no-pair samples --")
n = 0
for r in rows:
    if r["exp"] is None:
        print(f"### {r['method']}  [{r['type']}]")
        print(f"    {r['msg']}")
        n += 1
        if n >= limit:
            break
