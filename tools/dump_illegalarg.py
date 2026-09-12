import json, sys, io

sys.stdout.reconfigure(encoding='utf-8')
data = json.load(open(r'D:\code\cs2j\triage-r140.json', encoding='utf-8'))
recs = data['records']
print('total', data.get('total'))
sel = [r for r in recs if r.get('kind') == 'java.lang.IllegalArgumentException'
       or (r.get('bucket') or '').startswith('IllegalArgument')]
print('selected', len(sel))
for i, r in enumerate(sel):
    print('=' * 100)
    print('## record', i)
    print('test   :', r.get('test'))
    print('outcome:', r.get('outcome'))
    print('kind   :', r.get('kind'))
    print('cls    :', r.get('cls'))
    print('bucket :', r.get('bucket'))
    print('--- msg ---')
    print(r.get('msg'))
    print('--- cause ---')
    print(r.get('cause'))
    print('--- frames ---')
    fr = r.get('frames') or []
    for f in fr:
        print('   ', f)
    print('--- raw keys ---', list(r.keys()))
