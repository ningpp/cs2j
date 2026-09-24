# Conditional Directive Stripper Implementation Plan

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

```csharp
using System.Text;
using CSharpToJava.Core.ConditionalDirectiveStrip;

namespace CSharpToJava.Tests;

public sealed class SourceTextIoTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cs2j-source-io-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task BomFile_ReadsWithoutMarker_AndWritesWithMarker()
    {
        var input = WriteBytes("input.cs", [.. SourceTextIo.Utf8Bom, .. Encoding.UTF8.GetBytes("// 中文\nclass C {}")]);
        var source = await SourceTextIo.ReadAsync(input);
        Assert.True(source.HasBom);
        Assert.Equal("// 中文\nclass C {}", source.Code);

        var output = Path.Combine(_root, "output.cs");
        await SourceTextIo.WriteAsync(output, source.Code, source.HasBom);
        Assert.Equal(await File.ReadAllBytesAsync(input), await File.ReadAllBytesAsync(output));
    }

    [Fact]
    public async Task NoBomUtf8_RoundTripsByteForByte()
    {
        var bytes = Encoding.UTF8.GetBytes("// 中文\r\nclass C {}\n");
        var input = WriteBytes("input.cs", bytes);
        var source = await SourceTextIo.ReadAsync(input);
        Assert.False(source.HasBom);

        var output = Path.Combine(_root, "output.cs");
        await SourceTextIo.WriteAsync(output, source.Code, source.HasBom);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(output));
    }

    [Fact]
    public async Task AtomicWrite_CreatesParent_ReplacesDestination_AndLeavesNoTempFile()
    {
        var output = Path.Combine(_root, "nested", "output.cs");
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        await File.WriteAllTextAsync(output, "old", new UTF8Encoding(false));

        await SourceTextIo.WriteAtomicallyAsync(output, "new", hasBom: false);

        Assert.Equal("new", await File.ReadAllTextAsync(output));
        Assert.Empty(Directory.EnumerateFiles(Path.GetDirectoryName(output)!, "*.tmp"));
    }

    [Fact]
    public async Task AtomicWrite_NewBomFile_HasExactlyOneBom()
    {
        var output = Path.Combine(_root, "new", "output.cs");
        await SourceTextIo.WriteAtomicallyAsync(output, "class C {}", hasBom: true);
        byte[] expected = [.. SourceTextIo.Utf8Bom, .. Encoding.UTF8.GetBytes("class C {}")];
        var actual = await File.ReadAllBytesAsync(output);
        Assert.Equal(expected, actual);
    }

    private string WriteBytes(string relativePath, byte[] bytes)
    {
        var path = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
```

- [ ] **Step 2: 运行测试并确认先失败**

Run:

```powershell
dotnet test tests/CSharpToJava.Tests/CSharpToJava.Tests.csproj --filter FullyQualifiedName~SourceTextIoTests --no-restore
```

Expected: FAIL with CS0117，因为旧实现没有 `WriteAtomicallyAsync`。

- [ ] **Step 3: 用下列完整代码替换实现文件**

```csharp
using System.Text;

namespace CSharpToJava.Core.ConditionalDirectiveStrip;

public static class SourceTextIo
{
    public static readonly byte[] Utf8Bom = [0xEF, 0xBB, 0xBF];

    public sealed record SourceText(string Code, bool HasBom);

    public static async Task<SourceText> ReadAsync(string path)
    {
        var bytes = await File.ReadAllBytesAsync(path);
        var hasBom = bytes is [0xEF, 0xBB, 0xBF, ..];
        var code = Encoding.UTF8.GetString(hasBom ? bytes[3..] : bytes);
        return new SourceText(code, hasBom);
    }

    public static async Task WriteAsync(string path, string code, bool hasBom)
    {
        var payload = Encoding.UTF8.GetBytes(code);
        if (hasBom)
        {
            payload = [.. Utf8Bom, .. payload];
        }

        await File.WriteAllBytesAsync(path, payload);
    }

    public static async Task WriteAtomicallyAsync(string path, string code, bool hasBom)
    {
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new ArgumentException("Output path has no parent directory.", nameof(path));
        Directory.CreateDirectory(directory);
        var tempPath = Path.Combine(directory, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");

        try
        {
            await WriteAsync(tempPath, code, hasBom);
            if (File.Exists(fullPath))
            {
                File.Replace(tempPath, fullPath, destinationBackupFileName: null);
            }
            else
            {
                File.Move(tempPath, fullPath);
            }
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }
}
```

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

```csharp
using CSharpToJava.Core.ConditionalDirectiveStrip;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Tests;

public class ConditionalDirectiveStripTests
{
    private static ConditionalDirectiveStripResult Strip(
        string source,
        string[]? symbols = null,
        bool removeRegions = false) =>
        new ConditionalDirectiveStripper().Strip(
            source,
            new ConditionalDirectiveStripOptions
            {
                Symbols = symbols ?? Array.Empty<string>(),
                RemoveRegions = removeRegions,
            });

    private static string Output(string source, string[]? symbols = null, bool removeRegions = false) =>
        Strip(source, symbols, removeRegions).OutputCode;

    private static void AssertSuccessful(
        string source,
        ConditionalDirectiveStripResult result,
        string[]? symbols = null,
        bool removeRegions = false)
    {
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Severity == ConditionalDirectiveSeverity.Error);
        var parseOptions = CSharpParseOptions.Default
            .WithLanguageVersion(LanguageVersion.Latest)
            .WithPreprocessorSymbols(symbols ?? Array.Empty<string>());
        var inputRoot = CSharpSyntaxTree.ParseText(source, parseOptions).GetCompilationUnitRoot();
        var outputRoot = CSharpSyntaxTree.ParseText(result.OutputCode, parseOptions).GetCompilationUnitRoot();
        Assert.DoesNotContain(outputRoot.SyntaxTree.GetDiagnostics(), diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);

        foreach (var trivia in outputRoot.DescendantTrivia(descendIntoTrivia: true))
        {
            Assert.False(trivia.IsKind(SyntaxKind.DisabledTextTrivia), $"disabled text left: {trivia}");
            if (trivia.GetStructure() is DirectiveTriviaSyntax directive)
            {
                Assert.False(
                    ConditionalDirectiveStripper.IsRemovedDirective(directive.Kind(), removeRegions),
                    $"target directive left: {directive}");
            }
        }

        var expectedTokens = inputRoot.DescendantTokens().Select(token => (token.RawKind, token.Text));
        var actualTokens = outputRoot.DescendantTokens().Select(token => (token.RawKind, token.Text));
        Assert.Equal(expectedTokens, actualTokens);
    }

    [Fact]
    public void NoDirectives_IsByteForByteUnchanged()
    {
        const string source = "class C { void M() {} }";
        var result = Strip(source);
        Assert.False(result.Changed);
        Assert.Equal(source, result.OutputCode);
        Assert.Equal(0, result.Statistics.DirectivesRemoved);
        Assert.Equal(0, result.Statistics.DisabledBranchesRemoved);
    }

    [Fact]
    public void IfElseAndElif_SelectExpectedBranches()
    {
        Assert.Equal("X();\n", Output("#if A\nX();\n#endif\n", ["A"]));
        Assert.Equal(string.Empty, Output("#if A\nX();\n#endif\n"));
        Assert.Equal("Y();\n", Output("#if A\nX();\n#else\nY();\n#endif\n"));
        Assert.Equal("B();\n", Output("#if A\nA();\n#elif B\nB();\n#elif C\nC();\n#else\nD();\n#endif\n", ["B"]));
        Assert.Equal("D();\n", Output("#if A\nA();\n#elif B\nB();\n#else\nD();\n#endif\n"));
    }

    [Theory]
    [InlineData(true, true, "OUTER_TRUE();\nINNER_TRUE();\n")]
    [InlineData(true, false, "OUTER_TRUE();\nINNER_FALSE();\n")]
    [InlineData(false, true, "OUTER_FALSE();\nDISABLED_INNER_TRUE();\n")]
    [InlineData(false, false, "OUTER_FALSE();\nDISABLED_INNER_FALSE();\n")]
    public void Nested_AllTruthCombinations(bool outer, bool inner, string expected)
    {
        const string source = "#if OUTER\nOUTER_TRUE();\n#if INNER\nINNER_TRUE();\n#else\nINNER_FALSE();\n#endif\n#else\nOUTER_FALSE();\n#if INNER\nDISABLED_INNER_TRUE();\n#else\nDISABLED_INNER_FALSE();\n#endif\n#endif\n";
        var symbols = new List<string>();
        if (outer) symbols.Add("OUTER");
        if (inner) symbols.Add("INNER");
        var result = Strip(source, symbols.ToArray());
        Assert.Equal(expected, result.OutputCode);
        AssertSuccessful(source, result, symbols.ToArray());
    }

    [Fact]
    public void ConditionalExpressionOperators_AreEvaluatedByRoslyn()
    {
        Assert.Equal("X();\n", Output("#if !A\nX();\n#endif\n"));
        Assert.Equal("X();\n", Output("#if (A || B) && !C\nX();\n#endif\n", ["B"]));
        Assert.Equal("X();\n", Output("#if A == true\nX();\n#endif\n", ["A"]));
        Assert.Equal("X();\n", Output("#if A != true\nX();\n#endif\n"));
        Assert.Equal("X();\n", Output("#if true\nX();\n#endif\n"));
        Assert.Equal(string.Empty, Output("#if false\nX();\n#endif\n"));
        Assert.Equal("X();\n", Output("#  if  A\nX();\n#  endif\n", ["A"]));
    }

    [Fact]
    public void SymbolEquality_IsLegalRoslynSyntax()
    {
        const string source = "#if A == B\nX();\n#endif\n";
        Assert.Equal("X();\n", Output(source));
        Assert.Equal(string.Empty, Output(source, ["A"]));
    }

    [Fact]
    public void Symbols_AreCaseSensitive()
    {
        Assert.Equal(string.Empty, Output("#if foo\nX();\n#endif\n", ["FOO"]));
    }

    [Fact]
    public void DefineAndUndef_AffectOnlyFollowingConditions()
    {
        Assert.Equal("X();\n", Output("#define A\n#if A\nX();\n#endif\n"));
        Assert.Equal(string.Empty, Output("#if A\nX();\n#endif\n#define A\n"));
        Assert.Equal("Y();\n", Output("#undef A\n#if A\nX();\n#else\nY();\n#endif\n", ["A"]));
        Assert.Equal("X();\n", Output("#define A\n#if A\nX();\n#endif\n", ["A"]));
    }

    public static TheoryData<string, string, string> PositionCases => new()
    {
        { "#if A\nusing Kept.Namespace;\n#else\nusing Removed.Namespace;\n#endif\nclass C {}", "Kept", "Removed" },
        { "#if A\n[Kept]\n#else\n[Removed]\n#endif\nclass C {}", "Kept", "Removed" },
        { "class C {\n#if A\nvoid Kept() {}\n#else\nvoid Removed() {}\n#endif\n}", "Kept", "Removed" },
        { "class C { public\n#if A\nstatic\n#else\nvirtual\n#endif\nvoid M() {} }", "static", "virtual" },
        { "var value = new List<\n#if A\nint\n#else\nstring\n#endif\n>();", "int", "string" },
        { "class C { void M() {\n#if A\nKept();\n#else\nRemoved();\n#endif\n} }", "Kept", "Removed" },
        { "F(\n#if A\nKept\n#else\nRemoved\n#endif\n);", "Kept", "Removed" },
        { "var value = new C {\n#if A\nP = Kept\n#else\nP = Removed\n#endif\n};", "Kept", "Removed" },
        { "var value = new[] {\n#if A\nKept\n#else\nRemoved\n#endif\n};", "Kept", "Removed" },
        { "var value = x switch {\n#if A\n1 => Kept,\n#else\n_ => Removed,\n#endif\n};", "Kept", "Removed" },
        { "var value = (\n#if A\nint Kept\n#else\nstring Removed\n#endif\n) => 0;", "Kept", "Removed" },
    };

    [Theory]
    [MemberData(nameof(PositionCases))]
    public void Directives_AreHandledAtEverySyntaxPosition(string source, string kept, string removed)
    {
        var result = Strip(source, ["A"]);
        var tokens = CSharpSyntaxTree.ParseText(result.OutputCode).GetCompilationUnitRoot()
            .DescendantTokens().Select(token => token.Text).ToArray();
        Assert.Contains(kept, tokens);
        Assert.DoesNotContain(removed, tokens);
        AssertSuccessful(source, result, ["A"]);
    }

    [Fact]
    public void EofDirectiveWithoutTrailingNewline_PreservesPrecedingNewline()
    {
        const string source = "class C { }\n#if A\n#endif";
        var result = Strip(source);
        Assert.Equal("class C { }\n", result.OutputCode);
        AssertSuccessful(source, result);
    }

    [Fact]
    public void EmptyAndDirectiveOnlyInputs_HaveNoResidualDirectiveLines()
    {
        Assert.Equal(string.Empty, Output("#define A\n"));
        Assert.Equal(string.Empty, Output("#if A\nclass C { }\n#endif\n"));
        Assert.Equal("A();\nB();\n", Output("A();\n    #if A\n    #endif\nB();\n"));
    }

    [Fact]
    public void DirectiveLookalikesInStringsAndComments_AreUnchanged()
    {
        const string source = "var a = \"#if A\";\nvar b = @\"x\n#endif\";\n// #if B\n/* #endif */\n/// <summary>#if C</summary>\nclass C {}";
        var result = Strip(source, ["A"]);
        Assert.False(result.Changed);
        Assert.Equal(source, result.OutputCode);
    }

    [Fact]
    public void DisabledGarbageAndNestedDirectives_AreRemoved()
    {
        const string source = "OK();\n#if A\n@@@ nonsense ]]]\n#if B\nX();\n#elif C\nY();\n#endif\n#endif\n";
        var result = Strip(source);
        Assert.Equal("OK();\n", result.OutputCode);
        AssertSuccessful(source, result);
    }

    [Theory]
    [InlineData("#warning hidden")]
    [InlineData("#error hidden")]
    [InlineData("#pragma warning disable CS0168")]
    [InlineData("#pragma checksum \"x.cs\" \"{406EA660-64CF-4C82-B6F0-42D48172A799}\" \"001122\"")]
    [InlineData("#line hidden")]
    [InlineData("#nullable enable")]
    [InlineData("#region R\n#endregion")]
    public void InactiveNonConditionalDirectives_AreRemovedWithTheirBranch(string directive)
    {
        var source = $"#if A\n{directive}\n#endif\n";
        var result = Strip(source);
        Assert.True(result.Changed);
        Assert.Equal(string.Empty, result.OutputCode);
        AssertSuccessful(source, result);
    }

    [Fact]
    public void ActiveNonConditionalDirectives_ArePreservedExactly()
    {
        const string source = "#pragma warning disable CS1591\n#pragma checksum \"x.cs\" \"{406EA660-64CF-4C82-B6F0-42D48172A799}\" \"001122\"\n#line hidden\n#line default\n#nullable enable\nclass C { }";
        var result = Strip(source);
        Assert.False(result.Changed);
        Assert.Equal(source, result.OutputCode);
    }

    [Fact]
    public void Regions_ArePreservedByDefaultAndOptionallyRemoved()
    {
        const string source = "#region R\nclass C { }\n#endregion\n";
        var kept = Strip(source);
        Assert.False(kept.Changed);
        Assert.Equal(source, kept.OutputCode);

        var removed = Strip(source, removeRegions: true);
        Assert.True(removed.Changed);
        Assert.Equal("class C { }\n", removed.OutputCode);
        AssertSuccessful(source, removed, removeRegions: true);
    }

    [Fact]
    public void ActiveWarning_IsPreservedAndProducesExactlyOneWarning()
    {
        const string source = "#warning fixme\nclass C { }";
        var result = Strip(source);
        Assert.False(result.Changed);
        Assert.Equal(source, result.OutputCode);
        var warning = Assert.Single(result.Diagnostics);
        Assert.Equal(ConditionalDirectiveSeverity.Warning, warning.Severity);
        Assert.Equal(1, warning.Line);
    }

    public static TheoryData<string> MalformedCases => new()
    {
        "#endif\n",
        "#if A\nX();\n",
        "#if A\n#else\n#elif B\n#endif\n",
        "class C { }\n#define A\n",
        "class C { #if A }\n",
        "#if A + B\nX();\n#endif\n",
        "#error nope\n",
        "#wibble\nclass C { }\n",
        "#r \"x.dll\"\n",
        "#load \"x.csx\"\n",
        "#!/usr/bin/env dotnet\nclass C { }\n",
    };

    [Theory]
    [MemberData(nameof(MalformedCases))]
    public void MalformedOrScriptOnlyInput_IsBlocked(string source)
    {
        var result = Strip(source);
        Assert.False(result.Changed);
        Assert.Equal(source, result.OutputCode);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Severity == ConditionalDirectiveSeverity.Error);
        Assert.Equal(0, result.Statistics.DirectivesRemoved);
        Assert.Equal(0, result.Statistics.DisabledBranchesRemoved);
    }

    [Theory]
    [InlineData("#if A\nX();\n#endif\n", "X();\n")]
    [InlineData("#if A\r\nX();\r\n#endif\r\n", "X();\r\n")]
    [InlineData("#if A\rX();\r#endif\r", "X();\r")]
    [InlineData("#if A\r\nX();\nY();\r#endif\r\n", "X();\nY();\r")]
    public void LineEndings_ArePreserved(string source, string expected)
    {
        Assert.Equal(expected, Output(source, ["A"]));
    }

    [Fact]
    public void Statistics_CountRemovedDirectiveTriviaAndDisabledTextBlocks()
    {
        var result = Strip("#define A\n#if A\nX();\n#else\nY();\n#endif\n");
        Assert.True(result.Changed);
        Assert.Equal(4, result.Statistics.DirectivesRemoved);
        Assert.Equal(1, result.Statistics.DisabledBranchesRemoved);
    }
}
```

## 附录 B：Task 1 完整实现代码

`src/CSharpToJava.Core/ConditionalDirectiveStrip/ConditionalDirectiveStripModels.cs`：

```csharp
namespace CSharpToJava.Core.ConditionalDirectiveStrip;

public enum ConditionalDirectiveSeverity { Warning, Error }

public sealed record ConditionalDirectiveDiagnostic(
    ConditionalDirectiveSeverity Severity,
    string Message,
    int? Line = null);

public sealed class ConditionalDirectiveStatistics
{
    public int DirectivesRemoved;
    public int DisabledBranchesRemoved;
}

public sealed record ConditionalDirectiveStripResult(
    string OutputCode,
    bool Changed,
    IReadOnlyList<ConditionalDirectiveDiagnostic> Diagnostics,
    ConditionalDirectiveStatistics Statistics);

public sealed record ConditionalDirectiveStripOptions
{
    public bool Verbose { get; init; }
    public bool Strict { get; init; }
    public bool RemoveRegions { get; init; }
    public IReadOnlyList<string> Symbols { get; init; } = Array.Empty<string>();
}
```

`src/CSharpToJava.Core/ConditionalDirectiveStrip/ConditionalDirectiveStripper.cs`：

```csharp
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.ConditionalDirectiveStrip;

public sealed class ConditionalDirectiveStripper
{
    public ConditionalDirectiveStripResult Strip(string sourceCode, ConditionalDirectiveStripOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(sourceCode);
        options ??= new ConditionalDirectiveStripOptions();
        var diagnostics = new List<ConditionalDirectiveDiagnostic>();
        var symbols = options.Symbols
            .Where(symbol => !string.IsNullOrWhiteSpace(symbol))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var parseOptions = CSharpParseOptions.Default
            .WithLanguageVersion(LanguageVersion.Latest)
            .WithPreprocessorSymbols(symbols);
        var tree = CSharpSyntaxTree.ParseText(sourceCode, parseOptions);
        var root = tree.GetCompilationUnitRoot();

        foreach (var error in tree.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error))
        {
            diagnostics.Add(new ConditionalDirectiveDiagnostic(
                ConditionalDirectiveSeverity.Error,
                $"{error.Id}: {error.GetMessage()}",
                error.Location.GetLineSpan().StartLinePosition.Line + 1));
        }

        if (diagnostics.Count > 0)
        {
            return new ConditionalDirectiveStripResult(sourceCode, false, diagnostics, new());
        }

        foreach (var warning in tree.GetDiagnostics().Where(diagnostic =>
                     diagnostic.Severity == DiagnosticSeverity.Warning && diagnostic.Id == "CS1030"))
        {
            diagnostics.Add(new ConditionalDirectiveDiagnostic(
                ConditionalDirectiveSeverity.Warning,
                $"#warning directive preserved: {warning.GetMessage()}",
                warning.Location.GetLineSpan().StartLinePosition.Line + 1));
        }

        if (!HasTargets(root, options.RemoveRegions))
        {
            return new ConditionalDirectiveStripResult(sourceCode, false, diagnostics, new());
        }

        var rewriter = new RemovingRewriter(options.RemoveRegions);
        var rewritten = (CompilationUnitSyntax)rewriter.Visit(root)!;
        var output = rewritten.ToFullString();

        if (!VerifyOutput(output, parseOptions, root, options.RemoveRegions))
        {
            diagnostics.Add(new ConditionalDirectiveDiagnostic(
                ConditionalDirectiveSeverity.Error,
                "internal post-verification failed (output would not parse cleanly or token stream changed); file left unchanged"));
            return new ConditionalDirectiveStripResult(sourceCode, false, diagnostics, new());
        }

        return new ConditionalDirectiveStripResult(
            output,
            !string.Equals(sourceCode, output, StringComparison.Ordinal),
            diagnostics,
            new ConditionalDirectiveStatistics
            {
                DirectivesRemoved = rewriter.DirectivesRemoved,
                DisabledBranchesRemoved = rewriter.DisabledBranchesRemoved,
            });
    }

    internal static bool IsRemovedDirective(SyntaxKind kind, bool removeRegions = false) => kind switch
    {
        SyntaxKind.RegionDirectiveTrivia or SyntaxKind.EndRegionDirectiveTrivia => removeRegions,
        SyntaxKind.LineDirectiveTrivia
            or SyntaxKind.PragmaWarningDirectiveTrivia
            or SyntaxKind.PragmaChecksumDirectiveTrivia
            or SyntaxKind.NullableDirectiveTrivia
            or SyntaxKind.ErrorDirectiveTrivia
            or SyntaxKind.WarningDirectiveTrivia
            or SyntaxKind.ReferenceDirectiveTrivia
            or SyntaxKind.LoadDirectiveTrivia
            or SyntaxKind.ShebangDirectiveTrivia => false,
        _ => true,
    };

    internal static bool IsTargetTrivia(SyntaxTrivia trivia, bool removeRegions)
    {
        if (trivia.IsKind(SyntaxKind.DisabledTextTrivia))
        {
            return true;
        }

        if (trivia.GetStructure() is not DirectiveTriviaSyntax directive)
        {
            return false;
        }

        if (!directive.IsActive)
        {
            return true;
        }

        return IsRemovedDirective(directive.Kind(), removeRegions);
    }

    private static bool HasTargets(CompilationUnitSyntax root, bool removeRegions)
    {
        foreach (var token in root.DescendantTokens())
        {
            foreach (var trivia in token.LeadingTrivia)
            {
                if (IsTargetTrivia(trivia, removeRegions))
                {
                    return true;
                }
            }

            foreach (var trivia in token.TrailingTrivia)
            {
                if (IsTargetTrivia(trivia, removeRegions))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool VerifyOutput(
        string output,
        CSharpParseOptions parseOptions,
        CompilationUnitSyntax originalRoot,
        bool removeRegions)
    {
        var outputRoot = CSharpSyntaxTree.ParseText(output, parseOptions).GetCompilationUnitRoot();
        if (outputRoot.SyntaxTree.GetDiagnostics().Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error))
        {
            return false;
        }

        if (HasTargets(outputRoot, removeRegions))
        {
            return false;
        }

        return TokenSequenceEqual(originalRoot, outputRoot);
    }

    private static bool TokenSequenceEqual(CompilationUnitSyntax left, CompilationUnitSyntax right)
    {
        using var leftTokens = left.DescendantTokens().GetEnumerator();
        using var rightTokens = right.DescendantTokens().GetEnumerator();
        while (true)
        {
            var hasLeft = leftTokens.MoveNext();
            var hasRight = rightTokens.MoveNext();
            if (hasLeft != hasRight)
            {
                return false;
            }

            if (!hasLeft)
            {
                return true;
            }

            if (leftTokens.Current.RawKind != rightTokens.Current.RawKind ||
                leftTokens.Current.Text != rightTokens.Current.Text)
            {
                return false;
            }
        }
    }

    private sealed class RemovingRewriter(bool removeRegions) : CSharpSyntaxRewriter
    {
        internal int DirectivesRemoved;
        internal int DisabledBranchesRemoved;

        public override SyntaxToken VisitToken(SyntaxToken token)
        {
            var visited = base.VisitToken(token);
            var leading = Filter(visited.LeadingTrivia);
            var trailing = Filter(visited.TrailingTrivia);
            return leading.Count == visited.LeadingTrivia.Count && trailing.Count == visited.TrailingTrivia.Count
                ? visited
                : visited.WithLeadingTrivia(leading).WithTrailingTrivia(trailing);
        }

        private SyntaxTriviaList Filter(SyntaxTriviaList triviaList)
        {
            if (triviaList.Count == 0)
            {
                return triviaList;
            }

            List<SyntaxTrivia>? result = null;
            for (var index = 0; index < triviaList.Count; index++)
            {
                var trivia = triviaList[index];
                if (!IsTargetTrivia(trivia, removeRegions))
                {
                    result?.Add(trivia);
                    continue;
                }

                result ??= new List<SyntaxTrivia>(triviaList.Take(index));
                if (trivia.IsKind(SyntaxKind.DisabledTextTrivia))
                {
                    DisabledBranchesRemoved++;
                }
                else
                {
                    DirectivesRemoved++;
                }

                while (result.Count > 0 && result[^1].IsKind(SyntaxKind.WhitespaceTrivia))
                {
                    result.RemoveAt(result.Count - 1);
                }
            }

            return result == null ? triviaList : SyntaxFactory.TriviaList(result);
        }
    }
}
```

## 附录 C：Task 3 完整测试与实现代码

`tests/CSharpToJava.Tests/ProjectSymbolResolverTests.cs`：

```csharp
using CSharpToJava.Core.ConditionalDirectiveStrip;

namespace CSharpToJava.Tests;

public sealed class ProjectSymbolResolverTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cs2j-symbols-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void ConfigurationConditions_AndOr_AreApplied()
    {
        var file = WriteFile("App/Code.cs");
        WriteFile("App/App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup Condition="'$(Configuration)' == 'Debug' Or '$(Configuration)' == 'Development'">
                <DefineConstants>DEBUG_ONLY</DefineConstants>
              </PropertyGroup>
              <PropertyGroup Condition="'$(Configuration)' != 'Debug' And '$(Configuration)' != 'Development'">
                <DefineConstants>RELEASE_ONLY</DefineConstants>
              </PropertyGroup>
            </Project>
            """);

        var debug = ProjectSymbolResolver.ResolveForFile(file, "Debug");
        Assert.Contains("DEBUG_ONLY", debug.Symbols);
        Assert.DoesNotContain("RELEASE_ONLY", debug.Symbols);

        var release = ProjectSymbolResolver.ResolveForFile(file, "Release");
        Assert.Contains("RELEASE_ONLY", release.Symbols);
        Assert.DoesNotContain("DEBUG_ONLY", release.Symbols);
    }

    [Fact]
    public void DefineConstants_AppendAndReplace_AreOrdered()
    {
        var file = WriteFile("App/Code.cs");
        WriteFile("Directory.Build.props", """
            <Project><PropertyGroup><DefineConstants>ROOT</DefineConstants></PropertyGroup></Project>
            """);
        WriteFile("App/Directory.Build.props", """
            <Project><PropertyGroup><DefineConstants>$(DefineConstants);CHILD</DefineConstants></PropertyGroup></Project>
            """);
        WriteFile("App/App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><DefineConstants>REPLACED</DefineConstants></PropertyGroup>
              <PropertyGroup><DefineConstants>$(DefineConstants);APPENDED</DefineConstants></PropertyGroup>
            </Project>
            """);

        var result = ProjectSymbolResolver.ResolveForFile(file);
        Assert.DoesNotContain("ROOT", result.Symbols);
        Assert.DoesNotContain("CHILD", result.Symbols);
        Assert.Contains("REPLACED", result.Symbols);
        Assert.Contains("APPENDED", result.Symbols);
    }

    [Fact]
    public void DirectoryBuildProps_AreAppliedParentToChild()
    {
        var file = WriteFile("Sub/App/Code.cs");
        WriteFile("Directory.Build.props", """
            <Project><PropertyGroup><DefineConstants>ROOT</DefineConstants></PropertyGroup></Project>
            """);
        WriteFile("Sub/Directory.Build.props", """
            <Project><PropertyGroup><DefineConstants>$(DefineConstants);SUB</DefineConstants></PropertyGroup></Project>
            """);
        WriteFile("Sub/App/App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><DefineConstants>$(DefineConstants);PROJECT</DefineConstants></PropertyGroup></Project>
            """);

        var result = ProjectSymbolResolver.ResolveForFile(file);
        Assert.Equal(["ROOT", "SUB", "PROJECT"], result.Symbols);
    }

    [Fact]
    public void UnknownMacros_AreSkippedWithWarning()
    {
        var file = WriteFile("App/Code.cs");
        WriteFile("App/App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><DefineConstants>$(Other);REAL</DefineConstants></PropertyGroup></Project>
            """);
        var result = ProjectSymbolResolver.ResolveForFile(file);
        Assert.Equal(["REAL"], result.Symbols);
        Assert.Single(result.Warnings);
        Assert.Contains("$(Other)", result.Warnings[0], StringComparison.Ordinal);
    }

    [Fact]
    public void NearestProject_Wins()
    {
        WriteFile("Outer/App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><DefineConstants>OUTER</DefineConstants></PropertyGroup></Project>
            """);
        WriteFile("Outer/Inner/Inner.csproj", """
            <Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><DefineConstants>INNER</DefineConstants></PropertyGroup></Project>
            """);
        var file = WriteFile("Outer/Inner/Code.cs");
        var result = ProjectSymbolResolver.ResolveForFile(file);
        Assert.Contains("INNER", result.Symbols);
        Assert.DoesNotContain("OUTER", result.Symbols);
    }

    [Fact]
    public void OldStyleMsBuildXmlNamespace_IsSupported()
    {
        var file = WriteFile("Legacy/Code.cs");
        WriteFile("Legacy/Legacy.csproj", """
            <Project xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
              <PropertyGroup>
                <TargetFramework>net48</TargetFramework>
                <DefineConstants>LEGACY</DefineConstants>
              </PropertyGroup>
            </Project>
            """);
        var result = ProjectSymbolResolver.ResolveForFile(file);
        Assert.Contains("LEGACY", result.Symbols);
        Assert.Contains("NET48", result.Symbols);
    }

    [Fact]
    public void ExtraDefines_AreTrimmedMergedAndCaseSensitive()
    {
        var file = WriteFile("Loose/Code.cs");
        var result = ProjectSymbolResolver.ResolveForFile(file, extraDefines: [" foo ", "FOO", "foo"]);
        Assert.Equal(["foo", "FOO"], result.Symbols);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void Configuration_AlsoSelectsTargetFramework()
    {
        var file = WriteFile("App/Code.cs");
        WriteFile("App/App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup Condition="'$(Configuration)' == 'Debug'"><TargetFramework>net6.0</TargetFramework></PropertyGroup>
              <PropertyGroup Condition="'$(Configuration)' == 'Release'"><TargetFramework>net8.0</TargetFramework></PropertyGroup>
            </Project>
            """);

        var debug = ProjectSymbolResolver.ResolveForFile(file, "Debug");
        Assert.Contains("NET6_0", debug.Symbols);
        Assert.DoesNotContain("NET8_0", debug.Symbols);
    }

    [Fact]
    public void ModernNet_SynthesizesExactCurrentLowerAndCompatibilitySymbols()
    {
        var symbols = Synthesize("net6.0");
        Assert.Contains("NET", symbols);
        Assert.Contains("NET6_0", symbols);
        Assert.Contains("NET6_0_OR_GREATER", symbols);
        Assert.Contains("NET5_0_OR_GREATER", symbols);
        Assert.Contains("NETCOREAPP", symbols);
        Assert.Contains("NETCOREAPP3_1_OR_GREATER", symbols);
        Assert.DoesNotContain("NET7_0_OR_GREATER", symbols);
        Assert.DoesNotContain("NETSTANDARD2_0_OR_GREATER", symbols);
        Assert.DoesNotContain("NETFRAMEWORK", symbols);
    }

    [Fact]
    public void NetCoreApp_OrGreaterChainStopsAtTarget()
    {
        var symbols = Synthesize("netcoreapp2.0");
        Assert.Contains("NETCOREAPP", symbols);
        Assert.Contains("NETCOREAPP2_0", symbols);
        Assert.Contains("NETCOREAPP2_0_OR_GREATER", symbols);
        Assert.Contains("NETCOREAPP1_1_OR_GREATER", symbols);
        Assert.DoesNotContain("NETCOREAPP2_1_OR_GREATER", symbols);
        Assert.DoesNotContain("NETCOREAPP3_1_OR_GREATER", symbols);
    }

    [Fact]
    public void NetStandard_OrGreaterChainStopsAtTarget()
    {
        var symbols = Synthesize("netstandard2.0");
        Assert.Contains("NETSTANDARD", symbols);
        Assert.Contains("NETSTANDARD2_0", symbols);
        Assert.Contains("NETSTANDARD2_0_OR_GREATER", symbols);
        Assert.Contains("NETSTANDARD1_0_OR_GREATER", symbols);
        Assert.DoesNotContain("NETSTANDARD2_1_OR_GREATER", symbols);
    }

    [Fact]
    public void NetFramework_UsesSdkSymbolSpelling()
    {
        var symbols = Synthesize("net48");
        Assert.Contains("NETFRAMEWORK", symbols);
        Assert.Contains("NET48", symbols);
        Assert.Contains("NET48_OR_GREATER", symbols);
        Assert.Contains("NET472_OR_GREATER", symbols);
        Assert.DoesNotContain("NET481_OR_GREATER", symbols);
    }

    [Fact]
    public void PlatformTfm_SynthesizesBaseFrameworkSymbols()
    {
        var symbols = Synthesize("net8.0-windows10.0.19041.0");
        Assert.Contains("NET8_0", symbols);
        Assert.Contains("NET8_0_OR_GREATER", symbols);
    }

    [Fact]
    public void MultiTarget_DefaultsToFirst_ExplicitTfmIsAuthoritative()
    {
        var file = WriteFile("App/Code.cs");
        WriteFile("App/App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFrameworks>netstandard2.0;net6.0</TargetFrameworks></PropertyGroup></Project>
            """);

        var defaultResult = ProjectSymbolResolver.ResolveForFile(file);
        Assert.Contains("NETSTANDARD2_0", defaultResult.Symbols);
        Assert.DoesNotContain("NET6_0", defaultResult.Symbols);

        var overrideResult = ProjectSymbolResolver.ResolveForFile(file, tfm: "net8.0");
        Assert.Contains("NET8_0", overrideResult.Symbols);
        Assert.DoesNotContain("NETSTANDARD2_0", overrideResult.Symbols);
        Assert.Empty(overrideResult.Warnings);
    }

    [Fact]
    public void ExplicitTfm_WorksWithoutProject()
    {
        var file = WriteFile("Loose/Code.cs");
        var result = ProjectSymbolResolver.ResolveForFile(file, tfm: "net8.0", extraDefines: ["EXTRA"]);
        Assert.Contains("NET8_0", result.Symbols);
        Assert.Contains("EXTRA", result.Symbols);
    }

    [Fact]
    public void UnknownTfm_ProducesNoFrameworkSymbolsAndOneWarning()
    {
        var file = WriteFile("App/Code.cs");
        WriteFile("App/App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>unknown</TargetFramework></PropertyGroup></Project>
            """);
        var result = ProjectSymbolResolver.ResolveForFile(file);
        Assert.Empty(result.Symbols);
        var warning = Assert.Single(result.Warnings);
        Assert.Contains("unknown", warning, StringComparison.Ordinal);
    }

    [Fact]
    public void TargetFrameworkMacro_IsSkippedWithWarning()
    {
        var file = WriteFile("App/Code.cs");
        WriteFile("App/App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>$(ComputedTfm)</TargetFramework></PropertyGroup></Project>
            """);
        var result = ProjectSymbolResolver.ResolveForFile(file);
        Assert.Empty(result.Symbols);
        var warning = Assert.Single(result.Warnings);
        Assert.Contains("$(ComputedTfm)", warning, StringComparison.Ordinal);
    }

    private static IReadOnlyList<string> Synthesize(string tfm)
    {
        var symbols = new List<string>();
        Assert.True(ProjectSymbolResolver.TrySynthesizeTfmSymbols(tfm, symbols));
        return symbols;
    }

    private string WriteFile(string relativePath, string content = "class F {}")
    {
        var path = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
```

`src/CSharpToJava.Core/ConditionalDirectiveStrip/ProjectSymbolResolver.cs`：

```csharp
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace CSharpToJava.Core.ConditionalDirectiveStrip;

public sealed record ProjectSymbols(IReadOnlyList<string> Symbols, IReadOnlyList<string> Warnings);

public static class ProjectSymbolResolver
{
    private static readonly string[] NetStandardVersions = ["1.0", "1.1", "1.2", "1.3", "1.4", "1.5", "1.6", "2.0", "2.1"];
    private static readonly string[] NetCoreAppVersions = ["1.0", "1.1", "2.0", "2.1", "2.2", "3.0", "3.1"];
    private static readonly string[] NetFxVersions = ["20", "35", "40", "45", "451", "452", "46", "461", "462", "47", "471", "472", "48", "481"];

    public static ProjectSymbols ResolveForFile(
        string filePath,
        string configuration = "Release",
        string? tfm = null,
        IEnumerable<string>? extraDefines = null)
    {
        var warnings = new List<string>();
        var symbols = new List<string>();
        var project = FindNearestProject(Path.GetDirectoryName(Path.GetFullPath(filePath)));
        var tfms = new List<string>();

        if (project != null)
        {
            foreach (var props in GetAncestorDirectoryBuildProps(project).Reverse())
            {
                ApplyDefineConstants(props, configuration, symbols, warnings);
            }

            ApplyDefineConstants(project, configuration, symbols, warnings);
            tfms = ReadTfms(project, configuration, warnings);
        }

        var chosenTfm = PickTfm(tfms, tfm);
        if (chosenTfm != null && !TrySynthesizeTfmSymbols(chosenTfm, symbols))
        {
            warnings.Add($"unrecognized TFM '{chosenTfm}'");
        }

        if (extraDefines != null)
        {
            symbols.AddRange(extraDefines
                .Where(symbol => !string.IsNullOrWhiteSpace(symbol))
                .Select(symbol => symbol.Trim()));
        }

        return new ProjectSymbols(Dedup(symbols), warnings);
    }

    internal static string? FindNearestProject(string? startDirectory)
    {
        var directory = startDirectory;
        while (!string.IsNullOrEmpty(directory))
        {
            var project = Directory.EnumerateFiles(directory, "*.csproj")
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
            if (project != null)
            {
                return Path.GetFullPath(project);
            }

            var parent = Path.GetDirectoryName(directory);
            if (parent == null || string.Equals(parent, directory, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            directory = parent;
        }

        return null;
    }

    private static IEnumerable<string> GetAncestorDirectoryBuildProps(string projectPath)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(projectPath))!;
        while (!string.IsNullOrEmpty(directory))
        {
            var props = Path.Combine(directory, "Directory.Build.props");
            if (File.Exists(props))
            {
                yield return props;
            }

            var parent = Path.GetDirectoryName(directory);
            if (parent == null || string.Equals(parent, directory, StringComparison.OrdinalIgnoreCase))
            {
                yield break;
            }

            directory = parent;
        }
    }

    private static void ApplyDefineConstants(
        string file,
        string configuration,
        List<string> symbols,
        List<string> warnings)
    {
        var document = LoadDocument(file, warnings);
        if (document?.Root == null)
        {
            return;
        }

        foreach (var group in ChildrenNamed(document.Root, "PropertyGroup"))
        {
            if (!EvaluateCondition(AttributeNamed(group, "Condition"), configuration))
            {
                continue;
            }

            foreach (var element in ChildrenNamed(group, "DefineConstants"))
            {
                if (!EvaluateCondition(AttributeNamed(element, "Condition"), configuration))
                {
                    continue;
                }

                var value = element.Value;
                if (string.IsNullOrWhiteSpace(value))
                {
                    continue;
                }

                var appends = false;
                var parts = new List<string>();
                foreach (var raw in value.Split(';', StringSplitOptions.RemoveEmptyEntries))
                {
                    var part = raw.Trim();
                    if (part.Length == 0)
                    {
                        continue;
                    }

                    if (part.Equals("$(DefineConstants)", StringComparison.OrdinalIgnoreCase))
                    {
                        appends = true;
                        continue;
                    }

                    if (part.Contains("$(", StringComparison.Ordinal))
                    {
                        warnings.Add($"skipped unresolvable macro '{part}' in {file}");
                        continue;
                    }

                    parts.Add(part);
                }

                if (!appends)
                {
                    symbols.Clear();
                }

                symbols.AddRange(parts);
            }
        }
    }

    internal static bool EvaluateCondition(string? condition, string configuration)
    {
        if (string.IsNullOrWhiteSpace(condition))
        {
            return true;
        }

        foreach (var orGroup in Regex.Split(condition, @"\s+or\s+", RegexOptions.IgnoreCase))
        {
            var all = true;
            foreach (var clause in Regex.Split(orGroup, @"\s+and\s+", RegexOptions.IgnoreCase))
            {
                var match = Regex.Match(
                    clause,
                    @"^\s*'?\$\(Configuration\)'?\s*(==|!=)\s*['""]([^'""]*)['""]\s*$",
                    RegexOptions.IgnoreCase);
                if (!match.Success)
                {
                    continue;
                }

                var equal = string.Equals(configuration, match.Groups[2].Value, StringComparison.OrdinalIgnoreCase);
                if ((match.Groups[1].Value == "==") != equal)
                {
                    all = false;
                    break;
                }
            }

            if (all)
            {
                return true;
            }
        }

        return false;
    }

    private static List<string> ReadTfms(string projectPath, string configuration, List<string> warnings)
    {
        var result = new List<string>();
        var document = LoadDocument(projectPath, warnings);
        if (document?.Root == null)
        {
            return result;
        }

        foreach (var group in ChildrenNamed(document.Root, "PropertyGroup"))
        {
            if (!EvaluateCondition(AttributeNamed(group, "Condition"), configuration))
            {
                continue;
            }

            foreach (var element in group.Elements().Where(element =>
                         element.Name.LocalName.Equals("TargetFramework", StringComparison.OrdinalIgnoreCase) ||
                         element.Name.LocalName.Equals("TargetFrameworks", StringComparison.OrdinalIgnoreCase)))
            {
                if (!EvaluateCondition(AttributeNamed(element, "Condition"), configuration))
                {
                    continue;
                }

                var value = element.Value.Trim();
                if (value.Length == 0)
                {
                    continue;
                }

                if (value.Contains("$(", StringComparison.Ordinal))
                {
                    warnings.Add($"skipped unresolvable TFM value '{value}' in {projectPath}");
                    continue;
                }

                result.Clear();
                result.AddRange(value.Split(';', StringSplitOptions.RemoveEmptyEntries)
                    .Select(item => item.Trim())
                    .Where(item => item.Length > 0));
            }
        }

        return result;
    }

    internal static string? PickTfm(IReadOnlyList<string> tfms, string? requested)
    {
        if (!string.IsNullOrWhiteSpace(requested))
        {
            return requested.Trim();
        }

        return tfms.FirstOrDefault();
    }

    internal static bool TrySynthesizeTfmSymbols(string tfm, List<string> symbols)
    {
        var value = tfm.Trim();
        const string platformSuffix = @"(?:-[A-Za-z0-9.]+)?$";

        var netStandard = Regex.Match(value, @"^netstandard(\d+)\.(\d+)" + platformSuffix, RegexOptions.IgnoreCase);
        if (netStandard.Success)
        {
            var target = $"{netStandard.Groups[1].Value}.{netStandard.Groups[2].Value}";
            symbols.Add("NETSTANDARD");
            symbols.Add($"NETSTANDARD{VersionToSymbol(target)}");
            foreach (var lower in NetStandardVersions.Where(version => CompareVersions(version, target) <= 0))
            {
                symbols.Add($"NETSTANDARD{VersionToSymbol(lower)}_OR_GREATER");
            }

            return true;
        }

        var netCoreApp = Regex.Match(value, @"^netcoreapp(\d+)\.(\d+)" + platformSuffix, RegexOptions.IgnoreCase);
        if (netCoreApp.Success)
        {
            var target = $"{netCoreApp.Groups[1].Value}.{netCoreApp.Groups[2].Value}";
            symbols.Add("NETCOREAPP");
            symbols.Add($"NETCOREAPP{VersionToSymbol(target)}");
            foreach (var lower in NetCoreAppVersions.Where(version => CompareVersions(version, target) <= 0))
            {
                symbols.Add($"NETCOREAPP{VersionToSymbol(lower)}_OR_GREATER");
            }

            return true;
        }

        var netModern = Regex.Match(value, @"^net(\d+)\.(\d+)" + platformSuffix, RegexOptions.IgnoreCase);
        if (netModern.Success && int.Parse(netModern.Groups[1].Value) >= 5)
        {
            var major = int.Parse(netModern.Groups[1].Value);
            var minor = int.Parse(netModern.Groups[2].Value);
            symbols.Add("NET");
            symbols.Add($"NET{major}_{minor}");
            symbols.Add("NETCOREAPP");
            symbols.Add($"NET{major}_{minor}_OR_GREATER");
            for (var lowerMajor = 5; lowerMajor < major; lowerMajor++)
            {
                symbols.Add($"NET{lowerMajor}_0_OR_GREATER");
            }

            foreach (var lower in NetCoreAppVersions)
            {
                symbols.Add($"NETCOREAPP{VersionToSymbol(lower)}_OR_GREATER");
            }

            return true;
        }

        var netFramework = Regex.Match(value, @"^net(\d{2,3})" + platformSuffix, RegexOptions.IgnoreCase);
        if (netFramework.Success)
        {
            var code = netFramework.Groups[1].Value;
            symbols.Add("NETFRAMEWORK");
            symbols.Add($"NET{code}");
            foreach (var lower in NetFxVersions.Where(version => CompareNetFxVersions(version, code) <= 0))
            {
                symbols.Add($"NET{lower}_OR_GREATER");
            }

            return true;
        }

        return false;
    }

    private static XDocument? LoadDocument(string path, List<string> warnings)
    {
        try
        {
            return XDocument.Load(path);
        }
        catch (Exception exception)
        {
            warnings.Add($"cannot read {path}: {exception.Message}");
            return null;
        }
    }

    private static IEnumerable<XElement> ChildrenNamed(XContainer parent, string localName) =>
        parent.Elements().Where(element => element.Name.LocalName.Equals(localName, StringComparison.OrdinalIgnoreCase));

    private static string? AttributeNamed(XElement element, string localName) =>
        element.Attributes().FirstOrDefault(attribute => attribute.Name.LocalName.Equals(localName, StringComparison.OrdinalIgnoreCase))?.Value;

    private static string VersionToSymbol(string version) => version.Replace('.', '_');

    private static int CompareVersions(string left, string right)
    {
        var leftParts = left.Split('.').Select(int.Parse).ToArray();
        var rightParts = right.Split('.').Select(int.Parse).ToArray();
        for (var index = 0; index < Math.Max(leftParts.Length, rightParts.Length); index++)
        {
            var comparison = (index < leftParts.Length ? leftParts[index] : 0)
                .CompareTo(index < rightParts.Length ? rightParts[index] : 0);
            if (comparison != 0)
            {
                return comparison;
            }
        }

        return 0;
    }

    private static int CompareNetFxVersions(string leftCode, string rightCode) =>
        (ToNetFxTuple(leftCode), ToNetFxTuple(rightCode)) switch
        {
            var (left, right) when left.major != right.major => left.major.CompareTo(right.major),
            var (left, right) when left.minor != right.minor => left.minor.CompareTo(right.minor),
            var (left, right) => left.patch.CompareTo(right.patch),
        };

    private static (int major, int minor, int patch) ToNetFxTuple(string code) => code.Length switch
    {
        2 => (code[0] - '0', code[1] - '0', 0),
        3 => (code[0] - '0', code[1] - '0', code[2] - '0'),
        _ => (0, 0, 0),
    };

    private static List<string> Dedup(IEnumerable<string> symbols)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>();
        foreach (var symbol in symbols)
        {
            if (seen.Add(symbol))
            {
                result.Add(symbol);
            }
        }

        return result;
    }
}
```

## 附录 D：Task 4 完整 CLI 测试、实现与注册修改

`tests/CSharpToJava.Tests/StripConditionalCliTests.cs`：

```csharp
using System.Text;
using CSharpToJava.CLI;
using CSharpToJava.Core.ConditionalDirectiveStrip;

namespace CSharpToJava.Tests;

public sealed class StripConditionalCliTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cs2j-strip-cli-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Parser_AcceptsRepeatedDefineOptions()
    {
        var input = WriteText("repeat/input.cs", "#if A && B\nclass C {}\n#endif\n");
        var output = Path.Combine(_root, "repeat", "output.cs");

        var exitCode = await Program.MainImpl([
            "strip-conditional", "-i", input, "-o", output,
            "--define", "A", "--define", "B",
        ]);

        Assert.Equal(0, exitCode);
        Assert.Equal("class C {}\n", await File.ReadAllTextAsync(output));
    }

    [Fact]
    public async Task SingleFile_UsesOnlyCliAndInFileSymbols()
    {
        WriteText("single/App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><DefineConstants>PROJECT_ONLY</DefineConstants></PropertyGroup>
            </Project>
            """);
        var input = WriteText("single/input.cs", "#if PROJECT_ONLY\nclass C {}\n#endif\n");
        var output = Path.Combine(_root, "single", "output.cs");

        var exitCode = await StripConditionalCommand.RunAsync(new StripConditionalOptions
        {
            Input = input,
            Output = output,
        }, TextWriter.Null, TextWriter.Null);

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, await File.ReadAllTextAsync(output));
    }

    [Fact]
    public async Task Strict_ChangesWarningExitCodeOnly()
    {
        var input = WriteText("warning/input.cs", "#warning fixme\nclass C {}\n");
        var normalOutput = Path.Combine(_root, "warning", "normal.cs");
        var strictOutput = Path.Combine(_root, "warning", "strict.cs");

        var normalExit = await StripConditionalCommand.RunAsync(new StripConditionalOptions
        {
            Input = input,
            Output = normalOutput,
        }, TextWriter.Null, TextWriter.Null);
        var strictExit = await StripConditionalCommand.RunAsync(new StripConditionalOptions
        {
            Input = input,
            Output = strictOutput,
            Strict = true,
        }, TextWriter.Null, TextWriter.Null);

        Assert.Equal(0, normalExit);
        Assert.Equal(1, strictExit);
        Assert.Equal(await File.ReadAllBytesAsync(input), await File.ReadAllBytesAsync(normalOutput));
        Assert.Equal(await File.ReadAllBytesAsync(input), await File.ReadAllBytesAsync(strictOutput));
    }

    [Fact]
    public async Task DirectoryMode_TransformsCopiesPreservesBomSkipsNestedDestinationAndReportsExactTotals()
    {
        var source = Path.Combine(_root, "tree");
        var destination = Path.Combine(source, "out");
        WriteText("tree/App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><DefineConstants>A</DefineConstants></PropertyGroup>
            </Project>
            """);
        WriteBytes("tree/Dirty.cs", [.. SourceTextIo.Utf8Bom, .. Encoding.UTF8.GetBytes("#if A\nclass C {}\n#else\nclass D {}\n#endif\n")]);
        var cleanBytes = Encoding.UTF8.GetBytes("class Clean {}\r\n");
        WriteBytes("tree/Clean.cs", cleanBytes);
        var assetBytes = new byte[] { 0, 1, 2, 255 };
        WriteBytes("tree/asset.bin", assetBytes);

        var stdout = new StringWriter();
        var exitCode = await StripConditionalCommand.RunAsync(new StripConditionalOptions
        {
            Source = source,
            Destination = destination,
        }, stdout, TextWriter.Null);

        Assert.Equal(0, exitCode);
        byte[] expectedDirty = [.. SourceTextIo.Utf8Bom, .. Encoding.UTF8.GetBytes("class C {}\n")];
        var actualDirty = await File.ReadAllBytesAsync(Path.Combine(destination, "Dirty.cs"));
        Assert.Equal(expectedDirty, actualDirty);
        Assert.Equal(cleanBytes, await File.ReadAllBytesAsync(Path.Combine(destination, "Clean.cs")));
        Assert.Equal(assetBytes, await File.ReadAllBytesAsync(Path.Combine(destination, "asset.bin")));
        Assert.False(Directory.Exists(Path.Combine(destination, "out")));
        Assert.Equal(
            "strip-conditional: 2 scanned, 1 transformed, 1 clean, 0 failed, 3 directives removed, 1 disabled blocks removed" + Environment.NewLine,
            stdout.ToString());
    }

    [Fact]
    public async Task ProjectPathInPlace_TransformsNestedFilesWithoutSelfCopy()
    {
        var project = WriteText("in-place/App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><DefineConstants>A</DefineConstants></PropertyGroup>
            </Project>
            """);
        var nested = WriteText("in-place/Sub/Code.cs", "#if A\nclass C {}\n#endif\n");

        var exitCode = await StripConditionalCommand.RunAsync(new StripConditionalOptions
        {
            Source = project,
            Destination = Path.GetDirectoryName(project),
        }, TextWriter.Null, TextWriter.Null);

        Assert.Equal(0, exitCode);
        Assert.Equal("class C {}\n", await File.ReadAllTextAsync(nested));
    }

    [Fact]
    public async Task ResolverWarning_IsWrittenOnce_AndStrictReturnsOne()
    {
        var source = Path.Combine(_root, "resolver-warning");
        var destination = Path.Combine(_root, "resolver-warning-output");
        WriteText("resolver-warning/App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>unknown</TargetFramework></PropertyGroup>
            </Project>
            """);
        WriteText("resolver-warning/A.cs", "class A {}");
        WriteText("resolver-warning/B.cs", "class B {}");
        var stderr = new StringWriter();

        var exitCode = await StripConditionalCommand.RunAsync(new StripConditionalOptions
        {
            Source = source,
            Destination = destination,
            Strict = true,
        }, TextWriter.Null, stderr);

        Assert.Equal(1, exitCode);
        Assert.Equal(1, stderr.ToString().Split("unrecognized TFM", StringSplitOptions.None).Length - 1);
        Assert.Contains("[Warning]", stderr.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task MalformedFile_IsCopiedUnchanged_AndReturnsOne()
    {
        const string malformed = "#if A + B\nclass C {}\n#endif\n";
        var source = Path.Combine(_root, "malformed");
        var destination = Path.Combine(_root, "malformed-output");
        var input = WriteText("malformed/Bad.cs", malformed);
        var stderr = new StringWriter();

        var exitCode = await StripConditionalCommand.RunAsync(new StripConditionalOptions
        {
            Source = source,
            Destination = destination,
        }, TextWriter.Null, stderr);

        Assert.Equal(1, exitCode);
        Assert.Equal(await File.ReadAllBytesAsync(input), await File.ReadAllBytesAsync(Path.Combine(destination, "Bad.cs")));
        Assert.Contains("[Error] Bad.cs:1: CS1025", stderr.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task MissingModeArguments_ReturnOne()
    {
        var stderr = new StringWriter();
        var exitCode = await StripConditionalCommand.RunAsync(new StripConditionalOptions(), TextWriter.Null, stderr);
        Assert.Equal(1, exitCode);
        Assert.Contains("provide (-i [-o]) or (-s <source> -d <destination>)", stderr.ToString(), StringComparison.Ordinal);
    }

    private string WriteText(string relativePath, string content)
    {
        var path = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content, new UTF8Encoding(false));
        return path;
    }

    private string WriteBytes(string relativePath, byte[] content)
    {
        var path = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, content);
        return path;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
```

`src/CSharpToJava.CLI/StripConditionalCommand.cs`：

```csharp
using CommandLine;
using CSharpToJava.Core.ConditionalDirectiveStrip;

namespace CSharpToJava.CLI;

[Verb("strip-conditional", HelpText = "Evaluate and remove C# conditional compilation directives (#if/#elif/#else/#endif/#define/#undef) from C# source")]
internal sealed class StripConditionalOptions
{
    [Option('i', "input", SetName = "file", HelpText = "Input .cs file")]
    public string? Input { get; set; }

    [Option('o', "output", SetName = "file", HelpText = "Output .cs file (default: stdout)")]
    public string? Output { get; set; }

    [Option('s', "source", SetName = "dir", HelpText = "Source directory (or .csproj/.sln: its directory is used as the root)")]
    public string? Source { get; set; }

    [Option('d', "destination", SetName = "dir", HelpText = "Destination directory")]
    public string? Destination { get; set; }

    [Option('v', "verbose", Default = false)]
    public bool Verbose { get; set; }

    [Option("strict", Default = false, HelpText = "Treat warning diagnostics as fatal")]
    public bool Strict { get; set; }

    [Option("define", Required = false, HelpText = "Extra preprocessor symbol (repeatable, highest precedence)")]
    public IEnumerable<string> Define { get; set; } = Enumerable.Empty<string>();

    [Option("configuration", Default = "Release", HelpText = "Configuration used to evaluate project symbols")]
    public string Configuration { get; set; } = "Release";

    [Option("tfm", Required = false, HelpText = "Target framework override for symbol synthesis (for example net8.0)")]
    public string? Tfm { get; set; }

    [Option("remove-regions", Default = false, HelpText = "Also remove #region/#endregion directives")]
    public bool RemoveRegions { get; set; }
}

internal static class StripConditionalCommand
{
    internal static async Task<int> RunAsync(
        StripConditionalOptions options,
        TextWriter? standardOutput = null,
        TextWriter? standardError = null)
    {
        standardOutput ??= Console.Out;
        standardError ??= Console.Error;

        try
        {
            var extraDefines = options.Define
                .Where(symbol => !string.IsNullOrWhiteSpace(symbol))
                .Select(symbol => symbol.Trim())
                .Distinct(StringComparer.Ordinal)
                .ToArray();

            if (options.Input != null)
            {
                return await RunSingleFileAsync(options, extraDefines, standardOutput, standardError);
            }

            return await RunDirectoryAsync(options, extraDefines, standardOutput, standardError);
        }
        catch (Exception exception)
        {
            await standardError.WriteLineAsync($"Error: {exception.Message}");
            if (options.Verbose)
            {
                await standardError.WriteLineAsync(exception.StackTrace);
            }

            return 1;
        }
    }

    private static async Task<int> RunSingleFileAsync(
        StripConditionalOptions options,
        IReadOnlyList<string> extraDefines,
        TextWriter standardOutput,
        TextWriter standardError)
    {
        if (!File.Exists(options.Input))
        {
            await standardError.WriteLineAsync($"Error: Input file not found: {options.Input}");
            return 1;
        }

        var source = await SourceTextIo.ReadAsync(options.Input);
        var result = new ConditionalDirectiveStripper().Strip(
            source.Code,
            new ConditionalDirectiveStripOptions
            {
                RemoveRegions = options.RemoveRegions,
                Symbols = extraDefines,
            });

        await WriteDiagnosticsAsync(result.Diagnostics, options.Input, standardError);
        if (options.Output == null)
        {
            await standardOutput.WriteAsync(result.OutputCode);
        }
        else if (result.Changed)
        {
            await SourceTextIo.WriteAtomicallyAsync(options.Output, result.OutputCode, source.HasBom);
        }
        else
        {
            CopyFileVerbatim(options.Input, options.Output);
        }

        if (options.Verbose)
        {
            await standardError.WriteLineAsync(
                $"strip-conditional: 1 scanned, {(result.Changed ? 1 : 0)} transformed, " +
                $"{(result.Changed ? 0 : 1)} clean, " +
                $"{(result.Diagnostics.Any(diagnostic => diagnostic.Severity == ConditionalDirectiveSeverity.Error) ? 1 : 0)} failed, " +
                $"{result.Statistics.DirectivesRemoved} directives removed, " +
                $"{result.Statistics.DisabledBranchesRemoved} disabled blocks removed");
        }

        var hasErrors = result.Diagnostics.Any(diagnostic => diagnostic.Severity == ConditionalDirectiveSeverity.Error);
        var hasWarnings = result.Diagnostics.Any(diagnostic => diagnostic.Severity == ConditionalDirectiveSeverity.Warning);
        return hasErrors || (options.Strict && hasWarnings) ? 1 : 0;
    }

    private static async Task<int> RunDirectoryAsync(
        StripConditionalOptions options,
        IReadOnlyList<string> extraDefines,
        TextWriter standardOutput,
        TextWriter standardError)
    {
        if (options.Source == null || options.Destination == null)
        {
            await standardError.WriteLineAsync("Error: provide (-i [-o]) or (-s <source> -d <destination>).");
            return 1;
        }

        var sourceIsDirectory = Directory.Exists(options.Source);
        var sourceIsProject = File.Exists(options.Source) &&
            (Path.GetExtension(options.Source).Equals(".csproj", StringComparison.OrdinalIgnoreCase) ||
             Path.GetExtension(options.Source).Equals(".sln", StringComparison.OrdinalIgnoreCase));
        if (!sourceIsDirectory && !sourceIsProject)
        {
            await standardError.WriteLineAsync($"Error: Source directory/project/solution not found: {options.Source}");
            return 1;
        }

        var sourceRoot = sourceIsProject
            ? Path.GetDirectoryName(Path.GetFullPath(options.Source))!
            : Path.GetFullPath(options.Source);
        var destinationRoot = Path.GetFullPath(options.Destination);
        var inPlace = PathsEqual(sourceRoot, destinationRoot);
        Directory.CreateDirectory(destinationRoot);

        var symbolCache = new Dictionary<string, ProjectSymbols>(StringComparer.OrdinalIgnoreCase);
        var emittedWarningKeys = new HashSet<string>(StringComparer.Ordinal);
        var hadWarnings = false;

        async Task<ProjectSymbols> SymbolsForFileAsync(string file, string relativePath)
        {
            var projectPath = FindNearestProjectPath(Path.GetDirectoryName(file)!);
            var cacheKey = projectPath ?? "<no-project>";
            if (!symbolCache.TryGetValue(cacheKey, out var symbols))
            {
                symbols = ProjectSymbolResolver.ResolveForFile(
                    file,
                    options.Configuration,
                    options.Tfm,
                    extraDefines);
                symbolCache.Add(cacheKey, symbols);
            }

            foreach (var warning in symbols.Warnings)
            {
                if (emittedWarningKeys.Add(cacheKey + "\0" + warning))
                {
                    hadWarnings = true;
                    await standardError.WriteLineAsync($"[Warning] {relativePath}:0: {warning}");
                }
            }

            return symbols;
        }

        var scanned = 0;
        var transformed = 0;
        var clean = 0;
        var failed = 0;
        var directivesRemoved = 0;
        var disabledBlocksRemoved = 0;

        foreach (var file in EnumerateFilesSkippingRoot(sourceRoot, inPlace ? null : destinationRoot)
                     .Where(path => Path.GetExtension(path).Equals(".cs", StringComparison.OrdinalIgnoreCase)))
        {
            scanned++;
            var relativePath = Path.GetRelativePath(sourceRoot, file);
            var destinationPath = Path.Combine(destinationRoot, relativePath);
            var source = await SourceTextIo.ReadAsync(file);
            var symbols = await SymbolsForFileAsync(file, relativePath);
            var result = new ConditionalDirectiveStripper().Strip(
                source.Code,
                new ConditionalDirectiveStripOptions
                {
                    RemoveRegions = options.RemoveRegions,
                    Symbols = symbols.Symbols,
                });

            await WriteDiagnosticsAsync(result.Diagnostics, relativePath, standardError);
            var hasErrors = result.Diagnostics.Any(diagnostic => diagnostic.Severity == ConditionalDirectiveSeverity.Error);
            var hasWarnings = result.Diagnostics.Any(diagnostic => diagnostic.Severity == ConditionalDirectiveSeverity.Warning);
            hadWarnings |= hasWarnings;
            directivesRemoved += result.Statistics.DirectivesRemoved;
            disabledBlocksRemoved += result.Statistics.DisabledBranchesRemoved;

            if (hasErrors)
            {
                failed++;
                if (!inPlace)
                {
                    CopyFileVerbatim(file, destinationPath);
                }

                continue;
            }

            if (result.Changed)
            {
                await SourceTextIo.WriteAtomicallyAsync(destinationPath, result.OutputCode, source.HasBom);
                transformed++;
                if (options.Verbose)
                {
                    await standardOutput.WriteLineAsync($"transformed: {relativePath}");
                }
            }
            else
            {
                if (!inPlace)
                {
                    CopyFileVerbatim(file, destinationPath);
                }

                clean++;
                if (options.Verbose)
                {
                    await standardOutput.WriteLineAsync($"clean (copy): {relativePath}");
                }
            }
        }

        if (!inPlace)
        {
            foreach (var file in EnumerateFilesSkippingRoot(sourceRoot, destinationRoot)
                         .Where(path => !Path.GetExtension(path).Equals(".cs", StringComparison.OrdinalIgnoreCase)))
            {
                var relativePath = Path.GetRelativePath(sourceRoot, file);
                CopyFileVerbatim(file, Path.Combine(destinationRoot, relativePath));
            }
        }

        await standardOutput.WriteLineAsync(
            $"strip-conditional: {scanned} scanned, {transformed} transformed, {clean} clean, {failed} failed, " +
            $"{directivesRemoved} directives removed, {disabledBlocksRemoved} disabled blocks removed");
        return failed > 0 || (options.Strict && hadWarnings) ? 1 : 0;
    }

    private static async Task WriteDiagnosticsAsync(
        IEnumerable<ConditionalDirectiveDiagnostic> diagnostics,
        string filePath,
        TextWriter standardError)
    {
        foreach (var diagnostic in diagnostics)
        {
            await standardError.WriteLineAsync(
                $"[{diagnostic.Severity}] {filePath}:{diagnostic.Line ?? 0}: {diagnostic.Message}");
        }
    }

    private static void CopyFileVerbatim(string sourcePath, string destinationPath)
    {
        if (PathsEqual(sourcePath, destinationPath))
        {
            return;
        }

        var parent = Path.GetDirectoryName(Path.GetFullPath(destinationPath))!;
        Directory.CreateDirectory(parent);
        File.Copy(sourcePath, destinationPath, overwrite: true);
    }

    private static string? FindNearestProjectPath(string startDirectory)
    {
        var directory = Path.GetFullPath(startDirectory);
        while (true)
        {
            var project = Directory.EnumerateFiles(directory, "*.csproj")
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
            if (project != null)
            {
                return Path.GetFullPath(project);
            }

            var parent = Directory.GetParent(directory)?.FullName;
            if (parent == null)
            {
                return null;
            }

            directory = parent;
        }
    }

    private static IEnumerable<string> EnumerateFilesSkippingRoot(string sourceRoot, string? skipRoot)
    {
        var pending = new Stack<string>();
        pending.Push(Path.GetFullPath(sourceRoot));
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            foreach (var file in Directory.EnumerateFiles(current).OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                yield return Path.GetFullPath(file);
            }

            foreach (var directory in Directory.EnumerateDirectories(current)
                         .OrderByDescending(path => path, StringComparer.OrdinalIgnoreCase))
            {
                var fullPath = Path.GetFullPath(directory);
                if (skipRoot != null && IsSameOrBelow(fullPath, skipRoot))
                {
                    continue;
                }

                pending.Push(fullPath);
            }
        }
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(
            Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);

    private static bool IsSameOrBelow(string path, string root)
    {
        var fullPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return fullPath.Equals(fullRoot, StringComparison.OrdinalIgnoreCase) ||
               fullPath.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
               fullPath.StartsWith(fullRoot + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}
```

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
