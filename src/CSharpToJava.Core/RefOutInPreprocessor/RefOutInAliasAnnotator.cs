using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.RefOutInPreprocessor;

/// <summary>
/// Pass 1: Classify every method/property/indexer symbol as Internal, External,
/// or Locked, and build the Holder set (symbols that must become ByRef&lt;T&gt;).
/// Shared across all files in the same compilation.
/// </summary>
internal sealed class RefOutInAliasAnnotator
{
    private readonly SemanticModel _semanticModel;
    private readonly RefOutInRewriterStatistics _stats;
    private readonly List<RefOutInRewriterDiagnostic> _diagnostics;

    // Symbol → classification
    private readonly Dictionary<ISymbol, SymbolClass> _classified = new(SymbolEqualityComparer.Default);

    // Holder set: parameter symbols and method symbols that must become ByRef<T>
    private readonly HashSet<ISymbol> _holderSymbols = new(SymbolEqualityComparer.Default);

    // Set of local declarations that are ref locals (identifier → known)
    private readonly HashSet<string> _refLocalNames = new();

    // CD8: `in` parameters kept because stripping would create an overload collision
    private readonly HashSet<IParameterSymbol> _inKeptParameters = new(SymbolEqualityComparer.Default);

    public IReadOnlySet<ISymbol> HolderSymbols => _holderSymbols;
    public IReadOnlySet<string> RefLocalNames => _refLocalNames;
    public IReadOnlySet<IParameterSymbol> InKeptParameters => _inKeptParameters;

    /// <summary>CD8 (C9): true when this in-parameter must keep its modifier.</summary>
    public bool IsInParameterKept(IParameterSymbol? parameter) =>
        parameter != null && _inKeptParameters.Contains(parameter);

    public RefOutInAliasAnnotator(
        SemanticModel semanticModel,
        RefOutInRewriterStatistics stats,
        List<RefOutInRewriterDiagnostic> diagnostics)
    {
        _semanticModel = semanticModel;
        _stats = stats;
        _diagnostics = diagnostics;
    }

    public void Annotate()
    {
        var root = _semanticModel.SyntaxTree.GetRoot();
        var methods = root.DescendantNodes()
            .OfType<BaseMethodDeclarationSyntax>();

        foreach (var method in methods)
        {
            var symbol = (IMethodSymbol?)_semanticModel.GetDeclaredSymbol(method);
            if (symbol == null) continue;
            ClassifyMethod(symbol);
        }

        // Also classify delegate declarations
        var delegates = root.DescendantNodes()
            .OfType<DelegateDeclarationSyntax>();
        foreach (var del in delegates)
        {
            var symbol = (INamedTypeSymbol?)_semanticModel.GetDeclaredSymbol(del);
            if (symbol == null) continue;
            ClassifyDelegate(symbol);
        }

        // Scan for ref locals
        var refLocals = root.DescendantNodes()
            .OfType<VariableDeclaratorSyntax>()
            .Where(v => v.Parent is VariableDeclarationSyntax vd
                && vd.Parent is LocalDeclarationStatementSyntax lds
                && lds.Declaration.Variables.Count > 0
                && IsRefVariableDeclaration(vd));
        foreach (var rl in refLocals)
        {
            _refLocalNames.Add(rl.Identifier.Text);
            _stats.RefLocalsRewritten++;
        }

        // Scan for ref fields
        var refFields = root.DescendantNodes()
            .OfType<FieldDeclarationSyntax>()
            .Where(f => f.Declaration.Type is RefTypeSyntax);
        foreach (var rf in refFields)
        {
            _stats.RefFieldsRewritten++;
        }
    }

    private static bool IsRefVariableDeclaration(VariableDeclarationSyntax vd)
    {
        return vd.Type is RefTypeSyntax;
    }

    private void ClassifyMethod(IMethodSymbol method)
    {
        var cls = GetSymbolClass(method);
        if (cls != SymbolClass.Internal) return;

        foreach (var param in method.Parameters)
        {
            var refKind = param.RefKind;
            if (refKind == RefKind.Ref || refKind == RefKind.Out)
            {
                _holderSymbols.Add(param);
                if (param.Type.IsRefLikeType)
                {
                    _stats.RefStructParamsRewritten++;
                }
                else
                {
                    if (refKind == RefKind.Ref)
                        _stats.RefParamsRewritten++;
                    else
                        _stats.OutParamsRewritten++;
                }
            }
            else if (refKind == RefKind.In)
            {
                // CD8 (C9): keep `in` when stripping would collide with an
                // existing by-value overload of the same member name.
                if (IsInOverloadCollision(method, method.ContainingType))
                {
                    _inKeptParameters.Add(param);
                    _stats.InOverloadKept++;
                    _diagnostics.Add(new RefOutInRewriterDiagnostic(
                        RefOutInDiagnosticCodes.InOverloadCollision,
                        $"stripping 'in' from '{method.ToDisplayString()}' parameter '{param.Name}' " +
                        "would create an overload collision; keeping 'in'",
                        RefOutInSeverity.Warning, param.Locations.FirstOrDefault()));
                }
                else
                {
                    _stats.InByValueParams++;
                }
            }
            else if (refKind == RefKind.RefReadOnly)
            {
                _stats.InByValueParams++;
            }
        }

        // Ref return
        if (method.RefKind == RefKind.Ref || method.RefKind == RefKind.RefReadOnly)
        {
            _holderSymbols.Add(method);
            _stats.RefReturnsRewritten++;
        }
    }

    private void ClassifyDelegate(INamedTypeSymbol delegateSymbol)
    {
        var invokeMethod = delegateSymbol.DelegateInvokeMethod;
        if (invokeMethod == null) return;

        var cls = GetSymbolClass(delegateSymbol);
        if (cls != SymbolClass.Internal) return;

        bool hasRefOrOut = false;
        foreach (var param in invokeMethod.Parameters)
        {
            if (param.RefKind == RefKind.Ref || param.RefKind == RefKind.Out)
            {
                _holderSymbols.Add(param);
                hasRefOrOut = true;
                if (param.RefKind == RefKind.Ref)
                    _stats.RefParamsRewritten++;
                else
                    _stats.OutParamsRewritten++;
            }
            else if (param.RefKind == RefKind.In || param.RefKind == RefKind.RefReadOnly)
            {
                _stats.InByValueParams++;
            }
        }

        if (hasRefOrOut)
        {
            _stats.DelegatesRewritten++;
        }
    }

    internal SymbolClass GetSymbolClass(ISymbol symbol)
    {
        if (_classified.TryGetValue(symbol, out var cls))
            return cls;

        // Locked checks
        if (IsLocked(symbol))
        {
            _classified[symbol] = SymbolClass.Locked;
            _stats.LockedContractMembers++;
            _diagnostics.Add(new RefOutInRewriterDiagnostic(
                "ROI001",
                $"Member '{symbol.ToDisplayString()}' is locked (external contract); preserving ref/out/in.",
                RefOutInSeverity.Info,
                symbol.Locations.FirstOrDefault()));
            return SymbolClass.Locked;
        }

        // Internal if declaration is in source
        var isInternal = symbol.Locations.Any(loc => loc.IsInSource);
        _classified[symbol] = isInternal ? SymbolClass.Internal : SymbolClass.External;
        return _classified[symbol];
    }

    /// <summary>CD8: true when another overload differs from the method only by `in`.</summary>
    private static bool IsInOverloadCollision(IMethodSymbol method, ITypeSymbol? containingType)
    {
        if (containingType == null) return false;
        foreach (var other in containingType.GetMembers(method.Name).OfType<IMethodSymbol>())
        {
            if (SymbolEqualityComparer.Default.Equals(other, method)) continue;
            if (other.Parameters.Length != method.Parameters.Length) continue;
            bool sameTypes = true;
            bool differsOnlyByIn = false;
            for (int i = 0; i < method.Parameters.Length; i++)
            {
                var pa = method.Parameters[i];
                var pb = other.Parameters[i];
                if (!SymbolEqualityComparer.Default.Equals(pa.Type, pb.Type)) { sameTypes = false; break; }
                if ((pa.RefKind == RefKind.In) != (pb.RefKind == RefKind.In)) differsOnlyByIn = true;
            }
            if (sameTypes && differsOnlyByIn) return true;
        }
        return false;
    }

    private static bool IsLocked(ISymbol symbol)
    {
        if (symbol is not IMethodSymbol method)
        {
            // Check if it's a delegate type
            if (symbol is ITypeSymbol type && type.TypeKind == TypeKind.Delegate)
            {
                var invoke = (symbol as INamedTypeSymbol)?.DelegateInvokeMethod;
                if (invoke != null)
                {
                    foreach (var attr in invoke.GetAttributes())
                    {
                        var attrName = attr.AttributeClass?.ToDisplayString();
                        if (attrName == "System.Runtime.InteropServices.DllImportAttribute"
                            || attrName == "System.Runtime.InteropServices.LibraryImportAttribute")
                            return true;
                    }
                }
                return false;
            }
            return false;
        }

        // DllImport / LibraryImport extern
        if (method.IsExtern)
        {
            foreach (var attr in method.GetAttributes())
            {
                var attrName = attr.AttributeClass?.ToDisplayString();
                if (attrName == "System.Runtime.InteropServices.DllImportAttribute"
                    || attrName == "System.Runtime.InteropServices.LibraryImportAttribute")
                    return true;
            }
        }

        // Override chain → external
        var overridden = method.OverriddenMethod;
        while (overridden != null)
        {
            if (!overridden.Locations.Any(loc => loc.IsInSource))
                return true;
            overridden = overridden.OverriddenMethod;
        }

        // Interface implementation → external
        if (method.ContainingType != null)
        {
            foreach (var iface in method.ContainingType.AllInterfaces)
            {
                if (!iface.Locations.Any(loc => loc.IsInSource))
                {
                    foreach (var ifaceMember in iface.GetMembers().OfType<IMethodSymbol>())
                    {
                        var impl = method.ContainingType.FindImplementationForInterfaceMember(ifaceMember);
                        if (impl != null && SymbolEqualityComparer.Default.Equals(impl, method))
                            return true;
                    }
                }
            }
        }

        return false;
    }

    internal enum SymbolClass { Internal, External, Locked }
}
