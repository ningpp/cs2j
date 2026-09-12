"""Diff the failing test SITES for one class between two maven logs."""
import re
import sys

full_log, single_log, cls = sys.argv[1], sys.argv[2], sys.argv[3]
AGG = re.compile(r"^\[ERROR\]   (?P<t>.+?) »")


def sites(path):
    out = set()
    with open(path, "rb") as fh:
        head = fh.read(2)
    enc = "utf-16" if head in (b"\xff\xfe", b"\xfe\xff") else "utf-8"
    with open(path, encoding=enc, errors="replace") as fh:
        for line in fh:
            m = AGG.match(line.rstrip("\n"))
            if not m:
                continue
            t = re.sub(r"->.*$", "", m.group("t").strip())
            if t.split(".")[0] == cls or ("." + cls + ".") in t:
                out.add(t)
    return out


a = sites(full_log)
b = sites(single_log)
print(f"{cls}: full-suite {len(a)} sites, single-class {len(b)} sites")
only_full = sorted(a - b)
only_single = sorted(b - a)
print(f"\nONLY in full-suite ordering ({len(only_full)}):")
for t in only_full:
    print("  F " + t)
print(f"\nONLY when run alone ({len(only_single)}):")
for t in only_single:
    print("  S " + t)
