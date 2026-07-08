# default(T) 转换单元测试 实现计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 为 C#→Java 转换器中的 `default(T)` / 裸 `default` 在单代码片段、csproj、sln 三个层级编写完整单元测试，并修正转换器在可空值类型等边缘场景下的错误，使所有测试通过。

**Architecture:** 单片段测试继承既有 `ConversionTestBase`（xUnit + `ConversionPipeline.Convert`）；项目级测试用 `ConversionPipeline.ConvertProjectWithPartialMergeAsync`；解决方案级测试用 `SolutionLoader.OpenSolutionAsync` + `ProjectConversionPipeline.ConvertProjectAsync`。新增一套样例解决方案夹具 `tests/SampleDefaultSolution/`（含 Lib 与 App 两个项目，刻意使用各类 `default`）。发现的转换器缺陷在 `TypeOperationTransformer.GetDefaultValueForType` 修复。

**Tech Stack:** C# (.NET 10)，xUnit 2.9.3，Roslyn 4.12，MSBuildWorkspace。

---

## 文件结构

**新建夹具（测试数据，须可被 MSBuild 编译以取得语义）：**
- `tests/SampleDefaultSolution/SampleDefaultSolution.sln`
- `tests/SampleDefaultSolution/SampleDefaultLib/SampleDefaultLib.csproj`
- `tests/SampleDefaultSolution/SampleDefaultLib/Defaults.cs`
- `tests/SampleDefaultSolution/SampleDefaultApp/SampleDefaultApp.csproj`
- `tests/SampleDefaultSolution/SampleDefaultApp/Program.cs`

**新建测试：**
- `tests/CSharpToJava.ConversionTests/Categories/DefaultValueTests.cs`（单片段，主力，参数化）
- `tests/CSharpToJava.ProjectTests/DefaultValueProjectTests.cs`（csproj 级）
- `tests/CSharpToJava.ProjectTests/DefaultValueSolutionTests.cs`（sln 级）

**修改：**
- `tests/CSharpToJava.ProjectTests/CSharpToJava.ProjectTests.csproj`（新增 Content 包含 `SampleDefaultSolution`）
- `src/CSharpToJava.Core/Transformers/Expression/Transformers/TypeOperationTransformer.cs`（`GetDefaultValueForType` 增加可空值类型处理）

---

## Task 1: 搭建样例解决方案夹具

**Files:**
- Create: `tests/SampleDefaultSolution/SampleDefaultSolution.sln`
- Create: `tests/SampleDefaultSolution/SampleDefaultLib/SampleDefaultLib.csproj`
- Create: `tests/SampleDefaultSolution/SampleDefaultLib/Defaults.cs`
- Create: `tests/SampleDefaultSolution/SampleDefaultApp/SampleDefaultApp.csproj`
- Create: `tests/SampleDefaultSolution/SampleDefaultApp/Program.cs`
- Modify: `tests/CSharpToJava.ProjectTests/CSharpToJava.ProjectTests.csproj`

- [ ] **Step 1: 创建 Lib 项目文件**

`tests/SampleDefaultSolution/SampleDefaultLib/SampleDefaultLib.csproj`:
```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <IsPackable>false</IsPackable>
  </PropertyGroup>

</Project>
```

- [ ] **Step 2: 创建 Lib 源文件（覆盖 struct / enum / 泛型 / tuple / 各语法位置）**

`tests/SampleDefaultSolution/SampleDefaultLib/Defaults.cs`:
```csharp
namespace SampleDefaultLib;

public struct Point
{
    public int X;
    public int Y;
}

public enum Status
{
    Full = 0,
    Empty = 1,
}

public enum Color
{
    Red = 1,
    Green = 2,
    Blue = 3,
}

[Flags]
public enum Permissions
{
    None = 0,
    Read = 1,
    Write = 2,
}

public class StructContainer<T> where T : struct
{
    public T Value = default;
}

public class ClassContainer<T> where T : class
{
    public T? Value = default;
}

public class FreeContainer<T>
{
    public T Value = default;

    public T Get() => default;
}

public class Defaults
{
    // 字段初始化位置
    private Point _p = default;
    private Status _s = default;
    private Permissions _f = default;
    private int? _n = default;
    private (int, string) _t = default;

    // 方法返回值位置
    public Point GetPoint() => default;
    public Status GetStatus() => default;
    public Permissions GetFlags() => default;
    public (int, string) GetTuple() => default;

    // 泛型方法（无约束）返回值位置
    public T GetDefault<T>() => default;

    // 方法参数默认值位置
    public void Process(int x = default) { }

    // 数组元素位置
    public int[] MakeArray() => new[] { default(int) };
}
```

- [ ] **Step 3: 创建 App 项目文件（引用 Lib，覆盖基本类型 / 可空 / 嵌套泛型 / 各位置）**

`tests/SampleDefaultSolution/SampleDefaultApp/SampleDefaultApp.csproj`:
```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <IsPackable>false</IsPackable>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\SampleDefaultLib\SampleDefaultLib.csproj" />
  </ItemGroup>

</Project>
```

`tests/SampleDefaultSolution/SampleDefaultApp/Program.cs`:
```csharp
using System.Collections.Generic;
using SampleDefaultLib;

namespace SampleDefaultApp;

public class Program
{
    // 字段初始化位置：基本类型与可空
    private int _i = default;
    private long _l = default;
    private short _s = default;
    private byte _b = default;
    private float _fl = default;
    private double _d = default;
    private bool _bo = default;
    private char _c = default;
    private decimal _dec = default;
    private string? _str = default;
    private int? _n = default;
    private Dictionary<int, string>? _map = default;
    private int[]? _arr = default;

    public static void Main() { }

    // 方法返回值位置
    public int GetInt() => default;
    public long GetLong() => default;
    public double GetDouble() => default;
    public bool GetBool() => default;
    public char GetChar() => default;
    public decimal GetDecimal() => default;
    public string? GetString() => default;
    public int? GetNullable() => default;
    public Dictionary<int, string>? GetMap() => default;

    // 方法参数默认值位置
    public void Run(int[] arr = default) { }

    // 数组元素位置
    public int[] MakeArray() => new int[] { default };
}
```

- [ ] **Step 4: 创建 .sln 文件**

`tests/SampleDefaultSolution/SampleDefaultSolution.sln`:
```
Microsoft Visual Studio Solution File, Format Version 12.00
# Visual Studio Version 17
VisualStudioVersion = 17.0.31903.59
MinimumVisualStudioVersion = 10.0.40219.1
Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "SampleDefaultLib", "SampleDefaultLib\SampleDefaultLib.csproj", "{11111111-1111-1111-1111-111111111111}"
EndProject
Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "SampleDefaultApp", "SampleDefaultApp\SampleDefaultApp.csproj", "{22222222-2222-2222-2222-222222222222}"
EndProject
Global
	GlobalSection(SolutionConfigurationPlatforms) = preSolution
		Debug|Any CPU = Debug|Any CPU
		Release|Any CPU = Release|Any CPU
	EndGlobalSection
	GlobalSection(ProjectConfigurationPlatforms) = postSolution
		{11111111-1111-1111-1111-111111111111}.Debug|Any CPU.ActiveCfg = Debug|Any CPU
		{11111111-1111-1111-1111-111111111111}.Debug|Any CPU.Build.0 = Debug|Any CPU
		{11111111-1111-1111-1111-111111111111}.Release|Any CPU.ActiveCfg = Release|Any CPU
		{11111111-1111-1111-1111-111111111111}.Release|Any CPU.Build.0 = Release|Any CPU
		{22222222-2222-2222-2222-222222222222}.Debug|Any CPU.ActiveCfg = Debug|Any CPU
		{22222222-2222-2222-2222-222222222222}.Debug|Any CPU.Build.0 = Debug|Any CPU
		{22222222-2222-2222-2222-222222222222}.Release|Any CPU.ActiveCfg = Release|Any CPU
		{22222222-2222-2222-2222-222222222222}.Release|Any CPU.Build.0 = Release|Any CPU
	EndGlobalSection
	GlobalSection(SolutionProperties) = preSolution
		HideSolutionNode = FALSE
	EndGlobalSection
EndGlobal
```

- [ ] **Step 5: 在 ProjectTests.csproj 中登记夹具（复制现有 SampleProject 的方式）**

在 `tests/CSharpToJava.ProjectTests/CSharpToJava.ProjectTests.csproj` 的最后一个 `<ItemGroup>` 之后追加：
```xml
  <ItemGroup>
    <!-- Include SampleDefaultSolution as content so it's available at test runtime -->
    <Content Include="..\SampleDefaultSolution\**\*.*">
      <Link>SampleDefaultSolution\%(RecursiveDir)%(Filename)%(Extension)</Link>
      <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
    </Content>
  </ItemGroup>
```

- [ ] **Step 6: 提交夹具脚手架**

```bash
git add tests/SampleDefaultSolution tests/CSharpToJava.ProjectTests/CSharpToJava.ProjectTests.csproj
git commit -m "test(fixture): add SampleDefaultSolution with default(T) usages"
```

---

## Task 2: 编写单代码片段级测试 DefaultValueTests.cs

**Files:**
- Create: `tests/CSharpToJava.ConversionTests/Categories/DefaultValueTests.cs`

- [ ] **Step 1: 编写测试文件（参数化覆盖所有分类）**

`tests/CSharpToJava.ConversionTests/Categories/DefaultValueTests.cs`：
```csharp
using System.Collections.Generic;
using Xunit;

namespace CSharpToJava.ConversionTests.Categories;

public class DefaultValueTests : ConversionTestBase
{
    public static IEnumerable<object[]> PrimitiveDefaults => new List<object[]>
    {
        // csharpType, javaTypeMarker
        ["int", "0"],
        ["long", "0L"],
        ["short", "(short)0"],
        ["byte", "0"],
        ["float", "0.0f"],
        ["double", "0.0"],
        ["bool", "false"],
        ["char", "'\\0'"],
        ["decimal", "Decimal.ZERO"],
        ["string", "null"],
    };

    [Theory]
    [MemberData(nameof(PrimitiveDefaults))]
    public void LocalVariable_PrimitiveDefault_Literal(string csharpType, string javaMarker)
    {
        var result = Convert($"class C {{ public void M() {{ {csharpType} x = default; }} }}");
        AssertConversion(result, $"{javaMarker}");
    }

    [Theory]
    [MemberData(nameof(PrimitiveDefaults))]
    public void LocalVariable_PrimitiveDefault_Explicit(string csharpType, string javaMarker)
    {
        var result = Convert($"class C {{ public void M() {{ {csharpType} x = default({csharpType}); }} }}");
        AssertConversion(result, $"{javaMarker}");
    }

    [Fact]
    public void StructDefault_EmitsNewT()
    {
        var result = Convert("struct Point { public int X; public int Y; } class C { public void M() { Point p = default; } }");
        AssertConversion(result, "new Point()");
    }

    [Fact]
    public void EnumDefault_WithZeroMember_EmitsZeroMember()
    {
        var result = Convert("enum Status { Full = 0, Empty = 1 } class C { public void M() { Status s = default; } }");
        AssertConversion(result, "Status.Full");
    }

    [Fact]
    public void EnumDefault_Flags_EmitsZero()
    {
        var result = Convert("[System.Flags] enum F { None = 0, A = 1 } class C { public void M() { F f = default; } }");
        AssertConversion(result, "0");
    }

    [Fact]
    public void EnumDefault_NoZeroMember_EmitsValues0()
    {
        // 无 0 值成员的枚举：当前转换器输出 values()[0]，作为稳定行为回归守卫。
        var result = Convert("enum Color { Red = 1, Green = 2 } class C { public void M() { Color c = default; } }");
        AssertConversion(result, "Color.values()[0]");
    }

    [Fact]
    public void GenericStructConstraint_EmitsNewT()
    {
        var result = Convert("class C<T> where T : struct { public T V = default; }");
        AssertConversion(result, "new T()");
    }

    [Fact]
    public void GenericClassConstraint_EmitsNull()
    {
        var result = Convert("class C<T> where T : class { public T V = default; }");
        AssertConversion(result, "null");
    }

    [Fact]
    public void GenericUnconstrained_MethodLevel_EmitsDefaultValueOf()
    {
        var result = Convert("class C { public T M<T>() { T v = default; return v; } }");
        AssertConversion(result, "DefaultValue.of(_cs2j_T)");
        AssertJavaContains(result, "import io.github.ningpp.compat.DefaultValue;");
    }

    [Fact]
    public void TupleDefault_Explicit_EmitsTupleOf()
    {
        var result = Convert("class C { public void M() { var t = default((int, string)); } }");
        AssertConversion(result, "Tuple.of(0, null)");
        AssertJavaContains(result, "import io.vavr.Tuple;");
    }

    [Fact]
    public void TupleDefault_Literal_EmitsTupleOf()
    {
        var result = Convert("class C { public void M() { (int, string) t = default; } }");
        AssertConversion(result, "Tuple.of(0, null)");
    }

    [Fact]
    public void NullableValueTypeDefault_EmitsNull()
    {
        var result = Convert("class C { public void M() { int? n = default; } }");
        AssertConversion(result, "null");
    }

    [Fact]
    public void NullableValueTypeDefault_Explicit_EmitsNull()
    {
        var result = Convert("class C { public void M() { int? n = default(int?); } }");
        AssertConversion(result, "null");
    }

    [Fact]
    public void NestedGenericDefault_EmitsNull()
    {
        var result = Convert("using System.Collections.Generic; class C { public void M() { Dictionary<int, string> d = default; } }");
        AssertConversion(result, "null");
    }

    [Fact]
    public void ArrayDefault_EmitsNull()
    {
        var result = Convert("class C { public void M() { int[] a = default; } }");
        AssertConversion(result, "null");
    }

    [Fact]
    public void DefaultInReturnPosition_EmitsMarker()
    {
        var result = Convert("class C { public int M() { return default; } }");
        AssertConversion(result, "return 0;");
    }

    [Fact]
    public void DefaultAsMethodArgument_EmitsMarker()
    {
        var result = Convert("class C { void Callee(int x) {} void Caller() { Callee(default); } }");
        AssertConversion(result, "Callee(0)");
    }

    [Fact]
    public void DefaultInArrayElement_EmitsMarker()
    {
        var result = Convert("class C { public void M() { int[] a = new int[] { default }; } }");
        AssertConversion(result, "new int[] { 0 }");
    }

    [Fact]
    public void DefaultInFieldInitializer_EmitsMarker()
    {
        var result = Convert("class C { private int _x = default; }");
        AssertConversion(result, "private int _x = 0;");
    }
}
```

- [ ] **Step 2: 运行单片段测试，确认失败项（期望至少 `NullableValueTypeDefault` 因转换器缺陷失败）**

```bash
dotnet test tests/CSharpToJava.ConversionTests/CSharpToJava.ConversionTests.csproj --filter "FullyQualifiedName~DefaultValueTests" --nologo
```

期望：多数通过；`NullableValueTypeDefault*` 与可能的 `EnumDefault_NoZeroMember` 失败（具体以实际输出为准）。记录失败项供 Task 3/4 修复。

- [ ] **Step 3: 提交测试（即便有已知失败，先提交测试骨架）**

```bash
git add tests/CSharpToJava.ConversionTests/Categories/DefaultValueTests.cs
git commit -m "test: add single-snippet default(T) conversion tests"
```

---

## Task 3: 修复可空值类型 default 转换器缺陷

**Files:**
- Modify: `src/CSharpToJava.Core/Transformers/Expression/Transformers/TypeOperationTransformer.cs`（`GetDefaultValueForType`，约 1055-1179 行）

- [ ] **Step 1: 在 `GetDefaultValueForType` 最前面增加可空值类型处理**

在 `GetDefaultValueForType` 方法内、`tupleType` 判断之前（约 1057 行）插入：
```csharp
        // Nullable<T> (e.g. int?) defaults to null in C#, not a zero struct.
        if (typeSymbol is INamedTypeSymbol { IsGenericType: true } nullableType
            && nullableType.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T)
        {
            return "null";
        }
```

- [ ] **Step 2: 重新运行单片段测试**

```bash
dotnet test tests/CSharpToJava.ConversionTests/CSharpToJava.ConversionTests.csproj --filter "FullyQualifiedName~DefaultValueTests" --nologo
```

期望：`NullableValueTypeDefault_EmitsNull` 与 `NullableValueTypeDefault_Explicit_EmitsNull` 通过。

- [ ] **Step 3: 提交修复**

```bash
git add src/CSharpToJava.Core/Transformers/Expression/Transformers/TypeOperationTransformer.cs
git commit -m "fix: default(int?) nullable value type should emit null"
```

---

## Task 4: 修复/核实其余 default 边缘差异

**Files:**
- Modify: `src/CSharpToJava.Core/Transformers/Expression/Transformers/TypeOperationTransformer.cs`（按需）

- [ ] **Step 1: 运行全部单片段 default 测试，逐条核对失败项**

```bash
dotnet test tests/CSharpToJava.ConversionTests/CSharpToJava.ConversionTests.csproj --filter "FullyQualifiedName~DefaultValueTests" --nologo
```

- [ ] **Step 2: 对每条“正确期望 vs 实际输出”不一致的项，定位并修复**

按现象对照 `GetDefaultValueForType`（行 1055 起）对应分支：
- 若某基本类型/struct/enum/泛型分支输出不符预期，校正值或补充分支。
- 枚举无 0 值成员：当前输出 `values()[0]`；若评审认为应为其他形式（如 `null` 或显式 `(Color)0`），在该分支修改并在 Task 2 的 `EnumDefault_NoZeroMember_EmitsValues0` 测试中同步更新期望标记。

每修复一处即重跑相关 Theory，确认该用例通过后再修下一处。

- [ ] **Step 3: 运行既有的 default 相关测试，确保未引入回归**

```bash
dotnet test tests/CSharpToJava.Tests/CSharpToJava.Tests.csproj --filter "FullyQualifiedName~Default" --nologo
dotnet test tests/CSharpToJava.ConversionTests/CSharpToJava.ConversionTests.csproj --filter "FullyQualifiedName~PrimitiveType" --nologo
```

期望：全绿（既有 default/Tuple/Enum/Struct/Decimal 测试不应因修复而失败）。

- [ ] **Step 4: 提交（如有修改）**

```bash
git add src/CSharpToJava.Core/Transformers/Expression/Transformers/TypeOperationTransformer.cs
git commit -m "fix: correct remaining default(T) edge-case conversions"
```

---

## Task 5: 编写 csproj 级测试

**Files:**
- Create: `tests/CSharpToJava.ProjectTests/DefaultValueProjectTests.cs`

- [ ] **Step 1: 编写 csproj 级测试（转换 Lib 项目并抽样验证）**

`tests/CSharpToJava.ProjectTests/DefaultValueProjectTests.cs`：
```csharp
using CSharpToJava.Core.Pipeline;
using Xunit;

namespace CSharpToJava.ProjectTests;

public class DefaultValueProjectTests
{
    private static string FixtureDir =>
        Path.Combine(AppContext.BaseDirectory, "SampleDefaultSolution");

    [Fact]
    public async Task LibProject_DefaultConvertedCorrectly()
    {
        var libDir = Path.Combine(FixtureDir, "SampleDefaultLib");
        Assert.True(Directory.Exists(libDir), $"Fixture not found: {libDir}");

        var options = new ConversionOptions
        {
            TypeMappingConfigPath = Path.Combine(AppContext.BaseDirectory, "config", "TypeMappings.json"),
        };

        var pipeline = new ConversionPipeline();
        var results = await pipeline.ConvertProjectWithPartialMergeAsync(libDir, options);

        Assert.NotEmpty(results);
        var result = Assert.Single(results, r =>
            string.Equals(r.FileName, "Defaults.java", StringComparison.OrdinalIgnoreCase));

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));

        // struct / enum(0 值成员) / Flags / tuple 的 default 应被正确转换
        Assert.Contains("new Point()", result.GeneratedCode);
        Assert.Contains("Status.Full", result.GeneratedCode);
        Assert.Contains("0", result.GeneratedCode); // Permissions flags default
        Assert.Contains("Tuple.of(0, null)", result.GeneratedCode);
    }
}
```

- [ ] **Step 2: 运行 csproj 级测试**

```bash
dotnet test tests/CSharpToJava.ProjectTests/CSharpToJava.ProjectTests.csproj --filter "FullyQualifiedName~DefaultValueProjectTests" --nologo
```

期望：通过。若 `Defaults.java` 中 `default(Permissions)` 未被转换为 `0` 或 `default((int,string))` 未生成 `Tuple.of(0, null)`，回到 Task 4 修复对应分支后重跑。

- [ ] **Step 3: 提交**

```bash
git add tests/CSharpToJava.ProjectTests/DefaultValueProjectTests.cs
git commit -m "test: add csproj-level default(T) conversion test"
```

---

## Task 6: 编写 sln 级测试

**Files:**
- Create: `tests/CSharpToJava.ProjectTests/DefaultValueSolutionTests.cs`

- [ ] **Step 1: 编写 sln 级测试（加载 .sln 并逐项目转换）**

`tests/CSharpToJava.ProjectTests/DefaultValueSolutionTests.cs`：
```csharp
using CSharpToJava.Core.Pipeline;
using CSharpToJava.Core.Workspace;
using Xunit;

namespace CSharpToJava.ProjectTests;

public class DefaultValueSolutionTests
{
    private static string SlnPath =>
        Path.Combine(AppContext.BaseDirectory, "SampleDefaultSolution", "SampleDefaultSolution.sln");

    [Fact]
    public async Task Solution_AllProjectsConvertedWithCorrectDefaults()
    {
        Assert.True(File.Exists(SlnPath), $"Solution not found: {SlnPath}");
        SolutionLoader.EnsureMSBuildRegistered();

        using var loader = new SolutionLoader();
        var projects = await loader.OpenSolutionAsync(SlnPath);
        Assert.Equal(2, projects.Count);

        var options = new ConversionOptions
        {
            TypeMappingConfigPath = Path.Combine(AppContext.BaseDirectory, "config", "TypeMappings.json"),
        };

        var pipeline = new ProjectConversionPipeline(options);

        foreach (var wp in projects)
        {
            var results = await pipeline.ConvertProjectAsync(
                wp.Compilation,
                projectName: wp.Name,
                projectFilePath: wp.FilePath,
                projectReferences: wp.ProjectReferences,
                isTestProject: wp.IsTestProject);

            Assert.NotEmpty(results);
            foreach (var r in results)
            {
                Assert.True(r.Success, $"{wp.Name}: {string.Join("\n", r.Diagnostics)}");
                // 解决方案级不应残留 C# 语法
                Assert.DoesNotContain("=>", r.GeneratedCode);
                Assert.DoesNotContain("?.", r.GeneratedCode);
                Assert.DoesNotContain("??", r.GeneratedCode);
            }
        }

        // 抽样验证 Lib 的 Defaults.java 与 App 的 Program.java
        var libResults = await pipeline.ConvertProjectAsync(
            projects.Single(p => p.Name == "SampleDefaultLib").Compilation,
            projectName: "SampleDefaultLib",
            projectFilePath: projects.Single(p => p.Name == "SampleDefaultLib").FilePath);
        var defaultsJava = libResults.Single(r => r.FileName == "Defaults.java");
        Assert.Contains("new Point()", defaultsJava.GeneratedCode);
        Assert.Contains("Status.Full", defaultsJava.GeneratedCode);

        var appResults = await pipeline.ConvertProjectAsync(
            projects.Single(p => p.Name == "SampleDefaultApp").Compilation,
            projectName: "SampleDefaultApp",
            projectFilePath: projects.Single(p => p.Name == "SampleDefaultApp").FilePath,
            projectReferences: projects.Single(p => p.Name == "SampleDefaultApp").ProjectReferences);
        var programJava = appResults.Single(r => r.FileName == "Program.java");
        Assert.Contains("null", programJava.GeneratedCode);   // int? / string 等可空/引用
        Assert.Contains("Decimal.ZERO", programJava.GeneratedCode);
    }
}
```

- [ ] **Step 2: 运行 sln 级测试**

```bash
dotnet test tests/CSharpToJava.ProjectTests/CSharpToJava.ProjectTests.csproj --filter "FullyQualifiedName~DefaultValueSolutionTests" --nologo
```

期望：通过。若加载/转换因可空或嵌套泛型处理不当而失败，回到 Task 4 修复相关分支后重跑。

- [ ] **Step 3: 提交**

```bash
git add tests/CSharpToJava.ProjectTests/DefaultValueSolutionTests.cs
git commit -m "test: add sln-level default(T) conversion test"
```

---

## Task 7: 全量回归与最终提交

**Files:** 无新增，仅运行

- [ ] **Step 1: 运行三个受影响测试项目全量**

```bash
dotnet test tests/CSharpToJava.ConversionTests/CSharpToJava.ConversionTests.csproj --nologo
dotnet test tests/CSharpToJava.ProjectTests/CSharpToJava.ProjectTests.csproj --nologo
dotnet test tests/CSharpToJava.Tests/CSharpToJava.Tests.csproj --filter "FullyQualifiedName~Default|FullyQualifiedName~Tuple|FullyQualifiedName~Enum|FullyQualifiedName~Struct|FullyQualifiedName~Decimal" --nologo
```

期望：全部通过（含既有 default 相关测试）。

- [ ] **Step 2: 若通过，最终汇总提交（已分步提交，此处仅确认工作树状态）**

```bash
git status --short
```

期望：工作树干净或仅有未跟踪的设计文档（设计/spec 已单独提交）。
