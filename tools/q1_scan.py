import re, sys
p = r'D:\code\cs2j\java\csharptojava-compat\src\main\java\io\github\ningpp\compat\TypeHelper.java'
s = open(p, encoding='utf-8').read().split('\n')
pat = re.compile(sys.argv[1] if len(sys.argv) > 1 else r'newNonGenericCollection')
for i, l in enumerate(s, 1):
    if pat.search(l):
        print(i, l)
