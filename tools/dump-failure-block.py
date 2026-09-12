#!/usr/bin/env python3
"""Extract full surefire failure blocks for given test-name substrings from the UTF-16 log."""
import sys, re, io

LOG = r"D:\code\cs2j\mvn-build-r140.log"
needles = sys.argv[1:]
with open(LOG, "r", encoding="utf-16", errors="replace") as f:
    lines = f.read().splitlines()

idx = [i for i, l in enumerate(lines) if "<<< FAILURE!" in l or "<<< ERROR!" in l]
blocks = []
for i in idx:
    # block extends until next test-start line or next [INFO] boundary
    j = i + 1
    buf = [lines[i]]
    while j < len(lines) and not re.match(r"^\[(INFO|ERROR|WARNING)\] (Running|Tests run|Building|$)", lines[j].strip()) \
            and "<<< FAILURE!" not in lines[j] and "<<< ERROR!" not in lines[j] \
            and not lines[j].strip().startswith("[INFO] Results"):
        buf.append(lines[j])
        j += 1
        if len(buf) > 80:
            break
    blocks.append((lines[i], "\n".join(buf)))

count = 0
for head, body in blocks:
    if any(n in head for n in needles):
        print("=" * 100)
        print(body)
        count += 1
        if count > 12:
            break
print(f"\n### matched {count} blocks of {len(blocks)}")
