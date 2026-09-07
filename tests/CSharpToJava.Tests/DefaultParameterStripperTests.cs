using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using CSharpToJava.Core.DefaultParameterStripper;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit.Abstractions;

namespace CSharpToJava.Tests;

public class DefaultParameterStripperTests
{
    private readonly ITestOutputHelper _output;
    public DefaultParameterStripperTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void Options_Default_AllEnabled()
    {
        var o = new DefaultParameterStripperOptions();
        Assert.True(o.GenerateOverloads);
        Assert.False(o.Verbose);
        Assert.False(o.Strict);
    }

    [Fact]
    public void Statistics_StartsZero()
    {
        var s = new DefaultParameterStripperStatistics();
        Assert.Equal(0, s.FilesTransformed);
        Assert.Equal(0, s.OverloadsGenerated);
    }

    [Fact]
    public void Strip_NoDefaults_ReturnsUnchanged()
    {
        var result = Strip("class C { public void M(int a) { } }");
        Assert.False(result.Changed);
        Assert.Equal(1, result.Statistics.FilesSkippedClean);
    }

    [Fact]
    public void Strip_SingleDefault_StripsAndGeneratesOverload()
    {
        var result = Strip("""class C { public void M(int a, string b = "x") { } }""");
        Assert.True(result.Changed);
        Assert.Equal(1, result.Statistics.DeclarationsStripped);
        Assert.Equal(1, result.Statistics.ParametersStripped);
        Assert.Equal(1, result.Statistics.OverloadsGenerated);
        Assert.DoesNotContain("= \"x\"", result.OutputCode);
        Assert.Contains("public void M(int a)", result.OutputCode);
        Assert.Contains("M(a, \"x\")", result.OutputCode);
        _output.WriteLine(result.OutputCode);
    }

    [Fact]
    public void Strip_StaticMethod_GeneratesOverload()
    {
        var result = Strip("class C { public static int M(int a, int b = 10) { return a + b; } }");
        Assert.True(result.Changed);
        Assert.Contains("public static int M(int a)", result.OutputCode);
        Assert.Contains("return M(a, 10)", result.OutputCode);
    }

    [Fact]
    public void Strip_AbstractMethod_GeneratesConcreteOverload()
    {
        var result = Strip("""abstract class C { public abstract void M(int a, string b = "x"); }""");
        Assert.True(result.Changed);
        Assert.Contains("public void M(int a)", result.OutputCode);
        Assert.Contains("M(a, \"x\")", result.OutputCode);
    }

    [Fact]
    public void Strip_InterfaceMethod_GeneratesDimOverload()
    {
        var result = Strip("""interface I { void M(int a, string b = "x"); }""");
        Assert.True(result.Changed);
        Assert.Contains("void M(int a) => M(a, \"x\")", result.OutputCode);
    }

    [Fact]
    public void Strip_DelegateDeclaration_StripsDefaults()
    {
        var result = Strip("delegate void D(int a = 1);");
        Assert.True(result.Changed);
        Assert.Equal(1, result.Statistics.DeclarationsStripped);
        Assert.Equal(1, result.Statistics.ParametersStripped);
        Assert.DoesNotContain("= 1", result.OutputCode);
    }

    [Fact]
    public void Strip_PartialMethod_StripsDefaults()
    {
        var result = Strip("partial class C { partial void M(int a = 1); }");
        Assert.True(result.Changed);
        Assert.DoesNotContain("= 1", result.OutputCode);
    }

    [Fact]
    public void Strip_OverrideMethod_StripsButNoOverload()
    {
        var src = """
        class B { public virtual void M(int a, string b = "x") { } }
        class D : B { public override void M(int a, string b = "y") { } }
        """;
        var result = Strip(src);
        Assert.True(result.Changed);
        Assert.DoesNotContain("= \"x\"", result.OutputCode);
        Assert.DoesNotContain("= \"y\"", result.OutputCode);
        // Base class should generate overload, override should not
        Assert.Contains("public void M(int a)", result.OutputCode);
    }

    [Fact]
    public void Strip_DefaultExpressionVerbatimCopy()
    {
        var src = """
        enum E { A, B }
        class C { public void M(int a, E b = E.A, string c = null, int d = -1) { } }
        """;
        var result = Strip(src);
        Assert.True(result.Changed);
        Assert.Contains("M(a, E.A, null, -1)", result.OutputCode);
    }

    [Fact]
    public void Strip_OutputIsSyntacticallyValid()
    {
        var src = """class C { public void M(int a, string b = "x", bool c = true) { } }""";
        var result = Strip(src);
        var tree = CSharpSyntaxTree.ParseText(result.OutputCode);
        var errors = tree.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        Assert.True(errors.Count == 0,
            "Output should be valid C#. Errors:\n" + string.Join("\n", errors));
    }

    // ------------------------------------------------------------------
    // Multiple default parameters — caller completion + overload generation
    // ------------------------------------------------------------------

    [Fact]
    public void Strip_MultipleDefaults_CompletesCallersAndGeneratesOverloads()
    {
        var src = """
            class C
            {
                public int Add(int a, int b = 1, int c = 2) => a + b + c;
                public int One() => Add(10);
                public int Two() => Add(10, 20);
                public int All() => Add(10, 20, 30);
            }
            """;
        var result = Strip(src);
        Assert.True(result.Changed);
        Assert.Equal(1, result.Statistics.DeclarationsStripped);
        Assert.Equal(2, result.Statistics.ParametersStripped);

        // Declaration stripped
        Assert.DoesNotContain("= 1", result.OutputCode);
        Assert.DoesNotContain("= 2", result.OutputCode);

        // Callers completed with the declared defaults as named arguments
        Assert.Contains("Add(10, b: 1, c: 2)", result.OutputCode);
        Assert.Contains("Add(10, 20, c: 2)", result.OutputCode);
        Assert.Contains("Add(10, 20, 30)", result.OutputCode);

        // Forwarding overloads for every default position
        Assert.Contains("public int Add(int a)", result.OutputCode);
        Assert.Contains("return Add(a, 1, 2)", result.OutputCode);
        Assert.Contains("public int Add(int a, int b)", result.OutputCode);
        Assert.Contains("return Add(a, b, 2)", result.OutputCode);

        AssertCompileClean(result.OutputCode);
    }

    [Fact]
    public void Strip_MultipleDefaults_NamedArgumentSkippingMiddle_CompletesMissingDefaults()
    {
        var src = """
            class C
            {
                public int Add(int a, int b = 1, int c = 2, int d = 3) => a + b + c + d;
                public int SkipMiddle() => Add(10, c: 30);
                public int SkipTail() => Add(10, d: 40);
            }
            """;
        var result = Strip(src);
        Assert.True(result.Changed);
        Assert.Contains("Add(10, b: 1, c: 30, d: 3)", result.OutputCode);
        Assert.Contains("Add(10, b: 1, c: 2, d: 40)", result.OutputCode);
        AssertCompileClean(result.OutputCode);
    }

    /// <summary>
    /// A method may mix defaulted parameters with parameters that have no
    /// syntactic default: required parameters before the defaults, an
    /// <c>[Optional]</c>-attributed parameter (optional without a default
    /// expression) between them, and a trailing <c>params</c> array.
    /// </summary>
    [Fact]
    public void Strip_MultipleDefaults_WithNonDefaultParametersMixedIn()
    {
        var src = """
            using System.Runtime.InteropServices;
            class C
            {
                public int Combine(int required, int b = 1, [Optional] object token, int d = 3) => required + b + d;
                public int Positional() => Combine(9, 8, 6);
                public int SkipOptional() => Combine(9, 8, d: 6);
            }
            """;
        var result = Strip(src);
        Assert.True(result.Changed);
        Assert.DoesNotContain("= 1", result.OutputCode);
        Assert.DoesNotContain("= 3", result.OutputCode);

        // Positional arguments bind in order; only the trailing default is filled.
        Assert.Contains("Combine(9, 8, 6, d: 3)", result.OutputCode);
        // An [Optional] parameter skipped by a named argument is completed
        // with `default`, so every argument of the stripped signature is supplied.
        Assert.Contains("Combine(9, 8, token: default, d: 6)", result.OutputCode);
        AssertCompileClean(result.OutputCode);
    }

    [Fact]
    public void Strip_MultipleDefaults_ParamsArrayTail_CompletesCallers()
    {
        var src = """
            class C
            {
                public void Log(string message, int level = 0, params object[] args) { }
                public void None() => Log("x");
                public void LevelOnly() => Log("x", 3);
                public void Full() => Log("x", 3, new object(), 4);
            }
            """;
        var result = Strip(src);
        Assert.True(result.Changed);
        // The params parameter may stay omitted; the defaulted one is completed.
        Assert.Contains("Log(\"x\", level: 0)", result.OutputCode);
        Assert.Contains("Log(\"x\", 3)", result.OutputCode);
        Assert.Contains("Log(\"x\", 3, new object(), 4)", result.OutputCode);
        AssertCompileClean(result.OutputCode);
    }

    // ------------------------------------------------------------------
    // [DefaultParameterValue] metadata attribute rendering (CS1908 guards)
    // ------------------------------------------------------------------

    [Fact]
    public void Strip_MetadataAttribute_NullableEnumNullDefault_DropsAttribute()
    {
        var src = """
            enum E { A, B }
            class C { public void M(E? e = null) { } }
            """;
        var result = Strip(src);
        Assert.True(result.Changed);
        Assert.DoesNotContain("= null", result.OutputCode);
        // [DefaultParameterValue(null)] on a nullable value type is rejected
        // with CS1908 and (E?)null with CS0182 — no legal spelling exists.
        Assert.DoesNotContain("DefaultParameterValue", result.OutputCode);
        AssertCompileClean(result.OutputCode);
    }

    [Fact]
    public void Strip_MetadataAttribute_ByteDefault_RendersExactTypeCast()
    {
        var result = Strip("class C { public void M(byte b = default) { } }");
        Assert.True(result.Changed);
        // An unsuffixed `0` is an int and is rejected for a byte parameter
        // (CS1908); the exact-type cast is required.
        Assert.Contains("DefaultParameterValue((byte)0)", result.OutputCode);
        AssertCompileClean(result.OutputCode);
    }

    [Fact]
    public void Strip_MetadataAttribute_IntegralLiteralDefaults_RenderExactTypeCasts()
    {
        var src = """
            class C
            {
                public void M(
                    byte b = 5,
                    sbyte sb = -1,
                    short sh = -3,
                    ushort us = 7,
                    uint ui = 9,
                    ulong ul = 11,
                    long l = 5,
                    int i = 6) { }
            }
            """;
        var result = Strip(src);
        Assert.True(result.Changed);
        Assert.Contains("DefaultParameterValue((byte)5)", result.OutputCode);
        Assert.Contains("DefaultParameterValue((sbyte)-1)", result.OutputCode);
        Assert.Contains("DefaultParameterValue((short)-3)", result.OutputCode);
        Assert.Contains("DefaultParameterValue((ushort)7)", result.OutputCode);
        Assert.Contains("DefaultParameterValue((uint)9)", result.OutputCode);
        Assert.Contains("DefaultParameterValue((ulong)11)", result.OutputCode);
        // int and long accept int literals through standard implicit conversions
        Assert.Contains("DefaultParameterValue(5)", result.OutputCode);
        Assert.Contains("DefaultParameterValue(6)", result.OutputCode);
        AssertCompileClean(result.OutputCode);
    }

    /// <summary>
    /// Regression test for the csharp-sdk build failure: nullable-enum null
    /// defaults and byte defaults used to render attribute arguments the
    /// compiler rejects (CS1908).
    /// </summary>
    [Fact]
    public void Strip_Output_Compiles_NullableEnumAndByteDefaults()
    {
        var src = """
            enum JsonNumberHandling { Strict = 0, Allow = 1 }
            enum JsonIgnoreCondition { Never = 0, Always = 1 }
            class C
            {
                public static int CreateProperty(
                    System.Type declaredPropertyType,
                    JsonNumberHandling? parentTypeNumberHandling = null,
                    JsonIgnoreCondition? ignoreCondition = null,
                    byte nextByte = default)
                    => 0;
                public int Call() => CreateProperty(typeof(int));
            }
            """;
        var result = Strip(src);
        Assert.True(result.Changed);
        Assert.DoesNotContain("DefaultParameterValue(null)", result.OutputCode);
        AssertCompileClean(result.OutputCode);
    }

    // ------------------------------------------------------------------
    // JsonConstructor attribute — ensures System.Text.Json can still
    // select the right constructor for deserialization after strip-default
    // generates forwarding overloads.
    // ------------------------------------------------------------------

    /// <summary>
    /// When a class has a single parameterized constructor with a default
    /// value and the file already uses JSON attributes (e.g. [JsonIgnore]),
    /// strip-default must annotate the stripped original constructor with
    /// [JsonConstructor] so that System.Text.Json does not throw
    /// NotSupportedException ("no singular parameterized constructor").
    /// </summary>
    [Fact]
    public void Strip_ConstructorWithDefault_AddsJsonConstructor_WhenFileUsesJsonAttributes()
    {
        var src = """
            using System.Text.Json.Serialization;
            class C
            {
                [JsonIgnore]
                public int X { get; }
                public C(int x, int y = 5) => X = x;
            }
            """;
        var result = Strip(src);
        _output.WriteLine("Output:\n" + result.OutputCode);
        Assert.True(result.Changed);
        // The stripped original constructor (with all params) gets [JsonConstructor]
        Assert.Contains("[JsonConstructor]", result.OutputCode);
        // The generated overload must NOT carry the attribute
        var lines = result.OutputCode.Split('\n');
        var overloadLines = lines.Where(l => l.Contains("public C(int x)") && !l.Contains("int y"));
        Assert.All(overloadLines, l => Assert.DoesNotContain("JsonConstructor", l));
        AssertCompileClean(result.OutputCode);
    }

    /// <summary>
    /// When the file does not use any System.Text.Json attributes,
    /// strip-default must NOT add [JsonConstructor] — the attribute may
    /// reference an assembly the project does not have.
    /// </summary>
    [Fact]
    public void Strip_ConstructorWithDefault_NoJsonConstructor_WhenFileHasNoJsonAttributes()
    {
        var src = "class C { public C(int x, int y = 5) { } }";
        var result = Strip(src);
        Assert.True(result.Changed);
        Assert.DoesNotContain("JsonConstructor", result.OutputCode);
        AssertCompileClean(result.OutputCode);
    }

    /// <summary>
    /// When the type already has a [JsonConstructor]-attributed constructor,
    /// strip-default must not add a second one.
    /// </summary>
    [Fact]
    public void Strip_ConstructorWithDefault_DoesNotAddSecond_WhenJsonConstructorAlreadyPresent()
    {
        var src = """
            using System.Text.Json.Serialization;
            class C
            {
                public int X { get; }
                [JsonConstructor]
                public C(int x, int y = 5) => X = x;
            }
            """;
        var result = Strip(src);
        Assert.True(result.Changed);
        // Count occurrences of [JsonConstructor] — should be exactly 1
        var count = result.OutputCode.Split("JsonConstructor").Length - 1;
        Assert.Equal(1, count);
        AssertCompileClean(result.OutputCode);
    }

    /// <summary>
    /// When multiple constructors have default values, only the first one
    /// processed gets [JsonConstructor] (at most one per type).
    /// </summary>
    [Fact]
    public void Strip_MultipleConstructorsWithDefaults_AddsAtMostOneJsonConstructor()
    {
        var src = """
            using System.Text.Json.Serialization;
            class C
            {
                [JsonIgnore]
                public int X { get; }
                public C(int x, int y = 5) => X = x;
                public C(int x, int y, int z = 7) => X = x;
            }
            """;
        var result = Strip(src);
        Assert.True(result.Changed);
        var count = result.OutputCode.Split("JsonConstructor").Length - 1;
        Assert.True(count <= 1, $"Expected at most 1 JsonConstructor, found {count}");
        AssertCompileClean(result.OutputCode);
    }

    /// <summary>
    /// When GenerateOverloads is disabled, no overload is generated, so
    /// no [JsonConstructor] should be added (the type still has a single
    /// parameterized constructor).
    /// </summary>
    [Fact]
    public void Strip_NoOverloads_NoJsonConstructor()
    {
        var src = """
            using System.Text.Json.Serialization;
            class C
            {
                [JsonIgnore]
                public int X { get; }
                public C(int x, int y = 5) => X = x;
            }
            """;
        var tree = CSharpSyntaxTree.ParseText(src);
        var compilation = CSharpCompilation.Create("strip-test-no-overload",
            new[] { tree },
            BuildMetadataReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var model = compilation.GetSemanticModel(tree);
        var stripper = new DefaultParameterStripper();
        stripper.Collect(tree, model);
        var result = stripper.Strip(src, model, new DefaultParameterStripperOptions(GenerateOverloads: false));
        Assert.True(result.Changed);
        Assert.DoesNotContain("JsonConstructor", result.OutputCode);
        AssertCompileClean(result.OutputCode);
    }

    private static void AssertCompileClean(string source)
    {
        var tree = CSharpSyntaxTree.ParseText(source);
        var compilation = CSharpCompilation.Create("strip-output-test",
            new[] { tree },
            BuildMetadataReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var errors = compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToList();
        Assert.True(errors.Count == 0,
            "Output should compile. Errors:\n" + string.Join("\n", errors));
    }

    private static DefaultParameterStripperResult Strip(string src)
    {
        var tree = CSharpSyntaxTree.ParseText(src);
        var compilation = CSharpCompilation.Create("strip-test",
            new[] { tree },
            BuildMetadataReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var model = compilation.GetSemanticModel(tree);
        var stripper = new DefaultParameterStripper();
        // Pass 1 registers declarations so call sites can be completed.
        stripper.Collect(tree, model);
        return stripper.Strip(src, model);
    }

    /// <summary>
    /// Builds the set of metadata references needed for test compilations.
    /// Includes System.Text.Json (and its transitive dependencies like
    /// System.Runtime) so that [JsonConstructor] / [JsonIgnore] attributes
    /// in test source can compile and be resolved by the rewriter's
    /// semantic model.
    /// </summary>
    private static IEnumerable<MetadataReference> BuildMetadataReferences()
    {
        var result = new List<MetadataReference>
        {
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
        };

        // Names of assemblies that should be referenced for JSON attribute
        // compilation. We try to load them explicitly because they may not
        // be present in the test host's AppDomain.
        var requiredAssemblies = new[]
        {
            "System.Runtime",
            "System.Text.Json",
            "System.Text.Encodings.Web",
            "System.Buffers",
            "System.Memory",
            "System.Numerics.Vectors",
            "System.Threading.Tasks.Extensions",
            "System.Runtime.CompilerServices.Unsafe",
            "System.Collections.Immutable",
        };

        foreach (var name in requiredAssemblies)
        {
            try
            {
                var asm = AppDomain.CurrentDomain.GetAssemblies()
                    .FirstOrDefault(a => string.Equals(
                        a.GetName().Name, name, StringComparison.OrdinalIgnoreCase));
                if (asm == null)
                {
                    // Try to load it from the .NET runtime directory.
                    var rtDir = RuntimeEnvironment.GetRuntimeDirectory();
                    var path = Path.Combine(rtDir, name + ".dll");
                    if (File.Exists(path))
                        asm = Assembly.LoadFrom(path);
                }
                if (asm != null && !asm.IsDynamic
                    && !string.IsNullOrEmpty(asm.Location))
                {
                    result.Add(MetadataReference.CreateFromFile(asm.Location));
                }
            }
            catch { /* skip unavailable assembly */ }
        }

        return result;
    }
}
