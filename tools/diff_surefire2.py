import re, sys

def parse(path):
    """Per-test outcome + first frame + error kind from a maven surefire console log."""
    out = {}
    lines = open(path, encoding='utf-16').read().split('\n')
    cur = None
    for i, line in enumerate(lines):
        m = re.search(r'\[ERROR\]\s+(?:[\w.$]+\.)?\w+Tests\.(\w+) -- Time elapsed: [\d.]+ s <<< (ERROR|FAILURE)!', line)
        if m:
            cur = m.group(1)
            out[cur] = [m.group(2), '', '']
            continue
        m2 = re.search(r'\[ERROR\]\s+(?:[\w.$]+\.)?(\w+Tests)\.(\w+):(\d+)\s*[»›]\s*([^$]*)', line)
        if m2:
            name = m2.group(2)
            if name not in out:
                out[name] = ['?', '', '']
            out[name][1] = m2.group(3)
            out[name][2] = m2.group(4).strip()[:110]
            cur = None
            continue
        if cur and line.strip().startswith('at ') and not out[cur][1]:
            mm = re.search(r'\((\w+\.java):(\d+)\)', line)
            if mm and mm.group(1) != 'CollectionTests.java':
                out[cur][1] = mm.group(1) + ':' + mm.group(2)
            elif mm:
                out[cur][1] = mm.group(2)
    return out

base = parse(sys.argv[1])
other = parse(sys.argv[2])
print('parsed base=%d other=%d' % (len(base), len(other)))
fixed = sorted(set(base) - set(other))
broke = sorted(set(other) - set(base))
print('\nFIXED (%d):' % len(fixed))
for k in fixed:
    print('  %-52s was %s | %s @%s' % (k, base[k][0], base[k][2], base[k][1]))
print('\nBROKEN (%d):' % len(broke))
for k in broke:
    print('  %-52s now %s | %s @%s' % (k, other[k][0], other[k][2], other[k][1]))
print('\nSIGNATURE CHANGED (%d):' % len([k for k in set(base) & set(other) if base[k][2] != other[k][2]]))
for k in sorted(set(base) & set(other)):
    if base[k][2] != other[k][2]:
        print('  %-52s\n      was %s\n      now %s' % (k, base[k][2], other[k][2]))

