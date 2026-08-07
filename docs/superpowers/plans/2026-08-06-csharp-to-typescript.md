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

- [x] **Step 1: 写失败测试**

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

- [x] **Step 5: 提交**

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

- [x] **Step 5: 提交**

```bash
git add tests/CSharpToTypeScript.Tests/ CSharpToJavaConverter.slnx
git commit -m "feat(ts): scaffold CSharpToTypeScript.Tests project with base class"
```

---

## Task 4: TsNaming — 标识符安全化

**Files:**
- Create: `src/CSharpToJava.Core/TypeScript/TsNaming.cs`

- [x] **Step 1: 写失败测试**

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

- [x] **Step 5: 提交**

```bash
git add src/CSharpToJava.Core/TypeScript/TsNaming.cs tests/CSharpToTypeScript.Tests/TsNamingSafetyTests.cs
git commit -m "feat(ts): add TsNaming for identifier safety and overload suffixes"
```

---

## Task 5: TsMemberMappingTable — Java→TS 映射

**Files:**
- Create: `src/CSharpToJava.Core/TypeScript/TsMemberMappingTable.cs`

- [x] **Step 1: 写失败测试**

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

- [x] **Step 5: 提交**

```bash
git add src/CSharpToJava.Core/TypeScript/TsMemberMappingTable.cs tests/CSharpToTypeScript.Tests/TsMemberMappingTableTests.cs
git commit -m "feat(ts): add TsMemberMappingTable with builtin + JSON config mappings"
```

---

## Task 6: TsImportCollector — ESM import 收集

**Files:**
- Create: `src/CSharpToJava.Core/TypeScript/TsImportCollector.cs`

- [x] **Step 1: 写失败测试**

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

- [x] **Step 5: 提交**

```bash
git add src/CSharpToJava.Core/TypeScript/TsImportCollector.cs tests/CSharpToTypeScript.Tests/TsImportCollectorTests.cs
git commit -m "feat(ts): add TsImportCollector for ESM import generation"
```

---

## Task 7: TsExpressionWriter — 表达式发射

**Files:**
- Create: `src/CSharpToJava.Core/TypeScript/TsExpressionWriter.cs`

- [x] **Step 1: 写失败测试**

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

- [x] **Step 5: 提交**

```bash
git add src/CSharpToJava.Core/TypeScript/TsExpressionWriter.cs tests/CSharpToTypeScript.Tests/TsExpressionWriterTests.cs
git commit -m "feat(ts): add TsExpressionWriter with literal/binary/method-call mapping"
```

---

## Task 8: TsStatementWriter — 语句发射

**Files:**
- Create: `src/CSharpToJava.Core/TypeScript/TsStatementWriter.cs`

- [x] **Step 1: 写失败测试**

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

- [x] **Step 5: 提交**

```bash
git add src/CSharpToJava.Core/TypeScript/TsStatementWriter.cs tests/CSharpToTypeScript.Tests/TsStatementWriterTests.cs
git commit -m "feat(ts): add TsStatementWriter for control flow emission"
```

---

## Task 9: TsMemberWriter + TsTypeWriter — 成员与类型发射

**Files:**
- Create: `src/CSharpToJava.Core/TypeScript/TsMemberWriter.cs`
- Create: `src/CSharpToJava.Core/TypeScript/TsTypeWriter.cs`

- [x] **Step 1: 写失败测试**

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

- [x] **Step 1: 写失败测试**

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

- [x] **Step 2: 运行测试验证失败**

Run: `dotnet test tests/CSharpToTypeScript.Tests/CSharpToTypeScript.Tests.csproj --filter "FullyQualifiedName~TsEmitterIntegrationTests"`
Expected: FAIL（管线未分叉，仍输出 Java）。
实测：9 失败 + 1 通过（`If_Statement_Emits_Correctly` 的断言 `if (x > 0)`/`} else {` 对 Java 输出同样成立，属语言中性断言，Task 11 分叉后自然转绿）；9 个失败原因均为 GeneratedCode 仍是 Java（`public class Foo` 而非 `export class Foo`）。另见注记 ①：管线缺 Java 类型映射配置时会先失败于配置错误，测试基建已补齐。

- [x] **Step 3: 实现 TypeScriptEmitter**

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

- [x] **Step 4: 运行测试验证仍失败（管线未分叉）**

Run: `dotnet test tests/CSharpToTypeScript.Tests/CSharpToTypeScript.Tests.csproj --filter "FullyQualifiedName~TsEmitterIntegrationTests"`
Expected: 仍 FAIL（管线未分叉，SingleFileJavaEmitPass 仍输出 Java）。
实测：发射器实现后与 Step 2 一致（9 失败 + 1 通过），失败断言均为 TS 专属子串缺失（如 `export class Foo`、`value: number = 42`、`console.log("hello")` 等）。

- [x] **Step 5: 提交（发射器就绪，待管线分叉）**

```bash
git add src/CSharpToJava.Core/TypeScript/TypeScriptEmitter.cs tests/CSharpToTypeScript.Tests/TsEmitterIntegrationTests.cs tests/CSharpToTypeScript.Tests/TsConversionTestBase.cs tests/CSharpToTypeScript.Tests/CSharpToTypeScript.Tests.csproj src/CSharpToJava.Core/Pipeline/ConversionPipeline.cs docs/superpowers/plans/2026-08-06-csharp-to-typescript.md
git commit -m "feat(ts): add TypeScriptEmitter entry point"
```

> **Task 10 实现注记（2026-08-06，以实测为准）**
> ① 测试基建（Task 10 范围内补齐）：`CSharpToTypeScript.Tests.csproj` 增加 `config\TypeMappings.json`
>    （Java 类型映射配置）复制，对齐 Java 测试项目；`TsConversionTestBase.Convert` 增加
>    `TypeMappingConfigPath` 指向该配置。原因：未分叉前 `ConversionPipeline.Convert` 无条件构造
>    `TypeMappingRegistry(TypeMappingConfigPath)`，缺配置会先以 "Configuration error: Type mapping
>    configuration file not found" 失败，无法到达计划预期的"仍输出 Java"红态。
> ② `ConversionPipeline.Convert` 增加一行 `result.Compilation = passState.JavaCompilation;`：
>    `ConversionResult.Compilation` 属性文档声明"发射后保留"，但单文件路径从未接线（任务背景所述
>    ConversionPipeline.cs:218 实为 passState 的 Roslyn Compilation）。本行是直接发射绿测试的前提，
>    亦供 Task 11 使用；Java 全量 2423 个测试零回归。
> ③ 直接发射绿测试（新增 10 个，`Direct_*`，全绿）：用 `Convert(source)` 的 `result.Compilation` +
>    `new ConversionContext(options, new TypeMappingRegistry())` 调 `TypeScriptEmitter.Emit`，
>    覆盖计划全部 10 个场景。其中 4 个场景按 IR 实证修订断言（发射器忠实于 IR，非发射器 bug）：
>    - 方法名：IR 为 Java camelCase（`sum()`/`doNothing()`/`greet()`），计划断言 `Sum(): number`、
>      `DoNothing(): void` 修订为 `sum(): number`/`doNothing(): void`（PascalCase 还原属命名管线后续任务）。
>    - `Console.WriteLine`：IR 中为 `JavaRawStatement("System.out.println(\"hello\");")`，非结构化调用节点，
>      `console.log` 映射（TsExpressionWriter 已实现、Task 9 单测已覆盖结构化节点）无法生效；
>      Task 11 引入 `TsRawStatementNormalizer` 最小归一化（println → console.log）后，
>      `Direct_Console_WriteLine_Maps_To_ConsoleLog` 按 `console.log("hello")` 断言（与测试名名副其实）。
>    - `foreach`：IR 中为 `JavaRawStatement("for (int item : arr) {...}")`（前端未产出
>      `JavaForEachStatement`）；Task 11 最小归一化（增强 for 头 → `for (const item of arr)`）后
>      `Direct_ForEach_Emits_ForOf` 按 for-of 断言（并加 `sum(arr: number[]): number`）。
>    ⚠️ 对 Task 11 的提示（Task 11 同步更正，2026-08-07）：提示所述两条集成测试断言
>    （`console.log("hello")`、`for (const item of arr)`）由 Task 11 以最小 raw 归一化路线兑现——
>    `TsRawStatementNormalizer` 在发射前做 println→console.log、增强 for 头→for-of 文本归一化
>    （不做 IR 结构化，属 M2+ 范围），两条断言按计划原样保持并转绿；`Direct_*` 对应断言同步修订。
>    归一化细节与理由见 Task 11 注记 ②。
> ④ 红态与全量统计：`TsEmitterIntegrationTests` 共 20 个——计划 10 个集成测试 9 红 1 绿
>    （`If_Statement_Emits_Correctly` 断言语言中性，Java/TS 输出均满足；其余 9 个红因均为
>    GeneratedCode 仍是 Java）+ 新增 10 个直接发射绿测试全绿。TS 测试项目总计 114
>    （105 通过 / 9 失败，失败即上述 9 个集成测试；Task 9 的 94 个测试不受影响）。
>    （Task 11 同步更正：分叉后 `TsEmitterIntegrationTests` 共 21 个（10 计划 + 10 Direct + 1 新增 L3）
>    全绿，TS 项目总计 133 通过 / 0 失败；红→绿清单与断言修订详见 Task 11 注记。）
---

## Task 11: SingleFileTsEmitPass + 管线分叉

**Files:**
- Modify: `src/CSharpToJava.Core/Pipeline/Passes/SingleFilePasses.cs`
- Modify: `src/CSharpToJava.Core/Pipeline/ConversionPipeline.cs`

- [x] **Step 1: 实现 SingleFileTsEmitPass**

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

- [x] **Step 2: 修改 ConversionPipeline.CreateSingleFilePasses 按目标分叉**

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

- [x] **Step 3: 运行测试验证通过**

Run: `dotnet test tests/CSharpToTypeScript.Tests/CSharpToTypeScript.Tests.csproj --filter "FullyQualifiedName~TsEmitterIntegrationTests"`
Expected: PASS（10 个测试）。
实测：`--filter "FullyQualifiedName~TsEmitterIntegrationTests"` 全绿——计划 10 个集成测试 + 10 个
`Direct_*` + 1 个新增 L3 tsc 用例（`Pipeline_Output_Compiles_With_Tsc`），共 21/21 通过。
分叉前后红→绿清单见本 Task 末尾注记。

- [x] **Step 4: 运行 Java 零回归测试**

Run: `dotnet test tests/CSharpToJava.Tests/CSharpToJava.Tests.csproj`
Expected: 全部 PASS（Java 路径未受影响）。
实测：`dotnet test tests/CSharpToJava.Tests/CSharpToJava.Tests.csproj` → 2423 通过 / 0 失败（零回归）。

- [x] **Step 5: 提交**

```bash
git add src/CSharpToJava.Core/Pipeline/Passes/SingleFilePasses.cs src/CSharpToJava.Core/Pipeline/ConversionPipeline.cs
git commit -m "feat(ts): fork pipeline at Codegen layer with SingleFileTsEmitPass"
```
实测：实际提交 `4edeb1f8`（message 含 raw 归一化说明，见注记）。

> **Task 11 实现注记（2026-08-07，以实测为准）**
>
> ① 分叉前后红→绿清单（`TsEmitterIntegrationTests`）：
>    - 分叉前（Task 10 末）：计划 10 个集成测试 9 红 1 绿（红因 GeneratedCode 仍是 Java；
>      `If_Statement_Emits_Correctly` 断言语言中性故绿）+ 10 个 `Direct_*` 全绿。
>    - 分叉后（本 Task）：10 个集成测试全绿。其中 5 个属"分叉即转绿"（`Empty_Class`、
>      `Int_Field`、`Long_Literal`、`String_Field`、`Bool_Field`——断言为 TS 专属文本，
>      Java 输出不含）；`Class_With_Field_And_Method_Converts`/`Void_Method_Gets_Void_Return`
>      经 camelCase 断言修订转绿（`Sum(): number`→`sum(): number`、`DoNothing(): void`→
>      `doNothing(): void`，IR 实证）；`If_Statement_Emits_Correctly` 补 TS 专属金丝雀
>      `check(x: number): number`；`ForEach_Emits_ForOf`/`Console_WriteLine_Maps_To_ConsoleLog`
>      依赖本 Task 引入的 `TsRawStatementNormalizer` 最小归一化转绿（计划断言原样保持：
>      `for (const item of arr)`、`console.log("hello")`）。
>    - `Direct_*` 同步修订 3 个弱断言使其名副其实：`Direct_If_Statement_Emits_Correctly` 补
>      `check(x: number): number`；`Direct_ForEach_Emits_ForOf` 由 `for (int item : arr)` 改为
>      `for (const item of arr)`（另加 `sum(arr: number[]): number`）；
>      `Direct_Console_WriteLine_Maps_To_ConsoleLog` 由 `System.out.println("hello")` 改为
>      `console.log("hello")`。
> ② 计划外必需项（本 Task 引入，理由）：
>    - `TsRawStatementNormalizer`（新增 `src/CSharpToJava.Core/TypeScript/TsRawStatementNormalizer.cs`，
>      组件级测试 14 个）：前端对 `Console.WriteLine`/`foreach` 产出 `JavaRawStatement` 整段直通
>      （非结构化节点），分叉后原样输出为非法 TS 且计划断言不满足。M1 不做 IR 结构化与表达式层
>      重写（M2+ 范围），采用最小文本归一化：`System.out.println(`→`console.log(`、
>      `System.err.println(`→`console.error(`；增强 for 头 `for (Type var : expr)`→
>      `for (const var of expr)`（按 for 头内第一个 ` : ` 切分，Type 丢弃、expr 与 body 保留；
>      仅当 `for (` 开头、头内含 ` : ` 且左侧末 token 为标识符时改写，普通 for/三元误命中直通）。
>      接线在 `TsStatementWriter` 的 `JavaRawStatement` 分支，`Emit`/`EmitType` 两路径行为一致。
>    - `MapType` 数组后缀修复（I1）：`TsMemberMappingTable.TryMapType` 兜底返回原值前，若键以
>      `[]` 结尾递归映射元素类型并回拼 `[]`（`int[]`→`number[]`、`String[]`→`string[]`、
>      `int[][]`→`number[][]`；元素未命中维持原行为）。内置/配置查找顺序不变，映射表新增 4 测试。
> ③ 断言修订清单：集成 2 处（`sum()`/`doNothing()`）、Direct 3 处（见 ①）。
>    `Sum(): number`/`DoNothing(): void` 属 IR 实证修订（IR 方法名为 Java camelCase；
>    PascalCase 还原属命名管线后续任务），非发射器缺陷。
> ④ 测试统计：TS 项目 133 通过 / 0 失败（集成 21 = 计划 10 + Direct 10 + 新增 L3 tsc 用例 1、
>    normalizer 14、映射表 18 = 既有 14 + 新增 4，其余为 Task 9 及更早存量）；
>    Java `CSharpToJava.Tests` 2423/2423 零回归；`CSharpToJava.ConversionTests` 1125 中有 37 个
>    存量失败，与父提交 d6a0cedb 基线失败名单逐项一致（非本提交引入，只报告不修）。
> ⑤ 提交：4edeb1f8（message 追加 "and raw-statement normalization"，见 Step 5）。
> ⑥ 复审修复（2026-08-07，规格审查发现 3 个 Important 归一化器 bug + 3 个 Minor 卫生项，均先写失败测试再修）：
>    - Bug 1 三元误改写（普通 for 头）：`for (int i = flag ? a : b; i < n; i++)` 被误改写为
>      `for (const a of b)...`（非法 TS）。根因：守卫只查"左侧末 token 为标识符"，三元 `? a : b`
>      中 `a :` 亦满足。修复：`TryRewriteForHead` 增加顶层 ";" 守卫（增强 for 头不含分号；
>      跳过字符串/字符字面量判定，`"a;b".split(";")` 类实参不受影响），普通 for（含任意三元
>      及标识符操作数）一律直通。回归：`Classic_For_With_Ternary_Identifier_Operands_Passes_Through`
>      （组件）+ `Classic_For_With_Ternary_Is_Not_Rewritten`（e2e，先红后绿；raw 经典 for 的
>      Java 形态初始值保留；let 化已由 Task 13 完成（经典 for 头声明段 → for (let i: number = ...)，见 Task 13 注记）。
>    - Bug 2 println 整串 Replace 改写字符串字面量：`Console.WriteLine("System.out.println(x)")`
>      输出 `console.log("console.log(x)")`（实参语义被破坏）。修复：`NormalizePrintln` 改为扫描式
>      替换——跳过字符串/字符字面量区域（复用 `SkipQuotedLiteral`），且仅在标识符边界命中
>      `System.out.println(`/`System.err.println(`。回归：
>      `Println_Argument_Containing_Println_Text_Is_Preserved`（组件）+ e2e（先红后绿）。
>    - Bug 3 嵌套 foreach 只归一化最外层头：内层 `for (int item : row)` 原样保留（非法 TS）。
>      修复：`NormalizeEnhancedForHeads` 扫描同一 raw 文本内所有 `for (` 头逐个改写（含嵌套
>      body 内层头），body 原样保留。回归：`Nested_Enhanced_Fors_Are_All_Normalized`（组件）
>      + `Nested_ForEach_Emits_ForOf_For_All_Levels`（e2e，L3 tsc 编译通过）。
>    - Minor 卫生：Task 5 Step 4 下误粘贴的 Task 11 Step 3 实测文本已删除（根因：注记插入时
>      `String.Replace` 全量替换锚点 `Expected: PASS（10 个测试）。`，Task 5 首处被误命中），
>      还原 Task 5 原样；清理 2 处 `> ` 行尾空白（git show --check 报警项）；本次改动文件末尾
>      均补换行。
>    统计更新：TS 项目 139 通过 / 0 失败（133 存量 + 3 组件回归 + 3 e2e）；
>    Java `CSharpToJava.Tests` 2423/2423 零回归（修复后全量复跑）。
>    提交：d3dbd2e8（fix(ts): harden raw statement normalization guards）。
>
---

## Task 12: CLI 新增 --target 选项

**Files:**
- Modify: `src/CSharpToJava.CLI/Program.cs`

- [x] **Step 1: 写失败测试**

创建 `tests/CSharpToTypeScript.Tests/TsCliTargetOptionTests.cs`：

```csharp
using CommandLine;
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
        // 直接属性赋值验证（真实命令行解析见 ConvertOptions_Parses_Dash_T_Typescript）
        var opts = new ConvertOptions { Target = "typescript" };
        Assert.Equal("typescript", opts.Target);
    }

    [Fact]
    public void ConvertOptions_Parses_Dash_T_Typescript()
    {
        // CommandLineParser 真实解析 -t/--target typescript
        // ConvertOptions.Input 为 Required，需连同 -i 一起解析
        var result = Parser.Default.ParseArguments<ConvertOptions>(new[] { "-i", "sample.cs", "-t", "typescript" });
        Assert.Equal(ParserResultType.Parsed, result.Tag);
        Assert.Equal("typescript", ((Parsed<ConvertOptions>)result).Value.Target);
    }
}
```

注：`ConvertOptions` 是 internal class（已核对：Program.cs 中无可见性修饰符），需在 `CSharpToJava.CLI.csproj` 加 `InternalsVisibleTo`（具体做法见 Step 5 的 csproj ItemGroup，不要在 Program.cs 加 assembly 特性，避免两种方式重复）。
实测：`tests/CSharpToTypeScript.Tests/TsCliTargetOptionTests.cs` 已创建（与计划逐字一致，UTF-8 无 BOM）。

- [x] **Step 2: 运行测试验证失败**

Run: `dotnet test tests/CSharpToTypeScript.Tests/CSharpToTypeScript.Tests.csproj --filter "FullyQualifiedName~TsCliTargetOptionTests"`
Expected: FAIL（`Target` 属性不存在）。
实测：红形态为编译错误 `CS0122: "ConvertOptions" 不可访问，因为它具有一定的保护级别`（internal 且 IVT 缺失）；`Target` 属性不存在的错误被可访问性错误遮蔽（编译器不再解析成员），与预期红因同源。

- [x] **Step 3: 在 ConvertOptions 和 ConvertProjectOptions 添加 --target**

在 `src/CSharpToJava.CLI/Program.cs` 的 `ConvertOptions` 类（第 2029 行）添加：

```csharp
[Option('t', "target", Default = "java", HelpText = "Target language: java or typescript")]
public string Target { get; set; } = "java";
```

在 `ConvertProjectOptions` 类（第 2081 行）同样添加。
实测：`-t` 短选项复核无误——convert 已占用 i/o/m/j/v（其余均为仅长选项），convert-project 已占用 s/d/m/f/j/v（其余均为仅长选项），`-t` 空闲；两个选项类均已在类首添加 `[Option('t', "target", Default = "java", ...)]`（实际行号随前序 Task 编辑漂移，约 L2072/L2127）。

- [x] **Step 4: 在 ConvertFile/ConvertProject 方法中传递 TargetLanguage**

在 `ConvertFile` 方法（约第 34 行，其 `ConversionOptions` 初始化在约第 55-63 行）添加：

```csharp
TargetLanguage = opts.Target.Equals("typescript", StringComparison.OrdinalIgnoreCase)
    ? TargetLanguage.TypeScript
    : TargetLanguage.Java,
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

在 `ResolveJavaMetadataPath` 之后（辅助方法聚集区）添加辅助方法：

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

对 `ConvertProject` 方法做同样修改（其 `ConversionOptions` 初始化在约第 238-246 行）。另注意：`-t` 短选项在两个 Verb 的选项类中均未被占用（已核对：convert 使用 i/o/m/j/v，convert-project 使用 s/m/j 等），可安全使用。
实测：代码按计划写入两处 `ConversionOptions` 初始化；计划片段中 `Context.TargetLanguage.TypeScript/Java` 编译失败（CS0103：`Context` 名称不在作用域，`using CSharpToJava.Core.Context;` 只导入类型不导入命名空间名），已改用同文件已导入的 `TargetLanguage.TypeScript/Java`（本段代码块已同步修订，见注记①）。`-m/--mapping` 在 TS 目标下按计划写法复用为 TS TypeMappings 路径（`opts.MappingConfig ?? ResolveTypeScriptTypeMappingPath()`），Java 目标语义不变。

- [x] **Step 5: 在 CLI csproj 中复制 TS 配置**

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

实测：csproj 已有存量 `AssemblyAttribute` 式 IVT（`CSharpToJava.Tests`），本次按计划新增 `<InternalsVisibleTo Include="CSharpToTypeScript.Tests" />` ItemGroup，两种写法并存、存量行未动；两个 TS 配置 Content 已加入（构建产物 `config/typescript/TypeMappings.json`、`MemberMappings.json` 均复制成功）。

- [x] **Step 6: 运行测试验证通过**

Run: `dotnet test tests/CSharpToTypeScript.Tests/CSharpToTypeScript.Tests.csproj --filter "FullyQualifiedName~TsCliTargetOptionTests"`
Expected: PASS（2 个测试）。
实测：`--filter "FullyQualifiedName~TsCliTargetOptionTests"` → 通过 3 / 失败 0 / 跳过 0（含复审新增 `ConvertOptions_Parses_Dash_T_Typescript` 真实解析用例，见注记⑤/⑦）。

- [x] **Step 7: 手动端到端验证**

Run:
```bash
dotnet build src/CSharpToJava.CLI/CSharpToJava.CLI.csproj
dotnet run --project src/CSharpToJava.CLI/CSharpToJava.CLI.csproj -- convert -i tests/SampleProject/Sample.cs -t typescript
```
Expected: 输出包含 `export class Sample` 的 TypeScript 代码。
实测（`tests/SampleProject/Sample.cs`：两个 const long 字段的简单类，无编译阻断构造；但产物存在已知限制，见注记⑦）：
```text
$ dotnet run --project src/CSharpToJava.CLI/CSharpToJava.CLI.csproj -- convert -i tests/SampleProject/Sample.cs -t typescript
export class Sample {
    private static readonly MaxIPv4Value: bigint = (long) (4294967295L);
    private static readonly MaxIPv4Value2: bigint = (long) (4294967295L);
}
```
对照 `-t java`（与未指定 `-t` 一致）：
```text
package SampleProject;

public class Sample {
    private static final long MaxIPv4Value = (long) (4294967295L);
    private static final long MaxIPv4Value2 = (long) (4294967295L);
}
```

- [x] **Step 8: 提交**

```bash
git add src/CSharpToJava.CLI/Program.cs src/CSharpToJava.CLI/CSharpToJava.CLI.csproj tests/CSharpToTypeScript.Tests/TsCliTargetOptionTests.cs
git commit -m "feat(ts): add --target CLI option for TypeScript conversion"
```
实测：提交 `5073614a`（见注记⑥）；本地 `dev-2.x` 未推送。

> **Task 12 实现注记（2026-08-07，以实测为准）**
>
> ① 红→绿：Step 2 实测红因 `CS0122`（internal `ConvertOptions` 对测试程序集不可见），加 IVT 后同一编译错误消失；计划片段 `Context.TargetLanguage.*` 在 CLI 项目中编译失败（`CS0103`：`using CSharpToJava.Core.Context;` 只导入类型、不导入 `Context` 命名空间名），已改为同文件已导入的 `TargetLanguage.TypeScript/Java`（不新增 using，计划代码块已同步修订）。
>
> ② e2e 实测摘录（见 Step 7）：`-t typescript` 输出含 `export class Sample`（TS 分叉生效，含 `bigint` 字段）；`-t java` 输出 `package SampleProject; public class Sample { ... }`，与未指定 `-t` 一致。输入 `tests/SampleProject/Sample.cs` 为两个 const long 字段的简单类，无编译阻断构造；但产物存在已知限制（cast 泄漏，见注记⑦），不暗示产物合法。
>
> ③ 测试统计：TS 项目 139 → 142（新增 3 个 CLI 选项测试，复审补真实解析用例）/ 0 失败；Java `CSharpToJava.Tests` 2423/2423 零回归。
>
> ④ 已登记 gap（Task 38/M5 范围，本任务未改）：`convert-project -t typescript` 的工程级 TS 分叉整体未生效——`ProjectConversionPipeline` 不消费 `TargetLanguage`（`src/CSharpToJava.Core/Pipeline/ProjectConversionPipeline.cs` 中零引用，已实证），产物内容仍为 Java；且输出文件名硬编码为 `.java` 扩展名（`src/CSharpToJava.CLI/Program.cs` L791 `EndsWith(".java")`、L793 与 L801 两处 `Path.ChangeExtension(..., ".java")`，共三处）。Task 12 只做 Options 传递，工程级 TS 产物/扩展名逻辑留待 Task 38 按 spec §2.4 分叉处理。
>
> ⑤ 偏离/选择记录：
>    - `Context.TargetLanguage.*` → `TargetLanguage.*`（编译错误驱动，见 ①）。
>    - 两个 Resolve 辅助方法置于 `ResolveJavaMetadataPath` 之后（辅助方法聚集区），而非字面"文件末尾"（文件末尾为选项类，放辅助区更一致）。
>    - IVT 采用计划指定的 `<InternalsVisibleTo Include="..." />` ItemGroup；存量 `AssemblyAttribute` 式（`CSharpToJava.Tests`）未动，两种机制并存且均生效。
>    - `-m/--mapping` 在 TS 目标下按计划复用为 TS TypeMappings 路径；Java 目标语义不变。
>    - 复审（M-1）：新增 `ConvertOptions_Parses_Dash_T_Typescript` 真实解析用例（`Parser.Default.ParseArguments<ConvertOptions>` + `-i`/`-t` 全量 args）；实测 `ConvertOptions.Input` 为 `Required = true`，仅传 `-t` 得到 `NotParsed`，用例须带 `-i`。CommandLineParser 经 CLI 项目 PackageReference 传递可用，TS 测试 csproj 无需显式引用。
>    - 复审（M-5）：测试文件补末尾换行；原属性自证测试上的误导性注释已移至真实解析用例。
> ⑥ 提交：`5073614a`（`feat(ts): add --target CLI option for TypeScript conversion`）；docs 注记另提交（`efc5b3d6`）。复审修复：`docs(ts): register Task 12 cast leak limitation and add parser test`（hash 见最终报告）。
>
> ⑦ 已知限制（复审阻塞项补登）：`-t typescript` 单文件转换下，`tests/SampleProject/Sample.cs` 的 const long 字段初始化器泄漏 Java 风味 `(long) (4294967295L)`（`uint.MaxValue` 经 Java 侧 `AdaptExpressionToTargetType`（`FieldTransformer.cs` 约 L301）渲染为 cast 前缀；`TsMemberWriter.NormalizeLiteral`（`TsMemberWriter.cs` L240）只归一化纯字面量形态，带 cast 前缀的字符串原样透传），产物非法 TS——tsc strict 实测两行均报 `TS1351: An identifier or keyword cannot immediately follow a numeric literal.`。归属：M2 Task 19（数值语义/窄化/bigint 混算）承接，`Sample.cs` 用例纳入 M2 验收。

---

## Task 13: M1 控制流与字面量测试补全

**Files:**
- Create: `tests/CSharpToTypeScript.Tests/TsControlFlowEmissionTests.cs`
- Create: `tests/CSharpToTypeScript.Tests/TsLiteralEmissionTests.cs`
- Create: `tests/CSharpToTypeScript.Tests/TsPrimitiveTypeMappingTests.cs`

- [x] **Step 1: 写 TsControlFlowEmissionTests**

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

- [x] **Step 2: 写 TsLiteralEmissionTests**

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

- [x] **Step 3: 写 TsPrimitiveTypeMappingTests**

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
        // IR 方法名为 Java camelCase（doNothing），PascalCase 还原属命名管线后续任务
        //（Task 10/11 既有约定：断言按 IR 实证修订）
        Assert.Contains("doNothing(): void", result.GeneratedCode);
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

- [x] **Step 4: 运行所有 M1 测试**

Run: `dotnet test tests/CSharpToTypeScript.Tests/CSharpToTypeScript.Tests.csproj`
Expected: 全部 PASS。
实测：TS 项目 176 通过 / 0 失败（142 存量 + 新增 34：计划 19 + normalizer 组件 11 + 表达式层 3 + L3 汇总 1）。

- [x] **Step 5: 运行 Java 零回归**

Run: `dotnet test tests/CSharpToJava.Tests/CSharpToJava.Tests.csproj`
Expected: 全部 PASS。
实测：2423/2423 零回归（本任务动了 raw normalizer/表达式 writer/字段发射器，全量复跑）。

- [x] **Step 6: 提交**

```bash
git add tests/CSharpToTypeScript.Tests/TsControlFlowEmissionTests.cs tests/CSharpToTypeScript.Tests/TsLiteralEmissionTests.cs tests/CSharpToTypeScript.Tests/TsPrimitiveTypeMappingTests.cs
git commit -m "test(ts): add M1 control flow, literal, and primitive type mapping tests"
```
实测：实际提交（含发射器修复与断言修订）见注记⑥。

> **Task 13 实现注记（2026-08-07，以实测为准）**
>
> ① 红→绿清单（计划 19 个测试，初跑 4 红 15 绿）：
>    - `For_Loop_Emits_For`（红 → 绿，发射器修复）：raw 经典 for 头保持 Java 形态
>      `for (int i = 0; i < n; i = i + 1)`（IR 实证，整段 JavaRawStatement 直通），含非法 TS
>      声明段 `int i = 0`。Task 11 注记⑥ 登记项，本任务在 `TsRawStatementNormalizer` 增加
>      经典 for 头声明段 let 化：`for (TYPE var = init; ...)` → `for (let var: MappedType = init; ...)`，
>      MappedType 经 `TsMemberMappingTable`（`TsStatementWriter` 传入 `_exprWriter.MapType` 回调，
>      `Normalize` 2 参重载）。仅当声明段匹配简单 `TYPE identifier = expr` 形态且类型映射命中时
>      改写；多声明（init 顶层逗号）、无初始化、泛型/数组类型、映射未命中、`for (;;)` 一律直通。
>      与既有增强 for 规则按头内是否含顶层 ";" 区分（经典 for 含 ";"、增强 for 不含）。
>      实现要点：`FindTopLevelSemicolons` 只按 `()`/`[]`/`{}` 计深度，尖括号不计——条件里的
>      比较符 `i < n` 若按泛型计深度会误吞第二个 ";" 导致不改写（首版实测 bug，已修并全量复跑）。
>      补组件测试 6 个（int/String 声明、多声明直通、无初始化直通、`for (;;)` 直通、三元共存）
>      + 既有 e2e 三元回归断言修订（见 ③）。L3 tsc 验证通过。
>    - `Char_Literal_Emits_As_String`（红 → 绿，发射器修复）：IR 实证 `return 'A';` 为
>      `JavaRawStatement` 整段直通（前端 return 表达式统一走 raw 路径，不经表达式发射层），
>      计划断言 `return "A";` 不满足。在 `TsRawStatementNormalizer` 增加 char 字面量归一化：
>      单引号字面量 → 双引号字符串（跳过双引号字符串区域；仅单字符/转义序列形态改写，
>      `'\n'`→`"\n"`、`'\''`→`"'"`；内含双引号的 `'\"'` 保持单引号直通，M2 字符任务细化）。
>      表达式层（`TsExpressionWriter.EmitLiteral`）同步补 `\'` → `'` 去转义（IR `'\''` 输出 `"'"`），
>      与 raw 层一致。补 raw 层组件测试 5 个 + 表达式层单测 3 个。
>    - `Nullable_Int_Maps_To_Number_Or_Null`（红 → 绿，发射器修复）：IR 实证 `int? x = null`
>      → 字段 `Type="Integer"`、`Initializer="null"`（`?` 后缀已在前端擦除，TS 层无法区分
>      `Integer` 来源）。既有 `EmitField` 只在"无初始化器且默认零值为 null"时放宽 `T | null`，
>      `= null` 初始化器形态未覆盖。一行级真实修复：实际发射 `= null` 时（初始化器为 null
>      字面量，或无初始化器且默认零值为 null）统一放宽 `T | null`，与既有约定一致；
>      `object x = null` → `any` 不受影响（any 豁免）。可空体系深度语义（局部变量/参数/
>      `Nullable<T>`/`?` 擦除）仍属 M2 Task 20。
>    - `Void_Maps_To_Void`（红 → 绿，断言修订）：IR 方法名为 Java camelCase（`doNothing`），
>      计划断言 `DoNothing(): void` 按 Task 10/11 既有约定修订为 `doNothing(): void`
>      （PascalCase 还原属命名管线后续任务，非发射器缺陷）；计划 Step 3 代码块已同步。
>    - 其余 15 个（while/do-while/switch/bare return/null/bool/hex/string escapes/char 字段/
>      int/double/long/bool/string/object）首跑即绿，无需改动。
> ② 断言修订清单（既有测试，因 let 化行为变化同步）：
>    - e2e `Classic_For_With_Ternary_Is_Not_Rewritten` → 更名为
>      `Classic_For_With_Ternary_Is_Let_ified_Not_ForOf`：断言由 Java 形态
>      `for (int i = (flag != 0 ? a : b); ...)` 修订为 let 化形态
>      `for (let i: number = (flag != 0 ? a : b); ...)`（该测试注释明确登记 let 化属本任务），
>      并加 L3 `AssertCompiles`；`DoesNotContain("for (const a of b)")` 回归守卫保留。
>    - normalizer 组件测试 3 个（`Classic_For_Without_Colon_Passes_Through`、
>      `Classic_For_With_Ternary_In_Header_Passes_Through`、
>      `Classic_For_With_Ternary_Identifier_Operands_Passes_Through`）：1 参重载（无映射表）
>      行为不变、断言不动；注释更新说明 let 化经 2 参重载覆盖（避免误导）。
> ③ 测试统计：TS 项目 142 → 176 通过 / 0 失败（新增 34 = 计划 19 + normalizer 组件 11
>    [Task 13 新增 6+5] + 表达式层 3 + L3 汇总 1；另有 e2e 三元测试更名 1 个计入存量）。
>    Java `CSharpToJava.Tests` 2423/2423 零回归（修复后全量复跑）。
> ④ L3 tsc 证据：`Task13_Key_Scenarios_Compile_With_Tsc`（经典 for let 化 + char 字段 +
>    `int? = null` + `object = null` 汇总）与 e2e 三元 let 化用例均过真实 tsc strict/ES2022；
>    手工复现 CLI 产物（`for (let i: number = 0; ...)`、`Flag: string = "A"`、
>    `Count: number | null = null`、`Tag: any = null`）tsc --noEmit exit 0。
> ⑤ 偏离/选择记录：
>    - `Normalize` 增加 2 参重载（`Func<string, string>? mapType`）而非改 1 参签名：
>      既有 14 个组件测试语义不变（无映射表时经典 for 直通），生产唯一调用点
>      `TsStatementWriter` 传 `_exprWriter.MapType`。
>    - 经典 for 类型映射未命中（IR 类型恒为 Java 风味，命中必改变文本，据此判定）→ 直通，
>      不输出诊断注释（与结构化 `ConvertForInitializer` 的注释策略不同：raw 层保持最小改写，
>      未覆盖形态维持 Task 11 现状直通）。
>    - char 字面量归一化放在 raw 归一化流水最后（println → for 头 → char）：for 头 paren
>      匹配与 println 实参跳过逻辑先按单引号区域处理，char 转换不与前两阶段相互干扰。
> ⑥ 提交：7e496da7（message 追加说明见注记 ①-⑤，含发射器修复与断言修订）。
> ⑦ 复审遗留登记（质量审查非阻塞项，归 M2 处理）：
>    - I-1：经典 for let 化未对 init 字面量做类型适配——`for (long i = 0L; ...)` 产 `let i: bigint = 0L`
>      （TS1351）、`for (float i = 1.5f; ...)` 产 `number = 1.5f`（TS1351）、`for (int? i = null; ...)`
>      产 `number = null`（TS2322）。建议随 Task 19（数值语义）或字面量归一化扩展处理（init 复用
>      NormalizeLiteral 类归一化）。
>    - I-2：`HasTopLevelComma` 不跳过字符串字面量——`for (String s = "a,b"; ...)` 被误判为多声明而直通
>      （保留 Java 风味，非法 TS；保守直通无破坏）。建议随 I-1 一并补齐引号跳过。
>    - M-2/M-3/M-4（Minor）：多声明直通头内 char 仍被改写（无新增破坏）；char 归一化会改写注释文本
>      （前端剥离注释，影响低）；字段初始化器 `'\''` 输出 `"\'"` 与 raw/表达式层 `"'"` 文本不一致
>      （同值合法）。
---

# 里程碑 M2：核心特性（泛型/接口/属性/枚举/异常/字符串插值/数值语义/可空）

**验收标准：** §5.3 核心特性组测试全绿 + L3；Java 全量测试零回归。

## Task 14: 泛型类型/方法/约束发射

**Files:**
- Modify: `src/CSharpToJava.Core/TypeScript/TsTypeWriter.cs`（泛型约束映射）
- Modify: `src/CSharpToJava.Core/TypeScript/TsMemberWriter.cs`（泛型方法）
- Create: `tests/CSharpToTypeScript.Tests/TsGenericEmissionTests.cs`
- Modify: `src/CSharpToJava.Core/TypeScript/TsMemberMappingTable.cs`（约束 bound 内置映射：Comparable/AutoCloseable → object）

- [x] **Step 1: 写失败测试**

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
        Assert.Contains("identity<T>(x: T): T", result.GeneratedCode);
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
        Assert.Contains("max<T extends ", result.GeneratedCode);
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

- [x] **Step 2: 运行测试验证失败**

Run: `dotnet test tests/CSharpToTypeScript.Tests/CSharpToTypeScript.Tests.csproj --filter "FullyQualifiedName~TsGenericEmissionTests"`
Actual: FAIL 2/5（方法泛型发射形态 + camelCase 断言差异，详见注记 ①）。

- [x] **Step 3: 验证 IR 是否携带类型参数并修正约束发射**

`JavaTypeDeclaration.TypeParameters` 已存在（`JavaTypeParameter` 定义在 `JavaTypeDeclaration.cs` 约第 351-368 行，已核对）：属性为 `Name` 与 `Bounds`（`List<string>`，**没有 `Constraint` 属性**）。`JavaTypeParameter.ToString()` 输出 Java 风味 `T extends A & B`，而 **TS 泛型约束只允许单一类型且不支持交叉类型**，因此不能直接用 `tp.ToString()`。

在 `TsTypeWriter` 中添加类型参数格式化辅助方法：

```csharp
public static string FormatTypeParameters(IReadOnlyList<JavaTypeParameter> typeParams, Func<string, string> mapBound)
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
        return $"{TsNaming.SafeIdentifier(tp.Name)} extends {mapBound(tp.Bounds[0])}{extra}";
    });
    return $"<{string.Join(", ", parts)}>";
}
```

将 `EmitClass`/`EmitInterface`/`TsMemberWriter.EmitMethod`/接口成员循环中的类型参数输出改为调用 `FormatTypeParameters(...)`（替换 Task 9 中的 `tp.ToString()` 拼接）。实现差异（见注记 ④）：静态化 + 注入 bound 映射函数；方法类型参数必须位于方法名**之后**（`name<T>(...)`，Task 9 原 `<T>name(...)` 为非法 TS）；首个 bound 经 Java→TS 类型表映射（如 Comparable → object）。

注：`new()` 约束在 IR 中无直接表示（Java 无对应），其降级（工厂参数）在 M3 TsGenericErasureRewriter 处理（spec §2.6.2）。

- [x] **Step 4: 运行测试验证通过**

Run: `dotnet test tests/CSharpToTypeScript.Tests/CSharpToTypeScript.Tests.csproj --filter "FullyQualifiedName~TsGenericEmissionTests"`
Actual: PASS（7 个测试：计划 5 + 组件补测 2，全部附 L3 tsc）。

- [x] **Step 5: 提交**

```bash
git add src/CSharpToJava.Core/TypeScript/TsTypeWriter.cs src/CSharpToJava.Core/TypeScript/TsMemberWriter.cs src/CSharpToJava.Core/TypeScript/TsMemberMappingTable.cs tests/CSharpToTypeScript.Tests/TsGenericEmissionTests.cs
git commit -m "feat(ts): emit generic type parameters with constraints"
```

**注记（2026-08-07 Task 14 实施，实测为准）：**
> ① 红→绿清单（证据）：
>    - 初跑红 2/5：`Generic_Method_Emits_Type_Parameter`、`Generic_Method_With_Constraint_Emits_Extends`
>      ——实际产物 `<T>identity(x: T): T` / `<T extends Comparable>max(a: T, b: T): T`：类型参数位于
>      方法名**之前**（非法 TS 语法）且方法名为 Java camelCase；
>    - 初跑绿 3/5：`Generic_Class_Emits_Type_Parameter`（`export class Container<T>`）、
>      `Generic_Class_With_Constraint_Emits_Extends`（`Container<T extends Comparable>`，单 bound 形态
>      Task 9 已能发射）、`Multiple_Type_Parameters_Emit_All`（`Pair<K, V>`）；
>    - 修复后 8/8 绿（含组件补测 2 + 接口泛型方法回归 1，见 ⑤/⑨）；全量 TS 184/184、Java 2423/2423 零回归。
> ② camelCase 断言修订（既有约定，Task 10/11/13 实证；计划原断言按大写方法名书写，与实测冲突
>    以实测为准并同步本计划）：
>    - `Identity<T>(x: T): T` → `identity<T>(x: T): T`；
>    - `Max<T extends ` → `max<T extends object>(a: T, b: T): T`；
>    - 约束断言从"只验证 extends 结构"增强为锁定映射后 bound（`Container<T extends object>`），
>      将 bound 映射决策固化为回归。
> ③ 约束 bound 映射决策（L3 tsc 实证：bound 未映射时 tsc 报 "Cannot find name 'Comparable'"；
>    spec §1.4.3 约束仅部分表达，以"产物合法 TS"为准则）：
>    - 首个 bound 经 Java→TS 类型表（MapType）映射后发射——与字段/参数/返回类型映射一致；
>    - 内置表新增 `Comparable → object`、`AutoCloseable → object`（C# IComparable/IDisposable 的
>      Java 风味名；TS 无对应内建接口，降级为最宽松对象类型）；
>    - 多 bound（Java "A & B" 交叉约束）TS 不支持：取首个，其余以
>      `/* cs2j: dropped bounds: ... */` 诊断注释丢弃
>      （`where T : IComparable, IDisposable` → `Multi<T extends object /* cs2j: dropped bounds: AutoCloseable */>`）。
> ④ 实现偏离（与 Step 3 草稿的差异）：
>    - `FormatTypeParameters` 为 `public static` 并注入 `Func<string,string> mapBound`（TsTypeWriter
>      依赖 TsMemberWriter，实例方法无法被 TsMemberWriter 复用；类/接口/方法/接口成员四处共用）；
>    - 方法类型参数位置修正（`<T>name(` → `name<T>(`）属 Task 14 范畴（方法发射的 `tp.ToString()`
>      拼接替换，产物须为合法 TS）；
>    - 接口成员泛型方法（EmitInterface 成员循环）原完全丢失类型参数，一并接入
>      （`interface IRepo<T> { T Find<T>(T id); }` → `find<T>(id: T): T;`）。
> ⑤ 补测（TsGenericEmissionTests 追加 3 个，与计划 5 个一并全绿）：
>    - `Multiple_Bounds_Drop_Extra_With_Diagnostic_Comment`：全管线多 bound 丢弃 + 注释 + L3 tsc；
>    - `Type_Parameter_Name_Is_Safe_Identifier`：IR 直构 `JavaTypeParameter("default")` →
>      `Foo<default$>` + L3 tsc；
>    - `Interface_Generic_Method_Emits_Type_Parameters`（双审补测，全管线）：`interface IRepo<T>` >      成员泛型方法 → `find<T>(id: T): T` / `put<U extends object>` + L3 tsc；
>    - 计划 5 个测试全部附加 `AssertCompiles`（L3：含约束/多类型参数产物经真实 tsc 编译，
>      strict:true 全过；另以 CLI 探针 Utils/Pair/Multi/IRepo 五类场景独立 tsc exit 0）。
> ⑥ 测试统计：TS 项目 184/184（176 + 新增 8）；Java 项目 2423/2423 零回归。
> ⑦ 复审遗留登记（非阻塞，归后续任务）：
>    - 泛型 bound 实例（`where T : IComparable<T>` → IR `Comparable<T>`）未命中类型表映射，
>      tsc 会报 "Cannot find name 'Comparable'"——随 M2/M3 泛型擦除/命名管线补映射；
>    - `where T : IEnumerable` → IR `CSharpGenericIterable<T>`（比 Comparable<T> 更常见）同样未命中
>    - 约束 bound 语义保真（`object` 排除 number/string/boolean 字面量约束场景）属 M3 泛型擦除范畴。
> ⑧ 提交：d88b5a8f（`feat(ts): emit generic type parameters with constraints`；4 文件：
>    TsTypeWriter.cs / TsMemberWriter.cs / TsMemberMappingTable.cs / TsGenericEmissionTests.cs）。
> ⑨ 双审修复（2026-08-07，Important + Minor）：
>    - **使用点位转义登记**：`FormatTypeParameters` 仅转义类型参数**声明位**；类型引用位
>      （字段/参数/返回/局部/cast 的类型名，如 `class Foo<@default> { @default value; }` →
>      `value: default`，tsc TS2552）未过 SafeIdentifier——本任务不修（涉及全类型发射路径的
>      命名管线），归后续命名管线任务；`Type_Parameter_Name_Is_Safe_Identifier` 保持声明层
>      组件断言并加注释说明（不改全管线断言，避免假红）；
>    - 接口泛型方法全管线回归补测（见 ⑤）：`interface IRepo<T>` 成员泛型方法；
>    - docs 同步：Step 1 代码块断言 camelCase 化（identity/max）、Step 5 `git add` 更新为
>      实际 4 文件、`TsMemberWriter` 双空行清理、测试文件行尾归一（LF）；
>    - 统计：TS 184/184、Java 2423/2423（Core 仅空行清理，零回归）；
>    - 提交：本修复提交（`fix(ts): sync Task 14 docs and add interface generic method test`；hash 见 git log）。
---

## Task 15: 接口与 implements 发射

**Files:**
- Modify: `src/CSharpToJava.Core/TypeScript/TsTypeWriter.cs`
- Create: `tests/CSharpToTypeScript.Tests/TsInterfaceEmissionTests.cs`

- [x] **Step 1: 写失败测试**

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
        // 方法名 Java camelCase（既有约定，Task 10/11/13/14 实证）：bar，非 Bar
        Assert.Contains("bar(): number;", result.GeneratedCode);
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
        // 接口属性发射为 readonly X: number;（仅 getter → 只读属性；Y 独立断言避免子串冗余）
        Assert.Contains("readonly X: number;", result.GeneratedCode);
        Assert.Contains("readonly Y: number;", result.GeneratedCode);
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

- [x] **Step 2: 运行测试验证失败/通过**

Run: `dotnet test tests/CSharpToTypeScript.Tests/CSharpToTypeScript.Tests.csproj --filter "FullyQualifiedName~TsInterfaceEmissionTests"`

部分测试可能已通过（Task 9 已实现基础接口发射）。失败项主要是接口属性发射格式。

Actual: 初跑红 2/4、绿 2/4。红：`Interface_Declaration_Emits_Export_Interface`（实际 `bar(): number;`，
计划断言 `Bar()` 未 camelCase）、`Interface_With_Properties_Emits_Properties`（实际 `getX(): number;`，
访问器未合并为属性）；绿：`Class_Implements_Interface_Emits_Implements`、`Interface_Extends_Interface_Emits_Extends`
（Task 9 基础发射已具备）。

- [x] **Step 3: 修复接口属性发射**

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
          .AppendLine($"{(readOnly ? "readonly " : "")}{TsNaming.SafeIdentifier(grp.Key)}: {_memberWriter.MapType(getter.ReturnType)};");
    }
}

foreach (var method in pureMethods)
{
    // 原有方法发射（参数/返回值类型同样经 MapType）
}
```

注：`TsTypeWriter` 访问 `MapType` 无需改构造器——Task 9 已通过 `TsMemberWriter.MapType`（public 实例转发）
公开，`EmitType` 组装代码不变。只写属性（仅 setter）无 TS 对应形态 → 保留 setter 方法发射 + 诊断注释；
接口默认实现方法（`JavaModifiers.Default`）的降级按 spec §2.5（拷贝到实现类/伴随辅助/诊断）在 M3 处理。

**implements/extends 类型映射（本任务范畴，Task 14 注记 ⑦ 登记项）：**
- heritage 类型（`EmitClass` implements / `EmitInterface` extends）经 `MapHeritageType` 做 Java→TS 映射；
- 框架接口（`Comparable`/`AutoCloseable`，C# `IComparable`/`IDisposable`）映射结果为 `object`，但
  implements/extends 位置是表达式/类型引用位置，`object` 关键字非法（tsc TS2552，TS 5.9.3 与 7.0.2
  实证一致）→ 降级为全局类型 `Object`（与 Task 14 注记 ③"最宽松对象类型"同一意图的合法形态）；
- 泛型形态分解：`Comparable&lt;Point&gt;` → 裸名映射为 `object` → 丢弃实参 → `implements Object`；
  用户接口未命中类型表（如 `IRepo&lt;Integer&gt;`）→ 保留原名，泛型实参逐段映射 →
  `implements IRepo&lt;number&gt;`（同文件接口，tsc 通过）；
- 嵌套泛型实参（`IBase&lt;Pair&lt;int, int&gt;&gt;`）逐段映射不递归、按逗号切分可能不完整，
  维持原样，登记后续命名管线任务（Task 14 注记 ⑨ 同源）。

- [x] **Step 4: 运行测试验证通过**

Run: `dotnet test tests/CSharpToTypeScript.Tests/CSharpToTypeScript.Tests.csproj --filter "FullyQualifiedName~TsInterfaceEmissionTests"`
Actual: PASS（9 个测试：计划 4 + 补测 5，全部附 L3 tsc；见注记 ⑤）。

- [x] **Step 5: 提交**

```bash
git add src/CSharpToJava.Core/TypeScript/TsTypeWriter.cs tests/CSharpToTypeScript.Tests/TsInterfaceEmissionTests.cs
git commit -m "feat(ts): emit interfaces with properties and extends"
```

**注记（2026-08-07 Task 15 实施，实测为准）：**
> ① 红→绿清单（证据）：
>    - 初跑红 2/4：`Interface_Declaration_Emits_Export_Interface`（实际 `bar(): number;`，
>      camelCase 与计划断言不符）、`Interface_With_Properties_Emits_Properties`
>      （实际 `getX(): number;` 访问器未合并）；
>    - 初跑绿 2/4：`Class_Implements_Interface_Emits_Implements`、`Interface_Extends_Interface_Emits_Extends`
>      （Task 9 基础接口发射已具备）；
>    - 修复后 9/9 绿（计划 4 + 补测 5，全部附 L3 tsc）；全量 TS 193/193（184 + 新增 9）、
>      Java 2423/2423 零回归。
> ② camelCase 断言修订（既有约定，Task 10/11/13/14 实证；计划原断言 `Bar(): number;` 与实测
>    不符，以实测为准并同步本计划）：`Bar(): number;` → `bar(): number;`（Step 1 代码块已改）。
> ③ 属性合并细节（实测 IR 形态与计划草稿一致）：
>    - 接口属性由 `PropertyTransformer` 产出 `getX()`/`setX(value)` 自动生成方法
>      （`"get"/"set" + PascalCase`，`IsAutoGenerated = true`，接口上下文中无后备字段）；
>    - `IsAccessor` 检测：`IsAutoGenerated && 名称以 get/set 开头 && 去前缀后首字符大写`；
>      索引器访问器名为裸 `get`/`set`/`put`（长度 ≤ 3）天然排除，不会误合并；
>    - 合并发射：仅 getter → `readonly X: number;`；get+set → `X: number;`（类型经 MapType）；
>      只写属性（仅 setter）→ 保留 `setZ(value: number): void;` 方法 + 诊断注释
>      `/* cs2j: write-only property Z: no TS counterpart; kept as setter method */`；
>      纯方法照常发射（顺序：属性组在前、纯方法在后）；
>    - `TsTypeWriter` 构造器**未变更**：Task 9 已公开 `TsMemberWriter.MapType`（public 实例转发），
>      `TypeScriptEmitter.EmitType` 组装代码无改动。
> ④ implements/extends 类型映射决策（Task 14 注记 ⑦ 登记项修复；L3 tsc 实证）：
>    - 新增 `TsTypeWriter.MapHeritageType`：整串精确匹配 → 泛型形态分解 → 非泛型原样；
>    - 框架接口 `Comparable`/`AutoCloseable` → 映射 `object` → heritage 位置 `object` 关键字
>      非法（tsc TS2552，TS 5.9.3 与 7.0.2 独立实证一致）→ 降级为全局类型 `Object`
>      （`class Point implements Object` / `interface IFoo extends Object` 均通过 tsc strict）；
>    - 泛型 implements：`IComparable&lt;Point&gt;` → IR `Comparable&lt;Point&gt;` → 裸名映射
>      `object` → 丢弃实参 → `implements Object`；用户接口 `IRepo&lt;Integer&gt;` → 实参逐段
>      映射 → `implements IRepo&lt;number&gt;`（同文件接口，tsc strict 通过）；
>    - `implements Object`/`extends Object` 对类/接口成员无额外约束（Object 全局接口成员均
>      由原型链满足），产物合法；嵌套泛型实参（`IBase&lt;Pair&lt;Integer, Integer&gt;&gt;`）
>      逐段映射不递归，产物**非法 TS（tsc TS2304）**——回拼后追加
>      `/* cs2j: unmapped nested generic args */` 诊断注释（双审 Important ① 最小修复），
>      完整修复（深度递归映射 + import 收集）随命名管线任务登记（Task 14 注记 ⑨ 同源）。
> ⑤ 补测（TsInterfaceEmissionTests 追加 5 个，与计划 4 个一并全绿，全部附 L3 tsc）：
>    - `Interface_Property_With_Getter_And_Setter_Emits_Non_Readonly`：get+set → `X: number;`
>      （无 readonly 前缀）；
>    - `Interface_Write_Only_Property_Keeps_Setter_With_Diagnostic`：只写属性 → setter 方法 +
>      诊断注释；
>    - `Class_Implements_Mapped_Framework_Interface_Emits_Object`：`IComparable&lt;Point&gt;` 与
>      `System.IDisposable` → `implements Object`（Task 14 审查登记项回归）；
>    - `Class_Implements_Generic_Interface_Emits_Mapped_Args`：`IRepo&lt;int&gt;` →
>      `implements IRepo&lt;number&gt;`；
>    - `Interface_Extends_Generic_Interface_Emits_Mapped_Args`：`IBase&lt;int&gt;` →
>      `extends IBase&lt;number&gt;`。
> ⑥ 测试统计：TS 项目 193/193（184 + 新增 9，L3 tsc 全过）；Java 项目 2423/2423 零回归
>    （Core 仅 TsTypeWriter.cs 改动）。
> ⑦ 偏离清单：
>    - 计划 Step 3 伪代码 `_exprWriter.MapType` → 实际 `_memberWriter.MapType`（构造器无需变更）；
>    - 计划 Step 1 `Bar()` → 实际 `bar()`（camelCase 修订，见 ②）；
>    - Step 4 预期 4 个测试 → 实际 9 个（补测 5 个，见 ⑤）；
>    - 其余按计划实施；类 `extends`（`ExtendedType`）仍原样发射（Task 9 现状，非本任务范畴）。
> ⑧ 提交：hash 见 git log（`feat(ts): emit interfaces with properties and extends`；3 文件：
>    TsTypeWriter.cs / TsInterfaceEmissionTests.cs / 本计划文档）。
> ⑨ 双审修复（2026-08-07，Important ① + Minor ③④⑤）：
>    - **嵌套泛型 heritage 诊断注释（Important）**：`IBase<Pair<Integer, Integer>>` 按逗号切分
>      无法完整映射，产物非法 TS（tsc TS2304）且原先无任何标记——`MapHeritageType` 回拼后
>      追加 ` /* cs2j: unmapped nested generic args */` 诊断注释（与 dropped bounds/ctor 重载
>      惯例一致）；单层实参全部命中（`IBase<string>`/`IRepo<int>`）不加注释；完整修复
>      （深度递归映射 + import 收集）随命名管线任务登记；
>    - **弱断言修复（Minor）**：`Interface_With_Properties_Emits_Properties` 的 `X: number;`
>      子串断言与 `readonly X: number;` 冗余 → 改为独立断言 `readonly Y: number;`；
>    - **边界回归补测（Minor，3 个）**：`Accessor_Like_User_Methods_Are_Not_Merged`
>      （`GetURL()`/`get()`→`get$()`（get 为 TS 保留字转义）/`GetItem(int)` 全保持方法形态 +
>      L3 tsc）、`Parameterized_Auto_Generated_Accessor_Is_Not_Merged`（组件级：带参自动生成
>      getter `getItem(index)` 不合并，IsAccessor 增 getter 参数守卫）、
>      `Interface_Extends_Nested_Generic_Args_Emits_Diagnostic_Comment`（组件级：注释追加 +
>      L3 探针（Integer 别名/Pair/IBase 声明后发射文本原样编译，注释形态为合法 TS））；
>    - 既有两个单层泛型测试补 `DoesNotContain("cs2j: unmapped nested generic args")`；
>    - 文档同步：Step 1 勾选、Step 1 代码块属性断言同步 `readonly Y: number;`、注记 ④ 措辞
>      修订（非法 TS TS2304）、注记 ⑧ 文件数 2→3（含计划文档）、测试文件末尾补换行；
>    - 统计：TS 196/196（193 + 新增 3，L3 tsc 全过）；Java 2423/2423 零回归；
>    - 提交：本修复提交（`fix(ts): add diagnostic for unmapped nested generic heritage args`；
>      hash 见 git log）。
---

## Task 16: 属性 get/set 发射

**Files:**
- Modify: `src/CSharpToJava.Core/TypeScript/TsMemberWriter.cs`
- Modify: `src/CSharpToJava.Core/TypeScript/TsTypeWriter.cs`
- Modify: `src/CSharpToJava.Core/TypeScript/TypeScriptEmitter.cs`
- Modify: `src/CSharpToJava.Core/Pipeline/Passes/SingleFilePasses.cs`
- Create: `src/CSharpToJava.Core/TypeScript/Rewriters/TsPropertyReconstructionRewriter.cs`
- Create: `tests/CSharpToTypeScript.Tests/TsPropertyEmissionTests.cs`
- Create: `tests/CSharpToTypeScript.Tests/TsPropertyReconstructionTests.cs`

- [x] **Step 1: 写失败测试**

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

- [x] **Step 2: 运行测试验证失败**

Run: `dotnet test tests/CSharpToTypeScript.Tests/CSharpToTypeScript.Tests.csproj --filter "FullyQualifiedName~TsPropertyEmissionTests"`

注（已核对 IR 实际形态）：C# 属性在 HIR 阶段已被 `PropertyTransformer`（`src/CSharpToJava.Core/Transformers/Member/PropertyTransformer.cs`）转为：私有后备字段（camelCase）+ `getX()`/`setX(value)` 方法（`"get" + PascalCase`，非 `get_X`），自动生成的访问器方法标记 `IsAutoGenerated = true`。表达式体属性（无后备字段）仅生成 getter。因此 TS 侧需“重组”而非直接发射。

- [x] **Step 3: 识别并发射属性访问器**

新增 `src/CSharpToJava.Core/TypeScript/Rewriters/TsPropertyReconstructionRewriter.cs`（在 `SingleFileTsEmitPass.RunTsRewriters` 中装配）：

1. **识别**：逐类扫描 `Methods` 中 `IsAutoGenerated == true` 且名称符合 `get{Name}`/`set{Name}` 模式的方法，按 `Name`（去前缀后的属性名）配对；同时定位对应的私有后备字段（camelCase 名）。
2. **标记**：在配对的方法上打标记（可新增 `CSharpToJava.Core.TypeScript` 内部的侧表 `Dictionary<JavaMethodDeclaration, TsAccessorRole>`，不改共享 IR 类），记录属性名/可见性/是否只读。
3. **调用点同步改写**：遍历全部 `JavaMethodCallExpression`：`target.getX()` → `JavaMemberAccessExpression(target, X)`；`target.setX(v)` → `JavaAssignmentExpression(Target=MemberAccess(target,X), Value=v)`（赋值表达式在 TS 中可内联）。dot-target 无类型信息或目标类型未注册时保守不改写（C1 双审修复：全局方法名集合匹配会误改跨类同名用户方法，宁可不改不可误改；仅 this/super/裸调用按当前类型 + 基类链解析，或目标类型可解析且该类型上下文注册过才改写，见注记⑩）。
4. **发射**：`TsTypeWriter.EmitClass` 对打了标记的方法发射为 TS 访问器：

```typescript
get X(): number { return this.x; }
private set X(value: number) { this.x = value; }
```

   getter/setter 方法体仍走 Task 9 的 StructuredBody/Body 发射路径；访问器形态下后备字段保留为私有字段（字段简化形态下后备字段提升为 public 属性字段，见注记②）。表达式体属性的 getter 体即原表达式，经字段限定补 this. 后为 `return this.count * 2;`（tsc TS2663 实证）。
5. **不重组的场景**（非自动生成、显式接口实现、无法配对）→ 维持方法发射；抽象访问器 → `abstract get X(): number;`/`abstract set X(value: number);`（I4 双审修复，与派生 override 访问器形态匹配，见注记⑩）；仅只写属性附加诊断注释（其余不加注释避免噪音）。

在 `tests/CSharpToTypeScript.Tests/TsPropertyReconstructionTests.cs` 中为 rewriter 补充单测（get/set 配对、调用点改写、只读属性不生成 setter）。

- [x] **Step 4: 运行测试验证通过**

Run: `dotnet test tests/CSharpToTypeScript.Tests/CSharpToTypeScript.Tests.csproj --filter "FullyQualifiedName~TsPropertyEmissionTests"`
Actual: PASS（11/11：5 个 emission 测试（附 L3 tsc）+ 6 个重建单测；见注记①⑦）。

- [x] **Step 5: 提交**

```bash
git add src/CSharpToJava.Core/TypeScript/Rewriters/TsPropertyReconstructionRewriter.cs src/CSharpToJava.Core/TypeScript/TsTypeWriter.cs src/CSharpToJava.Core/TypeScript/TsMemberWriter.cs src/CSharpToJava.Core/TypeScript/TypeScriptEmitter.cs src/CSharpToJava.Core/Pipeline/Passes/SingleFilePasses.cs tests/CSharpToTypeScript.Tests/TsPropertyEmissionTests.cs tests/CSharpToTypeScript.Tests/TsPropertyReconstructionTests.cs docs/superpowers/plans/2026-08-06-csharp-to-typescript.md
git commit -m "feat(ts): reconstruct C# properties as TS accessors"
```

**注记（2026-08-07 Task 16 实施，实测为准）：**
> ① 红→绿清单（证据）：
>    - 初跑 7 红 2 绿：5 个 emission 测试全红（类属性以 getX/setX 方法发射，与 Step 2
>      预期一致）+ 2 个重建单测红（`Auto_Getter_Setter_Pair_Is_Simplified_To_Public_Field`、
>      `Getter_Setter_Call_Sites_Are_Rewritten_To_Member_Access_And_Assignment`）；
>      `User_Method_With_Accessor_Like_Name_Is_Not_Reconstructed`、
>      `Explicit_Interface_Implementation_Accessor_Is_Not_Reconstructed` 天然绿（守卫语义）；
>    - 实现后 10/10 绿；补第 11 个跨类同名回归测试
>      （`Write_Only_Setter_Call_Is_Not_Rewritten_When_Another_Class_Has_Same_Property`）红——
>      类名字典互串（两个同名 Point 的 setX 被全局集合误改）→ 类上下文改按节点键控
>      （`Dictionary<JavaClassDeclaration, ClassContext>` + 基类名索引）→ 11/11 绿；
>    - 全量 TS 207/207（196 + 新增 11，L3 tsc 全过）；Java 2423/2423 零回归。
> ② 字段简化 vs 访问器决策（与 Step 3"全部发射为访问器"的张力，以 IR 实测为准）：
>    - 自动 get;set; 双访问器（非表达式体、非 struct、有 camelCase 后备字段、setter 非
>      受限可见性）→ **字段简化**：后备字段提升为属性名（PascalCase）、可见性取 getter、
>      getX/setX 方法从 IR 移除 → `X: number` 断言成立（与 Step 1 意图一致，C# 公开自动
>      属性与公开字段语义等价）；
>    - 只读 `{ get; }`/表达式体 → getter 访问器；getter + private/protected setter →
>      getter + setter 访问器对；struct（`IsConvertedFromStruct`）跳过简化保持访问器对；
>      只写 `{ set; }` → 维持 setter 方法 + 诊断注释；
>    - Step 3 第 4/5 点表述已同步修订（见上文勾选段）。
> ③ 侧表方案（侵入最小者，未改共享 IR 类）：
>    - `TsPropertyReconstructionRewriter.Roles`（`Dictionary<JavaMethodDeclaration,
>      TsAccessorRole>`，`TsAccessorRole`：Field/Getter/GetterSetter/WriteOnly）；
>    - 经 `TypeScriptEmitter` 可选构造参数（默认 null）注入 `TsTypeWriter`，Java 路径与
>      其他调用方零影响；`TsTypeWriter.EmitClass` 按角色分发：Field 跳过（方法已移除）、
>      Getter/GetterSetter → `TsMemberWriter.EmitPropertyAccessorGetter/Setter`、WriteOnly →
>      诊断注释 + 方法发射。
> ④ 调用点改写范围（结构化 + raw 双路径）：
>    - 结构化：`VisitExpression` 拦截 `JavaMethodCallExpression` → getter → 成员访问、
>      setter → 赋值表达式；裸/this/super 调用按当前类 + 基类链解析（静态补类名.，
>      实例补 this.）；
>    - raw 文本：`JavaRawStatement.Code`、方法/构造器 `Body` 字符串、`JavaRawExpression.Code`、
>      字段初始化器、静态初始化块（`JavaSyntaxRewriter` 不遍历，`VisitClassDeclaration`
>      补改）；跳过字符串/字符/注释区域；setter 仅接受单个顶层实参（可递归改写嵌套调用）；
>    - 解析边界：裸/this/super 调用按当前类 + 基类链（节点键上下文，杜绝同名单类互串）；
>      其他 dot-target 仅当目标类型可解析（参数/局部变量/当前类字段/类型名 + 解析辅助）且该类型上下文注册该访问器名才改写；无类型信息/未注册 → 保守保持方法调用（C1 修复，取代原全局方法名集合匹配 + L3 兜底表述，见注记⑩）；只写属性不注册
>      方法名 → setter 调用保持方法形态；抽象/显式接口实现访问器不重组。
> ⑤ 断言修订（以 IR/tsc 实测为准）：
>    - 表达式体 getter：IR 体为裸 `count * 2`（无 this.），TS 类体内裸字段非法（tsc
>      TS2663）→ 访问器体字段限定补 `this.`/`类名.` → 断言 `return this.count * 2;`；
>    - ctor 赋值调用点：`X = x` → `this.X = x;`；外部读取：`p.getX()` → `return p.X + p.Y;`；
>    - TS setter 不允许返回类型注解（tsc TS1095 实证）→ `private set X(value: number)`
>      （无 `: void`）；
>    - 只读属性 ctor `X = x` 直写后备字段 `this.x = x;` 保留（只读 getter 不生成 setter）。
> ⑥ 边界与已知缺口：
>    - 继承：基类在本单元内 → 裸调用可解析到基类属性；基类不在本单元内 → 不改写
>      （多文件/项目模式 M5，L3 兜底）；派生类访问器体中的裸基类字段不在本类字段集内
>      不限定（tsc TS2663 缺口保留，非本任务范畴）；
>    - 静态属性：裸调用补 `类名.` 前缀（`Stat.X`），静态字段限定同理；
>    - 既有缺口（L3 探针）：`CSharpList`/`Integer` 未映射（TS2304）等沿用 Task 9 注记，
>      与本任务产物无关。
>    - `{ get; init; }`（C# 9 init 访问器）：HIR 中与普通 setter 无异（仅 LeadingComment 标注
>      `@implNote Set during construction only`）→ 落入字段简化，放宽为 public 可写字段
>      （决策登记：TS 无 init-only 语义，接受语义放宽，双审 Minor 登记）；
>    - 嵌套类裸调用外部类属性不改写（既有缺口登记）：`_classStack` 按嵌套类型独立压栈，
>      裸调用仅解析当前类型 + 基类链，外层类属性需显式 `this.X`/`Outer.X` 形态才改写。
> ⑦ 测试统计：TS 项目 207/207（196 + 新增 11：5 emission 测试附 L3 tsc + 6 重建单测（IR 直构、发射文本断言，组件级不跑 tsc））；
>    Java 项目 2423/2423 零回归（rewriter 仅装配于 SingleFileTsEmitPass，Java 路径不进入）。
> ⑧ 偏离清单：
>    - 计划 Step 3 第 4 点"后备字段保留为私有字段"：仅访问器形态成立；字段简化形态为
>      后备字段提升为 public 属性字段（见 ②）；
>    - 计划 Step 3 第 5 点"不重组 → 维持方法发射 + 诊断注释"：实测仅只写属性加诊断注释；
>      显式接口实现/非自动生成维持方法发射（不加注释避免噪音）；抽象访问器改由 I4 重组（见⑩）；
>    - Step 4 预期 4 个测试 → 实际 11 个（5 emission + 6 重建，见 ⑦）；计划 Files 列表
>      已同步实际改动文件（补 TsTypeWriter/TypeScriptEmitter/SingleFilePasses/
>      TsPropertyReconstructionTests）。
> ⑨ 提交：`feat(ts): reconstruct C# properties as TS accessors`，hash 见 git log（8 文件：
>    TsMemberWriter.cs / TsTypeWriter.cs / TypeScriptEmitter.cs / SingleFilePasses.cs /
>    TsPropertyReconstructionRewriter.cs / TsPropertyEmissionTests.cs /
>    TsPropertyReconstructionTests.cs / 本计划文档）。双审修复提交
>    `fix(ts): harden property reconstruction call-site rewriting` 见注记⑩。
> ⑩ 双审修复（2026-08-07，P1/C1/I2–I5 + Minor，红→绿）：
>    - 红→绿清单（证据）：新增 `TsPropertyReconstructionReviewTests.cs` 13 个回归测试，
>      初跑 12 红 1 绿（唯一绿 `Dot_Target_With_Resolvable_Type_Is_Rewritten`——旧实现
>      按全局方法名集合匹配恰好命中）；修复后 13/13 绿；全量 TS 220/220（207 + 新增 13，
>      测试内嵌 L3 tsc），Java 2423/2423 零回归。
>    - P1（静默语义损坏）：setter 参数 `value` 与字段同名被字段限定误补 `this.value` →
>      限定集合排除方法参数与结构化局部变量（`MethodContext.NonFieldNames`）；
>      `int value` 字段 + `int X { get; private set; }` → setter 体 `this.x = value;`
>      （回归测试 + L3 tsc 过）。
>    - C1（Critical 误改写）：删除全局 `_accessorsByMethodName` 集合；dot-target 仅当
>      目标类型可解析（参数/局部变量/当前类字段/类型名 + `ResolveExpressionType`/
>      `ResolveIdentifierType`）且该类型上下文注册该访问器名才改写；无类型信息/未注册
>      → 保守保持方法调用。`Config.Value` + `Service.GetValue()/SetValue()` 同单元：
>      `s.getValue()`/`s.setValue(3)` 保持方法形态（tsc 过）；用户 `getX()` 方法、静态
>      用户方法、只写 `w.setX(3)` 均不误改。
>    - I2（接口访问器调用改写）：`AnalyzeInterface` 注册接口访问器名（只读仅 getter；
>      get+set 注册两者；只写不注册），不产生 roles；`iface.getX()` → `iface.X`、
>      `iface.setY(v)` → `iface.Y = v`，与 Task 15 readonly 属性合并一致（接口有实现类
>      同单元亦可解析）。
>    - I3（super 目标）：`base.X`/`base.X = v` → `this.X`/`this.X = v`（结构化 + raw 两路；
>      raw 路回滚已累积 `"super."`/`"this."` 文本）。TS2855 仅限基类字段；基类成员为重建访问器时保留 `super.Y`（见注记⑪ R2）。
>    - I4（抽象属性 + 派生覆盖）：抽象访问器 → `abstract get X(): number;`/
>      `abstract set X(value: number);`（`TsMemberWriter` 两处访问器发射补 `abstract ` 前缀），
>      派生 override 自动对禁字段简化保持访问器对；abstract get-only + override get-only、
>      abstract get/set + override get/set 均 tsc 过。
>    - I5（结构化体字段限定）：字段限定扩展至结构化语句（`VisitRawStatement` 与
>      `VisitExpression` 中 `JavaIdentifierExpression` → 成员访问），排除局部变量/参数
>      （与 P1 同源）；`int _count; int Count { get { return _count; } set { _count = value; } }`
>      → `this._count` 限定、`value` 不限定，tsc 过。
>    - Minor：`Roles` 注释与实现同步（Field 角色方法已移除但键保留供诊断/测试）；注记⑥
>      补登 `{ get; init; }` 字段简化放宽与嵌套类裸调用缺口（见⑥）；C1 保守策略替换原
>      "误配风险 L3 兜底"表述（Step 3 第 3/5 点 + 注记④）。
>    - L3 探针（4 场景独立 tsc --noEmit --strict 全过，exit=0）：p1（value 字段 + 私有
>      setter）、i4（抽象 + override + base.X）、i2c1（接口读写 + dot 保守）、i5（结构化
>      显式体访问器）。
>    - 提交：`fix(ts): harden property reconstruction call-site rewriting`，hash 见 git log
>      （4 文件：TsPropertyReconstructionRewriter.cs / TsMemberWriter.cs /
> ⑪ 二轮复审修复（2026-08-07，C2/R1+R6/R2/I7/I8 + Minor，红→绿）：
>    - 红→绿清单（证据）：新增 `TsPropertyReconstructionReview2Tests.cs` 15 个回归测试，
>      初跑 12 红 3 绿（旧实现绿：`Chained_Dot_Target_Structured_Is_Rewritten`、
>      `Deep_Chain_With_Unregistered_Leaf_Is_Not_Rewritten`、
>      `Chained_Dot_Target_Two_Level_Is_Rewritten`——单段/结构化链旧代码已覆盖）；
>      修复后 15/15 绿；全量 TS 235/235（220 + 新增 15，完整转换用例内嵌 L3 tsc），
>      Java 2423/2423 零回归（rewriter 仅装配于 TS 分支，Java 路径不进入）。
>    - C2（Critical 链式 dot-target 漏改）：raw 路径 `TryResolveRawDotTarget` 逐段解析
>      （`ExtractRawChainSegments`）：`b.a.getX()` 按段 b→B、a→B.a 字段(A)、getX→A 已注册
>      访问器才改写；`arr[0].getX()` 支持数组元素类型；`o.getA().getX()` 支持方法返回类型
>      链；任何一段不可解析 → 保守保留方法调用。结构化路径 `ResolveMemberTypeInContext`
>      查字段 → AccessorMap → 基类链回退；字段简化改名后同步刷新上下文表
>      （CreateClassContext 在 AnalyzeClass 字段改名之前构建 → 改名时更新表）。
>      回归测试 5 个（TwoLevel/Deep/ArrayIR/Structured + 未注册末端保守），另
>      `Inherited_Field_Target_Is_Rewritten`（继承字段目标，Minor 顺手修）全过。
>    - R1 + I6（virtual/override 形态冲突，同源合并修）：继承参与的属性统一禁用字段简化
>      ——`isTrivialAutoPair` 除排除 Override 外，经 `CollectInheritedPropertyNames`
>      （override 属性名集合）排除 virtual 基类属性；基类 virtual + 派生 override 均发射
>      访问器对（TS2611 消除）；抽象访问器维持 `abstract get/set`（I4 用例保持绿）。
>    - R2（super 按基类成员形态改写）：上一轮"super 一律改 this."导致基类访问器场景
>      `override int Y { get { return base.Y + _z; } }` 变成 `this.Y` 无限递归（tsc 静默
>      过）。实测 TS 中 `super.Y` 对基类访问器合法且语义正确（TS2855 仅限基类字段）。
>      修复：`TryResolveBaseAccessor`（基类链递归，不含当前类）判定基类成员形态——
>      重建后的访问器 → 保留 `super.Y`（raw 路 `keepSuper` 不截断已累积 "super." 文本；
>      结构化路保留 `IsSuper=true`）；基类简化字段 → `this.Y`。配合 R1（继承属性全是
>      访问器），`base.Y` 场景自然保留 `super.Y`。回归测试 2 个（raw/结构化）tsc 过。
>    - I7（for 循环 init 局部变量误限定）：`CollectForInitDeclarations` 解析 for-init
>      声明（含多声明 `int i = 0, j = 1`）并入排除集；raw 文本 `for(` 头经
>      `IndexOfTopLevelSemicolon` 定位 init 段收集变量。回归测试 2 个（结构化 Initializer
>      + raw for 头 + 多声明），`for (let i: number = 0; ...)` 不补 this.，tsc 过。
>    - I8（raw lambda 参数误限定）：raw 文本扫描识别 `ident =>`/`(a, b) =>` 形态（支持
>      `->`/`=>`，跳过字符串/注释）并入排除集；结构化 `JavaLambdaExpression.Parameters`
>      经 `_shadowStack` 收集。回归测试 3 个（raw 单参/双参 + 结构化）。注：完整转换
>      管线 lambda 直通缺口（`->`→`=>` 属 M2+、`System.Func` 未映射）为既有缺口，
>      I8 L3 tsc 受阻，以 IR 级断言 + 探针产物（`x -> x * 2` 不被限定）为证据。
>    - Minor：TS2415 派生/基类同名私有后备字段冲突（virtual+override 各自生成字段）→
>      `RenameConflictingBackingField` 派生字段加 `_` 后缀并同步访问器体文本与上下文表
>      （额外偏离，见下）；Roles 注释与实现同步；注记⑩ I3 表述修订（TS2855 仅限基类
>      字段，基类访问器支持 super 属性访问）。
>    - L3 探针（9 场景独立 tsc --noEmit --strict，exit=0）：chain（b.a.X）、array
>      （arr[0].X）、virtual（virtual+override 访问器对 + super.Y 读写）、forinit、
>      p1、i4（抽象 + override）、i2c1（接口读写 + dot 保守）、i5（结构化显式体）、
>      superfield（基类简化字段 → this.Z）。上轮场景复验无倒退；structprop 探针属性
>      部分正确（访问器对 + p.X 调用点），tsc 受阻于既有 struct 降级缺口（Java
>      `Point copy = new Point();` 直通），与本任务产物无关。
>    - 偏离清单（本轮）：继承参与属性统一禁字段简化（R1）使"virtual 基类 + 无 override"
>      的自动属性也发射访问器对而非字段（避免与派生形态冲突，形态一致性优先）；
>      TS2415 派生后备字段改名 `_` 后缀为访问器体文本层面的同步改造（未改共享 IR）。
>    - 提交：`fix(ts): resolve chained property targets and inheritance-aware reconstruction`，
>      hash 见 git log（3 文件：TsPropertyReconstructionRewriter.cs /
> ⑫ 三审修复（2026-08-07，R-1/R-2 + R-3/4/5/M1/M2 登记，红→绿）：
>    - 红→绿清单（证据）：新增 `TsPropertyReconstructionReview3Tests.cs` 12 个回归测试，
>      初跑 9 红 3 绿（绿：`Base_Call_Through_Grandparent_Base_Keeps_Super_Accessor`——
>      C 自身 override 注册命中，非递归路径；`Method_Return_Chain_Unresolvable_Leaf_Is_Conservative`
>      ——保守守卫用例；`Static_Property_Inherited_Access_Emits_Declaring_Class_Guard`
>      ——声明类归一守卫，父提交即绿，注记漏计，见注记⑬ 四审修正）；其中 2 个测试按 IR
>      实测重写（见"实测修订"），修复后 12/12 绿；
>      全量 TS 247/247（235 + 新增 12，完整转换用例内嵌 L3 tsc），Java 2423/2423 零回归。
>    - R-1（必修，基类链递归）：`TryResolveAccessor` 基类循环只查直接基类一层，递归版
>      `TryResolveAccessorInContext` 仅 R2 super 形态判定使用 → 爷孙链
>      `class A{X} class B:A {} class C:B` 中 this/base 调用残留 getX()/setX()（TS2339）。
>      修复：基类循环改调 `TryResolveAccessorInContext`（递归，三处落地——裸/this/super
>      调用 `TryResolveAccessor`、raw dot-target 末端查注册、结构化 dot-target
>      `TryRewriteCall`）；类上下文 BaseTypeNames 并入 `ImplementedTypes`
>      （`BaseTypeNamesFor`，接口属性经中间基类解析）。回归：this/base 爷孙链（tsc 过）、
>      `C.Stat` 派生类名静态访问（IR 级 raw `C.getStat()` → `C.Stat`）、接口继承链
>      `IMid : IShape` + 类链 `Mid→Base` + 爷孙链 `Leaf→Mid→Base` 三调用点（tsc 过）。
>    - R-2（建议本轮，方法返回类型链）：`ResolveRawSegment` 调用段只查 AccessorMap（属性
>      getter），用户方法 `d.GetB().A.X`（GetB 返回 B）链断在 getB() → 保守保留方法调用
>      （TS2339）。修复：`ClassContext.MethodReturnTypes`（类 + 接口方法名 → 返回类型，
>      void/空返回不登记），raw 调用段与结构化 `ResolveExpressionType` 新
>      `JavaMethodCallExpression` 分支均先查 AccessorMap 再查 MethodReturnTypes；方法返回
>      类型不可解析 → 保守保留。回归：`d.getB().a.X` 类返回链、`d.getI().X` 接口返回链
>      （均 tsc 过）、不可解析末端保守守卫、结构化 `d.getB().a.getX()` 链（IR 级）。
>    - R-5 随 R-2 落地：结构化链方法调用段原无 `JavaMethodCallExpression` 分支（真实管线
>      走 raw，潜在缺口）——R-2 的结构化分支已覆盖，不再为缺口。
>    - 实测修订（以 IR 为准，2026-08-07 四审探针实证修正）：R-1 声称"裸调用 `return X;`
>      → `return getX();` TS2304"——HIR 对**继承**的裸属性读/写同样降级为 getX()/setX()
>      （探针 Java 目标：`class C:B:A` 中 `return X;`/`X = 5;` 产物为 `return getX();`/
>      `setX(5);`），非"保留 `X` 裸标识符"；R-1"裸调用"以 IR 级 raw `return getX();`
>      回归覆盖与真实管线一致（红→绿）。
>    - R-3/4（Minor 登记，不修）：多段数组下标 `arr[0][1].X` 保守保留（TS2339）；泛型
>      字段链 `Box<T>.item.X` 不可解析保守保留。均属链式解析保守边界，维持方法调用。
>    - M1（规格 Minor 登记）：`_inheritedPropertyNames` 扁平集合过度保守——无继承关系
>      的同名属性也禁字段简化（IR 重复字段输入下可致 TS2300），后续限定到实际继承链或
>      修后备字段去重，本轮不修。M2：注记⑪ 偏离清单补登"无继承关系同名属性也走访问器
>      对"触发面（见注记⑪ R1 偏离 + 本注记 M1）。
>    - L3 探针（13 场景独立 tsc --noEmit --strict，exit=0）：新增 r2_chain（d.getB().a.X）、
>      r2_iface2（d.getI().X）、r1_iface2（接口/类/爷孙三链）、r1_static（A.Stat 守卫）；
>      上轮 9 场景（chain/array/virtual/forinit/p1/i4/i2c1/i5/superfield）复验无倒退；
>      lambda/structprop 既有缺口产物不变（`x -> x * 2` 不被限定、`Point copy = ...` 直通）。
>    - 提交：`fix(ts): resolve property accessors through base chains and method returns`，
>      hash 见 git log（3 文件：TsPropertyReconstructionRewriter.cs /
>      TsPropertyReconstructionReview3Tests.cs / 本计划文档）。
>      TsPropertyReconstructionReview2Tests.cs / 本计划文档）。
>      TsPropertyReconstructionReviewTests.cs / 本计划文档）。
---

> ⑬ 四审修复（2026-08-07，C1 循环继承栈溢出 + R-6 重载键控 + R-7 数组返回链，红→绿）：
>    - 红→绿清单（证据）：新增 `TsPropertyReconstructionReview4Tests.cs` 11 个回归测试，
>      初跑 4 个循环继承用例触发 `TryResolveAccessorInContext`/`ResolveMemberTypeInContext`
>      自递归 → StackOverflow 进程崩溃（红）；非循环 7 用例 5 红 2 绿（红：raw 0 参/1 参
>      消歧漏改写与误改写方向、同参个数歧义误改写、数组返回链漏改写、完整管线 Contains
>      失败；绿：结构化 1 参用例恰因"后声明覆盖"方向一致而绿、不可解析末端天然保守）；
>      修复后 11/11 绿；全量 TS 258/258（247 + 新增 11，完整转换用例内嵌 L3 tsc），
>      Java 2423/2423 零回归。
>    - C1（Critical，IR 级循环继承 → 栈溢出崩溃）：`TryResolveAccessorInContext`、
>      `ResolveMemberTypeInContext`、`HasFieldInBaseChain` 三条基类链递归均无环保护，
>      self-extends（`class A : A`）或互相 extends（`A:B`/`B:A`）输入时无限递归。C# 语法
>      不可达（编译器拒绝循环继承），纯 IR 防御缺口。修复：三处递归加 `HashSet<string>`
>      visited 参数（`finally` 移除，菱形继承不受影响），遇环即中止 → 保守不改写。
>      回归 4 个（自环裸调用、互环裸调用、互环成员段、互环末端访问器查注册），均不得
>      崩溃且保留方法调用。
>    - R-6（Important，同名重载方法返回类型字典覆盖）：`RecordMethodReturnTypes` 键仅
>      方法名 → `GetB()`/`GetB(int)` 后声明者覆盖先声明者，解析结果取决于声明顺序
>      （漏改写 + 潜在误改写，C1 类）。修复：键改为"方法名 + 参数个数"
>      （`MethodReturnKey`）；raw 调用段 `ExtractRawChainSegments` 新增实参个数提取
>      （`CountTopLevelArgs` 顶层逗号计数），结构化分支用 `Arguments.Count`；同名同参
>      数个数的重载（仅参数类型不同）静态无法判定 → 移除键 → 调用点保守不改写
>      （宁可不改不可误改）。回归 4 个（raw 0 参/1 参消歧、结构化 1 参消歧、同参个数
>      歧义保守、完整管线双调用点 Contains 断言——L3 不启用，重载发射缺口 TS2393 已
>      登记 M3 Task 23 处理）。
>    - R-7（Minor，方法返回数组 + 下标链漏改）：`ExtractRawChainSegments` 数组段要求
>      `[` 前为标识符字符，`getA()[0]` 的 `)` 导致返回 null → 保守保留 `getX()`（TS2339）。
>      修复：`]` 段反向解析后若前字符为 `)` 则先解析调用段（复用 `)` 分支逻辑）。
>      回归 3 个（0 参数组链、带参数组链、数组元素类型不可解析保守）。
>    - 三审登记偏差修正（Volta 三审复核 + 本注记实证，Minor）：注记⑫ "初跑 9 红 2 绿"
>      实测为 9 红 3 绿（`Static_Property_Inherited_Access_Emits_Declaring_Class_Guard`
>      父提交即绿，漏计）；注记⑫ "实测修订"声称"HIR 对继承裸属性读不降级"与实测不符——
>      探针（Java 目标）`return X;`（X 在爷孙基类）实测降级为 `return getX();` 并被正确
>      改写为 `this.X`，本注记按实证修订，Review3 测试类注释同步修正。
>    - 提交：`fix(ts): guard circular inheritance and key method return types by arity`，
>      hash 见 git log（4 文件：TsPropertyReconstructionRewriter.cs /
>      TsPropertyReconstructionReview4Tests.cs / TsPropertyReconstructionReview3Tests.cs（注释修正） /
>      本计划文档）。

---

## Task 17: 枚举（显式值与 [Flags]）

**Files:**
- Modify: `src/CSharpToJava.Core/TypeScript/TsTypeWriter.cs` / `TypeScriptEmitter.cs` / `Pipeline/Passes/SingleFilePasses.cs`
- Create: `src/CSharpToJava.Core/TypeScript/Rewriters/TsEnumCallRewriter.cs`
- Create: `tests/CSharpToTypeScript.Tests/TsEnumEmissionTests.cs`

- [x] **Step 1: 核对 IR 中枚举形态**

先转换样本确认：`JavaEnumDeclaration.Values` 带载显式数值（C# `enum E { A = 1 }`）。Java 枚举无数值，若 IR 丢失显式值，需在 HIR 层保留——优先不改共享 IR。

实测（探针 Java 目标，运行时验证）：
- 普通枚举→ `JavaEnumDeclaration`，`Values` 为 `"Name(value)"` 形态（显式值保留；无显式值时为 `A(0)/B(1)/...`），末尾附 `_UNMAPPED(0)/(-1)` 哨兵；枚举体含 `value`/私有字段 + `E(int v)` 构造器 + `getValue()/fromValue()/fromValueUnchecked()` 方法（Java 专属产物）。
- `[Flags]` 枚举→ **共享管线已转为普通类**（`JavaClassDeclaration` + `public static final int` 常量字段 + `and/or/complement/has/fromValue/fromValueUnchecked` 静态辅助），类型引用已擦除为 `int`；辨识方式：`ConversionContext.TypeMapper.IsFlagsEnum(name)`。
- 调用点形态（全部在共享管线内完成）：`(int)e` → `e.getValue()`；`(E)i` → `E.fromValue((int)(i))`；隐式枚举位运算 → `e.ordinal() & f.ordinal()`；`HasFlag` 已内联为 `((s & f) != 0)`；`Enum.Parse` → `(Color)(EnumHelper.parse(Color.class, s))`；`Enum.GetName` → `Enum.getName(Color.class, c)`。

- [x] **Step 2: 实现发射**

1. 普通枚举：直译 `export enum X { A = 1, ... }`（解析 `Values` 中 `Name(value)` 形态，`_UNMAPPED` 哨兵剔除；枚举体不发射；long 值后缀 `L/l` 剥除）。
2. `[Flags]` 枚举（spec §3.2）：发射为 const 对象 + 联合类型：

```typescript
export const Style = { None: 0, Bold: 1, Italic: 2 } as const;
export type Style = (typeof Style)[keyof typeof Style];
```

   辅助方法（and/or/complement/has/fromValue）为共享管线死代码（调用点已内联为位运算），不发射；`HasFlag` 已在共享管线内联为 `(flags & flag) != 0`（非 spec 的 `=== flag` 形式，语义等价，偏离见注记）。
3. 新增 `TsEnumCallRewriter`（发射前装配）：`x.getValue()`/`x.ordinal()` → `x`（数字枚举即 number，仅目标类型可解析且为枚举时改写，C1 保守）；`E.fromValue(v)`/`fromValueUnchecked(v)` → `(v) as E`（剥除 `(int)/(long)` 包装；TS 数字枚举可持有任意数值，对齐 C# 未定义值语义）；`Enum.Parse/EnumHelper.*/Enum.getName` → 诊断 + 兼容库桩（M4）；枚举 `.toString()` 语义差异（TS 返回数字字符串）→ 诊断。结构化与 raw 两路均覆盖。
4. 测试：`TsEnumEmissionTests` 13 个（显式/隐式/负值枚举发射、成员使用、cast 调用点、位运算调用点、this 字段 cast、flags 形态、flags 使用 + HasFlag、Enum.Parse 诊断、ToString 诊断、IR 级结构化 getValue/fromValue）。

- [x] **Step 3: 运行测试 + 提交**

Run: `dotnet test tests/CSharpToTypeScript.Tests/CSharpToTypeScript.Tests.csproj --filter "FullyQualifiedName~TsEnumEmissionTests"`
Actual: PASS（13/13，含 L3 tsc）；全量 TS 271/271（258 + 新增 13），Java 2423/2423 零回归。

**注记（2026-08-07 Task 17 实施，实测为准）：**
> ① 红→绿清单（证据）：新增 13 测试，初跑 12 红 1 绿（绿：`Enum_Parse_Emits_Diagnostic`——开发中第一版断言太宽，`"Enum"` 命中既有诊断，改紧为 `"Enum.Parse"` 后红）；第一轮实现后 12/13，剩余 1 红为 flags L3（既有裸字段限定缺口 TS2663，测试改用 `this.s` 后绿）；翻译过程中发现 raw 改写略除 `.getValue()` 时保留了前置 `.`（`c2.`）——回退 sb 末尾一个字符后绿。
> ② 已登记限制（未修）：（1）raw `(int)` 强转在 fromValue 实参以外仍为既有缺口（如 `(Style)3` 字段初始化器→ `(int)(3)`，与三审登记的 raw int 缺口一致）；（2）`(Color)(EnumHelper.parse(...))` 外层 raw 强转未剥（EnumHelper 诊断已发，产物不参与 L3）；（3）long 底层枚举值 > 2^53 精度不保证（TS 数字枚举无 bigint 成员）；（4）枚举 `.toString()`/`GetHashCode` 语义差异诊断，M4 兼容库 `CsEnum.getName` 桩。
> ③ 偏离清单：flags 辅助方法不发射（死代码）；`HasFlag` 内联形为 `!= 0` 而非 spec 文字的 `=== flag`（语义等价，共享管线形态）；`Enum_Declaration_Emits_Members`（既有）的裸 `Values` 条目仍支持（无括号形态直发名称）。
> ④ 提交：`feat(ts): emit enums with explicit values and flags as const objects`，hash 见 git log（6 文件：TsEnumCallRewriter.cs 新增 / TsTypeWriter.cs / TypeScriptEmitter.cs / SingleFilePasses.cs / TsEnumEmissionTests.cs / 本计划文档）。

## Task 18: 字符串插值与 String.Format

**Files:**
- Modify: `src/CSharpToJava.Core/TypeScript/TsExpressionWriter.cs`
- Modify: `src/CSharpToJava.Core/Pipeline/Passes/SingleFilePasses.cs`（`RunTsRewriters` 返回 `CompatSymbols`）
- Modify: `src/CSharpToJava.Core/TypeScript/TypeScriptEmitter.cs`（compat 导入登记）
- Modify: `tests/CSharpToTypeScript.Tests/TsConversionTestBase.cs`（compat 测试桩）
- Create: `src/CSharpToJava.Core/TypeScript/JavaToCSharpFormatTranslator.cs`
- Create: `src/CSharpToJava.Core/TypeScript/Rewriters/TsStringCallRewriter.cs`
- Create: `tests/CSharpToTypeScript.Tests/TsStringInterpolationTests.cs`

- [x] **Step 1: 核对 IR 中插值形态**

C# 插值字符串在 HIR 后的实际形态（字符串拼接 `+` / Java `String.format(...)` / Raw 表达式）需先转换样本确认，再决定发射策略。

实测（探针 Java 目标，运行时验证）：
- 简单插值 `$"count={n}"` → `"count=" + n`（字符串拼接 `+` 链，字面量段保序）。
- 带格式符 `$"{d:F2}"` → `String.format("%.2f", d)`（共享管线 ConvertCSharpFormatToJava 已把 C# 格式符转为 Java 指令）。
- `string.Format("{0} {1}", ...)` → `String.format("%1$s %2$s", ...)`（位置参数 `%N$` 形态）。
- 对象插值 `$"{o}"` → `String.valueOf(o)`。
- 4+ 段插值 / 内联 builder → `new StringBuilder().append(...).toString()` 链。
- 非字面量格式串（变量作格式串）→ `StringHelper.formatCs(...)`（C# 侧运行时转换 `{N}` 占位符）。

- [x] **Step 2: 实现**

1. 简单插值（无格式符）→ 模板字符串 `` `${x}` ``（spec §1.4.7）：raw 与结构化 `+` 链统一按"链首或第二段为字符串字面量"重建模板（首个 `+` 即字符串拼接，C#/JS 规则一致）；`x + y + "a"`（首个 `+` 可能为数值相加）与纯表达式链保守保持 `+` 拼接。
2. 带格式符 / `string.Format` → `CsString.format(...)`（spec §4.3.6 契约）：新增 `JavaToCSharpFormatTranslator` 将 Java 指令逆映射回 C# `{N}`/`{N:spec}` 格式，覆盖 `%s/%N$s`→`{N}`、`%04d`→`{N:D4}`、`%,.2f`→`{N:N2}`、`%.2f`→`{N:F2}`、`%e/%g/%x`、对齐 `%4d`→`{N,4}`、`%%`→`%`、`{`/`}`→`{{`/`}}`；未识别指令原样保留 + 诊断。新增 `TsStringCallRewriter`（发射前装配，`RunTsRewriters` 返回 `CompatSymbols` 供发射器注册 compat 导入）：raw/结构化 `String.format(...)`→`CsString.format(...)`、`StringHelper.formatCs(...)`→`CsString.format(...)`、`String.valueOf(x)`→`String(x)`、builder 链→模板/拼接、文本级 `"a" + expr` 链→模板字符串。
3. 测试：`TsStringInterpolationTests` 28 个（spec §5.3 M2 组；含 L3 tsc 编译用例）。

- [x] **Step 3: 运行测试 + 提交**

Run: `dotnet test tests/CSharpToTypeScript.Tests/CSharpToTypeScript.Tests.csproj --filter "FullyQualifiedName~TsStringInterpolationTests"`
Actual: PASS（28/28，含 L3 tsc）；全量 TS 299/299（271 + 新增 28），Java 2423/2423 零回归。

**注记（2026-08-06 Task 18 实施，实测为准）：**
> ① 关键形态与实现路径（证据）：raw `Console.WriteLine` 中 `String.format("%.2f", d)` → `CsString.format("{0:F2}", d)`；结构化 `String.valueOf(o)` → `String(o)`；4+ 段 builder 链（`new StringBuilder().append(...).toString()`）→ 模板字符串；非字面量格式串 `StringHelper.formatCs` → `CsString.format`（契约一致）。翻译器映射与共享管线 ConvertCSharpFormatToJava 产出的指令子集互逆（D/F/E/X/N/G/P），未知格式符占位注释 `"%s /* TODO: format specifier Q */"` 丢弃 + 诊断。
> ② 模板规则：仅"链首或第二段为字符串字面量"时整链重建模板（首个 `+` 即字符串拼接，C# 与 JS 规则一致）；`x + y + "a"` 与纯表达式链（`$"{n}{m}"` 降级的 `n + m`）无类型信息，保守保持 `+` 拼接（JS 求值本身与 C# 一致）。
> ③ 已登记限制（未修）：（1）`%` 歧义——C# `{0:P2}`（×100 + %）与 `{0:F2}` + 字面 `%` 在共享管线同形（`%.Nf%%`），无法区分，按 Java 侧语义发射（`{N:F2}%`）+ 诊断；（2）花括号保真（共享管线级 bug）：`{{{0}}}` → Java `{{%1$s}}` 渲染 `{{x}}`，翻译器发 `{{{{{0}}}}}`（渲染同为 `{{x}}`）；（3）`$"{n}"` 裸返回（number vs string 语义）与相邻原始类型 `$"{n}{m}"` → `n + m` 数值相加（C# 字符串拼接 vs JS 数值相加）；（4）`String.valueOf` 的 null 语义未逐点验证（Java `"null"` vs JS `String(null)` 同为 `"null"`，形态一致）。
> ④ 偏离清单：`config/typescript/MemberMappings.json` 未改（`System.String.Format` 映射已在既有表中；实际形态由 rewriter 覆盖 Java `String.format`）；compat 桩为测试基座最小实现（`index.d.ts` 声明 `CsString.format`），完整兼容库归 M4。
> ⑤ 提交：`feat(ts): emit interpolated strings and String.Format via CsString.format`，hash 见 git log（7 文件：JavaToCSharpFormatTranslator.cs / TsStringCallRewriter.cs / TsStringInterpolationTests.cs 新增；TsExpressionWriter.cs / SingleFilePasses.cs / TypeScriptEmitter.cs / TsConversionTestBase.cs 修改；本计划文档）。

---

## Task 19: 数值语义（类型感知的整数除法/窄化/混算）

**Files:**
- Create: `src/CSharpToJava.Core/TypeScript/Rewriters/TsNumericSemanticsRewriter.cs`
- Modify: `src/CSharpToJava.Core/Pipeline/Passes/SingleFilePasses.cs`（`RunTsRewriters` 装配）
- Create: `tests/CSharpToTypeScript.Tests/TsNumericSemanticsTests.cs`

- [x] **Step 1: 设计**

IR 不携带类型信息（同 §2.6.1 重载消歧的困境），类型感知必须由 rewriter 持有 Roslyn 语义模型回调：`SingleFileTsEmitPass` 装配时注入 `state.Context.SemanticModel`。

- [x] **Step 2: 实现**

1. **整数除法**：两操作数经语义模型判定均为 C# 整型（非 long/ulong）时，将 `JavaBinaryExpression("/")` 替换为 `JavaRawExpression($"Math.trunc({l} / {r})")`；long/ulong（bigint）不包（JS bigint 除法天然截断，spec §2.5 修订注记）。补齐 Task 7 M1 阶段仅字面量包装的缺口。
2. **窄化转换**：`(byte)e`/`(sbyte)e` 等 → 掩码/截断辅助（沿用 Java 目标的 `& 0xFF` 策略，见 spec §1.4.2）。
3. **bigint/number 混算**：语义模型发现两侧类型不一致时插入显式 `BigInt(...)`/`Number(...)` 转换；无法判定时诊断。
4. `List.Clear()` 语句级特殊分支（Task 2 排除项）：调用点 `x.Clear()`（接收者为数组映射类型）→ `x.length = 0` 语句改写。
5. 测试：`TsNumericSemanticsTests`（spec §5.3：整数除法包 Math.trunc、窄化掩码、混算转换；另加“double 除法不被误包”反向用例）。

- [x] **Step 3: 运行测试 + Java 零回归 + 提交**

Run: `dotnet test tests/CSharpToTypeScript.Tests/CSharpToTypeScript.Tests.csproj`
Run: `dotnet test tests/CSharpToJava.Tests/CSharpToJava.Tests.csproj`（Java 零回归）
Actual: PASS（TS 314/314 = 300 基线 + 14 新增，含 L3 tsc；Java 2423/2423 零回归）。

**注记（2026-08-07 Task 19 实施，实测为准）：**
> ① 实现路径（证据）：新增 `TsNumericSemanticsRewriter`（syntax-tree 层，`SingleFileTsEmitPass` 在既有 TsRawStatementNormalizer 之前装配，注入 `state.Context.SemanticModel`），在发射 pass 步骤 1 前改写 syntax tree 并重建 compilation，再走共享 IR 管线。raw 文本直接流入（`Math.trunc`/`BigInt`/`Number` 在 Java 文本与 TS 中同形）；结构化节点（CastExpressionSyntax 等）经 `RoslynSyntaxTreeRewriter` 改写后重新绑定 semantic model。`InvocationExpressionTransformer.ApplyCamelCaseAndMappings` 增补 `BigInt`/`Number` 全局函数名白名单（TS 目标下不被 camelCase 改名）；`TsMemberWriter.NormalizeLiteral` 剥除 Java 数值 cast 前缀（`(long) (4294967295L)` → `4294967295n`）；`ExpressionTransformerHelpers.AdaptExpressionToTargetTypeCore` 在 TS 目标下共享适配层直通/转换（同型短路透传 + 跨类型 `BigInt(...)`/`Number(...)`），与 `TsRawStatementNormalizer` 无关。
> ② 实测产物形态（探针，全部经 L3 tsc 编译验证）：`a / b`（int/int）→ `Math.trunc(a / b)`，double 除法原样、long/ulong 除法原样；`(int)d` → `Math.trunc(d)`；`(long)x`（int）→ `BigInt(x)`；`(long)d` → `BigInt(Math.trunc(x))`；`(byte)long` → `(Number(x) & 0xFF)`、`(byte)double` → `(x & 0xFF)`、object 源均 `Number(...)`；`(sbyte)x` → `x << 24 >> 24`、`(short)x` → `x << 16 >> 16`、`(ushort)x` → `x & 0xFFFF`；`(uint)d` → `(Math.trunc(Number(x)) % 0x100000000 + 0x100000000) % 0x100000000`（十六进制 `0x100000000` 避免 long 后缀 `L` 泄漏）；混算 `a + BigInt(b)`、`Number(a) + b`、比较 `a > BigInt(b)`、字符串 `s + Number(a)`、移位 `x << BigInt(n)`、复合赋值 `a += BigInt(2)`；`l.Clear()`/`this.items.Clear()` → `l.length = 0`/`this.items.length = 0`。
> ③ 隐式转换不再泄漏 Java cast：`long Widen(int x) { return x; }` → `return BigInt(x);`；`double WidenD(int x)` → `return x;`（TS 统一 number 透传）；`long WidenByte(byte b)` → `return BigInt(b);`；`byte→int`（`NarrowByte`）直通。共享适配层 TS 分支判定规则：`!sourceIsBigInt && targetIsBigInt && 整型源` → `BigInt(...)`；`sourceIsBigInt && 目标 float/double` → `Number(...)`；其余（含同型）透传，窄化掩码语义由 rewriter 语法改写产出。
> ④ 无法判定的类型参数与 bigint 混算（`long M<T>(long a, T b) { return a + b; }`）→ 诊断 `unresolved operand type`（rewriter `WarnOnce`，每文件一次）。
> ⑤ 已登记限制（未修）：（1）`List.Clear()` 用例的 tsc 编译用 `global.d.ts` 桩 `type Integer = number; type CSharpList<T> = Array<T>;`——`List<int>` 泛型实参的 Java 风味名泄漏归既有基线 gap（Task 27/M4 类型映射）；（2）`const long MaxUInt = uint.MaxValue` 的 IR 形态为 `BigInt(4294967295L)`（共享适配层 TS 分支包裹），由 `TsMemberWriter.NormalizeLiteral` 归一化为 `4294967295n`（注记⑦，Sample.cs 实测无泄漏）；（3）移位计数 bigint 源（`x << (long)n`）未显式覆盖——计数侧按值侧类型包 `BigInt`/`Number`，bigint 源计数为 TS 合法形态（bigint 移位计数必须 bigint），未见泄漏用例。
> ⑥ 提交：`feat(ts): emit C# numeric semantics for TypeScript (integer division, narrowing, bigint mixing)`，hash 见 git log（3 文件新增：TsNumericSemanticsRewriter.cs / TsNumericSemanticsTests.cs / 本计划文档；4 文件修改：SingleFilePasses.cs / InvocationExpressionTransformer.cs / TsMemberWriter.cs / ExpressionTransformerHelpers.cs——return 语句与隐式提升路径在 TS 目标经共享适配层直通/转换）。

---

## Task 20: 可空值类型与 null 统一

**Files:**
- Modify: `src/CSharpToJava.Core/TypeScript/TsExpressionWriter.cs` / `TsMemberMappingTable.cs`
- Create: `tests/CSharpToTypeScript.Tests/TsNullableHandlingTests.cs`

- [x] **Step 1: 核对 IR 中可空形态**

C# `int?` 在 Java 目标映射为装箱 `Integer`/`Optional`（视 `UseOptionalForNullable`）。TS 目标统一 `number | null`（spec §1.4.7），需先转换样本确认 IR 实际形态。

实测（探针，默认选项 UseOptionalForNullable=false）：
- `int?` 字段/参数/返回值 → IR 整串类型名 `Integer`（long?/bool?/double?/char? 同理为 `Long`/`Boolean`/`Double`/`Character`；`object?` → `Object`）。
- `.HasValue`/`.Value` 在共享管线均经 `TryGetNullableValueUnboxMethod` → 拆箱调用 `p.intValue()`（该辅助不区分成员名，HasValue 与 Value 同形）。
- `??` → `JavaConditionalExpression`（raw 文本 `x != null ? x : y`，安全左值重复求值/复杂左值 temp 提升）；`??=` → raw 语句 `if (x == null) x = 5;`（简单标识符注入 pre-statement，复杂左值 temp 提升）。
- `Nullable<int>` 显式形态 → 同样 `Integer`。

- [x] **Step 2: 实现**

1. 类型映射：IR 整串装箱名 `Integer/Long/Boolean/Double/Float/Character/Short/Byte` 仅来自 C# 可空值类型 → `TsExpressionWriter.MapType` 映射后追加 `| null`（字段/参数/返回/局部变量全走此入口，一次改动全场景生效；`any` 吸收 null 不宽化）。与 Task 5 非可空映射的区分：泛型实参/继承约束等"Java 泛型装箱"位置（`IRepo<Integer>` 来自 C# `IRepo<int>`，非可空）走新增 `MapTypeNonNull` 不宽化——`MapHeritageType` 与 `FormatTypeParameters` 已切换。
2. `.HasValue` → `({expr} !== null)`；`.Value` → `({expr}!)`（非空断言；必要时运行时抛异常形态归 M4 兼容库）。共享管线 3 处 `TryGetNullableValueUnboxMethod` 调用点（`ExpressionTransformerFacade` 两处 + `IdentifierExpressionTransformer.TransformMemberAccess`）先经新增 `IsNullableValueProperty` 分流：TS 分支直通，Java 路径行为不变。
3. `??` → `({left} ?? {right})`（`BinaryExpressionTransformer.TransformCoalesceExpression` TS 分支，统一括号保证任意嵌入位置优先级）；`??=` → `{lhs} ??= {rhs}`（`AssignmentTransformer.TransformCoalesceAssignment` TS 分支，JS 复合赋值接收者单次求值无需 temp）。均为 ES2020/ES2021（tsconfig target ES2022 兼容）；产物不出现 `undefined`（断言用例）。
4. 测试：`TsNullableHandlingTests` 8 个（spec §5.3：字段/参数/返回值 `number | null` 等、`HasValue/Value` 展开、`??`/`??=` 直译、泛型实参不宽化反向用例、null 统一性、全部经 L3 tsc strict 编译）。

- [x] **Step 3: 运行测试 + 提交**

Run: `dotnet test tests/CSharpToTypeScript.Tests/CSharpToTypeScript.Tests.csproj`
Run: `dotnet test tests/CSharpToJava.Tests/CSharpToJava.Tests.csproj`（Java 零回归）
Actual: PASS（TS 322/322 = 313 基线 + 8 新增 + 1 探针，含 L3 tsc；Java 2423/2423 零回归）。

**注记（2026-08-07 Task 20 实施，实测为准）：**
> ① 实测产物形态（探针，全部经 L3 tsc strict 编译验证）：字段 `int? a;`/`= null`/`= 5` → `a: number | null = null`/`= null`/`= 5`；`long? d = 7` → `d: bigint | null = 7n`；`bool?`/`double?`/`char?` 同理（`char? g = 'x'` → `g: string | null = "x"`）；`object?` → `any`（不宽化）；方法 `int? M(int? p)` → `m(p: number | null): number | null`；`if (p.HasValue) return p.Value;` → `if ((p !== null)) { return (p!); }`；`p.Value + 1` → `(p!) + 1`；`x ?? y` → `(x ?? y)`（嵌套 `a ?? b ?? c` → `(a ?? (b ?? c))`）；`x ??= 5` → `x ??= 5;`；`p.HasValue ? p.Value : 0` → `((p !== null) ? (p!) : 0)`。
> ② 实现路径（证据）：类型宽化在 `TsExpressionWriter.MapType`（非计划列出的 `TsMemberMappingTable`——内置表保持 Java 装箱名 → 基础 TS 类型不变，宽化在发射入口按上下文追加，使 `MapHeritageType` 泛型实参可走 `MapTypeNonNull` 保持 `IRepo<int>` → `IRepo<number>`，避免整表改动破坏非可空泛型上下文）；`HasValue/Value` 三处调用点分流 + 新辅助 `IsNullableValueProperty`；`??`/`??=` 在共享转换器加 TS 目标分支（raw 文本直通发射器，与 Task 19 共享适配层模式一致）。
> ③ 已登记限制（未修）：（1）`.Value` 空值语义：C# 抛 `InvalidOperationException`，TS `!` 为编译期断言（运行时得到 undefined/NaN 而非异常），运行时抛异常兼容类归 M4 `CsNullable`；（2）`== null` 仍发射 `==`（spec 表建议 `===`，语义等价且与既有 `?.` 产物形态一致，全面相等语义归 M3 Task 21 相等重写）；（3）`List<int?>`/`IRepo<int?>` 泛型实参可空性在 IR 层丢失（与 `IRepo<int>` 同形 `Integer`，泛型实参一律按非可空映射），归 Task 27/M4 语义模型类型映射；（4）`(int)p`（可空强转，C# 空值抛异常）走 Task 19 窄化路径 `Math.trunc(p)`（null → 0），归 M4。
> ④ 提交：`feat(ts): emit C# nullable semantics for TypeScript (number | null, HasValue/Value, ??/??=)`，hash 见 git log（1 文件新增：TsNullableHandlingTests.cs；8 文件修改：TsExpressionWriter.cs / TsMemberWriter.cs / TsTypeWriter.cs / ExpressionTransformerFacade.cs / IdentifierExpressionTransformer.cs / BinaryExpressionTransformer.cs / AssignmentTransformer.cs / ExpressionTransformerHelpers.cs；本计划文档）。

---

## Task 21: 异常发射与兼容异常类映射

**Files:**
- Modify: `src/CSharpToJava.Core/TypeScript/TsMemberMappingTable.cs`（异常反向映射：内置覆盖表 + 兼容类集合 + Java TypeMappings 全量提取 + `TryMapException`）
- Modify: `src/CSharpToJava.Core/TypeScript/TsExpressionWriter.cs`（`EmitNew`/`EmitInstanceOf` 异常映射 + `MapExceptionName` raw 回调）
- Modify: `src/CSharpToJava.Core/TypeScript/TsStatementWriter.cs`（catch instanceof 类型名映射 + raw 归一化接线）
- Modify: `src/CSharpToJava.Core/TypeScript/TsRawStatementNormalizer.cs`（raw 文本 `new X(`/`instanceof X` 异常名归一化）
- Modify: `src/CSharpToJava.Core/TypeScript/TsTypeWriter.cs`（基类 `extends` 异常名映射）
- Modify: `src/CSharpToJava.Core/TypeScript/TypeScriptEmitter.cs`（传入 Java TypeMappings 路径）
- Modify: `tests/CSharpToTypeScript.Tests/TsConversionTestBase.cs`（compat 桩补 §4.3.9 异常类层次）
- Modify: `tests/CSharpToTypeScript.Tests/TsStatementWriterTests.cs`（IllegalStateException 期望改为映射后名）
- Create: `tests/CSharpToTypeScript.Tests/TsExceptionEmissionTests.cs`

- [x] **Step 1: 实现**

1. catch/throw 中的 Java 风味异常名（如 `IllegalArgumentException`/`IllegalStateException`）映射到兼容库名（`ArgumentException`/`InvalidOperationException`，spec §4.3.9）；未映射异常 → `CsException` + 诊断。
2. `new IllegalArgumentException("msg")` → `new ArgumentException("msg")`，imports 自动加 compat（`WithImports` 机制）。
3. catch 按类型过滤的 instanceof 链已在 Task 8 实现；本任务验证其与兼容异常类配合（异常类是 class，`instanceof` 可用，spec §4.3.9）。
4. 测试：`TsExceptionEmissionTests`（spec §5.3 M2 组）。

- [x] **Step 2: 运行测试 + 提交**

Run: `dotnet test tests/CSharpToTypeScript.Tests/CSharpToTypeScript.Tests.csproj`
Run: `dotnet test tests/CSharpToJava.Tests/CSharpToJava.Tests.csproj`（Java 零回归）
Actual: PASS（TS 337/337 = 322 基线 + 15 新增，含 L3 tsc；Java 2423/2423 零回归）。

**注记（2026-08-07 Task 21 实施，实测为准）：**
> ① 实测产物形态（全部经 L3 tsc strict 编译）：`throw new InvalidOperationException("bad state")` → `throw new InvalidOperationException("bad state")`（imports 自动收集 `import { InvalidOperationException } from "@csharptotypescript/compat"`）；`throw new Exception("boom")` → `throw new CsException("boom")`（IR RuntimeException → 覆盖映射，无降级注释）；`throw new NullReferenceException("null")` → `throw new CsException /* cs2j: unmapped exception */("null")`（raw 归一化形态，注释位于名字与括号之间）；`catch (InvalidOperationException ex)` → `if (ex instanceof InvalidOperationException) {`；`catch (Exception ex)` → `else if (ex instanceof CsException) {`；裸 `catch` → 链尾 else；`class MyException : Exception` → `class MyException extends CsException`（基类经 `MapTypeNonNull` 映射）；`Exception field;`/`Exception M(Exception p)` → `field: CsException | null = null`/`m(p: CsException): CsException`；用户定义异常（`MyException`）不做映射且不误加 compat 导入。
> ② 实现路径（证据）：完整反向映射表由 `TsMemberMappingTable` 承载——5 条 Java 标准名覆盖（`IllegalArgumentException→ArgumentException`、`IllegalStateException→InvalidOperationException`、`IndexOutOfBoundsException→IndexOutOfRangeException`、`RuntimeException→CsException`、`UnsupportedOperationException→NotSupportedException`）+ spec §4.3.9 兼容类名集合（恒等）+ “已知 BCL 异常名”集合（运行时从 Java `TypeMappings.json` 全量提取 csharp/java 两侧简单名，配置缺失回退内置高频集合）；`TryMapException` 返回 `isFallback` 供调用方追加 `/* cs2j: unmapped exception */` 注释，`TryMapType` 同步挂钩覆盖字段/参数/返回值/局部变量等类型位置。发射端四处接线：`EmitNew`、`EmitInstanceOf`、`EmitTryCatch`（结构化节点）+ `TsRawStatementNormalizer` 规则 ④（方法体直通文本的 `new X(`/`instanceof X`，跳过字符串字面量、标识符边界命中）——实测方法体 throw 为 raw 文本，节点层映射不覆盖，raw 归一化是必需项（计划未列，Task 8 同款“计划外必需项”模式）。
> ③ 已登记限制（未修）：（1）歧义共射名取语义最近兼容类：`UnsupportedOperationException` 兼收 `NotImplementedException`/`NotSupportedException`，统一映射 `NotSupportedException`（IR 无源类型信息，M4 语义模型可细分）；（2）`NullReferenceException`（IR `NullPointerException`）、`TimeoutException`、`SocketException` 等无兼容类 → 降级 `CsException` + 诊断注释（spec §4.3.9 类层次之外的 BCL 异常，M4 按需扩展）；（3）when 过滤为 false 的分支降级为跳过该 catch（不尝试后续 catch），strict 下需尾部 return 兜底（完整 when 语义归 M3 模式匹配）；（4）compat 桩仅含 §4.3.9 类 + `getMessage()`，`Message`/`InnerException` 等成员映射归 M4；（5）用户类型与 BCL 异常同名（如自建 `ConfigurationException`）会被误识别为 BCL 异常（与 Java 路径命名空间消歧机制不同，M5 语义模型修正）。
> ④ 提交：`feat(ts): map BCL exceptions to compat classes for TypeScript`（9 文件修改 + 2 文件新增：TsExceptionEmissionTests.cs / TsMemberMappingTableTests.cs 追加 7 用例；本计划文档）。

---

## Task 22: M2 验收（L3 冒烟编译 + 零回归）

**Files:**
- Create: `tests/CSharpToTypeScript.Tests/TsSmokeCompileTests.cs`

- [x] **Step 1: 实现冒烟编译测试**

将 M1+M2 全部特性样本（类/字段/泛型/接口/属性/枚举/异常/插值/数值/可空）合并为一个多文件工程，调用基类 `AssertCompiles`（共享 fixture）整体 `tsc --noEmit`（spec §5.5）。无 Node 环境时自动 Skipped。

- [x] **Step 2: 全量验收**

Run: `dotnet test tests/CSharpToTypeScript.Tests/CSharpToTypeScript.Tests.csproj`
Run: `dotnet test tests/CSharpToJava.Tests/CSharpToJava.Tests.csproj`（Java 零回归）
Expected: 全绿（L3 在有 Node 环境时通过）。

- [x] **Step 3: 提交**

**实现注记（2026-08-07）：**
- `TsSmokeCompileTests`：9 个独立 C# 样本（类/控制流/泛型/接口/属性/枚举/异常/插值/数值/可空），各自
  C# 源 → 独立 `.ts`；首样本经主入口写入，其余经 `AssertCompilesWithCompat` 的 `additionalFiles`
  写入同一工程，整体 `tsc --noEmit`（strict + NodeNext）。全部样本复用既有测试已验证的产物形态。
- **计划外必需项**：`TsRawStatementNormalizer` 新增规则 ⑤——raw 文本中 long 字面量 `7L` → `7n`
  （0x/0b/下划线均覆盖，跳过字符串/字符区域、标识符边界守卫）；数值语义 rewriter 改写出的
  `BigInt(...)` 混合链同样落在 raw 文本，必须归一化（3 个新单测，见 `TsRawStatementNormalizerTests`）。
- **已知限制（登记，不在本任务修复）**：
  1. raw 文本中的属性访问不“去括号化”——`s.Length` 直通为 `s.length()`（TS2349）。冒烟样本改用
     已验证形态（`s == null` / 字面量返回）；待后续成员访问重写任务统一处理。
  2. 方法体内裸字段引用不注入 `this.`（TS2663）——C# 源省略 `this.` 时 IR body 直通裸字段名。
     冒烟样本采用显式 `this.radius`（既有测试已验证形态）；待 M3 属性重构/成员访问任务处理。
- 验收结果：TS 341/341 全绿（337 基线 + 3 normalizer 新增 + 1 冒烟），Java 2423/2423 零回归。

---

# 里程碑 M3：语义降级（S 级 rewriter + 特性降级）

**验收标准：** §5.4 全部 rewriter 测试全绿 + §5.3 M3 组全绿 + L3；Java 零回归。

> M3 任务以大纲形式给出（每个任务仍遵循“写失败测试→实现→验证→提交”循环）；开工前逐个展开为细粒度步骤，展开时需先核对相关 IR 形态（参照 M1/M2 任务中的核对惯例）。

## Task 23: TsOverloadDisambiguationRewriter（方法重载改写，spec §2.6.1）

- **Files:** `Rewriters/TsOverloadDisambiguationRewriter.cs`（含同文件 IR 层工厂合成器 `TsCtorFactoryRewriter`）、`TsOverloadRewriterTests.cs`
- **要点：** 同名重载分组；首个保留原名，其余追加 `TsNaming.OverloadSuffix` 类型后缀；调用点按实参数量 + 语义模型静态类型消歧（装配时注入 `SemanticModel` 回调）；构造器重载 → `Create$N` 静态工厂 + `new` 调用点同步；无法静态判定 → 诊断 + 保留首重载。
- **验证：** §5.4 `TsOverloadRewriterTests` 全部用例 + L3。

- [x] **Step 1: 核对 IR 形态与架构决策（2026-08-07 实测）**

已核对（证据见 Task 19 注记同款探针）：
- IR 方法体/构造器体均为 raw 文本（`JavaMethodDeclaration.Body`/`JavaConstructorDeclaration.Body` 字符串），
  `JavaMethodCallExpression` 等结构化节点不携带源码位置 → **调用点改写必须在 C# 语法树层完成**
  （沿用 Task 19 确立的 syntax-level rewriter + 重建编译模式）；IR 层无法做语义消歧。
- 构造器当前发射：`TsTypeWriter.EmitClass` 仅发射第一个 ctor，其余降级为 `// TODO(TS): 第 N 个构造器...`
  注释；`new Point(5, 6)` 调用点原样发射（对重载类为非法 TS）——Task 23 替换该降级路径。
- `TsNaming.OverloadSuffix`/`FactoryName` 已就绪（Task 5 预留）；`TsMemberWriter.EmitMethod` 支持
  static + 类型参数 + 映射返回类型；`EmitConstructor` 对派生类无条件补 `super()`（私有空构造器可直接复用）。
- 调用点名称经 `ApplyCamelCaseAndMappings`/`TransformMemberInvocation` camelCase（如 `Create$2` → `create$2`）；
  C# 标识符不含 `$`，工厂名 `Create$N` 需在 `TransformMemberInvocation` 顶部加**大小写保留守卫**
  （`ConversionContext.TsFactoryMethodNames` 集合，symbol==null 时直通）。

**架构决策（实现以此为准）：**
1. `TsOverloadDisambiguationRewriter` 为 **CSharpSyntaxRewriter**（语法树层，注入 `state.Context.SemanticModel`），
   在 `SingleFileTsEmitPass` 数值语义 rewriter 之后装配（两者都改写语法树并重建编译）。
2. **方法重载**：同一类型内按名称分组（跳过访问器/运算符/显式接口实现）；组内按参数数量升序稳定排序，
   首个保留原名，其余 `原名 + OverloadSuffix(参数 TS 映射名 tokens)`；tokens 由 `ITypeSymbol` 生成
   （SpecialType → number/bigint/string/boolean/any；`int?` → `number_nullable`；泛型 → `list_string` 式
   容器_元素；类型参数 → 小写名；未命中 → 小写 C# 名）；组内重名冲突追加 `$N` 序数。调用点经语义模型
   `GetSymbolInfo` 解析 `IMethodSymbol.OriginalDefinition` → 查改名表（Roslyn 已做重载决议，无需自行
   按实参类型匹配）；无法解析且名字命中重载组 → 诊断 + 保留原名。方法组引用（delegate 化）→ 已知限制。
3. **构造器重载**（语法层只做调用点 + 命名登记，工厂合成在 IR 层）：
   - 语法层：组内 ctor 按参数数量升序稳定排序，全部转工厂 `Create$1..N`（`TsNaming.FactoryName`，
     1 基序数）；`new Type(args)` / target-typed `new(args)` 调用点经语义模型解析目标 ctor → 改写为
     `Type.Create$N(args)`（object/collection 初始化器形态 → 诊断 + 保留）；工厂名登记入
     `context.TsFactoryMethodNames` 供大小写守卫。
   - IR 层（`TsCtorFactoryRewriter`，同文件，RunTsRewriters 内装配）：按同一算法独立重算工厂名
     （稳定排序一致，无跨层匹配）；`cls.Constructors` 清空 → 补合成私有无参 `constructor()`（TS 将
     字段默认值提升进该构造器，`new Foo()` 自动应用默认值）；每个用户 ctor → `static Create$N(...): Foo`
     方法，Body 为 raw TS 文本：`const self = new Foo();` + 内联构造器体（`this(...)` 委托按实参数量
     递归内联目标体，先委托后自身；`super(...)` → 诊断 + 跳过，私有构造器补 `super()` 兜底基类默认值）+
     `return self;`；内联体中 `this`（标识符边界 + 跳字符串）→ `self`。
4. **已知限制（登记，不在本任务修复）**：方法组引用（`Func<int,int> f = M;`）；派生类构造器工厂不内联
   基类构造器体（`super(...)` 跳过，仅基类字段默认值生效）；`readonly` 字段 + 构造器重载（工厂内
   `self.x = x` 触发 TS2540）；构造器体内裸 `return;`（工厂返回类型非 void 时 TS2355）；同参数量
   `this(...)` 委托歧义（取首个 + 诊断）；record 主构造器不参与分组（M3 后续任务）。

- [x] **Step 2: 写失败测试**（`TsOverloadRewriterTests`，spec §5.4）

1. 同名两重载（不同参数数量）→ 第二改名 `Parse_string_number`，声明 + 同/跨类型调用点同步改写；
2. 同参数量重载（int vs string）→ 按静态类型消歧（`Fmt(x)`/`Fmt(s)` 各自命中）；
3. 无歧义单实现不改名（断言无后缀名出现）；
4. 构造器重载 → `static Create$1/Create$2` 工厂 + `new` 调用点改写（含字段初始化器/方法体内形态）+ 
   `this(...)` 委托内联顺序断言；
5. 无法静态判定（`object` 实参）→ 诊断 + 保留原名；
6. 代表性产出过 L3 `tsc --noEmit`（`AssertCompilesWithCompat`）。

- [x] **Step 3: 实现**（`TsOverloadDisambiguationRewriter` + `TsCtorFactoryRewriter` + 管线装配）

管线：`SingleFileTsEmitPass.Execute` 在数值语义重建后追加重载 rewriter + 重建；`RunTsRewriters` 追加
`TsCtorFactoryRewriter(context)`；`TransformMemberInvocation` 顶部加工厂名大小写守卫；
`ConversionContext` 新增 `TsFactoryMethodNames` 属性；`TsTypeWriter.EmitClass` 的 TODO 降级路径保留
（直接发射器测试仍走注释路径，转换管线经 rewriter 后不再触达）。

- [x] **Step 4: 运行测试 + Java 零回归 + 提交**

Run: `dotnet test tests/CSharpToTypeScript.Tests/CSharpToTypeScript.Tests.csproj`
Run: `dotnet test tests/CSharpToJava.Tests/CSharpToJava.Tests.csproj`（Java 零回归）
Expected: 全绿（TS 新增用例全过 + L3；Java 2423 零回归）。

**实现注记（2026-08-07）：**
- 实测坑（与 Step 1 决策的偏差/修正）：
  ① rewriter 必须**先访问子节点、后改标识符**——在 `node.WithIdentifier(...)` 之后调用
     `base.Visit...` 会访问到"不在语法树中"的子树（Roslyn 节点树归属问题），先 base 访问
     再对结果 `WithIdentifier` 即可；`VisitMethodDeclaration` 曾漏 return 改名结果（初版
     返回未改名节点，调用点已改而声明未改），已修。
  ② 方法体未限定实例成员引用不注入 `this.`（Task 22 已登记限制）——测试样本统一用显式
     `this.Parse(...)` 形态；`TsPropertyReconstructionReview4Tests.Overloaded_Method_Return_Chain_Full_Pipeline`
     期望更新为 Task 23 新行为（`GetB(int)` → `getB_number`，调用点同步）。
  ③ 工厂调用点大小写保留：`InvocationExpressionTransformer.TransformMemberInvocation` 顶部加
     `TsFactoryMethodNames` 守卫（symbol==null 时直通，避免 `Create$2` camelCase 成 `create$2`）。
- 验收结果：TS 349/349 全绿（341 基线 + 8 新增，含 L3 tsc），Java 2423/2423 零回归。
- 工厂形态（L3 验证）：`private constructor() {}` + `static Create$N(...): T { const self = new T();
  内联体（this→self，this(...) 委托递归内联）; return self; }`；字段默认值由 TS 提升进私有构造器。

## Task 24: TsGenericErasureRewriter（泛型擦除降级，spec §2.6.2）

- **Files:** `Rewriters/TsGenericErasureRewriter.cs`、`TsGenericErasureRewriterTests.cs`
- **要点：** `default(T)` 类型特定零值/`csDefault`；`new T()` 工厂参数注入；`typeof(T)` → TypeToken；`typeof(SomeClass)` 直译类引用（`.Name`→`.name`）；`where T : new()/struct/class` 约束移除联动。

- [x] **Step 1: 核对 IR 形态与架构决策（2026-08-07 实测）**

已核对（探针证据）：
- 共享前端对五个构造的现状（IR raw 文本/参数形态）：
  `typeof(T)`（类型参数）→ 方法签名注入 `Class<T> _cs2j_T` + 体 `TypeHelper.toWrapperType(_cs2j_T)`，
  调用点注入 `int.class` 实参（TS 全部非法）；`typeof(Foo)`（具体类）→ `Foo.class`（结构化
  `JavaMemberAccessExpression{MemberName="class"}`），`.Name` → `.getName()`；`default(T)`（无约束）
  → 方法级 `DefaultValue.of(_cs2j_T)` / 类级合成 `_cs2jDefault_T()` 辅助方法；`default(T)`（struct 约束）
  → `new T()`；`default(T)`（class 约束）→ `null`（已正确）；`new T()`（new() 约束）→ `new Object()`
  （与 `new object()` 不可区分）；`is T` → `x instanceof T`；`(T)x` → `(T)(x)`（且 `(Widget)x`/`(String)x`/
  `(double)x` 具体/BCL 强转同样为非法 TS，属既有缺口）。
- 标记调用透传验证：`csDefault(TypeToken.of("T"))`、`TypeToken.nameOf("T")`、`TypeToken.isInstance("T", x)`
  均以 C# 树直写 → 前端原样透传为 raw 文本（仅产生一次 "Completely unresolved type degraded to Object"
  噪音诊断）；泛型调用类型实参（`TypeToken.of<T>()`）会被前端**剥除** → 标记一律用字符串实参形态。
- `System.Func<T>` 参数 → IR `Supplier<T>`（TS 非法）且体内调用变 `factory.get()`；可选参数（`= null`）
  会被前端拆成重载包装（与 Task 23 重载改名冲突）→ 工厂参数用**必选** `System.Func<T>`，IR 层把
  合成参数类型改写为 `() => T`、调用点 `_factory_T.get()` 改写为 `_factory_T()`。
- raw 方法体内的 C# lambda 经前端变为 `() -> new Widget()`（Java 箭头），TS 非法且无既有改写 →
  IR 层做 `->` → `=>` 文本改写（跳字符串/字符字面量；TS 无 `->` 运算符，安全）。

**架构决策（实现以此为准）：**
1. **语法树层 `TsGenericErasureRewriter`（CSharpSyntaxRewriter，注入 SemanticModel）** 在
   `SingleFileTsEmitPass` 重载 rewriter（1b）之后装配（需有效语义模型；两者都改写并重建编译）：
   - `default(T)`/裸 `default`（ConvertedType 为类型参数，且非 class 约束——class 约束前端已产
     `null`）→ `csDefault(TypeToken.of("T"))` + 诊断；
   - `typeof(T)` → `TypeToken.of("T")`、`typeof(T).Name` → `TypeToken.nameOf("T")`（+ 诊断）；
     具体类型 → 不动（IR 层改类字面量）；
   - `x is T`（IsExpression，右操作数解析为类型参数）→ `TypeToken.isInstance("T", x)` + 诊断；
     `x is T t`（声明模式）→ 诊断 + 保留（登记限制）；
   - `new T()`（ObjectCreation 目标为类型参数，方法体上下文）→ `_factory_{T}()` 调用 +
     方法声明追加必选参数 `System.Func<T> _factory_{T}`；调用点（语义模型解析 IMethodSymbol.
     OriginalDefinition 查登记表）追加工厂实参：类型实参为具体类 → `() => new X()`（复用实参语法
     TypeSyntax）；为类型参数且调用方自带该工厂参数 → 透传 `_factory_{T}`；否则 → 诊断 +
     `csDefault()`（返回 any，可赋值任意函数类型）；构造器体/字段初始化器内的 `new T()` →
     诊断 + 保留（登记限制）。
   - 类型参数归属：方法级与类级类型参数均可在方法级注入工厂参数。
2. **IR 层 `TsGenericErasureIrRewriter`（JavaSyntaxRewriter，同文件）** 在 `RunTsRewriters`
   （stringRewriter 之后、ctorFactory 之前）装配（Java 路径不进）：
   - raw 文本改写（方法 Body/构造器 Body/字段 Initializer/静态初始化块/JavaRawStatement/
     JavaRawExpression）：`X.class.getName()`/`X.class.getTypeName()` → `mappedX.name`；
     `X.class` → `mappedX`（用户类/枚举 → SafeClassName 类引用；用户接口 → `TypeToken.of("IX")` +
     诊断（Task 25 细化）；BCL 表：String→String、Integer/Short/Byte/Double/Float→Number、
     Long→BigInt、Boolean→Boolean、Character→String、Object→Object、
     CSharpList/ArrayList/List→Array；未命中 → `TypeToken.of("X")` + 诊断）；
     `instanceof {T}`（T 为方法/类类型参数）→ `TypeToken.isInstance("T", expr)`；
     `({T})({...})` / `({T[]})({...})` 类型参数强转 → `({...} as {T})`（括号配平扫描，
     仅命中类型参数名 + 单元内类型名 + BCL 表名，避免误伤调用）；`->` → `=>`（lambda 箭头）；
     `_factory_{T}.get()` → `_factory_{T}()`（仅合成工厂参数名）。
   - 结构化节点同步：`JavaMemberAccessExpression{MemberName="class"}` → 类引用标识符；
     `JavaMethodCallExpression{MethodName="getName"/"getTypeName"}` 目标为类字面量 → `.name`；
     `JavaInstanceOfExpression.Type` 为类型参数 → `TypeToken.isInstance("T", expr)` 结构化调用；
     `JavaParameter.Type` 为 `Supplier<T>` 且参数名为 `_factory_{T}` → `() => T`。
   - 登记 compat 符号（`TypeToken`/`csDefault`）供发射器生成
     `@csharptypescript/compat` 导入（RunTsRewriters 合并 CompatSymbols）。
3. **约束 `where T : new()/struct/class`**：共享前端已移除 + 诊断，本任务只做工厂参数联动，不再重复。
4. **已知限制（登记）**：`x is T t` 声明模式（诊断 + 保留）；构造器体/字段初始化器 `new T()`；
   `typeof(T)` 运行时仅知类型参数名（M4 TypeToken 显式传递细化）；`csDefault` 对 struct 约束
   返回 null（C# 为零值结构体）；`Number.name` ≠ C# `Int32`（BCL 类字面量名差异）；
   `new X()` 工厂实参遇 Task 23 私有构造器类（TS2540）；`(List<T>)x` 泛型实例化强转未覆盖；
   泛型数组 `new T[n]` 运行时类参数机制（`_cs2j_T: Class<T>`）不在本任务范围（既有缺口）。

- [x] **Step 2: 写失败测试**（`TsGenericErasureRewriterTests`，spec §5.4）

1. `default(int)`→`0`、`default(bool)`→`false`、`default(string)`→`null`（类型特定，无 csDefault 标记）；
2. `default(T)`（无约束）→ `csDefault(TypeToken.of("T"))` + 诊断；`where T : class` → `null`（不改写）；
3. `new T()` → `_factory_T: () => T` 参数 + 体内 `_factory_T()`，调用点 `A<Widget>()` → `a(() => new Widget())`；
   类型参数实参透传 `b(_factory_T)`；不可解析 → 诊断 + `csDefault()`；
4. `typeof(T)` → `TypeToken.of("T")` + 诊断；`typeof(T).Name` → `TypeToken.nameOf("T")`；
5. `typeof(Widget)` → `Widget`、`typeof(Widget).Name` → `Widget.name`；接口 → TypeToken + 诊断；
6. `x is T` → `TypeToken.isInstance("T", x)` + 诊断；
7. `(T)x` → `(x as T)`；`(Widget)x` → `(x as Widget)`（具体类强转一并修复）；
8. 代表性产出过 L3 `tsc --noEmit`（`AssertCompilesWithCompat`，stub 追加 TypeToken/csDefault）。

- [x] **Step 3: 实现**（`TsGenericErasureRewriter` + `TsGenericErasureIrRewriter` + 管线装配）

管线：`SingleFileTsEmitPass.Execute` 在重载重建后追加泛型擦除 rewriter + 重建；`RunTsRewriters`
追加 IR 层 rewriter（stringRewriter 之后、ctorFactory 之前），CompatSymbols 合并；
`TsConversionTestBase` 兼容库 stub 追加 `TypeToken`/`csDefault`。

- [x] **Step 4: 运行测试 + Java 零回归 + 提交**

Run: `dotnet test tests/CSharpToTypeScript.Tests/CSharpToTypeScript.Tests.csproj`
Run: `dotnet test tests/CSharpToJava.Tests/CSharpToJava.Tests.csproj`（Java 零回归）
Expected: 全绿（TS 新增用例全过 + L3；Java 2423 零回归）。

**实现注记（2026-08-07，与 Step 1 决策的偏差/实测坑）：**
- ① 语法层 `Invocation` 工具函数曾用 `SyntaxFactory.IdentifierName("TypeToken.of")` 构建
  点分调用——IdentifierName 含点不是结构化 MemberAccess，前端会把整个标识符 camelCase 成
  `typeToken.of`（探针复现）。改为 `IdentifierPath` 逐段建 `MemberAccessExpression` 链。
- ② `semanticModel.GetTypeInfo(creation.Type)` 对 `new T()` 返回 **null**（探针验证），
  预扫描 `CollectFactoryNeeds` 与 `VisitObjectCreationExpression` 均须改用
  `GetTypeInfo(creation)`（整表达式，返回 TypeParameter）。
- ③ `new Box<Widget>().Make()` 的 T 属**含类型**类型参数：`ResolveTypeArgSymbol` 原只查
  方法级 TypeArguments（长度 0）→ 无法解析。补含类型 `callee.ContainingType.TypeArguments`
  查表，`() => new Widget()` 用符号显示名回退（调用点语法无类型实参可复用）。
- ④ 1c 重写后方法已带工厂参数，**原调用点按重写后签名绑定失败**
  （`this.A<T>()` → OverloadResolutionFailure，裸 `A<T>()` 却能解析——Roslyn 差异）。
  `VisitInvocationExpression`/透传闭包预扫描均回退 `CandidateSymbols` 首方法符号。
- ⑤ 前端 lambda 消歧对工厂 lambda 实参包 `(Supplier<X>)` 目标类型强转（构造泛型方法
  实参 `Func<Widget>` ≠ 定义 `Func<T>`，`HasOverloadWithDifferentFunctionalParam` 误判），
  IR 层 `TryStripSupplierLambdaCast` 剥离仅后随 lambda 的 `(Supplier<X>)` 前缀（注意
  `() ->` 括号组与箭头间有空白，需跳过）。
- ⑥ 实例方法未限定调用不注入 `this.`（Task 22 登记限制）→ L3 样本统一
  `this.A<Widget>(new Widget())` 显式形态。
- ⑦ compat 符号登记：语法层 `TypeToken.of/nameOf/isInstance`、`csDefault` 使用点置位
  `context.TsCompatSymbols`，`RunTsRewriters` 合并进发射器 CompatSymbols 生成
  `import { TypeToken, csDefault } from "@csharptypescript/compat"`。
- 验收结果：TS 362/362 全绿（349 基线 + 13 新增，含 L3 tsc），Java 2423/2423 零回归。

## Task 25: TsInterfaceProbeRewriter（接口运行时检查，spec §2.6.3）

- **Files:** `Rewriters/TsInterfaceProbeRewriter.cs`、`TsInterfaceProbeRewriterTests.cs`
- **要点：** 每接口生成 `is$IXxx` 探针函数（必需方法存在性检查 + 继承链合并）；`is/as` 调用点改写；实现类构造器注入 `CsRuntime.markImplements` 标记（依赖 M4 兼容库，M3 可先用占位实现）；纯属性接口诊断。

- [x] **Task 25 完成（2026-08-07）**

**实现注记（2026-08-07，与设计决策的偏差/实测坑）：**
- ① 探针函数 `export function is$IXxx(v: unknown): boolean`：`CsRuntime.isMarked(v, "IX")`
  快路径 + `typeof v === "object" && v !== null` 守卫 + `typeof (v as IX).m === "function"`
  结构检查；方法名用 `TsNaming.SafeIdentifier`（IR 名即发射器最终名，无需重复 TS 方法名
  映射逻辑——曾拟 `nameof(IFoo.m)` 形态，TS 无 nameof 运算符，弃用）；继承链
  （ExtendedTypes 递归）方法合并去重；泛型接口探针名用裸名 `is$IRepo`、结构检查强转
  `(v as IRepo<any>)`；纯属性/无方法接口 → 仅 `return CsRuntime.isMarked(...)` +
  "structural probe" 诊断。
- ② 调用点改写（raw 文本扫描 + 结构化 instanceof）：`{expr} instanceof {Iface}` →
  `is$Iface({expr})`（限定名 `Outer.IFoo` 按末段匹配）；`(Iface)({expr})` 接口强转 →
  `({expr} as {Iface})`（泛型实参 `<String>` 归一化 `<string>`）；**as 三元式整体改写**：
  `(x instanceof IFoo ? (IFoo)(x) : null)` → `(is$IFoo(x) ? (x as IFoo) : null) as IFoo`
  ——strict 下 `: null` 分支类型不符，整体再 `as` 回目标类型（C# `as` 运行时仍可返回
  null，非空性为编译期声明；`Interface_Sample_Compiles_L3` 验证 tsc）。
- ③ 实现类构造器注入 `CsRuntime.markImplements(this, "IFoo")`（多接口逗号分隔）；无构造器
  类合成 `constructor() { mark; }`；Task 23 工厂化后的私有空构造器同样注入（工厂内
  `new C()` 自动经构造器标记）。
- ④ **实测坑：raw 文本扫描在 instanceof 位置发起匹配，左侧表达式前缀已被复制进输出缓冲**
  ——需 `sb.Length -= i - spanStart` 回退、`i = spanStart + consumed` 前进（直接
  `i += consumed` 会吞掉 `;`/`?` 等后缀字符，探针复现）。
- ⑤ 探针登记进 `context.TsInterfaceProbes`，发射器在类型声明后追加；`CsRuntime` 登记
  `TsCompatSymbols` 生成 compat 导入；`TsConversionTestBase` 桩追加 CsRuntime
  （d.ts + js）。
- ⑥ 探针/标记使接口产物依赖 compat 模块：9 个既有接口发射测试的 `AssertCompiles` 改
  `AssertCompilesWithCompat`。
- ⑦ 已知限制（登记）：switch 模式变量 `case IFoo f:`（前端产出 `(x instanceof IFoo f)`
  Java 16 形态）不改写（Task 27 模式匹配处理）。
- 验收结果：TS 372/372 全绿（362 基线 + 10 新增，含 L3 tsc），Java 2423/2423 零回归；
  `CSharpToJava.ConversionTests` 37 失败为基线既有（stash 干净树复现，与 Task 25 无关）。

## Task 26: TsEqualityRewriter（相等语义，spec §2.6.4）

- **Files:** `Rewriters/TsEqualityRewriter.cs`、`TsEqualityRewriterTests.cs`
- **要点：** record/struct 与覆写 Equals 的类的 `==`/`.Equals()` 调用点 → `CsObject.equals`；原始类型/字符串 `==` → `===`；`ReferenceEquals` → `===`；`GetHashCode` → `hashCode()`。
- [x] **Task 26 完成（2026-08-07）**

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
