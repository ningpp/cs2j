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

    /// <summary>
    /// 语法预过滤器（性能优化）：从所有前缀表中提取最短的关键子串，用于在
    /// 调用昂贵的 <c>GetSymbolInfo</c>/<c>GetTypeInfo</c> 之前快速排除不可能
    /// 命中任何前缀表的节点。子串取自每个前缀的第一个点分段（如
    /// "System.Windows.Forms" → "System.Windows"），因为语法 ToString() 的
    /// 输出必然包含该子串当且仅当 identity 可能以完整前缀开头。
    /// </summary>
    public static class SyntaxPrefilter
    {
        /// <summary>
        /// 所有可能命中的关键子串集合。取自所有前缀表（UI/Platform/Member/
        /// Native），用于对 invocation/memberAccess/objectCreation 的
        /// <c>ToString()</c> 做快速 <c>Contains</c> 检查。任一子串命中才需
        /// 进一步语义查询；全部不命中则可安全跳过（该节点不可能产生站点）。
        /// </summary>
        private static readonly string[] s_syntaxKeys = BuildSyntaxKeys();

        /// <summary>
        /// 用于 typeSyntax 的预过滤子串。typeSyntax.ToString() 在类型位置可能是
        /// 简单名（如 "RegistryKey"）或限定名（如 "Microsoft.Win32.RegistryKey"），
        /// 故除了完整前缀的根子串外还需包含每个前缀的末段简单名。
        /// </summary>
        private static readonly string[] s_typeSyntaxKeys = BuildTypeSyntaxKeys();

        /// <summary>
        /// 用于 attribute 名的预过滤子串（OS guard + native interop 属性简单名）。
        /// 属性名在语法树中通常是无限定名，故直接用简单名表。
        /// </summary>
        private static readonly string[] s_attributeKeys = BuildAttributeKeys();

        /// <summary>
        /// 对 invocation/memberAccess/objectCreation 的 ToString() 做快速检查：
        /// 只有包含任一关键子串时才可能命中前缀表，需要进一步语义查询。
        /// </summary>
        public static bool MightMatchExpression(string text)
            => MightMatchAny(text, s_syntaxKeys);

        /// <summary>
        /// 对 typeSyntax 的 ToString() 做快速检查。
        /// </summary>
        public static bool MightMatchType(string text)
            => MightMatchAny(text, s_typeSyntaxKeys);

        /// <summary>
        /// 对 attribute 名的 ToString() 做快速检查。
        /// </summary>
        public static bool MightMatchAttribute(string text)
            => MightMatchAny(text, s_attributeKeys);

        private static bool MightMatchAny(string text, string[] keys)
        {
            // 空文本不可能命中（调用方已保证 text 非空，但防御性返回 true
            // 以保持正确性——宁可多查不可漏查）。
            if (string.IsNullOrEmpty(text))
            {
                return true;
            }

            foreach (var key in keys)
            {
                if (text.Contains(key, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        private static string[] BuildSyntaxKeys()
        {
            var keys = new HashSet<string>(StringComparer.Ordinal);

            // 从每个前缀提取根子串（去掉最后一段成员名）：
            // "System.Environment.OSVersion" → "System.Environment"
            // "Microsoft.Win32.Registry" → "Microsoft.Win32"
            // "System.Windows.Forms" → "System.Windows"（但 "System.Windows" 自身也是前缀）
            foreach (var prefix in UiNamespacePrefixes
                .Concat(PlatformTypePrefixes)
                .Concat(PlatformMemberPrefixes)
                .Concat(NativeInteropTypePrefixes))
            {
                AddRootSubstrings(keys, prefix);
            }

            // 当源文件用 using 引入命名空间后，invocation/memberAccess 的 ToString()
            // 可能是简单名调用，如 "Marshal.AllocHGlobal(0)"——不包含完整前缀根子串。
            // 故对 PlatformType 和 NativeInterop 类型前缀追加末段类型简单名
            // （如 "Registry"、"RegistryKey"、"EventLog"、"Marshal"、"SafeHandle" 等）。
            // PlatformMember 前缀的末段是方法名（如 "IsWindows"），不出现在
            // memberAccess/invocation 的文本首段中（调用形如 "OperatingSystem.IsWindows()"，
            // 已被根子串 "System.OperatingSystem" 覆盖），故不追加。
            foreach (var prefix in PlatformTypePrefixes.Concat(NativeInteropTypePrefixes))
            {
                var lastDot = prefix.LastIndexOf('.');
                if (lastDot >= 0 && lastDot + 1 < prefix.Length)
                {
                    keys.Add(prefix[(lastDot + 1)..]);
                }
            }

            // PlatformMember 前缀倒数第二段是容器类型名，在 using 简写调用时出现
            // （如 "RuntimeInformation.ProcessArchitecture"、"OperatingSystem.IsWindows()"）。
            // 这些简单名需要追加到预过滤表，否则会漏掉通过 using 引入的探针调用。
            foreach (var prefix in PlatformMemberPrefixes)
            {
                var segments = prefix.Split('.');
                if (segments.Length >= 2)
                {
                    keys.Add(segments[^2]);
                }
            }

            return keys.ToArray();
        }

        private static string[] BuildTypeSyntaxKeys()
        {
            var keys = new HashSet<string>(StringComparer.Ordinal);

            foreach (var prefix in PlatformTypePrefixes.Concat(NativeInteropTypePrefixes))
            {
                AddRootSubstrings(keys, prefix);
                // typeSyntax 可能是简单名，如 "RegistryKey"、"EventLog"
                var lastDot = prefix.LastIndexOf('.');
                if (lastDot >= 0 && lastDot + 1 < prefix.Length)
                {
                    keys.Add(prefix[(lastDot + 1)..]);
                }
            }

            return keys.ToArray();
        }

        private static string[] BuildAttributeKeys()
        {
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var name in OsGuardAttributeNames)
            {
                keys.Add(name);
            }

            foreach (var name in NativeInteropAttributeNames)
            {
                keys.Add(name);
            }

            return keys.ToArray();
        }

        /// <summary>
        /// 从前缀中提取所有有意义的根子串。例如 "System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform"
        /// 产生 "System.Runtime.InteropServices.RuntimeInformation"、"System.Runtime.InteropServices"、
        /// "System.Runtime"、"System"。这些子串必然出现在语法 ToString() 中，
        /// 当且仅当 identity 可能以完整前缀开头。
        /// 但 "System" 太常见会导致过多 false positive，故只取前缀的前两段以上。
        /// </summary>
        private static void AddRootSubstrings(HashSet<string> keys, string prefix)
        {
            var segments = prefix.Split('.');
            for (var count = segments.Length - 1; count >= 2; count--)
            {
                keys.Add(string.Join('.', segments.AsSpan(0, count).ToArray()));
            }
        }
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