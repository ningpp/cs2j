import json, sys, collections

sys.stdout.reconfigure(encoding='utf-8')
d = json.load(open(r'D:\code\cs2j\triage-r140.json', encoding='utf-8'))
rs = d['records']
print('total records:', len(rs))

def msg(r): return r.get('msg') or ''

fam = [r for r in rs if msg(r).startswith('public ')]
print('\n-- records whose msg is the compat MethodInfo.invoke wrapper ("public <ret> <decl>.<name>(...) target=...") --')
print('count:', len(fam))
for r in fam:
    print('  outcome=%-5s cls=%-28s %s' % (r.get('outcome'), r.get('cls'), msg(r)[:120]))

print('\n-- bucket split (ERROR records only) --')
b = collections.Counter()
for r in rs:
    if r.get('kind') == 'java.lang.IllegalArgumentException' and msg(r).startswith('public '):
        b[msg(r).split()[1]] += 1
for k, v in b.items():
    print('  %-55s %d' % (k, v))

print('\n-- stack-frame count for the throw site --')
n110 = sum(1 for r in rs if any('MethodInfo.invoke:110' in f for f in (r.get('frames') or [])))
n100 = sum(1 for r in rs if any('MethodInfo.invoke:100' in f for f in (r.get('frames') or [])))
print('  frames contain MethodInfo.invoke:110 ->', n110, ' (records with frames):', sum(1 for r in rs if r.get('frames')))
print('  frames contain MethodInfo.invoke:100 ->', n100)

print('\n-- per-class counts of the wrapper family (ERROR+AGG) --')
for k, v in collections.Counter(r.get('cls') for r in fam).items():
    print('  %-28s %d' % (k, v))
