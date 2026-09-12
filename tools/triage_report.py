"""For every bucket: size, class spread, the deepest generated-SDK frame, and samples."""
import json
import re
import sys
from collections import Counter

FILE = r"D:\code\cs2j\triage-xml.json"
data = json.load(open(FILE, encoding="utf-8"))
recs = data["records"]
top = int(sys.argv[1]) if len(sys.argv) > 1 else 40
want = sys.argv[2] if len(sys.argv) > 2 else ""

SKIP = ("dotnet.text.json.ThrowHelper", "java.base", "java.lang.reflect", "jdk.internal", "csharp.xunit")


def cause_frame(r):
    """First frame inside the generated SDK that is not ThrowHelper/reflection."""
    for f in r["frames"]:
        if f.startswith(SKIP):
            continue
        if f.startswith(("dotnet.", "io.github")):
            return f
    return "-"


def test_frame(r):
    """Deepest frame belonging to a test class."""
    for f in reversed(r["frames"]):
        if ".Tests." in f or ".Tests$" in f:
            return f
    return "-"


print(f"total {len(recs)}\n")
for i, (b, n) in enumerate(data["byBucket"][:top]):
    sel = [r for r in recs if r["bucket"] == b]
    classes = Counter(r["cls"].split(".")[-1] for r in sel)
    cfs = Counter(cause_frame(r) for r in sel)
    print(f"[{n:4}] {b}")
    print("       classes : " + ", ".join(f"{c}:{k}" for c, k in classes.most_common(6)))
    print("       frames  : " + ", ".join(f"{c}:{k}" for c, k in cfs.most_common(3)))
    if want and (want in b or b in want):
        see = [r for r in sel if cause_frame(r) == cfs.most_common(1)[0][0]][:6]
        for r in see:
            print(f"         - {r['cls'].split('.')[-1]}.{r['method'][:52]}  @{test_frame(r)}")
            print(f"           {re.sub(chr(10), ' ', r['msg'])[:150]}")
    print()
