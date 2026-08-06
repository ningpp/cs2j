# C# → TypeScript 转换后端 实现计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 在现有 CSharpToJavaConverter 基础上新增 TypeScript 目标后端，将 C# 项目转换为可编译运行的完整 TypeScript 工程（npm workspace）。

**Architecture:** 复用现有 C#→Java 管线前端（Roslyn 解析/语义/检查/去糖/HIR 生成），在 Codegen 层分叉——基于生产 `Java` IR（JavaSyntaxNode 树），新增 `TypeScriptEmitter` 走 Java→TS 二次映射发射 `.ts`；S 级难点（重载/泛型擦除/接口探针/相等）由 TS 专属 IR rewriter 在发射前处理；新增 `csharptotypescript-compat` 兼容运行时 npm 包。Java 路径通过 `TargetLanguage` 枚举分支隔离，零回归。

**Tech Stack:** C# net10.0（转换器端），TypeScript 5.x + Node.js LTS（产物端），xUnit 2.9.3（转换器测试），vitest（兼容库测试），ESM + tsconfig `strict:true`/`target:ES2022`/`module:NodeNext`。

**Spec:** [docs/superpowers/specs/2026-08-06-csharp-to-typescript-design.md](file:///d:/code/cs2j/docs/superpowers/specs/2026-08-06-csharp-to-typescript-design.md)

---

## 公共 API 契约（贯穿全计划，类型名/签名以此为准）

```csharp
namespace CSharpToJava.Core.Context;

public enum TargetLanguage { Java, TypeScript }

// ConversionOptions 新增字段
public TargetLanguage TargetLanguage { get; set; } = TargetLanguage.Java;
public string? TypeScriptMappingConfigPath { get; set; }  // 默认 config/typescript/TypeMappings.json
public string? TypeScriptMemberMappingPath { get; set; }   // 默认 config/typescript/MemberMappings.json

namespace CSharpToJava.Core.TypeScript;

public sealed class TypeScriptEmitter
{
    public string Emit(Java.JavaCompilationUnit unit, Context.ConversionContext context);
    public const string FileExtension = ".ts";
}

public sealed class TsMemberMappingTable
{
    public TsMemberMappingTable(string? typeMappingPath, string? memberMappingPath);
    public bool TryMapType(string csharpOrJavaType, out string tsType, out IReadOnlyList<string> imports);
    public bool TryMapMethod(string ownerType, string methodName, out string tsMethod, out IReadOnlyList<string> imports);
    public bool TryMapMember(string ownerType, string memberName, out string tsMember);  // 属性/字段
}

public static class TsNaming
{
    public string SafeIdentifier(string name);          // TS 保留字处理
    string SafeClassName(string name);
    string OverloadSuffix(IReadOnlyList<string> paramTsTypes);
    string FactoryName(int ordinal);                    // 构造器重载 -> Create$N
}

namespace CSharpToJava.Core.TypeScript.Rewriters;

public sealed class TsOverloadDisambiguationRewriter : Java.JavaSyntaxRewriter { ... }
public sealed class TsGenericErasureRewriter : Java.JavaSyntaxRewriter { ... }
public sealed class TsInterfaceProbeRewriter : Java.JavaSyntaxRewriter { ... }
public sealed class TsEqualityRewriter : Java.JavaSyntaxRewriter { ... }
```

---

## 文件结构

### 新增文件

| 路径 | 职责 |
|---|---|
| `src/CSharpToJava.Core/TypeScript/TsNaming.cs` | 标识符安全化、TS 保留字、重载后缀、工厂命名 |
| `src/CSharpToJava.Core/TypeScript/TsMemberMappingTable.cs` | Java→TS 类型/方法/成员映射（内置表 + JSON 配置） |
| `src/CSharpToJava.Core/TypeScript/TsImportCollector.cs` | ESM import 收集与相对路径计算 |
| `src/CSharpToJava.Core/TypeScript/TsExpressionWriter.cs` | 表达式发射 + 成员映射应用 |
| `src/CSharpToJava.Core/TypeScript/TsStatementWriter.cs` | 语句发射 |
| `src/CSharpToJava.Core/TypeScript/TsMemberWriter.cs` | 字段/方法/属性/构造器发射 |
| `src/CSharpToJava.Core/TypeScript/TsTypeWriter.cs` | class/interface/enum 声明发射 |
| `src/CSharpToJava.Core/TypeScript/TypeScriptEmitter.cs` | 总入口，编排上述 writer |
| `src/CSharpToJava.Core/TypeScript/NpmProjectPlan.cs` | npm workspace 工程规划 |
| `src/CSharpToJava.Core/TypeScript/Rewriters/TsOverloadDisambiguationRewriter.cs` | S 级：重载名称改写 + 调用点同步 |
| `src/CSharpToJava.Core/TypeScript/Rewriters/TsGenericErasureRewriter.cs` | S 级：typeof(T)/new T()/default(T) 降级 |
| `src/CSharpToJava.Core/TypeScript/Rewriters/TsInterfaceProbeRewriter.cs` | S 级：接口 instanceof → 运行时探针 |
| `src/CSharpToJava.Core/TypeScript/Rewriters/TsEqualityRewriter.cs` | S 级：Equals 覆写类的相等调用点改写 |
| `config/typescript/TypeMappings.json` | C#/Java → TS 类型映射配置 |
| `config/typescript/MemberMappings.json` | 成员调用映射配置 |
| `typescript/csharptotypescript-compat/package.json` | 兼容库 npm 包定义 |
| `typescript/csharptotypescript-compat/tsconfig.json` | 兼容库 TS 编译配置 |
| `typescript/csharptotypescript-compat/vitest.config.ts` | vitest 配置 |
| `typescript/csharptotypescript-compat/src/index.ts` | 统一导出 |
| `typescript/csharptotypescript-compat/src/core/*.ts` | Ref, CsObject, CsRecord, CsRuntime, TypeToken |
| `typescript/csharptotypescript-compat/src/primitives/*.ts` | CsDecimal, CsChar, CsGuid |
| `typescript/csharptotypescript-compat/src/time/*.ts` | CsDateTime, CsTimeSpan |
| `typescript/csharptotypescript-compat/src/text/*.ts` | CsString, CsStringBuilder |
| `typescript/csharptotypescript-compat/src/collections/*.ts` | CsDictionary, CsSet, Enumerable |
| `typescript/csharptotypescript-compat/src/events/*.ts` | CsEvent |
| `typescript/csharptotypescript-compat/src/exceptions/*.ts` | CsException 层次 |
| `tests/CSharpToTypeScript.Tests/CSharpToTypeScript.Tests.csproj` | TS 转换单元测试项目 |
| `tests/CSharpToTypeScript.Tests/TsConversionTestBase.cs` | 测试基类（Convert/AssertCompiles/AssertDiagnostic） |

### 修改文件

| 路径 | 改动 |
|---|---|
| `src/CSharpToJava.Core/Context/ConversionOptions.cs` | 新增 `TargetLanguage`/`TypeScriptMappingConfigPath`/`TypeScriptMemberMappingPath` |
| `src/CSharpToJava.Core/Pipeline/ConversionPipeline.cs` | `CreateSingleFilePasses` 按目标分叉 |
| `src/CSharpToJava.Core/Pipeline/Passes/SingleFilePasses.cs` | 新增 `SingleFileTsEmitPass` |
| `src/CSharpToJava.Core/Pipeline/ProjectConversionPipeline.cs` | 项目级按目标分叉（M5） |
| `src/CSharpToJava.CLI/Program.cs` | `convert`/`convert-project` 新增 `--target` |
| `CSharpToJavaConverter.slnx` | 注册新测试项目 |

---

## 里程碑依赖

```
M1 骨架 ──► M2 核心特性 ──► M3 语义降级 ──► M5 项目级
                ▲                  │
                └──────────────────┘
                    M4 兼容库（依赖 M1 发射骨架）
```

每个里程碑独立可交付、独立验收。M1→M2→M3 顺序执行；M4 可与 M3 并行（仅依赖 M1）；M5 依赖 M1-M4。

---

# 里程碑 M1：骨架（TargetLanguage 选项 + 管线分叉 + 基础发射器）

**验收标准：** §5.3 基础发射组测试全绿；简单类产物过 L3 `npx tsc --noEmit`。

## Task 1: ConversionOptions 新增 TargetLanguage 字段

**Files:**
- Modify: `src/CSharpToJava.Core/Context/ConversionOptions.cs`

- [ ] **Step 1: 写失败测试**

创建 `tests/CSharpToTypeScript.Tests/CSharpToTypeScript.Tests.csproj`（见 Task 3）暂不可用，先直接在现有测试项目加临时测试验证字段存在。

在 `tests/CSharpToJava.Tests/TargetLanguageOptionTests.cs` 写：

```csharp
using CSharpToJava.Core.Context;

namespace CSharpToJava.Tests;

public class TargetLanguageOptionTests
{
    [Fact]
    public void Default_TargetLanguage_Is_Java()
    {
        var opts = new ConversionOptions();
        Assert.Equal(TargetLanguage.Java, opts.TargetLanguage);
    }

    [Fact]
    public void TargetLanguage_Can_Be_Set_To_TypeScript()
    {
        var opts = new ConversionOptions { TargetLanguage = TargetLanguage.TypeScript };
        Assert.Equal(TargetLanguage.TypeScript, opts.TargetLanguage);
    }
}
```

- [ ] **Step 2: 运行测试验证失败**

Run: `dotnet test tests/CSharpToJava.Tests/CSharpToJava.Tests.csproj --filter "FullyQualifiedName~TargetLanguageOptionTests"`
Expected: FAIL with "Type 'TargetLanguage' not found" 或类似编译错误。

- [ ] **Step 3: 实现最小代码**

在 `src/CSharpToJava.Core/Context/ConversionOptions.cs` 末尾（`JavaVersion` 枚举前）添加字段，在文件末尾添加枚举：

```csharp
// 在 ConversionOptions 类内末尾（DefaultFactoryMethods 属性后）添加：

/// <summary>
/// 转换目标语言。默认 Java（向后兼容）。
/// 设为 TypeScript 时，管线在 Codegen 层分叉到 TypeScriptEmitter。
/// </summary>
public TargetLanguage TargetLanguage { get; set; } = TargetLanguage.Java;

/// <summary>
/// TypeScript 类型映射配置路径（默认 config/typescript/TypeMappings.json）。
/// 仅当 TargetLanguage == TypeScript 时使用。
/// </summary>
public string? TypeScriptMappingConfigPath { get; set; }

/// <summary>
/// TypeScript 成员调用映射配置路径（默认 config/typescript/MemberMappings.json）。
/// 仅当 TargetLanguage == TypeScript 时使用。
/// </summary>
public string? TypeScriptMemberMappingPath { get; set; }
```

文件末尾添加枚举：

```csharp
/// <summary>
/// 转换目标语言枚举
/// </summary>
public enum TargetLanguage
{
    Java,
    TypeScript,
}
```

- [ ] **Step 4: 运行测试验证通过**

Run: `dotnet test tests/CSharpToJava.Tests/CSharpToJava.Tests.csproj --filter "FullyQualifiedName~TargetLanguageOptionTests"`
Expected: PASS（2 个测试）。

- [ ] **Step 5: 提交**

```bash
git add src/CSharpToJava.Core/Context/ConversionOptions.cs tests/CSharpToJava.Tests/TargetLanguageOptionTests.cs
git commit -m "feat(ts): add TargetLanguage option to ConversionOptions"
```

---

## Task 2: 创建 TS 映射配置文件

**Files:**
- Create: `config/typescript/TypeMappings.json`
- Create: `config/typescript/MemberMappings.json`

- [ ] **Step 1: 创建 TypeMappings.json**

```json
{
    "typeMappings": [
        { "csharp": "System.String", "typescript": "string", "imports": [] },
        { "csharp": "System.Int32", "typescript": "number", "imports": [] },
        { "csharp": "System.Int64", "typescript": "bigint", "imports": [] },
        { "csharp": "System.Int16", "typescript": "number", "imports": [] },
        { "csharp": "System.Byte", "typescript": "number", "imports": [] },
        { "csharp": "System.SByte", "typescript": "number", "imports": [] },
        { "csharp": "System.UInt32", "typescript": "number", "imports": [] },
        { "csharp": "System.UInt64", "typescript": "bigint", "imports": [] },
        { "csharp": "System.UInt16", "typescript": "number", "imports": [] },
        { "csharp": "System.Single", "typescript": "number", "imports": [] },
        { "csharp": "System.Double", "typescript": "number", "imports": [] },
        { "csharp": "System.Boolean", "typescript": "boolean", "imports": [] },
        { "csharp": "System.Char", "typescript": "string", "imports": [] },
        { "csharp": "System.Object", "typescript": "any", "imports": [] },
        { "csharp": "System.Void", "typescript": "void", "imports": [] },
        { "csharp": "System.Decimal", "typescript": "CsDecimal", "imports": ["@csharptotypescript/compat"] },
        { "csharp": "System.DateTime", "typescript": "CsDateTime", "imports": ["@csharptotypescript/compat"] },
        { "csharp": "System.DateTimeOffset", "typescript": "CsDateTime", "imports": ["@csharptotypescript/compat"] },
        { "csharp": "System.TimeSpan", "typescript": "CsTimeSpan", "imports": ["@csharptotypescript/compat"] },
        { "csharp": "System.Guid", "typescript": "CsGuid", "imports": ["@csharptotypescript/compat"] },
        { "csharp": "System.Text.StringBuilder", "typescript": "CsStringBuilder", "imports": ["@csharptotypescript/compat"] },
        { "csharp": "System.Collections.Generic.List`1", "typescript": "Array", "imports": [] },
        { "csharp": "System.Collections.Generic.IEnumerable`1", "typescript": "Iterable", "imports": [] },
        { "csharp": "System.Collections.Generic.IEnumerator`1", "typescript": "Iterator", "imports": [] },
        { "csharp": "System.Collections.Generic.Dictionary`2", "typescript": "CsDictionary", "imports": ["@csharptotypescript/compat"] },
        { "csharp": "System.Collections.Generic.HashSet`1", "typescript": "CsSet", "imports": ["@csharptotypescript/compat"] },
        { "csharp": "System.Threading.Tasks.Task", "typescript": "Promise<void>", "imports": [] },
        { "csharp": "System.Threading.Tasks.Task`1", "typescript": "Promise", "imports": [] },
        { "csharp": "System.Threading.Tasks.ValueTask`1", "typescript": "Promise", "imports": [] },
        { "csharp": "System.Exception", "typescript": "CsException", "imports": ["@csharptotypescript/compat"] },
        { "csharp": "System.IAsyncResult", "typescript": "Promise<void>", "imports": [] }
    ]
}
```

- [ ] **Step 2: 创建 MemberMappings.json**

```json
{
    "methodMappings": [
        { "type": "System.String", "method": "ToUpper", "tsMethod": "toUpperCase", "signature": "" },
        { "type": "System.String", "method": "ToLower", "tsMethod": "toLowerCase", "signature": "" },
        { "type": "System.String", "method": "Length", "tsMethod": "length", "signature": "property" },
        { "type": "System.String", "method": "Split", "tsMethod": "CsString.split", "signature": "static", "imports": ["@csharptotypescript/compat"] },
        { "type": "System.String", "method": "Format", "tsMethod": "CsString.format", "signature": "static", "imports": ["@csharptotypescript/compat"] },
        { "type": "System.String", "method": "IsNullOrEmpty", "tsMethod": "CsString.isNullOrEmpty", "signature": "static", "imports": ["@csharptotypescript/compat"] },
        { "type": "System.String", "method": "IsNullOrWhiteSpace", "tsMethod": "CsString.isNullOrWhiteSpace", "signature": "static", "imports": ["@csharptotypescript/compat"] },
        { "type": "System.String", "method": "PadLeft", "tsMethod": "CsString.padLeft", "signature": "static", "imports": ["@csharptotypescript/compat"] },
        { "type": "System.String", "method": "PadRight", "tsMethod": "CsString.padRight", "signature": "static", "imports": ["@csharptotypescript/compat"] },
        { "type": "System.String", "method": "Join", "tsMethod": "CsString.join", "signature": "static", "imports": ["@csharptotypescript/compat"] },
        { "type": "System.Collections.Generic.List`1", "method": "Add", "tsMethod": "push", "signature": "" },
        { "type": "System.Collections.Generic.List`1", "method": "Count", "tsMethod": "length", "signature": "property" },
        { "type": "System.Collections.Generic.List`1", "method": "Clear", "tsMethod": "length = 0", "signature": "" },
        { "type": "System.Console", "method": "WriteLine", "tsMethod": "console.log", "signature": "static" },
        { "type": "System.Console", "method": "Write", "tsMethod": "process.stdout.write", "signature": "static" },
        { "type": "System.Math", "method": "Sqrt", "tsMethod": "Math.sqrt", "signature": "static" },
        { "type": "System.Math", "method": "Abs", "tsMethod": "Math.abs", "signature": "static" },
        { "type": "System.Math", "method": "Max", "tsMethod": "Math.max", "signature": "static" },
        { "type": "System.Math", "method": "Min", "tsMethod": "Math.min", "signature": "static" },
        { "type": "System.Math", "method": "Floor", "tsMethod": "Math.floor", "signature": "static" },
        { "type": "System.Math", "method": "Ceiling", "tsMethod": "Math.ceil", "signature": "static" },
        { "type": "System.Math", "method": "Round", "tsMethod": "Math.round", "signature": "static" },
        { "type": "System.Convert", "method": "ToInt32", "tsMethod": "Math.trunc", "signature": "static" },
        { "type": "System.Convert", "method": "ToInt64", "tsMethod": "BigInt", "signature": "static" },
        { "type": "System.Convert", "method": "ToString", "tsMethod": "String", "signature": "static" }
    ]
}
```

- [ ] **Step 3: 提交**

```bash
git add config/typescript/TypeMappings.json config/typescript/MemberMappings.json
git commit -m "feat(ts): add TypeScript type and member mapping configs"
```

---

## Task 3: 创建 TS 测试项目骨架

**Files:**
- Create: `tests/CSharpToTypeScript.Tests/CSharpToTypeScript.Tests.csproj`
- Create: `tests/CSharpToTypeScript.Tests/TsConversionTestBase.cs`
- Modify: `CSharpToJavaConverter.slnx`

- [ ] **Step 1: 创建测试项目 csproj**

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <IsPackable>false</IsPackable>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="coverlet.collector" Version="6.0.4" />
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.14.1" />
    <PackageReference Include="xunit" Version="2.9.3" />
    <PackageReference Include="xunit.runner.visualstudio" Version="3.1.4" />
  </ItemGroup>

  <ItemGroup>
    <Using Include="Xunit" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\..\src\CSharpToJava.Core\CSharpToJava.Core.csproj" />
    <ProjectReference Include="..\..\src\CSharpToJava.CLI\CSharpToJava.CLI.csproj" />
    <ProjectReference Include="..\..\src\CSharpToJava.TypeMapping\CSharpToJava.TypeMapping.csproj" />
  </ItemGroup>

  <ItemGroup>
    <Content Include="..\..\config\typescript\TypeMappings.json">
      <Link>config\typescript\TypeMappings.json</Link>
      <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
    </Content>
    <Content Include="..\..\config\typescript\MemberMappings.json">
      <Link>config\typescript\MemberMappings.json</Link>
      <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
    </Content>
  </ItemGroup>

</Project>
```

- [ ] **Step 2: 创建测试基类**

```csharp
using System.Diagnostics;
using System.IO;
using System.Reflection;
using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToTypeScript.Tests;

public abstract class TsConversionTestBase
{
    protected ConversionResult Convert(string csharpSource)
    {
        var basePath = AppContext.BaseDirectory;
        var typeMappingPath = Path.Combine(basePath, "config", "typescript", "TypeMappings.json");
        var memberMappingPath = Path.Combine(basePath, "config", "typescript", "MemberMappings.json");

        var pipeline = new ConversionPipeline();
        return pipeline.Convert(new ConversionRequest
        {
            SourceCode = csharpSource,
            FileName = "Sample.cs",
            Options = new ConversionOptions
            {
                TargetLanguage = TargetLanguage.TypeScript,
                TypeScriptMappingConfigPath = File.Exists(typeMappingPath) ? typeMappingPath : null,
                TypeScriptMemberMappingPath = File.Exists(memberMappingPath) ? memberMappingPath : null,
            },
        });
    }

    protected static string StripExports(string code)
    {
        return code.Replace("export ", "");
    }

    protected void AssertDiagnostic(ConversionResult r, string contains)
    {
        Assert.True(r.Diagnostics.Any(d => d.Message.Contains(contains, StringComparison.Ordinal)),
            $"Expected diagnostic containing '{contains}'. Got: {string.Join("; ", r.Diagnostics.Select(d => d.Message))}");
    }

    /// <summary>
    /// L3: 写临时工程 + npx tsc --noEmit。无 Node 环境时跳过。
    /// </summary>
    protected void AssertCompiles(string tsCode, params string[] additionalFiles)
    {
        var nodePath = FindNodeExecutable();
        if (nodePath == null)
        {
            Skip.If(true, "Node.js not available — L3 tsc check skipped.");
            return;
        }

        var tmpDir = Path.Combine(Path.GetTempPath(), "cs2j_ts_compile_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmpDir);
        try
        {
            File.WriteAllText(Path.Combine(tmpDir, "package.json"), "{\"type\":\"module\"}");
            File.WriteAllText(Path.Combine(tmpDir, "tsconfig.json"),
                "{\"compilerOptions\":{\"strict\":true,\"target\":\"ES2022\",\"module\":\"NodeNext\",\"moduleResolution\":\"NodeNext\",\"noEmit\":true,\"useDefineForClassFields\":false}}");
            File.WriteAllText(Path.Combine(tmpDir, "Sample.ts"), tsCode);
            for (int i = 0; i < additionalFiles.Length; i += 2)
            {
                File.WriteAllText(Path.Combine(tmpDir, additionalFiles[i]), additionalFiles[i + 1]);
            }

            var psi = new ProcessStartInfo
            {
                FileName = nodePath,
                Arguments = "node_modules/typescript/bin/tsc --noEmit",
                WorkingDirectory = tmpDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            // 先安装 typescript
            var installPsi = new ProcessStartInfo
            {
                FileName = nodePath.Contains("node.exe") ? Path.Combine(Path.GetDirectoryName(nodePath)!, "npm.cmd") : "npm",
                Arguments = "install typescript --no-save",
                WorkingDirectory = tmpDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            var install = Process.Start(installPsi);
            install!.WaitForExit(60000);

            var proc = Process.Start(psi);
            proc!.WaitForExit(30000);
            var stdout = proc.StandardOutput.ReadToEnd();
            var stderr = proc.StandardError.ReadToEnd();
            Assert.True(proc.ExitCode == 0,
                $"tsc failed (exit {proc.ExitCode}):\n--- stdout ---\n{stdout}\n--- stderr ---\n{stderr}\n--- code ---\n{tsCode}");
        }
        finally
        {
            try { Directory.Delete(tmpDir, true); } catch { }
        }
    }

    private static string? FindNodeExecutable()
    {
        var name = OperatingSystem.IsWindows() ? "node.exe" : "node";
        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (pathEnv == null) return null;
        foreach (var dir in pathEnv.Split(Path.PathSeparator))
        {
            var full = Path.Combine(dir, name);
            if (File.Exists(full)) return full;
        }
        return null;
    }
}
```

注：测试基类使用 `Xunit.SkippableFact` 风格的 `Skip.If`，需添加 `xunit.SkippableFact` 包。为简化 M1，先改用条件 `Skip.NotNode` 标记——在 csproj 额外加：

```xml
<PackageReference Include="xunit.SkippableFact" Version="1.5.23" />
```

并在基类 using 加 `using Xunit;`（已含）。将 `AssertCompiles` 标记为可跳过：将 `Skip.If(true, ...)` 改用 `throw new SkipException(...)`（SkippableFact 包提供）。

- [ ] **Step 3: 注册到 slnx**

读取 `CSharpToJavaConverter.slnx`，在 `<Project>` 列表中添加：

```xml
<Project Path="tests/CSharpToTypeScript.Tests/CSharpToTypeScript.Tests.csproj" />
```

- [ ] **Step 4: 验证构建**

Run: `dotnet build tests/CSharpToTypeScript.Tests/CSharpToTypeScript.Tests.csproj`
Expected: BUILD SUCCESS（即使 TS 发射器尚未实现，测试项目应能编译，因为只依赖现有 Core 项目）。

- [ ] **Step 5: 提交**

```bash
git add tests/CSharpToTypeScript.Tests/ CSharpToJavaConverter.slnx
git commit -m "feat(ts): scaffold CSharpToTypeScript.Tests project with base class"
```

---

## Task 4: TsNaming — 标识符安全化

**Files:**
- Create: `src/CSharpToJava.Core/TypeScript/TsNaming.cs`

- [ ] **Step 1: 写失败测试**

创建 `tests/CSharpToTypeScript.Tests/TsNamingSafetyTests.cs`：

```csharp
using CSharpToJava.Core.TypeScript;

namespace CSharpToTypeScript.Tests;

public class TsNamingSafetyTests
{
    [Fact]
    public void SafeIdentifier_Passthrough_Normal_Name()
    {
        Assert.Equal("foo", TsNaming.SafeIdentifier("foo"));
    }

    [Fact]
    public void SafeIdentifier_TsReservedWord_Gets_Dollar_Suffix()
    {
        Assert.Equal("let$", TsNaming.SafeIdentifier("let"));
        Assert.Equal("any$", TsNaming.SafeIdentifier("any"));
        Assert.Equal("namespace$", TsNaming.SafeIdentifier("namespace"));
    }

    [Fact]
    public void SafeIdentifier_Csharp_At_Escaped_Strips_Prefix()
    {
        // C# @class -> TS class$ (class is TS reserved-ish)
        Assert.Equal("class$", TsNaming.SafeIdentifier("@class"));
    }

    [Fact]
    public void OverloadSuffix_Joins_Param_Types()
    {
        var suffix = TsNaming.OverloadSuffix(new[] { "string", "number" });
        Assert.Equal("_string_number", suffix);
    }

    [Fact]
    public void OverloadSuffix_Single_Type()
    {
        Assert.Equal("_number", TsNaming.OverloadSuffix(new[] { "number" }));
    }

    [Fact]
    public void FactoryName_Uses_Ordinal()
    {
        Assert.Equal("Create$1", TsNaming.FactoryName(1));
        Assert.Equal("Create$2", TsNaming.FactoryName(2));
    }
}
```

- [ ] **Step 2: 运行测试验证失败**

Run: `dotnet test tests/CSharpToTypeScript.Tests/CSharpToTypeScript.Tests.csproj --filter "FullyQualifiedName~TsNamingSafetyTests"`
Expected: FAIL（TypeScriptEmitter 命名空间不存在）。

- [ ] **Step 3: 实现 TsNaming**

```csharp
using System.Collections.Generic;

namespace CSharpToJava.Core.TypeScript;

/// <summary>
/// TypeScript 标识符安全化与命名规则。
/// 处理 TS 保留字冲突、C# @ 转义前缀去除、重载后缀生成、构造器工厂命名。
/// </summary>
public static class TsNaming
{
    /// <summary>
    /// TypeScript 保留字与上下文关键字集合（与 C# 交集部分需后缀化）。
    /// </summary>
    private static readonly HashSet<string> TsReservedWords = new()
    {
        "break", "case", "catch", "class", "const", "continue", "debugger",
        "default", "delete", "do", "else", "enum", "export", "extends",
        "false", "finally", "for", "function", "if", "import", "in",
        "instanceof", "let", "new", "null", "return", "super", "switch",
        "this", "throw", "true", "try", "typeof", "var", "void", "while",
        "with", "yield", "any", "as", "async", "await", "boolean",
        "constructor", "declare", "get", "infer", "is", "keyof", "module",
        "namespace", "never", "number", "object", "of", "private",
        "protected", "public", "readonly", "require", "satisfies", "set",
        "static", "string", "symbol", "type", "undefined", "unknown",
    };

    /// <summary>
    /// 将 C# 标识符安全化为合法 TS 标识符。
    /// 规则：① 去除 C# @ 前缀；② 若结果为 TS 保留字，追加 $ 后缀。
    /// </summary>
    public static string SafeIdentifier(string name)
    {
        if (string.IsNullOrEmpty(name)) return "_";

        // 去除 C# @ 转义前缀
        if (name.StartsWith("@"))
        {
            name = name[1..];
        }

        // TS 保留字冲突 -> 追加 $
        if (TsReservedWords.Contains(name))
        {
            return name + "$";
        }

        return name;
    }

    /// <summary>
    /// 类名安全化（同 SafeIdentifier，但保留 PascalCase）。
    /// </summary>
    public static string SafeClassName(string name)
    {
        return SafeIdentifier(name);
    }

    /// <summary>
    /// 生成重载方法后缀。规则：_type1_type2（TS 映射类型名小写连接）。
    /// 示例：["string", "number"] -> "_string_number"
    /// </summary>
    public static string OverloadSuffix(IReadOnlyList<string> paramTsTypes)
    {
        if (paramTsTypes.Count == 0) return "";
        return "_" + string.Join("_", paramTsTypes);
    }

    /// <summary>
    /// 构造器重载转静态工厂的命名。规则：Create$N（N 按参数列表序号）。
    /// 与方法重载的类型后缀规则刻意区分（构造器无名称，类型后缀易产生超长难读名）。
    /// </summary>
    public static string FactoryName(int ordinal)
    {
        return $"Create${ordinal}";
    }
}
```

- [ ] **Step 4: 运行测试验证通过**

Run: `dotnet test tests/CSharpToTypeScript.Tests/CSharpToTypeScript.Tests.csproj --filter "FullyQualifiedName~TsNamingSafetyTests"`
Expected: PASS（6 个测试）。

- [ ] **Step 5: 提交**

```bash
git add src/CSharpToJava.Core/TypeScript/TsNaming.cs tests/CSharpToTypeScript.Tests/TsNamingSafetyTests.cs
git commit -m "feat(ts): add TsNaming for identifier safety and overload suffixes"
```

---

## Task 5: TsMemberMappingTable — Java→TS 映射

**Files:**
- Create: `src/CSharpToJava.Core/TypeScript/TsMemberMappingTable.cs`

- [ ] **Step 1: 写失败测试**

创建 `tests/CSharpToTypeScript.Tests/TsMemberMappingTableTests.cs`：

```csharp
using CSharpToJava.Core.TypeScript;

namespace CSharpToTypeScript.Tests;

public class TsMemberMappingTableTests
{
    private static string ConfigPath(string name) =>
        System.IO.Path.Combine(System.AppContext.BaseDirectory, "config", "typescript", name);

    [Fact]
    public void TryMapType_Int32_To_Number()
    {
        var table = new TsMemberMappingTable(ConfigPath("TypeMappings.json"), ConfigPath("MemberMappings.json"));
        Assert.True(table.TryMapType("System.Int32", out var tsType, out _));
        Assert.Equal("number", tsType);
    }

    [Fact]
    public void TryMapType_Int64_To_Bigint()
    {
        var table = new TsMemberMappingTable(ConfigPath("TypeMappings.json"), ConfigPath("MemberMappings.json"));
        Assert.True(table.TryMapType("System.Int64", out var tsType, out _));
        Assert.Equal("bigint", tsType);
    }

    [Fact]
    public void TryMapType_Decimal_To_CsDecimal_With_Import()
    {
        var table = new TsMemberMappingTable(ConfigPath("TypeMappings.json"), ConfigPath("MemberMappings.json"));
        Assert.True(table.TryMapType("System.Decimal", out var tsType, out var imports));
        Assert.Equal("CsDecimal", tsType);
        Assert.Contains("@csharptotypescript/compat", imports);
    }

    [Fact]
    public void TryMapMethod_String_ToUpper_To_ToUpperCase()
    {
        var table = new TsMemberMappingTable(ConfigPath("TypeMappings.json"), ConfigPath("MemberMappings.json"));
        Assert.True(table.TryMapMethod("System.String", "ToUpper", out var tsMethod, out _));
        Assert.Equal("toUpperCase", tsMethod);
    }

    [Fact]
    public void TryMapMethod_List_Add_To_Push()
    {
        var table = new TsMemberMappingTable(ConfigPath("TypeMappings.json"), ConfigPath("MemberMappings.json"));
        Assert.True(table.TryMapMethod("System.Collections.Generic.List`1", "Add", out var tsMethod, out _));
        Assert.Equal("push", tsMethod);
    }

    [Fact]
    public void TryMapMember_String_Length_To_length()
    {
        var table = new TsMemberMappingTable(ConfigPath("TypeMappings.json"), ConfigPath("MemberMappings.json"));
        Assert.True(table.TryMapMember("System.String", "Length", out var tsMember));
        Assert.Equal("length", tsMember);
    }

    [Fact]
    public void TryMapType_Unknown_Returns_False()
    {
        var table = new TsMemberMappingTable(ConfigPath("TypeMappings.json"), ConfigPath("MemberMappings.json"));
        Assert.False(table.TryMapType("MyApp.Foo", out _, out _));
    }

    [Fact]
    public void Builtin_MapSize_To_Length()
    {
        // 内置表：Java .size() -> TS .length（无配置）
        var table = new TsMemberMappingTable(null, null);
        Assert.True(table.TryMapMethod("java.util.List", "size", out var tsMethod, out _));
        Assert.Equal("length", tsMethod);
    }
}
```

- [ ] **Step 2: 运行测试验证失败**

Run: `dotnet test tests/CSharpToTypeScript.Tests/CSharpToTypeScript.Tests.csproj --filter "FullyQualifiedName~TsMemberMappingTableTests"`
Expected: FAIL（类型不存在）。

- [ ] **Step 3: 实现 TsMemberMappingTable**

```csharp
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace CSharpToJava.Core.TypeScript;

/// <summary>
/// Java→TypeScript 二次映射表。
/// 解析顺序（发射期，spec §3.1.2）：
/// ① 内置表（代码，覆盖最高频项：size→length、add→push 等）
/// ② MemberMappings.json（配置，可扩展）
/// ③ TypeMappings.json 的 csharp→typescript 直查（用于类型注解）
/// ④ 未命中 -> 调用方输出 /* cs2j: unmapped */ 诊断注释
/// </summary>
public sealed class TsMemberMappingTable
{
    private readonly Dictionary<string, (string TsType, List<string> Imports)> _typeMappings = new();
    private readonly Dictionary<(string Type, string Method), (string TsMethod, List<string> Imports)> _methodMappings = new();
    private readonly Dictionary<(string Type, string Member), string> _memberMappings = new();

    // 内置成员映射（最高频项，spec §3.1.2 ①）
    // IR 中已是 Java 风味，这里覆盖 Java 化 API 调用
    private static readonly Dictionary<(string Type, string Method), string> BuiltinMethodMappings = new()
    {
        { ("java.util.List", "size"), "length" },
        { ("java.util.Collection", "size"), "length" },
        { ("java.lang.String", "length"), "length" },
        { ("java.util.List", "add"), "push" },
        { ("java.util.ArrayList", "add"), "push" },
        { ("java.util.List", "get"), "at" },  // 注意：JS at() 索引，与 Java get(i) 一致
        { ("java.util.List", "isEmpty"), "length === 0" },
        { ("java.util.Collection", "isEmpty"), "length === 0" },
        { ("java.util.List", "contains"), "includes" },
        { ("java.util.List", "indexOf"), "indexOf" },
        { ("java.util.List", "remove"), "splice" },
        { ("java.lang.String", "toUpperCase"), "toUpperCase" },
        { ("java.lang.String", "toLowerCase"), "toLowerCase" },
        { ("java.lang.String", "trim"), "trim" },
        { ("java.lang.String", "split"), "split" },
        { ("java.lang.String", "replace"), "replace" },
        { ("java.lang.String", "substring"), "substring" },
        { ("java.lang.String", "startsWith"), "startsWith" },
        { ("java.lang.String", "endsWith"), "endsWith" },
        { ("java.lang.String", "contains"), "includes" },
        { ("java.lang.String", "indexOf"), "indexOf" },
        { ("java.lang.String", "charAt"), "charAt" },
        { ("java.lang.String", "equals"), "===" },
        { ("java.lang.String", "equalsIgnoreCase"), "toLowerCase() === .toLowerCase()" },
        { ("java.lang.System", "out.println"), "console.log" },
        { ("java.lang.System", "out.print"), "process.stdout.write" },
        { ("java.lang.Math", "sqrt"), "Math.sqrt" },
        { ("java.lang.Math", "abs"), "Math.abs" },
        { ("java.lang.Math", "max"), "Math.max" },
        { ("java.lang.Math", "min"), "Math.min" },
        { ("java.lang.Math", "floor"), "Math.floor" },
        { ("java.lang.Math", "ceil"), "Math.ceil" },
        { ("java.lang.Math", "round"), "Math.round" },
        { ("java.lang.Math", "pow"), "Math.pow" },
    };

    // 内置属性/字段映射
    private static readonly Dictionary<(string Type, string Member), string> BuiltinMemberMappings = new()
    {
        { ("java.util.List", "size"), "length" },
        { ("java.util.Collection", "size"), "length" },
        { ("java.lang.String", "length"), "length" },
        { ("java.util.ArrayList", "size"), "length" },
    };

    public TsMemberMappingTable(string? typeMappingPath, string? memberMappingPath)
    {
        LoadTypeMappings(typeMappingPath);
        LoadMemberMappings(memberMappingPath);
    }

    public bool TryMapType(string csharpOrJavaType, out string tsType, out IReadOnlyList<string> imports)
    {
        if (_typeMappings.TryGetValue(csharpOrJavaType, out var entry))
        {
            tsType = entry.TsType;
            imports = entry.Imports;
            return true;
        }
        tsType = csharpOrJavaType;
        imports = System.Array.Empty<string>();
        return false;
    }

    public bool TryMapMethod(string ownerType, string methodName, out string tsMethod, out IReadOnlyList<string> imports)
    {
        imports = System.Array.Empty<string>();
        // ① 内置表
        if (BuiltinMethodMappings.TryGetValue((ownerType, methodName), out var builtin))
        {
            tsMethod = builtin;
            return true;
        }
        // ② 配置表
        if (_methodMappings.TryGetValue((ownerType, methodName), out var entry))
        {
            tsMethod = entry.TsMethod;
            imports = entry.Imports;
            return true;
        }
        tsMethod = methodName;
        return false;
    }

    public bool TryMapMember(string ownerType, string memberName, out string tsMember)
    {
        // ① 内置表
        if (BuiltinMemberMappings.TryGetValue((ownerType, memberName), out var builtin))
        {
            tsMember = builtin;
            return true;
        }
        // ② 配置表（属性类成员映射复用 methodMappings，signature == "property"）
        if (_memberMappings.TryGetValue((ownerType, memberName), out var configured))
        {
            tsMember = configured;
            return true;
        }
        tsMember = memberName;
        return false;
    }

    private void LoadTypeMappings(string? path)
    {
        if (path == null || !File.Exists(path)) return;
        var json = File.ReadAllText(path);
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("typeMappings", out var arr)) return;
        foreach (var item in arr.EnumerateArray())
        {
            var csharp = item.GetProperty("csharp").GetString()!;
            var ts = item.GetProperty("typescript").GetString()!;
            var imports = new List<string>();
            if (item.TryGetProperty("imports", out var impArr))
            {
                foreach (var imp in impArr.EnumerateArray())
                {
                    imports.Add(imp.GetString()!);
                }
            }
            _typeMappings[csharp] = (ts, imports);
        }
    }

    private void LoadMemberMappings(string? path)
    {
        if (path == null || !File.Exists(path)) return;
        var json = File.ReadAllText(path);
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("methodMappings", out var arr)) return;
        foreach (var item in arr.EnumerateArray())
        {
            var type = item.GetProperty("type").GetString()!;
            var method = item.GetProperty("method").GetString()!;
            var tsMethod = item.GetProperty("tsMethod").GetString()!;
            var signature = item.TryGetProperty("signature", out var sig) ? sig.GetString() ?? "" : "";

            var imports = new List<string>();
            if (item.TryGetProperty("imports", out var impArr))
            {
                foreach (var imp in impArr.EnumerateArray())
                {
                    imports.Add(imp.GetString()!);
                }
            }

            if (signature == "property")
            {
                _memberMappings[(type, method)] = tsMethod;
            }
            else
            {
                _methodMappings[(type, method)] = (tsMethod, imports);
            }
        }
    }
}
```

- [ ] **Step 4: 运行测试验证通过**

Run: `dotnet test tests/CSharpToTypeScript.Tests/CSharpToTypeScript.Tests.csproj --filter "FullyQualifiedName~TsMemberMappingTableTests"`
Expected: PASS（8 个测试）。

- [ ] **Step 5: 提交**

```bash
git add src/CSharpToJava.Core/TypeScript/TsMemberMappingTable.cs tests/CSharpToTypeScript.Tests/TsMemberMappingTableTests.cs
git commit -m "feat(ts): add TsMemberMappingTable with builtin + JSON config mappings"
```

---

## Task 6: TsImportCollector — ESM import 收集

**Files:**
- Create: `src/CSharpToJava.Core/TypeScript/TsImportCollector.cs`

- [ ] **Step 1: 写失败测试**

创建 `tests/CSharpToTypeScript.Tests/TsImportCollectorTests.cs`：

```csharp
using CSharpToJava.Core.TypeScript;

namespace CSharpToTypeScript.Tests;

public class TsImportCollectorTests
{
    [Fact]
    public void Add_Compat_Import_Deduplicates()
    {
        var collector = new TsImportCollector();
        collector.AddNamedImport("CsDateTime", "@csharptotypescript/compat");
        collector.AddNamedImport("CsTimeSpan", "@csharptotypescript/compat");
        collector.AddNamedImport("CsDateTime", "@csharptotypescript/compat");  // 重复
        var imports = collector.GetImports();
        Assert.Single(imports);
        Assert.Equal("@csharptotypescript/compat", imports[0].ModulePath);
        Assert.Contains("CsDateTime", imports[0].NamedImports);
        Assert.Contains("CsTimeSpan", imports[0].NamedImports);
    }

    [Fact]
    public void Add_Relative_Import_For_Same_Package_Type()
    {
        var collector = new TsImportCollector(currentNamespace: "App.Models");
        collector.AddRelativeImport("Point", "App.Models");
        var imports = collector.GetImports();
        Assert.Single(imports);
        Assert.Equal("./Point.js", imports[0].ModulePath);
    }

    [Fact]
    public void Add_Relative_Import_For_Child_Namespace()
    {
        var collector = new TsImportCollector(currentNamespace: "App");
        collector.AddRelativeImport("Point", "App.Models");
        var imports = collector.GetImports();
        Assert.Single(imports);
        Assert.Equal("./Models/Point.js", imports[0].ModulePath);
    }

    [Fact]
    public void GetImports_Sorts_By_Module_Path()
    {
        var collector = new TsImportCollector();
        collector.AddNamedImport("Z", "@csharptotypescript/compat");
        collector.AddNamedImport("A", "@csharptotypescript/other");
        var imports = collector.GetImports();
        Assert.Equal("@csharptotypescript/compat", imports[0].ModulePath);
    }

    [Fact]
    public void EmitImports_Returns_Empty_For_No_Imports()
    {
        var collector = new TsImportCollector();
        Assert.Equal("", collector.EmitImports());
    }

    [Fact]
    public void EmitImports_Formats_Named_Imports()
    {
        var collector = new TsImportCollector();
        collector.AddNamedImport("CsDateTime", "@csharptotypescript/compat");
        collector.AddNamedImport("CsTimeSpan", "@csharptotypescript/compat");
        var output = collector.EmitImports();
        Assert.Contains("import { CsDateTime, CsTimeSpan } from \"@csharptotypescript/compat\";", output);
    }
}
```

- [ ] **Step 2: 运行测试验证失败**

Run: `dotnet test tests/CSharpToTypeScript.Tests/CSharpToTypeScript.Tests.csproj --filter "FullyQualifiedName~TsImportCollectorTests"`
Expected: FAIL。

- [ ] **Step 3: 实现 TsImportCollector**

```csharp
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace CSharpToJava.Core.TypeScript;

/// <summary>
/// 收集 ESM import 声明并计算相对路径（spec §3.3）。
/// 规则：
/// - 同包内类型 -> 相对路径（./Type.js），NodeNext 模块解析要求 .js 后缀
/// - 跨包引用 -> workspace 包名导入（@proj/core）
/// - 兼容库符号 -> @csharptotypescript/compat
/// - 按模块路径去重，命名导入合并
/// </summary>
public sealed class TsImportCollector
{
    private readonly string? _currentNamespace;
    private readonly Dictionary<string, SortedSet<string>> _namedImports = new();

    public TsImportCollector(string? currentNamespace = null)
    {
        _currentNamespace = currentNamespace;
    }

    /// <summary>
    /// 添加具名导入（如 CsDateTime from @csharptotypescript/compat）。
    /// </summary>
    public void AddNamedImport(string name, string modulePath)
    {
        if (!_namedImports.TryGetValue(modulePath, out var names))
        {
            names = new SortedSet<string>();
            _namedImports[modulePath] = names;
        }
        names.Add(name);
    }

    /// <summary>
    /// 添加同项目内类型的相对路径导入。
    /// namespace -> 目录路径（App.Models -> App/Models/），文件名 = 类型名 + .js。
    /// </summary>
    public void AddRelativeImport(string typeName, string targetNamespace)
    {
        var modulePath = ComputeRelativePath(targetNamespace);
        AddNamedImport(typeName, modulePath);
    }

    /// <summary>
    /// 计算从当前命名空间到目标命名空间的相对模块路径。
    /// App + App.Models -> ./Models/Type.js
    /// App.Models + App.Models -> ./Type.js
    /// </summary>
    private string ComputeRelativePath(string targetNamespace)
    {
        var currentParts = _currentNamespace?.Split('.') ?? System.Array.Empty<string>();
        var targetParts = targetNamespace.Split('.');

        // 找公共前缀
        int common = 0;
        while (common < currentParts.Length && common < targetParts.Length
               && currentParts[common] == targetParts[common])
        {
            common++;
        }

        // 目标在当前命名空间内（公共前缀 == 当前命名空间长度）
        if (common == currentParts.Length)
        {
            var remaining = targetParts.Skip(common).ToArray();
            if (remaining.Length == 0)
            {
                // 同命名空间：./Type.js
                return "";  // 调用方负责追加类型名
            }
            return "./" + string.Join("/", remaining) + "/";
        }

        // 跨命名空间回退（简化处理：用相对 ../）
        var up = currentParts.Length - common;
        var down = targetParts.Skip(common).ToArray();
        var sb = new StringBuilder();
        for (int i = 0; i < up; i++)
        {
            sb.Append("../");
        }
        if (down.Length > 0)
        {
            sb.Append(string.Join("/", down)).Append('/');
        }
        return sb.ToString();
    }

    public IReadOnlyList<TsImportEntry> GetImports()
    {
        return _namedImports
            .OrderBy(kv => kv.Key)
            .Select(kv => new TsImportEntry(kv.Key, kv.Value.ToList()))
            .ToList();
    }

    /// <summary>
    /// 发射 import 语句块（每行一条，以换行结尾）。
    /// </summary>
    public string EmitImports()
    {
        if (_namedImports.Count == 0) return "";

        var sb = new StringBuilder();
        foreach (var entry in GetImports())
        {
            var names = string.Join(", ", entry.NamedImports);
            sb.AppendLine($"import {{ {names} }} from \"{entry.ModulePath}\";");
        }
        return sb.ToString();
    }
}

public sealed record TsImportEntry(string ModulePath, IReadOnlyList<string> NamedImports);
```

注：`AddRelativeImport` 中 `ComputeRelativePath` 返回的前缀需与类型名拼接。修正实现——将 `AddRelativeImport` 改为：

```csharp
public void AddRelativeImport(string typeName, string targetNamespace)
{
    var prefix = ComputeRelativePath(targetNamespace);
    // prefix 形如 "./Models/" 或 "./" 或 "../"
    // 同命名空间时 ComputeRelativePath 返回 ""，需补 "./"
    var modulePath = prefix == "" ? $"./{typeName}.js" : $"{prefix}{typeName}.js";
    AddNamedImport(typeName, modulePath);
}
```

并在 `ComputeRelativePath` 的 `common == currentParts.Length` 且 `remaining.Length == 0` 分支返回 `"./"` 而非 `""`：

```csharp
if (common == currentParts.Length)
{
    var remaining = targetParts.Skip(common).ToArray();
    if (remaining.Length == 0)
    {
        return "./";  // 同命名空间
    }
    return "./" + string.Join("/", remaining) + "/";
}
```

- [ ] **Step 4: 运行测试验证通过**

Run: `dotnet test tests/CSharpToTypeScript.Tests/CSharpToTypeScript.Tests.csproj --filter "FullyQualifiedName~TsImportCollectorTests"`
Expected: PASS（6 个测试）。

- [ ] **Step 5: 提交**

```bash
git add src/CSharpToJava.Core/TypeScript/TsImportCollector.cs tests/CSharpToTypeScript.Tests/TsImportCollectorTests.cs
git commit -m "feat(ts): add TsImportCollector for ESM import generation"
```

---

## Task 7: TsExpressionWriter — 表达式发射

**Files:**
- Create: `src/CSharpToJava.Core/TypeScript/TsExpressionWriter.cs`

- [ ] **Step 1: 写失败测试**

创建 `tests/CSharpToTypeScript.Tests/TsExpressionWriterTests.cs`：

```csharp
using CSharpToJava.Core.Java;
using CSharpToJava.Core.TypeScript;

namespace CSharpToTypeScript.Tests;

public class TsExpressionWriterTests
{
    private static string EmitExpr(JavaExpression expr)
    {
        var table = new TsMemberMappingTable(null, null);
        var writer = new TsExpressionWriter(table, new TsImportCollector());
        return writer.ToInlineString(expr);
    }

    [Fact]
    public void Literal_String_Emits_With_Quotes()
    {
        Assert.Equal("\"hello\"", EmitExpr(new JavaLiteralExpression("\"hello\"")));
    }

    [Fact]
    public void Literal_Long_Emits_Bigint_Suffix()
    {
        // IR 中 long 字面量带 L 后缀，TS 需改为 n
        Assert.Equal("100n", EmitExpr(new JavaLiteralExpression("100L")));
    }

    [Fact]
    public void Literal_Char_Emits_As_String()
    {
        // IR 中 char 字面量 'a'，TS 用 "a"
        Assert.Equal("\"a\"", EmitExpr(new JavaLiteralExpression("'a'")));
    }

    [Fact]
    public void Identifier_Passthrough()
    {
        Assert.Equal("foo", EmitExpr(new JavaIdentifierExpression("foo")));
    }

    [Fact]
    public void Identifier_Reserved_Word_Suffixed()
    {
        Assert.Equal("let$", EmitExpr(new JavaIdentifierExpression("let")));
    }

    [Fact]
    public void BinaryExpression_IntegerDivision_Wraps_MathTrunc()
    {
        // 两 number 操作数的 / -> Math.trunc(a / b)
        var expr = new JavaBinaryExpression
        {
            Left = new JavaIdentifierExpression("a"),
            Operator = "/",
            Right = new JavaIdentifierExpression("b"),
        };
        var result = EmitExpr(expr);
        Assert.Equal("Math.trunc(a / b)", result);
    }

    [Fact]
    public void MethodCall_String_ToUpper_Maps_To_ToUpperCase()
    {
        // IR 中已是 Java 风味：str.toUpperCase() （Java 方法名）
        // 内置表映射 java.lang.String.toUpperCase -> toUpperCase（直译）
        var call = new JavaMethodCallExpression
        {
            Target = new JavaIdentifierExpression("str"),
            MethodName = "toUpperCase",
        };
        Assert.Equal("str.toUpperCase()", EmitExpr(call));
    }

    [Fact]
    public void MethodCall_System_Out_Println_Maps_To_ConsoleLog()
    {
        // IR: System.out.println(...) -> console.log(...)
        // 这里用 java.lang.System + out.println 测内置映射
        var call = new JavaMethodCallExpression
        {
            Target = new JavaMemberAccessExpression
            {
                Target = new JavaIdentifierExpression("System"),
                MemberName = "out",
            },
            MethodName = "println",
            Arguments = { new JavaLiteralExpression("\"hi\"") },
        };
        Assert.Equal("console.log(\"hi\")", EmitExpr(call));
    }

    [Fact]
    public void ThisExpression_Emits_As_This()
    {
        Assert.Equal("this", EmitExpr(new JavaThisExpression()));
    }

    [Fact]
    public void LambdaExpression_Emits_As_Arrow_Function()
    {
        var lambda = new JavaLambdaExpression
        {
            Parameters = { new JavaParameter("number", "x") },
            Body = "return x + 1;",
        };
        var result = EmitExpr(lambda);
        Assert.Contains("(x: number) =>", result);
        Assert.Contains("return x + 1;", result);
    }
}
```

- [ ] **Step 2: 运行测试验证失败**

Run: `dotnet test tests/CSharpToTypeScript.Tests/CSharpToTypeScript.Tests.csproj --filter "FullyQualifiedName~TsExpressionWriterTests"`
Expected: FAIL（类型不存在）。

- [ ] **Step 3: 实现 TsExpressionWriter**

```csharp
using System.Text;
using CSharpToJava.Core.Java;

namespace CSharpToJava.Core.TypeScript;

/// <summary>
/// 表达式发射器：遍历 JavaExpression IR 节点，输出 TS 表达式文本。
/// 应用 TsMemberMappingTable 进行 Java→TS 成员/方法映射（spec §2.5 表达式部分）。
/// </summary>
public sealed class TsExpressionWriter
{
    private readonly TsMemberMappingTable _mappings;
    private readonly TsImportCollector _imports;

    public TsExpressionWriter(TsMemberMappingTable mappings, TsImportCollector imports)
    {
        _mappings = mappings;
        _imports = imports;
    }

    public string ToInlineString(JavaExpression expr)
    {
        return expr switch
        {
            JavaLiteralExpression lit => EmitLiteral(lit),
            JavaIdentifierExpression id => TsNaming.SafeIdentifier(id.Name),
            JavaThisExpression => "this",
            JavaBinaryExpression binary => EmitBinary(binary),
            JavaUnaryExpression unary => EmitUnary(unary),
            JavaConditionalExpression cond => $"{ToInlineString(cond.Condition)} ? {ToInlineString(cond.WhenTrue)} : {ToInlineString(cond.WhenFalse)}",
            JavaCastExpression cast => EmitCast(cast),
            JavaNewExpression newExpr => EmitNew(newExpr),
            JavaMethodCallExpression call => EmitMethodCall(call),
            JavaMemberAccessExpression access => EmitMemberAccess(access),
            JavaLambdaExpression lambda => EmitLambda(lambda),
            JavaArrayAccessExpression arr => $"{ToInlineString(arr.Target)}[{ToInlineString(arr.Index)}]",
            JavaAssignmentExpression assign => $"{ToInlineString(assign.Left)} {assign.Operator} {ToInlineString(assign.Right)}",
            JavaParenthesizedExpression paren => $"({ToInlineString(paren.Expression)})",
            JavaInstanceOfExpression inst => EmitInstanceOf(inst),
            JavaRawExpression raw => raw.Expression,
            _ => expr.ToInlineString(),
        };
    }

    private string EmitLiteral(JavaLiteralExpression lit)
    {
        var v = lit.Value;

        // long 字面量：100L -> 100n
        if (v.EndsWith("L") && long.TryParse(v[..^1], out _))
        {
            return v[..^1] + "n";
        }

        // char 字面量：'a' -> "a"
        if (v.StartsWith("'") && v.EndsWith("'") && v.Length == 3)
        {
            return "\"" + v[1] + "\"";
        }

        return v;
    }

    private string EmitBinary(JavaBinaryExpression binary)
    {
        var left = ToInlineString(binary.Left);
        var right = ToInlineString(binary.Right);

        // 整数除法补偿：number 操作数的 / -> Math.trunc(a / b)
        // 简化判定：操作符为 / 且左右均为标识符或字面量（非 bigint）
        if (binary.Operator == "/" && IsIntegerOperand(binary.Left) && IsIntegerOperand(binary.Right))
        {
            return $"Math.trunc({left} / {right})";
        }

        // 相等比较：原始类型 === / !==
        var op = binary.Operator switch
        {
            "==" => "===",
            "!=" => "!==",
            _ => binary.Operator,
        };

        return $"{left} {op} {right}";
    }

    private static bool IsIntegerOperand(JavaExpression expr)
    {
        // 简化：标识符或数字字面量视为可能的 number 操作数
        // 真正的类型感知在 M3 TsEqualityRewriter 中处理
        return expr is JavaIdentifierExpression or JavaLiteralExpression;
    }

    private string EmitUnary(JavaUnaryExpression unary)
    {
        return unary.IsPostfix
            ? $"{ToInlineString(unary.Operand)}{unary.Operator}"
            : $"{unary.Operator}{ToInlineString(unary.Operand)}";
    }

    private string EmitCast(JavaCastExpression cast)
    {
        // 引用类型 -> as；数值窄化 -> Number() 等（M2 细化）
        var tsType = cast.TargetType;
        if (tsType == "number" || tsType == "Number")
        {
            return $"Number({ToInlineString(cast.Expression)})";
        }
        return $"{ToInlineString(cast.Expression)} as {tsType}";
    }

    private string EmitNew(JavaNewExpression newExpr)
    {
        var args = string.Join(", ", newExpr.Arguments.Select(ToInlineString));
        return $"new {newExpr.Type}({args})";
    }

    private string EmitMethodCall(JavaMethodCallExpression call)
    {
        var target = call.Target != null ? ToInlineString(call.Target) + "." : "";

        // 尝试方法映射
        // 简化：根据 target 类型查映射。M1 用方法名直译 + 内置 System.out.println 等特殊处理
        var methodName = call.MethodName;

        // System.out.println -> console.log（特殊：target 是 System.out）
        if (call.Target is JavaMemberAccessExpression ma
            && ma.Target is JavaIdentifierExpression sysId
            && sysId.Name == "System"
            && ma.MemberName == "out"
            && (methodName == "println" || methodName == "print"))
        {
            var args = string.Join(", ", call.Arguments.Select(ToInlineString));
            return methodName == "println"
                ? $"console.log({args})"
                : $"process.stdout.write({args})";
        }

        var args = string.Join(", ", call.Arguments.Select(ToInlineString));

        // 类型参数（泛型方法调用）TS 不需要运行时类型参数，丢弃
        return $"{target}{methodName}({args})";
    }

    private string EmitMemberAccess(JavaMemberAccessExpression access)
    {
        var target = ToInlineString(access.Target);
        // 特殊：System.out / System.err 直译
        if (target == "System")
        {
            return access.MemberName switch
            {
                "out" => "console",
                "err" => "console",
                _ => $"{target}.{access.MemberName}",
            };
        }
        var member = TsNaming.SafeIdentifier(access.MemberName);
        return $"{target}.{member}";
    }

    private string EmitLambda(JavaLambdaExpression lambda)
    {
        var parms = string.Join(", ", lambda.Parameters.Select(p => $"{TsNaming.SafeIdentifier(p.Name)}: {p.Type}"));
        if (lambda.IsExpressionLambda)
        {
            return $"({parms}) => {lambda.Body}";
        }
        return $"({parms}) => {{ {lambda.Body} }}";
    }

    private string EmitInstanceOf(JavaInstanceOfExpression inst)
    {
        // class 直译 instanceof；interface 由 TsInterfaceProbeRewriter 预处理（M3）
        return $"{ToInlineString(inst.Expression)} instanceof {inst.TypeName}";
    }
}
```

注：`JavaLambdaExpression` 的属性名需对照实际 IR。读取 `src/CSharpToJava.Core/Java/JavaExpression.cs` 第 197-248 行确认属性名（`Parameters`/`Body`/`IsExpressionLambda`），若不一致则按实际 IR 调整。

- [ ] **Step 4: 运行测试验证通过**

Run: `dotnet test tests/CSharpToTypeScript.Tests/CSharpToTypeScript.Tests.csproj --filter "FullyQualifiedName~TsExpressionWriterTests"`
Expected: PASS（10 个测试）。

- [ ] **Step 5: 提交**

```bash
git add src/CSharpToJava.Core/TypeScript/TsExpressionWriter.cs tests/CSharpToTypeScript.Tests/TsExpressionWriterTests.cs
git commit -m "feat(ts): add TsExpressionWriter with literal/binary/method-call mapping"
```

---

## Task 8: TsStatementWriter — 语句发射

**Files:**
- Create: `src/CSharpToJava.Core/TypeScript/TsStatementWriter.cs`

- [ ] **Step 1: 写失败测试**

创建 `tests/CSharpToTypeScript.Tests/TsStatementWriterTests.cs`：

```csharp
using CSharpToJava.Core.Java;
using CSharpToJava.Core.TypeScript;

namespace CSharpToTypeScript.Tests;

public class TsStatementWriterTests
{
    private readonly TsStatementWriter _writer = new(
        new TsExpressionWriter(new TsMemberMappingTable(null, null), new TsImportCollector()),
        "    ");

    private string EmitStmt(JavaStatement stmt) => _writer.Emit(stmt, 0);

    [Fact]
    public void BlockStatement_Emits_Braces()
    {
        var block = new JavaBlockStatement();
        block.Statements.Add(new JavaReturnStatement(new JavaLiteralExpression("1")));
        var result = EmitStmt(block);
        Assert.Contains("{", result);
        Assert.Contains("return 1;", result);
        Assert.Contains("}", result);
    }

    [Fact]
    public void VariableDeclaration_Emits_Let()
    {
        var decl = new JavaVariableDeclarationStatement
        {
            VariableType = "number",
            VariableName = "x",
            Initializer = new JavaLiteralExpression("42"),
        };
        var result = EmitStmt(decl);
        Assert.Contains("let x: number = 42;", result);
    }

    [Fact]
    public void ReturnStatement_Emits_Return()
    {
        var ret = new JavaReturnStatement(new JavaIdentifierExpression("x"));
        Assert.Contains("return x;", EmitStmt(ret));
    }

    [Fact]
    public void IfStatement_Emits_If_Else()
    {
        var ifStmt = new JavaIfStatement
        {
            Condition = new JavaIdentifierExpression("flag"),
            ThenStatement = new JavaBlockStatement(),
            ElseStatement = new JavaBlockStatement(),
        };
        var result = EmitStmt(ifStmt);
        Assert.Contains("if (flag) {", result);
        Assert.Contains("} else {", result);
    }

    [Fact]
    public void ForEachStatement_Emits_ForOf()
    {
        var forEach = new JavaForEachStatement
        {
            VariableType = "number",
            VariableName = "item",
            Collection = new JavaIdentifierExpression("arr"),
            Body = new JavaBlockStatement(),
        };
        var result = EmitStmt(forEach);
        Assert.Contains("for (const item of arr) {", result);
    }

    [Fact]
    public void WhileStatement_Emits_While()
    {
        var whileStmt = new JavaWhileStatement
        {
            Condition = new JavaIdentifierExpression("running"),
            Body = new JavaBlockStatement(),
        };
        var result = EmitStmt(whileStmt);
        Assert.Contains("while (running) {", result);
    }

    [Fact]
    public void ThrowStatement_Emits_Throw()
    {
        var throwStmt = new JavaThrowStatement
        {
            Expression = new JavaNewExpression { Type = "Error", Arguments = { new JavaLiteralExpression("\"oops\"") } },
        };
        var result = EmitStmt(throwStmt);
        Assert.Contains("throw new Error(\"oops\");", result);
    }

    [Fact]
    public void BreakStatement_Emits_Break()
    {
        Assert.Contains("break;", EmitStmt(new JavaBreakStatement()));
    }

    [Fact]
    public void ContinueStatement_Emits_Continue()
    {
        Assert.Contains("continue;", EmitStmt(new JavaContinueStatement()));
    }
}
```

- [ ] **Step 2: 运行测试验证失败**

Run: `dotnet test tests/CSharpToTypeScript.Tests/CSharpToTypeScript.Tests.csproj --filter "FullyQualifiedName~TsStatementWriterTests"`
Expected: FAIL。

- [ ] **Step 3: 实现 TsStatementWriter**

```csharp
using System.Text;
using CSharpToJava.Core.Java;

namespace CSharpToJava.Core.TypeScript;

/// <summary>
/// 语句发射器：遍历 JavaStatement IR 节点，输出 TS 语句文本（spec §2.5 语句部分）。
/// </summary>
public sealed class TsStatementWriter
{
    private readonly TsExpressionWriter _exprWriter;
    private readonly string _indent;

    public TsStatementWriter(TsExpressionWriter exprWriter, string indent = "    ")
    {
        _exprWriter = exprWriter;
        _indent = indent;
    }

    public string Emit(JavaStatement stmt, int indentLevel)
    {
        var ind = new string(_indent[0], indentLevel * _indent.Length);
        return stmt switch
        {
            JavaBlockStatement block => EmitBlock(block, indentLevel),
            JavaVariableDeclarationStatement varDecl => EmitVarDecl(varDecl, ind),
            JavaExpressionStatement exprStmt => $"{ind}{_exprWriter.ToInlineString(exprStmt.Expression)};",
            JavaReturnStatement ret => ret.Expression != null
                ? $"{ind}return {_exprWriter.ToInlineString(ret.Expression)};"
                : $"{ind}return;",
            JavaIfStatement ifStmt => EmitIf(ifStmt, indentLevel),
            JavaForEachStatement forEach => EmitForEach(forEach, indentLevel),
            JavaForStatement forStmt => EmitFor(forStmt, indentLevel),
            JavaWhileStatement whileStmt => EmitWhile(whileStmt, indentLevel),
            JavaDoWhileStatement doWhile => EmitDoWhile(doWhile, indentLevel),
            JavaTryCatchStatement tryCatch => EmitTryCatch(tryCatch, indentLevel),
            JavaThrowStatement throwStmt => $"{ind}throw {_exprWriter.ToInlineString(throwStmt.Expression)};",
            JavaBreakStatement => $"{ind}break;",
            JavaContinueStatement => $"{ind}continue;",
            JavaSwitchStatement sw => EmitSwitch(sw, indentLevel),
            JavaRawStatement raw => $"{ind}{raw.Code}",
            _ => $"{ind}/* cs2j: unsupported statement {stmt.GetType().Name} */",
        };
    }

    private string EmitBlock(JavaBlockStatement block, int indentLevel)
    {
        var ind = new string(_indent[0], indentLevel * _indent.Length);
        var sb = new StringBuilder();
        sb.AppendLine($"{ind}{{");
        foreach (var s in block.Statements)
        {
            sb.AppendLine(Emit(s, indentLevel + 1));
        }
        sb.Append($"{ind}}}");
        return sb.ToString();
    }

    private string EmitVarDecl(JavaVariableDeclarationStatement decl, string ind)
    {
        var init = decl.Initializer != null ? $" = {_exprWriter.ToInlineString(decl.Initializer)}" : "";
        return $"{ind}let {TsNaming.SafeIdentifier(decl.VariableName)}: {decl.VariableType}{init};";
    }

    private string EmitIf(JavaIfStatement ifStmt, int indentLevel)
    {
        var ind = new string(_indent[0], indentLevel * _indent.Length);
        var sb = new StringBuilder();
        sb.Append($"{ind}if ({_exprWriter.ToInlineString(ifStmt.Condition)}) ");
        sb.Append(Emit(ifStmt.ThenStatement, indentLevel));
        if (ifStmt.ElseStatement != null)
        {
            sb.Append($" else ");
            sb.Append(Emit(ifStmt.ElseStatement, indentLevel));
        }
        return sb.ToString();
    }

    private string EmitForEach(JavaForEachStatement forEach, int indentLevel)
    {
        var ind = new string(_indent[0], indentLevel * _indent.Length);
        var sb = new StringBuilder();
        sb.Append($"{ind}for (const {TsNaming.SafeIdentifier(forEach.VariableName)} of {_exprWriter.ToInlineString(forEach.Collection)}) ");
        sb.Append(Emit(forEach.Body, indentLevel));
        return sb.ToString();
    }

    private string EmitFor(JavaForStatement forStmt, int indentLevel)
    {
        var ind = new string(_indent[0], indentLevel * _indent.Length);
        var init = forStmt.Initializer != null ? _exprWriter.ToInlineString(forStmt.Initializer) : "";
        var cond = forStmt.Condition != null ? _exprWriter.ToInlineString(forStmt.Condition) : "";
        var upd = forStmt.Update != null ? _exprWriter.ToInlineString(forStmt.Update) : "";
        var sb = new StringBuilder();
        sb.Append($"{ind}for ({init}; {cond}; {upd}) ");
        sb.Append(Emit(forStmt.Body, indentLevel));
        return sb.ToString();
    }

    private string EmitWhile(JavaWhileStatement whileStmt, int indentLevel)
    {
        var ind = new string(_indent[0], indentLevel * _indent.Length);
        var sb = new StringBuilder();
        sb.Append($"{ind}while ({_exprWriter.ToInlineString(whileStmt.Condition)}) ");
        sb.Append(Emit(whileStmt.Body, indentLevel));
        return sb.ToString();
    }

    private string EmitDoWhile(JavaDoWhileStatement doWhile, int indentLevel)
    {
        var ind = new string(_indent[0], indentLevel * _indent.Length);
        var sb = new StringBuilder();
        sb.Append($"{ind}do ");
        sb.Append(Emit(doWhile.Body, indentLevel));
        sb.Append($" while ({_exprWriter.ToInlineString(doWhile.Condition)});");
        return sb.ToString();
    }

    private string EmitTryCatch(JavaTryCatchStatement tryCatch, int indentLevel)
    {
        var ind = new string(_indent[0], indentLevel * _indent.Length);
        var sb = new StringBuilder();
        sb.AppendLine($"{ind}try ");
        sb.AppendLine(Emit(tryCatch.TryBlock, indentLevel));

        foreach (var clause in tryCatch.CatchClauses)
        {
            // catch 按类型过滤 -> instanceof 链
            var exVar = string.IsNullOrEmpty(clause.VariableName) ? "e" : TsNaming.SafeIdentifier(clause.VariableName);
            if (!string.IsNullOrEmpty(clause.ExceptionType) && clause.ExceptionType != "Exception" && clause.ExceptionType != "Throwable")
            {
                sb.AppendLine($"{ind}} catch ({exVar}) {{");
                sb.AppendLine($"{ind}{_indent}if (!({exVar} instanceof {clause.ExceptionType})) throw {exVar};");
            }
            else
            {
                sb.AppendLine($"{ind}} catch ({exVar}) {{");
            }
            sb.Append(Emit(clause.Body, indentLevel + 1));
        }

        if (tryCatch.FinallyBlock != null)
        {
            sb.AppendLine();
            sb.Append($"{ind}finally ");
            sb.Append(Emit(tryCatch.FinallyBlock, indentLevel));
        }
        return sb.ToString();
    }

    private string EmitSwitch(JavaSwitchStatement sw, int indentLevel)
    {
        var ind = new string(_indent[0], indentLevel * _indent.Length);
        var sb = new StringBuilder();
        sb.AppendLine($"{ind}switch ({_exprWriter.ToInlineString(sw.Expression)}) {{");
        foreach (var section in sw.Sections)
        {
            foreach (var label in section.Labels)
            {
                sb.AppendLine($"{ind}{_indent}{label}:");
            }
            foreach (var s in section.Statements)
            {
                sb.AppendLine(Emit(s, indentLevel + 2));
            }
        }
        sb.Append($"{ind}}}");
        return sb.ToString();
    }
}
```

注：需对照 `JavaStatement.cs` 实际属性名调整（如 `JavaForEachStatement.Collection` vs `Iterable`、`JavaForStatement` 的字段名等）。实现前先 Read 该文件确认。

- [ ] **Step 4: 运行测试验证通过**

Run: `dotnet test tests/CSharpToTypeScript.Tests/CSharpToTypeScript.Tests.csproj --filter "FullyQualifiedName~TsStatementWriterTests"`
Expected: PASS（9 个测试）。

- [ ] **Step 5: 提交**

```bash
git add src/CSharpToJava.Core/TypeScript/TsStatementWriter.cs tests/CSharpToTypeScript.Tests/TsStatementWriterTests.cs
git commit -m "feat(ts): add TsStatementWriter for control flow emission"
```

---

## Task 9: TsMemberWriter + TsTypeWriter — 成员与类型发射

**Files:**
- Create: `src/CSharpToJava.Core/TypeScript/TsMemberWriter.cs`
- Create: `src/CSharpToJava.Core/TypeScript/TsTypeWriter.cs`

- [ ] **Step 1: 写失败测试**

创建 `tests/CSharpToTypeScript.Tests/TsBasicClassEmissionTests.cs`：

```csharp
using CSharpToJava.Core.Java;
using CSharpToJava.Core.TypeScript;

namespace CSharpToTypeScript.Tests;

public class TsBasicClassEmissionTests
{
    private static string EmitType(JavaTypeDeclaration type)
    {
        var table = new TsMemberMappingTable(null, null);
        var imports = new TsImportCollector();
        var emitter = new TypeScriptEmitter();
        return emitter.EmitType(type, table, imports);
    }

    [Fact]
    public void Empty_Class_Emits_Export_Class()
    {
        var cls = new JavaClassDeclaration { Name = "Foo", Modifiers = JavaModifiers.Public };
        var result = EmitType(cls);
        Assert.Contains("export class Foo {", result);
        Assert.Contains("}", result);
    }

    [Fact]
    public void Class_With_Field_Emits_Field_With_Type()
    {
        var cls = new JavaClassDeclaration { Name = "Foo", Modifiers = JavaModifiers.Public };
        cls.Fields.Add(new JavaFieldDeclaration
        {
            Modifiers = JavaModifiers.Public,
            Type = "number",
            Name = "X",
        });
        var result = EmitType(cls);
        Assert.Contains("public X: number;", result);
    }

    [Fact]
    public void Field_Without_Initializer_Adds_Default_Zero()
    {
        // 无显式初始化器的字段必须补默认零值（spec §2.5 成员表）
        var cls = new JavaClassDeclaration { Name = "Foo", Modifiers = JavaModifiers.Public };
        cls.Fields.Add(new JavaFieldDeclaration
        {
            Modifiers = JavaModifiers.Public,
            Type = "number",
            Name = "Count",
            // 无 Initializer
        });
        var result = EmitType(cls);
        Assert.Contains("public Count: number = 0;", result);
    }

    [Fact]
    public void Bool_Field_Default_False()
    {
        var cls = new JavaClassDeclaration { Name = "Foo", Modifiers = JavaModifiers.Public };
        cls.Fields.Add(new JavaFieldDeclaration
        {
            Modifiers = JavaModifiers.Public,
            Type = "boolean",
            Name = "Flag",
        });
        var result = EmitType(cls);
        Assert.Contains("public Flag: boolean = false;", result);
    }

    [Fact]
    public void String_Field_Default_Null()
    {
        var cls = new JavaClassDeclaration { Name = "Foo", Modifiers = JavaModifiers.Public };
        cls.Fields.Add(new JavaFieldDeclaration
        {
            Modifiers = JavaModifiers.Public,
            Type = "string",
            Name = "Name",
        });
        var result = EmitType(cls);
        Assert.Contains("public Name: string = null;", result);
    }

    [Fact]
    public void Method_Emits_With_Body()
    {
        var cls = new JavaClassDeclaration { Name = "Foo", Modifiers = JavaModifiers.Public };
        cls.Methods.Add(new JavaMethodDeclaration
        {
            Modifiers = JavaModifiers.Public,
            ReturnType = "number",
            Name = "GetX",
            Body = "return this.X;",
        });
        var result = EmitType(cls);
        Assert.Contains("public GetX(): number {", result);
        Assert.Contains("return this.X;", result);
    }

    [Fact]
    public void Constructor_Emits_Constructor_Keyword()
    {
        var cls = new JavaClassDeclaration { Name = "Foo", Modifiers = JavaModifiers.Public };
        cls.Constructors.Add(new JavaConstructorDeclaration
        {
            Modifiers = JavaModifiers.Public,
            ClassName = "Foo",
            Body = "this.X = 0;",
        });
        var result = EmitType(cls);
        Assert.Contains("constructor() {", result);
    }

    [Fact]
    public void Static_Field_Emits_Static()
    {
        var cls = new JavaClassDeclaration { Name = "Foo", Modifiers = JavaModifiers.Public };
        cls.Fields.Add(new JavaFieldDeclaration
        {
            Modifiers = JavaModifiers.Public | JavaModifiers.Static | JavaModifiers.Final,
            Type = "number",
            Name = "PI",
            Initializer = "3.14",
        });
        var result = EmitType(cls);
        Assert.Contains("public static readonly PI: number = 3.14;", result);
    }

    [Fact]
    public void Abstract_Method_Emits_Abstract()
    {
        var cls = new JavaClassDeclaration { Name = "Foo", Modifiers = JavaModifiers.Public | JavaModifiers.Abstract };
        cls.Methods.Add(new JavaMethodDeclaration
        {
            Modifiers = JavaModifiers.Public | JavaModifiers.Abstract,
            ReturnType = "number",
            Name = "Compute",
        });
        var result = EmitType(cls);
        Assert.Contains("export abstract class Foo", result);
        Assert.Contains("public abstract Compute(): number;", result);
    }

    [Fact]
    public void Interface_Emits_Export_Interface()
    {
        var iface = new JavaInterfaceDeclaration { Name = "IFoo", Modifiers = JavaModifiers.Public };
        iface.Methods.Add(new JavaMethodDeclaration
        {
            Modifiers = JavaModifiers.Public,
            ReturnType = "number",
            Name = "Bar",
        });
        var result = EmitType(iface);
        Assert.Contains("export interface IFoo {", result);
        Assert.Contains("Bar(): number;", result);
    }

    [Fact]
    public void Enum_Emits_Export_Enum()
    {
        var en = new JavaEnumDeclaration { Name = "Color", Modifiers = JavaModifiers.Public };
        en.Members.Add("Red");
        en.Members.Add("Green");
        en.Members.Add("Blue");
        var result = EmitType(en);
        Assert.Contains("export enum Color {", result);
        Assert.Contains("Red,", result);
        Assert.Contains("Green,", result);
        Assert.Contains("Blue", result);
    }
}
```

- [ ] **Step 2: 运行测试验证失败**

Run: `dotnet test tests/CSharpToTypeScript.Tests/CSharpToTypeScript.Tests.csproj --filter "FullyQualifiedName~TsBasicClassEmissionTests"`
Expected: FAIL（TypeScriptEmitter.EmitType 不存在）。

- [ ] **Step 3: 实现 TsMemberWriter**

```csharp
using System.Text;
using CSharpToJava.Core.Java;

namespace CSharpToJava.Core.TypeScript;

/// <summary>
/// 成员发射器：字段/方法/构造器/属性发射为 TS（spec §2.5 成员部分）。
/// </summary>
public sealed class TsMemberWriter
{
    private readonly TsStatementWriter _stmtWriter;
    private readonly string _indent;

    public TsMemberWriter(TsStatementWriter stmtWriter, string indent = "    ")
    {
        _stmtWriter = stmtWriter;
        _indent = indent;
    }

    public string EmitField(JavaFieldDeclaration field, int indentLevel)
    {
        var ind = new string(_indent[0], indentLevel * _indent.Length);
        var sb = new StringBuilder(ind);

        // 修饰符
        if ((field.Modifiers & JavaModifiers.Public) != 0) sb.Append("public ");
        else if ((field.Modifiers & JavaModifiers.Protected) != 0) sb.Append("protected ");
        else if ((field.Modifiers & JavaModifiers.Private) != 0) sb.Append("private ");
        if ((field.Modifiers & JavaModifiers.Static) != 0) sb.Append("static ");
        if ((field.Modifiers & JavaModifiers.Final) != 0) sb.Append("readonly ");

        var name = TsNaming.SafeIdentifier(field.Name);
        sb.Append($"{name}: {field.Type}");

        // 初始化器：有则用，无则补默认零值（spec §2.5：tsc strictPropertyInitialization 要求）
        if (!string.IsNullOrEmpty(field.Initializer))
        {
            sb.Append($" = {field.Initializer}");
        }
        else
        {
            sb.Append($" = {DefaultValueForType(field.Type)}");
        }

        sb.Append(';');
        return sb.ToString();
    }

    public string EmitMethod(JavaMethodDeclaration method, int indentLevel)
    {
        var ind = new string(_indent[0], indentLevel * _indent.Length);
        var sb = new StringBuilder(ind);

        if ((method.Modifiers & JavaModifiers.Public) != 0) sb.Append("public ");
        else if ((method.Modifiers & JavaModifiers.Protected) != 0) sb.Append("protected ");
        else if ((method.Modifiers & JavaModifiers.Private) != 0) sb.Append("private ");
        if ((method.Modifiers & JavaModifiers.Static) != 0) sb.Append("static ");
        if ((method.Modifiers & JavaModifiers.Abstract) != 0) sb.Append("abstract ");

        // 类型参数
        if (method.TypeParameters.Count > 0)
        {
            sb.Append('<');
            sb.Append(string.Join(", ", method.TypeParameters.Select(tp => tp.ToString())));
            sb.Append('>');
        }

        var name = TsNaming.SafeIdentifier(method.Name);
        sb.Append($"{name}(");

        // 参数
        var parms = method.Parameters.Select(p =>
        {
            var pName = TsNaming.SafeIdentifier(p.Name);
            return $"{pName}: {p.Type}";
        });
        sb.Append(string.Join(", ", parms));

        sb.Append($"): {method.ReturnType}");

        if ((method.Modifiers & JavaModifiers.Abstract) != 0 || method.Body == null)
        {
            sb.Append(';');
        }
        else if (method.IsBodyExpression)
        {
            sb.Append($" => {method.Body};");
        }
        else
        {
            sb.AppendLine(" {");
            // body 已是字符串，按行缩进
            var bodyLines = method.Body.Split('\n');
            foreach (var line in bodyLines)
            {
                sb.Append(ind).Append(_indent).AppendLine(line);
            }
            sb.Append(ind).Append('}');
        }

        return sb.ToString();
    }

    public string EmitConstructor(JavaConstructorDeclaration ctor, int indentLevel)
    {
        var ind = new string(_indent[0], indentLevel * _indent.Length);
        var sb = new StringBuilder(ind);

        if ((ctor.Modifiers & JavaModifiers.Public) != 0) sb.Append("public ");
        else if ((ctor.Modifiers & JavaModifiers.Protected) != 0) sb.Append("protected ");
        else if ((ctor.Modifiers & JavaModifiers.Private) != 0) sb.Append("private ");

        sb.Append("constructor(");
        var parms = ctor.Parameters.Select(p => $"{TsNaming.SafeIdentifier(p.Name)}: {p.Type}");
        sb.Append(string.Join(", ", parms));
        sb.Append(')');

        if (!string.IsNullOrEmpty(ctor.BaseConstructorCall))
        {
            sb.Append($" : {ctor.BaseConstructorCall}");
        }

        sb.AppendLine(" {");
        if (!string.IsNullOrEmpty(ctor.Body))
        {
            var bodyLines = ctor.Body.Split('\n');
            foreach (var line in bodyLines)
            {
                sb.Append(ind).Append(_indent).AppendLine(line);
            }
        }
        sb.Append(ind).Append('}');
        return sb.ToString();
    }

    /// <summary>
    /// 返回 TS 类型的默认零值（spec §2.5 字段默认零值规则）。
    /// </summary>
    public static string DefaultValueForType(string tsType)
    {
        if (string.IsNullOrEmpty(tsType)) return "null";

        // 可空类型 X | null -> null
        if (tsType.Contains("| null") || tsType.Contains("null |")) return "null";

        // 数组 -> []
        if (tsType.EndsWith("[]")) return "[]";

        return tsType switch
        {
            "number" => "0",
            "bigint" => "0n",
            "boolean" => "false",
            "string" => "null",  // C# string 默认 null，不用 ""
            "void" => "undefined",
            "any" => "null",
            "null" => "null",
            "undefined" => "undefined",
            _ => "null",  // 引用类型默认 null
        };
    }
}
```

- [ ] **Step 4: 实现 TsTypeWriter**

```csharp
using System.Text;
using CSharpToJava.Core.Java;

namespace CSharpToJava.Core.TypeScript;

/// <summary>
/// 类型声明发射器：class/interface/enum 发射为 TS（spec §2.5 类型声明部分）。
/// </summary>
public sealed class TsTypeWriter
{
    private readonly TsMemberWriter _memberWriter;
    private readonly string _indent;

    public TsTypeWriter(TsMemberWriter memberWriter, string indent = "    ")
    {
        _memberWriter = memberWriter;
        _indent = indent;
    }

    public string Emit(JavaTypeDeclaration type, int indentLevel = 0)
    {
        return type switch
        {
            JavaClassDeclaration cls => EmitClass(cls, indentLevel),
            JavaInterfaceDeclaration iface => EmitInterface(iface, indentLevel),
            JavaEnumDeclaration en => EmitEnum(en, indentLevel),
            _ => $"/* cs2j: unsupported type {type.GetType().Name} */",
        };
    }

    private string EmitClass(JavaClassDeclaration cls, int indentLevel)
    {
        var ind = new string(_indent[0], indentLevel * _indent.Length);
        var sb = new StringBuilder(ind);

        // export
        sb.Append("export ");

        // abstract
        if ((cls.Modifiers & JavaModifiers.Abstract) != 0) sb.Append("abstract ");

        // sealed -> 注释标记（spec §2.5）
        if ((cls.Modifiers & JavaModifiers.Sealed) != 0) sb.Append("/* sealed */ ");

        sb.Append("class ");
        sb.Append(TsNaming.SafeClassName(cls.Name));

        // 类型参数
        if (cls.TypeParameters.Count > 0)
        {
            sb.Append('<');
            sb.Append(string.Join(", ", cls.TypeParameters.Select(tp => tp.ToString())));
            sb.Append('>');
        }

        // 基类
        if (!string.IsNullOrEmpty(cls.ExtendedType))
        {
            sb.Append($" extends {cls.ExtendedType}");
        }

        // 接口
        if (cls.ImplementedTypes.Count > 0)
        {
            sb.Append($" implements {string.Join(", ", cls.ImplementedTypes)}");
        }

        sb.AppendLine(" {");

        // 字段
        foreach (var field in cls.Fields)
        {
            sb.AppendLine(_memberWriter.EmitField(field, indentLevel + 1));
        }

        if (cls.Fields.Count > 0 && (cls.Constructors.Count > 0 || cls.Methods.Count > 0))
        {
            sb.AppendLine();
        }

        // 构造器
        foreach (var ctor in cls.Constructors)
        {
            sb.AppendLine(_memberWriter.EmitConstructor(ctor, indentLevel + 1));
            sb.AppendLine();
        }

        // 方法
        for (int i = 0; i < cls.Methods.Count; i++)
        {
            sb.AppendLine(_memberWriter.EmitMethod(cls.Methods[i], indentLevel + 1));
            if (i < cls.Methods.Count - 1) sb.AppendLine();
        }

        // 静态初始化块 -> static { } （ES2022）
        foreach (var staticInit in cls.StaticInitializers)
        {
            sb.Append(ind).Append(_indent).AppendLine("static {");
            sb.AppendLine(staticInit.Body);
            sb.Append(ind).Append(_indent).Append('}');
        }

        // 嵌套类型
        foreach (var nested in cls.NestedTypes)
        {
            sb.AppendLine();
            sb.AppendLine(Emit(nested, indentLevel + 1));
        }

        sb.Append(ind).Append('}');
        return sb.ToString();
    }

    private string EmitInterface(JavaInterfaceDeclaration iface, int indentLevel)
    {
        var ind = new string(_indent[0], indentLevel * _indent.Length);
        var sb = new StringBuilder(ind);

        sb.Append("export interface ");
        sb.Append(TsNaming.SafeClassName(iface.Name));

        // 类型参数
        if (iface.TypeParameters.Count > 0)
        {
            sb.Append('<');
            sb.Append(string.Join(", ", iface.TypeParameters.Select(tp => tp.ToString())));
            sb.Append('>');
        }

        // 继承的接口
        if (iface.ImplementedTypes.Count > 0)
        {
            sb.Append($" extends {string.Join(", ", iface.ImplementedTypes)}");
        }

        sb.AppendLine(" {");

        // 成员（接口无修饰符，无 body）
        foreach (var method in iface.Methods)
        {
            var parms = string.Join(", ", method.Parameters.Select(p => $"{TsNaming.SafeIdentifier(p.Name)}: {p.Type}"));
            sb.Append(ind).Append(_indent)
              .AppendLine($"{TsNaming.SafeIdentifier(method.Name)}({parms}): {method.ReturnType};");
        }

        sb.Append(ind).Append('}');
        return sb.ToString();
    }

    private string EmitEnum(JavaEnumDeclaration en, int indentLevel)
    {
        var ind = new string(_indent[0], indentLevel * _indent.Length);
        var sb = new StringBuilder(ind);

        // 普通 enum 直译（[Flags] 在 M2 处理）
        sb.AppendLine($"export enum {TsNaming.SafeClassName(en.Name)} {{");
        for (int i = 0; i < en.Members.Count; i++)
        {
            var comma = i < en.Members.Count - 1 ? "," : "";
            sb.Append(ind).Append(_indent).AppendLine($"{en.Members[i]}{comma}");
        }
        sb.Append(ind).Append('}');
        return sb.ToString();
    }
}
```

注：需对照 `JavaTypeDeclaration.cs` 实际属性名（如 `JavaEnumDeclaration.Members`、`JavaInterfaceDeclaration.ImplementedTypes` 等）。实现前先 Read 该文件确认。

- [ ] **Step 5: 运行测试验证通过**

Run: `dotnet test tests/CSharpToTypeScript.Tests/CSharpToTypeScript.Tests.csproj --filter "FullyQualifiedName~TsBasicClassEmissionTests"`
Expected: PASS（11 个测试）。

- [ ] **Step 6: 提交**

```bash
git add src/CSharpToJava.Core/TypeScript/TsMemberWriter.cs src/CSharpToJava.Core/TypeScript/TsTypeWriter.cs tests/CSharpToTypeScript.Tests/TsBasicClassEmissionTests.cs
git commit -m "feat(ts): add TsMemberWriter and TsTypeWriter for class/interface/enum emission"
```

---

## Task 10: TypeScriptEmitter — 总入口

**Files:**
- Create: `src/CSharpToJava.Core/TypeScript/TypeScriptEmitter.cs`

- [ ] **Step 1: 写失败测试**

创建 `tests/CSharpToTypeScript.Tests/TsEmitterIntegrationTests.cs`：

```csharp
using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToTypeScript.Tests;

public class TsEmitterIntegrationTests : TsConversionTestBase
{
    [Fact]
    public void Empty_Class_Converts_To_Ts()
    {
        var result = Convert("""
class Foo { }
""");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("export class Foo", result.GeneratedCode);
        Assert.Contains("}", result.GeneratedCode);
    }

    [Fact]
    public void Class_With_Field_And_Method_Converts()
    {
        var result = Convert("""
class Point
{
    int x;
    int y;

    int Sum()
    {
        return x + y;
    }
}
""");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("export class Point", result.GeneratedCode);
        Assert.Contains("x: number = 0", result.GeneratedCode);
        Assert.Contains("y: number = 0", result.GeneratedCode);
        Assert.Contains("Sum(): number", result.GeneratedCode);
    }

    [Fact]
    public void Int_Field_Gets_Number_Type()
    {
        var result = Convert("""
class Sample
{
    int value = 42;
}
""");
        Assert.True(result.Success);
        Assert.Contains("value: number = 42", result.GeneratedCode);
    }

    [Fact]
    public void Long_Literal_Gets_Bigint_Suffix()
    {
        var result = Convert("""
class Sample
{
    long ticks = 100L;
}
""");
        Assert.True(result.Success);
        Assert.Contains("ticks: bigint = 100n", result.GeneratedCode);
    }

    [Fact]
    public void String_Field_Gets_String_Type()
    {
        var result = Convert("""
class Sample
{
    string name = "test";
}
""");
        Assert.True(result.Success);
        Assert.Contains("name: string = \"test\"", result.GeneratedCode);
    }

    [Fact]
    public void Bool_Field_Gets_Boolean_Type()
    {
        var result = Convert("""
class Sample
{
    bool flag = true;
}
""");
        Assert.True(result.Success);
        Assert.Contains("flag: boolean = true", result.GeneratedCode);
    }

    [Fact]
    public void Void_Method_Gets_Void_Return()
    {
        var result = Convert("""
class Sample
{
    void DoNothing() { }
}
""");
        Assert.True(result.Success);
        Assert.Contains("DoNothing(): void", result.GeneratedCode);
    }

    [Fact]
    public void If_Statement_Emits_Correctly()
    {
        var result = Convert("""
class Sample
{
    int Check(int x)
    {
        if (x > 0)
        {
            return 1;
        }
        else
        {
            return 0;
        }
    }
}
""");
        Assert.True(result.Success);
        Assert.Contains("if (x > 0)", result.GeneratedCode);
        Assert.Contains("} else {", result.GeneratedCode);
    }

    [Fact]
    public void ForEach_Emits_ForOf()
    {
        var result = Convert("""
class Sample
{
    int Sum(int[] arr)
    {
        int total = 0;
        foreach (var item in arr)
        {
            total = total + item;
        }
        return total;
    }
}
""");
        Assert.True(result.Success);
        Assert.Contains("for (const item of arr)", result.GeneratedCode);
    }

    [Fact]
    public void Console_WriteLine_Maps_To_ConsoleLog()
    {
        var result = Convert("""
class Sample
{
    void Greet()
    {
        Console.WriteLine("hello");
    }
}
""");
        Assert.True(result.Success);
        Assert.Contains("console.log(\"hello\")", result.GeneratedCode);
    }
}
```

- [ ] **Step 2: 运行测试验证失败**

Run: `dotnet test tests/CSharpToTypeScript.Tests/CSharpToTypeScript.Tests.csproj --filter "FullyQualifiedName~TsEmitterIntegrationTests"`
Expected: FAIL（管线未分叉，仍输出 Java）。

- [ ] **Step 3: 实现 TypeScriptEmitter**

```csharp
using System.Text;
using CSharpToJava.Core.Context;
using CSharpToJava.Core.Java;

namespace CSharpToJava.Core.TypeScript;

/// <summary>
/// TypeScript 发射器总入口：遍历 JavaCompilationUnit IR，编排各 writer 输出 .ts 文本。
/// spec §2.5 — IR 节点级发射映射。
/// </summary>
public sealed class TypeScriptEmitter
{
    public const string FileExtension = ".ts";

    public string Emit(JavaCompilationUnit unit, ConversionContext context)
    {
        var mappingPath = context.Options.TypeScriptMappingConfigPath;
        var memberPath = context.Options.TypeScriptMemberMappingPath;
        var mappings = new TsMemberMappingTable(mappingPath, memberPath);
        var imports = new TsImportCollector(unit.Package);

        var sb = new StringBuilder();

        // 发射类型声明，收集 imports
        var typeBodies = new StringBuilder();
        foreach (var type in unit.TypeDeclarations)
        {
            typeBodies.AppendLine(EmitType(type, mappings, imports));
            typeBodies.AppendLine();
        }

        // 头部：ESM imports
        var importBlock = imports.EmitImports();
        if (!string.IsNullOrEmpty(importBlock))
        {
            sb.Append(importBlock);
            sb.AppendLine();
        }

        sb.Append(typeBodies);
        return sb.ToString();
    }

    /// <summary>
    /// 发射单个类型声明（供测试直接调用）。
    /// </summary>
    public string EmitType(JavaTypeDeclaration type, TsMemberMappingTable mappings, TsImportCollector imports)
    {
        var exprWriter = new TsExpressionWriter(mappings, imports);
        var stmtWriter = new TsStatementWriter(exprWriter);
        var memberWriter = new TsMemberWriter(stmtWriter);
        var typeWriter = new TsTypeWriter(memberWriter);
        return typeWriter.Emit(type, 0);
    }
}
```

- [ ] **Step 4: 运行测试验证仍失败（管线未分叉）**

Run: `dotnet test tests/CSharpToTypeScript.Tests/CSharpToTypeScript.Tests.csproj --filter "FullyQualifiedName~TsEmitterIntegrationTests"`
Expected: 仍 FAIL（管线未分叉，SingleFileJavaEmitPass 仍输出 Java）。

- [ ] **Step 5: 提交（发射器就绪，待管线分叉）**

```bash
git add src/CSharpToJava.Core/TypeScript/TypeScriptEmitter.cs tests/CSharpToTypeScript.Tests/TsEmitterIntegrationTests.cs
git commit -m "feat(ts): add TypeScriptEmitter entry point"
```

---

## Task 11: SingleFileTsEmitPass + 管线分叉

**Files:**
- Modify: `src/CSharpToJava.Core/Pipeline/Passes/SingleFilePasses.cs`
- Modify: `src/CSharpToJava.Core/Pipeline/ConversionPipeline.cs`

- [ ] **Step 1: 实现 SingleFileTsEmitPass**

在 `src/CSharpToJava.Core/Pipeline/Passes/SingleFilePasses.cs` 末尾追加：

```csharp
public sealed class SingleFileTsEmitPass : ICs2jPass<SingleFilePassState>
{
    private readonly IReadOnlyList<Java.JavaSyntaxRewriter> _irRewriters;

    public SingleFileTsEmitPass(IReadOnlyList<Java.JavaSyntaxRewriter> irRewriters)
    {
        _irRewriters = irRewriters;
    }

    public string Name => nameof(SingleFileTsEmitPass);
    public Cs2jPassStage Stage => Cs2jPassStage.Emit;

    public void Execute(SingleFilePassState state)
    {
        if (state.BlockEmit)
        {
            state.EmitSucceeded = false;
            state.GeneratedCode = string.Empty;
            return;
        }

        // 1. 生成 Java IR（与 Java 路径一致）
        var visitor = new CSharpToJavaVisitor(state.Context);
        var compilationUnit = visitor.Visit(state.SyntaxTree.GetRoot());
        if (compilationUnit is not Java.JavaCompilationUnit javaCompilation)
        {
            state.EmitSucceeded = false;
            return;
        }

        // 2. 运行用户注册的 IR rewriter（目标无关）
        foreach (var rewriter in _irRewriters)
        {
            rewriter.VisitCompilationUnit(javaCompilation);
        }

        // 3. 运行 TS 专属 rewriter（M3 实现，M1 暂空）
        RunTsRewriters(javaCompilation, state.Context);

        // 4. TS 发射
        var emitter = new TypeScript.TypeScriptEmitter();
        var code = emitter.Emit(javaCompilation, state.Context);

        state.GeneratedCode = code;
        state.JavaCompilation = javaCompilation;
        state.EmitSucceeded = true;
    }

    private static void RunTsRewriters(Java.JavaCompilationUnit compilation, ConversionContext context)
    {
        // M1: 暂无 TS rewriter；M3 在此装配
        // TsOverloadDisambiguationRewriter, TsGenericErasureRewriter,
        // TsInterfaceProbeRewriter, TsEqualityRewriter
    }
}
```

在文件顶部 using 区添加：

```csharp
using CSharpToJava.Core.TypeScript;
```

- [ ] **Step 2: 修改 ConversionPipeline.CreateSingleFilePasses 按目标分叉**

在 `src/CSharpToJava.Core/Pipeline/ConversionPipeline.cs` 第 402 行 `CreateSingleFilePasses` 方法改为：

```csharp
private IReadOnlyList<ICs2jPass<SingleFilePassState>> CreateSingleFilePasses()
{
    if (_options.TargetLanguage == Context.TargetLanguage.TypeScript)
    {
        return new ICs2jPass<SingleFilePassState>[]
        {
            new SingleFileLinqDesugarPass(),
            new SingleFileCompilationCheckPass(),
            new SingleFileUnsupportedDomainCheckPass(),
            new SingleFilePlatformBoundaryCheckPass(),
            new SingleFileNativeInteropCheckPass(),
            new SingleFileContextNormalizationPass(),
            new SingleFileTsEmitPass(_irRewriters),
        };
    }

    return new ICs2jPass<SingleFilePassState>[]
    {
        new SingleFileLinqDesugarPass(),
        new SingleFileCompilationCheckPass(),
        new SingleFileUnsupportedDomainCheckPass(),
        new SingleFilePlatformBoundaryCheckPass(),
        new SingleFileNativeInteropCheckPass(),
        new SingleFileContextNormalizationPass(),
        new SingleFileJavaEmitPass(_irRewriters),
    };
}
```

注：`CreateSingleFilePasses` 当前是无参实例方法，但 `_options` 是 `ConversionPipeline` 的字段吗？查看 ConversionPipeline 类——它没有 `_options` 字段（与 ProjectConversionPipeline 不同）。需要调整：`ConversionPipeline.Convert` 方法内已构造 `context`，可改为将 `request.Options.TargetLanguage` 传入 `CreateSingleFilePasses`。

修正——将方法签名改为：

```csharp
private IReadOnlyList<ICs2jPass<SingleFilePassState>> CreateSingleFilePasses(ConversionOptions options)
```

调用处（第 222 行）改为：

```csharp
Cs2jPassExecutor.Execute(passState, context, CreateSingleFilePasses(request.Options), passMetrics);
```

- [ ] **Step 3: 运行测试验证通过**

Run: `dotnet test tests/CSharpToTypeScript.Tests/CSharpToTypeScript.Tests.csproj --filter "FullyQualifiedName~TsEmitterIntegrationTests"`
Expected: PASS（10 个测试）。

- [ ] **Step 4: 运行 Java 零回归测试**

Run: `dotnet test tests/CSharpToJava.Tests/CSharpToJava.Tests.csproj`
Expected: 全部 PASS（Java 路径未受影响）。

- [ ] **Step 5: 提交**

```bash
git add src/CSharpToJava.Core/Pipeline/Passes/SingleFilePasses.cs src/CSharpToJava.Core/Pipeline/ConversionPipeline.cs
git commit -m "feat(ts): fork pipeline at Codegen layer with SingleFileTsEmitPass"
```

---

## Task 12: CLI 新增 --target 选项

**Files:**
- Modify: `src/CSharpToJava.CLI/Program.cs`

- [ ] **Step 1: 写失败测试**

创建 `tests/CSharpToTypeScript.Tests/TsCliTargetOptionTests.cs`：

```csharp
using CSharpToJava.CLI;

namespace CSharpToTypeScript.Tests;

public class TsCliTargetOptionTests
{
    [Fact]
    public void ConvertOptions_Default_Target_Is_Java()
    {
        var opts = new ConvertOptions();
        Assert.Equal("java", opts.Target);
    }

    [Fact]
    public void ConvertOptions_Accepts_TypeScript_Target()
    {
        // CommandLineParser 解析 --target typescript
        var opts = new ConvertOptions { Target = "typescript" };
        Assert.Equal("typescript", opts.Target);
    }
}
```

注：`ConvertOptions` 是 internal class，需在 `CSharpToJava.CLI.csproj` 加 `InternalsVisibleTo` 或将测试移到 CLI 项目内。简化：在 `Program.cs` 顶部加：

```csharp
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("CSharpToTypeScript.Tests")]
```

- [ ] **Step 2: 运行测试验证失败**

Run: `dotnet test tests/CSharpToTypeScript.Tests/CSharpToTypeScript.Tests.csproj --filter "FullyQualifiedName~TsCliTargetOptionTests"`
Expected: FAIL（`Target` 属性不存在）。

- [ ] **Step 3: 在 ConvertOptions 和 ConvertProjectOptions 添加 --target**

在 `src/CSharpToJava.CLI/Program.cs` 的 `ConvertOptions` 类（第 2029 行）添加：

```csharp
[Option('t', "target", Default = "java", HelpText = "Target language: java or typescript")]
public string Target { get; set; } = "java";
```

在 `ConvertProjectOptions` 类（第 2081 行）同样添加。

- [ ] **Step 4: 在 ConvertFile/ConvertProject 方法中传递 TargetLanguage**

在 `ConvertFile` 方法（第 46 行）的 `ConversionOptions` 初始化处添加：

```csharp
TargetLanguage = opts.Target.Equals("typescript", StringComparison.OrdinalIgnoreCase)
    ? Context.TargetLanguage.TypeScript
    : Context.TargetLanguage.Java,
```

并在 TypeScript 目标时设置默认映射路径：

```csharp
TypeScriptMappingConfigPath = opts.Target.Equals("typescript", StringComparison.OrdinalIgnoreCase)
    ? (opts.MappingConfig ?? ResolveTypeScriptTypeMappingPath())
    : null,
TypeScriptMemberMappingPath = opts.Target.Equals("typescript", StringComparison.OrdinalIgnoreCase)
    ? ResolveTypeScriptMemberMappingPath()
    : null,
```

在文件末尾添加辅助方法：

```csharp
private static string? ResolveTypeScriptTypeMappingPath()
{
    var basePath = AppContext.BaseDirectory;
    var path = Path.Combine(basePath, "config", "typescript", "TypeMappings.json");
    return File.Exists(path) ? path : null;
}

private static string? ResolveTypeScriptMemberMappingPath()
{
    var basePath = AppContext.BaseDirectory;
    var path = Path.Combine(basePath, "config", "typescript", "MemberMappings.json");
    return File.Exists(path) ? path : null;
}
```

对 `ConvertProject` 方法做同样修改。

- [ ] **Step 5: 在 CLI csproj 中复制 TS 配置**

在 `src/CSharpToJava.CLI/CSharpToJava.CLI.csproj` 添加：

```xml
<ItemGroup>
  <Content Include="..\..\config\typescript\TypeMappings.json">
    <Link>config\typescript\TypeMappings.json</Link>
    <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
  </Content>
  <Content Include="..\..\config\typescript\MemberMappings.json">
    <Link>config\typescript\MemberMappings.json</Link>
    <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
  </Content>
</ItemGroup>
```

并添加 `InternalsVisibleTo`：

```xml
<ItemGroup>
  <InternalsVisibleTo Include="CSharpToTypeScript.Tests" />
</ItemGroup>
```

- [ ] **Step 6: 运行测试验证通过**

Run: `dotnet test tests/CSharpToTypeScript.Tests/CSharpToTypeScript.Tests.csproj --filter "FullyQualifiedName~TsCliTargetOptionTests"`
Expected: PASS（2 个测试）。

- [ ] **Step 7: 手动端到端验证**

Run:
```bash
dotnet build src/CSharpToJava.CLI/CSharpToJava.CLI.csproj
dotnet run --project src/CSharpToJava.CLI/CSharpToJava.CLI.csproj -- convert -i tests/SampleProject/Sample.cs -t typescript
```
Expected: 输出包含 `export class Sample` 的 TypeScript 代码。

- [ ] **Step 8: 提交**

```bash
git add src/CSharpToJava.CLI/Program.cs src/CSharpToJava.CLI/CSharpToJava.CLI.csproj tests/CSharpToTypeScript.Tests/TsCliTargetOptionTests.cs
git commit -m "feat(ts): add --target CLI option for TypeScript conversion"
```

---

## Task 13: M1 控制流与字面量测试补全

**Files:**
- Create: `tests/CSharpToTypeScript.Tests/TsControlFlowEmissionTests.cs`
- Create: `tests/CSharpToTypeScript.Tests/TsLiteralEmissionTests.cs`
- Create: `tests/CSharpToTypeScript.Tests/TsPrimitiveTypeMappingTests.cs`

- [ ] **Step 1: 写 TsControlFlowEmissionTests**

```csharp
namespace CSharpToTypeScript.Tests;

public class TsControlFlowEmissionTests : TsConversionTestBase
{
    [Fact]
    public void While_Loop_Emits_While()
    {
        var result = Convert("""
class Sample
{
    int Count(int n)
    {
        int i = 0;
        while (i < n)
        {
            i = i + 1;
        }
        return i;
    }
}
""");
        Assert.True(result.Success);
        Assert.Contains("while (i < n)", result.GeneratedCode);
    }

    [Fact]
    public void For_Loop_Emits_For()
    {
        var result = Convert("""
class Sample
{
    int Sum(int n)
    {
        int total = 0;
        for (int i = 0; i < n; i = i + 1)
        {
            total = total + i;
        }
        return total;
    }
}
""");
        Assert.True(result.Success);
        Assert.Contains("for (let i: number = 0; i < n; i = i + 1)", result.GeneratedCode);
    }

    [Fact]
    public void Do_While_Emits_DoWhile()
    {
        var result = Convert("""
class Sample
{
    int Run()
    {
        int x = 0;
        do
        {
            x = x + 1;
        } while (x < 10);
        return x;
    }
}
""");
        Assert.True(result.Success);
        Assert.Contains("do {", result.GeneratedCode);
        Assert.Contains("} while (x < 10);", result.GeneratedCode);
    }

    [Fact]
    public void Switch_Emits_Switch_Case()
    {
        var result = Convert("""
class Sample
{
    string Describe(int n)
    {
        switch (n)
        {
            case 1:
                return "one";
            case 2:
                return "two";
            default:
                return "other";
        }
    }
}
""");
        Assert.True(result.Success);
        Assert.Contains("switch (n)", result.GeneratedCode);
        Assert.Contains("case 1:", result.GeneratedCode);
        Assert.Contains("default:", result.GeneratedCode);
    }

    [Fact]
    public void Return_Without_Value_Emits_Bare_Return()
    {
        var result = Convert("""
class Sample
{
    void EarlyExit(bool flag)
    {
        if (flag)
        {
            return;
        }
    }
}
""");
        Assert.True(result.Success);
        Assert.Contains("return;", result.GeneratedCode);
    }
}
```

- [ ] **Step 2: 写 TsLiteralEmissionTests**

```csharp
namespace CSharpToTypeScript.Tests;

public class TsLiteralEmissionTests : TsConversionTestBase
{
    [Fact]
    public void Null_Literal_Emits_Null()
    {
        var result = Convert("""
class Sample
{
    string GetNull()
    {
        return null;
    }
}
""");
        Assert.True(result.Success);
        Assert.Contains("return null;", result.GeneratedCode);
    }

    [Fact]
    public void Boolean_Literals_Emit_True_False()
    {
        var result = Convert("""
class Sample
{
    bool GetTrue()
    {
        return true;
    }
    bool GetFalse()
    {
        return false;
    }
}
""");
        Assert.True(result.Success);
        Assert.Contains("return true;", result.GeneratedCode);
        Assert.Contains("return false;", result.GeneratedCode);
    }

    [Fact]
    public void Hex_Literal_Emits_Directly()
    {
        var result = Convert("""
class Sample
{
    int GetHex()
    {
        return 0xFF;
    }
}
""");
        Assert.True(result.Success);
        Assert.Contains("return 0xFF;", result.GeneratedCode);
    }

    [Fact]
    public void String_With_Escapes_Emits_With_Escapes()
    {
        var result = Convert("""
class Sample
{
    string GetMessage()
    {
        return "hello\nworld";
    }
}
""");
        Assert.True(result.Success);
        Assert.Contains("return \"hello\\nworld\";", result.GeneratedCode);
    }

    [Fact]
    public void Char_Literal_Emits_As_String()
    {
        var result = Convert("""
class Sample
{
    char GetChar()
    {
        return 'A';
    }
}
""");
        Assert.True(result.Success);
        Assert.Contains("return \"A\";", result.GeneratedCode);
    }
}
```

- [ ] **Step 3: 写 TsPrimitiveTypeMappingTests**

```csharp
namespace CSharpToTypeScript.Tests;

public class TsPrimitiveTypeMappingTests : TsConversionTestBase
{
    [Fact]
    public void Int_Maps_To_Number()
    {
        var result = Convert("class S { int x = 1; }");
        Assert.Contains("x: number = 1", result.GeneratedCode);
    }

    [Fact]
    public void Double_Maps_To_Number()
    {
        var result = Convert("class S { double x = 1.5; }");
        Assert.Contains("x: number = 1.5", result.GeneratedCode);
    }

    [Fact]
    public void Long_Maps_To_Bigint_With_N_Suffix()
    {
        var result = Convert("class S { long x = 100L; }");
        Assert.Contains("x: bigint = 100n", result.GeneratedCode);
    }

    [Fact]
    public void Bool_Maps_To_Boolean()
    {
        var result = Convert("class S { bool x = true; }");
        Assert.Contains("x: boolean = true", result.GeneratedCode);
    }

    [Fact]
    public void String_Maps_To_String()
    {
        var result = Convert("class S { string x = \"hi\"; }");
        Assert.Contains("x: string = \"hi\"", result.GeneratedCode);
    }

    [Fact]
    public void Char_Maps_To_String()
    {
        var result = Convert("class S { char x = 'a'; }");
        Assert.Contains("x: string = \"a\"", result.GeneratedCode);
    }

    [Fact]
    public void Void_Maps_To_Void()
    {
        var result = Convert("class S { void DoNothing() { } }");
        Assert.Contains("DoNothing(): void", result.GeneratedCode);
    }

    [Fact]
    public void Object_Maps_To_Any()
    {
        var result = Convert("class S { object x = null; }");
        Assert.Contains("x: any = null", result.GeneratedCode);
    }

    [Fact]
    public void Nullable_Int_Maps_To_Number_Or_Null()
    {
        var result = Convert("""
class S
{
    int? x = null;
}
""");
        Assert.True(result.Success);
        Assert.Contains("number | null", result.GeneratedCode);
    }
}
```

- [ ] **Step 4: 运行所有 M1 测试**

Run: `dotnet test tests/CSharpToTypeScript.Tests/CSharpToTypeScript.Tests.csproj`
Expected: 全部 PASS。

- [ ] **Step 5: 运行 Java 零回归**

Run: `dotnet test tests/CSharpToJava.Tests/CSharpToJava.Tests.csproj`
Expected: 全部 PASS。

- [ ] **Step 6: 提交**

```bash
git add tests/CSharpToTypeScript.Tests/TsControlFlowEmissionTests.cs tests/CSharpToTypeScript.Tests/TsLiteralEmissionTests.cs tests/CSharpToTypeScript.Tests/TsPrimitiveTypeMappingTests.cs
git commit -m "test(ts): add M1 control flow, literal, and primitive type mapping tests"
```

---

# 里程碑 M2：核心特性（泛型/接口/属性/枚举/异常/字符串插值/数值语义/可空）

**验收标准：** §5.3 核心特性组测试全绿 + L3；Java 全量测试零回归。

## Task 14: 泛型类型/方法/约束发射

**Files:**
- Modify: `src/CSharpToJava.Core/TypeScript/TsTypeWriter.cs`（泛型约束映射）
- Modify: `src/CSharpToJava.Core/TypeScript/TsMemberWriter.cs`（泛型方法）
- Create: `tests/CSharpToTypeScript.Tests/TsGenericEmissionTests.cs`

- [ ] **Step 1: 写失败测试**

```csharp
namespace CSharpToTypeScript.Tests;

public class TsGenericEmissionTests : TsConversionTestBase
{
    [Fact]
    public void Generic_Class_Emits_Type_Parameter()
    {
        var result = Convert("""
class Container<T>
{
    T value;
}
""");
        Assert.True(result.Success);
        Assert.Contains("export class Container<T>", result.GeneratedCode);
    }

    [Fact]
    public void Generic_Class_With_Constraint_Emits_Extends()
    {
        var result = Convert("""
class Container<T> where T : IComparable
{
    T value;
}
""");
        Assert.True(result.Success);
        Assert.Contains("Container<T extends IComparable>", result.GeneratedCode);
    }

    [Fact]
    public void Generic_Method_Emits_Type_Parameter()
    {
        var result = Convert("""
class Utils
{
    T Identity<T>(T x)
    {
        return x;
    }
}
""");
        Assert.True(result.Success);
        Assert.Contains("Identity<T>(x: T): T", result.GeneratedCode);
    }

    [Fact]
    public void Generic_Method_With_Constraint_Emits_Extends()
    {
        var result = Convert("""
class Utils
{
    T Max<T>(T a, T b) where T : IComparable
    {
        return a;
    }
}
""");
        Assert.True(result.Success);
        Assert.Contains("Max<T extends IComparable>", result.GeneratedCode);
    }

    [Fact]
    public void Multiple_Type_Parameters_Emit_All()
    {
        var result = Convert("""
class Pair<K, V>
{
    K key;
    V value;
}
""");
        Assert.True(result.Success);
        Assert.Contains("Pair<K, V>", result.GeneratedCode);
    }
}
```

- [ ] **Step 2: 运行测试验证失败**

Run: `dotnet test tests/CSharpToTypeScript.Tests/CSharpToTypeScript.Tests.csproj --filter "FullyQualifiedName~TsGenericEmissionTests"`
Expected: FAIL（泛型类型参数未发射）。

- [ ] **Step 3: 验证 IR 是否携带类型参数**

查看 `JavaTypeDeclaration.cs` 的 `TypeParameters` 属性与 `JavaTypeParameter.ToString()`。IR 已有该信息（基类 `WriteTypeParameters` 方法存在）。

确认 `TsTypeWriter.EmitClass` 已有类型参数发射代码（Task 9 中已实现 `if (cls.TypeParameters.Count > 0)`）。若已实现但约束未发射，需修改 `JavaTypeParameter` 的 TS 表示。

在 `TsTypeWriter` 中添加类型参数格式化辅助方法：

```csharp
private string FormatTypeParameters(IReadOnlyList<JavaTypeParameter> typeParams)
{
    if (typeParams.Count == 0) return "";
    var parts = typeParams.Select(tp =>
    {
        // where T : X -> T extends X
        if (!string.IsNullOrEmpty(tp.Constraint))
        {
            return $"{tp.Name} extends {tp.Constraint}";
        }
        return tp.Name;
    });
    return $"<{string.Join(", ", parts)}>";
}
```

将 `EmitClass`/`EmitInterface` 中的类型参数发射改为调用 `FormatTypeParameters(cls.TypeParameters)`。

注：需确认 `JavaTypeParameter` 的属性名（`Name`/`Constraint`）。Read `JavaTypeDeclaration.cs` 第 351-372 行确认。

- [ ] **Step 4: 运行测试验证通过**

Run: `dotnet test tests/CSharpToTypeScript.Tests/CSharpToTypeScript.Tests.csproj --filter "FullyQualifiedName~TsGenericEmissionTests"`
Expected: PASS（5 个测试）。

- [ ] **Step 5: 提交**

```bash
git add src/CSharpToJava.Core/TypeScript/TsTypeWriter.cs tests/CSharpToTypeScript.Tests/TsGenericEmissionTests.cs
git commit -m "feat(ts): emit generic type parameters with constraints"
```

---

## Task 15: 接口与 implements 发射

**Files:**
- Modify: `src/CSharpToJava.Core/TypeScript/TsTypeWriter.cs`
- Create: `tests/CSharpToTypeScript.Tests/TsInterfaceEmissionTests.cs`

- [ ] **Step 1: 写失败测试**

```csharp
namespace CSharpToTypeScript.Tests;

public class TsInterfaceEmissionTests : TsConversionTestBase
{
    [Fact]
    public void Interface_Declaration_Emits_Export_Interface()
    {
        var result = Convert("""
interface IFoo
{
    int Bar();
}
""");
        Assert.True(result.Success);
        Assert.Contains("export interface IFoo {", result.GeneratedCode);
        Assert.Contains("Bar(): number;", result.GeneratedCode);
    }

    [Fact]
    public void Class_Implements_Interface_Emits_Implements()
    {
        var result = Convert("""
interface IFoo
{
    int Bar();
}
class Foo : IFoo
{
    public int Bar() { return 1; }
}
""");
        Assert.True(result.Success);
        Assert.Contains("export class Foo implements IFoo", result.GeneratedCode);
    }

    [Fact]
    public void Interface_With_Properties_Emits_Properties()
    {
        var result = Convert("""
interface IPoint
{
    int X { get; }
    int Y { get; }
}
""");
        Assert.True(result.Success);
        Assert.Contains("export interface IPoint", result.GeneratedCode);
        // 接口属性发射为 X: number;
        Assert.Contains("X: number;", result.GeneratedCode);
    }

    [Fact]
    public void Interface_Extends_Interface_Emits_Extends()
    {
        var result = Convert("""
interface IBase
{
    int A();
}
interface IDerived : IBase
{
    int B();
}
""");
        Assert.True(result.Success);
        Assert.Contains("export interface IDerived extends IBase", result.GeneratedCode);
    }
}
```

- [ ] **Step 2: 运行测试验证失败/通过**

Run: `dotnet test tests/CSharpToTypeScript.Tests/CSharpToTypeScript.Tests.csproj --filter "FullyQualifiedName~TsInterfaceEmissionTests"`

部分测试可能已通过（Task 9 已实现基础接口发射）。失败项主要是接口属性发射格式。

- [ ] **Step 3: 修复接口属性发射**

在 `TsTypeWriter.EmitInterface` 中，IR 的接口属性可能以 `JavaMethodDeclaration`（getter）形式存在。需检测 `IsAutoGenerated` 或属性模式。

读取 `JavaInterfaceDeclaration` 实际结构（`JavaTypeDeclaration.cs` 第 217 行），确认接口属性在 IR 中的表示。若属性以方法形式存储（`get_X`），需在发射时识别并转为属性语法。

简化处理：接口中的 `get_X()` / `set_X()` 方法对发射为 `X: type;` 属性。在 `EmitInterface` 中添加属性检测逻辑：

```csharp
// 检测 get_X/set_X 方法对，合并为属性
var propertyGroups = iface.Methods
    .Where(m => m.Name.StartsWith("get_") || m.Name.StartsWith("set_"))
    .GroupBy(m => m.Name.Substring(4))
    .ToList();

var pureMethods = iface.Methods
    .Where(m => !m.Name.StartsWith("get_") && !m.Name.StartsWith("set_"))
    .ToList();

foreach (var grp in propertyGroups)
{
    var getter = grp.FirstOrDefault(m => m.Name.StartsWith("get_"));
    if (getter != null)
    {
        sb.Append(ind).Append(_indent)
          .AppendLine($"{TsNaming.SafeIdentifier(grp.Key)}: {getter.ReturnType};");
    }
}

foreach (var method in pureMethods)
{
    // 原有方法发射
}
```

- [ ] **Step 4: 运行测试验证通过**

Run: `dotnet test tests/CSharpToTypeScript.Tests/CSharpToTypeScript.Tests.csproj --filter "FullyQualifiedName~TsInterfaceEmissionTests"`
Expected: PASS（4 个测试）。

- [ ] **Step 5: 提交**

```bash
git add src/CSharpToJava.Core/TypeScript/TsTypeWriter.cs tests/CSharpToTypeScript.Tests/TsInterfaceEmissionTests.cs
git commit -m "feat(ts): emit interfaces with properties and extends"
```

---

## Task 16: 属性 get/set 发射

**Files:**
- Modify: `src/CSharpToJava.Core/TypeScript/TsMemberWriter.cs`
- Create: `tests/CSharpToTypeScript.Tests/TsPropertyEmissionTests.cs`

- [ ] **Step 1: 写失败测试**

```csharp
namespace CSharpToTypeScript.Tests;

public class TsPropertyEmissionTests : TsConversionTestBase
{
    [Fact]
    public void Auto_Property_Emits_As_Field()
    {
        var result = Convert("""
class Point
{
    int X { get; set; }
}
""");
        Assert.True(result.Success);
        // 自动属性 -> 字段（简化 M2）
        Assert.Contains("X: number", result.GeneratedCode);
    }

    [Fact]
    public void Read_Only_Property_Emits_Getter()
    {
        var result = Convert("""
class Point
{
    int X { get; }
    public Point(int x) { X = x; }
}
""");
        Assert.True(result.Success);
        Assert.Contains("get X(): number", result.GeneratedCode);
    }

    [Fact]
    public void Property_With_Private_Setter_Emits_Getter_And_Private_Setter()
    {
        var result = Convert("""
class Point
{
    int X { get; private set; }
}
""");
        Assert.True(result.Success);
        Assert.Contains("get X(): number", result.GeneratedCode);
        Assert.Contains("private set X(value: number)", result.GeneratedCode);
    }

    [Fact]
    public void Property_With_Expression_Body_Emits_Getter()
    {
        var result = Convert("""
class Container
{
    int count;
    int Doubled => count * 2;
}
""");
        Assert.True(result.Success);
        Assert.Contains("get Doubled(): number", result.GeneratedCode);
        Assert.Contains("return this.count * 2;", result.GeneratedCode);
    }
}
```

- [ ] **Step 2: 运行测试验证失败**

Run: `dotnet test tests/CSharpToTypeScript.Tests/CSharpToTypeScript.Tests.csproj --filter "FullyQualifiedName~TsPropertyEmissionTests"`

注：IR 中属性可能已被 `LowerProperty` lowering pass 转为 `getX()`/`setX()` 方法。需检查 IR 实际形态。

- [ ] **Step 3: 识别并发射