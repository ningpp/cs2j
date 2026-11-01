using System.Reflection;
using System.Runtime.Loader;
using CSharpToJava.Core.RefOutInPreprocessor;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace CSharpToJava.Tests;

/// <summary>
/// Regression for CS0103 in the ref/out/in pass: a declaration-expression
/// (<c>out double valueDouble</c>) inside a switch section is statement-lifted
/// into a generated hoisted block. A switch section is not a BlockSyntax, so
/// that block survived as an extra scope and hid the root declaration from the
/// following statements of the same section (<c>root = valueDouble;</c>).
/// </summary>
public class RefOutInSwitchSectionScopeTests
{
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
                // out 值类型读回（与 src/Cs2jRefOutIn/ByRef.cs 的 ReadbackX 一致，Java 半区 null→default 桥）
                public static int ReadbackInt(ByRef<int> slot) => slot.Value;
                public static double ReadbackDouble(ByRef<double> slot) => slot.Value;
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
            "roi-switch-section-" + Guid.NewGuid().ToString("N"),
            new[] { tree },
            GetReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var model = compilation.GetSemanticModel(tree);
        return new RefOutInRewriter().Rewrite(src, model);
    }

    private static void AssertOutputCompiles(string code)
    {
        var tree = CSharpSyntaxTree.ParseText(code);
        var support = CSharpSyntaxTree.ParseText(ByRefSupportStub);
        var compilation = CSharpCompilation.Create(
            "roi-switch-section-verify-" + Guid.NewGuid().ToString("N"),
            new[] { tree, support },
            GetReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var errors = compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        Assert.True(errors.Count == 0,
            "ref/out/in output failed to compile:\n" + string.Join("\n", errors));
    }

    private static Assembly EmitOutput(string code)
    {
        var tree = CSharpSyntaxTree.ParseText(code);
        var support = CSharpSyntaxTree.ParseText(ByRefSupportStub);
        var compilation = CSharpCompilation.Create(
            "roi-switch-section-emit-" + Guid.NewGuid().ToString("N"),
            new[] { tree, support },
            GetReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var errors = compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        Assert.True(errors.Count == 0,
            "ref/out/in output failed to compile:\n" + string.Join("\n", errors));

        using var ms = new MemoryStream();
        var emit = compilation.Emit(ms);
        Assert.True(emit.Success,
            "emit failed:\n" + string.Join("\n",
                emit.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
        return AssemblyLoadContext.Default.LoadFromStream(new MemoryStream(ms.ToArray()));
    }

    private const string Source = """
        public static class Api
        {
            public static bool TryGet(ref int state, out double value)
            {
                value = state;
                state++;
                return true;
            }
        }

        public static class Use
        {
            public static double Run(int token)
            {
                int state = 4;
                double root = 0;
                switch (token)
                {
                    case 0:
                        Api.TryGet(ref state, out double valueDouble);
                        root = valueDouble;
                        break;
                }
                return root;
            }
        }
        """;

    [Fact]
    public void Out_Declaration_Stays_In_Switch_Section_Scope()
    {
        var result = Run(Source);
        Assert.True(result.Changed);
        Assert.DoesNotContain(result.Diagnostics, d => d.Severity == RefOutInSeverity.Error);
        AssertOutputCompiles(result.OutputCode);

        var output = result.OutputCode;
        Assert.Contains("double valueDouble = default", output);

        // The lifted root declaration and the following use must live in the
        // SAME section statement sequence: no generated `{ ... }` may separate
        // them (that extra block was the CS0103 cause).
        var sectionStart = output.IndexOf("case 0:", StringComparison.Ordinal);
        var useIndex = output.IndexOf("root = valueDouble;", StringComparison.Ordinal);
        Assert.True(sectionStart >= 0 && useIndex > sectionStart,
            "expected the case section and its use of valueDouble in the output");

        var sectionText = output[sectionStart..useIndex];
        Assert.Equal(
            sectionText.Count(c => c == '{'),
            sectionText.Count(c => c == '}'));
    }

    [Fact]
    public void Out_Declaration_In_Switch_Section_Runs()
    {
        var result = Run(Source);
        Assert.True(result.Changed);
        Assert.DoesNotContain(result.Diagnostics, d => d.Severity == RefOutInSeverity.Error);

        var asm = EmitOutput(result.OutputCode);
        var run = asm.GetType("Use")!.GetMethod("Run", BindingFlags.Public | BindingFlags.Static)!;
        Assert.Equal(4d, run.Invoke(null, new object[] { 0 }));
    }
}
