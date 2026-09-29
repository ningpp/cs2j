using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.MemberDesugar.Operators;

/// <summary>
/// C14 运算符规则（Kind=Operator, Order=40）。阶段 1 声明收集+声明级闸门（5175/5177）；
/// 阶段 2 站点扫描（5173/5174/5176/5179 升级 plan Infeasible）；阶段 3 命名预留+plan 产出。
/// Info 级白名单（inc/dec、门控 &amp;/|、op_True/False、表外槽位）不建 plan，重写期计数（设计 §6）。
/// </summary>
[MemberDesugarRule(MemberDesugarKind.Operator, 40)]
internal sealed class OperatorRule : IMemberDesugarRule
{
    public MemberDesugarKind Kind => MemberDesugarKind.Operator;
    public int Order => 40;

    public void Analyze(MemberDesugarAnalysisContext context, MemberDesugarPlanBuilder builder)
    {
        // ---- 阶段 1：声明收集 + 声明级闸门 ----
        var candidates = new Dictionary<IMethodSymbol, (bool IsConversion, string BaseName)>(
            SymbolEqualityComparer.Default);
        var failures = new Dictionary<IMethodSymbol, (MemberDesugarDiagnosticCode Code, string Reason)>(
            SymbolEqualityComparer.Default);
        var userSkipped = new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default);

        foreach (var model in context.Models)
        {
            var root = (CompilationUnitSyntax)model.SyntaxTree.GetRoot();
            foreach (var decl in root.DescendantNodes().OfType<OperatorDeclarationSyntax>())
                CollectDeclaration(decl, model, context.Options, candidates, failures, userSkipped);
            foreach (var decl in root.DescendantNodes().OfType<ConversionOperatorDeclarationSyntax>())
                CollectDeclaration(decl, model, context.Options, candidates, failures, userSkipped);
        }

        // ---- 阶段 2：站点扫描 ----
        var siteFiles = new Dictionary<IMethodSymbol, HashSet<string>>(SymbolEqualityComparer.Default);
        var siteFailures = new Dictionary<IMethodSymbol, (MemberDesugarDiagnosticCode Code, string Reason)>(
            SymbolEqualityComparer.Default);

        foreach (var model in context.Models)
        {
            var filePath = model.SyntaxTree.FilePath;
            var root = (CompilationUnitSyntax)model.SyntaxTree.GetRoot();
            foreach (var node in root.DescendantNodes())
            {
                switch (node)
                {
                    case BinaryExpressionSyntax bin
                        when !bin.IsKind(SyntaxKind.LogicalAndExpression)
                             && !bin.IsKind(SyntaxKind.LogicalOrExpression):
                        ScanOperatorSite(bin, model.GetSymbolInfo(bin).Symbol, model, filePath,
                            candidates, siteFiles, siteFailures, context.Options,
                            context.MetadataOperatorSitesByFile);
                        break;

                    case PrefixUnaryExpressionSyntax un when IsRewritableUnary(un):
                        ScanOperatorSite(un, model.GetSymbolInfo(un).Symbol, model, filePath,
                            candidates, siteFiles, siteFailures, context.Options,
                            context.MetadataOperatorSitesByFile);
                        break;

                    case AssignmentExpressionSyntax assign when IsCompoundKind(assign):
                        ScanCompoundSite(assign, model, filePath, candidates, siteFiles, siteFailures,
                            context.Options, context.MetadataOperatorSitesByFile);
                        break;

                    case ExpressionSyntax expr:
                        // 转换站点（决策 4）：cast 语法与非 cast 隐式站点统一经 GetOperation 判定
                        ScanConversionNode(expr, model, filePath, candidates, siteFiles, siteFailures,
                            context.Options, context.MetadataOperatorSitesByFile);
                        break;
                }
            }
        }

        // ---- 阶段 3：命名预留 + plan 产出（按类型分组、源 span 排序，确定性）----
        foreach (var group in candidates.Keys.GroupBy(o => o.ContainingType, SymbolEqualityComparer.Default))
        {
            var reserved = new HashSet<string>(StringComparer.Ordinal);
            // 碰撞预留含基类链成员（决策 8）：如 string 目标基名 ToString 与 object.ToString 冲突。
            // 运算符/转换符号自身不预留——同名重载（op_Multiply(Point,int)/(int,Point)）
            // 必须共享生成名以镜像 Java 重载（设计决策 8 注记）。
            for (var b = (INamedTypeSymbol)group.Key!; b != null; b = b.BaseType)
                foreach (var member in b.GetMembers())
                    if (member is not IMethodSymbol
                        { MethodKind: MethodKind.UserDefinedOperator or MethodKind.Conversion })
                        reserved.Add(member.Name);

            // 同名重载组（op_Multiply(Point,int)/(int,Point)/(Point,Point)）共享生成名以
            // 镜像 Java 重载与 OperatorTransformer 今发名（决策 1/8）；异名组按组内首个
            // 源 span 顺序逐组消歧，确定性。
            var finalNames = new Dictionary<IMethodSymbol, string>(SymbolEqualityComparer.Default);
            foreach (var nameGroup in group
                         .GroupBy(o => candidates[o].BaseName, StringComparer.Ordinal)
                         .OrderBy(g => g.Min(SpanStart)))
            {
                var groupName = OperatorNames.Disambiguate(nameGroup.Key, reserved);
                reserved.Add(groupName);
                // 组内同类碰撞（*Operator 已被用户成员占用）整组同升级
                foreach (var op in nameGroup) finalNames[op] = groupName;
            }

            foreach (var op in group.OrderBy(SpanStart))
            {
                var (isConv, baseName) = candidates[op];
                var finalName = finalNames[op];

                var touched = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var loc in op.Locations.Where(l => l.IsInSource))
                    touched.Add(loc.SourceTree!.FilePath);
                if (siteFiles.TryGetValue(op, out var files))
                    foreach (var f in files) touched.Add(f);

                if (siteFailures.TryGetValue(op, out var fail))
                {
                    builder.Add(new OperatorPlan
                    {
                        Id = OperatorPlan.BuildId(op), Kind = MemberDesugarKind.Operator,
                        Symbol = op, Outcome = MemberDesugarOutcome.Infeasible,
                        TouchedFiles = DeclarationFiles(op),
                        FailCode = fail.Code, FailReason = fail.Reason,
                        Operator = op, IsConversion = isConv,
                        GeneratedMethodName = finalName, BaseMethodName = baseName,
                    });
                    continue;
                }

                builder.Add(new OperatorPlan
                {
                    Id = OperatorPlan.BuildId(op), Kind = MemberDesugarKind.Operator,
                    Symbol = op, Outcome = MemberDesugarOutcome.Planned,
                    TouchedFiles = touched.ToList(),
                    Operator = op, IsConversion = isConv,
                    GeneratedMethodName = finalName, BaseMethodName = baseName,
                });
            }
        }

        foreach (var (op, fail) in failures)
            builder.Add(new OperatorPlan
            {
                Id = OperatorPlan.BuildId(op), Kind = MemberDesugarKind.Operator,
                Symbol = op, Outcome = MemberDesugarOutcome.Infeasible,
                TouchedFiles = DeclarationFiles(op),
                FailCode = fail.Code, FailReason = fail.Reason,
                Operator = op, IsConversion = op.MethodKind == MethodKind.Conversion,
                GeneratedMethodName = op.Name, BaseMethodName = op.Name,
            });

        foreach (var op in userSkipped)
            builder.Add(new OperatorPlan
            {
                Id = OperatorPlan.BuildId(op), Kind = MemberDesugarKind.Operator,
                Symbol = op, Outcome = MemberDesugarOutcome.UserSkipped,
                TouchedFiles = DeclarationFiles(op),
                Operator = op, IsConversion = op.MethodKind == MethodKind.Conversion,
                GeneratedMethodName = op.Name, BaseMethodName = op.Name,
            });
    }

    // ---- 声明收集 ----

    private static void CollectDeclaration(
        SyntaxNode decl, SemanticModel model, MemberDesugarOptions options,
        Dictionary<IMethodSymbol, (bool IsConversion, string BaseName)> candidates,
        Dictionary<IMethodSymbol, (MemberDesugarDiagnosticCode Code, string Reason)> failures,
        HashSet<IMethodSymbol> userSkipped)
    {
        if (model.GetDeclaredSymbol(decl) is not IMethodSymbol op
            || !OperatorFacts.IsUserDefinedOperatorKind(op)) return;
        if (candidates.ContainsKey(op) || failures.ContainsKey(op) || userSkipped.Contains(op)) return;

        // Info 级白名单：不建 plan（重写期计数，设计 §6）
        if (op.MethodKind == MethodKind.UserDefinedOperator)
        {
            if (OperatorFacts.IsIncrementOrDecrement(op.Name)) return;
            if (OperatorFacts.IsTrueOrFalse(op.Name)) return;
            if (OperatorFacts.IsGateAndOr(op.Name) && OperatorFacts.IsGateType(op.ContainingType!)) return;
            if (!OperatorNames.IsSupportedOperatorName(op.Name)) return;
        }

        if (MatchesSet(options.SkipQualifiedNames, op))
        {
            userSkipped.Add(op);
            return;
        }

        if (OperatorFacts.IsInterfaceStaticAbstract(op))
        {
            failures[op] = (MemberDesugarDiagnosticCode.StaticAbstractInterfaceOperator,
                OperatorDiagnostics.StaticAbstractInterface(op.Name));
            return;
        }

        if (op.MethodKind == MethodKind.Conversion)
        {
            var isExplicit = decl is ConversionOperatorDeclarationSyntax c
                && c.ImplicitOrExplicitKeyword.IsKind(SyntaxKind.ExplicitKeyword);
            if (!OperatorNames.TryGetConversionBaseName(op.ReturnType, isExplicit, out var convBase))
            {
                failures[op] = (MemberDesugarDiagnosticCode.ConversionMirrorMiss,
                    OperatorDiagnostics.MirrorMiss(op.Name, op.ReturnType.ToDisplayString()));
                return;
            }
            candidates[op] = (true, convBase);
        }
        else
        {
            candidates[op] = (false, OperatorNames.ConvertOperatorName(op.Name));
        }
    }

    // ---- 站点扫描 ----

    private static void ScanOperatorSite(
        ExpressionSyntax site, ISymbol? symbol, SemanticModel model, string filePath,
        Dictionary<IMethodSymbol, (bool IsConversion, string BaseName)> candidates,
        Dictionary<IMethodSymbol, HashSet<string>> siteFiles,
        Dictionary<IMethodSymbol, (MemberDesugarDiagnosticCode Code, string Reason)> siteFailures,
        MemberDesugarOptions options, Dictionary<string, int> metadataSites)
    {
        if (symbol is not IMethodSymbol { MethodKind: MethodKind.UserDefinedOperator } m) return;
        if (!OperatorFacts.HasSourceDeclaration(m.OriginalDefinition))
        {
            // 决策 9：元数据-only 运算符（DateTime+TimeSpan 等）站点只计数不动
            RecordMetadataSite(metadataSites, filePath);
            return;
        }
        if (!TryNormalize(m, candidates, out var planOp)) return;
        Record(siteFiles, planOp, filePath);

        if (MemberDesugarSyntaxFacts.IsInsideExpressionTree(site, model)
            && !MatchesSet(options.ForceQualifiedNames, planOp))
        {
            MarkFailure(siteFailures, planOp,
                MemberDesugarDiagnosticCode.OperatorExpressionTreeSite,
                OperatorDiagnostics.ExpressionTree(planOp.Name));
            return;
        }

        var operation = model.GetOperation(site);
        if (operation != null && ContainsDynamicOperand(operation))
        {
            MarkFailure(siteFailures, planOp, MemberDesugarDiagnosticCode.OperatorDynamicSite,
                OperatorDiagnostics.Dynamic(planOp.Name));
            return;
        }
        if (operation is IBinaryOperation { IsLifted: true } or IUnaryOperation { IsLifted: true }
            && !LiftedSitesAreBuildable(operation, model))
        {
            MarkFailure(siteFailures, planOp, MemberDesugarDiagnosticCode.UnbuildableLift,
                OperatorDiagnostics.UnbuildableLift(planOp.Name));
        }
    }

    private static void ScanCompoundSite(
        AssignmentExpressionSyntax assign, SemanticModel model, string filePath,
        Dictionary<IMethodSymbol, (bool IsConversion, string BaseName)> candidates,
        Dictionary<IMethodSymbol, HashSet<string>> siteFiles,
        Dictionary<IMethodSymbol, (MemberDesugarDiagnosticCode Code, string Reason)> siteFailures,
        MemberDesugarOptions options, Dictionary<string, int> metadataSites)
    {
        if (model.GetOperation(assign) is not ICompoundAssignmentOperation compound) return;
        if (compound.OperatorMethod is not { } m) return; // 事件 += 是 EventAssignment，不进这里
        if (!OperatorFacts.HasSourceDeclaration(m.OriginalDefinition))
        {
            RecordMetadataSite(metadataSites, filePath);
            return;
        }
        if (!TryNormalize(m, candidates, out var planOp)) return;
        Record(siteFiles, planOp, filePath);

        if (MemberDesugarSyntaxFacts.IsInsideExpressionTree(assign, model)
            && !MatchesSet(options.ForceQualifiedNames, planOp))
        {
            MarkFailure(siteFailures, planOp,
                MemberDesugarDiagnosticCode.OperatorExpressionTreeSite,
                OperatorDiagnostics.ExpressionTree(planOp.Name));
            return;
        }

        // 决策 2b：属性/索引器目标 → 整 plan 跳过；复杂接收者且非语句位 → 不可提升
        if (model.GetSymbolInfo(assign.Left).Symbol is IPropertySymbol)
        {
            MarkFailure(siteFailures, planOp,
                MemberDesugarDiagnosticCode.CompoundTargetUnsupported,
                OperatorDiagnostics.CompoundUnsupported(planOp.Name, "property/indexer target"));
            return;
        }
        if (assign.Left is not IdentifierNameSyntax && assign.Parent is not ExpressionStatementSyntax)
        {
            MarkFailure(siteFailures, planOp,
                MemberDesugarDiagnosticCode.CompoundTargetUnsupported,
                OperatorDiagnostics.CompoundUnsupported(planOp.Name, "non-statement complex receiver"));
            return;
        }
        if (compound.IsLifted && !OperatorFacts.IsSimpleLiftedOperand(assign.Left, model))
        {
            MarkFailure(siteFailures, planOp, MemberDesugarDiagnosticCode.UnbuildableLift,
                OperatorDiagnostics.UnbuildableLift(planOp.Name));
            return;
        }

        // 入/出转换链若绑定用户转换，其 plan 必须同为候选（否则链不可重建 → 5176）
        var compilation = (CSharpCompilation)model.Compilation;
        if (planOp.Parameters.Length == 2 && compound.Value.Type is { } rightType)
        {
            var inConv = compilation.ClassifyConversion(rightType, planOp.Parameters[1].Type);
            if (inConv.IsUserDefined && inConv.MethodSymbol is { } inM
                && !TryNormalize(inM, candidates, out _))
            {
                MarkFailure(siteFailures, planOp, MemberDesugarDiagnosticCode.UnbuildableLift,
                    OperatorDiagnostics.UnbuildableLift(planOp.Name));
            }
        }
        var outConv = compilation.ClassifyConversion(planOp.ReturnType, compound.Type!);
        if (outConv.IsUserDefined && outConv.MethodSymbol is { } outM
            && !TryNormalize(outM, candidates, out _))
        {
            MarkFailure(siteFailures, planOp, MemberDesugarDiagnosticCode.UnbuildableLift,
                OperatorDiagnostics.UnbuildableLift(planOp.Name));
        }
    }

    private static void ScanConversionNode(
        ExpressionSyntax node, SemanticModel model, string filePath,
        Dictionary<IMethodSymbol, (bool IsConversion, string BaseName)> candidates,
        Dictionary<IMethodSymbol, HashSet<string>> siteFiles,
        Dictionary<IMethodSymbol, (MemberDesugarDiagnosticCode Code, string Reason)> siteFailures,
        MemberDesugarOptions options, Dictionary<string, int> metadataSites)
    {
        if (model.GetOperation(node) is not IConversionOperation conv
            || conv.OperatorMethod is not { } m) return; // 标准转换 OperatorMethod 为 null
        if (!OperatorFacts.HasSourceDeclaration(m.OriginalDefinition))
        {
            RecordMetadataSite(metadataSites, filePath);
            return;
        }
        if (!TryNormalize(m, candidates, out var planOp)) return;
        Record(siteFiles, planOp, filePath);

        if (MemberDesugarSyntaxFacts.IsInsideExpressionTree(node, model)
            && !MatchesSet(options.ForceQualifiedNames, planOp))
        {
            MarkFailure(siteFailures, planOp,
                MemberDesugarDiagnosticCode.OperatorExpressionTreeSite,
                OperatorDiagnostics.ExpressionTree(planOp.Name));
            return;
        }
        if (conv.Operand.Type is { TypeKind: TypeKind.Dynamic })
        {
            MarkFailure(siteFailures, planOp, MemberDesugarDiagnosticCode.OperatorDynamicSite,
                OperatorDiagnostics.Dynamic(planOp.Name));
            return;
        }

        // 提升转换 S? → T?（公式 ①）；其它 nullable 形态 → 5176
        if (conv.Operand.Type is INamedTypeSymbol
            {
                OriginalDefinition.SpecialType: SpecialType.System_Nullable_T
            } operandN)
        {
            var paramType = planOp.Parameters.FirstOrDefault()?.Type;
            var shapeOk = conv.Type is INamedTypeSymbol
            {
                OriginalDefinition.SpecialType: SpecialType.System_Nullable_T
            } targetN
                && paramType != null
                && SymbolEqualityComparer.Default.Equals(targetN.TypeArguments[0], planOp.ReturnType)
                && SymbolEqualityComparer.Default.Equals(operandN.TypeArguments[0], paramType);
            if (!shapeOk
                || conv.Operand.Syntax is not ExpressionSyntax operandSyntax
                || !OperatorFacts.IsSimpleLiftedOperand(operandSyntax, model))
                MarkFailure(siteFailures, planOp, MemberDesugarDiagnosticCode.UnbuildableLift,
                    OperatorDiagnostics.UnbuildableLift(planOp.Name));
        }
    }

    private static bool LiftedSitesAreBuildable(IOperation op, SemanticModel model)
    {
        foreach (var child in op.DescendantsAndSelf())
        {
            switch (child)
            {
                case IUnaryOperation { IsLifted: true } u
                    when u.Operand.Syntax is not ExpressionSyntax us
                         || !OperatorFacts.IsSimpleLiftedOperand(us, model):
                    return false;
                case IBinaryOperation { IsLifted: true } b
                    when b.LeftOperand.Syntax is not ExpressionSyntax ls
                         || b.RightOperand.Syntax is not ExpressionSyntax rs
                         || !OperatorFacts.IsSimpleLiftedOperand(ls, model)
                         || !OperatorFacts.IsSimpleLiftedOperand(rs, model):
                    return false;
            }
        }
        return true;
    }

    private static bool ContainsDynamicOperand(IOperation op)
        => op.DescendantsAndSelf().Any(d => d.Type is { TypeKind: TypeKind.Dynamic });

    internal static bool IsRewritableUnary(PrefixUnaryExpressionSyntax un)
        => un.OperatorToken.IsKind(SyntaxKind.MinusToken)
           || un.OperatorToken.IsKind(SyntaxKind.PlusToken)
           || un.OperatorToken.IsKind(SyntaxKind.ExclamationToken)
           || un.OperatorToken.IsKind(SyntaxKind.TildeToken);

    internal static bool IsCompoundKind(AssignmentExpressionSyntax a)
        => a.Kind() is SyntaxKind.AddAssignmentExpression or SyntaxKind.SubtractAssignmentExpression
            or SyntaxKind.MultiplyAssignmentExpression or SyntaxKind.DivideAssignmentExpression
            or SyntaxKind.ModuloAssignmentExpression or SyntaxKind.AndAssignmentExpression
            or SyntaxKind.OrAssignmentExpression or SyntaxKind.ExclusiveOrAssignmentExpression
            or SyntaxKind.LeftShiftAssignmentExpression or SyntaxKind.RightShiftAssignmentExpression
            or SyntaxKind.UnsignedRightShiftAssignmentExpression;

    // ---- 小工具（镜像 C11 PropertyRule）----

    /// <summary>确定性排序键：声明处的源 span 起点（元数据-only 为 0）。</summary>
    private static int SpanStart(IMethodSymbol op)
        => op.Locations.FirstOrDefault(l => l.IsInSource)?.SourceSpan.Start ?? 0;

    internal static bool TryNormalize(
        IMethodSymbol m,
        Dictionary<IMethodSymbol, (bool IsConversion, string BaseName)> candidates,
        out IMethodSymbol normalized)
    {
        normalized = m.IsDefinition ? m : m.OriginalDefinition;
        return candidates.ContainsKey(normalized);
    }

    private static void Record(
        Dictionary<IMethodSymbol, HashSet<string>> siteFiles, IMethodSymbol op, string filePath)
    {
        if (!siteFiles.TryGetValue(op, out var set))
            siteFiles[op] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        set.Add(filePath);
    }

    private static void RecordMetadataSite(Dictionary<string, int> metadataSites, string filePath)
    {
        metadataSites.TryGetValue(filePath, out var n);
        metadataSites[filePath] = n + 1;
    }

    private static void MarkFailure(
        Dictionary<IMethodSymbol, (MemberDesugarDiagnosticCode Code, string Reason)> failures,
        IMethodSymbol op, MemberDesugarDiagnosticCode code, string reason)
        => failures.TryAdd(op, (code, reason));

    private static bool MatchesSet(IReadOnlySet<string> names, IMethodSymbol op)
    {
        var type = op.ContainingType?.ToDisplayString();
        return names.Contains($"{type}::{op.Name}")
               || names.Contains($"{type}::conversion({op.ReturnType.ToDisplayString()})")
               || (type != null && names.Contains(type));
    }

    private static IReadOnlyList<string> DeclarationFiles(IMethodSymbol op)
        => op.Locations.Where(l => l.IsInSource)
            .Select(l => l.SourceTree!.FilePath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
}
