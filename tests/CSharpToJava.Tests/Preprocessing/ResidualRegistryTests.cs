using CSharpToJava.Core.Normalization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace CSharpToJava.Tests.Preprocessing;

/// <summary>
/// M0-a：NORM 残留契约与 10 个只读探针的注册表契约、正负样例与扫描顺序。
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
    public void CreateDefault_Registers_Ten_Unique_Residual_Codes()
    {
        var registry = ResidualRegistry.CreateDefault();

        Assert.Equal(10, registry.Descriptors.Count);
        Assert.Equal(10, registry.Probes.Count);
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
                "NORM-POINTER-LOCAL",
                "NORM-UNSAFE-BLOCK",
                "NORM-FIXED",
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
        // NORM-REF-LOCAL is owned by the dedicated unsafe-ref-lowering ABI pass.
        Assert.Equal(
            "unsafe-ref-lowering",
            registry.Descriptors.Single(d => d.Code == "NORM-REF-LOCAL").OwnerPassId);
        // NORM-REF-STRUCT is owned by the dedicated ref-struct-lowering pass.
        Assert.Equal(
            "ref-struct-lowering",
            registry.Descriptors.Single(d => d.Code == "NORM-REF-STRUCT").OwnerPassId);
        // Hard unsafe/fixed/pointer declarations are owned by unsafe-ffm.
        Assert.Equal(
            "unsafe-ffm",
            registry.Descriptors.Single(d => d.Code == "NORM-POINTER-LOCAL").OwnerPassId);
        Assert.Equal(
            "unsafe-ffm",
            registry.Descriptors.Single(d => d.Code == "NORM-UNSAFE-BLOCK").OwnerPassId);
        Assert.Equal(
            "unsafe-ffm",
            registry.Descriptors.Single(d => d.Code == "NORM-FIXED").OwnerPassId);
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
    [InlineData("NORM-POINTER-LOCAL",
        "unsafe class C { void M() { int* p = null; } }")]
    [InlineData("NORM-POINTER-LOCAL",
        "unsafe class C { void M(int* q) { var p = q; } }")]
    [InlineData("NORM-UNSAFE-BLOCK",
        "unsafe class C { void M() { unsafe { } } }")]
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

    [Fact]
    public void Fixed_Statement_Reports_Fixed_And_Pointer_Local_Hits()
    {
        const string source = "unsafe class C { void M(int[] a) { fixed (int* p = a) { } } }";

        var hits = Scan(source);

        Assert.Equal(2, hits.Count);
        Assert.Equal(
            new[] { "NORM-POINTER-LOCAL", "NORM-FIXED" },
            hits.Select(h => h.Code).ToArray());
        Assert.All(hits, hit => Assert.Equal(1, hit.Line));
        var pointerHit = Assert.Single(hits, h => h.Code == "NORM-POINTER-LOCAL");
        Assert.Contains("'p'", pointerHit.Reason);
    }

    [Theory]
    [InlineData("class C { void M() { System.Action a = () => { }; } }")]
    [InlineData("class C { bool M(int a, int b) => (a - b) > 0; }")]
    [InlineData("enum E { A } class C { bool M(E a, E b) => a == b; }")]
    // 可空枚举与同类枚举成员比较：enum pass 归一化后的形态（提升相等语义与
    // 常量模式一致，null 分别得 false/true），Java 引用相等直译合法。
    [InlineData("enum E { A } class C { bool M(E? a) => a == E.A; }")]
    [InlineData("enum E { A } class C { bool M(E? a) => a != E.A; }")]
    // 可空枚举判空：Java 引用判空直译，不属于 enum 操作数残留。
    [InlineData("enum E { A } class C { bool M(E? a) => a != null; }")]
    [InlineData("struct S { }")]
    [InlineData("class C { void M() { int x = 1; } }")]
    [InlineData("class C { void M() { string s = string.Empty; } }")]
    [InlineData("class C { int M(int x) => -x; }")]
    [InlineData("unsafe class C { void M() { int x = 1; } }")]
    [InlineData("class C { void M() { using (var s = new System.IO.MemoryStream()) { } } }")]
    [InlineData("class C { void M(int[] a) { foreach (var x in a) { } } }")]
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
                    int* lp = null;
                    unsafe { }
                    fixed (int* fp = null) { }
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
                ("NORM-POINTER-LOCAL", 13),
                ("NORM-POINTER-LOCAL", 15),
                ("NORM-UNSAFE-BLOCK", 14),
                ("NORM-FIXED", 15),
            },
            hits.Select(h => (h.Code, h.Line)).ToArray());
    }
}
