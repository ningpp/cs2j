using CSharpToJava.Core.MemberDesugar;
using CSharpToJava.Core.Pipeline;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace CSharpToJava.Tests;

/// <summary>
/// member-desugar 特性类豁免：System.Attribute 派生类上的属性被其他源文件以
/// 特性命名实参形式按名引用（如 <c>[XmlAttribute("source", DataType = "anyURI")]</c>），
/// 这类站点在语法上是 NameEquals，无法改写成 get_/set_ 方法调用。属性一旦被
/// 去糖（真实 SDK：XmlAttributeAttribute.DataType 等），命名实参悬空 → CS0246。
/// 特性类属性必须整体保留（最终 Java 侧属性照常落为方法，特性用法整体被丢弃，
/// 中间链尾的 C# 编译是唯一约束）。
/// </summary>
public class MemberDesugarAttributeClassPropertyTests
{
    private static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.Preview);

    private static (string DeclOutput, string UseOutput) RewriteTwoFiles(
        string declSource, string useSource)
    {
        var declTree = CSharpSyntaxTree.ParseText(declSource, ParseOptions, "Attr.cs");
        var useTree = CSharpSyntaxTree.ParseText(useSource, ParseOptions, "Use.cs");
        var compilation = CSharpCompilation.Create(
            "member-desugar-attr",
            new[] { declTree, useTree },
            ProjectCompilationBuilder.GetMetadataReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));

        var preprocessor = new MemberDesugarPreprocessor();
        var stats = new MemberDesugarStatistics();
        var diags = new List<MemberDesugarDiagnostic>();
        var declOut = preprocessor.RewriteFile(
            declSource, compilation.GetSemanticModel(declTree), stats, diags).OutputCode;
        var useOut = preprocessor.RewriteFile(
            useSource, compilation.GetSemanticModel(useTree), stats, diags).OutputCode;
        return (declOut, useOut);
    }

    private const string AttrDecl = """
using System;

namespace dotnet.xml.serialization
{
    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Property)]
    public class XmlAttributeAttribute : System.Attribute
    {
        private string _attributeName;
        private string _dataType;

        public XmlAttributeAttribute() { }
        public XmlAttributeAttribute(string attributeName) { _attributeName = attributeName; }

        public string AttributeName
        {
            get { return _attributeName; }
            set { _attributeName = value; }
        }

        public string DataType
        {
            get { return _dataType; }
            set { _dataType = value; }
        }
    }
}
""";

    private const string AttrUse = """
namespace dotnet.xml.schema
{
    using dotnet.xml.serialization;

    public class SampleNode
    {
        [XmlAttribute("lang", DataType = "language")]
        public string Source { get; set; }

        [XmlAttribute("source", DataType = "anyURI")]
        public string Lang { get; set; }
    }
}
""";

    [Fact]
    public void AttributeClassProperty_Retained_NamedArgumentStillBinds()
    {
        var (declOut, useOut) = RewriteTwoFiles(AttrDecl, AttrUse);

        // 特性类属性必须保留属性形态，不得展开为 get_/set_ 方法
        Assert.Contains("public string DataType", declOut, StringComparison.Ordinal);
        Assert.DoesNotContain("get_DataType", declOut, StringComparison.Ordinal);
        Assert.DoesNotContain("set_DataType", declOut, StringComparison.Ordinal);

        // 普通类（SampleNode.Source）照常去糖，证明重写器确实运行
        Assert.Contains("get_Source", useOut, StringComparison.Ordinal);

        // 改名后双文件必须仍能编译：特性命名实参 DataType = "anyURI" 有属性可绑
        AssertCompiles(declOut, useOut);
    }

    private static void AssertCompiles(string declOut, string useOut)
    {
        var trees = new[]
        {
            CSharpSyntaxTree.ParseText(declOut, ParseOptions, "Attr.cs"),
            CSharpSyntaxTree.ParseText(useOut, ParseOptions, "Use.cs"),
        };
        var compilation = CSharpCompilation.Create(
            "member-desugar-attr-verify",
            trees,
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
