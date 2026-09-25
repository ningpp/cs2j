using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.RecordReadOnly;

/// <summary>
/// Phase 3d：接口连带（IR）。
/// IR-1：向 in-tree 接口追加 With 成员（多个 record 计划触发同形状成员时去重；
/// 成员始终加到“声明它”的接口，接口继承 IR-5 经 AllInterfaces 自然传播）。
/// IR-2：向转换中的 record 追加——旧接口属性的显式实现桩（get 转发 / set throw）、
/// 新 With 成员的显式接口实现（委托公共版）、公共强类型 With（同形状跨接口只生成一份）。
/// IR-3（其他 in-tree 实现者 fluent 补齐）与方法触发在后续任务接入。
/// </summary>
internal sealed class InterfaceRippler : CSharpSyntaxRewriter
{
    private readonly RewriteContext _context;
    private readonly InterfaceRippleIndex _index;

    public InterfaceRippler(RewriteContext context)
    {
        _context = context;
        _index = InterfaceRippleIndex.Build(context);
    }

    public override SyntaxNode? VisitInterfaceDeclaration(InterfaceDeclarationSyntax node)
    {
        var visited = (InterfaceDeclarationSyntax)base.VisitInterfaceDeclaration(node)!;
        // node 属于前序 rewriter 产出的脱离树，须先在原始树中找回同构节点再解析符号。
        var symbol = ResolveSymbol(node);
        if (symbol == null || !IsPrimaryPart(node, symbol)) return visited;
        if (!_index.Additions.TryGetValue(symbol, out var members) || members.Count == 0)
            return visited;

        var additions = members
            .Select(m => SyntaxFactory.ParseMemberDeclaration(InterfaceMemberText(m))!)
            .Cast<MemberDeclarationSyntax>()
            .Select(m => m
                .WithLeadingTrivia(SyntaxFactory.CarriageReturnLineFeed)
                .WithAdditionalAnnotations(RecordSyntaxFacts.RewriteAnnotation))
            .ToArray();
        foreach (var _ in additions)
            _context.Statistics.InterfaceRipples++;
        return visited
            .WithMembers(visited.Members.AddRange(additions))
            .WithAdditionalAnnotations(RecordSyntaxFacts.RewriteAnnotation);
    }

    public override SyntaxNode? VisitRecordDeclaration(RecordDeclarationSyntax node)
    {
        var visited = (RecordDeclarationSyntax)base.VisitRecordDeclaration(node)!;
        var symbol = ResolveSymbol(node);
        if (symbol == null || !IsPrimaryPart(node, symbol)) return visited;

        var plan = _context.ActivePlans.FirstOrDefault(p =>
            SymbolEqualityComparer.Default.Equals(p.Symbol, symbol)
            && p.Outcome == PlanOutcome.Converted);
        if (plan == null) return visited;

        var additions = new List<MemberDeclarationSyntax>();
        var emittedPublicShapes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var ripple in plan.Ripples)
        {
            foreach (var member in ripple.Members)
            {
                additions.Add(ExplicitPropertyStub(member)
                    .WithLeadingTrivia(SyntaxFactory.CarriageReturnLineFeed));
                additions.Add(ExplicitWithPropertyImplementation(member)
                    .WithLeadingTrivia(SyntaxFactory.CarriageReturnLineFeed));

                if (emittedPublicShapes.Add(PublicShapeKey(member)))
                {
                    additions.Add(PublicWithPropertyMethod(symbol, member)
                        .WithLeadingTrivia(SyntaxFactory.CarriageReturnLineFeed));
                }
            }
        }
        if (additions.Count == 0) return visited;
        return visited
            .WithMembers(visited.Members.AddRange(additions))
            .WithAdditionalAnnotations(RecordSyntaxFacts.RewriteAnnotation);
    }

    /// <summary>接口/record 符号解析：按结构路径在原始树找回同构节点（输入树已脱离模型）。</summary>
    private INamedTypeSymbol? ResolveSymbol(TypeDeclarationSyntax node)
    {
        var origRoot = _context.Model.SyntaxTree.GetRoot();
        var origNode = DeclarationRewriter.FindByPath(origRoot, node) ?? node;
        try { return _context.Model.GetDeclaredSymbol(origNode) as INamedTypeSymbol; }
        catch (ArgumentException) { return null; }
    }

    /// <summary>IR-1：接口追加 `I WithP(T value);`（抽象；DIM 默认关闭，见 IR-7 注）。</summary>
    private static string InterfaceMemberText(RippleMember member)
    {
        var property = (IPropertySymbol)member.InterfaceMember;
        var interfaceText = member.Interface.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        var valueTypeText = property.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        return $"{interfaceText} {member.WithName}({valueTypeText} value);";
    }

    /// <summary>IR-2：`T I.P { get =&gt; P; set =&gt; throw new NotSupportedException(...); }`</summary>
    private static PropertyDeclarationSyntax ExplicitPropertyStub(RippleMember member)
    {
        var interfaceProperty = (IPropertySymbol)member.InterfaceMember;
        var recordProperty = (IPropertySymbol)member.RecordMember;
        var message =
            $"interface setter '{member.Interface.ToDisplayString()}.{interfaceProperty.Name}' " +
            "is not supported after the record became readonly; use the generated 'With' " +
            "member instead (IR-2).";
        var throwExpression = SyntaxFactory.ParseExpression(
            "throw new global::System.NotSupportedException(\"" + message + "\")");

        var getAccessor = SyntaxFactory.AccessorDeclaration(SyntaxKind.GetAccessorDeclaration)
            .WithExpressionBody(SyntaxFactory.ArrowExpressionClause(
                SyntaxFactory.IdentifierName(recordProperty.Name)))
            .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken));
        var setAccessor = SyntaxFactory.AccessorDeclaration(SyntaxKind.SetAccessorDeclaration)
            .WithExpressionBody(SyntaxFactory.ArrowExpressionClause(throwExpression))
            .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken));

        return SyntaxFactory.PropertyDeclaration(
                RecordSyntaxFacts.GlobalType(interfaceProperty.Type),
                SyntaxFactory.Identifier(interfaceProperty.Name))
            .WithExplicitInterfaceSpecifier(ExplicitInterface(member.Interface))
            .WithAccessorList(SyntaxFactory.AccessorList(
                SyntaxFactory.List(new[] { getAccessor, setAccessor })))
            .WithAdditionalAnnotations(RecordSyntaxFacts.RewriteAnnotation);
    }

    /// <summary>IR-2：`I I.WithP(T value) =&gt; this.WithP(value);`（委托公共强类型版）。</summary>
    private static MethodDeclarationSyntax ExplicitWithPropertyImplementation(RippleMember member)
    {
        var property = (IPropertySymbol)member.InterfaceMember;
        return SyntaxFactory.MethodDeclaration(
                RecordSyntaxFacts.GlobalType(member.Interface),
                SyntaxFactory.Identifier(member.WithName))
            .WithExplicitInterfaceSpecifier(ExplicitInterface(member.Interface))
            .WithParameterList(ValueParameterList(property.Type))
            .WithExpressionBody(SyntaxFactory.ArrowExpressionClause(
                SyntaxFactory.ParseExpression($"this.{member.WithName}(value)")))
            .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken))
            .WithAdditionalAnnotations(RecordSyntaxFacts.RewriteAnnotation);
    }

    /// <summary>IR-2：`public R WithP(T value) =&gt; this with { P = value };`</summary>
    private static MemberDeclarationSyntax PublicWithPropertyMethod(
        INamedTypeSymbol record, RippleMember member)
    {
        var interfaceProperty = (IPropertySymbol)member.InterfaceMember;
        var recordProperty = (IPropertySymbol)member.RecordMember;
        var text =
            $"public {record.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)} " +
            $"{member.WithName}(" +
            $"{interfaceProperty.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)} value) " +
            $"=> this with {{ {recordProperty.Name} = value }};";
        return SyntaxFactory.ParseMemberDeclaration(text)!
            .WithAdditionalAnnotations(RecordSyntaxFacts.RewriteAnnotation);
    }

    private static ParameterListSyntax ValueParameterList(ITypeSymbol valueType) =>
        SyntaxFactory.ParameterList(SyntaxFactory.SingletonSeparatedList(
            SyntaxFactory.Parameter(SyntaxFactory.Identifier("value"))
                .WithType(RecordSyntaxFacts.GlobalType(valueType))));

    private static ExplicitInterfaceSpecifierSyntax ExplicitInterface(INamedTypeSymbol iface) =>
        SyntaxFactory.ExplicitInterfaceSpecifier(
            SyntaxFactory.ParseName(iface.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)));

    /// <summary>公共版去重形状：With 名 + 值参类型（返回类型恒为本 record，不参与）。</summary>
    private static string PublicShapeKey(RippleMember member)
    {
        var property = (IPropertySymbol)member.InterfaceMember;
        return member.WithName + "("
            + property.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) + ")";
    }

    /// <summary>
    /// partial 类型只向确定性选定的主部件（文件路径序、再跨度序最小）追加成员，
    /// 避免每个部件各加一份造成重复定义。
    /// </summary>
    private static bool IsPrimaryPart(SyntaxNode current, INamedTypeSymbol symbol)
    {
        var refs = symbol.DeclaringSyntaxReferences;
        if (refs.Length <= 1) return true;
        var primary = refs
            .OrderBy(r => r.SyntaxTree.FilePath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.Span.Start)
            .First();
        return string.Equals(current.SyntaxTree!.FilePath, primary.SyntaxTree.FilePath,
                   StringComparison.OrdinalIgnoreCase)
               && current.SpanStart == primary.Span.Start;
    }
}

/// <summary>
/// 本文件需要落地的接口追加（IR-1）与实现者补齐（IR-3，下一个任务）索引。
/// 跨 record 计划按“同接口 + With 名 + 值参形状”去重，避免重复成员。
/// </summary>
internal sealed class InterfaceRippleIndex
{
    public required Dictionary<INamedTypeSymbol, List<RippleMember>> Additions { get; init; }
    public required Dictionary<INamedTypeSymbol, List<RippleMember>> Fills { get; init; }

    public static InterfaceRippleIndex Build(RewriteContext context)
    {
        var additions = new Dictionary<INamedTypeSymbol, List<RippleMember>>(
            SymbolEqualityComparer.Default);
        foreach (var plan in context.ActivePlans)
        {
            if (plan.Outcome != PlanOutcome.Converted) continue;
            foreach (var ripple in plan.Ripples)
            {
                if (!additions.TryGetValue(ripple.Interface, out var list))
                    additions[ripple.Interface] = list = new();
                foreach (var member in ripple.Members)
                {
                    if (list.Any(existing => SameShape(existing, member))) continue;
                    list.Add(member);
                }
            }
        }
        return new InterfaceRippleIndex
        {
            Additions = additions,
            Fills = new(SymbolEqualityComparer.Default),
        };
    }

    /// <summary>同接口内 With 名相同且值参类型相同即视为同一份契约（方法形状 Task 21 补全）。</summary>
    internal static bool SameShape(RippleMember a, RippleMember b)
    {
        if (a.IsMethod != b.IsMethod || a.WithName != b.WithName) return false;
        if (a.IsMethod) return false;
        var pa = (IPropertySymbol)a.InterfaceMember;
        var pb = (IPropertySymbol)b.InterfaceMember;
        return SymbolEqualityComparer.Default.Equals(pa.Type, pb.Type);
    }
}
