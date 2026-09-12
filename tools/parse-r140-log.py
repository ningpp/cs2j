#!/usr/bin/env python3
"""Parse mvn-build-r140.log (UTF-16LE) into structured failure records.

Each record: test header line, exception class, message lines (until first \tat),
Expected/Actual pair if present, top app frames (csharp.xunit.Assert / test class).
Output: D:\\code\\cs2j\\probe\\failures-r140.json
"""
import json, re, collections

LOG = r"D:\code\cs2j\mvn-build-r140.log"
OUT = r"D:\code\cs2j\probe\failures-r140.json"

with open(LOG, "r", encoding="utf-16", errors="replace") as f:
    lines = f.read().splitlines()

head_re = re.compile(r"^\[(?:ERROR|INFO)\]\s+(?P<test>[\w\.\$]+(?:\(.*?\))?(?:\[\d+\])?)\s+--\s+Time elapsed")
except_re = re.compile(r"^(?P<cls>(?:[a-zA-Z_$][\w$]*\.)+[A-Za-z_$][\w$]*(?:Exception|Error))(?::\s?(?P<msg>.*))?$")

records = []
i = 0
n = len(lines)
while i < n:
    m = head_re.match(lines[i].strip())
    if not m or ("<<< FAILURE!" not in lines[i] and "<<< ERROR!" not in lines[i]):
        i += 1
        continue
    test = m.group("test")
    # gather until next header
    j = i + 1
    buf = []
    while j < n and not head_re.match(lines[j].strip()):
        buf.append(lines[j])
        j += 1
        if len(buf) > 400:
            break
    kind = None
    msg_lines = []
    frames = []
    for k, l in enumerate(buf):
        ls = l.strip()
        if kind is None:
            em = except_re.match(ls)
            if em:
                kind = em.group("cls")
                if em.group("msg"):
                    msg_lines.append(em.group("msg"))
                continue
        if ls.startswith("at "):
            frames.append(ls[3:].strip())
            continue
        if kind is None:
            continue
        if ls.startswith("Suppressed:") or ls.startswith("Caused by:"):
            break
        if len(msg_lines) < 12:
            msg_lines.append(l.rstrip())
    msg = "\n".join(msg_lines).strip()
    records.append({
        "test": test,
        "kind": kind,
        "msg": msg,
        "frames": frames[:8],
        "class": test.split("(")[0].rsplit(".", 1)[0] if "." in test else test,
        "method": test.split("(")[0].rsplit(".", 1)[-1],
    })
    i = j

print("parsed failure records:", len(records))
with open(OUT, "w", encoding="utf-8") as f:
    json.dump(records, f, indent=1)

print("\n=== by exception class ===")
for k, v in collections.Counter(r["kind"] for r in records).most_common(30):
    print(f"{v:5d}  {k}")

PAIR = re.compile(r"Expected: (.+?)\nActual: (.+)", re.S)
print("\n=== Assert.* message heads ===")
heads = collections.Counter()
for r in records:
    if r["kind"] and r["kind"].endswith("AssertionError"):
        heads[(r["msg"] or "").strip().split("\n")[0]] += 1
for k, v in heads.most_common(40):
    print(f"{v:5d}  {k}")
