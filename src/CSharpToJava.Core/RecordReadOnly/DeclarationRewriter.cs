using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace CSharpToJava.Core.RecordReadOnly;

internal sealed class DeclarationRewriter : CSharpSyntaxRewriter
{
    private readonly RewriteContext _context;

    public DeclarationRewriter(RewriteContext context) => _context = context;

    /// <summary>
    /// 声明符号解析：前置 rewriter（方法迁移/用点回写）会平移后续声明的绝对位置，
    /// Span 反查不可靠；但成员列表槽位不受语句级改写影响，故按“从根到节点的子节点
    /// 槽位路径”在原始树找回声明节点，再做符号解析。找不到时退回 Span 反查。
    /// </summary>
    private ISymbol? GetDeclaredSymbolRobust(SyntaxNode node)
    {
        var origRoot = _context.Model.SyntaxTree.GetRoot();
        var origNode = FindByPath(origRoot, node) ?? FindBySpan(origRoot, node);
        if (origNode == null) return null;
        try { return _context.Model.GetDeclaredSymbol(origNode); }
        catch (ArgumentException) { return null; }
    }

    internal static SyntaxNode? FindByPath(SyntaxNode origRoot, SyntaxNode node)
    {
        var slots = new List<int>();
        for (var cursor = node; cursor.Parent != null; cursor = cursor.Parent)
        {
            var slot = IndexOfChild(cursor.Parent, cursor);
            if (slot < 0) return null;
            slots.Add(slot);
        }
        slots.Reverse();
        var current = origRoot;
        foreach (var slot in slots)
        {
            var children = current.ChildNodes().ToList();
            if (slot >= children.Count) return null;
            current = children[slot];
        }
        return current;
    }

    private static int IndexOfChild(SyntaxNode parent, SyntaxNode child)
    {
        var index = 0;
        foreach (var c in parent.ChildNodes())
        {
            if (ReferenceEquals(c, child)) return index;
            index++;
        }
        return -1;
    }

    private static SyntaxNode? FindBySpan(SyntaxNode origRoot, SyntaxNode node)
    {
        try
        {
            return origRoot.FindNode(new TextSpan(node.SpanStart, 0));
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private RecordPlan? ActivePlan(INamedTypeSymbol? type) =>
        type == null ? null
        : _context.ActivePlans.FirstOrDefault(p =>
            SymbolEqualityComparer.Default.Equals(p.Symbol, type));

    private MemberRewrite? FindRewrite(RecordPlan plan, ISymbol symbol) =>
        plan.MemberRewrites.FirstOrDefault(r =>
            SymbolEqualityComparer.Default.Equals(r.Symbol, symbol));

    public override SyntaxNode? VisitRecordDeclaration(RecordDeclarationSyntax node)
    {
        // 在 base.Visit 之前解析 record 符号（visited 节点不在语法树中，
        // SemanticModel.GetDeclaredSymbol 会抛 ArgumentException）。
        var recordSymbol = GetDeclaredSymbolRobust(node) as INamedTypeSymbol;
        var plan = ActivePlan(recordSymbol);

        // 预先收集本 record 字段声明的符号 → MemberRewrite 映射，按声明符索引。
        // 字段声明节点未被前置 rewriter 改写，符号定位标注保持，可直接查映射。
        var fieldRewriteMap = new Dictionary<(int FieldIndex, int VarIndex), MemberRewrite?>();
        if (plan is { Outcome: PlanOutcome.Converted })
        {
            var fieldIndex = 0;
            foreach (var member in node.Members)
            {
                if (member is FieldDeclarationSyntax field)
                {
                    for (var vi = 0; vi < field.Declaration.Variables.Count; vi++)
                    {
                        var varSymbol = GetDeclaredSymbolRobust(
                            field.Declaration.Variables[vi]) as IFieldSymbol;
                        var rewrite = varSymbol == null ? null : FindRewrite(plan, varSymbol);
                        fieldRewriteMap[(fieldIndex, vi)] = rewrite;
                    }
                    fieldIndex++;
                }
            }
        }

        var visited = (RecordDeclarationSyntax)base.VisitRecordDeclaration(node)!;

        // RS-5/RS-6：把计划中要转属性的字段在成员列表里展开（一条 FieldDeclaration
        // 可含多个变量声明符）。仅对本 record 的字段动手。
        if (plan is { Outcome: PlanOutcome.Converted })
        {
            var expanded = new List<MemberDeclarationSyntax>();
            var origFieldIndex = 0;
            foreach (var member in visited.Members)
            {
                if (member is FieldDeclarationSyntax field)
                {
                    var replacements = ExpandField(plan, field, fieldRewriteMap, origFieldIndex);
                    if (replacements != null) { expanded.AddRange(replacements); origFieldIndex++; continue; }
                    origFieldIndex++;
                }
                expanded.Add(member);
            }
            visited = visited.WithMembers(SyntaxFactory.List(expanded));
        }

        if (plan is not { Outcome: PlanOutcome.Converted }) return visited;
        if (plan.Kind != RecordKind.Struct) return visited; // RC-1：class 无标记
        if (visited.Modifiers.Any(m => m.IsKind(SyntaxKind.ReadOnlyKeyword))) return visited;

        var modifier = SyntaxFactory.Token(SyntaxKind.ReadOnlyKeyword)
            .WithTrailingTrivia(SyntaxFactory.Space);

        // readonly 必须排在 partial 之前，否则 CS1585。
        // 找到 partial 的位置，把 readonly 插在其前；没有 partial 则追加到末尾。
        var mods = visited.Modifiers;
        var partialIndex = -1;
        for (var i = 0; i < mods.Count; i++)
        {
            if (mods[i].IsKind(SyntaxKind.PartialKeyword))
            {
                partialIndex = i;
                break;
            }
        }
        var newMods = partialIndex >= 0
            ? mods.Insert(partialIndex, modifier)
            : SyntaxFactory.TokenList(mods.Concat(new[] { modifier }));

        return visited
            .WithModifiers(newMods)
            .WithAdditionalAnnotations(RecordSyntaxFacts.RewriteAnnotation);
    }

    /// <summary>
    /// 字段声明 → 一个或多个替换成员。返回 null 表示该 FieldDeclaration 整体保留
    /// （可能其中的变量加了 readonly，那种情况由 base.VisitFieldDeclaration 处理）。
    /// </summary>
    private List<MemberDeclarationSyntax>? ExpandField(
        RecordPlan plan, FieldDeclarationSyntax field,
        Dictionary<(int FieldIndex, int VarIndex), MemberRewrite?> fieldRewriteMap,
        int origFieldIndex)
    {
        var replacements = new List<MemberDeclarationSyntax>();
        var anyPromotion = false;
        var index = 0;
        foreach (var variable in field.Declaration.Variables)
        {
            // 使用预构建的映射，避免对 visited 节点调用 GetDeclaredSymbol。
            var rewrite = fieldRewriteMap.GetValueOrDefault((origFieldIndex, index));
            if (rewrite is { Kind: MemberRewriteKind.FieldToInitProperty }
                or { Kind: MemberRewriteKind.PrivateFieldToInitProperty })
            {
                anyPromotion = true;
                var property = BuildPropertyFromField(field, variable, rewrite!, index == 0);
                replacements.Add(property);
                _context.Statistics.FieldsToProperties++;
            }
            else
            {
                // 同一声明里混着不转属性的变量：展开器不处理混合声明——Analyzer 保证
                // 同名字段分类逐符号进行；遇混合则退化为"保留原声明"，并由该处变量
                // 的 readonly 需求另行处理（见 VisitFieldDeclaration 不会触发时的说明）。
                return null;
            }
            index++;
        }
        return anyPromotion ? replacements : null;
    }

    private static PropertyDeclarationSyntax BuildPropertyFromField(
        FieldDeclarationSyntax field,
        VariableDeclaratorSyntax variable,
        MemberRewrite rewrite,
        bool keepTrivia)
    {
        var modifiers = field.Modifiers
            .Where(m => !m.IsKind(SyntaxKind.VolatileKeyword))
            .ToList();
        if (rewrite.IsRequired && !modifiers.Any(m => m.IsKind(SyntaxKind.RequiredKeyword)))
            modifiers.Add(SyntaxFactory.Token(SyntaxKind.RequiredKeyword).WithTrailingTrivia(SyntaxFactory.Space));

        var property = SyntaxFactory.PropertyDeclaration(field.Declaration.Type, variable.Identifier)
            .WithAttributeLists(field.AttributeLists)
            .WithModifiers(SyntaxFactory.TokenList(modifiers))
            .AddAccessorListAccessors(
                SyntaxFactory.AccessorDeclaration(SyntaxKind.GetAccessorDeclaration)
                    .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken)),
                SyntaxFactory.AccessorDeclaration(SyntaxKind.InitAccessorDeclaration)
                    .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken)));

        if (variable.Initializer is { } init)
        {
            property = property
                .WithInitializer(init)
                .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken));
        }

        property = property
            .WithLeadingTrivia(keepTrivia ? field.GetLeadingTrivia() : SyntaxFactory.TriviaList(SyntaxFactory.CarriageReturnLineFeed))
            .WithTrailingTrivia(keepTrivia ? field.GetTrailingTrivia() : SyntaxFactory.TriviaList())
            .WithAdditionalAnnotations(RecordSyntaxFacts.RewriteAnnotation);
        return property;
    }

    public override SyntaxNode? VisitPropertyDeclaration(PropertyDeclarationSyntax node)
    {
        var visited = (PropertyDeclarationSyntax)base.VisitPropertyDeclaration(node)!;
        var symbol = GetDeclaredSymbolRobust(node);
        var plan = ActivePlan(symbol?.ContainingType);
        if (plan == null || plan.Outcome != PlanOutcome.Converted) return visited;
        var rewrite = symbol == null ? null : FindRewrite(plan, symbol);
        if (rewrite?.Kind != MemberRewriteKind.SetToInit) return visited;

        var accessors = node.AccessorList!.Accessors;
        if (!accessors.Any(a => a.IsKind(SyntaxKind.SetAccessorDeclaration))) return visited;

        var newAccessors = new SyntaxList<AccessorDeclarationSyntax>();
        var replaced = false;
        foreach (var accessor in accessors)
        {
            if (accessor.IsKind(SyntaxKind.SetAccessorDeclaration))
            {
                replaced = true;
                var initAccessor = SyntaxFactory
                    .AccessorDeclaration(SyntaxKind.InitAccessorDeclaration)
                    .WithKeyword(SyntaxFactory.Token(SyntaxKind.InitKeyword)
                        .WithTrailingTrivia(SyntaxFactory.Space))
                    .WithAttributeLists(accessor.AttributeLists)
                    .WithModifiers(accessor.Modifiers) // RS-3：保留 private 等修饰
                    .WithBody(accessor.Body)           // RS-4/RS-9：保体
                    .WithExpressionBody(accessor.ExpressionBody)
                    .WithSemicolonToken(accessor.SemicolonToken)
                    .WithLeadingTrivia(accessor.GetLeadingTrivia())
                    .WithTrailingTrivia(accessor.GetTrailingTrivia())
                    .WithAdditionalAnnotations(RecordSyntaxFacts.RewriteAnnotation);
                newAccessors = newAccessors.Add(initAccessor);
            }
            else
            {
                newAccessors = newAccessors.Add(accessor);
            }
        }
        if (!replaced) return visited;
        _context.Statistics.MembersSetToInit++;
        return visited.WithAccessorList(node.AccessorList!.WithAccessors(newAccessors));
    }

    public override SyntaxNode? VisitFieldDeclaration(FieldDeclarationSyntax node)
    {
        var visited = (FieldDeclarationSyntax)base.VisitFieldDeclaration(node)!;
        // RS-6a：不提升属性、又非 readonly 的私有字段，加 readonly。
        var any = false;
        foreach (var variable in node.Declaration.Variables)
        {
            var symbol = GetDeclaredSymbolRobust(variable) as IFieldSymbol;
            var plan = ActivePlan(symbol?.ContainingType);
            var rewrite = symbol == null || plan == null ? null : FindRewrite(plan, symbol);
            if (rewrite?.Kind == MemberRewriteKind.PrivateFieldReadonly) any = true;
        }
        if (!any || visited.Modifiers.Any(m => m.IsKind(SyntaxKind.ReadOnlyKeyword))) return visited;
        var token = SyntaxFactory.Token(SyntaxKind.ReadOnlyKeyword)
            .WithTrailingTrivia(SyntaxFactory.Space);
        return visited
            .WithModifiers(SyntaxFactory.TokenList(visited.Modifiers.Concat(new[] { token })))
            .WithAdditionalAnnotations(RecordSyntaxFacts.RewriteAnnotation);
    }
}
