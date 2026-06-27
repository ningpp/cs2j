# GotoEliminator — C# Goto/Label 预处理器设计

**日期**: 2026-06-27
**范围**: 新模块 `src/CSharpToJava.Core/GotoEliminator/` + CLI 动词 `eliminate-goto` + 验证脚本 + 单元测试
**状态**: 待用户审核

## 1. 背景与目标

### 1.1 任务

开发一个 C# 源代码预处理器，将包含 `goto`/`label` 的 C# 代码转换为等价的、不含 `goto`/`label` 的 C# 代码。其输出将作为现有 C#→Java 转换器的输入（Java 没有 goto 等价物）。

### 1.2 项目背景

- 工作目录 `d:\code\cs2j` 是基于 Roslyn 的 C#→Java 转换器（net10.0，Roslyn `Microsoft.CodeAnalysis.CSharp` 4.12.0，xUnit 2.9.3）。
- 只读源项目 `D:\csharpxml` 是 .NET 的 `System.Private.Xml` 实现（net10.0，`AllowUnsafeBlocks`，`LangVersion=latest`）。共 19 个 `.cs` 文件含字面量 `goto`，120+ 处实际 goto 语句。
- 现有转换器已有 IR 层 goto 处理（`Context/GotoAnalyzer.cs`、`Context/LabelRegistry.cs`）和两份相关设计文档（`2026-05-31-label-goto-statement-design.md`、`2026-06-03-goto-conversion-redesign.md`）。本设计与它们**完全独立**，不修改它们。

### 1.3 强制约束

1. `D:\csharpxml` 绝对只读——预处理器和验证脚本绝不向该路径写入。
2. 文件完整性——不丢失、合并、拆分任何源文件。
3. 幂等性——不含 `goto`/`label` 的文件输出 SHA256 与输入完全一致。
4. 语义等价——全部执行路径行为一致。
5. 验证流程——复制 `D:\csharpxml` → 临时目录 → 转换副本 → `dotnet build` + `dotnet test` 全过。

### 1.4 成功标准

- [ ] `D:\csharpxml` 原目录 SHA256 校验未被修改
- [ ] 临时副本 `dotnet build` 通过
- [ ] 临时副本 `dotnet test` 通过
- [ ] 所有含 goto 的文件被成功转换且编译通过
- [ ] 不含 goto 的文件 SHA256 与原始文件一致
- [ ] 转换后含 goto 的文件数 = 0（自检通过）

## 2. 关键设计决策（已与用户确认）

| # | 决策 | 选项 | 选择 | 理由 |
|---|------|------|------|------|
| D1 | `goto label` 转换策略 | (a) 统一状态机；(b) 混合（A/D 类简化、B/C 类状态机）；(c) 最小作用域包装 | **(a) 统一状态机** | 最易正确实现与验证；输出会再次进入转换器，可读性次要 |
| D2 | `goto case`/`goto default` 处理 | (a) 去糖为合成标签后走统一状态机；(b) switch 局部 while 循环；(c) fall-through | **(a) 去糖为合成标签** | 每方法单一状态机，最统一，合成标签永不进入输出 |
| D3 | CLI 集成 | (a) 仅新增 `eliminate-goto`；(b) 接入 `convert`/`convert-project`；(c) 接入并保留 IR 层回退 | **(a) 仅新增 `eliminate-goto`** | 自包含模块，blast radius 最小；现有 IR 层 goto 处理保持不变 |

### 2.1 偏离任务简报的一处（已获用户批准）

任务简报推荐**内联**状态机：`label:` → `if(__state==N){__state=-1;}`，`goto label` → `__state=N; continue;`。

本设计改用**基于基本块的 switch 状态机**。理由：内联模式在本代码库的 `2026-06-03-goto-conversion-redesign.md §1.2` 中已记录致命缺陷（Bug #1/#2/#4/#5）——`goto` 后从 `while(true)` 顶部重入会**重复执行标签之前的所有代码**，包括变量声明。switch 形式直接跳到目标基本块，避免重复执行。

仍遵循简报要求使用 `__state`/`while(true)`/`continue` 与 `__` 前缀变量名，仅结构从内联守卫改为 `switch(__state)` 分派。

## 3. 模块边界与布局

新模块位于 `src/CSharpToJava.Core/GotoEliminator/`，**自包含**（不引用现有 `GotoAnalyzer`/`LabelRegistry`）。遵循 `LinqRewrite/` 的 `CSharpSyntaxRewriter` 约定。

```
src/CSharpToJava.Core/GotoEliminator/
  GotoEliminator.cs            # 公共入口 Eliminate(source, opts) -> (code, changed, diagnostics)
  GotoCaseDesugarer.cs         # CSharpSyntaxRewriter：goto case/default -> 合成标签
  StateMachineBuilder.cs       # 基本块分割 + 变量提升 + while/switch 发射
  BasicBlock.cs                # 基本块模型（index, label, stmts, exit, fallThroughTarget）
  GotoEliminatorOptions.cs     # verbose, strict（遇到不支持方法 throw vs warn-and-skip）
  GotoEliminatorDiagnostics.cs # 诊断记录类型
```

公共 API：

```csharp
public sealed class GotoEliminator
{
    public GotoEliminatorResult Eliminate(string sourceCode, GotoEliminatorOptions? options = null);
}

public sealed record GotoEliminatorResult(
    string OutputCode,
    bool Changed,
    IReadOnlyList<GotoEliminatorDiagnostic> Diagnostics,
    GotoEliminatorStatistics Statistics);

public sealed record GotoEliminatorOptions(bool Verbose, bool Strict);
```

## 4. 转换管线

### 4.1 文件级流程

```
Eliminate(sourceCode, opts):
  1. CSharpSyntaxTree.ParseText(sourceCode) -> tree
  2. root = tree.GetRoot()
  3. dirty = root.DescendantNodes().Any(n is GotoStatementSyntax or LabeledStatementSyntax)
  4. if not dirty: return (sourceCode, Changed=false, [], stats)   # 字节级保真
  5. desugared = GotoCaseDesugarer.Visit(root)                     # §4.2
  6. rewritten = StateMachineRewriter.Visit(desugared)             # §4.3
  7. formatted = Formatter.Format(rewritten, workspace, docOptions)
  8. return (formatted.ToFullString(), Changed=true, diagnostics, stats)
```

**Dirty 判定**：语法树中存在任意 `GotoStatementSyntax`（含 `goto case`/`goto default`）或 `LabeledStatementSyntax` 节点。字符串/注释中的字面 "goto" 不算 dirty。

**字节级保真**：clean 文件直接返回原 `sourceCode` 字符串（不重新规范化换行/编码）。`-s/-d` 目录模式下 clean 文件用 `File.Copy` 字节复制，不读入再写出。

### 4.2 Pass 1 — GotoCaseDesugarer

`CSharpSyntaxRewriter`，将 `goto case`/`goto default` 去糖为合成 `goto <标识符>`，使后续状态机只需处理 `goto <identifier>` 一种形式。

对每个含 `goto case`/`goto default` 的 `SwitchStatementSyntax`：

1. 为该 switch 分配唯一编号 `N`（方法内递增）。
2. 对每个 `SwitchSectionSyntax`：
   - 若该 section 的标签是 `CaseSwitchLabelSyntax` 且其值为常量 `X`：在 section 语句列表最前插入合成标签 `__caseN_X:`（若 `X` 是数值/字符串字面量，按其文本；若是枚举成员 `E.Member`，用 `__caseN_E_Member`）。仅当该 section 是某 `goto case X` 的目标时才插入（避免无谓标签）。
   - 若该 section 的标签是 `DefaultSwitchLabelSyntax` 且是 `goto default` 的目标：插入 `__defaultN:`。
3. 将每个 `GotoStatementSyntax`（`Kind() == GotoCaseStatement`）替换为 `goto __caseN_X;`；将 `GotoDefaultStatement` 替换为 `goto __defaultN;`。
4. **`X` 的规范化**：C# 规定 `goto case` 的值为常量表达式。统一取其 `SymbolDisplayFormat.FullyQualifiedFormat` 或退化为源文本去空白后转合法标识符字符（非字母数字→下划线），保证同一 switch 内 `goto case X` 与 `case X:` 规范化结果一致。例：`goto case 2;`→`__case0_2`；`goto case DayOfWeek.Monday;`→`__case0_DayOfWeek_Monday`；`goto case 1+1;`→`__case0_1_1`（源文本去空白）。
5. 合成标签名以 `__` 开头，与用户标签空间隔离；去糖后它们由 Pass 2 的状态机消除，永不进入最终输出。

### 4.3 Pass 2 — StateMachineBuilder

#### 4.3.1 作用域

逐方法处理。仅当方法/构造器/操作符的 **body block** 含 `GotoStatementSyntax` 或 `LabeledStatementSyntax` 时才改写该方法；其它成员原样透传。表达式体成员（arrow expression）不可能含 goto，跳过。

对每个 dirty 方法体 `BlockSyntax methodBody`：

#### 4.3.2 扁平化

将 `methodBody.Statements` 扁平化为线性语句列表：递归展开**含 label/goto 的裸 `BlockSyntax`**（即 `BlockSyntax` 直接作为语句列表元素出现，而非作为 `if`/`for`/`while`/`try`/`using`/`fixed`/`lock` 等语句的控制体），把内层语句上提到当前列表；**不含** label/goto 的裸块整体作为单个语句保留。受控制语句（`if`/`for`/`while`/`try`/...）体内的 `BlockSyntax` **不**展开——它们随父语句一起作为一个语句保留。这样标签与 goto 暴露到同一顶层线性序列，便于基本块分割，同时不破坏控制流结构。

#### 4.3.3 基本块分割

按以下边界切分线性语句列表（与 `2026-06-03-goto-conversion-redesign.md §2.3.2` 一致）：

- `LabeledStatementSyntax`：开新块，标签记入新块。
- `GotoStatementSyntax`：当前块追加该语句后终结（`BlockExit.Goto`），开新块。
- `ReturnStatementSyntax` / `ThrowStatementSyntax`：当前块追加后终结（`BlockExit.Return`），开新块。
- `BreakStatementSyntax` / `ContinueStatementSyntax`（非 goto）：当前块追加后终结（`BlockExit.Break`），开新块。
- 含 goto 的 `IfStatementSyntax`：当前块追加后终结（`BlockExit.ConditionalGoto`），开新块。
- 其它语句：追加到当前块。
- 末块若非空：`BlockExit.FallThrough`。
- 为每个 `FallThrough`/`ConditionalGoto` 块设置 `FallThroughTarget = 下一块 index`。

`BasicBlock` 模型：
```csharp
public sealed class BasicBlock {
    public int Index { get; init; }
    public string? Label { get; init; }          // 起始标签（用户标签或合成 __caseN_X）
    public List<StatementSyntax> Statements { get; }
    public BlockExit Exit { get; set; }           // FallThrough|Goto|ConditionalGoto|Return|Break
    public int? FallThroughTarget { get; set; }
}
public enum BlockExit { FallThrough, Goto, ConditionalGoto, Return, Break }
```

#### 4.3.4 变量提升

状态机的 `while` 循环会重入，跨基本块使用的局部声明必须提升到循环外，否则重入时重复声明或引用未声明变量。

**判定算法**：对扁平化后位于基本块 `i` 的每个 `LocalDeclarationStatementSyntax`，收集其声明的标识符集合 `names`。对每个 `j > i` 的基本块，若该块任意语句的 `DescendantNodes().OfType<IdentifierNameSyntax>()` 中存在 `Identifier.Text ∈ names`，则该声明需提升。（仅扫扁平化后的顶层语句；嵌套在 `if`/`for`/`try` 等内部的局部声明不跨块，不提升。若声明本身在嵌套控制语句内，它不属于扁平化顶层，不在本算法范围——保持原样。）

**规则**：

| 局部类型 | 处理 |
|----------|------|
| 普通 `T x = expr;`（非 const 非 using 非 ref 非 fixed） | 提升：循环外 `T x = default;`，原位置改为 `x = expr;` |
| `const T x = expr;` | 原地保留（编译期常量，无运行时重入问题） |
| `using var x = ...;` / `using(...){}` 跨块 | 发诊断，按 `Strict` 模式 throw 或 skip 该方法（见 §4.3.6） |
| `fixed` / `unsafe` 跨块 | 同上 |
| `ref`/`out` 局部跨块 | 同上 |
| 模式变量 `x is Foo f` / 调用中的 `out var` | 块内作用域，不跨块，不提升 |
| 仅块内使用的局部 | 不提升，原地保留 |

**default 值**：`default(T)`（直接构造 `default(T)` 表达式节点）。

#### 4.3.5 状态机发射

```csharp
int __state = 0;
while (true) {
    switch (__state) {
        case 0:
            <block 0 statements, with goto rewritten>
            __state = <nextOrTarget>; continue;
        case 1:
            <block 1 statements>
            __state = <nextOrTarget>; continue;
        // ...
    }
}
```

**语句改写**：

| 原语句 | 改写为 |
|--------|--------|
| `goto label;`（用户或合成标签） | `__state = <label 所在块 index>; continue;` |
| `if (c) goto L;` | `if (c) { __state = <L 块>; continue; }`（后接 fall-through 转移） |
| `if (c) goto L; else goto M;` | `if (c) { __state = <L>; continue; } else { __state = <M>; continue; }` |
| `return X;` / `throw X;` | 原样（直接退出方法） |
| 非_goto 的 `break;` / `continue;`（位于内层循环内） | 原样（仅影响内层循环） |
| `BlockExit.FallThrough` 末尾 | `__state = <FallThroughTarget>; continue;` |
| `BlockExit.Return`/`Break`（非 goto 的 break/continue 终结块） | 块内已含该语句，无需额外转移；块末尾不放任何转移（不可达） |

**case 结尾**：每个 case 必须以 `__state=...; continue;` 或 `return`/`throw` 结束——C# 不允许 switch case fall-through，`continue`（继续外层 while）满足"case 不 fall through"规则。绝不在 case 级发射裸 `break;`。

**try/catch/finally**：`__state=N; continue;` 在 try 块内发射时，C# try/finally 语义保证 finally 先执行再转移。跨 try 的 goto（goto 在 try 内、标签在 try 外）也正确：continue 触发 finally，while 重入到目标 case。保留异常语义。

#### 4.3.6 不支持方法的回退

若某方法触发以下任一情形：
- 跨块的 `using`/`fixed`/`ref`/`out` 局部（§4.3.4）；
- 或 StateMachineBuilder 检测到的其它无法安全改写模式。

则：
- `Strict=true`：抛 `GotoEliminatorException`，CLI 返回非零退出码。
- `Strict=false`（默认）：该方法**原样保留**（含 goto/label），记录 `GotoEliminatorDiagnostic`（Severity=Warning, 方法名, 原因）。其它方法仍正常改写。文件输出仍标记为 `Changed=true` 但含未消除的 goto。

> 注：成功标准要求"所有含 goto 的文件被成功转换"。若 csharpxml 实际触发此回退，实现阶段需扩展提升器（最可能扩展 `using var` 跨块为显式 try/finally）。本设计的回退机制保证不崩溃，但若触发则需迭代解决。

### 4.4 格式化与 trivia 保留

- 用 Roslyn `Microsoft.CodeAnalysis.Formatting.Formatter.Format` 格式化改写后的根节点。
- 文档选项：若从 workspace 加载，用 `Document.GetOptionsAsync()`；否则用 `CompilationOptions` + 默认 `Workspace.Options`。
- trivia（注释、预处理器指令、空白）通过 `WithTriviaFrom` / `WithLeadingTrivia` / `WithTrailingTrivia` 在改写时显式附加到最近等价节点。标签的 leading trivia 迁移到对应 case 标签。
- 脏文件不要求字节级一致，仅"风格一致"（缩进、大括号风格跟随 `DocumentOptions`）。

## 5. CLI

在 `src/CSharpToJava.CLI/Program.cs` 新增动词，沿用 `CommandLineParser` `[Verb]` 模式（与 `convert`/`analyze` 一致）。

```
dotnet run --project src/CSharpToJava.CLI/CSharpToJava.CLI.csproj -- eliminate-goto -i <input.cs> -o <output.cs>
dotnet run --project src/CSharpToJava.CLI/CSharpToJava.CLI.csproj -- eliminate-goto -s <srcDir> -d <dstDir>
```

```csharp
[Verb("eliminate-goto", HelpText = "Eliminate goto/label from C# source")]
class EliminateGotoOptions
{
    [Option('i', "input",  SetName = "file",  HelpText = "Input .cs file")]
    public string? Input { get; set; }
    [Option('o', "output", SetName = "file",  HelpText = "Output .cs file (default: stdout)")]
    public string? Output { get; set; }
    [Option('s', "source",      SetName = "dir", HelpText = "Source directory")]
    public string? Source { get; set; }
    [Option('d', "destination", SetName = "dir", HelpText = "Destination directory (required for -s; may equal -s for in-place transform of a copy)")]
    public string? Destination { get; set; }
    [Option('v', "verbose", Default = false)]
    public bool Verbose { get; set; }
    [Option("strict", Default = false, HelpText = "Treat unsupported-method diagnostics as fatal")]
    public bool Strict { get; set; }
}
```

**行为**：
- `-i/-o`：单文件。`-o` 省略时输出到 stdout。
- `-s/-d`：目录模式。`-d` 必填（handler 校验：`-s` 提供而 `-d` 缺失 → 报错退出 1）。递归复制 `Source` → `Destination`（保留结构，所有文件），随后对其中 dirty `.cs` 用改写结果覆盖。clean `.cs` 与非 `.cs` 文件字节复制。允许 `-s == -d`（在已复制的临时目录上就地转换，见 §6.2）；CLI 内部对每个 dirty 文件先写 `*.tmp` 再原子替换，避免写失败损坏文件。**`Source` 本身绝不被修改**——调用方负责确保 `Source` 是副本或可写目录。
- 退出码：0 = 所有 dirty 文件完全转换；1 = 有方法被 skip 或 I/O 错误或参数校验失败。
- `-v`：逐文件日志（dirty/clean、方法数、诊断）。

**Program.cs 改动**：在 `ParseArguments<ConvertOptions, ConvertProjectOptions, AnalyzeOptions, EliminateGotoOptions>(args)` 加一个泛型参数与一个 `MapResult` 分支 `(EliminateGotoOptions opts) => EliminateGoto(opts)`。

## 6. 验证自动化

### 6.1 红基线脚本 `scripts/verify-goto-eliminator-baseline.ps1`

建立"未转换的 csharpxml 今日是否 build+test 通过"基线。实现第一步先跑它；若失败即前置问题，立即上报。

```powershell
$src = "D:\csharpxml"
$tmp = "D:\temp\cs2j_baseline_" + [guid]::NewGuid().ToString("N")
Copy-Item -Recurse -Path $src -Destination $tmp
Set-Location $tmp
dotnet build csharpxml.sln
dotnet test csharpxml.sln
Set-Location D:\code\cs2j
Remove-Item -Recurse -Force $tmp
```

### 6.2 验证脚本 `scripts/verify-goto-eliminator.ps1`

```powershell
$src = "D:\csharpxml"
$tmp = "D:\temp\cs2j_verify_" + [guid]::NewGuid().ToString("N")

# Step 0: 记录源目录 SHA256 基线
$beforeHashes = Get-FileHash -Algorithm SHA256 -Path (Get-ChildItem -Recurse -File $src)

# Step 1: 复制到临时目录
Copy-Item -Recurse -Path $src -Destination $tmp

# Step 2: 对副本就地转换（CLI 内部以临时目录为源与目标）
dotnet run --project src/CSharpToJava.CLI/CSharpToJava.CLI.csproj -- `
    eliminate-goto -s $tmp -d $tmp --verbose

# Step 3: 副本编译并测试
Set-Location $tmp
dotnet build csharpxml.sln
dotnet test csharpxml.sln

# Step 4: 自检——转换后副本中含 goto/label 的 .cs 文件数应为 0
# （CLI 输出或脚本用 Select-String \bgoto\b 复核）

# Step 5: 重新记录源目录 SHA256 并断言未变
Set-Location D:\code\cs2j
$afterHashes = Get-FileHash -Algorithm SHA256 -Path (Get-ChildItem -Recurse -File $src)
Compare-Object $beforeHashes $afterHashes   # 期望无差异

# Step 6: 清理
Remove-Item -Recurse -Force $tmp
```

**实现注记**：`-s $tmp -d $tmp`（同源同目标）由 CLI 安全处理——内部先写临时文件再原子替换，或先复制到 `$tmp.out` 再覆盖。实现时择一，保证不破坏文件。

### 6.3 验证局限

`D:\csharpxml\Tests\Tests.csproj` 注释明确写明"不引用自定义库，直接使用框架 XML 类型测试功能正确性"——即测试项目不引用 `System.Private.Xml.csproj`。因此：

- `dotnet test` 通过**不**直接验证含 goto 代码的运行时语义。
- **`dotnet build`（主库编译）是主要 gate**——若转换破坏语义导致编译失败，build 失败。
- 运行时语义 bug 若仍能编译，单靠此流程无法捕获。缓解：单元测试（§7）覆盖行为级断言；若需更强保证，后续可加针对转换前后行为对比的快照测试。

## 7. 单元测试

新文件 `tests/CSharpToJava.Tests/GotoEliminatorTests.cs`（xUnit，沿用现有测试 csproj 约定）。

| # | 分类 | 用例 | 断言 |
|---|------|------|------|
| 1 | 幂等 | 无 goto/label 的源 | 输出与输入字节一致 |
| 2 | 幂等 | "goto" 仅出现在字符串/注释中 | dirty=false，字节一致 |
| 3 | A1 | `outer: for(...){ if(x) goto outer; }` | 0 goto/label；行为等价 |
| 4 | A2 | `target: { if(x) goto target; }` | 同上 |
| 5 | B2 后向 | `t: x=1; if(c) goto t;` | 同上 |
| 6 | B3 前向 | `goto skip; x=1; skip: x=2;` | 同上 |
| 7 | C 跨域 | `t: x=1; for(...){ if(c) goto t; }` | 同上 |
| 8 | 变量提升 | 局部声明在标签前、在标签后使用 | 提升到 while 外，无重复声明，行为等价 |
| 9 | goto case 前向 | `case 1: goto case 2; case 2: ...` | 0 goto/label，行为等价 |
| 10 | goto case 后向 | `case 2: goto case 1; case 1: ...` | 同上 |
| 11 | goto default | `case 1: goto default; default: ...` | 同上 |
| 12 | try/finally + goto | goto 在 try 内、标签在 try 外 | finally 仍执行，行为等价 |
| 13 | 嵌套循环 break/continue | 非 goto 的 break/continue 在内层循环内 | 原样保留，行为等价 |
| 14 | return/throw 在块中 | 终结块 | 直接退出，无 fall-through 转移 |
| 15 | round-trip | 任意转换结果 | 重新解析后 0 个 goto/label 节点 |
| 16 | csharpxml 快照 | 转换 `XsdDuration.cs`（含 ~40 goto） | 输出可编译、0 goto/label |
| 17 | 跨块 using 跨块（负面） | 触发 §4.3.6 回退 | 默认模式：warn + 保留方法；strict：throw |
| 18 | 多方法文件 | 一个文件含 dirty 与 clean 方法 | 仅 dirty 方法改写，clean 方法字节不变 |

**行为等价断言**：用 `CSharpScript.EvaluateAsync` 或编译为程序集后反射调用，对比转换前后对相同输入的输出/异常。对复杂用例（#3/#7/#12），构造可执行 snippet。

## 8. 风险与缓解

| 风险 | 缓解 |
|------|------|
| 跨块 `using`/`fixed`/`ref` 局部触发 §4.3.6 回退 | 实现初期先扫描 csharpxml 统计实际触发面；最可能扩展 `using var` 跨块为显式 try/finally |
| 格式化漂移（脏文件不字节一致） | 接受——简报仅要求"风格一致"；clean 文件字节保真 |
| trivia（注释）丢失 | `WithTriviaFrom` 显式附加；测试 #16 真实文件校验 |
| 磁盘空间（每次验证完整复制 csharpxml） | 脚本每次清理；同时只存在一个临时目录 |
| `dotnet test` 不验证含 goto 代码运行时语义 | 主 gate 为 `dotnet build`；单元测试补行为断言（§7） |
| 转换后转换器仍含 IR 层 goto 处理（重复） | 不冲突——预处理后输入无 goto，IR 层代码路径不触发；保留作为直接 convert 输入的回退 |
| 合成标签名 `__caseN_X` 与用户代码冲突 | `__` 前缀；进一步在方法作用域内查重，冲突则追加下划线 |

## 9. 实现约束

- 必须在 `src/CSharpToJava.Core/GotoEliminator/` 下实现，自包含。
- 不修改 `Context/GotoAnalyzer.cs`、`Context/LabelRegistry.cs` 及现有 IR 层 goto 代码。
- 遵循 `LinqRewrite/` 的 `CSharpSyntaxRewriter` 约定。
- 遵循现有 `[Verb]` CLI 模式与 xUnit 测试模式。
- 状态机变量名使用 `__` 前缀（`__state`、`__gotoTmp` 等）。
- 保留所有原始注释（trivia）。
- net10.0 + Roslyn 4.12.0；`Nullable=enable` + `ImplicitUsings=enable`（跟随 Core 项目）。

## 10. 文件清单

### 10.1 新增

| 路径 | 内容 |
|------|------|
| `src/CSharpToJava.Core/GotoEliminator/GotoEliminator.cs` | 公共入口 |
| `src/CSharpToJava.Core/GotoEliminator/GotoCaseDesugarer.cs` | goto case/default 去糖 |
| `src/CSharpToJava.Core/GotoEliminator/StateMachineBuilder.cs` | 基本块 + 提升 + 发射 |
| `src/CSharpToJava.Core/GotoEliminator/BasicBlock.cs` | 基本块模型 |
| `src/CSharpToJava.Core/GotoEliminator/GotoEliminatorOptions.cs` | 选项 |
| `src/CSharpToJava.Core/GotoEliminator/GotoEliminatorDiagnostics.cs` | 诊断类型 |
| `scripts/verify-goto-eliminator.ps1` | 验证脚本 |
| `scripts/verify-goto-eliminator-baseline.ps1` | 红基线脚本 |
| `tests/CSharpToJava.Tests/GotoEliminatorTests.cs` | 单元测试 |

### 10.2 修改

| 路径 | 修改 |
|------|------|
| `src/CSharpToJava.CLI/Program.cs` | 加 `EliminateGotoOptions` verb + `EliminateGoto` handler + `ParseArguments` 泛型参数 |

### 10.3 不修改

- `D:\csharpxml\**`（只读）
- `src/CSharpToJava.Core/Context/GotoAnalyzer.cs`、`LabelRegistry.cs`
- 现有 IR 层 goto 处理代码（无论实际文件名为何，均不修改）
- 现有 `convert`/`convert-project`/`analyze` 命令
