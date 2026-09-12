import glob
import re
import xml.etree.ElementTree as ET

B1 = re.compile(r"The JSON value could not be converted to (?:class |interface )?([\w.$]+)")
B2 = re.compile(r"((?:class |interface )?[\w.$]+) cannot be cast to (?:class |interface )?([\w.$]+)")

path = glob.glob(r"D:\cs-json-0911-sdk\**\TEST-*SpanTests.xml", recursive=True)
print("files:", path)
suite = ET.parse(path[0]).getroot()
n = 0
for case in suite.iter("testcase"):
    bad = case.find("failure") or case.find("error")
    if bad is None:
        continue
    msg = (bad.get("message") or "").strip()
    full = (bad.text or "").strip()
    blob = msg + " " + full
    m1 = B1.search(blob)
    m2 = B2.search(blob)
    print(f"--- {case.get('name')}")
    print("  msg :", repr(msg[:90]))
    print("  full0:", repr(full[:90]))
    print("  B1:", (m1.group(1) if m1 else None), " B2:", (m2.groups() if m2 else None))
    n += 1
    if n >= 6:
        break
