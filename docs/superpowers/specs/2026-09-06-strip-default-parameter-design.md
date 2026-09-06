# Strip Default Parameter — C#→C# 去除参数默认值预处理通道

**日期**: 2026-09-06
**范围**: 新模块 `src/CSharpToJava.Core/DefaultParameterStripper/` + 新 CLI 预处理器 `ProjectDefaultParameterStripper` + `convert-project` 流水线接入 + `strip-default` 独立动词 + 单元/集成测试 + `D:\csharp-sdk` 验证
**状态**: 待用户审核

---

## 1. 背景与目标

### 1.1 问题

C# 方法参数的默认值（optional parameters）目前由 `Core/Transformers/Utilities/DefaultParameterHelper.cs` 在**转译阶段**通过生成 Java 重载实现。该路径：

- 复杂度高（217 行，需处理前置 synthetic `Class<T>`、后置 trailing `Class<?>`、泛型推断、ref/out、abstract、协变接口实现）。
- 易错（`GenericDefaultParameterOverloadTests.cs` 中 5 个回归用例全是 SDK 真实 bug）。
- 跨文件不安全：只在当前文件编译上下文中工作，跨项目调用点无法补全默认值。

### 1.2 目标

新增前置通道：作用于 C# 源码的 Roslyn 语法重写器，把方法/构造器参数的默认值**剥离**，并**生成对应的 C# 重载方法**。产物是仍然正确、可编译、行为等价的 C# 程序，主转译器只需处理普通 Java 方法签名。

**核心门禁**：预处理产物在 .NET 上 `dotnet build -c Release` / `dotnet test -c Release` 必须零错误通过。

### 1.3 强制约束

1. 预处理流程顺序固定：**原始源码 → Strip Default → Unsigned → Generic → ReadOnly → Goto**。
2. `D:\csharp-sdk` 只读，绝不写入。
3. 文件完整性：不丢失、合并、拆分任何源文件。
4. 幂等：无默认值的文件字节级保真（直接 `File.Copy`）。
5. 本次**不修改** `DefaultParameterHelper.cs`，不动 `DefaultParameterTests.cs` / `GenericDefaultParameterOverloadTests.cs`。

### 1.4 成功标准

- [ ] `D:\csharp-sdk` 预处理到 `.cs2j-strip-default-src` 后 `dotnet build -c Release` 零错误
- [ ] 完整日志落盘
- [ ] 预处理产物中（受管上下文内）不再存在任何 `param = value` 语法（自检通过）
- [ ] 单文件重写器单元测试 + 项目级集成测试（含跨项目调用）全绿
- [ ] 全量 `dotnet test -c Release` 无回归

---

## 2. 关键设计决策（已与用户确认）

| # | 决策 | 选项 | 选择 | 理由 |
|---|------|------|------|------|
| D1 | 调用点处理 | (a) 只剥离+重载，调用点零改动；(b) 既重载也补全调用点 | **(a) 调用点零改动** | 用户明确"调用方不需要改"。跨项目靠生成的重载自动绑定；避免跨文件默认值表达式限定（using/关键字/常量可见性）的全部风险 |
| D2 | 接口方法重载机制 | (a) C# 8+ 默认接口实现（DIM）；(b) 扩展方法静态类；(c) 双保险 | **(a) DIM** | 用户指定。无命名空间可见性坑，实例方法天然参与虚派发 |
| D3 | `convert-project` 接入 | (a) `--strip-default` 默认关；(b) `--no-strip-default` 默认开；(c) 仅独立动词 | **(b) 默认开启** | 符合"强制顺序"要求；顺序位于 Unsigned 之前 |
| D4 | `DefaultParameterHelper` | (a) 本次不动；(b) 开关联动下线 | **(a) 本次不动** | blast radius 最小，既有测试零影响 |
| D5 | 语义模型来源 | (a) 全解决方案合并 `CSharpCompilation`；(b) 逐项目编译 + 项目引用 | **(a) 合并编译** | 与 `ProjectGenericPreprocessor` / `ProjectReadOnlyStructPreprocessor` 既有范式一致，且合并后所有类型互相可见，跨项目解析最稳 |
| D6 | 默认值表达式渲染 | (a) 跨文件语义重建；(b) 逐字复制原语法 | **(b) 逐字复制** | D1 决定调用点不改；重载生成在同文件内，逐字复制 `param.Default.Value.ToString()` 天然正确 |

### 2.1 偏离任务简报的三处（已获用户批准）

1. **不做调用点补全**。原简报硬性约束 #1 要求"预处理后 App 项目的调用点必须补全默认值"。用户改判为"调用方不需要改"。跨项目集成测试相应改为断言：**App 调用点源码与原始逐字一致**，且整体 `dotnet build -c Release` 通过。
2. **接口方法生成 DIM 重载**，而非简报所写"接口不生成重载，职责留给实现类"。
3. **委托（delegate）声明不剥离默认值**。委托无法生成重载，剥离后 `d()` 调用点必然编译失败，而 D1 又禁止改调用点 —— 二者不可兼得，故保留委托默认值并记为已知限制（§6 L1）。

---

## 3. 模块边界与布局

```
src/CSharpToJava.Core/DefaultParameterStripper/
  DefaultParameterStripper.cs              # 公共入口：Collect(tree, model) / Strip(code, model, opts)
  DefaultParameterStripperOptions.cs       # Verbose / Strict / 各子开关
  DefaultParameterStripperResult.cs        # OutputCode / Changed / Diagnostics / Statistics
  DefaultParameterStripperStatistics.cs
  DefaultParameterStripDiagnostics.cs      # severity enum + diagnostic record
  DefaultParameterCatalog.cs               # Pass1 全局目录：已存在签名 / 已生成签名 / 默认值清单
  DefaultParameterDeclarationRewriter.cs   # CSharpSyntaxRewriter：剥离 + 生成重载
  DefaultParameterOverloadBuilder.cs       # 重载声明构造（方法/构造器/局部函数/接口 DIM/主构造函数）
  DefaultParameterVerifier.cs              # Pass3：产物自检，断言无残留默认值
```

```
src/CSharpToJava.CLI/
  ProjectDefaultParameterStripper.cs       # IntermediateDirectoryName = ".cs2j-strip-default-src"
```

命名空间 `CSharpToJava.Core.DefaultParameterStripper`，遵循 `UnsignedTypeRewriter/` 的 Options / Result / Statistics / Diagnostics / Rewriter 分文件范式。

公共 API：

```csharp
public sealed class DefaultParameterStripper
{
    /// Pass 1：扫描一个语法树，登记「已存在成员签名」与「待生成重载签名」，供冲突检测使用。
    public void Collect(SyntaxTree tree, SemanticModel model);

    /// Pass 2：剥离默认值 + 生成重载。
    /// 若 model.SyntaxTree 尚未 Collect（单文件场景），内部自动先执行一次 Collect。
    public DefaultParameterStripperResult Strip(
        string sourceCode,
        SemanticModel model,
        DefaultParameterStripperOptions? options = null);
}

public sealed record DefaultParameterStripperOptions(
    bool Verbose = false,
    bool Strict = false,
    bool GenerateOverloads = true);

public sealed record DefaultParameterStripperResult(
    string OutputCode, bool Changed,
    IReadOnlyList<DefaultParameterStripDiagnostic> Diagnostics,
    DefaultParameterStripperStatistics Statistics);

public enum DefaultParameterStripSeverity { Info, Warning, Error }
public sealed record DefaultParameterStripDiagnostic(
    DefaultParameterStripSeverity Severity, string Message);

public sealed class DefaultParameterStripperStatistics
{
    public int FilesScanned, FilesTransformed, FilesSkippedClean;
    public int DeclarationsStripped;    // 被剥离默认值的声明数
    public int ParametersStripped;      // 被剥离的参数数
    public int OverloadsGenerated;      // 生成的重载数
    public int SkippedNoBody;           // abstract 之外的无体声明（extern / partial 定义）
    public int SkippedConflict;         // 因签名冲突未生成
    public int SkippedHidesAbstract;    // 因会隐藏继承抽象成员未生成
    public int SkippedUnsupported;      // 委托 / 索引器 / 运算符等不受管上下文
}
```

---

## 4. 转换规则

### 4.1 覆盖矩阵

| 语法上下文 | 剥离默认值 | 生成重载 | 备注 |
|---|---|---|---|
| 实例 / static 方法 | ✅ | ✅ | 含 virtual / sealed override / new / async / 泛型 / unsafe |
| 抽象方法 `abstract` | ✅ | ✅ | 在抽象类内生成**具体**转发重载 `public void M() => M(1);` |
| `override` 方法 | ✅ | ❌ | 继承基类生成的重载即可（虚派发到 `override`），避免 CS0108 |
| 构造器 | ✅ | ✅ | 转发用 `: this(args)` 初始化器；结构体参数less 重载受 C# 10 门槛限制（§4.5）。静态构造器无参数，不涉此路径 |
| 接口方法 | ✅ | ✅ | C# 8+ DIM：`void M() => M(1);` |
| 局部函数 `LocalFunctionStatement` | ✅ | ✅ | 局部函数支持重载 |
| 显式接口实现 `void IFoo.M(...)` | ✅ | ❌ | 接口侧 DIM 已提供 `M()`；显式实现处的默认值本身无效果 |
| 分部方法 `partial` | ❌ | ❌ | 无体声明无法转发；且两处声明默认值一致性有风险，整体跳过 |
| `extern` 方法 | ❌ | ❌ | 无体无法转发 |
| 委托声明 `delegate` | ❌ | ❌ | 无法生成重载（§2.1 第 3 条） |
| 索引器 / 运算符 / 转换运算符 | ❌ | ❌ | C# 中默认值本就无效果（CS1066） |
| record / class 主构造函数 | ✅ | ✅ | 生成显式构造函数 `: this(args)` |
| `record` 的 `Deconstruct` | ✅ | ✅ | 普通方法路径 |
| 属性访问器 / 析构器 / 事件访问器 | ❌ | ❌ | 无参数或不可带默认值 |
| `[CallerMemberName]` 等特性参数 | ❌ | ❌ | 无显式默认值语法，不在处理范围 |
| `[Optional]` / `[DefaultParameterValue]` 特性 | ❌ | ❌ | 无 `= value` 语法；记为已知限制（§6 L2） |
| `params` | ✅ | ✅ | 走通用路径；`params` 参数永不作为 `cutAt` 候选（§4.3），因此永不会被"省略" |

### 4.2 剥离

对受管声明的每个 `ParameterSyntax`：`param.WithDefault(null)`，并清理标识符后残留的空白 trivia。
仅当**存在** `Default != null` 时改写；文件无任何默认值时输出字节级保真（走 `File.Copy`）。

### 4.3 重载生成

设声明参数表 `P[0..n-1]`，`D = { i | P[i].Default != null 且 P[i] 不是 params }`。

- `D` 为空 → 无默认值，不处理。
- `firstDefaultIdx = min{i | P[i].Default != null}`。若 `P[firstDefaultIdx..n-1]` 中存在既无默认值又非 `params` 的参数 → 不是合法的 C# 可选参数尾部（CS1737），整体跳过并记 Warning。

对 `cutAt ∈ D`（且 `cutAt >= firstDefaultIdx`）生成一个重载，保留 `P[0..cutAt-1]`：

> `params` 参数**永不**作为 `cutAt` 候选：否则生成重载会把「空数组」语义替换成默认值语义，改变行为。

```csharp
// 原：public T M<T>(int a, string b = "x", bool c = true) where T : class { ... }

// 生成 1：保留 1 个参数
public T M<T>(int a) where T : class { return M<T>(a, "x", true); }
// 生成 2：保留 2 个参数
public T M<T>(int a, string b) where T : class { return M<T>(a, b, true); }
```

规则细节：

- **类型参数与约束**：完整复制 `TypeParameterList` 与 `ConstraintClauses`。
- **泛型调用**：显式写出类型实参 `M<T>(...)`，不依赖推断。
- **修饰符**：复制原修饰符，剔除 `abstract` / `extern` / `partial` / `virtual` / `override` / `async` / `sealed`(无 override 时非法)；按 §4.4 可能追加 `new`。
- **方法特性**：**不复制**。重载是编译器式转发壳，复制 `[Fact]`/`[Test]` 会导致测试框架重复发现；复制 XML doc 会因 `<param>` 不匹配触发 CS1573。
- **参数特性与修饰符**：保留（`this` / `params` / 特性）。
- **返回值**：非 `void` → `{ return X(...); }`；`void` → `{ X(...); }`。接口 DIM 用表达式体 `=> X(...)`。
- **转发实参**：`i < cutAt` 用参数标识符原文；`i >= cutAt` 用 `P[i].Default.Value.ToString()` **逐字复制**（同文件，必然合法）。
- **构造器**：`public C(int a) : this(a, "x", true) { }`（空体）。
- **局部函数**：`void L() { L(1); }` / 非 void 用 `return`。
- **接口 DIM**：`void M() => M(1);`（虚派发到实现类的 `M(int)`）。
- **abstract**：在抽象类内生成**具体**方法 `public void M() { M(1); }`，虚派发到派生类 override —— 与原语义一致。
- **放置位置**：紧随原声明之后（同 member list / 同 block）。

### 4.4 冲突与隐藏检测（跨文件，Pass 1 预处理）

签名键：`(包含符号, 成员类别, 名称, 参数类型串)`；参数类型用 `SymbolDisplayFormat.FullyQualifiedFormat` 渲染；包含符号用 `SymbolEqualityComparer.Default` 比较（partial 类型在各文件中是同一符号）。

Pass 1 `Collect` 遍历全部语法树，登记：

- `existingSignatures`: 每个类型/作用域内**已存在**的成员签名（含全部参数，不管有没有默认值）。
- `pendingOverloads`: 每个待处理声明将要生成的重载签名集合。

Pass 2 生成前检查：

1. `existingSignatures` 命中 → 跳过（`SkippedConflict`）。
2. `pendingOverloads` 中同一签名已被**另一个声明**预约 → 只生成一次，后者跳过（`SkippedConflict`）。
3. 隐藏检查（沿继承链查找同签名可访问成员）：
   - 类 / 结构体：遍历 `BaseType` 链。
   - 接口：遍历 `AllInterfaces`（接口成员隐藏同样触发 CS0108）。
   - 命中且为 `abstract` → 跳过（`SkippedHidesAbstract`，否则 CS0533）。
   - 命中且非 abstract → 生成时追加 `new` 修饰符（消除 CS0108/CS0114）。
4. `override` 方法：跳过重载生成（继承基类的重载即可，虚派发保证行为一致，同时避免 CS0108）。

> 结果：任一重载生成失败都不会破坏编译 —— 最坏情况是调用点绑定到"参数更多的完整方法"，此时原 C# 本身也要求调用点给出该参数。

### 4.5 结构体参数less 构造器门槛

`struct S { S(int a = 1) }` 剥离后若生成 `S() : this(1) {}`：
- C# 10+ 允许显式无参结构构造器 → 正常生成。
- C# 9- → **不剥离**该构造器（保留默认值），记为 Info 诊断。

判定依据：`((CSharpParseOptions)tree.Options).LanguageVersion`；`Default` / `Latest` / `Preview` 视为支持。

### 4.6 格式化

仅对**发生变化**的文件做格式化，使用 `Formatter.Format` + `AdhocWorkspace`（与 `UnsignedTypeRewriter` 一致）。新增声明节点打 `Formatter.Annotation`，优先只格式化标注区域以减小 diff。

---

## 5. 流程

### 5.1 项目级（`ProjectDefaultParameterStripper.PreprocessAsync`）

```
ResolveLayout(sourcePath)
  → 删除/创建 <DestinationRoot>/.cs2j-strip-default-src
  → TryLoadProjectGraph + SupportedProjectFiltering.ComputeExclusions
  → ProjectPreprocessSource.CopyProjectFilesAsync + CopyAncestorMsBuildDirectoryFiles
  → EnumerateSourceFiles（跳过被排除项目）
      非 .cs：File.Copy（.sln 跳过，已在上一步复制/改写）
  → Pass 1  解析全部 .cs 为 SyntaxTree（CSharpParseOptions(preprocessorSymbols: NETCOREAPP)）
      构建 CSharpCompilation（ProjectUnsignedTypePreprocessor.GetBasicReferences(),
        CSharpCompilationOptions(DynamicallyLinkedLibrary, allowUnsafe: true))
      stripper.Collect(tree, compilation.GetSemanticModel(tree))     ← 全局冲突目录
  → Pass 2  逐文件 stripper.Strip(code, model, options)
      changed → WriteAllText(UTF8 无 BOM)；!changed → File.Copy
  → Pass 3  DefaultParameterVerifier 重解析输出树；排除「有意保留」上下文
      （delegate / 索引器 / 运算符 / 转换运算符 / partial / extern / 属性访问器 / [Optional] 特性）
      后仍有 ParameterSyntax.Default != null → Error 诊断
  → ProjectPreprocessSource.ResolveRestoreTarget + RunDotNetRestore
  → VerifyBuildAndTestsAsync：dotnet build -c Release（失败 → Error 诊断并 return）
                              有受支持测试项目时 dotnet test -c Release
  → DeleteBuildOutputDirectories（清 bin/obj）
```

与 `ProjectGenericPreprocessor` 同构；`VerifyBuild` / `VerifyTests` 默认 `true`，可在请求中关闭。

### 5.2 独立动词

新增 `StripDefaultOptions`（`--input/--output` 单文件、`--source/--destination` 项目），动词 `strip-default`，注册进 `Parser.Default.ParseArguments<..., StripDefaultOptions>`，实现方法 `StripDefault(opts)`，形态照抄 `UnsignedType(opts)`。

### 5.3 `convert-project` 接入

`ConvertProjectOptions` 新增：

```csharp
[Option("no-strip-default", Default = false,
    HelpText = "Disable the default strip-default-parameter preprocessing step")]
public bool NoStripDefault { get; set; }
```

在 `ConvertProject` 中，于 `if (opts.UnsignedType)` **之前**插入：

```csharp
if (!opts.NoStripDefault) { ...ProjectDefaultParameterStripper.PreprocessAsync(...)
    opts.Source = stripResult.PreprocessedSourcePath;
    opts.FingerprintSourcePath ??= stripResult.OriginalSourcePath;
    opts.FingerprintInputRoot ??= stripResult.SourceRoot; }
```

同时：
- 中间目录保留列表追加 `ProjectDefaultParameterStripper.IntermediateDirectoryName`。
- 指纹参数列表追加 `strip-default={...}`。
- TS 目标分支（`opts.Target == "typescript"`，位于 L168）在 Java 专属预处理之前 return —— strip-default 属于 Java 通道，**不**在该分支内触发，因此无需额外处理。

---

## 6. 已知限制（明确不处理）

- **L1 委托声明默认值**：`delegate void D(int a = 1);` 保留不动。委托无重载机制，剥离必然破坏 `d()` 调用。
- **L2 `[Optional]` / `[DefaultParameterValue]` 特性**：无 `= value` 语法，保留不动。
- **L3 `dynamic` 调用点**：无法静态绑定，本就依赖运行时默认值；不处理（D1 已决定不改调用点，且本通道不改调用点，故不受影响）。
- **L4 分部方法**：整体跳过。
- **L5 索引器 / 运算符**：保留不动（C# 中默认值本无效果）。
- **L6 跨程序集（NuGet / BCL）调用省略参数**：依赖被引用程序集自身的重载，与本通道无关。

---

## 7. 测试计划

### 7.1 单文件重写器单元测试

`tests/CSharpToJava.Tests/DefaultParameterStripperTests.cs`（参考 `GotoEliminatorTests.cs` / `GenericPreprocessTests.cs` 风格，先红后绿）：

- Options / Diagnostics / Statistics 记录属性往返
- 无默认值 → `Changed == false`，`FilesSkippedClean == 1`
- 方法：1 个 / 多个默认值 → 生成正确数量与形状的重载
- 构造器：`this(a, 10);` 转发
- static / 泛型（显式类型实参）/ async（剔除 `async`，`Task<T>` 返回）/ virtual / `new`
- abstract → 生成具体转发重载
- interface → 生成 DIM `void M() => M(1);`
- 局部函数 → 生成局部函数重载
- 显式接口实现 → 剥离但不生成重载
- partial / extern → 完全不动
- delegate / 索引器 / operator → 完全不动
- 默认值表达式逐字复制（枚举成员、字符串、null、`default`、`nameof`、负数字面量）
- 冲突：已存在同签名 → 不生成；两个声明生成同签名 → 只生成一次
- 隐藏：基类同签名非抽象 → 带 `new`；基类抽象 → 跳过
- `override` → 不生成重载

### 7.2 项目级集成测试

`tests/CSharpToJava.ProjectTests/StripDefaultProjectTests.cs` + fixture `tests/SampleStripDefaultSolution/`（结构与 `SampleDefaultSolution` 一致：Lib + App + .sln）：

- **跨项目调用点验证（核心）**：`SampleStripDefaultLib` 的方法/构造器含默认值；`SampleStripDefaultApp` 调用时**省略参数**。断言：
  1. 预处理后 App 的调用点源码与原始**逐字一致**（D1）；
  2. Lib 中被剥离且生成了重载；
  3. Pass 3 自检无残留默认值；
  4. 中间目录 `dotnet build -c Release` 退出码 0。
- 无默认值项目 → `FilesTransformed == 0`，产物与源一致。
- 重跑（`Force`）不残留旧产物。
- `CSharpToJava.ProjectTests.csproj` 追加 `SampleStripDefaultSolution` 的 `Content` Include。

### 7.3 `D:\csharp-sdk` 验证

- 以 `strip-default` 动词把 `D:\csharp-sdk\csharp-sdk.sln` 预处理到临时目的地的 `.cs2j-strip-default-src`。
- 对产物执行 `dotnet build -c Release`（必要时 `dotnet test -c Release`），**要求零错误**。
- 完整 stdout/stderr 落盘保存（路径在实现阶段确定，见 §8）。
- 校验 `D:\csharp-sdk` 未被修改（git status 干净）。

### 7.4 回归

- 全量 `dotnet test -c Release`。
- 因 D3 默认开启，需重点核对 `CSharpToJava.ProjectTests` / `CSharpToJava.ConversionTests` 中走 `convert-project` 的用例；若出现回归，逐条定位后在实现阶段处理（必要时为用例显式传 `--no-strip-default`）。

---

## 8. 待实现阶段确定的事项

- 验证日志落盘位置（暂定 `d:/code/cs2j/.cs2j-logs/strip-default-<timestamp>.log`，需核对 `.gitignore`）。
- `SampleStripDefaultSolution` 各项目的 `TargetFramework`（与 `SampleUnsignedSolution` 保持一致）。
- `csharp-sdk` 全量 `dotnet test` 是否纳入门禁（先只做 `build`，若快则加 `test`）。
