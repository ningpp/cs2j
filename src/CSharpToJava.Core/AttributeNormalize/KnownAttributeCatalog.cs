using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.AttributeNormalize;

/// <summary>
/// 内置已知 attribute 分类结果。
/// </summary>
internal readonly record struct CatalogClassification(
    string MetadataName,
    AttributeFamily Family,
    BridgeStatus Status,
    string? OwnerPass)
{
    public bool IsKnown => Family != AttributeFamily.Custom;
}

/// <summary>
/// 内置 attribute 白名单分类表（design §4.4）。
/// M1 最小表：Caller 四族 + 平台互操作 GovernedElsewhere（C06/C42）
/// + Flags(C05) + ThreadStatic(C09) + Conditional(conditional)。
/// </summary>
internal static class KnownAttributeCatalog
{
    // ----- Caller 四族元数据名 -----
    public const string CallerMemberNameAttribute
        = "System.Runtime.CompilerServices.CallerMemberNameAttribute";
    public const string CallerFilePathAttribute
        = "System.Runtime.CompilerServices.CallerFilePathAttribute";
    public const string CallerLineNumberAttribute
        = "System.Runtime.CompilerServices.CallerLineNumberAttribute";
    public const string CallerArgumentExpressionAttribute
        = "System.Runtime.CompilerServices.CallerArgumentExpressionAttribute";

    // ----- 互操作（C06） -----
    public const string DllImportAttribute
        = "System.Runtime.InteropServices.DllImportAttribute";
    public const string LibraryImportAttribute
        = "System.Runtime.InteropServices.LibraryImportAttribute";
    public const string ComImportAttribute
        = "System.Runtime.InteropServices.ComImportAttribute";
    public const string MarshalAsAttribute
        = "System.Runtime.InteropServices.MarshalAsAttribute";
    public const string UnmanagedCallersOnlyAttribute
        = "System.Runtime.InteropServices.UnmanagedCallersOnlyAttribute";
    public const string GeneratedComInterfaceAttribute
        = "System.Runtime.InteropServices.Marshalling.GeneratedComInterfaceAttribute";

    // ----- OS guard（C42） -----
    public const string SupportedOSPlatformAttribute
        = "System.Runtime.Versioning.SupportedOSPlatformAttribute";
    public const string UnsupportedOSPlatformAttribute
        = "System.Runtime.Versioning.UnsupportedOSPlatformAttribute";
    public const string SupportedOSPlatformGuardAttribute
        = "System.Runtime.Versioning.SupportedOSPlatformGuardAttribute";
    public const string UnsupportedOSPlatformGuardAttribute
        = "System.Runtime.Versioning.UnsupportedOSPlatformGuardAttribute";

    // ----- 其它 GovernedElsewhere -----
    public const string FlagsAttribute = "System.FlagsAttribute";
    public const string ThreadStaticAttribute = "System.ThreadStaticAttribute";
    public const string ConditionalAttribute = "System.Diagnostics.ConditionalAttribute";

    /// <summary>Caller 四族元数据名集合。</summary>
    public static readonly IReadOnlySet<string> KnownCallerAttributeNames
        = new HashSet<string>(StringComparer.Ordinal)
        {
            CallerMemberNameAttribute,
            CallerFilePathAttribute,
            CallerLineNumberAttribute,
            CallerArgumentExpressionAttribute,
        };

    private static readonly Dictionary<string, (AttributeFamily Family, BridgeStatus Status, string? OwnerPass)> Entries
        = new(StringComparer.Ordinal)
        {
            [CallerMemberNameAttribute] = (AttributeFamily.CallerInfo, BridgeStatus.Bridged, null),
            [CallerFilePathAttribute] = (AttributeFamily.CallerInfo, BridgeStatus.Bridged, null),
            [CallerLineNumberAttribute] = (AttributeFamily.CallerInfo, BridgeStatus.Bridged, null),
            [CallerArgumentExpressionAttribute] = (AttributeFamily.CallerInfo, BridgeStatus.Bridged, null),

            [DllImportAttribute] = (AttributeFamily.Interop, BridgeStatus.GovernedElsewhere, "C06"),
            [LibraryImportAttribute] = (AttributeFamily.Interop, BridgeStatus.GovernedElsewhere, "C06"),
            [ComImportAttribute] = (AttributeFamily.Interop, BridgeStatus.GovernedElsewhere, "C06"),
            [MarshalAsAttribute] = (AttributeFamily.Interop, BridgeStatus.GovernedElsewhere, "C06"),
            [UnmanagedCallersOnlyAttribute] = (AttributeFamily.Interop, BridgeStatus.GovernedElsewhere, "C06"),
            [GeneratedComInterfaceAttribute] = (AttributeFamily.Interop, BridgeStatus.GovernedElsewhere, "C06"),

            [SupportedOSPlatformAttribute] = (AttributeFamily.Platform, BridgeStatus.GovernedElsewhere, "C42"),
            [UnsupportedOSPlatformAttribute] = (AttributeFamily.Platform, BridgeStatus.GovernedElsewhere, "C42"),
            [SupportedOSPlatformGuardAttribute] = (AttributeFamily.Platform, BridgeStatus.GovernedElsewhere, "C42"),
            [UnsupportedOSPlatformGuardAttribute] = (AttributeFamily.Platform, BridgeStatus.GovernedElsewhere, "C42"),

            [FlagsAttribute] = (AttributeFamily.Flags, BridgeStatus.GovernedElsewhere, "C05"),
            [ThreadStaticAttribute] = (AttributeFamily.Compiler, BridgeStatus.GovernedElsewhere, "C09"),
            [ConditionalAttribute] = (AttributeFamily.Compiler, BridgeStatus.GovernedElsewhere, "conditional"),
        };

    /// <summary>
    /// 按符号身份分类一个 attribute；attributeType 不可解析时退回语法名 → Custom。
    /// </summary>
    public static CatalogClassification Classify(
        INamedTypeSymbol? attributeType,
        AttributeSyntax? syntax,
        AttributeNormalizeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        string? metadataName = attributeType?.ToDisplayString();

        if (metadataName != null)
        {
            if (options.IgnoreAttributes.Contains(metadataName))
                return new CatalogClassification(metadataName, AttributeFamily.Other, BridgeStatus.Ignored, null);
            if (options.KeepAttributes.Contains(metadataName))
                return new CatalogClassification(metadataName, AttributeFamily.Other, BridgeStatus.InventoriedPassthrough, null);
            if (Entries.TryGetValue(metadataName, out var entry))
                return new CatalogClassification(metadataName, entry.Family, entry.Status, entry.OwnerPass);

            return new CatalogClassification(metadataName, AttributeFamily.Custom, BridgeStatus.Unknown, null);
        }

        var syntaxName = syntax != null ? NormalizeSyntaxName(syntax) : "<unresolved>";
        return new CatalogClassification(syntaxName, AttributeFamily.Custom, BridgeStatus.Unknown, null);
    }

    /// <summary>符号是否为 Caller 族；是则给出族种类。</summary>
    public static bool TryGetCallerKind(INamedTypeSymbol? attributeType, out CallerInfoKind kind)
    {
        kind = default;
        if (attributeType == null) return false;

        switch (attributeType.ToDisplayString())
        {
            case CallerMemberNameAttribute: kind = CallerInfoKind.CallerMemberName; return true;
            case CallerFilePathAttribute: kind = CallerInfoKind.CallerFilePath; return true;
            case CallerLineNumberAttribute: kind = CallerInfoKind.CallerLineNumber; return true;
            case CallerArgumentExpressionAttribute: kind = CallerInfoKind.CallerArgumentExpression; return true;
            default: return false;
        }
    }

    public static bool IsCallerAttribute(INamedTypeSymbol? attributeType)
        => TryGetCallerKind(attributeType, out _);

    /// <summary>语法名简单归一：取 attribute 名的最右标识符文本。</summary>
    public static string NormalizeSyntaxName(AttributeSyntax syntax)
    {
        var name = syntax.Name.ToString();
        var dot = name.LastIndexOf('.');
        if (dot >= 0) name = name[(dot + 1)..];
        return name;
    }
}
