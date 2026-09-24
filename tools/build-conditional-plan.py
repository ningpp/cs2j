from pathlib import Path

root = Path(r"d:/code/cs2j")
validation = root / "tools/conditional-plan-validation"
plan = root / "docs/superpowers/plans/2026-09-24-conditional-directive-strip.md"


def code(path: str, language: str = "csharp") -> str:
    text = (validation / path).read_text(encoding="utf-8").rstrip()
    return f"```{language}\n{text}\n```\n"


parts: list[str] = []
parts.append(r'''# Conditional Directive Stripper Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 提供独立的 C#→C# 预处理器：按选定符号集固化条件分支，彻底移除 `#if/#elif/#else/#endif/#define/#undef`，可选移除 `#region/#endregion`，并保持其余源码、换行和 UTF-8 BOM。

**Architecture:** Core 使用 Roslyn 4.12.0 解析并求值预处理指令，再以 `CSharpSyntaxRewriter.VisitToken` 过滤目标 trivia，最后重解析并验证 token 流不变。目录模式以轻量 XML 方式解析最近祖先 `.csproj`、`Directory.Build.props`、`DefineConstants` 和 TFM；CLI 负责文件树复制、同目录临时文件原子替换、诊断/统计以及退出码。

**Tech Stack:** .NET 10、Microsoft.CodeAnalysis.CSharp 4.12.0、CommandLineParser 2.9.1、xUnit 2.9.3。

**权威设计：** `docs/superpowers/specs/2026-09-24-conditional-directive-strip-design.md`

---

## 执行前必须遵守的已核实约定

1. 当前仓库已存在该功能的早期实现。本计划中的 `Modify` 文件必须以附录给出的完整内容整体替换；Task 4 会把旧的 `Program.StripConditionalAsync` 拆到独立且可测试的 `StripConditionalCommand`。不要并存两份 `StripConditionalOptions` 或两份 handler。
2. `tests/CSharpToJava.Tests/CSharpToJava.Tests.csproj` 已同时引用 Core 和 CLI；Core 与 CLI 的 csproj 均已通过 `InternalsVisibleTo("CSharpToJava.Tests")` 开放 `internal`，无需修改项目文件。
3. CLI 使用的是 `CommandLineParser` 2.9.1（命名空间 `CommandLine`），不是 `System.CommandLine` 或 `CommandLine.Utils`。
4. 单文件模式严格按 spec §5：只使用重复 `--define` 与文件内 `#define/#undef`，不向上读取 `.csproj`。目录/项目模式才调用 `ProjectSymbolResolver`。
5. `--strict` 的契约在本 verb 内明确实现：Error 始终返回 1；Warning 默认返回 0，仅 `--strict` 返回 1。不要复制当前 `eliminate-goto` 对 Warning 无条件返回 1 的缺陷。
6. `DisabledBranchesRemoved` 的可执行定义是“删除的 `DisabledTextTrivia` 块数”，不是语义分支数。`DirectivesRemoved` 是删除的 directive trivia 数；例如 `#define/#if/#else/#endif` 为 4，非 5。
7. 本轮已用仓库实际 Roslyn 4.12.0 验证：`#if A == B` 合法；真正非法表达式 `#if A + B` 产生 CS1025；EOF 例输出保留前一行换行；LF/CRLF/CR/混合换行期望见附录 A；非激活 `#warning/#error/#pragma/#line/#nullable/#region` 都是 `IsActive == false` 的结构化 directive，必须随禁用分支删除。
8. 本轮已用本机 .NET SDK 10 的 `Microsoft.NET.Sdk.BeforeCommon.targets` 核对 TFM 规则：现代 .NET 还定义裸 `NETCOREAPP`；.NET Framework `_OR_GREATER` 的真实拼写是 `NET48_OR_GREATER`（不是 `NETFRAMEWORK48_OR_GREATER`）。附录 C 按 SDK 行为实现。
9. CommandLineParser 2.9.1 的 `Parser.Default` 会拒绝重复 `--define`；必须设置 `ParserSettings.AllowMultiInstance = true`。本轮已实测 `--define A --define B` 解析为两个值。

## 文件责任图

- `src/CSharpToJava.Core/ConditionalDirectiveStrip/ConditionalDirectiveStripModels.cs`：公开结果、诊断、选项及单文件统计模型。
- `src/CSharpToJava.Core/ConditionalDirectiveStrip/ConditionalDirectiveStripper.cs`：Roslyn 求值、trivia 删除、后置验证。
- `src/CSharpToJava.Core/ConditionalDirectiveStrip/SourceTextIo.cs`：UTF-8/BOM 读写和同目录临时文件原子替换。
- `src/CSharpToJava.Core/ConditionalDirectiveStrip/ProjectSymbolResolver.cs`：项目/props 符号与 TFM 符号合成。
- `src/CSharpToJava.CLI/StripConditionalCommand.cs`：完整 verb options、单文件/目录 handler、复制、缓存、统计和退出码。
- `src/CSharpToJava.CLI/Program.cs`：只保留 parser 注册与到 `StripConditionalCommand.RunAsync` 的分派。
- `tests/CSharpToJava.Tests/ConditionalDirectiveStripTests.cs`、`SourceTextIoTests.cs`、`ProjectSymbolResolverTests.cs`、`StripConditionalCliTests.cs`：分别覆盖 Core、IO、符号解析和 CLI 端到端。

---

### Task 1: Core 模型与 stripper 主体（TDD）

**Files:**
- Modify: `tests/CSharpToJava.Tests/ConditionalDirectiveStripTests.cs`
- Modify: `src/CSharpToJava.Core/ConditionalDirectiveStrip/ConditionalDirectiveStripModels.cs`
- Modify: `src/CSharpToJava.Core/ConditionalDirectiveStrip/ConditionalDirectiveStripper.cs`

- [ ] **Step 1: 先用附录 A 完整替换 Core 测试**

附录 A 覆盖 spec §3/§4/§7：基础分支、嵌套四组合、全部表达式、符号大小写、文件内 define/undef、全部位置矩阵、EOF、空分支、字符串/注释、禁用垃圾、所有保留指令、非激活非条件指令、8 类畸形/非法输入、`#r/#load/shebang` 普通 `.cs` 阻断、LF/CRLF/CR/混合换行、`Changed`、精确统计以及 token 等价。

- [ ] **Step 2: 运行测试并确认先失败**

Run:

```powershell
dotnet test tests/CSharpToJava.Tests/CSharpToJava.Tests.csproj --filter FullyQualifiedName~ConditionalDirectiveStripTests --no-restore
```

Expected: FAIL；当前实现至少会在 `InactiveNonConditionalDirectives_AreRemovedWithTheirBranch` 失败（非激活 `#warning/#pragma/#line/#nullable` 泄漏，非激活 `#error` 触发后置校验失败）。若测试意外全绿，先确认测试文件确已按附录 A 整体替换。

- [ ] **Step 3: 用附录 B 完整替换模型和 stripper**

关键点：`HasTargets` 只调用已定义的 `IsTargetTrivia`；所有 `!directive.IsActive` 的指令均删除；`BadDirectiveTrivia` 不在保留白名单中，但原始树任何 Error 都会在重写前阻断并原样返回。

- [ ] **Step 4: 运行定向测试并确认全绿**

Run:

```powershell
dotnet test tests/CSharpToJava.Tests/CSharpToJava.Tests.csproj --filter FullyQualifiedName~ConditionalDirectiveStripTests --no-restore
```

Expected: PASS，0 failed。

- [ ] **Step 5: Commit**

```powershell
git add tests/CSharpToJava.Tests/ConditionalDirectiveStripTests.cs src/CSharpToJava.Core/ConditionalDirectiveStrip/ConditionalDirectiveStripModels.cs src/CSharpToJava.Core/ConditionalDirectiveStrip/ConditionalDirectiveStripper.cs
git commit -m "fix: harden conditional directive stripping"
```

### Task 2: BOM/编码与原子写 IO（TDD）

**Files:**
- Modify: `tests/CSharpToJava.Tests/SourceTextIoTests.cs`
- Modify: `src/CSharpToJava.Core/ConditionalDirectiveStrip/SourceTextIo.cs`

- [ ] **Step 1: 用下列完整代码替换测试文件**

''')
parts.append(code("SourceTextIoTests.cs"))
parts.append(r'''
- [ ] **Step 2: 运行测试并确认先失败**

Run:

```powershell
dotnet test tests/CSharpToJava.Tests/CSharpToJava.Tests.csproj --filter FullyQualifiedName~SourceTextIoTests --no-restore
```

Expected: FAIL with CS0117，因为旧实现没有 `WriteAtomicallyAsync`。

- [ ] **Step 3: 用下列完整代码替换实现文件**

''')
parts.append(code("SourceTextIo.cs"))
parts.append(r'''
- [ ] **Step 4: 运行定向测试并确认全绿**

```powershell
dotnet test tests/CSharpToJava.Tests/CSharpToJava.Tests.csproj --filter FullyQualifiedName~SourceTextIoTests --no-restore
```

Expected: PASS，0 failed。`File.Replace` 的源临时文件与目标位于同一目录，替换前不删除目标；`finally` 清理失败残留。

- [ ] **Step 5: Commit**

```powershell
git add tests/CSharpToJava.Tests/SourceTextIoTests.cs src/CSharpToJava.Core/ConditionalDirectiveStrip/SourceTextIo.cs
git commit -m "feat: add atomic source writes"
```

### Task 3: ProjectSymbolResolver（TDD）

**Files:**
- Modify: `tests/CSharpToJava.Tests/ProjectSymbolResolverTests.cs`
- Modify: `src/CSharpToJava.Core/ConditionalDirectiveStrip/ProjectSymbolResolver.cs`

- [ ] **Step 1: 用附录 C 的测试代码完整替换测试文件**

覆盖：Configuration 的 `And`/`Or`、裸值替换/`$(DefineConstants)` 追加、props 父→子、未知宏 Warning、最近祖先项目、旧式 MSBuild XML namespace、extra defines 修剪与大小写、Configuration 条件 TFM、net6/netcoreapp2.0/netstandard2.0/net48/平台 TFM、多目标默认与显式 override、无项目显式 TFM、未知 TFM 与 TFM 宏 Warning。

- [ ] **Step 2: 运行测试并确认先失败**

```powershell
dotnet test tests/CSharpToJava.Tests/CSharpToJava.Tests.csproj --filter FullyQualifiedName~ProjectSymbolResolverTests --no-restore
```

Expected: FAIL；旧实现会在条件化 TFM、显式 override、`netcoreapp` 上界、现代 .NET 裸 `NETCOREAPP`、平台 TFM或 XML namespace 用例中失败。

- [ ] **Step 3: 用附录 C 的实现代码完整替换 resolver**

显式 `--tfm` 是权威 override，即使不在项目 `TargetFrameworks` 中也直接尝试合成；无法识别时返回空框架符号并产生 Warning，绝不静默回退到项目第一个 TFM。没有项目且显式给出 TFM 时也合成该 TFM；CLI 单文件模式不会调用 resolver，因此仍满足 spec §5 的“单文件仅 `--define`”。

- [ ] **Step 4: 运行定向测试并确认全绿**

```powershell
dotnet test tests/CSharpToJava.Tests/CSharpToJava.Tests.csproj --filter FullyQualifiedName~ProjectSymbolResolverTests --no-restore
```

Expected: PASS，0 failed。

- [ ] **Step 5: Commit**

```powershell
git add tests/CSharpToJava.Tests/ProjectSymbolResolverTests.cs src/CSharpToJava.Core/ConditionalDirectiveStrip/ProjectSymbolResolver.cs
git commit -m "fix: align project symbol resolution with SDK"
```

### Task 4: CLI verb `strip-conditional`（TDD）

**Files:**
- Create: `tests/CSharpToJava.Tests/StripConditionalCliTests.cs`
- Create: `src/CSharpToJava.CLI/StripConditionalCommand.cs`
- Modify: `src/CSharpToJava.CLI/Program.cs`

- [ ] **Step 1: 写入附录 D 的完整 CLI 集成测试**

这些测试直接覆盖 `Program.MainImpl` 的重复 `--define` 解析，并通过可注入 `TextWriter` 的 handler 覆盖单文件符号隔离、Warning/`--strict` 退出码、目录递归、BOM、非 `.cs` 字节复制、目标子树跳过、`.csproj` 入口 in-place、resolver Warning 去重、统计输出和 Error 原样透传。

- [ ] **Step 2: 运行测试并确认先失败**

```powershell
dotnet test tests/CSharpToJava.Tests/CSharpToJava.Tests.csproj --filter FullyQualifiedName~StripConditionalCliTests --no-restore
```

Expected: FAIL with CS0103（`StripConditionalCommand` 尚不存在）。不要通过削弱测试绕过失败。

- [ ] **Step 3: 写入附录 D 的完整 `StripConditionalCommand.cs`**

实现约束：
- 单文件：只使用 `--define`，stdout 默认只输出源码；不变/Error 且写文件时做字节复制，变更时用 `WriteAtomicallyAsync`。
- 目录：先把 `.csproj/.sln` 归一化到 `sourceRoot`，再与 `destinationRoot` 比较 in-place；目标位于源内时整棵目标子树跳过；非 `.cs` 原字节复制。
- resolver 以最近 `.csproj` 的完整路径缓存；同一缓存项的 Warning 只输出一次且参与 strict。
- 摘要固定为 `strip-conditional: N scanned, N transformed, N clean, N failed, N directives removed, N disabled blocks removed`。

- [ ] **Step 4: 精确修改 `Program.cs` 注册并删除旧实现**

1. 将 `Parser.Default.ParseArguments<...>` 改为附录 D 的 `using var parser` + `parser.ParseArguments<...>`，以启用 `AllowMultiInstance`。
2. MapResult 使用 `(StripConditionalOptions opts) => StripConditionalCommand.RunAsync(opts)`。
3. 删除旧 `Program.StripConditionalAsync`、仅供它使用的 `EnumerateFilesSkippingRoot`/`IsSameOrBelow`、旧的顶层 `StripConditionalOptions`，避免重复定义。
4. 若 `using CSharpToJava.Core.ConditionalDirectiveStrip;` 删除旧 handler 后不再被 `Program.cs` 使用，则删除该 using。

- [ ] **Step 5: 运行 CLI 测试并确认全绿**

```powershell
dotnet test tests/CSharpToJava.Tests/CSharpToJava.Tests.csproj --filter FullyQualifiedName~StripConditionalCliTests --no-restore
```

Expected: PASS，0 failed。

- [ ] **Step 6: 构建 CLI 并做真实进程冒烟**

```powershell
dotnet build src/CSharpToJava.CLI/CSharpToJava.CLI.csproj --no-restore
$tmp = Join-Path $env:TEMP ("cs2j-strip-smoke-" + [guid]::NewGuid().ToString("N")); New-Item -ItemType Directory -Path $tmp | Out-Null; try { $input = Join-Path $tmp "Input.cs"; $output = Join-Path $tmp "Output.cs"; [IO.File]::WriteAllText($input, "#if A && B`nclass Kept {}`n#else`nclass Removed {}`n#endif`n", [Text.UTF8Encoding]::new($false)); dotnet run --project src/CSharpToJava.CLI/CSharpToJava.CLI.csproj --no-build -- strip-conditional -i $input -o $output --define A --define B; if ($LASTEXITCODE -ne 0) { throw "unexpected exit code $LASTEXITCODE" }; if ([IO.File]::ReadAllText($output) -ne "class Kept {}`n") { throw "unexpected output" } } finally { Remove-Item -Recurse -Force $tmp }
```

Expected: build succeeds；smoke exits 0 and throws nothing。不要用当前 `tests/SampleProject` 作为唯一冒烟输入，因为该项目现有源码不含条件指令。

- [ ] **Step 7: Commit**

```powershell
git add tests/CSharpToJava.Tests/StripConditionalCliTests.cs src/CSharpToJava.CLI/StripConditionalCommand.cs src/CSharpToJava.CLI/Program.cs
git commit -m "fix: complete strip-conditional CLI behavior"
```

### Task 5: README 同步与全量验证

**Files:**
- Modify: `README.md`

- [ ] **Step 1: 用下列完整段落替换现有 `Strip conditional compilation directives` 小节，并在 CLI options 区加入同名表格**

````markdown
### Strip conditional compilation directives (C# -> C#)

Evaluates and removes `#if/#elif/#else/#endif/#define/#undef` and disabled branches, optionally removes `#region/#endregion`, and preserves UTF-8 BOM and original line endings.

```bash
# Single file; stdout when -o is omitted. Only --define and in-file #define/#undef supply symbols.
dotnet run --project src/CSharpToJava.CLI/CSharpToJava.CLI.csproj -- strip-conditional -i Input.cs -o Output.cs --define FEATURE_A --define FEATURE_B

# Directory/project/solution; non-.cs files are copied byte-for-byte.
dotnet run --project src/CSharpToJava.CLI/CSharpToJava.CLI.csproj -- strip-conditional -s ./src -d ./clean --configuration Release --tfm net8.0 --define FEATURE_A --remove-regions
```

Directory mode resolves symbols per file from the nearest ancestor `.csproj`, parent-to-child `Directory.Build.props`, the selected target framework, and repeated `--define` values. A bare `<DefineConstants>` replaces earlier values; a value containing `$(DefineConstants)` appends. Malformed files are reported and copied unchanged. Errors always return exit code 1; warnings return 1 only with `--strict`.
````

在 `CLI Commands & Options` 中加入：

```markdown
### `strip-conditional` — Conditional compilation cleanup

| Option | Description |
|--------|-------------|
| `-i, --input` | Input `.cs` file; mutually exclusive with directory mode |
| `-o, --output` | Output `.cs` file; omit for stdout |
| `-s, --source` | Source directory, `.csproj`, or `.sln` |
| `-d, --destination` | Destination directory; required with `--source` |
| `--define` | Extra preprocessor symbol; repeat the option for multiple symbols |
| `--configuration` | Configuration for lightweight project property evaluation (default: `Release`) |
| `--tfm` | Explicit target framework override |
| `--remove-regions` | Also remove `#region/#endregion` |
| `--strict` | Return 1 when any warning is emitted |
| `-v, --verbose` | Show per-file progress and single-file statistics |
```

- [ ] **Step 2: 运行定向测试、全量测试和构建**

```powershell
dotnet test tests/CSharpToJava.Tests/CSharpToJava.Tests.csproj --filter "FullyQualifiedName~ConditionalDirectiveStripTests|FullyQualifiedName~SourceTextIoTests|FullyQualifiedName~ProjectSymbolResolverTests|FullyQualifiedName~StripConditionalCliTests" --no-restore
dotnet test tests/CSharpToJava.Tests/CSharpToJava.Tests.csproj --no-restore
dotnet build CSharpToJavaConverter.slnx --no-restore
```

Expected: 三条命令 exit code 0；允许仓库既有 Warning，但不得新增编译 Error 或测试失败。

- [ ] **Step 3: 做最终静态门禁**

```powershell
$forbidden = @('Not' + 'ImplementedException', 'TO' + 'DO', 'T' + 'BD', '实际实现' + '见', '简化' + '实现', '近' + '似')
$hits = Select-String -Path docs/superpowers/plans/2026-09-24-conditional-directive-strip.md -Pattern $forbidden
if ($hits) { $hits; throw 'plan contains forbidden placeholder text' }
git diff --check
git status --short
```

Expected: `Select-String` 无匹配；`git diff --check` exit code 0；`git status --short` 只显示本计划列出的实现、测试和 README 文件。

- [ ] **Step 4: Commit**

```powershell
git add README.md
git commit -m "docs: document strip-conditional usage"
```

---

## 附录 A：Task 1 完整测试代码

`tests/CSharpToJava.Tests/ConditionalDirectiveStripTests.cs`：

''')
parts.append(code("ConditionalDirectiveStripTests.cs"))
parts.append(r'''
## 附录 B：Task 1 完整实现代码

`src/CSharpToJava.Core/ConditionalDirectiveStrip/ConditionalDirectiveStripModels.cs`：

''')
parts.append(code("ConditionalDirectiveStripModels.cs"))
parts.append(r'''
`src/CSharpToJava.Core/ConditionalDirectiveStrip/ConditionalDirectiveStripper.cs`：

''')
parts.append(code("ConditionalDirectiveStripper.cs"))
parts.append(r'''
## 附录 C：Task 3 完整测试与实现代码

`tests/CSharpToJava.Tests/ProjectSymbolResolverTests.cs`：

''')
parts.append(code("ProjectSymbolResolverTests.cs"))
parts.append(r'''
`src/CSharpToJava.Core/ConditionalDirectiveStrip/ProjectSymbolResolver.cs`：

''')
parts.append(code("ProjectSymbolResolver.cs"))
parts.append(r'''
## 附录 D：Task 4 完整 CLI 测试、实现与注册修改

`tests/CSharpToJava.Tests/StripConditionalCliTests.cs`：

''')
parts.append(code("StripConditionalCliTests.cs"))
parts.append(r'''
`src/CSharpToJava.CLI/StripConditionalCommand.cs`：

''')
parts.append(code("StripConditionalCommand.cs"))
parts.append(r'''
`src/CSharpToJava.CLI/Program.cs` 的注册修改（其余 verb 与方法保持原样）：

```csharp
internal static async Task<int> MainImpl(string[] args)
{
    using var parser = new Parser(settings => settings.AllowMultiInstance = true);
    return await parser.ParseArguments<ConvertOptions, ConvertProjectOptions, AnalyzeOptions, EliminateGotoOptions, MakeReadOnlyOptions, UnsignedTypeOptions, GenericPreprocessOptions, StripDefaultOptions, LocalFunctionLiftOptions, TupleToStructVerbOptions, StripConditionalOptions>(args)
        .MapResult(
            (ConvertOptions opts) => ConvertFile(opts),
            (ConvertProjectOptions opts) => ConvertProject(opts),
            (AnalyzeOptions opts) => AnalyzeProject(opts),
            (EliminateGotoOptions opts) => EliminateGoto(opts),
            (MakeReadOnlyOptions opts) => MakeReadonly(opts),
            (UnsignedTypeOptions opts) => UnsignedType(opts),
            (GenericPreprocessOptions opts) => GenericPreprocess(opts),
            (StripDefaultOptions opts) => StripDefault(opts),
            (LocalFunctionLiftOptions opts) => LocalFunctionLiftAsync(opts),
            (TupleToStructVerbOptions opts) => TupleToStructVerbAsync(opts),
            (StripConditionalOptions opts) => StripConditionalCommand.RunAsync(opts),
            errs => Task.FromResult(1));
}
```

同时删除 `Program.cs` 中旧的以下完整声明，不能保留重复实现：

```text
private static Task<int> StripConditionalAsync(StripConditionalOptions opts)
private static IEnumerable<string> EnumerateFilesSkippingRoot(...)
private static bool IsSameOrBelow(...)
class StripConditionalOptions
```

---

## Spec 覆盖审查与差异清单

| Spec 要求 | 计划落点 | 自动化门禁 |
|---|---|---|
| §3 条件族删除、define/undef 生效 | Task 1 / 附录 A、B | `IfElseAndElif_*`、`DefineAndUndef_*` |
| §3 region 默认保留/可选删除 | Task 1 | `Regions_ArePreservedByDefaultAndOptionallyRemoved` |
| §3 pragma/line/nullable/pragma checksum 保留 | Task 1 | `ActiveNonConditionalDirectives_ArePreservedExactly` |
| §3 激活 warning 保留并报告 | Task 1 + Task 4 | Core 单 Warning；CLI strict 0/1 |
| §3 激活 error、非法指令、普通 `.cs` 的 r/load/shebang 阻断 | Task 1 | `MalformedOrScriptOnlyInput_IsBlocked` |
| §3 非激活非条件指令不得泄漏 | Task 1 | `InactiveNonConditionalDirectives_AreRemovedWithTheirBranch` |
| §4 全部语法位置 | Task 1 | `PositionCases` 11 组 + 文件首/EOF/空文件用例 |
| §4 嵌套四组合 | Task 1 | `Nested_AllTruthCombinations` 4 行 theory |
| §4 表达式与大小写 | Task 1 | `ConditionalExpressionOperators_*`、`SymbolEquality_*`、`Symbols_*` |
| §4 禁用垃圾、字符串/注释、空分支 | Task 1 | 对应精确输出与不变断言 |
| §4 LF/CRLF/CR/混合换行、BOM | Task 1、2、4 | 精确字符串与字节断言 |
| §4 八类畸形/非法场景 | Task 1 | §4.9 七类（非法表达式改为 `A + B`）+ §3 `#foo`；另测 r/load/shebang |
| §4/§7 后置零错、零残留、token 等价 | Task 1 | `AssertSuccessful` 与生产 `VerifyOutput` |
| §5 DefineConstants、props 顺序、Configuration | Task 3 | `ProjectSymbolResolverTests` |
| §5 TFM 合成、默认首项、显式覆盖、未知 Warning | Task 3 | 四族 TFM、平台后缀、override 与 unknown 用例 |
| §5 单文件仅 CLI + 文件内符号 | Task 4 | `SingleFile_UsesOnlyCliAndInFileSymbols` |
| §6 统计模型 | Task 1 + Task 4 | 单文件精确计数、目录精确摘要 |
| §8 参数、目录/项目入口、复制、in-place、目标子树、strict | Task 4 | CLI 集成测试逐项覆盖 |
| §9 build + 全量测试门 | Task 5 | 三条可复制命令 |

### Spec 有、原计划无（本版已补齐）

- §3：非激活 `#warning/#error/#pragma/#line/#nullable` 必须随分支删除；`#r/#load/shebang` 在普通 `.cs` 的失败路径；`#pragma checksum` 保留。
- §4：成员、using/attribute、对象/集合初始化器、switch arm、lambda 参数、CR-only、嵌套四组合、保留分支注释、显式 token 等价。
- §5：单文件不读取项目；Configuration 同时作用于 TFM 属性；显式 TFM 的确定语义；真实 SDK 的现代 .NET/NETCOREAPP 兼容符号和 netcoreapp 上界。
- §6/§8：完整目录统计、resolver Warning 输出与 strict、`.csproj/.sln` in-place、目标子树跳过、非 `.cs` 字节复制、可验证的原子替换。
- §9：CLI parser/handler 端到端测试与真实含条件指令的冒烟。

修复后 **spec 有、计划无：无**。

### 原计划有、spec 无或与 spec 冲突（本版处理）

- 原计划称“9 类畸形输入”，实际 §4.9 只列 7 类；本版覆盖该 7 类并加入 §3 的非法指令，共 8 类，不再虚报 9 类。
- 原计划把 `#if A == B` 当非法。Roslyn 4.12.0 实测零诊断，因此本版把它列为合法表达式回归，并用 `#if A + B` 验证非法表达式阻断。这按 spec §2“求值交给 Roslyn”的总原则解决 §4.9 示例错误。
- 原计划单文件调用 `ProjectSymbolResolver`，违反 §5 的单文件契约；本版删除该行为。
- 原计划称 CLI 包为 `CommandLine.Utils`；真实依赖为 `CommandLineParser` 2.9.1，本版已纠正。
- 本版新增但不改变产品契约的实现细节只有：独立 `StripConditionalCommand` 测试接缝、同层多个 `.csproj` 时按路径排序后选首个以保证确定性、平台 TFM 后缀按基础 TFM 合成框架符号。

### 本轮实测结论

- 现有定向 Core/IO/resolver 测试：72/72 通过。
- 临时候选完整实现 + 旧回归 + 新增边界/CLI 测试：101/101 通过，0 编译错误。
- Roslyn 4.12.0 探针确认：`A == B` 合法；`A + B` 为 CS1025；EOF/缩进行/CRLF/LF/CR/混合换行输出与附录 A 一致；统计 fixture 为 4 条 directive、1 个 disabled trivia 块。
- CommandLineParser 2.9.1 探针确认：默认 parser 拒绝重复 `--define`；`AllowMultiInstance = true` 后 `--define A --define B` 得到两个值。
- Core/CLI 的 `InternalsVisibleTo`、`net10.0`、Roslyn/CommandLineParser/xUnit 版本及真实 namespace 均已从 csproj 和源码核实。

执行时无需依赖未验证的 Roslyn 输出假设；若目标执行环境的文件系统不支持 `.NET File.Replace`，`SourceTextIoTests.AtomicWrite_CreatesParent_ReplacesDestination_AndLeavesNoTempFile` 必须先失败并阻止提交，不能退回“先删目标再移动”的非原子实现。
''')

plan.write_text("".join(parts), encoding="utf-8", newline="\n")
