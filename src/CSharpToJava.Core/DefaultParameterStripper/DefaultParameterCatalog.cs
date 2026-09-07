using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.DefaultParameterStripper;

/// <summary>
/// Cross-file registry shared by the two strip-default passes.
///
/// Pass 1 (<see cref="Collect"/>) records
///   * every declared member signature, so generated overloads never collide
///     with a member that already exists, and
///   * every declaration that owns default parameter values, together with the
///     verbatim default expressions, so pass 2 can complete call sites.
///
/// Pass 2 (the rewriter) looks declarations up by symbol. The primary key is the
/// fully qualified display string of the symbol definition, which uniquely
/// identifies an overload (it contains the parameter types). A name/arity key is
/// kept as a fallback for the (frequent) case where the semantic model cannot
/// resolve a symbol, e.g. because NuGet references are unavailable.
/// </summary>
internal sealed class DefaultParameterCatalog
{
    /// <summary>Format used to render types (namespaces + containing types).</summary>
    private static readonly SymbolDisplayFormat s_qualifiedFormat =
        SymbolDisplayFormat.FullyQualifiedFormat;

    /// <summary>
    /// Format used to render method-like symbols.
    ///
    /// <see cref="SymbolDisplayFormat.FullyQualifiedFormat"/> deliberately omits
    /// <see cref="SymbolDisplayMemberOptions"/>, which makes every method render
    /// as its bare name (<c>Clear</c>) — useless as an identity key because all
    /// same-named members collapse onto each other. This format adds the
    /// containing type and the parameter list so each overload gets a unique,
    /// stable key.
    /// </summary>
    private static readonly SymbolDisplayFormat s_memberFormat = new(
        globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Included,
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        memberOptions: SymbolDisplayMemberOptions.IncludeParameters
            | SymbolDisplayMemberOptions.IncludeContainingType
            | SymbolDisplayMemberOptions.IncludeExplicitInterface,
        parameterOptions: SymbolDisplayParameterOptions.IncludeType
            | SymbolDisplayParameterOptions.IncludeParamsRefOut
            | SymbolDisplayParameterOptions.IncludeExtensionThis,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers
            | SymbolDisplayMiscellaneousOptions.UseSpecialTypes);

    private readonly HashSet<string> _existingSignatures = new(StringComparer.Ordinal);
    private readonly HashSet<string> _pendingOverloads = new(StringComparer.Ordinal);

    // exact symbol key -> signature
    private readonly Dictionary<string, DefaultParameterSignature> _bySymbol = new(StringComparer.Ordinal);

    // "name#paramCount" -> candidate signatures (fallback when symbols do not resolve)
    private readonly Dictionary<string, List<DefaultParameterSignature>> _byName = new(StringComparer.Ordinal);

    // "containingType::name(paramTypes)" -> declared parameter types (conflict detection)
    private readonly Dictionary<string, List<string>> _methodParamTypesByType = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<string>> _methodParamTypes = new(StringComparer.Ordinal);

    public void Collect(SyntaxTree tree, SemanticModel model)
    {
        var root = tree.GetRoot();
        foreach (var node in root.DescendantNodes())
        {
            switch (node)
            {
                case MethodDeclarationSyntax method:
                    RegisterExistingSignature(model, method);
                    RegisterStripped(model, method, method.ParameterList.Parameters, method.Identifier.Text);
                    break;
                case ConstructorDeclarationSyntax ctor:
                    RegisterExistingSignature(model, ctor);
                    RegisterStripped(model, ctor, ctor.ParameterList.Parameters, ".ctor");
                    break;
                case LocalFunctionStatementSyntax local when local.ParameterList != null:
                    RegisterStripped(model, local, local.ParameterList.Parameters, local.Identifier.Text);
                    break;
                case DelegateDeclarationSyntax @delegate when @delegate.ParameterList != null:
                    RegisterStrippedDelegate(model, @delegate);
                    break;
                case TypeDeclarationSyntax type when type.ParameterList is { Parameters.Count: > 0 }:
                    RegisterStrippedPrimaryConstructor(model, type);
                    break;
            }
        }
    }

    // ------------------------------------------------------------------
    // Conflict detection (used by the overload builder)
    // ------------------------------------------------------------------

    public bool HasExistingSignature(ISymbol containingSymbol, string memberName, string paramTypeString)
        => _existingSignatures.Contains(BuildKey(containingSymbol, memberName, paramTypeString));

    public bool HasPendingOverload(ISymbol containingSymbol, string memberName, string paramTypeString)
        => _pendingOverloads.Contains(BuildKey(containingSymbol, memberName, paramTypeString));

    public void RegisterPendingOverload(ISymbol containingSymbol, string memberName, string paramTypeString)
        => _pendingOverloads.Add(BuildKey(containingSymbol, memberName, paramTypeString));

    /// <summary>
    /// True when the type already declares a member with the same name and the
    /// same parameter count but different parameter types. Emitting another
    /// overload of that arity would make some call sites ambiguous (CS0121).
    ///
    /// Only declarations registered during pass 1 are considered, so two
    /// generated overloads never block each other — they may legitimately share
    /// an arity (Utf8JsonWriter(Stream) / Utf8JsonWriter(IBufferWriter&lt;byte&gt;)).
    /// </summary>
    public bool WouldCreateAmbiguityForType(
        ISymbol containingSymbol, string memberName, int paramCount, string paramTypeString)
    {
        if (!_methodParamTypesByType.TryGetValue(
                BuildParamTypeKey(containingSymbol, memberName, paramCount), out var existing))
        {
            return false;
        }

        foreach (var candidate in existing)
        {
            if (!string.Equals(candidate, paramTypeString, StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Registers a declaration's parameter types for ambiguity detection.
    /// Called from pass 1 only — generated overloads must not influence it.
    /// </summary>
    public void RegisterParamTypesForType(
        ISymbol containingSymbol, string memberName, int paramCount, string paramTypeString)
    {
        var key = BuildParamTypeKey(containingSymbol, memberName, paramCount);
        if (!_methodParamTypesByType.TryGetValue(key, out var list))
        {
            list = new List<string>();
            _methodParamTypesByType[key] = list;
        }
        if (!list.Contains(paramTypeString))
        {
            list.Add(paramTypeString);
        }
    }

    /// <summary>Registers a method's parameter types for syntax-based ambiguity detection.</summary>
    public void RegisterParamTypes(string memberName, int paramCount, string paramTypeString)
    {
        var key = $"{memberName}#{paramCount}";
        if (!_methodParamTypes.TryGetValue(key, out var list))
        {
            list = new List<string>();
            _methodParamTypes[key] = list;
        }
        if (!list.Contains(paramTypeString))
        {
            list.Add(paramTypeString);
        }
    }

    public static string BuildParamTypeString(
        SemanticModel model,
        SeparatedSyntaxList<ParameterSyntax> parameters,
        int cutAt)
    {
        var parts = new List<string>(cutAt);
        for (var i = 0; i < cutAt && i < parameters.Count; i++)
        {
            var type = parameters[i].Type;
            if (type == null)
            {
                parts.Add("");
                continue;
            }
            var info = model.GetSymbolInfo(type);
            parts.Add(info.Symbol is ITypeSymbol typeSymbol
                ? typeSymbol.ToDisplayString(s_qualifiedFormat)
                : type.ToString());
        }
        return string.Join(", ", parts);
    }

    public static string BuildFullParamTypeString(
        SemanticModel model,
        SeparatedSyntaxList<ParameterSyntax> parameters)
        => BuildParamTypeString(model, parameters, parameters.Count);

    /// <summary>Syntax-based parameter type string (no semantic model needed).</summary>
    public static string BuildParamTypeStringSyntax(
        SeparatedSyntaxList<ParameterSyntax> parameters, int cutAt)
    {
        var parts = new List<string>(cutAt);
        for (var i = 0; i < cutAt && i < parameters.Count; i++)
        {
            parts.Add(parameters[i].Type?.ToString() ?? "");
        }
        return string.Join(", ", parts);
    }

    // ------------------------------------------------------------------
    // Stripped-declaration lookup (used by the call-site rewriter)
    // ------------------------------------------------------------------

    /// <summary>
    /// Finds the signature registered for the declaration the given symbol was
    /// declared by. Handles constructed generics, reduced extension methods and
    /// overridden members.
    /// </summary>
    public DefaultParameterSignature? TryGetSignature(IMethodSymbol? symbol)
    {
        foreach (var symbolToTry in EnumerateLookupSymbols(symbol))
        {
            var reduced = symbolToTry.ReducedFrom;
            foreach (var key in EnumerateKeys(symbolToTry))
            {
                if (_bySymbol.TryGetValue(key, out var signature))
                {
                    return reduced != null ? signature.WithoutFirst() : signature;
                }
            }
        }
        return null;
    }

    /// <summary>
    /// Syntax-only fallback: returns a signature when exactly one stripped
    /// declaration shares the member name and arity.
    /// </summary>
    public DefaultParameterSignature? TryGetSignatureByName(string memberName, int paramCount)
    {
        if (!_byName.TryGetValue($"{memberName}#{paramCount}", out var candidates))
        {
            return null;
        }
        return candidates.Count == 1 ? candidates[0] : null;
    }

    private static IEnumerable<IMethodSymbol> EnumerateLookupSymbols(IMethodSymbol? symbol)
    {
        var current = symbol;
        var depth = 0;
        while (current != null && depth++ < 8)
        {
            yield return current;
            current = current.OverriddenMethod;
        }
    }

    private static IEnumerable<string> EnumerateKeys(IMethodSymbol symbol)
    {
        var definitions = new List<IMethodSymbol>();
        AddIfMissing(definitions, symbol.OriginalDefinition);
        if (symbol.ConstructedFrom != null)
        {
            AddIfMissing(definitions, symbol.ConstructedFrom.OriginalDefinition);
        }
        if (symbol.ReducedFrom != null)
        {
            AddIfMissing(definitions, symbol.ReducedFrom.OriginalDefinition);
        }
        if (symbol is { MethodKind: MethodKind.DelegateInvoke } && symbol.ContainingType is INamedTypeSymbol owner)
        {
            var invoke = owner.DelegateInvokeMethod;
            if (invoke != null)
            {
                AddIfMissing(definitions, invoke.OriginalDefinition);
            }
        }

        foreach (var definition in definitions)
        {
            yield return definition.ToDisplayString(s_memberFormat);
        }
    }

    private static void AddIfMissing(List<IMethodSymbol> list, IMethodSymbol candidate)
    {
        foreach (var existing in list)
        {
            if (SymbolEqualityComparer.Default.Equals(existing, candidate))
            {
                return;
            }
        }
        list.Add(candidate);
    }

    // ------------------------------------------------------------------
    // Registration
    // ------------------------------------------------------------------

    private void RegisterStripped(
        SemanticModel model,
        SyntaxNode declaration,
        SeparatedSyntaxList<ParameterSyntax> parameters,
        string memberName)
    {
        var signature = DefaultParameterSignature.Create(parameters);
        if (!signature.HasAnyDefault)
        {
            return;
        }

        RegisterNameKey($"{memberName}#{parameters.Count}", signature);

        // Constructors are additionally indexed by their declaring type name so
        // an object creation can be resolved without a semantic model.
        if (memberName == ".ctor" && declaration.Parent is TypeDeclarationSyntax owner)
        {
            RegisterNameKey($"{owner.Identifier.Text}#{parameters.Count}", signature);
        }

        RegisterParamTypes(memberName, parameters.Count,
            BuildParamTypeStringSyntax(parameters, parameters.Count));

        if (model.GetDeclaredSymbol(declaration) is not ISymbol symbol)
        {
            return;
        }

        var key = symbol.ToDisplayString(s_memberFormat);
        if (!_bySymbol.ContainsKey(key))
        {
            _bySymbol[key] = signature;
        }
    }

    private void RegisterStrippedDelegate(SemanticModel model, DelegateDeclarationSyntax declaration)
    {
        if (model.GetDeclaredSymbol(declaration) is not INamedTypeSymbol delegateType)
        {
            return;
        }

        var invoke = delegateType.DelegateInvokeMethod;
        var parameters = declaration.ParameterList!.Parameters;
        var signature = DefaultParameterSignature.Create(parameters);
        if (!signature.HasAnyDefault)
        {
            return;
        }

        var nameKey = $"Invoke#{parameters.Count}";
        if (!_byName.TryGetValue(nameKey, out var list))
        {
            list = new List<DefaultParameterSignature>();
            _byName[nameKey] = list;
        }
        if (!list.Any(s => SameShape(s, signature)))
        {
            list.Add(signature);
        }

        if (invoke == null)
        {
            return;
        }

        var key = invoke.ToDisplayString(s_memberFormat);
        if (!_bySymbol.ContainsKey(key))
        {
            _bySymbol[key] = signature;
        }
    }

    private void RegisterStrippedPrimaryConstructor(SemanticModel model, TypeDeclarationSyntax type)
    {
        var parameters = type.ParameterList!.Parameters;
        var signature = DefaultParameterSignature.Create(parameters);
        if (!signature.HasAnyDefault)
        {
            return;
        }

        if (model.GetDeclaredSymbol(type) is not INamedTypeSymbol typeSymbol)
        {
            return;
        }

        var ctor = typeSymbol.InstanceConstructors
            .FirstOrDefault(c => c.Parameters.Length == parameters.Count
                && (parameters.Count == 0 || c.Parameters[0].Name == parameters[0].Identifier.ValueText));
        if (ctor == null)
        {
            return;
        }

        var key = ctor.ToDisplayString(s_memberFormat);
        if (!_bySymbol.ContainsKey(key))
        {
            _bySymbol[key] = signature;
        }

        var nameKey = $".ctor#{parameters.Count}";
        if (!_byName.TryGetValue(nameKey, out var list))
        {
            list = new List<DefaultParameterSignature>();
            _byName[nameKey] = list;
        }
        if (!list.Any(s => SameShape(s, signature)))
        {
            list.Add(signature);
        }
    }

    private void RegisterNameKey(string key, DefaultParameterSignature signature)
    {
        if (!_byName.TryGetValue(key, out var list))
        {
            list = new List<DefaultParameterSignature>();
            _byName[key] = list;
        }
        if (!list.Any(s => SameShape(s, signature)))
        {
            list.Add(signature);
        }
    }

    private static bool SameShape(DefaultParameterSignature a, DefaultParameterSignature b)
    {
        if (a.Count != b.Count)
        {
            return false;
        }
        for (var i = 0; i < a.Count; i++)
        {
            if (!string.Equals(a.Names[i], b.Names[i], StringComparison.Ordinal)
                || !string.Equals(a.DefaultTexts[i], b.DefaultTexts[i], StringComparison.Ordinal))
            {
                return false;
            }
        }
        return true;
    }

    private void RegisterExistingSignature(SemanticModel model, MemberDeclarationSyntax member)
    {
        var symbol = model.GetDeclaredSymbol(member);
        if (symbol == null) return;

        string? name = member switch
        {
            MethodDeclarationSyntax m => m.Identifier.Text,
            ConstructorDeclarationSyntax => ".ctor",
            _ => null,
        };
        if (name == null) return;

        SeparatedSyntaxList<ParameterSyntax>? parameters = member switch
        {
            MethodDeclarationSyntax m => m.ParameterList.Parameters,
            ConstructorDeclarationSyntax c => c.ParameterList.Parameters,
            _ => null,
        };
        if (parameters == null) return;

        var paramTypeString = BuildFullParamTypeString(model, parameters.Value);
        var containingType = symbol.ContainingType ?? symbol;
        _existingSignatures.Add(BuildKey(containingType, name, paramTypeString));

        var paramCount = parameters.Value.Count;
        RegisterParamTypes(name, paramCount, paramTypeString);
        RegisterParamTypesForType(containingType, name, paramCount, paramTypeString);
    }

    private static string BuildParamTypeKey(ISymbol containingSymbol, string memberName, int paramCount)
    {
        var containingKey = containingSymbol.ToDisplayString(s_qualifiedFormat);
        return $"{containingKey}::{memberName}#{paramCount}";
    }

    private static string BuildKey(ISymbol containingSymbol, string memberName, string paramTypeString)
    {
        var containingKey = containingSymbol == null
            ? "<global>"
            : containingSymbol.ToDisplayString(s_qualifiedFormat);
        return $"{containingKey}::{memberName}({paramTypeString})";
    }
}
