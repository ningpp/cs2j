using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.Context;

namespace CSharpToJava.Core.Pipeline;

internal static class UnsupportedDomainAnalyzer
{
    private static readonly string[] UnsupportedNamespacePrefixes =
    [
        "System.Windows.Forms",
        "System.Windows.Controls",
        "System.Windows",
        "Microsoft.Maui",
        "Windows.UI.Xaml",
        "System.Xaml",
    ];

    public static IReadOnlyList<DiagnosticMessage> AnalyzeSyntaxTree(SyntaxTree syntaxTree)
    {
        if (syntaxTree.GetRoot() is not CompilationUnitSyntax root)
        {
            return [];
        }

        var collector = new BoundaryDiagnosticCollector();

        foreach (var usingDirective in root.Usings)
        {
            if (usingDirective.Name == null)
            {
                continue;
            }

            if (BoundaryAnalysisHelpers.MatchesAnyPrefix(usingDirective.Name.ToString(), UnsupportedNamespacePrefixes))
            {
                collector.Add(
                    code: "CS2J3001",
                    category: "unsupported-domain",
                    message: $"Namespace '{usingDirective.Name}' is not supported by the Java-only conversion pipeline.",
                    location: usingDirective.GetLocation());
            }
        }

        foreach (var qualifiedName in root.DescendantNodes().OfType<QualifiedNameSyntax>())
        {
            if (BoundaryAnalysisHelpers.MatchesAnyPrefix(qualifiedName.ToString(), UnsupportedNamespacePrefixes))
            {
                collector.Add(
                    code: "CS2J3001",
                    category: "unsupported-domain",
                    message: $"Namespace '{qualifiedName}' is not supported by the Java-only conversion pipeline.",
                    location: qualifiedName.GetLocation());
            }
        }

        foreach (var memberAccess in root.DescendantNodes().OfType<MemberAccessExpressionSyntax>())
        {
            if (BoundaryAnalysisHelpers.MatchesAnyPrefix(memberAccess.ToString(), UnsupportedNamespacePrefixes))
            {
                collector.Add(
                    code: "CS2J3001",
                    category: "unsupported-domain",
                    message: $"Namespace '{memberAccess}' is not supported by the Java-only conversion pipeline.",
                    location: memberAccess.GetLocation());
            }
        }

        return collector.Diagnostics;
    }
}

internal static class PlatformBoundaryAnalyzer
{
    private static readonly string[] PlatformTypePrefixes =
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

    private static readonly string[] PlatformMemberPrefixes =
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

    public static IReadOnlyList<DiagnosticMessage> AnalyzeSyntaxTree(SyntaxTree syntaxTree, SemanticModel? semanticModel)
    {
        if (syntaxTree.GetRoot() is not CompilationUnitSyntax root)
        {
            return [];
        }

        var collector = new BoundaryDiagnosticCollector();

        foreach (var attribute in root.DescendantNodes().OfType<AttributeSyntax>())
        {
            var attributeName = BoundaryAnalysisHelpers.NormalizeAttributeName(attribute.Name.ToString());
            if (attributeName is "SupportedOSPlatform" or "UnsupportedOSPlatform" or "SupportedOSPlatformGuard" or "UnsupportedOSPlatformGuard")
            {
                collector.Add(
                    code: "CS2J3101",
                    category: "platform-boundary",
                    message: $"Platform boundary attribute '{attributeName}' must be isolated before Java conversion.",
                    location: attribute.GetLocation());
            }
        }

        foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (!BoundaryAnalysisHelpers.TryResolveSymbolIdentity(invocation, semanticModel, out var identity))
            {
                continue;
            }

            if (BoundaryAnalysisHelpers.MatchesAnyPrefix(identity, PlatformMemberPrefixes))
            {
                collector.Add(
                    code: "CS2J3102",
                    category: "platform-boundary",
                    message: $"Platform-specific API '{identity}' must be isolated before Java conversion.",
                    location: invocation.GetLocation());
            }
        }

        foreach (var memberAccess in root.DescendantNodes().OfType<MemberAccessExpressionSyntax>())
        {
            if (!BoundaryAnalysisHelpers.TryResolveSymbolIdentity(memberAccess, semanticModel, out var identity))
            {
                continue;
            }

            if (BoundaryAnalysisHelpers.MatchesAnyPrefix(identity, PlatformMemberPrefixes)
                || BoundaryAnalysisHelpers.MatchesAnyPrefix(identity, PlatformTypePrefixes))
            {
                collector.Add(
                    code: "CS2J3102",
                    category: "platform-boundary",
                    message: $"Platform-specific API '{identity}' must be isolated before Java conversion.",
                    location: memberAccess.GetLocation());
            }
        }

        foreach (var typeSyntax in root.DescendantNodes().OfType<TypeSyntax>())
        {
            if (!BoundaryAnalysisHelpers.TryResolveTypeIdentity(typeSyntax, semanticModel, out var identity))
            {
                continue;
            }

            if (BoundaryAnalysisHelpers.MatchesAnyPrefix(identity, PlatformTypePrefixes))
            {
                collector.Add(
                    code: "CS2J3102",
                    category: "platform-boundary",
                    message: $"Platform-specific type '{identity}' must be isolated before Java conversion.",
                    location: typeSyntax.GetLocation());
            }
        }

        foreach (var objectCreation in root.DescendantNodes().OfType<ObjectCreationExpressionSyntax>())
        {
            if (!BoundaryAnalysisHelpers.TryResolveSymbolIdentity(objectCreation, semanticModel, out var identity))
            {
                continue;
            }

            if (BoundaryAnalysisHelpers.MatchesAnyPrefix(identity, PlatformTypePrefixes))
            {
                collector.Add(
                    code: "CS2J3102",
                    category: "platform-boundary",
                    message: $"Platform-specific type '{identity}' must be isolated before Java conversion.",
                    location: objectCreation.GetLocation());
            }
        }

        return collector.Diagnostics;
    }
}

internal static class NativeInteropAnalyzer
{
    private static readonly string[] NativeInteropTypePrefixes =
    [
        "System.Runtime.InteropServices.Marshal",
        "System.Runtime.InteropServices.NativeLibrary",
        "System.Runtime.InteropServices.HandleRef",
        "System.Runtime.InteropServices.SafeHandle",
    ];

    public static IReadOnlyList<DiagnosticMessage> AnalyzeSyntaxTree(SyntaxTree syntaxTree, SemanticModel? semanticModel)
    {
        if (syntaxTree.GetRoot() is not CompilationUnitSyntax root)
        {
            return [];
        }

        var collector = new BoundaryDiagnosticCollector();

        foreach (var attribute in root.DescendantNodes().OfType<AttributeSyntax>())
        {
            var attributeName = BoundaryAnalysisHelpers.NormalizeAttributeName(attribute.Name.ToString());
            if (attributeName is "DllImport" or "LibraryImport" or "ComImport" or "MarshalAs" or "StructLayout" or "FieldOffset" or "UnmanagedCallersOnly" or "GeneratedComInterface")
            {
                collector.Add(
                    code: "CS2J3201",
                    category: "native-interop",
                    message: $"Native interop attribute '{attributeName}' is not supported by the Java-only conversion pipeline.",
                    location: attribute.GetLocation());
            }
        }

        foreach (var methodDeclaration in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
        {
            if (!methodDeclaration.Modifiers.Any(modifier => modifier.IsKind(SyntaxKind.ExternKeyword)))
            {
                continue;
            }

            collector.Add(
                code: "CS2J3203",
                category: "native-interop",
                message: $"Extern method '{methodDeclaration.Identifier.ValueText}' is not supported by the Java-only conversion pipeline.",
                location: methodDeclaration.Identifier.GetLocation());
        }

        foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (!BoundaryAnalysisHelpers.TryResolveSymbolIdentity(invocation, semanticModel, out var identity))
            {
                continue;
            }

            if (BoundaryAnalysisHelpers.MatchesAnyPrefix(identity, NativeInteropTypePrefixes))
            {
                collector.Add(
                    code: "CS2J3202",
                    category: "native-interop",
                    message: $"Native interop API '{identity}' is not supported by the Java-only conversion pipeline.",
                    location: invocation.GetLocation());
            }
        }

        foreach (var memberAccess in root.DescendantNodes().OfType<MemberAccessExpressionSyntax>())
        {
            if (!BoundaryAnalysisHelpers.TryResolveSymbolIdentity(memberAccess, semanticModel, out var identity))
            {
                continue;
            }

            if (BoundaryAnalysisHelpers.MatchesAnyPrefix(identity, NativeInteropTypePrefixes))
            {
                collector.Add(
                    code: "CS2J3202",
                    category: "native-interop",
                    message: $"Native interop API '{identity}' is not supported by the Java-only conversion pipeline.",
                    location: memberAccess.GetLocation());
            }
        }

        foreach (var typeSyntax in root.DescendantNodes().OfType<TypeSyntax>())
        {
            if (!BoundaryAnalysisHelpers.TryResolveTypeIdentity(typeSyntax, semanticModel, out var identity))
            {
                continue;
            }

            if (BoundaryAnalysisHelpers.MatchesAnyPrefix(identity, NativeInteropTypePrefixes))
            {
                collector.Add(
                    code: "CS2J3202",
                    category: "native-interop",
                    message: $"Native interop type '{identity}' is not supported by the Java-only conversion pipeline.",
                    location: typeSyntax.GetLocation());
            }
        }

        foreach (var objectCreation in root.DescendantNodes().OfType<ObjectCreationExpressionSyntax>())
        {
            if (!BoundaryAnalysisHelpers.TryResolveSymbolIdentity(objectCreation, semanticModel, out var identity))
            {
                continue;
            }

            if (BoundaryAnalysisHelpers.MatchesAnyPrefix(identity, NativeInteropTypePrefixes))
            {
                collector.Add(
                    code: "CS2J3202",
                    category: "native-interop",
                    message: $"Native interop type '{identity}' is not supported by the Java-only conversion pipeline.",
                    location: objectCreation.GetLocation());
            }
        }

        return collector.Diagnostics;
    }
}

internal static class BoundaryAnalysisHelpers
{
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

internal sealed class BoundaryDiagnosticCollector
{
    private readonly List<DiagnosticMessage> _diagnostics = [];
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);

    public IReadOnlyList<DiagnosticMessage> Diagnostics => _diagnostics;

    public void Add(string code, string category, string message, Location? location)
    {
        var lineSpan = location?.GetLineSpan();
        var path = lineSpan?.Path ?? string.Empty;
        var line = lineSpan?.StartLinePosition.Line ?? -1;
        var column = lineSpan?.StartLinePosition.Character ?? -1;
        var key = $"{code}|{category}|{path}|{line}|{column}|{message}";
        if (!_seen.Add(key))
        {
            return;
        }

        _diagnostics.Add(new DiagnosticMessage(Context.DiagnosticSeverity.Error, message, location, code, category));
    }
}