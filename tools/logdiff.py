"""Per-test set diff driven by the maven `-e` aggregate failure list.

The surefire XML in this project is not a reliable census: some classes emit a
single collapsed testcase element (CustomConverterTests: 1 vs 143 executed), and
skipped/parameterised cases are counted differently. The console aggregate list
printed by `mvn -e` is complete and matches the summary exactly
(810 lines == Failures 425 + Errors 385), so it is the census of record.

Usage:
  python tools/logdiff.py snapshot <label> <log>
  python tools/logdiff.py diff <labelA> <labelB>
"""
import os
import re
import sys

SNAPDIR = r"D:\code\cs2j\snapshots"
AGG = re.compile(r"^\[ERROR\]   (?P<t>.+?) »")
SUMMARY = re.compile(r"^\[ERROR\] Tests run: (\d+), Failures: (\d+), Errors: (\d+), Skipped: (\d+)$")


def open_log(path):
    with open(path, "rb") as fh:
        head = fh.read(2)
    enc = "utf-16" if head in (b"\xff\xfe", b"\xfe\xff") else "utf-8"
    return open(path, "r", encoding=enc, errors="replace")


def parse(path):
    """-> (set of failing test ids, summary tuple)"""
    fails, summary = set(), None
    with open_log(path) as fh:
        for line in fh:
            line = line.rstrip("\n")
            m = AGG.match(line)
            if m:
                t = m.group("t").strip()
                # Keep the line number and drop only the lambda indirection: the
                # same test can fail at several distinct assertion sites, and the
                # site is the useful part of the identity.
                t = re.sub(r"->.*$", "", t)
                fails.add(t)
                continue
            s = SUMMARY.match(line)
            if s:
                summary = tuple(int(g) for g in s.groups())
    return fails, summary


def cls_of(t):
    parts = t.split(".")
    for i, p in enumerate(parts[:-1]):
        if p[:1].isupper() and (p.endswith("Tests") or p.endswith("Test")):
            return ".".join(parts[: i + 1])
    return ".".join(parts[:-1])


cmd = sys.argv[1]
os.makedirs(SNAPDIR, exist_ok=True)

if cmd == "snapshot":
    label, log = sys.argv[2], sys.argv[3]
    fails, summary = parse(log)
    out = os.path.join(SNAPDIR, label + ".txt")
    with open(out, "w", encoding="utf-8") as fh:
        fh.write(f"# summary={summary}\n")
        for t in sorted(fails):
            fh.write(t + "\n")
    print(f"{label}: {len(fails)} failing tests, summary={summary}")
    if summary and sum(summary[1:3]) != len(fails):
        print(f"  WARNING: summary says {sum(summary[1:3])} but {len(fails)} unique ids parsed")
    print(f"  -> {out}")

elif cmd == "diff":
    def load(label):
        with open(os.path.join(SNAPDIR, label + ".txt"), encoding="utf-8") as fh:
            lines = fh.read().splitlines()
        head = lines[0]
        return set(lines[1:]), head

    a, ha = load(sys.argv[2])
    b, hb = load(sys.argv[3])

    def base_id(t):
        """Strip the assertion line and any lambda hop: the test's identity, not the site."""
        t = re.sub(r"->.*$", "", t)
        return re.sub(r":\d+$", "", t)

    def by_test(s):
        return {base_id(t) for t in s}

    fixed = sorted(a - b)
    broke = sorted(b - a)
    ta, tb = by_test(a), by_test(b)
    t_fixed = sorted(ta - tb)
    t_broke = sorted(tb - ta)
    print(f"{sys.argv[2]}: {len(a)} failure sites over {len(ta)} tests   {ha}")
    print(f"{sys.argv[3]}: {len(b)} failure sites over {len(tb)} tests   {hb}")
    print(f"\nTEST-level fixed ({len(t_fixed)}):")
    for t in t_fixed:
        print("  + " + t)
    print(f"\nTEST-level regressed ({len(t_broke)}):")
    for t in t_broke:
        print("  - " + t)
    print(f"\nSITE-level fixed ({len(fixed)}):")
    for t in fixed:
        print("  ~ " + t)
    print(f"\nSITE-level regressed ({len(broke)}):")
    for t in broke:
        print("  ~ " + t)
    sa = ha.split("=")[1].strip("() ").split(",")
    sb = hb.split("=")[1].strip("() ").split(",")
    na = int(sa[1]) + int(sa[2]) if len(sa) > 2 else len(a)
    nb = int(sb[1]) + int(sb[2]) if len(sb) > 2 else len(b)
    verdict = "KEEP" if nb < na and not t_broke else ("NEUTRAL" if nb == na else "REVERT")
    print(f"\nfailures+errors: {na} -> {nb}   net {na - nb:+d}")
    print(f"verdict: {verdict}")
