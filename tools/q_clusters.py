import json, collections, sys, re

d = json.load(open(r'D:\code\cs2j\triage-r140.json'))
rs = d['records']

def show(pred, label, limit=60):
    hits = [r for r in rs if pred(r)]
    print('=' * 8, label, len(hits))
    for r in hits[:limit]:
        cls = r['cls'].split('.')[-1]
        tst = r['test'].split('.')[-1]
        print('  %-46s %-16s %s' % (cls + '.' + tst, r['outcome'], r['bucket']))
        print('      msg  : ' + r['msg'][:190])
        if r['cause']:
            print('      cause: ' + r['cause'][:190])
        for f in r['frames'][:4]:
            print('      at   : ' + f)
    print()

q = sys.argv[1] if len(sys.argv) > 1 else 'all'

if q in ('all', 'q2'):
    show(lambda r: r['bucket'] == 'DictKeyNotSupported', 'Q2 DictKeyNotSupported')
if q in ('all', 'q3'):
    show(lambda r: 'CSharpDictionary -> CSharpIDictionary' in r['bucket'], 'Q3 Cast CSharpDictionary->CSharpIDictionary')
if q in ('all', 'q4'):
    show(lambda r: r['bucket'] == 'NotSupportedType class [[I', 'Q4 [[I unsupported')
if q in ('all', 'q5'):
    show(lambda r: r['bucket'].startswith('ClassCast JsonElement ->'), 'Q5 JsonElement -> primitive')
if q in ('all', 'q6'):
    show(lambda r: r['bucket'].startswith('IllegalArgument') and 'ningpp.compat' in r['bucket'], 'Q6 IllegalArgument reflective')
if q in ('all', 'q6b'):
    show(lambda r: r['kind'] == 'java.lang.IllegalArgumentException', 'Q6b all IllegalArgumentException')
