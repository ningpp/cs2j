"""ObjectHolder sites that pass a LOCAL variable as a ref/out argument without read-back.

`new ObjectHolder<>(x)` models `ref x` / `out x`. The callee writes `holder.value`.
If the caller never copies it back into `x`, the caller keeps the original value.
`this` is excluded (the callee mutates the shared object).

Usage: python tools/holder_scan2.py <root> [contextLines]
"""
import glob
import os
import re
import sys

root = sys.argv[1]
span = int(sys.argv[2]) if len(sys.argv) > 2 else 12
pattern = re.compile(r"ObjectHolder<[^>]*>\s+(\w+)\s*=\s*new\s+io\.github\.ningpp\.compat\.ObjectHolder<>\((\w+)\)")

hits = []
total = 0
for path in glob.glob(os.path.join(root, "**", "*.java"), recursive=True):
    if os.sep + "perf-probe" in path:
        continue
    with open(path, encoding="utf-8", errors="replace") as handle:
        lines = handle.readlines()
    for index, line in enumerate(lines):
        match = pattern.search(line)
        if not match:
            continue
        total += 1
        holder, wrapped = match.group(1), match.group(2)
        if wrapped == "this":
            continue
        window = "".join(lines[index:index + span])
        if re.search(re.escape(holder) + r"\.value", window):
            continue
        if re.search(r"(?<![\w.])" + re.escape(wrapped) + r"\s*=", window):
            continue
        hits.append((path, index + 1, wrapped, line.strip()[:110]))

print("holder sites:", total, " suspects:", len(hits))
for path, number, wrapped, text in hits[:40]:
    print("  %s:%d  [%s]" % (os.path.relpath(path, root), number, wrapped))
    print("      %s" % text)
