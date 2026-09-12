"""List differing aggregate failure entries for one class between two maven logs."""
import re
import sys
from collections import Counter

cls = sys.argv[1]
logs = sys.argv[2:]
AGG = re.compile(r"^\[ERROR\]   (?P<t>.+?) »(?: (?P<k>\S+))?(?: (?P<m>.*))?$")


def entries(path):
    out = Counter()
    with open(path, "rb") as fh:
        head = fh.read(2)
    enc = "utf-16" if head in (b"\xff\xfe", b"\xfe\xff") else "utf-8"
    with open(path, encoding=enc, errors="replace") as fh:
        for line in fh:
            m = AGG.match(line.rstrip("\n"))
            if not m:
                continue
            t = m.group("t").strip()
            if ("." + cls + ".") not in t:
                continue
            key = re.sub(r"->.*$", "", t) + "  ::  " + (m.group("k") or "")
            out[key] += 1
    return out


a = entries(logs[0])
b = entries(logs[1])
print(f"{cls}: {logs[0]} -> {sum(a.values())} entries, {logs[1]} -> {sum(b.values())} entries")
print("\nnew in", logs[1], ":")
for k, v in sorted((b - a).items()):
    print(f"  +{v}  {k}")
print("\ngone from", logs[1], ":")
for k, v in sorted((a - b).items()):
    print(f"  -{v}  {k}")
