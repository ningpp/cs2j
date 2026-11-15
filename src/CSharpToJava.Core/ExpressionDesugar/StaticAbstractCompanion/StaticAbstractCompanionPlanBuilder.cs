using System.Collections.Immutable;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.ExpressionDesugar.StaticAbstractCompanion;

/// <summary>
/// Builds the whole-tree companion plan for C# 11 interface
/// <c>static abstract</c>/<c>static virtual</c> members.
///
/// The lowering removes the Java-inexpressible static dispatch from the
/// intermediate tree:
/// <list type="number">
/// <item>the interface's static abstract members are replaced by an instance
/// companion interface (<c>__cs2j_Companion_&lt;I&gt;</c>);</item>
/// <item>each implementor gets a companion class forwarding to its static
/// members (<c>__cs2j_Companion_&lt;I&gt;_Impl&lt;n&gt;</c>);</item>
/// <item>a static dispatch helper (<c>__cs2j_Companion_&lt;I&gt;_Dispatch&lt;TImpl&gt;</c>)
/// maps <c>typeof(TImpl)</c> to the companion instance with a <c>typeof</c>/
/// <c>MakeGenericType</c> switch (no reflection on members, no ref-struct
/// boxing);</item>
/// <item>call sites on a type parameter are rewritten to
/// <c>__cs2j_Companion_&lt;I&gt;_Dispatch&lt;..., T&gt;.Instance.Member</c>.</item>
/// </list>
///
/// An interface is lowered only when the whole plan is provably safe: every
/// implementor must be in source and accessible from the interface's scope, no
/// explicit static interface implementations, no static virtual bodies, and
/// every call site must have a type-parameter receiver. Otherwise the interface
/// is left untouched and reported so the C36 guard keeps its CS2J0585.
/// </summary>
public static class StaticAbstractCompanionPlanBuilder
{
    private static readonly SymbolDisplayFormat TypeFormat =
        SymbolDisplayFormat.FullyQualifiedFormat
            .WithMiscellaneousOptions(
                SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers
                | SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier
                | SymbolDisplayMiscellaneousOptions.UseSpecialTypes);

    public static StaticAbstractCompanionPlan Build(CSharpCompilation compilation)
    {
        var statistics = new StaticAbstractCompanionStatistics();
        var diagnostics = new List<StaticAbstractCompanionDiagnostic>();

        var allTypes = EnumerateSourceTypes(compilation).ToList();
        var candidates = allTypes
            .Where(static t => t.TypeKind == TypeKind.Interface)
            .Where(HasStaticAbstractOrVirtualMember)
            .Distinct(SymbolEqualityComparer.Default)
            .Cast<INamedTypeSymbol>()
            .OrderBy(static t => t.ToDisplayString(), StringComparer.Ordinal)
            .ToList();

        if (candidates.Count == 0)
            return StaticAbstractCompanionPlan.Empty;

        statistics.InterfacesFound = candidates.Count;

        // One semantic pass collects the reference sites of every candidate
        // member name. Only syntactic candidates whose name text matches pay the
        // binding cost, so large clean trees stay cheap.
        var siteIndex = CollectReferenceSites(compilation, candidates, diagnostics);
        foreach (var bad in siteIndex.UnsupportedSites)
        {
            diagnostics.Add(new StaticAbstractCompanionDiagnostic(
                StaticAbstractCompanionSeverity.Warning,
                StaticAbstractCompanionCodes.CallSiteSkipped,
                bad,
                null));
        }

        var byInterface = ImmutableDictionary.CreateBuilder<ISymbol, StaticAbstractCompanionInterfacePlan>(
            SymbolEqualityComparer.Default);
        var memberDefinitions = ImmutableHashSet.CreateBuilder<ISymbol>(SymbolEqualityComparer.Default);
        var interfaceNames = ImmutableArray.CreateBuilder<string>();
        var siteTokens = ImmutableHashSet.CreateBuilder<string>(StringComparer.Ordinal);

        foreach (var iface in candidates)
        {
            var implemented = allTypes
                .Where(t => t.TypeKind is TypeKind.Class or TypeKind.Struct
                    && t.AllInterfaces.Any(i =>
                        SymbolEqualityComparer.Default.Equals(i.OriginalDefinition, iface)))
                .Distinct(SymbolEqualityComparer.Default)
                .Cast<INamedTypeSymbol>()
                .OrderBy(static t => t.ToDisplayString(), StringComparer.Ordinal)
                .ToList();

            var reasons = new List<string>();
            var members = GetStaticAbstractOrVirtualMembers(iface).ToList();

            if (implemented.Count == 0)
                reasons.Add("no implementor in the conversion closure (open-world interface)");
            if (siteIndex.UnsupportedInterfaces.Contains(iface, SymbolEqualityComparer.Default))
                reasons.Add("a call site has no type-parameter receiver");

            foreach (var member in members)
            {
                if (!member.IsAbstract && member is not IPropertySymbol { SetMethod.IsInitOnly: true })
                    reasons.Add($"static virtual member '{member.Name}' carries a default body");
                if (member is IMethodSymbol method)
                {
                    if (method.Name.StartsWith("op_Checked", StringComparison.Ordinal))
                        reasons.Add($"checked operator '{method.Name}' is not lowered");
                    if (method.Name is "op_Implicit" or "op_CheckedImplicit")
                        reasons.Add("implicit conversion call sites are not lowered");
                    if (method.Name is "op_Increment" or "op_Decrement")
                        reasons.Add($"increment/decrement operator '{method.Name}' needs write-back lowering");
                    if (method.Name is "op_True" or "op_False")
                        reasons.Add($"'{method.Name}' has no direct call syntax");
                }
                if (member is IPropertySymbol property && property.SetMethod?.IsInitOnly == true)
                    reasons.Add($"init-only property '{property.Name}' cannot be forwarded");
                if (member is IPropertySymbol { IsIndexer: true })
                    reasons.Add("static abstract indexers do not exist in C#");
                if (member is IEventSymbol eventSymbol)
                {
                    if (!eventSymbol.AddMethod!.IsAbstract || !eventSymbol.RemoveMethod!.IsAbstract)
                        reasons.Add($"event '{eventSymbol.Name}' has custom accessors");
                }
            }

            var within = (ISymbol?)iface.ContainingType ?? iface.ContainingAssembly;
            foreach (var implementor in implemented)
            {
                if (!compilation.IsSymbolAccessibleWithin(implementor, within!))
                    reasons.Add($"implementor '{implementor.ToDisplayString()}' is not accessible from the interface scope");
                if (HasExplicitStaticInterfaceImplementation(implementor, members))
                    reasons.Add($"implementor '{implementor.ToDisplayString()}' implements a member explicitly");
                if (!IsScopeReachable(iface, implementor))
                    reasons.Add($"implementor '{implementor.ToDisplayString()}' is nested in a type parameter scope that the generated companion cannot reference");
            }

            if (reasons.Count > 0)
            {
                statistics.InterfacesSkipped++;
                diagnostics.Add(new StaticAbstractCompanionDiagnostic(
                    StaticAbstractCompanionSeverity.Warning,
                    StaticAbstractCompanionCodes.InterfaceSkipped,
                    $"Interface '{iface.ToDisplayString()}' keeps its static abstract/virtual members: " +
                    string.Join("; ", reasons.Distinct()) + "."));
                continue;
            }

            var declaringTree = iface.DeclaringSyntaxReferences
                .Select(static r => r.SyntaxTree.FilePath)
                .FirstOrDefault(static p => !string.IsNullOrEmpty(p));
            if (string.IsNullOrEmpty(declaringTree))
            {
                statistics.InterfacesSkipped++;
                diagnostics.Add(new StaticAbstractCompanionDiagnostic(
                    StaticAbstractCompanionSeverity.Warning,
                    StaticAbstractCompanionCodes.InterfaceSkipped,
                    $"Interface '{iface.ToDisplayString()}' has no source declaration file."));
                continue;
            }

            var plan = GenerateInterfacePlan(compilation, iface, members, implemented, declaringTree);
            if (plan == null)
            {
                statistics.InterfacesSkipped++;
                diagnostics.Add(new StaticAbstractCompanionDiagnostic(
                    StaticAbstractCompanionSeverity.Warning,
                    StaticAbstractCompanionCodes.InterfaceSkipped,
                    $"Interface '{iface.ToDisplayString()}' generated companion code that does not parse; " +
                    "the interface keeps its static abstract/virtual members."));
                continue;
            }

            byInterface[iface] = plan;
            statistics.InterfacesLowered++;
            statistics.Implementors += implemented.Count;
            foreach (var member in members)
            {
                memberDefinitions.Add(member.OriginalDefinition);
                siteTokens.Add(member.Name);
                switch (member)
                {
                    case IMethodSymbol { MethodKind: MethodKind.Conversion }:
                        statistics.Conversions++;
                        break;
                    case IMethodSymbol { MethodKind: MethodKind.UserDefinedOperator or MethodKind.BuiltinOperator }:
                        statistics.Operators++;
                        break;
                    case IMethodSymbol:
                        statistics.Methods++;
                        break;
                    case IPropertySymbol:
                        statistics.Properties++;
                        break;
                    case IEventSymbol:
                        statistics.Events++;
                        break;
                }
            }

            interfaceNames.Add(iface.Name);
            diagnostics.Add(new StaticAbstractCompanionDiagnostic(
                StaticAbstractCompanionSeverity.Info,
                StaticAbstractCompanionCodes.InterfaceLowered,
                $"Lowered interface '{iface.ToDisplayString()}' with {implemented.Count} implementor(s) " +
                $"into companion dispatch."));
        }

        return new StaticAbstractCompanionPlan
        {
            ByInterface = byInterface.ToImmutable(),
            MemberDefinitions = memberDefinitions.ToImmutable(),
            InterfaceNames = interfaceNames.ToImmutable(),
            SiteTokens = siteTokens.ToImmutableArray(),
            RequiresFullScan = byInterface.Values
                .SelectMany(static p => p.LoweredMembers)
                .Any(static m => m is IMethodSymbol
                {
                    MethodKind: MethodKind.UserDefinedOperator or MethodKind.Conversion,
                }),
            Statistics = statistics,
            Diagnostics = diagnostics.ToImmutableArray(),
        };
    }

    // ------------------------------------------------------------------
    // discovery
    // ------------------------------------------------------------------

    private static IEnumerable<INamedTypeSymbol> EnumerateSourceTypes(CSharpCompilation compilation)
    {
        foreach (var tree in compilation.SyntaxTrees)
        {
            var model = compilation.GetSemanticModel(tree);
            foreach (var declaration in tree.GetRoot().DescendantNodes())
            {
                if (declaration is BaseTypeDeclarationSyntax or DelegateDeclarationSyntax)
                {
                    var symbol = model.GetDeclaredSymbol(declaration);
                    if (symbol is INamedTypeSymbol named && named.Locations.Any(static l => l.IsInSource))
                        yield return named;
                }
            }
        }
    }

    private static bool HasStaticAbstractOrVirtualMember(INamedTypeSymbol iface)
        => iface.GetMembers().Any(IsStaticAbstractOrVirtualInterfaceMember);

    private static IEnumerable<ISymbol> GetStaticAbstractOrVirtualMembers(INamedTypeSymbol iface)
        => iface.GetMembers().Where(IsStaticAbstractOrVirtualInterfaceMember);

    private static bool IsStaticAbstractOrVirtualInterfaceMember(ISymbol symbol)
    {
        if (!symbol.IsStatic || (!symbol.IsAbstract && !symbol.IsVirtual))
            return false;

        // Property/event accessor methods are surfaced by GetMembers() as well;
        // they are forwarded through their owning property/event.
        if (symbol is IMethodSymbol { AssociatedSymbol: not null })
            return false;

        return symbol.ContainingType?.TypeKind == TypeKind.Interface;
    }

    private static bool HasExplicitStaticInterfaceImplementation(
        INamedTypeSymbol implementor, IReadOnlyList<ISymbol> members)
    {
        foreach (var member in implementor.GetMembers())
        {
            var explicitImplementations = member switch
            {
                IMethodSymbol method => method.ExplicitInterfaceImplementations.Cast<ISymbol>(),
                IPropertySymbol property => property.ExplicitInterfaceImplementations.Cast<ISymbol>(),
                IEventSymbol eventSymbol => eventSymbol.ExplicitInterfaceImplementations.Cast<ISymbol>(),
                _ => Enumerable.Empty<ISymbol>(),
            };

            if (explicitImplementations.Any(e => members.Contains(e.OriginalDefinition, SymbolEqualityComparer.Default)))
                return true;
        }

        return false;
    }

    /// <summary>
    /// A companion is generated inside the interface's containing type (nested
    /// interface) or namespace (top-level interface). An implementor nested
    /// inside a different generic type cannot be named from that scope.
    /// </summary>
    private static bool IsScopeReachable(INamedTypeSymbol iface, INamedTypeSymbol implementor)
    {
        var ifaceScope = iface.ContainingType;
        for (var containing = implementor.ContainingType; containing != null; containing = containing.ContainingType)
        {
            if (SymbolEqualityComparer.Default.Equals(containing, ifaceScope))
                return true;
            if (containing.TypeParameters.Length > 0)
                return false;
        }

        return true;
    }

    // ------------------------------------------------------------------
    // call-site feasibility scan
    // ------------------------------------------------------------------

    private sealed class ReferenceSiteIndex
    {
        public HashSet<ISymbol> UnsupportedInterfaces { get; } =
            new(SymbolEqualityComparer.Default);

        public List<string> UnsupportedSites { get; } = new();
    }

    private static ReferenceSiteIndex CollectReferenceSites(
        CSharpCompilation compilation,
        IReadOnlyList<INamedTypeSymbol> candidates,
        List<StaticAbstractCompanionDiagnostic> diagnostics)
    {
        var index = new ReferenceSiteIndex();
        var memberNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var candidate in candidates)
        {
            foreach (var member in GetStaticAbstractOrVirtualMembers(candidate))
                memberNames.Add(member.Name);
        }

        foreach (var tree in compilation.SyntaxTrees)
        {
            var text = tree.GetText().ToString();
            if (!memberNames.Any(name => text.Contains(name, StringComparison.Ordinal)))
                continue;

            var model = compilation.GetSemanticModel(tree);
            foreach (var access in tree.GetRoot().DescendantNodes().OfType<MemberAccessExpressionSyntax>())
            {
                var name = access.Name.Identifier.ValueText;
                if (!memberNames.Contains(name))
                    continue;

                var symbol = model.GetSymbolInfo(access.Name).Symbol;
                if (symbol is null)
                    continue;

                var original = symbol.OriginalDefinition;
                var owningInterface = original.ContainingType?.OriginalDefinition;
                if (owningInterface is null
                    || !candidates.Contains(owningInterface, SymbolEqualityComparer.Default))
                {
                    continue;
                }

                var receiver = model.GetSymbolInfo(access.Expression).Symbol;
                if (receiver is not ITypeParameterSymbol)
                {
                    index.UnsupportedInterfaces.Add(owningInterface);
                    index.UnsupportedSites.Add(
                        $"{Path.GetFileName(tree.FilePath)}: '{access}' binds '{original.ToDisplayString()}' " +
                        "through a non-type-parameter receiver; the interface keeps its static abstract members.");
                }
            }
        }

        return index;
    }

    // ------------------------------------------------------------------
    // code generation
    // ------------------------------------------------------------------

    private static StaticAbstractCompanionInterfacePlan? GenerateInterfacePlan(
        CSharpCompilation compilation,
        INamedTypeSymbol iface,
        IReadOnlyList<ISymbol> members,
        IReadOnlyList<INamedTypeSymbol> implementors,
        string declaringTree)
    {
        var companionName = UniqueName(iface.ContainingType, $"__cs2j_Companion_{iface.Name}", compilation);
        var dispatchName = UniqueName(iface.ContainingType, $"__cs2j_Companion_{iface.Name}_Dispatch", compilation);
        var access = AccessibilityText(iface.DeclaredAccessibility);
        var ifaceTypeParameters = RenderTypeParameterList(iface.TypeParameters);
        var ifaceConstraints = RenderConstraints(iface.TypeParameters, indent: string.Empty);

        var companionInterface = new StringBuilder();
        companionInterface.Append(access).Append(" interface ").Append(companionName)
            .Append(ifaceTypeParameters).AppendLine();
        if (!string.IsNullOrEmpty(ifaceConstraints))
            companionInterface.AppendLine(ifaceConstraints);
        companionInterface.AppendLine("{");
        foreach (var member in members)
        {
            var rendered = RenderCompanionInterfaceMember(member);
            if (rendered == null)
                return null;
            companionInterface.Append("    ").AppendLine(rendered);
        }
        companionInterface.AppendLine("}");

        var declarations = new StringBuilder();
        declarations.AppendLine(companionInterface.ToString());

        var implementorBranches = new List<(INamedTypeSymbol Implementor, string CompanionTypeName, bool Generic)>();
        var index = 0;
        foreach (var implementor in implementors)
        {
            var constructed = implementor.AllInterfaces.First(i =>
                SymbolEqualityComparer.Default.Equals(i.OriginalDefinition, iface));
            var companionImplName = $"__cs2j_Companion_{iface.Name}_Impl{index++}";
            var implTypeParams = RenderTypeParameterList(implementor.TypeParameters);
            var implConstraints = RenderConstraints(implementor.TypeParameters, indent: "    ");
            var companionArgs = string.Join(
                ", ",
                constructed.TypeArguments.Select(static a => a.ToDisplayString(TypeFormat)));

            declarations.Append(access).Append(" sealed class ").Append(companionImplName)
                .Append(implTypeParams)
                .Append(" : ").Append(companionName);
            if (companionArgs.Length > 0)
                declarations.Append('<').Append(companionArgs).Append('>');
            if (!string.IsNullOrEmpty(implConstraints))
                declarations.Append(' ').Append(implConstraints.Trim());
            declarations.AppendLine();
            declarations.AppendLine("{");
            foreach (var member in members)
            {
                var forwarding = RenderForwardingMember(member, implementor, constructed);
                if (forwarding == null)
                    return null;
                declarations.Append("    ").AppendLine(forwarding);
            }
            declarations.AppendLine("}");

            implementorBranches.Add((
                implementor,
                companionImplName,
                implementor.TypeParameters.Length > 0));
        }

        var dispatchTypeParameters = new List<string>();
        foreach (var typeParameter in iface.TypeParameters)
            dispatchTypeParameters.Add(RenderTypeParameter(typeParameter));
        dispatchTypeParameters.Add("TImpl");
        var dispatchConstraints = RenderConstraints(iface.TypeParameters, indent: string.Empty);

        var instanceType = companionName + RenderTypeParameterUsage(iface.TypeParameters);
        declarations.Append(access).Append(" static class ").Append(dispatchName)
            .Append('<').Append(string.Join(", ", dispatchTypeParameters)).AppendLine(">");
        if (!string.IsNullOrEmpty(dispatchConstraints))
            declarations.AppendLine(dispatchConstraints);
        declarations.AppendLine("{");
        declarations.Append("    public static readonly ").Append(instanceType)
            .AppendLine(" Instance = __Create();");
        declarations.Append("    private static ").Append(instanceType).AppendLine(" __Create()");
        declarations.AppendLine("    {");
        declarations.AppendLine("        global::System.Type t = typeof(TImpl);");
        foreach (var branch in implementorBranches)
        {
            var display = branch.Implementor.ToDisplayString(TypeFormat);
            if (!branch.Generic)
            {
                declarations.Append("        if (t == typeof(").Append(display).AppendLine("))");
                declarations.Append("            return (").Append(instanceType).Append(")(object)new ")
                    .Append(branch.CompanionTypeName).AppendLine("();");
            }
            else
            {
                declarations.Append("        if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(")
                    .Append(RenderOpenGeneric(branch.Implementor)).AppendLine("))");
                declarations.Append("            return (").Append(instanceType).Append(")global::System.Activator.CreateInstance(")
                    .Append("typeof(").Append(branch.CompanionTypeName).Append("<>).MakeGenericType(t.GetGenericArguments()))!;");
                declarations.AppendLine();
            }
        }
        declarations.Append("        throw new global::System.NotSupportedException(\"No companion registered for \" + t.FullName);")
            .AppendLine();
        declarations.AppendLine("    }");
        declarations.AppendLine("}");

        var generated = SyntaxFactory.ParseCompilationUnit(declarations.ToString())
            .Members
            .ToImmutableArray();
        if (generated.Length == 0)
            return null;

        var reference = iface.ContainingType != null
            ? dispatchName
            : (iface.ContainingNamespace is { IsGlobalNamespace: false } ns
                ? $"global::{ns.ToDisplayString()}.{dispatchName}"
                : dispatchName);

        return new StaticAbstractCompanionInterfacePlan
        {
            Interface = iface,
            CompanionInterfaceName = companionName,
            DispatchName = dispatchName,
            DispatchReference = reference,
            InterfaceTypeParameterCount = iface.TypeParameters.Length,
            LoweredMembers = members.Select(static m => m.OriginalDefinition).ToImmutableArray(),
            GeneratedMembers = generated,
            DeclaringFilePath = declaringTree,
        };
    }

    private static string? RenderCompanionInterfaceMember(ISymbol member)
    {
        switch (member)
        {
            case IMethodSymbol method:
            {
                var signature = RenderMethodSignature(method);
                return "public " + signature + ";";
            }
            case IPropertySymbol property when !property.IsIndexer:
            {
                var accessors = new List<string>();
                if (property.GetMethod != null)
                    accessors.Add(property.ReturnsByRef || property.ReturnsByRefReadonly ? "get;" : "get;");
                if (property.SetMethod != null)
                    accessors.Add(property.SetMethod.IsInitOnly ? "init;" : "set;");
                if (accessors.Count == 0)
                    return null;
                return $"{RenderPropertyType(property)} {Escape(property.Name)} {{ {string.Join(" ", accessors)} }}";
            }
            case IEventSymbol eventSymbol:
                return $"event {eventSymbol.Type.ToDisplayString(TypeFormat)} {Escape(eventSymbol.Name)};";
            default:
                return null;
        }
    }

    private static string? RenderForwardingMember(
        ISymbol member,
        INamedTypeSymbol implementor,
        INamedTypeSymbol constructedInterface)
    {
        // The companion class only declares the implementor's own type
        // parameters (PlainLoader<T>), so signatures must use the constructed
        // interface member (Load(T), not Load(TLeft)).
        var constructedMember = ResolveConstructedMember(constructedInterface, member);
        var implType = implementor.ToDisplayString(TypeFormat);
        var selfTypes = new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default);
        selfTypes.Add(constructedInterface);
        foreach (var typeParameter in constructedInterface.TypeArguments)
        {
            if (typeParameter is ITypeParameterSymbol)
                selfTypes.Add(typeParameter);
        }

        switch (constructedMember)
        {
            case IMethodSymbol method when method.MethodKind is not (MethodKind.UserDefinedOperator or MethodKind.Conversion):
            {
                var parameters = method.Parameters;
                var args = parameters.Select(p => RefArgument(p)).ToList();
                var typeArguments = method.TypeParameters.Length == 0
                    ? string.Empty
                    : "<" + string.Join(", ", method.TypeParameters.Select(static t => Escape(t.Name))) + ">";
                return $"public {RenderMethodSignature(method)} => {implType}.{Escape(method.Name)}{typeArguments}({string.Join(", ", args)});";
            }
            case IPropertySymbol property when !property.IsIndexer:
            {
                if (property.SetMethod?.IsInitOnly == true)
                    return null;

                if (property.GetMethod == null)
                    return $"public {RenderPropertyType(property)} {Escape(property.Name)} {{ set => {implType}.{Escape(property.Name)} = value; }}";

                if (property.SetMethod == null)
                {
                    var refPrefix = property.ReturnsByRef ? "ref " : property.ReturnsByRefReadonly ? "ref readonly " : string.Empty;
                    var refCall = property.ReturnsByRef || property.ReturnsByRefReadonly ? "ref " : string.Empty;
                    return $"public {RenderPropertyType(property)} {Escape(property.Name)} => {refCall}{implType}.{Escape(property.Name)};";
                }

                return $"public {RenderPropertyType(property)} {Escape(property.Name)} " +
                       $"{{ get => {implType}.{Escape(property.Name)}; set => {implType}.{Escape(property.Name)} = value; }}";
            }
            case IEventSymbol eventSymbol:
                return $"public event {eventSymbol.Type.ToDisplayString(TypeFormat)} {Escape(eventSymbol.Name)} " +
                       $"{{ add => {implType}.{Escape(eventSymbol.Name)} += value; " +
                       $"remove => {implType}.{Escape(eventSymbol.Name)} -= value; }}";
            case IMethodSymbol method when method.MethodKind == MethodKind.UserDefinedOperator:
                return RenderOperatorForwarding(method, implType, selfTypes);
            case IMethodSymbol method when method.MethodKind == MethodKind.Conversion:
                return RenderConversionForwarding(method, implType, selfTypes);
            default:
                return null;
        }
    }

    private static ISymbol ResolveConstructedMember(
        INamedTypeSymbol constructedInterface,
        ISymbol member)
    {
        foreach (var candidate in constructedInterface.GetMembers(member.Name))
        {
            if (SymbolEqualityComparer.Default.Equals(
                    candidate.OriginalDefinition, member.OriginalDefinition))
            {
                return candidate;
            }
        }

        return member;
    }

    private static string? RenderOperatorForwarding(
        IMethodSymbol op, string implType, HashSet<ITypeSymbol> selfTypes)
    {
        var returnType = op.ReturnType.ToDisplayString(TypeFormat);
        var parameters = op.Parameters;
        string Operand(IParameterSymbol parameter)
            => selfTypes.Contains(parameter.Type)
                ? $"({implType}){Escape(parameter.Name)}"
                : Escape(parameter.Name);

        if (op.Parameters.Length == 2)
        {
            var left = Operand(parameters[0]);
            var right = Operand(parameters[1]);
            var binary = op.Name switch
            {
                "op_Addition" => $"{left} + {right}",
                "op_Subtraction" => $"{left} - {right}",
                "op_Multiply" => $"{left} * {right}",
                "op_Division" => $"{left} / {right}",
                "op_Modulus" => $"{left} % {right}",
                "op_BitwiseAnd" => $"{left} & {right}",
                "op_BitwiseOr" => $"{left} | {right}",
                "op_ExclusiveOr" => $"{left} ^ {right}",
                "op_LeftShift" => $"{left} << {right}",
                "op_RightShift" => $"{left} >> {right}",
                "op_UnsignedRightShift" => $"{left} >>> {right}",
                "op_Equality" => $"{left} == {right}",
                "op_Inequality" => $"{left} != {right}",
                "op_LessThan" => $"{left} < {right}",
                "op_LessThanOrEqual" => $"{left} <= {right}",
                "op_GreaterThan" => $"{left} > {right}",
                "op_GreaterThanOrEqual" => $"{left} >= {right}",
                _ => null,
            };
            if (binary == null)
                return null;
            return $"public {RenderMethodSignature(op)} => ({returnType})({binary});";
        }

        if (op.Parameters.Length == 1)
        {
            var operand = Operand(parameters[0]);
            var unary = op.Name switch
            {
                "op_UnaryNegation" => $"-{operand}",
                "op_UnaryPlus" => $"+{operand}",
                "op_LogicalNot" => $"!{operand}",
                "op_OnesComplement" => $"~{operand}",
                _ => null,
            };
            if (unary == null)
                return null;
            return $"public {RenderMethodSignature(op)} => ({returnType})({unary});";
        }

        return null;
    }

    private static string? RenderConversionForwarding(
        IMethodSymbol conversion, string implType, HashSet<ITypeSymbol> selfTypes)
    {
        if (conversion.Parameters.Length != 1)
            return null;

        var returnType = conversion.ReturnType.ToDisplayString(TypeFormat);
        var parameter = conversion.Parameters[0];
        var operand = selfTypes.Contains(parameter.Type)
            ? $"({implType}){Escape(parameter.Name)}"
            : Escape(parameter.Name);
        return $"public {RenderMethodSignature(conversion)} => ({returnType})({operand});";
    }

    private static string RenderMethodSignature(IMethodSymbol method)
    {
        var typeParameters = RenderTypeParameterList(method.TypeParameters);
        var returnPrefix = method.ReturnsByRef
            ? "ref "
            : method.ReturnsByRefReadonly
                ? "ref readonly "
                : string.Empty;
        var returnType = method.ReturnType.ToDisplayString(TypeFormat);
        var parameters = string.Join(", ", method.Parameters.Select(RenderParameter));
        var constraints = RenderConstraints(method.TypeParameters, indent: string.Empty);
        var signature = $"{returnPrefix}{returnType} {Escape(method.Name)}{typeParameters}({parameters})";
        if (!string.IsNullOrEmpty(constraints))
            signature += " " + constraints;
        return signature;
    }

    private static string RenderPropertyType(IPropertySymbol property)
    {
        var prefix = property.ReturnsByRef
            ? "ref "
            : property.ReturnsByRefReadonly
                ? "ref readonly "
                : string.Empty;
        return prefix + property.Type.ToDisplayString(TypeFormat);
    }

    private static string RenderParameter(IParameterSymbol parameter)
    {
        var scoped = parameter.ScopedKind == ScopedKind.ScopedValue ? "scoped " : string.Empty;
        var modifier = parameter.RefKind switch
        {
            RefKind.Ref => "ref ",
            RefKind.Out => "out ",
            RefKind.In => "in ",
            RefKind.RefReadOnlyParameter => "ref readonly ",
            _ => string.Empty,
        };
        var @params = parameter.IsParams ? "params " : string.Empty;
        var defaultValue = parameter.HasExplicitDefaultValue
            ? " = " + RenderDefaultValue(parameter)
            : string.Empty;
        return $"{scoped}{@params}{modifier}{parameter.Type.ToDisplayString(TypeFormat)} " +
               $"{Escape(parameter.Name)}{defaultValue}";
    }

    private static string RenderDefaultValue(IParameterSymbol parameter)
    {
        var value = parameter.ExplicitDefaultValue;
        if (value is null)
            return parameter.Type.IsValueType ? "default" : "null";
        if (parameter.Type.TypeKind == TypeKind.Enum && parameter.Type is INamedTypeSymbol enumType)
        {
            foreach (var member in enumType.GetMembers().OfType<IFieldSymbol>())
            {
                if (!member.HasConstantValue || !Equals(member.ConstantValue, value))
                    continue;
                return $"{enumType.ToDisplayString(TypeFormat)}.{Escape(member.Name)}";
            }
            return $"({enumType.ToDisplayString(TypeFormat)})({value})";
        }

        return value switch
        {
            bool b => b ? "true" : "false",
            string s => SymbolDisplay.FormatLiteral(s, quote: true),
            char c => SymbolDisplay.FormatLiteral(c, quote: true),
            float f => f.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + "F",
            double d => d.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + "D",
            decimal m => m.ToString(System.Globalization.CultureInfo.InvariantCulture) + "M",
            long l => l.ToString(System.Globalization.CultureInfo.InvariantCulture) + "L",
            ulong ul => ul.ToString(System.Globalization.CultureInfo.InvariantCulture) + "UL",
            uint ui => ui.ToString(System.Globalization.CultureInfo.InvariantCulture) + "U",
            _ => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? "default",
        };
    }

    private static string RenderTypeParameterList(ImmutableArray<ITypeParameterSymbol> typeParameters)
    {
        if (typeParameters.Length == 0)
            return string.Empty;

        return "<" + string.Join(
            ", ",
            typeParameters.Select(static t => RenderTypeParameter(t))) + ">";
    }

    private static string RenderTypeParameter(ITypeParameterSymbol typeParameter)
    {
        var variance = typeParameter.Variance switch
        {
            VarianceKind.In => "in ",
            VarianceKind.Out => "out ",
            _ => string.Empty,
        };
        return variance + Escape(typeParameter.Name);
    }

    private static string RenderTypeParameterUsage(ImmutableArray<ITypeParameterSymbol> typeParameters)
        => typeParameters.Length == 0
            ? string.Empty
            : "<" + string.Join(", ", typeParameters.Select(static t => Escape(t.Name))) + ">";

    private static string RenderConstraints(ImmutableArray<ITypeParameterSymbol> typeParameters, string indent)
    {
        var clauses = new List<string>();
        foreach (var typeParameter in typeParameters)
        {
            var parts = new List<string>();
            if (typeParameter.HasUnmanagedTypeConstraint)
                parts.Add("unmanaged");
            else if (typeParameter.HasValueTypeConstraint)
                parts.Add("struct");
            else if (typeParameter.HasReferenceTypeConstraint)
                parts.Add(typeParameter.ReferenceTypeConstraintNullableAnnotation == NullableAnnotation.Annotated
                    ? "class?"
                    : "class");

            if (typeParameter.HasNotNullConstraint)
                parts.Add("notnull");

            foreach (var constraintType in typeParameter.ConstraintTypes)
                parts.Add(constraintType.ToDisplayString(TypeFormat));

            if (typeParameter.HasConstructorConstraint)
                parts.Add("new()");

            if (parts.Count > 0)
                clauses.Add($"{indent}where {Escape(typeParameter.Name)} : {string.Join(", ", parts)}");
        }

        return string.Join(Environment.NewLine, clauses);
    }

    private static string RefArgument(IParameterSymbol parameter)
    {
        var modifier = parameter.RefKind switch
        {
            RefKind.Ref => "ref ",
            RefKind.Out => "out ",
            RefKind.In => "in ",
            RefKind.RefReadOnlyParameter => "in ",
            _ => string.Empty,
        };
        return modifier + Escape(parameter.Name);
    }

    private static string AccessibilityText(Accessibility accessibility)
        => accessibility switch
        {
            Accessibility.Public => "public",
            Accessibility.Internal => "internal",
            Accessibility.Protected => "protected",
            Accessibility.ProtectedOrInternal => "protected internal",
            Accessibility.ProtectedAndInternal => "private protected",
            _ => "private",
        };

    private static string Escape(string name)
        => SyntaxFacts.GetKeywordKind(name) != SyntaxKind.None ? "@" + name : name;

    private static string UniqueName(INamedTypeSymbol? scope, string candidate, CSharpCompilation compilation)
    {
        // Companion names are inserted in the same scope as the interface. Two
        // interfaces with the same simple name can never share that scope, so
        // the candidate is already unique; the loop is defensive for generated
        // declarations colliding with user types.
        if (scope == null)
            return candidate;

        var existing = scope.GetMembers(candidate).Length > 0;
        return existing ? candidate + "_cs2j" : candidate;
    }

    private static string RenderOpenGeneric(INamedTypeSymbol type)
    {
        var arity = type.TypeParameters.Length;
        var arguments = arity == 0 ? string.Empty : "<" + new string(',', arity - 1) + ">";
        if (type.ContainingType is { } containing)
            return RenderOpenGeneric(containing) + "." + type.Name + arguments;

        return type.ContainingNamespace is { IsGlobalNamespace: false } ns
            ? $"global::{ns.ToDisplayString()}.{type.Name}{arguments}"
            : $"global::{type.Name}{arguments}";
    }
}
