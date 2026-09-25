using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.RecordReadOnly;

internal sealed record MigrationUnit(
    IMethodSymbol Method,
    IReadOnlyList<ISymbol> PromotedMembers,
    bool IsVoid);

internal sealed record MigrationFailure(
    RecordReadOnlyDiagnosticCode Code,
    string Reason,
    Location Location);

/// <summary>MM-1：同 record 调用图上的影响集闭包；MM-6 的结构化否决。</summary>
internal static class MethodMigrationPlanner
{
    public static (Dictionary<IMethodSymbol, MigrationUnit> Units, MigrationFailure? Failure)
        BuildUnits(RecordInfo info)
    {
        var writeInfos = info.WriteInfos.Values
            .Where(w => !w.IsConstructor && !w.IsPropertySetter)
            .ToList();

        var influence = writeInfos.ToDictionary(
            w => w.Method.OriginalDefinition,
            w => new HashSet<ISymbol>(w.WrittenMembers, SymbolEqualityComparer.Default));

        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var w in writeInfos)
            foreach (var callee in w.SameRecordCalls)
            {
                if (!influence.TryGetValue(callee.OriginalDefinition, out var calleeInfluence))
                    continue;
                foreach (var member in calleeInfluence)
                    if (influence[w.Method.OriginalDefinition].Add(member)) changed = true;
            }
        }

        // MM-3 预备：this = expr 等价于整体重写实例状态。枚举全部实例字段/属性，
        // 判定转换后能否经 with 设置：
        //   属性（非索引器）：有 set/init 访问器（私有 init 在类型内亦可访问）；
        //   字段：当前可变（RS-5 转公共 init 属性、RS-6b 转私有 init 属性）。
        // 只读字段 / get-only 属性无法经 with 设置 → RR064 否决整条 record。
        var stateMembers = new List<(ISymbol Member, bool WithSettable)>();
        foreach (var member in info.Symbol.GetMembers())
        {
            if (member.IsStatic || member.IsImplicitlyDeclared) continue;
            switch (member)
            {
                case IPropertySymbol { IsIndexer: false } property:
                    stateMembers.Add((property, property.SetMethod != null));
                    break;
                case IFieldSymbol field:
                    stateMembers.Add((field,
                        info.MutableFields.Contains(field, SymbolEqualityComparer.Default)));
                    break;
            }
        }

        foreach (var w in writeInfos.Where(w => w.WritesThis))
        {
            var set = influence[w.Method.OriginalDefinition];
            var location = w.Method.Locations.FirstOrDefault(l => l.IsInSource) ?? Location.None;
            foreach (var (member, withSettable) in stateMembers)
            {
                if (!withSettable)
                    return (new(), new MigrationFailure(RecordReadOnlyDiagnosticCode.RR064,
                        $"'this = <expr>' rewrites every instance member, but '{member.Name}' " +
                        "cannot be set via 'with' after conversion (no set/init accessor) " +
                        "(MM-3/MM-6).", location));
                set.Add(member);
            }
        }

        var mutating = writeInfos
            .Where(w => influence[w.Method.OriginalDefinition].Count > 0 || w.WritesThis)
            .OrderBy(w => w.Method.Locations.FirstOrDefault(l => l.IsInSource)?.SourceSpan.Start ?? int.MaxValue)
            .ToList();

        var cycle = DetectCycle(mutating);
        if (cycle != null) return (new Dictionary<IMethodSymbol, MigrationUnit>(), cycle);

        foreach (var w in mutating)
        {
            var location = w.Method.Locations.FirstOrDefault(l => l.IsInSource) ?? Location.None;
            if (w.IsGetAccessor)
                return (new(), new MigrationFailure(RecordReadOnlyDiagnosticCode.RR063,
                    "a property/indexer getter mutates instance state; on a readonly record " +
                    "'this' is read-only inside getters (MM-6).", location));
            if (w.Iterator || w.Async)
                return (new(), new MigrationFailure(RecordReadOnlyDiagnosticCode.RR060,
                    "iterator/async methods occupy the return protocol and cannot change their " +
                    "signature to return the record (MM-6).", location));
            if (w.Partial)
                return (new(), new MigrationFailure(RecordReadOnlyDiagnosticCode.RR061,
                    "partial method signatures are bound across declarations and cannot be " +
                    "changed (MM-6).", location));
            if (w.IsOverrideExplicitOrVirtual || ImplementsInterfaceMethod(w.Method))
                return (new(), new MigrationFailure(RecordReadOnlyDiagnosticCode.RR020,
                    "virtual/override/interface-implementation methods are handled by interface " +
                    "ripple (IR) in a later stage.", location));
            if (!w.Method.ReturnsVoid)
                return (new(), new MigrationFailure(RecordReadOnlyDiagnosticCode.RR020,
                    "non-void mutating methods migrate to the 'out T result' shape in the next " +
                    "task (MM-5).", location));
        }

        var units = mutating.ToDictionary(
            w => w.Method,
            w => new MigrationUnit(
                w.Method,
                influence[w.Method.OriginalDefinition]
                    .OrderBy(m => m.Name, StringComparer.Ordinal)
                    .ThenBy(m => m.ToDisplayString(), StringComparer.Ordinal)
                    .ToList(),
                w.Method.ReturnsVoid));
        return (units, null);
    }

    private static MigrationFailure? DetectCycle(List<MethodWriteInfo> mutating)
    {
        var set = mutating.Select(w => w.Method.OriginalDefinition).ToHashSet(SymbolEqualityComparer.Default);
        var byOriginal = mutating.GroupBy(w => w.Method.OriginalDefinition)
            .ToDictionary(g => g.Key, g => g.First());

        var state = new Dictionary<ISymbol, int>(SymbolEqualityComparer.Default);
        foreach (var node in set)
        {
            var cycleAt = Dfs(node);
            if (cycleAt != null)
            {
                var w = byOriginal[(IMethodSymbol)cycleAt!];
                return new MigrationFailure(RecordReadOnlyDiagnosticCode.RR062,
                    "mutating methods form a call-graph cycle; a bottom-up migration order cannot " +
                    "be fixed (MM-1/MM-6).",
                    w.Method.Locations.FirstOrDefault(l => l.IsInSource) ?? Location.None);
            }
        }
        return null;

        ISymbol? Dfs(ISymbol node)
        {
            state.TryGetValue(node, out var mark);
            if (mark == 1) return node;
            if (mark == 2) return null;
            state[node] = 1;
            if (byOriginal.TryGetValue(node as IMethodSymbol ?? (IMethodSymbol)byOriginal.Keys.First(), out var w))
            {
                foreach (var callee in w.SameRecordCalls)
                {
                    if (!set.Contains(callee.OriginalDefinition)) continue;
                    var hit = Dfs(callee.OriginalDefinition);
                    if (hit != null) return hit;
                }
            }
            state[node] = 2;
            return null;
        }
    }

    private static bool ImplementsInterfaceMethod(IMethodSymbol method)
    {
        var type = method.ContainingType;
        foreach (var iface in type.AllInterfaces)
        foreach (var interfaceMethod in iface.GetMembers().OfType<IMethodSymbol>())
        {
            var impl = type.FindImplementationForInterfaceMember(interfaceMethod);
            if (impl == null) continue;
            if (SymbolEqualityComparer.Default.Equals(impl, method)
                || SymbolEqualityComparer.Default.Equals(
                    (impl as IMethodSymbol)?.PartialImplementationPart, method)
                || SymbolEqualityComparer.Default.Equals(
                    (impl as IMethodSymbol)?.PartialDefinitionPart, method))
                return true;
        }
        return false;
    }
}

/// <summary>全树扫描迁移方法调用点，判定吸收 / this 回写 / 外部回写可行性。</summary>
internal static class MethodCallSiteScanner
{
    public static List<(SitePlan Plan, bool WritebackThis)> Scan(
        Compilation compilation,
        RecordInfo owner,
        Dictionary<IMethodSymbol, MigrationUnit> units,
        out SiteFailure? failure)
    {
        failure = null;
        var result = new List<(SitePlan, bool)>();
        var unitsByOriginal = units.Keys.ToDictionary(
            m => m.OriginalDefinition, m => m, SymbolEqualityComparer.Default);

        foreach (var tree in compilation.SyntaxTrees)
        {
            var model = compilation.GetSemanticModel(tree);
            var root = tree.GetRoot();
            foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                if (model.GetSymbolInfo(invocation).Symbol is not IMethodSymbol callee) continue;
                if (!unitsByOriginal.TryGetValue(callee.OriginalDefinition, out var migrated))
                    continue;

                var enclosing = EnclosingMember(model, invocation);
                var nested = IsInNestedFunction(invocation);
                var implicitThis = invocation.Expression is IdentifierNameSyntax;
                var explicitThis = invocation.Expression
                    is MemberAccessExpressionSyntax { Expression: ThisExpressionSyntax };
                var receiverIsThis = implicitThis || explicitThis;

                if (receiverIsThis
                    && enclosing != null
                    && SymbolEqualityComparer.Default.Equals(
                        enclosing.ContainingType?.OriginalDefinition, owner.Symbol.OriginalDefinition))
                {
                    // 迁移方法体内直接调用：体改写时吸收结果，不生成外部用点。
                    if (!nested && unitsByOriginal.ContainsKey(enclosing.OriginalDefinition))
                        continue;
                    // MM-5：仅 ctor 内 this 可整体回写（struct ctor 内 this 赋值合法）。
                    // getter/init(set)/add/remove 访问器与 lambda/本地函数内 this 只读，
                    // 整体 this 赋值不合法，保守阻断（convert-or-skip）。
                    if (!nested && enclosing.MethodKind == MethodKind.Constructor)
                    {
                        result.Add((MakeSite(tree, invocation,
                            SyntaxFactory.ThisExpression(), owner, migrated), WritebackThis: true));
                        continue;
                    }
                    failure = new SiteFailure(RecordReadOnlyDiagnosticCode.RR050,
                        "calling a migrated method on 'this' here cannot write back: 'this' is " +
                        "read-only in this context (only a constructor can emit 'this = this.M(...)') " +
                        "(MM-5).", invocation.GetLocation());
                    return result;
                }

                var statementPosition =
                    invocation.FirstAncestorOrSelf<ExpressionStatementSyntax>() != null;
                if (!statementPosition)
                {
                    failure = new SiteFailure(RecordReadOnlyDiagnosticCode.RR053,
                        "a migrated-method call consumed in an expression context cannot be " +
                        "rewritten to a write-back statement (MM-5/MS-16).",
                        invocation.GetLocation());
                    return result;
                }

                var receiver = invocation.Expression switch
                {
                    MemberAccessExpressionSyntax ma => ma.Expression,
                    _ => invocation.Expression,
                };
                var receiverFailure = MutationSiteClassifier.ReceiverWritableAndPure(model, receiver);
                if (receiverFailure != null)
                {
                    failure = receiverFailure;
                    return result;
                }
                result.Add((MakeSite(tree, invocation, receiver, owner, migrated),
                    WritebackThis: false));
            }
        }
        return result;
    }

    private static SitePlan MakeSite(SyntaxTree tree, InvocationExpressionSyntax invocation,
        ExpressionSyntax receiver, RecordInfo owner, IMethodSymbol callee)
    {
        var site = new MutationSiteInfo
        {
            Location = invocation.GetLocation(),
            FilePath = tree.FilePath,
            WriteNode = invocation,
            Left = invocation,
            Receiver = receiver,
            ReceiverType = owner.Symbol,
            Kind = SiteKind.MethodCall,
            IsStatementPosition =
                invocation.FirstAncestorOrSelf<ExpressionStatementSyntax>() != null,
            MethodCallee = callee,
        };
        site.TargetRecord = owner;
        return new SitePlan(site, SiteActionKind.MethodMigrateCall);
    }

    private static IMethodSymbol? EnclosingMember(SemanticModel model, SyntaxNode node)
    {
        var accessor = node.FirstAncestorOrSelf<AccessorDeclarationSyntax>();
        if (accessor != null && model.GetDeclaredSymbol(accessor) is { } accessorMethod)
            return accessorMethod;
        var declaration = node.FirstAncestorOrSelf<BaseMethodDeclarationSyntax>();
        return declaration == null ? null : model.GetDeclaredSymbol(declaration);
    }

    private static bool IsInNestedFunction(SyntaxNode node)
    {
        SyntaxNode? stop = (SyntaxNode?)node.FirstAncestorOrSelf<BaseMethodDeclarationSyntax>()
            ?? (SyntaxNode?)node.FirstAncestorOrSelf<AccessorDeclarationSyntax>();
        for (var cursor = node.Parent; cursor != null && cursor != stop?.Parent; cursor = cursor.Parent)
        {
            if (cursor is AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax)
                return true;
            if (cursor == stop) return false;
        }
        return false;
    }
}

/// <summary>
/// Phase 3a：迁移方法声明（提升/出口合成/签名）与调用点回写；
/// 同时作为第一个 rewriter，为后续 MutationSiteRewriter 的用点节点挂定位注解。
/// </summary>
internal sealed class MethodMigrator : CSharpSyntaxRewriter
{
    private readonly RewriteContext _context;
    private readonly Dictionary<(int Start, int Kind), SitePlan> _callSites = new();
    private readonly Dictionary<(int Start, int Kind), SyntaxAnnotation> _msAnnotations = new();
    private readonly Dictionary<IMethodSymbol, MigrationUnit> _units =
        new(SymbolEqualityComparer.Default);
    private readonly HashSet<ISymbol> _migratedByOriginal;

    public MethodMigrator(RewriteContext context)
    {
        _context = context;
        foreach (var plan in context.ActivePlans)
        {
            foreach (var migration in plan.Migrations)
            {
                if (migration.Method.DeclaringSyntaxReferences.FirstOrDefault()?.SyntaxTree.FilePath
                        is { } file
                    && string.Equals(file, context.FilePath, StringComparison.OrdinalIgnoreCase))
                {
                    _units[migration.Method] = new MigrationUnit(
                        migration.Method, migration.PromotedMembers,
                        migration.Kind == MethodMigrationKind.VoidToRecord);
                }
            }
            foreach (var sitePlan in plan.Sites)
            {
                if (!string.Equals(sitePlan.Site.FilePath, context.FilePath,
                        StringComparison.OrdinalIgnoreCase))
                    continue;
                var key = (sitePlan.Site.WriteNode.SpanStart, sitePlan.Site.WriteNode.RawKind);
                if (sitePlan.Action == SiteActionKind.MethodMigrateCall)
                    _callSites[key] = sitePlan;
                else
                    _msAnnotations[key] = MutationSiteRewriter.SiteAnnotationFor(plan, sitePlan);
            }
        }
        _migratedByOriginal = _units.Keys
            .Select(m => (ISymbol)m.OriginalDefinition)
            .ToHashSet(SymbolEqualityComparer.Default);
    }

    public override SyntaxNode? Visit(SyntaxNode? node)
    {
        var visited = base.Visit(node);
        if (node != null && visited != null
            && _msAnnotations.TryGetValue((node.SpanStart, node.RawKind), out var annotation)
            && !visited.HasAnnotations(annotation.Kind))
        {
            visited = visited.WithAdditionalAnnotations(annotation);
        }
        return visited;
    }

    public override SyntaxNode? VisitMethodDeclaration(MethodDeclarationSyntax node)
    {
        var symbol = _context.Model.GetDeclaredSymbol(node);
        if (symbol == null || !_units.TryGetValue(symbol, out var unit))
            return base.VisitMethodDeclaration(node);

        var body = node.Body;
        if (body == null && node.ExpressionBody != null)
        {
            // 表达式体展开为块体（MM-4）。mutating void 箭头右侧只可能是赋值或调用表达式，
            // 二者本身都是合法表达式语句。
            body = SyntaxFactory.Block(
                SyntaxFactory.ExpressionStatement(node.ExpressionBody.Expression));
        }
        if (body == null) return base.VisitMethodDeclaration(node);

        var bodyRewriter = new MigratedMethodBodyRewriter(
            _context.Model, unit, _migratedByOriginal, _msAnnotations);
        var migrated = (BlockSyntax)bodyRewriter.Visit(body)!;

        // MM-2：方法体顶部为每个被提升成员声明局部副本：var __x = this.X;
        var promotionDeclarations = unit.PromotedMembers.Select(member =>
        {
            var localName = bodyRewriter.LocalNames[member];
            var declarator = SyntaxFactory.VariableDeclarator(
                    SyntaxFactory.Identifier(localName))
                .WithInitializer(SyntaxFactory.EqualsValueClause(
                    SyntaxFactory.MemberAccessExpression(
                        SyntaxKind.SimpleMemberAccessExpression,
                        SyntaxFactory.ThisExpression(),
                        SyntaxFactory.IdentifierName(member.Name))));
            return SyntaxFactory.LocalDeclarationStatement(
                SyntaxFactory.VariableDeclaration(
                    SyntaxFactory.ParseTypeName("var"),
                    SyntaxFactory.SingletonSeparatedList(declarator)));
        }).ToList();

        var returnStatement = SyntaxFactory.ReturnStatement(BuildWithThisReturn(unit, bodyRewriter));
        var finalBody = SyntaxFactory.Block(
            promotionDeclarations.Concat(migrated.Statements).Append(returnStatement));

        var recordTypeSyntax = SyntaxFactory.ParseTypeName(
            unit.Method.ContainingType.Name);
        _context.Statistics.MethodsMigrated++;
        return node.WithReturnType(recordTypeSyntax)
            .WithBody(finalBody)
            .WithExpressionBody(null)
            .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.None))
            .WithAdditionalAnnotations(RecordSyntaxFacts.RewriteAnnotation);
    }

    internal static ExpressionSyntax BuildWithThisReturn(
        MigrationUnit unit, MigratedMethodBodyRewriter bodyRewriter)
    {
        // 使用文本构造：以编程方式创建的 WithExpression 节点不含空白琐事，
        // 格式化器会将其展开为多行；文本解析保留单行紧凑格式。
        var memberAssigns = unit.PromotedMembers.Select(member =>
            $"{member.Name} = {bodyRewriter.LocalNames[member]}");
        var exprText = $"this with {{ {string.Join(", ", memberAssigns)} }}";
        return SyntaxFactory.ParseExpression(exprText);
    }

    public override SyntaxNode? VisitExpressionStatement(ExpressionStatementSyntax node)
    {
        if (node.Expression is InvocationExpressionSyntax invocation
            && _callSites.TryGetValue((node.Expression.SpanStart, node.Expression.RawKind),
                out var sitePlan))
        {
            var rewritten = RewriteCallWriteback(invocation, sitePlan.Site.Receiver);
            return SyntaxFactory.Block(
                SyntaxFactory.ExpressionStatement(rewritten)
                    .WithAdditionalAnnotations(RecordSyntaxFacts.RewriteAnnotation));
        }
        return base.VisitExpressionStatement(node);
    }

    /// <summary>调用点接收者：M() → this；x.M() → x。</summary>
    internal static ExpressionSyntax ReceiverOf(InvocationExpressionSyntax invocation) =>
        invocation.Expression switch
        {
            MemberAccessExpressionSyntax ma => ma.Expression,
            _ => SyntaxFactory.ThisExpression(),
        };

    /// <summary>recv.M(...); → recv = recv.M(...);（接收者可写性已由 scanner 判定）。</summary>
    internal static ExpressionSyntax RewriteCallWriteback(
        InvocationExpressionSyntax invocation, ExpressionSyntax receiver)
    {
        var receiverText = receiver.ToFullString();
        var receiverForCall = receiver.WithoutTrivia();
        InvocationExpressionSyntax callWithReceiver;
        if (invocation.Expression is IdentifierNameSyntax id)
        {
            callWithReceiver = invocation.WithExpression(
                SyntaxFactory.MemberAccessExpression(
                    SyntaxKind.SimpleMemberAccessExpression, receiverForCall,
                    SyntaxFactory.IdentifierName(id.Identifier.Text)));
        }
        else if (invocation.Expression is MemberAccessExpressionSyntax ma)
        {
            callWithReceiver = invocation.WithExpression(
                ma.WithExpression(receiverForCall));
        }
        else
        {
            callWithReceiver = invocation;
        }
        // LHS 必须是独立实例（同一节点不能同时挂在两个树位置），重新解析接收者文本。
        var lhs = SyntaxFactory.ParseExpression(receiverText);
        return SyntaxFactory.AssignmentExpression(
                SyntaxKind.SimpleAssignmentExpression, lhs, callWithReceiver)
            .WithAdditionalAnnotations(RecordSyntaxFacts.RewriteAnnotation);
    }
}

/// <summary>单个迁移方法体内的提升替换 / this= 拆解 / 同 record 调用吸收 / 出口替换。</summary>
internal sealed class MigratedMethodBodyRewriter : CSharpSyntaxRewriter
{
    private readonly SemanticModel _model;
    private readonly MigrationUnit _unit;
    private readonly IReadOnlySet<ISymbol> _migratedByOriginal;
    private readonly IReadOnlyDictionary<(int Start, int Kind), SyntaxAnnotation> _msAnnotations;
    private readonly Dictionary<ISymbol, string> _localByMember;
    private readonly HashSet<ISymbol> _promoted;
    private int _absorbedCounter;

    public MigratedMethodBodyRewriter(SemanticModel model, MigrationUnit unit,
        IReadOnlySet<ISymbol> migratedByOriginal,
        IReadOnlyDictionary<(int Start, int Kind), SyntaxAnnotation> msAnnotations)
    {
        _model = model;
        _unit = unit;
        _migratedByOriginal = migratedByOriginal;
        _msAnnotations = msAnnotations;
        var declaration = unit.Method.DeclaringSyntaxReferences.First().GetSyntax()
            .AncestorsAndSelf().OfType<MethodDeclarationSyntax>().First();
        _localByMember = BuildLocalNames(unit, declaration);
        _promoted = new HashSet<ISymbol>(unit.PromotedMembers, SymbolEqualityComparer.Default);
        LocalNames = _localByMember;
    }

    public IReadOnlyDictionary<ISymbol, string> LocalNames { get; }

    // 迁移方法子树不经过 MethodMigrator.Visit，MS 用点注解在此补发；
    // 这样后续 MutationSiteRewriter 仍能按注解改写体内的 other.X = 2 等用点。
    public override SyntaxNode? Visit(SyntaxNode? node)
    {
        var visited = base.Visit(node);
        if (node != null && visited != null
            && _msAnnotations.TryGetValue((node.SpanStart, node.RawKind), out var annotation)
            && !visited.HasAnnotations(annotation.Kind))
        {
            visited = visited.WithAdditionalAnnotations(annotation);
        }
        return visited;
    }

    public override SyntaxNode? VisitMemberAccessExpression(MemberAccessExpressionSyntax node)
    {
        if (node.Expression is ThisExpressionSyntax
            && _model.GetSymbolInfo(node).Symbol is { } symbol
            && _promoted.Contains(symbol) && !IsNameof(node))
        {
            return SyntaxFactory.IdentifierName(_localByMember[symbol]);
        }
        return base.VisitMemberAccessExpression(node);
    }

    public override SyntaxNode? VisitIdentifierName(IdentifierNameSyntax node)
    {
        if (_model.GetSymbolInfo(node).Symbol is { } symbol
            && _promoted.Contains(symbol)
            && symbol is IFieldSymbol or IPropertySymbol
            && !IsNameof(node)
            // 只替换隐式 this 的成员引用（X 而非 other.X）；
            // 显式 this.X 由 VisitMemberAccessExpression 处理。
            // 当 node 是 MemberAccessExpression.Name 且接收者非 this 时不替换。
            && !IsMemberAccessNameOnNonThis(node))
        {
            return SyntaxFactory.IdentifierName(_localByMember[symbol]);
        }
        return base.VisitIdentifierName(node);
    }

    /// <summary>判断 node 是否为 member-access 的 Name 部分、且接收者不是 this。</summary>
    private static bool IsMemberAccessNameOnNonThis(IdentifierNameSyntax node)
    {
        return node.Parent is MemberAccessExpressionSyntax ma
            && ReferenceEquals(ma.Name, node)
            && ma.Expression is not ThisExpressionSyntax;
    }

    public override SyntaxNode? VisitReturnStatement(ReturnStatementSyntax node)
    {
        if (node.Expression != null) return base.VisitReturnStatement(node);
        var visited = (ReturnStatementSyntax)base.VisitReturnStatement(node)!;
        return visited.WithExpression(MethodMigrator.BuildWithThisReturn(_unit, this));
    }

    public override SyntaxNode? VisitExpressionStatement(ExpressionStatementSyntax node)
    {
        // MM-3：this = expr; → 逐提升成员拷贝。RHS 先经 Visit，使其中对提升成员的
        // 读取同步替换为局部（this = Make(X); → __x = Make(__x).X;）。
        if (node.Expression is AssignmentExpressionSyntax
            {
                RawKind: (int)SyntaxKind.SimpleAssignmentExpression,
                Left: ThisExpressionSyntax,
            } assignment)
        {
            var visitedRight = (ExpressionSyntax)Visit(assignment.Right)!;
            var rightText = visitedRight.ToFullString();
            var statements = _unit.PromotedMembers.Select(member =>
            {
                // 每个成员各解析一份 RHS：同一节点实例不能挂在多个拷贝语句中。
                var rightCopy = SyntaxFactory.ParseExpression(rightText);
                return SyntaxFactory.ExpressionStatement(
                    SyntaxFactory.AssignmentExpression(
                        SyntaxKind.SimpleAssignmentExpression,
                        SyntaxFactory.IdentifierName(_localByMember[member]),
                        SyntaxFactory.MemberAccessExpression(
                            SyntaxKind.SimpleMemberAccessExpression,
                            rightCopy,
                            SyntaxFactory.IdentifierName(member.Name))));
            });
            return SyntaxFactory.Block(statements
                .WithNavigationAnnotation(RecordSyntaxFacts.RewriteAnnotation));
        }

        // MM-5（吸收）：this.M()/M()（被调方是迁移方法）→ var __r = this.M(...); fi = __r.Fi;
        if (node.Expression is InvocationExpressionSyntax invocation
            && IsCallToMigratedSameRecord(invocation))
        {
            // 先常规访问，使实参内对提升成员的读取替换为局部（B(X) → B(__x)）；
            // this.M 的 M 是方法符号、不在提升集合，成员访问保持不变。
            var visited = (InvocationExpressionSyntax)base.VisitInvocationExpression(invocation)!;
            var temp = $"__r{_absorbedCounter++}";
            var call = visited.Expression is IdentifierNameSyntax id
                ? visited.WithExpression(
                    SyntaxFactory.MemberAccessExpression(
                        SyntaxKind.SimpleMemberAccessExpression,
                        SyntaxFactory.ThisExpression(),
                        SyntaxFactory.IdentifierName(id.Identifier.Text)))
                : visited;
            var declaration = SyntaxFactory.LocalDeclarationStatement(
                SyntaxFactory.VariableDeclaration(
                    SyntaxFactory.ParseTypeName("var"),
                    SyntaxFactory.SingletonSeparatedList(
                        SyntaxFactory.VariableDeclarator(SyntaxFactory.Identifier(temp))
                            .WithInitializer(SyntaxFactory.EqualsValueClause(call)))));
            var copies = _unit.PromotedMembers.Select(member =>
                SyntaxFactory.ExpressionStatement(
                    SyntaxFactory.AssignmentExpression(
                        SyntaxKind.SimpleAssignmentExpression,
                        SyntaxFactory.IdentifierName(_localByMember[member]),
                        SyntaxFactory.MemberAccessExpression(
                            SyntaxKind.SimpleMemberAccessExpression,
                            SyntaxFactory.IdentifierName(temp),
                            SyntaxFactory.IdentifierName(member.Name)))));
            return SyntaxFactory.Block(new StatementSyntax[] { declaration }.Concat(copies)
                .WithNavigationAnnotation(RecordSyntaxFacts.RewriteAnnotation));
        }

        // 外部接收者（语句位）：other.B(); → other = other.B();。
        // 被调方迁移前是 void，源码中不可能在表达式消费位出现，故语句位即全部形态。
        if (node.Expression is InvocationExpressionSyntax external
            && IsCallToMigratedExternal(external))
        {
            var visitedCall = (InvocationExpressionSyntax)base.VisitInvocationExpression(external)!;
            var receiver = MethodMigrator.ReceiverOf(visitedCall);
            var writeback = MethodMigrator.RewriteCallWriteback(visitedCall, receiver);
            return SyntaxFactory.ExpressionStatement(writeback)
                .WithAdditionalAnnotations(RecordSyntaxFacts.RewriteAnnotation);
        }

        return base.VisitExpressionStatement(node);
    }

    /// <summary>
    /// MM-5（吸收）判据：解析出的 callee 属于本 record 的迁移单元集合（按 OriginalDefinition），
    /// 且调用接收者是 this 或隐式 this（M() / this.M()）。对其他接收者（other.M()）不在此吸收。
    /// </summary>
    private bool IsCallToMigratedSameRecord(InvocationExpressionSyntax invocation)
    {
        if (_model.GetSymbolInfo(invocation).Symbol is not IMethodSymbol callee) return false;
        if (!_migratedByOriginal.Contains(callee.OriginalDefinition)) return false;
        return invocation.Expression switch
        {
            IdentifierNameSyntax => true,
            MemberAccessExpressionSyntax { Expression: ThisExpressionSyntax } => true,
            _ => false,
        };
    }

    /// <summary>callee 在迁移单元集合、接收者非 this（other.M()/this.F.M() 等）。</summary>
    private bool IsCallToMigratedExternal(InvocationExpressionSyntax invocation)
    {
        if (_model.GetSymbolInfo(invocation).Symbol is not IMethodSymbol callee) return false;
        if (!_migratedByOriginal.Contains(callee.OriginalDefinition)) return false;
        return invocation.Expression is MemberAccessExpressionSyntax
            { Expression: not ThisExpressionSyntax };
    }

    private static bool IsNameof(ExpressionSyntax node)
    {
        var argument = node.FirstAncestorOrSelf<ArgumentSyntax>();
        return argument?.Parent?.Parent is InvocationExpressionSyntax invoked
            && invoked.Expression is IdentifierNameSyntax { Identifier.Text: "nameof" };
    }

    private static Dictionary<ISymbol, string> BuildLocalNames(
        MigrationUnit unit, MethodDeclarationSyntax declaration)
    {
        var taken = declaration.DescendantNodes().OfType<VariableDeclaratorSyntax>()
            .Select(v => v.Identifier.Text)
            .Concat(declaration.ParameterList.Parameters.Select(p => p.Identifier.Text))
            .ToHashSet(StringComparer.Ordinal);
        var map = new Dictionary<ISymbol, string>(SymbolEqualityComparer.Default);
        foreach (var member in unit.PromotedMembers)
        {
            var stem = member.Name.TrimStart('_');
            var candidate = "__" + (stem.Length == 0
                ? "x"
                : char.ToLower(stem[0]) + stem.Substring(1));
            while (taken.Contains(candidate)) candidate += "_";
            taken.Add(candidate);
            map[member] = candidate;
        }
        return map;
    }
}

internal static class RrStatementListExtensions
{
    public static IEnumerable<StatementSyntax> WithNavigationAnnotation(
        this IEnumerable<StatementSyntax> statements, SyntaxAnnotation annotation) =>
        statements.Select(s => s.WithAdditionalAnnotations(annotation));
}
