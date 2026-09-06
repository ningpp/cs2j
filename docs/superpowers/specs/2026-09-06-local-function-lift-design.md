# LocalFunctionLiftingPreprocessor — C#→C# 局部函数提升预处理通道

- 日期：2026-09-06
- 状态：已批准，待实现
- 中间目录：`.cs2j-local-fn-src`
- CLI 选项：`--local-function-lift`（bool，默认 off）、`--no-local-function-strict`（bool，默认 off）

---

## 1. 背景与目标

### 1.1 问题

`D:\csharp-sdk`（818 个 `.cs` 文件）中共有 **122 个 local function**，分布在 **41 个文件**（src 层 46 个，test 层 76 个），语法节点类型为 `LocalFunctionStatementSyntax`。

当前转换器在 `src/CSharpToJava.Core/Transformers/Statement/StatementTransformer.LocalFunctions.cs` 中已实现局部函数提升（hoisting），但它发生在 **C#→Java 转换过程中**，存在三个问题：

1. hoisting 与 Java 代码生成耦合，捕获变量要映射成 Java 类型、写入型捕获要用 `IntHolder` 包装并回写，逻辑复杂且难以单独验证；
2. 生成的 Java 未经「C# 侧等价性」验证，只能靠最终 Java 编译/测试兜底；
3. 名字冲突消解依赖转换期的 `ConversionContext`，无法跨文件预计算。

### 1.2 目标

新增一个**纯 C#→C# 预处理器**，在所有转换之前把局部函数提升为类级别 `private` 方法，使进入转换管线的代码中**不再含有任何 `LocalFunctionStatementSyntax`**。转换器侧的 hoisting 逻辑保留为 fallback，不再在启用预处理的路径上被触发。

### 1.3 强制约束

1. **执行顺序**：`Program.cs` 中必须插在 `if (opts.StripDefault)` **之前**。这决定了预处理时**参数默认值仍然存在**、**泛型尚未被 generic-preprocess 降低**。
2. **语义等价优先**：宁可跳过不处理（保留原样留给转换器 fallback），也不生成不等价的代码。
3. **跳过即诊断**：每个被跳过的局部函数产生一条 `Warning`，含文件路径、行号、跳过原因。
4. **单文件模式兼容**：核心入口 `LocalFunctionLiftPreprocessor.Preprocess(string sourceCode, SemanticModel? model = null)` 必须能在无项目上下文时工作。
5. **不破坏已有功能**：不修改 `StatementTransformer.LocalFunctions.cs` 的任何已有逻辑。

### 1.4 成功标准

| # | 标准 |
|---|---|
| S1 | 预处理后的 csharp-sdk `dotnet build -c Release` 无错误 |
| S2 | `dotnet test -c Release` 全部通过，与 baseline 一致 |
| S3 | 预处理后的代码中残留 `LocalFunctionStatementSyntax` 数量 = 0（严格模式） |
| S4 | cs2j 自身 `dotnet test` 全绿（6 个 hoisting 测试文件转为 Skip 弃用态） |
| S5 | 统计输出：`FilesScanned / FilesTransformed / FilesSkippedClean / LocalFunctionsLifted / LocalFunctionsSkipped` |

---

## 2. 关键设计决策（已与用户确认）

| # | 决策 | 选择 |
|---|---|---|
| D0-1 | 写入型捕获（赋值 / `++` / `--` / `ref`、`out` 实参）的 by-reference 语义 | **`ref` 形参**：`private void f(ref int _cs2j_cap_x)`，调用点 `f(ref x)`。与 C# 闭包语义完全一致，且无需改写外层变量声明 |
| D0-2 | 无法安全提升时的跳过粒度 | **函数级**：只跳过有问题的那个局部函数，同文件其他局部函数照常提升 |
| D0-3 | 残留局部函数的处理 | **默认报错**：残留 > 0 → `Error` → `convert-project` 返回 1；`--no-local-function-strict` 降级为 `Warning` |
| D0-4 | 6 个 hoisting 测试文件的处置 | **标记弃用**：保留文件，给 `[Fact]` 加 `Skip = "..."` 说明原因，作为回归基线 |

### 2.1 偏离任务简报的三处（已获用户批准）

**D1 — 捕获参数默认「前置」而非「尾部」。**

任务书 §3 要求捕获变量成为「提升方法的尾部参数」。但 C# 规定 **optional 参数必须位于所有必填参数之后**（CS1737）、**`params` 必须是最后一个参数**。而 `--local-function-lift` 被强制排在 `--strip-default` **之前**，此时默认值尚未剥离 —— 尾部追加捕获参数会让这两个场景直接编译失败。

前置追加可同时兼容两者，且**参数顺序不影响语义**。因此：

- 默认 `CapturesAtEnd = false`（前置）；
- `LocalFunctionLiftOptions.CapturesAtEnd = true` 可切回尾部，此时若原参数含默认值或 `params` → 走闸门 #10 跳过。

**D2 — 新增 5 条任务书未列的保守闸门。**

| 闸门 | 理由 |
|---|---|
| #3 位于 lambda / 匿名方法 / 字段·属性初始化器 | 无合法类级容器，提升到类级会丢失 lambda 自己的捕获 |
| #4 宿主类型为 `interface` | 私有接口方法带体依赖 C# 8+，受目标项目 LangVersion 约束 |
| #5 存在方法组 / 委托转换引用 | 提升后签名变化，既有委托类型不再匹配 |
| #6 位于 `checked` / `unchecked` 块内 | 溢出检查上下文丢失，可能改变算术语义 |
| #11 调用点使用命名实参 | 前置捕获参数后，原有的「非尾部命名实参」会变成错位实参而编译失败 |

另外 #8 中「调用点位于 async / 迭代器方法」默认也跳过，这是**保守**而非必然错误；可通过 `LocalFunctionLiftOptions.AllowRefInAsyncCallers = true` 放宽，待 csharp-sdk 构建验证后再决定是否默认开启。

**D3 — 单文件模式自建 Compilation。**

任务书签名允许 `model == null`。为避免退化成「无语义的语法猜测」，`null` 时在 Core 内按 `NewConversionPipeline` 的既有模式自建最小 Compilation（4 个基础 `MetadataReference` + `allowUnsafe: true`）再取 `SemanticModel`。

---

## 3. 模块边界与布局

### 3.1 Core：`src/CSharpToJava.Core/LocalFunctionLift/`

| 文件 | 类型 | 职责 |
|---|---|---|
| `LocalFunctionLiftPreprocessor.cs` | `sealed class` | 入口。`Preprocess(string, SemanticModel?, LocalFunctionLiftOptions?) → LocalFunctionLiftResult`；dirty check；持有跨文件「已提升名」表 |
| `LocalFunctionLiftAnalyzer.cs` | `internal sealed class` | 纯分析，不改写：定位宿主类型、定名、捕获集合、`IsWritten`、不动点传播、可行性闸门 → 输出 `LiftPlan` 列表 |
| `LocalFunctionLiftRewriter.cs` | `CSharpSyntaxRewriter` | 删除局部函数声明、改写调用点、重命名体内捕获引用、构造并插入 `MethodDeclarationSyntax` |
| `LocalFunctionLiftNaming.cs` | `internal static class` | camelCase、成员冲突消解、捕获参数命名 + `_cs2j_cap_` 后缀 |
| `LocalFunctionLiftPlan.cs` | `internal sealed class LiftPlan` + `internal sealed record CapturePlan` | 分析与改写之间的数据契约 |
| `LocalFunctionLiftResult.cs` | `sealed record` | `(string OutputCode, bool Changed, IReadOnlyList<LocalFunctionLiftDiagnostic> Diagnostics, LocalFunctionLiftStatistics Statistics)` |
| `LocalFunctionLiftStatistics.cs` | `sealed class` | 见 §6 |
| `LocalFunctionLiftDiagnostic.cs` | `enum` + `sealed record` + `enum` | 见 §6 |

数据契约（`LocalFunctionLiftPlan.cs`）：

```csharp
internal sealed class LiftPlan
{
    public LocalFunctionStatementSyntax Node;
    public IMethodSymbol Symbol;
    public string NewName;
    public TypeDeclarationSyntax HostType;      // 插入目标（同文件内那份声明）
    public bool IsStatic;
    public List<CapturePlan> Captures;          // 有序
    public bool Skipped;
    public LocalFunctionSkipReason SkipReason;
}

internal sealed record CapturePlan(
    ISymbol Symbol,
    string OriginalName,
    string ParameterName,     // _cs2j_cap_xxx / _cs2j_cap_xxx_2
    string TypeDisplay,       // FullyQualifiedFormat
    bool IsWritten,           // → ref 形参
    bool ByRef);              // 最终形态（= IsWritten）
```

### 3.2 CLI：`src/CSharpToJava.CLI/ProjectLocalFunctionLiftPreprocessor.cs`

- `internal sealed class ProjectLocalFunctionLiftRequest`（字段与 `ProjectGenericPreprocessRequest` 一致 + `Strict`）
- `internal sealed class ProjectLocalFunctionLiftResult`（`OriginalSourcePath / SourceRoot / IntermediateRoot / PreprocessedSourcePath / Success / Statistics / Diagnostics`）
- `internal static class ProjectLocalFunctionLiftPreprocessor`，`IntermediateDirectoryName = ".cs2j-local-fn-src"`

骨架逐字照搬 `ProjectGenericPreprocessor.PreprocessAsync`：`ResolveLayout` → 中间目录清理 → `SupportedProjectFiltering.ComputeExclusions` → `CopyProjectFilesAsync` + `CopyAncestorMsBuildDirectoryFiles` → `EnumerateSourceFiles` → `CSharpParseOptions(NETCOREAPP)` 解析 → `ProjectUnsignedTypePreprocessor.GetBasicReferences()` 建 `CSharpCompilation` → 逐文件 `preprocessor.Preprocess(...)` → 合并统计/诊断 → 写回 → `RunDotNetRestore` → `VerifyBuildAndTestsAsync` → `DeleteBuildOutputDirectories`。

**新增**：写回后做残留检测（§5.2）。

### 3.3 `Program.cs` 改动（4 处）

1. 选项定义（§5.4）
2. 在 `if (opts.StripDefault)` **之前**插入 `if (opts.LocalFunctionLift)` 执行块
3. `GetPreservedDestinationDirectories` 追加 `ProjectLocalFunctionLiftPreprocessor.IntermediateDirectoryName`
4. fingerprint 串追加 `$"local-function-lift={opts.LocalFunctionLift}"`

---

## 4. 转换规则

### 4.1 宿主类型与 static 判定

**宿主类型** = 沿祖先链向上第一个 `TypeDeclarationSyntax`（class / struct / record / interface）。对 partial 类型，插入的是**局部函数所在文件内的那一份声明**。

**`static` 判定**（易错点）：提升后的方法必须是 `static`，否则从 `static` 成员里调用它会编译失败。规则：

```
IsStatic = 原局部函数带 static
         OR 沿祖先链（跳过所有 LocalFunctionStatementSyntax）遇到的第一个
            类级成员（方法 / 构造函数 / 析构函数 / 运算符 / 访问器）带 static
```

- 静态构造函数 → `static`
- 实例构造函数 → 非 `static`（`this` 可用）
- 嵌套在 static 局部函数里的非 static 局部函数 → 沿链上溯到类级成员，仍得 `static`

### 4.2 命名

- 基础名 = 原标识符首字母小写（`ParseOffset` → `parseOffset`），保留 `_` 前缀与 `@` 转义符原样
- 冲突集 = 宿主类型的**全部已声明成员名**（`SemanticModel` 取符号，自动含 partial 跨文件部分）
  \+ 本预处理器实例已分配的名字
- 冲突则追加 `2`、`3`…（`parseOffset2` 再冲突才 `parseOffset3`）
- **按名冲突而非按签名冲突**：C# 允许重载，但最终目标是 Java（类型擦除），同名不同签名会被 `RemoveAllErasedSignatureDuplicates` 丢弃并错绑调用点，故保守按名去重
- **跨文件**：项目模式复用同一个 `LocalFunctionLiftPreprocessor` 实例，用 `Dictionary<string /*typeDisplayString*/, HashSet<string>>` 记录已分配名。语义模型来自**原始** Compilation，看不到先处理文件里新提升的方法，因此该表是跨文件正确性的必要条件
- 名字分配结果对同一输入是确定的（文件枚举顺序确定）

### 4.3 捕获集合

沿用 `StatementTransformer.LocalFunctions.cs` 中已验证的判定：

- `static` 局部函数 → 零捕获（C# 语义保证）
- 遍历 `body.DescendantNodes(d => d is not LocalFunctionStatementSyntax)` 中的 `IdentifierNameSyntax`（**会**进入 lambda，因为 lambda 内引用外层变量也算捕获），按 `GetSymbolInfo().Symbol` 判定：
  - `ILocalSymbol { HasConstantValue: true }` → 不是捕获（编译期常量）
  - `ILocalSymbol / IParameterSymbol / IRangeVariableSymbol` 且 `ContainingSymbol` ≠ 该局部函数 → 是捕获
  - `identifier.Parent is NameColonSyntax` → 跳过（命名实参名，不是变量引用）
- 参数名：`_cs2j_cap_<原名>`，与已有捕获参数重名则 `_cs2j_cap_<原名>_2`、`_3`…（不同作用域可能同名，故必须消解）
- 类型：`symbol.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)`；`TypeKind.Error` 或匿名类型 → 闸门 #7

**不动点传播**：与现有实现同构。若 A 的体内直接调用了 B（不下降进嵌套局部函数），则把 B 的捕获并入 A；同一符号的 `IsWritten` 取逻辑或。循环至不再变化。

### 4.4 写入型捕获 → `ref` 形参

`IsWritten` 判定沿用现有 `IsCapturedVariableWritten`：赋值左值（含 `+=`、`??=`）、`++`/`--`、`ref`/`out` 实参。**通过成员或元素写入（`arr[i] = x`）不算**，因为变量引用本身未变，按值捕获仍正确。

`IsWritten == true` → 形参加 `ref`，调用点传 `ref x`。语义与 C# 闭包（共享显示类字段）**完全等价**。

`ref` 不可行时走闸门 #8 跳过（详见 §4.7）。

### 4.5 提升后的方法签名

```
[Attributes] private [static] [async] [unsafe] <ReturnType> <newName><TypeParams>(<captures>, <原参数>)
    <约束子句>
{ ... }
```

- **Attributes**：原样复制
- **修饰符**：`private` + `static`（§4.1）+ `async`（原样）+ `unsafe`（原样）
- **返回类型**：原样复制（含 `ref` 返回的 `RefTypeSyntax`）
- **类型参数**：原 `TypeParameterList` **+ 追加外层方法的类型参数**
  - 外层**类**的类型参数不追加 —— 提升到类级后仍可见
  - 外层**方法**的类型参数必须追加（含其 `where` 约束子句），否则提升后不可见
  - 追加的形参与局部函数已有类型参数重名 → 闸门 #9 跳过（重命名需改写体内所有引用，风险过高）
  - 追加的形参若未出现在任何形参类型中（调用点无法推断）→ 闸门 #9 跳过
- **参数列表**：
  - 捕获参数**前置**（D1），顺序 = `LiftPlan.Captures` 顺序
  - 原参数（含默认值、`ref`/`out`/`in`/`params`）原样保留其后
- **Body**：
  - 有 `Body` → 复制，并把体内对捕获变量的引用重命名为参数名
  - 有 `ExpressionBody` → 转块体：非 void → `{ return expr; }`；void → `{ expr; }`。`ArrowExpressionClause.Expression` 本身不含分号，直接包 `ReturnStatement` / `ExpressionStatement`
  - 无 body → 闸门 #2 跳过

### 4.6 调用点重写（两遍）

**第一遍**（Analyzer）：收集文件内全部局部函数的 `LiftPlan`，因为 C# 允许局部函数声明出现在调用之后。

**第二遍**（Rewriter）：对每个 `IdentifierNameSyntax` / `GenericNameSyntax`：

| 语境 | 处理 |
|---|---|
| `invocation.Expression == id` 且符号是被提升的局部函数 | 改名 + 追加捕获实参（只读 → `x`，写入 → `ref x`）；保留原类型实参 `f<int>(...)` → `f<int>(x, ...)` |
| `nameof(f)` 中的实参 | 只改名，**不追加实参** |
| 其他任何位置（方法组转换、`new Action(f)` 等） | 闸门 #5 跳过该函数 |

- 递归调用同样处理
- 调用点在 lambda 内、在被提升的函数体内、在嵌套函数体内，规则一致
- 若调用点使用**命名实参**（任一 `NameColonSyntax`）→ 闸门 #11 跳过

**体内捕获引用重命名**：对提升后方法体（不下降进嵌套局部函数，但**下降进 lambda**），把 `GetSymbolInfo().Symbol` 等于捕获符号的 `IdentifierNameSyntax` 替换为参数名。`ref` 形参在体侧无需额外语法。

### 4.7 跳过闸门（函数级）

每条产生一条 `Warning` + `LocalFunctionSkipReason`，并累加 `LocalFunctionsSkipped`：

| # | `SkipReason` | 条件 |
|---|---|---|
| 1 | `Iterator` | 体含 `yield return` / `yield break` |
| 2 | `NoBody` | 既无 `Body` 也无 `ExpressionBody` |
| 3 | `NoHostType` | 宿主为顶层语句；或位于 lambda / 匿名方法 / 字段·属性初始化器内；或找不到 `TypeDeclarationSyntax` |
| 4 | `InterfaceHost` | 宿主类型为 `interface`（可用 `AllowInterfaceHost` 放开） |
| 5 | `MethodGroupConversion` | 存在方法组或委托转换引用（`Action a = f;`、`new Action(f)`） |
| 6 | `CheckedContext` | 位于 `checked` / `unchecked` 语句或表达式内 |
| 7 | `UnsupportedCaptureType` | 捕获变量类型不可表达：匿名类型、error type、`ref` 局部、指针类型 |
| 8 | `RefCaptureUnavailable` | 写入型捕获且 `ref` 不可行：局部函数 `async`（async 方法不能声明 `ref`/`out` 形参）/ 有调用点位于 async 或迭代器方法体内（可用 `AllowRefInAsyncCallers` 放开）/ 变量是 `foreach` 迭代变量、`using` 变量、`fixed` 变量 / 变量是 `in` 或 `out` 参数 / 局部变量声明处无初始化器（`ref x` 会触发 CS0165）|
| 9 | `EnclosingTypeParameterConflict` | 需追加外层方法类型参数但重名，或追加后无法从形参推断 |
| 10 | `ParamsOrOptionalConflict` | 仅 `CapturesAtEnd = true` 时：原参数含 `params` 或含默认值 |
| 11 | `NamedArgumentCallSite` | 任一调用点使用命名实参 |
| 12 | `InternalError` | 分析过程抛异常（兜底，保留原样） |

说明：闸门 #8 中「局部变量声明处无初始化器」的判定为 `VariableDeclarator.Initializer == null`；`foreach` / 解构 / `out var` 声明的变量一律视为未初始化。

### 4.8 放置与 trivia

- 插入位置：宿主类型成员列表中「最后一个 `FieldDeclarationSyntax` 之后」；无字段则插到成员列表最前
- 原局部函数的 **leading trivia（注释、XML doc）** 转移到提升后方法上；trailing trivia 保留在原位
- 移除声明后原位置不留空行堆积
- 最后 `Formatter.Format(newRoot, new AdhocWorkspace())`（与 `DefaultParameterStripper` 一致）
- **不保证行号不变**：删除语句必然改变行号，本设计只保证注释不丢失、不作额外空行压缩

---

## 5. 流程

### 5.1 项目级 `ProjectLocalFunctionLiftPreprocessor.PreprocessAsync`

```
1  ResolveLayout(SourcePath)
2  中间目录 = <DestinationRoot>/.cs2j-local-fn-src（已存在且 Force → 删除重建）
3  TryLoadProjectGraph + SupportedProjectFiltering.ComputeExclusions
4  CopyProjectFilesAsync（.csproj 走 FixPackageDowngrades，.sln 走 RewriteSolutionContent）
   + CopyAncestorMsBuildDirectoryFiles("[local-fn]")
5  EnumerateSourceFiles（排除 excluded 项目、排除目标/输出根）
6  CSharpParseOptions(preprocessorSymbols: ["NETCOREAPP"]) 解析全部 .cs
7  CSharpCompilation.Create("local-fn-preprocess", trees,
       ProjectUnsignedTypePreprocessor.GetBasicReferences(),
       new CSharpCompilationOptions(DynamicallyLinkedLibrary, allowUnsafe: true))
8  var preprocessor = new LocalFunctionLiftPreprocessor();   // 跨文件共享命名表
9  逐文件：
     model = compilation.GetSemanticModel(tree)
     result = preprocessor.Preprocess(sourceCode, model, options)
     合并 Statistics / Diagnostics（诊断附 relativePath）
     Changed ? 写回 UTF8(no BOM) : File.Copy
10 残留检测（§5.2）
11 RunDotNetRestore → VerifyBuildAndTestsAsync(build -c Release / test -c Release) → DeleteBuildOutputDirectories
12 返回 Result（Success = 无 Error 级诊断）
```

### 5.2 残留检测

步骤 9 之后，对中间目录下所有 `.cs` 重新 `CSharpSyntaxTree.ParseText`，统计 `LocalFunctionStatementSyntax` 总数 → `Statistics.LocalFunctionsResidual`。

- `Strict == true`（默认）且 residual > 0 → 追加 `Error` 诊断（含每个残留的文件:行号）→ `Success = false` → `Program.cs` `return 1`
- `Strict == false` → 追加 `Warning`

### 5.3 `convert-project` 接入

```csharp
// Program.cs，位于 if (opts.StripDefault) 之前
if (opts.LocalFunctionLift)
{
    var liftResult = await ProjectLocalFunctionLiftPreprocessor.PreprocessAsync(
        new ProjectLocalFunctionLiftRequest
        {
            SourcePath = opts.Source,
            DestinationRoot = opts.Destination,
            Force = opts.Force,
            Verbose = opts.Verbose,
            FilterUnsupportedProjects = opts.FilterUnsupportedProjects,
            CompatNamespacePrefixes = LoadCompatNamespacePrefixes(opts.MappingConfig),
            VerifyBuild = opts.VerifyBuild,
            VerifyTests = opts.VerifyTests,
            Strict = opts.LocalFunctionStrict,
        });

    Console.WriteLine(
        $"local-function-lift: {liftResult.Statistics.FilesTransformed} transformed, " +
        $"{liftResult.Statistics.FilesSkippedClean} clean, " +
        $"{liftResult.Statistics.LocalFunctionsLifted} lifted, " +
        $"{liftResult.Statistics.LocalFunctionsSkipped} skipped, " +
        $"{liftResult.Statistics.LocalFunctionsResidual} residual");

    foreach (var d in liftResult.Diagnostics)
        Console.Error.WriteLine($"[{d.Severity}] {d.FilePath}:{d.Line}: {d.Message}");

    if (!liftResult.Success) return 1;

    opts.Source = liftResult.PreprocessedSourcePath;
    opts.FingerprintSourcePath ??= liftResult.OriginalSourcePath;
    opts.FingerprintInputRoot ??= liftResult.SourceRoot;
}
```

### 5.4 CLI 选项

```csharp
[Option("local-function-lift", Default = false,
    HelpText = "Hoist C# local functions to class-level private methods before conversion")]
public bool LocalFunctionLift { get; set; }

[Option("no-local-function-strict", Default = false,
    HelpText = "Report residual local functions as warnings instead of failing the conversion")]
public bool NoLocalFunctionStrict { get; set; }

public bool LocalFunctionStrict => !NoLocalFunctionStrict;
```

### 5.5 单文件模式

`Preprocess(string sourceCode, SemanticModel? model = null, LocalFunctionLiftOptions? options = null)`：

- `model != null` → 用 `model.SyntaxTree` 的 root
- `model == null` → `CSharpSyntaxTree.ParseText` 后，按 `NewConversionPipeline` 的既有模式自建最小 Compilation（`object` / `List<>` / `Console` / `Enumerable` 四个 `MetadataReference`，`CSharpCompilationOptions(DynamicallyLinkedLibrary, allowUnsafe: true)`），再取 `SemanticModel`
- dirty check：root 中无 `LocalFunctionStatementSyntax` → `FilesSkippedClean = 1`，原样返回

### 5.6 `LocalFunctionLiftOptions`

```csharp
public sealed record LocalFunctionLiftOptions(
    bool CapturesAtEnd = false,          // D1：默认前置
    bool AllowRefInAsyncCallers = false, // 放宽闸门 #8 的 async/迭代器调用点限制
    bool AllowInterfaceHost = false,     // 放宽闸门 #4
    bool Verbose = false);
```

---

## 6. 统计与诊断

```csharp
public sealed class LocalFunctionLiftStatistics
{
    public int FilesScanned;
    public int FilesTransformed;
    public int FilesSkippedClean;
    public int LocalFunctionsLifted;
    public int LocalFunctionsSkipped;
    public int LocalFunctionsResidual;
    public int CapturesByValue;
    public int CapturesByRef;
    public Dictionary<LocalFunctionSkipReason, int> SkippedByReason = new();
}

public enum LocalFunctionLiftSeverity { Info, Warning, Error }

public enum LocalFunctionSkipReason
{
    None, Iterator, NoBody, NoHostType, InterfaceHost, MethodGroupConversion,
    CheckedContext, UnsupportedCaptureType, RefCaptureUnavailable,
    EnclosingTypeParameterConflict, ParamsOrOptionalConflict,
    NamedArgumentCallSite, InternalError,
}

public sealed record LocalFunctionLiftDiagnostic(
    LocalFunctionLiftSeverity Severity,
    string Message,
    string? FilePath = null,
    int Line = 0,
    LocalFunctionSkipReason Reason = LocalFunctionSkipReason.None);
```

---

## 7. 已知限制（明确不做）

1. 不做 holder 包装（Java 侧的 `IntHolder` 方案）—— `ref` 形参在 C# 侧已精确等价
2. 不做行号保持，只保留注释 trivia
3. 不做跨文件的提升目标选择：partial class 提升到局部函数所在文件的那一份声明
4. 不做 `unsafe` 推导：沿用原局部函数的 `unsafe` 修饰符，捕获类型含指针时走闸门 #7
5. 不做「非必要 static 化」：宿主成员是实例成员时，提升方法保持实例方法（即使它没用到 `this`）
6. 不做命名实参调用点的参数重排（走闸门 #11 跳过）
7. 不处理 `checked`/`unchecked` 上下文（走闸门 #6 跳过）

---

## 8. 测试计划

### 8.1 单文件重写器单元测试（新增 `tests/CSharpToJava.Tests/LocalFunctionLiftTests.cs`）

每个用例：给定 C# 片段 → `LocalFunctionLiftPreprocessor.Preprocess` → 断言输出不含 `LocalFunctionStatementSyntax`，且包含期望的提升后签名/调用点。

| 类别 | 用例 |
|---|---|
| 基线 | 无捕获无参数；`static` 局部函数；普通非 static |
| 命名 | camelCase；与已有成员冲突 → `name2`；两个方法同名局部函数 → `f` / `f2` |
| 修饰符 | `async` 保留；`static` → `private static`；static 宿主方法中的非 static 局部函数 → `private static`（§4.1）；实例构造函数中 → 实例方法 |
| 泛型 | 局部函数自带 `<T>`；外层方法 `<T>` 被引用 → 追加；追加后重名 → 跳过；追加后不可推断 → 跳过 |
| 捕获 | 只读捕获 → 值参数；写入捕获（赋值 / `++` / `ref` 实参）→ `ref` 参数 + `ref` 调用点；捕获名冲突 → `_2`；const 不算捕获；嵌套传播（A 调 B，B 捕获 x → A 也捕获 x）；传递性写入传播（A 只读但调 B 写入 → A 也 `ref`）|
| 调用点 | 递归；声明在调用之后；lambda 内调用；泛型显式类型实参；`nameof` 不追加实参 |
| 体 | 表达式体 → `{ return expr; }`；void 表达式体 → `{ expr; }` |
| 跳过闸门 | 12 条各一例，断言 `LocalFunctionsSkipped == 1` 且 `Reason` 正确、输出原样 |

### 8.2 项目级集成测试

用一个最小多项目 sample（`classlib` + `xunit`），验证：中间目录生成、`.sln` 过滤、跨文件 partial class 的命名去重、restore/build/test 通过、残留检测为 0。

### 8.3 `D:\csharp-sdk` 验证

```
cs2j convert-project -s D:\csharp-sdk -d D:\out --local-function-lift -f -v
```

- 期望 `LocalFunctionsLifted ≈ 122`、`LocalFunctionsResidual == 0`
- `dotnet build -c Release` 与 `dotnet test -c Release` 于 `.cs2j-local-fn-src`，结果与原始项目 baseline 一致
- 若残留 > 0：先加 `--no-local-function-strict` 跑一遍，按 `SkippedByReason` 分布决定是否放宽某条闸门（优先候选：`AllowRefInAsyncCallers`、`AllowInterfaceHost`）

### 8.4 回归

- cs2j 自身 `dotnet test` 全绿
- 6 个 hoisting 测试文件加 `Skip`（文件与 `Skip` 文案）：
  - `LocalFunctionHoistingTests.cs`
  - `LocalFunctionWrittenCaptureTests.cs`
  - `HoistedLocalFunctionCaptureScopeTests.cs`
  - `LocalFunctionAfterReturnHoistingTests.cs`
  - `LocalFunctionExpressionBodySpanAdaptationTests.cs`
  - `GenericLocalFunctionRenamedCallSiteTests.cs`

  `Skip` 文案统一为：`"预处理阶段已消除局部函数；转换器 hoisting 路径仅作为 fallback 保留。"`
- 未启用 `--local-function-lift` 时，既有转换行为不得变化

---

## 9. 待实现阶段确定的事项

1. `AllowRefInAsyncCallers` 是否在 csharp-sdk 上需要开启（取决于闸门 #8 造成的残留量）
2. 闸门 #6（`checked`/`unchecked`）在 csharp-sdk 中的命中率；若命中率显著，可改为「提升方法体整体包一层 `checked { }`」而不是跳过
3. `params` + 捕获在 `CapturesAtEnd = false` 下已无冲突，需实测确认调用点数组展开正确
4. 跨文件命名表在 `--no-cache` 与并发场景下的确定性（当前为单线程顺序处理，风险低）
