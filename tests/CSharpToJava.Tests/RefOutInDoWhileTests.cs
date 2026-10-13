using System.Reflection;
using System.Runtime.Loader;
using CSharpToJava.Core.RefOutInPreprocessor;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace CSharpToJava.Tests;

/// <summary>
/// Regression for ROI3303 on do-while loops whose condition contains an
/// internal ref/out call (the three XmlTextReaderImpl ParseText/ParsePIValue/
/// ParseCDataOrComment sites). The rewrite
///
///   do B while (C);  →  while (true) { B; &lt;condition splice&gt;; if (!C') break; }
///
/// must preserve do-while semantics: body runs before the first condition
/// evaluation, a direct `continue` re-evaluates the condition (it is mapped to
/// a break out of a do-once wrapper), and a direct `break` leaves the loop.
/// </summary>
public class RefOutInDoWhileTests
{
    // Mirrors src/Cs2jRefOutIn/ByRef.cs: Value is an abstract property so the
    // accessor factory writes THROUGH to the captured ref target (a field-
    // backed snapshot stub would silently drop the callee's mutations).
    private const string ByRefSupportStub = """
        namespace Cs2jByRef
        {
            public abstract class ByRef<T>
            {
                public abstract T Value { get; set; }
                public static ByRef<T> Create() => new SlotByRef<T>();
                public static ByRef<T> Create(T value) => new SlotByRef<T>(value);
                public static ByRef<T> Create(System.Func<T> getter, System.Action<T> setter)
                    => new AccessorByRef<T>(getter, setter);
            }
            internal sealed class SlotByRef<T> : ByRef<T>
            {
                private T _slot = default!;
                public SlotByRef() { }
                public SlotByRef(T value) { _slot = value; }
                public override T Value { get => _slot; set => _slot = value; }
            }
            internal sealed class AccessorByRef<T> : ByRef<T>
            {
                private readonly System.Func<T> _getter;
                private readonly System.Action<T> _setter;
                public AccessorByRef(System.Func<T> getter, System.Action<T> setter)
                { _getter = getter; _setter = setter; }
                public override T Value { get => _getter(); set => _setter(value); }
            }
        }
        """;

    private static IEnumerable<MetadataReference> GetReferences()
    {
        var trusted = (string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES");
        if (trusted != null)
        {
            foreach (var assembly in trusted.Split(Path.PathSeparator))
            {
                if (File.Exists(assembly)) yield return MetadataReference.CreateFromFile(assembly);
            }

            yield break;
        }

        yield return MetadataReference.CreateFromFile(typeof(object).Assembly.Location);
    }

    private static RefOutInRewriterResult Run(string src)
    {
        var tree = CSharpSyntaxTree.ParseText(src);
        var compilation = CSharpCompilation.Create(
            "roi-dowhile-" + Guid.NewGuid().ToString("N"),
            new[] { tree },
            GetReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        var model = compilation.GetSemanticModel(tree);
        return new RefOutInRewriter().Rewrite(src, model);
    }

    private static void AssertOutputCompiles(string code)
    {
        var tree = CSharpSyntaxTree.ParseText(code);
        var support = CSharpSyntaxTree.ParseText(ByRefSupportStub);
        var compilation = CSharpCompilation.Create(
            "roi-dowhile-verify-" + Guid.NewGuid().ToString("N"),
            new[] { tree, support },
            GetReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var errors = compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        Assert.True(errors.Count == 0,
            "ref/out/in output failed to compile:\n" + string.Join("\n", errors));
    }

    /// <summary>Compiles source to an in-memory assembly and invokes Demo.<paramref name="method"/>.</summary>
    private static string? Invoke(string source, string method, bool withByRefStub)
    {
        var trees = new List<SyntaxTree> { CSharpSyntaxTree.ParseText(source) };
        if (withByRefStub) trees.Add(CSharpSyntaxTree.ParseText(ByRefSupportStub));
        var compilation = CSharpCompilation.Create(
            "roi-dowhile-emit-" + Guid.NewGuid().ToString("N"),
            trees,
            GetReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        using var ms = new MemoryStream();
        var emit = compilation.Emit(ms);
        Assert.True(emit.Success,
            "emit failed:\n" + string.Join("\n",
                emit.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));

        var asm = AssemblyLoadContext.Default.LoadFromStream(new MemoryStream(ms.ToArray()));
        var demo = asm.GetType("Demo")!;
        return (string?)demo.GetMethod(method, BindingFlags.Public | BindingFlags.Static)!
            .Invoke(null, null);
    }

    private const string ShapeSource = """
        class C
        {
            int _calls;
            bool Parse(out int a, out int b, ref int r)
            {
                _calls++;
                r++;
                a = _calls;
                b = _calls * 10;
                return _calls >= 3;
            }

            void M()
            {
                int a = 0, b = 0, r = 0;
                do
                {
                    System.Console.WriteLine(a + b + r);
                } while (!Parse(out a, out b, ref r));
            }
        }
        """;

    [Fact]
    public void DoWhile_With_Out_Calls_In_Condition_Is_Lifted_Without_Error()
    {
        var result = Run(ShapeSource);

        Assert.True(result.Changed);
        Assert.DoesNotContain(result.Diagnostics,
            d => d.Severity == RefOutInSeverity.Error);
        Assert.DoesNotContain(result.OutputCode, "ROI3303");
        // do-while became a guarded infinite loop; the condition call is
        // materialized before the break test.
        Assert.Contains("while (true)", result.OutputCode);
        AssertOutputCompiles(result.OutputCode);
    }

    /// <summary>
    /// Shared behavioral source: plain loop (Run1) and a loop with a direct
    /// continue/break plus a nested loop (Run2). Both original and rewritten
    /// assemblies are executed; results must be identical.
    /// </summary>
    private const string BehavioralSource = """
        using System.Text;
        public class Demo
        {
            static int calls;
            static bool TryParse(out int a, out int b, ref int r)
            {
                calls++;
                r++;
                a = calls;
                b = calls * 10;
                return calls >= 3;
            }

            public static string Run1()
            {
                calls = 0;
                int a = 0, b = 0, r = 0;
                var sb = new StringBuilder();
                do
                {
                    sb.Append(a).Append(',').Append(b).Append(',').Append(r).Append(';');
                } while (!TryParse(out a, out b, ref r));
                sb.Append("end:").Append(a).Append(',').Append(b).Append(',').Append(r);
                return sb.ToString();
            }

            public static string Run2()
            {
                calls = 0;
                int a = 0, b = 0, r = 0;
                var sb = new StringBuilder();
                int guard = 0;
                do
                {
                    guard++;
                    if (guard > 20) { sb.Append("INF"); break; }
                    if (a == 2) { a = -2; continue; }
                    int k = 0;
                    while (k < 2)
                    {
                        k++;
                        if (k == 1) continue;   // binds the nested while — must stay a continue
                        sb.Append('n');
                    }
                    sb.Append(a).Append(',').Append(b).Append(';');
                } while (!TryParse(out a, out b, ref r));
                sb.Append("end");
                return sb.ToString();
            }
        }
        """;

    [Fact]
    public void DoWhile_Plain_Body_Behaves_Identically_After_Lifting()
    {
        var expected = Invoke(BehavioralSource, "Run1", false);
        var result = Run(BehavioralSource);
        Assert.True(result.Changed);
        AssertOutputCompiles(result.OutputCode);
        var actual = Invoke(result.OutputCode, "Run1", true);
        Assert.Equal(expected, actual);
        Assert.Equal("0,0,0;1,10,1;2,20,2;end:3,30,3", actual);
    }

    [Fact]
    public void DoWhile_Continue_Break_And_Nested_Loops_Behave_Identically_After_Lifting()
    {
        var expected = Invoke(BehavioralSource, "Run2", false);
        var result = Run(BehavioralSource);
        AssertOutputCompiles(result.OutputCode);

        // Direct continue required the do-once wrapper + inner break mapping;
        // the nested loop's own continue must be untouched.
        Assert.Contains("while (false)", result.OutputCode);
        Assert.Contains("__cs2jDoBrk", result.OutputCode);
        Assert.Contains("continue;", result.OutputCode);

        var actual = Invoke(result.OutputCode, "Run2", true);
        Assert.Equal(expected, actual);
        Assert.Equal("n0,0;n1,10;end", actual);
    }

    [Fact]
    public void DoWhile_OutVar_Declaration_From_Condition_Is_In_Scope_In_Body()
    {
        // `out var x` in the condition is in scope inside the body; the pure
        // declaration must be hoisted BEFORE the body, while the call and
        // read-backs stay after it.
        const string src = """
            class C
            {
                int _n;
                bool Try(out int x)
                {
                    _n++;
                    x = _n;
                    return _n >= 2;
                }

                void M()
                {
                    do
                    {
                        x = 5;   // legal: x is in scope (unassigned reads would not be)
                        System.Console.WriteLine(x);
                    } while (Try(out var x));
                }
            }
            """;
        var result = Run(src);
        Assert.True(result.Changed);
        Assert.DoesNotContain(result.Diagnostics,
            d => d.Severity == RefOutInSeverity.Error);
        AssertOutputCompiles(result.OutputCode);
    }

    [Fact]
    public void DoWhile_Without_Liftable_Condition_Is_Not_Restructured()
    {
        const string src = """
            class C
            {
                void M()
                {
                    int i = 0;
                    do
                    {
                        i++;
                    } while (i < 3);
                }
            }
            """;
        var result = Run(src);
        Assert.False(result.Changed);
        Assert.DoesNotContain(result.OutputCode, "while (true)");
    }
}
