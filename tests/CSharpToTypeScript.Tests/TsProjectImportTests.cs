using CSharpToJava.Core;
using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToTypeScript.Tests;

/// <summary>
/// Task 40 跨文件/跨包导入发射测试（spec §3.3）：项目模式每类型一文件——同命名空间
/// 跨文件引用 → "./Type.js"；跨命名空间 → 相对目录（"./Models/Type.js" / "../Type.js"）；
/// 跨项目引用 → workspace 包名导入（源目录前缀接线）；自引用不导入；多类型同文件
/// 按归属文件导入。
/// </summary>
public class TsProjectImportTests
{
    private static ConversionOptions CreateOptions(TargetLanguage target = TargetLanguage.TypeScript)
    {
        var basePath = AppContext.BaseDirectory;
        return new ConversionOptions
        {
            TargetLanguage = target,
            TypeMappingConfigPath = Path.Combine(basePath, "config", "TypeMappings.json"),
            TypeScriptMappingConfigPath = Path.Combine(basePath, "config", "typescript", "TypeMappings.json"),
            TypeScriptMemberMappingPath = Path.Combine(basePath, "config", "typescript", "MemberMappings.json"),
        };
    }

    private static async Task<List<ConversionResult>> ConvertProjectAsync(
        ConversionOptions options,
        ISet<string>? emitFilePaths = null,
        params (string FilePath, string Content)[] files)
    {
        var pipeline = new ProjectConversionPipeline(options);
        return await pipeline.ConvertProjectAsync(
            files.Select(file => new SourceFile { FilePath = file.FilePath, Content = file.Content }),
            emitFilePaths);
    }

    [Fact]
    public async Task Same_Namespace_Cross_File_Reference_Emits_Relative_Import()
    {
        var results = await ConvertProjectAsync(
            CreateOptions(),
            null,
            ("Calc.cs", """
namespace App
{
    public class Calc
    {
        public int Add(int a, int b) => a + b;
    }
}
"""),
            ("Use.cs", """
namespace App
{
    public class Use
    {
        public int Go() => new Calc().Add(1, 2);
    }
}
"""));

        var use = Assert.Single(results, result => result.FileName == "Use.ts");
        Assert.True(use.Success, string.Join("; ", use.Diagnostics.Select(d => d.Message)));
        Assert.Contains("import { Calc } from \"./Calc.js\";", use.GeneratedCode, StringComparison.Ordinal);

        // 源文件自身不出现导入（自引用/无跨文件引用）
        var calc = Assert.Single(results, result => result.FileName == "Calc.ts");
        Assert.DoesNotContain("import", calc.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Sub_Namespace_Reference_Emits_Namespace_Directory_Import()
    {
        var results = await ConvertProjectAsync(
            CreateOptions(),
            null,
            ("Point.cs", "namespace App.Models { public class Point { public int X { get; set; } } }"),
            ("Use.cs", """
using App.Models;
namespace App
{
    public class Use
    {
        public int Go() => new Point().X;
    }
}
"""));

        var use = Assert.Single(results, result => result.FileName == "Use.ts");
        Assert.True(use.Success, string.Join("; ", use.Diagnostics.Select(d => d.Message)));
        Assert.Contains("import { Point } from \"./Models/Point.js\";", use.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Parent_Namespace_Reference_Emits_Parent_Directory_Import()
    {
        var results = await ConvertProjectAsync(
            CreateOptions(),
            null,
            ("Use.cs", "namespace App { public class Use { public int Id { get; set; } } }"),
            ("Point.cs", """
namespace App.Models
{
    public class Point
    {
        public int Go() => new App.Use().Id;
    }
}
"""));

        var point = Assert.Single(results, result => result.FileName == "Point.ts");
        Assert.True(point.Success, string.Join("; ", point.Diagnostics.Select(d => d.Message)));
        Assert.Contains("import { Use } from \"../Use.js\";", point.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cross_Project_Reference_Emits_Workspace_Package_Import()
    {
        var options = CreateOptions();
        options.TsModuleBySourcePathPrefix = new Dictionary<string, string>
        {
            [Path.GetFullPath(@"C:\proj\sample-core")] = "sample-core",
        };

        var results = await ConvertProjectAsync(
            options,
            emitFilePaths: new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                Path.GetFullPath(@"C:\proj\sample-app\Use.cs"),
            },
            (@"C:\proj\sample-core\Calc.cs",
                "namespace App { public class Calc { public int Add(int a, int b) => a + b; } }"),
            (@"C:\proj\sample-app\Use.cs",
                """
namespace App
{
    public class Use
    {
        public int Go() => new Calc().Add(1, 2);
    }
}
"""));

        var use = Assert.Single(results);
        Assert.Equal("Use.ts", use.FileName);
        Assert.True(use.Success, string.Join("; ", use.Diagnostics.Select(d => d.Message)));
        Assert.Contains("import { Calc } from \"sample-core\";", use.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Multi_Type_File_Reference_Imports_By_Ownership_File()
    {
        var results = await ConvertProjectAsync(
            CreateOptions(),
            null,
            ("Shape.cs", """
namespace App
{
    public class Shape
    {
        public virtual int Area() => 0;
    }

    public class Circle : Shape
    {
        public override int Area() => 3;
    }
}
"""),
            ("Use.cs", """
namespace App
{
    public class Use
    {
        public int Go() => new Circle().Area();
    }
}
"""));

        var use = Assert.Single(results, result => result.FileName == "Use.ts");
        Assert.True(use.Success, string.Join("; ", use.Diagnostics.Select(d => d.Message)));
        // 同文件多类型 → 每类型一个 .ts（Circle.ts/Shape.ts），引用按类型归属文件
        Assert.Contains("import { Circle } from \"./Circle.js\";", use.GeneratedCode, StringComparison.Ordinal);
    }
}