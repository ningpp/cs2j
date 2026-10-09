using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.GenericPreprocess;

/// <summary>
/// Post-rewrite gate (spec §4.5): counts shapes that must be gone after C02.
/// The scan runs against the REWRITTEN, recompiled tree set and uses the analysis
/// only for the planned-field list (rule 6). Rules:
/// <list type="number">
/// <item><c>static-field</c> — static field declarations referencing a class type parameter.</item>
/// <item><c>catch-type-param</c> / <c>type-param-pattern</c> — unf lowered shapes.</item>
/// <item><c>method-without-anchor</c> / <c>family-anchor-mismatch</c> — a visible token site without an anchor in the method, or override-family anchor sets disagree.</item>
/// <item><c>forward-only-without-anchor</c> — a type parameter only forwarded to a generic callee, no anchor.</item>
/// <item><c>exprtree-illegal-rewrite</c> — anchor/lowered nodes structurally present inside a quoted expression-tree lambda.</item>
/// <item><c>holder-bare-field</c> — an unbound read/write of a holder-removed static generic field.</item>
/// </list>
/// </summary>
public static class GenericDeepenResidualScanner
{
    public static GenericDeepenResidualReport Scan(
        Compilation rewritten,
        GenericDeepenAnalysis analysis,
        GenericDeepenOptions options,
        GenericDeepenOverrides? overrides = null)
    {
        var findings = new List<Finding>();
        var plannedFieldNames = analysis.StaticFields
            .Select(plan => plan.FieldName)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var tree in rewritten.SyntaxTrees)
        {
            var model = rewritten.GetSemanticModel(tree);
            var root = tree.GetRoot();
            var isHolderFile = tree.FilePath.Replace('\\', '/')
                .Contains(GenericDeepenSyntaxFacts.GeneratedFileStem, StringComparison.OrdinalIgnoreCase);
            if (isHolderFile)
            {
                // Our own generated holder classes: token parameters serve the tokens
                // and the gate only judges user code shapes.
                continue;
            }

            ScanStructuralRules(model, root, findings);
            ScanMethodAnchorRules(model, root, findings);
            ScanExpressionTreeRewrites(model, root, findings);
            ScanBareFieldReferences(model, root, plannedFieldNames, findings);
        }

        ScanFamilyConsistency(rewritten, findings);
        return BuildReport(findings, options, overrides);
    }

    private sealed record Finding(
        string Rule,
        SyntaxNode Node,
        ISymbol? Member,
        ITypeSymbol? Type,
        string Detail);

    // ---- rules 1/2: shapes the lowering must remove ----

    private static void ScanStructuralRules(SemanticModel model, SyntaxNode root, List<Finding> findings)
    {
        foreach (var node in root.DescendantNodes())
        {
            switch (node)
            {
                case FieldDeclarationSyntax field when field.Modifiers.Any(m => m.IsKind(SyntaxKind.StaticKeyword))
                        && ReferencesVisibleTypeParameter(model, field.Declaration.Type):
                {
                    var declarator = field.Declaration.Variables.FirstOrDefault();
                    var symbol = declarator == null ? null : model.GetDeclaredSymbol(declarator);
                    findings.Add(new Finding(
                        "static-field", field, symbol, symbol?.ContainingType,
                        $"static generic field '{field.Declaration.Variables.FirstOrDefault()?.Identifier.ValueText}' was not replaced by a holder"));
                    break;
                }
                case CatchDeclarationSyntax catchDeclaration when IsTypeParameter(model, catchDeclaration.Type):
                {
                    var method = EnclosingMethodSymbol(catchDeclaration, model);
                    findings.Add(new Finding(
                        "catch-type-param", catchDeclaration, method, method?.ContainingType,
                        $"catch({catchDeclaration.Type}) was not lowered"));
                    break;
                }
                case DeclarationPatternSyntax declaration when IsTypeParameter(model, declaration.Type):
                {
                    var method = EnclosingMethodSymbol(declaration, model);
                    findings.Add(new Finding(
                        "type-param-pattern", declaration, method, method?.ContainingType,
                        $"type-parameter declaration pattern '{declaration}' was not lifted"));
                    break;
                }
            }
        }
    }

    // ---- rules 3/4: token sites / forwarded parameters must be anchored somewhere in the method ----

    private static void ScanMethodAnchorRules(SemanticModel model, SyntaxNode root, List<Finding> findings)
    {
        foreach (var method in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
        {
            var symbol = model.GetDeclaredSymbol(method);
            if (symbol == null || method.Body == null && method.ExpressionBody == null)
            {
                continue;
            }

            var scope = (SyntaxNode?)method.Body ?? method.ExpressionBody!;
            var anchored = CollectAnchoredParameterNames(scope);

            var directSites = new HashSet<string>(StringComparer.Ordinal);
            var forwarded = new HashSet<string>(StringComparer.Ordinal);
            foreach (var node in scope.DescendantNodes())
            {
                switch (node)
                {
                    case TypeOfExpressionSyntax typeOf when ResolveTypeParameterName(model, typeOf.Type) is { } typeofName:
                        // Holder accessors carry the token inline (`__cs2j_get_X<T>(typeof(T))`);
                        // the method needs no separate anchor for that token.
                        if (!IsHolderTokenArgument(typeOf)
                            && !CSharpToJava.Core.Analysis.ExpressionTreeFacts.IsInsideQuotedExpressionLambda(model, node))
                        {
                            directSites.Add(typeofName);
                        }

                        break;
                    case BinaryExpressionSyntax binary when binary.IsKind(SyntaxKind.IsExpression)
                            || binary.IsKind(SyntaxKind.AsExpression):
                        if (binary.Right is TypeSyntax binaryType
                            && ResolveTypeParameterName(model, binaryType) is { } binaryName
                            && !CSharpToJava.Core.Analysis.ExpressionTreeFacts.IsInsideQuotedExpressionLambda(model, node))
                        {
                            directSites.Add(binaryName);
                        }

                        break;
                    case InvocationExpressionSyntax invocation when !IsHolderAccessorInvocation(invocation)
                            && !CSharpToJava.Core.Analysis.ExpressionTreeFacts.IsInsideQuotedExpressionLambda(model, invocation):
                        foreach (var name in ForwardedTypeParameterNames(model, invocation))
                        {
                            forwarded.Add(name);
                        }

                        break;
                }
            }

            foreach (var name in directSites)
            {
                if (!anchored.Contains(name))
                {
                    findings.Add(new Finding(
                        "method-without-anchor", method, symbol, symbol.ContainingType,
                        $"method '{symbol.ToDisplayString()}' uses a token for type parameter '{name}' but no anchor is visible in the method"));
                }
            }

            foreach (var name in forwarded)
            {
                if (!directSites.Contains(name) && !anchored.Contains(name))
                {
                    findings.Add(new Finding(
                        "forward-only-without-anchor", method, symbol, symbol.ContainingType,
                        $"method '{symbol.ToDisplayString()}' only forwards type parameter '{name}' to a generic callee but carries no anchor"));
                }
            }
        }
    }

    /// <summary>Anchor statements carry the token/sentinel prefixes at any block depth in the scope.</summary>
    private static HashSet<string> CollectAnchoredParameterNames(SyntaxNode scope)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var local in scope.DescendantNodes().OfType<LocalDeclarationStatementSyntax>())
        {
            foreach (var variable in local.Declaration.Variables)
            {
                var identifier = variable.Identifier.ValueText;
                string? parameterName = null;
                if (identifier.StartsWith(GenericDeepenSyntaxFacts.AnchorPrefix, StringComparison.Ordinal))
                {
                    parameterName = identifier[GenericDeepenSyntaxFacts.AnchorPrefix.Length..];
                }
                else if (identifier.StartsWith(GenericDeepenSyntaxFacts.E10SentinelPrefix, StringComparison.Ordinal))
                {
                    parameterName = identifier[GenericDeepenSyntaxFacts.E10SentinelPrefix.Length..];
                }

                if (parameterName != null
                    && variable.Initializer?.Value is TypeOfExpressionSyntax typeOf
                    && typeOf.Type is IdentifierNameSyntax typeIdentifier)
                {
                    names.Add(typeIdentifier.Identifier.ValueText);
                }
            }
        }

        return names;
    }

    private static bool IsHolderTokenArgument(TypeOfExpressionSyntax typeOf)
        => typeOf.Parent is ArgumentSyntax { Parent: ArgumentListSyntax { Parent: InvocationExpressionSyntax invocation } }
            && IsHolderAccessorInvocation(invocation);

    private static bool IsHolderAccessorInvocation(InvocationExpressionSyntax invocation)
        => invocation.Expression switch
        {
            GenericNameSyntax generic => GenericDeepenSyntaxFacts.IsHolderAccessorName(generic.Identifier.ValueText),
            MemberAccessExpressionSyntax memberAccess =>
                GenericDeepenSyntaxFacts.IsHolderAccessorName(memberAccess.Name.Identifier.ValueText),
            _ => false,
        };

    private static IEnumerable<string> ForwardedTypeParameterNames(SemanticModel model, InvocationExpressionSyntax invocation)
    {
        switch (invocation.Expression)
        {
            case GenericNameSyntax genericName:
                foreach (var argument in genericName.TypeArgumentList.Arguments)
                {
                    if (ResolveTypeParameterName(model, argument) is { } name)
                    {
                        yield return name;
                    }
                }

                break;
            case MemberAccessExpressionSyntax { Name: GenericNameSyntax memberGeneric }:
                foreach (var argument in memberGeneric.TypeArgumentList.Arguments)
                {
                    if (ResolveTypeParameterName(model, argument) is { } name)
                    {
                        yield return name;
                    }
                }

                break;
        }
    }

    // ---- rule 5: structural self-check inside quoted expression trees ----

    private static void ScanExpressionTreeRewrites(SemanticModel model, SyntaxNode root, List<Finding> findings)
    {
        foreach (var local in root.DescendantNodes().OfType<LocalDeclarationStatementSyntax>())
        {
            var hasAnchor = local.Declaration.Variables.Any(v =>
                v.Identifier.ValueText.StartsWith(GenericDeepenSyntaxFacts.AnchorPrefix, StringComparison.Ordinal)
                || v.Identifier.ValueText.StartsWith(GenericDeepenSyntaxFacts.E10SentinelPrefix, StringComparison.Ordinal));
            if (hasAnchor && CSharpToJava.Core.Analysis.ExpressionTreeFacts.IsInsideQuotedExpressionLambda(model, local))
            {
                var method = EnclosingMethodSymbol(local, model);
                findings.Add(new Finding(
                    "exprtree-illegal-rewrite", local, method, method?.ContainingType,
                    "token anchor statement was inserted inside a quoted expression-tree lambda"));
            }
        }

        // GD-ISAS-B lowered shape: typeof(T).IsInstanceOfType(x).
        foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (invocation.Expression is MemberAccessExpressionSyntax
                {
                    Name.Identifier.ValueText: "IsInstanceOfType",
                    Expression: TypeOfExpressionSyntax,
                }
                && CSharpToJava.Core.Analysis.ExpressionTreeFacts.IsInsideQuotedExpressionLambda(model, invocation))
            {
                var method = EnclosingMethodSymbol(invocation, model);
                findings.Add(new Finding(
                    "exprtree-illegal-rewrite", invocation, method, method?.ContainingType,
                    "lowered IsInstanceOfType pattern call was inserted inside a quoted expression-tree lambda"));
            }
        }
    }

    // ---- rule 6: bare reads/writes of a field the holder rewrite removed ----

    private static void ScanBareFieldReferences(
        SemanticModel model,
        SyntaxNode root,
        HashSet<string> plannedFieldNames,
        List<Finding> findings)
    {
        if (plannedFieldNames.Count == 0)
        {
            return;
        }

        foreach (var identifier in root.DescendantNodes().OfType<IdentifierNameSyntax>())
        {
            if (!plannedFieldNames.Contains(identifier.Identifier.ValueText))
            {
                continue;
            }

            var info = model.GetSymbolInfo(identifier);
            if (info.Symbol != null || info.CandidateSymbols.Length > 0)
            {
                // Bound to something (a real same-named member, the field kept by a skip, etc.).
                continue;
            }

            var method = EnclosingMethodSymbol(identifier, model);
            findings.Add(new Finding(
                "holder-bare-field", identifier, method, method?.ContainingType,
                $"reference to removed holder field '{identifier.Identifier.ValueText}' was not rewritten to an accessor"));
        }
    }

    // ---- rule 3b: override/interface families must anchor the same method type parameters ----

    private static void ScanFamilyConsistency(Compilation rewritten, List<Finding> findings)
    {
        var families = BuildFamilies(rewritten);
        foreach (var members in families.Values)
        {
            // member -> method-entry-anchored own type-parameter ordinals.
            var anchoredPerMember = new List<(IMethodSymbol Member, HashSet<int> Anchored)>();
            foreach (var member in members)
            {
                var declaration = member.DeclaringSyntaxReferences
                    .Select(r => r.GetSyntax())
                    .OfType<MethodDeclarationSyntax>()
                    .FirstOrDefault();
                if (declaration?.Body == null)
                {
                    continue;
                }

                var ordinals = new HashSet<int>();
                var anchored = CollectAnchoredParameterNames(declaration.Body);
                foreach (var parameter in member.TypeParameters)
                {
                    if (anchored.Contains(parameter.Name))
                    {
                        ordinals.Add(parameter.Ordinal);
                    }
                }

                anchoredPerMember.Add((member, ordinals));
            }

            if (anchoredPerMember.Count <= 1)
            {
                continue;
            }

            var union = new HashSet<int>(anchoredPerMember.SelectMany(a => a.Anchored));
            if (union.Count == 0)
            {
                continue;
            }

            foreach (var (member, anchored) in anchoredPerMember)
            {
                if (union.IsProperSupersetOf(anchored))
                {
                    var memberDeclaration = member.DeclaringSyntaxReferences
                        .Select(r => r.GetSyntax())
                        .OfType<MethodDeclarationSyntax>()
                        .First();
                    findings.Add(new Finding(
                        "family-anchor-mismatch",
                        memberDeclaration,
                        member, member.ContainingType,
                        $"override/interface member '{member.ToDisplayString()}' anchors method type parameter(s) "
                        + string.Join(",", union.Where(o => !anchored.Contains(o)).Select(o => $"#{o}"))
                        + " while another family member does; family anchor sets disagree"));
                }
            }
        }
    }

    private static Dictionary<IMethodSymbol, HashSet<IMethodSymbol>> BuildFamilies(Compilation compilation)
    {
        var families = new Dictionary<IMethodSymbol, HashSet<IMethodSymbol>>(SymbolEqualityComparer.Default);

        void Add(IMethodSymbol root, IMethodSymbol member)
        {
            if (!families.TryGetValue(root, out var members))
            {
                members = new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default);
                families[root] = members;
            }

            members.Add(member);
        }

        foreach (var type in AllTypes(compilation.GlobalNamespace))
        {
            foreach (var member in type.GetMembers().OfType<IMethodSymbol>())
            {
                if (member is { MethodKind: MethodKind.Ordinary, IsOverride: true, OverriddenMethod: not null })
                {
                    var root = member;
                    while (root.OverriddenMethod != null)
                    {
                        root = root.OverriddenMethod;
                    }

                    Add(root.OriginalDefinition, root.OriginalDefinition);
                    Add(root.OriginalDefinition, member.OriginalDefinition);
                }
            }

            foreach (var interfaceMethod in type.AllInterfaces.SelectMany(i => i.GetMembers().OfType<IMethodSymbol>()))
            {
                if (type.FindImplementationForInterfaceMember(interfaceMethod) is IMethodSymbol implementation)
                {
                    Add(interfaceMethod.OriginalDefinition, interfaceMethod.OriginalDefinition);
                    Add(interfaceMethod.OriginalDefinition, implementation.OriginalDefinition);
                }
            }
        }

        return families;
    }

    private static IEnumerable<INamedTypeSymbol> AllTypes(INamespaceSymbol root)
    {
        foreach (var type in root.GetTypeMembers())
        {
            yield return type;
            foreach (var nested in AllTypes(type))
            {
                yield return nested;
            }
        }

        foreach (var ns in root.GetNamespaceMembers())
        {
            foreach (var type in AllTypes(ns))
            {
                yield return type;
            }
        }
    }

    private static IEnumerable<INamedTypeSymbol> AllTypes(INamedTypeSymbol type)
    {
        foreach (var nested in type.GetTypeMembers())
        {
            yield return nested;
            foreach (var deeper in AllTypes(nested))
            {
                yield return deeper;
            }
        }
    }

    // ---- report assembly: overrides suppression + Info traces + CS2J1120 diagnostics ----

    private static GenericDeepenResidualReport BuildReport(
        List<Finding> findings,
        GenericDeepenOptions options,
        GenericDeepenOverrides? overrides)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var survivors = new List<Finding>();
        var diagnostics = new List<GenericDeepenDiagnostic>();
        var suppressedTraces = new HashSet<string>(StringComparer.Ordinal);

        foreach (var finding in findings)
        {
            if (overrides is not null
                && overrides.IsSuppressed(finding.Member, finding.Type ?? finding.Member?.ContainingType,
                    out var matchedEntry, out var force))
            {
                var traceKey = finding.Rule + "|" + matchedEntry;
                if (options.ReportInfo && suppressedTraces.Add(traceKey))
                {
                    diagnostics.Add(new GenericDeepenDiagnostic(
                        GenericDeepenSeverity.Info, GenericDeepenRule.Residual, GenericDeepenCodes.Info,
                        $"Residual rule '{finding.Rule}' suppressed by {(force ? "force" : "skip")} override '{matchedEntry}': {finding.Detail}",
                        finding.Node.SyntaxTree?.FilePath,
                        finding.Node.GetLocation().GetLineSpan().StartLinePosition.Line + 1));
                }

                continue;
            }

            survivors.Add(finding);
            counts.TryGetValue(finding.Rule, out var current);
            counts[finding.Rule] = current + 1;
        }

        var severity = options.Strict ? GenericDeepenSeverity.Error : GenericDeepenSeverity.Warning;
        foreach (var (rule, count) in counts)
        {
            var first = survivors.First(f => f.Rule == rule);
            diagnostics.Add(new GenericDeepenDiagnostic(
                severity, GenericDeepenRule.Residual, GenericDeepenCodes.ResidualFound,
                $"{count} residual site(s) of rule '{rule}' remain after generic-deepen; the rewrite did not cover them (spec §4.5).",
                first.Node.SyntaxTree?.FilePath,
                first.Node.GetLocation().GetLineSpan().StartLinePosition.Line + 1));
        }

        return new GenericDeepenResidualReport(counts, diagnostics);
    }

    private static IMethodSymbol? EnclosingMethodSymbol(SyntaxNode node, SemanticModel model)
    {
        var declaration = node.AncestorsAndSelf().OfType<MethodDeclarationSyntax>().FirstOrDefault();
        return declaration == null ? null : model.GetDeclaredSymbol(declaration);
    }

    private static bool IsTypeParameter(SemanticModel model, TypeSyntax? type)
        => ResolveTypeParameterName(model, type ?? SyntaxFactory.ParseTypeName("int")) != null;

    private static string? ResolveTypeParameterName(SemanticModel model, TypeSyntax type)
    {
        var syntax = type;
        if (syntax is NullableTypeSyntax nullable) syntax = nullable.ElementType;
        if (syntax is ArrayTypeSyntax array) syntax = array.ElementType;
        if (syntax is not IdentifierNameSyntax identifier) return null;
        return model.GetSymbolInfo(identifier).Symbol is ITypeParameterSymbol ? identifier.Identifier.ValueText : null;
    }

    private static bool ReferencesVisibleTypeParameter(SemanticModel model, TypeSyntax type)
    {
        foreach (var identifier in type.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>())
        {
            if (model.GetSymbolInfo(identifier).Symbol is ITypeParameterSymbol) return true;
        }

        return false;
    }
}
