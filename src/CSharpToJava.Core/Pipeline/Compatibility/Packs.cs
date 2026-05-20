namespace CSharpToJava.Core.Pipeline.Compatibility;

/// <summary>
/// Ref/Out 参数 Holder 类 Pack — IntHolder, DoubleHolder, ObjectHolder&lt;T&gt; 等
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
            "InvalidDataException", "MemoryStream", "StreamReader", "StreamWriter",
            "TextReader", "ThreadHelper", "ArrayHelper");
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
}

/// <summary>
/// I/O Pack — FileHelper, FileMode, TextReader
/// </summary>
public class IoPack : ICompatibilityPack
{
    public string Id => "io";
    public string Description => "File I/O compatibility (FileHelper, FileMode, TextReader, StreamReader/Writer, MemoryStream)";
    public IReadOnlyList<string> MavenDependencies => [];

    public bool IsApplicable(CompatibilityPackContext context)
    {
        return false;
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
}
