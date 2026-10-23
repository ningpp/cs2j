using CSharpToJava.Core.MemberDesugar;
using CSharpToJava.Core.Pipeline;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace CSharpToJava.Tests;

/// <summary>
/// member-desugar override 链一致性（决策 8 回退闭环）：基类虚属性被展开成
/// get_X() 而派生 override 属性因声明级闸门（如仅限 Property 目标的特性无法
/// 路由，CS2J5113）被保留时，派生属性找不到可重写的属性槽 → CS0115（真实
/// SDK：XmlSchemaParticle.NameString 被展开、XmlSchemaElement.NameString 因
/// [XmlIgnore] 保留）。任一链成员不可展开时，整条链必须保守保留；反向
/// （派生展开、基类保留）同理，否则 override 语义悄悄丢失。
/// </summary>
public class MemberDesugarOverrideChainCoherenceTests
{
    private static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.Preview);

    private static (string Output, List<MemberDesugarDiagnostic> Diags) Rewrite(string source)
    {
        var tree = CSharpSyntaxTree.ParseText(source, ParseOptions, "Chain.cs");
        var compilation = CSharpCompilation.Create(
            "member-desugar-chain",
            new[] { tree },
            ProjectCompilationBuilder.GetMetadataReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));

        var preprocessor = new MemberDesugarPreprocessor();
        var stats = new MemberDesugarStatistics();
        var diags = new List<MemberDesugarDiagnostic>();
        var output = preprocessor.RewriteFile(
            source, compilation.GetSemanticModel(tree), stats, diags).OutputCode;
        return (output, diags);
    }

    private const string ChainSource = """
using System;

namespace Test.Support
{
    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Property)]
    public class XmlIgnoreAttribute : System.Attribute
    {
    }
}

namespace Test.Schema
{
    using Test.Support;

    public class XmlQualifiedName
    {
        private readonly string _s;
        public XmlQualifiedName(string s) { _s = s; }
        public override string ToString() { return _s; }
    }

    public abstract class XmlSchemaParticle
    {
        internal virtual string NameString
        {
            get { return string.Empty; }
        }
    }

    public class XmlSchemaElement : XmlSchemaParticle
    {
        private XmlQualifiedName _qualifiedName;

        [XmlIgnore]
        internal override string NameString
        {
            get { return _qualifiedName.ToString(); }
        }
    }

    public static class Consumer
    {
        public static string Read(XmlSchemaParticle p)
        {
            return p.NameString;
        }
    }
}
""";

    [Fact]
    public void BaseDesugaredDerivedRetained_ChainStaysCoherent_AndCompiles()
    {
        var (output, diags) = Rewrite(ChainSource);

        // 整条链保守保留：基类与派生的 NameString 都保持属性形态
        Assert.Contains("internal virtual string NameString", output, StringComparison.Ordinal);
        Assert.DoesNotContain("get_NameString", output, StringComparison.Ordinal);

        // 改写后的源码必须仍可编译（修复前：基类展开 get_NameString、派生保留
        // override 属性 → CS0115）
        var tree = CSharpSyntaxTree.ParseText(output, ParseOptions, "Chain.cs");
        var compilation = CSharpCompilation.Create(
            "member-desugar-chain-verify",
            new[] { tree },
            ProjectCompilationBuilder.GetMetadataReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        var errors = compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .Select(d => d.Descriptor.Id + ": " + d.GetMessage())
            .ToList();
        Assert.True(errors.Count == 0,
            "rewritten compilation has errors:\n" + string.Join("\n", errors));
    }
}
