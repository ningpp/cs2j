import json, collections, re, sys

d = json.load(open(r'D:\code\cs2j\triage-r140.json', encoding='utf-8'))
r = [x for x in d['records'] if x.get('outcome') != 'AGG']
print('non-agg records', len(r))

def sig(x):
    msg = x['msg']
    cause = x['cause']
    frames = x['frames']
    b = x['bucket']
    if 'cannot be cast to class java.lang.String' in msg:
        return 'A CCE->String  (element converter is StringConverter)'
    if 'JsonElement' in msg and 'cannot be cast' in msg:
        return 'D JsonElement->X (element type erased to Object)'
    if b == 'JsonConvert ':
        if 'as a string' in cause or 'as a string' in msg:
            return 'C JsonException: read with StringConverter'
        if 'as a number' in cause:
            return 'C JsonException: read with number converter'
        if 'StartObject' in cause or 'StartArray' in cause:
            return 'C JsonException: token shape mismatch (wrong converter)'
        return 'C JsonException: other'
    if 'could not be instantiated and populated' in msg:
        return 'E collection could not be instantiated/populated'
    if 'UnsupportedOperation' in x['kind']:
        return 'E UnsupportedOperation other'
    if 'ClassCast' in x['kind']:
        return 'B other ClassCast: ' + msg[:60]
    return 'Z other: ' + b[:60]

c = collections.Counter(sig(x) for x in r)
tot = 0
for k, v in c.most_common(40):
    print('%5d  %s' % (v, k))
    tot += v
print('total', tot)

print()
print('=== A-bucket breakdown by frames ===')
a = [x for x in r if sig(x).startswith('A ')]
print(len(a))
fc = collections.Counter()
for x in a:
    for f in x['frames'][:6]:
        fc[f] += 1
for k, v in fc.most_common(25):
    print('%5d  %s' % (v, k))
