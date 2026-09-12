import json, sys

d = json.load(open(r'D:\code\cs2j\triage-r140.json'))
rs = d['records']

def dump(pred, label, maxframes=40):
    hits = [r for r in rs if pred(r)]
    print('=' * 10, label, len(hits))
    for r in hits:
        print('---', r['cls'] + '.' + r['test'], r['outcome'], r['kind'])
        print('msg   :', r['msg'])
        print('cause :', r['cause'])
        for i, f in enumerate(r['frames'][:maxframes]):
            print('   %2d %s' % (i, f))
        print()

q = sys.argv[1] if len(sys.argv) > 1 else 'q2'
if q == 'q2':
    dump(lambda r: r['bucket'] == 'DictKeyNotSupported' and r['outcome'] == 'ERROR',
         'Q2 DictKeyNotSupported (full frames)')
elif q == 'q3':
    dump(lambda r: 'CSharpDictionary -> CSharpIDictionary' in r['bucket'] and r['outcome'] == 'ERROR',
         'Q3 (full frames)')
elif q == 'q6':
    dump(lambda r: r['bucket'].startswith('IllegalArgument') and 'ningpp.compat' in r['bucket'],
         'Q6 (full frames)')
elif q == 'q4':
    dump(lambda r: r['bucket'] == 'NotSupportedType class [[I' and r['outcome'] == 'ERROR',
         'Q4 (full frames)')
elif q == 'q1':
    dump(lambda r: 'AbstractCollection' in r['bucket'] and r['outcome'] == 'ERROR',
         'Q1 (full frames)', 24)
elif q == 'jsonel':
    dump(lambda r: r['bucket'] == 'ClassCast JsonElement -> Integer',
         'Q5 JsonElement->Integer (full frames)', 20)
