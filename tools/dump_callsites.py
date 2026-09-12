import json, re, os

BASE = 'd:\\cs-json-0911-sdk\\system-text-json-tests\\src\\test\\java\\'
d = json.load(open(r'D:\code\cs2j\triage-r140.json', encoding='utf-8'))
recs = [r for r in d['records'] if r['bucket'].startswith('ClassCast JsonElement ->') and r['outcome'] == 'ERROR']


def find_file(fq):
    for i in range(len(fq.split('.')), 0, -1):
        parts = fq.split('.')[:i]
        cand = os.path.join(BASE, *parts[:-1], parts[-1] + '.java')
        if os.path.exists(cand):
            return cand
    return None


seen = set()
for r in recs:
    name = r['test'].split('.')[-1]
    if name in seen:
        continue
    seen.add(name)
    frames = r.get('frames') or []
    loc = frames[0] if frames else ''
    cls = loc.split('$')[0].rsplit(':', 1)[0]
    path = find_file(cls)
    if not path or not path.startswith(BASE):
        # fall back to the test class
        path = find_file(r['test'].rsplit('.', 1)[0])
    if not path:
        print('## %s : file not found' % name)
        continue
    L = open(path, encoding='utf-8', errors='replace').read().split('\n')
    start = None
    for i, l in enumerate(L):
        if re.search(r'\b' + re.escape(name) + r'\s*\(', l) and ('void' in l):
            start = i
    if start is None:
        print('## %s : method not found in %s' % (name, path))
        continue
    end = start + 1
    while end < len(L) and not L[end].startswith('    }'):
        end += 1
    print('## %s  (%s:%d)' % (name, os.path.basename(path), start + 1))
    for i in range(start, min(end + 1, start + 45)):
        if 'deserialize' in L[i] or 'serialize(' in L[i]:
            print('   %d: %s' % (i + 1, L[i].strip()[:240]))
    print()
