using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.VarianceAudit;

/// <summary>Direction of a variance-dependent conversion edge (design D3a).</summary>
public enum VarianceConversionDirection
{
    None,
    Covariant,
    Contravariant,
    Array,
    Both,
    Unresolved,
}

/// <summary>
/// One conversion edge that depends on C# variance (or is a conservative candidate for it),
/// with the diagnostic code it maps to (design D3a/D8/§6).
/// </summary>
public sealed record VarianceConversionFinding(
    string Code,
    VarianceConversionDirection Direction,
    bool Resolved,
    UseFlow Flow,
    string SourceType,
    string TargetType,
    string Message,
    string? FilePath = null,
    int Line = 0,
    int Column = 0);

/// <summary>
/// Use-site safety classification (design D3②/D3a/D8/D11). Refines position flows with a
/// scope member-access summary, structurally detects variance implicit conversions over
/// assignment/initializer/return/argument/method-group edges (Roslyn public API only,
/// prefer false positives), attaches mapped-mutability facts (D9) and computes the
/// three-state conclusion per use site.
/// </summary>
internal sealed class UseSiteSafetyClassifier
{
    private readonly Compilation _compilation;
    private readonly VarianceAuditOptions _options;
    private readonly VarianceAuditStatistics _statistics;
    private readonly TypeMutabilityTable _mutability;
    private readonly Dictionary<(string File, int Line, int Column), (SemanticModel Model, TypeSyntax Node)> _index = new();

    public UseSiteSafetyClassifier(
        Compilation compilation,
        VarianceAuditOptions options,
        VarianceAuditStatistics statistics)
    {
        _compilation = compilation;
        _options = options;
        _statistics = statistics;
        _mutability = new TypeMutabilityTable();
        BuildIndex();
    }

    public void Enrich(VarianceAuditAnalysis analysis)
    {
        var findings = new List<VarianceConversionFinding>();
        foreach (var tree in _compilation.SyntaxTrees)
        {
            ScanEdges(_compilation.GetSemanticModel(tree), findings);
        }

        findings.Sort((a, b) => VarianceSiteInventory.Compare(
            a.FilePath, a.Line, a.Column, a.Code,
            b.FilePath, b.Line, b.Column, b.Code));
        analysis.ConversionSites.AddRange(findings);

        EnrichUseRecords(analysis, findings);
        FillStatistics(analysis, findings);
    }

    // ---- D3② scope member-access summary ----

    private void EnrichUseRecords(VarianceAuditAnalysis analysis, List<VarianceConversionFinding> findings)
    {
        var byKey = new Dictionary<(string, int, int), VarianceConversionFinding>();
        foreach (var finding in findings)
        {
            var key = (finding.FilePath ?? string.Empty, finding.Line, finding.Column);
            byKey.TryAdd(key, finding);
        }

        var enriched = new List<VarianceUseRecord>(analysis.UseSites.Count);
        foreach (var use in analysis.UseSites)
        {
            var flow = use.Flow;
            var unresolved = false;
            INamedTypeSymbol? named = null;
            if (_index.TryGetValue((use.FilePath ?? string.Empty, use.Line, use.Column), out var entry))
            {
                named = entry.Model.GetTypeInfo(entry.Node).Type as INamedTypeSymbol;
                if (named != null && IsVariantClosed(named) && !use.BaselineHasErrors)
                {
                    (flow, unresolved) = RefineFlow(entry.Model, entry.Node, use.Flow);
                }
            }

            string? mapped = null;
            IReadOnlyList<string>? mutating = null;
            if (named != null)
            {
                var tableEntry = _mutability.Lookup(named);
                if (tableEntry != null)
                {
                    mapped = tableEntry.JavaType;
                    mutating = tableEntry.MutatingMembers;
                }
                else if (_options.CheckMappedMutability && _mutability.IsConservativelyMutable(named))
                {
                    mutating = TypeMutabilityTable.UnmappedMembers;
                }
            }

            var mutabilityExpansion = _options.CheckMappedMutability
                && flow == UseFlow.Producer && mutating is { Count: > 0 };

            var matched = byKey.TryGetValue((use.FilePath ?? string.Empty, use.Line, use.Column), out var finding);
            var classification = matched
                ? (finding!.Resolved
                   && finding.Direction is VarianceConversionDirection.Covariant
                       or VarianceConversionDirection.Contravariant
                       or VarianceConversionDirection.Array
                   && finding.Flow != UseFlow.Both
                    ? VarianceClassification.NeedsWildcard
                    : VarianceClassification.Unsafe)
                : mutabilityExpansion || unresolved
                    ? VarianceClassification.Unsafe
                    : VarianceClassification.Safe;

            enriched.Add(use with
            {
                Flow = flow,
                VarianceConversion = matched,
                Classification = classification,
                MappedJavaType = mapped,
                MutatingMembers = mutating,
                MemberResolutionIncomplete = unresolved,
            });
        }

        analysis.UseSites.Clear();
        analysis.UseSites.AddRange(enriched);
    }

    private static (UseFlow Flow, bool Unresolved) RefineFlow(SemanticModel model, TypeSyntax node, UseFlow baseFlow)
    {
        var symbols = DeclaredSymbolsOf(node, model);
        if (symbols.Count == 0)
        {
            return (baseFlow, false);
        }

        var container = node.Ancestors().FirstOrDefault(a => a is MemberDeclarationSyntax)
                        ?? (SyntaxNode)node.SyntaxTree.GetRoot();
        var anyConsumer = false;
        var anyProducer = false;
        var unresolved = false;
        foreach (var identifier in container.DescendantNodes().OfType<IdentifierNameSyntax>())
        {
            var symbol = model.GetSymbolInfo(identifier).Symbol;
            if (symbol == null || !symbols.Any(s => SymbolEqualityComparer.Default.Equals(s, symbol)))
            {
                continue;
            }

            if (identifier.Parent is MemberAccessExpressionSyntax access && access.Expression == identifier)
            {
                var member = model.GetSymbolInfo(access).Symbol;
                if (member == null)
                {
                    unresolved = true;
                    continue;
                }

                switch (MemberFlow(member))
                {
                    case UseFlow.Consumer:
                        anyConsumer = true;
                        break;
                    case UseFlow.Producer:
                        anyProducer = true;
                        break;
                    default:
                        anyConsumer = true;
                        anyProducer = true;
                        break;
                }
            }
            else if (identifier.Parent is ElementAccessExpressionSyntax element && element.Expression == identifier)
            {
                if (IsAssignmentTarget(element))
                {
                    anyConsumer = true;
                }
                else
                {
                    anyProducer = true;
                }
            }
            else if (identifier.Parent is ArgumentSyntax)
            {
                anyConsumer = true;
            }
            else if (identifier.Parent is ReturnStatementSyntax or ArrowExpressionClauseSyntax)
            {
                anyProducer = true;
            }
        }

        switch (baseFlow)
        {
            case UseFlow.Both:
                if (unresolved)
                {
                    return (UseFlow.Both, true);
                }

                if (anyConsumer)
                {
                    return (UseFlow.Both, false);
                }

                return anyProducer ? (UseFlow.Producer, false) : (UseFlow.Both, false);
            case UseFlow.Producer:
                return unresolved || anyConsumer ? (UseFlow.Both, unresolved) : (UseFlow.Producer, false);
            default:
                return (UseFlow.Consumer, unresolved);
        }
    }

    private static List<ISymbol> DeclaredSymbolsOf(TypeSyntax node, SemanticModel model)
    {
        var symbols = new List<ISymbol>();
        switch (node.Parent)
        {
            case VariableDeclarationSyntax declaration:
                foreach (var declarator in declaration.Variables)
                {
                    if (model.GetDeclaredSymbol(declarator) is { } symbol)
                    {
                        symbols.Add(symbol);
                    }
                }

                break;
            case ParameterSyntax parameter:
                if (model.GetDeclaredSymbol(parameter) is { } parameterSymbol)
                {
                    symbols.Add(parameterSymbol);
                }

                break;
            case ForEachStatementSyntax forEach:
                if (model.GetDeclaredSymbol(forEach) is { } forEachSymbol)
                {
                    symbols.Add(forEachSymbol);
                }

                break;
        }

        return symbols;
    }

    private static UseFlow MemberFlow(ISymbol symbol) => symbol switch
    {
        IMethodSymbol method => method.ReturnsVoid ? UseFlow.Consumer : UseFlow.Producer,
        IPropertySymbol property when property.GetMethod != null && property.SetMethod == null => UseFlow.Producer,
        IPropertySymbol property when property.SetMethod != null && property.GetMethod == null => UseFlow.Consumer,
        _ => UseFlow.Both,
    };

    private static bool IsAssignmentTarget(SyntaxNode node)
    {
        if (node.Parent is AssignmentExpressionSyntax assignment && assignment.Left == node)
        {
            return true;
        }

        if (node.Parent is PrefixUnaryExpressionSyntax prefix
            && (prefix.IsKind(SyntaxKind.PreIncrementExpression) || prefix.IsKind(SyntaxKind.PreDecrementExpression)))
        {
            return true;
        }

        return node.Parent is PostfixUnaryExpressionSyntax postfix
               && (postfix.IsKind(SyntaxKind.PostIncrementExpression) || postfix.IsKind(SyntaxKind.PostDecrementExpression));
    }

    // ---- D3a structured conversion detection ----

    private void ScanEdges(SemanticModel model, List<VarianceConversionFinding> findings)
    {
        if (VariancePositionFacts.HasBaselineErrors(model))
        {
            return;
        }

        var root = model.SyntaxTree.GetRoot();
        foreach (var node in root.DescendantNodes())
        {
            switch (node)
            {
                case VariableDeclaratorSyntax declarator
                    when declarator.Initializer != null && declarator.Parent is VariableDeclarationSyntax declaration:
                    TryAddEdge(
                        model, model.GetTypeInfo(declaration.Type).Type, declaration.Type,
                        declarator.Initializer.Value, UseFlow.Both, findings);
                    break;
                case AssignmentExpressionSyntax assignment when assignment.IsKind(SyntaxKind.SimpleAssignmentExpression):
                    TryAddEdge(
                        model, model.GetTypeInfo(assignment.Left).Type, assignment.Left,
                        assignment.Right, UseFlow.Both, findings);
                    break;
                case ReturnStatementSyntax statement when statement.Expression != null && !InsideLambda(statement):
                    TryAddEdge(
                        model, ReturnTypeOf(model, statement), statement,
                        statement.Expression, UseFlow.Producer, findings);
                    break;
                case ArgumentSyntax argument:
                    var target = ArgumentTargetType(model, argument);
                    if (target != null)
                    {
                        TryAddEdge(model, target, argument.Expression, argument.Expression, UseFlow.Consumer, findings);
                    }

                    break;
            }
        }
    }

    private void TryAddEdge(
        SemanticModel model,
        ITypeSymbol? targetType,
        SyntaxNode targetNode,
        ExpressionSyntax source,
        UseFlow flow,
        List<VarianceConversionFinding> findings)
    {
        if (targetType == null)
        {
            return;
        }

        var sourceType = model.GetTypeInfo(source).Type;
        var conversion = model.GetConversion(source);
        VarianceConversionDirection direction;
        bool resolved;
        if (conversion.IsUserDefined)
        {
            if (!IsVariantClosed(targetType) && !IsVariantClosed(sourceType))
            {
                return;
            }

            direction = VarianceConversionDirection.Unresolved;
            resolved = false;
        }
        else if (sourceType == null)
        {
            if (conversion.IsMethodGroup && targetType is INamedTypeSymbol { TypeKind: TypeKind.Delegate } delegateType)
            {
                direction = CompareMethodGroup(conversion.MethodSymbol, delegateType);
                resolved = direction is VarianceConversionDirection.Covariant
                    or VarianceConversionDirection.Contravariant;
            }
            else
            {
                return;
            }
        }
        else if (sourceType.TypeKind == TypeKind.Error || sourceType is IDynamicTypeSymbol)
        {
            if (!IsVariantClosed(targetType))
            {
                return;
            }

            direction = VarianceConversionDirection.Unresolved;
            resolved = false;
        }
        else
        {
            direction = ComparePair(sourceType, targetType);
            resolved = direction is VarianceConversionDirection.Covariant
                or VarianceConversionDirection.Contravariant
                or VarianceConversionDirection.Array
                or VarianceConversionDirection.Both;
            if (direction == VarianceConversionDirection.None)
            {
                return;
            }
        }

        if (direction == VarianceConversionDirection.None)
        {
            return;
        }

        var code = CodeFor(direction, resolved, flow);
        var sourceText = sourceType?.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat) ?? "<method group>";
        var targetText = targetType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
        var span = targetNode.GetLocation().GetLineSpan().StartLinePosition;
        findings.Add(new VarianceConversionFinding(
            Code: code,
            Direction: direction,
            Resolved: resolved,
            Flow: flow,
            SourceType: sourceText,
            TargetType: targetText,
            Message: ConversionMessage(code, direction, flow, sourceText, targetText),
            FilePath: model.SyntaxTree.FilePath,
            Line: span.Line + 1,
            Column: span.Character + 1));
    }

    private static VarianceConversionDirection ComparePair(ITypeSymbol source, ITypeSymbol target)
    {
        if (target is IArrayTypeSymbol targetArray && source is IArrayTypeSymbol sourceArray)
        {
            if (targetArray.Rank != sourceArray.Rank
                || !targetArray.ElementType.IsReferenceType
                || !sourceArray.ElementType.IsReferenceType)
            {
                return VarianceConversionDirection.None;
            }

            return IsDerivedFrom(sourceArray.ElementType, targetArray.ElementType)
                ? VarianceConversionDirection.Array
                : VarianceConversionDirection.None;
        }

        if (target is not INamedTypeSymbol targetNamed
            || source is not INamedTypeSymbol sourceNamed
            || !IsVariantClosed(targetNamed))
        {
            return VarianceConversionDirection.None;
        }

        var original = targetNamed.OriginalDefinition;
        if (!SymbolEqualityComparer.Default.Equals(sourceNamed.OriginalDefinition, original))
        {
            return VarianceConversionDirection.None;
        }

        var covariant = false;
        var contravariant = false;
        var count = Math.Min(sourceNamed.TypeArguments.Length, original.TypeParameters.Length);
        for (var i = 0; i < count; i++)
        {
            var parameter = original.TypeParameters[i];
            if (parameter.Variance == Microsoft.CodeAnalysis.VarianceKind.None)
            {
                continue;
            }

            var sourceArgument = sourceNamed.TypeArguments[i];
            var targetArgument = targetNamed.TypeArguments[i];
            if (SymbolEqualityComparer.Default.Equals(sourceArgument, targetArgument)
                || !sourceArgument.IsReferenceType
                || !targetArgument.IsReferenceType)
            {
                continue;
            }

            if (parameter.Variance == Microsoft.CodeAnalysis.VarianceKind.Out)
            {
                covariant |= IsDerivedFrom(sourceArgument, targetArgument);
            }
            else
            {
                contravariant |= IsDerivedFrom(targetArgument, sourceArgument);
            }
        }

        if (covariant && contravariant)
        {
            return VarianceConversionDirection.Both;
        }

        if (covariant)
        {
            return VarianceConversionDirection.Covariant;
        }

        return contravariant ? VarianceConversionDirection.Contravariant : VarianceConversionDirection.None;
    }

    private static VarianceConversionDirection CompareMethodGroup(IMethodSymbol? method, INamedTypeSymbol delegateType)
    {
        if (method == null)
        {
            return VarianceConversionDirection.Unresolved;
        }

        var original = delegateType.OriginalDefinition;
        var invoke = original.DelegateInvokeMethod ?? delegateType.DelegateInvokeMethod;
        if (invoke == null)
        {
            return VarianceConversionDirection.Unresolved;
        }

        var covariant = false;
        var contravariant = false;
        var mapped = false;
        for (var i = 0; i < original.TypeParameters.Length && i < delegateType.TypeArguments.Length; i++)
        {
            var parameter = original.TypeParameters[i];
            if (parameter.Variance == Microsoft.CodeAnalysis.VarianceKind.None)
            {
                continue;
            }

            var argument = delegateType.TypeArguments[i];
            for (var j = 0; j < invoke.Parameters.Length; j++)
            {
                if (!SymbolEqualityComparer.Default.Equals(invoke.Parameters[j].Type, parameter) || j >= method.Parameters.Length)
                {
                    continue;
                }

                mapped = true;
                var source = method.Parameters[j].Type;
                if (source.IsReferenceType && argument.IsReferenceType && IsDerivedFrom(argument, source))
                {
                    contravariant = true;
                }
            }

            if (SymbolEqualityComparer.Default.Equals(invoke.ReturnType, parameter))
            {
                mapped = true;
                if (method.ReturnType.IsReferenceType && argument.IsReferenceType
                    && IsDerivedFrom(method.ReturnType, argument))
                {
                    covariant = true;
                }
            }
        }

        if (covariant && contravariant)
        {
            return VarianceConversionDirection.Both;
        }

        if (covariant)
        {
            return VarianceConversionDirection.Covariant;
        }

        if (contravariant)
        {
            return VarianceConversionDirection.Contravariant;
        }

        return mapped ? VarianceConversionDirection.None : VarianceConversionDirection.Unresolved;
    }

    private static bool IsDerivedFrom(ITypeSymbol derived, ITypeSymbol baseType)
    {
        if (SymbolEqualityComparer.Default.Equals(derived, baseType))
        {
            return true;
        }

        if (!derived.IsReferenceType || !baseType.IsReferenceType)
        {
            return false;
        }

        for (var current = derived.BaseType; current != null; current = current.BaseType)
        {
            if (SymbolEqualityComparer.Default.Equals(current, baseType))
            {
                return true;
            }
        }

        return derived.AllInterfaces.Any(i => SymbolEqualityComparer.Default.Equals(i, baseType));
    }

    private static bool IsVariantClosed(ITypeSymbol? type)
        => type is INamedTypeSymbol named
           && named.TypeArguments.Length > 0
           && named.OriginalDefinition.TypeParameters.Any(
               parameter => parameter.Variance != Microsoft.CodeAnalysis.VarianceKind.None);

    private static ITypeSymbol? ReturnTypeOf(SemanticModel model, ReturnStatementSyntax statement)
        => model.GetEnclosingSymbol(statement.SpanStart) is IMethodSymbol method ? method.ReturnType : null;

    private static bool InsideLambda(ReturnStatementSyntax statement)
    {
        foreach (var ancestor in statement.Ancestors())
        {
            if (ancestor is LambdaExpressionSyntax or AnonymousMethodExpressionSyntax)
            {
                return true;
            }

            if (ancestor is MemberDeclarationSyntax)
            {
                break;
            }
        }

        return false;
    }

    private static ITypeSymbol? ArgumentTargetType(SemanticModel model, ArgumentSyntax argument)
    {
        if (argument.Parent is not ArgumentListSyntax list)
        {
            return null;
        }

        var method = list.Parent switch
        {
            InvocationExpressionSyntax invocation => model.GetSymbolInfo(invocation).Symbol as IMethodSymbol,
            ObjectCreationExpressionSyntax creation => model.GetSymbolInfo(creation).Symbol as IMethodSymbol,
            _ => null,
        };
        if (method == null || method.IsExtensionMethod)
        {
            return null;
        }

        if (argument.NameColon != null)
        {
            var name = argument.NameColon.Name.Identifier.ValueText;
            return method.Parameters.FirstOrDefault(parameter => parameter.Name == name)?.Type;
        }

        var index = list.Arguments.IndexOf(argument);
        return index >= 0 && index < method.Parameters.Length ? method.Parameters[index].Type : null;
    }

    private static string CodeFor(VarianceConversionDirection direction, bool resolved, UseFlow flow)
    {
        if (!resolved || direction == VarianceConversionDirection.Both || flow == UseFlow.Both)
        {
            return VarianceCodes.BothConversionSite;
        }

        return direction == VarianceConversionDirection.Contravariant
            ? VarianceCodes.ContravariantConversionSite
            : VarianceCodes.CovariantConversionSite;
    }

    private static string ConversionMessage(
        string code,
        VarianceConversionDirection direction,
        UseFlow flow,
        string source,
        string target)
    {
        if (code == VarianceCodes.CovariantConversionSite)
        {
            var subject = direction == VarianceConversionDirection.Array
                ? "reference-array covariance"
                : "covariant conversion";
            return $"site '<{source}>' relies on {subject} to {target}; Java emits invariant types and "
                   + "this use will not compile; redesign at the boundary or await the use-site rewrite pass";
        }

        if (code == VarianceCodes.ContravariantConversionSite)
        {
            return $"site '<{source}>' relies on contravariant conversion to {target}; Java emits invariant types "
                   + "and this use will not compile; redesign at the boundary or await the use-site rewrite pass";
        }

        return $"site '<{source}>' -> {target}: variance conversion could not be fully resolved "
               + $"({(flow == UseFlow.Both ? "both-flow" : "dynamic/error/user-defined")}); conservatively treated as "
               + "unsafe; redesign at the boundary";
    }

    // ---- index + statistics ----

    private void BuildIndex()
    {
        foreach (var tree in _compilation.SyntaxTrees)
        {
            var model = _compilation.GetSemanticModel(tree);
            foreach (var node in tree.GetRoot().DescendantNodes().OfType<TypeSyntax>())
            {
                var span = node.GetLocation().GetLineSpan().StartLinePosition;
                _index.TryAdd((tree.FilePath, span.Line + 1, span.Character + 1), (model, node));
            }
        }
    }

    private void FillStatistics(VarianceAuditAnalysis analysis, List<VarianceConversionFinding> findings)
    {
        _statistics.VarianceConversionsCovariant =
            findings.Count(f => f.Direction == VarianceConversionDirection.Covariant);
        _statistics.VarianceConversionsContravariant =
            findings.Count(f => f.Direction == VarianceConversionDirection.Contravariant);
        _statistics.VarianceConversionsArray =
            findings.Count(f => f.Direction == VarianceConversionDirection.Array);

        _statistics.UseSitesProducer = analysis.UseSites.Count(u => u.Flow == UseFlow.Producer);
        _statistics.UseSitesConsumer = analysis.UseSites.Count(u => u.Flow == UseFlow.Consumer);
        _statistics.UseSitesBoth = analysis.UseSites.Count(u => u.Flow == UseFlow.Both);

        var matched = new HashSet<(string, int, int)>();
        foreach (var use in analysis.UseSites.Where(u => u.VarianceConversion))
        {
            matched.Add((use.FilePath ?? string.Empty, use.Line, use.Column));
        }

        var unsafeSites = analysis.UseSites.Count(u => u.Classification == VarianceClassification.Unsafe);
        var unmatchedUnresolved = findings.Count(f =>
            f.Code == VarianceCodes.BothConversionSite
            && !matched.Contains((f.FilePath ?? string.Empty, f.Line, f.Column)));
        _statistics.UnsafeUseSites = unsafeSites + unmatchedUnresolved;
    }
}