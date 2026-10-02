using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.MemberDesugar.Properties;

/// <summary>FK-7（决策 8）：按语法目标符（AttributeListSyntax.Target）迁移特性。
/// <list type="bullet">
/// <item><c>[field: …]</c>：整张迁到合成字段（剥 target）；属性侧不得残留；</item>
/// <item><c>[get:]/[set:]/[init:]</c>：整张挂到对应 AccessorDeclarationSyntax
/// （剥 target），随后 C11 Expander 自然平移到 get_X/set_X 方法；</item>
/// <item><c>method:/return:/property:/默认</c>：原位保留，交 C11 PD-10
/// （C50 阶段不拦截、不判定 AttributeUsage）。</item>
/// </list>
/// 目标访问器缺失（如属性级表达式体上出现 [get:]）→ CS2J5128，plan 原子跳过。
/// 正常可编译基线不会触发 5128（该组合本身非法）。</summary>
internal static class FieldKeywordAttributeMigration
{
    private enum AccessorTarget { Get, Set, Init }

    public sealed class Result
    {
        public required PropertyDeclarationSyntax Declaration { get; init; }
        public required IReadOnlyList<AttributeListSyntax> FieldAttributeLists { get; init; }
        public required int MigratedAttributeCount { get; init; }
        public MemberDesugarDiagnosticCode? FailCode { get; init; }
        public string? FailReason { get; init; }
    }

    public static Result Migrate(PropertyDeclarationSyntax visitedDecl, string propertyName)
    {
        var fieldLists = new List<AttributeListSyntax>();
        var keptLists = new List<AttributeListSyntax>();
        var getLists = new List<AttributeListSyntax>();
        var setLists = new List<AttributeListSyntax>();
        var initLists = new List<AttributeListSyntax>();
        var migrated = 0;

        foreach (var list in visitedDecl.AttributeLists)
        {
            switch (list.Target?.Identifier.ValueText)
            {
                case "field":
                    fieldLists.Add(StripTarget(list));
                    migrated += list.Attributes.Count;
                    break;
                case "get":
                    getLists.Add(StripTarget(list));
                    break;
                case "set":
                    setLists.Add(StripTarget(list));
                    break;
                case "init":
                    initLists.Add(StripTarget(list));
                    break;
                default:
                    // method:/return:/property:/无 target：原位保留（PD-10 接管）。
                    keptLists.Add(list);
                    break;
            }
        }

        var result = visitedDecl.WithAttributeLists(SyntaxFactory.List(keptLists));

        if (getLists.Count > 0 || setLists.Count > 0 || initLists.Count > 0)
        {
            if (result.AccessorList == null)
            {
                return Fail(propertyName,
                    "accessor-targeted attribute ([get:]/[set:]/[init:]) on a property " +
                    "without an accessor list");
            }

            var accessors = result.AccessorList.Accessors;
            bool Has(SyntaxKind kind) => accessors.Any(a => a.IsKind(kind));

            // 目标访问器不存在（如只有 get 的属性上出现 [set:]）：可编译基线本身会报
            // CS0641/CS7014 类错误；此处仍显式 5128，不静默丢特性。
            if ((getLists.Count > 0 && !Has(SyntaxKind.GetAccessorDeclaration))
                || (setLists.Count > 0 && !Has(SyntaxKind.SetAccessorDeclaration))
                || (initLists.Count > 0 && !Has(SyntaxKind.InitAccessorDeclaration)))
            {
                return Fail(propertyName,
                    "accessor-targeted attribute has no matching accessor declaration");
            }

            var newAccessors = new List<AccessorDeclarationSyntax>();
            foreach (var acc in accessors)
            {
                var extra = acc.Kind() switch
                {
                    SyntaxKind.GetAccessorDeclaration => getLists,
                    SyntaxKind.SetAccessorDeclaration => setLists,
                    SyntaxKind.InitAccessorDeclaration => initLists,
                    _ => new List<AttributeListSyntax>(),
                };
                newAccessors.Add(extra.Count == 0
                    ? acc
                    : acc.WithAttributeLists(SyntaxFactory.List(
                            acc.AttributeLists.Concat(extra)))
                        .WithAdditionalAnnotations(MemberDesugarSyntaxFacts.RewriteAnnotation));
            }

            result = result.WithAccessorList(
                result.AccessorList.WithAccessors(SyntaxFactory.List(newAccessors)));
        }

        return new Result
        {
            Declaration = result,
            FieldAttributeLists = fieldLists,
            MigratedAttributeCount = migrated,
        };
    }

    /// <summary>剥除目标符：<c>[field: NonSerialized]</c> → <c>[NonSerialized]</c>。
    /// 目标 token 的 trivia 一并随 target 移除；括号本身定界，空白由 Formatter 修复。</summary>
    private static AttributeListSyntax StripTarget(AttributeListSyntax list)
        => list.WithTarget(null)
            .WithAdditionalAnnotations(MemberDesugarSyntaxFacts.RewriteAnnotation);

    private static Result Fail(string propertyName, string detail)
        => new()
        {
            Declaration = null!,
            FieldAttributeLists = Array.Empty<AttributeListSyntax>(),
            MigratedAttributeCount = 0,
            FailCode = MemberDesugarDiagnosticCode.FieldKeywordAttributeFailed,
            FailReason = FieldKeywordDiagnostics.AttributeMigrationFailed(propertyName, detail),
        };
}
