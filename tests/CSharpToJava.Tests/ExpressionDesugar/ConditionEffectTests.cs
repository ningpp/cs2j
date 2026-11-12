using System.Reflection;
using CSharpToJava.Core.ExpressionDesugar;
using CSharpToJava.Core.ExpressionDesugar.ConditionEffect;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace CSharpToJava.Tests.ExpressionDesugar;

/// <summary>
/// J-04 / G-36 / JE-04: property/indexer side effects in <c>while</c>/<c>if</c>
/// conditions are linearized into the canonical statement form in C# so the
/// Java side no longer has to rebuild the control flow at text-emission time
/// (the <c>while (++start.Value &lt; _length)</c> infinite-loop bug a2d3a6f0).
/// </summary>
public sealed class ConditionEffectTests
{
    private static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.Preview);

    private static readonly MetadataReference[] References = BuildReferences();

    private static MetadataReference[] BuildReferences()
    {
        var trusted = (string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES");
        if (trusted == null)
        {
            return new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) };
        }

        return trusted
            .Split(Path.PathSeparator)
            .Where(File.Exists)
            .Select(static path => (MetadataReference)MetadataReference.CreateFromFile(path))
            .ToArray();
    }

    private static ExpressionDesugarResult Run(string source)
    {
        var tree = CSharpSyntaxTree.ParseText(source, ParseOptions);
        var compilation = CSharpCompilation.Create(
            "condition-effect-in-" + Guid.NewGuid().ToString("N"),
            new[] { tree },
            References,
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                allowUnsafe: true));
        Assert.DoesNotContain(
            compilation.GetDiagnostics(),
            static d => d.Severity == DiagnosticSeverity.Error);

        return new ExpressionDesugarPreprocessor().Preprocess(
            source,
            compilation.GetSemanticModel(tree),
            new ExpressionDesugarOptions
            {
                ConditionEffect = new ConditionEffectOptions(),
            });
    }

    private static string Squish(string code)
        => System.Text.RegularExpressions.Regex.Replace(code, @"\s+", " ");

    private static List<Diagnostic> CompileErrors(string code)
    {
        var tree = CSharpSyntaxTree.ParseText(code, ParseOptions);
        var compilation = CSharpCompilation.Create(
            "condition-effect-out-" + Guid.NewGuid().ToString("N"),
            new[] { tree },
            References,
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                allowUnsafe: true));
        return compilation.GetDiagnostics()
            .Where(static d => d.Severity == DiagnosticSeverity.Error)
            .ToList();
    }

    private static Assembly LoadAssembly(string code)
    {
        var tree = CSharpSyntaxTree.ParseText(code, ParseOptions);
        var compilation = CSharpCompilation.Create(
            "condition-effect-run-" + Guid.NewGuid().ToString("N"),
            new[] { tree },
            References,
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                allowUnsafe: true));
        using var stream = new MemoryStream();
        var emit = compilation.Emit(stream);
        Assert.True(
            emit.Success,
            string.Join("; ", emit.Diagnostics
                .Where(static d => d.Severity == DiagnosticSeverity.Error)));
        return Assembly.Load(stream.ToArray());
    }

    private static int RunInt(string code, string typeName, string methodName)
    {
        var assembly = LoadAssembly(code);
        var type = assembly.GetType(typeName, throwOnError: true)!;
        var method = type.GetMethod(
            methodName, BindingFlags.Public | BindingFlags.Static)!;

        var result = 0;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                result = (int)method.Invoke(null, null)!;
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        })
        {
            IsBackground = true,
        };
        thread.Start();
        Assert.True(
            thread.Join(10_000),
            "rewritten loop did not terminate within 10s (condition not re-evaluated)");
        if (failure != null)
            throw new InvalidOperationException("rewritten method failed", failure);
        return result;
    }

    private const string PropertyWhileSource = """
        using System;

        public class Counter {
            private int _value;
            public int Value { get => _value; set => _value = value; }
        }

        public static class C {
            public static void M() {
                var obj = new Counter();
                while (++obj.Value < 10) {
                    Console.WriteLine(obj.Value);
                }
            }
        }
        """;

    [Fact]
    public void While_PropertyIncrement_BecomesWhileTrue()
    {
        var result = Run(PropertyWhileSource);

        Assert.True(result.Changed);
        Assert.Equal(1, result.Statistics.ConditionEffectRewrites);

        var output = Squish(result.OutputCode);
        Assert.Contains("while (true)", output);
        Assert.Contains("bool __ed_cond_0 = ++obj.Value < 10;", output);
        Assert.Contains("if (!__ed_cond_0)", output);
        Assert.Contains("break;", output);
        Assert.Contains("Console.WriteLine(obj.Value);", output);

        Assert.Empty(CompileErrors(result.OutputCode));
    }

    [Fact]
    public void While_ContinueReevaluatesCondition()
    {
        const string source = """
            public class Counter {
                private int _value;
                public int Value { get => _value; set => _value = value; }
            }

            public static class C {
                public static int M() {
                    var obj = new Counter();
                    var sum = 0;
                    while (++obj.Value < 10) {
                        if (obj.Value == 3)
                            continue;
                        sum += obj.Value;
                    }
                    return sum;
                }
            }
            """;

        var result = Run(source);

        Assert.True(result.Changed);
        var output = Squish(result.OutputCode);
        Assert.Contains("while (true)", output);
        Assert.Contains("continue;", output);

        // 1+2+4+5+6+7+8+9 = 42: `continue` must re-evaluate (and increment) the
        // condition instead of skipping back to the body head.
        Assert.Equal(42, RunInt(result.OutputCode, "C", "M"));
        Assert.Empty(CompileErrors(result.OutputCode));
    }

    [Fact]
    public void If_PropertyAssignment_IsLifted()
    {
        // The triggering `if` is an embedded statement (body of another `if`),
        // so the canonical block-wrapped form is required: a bare declaration
        // cannot be inserted into an embedded-statement position.
        const string source = """
            public class Counter {
                private int _value;
                private int _next;
                public int Value { get => _value; set => _value = value; }
                public int Next() { _next = _next + 1; return _next; }
            }

            public static class C {
                public static int M(Counter obj, bool outer) {
                    var r = 0;
                    if (outer)
                        if ((obj.Value = obj.Next()) > 0)
                            r = obj.Value;
                    return r;
                }
            }
            """;

        var result = Run(source);

        Assert.True(result.Changed);
        var output = Squish(result.OutputCode);
        Assert.Contains("if (outer) { bool __ed_cond_0 = (obj.Value = obj.Next()) > 0;", output);
        Assert.Contains("if (__ed_cond_0)", output);
        Assert.Empty(CompileErrors(result.OutputCode));
    }

    [Fact]
    public void If_InBlock_InsertsDeclarationBefore()
    {
        const string source = """
            using System;

            public class Counter {
                private int _value;
                public int Value { get => _value; set => _value = value; }
            }

            public static class C {
                public static void M(Counter obj) {
                    if (obj.Value++ > 0) {
                        Console.WriteLine(obj.Value);
                    }
                }
            }
            """;

        var result = Run(source);

        Assert.True(result.Changed);
        var output = Squish(result.OutputCode);
        Assert.Contains(
            "bool __ed_cond_0 = obj.Value++ > 0; if (__ed_cond_0)", output);

        // In-block sites keep the if in place; no extra wrapping block.
        Assert.DoesNotContain("{ { bool __ed_cond_0", output);
        Assert.Empty(CompileErrors(result.OutputCode));
    }

    [Fact]
    public void PureCondition_NotRewritten()
    {
        const string source = """
            using System.Collections.Generic;

            public static class C {
                public static void M(int x, List<int> list) {
                    if (x > 0) {
                        x = x - 1;
                    }
                    while (list.Count > 0) {
                        list.RemoveAt(list.Count - 1);
                    }
                }
            }
            """;

        var result = Run(source);

        Assert.False(result.Changed);
        Assert.Equal(source, result.OutputCode);
        Assert.Equal(0, result.Statistics.ConditionEffectRewrites);
    }

    [Fact]
    public void ForCondition_NotRewritten()
    {
        const string source = """
            using System;

            public class Counter {
                private int _value;
                public int Value { get => _value; set => _value = value; }
            }

            public static class C {
                public static void M() {
                    var obj = new Counter();
                    for (; ++obj.Value < 10; ) {
                        Console.WriteLine(obj.Value);
                    }
                }
            }
            """;

        var result = Run(source);

        Assert.False(result.Changed);
        Assert.Equal(source, result.OutputCode);
    }

    [Fact]
    public void ConditionWithOutVar_NotRewritten()
    {
        const string source = """
            using System;

            public static class C {
                public static bool TryGet(out int value) {
                    value = 1;
                    return true;
                }

                public static void M() {
                    if (TryGet(out var x)) {
                        Console.WriteLine(x);
                    }
                }
            }
            """;

        var result = Run(source);

        Assert.False(result.Changed);
        Assert.Equal(source, result.OutputCode);
        Assert.Equal(0, result.Statistics.ConditionEffectRewrites);
    }

    /// <summary>
    /// `if (cond && TryGet(out x))` derives x's definite assignment from the
    /// CONDITION expression (x is assigned in the true branch). Hoisting the
    /// condition into a temp would lose that knowledge and emit CS0165 at the
    /// uses, so the whole site must be preserved (with a Warning) even though
    /// the condition also carries a property side effect.
    /// </summary>
    [Fact]
    public void ConditionWithOutVariableAndPropertyEffect_IsPreserved()
    {
        const string source = """
            using System;

            public class Counter {
                private int _value;
                public int Value { get => _value; set => _value = value; }
            }

            public static class C {
                public static bool TryGet(out int value) {
                    value = 1;
                    return true;
                }

                public static void M(Counter obj) {
                    int x;
                    if (obj.Value++ > 0 && TryGet(out x)) {
                        Console.WriteLine(x);
                    }
                }
            }
            """;

        var result = Run(source);

        Assert.False(result.Changed);
        Assert.Equal(source, result.OutputCode);
        Assert.Contains(
            result.Diagnostics,
            static d => d.Code == "CS2J0590"
                        && d.Severity == ExpressionDesugarSeverity.Warning);
    }

    [Fact]
    public void NestedBlocks_UniqueTempNames()
    {
        const string source = """
            public class Counter {
                private int _value;
                public int Value { get => _value; set => _value = value; }
            }

            public static class C {
                public static void A() {
                    var obj = new Counter();
                    while (++obj.Value < 3) {
                    }
                }

                public static void B() {
                    var obj = new Counter();
                    if (obj.Value-- > 0) {
                    }
                }
            }
            """;

        var result = Run(source);

        Assert.True(result.Changed);
        var output = Squish(result.OutputCode);
        Assert.Contains("bool __ed_cond_0 = ++obj.Value < 3;", output);
        Assert.Contains("bool __ed_cond_1 = obj.Value-- > 0;", output);
        Assert.Empty(CompileErrors(result.OutputCode));
    }

    [Fact]
    public void Idempotent_SecondRunUnchanged()
    {
        var first = Run(PropertyWhileSource);
        Assert.True(first.Changed);

        var second = Run(first.OutputCode);

        Assert.False(second.Changed);
        Assert.Equal(first.OutputCode, second.OutputCode);
        Assert.Equal(0, second.Statistics.ConditionEffectRewrites);
    }
}
