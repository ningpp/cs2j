using System.IO;
using System.Reflection;
using CSharpToJava.Core.EnumDeclaration;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace CSharpToJava.Tests.EnumDeclaration;

/// <summary>
/// enum-declaration 值类型零默认语义（native enum zero member）回归：
/// class 容器播种、struct 容器 default(S)/new S() 读取零值、以及仅含类型引用
/// （无其它使用点）的声明文件必须进入 TouchedFiles 并被改写。
/// </summary>
public class EnumDeclarationZeroDefaultUnitTests
{
    private static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.Preview);

    private static (string Code, bool Changed, EnumDeclarationStatistics Stats,
        List<EnumDeclarationDiagnostic> Diags) Rewrite(string src)
    {
        var tree = CSharpSyntaxTree.ParseText(src, ParseOptions, path: "Test.cs");
        var compilation = Compile(new[] { tree });
        var pp = new EnumDeclarationPreprocessor();
        var stats = new EnumDeclarationStatistics();
        var diags = new List<EnumDeclarationDiagnostic>();
        var result = pp.RewriteFile(src, compilation.GetSemanticModel(tree), stats, diags);
        return (result.OutputCode, result.Changed, stats, diags);
    }

    private static CSharpCompilation Compile(IEnumerable<SyntaxTree> trees, string name = "ed-zero-test")
        => CSharpCompilation.Create(
            name,
            trees,
            GetReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));

    private static IEnumerable<MetadataReference> GetReferences()
    {
        var trusted = (string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES");
        if (trusted != null)
        {
            foreach (var assembly in trusted.Split(Path.PathSeparator))
            {
                if (File.Exists(assembly)) yield return MetadataReference.CreateFromFile(assembly);
            }
            yield break;
        }
        yield return MetadataReference.CreateFromFile(typeof(object).Assembly.Location);
    }

    private static void AssertOutputCompiles(string output)
    {
        var tree = CSharpSyntaxTree.ParseText(output, ParseOptions, path: "Rewritten.cs");
        var errors = Compile(new[] { tree }, "ed-zero-verify").GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        Assert.True(errors.Count == 0,
            "Rewritten code failed to compile:\n" + string.Join("\n", errors) +
            "\n--- output ---\n" + output);
    }

    private static Assembly EmitAssembly(string code, string assemblyName)
    {
        var tree = CSharpSyntaxTree.ParseText(code, ParseOptions);
        using var ms = new MemoryStream();
        var emit = Compile(new[] { tree }, assemblyName).Emit(ms);
        Assert.True(emit.Success,
            "failed to emit generated assembly:\n" +
            string.Join("\n", emit.Diagnostics
                .Where(d => d.Severity == DiagnosticSeverity.Error)) +
            "\n--- output ---\n" + code);
        return Assembly.Load(ms.ToArray());
    }

    private static long GetUnderlyingValue(object enumInstance)
    {
        var method = enumInstance.GetType().GetMethod("GetValue")!;
        return Convert.ToInt64(method.Invoke(enumInstance, null));
    }

    [Fact]
    public void Class_Uninitialized_Field_And_AutoProperty_Seed_Zero_Default()
    {
        const string src = """
            using System;
            [Flags]
            enum E : byte { None = 0, A = 1, B = 2 }
            class C
            {
                private E _f;
                public E Auto { get; set; }
            }
            """;

        var (code, changed, _, diags) = Rewrite(src);

        Assert.True(changed, string.Join("\n", diags.Select(d => $"{d.Code}: {d.Message}")));
        Assert.Contains("_f = global::E.DEFAULT;", code);
        Assert.Contains("public E Auto { get; set; } = global::E.DEFAULT;", code);
        AssertOutputCompiles(code);

        var asm = EmitAssembly(code, "ed-zero-class-" + Guid.NewGuid().ToString("N"));
        var cType = asm.GetType("C")!;
        var instance = Activator.CreateInstance(cType)!;
        var field = cType.GetField("_f", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(instance);
        var auto = cType.GetProperty("Auto")!.GetValue(instance);
        Assert.NotNull(field);
        Assert.NotNull(auto);
        Assert.Equal(0, GetUnderlyingValue(field!));
        Assert.Equal(0, GetUnderlyingValue(auto!));
    }

    [Fact]
    public void Struct_Uninitialized_AutoProperty_And_Field_Read_Zero_For_DefaultAndNew()
    {
        // A native enum member inside a struct starts at the zero member for both
        // default(S) and new S(); the class form leaves the storage null. Reads
        // must therefore coalesce to E.DEFAULT in the struct accessor itself,
        // because default(S) never runs constructors/field initializers.
        const string src = """
            using System;
            [Flags]
            enum E : byte { None = 0, A = 1, B = 2 }
            struct S
            {
                public E Prop { get; set; }
                public E Field;
            }
            class C
            {
                public static E FromDefaultProp() => default(S).Prop;
                public static E FromNewProp() => new S().Prop;
                public static E FromDefaultField() => default(S).Field;
                public static E FromNewField() => new S().Field;
            }
            """;

        var (code, changed, _, diags) = Rewrite(src);

        Assert.True(changed, string.Join("\n", diags.Select(d => $"{d.Code}: {d.Message}")));
        Assert.Contains("?? global::E.DEFAULT", code);
        AssertOutputCompiles(code);

        var asm = EmitAssembly(code, "ed-zero-struct-" + Guid.NewGuid().ToString("N"));
        var cType = asm.GetType("C")!;
        foreach (var methodName in new[]
                 {
                     "FromDefaultProp", "FromNewProp", "FromDefaultField", "FromNewField",
                 })
        {
            var value = cType.GetMethod(methodName)!.Invoke(null, null);
            Assert.NotNull(value);
            Assert.Equal(0, GetUnderlyingValue(value!));
        }
    }

    [Fact]
    public void TypeReferenceOnly_File_IsTouched_And_Class_AutoProperty_Seeded()
    {
        // Label.cs pattern: the file only mentions the converted enum as a
        // property type. No member access / cast / default(Type) site exists, so
        // the analyzer must record TouchedFiles from the type reference itself;
        // otherwise the file is skipped and the property stays null.
        const string enumSource = """
            using System;
            [Flags]
            enum E : byte { None = 0, A = 1, B = 2 }
            """;
        const string holderSource = """
            class Holder
            {
                public E Prop { get; set; }
            }
            """;

        var enumTree = CSharpSyntaxTree.ParseText(enumSource, ParseOptions, path: "Enum.cs");
        var holderTree = CSharpSyntaxTree.ParseText(holderSource, ParseOptions, path: "Holder.cs");
        var compilation = Compile(new[] { enumTree, holderTree });

        var pp = new EnumDeclarationPreprocessor();
        pp.BindCompilation(compilation);
        var stats = new EnumDeclarationStatistics();
        var diags = new List<EnumDeclarationDiagnostic>();
        var result = pp.RewriteFile(
            holderSource, compilation.GetSemanticModel(holderTree), stats, diags);

        Assert.True(result.Changed,
            string.Join("\n", diags.Select(d => $"{d.Code}: {d.Message}")));
        Assert.Contains("public E Prop { get; set; } = global::E.DEFAULT;", result.OutputCode);

        var enumResult = pp.RewriteFile(
            enumSource, compilation.GetSemanticModel(enumTree),
            new EnumDeclarationStatistics(), new List<EnumDeclarationDiagnostic>());
        Assert.True(enumResult.Changed);

        var rewrittenEnumTree = CSharpSyntaxTree.ParseText(
            enumResult.OutputCode, ParseOptions, path: "Enum.cs");
        var rewrittenHolderTree = CSharpSyntaxTree.ParseText(
            result.OutputCode, ParseOptions, path: "Holder.cs");
        var errors = Compile(new[] { rewrittenEnumTree, rewrittenHolderTree })
            .GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        Assert.True(errors.Count == 0,
            "Rewritten multi-file code failed to compile:\n" + string.Join("\n", errors) +
            "\n--- holder ---\n" + result.OutputCode);
    }
}
