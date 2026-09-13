"""Print the s_json literal around a byte offset, to decode reported BytePositionInLine.

Usage: python tools/s_json_at.py <SimpleTestClass.java> <offset> [span]
"""
import re
import sys

src = open(sys.argv[1], encoding="utf-8").read()
offset = int(sys.argv[2])
span = int(sys.argv[3]) if len(sys.argv) > 3 else 40
match = re.search(r'public static final String s_json = "(.*?)";', src, re.S)
text = match.group(1).encode().decode("unicode_escape")
print("length:", len(text))
print("around %d: %r" % (offset, text[max(0, offset - span):offset + span]))
for name in ("MyChar", "MyDateTime", "MyEnum", "MyGuid", "MyUri"):
    index = text.find(name)
    print("%-12s at %s" % (name, index))
