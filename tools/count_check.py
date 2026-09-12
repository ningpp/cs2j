"""Compare surefire XML counts against the console per-class summary lines."""
import glob
import re
import sys
import xml.etree.ElementTree as ET
from collections import Counter

log = sys.argv[1] if len(sys.argv) > 1 else r"D:\code\cs2j\mvn-build-r142.log"

xmlfail, xmltests = Counter(), Counter()
for p in glob.glob(r"D:\cs-json-0911-sdk\**\TEST-*.xml", recursive=True):
    s = ET.parse(p).getroot()
    suite_name = s.get("name")
    for c in s.iter("testcase"):
        if c.find("skipped") is not None:
            continue
        name = c.get("classname") or suite_name
        xmltests[name] += 1
        if c.find("failure") is not None or c.find("error") is not None:
            xmlfail[name] += 1

pat = re.compile(r"^\[ERROR\] Tests run: (\d+), Failures: (\d+), Errors: (\d+), Skipped: (\d+).*? -- in ([\w.$]+)")
conq = {}
with open(log, "rb") as fh:
    head = fh.read(2)
enc = "utf-16" if head in (b"\xff\xfe", b"\xfe\xff") else "utf-8"
with open(log, encoding=enc, errors="replace") as fh:
    for line in fh:
        m = pat.match(line.rstrip())
        if m:
            conq[m.group(5)] = (int(m.group(1)), int(m.group(2)) + int(m.group(3)))

print(f"{'class':46} {'xmlT':>6} {'conT':>6} {'xmlF':>6} {'conF':>6}")
tot_x = tot_c = tf_x = tf_c = 0
for k in sorted(set(list(xmlfail) + list(conq))):
    xt, xf = xmltests.get(k, 0), xmlfail.get(k, 0)
    ct, cf = conq.get(k, (0, 0))
    tot_x += xt
    tot_c += ct
    tf_x += xf
    tf_c += cf
    flag = "" if (xt == ct and xf == cf) else "   <<<"
    print(f"{k.split('.')[-1]:46} {xt:6} {ct:6} {xf:6} {cf:6}{flag}")
print(f"{'TOTAL':46} {tot_x:6} {tot_c:6} {tf_x:6} {tf_c:6}")
