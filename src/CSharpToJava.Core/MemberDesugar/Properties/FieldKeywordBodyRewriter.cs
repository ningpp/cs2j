using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.MemberDesugar.Properties;

/// <summary>FK-3/FK-4/FK-5（决策 4/5/7）：声明侧体改写。
/// <list type="bullet">
/// <item>FieldExpressionSyntax 整体替换为后备字段 IdentifierName（保留 token trivia）；</item>
/// <item>无体 auto 访问器按 C11 PD-1 同构展开（get; → get => f;，set;/init; → … => f = value;）；</item>
/// <item>属性初始化器从声明摘除（值由调用方平移到字段声明，决策 7，不触发 init/set）。</item>
/// </list>
/// 只改 Resolver 命中的形态；属性级表达式体（FK-5）仅做节点替换，保最小改形。</summary>
internal static class FieldKeywordBodyRewriter
{
    public sealed class Result
    {
        public required PropertyDeclarationSyntax Declaration { get; init; }
        public required int ReferencesRewritten { get; init; }
        public required int AutoAccessorsExpanded { get; init; }
        /// <summary>从属性上摘下来的原始初始化器节点（null 表示无初始化器）。</summary>
        public EqualsValueClauseSyntax? MovedInitializer { get; init; }
    }

    public static Result Rewrite(PropertyDeclarationSyntax decl, string fieldName)
    {
        // FK-3：FieldExpressionSyntax（C#14 GA 节点）→ 同名契约字段标识符。
        var hits = decl.DescendantNodes().OfType<FieldExpressionSyntax>().ToList();
        var replaced = hits.Count == 0
            ? decl
            : decl.ReplaceNodes(hits, (original, _) => BuildFieldIdentifier(original, fieldName));

        // FK-4：无体 auto 访问器展开（保留访问器修饰符，如 private set;）。
        var expanded = 0;
        if (replaced.AccessorList != null)
        {
            var accessors = replaced.AccessorList.Accessors;
            var anyChanged = false;
            var newList = new List<AccessorDeclarationSyntax>(accessors.Count);
            foreach (var acc in accessors)
            {
                if (acc.Body != null || acc.ExpressionBody != null)
                {
                    newList.Add(acc);
                    continue;
                }
                anyChanged = true;
                expanded++;
                newList.Add(ExpandAutoAccessor(acc, fieldName));
            }
            if (anyChanged)
                replaced = replaced.WithAccessorList(replaced.AccessorList.WithAccessors(
                    SyntaxFactory.List(newList)));
        }

        // FK-6（决策 7）：摘初始化器。
        EqualsValueClauseSyntax? initializer = replaced.Initializer;
        if (initializer != null)
            replaced = replaced.WithInitializer(null);

        return new Result
        {
            Declaration = (PropertyDeclarationSyntax)replaced
                .WithAdditionalAnnotations(MemberDesugarSyntaxFacts.RewriteAnnotation),
            ReferencesRewritten = hits.Count,
            AutoAccessorsExpanded = expanded,
            MovedInitializer = initializer,
        };
    }

    /// <summary>把 field token 的 trivia 平移到普通标识符（@field 不会走到这里）。</summary>
    private static IdentifierNameSyntax BuildFieldIdentifier(FieldExpressionSyntax node, string fieldName)
    {
        var token = SyntaxFactory.Token(
            node.Token.LeadingTrivia,
            SyntaxKind.IdentifierToken,
            fieldName,
            fieldName,
            node.Token.TrailingTrivia);
        return SyntaxFactory.IdentifierName(token)
            .WithAdditionalAnnotations(MemberDesugarSyntaxFacts.RewriteAnnotation);
    }

    private static AccessorDeclarationSyntax ExpandAutoAccessor(
        AccessorDeclarationSyntax acc, string fieldName)
    {
        ExpressionSyntax rhs = acc.IsKind(SyntaxKind.GetAccessorDeclaration)
            ? SyntaxFactory.IdentifierName(fieldName)
            : SyntaxFactory.AssignmentExpression(
                SyntaxKind.SimpleAssignmentExpression,
                SyntaxFactory.IdentifierName(fieldName),
                SyntaxFactory.IdentifierName("value"));

        var arrow = SyntaxFactory.ArrowExpressionClause(
                SyntaxFactory.Token(SyntaxKind.MinusGreaterThanToken)
                    .WithLeadingTrivia(SyntaxFactory.ElasticSpace)
                    .WithTrailingTrivia(SyntaxFactory.ElasticSpace),
                rhs)
            .WithLeadingTrivia(SyntaxFactory.ElasticSpace);

        var semicolon = acc.SemicolonToken.IsKind(SyntaxKind.SemicolonToken)
            ? acc.SemicolonToken
            : SyntaxFactory.Token(SyntaxKind.SemicolonToken);

        return acc
            .WithExpressionBody(arrow)
            .WithSemicolonToken(semicolon)
            .WithAdditionalAnnotations(MemberDesugarSyntaxFacts.RewriteAnnotation);
    }
}
