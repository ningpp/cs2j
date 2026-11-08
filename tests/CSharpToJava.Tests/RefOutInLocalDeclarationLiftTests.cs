using System.Runtime.Loader;
using CSharpToJava.Core.RefOutInPreprocessor;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace CSharpToJava.Tests;

/// <summary>
/// Regression: a local declaration whose initializer forwards a holder to a
/// lifted ref/out call via the "holder slot direct pass" needs no hoisted
/// statement. <c>RefOutInStatementLifter.LiftInvocation</c> still returns a
/// rewritten expression (keyword dropped, <c>DirectPassAnnotation</c> on the
/// argument), but <c>VisitLocalDeclarationStatement</c> used to detect the
/// lift only via <c>hoisted.Count &gt; 0</c> and discard the remainder,
/// falling back to the normal visit of the ORIGINAL initializer. That visit
/// injects <c>.Value</c> into the holder read, turning <c>M(ref holder)</c>
/// into the illegal <c>M(ref holder.Value)</c> (CS1615).
/// </summary>
public class RefOutInLocalDeclarationLiftTests
{
    private static readonly CSharpParseOptions ParseOptions =
        new CSharpParseOptions(LanguageVersion.Latest);

    private const string ByRefSupportStub = """
        namespace Cs2jByRef
        {
            public abstract class ByRef<T>
            {
                public abstract ref T Value { get; }
                public static ByRef<T> Create() => new SlotByRef<T>();
                public static ByRef<T> Create(T value) => new SlotByRef<T>(value);
            }
            internal sealed class SlotByRef<T> : ByRef<T>
            {
                private T _slot = default!;
                public SlotByRef() { }
                public SlotByRef(T value) { _slot = value; }
                public override ref T Value => ref _slot;
            }
        }
        """;

    private const string CallerSource = """
        public struct WriteStack { public int X; }

        public abstract class Base
        {
            protected abstract bool Run(ref WriteStack s);
        }

        public partial class Impl : Base
        {
            protected override bool Run(ref WriteStack s)
            {
                Element element = GetElement(ref s);
                return element != null;
            }
        }
        """;

    // Callee lives in a SECOND syntax tree (same shape as the SDK project:
    // the forwarding call is cross-file); it is rewritten separately so the
    // combined output keeps a matching ByRef<T> signature.
    private const string CalleeSource = """
        public partial class Impl
        {
            protected static Element GetElement(ref WriteStack s) => null!;
        }

        public sealed class Element { }
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

    private static (RefOutInRewriterResult Caller, RefOutInRewriterResult Callee) RewriteBoth()
    {
        var callerTree = CSharpSyntaxTree.ParseText(CallerSource, ParseOptions);
        var calleeTree = CSharpSyntaxTree.ParseText(CalleeSource, ParseOptions);
        var compilation = CSharpCompilation.Create(
            "roi-local-decl-" + Guid.NewGuid().ToString("N"),
            new[] { callerTree, calleeTree },
            GetReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));

        var rewriter = new RefOutInRewriter();
        var caller = rewriter.Rewrite(CallerSource, compilation.GetSemanticModel(callerTree));
        var callee = rewriter.Rewrite(CalleeSource, compilation.GetSemanticModel(calleeTree));
        return (caller, callee);
    }

    [Fact]
    public void LocalDeclaration_ForwardsHolderDirectly_WithoutValueInjection()
    {
        var (caller, callee) = RewriteBoth();

        Assert.True(caller.Changed);
        Assert.DoesNotContain(caller.Diagnostics, d => d.Severity == RefOutInSeverity.Error);

        // Declaration side: the ref parameter is a ByRef<WriteStack> holder.
        Assert.Contains("ByRef<WriteStack> s", caller.OutputCode);
        // Initializer side: the direct pass keeps the whole holder.
        Assert.Contains("GetElement(s)", caller.OutputCode);
        Assert.DoesNotContain("ref s.Value", caller.OutputCode);

        var compilation = CSharpCompilation.Create(
            "roi-local-decl-emit-" + Guid.NewGuid().ToString("N"),
            new[]
            {
                CSharpSyntaxTree.ParseText(caller.OutputCode, ParseOptions),
                CSharpSyntaxTree.ParseText(callee.OutputCode, ParseOptions),
                CSharpSyntaxTree.ParseText(ByRefSupportStub, ParseOptions),
            },
            GetReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        using var ms = new MemoryStream();
        var emit = compilation.Emit(ms);
        Assert.True(emit.Success,
            "emit failed:\n" + string.Join("\n",
                emit.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error))
            + "\n===== CALLER OUTPUT =====\n" + caller.OutputCode
            + "\n===== CALLEE OUTPUT =====\n" + callee.OutputCode);
        Assert.NotNull(AssemblyLoadContext.Default.LoadFromStream(new MemoryStream(ms.ToArray())));
    }
}
