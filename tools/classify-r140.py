#!/usr/bin/env python3
"""Dedupe + deep classification of the throws/exception-semantics cluster."""
import json, re, collections

recs = json.load(open(r"D:\code\cs2j\probe\failures-r140.json", encoding="utf-8"))
print("raw parsed:", len(recs))

# dedupe on (test, kind, msg)
seen = {}
for r in recs:
    key = (r["test"], r["kind"], (r["msg"] or "")[:300])
    if key not in seen:
        seen[key] = r
recs = list(seen.values())
print("deduped:", len(recs))

PAIR = re.compile(r"^Expected: (.*?)$", re.M)
ACT = re.compile(r"^Actual: (.*?)$", re.M)


def pair(r):
    e = PAIR.search(r["msg"] or "")
    a = ACT.search(r["msg"] or "")
    return (e.group(1).strip() if e else None, a.group(1).strip() if a else None)


def bucket(r):
    m = (r["msg"] or "").strip()
    if not r["kind"].endswith("AssertionError"):
        return "NON-ASSERT:" + (r["kind"] or "?")
    h = m.split("\n")[0]
    if h.startswith("Assert.throws() failure: No exception thrown"):
        return "A.throws:no-exception"
    if h.startswith("Assert.throws() failure: Wrong exception type"):
        return "A.throws:wrong-type"
    if h.startswith("Assert.throws() failure: Exception type was not exact"):
        return "A.throws:not-exact"
    if h.startswith("Assert.throwsAny"):
        return "A.throwsAny"
    if h.startswith("Assert.null() failure"):
        return "B.null"
    if h.startswith("Assert.contains() failure"):
        return "B.contains"
    if h.startswith("Assert.true() failure") or h.startswith("Assert.false() failure"):
        return "B.truefalse"
    if h.startswith("Assert.same()") or h.startswith("Assert.notSame()"):
        return "B.same"
    if h.startswith("Assert.notNull()"):
        return "B.notNull"
    if h.startswith("Assert.doesNotContain()"):
        return "B.doesNotContain"
    if h.startswith("Assert.equal() failure: Values differ"):
        return "C.equal-values-differ"
    if h.startswith("Assert.equal()"):
        return "C.equal-other"
    if h.startswith("Assert.startsWith()"):
        return "B.startsWith"
    return "Z.other:" + h[:70]


buckets = collections.Counter(bucket(r) for r in recs)
print("\n=== buckets (deduped) ===")
for k, v in buckets.most_common():
    print(f"{v:5d}  {k}")

# ---- throws cluster detail ----------------------------------------------
throws = [r for r in recs if bucket(r).startswith("A.")]
print(f"\n=== throws-family total: {len(throws)} ===")

print("\n--- no-exception: expected type ---")
c = collections.Counter(pair(r)[0] for r in throws if bucket(r) == "A.throws:no-exception")
for k, v in c.most_common():
    print(f"{v:5d}  {k}")

print("\n--- no-exception: by test class ---")
c = collections.Counter(r["test"].split("(")[0].rsplit(".", 1)[0].split(".")[-1]
                        for r in throws if bucket(r) == "A.throws:no-exception")
for k, v in c.most_common():
    print(f"{v:5d}  {k}")

print("\n--- wrong-type: (exp -> act) ---")
c = collections.Counter(pair(r) for r in throws if bucket(r) == "A.throws:wrong-type")
for k, v in c.most_common():
    print(f"{v:5d}  {k[0]}  ->  {k[1]}")

print("\n--- all A.* records ---")
for r in throws:
    e, a = pair(r)
    print(f"{bucket(r):24s} {r['test'][:120]:120s} exp={e} act={a}")

# ---- non-assert failures that mention exception semantics ---------------
print("\n=== NON-ASSERT: UnsupportedOperation / IllegalState / NullPointer records ===")
for r in recs:
    if bucket(r).startswith("NON-ASSERT") and r["kind"] in (
            "java.lang.UnsupportedOperationException", "java.lang.IllegalStateException",
            "java.lang.NullPointerException"):
        print(f"{r['kind']:45s} {r['test'][:110]:110s} | {(r['msg'] or '')[:90]}")
