using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.MemberDesugar.Records;

/// <summary>
/// RC-1/RC-2 克隆工厂注入（设计 §4.3/决策 3）：按 <see cref="RecordClonePlan"/> 期算定的
/// 工厂形态与统一定名，在目标 record 声明部件的成员表末尾发射一个工厂方法——
/// abstract record 发 <c>public abstract T Cs2jWithClone();</c>；
/// 非密封具体 record 发 <c>public virtual T Cs2jWithClone() =&gt; new T(this);</c>；
/// 派生发 <c>public override D Cs2jWithClone() =&gt; new D(this);</c>；
/// 密封 record 发非虚普通方法。方法体 <c>new T(this)</c> 调复制 ctor（编译器合成
/// protected 或用户自定义均自动接管，零特殊处理）。
/// 本类只做纯语法发射，不做任何符号判定（结论全部来自 plan）；声明自身的 XML doc/
/// trivia/特性原样保留，新成员挂族 RewriteAnnotation，间距交给族 FormatAndValidate。
/// </summary>
internal static class RecordDeclarationRewriter
{
    /// <summary>
    /// 为指定声明部件构造克隆工厂；该部件不是 plan 选定的注入部件 / 工厂已存在 /
    /// 闸门形态不发工厂时返回 null。
    /// </summary>
    public static MethodDeclarationSyntax? TryBuildFactory(
        RecordDeclarationSyntax decl,
        RecordClonePlan plan,
        MemberDesugarOptions options,
        MemberDesugarStatistics stats,
        List<MemberDesugarDiagnostic> diagnostics,
        string filePath)
    {
        if (plan.IsOrphan || !plan.FactoryRequired || plan.FactoryAlreadyExists
            || plan.FactoryName is not { Length: > 0 } factoryName
            || plan.FactoryForm is not { } form
            || plan.FactoryInsertSpanStart is not { } targetSpan
            || decl.Identifier.SpanStart != targetSpan)
        {
            return null;
        }

        // 防御性幂等：plan 期已按符号识别同签名工厂（首轮产物/用户手写）；此处再按
        // 语法挡一层，任何情况下同部件不重发同名方法。
        if (decl.Members.OfType<MethodDeclarationSyntax>()
            .Any(m => m.Identifier.ValueText == factoryName))
        {
            return null;
        }

        var typeName = BuildSelfTypeName(decl);
        var modifiers = BuildModifiers(form);

        MethodDeclarationSyntax method;
        if (form == RecordFactoryForm.Abstract)
        {
            method = SyntaxFactory.MethodDeclaration(
                    default,
                    modifiers,
                    typeName,
                    null,
                    SyntaxFactory.Identifier(factoryName),
                    null,
                    SyntaxFactory.ParameterList(),
                    SyntaxFactory.List<TypeParameterConstraintClauseSyntax>(),
                    null,
                    null)
                .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken));
        }
        else
        {
            // new T(this) / new T<...>(this)：复制 ctor 由 record 语言保证存在且可从
            // 类型内部访问（protected 合成 ctor 同样可见），派生 ctor 强制链基类复制 ctor。
            var copyCall = SyntaxFactory.ObjectCreationExpression(
                typeName.WithoutTrivia(),
                SyntaxFactory.ArgumentList(SyntaxFactory.SingletonSeparatedList(
                    SyntaxFactory.Argument(SyntaxFactory.ThisExpression()))),
                null);
            method = SyntaxFactory.MethodDeclaration(
                    default,
                    modifiers,
                    typeName,
                    null,
                    SyntaxFactory.Identifier(factoryName),
                    null,
                    SyntaxFactory.ParameterList(),
                    SyntaxFactory.List<TypeParameterConstraintClauseSyntax>(),
                    null,
                    SyntaxFactory.ArrowExpressionClause(copyCall))
                .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken));
        }

        method = method
            .WithLeadingTrivia(
                SyntaxFactory.TriviaList(SyntaxFactory.ElasticCarriageReturnLineFeed))
            .WithAdditionalAnnotations(MemberDesugarSyntaxFacts.RewriteAnnotation);

        stats.CloneFactoriesSynthesized++;
        if (options.ReportInfo)
        {
            diagnostics.Add(new MemberDesugarDiagnostic(
                MemberDesugarSeverity.Info,
                MemberDesugarDiagnosticCode.CloneFactorySynthesized,
                RecordDiagnostics.CloneFactorySynthesized(
                    plan.RecordType?.ToDisplayString() ?? factoryName, form, factoryName),
                filePath,
                decl.Identifier.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                0,
                plan.RecordType?.Name));
        }

        return method;
    }

    /// <summary>工厂修饰符（决策 3）：abstract/virtual/override/非虚（密封）。</summary>
    private static SyntaxTokenList BuildModifiers(RecordFactoryForm form)
    {
        var kind = form switch
        {
            RecordFactoryForm.Abstract => SyntaxKind.AbstractKeyword,
            RecordFactoryForm.Virtual => SyntaxKind.VirtualKeyword,
            RecordFactoryForm.Override => SyntaxKind.OverrideKeyword,
            _ => (SyntaxKind?)null,
        };
        var tokens = new List<SyntaxToken> { SyntaxFactory.Token(SyntaxKind.PublicKeyword) };
        if (kind is { } k) tokens.Add(SyntaxFactory.Token(k));
        return SyntaxFactory.TokenList(tokens);
    }

    /// <summary>本类型在自身体内的自命名类型：带类型形参（Box&lt;T&gt;），无需任何限定。</summary>
    private static TypeSyntax BuildSelfTypeName(RecordDeclarationSyntax decl)
    {
        var identifier = SyntaxFactory.Identifier(decl.Identifier.ValueText);
        if (decl.TypeParameterList is not { Parameters.Count: > 0 } typeParameters)
            return SyntaxFactory.IdentifierName(identifier);

        var args = typeParameters.Parameters
            .Select(p => (TypeSyntax)SyntaxFactory.IdentifierName(
                SyntaxFactory.Identifier(p.Identifier.ValueText)));
        return SyntaxFactory.GenericName(
            identifier,
            SyntaxFactory.TypeArgumentList(SyntaxFactory.SeparatedList(args)));
    }
}
