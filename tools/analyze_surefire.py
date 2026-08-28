#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Parse surefire XML reports across the converted SDK and categorize test failures.

Usage: python analyze_surefire.py <sdk_root>
Groups by: exception type, normalized message signature, and first meaningful
stack frame (the origin in generated/runtime/test code).
"""
import os, re, sys, glob, collections
import xml.etree.ElementTree as ET

SDK = sys.argv[1] if len(sys.argv) > 1 else r"D:\cs-java-828-sdk"

def strip_param(name: str) -> str:
    return re.sub(r"\[.*$", "", name)

def norm_message(msg: str) -> str:
    if not msg:
        return ""
    s = msg
    # numbers (incl. big literals) → N
    s = re.sub(r"\b\d{4,}\b", "N", s)
    s = re.sub(r"\b\d+\.\d+\b", "N", s)
    s = re.sub(r"\b\d+\b", "N", s)
    # quoted JSON fragments / strings → '…'
    s = re.sub(r"'[^']*'", "'…'", s)
    # hex/binary literals
    s = re.sub(r"0[xX][0-9a-fA-F]+", "H", s)
    s = re.sub(r"0b[01]+", "B", s)
    # paths / positions
    s = re.sub(r"LineNumber: \d+ \| BytePositionInLine: \d+", "LineNumber: N | BytePositionInLine: N", s)
    s = re.sub(r"Path: \$[^.]*", "Path: $", s)
    # identifiers like uint0 / ulong12
    s = re.sub(r"(uint|ulong|ushort|byte|sbyte|short|long|int|double|single|decimal)\d+", r"\1N", s)
    return re.sub(r"\s+", " ", s).strip()[:150]

def first_frames(stack: str, limit=3) -> list:
    frames = []
    for line in stack.splitlines():
        line = line.strip()
        m = re.match(r"at ([\w.$]+)\(([\w.$]+\.java)(?::(\d+))?\)", line)
        if m:
            cls, f, ln = m.group(1), m.group(2), m.group(3)
            # skip framework noise
            if cls.startswith(("org.junit", "java.base/java.util.ArrayList",
                               "java.base/java.lang.reflect.Method")):
                continue
            frames.append(f"{cls}({f}:{ln or '?'})")
            if len(frames) >= limit:
                break
    return frames

records = []  # (module, class, test, kind, exctype, normmsg, frames)
total_run = total_fail = total_err = total_skip = 0
files = glob.glob(os.path.join(SDK, "*", "target", "surefire-reports", "TEST-*.xml"))
for path in files:
    module = os.path.basename(os.path.dirname(os.path.dirname(os.path.dirname(path))))
    try:
        root = ET.parse(path).getroot()
    except ET.ParseError:
        continue
    total_run += int(root.get("tests", 0))
    total_fail += int(root.get("failures", 0))
    total_err += int(root.get("errors", 0))
    total_skip += int(root.get("skipped", 0))
    for tc in root.iter("testcase"):
        cls = strip_param(tc.get("classname", ""))
        name = strip_param(tc.get("name", ""))
        node = tc.find("failure") if tc.find("failure") is not None else tc.find("error")
        if node is None:
            continue
        kind = "F" if node.tag == "failure" else "E"
        etype = node.get("type", "?")
        msg = node.get("message", "") or ""
        stack = node.text or ""
        records.append((module, cls, name, kind, etype, norm_message(msg), first_frames(stack)))

print(f"=== TOTALS across {len(files)} XML reports ===")
print(f"tests={total_run} failures={total_fail} errors={total_err} skipped={total_skip} "
      f"=> failing={total_fail + total_err}")

print("\n=== BY MODULE ===")
by_mod = collections.Counter()
for m, c, t, k, e, msg, fr in records:
    by_mod[m] += 1
for m, n in by_mod.most_common():
    print(f"{n:5d}  {m}")

print("\n=== BY TEST CLASS (top 20) ===")
by_cls = collections.Counter()
for m, c, t, k, e, msg, fr in records:
    by_cls[c] += 1
for c, n in by_cls.most_common(20):
    print(f"{n:5d}  {c}")

print("\n=== BY EXCEPTION TYPE (top 15) ===")
by_type = collections.Counter()
for m, c, t, k, e, msg, fr in records:
    by_type[e] += 1
for e, n in by_type.most_common(15):
    print(f"{n:5d}  {e}")

print("\n=== BY (TYPE + MESSAGE SIGNATURE) (top 25) ===")
by_sig = collections.Counter()
for m, c, t, k, e, msg, fr in records:
    key = f"{e} :: {msg}" if msg else e
    by_sig[key] += 1
for s, n in by_sig.most_common(25):
    print(f"{n:5d}  {s[:160]}")

print("\n=== BY ORIGIN FRAME[0] (top 25) ===")
by_frame = collections.Counter()
for m, c, t, k, e, msg, fr in records:
    by_frame[fr[0] if fr else "(no stack)"] += 1
for fr, n in by_frame.most_common(25):
    print(f"{n:5d}  {fr}")

print("\n=== BY ORIGIN FRAME[0] + FRAME[1] (top 25) ===")
by_frame2 = collections.Counter()
for m, c, t, k, e, msg, fr in records:
    by_frame2[" -> ".join(fr[:2]) if fr else "(no stack)"] += 1
for fr, n in by_frame2.most_common(25):
    print(f"{n:5d}  {fr}")

print("\n=== CLUSTERS: type+signature+origin (top 30) ===")
by_cluster = collections.Counter()
examples = {}
for m, c, t, k, e, msg, fr in records:
    key = (e, msg, fr[0] if fr else "(no stack)")
    by_cluster[key] += 1
    if key not in examples:
        examples[key] = (m, c, t)
for (e, msg, fr), n in by_cluster.most_common(30):
    m0, c0, t0 = examples[(e, msg, fr)]
    print(f"{n:5d}  {e} | {msg[:90]}\n       @ {fr}\n       e.g. {c0}.{t0}")
