using CSharpToJava.Core.MemberDesugar.Properties;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.MemberDesugar.Indexers;

/// <summary>IX-1..IX-5：把一个索引器声明展开为 1~2 个 MethodDeclarationSyntax。
/// 修饰符规则镜像 AccessorsToMethods；ref 签名保留（D9）；init 放宽（D8，CS2J5132）；
/// [IndexerName] 移除（D3，CS2J5131）；[DefaultMember] 类型级保留盘点（CS2J5133）。
/// 使用点改写在 <see cref="IndexerUseSiteRewriter"/>。</summary>
internal sealed class IndexerDeclarationExpander
{
    private readonly SemanticModel _model;
    private readonly MemberDesugarOptions _options;
    private readonly MemberDesugarStatistics _stats;
    private readonly List<MemberDesugarDiagnostic> _diagnostics;
    private readonly string _filePath;

    public IndexerDeclarationExpander(
        SemanticModel model, MemberDesugarOptions options, MemberDesugarStatistics stats,
        List<MemberDesugarDiagnostic> diagnostics, string filePath)
    {
        _model = model;
        _options = options;
        _stats = stats;
        _diagnostics = diagnostics;
        _filePath = filePath;
    }

    /// <summary><paramref name="bindingDecl"/> 是语义绑定（特性路由）用的原始树内声明；
    /// <paramref name="decl"/> 的访问器体已过使用点改写（脱离语法树的拷贝）。</summary>
    public IReadOnlyList<MemberDeclarationSyntax> Expand(
        IndexerDeclarationSyntax decl, IndexerPlan plan, IndexerDeclarationSyntax? bindingDecl = null)
    {
        bindingDecl ??= decl;
        var prop = plan.Indexer;
        var inInterface = prop.ContainingType is { TypeKind: TypeKind.Interface };
        var isExplicit = decl.ExplicitInterfaceSpecifier != null;

        var accessors = decl.AccessorList?.Accessors;
        var getAccessor = accessors?.FirstOrDefault(a =>
            a.IsKind(SyntaxKind.GetAccessorDeclaration));
        var setAccessor = accessors?.FirstOrDefault(a =>
            a.IsKind(SyntaxKind.SetAccessorDeclaration)
            || a.IsKind(SyntaxKind.InitAccessorDeclaration));
        // C# 6 起支持声明级表达式体索引器（T this[...] => e;）：箭头子句挂在声明上，
        // 合成到 get_Item（镜像 C11 表达式体属性）。
        var declExpressionBodied = decl.AccessorList == null && decl.ExpressionBody != null;

        if (plan.MetadataName != "Item")
            _diagnostics.Add(new MemberDesugarDiagnostic(
                MemberDesugarSeverity.Info, MemberDesugarDiagnosticCode.IndexerNameAdopted,
                $"indexer name '{prop.MetadataName}' adopted from [IndexerName]; the attribute " +
                "was removed from the declaration",
                _filePath, LineOf(bindingDecl), ColOf(bindingDecl), prop.MetadataName));
        if (plan.HasInitAccessor)
        {
            _stats.InitAccessorsWidened++;
            _diagnostics.Add(new MemberDesugarDiagnostic(
                MemberDesugarSeverity.Info, MemberDesugarDiagnosticCode.IndexerInitWidened,
                "init-only indexer accessor widened to an ordinary set_Item method",
                _filePath, LineOf(bindingDecl), ColOf(bindingDecl), prop.MetadataName));
        }
        if (isExplicit) _stats.ExplicitInterfaceIndexers++;
        EmitDefaultMemberNote(prop, bindingDecl);

        var results = new List<MemberDeclarationSyntax>();

        if (prop.GetMethod != null)
        {
            // ref 返回声明的 decl.Type 本身就是 RefTypeSyntax（含 ref/ref readonly），
            // 不能再包一层；符号判定仅作无语法标记时的防御路径。
            TypeSyntax returnType = decl.Type switch
            {
                RefTypeSyntax rt => rt,
                _ when prop.ReturnsByRef => SyntaxFactory.RefType(
                    SyntaxFactory.Token(SyntaxKind.RefKeyword), decl.Type),
                _ when prop.ReturnsByRefReadonly => SyntaxFactory.RefType(
                    SyntaxFactory.Token(SyntaxKind.RefKeyword),
                    SyntaxFactory.Token(SyntaxKind.ReadOnlyKeyword), decl.Type),
                _ => decl.Type,
            };

            // 表达式体/abstract 访问器的 `;` 挂在方法声明节点上，取访问器自身的分号；
            // 块体访问器无分号（default）。
            var method = SyntaxFactory.MethodDeclaration(
                SyntaxFactory.List(RouteDeclarationAttributes(bindingDecl, forGetter: true)
                    .Concat(getAccessor?.AttributeLists ?? Enumerable.Empty<AttributeListSyntax>())
                    .ToList()),
                BuildIndexerModifiers(decl, getAccessor, inInterface, isExplicit),
                returnType,
                isExplicit
                    ? SyntaxFactory.ExplicitInterfaceSpecifier(decl.ExplicitInterfaceSpecifier!.Name)
                    : null,
                SyntaxFactory.Identifier(plan.GetterMethodName),
                null,
                SyntaxFactory.ParameterList(decl.ParameterList!.Parameters),
                SyntaxFactory.List<TypeParameterConstraintClauseSyntax>(),
                getAccessor?.Body,
                declExpressionBodied ? decl.ExpressionBody : getAccessor?.ExpressionBody,
                declExpressionBodied
                    ? decl.SemicolonToken
                    : getAccessor?.SemicolonToken ?? default);
            results.Add(method);
        }

        if (prop.SetMethod != null)
        {
            // ref 索引器的 value 形参类型取 RefType 内层类型（ref int → int）。
            var valueType = decl.Type is RefTypeSyntax rt ? rt.Type : decl.Type;
            var valueParameter = SyntaxFactory.Parameter(
                default(SyntaxList<AttributeListSyntax>), default(SyntaxTokenList),
                valueType.WithoutTrivia(), SyntaxFactory.Identifier("value"), null);
            var parameters = decl.ParameterList!.Parameters.Add(valueParameter);
            var method = SyntaxFactory.MethodDeclaration(
                SyntaxFactory.List(RouteDeclarationAttributes(bindingDecl, forGetter: false)
                    .Concat(setAccessor?.AttributeLists ?? Enumerable.Empty<AttributeListSyntax>())
                    .ToList()),
                BuildIndexerModifiers(decl, setAccessor, inInterface, isExplicit),
                SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.VoidKeyword)),
                isExplicit
                    ? SyntaxFactory.ExplicitInterfaceSpecifier(decl.ExplicitInterfaceSpecifier!.Name)
                    : null,
                SyntaxFactory.Identifier(plan.SetterMethodName),
                null,
                SyntaxFactory.ParameterList(parameters),
                SyntaxFactory.List<TypeParameterConstraintClauseSyntax>(),
                setAccessor?.Body,
                setAccessor?.ExpressionBody,
                setAccessor?.SemicolonToken ?? default);
            results.Add(method);
        }

        // trivia：首成员接声明 leading（含文档注释），末成员接 trailing。
        if (results.Count > 0)
        {
            var annotated = results
                .Select(m => m.WithAdditionalAnnotations(MemberDesugarSyntaxFacts.RewriteAnnotation))
                .ToList();
            annotated[0] = annotated[0].WithLeadingTrivia(decl.GetLeadingTrivia());
            annotated[^1] = annotated[^1].WithTrailingTrivia(decl.GetTrailingTrivia());
            return annotated;
        }
        return results;
    }

    private void EmitDefaultMemberNote(IPropertySymbol prop, IndexerDeclarationSyntax decl)
    {
        if (prop.ContainingType?.GetAttributes().Any(a =>
                a.AttributeClass is { Name: "DefaultMemberAttribute" }
                && a.AttributeClass.ContainingNamespace?.ToDisplayString() == "System.Reflection")
            != true) return;
        _diagnostics.Add(new MemberDesugarDiagnostic(
            MemberDesugarSeverity.Info, MemberDesugarDiagnosticCode.DefaultMemberRetained,
            "type-level [DefaultMember] was retained; reflection lookups such as " +
            "GetProperty(\"Item\") are no longer guaranteed to find the desugared indexer",
            _filePath, LineOf(decl), ColOf(decl), prop.MetadataName));
    }

    /// <summary>声明级特性：IndexerName 移除；return: 目标 → getter；其余按
    /// AttributeRouting 路由（Unroutable 落 getter，best-effort，受整树回滚保护）。</summary>
    private IEnumerable<AttributeListSyntax> RouteDeclarationAttributes(
        IndexerDeclarationSyntax bindingDecl, bool forGetter)
    {
        foreach (var list in bindingDecl.AttributeLists)
        {
            var kept = list.Attributes.Where(a => !IsIndexerName(a)).ToList();
            if (kept.Count == 0) continue;
            var filtered = list.WithAttributes(SyntaxFactory.SeparatedList(kept));
            if (list.Target?.Identifier.ValueText == "return")
            {
                if (forGetter) yield return filtered;
                continue;
            }
            var dest = AttributeRouting.RouteList(list, _model, fieldGenerated: false);
            if (dest == AttributeRouting.Destination.Getter && !forGetter) continue;
            yield return filtered;
        }
    }

    private bool IsIndexerName(AttributeSyntax attr)
        => attr.Name.ToString() is "IndexerName" or "IndexerNameAttribute"
           || (_model.GetSymbolInfo(attr).Symbol as IMethodSymbol)?.ContainingType?.Name
               == "IndexerNameAttribute";

    /// <summary>索引器版修饰符规则（镜像 AccessorsToMethods.BuildMethodModifiers；
    /// readonly 一律丢弃（D7），接口与显式实现不带修饰符）。</summary>
    private static SyntaxTokenList BuildIndexerModifiers(
        IndexerDeclarationSyntax decl, AccessorDeclarationSyntax? accessor,
        bool inInterface, bool isExplicit)
    {
        if (isExplicit) return SyntaxFactory.TokenList();
        if (inInterface) return SyntaxFactory.TokenList();

        var mods = new List<SyntaxToken>();
        var accessorAccessibility =
            accessor?.Modifiers.FirstOrDefault(AccessorsToMethods.IsAccessibility) ?? default;
        var chosen = accessorAccessibility != default
            ? accessorAccessibility
            : decl.Modifiers.FirstOrDefault(AccessorsToMethods.IsAccessibility);
        if (chosen != default) mods.Add(chosen);

        foreach (var m in decl.Modifiers)
        {
            if (AccessorsToMethods.IsAccessibility(m)) continue;
            if (m.IsKind(SyntaxKind.ReadOnlyKeyword) || m.IsKind(SyntaxKind.PartialKeyword))
                continue;
            mods.Add(m);
        }
        return SyntaxFactory.TokenList(mods);
    }

    private static int LineOf(IndexerDeclarationSyntax decl)
        => decl.GetLocation().GetLineSpan().StartLinePosition.Line + 1;

    private static int ColOf(IndexerDeclarationSyntax decl)
        => decl.GetLocation().GetLineSpan().StartLinePosition.Character + 1;
}
