using CSharpToJava.Core.TupleToStruct;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace CSharpToJava.Tests;

/// <summary>
/// tuple-to-struct 预处理器单元测试：
/// 1) 纯消费文件（自身无 tuple 语法、只用具名元素访问别处声明的元组）必须改写，
///    否则声明点换成生成 struct 后残留旧元素名（CS1061，语料 RegexInterpreter）；
/// 2) 生成 struct 必须是可变值类型（镜像 System.ValueTuple），保证字段赋值、
///    复合赋值、ref/out 元素传递合法（CS0191/CS0192，语料 Regex.Cache/RegexReplacement）。
/// </summary>
public sealed class TupleToStructPreprocessorTests
{
    private static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.Preview);

    private static IEnumerable<MetadataReference> GetReferences()
    {
        var trusted = (string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES");
        if (trusted != null)
        {
            foreach (var assembly in trusted.Split(Path.PathSeparator))
            {
                if (File.Exists(assembly))
                {
                    yield return MetadataReference.CreateFromFile(assembly);
                }
            }

            yield break;
        }

        yield return MetadataReference.CreateFromFile(typeof(object).Assembly.Location);
    }

    private sealed record RunResult(
        List<string> Outputs,
        string? StructFile,
        TupleToStructStatistics Statistics,
        List<TupleToStructDiagnostic> Diagnostics);

    private static RunResult RunAll(params string[] sources)
    {
        var trees = sources
            .Select(source => CSharpSyntaxTree.ParseText(source, ParseOptions))
            .ToArray();
        var compilation = CSharpCompilation.Create(
            "tuple-test",
            trees,
            GetReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));

        var preprocessor = new TupleToStructPreprocessor();
        var statistics = new TupleToStructStatistics();
        var allDiagnostics = new List<TupleToStructDiagnostic>();
        var outputs = new List<string>();

        for (var i = 0; i < trees.Length; i++)
        {
            var fileDiagnostics = new List<TupleToStructDiagnostic>();
            var result = preprocessor.RewriteFile(
                sources[i], compilation.GetSemanticModel(trees[i]),
                statistics, fileDiagnostics);
            allDiagnostics.AddRange(fileDiagnostics);
            outputs.Add(result.OutputCode);
        }

        return new RunResult(
            outputs,
            preprocessor.GenerateStructFile(),
            statistics,
            allDiagnostics);
    }

    private static void AssertOutputCompiles(IEnumerable<string> sources, string? structFile)
    {
        var trees = sources
            .Select(source => CSharpSyntaxTree.ParseText(source, ParseOptions))
            .ToList();
        if (structFile != null)
        {
            trees.Add(CSharpSyntaxTree.ParseText(structFile, ParseOptions));
        }

        var compilation = CSharpCompilation.Create(
            "tuple-verify",
            trees,
            GetReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        var errors = compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToList();
        Assert.True(
            errors.Count == 0,
            "Rewritten tuple code failed to compile:\n" +
            string.Join("\n", errors) +
            "\n--- struct file ---\n" + structFile);
    }

    [Fact]
    public void NamedElementAccess_InFileWithoutTupleSyntax_IsRewrittenToItemN()
    {
        // Regression for CS1061 in RegexInterpreter.cs: _code.LeadingCharClasses[0].CharClass
        // lived in a file that spells no tuple syntax itself; the syntax-only dirty
        // gate skipped the whole file while the declaration file was rewritten.
        var declaration = """
            class Holder
            {
                public (string CharClass, bool CaseInsensitive)[]? Classes;
            }
            """;
        var consumer = """
            class Consumer
            {
                string First(Holder h) => h.Classes![0].CharClass;
                bool Second(Holder h) => h.Classes![0].CaseInsensitive;
                string Named(Holder h)
                {
                    var item = h.Classes![0];
                    return item.CharClass;
                }
            }
            """;

        var result = RunAll(declaration, consumer);

        Assert.Empty(result.Diagnostics.Where(d => d.Severity == TupleToStructSeverity.Error));
        Assert.Equal(2, result.Statistics.FilesTransformed);
        Assert.DoesNotContain("CharClass", result.Outputs[1]);
        Assert.DoesNotContain("CaseInsensitive", result.Outputs[1]);
        Assert.Contains(".Item1", result.Outputs[1]);
        Assert.Contains(".Item2", result.Outputs[1]);
        Assert.NotNull(result.StructFile);
        AssertOutputCompiles(result.Outputs, result.StructFile);
    }

    [Fact]
    public void PlainItemAccess_InFileWithoutTupleSyntax_IsRewritten()
    {
        var declaration = """
            class Holder
            {
                public global::System.Func<(int First, int Second)> Factory = null!;
            }
            """;
        var consumer = """
            class Consumer
            {
                int Add(Holder h) => h.Factory().Item1 + h.Factory().Item2;
            }
            """;

        var result = RunAll(declaration, consumer);

        Assert.Contains(".Item1", result.Outputs[1]);
        Assert.Contains(".Item2", result.Outputs[1]);
        AssertOutputCompiles(result.Outputs, result.StructFile);
    }

    [Fact]
    public void ConditionalNamedAccess_InFileWithoutTupleSyntax_IsRewritten()
    {
        var declaration = """
            class Holder
            {
                public (string Name, int Value)? Maybe;
            }
            """;
        var consumer = """
            class Consumer
            {
                string? Get(Holder h) => h.Maybe?.Name;
            }
            """;

        var result = RunAll(declaration, consumer);

        Assert.DoesNotContain("?.Name", result.Outputs[1]);
        Assert.Contains("?.Item1", result.Outputs[1]);
        AssertOutputCompiles(result.Outputs, result.StructFile);
    }

    [Fact]
    public void MutableStruct_FieldAssignment_CompoundAssignment_AndRefOut_Compile()
    {
        // Regression for CS0191/CS0192 in Regex.Cache.cs / RegexReplacement.cs:
        // ValueTuple is a mutable struct; the generated replacement must be
        // mutable too or field writes/ref element passing are illegal.
        var source = """
            class C
            {
                delegate void Mutator(ref (int X, int Y) q);

                void ConsumeRef(ref int x) => x++;
                void ConsumeOut(out int x) => x = 42;

                void Mutate()
                {
                    var t = (1, 2);
                    t.Item1 = 5;
                    t.Item2++;
                    --t.Item1;
                    ref int ra = ref t.Item1;
                    ConsumeRef(ref t.Item2);
                    ConsumeOut(out t.Item1);

                    Mutator m = (ref (int X, int Y) q) =>
                    {
                        q.X = 3;
                        --q.Y;
                        ConsumeRef(ref q.X);
                    };
                    m(ref t);
                }
            }
            """;

        var result = RunAll(source);

        Assert.Empty(result.Diagnostics.Where(d => d.Severity == TupleToStructSeverity.Error));
        Assert.NotNull(result.StructFile);
        AssertOutputCompiles(result.Outputs, result.StructFile);
    }

    [Fact]
    public void ShapeWithPrivateNestedElementType_StaysNativeValueTuple()
    {
        // Regression for CS0122 in XmlTextReaderImpl: a tuple whose element is a
        // PRIVATE nested type (private enum EntityType) can only be used inside the
        // enclosing class. Hoisting it to a top-level Cs2jTuples struct generates
        // fields that name an inaccessible type. The shape must stay a native
        // System.ValueTuple and must not appear in the generated struct file.
        var source = """
            using System.Threading.Tasks;

            partial class Reader
            {
                private enum EntityType { None, Expanded }

                private Task<(int EndPos, EntityType Kind)> HandleAsync(bool flag)
                {
                    return Task.FromResult((0, EntityType.None));
                }

                private async Task ConsumeAsync()
                {
                    var r = await HandleAsync(true);
                    // Named-element access keeps binding through the retained
                    // [TupleElementNames] attribute on HandleAsync.
                    var (endPos, kind) = (r.EndPos, r.Kind);
                    if (kind == EntityType.None)
                    {
                        System.Console.WriteLine(endPos);
                    }
                }
            }
            """;

        var result = RunAll(source);

        Assert.Empty(result.Diagnostics.Where(d => d.Severity == TupleToStructSeverity.Error));
        // The entity tuple survives verbatim: no struct reference at the use sites.
        Assert.DoesNotContain("Cs2jTuples", result.Outputs[0]);
        // The private type must never be emitted into the shared struct file.
        Assert.DoesNotContain("EntityType", result.StructFile ?? string.Empty);
        // Native tuple syntax (return type + literal + deconstruction) compiles.
        AssertOutputCompiles(result.Outputs, result.StructFile);
    }

    [Fact]
    public void EmittableShape_AlongsideDeniedShape_StillRewritten()
    {
        // One file using both a denied (private-element) tuple and an ordinary
        // (int, string) tuple: only the emittable shape is lowered and generated.
        var source = """
            partial class Reader
            {
                private enum EntityType { None }

                (EntityType Kind, int Pos) Denied() => (EntityType.None, 1);

                (int A, string B) Allowed() => (1, "x");

                void Use()
                {
                    var a = Allowed();
                    System.Console.WriteLine(a.A + a.B);
                }
            }
            """;

        var result = RunAll(source);

        Assert.Contains("Cs2jTuples", result.Outputs[0]);
        Assert.Contains("TupleInt32String", result.StructFile ?? string.Empty);
        Assert.DoesNotContain("EntityType", result.StructFile ?? string.Empty);
        AssertOutputCompiles(result.Outputs, result.StructFile);
    }

    [Fact]
    public void GeneratedStruct_IsMutable_MirroringValueTuple()
    {
        var source = """
            class C
            {
                (int A, string B) _field;
                void M() => _field = (1, "x");
            }
            """;

        var result = RunAll(source);

        Assert.NotNull(result.StructFile);
        Assert.Contains("public struct", result.StructFile);
        Assert.DoesNotContain("readonly struct", result.StructFile);
        Assert.DoesNotContain("public readonly", result.StructFile);
        // The struct itself must stay compilable in isolation.
        AssertOutputCompiles(System.Array.Empty<string>(), result.StructFile);
    }
}
