import json, re, os, collections

TRIAGE = r'D:\code\cs2j\triage-r140.json'
BASE = 'd:\\cs-json-0911-sdk\\system-text-json-tests\\src\\test\\java\\'

d = json.load(open(TRIAGE, encoding='utf-8'))
recs = [r for r in d['records'] if r['bucket'].startswith('ClassCast JsonElement ->')]
print('records in buckets starting with "ClassCast JsonElement ->":', len(recs))
print('by bucket:', dict(collections.Counter(r['bucket'] for r in recs)))
print()

def src_line(test, loc):
    m = re.match(r'^(.*?):(\d+)$', loc or '')
    if not m:
        return None, None, 'NO-FRAME'
    fq, ln = m.group(1), int(m.group(2))
    pkgcls = fq.split('$')[0]
    parts = pkgcls.split('.')
    # package is everything up to the class name: heuristic - find the file
    for i in range(len(parts), 0, -1):
        cand = os.path.join(BASE, *parts[:i-1], parts[i-1] + '.java')
        if os.path.exists(cand):
            L = open(cand, encoding='utf-8', errors='replace').read().split('\n')
            line = L[ln-1].strip() if ln-1 < len(L) else '??'
            return cand, ln, line
    return None, ln, 'FILE-NOT-FOUND:' + pkgcls

for r in recs:
    frames = r.get('frames') or []
    loc = frames[0] if frames else ''
    f, ln, line = src_line(r['test'], loc)
    kind = 'AGG' if r['outcome'] == 'AGG' else 'ERR'
    print('[%s] %-52s %-34s' % (kind, r['bucket'].replace('ClassCast JsonElement -> ', '-> '), r['test'].split('.')[-1]))
    print('        at %s' % loc)
    print('        src: %s' % line[:200])
