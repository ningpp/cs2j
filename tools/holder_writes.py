"""List every `x.value = ...` assignment in a tree, grouped by the holder name.

These assignments emulate C# `ref`/`out` writes. A holder that the CALLER also reads
back is fine; one whose callers keep their own variable is the round-73 bug.

Usage: python tools/holder_writes.py <root> [topN]
"""
import glob
import os
import re
import sys
from collections import Counter

root = sys.argv[1]
top = int(sys.argv[2]) if len(sys.argv) > 2 else 25
pattern = re.compile(r"^\s*([\w.]+)\.value\s*=")

groups = Counter()
samples = {}
for path in glob.glob(os.path.join(root, "**", "*.java"), recursive=True):
    with open(path, encoding="utf-8", errors="replace") as handle:
        for number, line in enumerate(handle, 1):
            match = pattern.match(line)
            if not match:
                continue
            name = match.group(1)
            groups[name] += 1
            samples.setdefault(name, (os.path.relpath(path, root), number, line.strip()[:110]))

print("distinct holders assigned:", len(groups), " total assignments:", sum(groups.values()))
for name, count in groups.most_common(top):
    path, number, text = samples[name]
    print("  %-34s %4d   e.g. %s:%d" % (name, count, path, number))
    print("        %s" % text)
