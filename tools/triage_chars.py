"""Extract the exact Expected/Actual strings around the first difference, char by char."""
import glob
import re
import sys
import xml.etree.ElementTree as ET

name = sys.argv[1]
pattern = sys.argv[2] if len(sys.argv) > 2 else ""
limit = int(sys.argv[3]) if len(sys.argv) > 3 else 3
WINDOW = int(sys.argv[4]) if len(sys.argv) > 4 else 55


def extract(text):
    em = re.search(r"^Expected: ?(.*)$", text, re.M)
    am = re.search(r"^Actual: ?(.*)$", text, re.M)
    if not em or not am:
        return None, None
    exp = text[em.end():am.start()].strip("\n")
    rest = text[am.end():]
    stop = re.search(r"^\s+at ", rest, re.M)
    act = (rest[:stop.start()] if stop else rest).strip("\n")
    return (em.group(1) + "\n" + exp).strip(), (am.group(1) + "\n" + act).strip()


for path in glob.glob(rf"D:\cs-json-0911-sdk\**\TEST-*{name}.xml", recursive=True):
    suite = ET.parse(path).getroot()
    n = 0
    for case in suite.iter("testcase"):
        bad = case.find("failure")
        if bad is None:
            bad = case.find("error")
        if bad is None:
            continue
        mname = case.get("name") or ""
        if pattern and pattern not in mname:
            continue
        exp, act = extract(bad.text or "")
        print(f"=== {mname}   lenE={len(exp) if exp else 0} lenA={len(act) if act else 0}")
        if exp is None:
            print("    (no pair)")
            continue
        i = next((k for k in range(min(len(exp), len(act))) if exp[k] != act[k]), None)
        if i is None:
            print("    identical prefix")
            continue
        lo = max(0, i - 20)
        print(f"    first diff at {i}")
        print(f"    E chars: " + " ".join(f"{ord(c):04X}" for c in exp[lo:i + WINDOW]))
        print(f"    A chars: " + " ".join(f"{ord(c):04X}" for c in act[lo:i + WINDOW]))
        print(f"    E raw  : {exp[lo:i + WINDOW]!r}")
        print(f"    A raw  : {act[lo:i + WINDOW]!r}")
        n += 1
        if n >= limit:
            break
    break
