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
/// IR-3：为同接口的其他 in-tree 实现者（class/struct/未转换 record）补 fluent 显式实现。
/// 方法触发在后续任务接入。
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
            .Select<RippleMember, MemberDeclarationSyntax>(m => m.IsMethod
                ? InterfaceMethodDeclaration(m)
                : SyntaxFactory.ParseMemberDeclaration(InterfaceMemberText(m))!)
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

    public override SyntaxNode? VisitClassDeclaration(ClassDeclarationSyntax node)
    {
        var visited = (ClassDeclarationSyntax)base.VisitClassDeclaration(node)!;
        return AddInterfaceFills(node, visited);
    }

    public override SyntaxNode? VisitStructDeclaration(StructDeclarationSyntax node)
    {
        var visited = (StructDeclarationSyntax)base.VisitStructDeclaration(node)!;
        return AddInterfaceFills(node, visited);
    }

    public override SyntaxNode? VisitRecordDeclaration(RecordDeclarationSyntax node)
    {
        var visited = (RecordDeclarationSyntax)base.VisitRecordDeclaration(node)!;
        var symbol = ResolveSymbol(node);
        if (symbol == null || !IsPrimaryPart(node, symbol)) return visited;

        var additions = new List<MemberDeclarationSyntax>();
        var emittedPublicShapes = new HashSet<string>(StringComparer.Ordinal);

        // IR-2 只属于本轮转换中的 owner record；未转换 record 在这里 plan == null。
        var plan = _context.ActivePlans.FirstOrDefault(p =>
            SymbolEqualityComparer.Default.Equals(p.Symbol, symbol)
            && p.Outcome == PlanOutcome.Converted);
        if (plan != null)
        {
            foreach (var ripple in plan.Ripples)
            {
                foreach (var member in ripple.Members)
                {
                    if (member.IsMethod)
                    {
                        additions.Add(ExplicitOldMethodStub(member)
                            .WithLeadingTrivia(SyntaxFactory.CarriageReturnLineFeed));
                        additions.Add(ExplicitWithMethodImplementation(member)
                            .WithLeadingTrivia(SyntaxFactory.CarriageReturnLineFeed));
                        continue;
                    }

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
        }

        // IR-3：未转换 record class（含 ConvertRecordClasses=false 时的 record class）/
        // 未转换 record struct 作为其他实现者补 fluent 显式实现。owner 与其他“本轮也转换”
        // 的 record 已在 InterfaceRippleIndex.Build 中排除（它们由自己的 IR-2 负责）。
        var fillAdditions = BuildFillMembers(symbol);
        additions.AddRange(fillAdditions);

        if (additions.Count == 0) return visited;
        foreach (var _ in fillAdditions)
            _context.Statistics.InterfaceRipples++;
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

    private SyntaxNode? AddInterfaceFills(TypeDeclarationSyntax node, TypeDeclarationSyntax visited)
    {
        var symbol = ResolveSymbol(node);
        if (symbol == null || !IsPrimaryPart(node, symbol)) return visited;
        var additions = BuildFillMembers(symbol);
        if (additions.Count == 0) return visited;
        // 每个实际生成的 fill 成员计一次 InterfaceRipples；接口追加在
        // VisitInterfaceDeclaration 计数，owner 的 IR-2 显式桩不计数。
        foreach (var _ in additions)
            _context.Statistics.InterfaceRipples++;
        return visited
            .WithMembers(visited.Members.AddRange(additions))
            .WithAdditionalAnnotations(RecordSyntaxFacts.RewriteAnnotation);
    }

    private List<MemberDeclarationSyntax> BuildFillMembers(INamedTypeSymbol implementer)
    {
        if (!_index.Fills.TryGetValue(implementer, out var members) || members.Count == 0)
            return new();

        // 属性与方法 fills 都支持。
        return members
            .Select(member => member.IsMethod
                ? MethodFillMethod(implementer, member)
                    .WithLeadingTrivia(SyntaxFactory.CarriageReturnLineFeed)
                : PropertyFillMethod(implementer, member)
                    .WithLeadingTrivia(SyntaxFactory.CarriageReturnLineFeed))
            .Cast<MemberDeclarationSyntax>()
            .ToList();
    }

    /// <summary>IR-3：`I I.WithP(T value) { P = value; return this; }`。</summary>
    private static MethodDeclarationSyntax PropertyFillMethod(
        INamedTypeSymbol implementer, RippleMember member)
    {
        var interfaceProperty = (IPropertySymbol)member.InterfaceMember;
        var implementation = (IPropertySymbol?)implementer
            .FindImplementationForInterfaceMember(interfaceProperty);
        var interfaceText = member.Interface.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        var valueTypeText = interfaceProperty.Type
            .ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

        var target = implementation != null
            && RecordSyntaxFacts.IsExplicitInterfaceImplementation(implementation, interfaceProperty)
            ? $"(({interfaceText})this).{interfaceProperty.Name}"
            : implementation?.Name ?? interfaceProperty.Name;

        var text =
            $"{interfaceText} {member.WithName}({valueTypeText} value) " +
            $"{{ {target} = value; return this; }}";
        return ((MethodDeclarationSyntax)SyntaxFactory.ParseMemberDeclaration(text)!)
            .WithExplicitInterfaceSpecifier(ExplicitInterface(member.Interface))
            .WithAdditionalAnnotations(RecordSyntaxFacts.RewriteAnnotation);
    }

    /// <summary>
    /// IR-3 方法 fill：
    /// void M(...)    { M(...); return this; }
    /// T M(...)       { __result = M(...); return this; }
    /// 隐式实现直接调原方法名（精确输出 `Bump();` / `__result = Next();`）；显式实现的
    /// class/record class 经 ((I)this) 调旧契约；显式 struct 已在 BuildInterfaceFills 的
    /// RR070 门阻断；泛型接口方法已在 PlanBuilder 的 RR070 门阻断——故此处没有也不允许
    /// 出现任何泛型分支或运行时异常。
    /// </summary>
    private static MethodDeclarationSyntax MethodFillMethod(
        INamedTypeSymbol implementer, RippleMember member)
    {
        var interfaceMethod = (IMethodSymbol)member.InterfaceMember;
        var implementation = (IMethodSymbol?)implementer
            .FindImplementationForInterfaceMember(interfaceMethod);
        var template = InterfaceMethodTemplate(member);
        var (parameters, outName) = WithOptionalResultParameter(template, interfaceMethod);

        var explicitCall = implementation != null
            && RecordSyntaxFacts.IsExplicitInterfaceImplementation(
                implementation, interfaceMethod);

        var arguments = template.ParameterList.Parameters
            .Select(p => SyntaxFactory.Argument(SyntaxFactory.IdentifierName(p.Identifier.Text))
                .WithRefKindKeyword(ArgumentRefKindToken(p)))
            .ToList();

        var originalName = explicitCall
            ? interfaceMethod.Name
            : implementation?.Name ?? interfaceMethod.Name;
        ExpressionSyntax target = explicitCall
            ? SyntaxFactory.MemberAccessExpression(
                SyntaxKind.SimpleMemberAccessExpression,
                SyntaxFactory.CastExpression(
                    RecordSyntaxFacts.GlobalType(member.Interface),
                    SyntaxFactory.ThisExpression()),
                SyntaxFactory.IdentifierName(originalName))
            : SyntaxFactory.IdentifierName(originalName);

        var invocation = SyntaxFactory.InvocationExpression(
            target, SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList(arguments)));
        var statements = new List<StatementSyntax>();
        if (outName == null)
        {
            statements.Add(SyntaxFactory.ExpressionStatement(invocation));
        }
        else
        {
            statements.Add(SyntaxFactory.ExpressionStatement(
                SyntaxFactory.AssignmentExpression(
                    SyntaxKind.SimpleAssignmentExpression,
                    SyntaxFactory.IdentifierName(outName),
                    invocation)));
        }
        statements.Add(SyntaxFactory.ReturnStatement(SyntaxFactory.ThisExpression()));

        return SyntaxFactory.MethodDeclaration(
                RecordSyntaxFacts.GlobalType(member.Interface),
                SyntaxFactory.Identifier(member.WithName))
            .WithModifiers(SyntaxFactory.TokenList())
            .WithParameterList(parameters)
            .WithExplicitInterfaceSpecifier(ExplicitInterface(member.Interface))
            .WithBody(SyntaxFactory.Block(statements))
            .WithAdditionalAnnotations(RecordSyntaxFacts.RewriteAnnotation);
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

    /// <summary>IR-1：接口原方法模板改返回 I、改名 WithX；非 void 末尾加 out __result。</summary>
    private static MethodDeclarationSyntax InterfaceMethodDeclaration(RippleMember member)
    {
        var template = InterfaceMethodTemplate(member);
        var interfaceMethod = (IMethodSymbol)member.InterfaceMember;
        var (parameters, _) = WithOptionalResultParameter(template, interfaceMethod);
        return CleanMethodTemplate(template)
            .WithReturnType(RecordSyntaxFacts.GlobalType(member.Interface))
            .WithIdentifier(SyntaxFactory.Identifier(member.WithName))
            .WithParameterList(parameters)
            .WithAdditionalAnnotations(RecordSyntaxFacts.RewriteAnnotation);
    }

    /// <summary>IR-2：旧方法契约保留为 throw 桩；返回类型/参数与原接口成员一致。</summary>
    private static MethodDeclarationSyntax ExplicitOldMethodStub(RippleMember member)
    {
        var template = InterfaceMethodTemplate(member);
        var method = (IMethodSymbol)member.InterfaceMember;
        var message =
            $"interface method '{member.Interface.ToDisplayString()}.{method.Name}' is not " +
            "supported after the record became readonly; use the generated 'With' member " +
            "instead (IR-2).";
        var throwExpression = SyntaxFactory.ParseExpression(
            "throw new global::System.NotSupportedException(\"" + message + "\")");

        return CleanMethodTemplate(template)
            .WithReturnType(template.ReturnType)
            .WithIdentifier(SyntaxFactory.Identifier(method.Name))
            .WithExplicitInterfaceSpecifier(ExplicitInterface(member.Interface))
            .WithExpressionBody(SyntaxFactory.ArrowExpressionClause(throwExpression))
            .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken))
            .WithAdditionalAnnotations(RecordSyntaxFacts.RewriteAnnotation);
    }

    /// <summary>IR-2：`I I.WithX(...) => ImplName(...[, out __result]);`。</summary>
    private static MethodDeclarationSyntax ExplicitWithMethodImplementation(RippleMember member)
    {
        var template = InterfaceMethodTemplate(member);
        var interfaceMethod = (IMethodSymbol)member.InterfaceMember;
        var (_, outName) = WithOptionalResultParameter(template, interfaceMethod);
        var invocation = InvokeOriginalMethod(member, template, outName);
        return InterfaceMethodDeclaration(member)
            .WithExplicitInterfaceSpecifier(ExplicitInterface(member.Interface))
            .WithExpressionBody(SyntaxFactory.ArrowExpressionClause(invocation))
            .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken))
            .WithAdditionalAnnotations(RecordSyntaxFacts.RewriteAnnotation);
    }

    /// <summary>
    /// 取接口方法的源码声明模板。Step 7 的 RR070 已保证接口在编译树内，方法必然存在
    /// in-source 的 DeclaringSyntaxReference；接口即使分部件声明，取第一个即可（模板只
    /// 借用返回类型/参数/特性清洗，与部件位置无关）。
    /// </summary>
    private static MethodDeclarationSyntax InterfaceMethodTemplate(RippleMember member) =>
        (MethodDeclarationSyntax)((IMethodSymbol)member.InterfaceMember)
            .DeclaringSyntaxReferences
            .First(r => r.SyntaxTree != null)
            .GetSyntax();

    private static MethodDeclarationSyntax CleanMethodTemplate(MethodDeclarationSyntax template) =>
        template
            .WithAttributeLists(default)
            .WithModifiers(SyntaxFactory.TokenList())
            .WithExplicitInterfaceSpecifier(null)
            .WithBody(null)
            .WithExpressionBody(null)
            .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken));

    /// <summary>
    /// 非 void 接口方法在参数表末尾追加独立的 `out T __result`（名与原参数去重）；
    /// void 原样返回。symbol 由调用方显式传入，不依赖 template 的语义父节点。
    /// </summary>
    private static (ParameterListSyntax Parameters, string? OutName) WithOptionalResultParameter(
        MethodDeclarationSyntax template, IMethodSymbol method)
    {
        if (method.ReturnsVoid)
            return (template.ParameterList, null);

        var outName = UniqueOutParameterName(template);
        var parameter = SyntaxFactory.Parameter(SyntaxFactory.Identifier(outName))
            .WithModifiers(SyntaxFactory.TokenList(
                SyntaxFactory.Token(SyntaxKind.OutKeyword)
                    .WithTrailingTrivia(SyntaxFactory.Space)))
            .WithType(RecordSyntaxFacts.GlobalType(method.ReturnType));
        return (SyntaxFactory.ParameterList(template.ParameterList.Parameters.Add(parameter)),
            outName);
    }

    private static string UniqueOutParameterName(MethodDeclarationSyntax template)
    {
        var taken = template.ParameterList.Parameters
            .Select(p => p.Identifier.Text)
            .ToHashSet(StringComparer.Ordinal);
        var name = "__result";
        var suffix = 0;
        while (taken.Contains(name))
            name = $"__result_{++suffix}";
        return name;
    }

    /// <summary>
    /// owner record 内的转发调用：`ImplName(args)`；非 void 末尾追加 `out __result`。
    /// 名字必须取**实现方法**名（`member.RecordMember`，即 MM 单元方法）：隐式实现允许
    /// 与接口方法不同名（`public int Fetch()` 实现 `int Next()`），而显式实现符号的
    /// Name 同样不带接口前缀（"Bump"），提升后 public 名就是它。刻意不带 `this.` 前缀
    /// ——输出以精确子串守护（`=> Bump();`、`=> Next(out __result);`）。
    /// </summary>
    private static InvocationExpressionSyntax InvokeOriginalMethod(
        RippleMember member, MethodDeclarationSyntax template, string? outName)
    {
        var arguments = template.ParameterList.Parameters
            .Select(p => SyntaxFactory.Argument(SyntaxFactory.IdentifierName(p.Identifier.Text))
                .WithRefKindKeyword(ArgumentRefKindToken(p)))
            .ToList();
        if (outName != null)
        {
            arguments.Add(SyntaxFactory.Argument(SyntaxFactory.IdentifierName(outName))
                .WithRefKindKeyword(SyntaxFactory.Token(SyntaxKind.OutKeyword)
                    .WithTrailingTrivia(SyntaxFactory.Space)));
        }

        // 显式接口实现符号的 Name 带接口限定（"I.Bump"），取末段得到提升后的 public 名。
        var recordMemberName = ((IMethodSymbol)member.RecordMember).Name;
        var implementationName = recordMemberName.Contains('.')
            ? recordMemberName[(recordMemberName.LastIndexOf('.') + 1)..]
            : recordMemberName;
        return SyntaxFactory.InvocationExpression(
            SyntaxFactory.IdentifierName(implementationName),
            SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList(arguments)));
    }

    private static SyntaxToken ArgumentRefKindToken(ParameterSyntax parameter)
    {
        if (parameter.Modifiers.Any(SyntaxKind.InKeyword))
            return SyntaxFactory.Token(SyntaxKind.InKeyword).WithTrailingTrivia(SyntaxFactory.Space);
        if (parameter.Modifiers.Any(SyntaxKind.RefKeyword))
            return SyntaxFactory.Token(SyntaxKind.RefKeyword).WithTrailingTrivia(SyntaxFactory.Space);
        if (parameter.Modifiers.Any(SyntaxKind.OutKeyword))
            return SyntaxFactory.Token(SyntaxKind.OutKeyword).WithTrailingTrivia(SyntaxFactory.Space);
        return SyntaxFactory.Token(SyntaxKind.None);
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
/// 本文件需要落地的接口追加（IR-1）与实现者补齐（IR-3）索引。
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
        var convertedImplementers = new HashSet<INamedTypeSymbol>(
            context.Plan.Plans
                .Where(p => p.Outcome == PlanOutcome.Converted)
                .Select(p => p.Symbol),
            SymbolEqualityComparer.Default);

        var fills = new Dictionary<INamedTypeSymbol, List<RippleMember>>(
            SymbolEqualityComparer.Default);
        foreach (var plan in context.ActivePlans)
        {
            if (plan.Outcome != PlanOutcome.Converted) continue;
            foreach (var ripple in plan.Ripples)
            foreach (var fill in ripple.Fills)
            {
                // 另一个已转换 record 由自己的 IR-2 生成显式实现；不能再按普通实现者补一份。
                if (convertedImplementers.Contains(fill.Implementer)) continue;
                if (!fills.TryGetValue(fill.Implementer, out var list))
                    fills[fill.Implementer] = list = new();
                foreach (var member in fill.Members)
                {
                    if (list.Any(existing => InterfaceRippleIndex.SameShape(existing, member)))
                        continue;
                    list.Add(member);
                }
            }
        }

        return new InterfaceRippleIndex
        {
            Additions = additions,
            Fills = fills,
        };
    }

    /// <summary>同接口内 With 名相同且值参类型相同即视为同一份契约。</summary>
    internal static bool SameShape(RippleMember a, RippleMember b)
    {
        if (a.IsMethod != b.IsMethod || a.WithName != b.WithName) return false;
        if (a.IsMethod)
        {
            // 索引按接口符号分组，这里只需比较接口方法本体；同一接口方法被多个
            // record 计划引用时 OriginalDefinition 相同，去重为一份契约。
            return SymbolEqualityComparer.Default.Equals(
                ((IMethodSymbol)a.InterfaceMember).OriginalDefinition,
                ((IMethodSymbol)b.InterfaceMember).OriginalDefinition);
        }
        var pa = (IPropertySymbol)a.InterfaceMember;
        var pb = (IPropertySymbol)b.InterfaceMember;
        return SymbolEqualityComparer.Default.Equals(pa.Type, pb.Type);
    }
}
