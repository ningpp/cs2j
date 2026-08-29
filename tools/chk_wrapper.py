import glob
for f in glob.glob(r'D:\cs-java-829-verify\*\target\surefire-reports\TEST-*.txt'):
    txt = open(f, encoding='utf-8', errors='replace').read()
    if 'ListWrapper' in txt and ('<<< ERROR' in txt or '<<< FAILURE' in txt):
        for line in txt.splitlines():
            if 'ListWrapper' in line and '<<<' in line:
                name = f.split(chr(92))[-1]
                print(name, '|', line[:90])
