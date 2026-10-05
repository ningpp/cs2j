using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.AttributeNormalize;

/// <summary>
/// attribute 使用点目标种类（决定该使用点是否落在转换器消费面上）。
/// </summary>
internal enum AttributeTarget
{
    Property,
    Field,
    Type,
    Method,
    Parameter,
    Event,
    ReturnValue,
    Assembly,
    Other,
}

/// <summary>
/// 已读实参的物化判据：转换器在读取该实参时对 TypedConstant 的实际形状要求。
/// </summary>
internal enum ArgValueKind
{
    /// <summary>只要提供即物化（转换器按表达式/值原文 ToString，含 null 字面量）。</summary>
    Always,

    /// <summary>Value != null 即物化。</summary>
    AnyNonNull,

    /// <summary>Value 必须是非 null string。</summary>
    String,

    /// <summary>Value 必须是非 null int（枚举底层值按 int 读取）。</summary>
    Int32,

    /// <summary>TypedConstant.Kind 必须是 Type（typeof 形式）。</summary>
    TypeOf,
}

/// <summary>
/// 转换器/compat 已读取的单个实参（design §4.4「已读取实参集合」）。
/// </summary>
/// <param name="ParameterName">构造参数（位置实参）或具名属性名。</param>
/// <param name="Kind">转换器对该实参的物化判据。</param>
/// <param name="Positional">true=位置（构造参数）；false=具名参数。</param>
internal sealed record ReadArgument(string ParameterName, ArgValueKind Kind, bool Positional);

/// <summary>
/// 内置分类表的一条目：携带 family、默认状态、证据指针、已读实参集合与
/// 转换器消费面（design §4.4，实施时逐条补证据指针）。
/// </summary>
internal sealed record CatalogEntry(
    AttributeFamily Family,
    BridgeStatus Status,
    string? OwnerPass,
    IReadOnlyList<ReadArgument> ReadArguments,
    IReadOnlySet<AttributeTarget> ConsumedTargets,
    IReadOnlyList<string> Evidence,
    string? BridgeNote = null)
{
    public bool TryGetPositionalRead(string parameterName, out ReadArgument? argument)
    {
        foreach (var read in ReadArguments)
        {
            if (read.Positional && read.ParameterName == parameterName)
            {
                argument = read;
                return true;
            }
        }

        argument = null;
        return false;
    }

    public bool TryGetNamedRead(string key, out ReadArgument? argument)
    {
        foreach (var read in ReadArguments)
        {
            if (!read.Positional && read.ParameterName == key)
            {
                argument = read;
                return true;
            }
        }

        argument = null;
        return false;
    }
}

/// <summary>
/// 内置已知 attribute 分类结果。
/// </summary>
internal readonly record struct CatalogClassification(
    string MetadataName,
    AttributeFamily Family,
    BridgeStatus Status,
    string? OwnerPass,
    CatalogEntry? Entry)
{
    public bool IsKnown => Family != AttributeFamily.Custom;
}

/// <summary>
/// 内置 attribute 白名单分类表（design §4.4 全族分类表）。
/// 每条目携带 family、默认 BridgeStatus、转换器/compat 已读实参集合、
/// 消费面与证据指针（文件:行/compat 类）。
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

    // 前缀
    private const string Stj = "System.Text.Json.Serialization.";
    private const string NsNewtonsoft = "Newtonsoft.Json.";
    private const string NsDataContract = "System.Runtime.Serialization.";
    private const string NsXml = "System.Xml.Serialization.";
    private const string NsXunit = "Xunit.";
    private const string NsNunit = "NUnit.Framework.";
    private const string NsMstest = "Microsoft.VisualStudio.TestTools.UnitTesting.";

    /// <summary>Caller 四族元数据名集合。</summary>
    public static readonly IReadOnlySet<string> KnownCallerAttributeNames
        = new HashSet<string>(StringComparer.Ordinal)
        {
            CallerMemberNameAttribute,
            CallerFilePathAttribute,
            CallerLineNumberAttribute,
            CallerArgumentExpressionAttribute,
        };

    private static readonly HashSet<AttributeTarget> PropertyAndField
        = new() { AttributeTarget.Property, AttributeTarget.Field };

    private static readonly HashSet<AttributeTarget> PropertyOnly
        = new() { AttributeTarget.Property };

    private static CatalogEntry Entry(
        AttributeFamily family,
        BridgeStatus status,
        string? ownerPass,
        IReadOnlyList<ReadArgument> reads,
        IReadOnlySet<AttributeTarget> targets,
        IReadOnlyList<string> evidence,
        string? note = null)
        => new(family, status, ownerPass, reads, targets, evidence, note);

    private static IReadOnlyList<ReadArgument> Pos(params ReadArgument[] args) => args;
    private static IReadOnlyList<ReadArgument> Named(params ReadArgument[] args) => args;

    private static readonly Dictionary<string, CatalogEntry> Entries
        = new(StringComparer.Ordinal)
        {
            // ===== CallerInfo：本 pass 独占，改写后移除 =====
            [CallerMemberNameAttribute] = Entry(
                AttributeFamily.CallerInfo, BridgeStatus.Bridged, null,
                Array.Empty<ReadArgument>(), new HashSet<AttributeTarget>(),
                new[] { "C10 pass (CallerInfoRewriter); no converter consumption" }),
            [CallerFilePathAttribute] = Entry(
                AttributeFamily.CallerInfo, BridgeStatus.Bridged, null,
                Array.Empty<ReadArgument>(), new HashSet<AttributeTarget>(),
                new[] { "C10 pass (CallerInfoRewriter); no converter consumption" }),
            [CallerLineNumberAttribute] = Entry(
                AttributeFamily.CallerInfo, BridgeStatus.Bridged, null,
                Array.Empty<ReadArgument>(), new HashSet<AttributeTarget>(),
                new[] { "C10 pass (CallerInfoRewriter); no converter consumption" }),
            [CallerArgumentExpressionAttribute] = Entry(
                AttributeFamily.CallerInfo, BridgeStatus.Bridged, null,
                Array.Empty<ReadArgument>(), new HashSet<AttributeTarget>(),
                new[] { "C10 pass (CallerInfoRewriter); no converter consumption" }),

            // ===== JsonSystemTextJson：Bridged，逐使用点核对实参 =====
            // PropertyTransformer.cs:L158-L159：具名 Condition(int) → CompatJsonIgnore(v)。
            [Stj + "JsonIgnoreAttribute"] = Entry(
                AttributeFamily.JsonSystemTextJson, BridgeStatus.Bridged, null,
                Named(new ReadArgument("Condition", ArgValueKind.Int32, Positional: false)),
                PropertyOnly,
                new[]
                {
                    "src/CSharpToJava.Core/Transformers/Member/PropertyTransformer.cs:L158-L159",
                    "java/csharptojava-compat/src/main/java/io/github/ningpp/compat/CompatJsonIgnore.java:L19-L27",
                }),
            // PropertyTransformer.cs:L166-L167 / FieldTransformer.cs:L210-L218：ctor name(string)。
            [Stj + "JsonPropertyNameAttribute"] = Entry(
                AttributeFamily.JsonSystemTextJson, BridgeStatus.Bridged, null,
                Pos(new ReadArgument("name", ArgValueKind.String, Positional: true)),
                PropertyAndField,
                new[]
                {
                    "src/CSharpToJava.Core/Transformers/Member/PropertyTransformer.cs:L166-L167",
                    "src/CSharpToJava.Core/Transformers/Member/FieldTransformer.cs:L210-L218",
                    "java/csharptojava-compat/src/main/java/io/github/ningpp/compat/CompatJsonPropertyName.java",
                }),
            // PropertyTransformer.cs:L165 / FieldTransformer.cs:L189-L194：无实参。
            [Stj + "JsonIncludeAttribute"] = Entry(
                AttributeFamily.JsonSystemTextJson, BridgeStatus.Bridged, null,
                Array.Empty<ReadArgument>(), PropertyAndField,
                new[]
                {
                    "src/CSharpToJava.Core/Transformers/Member/PropertyTransformer.cs:L165",
                    "src/CSharpToJava.Core/Transformers/Member/FieldTransformer.cs:L189-L194",
                    "java/csharptojava-compat/src/main/java/io/github/ningpp/compat/CompatJsonInclude.java",
                }),
            // PropertyTransformer.cs:L168,L179-L192：ctor converterType(typeof)。
            [Stj + "JsonConverterAttribute"] = Entry(
                AttributeFamily.JsonSystemTextJson, BridgeStatus.Bridged, null,
                Pos(new ReadArgument("converterType", ArgValueKind.TypeOf, Positional: true)),
                PropertyOnly,
                new[]
                {
                    "src/CSharpToJava.Core/Transformers/Member/PropertyTransformer.cs:L168,L179-L192",
                    "java/csharptojava-compat/src/main/java/io/github/ningpp/compat/CompatJsonConverter.java:L29-L40",
                }),
            // PropertyTransformer.cs:L160-L161 / FieldTransformer.cs:L169-L177：ctor handling。
            [Stj + "JsonNumberHandlingAttribute"] = Entry(
                AttributeFamily.JsonSystemTextJson, BridgeStatus.Bridged, null,
                Pos(new ReadArgument("handling", ArgValueKind.AnyNonNull, Positional: true)),
                PropertyAndField,
                new[]
                {
                    "src/CSharpToJava.Core/Transformers/Member/PropertyTransformer.cs:L160-L161",
                    "src/CSharpToJava.Core/Transformers/Member/FieldTransformer.cs:L169-L177",
                    "java/csharptojava-compat/src/main/java/io/github/ningpp/compat/CompatJsonNumberHandling.java",
                }),
            // PropertyTransformer.cs:L157：无实参。
            [Stj + "JsonExtensionDataAttribute"] = Entry(
                AttributeFamily.JsonSystemTextJson, BridgeStatus.Bridged, null,
                Array.Empty<ReadArgument>(), PropertyOnly,
                new[]
                {
                    "src/CSharpToJava.Core/Transformers/Member/PropertyTransformer.cs:L157",
                    "java/csharptojava-compat/src/main/java/io/github/ningpp/compat/CompatJsonExtensionData.java",
                }),
            // 无构造实参；仅 E08 语法名识别与 StructAnalyzer 只读分析，不报缺口。
            [Stj + "JsonConstructorAttribute"] = Entry(
                AttributeFamily.JsonSystemTextJson, BridgeStatus.Bridged, null,
                Array.Empty<ReadArgument>(), new HashSet<AttributeTarget>(),
                new[]
                {
                    "src/CSharpToJava.Core/DefaultParameterStripper/DefaultParameterDeclarationRewriter.cs:L1075-L1095",
                    "src/CSharpToJava.Core/ReadOnlyStructMaker/StructAnalyzer.cs:L405-L412",
                },
                "JsonConstructor has no arguments; presence recognized by E08/StructAnalyzer"),

            // ===== JsonNewtonsoft：BridgeGap（成员级无符号读取分支） =====
            [NsNewtonsoft + "JsonPropertyAttribute"] = Entry(
                AttributeFamily.JsonNewtonsoft, BridgeStatus.BridgeGap, null,
                Array.Empty<ReadArgument>(), new HashSet<AttributeTarget>(),
                new[]
                {
                    "grep JsonPropertyAttribute src/: no member-level consumption branch",
                    "java/csharptojava-compat/src/main/java/io/github/ningpp/compat/DefaultContractResolver.java",
                    "java/csharptojava-compat/src/main/java/io/github/ningpp/compat/JsonConvert.java",
                },
                "Newtonsoft contract serializer exists but member arguments (PropertyName etc.) are not emitted"),
            [NsNewtonsoft + "JsonIgnoreAttribute"] = Entry(
                AttributeFamily.JsonNewtonsoft, BridgeStatus.BridgeGap, null,
                Array.Empty<ReadArgument>(), new HashSet<AttributeTarget>(),
                new[]
                {
                    "grep Newtonsoft JsonIgnore src/: no member-level consumption branch",
                    "java/csharptojava-compat/src/main/java/io/github/ningpp/compat/DefaultContractResolver.java",
                },
                "Newtonsoft contract serializer exists but member attributes are not emitted"),
            [NsNewtonsoft + "JsonConverterAttribute"] = Entry(
                AttributeFamily.JsonNewtonsoft, BridgeStatus.BridgeGap, null,
                Array.Empty<ReadArgument>(), new HashSet<AttributeTarget>(),
                new[]
                {
                    "grep Newtonsoft JsonConverter src/: no member-level consumption branch",
                    "java/csharptojava-compat/src/main/java/io/github/ningpp/compat/JsonSerializerSettings.java",
                },
                "Newtonsoft serializer settings exist but converter attributes are not emitted"),
            [NsNewtonsoft + "JsonObjectAttribute"] = Entry(
                AttributeFamily.JsonNewtonsoft, BridgeStatus.BridgeGap, null,
                Array.Empty<ReadArgument>(), new HashSet<AttributeTarget>(),
                new[]
                {
                    "src/CSharpToJava.Core/ReadOnlyStructMaker/StructAnalyzer.cs:L559 (read-only analysis)",
                    "java/csharptojava-compat/src/main/java/io/github/ningpp/compat/DefaultContractResolver.java",
                },
                "Newtonsoft contract serializer exists but type-level JsonObject is not emitted"),

            // ===== DataContract：BridgeGap（Name/EmitDefaultValue 无读取分支） =====
            [NsDataContract + "DataContractAttribute"] = Entry(
                AttributeFamily.DataContract, BridgeStatus.BridgeGap, null,
                Array.Empty<ReadArgument>(), new HashSet<AttributeTarget>(),
                new[]
                {
                    "src/CSharpToJava.Core/ReadOnlyStructMaker/StructAnalyzer.cs:L544-L563 (read-only analysis)",
                    "java/csharptojava-compat/src/main/java/io/github/ningpp/compat/DataContractSerializer.java",
                },
                "DataContractSerializer exists but member/type arguments are not emitted"),
            [NsDataContract + "DataMemberAttribute"] = Entry(
                AttributeFamily.DataContract, BridgeStatus.BridgeGap, null,
                Array.Empty<ReadArgument>(), new HashSet<AttributeTarget>(),
                new[]
                {
                    "grep DataMemberAttribute src/: no member-level consumption branch (Name/EmitDefaultValue unread)",
                    "java/csharptojava-compat/src/main/java/io/github/ningpp/compat/DataContractSerializer.java",
                },
                "DataContractSerializer exists but DataMember arguments (Name/EmitDefaultValue) are not emitted"),
            [NsDataContract + "IgnoreDataMemberAttribute"] = Entry(
                AttributeFamily.DataContract, BridgeStatus.BridgeGap, null,
                Array.Empty<ReadArgument>(), new HashSet<AttributeTarget>(),
                new[]
                {
                    "grep IgnoreDataMemberAttribute src/: no member-level consumption branch",
                    "java/csharptojava-compat/src/main/java/io/github/ningpp/compat/DataContractSerializer.java",
                },
                "DataContractSerializer exists but IgnoreDataMember is not emitted"),

            // ===== XmlSerialization：Bridged，逐参数核对（PropertyTransformer.cs:L142-L153） =====
            [NsXml + "XmlAttributeAttribute"] = Entry(
                AttributeFamily.XmlSerialization, BridgeStatus.Bridged, null,
                Pos(new ReadArgument("attributeName", ArgValueKind.Always, Positional: true)),
                PropertyOnly,
                new[] { "src/CSharpToJava.Core/Transformers/Member/PropertyTransformer.cs:L147-L153" }),
            [NsXml + "XmlIgnoreAttribute"] = Entry(
                AttributeFamily.XmlSerialization, BridgeStatus.Bridged, null,
                Array.Empty<ReadArgument>(), PropertyOnly,
                new[] { "src/CSharpToJava.Core/Transformers/Member/PropertyTransformer.cs:L149" }),
            [NsXml + "XmlArrayAttribute"] = Entry(
                AttributeFamily.XmlSerialization, BridgeStatus.Bridged, null,
                Pos(new ReadArgument("elementName", ArgValueKind.Always, Positional: true)),
                PropertyOnly,
                new[] { "src/CSharpToJava.Core/Transformers/Member/PropertyTransformer.cs:L150" }),
            [NsXml + "XmlArrayItemAttribute"] = Entry(
                AttributeFamily.XmlSerialization, BridgeStatus.Bridged, null,
                Pos(new ReadArgument("elementName", ArgValueKind.Always, Positional: true)),
                PropertyOnly,
                new[] { "src/CSharpToJava.Core/Transformers/Member/PropertyTransformer.cs:L151" }),

            // ===== Debugger：部分 Bridged =====
            // PropertyTransformer.cs:L163-L164：ctor state(int)，仅属性消费。
            ["System.Diagnostics.DebuggerBrowsableAttribute"] = Entry(
                AttributeFamily.Debugger, BridgeStatus.Bridged, null,
                Pos(new ReadArgument("state", ArgValueKind.Int32, Positional: true)),
                PropertyOnly,
                new[]
                {
                    "src/CSharpToJava.Core/Transformers/Member/PropertyTransformer.cs:L163-L164",
                    "java/csharptojava-compat/src/main/java/io/github/ningpp/compat/DebuggerBrowsable.java",
                }),
            // compat 有 DebuggerDisplay 壳；转换器无消费分支 → BridgeGap。
            ["System.Diagnostics.DebuggerDisplayAttribute"] = Entry(
                AttributeFamily.Debugger, BridgeStatus.BridgeGap, null,
                Array.Empty<ReadArgument>(), new HashSet<AttributeTarget>(),
                new[]
                {
                    "grep DebuggerDisplayAttribute src/: no converter consumption branch",
                    "java/csharptojava-compat/src/main/java/io/github/ningpp/compat/DebuggerDisplay.java",
                },
                "DebuggerDisplay shell exists in compat but its arguments are not emitted"),

            // ===== Compiler/BCL：InventoriedPassthrough =====
            ["System.ObsoleteAttribute"] = Entry(
                AttributeFamily.Compiler, BridgeStatus.InventoriedPassthrough, null,
                Array.Empty<ReadArgument>(), new HashSet<AttributeTarget>(),
                new[]
                {
                    "grep ObsoleteAttribute src/: no converter consumption",
                    "java/csharptojava-compat/src/main/java/io/github/ningpp/compat/ObsoleteAttribute.java",
                }),
            ["System.ComponentModel.DefaultValueAttribute"] = Entry(
                AttributeFamily.Compiler, BridgeStatus.InventoriedPassthrough, null,
                Array.Empty<ReadArgument>(), new HashSet<AttributeTarget>(),
                new[]
                {
                    "grep DefaultValueAttribute src/: no converter consumption",
                    "java/csharptojava-compat/src/main/java/io/github/ningpp/compat/DefaultValueAttribute.java",
                }),
            ["System.SerializableAttribute"] = Entry(
                AttributeFamily.Compiler, BridgeStatus.InventoriedPassthrough, null,
                Array.Empty<ReadArgument>(), new HashSet<AttributeTarget>(),
                new[]
                {
                    "src/CSharpToJava.Core/ReadOnlyStructMaker/StructAnalyzer.cs:L551 (read-only analysis)",
                }),
            ["System.AttributeUsageAttribute"] = Entry(
                AttributeFamily.Compiler, BridgeStatus.InventoriedPassthrough, null,
                Array.Empty<ReadArgument>(), new HashSet<AttributeTarget>(),
                new[] { "compiler-consumed attribute; no converter consumption" }),

            // ===== Tests：InventoriedPassthrough（测试项目内不告警） =====
            // xUnit
            [NsXunit + "FactAttribute"] = TestsEntry(),
            [NsXunit + "TheoryAttribute"] = TestsEntry(),
            [NsXunit + "InlineDataAttribute"] = TestsEntry(),
            [NsXunit + "MemberDataAttribute"] = TestsEntry(),
            [NsXunit + "ClassDataAttribute"] = TestsEntry(),
            // NUnit
            [NsNunit + "TestAttribute"] = TestsEntry(),
            [NsNunit + "TestCaseAttribute"] = TestsEntry(),
            [NsNunit + "TestFixtureAttribute"] = TestsEntry(),
            [NsNunit + "SetUpAttribute"] = TestsEntry(),
            [NsNunit + "TearDownAttribute"] = TestsEntry(),
            [NsNunit + "OneTimeSetUpAttribute"] = TestsEntry(),
            [NsNunit + "OneTimeTearDownAttribute"] = TestsEntry(),
            // MSTest
            [NsMstest + "TestClassAttribute"] = TestsEntry(),
            [NsMstest + "TestMethodAttribute"] = TestsEntry(),
            [NsMstest + "DataTestMethodAttribute"] = TestsEntry(),
            [NsMstest + "DataRowAttribute"] = TestsEntry(),
            [NsMstest + "TestInitializeAttribute"] = TestsEntry(),
            [NsMstest + "TestCleanupAttribute"] = TestsEntry(),

            // ===== Interop/Platform：GovernedElsewhere（C06/C42） =====
            [DllImportAttribute] = Governed(AttributeFamily.Interop, "C06"),
            [LibraryImportAttribute] = Governed(AttributeFamily.Interop, "C06"),
            [ComImportAttribute] = Governed(AttributeFamily.Interop, "C06"),
            [MarshalAsAttribute] = Governed(AttributeFamily.Interop, "C06"),
            [UnmanagedCallersOnlyAttribute] = Governed(AttributeFamily.Interop, "C06"),
            [GeneratedComInterfaceAttribute] = Governed(AttributeFamily.Interop, "C06"),

            [SupportedOSPlatformAttribute] = Governed(AttributeFamily.Platform, "C42"),
            [UnsupportedOSPlatformAttribute] = Governed(AttributeFamily.Platform, "C42"),
            [SupportedOSPlatformGuardAttribute] = Governed(AttributeFamily.Platform, "C42"),
            [UnsupportedOSPlatformGuardAttribute] = Governed(AttributeFamily.Platform, "C42"),

            // ===== Flags / ThreadStatic / Conditional：GovernedElsewhere =====
            [FlagsAttribute] = Governed(AttributeFamily.Flags, "C05"),
            [ThreadStaticAttribute] = Governed(AttributeFamily.Compiler, "C09"),
            [ConditionalAttribute] = Governed(AttributeFamily.Compiler, "conditional"),
        };

    private static CatalogEntry TestsEntry()
        => Entry(
            AttributeFamily.Tests, BridgeStatus.InventoriedPassthrough, null,
            Array.Empty<ReadArgument>(), new HashSet<AttributeTarget>(),
            new[] { "test framework annotation (xUnit/NUnit/MSTest); not warned inside test projects" });

    private static CatalogEntry Governed(AttributeFamily family, string ownerPass)
        => Entry(
            family, BridgeStatus.GovernedElsewhere, ownerPass,
            Array.Empty<ReadArgument>(), new HashSet<AttributeTarget>(),
            new[] { $"governed by {ownerPass}" });

    /// <summary>
    /// 按符号身份分类一个 attribute；attributeType 不可解析时退回语法名 → Custom。
    /// overrides（ignore/keep）优先于内置表；System.Runtime.CompilerServices 命名空间内
    /// 非 Caller 属性归 Compiler/InventoriedPassthrough（design §4.4）。
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
                return new CatalogClassification(metadataName, AttributeFamily.Other, BridgeStatus.Ignored, null, null);
            if (options.KeepAttributes.Contains(metadataName))
                return new CatalogClassification(metadataName, AttributeFamily.Other, BridgeStatus.InventoriedPassthrough, null, null);
            if (Entries.TryGetValue(metadataName, out var entry))
                return new CatalogClassification(metadataName, entry.Family, entry.Status, entry.OwnerPass, entry);

            // 非 Caller 的 CompilerServices：InventoriedPassthrough（Caller 四族已在表中先命中）。
            if (attributeType!.ContainingNamespace.ToDisplayString()
                == "System.Runtime.CompilerServices")
            {
                return new CatalogClassification(
                    metadataName, AttributeFamily.Compiler, BridgeStatus.InventoriedPassthrough, null, null);
            }

            return new CatalogClassification(metadataName, AttributeFamily.Custom, BridgeStatus.Unknown, null, null);
        }

        var syntaxName = syntax != null ? NormalizeSyntaxName(syntax) : "<unresolved>";
        return new CatalogClassification(syntaxName, AttributeFamily.Custom, BridgeStatus.Unknown, null, null);
    }

    /// <summary>按元数据名查内置条目（诊断消息拼证据指针用）。</summary>
    internal static bool TryGetEntry(string metadataName, out CatalogEntry entry)
        => Entries.TryGetValue(metadataName, out entry!);

    // =====================================================================
    // AN-P2：主管辖诊断码（design §4.3/§6；与 UnsupportedDomainPasses
    // 实际发射的 CS2J3xxx 码对齐；C10 自己永不发射这些码）
    // =====================================================================

    /// <summary>互操作 attribute 的主管辖诊断码（attribute 本身）。</summary>
    public const string GoverningInteropAttributeCode = "CS2J3201";

    /// <summary>extern 方法的主管辖诊断码（互操作 attribute 落在 extern 声明时附带）。</summary>
    public const string GoverningExternMethodCode = "CS2J3203";

    /// <summary>OS guard attribute 的主管辖诊断码。</summary>
    public const string GoverningPlatformAttributeCode = "CS2J3101";

    /// <summary>
    /// 按 family 取主管辖诊断码引用（AN-P2）：Interop→CS2J3201、
    /// Platform→CS2J3101、其余族→空。extern 方法上的互操作出现点额外
    /// 附带 CS2J3203（由调用方经 <see cref="GoverningExternMethodCode"/> 追加）。
    /// 返回确定性顺序的只读列表。
    /// </summary>
    public static IReadOnlyList<string> GetGoverningDiagnosticCodes(AttributeFamily family) => family switch
    {
        AttributeFamily.Interop => new[] { GoverningInteropAttributeCode },
        AttributeFamily.Platform => new[] { GoverningPlatformAttributeCode },
        _ => Array.Empty<string>(),
    };

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

    // =====================================================================
    // AN-U2 denylist（CS2J1321）
    // =====================================================================

    /// <summary>
    /// 内置 denylist：已知无桥且语义可能被依赖、且无主管辖 pass 的行为型 attribute。
    /// 按设计「宁少勿滥」，M2 sdk 测量未发现明确候选 → 表为空。
    /// </summary>
    private static readonly IReadOnlySet<string> BuiltInDenyList
        = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>测试注入表（internal seam，仅测试用）。</summary>
    private static volatile IReadOnlySet<string>? _testDenyList;

    /// <summary>
    /// 测试 hook（InternalsVisibleTo）：临时替换 denylist，返回值 dispose 后恢复。
    /// 生产路径永不调用。
    /// </summary>
    internal static IDisposable SetTestDenyList(IEnumerable<string> metadataNames)
    {
        ArgumentNullException.ThrowIfNull(metadataNames);
        var previous = _testDenyList;
        _testDenyList = new HashSet<string>(metadataNames, StringComparer.Ordinal);
        return new RestoreScope(() => _testDenyList = previous);
    }

    /// <summary>属性是否命中 denylist（内置表 + 测试注入表）。</summary>
    public static bool IsDenied(string metadataName)
    {
        if (BuiltInDenyList.Contains(metadataName)) return true;
        return _testDenyList?.Contains(metadataName) == true;
    }

    /// <summary>内置 denylist 当前条目数（报告/验证用）。</summary>
    public static int BuiltInDenyListCount => BuiltInDenyList.Count;

    private sealed class RestoreScope(Action restore) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            restore();
        }
    }
}
