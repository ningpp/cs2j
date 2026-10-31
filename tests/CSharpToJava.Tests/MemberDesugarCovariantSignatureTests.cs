using CSharpToJava.Core.MemberDesugar;
using CSharpToJava.Core.MemberDesugar.Signatures;
using CSharpToJava.Core.Pipeline;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace CSharpToJava.Tests;

/// <summary>
/// C46 CR 协变链识别：override 与基方法的返回类型若在「方法类型参数按序号对应」
/// 的意义上是同一签名，则不属于协变。真实 SDK 的 MemberAccessor 泛型方法
/// （CreateAddMethodDelegate&lt;TCollection&gt; 返回 Action&lt;TCollection, object?&gt;）
/// 派生 override 的返回类型逐字相同，却因两侧类型参数是不同符号而被误判为协变，
/// 触发 CS2J5195 Error 中断整条 C#→C# 预处理链。
/// </summary>
public class MemberDesugarCovariantSignatureTests
{
    private static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.Preview);

    private static (string Output, bool Changed, MemberDesugarStatistics Stats,
        List<MemberDesugarDiagnostic> Diags) Rewrite(string source)
    {
        var tree = CSharpSyntaxTree.ParseText(source, ParseOptions, "Covariant.cs");
        var compilation = CSharpCompilation.Create(
            "member-desugar-covariant",
            new[] { tree },
            ProjectCompilationBuilder.GetMetadataReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));

        var options = new MemberDesugarOptions
        {
            Signatures = new SignatureOptions { Enabled = true },
        };
        var preprocessor = new MemberDesugarPreprocessor(options);
        var stats = new MemberDesugarStatistics();
        var diags = new List<MemberDesugarDiagnostic>();
        var result = preprocessor.RewriteFile(
            source, compilation.GetSemanticModel(tree), stats, diags);
        return (result.OutputCode, result.Changed, stats, diags);
    }

    [Fact]
    public void SameSignatureGenericMethodOverride_IsNotCovariant_NoCs2j5195()
    {
        const string src = """
            using System;
            public abstract class MemberAccessor
            {
                public abstract Action<TCollection, object?> CreateAddMethodDelegate<TCollection>();
                public abstract Func<object, TProperty> CreatePropertyGetter<TProperty>();
            }
            public sealed class ReflectionMemberAccessor : MemberAccessor
            {
                public override Action<TCollection, object?> CreateAddMethodDelegate<TCollection>()
                    => null!;
                public override Func<object, TProperty> CreatePropertyGetter<TProperty>()
                    => null!;
            }
            """;
        var (output, changed, stats, diags) = Rewrite(src);

        Assert.Equal(src, output);   // 非协变：原样保留、字节一致
        Assert.False(changed);

        Assert.Equal(0, stats.SignatureCovariantChains);
        Assert.DoesNotContain(diags, d =>
            d.Code == MemberDesugarDiagnosticCode.SignatureValueTypeCovariant);
        Assert.DoesNotContain(diags, d => d.Severity == MemberDesugarSeverity.Error);
    }
}
