"""Report each surefire XML's declared tests/failures vs the testcase elements present."""
import glob
import xml.etree.ElementTree as ET

for p in sorted(glob.glob(r"D:\code\cs2j\snapshots\surefire-base-r143\TEST-*.xml")):
    s = ET.parse(p).getroot()
    cases = list(s.iter("testcase"))
    fails = sum(1 for c in cases if c.find("failure") is not None or c.find("error") is not None)
    decl_t, decl_f = s.get("tests"), s.get("failures")
    decl_e = s.get("errors")
    run = int(decl_f or 0) + int(decl_e or 0)
    flag = "" if len(cases) == int(decl_t or 0) else "   <<< testcase count mismatch"
    if flag or len(cases) < int(decl_t or 0):
        print(f"{s.get('name').split('.')[-1]:44} files={len(cases):5} declared={decl_t:>5} "
              f"declFail={run:>4} elemFail={fails:>4}{flag}")
