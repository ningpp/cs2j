import json, collections

d = json.load(open(r'D:\code\cs2j\triage-r140.json'))
rs = d['records']

def tally(pred, label):
    hits = [r for r in rs if pred(r)]
    err = [r for r in hits if r['outcome'] != 'AGG']
    agg = [r for r in hits if r['outcome'] == 'AGG']
    print('%-46s rows=%-3d real=%-3d agg=%-3d tests=%s' % (
        label, len(hits), len(err), len(agg),
        ','.join(sorted({r['cls'].split('.')[-1] + '.' + r['test'].split('.')[-1] for r in err}))))

tally(lambda r: 'AbstractCollection' in r['bucket'], 'Q1 CannotPopulateCollection')
for t in ('CSharpObjStack', 'CSharpObjQueue', 'CSharpArrayList', 'WrapperForIList', 'StringHashSetWrapper'):
    tally(lambda r, t=t: 'AbstractCollection' in r['bucket'] and r['bucket'].endswith(t), '  Q1.' + t)
tally(lambda r: r['bucket'] == 'DictKeyNotSupported', 'Q2 DictKeyNotSupported')
tally(lambda r: 'CSharpDictionary -> CSharpIDictionary' in r['bucket'], 'Q3 dict->CSharpIDictionary cast')
tally(lambda r: r['bucket'] == 'NotSupportedType class [[I', 'Q4 [[I not supported')
tally(lambda r: r['bucket'].startswith('ClassCast JsonElement ->'), 'Q5 JsonElement -> X')
tally(lambda r: r['bucket'].startswith('IllegalArgument') and 'ningpp.compat' in r['bucket'], 'Q6 IllegalArgument reflective')
print()
print('byClass for Q1+Q2+Q3:')
c = collections.Counter()
for r in rs:
    if 'AbstractCollection' in r['bucket'] or r['bucket'] == 'DictKeyNotSupported' \
       or 'CSharpDictionary -> CSharpIDictionary' in r['bucket']:
        c[r['cls'].split('.')[-1]] += 1
for k, v in c.most_common():
    print('  %-30s %d' % (k, v))
