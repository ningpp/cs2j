#!/usr/bin/env python3
import json, re, collections
with open(r"D:\code\cs2j\triage-r140.json", "r", encoding="utf-8") as f:
    data = json.load(f)
recs = data["records"]
assertion = [r for r in recs if r["kind"] == "java.lang.AssertionError"]
for r in assertion:
    if (r["msg"] or "").startswith("Assert.throws() failure: Wrong exception type"):
        print(repr(r["test"]))
        print(repr(r["msg"]))
        print("---")
