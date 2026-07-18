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
            "TextReader", "ThreadHelper", "ArrayHelper", "MapHelper",
            "IPAddressHelper",
            "GCHandle.");
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

/// <summary>
/// Xunit Pack — Xunit.Assert
/// </summary>
public class XunitPack : ICompatibilityPack
{
    public string Id => "xunit";
    public string Description => "Xunit compatibility (Assert)";
    public IReadOnlyList<string> MavenDependencies => [];

    public bool IsApplicable(CompatibilityPackContext context)
    {
        return context.ReferencesAnyType("Xunit.Assert", "Xunit.FactAttribute");
    }
}

/// <summary>
/// System.Xml Pack — System.Xml → dotnet.xml bridge.
/// 提供 dotnet.xml 包（XmlReader/XmlWriter/XmlDocument 等）的 Maven 依赖。
/// </summary>
public class SystemXmlPack : ICompatibilityPack
{
    public string Id => "xml";
    public string Description => "System.Xml → dotnet.xml bridge (io.github.ningpp:system-private-xml)";
    public IReadOnlyList<string> MavenDependencies => ["io.github.ningpp:system-private-xml:0.0.1-SNAPSHOT"];

    public bool IsApplicable(CompatibilityPackContext context)
    {
        return context.ReferencesType("dotnet.xml");
    }
}

/// <summary>
/// System.Uri Pack — System.Uri → dotnet.uri bridge.
/// 提供 dotnet.uri 包（Uri 等）的 Maven 依赖。
/// </summary>
public class SystemUriPack : ICompatibilityPack
{
    public string Id => "uri";
    public string Description => "System.Uri → dotnet.uri bridge (io.github.ningpp:system-private-uri)";
    public IReadOnlyList<string> MavenDependencies => ["io.github.ningpp:system-private-uri:0.0.1-SNAPSHOT"];

    public bool IsApplicable(CompatibilityPackContext context)
    {
        // The TypeMapping for System.Uri emits imports under the dotnet.system package
        // (e.g., dotnet.system.Uri), so detect that package rather than the legacy dotnet.uri.
        return context.ReferencesType("dotnet.system.Uri");
    }
}
