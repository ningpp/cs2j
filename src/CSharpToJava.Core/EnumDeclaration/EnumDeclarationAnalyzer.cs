using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace CSharpToJava.Core.EnumDeclaration;

/// <summary>
/// Phase 1 整树盘点（设计 §4.3）：枚举所有源内 enum，求值成员常量（D4），
/// 按 D2 分类，建使用点倒排/按需成员清单/可行性标记（ED020）/序列化盘点（ED040）。
/// 分类纯函数集中在 <see cref="Site"/>，供 Analyzer 与 EnumUseSiteRewriter 共用，
/// 保证「盘点判不可行」与「重写落地」同一口径。
/// </summary>
internal sealed class EnumDeclarationAnalyzer
{
    private readonly Compilation _compilation;
    private readonly EnumDeclarationOptions _options;

    public EnumDeclarationAnalyzer(Compilation compilation, EnumDeclarationOptions options)
    {
        _compilation = compilation;
        _options = options;
    }

    public EnumAnalysis Analyze()
    {
        var analysis = new EnumAnalysis { Compilation = _compilation };

        // 1) 枚举所有源内 enum，建 plan 骨架 + 成员常量。
        foreach (var type in GetAllTypes(_compilation.GlobalNamespace))
        {
            if (type.TypeKind != TypeKind.Enum) continue;
            if (!type.Locations.Any(l => l.IsInSource)) continue;
            if (type.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax()
                is not EnumDeclarationSyntax enumDecl)
            {
                continue;
            }

            var underlying = type.EnumUnderlyingType
                ?? _compilation.GetSpecialType(SpecialType.System_Int32);
            var qn = type.ToDisplayString();
            var plan = new EnumDeclarationPlan
            {
                Id = qn,
                EnumShortName = type.Name,
                EnumQualifiedName = qn,
                Symbol = type,
                Declaration = enumDecl,
                Underlying = underlying.SpecialType,
                UnderlyingSymbol = underlying,
                IsFlags = EnumDeclarationSyntaxFacts.IsFlagsSymbol(type)
                    || EnumDeclarationSyntaxFacts.IsFlagsSyntax(enumDecl),
                IsNonInt = underlying.SpecialType != SpecialType.System_Int32,
            };

            var anyExplicit = false;
            var seenBits = new HashSet<ulong>();
            var anyDuplicate = false;
            var constantFailure = false;

            foreach (var field in type.GetMembers().OfType<IFieldSymbol>())
            {
                if (field.IsImplicitlyDeclared) continue;
                anyExplicit |= field.HasConstantValue
                    && enumDecl.Members.Any(m => m.Identifier.Text == field.Name
                        && m.EqualsValue != null);
                if (!EnumDeclarationSyntaxFacts.TryGetConstantValue(field, out var value))
                {
                    constantFailure = true;
                    analysis.ConstantDiagnostics.Add(Diagnostic(
                        EnumDeclarationSeverity.Error, EnumDeclarationDiagnosticCode.ED010,
                        $"enum '{qn}' member '{field.Name}' has no compile-time constant value; " +
                        "the enum cannot be normalized (D4).",
                        field.Locations.FirstOrDefault(l => l.IsInSource), qn));
                    continue;
                }
                var bits = EnumDeclarationSyntaxFacts.ToUInt64(value, underlying.SpecialType);
                if (!seenBits.Add(bits)) anyDuplicate = true;
                plan.Members.Add(new EnumMemberInfo(
                    field, field.Name, value, bits,
                    enumDecl.Members.FirstOrDefault(m => m.Identifier.Text == field.Name)
                        ?.EqualsValue != null,
                    field.Locations.FirstOrDefault(l => l.IsInSource)!));
            }

            plan.HasExplicitValue = anyExplicit;
            plan.HasDuplicateValue = anyDuplicate;

            var loc = type.Locations.First(l => l.IsInSource);
            plan.TouchedFiles.Add(loc.SourceTree!.FilePath);
            plan.Touches.Add(new EnumPlanTouch(loc.SourceTree!.FilePath, type.Name, null));

            if (constantFailure)
            {
                plan.Outcome = EnumPlanOutcome.Infeasible;
                plan.FailCode = EnumDeclarationDiagnosticCode.ED010;
                plan.FailReason = "one or more enum members have no compile-time constant value.";
                plan.FailLocation = loc;
            }
            else
            {
                // D16：固定签名方法（Equals/GetHashCode/ToString override）与同名字段在同一
                // 声明空间不能共存；私有/可改名成员用序号消解，这三个不能改名 → ED020。
                var fixedCollision = plan.Members
                    .FirstOrDefault(m => m.Name is "Equals" or "GetHashCode" or "ToString");
                if (fixedCollision != null)
                {
                    plan.Outcome = EnumPlanOutcome.Infeasible;
                    plan.FailCode = EnumDeclarationDiagnosticCode.ED020;
                    plan.FailReason = $"enum member '{fixedCollision.Name}' collides with a required " +
                        "fixed-signature generated method (Equals/GetHashCode/ToString).";
                    plan.FailLocation = fixedCollision.Location;
                    plan.InfeasibleSites.Add(new EnumInfeasibleSite(
                        fixedCollision.Location, plan.FailReason));
                }
                else if (!EnumPlanBuilder2.IsProblem(
                    plan.IsFlags, underlying.SpecialType, anyExplicit, anyDuplicate))
                {
                    // D2 ProblemOnly：普通顺序 int enum 不建计划（不出现在 BySymbol 中）。
                    continue;
                }
            }

            // D10：类型级序列化名字契约盘点（ED040，不阻断）。
            var declModel = _compilation.GetSemanticModel(loc.SourceTree!);
            ScanNameContractAttributes(plan, type, enumDecl, declModel);

            analysis.Plans.Add(plan);
            analysis.BySymbol[type] = plan;
        }

        // 2) 逐语法树做使用点分类。
        foreach (var tree in _compilation.SyntaxTrees)
        {
            if (tree.FilePath == "__cs2j_global_usings.g.cs") continue;
            var model = _compilation.GetSemanticModel(tree);
            var root = tree.GetRoot();
            var walker = new UseSiteWalker(model, analysis, _options);
            walker.Visit(root);
        }

        // 3) 单文件模式跨文件判定（ED090）。
        if (_options.SingleFileMode)
        {
            foreach (var plan in analysis.Plans)
            {
                if (plan.Outcome != EnumPlanOutcome.Converted) continue;
                if (plan.TouchedFiles.Count > 1)
                {
                    plan.Outcome = EnumPlanOutcome.Infeasible;
                    plan.FailCode = EnumDeclarationDiagnosticCode.ED090;
                    plan.FailReason = "enum rewrite is not self-contained in single-file mode.";
                    plan.FailLocation = plan.Symbol.Locations.FirstOrDefault(l => l.IsInSource);
                }
            }
        }

        analysis.Plans.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));
        return analysis;
    }

    private static void ScanNameContractAttributes(
        EnumDeclarationPlan plan, INamedTypeSymbol type, EnumDeclarationSyntax enumDecl,
        SemanticModel declModel)
    {
        foreach (var attr in type.GetAttributes())
        {
            if (EnumDeclarationSyntaxFacts.IsNameContractAttribute(attr))
            {
                plan.NameContractSites.Add(new EnumNameContractSite(
                    AttributeLocation(attr)
                        ?? type.Locations.First(l => l.IsInSource),
                    $"enum '{plan.EnumQualifiedName}' carries name-contract attribute " +
                    $"'{attr.AttributeClass?.Name}'; on-by-name wire semantics are not auto-preserved."));
            }
        }
        foreach (var field in type.GetMembers().OfType<IFieldSymbol>())
        {
            if (field.IsImplicitlyDeclared) continue;
            foreach (var attr in field.GetAttributes())
            {
                if (EnumDeclarationSyntaxFacts.IsNameContractAttribute(attr))
                {
                    plan.NameContractSites.Add(new EnumNameContractSite(
                        AttributeLocation(attr)
                            ?? field.Locations.First(l => l.IsInSource),
                        $"enum member '{plan.EnumQualifiedName}.{field.Name}' carries name-contract " +
                        $"attribute '{attr.AttributeClass?.Name}'."));
                }
            }
        }
        // 语法层 JsonStringEnumConverter（符号层实参类型解析可能受 using 影响）。
        foreach (var attr in enumDecl.AttributeLists.SelectMany(al => al.Attributes))
        {
            var attrName = attr.Name.ToString();
            if (!attrName.Contains("JsonConverter")) continue;
            var isString = EnumDeclarationSyntaxFacts.IsJsonStringEnumConverterSyntax(
                attr, declModel);
            var converterName = attr.ArgumentList?.Arguments
                .Select(a => a.Expression)
                .OfType<TypeOfExpressionSyntax>()
                .Select(t => declModel.GetTypeInfo(t.Type).Type?.Name)
                .FirstOrDefault(n => n != null);
            var target = converterName == null ? string.Empty : $" targeting '{converterName}'";
            plan.NameContractSites.Add(new EnumNameContractSite(
                type.Locations.First(l => l.IsInSource),
                $"enum '{plan.EnumQualifiedName}' carries name-contract attribute " +
                $"'{attrName}'{target}; " +
                (isString ? "string-enum conversion is not auto-preserved."
                    : "enum JSON conversion may change wire format.")));
        }
    }

    private static Location? AttributeLocation(AttributeData attr)
    {
        var sr = attr.ApplicationSyntaxReference;
        return sr == null ? null : Location.Create(sr.SyntaxTree, sr.Span);
    }

    private static IEnumerable<INamedTypeSymbol> GetAllTypes(INamespaceSymbol ns)
    {
        foreach (var member in ns.GetMembers())
        {
            if (member is INamedTypeSymbol type)
            {
                yield return type;
                foreach (var nested in GetAllTypes(type)) yield return nested;
            }
            else if (member is INamespaceSymbol child)
            {
                foreach (var t in GetAllTypes(child)) yield return t;
            }
        }
    }

    private static IEnumerable<INamedTypeSymbol> GetAllTypes(INamedTypeSymbol type)
    {
        foreach (var nested in type.GetTypeMembers())
        {
            yield return nested;
            foreach (var deeper in GetAllTypes(nested)) yield return deeper;
        }
    }

    internal static EnumDeclarationDiagnostic Diagnostic(
        EnumDeclarationSeverity severity,
        EnumDeclarationDiagnosticCode code,
        string message,
        Location? location,
        string? enumName)
    {
        var file = location?.SourceTree?.FilePath;
        var span = location?.GetLineSpan().StartLinePosition;
        return new EnumDeclarationDiagnostic(
            severity, code, message, file,
            (span?.Line ?? 0) + 1, (span?.Character ?? 0) + 1, enumName);
    }

    // ────────────────────────────────────────────────────────────────────────
    // 使用点分类 walker
    // ────────────────────────────────────────────────────────────────────────

    private sealed class UseSiteWalker : CSharpSyntaxWalker
    {
        private readonly SemanticModel _model;
        private readonly EnumAnalysis _analysis;
        private readonly EnumDeclarationOptions _options;

        public UseSiteWalker(SemanticModel model, EnumAnalysis analysis, EnumDeclarationOptions options)
        {
            _model = model;
            _analysis = analysis;
            _options = options;
        }

        private EnumDeclarationPlan? FeasiblePlan(ITypeSymbol? type)
        {
            var inner = EnumDeclarationSyntaxFacts.UnwrapNullableEnum(type);
            if (inner == null) return null;
            if (!_analysis.BySymbol.TryGetValue(inner, out var plan)) return null;
            return plan.Outcome == EnumPlanOutcome.Converted ? plan : null;
        }

        private void Touch(EnumDeclarationPlan plan, SyntaxNode node)
        {
            var file = node.SyntaxTree?.FilePath;
            if (file != null) plan.TouchedFiles.Add(file);
        }

        private void Infeasible(
            EnumDeclarationPlan plan, SyntaxNode node, string reason, Location? location = null)
        {
            var loc = location ?? node.GetLocation();
            if (plan.InfeasibleSites.All(s => s.Location != loc))
                plan.InfeasibleSites.Add(new EnumInfeasibleSite(loc, reason));
            // 任一不可表示站点 → 整 plan 回滚为真 enum（ED020）；ED010/ED090 已预置码时保留。
            // Deferred（让位给 numeric-boundary）遇到真正不可表示站点时升级为 Infeasible。
            if (plan.Outcome != EnumPlanOutcome.Infeasible)
            {
                plan.Outcome = EnumPlanOutcome.Infeasible;
                plan.FailCode = EnumDeclarationDiagnosticCode.ED020;
                plan.FailReason = reason;
                plan.FailLocation = loc;
            }
            Touch(plan, node);
        }

        /// <summary>
        /// ++/--、enum +/- 与复合赋值站点：保真 enum 且不发 ED020——链中更晚的
        /// numeric-boundary 族（C15/C17）会把这些站点降为底层域 cast。
        /// </summary>
        private static void Defer(EnumDeclarationPlan plan, SyntaxNode node, string reason)
        {
            if (plan.Outcome == EnumPlanOutcome.Converted)
            {
                plan.Outcome = EnumPlanOutcome.Deferred;
                plan.FailReason = reason;
                plan.FailLocation = node.GetLocation();
            }
        }

        private static bool IsInAttributeArgument(SyntaxNode node)
            => node.Ancestors().OfType<AttributeArgumentSyntax>().Any();

        private static bool IsInNameOf(SyntaxNode node)
        {
            var invocation = node.Ancestors().OfType<InvocationExpressionSyntax>().FirstOrDefault();
            return invocation != null
                && invocation.Expression is IdentifierNameSyntax n
                && n.Identifier.IsKind(SyntaxKind.NameOfKeyword);
        }

        public override void VisitCastExpression(CastExpressionSyntax node)
        {
            base.VisitCastExpression(node);
            var target = _model.GetTypeInfo(node.Type).Type;
            var source = _model.GetTypeInfo(node.Expression).Type;
            var targetPlan = FeasiblePlan(target);
            var sourcePlan = FeasiblePlan(source);

            // 源含 dynamic → 重写改变运行时绑定（R7）。
            if (source?.TypeKind == TypeKind.Dynamic && targetPlan != null)
            {
                Infeasible(targetPlan, node,
                    "dynamic to enum cast cannot be rewritten to a static FromValue call.");
                return;
            }

            // (U?)e：E? → 底层数值族的可空提升转换（如 (int?)nullableEnum）。
            // class 形态满足不了 Nullable<T> 的 struct 约束；该站点由链中更晚的
            // numeric-boundary C22 lifted 转换规则显式化 → 静默保真 enum。
            if (source is INamedTypeSymbol liftedSource
                && liftedSource.ConstructedFrom.SpecialType == SpecialType.System_Nullable_T
                && liftedSource.TypeArguments[0] is INamedTypeSymbol { TypeKind: TypeKind.Enum } liftedSourceEnum
                && target is INamedTypeSymbol liftedTarget
                && liftedTarget.ConstructedFrom.SpecialType == SpecialType.System_Nullable_T
                && IsEnumUnderlyingNumeric(liftedTarget.TypeArguments[0].SpecialType)
                && FeasiblePlan(liftedSourceEnum) is { } liftedEnumPlan)
            {
                Defer(liftedEnumPlan, node,
                    "lifted nullable enum-to-numeric cast lowered later by numeric-boundary.");
                return;
            }

            // (IFormattable)/(IConvertible) 作用于被改写 enum → 无法等价（§6 ED020）。
            if (sourcePlan != null && target is INamedTypeSymbol ts
                && ts.ToDisplayString() is "System.IFormattable" or "System.IConvertible")
            {
                Infeasible(sourcePlan, node,
                    $"cast to '{ts.ToDisplayString()}' has no equivalent on the generated enum-like class.");
                return;
            }

            // (System.Enum)/(System.ValueType) 作用于被改写 enum：class 形态不继承它们。
            if (sourcePlan != null && target is INamedTypeSymbol te
                && te.ToDisplayString() is "System.Enum" or "System.ValueType")
            {
                Infeasible(sourcePlan, node,
                    $"cast to '{te.ToDisplayString()}' is not supported on the generated enum-like class.");
                return;
            }

            if (targetPlan != null)
            {
                var targetIsNullable = EnumDeclarationSyntaxFacts.IsNullableOfEnum(target, targetPlan.Symbol);

                // (E)(E?) 直接解包：class 形态下源是可空引用，null 解包应抛异常而
                // 非默认值，M1 不承诺该站点（GetValuedefault 合并语义不同）→ ED020。
                if (!targetIsNullable
                    && EnumDeclarationSyntaxFacts.IsNullableOfEnum(source, targetPlan.Symbol))
                {
                    Infeasible(targetPlan, node,
                        "direct unwrap cast from a nullable enum to the enum-like class is " +
                        "not supported; handle null explicitly before the cast.");
                    return;
                }

                if (sourcePlan != null)
                {
                    // 双问题 enum 互转（含同一 plan 的冗余转换，重写幂等）。
                    Touch(targetPlan, node);
                    if (!ReferenceEquals(sourcePlan, targetPlan)) Touch(sourcePlan, node);
                    return;
                }

                var sourceEnum = EnumDeclarationSyntaxFacts.UnwrapNullableEnum(source);
                if (sourceEnum != null)
                {
                    // 到未改写普通 enum 的跨形态：class 形态走整数桥 (int)(object)。
                    if (targetIsNullable)
                    {
                        Infeasible(targetPlan, node,
                            "cross-form cast into a nullable enum-like is not supported.");
                        return;
                    }
                    targetPlan.CrossFormCastCount++;
                    Touch(targetPlan, node);
                    return;
                }

                if (source != null && source.SpecialType is SpecialType.System_Object
                    or SpecialType.System_Enum or SpecialType.System_ValueType)
                {
                    // 拆箱 (E)(object)：引用转换，class 形态合法，零改写（记录触碰供 residual 扫描）。
                    Touch(targetPlan, node);
                    return;
                }

                // 整数 → E：FromValue（D14）。可空目标 + 可空整数（E04 lifted）暂不支持。
                if (targetIsNullable
                    && source is INamedTypeSymbol sn
                    && sn.ConstructedFrom.SpecialType == SpecialType.System_Nullable_T)
                {
                    Infeasible(targetPlan, node,
                        "lifted nullable integer-to-enum cast produced by the enum bitwise pass is " +
                        "not supported for the enum-like class representation.");
                    return;
                }
                Touch(targetPlan, node);
                return;
            }

            if (sourcePlan != null)
            {
                // E → 整数（含 E04 产物 (U)e 再消解）；可空整数目标走 lifted 形态。
                Touch(sourcePlan, node);
                var targetEnum = EnumDeclarationSyntaxFacts.UnwrapNullableEnum(target);
                if (targetEnum != null)
                {
                    // 被改写 enum → 普通 enum 的跨形态。
                    sourcePlan.CrossFormCastCount++;
                }
                return;
            }

            // 普通 enum → 普通整数的 cast 不处理（E04 域）。
        }

        public override void VisitDefaultExpression(DefaultExpressionSyntax node)
        {
            base.VisitDefaultExpression(node);
            var plan = FeasiblePlan(_model.GetTypeInfo(node).Type)
                ?? FeasiblePlan(_model.GetTypeInfo(node).ConvertedType);
            if (plan != null) Touch(plan, node);
        }

        public override void VisitLiteralExpression(LiteralExpressionSyntax node)
        {
            base.VisitLiteralExpression(node);
            if (!node.IsKind(SyntaxKind.DefaultLiteralExpression)) return;
            var plan = FeasiblePlan(_model.GetTypeInfo(node).ConvertedType)
                ?? FeasiblePlan(_model.GetTypeInfo(node).Type);
            if (plan != null) Touch(plan, node);
        }

        public override void VisitObjectCreationExpression(ObjectCreationExpressionSyntax node)
        {
            base.VisitObjectCreationExpression(node);
            var plan = FeasiblePlan(_model.GetTypeInfo(node).Type);
            if (plan != null && node.ArgumentList?.Arguments.Count == 0)
                Touch(plan, node);
        }

        public override void VisitSwitchStatement(SwitchStatementSyntax node)
        {
            base.VisitSwitchStatement(node);
            var plan = FeasiblePlan(_model.GetTypeInfo(node.Expression).Type)
                ?? FeasiblePlan(_model.GetTypeInfo(node.Expression).ConvertedType);
            if (plan == null) return;

            Touch(plan, node);
            // goto case / goto default：本 pass 不处理控制流跳转（§4.5.4）。
            foreach (var gotoStmt in node.DescendantNodes().OfType<GotoStatementSyntax>())
            {
                if (gotoStmt.IsKind(SyntaxKind.GotoCaseStatement)
                    || gotoStmt.IsKind(SyntaxKind.GotoDefaultStatement))
                {
                    Infeasible(plan, gotoStmt,
                        "switch with 'goto case'/'goto default' on a rewritten enum cannot be lowered "
                        + "by this pass; run a goto-elimination pass first.");
                }
            }

            // 声明/属性/位置模式作用于被改写 enum → ED020。
            foreach (var label in node.DescendantNodes().OfType<SwitchLabelSyntax>())
            {
                CheckPattern(label, plan);
            }
        }

        public override void VisitSwitchExpression(SwitchExpressionSyntax node)
        {
            base.VisitSwitchExpression(node);
            var plan = FeasiblePlan(_model.GetTypeInfo(node.GoverningExpression).Type)
                ?? FeasiblePlan(_model.GetTypeInfo(node.GoverningExpression).ConvertedType);
            if (plan == null) return;
            Touch(plan, node);
            foreach (var arm in node.Arms)
                CheckPattern(arm, plan);
        }

        private void CheckPattern(SyntaxNode container, EnumDeclarationPlan plan)
        {
            foreach (var pattern in container.DescendantNodes()
                         .OfType<PatternSyntax>()
                         .Where(p => !p.Ancestors().OfType<PatternSyntax>()
                             .Any(a => a.Parent == p.Parent && a != p && a.Span.Contains(p.Span))))
            {
                switch (pattern)
                {
                    case ConstantPatternSyntax:
                        break;
                    case BinaryPatternSyntax:
                        break;
                    case ParenthesizedPatternSyntax:
                        break;
                    case DiscardPatternSyntax:
                    case VarPatternSyntax:
                        break;
                    case DeclarationPatternSyntax dp:
                        if (FeasiblePlan(_model.GetTypeInfo(dp.Type).Type) != null
                            || FeasiblePlan(_model.GetTypeInfo(dp.Type).ConvertedType) != null)
                        {
                            Infeasible(plan, dp,
                                "declaration pattern on a rewritten enum cannot be represented; "
                                + "lower the pattern in an earlier pattern-desugaring pass.");
                        }
                        break;
                    case RecursivePatternSyntax rp when rp.Type != null:
                        if (FeasiblePlan(_model.GetTypeInfo(rp.Type).Type) != null)
                        {
                            Infeasible(plan, rp,
                                "property/positional pattern on a rewritten enum cannot be represented.");
                        }
                        break;
                }
            }
        }

        public override void VisitIsPatternExpression(IsPatternExpressionSyntax node)
        {
            base.VisitIsPatternExpression(node);
            var plan = FeasiblePlan(_model.GetTypeInfo(node.Expression).Type);
            if (plan != null)
            {
                Touch(plan, node);
                CheckPattern(node, plan);
            }
        }

        public override void VisitConstantPattern(ConstantPatternSyntax node)
        {
            base.VisitConstantPattern(node);
            // is E.M（含 or 模式子节点）/ switch 常量标签：零改写引用、触碰文件即可，
            // 降级在父 switch/is 重写时统一处理。
            if (_model.GetSymbolInfo(node.Expression).Symbol is IFieldSymbol f
                && _analysis.BySymbol.TryGetValue(f.ContainingType, out var plan)
                && plan.Outcome == EnumPlanOutcome.Converted)
            {
                Touch(plan, node);
            }
        }

        public override void VisitInvocationExpression(InvocationExpressionSyntax node)
        {
            base.VisitInvocationExpression(node);
            if (_model.GetSymbolInfo(node).Symbol is not IMethodSymbol method) return;

            // System.Enum 静态 API（D8）。
            if (method.ContainingType is { } ct
                && ct.ToDisplayString() == "System.Enum"
                && method.IsStatic)
            {
                ClassifyEnumApi(node, method);
                return;
            }

            // x.HasFlag(f) 残余站点（D6/ED013）。
            if (method.Name == "HasFlag" && node.Expression is MemberAccessExpressionSyntax ma)
            {
                var receiverPlan = FeasiblePlan(_model.GetTypeInfo(ma.Expression).Type);
                if (receiverPlan != null)
                {
                    var receiverType = _model.GetTypeInfo(ma.Expression).Type;
                    if (receiverType is INamedTypeSymbol nts
                        && nts.ConstructedFrom.SpecialType == SpecialType.System_Nullable_T)
                    {
                        receiverPlan.ResidualE04Count++;
                    }
                    receiverPlan.Demand(EnumOptionalMembers.HasFlag);
                    Touch(receiverPlan, node);
                }
                return;
            }

            // x.ToString(format) 实例格式化（§4.5.6）。
            if (method.Name == nameof(object.ToString) && node.Expression is MemberAccessExpressionSyntax ta
                && node.ArgumentList.Arguments.Count == 1)
            {
                var p = FeasiblePlan(_model.GetTypeInfo(ta.Expression).Type);
                if (p != null)
                {
                    p.Demand(EnumOptionalMembers.ToStringFormat);
                    Touch(p, node);
                }
                return;
            }

            // GetValueOrDefault() 作用于 E?（class 形态下降为 null 合并）。
            if (method.Name == "GetValueOrDefault"
                && method.ContainingType.ConstructedFrom.SpecialType
                    == SpecialType.System_Nullable_T)
            {
                var p = FeasiblePlan(method.ReceiverType ?? method.ContainingType.TypeArguments.FirstOrDefault());
                if (p != null) Touch(p, node);
                return;
            }

            // 反射查 FlagsAttribute（R6）：Attribute.GetCustomAttribute(s)(typeof(E), typeof(FlagsAttribute))。
            if (method.Name is "GetCustomAttribute" or "GetCustomAttributes"
                && node.ArgumentList.Arguments.Count >= 2)
            {
                var args = node.ArgumentList.Arguments;
                var typeArgPlan = PlanFromTypeOfArg(args[0].Expression);
                if (typeArgPlan != null
                    && args[1].Expression is TypeOfExpressionSyntax t2
                    && _model.GetTypeInfo(t2.Type).Type?.Name is "FlagsAttribute" or "Type")
                {
                    typeArgPlan.NameContractSites.Add(new EnumNameContractSite(
                        node.GetLocation(),
                        $"reflection look-up of FlagsAttribute on '{typeArgPlan.EnumQualifiedName}' " +
                        "cannot observe the attribute after normalization."));
                    Touch(typeArgPlan, node);
                }
            }

            // 泛型实参绑定到 : struct, Enum / : Enum 约束（§4.3 可行性）。
            if (method.TypeArguments.Length > 0)
                CheckGenericConstraints(node, method);
        }

        private void CheckGenericConstraints(SyntaxNode node, IMethodSymbol method)
        {
            for (var i = 0; i < method.TypeArguments.Length && i < method.TypeParameters.Length; i++)
            {
                var plan = FeasiblePlan(method.TypeArguments[i]);
                if (plan == null) continue;
                var tp = method.TypeParameters[i];
                var hasStructConstraint = tp.HasValueTypeConstraint;
                var hasEnumConstraint = tp.ConstraintTypes.Any(c =>
                    c.ToDisplayString() is "System.Enum" or "System.ValueType");
                if (hasStructConstraint || hasEnumConstraint)
                {
                    Infeasible(plan, node,
                        $"generic method '{method.ToDisplayString()}' constrains its type parameter "
                        + (hasStructConstraint ? "to 'struct'" : "to 'System.Enum'")
                        + "; the generated enum-like class cannot satisfy the constraint.");
                }
            }
        }

        private EnumDeclarationPlan? PlanFromTypeOfArg(ExpressionSyntax expression)
        {
            if (expression is not TypeOfExpressionSyntax tos) return null;
            return FeasiblePlan(_model.GetTypeInfo(tos.Type).Type);
        }

        private void ClassifyEnumApi(InvocationExpressionSyntax node, IMethodSymbol method)
        {
            var args = node.ArgumentList.Arguments;
            EnumDeclarationPlan? plan = null;

            // 泛型 API：Enum.GetValues<E>()/GetNames<E>()/TryParse<TEnum>(...)。
            if (method.TypeArguments.Length > 0)
                plan = FeasiblePlan(method.TypeArguments[0]);

            if (plan == null && args.Count > 0)
            {
                plan = PlanFromTypeOfArg(args[0].Expression);
                if (plan == null
                    && EnumDeclarationSyntaxFacts.UnwrapNullableEnum(
                        _model.GetTypeInfo(args[0].Expression).Type) is { } maybePlan)
                {
                    plan = _analysis.BySymbol.TryGetValue(maybePlan, out var p)
                        && p.Outcome == EnumPlanOutcome.Converted ? p : null;
                }
                if (plan == null)
                {
                    // 实参不是常量 typeof(被改写 enum)（运行时 Type）→ ED020。
                    var argPlan = EnumDeclarationSyntaxFacts.UnwrapNullableEnum(
                        _model.GetTypeInfo(args[0].Expression).Type);
                    if (argPlan != null && _analysis.BySymbol.TryGetValue(argPlan, out var raw)
                        && raw.Outcome == EnumPlanOutcome.Converted)
                    {
                        plan = raw;
                    }
                    else
                    {
                        // 与任何候选 enum 无关的 System.Enum 调用不处理。
                        return;
                    }
                }
                if (args[0].Expression is not TypeOfExpressionSyntax
                    && method.TypeArguments.Length == 0)
                {
                    Infeasible(plan, args[0],
                        $"System.Enum.{method.Name} with a runtime Type argument cannot be " +
                        "redirected to the generated enum-like members; only constant typeof(E) is supported.");
                    return;
                }
            }

            if (plan == null) return;
            Touch(plan, node);

            switch (method.Name)
            {
                case "GetValues":
                    plan.Demand(EnumOptionalMembers.GetValues);
                    break;
                case "GetNames":
                    plan.Demand(EnumOptionalMembers.GetNames);
                    break;
                case "GetName":
                    plan.Demand(EnumOptionalMembers.GetName);
                    break;
                case "Parse":
                    plan.Demand(EnumOptionalMembers.Parse);
                    break;
                case "TryParse":
                    plan.Demand(EnumOptionalMembers.TryParse);
                    break;
                case "IsDefined":
                    plan.Demand(args.Count >= 2
                        && _model.GetTypeInfo(args[1].Expression).Type?.SpecialType
                            == SpecialType.System_String
                        ? EnumOptionalMembers.IsDefinedName
                        : EnumOptionalMembers.IsDefinedValue);
                    break;
                case "Format":
                    plan.Demand(EnumOptionalMembers.Format);
                    plan.Demand(EnumOptionalMembers.ToStringFormat);
                    break;
                case "ToObject":
                    plan.Demand(EnumOptionalMembers.ToObject);
                    break;
                case "GetUnderlyingType":
                    // Enum.GetUnderlyingType(typeof(E)) → typeof(U)，无需按需成员。
                    break;
            }
        }

        public override void VisitMemberAccessExpression(MemberAccessExpressionSyntax node)
        {
            base.VisitMemberAccessExpression(node);
            var symbol = _model.GetSymbolInfo(node).Symbol;

            // 成员引用 E.M（零改写；只建触碰 + 常量上下文处置）。
            if (symbol is IFieldSymbol field && field.HasConstantValue
                && _analysis.BySymbol.TryGetValue(field.ContainingType, out var memberPlan)
                && memberPlan.Outcome == EnumPlanOutcome.Converted
                && !IsInNameOf(node))
            {
                Touch(memberPlan, node);
                if (IsInAttributeArgument(node))
                {
                    Infeasible(memberPlan, node,
                        "enum member used as an attribute argument requires a compile-time constant; "
                        + "the generated static readonly field cannot serve in that position.");
                    return;
                }
                ClassifyConstantContext(memberPlan, node);
            }

            // E? 实例成员 .HasValue / .Value（Nullable<T>）。
            if (symbol is IPropertySymbol prop
                && prop.ContainingType.ConstructedFrom.SpecialType
                    == SpecialType.System_Nullable_T)
            {
                var inner = prop.ContainingType.TypeArguments.FirstOrDefault() as INamedTypeSymbol;
                if (inner != null && _analysis.BySymbol.TryGetValue(inner, out var np)
                    && np.Outcome == EnumPlanOutcome.Converted)
                {
                    Touch(np, node);
                }
            }
        }

        public override void VisitIdentifierName(IdentifierNameSyntax node)
        {
            base.VisitIdentifierName(node);
            var symbol = _model.GetSymbolInfo(node).Symbol;
            if (symbol is IFieldSymbol field && field.HasConstantValue
                && _analysis.BySymbol.TryGetValue(field.ContainingType, out var memberPlan)
                && memberPlan.Outcome == EnumPlanOutcome.Converted
                && !IsInNameOf(node))
            {
                // using static 下的裸 M。
                Touch(memberPlan, node);
                if (IsInAttributeArgument(node))
                {
                    Infeasible(memberPlan, node,
                        "enum member used as an attribute argument requires a compile-time constant.");
                    return;
                }
                ClassifyConstantContext(memberPlan, node);
            }
        }

        private void ClassifyConstantContext(EnumDeclarationPlan plan, SyntaxNode node)
        {
            // const 字段/局部 → 降级 static readonly/普通变量（可表示，§4.5.1）。
            foreach (var ancestor in node.Ancestors())
            {
                switch (ancestor)
                {
                    case VariableDeclaratorSyntax vd when vd.Initializer != null
                        && vd.Initializer.Value.Span.Contains(node.Span):
                        if (ancestor.Parent?.Parent is FieldDeclarationSyntax fd
                            && fd.Modifiers.Any(SyntaxKind.ConstKeyword))
                        {
                            Touch(plan, fd);
                            return;
                        }
                        if (vd.Parent is VariableDeclarationSyntax vds
                            && vds.Parent is LocalDeclarationStatementSyntax ld
                            && ld.Modifiers.Any(SyntaxKind.ConstKeyword))
                        {
                            Touch(plan, ld);
                            return;
                        }
                        break;
                    case ParameterSyntax p when p.Default?.Value.Span.Contains(node.Span) == true:
                        Infeasible(plan, node,
                            "enum member used as a default parameter value requires a compile-time "
                            + "constant; the generated static readonly field cannot serve there.");
                        return;
                }
            }
        }

        public override void VisitBinaryExpression(BinaryExpressionSyntax node)
        {
            base.VisitBinaryExpression(node);

            // 经典 is 表达式 p is E.M（语法树是 IsExpression，不是 IsPatternExpression）。
            // 右侧解析为 enum 成员常量 → 值比较改写；右侧是类型（p is E / p is int）→
            // class 形态引用类型测试合法，零改写。
            if (node.IsKind(SyntaxKind.IsExpression))
            {
                var isPlan = FeasiblePlan(_model.GetTypeInfo(node.Left).Type)
                    ?? FeasiblePlan(_model.GetTypeInfo(node.Left).ConvertedType);
                if (isPlan != null
                    && _model.GetSymbolInfo(node.Right).Symbol is IFieldSymbol rf
                    && SymbolEqualityComparer.Default.Equals(rf.ContainingType, isPlan.Symbol))
                {
                    Touch(isPlan, node);
                }
                return;
            }

            // E +/- E、E +/- n、n +/- E 在真 enum 上合法，但 enum-like class
            // 形态没有这些运算符（C# 的 enum ++/--/算术由链中更晚的
            // numeric-boundary 族降低为底层域 cast）→ ED020 保 enum。
            // 字符串拼接（另一操作数为 string）由 use-site rewriter 的
            // AddExpression 路径收口，不在此列。
            if (node.IsKind(SyntaxKind.AddExpression)
                || node.IsKind(SyntaxKind.SubtractExpression))
            {
                var leftType = _model.GetTypeInfo(node.Left);
                var rightType = _model.GetTypeInfo(node.Right);
                var isStringConcat = leftType.ConvertedType?.SpecialType == SpecialType.System_String
                    || rightType.ConvertedType?.SpecialType == SpecialType.System_String
                    || leftType.Type?.SpecialType == SpecialType.System_String
                    || rightType.Type?.SpecialType == SpecialType.System_String;
                if (!isStringConcat)
                {
                    var arithmeticPlan = FeasiblePlan(leftType.Type)
                        ?? FeasiblePlan(leftType.ConvertedType)
                        ?? FeasiblePlan(rightType.Type)
                        ?? FeasiblePlan(rightType.ConvertedType);
                    if (arithmeticPlan != null)
                    {
                        Defer(arithmeticPlan, node,
                            "enum +/- arithmetic lowered later by numeric-boundary.");
                    }
                }

                return;
            }

            if (!node.IsKind(SyntaxKind.LogicalAndExpression)
                && !node.IsKind(SyntaxKind.LogicalOrExpression)
                && node.Kind() is not (SyntaxKind.EqualsExpression
                    or SyntaxKind.NotEqualsExpression
                    or SyntaxKind.LessThanExpression
                    or SyntaxKind.LessThanOrEqualExpression
                    or SyntaxKind.GreaterThanExpression
                    or SyntaxKind.GreaterThanOrEqualExpression))
            {
                return;
            }

            var left = FeasiblePlan(_model.GetTypeInfo(node.Left).Type)
                ?? FeasiblePlan(_model.GetTypeInfo(node.Left).ConvertedType);
            var right = FeasiblePlan(_model.GetTypeInfo(node.Right).Type)
                ?? FeasiblePlan(_model.GetTypeInfo(node.Right).ConvertedType);
            var plan = left ?? right;
            if (plan != null)
            {
                if (node.IsKind(SyntaxKind.EqualsExpression)
                    || node.IsKind(SyntaxKind.NotEqualsExpression)
                    || node.IsKind(SyntaxKind.LessThanExpression)
                    || node.IsKind(SyntaxKind.LessThanOrEqualExpression)
                    || node.IsKind(SyntaxKind.GreaterThanExpression)
                    || node.IsKind(SyntaxKind.GreaterThanOrEqualExpression))
                {
                    plan.Demand(EnumOptionalMembers.CompareTo);
                }
                Touch(plan, node);
            }
        }

        public override void VisitPrefixUnaryExpression(PrefixUnaryExpressionSyntax node)
        {
            base.VisitPrefixUnaryExpression(node);
            if (node.IsKind(SyntaxKind.PreIncrementExpression)
                || node.IsKind(SyntaxKind.PreDecrementExpression))
            {
                MarkEnumMutationDeferred(node.Operand, node,
                    "prefix ++/-- on an enum has no equivalent on the generated enum-like class.");
            }
        }

        public override void VisitPostfixUnaryExpression(PostfixUnaryExpressionSyntax node)
        {
            base.VisitPostfixUnaryExpression(node);
            if (node.IsKind(SyntaxKind.PostIncrementExpression)
                || node.IsKind(SyntaxKind.PostDecrementExpression))
            {
                MarkEnumMutationDeferred(node.Operand, node,
                    "postfix ++/-- on an enum has no equivalent on the generated enum-like class.");
            }
        }

        public override void VisitAssignmentExpression(AssignmentExpressionSyntax node)
        {
            base.VisitAssignmentExpression(node);
            if (node.IsKind(SyntaxKind.AddAssignmentExpression)
                || node.IsKind(SyntaxKind.SubtractAssignmentExpression)
                || node.IsKind(SyntaxKind.AndAssignmentExpression)
                || node.IsKind(SyntaxKind.OrAssignmentExpression)
                || node.IsKind(SyntaxKind.ExclusiveOrAssignmentExpression))
            {
                MarkEnumMutationDeferred(node.Left, node,
                    "compound assignment on an enum has no equivalent on the generated enum-like class.");
            }
        }

        // C# 枚举允许的底层数值族（用于判定 (U?)e 提升转换）。
        private static bool IsEnumUnderlyingNumeric(SpecialType specialType) => specialType
            is SpecialType.System_Byte
            or SpecialType.System_SByte
            or SpecialType.System_Int16
            or SpecialType.System_UInt16
            or SpecialType.System_Int32
            or SpecialType.System_UInt32
            or SpecialType.System_Int64
            or SpecialType.System_UInt64
            or SpecialType.System_IntPtr
            or SpecialType.System_UIntPtr
            or SpecialType.System_Single
            or SpecialType.System_Double
            or SpecialType.System_Decimal
            or SpecialType.System_Char;

        /// <summary>
        /// enum（含 E?）目标的 ++/-- 与复合赋值：保真 enum 但不发 ED020；
        /// numeric-boundary 族（C15/C17）在链中更晚把这些站点降成底层域 cast。
        /// </summary>
        private void MarkEnumMutationDeferred(SyntaxNode site, SyntaxNode node, string reason)
        {
            var typeInfo = _model.GetTypeInfo(site);
            var plan = FeasiblePlan(typeInfo.Type) ?? FeasiblePlan(typeInfo.ConvertedType);
            if (plan == null
                && typeInfo.Type is INamedTypeSymbol
                {
                    OriginalDefinition.SpecialType: SpecialType.System_Nullable_T,
                } nullable
                && nullable.TypeArguments[0] is INamedTypeSymbol inner)
            {
                plan = FeasiblePlan(inner);
            }

            if (plan != null)
            {
                Defer(plan, node, reason);
            }
        }

        public override void VisitInterpolation(InterpolationSyntax node)
        {
            base.VisitInterpolation(node);
            if (node.FormatClause == null) return;
            var plan = FeasiblePlan(_model.GetTypeInfo(node.Expression).Type);
            if (plan != null)
            {
                plan.Demand(EnumOptionalMembers.ToStringFormat);
                Touch(plan, node);
            }
        }

        public override void VisitTypeOfExpression(TypeOfExpressionSyntax node)
        {
            base.VisitTypeOfExpression(node);
            var plan = FeasiblePlan(_model.GetTypeInfo(node.Type).Type);
            if (plan != null) Touch(plan, node);
        }

        public override void VisitGenericName(GenericNameSyntax node)
        {
            base.VisitGenericName(node);
            // 显式 Nullable<E>：class 形态不满足 Nullable<T> 的 struct 约束 → ED020。
            if (node.Identifier.Text == "Nullable"
                && node.TypeArgumentList.Arguments.Count == 1)
            {
                var plan = FeasiblePlan(_model.GetTypeInfo(node.TypeArgumentList.Arguments[0]).Type);
                if (plan != null)
                {
                    Infeasible(plan, node,
                        "explicit Nullable<E> over a rewritten enum is not supported by the "
                        + "enum-like class representation; use the nullable reference form E?.");
                }
            }
        }

        public override void VisitParenthesizedLambdaExpression(ParenthesizedLambdaExpressionSyntax node)
            => CheckExpressionTreeLambda(node);

        public override void VisitSimpleLambdaExpression(SimpleLambdaExpressionSyntax node)
            => CheckExpressionTreeLambda(node);

        private void CheckExpressionTreeLambda(LambdaExpressionSyntax node)
        {
            base.DefaultVisit(node);
            var converted = _model.GetTypeInfo(node).ConvertedType;
            if (converted is not INamedTypeSymbol nt
                || nt.ConstructedFrom.ToDisplayString() != "System.Linq.Expressions.Expression<TDelegate>")
            {
                return;
            }
            foreach (var descendant in node.DescendantNodes())
            {
                ITypeSymbol? type = descendant is ExpressionSyntax es
                    ? (_model.GetTypeInfo(es).Type ?? _model.GetTypeInfo(es).ConvertedType)
                    : null;
                var plan = FeasiblePlan(type);
                if (plan != null)
                {
                    Infeasible(plan, node,
                        "expression tree captures a rewritten enum cast/member; rewriting would "
                        + "change the expression tree shape (R7).");
                    return;
                }
            }
        }
    }
}
