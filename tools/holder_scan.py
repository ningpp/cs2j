"""Find ObjectHolder call sites whose wrapped variable is never read back.

A `ref`/`out` parameter is emulated with `new ObjectHolder<>(x)`; the callee assigns
`holder.value = ...`. If the caller never copies `holder.value` back into `x`, the
caller keeps the ORIGINAL object and sees none of the callee's changes — the bug
fixed in round 73 (`reader.value = restore`).

Usage: python tools/holder_scan.py <root> [contextLines]
"""
import glob
import os
import re
import sys

root = sys.argv[1]
span = int(sys.argv[2]) if len(sys.argv) > 2 else 20
pattern = re.compile(r"new io\.github\.ningpp\.compat\.ObjectHolder<>\((\w+)\)")

hits = []
for path in glob.glob(os.path.join(root, "**", "*.java"), recursive=True):
    with open(path, encoding="utf-8", errors="replace") as handle:
        lines = handle.readlines()
    for index, line in enumerate(lines):
        match = pattern.search(line)
        if not match:
            continue
        name = match.group(1)
        window = "".join(lines[index:index + span])
        # Read-back: an assignment to the wrapped variable, e.g. "reader = _readerRef.value;"
        if re.search(r"(?<![\w.])" + re.escape(name) + r"\s*=\s*\w+\.value\s*;", window):
            continue
        # A holder variable that is itself read later ("_readerRef.value") is also a read-back.
        holder = re.search(r"ObjectHolder<>\([^)]*\)", line)
        if re.search(r"ObjectHolder<[^>]*>\s+(\w+)", line):
            holder_name = re.search(r"ObjectHolder<[^>]*>\s+(\w+)", line).group(1)
            if re.search(re.escape(holder_name) + r"\.value", window):
                continue
        hits.append((path, index + 1, name, line.strip()[:120]))

print("suspect sites:", len(hits))
for path, number, name, text in hits[:40]:
    print("  %s:%d  [%s]  %s" % (os.path.relpath(path, root), number, name, text))
