"""Per-test set diff between two surefire XML snapshots — the keep/revert decision tool.

Usage:
  python tools/passfail.py snapshot <label>     # save current surefire state
  python tools/passfail.py diff <labelA> <labelB>

A "set diff" is the only trustworthy signal here: the aggregate failure/error
count of this suite drifts between otherwise identical runs (855 vs 812 observed
on unchanged sources), so a change is kept only when it adds passes without
adding failures.
"""
import glob
import json
import os
import sys
import xml.etree.ElementTree as ET

ROOT = r"D:\cs-json-0911-sdk"
SNAPDIR = r"D:\code\cs2j\snapshots"


def collect():
    """-> {test_id: 'PASS'|'FAIL'} for every executed test case.

    The id is keyed by the testcase's own @classname: surefire writes a suite per
    *file*, while parameterised/inherited methods belong to the class named on
    the testcase, and only the testcase attribute matches the console summary.
    """
    state = {}
    for path in glob.glob(ROOT + r"\**\TEST-*.xml", recursive=True):
        try:
            suite = ET.parse(path).getroot()
        except ET.ParseError:
            continue
        for case in suite.iter("testcase"):
            if case.find("skipped") is not None:
                continue
            cls = case.get("classname") or suite.get("name") or ""
            name = case.get("name") or ""
            bad = case.find("failure") is not None or case.find("error") is not None
            state[f"{cls}#{name}"] = "FAIL" if bad else "PASS"
    return state


def summarize(state):
    npass = sum(1 for v in state.values() if v == "PASS")
    nfail = sum(1 for v in state.values() if v == "FAIL")
    return npass, nfail


cmd = sys.argv[1] if len(sys.argv) > 1 else "snapshot"
os.makedirs(SNAPDIR, exist_ok=True)

if cmd == "snapshot":
    label = sys.argv[2] if len(sys.argv) > 2 else "current"
    state = collect()
    out = os.path.join(SNAPDIR, label + ".json")
    json.dump(state, open(out, "w", encoding="utf-8"), indent=0)
    p, f = summarize(state)
    print(f"{label}: {len(state)} tests, {p} pass, {f} fail  -> {out}")

elif cmd == "diff":
    a = json.load(open(os.path.join(SNAPDIR, sys.argv[2] + ".json"), encoding="utf-8"))
    b = json.load(open(os.path.join(SNAPDIR, sys.argv[3] + ".json"), encoding="utf-8"))
    fixed = sorted(t for t in b if b[t] == "PASS" and a.get(t) == "FAIL")
    broke = sorted(t for t in b if b[t] == "FAIL" and a.get(t) == "PASS")
    gone = sorted(t for t in a if t not in b)
    new = sorted(t for t in b if t not in a)
    pa, fa = summarize(a)
    pb, fb = summarize(b)
    print(f"{sys.argv[2]}: {pa} pass / {fa} fail")
    print(f"{sys.argv[3]}: {pb} pass / {fb} fail")
    print(f"\nfixed ({len(fixed)}):")
    for t in fixed:
        print("  + " + t)
    print(f"\nregressed ({len(broke)}):")
    for t in broke:
        print("  - " + t)
    if gone:
        print(f"\nnot run any more ({len(gone)}):")
        for t in gone[:20]:
            print("  ? " + t)
    if new:
        print(f"\nnewly present ({len(new)}):")
        for t in new[:20]:
            print("  * " + t)
    verdict = "KEEP" if fixed and not broke else ("NEUTRAL" if not fixed and not broke else "REVERT")
    print(f"\nverdict: {verdict}  (net {len(fixed) - len(broke):+d})")
