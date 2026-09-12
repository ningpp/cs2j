"""Extract Expected/Actual pairs for Assert.equal failures in a given test class.

Also reconciles the parsed failures against the surefire per-class counts so no
failure is silently missing from the triage.
"""
import json
import re
import sys
from collections import Counter

FILE = r"D:\code\cs2j\triage-r140.json"
data = json.load(open(FILE, encoding="utf-8"))
recs = data["records"]

if len(sys.argv) > 1 and sys.argv[1] == "reconcile":
    counted = {}
    with open(r"D:\code\cs2j\mvn-build-r140.log", "rb") as fh:
        head = fh.read(2)
    enc = "utf-16" if head in (b"\xff\xfe", b"\xfe\xff") else "utf-8"
    pat = re.compile(r"^\[ERROR\] Tests run: (\d+), Failures: (\d+), Errors: (\d+), Skipped: (\d+).*? -- in ([\w.$]+)")
    with open(r"D:\code\cs2j\mvn-build-r140.log", encoding=enc, errors="replace") as fh:
        for line in fh:
            m = pat.match(line.rstrip("\n"))
            if m:
                counted[m.group(5)] = int(m.group(2)) + int(m.group(3))
    have = Counter(r.get("cls") or "?" for r in recs)
    total_counted = sum(counted.values())
    print(f"surefire counted failures+errors: {total_counted} over {len(counted)} classes")
    print(f"triage parsed: {len(recs)}")
    missing = {c: n - have.get(c, 0) for c, n in counted.items() if n > have.get(c, 0)}
    print(f"\nclasses short in triage ({sum(missing.values())} failures):")
    for c, n in sorted(missing.items(), key=lambda kv: -kv[1]):
        print(f"  {n:4}  {c}")
    sys.exit(0)

needle = sys.argv[1]
want = int(sys.argv[2]) if len(sys.argv) > 2 else 6
sel = [r for r in recs if needle in (r.get("cls") or "")]
print(f"{len(sel)} failures in {needle}\n")
shown = 0
for r in sel:
    if r["msg"].startswith("Assert.equal") and r.get("cause"):
        print(f"### {r['test']}")
        print(r["cause"][:900])
        print()
        shown += 1
        if shown >= want:
            break
if shown == 0:
    for r in sel[:want]:
        print(f"### {r['test']}\n{r['msg'][:400]}\n{r.get('cause','')[:400]}\n")
