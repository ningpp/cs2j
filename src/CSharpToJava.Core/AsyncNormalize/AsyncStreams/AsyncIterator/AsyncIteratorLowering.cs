using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.AsyncNormalize.Lambda;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace CSharpToJava.Core.AsyncNormalize.AsyncStreams.AsyncIterator;

/// <summary>
/// C38 M4 联合异步迭代器降级（设计 D7、§3.2 AS-6、§4.4）：把可降级的 async 迭代器
/// 方法（<c>async IAsyncEnumerable&lt;T&gt;</c>/<c>IAsyncEnumerator&lt;T&gt;</c> + yield）
/// 重写为「非 async 工厂方法 + 私有嵌套枚举器类（+ 可枚举包装类）」。
/// <para>枚举器的 <c>MoveNextAsync</c> 仍是 <c>public async ValueTask&lt;bool&gt;</c>：真实
/// await 全部保留（异步挂起由 async 方法自身完成），只有 yield 跨调用经 state/goto 持久化。
/// 控制流扁平化遵循 C# goto 作用域规则（goto 不能跳入 try/循环/if/switch section）：
/// if/switch/循环被线性化为同块级 goto 流（不引入新块作用域）；try-finally（含 await
/// foreach/await using 展开产物与同步 using/foreach 区域）保留为真 try 节点，采用
/// 「层外重入标签 <c>__cs2jL_</c> + 层内边界 switch + 链式 goto 分发 + run 标志位」：
/// 挂起时活跃区域链的 run 标志位置 false（finally 跳过释放），重入时逐层置 true。</para>
/// <para>方法论复用 C04（plan/naming/capture、保守活性提升），但 CFG 未照抄 C04：
/// C04 的 BodyTransformRewriter 仅把 yield 替换为嵌套 Block（标签跨块作用域，源码不可
/// 编译、仅文本断言）；本实现产出真扁平、可编译的 async MoveNextAsync/DisposeAsync。</para>
/// </summary>
internal sealed class AsyncIteratorLowering
{
    private static readonly SyntaxAnnotation Synthetic =
        new(AsyncNormalizeAnnotations.AnnotationKind);

    private readonly SemanticModel _model;
    private readonly INamedTypeSymbol? _iDisposable;

    public AsyncIteratorLowering(SemanticModel model)
    {
        _model = model;
        _iDisposable = model.Compilation.GetSpecialType(SpecialType.System_IDisposable);
    }

    public bool Changed { get; private set; }
    public int IteratorsLowered { get; private set; }
    public int ClassesEmitted { get; private set; }
    public int RegionsEmitted { get; private set; }

    public CompilationUnitSyntax Rewrite(
        CompilationUnitSyntax root,
        IReadOnlyList<AsyncIteratorPlan> plans)
    {
        if (plans.Count == 0)
            return root;

        var entries = new List<MethodLowering>();
        foreach (var plan in plans)
            entries.Add(new MethodLowering(this, plan, _model, _iDisposable));

        var newRoot = root;
        foreach (var typeGroup in entries.GroupBy(e => e.Plan.Node.Parent).OfType<
                     IGrouping<SyntaxNode, MethodLowering>>())
        {
            if (typeGroup.Key is not TypeDeclarationSyntax hostType)
                continue; // Planner 已拒绝非类型宿主，防御。

            var groupList = typeGroup.ToList();
            var newMembers = new List<MemberDeclarationSyntax>();
            foreach (var member in hostType.Members)
            {
                var entry = groupList.FirstOrDefault(e => ReferenceEquals(e.Plan.Node, member));
                if (entry != null)
                    newMembers.Add(entry.FactoryMethod);
                else
                    newMembers.Add(member);
            }

            foreach (var entry in groupList)
            {
                newMembers.Add(entry.EnumeratorClass);
                if (entry.WrapperClass != null)
                    newMembers.Add(entry.WrapperClass);

                IteratorsLowered++;
                ClassesEmitted += entry.WrapperClass != null ? 2 : 1;
                RegionsEmitted += entry.FlagRegionCount;
            }

            var rebuiltHost = hostType.WithMembers(List(newMembers));
            newRoot = (CompilationUnitSyntax)newRoot.ReplaceNode(hostType, rebuiltHost)!;
        }

        Changed = true;
        return newRoot;
    }

    internal static TypeSyntax FQF(ITypeSymbol type)
        => ParseTypeName(type.ToDisplayString(
            AsyncLambdaCaptureAnalysis.GlobalFullyQualifiedFormat));

    internal static TMember MarkSynthetic<TMember>(TMember member)
        where TMember : MemberDeclarationSyntax
        => (TMember)member.WithAdditionalAnnotations(Synthetic);

    internal static StatementSyntax MarkSynthetic(StatementSyntax statement)
        => statement.WithAdditionalAnnotations(Synthetic);

    /// <summary>在成员（方法）前发 #pragma warning disable、成员后 restore（CS1998 抑制）。</summary>
    internal static TMember WithAsyncAwaitPragmas<TMember>(TMember member, string code)
        where TMember : MemberDeclarationSyntax
    {
        var disable = Trivia(PragmaWarningDirectiveTrivia(
            Token(SyntaxKind.DisableKeyword),
            SingletonSeparatedList<ExpressionSyntax>(IdentifierName(code)),
            isActive: true));
        var restore = Trivia(PragmaWarningDirectiveTrivia(
            Token(SyntaxKind.RestoreKeyword),
            SingletonSeparatedList<ExpressionSyntax>(IdentifierName(code)),
            isActive: true));

        member = member.WithLeadingTrivia(
            member.GetLeadingTrivia().Add(disable).Add(EndOfLine("\r\n")));
        var lastToken = member.GetLastToken();
        member = member.ReplaceToken(
            lastToken,
            lastToken.WithTrailingTrivia(lastToken.TrailingTrivia
                .Add(EndOfLine("\r\n")).Add(restore).Add(EndOfLine("\r\n"))));
        return member;
    }

    // ── 单方法降级 ──

    private sealed class MethodLowering
    {
        private readonly AsyncIteratorLowering _owner;
        private readonly SemanticModel _model;
        private readonly INamedTypeSymbol? _iDisposable;

        public AsyncIteratorPlan Plan { get; }
        private MethodDeclarationSyntax Method => Plan.Node;
        private int N => Plan.Index;

        // 活性提升
        private readonly HashSet<IParameterSymbol> _usedParameters = new(SymbolEqualityComparer.Default);
        private readonly HashSet<ILocalSymbol> _usedLocals = new(SymbolEqualityComparer.Default);
        private readonly Dictionary<ILocalSymbol, string> _localFields =
            new(SymbolEqualityComparer.Default);
        private bool _usedThis;

        // 区域 / yield 状态
        private readonly List<RegionInfo> _regions = new();
        private readonly Dictionary<SyntaxNode, RegionInfo> _regionByNode = new();
        private readonly Dictionary<YieldStatementSyntax, int> _yieldState = new();
        private readonly Dictionary<int, List<RegionInfo>> _chains = new();
        private readonly List<SyntaxNode> _intact = new();

        // 扁平化游标
        private readonly List<StatementSyntax> _rootStatements = new();
        private readonly Stack<RegionInfo> _activeRegions = new();
        private readonly Stack<(string? ContinueLabel, string BreakLabel)> _breakTargets = new();
        private int _chunkId;
        private bool _endReferenced;
        private readonly HoistRewriter _hoist;
        private readonly HashSet<string> _fieldNames = new(StringComparer.Ordinal);
        private readonly List<(string Name, ITypeSymbol Type)> _switchTemps = new();

        // 产物
        public MethodDeclarationSyntax FactoryMethod { get; }
        public ClassDeclarationSyntax EnumeratorClass { get; }
        public ClassDeclarationSyntax? WrapperClass { get; }
        public int FlagRegionCount { get; }

        public MethodLowering(
            AsyncIteratorLowering owner,
            AsyncIteratorPlan plan,
            SemanticModel model,
            INamedTypeSymbol? iDisposable)
        {
            _owner = owner;
            Plan = plan;
            _model = model;
            _iDisposable = iDisposable;
            _hoist = new HoistRewriter(this, model);

            var body = Method.Body!;
            _intact = AsyncIteratorPlanner.CollectIntactBoundaries(body);

            SeedFieldNames();
            AnalyzeLiveness(body);
            BuildLocalFieldNames(body);
            DetectRegions(body);
            AssignYieldStates(body);
            BuildChains();

            FlattenBody(body);

            EnumeratorClass = BuildEnumeratorClass();
            WrapperClass = Plan.IsEnumerable ? BuildWrapperClass() : null;
            FactoryMethod = BuildFactoryMethod();
            FlagRegionCount = _regions.Count(r => r.HasFlag);
        }

        // ── 命名 / 类型辅助 ──

        private string StateField => AsyncStreamNaming.GetIteratorStateField(N);
        private string CurrentField => AsyncStreamNaming.GetIteratorCurrentField(N);
        private string ThisField => AsyncStreamNaming.GetIteratorThisField(N);
        private string CtField => AsyncStreamNaming.GetIteratorCtField(N);
        private string CtParameterName => AsyncStreamNaming.GetIteratorCtParameter(N);
        private string EndLabel => AsyncStreamNaming.GetIteratorEndLabel(N);

        private TypeSyntax ElementTypeSyntax => FQF(Plan.ElementType);

        private static readonly string AsyncEnumeratorInterface =
            "global::System.Collections.Generic.IAsyncEnumerator";
        private static readonly string AsyncEnumerableInterface =
            "global::System.Collections.Generic.IAsyncEnumerable";
        // IAsyncEnumerator<T> already implies IAsyncDisposable, but listing it
        // explicitly is legal C# and keeps the Java-half bridge contract
        // (AsyncDisposable implements surface) visible to the converter.
        private static readonly string AsyncDisposableInterface =
            "global::System.IAsyncDisposable";
        private static readonly string ValueTaskBoolType =
            "global::System.Threading.Tasks.ValueTask<bool>";
        private static readonly string ValueTaskType =
            "global::System.Threading.Tasks.ValueTask";
        private static readonly string CancellationTokenType =
            "global::System.Threading.CancellationToken";
        private static readonly string IDisposableType = "global::System.IDisposable";

        private TypeSyntax GenericInterface(string interfaceName)
            => ParseTypeName($"{interfaceName}<{Plan.ElementType.ToDisplayString(
                AsyncLambdaCaptureAnalysis.GlobalFullyQualifiedFormat)}>");

        private TypeArgumentListSyntax? MethodTypeArguments =>
            Method.TypeParameterList == null
                ? null
                : TypeArgumentList(SeparatedList(
                    Method.TypeParameterList.Parameters
                        .Select(p => (TypeSyntax)IdentifierName(p.Identifier.ValueText))));

        private void SeedFieldNames()
        {
            _fieldNames.Add(StateField);
            _fieldNames.Add(CurrentField);
            _fieldNames.Add(ThisField);
            _fieldNames.Add(CtField);
            foreach (var p in Plan.Parameters)
                _fieldNames.Add(p.FieldName);
        }

        private bool IsCtParameter(IParameterSymbol symbol)
            => Plan.CancellationTokenParameter != null
               && SymbolEqualityComparer.Default.Equals(symbol, Plan.CancellationTokenParameter);

        // ── 活性分析 ──

        private void AnalyzeLiveness(BlockSyntax body)
        {
            var parameterSymbols = new HashSet<IParameterSymbol>(
                Plan.Parameters.Select(p => p.Symbol), SymbolEqualityComparer.Default);

            foreach (var identifier in body.DescendantNodes().OfType<IdentifierNameSyntax>())
            {
                if (IsNameofArgument(identifier))
                    continue;
                var symbol = _model.GetSymbolInfo(identifier).Symbol;
                switch (symbol)
                {
                    case IParameterSymbol parameter when parameterSymbols.Contains(parameter):
                        _usedParameters.Add(parameter);
                        break;
                    case ILocalSymbol local:
                        _usedLocals.Add(local);
                        break;
                }
            }

            // 隐式 this：裸标识符绑定到宿主（含基类继承链）的实例字段/属性/事件/
            // 方法（`yield return Seed;` 不产生 ThisExpressionSyntax，必须经符号判定）。
            if (!_usedThis && !Plan.Symbol.IsStatic)
            {
                foreach (var identifier in body.DescendantNodes()
                             .OfType<IdentifierNameSyntax>())
                {
                    if (IsNameofArgument(identifier))
                        continue;
                    var referenced = _model.GetSymbolInfo(identifier).Symbol;
                    if (referenced != null && !referenced.IsStatic && IsHostMember(referenced))
                    {
                        _usedThis = true;
                        break;
                    }
                }
            }

            if (body.DescendantNodes().Any(n => n is ThisExpressionSyntax))
                _usedThis = true;
        }

        /// <summary>
        /// 符号是否为宿主类型或其任一基类声明的实例成员（隐式 this 访问的保守识别）。
        /// </summary>
        private bool IsHostMember(ISymbol symbol)
        {
            if (symbol is not (IFieldSymbol or IPropertySymbol or IEventSymbol or IMethodSymbol))
                return false;
            var host = Plan.ContainingType;
            for (var current = host; current != null; current = current.BaseType)
            {
                if (SymbolEqualityComparer.Default.Equals(symbol.ContainingType, current))
                    return true;
            }
            return false;
        }

        private static bool IsNameofArgument(IdentifierNameSyntax identifier)
        {
            return identifier.Parent is ArgumentSyntax
            {
                Parent: ArgumentListSyntax { Parent: InvocationExpressionSyntax invocation }
            } && invocation.Expression is IdentifierNameSyntax { Identifier.Text: "nameof" };
        }

        private void BuildLocalFieldNames(BlockSyntax body)
        {
            var candidates = AsyncIteratorPlanner
                .EnumerateHoistCandidates(Method, _model)
                .ToHashSet(SymbolEqualityComparer.Default);

            foreach (var local in _usedLocals.Where(candidates.Contains))
            {
                var field = AsyncNormalizeNaming.ResolveUniqueName(
                    AsyncStreamNaming.GetIteratorLocalField(N, local.Name), _fieldNames);
                _localFields[local] = field;
            }
        }

        public string? ParameterField(IParameterSymbol symbol)
        {
            if (!_usedParameters.Contains(symbol))
                return null;
            return IsCtParameter(symbol)
                ? CtField
                : Plan.Parameters.First(p => SymbolEqualityComparer.Default.Equals(p.Symbol, symbol))
                    .FieldName;
        }

        public string? LocalField(ILocalSymbol symbol)
            => _localFields.TryGetValue(symbol, out var field) ? field : null;

        // ── 区域检测 / yield 编号 / 链式重入 ──

        private enum RegionKind { Try, Using, Foreach }

        private sealed record RegionInfo(
            StatementSyntax Node,
            int Id,
            RegionKind Kind,
            bool HasFlag,
            string ReEntryLabel,
            string? RunFlagField,
            IReadOnlyList<(string Name, ITypeSymbol Type)> Resources,
            string? EnumeratorField,
            ITypeSymbol? EnumeratorType,
            HashSet<int> YieldStates);

        private void DetectRegions(BlockSyntax body)
        {
            var id = 0;
            foreach (var node in body.DescendantNodes(AsyncIteratorPlanner.OutsideNestedFunctions))
            {
                if (AsyncIteratorPlanner.IsInsideAny(node, _intact))
                    continue;

                var isRegion = node switch
                {
                    TryStatementSyntax => true,
                    UsingStatementSyntax u when !u.AwaitKeyword.IsKind(SyntaxKind.AwaitKeyword) => true,
                    ForEachStatementSyntax f when !f.AwaitKeyword.IsKind(SyntaxKind.AwaitKeyword) => true,
                    _ => false,
                };
                if (!isRegion)
                    continue;

                var hasYield = AsyncIteratorPlanner.ContainsYield(node);
                var hasReturn = AsyncIteratorPlanner.ContainsReturn(node);
                if (!hasYield && !hasReturn)
                    continue;

                var regionId = id++;
                var hasFlag = hasYield;
                IReadOnlyList<(string Name, ITypeSymbol Type)> resources = Array.Empty<(string, ITypeSymbol)>();
                string? enumeratorField = null;
                ITypeSymbol? enumeratorType = null;

                switch (node)
                {
                    case UsingStatementSyntax u:
                        resources = CollectUsingResources(u, regionId);
                        break;
                    case ForEachStatementSyntax f:
                        enumeratorField = AsyncStreamNaming.GetIteratorEnumeratorField(N, regionId);
                        _fieldNames.Add(enumeratorField);
                        var info = _model.GetForEachStatementInfo(f);
                        enumeratorType = info.GetEnumeratorMethod?.ReturnType;
                        break;
                }

                var runFlag = hasFlag
                    ? AsyncStreamNaming.GetIteratorRunFlag(N, regionId)
                    : null;
                if (runFlag != null)
                    _fieldNames.Add(runFlag);

                var region = new RegionInfo(
                    (StatementSyntax)node,
                    regionId,
                    node switch
                    {
                        TryStatementSyntax => RegionKind.Try,
                        UsingStatementSyntax => RegionKind.Using,
                        _ => RegionKind.Foreach,
                    },
                    hasFlag,
                    AsyncStreamNaming.GetIteratorChunkLabel(N, regionId),
                    runFlag,
                    resources,
                    enumeratorField,
                    enumeratorType,
                    new HashSet<int>());

                _regions.Add(region);
                _regionByNode[node] = region;
            }
        }

        private IReadOnlyList<(string Name, ITypeSymbol Type)> CollectUsingResources(
            UsingStatementSyntax u, int regionId)
        {
            var result = new List<(string, ITypeSymbol)>();
            var resourceIndex = 0;

            if (u.Expression != null)
            {
                var type = _model.GetTypeInfo(u.Expression).Type
                    ?? _model.GetTypeInfo(u.Expression).ConvertedType;
                var name = AsyncStreamNaming.GetIteratorResourceField(N, regionId);
                _fieldNames.Add(name);
                if (type != null)
                    result.Add((name, type));
                resourceIndex++;
            }

            if (u.Declaration != null)
            {
                foreach (var variable in u.Declaration.Variables)
                {
                    var name = resourceIndex == 0
                        ? AsyncStreamNaming.GetIteratorResourceField(N, regionId)
                        : AsyncStreamNaming.GetIteratorResourceField(N, regionId) + "_" + resourceIndex;
                    _fieldNames.Add(name);
                    if (_model.GetDeclaredSymbol(variable) is ILocalSymbol local)
                        result.Add((name, local.Type));
                    resourceIndex++;
                }
            }

            return result;
        }

        private void AssignYieldStates(BlockSyntax body)
        {
            var state = 0;
            foreach (var yield in body.DescendantNodes(AsyncIteratorPlanner.OutsideNestedFunctions)
                         .OfType<YieldStatementSyntax>()
                         .Where(y => y.IsKind(SyntaxKind.YieldReturnStatement)))
            {
                _yieldState[yield] = ++state;
            }
        }

        private void BuildChains()
        {
            foreach (var (yield, state) in _yieldState)
            {
                var chain = new List<RegionInfo>();
                for (var node = yield.Parent; node != null && node != Method.Body; node = node.Parent)
                {
                    if (_regionByNode.TryGetValue(node, out var region))
                        chain.Add(region);
                }

                chain.Reverse(); // 外层 → 内层
                _chains[state] = chain;
                foreach (var region in chain)
                    region.YieldStates.Add(state);
            }
        }

        private string NewChunkLabel()
            => AsyncStreamNaming.GetIteratorChunkLabel(N, unchecked(++_chunkId + 10000));

        // ── 表达式 / 语句提升 ──

        private ExpressionSyntax Hoist(ExpressionSyntax expression)
            => (ExpressionSyntax)_hoist.Visit(expression)!;

        private StatementSyntax Hoist(StatementSyntax statement)
            => (StatementSyntax)_hoist.Visit(statement)!;

        private static StatementSyntax MarkerLabel(string name)
            => LabeledStatement(Identifier(name), EmptyStatement());

        private static StatementSyntax GotoLabel(string name)
            => GotoStatement(SyntaxKind.GotoStatement, IdentifierName(name));

        private StatementSyntax GotoEnd()
        {
            _endReferenced = true;
            return GotoLabel(EndLabel);
        }

        private ExpressionStatementSyntax Assign(string field, ExpressionSyntax value)
            => ExpressionStatement(AssignmentExpression(
                SyntaxKind.SimpleAssignmentExpression, IdentifierName(field), value));

        private ExpressionStatementSyntax AssignLiteral(string field, int value)
            => ExpressionStatement(AssignmentExpression(
                SyntaxKind.SimpleAssignmentExpression,
                IdentifierName(field),
                LiteralExpression(SyntaxKind.NumericLiteralExpression, Literal(value))));

        private static ExpressionStatementSyntax AssignBool(string field, bool value)
            => ExpressionStatement(AssignmentExpression(
                SyntaxKind.SimpleAssignmentExpression,
                IdentifierName(field),
                LiteralExpression(value
                    ? SyntaxKind.TrueLiteralExpression
                    : SyntaxKind.FalseLiteralExpression)));

        private static ExpressionSyntax Not(ExpressionSyntax condition)
            => PrefixUnaryExpression(SyntaxKind.LogicalNotExpression,
                ParenthesizedExpression(condition));

        private static ExpressionSyntax EqualsField(string field, ExpressionSyntax value)
            // object.Equals 静态派发：避免 object 与值类型常量的 == 触发 CS0252/CS0253 警告。
            => InvocationExpression(MemberAccessExpression(
                SyntaxKind.SimpleMemberAccessExpression,
                ParseExpression("global::System.Object"),
                IdentifierName("Equals")))
                .WithArgumentList(ArgumentList(SeparatedList(new[]
                {
                    Argument(IdentifierName(field)),
                    Argument(value),
                })));

        // ── 扁平化主体 ──

        private void FlattenBody(BlockSyntax body)
        {
            FlattenStmts(body.Statements, _rootStatements);
        }

        private void FlattenStmts(IEnumerable<StatementSyntax> statements, List<StatementSyntax> output)
        {
            foreach (var statement in statements)
                FlattenStmt(statement, output);
        }

        private void FlattenStmt(StatementSyntax node, List<StatementSyntax> output)
        {
            if (AsyncIteratorPlanner.IsInsideAny(node, _intact))
            {
                output.Add(Hoist(node));
                return;
            }

            if (_regionByNode.TryGetValue(node, out var region))
            {
                EmitRegion(region, output);
                return;
            }

            switch (node)
            {
                case BlockSyntax block:
                    FlattenStmts(block.Statements, output);
                    return;

                case LocalDeclarationStatementSyntax local:
                    EmitLocalDeclaration(local, output);
                    return;

                case YieldStatementSyntax { Expression: { } expression } yield
                    when yield.IsKind(SyntaxKind.YieldReturnStatement):
                    EmitYieldReturn(yield, expression, output);
                    return;

                case YieldStatementSyntax when node.IsKind(SyntaxKind.YieldBreakStatement):
                    output.Add(GotoEnd());
                    return;

                case ReturnStatementSyntax:
                    output.Add(GotoEnd());
                    return;

                case BreakStatementSyntax:
                    output.Add(GotoLabel(_breakTargets.Peek().BreakLabel));
                    return;

                case ContinueStatementSyntax:
                    output.Add(GotoLabel(_breakTargets.Peek().ContinueLabel!));
                    return;

                case IfStatementSyntax @if:
                    EmitIf(@if, output);
                    return;

                case WhileStatementSyntax @while:
                    EmitWhile(@while, output);
                    return;

                case DoStatementSyntax @do:
                    EmitDo(@do, output);
                    return;

                case ForStatementSyntax @for:
                    EmitFor(@for, output);
                    return;

                case SwitchStatementSyntax @switch:
                    EmitSwitch(@switch, output);
                    return;

                case ExpressionStatementSyntax expressionStatement:
                    output.Add(expressionStatement.WithExpression(Hoist(expressionStatement.Expression)));
                    return;

                case ThrowStatementSyntax @throw:
                    output.Add(@throw.Expression == null
                        ? @throw
                        : @throw.WithExpression(Hoist(@throw.Expression)));
                    return;

                case EmptyStatementSyntax:
                    output.Add(node);
                    return;

                case CheckedStatementSyntax checkedStatement:
                    output.Add(checkedStatement.WithBlock(
                        (BlockSyntax)FlattenBlockToNode(checkedStatement.Block)));
                    return;

                default:
                    // Planner 闸门保证剩余语句不含 yield；原样保留（仅做提升替换）。
                    if (AsyncIteratorPlanner.ContainsYield(node))
                    {
                        throw new InvalidOperationException(
                            "C38 M4 lowering: unsupported statement shape contains 'yield': "
                            + node.Kind());
                    }
                    output.Add(Hoist(node));
                    return;
            }
        }

        private SyntaxNode FlattenBlockToNode(BlockSyntax block)
        {
            var statements = new List<StatementSyntax>();
            FlattenStmts(block.Statements, statements);
            return block.WithStatements(List(statements));
        }

        private void EmitLocalDeclaration(
            LocalDeclarationStatementSyntax local,
            List<StatementSyntax> output)
        {
            foreach (var variable in local.Declaration.Variables)
            {
                var symbol = _model.GetDeclaredSymbol(variable) as ILocalSymbol;
                var field = symbol != null ? LocalField(symbol) : null;
                if (field != null)
                {
                    if (variable.Initializer is { Value: { } initializer })
                        output.Add(Assign(field, Hoist(initializer)));
                }
                else if (variable.Initializer is { Value: { } initializer })
                {
                    // 未被引用的局部：字段不发射，初始化副作用保留（丢弃赋值）。
                    output.Add(ExpressionStatement(AssignmentExpression(
                        SyntaxKind.SimpleAssignmentExpression,
                        IdentifierName(Token(SyntaxKind.UnderscoreToken)),
                        Hoist(initializer))));
                }
            }
        }

        private void EmitYieldReturn(
            YieldStatementSyntax yield,
            ExpressionSyntax expression,
            List<StatementSyntax> output)
        {
            var state = _yieldState[yield];
            output.Add(Assign(CurrentField, Hoist(expression)));
            output.Add(AssignLiteral(StateField, state));
            foreach (var active in _activeRegions.Where(r => r.HasFlag))
                output.Add(AssignBool(active.RunFlagField!, false));
            output.Add(ReturnStatement(LiteralExpression(SyntaxKind.TrueLiteralExpression)));
            output.Add(MarkerLabel(AsyncStreamNaming.GetResumeLabel(state)));
        }

        private void EmitIf(IfStatementSyntax @if, List<StatementSyntax> output)
        {
            var endLabel = NewChunkLabel();
            if (@if.Statement is BlockSyntax thenBlock)
            {
                if (@if.Else != null)
                {
                    var elseLabel = NewChunkLabel();
                    output.Add(IfStatement(Not(Hoist(@if.Condition)), GotoLabel(elseLabel)));
                    FlattenStmts(thenBlock.Statements, output);
                    output.Add(GotoLabel(endLabel));
                    output.Add(MarkerLabel(elseLabel));
                    if (@if.Else.Statement is BlockSyntax elseBlock)
                        FlattenStmts(elseBlock.Statements, output);
                    else
                        FlattenStmt(@if.Else.Statement, output);
                    output.Add(MarkerLabel(endLabel));
                }
                else
                {
                    output.Add(IfStatement(Not(Hoist(@if.Condition)), GotoLabel(endLabel)));
                    FlattenStmts(thenBlock.Statements, output);
                    output.Add(MarkerLabel(endLabel));
                }
            }
            else
            {
                // 非块 then/else：单语句扁平化（极少；C# 允许 if (…) Stmt;）。
                if (@if.Else != null)
                {
                    var elseLabel = NewChunkLabel();
                    output.Add(IfStatement(Not(Hoist(@if.Condition)), GotoLabel(elseLabel)));
                    FlattenStmt(@if.Statement, output);
                    output.Add(GotoLabel(endLabel));
                    output.Add(MarkerLabel(elseLabel));
                    FlattenStmt(@if.Else.Statement, output);
                    output.Add(MarkerLabel(endLabel));
                }
                else
                {
                    output.Add(IfStatement(Not(Hoist(@if.Condition)), GotoLabel(endLabel)));
                    FlattenStmt(@if.Statement, output);
                    output.Add(MarkerLabel(endLabel));
                }
            }
        }

        private void EmitWhile(WhileStatementSyntax @while, List<StatementSyntax> output)
        {
            var condLabel = NewChunkLabel();
            var endLabel = NewChunkLabel();
            output.Add(MarkerLabel(condLabel));
            output.Add(IfStatement(Not(Hoist(@while.Condition)), GotoLabel(endLabel)));
            _breakTargets.Push((condLabel, endLabel));
            FlattenStmt(@while.Statement, output);
            _breakTargets.Pop();
            output.Add(GotoLabel(condLabel));
            output.Add(MarkerLabel(endLabel));
        }

        private void EmitDo(DoStatementSyntax @do, List<StatementSyntax> output)
        {
            var bodyLabel = NewChunkLabel();
            var condLabel = NewChunkLabel();
            var endLabel = NewChunkLabel();
            output.Add(MarkerLabel(bodyLabel));
            _breakTargets.Push((condLabel, endLabel));
            FlattenStmt(@do.Statement, output);
            _breakTargets.Pop();
            output.Add(MarkerLabel(condLabel));
            output.Add(IfStatement(Hoist(@do.Condition), GotoLabel(bodyLabel)));
            output.Add(MarkerLabel(endLabel));
        }

        private void EmitFor(ForStatementSyntax @for, List<StatementSyntax> output)
        {
            var condLabel = NewChunkLabel();
            var continueLabel = NewChunkLabel();
            var endLabel = NewChunkLabel();

            if (@for.Declaration != null)
            {
                foreach (var variable in @for.Declaration.Variables)
                {
                    var symbol = _model.GetDeclaredSymbol(variable) as ILocalSymbol;
                    var field = symbol != null ? LocalField(symbol) : null;
                    if (field != null)
                    {
                        if (variable.Initializer is { Value: { } initializer })
                            output.Add(Assign(field, Hoist(initializer)));
                    }
                    else if (variable.Initializer is { Value: { } initializer })
                    {
                        output.Add(ExpressionStatement(AssignmentExpression(
                            SyntaxKind.SimpleAssignmentExpression,
                            IdentifierName(Token(SyntaxKind.UnderscoreToken)),
                            Hoist(initializer))));
                    }
                }
            }

            foreach (var initializer in @for.Initializers)
                output.Add(ExpressionStatement(Hoist(initializer)));

            output.Add(MarkerLabel(condLabel));
            if (@for.Condition != null)
                output.Add(IfStatement(Not(Hoist(@for.Condition)), GotoLabel(endLabel)));

            _breakTargets.Push((continueLabel, endLabel));
            FlattenStmt(@for.Statement, output);
            _breakTargets.Pop();

            output.Add(MarkerLabel(continueLabel));
            foreach (var incrementor in @for.Incrementors)
                output.Add(ExpressionStatement(Hoist(incrementor)));
            output.Add(GotoLabel(condLabel));
            output.Add(MarkerLabel(endLabel));
        }

        private void EmitSwitch(SwitchStatementSyntax @switch, List<StatementSyntax> output)
        {
            var endLabel = NewChunkLabel();

            var hasConstLabel = @switch.Sections
                .SelectMany(s => s.Labels)
                .Any(l => l is CaseSwitchLabelSyntax);
            string? switchField = null;

            if (hasConstLabel)
            {
                var switchType = _model.GetTypeInfo(@switch.Expression).Type
                    ?? _model.GetTypeInfo(@switch.Expression).ConvertedType
                    ?? _model.Compilation.GetSpecialType(SpecialType.System_Object);
                switchField = AddSwitchTemp(switchType);
                output.Add(Assign(switchField, Hoist(@switch.Expression)));
            }

            var sectionLabels = new List<string>();
            string? defaultTarget = null;
            foreach (var section in @switch.Sections)
            {
                var sectionLabel = NewChunkLabel();
                sectionLabels.Add(sectionLabel);
                foreach (var label in section.Labels)
                {
                    if (label is DefaultSwitchLabelSyntax)
                    {
                        defaultTarget = sectionLabel;
                    }
                    else if (label is CaseSwitchLabelSyntax caseLabel && switchField != null)
                    {
                        output.Add(IfStatement(
                            EqualsField(switchField, Hoist(caseLabel.Value)),
                            GotoLabel(sectionLabel)));
                    }
                }
            }

            output.Add(GotoLabel(defaultTarget ?? endLabel));

            _breakTargets.Push((null, endLabel));
            for (var i = 0; i < @switch.Sections.Count; i++)
            {
                output.Add(MarkerLabel(sectionLabels[i]));
                FlattenStmts(@switch.Sections[i].Statements, output);
            }
            _breakTargets.Pop();

            output.Add(MarkerLabel(endLabel));
        }

        private string AddSwitchTemp(ITypeSymbol type)
        {
            var name = AsyncNormalizeNaming.ResolveUniqueName(
                AsyncStreamNaming.GetIteratorLocalField(N, "sw" + _switchTemps.Count),
                _fieldNames);
            _switchTemps.Add((name, type));
            return name;
        }

        // ── 区域发射（try-finally / 同步 using / 同步 foreach） ──

        private void EmitRegion(RegionInfo region, List<StatementSyntax> output)
        {
            _activeRegions.Push(region);
            try
            {
                switch (region.Node)
                {
                    case TryStatementSyntax @try:
                        EmitTryRegion(region, @try, output);
                        break;
                    case UsingStatementSyntax u:
                        EmitUsingRegion(region, u, output);
                        break;
                    case ForEachStatementSyntax f:
                        EmitForeachRegion(region, f, output);
                        break;
                }
            }
            finally
            {
                _activeRegions.Pop();
            }
        }

        private void EmitTryRegion(
            RegionInfo region,
            TryStatementSyntax @try,
            List<StatementSyntax> output)
        {
            var bodyStatements = new List<StatementSyntax>();
            if (region.HasFlag)
                bodyStatements.Add(BuildBoundarySwitch(region));
            FlattenStmts(@try.Block.Statements, bodyStatements);

            var tryStatement = TryStatement().WithBlock(Block(bodyStatements));

            if (@try.Catches.Count > 0)
            {
                var catches = @try.Catches.Select(FlattenCatchClause).ToList();
                tryStatement = tryStatement.WithCatches(List(catches));
            }

            if (@try.Finally != null)
            {
                var finallyStatements = @try.Finally.Block.Statements
                    .Select(Hoist).Cast<StatementSyntax>().ToList();
                if (region.HasFlag)
                {
                    finallyStatements = new List<StatementSyntax>
                    {
                        IfStatement(
                            IdentifierName(region.RunFlagField!),
                            Block(finallyStatements)),
                    };
                }
                tryStatement = tryStatement.WithFinally(
                    FinallyClause(Block(finallyStatements)));
            }

            if (region.HasFlag)
            {
                output.Add(MarkerLabel(region.ReEntryLabel));
                output.Add(AssignBool(region.RunFlagField!, true));
            }
            output.Add(tryStatement);
        }

        private CatchClauseSyntax FlattenCatchClause(CatchClauseSyntax catchClause)
        {
            var statements = new List<StatementSyntax>();
            FlattenStmts(catchClause.Block.Statements, statements);
            // catch 内 return 被改写为 goto end（跳出 catch 合法，无 yield）。
            return catchClause.WithBlock(Block(statements));
        }

        private void EmitUsingRegion(
            RegionInfo region,
            UsingStatementSyntax u,
            List<StatementSyntax> output)
        {
            var acquisitions = new List<StatementSyntax>();
            var resourceIndex = 0;
            if (u.Expression != null && region.Resources.Count > 0)
            {
                acquisitions.Add(Assign(
                    region.Resources[0].Name, Hoist(u.Expression)));
                resourceIndex = 1;
            }
            if (u.Declaration != null)
            {
                foreach (var variable in u.Declaration.Variables)
                {
                    if (variable.Initializer is { Value: { } initializer }
                        && resourceIndex < region.Resources.Count)
                    {
                        acquisitions.Add(Assign(
                            region.Resources[resourceIndex].Name, Hoist(initializer)));
                    }
                    resourceIndex++;
                }
            }

            var bodyStatements = new List<StatementSyntax>();
            if (region.HasFlag)
                bodyStatements.Add(BuildBoundarySwitch(region));
            if (u.Statement is BlockSyntax usingBlock)
                FlattenStmts(usingBlock.Statements, bodyStatements);
            else
                FlattenStmt(u.Statement, bodyStatements);

            var disposeStatements = BuildDisposeCalls(region.Resources);
            if (region.HasFlag)
            {
                disposeStatements = new List<StatementSyntax>
                {
                    IfStatement(
                        IdentifierName(region.RunFlagField!),
                        Block(disposeStatements)),
                };
            }

            var tryStatement = TryStatement()
                .WithBlock(Block(bodyStatements))
                .WithFinally(FinallyClause(Block(disposeStatements)));

            output.AddRange(acquisitions);
            if (region.HasFlag)
            {
                output.Add(MarkerLabel(region.ReEntryLabel));
                output.Add(AssignBool(region.RunFlagField!, true));
            }
            output.Add(tryStatement);
        }

        private void EmitForeachRegion(
            RegionInfo region,
            ForEachStatementSyntax f,
            List<StatementSyntax> output)
        {
            var enumeratorField = region.EnumeratorField!;
            var acquisition = ExpressionStatement(AssignmentExpression(
                SyntaxKind.SimpleAssignmentExpression,
                IdentifierName(enumeratorField),
                InvocationExpression(MemberAccessExpression(
                    SyntaxKind.SimpleMemberAccessExpression,
                    Hoist(f.Expression),
                    IdentifierName("GetEnumerator")))));
            output.Add(acquisition);

            var condLabel = NewChunkLabel();
            var endLabel = NewChunkLabel();

            var bodyStatements = new List<StatementSyntax>();
            if (region.HasFlag)
                bodyStatements.Add(BuildBoundarySwitch(region));
            bodyStatements.Add(MarkerLabel(condLabel));
            bodyStatements.Add(IfStatement(
                Not(InvocationExpression(MemberAccessExpression(
                    SyntaxKind.SimpleMemberAccessExpression,
                    IdentifierName(enumeratorField),
                    IdentifierName("MoveNext")))),
                GotoLabel(endLabel)));

            var loopVariable = _model.GetDeclaredSymbol(f) as ILocalSymbol;
            var loopField = loopVariable != null ? LocalField(loopVariable) : null;
            var currentAccess = (ExpressionSyntax)MemberAccessExpression(
                SyntaxKind.SimpleMemberAccessExpression,
                IdentifierName(enumeratorField),
                IdentifierName("Current"));

            // foreach 元素转换（数组/非泛型枚举器 Current 为 object，需镜像 C# 编译器
            // 插入的显式转换，如 (int)__fe.Current）。
            var currentPropertyType = _model.GetForEachStatementInfo(f).CurrentProperty?.Type;
            if (loopVariable != null && currentPropertyType != null)
            {
                var elementConversion = _model.Compilation.ClassifyConversion(
                    currentPropertyType, loopVariable.Type);
                if (!elementConversion.IsImplicit)
                    currentAccess = CastExpression(FQF(loopVariable.Type), currentAccess);
            }

            bodyStatements.Add(loopField != null
                ? Assign(loopField, currentAccess)
                : ExpressionStatement(AssignmentExpression(
                    SyntaxKind.SimpleAssignmentExpression,
                    IdentifierName(Token(SyntaxKind.UnderscoreToken)),
                    currentAccess)));

            _breakTargets.Push((condLabel, endLabel));
            FlattenStmt(f.Statement, bodyStatements);
            _breakTargets.Pop();

            bodyStatements.Add(GotoLabel(condLabel));
            bodyStatements.Add(MarkerLabel(endLabel));

            var disposeStatements = region.EnumeratorType != null
                ? BuildDisposeCalls(new[] { (enumeratorField, region.EnumeratorType) })
                : new List<StatementSyntax>();
            if (region.HasFlag)
            {
                disposeStatements = new List<StatementSyntax>
                {
                    IfStatement(
                        IdentifierName(region.RunFlagField!),
                        Block(disposeStatements)),
                };
            }

            var tryStatement = TryStatement()
                .WithBlock(Block(bodyStatements))
                .WithFinally(FinallyClause(Block(disposeStatements)));

            if (region.HasFlag)
            {
                output.Add(MarkerLabel(region.ReEntryLabel));
                output.Add(AssignBool(region.RunFlagField!, true));
            }
            output.Add(tryStatement);
        }

        /// <summary>
        /// 区域 try 体首的边界 switch：仅发射本区域子树上的 yield 状态，case goto
        /// 直接内层区域的重入标签（链式分发）或本层可见的 resume 标签。
        /// </summary>
        private SwitchStatementSyntax BuildBoundarySwitch(RegionInfo region)
        {
            var targets = new List<(string Target, int State)>();
            foreach (var state in OrderStates(region.YieldStates))
            {
                var chain = _chains[state];
                var index = chain.FindIndex(r => r.Id == region.Id);
                var target = index == chain.Count - 1
                    ? AsyncStreamNaming.GetResumeLabel(state)
                    : chain[index + 1].ReEntryLabel;
                targets.Add((target, state));
            }
            return BuildStateSwitch(targets);
        }

        private SwitchStatementSyntax BuildTopSwitch()
        {
            var targets = new List<(string Target, int State)>();
            for (var state = 1; state <= Plan.YieldReturnCount; state++)
            {
                var chain = _chains[state];
                var target = chain.Count == 0
                    ? AsyncStreamNaming.GetResumeLabel(state)
                    : chain[0].ReEntryLabel;
                targets.Add((target, state));
            }
            return BuildStateSwitch(targets);
        }

        private static IEnumerable<int> OrderStates(IEnumerable<int> states)
            => states.OrderBy(s => s);

        private SwitchStatementSyntax BuildStateSwitch(List<(string Target, int State)> targets)
        {
            var sections = new List<SwitchSectionSyntax>();
            foreach (var group in targets.GroupBy(t => t.Target))
            {
                var labels = group.Select(t => (SwitchLabelSyntax)CaseSwitchLabel(
                    LiteralExpression(SyntaxKind.NumericLiteralExpression, Literal(t.State)))).ToList();
                var section = SwitchSection(
                    List(labels),
                    SingletonList<StatementSyntax>(GotoLabel(group.Key)));
                sections.Add(section);
            }
            return SwitchStatement(IdentifierName(StateField), List(sections));
        }

        // ── Dispose 语句（同步 using 资源 / 同步 foreach 枚举器） ──

        private List<StatementSyntax> BuildDisposeCalls(
            IReadOnlyList<(string Name, ITypeSymbol Type)> resources)
        {
            var result = new List<StatementSyntax>();
            foreach (var (name, type) in resources.Reverse())
            {
                var conversion = _iDisposable != null
                    ? _model.Compilation.ClassifyConversion(type, _iDisposable)
                    : default;
                if (conversion.IsImplicit)
                {
                    // 隐式可转（含实现接口的枚举器/资源）：((IDisposable)x).Dispose()，
                    // 引用类型保留 null 守卫。
                    var disposeCall = InvocationExpression(MemberAccessExpression(
                        SyntaxKind.SimpleMemberAccessExpression,
                        ParenthesizedExpression(
                            CastExpression(ParseTypeName(IDisposableType), IdentifierName(name))),
                        IdentifierName("Dispose")));

                    if (type.IsReferenceType)
                    {
                        result.Add(IfStatement(
                            BinaryExpression(SyntaxKind.NotEqualsExpression,
                                IdentifierName(name), LiteralExpression(SyntaxKind.NullLiteralExpression)),
                            ExpressionStatement(disposeCall)));
                    }
                    else
                    {
                        result.Add(ExpressionStatement(disposeCall));
                    }
                }
                else
                {
                    // 非隐式（典型：非泛型 System.Collections.IEnumerator 字段）：镜像
                    // C# foreach 的释放语义——is 探测后再显式转换调用，避免对未实现
                    // IDisposable 的具体枚举器抛 InvalidCastException。
                    var disposableCheck = BinaryExpression(SyntaxKind.IsExpression,
                        IdentifierName(name), ParseTypeName(IDisposableType));
                    var disposeCall = InvocationExpression(MemberAccessExpression(
                        SyntaxKind.SimpleMemberAccessExpression,
                        ParenthesizedExpression(
                            CastExpression(ParseTypeName(IDisposableType), IdentifierName(name))),
                        IdentifierName("Dispose")));
                    result.Add(IfStatement(disposableCheck, ExpressionStatement(disposeCall)));
                }
            }
            return result;
        }

        // ── 枚举器类 ──

        private ClassDeclarationSyntax BuildEnumeratorClass()
        {
            var members = new List<MemberDeclarationSyntax>();
            members.AddRange(BuildEnumeratorFields());
            members.Add(BuildEnumeratorConstructor());
            members.Add(BuildCurrentProperty());
            members.Add(BuildMoveNextMethod());
            members.Add(BuildDisposeAsyncMethod());

            var declaration = ClassDeclaration(Identifier(Plan.EnumeratorClassName))
                .WithModifiers(TokenList(
                    Token(SyntaxKind.PrivateKeyword),
                    Token(SyntaxKind.SealedKeyword)))
                .WithBaseList(BaseList(SeparatedList(new BaseTypeSyntax[]
                {
                    SimpleBaseType(GenericInterface(AsyncEnumeratorInterface)),
                    SimpleBaseType(ParseTypeName(AsyncDisposableInterface)),
                })))
                .WithMembers(List(members));

            if (Method.TypeParameterList != null)
            {
                declaration = declaration
                    .WithTypeParameterList(Method.TypeParameterList)
                    .WithConstraintClauses(Method.ConstraintClauses);
            }

            return MarkSynthetic(declaration);
        }

        private IEnumerable<MemberDeclarationSyntax> BuildEnumeratorFields()
        {
            yield return FieldDeclaration(VariableDeclaration(PredefinedType(Token(SyntaxKind.IntKeyword)))
                    .WithVariables(SingletonSeparatedList(
                        VariableDeclarator(StateField)
                            .WithInitializer(EqualsValueClause(
                                LiteralExpression(SyntaxKind.NumericLiteralExpression, Literal(0)))))))
                .WithModifiers(TokenList(Token(SyntaxKind.PrivateKeyword)));

            yield return FieldDeclaration(VariableDeclaration(ElementTypeSyntax)
                    .WithVariables(SingletonSeparatedList(
                        VariableDeclarator(CurrentField)
                            .WithInitializer(EqualsValueClause(
                                PostfixUnaryExpression(SyntaxKind.SuppressNullableWarningExpression,
                                    LiteralExpression(SyntaxKind.DefaultLiteralExpression)))))))
                .WithModifiers(TokenList(Token(SyntaxKind.PrivateKeyword)));

            if (_usedThis && !Plan.Symbol.IsStatic)
            {
                yield return FieldDeclaration(VariableDeclaration(FQF(Plan.ContainingType))
                        .WithVariables(SingletonSeparatedList(VariableDeclarator(ThisField))))
                    .WithModifiers(TokenList(
                        Token(SyntaxKind.PrivateKeyword), Token(SyntaxKind.ReadOnlyKeyword)));
            }

            foreach (var parameter in Plan.Parameters)
            {
                if (!_usedParameters.Contains(parameter.Symbol))
                    continue;
                if (IsCtParameter(parameter.Symbol))
                    continue; // CT 单独字段。
                yield return FieldDeclaration(VariableDeclaration(FQF(parameter.Symbol.Type))
                        .WithVariables(SingletonSeparatedList(VariableDeclarator(parameter.FieldName))))
                    .WithModifiers(TokenList(
                        Token(SyntaxKind.PrivateKeyword), Token(SyntaxKind.ReadOnlyKeyword)));
            }

            if (Plan.CancellationTokenParameter != null
                && _usedParameters.Contains(Plan.CancellationTokenParameter))
            {
                yield return FieldDeclaration(VariableDeclaration(ParseTypeName(CancellationTokenType))
                        .WithVariables(SingletonSeparatedList(VariableDeclarator(CtField))))
                    .WithModifiers(TokenList(
                        Token(SyntaxKind.PrivateKeyword), Token(SyntaxKind.ReadOnlyKeyword)));
            }

            var hoistedMutableFields = _localFields
                .Select(kv => (Name: kv.Value, Type: (ITypeSymbol)kv.Key.Type))
                .Concat(_switchTemps.Select(t => (Name: t.Name, Type: t.Type)))
                .ToList();
            foreach (var (name, type) in hoistedMutableFields)
            {
                yield return DefaultableMutableField(name, type);
            }

            foreach (var region in _regions.Where(r => r.HasFlag))
            {
                yield return FieldDeclaration(VariableDeclaration(PredefinedType(Token(SyntaxKind.BoolKeyword)))
                        .WithVariables(SingletonSeparatedList(
                            VariableDeclarator(region.RunFlagField!)
                                .WithInitializer(EqualsValueClause(
                                    LiteralExpression(SyntaxKind.FalseLiteralExpression))))))
                    .WithModifiers(TokenList(Token(SyntaxKind.PrivateKeyword)));
            }

            foreach (var (name, type) in _regions.SelectMany(r => r.Resources))
                yield return DefaultableMutableField(name, type);

            foreach (var region in _regions.Where(r => r.EnumeratorField != null))
                yield return DefaultableMutableField(region.EnumeratorField!, region.EnumeratorType!);
        }

        private static FieldDeclarationSyntax DefaultableMutableField(string name, ITypeSymbol type)
            => FieldDeclaration(VariableDeclaration(FQF(type))
                    .WithVariables(SingletonSeparatedList(
                        VariableDeclarator(name).WithInitializer(EqualsValueClause(
                            PostfixUnaryExpression(SyntaxKind.SuppressNullableWarningExpression,
                                LiteralExpression(SyntaxKind.DefaultLiteralExpression)))))))
                .WithModifiers(TokenList(Token(SyntaxKind.PrivateKeyword)));

        /// <summary>ctor 形参形状：this、非 CT 已用形参（源序）、CT；字段同名形参。</summary>
        private List<(string Field, TypeSyntax Type)> CtorShape()
        {
            var shape = new List<(string, TypeSyntax)>();
            if (_usedThis && !Plan.Symbol.IsStatic)
                shape.Add((ThisField, FQF(Plan.ContainingType)));
            foreach (var parameter in Plan.Parameters)
            {
                if (!_usedParameters.Contains(parameter.Symbol) || IsCtParameter(parameter.Symbol))
                    continue;
                shape.Add((parameter.FieldName, FQF(parameter.Symbol.Type)));
            }
            if (Plan.CancellationTokenParameter != null
                && _usedParameters.Contains(Plan.CancellationTokenParameter))
            {
                shape.Add((CtField, ParseTypeName(CancellationTokenType)));
            }
            return shape;
        }

        private ConstructorDeclarationSyntax BuildEnumeratorConstructor()
        {
            var shape = CtorShape();
            var parameters = shape.Select(s =>
                Parameter(Identifier(s.Field)).WithType(s.Type)).ToList();
            var assignments = shape.Select(s =>
                ExpressionStatement(AssignmentExpression(
                    SyntaxKind.SimpleAssignmentExpression,
                    MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression,
                        ThisExpression(), IdentifierName(s.Field)),
                    IdentifierName(s.Field)))).Cast<StatementSyntax>().ToList();

            return ConstructorDeclaration(Identifier(Plan.EnumeratorClassName))
                .WithModifiers(TokenList(Token(SyntaxKind.PublicKeyword)))
                .WithParameterList(ParameterList(SeparatedList(parameters)))
                .WithBody(Block(assignments));
        }

        private PropertyDeclarationSyntax BuildCurrentProperty()
            => PropertyDeclaration(ElementTypeSyntax, Identifier("Current"))
                .WithModifiers(TokenList(Token(SyntaxKind.PublicKeyword)))
                .WithExpressionBody(ArrowExpressionClause(IdentifierName(CurrentField)))
                .WithSemicolonToken(Token(SyntaxKind.SemicolonToken));

        private MethodDeclarationSyntax BuildMoveNextMethod()
        {
            var statements = new List<StatementSyntax>
            {
                // state==-1（已终结）后再次调用：直接 false，不重跑方法体。
                IfStatement(
                    BinaryExpression(SyntaxKind.EqualsExpression,
                        IdentifierName(StateField),
                        PrefixUnaryExpression(SyntaxKind.UnaryMinusExpression,
                            LiteralExpression(SyntaxKind.NumericLiteralExpression, Literal(1)))),
                    ReturnStatement(LiteralExpression(SyntaxKind.FalseLiteralExpression))),
                BuildTopSwitch(),
            };
            statements.AddRange(_rootStatements);
            if (_endReferenced)
                statements.Add(MarkerLabel(EndLabel));
            statements.Add(AssignLiteral(StateField, -1));
            statements.Add(ReturnStatement(LiteralExpression(SyntaxKind.FalseLiteralExpression)));

            var method = MethodDeclaration(
                    ParseTypeName(ValueTaskBoolType), Identifier("MoveNextAsync"))
                .WithModifiers(TokenList(
                    Token(SyntaxKind.PublicKeyword), Token(SyntaxKind.AsyncKeyword)))
                .WithBody(PruneUnreferencedLabels(Block(statements)));
            return WithAsyncAwaitPragmas(method, "CS1998");
        }

        /// <summary>
        /// 删除没有任何 goto 引用的标记标签（do/for 无 break/continue 等形态会产生
        /// 不可达标签 → CS0164 是警告，正例要求零诊断）。标签全方法唯一，按名收集即可。
        /// </summary>
        private static BlockSyntax PruneUnreferencedLabels(BlockSyntax block)
        {
            var referenced = new HashSet<string>(StringComparer.Ordinal);
            foreach (var gotoStatement in block.DescendantNodes().OfType<GotoStatementSyntax>())
            {
                if (gotoStatement.Expression is IdentifierNameSyntax identifier)
                    referenced.Add(identifier.Identifier.ValueText);
            }
            return (BlockSyntax)new LabelPruner(referenced).Visit(block)!;
        }

        private sealed class LabelPruner : CSharpSyntaxRewriter
        {
            private readonly HashSet<string> _referenced;

            public LabelPruner(HashSet<string> referenced)
                : base(visitIntoStructuredTrivia: false)
                => _referenced = referenced;

            public override SyntaxNode? VisitBlock(BlockSyntax node)
            {
                var visited = (BlockSyntax)base.VisitBlock(node)!;
                var kept = visited.Statements.Where(statement =>
                    statement is not LabeledStatementSyntax
                    {
                        Statement: EmptyStatementSyntax
                    } labeled
                    || _referenced.Contains(labeled.Identifier.ValueText));
                return visited.WithStatements(List(kept));
            }
        }

        private MethodDeclarationSyntax BuildDisposeAsyncMethod()
        {
            var statements = new List<StatementSyntax>();
            // 内层区域先释放（区域号按文档序，嵌套内层 id 更大）。
            foreach (var region in Enumerable.Reverse(_regions).Where(r => r.YieldStates.Count > 0))
            {
                ExpressionSyntax? condition = null;
                foreach (var state in OrderStates(region.YieldStates))
                {
                    var comparison = BinaryExpression(SyntaxKind.EqualsExpression,
                        IdentifierName(StateField),
                        LiteralExpression(SyntaxKind.NumericLiteralExpression, Literal(state)));
                    condition = condition == null
                        ? comparison
                        : BinaryExpression(SyntaxKind.LogicalOrExpression, condition, comparison);
                }

                List<StatementSyntax> actions;
                switch (region.Kind)
                {
                    case RegionKind.Try when region.Node is TryStatementSyntax @try
                        && @try.Finally != null:
                        actions = @try.Finally.Block.Statements
                            .Select(Hoist).Cast<StatementSyntax>().ToList();
                        break;
                    case RegionKind.Using:
                        actions = BuildDisposeCalls(region.Resources);
                        break;
                    case RegionKind.Foreach when region.EnumeratorType != null:
                        actions = BuildDisposeCalls(
                            new[] { (region.EnumeratorField!, region.EnumeratorType) });
                        break;
                    default:
                        actions = new List<StatementSyntax>();
                        break;
                }

                // 无 finally/不可绑资源的区域（Planner 闸门下基本不可达）不发空 if。
                if (actions.Count > 0)
                    statements.Add(IfStatement(condition!, Block(actions)));
            }

            statements.Add(AssignLiteral(StateField, -1));

            var method = MethodDeclaration(
                    ParseTypeName(ValueTaskType), Identifier("DisposeAsync"))
                .WithModifiers(TokenList(
                    Token(SyntaxKind.PublicKeyword), Token(SyntaxKind.AsyncKeyword)))
                .WithBody(Block(statements));
            return WithAsyncAwaitPragmas(method, "CS1998");
        }

        // ── 可枚举包装类 ──

        private ClassDeclarationSyntax BuildWrapperClass()
        {
            var members = new List<MemberDeclarationSyntax>();
            var shape = CtorShape();

            foreach (var (field, type) in shape)
            {
                members.Add(FieldDeclaration(VariableDeclaration(type)
                        .WithVariables(SingletonSeparatedList(VariableDeclarator(field))))
                    .WithModifiers(TokenList(
                        Token(SyntaxKind.PrivateKeyword), Token(SyntaxKind.ReadOnlyKeyword))));
            }

            var assignments = shape.Select(s =>
                ExpressionStatement(AssignmentExpression(
                    SyntaxKind.SimpleAssignmentExpression,
                    MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression,
                        ThisExpression(), IdentifierName(s.Field)),
                    IdentifierName(s.Field)))).Cast<StatementSyntax>().ToList();

            members.Add(ConstructorDeclaration(Identifier(Plan.EnumerableClassName!))
                .WithModifiers(TokenList(Token(SyntaxKind.PublicKeyword)))
                .WithParameterList(ParameterList(SeparatedList(
                    shape.Select(s => Parameter(Identifier(s.Field)).WithType(s.Type)))))
                .WithBody(Block(assignments)));

            members.Add(BuildGetAsyncEnumerator(shape));

            var declaration = ClassDeclaration(Identifier(Plan.EnumerableClassName!))
                .WithModifiers(TokenList(
                    Token(SyntaxKind.PrivateKeyword), Token(SyntaxKind.SealedKeyword)))
                .WithBaseList(BaseList(SingletonSeparatedList<BaseTypeSyntax>(
                    SimpleBaseType(GenericInterface(AsyncEnumerableInterface)))))
                .WithMembers(List(members));

            if (Method.TypeParameterList != null)
            {
                declaration = declaration
                    .WithTypeParameterList(Method.TypeParameterList)
                    .WithConstraintClauses(Method.ConstraintClauses);
            }

            return MarkSynthetic(declaration);
        }

        private MethodDeclarationSyntax BuildGetAsyncEnumerator(List<(string Field, TypeSyntax Type)> shape)
        {
            var arguments = new List<ArgumentSyntax>();
            var ctUsed = Plan.CancellationTokenParameter != null
                && _usedParameters.Contains(Plan.CancellationTokenParameter);

            foreach (var (field, _) in shape)
            {
                ExpressionSyntax value = IdentifierName(field);
                if (ctUsed && field == CtField)
                {
                    value = ConditionalExpression(
                        MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression,
                            IdentifierName(CtParameterName), IdentifierName("CanBeCanceled")),
                        IdentifierName(CtParameterName),
                        IdentifierName(CtField));
                }
                arguments.Add(Argument(value));
            }

            var creation = ObjectCreationExpression(
                    QualifiedOrGenericName(Plan.EnumeratorClassName))
                .WithArgumentList(ArgumentList(SeparatedList(arguments)));

            var parameter = Parameter(Identifier(CtParameterName))
                .WithType(ParseTypeName(CancellationTokenType))
                .WithDefault(EqualsValueClause(
                    DefaultExpression(ParseTypeName(CancellationTokenType))));

            return MethodDeclaration(
                    GenericInterface(AsyncEnumeratorInterface), Identifier("GetAsyncEnumerator"))
                .WithModifiers(TokenList(Token(SyntaxKind.PublicKeyword)))
                .WithParameterList(ParameterList(SingletonSeparatedList(parameter)))
                .WithBody(Block(SingletonList<StatementSyntax>(ReturnStatement(creation))));
        }

        private NameSyntax QualifiedOrGenericName(string className)
        {
            var name = IdentifierName(className);
            if (Method.TypeParameterList == null)
                return name;
            return GenericName(Identifier(className))
                .WithTypeArgumentList(MethodTypeArguments!);
        }

        // ── 工厂方法 ──

        private MethodDeclarationSyntax BuildFactoryMethod()
        {
            var arguments = new List<ArgumentSyntax>();
            if (_usedThis && !Plan.Symbol.IsStatic)
                arguments.Add(Argument(ThisExpression()));

            foreach (var parameter in Plan.Parameters)
            {
                if (!_usedParameters.Contains(parameter.Symbol) || IsCtParameter(parameter.Symbol))
                    continue;
                arguments.Add(Argument(IdentifierName(parameter.Symbol.Name)));
            }

            if (Plan.CancellationTokenParameter != null
                && _usedParameters.Contains(Plan.CancellationTokenParameter))
            {
                arguments.Add(Argument(IdentifierName(Plan.CancellationTokenParameter.Name)));
            }

            var className = Plan.IsEnumerable
                ? Plan.EnumerableClassName!
                : Plan.EnumeratorClassName;
            var creation = ObjectCreationExpression(QualifiedOrGenericName(className))
                .WithArgumentList(ArgumentList(SeparatedList(arguments)));

            var nonAsyncModifiers = TokenList(Method.Modifiers
                .Where(m => !m.IsKind(SyntaxKind.AsyncKeyword))
                .Select(m => m.WithoutTrivia()));

            // 工厂不再是 async 迭代器：[EnumeratorCancellation] 留在形参上会触发
            // CS8424（特性仅在 async IAsyncEnumerable 迭代器方法上有效）；形参
            // 类型/名/默认值保留（工厂形参只为向生成类透传实参）。
            var strippedParameters = Method.ParameterList.Parameters
                .Select(p => (ParameterSyntax)p.WithAttributeLists(List<AttributeListSyntax>()))
                .ToList();
            var strippedParameterList = Method.ParameterList
                .WithParameters(SeparatedList(strippedParameters));

            return MarkSynthetic(Method
                .WithModifiers(nonAsyncModifiers)
                .WithParameterList(strippedParameterList)
                .WithBody(null)
                .WithExpressionBody(ArrowExpressionClause(creation))
                .WithSemicolonToken(Token(SyntaxKind.SemicolonToken)));
        }

        // ── 提升重写器 ──

        private sealed class HoistRewriter : CSharpSyntaxRewriter
        {
            private readonly MethodLowering _lowering;
            private readonly SemanticModel _model;

            public HoistRewriter(MethodLowering lowering, SemanticModel model)
                : base(visitIntoStructuredTrivia: false)
            {
                _lowering = lowering;
                _model = model;
            }

            public override SyntaxNode VisitIdentifierName(IdentifierNameSyntax node)
            {
                if (!IsNameofArgument(node))
                {
                    var symbol = _model.GetSymbolInfo(node).Symbol;
                    string? field = symbol switch
                    {
                        IParameterSymbol parameter => _lowering.ParameterField(parameter),
                        ILocalSymbol local => _lowering.LocalField(local),
                        _ => null,
                    };
                    if (field != null)
                        return IdentifierName(field).WithTriviaFrom(node);

                    // 宿主实例成员（隐式 this）→ __cs2jAsItThis_n.Member。
                    if (_lowering._usedThis
                        && !_lowering.Plan.Symbol.IsStatic
                        && symbol != null
                        && !symbol.IsStatic
                        && _lowering.IsHostMember(symbol))
                    {
                        return MemberAccessExpression(
                            SyntaxKind.SimpleMemberAccessExpression,
                            IdentifierName(_lowering.ThisField),
                            IdentifierName(node.Identifier.ValueText)).WithTriviaFrom(node);
                    }
                }
                return base.VisitIdentifierName(node);
            }

            public override SyntaxNode VisitThisExpression(ThisExpressionSyntax node)
            {
                if (_lowering._usedThis && !_lowering.Plan.Symbol.IsStatic)
                    return IdentifierName(_lowering.ThisField).WithTriviaFrom(node);
                return base.VisitThisExpression(node);
            }

            private static bool IsNameofArgument(IdentifierNameSyntax identifier)
            {
                return identifier.Parent is ArgumentSyntax
                {
                    Parent: ArgumentListSyntax { Parent: InvocationExpressionSyntax invocation }
                } && invocation.Expression is IdentifierNameSyntax { Identifier.Text: "nameof" };
            }
        }
    }
}
