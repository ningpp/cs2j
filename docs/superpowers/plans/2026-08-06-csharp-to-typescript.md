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
    public static string SafeIdentifier(string name);          // TS 保留字处理
    public static string SafeClassName(string name);
    public static string OverloadSuffix(IReadOnlyList<string> paramTsTypes);
    public static string FactoryName(int ordinal);                    // 构造器重载 -> Create$N
}

namespace CSharpToJava.Core.TypeScript.Rewriters;

public sealed class TsOverloadDisambiguationRewriter : Java.JavaSyntaxRewriter { ... }
public sealed class TsGenericErasureRewriter : Java.JavaSyntaxRewriter { ... }
public sealed class TsInterfaceProbeRewriter : Java.JavaSyntaxRewriter { ... }
public sealed class TsEqualityRewriter : Java.JavaSyntaxRewriter { ... }
```

> **IR 属性名契约（已对照 `src/CSharpToJava.Core/Java/` 实际代码核对，实现时以此为准）：**
> - 表达式：`JavaCastExpression.Type`、`JavaAssignmentExpression.Target/Operator/Value`、`JavaParenthesizedExpression.InnerExpression`、`JavaInstanceOfExpression.Type`、`JavaRawExpression.Code`、`JavaThisExpression.IsSuper`；`JavaLambdaExpression.Parameters` 为 `List<string>`（纯参数名），方法体在 `ExpressionBody`（表达式）/`BlockBody`（块）二选一。
> - 语句：`JavaVariableDeclarationStatement.Type/Name/Initializer`、`JavaIfStatement.ThenBody/ElseBody`、`JavaForStatement.Initializer/Increment`（均为**字符串**）`/Condition/Body`、`JavaTryCatchStatement.TryBody/CatchClauses/FinallyBody`、`JavaSwitchSection.Labels`（**已含冒号**，如 `"case 1:"`）、`JavaRawStatement.Code`。
> - 类型/成员：`JavaInterfaceDeclaration.ExtendedTypes`、`JavaEnumDeclaration.Values`、`JavaConstructorDeclaration.Initializer`（`"super(...)"`/`"this(...)"` 字符串）、`JavaStaticInitializerBlock.Statements`（`List<string>`）；`JavaMethodDeclaration`/`JavaConstructorDeclaration` 的 `StructuredBody`（`JavaMethodBody`，结构化语句列表）**优先于** `Body` 字符串，发射器必须两者都处理；`JavaTypeParameter.Name/Bounds`（`List<string>`，无 `Constraint` 属性）。
> - **IR 中的类型/成员名均为 Java 风味**（如 `int`/`String`/`List<String>`/`toUpperCase`），TS 发射前需经 Java→TS 映射（`TsMemberMappingTable` 内置 Java 类型表，见 Task 5）。

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
| `src/CSharpToJava.Core/TypeScript/Rewriters/TsPropertyReconstructionRewriter.cs` | M2：属性访问器重组（getX/setX → TS getter/setter + 调用点改写，Task 16） |
| `src/CSharpToJava.Core/TypeScript/Rewriters/TsNumericSemanticsRewriter.cs` | M2：类型感知的整数除法/窄化/bigint 混算（Task 19） |
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
M1 骨架 ──► M2 核心特性 ──► M3 语义降级 ──┬──► M5 项目级
   │                                      │
   └────────► M4 兼容库 ────────────────────┘
```

每个里程碑独立可交付、独立验收。M1→M2→M3 顺序执行；M4 仅依赖 M1 发射骨架，可与 M2/M3 并行；M5 依赖 M1-M4（与 spec §6.3 一致：M3 依赖 M2 的接口发射，M5 依赖前四者）。

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

> **配置约束说明**：`methodMappings` 只能表达“方法名→方法名/静态调用”的替换，**不能**收录需要表达式/语句级改写的映射（如 `List.Clear()` → `x.length = 0` 是赋值语句，`Remove` 返回值语义差异等）。此类场景一律从配置中排除，由发射器特殊分支或后续里程碑的 rewriter 处理（`List.Clear` 在 M2 由 `TsExpressionWriter` 语句级特殊分支处理，见 Task 19）。

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
    <PackageReference Include="xunit.SkippableFact" Version="1.5.23" />
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
    /// L3: 会话级共享临时工程（spec §5.2：预生成 package.json + tsconfig.json，
    /// typescript 仅安装一次），写入待验证 .ts 后调用 tsc --noEmit。无 Node 时跳过。
    /// </summary>
    protected void AssertCompiles(string tsCode, params string[] additionalFiles)
    {
        var nodePath = FindNodeExecutable();
        if (nodePath == null)
        {
            throw new SkipException("Node.js not available — L3 tsc check skipped.");
        }

        var workDir = TsCompileFixture.GetWorkDir(nodePath);   // 懒初始化：首次调用时 npm install typescript
        var probePath = Path.Combine(workDir, "probe_" + Guid.NewGuid().ToString("N") + ".ts");
        File.WriteAllText(probePath, tsCode);
        for (int i = 0; i < additionalFiles.Length; i += 2)
        {
            File.WriteAllText(Path.Combine(workDir, additionalFiles[i]), additionalFiles[i + 1]);
        }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = nodePath,
                Arguments = $"node_modules/typescript/bin/tsc --noEmit -p tsconfig.json",
                WorkingDirectory = workDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            var proc = Process.Start(psi)!;
            var stdout = proc.StandardOutput.ReadToEnd();   // 先读完输出再等待，避免管道缓冲区满导致死锁
            var stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit(60000);
            Assert.True(proc.ExitCode == 0,
                $"tsc failed (exit {proc.ExitCode}):\n--- stdout ---\n{stdout}\n--- stderr ---\n{stderr}\n--- code ---\n{tsCode}");
        }
        finally
        {
            try { File.Delete(probePath); } catch { }
        }
    }

    /// <summary>
    /// 会话级共享 tsc 工程（避免每个测试重复 npm install，spec §5.2）。
    /// </summary>
    private static class TsCompileFixture
    {
        private static readonly object Lock = new();
        private static string? _workDir;

        public static string GetWorkDir(string nodePath)
        {
            lock (Lock)
            {
                if (_workDir != null) return _workDir;
                var dir = Path.Combine(Path.GetTempPath(), "cs2j_ts_compile_shared");
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, "package.json"), "{\"type\":\"module\"}");
                File.WriteAllText(Path.Combine(dir, "tsconfig.json"),
                    "{\"compilerOptions\":{\"strict\":true,\"target\":\"ES2022\",\"module\":\"NodeNext\",\"moduleResolution\":\"NodeNext\",\"noEmit\":true,\"useDefineForClassFields\":false},\"include\":[\"*.ts\"]}");
                if (!Directory.Exists(Path.Combine(dir, "node_modules", "typescript")))
                {
                    var npmCmd = OperatingSystem.IsWindows()
                        ? Path.Combine(Path.GetDirectoryName(nodePath)!, "npm.cmd")
                        : "npm";
                    var installPsi = new ProcessStartInfo
                    {
                        FileName = npmCmd,
                        Arguments = "install typescript --no-save",
                        WorkingDirectory = dir,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                    };
                    var install = Process.Start(installPsi)!;
                    install.StandardOutput.ReadToEnd();
                    install.StandardError.ReadToEnd();
                    install.WaitForExit(120000);
                    Assert.True(install.ExitCode == 0, $"npm install typescript failed (exit {install.ExitCode})");
                }
                _workDir = dir;
                return _workDir;
            }
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

注：实测确认在普通 `[Fact]` 中抛 `SkipException` 会使测试 FAIL 而非 SKIP（xunit v2 普通 `[Fact]` 无法动态跳过），因此 `AssertCompiles` 在无 Node 时直接返回（空转通过），避免无 Node 机器上 L3 测试误红；`SkipException` 仅配合 `[SkippableFact]` 特性使用。共享根目录 `cs2j_ts_compile_shared` 仅保留 typescript 安装（node_modules）；每次校验在唯一 `run_<GUID>` 子目录中进行并在 `finally` 中递归删除。

- [ ] **Step 3: 注册到 slnx**

读取 `CSharpToJavaConverter.slnx`（现有条目均带 `Id`/`Type` 属性，且需在 `/tests/` Folder 内加 `ProjectRef`），在 `<Solution>` 下添加：

```xml
<Project Path="tests\CSharpToTypeScript.Tests\CSharpToTypeScript.Tests.csproj" Id="c4d5e6f7-a8b9-4c0d-9e1f-2a3b4c5d6e7f" Type="Microsoft.VisualStudio.Project" />
```

并在现有 `<Folder ... Name="/tests/" ...>` 内追加：

```xml
<ProjectRef Id="c4d5e6f7-a8b9-4c0d-9e1f-2a3b4c5d6e7f" />
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

    [Fact]
    public void TryMapType_Java_Flavor_String_To_string()
    {
        // IR 中的类型是 Java 风味，内置 Java→TS 类型表覆盖
        var table = new TsMemberMappingTable(null, null);
        Assert.True(table.TryMapType("String", out var tsType, out _));
        Assert.Equal("string", tsType);
    }

    [Fact]
    public void TryMapType_Java_Flavor_Primitives()
    {
        var table = new TsMemberMappingTable(null, null);
        Assert.True(table.TryMapType("int", out var t1, out _));
        Assert.Equal("number", t1);
        Assert.True(table.TryMapType("long", out var t2, out _));
        Assert.Equal("bigint", t2);
        Assert.True(table.TryMapType("boolean", out var t3, out _));
        Assert.Equal("boolean", t3);
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
    // IR 中已是 Java 风味，这里覆盖 Java 化 API 调用。
    // 注意：只收录“方法名→方法名”可直接替换的项；需要参数感知/语句级改写的
    // （如 remove(int) 返回值语义、equalsIgnoreCase 两侧调用、Clear → length = 0）
    // 不放进本表，由发射器特殊分支或 M3 rewriter 处理。
    private static readonly Dictionary<(string Type, string Method), string> BuiltinMethodMappings = new()
    {
        { ("java.util.List", "size"), "length" },
        { ("java.util.Collection", "size"), "length" },
        { ("java.lang.String", "length"), "length" },
        { ("java.util.List", "add"), "push" },
        { ("java.util.ArrayList", "add"), "push" },
        { ("java.util.List", "isEmpty"), "length === 0" },
        { ("java.util.Collection", "isEmpty"), "length === 0" },
        { ("java.util.List", "contains"), "includes" },
        { ("java.util.List", "indexOf"), "indexOf" },
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
        { ("java.lang.Math", "sqrt"), "Math.sqrt" },
        { ("java.lang.Math", "abs"), "Math.abs" },
        { ("java.lang.Math", "max"), "Math.max" },
        { ("java.lang.Math", "min"), "Math.min" },
        { ("java.lang.Math", "floor"), "Math.floor" },
        { ("java.lang.Math", "ceil"), "Math.ceil" },
        { ("java.lang.Math", "round"), "Math.round" },
        { ("java.lang.Math", "pow"), "Math.pow" },
    };

    // 内置 Java 风味类型 → TS 类型映射（IR 中字段/参数/返回值类型均为 Java 名，
    // 发射器必须先经此表转换；spec 公共契约“IR 为 Java 风味”）
    private static readonly Dictionary<string, string> BuiltinJavaTypeMappings = new()
    {
        { "int", "number" }, { "long", "bigint" }, { "short", "number" },
        { "byte", "number" }, { "char", "string" }, { "float", "number" },
        { "double", "number" }, { "boolean", "boolean" }, { "void", "void" },
        { "String", "string" }, { "java.lang.String", "string" },
        { "Object", "any" }, { "java.lang.Object", "any" },
        { "Integer", "number" }, { "Long", "bigint" }, { "Boolean", "boolean" },
        { "Double", "number" }, { "Float", "number" }, { "Character", "string" },
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
        // ① 配置文件（C# 全名，如 System.Int32）
        if (_typeMappings.TryGetValue(csharpOrJavaType, out var entry))
        {
            tsType = entry.TsType;
            imports = entry.Imports;
            return true;
        }
        // ② 内置 Java 风味类型表（IR 中实际出现的类型名，如 int/String）
        if (BuiltinJavaTypeMappings.TryGetValue(csharpOrJavaType, out var javaMapped))
        {
            tsType = javaMapped;
            imports = System.Array.Empty<string>();
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
Expected: PASS（10 个测试）。

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
        var prefix = ComputeRelativePath(targetNamespace);   // 形如 "./"、"./Models/"、"../X/"
        AddNamedImport(typeName, $"{prefix}{typeName}.js");
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
                // 同命名空间：./Type.js（调用方拼接类型名）
                return "./";
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
    public void BinaryExpression_IntegerLiteralDivision_Wraps_MathTrunc()
    {
        // 两个整数字面量操作数的 / -> Math.trunc(7 / 2)
        var expr = new JavaBinaryExpression
        {
            Left = new JavaLiteralExpression("7"),
            Operator = "/",
            Right = new JavaLiteralExpression("2"),
        };
        var result = EmitExpr(expr);
        Assert.Equal("Math.trunc(7 / 2)", result);
    }

    [Fact]
    public void BinaryExpression_IdentifierDivision_Not_Wrapped_In_M1()
    {
        // 标识符操作数无类型信息（IR 不携带类型），M1 不包 Math.trunc，
        // 避免误伤 double 除法；类型感知处理在 M2 数值语义 rewriter（Task 19）
        var expr = new JavaBinaryExpression
        {
            Left = new JavaIdentifierExpression("a"),
            Operator = "/",
            Right = new JavaIdentifierExpression("b"),
        };
        Assert.Equal("a / b", EmitExpr(expr));
    }

    [Fact]
    public void BinaryExpression_BigintLiteralDivision_Not_Wrapped()
    {
        // bigint 除法 JS 天然截断，且 Math.trunc 不接受 bigint（spec §2.5）
        var expr = new JavaBinaryExpression
        {
            Left = new JavaLiteralExpression("100L"),
            Operator = "/",
            Right = new JavaLiteralExpression("3L"),
        };
        Assert.Equal("100n / 3n", EmitExpr(expr));
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
        // JavaLambdaExpression.Parameters 为 List<string>（纯参数名），
        // 块体在 BlockBody（JavaBlockStatement）
        var lambda = new JavaLambdaExpression();
        lambda.Parameters.Add("x");
        var block = new JavaBlockStatement();
        block.Statements.Add(new JavaReturnStatement { Expression = new JavaLiteralExpression("42") });
        lambda.BlockBody = block;
        var result = EmitExpr(lambda);
        Assert.Contains("(x) =>", result);
        Assert.Contains("return 42;", result);
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
            JavaThisExpression thisExpr => thisExpr.IsSuper ? "super" : "this",
            JavaBinaryExpression binary => EmitBinary(binary),
            JavaUnaryExpression unary => EmitUnary(unary),
            JavaConditionalExpression cond => $"{ToInlineString(cond.Condition)} ? {ToInlineString(cond.WhenTrue)} : {ToInlineString(cond.WhenFalse)}",
            JavaCastExpression cast => EmitCast(cast),
            JavaNewExpression newExpr => EmitNew(newExpr),
            JavaMethodCallExpression call => EmitMethodCall(call),
            JavaMemberAccessExpression access => EmitMemberAccess(access),
            JavaLambdaExpression lambda => EmitLambda(lambda),
            JavaArrayAccessExpression arr => $"{ToInlineString(arr.Target)}[{ToInlineString(arr.Index)}]",
            JavaAssignmentExpression assign => $"{ToInlineString(assign.Target)} {assign.Operator} {ToInlineString(assign.Value)}",
            JavaParenthesizedExpression paren => $"({ToInlineString(paren.InnerExpression)})",
            JavaInstanceOfExpression inst => EmitInstanceOf(inst),
            JavaRawExpression raw => raw.Code,
            _ => expr.ToInlineString(),
        };
    }

    /// <summary>
    /// Java 风味类型名 → TS 类型（供语句/成员发射器复用，如变量声明、字段）。
    /// </summary>
    public string MapType(string javaOrCsharpType)
    {
        return _mappings.TryMapType(javaOrCsharpType, out var ts, out var imports)
            ? WithImports(ts, imports)
            : javaOrCsharpType;
    }

    private string WithImports(string tsType, IReadOnlyList<string> imports)
    {
        foreach (var imp in imports)
        {
            // compat 包符号导入；模块名即 imp，符号名即 tsType
            _imports.AddNamedImport(tsType.Split('<')[0], imp);
        }
        return tsType;
    }

    private string EmitLiteral(JavaLiteralExpression lit)
    {
        var v = lit.Value;

        // long 字面量：100L -> 100n
        if (v.EndsWith("L") && long.TryParse(v[..^1], out _))
        {
            return v[..^1] + "n";
        }

        // char 字面量：'a' -> "a"；泛化支持转义字符（'\n' -> "\n"）。
        // 边界：内含双引号（如 '\"'）的转义在 M2 字符处理任务中细化。
        if (v.Length >= 3 && v.StartsWith("'") && v.EndsWith("'"))
        {
            var inner = v[1..^1];
            if (!inner.Contains('"'))
            {
                return "\"" + inner + "\"";
            }
        }

        return v;
    }

    private string EmitBinary(JavaBinaryExpression binary)
    {
        var left = ToInlineString(binary.Left);
        var right = ToInlineString(binary.Right);

        // 整数除法补偿（spec §2.5：两操作数均为 number 整型时才包 Math.trunc）。
        // IR 不携带类型信息：M1 仅对“两个整数字面量”这一确定场景包装；
        // 标识符/表达式操作数的类型感知判定由 M2 Task 19 的数值语义 rewriter
        //（持有 Roslyn 语义模型回调，模式同 §2.6.1 重载消歧）处理。
        if (binary.Operator == "/"
            && IsIntegerLiteralOperand(binary.Left)
            && IsIntegerLiteralOperand(binary.Right))
        {
            return $"Math.trunc({left} / {right})";
        }

        // 相等比较：原始类型 === / !==（record/struct 场景由 M3 TsEqualityRewriter 预处理）
        var op = binary.Operator switch
        {
            "==" => "===",
            "!=" => "!==",
            _ => binary.Operator,
        };

        return $"{left} {op} {right}";
    }

    private static bool IsIntegerLiteralOperand(JavaExpression expr)
    {
        // 仅整数字面量（可解析为 long 且无 L 后缀）。bigint 字面量（L 后缀）
        // 不可包 Math.trunc：JS bigint 除法天然截断，且 Math.* 不接受 bigint（运行时 TypeError）。
        return expr is JavaLiteralExpression lit
            && long.TryParse(lit.Value, out _)
            && !lit.Value.EndsWith("L");
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
        var tsType = MapType(cast.Type);   // 实际属性名是 Type（非 TargetType）
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
            var specialArgs = string.Join(", ", call.Arguments.Select(ToInlineString));
            return methodName == "println"
                ? $"console.log({specialArgs})"
                : $"process.stdout.write({specialArgs})";
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
        // JavaLambdaExpression.Parameters 是 List<string>（纯参数名，无类型）；
        // 参数类型注解在 M2/M3 委托转换时由语义模型补充。
        var parms = string.Join(", ", lambda.Parameters.Select(TsNaming.SafeIdentifier));
        if (lambda.ExpressionBody != null)
        {
            return $"({parms}) => {ToInlineString(lambda.ExpressionBody)}";
        }
        if (lambda.BlockBody != null)
        {
            // 块体交给语句发射器（构造时注入；此处简化为 IR 自带 Java 文本，
            // 完整版在 TypeScriptEmitter 组装时用 TsStatementWriter 渲染 BlockBody）
            return $"({parms}) => {lambda.BlockBody.ToString("")}";
        }
        return $"({parms}) => {{}}";
    }

    private string EmitInstanceOf(JavaInstanceOfExpression inst)
    {
        // class 直译 instanceof；interface 由 TsInterfaceProbeRewriter 预处理（M3）
        // 实际属性名是 Type（非 TypeName）；Java 16 模式变量（PatternVariable）在 M3 模式匹配任务处理
        return $"{ToInlineString(inst.Expression)} instanceof {inst.Type}";
    }
}
```

注：以上属性名已对照 `src/CSharpToJava.Core/Java/JavaExpression.cs` 实际定义核对（见文首“IR 属性名契约”）。

- [ ] **Step 4: 运行测试验证通过**

Run: `dotnet test tests/CSharpToTypeScript.Tests/CSharpToTypeScript.Tests.csproj --filter "FullyQualifiedName~TsExpressionWriterTests"`
Expected: PASS（12 个测试）。

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
        block.Statements.Add(new JavaReturnStatement { Expression = new JavaLiteralExpression("1") });
        var result = EmitStmt(block);
        Assert.Contains("{", result);
        Assert.Contains("return 1;", result);
        Assert.Contains("}", result);
    }

    [Fact]
    public void VariableDeclaration_Emits_Let()
    {
        // 实际属性名：Type/Name（非 VariableType/VariableName）
        var decl = new JavaVariableDeclarationStatement
        {
            Type = "number",
            Name = "x",
            Initializer = new JavaLiteralExpression("42"),
        };
        var result = EmitStmt(decl);
        Assert.Contains("let x: number = 42;", result);
    }

    [Fact]
    public void ReturnStatement_Emits_Return()
    {
        var ret = new JavaReturnStatement { Expression = new JavaIdentifierExpression("x") };
        Assert.Contains("return x;", EmitStmt(ret));
    }

    [Fact]
    public void IfStatement_Emits_If_Else()
    {
        // 实际属性名：ThenBody/ElseBody（非 ThenStatement/ElseStatement）
        var ifStmt = new JavaIfStatement
        {
            Condition = new JavaIdentifierExpression("flag"),
            ThenBody = new JavaBlockStatement(),
            ElseBody = new JavaBlockStatement(),
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
        // 实际属性名：Type/Name；类型需经 Java→TS 映射（IR 为 Java 风味）
        var tsType = _exprWriter.MapType(decl.Type);
        var init = decl.Initializer != null ? $" = {_exprWriter.ToInlineString(decl.Initializer)}" : "";
        return $"{ind}let {TsNaming.SafeIdentifier(decl.Name)}: {tsType}{init};";
    }

    private string EmitIf(JavaIfStatement ifStmt, int indentLevel)
    {
        var ind = new string(_indent[0], indentLevel * _indent.Length);
        var sb = new StringBuilder();
        sb.Append($"{ind}if ({_exprWriter.ToInlineString(ifStmt.Condition)}) ");
        sb.Append(Emit(ifStmt.ThenBody, indentLevel));
        if (ifStmt.ElseBody != null)
        {
            sb.Append($" else ");
            sb.Append(Emit(ifStmt.ElseBody, indentLevel));
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
        // JavaForStatement.Initializer/Increment 均为字符串（非表达式节点）。
        // Initializer 形如 "int i = 0"（Java 风味），转换为 "let i: number = 0"；
        // 无法匹配时原样输出 + 诊断注释（M2 细化）。
        var init = forStmt.Initializer != null ? ConvertForInitializer(forStmt.Initializer) : "";
        var cond = forStmt.Condition != null ? _exprWriter.ToInlineString(forStmt.Condition) : "";
        var upd = forStmt.Increment ?? "";
        var sb = new StringBuilder();
        sb.Append($"{ind}for ({init}; {cond}; {upd}) ");
        sb.Append(Emit(forStmt.Body, indentLevel));
        return sb.ToString();
    }

    /// <summary>
    /// "int i = 0" / "final int i = 0" -> "let i: number = 0"；不匹配则原样保留。
    /// </summary>
    private string ConvertForInitializer(string javaInit)
    {
        var m = System.Text.RegularExpressions.Regex.Match(
            javaInit.Trim(), @"^(?:final\s+)?(\w[\w.<>\[\], ]*?)\s+(\w+)\s*=\s*(.+)$");
        if (!m.Success) return javaInit;
        var tsType = _exprWriter.MapType(m.Groups[1].Value.Trim());
        return $"let {TsNaming.SafeIdentifier(m.Groups[2].Value)}: {tsType} = {m.Groups[3].Value}";
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
        // 实际属性名：TryBody/FinallyBody（非 TryBlock/FinallyBlock）。
        // JS/TS 文法每个 try 只允许一个 catch 子句，多 catch 转守卫链：
        // if/else-if instanceof 过滤，链尾补 throw 重抛保持 C# 语义；
        // 绑定变量统一用首子句名（C# 多 catch 不同变量名场景 M1 简化，记录）。
        var ind = new string(_indent[0], indentLevel * _indent.Length);
        var sb = new StringBuilder();
        // try 体经 EmitBlock 自闭合（以 } 结尾），catch 行直接接在其后，不再输出前导 }
        sb.Append($"{ind}try ");
        sb.Append(Emit(tryCatch.TryBody, indentLevel));

        if (tryCatch.CatchClauses.Count > 0)
        {
            var exVar = string.IsNullOrEmpty(tryCatch.CatchClauses[0].VariableName)
                ? "e"
                : TsNaming.SafeIdentifier(tryCatch.CatchClauses[0].VariableName);
            sb.AppendLine($" catch ({exVar}) {{");

            var isFirst = true;
            var closedWithBare = false;
            foreach (var clause in tryCatch.CatchClauses)
            {
                var isBare = string.IsNullOrEmpty(clause.ExceptionType)
                    || clause.ExceptionType == "Exception"
                    || clause.ExceptionType == "Throwable";

                if (isBare)
                {
                    if (isFirst)
                    {
                        // 唯一裸 catch 场景：catch 区域直接发射 body 内容，无 instanceof 守卫
                        foreach (var s in clause.Body.Statements)
                        {
                            sb.AppendLine(Emit(s, indentLevel + 1));
                        }
                    }
                    else
                    {
                        // 链尾 else 分支直接接 body 内容
                        sb.AppendLine($" else {{");
                        foreach (var s in clause.Body.Statements)
                        {
                            sb.AppendLine(Emit(s, indentLevel + 2));
                        }
                        sb.AppendLine($"{ind}{_indent}}}");
                    }
                    closedWithBare = true;
                    break;
                }

                // 守卫链块：if/else-if 行自带花括号，body 语句直接落在块内（贴合单 catch 输出形状）
                var head = isFirst
                    ? $"{ind}{_indent}if ({exVar} instanceof {clause.ExceptionType}) {{"
                    : $" else if ({exVar} instanceof {clause.ExceptionType}) {{";
                sb.AppendLine(head);
                foreach (var s in clause.Body.Statements)
                {
                    sb.AppendLine(Emit(s, indentLevel + 2));
                }
                sb.Append($"{ind}{_indent}}}");
                isFirst = false;
            }

            if (!closedWithBare)
            {
                // 全部为 typed 子句：不匹配任何 catch 则重抛，保持 C# 语义
                sb.AppendLine($" else {{");
                sb.AppendLine($"{ind}{_indent}{_indent}throw {exVar};");
                sb.AppendLine($"{ind}{_indent}}}");
            }

            sb.Append(ind).Append('}');
        }

        if (tryCatch.FinallyBody != null)
        {
            sb.AppendLine();
            sb.Append($"{ind}finally ");
            sb.Append(Emit(tryCatch.FinallyBody, indentLevel));
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
                // JavaSwitchSection.Labels 已含冒号（如 "case 1:"/"default:"），不可重复追加
                sb.AppendLine($"{ind}{_indent}{label}");
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

注：以上属性名已对照 `src/CSharpToJava.Core/Java/JavaStatement.cs` 实际定义核对（见文首“IR 属性名契约”）。`JavaCatchClause.ExceptionType` 在 IR 中为 Java 风味名（如 `IllegalArgumentException`），到兼容库异常名的映射在 M2 Task 21 处理。

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
using System.Text;
using CSharpToJava.Core.Java;
using CSharpToJava.Core.TypeScript;

namespace CSharpToTypeScript.Tests;

public class TsBasicClassEmissionTests : TsConversionTestBase
{
    private static string EmitType(JavaTypeDeclaration type)
    {
        var table = new TsMemberMappingTable(null, null);
        var imports = new TsImportCollector();
        var emitter = new TypeScriptEmitter();
        return emitter.EmitType(type, table, imports);
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        int count = 0;
        int idx = 0;
        while ((idx = haystack.IndexOf(needle, idx, StringComparison.Ordinal)) >= 0)
        {
            count++;
            idx += needle.Length;
        }
        return count;
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
        // 无初始化器时补默认零值（= 0），见 Field_Without_Initializer_Adds_Default_Zero
        Assert.Contains("public X: number", result);
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
    public void String_Field_Default_Null_Widens_To_Nullable()
    {
        // 零值 null 在 strictNullChecks 下不可赋给 string（TS2322）：
        // 无初始化器且默认零值为 null 时，字段类型放宽为 string | null
        var cls = new JavaClassDeclaration { Name = "Foo", Modifiers = JavaModifiers.Public };
        cls.Fields.Add(new JavaFieldDeclaration
        {
            Modifiers = JavaModifiers.Public,
            Type = "string",
            Name = "Name",
        });
        var result = EmitType(cls);
        Assert.Contains("public Name: string | null = null;", result);
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
        // 实际属性名：Values（非 Members）
        var en = new JavaEnumDeclaration { Name = "Color", Modifiers = JavaModifiers.Public };
        en.Values.Add("Red");
        en.Values.Add("Green");
        en.Values.Add("Blue");
        var result = EmitType(en);
        Assert.Contains("export enum Color {", result);
        Assert.Contains("Red,", result);
        Assert.Contains("Green,", result);
        Assert.Contains("Blue", result);
    }

    [Fact]
    public void Multiple_Constructors_Only_First_Emits_Constructor_Keyword()
    {
        // TS 只允许一个 constructor（TS2392）：第二起以诊断注释占位，不发射非法语法
        var cls = new JavaClassDeclaration { Name = "Foo", Modifiers = JavaModifiers.Public };
        cls.Constructors.Add(new JavaConstructorDeclaration
        {
            Modifiers = JavaModifiers.Public,
            ClassName = "Foo",
            Body = "this.X = 0;",
        });
        cls.Constructors.Add(new JavaConstructorDeclaration
        {
            Modifiers = JavaModifiers.Public,
            ClassName = "Foo",
            Parameters = { new JavaParameter("int", "x") },
            Body = "this.X = x;",
        });
        var result = EmitType(cls);
        Assert.Contains("constructor() {", result);
        Assert.Contains("// TODO(TS): 第 2 个构造器(x: number) 参数无法直译，需工厂/参数对象占位", result);
        Assert.Equal(1, CountOccurrences(result, "constructor("));
    }

    [Fact]
    public void Multiple_Constructors_With_This_Delegation_Emits_Comment_Only()
    {
        var cls = new JavaClassDeclaration { Name = "Foo", Modifiers = JavaModifiers.Public };
        cls.Constructors.Add(new JavaConstructorDeclaration
        {
            Modifiers = JavaModifiers.Public,
            ClassName = "Foo",
            Body = "this.X = 0;",
        });
        cls.Constructors.Add(new JavaConstructorDeclaration
        {
            Modifiers = JavaModifiers.Public,
            ClassName = "Foo",
            Initializer = "this(0)",
            Body = "this.Y = 0;",
        });
        var result = EmitType(cls);
        Assert.Contains("// TODO(TS): 委托构造 this(0) 无法直译，仅注释说明", result);
        Assert.Equal(1, CountOccurrences(result, "constructor("));
    }

    [Fact]
    public void Derived_Class_Constructor_Emits_Super_Without_Args()
    {
        // 派生类构造器必须发射 super()（TS2377/TS17009）：无 base 实参时补 super();
        var cls = new JavaClassDeclaration { Name = "Derived", Modifiers = JavaModifiers.Public, ExtendedType = "Base" };
        cls.Constructors.Add(new JavaConstructorDeclaration
        {
            Modifiers = JavaModifiers.Public,
            ClassName = "Derived",
            Body = "this.X = 0;",
        });
        var result = EmitType(cls);
        Assert.Contains("super();", result);
        Assert.Contains("export class Derived extends Base {", result);
    }

    [Fact]
    public void Derived_Class_Constructor_Emits_Super_With_Args()
    {
        var cls = new JavaClassDeclaration { Name = "Derived", Modifiers = JavaModifiers.Public, ExtendedType = "Base" };
        cls.Constructors.Add(new JavaConstructorDeclaration
        {
            Modifiers = JavaModifiers.Public,
            ClassName = "Derived",
            Initializer = "super(1, \"a\")",
            Body = "this.X = 0;",
        });
        var result = EmitType(cls);
        Assert.Contains("super(1, \"a\");", result);
    }

    [Fact]
    public void Expression_Body_Method_Emits_Return_Block()
    {
        // 类成员体不支持 => 语法（TS1144）：表达式体方法转 { return expr; }
        var cls = new JavaClassDeclaration { Name = "Foo", Modifiers = JavaModifiers.Public };
        cls.Methods.Add(new JavaMethodDeclaration
        {
            Modifiers = JavaModifiers.Public,
            ReturnType = "number",
            Name = "Double",
            Body = "this.X * 2",
            IsBodyExpression = true,
        });
        var result = EmitType(cls);
        Assert.Contains("public Double(): number {", result);
        Assert.Contains("return this.X * 2;", result);
        Assert.DoesNotContain("=>", result);
    }

    [Fact]
    public void Expression_Body_Void_Method_Emits_Statement_Block()
    {
        // void 表达式体：{ expr; } 不带 return（对齐 JavaMemberDeclaration 语义）
        var cls = new JavaClassDeclaration { Name = "Foo", Modifiers = JavaModifiers.Public };
        cls.Methods.Add(new JavaMethodDeclaration
        {
            Modifiers = JavaModifiers.Public,
            ReturnType = "void",
            Name = "Log",
            Body = "this.X = 0",
            IsBodyExpression = true,
        });
        var result = EmitType(cls);
        Assert.Contains("public Log(): void {", result);
        Assert.Contains("this.X = 0;", result);
        Assert.DoesNotContain("=>", result);
        Assert.DoesNotContain("return this.X = 0;", result);
    }

    [Fact]
    public void Nested_Type_Is_Emitted_At_Top_Level_After_Outer()
    {
        // 类体内不允许嵌套类型声明（TS1068）：嵌套类型提升到外层类之后、文件顶层
        var cls = new JavaClassDeclaration { Name = "Outer", Modifiers = JavaModifiers.Public };
        cls.NestedTypes.Add(new JavaClassDeclaration { Name = "Inner", Modifiers = JavaModifiers.Public });
        var result = EmitType(cls);
        var outerIdx = result.IndexOf("export class Outer", StringComparison.Ordinal);
        var outerCloseIdx = result.IndexOf("}", StringComparison.Ordinal);
        var innerIdx = result.IndexOf("export class Inner", StringComparison.Ordinal);
        Assert.True(outerIdx >= 0 && innerIdx > outerCloseIdx && outerCloseIdx > outerIdx, result);
    }

    [Fact]
    public void Long_Field_Initializers_Produce_Valid_Bigint_Literals()
    {
        // RenderConstantValue 产出 100L/0xFF00L/1_000_000L（Java 风味），必须转合法 TS bigint 字面量
        var cls = new JavaClassDeclaration { Name = "Foo", Modifiers = JavaModifiers.Public };
        cls.Fields.Add(new JavaFieldDeclaration { Modifiers = JavaModifiers.Public, Type = "long", Name = "Ticks", Initializer = "100L" });
        cls.Fields.Add(new JavaFieldDeclaration { Modifiers = JavaModifiers.Public, Type = "long", Name = "HexMask", Initializer = "0xFF00L" });
        cls.Fields.Add(new JavaFieldDeclaration { Modifiers = JavaModifiers.Public, Type = "long", Name = "Separated", Initializer = "1_000_000L" });
        var result = EmitType(cls);
        Assert.Contains("public Ticks: bigint = 100n;", result);
        Assert.Contains("public HexMask: bigint = 0xFF00n;", result);
        Assert.Contains("public Separated: bigint = 1_000_000n;", result);
    }

    [Fact]
    public void Float_Field_Initializer_Strips_F_Suffix()
    {
        var cls = new JavaClassDeclaration { Name = "Foo", Modifiers = JavaModifiers.Public };
        cls.Fields.Add(new JavaFieldDeclaration { Modifiers = JavaModifiers.Public, Type = "float", Name = "Ratio", Initializer = "1.5f" });
        var result = EmitType(cls);
        Assert.Contains("public Ratio: number = 1.5;", result);
    }

    [Fact]
    public void Interface_Method_Uses_Mapped_Types()
    {
        // 接口成员参数/返回类型必须走 Java→TS 映射（与类成员一致，I3）
        var iface = new JavaInterfaceDeclaration { Name = "ICounter", Modifiers = JavaModifiers.Public };
        iface.Methods.Add(new JavaMethodDeclaration
        {
            Modifiers = JavaModifiers.Public,
            ReturnType = "long",
            Name = "Increment",
            Parameters = { new JavaParameter("long", "step") },
        });
        var result = EmitType(iface);
        Assert.Contains("Increment(step: bigint): bigint;", result);
    }

    [Fact]
    public void Private_Constructor_Emits_Private()
    {
        // TS 支持 private/protected constructor：可见性直译
        var cls = new JavaClassDeclaration { Name = "Singleton", Modifiers = JavaModifiers.Public };
        cls.Constructors.Add(new JavaConstructorDeclaration
        {
            Modifiers = JavaModifiers.Private,
            ClassName = "Singleton",
        });
        var result = EmitType(cls);
        Assert.Contains("private constructor() {", result);
    }

    [Fact]
    public void Protected_Constructor_Emits_Protected()
    {
        var cls = new JavaClassDeclaration { Name = "AbstractBase", Modifiers = JavaModifiers.Public | JavaModifiers.Abstract };
        cls.Constructors.Add(new JavaConstructorDeclaration
        {
            Modifiers = JavaModifiers.Protected,
            ClassName = "AbstractBase",
        });
        var result = EmitType(cls);
        Assert.Contains("protected constructor() {", result);
    }

    [Fact]
    public void All_Fixed_Features_Compile_With_Tsc()
    {
        // L3：汇总本轮审查修复的全部特性产物，走共享 tsc 工程 strict 编译（spec §5.2/§5.5）
        var sb = new StringBuilder();
        sb.AppendLine(EmitType(new JavaClassDeclaration { Name = "Base", Modifiers = JavaModifiers.Public }));

        var derived = new JavaClassDeclaration { Name = "Derived", Modifiers = JavaModifiers.Public, ExtendedType = "Base" };
        derived.Fields.Add(new JavaFieldDeclaration { Modifiers = JavaModifiers.Public, Type = "boolean", Name = "Ready" });
        derived.Constructors.Add(new JavaConstructorDeclaration
        {
            Modifiers = JavaModifiers.Public,
            ClassName = "Derived",
            Body = "this.Ready = true;",
        });
        sb.AppendLine(EmitType(derived));

        var multi = new JavaClassDeclaration { Name = "Multi", Modifiers = JavaModifiers.Public };
        multi.Fields.Add(new JavaFieldDeclaration { Modifiers = JavaModifiers.Public, Type = "int", Name = "X" });
        multi.Constructors.Add(new JavaConstructorDeclaration { Modifiers = JavaModifiers.Public, ClassName = "Multi" });
        multi.Constructors.Add(new JavaConstructorDeclaration
        {
            Modifiers = JavaModifiers.Public,
            ClassName = "Multi",
            Parameters = { new JavaParameter("int", "x") },
            Body = "this.X = x;",
        });
        sb.AppendLine(EmitType(multi));

        var exprBody = new JavaClassDeclaration { Name = "ExprBody", Modifiers = JavaModifiers.Public };
        exprBody.Fields.Add(new JavaFieldDeclaration { Modifiers = JavaModifiers.Public, Type = "int", Name = "X", Initializer = "1" });
        exprBody.Methods.Add(new JavaMethodDeclaration
        {
            Modifiers = JavaModifiers.Public,
            ReturnType = "number",
            Name = "Double",
            Body = "this.X * 2",
            IsBodyExpression = true,
        });
        exprBody.Methods.Add(new JavaMethodDeclaration
        {
            Modifiers = JavaModifiers.Public,
            ReturnType = "void",
            Name = "Reset",
            Body = "this.X = 0",
            IsBodyExpression = true,
        });
        sb.AppendLine(EmitType(exprBody));

        var outer = new JavaClassDeclaration { Name = "Outer", Modifiers = JavaModifiers.Public };
        outer.NestedTypes.Add(new JavaClassDeclaration { Name = "Inner", Modifiers = JavaModifiers.Public });
        sb.AppendLine(EmitType(outer));

        var nullable = new JavaClassDeclaration { Name = "Nullable", Modifiers = JavaModifiers.Public };
        nullable.Fields.Add(new JavaFieldDeclaration { Modifiers = JavaModifiers.Public, Type = "string", Name = "Name" });
        sb.AppendLine(EmitType(nullable));

        var longs = new JavaClassDeclaration { Name = "Longs", Modifiers = JavaModifiers.Public };
        longs.Fields.Add(new JavaFieldDeclaration { Modifiers = JavaModifiers.Public, Type = "long", Name = "Ticks", Initializer = "100L" });
        longs.Fields.Add(new JavaFieldDeclaration { Modifiers = JavaModifiers.Public, Type = "long", Name = "Mask", Initializer = "0xFFL" });
        longs.Fields.Add(new JavaFieldDeclaration { Modifiers = JavaModifiers.Public, Type = "long", Name = "Big", Initializer = "1_000L" });
        sb.AppendLine(EmitType(longs));

        var iface = new JavaInterfaceDeclaration { Name = "ICounter", Modifiers = JavaModifiers.Public };
        iface.Methods.Add(new JavaMethodDeclaration
        {
            Modifiers = JavaModifiers.Public,
            ReturnType = "long",
            Name = "Increment",
            Parameters = { new JavaParameter("long", "step") },
        });
        sb.AppendLine(EmitType(iface));

        var priv = new JavaClassDeclaration { Name = "Priv", Modifiers = JavaModifiers.Public };
        priv.Constructors.Add(new JavaConstructorDeclaration { Modifiers = JavaModifiers.Private, ClassName = "Priv" });
        sb.AppendLine(EmitType(priv));

        var prot = new JavaClassDeclaration { Name = "Prot", Modifiers = JavaModifiers.Public };
        prot.Constructors.Add(new JavaConstructorDeclaration { Modifiers = JavaModifiers.Protected, ClassName = "Prot" });
        sb.AppendLine(EmitType(prot));

        AssertCompiles(sb.ToString());
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
    private readonly TsExpressionWriter _exprWriter;   // 用于 Java→TS 类型映射
    private readonly string _indent;

    public TsMemberWriter(TsStatementWriter stmtWriter, TsExpressionWriter exprWriter, string indent = "    ")
    {
        _stmtWriter = stmtWriter;
        _exprWriter = exprWriter;
        _indent = indent;
    }

    /// <summary>
    /// Java→TS 类型映射转发（类/接口成员发射共用，保持映射一致）。
    /// </summary>
    public string MapType(string javaOrCsharpType) => _exprWriter.MapType(javaOrCsharpType);

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

        // IR 字段类型为 Java 风味（int/String/...），先经映射转 TS
        var tsType = _exprWriter.MapType(field.Type);
        var name = TsNaming.SafeIdentifier(field.Name);
        var hasInitializer = !string.IsNullOrEmpty(field.Initializer);

        // 无初始化器补默认零值（spec §2.5：tsc strictPropertyInitialization 要求）。
        // strictNullChecks 下 null 不可赋给非空类型（TS2322）：零值恰为 null 时
        // 类型放宽为 `T | null`（仅当实际发射 `= null` 时放宽，避免无谓可空扩散）。
        var emittedType = tsType;
        if (!hasInitializer
            && DefaultValueForType(tsType) == "null"
            && !tsType.Contains("| null") && !tsType.Contains("null |")
            && tsType is not ("null" or "any" or "undefined" or "void"))
        {
            emittedType = $"{tsType} | null";
        }
        sb.Append($"{name}: {emittedType}");

        if (hasInitializer)
        {
            // 初始化器为 Java 风味字面量（FieldTransformer.RenderConstantValue 产出
            // 100L/1.5f/0xFFL 等），需归一化为合法 TS 字面量再发射
            sb.Append($" = {NormalizeLiteral(field.Initializer)}");
        }
        else
        {
            sb.Append($" = {DefaultValueForType(tsType)}");
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

        // 参数/返回值类型需经 Java→TS 映射
        var parms = method.Parameters.Select(p =>
        {
            var pName = TsNaming.SafeIdentifier(p.Name);
            return $"{pName}: {_exprWriter.MapType(p.Type)}";
        });
        sb.Append(string.Join(", ", parms));

        sb.Append($"): {_exprWriter.MapType(method.ReturnType)}");

        if ((method.Modifiers & JavaModifiers.Abstract) != 0
            || (method.Body == null && method.StructuredBody == null))
        {
            sb.Append(';');
        }
        else if (method.StructuredBody != null)
        {
            // 结构化方法体优先于 Body 字符串（JavaMethodDeclaration 契约）：
            // 逐语句交给 TsStatementWriter 发射，避免丢失 body
            sb.AppendLine(" {");
            foreach (var stmt in method.StructuredBody.Statements)
            {
                sb.AppendLine(_stmtWriter.Emit(stmt, indentLevel + 2));
            }
            sb.Append(ind).Append(_indent).Append('}');
        }
        else if (method.IsBodyExpression)
        {
            // 表达式体成员（方法/属性 getter/索引器/运算符）：类成员体不支持 => 语法
            // （TS1144），转块体。对齐 JavaMemberDeclaration 语义：非 void 加 return，
            // void 表达式即语句（无 return）。
            var isVoid = _exprWriter.MapType(method.ReturnType) == "void";
            sb.AppendLine(" {");
            sb.Append(ind).Append(_indent);
            if (!isVoid) sb.Append("return ");
            sb.AppendLine($"{method.Body};");
            sb.Append(ind).Append('}');
        }
        else
        {
            sb.AppendLine(" {");
            // body 已是字符串，按行缩进
            var bodyLines = method.Body!.Split('\n');
            foreach (var line in bodyLines)
            {
                sb.Append(ind).Append(_indent).AppendLine(line);
            }
            sb.Append(ind).Append('}');
        }

        return sb.ToString();
    }

    public string EmitConstructor(JavaConstructorDeclaration ctor, string? extendedType, int indentLevel)
    {
        var ind = new string(_indent[0], indentLevel * _indent.Length);
        var sb = new StringBuilder(ind);

        // TS 支持 private/protected constructor（I4）；public 为默认可省略
        if ((ctor.Modifiers & JavaModifiers.Private) != 0) sb.Append("private ");
        else if ((ctor.Modifiers & JavaModifiers.Protected) != 0) sb.Append("protected ");

        sb.Append("constructor(");
        var parms = ctor.Parameters.Select(p => $"{TsNaming.SafeIdentifier(p.Name)}: {_exprWriter.MapType(p.Type)}");
        sb.Append(string.Join(", ", parms));
        sb.Append(')');

        sb.AppendLine(" {");

        // 基类构造调用：IR 中为 Java 风味字符串（实际属性名 Initializer，非 BaseConstructorCall），
        // 如 "super(a, b)"。TS 要求派生类构造器必须显式调用 super()（TS2377/TS17009）：
        // 类有 ExtendedType 时无条件发射 super(...)（有实参原样，无实参 super();）。
        // "this(...)" 重载委托无 TS 对应 → 诊断注释（重载构造器在 M3 转工厂）。
        if (!string.IsNullOrEmpty(ctor.Initializer))
        {
            if (ctor.Initializer.StartsWith("super("))
            {
                sb.Append(ind).Append(_indent).AppendLine($"{ctor.Initializer};");
            }
            else
            {
                // this(...) 委托：先满足基类 super() 要求，再注释说明委托无法直译
                if (!string.IsNullOrEmpty(extendedType))
                {
                    sb.Append(ind).Append(_indent).AppendLine("super();");
                }
                sb.Append(ind).Append(_indent).AppendLine($"/* cs2j: unsupported ctor delegation: {ctor.Initializer} */");
            }
        }
        else if (!string.IsNullOrEmpty(extendedType))
        {
            sb.Append(ind).Append(_indent).AppendLine("super();");
        }

        if (ctor.StructuredBody != null)
        {
            foreach (var stmt in ctor.StructuredBody.Statements)
            {
                sb.AppendLine(_stmtWriter.Emit(stmt, indentLevel + 1));
            }
        }
        else if (!string.IsNullOrEmpty(ctor.Body))
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

    /// <summary>
    /// 将 Java 风味字面量归一化为合法 TS 字面量（I2，等价于 TsExpressionWriter.EmitLiteral 的
    /// 字符串版）：long/ulong 后缀 L → bigint 后缀 n（100L→100n、0xFFL→0xFFn、1_000L→1_000n）；
    /// float/double 后缀 f/d → 去掉（1.5f→1.5）；char 字面量 'a' → "a"。
    /// 仅当整个字符串为字面量形态时才转换，避免误伤表达式/标识符。
    /// </summary>
    private static string NormalizeLiteral(string literal)
    {
        if (literal.Length >= 3 && literal[0] == '\'' && literal[^1] == '\'' && !literal[1..^1].Contains('"'))
        {
            return "\"" + literal[1..^1] + "\"";
        }

        if (literal.EndsWith("L", StringComparison.Ordinal) && IsNumericLiteralBody(literal[..^1]))
        {
            return literal[..^1] + "n";
        }

        if (literal.EndsWith("f", StringComparison.OrdinalIgnoreCase) && IsNumericLiteralBody(literal[..^1]))
        {
            return literal[..^1];
        }

        return literal;
    }

    private static bool IsNumericLiteralBody(string body)
    {
        if (body.Length == 0) return false;

        // 十六进制（C# uint/ulong 常量渲染为 0x... / 0x...L），可含下划线
        if (body.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return body.Length > 2 && body[2..].All(c => Uri.IsHexDigit(c) || c == '_');
        }

        // 十进制整数/浮点，可含下划线、小数点、指数（100、1_000、1.5、1e5、-100）
        var start = body[0] is '+' or '-' ? 1 : 0;
        var anyDigit = false;
        var anyFraction = false;
        var anyExponent = false;
        for (var i = start; i < body.Length; i++)
        {
            var c = body[i];
            if (char.IsAsciiDigit(c)) { anyDigit = true; continue; }
            if (c == '_') continue;
            if (c == '.' && !anyFraction && !anyExponent) { anyFraction = true; continue; }
            if ((c == 'e' || c == 'E') && anyDigit && !anyExponent) { anyExponent = true; continue; }
            if ((c == '+' || c == '-') && anyExponent && i > start) { continue; }
            return false;
        }
        return anyDigit;
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

        // 构造器：TS 只允许一个 constructor（TS2392）。第一个发射为 constructor(...)，
        // 第二个起降级为诊断注释（M3 重载转工厂/参数对象占位），不发射非法语法。
        for (int i = 0; i < cls.Constructors.Count; i++)
        {
            if (i == 0)
            {
                sb.AppendLine(_memberWriter.EmitConstructor(cls.Constructors[i], cls.ExtendedType, indentLevel + 1));
            }
            else
            {
                var ctor = cls.Constructors[i];
                var parms = string.Join(", ", ctor.Parameters.Select(p => $"{TsNaming.SafeIdentifier(p.Name)}: {_memberWriter.MapType(p.Type)}"));
                sb.Append(ind).Append(_indent)
                  .AppendLine($"// TODO(TS): 第 {i + 1} 个构造器({parms}) 参数无法直译，需工厂/参数对象占位");
                if (!string.IsNullOrEmpty(ctor.Initializer) && ctor.Initializer.StartsWith("this("))
                {
                    sb.Append(ind).Append(_indent)
                      .AppendLine($"// TODO(TS): 委托构造 {ctor.Initializer} 无法直译，仅注释说明");
                }
            }
            sb.AppendLine();
        }

        // 方法
        for (int i = 0; i < cls.Methods.Count; i++)
        {
            sb.AppendLine(_memberWriter.EmitMethod(cls.Methods[i], indentLevel + 1));
            if (i < cls.Methods.Count - 1) sb.AppendLine();
        }

        // 静态初始化块 -> static { } （ES2022）；实际属性是 Statements（List<string>，非 Body）
        foreach (var staticInit in cls.StaticInitializers)
        {
            sb.Append(ind).Append(_indent).AppendLine("static {");
            foreach (var stmtLine in staticInit.Statements)
            {
                if (!string.IsNullOrWhiteSpace(stmtLine))
                {
                    sb.Append(ind).Append(_indent).Append(_indent).AppendLine(stmtLine.Trim());
                }
            }
            sb.Append(ind).Append(_indent).AppendLine("}");
        }

        sb.Append(ind).Append('}');

        // 嵌套类型提升到外层类之后、文件顶层发射（TS1068：类体内不允许嵌套类型声明；
        // spec 顶层顺序：主类型 → 其嵌套类型；多个顶层类型各自后跟其嵌套类型）
        foreach (var nested in cls.NestedTypes)
        {
            sb.AppendLine();
            sb.AppendLine();
            sb.Append(Emit(nested, indentLevel));
        }

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

        // 继承的接口（实际属性名：ExtendedTypes，非 ImplementedTypes）
        if (iface.ExtendedTypes.Count > 0)
        {
            sb.Append($" extends {string.Join(", ", iface.ExtendedTypes)}");
        }

        sb.AppendLine(" {");

        // 成员（接口无修饰符，无 body；参数/返回类型经 Java→TS 映射，与类成员一致）
        foreach (var method in iface.Methods)
        {
            var parms = string.Join(", ", method.Parameters.Select(p => $"{TsNaming.SafeIdentifier(p.Name)}: {_memberWriter.MapType(p.Type)}"));
            sb.Append(ind).Append(_indent)
              .AppendLine($"{TsNaming.SafeIdentifier(method.Name)}({parms}): {_memberWriter.MapType(method.ReturnType)};");
        }

        sb.Append(ind).Append('}');
        return sb.ToString();
    }

    private string EmitEnum(JavaEnumDeclaration en, int indentLevel)
    {
        var ind = new string(_indent[0], indentLevel * _indent.Length);
        var sb = new StringBuilder(ind);

        // 普通 enum 直译（[Flags] 在 M2 Task 17 处理）；实际属性名：Values（非 Members）
        sb.AppendLine($"export enum {TsNaming.SafeClassName(en.Name)} {{");
        for (int i = 0; i < en.Values.Count; i++)
        {
            var comma = i < en.Values.Count - 1 ? "," : "";
            sb.Append(ind).Append(_indent).AppendLine($"{en.Values[i]}{comma}");
        }
        sb.Append(ind).Append('}');
        return sb.ToString();
    }
}
```

注：类型/成员属性名已对照 `src/CSharpToJava.Core/Java/JavaTypeDeclaration.cs`、`JavaMemberDeclaration.cs` 实际定义核对（见文首“IR 属性名契约”：`ExtendedTypes`/`Values`/`Initializer`/`StructuredBody`/`Statements`）。

注：**2026-08-06 复审修订（审查结论优先于原计划，tsc 7.0.2 实证）**：C1 多构造器仅首个发射 `constructor(...)`，第二起以诊断注释占位（TS2392）；C2 派生类构造器无条件发射 `super(...)`（有实参原样、无实参 `super();`，TS2377/TS17009）；C3 表达式体方法/属性 getter 输出 `{ return expr; }`、void 输出 `{ expr; }`（TS1144）；C4 嵌套类型提升到外层类之后、文件顶层发射（TS1068）；I1 无初始化器且零值为 null 的字段类型放宽为 `T | null`（TS2322）；I2 字段常量初始化器经字面量归一化（`L`→`n`、去 `f`/`d` 后缀、char→string，TS1351）；I3 接口成员参数/返回类型走 `MapType`；I4 构造器 `private`/`protected` 可见性直译。测试增至 24 个（含 L3 tsc 编译回归）。

- [ ] **Step 5: 运行测试验证通过**

Run: `dotnet test tests/CSharpToTypeScript.Tests/CSharpToTypeScript.Tests.csproj --filter "FullyQualifiedName~TsBasicClassEmissionTests"`
Expected: PASS（24 个测试）。

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
        var memberWriter = new TsMemberWriter(stmtWriter, exprWriter);
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

`ConversionPipeline` **没有 `_options` 字段**（与 ProjectConversionPipeline 不同，已核对），因此需将 `CreateSingleFilePasses`（当前位于 `ConversionPipeline.cs` 约第 403 行的无参实例方法）改为接受选项参数：

```csharp
private IReadOnlyList<ICs2jPass<SingleFilePassState>> CreateSingleFilePasses(ConversionOptions options)
{
    if (options.TargetLanguage == Context.TargetLanguage.TypeScript)
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

调用处（`Convert` 方法内，约第 222 行）同步改为：

```csharp
Cs2jPassExecutor.Execute(passState, context, CreateSingleFilePasses(request.Options), passMetrics);
```

注：Java 路径的 `SingleFileJavaEmitPass` 还会运行内置兼容/结构 rewriter（`RunBuiltInRewriters`/`RunStructureRewriters`，见 `SingleFilePasses.cs` 约第 298 行起）。M1 的 TS pass 有意只跑用户注册 rewriter（其中多数为 Java 专属）；若后续发现目标无关的内置 rewriter（如变量名去重）对 TS 产物必要，再逐项加入 `SingleFileTsEmitPass`，避免一次性引入 Java 专属变换。

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

注：`ConvertOptions` 是 internal class（已核对：Program.cs 中无可见性修饰符），需在 `CSharpToJava.CLI.csproj` 加 `InternalsVisibleTo`（具体做法见 Step 5 的 csproj ItemGroup，不要在 Program.cs 加 assembly 特性，避免两种方式重复）。

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

在 `ConvertFile` 方法（约第 34 行，其 `ConversionOptions` 初始化在约第 47-52 行）添加：

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

对 `ConvertProject` 方法做同样修改（其 `ConversionOptions` 初始化在约第 229-234 行）。另注意：`-t` 短选项在两个 Verb 的选项类中均未被占用（已核对：convert 使用 i/o/m/j/v，convert-project 使用 s/m/j 等），可安全使用。

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
        // C# IComparable 在 IR 中映射为 Java 风味约束名（如 Comparable），断言只验证 extends 结构
        Assert.Contains("Container<T extends ", result.GeneratedCode);
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
        Assert.Contains("Max<T extends ", result.GeneratedCode);
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

- [ ] **Step 3: 验证 IR 是否携带类型参数并修正约束发射**

`JavaTypeDeclaration.TypeParameters` 已存在（`JavaTypeParameter` 定义在 `JavaTypeDeclaration.cs` 约第 351-368 行，已核对）：属性为 `Name` 与 `Bounds`（`List<string>`，**没有 `Constraint` 属性**）。`JavaTypeParameter.ToString()` 输出 Java 风味 `T extends A & B`，而 **TS 泛型约束只允许单一类型且不支持交叉类型**，因此不能直接用 `tp.ToString()`。

在 `TsTypeWriter` 中添加类型参数格式化辅助方法：

```csharp
private string FormatTypeParameters(IReadOnlyList<JavaTypeParameter> typeParams)
{
    if (typeParams.Count == 0) return "";
    var parts = typeParams.Select(tp =>
    {
        if (tp.Bounds.Count == 0) return TsNaming.SafeIdentifier(tp.Name);
        // TS 约束仅支持单一类型：取首个 bound；多 bound（Java "A & B"）
        // 丢弃其余并加诊断注释（spec §1.4.3：约束仅部分表达）
        var extra = tp.Bounds.Count > 1
            ? " /* cs2j: dropped bounds: " + string.Join(" & ", tp.Bounds.Skip(1)) + " */"
            : "";
        return $"{TsNaming.SafeIdentifier(tp.Name)} extends {tp.Bounds[0]}{extra}";
    });
    return $"<{string.Join(", ", parts)}>";
}
```

将 `EmitClass`/`EmitInterface`/方法发射中的类型参数输出改为调用 `FormatTypeParameters(...)`（替换 Task 9 中的 `tp.ToString()` 拼接）。

注：`new()` 约束在 IR 中无直接表示（Java 无对应），其降级（工厂参数）在 M3 TsGenericErasureRewriter 处理（spec §2.6.2）。

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

已核对 IR 实际形态：接口属性由 `PropertyTransformer` 转为 `getX()`/`setX(value)` 方法（`"get" + PascalCase`，**不是 `get_X`**），且标记 `IsAutoGenerated = true`（接口上下文中无后备字段）。`JavaInterfaceDeclaration` 定义在 `JavaTypeDeclaration.cs` 约第 217 行（`ExtendedTypes`/`Methods`/`Fields`/`NestedTypes`）。

在 `EmitInterface` 中将自动生成的访问器方法合并发射为 TS 属性声明：

```csharp
// 检测 getX/setX 自动生成方法对，合并为属性（名称取 get/set 后 PascalCase 原名）
static bool IsAccessor(JavaMethodDeclaration m, out string propName)
{
    propName = "";
    if (!m.IsAutoGenerated || m.Name.Length <= 3) return false;
    if (m.Name.StartsWith("get") || m.Name.StartsWith("set"))
    {
        propName = m.Name.Substring(3);   // 保持 PascalCase 原名
        return propName.Length > 0 && char.IsUpper(propName[0]);
    }
    return false;
}

var propertyGroups = iface.Methods
    .Where(m => IsAccessor(m, out _))
    .GroupBy(m => m.Name.Substring(3))
    .ToList();

var pureMethods = iface.Methods.Where(m => !IsAccessor(m, out _)).ToList();

foreach (var grp in propertyGroups)
{
    var getter = grp.FirstOrDefault(m => m.Name.StartsWith("get"));
    if (getter != null)
    {
        // 只读属性（无 setter）可加 readonly 前缀
        var readOnly = grp.All(m => !m.Name.StartsWith("set"));
        sb.Append(ind).Append(_indent)
          .AppendLine($"{(readOnly ? "readonly " : "")}{TsNaming.SafeIdentifier(grp.Key)}: {_exprWriter.MapType(getter.ReturnType)};");
    }
}

foreach (var method in pureMethods)
{
    // 原有方法发射（参数/返回值类型同样经 MapType）
}
```

注：上述代码需 `TsTypeWriter` 能访问表达式发射器的 `MapType`——将 `TsTypeWriter` 构造函数改为同时接收 `TsExpressionWriter`（或经由 `TsMemberWriter` 暴露），并同步更新 Task 10 `EmitType` 的组装代码。只写属性（仅 setter）无 TS 对应形态 → 保留 setter 方法发射 + 诊断注释；接口默认实现方法（`JavaModifiers.Default`）的降级按 spec §2.5（拷贝到实现类/伴随辅助/诊断）在 M3 处理。

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

注（已核对 IR 实际形态）：C# 属性在 HIR 阶段已被 `PropertyTransformer`（`src/CSharpToJava.Core/Transformers/Member/PropertyTransformer.cs`）转为：私有后备字段（camelCase）+ `getX()`/`setX(value)` 方法（`"get" + PascalCase`，非 `get_X`），自动生成的访问器方法标记 `IsAutoGenerated = true`。表达式体属性（无后备字段）仅生成 getter。因此 TS 侧需“重组”而非直接发射。

- [ ] **Step 3: 识别并发射属性访问器**

新增 `src/CSharpToJava.Core/TypeScript/Rewriters/TsPropertyReconstructionRewriter.cs`（在 `SingleFileTsEmitPass.RunTsRewriters` 中装配）：

1. **识别**：逐类扫描 `Methods` 中 `IsAutoGenerated == true` 且名称符合 `get{Name}`/`set{Name}` 模式的方法，按 `Name`（去前缀后的属性名）配对；同时定位对应的私有后备字段（camelCase 名）。
2. **标记**：在配对的方法上打标记（可新增 `CSharpToJava.Core.TypeScript` 内部的侧表 `Dictionary<JavaMethodDeclaration, TsAccessorRole>`，不改共享 IR 类），记录属性名/可见性/是否只读。
3. **调用点同步改写**：遍历全部 `JavaMethodCallExpression`：`target.getX()` → `JavaMemberAccessExpression(target, X)`；`target.setX(v)` → `JavaAssignmentExpression(Target=MemberAccess(target,X), Value=v)`（赋值表达式在 TS 中可内联）。无目标类型信息时按方法名模式匹配（get/set + 首字母大写），误配风险用 L3 tsc 回归兜底。
4. **发射**：`TsTypeWriter.EmitClass` 对打了标记的方法发射为 TS 访问器：

```typescript
get X(): number { return this.x; }
private set X(value: number) { this.x = value; }
```

   getter/setter 方法体仍走 Task 9 的 StructuredBody/Body 发射路径；后备字段保留为私有字段。表达式体属性的 getter 体即原表达式（`return this.count * 2;`）。
5. **不重组的场景**（非自动生成、无法配对）→ 维持方法发射 + 诊断注释。

在 `tests/CSharpToTypeScript.Tests/TsPropertyReconstructionTests.cs` 中为 rewriter 补充单测（get/set 配对、调用点改写、只读属性不生成 setter）。

- [ ] **Step 4: 运行测试验证通过**

Run: `dotnet test tests/CSharpToTypeScript.Tests/CSharpToTypeScript.Tests.csproj --filter "FullyQualifiedName~TsPropertyEmissionTests"`
Expected: PASS（4 个测试）+ TsPropertyReconstructionTests 全绿。

- [ ] **Step 5: 提交**

```bash
git add src/CSharpToJava.Core/TypeScript/Rewriters/TsPropertyReconstructionRewriter.cs src/CSharpToJava.Core/TypeScript/TsTypeWriter.cs tests/CSharpToTypeScript.Tests/TsPropertyEmissionTests.cs tests/CSharpToTypeScript.Tests/TsPropertyReconstructionTests.cs
git commit -m "feat(ts): reconstruct C# properties as TS accessors"
```

---

## Task 17: 枚举（显式值与 [Flags]）

**Files:**
- Modify: `src/CSharpToJava.Core/TypeScript/TsTypeWriter.cs`
- Create: `tests/CSharpToTypeScript.Tests/TsEnumEmissionTests.cs`

- [ ] **Step 1: 核对 IR 中枚举形态**

先转换样本确认：`JavaEnumDeclaration.Values` 是否携带显式数值（C# `enum E { A = 1 }`）。Java 枚举无数值，若 IR 丢失显式值，需在 HIR 层保留（通过 `JavaEnumDeclaration.Annotations` 或新增 TS 侧可读取的旁路信息）——优先不改共享 IR，若信息确实丢失且无法旁路，再最小侵入扩展 `JavaEnumDeclaration`（加可选字段，Java 发射忽略）并同步 spec。

- [ ] **Step 2: 实现发射**

1. 普通枚举：直译 `export enum X { A = 1, ... }`（显式值可用时）。
2. `[Flags]` 枚举（spec §3.2）：发射为 const 对象 + 联合类型：

```typescript
export const Style = { None: 0, Bold: 1, Italic: 2 } as const;
export type Style = (typeof Style)[keyof typeof Style];
```

   `HasFlag` 调用 → `(v & flag) === flag`（M3/M4 兼容库 `CsEnum.hasFlag` 可选）。
3. 测试：`TsEnumEmissionTests`（显式值断言、flags 形态断言、`Enum.Parse` 等复杂 API → 诊断 + 兼容库桩）。

- [ ] **Step 3: 运行测试 + 提交**

Run: `dotnet test tests/CSharpToTypeScript.Tests/CSharpToTypeScript.Tests.csproj --filter "FullyQualifiedName~TsEnumEmissionTests"`
Expected: PASS。

---

## Task 18: 字符串插值与 String.Format

**Files:**
- Modify: `src/CSharpToJava.Core/TypeScript/TsExpressionWriter.cs`
- Modify: `config/typescript/MemberMappings.json`
- Create: `tests/CSharpToTypeScript.Tests/TsStringInterpolationTests.cs`

- [ ] **Step 1: 核对 IR 中插值形态**

C# 插值字符串在 HIR 后的实际形态（字符串拼接 `+` / Java `String.format(...)` / Raw 表达式）需先转换样本确认，再决定发射策略。

- [ ] **Step 2: 实现**

1. 简单插值（无格式符）→ 模板字符串 `` `${x}` ``（spec §1.4.7）。
2. 带格式符（`{0:F2}`）或 `string.Format` 调用 → `CsString.format(...)`（MemberMappings 已配 `Format` 映射；若 IR 中已是 Java `String.format`，在内置表补 `("java.lang.String","format") → "CsString.format"`，imports 加 compat）。
3. 测试：`TsStringInterpolationTests`（spec §5.3 M2 组）。

- [ ] **Step 3: 运行测试 + 提交**

---

## Task 19: 数值语义（类型感知的整数除法/窄化/混算）

**Files:**
- Create: `src/CSharpToJava.Core/TypeScript/Rewriters/TsNumericSemanticsRewriter.cs`
- Modify: `src/CSharpToJava.Core/Pipeline/Passes/SingleFilePasses.cs`（`RunTsRewriters` 装配）
- Create: `tests/CSharpToTypeScript.Tests/TsNumericSemanticsTests.cs`

- [ ] **Step 1: 设计**

IR 不携带类型信息（同 §2.6.1 重载消歧的困境），类型感知必须由 rewriter 持有 Roslyn 语义模型回调：`SingleFileTsEmitPass` 装配时注入 `state.Context.SemanticModel`。

- [ ] **Step 2: 实现**

1. **整数除法**：两操作数经语义模型判定均为 C# 整型（非 long/ulong）时，将 `JavaBinaryExpression("/")` 替换为 `JavaRawExpression($"Math.trunc({l} / {r})")`；long/ulong（bigint）不包（JS bigint 除法天然截断，spec §2.5 修订注记）。补齐 Task 7 M1 阶段仅字面量包装的缺口。
2. **窄化转换**：`(byte)e`/`(sbyte)e` 等 → 掩码/截断辅助（沿用 Java 目标的 `& 0xFF` 策略，见 spec §1.4.2）。
3. **bigint/number 混算**：语义模型发现两侧类型不一致时插入显式 `BigInt(...)`/`Number(...)` 转换；无法判定时诊断。
4. `List.Clear()` 语句级特殊分支（Task 2 排除项）：调用点 `x.Clear()`（接收者为数组映射类型）→ `x.length = 0` 语句改写。
5. 测试：`TsNumericSemanticsTests`（spec §5.3：整数除法包 Math.trunc、窄化掩码、混算转换；另加“double 除法不被误包”反向用例）。

- [ ] **Step 3: 运行测试 + Java 零回归 + 提交**

---

## Task 20: 可空值类型与 null 统一

**Files:**
- Modify: `src/CSharpToJava.Core/TypeScript/TsExpressionWriter.cs` / `TsMemberMappingTable.cs`
- Create: `tests/CSharpToTypeScript.Tests/TsNullableHandlingTests.cs`

- [ ] **Step 1: 核对 IR 中可空形态**

C# `int?` 在 Java 目标映射为装箱 `Integer`/`Optional`（视 `UseOptionalForNullable`）。TS 目标统一 `number | null`（spec §1.4.7），需先转换样本确认 IR 实际形态。

- [ ] **Step 2: 实现**

1. 类型映射：装箱类型 `Integer/Long/Boolean/...` → `number | null`/`bigint | null`/...（内置 Java 类型表扩展，注意与 Task 5 非可空映射的区分：发射器按上下文选择）。
2. `.HasValue` → `!== null`；`.Value` → 直接取值（必要时 `!` 断言或运行时检查 + 抛 `InvalidOperationException` 兼容类）。
3. `??`/`??=` 直译；产物中不出现 `undefined`（断言用例，spec §5.3）。
4. 测试：`TsNullableHandlingTests`。

- [ ] **Step 3: 运行测试 + 提交**

---

## Task 21: 异常发射与兼容异常类映射

**Files:**
- Modify: `src/CSharpToJava.Core/TypeScript/TsStatementWriter.cs`（catch 类型名映射）
- Modify: `src/CSharpToJava.Core/TypeScript/TsMemberMappingTable.cs`（Java 异常名 → 兼容库异常名内置表）
- Create: `tests/CSharpToTypeScript.Tests/TsExceptionEmissionTests.cs`

- [ ] **Step 1: 实现**

1. catch/throw 中的 Java 风味异常名（如 `IllegalArgumentException`/`IllegalStateException`）映射到兼容库名（`ArgumentException`/`InvalidOperationException`，spec §4.3.9）；未映射异常 → `CsException` + 诊断。
2. `new IllegalArgumentException("msg")` → `new ArgumentException("msg")`，imports 自动加 compat（`WithImports` 机制）。
3. catch 按类型过滤的 instanceof 链已在 Task 8 实现；本任务验证其与兼容异常类配合（异常类是 class，`instanceof` 可用，spec §4.3.9）。
4. 测试：`TsExceptionEmissionTests`（spec §5.3 M2 组）。

- [ ] **Step 2: 运行测试 + 提交**

---

## Task 22: M2 验收（L3 冒烟编译 + 零回归）

**Files:**
- Create: `tests/CSharpToTypeScript.Tests/TsSmokeCompileTests.cs`

- [ ] **Step 1: 实现冒烟编译测试**

将 M1+M2 全部特性样本（类/字段/泛型/接口/属性/枚举/异常/插值/数值/可空）合并为一个多文件工程，调用基类 `AssertCompiles`（共享 fixture）整体 `tsc --noEmit`（spec §5.5）。无 Node 环境时自动 Skipped。

- [ ] **Step 2: 全量验收**

Run: `dotnet test tests/CSharpToTypeScript.Tests/CSharpToTypeScript.Tests.csproj`
Run: `dotnet test tests/CSharpToJava.Tests/CSharpToJava.Tests.csproj`（Java 零回归）
Expected: 全绿（L3 在有 Node 环境时通过）。

- [ ] **Step 3: 提交**

---

# 里程碑 M3：语义降级（S 级 rewriter + 特性降级）

**验收标准：** §5.4 全部 rewriter 测试全绿 + §5.3 M3 组全绿 + L3；Java 零回归。

> M3 任务以大纲形式给出（每个任务仍遵循“写失败测试→实现→验证→提交”循环）；开工前逐个展开为细粒度步骤，展开时需先核对相关 IR 形态（参照 M1/M2 任务中的核对惯例）。

## Task 23: TsOverloadDisambiguationRewriter（方法重载改写，spec §2.6.1）

- **Files:** `Rewriters/TsOverloadDisambiguationRewriter.cs`、`TsOverloadRewriterTests.cs`
- **要点：** 同名重载分组；首个保留原名，其余追加 `TsNaming.OverloadSuffix` 类型后缀；调用点按实参数量 + 语义模型静态类型消歧（装配时注入 `SemanticModel` 回调）；构造器重载 → `Create$N` 静态工厂 + `new` 调用点同步；无法静态判定 → 诊断 + 保留首重载。
- **验证：** §5.4 `TsOverloadRewriterTests` 全部用例 + L3。

## Task 24: TsGenericErasureRewriter（泛型擦除降级，spec §2.6.2）

- **Files:** `Rewriters/TsGenericErasureRewriter.cs`、`TsGenericErasureRewriterTests.cs`
- **要点：** `default(T)` 类型特定零值/`csDefault`；`new T()` 工厂参数注入；`typeof(T)` → TypeToken；`typeof(SomeClass)` 直译类引用（`.Name`→`.name`）；`where T : new()/struct/class` 约束移除联动。

## Task 25: TsInterfaceProbeRewriter（接口运行时检查，spec §2.6.3）

- **Files:** `Rewriters/TsInterfaceProbeRewriter.cs`、`TsInterfaceProbeRewriterTests.cs`
- **要点：** 每接口生成 `is$IXxx` 探针函数（必需方法存在性检查 + 继承链合并）；`is/as` 调用点改写；实现类构造器注入 `CsRuntime.markImplements` 标记（依赖 M4 兼容库，M3 可先用占位实现）；纯属性接口诊断。

## Task 26: TsEqualityRewriter（相等语义，spec §2.6.4）

- **Files:** `Rewriters/TsEqualityRewriter.cs`、`TsEqualityRewriterTests.cs`
- **要点：** record/struct 与覆写 Equals 的类的 `==`/`.Equals()` 调用点 → `CsObject.equals`；原始类型/字符串 `==` → `===`；`ReferenceEquals` → `===`；`GetHashCode` → `hashCode()`。

## Task 27: 委托/事件/ref-out/索引器/运算符/模式匹配/yield/async/扩展方法/record 发射

按 spec §5.3 M3 测试类逐项实现，每项一个子任务（测试类名即验收项）：

| 子任务 | 测试类 | 关键策略（spec 章节） |
|---|---|---|
| 委托→函数类型 | `TsDelegateToFunctionTypeTests` | `Func<T,R>` → `(x: T) => R`（§3.1.3） |
| 事件 | `TsEventEmissionTests` | `CsEvent<T>`，`+=`→`Add`（§4.3.7） |
| ref/out 参数 | `TsRefOutParamTests` | `Ref<T>` 包装 + `refOf`（§4.3.1）；注意 IR 中 Java 目标已用 Holder 包装（`JavaVariableDeclarationStatement.HolderInfo`），TS 侧需映射 Holder→`Ref<T>` |
| 索引器 | `TsIndexerTests` | `get_Item/set_Item`（§1.4.3；注意 Java 目标 IndexerTransformer 实际生成 get/set/put，发射期二次改名） |
| 运算符重载 | `TsOperatorOverloadTests` | `op_Add` 静态方法 + 调用点改写（§3.2） |
| 模式匹配 | `TsPatternMatchingTests` | 类型模式→instanceof+守卫；复杂模式→if 链（§1.4.4） |
| yield→generator | `TsYieldToGeneratorTests` | `function*` + `IterableIterator<T>`（§1.4.4） |
| async/await | `TsAsyncAwaitTests` | `Promise<T>` 直译；`Task.WhenAll`→`Promise.all`；`Task.Run` 诊断（§1.4.5） |
| 扩展方法 | `TsExtensionMethodTests` | 静态宿主调用（§1.4.3） |
| record | `TsRecordEmissionTests` | class + `CsRecord` 基类（§4.3.2）；注意 IR 中 `JavaClassDeclaration.IsRecord`/`RecordComponents` |

- **验证：** 每子任务完成后运行对应测试类 + L3；M3 收尾时全量 TS 测试 + Java 零回归。

---

# 里程碑 M4：兼容运行时库（csharptotypescript-compat）+ BCL 映射

**验收标准：** §5.6 vitest 全绿；含 BCL 调用样本转换后过 L3。

> M4 与 M2/M3 可并行（仅依赖 M1）。任务按 spec §4.3 组件拆分，每组件一个任务（实现 + vitest 语义测试）：

## Task 28: 兼容库脚手架

- **Files:** `typescript/csharptotypescript-compat/`（package.json name `@csharptotypescript/compat`、ESM、strict tsconfig、vitest.config.ts、src/index.ts）
- **验证：** `npm install` + `npx tsc --noEmit` + `npx vitest run` 在空骨架上通过。

## Task 29-35: 逐组件实现（每个含 vitest 对齐 C# 行为的语义测试，spec §5.6）

| Task | 组件 | 关键验收（spec 章节） |
|---|---|---|
| 29 | `core/`：Ref、CsObject、CsRecord、CsRuntime、TypeToken、csDefault、cloneStruct | 逐字段值相等/哈希稳定（§4.3.1/4.3.2/4.3.3） |
| 30 | `time/`：CsDateTime、CsTimeSpan | ticks=100ns bigint；闰年/MinValue/MaxValue（§4.3.4） |
| 31 | `text/`：CsString、CsStringBuilder | `format` 格式符、split 重载对齐（§4.3.6） |
| 32 | `collections/`：CsDictionary、CsSet | 对象键 Equals/HashCode；重复 Add 抛异常；插入序（§4.3.5） |
| 33 | `events/`：CsEvent | 多播顺序/不去重/空事件安全（§4.3.7） |
| 34 | `exceptions/`：CsException 层次 | spec §4.3.9 全部类 |
| 35 | `primitives/`：CsDecimal、CsChar、CsGuid | `0.1+0.2===0.3`（定点）；char 算术辅助（§4.3.10） |

## Task 36: MemberMappings 生效与 LINQ 目标分叉

- **要点：** 发射器实际消费 MemberMappings.json 的静态调用映射（`CsString.*` 等，imports 自动收集）；LINQ 策略按目标分叉（spec §2.4：M4 起 TS 用数组方法链/`Enumerable` 惰性链，§4.3.8）；`Enumerable` generator 实现 + `Enumerable.test.ts` 惰性验证。
- **验证：** 含 BCL 调用的样本转换后过 L3；`dotnet test` TS 全量 + Java 零回归。

---

# 里程碑 M5：项目级输出（npm workspace + CLI 验证链）

**验收标准：** 单测语料项目级转换 → `npm install` + `tsc` + `vitest` 全通过；`DualTargetParityTests` 全绿。

## Task 37: NpmProjectPlan（spec §6.1）

- **Files:** `src/CSharpToJava.Core/TypeScript/NpmProjectPlan.cs`
- **要点：** 项目 → npm 包目录；解决方案 → workspaces（`"workspace:*"` 依赖）；tsconfig.base.json（strict/ES2022/NodeNext/`useDefineForClassFields: false`）；每类型一文件目录布局；产物指纹按目标区分（`OutputIncrementalWriteSession`）；资源拷贝。

## Task 38: ProjectConversionPipeline TS 分叉

- **Files:** `src/CSharpToJava.Core/Pipeline/ProjectConversionPipeline.cs`
- **要点：** 按 spec §2.4 逐 pass 处置：`ProjectTypeEmitPass` → TS emit pass（注入 Compilation/SemanticModel 供重载/数值 rewriter 消歧）；`ProjectCompatibilityEmitPass`/`ProjectCrossPackageImportEmitPass` → TS 变体；`ProjectJavaModuleDependencyPass` → `NpmDependencyPass`；NativeInterop 检查降级为警告。

## Task 39: 测试项目 → vitest 工程

- **要点：** `IsTestProject` 检测；xUnit/MSTest 特性映射（`[Fact]`→`it`、`[Theory]+InlineData`→`it.each`、Assert 映射，spec §6.1）。

## Task 40: CLI 自动验证链（spec §6.2）

- **要点：** TS 目标转换完成后：`npm install` → `npx tsc --noEmit -p <模块>`（`--no-verify-build` 关闭）→ 测试包 `npx vitest run`（`--no-verify-tests` 关闭）；失败即中止并输出诊断。

## Task 41: DualTargetParityTests + M5 验收（spec §5.7）

- **要点：** 同一批 C# 语料分别以 Java/TS 目标转换，断言两者均成功且关键结构特征（类型数/方法数/诊断数）对齐；项目级端到端验证链全通过。

---

## 收尾检查清单（M5 完成后）

- [ ] 全部 TS 测试项目绿 + Java 全量测试零回归
- [ ] L3/L4（tsc/vitest）在 CI 中启用（Node LTS 锁定，spec §5.8）
- [ ] 已知限制清单（spec §6.5）随产物诊断告知用户
- [ ] `analyze --target typescript` 等后续项归档至 spec §6.6（本计划不实现）