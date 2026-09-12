"""Full multi-line Expected/Actual extraction from surefire XML."""
import glob
import re
import sys
import xml.etree.ElementTree as ET

name = sys.argv[1]
pattern = sys.argv[2] if len(sys.argv) > 2 else ""
limit = int(sys.argv[3]) if len(sys.argv) > 3 else 6
CLIP = 700


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
        print(f"=== {mname}  [{ (bad.get('type') or '').split('.')[-1] }]")
        if exp is None:
            print("    (no pair)")
        else:
            print(f"    E: {exp[:CLIP]}")
            print(f"    A: {act[:CLIP]}")
            if exp != act:
                for i in range(min(len(exp), len(act))):
                    if exp[i] != act[i]:
                        print(f"    diff@{i}: E[{exp[max(0,i-25):i+25]!r}] A[{act[max(0,i-25):i+25]!r}]")
                        break
                else:
                    print(f"    prefix equal; length E={len(exp)} A={len(act)}")
                ctx = next((i for i in range(min(len(exp), len(act))) if exp[i] != act[i]), 0)
                lo = max(0, ctx - 40)
                print(f"    E[..]: {exp[lo:lo+340]!r}")
                print(f"    A[..]: {act[lo:lo+340]!r}")
        n += 1
        if n >= limit:
            break
    break
