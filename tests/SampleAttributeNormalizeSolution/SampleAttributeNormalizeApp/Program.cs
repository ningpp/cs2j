using System.Text.Json;
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
    ("$", "SampleAttributeNormalizeApp/Program.cs", 6, "\"hello from main\""),
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

// ── STJ 往返：JsonPropertyName 改名生效；WhenWritingDefault 的 null 被忽略 ──
var stj = new StjPerson
{
    Id = 7,
    Secret = null,
    Score = 12,
};
var stjJson = JsonSerializer.Serialize(stj);
if (!stjJson.Contains("\"id\":7"))
    throw new Exception($"STJ serialize missing renamed id: {stjJson}");
if (stjJson.Contains("Secret", StringComparison.Ordinal))
    throw new Exception($"STJ serialize should ignore default Secret: {stjJson}");
// AllowReadingFromString：字符串形式数字也能读入 int Score。
var stjBack = JsonSerializer.Deserialize<StjPerson>("{\"id\":7,\"Score\":\"9\"}")
    ?? throw new Exception("STJ deserialize returned null");
if (stjBack.Id != 7 || stjBack.Score != 9)
    throw new Exception("STJ deserialize roundtrip mismatch");

// ── Newtonsoft 往返：JsonProperty 改名生效；JsonIgnore 生效 ──
var nsJson = Newtonsoft.Json.JsonConvert.SerializeObject(new NewtonsoftPerson
{
    Id = 9,
    Secret = "hidden",
});
if (!nsJson.Contains("person_id"))
    throw new Exception($"Newtonsoft serialize missing renamed property: {nsJson}");
if (nsJson.Contains("Secret", StringComparison.Ordinal))
    throw new Exception($"Newtonsoft serialize should ignore Secret: {nsJson}");
var nsBack = Newtonsoft.Json.JsonConvert.DeserializeObject<NewtonsoftPerson>("{\"person_id\":11}")
    ?? throw new Exception("Newtonsoft deserialize returned null");
if (nsBack.Id != 11)
    throw new Exception("Newtonsoft deserialize roundtrip mismatch");

Console.WriteLine("json roundtrip verified (STJ + Newtonsoft).");
