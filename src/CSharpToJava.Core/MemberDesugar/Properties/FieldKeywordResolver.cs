using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.MemberDesugar.Properties;

/// <summary>C50 plan 挂载点：一个 field-backed 属性一条（设计 §3.3/§4.4-1）。
/// 字段事实全部读编译器合成的 <see cref="IFieldSymbol"/>（FK-2，决策 2/17）。</summary>
internal sealed class FieldKeywordInfo
{
    /// <summary>编译器为该属性合成的后备字段符号（IsImplicitlyDeclared=true，
    /// AssociatedSymbol 为该 IPropertySymbol）。</summary>
    public required IFieldSymbol BackingField { get; init; }

    /// <summary>合成显式字段是否 static（直读符号，决策 2）。</summary>
    public bool IsStatic => BackingField.IsStatic;

    /// <summary>合成字段的符号级 readonly 事实（注意：init 放宽为 set 后产物字段必须
    /// 可变，实际发射条件见 <see cref="FieldKeywordNormalizer"/>，与 C11 PD-3 对齐）。</summary>
    public bool SymbolIsReadOnly => BackingField.IsReadOnly;
}

/// <summary>FK-1 语义识别：判定 field-backed、枚举命中节点、读字段事实、容器闸门。
/// 纯语义判定，禁止按标识符文本替换（决策 1/15）。</summary>
internal static class FieldKeywordResolver
{
    /// <summary>语法快判：属性声明（含访问器体与属性级表达式体）内是否出现
    /// <see cref="SyntaxKind.FieldExpression"/>。C#14 GA 下未转义 field 解析为
    /// FieldExpressionSyntax；<c>@field</c> 是 IdentifierNameSyntax，天然不命中。</summary>
    public static bool HasFieldKeywordSyntax(PropertyDeclarationSyntax decl)
        => decl.DescendantNodes().Any(n => n.IsKind(SyntaxKind.FieldExpression));

    public readonly record struct ResolveResult(
        bool Success, FieldKeywordInfo? Info,
        MemberDesugarDiagnosticCode? FailCode, string? FailReason)
    {
        public static ResolveResult Ok(FieldKeywordInfo info) => new(true, info, null, null);
        public static ResolveResult Fail(MemberDesugarDiagnosticCode code, string reason)
            => new(false, null, code, reason);
    }

    /// <summary>对单个属性做 FK-1 判定。调用方须已完成 partial(5114)/ref(5115)/
    /// static-abstract(5116) 前置闸门。</summary>
    public static ResolveResult Resolve(
        PropertyDeclarationSyntax decl,
        IPropertySymbol prop,
        SemanticModel model,
        MemberDesugarOptions options)
    {
        // FK-8 容器闸门（决策 11；探针 FK-P3 定案）：
        // 接口实例 DIM field 非法（CS0525 基线错误）→ CS2J5127；
        // 接口静态 field-backed 属性经探针证实合法（接口可含静态字段），正常收编
        // （附裁决 4：M2 直接收编，不长期保留 5127）。
        if (prop.ContainingType is { TypeKind: TypeKind.Interface } && !prop.IsStatic)
        {
            return ResolveResult.Fail(
                MemberDesugarDiagnosticCode.FieldKeywordUnsupportedContainer,
                FieldKeywordDiagnostics.UnsupportedContainer(
                    prop.Name, prop.ContainingType.ToDisplayString()));
        }

        var hits = decl.DescendantNodes().OfType<FieldExpressionSyntax>().ToList();
        if (hits.Count == 0)
            return ResolveResult.Fail(
                MemberDesugarDiagnosticCode.FieldKeywordSafetyNet,
                FieldKeywordDiagnostics.SafetyNet(prop.Name, "no field token found"));

        IFieldSymbol? backing = null;
        foreach (var fe in hits)
        {
            // 决策 18：访问器体内嵌表达式树 lambda 引用 field，v1 同族保守跳过
            //（CS2J5117，overrides.force 可解除，与站点闸门同形）。
            if (MemberDesugarSyntaxFacts.IsInsideExpressionTree(fe, model)
                && !MatchesSet(options.ForceQualifiedNames, prop))
            {
                return ResolveResult.Fail(
                    MemberDesugarDiagnosticCode.ExpressionTreeSite,
                    $"property '{prop.Name}' references the backing field inside an " +
                    "expression-tree lambda; rewriting would change the tree shape; " +
                    "the property declaration and all of its use sites were left unchanged " +
                    "(overrides.force lifts this).");
            }

            if (model.GetSymbolInfo(fe).Symbol is not IFieldSymbol fs
                || !fs.IsImplicitlyDeclared
                || !SymbolEqualityComparer.Default.Equals(fs.AssociatedSymbol, prop))
            {
                // FK-1 安全网（CS2J5126）：宁原子跳过，严禁退化为文本替换。
                return ResolveResult.Fail(
                    MemberDesugarDiagnosticCode.FieldKeywordSafetyNet,
                    FieldKeywordDiagnostics.SafetyNet(prop.Name,
                        "GetSymbolInfo did not return the associated implicit backing field"));
            }
            backing ??= fs;
        }

        return ResolveResult.Ok(new FieldKeywordInfo { BackingField = backing! });
    }

    private static bool MatchesSet(IReadOnlySet<string> names, IPropertySymbol prop)
    {
        var type = prop.ContainingType?.ToDisplayString();
        return names.Contains($"{type}::{prop.Name}")
            || (type != null && names.Contains(type));
    }
}
