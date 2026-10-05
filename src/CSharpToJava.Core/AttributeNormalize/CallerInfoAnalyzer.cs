using CSharpToJava.Core.Analysis;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace CSharpToJava.Core.AttributeNormalize;

/// <summary>
/// 整树一次的 Caller 分析（design §4.1/§4.3 AN-C1~AN-C10）：
/// 枚举调用/对象创建/索引器操作的隐式默认值实参，按目标符号聚合 plan；
/// 同步跑全树 attribute inventory。
/// </summary>
internal sealed class CallerInfoAnalyzer
{
    private readonly AttributeNormalizeOptions _options;

    private readonly Dictionary<IMethodSymbol, CallerInfoPlan> _plans
        = new(SymbolEqualityComparer.Default);

    private readonly List<AttributeOccurrence> _occurrences = new();

    public CallerInfoAnalyzer(AttributeNormalizeOptions options)
    {
        _options = options;
    }

    public (List<CallerInfoPlan> Plans, AttributeInventoryReport Inventory) Analyze(Compilation compilation)
    {
        ArgumentNullException.ThrowIfNull(compilation);

        foreach (var tree in compilation.SyntaxTrees)
        {
            var root = tree.GetRoot();
            var model = compilation.GetSemanticModel(tree);

            CollectSites(root, model);
            ScanInventory(root, model);
        }

        FinalizePlans();

        var orderedPlans = _plans.Values
            .OrderBy(p => p.Id, StringComparer.Ordinal)
            .ToList();

        var orderedOccurrences = _occurrences
            .OrderBy(o => o.Location.SourceTree?.FilePath ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(o => o.Location.SourceSpan.Start)
            .ThenBy(o => o.AttributeMetadataName, StringComparer.Ordinal)
            .ToList();

        var report = new AttributeInventoryReport { Occurrences = orderedOccurrences };
        return (orderedPlans, report);
    }

    // =====================================================================
    // 站点枚举
    // =====================================================================

    private void CollectSites(SyntaxNode root, SemanticModel model)
    {
        foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            switch (model.GetOperation(invocation))
            {
                case IInvocationOperation op:
                    CollectFromInvocation(op, model);
                    break;
                case IDynamicInvocationOperation:
                case IInvalidOperation when IsDynamicContext(invocation, model):
                    TryCollectDynamicFailure(invocation, model);
                    break;
            }
        }

        foreach (var initializer in root.DescendantNodes().OfType<ConstructorInitializerSyntax>())
        {
            if (model.GetOperation(initializer) is IInvocationOperation op)
                CollectFromInvocation(op, model);
        }

        foreach (var objectCreation in root.DescendantNodes().OfType<ObjectCreationExpressionSyntax>())
        {
            switch (model.GetOperation(objectCreation))
            {
                case IObjectCreationOperation op:
                    CollectFromObjectCreation(op, model);
                    break;
                case IDynamicObjectCreationOperation:
                case IInvalidOperation when IsDynamicObjectContext(objectCreation, model):
                    TryCollectDynamicObjectFailure(objectCreation, model);
                    break;
            }
        }

        foreach (var elementAccess in root.DescendantNodes().OfType<ElementAccessExpressionSyntax>())
        {
            if (model.GetOperation(elementAccess) is IPropertyReferenceOperation op
                && op.Property.IsIndexer)
            {
                CollectFromIndexer(op, model);
            }
        }
    }

    private void CollectFromInvocation(IInvocationOperation op, SemanticModel model)
    {
        if (IsTargetInSkippedType(op.TargetMethod)) return;
        foreach (var argument in op.Arguments)
            ConsiderArgument(argument, op.TargetMethod, op.Syntax, model);
    }

    private void CollectFromObjectCreation(IObjectCreationOperation op, SemanticModel model)
    {
        if (op.Constructor == null) return;
        if (IsTargetInSkippedType(op.Constructor)) return;
        foreach (var argument in op.Arguments)
            ConsiderArgument(argument, op.Constructor, op.Syntax, model);
    }

    private void CollectFromIndexer(IPropertyReferenceOperation op, SemanticModel model)
    {
        var isSet = op.Parent is IAssignmentOperation assignment && assignment.Target == op;
        var accessor = isSet ? op.Property.SetMethod : op.Property.GetMethod;
        if (accessor == null) return;
        if (IsTargetInSkippedType(accessor)) return;

        foreach (var argument in op.Arguments)
            ConsiderArgument(argument, accessor, op.Syntax, model);
    }

    private void ConsiderArgument(
        IArgumentOperation argument,
        IMethodSymbol target,
        SyntaxNode callNode,
        SemanticModel model)
    {
        var parameter = argument.Parameter;
        if (parameter == null) return;

        var (attribute, kind) = FindCallerAttribute(parameter);
        if (attribute == null) return;

        // AN-C1：隐式默认值实参即省略点。
        var implicitDefault = argument.IsImplicit
            && argument.ArgumentKind == ArgumentKind.DefaultValue;

        // E08 strip-default runs earlier in the chain and "completes" call-site
        // argument lists with named arguments carrying each parameter's
        // default. Without recognizing that shape C10 would have nothing to do
        // when chained: accept a named explicit argument whose expression's
        // constant value equals the parameter's declared default.
        var explicitDefaultNamed = false;
        if (!argument.IsImplicit
            && argument.Syntax is ArgumentSyntax { NameColon: not null } argSyntax)
        {
            explicitDefaultNamed = IsDefaultValued(parameter, argSyntax.Expression, model);
        }

        // AN-C1：其余显式实参永不覆盖。
        if (!implicitDefault && !explicitDefaultNamed) return;

        if (IsInSkippedType(callNode, model)) return;

        var site = new CallerSite
        {
            CallNode = callNode,
            SiteLocation = callNode.GetLocation(),
            Parameter = parameter,
            ParameterOrdinal = parameter.Ordinal,
            Kind = kind,
            WasExplicitDefault = explicitDefaultNamed,
        };

        ApplyRestrictionsAndCompute(site, target, attribute, kind, model);
        AddSite(target, site);
    }

    private static bool IsDefaultValued(
        IParameterSymbol parameter,
        ExpressionSyntax expression,
        SemanticModel model)
    {
        if (!parameter.HasExplicitDefaultValue) return false;

        Optional<object?> constant;
        try
        {
            constant = model.GetConstantValue(expression);
        }
        catch
        {
            return false;
        }

        if (!constant.HasValue) return false;
        return Equals(constant.Value, parameter.ExplicitDefaultValue);
    }

    private void AddSite(IMethodSymbol target, CallerSite site)
    {
        if (!_plans.TryGetValue(target, out var plan))
        {
            plan = new CallerInfoPlan
            {
                Id = BuildPlanId(target),
                TargetMethod = target,
                Kind = IsSourceTarget(target) ? CallerPlanKind.SourceDeclaration : CallerPlanKind.ExternalTarget,
            };
            _plans[target] = plan;
        }

        plan.Sites.Add(site);
    }

    // =====================================================================
    // 禁区判定与取值
    // =====================================================================

    private void ApplyRestrictionsAndCompute(
        CallerSite site,
        IMethodSymbol target,
        AttributeData attribute,
        CallerInfoKind kind,
        SemanticModel model)
    {
        var callNode = site.CallNode;

        // AN-C8：表达式树 lambda 内站点 → CS2J1303。
        var enclosingLambda = callNode.Ancestors().OfType<LambdaExpressionSyntax>().FirstOrDefault();
        if (enclosingLambda != null && ExpressionTreeFacts.IsExpressionTreeLambda(model, enclosingLambda))
        {
            site.FailCode = AttributeNormalizeDiagnosticCode.CS2J1303;
            site.FailReason = "call site is inside an expression tree lambda";
            return;
        }

        switch (kind)
        {
            case CallerInfoKind.CallerMemberName:
                var memberName = GetCallerMemberNameValue(callNode);
                if (memberName == null)
                {
                    site.FailCode = AttributeNormalizeDiagnosticCode.CS2J1304;
                    site.FailReason = "caller member name cannot be statically determined";
                }
                else
                {
                    site.StringValue = memberName;
                }
                break;

            case CallerInfoKind.CallerFilePath:
                TryComputeFilePath(site, callNode);
                break;

            case CallerInfoKind.CallerLineNumber:
                var lineSpan = callNode.GetLocation().GetLineSpan();
                site.IntValue = lineSpan.StartLinePosition.Line + 1;
                break;

            case CallerInfoKind.CallerArgumentExpression:
                ComputeArgumentExpression(site, target, attribute, model);
                break;
        }
    }

    private bool TryComputeFilePath(CallerSite site, SyntaxNode callNode)
    {
        switch (_options.CallerFilePathPolicy)
        {
            case CallerFilePathPolicy.Empty:
                site.StringValue = string.Empty;
                return true;
            case CallerFilePathPolicy.Error:
                site.FailCode = AttributeNormalizeDiagnosticCode.CS2J1304;
                site.FailReason = "CallerFilePathPolicy.Error: file path must not be frozen";
                return false;
            case CallerFilePathPolicy.FreezeAnalyzed:
            default:
                // 冻结分析树（C10 输入树）FilePath 字面量。
                site.StringValue = callNode.SyntaxTree?.FilePath ?? string.Empty;
                return true;
        }
    }


    private static void ComputeArgumentExpression(
        CallerSite site,
        IMethodSymbol target,
        AttributeData attribute,
        SemanticModel model)
    {
        // AN-C5：特性构造实参必须是常量字符串形参名。
        if (attribute.ConstructorArguments.Length != 1
            || attribute.ConstructorArguments[0].Value is not string targetParamName
            || string.IsNullOrEmpty(targetParamName))
        {
            site.FailCode = AttributeNormalizeDiagnosticCode.CS2J1304;
            site.FailReason = "CallerArgumentExpression constructor argument is not a constant parameter name";
            return;
        }

        var targetParam = target.Parameters.FirstOrDefault(p => p.Name == targetParamName);
        if (targetParam == null)
        {
            site.FailCode = AttributeNormalizeDiagnosticCode.CS2J1304;
            site.FailReason = $"CallerArgumentExpression target parameter '{targetParamName}' not found";
            return;
        }

        var callOp = model.GetOperation(site.CallNode);
        IEnumerable<IArgumentOperation> allArguments = callOp switch
        {
            IInvocationOperation i => i.Arguments,
            IObjectCreationOperation oc => oc.Arguments,
            IPropertyReferenceOperation pr => pr.Arguments,
            _ => Array.Empty<IArgumentOperation>(),
        };

        var matched = allArguments
            .Where(a => !a.IsImplicit && SymbolEqualityComparer.Default.Equals(a.Parameter, targetParam))
            .ToList();

        if (matched.Count != 1)
        {
            site.FailCode = AttributeNormalizeDiagnosticCode.CS2J1304;
            site.FailReason = $"explicit argument for '{targetParamName}' is missing at this call site";
            return;
        }

        if (matched[0].ArgumentKind == ArgumentKind.ParamArray)
        {
            site.FailCode = AttributeNormalizeDiagnosticCode.CS2J1304;
            site.FailReason = $"target parameter '{targetParamName}' is a params expansion; cannot extract single expression text";
            return;
        }

        if (matched[0].Syntax is not ArgumentSyntax argumentSyntax)
        {
            site.FailCode = AttributeNormalizeDiagnosticCode.CS2J1304;
            site.FailReason = "bound argument syntax is not an ArgumentSyntax";
            return;
        }

        // 含内部 trivia、不含外层 trivia。
        site.StringValue = argumentSyntax.Expression.ToString();
    }

    /// <summary>
    /// CallerMemberName 严格取值表（design §2）。
    /// </summary>
    private static string? GetCallerMemberNameValue(SyntaxNode siteNode)
    {
        for (var n = siteNode.Parent; n != null; n = n.Parent)
        {
            switch (n)
            {
                case MethodDeclarationSyntax m:
                    return m.Identifier.Text;
                case LocalFunctionStatementSyntax lf:
                    return lf.Identifier.Text;
                case ConstructorDeclarationSyntax c:
                    return c.Modifiers.Any(t => t.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.StaticKeyword))
                        ? ".cctor"
                        : ".ctor";
                case DestructorDeclarationSyntax:
                    return "Finalize";
                case PropertyDeclarationSyntax p:
                    return p.Identifier.Text;
                case IndexerDeclarationSyntax:
                    return "Item";
                case EventDeclarationSyntax e:
                    return e.Identifier.Text;
                case VariableDeclaratorSyntax vd when vd.Parent is VariableDeclarationSyntax v
                    && v.Parent is BaseFieldDeclarationSyntax field:
                    return field.Modifiers.Any(t => t.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.StaticKeyword))
                        ? ".cctor"
                        : ".ctor";
                case GlobalStatementSyntax:
                    return "$";
                case CompilationUnitSyntax:
                    // 顶层语句等无成员上下文。
                    return "$";
            }
        }

        return null;
    }

    // =====================================================================
    // dynamic 站点（CS2J1302）
    // =====================================================================

    private static bool IsDynamicContext(InvocationExpressionSyntax node, SemanticModel model)
    {
        var symbolInfo = model.GetSymbolInfo(node);
        if (symbolInfo.CandidateReason == CandidateReason.LateBound) return true;
        if (model.GetTypeInfo(node.Expression).Type?.TypeKind == TypeKind.Dynamic) return true;
        foreach (var argument in node.ArgumentList.Arguments)
        {
            if (model.GetTypeInfo(argument.Expression).Type?.TypeKind == TypeKind.Dynamic) return true;
        }
        return false;
    }

    private static bool IsDynamicObjectContext(ObjectCreationExpressionSyntax node, SemanticModel model)
    {
        var symbolInfo = model.GetSymbolInfo(node);
        if (symbolInfo.CandidateReason == CandidateReason.LateBound) return true;
        foreach (var argument in node.ArgumentList.Arguments)
        {
            if (model.GetTypeInfo(argument.Expression).Type?.TypeKind == TypeKind.Dynamic) return true;
        }
        return false;
    }

    private void TryCollectDynamicFailure(InvocationExpressionSyntax node, SemanticModel model)
    {
        var symbolInfo = model.GetSymbolInfo(node);
        var candidates = symbolInfo.CandidateSymbols.OfType<IMethodSymbol>()
            .Where(HasCallerParameter)
            .ToList();
        if (candidates.Count == 0 && symbolInfo.Symbol is IMethodSymbol direct && HasCallerParameter(direct))
            candidates.Add(direct);

        foreach (var target in candidates)
        {
            if (IsTargetInSkippedType(target)) continue;
            AddDynamicSites(target, node);
        }
    }

    private void TryCollectDynamicObjectFailure(ObjectCreationExpressionSyntax node, SemanticModel model)
    {
        var symbolInfo = model.GetSymbolInfo(node);
        var candidateCtors = symbolInfo.CandidateSymbols.OfType<IMethodSymbol>()
            .Where(m => m.MethodKind == MethodKind.Constructor && HasCallerParameter(m))
            .ToList();
        if (candidateCtors.Count == 0
            && symbolInfo.Symbol is IMethodSymbol direct
            && direct.MethodKind == MethodKind.Constructor
            && HasCallerParameter(direct))
        {
            candidateCtors.Add(direct);
        }

        foreach (var target in candidateCtors)
        {
            if (IsTargetInSkippedType(target)) continue;
            AddDynamicSites(target, node);
        }
    }

    private void AddDynamicSites(IMethodSymbol target, SyntaxNode callNode)
    {
        foreach (var parameter in target.Parameters)
        {
            var (attribute, kind) = FindCallerAttribute(parameter);
            if (attribute == null) continue;

            var site = new CallerSite
            {
                CallNode = callNode,
                SiteLocation = callNode.GetLocation(),
                Parameter = parameter,
                ParameterOrdinal = parameter.Ordinal,
                Kind = kind,
                FailCode = AttributeNormalizeDiagnosticCode.CS2J1302,
                FailReason = "call site is dynamically bound; caller value cannot be statically determined",
            };
            AddSite(target, site);
        }
    }

    private static bool HasCallerParameter(IMethodSymbol method)
    {
        foreach (var parameter in method.Parameters)
            foreach (var attribute in parameter.GetAttributes())
                if (KnownAttributeCatalog.IsCallerAttribute(attribute.AttributeClass))
                    return true;
        return false;
    }

    // =====================================================================
    // plan 收尾
    // =====================================================================

    private void FinalizePlans()
    {
        foreach (var plan in _plans.Values)
        {
            var sourceLocations = plan.TargetMethod.Locations
                .Where(l => l.IsInSource)
                .ToList();
            if (sourceLocations.Count == 0
                && plan.TargetMethod.AssociatedSymbol is IPropertySymbol associatedProperty)
            {
                sourceLocations = associatedProperty.Locations.Where(l => l.IsInSource).ToList();
            }

            if (plan.Kind == CallerPlanKind.SourceDeclaration)
            {
                plan.DeclarationLocation = sourceLocations.FirstOrDefault();

                foreach (var parameter in plan.TargetMethod.Parameters)
                {
                    foreach (var attribute in parameter.GetAttributes())
                    {
                        if (!KnownAttributeCatalog.IsCallerAttribute(attribute.AttributeClass)) continue;
                        var attrLocation = GetAttributeSyntaxLocation(attribute);
                        if (attrLocation is { IsInSource: true })
                            plan.DeclarationCallerAttributeLocations.Add(attrLocation);
                    }
                }
            }

            foreach (var location in sourceLocations)
            {
                if (location.SourceTree?.FilePath is { } file) plan.TouchedFiles.Add(file);
            }

            foreach (var site in plan.Sites)
            {
                if (site.FilePath is { Length: > 0 } siteFile) plan.TouchedFiles.Add(siteFile);
            }

            plan.Sites.Sort(CompareSites);

            // plan 原子性：任一不可行站点 → 整 plan 跳过。
            var infeasible = plan.Sites.Where(s => !s.IsFeasible).ToList();
            if (infeasible.Count > 0)
            {
                plan.Outcome = CallerPlanOutcome.Skipped;
                var codes = infeasible.Select(s => s.FailCode!).Distinct().ToList();
                plan.FailCode = codes.Count == 1 ? codes[0] : AttributeNormalizeDiagnosticCode.CS2J1301;
                plan.FailReason = BuildSkipReason(infeasible);
            }
        }
    }

    private static int CompareSites(CallerSite a, CallerSite b)
    {
        var fileCmp = string.CompareOrdinal(a.FilePath, b.FilePath);
        if (fileCmp != 0) return fileCmp;
        var spanCmp = a.SiteLocation.SourceSpan.Start.CompareTo(b.SiteLocation.SourceSpan.Start);
        if (spanCmp != 0) return spanCmp;
        return a.ParameterOrdinal.CompareTo(b.ParameterOrdinal);
    }

    private static string BuildSkipReason(List<CallerSite> infeasible)
    {
        var parts = infeasible
            .GroupBy(s => s.FailCode)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => $"{g.Key} x{g.Count()} ({g.First().FailReason})");
        return "Caller plan atomically skipped: " + string.Join("; ", parts);
    }

    // =====================================================================
    // attribute inventory
    // =====================================================================

    private void ScanInventory(SyntaxNode root, SemanticModel model)
    {
        foreach (var attribute in root.DescendantNodes().OfType<AttributeSyntax>())
        {
            var symbolInfo = model.GetSymbolInfo(attribute);
            var ctor = symbolInfo.Symbol as IMethodSymbol
                ?? symbolInfo.CandidateSymbols.OfType<IMethodSymbol>().FirstOrDefault();
            var attributeType = ctor?.ContainingType;

            var classification = KnownAttributeCatalog.Classify(attributeType, attribute, _options);
            if (classification.Status == BridgeStatus.Ignored) continue;

            var targetSymbol = TryGetAttributeTarget(attribute, model);

            _occurrences.Add(new AttributeOccurrence(
                classification.MetadataName,
                classification.Family,
                classification.Status,
                targetSymbol,
                attribute.GetLocation(),
                Array.Empty<string>(),
                Array.Empty<string>(),
                classification.OwnerPass));
        }
    }

    private static ISymbol? TryGetAttributeTarget(AttributeSyntax attribute, SemanticModel model)
    {
        if (attribute.Parent is not AttributeListSyntax list) return null;

        if (list.Target?.Identifier.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.AssemblyKeyword) == true)
            return model.Compilation.Assembly;
        if (list.Target?.Identifier.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.ModuleKeyword) == true)
            return model.Compilation.SourceModule;

        if (list.Parent is { } targetNode)
            return model.GetDeclaredSymbol(targetNode) ?? model.GetSymbolInfo(targetNode).Symbol;

        return null;
    }

    // =====================================================================
    // SkipTypes
    // =====================================================================

    private bool IsTargetInSkippedType(IMethodSymbol? method)
    {
        if (method == null || _options.SkipTypes.Count == 0) return false;
        var containingType = method.ContainingType?.ToDisplayString();
        return containingType != null && _options.SkipTypes.Contains(containingType);
    }

    private bool IsInSkippedType(SyntaxNode node, SemanticModel model)
    {
        if (_options.SkipTypes.Count == 0) return false;

        foreach (var ancestor in node.AncestorsAndSelf())
        {
            if (ancestor is BaseTypeDeclarationSyntax typeDeclaration)
            {
                if (model.GetDeclaredSymbol(typeDeclaration) is INamedTypeSymbol typeSymbol
                    && _options.SkipTypes.Contains(typeSymbol.ToDisplayString()))
                {
                    return true;
                }

                return false;
            }
        }

        return false;
    }


    private static Location? GetAttributeSyntaxLocation(AttributeData attribute)
    {
        if (attribute.ApplicationSyntaxReference is { } syntaxReference)
            return syntaxReference.SyntaxTree.GetLocation(syntaxReference.Span);
        return null;
    }
    // =====================================================================
    // 工具
    // =====================================================================

    private static (AttributeData? Data, CallerInfoKind Kind) FindCallerAttribute(IParameterSymbol parameter)
    {
        foreach (var attribute in parameter.GetAttributes())
        {
            if (attribute.AttributeClass != null
                && KnownAttributeCatalog.TryGetCallerKind(attribute.AttributeClass, out var kind))
            {
                return (attribute, kind);
            }
        }

        return (null, default);
    }

    private static bool IsSourceTarget(IMethodSymbol method)
    {
        if (method.Locations.Any(l => l.IsInSource)) return true;
        if (method.AssociatedSymbol is IPropertySymbol property && property.Locations.Any(l => l.IsInSource))
            return true;
        return false;
    }

    private static string BuildPlanId(IMethodSymbol target)
    {
        var callerIndices = target.Parameters
            .Where(p => p.GetAttributes().Any(a => KnownAttributeCatalog.IsCallerAttribute(a.AttributeClass)))
            .Select(p => p.Ordinal)
            .OrderBy(i => i)
            .Select(i => i.ToString());

        return $"caller:{target.ToDisplayString()}({string.Join(",", callerIndices)})";
    }
}
