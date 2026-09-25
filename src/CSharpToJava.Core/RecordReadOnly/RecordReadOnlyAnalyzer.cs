using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.RecordReadOnly;

internal sealed record RewriteContext(
    SemanticModel Model,
    RecordAnalysis Analysis,
    ConversionPlan Plan,
    IReadOnlyList<RecordPlan> ActivePlans,
    IReadOnlySet<string> ExcludedPlanIds,
    RecordReadOnlyStatistics Statistics,
    List<RecordReadOnlyDiagnostic> Diagnostics,
    string FilePath);

internal enum RecordKind { Struct, Class }

internal enum SiteKind
{
    SimpleAssign,       // a.M = v
    CompoundAssign,     // a.M += v 等
    Increment,          // a.M++ / ++a.M
    ConditionalAssign,  // a?.M = v / a?.M += v / a?.M++（C#14，解析得到才会出现）
    IndexerAssign,      // r[i] = v（record 自身索引器）
    RefOutRecordArg,    // M(ref r) / M(out r)，r 整体是 record
    RefOutMemberArg,    // M(ref r.F) / int.TryParse(s, out r.N)
    AddressOfMember,    // &r.F
    MethodCall,         // 迁移方法的调用点（MM-5）
}

internal sealed record ChainSegment(ISymbol Member, ITypeSymbol? ContainerType, ExpressionSyntax Access);

internal sealed class MutationSiteInfo
{
    public required Location Location { get; init; }
    public required string FilePath { get; init; }
    public required SyntaxNode WriteNode { get; init; }
    public required ExpressionSyntax Left { get; init; }
    public required ExpressionSyntax Receiver { get; init; }
    /// <summary>最内层被写成员（a.B.C=v 时为 C）。</summary>
    public ISymbol? Member { get; init; }
    public required ITypeSymbol ReceiverType { get; init; }
    /// <summary>链最外层根接收者类型（a.B.C=v 时为 a 的类型）；MS-17 判定外层 record 用。</summary>
    public ITypeSymbol? RootReceiverType { get; init; }
    /// <summary>接收者静态类型正是某个接口（IR-4 接口接收者路径）时非空。</summary>
    public INamedTypeSymbol? ReceiverInterface { get; init; }
    public required SiteKind Kind { get; init; }
    public required bool IsStatementPosition { get; init; }
    public IMethodSymbol? EnclosingMethod { get; init; }
    public IMethodSymbol? RefOutCallee { get; init; }
    public bool CalleeInTree { get; init; }
    public List<ChainSegment> Chain { get; init; } = new();
    /// <summary>MethodCall：被调用的迁移方法符号。</summary>
    public IMethodSymbol? MethodCallee { get; set; }
    /// <summary>归属的目标 record（成员 ContainingType 命中分析清单时设置）。</summary>
    public RecordInfo? TargetRecord { get; set; }
}

internal sealed class MethodWriteInfo
{
    public required IMethodSymbol Method { get; init; }
    public required HashSet<ISymbol> WrittenMembers { get; init; } = new(SymbolEqualityComparer.Default);
    public bool WritesThis { get; set; }
    public bool Iterator { get; init; }
    public bool Async { get; init; }
    public bool Partial { get; init; }
    public bool IsGetAccessor { get; init; }
    public bool IsPropertySetter { get; init; }
    public bool IsExtension { get; init; }
    public bool ExpressionBody { get; init; }
    public bool IsOverrideExplicitOrVirtual { get; init; }
    public bool IsConstructor { get; init; }
    /// <summary>体内调用的、定义在同一 record 上的 in-tree 方法（MM-1 调用图边）。</summary>
    public List<IMethodSymbol> SameRecordCalls { get; } = new();
}

internal sealed class RecordInfo
{
    public required INamedTypeSymbol Symbol { get; init; }
    public required RecordKind Kind { get; init; }
    public required List<RecordDeclarationSyntax> Parts { get; init; }
    public bool AlreadyReadonly { get; set; }
    public bool IsPositional { get; set; }
    public bool HasExplicitLayout { get; set; }
    public bool HasFixedSizeBuffer { get; set; }
    public bool HasInstanceEvent { get; set; }
    public bool HasVisibleRefReturningMember { get; set; }
    public bool HasSettableIndexer { get; set; }
    public bool HasPrivateCopyConstructor { get; set; }

    public List<IPropertySymbol> SettableProperties { get; } = new();
    public List<IFieldSymbol> MutableFields { get; } = new();
    public List<IMethodSymbol> InTreeMethods { get; } = new();
    public Dictionary<IMethodSymbol, MethodWriteInfo> WriteInfos { get; }
        = new(SymbolEqualityComparer.Default);
    public List<MutationSiteInfo> Sites { get; } = new();

    public INamedTypeSymbol? BaseRecordType { get; set; }
    public IReadOnlyList<INamedTypeSymbol> Interfaces => Symbol.AllInterfaces;
}

internal sealed class InterfaceFact
{
    public required INamedTypeSymbol Interface { get; init; }
    public required HashSet<ISymbol> SettingMembers { get; init; } = new(SymbolEqualityComparer.Default);
    public required HashSet<INamedTypeSymbol> InTreeImplementers { get; init; }
        = new(SymbolEqualityComparer.Default);
}

internal sealed class RecordAnalysis
{
    public Compilation? Compilation { get; set; }
    public List<RecordInfo> Records { get; } = new();
    public Dictionary<INamedTypeSymbol, RecordInfo> BySymbol { get; }
        = new(SymbolEqualityComparer.Default);
    public Dictionary<INamedTypeSymbol, InterfaceFact> Interfaces { get; }
        = new(SymbolEqualityComparer.Default);
    /// <summary>全树出现 dynamic 接收者或反射 SetValue 调用（MS-14）。保守起见阻断全部 record。</summary>
    public bool HasDynamicOrReflectionMutation { get; set; }
}

internal sealed class RecordReadOnlyAnalyzer
{
    private readonly RecordReadOnlyOptions _options;
    private RecordAnalysis _analysis = new();
    private readonly HashSet<INamedTypeSymbol> _recordSymbols = new(SymbolEqualityComparer.Default);

    public RecordReadOnlyAnalyzer(RecordReadOnlyOptions options) => _options = options;

    public RecordAnalysis Analyze(Compilation compilation)
    {
        _analysis = new RecordAnalysis { Compilation = compilation };
        _recordSymbols.Clear();

        // Pass 1：record 符号与部件。
        foreach (var tree in compilation.SyntaxTrees)
        {
            var root = tree.GetRoot();
            var model = compilation.GetSemanticModel(tree);
            foreach (var decl in root.DescendantNodes().OfType<RecordDeclarationSyntax>())
            {
                if (model.GetDeclaredSymbol(decl) is not { } symbol || !symbol.IsRecord) continue;
                if (!_recordSymbols.Add(symbol)) continue;
                if (symbol.TypeKind == TypeKind.Struct)
                    CreateRecord(symbol, RecordKind.Struct, compilation);
                else if (_options.ConvertRecordClasses)
                    CreateRecord(symbol, RecordKind.Class, compilation);
            }
        }

        // Pass 2：成员/方法写集/接口/层次事实。
        foreach (var info in _analysis.Records.ToList())
            FillMemberFacts(info, compilation);

        // Pass 3：全树用点 + dynamic/反射扫描。
        foreach (var tree in compilation.SyntaxTrees)
            ScanTreeForSites(compilation, tree);

        // Pass 4：接口图（只收集 in-tree 接口与其 in-tree 实现者）。
        BuildInterfaceGraph(compilation);

        return _analysis;
    }

    private void CreateRecord(INamedTypeSymbol symbol, RecordKind kind, Compilation compilation)
    {
        var parts = symbol.DeclaringSyntaxReferences
            .Select(r => r.GetSyntax())
            .OfType<RecordDeclarationSyntax>()
            .ToList();
        var info = new RecordInfo
        {
            Symbol = symbol,
            Kind = kind,
            Parts = parts,
            BaseRecordType = kind == RecordKind.Class && symbol.BaseType is { IsRecord: true } b
                ? b : null,
        };
        info.AlreadyReadonly = kind == RecordKind.Struct && parts.Count > 0
            && parts.All(p => p.Modifiers.Any(m => m.IsKind(SyntaxKind.ReadOnlyKeyword)));
        info.IsPositional = parts.Any(p => p.ParameterList is { Parameters.Count: > 0 });
        info.HasExplicitLayout = kind == RecordKind.Struct && RecordSyntaxFacts.HasExplicitLayout(symbol);
        _analysis.Records.Add(info);
        _analysis.BySymbol[symbol] = info;
    }

    private void FillMemberFacts(RecordInfo info, Compilation compilation)
    {
        foreach (var member in info.Symbol.GetMembers())
        {
            switch (member)
            {
                case IPropertySymbol property:
                    if (property.IsIndexer)
                    {
                        if (property.SetMethod is { IsInitOnly: false }) info.HasSettableIndexer = true;
                        break;
                    }
                    // RS-12：静态属性不在 readonly struct 约束范围内，不收集。
                    if (!property.IsStatic && property.SetMethod is { } set && !set.IsInitOnly)
                        info.SettableProperties.Add(property);
                    if ((property.ReturnsByRef || property.ReturnsByRefReadonly)
                        && RecordSyntaxFacts.IsVisibleOutsideType(property))
                        info.HasVisibleRefReturningMember = true;
                    break;

                case IFieldSymbol field when !field.IsStatic && !field.IsConst:
                    if (field.IsVolatile)
                    {
                        info.MutableFields.Add(field); // PlanBuilder 据 IsVolatile 阻断
                    }
                    else
                    {
                        info.MutableFields.Add(field);
                    }
                    break;

                case IEventSymbol evt when !evt.IsStatic:
                    info.HasInstanceEvent = true;
                    break;

                case IMethodSymbol method when !method.IsStatic
                    && method.DeclaringSyntaxReferences.Length > 0:
                    if (RecordSyntaxFacts.IsRefReturning(method))
                        info.HasVisibleRefReturningMember = true;
                    break;
            }
        }

        // 源部件级：fixed 缓冲区；私有拷贝 ctor（class）。
        foreach (var part in info.Parts)
        {
            foreach (var field in part.DescendantNodes().OfType<FieldDeclarationSyntax>())
            {
                if (field.Modifiers.Any(SyntaxKind.FixedKeyword)) info.HasFixedSizeBuffer = true;
            }
        }

        if (info.Kind == RecordKind.Class)
        {
            info.HasPrivateCopyConstructor = info.Symbol.GetMembers()
                .OfType<IMethodSymbol>()
                .Any(m => m.MethodKind == MethodKind.Constructor
                    && m is { Parameters.Length: 1 }
                    && SymbolEqualityComparer.Default.Equals(m.Parameters[0].Type, info.Symbol)
                    && m.DeclaredAccessibility == Accessibility.Private);
        }

        // in-tree 方法与其写入成员集合（MM/RS-6 依据）。
        foreach (var member in info.Symbol.GetMembers().OfType<IMethodSymbol>())
        {
            if (member.DeclaringSyntaxReferences.Length == 0) continue;
            if (member.MethodKind is not (MethodKind.Ordinary or MethodKind.Constructor
                or MethodKind.PropertyGet or MethodKind.PropertySet or MethodKind.EventAdd
                or MethodKind.EventRemove))
                continue;
            info.InTreeMethods.Add(member);
            var wi = BuildWriteInfo(member, info, compilation);
            if (wi != null) info.WriteInfos[member] = wi;
        }
    }

    private MethodWriteInfo? BuildWriteInfo(IMethodSymbol method, RecordInfo owner,
        Compilation compilation)
    {
        // For partial methods, prefer the implementation part (has body) over the definition part.
        var effectiveMethod = method.PartialImplementationPart ?? method;
        var syntax = effectiveMethod.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax();
        BaseMethodDeclarationSyntax? declaration = syntax as BaseMethodDeclarationSyntax;
        AccessorDeclarationSyntax? accessor = syntax as AccessorDeclarationSyntax;
        SyntaxNode? bodyRoot = declaration is not null ? (SyntaxNode)declaration : accessor;
        if (bodyRoot == null) return null;

        var info = new MethodWriteInfo
        {
            Method = method,
            WrittenMembers = new(SymbolEqualityComparer.Default),
            Iterator = bodyRoot.DescendantNodes().Any(n =>
                n is YieldStatementSyntax),
            Async = method.IsAsync,
            Partial = method.PartialDefinitionPart != null || method.PartialImplementationPart != null
                || (declaration?.Modifiers.Any(m => m.IsKind(SyntaxKind.PartialKeyword)) ?? false),
            IsGetAccessor = method.MethodKind == MethodKind.PropertyGet,
            IsPropertySetter = method.MethodKind == MethodKind.PropertySet,
            IsExtension = method.IsExtensionMethod,
            ExpressionBody = declaration?.ExpressionBody != null,
            IsOverrideExplicitOrVirtual = method.IsOverride || method.IsVirtual || method.IsAbstract
                || (declaration as MethodDeclarationSyntax)?.ExplicitInterfaceSpecifier != null
                || (accessor is not null && accessor.Parent?.Parent is IndexerDeclarationSyntax { ExplicitInterfaceSpecifier: not null }
                    or PropertyDeclarationSyntax { ExplicitInterfaceSpecifier: not null }),
            IsConstructor = method.MethodKind == MethodKind.Constructor,
        };

        // 方法体与其声明同 tree；用该 tree 的语义模型解析体内成员符号。
        var model = compilation.GetSemanticModel(bodyRoot.SyntaxTree);
        foreach (var node in bodyRoot.DescendantNodes())
        {
            switch (node)
            {
                case AssignmentExpressionSyntax assign:
                    CollectWrittenSymbol(assign.Left, model, owner, info.WrittenMembers);
                    if (assign.Left is ThisExpressionSyntax) info.WritesThis = true;
                    break;
                case PostfixUnaryExpressionSyntax post when post.Kind()
                    is SyntaxKind.PostIncrementExpression or SyntaxKind.PostDecrementExpression:
                    CollectWrittenSymbol(post.Operand, model, owner, info.WrittenMembers);
                    break;
                case PrefixUnaryExpressionSyntax pre when pre.Kind()
                    is SyntaxKind.PreIncrementExpression or SyntaxKind.PreDecrementExpression:
                    CollectWrittenSymbol(pre.Operand, model, owner, info.WrittenMembers);
                    break;
                case ArgumentSyntax arg when arg.RefKindKeyword.RawKind != (int)SyntaxKind.None:
                    CollectWrittenSymbol(arg.Expression, model, owner, info.WrittenMembers);
                    break;
            }
        }

        // MM-1：同 record in-tree 调用边（按 OriginalDefinition 归一，兼容泛型实例化）。
        foreach (var invocation in bodyRoot.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (model.GetSymbolInfo(invocation).Symbol is not IMethodSymbol callee) continue;
            if (callee.MethodKind != MethodKind.Ordinary) continue;
            if (callee.DeclaringSyntaxReferences.Length == 0) continue;
            if (SymbolEqualityComparer.Default.Equals(
                    callee.ContainingType?.OriginalDefinition, owner.Symbol.OriginalDefinition))
            {
                info.SameRecordCalls.Add(callee);
            }
        }

        return info;
    }

    private static void CollectWrittenSymbol(
        ExpressionSyntax expression, SemanticModel? model, RecordInfo owner,
        HashSet<ISymbol> written)
    {
        if (model == null) return;
        var current = expression;
        // 剥掉 this. / 强制类型转换 / null 抑制。
        while (true)
        {
            switch (current)
            {
                // 仅收集 this.F 写（MM 提升的是 this 的字段）；other.F 由 MS 用点处理。
                case MemberAccessExpressionSyntax { Expression: ThisExpressionSyntax } m:
                    if (model.GetSymbolInfo(m).Symbol is { } s && SymbolBelongsTo(s, owner.Symbol))
                        written.Add(s);
                    return;
                case IdentifierNameSyntax id when model.GetSymbolInfo(id).Symbol is { } s2
                    && SymbolBelongsTo(s2, owner.Symbol):
                    written.Add(s2);
                    return;
                case CastExpressionSyntax cast: current = cast.Expression; continue;
                case PostfixUnaryExpressionSyntax { RawKind: (int)SyntaxKind.SuppressNullableWarningExpression } supp:
                    current = supp.Operand; continue;
                case DeclarationExpressionSyntax: return;
                default: return;
            }
        }
    }

    private static bool SymbolBelongsTo(ISymbol symbol, INamedTypeSymbol ownerType) =>
        symbol is IFieldSymbol or IPropertySymbol or IEventSymbol
        && SymbolEqualityComparer.Default.Equals(symbol.ContainingType, ownerType);

    private void ScanTreeForSites(Compilation compilation, SyntaxTree tree)
    {
        var root = tree.GetRoot();
        var model = compilation.GetSemanticModel(tree);

        foreach (var node in root.DescendantNodes())
        {
            switch (node)
            {
                case AssignmentExpressionSyntax assign:
                    HandleAssignment(compilation, tree, model, assign);
                    break;
                case PostfixUnaryExpressionSyntax post when post.Kind()
                    is SyntaxKind.PostIncrementExpression or SyntaxKind.PostDecrementExpression:
                    HandleIncrement(compilation, tree, model, post);
                    break;
                case PrefixUnaryExpressionSyntax pre when pre.Kind()
                    is SyntaxKind.PreIncrementExpression or SyntaxKind.PreDecrementExpression:
                    HandleIncrement(compilation, tree, model, pre);
                    break;
                case ArgumentSyntax arg when arg.RefKindKeyword.RawKind != (int)SyntaxKind.None:
                    HandleRefOutArgument(compilation, tree, model, arg);
                    break;
                case InvocationExpressionSyntax invocation when IsReflectionSetValue(model, invocation):
                    _analysis.HasDynamicOrReflectionMutation = true;
                    break;
            }

            if (node.DescendantNodesAndSelf().Any(n => n is IdentifierNameSyntax id
                    && model.GetTypeInfo(id).Type?.TypeKind == TypeKind.Dynamic))
            {
                // 仅当下文是赋值/调用写时才算；保守地标记，由 PlanBuilder 决定是否阻断。
                if (node is AssignmentExpressionSyntax or InvocationExpressionSyntax)
                    _analysis.HasDynamicOrReflectionMutation = true;
            }
        }
    }

    private void HandleAssignment(Compilation compilation, SyntaxTree tree, SemanticModel model,
        AssignmentExpressionSyntax assign)
    {
        if (assign.Left is not ExpressionSyntax left) return;

        // 解构 / 声明式赋值不用点处理。
        if (left is TupleExpressionSyntax or DeclarationExpressionSyntax) return;
        // 对象初始化器内的赋值（new R { X = 1 }）对 init 合法，不是用点。
        if (assign.FirstAncestorOrSelf<InitializerExpressionSyntax>()
            is { RawKind: (int)SyntaxKind.ObjectInitializerExpression })
        {
            return;
        }

        switch (left)
        {
            case ElementAccessExpressionSyntax element:
                HandleElementTarget(compilation, tree, model, assign, element);
                return;
            case ConditionalAccessExpressionSyntax:
                AddSite(compilation, tree, model, assign, left, SiteKind.ConditionalAssign);
                return;
            case MemberAccessExpressionSyntax { Expression: ConditionalAccessExpressionSyntax }:
                AddSite(compilation, tree, model, assign, left, SiteKind.ConditionalAssign);
                return;
        }

        if (left is MemberAccessExpressionSyntax)
        {
            var kind = assign.IsKind(SyntaxKind.SimpleAssignmentExpression)
                ? SiteKind.SimpleAssign
                : SiteKind.CompoundAssign;
            AddSite(compilation, tree, model, assign, left, kind);
        }
    }

    private void HandleElementTarget(Compilation compilation, SyntaxTree tree, SemanticModel model,
        AssignmentExpressionSyntax assign, ElementAccessExpressionSyntax element)
    {
        // r[i] = v：r 自身是 record（record 声明的索引器）→ MS-18。
        if (model.GetTypeInfo(element.Expression).Type is INamedTypeSymbol { IsRecord: true } rec
            && _analysis.BySymbol.ContainsKey(rec))
        {
            AddSite(compilation, tree, model, assign, element, SiteKind.IndexerAssign);
            return;
        }
        // arr[i].M = v：左侧成员访问形态被 VisitMemberAccess 视角看作普通赋值，
        // 这里 element 只是接收者；让通用 AddSite 从 assign.Left 解析（arr[i].M 形态）。
        if (assign.Left is MemberAccessExpressionSyntax)
            AddSite(compilation, tree, model, assign, assign.Left, SiteKind.SimpleAssign);
    }

    private void HandleIncrement(Compilation compilation, SyntaxTree tree, SemanticModel model,
        ExpressionSyntax inc)
    {
        if (inc is not (PrefixUnaryExpressionSyntax or PostfixUnaryExpressionSyntax)) return;
        var operand = inc is PrefixUnaryExpressionSyntax pre ? pre.Operand : ((PostfixUnaryExpressionSyntax)inc).Operand;
        // p?.X++ → operand 是 ConditionalAccessExpressionSyntax（MS-5 空条件自增）。
        if (operand is ConditionalAccessExpressionSyntax)
        {
            AddSite(compilation, tree, model, inc, operand, SiteKind.ConditionalAssign);
            return;
        }
        if (operand is not MemberAccessExpressionSyntax) return;
        // 表达式位置也登记：PlanBuilder 据此判 RR053（MS-4 只允许语句位置）。
        AddSite(compilation, tree, model, inc, operand, SiteKind.Increment);
    }

    private void HandleRefOutArgument(Compilation compilation, SyntaxTree tree, SemanticModel model,
        ArgumentSyntax arg)
    {
        var invocation = arg.FirstAncestorOrSelf<InvocationExpressionSyntax>();
        var callee = invocation is null ? null : model.GetSymbolInfo(invocation).Symbol as IMethodSymbol;
        var inTree = callee?.DeclaringSyntaxReferences.Length > 0;

        // ref/out 实参整体是 record（MS-10）。
        if (arg.Expression is not MemberAccessExpressionSyntax
            && model.GetTypeInfo(arg.Expression).Type is INamedTypeSymbol { IsRecord: true } whole
            && _analysis.BySymbol.ContainsKey(whole))
        {
            var site = new MutationSiteInfo
            {
                Location = arg.GetLocation(), FilePath = tree.FilePath, WriteNode = arg,
                Left = arg.Expression, Receiver = arg.Expression, ReceiverType = whole,
                Kind = SiteKind.RefOutRecordArg,
                IsStatementPosition = arg.FirstAncestorOrSelf<ExpressionStatementSyntax>() != null,
                RefOutCallee = callee, CalleeInTree = inTree,
            };
            AttachTarget(site, whole);
        }

        // ref/out 实参是 record 成员（MS-11/地址取址）。
        if (arg.Expression is MemberAccessExpressionSyntax or PrefixUnaryExpressionSyntax)
        {
            var inner = arg.Expression is PrefixUnaryExpressionSyntax { RawKind: (int)SyntaxKind.AddressOfExpression } addr
                ? addr.Operand : arg.Expression;
            if (inner is MemberAccessExpressionSyntax memberAccess)
            {
                // GetSymbolInfo 可能因编译错误返回 null（如对属性取 ref），
                // 此时从接收者类型上按名字查找成员（与 AddSite 相同策略）。
                var sym = model.GetSymbolInfo(memberAccess).Symbol;
                if (sym == null && model.GetTypeInfo(memberAccess.Expression).Type is INamedTypeSymbol rt)
                    sym = rt.GetMembers(memberAccess.Name.Identifier.Text).FirstOrDefault();
                if (sym is { ContainingType: { IsRecord: true } ct }
                    && _analysis.BySymbol.TryGetValue(ct, out var target))
                {
                    var site = new MutationSiteInfo
                    {
                        Location = arg.GetLocation(), FilePath = tree.FilePath, WriteNode = arg,
                        Left = memberAccess, Receiver = memberAccess.Expression,
                        Member = sym, ReceiverType = ct,
                        Kind = SiteKind.RefOutMemberArg,
                        IsStatementPosition = arg.FirstAncestorOrSelf<ExpressionStatementSyntax>() != null,
                        RefOutCallee = callee, CalleeInTree = inTree,
                    };
                    site.TargetRecord = target;
                    target.Sites.Add(site);
                }
            }
        }
    }

    private void AddSite(Compilation compilation, SyntaxTree tree, SemanticModel model,
        SyntaxNode writeNode, ExpressionSyntax left, SiteKind kind)
    {
        // 拆解成员链 a.B.C：从最内层向外收集，最外层成员的 ContainingType 即目标 record。
        // 注意：源码可能有编译错误（如 foreach 变量赋值、属性返回值类型成员修改），
        // 导致 GetSymbolInfo 返回 null。此时用 GetTypeInfo 推断接收者类型，
        // 再从类型上查找成员。
        var chain = new List<ChainSegment>();
        ExpressionSyntax? cursor = left;
        ISymbol? deepestMember = null;
        ITypeSymbol? receiverType = null;
        // record 成员的直接接收者表达式（如 t.a.X = 3 中 Receiver 应为 t.a，而非根 t）。
        ExpressionSyntax? recordMemberReceiver = null;

        // MS-5：p?.X = v → left 是 ConditionalAccessExpressionSyntax。
        // WhenNotNull 是 MemberBindingExpressionSyntax（.X），需要从中提取成员符号。
        // Receiver 设为整个 ConditionalAccessExpressionSyntax，以便重写器从中取根接收者。
        if (left is ConditionalAccessExpressionSyntax cae
            && cae.WhenNotNull is MemberBindingExpressionSyntax mbe)
        {
            var memberName = mbe.Name.Identifier.Text;
            var recvType = model.GetTypeInfo(cae.Expression).Type;
            if (recvType is INamedTypeSymbol rt)
            {
                // nullable struct P? → UnderlyingType 是 P
                var effectiveType = rt.NullableAnnotation == NullableAnnotation.Annotated
                    && rt.OriginalDefinition.SpecialType != SpecialType.System_Nullable_T
                        ? rt
                        : (rt is { IsValueType: true, OriginalDefinition.SpecialType: SpecialType.System_Nullable_T }
                            ? ((INamedTypeSymbol)rt).TypeArguments[0]
                            : rt);
                deepestMember = effectiveType.GetMembers(memberName).FirstOrDefault();
                receiverType = effectiveType;
                recordMemberReceiver = left; // 整个 p?.X 作为 Receiver
                chain.Add(new ChainSegment(deepestMember!, effectiveType, left));
            }
            cursor = cae.Expression;
        }
        else
        {
            while (cursor is MemberAccessExpressionSyntax ma)
            {
                var sym = model.GetSymbolInfo(ma).Symbol;
                var recvType = model.GetTypeInfo(ma.Expression).Type;
                // 若 symbol 查找失败（编译错误场景），尝试从接收者类型上按名字查找成员。
                if (sym == null && recvType is INamedTypeSymbol rt2)
                    sym = rt2.GetMembers(ma.Name.Identifier.Text).FirstOrDefault();
                chain.Add(new ChainSegment(sym!, recvType, ma));
                if (deepestMember == null)
                {
                    deepestMember = sym;
                    recordMemberReceiver = ma.Expression;
                }
                receiverType ??= recvType;
                cursor = ma.Expression;
            }
            if (cursor is ElementAccessExpressionSyntax)
            {
                receiverType ??= model.GetTypeInfo(cursor).Type;
            }
        }
        if (deepestMember?.ContainingType is not { IsRecord: true } targetType) return;
        if (!_analysis.BySymbol.TryGetValue(targetType, out var target)) return;
        if (receiverType == null) return;

        // this.F / 隐式 this 写发生在 record 自身方法/访问器（含本地函数/lambda 所在的
        // 外层成员）内时，属 MM（字段提升）或 ctor/init 访问器的合法路径，不登记为 MS 用点。
        SyntaxNode? enclosingDecl = (SyntaxNode?)writeNode.FirstAncestorOrSelf<BaseMethodDeclarationSyntax>()
            ?? writeNode.FirstAncestorOrSelf<AccessorDeclarationSyntax>();
        if (cursor is ThisExpressionSyntax
            && enclosingDecl != null
            && model.GetDeclaredSymbol(enclosingDecl) is IMethodSymbol enclosingSymbol
            && SymbolEqualityComparer.Default.Equals(enclosingSymbol.ContainingType, targetType))
        {
            return;
        }

        INamedTypeSymbol? receiverInterface = receiverType.TypeKind == TypeKind.Interface
            ? (INamedTypeSymbol)receiverType : null;
        var rootReceiverType = model.GetTypeInfo(cursor ?? left).Type ?? receiverType;

        var site = new MutationSiteInfo
        {
            Location = writeNode.GetLocation(),
            FilePath = tree.FilePath,
            WriteNode = writeNode,
            Left = left,
            Receiver = recordMemberReceiver ?? cursor ?? left,
            Member = deepestMember,
            ReceiverType = receiverType,
            RootReceiverType = rootReceiverType,
            ReceiverInterface = receiverInterface,
            Kind = kind,
            IsStatementPosition = writeNode.FirstAncestorOrSelf<ExpressionStatementSyntax>() != null
                || writeNode.FirstAncestorOrSelf<GlobalStatementSyntax>() != null,
            EnclosingMethod = writeNode.FirstAncestorOrSelf<BaseMethodDeclarationSyntax>() is { } md
                ? model.GetDeclaredSymbol(md) : null,
            Chain = chain,
        };
        site.TargetRecord = target;
        target.Sites.Add(site);
    }

    private void AttachTarget(MutationSiteInfo site, INamedTypeSymbol whole)
    {
        if (_analysis.BySymbol.TryGetValue(whole, out var target))
        {
            site.TargetRecord = target;
            target.Sites.Add(site);
        }
    }

    private static bool IsReflectionSetValue(SemanticModel model, InvocationExpressionSyntax invocation)
    {
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "SetValue" })
            return false;
        if (model.GetSymbolInfo(invocation).Symbol is not IMethodSymbol method) return false;
        return method.ContainingType.ToDisplayString()
            is "System.Reflection.PropertyInfo" or "System.Reflection.FieldInfo";
    }

    private void BuildInterfaceGraph(Compilation compilation)
    {
        var inTreeTypes = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
        foreach (var tree in compilation.SyntaxTrees)
        {
            var model = compilation.GetSemanticModel(tree);
            foreach (var typeDecl in tree.GetRoot().DescendantNodes()
                         .OfType<BaseTypeDeclarationSyntax>())
            {
                if (model.GetDeclaredSymbol(typeDecl) is INamedTypeSymbol t) inTreeTypes.Add(t);
            }
        }

        foreach (var record in _analysis.Records)
        {
            foreach (var iface in record.Symbol.AllInterfaces)
            {
                if (!RecordSyntaxFacts.IsInTree(iface)) continue;
                var setting = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
                foreach (var m in iface.GetMembers())
                {
                    if (m is IPropertySymbol { SetMethod: { IsInitOnly: false } } p) setting.Add(p);
                    if (m is IEventSymbol) setting.Add(m);
                }
                if (setting.Count == 0
                    && !record.WriteInfos.Keys.Any(km => km.ExplicitInterfaceImplementations.Any()
                        || InterfaceDeclaresMethod(iface, km)))
                {
                    // 方法连带在 M4 计划阶段按迁移方法精确匹配；此处保留接口节点。
                }
                if (!_analysis.Interfaces.TryGetValue(iface, out var fact))
                {
                    fact = new InterfaceFact
                    {
                        Interface = iface,
                        SettingMembers = new(SymbolEqualityComparer.Default),
                        InTreeImplementers = new(SymbolEqualityComparer.Default),
                    };
                    foreach (var t in inTreeTypes)
                    {
                        if (SymbolEqualityComparer.Default.Equals(t, iface)) continue;
                        if (t.AllInterfaces.Contains(iface, SymbolEqualityComparer.Default))
                            fact.InTreeImplementers.Add(t);
                    }
                    _analysis.Interfaces[iface] = fact;
                }
                foreach (var s in setting) fact.SettingMembers.Add(s);
            }
        }
    }

    private static bool InterfaceDeclaresMethod(INamedTypeSymbol iface, IMethodSymbol method)
        => iface.GetMembers().OfType<IMethodSymbol>()
            .Any(m => m.Name == method.Name && m.Parameters.Length == method.Parameters.Length);

    public static void ReportSkippedRecords(
        ConversionPlan plan,
        IReadOnlySet<string> excludedPlanIds,
        string filePath,
        RecordReadOnlyStatistics statistics,
        List<RecordReadOnlyDiagnostic> diagnostics,
        RecordReadOnlyOptions options)
    {
        foreach (var p in plan.Plans)
        {
            if (!p.TouchedFiles.Contains(filePath)) continue;
            if (excludedPlanIds.Contains(p.Id)) continue;
            if (p.Reported) continue;
            p.Reported = true;

            if (p.Outcome == PlanOutcome.Converted)
            {
                if (p.Kind == RecordKind.Struct) statistics.RecordStructsConverted++;
                else statistics.RecordClassesConverted++;
                if (options.ReportInfo)
                    diagnostics.Add(p.InfoDiagnostic(RecordReadOnlyDiagnosticCode.RR001,
                        $"record {(p.Kind == RecordKind.Struct ? "struct" : "class")} converted."));
                continue;
            }
            if (p.Outcome == PlanOutcome.AlreadyReadOnly)
            {
                statistics.AlreadyReadOnly++;
                if (options.ReportInfo)
                    diagnostics.Add(p.InfoDiagnostic(RecordReadOnlyDiagnosticCode.RR010,
                        "already a readonly record struct; skipped."));
                continue;
            }
            if (p.Outcome == PlanOutcome.UserSkipped)
            {
                statistics.RecordsSkipped++;
                if (options.ReportInfo)
                    diagnostics.Add(p.InfoDiagnostic(RecordReadOnlyDiagnosticCode.RR011,
                        "excluded by overrides.skip."));
                continue;
            }
            // Infeasible：原子跳过。
            statistics.RecordsSkipped++;
            statistics.SkippedByReason[p.FailCode!.Value] =
                statistics.SkippedByReason.GetValueOrDefault(p.FailCode.Value) + 1;
            diagnostics.Add(p.FailDiagnostic());
        }
    }
}
