# UnsignedMath 独立 lib 项目 + compat 手写 Java 实现 — 实现计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 把 `_Cs2jUnsignedMath.cs` 字符串常量机制升级为真实 lib 项目 `Cs2jUnsignedMath`（嵌入资源分发 + ProjectReference 注入），Java 侧由 `csharptojava-compat` 手写 `UnsignedMath` 直接委托 JDK unsigned API。

**Architecture:** C# 侧真源落到 `src/Cs2jUnsignedMath/UnsignedMath.cs`（`public static class`，net10.0/LangVersion 11）；CLI 以 `EmbeddedResource` 嵌入两份文件，预处理阶段在中间树根释放 `Cs2jUnsignedMath/` 子目录并给每个 host csproj 注入 `<ProjectReference>`；主转换器（workspace 路径与 graph 路径）按 csproj 文件名跳过该项目（仅符号来源）；`config/TypeMappings.json` 把 `Cs2jUnsigned.UnsignedMath` 映射到 compat 手写类，调用点带 compat import。

**Tech Stack:** .NET 10 SDK / Roslyn MSBuildWorkspace / xUnit；Java 28 + Maven（JUnit Jupiter 5.11.4）。

**Spec:** `docs/superpowers/specs/2026-09-24-unsigned-math-lib-project-design.md`（修订版 2）

---

## 0. 前置事实（本计划编写时已逐一验证，执行者不必重查）

对后续任务起支撑作用的代码库事实，均已核实：

1. **MSBuildWorkspace 会沿 sln 外 ProjectReference 加载项目**（spec §5.2 前置验证项）。已用探针工程验证：`SolutionLoader.OpenSolutionAsync` 打开只含 App 的 sln（App 引用 sln 外 LibOutside），返回列表含 LibOutside，且 App 的 compilation 能解析 `LibOutside.Helper`。因此**不需要 fallback 补载**；Task 9 的端到端测试把这个行为锁进回归。
2. **旧生成 Java 的调用形态**（对 tmp 探针输出实测）：调用点为全限定 `Cs2jUnsigned.UnsignedMath.addUInt32((int)(a), (int)(b))`——**方法名是 camelCase**（转换器对静态调用统一 Pascal→camel），辅助类签名为 `int`（byte/ushort 域）、`long`（u64 域）、`Decimal`、`IFormatProvider` 参数。compat 全库同类手写类（`Convert`/`BitConverter` 等）均为 camelCase 方法名。**因此 compat 手写类必须用 camelCase 方法名**（spec §6.3 的"Pascal 透传"论证与实测不符，以实测为准；方法名实测形态与旧产物一致）。
3. 旧转换产物存在真实语义缺陷（如 `toStringUInt64(-1L)` 走有符号 `Long.toString` 输出 `"-1"`），新实现用 `Long.toUnsignedString` 修正——这是行为改进，不是回归。
4. compat 基建已具备：`MathHelper.formatNumeric(String, Object)`（C# 数字格式串）、`Decimal.valueOf(long)` / `Decimal.parse(String)` / `equals`、`IFormatProvider`、`CultureInfo.getInvariantCulture()`；JUnit 5.11.4 + surefire 已配置。
5. `csharptojava-compat` 是每个生成模块的默认依赖（`src/CSharpToJava.Core/Pipeline/Planning/WorkspacePlanBuilder.cs:70`），生成 pom 零改动成立。
6. 阶段复制层已忽略 `bin`/`obj`（`SupportedProjectFiltering.IgnoredDirectoryNames`），spec §5.1 ② 的 obj/bin 冗余无需处理。
7. `ProjectDiscovery.LoadRecursive`（`src/CSharpToJava.CLI/ProjectDiscovery.cs:165-215`）已沿 ProjectReference 递归加载，无需改动。
8. CLI 程序集对 `CSharpToJava.ProjectTests` 有 `InternalsVisibleTo`（CLI csproj），internal 类可直接测试。
9. `2026-08-15` 设计文档 §4.3 的保留项目名契约已增补到位，无需再改旧文档。
10. 基线复现：当前 master 流水线在 SampleUnsignedSolution 上生成 3 个模块计数（父 + app + lib）、两份 `Cs2jUnsigned/UnsignedMath.java`——Task 10 的冒烟用它做前后对比。
11. 本仓库在 `dev-3.x` 分支上开发（工作区当前干净），计划直接在 `dev-3.x` 上按任务提交。

**通用命令约定**（各任务复用）：
- 构建：`dotnet build <csproj> -v q --nologo`，期望 `0 个错误`。
- C# 测试：`dotnet test tests/CSharpToJava.ProjectTests/CSharpToJava.ProjectTests.csproj --filter "<FullyQualifiedNameContains>"`，期望 `Passed!`。
- 提交：只 `git add` 本任务涉及文件，逐任务一次提交。

---

## 文件结构总览

| 文件 | 动作 | 职责 |
| --- | --- | --- |
| `src/Cs2jUnsignedMath/Cs2jUnsignedMath.csproj` | 新建 | lib 项目定义（net10.0 / LangVersion 11） |
| `src/Cs2jUnsignedMath/UnsignedMath.cs` | 新建 | 辅助类唯一真源（public） |
| `CSharpToJavaConverter.slnx` | 修改 | 挂入 lib 项目 |
| `src/CSharpToJava.CLI/CSharpToJava.CLI.csproj` | 修改 | 嵌入两份支撑文件（EmbeddedResource） |
| `src/CSharpToJava.CLI/UnsignedMathSupportFiles.cs` | 新建 | 从嵌入资源读取/释放支撑文件 |
| `src/CSharpToJava.CLI/Cs2jUnsignedMathProject.cs` | 新建 | 项目名判定（csproj 文件名精确匹配） |
| `src/CSharpToJava.CLI/ProjectUnsignedTypePreprocessor.cs` | 修改 | 释放库项目 + ProjectReference 注入 + 特判/碰撞/TFM 告警 |
| `src/CSharpToJava.CLI/MultiModulePlanner.cs` | 修改 | graph 路径过滤 Cs2jUnsignedMath |
| `src/CSharpToJava.CLI/Program.cs` | 修改 | workspace 过滤、单文件模式、2 处注释 |
| `src/CSharpToJava.Core/UnsignedTypeRewriter/UnsignedMathFileContent.cs` | 删除 | 字符串常量退役 |
| `config/TypeMappings.json` | 修改 | 类型级映射条目 |
| `java/csharptojava-compat/src/main/java/io/github/ningpp/compat/UnsignedMath.java` | 新建 | 手写 compat 实现（camelCase，委托 JDK） |
| `java/csharptojava-compat/src/test/java/io/github/ningpp/compat/UnsignedMathTest.java` | 新建 | 边界值对拍 JUnit |
| `tests/CSharpToJava.ProjectTests/UnsignedMathSupportFilesTests.cs` | 新建 | 嵌入资源读取测试 |
| `tests/CSharpToJava.ProjectTests/UnsignedTypeProjectTests.cs` | 修改 | 项目模式断言全面更新 |
| `tests/CSharpToJava.ProjectTests/UnsignedPreprocessHelpersTests.cs` | 新建 | TFM 判定单元测试 |
| `tests/CSharpToJava.ProjectTests/MultiModulePlannerTests.cs` | 新建 | planner 过滤测试 |
| `tests/CSharpToJava.ProjectTests/UnsignedConversionEndToEndTests.cs` | 新建 | 预处理→转换端到端 |
| `tests/CSharpToJava.ProjectTests/UnsignedTypeRewriterUnitTests.cs` | 修改 | helper 断言改读真源、internal→public |
| `tests/CSharpToJava.ProjectTests/UnsignedTypeSemanticsTests.cs` | 修改 | helper 类型改由引用程序集提供 |
| `tests/CSharpToJava.ProjectTests/CSharpToJava.ProjectTests.csproj` | 修改 | 增加对 lib 项目的 ProjectReference |
| `tests/SampleUnsignedSolution/SampleNameCollision/*` | 新建 | 碰撞 fail-fast 测试夹具 |

---

### Task 1: 建立 `Cs2jUnsignedMath` lib 项目（真源落地）

**Files:**
- Create: `src/Cs2jUnsignedMath/Cs2jUnsignedMath.csproj`
- Create: `src/Cs2jUnsignedMath/UnsignedMath.cs`
- Modify: `CSharpToJavaConverter.slnx`

- [ ] **Step 1: 创建 csproj**

写入 `src/Cs2jUnsignedMath/Cs2jUnsignedMath.csproj`：

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <LangVersion>11</LangVersion>
    <Nullable>disable</Nullable>
    <ImplicitUsings>disable</ImplicitUsings>
  </PropertyGroup>

</Project>
```

（`LangVersion 11` 为 `>>>` 所需；该项目自带语言版本，不再约束宿主项目。）

- [ ] **Step 2: 创建 UnsignedMath.cs 真源**

写入 `src/Cs2jUnsignedMath/UnsignedMath.cs`。内容 = 现 `UnsignedMathFileContent.Generate()`（`src/CSharpToJava.Core/UnsignedTypeRewriter/UnsignedMathFileContent.cs:16-134` 的原始字符串体）做两处调整：`internal static class` → `public static class`；顶部加文件头注释。完整内容：

```csharp
// Cs2jUnsigned.UnsignedMath：无符号数学辅助类（唯一真源）。
// 本文件既是本库项目的源码，又被 CLI 以 EmbeddedResource 嵌入分发：
// unsigned-type 预处理把它释放到中间树 Cs2jUnsignedMath/ 下，主转换器
// 跳过该项目的转换，调用点经 TypeMappings 映射到 compat 手写类。
// 单文件模式（unsigned-type -i/-o）会把本文件原样写到输出目录，用户
// 自行编译后再走主转换，因此内容须保持"可转换 C# 子集"：
// - 无符号右移一律写 C# 11 的 >>>（转换器映射为 Java >>>）；
// - 不得写 (ulong)a / (ulong)b 这类强转——转换器会把 (ulong) 擦成 (long)；
// - ulong → 浮点/decimal 一律经 ((ulong)v).ToString() 再 Parse。
namespace Cs2jUnsigned
{
    public static class UnsignedMath
    {
        // ---- Int32 domain ----
        public static int AddInt32(int a, int b) => unchecked(a + b);
        public static int SubtractInt32(int a, int b) => unchecked(a - b);
        public static int MultiplyInt32(int a, int b) => unchecked(a * b);
        public static int DivideInt32(int a, int b) => a / b;
        public static int RemainderInt32(int a, int b) => a % b;
        public static int AndInt32(int a, int b) => a & b;
        public static int OrInt32(int a, int b) => a | b;
        public static int XorInt32(int a, int b) => a ^ b;
        public static int NotInt32(int a) => ~a;
        public static int LeftShiftInt32(int a, int n) => a << (n & 31);
        public static int RightShiftInt32(int a, int n) => a >> (n & 31);
        public static int CompareInt32(int a, int b) => a < b ? -1 : a > b ? 1 : 0;

        // ---- Int64 domain ----
        public static long AddInt64(long a, long b) => unchecked(a + b);
        public static long SubtractInt64(long a, long b) => unchecked(a - b);
        public static long MultiplyInt64(long a, long b) => unchecked(a * b);
        public static long DivideInt64(long a, long b) => a / b;
        public static long RemainderInt64(long a, long b) => a % b;
        public static long AndInt64(long a, long b) => a & b;
        public static long OrInt64(long a, long b) => a | b;
        public static long XorInt64(long a, long b) => a ^ b;
        public static int CompareInt64(long a, long b) => a < b ? -1 : a > b ? 1 : 0;

        // ---- UInt32 domain ----
        public static int AddUInt32(int a, int b) => unchecked(a + b);
        public static int SubtractUInt32(int a, int b) => unchecked(a - b);
        public static int MultiplyUInt32(int a, int b) => unchecked(a * b);
        public static int DivideUInt32(int a, int b) => unchecked((int)((a & 0xFFFFFFFFL) / (b & 0xFFFFFFFFL)));
        public static int RemainderUInt32(int a, int b) => unchecked((int)((a & 0xFFFFFFFFL) % (b & 0xFFFFFFFFL)));
        public static int AndUInt32(int a, int b) => a & b;
        public static int OrUInt32(int a, int b) => a | b;
        public static int XorUInt32(int a, int b) => a ^ b;
        public static int NotUInt32(int a) => ~a;
        public static int LeftShiftUInt32(int a, int n) => a << (n & 31);
        public static int RightShiftUInt32(int a, int n) => a >>> (n & 31);
        public static int CompareUInt32(int a, int b)
        {
            var ua = a & 0xFFFFFFFFL;
            var ub = b & 0xFFFFFFFFL;
            return ua < ub ? -1 : ua > ub ? 1 : 0;
        }

        // ---- UInt64 domain ----
        public static long AddUInt64(long a, long b) => unchecked(a + b);
        public static long SubtractUInt64(long a, long b) => unchecked(a - b);
        public static long MultiplyUInt64(long a, long b) => unchecked(a * b);
        public static long DivideUInt64(long a, long b)
        {
            if (b == 0)
            {
                // 让除零自然抛出 DivideByZeroException / ArithmeticException。
                return a / b;
            }

            if (b < 0)
            {
                // 无符号下 b >= 2^63,商只可能是 0 或 1。
                return CompareUInt64(a, b) < 0 ? 0 : 1;
            }

            if (a >= 0)
            {
                return a / b;
            }

            // a 的符号位为 1:先对折再修正(JDK Long.divideUnsigned 同款算法)。
            long quotient = unchecked(((a >>> 1) / b) * 2);
            long remainder = unchecked(a - quotient * b);
            return quotient + (CompareUInt64(remainder, b) >= 0 ? 1 : 0);
        }
        public static long RemainderUInt64(long a, long b) => unchecked(a - DivideUInt64(a, b) * b);
        public static long AndUInt64(long a, long b) => a & b;
        public static long OrUInt64(long a, long b) => a | b;
        public static long XorUInt64(long a, long b) => a ^ b;
        public static long NotUInt64(long a) => ~a;
        public static long LeftShiftUInt64(long a, int n) => a << (n & 63);
        public static long RightShiftUInt64(long a, int n) => a >>> (n & 63);
        public static int CompareUInt64(long a, long b)
        {
            if (a == b) return 0;
            if (a >= 0 && b < 0) return -1;
            if (a < 0 && b >= 0) return 1;
            return a < b ? -1 : 1;
        }

        // ---- Conversions ----
        public static System.Byte ToByteInt32(int v) => unchecked((byte)(v & 0xFF));
        public static System.Byte ToByteInt64(long v) => unchecked((byte)((int)v & 0xFF));
        public static System.UInt16 ToUInt16Int32(int v) => unchecked((ushort)(v & 0xFFFF));
        public static System.UInt16 ToUInt16Int64(long v) => unchecked((ushort)((int)v & 0xFFFF));
        public static long ToInt64UInt32(int v) => v & 0xFFFFFFFFL;
        public static System.UInt64 ToUInt64UInt32(int v) => unchecked((ulong)(v & 0xFFFFFFFFL));
        public static int ToInt32UInt64(long v) => unchecked((int)v);
        public static double ToDoubleUInt32(int v) => (double)(v & 0xFFFFFFFFL);
        public static double ToDoubleUInt64(long v) => double.Parse(((ulong)v).ToString(System.Globalization.CultureInfo.InvariantCulture));
        public static float ToSingleUInt32(int v) => (float)(v & 0xFFFFFFFFL);
        public static float ToSingleUInt64(long v) => float.Parse(((ulong)v).ToString(System.Globalization.CultureInfo.InvariantCulture));
        public static decimal ToDecimalUInt32(int v) => (decimal)(v & 0xFFFFFFFFL);
        public static decimal ToDecimalUInt64(long v) => decimal.Parse(((ulong)v).ToString(System.Globalization.CultureInfo.InvariantCulture));

        // ---- ToString (unsigned) ----
        public static string ToStringUInt32(int v) => (v & 0xFFFFFFFFL).ToString(System.Globalization.CultureInfo.InvariantCulture);
        public static string ToStringUInt32(int v, string? format) => ToStringUInt32(v, format, null);
        public static string ToStringUInt32(int v, System.IFormatProvider? provider) => ToStringUInt32(v);
        public static string ToStringUInt32(int v, string? format, System.IFormatProvider? provider) => format == null || format.Length == 0 ? ToStringUInt32(v) : (v & 0xFFFFFFFFL).ToString(format, provider ?? System.Globalization.CultureInfo.InvariantCulture);
        public static string ToStringUInt64(long v) => ((ulong)v).ToString(System.Globalization.CultureInfo.InvariantCulture);
        public static string ToStringUInt64(long v, string? format) => ToStringUInt64(v, format, null);
        public static string ToStringUInt64(long v, System.IFormatProvider? provider) => ToStringUInt64(v);
        public static string ToStringUInt64(long v, string? format, System.IFormatProvider? provider) => format == null || format.Length == 0 ? ToStringUInt64(v) : ((ulong)v).ToString(format, provider ?? System.Globalization.CultureInfo.InvariantCulture);
    }
}
```

（与 `Generate()` 输出逐行一致，仅 `internal`→`public` + 文件头；namespace、方法名与签名全部不变。）

- [ ] **Step 3: 挂入 slnx**

`CSharpToJavaConverter.slnx`：在 `<Solution>` 直接子级的两个 `<Project .../>` 之后（CLI 条目后）加：

```xml
  <Project Path="src\Cs2jUnsignedMath\Cs2jUnsignedMath.csproj" Id="3f4a5b6c-7d8e-4f90-a1b2-3c4d5e6f7a8b" Type="Microsoft.VisualStudio.Project" />
```

并在 `/src/` Folder 的 `<ProjectRef Id="e5f6a7b8-c9d0-4e8f-2a1b-5e6f7a8b9c0d" />` 之后加：

```xml
    <ProjectRef Id="3f4a5b6c-7d8e-4f90-a1b2-3c4d5e6f7a8b" />
```

- [ ] **Step 4: 构建验证**

```bash
dotnet build src/Cs2jUnsignedMath/Cs2jUnsignedMath.csproj -v q --nologo
dotnet build CSharpToJavaConverter.slnx -v q --nologo
```

期望：两者均 `0 个错误`（`>>>` 由 LangVersion 11 覆盖）。

- [ ] **Step 5: 提交**

```bash
git add src/Cs2jUnsignedMath/ CSharpToJavaConverter.slnx
git commit -m "feat: add Cs2jUnsignedMath lib project (true source for unsigned helper)"
```

---

### Task 2: CLI 嵌入资源 + `UnsignedMathSupportFiles`

**Files:**
- Modify: `src/CSharpToJava.CLI/CSharpToJava.CLI.csproj`
- Create: `src/CSharpToJava.CLI/UnsignedMathSupportFiles.cs`
- Test: `tests/CSharpToJava.ProjectTests/UnsignedMathSupportFilesTests.cs`

- [ ] **Step 1: 写失败测试**

新建 `tests/CSharpToJava.ProjectTests/UnsignedMathSupportFilesTests.cs`：

```csharp
using CSharpToJava.CLI;

namespace CSharpToJava.ProjectTests;

public class UnsignedMathSupportFilesTests
{
    [Fact]
    public void GetUnsignedMathFileContent_ReturnsPublicTrueSource()
    {
        var content = UnsignedMathSupportFiles.GetUnsignedMathFileContent();
        Assert.Contains("namespace Cs2jUnsigned", content);
        Assert.Contains("public static class UnsignedMath", content);
        Assert.DoesNotContain("internal static class UnsignedMath", content);
        Assert.Contains("public static int AddUInt32(int a, int b)", content);
    }

    [Fact]
    public async Task ReleaseAsync_WritesProjectAndSourceFiles()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cs2j-unsigned-support-" + Guid.NewGuid().ToString("N"));
        try
        {
            await UnsignedMathSupportFiles.ReleaseAsync(dir);
            Assert.True(File.Exists(Path.Combine(dir, "Cs2jUnsignedMath.csproj")));
            var csproj = await File.ReadAllTextAsync(Path.Combine(dir, "Cs2jUnsignedMath.csproj"));
            var source = await File.ReadAllTextAsync(Path.Combine(dir, "UnsignedMath.cs"));
            Assert.Contains("<TargetFramework>net10.0</TargetFramework>", csproj);
            Assert.Contains("<LangVersion>11</LangVersion>", csproj);
            Assert.Contains("public static class UnsignedMath", source);
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }
}
```

- [ ] **Step 2: 运行确认失败**

```bash
dotnet test tests/CSharpToJava.ProjectTests/CSharpToJava.ProjectTests.csproj --filter "FullyQualifiedName~UnsignedMathSupportFilesTests"
```

期望：编译失败，`UnsignedMathSupportFiles` 不存在。

- [ ] **Step 3: CLI csproj 加 EmbeddedResource**

`src/CSharpToJava.CLI/CSharpToJava.CLI.csproj`：在 `PackageReference` 的 ItemGroup 之后新增（显式 `LogicalName` 使资源名确定，读取端不依赖 MSBuild 默认命名）：

```xml
  <ItemGroup>
    <!-- Cs2jUnsignedMath 支撑库（唯一真源在 src/Cs2jUnsignedMath/，此处嵌入分发） -->
    <EmbeddedResource Include="..\Cs2jUnsignedMath\UnsignedMath.cs">
      <Link>support\Cs2jUnsignedMath\UnsignedMath.cs</Link>
      <LogicalName>Cs2jUnsignedMath.UnsignedMath.cs</LogicalName>
    </EmbeddedResource>
    <EmbeddedResource Include="..\Cs2jUnsignedMath\Cs2jUnsignedMath.csproj">
      <Link>support\Cs2jUnsignedMath\Cs2jUnsignedMath.csproj</Link>
      <LogicalName>Cs2jUnsignedMath.Cs2jUnsignedMath.csproj</LogicalName>
    </EmbeddedResource>
  </ItemGroup>
```

- [ ] **Step 4: 实现 UnsignedMathSupportFiles**

新建 `src/CSharpToJava.CLI/UnsignedMathSupportFiles.cs`：

```csharp
namespace CSharpToJava.CLI;

/// <summary>
/// Cs2jUnsignedMath 支撑文件的嵌入资源读取/释放。真源是
/// src/Cs2jUnsignedMath/ 下的两个文件（构建期嵌入 CLI 程序集），结构上
/// 不存在第二份副本可漂移。单文件模式（unsigned-type -i/-o）沿用
/// _Cs2j* 支撑文件前缀契约，文件名与项目模式的 UnsignedMath.cs 不同名是有意的。
/// </summary>
internal static class UnsignedMathSupportFiles
{
    public const string LibDirectoryName = "Cs2jUnsignedMath";
    public const string SingleFileHelperFileName = "_Cs2jUnsignedMath.cs";

    private const string SourceResourceName = "Cs2jUnsignedMath.UnsignedMath.cs";
    private const string ProjectResourceName = "Cs2jUnsignedMath.Cs2jUnsignedMath.csproj";

    public static string GetUnsignedMathFileContent() => ReadResource(SourceResourceName);

    public static string GetProjectFileContent() => ReadResource(ProjectResourceName);

    /// <summary>把库项目（csproj + 源码）释放到目标目录（UTF-8 无 BOM）。</summary>
    public static async Task ReleaseAsync(string destinationDirectory)
    {
        Directory.CreateDirectory(destinationDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(destinationDirectory, "UnsignedMath.cs"),
            GetUnsignedMathFileContent(),
            new System.Text.UTF8Encoding(false));
        await File.WriteAllTextAsync(
            Path.Combine(destinationDirectory, "Cs2jUnsignedMath.csproj"),
            GetProjectFileContent(),
            new System.Text.UTF8Encoding(false));
    }

    private static string ReadResource(string resourceName)
    {
        var assembly = typeof(UnsignedMathSupportFiles).Assembly;
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException(
                $"Embedded resource not found: {resourceName}. " +
                "The CLI project must embed src/Cs2jUnsignedMath/* with explicit LogicalName.");
        using var reader = new System.IO.StreamReader(stream, System.Text.Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
```

- [ ] **Step 5: 运行确认通过**

```bash
dotnet test tests/CSharpToJava.ProjectTests/CSharpToJava.ProjectTests.csproj --filter "FullyQualifiedName~UnsignedMathSupportFilesTests"
```

期望：`Passed! 2`。

- [ ] **Step 6: 提交**

```bash
git add src/CSharpToJava.CLI/CSharpToJava.CLI.csproj src/CSharpToJava.CLI/UnsignedMathSupportFiles.cs tests/CSharpToJava.ProjectTests/UnsignedMathSupportFilesTests.cs
git commit -m "feat: embed UnsignedMath support files in CLI as resources"
```

---

### Task 3: 预处理器改造——释放库项目 + ProjectReference 注入

**Files:**
- Create: `src/CSharpToJava.CLI/Cs2jUnsignedMathProject.cs`
- Modify: `src/CSharpToJava.CLI/ProjectUnsignedTypePreprocessor.cs`
- Test: `tests/CSharpToJava.ProjectTests/UnsignedTypeProjectTests.cs`

本任务后：中间树根出现 `Cs2jUnsignedMath/`（csproj + UnsignedMath.cs）；每个 host csproj 含 `<ProjectReference>`、不再有 Compile Link；根目录无平文件。根 host 特判、碰撞、TFM 告警分别在 Task 4/5。

- [ ] **Step 1: 更新测试（先改断言，确认失败）**

`tests/CSharpToJava.ProjectTests/UnsignedTypeProjectTests.cs`：

(a) `LibProject_AllUnsignedTypes_RewrittenCorrectly` 中，把

```csharp
            Assert.True(File.Exists(Path.Combine(intermediateDir, "_Cs2jUnsignedMath.cs")),
                "Unsigned math helper file not generated");
            Assert.False(File.Exists(Path.Combine(intermediateDir, "_Cs2jUnsignedExtensions.cs")),
                "Old extension file must not be generated");
```

替换为：

```csharp
            Assert.True(File.Exists(Path.Combine(intermediateDir, "Cs2jUnsignedMath", "Cs2jUnsignedMath.csproj")),
                "Cs2jUnsignedMath support project not generated");
            Assert.True(File.Exists(Path.Combine(intermediateDir, "Cs2jUnsignedMath", "UnsignedMath.cs")),
                "Cs2jUnsignedMath helper source not generated");
            Assert.False(File.Exists(Path.Combine(intermediateDir, "_Cs2jUnsignedMath.cs")),
                "Old flat helper file must not be generated");
            Assert.False(File.Exists(Path.Combine(intermediateDir, "_Cs2jUnsignedExtensions.cs")),
                "Old extension file must not be generated");

            var libCsprojContent = await File.ReadAllTextAsync(
                Path.Combine(intermediateDir, "SampleUnsignedLib.csproj"));
            Assert.Contains(
                "<ProjectReference Include=\"Cs2jUnsignedMath\\Cs2jUnsignedMath.csproj\" />",
                libCsprojContent);
            Assert.DoesNotContain("_Cs2jUnsignedMath.cs", libCsprojContent);
```

(b) `AppProject_CrossProjectRewrite_Succeeds` 与 `SlnProject_FullRewrite_AllFilesSucceed` 中，`Assert.True(result.Success, ...)` 之后（两个测试都加）：

```csharp
            foreach (var projectName in new[] { "SampleUnsignedApp", "SampleUnsignedLib" })
            {
                var csprojContent = await File.ReadAllTextAsync(
                    Path.Combine(result.IntermediateRoot, projectName, projectName + ".csproj"));
                Assert.Contains(
                    "<ProjectReference Include=\"..\\Cs2jUnsignedMath\\Cs2jUnsignedMath.csproj\" />",
                    csprojContent);
                Assert.DoesNotContain("_Cs2jUnsignedMath.cs", csprojContent);
            }

            Assert.True(File.Exists(Path.Combine(result.IntermediateRoot, "Cs2jUnsignedMath", "UnsignedMath.cs")),
                "Cs2jUnsignedMath helper source not generated");
            Assert.False(File.Exists(Path.Combine(result.IntermediateRoot, "_Cs2jUnsignedMath.cs")),
                "Old flat helper file must not be generated");
```

（两个测试共用同一 outputDir `.test-output-sln`，本就有先后覆盖，断言互不冲突。）

(c) `ExplicitCompileLib_ExtensionFileAddedToProjectCompileItems` 整体替换为：

```csharp
    [Fact]
    public async Task ExplicitCompileLib_ProjectReferenceInjected()
    {
        var libDir = Path.Combine(FixtureDir, "SampleExplicitCompileLib");
        Assert.True(Directory.Exists(libDir), $"Fixture not found: {libDir}");
        var outputDir = Path.Combine(libDir, "..", ".test-output-explicit");

        try
        {
            var result = await ProjectUnsignedTypePreprocessor.PreprocessAsync(
                new ProjectUnsignedTypePreprocessRequest
                {
                    SourcePath = libDir,
                    DestinationRoot = outputDir,
                    Verbose = false,
                });

            Assert.True(result.Success, string.Join("\n", result.Diagnostics));

            var csprojPath = Path.Combine(result.IntermediateRoot, "SampleExplicitCompileLib.csproj");
            var csprojContent = await File.ReadAllTextAsync(csprojPath);
            Assert.Contains(
                "<ProjectReference Include=\"Cs2jUnsignedMath\\Cs2jUnsignedMath.csproj\" />",
                csprojContent);
            Assert.DoesNotContain("_Cs2jUnsignedMath.cs", csprojContent);
            Assert.DoesNotContain(".test-output-explicit", csprojContent);
        }
        finally
        {
            if (Directory.Exists(outputDir))
            {
                Directory.Delete(outputDir, recursive: true);
            }
        }
    }
```

(d) `CrossDirectoryCompile_ExtensionFileAddedToCompilingProjects` 中，把

```csharp
            Assert.Contains("_Cs2jUnsignedMath.cs", srcContent);
            Assert.Contains("_Cs2jUnsignedMath.cs", testsContent);
            Assert.DoesNotContain(".test-output-cross", testsContent);
            Assert.True(File.Exists(Path.Combine(result.IntermediateRoot, "_Cs2jUnsignedMath.cs")),
                "Shared helper file not generated at intermediate root");
            Assert.False(File.Exists(Path.Combine(result.IntermediateRoot, "Tests", "_Cs2jUnsignedMath.cs")),
                "Helper file must not be copied into project directories");
            Assert.False(File.Exists(Path.Combine(result.IntermediateRoot, "Src", "_Cs2jUnsignedMath.cs")),
                "Helper file must not be copied into project directories");
```

替换为：

```csharp
            Assert.Contains(
                "<ProjectReference Include=\"..\\Cs2jUnsignedMath\\Cs2jUnsignedMath.csproj\" />",
                srcContent);
            Assert.Contains(
                "<ProjectReference Include=\"..\\Cs2jUnsignedMath\\Cs2jUnsignedMath.csproj\" />",
                testsContent);
            Assert.DoesNotContain(".test-output-cross", testsContent);
            Assert.True(File.Exists(Path.Combine(result.IntermediateRoot, "Cs2jUnsignedMath", "UnsignedMath.cs")),
                "Cs2jUnsignedMath helper source not generated at intermediate root");
            Assert.False(File.Exists(Path.Combine(result.IntermediateRoot, "_Cs2jUnsignedMath.cs")),
                "Old flat helper file must not be generated");
            Assert.False(Directory.Exists(Path.Combine(result.IntermediateRoot, "Tests", "Cs2jUnsignedMath")),
                "Helper project must not be copied into project directories");
            Assert.False(Directory.Exists(Path.Combine(result.IntermediateRoot, "Src", "Cs2jUnsignedMath")),
                "Helper project must not be copied into project directories");
```

(e) `Rerun_DoesNotLeaveOldExtensionArtifacts` 的断言扩展（追加一行）：

```csharp
                Assert.False(File.Exists(Path.Combine(result.IntermediateRoot, "_Cs2jUnsignedMath.cs")),
                    "old flat helper file after rerun");
```

- [ ] **Step 2: 运行确认失败**

```bash
dotnet test tests/CSharpToJava.ProjectTests/CSharpToJava.ProjectTests.csproj --filter "FullyQualifiedName~UnsignedTypeProjectTests"
```

期望：`LibProject/SlnProject/ExplicitCompileLib/CrossDirectoryCompile` 5 个测试 FAIL（缺 Cs2jUnsignedMath 目录 / ProjectReference）。

- [ ] **Step 3: 新建 Cs2jUnsignedMathProject 判定类**

新建 `src/CSharpToJava.CLI/Cs2jUnsignedMathProject.cs`：

```csharp
namespace CSharpToJava.CLI;

/// <summary>
/// 按 csproj 文件名精确识别 cs2j 内部支撑库 Cs2jUnsignedMath
/// （2026-08-15 设计 §4.3 的保留项目名契约，项目级落地）。
/// </summary>
internal static class Cs2jUnsignedMathProject
{
    public const string ProjectFileName = "Cs2jUnsignedMath.csproj";

    public static bool IsCs2jUnsignedMathProject(string? projectFilePath)
        => projectFilePath != null
           && Path.GetFileName(projectFilePath).Equals(ProjectFileName, StringComparison.OrdinalIgnoreCase);
}
```

- [ ] **Step 4: 重写预处理器扩展文件逻辑**

`src/CSharpToJava.CLI/ProjectUnsignedTypePreprocessor.cs`：

(a) `PreprocessAsync` 中（现 `:165-169`）调用点加 `diagnostics` 实参：

```csharp
        if (statistics.OperatorsRewritten > 0 || statistics.CastsRewritten > 0)
        {
            await WriteExtensionFilesAsync(
                intermediateRoot, sourceRoot, layout, exclusionInfo, rewrittenSourceFiles, projectGraph, diagnostics);
        }
```

(b) 删除方法 `WriteExtensionFilesAsync`（现 `:314-330`）、`EnsureExtensionCompileItems`（现 `:430-462`）、`EnsureExtensionCompileItem`（现 `:464-528`）、`MatchesHelperFile`（现 `:530-540`），代之以：

```csharp
    /// <summary>
    /// 把 Cs2jUnsignedMath 支撑库项目释放到中间树根（唯一真源经 CLI 嵌入资源
    /// 分发），并给每个实际编译了被改写源文件的项目注入 ProjectReference。
    /// host csproj 镜像原树任意深度，Include 相对路径动态计算。
    /// </summary>
    private static async Task WriteExtensionFilesAsync(
        string intermediateRoot,
        string sourceRoot,
        ProjectPreprocessSourceLayout layout,
        SupportedProjectFiltering.ExclusionInfo exclusionInfo,
        IReadOnlyList<string> rewrittenFiles,
        ProjectGraph? projectGraph,
        List<UnsignedTypeRewriterDiagnostic> diagnostics)
    {
        await UnsignedMathSupportFiles.ReleaseAsync(
            Path.Combine(intermediateRoot, UnsignedMathSupportFiles.LibDirectoryName));

        var hostProjectDirectories = ComputeAllExtensionHostProjectDirectories(
            layout, exclusionInfo, rewrittenFiles, projectGraph);
        EnsureExtensionProjectReferences(
            sourceRoot, intermediateRoot, layout, exclusionInfo, hostProjectDirectories, diagnostics);
    }

    /// <summary>
    /// 对每个编译被改写源文件的项目，注入指向 Cs2jUnsignedMath 的
    /// ProjectReference（追加前先移除指向同一目标的旧条目防重复）。
    /// </summary>
    private static void EnsureExtensionProjectReferences(
        string sourceRoot,
        string intermediateRoot,
        ProjectPreprocessSourceLayout layout,
        SupportedProjectFiltering.ExclusionInfo exclusionInfo,
        IReadOnlySet<string> hostProjectDirectories,
        List<UnsignedTypeRewriterDiagnostic> diagnostics)
    {
        var libProjectPath = Path.Combine(
            intermediateRoot,
            UnsignedMathSupportFiles.LibDirectoryName,
            Cs2jUnsignedMathProject.ProjectFileName);

        foreach (var projectFile in layout.ProjectFiles
                     .Where(file => file.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)))
        {
            if (SupportedProjectFiltering.IsUnderExcludedProject(projectFile, exclusionInfo))
            {
                continue;
            }

            var projectDirectory = Path.GetDirectoryName(projectFile)!;
            if (!hostProjectDirectories.Contains(projectDirectory))
            {
                continue;
            }

            var destinationProjectFile = Path.Combine(
                intermediateRoot,
                Path.GetRelativePath(sourceRoot, projectFile));
            EnsureExtensionProjectReference(
                destinationProjectFile, libProjectPath, intermediateRoot, diagnostics);
        }
    }

    private static void EnsureExtensionProjectReference(
        string projectFilePath,
        string libProjectPath,
        string intermediateRoot,
        List<UnsignedTypeRewriterDiagnostic> diagnostics)
    {
        if (!File.Exists(projectFilePath))
        {
            return;
        }

        XDocument document;
        try
        {
            document = XDocument.Load(projectFilePath, LoadOptions.PreserveWhitespace);
        }
        catch (Exception)
        {
            return;
        }

        var root = document.Root;
        if (root == null || !root.Name.LocalName.Equals("Project", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var projectDirectory = Path.GetDirectoryName(projectFilePath)!;

        foreach (var referenceElement in root.Descendants()
                     .Where(e => e.Name.LocalName == "ProjectReference")
                     .ToList())
        {
            var include = (string?)referenceElement.Attribute("Include");
            if (string.IsNullOrWhiteSpace(include))
            {
                continue;
            }

            var resolvedPath = Path.GetFullPath(Path.Combine(projectDirectory, include!));
            if (PathsEqual(resolvedPath, libProjectPath))
            {
                referenceElement.Remove();
            }
        }

        var relativeInclude = Path.GetRelativePath(projectDirectory, libProjectPath);
        root.Add(
            new XText(Environment.NewLine + "  "),
            new XElement("ItemGroup",
                new XElement("ProjectReference", new XAttribute("Include", relativeInclude))),
            new XText(Environment.NewLine));

        document.Save(projectFilePath, SaveOptions.DisableFormatting);
    }

    private static bool PathsEqual(string a, string b)
        => string.Equals(
            Path.GetFullPath(a).TrimEnd('\\', '/'),
            Path.GetFullPath(b).TrimEnd('\\', '/'),
            StringComparison.OrdinalIgnoreCase);
```

注意：保留 `GetPropertyValue`（后续任务仍用）；类头 `using CSharpToJava.Core.UnsignedTypeRewriter;` 保留（`UnsignedTypeRewriterDiagnostic` 等仍需要）。此时 `UnsignedMathFileContent` 在本文件已无引用（Program.cs 与两个测试文件仍在用，Task 6 删）。

- [ ] **Step 5: 运行确认通过**

```bash
dotnet test tests/CSharpToJava.ProjectTests/CSharpToJava.ProjectTests.csproj --filter "FullyQualifiedName~UnsignedTypeProjectTests"
```

期望：`Passed! 6`（6 个测试全绿）。

- [ ] **Step 6: 提交**

```bash
git add src/CSharpToJava.CLI/Cs2jUnsignedMathProject.cs src/CSharpToJava.CLI/ProjectUnsignedTypePreprocessor.cs tests/CSharpToJava.ProjectTests/UnsignedTypeProjectTests.cs
git commit -m "refactor: unsigned preprocessor releases Cs2jUnsignedMath project + ProjectReference"
```

---

### Task 4: 根目录 host 特判——排除默认 glob

**Files:**
- Modify: `src/CSharpToJava.CLI/ProjectUnsignedTypePreprocessor.cs`
- Test: `tests/CSharpToJava.ProjectTests/UnsignedTypeProjectTests.cs`

新布局下若 host csproj 本身位于中间树根（`SourcePath` 直接是项目目录的场景，如 `LibProject_AllUnsignedTypes` 测试），SDK 默认 `**/*.cs` glob 会把 `Cs2jUnsignedMath/*.cs` 编进 host：多编一份 helper，且 host `LangVersion < 11` 时 `>>>` 直接编译失败——§1 的 LangVersion 泄漏在该场景原样回归。处理：显式 `<Compile Remove>`。`SampleExplicitCompileLib`（`EnableDefaultCompileItems=false`）不受影响，不追加 Remove。

- [ ] **Step 1: 加失败断言**

`LibProject_AllUnsignedTypes_RewrittenCorrectly` 中，在 Task 3 加入的 `Assert.DoesNotContain("_Cs2jUnsignedMath.cs", libCsprojContent);` 之后追加：

```csharp
            Assert.Contains(
                "<Compile Remove=\"Cs2jUnsignedMath\\**\" />",
                libCsprojContent);
```

- [ ] **Step 2: 运行确认失败**

```bash
dotnet test tests/CSharpToJava.ProjectTests/CSharpToJava.ProjectTests.csproj --filter "FullyQualifiedName~LibProject_AllUnsignedTypes"
```

期望：FAIL，csproj 无 Compile Remove。

- [ ] **Step 3: 实现特判**

`ProjectUnsignedTypePreprocessor.cs` 的 `EnsureExtensionProjectReference` 中，在移除旧 ProjectReference 的 foreach 之前插入：

```csharp
        // 根目录 host 特判：host csproj 本身位于中间树根时，Cs2jUnsignedMath/**
        // 会落入 SDK 默认编译 glob，host 会多编一份 helper（LangVersion < 11
        // 时直接编译失败）。显式排除，ProjectReference 照常注入。
        var isSdkProject = root.Attribute("Sdk") != null;
        var enableDefaultCompileItems = GetPropertyValue(root, "EnableDefaultCompileItems");
        if (isSdkProject
            && !string.Equals(enableDefaultCompileItems?.Trim(), "false", StringComparison.OrdinalIgnoreCase)
            && PathsEqual(projectDirectory, intermediateRoot))
        {
            var removePattern = Path.Combine(UnsignedMathSupportFiles.LibDirectoryName, "**");
            if (!root.Descendants().Any(e =>
                    e.Name.LocalName == "Compile"
                    && string.Equals((string?)e.Attribute("Remove"), removePattern, StringComparison.OrdinalIgnoreCase)))
            {
                root.Add(
                    new XText(Environment.NewLine + "  "),
                    new XElement("ItemGroup",
                        new XElement("Compile", new XAttribute("Remove", removePattern))),
                    new XText(Environment.NewLine));
            }
        }
```

- [ ] **Step 4: 运行确认通过**

```bash
dotnet test tests/CSharpToJava.ProjectTests/CSharpToJava.ProjectTests.csproj --filter "FullyQualifiedName~UnsignedTypeProjectTests"
```

期望：`Passed! 6`。

- [ ] **Step 5: 提交**

```bash
git add src/CSharpToJava.CLI/ProjectUnsignedTypePreprocessor.cs tests/CSharpToJava.ProjectTests/UnsignedTypeProjectTests.cs
git commit -m "fix: exclude Cs2jUnsignedMath from root host default compile glob"
```

---

### Task 5: 名称碰撞 fail-fast + host TFM 告警

**Files:**
- Modify: `src/CSharpToJava.CLI/ProjectUnsignedTypePreprocessor.cs`
- Create: `tests/SampleUnsignedSolution/SampleNameCollision/Cs2jUnsignedMath.csproj`
- Create: `tests/SampleUnsignedSolution/SampleNameCollision/Collide.cs`
- Test: `tests/CSharpToJava.ProjectTests/UnsignedTypeProjectTests.cs`
- Test: `tests/CSharpToJava.ProjectTests/UnsignedPreprocessHelpersTests.cs`

- [ ] **Step 1: 建碰撞夹具**

`tests/SampleUnsignedSolution/SampleNameCollision/Cs2jUnsignedMath.csproj`：

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
</Project>
```

`tests/SampleUnsignedSolution/SampleNameCollision/Collide.cs`：

```csharp
public static class Collide
{
    public static uint Twice(uint v) => v + v;
}
```

（ProjectTests csproj 已有 `..\SampleUnsignedSolution\**\*.*` 的 Content 复制，自动带上夹具；该目录不在 SampleUnsignedSolution.sln 内，对 sln 布局不可见、互不干扰。）

- [ ] **Step 2: 写失败测试**

(a) `tests/CSharpToJava.ProjectTests/UnsignedTypeProjectTests.cs` 追加测试：

```csharp
    [Fact]
    public async Task NameCollision_FailsFastBeforeReleasing()
    {
        var dir = Path.Combine(FixtureDir, "SampleNameCollision");
        Assert.True(Directory.Exists(dir), $"Fixture not found: {dir}");
        var outputDir = Path.Combine(dir, "..", ".test-output-collision");

        try
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                ProjectUnsignedTypePreprocessor.PreprocessAsync(
                    new ProjectUnsignedTypePreprocessRequest
                    {
                        SourcePath = dir,
                        DestinationRoot = outputDir,
                        Verbose = false,
                    }));
            Assert.Contains("Cs2jUnsignedMath.csproj", ex.Message);
        }
        finally
        {
            if (Directory.Exists(outputDir))
            {
                Directory.Delete(outputDir, recursive: true);
            }
        }
    }
```

(b) 新建 `tests/CSharpToJava.ProjectTests/UnsignedPreprocessHelpersTests.cs`：

```csharp
using CSharpToJava.CLI;

namespace CSharpToJava.ProjectTests;

public class UnsignedPreprocessHelpersTests
{
    [Theory]
    [InlineData("net10.0", true)]
    [InlineData("net11.0", true)]
    [InlineData("net10.0-windows", true)]
    [InlineData("net9.0", false)]
    [InlineData("net8.0", false)]
    [InlineData("netcoreapp3.1", false)]
    [InlineData("netstandard2.0", false)]
    [InlineData("net472", false)]
    [InlineData(null, false)]
    [InlineData("", false)]
    public void IsTargetFrameworkAtLeastNet10_Classifies(string? targetFramework, bool expected)
    {
        Assert.Equal(expected, ProjectUnsignedTypePreprocessor.IsTargetFrameworkAtLeastNet10(targetFramework));
    }
}
```

- [ ] **Step 3: 运行确认失败**

```bash
dotnet test tests/CSharpToJava.ProjectTests/CSharpToJava.ProjectTests.csproj --filter "FullyQualifiedName~NameCollision|FullyQualifiedName~UnsignedPreprocessHelpersTests"
```

期望：碰撞测试 FAIL（未抛异常）；TFM 测试编译失败（方法不存在）。

- [ ] **Step 4: 实现碰撞检测 + TFM 告警**

`ProjectUnsignedTypePreprocessor.cs`：

(a) `WriteExtensionFilesAsync` 开头（`ReleaseAsync` 之前）插入：

```csharp
        ThrowOnCs2jUnsignedMathNameCollision(layout);
```

(b) 新增三个方法（放在 `WriteExtensionFilesAsync` 之后）：

```csharp
    /// <summary>
    /// 用户解决方案中已有保留名项目时立即终止，不静默误识别
    /// （契约见 2026-08-15 设计文档 §4.3）。
    /// </summary>
    private static void ThrowOnCs2jUnsignedMathNameCollision(ProjectPreprocessSourceLayout layout)
    {
        var collision = layout.ProjectFiles.FirstOrDefault(Cs2jUnsignedMathProject.IsCs2jUnsignedMathProject);
        if (collision != null)
        {
            throw new InvalidOperationException(
                $"The source solution already contains a project named " +
                $"'{Cs2jUnsignedMathProject.ProjectFileName}' ({collision}). That name is reserved " +
                "for the cs2j unsigned math support library; rename the project and retry.");
        }
    }

    /// <summary>
    /// host TFM 低于 net10.0 时构建前告警（NU1201 会推迟到用户 dotnet build
    /// 中间树时才暴露，这里提前给出两条出路）。
    /// </summary>
    private static void WarnWhenTargetFrameworkBelowNet10(
        XElement root,
        string projectFilePath,
        List<UnsignedTypeRewriterDiagnostic> diagnostics)
    {
        var tfm = GetFirstTargetFramework(root);
        if (IsTargetFrameworkAtLeastNet10(tfm))
        {
            return;
        }

        diagnostics.Add(new UnsignedTypeRewriterDiagnostic(
            UnsignedTypeSeverity.Warning,
            $"{projectFilePath}: target framework '{tfm ?? "(none)"}' is below net10.0; the injected " +
            $"reference to {Cs2jUnsignedMathProject.ProjectFileName} (net10.0) will fail to restore/build " +
            "with NU1201. Upgrade the host TargetFramework to net10.0+, or report the case so " +
            "Cs2jUnsignedMath can multi-target."));
    }

    internal static string? GetFirstTargetFramework(XElement root)
    {
        var frameworks = GetPropertyValue(root, "TargetFrameworks");
        if (!string.IsNullOrWhiteSpace(frameworks))
        {
            return frameworks!.Split(';', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim();
        }

        return GetPropertyValue(root, "TargetFramework")?.Trim();
    }

    internal static bool IsTargetFrameworkAtLeastNet10(string? targetFramework)
    {
        if (string.IsNullOrWhiteSpace(targetFramework))
        {
            return false;
        }

        var match = System.Text.RegularExpressions.Regex.Match(
            targetFramework,
            @"^net(?:coreapp)?(\d+(?:\.\d+)?)(?:-[a-z0-9.]+)?$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (!match.Success)
        {
            return false;
        }

        return double.TryParse(
            match.Groups[1].Value,
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture,
            out var version) && version >= 10.0;
    }
```

（`net10.0-windows` 后缀放行——它可正常引用 net10.0 库，不告警。）

(c) `EnsureExtensionProjectReference` 末尾（`document.Save` 之前）追加：

```csharp
        WarnWhenTargetFrameworkBelowNet10(root, projectFilePath, diagnostics);
```

- [ ] **Step 5: 运行确认通过**

```bash
dotnet test tests/CSharpToJava.ProjectTests/CSharpToJava.ProjectTests.csproj --filter "FullyQualifiedName~UnsignedTypeProjectTests|FullyQualifiedName~UnsignedPreprocessHelpersTests"
```

期望：`Passed!`（7 + 10 全绿；TFM 告警对 net10.0 夹具不触发，现有 Success 断言不受影响）。

- [ ] **Step 6: 提交**

```bash
git add src/CSharpToJava.CLI/ProjectUnsignedTypePreprocessor.cs tests/CSharpToJava.ProjectTests/UnsignedTypeProjectTests.cs tests/CSharpToJava.ProjectTests/UnsignedPreprocessHelpersTests.cs tests/SampleUnsignedSolution/SampleNameCollision/
git commit -m "feat: name-collision fail-fast + host TFM warning in unsigned preprocessor"
```

---

### Task 6: 单文件模式切换 + 删除 `UnsignedMathFileContent` + 单元/语义测试改造

**Files:**
- Modify: `src/CSharpToJava.CLI/Program.cs`（`:3723-3726` 单文件写支撑文件处）
- Delete: `src/CSharpToJava.Core/UnsignedTypeRewriter/UnsignedMathFileContent.cs`
- Modify: `tests/CSharpToJava.ProjectTests/CSharpToJava.ProjectTests.csproj`
- Modify: `tests/CSharpToJava.ProjectTests/UnsignedTypeRewriterUnitTests.cs`（`:29-38`、`:534-544`）
- Modify: `tests/CSharpToJava.ProjectTests/UnsignedTypeSemanticsTests.cs`（`:46`、`:50-51`）

单文件语义不变：有 `-o` 时写一份 `_Cs2jUnsignedMath.cs`（前缀契约保留）到输出同目录；内容改从嵌入资源读（public 版真源，对自包含场景无影响）；不释放 csproj。

- [ ] **Step 1: 改造单元测试断言（先失败）**

`tests/CSharpToJava.ProjectTests/UnsignedTypeRewriterUnitTests.cs`：

(a) 类内加真源定位属性：

```csharp
    private static string HelperSource { get; } = File.ReadAllText(Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", "..",
        "src", "Cs2jUnsignedMath", "UnsignedMath.cs")));
```

（BaseDirectory = `tests/CSharpToJava.ProjectTests/bin/Debug/net10.0`，5 级向上到仓库根。）

(b) `HelperFile_IsInternalUnsignedMathWithBlockNamespace`（`:29-38`）整体替换为：

```csharp
    [Fact]
    public void HelperFile_IsPublicUnsignedMathWithBlockNamespace()
    {
        var content = HelperSource;
        Assert.Contains("namespace Cs2jUnsigned", content);
        Assert.Contains("public static class UnsignedMath", content);
        Assert.DoesNotContain("internal static class UnsignedMath", content);
        Assert.Contains("public static int AddUInt32(int a, int b)", content);
        Assert.Contains("public static long ToInt64UInt32(int v)", content);
        Assert.DoesNotContain("public static class UnsignedExtensions", content);
    }
```

(c) `HelperFile_UsesConverterSafeUnsignedPatterns`（`:534-544`）中 `var content = UnsignedMathFileContent.Generate();` 改为 `var content = HelperSource;`，断言不变：

```csharp
    [Fact]
    public void HelperFile_UsesConverterSafeUnsignedPatterns()
    {
        var content = HelperSource;
        // (ulong) 强转算术会被 Java 转换器擦成有符号,必须换成 >>> / JDK 算法。
        Assert.DoesNotContain("(ulong)a / (ulong)b", content);
        Assert.DoesNotContain("(ulong)a % (ulong)b", content);
        Assert.DoesNotContain("(ulong)a >>", content);
        Assert.Contains("a >>> (n & 63)", content);
        Assert.Contains("double.Parse(((ulong)v).ToString", content);
    }
```

- [ ] **Step 2: 改造语义测试（先失败）**

(a) `tests/CSharpToJava.ProjectTests/CSharpToJava.ProjectTests.csproj`：在现有两个 ProjectReference 的 ItemGroup 里追加一行：

```xml
    <ProjectReference Include="..\..\src\Cs2jUnsignedMath\Cs2jUnsignedMath.csproj" />
```

(b) `tests/CSharpToJava.ProjectTests/UnsignedTypeSemanticsTests.cs`：

删除 `:46` 的

```csharp
        rewrittenSources.Add(UnsignedMathFileContent.Generate());
```

并把 `:50-51` 的

```csharp
        var originalOutput = CompileAndRun(sources.Select(s => s.Code), references, "unsigned-semantics-original");
        var rewrittenOutput = CompileAndRun(rewrittenSources, references, "unsigned-semantics-rewritten");
```

替换为（helper 类型改由引用程序集提供，public 由此成为硬需求）：

```csharp
        var helperReference = MetadataReference.CreateFromFile(typeof(Cs2jUnsigned.UnsignedMath).Assembly.Location);
        var originalOutput = CompileAndRun(sources.Select(s => s.Code), references, "unsigned-semantics-original");
        var rewrittenOutput = CompileAndRun(rewrittenSources, references.Append(helperReference), "unsigned-semantics-rewritten");
```

- [ ] **Step 3: 运行改造后测试（守护基线）**

```bash
dotnet test tests/CSharpToJava.ProjectTests/CSharpToJava.ProjectTests.csproj --filter "FullyQualifiedName~HelperFile|FullyQualifiedName~RewrittenCode_ProducesSameRuntimeResults"
```

期望：`Passed! 3`。说明：真源已由 Task 1 提供、测试 csproj 引用已就位，此时测试即已转绿——本任务是机械切换而非新增行为，先行测试的意义是**保证后续删除 `UnsignedMathFileContent` 时没有测试再依赖它**（若此步红了，说明 Step 1/2 改漏了引用点，先修再继续）。

- [ ] **Step 4: 切换 Program.cs 单文件模式并删除常量类**

(a) `src/CSharpToJava.CLI/Program.cs` `:3723-3726`：

```csharp
                    var outputDir = Path.GetDirectoryName(Path.GetFullPath(opts.Output)) ?? ".";
                    var extensionPath = Path.Combine(outputDir, UnsignedMathFileContent.FileName);
                    await File.WriteAllTextAsync(extensionPath, UnsignedMathFileContent.Generate(), new System.Text.UTF8Encoding(false));
```

替换为：

```csharp
                    var outputDir = Path.GetDirectoryName(Path.GetFullPath(opts.Output)) ?? ".";
                    var extensionPath = Path.Combine(outputDir, UnsignedMathSupportFiles.SingleFileHelperFileName);
                    await File.WriteAllTextAsync(extensionPath, UnsignedMathSupportFiles.GetUnsignedMathFileContent(), new System.Text.UTF8Encoding(false));
```

(b) 删除 `src/CSharpToJava.Core/UnsignedTypeRewriter/UnsignedMathFileContent.cs`。此时其全部 5 组引用点均已改造：预处理器（Task 3）、Program.cs（本步）、两个测试文件（本任务 Step 1/2）。

- [ ] **Step 5: 运行确认通过 + 全量项目测试**

```bash
dotnet test tests/CSharpToJava.ProjectTests/CSharpToJava.ProjectTests.csproj --filter "FullyQualifiedName~Unsigned"
dotnet test tests/CSharpToJava.ProjectTests/CSharpToJava.ProjectTests.csproj
```

期望：第一条 `Passed!`；第二条全绿（语义测试两版输出仍逐字节一致）。

- [ ] **Step 6: 单文件模式手工冒烟**

```bash
dotnet build src/CSharpToJava.CLI/CSharpToJava.CLI.csproj -v q --nologo
rm -rf tmp-probe/single && mkdir -p tmp-probe/single
dotnet src/CSharpToJava.CLI/bin/Debug/net10.0/CSharpToJava.CLI.dll unsigned-type -i tests/SampleUnsignedSolution/SampleUnsignedLib/UnsignedOperators.cs -o tmp-probe/single/UnsignedOperators.cs
ls tmp-probe/single
grep -c "public static class UnsignedMath" tmp-probe/single/_Cs2jUnsignedMath.cs
```

期望：目录含 `UnsignedOperators.cs` 与 `_Cs2jUnsignedMath.cs` 两文件；grep 输出 `1`。确认后 `rm -rf tmp-probe/single`。

- [ ] **Step 7: 提交**

```bash
git add src/CSharpToJava.CLI/Program.cs src/CSharpToJava.Core/UnsignedTypeRewriter/UnsignedMathFileContent.cs tests/CSharpToJava.ProjectTests/CSharpToJava.ProjectTests.csproj tests/CSharpToJava.ProjectTests/UnsignedTypeRewriterUnitTests.cs tests/CSharpToJava.ProjectTests/UnsignedTypeSemanticsTests.cs
git commit -m "refactor: retire UnsignedMathFileContent; single-file mode uses embedded true source"
```

（`git add` 对删除文件同样生效，记录删除。）

---

### Task 7: 主转换器双侧过滤 + `DiscoverImplicitModuleDependencies` 注释更新

**Files:**
- Modify: `src/CSharpToJava.CLI/MultiModulePlanner.cs:33-35`
- Modify: `src/CSharpToJava.CLI/Program.cs:506-508`（workspace 路径）、`:3273-3275`、`:3370-3373`（注释）
- Test: `tests/CSharpToJava.ProjectTests/MultiModulePlannerTests.cs`

主转换默认走 MSBuild workspace 路径（`Program.cs:493-529`）；MSBuildWorkspace 会沿 sln 外 ProjectReference 加载 Cs2jUnsignedMath（前置事实 1），所以 workspace 路径需要过滤；graph 回退路径的过滤在 `MultiModulePlanner.Build`。两处过滤后，host→lib 的引用边在依赖收集处自然落空（workspace 路径 `Program.cs:1557-1562` 的 `FirstOrDefault` 找不到即跳过；graph 路径 `MultiModulePlanner.cs:62-68` 的 `TryGetValue` 落空），无需任何删除逻辑，pom 不出现该依赖。TypeScript 路径（`Program.cs:673` 一带）不经 unsigned 预处理，无 Cs2jUnsignedMath 引用边，不改（spec §3）。

- [ ] **Step 1: 写失败测试**

新建 `tests/CSharpToJava.ProjectTests/MultiModulePlannerTests.cs`：

```csharp
using CSharpToJava.CLI;

namespace CSharpToJava.ProjectTests;

public class MultiModulePlannerTests
{
    private static DiscoveredProject MakeProject(
        string name, string path, params string[] references) => new()
    {
        Name = name,
        ProjectFilePath = path,
        ProjectDirectory = Path.GetDirectoryName(path)!,
        Kind = ProjectKind.Production,
        ProjectReferences = references,
        ResourceItems = [],
    };

    [Fact]
    public void Build_FiltersCs2jUnsignedMath_AndDropsItsDependencyEdges()
    {
        var graph = new ProjectGraph
        {
            RootProjectPath = @"C:\repo\App\App.csproj",
            ProjectsInTopologicalOrder =
            [
                MakeProject(
                    "Cs2jUnsignedMath",
                    @"C:\repo\App\Cs2jUnsignedMath\Cs2jUnsignedMath.csproj"),
                MakeProject(
                    "SampleUnsignedLib",
                    @"C:\repo\App\SampleUnsignedLib\SampleUnsignedLib.csproj",
                    @"C:\repo\App\Cs2jUnsignedMath\Cs2jUnsignedMath.csproj"),
                MakeProject(
                    "App",
                    @"C:\repo\App\App.csproj",
                    @"C:\repo\App\SampleUnsignedLib\SampleUnsignedLib.csproj",
                    @"C:\repo\App\Cs2jUnsignedMath\Cs2jUnsignedMath.csproj"),
            ],
        };

        var plan = MultiModulePlanner.Build(graph, includeTests: false);

        Assert.Equal(2, plan.ModulesInBuildOrder.Count);
        var app = plan.ModulesInBuildOrder.Single(m => m.Name == "app");
        var lib = plan.ModulesInBuildOrder.Single(m => m.Name == "sampleunsignedlib");
        Assert.Equal("sampleunsignedlib", string.Join(",", app.CompileDependencies));
        Assert.Empty(lib.CompileDependencies);
    }
}
```

- [ ] **Step 2: 运行确认失败**

```bash
dotnet test tests/CSharpToJava.ProjectTests/CSharpToJava.ProjectTests.csproj --filter "FullyQualifiedName~MultiModulePlannerTests"
```

期望：FAIL——3 个模块，`cs2junsignedmath` 模块出现、依赖边存在。

- [ ] **Step 3: 实现 planner 过滤**

`src/CSharpToJava.CLI/MultiModulePlanner.cs:33-35`：

```csharp
        var productionProjects = graph.ProjectsInTopologicalOrder
            .Where(p => p.Kind == ProjectKind.Production)
            .ToList();
```

替换为（拓扑排序之后、模块命名之前过滤）：

```csharp
        var productionProjects = graph.ProjectsInTopologicalOrder
            .Where(p => p.Kind == ProjectKind.Production)
            .Where(p => !Cs2jUnsignedMathProject.IsCs2jUnsignedMathProject(p.ProjectFilePath))
            .ToList();
```

- [ ] **Step 4: workspace 路径过滤**

`src/CSharpToJava.CLI/Program.cs`（`:506` 一带）：

```csharp
            if (workspaceProjects != null && workspaceProjects.Count > 0)
            {
                workspaceProjects = NormalizeWorkspaceProjects(workspaceProjects, opts.Verbose);
```

替换为：

```csharp
            if (workspaceProjects != null && workspaceProjects.Count > 0)
            {
                workspaceProjects = NormalizeWorkspaceProjects(workspaceProjects, opts.Verbose);
                // Cs2jUnsignedMath 是 unsigned-type 预处理注入的内部支撑库：
                // 仅作符号来源，不为其规划 Maven 模块、不转换其源文件。
                // 放在指纹快照之前——helper 文件不再进入 Java 输出，不应参与缓存键。
                workspaceProjects = workspaceProjects
                    .Where(p => !Cs2jUnsignedMathProject.IsCs2jUnsignedMathProject(p.FilePath))
                    .ToList();
```

（只改 Java 目标的加载路径 `:506`；TypeScript 目标 `:673` 处不动。）

- [ ] **Step 5: 更新两处失效注释**

`Program.cs:3273-3275`：

```csharp
        // Multiple modules can share the same package (e.g. "Cs2jUnsigned"
        // is used by system-private-uri and system-text-json).
```

替换为：

```csharp
        // Multiple modules can share the same package (e.g. a framework
        // package declared by several converted modules).
```

`Program.cs:3370-3373`（wildcard 豁免注释）：

```csharp
                            // NOT declare the package. This avoids spurious
                            // dependencies between modules that happen to share
                            // the same package (e.g. Cs2jUnsigned is declared in
                            // system-private-uri, system-text-json, etc.).
```

替换为：

```csharp
                            // NOT declare the package. This avoids spurious
                            // dependencies between modules that happen to share
                            // the same package.
```

（`DiscoverImplicitModuleDependencies` 的"本模块自声明包则跳过依赖"豁免是通用机制，与 Cs2jUnsigned 无字符串耦合，逻辑保留不动——spec §5.2。）

- [ ] **Step 6: 运行确认通过**

```bash
dotnet test tests/CSharpToJava.ProjectTests/CSharpToJava.ProjectTests.csproj --filter "FullyQualifiedName~MultiModulePlannerTests"
```

期望：`Passed! 1`。

- [ ] **Step 7: 提交**

```bash
git add src/CSharpToJava.CLI/MultiModulePlanner.cs src/CSharpToJava.CLI/Program.cs tests/CSharpToJava.ProjectTests/MultiModulePlannerTests.cs
git commit -m "feat: skip Cs2jUnsignedMath in module planning and workspace conversion"
```

---

### Task 8: compat 手写 `UnsignedMath` + JUnit 对拍 + `mvn install`

**Files:**
- Create: `java/csharptojava-compat/src/main/java/io/github/ningpp/compat/UnsignedMath.java`
- Test: `java/csharptojava-compat/src/test/java/io/github/ningpp/compat/UnsignedMathTest.java`

实现口径：**C# 版方法全集实现**（含改写器从不调用的 `NotInt32`/`LeftShiftInt32`/`RightShiftInt32`/`CompareInt32` 等 Int32 域），**按改写器调用闭集验收**（§6.2 清单即验收基线，亦为该类 public API 基线）。方法名 camelCase（前置事实 2）；签名的 Java 类型对齐旧转换产物：byte/ushort 域返回 `int`、u64 域 `long`、decimal 返回 compat `Decimal`、provider 参数为 compat `IFormatProvider`。ToString 带格式重载委托 `MathHelper.formatNumeric`（UInt64 经 `BigInteger` 传无符号幅值，避免旧产物 `Long.toString` 的有符号缺陷——前置事实 3）；provider 恒按 Invariant 语义（spec §6.2）。

- [ ] **Step 1: 写失败 JUnit 测试**

新建 `java/csharptojava-compat/src/test/java/io/github/ningpp/compat/UnsignedMathTest.java`：

```java
package io.github.ningpp.compat;

import org.junit.jupiter.api.Test;

import static org.junit.jupiter.api.Assertions.*;

class UnsignedMathTest {

    private static final int[] INT_EDGE = {
        0, 1, -1, Integer.MIN_VALUE, Integer.MAX_VALUE, Integer.MAX_VALUE / 2, 0x12345678, -0x12345678};
    private static final long[] LONG_EDGE = {
        0L, 1L, -1L, Long.MIN_VALUE, Long.MAX_VALUE, Long.MAX_VALUE / 2L, 0x123456789ABCDEFL, -0x123456789ABCDEFL};

    // ---- 改写器调用闭集：算术/位运算（补码回绕 == C# unchecked） ----

    @Test
    void arithmeticUInt32_matchesWraparoundSemantics() {
        assertEquals(-2, UnsignedMath.addUInt32(Integer.MAX_VALUE, Integer.MAX_VALUE));
        assertEquals(0, UnsignedMath.subtractUInt32(Integer.MIN_VALUE, Integer.MIN_VALUE));
        assertEquals(1, UnsignedMath.multiplyUInt32(Integer.MIN_VALUE, Integer.MIN_VALUE));
        assertEquals(-4, UnsignedMath.xorUInt32(-1, 3));
        assertEquals(0x00F0, UnsignedMath.andUInt32(0xF0F0, 0x0FF0));
        assertEquals(-1, UnsignedMath.orUInt32(-8, 7));
        assertEquals(0, UnsignedMath.notUInt32(-1));
    }

    @Test
    void arithmeticUInt64_matchesWraparoundSemantics() {
        assertEquals(-2L, UnsignedMath.addUInt64(Long.MAX_VALUE, Long.MAX_VALUE));
        assertEquals(1L, UnsignedMath.multiplyUInt64(Long.MIN_VALUE, Long.MIN_VALUE));
        assertEquals(~(-1L), UnsignedMath.notUInt64(-1L));
        assertEquals(-1L, UnsignedMath.orUInt64(-8L, 7L));
    }

    @Test
    void divideRemainderUInt32_matchesJdkReference() {
        int[] divisors = {1, 2, 3, 17, Integer.MAX_VALUE, -1, -2, Integer.MIN_VALUE};
        for (int a : INT_EDGE) {
            for (int b : divisors) {
                assertEquals(Integer.divideUnsigned(a, b), UnsignedMath.divideUInt32(a, b),
                        "divide(" + a + ", " + b + ")");
                assertEquals(Integer.remainderUnsigned(a, b), UnsignedMath.remainderUInt32(a, b),
                        "remainder(" + a + ", " + b + ")");
            }
        }
    }

    @Test
    void divideRemainderUInt64_matchesJdkReference() {
        long[] divisors = {1L, 2L, 3L, 17L, Long.MAX_VALUE, -1L, -2L, Long.MIN_VALUE};
        for (long a : LONG_EDGE) {
            for (long b : divisors) {
                assertEquals(Long.divideUnsigned(a, b), UnsignedMath.divideUInt64(a, b),
                        "divide(" + a + ", " + b + ")");
                assertEquals(Long.remainderUnsigned(a, b), UnsignedMath.remainderUInt64(a, b),
                        "remainder(" + a + ", " + b + ")");
            }
        }
    }

    // ---- 比较 ----

    @Test
    void compare_matchesJdkReference() {
        for (int a : INT_EDGE) {
            for (int b : INT_EDGE) {
                assertEquals(Integer.compareUnsigned(a, b), UnsignedMath.compareUInt32(a, b));
            }
        }
        for (long a : LONG_EDGE) {
            for (long b : LONG_EDGE) {
                assertEquals(Long.compareUnsigned(a, b), UnsignedMath.compareUInt64(a, b));
                assertEquals(Long.compare(a, b), UnsignedMath.compareInt64(a, b));
            }
        }
    }

    // ---- 转换（与 C# 版逐方法对齐） ----

    @Test
    void conversions_matchCSharpSemantics() {
        assertEquals(0xFF, UnsignedMath.toByteInt32(0x1234ABFF));
        assertEquals(0xFF, UnsignedMath.toByteInt64(0x12345678ABCDEFFL));
        assertEquals(0xCDEF, UnsignedMath.toUInt16Int32(0x1234CDEF));
        assertEquals(0xCDEF, UnsignedMath.toUInt16Int64(0x12345678ABCDEFL));
        assertEquals(4294967295L, UnsignedMath.toInt64UInt32(-1));
        assertEquals(4294967295L, UnsignedMath.toUInt64UInt32(-1));
        assertEquals(-1, UnsignedMath.toInt32UInt64(-1L));
        assertEquals(4294967295.0, UnsignedMath.toDoubleUInt32(-1), 0.0);
        assertEquals(1.8446744073709552E19, UnsignedMath.toDoubleUInt64(-1L), 0.0);
        assertEquals(4.2949673E9f, UnsignedMath.toSingleUInt32(-1), 0.0f);
        assertEquals(1.8446744E19f, UnsignedMath.toSingleUInt64(-1L), 0.0f);
        assertEquals(Decimal.parse("4294967295"), UnsignedMath.toDecimalUInt32(-1));
        assertEquals(Decimal.parse("18446744073709551615"), UnsignedMath.toDecimalUInt64(-1L));
    }

    // ---- ToString（unsigned） ----

    @Test
    void toStringUnsigned_plainOverloads() {
        assertEquals("0", UnsignedMath.toStringUInt32(0));
        assertEquals("4294967295", UnsignedMath.toStringUInt32(-1));
        assertEquals("2147483647", UnsignedMath.toStringUInt32(Integer.MAX_VALUE));
        assertEquals("18446744073709551615", UnsignedMath.toStringUInt64(-1L));
        assertEquals("0", UnsignedMath.toStringUInt64(0L));
        // provider 被忽略，恒 InvariantCulture（与 C# 版一致）
        assertEquals("4294967295", UnsignedMath.toStringUInt32(-1, CultureInfo.getInvariantCulture()));
        assertEquals("18446744073709551615", UnsignedMath.toStringUInt64(-1L, CultureInfo.getInvariantCulture()));
    }

    @Test
    void toStringUInt32_formatOverloads() {
        assertEquals("FF", UnsignedMath.toStringUInt32(255, "X"));
        assertEquals("00ff", UnsignedMath.toStringUInt32(255, "x4"));
        assertEquals("00012345", UnsignedMath.toStringUInt32(12345, "D8"));
        assertEquals("4294967295", UnsignedMath.toStringUInt32(-1, ""));
        assertEquals("4294967295", UnsignedMath.toStringUInt32(-1, (String) null));
        // 带格式 + provider：provider 恒 Invariant
        assertEquals("FF", UnsignedMath.toStringUInt32(255, "X", CultureInfo.getInvariantCulture()));
        assertEquals("FFFFFFFF", UnsignedMath.toStringUInt32(-1, "X", null));
    }

    @Test
    void toStringUInt64_formatOverloads() {
        assertEquals("FFFFFFFFFFFFFFFF", UnsignedMath.toStringUInt64(-1L, "X"));
        assertEquals("ffffffffffffffff", UnsignedMath.toStringUInt64(-1L, "x"));
        assertEquals("00000000000000ff", UnsignedMath.toStringUInt64(255L, "x16"));
        assertEquals("001", UnsignedMath.toStringUInt64(1L, "D3"));
        assertEquals("18446744073709551615", UnsignedMath.toStringUInt64(-1L, ""));
        assertEquals("FFFFFFFFFFFFFFFF", UnsignedMath.toStringUInt64(-1L, "X", CultureInfo.getInvariantCulture()));
    }

    // ---- 闭集外但全集口径内的对称方法（编译冒烟 + 基本行为） ----

    @Test
    void int32Domain_andShifts_compileAndBehave() {
        assertEquals(-2, UnsignedMath.addInt32(Integer.MAX_VALUE, Integer.MAX_VALUE));
        assertEquals(2, UnsignedMath.subtractInt32(Integer.MAX_VALUE, Integer.MAX_VALUE - 2));
        assertEquals(-1, UnsignedMath.notInt32(0));
        assertEquals(1 << 5, UnsignedMath.leftShiftInt32(1, 5));
        assertEquals(-16 >> 2, UnsignedMath.rightShiftInt32(-16, 2));
        assertEquals(Integer.compare(-3, 2), UnsignedMath.compareInt32(-3, 2));
        assertEquals(-1 >>> 3, UnsignedMath.rightShiftUInt32(-1, 3));
        assertEquals(-1L >>> 63, UnsignedMath.rightShiftUInt64(-1L, 63));
        assertEquals(1L << 62, UnsignedMath.leftShiftUInt64(1L, 62));
    }

    @Test
    void int64Domain_signedOps_behave() {
        assertEquals(-2L, UnsignedMath.addInt64(Long.MAX_VALUE, Long.MAX_VALUE));
        assertEquals(6L, UnsignedMath.multiplyInt64(-3L, -2L));
        assertEquals(-3L / 2L, UnsignedMath.divideInt64(-3L, 2L));
        assertEquals(-3L % 2L, UnsignedMath.remainderInt64(-3L, 2L));
        assertEquals(0xFL, UnsignedMath.andInt64(0xFFL, 0x0FL));
        assertEquals(0xF0L, UnsignedMath.orInt64(0xF0L, 0x00L));
        assertEquals(0xFFL ^ 0x0FL, UnsignedMath.xorInt64(0xFFL, 0x0FL));
    }
}
```

- [ ] **Step 2: 运行确认失败**

```bash
mvn -f java/csharptojava-compat/pom.xml test -Dtest=UnsignedMathTest
```

期望：编译失败，`UnsignedMath` 不存在。

- [ ] **Step 3: 实现手写类**

新建 `java/csharptojava-compat/src/main/java/io/github/ningpp/compat/UnsignedMath.java`：

```java
package io.github.ningpp.compat;

import java.math.BigInteger;

/**
 * C# 无符号数学辅助类 Cs2jUnsigned.UnsignedMath 的手写对应物（TypeMappings
 * 把调用点路由到本类）。方法全集与 C# 版同名同参数个数（camelCase 命名与
 * 转换器生成调用一致），直接委托 JDK unsigned API。仅服务改写调用点的静态
 * 数学运算，不承担 UInt/ULong/UIntPtr 的类型替换职责。
 */
public final class UnsignedMath {
    private UnsignedMath() {
    }

    // ---- Int32 domain ----
    public static int addInt32(int a, int b) { return a + b; }
    public static int subtractInt32(int a, int b) { return a - b; }
    public static int multiplyInt32(int a, int b) { return a * b; }
    public static int divideInt32(int a, int b) { return a / b; }
    public static int remainderInt32(int a, int b) { return a % b; }
    public static int andInt32(int a, int b) { return a & b; }
    public static int orInt32(int a, int b) { return a | b; }
    public static int xorInt32(int a, int b) { return a ^ b; }
    public static int notInt32(int a) { return ~a; }
    public static int leftShiftInt32(int a, int n) { return a << (n & 31); }
    public static int rightShiftInt32(int a, int n) { return a >> (n & 31); }
    public static int compareInt32(int a, int b) { return Integer.compare(a, b); }

    // ---- Int64 domain ----
    public static long addInt64(long a, long b) { return a + b; }
    public static long subtractInt64(long a, long b) { return a - b; }
    public static long multiplyInt64(long a, long b) { return a * b; }
    public static long divideInt64(long a, long b) { return a / b; }
    public static long remainderInt64(long a, long b) { return a % b; }
    public static long andInt64(long a, long b) { return a & b; }
    public static long orInt64(long a, long b) { return a | b; }
    public static long xorInt64(long a, long b) { return a ^ b; }
    public static int compareInt64(long a, long b) { return Long.compare(a, b); }

    // ---- UInt32 domain ----
    public static int addUInt32(int a, int b) { return a + b; }
    public static int subtractUInt32(int a, int b) { return a - b; }
    public static int multiplyUInt32(int a, int b) { return a * b; }
    public static int divideUInt32(int a, int b) { return Integer.divideUnsigned(a, b); }
    public static int remainderUInt32(int a, int b) { return Integer.remainderUnsigned(a, b); }
    public static int andUInt32(int a, int b) { return a & b; }
    public static int orUInt32(int a, int b) { return a | b; }
    public static int xorUInt32(int a, int b) { return a ^ b; }
    public static int notUInt32(int a) { return ~a; }
    public static int leftShiftUInt32(int a, int n) { return a << (n & 31); }
    public static int rightShiftUInt32(int a, int n) { return a >>> (n & 31); }
    public static int compareUInt32(int a, int b) { return Integer.compareUnsigned(a, b); }

    // ---- UInt64 domain ----
    public static long addUInt64(long a, long b) { return a + b; }
    public static long subtractUInt64(long a, long b) { return a - b; }
    public static long multiplyUInt64(long a, long b) { return a * b; }
    public static long divideUInt64(long a, long b) { return Long.divideUnsigned(a, b); }
    public static long remainderUInt64(long a, long b) { return Long.remainderUnsigned(a, b); }
    public static long andUInt64(long a, long b) { return a & b; }
    public static long orUInt64(long a, long b) { return a | b; }
    public static long xorUInt64(long a, long b) { return a ^ b; }
    public static long notUInt64(long a) { return ~a; }
    public static long leftShiftUInt64(long a, int n) { return a << (n & 63); }
    public static long rightShiftUInt64(long a, int n) { return a >>> (n & 63); }
    public static int compareUInt64(long a, long b) { return Long.compareUnsigned(a, b); }

    // ---- Conversions ----
    public static int toByteInt32(int v) { return v & 0xFF; }
    public static int toByteInt64(long v) { return (int) v & 0xFF; }
    public static int toUInt16Int32(int v) { return v & 0xFFFF; }
    public static int toUInt16Int64(long v) { return (int) v & 0xFFFF; }
    public static long toInt64UInt32(int v) { return v & 0xFFFFFFFFL; }
    public static long toUInt64UInt32(int v) { return v & 0xFFFFFFFFL; }
    public static int toInt32UInt64(long v) { return (int) v; }
    public static double toDoubleUInt32(int v) { return (double) (v & 0xFFFFFFFFL); }
    public static double toDoubleUInt64(long v) { return Double.parseDouble(Long.toUnsignedString(v)); }
    public static float toSingleUInt32(int v) { return (float) (v & 0xFFFFFFFFL); }
    public static float toSingleUInt64(long v) { return Float.parseFloat(Long.toUnsignedString(v)); }
    public static Decimal toDecimalUInt32(int v) { return Decimal.valueOf(v & 0xFFFFFFFFL); }
    public static Decimal toDecimalUInt64(long v) { return Decimal.parse(Long.toUnsignedString(v)); }

    // ---- ToString (unsigned) ----
    public static String toStringUInt32(int v) { return Integer.toUnsignedString(v); }
    public static String toStringUInt32(int v, String format) { return toStringUInt32(v, format, null); }
    public static String toStringUInt32(int v, IFormatProvider provider) { return toStringUInt32(v); }
    public static String toStringUInt32(int v, String format, IFormatProvider provider) {
        if (format == null || format.isEmpty()) {
            return toStringUInt32(v);
        }
        // v & 0xFFFFFFFFL 非负，Long 幅值即无符号值
        return MathHelper.formatNumeric(format, v & 0xFFFFFFFFL);
    }

    public static String toStringUInt64(long v) { return Long.toUnsignedString(v); }
    public static String toStringUInt64(long v, String format) { return toStringUInt64(v, format, null); }
    public static String toStringUInt64(long v, IFormatProvider provider) { return toStringUInt64(v); }
    public static String toStringUInt64(long v, String format, IFormatProvider provider) {
        if (format == null || format.isEmpty()) {
            return toStringUInt64(v);
        }
        // BigInteger 携带完整无符号幅值，X/x/D 格式按 C# ulong 语义输出
        return MathHelper.formatNumeric(format, new BigInteger(Long.toUnsignedString(v)));
    }
}
```

（除零语义：C# 版 `a / b` 自然抛出，`Integer/Long.divideUnsigned` 同样抛 `ArithmeticException`，一致。）

- [ ] **Step 4: 运行确认通过**

```bash
mvn -f java/csharptojava-compat/pom.xml test -Dtest=UnsignedMathTest
```

期望：`Tests run: 11, Failures: 0, Errors: 0`。

- [ ] **Step 5: 全量 compat 回归 + 安装**

```bash
mvn -f java/csharptojava-compat/pom.xml test
mvn -f java/csharptojava-compat/pom.xml install
```

期望：全量测试通过；install 成功（坐标 `io.github.ningpp:csharptojava-compat:1.0-SNAPSHOT`，本机解析走 `D:\mvnrepo`）。生成模块对该 SNAPSHOT 的依赖是既有的默认项（前置事实 5），pom 零改动。

- [ ] **Step 6: 提交**

```bash
git add java/csharptojava-compat/src/main/java/io/github/ningpp/compat/UnsignedMath.java java/csharptojava-compat/src/test/java/io/github/ningpp/compat/UnsignedMathTest.java
git commit -m "feat: hand-written compat UnsignedMath delegating to JDK unsigned APIs"
```

---

### Task 9: TypeMappings 条目 + 端到端转换测试

**Files:**
- Modify: `config/TypeMappings.json`（typeMappings 数组，`System.Text.Json.JsonSerializer` compat 条目之后，约 `:325-333`）
- Test: `tests/CSharpToJava.ProjectTests/UnsignedConversionEndToEndTests.cs`

- [ ] **Step 1: 写端到端测试（先失败）**

新建 `tests/CSharpToJava.ProjectTests/UnsignedConversionEndToEndTests.cs`：

```csharp
using CSharpToJava.CLI;
using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using CSharpToJava.Core.Workspace;

namespace CSharpToJava.ProjectTests;

public class UnsignedConversionEndToEndTests
{
    private static string FixtureDir =>
        Path.Combine(AppContext.BaseDirectory, "SampleUnsignedSolution");

    private static ConversionOptions Options => new()
    {
        TypeMappingConfigPath = Path.Combine(AppContext.BaseDirectory, "config", "TypeMappings.json"),
    };

    [Fact]
    public async Task PreprocessedSolution_ConvertsToJava_WithCompatUnsignedMath()
    {
        var outputDir = Path.Combine(FixtureDir, ".test-output-e2e");

        try
        {
            // 1) 预处理：释放 Cs2jUnsignedMath + 注入 ProjectReference
            var result = await ProjectUnsignedTypePreprocessor.PreprocessAsync(
                new ProjectUnsignedTypePreprocessRequest
                {
                    SourcePath = Path.Combine(FixtureDir, "SampleUnsignedSolution.sln"),
                    DestinationRoot = outputDir,
                    Verbose = false,
                });
            Assert.True(result.Success, string.Join("\n", result.Diagnostics));

            // 2) 主转换加载（workspace 路径）+ 过滤内部支撑库（与 Program.cs 同规则）
            SolutionLoader.EnsureMSBuildRegistered();
            using var loader = new SolutionLoader();
            var workspaceProjects = await loader.TryOpenAsync(result.PreprocessedSourcePath);
            Assert.NotNull(workspaceProjects);
            Assert.True(workspaceProjects.Count > 0);

            // 前置验证锁定：host compilation 必须能解析辅助类型（MSBuildWorkspace
            // 沿 sln 外 ProjectReference 加载 Cs2jUnsignedMath）。
            var libProject = workspaceProjects!.Single(p => p.Name == "SampleUnsignedLib");
            Assert.NotNull(libProject.Compilation.GetTypeByMetadataName("Cs2jUnsigned.UnsignedMath"));

            var convertProjects = workspaceProjects
                .Where(p => !Cs2jUnsignedMathProject.IsCs2jUnsignedMathProject(p.FilePath))
                .ToList();
            Assert.DoesNotContain(convertProjects, p => p.Name == "Cs2jUnsignedMath");

            // 3) 逐项目转换（不带 additionalSemanticProjectPaths——不得把支撑库
            //    源码并入任何转换）
            var pipeline = new ConversionPipeline();
            var converted = new List<ConversionResult>();
            foreach (var wp in convertProjects.Where(p => !p.IsTestProject))
            {
                converted.AddRange(await pipeline.ConvertProjectWithPartialMergeAsync(
                    wp.Directory,
                    Options,
                    projectName: wp.Name,
                    projectFilePath: wp.FilePath,
                    projectReferences: wp.ProjectReferences,
                    isTestProject: wp.IsTestProject));
            }

            Assert.NotEmpty(converted);
            foreach (var r in converted)
            {
                Assert.True(r.Success, $"{r.FileName}: {string.Join("\n", r.Diagnostics)}");
            }

            // 4) 不再生成 Cs2jUnsigned/UnsignedMath.java（任何模块）
            Assert.DoesNotContain(converted, r =>
                string.Equals(r.FileName, "UnsignedMath.java", StringComparison.OrdinalIgnoreCase));

            // 5) 调用点带 compat import、camelCase 方法、无 Cs2jUnsigned 残留
            var operators = converted.Single(r =>
                string.Equals(r.FileName, "UnsignedOperators.java", StringComparison.OrdinalIgnoreCase));
            Assert.Contains("import io.github.ningpp.compat.UnsignedMath;", operators.GeneratedCode);
            Assert.Contains("UnsignedMath.addUInt32(", operators.GeneratedCode);
            Assert.Contains("UnsignedMath.compareUInt32(", operators.GeneratedCode);
            Assert.DoesNotContain("Cs2jUnsigned.UnsignedMath", operators.GeneratedCode);
        }
        finally
        {
            if (Directory.Exists(outputDir))
            {
                Directory.Delete(outputDir, recursive: true);
            }
        }
    }
}
```

- [ ] **Step 2: 运行确认失败**

```bash
dotnet test tests/CSharpToJava.ProjectTests/CSharpToJava.ProjectTests.csproj --filter "FullyQualifiedName~UnsignedConversionEndToEndTests"
```

期望：FAIL——调用点仍是 `Cs2jUnsigned.UnsignedMath.addUInt32(...)`、无 compat import（TypeMappings 条目尚未添加，符号无法映射到 compat 类）。

- [ ] **Step 3: 加 TypeMappings 条目**

`config/TypeMappings.json` 的 `typeMappings` 数组中，找到条目：

```json
        {
            "csharp": "System.Text.Json.JsonSerializer",
            "java": "JsonSerializer",
            "imports": [
                "io.github.ningpp.compat.JsonSerializer"
            ]
        },
```

在其后插入（顶级类全限定名精确匹配，与该条目同形态）：

```json
        {
            "csharp": "Cs2jUnsigned.UnsignedMath",
            "java": "UnsignedMath",
            "imports": [
                "io.github.ningpp.compat.UnsignedMath"
            ]
        },
```

不需要 methodMappings 条目：调用点方法名经转换器统一 camel 化（前置事实 2），与 compat 类 camelCase 方法直接匹配；若端到端测试发现某调用形态未被路由，按闭集补 `(type, method, paramCount)` 显式条目（spec §6.3 兜底）。

- [ ] **Step 4: 运行确认通过**

```bash
dotnet test tests/CSharpToJava.ProjectTests/CSharpToJava.ProjectTests.csproj --filter "FullyQualifiedName~UnsignedConversionEndToEndTests"
```

期望：`Passed! 1`。若方法名断言不匹配（如实际生成为其他形态），以实际生成形态修正 compat 类方法名或断言（camelCase 为既定约定，优先怀疑路由条目）。

- [ ] **Step 5: 提交**

```bash
git add config/TypeMappings.json tests/CSharpToJava.ProjectTests/UnsignedConversionEndToEndTests.cs
git commit -m "feat: map Cs2jUnsigned.UnsignedMath to compat UnsignedMath in TypeMappings"
```

---

### Task 10: 全链路冒烟 + 全量回归

**Files:** 无新增（验证任务；冒烟产物在 tmp 下，用后即删）

- [ ] **Step 1: CLI 全链路冒烟**

```bash
dotnet build src/CSharpToJava.CLI/CSharpToJava.CLI.csproj -v q --nologo
rm -rf tmp-probe/unsigned-final && mkdir -p tmp-probe/unsigned-final
dotnet src/CSharpToJava.CLI/bin/Debug/net10.0/CSharpToJava.CLI.dll unsigned-type \
  -s tests/SampleUnsignedSolution/SampleUnsignedSolution.sln -d tmp-probe/unsigned-final
dotnet src/CSharpToJava.CLI/bin/Debug/net10.0/CSharpToJava.CLI.dll convert-project \
  -s tmp-probe/unsigned-final/.cs2j-unsigned-src/SampleUnsignedSolution.sln \
  -d tmp-probe/unsigned-final/java-out
```

期望：预处理 `24 transformed`；转换 `Conversion complete (MSBuild): 27 succeeded, 0 failed`（与基线一致，前置事实 10）。

- [ ] **Step 2: 产物结构断言（手工检查）**

```bash
ls tmp-probe/unsigned-final/java-out
find tmp-probe/unsigned-final/java-out -name "UnsignedMath.java"
grep -l "io.github.ningpp.compat.UnsignedMath" -r tmp-probe/unsigned-final/java-out --include=*.java | head
ls tmp-probe/unsigned-final/java-out/.cs2j-readonly-src/Cs2jUnsignedMath
```

期望：
- 模块目录只有 `sampleunsignedapp`、`sampleunsignedlib`（无 `cs2junsignedmath` 模块）；
- `find` 无结果（任何模块都不再有 `Cs2jUnsigned/UnsignedMath.java`）；
- `grep` 命中多个生成文件（调用点带 compat import）；
- `.cs2j-readonly-src/Cs2jUnsignedMath` 含 `UnsignedMath.cs` + `Cs2jUnsignedMath.csproj`（后续阶段逐文件全树复制存活、相对路径不变——spec §5.1；tuple 阶段树如生成，同样检查）。

检查后清理：`rm -rf tmp-probe/unsigned-final`。

- [ ] **Step 3: 生成产物可编译验证（spec §8 端到端验收项）**

重新跑 Step 1 的两条命令生成 `java-out` 后执行：

```bash
mvn -f tmp-probe/unsigned-final/java-out/pom.xml compile
```

期望：`BUILD SUCCESS`。依赖（compat SNAPSHOT / vavr / jackson）从本机 `D:\mvnrepo` 解析。说明：基线（改造前）在这一步会因旧转换产物 `UnsignedMath.UnsignedMath.java` 的 `(v & 0xFFFFFFFFL).toString(format, provider)` 非法调用而编译失败——本步通过即证明 spec §8"产物可编译"验收项达成。若本地仓库缺依赖导致解析失败，记录缺失坐标并在计划执行总结中说明（不视为功能失败）。

验证后清理：`rm -rf tmp-probe/unsigned-final`。

- [ ] **Step 4: 全量 C# 回归**

```bash
dotnet test CSharpToJavaConverter.slnx
dotnet test tests/CSharpToJava.ConversionTests/CSharpToJava.ConversionTests.csproj --filter "FullyQualifiedName~Unsigned"
```

期望：slnx 全绿（Core / CLI / Tests / ProjectTests）；ConversionTests 的 Unsigned 过滤集全绿。

- [ ] **Step 5: 收尾提交（如冒烟/回归阶段有任何微调）**

```bash
git status   # 确认无遗漏文件
git add -A && git commit -m "test: full-pipeline smoke for unsigned math lib project"  # 仅有改动时
```

---

## 迁移说明（部署侧，对应 spec §10）

- 使用新版本前需重装 compat：`mvn install -f java/csharptojava-compat/pom.xml`（Task 8 已含）。
- 旧产物需重跑 cs2j 全流水线再生效；旧模块中的 `Cs2jUnsigned/UnsignedMath.java` 不会被自动清理（与新 compat 类不同包、不冲突但成为死代码）；手工修改过的下游 Maven 工程不兼容本变更。

## 风险与回退

- **方法名路由**：依赖转换器统一 camel 化（实测基线 + compat 全库先例）。端到端测试（Task 9）为守门；异常时按闭集补 methodMappings 显式条目即可，不动 C# 侧。
- **MSBuildWorkspace sln 外引用加载**：已探针验证（前置事实 1）并由 Task 9 的 `GetTypeByMetadataName` 断言长期锁定；若未来 Roslyn 行为变化导致该断言失败，fallback 为图加载后显式补载该项目的 compilation（spec §5.2 预案）。
- **TFM 低于 net10.0 的宿主**：构建前告警（Task 5），实际 NU1201 仍会在用户侧暴露；多目标的触发条件即"预处理告警后仍有用户报告 NU1201"。
