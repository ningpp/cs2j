using System.Reflection;
using CSharpToJava.Core.MemberDesugar;
using CSharpToJava.Core.Pipeline;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace CSharpToJava.Tests;

/// <summary>
/// 序列化成员面保真回归（真实 SDK：json 测试工程 member-desugar 阶段 2108 失败，
/// 其中 ContinuationTests 1122 例、构造器参数绑定 86 例，全部因属性降形后
/// 「S 等原成员名消失 + public 后备字段默认不被 STJ 枚举」）。修复两点：
/// 后备字段保留声明原名；public 属性的字段补 [JsonInclude]，使默认
/// IncludeFields=false 的 System.Text.Json 仍枚举它。
/// </summary>
public class MemberDesugarSerializationMetadataTests
{
    private static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.Preview);

    private const string DtoSource = """
using System.Text.Json.Serialization;

namespace dto
{
    public class JsonDto
    {
        public int Id { get; set; }
        public string Name { get; set; }
        public string Lazy { get; private set; }
        private string Secret { get; set; }
        public static int Cache { get; set; }
        [JsonInclude] public int Marked { get; set; }
        [JsonPropertyName("renamed")] public string Original { get; set; }
    }
}
""";

    private static string Rewrite(string src, string filePath = "Dto.cs")
    {
        var tree = CSharpSyntaxTree.ParseText(src, ParseOptions, filePath);
        var compilation = CSharpCompilation.Create(
            "member-desugar-serialization",
            new[] { tree },
            ProjectCompilationBuilder.GetMetadataReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        var preprocessor = new MemberDesugarPreprocessor();
        var stats = new MemberDesugarStatistics();
        var diags = new List<MemberDesugarDiagnostic>();
        var result = preprocessor.RewriteFile(
            src, compilation.GetSemanticModel(tree), stats, diags);
        Assert.True(diags.All(d => d.Severity != MemberDesugarSeverity.Error),
            "unexpected error diagnostics: " + string.Join(";", diags));
        return result.OutputCode;
    }

    private static string Compact(string code)
        => new(code.Where(ch => !char.IsWhiteSpace(ch)).ToArray());

    private static Assembly CompileToAssembly(string output)
    {
        var tree = CSharpSyntaxTree.ParseText(output, new CSharpParseOptions(LanguageVersion.Preview));
        var references = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES"))!
            .Split(Path.PathSeparator)
            .Select(path => MetadataReference.CreateFromFile(path));
        var compilation = CSharpCompilation.Create(
            "md-serialization-emit-" + Guid.NewGuid().ToString("N"),
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

    [Fact]
    public void PublicAutoProperty_BackingFieldKeepsOriginalNameAndGetsJsonInclude()
    {
        var output = Rewrite(DtoSource);
        var compact = Compact(output);

        // 名字保真：字段沿用声明原名（不是 camelCase 的 id/name）。
        Assert.Contains("publicintId;", compact, StringComparison.Ordinal);
        Assert.Contains("publicstringName;", compact, StringComparison.Ordinal);
        Assert.Contains("publicstringOriginal;", compact, StringComparison.Ordinal);

        // 默认 IncludeFields=false 的 STJ 要能看到字段：public 属性补 [JsonInclude]，
        // 已由 PD-10 路由过 [JsonInclude] 的属性不重复发射（CS0579）。
        Assert.True(compact.Contains("JsonIncludeAttribute"), compact);
        Assert.Contains(
            "[global::System.Text.Json.Serialization.JsonIncludeAttribute()]publicintId;",
            compact, StringComparison.Ordinal);

        // 已由 PD-10 路由过 [JsonInclude] 的属性不重复发射（CS0579）。
        Assert.Contains("[JsonInclude]publicintMarked;", compact, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "[global::System.Text.Json.Serialization.JsonIncludeAttribute()][JsonInclude]",
            compact, StringComparison.Ordinal);

        // 非 public 访问器的属性（public getter + private setter）保留为原属性：
        // 字段形态无法表达「只序列化不反序列化」，整属性保留是该语义的最小保真。
        Assert.Contains("publicstringLazy{get;privateset;}", compact, StringComparison.Ordinal);
        Assert.DoesNotContain("get_Lazy", output, StringComparison.Ordinal);

        // 非 public / static 属性原本就不参与 STJ 序列化，不补标记。
        Assert.DoesNotContain(
            "JsonIncludeAttribute()]publicstaticintCache;", compact, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "JsonIncludeAttribute()]publicstringSecret;", compact, StringComparison.Ordinal);
    }

    [Fact]
    public void DesugaredDto_RoundTripsThroughSystemTextJsonWithOriginalNames()
    {
        var output = Rewrite(DtoSource);
        var assembly = CompileToAssembly(output);
        var dto = assembly.GetType("dto.JsonDto")!;

        var instance = Activator.CreateInstance(dto)!;
        dto.GetField("Id")!.SetValue(instance, 7);
        dto.GetField("Original")!.SetValue(instance, "x");

        var json = Serialize(dto, instance, options: null);
        Assert.Contains("\"Id\":7", json, StringComparison.Ordinal);
        Assert.Contains("\"renamed\":\"x\"", json, StringComparison.Ordinal);

        // 反序列化按原成员名/显式 JsonPropertyName 回写字段（字段被 [JsonInclude] 纳入）。
        var parsed = Deserialize(dto, "{\"Id\":9,\"renamed\":\"y\"}", options: null);
        Assert.Equal(9, dto.GetField("Id")!.GetValue(parsed));
        Assert.Equal("y", dto.GetField("Original")!.GetValue(parsed));

        // 运行时 PropertyNamingPolicy 仍作用于原成员名（[JsonInclude] 不会压过策略，
        // 故不发射 [JsonPropertyName]——否则 camelCase 策略下会输出 "Id"/"Original"）。
        var camelCase = new System.Text.Json.JsonSerializerOptions
        {
            PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
        };
        var camelJson = Serialize(dto, instance, camelCase);
        Assert.Contains("\"id\":7", camelJson, StringComparison.Ordinal);
        Assert.DoesNotContain("\"Id\":7", camelJson, StringComparison.Ordinal);
    }

    [Fact]
    public void WithoutJsonNamespaceInScope_NoAttributeIsInjected()
    {
        const string src = """
namespace dto
{
    public class PlainDto
    {
        public int Id { get; set; }
    }
}
""";
        var output = Rewrite(src, "PlainDto.cs");
        Assert.DoesNotContain("JsonInclude", output, StringComparison.Ordinal);
        // 名字保真与特性发射无关，始终生效。
        Assert.Contains("publicintId;", Compact(output), StringComparison.Ordinal);
    }

    [Fact]
    public void AnonymousTypeLiftDto_StaysSerializableThroughSystemTextJson()
    {
        // C21 生成的只读属性是序列化可见契约；生成文件位于 solution root，无法按工程
        // 作用域补 [JsonInclude]，因此保留属性形态（真实链 text.json 11 例）。
        const string src = """
namespace Cs2jAnonymous
{
    internal sealed class Anon_Name_String
    {
        public string Name { get; }

        public Anon_Name_String(string name)
        {
            Name = name;
        }
    }
}
""";
        var output = Rewrite(src, "_Cs2jAnonymousTypes.cs");
        Assert.Contains("public string Name { get; }", output, StringComparison.Ordinal);
        Assert.DoesNotContain("get_Name", output, StringComparison.Ordinal);

        var assembly = CompileToAssembly(output);
        var dto = assembly.GetType("Cs2jAnonymous.Anon_Name_String")!;
        var instance = Activator.CreateInstance(dto, "x")!;
        Assert.Equal("{\"Name\":\"x\"}", Serialize(dto, instance, options: null));

        var parsed = Deserialize(dto, "{\"Name\":\"y\"}", options: null);
        Assert.Equal("y", dto.GetProperty("Name")!.GetValue(parsed));
    }

    private static string Serialize(Type type, object value, System.Text.Json.JsonSerializerOptions? options)
        => (string)typeof(System.Text.Json.JsonSerializer)
            .GetMethod("Serialize", new[]
            {
                typeof(object), typeof(Type), typeof(System.Text.Json.JsonSerializerOptions),
            })!
            .Invoke(null, new object?[] { value, type, options })!;

    private static object Deserialize(Type type, string json, System.Text.Json.JsonSerializerOptions? options)
        => typeof(System.Text.Json.JsonSerializer)
            .GetMethod("Deserialize", new[]
            {
                typeof(string), typeof(Type), typeof(System.Text.Json.JsonSerializerOptions),
            })!
            .Invoke(null, new object?[] { json, type, options })!;
}
