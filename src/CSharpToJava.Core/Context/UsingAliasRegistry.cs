using Microsoft.CodeAnalysis;

namespace CSharpToJava.Core.Context;

/// <summary>
/// File-scoped using alias registry.
/// Tracks C# using aliases (e.g. using P2 = Core.Geometry.Point) for the current file.
/// Cleared when processing a new file.
/// </summary>
public class UsingAliasRegistry
{
    private Dictionary<string, UsingAliasInfo> _usingAliases = new();

    public IReadOnlyDictionary<string, UsingAliasInfo> Aliases => _usingAliases;

    /// <summary>
    /// Registers a using alias. Returns false if the alias is invalid (duplicate, Java keyword, etc).
    /// </summary>
    public bool Register(
        string aliasName,
        ITypeSymbol targetType,
        Location? location,
        DiagnosticCollector diagnostics,
        string currentNamespace,
        INamespaceSymbol? globalNamespace,
        IReadOnlyDictionary<ITypeSymbol, string> typeCache)
    {
        if (_usingAliases.ContainsKey(aliasName))
        {
            diagnostics.Error($"Duplicate alias '{aliasName}' in this file", location);
            return false;
        }

        if (ConversionContext.IsJavaKeyword(aliasName))
        {
            diagnostics.Error($"Alias '{aliasName}' is a Java keyword and cannot be used", location);
            return false;
        }

        foreach (var cachedType in typeCache.Values)
        {
            if (cachedType == aliasName)
            {
                diagnostics.Warning($"Alias '{aliasName}' conflicts with existing type", location);
                break;
            }
        }

        if (targetType.Name == aliasName)
        {
            diagnostics.Warning($"Alias '{aliasName}' has the same name as the target type '{targetType.Name}'", location);
        }

        if (!string.IsNullOrEmpty(currentNamespace) && globalNamespace != null)
        {
            var currentNs = globalNamespace.GetMembers(currentNamespace);
            foreach (var member in currentNs)
            {
                if (member is ITypeSymbol type && type.Name == aliasName)
                {
                    diagnostics.Warning($"Alias '{aliasName}' conflicts with type '{type.Name}' in current namespace", location);
                    break;
                }
            }
        }

        _usingAliases[aliasName] = new UsingAliasInfo
        {
            AliasName = aliasName,
            TargetType = targetType,
            Location = location
        };

        return true;
    }

    public bool IsAlias(string identifier) => _usingAliases.ContainsKey(identifier);

    public ITypeSymbol? Resolve(string aliasName)
        => _usingAliases.GetValueOrDefault(aliasName)?.TargetType;

    public void Clear() => _usingAliases.Clear();

    /// <summary>
    /// C# using alias information.
    /// </summary>
    public class UsingAliasInfo
    {
        public string AliasName { get; set; } = string.Empty;
        public ITypeSymbol? TargetType { get; set; }
        public Location? Location { get; set; }
        public bool IsGeneric => TargetType is INamedTypeSymbol named && named.TypeArguments.Length > 0;
    }
}
