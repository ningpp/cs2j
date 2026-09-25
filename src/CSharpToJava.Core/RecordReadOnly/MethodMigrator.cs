using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Globalization;

namespace CSharpToJava.Core.RecordReadOnly;

internal sealed record MigrationUnit(
    IMethodSymbol Method,
    MethodMigrationKind Kind,
    IReadOnlyList<ISymbol> PromotedMembers)
{
    /// <summary>MM-7：扩展方法迁移单元。实例方法为 false（自身接收者即 this）。</summary>
    public bool IsExtension => (Kind & MethodMigrationKind.Extension) != 0;

    /// <summary>扩展方法的 this 形参；实例方法为 null。</summary>
    public IParameterSymbol? SelfParameter =>
        IsExtension && Method.Parameters.Length > 0 ? Method.Parameters[0] : null;
}

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
            // 隐式接口实现不标记 virtual，两个判定必须并列、接口在前（MM-6/IR）。
            if (ImplementsInterfaceMethod(w.Method))
                return (new(), new MigrationFailure(RecordReadOnlyDiagnosticCode.RR020,
                    "interface-implementation methods are handled by interface ripple (IR) " +
                    "in a later stage.", location));
            if (w.IsOverrideExplicitOrVirtual)
                return (new(), new MigrationFailure(RecordReadOnlyDiagnosticCode.RR065,
                    "a virtual/override/object-contract method cannot change its signature; " +
                    "only interface-implementation methods can be rerouted via IR (MM-6).",
                    location));
        }

        var units = mutating.ToDictionary(
            w => w.Method,
            w =>
            {
                var kind = (w.Method.ReturnsVoid
                        ? MethodMigrationKind.VoidToRecord
                        : MethodMigrationKind.NonVoidToOut)
                    | (w.Method.IsExtensionMethod ? MethodMigrationKind.Extension : 0);
                return new MigrationUnit(
                    w.Method,
                    kind,
                    influence[w.Method.OriginalDefinition]
                        .OrderBy(m => m.Name, StringComparer.Ordinal)
                        .ThenBy(m => m.ToDisplayString(), StringComparer.Ordinal)
                        .ToList());
            });
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
                var w = byOriginal[(IMethodSymbol)cycleAt];
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
            if (byOriginal.TryGetValue((IMethodSymbol)node, out var w))
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

/// <summary>
/// MS-15：方法组/委托绑定（Action&lt;R&gt; a = Bump; / new Func&lt;..&gt;(E.Bump) /
/// 实参传方法组 Use(Bump) / 箭头 => Bump）指向任一被迁移方法即否决。调用形态
/// Bump(...) / E.Bump(...) 由 MethodCallSiteScanner 处理，此处显式排除。
/// </summary>
internal static class MethodGroupBindingScanner
{
    public static MigrationFailure? Scan(
        Compilation compilation, IReadOnlyDictionary<IMethodSymbol, MigrationUnit> units)
    {
        var byOriginal = units.Keys.ToDictionary(
            m => m.OriginalDefinition, m => m, SymbolEqualityComparer.Default);

        foreach (var tree in compilation.SyntaxTrees)
        {
            var model = compilation.GetSemanticModel(tree);
            foreach (var name in tree.GetRoot().DescendantNodes()
                         .OfType<ExpressionSyntax>())
            {
                if (name is not (IdentifierNameSyntax or MemberAccessExpressionSyntax))
                    continue;
                if (name.Parent is InvocationExpressionSyntax invocation
                    && invocation.Expression == name)
                    continue;

                var symbolInfo = model.GetSymbolInfo(name);
                IEnumerable<ISymbol> candidates = symbolInfo.Symbol != null
                    ? new[] { symbolInfo.Symbol }
                    : symbolInfo.CandidateSymbols;
                if (!candidates.Any(s => s is IMethodSymbol m
                        && byOriginal.ContainsKey(m.OriginalDefinition)))
                    continue;
                if (!model.GetConversion(name).IsMethodGroup) continue;

                return new MigrationFailure(
                    RecordReadOnlyDiagnosticCode.RR058,
                    "a method-group/delegate binding targets a method whose signature " +
                    "changes during migration; the binding cannot compile afterwards (MS-15).",
                    name.GetLocation());
            }
        }
        return null;
    }
}

/// <summary>全树扫描迁移方法调用点，判定吸收 / this 回写 / 外部回写与提升可行性。</summary>
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
                if (model.GetSymbolInfo(invocation).Symbol is not IMethodSymbol rawCallee) continue;
                // reduced 扩展调用（r.M()）归一回原始静态方法后再查迁移单元。
                var callee = rawCallee.ReducedFrom ?? rawCallee;
                if (!unitsByOriginal.TryGetValue(callee.OriginalDefinition, out var migrated))
                    continue;

                var enclosing = EnclosingMember(model, invocation);
                var nested = IsInNestedFunction(invocation);
                var self = SelfArgument(invocation, callee, model);

                // 体吸收：调用位于一个自身也被迁移的方法/访问器体内（非嵌套函数），且作用
                // 接收者就是该体的自身接收者（实例方法 this；扩展方法 this 形参）。这类调用
                // 由 ForBody 重写器吸收为 var __r = ...，不生成外部用点。四种形态：
                //   实例体内 M()/this.M()；扩展体内 r.Inc()/r.A()/Bump(r)/E.Bump(r)；
                //   实例体内以静态形态调扩展 E.Bump(this)。
                if (self != null
                    && !nested
                    && enclosing != null
                    && unitsByOriginal.ContainsKey(enclosing.OriginalDefinition)
                    && IsOwnSelfReceiver(enclosing, self, model))
                {
                    continue;
                }

                // MM-5：self 为 this 时只在 struct ctor 内可整体回写；getter/init(set)/
                // add/remove 访问器与 lambda/本地函数内 this 只读，保守阻断。
                if (self is ThisExpressionSyntax
                    && enclosing != null
                    && SymbolEqualityComparer.Default.Equals(
                        enclosing.ContainingType?.OriginalDefinition, owner.Symbol.OriginalDefinition))
                {
                    if (!nested && enclosing.MethodKind == MethodKind.Constructor)
                    {
                        if (FindHoistBarrier(invocation) != null)
                        {
                            failure = new SiteFailure(RecordReadOnlyDiagnosticCode.RR053,
                                "a migrated-method call in a non-statement context cannot be " +
                                "hoisted to a 'this = this.M(...)' write-back (MM-5/MS-16).",
                                invocation.GetLocation());
                            return result;
                        }
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

                // void 迁移方法的调用只可能出现在语句位；非 void 迁移方法可在表达式内消费，
                // 但必须能提升前置语句（MM-5 三形态）。字段/属性初始化器、箭头表达式体、
                // 特性实参、查询子句、构造初始化器(: this/base)、无块 lambda、多变量声明
                // 都是不可提升屏障。
                if (FindHoistBarrier(invocation) != null)
                {
                    failure = new SiteFailure(RecordReadOnlyDiagnosticCode.RR053,
                        "a migrated-method call in a non-statement context (field/property " +
                        "initializer, expression-bodied member, attribute argument, query " +
                        "clause, constructor initializer, expression lambda, or multi-variable " +
                        "declarator) cannot be hoisted to a write-back statement (MM-5/MS-16).",
                        invocation.GetLocation());
                    return result;
                }

                if (self == null)
                {
                    failure = new SiteFailure(RecordReadOnlyDiagnosticCode.RR053,
                        "a migrated-method call in an unrecognized invocation form cannot be " +
                        "rewritten to a receiver write-back (MM-7/MS-19).",
                        invocation.GetLocation());
                    return result;
                }

                var receiverFailure = MutationSiteClassifier.ReceiverWritableAndPure(model, self);
                if (receiverFailure != null)
                {
                    failure = receiverFailure;
                    return result;
                }
                result.Add((MakeSite(tree, invocation, self, owner, migrated),
                    WritebackThis: false));
            }
        }
        return result;
    }

    /// <summary>
    /// 调用作用的“自身接收者”实参表达式：实例 M() → 合成 this；r.M()/this.M() → 点号
    /// 左侧；扩展静态形态 M(r)/E.M(r) → 首参。E.M(r) 中 M 左侧绑定到类型符号，需与
    /// reduced r.M()（左侧绑定到变量/形参）区分。必须在原始语法树节点上调用。
    /// </summary>
    internal static ExpressionSyntax? SelfArgument(
        InvocationExpressionSyntax invocation, IMethodSymbol callee, SemanticModel model)
    {
        switch (invocation.Expression)
        {
            case IdentifierNameSyntax:
                return callee.IsExtensionMethod
                    ? invocation.ArgumentList.Arguments.FirstOrDefault()?.Expression
                    : SyntaxFactory.ThisExpression();
            case MemberAccessExpressionSyntax ma:
                // this.M() 是 reduced 扩展的实例形态，接收者是 this（GetSymbolInfo(this)
                // 会解析到包含类型符号，必须先于类型限定判定）。
                if (ma.Expression is ThisExpressionSyntax) return ma.Expression;
                if (callee.IsExtensionMethod
                    && model.GetSymbolInfo(ma.Expression).Symbol is ITypeSymbol)
                {
                    return invocation.ArgumentList.Arguments.FirstOrDefault()?.Expression;
                }
                return ma.Expression;
            default:
                return null;
        }
    }

    private static bool IsOwnSelfReceiver(
        IMethodSymbol enclosing, ExpressionSyntax self, SemanticModel model)
    {
        if (self is ThisExpressionSyntax) return !enclosing.IsStatic;
        return enclosing.IsExtensionMethod
            && enclosing.Parameters.Length > 0
            && self is IdentifierNameSyntax id
            && SymbolEqualityComparer.Default.Equals(
                model.GetSymbolInfo(id).Symbol, enclosing.Parameters[0]);
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

    /// <summary>
    /// 从调用点向上走到最近方法/访问器/匿名函数/本地函数/编译单元边界；途中命中不可提升
    /// 语法形态即返回该屏障节点。有块 lambda 与有块本地函数不是屏障（块内可插入语句）。
    /// </summary>
    private static SyntaxNode? FindHoistBarrier(SyntaxNode node)
    {
        var cursor = node.Parent;
        while (cursor is not null
            and not BaseMethodDeclarationSyntax
            and not AccessorDeclarationSyntax
            and not AnonymousFunctionExpressionSyntax
            and not LocalFunctionStatementSyntax
            and not CompilationUnitSyntax)
        {
            switch (cursor)
            {
                case ArrowExpressionClauseSyntax arrow:
                    return arrow;
                case AttributeSyntax or AttributeArgumentSyntax:
                    return cursor;
                case ConstructorInitializerSyntax constructorInitializer:
                    return constructorInitializer;
                case QueryClauseSyntax queryClause:
                    return queryClause;
                case EqualsValueClauseSyntax { Parent: PropertyDeclarationSyntax }:
                    return cursor;
                case EqualsValueClauseSyntax
                {
                    Parent: VariableDeclaratorSyntax
                    {
                        Parent: VariableDeclarationSyntax
                        {
                            Parent: FieldDeclarationSyntax or EventFieldDeclarationSyntax
                        }
                    }
                }:
                    return cursor;
                case VariableDeclarationSyntax { Variables.Count: > 1 }:
                    return cursor;
            }
            cursor = cursor.Parent;
        }
        if (cursor is AnonymousFunctionExpressionSyntax { Block: null }
            or LocalFunctionStatementSyntax { Body: null, ExpressionBody: not null })
            return cursor;
        return null;
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
            ?? node.FirstAncestorOrSelf<AccessorDeclarationSyntax>();
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
/// 迁移调用点的共享语法重写器（MM-5 三形态 + 体内吸收 + this 回写）。
/// 设计要点：每条语句最多产出"一个提升后的语句块"，块用 HoistBlockAnnotation 标记，
/// 在最近的真实 Block/SwitchSection/CompilationUnit 处展平，保证声明语句提升后
/// 作用域不缩小（var v = recv.M() 的 v 在后续语句仍可见）。
/// </summary>
internal class MethodCallSyntaxRewriter : CSharpSyntaxRewriter
{
    private const string CallMarkKind = "Cs2jRecordReadOnlyMigratedCall";
    internal static readonly SyntaxAnnotation HoistBlockAnnotation =
        new("Cs2jRecordReadOnlyHoistBlock");

    protected readonly SemanticModel Model;
    protected readonly IReadOnlySet<IMethodSymbol> MigratedByOriginal;
    protected readonly IReadOnlyDictionary<(int Start, int Kind), SyntaxAnnotation> MsAnnotations;

    private readonly MigrationUnit? _unit;
    private readonly Dictionary<ISymbol, string>? _locals;
    private readonly string _resultName = "result";
    private readonly HashSet<string> _usedNames = new(StringComparer.Ordinal);

    private int _absorbedCounter;
    private int _outCounter;
    private bool _processingStatement;
    private readonly List<StatementSyntax> _prefixes = new();
    private string? _directCallData;
    private string? _directDeclaratorName;
    private Dictionary<string, IMethodSymbol>? _calleeByData;
    private Dictionary<string, string?>? _selfReceiverTextByData;
    private Dictionary<string, bool>? _selfIsOwnByData;
    private bool _mm3;
    private string? _mm3Source;

    protected MethodCallSyntaxRewriter(
        SemanticModel model,
        IReadOnlySet<IMethodSymbol> migratedByOriginal,
        IReadOnlyDictionary<(int Start, int Kind), SyntaxAnnotation> msAnnotations)
    {
        Model = model;
        MigratedByOriginal = migratedByOriginal;
        MsAnnotations = msAnnotations;
    }

    protected MethodCallSyntaxRewriter(
        SemanticModel model,
        MigrationUnit unit,
        IReadOnlySet<IMethodSymbol> migratedByOriginal,
        IReadOnlyDictionary<(int Start, int Kind), SyntaxAnnotation> msAnnotations,
        string resultName)
        : this(model, migratedByOriginal, msAnnotations)
    {
        _unit = unit;
        _resultName = resultName;
        var declaration = unit.Method.DeclaringSyntaxReferences.First().GetSyntax()
            .AncestorsAndSelf().OfType<MethodDeclarationSyntax>().First();
        _locals = BuildLocalNames(unit, declaration);
        LocalNames = _locals;
        foreach (var name in _locals.Values) _usedNames.Add(name);
    }

    internal static MethodCallSyntaxRewriter ForBody(
        SemanticModel model,
        MigrationUnit unit,
        IReadOnlySet<IMethodSymbol> migratedByOriginal,
        IReadOnlyDictionary<(int Start, int Kind), SyntaxAnnotation> msAnnotations,
        string resultName) =>
        new(model, unit, migratedByOriginal, msAnnotations, resultName);

    public IReadOnlyDictionary<ISymbol, string>? LocalNames { get; }

    internal ExpressionSyntax BuildRecordWith(ExpressionSyntax receiver)
    {
        var initializers = _unit!.PromotedMembers.Select(member =>
            (ExpressionSyntax)SyntaxFactory.AssignmentExpression(
                SyntaxKind.SimpleAssignmentExpression,
                SyntaxFactory.IdentifierName(member.Name),
                SyntaxFactory.IdentifierName(_locals![member])));
        return SyntaxFactory.WithExpression(receiver,
            SyntaxFactory.InitializerExpression(
                SyntaxKind.WithInitializerExpression,
                SyntaxFactory.SeparatedList(initializers)));
    }

    // 迁移方法子树不经过外层 Visit，MS 用点注解在此补发；这样后续 MutationSiteRewriter
    // 仍能按注解改写体内的 other.X = 2 等用点。
    public override SyntaxNode? Visit(SyntaxNode? node)
    {
        // 语句级拦截：Roslyn 的 CSharpSyntaxRewriter 没有 VisitStatement(StatementSyntax)，
        // 在此对非 Block 语句做迁移调用标记 + 提升 + 直接形态判定。
        if (node is StatementSyntax statement
            && !_processingStatement
            && statement is not BlockSyntax)
        {
            return ProcessStatement(statement);
        }

        var visited = base.Visit(node);
        if (node != null && visited != null
            && MsAnnotations.TryGetValue((node.SpanStart, node.RawKind), out var annotation)
            && !visited.HasAnnotations(annotation.Kind))
        {
            visited = visited.WithAdditionalAnnotations(annotation);
        }
        return visited;
    }

    private SyntaxNode? ProcessStatement(StatementSyntax node)
    {
        // 仅收集“直接属于本语句表达式部分”的迁移调用；嵌套块/lambda/本地函数内的调用
        // 由它们各自的语句 Visit 处理（FirstAncestor Statement 不等于本语句）。
        var marked = node.DescendantNodes(n =>
                n is not AnonymousFunctionExpressionSyntax and not LocalFunctionStatementSyntax)
            .OfType<InvocationExpressionSyntax>()
            .Where(invocation =>
                invocation.FirstAncestorOrSelf<StatementSyntax>() == node
                && Model.GetSymbolInfo(invocation).Symbol is IMethodSymbol raw
                && MigratedByOriginal.Contains((raw.ReducedFrom ?? raw).OriginalDefinition))
            .ToList();
        if (marked.Count == 0) return base.Visit(node);

        var dataBySpan = new Dictionary<int, string>();
        _calleeByData = new Dictionary<string, IMethodSymbol>();
        _selfReceiverTextByData = new Dictionary<string, string?>();
        _selfIsOwnByData = new Dictionary<string, bool>();
        for (var i = 0; i < marked.Count; i++)
        {
            var data = i.ToString(CultureInfo.InvariantCulture);
            dataBySpan[marked[i].SpanStart] = data;
            var rawSymbol = (IMethodSymbol)Model.GetSymbolInfo(marked[i]).Symbol!;
            // reduced 扩展调用归一回原始静态方法，保证 IsExtensionMethod/签名判定成立。
            var callee = rawSymbol.ReducedFrom ?? rawSymbol;
            _calleeByData[data] = callee;
            // 自身接收者的判定与文本必须在原始树节点上解析（prepared 克隆的 span 是
            // 语句相对的，脱离原树后无法查符号）；克隆树上只消费这里算好的结果。
            var selfArg = MethodCallSiteScanner.SelfArgument(marked[i], callee, Model);
            _selfReceiverTextByData[data] = selfArg?.WithoutTrivia().ToFullString();
            _selfIsOwnByData[data] = IsOwnSelfOf(selfArg);
        }
        var prepared = node.ReplaceNodes(marked,
            (original, _) => original.WithAdditionalAnnotations(
                new SyntaxAnnotation(CallMarkKind, dataBySpan[original.SpanStart])));

        _directCallData = null;
        _directDeclaratorName = null;
        MarkDirectForm(node, dataBySpan);

        _prefixes.Clear();
        _processingStatement = true;
        StatementSyntax visited;
        try
        {
            visited = (StatementSyntax)base.Visit(prepared)!;
        }
        finally
        {
            _processingStatement = false;
        }

        var prefixes = _prefixes.ToList();
        var direct = _directCallData != null;
        _directCallData = null;
        _directDeclaratorName = null;
        _calleeByData = null;
        _selfReceiverTextByData = null;
        _selfIsOwnByData = null;
        _mm3 = false;
        _mm3Source = null;

        if (direct)
        {
            // 丢弃调用 / 单变量声明确：原语句整体被前缀序列取代。
            return SyntaxFactory.Block(prefixes
                    .WithNavigationAnnotation(RecordSyntaxFacts.RewriteAnnotation))
                .WithAdditionalAnnotations(HoistBlockAnnotation);
        }
        if (prefixes.Count == 0) return visited;
        return SyntaxFactory.Block(prefixes.Concat(new[] { visited })
                .WithNavigationAnnotation(RecordSyntaxFacts.RewriteAnnotation))
            .WithAdditionalAnnotations(HoistBlockAnnotation);
    }

    public override SyntaxNode? VisitBlock(BlockSyntax node)
    {
        var visited = (BlockSyntax)base.VisitBlock(node)!;
        return TryFlatten(visited.Statements, out var flat)
            ? visited.WithStatements(SyntaxFactory.List(flat))
            : visited;
    }

    public override SyntaxNode? VisitSwitchSection(SwitchSectionSyntax node)
    {
        var visited = (SwitchSectionSyntax)base.VisitSwitchSection(node)!;
        return TryFlatten(visited.Statements, out var flat)
            ? visited.WithStatements(SyntaxFactory.List(flat))
            : visited;
    }

    public override SyntaxNode? VisitCompilationUnit(CompilationUnitSyntax node)
    {
        var visited = (CompilationUnitSyntax)base.VisitCompilationUnit(node)!;
        if (!visited.Members.Any(m => m is GlobalStatementSyntax
                { Statement: BlockSyntax block } && block.HasAnnotation(HoistBlockAnnotation)))
            return visited;
        var members = new List<MemberDeclarationSyntax>();
        foreach (var member in visited.Members)
        {
            if (member is GlobalStatementSyntax global
                && global.Statement is BlockSyntax wrapped
                && wrapped.HasAnnotation(HoistBlockAnnotation))
            {
                members.AddRange(wrapped.Statements.Select(
                    s => (MemberDeclarationSyntax)SyntaxFactory.GlobalStatement(s)));
            }
            else
            {
                members.Add(member);
            }
        }
        return visited.WithMembers(SyntaxFactory.List(members));
    }

    /// <summary>把带 HoistBlockAnnotation 的包装块就地展平为其语句序列。</summary>
    private static bool TryFlatten(IReadOnlyList<StatementSyntax> statements,
        out List<StatementSyntax> flat)
    {
        flat = new List<StatementSyntax>();
        var any = false;
        foreach (var statement in statements)
        {
            if (statement is BlockSyntax block && block.HasAnnotation(HoistBlockAnnotation))
            {
                any = true;
                flat.AddRange(block.Statements);
            }
            else
            {
                flat.Add(statement);
            }
        }
        return any;
    }

    private void MarkDirectForm(StatementSyntax node,
        IReadOnlyDictionary<int, string> dataBySpan)
    {
        if (node is ExpressionStatementSyntax { Expression: InvocationExpressionSyntax directCall }
            && dataBySpan.TryGetValue(directCall.SpanStart, out var d1))
        {
            _directCallData = d1;
            return;
        }
        if (node is LocalDeclarationStatementSyntax { Declaration.Variables.Count: 1 } declaration
            && declaration.Declaration.Variables[0].Initializer?.Value
                is InvocationExpressionSyntax declaratorCall
            && dataBySpan.TryGetValue(declaratorCall.SpanStart, out var d2))
        {
            _directCallData = d2;
            _directDeclaratorName = declaration.Declaration.Variables[0].Identifier.Text;
        }
    }

    public override SyntaxNode? VisitInvocationExpression(InvocationExpressionSyntax node)
    {
        var visited = (InvocationExpressionSyntax)base.VisitInvocationExpression(node)!;
        if (!_processingStatement || _calleeByData == null) return visited;
        var data = node.GetAnnotations(CallMarkKind).FirstOrDefault()?.Data;
        if (data == null || !_calleeByData.TryGetValue(data, out var callee)) return visited;
        return RewriteMarkedCall(visited, data, callee);
    }

    private ExpressionSyntax RewriteMarkedCall(
        InvocationExpressionSyntax visited, string data, IMethodSymbol callee)
    {
        var nonVoid = !callee.ReturnsVoid;
        var isDirect = data == _directCallData;
        // 自身接收者文本/吸收判定已在 ProcessStatement 于原始树节点上算好（克隆节点
        // 脱离原树后 span 是语句相对的，无法查符号）。
        _selfReceiverTextByData!.TryGetValue(data, out var receiverText);
        var absorb = _unit != null && _selfIsOwnByData![data];

        string? outName;
        if (!nonVoid)
        {
            outName = null;
        }
        else if (_mm3 && absorb)
        {
            // this = M()（MM-3 源是吸收临时变量 __r，原返回值被丢弃）。
            outName = "_";
        }
        else if (isDirect && _directDeclaratorName != null)
        {
            outName = _directDeclaratorName;
        }
        else if (isDirect)
        {
            outName = "_";
        }
        else
        {
            outName = NextSyntheticName(visited, "__o", ref _outCounter);
        }

        var call = nonVoid ? WithOutArgument(visited, outName!) : visited;

        if (absorb)
        {
            var temp = NextSyntheticName(visited, "__r", ref _absorbedCounter);
            // 仅实例方法体内的裸调用 M() 需要补 this.M()；扩展静态形态 Bump(p) 与
            // reduced p.M()/this.M() 已是合法形态，保持原样。
            var qualified = call.Expression is IdentifierNameSyntax id
                && !callee.IsExtensionMethod
                ? call.WithExpression(QualifyThis(id.Identifier.Text))
                : call;
            _prefixes.Add(DeclareVar(temp, qualified));
            // MM-3 场景的逐成员拷贝由 VisitExpressionStatement 用临时变量统一产出，避免重复。
            if (!_mm3)
            {
                foreach (var member in _unit!.PromotedMembers)
                    _prefixes.Add(SyntaxFactory.ExpressionStatement(
                        SyntaxFactory.AssignmentExpression(
                            SyntaxKind.SimpleAssignmentExpression,
                            SyntaxFactory.IdentifierName(_locals![member]),
                            Qualify(temp, member.Name))));
            }
            _mm3Source = temp;
            return isDirect ? visited : SyntaxFactory.IdentifierName(outName!);
        }

        // 非吸收：回写自身接收者（this 仅可能出现在 ctor——scanner 已放行）。
        // 扩展静态形态 Bump(r)/E.Bump(r)：接收者取首参，调用表达式本身原样保留，
        // 只在左侧产出 r = ...；reduced r.M() 同理。仅实例裸调用 M() 需补 this.M()。
        if (receiverText == null) return isDirect ? visited : SyntaxFactory.IdentifierName(outName!);
        ExpressionSyntax writebackCall;
        if (visited.Expression is IdentifierNameSyntax identifier && !callee.IsExtensionMethod)
        {
            writebackCall = call.WithExpression(
                SyntaxFactory.MemberAccessExpression(
                    SyntaxKind.SimpleMemberAccessExpression,
                    // LHS 必须是独立解析节点（同一节点不能同时挂两个树位置）。
                    SyntaxFactory.ParseExpression(receiverText),
                    SyntaxFactory.IdentifierName(identifier.Identifier.Text)));
        }
        else
        {
            writebackCall = call;
        }
        var lhs = SyntaxFactory.ParseExpression(receiverText);
        _prefixes.Add(SyntaxFactory.ExpressionStatement(
            SyntaxFactory.AssignmentExpression(
                    SyntaxKind.SimpleAssignmentExpression, lhs, writebackCall)
                .WithAdditionalAnnotations(RecordSyntaxFacts.RewriteAnnotation)));
        return isDirect ? visited : SyntaxFactory.IdentifierName(outName!);
    }

    internal static InvocationExpressionSyntax WithOutArgument(
        InvocationExpressionSyntax call, string name)
    {
        ExpressionSyntax outExpression = name == "_"
            ? SyntaxFactory.IdentifierName("_")
            : SyntaxFactory.DeclarationExpression(
                SyntaxFactory.IdentifierName(SyntaxFactory.Identifier("var")
                    .WithTrailingTrivia(SyntaxFactory.Space)),
                SyntaxFactory.SingleVariableDesignation(SyntaxFactory.Identifier(name)));
        var argument = SyntaxFactory.Argument(outExpression)
            .WithRefKindKeyword(SyntaxFactory.Token(SyntaxKind.OutKeyword)
                .WithTrailingTrivia(SyntaxFactory.Space));
        return call.AddArgumentListArguments(argument);
    }

    public override SyntaxNode? VisitExpressionStatement(ExpressionStatementSyntax node)
    {
        // MM-3：this = expr; → 逐提升成员拷贝。RHS 先经 Visit，使其中对提升成员的读取
        // 同步替换为局部（this = Make(X); → __x = Make(__x).X;）。
        if (_unit != null
            && node.Expression is AssignmentExpressionSyntax
                {
                    RawKind: (int)SyntaxKind.SimpleAssignmentExpression,
                    Left: ThisExpressionSyntax,
                } assignment)
        {
            _mm3 = true;
            _mm3Source = null;
            var visitedRight = (ExpressionSyntax)Visit(assignment.Right)!;
            var rhsPrefixes = _prefixes.ToList();
            _prefixes.Clear();

            // RHS 是对本 record 迁移方法的调用时，源对象是吸收临时（var __r = this.M(...)）；
            // 否则按 RHS 整体表达式逐成员解析拷贝（每个成员各解析一份，禁止节点复用）。
            var sourceText = visitedRight.WithoutTrivia().ToFullString();
            var copies = _unit.PromotedMembers.Select(member =>
            {
                var source = _mm3Source != null
                    ? (ExpressionSyntax)SyntaxFactory.IdentifierName(_mm3Source)
                    : SyntaxFactory.ParseExpression(sourceText);
                return SyntaxFactory.ExpressionStatement(
                    SyntaxFactory.AssignmentExpression(
                        SyntaxKind.SimpleAssignmentExpression,
                        SyntaxFactory.IdentifierName(_locals![member]),
                        SyntaxFactory.MemberAccessExpression(
                            SyntaxKind.SimpleMemberAccessExpression,
                            source,
                            SyntaxFactory.IdentifierName(member.Name))));
            });
            return SyntaxFactory.Block(rhsPrefixes.Concat(copies)
                .WithNavigationAnnotation(RecordSyntaxFacts.RewriteAnnotation));
        }
        return (SyntaxNode?)base.VisitExpressionStatement(node)!;
    }

    public override SyntaxNode? VisitReturnStatement(ReturnStatementSyntax node)
    {
        if (_unit == null) return base.VisitReturnStatement(node);
        var nonVoid = (_unit.Kind & MethodMigrationKind.NonVoidToOut) != 0;
        if (node.Expression is { } expression)
        {
            if (!nonVoid) return base.VisitReturnStatement(node);
            var visitedExpression = (ExpressionSyntax)Visit(expression)!;
            return SyntaxFactory.Block(
                SyntaxFactory.ExpressionStatement(
                        SyntaxFactory.AssignmentExpression(
                            SyntaxKind.SimpleAssignmentExpression,
                            SyntaxFactory.IdentifierName(_resultName),
                            visitedExpression))
                    .WithAdditionalAnnotations(RecordSyntaxFacts.RewriteAnnotation),
                SyntaxFactory.ReturnStatement(BuildRecordWith(SelfReceiver()))
                    .WithAdditionalAnnotations(RecordSyntaxFacts.RewriteAnnotation))
                .WithAdditionalAnnotations(HoistBlockAnnotation);
        }
        return SyntaxFactory.ReturnStatement(BuildRecordWith(SelfReceiver()))
            .WithAdditionalAnnotations(RecordSyntaxFacts.RewriteAnnotation);
    }

    public override SyntaxNode? VisitMemberAccessExpression(MemberAccessExpressionSyntax node)
    {
        var visited = base.VisitMemberAccessExpression(node);
        if (_locals != null
            && IsSelfMemberAccess(node)
            && TryGetSymbol(node, out var symbol)
            && _locals.ContainsKey(symbol)
            && !IsNameof(node))
        {
            return SyntaxFactory.IdentifierName(_locals[symbol]);
        }
        return visited;
    }

    /// <summary>
    /// 实例体只替换 this.F；扩展体替换 this 形参上的 r.F（左值右值都替换，
    /// r.X = r.X + v → __x = __x + v）。
    /// </summary>
    private bool IsSelfMemberAccess(MemberAccessExpressionSyntax node)
    {
        if (_unit?.SelfParameter == null)
            return node.Expression is ThisExpressionSyntax;
        return node.Expression is IdentifierNameSyntax id
            && TryGetSymbol(id, out var symbol)
            && SymbolEqualityComparer.Default.Equals(symbol, _unit.SelfParameter);
    }

    public override SyntaxNode? VisitIdentifierName(IdentifierNameSyntax node)
    {
        var visited = base.VisitIdentifierName(node);
        if (_locals != null
            && TryGetSymbol(node, out var symbol)
            && symbol is IFieldSymbol or IPropertySymbol
            && _locals.ContainsKey(symbol)
            && !IsNameof(node)
            && !IsMemberAccessNameOnNonThis(node))
        {
            return SyntaxFactory.IdentifierName(_locals[symbol]);
        }
        return visited;
    }

    /// <summary>
    /// 安全符号查找：节点经过 ReplaceNodes/WithAdditionalAnnotations 克隆后可能脱离
    /// 原始语法树，SemanticModel.GetSymbolInfo 会抛 ArgumentException。通过 Span 在
    /// 原始树中定位等价节点来解析符号。
    /// </summary>
    private bool TryGetSymbol(ExpressionSyntax node, out ISymbol? symbol)
    {
        symbol = null;
        if (node.SyntaxTree == Model.SyntaxTree)
        {
            symbol = Model.GetSymbolInfo(node).Symbol;
            return symbol != null;
        }
        // 克隆节点：用 Span 回原始树查找等价节点。Span 可能因克隆偏移而无法精确匹配，
        // 尝试宽容查找（findInsideTrivia = true）。
        try
        {
            var root = Model.SyntaxTree.GetRoot();
            var span = node.Span;
            if (span.Start < 0 || span.End > root.FullSpan.End) return false;
            var original = root.FindNode(span, findInsideTrivia: true, getInnermostNodeForTie: true);
            symbol = Model.GetSymbolInfo(original).Symbol;
            return symbol != null;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>判断 node 是否为 member-access 的 Name 部分、且接收者不是 this。</summary>
    private static bool IsMemberAccessNameOnNonThis(IdentifierNameSyntax node)
    {
        return node.Parent is MemberAccessExpressionSyntax ma
            && ReferenceEquals(ma.Name, node)
            && ma.Expression is not ThisExpressionSyntax;
    }

    private string NextSyntheticName(SyntaxNode anchor, string prefix, ref int counter)
    {
        var taken = EnclosingDeclaredNames(anchor);
        string candidate;
        do
        {
            candidate = prefix + counter.ToString(CultureInfo.InvariantCulture);
            counter++;
        } while (taken.Contains(candidate) || !_usedNames.Add(candidate));
        return candidate;
    }

    private static HashSet<string> EnclosingDeclaredNames(SyntaxNode node)
    {
        var scope = node.AncestorsAndSelf().FirstOrDefault(ancestor => ancestor is
            BaseMethodDeclarationSyntax
            or AccessorDeclarationSyntax
            or AnonymousFunctionExpressionSyntax
            or LocalFunctionStatementSyntax);
        var names = new HashSet<string>(StringComparer.Ordinal);
        if (scope == null) return names;
        foreach (var declarator in scope.DescendantNodes().OfType<VariableDeclaratorSyntax>())
            names.Add(declarator.Identifier.Text);
        var parameters = scope switch
        {
            BaseMethodDeclarationSyntax method => method.ParameterList.Parameters,
            ParenthesizedLambdaExpressionSyntax parenLambda => parenLambda.ParameterList.Parameters,
            SimpleLambdaExpressionSyntax simpleLambda when simpleLambda.Parameter != null =>
                SyntaxFactory.SingletonSeparatedList(simpleLambda.Parameter),
            LocalFunctionStatementSyntax local => local.ParameterList.Parameters,
            _ => default(SeparatedSyntaxList<ParameterSyntax>),
        };
        foreach (var parameter in parameters)
            names.Add(parameter.Identifier.Text);
        return names;
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

    /// <summary>调用点接收者：M() → this；x.M() → x。</summary>
    internal static ExpressionSyntax ReceiverOf(InvocationExpressionSyntax invocation) =>
        invocation.Expression switch
        {
            MemberAccessExpressionSyntax ma => ma.Expression,
            _ => SyntaxFactory.ThisExpression(),
        };

    /// <summary>
    /// 体模式吸收判定：调用的自身接收者是否就是本方法的 self（this 或 this 形参）。
    /// selfArg 必须来自原始语法树节点（语义查找依赖原树）。
    /// </summary>
    private bool IsOwnSelfOf(ExpressionSyntax? selfArg)
    {
        if (_unit == null || selfArg == null) return false;
        if (selfArg is ThisExpressionSyntax) return _unit.SelfParameter == null;
        return _unit.SelfParameter != null
            && selfArg is IdentifierNameSyntax id
            && Model.GetSymbolInfo(id).Symbol is { } symbol
            && SymbolEqualityComparer.Default.Equals(symbol, _unit.SelfParameter);
    }

    /// <summary>出口 with/提升源的接收者表达式：实例方法 this；扩展方法 this 形参（r）。</summary>
    internal ExpressionSyntax SelfReceiver() =>
        _unit!.SelfParameter == null
            ? SyntaxFactory.ThisExpression()
            : SyntaxFactory.IdentifierName(_unit.SelfParameter.Name);

    /// <summary>self 接收者上的成员访问：this.F（实例）或 r.F（扩展）。</summary>
    internal MemberAccessExpressionSyntax QualifySelf(string name) =>
        SyntaxFactory.MemberAccessExpression(
            SyntaxKind.SimpleMemberAccessExpression, SelfReceiver(),
            SyntaxFactory.IdentifierName(name));

    internal static LocalDeclarationStatementSyntax DeclareVar(
        string name, ExpressionSyntax initializer)
    {
        var declarator = SyntaxFactory.VariableDeclarator(SyntaxFactory.Identifier(name))
            .WithInitializer(SyntaxFactory.EqualsValueClause(initializer));
        var varType = SyntaxFactory.IdentifierName(SyntaxFactory.Identifier("var")
            .WithTrailingTrivia(SyntaxFactory.Space));
        return SyntaxFactory.LocalDeclarationStatement(
            SyntaxFactory.VariableDeclaration(
                varType, SyntaxFactory.SingletonSeparatedList(declarator)));
    }

    internal static MemberAccessExpressionSyntax QualifyThis(string name) =>
        SyntaxFactory.MemberAccessExpression(
            SyntaxKind.SimpleMemberAccessExpression,
            SyntaxFactory.ThisExpression(),
            SyntaxFactory.IdentifierName(name));

    internal static MemberAccessExpressionSyntax Qualify(string receiver, string name) =>
        SyntaxFactory.MemberAccessExpression(
            SyntaxKind.SimpleMemberAccessExpression,
            SyntaxFactory.IdentifierName(receiver),
            SyntaxFactory.IdentifierName(name));
}

/// <summary>
/// Phase 3c：迁移方法声明（提升 / 出口合成 / 签名）与全树调用点回写（外部模式逻辑
/// 全部在共享基类）；同时作为第一个 rewriter，为后续 MutationSiteRewriter 的用点节点
/// 挂定位注解。
/// </summary>
internal sealed class MethodMigrator : MethodCallSyntaxRewriter
{
    /// <summary>类型显示格式：完全限定但不加 global:: 前缀，特殊类型用关键字。</summary>
    private static readonly SymbolDisplayFormat TypeFormat =
        new SymbolDisplayFormat(
            typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
            globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Omitted,
            miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes);

    private readonly RewriteContext _context;
    private readonly Dictionary<IMethodSymbol, MigrationUnit> _units =
        new(SymbolEqualityComparer.Default);

    public MethodMigrator(RewriteContext context)
        : base(context.Model, BuildMigratedSet(context), BuildMsAnnotationMap(context))
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
                        migration.Method, migration.Kind, migration.PromotedMembers);
                }
            }
        }
    }

    private static IReadOnlySet<IMethodSymbol> BuildMigratedSet(RewriteContext context)
    {
        // 跨文件/跨 record 的调用也必须识别：集合取所有 active 计划的全部迁移方法。
        var set = new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default);
        foreach (var plan in context.ActivePlans)
            foreach (var migration in plan.Migrations)
                set.Add(migration.Method.OriginalDefinition);
        return set;
    }

    private static Dictionary<(int Start, int Kind), SyntaxAnnotation> BuildMsAnnotationMap(
        RewriteContext context)
    {
        var map = new Dictionary<(int, int), SyntaxAnnotation>();
        foreach (var plan in context.ActivePlans)
        {
            foreach (var sitePlan in plan.Sites)
            {
                if (!string.Equals(sitePlan.Site.FilePath, context.FilePath,
                        StringComparison.OrdinalIgnoreCase))
                    continue;
                if (sitePlan.Action == SiteActionKind.MethodMigrateCall) continue;
                var key = (sitePlan.Site.WriteNode.SpanStart, sitePlan.Site.WriteNode.RawKind);
                map[key] = MutationSiteRewriter.SiteAnnotationFor(plan, sitePlan);
            }
        }
        return map;
    }

    public override SyntaxNode? VisitMethodDeclaration(MethodDeclarationSyntax node)
    {
        var symbol = _context.Model.GetDeclaredSymbol(node);
        if (symbol == null || !_units.TryGetValue(symbol, out var unit))
            return base.VisitMethodDeclaration(node);

        var nonVoid = (unit.Kind & MethodMigrationKind.NonVoidToOut) != 0;
        var resultName = UniqueResultName(node);

        var bodyRewriter = ForBody(
            _context.Model, unit, MigratedByOriginal, MsAnnotations, resultName);

        var body = node.Body;
        if (body == null && node.ExpressionBody != null)
        {
            // 表达式体展开为块体（MM-4）。非 void：Bump() => X += 1;
            // → { result = __x += 1; }，尾部统一补 return this with {...}。
            // 先用 bodyRewriter 访问原始表达式（节点在原始树中，符号可解析），
            // 使 X 等成员引用替换为提升局部 __x，然后再包装为语句。
            var rewrittenExpr = (ExpressionSyntax)bodyRewriter.Visit(
                node.ExpressionBody.Expression)!;
            body = nonVoid
                ? SyntaxFactory.Block(SyntaxFactory.ExpressionStatement(
                    SyntaxFactory.AssignmentExpression(
                        SyntaxKind.SimpleAssignmentExpression,
                        SyntaxFactory.IdentifierName(resultName),
                        rewrittenExpr)))
                : SyntaxFactory.Block(
                    SyntaxFactory.ExpressionStatement(rewrittenExpr));
        }
        if (body == null) return base.VisitMethodDeclaration(node);

        var migrated = (BlockSyntax)bodyRewriter.Visit(body)!;

        // MM-2/MM-7：方法体顶部为每个被提升成员声明局部副本：
        // 实例方法 var __x = this.X;；扩展方法 var __x = r.X;。
        var promotionDeclarations = unit.PromotedMembers
            .Select(member => DeclareVar(
                bodyRewriter.LocalNames![member], bodyRewriter.QualifySelf(member.Name)))
            .ToList();

        // 仅当迁移体不以 return 结尾时才补尾部 return this with {...}；
        // VisitReturnStatement 已为非 void return 插入 return this with，
        // 此处不重复添加。
        var stmts = migrated.Statements;
        var lastIsReturn = stmts.Count > 0
            && stmts[^1] is ReturnStatementSyntax
            && stmts[^1].HasAnnotation(RecordSyntaxFacts.RewriteAnnotation);
        var allStatements = lastIsReturn
            ? promotionDeclarations.Concat(stmts)
            : promotionDeclarations.Concat(stmts).Append(
                SyntaxFactory.ReturnStatement(
                        bodyRewriter.BuildRecordWith(bodyRewriter.SelfReceiver()))
                    .WithAdditionalAnnotations(RecordSyntaxFacts.RewriteAnnotation));
        var finalBody = SyntaxFactory.Block(allStatements);

        // 扩展方法定义在静态类中，返回类型取 this 形参类型；实例方法取所在 record 类型。
        var migratedReturnType = unit.SelfParameter?.Type ?? unit.Method.ContainingType;
        var recordTypeSyntax = SyntaxFactory.ParseTypeName(
            migratedReturnType.ToDisplayString(TypeFormat));

        var parameters = node.ParameterList.Parameters;
        if (nonVoid)
        {
            // T M() → R M(out T result)。result 类型独立解析，避免与旧返回类型共享节点。
            var resultTypeSyntax = SyntaxFactory.ParseTypeName(
                symbol.ReturnType.ToDisplayString(TypeFormat));
            var resultParameter = SyntaxFactory.Parameter(SyntaxFactory.Identifier(resultName))
                .WithType(resultTypeSyntax)
                .WithModifiers(SyntaxFactory.TokenList(
                    SyntaxFactory.Token(SyntaxKind.OutKeyword)
                        .WithTrailingTrivia(SyntaxFactory.Space)));
            // MM-7：static R Next(this R r, out T result)——out 恒在 this 参之后。
            parameters = unit.IsExtension
                ? parameters.Insert(1, resultParameter)
                : parameters.Add(resultParameter);
        }

        _context.Statistics.MethodsMigrated++;
        return node.WithReturnType(recordTypeSyntax)
            .WithParameterList(SyntaxFactory.ParameterList(parameters))
            .WithBody(finalBody)
            .WithExpressionBody(null)
            .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.None))
            .WithAdditionalAnnotations(RecordSyntaxFacts.RewriteAnnotation);
    }

    private static string UniqueResultName(MethodDeclarationSyntax node)
    {
        var taken = node.ParameterList.Parameters.Select(p => p.Identifier.Text)
            .Concat(node.DescendantNodes().OfType<VariableDeclaratorSyntax>()
                .Select(v => v.Identifier.Text))
            .ToHashSet(StringComparer.Ordinal);
        var name = "result";
        while (taken.Contains(name)) name += "_";
        return name;
    }
}

internal static class RrStatementListExtensions
{
    public static IEnumerable<StatementSyntax> WithNavigationAnnotation(
        this IEnumerable<StatementSyntax> statements, SyntaxAnnotation annotation) =>
        statements.Select(s => s.WithAdditionalAnnotations(annotation));
}
