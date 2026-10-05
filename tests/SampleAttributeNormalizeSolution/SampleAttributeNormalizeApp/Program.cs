using SampleAttributeNormalizeLib;

var log = new CallerLog();

log.Notify("hello from main");

var samples = new CallSiteSamples(log);
_ = samples.Property;
_ = samples[0];
samples.LambdaAndLocalFunction();
new CrossClassCaller().CallAcross(log);

var expected = new (string Member, string FileSuffix, int Line, string Expr)[]
{
    ("$", "SampleAttributeNormalizeApp/Program.cs", 5, "\"hello from main\""),
    (".ctor", "SampleAttributeNormalizeLib/CallSiteSamples.cs", 10, "\"ctor site\""),
    ("Property", "SampleAttributeNormalizeLib/CallSiteSamples.cs", 17, "\"property getter\""),
    ("Item", "SampleAttributeNormalizeLib/CallSiteSamples.cs", 26, "\"indexer getter\""),
    ("LambdaAndLocalFunction", "SampleAttributeNormalizeLib/CallSiteSamples.cs", 33, "\"lambda site\""),
    ("Local", "SampleAttributeNormalizeLib/CallSiteSamples.cs", 36, "\"local function site\""),
    ("CallAcross", "SampleAttributeNormalizeLib/CrossClassCaller.cs", 9, "\"cross-class site\""),
};

if (log.Records.Count != expected.Length)
    throw new Exception($"record count = {log.Records.Count}, expected {expected.Length}");

for (var i = 0; i < expected.Length; i++)
{
    var record = log.Records[i];
    var want = expected[i];

    var normalizedFile = record.File.Replace('\\', '/');
    if (!normalizedFile.EndsWith(want.FileSuffix, StringComparison.Ordinal))
        throw new Exception($"[{i}] file = {record.File}, expected suffix {want.FileSuffix}");
    if (record.Member != want.Member)
        throw new Exception($"[{i}] member = {record.Member}, expected {want.Member}");
    if (record.Line != want.Line)
        throw new Exception($"[{i}] line = {record.Line}, expected {want.Line}");
    if (record.Expr != want.Expr)
        throw new Exception($"[{i}] expr = {record.Expr}, expected {want.Expr}");
}

Console.WriteLine($"AttributeNormalize fixture OK: {log.Records.Count} caller records verified.");
