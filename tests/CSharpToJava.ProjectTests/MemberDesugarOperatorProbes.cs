using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.ProjectTests;

/// <summary>M0 探针（设计决策 1/3、§9-M0）：只记录语言事实，不 gate 命名决策。</summary>
public class MemberDesugarOperatorProbes
{
    private static CSharpCompilation Compile(string src) => CSharpCompilation.Create(
        "probe",
        new[] { CSharpSyntaxTree.ParseText(src, new CSharpParseOptions(LanguageVersion.Preview)) },
        new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) },
        new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

    [Fact]
    public void Probe_OpMetadataNameAsMethod_IsLegalInCSharp()
    {
        // 决策 1 评审修订：op_* 命名的充分依据是零漂移对齐；本探针只钉死「手写是否合法」事实。
        var comp = Compile("""
            public class C
            {
                public static int op_Addition(int a, int b) => a + b;
            }
            """);
        var errs = comp.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        Assert.True(errs.Count == 0,
            "op_Addition as plain method rejected: " + string.Join("; ", errs.Select(e => e.Id)));
        var tree = comp.SyntaxTrees.Single();
        Assert.Contains(tree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>(),
            m => m.Identifier.ValueText == "op_Addition");
    }

    [Fact]
    public void Probe_ImplicitAndExplicitSamePair_IsRejectedByCSharp()
    {
        var comp = Compile("""
            public class C
            {
                public static implicit operator int(C c) => 1;
                public static explicit operator int(C c) => 2;
            }
            """);
        var errs = comp.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        Assert.NotEmpty(errs); // 预期 CS0557 duplicate conversion —— 同 (S,T) 对两版共存非法
        Assert.Contains(errs, e => e.Id == "CS0557");
    }
}
