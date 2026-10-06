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
    public const string StructLayoutAttribute
        = "System.Runtime.InteropServices.StructLayoutAttribute";
    public const string FieldOffsetAttribute
        = "System.Runtime.InteropServices.FieldOffsetAttribute";

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
            // JsonExtensionData 把未知 JSON 键/值摊到成员（字典/IDictionary），转换器
            // 无成员级物化分支（§8 门 3 sdk 语料 ReferenceHandlerTests.Deserialize.cs
            // L1467 命中）→ BridgeGap；STJ 同名条目已有 compat 物化，二者不可混用别名。
            [NsNewtonsoft + "JsonExtensionDataAttribute"] = Entry(
                AttributeFamily.JsonNewtonsoft, BridgeStatus.BridgeGap, null,
                Array.Empty<ReadArgument>(), new HashSet<AttributeTarget>(),
                new[]
                {
                    "grep Newtonsoft JsonExtensionData src/: no member-level spillover consumption branch",
                    "java/csharptojava-compat/src/main/java/io/github/ningpp/compat/DefaultContractResolver.java",
                },
                "Newtonsoft contract serializer exists but extension-data spillover is not emitted"),

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
            // EnumMember(Value=...) 按名契约同 DataMember：转换器无成员级读取分支 → BridgeGap。
            [NsDataContract + "EnumMemberAttribute"] = Entry(
                AttributeFamily.DataContract, BridgeStatus.BridgeGap, null,
                Array.Empty<ReadArgument>(), new HashSet<AttributeTarget>(),
                new[]
                {
                    "grep EnumMemberAttribute src/: no member-level consumption branch (Value unread)",
                    "java/csharptojava-compat/src/main/java/io/github/ningpp/compat/DataContractSerializer.java",
                },
                "DataContractSerializer exists but EnumMember(Value=) is not emitted"),

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
            // 其余 XmlSerializer 注解转换器无消费分支：按名/形态契约会静默丢失 → BridgeGap
            // （PropertyTransformer.cs:L148-L151 仅消费上述四个；§8 门 3 triage 登记）。
            [NsXml + "XmlElementAttribute"] = XmlGapEntry("element name/order contract"),
            [NsXml + "XmlRootAttribute"] = XmlGapEntry("type-level root element name contract"),
            [NsXml + "XmlTextAttribute"] = XmlGapEntry("xml text content contract"),
            [NsXml + "XmlEnumAttribute"] = XmlGapEntry("enum member wire name contract"),
            [NsXml + "XmlAnyAttributeAttribute"] = XmlGapEntry("catch-all attribute contract"),
            [NsXml + "XmlAnyElementAttribute"] = XmlGapEntry("catch-all element contract"),
            // XmlNamespaceDeclarations 把 xmlns 声明摊到 XmlSerializerNamespaces 成员，
            // PropertyTransformer 无消费分支（§8 门 3 xml-tiny XmlSchemaObject.cs L67
            // 命中的是源拷贝名，真名一并登记）→ BridgeGap。
            [NsXml + "XmlNamespaceDeclarationsAttribute"] = XmlGapEntry("xml namespace declarations spillover contract"),

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
            // DebuggerDisplay 仅调试器显示用、无任何运行时语义（IDE/调试元数据），
            // 按 InventoriedPassthrough 盘点；compat 的 DebuggerDisplay 壳不构成语义契约。
            ["System.Diagnostics.DebuggerDisplayAttribute"] = Entry(
                AttributeFamily.Debugger, BridgeStatus.InventoriedPassthrough, null,
                Array.Empty<ReadArgument>(), new HashSet<AttributeTarget>(),
                new[]
                {
                    "debugger-only metadata; no runtime semantics; compat shell io/github/ningpp/compat/DebuggerDisplay.java is informational",
                }),
            // 其余 System.Diagnostics 调试器元数据全部无运行时语义：DebuggerTypeProxy
            // （代理展示类型，§8 门 3 regex 语料 3 命中）、步进隐藏四件套
            // （StepThrough/Hidden/NonUserCode/StepperBoundary），统一盘点透传。
            ["System.Diagnostics.DebuggerTypeProxyAttribute"] = DebuggerPassthroughEntry(),
            ["System.Diagnostics.DebuggerStepThroughAttribute"] = DebuggerPassthroughEntry(),
            ["System.Diagnostics.DebuggerHiddenAttribute"] = DebuggerPassthroughEntry(),
            ["System.Diagnostics.DebuggerNonUserCodeAttribute"] = DebuggerPassthroughEntry(),
            ["System.Diagnostics.DebuggerStepperBoundaryAttribute"] = DebuggerPassthroughEntry(),

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
            // 纯元数据/分析契约：无运行时语义，转换器不消费。
            ["System.CLSCompliantAttribute"] = Entry(
                AttributeFamily.Compiler, BridgeStatus.InventoriedPassthrough, null,
                Array.Empty<ReadArgument>(), new HashSet<AttributeTarget>(),
                new[] { "CLS compliance is compile-time metadata only; no converter consumption" }),
            ["System.ComponentModel.EditorBrowsableAttribute"] = Entry(
                AttributeFamily.Compiler, BridgeStatus.InventoriedPassthrough, null,
                Array.Empty<ReadArgument>(), new HashSet<AttributeTarget>(),
                new[] { "IDE browse state is design-time metadata only; no converter consumption" }),

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
            // xUnit 生态（NuGet 包属性；预处理编译不带 NuGet 引用时经短名别名命中）
            [NsXunit + "SkippableFactAttribute"] = TestsEntry(),
            [NsXunit + "SkippableTheoryAttribute"] = TestsEntry(),
            [NsXunit + "TraitAttribute"] = TestsEntry(),
            [NsXunit + "CollectionAttribute"] = TestsEntry(),
            [NsXunit + "CollectionDefinitionAttribute"] = TestsEntry(),
            [NsXunit + "CollectionBehaviorAttribute"] = TestsEntry(),
            ["Xunit.Sdk.DataDiscovererAttribute"] = TestsEntry(),

            // ===== Interop/Platform：GovernedElsewhere（C06/C42） =====
            [DllImportAttribute] = Governed(AttributeFamily.Interop, "C06"),
            [LibraryImportAttribute] = Governed(AttributeFamily.Interop, "C06"),
            [ComImportAttribute] = Governed(AttributeFamily.Interop, "C06"),
            [MarshalAsAttribute] = Governed(AttributeFamily.Interop, "C06"),
            [UnmanagedCallersOnlyAttribute] = Governed(AttributeFamily.Interop, "C06"),
            [GeneratedComInterfaceAttribute] = Governed(AttributeFamily.Interop, "C06"),
            // 布局互操作：语义归 C06（CS2J3201 域），本 pass 只盘点透传。
            [StructLayoutAttribute] = Governed(AttributeFamily.Interop, "C06"),
            [FieldOffsetAttribute] = Governed(AttributeFamily.Interop, "C06"),

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

    private static CatalogEntry DebuggerPassthroughEntry()
        => Entry(
            AttributeFamily.Debugger, BridgeStatus.InventoriedPassthrough, null,
            Array.Empty<ReadArgument>(), new HashSet<AttributeTarget>(),
            new[] { "debugger-only metadata; no runtime semantics" });

    private static CatalogEntry XmlGapEntry(string contract)
        => Entry(
            AttributeFamily.XmlSerialization, BridgeStatus.BridgeGap, null,
            Array.Empty<ReadArgument>(), new HashSet<AttributeTarget>(),
            new[]
            {
                "src/CSharpToJava.Core/Transformers/Member/PropertyTransformer.cs:L148-L151 only consumes "
                + "XmlAttribute/XmlIgnore/XmlArray/XmlArrayItem",
            },
            $"XmlSerializer {contract} is not emitted to the Java side");

    private static CatalogEntry Governed(AttributeFamily family, string ownerPass)
        => Entry(
            family, BridgeStatus.GovernedElsewhere, ownerPass,
            Array.Empty<ReadArgument>(), new HashSet<AttributeTarget>(),
            new[] { $"governed by {ownerPass}" });

    /// <summary>
    /// 按符号身份分类一个 attribute；attributeType 不可解析时：
    /// 先按语法短名查「未绑定别名表」（分类表条目的短名唯一映射，覆盖 NuGet
    /// 引用缺失场景），命中歧义或无别名则归 <see cref="BridgeStatus.Unresolved"/>。
    /// overrides（ignore/keep）对绑定名与别名规范名同时生效。
    /// System.Runtime.CompilerServices 命名空间内非 Caller 属性、
    /// System.Diagnostics.CodeAnalysis 命名空间全部属性（纯分析契约）
    /// 归 Compiler/InventoriedPassthrough（design §4.4）。
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

            // System.Diagnostics.CodeAnalysis 命名空间全部为分析器/可空契约元数据，
            // 无运行时语义、转换器永不消费（NotNullWhen/DoesNotReturn/SuppressMessage 等）。
            if (metadataName.StartsWith(
                    "System.Diagnostics.CodeAnalysis.", StringComparison.Ordinal)
                && metadataName.EndsWith("Attribute", StringComparison.Ordinal))
            {
                return new CatalogClassification(
                    metadataName, AttributeFamily.Compiler, BridgeStatus.InventoriedPassthrough, null, null);
            }

            return new CatalogClassification(metadataName, AttributeFamily.Custom, BridgeStatus.Unknown, null, null);
        }

        // ----- 属性类型未绑定（预处理编译缺 NuGet/程序集引用） -----
        var syntaxName = syntax != null ? NormalizeSyntaxName(syntax) : "<unresolved>";

        if (TryResolveUnboundAlias(syntaxName, out var canonicalName, out var aliasEntry))
        {
            if (options.IgnoreAttributes.Contains(canonicalName!))
                return new CatalogClassification(canonicalName!, AttributeFamily.Other, BridgeStatus.Ignored, null, null);
            if (options.KeepAttributes.Contains(canonicalName!))
                return new CatalogClassification(canonicalName!, AttributeFamily.Other, BridgeStatus.InventoriedPassthrough, null, null);
            return new CatalogClassification(
                canonicalName!, aliasEntry!.Family, aliasEntry.Status, aliasEntry.OwnerPass, aliasEntry);
        }

        if (options.IgnoreAttributes.Contains(syntaxName))
            return new CatalogClassification(syntaxName, AttributeFamily.Other, BridgeStatus.Ignored, null, null);
        if (options.KeepAttributes.Contains(syntaxName))
            return new CatalogClassification(syntaxName, AttributeFamily.Other, BridgeStatus.InventoriedPassthrough, null, null);

        return new CatalogClassification(syntaxName, AttributeFamily.Custom, BridgeStatus.Unresolved, null, null);
    }

    /// <summary>
    /// 未绑定属性语法短名 → 分类表唯一条目别名表。键为分类表键的最右标识符
    /// 去掉/保留 <c>Attribute</c> 后缀两种形态；同一短名对应多个条目时
    /// （如 JsonIgnore 同时属于 STJ 与 Newtonsoft）记为歧义，不参与别名。
    /// 延迟构造一次；值为 null 表示歧义键。
    /// </summary>
    private static readonly Lazy<Dictionary<string, CatalogEntry?>> UnboundShortNameAliases
        = new(BuildUnboundShortNameAliases);

    private static Dictionary<string, CatalogEntry?> BuildUnboundShortNameAliases()
    {
        var map = new Dictionary<string, CatalogEntry?>(StringComparer.Ordinal);
        var origins = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var kv in Entries)
        {
            var metadataName = kv.Key;
            var dot = metadataName.LastIndexOf('.');
            var shortName = dot >= 0 ? metadataName[(dot + 1)..] : metadataName;
            AddAlias(shortName, metadataName, kv.Value);

            if (shortName.EndsWith("Attribute", StringComparison.Ordinal)
                && shortName.Length > "Attribute".Length)
            {
                AddAlias(shortName[..^"Attribute".Length], metadataName, kv.Value);
            }
        }

        void AddAlias(string key, string origin, CatalogEntry value)
        {
            if (origins.TryGetValue(key, out var existingOrigin)
                && existingOrigin != origin)
            {
                map[key] = null; // 歧义：不别名（如 JsonIgnore/JsonConverter）
                return;
            }

            origins[key] = origin;
            map.TryAdd(key, value);
        }

        return map;
    }

    /// <summary>
    /// 语法短名解析未绑定别名：唯一命中返回规范名与条目；歧义（同短名多条目）
    /// 或无别名返回 false。
    /// </summary>
    private static bool TryResolveUnboundAlias(
        string syntaxName,
        out string? canonicalName,
        out CatalogEntry? entry)
    {
        canonicalName = null;
        entry = null;
        if (!UnboundShortNameAliases.Value.TryGetValue(syntaxName, out var candidate)
            || candidate == null)
        {
            return false;
        }

        // 反查规范名：短名 → 条目对象 → 条目键（条目共享时取任一同对象键）。
        foreach (var kv in Entries)
        {
            if (ReferenceEquals(kv.Value, candidate))
            {
                canonicalName = kv.Key;
                break;
            }
        }

        if (canonicalName == null) return false;
        entry = candidate;
        return true;
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
