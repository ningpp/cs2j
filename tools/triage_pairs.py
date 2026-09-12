"""Extract Expected/Actual pairs from the surefire log and cluster the differences."""
import json
import re
import sys
from collections import Counter

LOG = r"D:\code\cs2j\mvn-build-r140.log"


def open_log(path):
    with open(path, "rb") as fh:
        head = fh.read(2)
    enc = "utf-16" if head in (b"\xff\xfe", b"\xfe\xff") else "utf-8"
    return open(path, "r", encoding=enc, errors="replace")


pairs = []
with open_log(LOG) as fh:
    lines = fh.read().split("\n")

for i, line in enumerate(lines):
    if line.startswith("Expected: ") and i + 1 < len(lines) and lines[i + 1].startswith("Actual: "):
        exp, act = line[10:], lines[i + 1][8:]
        test = ""
        for j in range(i - 1, max(0, i - 30), -1):
            if "-- Time elapsed:" in lines[j]:
                test = lines[j]
                break
        pairs.append({"exp": exp, "act": act, "test": test[:160]})

print(f"pairs: {len(pairs)}")
json.dump(pairs, open(r"D:\code\cs2j\triage-pairs.json", "w", encoding="utf-8"), ensure_ascii=False, indent=1)


def norm(s):
    return s.replace("+00:00", "Z")


def sig(exp, act):
    if exp == act:
        return "IDENTICAL(?)"
    if norm(exp) == norm(act):
        return "OFFSET_ONLY(Z vs +00:00)"
    if re.fullmatch(r"-?\d+(\.\d+)?", exp or "") and re.fullmatch(r"-?\d+(\.\d+)?", act or ""):
        try:
            if float(exp) == float(act):
                return "NUMERIC_FORMAT_ONLY"
        except ValueError:
            pass
    if exp.strip('"') == act.strip('"'):
        return "QUOTING_ONLY"
    if exp.replace("\\/", "/") == act.replace("\\/", "/"):
        return "ESCAPE_ONLY"
    if re.sub(r"\s+", "", exp) == re.sub(r"\s+", "", act):
        return "WHITESPACE_ONLY"
    return None


buckets = Counter()
unmatched = []
for p in pairs:
    s = sig(p["exp"], p["act"])
    if s:
        buckets[s] += 1
    else:
        unmatched.append(p)
        key = f"E={p['exp'][:55]} || A={p['act'][:55]}"
        buckets[key] += 1

print("\n=== pair-difference clusters ===")
for k, c in buckets.most_common(30):
    print(f"{c:5}  {k}")
