using System.Reflection;
using CSharpToJava.Core.MemberDesugar;
using CSharpToJava.Core.Pipeline;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace CSharpToJava.Tests;

/// <summary>
/// member-desugar 反射可见性回归：XmlSerializer 等 .NET 反射框架只枚举 public 的
/// FieldInfo/PropertyInfo 成员（真实 SDK：xml-tiny StructModel.GetMemberInfos 用
/// BindingFlags.DeclaredOnly|Public|Instance|Static，XmlReflectionImporter 只接受
/// FieldInfo/PropertyInfo）。自动属性降形为「后备字段 + get_/set_ 方法」后，
/// DTO 的可序列化成员面必须仍然可见：后备字段必须 public 且带着按 PD-10 路由
/// 过来的属性级特性。private 后备字段会让反射枚举结果为 0，序列化输出空根元素
///（真实链现场：xml-tiny SimpleSerializerTests 4 用例全失败）。
/// </summary>
public class MemberDesugarReflectionVisibilityTests
{
    private static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.Preview);

    private const string DtoSource = """
using System;

namespace dto
{
    // XmlAttribute 风格特性（AttributeUsage 同时含 Field|Property，
    // PD-10 属性级默认路由 → 后备字段），不依赖 System.Xml 真实类型。
    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Property)]
    public sealed class XmlAttributeAttribute : Attribute
    {
        public XmlAttributeAttribute(string attributeName) { AttributeName = attributeName; }

        public string AttributeName { get; set; }
    }

    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Property)]
    public sealed class XmlIgnoreAttribute : Attribute { }

    public class SimpleDto
    {
        [XmlAttribute("id")]
        public int Id { get; set; }

        [XmlIgnore]
        public string Name { get; set; }

        public int[] Items { get; set; }

        public string Note { get; init; }

        public required string Kind { get; set; }

        public int Count { get; set; }

        public static int CacheSize { get; set; }
    }
}
""";

    private static string Rewrite(string src, string desugarMarker = "get_Id")
    {
        var tree = CSharpSyntaxTree.ParseText(src, ParseOptions, "Dto.cs");
        var compilation = CSharpCompilation.Create(
            "member-desugar-reflect",
            new[] { tree },
            ProjectCompilationBuilder.GetMetadataReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        var preprocessor = new MemberDesugarPreprocessor();
        var stats = new MemberDesugarStatistics();
        var diags = new List<MemberDesugarDiagnostic>();
        var result = preprocessor.RewriteFile(src, compilation.GetSemanticModel(tree), stats, diags);
        Assert.True(diags.All(d => d.Severity != MemberDesugarSeverity.Error),
            "unexpected error diagnostics: " + string.Join(";", diags));
        Assert.Contains(desugarMarker, result.OutputCode, StringComparison.Ordinal); // 去糖确实运行
        return result.OutputCode;
    }

    [Fact]
    public void AutoPropertyDto_PublicReflectionSurfaceNonEmptyAfterDesugar()
    {
        var output = Rewrite(DtoSource);
        AssertCompiles(output);
        var dto = CompileToAssembly(output).GetType("dto.SimpleDto")!;

        // 与 xml-tiny StructModel.GetMemberInfos 同款枚举（只收 public Field/Property）。
        var serializableMembers = dto.GetMembers(
                BindingFlags.DeclaredOnly | BindingFlags.Public |
                BindingFlags.Instance | BindingFlags.Static)
            .Where(m => m is FieldInfo or PropertyInfo)
            .ToList();

        // 修复前：后备字段 private、方法不算 Field/Property → 枚举结果为 0（空根元素）。
        Assert.NotEmpty(serializableMembers);

        var fields = serializableMembers.OfType<FieldInfo>().ToDictionary(f => f.Name);
        foreach (var name in new[] { "id", "name", "items", "note", "kind", "count" })
        {
            Assert.True(fields.ContainsKey(name),
                $"public backing field '{name}' missing from public reflection surface");
            Assert.True(fields[name].IsPublic, $"backing field '{name}' must be public");
            Assert.False(fields[name].IsStatic, $"instance backing field '{name}' must not be static");
        }

        // init 放宽为普通 set 后字段必须可写（XmlSerializer 反序列化要写它）。
        Assert.False(fields["note"].IsInitOnly, "init-widened backing field 'note' must stay mutable");

        // static 自动属性 → public static 字段。
        var staticField = dto.GetField("cacheSize",
            BindingFlags.DeclaredOnly | BindingFlags.Public | BindingFlags.Static)!;
        Assert.NotNull(staticField);
        Assert.True(staticField.IsPublic && staticField.IsStatic);

        // 公共字段 + get_/set_ 方法协作自洽（round-trip 语义）。
        var instance = Activator.CreateInstance(dto)!;
        var idField = dto.GetField("id", BindingFlags.Public | BindingFlags.Instance)!;
        idField.SetValue(instance, 7);
        Assert.Equal(7, dto.GetMethod("get_Id", BindingFlags.Public | BindingFlags.Instance)!
            .Invoke(instance, null));
        dto.GetMethod("set_Id", BindingFlags.Public | BindingFlags.Instance)!
            .Invoke(instance, new object[] { 9 });
        Assert.Equal(9, idField.GetValue(instance));
    }

    [Fact]
    public void PropertyLevelAttributes_LandOnPublicBackingFields()
    {
        var output = Rewrite(DtoSource);
        AssertCompiles(output);
        var dto = CompileToAssembly(output).GetType("dto.SimpleDto")!;

        // [XmlAttribute]（属性级默认路由）→ public 后备字段 id 上可反射读取。
        var idField = dto.GetField("id", BindingFlags.Public | BindingFlags.Instance)!;
        var attrData = idField.GetCustomAttributesData()
            .Single(a => a.AttributeType.Name == "XmlAttributeAttribute");
        Assert.Equal("id", attrData.ConstructorArguments[0].Value);

        // [XmlIgnore] 同样落在 public 后备字段 name 上。
        var nameField = dto.GetField("name", BindingFlags.Public | BindingFlags.Instance)!;
        Assert.Contains(nameField.GetCustomAttributesData(),
            a => a.AttributeType.Name == "XmlIgnoreAttribute");

        // 源码层：特性紧贴 public 字段声明（驼峰后备字段）。
        var compact = Compact(output);
        Assert.Contains("[XmlAttribute(\"id\")]publicintid;", compact, StringComparison.Ordinal);
        Assert.Contains("[XmlIgnore]publicstringname;", compact, StringComparison.Ordinal);
    }

    [Fact]
    public void GetOnlyAutoProperty_ReadOnlyPublicFieldStaysReflectable()
    {
        var src = """
using System;

namespace dto
{
    public class GetOnlyDto
    {
        public int Tag { get; } = 42;
    }
}
""";
        var output = Rewrite(src, desugarMarker: "get_Tag");
        AssertCompiles(output);
        var dtoType = CompileToAssembly(output).GetType("dto.GetOnlyDto")!;

        var members = dtoType.GetMembers(
                BindingFlags.DeclaredOnly | BindingFlags.Public |
                BindingFlags.Instance | BindingFlags.Static)
            .Where(m => m is FieldInfo or PropertyInfo)
            .ToList();
        Assert.NotEmpty(members);

        var field = Assert.IsType<FieldInfo>(
            Assert.Single(members, m => m.Name == "tag"), exactMatch: false);
        Assert.True(field.IsPublic);
        Assert.True(field.IsInitOnly, "get-only auto property must map to a readonly public field");

        var instance = Activator.CreateInstance(dtoType)!;
        Assert.Equal(42, field.GetValue(instance));
    }

    private static string Compact(string code)
        => new string(code.Where(ch => !char.IsWhiteSpace(ch)).ToArray());

    private static void AssertCompiles(string output)
    {
        var tree = CSharpSyntaxTree.ParseText(output, ParseOptions, "Dto.cs");
        var compilation = CSharpCompilation.Create(
            "member-desugar-reflect-verify",
            new[] { tree },
            ProjectCompilationBuilder.GetMetadataReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        var errors = compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .Select(d => d.Descriptor.Id + ": " + d.GetMessage())
            .ToList();
        Assert.True(errors.Count == 0,
            "rewritten compilation has errors:\n" + string.Join("\n", errors));
    }

    private static Assembly CompileToAssembly(string output)
    {
        var tree = CSharpSyntaxTree.ParseText(output, new CSharpParseOptions(LanguageVersion.Preview));
        var references = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES"))!
            .Split(Path.PathSeparator)
            .Select(path => MetadataReference.CreateFromFile(path));
        var compilation = CSharpCompilation.Create(
            "md-reflect-emit-" + Guid.NewGuid().ToString("N"),
            new[] { tree },
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        using var ms = new MemoryStream();
        var emit = compilation.Emit(ms);
        Assert.True(emit.Success,
            "rewritten code failed to emit:\n" +
            string.Join("\n", emit.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
        return Assembly.Load(ms.ToArray());
    }
}
