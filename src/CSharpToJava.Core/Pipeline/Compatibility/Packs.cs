using CSharpToJava.Core.Context;

namespace CSharpToJava.Core.Pipeline.Compatibility;

/// <summary>
/// Ref/Out 参数 Holder 类 Pack — 生成 IntHolder, DoubleHolder, ObjectHolder&lt;T&gt; 等
/// </summary>
public class RefHolderPack : ICompatibilityPack
{
    public string Id => "ref-holder";
    public string Description => "Holder classes for ref/out parameter semantics (IntHolder, ObjectHolder<T>, StopwatchHelper)";
    public IReadOnlyList<string> MavenDependencies => [];

    public bool IsApplicable(CompatibilityPackContext context)
    {
        return context.ReferencesAnyType(
            "IntHolder", "LongHolder", "DoubleHolder", "FloatHolder",
            "BoolHolder", "CharHolder", "ShortHolder", "ByteHolder",
            "ObjectHolder", "StopwatchHelper");
    }

    public IReadOnlyList<ConversionResult> Generate(string targetPackage)
    {
        return CompatibilityClassGenerator.GenerateHolderClasses(targetPackage);
    }
}

/// <summary>
/// .NET 核心工具类 Pack — StringHelper, MathHelper, EnumHelper, ArrayHelper, LinkedList, 异常等
/// </summary>
public class DotNetCorePack : ICompatibilityPack
{
    public string Id => "dotnet-core";
    public string Description => "Core .NET BCL bridges (StringHelper, MathHelper, EnumHelper, FileHelper, LinkedList, etc.)";
    public IReadOnlyList<string> MavenDependencies => [];

    public bool IsApplicable(CompatibilityPackContext context)
    {
        return context.ReferencesAnyType(
            "StringHelper", "MathHelper", "EnumHelper", "FileHelper",
            "FileMode", "CultureInfo", "IEqualityComparer",
            "LinkedListNode", "LinkedListWithNodes",
            "InvalidDataException", "TextReader", "ThreadHelper", "ArrayHelper");
    }

    public IReadOnlyList<ConversionResult> Generate(string targetPackage)
    {
        return CompatibilityClassGenerator.GenerateUtilityClasses(targetPackage);
    }
}

/// <summary>
/// 正则表达式 Pack — Regex, Match, Group, GroupCollection, RegexOptions
/// </summary>
public class RegexPack : ICompatibilityPack
{
    public string Id => "regex";
    public string Description => "System.Text.RegularExpressions → java.util.regex bridge";
    public IReadOnlyList<string> MavenDependencies => [];

    public bool IsApplicable(CompatibilityPackContext context)
    {
        return context.ReferencesAnyType(
            "RegexOptions", "GroupCollection", "new Regex(", "Match.Empty");
    }

    public IReadOnlyList<ConversionResult> Generate(string targetPackage)
    {
        return CompatibilityClassGenerator.GenerateRegexCompatibilityClasses(targetPackage);
    }
}

/// <summary>
/// XML Pack — XmlReader, XmlWriter, XmlConvert 等 StAX 桥接
/// </summary>
public class XmlPack : ICompatibilityPack
{
    public string Id => "xml";
    public string Description => "System.Xml → javax.xml.stream (StAX) bridge";
    public IReadOnlyList<string> MavenDependencies => [];

    public bool IsApplicable(CompatibilityPackContext context)
    {
        return context.ReferencesAnyType(
            "XmlReader", "XmlWriter", "XmlTextReader",
            "XmlNodeType", "XmlConvert", "XmlReaderSettings", "XmlWriterSettings", "ReadState");
    }

    public IReadOnlyList<ConversionResult> Generate(string targetPackage)
    {
        return CompatibilityClassGenerator.GenerateXmlWrappers(targetPackage);
    }
}

/// <summary>
/// JSON Pack — JsonSerializer, JsonSerializerOptions (Jackson 桥接)
/// </summary>
public class JsonPack : ICompatibilityPack
{
    public string Id => "json";
    public string Description => "System.Text.Json → Jackson bridge";
    public IReadOnlyList<string> MavenDependencies => ["com.fasterxml.jackson.core:jackson-databind:2.17.0"];

    public bool IsApplicable(CompatibilityPackContext context)
    {
        return context.ReferencesAnyType("JsonSerializer", "JsonSerializerOptions");
    }

    public IReadOnlyList<ConversionResult> Generate(string targetPackage)
    {
        return CompatibilityClassGenerator.GenerateJsonWrappers(targetPackage);
    }
}

/// <summary>
/// Trace Pack — Trace, DefaultTraceListener
/// </summary>
public class TracePack : ICompatibilityPack
{
    public string Id => "trace";
    public string Description => "System.Diagnostics.Trace compatibility";
    public IReadOnlyList<string> MavenDependencies => [];

    public bool IsApplicable(CompatibilityPackContext context)
    {
        return context.ReferencesAnyType("Trace.", "DefaultTraceListener");
    }

    public IReadOnlyList<ConversionResult> Generate(string targetPackage)
    {
        return CompatibilityClassGenerator.GenerateTraceCompatibilityClasses(targetPackage);
    }
}

/// <summary>
/// I/O Pack — FileHelper, FileMode, TextReader
/// 注意：这些类目前包含在 DotNetCorePack 的 GenerateUtilityClasses 中。
/// 当 DotNetCorePack 被拆分时，此 Pack 将独立持有这些类。
/// 目前此 Pack 标记为不适用，由 DotNetCorePack 统一生成。
/// </summary>
public class IoPack : ICompatibilityPack
{
    public string Id => "io";
    public string Description => "File I/O compatibility (FileHelper, FileMode, TextReader)";
    public IReadOnlyList<string> MavenDependencies => [];

    public bool IsApplicable(CompatibilityPackContext context)
    {
        // 当前 I/O 类由 DotNetCorePack 统一生成，此 Pack 作为未来拆分的预留
        return false;
    }

    public IReadOnlyList<ConversionResult> Generate(string targetPackage)
    {
        return [];
    }
}

/// <summary>
/// MSTest Pack — TestContext, Assert, CollectionAssert
/// </summary>
public class TestPack : ICompatibilityPack
{
    public string Id => "test";
    public string Description => "MSTest compatibility (TestContext, Assert, CollectionAssert)";
    public IReadOnlyList<string> MavenDependencies => [];

    public bool IsApplicable(CompatibilityPackContext context)
    {
        return context.ReferencesAnyType(
            "Microsoft.VisualStudio.TestTools.UnitTesting", "TestContext");
    }

    public IReadOnlyList<ConversionResult> Generate(string targetPackage)
    {
        return CompatibilityClassGenerator.GenerateMSTestCompatibilityClasses(true);
    }
}
