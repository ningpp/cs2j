using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.MemberDesugar.Properties;

/// <summary>FK-2/FK-6（决策 2/6/7）：C50 编排入口。对一个已计划的 field-backed 属性，
/// 在 C11 <see cref="PropertyDeclarationExpander"/> 之前把 field 形态归一为
/// 「显式 private 后备字段 + 全访问器有体/无 field 主表达式/无属性初始化器」的普通属性，
/// 随后 C11 复用既有字段名（plan.BackingFieldName）与展开路径，不重复发字段。
/// 失败时不产出任何改动并返回跳过码（plan 原子不动，严禁文本替换兜底）。</summary>
internal static class FieldKeywordNormalizer
{
    private static readonly SymbolDisplayFormat MinimalNullableFormat =
        SymbolDisplayFormat.MinimallyQualifiedFormat
            .WithMiscellaneousOptions(
                SymbolDisplayFormat.MinimallyQualifiedFormat.MiscellaneousOptions
                | SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);

    /// <returns>true=归一成功（normalized/field 产出）；false=已发跳过诊断，调用方原样保留。</returns>
    public static bool TryNormalize(
        PropertyDeclarationSyntax decl,
        PropertyPlan plan,
        SemanticModel model,
        MemberDesugarOptions options,
        MemberDesugarStatistics stats,
        List<MemberDesugarDiagnostic> diagnostics,
        string filePath,
        out PropertyDeclarationSyntax normalized,
        out FieldDeclarationSyntax field)
    {
        normalized = null!;
        field = null!;

        // 改写期重解析后再判一次（与分析期同算法，防止跨阶段漂移）。
        var resolve = FieldKeywordResolver.Resolve(decl, plan.Property, model, options);
        if (!resolve.Success)
        {
            diagnostics.Add(new MemberDesugarDiagnostic(
                options.Strict ? MemberDesugarSeverity.Error : MemberDesugarSeverity.Warning,
                resolve.FailCode!.Value,
                resolve.FailReason!,
                filePath,
                decl.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                decl.GetLocation().GetLineSpan().StartLinePosition.Character + 1,
                plan.SymbolName));
            return false;
        }

        var info = resolve.Info!;
        var fieldName = plan.BackingFieldName
            ?? throw new InvalidOperationException(
                $"C50: field-backed property '{plan.SymbolName}' has no backing field name.");

        var body = FieldKeywordBodyRewriter.Rewrite(decl, fieldName);

        // FK-7 接缝（决策 8）：commit c 接入 [field:] 特性整组迁移。
        var fieldAttributeLists = Array.Empty<AttributeListSyntax>();
        const int attributeCount = 0;
        var strippedDecl = body.Declaration;

        var fieldDecl = BuildField(
            decl, plan, info, fieldName, body.MovedInitializer, fieldAttributeLists, model);

        normalized = strippedDecl;
        field = fieldDecl;

        stats.FieldKeywordProperties++;
        stats.FieldKeywordAutoAccessorsExpanded += body.AutoAccessorsExpanded;
        stats.FieldReferencesRewritten += body.ReferencesRewritten;
        stats.FieldTargetAttributesMigrated += attributeCount;
        if (body.MovedInitializer != null) stats.FieldInitializersMoved++;
        // 字段由 C50 合成、C11 Expander 复用：计入族级「字段生成」口径。
        stats.BackingFieldsGenerated++;

        if (options.ReportInfo)
        {
            diagnostics.Add(new MemberDesugarDiagnostic(
                MemberDesugarSeverity.Info,
                MemberDesugarDiagnosticCode.FieldKeywordNormalized,
                FieldKeywordDiagnostics.Normalized(
                    plan.DeclName, fieldName, info.IsStatic,
                    EmitReadOnly(info, plan),
                    body.AutoAccessorsExpanded, body.ReferencesRewritten,
                    body.MovedInitializer != null),
                filePath,
                (plan.Property.Locations.FirstOrDefault(l => l.IsInSource)
                    ?.GetLineSpan().StartLinePosition.Line ?? 0) + 1,
                0,
                plan.SymbolName));
        }
        return true;
    }

    private static FieldDeclarationSyntax BuildField(
        PropertyDeclarationSyntax decl,
        PropertyPlan plan,
        FieldKeywordInfo info,
        string fieldName,
        EqualsValueClauseSyntax? initializer,
        IReadOnlyList<AttributeListSyntax> attributeLists,
        SemanticModel model)
    {
        var mods = new List<SyntaxToken>
        {
            SyntaxFactory.Token(SyntaxKind.PrivateKeyword),
        };
        if (info.IsStatic)
            mods.Add(SyntaxFactory.Token(SyntaxKind.StaticKeyword));
        // FK-2（决策 2）：static/readonly 读编译器字段符号；init 放宽为普通 set 后字段必须
        // 可变（与 C11 PD-3 对齐：SetMethod 含 init 访问器，故加 SetMethod==null 守卫）。
        // Roslyn 5.0 实测：field-keyword 形态（含 get-only 纯读）合成字段 IL 层均非 initonly，
        // 与 auto { get; } 不同；以符号事实为准。
        if (EmitReadOnly(info, plan))
            mods.Add(SyntaxFactory.Token(SyntaxKind.ReadOnlyKeyword));

        // FK-2：类型/可空性直读字段符号（MinimallyQualified 支持泛型参数与特殊类型）。
        var typeText = info.BackingField.Type.ToMinimalDisplayString(
            model, decl.SpanStart, MinimalNullableFormat);
        var typeSyntax = SyntaxFactory.ParseTypeName(typeText);

        var declarator = SyntaxFactory.VariableDeclarator(SyntaxFactory.Token(
            SyntaxTriviaList.Empty, SyntaxKind.IdentifierToken, fieldName, fieldName,
            SyntaxTriviaList.Empty));
        if (initializer != null)
            declarator = declarator.WithInitializer(initializer);

        var declaration = SyntaxFactory.VariableDeclaration(typeSyntax)
            .WithVariables(SyntaxFactory.SeparatedList(new[] { declarator }));

        return (FieldDeclarationSyntax)SyntaxFactory.FieldDeclaration(
                SyntaxFactory.List(attributeLists),
                SyntaxFactory.TokenList(mods),
                declaration,
                SyntaxFactory.Token(SyntaxKind.SemicolonToken))
            .WithLeadingTrivia(SyntaxFactory.TriviaList(SyntaxFactory.ElasticSpace))
            .WithAdditionalAnnotations(MemberDesugarSyntaxFacts.RewriteAnnotation);
    }

    private static bool EmitReadOnly(FieldKeywordInfo info, PropertyPlan plan)
        => info.SymbolIsReadOnly && plan.Property.SetMethod == null;
}
