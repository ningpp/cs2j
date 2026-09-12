import io
import re

path = r"D:\code\cs2j\mvn-build-r140.log"
BLOCK = re.compile(r"^\[ERROR\] (?P<t>[\w.$]+(?::\d+)?(?:->[\w.$:()]+)?) -- Time elapsed: [\d.]+ s <<< (?P<kind>ERROR|FAILURE)!")
AGG = re.compile(r"^\[ERROR\]   (?P<t>\S+?) » (?P<k>\S+)(?: (?P<m>.*))?$")

n_marker = 0
n_block = 0
n_agg = 0
samples = []
with io.open(path, encoding="utf-8", errors="replace") as fh:
    for raw in fh:
        line = raw.rstrip("\n")
        if "Time elapsed:" in line and "<<<" in line:
            n_marker += 1
            if BLOCK.match(line):
                n_block += 1
            elif len(samples) < 3:
                samples.append(repr(line[:160]))
        if line.startswith("[ERROR]   "):
            n_agg += 1
            if not AGG.match(line) and len(samples) < 6:
                samples.append("AGG:" + repr(line[:160]))

print("marker lines:", n_marker, "matched:", n_block, "agg-ish:", n_agg)
for s in samples:
    print(s)
