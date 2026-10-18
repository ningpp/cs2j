using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.Context;
using CSharpToJava.Core.PlatformIsolation;

namespace CSharpToJava.Core.Pipeline;

internal static class UnsupportedDomainAnalyzer
{
    public static IReadOnlyList<DiagnosticMessage> AnalyzeSyntaxTree(
        SyntaxTree syntaxTree,
        IReadOnlyCollection<string>? compatNamespacePrefixes = null)
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

            if (BoundaryAnalysisHelpers.MatchesAnyPrefix(usingDirective.Name.ToString(), BoundaryTables.UiNamespacePrefixes)
                && !HasCompatNamespacePrefix(usingDirective.Name.ToString(), compatNamespacePrefixes))
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
            if (BoundaryAnalysisHelpers.MatchesAnyPrefix(qualifiedName.ToString(), BoundaryTables.UiNamespacePrefixes)
                && !HasCompatNamespacePrefix(qualifiedName.ToString(), compatNamespacePrefixes))
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
            if (BoundaryAnalysisHelpers.MatchesAnyPrefix(memberAccess.ToString(), BoundaryTables.UiNamespacePrefixes)
                && !HasCompatNamespacePrefix(memberAccess.ToString(), compatNamespacePrefixes))
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

    /// <summary>
    /// Namespaces that have an explicit TypeMappings namespace mapping are backed by a
    /// compatibility package (e.g. System.Windows.Forms → dotnet.winforms.swing), so they
    /// are allowed through the unsupported-domain check. Any sub-namespace of an allowed
    /// prefix is mapped by the same namespace mapping, so a StartsWith check is sufficient.
    /// </summary>
    private static bool HasCompatNamespacePrefix(string name, IReadOnlyCollection<string>? compatNamespacePrefixes)
    {
        if (compatNamespacePrefixes == null || compatNamespacePrefixes.Count == 0)
        {
            return false;
        }

        foreach (var prefix in compatNamespacePrefixes)
        {
            if (name.Equals(prefix, StringComparison.Ordinal)
                || name.StartsWith(prefix + ".", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}

internal static class PlatformBoundaryAnalyzer
{
    public static IReadOnlyList<DiagnosticMessage> AnalyzeSyntaxTree(
        SyntaxTree syntaxTree,
        SemanticModel? semanticModel,
        bool shouldDowngradeTestPlatformProbes = false)
    {
        if (syntaxTree.GetRoot() is not CompilationUnitSyntax root)
        {
            return [];
        }

        var collector = new BoundaryDiagnosticCollector();

        foreach (var attribute in root.DescendantNodes().OfType<AttributeSyntax>())
        {
            var attributeName = BoundaryAnalysisHelpers.NormalizeAttributeName(attribute.Name.ToString());
            if (BoundaryTables.IsOsGuardAttribute(attributeName))
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

            if (BoundaryAnalysisHelpers.MatchesAnyPrefix(identity, BoundaryTables.PlatformMemberPrefixes))
            {
                collector.Add(
                    code: "CS2J3102",
                    category: "platform-boundary",
                    message: $"Platform-specific API '{identity}' must be isolated before Java conversion.",
                    location: invocation.GetLocation(),
                    severity: shouldDowngradeTestPlatformProbes && IsTestProjectPlatformProbe(identity)
                        ? Context.DiagnosticSeverity.Warning
                        : Context.DiagnosticSeverity.Error);
            }
        }

        foreach (var memberAccess in root.DescendantNodes().OfType<MemberAccessExpressionSyntax>())
        {
            if (!BoundaryAnalysisHelpers.TryResolveSymbolIdentity(memberAccess, semanticModel, out var identity))
            {
                continue;
            }

            if (BoundaryAnalysisHelpers.MatchesAnyPrefix(identity, BoundaryTables.PlatformMemberPrefixes)
                || BoundaryAnalysisHelpers.MatchesAnyPrefix(identity, BoundaryTables.PlatformTypePrefixes))
            {
                collector.Add(
                    code: "CS2J3102",
                    category: "platform-boundary",
                    message: $"Platform-specific API '{identity}' must be isolated before Java conversion.",
                    location: memberAccess.GetLocation(),
                    severity: shouldDowngradeTestPlatformProbes && IsTestProjectPlatformProbe(identity)
                        ? Context.DiagnosticSeverity.Warning
                        : Context.DiagnosticSeverity.Error);
            }
        }

        foreach (var typeSyntax in root.DescendantNodes().OfType<TypeSyntax>())
        {
            // Every IdentifierName IS-A TypeSyntax in the Roslyn hierarchy, and
            // a qualified name nests one TypeSyntax per dot segment. Report each
            // platform/native TYPE occurrence exactly once: the outermost
            // qualified/alias-qualified node only, and never an identifier used
            // in expression position (a use of a platform-typed value is not a
            // type site and must not be counted per occurrence).
            if (!BoundaryAnalysisHelpers.IsTypeSyntaxSite(typeSyntax, semanticModel))
            {
                continue;
            }

            if (!BoundaryAnalysisHelpers.TryResolveTypeIdentity(typeSyntax, semanticModel, out var identity))
            {
                continue;
            }

            if (BoundaryAnalysisHelpers.MatchesAnyPrefix(identity, BoundaryTables.PlatformTypePrefixes))
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

            if (BoundaryAnalysisHelpers.MatchesAnyPrefix(identity, BoundaryTables.PlatformTypePrefixes))
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

    internal static bool IsTestProjectPlatformProbe(string identity)
        => identity is "System.OperatingSystem.IsWindows"
            or "System.OperatingSystem.IsLinux"
            or "System.OperatingSystem.IsMacOS"
            or "System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform"
            or "System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture"
            or "System.Runtime.InteropServices.RuntimeInformation.OSArchitecture";
}

internal static class NativeInteropAnalyzer
{
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
            if (BoundaryTables.IsNativeInteropAttribute(attributeName))
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

            if (BoundaryAnalysisHelpers.MatchesAnyPrefix(identity, BoundaryTables.NativeInteropTypePrefixes))
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

            if (BoundaryAnalysisHelpers.MatchesAnyPrefix(identity, BoundaryTables.NativeInteropTypePrefixes))
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
            // Every IdentifierName IS-A TypeSyntax in the Roslyn hierarchy, and
            // a qualified name nests one TypeSyntax per dot segment. Report each
            // platform/native TYPE occurrence exactly once: the outermost
            // qualified/alias-qualified node only, and never an identifier used
            // in expression position (a use of a platform-typed value is not a
            // type site and must not be counted per occurrence).
            if (!BoundaryAnalysisHelpers.IsTypeSyntaxSite(typeSyntax, semanticModel))
            {
                continue;
            }

            if (!BoundaryAnalysisHelpers.TryResolveTypeIdentity(typeSyntax, semanticModel, out var identity))
            {
                continue;
            }

            if (BoundaryAnalysisHelpers.MatchesAnyPrefix(identity, BoundaryTables.NativeInteropTypePrefixes))
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

            if (BoundaryAnalysisHelpers.MatchesAnyPrefix(identity, BoundaryTables.NativeInteropTypePrefixes))
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

/// <summary>
/// 单树一次的 platform+native 合并分析器（仅 platform-isolation inventory 消费）。
/// 现状重复：<see cref="PlatformBoundaryAnalyzer"/> 与 <see cref="NativeInteropAnalyzer"/>
/// 对 invocation/memberAccess/typeSyntax/objectCreation 各自解析一次符号身份，并各做
/// 5/6 次 <c>DescendantNodes()</c> 全树遍历；本合并器只遍历一次、每节点身份只解析
/// 一次，同时按两张前缀表出两份诊断。返回的两列表与两个原 analyzer 逐诊断同码/
/// 同文案/同位置/同次序（各自 collector 去重口径不变），原 analyzer 保持原样供
/// ProjectPasses / SingleFilePasses 等其他调用方使用。
/// </summary>
internal static class PlatformInteropBoundaryAnalyzer
{
    public static (IReadOnlyList<DiagnosticMessage> Platform, IReadOnlyList<DiagnosticMessage> Native)
        AnalyzeSyntaxTree(
            SyntaxTree syntaxTree,
            SemanticModel? semanticModel,
            bool shouldDowngradeTestPlatformProbes)
    {
        var platform = new BoundaryDiagnosticCollector();
        var native = new BoundaryDiagnosticCollector();
        if (syntaxTree.GetRoot() is not CompilationUnitSyntax root)
        {
            return (platform.Diagnostics, native.Diagnostics);
        }

        // 单次 DescendantNodes 遍历（文档序），按节点种类分桶；每桶次序即原
        // analyzer 的 OfType<T>() 次序，供下方按各自族序出站点。
        var attributes = new List<AttributeSyntax>();
        var invocations = new List<InvocationExpressionSyntax>();
        var memberAccesses = new List<MemberAccessExpressionSyntax>();
        var objectCreations = new List<ObjectCreationExpressionSyntax>();
        var typeSyntaxes = new List<TypeSyntax>();
        var externMethods = new List<MethodDeclarationSyntax>();

        foreach (var node in root.DescendantNodes())
        {
            switch (node)
            {
                case AttributeSyntax attribute:
                    attributes.Add(attribute);
                    break;
                case InvocationExpressionSyntax invocation:
                    invocations.Add(invocation);
                    break;
                case MemberAccessExpressionSyntax memberAccess:
                    memberAccesses.Add(memberAccess);
                    break;
                case ObjectCreationExpressionSyntax objectCreation:
                    objectCreations.Add(objectCreation);
                    break;
                case MethodDeclarationSyntax methodDeclaration:
                    if (methodDeclaration.Modifiers.Any(modifier => modifier.IsKind(SyntaxKind.ExternKeyword)))
                    {
                        externMethods.Add(methodDeclaration);
                    }
                    break;
                // TypeSyntax 放最后：IdentifierName/QualifiedName 等皆为其子类，
                // 只有真正落入类型位置语法形态的节点才在此桶（等价 OfType<TypeSyntax>()）。
                case TypeSyntax typeSyntax:
                    typeSyntaxes.Add(typeSyntax);
                    break;
            }
        }

        // 平台族序：属性 → 调用 → 成员访问 → 类型 → 对象创建。
        foreach (var attribute in attributes)
        {
            var attributeName = BoundaryAnalysisHelpers.NormalizeAttributeName(attribute.Name.ToString());
            if (BoundaryTables.IsOsGuardAttribute(attributeName))
            {
                platform.Add(
                    code: "CS2J3101",
                    category: "platform-boundary",
                    message: $"Platform boundary attribute '{attributeName}' must be isolated before Java conversion.",
                    location: attribute.GetLocation());
            }
        }

        // 原生族序：属性 → extern 方法 → 调用 → 成员访问 → 类型 → 对象创建。
        foreach (var attribute in attributes)
        {
            var attributeName = BoundaryAnalysisHelpers.NormalizeAttributeName(attribute.Name.ToString());
            if (BoundaryTables.IsNativeInteropAttribute(attributeName))
            {
                native.Add(
                    code: "CS2J3201",
                    category: "native-interop",
                    message: $"Native interop attribute '{attributeName}' is not supported by the Java-only conversion pipeline.",
                    location: attribute.GetLocation());
            }
        }

        foreach (var methodDeclaration in externMethods)
        {
            native.Add(
                code: "CS2J3203",
                category: "native-interop",
                message: $"Extern method '{methodDeclaration.Identifier.ValueText}' is not supported by the Java-only conversion pipeline.",
                location: methodDeclaration.Identifier.GetLocation());
        }

        // 调用：一次解析身份，同时判两张前缀表。
        foreach (var invocation in invocations)
        {
            if (!BoundaryAnalysisHelpers.TryResolveSymbolIdentity(invocation, semanticModel, out var identity))
            {
                continue;
            }

            if (BoundaryAnalysisHelpers.MatchesAnyPrefix(identity, BoundaryTables.PlatformMemberPrefixes))
            {
                platform.Add(
                    code: "CS2J3102",
                    category: "platform-boundary",
                    message: $"Platform-specific API '{identity}' must be isolated before Java conversion.",
                    location: invocation.GetLocation(),
                    severity: shouldDowngradeTestPlatformProbes
                        && PlatformBoundaryAnalyzer.IsTestProjectPlatformProbe(identity)
                        ? Context.DiagnosticSeverity.Warning
                        : Context.DiagnosticSeverity.Error);
            }

            if (BoundaryAnalysisHelpers.MatchesAnyPrefix(identity, BoundaryTables.NativeInteropTypePrefixes))
            {
                native.Add(
                    code: "CS2J3202",
                    category: "native-interop",
                    message: $"Native interop API '{identity}' is not supported by the Java-only conversion pipeline.",
                    location: invocation.GetLocation());
            }
        }

        // 成员访问。
        foreach (var memberAccess in memberAccesses)
        {
            if (!BoundaryAnalysisHelpers.TryResolveSymbolIdentity(memberAccess, semanticModel, out var identity))
            {
                continue;
            }

            if (BoundaryAnalysisHelpers.MatchesAnyPrefix(identity, BoundaryTables.PlatformMemberPrefixes)
                || BoundaryAnalysisHelpers.MatchesAnyPrefix(identity, BoundaryTables.PlatformTypePrefixes))
            {
                platform.Add(
                    code: "CS2J3102",
                    category: "platform-boundary",
                    message: $"Platform-specific API '{identity}' must be isolated before Java conversion.",
                    location: memberAccess.GetLocation(),
                    severity: shouldDowngradeTestPlatformProbes
                        && PlatformBoundaryAnalyzer.IsTestProjectPlatformProbe(identity)
                        ? Context.DiagnosticSeverity.Warning
                        : Context.DiagnosticSeverity.Error);
            }

            if (BoundaryAnalysisHelpers.MatchesAnyPrefix(identity, BoundaryTables.NativeInteropTypePrefixes))
            {
                native.Add(
                    code: "CS2J3202",
                    category: "native-interop",
                    message: $"Native interop API '{identity}' is not supported by the Java-only conversion pipeline.",
                    location: memberAccess.GetLocation());
            }
        }

        // 类型站点。
        foreach (var typeSyntax in typeSyntaxes)
        {
            if (!BoundaryAnalysisHelpers.IsTypeSyntaxSite(typeSyntax, semanticModel))
            {
                continue;
            }

            if (!BoundaryAnalysisHelpers.TryResolveTypeIdentity(typeSyntax, semanticModel, out var identity))
            {
                continue;
            }

            if (BoundaryAnalysisHelpers.MatchesAnyPrefix(identity, BoundaryTables.PlatformTypePrefixes))
            {
                platform.Add(
                    code: "CS2J3102",
                    category: "platform-boundary",
                    message: $"Platform-specific type '{identity}' must be isolated before Java conversion.",
                    location: typeSyntax.GetLocation());
            }

            if (BoundaryAnalysisHelpers.MatchesAnyPrefix(identity, BoundaryTables.NativeInteropTypePrefixes))
            {
                native.Add(
                    code: "CS2J3202",
                    category: "native-interop",
                    message: $"Native interop type '{identity}' is not supported by the Java-only conversion pipeline.",
                    location: typeSyntax.GetLocation());
            }
        }

        // 对象创建。
        foreach (var objectCreation in objectCreations)
        {
            if (!BoundaryAnalysisHelpers.TryResolveSymbolIdentity(objectCreation, semanticModel, out var identity))
            {
                continue;
            }

            if (BoundaryAnalysisHelpers.MatchesAnyPrefix(identity, BoundaryTables.PlatformTypePrefixes))
            {
                platform.Add(
                    code: "CS2J3102",
                    category: "platform-boundary",
                    message: $"Platform-specific type '{identity}' must be isolated before Java conversion.",
                    location: objectCreation.GetLocation());
            }

            if (BoundaryAnalysisHelpers.MatchesAnyPrefix(identity, BoundaryTables.NativeInteropTypePrefixes))
            {
                native.Add(
                    code: "CS2J3202",
                    category: "native-interop",
                    message: $"Native interop type '{identity}' is not supported by the Java-only conversion pipeline.",
                    location: objectCreation.GetLocation());
            }
        }

        return (platform.Diagnostics, native.Diagnostics);
    }
}

internal static class BoundaryAnalysisHelpers
{
    /// <summary>
    /// MSBuild-generated AssemblyInfo files (obj\...\AssemblyInfo.cs) carry
    /// [assembly: SupportedOSPlatform(...)] attributes that are not part of the
    /// hand-written source. The platform-boundary check skips these files; the
    /// generated Java ignores assembly-level attributes anyway.
    /// </summary>
    public static bool IsGeneratedAssemblyInfoFile(string? filePath)
        => !string.IsNullOrWhiteSpace(filePath)
            && Path.GetFileName(filePath).EndsWith(".AssemblyInfo.cs", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// NativeMethods.cs is the conventional .NET container for P/Invoke extern
    /// declarations. Measured behavior (design §1.2): the converter NEVER emits
    /// stub bodies for extern/DllImport methods on any path. In the project
    /// pipeline a file named exactly NativeMethods.cs skips these native-interop
    /// checks, reaches emit and then hard-fails in the class transformer with
    /// NotSupportedException ("CS2J normalization required: extern/DllImport
    /// method … must be lowered by a preprocessor"); no NativeMethods.java is
    /// produced. In the single-file pipeline the exemption used to never apply
    /// because ConversionPipeline parsed the source without its file name; that
    /// was fixed by passing the path into ParseText, after which single-file
    /// conversions behave like the project pipeline (checks skipped, hard
    /// failure downstream). C06 later milestones replace both outcomes with an
    /// explicit UF060 rejection or a whitelisted interop facade.
    /// </summary>
    public static bool IsPInvokeContainerFile(string? filePath)
        => !string.IsNullOrWhiteSpace(filePath)
            && string.Equals(Path.GetFileName(filePath), "NativeMethods.cs", StringComparison.OrdinalIgnoreCase);

    // Identity / attribute helpers are the single-source BoundaryTables
    // implementation (C42 M2 抽表); this facade is kept so the CheckPasses and
    // the platform-isolation inventory keep their existing call sites.
    public static bool MatchesAnyPrefix(string value, IEnumerable<string> prefixes)
        => BoundaryTables.MatchesAnyPrefix(value, prefixes);

    /// <summary>
    /// True when <paramref name="typeSyntax"/> is a real type-syntax site
    /// (declaration, cast/is/as, typeof, generic argument, …) rather than a
    /// nested qualified/alias-qualified name segment or an identifier consumed
    /// as an expression value. Every IdentifierName IS-A TypeSyntax in the
    /// Roslyn hierarchy, so without this guard a platform TYPE occurrence is
    /// reported once per dot segment and every use of a platform-typed
    /// parameter/local/field is miscounted as a type site.
    /// </summary>
    public static bool IsTypeSyntaxSite(TypeSyntax typeSyntax, SemanticModel? semanticModel)
    {
        // Report a dotted/alias-qualified name only at its outermost node.
        if (typeSyntax.Parent is QualifiedNameSyntax or AliasQualifiedNameSyntax)
        {
            return false;
        }

        if (semanticModel != null)
        {
            var symbol = semanticModel.GetSymbolInfo(typeSyntax).Symbol;
            if (symbol != null && symbol is not ITypeSymbol && symbol.Kind != SymbolKind.Alias)
            {
                // local/parameter/field/property/event/method-group usage, etc.
                return false;
            }
        }

        return true;
    }

    public static string NormalizeAttributeName(string name)
        => BoundaryTables.NormalizeAttributeName(name);

    public static bool TryResolveSymbolIdentity(SyntaxNode node, SemanticModel? semanticModel, out string identity)
        => BoundaryTables.TryResolveSymbolIdentity(node, semanticModel, out identity);

    public static bool TryResolveTypeIdentity(TypeSyntax typeSyntax, SemanticModel? semanticModel, out string identity)
        => BoundaryTables.TryResolveTypeIdentity(typeSyntax, semanticModel, out identity);
}

internal sealed class BoundaryDiagnosticCollector
{
    private readonly List<DiagnosticMessage> _diagnostics = [];
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);

    public IReadOnlyList<DiagnosticMessage> Diagnostics => _diagnostics;

    public void Add(
        string code,
        string category,
        string message,
        Location? location,
        Context.DiagnosticSeverity severity = Context.DiagnosticSeverity.Error)
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

        _diagnostics.Add(new DiagnosticMessage(severity, message, location, code, category));
    }
}

