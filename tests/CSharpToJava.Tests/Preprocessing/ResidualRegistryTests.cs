using CSharpToJava.Core.Normalization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace CSharpToJava.Tests.Preprocessing;

/// <summary>
/// M0-a：NORM 残留契约与 7 个只读探针的注册表契约、正负样例与扫描顺序。
/// </summary>
public sealed class ResidualRegistryTests
{
    private const string RelativePath = "src/Sample.cs";

    private static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.Preview);

    private static readonly MetadataReference[] References = BuildReferences();

    private static MetadataReference[] BuildReferences()
    {
        var trusted = (string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES");
        if (trusted is null)
            return new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) };

        return trusted.Split(Path.PathSeparator)
            .Where(File.Exists)
            .Select(path => MetadataReference.CreateFromFile(path))
            .ToArray();
    }

    private static IReadOnlyList<ResidualHit> Scan(string source)
    {
        var tree = CSharpSyntaxTree.ParseText(source, ParseOptions);
        var compilation = CSharpCompilation.Create(
            "residual-registry-test-" + Guid.NewGuid().ToString("N"),
            new[] { tree },
            References,
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                allowUnsafe: true));
        var model = compilation.GetSemanticModel(tree);

        return ResidualRegistry.CreateDefault()
            .Scan(model, RelativePath)
            .ToList();
    }

    [Fact]
    public void CreateDefault_Registers_Seven_Unique_Residual_Codes()
    {
        var registry = ResidualRegistry.CreateDefault();

        Assert.Equal(7, registry.Descriptors.Count);
        Assert.Equal(7, registry.Probes.Count);
        Assert.Equal(
            new[]
            {
                "NORM-ENUM-ARITH",
                "NORM-STACKALLOC",
                "NORM-REF-STRUCT",
                "NORM-REF-LOCAL",
                "NORM-ASYNC-LAMBDA",
                "NORM-TUPLE-LOCAL",
                "NORM-POINTER-OP",
            },
            registry.Descriptors.Select(d => d.Code).ToArray());
        Assert.Equal(
            registry.Descriptors.Count,
            registry.Descriptors.Select(d => d.Code).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(
            registry.Descriptors.Select(d => d.Code).ToArray(),
            registry.Probes.Select(p => p.Code).ToArray());
        Assert.All(registry.Descriptors, descriptor =>
        {
            Assert.False(string.IsNullOrWhiteSpace(descriptor.OwnerPassId));
            Assert.False(string.IsNullOrWhiteSpace(descriptor.JavaGate));
            Assert.Equal(ResidualPolicy.Fail, descriptor.Policy);
        });
    }

    [Theory]
    [InlineData("NORM-ENUM-ARITH",
        "enum E { A } class C { bool M(E a, E b) => (a - b) > 0; }")]
    [InlineData("NORM-ENUM-ARITH",
        "enum E { A } class C { bool M(E a) => a == 0; }")]
    [InlineData("NORM-STACKALLOC",
        "class C { void M(int n) { System.Span<byte> s = stackalloc byte[n]; } }")]
    [InlineData("NORM-REF-STRUCT",
        "ref struct R { }")]
    [InlineData("NORM-REF-LOCAL",
        "class C { void M() { int x = 1; ref int r = ref x; } }")]
    [InlineData("NORM-ASYNC-LAMBDA",
        "class C { void M() { System.Func<System.Threading.Tasks.Task> f = async () => await System.Threading.Tasks.Task.CompletedTask; } }")]
    [InlineData("NORM-TUPLE-LOCAL",
        "class C { void M() { (int, int) v = default; } }")]
    [InlineData("NORM-POINTER-OP",
        "unsafe class C { int M(char* p) => *p; }")]
    public void Probe_Reports_Single_Hit_With_OneBased_Location(string code, string source)
    {
        var hit = Assert.Single(Scan(source));

        Assert.Equal(code, hit.Code);
        Assert.Equal(RelativePath, hit.FilePath);
        Assert.Equal(1, hit.Line);
        Assert.True(hit.Column >= 1);
        Assert.False(string.IsNullOrWhiteSpace(hit.Snippet));
        Assert.True(hit.Snippet.Length <= 120);
    }

    [Theory]
    [InlineData("class C { void M() { System.Action a = () => { }; } }")]
    [InlineData("class C { bool M(int a, int b) => (a - b) > 0; }")]
    [InlineData("enum E { A } class C { bool M(E a, E b) => a == b; }")]
    [InlineData("struct S { }")]
    [InlineData("class C { void M() { int x = 1; } }")]
    [InlineData("class C { void M() { string s = string.Empty; } }")]
    [InlineData("class C { int M(int x) => -x; }")]
    public void Probes_Report_No_Hits_For_Non_Residual_Shapes(string source)
    {
        Assert.Empty(Scan(source));
    }

    [Fact]
    public void Scan_Returns_Hits_In_Stable_Probe_Order_With_Expected_Lines()
    {
        const string source = """
            ref struct R { }
            enum E { A }
            class C
            {
                unsafe void M(char* p, int n, E a, E b)
                {
                    System.Span<byte> s = stackalloc byte[n];
                    ref int r = ref n;
                    (int, int) v = default;
                    bool q = (a - b) > 0;
                    int x = *p;
                    System.Func<System.Threading.Tasks.Task> f = async () => await System.Threading.Tasks.Task.CompletedTask;
                }
            }
            """;

        var hits = Scan(source);

        Assert.Equal(
            new (string Code, int Line)[]
            {
                ("NORM-ENUM-ARITH", 10),
                ("NORM-STACKALLOC", 7),
                ("NORM-REF-STRUCT", 1),
                ("NORM-REF-LOCAL", 8),
                ("NORM-ASYNC-LAMBDA", 12),
                ("NORM-TUPLE-LOCAL", 9),
                ("NORM-POINTER-OP", 11),
            },
            hits.Select(h => (h.Code, h.Line)).ToArray());
    }
}
