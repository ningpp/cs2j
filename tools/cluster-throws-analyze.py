#!/usr/bin/env python3
"""Cluster analysis: 'expected an exception, got the wrong one / none'.

Reads D:\\code\\cs2j\\triage-r140.json and classifies java.lang.AssertionError
records into sub-groups, extracting Expected/Actual exception pairs.
"""
import json
import re
import collections
import sys

TRIAGE = r"D:\code\cs2j\triage-r140.json"

with open(TRIAGE, "r", encoding="utf-8") as f:
    data = json.load(f)

recs = data["records"]
print(f"total records: {len(recs)}")

assertion = [r for r in recs if r["kind"] == "java.lang.AssertionError"]
print(f"AssertionError records: {len(assertion)}")

# ---- classify by message shape -------------------------------------------
def classify(r):
    m = r["msg"] or ""
    if m.startswith("Assert.throws() failure: No exception thrown"):
        return "throws:no-exception"
    if m.startswith("Assert.throws() failure: Wrong exception type"):
        return "throws:wrong-type"
    if m.startswith("Assert.throws() failure: Exception type was not exact match"):
        return "throws:not-exact"
    if m.startswith("Assert.throwsAny"):
        return "throwsAny"
    if m.startswith("Assert.null() failure"):
        return "null"
    if m.startswith("Assert.notNull() failure"):
        return "notNull"
    if m.startswith("Assert.contains() failure"):
        return "contains"
    if m.startswith("Assert.doesNotContain() failure"):
        return "doesNotContain"
    if m.startswith("Assert.false() failure"):
        return "false"
    if m.startswith("Assert.true() failure"):
        return "true"
    if m.startswith("Assert.equal() failure"):
        return "equal"
    if m.startswith("Assert.notEqual() failure"):
        return "notEqual"
    if m.startswith("Assert.isType() failure"):
        return "isType"
    if m.startswith("Assert.isAssignableFrom() failure"):
        return "isAssignableFrom"
    if m.startswith("Assert.empty() failure"):
        return "empty"
    if m.startswith("Assert.notEmpty() failure"):
        return "notEmpty"
    if m.startswith("Assert.single() failure"):
        return "single"
    if m.startswith("Assert.matches() failure"):
        return "matches"
    if m.startswith("Assert.startsWith() failure"):
        return "startsWith"
    if m.startswith("Assert.endsWith() failure"):
        return "endsWith"
    if m.startswith("Assert.collection() failure"):
        return "collection"
    if m.startswith("Assert.multiple() failure"):
        return "multiple"
    if m.startswith("Assert.all() failure"):
        return "all"
    if m.startswith("Assert.same() failure") or m.startswith("Assert.notSame() failure"):
        return "same"
    if m.startswith("Assert.inRange() failure") or m.startswith("Assert.notInRange() failure"):
        return "inRange"
    if m.startswith("Assert.subset") or m.startswith("Assert.superset") or m.startswith("Assert.proper"):
        return "setops"
    if m.startswith("Assert.equivalent() failure"):
        return "equivalent"
    if m.startswith("Assert.notStrictEqual") or m.startswith("Assert.strictEqual"):
        return "strictEqual"
    if m.startswith("Assert.fail()"):
        return "fail"
    # non-Assert.* assertion-style failures (xunit SDK AssertionException etc.)
    return "other:" + (m.split("\n")[0][:60] if m else "<empty>")

buckets = collections.Counter(classify(r) for r in assertion)
print("\n=== AssertionError sub-buckets ===")
for k, v in buckets.most_common():
    print(f"{v:5d}  {k}")

# ---- extract expected/actual exception pairs ------------------------------
pair_re = re.compile(r"Expected: ([\w\.\$]+)\nActual: ([\w\.\$]+)")
pairs = collections.Counter()
pair_tests = collections.defaultdict(list)
for r in assertion:
    m = pair_re.search(r["msg"] or "")
    if m:
        key = (m.group(1), m.group(2))
        pairs[key] += 1
        pair_tests[key].append(r["test"])
print("\n=== (Expected -> Actual) exception-type pairs ===")
for (e, a), c in pairs.most_common():
    print(f"{c:5d}  {e}  ->  {a}")

# ---- no-exception subgroup ----------------------------------------------
print("\n=== 'No exception thrown' expected types ===")
noexc = [r for r in assertion if classify(r) == "throws:no-exception"]
exp_re = re.compile(r"Expected: ([\w\.\$]+)")
ne = collections.Counter()
for r in noexc:
    m = exp_re.search(r["msg"] or "")
    ne[m.group(1) if m else "<none>"] += 1
for k, v in ne.most_common():
    print(f"{v:5d}  {k}")

print(f"\n=== 'No exception thrown' by test class ===")
nc = collections.Counter(r["cls"].split(".")[-1] for r in noexc)
for k, v in nc.most_common():
    print(f"{v:5d}  {k}")

# ---- wrong type subgroup by test class ----------------------------------
print("\n=== 'Wrong exception type' by test class ===")
wt = [r for r in assertion if classify(r) in ("throws:wrong-type", "throws:not-exact")]
wc = collections.Counter(r["cls"].split(".")[-1] for r in wt)
for k, v in wc.most_common():
    print(f"{v:5d}  {k}")

# ---- full dump of throws-family records ---------------------------------
print("\n=== throws-family records (test | expected -> actual) ===")
for r in assertion:
    c = classify(r)
    if not c.startswith("throws"):
        continue
    m = pair_re.search(r["msg"] or "")
    exp = re.search(r"Expected: ([\w\.\$]+)", r["msg"] or "")
    print(f"{c:22s} {r['test']:110s} exp={exp.group(1) if exp else '?'}"
          + (f" act={m.group(2)}" if m else ""))

# ---- dump all assertion buckets with sample tests -----------------------
print("\n=== samples per bucket ===")
bysample = collections.defaultdict(list)
for r in assertion:
    bysample[classify(r)].append(r["test"])
for k, v in buckets.most_common():
    print(f"\n--- {k} ({len(bysample[k])}) ---")
    for t in bysample[k][:12]:
        print("   ", t)
