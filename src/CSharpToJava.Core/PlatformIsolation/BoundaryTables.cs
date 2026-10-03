using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.PlatformIsolation;

/// <summary>
/// C42 BoundaryTables: the single source of truth for the platform / interop
/// boundary prefix tables and identity-matching helpers (design §4 file table,
/// §4.1). Extracted verbatim from
/// <c>Pipeline/Passes/UnsupportedDomainPasses.cs</c> so the three converter
/// analyzers (thin wrappers) and the platform-isolation inventory share one
/// definition. Table contents, ordering and matching semantics are frozen;
/// diagnostic codes, messages and locations stay in the analyzers.
/// </summary>
internal static class BoundaryTables
{
    /// <summary>UI 黑名单命名空间前缀（6，design §1.1）。</summary>
    public static readonly string[] UiNamespacePrefixes =
    [
        "System.Windows.Forms",
        "System.Windows.Controls",
        "System.Windows",
        "Microsoft.Maui",
        "Windows.UI.Xaml",
        "System.Xaml",
    ];

    /// <summary>平台服务类型前缀（9，design §1.1）。</summary>
    public static readonly string[] PlatformTypePrefixes =
    [
        "Microsoft.Win32.Registry",
        "Microsoft.Win32.RegistryKey",
        "System.Diagnostics.EventLog",
        "System.Management.ManagementObject",
        "System.Management.ManagementObjectSearcher",
        "System.ServiceProcess.ServiceController",
        "System.DirectoryServices.DirectoryEntry",
        "System.DirectoryServices.DirectorySearcher",
        "System.IO.Ports.SerialPort",
    ];

    /// <summary>OS 探针成员前缀（8，design §1.1）。</summary>
    public static readonly string[] PlatformMemberPrefixes =
    [
        "System.Environment.OSVersion",
        "System.OperatingSystem.IsWindows",
        "System.OperatingSystem.IsLinux",
        "System.OperatingSystem.IsMacOS",
        "System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform",
        "System.Runtime.InteropServices.RuntimeInformation.OSDescription",
        "System.Runtime.InteropServices.RuntimeInformation.OSArchitecture",
        "System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture",
    ];

    /// <summary>OS guard 属性简单名（4，已去 <c>Attribute</c> 后缀，design §1.1）。</summary>
    public static readonly string[] OsGuardAttributeNames =
    [
        "SupportedOSPlatform",
        "UnsupportedOSPlatform",
        "SupportedOSPlatformGuard",
        "UnsupportedOSPlatformGuard",
    ];

    /// <summary>原生互操作属性简单名（6，design §1.1）。</summary>
    public static readonly string[] NativeInteropAttributeNames =
    [
        "DllImport",
        "LibraryImport",
        "ComImport",
        "MarshalAs",
        "UnmanagedCallersOnly",
        "GeneratedComInterface",
    ];

    /// <summary>原生互操作类型前缀（4，design §1.1）。</summary>
    public static readonly string[] NativeInteropTypePrefixes =
    [
        "System.Runtime.InteropServices.Marshal",
        "System.Runtime.InteropServices.NativeLibrary",
        "System.Runtime.InteropServices.HandleRef",
        "System.Runtime.InteropServices.SafeHandle",
    ];

    /// <summary>属性简单名是否为 4 个 OS guard 属性之一。</summary>
    public static bool IsOsGuardAttribute(string attributeName)
        => Array.IndexOf(OsGuardAttributeNames, attributeName) >= 0;

    /// <summary>属性简单名是否为 6 个原生互操作属性之一。</summary>
    public static bool IsNativeInteropAttribute(string attributeName)
        => Array.IndexOf(NativeInteropAttributeNames, attributeName) >= 0;

    /// <summary>身份串是否命中 8 个 OS 探针成员前缀（PlatformMember 族）。</summary>
    public static bool IsPlatformMemberIdentity(string identity)
        => MatchesAnyPrefix(identity, PlatformMemberPrefixes);

    /// <summary>身份串是否命中 9 个平台服务类型前缀（PlatformType 族）。</summary>
    public static bool IsPlatformTypeIdentity(string identity)
        => MatchesAnyPrefix(identity, PlatformTypePrefixes);

    public static bool MatchesAnyPrefix(string value, IEnumerable<string> prefixes)
    {
        foreach (var prefix in prefixes)
        {
            if (value.Equals(prefix, StringComparison.Ordinal)
                || value.StartsWith(prefix + ".", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    public static string NormalizeAttributeName(string name)
    {
        var simpleName = name.Split('.').Last();
        return simpleName.EndsWith("Attribute", StringComparison.Ordinal)
            ? simpleName[..^"Attribute".Length]
            : simpleName;
    }

    public static bool TryResolveSymbolIdentity(SyntaxNode node, SemanticModel? semanticModel, out string identity)
    {
        if (semanticModel != null)
        {
            var symbolInfo = semanticModel.GetSymbolInfo(node);
            var symbol = symbolInfo.Symbol ?? symbolInfo.CandidateSymbols.FirstOrDefault();
            if (symbol != null)
            {
                switch (symbol)
                {
                    case IMethodSymbol methodSymbol when methodSymbol.MethodKind == MethodKind.Constructor:
                        identity = methodSymbol.ContainingType.ToDisplayString();
                        return true;
                    case IMethodSymbol methodSymbol:
                        identity = $"{methodSymbol.ContainingType.ToDisplayString()}.{methodSymbol.Name}";
                        return true;
                    case IPropertySymbol propertySymbol:
                        identity = $"{propertySymbol.ContainingType.ToDisplayString()}.{propertySymbol.Name}";
                        return true;
                    case IFieldSymbol fieldSymbol:
                        identity = $"{fieldSymbol.ContainingType.ToDisplayString()}.{fieldSymbol.Name}";
                        return true;
                    case INamedTypeSymbol namedTypeSymbol:
                        identity = namedTypeSymbol.ToDisplayString();
                        return true;
                }
            }

            var typeInfo = semanticModel.GetTypeInfo(node);
            if (typeInfo.Type is INamedTypeSymbol typeSymbol)
            {
                identity = typeSymbol.ToDisplayString();
                return true;
            }
        }

        identity = node switch
        {
            InvocationExpressionSyntax invocation => invocation.Expression.ToString(),
            ObjectCreationExpressionSyntax objectCreation => objectCreation.Type.ToString(),
            AttributeSyntax attribute => attribute.Name.ToString(),
            _ => node.ToString(),
        };

        return !string.IsNullOrWhiteSpace(identity);
    }

    public static bool TryResolveTypeIdentity(TypeSyntax typeSyntax, SemanticModel? semanticModel, out string identity)
    {
        if (semanticModel != null)
        {
            var typeInfo = semanticModel.GetTypeInfo(typeSyntax);
            if (typeInfo.Type is INamedTypeSymbol typeSymbol)
            {
                identity = typeSymbol.ToDisplayString();
                return true;
            }
        }

        identity = typeSyntax.ToString();
        return !string.IsNullOrWhiteSpace(identity);
    }
}