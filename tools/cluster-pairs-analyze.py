#!/usr/bin/env python3
"""Analyze D:\\code\\cs2j\\triage-pairs.json (exp/act extracted from mvn-build-r140.log)."""
import json, re, collections

d = json.load(open(r"D:\code\cs2j\triage-pairs.json", encoding="utf-8"))
print("pairs records:", len(d))

EXC = re.compile(r"(?:[a-z][\w]*\.)+[A-Z]\w*(?:Exception|Error)$")
def isexc(s):
    return bool(s) and bool(EXC.match(s.strip()))

exc = [r for r in d if isexc(r["exp"]) or isexc(r["act"])]
print("records mentioning an exception type:", len(exc))

pairs = collections.Counter((r["exp"].strip(), r["act"].strip()) for r in exc)
print("\n=== (expected -> actual) pairs ===")
for (e, a), c in pairs.most_common(40):
    print(f"{c:5d}  {e}   ->   {a}")

# group by expected
print("\n=== by expected exception ===")
byexp = collections.defaultdict(collections.Counter)
for r in exc:
    byexp[r["exp"].strip()][r["act"].strip()] += 1
for e, ctr in sorted(byexp.items(), key=lambda kv: -sum(kv[1].values())):
    print(f"\n{e}  (total {sum(ctr.values())})")
    for a, c in ctr.most_common():
        print(f"    {c:4d}  <- {a}")

# tests per pair for the top pairs
print("\n=== test names for the top 8 pairs ===")
for (e, a), c in pairs.most_common(8):
    if not isexc(e) and not isexc(a):
        continue
    print(f"\n## {e} -> {a}  ({c})")
    n = 0
    for r in exc:
        if r["exp"].strip() == e and r["act"].strip() == a:
            print("   ", r["test"].strip())
            n += 1
            if n >= 25:
                print("    ...")
                break
