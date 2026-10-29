using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace CSharpToJava.Core.MemberDesugar.Operators;

/// <summary>OC-1..OC-9 站点/声明重写器（单遍）。绑定模型可能是原始模型，也可能是
/// 属性重写后替换树的新鲜模型（设计 §4.5 修订/§4.6）——plan 查找一律走稳定键桥接，
/// 不依赖跨编译符号相等。运行于 Property rewriter 之后。</summary>
internal sealed partial class OperatorUseSiteRewriter : CSharpSyntaxRewriter
{
    private readonly SemanticModel _model;
    private readonly Dictionary<string, OperatorPlan> _active;
    private readonly HashSet<string> _knownKeys;
    private readonly ConversionResolver _conversions;
    // 前置属性/索引器阶段在同一遍内合成的写入方法（set_X）→ 目标属性符号。
    // 这些成员不在本文件的原始语义模型里，`b.set_X(arg)` 的实参转换没有
    // IArgumentOperation（GetOperation 为 null），需要按 receiver 类型 + 方法名
    // 回查目标类型后再套用本文件的转换 plan。
    private readonly Dictionary<(string Owner, string Method), IPropertySymbol> _synthesizedSetters;
    private readonly MemberDesugarOptions _options;
    private readonly MemberDesugarStatistics _stats;
    private readonly List<MemberDesugarDiagnostic> _diagnostics;
    private readonly string _filePath;
    private int _tempCounter;

    public OperatorUseSiteRewriter(
        SemanticModel model,
        IReadOnlyList<OperatorPlan> plannedPlans,
        IReadOnlyList<OperatorPlan> allPlans,
        MemberDesugarOptions options,
        MemberDesugarStatistics stats,
        List<MemberDesugarDiagnostic> diagnostics,
        string filePath,
        IReadOnlyList<Properties.PropertyPlan>? propertyPlans = null)
    {
        _model = model;
        _active = new Dictionary<string, OperatorPlan>(StringComparer.Ordinal);
        foreach (var p in plannedPlans) _active[PlanKey(p.Operator)] = p;
        _knownKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in allPlans) _knownKeys.Add(PlanKey(p.Operator));
        _conversions = new ConversionResolver(
            (CompilationUnitSyntax)model.SyntaxTree.GetRoot(), model, plannedPlans);
        _synthesizedSetters = new Dictionary<(string, string), IPropertySymbol>();
        if (propertyPlans != null)
        {
            foreach (var p in propertyPlans)
            {
                if (p.Outcome != MemberDesugarOutcome.Planned
                    || p.SetterMethodName == null
                    || p.Property.ContainingType == null)
                {
                    continue;
                }
                _synthesizedSetters[(
                    p.Property.ContainingType.OriginalDefinition.ToDisplayString(),
                    p.SetterMethodName)] = p.Property;
            }
        }
        _options = options;
        _stats = stats;
        _diagnostics = diagnostics;
        _filePath = filePath;
    }

    /// <summary>跨编译稳定键：类型全名::op 元数据名(参数表):返回类型（设计 §4.6）。</summary>
    internal static string PlanKey(IMethodSymbol op)
        => $"{op.ContainingType?.ToDisplayString()}::{op.Name}" +
           $"({string.Join(",", op.Parameters.Select(p => p.Type.ToDisplayString()))})" +
           $":{op.ReturnType.ToDisplayString()}";

    // ---- OC-4/OC-5 转换站点：Visit 漏斗（CSharpSyntaxRewriter 的 Visit(SyntaxNode) 是
    //      唯一全覆盖拦截点——DefaultVisit 对 C# 树不再被调用，实测 75/75 节点全经 Visit）----

    public override SyntaxNode? Visit(SyntaxNode? node)
    {
        var visited = base.Visit(node);
        if (node is ExpressionSyntax original
            && visited is ExpressionSyntax visitedExpr
            && _conversions.Recipes.TryGetValue(original, out var recipe))
        {
            // cast 站点的配方键在 cast 节点、实参是**内层运算元**（含复合 cast：用户转换
            // 的内层运算元），故取 Visit 后的 cast.Expression 而非 cast 节点本身。
            var argument = original is CastExpressionSyntax
                ? ((CastExpressionSyntax)visitedExpr).Expression
                : visitedExpr;
            return _conversions.Wrap(recipe, argument, original.SpanStart, _stats)
                .WithTriviaFrom(original)
                .WithAdditionalAnnotations(MemberDesugarSyntaxFacts.RewriteAnnotation);
        }
        return visited;
    }

    // ---- OD-1/OD-2 声明归约 + 白名单计数 + OC-9 residual 闸门 ----

    public override SyntaxNode? VisitOperatorDeclaration(OperatorDeclarationSyntax node)
    {
        if (_model.GetDeclaredSymbol(node) is not { } op)
            return base.VisitOperatorDeclaration(node);

        if (OperatorFacts.IsIncrementOrDecrement(op.Name))
        {
            _stats.IncrementDecrementDeferred++;
            EmitInfo(MemberDesugarDiagnosticCode.IncrementDecrementDeferred,
                OperatorDiagnostics.IncrementDeferred(op.ContainingType?.Name ?? "?"), node);
            return base.VisitOperatorDeclaration(node);
        }
        if (OperatorFacts.IsGateAndOr(op.Name) && OperatorFacts.IsGateType(op.ContainingType!))
        {
            _stats.GateOperatorsRetained++;
            EmitInfo(MemberDesugarDiagnosticCode.GateOperatorRetained,
                OperatorDiagnostics.GateRetained(op.ContainingType!.Name, op.Name), node);
            return base.VisitOperatorDeclaration(node);
        }
        if (_active.TryGetValue(PlanKey(op), out var plan))
            return ReplaceOperatorWithMethod(
                node.Modifiers, node.AttributeLists, node.ReturnType,
                node.ParameterList, node.Body, node.ExpressionBody, node.SemicolonToken,
                node.OperatorToken, plan);

        if (!_knownKeys.Contains(PlanKey(op)) && OperatorNames.IsSupportedOperatorName(op.Name))
            EmitResidual(op.Name, node);
        return base.VisitOperatorDeclaration(node);
    }

    public override SyntaxNode? VisitConversionOperatorDeclaration(ConversionOperatorDeclarationSyntax node)
    {
        if (_model.GetDeclaredSymbol(node) is not { } op)
            return base.VisitConversionOperatorDeclaration(node);
        if (_active.TryGetValue(PlanKey(op), out var plan))
            return ReplaceOperatorWithMethod(
                node.Modifiers, node.AttributeLists, node.Type,
                node.ParameterList, node.Body, node.ExpressionBody, node.SemicolonToken,
                node.OperatorKeyword, plan);
        if (!_knownKeys.Contains(PlanKey(op)))
            EmitResidual(op.Name, node);
        return base.VisitConversionOperatorDeclaration(node);
    }

    // ---- OC-1/OC-2 二元/一元站点（提升站点由分析闸门约束到简单运算元，公式展开在 Task 8）----

    public override SyntaxNode? VisitBinaryExpression(BinaryExpressionSyntax node)
    {
        if (node.IsKind(SyntaxKind.LogicalAndExpression)
            || node.IsKind(SyntaxKind.LogicalOrExpression))
            return base.VisitBinaryExpression(node);

        if (_model.GetSymbolInfo(node).Symbol is IMethodSymbol
            { MethodKind: MethodKind.UserDefinedOperator } m
            && TryGetPlan(m, out var plan))
        {
            var left = (ExpressionSyntax)Visit(node.Left)!;
            var right = (ExpressionSyntax)Visit(node.Right)!;
            return RenderCall(plan, m, node, left, right);
        }
        return base.VisitBinaryExpression(node);
    }

    public override SyntaxNode? VisitPrefixUnaryExpression(PrefixUnaryExpressionSyntax node)
    {
        if (OperatorRule.IsRewritableUnary(node)
            && _model.GetSymbolInfo(node).Symbol is IMethodSymbol
            { MethodKind: MethodKind.UserDefinedOperator } m
            && TryGetPlan(m, out var plan))
        {
            var operand = (ExpressionSyntax)Visit(node.Operand)!;
            return RenderCall(plan, m, node, operand);
        }
        return base.VisitPrefixUnaryExpression(node);
    }

    private ExpressionSyntax RenderCall(
        OperatorPlan plan, IMethodSymbol boundOperator, ExpressionSyntax site,
        params ExpressionSyntax[] args)
    {
        if (_model.GetOperation(site) is IBinaryOperation { IsLifted: true }
            or IUnaryOperation { IsLifted: true })
        {
            _stats.OperatorLiftedSitesExpanded++;
            return LiftedOperatorExpansion.ExpandBinaryOrUnary(
                plan, boundOperator, _model, site, args);
        }

        var qualifier = boundOperator.ContainingType!
            .ToMinimalDisplayString(_model, site.SpanStart);
        var call = SyntaxFactory.InvocationExpression(
                SyntaxFactory.MemberAccessExpression(
                    SyntaxKind.SimpleMemberAccessExpression,
                    SyntaxFactory.ParseTypeName(qualifier),
                    SyntaxFactory.IdentifierName(plan.GeneratedMethodName)),
                SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList(
                    args.Select(SyntaxFactory.Argument))))
            .WithAdditionalAnnotations(MemberDesugarSyntaxFacts.RewriteAnnotation);
        if (site is BinaryExpressionSyntax) _stats.OperatorBinarySitesRewritten++;
        else _stats.OperatorUnarySitesRewritten++;
        return call;
    }

    private bool TryGetPlan(IMethodSymbol m, out OperatorPlan plan)
        => _active.TryGetValue(PlanKey(m.IsDefinition ? m : m.OriginalDefinition), out plan!)
           && plan != null;

    // ---- OC-3 复合赋值（决策 2b）：简单左值内联；复杂接收者在语句位提升 __cs2j_recv ----

    /// <summary>
    /// 实参位置的隐式用户转换兜底。属性/索引器前置阶段在同一遍内合成的新调用
    /// （`b.setValue(r)`）不在本文件原始语义模型中：<c>GetOperation(argument)</c> 为 null，
    /// <see cref="ConversionResolver"/> 的语法父链探测也命中不了。此时按
    /// 「receiver 类型 + 合成 setter 名」回查目标属性类型，与源类型配对后套用本文件
    /// 的转换 plan（regex-sdk 的 `ByRef&lt;LocalBuilder&gt;.set_Value(spanLocal)` 残留即此形态）。
    /// 已有配方的站点跳过，避免双重包裹。
    /// </summary>
    public override SyntaxNode? VisitArgument(ArgumentSyntax node)
    {
        var visited = (ArgumentSyntax)base.VisitArgument(node)!;
        if (node.Expression is not ExpressionSyntax originalExpr
            || visited.Expression is not ExpressionSyntax value
            || _conversions.Recipes.ContainsKey(originalExpr))
            return visited;

        OperatorPlan plan;
        ITypeSymbol? sourceType;
        ITypeSymbol? targetType;
        var operation = _model.GetOperation(node);
        if (operation is IArgumentOperation argument)
        {
            if (!argument.InConversion.IsUserDefined
                || argument.InConversion.MethodSymbol is not { } m
                || !TryGetPlan(m, out var opPlan))
                return visited;
            plan = opPlan;
            sourceType = _model.GetTypeInfo(originalExpr).Type;
            targetType = argument.Parameter?.Type;
        }
        else if (operation == null
                 && node.Parent?.Parent is InvocationExpressionSyntax invocation
                 && TryResolveSynthesizedTarget(invocation, out var synthesizedTarget))
        {
            sourceType = _model.GetTypeInfo(originalExpr).Type;
            targetType = synthesizedTarget;
            var found = TryFindConversionPlan(sourceType, targetType, out plan);
            if (!found)
                return visited;
        }
        else
        {
            return visited;
        }

        var qualifier = plan.Operator.ContainingType!.ToDisplayString();
        var sourceNullable = sourceType is INamedTypeSymbol
            { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T };
        var targetNullable = targetType is INamedTypeSymbol
            { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T };

        // S? → T / S? → T?：与 OperatorRule/ConversionResolver 的提升公式一致，
        // 缺值分支保持 null（ExpandLiftedConversion）。
        if (sourceNullable && targetType != null)
        {
            _stats.OperatorLiftedSitesExpanded++;
            var underlying = targetNullable
                ? ((INamedTypeSymbol)targetType).TypeArguments[0]
                : targetType;
            var nullableDisplay = underlying.ToDisplayString() + "?";
            return node.WithExpression(LiftedOperatorExpansion.ExpandLiftedConversion(
                qualifier, plan.GeneratedMethodName, nullableDisplay, value));
        }

        _stats.OperatorConversionSitesRewritten++;
        var call = SyntaxFactory.InvocationExpression(
                SyntaxFactory.MemberAccessExpression(
                    SyntaxKind.SimpleMemberAccessExpression,
                    SyntaxFactory.ParseTypeName(qualifier),
                    SyntaxFactory.IdentifierName(plan.GeneratedMethodName)),
                SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList(
                    new[] { SyntaxFactory.Argument(value) })))
            .WithAdditionalAnnotations(MemberDesugarSyntaxFacts.RewriteAnnotation);
        return node.WithExpression(call);
    }

    /// <summary>合成 setter 的目标类型：receiver 为构造泛型时把属性的类型参数替换为实参。</summary>
    private bool TryResolveSynthesizedTarget(
        InvocationExpressionSyntax invocation, out ITypeSymbol targetType)
    {
        targetType = null!;
        if (invocation.Expression is not MemberAccessExpressionSyntax access)
            return false;
        if (_model.GetTypeInfo(access.Expression).Type is not INamedTypeSymbol receiverType)
            return false;
        var key = (receiverType.OriginalDefinition.ToDisplayString(),
                   access.Name.Identifier.ValueText);
        if (!_synthesizedSetters.TryGetValue(key, out var property))
            return false;
        targetType = SubstitutePropertyType(property.Type, receiverType);
        return true;
    }

    private static ITypeSymbol SubstitutePropertyType(
        ITypeSymbol type, INamedTypeSymbol receiverType)
    {
        if (type is ITypeParameterSymbol parameter
            && parameter.DeclaringType != null
            && parameter.Ordinal >= 0
            && parameter.Ordinal < receiverType.TypeArguments.Length)
        {
            return receiverType.TypeArguments[parameter.Ordinal];
        }
        return type;
    }

    /// <summary>按 (源类型, 目标类型) 在活动 plan 中找唯一的转换运算符。</summary>
    private bool TryFindConversionPlan(
        ITypeSymbol? sourceType, ITypeSymbol? targetType, out OperatorPlan plan)
    {
        plan = null!;
        if (sourceType == null || targetType == null) return false;
        var sourceUnderlying = sourceType is INamedTypeSymbol
            { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } sn
                ? sn.TypeArguments[0]
                : sourceType;
        var targetUnderlying = targetType is INamedTypeSymbol
            { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } tn
                ? tn.TypeArguments[0]
                : targetType;
        // plan 来自分析期的编译，_model 属于重写期的替换编译；跨编译符号引用不等，
        // 因此按显示串配对（构造泛型归一后稳定）。
        var sourceDisplay = sourceUnderlying.ToDisplayString();
        var targetDisplay = targetUnderlying.ToDisplayString();
        foreach (var candidate in _active.Values)
        {
            if (!candidate.IsConversion || candidate.Operator.Parameters.Length != 1)
                continue;
            if (!string.Equals(
                    candidate.Operator.Parameters[0].Type.ToDisplayString(),
                    sourceDisplay, StringComparison.Ordinal))
                continue;
            if (!string.Equals(
                    candidate.Operator.ReturnType.ToDisplayString(),
                    targetDisplay, StringComparison.Ordinal))
                continue;
            plan = candidate;
            return true;
        }
        return false;
    }

    public override SyntaxNode? VisitAssignmentExpression(AssignmentExpressionSyntax node)
    {
        if (OperatorRule.IsCompoundKind(node)
            && _model.GetOperation(node) is ICompoundAssignmentOperation compound
            && compound.OperatorMethod is { } m && TryGetPlan(m, out var plan)
            && (OperatorFacts.IsSimpleLvalue(node.Left) || IsInlineElementTarget(node)))
        {
            var left = (ExpressionSyntax)Visit(node.Left)!;
            var right = (ExpressionSyntax)Visit(node.Right)!;
            var inner = RenderCompoundInner(plan, m, compound, left, right);
            _stats.OperatorCompoundSitesRewritten++;
            return SyntaxFactory.AssignmentExpression(
                    SyntaxKind.SimpleAssignmentExpression, left, inner)
                .WithTriviaFrom(node);
        }
        return base.VisitAssignmentExpression(node);
    }

    /// <summary>语句位的简单元素访问目标：内联为 `a[i] = op(a[i], v)`，写入由后续
    /// 索引器 pass（索引器 plan）或 Java 侧索引语义承接。</summary>
    private static bool IsInlineElementTarget(AssignmentExpressionSyntax node)
        => node.Left is ElementAccessExpressionSyntax element
           && node.Parent is ExpressionStatementSyntax
           && OperatorFacts.IsSimpleElementTarget(element);

    public override SyntaxNode? VisitExpressionStatement(ExpressionStatementSyntax node)
    {
        // 复杂接收者（带副作用的接收者表达式）：提升接收者只求值一次（设计决策 2b）
        if (node.Expression is AssignmentExpressionSyntax assign
            && OperatorRule.IsCompoundKind(assign)
            && _model.GetOperation(assign) is ICompoundAssignmentOperation compound
            && compound.OperatorMethod is { } m && TryGetPlan(m, out var plan)
            && assign.Left is MemberAccessExpressionSyntax access
            && !OperatorFacts.IsSimpleLvalue(access))
        {
            var recv = (ExpressionSyntax)Visit(access.Expression)!;
            var right = (ExpressionSyntax)Visit(assign.Right)!;
            var temp = MemberDesugarSyntaxFacts.TempName(ref _tempCounter, "recv");
            var tempIdent = SyntaxFactory.IdentifierName(temp);
            var leftRewritten = access.WithExpression(tempIdent);
            var inner = RenderCompoundInner(plan, m, compound, leftRewritten, right);
            _stats.OperatorCompoundSitesRewritten++;
            var store = SyntaxFactory.AssignmentExpression(
                SyntaxKind.SimpleAssignmentExpression, leftRewritten, inner);
            return SyntaxFactory.Block(
                    SyntaxFactory.LocalDeclarationStatement(
                        SyntaxFactory.VariableDeclaration(
                            SyntaxFactory.IdentifierName("var")
                                .WithTrailingTrivia(SyntaxFactory.Space),
                            SyntaxFactory.SingletonSeparatedList(
                                SyntaxFactory.VariableDeclarator(temp)
                                    .WithInitializer(SyntaxFactory.EqualsValueClause(recv))))),
                    SyntaxFactory.ExpressionStatement(store))
                .WithLeadingTrivia(node.GetLeadingTrivia())
                .WithTrailingTrivia(node.GetTrailingTrivia())
                .WithAdditionalAnnotations(MemberDesugarSyntaxFacts.RewriteAnnotation);
        }
        return base.VisitExpressionStatement(node);
    }

    /// <summary>checked 上下文：用户运算符调用点的 checked 语义已由**变体选择**
    /// （op_Checked* → AddChecked）承载，包装壳对纯调用表达式是冗余的（壳内已无数值运算）；
    /// 内置 checked 算术与标准转换的 checked 壳保持原样。</summary>
    public override SyntaxNode? VisitCheckedExpression(CheckedExpressionSyntax node)
    {
        var inner = (ExpressionSyntax)Visit(node.Expression)!;
        var redundant = _model.GetOperation(node.Expression) switch
        {
            IBinaryOperation { OperatorMethod: { } bm } => TryGetPlan(bm, out _),
            IUnaryOperation { OperatorMethod: { } um } => TryGetPlan(um, out _),
            ICompoundAssignmentOperation { OperatorMethod: { } cm } => TryGetPlan(cm, out _),
            IConversionOperation { OperatorMethod: { } vm } => TryGetPlan(vm, out _),
            _ => false,
        };
        return redundant
            ? inner.WithTriviaFrom(node)
            : node.WithExpression(inner);
    }

    public override SyntaxNode? VisitBlock(BlockSyntax node)
        => node.WithStatements(SyntaxFactory.List(SpliceStatements(node.Statements)));

    public override SyntaxNode? VisitSwitchSection(SwitchSectionSyntax node)
        => node.WithStatements(SyntaxFactory.List(SpliceStatements(node.Statements)));

    private IEnumerable<StatementSyntax> SpliceStatements(SyntaxList<StatementSyntax> statements)
    {
        foreach (var stmt in statements)
        {
            if (Visit(stmt) is not StatementSyntax visited) continue;
            if (visited is BlockSyntax spliced
                && spliced.HasAnnotations(MemberDesugarSyntaxFacts.RewriteAnnotation.Kind))
            {
                var list = spliced.Statements.ToList();
                if (list.Count > 0)
                {
                    list[0] = list[0].WithLeadingTrivia(
                        spliced.GetLeadingTrivia().Concat(list[0].GetLeadingTrivia()));
                    list[^1] = list[^1].WithTrailingTrivia(spliced.GetTrailingTrivia());
                }
                foreach (var s in list) yield return s;
                continue;
            }
            yield return visited;
        }
    }

    /// <summary>语义恒等 `x = (T)(x op v)`：内层调用 + 入转换（右运算元）/出转换（结果）链。
    /// 入/出转换按 `ClassifyConversion` 实测判定（CommonConversion 无 IsExplicit）。</summary>
    private ExpressionSyntax RenderCompoundInner(
        OperatorPlan plan, IMethodSymbol boundOperator, ICompoundAssignmentOperation compound,
        ExpressionSyntax left, ExpressionSyntax right)
    {
        var compilation = (CSharpCompilation)_model.Compilation;
        var qualifier = boundOperator.ContainingType!
            .ToMinimalDisplayString(_model, compound.Syntax.SpanStart);
        if (compound.IsLifted)
        {
            // 公式 ⑥：`x = x.HasValue ? D.Add(x.Value, v) : (R?)null`（入/出转换为可空提升语义，
            // 不走下面的标准转换链判定——可空→非可空的“转换”是守卫，不是 cast）
            _stats.OperatorLiftedSitesExpanded++;
            return LiftedOperatorExpansion.ExpandLiftedCompound(
                qualifier, plan.GeneratedMethodName, left, right,
                compound.Type!, _model, compound.Syntax.SpanStart);
        }
        var argRight = right;
        if (boundOperator.Parameters.Length == 2 && compound.Value.Type is { } rightType)
        {
            var inConv = compilation.ClassifyConversion(rightType, boundOperator.Parameters[1].Type);
            argRight = ApplyInConversion(inConv, right, boundOperator.Parameters[1].Type);
        }
        var call = SyntaxFactory.InvocationExpression(
                SyntaxFactory.MemberAccessExpression(
                    SyntaxKind.SimpleMemberAccessExpression,
                    SyntaxFactory.ParseTypeName(qualifier),
                    SyntaxFactory.IdentifierName(plan.GeneratedMethodName)),
                SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList(new[]
                {
                    SyntaxFactory.Argument(left),
                    SyntaxFactory.Argument(argRight),
                })))
            .WithAdditionalAnnotations(MemberDesugarSyntaxFacts.RewriteAnnotation);
        var outConv = compilation.ClassifyConversion(
            boundOperator.ReturnType, compound.Type!);
        return ApplyOutConversion(outConv, call, compound.Type!, compound.Syntax.SpanStart);
    }

    private ExpressionSyntax ApplyInConversion(
        Conversion inConversion, ExpressionSyntax value, ITypeSymbol parameterType)
    {
        if (inConversion.IsUserDefined && inConversion.MethodSymbol is { } um
            && TryGetPlan(um, out var up))
            return InvokeConversion(up, value);
        if (!inConversion.IsImplicit && inConversion.Exists)
            return SyntaxFactory.CastExpression(
                SyntaxFactory.ParseTypeName(
                    parameterType.ToMinimalDisplayString(_model, value.SpanStart)),
                value);
        return value;
    }

    private ExpressionSyntax ApplyOutConversion(
        Conversion outConversion, ExpressionSyntax value, ITypeSymbol targetType, int spanStart)
    {
        if (outConversion.IsUserDefined && outConversion.MethodSymbol is { } um
            && TryGetPlan(um, out var up))
            return InvokeConversion(up, value);
        if (!outConversion.IsImplicit && outConversion.Exists)
            return SyntaxFactory.CastExpression(
                SyntaxFactory.ParseTypeName(
                    targetType.ToMinimalDisplayString(_model, spanStart)),
                value);
        return value;
    }

    private ExpressionSyntax InvokeConversion(OperatorPlan conversionPlan, ExpressionSyntax value)
    {
        var qualifier = conversionPlan.Operator.ContainingType!
            .ToMinimalDisplayString(_model, value.SpanStart);
        return SyntaxFactory.InvocationExpression(
                SyntaxFactory.MemberAccessExpression(
                    SyntaxKind.SimpleMemberAccessExpression,
                    SyntaxFactory.ParseTypeName(qualifier),
                    SyntaxFactory.IdentifierName(conversionPlan.GeneratedMethodName)),
                SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList(
                    new[] { SyntaxFactory.Argument(value) })))
            .WithAdditionalAnnotations(MemberDesugarSyntaxFacts.RewriteAnnotation);
    }

    // ---- 声明→方法 ----

    private SyntaxNode ReplaceOperatorWithMethod(
        SyntaxTokenList modifiers, SyntaxList<AttributeListSyntax> attributeLists,
        TypeSyntax returnType, ParameterListSyntax parameterList,
        BlockSyntax? body, ArrowExpressionClauseSyntax? expressionBody, SyntaxToken semicolon,
        SyntaxToken operatorToken, OperatorPlan plan)
    {
        // checked 不进方法修饰符；implicit/explicit/operator 槽位由标识符替代（OD-1/OD-2）
        var cleaned = SyntaxFactory.TokenList(modifiers
            .Where(m => !m.IsKind(SyntaxKind.CheckedKeyword)));
        if (cleaned.Count == 0)
            cleaned = SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.PublicKeyword));

        var visitedBody = body != null ? (BlockSyntax)Visit(body) : null;
        var visitedExpr = expressionBody != null
            ? (ArrowExpressionClauseSyntax)Visit(expressionBody) : null;

        // Conversion declarations pass the `operator` keyword, whose leading
        // trivia is empty (the space after implicit/explicit is trailing
        // trivia of that keyword), so WithTriviaFrom would fuse the return
        // type and method name (`doubleToDouble`).  Keep the separating space
        // explicitly for conversions; binary operator tokens carry it already.
        var identifier = plan.IsConversion
            ? SyntaxFactory.Identifier(plan.GeneratedMethodName)
                .WithLeadingTrivia(SyntaxFactory.Space)
            : SyntaxFactory.Identifier(plan.GeneratedMethodName).WithTriviaFrom(operatorToken);

        var method = SyntaxFactory.MethodDeclaration(returnType, identifier)
            .WithModifiers(cleaned)
            .WithParameterList(parameterList)
            .WithBody(visitedBody)
            .WithExpressionBody(visitedExpr)
            .WithSemicolonToken(semicolon)
            .WithAttributeLists(attributeLists)
            .WithAdditionalAnnotations(MemberDesugarSyntaxFacts.RewriteAnnotation);

        var loc = operatorToken.Parent!;
        if (plan.GeneratedMethodName != plan.BaseMethodName)
            EmitInfo(MemberDesugarDiagnosticCode.OperatorNameDisambiguated,
                OperatorDiagnostics.NameDisambiguated(plan.BaseMethodName, plan.GeneratedMethodName), loc);
        if (plan.IsConversion)
            EmitInfo(MemberDesugarDiagnosticCode.ConversionPlanCreated,
                OperatorDiagnostics.ConversionPlanCreated(plan.GeneratedMethodName,
                    plan.Operator.ReturnType.ToDisplayString(), "conversion"), loc);
        else
            EmitInfo(MemberDesugarDiagnosticCode.OperatorPlanCreated,
                OperatorDiagnostics.PlanCreated(plan.GeneratedMethodName, 0), loc);
        return method;
    }

    private void EmitInfo(MemberDesugarDiagnosticCode code, string message, SyntaxNode node)
    {
        if (!_options.ReportInfo) return;
        var span = node.GetLocation().GetLineSpan().StartLinePosition;
        _diagnostics.Add(new MemberDesugarDiagnostic(
            MemberDesugarSeverity.Info, code, message,
            _filePath, span.Line + 1, span.Character + 1, null));
    }

    private void EmitResidual(string name, SyntaxNode node)
    {
        var span = node.GetLocation().GetLineSpan().StartLinePosition;
        _diagnostics.Add(new MemberDesugarDiagnostic(
            _options.Strict ? MemberDesugarSeverity.Error : MemberDesugarSeverity.Warning,
            MemberDesugarDiagnosticCode.ResidualOperator,
            OperatorDiagnostics.Residual(name), _filePath, span.Line + 1, span.Character + 1, name));
        _stats.PlansSkipped++;
        _stats.SkippedByReason.TryGetValue(MemberDesugarDiagnosticCode.ResidualOperator, out var n);
        _stats.SkippedByReason[MemberDesugarDiagnosticCode.ResidualOperator] = n + 1;
    }
}
