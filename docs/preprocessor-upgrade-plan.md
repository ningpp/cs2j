# Preprocessor 能力升级方案

> 版本：2026-11-01  
> 基线：`d:\code\cs2j`，HEAD `78e810c6`（工作区干净）  
> 证据：`D:\cs\202611040733.log`、`D:\cs\cs-sdk-78e810c6` 中间树，以及单 pass 复跑结果  
> 范围：只分析与设计 C# → C# 预处理链，不涉及 C# → Java 转换器的规则实现

---

## 0. 结论摘要

当前预处理链由 **42 个 Implemented、默认开启的 pass** 组成（`preprocess --list-passes` 实测）。本次 SDK 转换结果为：

```text
Conversion complete (MSBuild): 1961 succeeded, 15 failed, 247 resources copied, modules=21
```

15 个失败全部位于 `json` 模块，都是 Java 转换器抛出的 `CS2J normalization required`，可归为 7 类 C# 残留：

| 类别 | 数量 | 代表性残留 | 现 owner pass |
|------|------|-----------|---------------|
| async lambda 未降低 | 6 | `Assert.ThrowsAsync<T>(async () => ...)` | `async-normalize` |
| stackalloc 未降低 | 3 | `cond ? stackalloc byte[n] : array` | `unsafe-ffm` R4 |
| ref struct 未降低 | 2 | `Utf8JsonReader`、`MyRefStruct` | `unsafe-ffm` R7 |
| enum 二元算术 | 1 | `value - lowerBound`（`JsonTokenType`） | `enum-declaration` → `numeric-boundary` |
| ref local 未降低 | 1 | `ref byte reference = ref MemoryMarshal.GetReference(...)` | `ref-out-in` / `unsafe-ffm` |
| tuple/解构局部声明 | 1 | `(TItem1, TItem2) value = default;` | `tuple` |
| 指针解引用 | 1 | `return *text;` | `unsafe-ffm` R5/R6 |

核心判断：**这不是“少一个 preprocessor”那么简单，而是 pass 契约、编译环境、失败策略、链末验证四个层面的结构性缺口**。

1. **契约缺口**：`PassDescriptor` 只描述 id、顺序、开关和一句摘要，没有声明“我消费什么、产出什么、残留由谁负责”。`enum-declaration` 把 enum 算术 `Deferred` 给 `numeric-boundary`，但后者没有普通 enum 算术规则，形成无人承接的死角。
2. **编译环境缺口**：语义 pass 默认共用一份只含 TPA 运行时程序集的合并编译，**不按工程加载 NuGet / ProjectReference 的真实引用集**（只有 `extension-methods` 等少数 adapter 额外补了 restored NuGet 程序集，`async-normalize` 没有）。`Assert.ThrowsAsync` 绑定失败 → async lambda 的 `ConvertedType` 为 null → 无法提升 → 降级为 warning。
3. **失败策略缺口**：`unsafe-ffm` 明确把 Java 收口“后置到转换阶段”，逐文件拒绝时原样保留；`async-normalize` 严格模式下整文件 all-or-nothing。两者都让残留静默穿过链。
4. **验证缺口**：链的成功标准是“C# 中间树可编译”，不是“final tree 可 Java 转换”；链末没有 `NormalizationRequired` 的对应审计，只有等到 Java 转换器逐类型抛错。

升级方案分五层：**契约层（Pass Contract v2）→ 编译层（真实项目引用）→ 验证层（链末 normalization-audit）→ 能力层（7 类残留的 lowering）→ 性能层（单树/增量）**。按证据驱动分 5 个波次实施，验收标准是 `preprocess` 的 `java-ready` profile 残差为 0，且本语料 Java 转换失败数 15 → 0。

---

## 1. 复现与证据

### 1.1 复现命令

```powershell
dotnet run -c Release --project src/CSharpToJava.CLI/CSharpToJava.CLI.csproj -- convert-project `
  -s D:\csharp-sdk\csharp-sdk.sln `
  -d d:\cs\cs-sdk-78e810c6 `
  --no-verify-build --no-verify-tests
```

pass 顺序：

```powershell
dotnet run -c Release --project src/CSharpToJava.CLI/CSharpToJava.CLI.csproj -- preprocess --list-passes
```

单 pass 复跑（用于定位静默残留）：

```powershell
dotnet run -c Release --project src/CSharpToJava.CLI/CSharpToJava.CLI.csproj -- preprocess `
  -s D:\cs\cs-sdk-78e810c6\.cs2j-java-keywords-src `
  -d D:\cs\tmp-unsafe-check `
  --no-verify-build --no-verify-tests --pass unsafe-ffm -v
```

### 1.2 失败分布

15 个失败全部是 `json` 模块的 Java 类型转换失败；全部 21 个模块中 1961 个类型成功。日志行 365 给出总数，行 4-363 给出逐类型错误。失败文件的最终中间树位于：

```text
D:\cs\cs-sdk-78e810c6\.cs2j-java-keywords-src\json\...
```

### 1.3 关键单 pass 复跑证据

`unsafe-ffm` 单独复跑（输入为最终树）输出：

```text
0 explicit stackalloc(s), 0 implicit stackalloc(s) heap-allocated
68 rejection(s) (UF020=37, UF021=20, UF040=11, UF050=0, UF052=1, ...)
```

但同一输入里 `JsonReaderHelper.Unescaping.cs` 含 **9 处 stackalloc**，该文件被标记为 `clean (copy)`，既无改写也无诊断。与此同时 `Utf8JsonWriter.WriteProperties.Bytes.cs` 的 11 处裸指针 stackalloc 报 `UF040` 后保留原文。**同一轮 pass 中，同一构造出现“静默跳过”和“显式拒绝”两种结果，且都不阻止链成功。**

`async-normalize` 单独复跑（输入为最终树）输出：

```text
0 lambda(s) lifted, ..., 25 skipped, ..., 25 residual
[Warning] ... target delegate type of the async lambda cannot be bound from the
SemanticModel (GetTypeInfo(lambda).ConvertedType); explicit 'new DelegateType(...)'
lifting is impossible without it.
```

即：不是“规则不会写”，而是**语义模型绑不到真实委托类型**，规则主动跳过并只发 warning。

---

## 2. 当前 preprocess 架构与契约

### 2.1 注册表与执行顺序

- 42 个 descriptor 的单一事实源在 [PassCatalog.cs](file:///D:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassCatalog.cs#L45-L681)。
- 顺序由 `RunAfterIds` 的拓扑排序决定，同层按声明顺序做确定性 tiebreak：[PassRegistry.cs](file:///D:/code/cs2j/src/CSharpToJava.Core/Preprocessing/PassRegistry.cs#L95-L133)。
- `PassStage` 仅用于 `--list-passes` 分组展示，**不参与排序**：[PassDescriptor.cs](file:///D:/code/cs2j/src/CSharpToJava.Core/Preprocessing/PassDescriptor.cs#L8-L30)。
- 一个 pass 的开关优先级：descriptor 默认 → legacy CLI 开关 → `--enable-pass` / `--disable-pass`：[PassPlanResolver.cs](file:///D:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassPlanResolver.cs#L67-L90)。
- `--pass X` 的语义是“只跑 X”：先把所有 pass 置为关闭，再显式启用所选集合：[Program.cs](file:///D:/code/cs2j/src/CSharpToJava.CLI/Program.cs#L4045-L4053)。

### 2.2 单轮执行模型

`ProjectPassRunner` 是所有 pass 的统一执行引擎，入口 [ProjectPassRunner.cs](file:///D:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/ProjectPassRunner.cs#L28-L74)，流程：

```text
解析 source layout（目录 / .csproj / .sln / .slnx）
→ 计算中间目录（.cs2j-<pass-id>-src，或统一 .cs2j-preprocessed-src）
→ 加载工程图 + 过滤不支持工程
→ 枚举源文件（含非 .cs 资源）
→ 严格语法门禁（任一 parse Error 直接失败，不落盘）
→ 复制工程文件/祖先 MSBuild 文件到中间树
→ 构建整树 CSharpCompilation（可选）
→ 逐文件 RewriteFile → AfterFiles
→ restore → verify build/test → 清理
```

关键点：

- **默认每个 pass 一份完整树拷贝**：42 个 pass 产生 42 个 `.cs2j-*-src` 目录；`--unified-preprocessed-src` 才改为单树原地改写：[ProjectPassRunner.cs](file:///D:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/ProjectPassRunner.cs#L103-L159)。
- 目录已存在时默认删除重建，`Force=false` 才报错：[ProjectPassRunner.cs](file:///D:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/ProjectPassRunner.cs#L138-L152)。
- `PassFileRewrite{OutputCode, Changed, PreserveBom}` 是文件级输出契约；`Changed=false` 等于逐字节复制：[ProjectPassAdapter.cs](file:///D:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/ProjectPassAdapter.cs#L49-L59)。
- 自定义多阶段 pass 可覆写 `ExecuteAsync`，直接调用 `BuildStandardCompilation` / `CopyVerbatim` / `WriteRewrittenAsync` / `VerifyBuildAndTestsAsync` 等 stage helper：[ProjectPassAdapter.cs](file:///D:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/ProjectPassAdapter.cs#L226-L267)。

### 2.3 每个 pass 的输入/输出钩子

统一契约在 [ProjectPassAdapter.cs](file:///D:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/ProjectPassAdapter.cs#L413-L522)：

| 钩子 | 输入 | 输出/语义 |
|------|------|-----------|
| `OnTreePreparedAsync(PassTreeContext)` | 已复制、未编译的整树 | 预处理工程级信息（如测试工程目录） |
| `CreateParseOptions` | 中间树 | `CSharpParseOptions`；默认 `NETCOREAPP` 符号 |
| `GetMetadataReferences` | - | 默认 TPA 运行时程序集；见 2.6 |
| `CreateCompilationOptions` | - | 默认 `DynamicallyLinkedLibrary, allowUnsafe=true` |
| `CreateAdditionalSyntaxTrees` | 工程图 | 合成 `__cs2j_global_usings.g.cs`；不包含 NuGet 引用 |
| `CompilationReadyAsync(compilation)` | 整树编译 | 一次性分析（plan/registry） |
| `RewriteFileAsync(context)` | `SourceCode` + 可选 `SemanticModel` | `PassFileRewrite?`；null=复制 |
| `AfterFilesAsync(tree)` | 全部改写完成 | 注入支持库、分发生成文件 |
| `OnRestoreFailed/OnBuildFailed/OnTestsFailed` | verify 结果 | pass 自己的诊断/统计 |
| `VerifyBuildAndTestsAsync` | 中间树 | `dotnet build` 后 `dotnet test`，仅当对应开关开启 |

### 2.4 验证契约

- 每个 descriptor 有 `VerifyBuildByDefault` / `VerifyTestsByDefault`；`--no-verify-build` / `--no-verify-tests` 全局关闭：[ProjectPassAdapter.cs](file:///D:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/ProjectPassAdapter.cs#L294-L313)。
- 验证在**该 pass 的中间树**上执行 `dotnet restore` → `dotnet build` → `dotnet test`：[ProjectPassRunner.cs](file:///D:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/ProjectPassRunner.cs#L631-L698)。
- **验证的对象是 C#，不是 Java 可转换性**。链成功只证明中间 C# 可编译；`CS2J normalization required` 不在任何 preprocess 校验中。
- 本次命令显式关闭两种验证，因此链的“成功”只代表 15 个失败类型之外的部分可编译；这不是本次失败的唯一原因，但会掩盖中间树回归。

### 2.5 诊断与日志

- 框架级诊断：`CS2J9001`（启用 Stub）、`CS2J9002`（未知 pass id）：[PreprocessDiagnosticCodes.cs](file:///D:/code/cs2j/src/CSharpToJava.Core/Preprocessing/PreprocessDiagnosticCodes.cs#L8-L15)。
- 各 pass 自带诊断码前缀：`UF0xx`（unsafe-ffm）、`ED0xx`（enum-declaration）、`ROI0xx`（ref-out-in）、`CS2J18xx`（async-normalize）、`CS2J53xx`（numeric-boundary）、`CS2J04xx`（iterator-lowering）等。
- 控制台统计格式由 [CliPreprocessChain.cs](file:///D:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/CliPreprocessChain.cs#L388-L390) 生成：

```text
  [pass.id] total: 12.34s | compile=..., rewrite=..., prepare=..., execute=...
```

- 失败即中止后续 pass：[CliPreprocessChain.cs](file:///D:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/CliPreprocessChain.cs#L377-L386)。
- **诊断只存在于控制台/适配器内存，没有每 pass 的 JSON 报告落盘**（只有 variance-audit、dynamic-desugar、platform-isolation、linq-channel 写各自报告）。

### 2.6 编译环境：当前语义 pass 看到的不是真实工程

`BuildStandardCompilation` 使用 `adapter.GetMetadataReferences()`：[ProjectPassRunner.cs](file:///D:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/ProjectPassRunner.cs#L307-L311)。默认实现是 TPA 运行时程序集列表：[ProjectUnsignedTypePreprocessor.cs](file:///D:/code/cs2j/src/CSharpToJava.CLI/ProjectUnsignedTypePreprocessor.cs#L81-L105)，**不读取 `project.assets.json`、不解析 `<PackageReference>`、不加载项目间引用、不按项目区分引用集**。

少数 adapter 会额外补充引用（例如 `ExtensionMethodResolvePassAdapter` 加载 restored NuGet 编译程序集，[ExtensionMethodResolvePassAdapter.cs](file:///D:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/Adapters/ExtensionMethodResolvePassAdapter.cs#L200-L210)；`DynamicDesugarPassAdapter` 补 `HintPath` 程序集）。但这是逐 adapter 的补丁，**没有统一的真实引用集**，`async-normalize` 仍走默认 TPA。

这带来一个系统性后果：凡是依赖 NuGet 类型才能绑定的语法（典型是 xUnit 泛型方法里的 async lambda），语义 pass 看到的是“未绑定”的树，只能跳过。`async-normalize` 的 25 个 skipped 就是直接证据。

---

## 3. 42 个 preprocessor 的输入/输出全景

> 说明：`Stage` 仅作分组展示；真实顺序按 `--list-passes`。表中“观察”只列本次证据或代码中明确的边界，不臆测未证实行为。

### 3.1 Lexical

| Pass | 输入形态 | 输出形态 | 支持库 | 观察/边界 |
|------|----------|----------|--------|-----------|
| `conditional` | `#if/#elif/#else/#endif`（按项目符号集）、可选 `#region` | 只保留存活分支的源码文本；指令剥除；`#warning` 保留并告警 | - | 纯文本、无语义模型；parse Error 或后校验失败→原文件不变 |
| `lexical-normalize` | `#line`、`#error`、extern alias、global using、file-scoped namespace、raw/u8/verbatim/`\e` 字面量（opt-in） | 规范化文本 | - | 纯语法、幂等；guard 诊断 + 后校验失败回滚 |
| `partial-merge` | 跨文件 `partial` 类型声明 | 合并到单一类型声明，带 `cs2j-partial-merge` 标记 | - | 整树分析；合并残留 `CheckResidual` 诊断 |

### 3.2 Declarative

| Pass | 输入形态 | 输出形态 | 支持库 | 观察/边界 |
|------|----------|----------|--------|-----------|
| `local-function-lift` | 局部函数（值捕获/引用捕获/静态/嵌套） | 类级 `private static` 方法；捕获降级为方法参数（引用捕获用 `ref` 实参） | - | 严格模式残留可失败；名称表跨文件复用 |
| `enum-declaration` | `[Flags]`、非 int 底层、显式值 enum，全树使用点 | enum-like sealed class + 静态字段/`GetValue`/`FromValue` | - | 不可表示→`ED020` 保真 enum；`E +/- n` 被 `Deferred` 给 numeric-boundary（见 4.4） |
| `delegate` | 委托 `+ - += -=`、匿名方法 | `CsPrelude.Combine/Remove`、lambda | `Cs2jPrelude` | 不支持形态 skip + residual 统计 |
| `tuple` | 元组类型/字面量/命名元素/解构声明/赋值/`out var`/foreach 解构/`ValueTuple` 工厂 | 生成的 `Cs2jTuples.TupleXxx` struct；形状跨文件去重 | `Cs2jTuples` | **开放泛型元组（元素为类型参数）未覆盖**（本次证据） |
| `anonymous-type-lift` | `new { X = ... }`、投影缩写 | 生成 `internal sealed` 不可变类 | - | 形状按属性序列+类型结构键去重 |
| `strip-default` | 默认参数、`params` 集合 | 生成重载；调用点改具名实参 | - | 无 body/签名冲突/隐藏 abstract 跳过；含 C48 子规则 |
| `make-readonly` | 可变 struct | readonly struct + 构造器归一 + 变异点/调用点改写 | - | 整树分析；写 `.cs2j-iface-members.json`；支持 force overrides |
| `attribute-normalize` | `Caller*` 调用点、JSON/序列化/PInvoke 属性 | 注入 `Caller*` 实参；属性盘点 | - | C10；plan 排除后声明端属性合法保留 |
| `struct-value-semantics` | struct 拷贝/变异/readonly 边界 | 防御性 Clone 插入 | - | C07；计划级回退 `SV030/SV031` |
| `record-readonly` | 可变 record class/struct | readonly record + 变异点/接口 ripple | - | 三阶段 + 计划级原子回退 |
| `thread-static` | `[ThreadStatic]` 字段 | `ThreadLocal<T>` holder + `.Value` | - | C09；字段/站点平面统计 |
| `concurrency-normalize` | `Monitor.Enter/Exit/TryEnter/Wait/Pulse`、`Volatile`、`System.Threading.Lock` | 规范 `lock` 形态 / 显式 enter-try-finally / compat 桥 | - | C29；TryEnter 显式不支持（CS2J2911） |
| `member-desugar` | 属性、索引器、事件、运算符、finalizer、签名、`field` 关键字、record `with/init/required` | backing field + 显式方法/成员 | - | C11-C14、C27、C39、C46、C50 |

### 3.3 Expression

| Pass | 输入形态 | 输出形态 | 支持库 | 观察/边界 |
|------|----------|----------|--------|-----------|
| `null-coalesce` | `??`、`??=` | 三元 / `if` 赋值；复杂 receiver 提临时；表达式体成员展开 | - | 字段/属性初始化器没有宿主语句块，实现注释明确“intentionally stay residual” |
| `expression-desugar` | C23 插值字符串、C24 对象/集合/目标类型 `new` 初始化器、C35 `?.`/`?[]` 读取、C51 空条件赋值、C36 集合表达式 | 显式表达式/语句 + 共享 `__ed_` 临时 | - | strict 下任一 Error 整文件不改；residual gate `0589`（CE-12） |
| `enum` | enum 上的 `\| & ^ ~`、复合赋值、`HasFlag`、可空 enum | 底层整数运算 + `(E)` cast | - | 只做运算层，不碰声明；窄底层包 `unchecked` |
| `foreach-deconstruction` | `foreach (var (k,v) in KVP/自定义 Deconstruct)` | 普通 foreach + 显式局部 | - | Tuple 族显式跳过（要求先跑 `tuple`）；非 KV/Deconstruct 残余 |
| `array-method` | 数组实例/静态方法、多维数组、`Array.Resize` 等 | `Cs2jArray.ArrayHelper` 静态调用 | `Cs2jArray` | 仅纯数组形态；扩展方法绑定场景排除 |
| `index-range` | `^`、`..` | 基于长度的 int 索引、`(offset, count)` | `Cs2jArray` | C31；clean 文件字节保真 |
| `numeric-boundary` | `checked/unchecked`、decimal 运算、`++/--` 复杂目标、可空数值提升、特殊数值 | 显式 helper/guard/cast | `Cs2jUnsignedMath` | C15/C34/C17/C22；**无普通 enum 算术规则** |
| `extension-methods` | 扩展方法调用 `x.M(a)`、C#14 extension block | 静态 host 调用 `global::Host.M(x, a)` | - | C19/C49；LINQ host 排除；加载 restored NuGet 引用 |
| `equality-concat` | `string ==/!=`、字符串 `+`、`(object)` 逃逸、委托相等、record `==` | `string.Equals` / `string.Concat` / `ReferenceEquals` / `DelegateOps.Equals` / `object.Equals` | `Cs2jPrelude` | C47 |

### 3.4 ControlFlow

| Pass | 输入形态 | 输出形态 | 支持库 | 观察/边界 |
|------|----------|----------|--------|-----------|
| `foreach-source` | `foreach` over string、残余解构、enumerator adapter、`ref` foreach | `ToCharArray()` / 显式适配循环 | - | C16/C40；无 foreach 语法直接字节复制 |
| `pattern-desugar` | pattern switch、非整数 governing switch、`catch when`、类型参数 catch | if 链 / 布尔表达式 / guarded catch | - | C03/C37；残留目标 `PD020` |
| `eliminate-goto` | `goto`/label/`goto case/default` | 结构化循环/状态机 | - | 无语义模型；clean 文件字节保真 |
| `linq-channel` | LINQ 查询表达式、方法链 | 过程化 `foreach`/循环；`IQueryable`、`Expression<>` 链冻结 | - | C26；三路路由，二次 desugar 幂等检查 |

### 3.5 StateMachine

| Pass | 输入形态 | 输出形态 | 支持库 | 观察/边界 |
|------|----------|----------|--------|-----------|
| `closure-lift` | 可变捕获的局部/参数、lambda/匿名方法 | holder 类 + display class + 命名方法 | - | C32/C45；递归 lambda 拒绝 |
| `iterator-lowering` | `yield` 迭代器、try/finally/using/lock、非跨越 ref struct/ref/pointer 局部 | 显式状态机类 | - | C04；不支持形态 `CS2J0402-0406` |
| `async-normalize` | async lambda/局部函数、`ConfigureAwait`、`await foreach/using`、async iterator | 命名 async 方法、显式 `IAsyncEnumerator`、async 状态机；`await` 站点保留 | - | C18/C38；**文件级 all-or-nothing**；委托类型绑定失败即跳过 |

### 3.6 Abi

| Pass | 输入形态 | 输出形态 | 支持库 | 观察/边界 |
|------|----------|----------|--------|-----------|
| `ref-out-in` | `ref/out/in` 参数、ref 局部/返回/字段、委托 ref 参数、ref struct holder、数组元素代理 | `ByRef<T>` / holder / accessor 代理 | `Cs2jRefOutIn` | C01；**外部元数据/extern/接口虚实现的 ref 契约锁定不改** |
| `unsigned` | `byte/ushort/uint/ulong` 关键字与运算 | `System.*` 全名 + `Cs2jUnsigned.UnsignedMath` | `Cs2jUnsignedMath` | 声明/字面量/cast/泛型实参/const/enum/后缀自增 |
| `generic` | `default(T)`、`new T()`、`new T[n]` | `default` 字面量 / `Activator.CreateInstance<T>` / `Array.CreateInstance` | - | `typeof(T)/is/as T` 仅统计，交 `generic-deepen` |
| `generic-deepen` | `typeof(T)`、`is/as T`、泛型静态字段、锚点 | 显式 `Type` 构造/锚点/封闭元数据 | - | C02 |
| `closed-metadata` | 封闭泛型类型注册、序列化入口 | `_Cs2jClosedTypes.<Project>.cs` + bootstrap guard | - | C30；无 holder-name resolver 时保持惰性 |
| `variance-audit` | `out/in` 型变、约束 | 只读 JSON 报告 | - | C33；从不改写 |
| `unsafe-ffm` | `unsafe`、`fixed`、`stackalloc`、指针、extern、ref struct | 剥 unsafe、数组化、索引化、FFM facade、ref struct 降级/拒绝 | - | C06；拒绝→原文件逐字节保留（Java 门禁后置） |
| `platform-isolation` | 平台/互操作边界 API | inventory / Reject / Stub / Facade | - | C42；默认 Report（只审计） |
| `dynamic-desugar` | `dynamic` 站点 | audit inventory；Rewrite 仅可证 P1 子集 | - | C25；默认 Audit，不改写 |

### 3.7 FinalNaming

| Pass | 输入形态 | 输出形态 | 支持库 | 观察/边界 |
|------|----------|----------|--------|-----------|
| `java-keywords` | Java 保留字标识符、`operator true/false`、`nameof` | 改名 + `IsTrue/IsFalse` + `nameof` 字面量化 | - | C08；链末，改完即交给 Java 转换 |

### 3.8 pass 间的共享产物

| 产物 | 生产者 | 消费者 | 形态 |
|------|--------|--------|------|
| `Cs2jPrelude` | `delegate` / `equality-concat` | 后续 C# 树 + Java 侧 | 源码注入 |
| `Cs2jTuples` | `tuple` | 后续 C# 树 | 生成 struct 文件 |
| `Cs2jRefOutIn` | `ref-out-in` | 后续 C# 树 | 源码注入 |
| `Cs2jUnsignedMath` | `unsigned` / `numeric-boundary` | 后续 C# 树 | 源码注入 |
| `Cs2jArray` | `array-method` / `index-range` | 后续 C# 树 | 源码注入 |
| 匿名类型 | `anonymous-type-lift` | 后续 C# 树 | 生成 `internal sealed` 文件 |
| `_Cs2jClosedTypes.*.cs` | `closed-metadata` | Java 侧反射 | 每工程一个登记文件 |
| `.cs2j-iface-members.json` | `make-readonly` | Java 侧接口元数据 | JSON |
| `variance-audit-report.json` | `variance-audit` | 人工/下游 | JSON |
| `dynamic-audit-report.json` | `dynamic-desugar` | 人工/下游 | JSON |
| 平台边界 inventory | `platform-isolation` | 人工/下游 | JSON |
| LINQ 报告 | `linq-channel` | 人工/下游 | 报告 |

**没有统一的生成物清单，也没有 pass 级执行报告**；每个生成物由各自 adapter 分散落盘。

### 3.9 关键顺序依赖（为什么必须是这个顺序）

| 生产者 → 消费者 | 依赖原因 |
|-----------------|----------|
| `conditional` → 全部 | 后续语义分析只能看到存活分支 |
| `lexical-normalize` → `partial-merge` / 语义 pass | 指令、file-scoped namespace、字面量先归一，避免解析/绑定歧义 |
| `partial-merge` → 所有依赖完整符号的语义 pass（直接或经中间 pass 传递） | 跨文件 `partial` 必须先合并，整树符号分析才完整 |
| `local-function-lift` → `null-coalesce` 等表达式 pass | 局部函数体被搬到方法级后，表达式改写只需处理一个位置 |
| `enum` → `enum-declaration` | 位运算先降到整数域，声明改写只需处理声明与剩余使用点 |
| `delegate` → `tuple` | 委托组合/匿名方法先归一，元组形状注册不被委托语法干扰 |
| `tuple` → `foreach-deconstruction` / `anonymous-type-lift` / `equality-concat` | 元组解构/形状先落地；匿名类型形状和相等语义随后处理 |
| `ref-out-in` → `unsigned` / `closure-lift` | `ByRef` 包装影响无符号调用与捕获形态；先定 ref 形态 |
| `unsigned` → `numeric-boundary` | 无符号底层类型先映射，checked/边界规则才有一致的域 |
| `generic` → `thread-static` / `generic-deepen` / `record-readonly` | 泛型基本改写先完成，线程静态/泛型加深/record 才看到稳定签名 |
| `generic-deepen` → `closed-metadata` / `attribute-normalize` | 封闭类型锚点先建立，元数据登记与属性注入才有目标 |
| `make-readonly` → `struct-value-semantics` / `attribute-normalize` / `unsafe-ffm` | readonly 形态决定后续 struct 拷贝、P/Invoke 与 unsafe 边界 |
| `struct-value-semantics` → `unsafe-ffm` / `member-desugar` | 值语义 clone 先插入，成员去糖不会破坏拷贝边界 |
| `closure-lift` → `iterator-lowering` / `async-normalize` | 捕获先提升，状态机只处理 holder/display class |
| `iterator-lowering` → `extension-methods` | 迭代器状态机中的调用点也要显式化 |
| `extension-methods` → `linq-channel` / `java-keywords` | 扩展调用先变静态 host 调用，LINQ 路由和命名才看到真实符号 |
| `equality-concat` → `java-keywords` | 相等/拼接先物化为 BCL 调用，命名不再影响语义 |
| `java-keywords` → （转换） | 命名安全必须最后做，避免后续 pass 再次引入冲突名 |

这张表也解释了为什么 `PassStage` 不能当排序依据：例如 `enum`（Expression）必须先于 `enum-declaration`（Declarative），`closure-lift`（StateMachine）必须先于 `iterator-lowering`（StateMachine）而 `async-normalize` 又依赖 `closure-lift`。

---

## 4. 本次失败的逐类归因

### 4.1 async lambda：语义模型绑不到委托类型，规则主动跳过

- 残留：`json\tests\JsonDocumentTests.cs:378`、`Serialization\Null.ReadTests.cs:193`、`Stream.Collections.cs`、`WriteValueTests.cs`、`ConstructorTests.*`、`CollectionTests.Dictionary.*`，共 6 个类型。
- Java 门禁：[LambdaTransformer.cs](file:///D:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/LambdaTransformer.cs#L52-L53)。
- 单 pass 复跑给出准确原因：`GetTypeInfo(lambda).ConvertedType` 为空，所以无法生成显式 `new DelegateType(...)`；25 个 async lambda/局部函数 skipped，25 个 residual，**只发 Warning**。
- 为什么 `ConvertedType` 为空：pass 编译只含 TPA 引用，测试工程的 xUnit 程序集不在编译引用中，`Assert.ThrowsAsync<T>` 无法绑定，lambda 自然没有目标委托类型。
- 次因：`async-normalize` 在 strict 模式下“任一 Error → 整文件不改写”：[AsyncNormalizePreprocessor.cs](file:///D:/code/cs2j/src/CSharpToJava.Core/AsyncNormalize/AsyncNormalizePreprocessor.cs#L72-L103)。一个不可绑定节点会连带阻断同文件其他可提升节点。

### 4.2 stackalloc：一条路径静默跳过，一条路径显式拒绝，二者都不阻断链

- 残留：`JsonReaderHelper.Unescaping.cs:21`、`JsonWriterHelper.Date.cs:14`、`Utf8JsonWriter.WriteProperties.Bytes.cs:36`。
- Java 门禁：[ObjectCreationTransformer.cs](file:///D:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/ObjectCreationTransformer.cs#L548-L549)。
- 复跑事实：
  - `JsonReaderHelper.Unescaping.cs` 含 9 处 stackalloc，但 R4 统计 `0 explicit stackalloc(s)`，文件 `clean (copy)`，无任何 `UF040/UF010` 诊断。
  - `Utf8JsonWriter.WriteProperties.Bytes.cs` 的 11 处裸指针 stackalloc 全部 `UF040`（“cursor argument 'ptr' is not passed to a supported method invocation”），文件原文保留。
  - R4 的拒绝预检与 residual gate 代码存在：[UnsafeFfmPreprocessor.cs](file:///D:/code/cs2j/src/CSharpToJava.Core/UnsafeFfm/UnsafeFfmPreprocessor.cs#L340-L350)、[#L530-L561](file:///D:/code/cs2j/src/CSharpToJava.Core/UnsafeFfm/UnsafeFfmPreprocessor.cs#L530-L561)。但“Span 目标的三目表达式”既没被收集，也没触发 residual gate。
- 结论：R4 的**站点识别覆盖**和**静默跳过可观测性**同时不足。前者需要补形状，后者需要链末审计。

### 4.3 ref struct：UF050 拒绝后原样保留，Java 侧直接硬失败

- 残留：`Utf8JsonReader.MultiSegment.cs:27`（生产类型）、`InvalidTypeTests.cs:167`（测试内 `MyRefStruct`）。
- Java 门禁：[StructTransformer.cs](file:///D:/code/cs2j/src/CSharpToJava.Core/Transformers/Type/StructTransformer.cs#L60-L63)。
- `unsafe-ffm` R7 对“带 ref-only 成员的 ref struct”发 `UF050` 并逐文件回滚；对“纯数据型 ref struct”走 `UF052` 去 `ref` 降级。测试工程 UF050 被降级为 warning：[UnsafeFfmPassAdapter.cs](file:///D:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/Adapters/UnsafeFfmPassAdapter.cs#L85-L93)。
- 适配器注释明确把 Java 收口推给转换阶段：[UnsafeFfmPassAdapter.cs](file:///D:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/Adapters/UnsafeFfmPassAdapter.cs#L95-L103)。
- 因此 `Utf8JsonReader` 这类 Java 无对应物的类型**没有任何链前失败信号**。`InvalidTypeTests` 同文件还有 UF020 指针拒绝（复跑日志第 151 行），最终树仍是 `ref struct MyRefStruct`，说明该文件的 UF052 降级结果没有落盘（整文件回滚或后续未写回）；具体路径需要按文件级诊断报告确认。

### 4.4 enum 二元算术：Deferred 给了一个不存在的承接者

- 残留：`JsonHelpers.Date.cs:915`：

```csharp
public static bool IsInRangeInclusive(JsonTokenType value, JsonTokenType lowerBound, JsonTokenType upperBound)
    => (value - lowerBound) <= (upperBound - lowerBound);
```

- `JsonTokenType` 在最终树仍是真 enum（`public enum JsonTokenType : global::System.Byte`）。
- `enum-declaration` analyzer 对 `E +/- E`、`E +/- n`、`n +/- E` 的处理是 **`Defer(plan, node, "enum +/- arithmetic lowered later by numeric-boundary.")`**：[EnumDeclarationAnalyzer.cs](file:///D:/code/cs2j/src/CSharpToJava.Core/EnumDeclaration/EnumDeclarationAnalyzer.cs#L1321-L1345)。`Defer` 会把 plan 从 `Converted` 降为 `Deferred`：[#L508-L516](file:///D:/code/cs2j/src/CSharpToJava.Core/EnumDeclaration/EnumDeclarationAnalyzer.cs#L508-L516)。
- `numeric-boundary` 只有 nullable enum 的 backstop（`NullableLiftRule` / `NullableLiftRewriter`），没有普通 enum 算术规则；搜索 `NumericBoundary/**` 没有 `EnumArithmetic` 规则。
- 结果：enum 没被转成 class，算术也没被降级，形成**双向死角**。这是“顺序锚点 ≠ 语义契约”的典型样本。

### 4.5 ref local：外部 ref + Unsafe 内在函数链无人承接

- 残留：`JsonClassInfo.Cache.cs:373-387`：

```csharp
ref global::System.Byte reference = ref MemoryMarshal.GetReference(name);
key = (ulong)Unsafe.ReadUnaligned<global::System.UInt64>(ref reference) ...
```

- Java 门禁：[StatementTransformer.Declarations.cs](file:///D:/code/cs2j/src/CSharpToJava.Core/Transformers/Statement/StatementTransformer.Declarations.cs#L27-L31)。
- `ref-out-in` 的设计把“外部元数据符号 / extern / 接口虚实现的 ref 契约”锁定不改（C01 设计 D1/D6）；`unsafe-ffm` 的 R5/R6 只处理指针签名与指针表达式，不处理 `ref` 局部 + `Unsafe.*(ref ...)` 内在函数。
- 这是一个**跨 pass 的第三类构造**：既是 ref local，又是指针/内在函数语义；当前没有 owner。

### 4.6 tuple/解构局部声明：开放泛型 tuple 未覆盖，Java 门禁还误分类

- 残留：`CustomConverterTests.NullableTypes.cs:521`：

```csharp
protected class ValueTupleConverter<TItem1, TItem2> : JsonConverter<ValueTuple<TItem1, TItem2>>
{
    public override (TItem1, TItem2) Read(...) { ... }
    (TItem1, TItem2) value = default;
```

- `tuple` pass 在 `.cs2j-tuple-src` 阶段对上述位置仍是 `(TItem1, TItem2)`，说明**开放泛型 tuple（元素类型为类型参数）没有被形状注册/改写**。
- Java 门禁把所有 `TupleTypeSyntax` 局部声明都报成“deconstruction local declaration”，即使这里只是普通 tuple 类型变量：[StatementTransformer.Declarations.cs](file:///D:/code/cs2j/src/CSharpToJava.Core/Transformers/Statement/StatementTransformer.Declarations.cs#L33-L37)。
- 这里有两个问题：tuple 覆盖缺口 + Java 门禁分类错误。升级方案应两者都修，且以“final tree 不含 TupleTypeSyntax”为准。

### 4.7 指针解引用：外部指针签名 override 被保留后无人接管

- 残留：`Utf8JsonWriterTests.cs:127-130`：

```csharp
public override unsafe int FindFirstCharacterToEncode(char* text, int textLength)
{
    return *text;
}
```

- 该方法是外部抽象类 `JavaScriptEncoder` 的 override；R5 对“继承外部指针签名”的成员打 `PreservedPointerSignatureAnnotation` 并原样保留：[PointerSignatureRewriter.cs](file:///D:/code/cs2j/src/CSharpToJava.Core/UnsafeFfm/PointerSignatureRewriter.cs#L91-L98)。R6 不会再索引化被保留的子树：[PointerExprRewriter.cs](file:///D:/code/cs2j/src/CSharpToJava.Core/UnsafeFfm/PointerExprRewriter.cs#L120-L123)。
- Java 门禁：[UnaryExpressionTransformer.cs](file:///D:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/UnaryExpressionTransformer.cs#L50)。
- 对外部指针契约，当前策略是“保留 + 交给转换阶段”，没有“生成托管 shim”或“链前显式声明不支持”。

### 4.8 归因汇总

| 现象 | 直接原因 | 结构原因 |
|------|---------|---------|
| async lambda 6 个 | 委托类型绑定失败 + 文件级 short-circuit | pass 编译缺真实引用；失败降级 |
| stackalloc 3 个 | R4 形状未覆盖/静默跳过 + 裸指针拒绝 | 无链末审计；拒绝即保留 |
| ref struct 2 个 | UF050 拒绝 + 测试降级 | Java 收口后置；无域策略 |
| enum 算术 1 个 | Deferred 无承接 | 顺序锚点无契约 |
| ref local 1 个 | 外部 ref + Unsafe 无人承接 | pass 间职责空白 |
| tuple 1 个 | 开放泛型 tuple 未覆盖 | 覆盖矩阵缺失 |
| 指针解引用 1 个 | 外部指针 override 保留 | 无 shim/无显式不支持 |

---

## 5. 升级方案

### 5.1 目标不变量

升级后，预处理链必须同时满足四条不变量：

1. **I1 C# 可编译**：任一 pass 的中间树 `dotnet build -c Release` 无错误。
2. **I2 C# 可测试**：默认链的最终树 `dotnet test -c Release` 无失败。
3. **I3 Java 可转换**：最终树中不存在 `NormalizationRequired` 构造，除非该构造被登记为“显式不支持域”，并带 owner、文件、行、原因。
4. **I4 确定性/幂等**：同一输入 + 同一 options，二次运行输出逐字节一致。

当前只保证了 I1 的一部分（且本次还关闭了验证），I3 完全缺失。

### 5.2 分层升级

```text
L0 契约层   PassDescriptor v2 / ResidualRegistry / NormalizationContract
L1 编译层   真实工程引用 / 每项目编译 / 共享分析缓存
L2 验证层   单 pass 自检 + 链末 normalization-audit + java-ready profile
L3 能力层   7 类残留的 lowering pass / 规则扩展
L4 性能层   单树默认 / 增量缓存 / 选择性 verify / 生成物清单
```

### 5.3 Pass Contract v2

`PassDescriptor` 当前字段只有 id、目录、stage、status、默认开关、runAfter、supportLibrary、needsSemanticModel、verify 默认值、一句 description、catalogItems：[PassDescriptor.cs](file:///D:/code/cs2j/src/CSharpToJava.Core/Preprocessing/PassDescriptor.cs#L65-L141)。建议增加机器可读契约：

```csharp
public sealed record PassContract(
    IReadOnlyList<string> Consumes,        // 本 pass 消除的构造 ID
    IReadOnlyList<string> Produces,        // 本 pass 引入、供后续消费的构造 ID
    IReadOnlyList<string> Preconditions,   // 运行前必须已不存在的构造
    IReadOnlyList<string> Postconditions,  // 运行后必须不存在的构造
    IReadOnlyList<string> MayLeave,        // 允许残留，但必须由 Owner 接管
    IReadOnlyList<string> Owners,          // MayLeave 的承接者
    ResidualPolicy Policy,                 // Fail / PreserveAndWarn / DeclaredUnsupported
    bool Idempotent,
    bool RequiresWholeTree,
    VerificationProfile Verification);

public sealed record PassDescriptor
{
    // 现有字段...
    public PassContract? Contract { get; init; }
}
```

构造 ID 用稳定名字，而不是散落的诊断码：

```text
NORM-ENUM-ARITH
NORM-STACKALLOC
NORM-REF-STRUCT
NORM-REF-LOCAL
NORM-ASYNC-LAMBDA
NORM-TUPLE-OPEN-GENERIC
NORM-POINTER-DEREF
NORM-POINTER-LOCAL
NORM-EXTERN
NORM-FIXED
...
```

**单一事实源**：`NormalizationContractCatalog` 同时被三处消费：

- `normalization-audit` 的 final tree 扫描；
- Java 转换器的 `NormalizationRequired` 门禁（改成从 catalog 取构造名/ID，或加测试断言一致性）；
- pass 的 `Postconditions` / `MayLeave` 校验。

这样就不会再出现“enum-declaration 以为 numeric-boundary 会做，numeric-boundary 并不知道”的情况。

### 5.4 新增/扩展 pass 清单

| 优先级 | ID / 类型 | 输入 | 输出 | 残留判定 | 备注 |
|--------|-----------|------|------|----------|------|
| P0 | `normalization-audit`（新增，Final） | 最终中间树 | 不改写；`preprocess-residual-report.json` | 扫描全部 `NORM-*` 构造；`java-ready` 下 Error | 与 Java 门禁共用 catalog |
| P0 | `pass-report`（框架能力，非 pass） | 每轮 pass 的统计/诊断 | `preprocess-report.json` | 每 pass `filesScanned/changed/skipped/residual/verify` | 解决“诊断只在控制台” |
| P1 | `enum-arithmetic`（扩展 `enum-declaration` 或 `numeric-boundary`） | `E ± E`、`E ± n`、`n ± E`、`E++/--`、复合赋值 | 转成 class 的 enum→`(int)value.GetValue()`；真 enum→底层 cast | 最终树不得残留 enum 算术 | 先修 Deferred 死角，再决定 owner |
| P1 | `tuple-open-generic`（扩展 `tuple`） | 元素为类型参数的 tuple 类型/字面量/参数/返回值/局部 | 生成泛型 `Cs2jTuples.TupleXxx<T1,T2>` 或改走 `ValueTuple<T1,T2>` 兼容路径 | 最终树不得含 `TupleTypeSyntax` | 需处理泛型形状键 |
| P1 | `deconstruction-local`（新增，Declarative，`tuple` 后） | `var (a,b) = expr`、`(T1,T2) = expr`、非 tuple `Deconstruct` | 临时变量 + `Deconstruct` 调用/索引访问 | 最终树不得含解构局部声明 | 与 `tuple` 分工：tuple 管元组类型，本 pass 管非元组 Deconstruct |
| P1 | `async-lambda-per-node`（扩展 `async-normalize`） | 每个 async lambda/局部函数独立 | 可绑定的提升；不可绑定的按节点保留并标注 | `java-ready` 下不可绑定即 Error | 改文件级 all-or-nothing 为节点级隔离 |
| P1 | `runtime-reference-compilation`（框架能力） | 工程图 + `project.assets.json` / MSBuild `ResolveReferences` | 每项目真实引用集 + 合并整树编译 | 不绑定 NuGet 类型的节点为 Error | 从根上修复 async lambda 的 ConvertedType 为空 |
| P2 | `stackalloc-shape-expansion`（扩展 `unsafe-ffm` R4） | 三目/`??`/嵌套表达式中的 `stackalloc`；`Span<T>` 目标；`stackalloc[]` | 堆数组 + `Span` 适配；裸指针走 UF040 或降级 | 最终树不得含 stackalloc | 同时补 residual gate 的“静默跳过”分支 |
| P2 | `ref-local-unsafe`（扩展 `unsafe-ffm`，新增 R10） | `ref T x = ref MemoryMarshal.GetReference(...)` + `Unsafe.*(ref x)` / `Unsafe.Add(ref x, n)` | 数组/偏移索引化；或 `ByRef<T>` 读回 | 最终树不得含 `RefTypeSyntax` 局部 | 与 `ref-out-in` 的外部契约锁定协同 |
| P2 | `external-pointer-shim`（扩展 `unsafe-ffm` R5/R6） | 外部指针签名 override/接口实现 | 生成托管 shim 方法；或登记 `DeclaredUnsupported` | `java-ready` 下保留即 Error | 测试与生产工程分级 |
| P3 | `ref-struct-domain`（扩展 `unsafe-ffm` R7） | 用户 ref struct 声明与使用点 | 纯数据型去 `ref`；ref-only 型→compat 白名单/显式不支持域 | `java-ready` 下必须分类 | `Utf8JsonReader` 是核心样例；需要域决策 |
| P3 | `type-mapping-compat`（配置能力） | BCL ref struct/span 类型 | 映射到 Java compat 类型，源类型跳过转换 | 映射表驱动 | 与 ref struct 域策略二选一或组合 |

**顺序插入点**：

```text
tuple
  → deconstruction-local (新)
  → foreach-deconstruction

enum-declaration
  → enum-arithmetic (新；或 numeric-boundary 内的 EnumArithmeticRule)

ref-out-in
  → ref-local-unsafe (新；或 unsafe-ffm R10 前移到此处)

unsafe-ffm
  → （R4 扩展 / R5-R6 扩展 / R7 域策略）

make-readonly, closure-lift
  → async-normalize（节点级隔离）

java-keywords
  → normalization-audit (新，Final)
```

不建议把 `normalization-audit` 做成普通 rewrite pass；它是**只读闸门**，不改树、不注入支持库。

### 5.5 链末 normalization-audit 的接口

```csharp
public interface IResidualProbe
{
    string Code { get; }                 // NORM-ENUM-ARITH
    string OwnerPassId { get; }          // enum-declaration
    ResidualPolicy Policy { get; }      // Fail / PreserveAndWarn / DeclaredUnsupported
    IEnumerable<ResidualHit> Scan(
        CSharpCompilation compilation,
        IReadOnlyDictionary<string, SemanticModel> models);
}

public sealed record ResidualHit(
    string Code,
    string File,
    int Line,
    int Column,
    string Snippet,
    string Reason);
```

报告 schema（建议）：

```json
{
  "schemaVersion": 1,
  "profile": "java-ready",
  "chain": ["conditional", "...", "java-keywords"],
  "summary": { "files": 4218, "residuals": 0, "errors": 0, "warnings": 0 },
  "passes": [
    {
      "id": "unsafe-ffm",
      "status": "ok",
      "filesScanned": 4218,
      "filesChanged": 312,
      "filesSkippedClean": 3900,
      "rejections": { "UF020": 37, "UF021": 20, "UF040": 11, "UF052": 1 },
      "residuals": [],
      "verify": { "build": "skipped", "tests": "skipped" }
    }
  ],
  "residuals": [
    {
      "code": "NORM-STACKALLOC",
      "file": "json/src/System/Text/Json/Reader/JsonReaderHelper.Unescaping.cs",
      "line": 21,
      "owner": "unsafe-ffm",
      "policy": "Fail",
      "reason": "Span-target stackalloc inside a conditional expression was not collected"
    }
  ]
}
```

CLI 增加：

```text
--profile default|java-ready|audit
--strict-residual            # java-ready 的别名/强化
--report <path>              # 默认 <dest>/preprocess-report.json
--residual-report <path>     # 默认 <dest>/preprocess-residual-report.json
```

### 5.6 编译层：使用真实工程引用

改动点：

- `ProjectPassRunner.BuildStandardCompilation` 从“一份 TPA 合并编译”升级为“按工程图解析真实引用集”：
  - `project.assets.json` → 包引用；
  - `<ProjectReference>` → 项目输出/源码；
  - `<Reference Include>` / `<PackageReference>`；
  - 每工程独立 `CSharpCompilation`，再做合并视图（或按工程分组运行语义 pass）。
- 或者直接复用 `convert-project` 已有的 MSBuild 工作区加载结果（`ProjectCompileSet` / `ProjectGraph`）。
- 保留 TPA 作为 fallback，但必须记录 `degraded` 标记；`java-ready` 下 degraded 编译直接失败。

这一步能同时改善：
- async lambda 的 `ConvertedType`；
- 泛型/扩展方法/特性绑定的准确性；
- 所有依赖 NuGet 类型的语义 pass 的覆盖率；
- 减少“规则不会写，只是没绑定”的假阴性。

### 5.7 失败策略与验证矩阵

建议把“残留处理”显式化：

| Policy | 适用 | 行为 |
|--------|------|------|
| `Fail` | 最终树仍含 Java 不支持的构造 | `java-ready` 下 Error，链退出非 0 |
| `PreserveAndWarn` | 当前能力确实无法处理，且用户接受 | 保留原文 + Warning + 报告；默认 profile 放行 |
| `DeclaredUnsupported` | 明确不转换的域（如某些 extern/ref struct） | 报告里列出文件/行/owner/原因；Java 转换前 fail-fast |

验证矩阵：

| 层级 | 频率 | 内容 |
|------|------|------|
| 文件级 | 每文件 | 语法后校验（已有）+ 本 pass 的 `Postconditions` 快速扫描 |
| pass 级 | 每 pass | 默认不跑完整 `dotnet build`；只跑 `ResidualProbe` + 变更文件语法校验 |
| 链级 | 一次 | final tree `dotnet build -c Release` + `dotnet test -c Release`（默认链只跑一次，而不是每个 pass 都跑） |
| Java 就绪 | 一次 | `normalization-audit` + `java-ready` profile |
| 可选 | CI/夜间 | 全量 Java 转换 + Maven 测试 |

### 5.8 可观测性

- 每 pass 落盘 `report.json`（统计、诊断、耗时、输入/输出哈希）。
- 链末 `residual-inventory.json`（按 `NORM-*` 聚合，含 owner）。
- 覆盖矩阵 `coverage-matrix.json`：构造 × pass × 语料计数；用于发现“文档说支持、实际 0 命中”的漂移。
- 诊断不再“降级即沉默”：`PreserveAndWarn` 必须进报告；`Fail` 必须退非 0。

### 5.9 性能与增量

现状：默认 42 次整树复制；本次输出目录中 42 个 `.cs2j-*-src` 即为证据。建议：

1. **默认统一中间树**：把 `--unified-preprocessed-src` 翻转成默认，保留 `--separate-preprocessed-src` 作为调试选项；需要 `PassTreeContext.InPlace` + `InPlaceSourceSnapshot` 已经具备（[ProjectPassAdapter.cs](file:///D:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/ProjectPassAdapter.cs#L114-L223)）。
2. **增量缓存**：以 `(passId, optionsHash, inputFileHash, referenceSetHash)` 为键，复用上一轮 pass 产物；未变更文件直接复制/跳过。
3. **选择性 verify**：默认链只在链末跑一次 build/test；单 pass 的 `verifyBuildByDefault` 保留给 `--pass X` 独立调试。
4. **pass 级并行**：仅对无数据依赖的独立分支并行（需要 Contract v2 的 `Consumes/Produces` 才能安全判定）；当前顺序链不建议盲目并行。
5. **生成物清单**：统一 support library / generated files 的注入与过滤，避免每个 pass 重复扫描。

### 5.10 文档同步

`prompts/preprocessor-catalog.md`、`prompts/preprocessor-completion-audit-2026-10-05.md` 与当前 42-pass 实现已经漂移（例如多个曾被标记 Stub 的 pass 现已 Implemented/默认开启；C35/C51 已落地在 expression-desugar）。建议：

- 以 `PassDescriptor + Contract` 为唯一事实源，生成 `docs/preprocessor-matrix.md`；
- 每次注册表变更跑一个 `PassContractConsistencyTests`，断言：
  - 每个 `NORM-*` 至少有一个 owner；
  - 每个 Java `NormalizationRequired` 都有对应 `NORM-*`；
  - 每个 pass 的 `Postconditions` 在 fixture 上成立。

---

## 6. 实施波次与验收

### 6.1 波次

| 波次 | 内容 | 产出 | 风险 |
|------|------|------|------|
| M0 可观测 | `ResidualRegistry` + `normalization-audit` + pass report + `--profile java-ready` | 本语料 15 个失败被分类为 7 类、带文件/行/owner；不改写任何源码 | 低 |
| M1 编译与 async | 真实工程引用；async-normalize 节点级隔离；xUnit 引用可见 | 6 个 async lambda 残留消除 | 中（编译层改动大） |
| M2 声明/表达式 | enum 算术、开放泛型 tuple、非元组解构局部 | 3 个残留消除 | 中 |
| M3 指针/栈 | stackalloc 形状扩展、ref local + Unsafe、外部指针 shim | 5 个残留消除 | 高（语义/类型系统） |
| M4 ref struct 域 | R7 域策略：纯数据降级、ref-only compat/显式不支持 | 2 个残留消除或显式登记 | 高（需要产品决策） |
| M5 性能/文档 | 统一树默认、增量缓存、覆盖矩阵、文档生成 | 复制次数 42→1；报告可追踪 | 中 |

### 6.2 验收标准

1. `java-ready` profile 在本语料上：

```powershell
dotnet run -c Release --project src/CSharpToJava.CLI/CSharpToJava.CLI.csproj -- preprocess `
  -s D:\csharp-sdk\csharp-sdk.sln `
  -d d:\cs\cs-sdk-78e810c6-java-ready `
  --profile java-ready
```

- 退出码 0；
- `preprocess-residual-report.json` 的 `summary.residuals = 0`，或所有残留均为 `DeclaredUnsupported` 且有 owner/原因；
- 中间树 `dotnet build -c Release` 无错误、`dotnet test -c Release` 无失败。

2. 重新执行原 `convert-project` 命令：
- `Conversion complete` 的 failed 数从 15 → 0（至少本语料 7 类全消）；
- 21 个模块、1961+ 类型仍成功（failed 之外的类型不回归）。

3. 回归：
- `CSharpToJava.Tests` 与 `CSharpToJava.ConversionTests` 全绿；
- 每个新增/扩展 pass 至少一组 fixture + 幂等测试 + residual 测试；
- 每个 `NORM-*` 有正向 fixture（触发）和负向 fixture（不触发）。

### 6.3 每类的最小验收样例

| 类别 | 最小输入 | 期望 final tree |
|------|----------|----------------|
| async lambda | `await Assert.ThrowsAsync<T>(async () => await F())` | 无 `async` lambda；提升为命名方法/显式委托 |
| stackalloc | `Span<byte> s = c ? stackalloc byte[n] : arr;` | 无 `stackalloc` |
| ref struct | `ref struct R { ... }` + `Utf8JsonReader` 用法 | 降级或登记为不支持域 |
| enum 算术 | `(E a, E b) => a - b` | 无 enum 操作数二元算术 |
| ref local | `ref byte r = ref MemoryMarshal.GetReference(s);` | 无 `RefTypeSyntax` 局部 |
| tuple | `(T1, T2) v = default;` | 无 `TupleTypeSyntax` |
| 指针 | `override unsafe int F(char* p) => *p;` | 托管 shim 或显式不支持域 |

---

## 7. 风险与开放问题

1. **ref struct 不是纯 lowering 问题**：`Utf8JsonReader` 的栈语义、生命周期、Span 借用无法在 Java 中完整表达。需要产品级决策：compat 类型映射、显式不支持域，或重写为 Java 类。建议先做“显式分类 + 报告”，再决定是否投入 lowering。
2. **真实引用集会显著增加编译成本**：每项目编译 + 合并视图有内存/时间代价。建议按工程分组，只对 `NeedsSemanticModel=true` 的 pass 构建；并引入编译缓存。
3. **开放泛型 tuple 的形状爆炸**：`(T1, T2)` 等开放形状需要泛型 struct，不能简单按具体类型注册。需要限制形状键或复用 `ValueTuple<T1,T2>` 兼容路径。
4. **enum 算术的语义边界**：`E ± E` 结果是 `int`；`E ± n` 结果仍是 `E`；溢出/窄底层类型需要 `unchecked` 包裹。不同 owner（enum-declaration vs numeric-boundary）会改变 cast 顺序，必须在 Contract 中固定。
5. **Java 门禁与 preprocess 契约的同步**：如果 Java 侧新增一个 `NormalizationRequired` 而 preprocess 没登记，audit 会漏。需要一致性测试，而不是靠人工。
6. **默认 profile 的选择**：把 `java-ready` 设为默认会改变现有“C# 中间树可编译即成功”的语义，可能影响下游使用。建议先提供 profile，默认仍是 `default`，CI 使用 `java-ready`；稳定后再翻转。
7. **测试工程的处理**：测试工程是 C# 行为预言机，不是 Java 产物输入。`unsafe-ffm` 的 UF050 降级策略就是基于这个假设。升级方案应把“测试工程 vs 生产工程”的残留策略显式化，而不是散落在 adapter 里。

---

## 8. 实施进度（2026-11-04）

本方案已按“一个修复、一次验证、一次提交”的方式落地了 M0 与 M1、M2 的大部分内容。当前状态如下（验证命令均为 Release）：

| 项 | 状态 | 关键提交/产物 | 验证证据 |
|----|------|---------------|----------|
| NORM 残留契约 + 7 个只读探针 | 完成 | `src/CSharpToJava.Core/Normalization/` | `ResidualRegistryTests` 17 通过 |
| `normalization-audit` 只读 pass + `--profile java-ready` / `audit` + 残留报告 | 完成 | `NormalizationAuditPassAdapter`、`preprocess-residual-report.json` | fixture 上 `java-ready` 退出 1、`audit` 退出 0；SDK 最终树可审计 |
| pass 级/链末 Java-readiness 闸门 | 完成 | 审计报告与 CLI profile | 全链 audit 可定位 7 类残留 |
| async lambda 委托目标绑定（NuGet 引用） | 完成 | `AsyncNormalizePassAdapter` 枚举/按需 restore restored 包 | 全链 25 skipped → 4 skipped |
| 安全捕获 async lambda 提升 | 完成 | `AsyncLambdaLifter` 捕获参数 + 非 async wrapper | 10 个捕获 lambda 提升；6 个原失败文件无 async lambda |
| async 节点级隔离 | 完成 | `AllowPartialRewriteOnError`（测试工程） | 21 lifted / 4 residual（只剩 async void） |
| enum 算术/`is`/成员初始化器降级 | 完成 | `EnumSyntaxRewriter` 通用源 enum 分支 | SDK 最终树 `NORM-ENUM-ARITH 43 → 0` |
| stackalloc 降级 + unsafe 块清理 | 完成 | `stackalloc-lowering` pass | `NORM-STACKALLOC 73 → 0`；json tests 0 编译错误 |
| tuple 局部声明规范化 | 完成 | `tuple` pass `var` 化 | `NORM-TUPLE-LOCAL 1 → 0` |
| 外部 Unsafe ref 局部降级 | 完成 | `unsafe-ref-lowering` pass | `NORM-REF-LOCAL 1 → 0` |
| 纯数据 ref struct 去 ref + audit 跳过 support lib | 完成 | `ref-struct-lowering` pass | `NORM-REF-STRUCT 5 → 1`（只剩 `Utf8JsonReader`） |
| `using` 资源表达式 ref 读回提升 | 完成 | `using-resource-lift` pass | JsonDocumentTests 的 7 处 using 门禁消除 |
| 指针解引用 → 元素访问 | 完成 | `pointer-deref-index` pass | `NORM-POINTER-OP 26 → 2`（只剩 `&` address-of）；Utf8JsonWriterTests 转换通过 |
| 新审计探针（pointer-local / unsafe-block / fixed） | 完成 | `Normalization/Probes` | 新增 3 个 NORM code，准确定位硬域 |

**全量转换对照**（同一 `csharp-sdk.sln`、同一命令）：

| 阶段 | 结果 |
|------|------|
| 初始 | 1961 succeeded / 15 failed |
| 修复 async/enum/stackalloc/tuple/ref-local 后 | 1971 succeeded / 5 failed（该轮尚未包含 ref-struct pass） |
| json-only 复跑（含 ref-struct / using-resource / pointer-deref） | 624 succeeded / **2 failed** |
| 单元回归 | `CSharpToJava.Tests` 4377 通过 / 0 失败 / 9 跳过（最新全量） |

剩余 2 个失败已收敛到两个硬域：

1. `Utf8JsonReader`（ref-only ref struct）：Span/ref 字段无法在 Java 中直接表达；需要 compat 类型映射或显式不支持域决策。
2. `JsonWriterHelper.Date.cs`（fixed + Span 指针算术）：`fixed` over `MemoryMarshal.GetReference` + `int*`/指针差值，属于 C06 指针 lowering 的深水区；`pointer-deref-index` 已消除其中 24 个解引用，但 fixed/address-of/指针差仍保留。

已消除的失败：async lambda、enum 算术、stackalloc、tuple 局部、ref local、ref struct（MyRefStruct）、using ref 读回、外部指针 override 解引用。

下一步需要针对这两个硬域做**域策略**：要么提供 `Utf8JsonReader`/`JsonWriterHelper.Date.cs` 的 compat/托管实现并映射，要么把它们登记为 `DeclaredUnsupported` 并在 Java 转换前 fail-fast（当前 audit 已能给出文件/行/owner）。另外 M5 的单树/增量/每 pass 报告仍未落地。

---

## 附录 A：证据索引

| 证据 | 位置 |
|------|------|
| 42 pass 锁定顺序 | `preprocess --list-passes` 输出 |
| descriptor 字段 | [PassDescriptor.cs](file:///D:/code/cs2j/src/CSharpToJava.Core/Preprocessing/PassDescriptor.cs#L60-L141) |
| 拓扑排序 | [PassRegistry.cs](file:///D:/code/cs2j/src/CSharpToJava.Core/Preprocessing/PassRegistry.cs#L95-L133) |
| pass 注册表 | [PassCatalog.cs](file:///D:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassCatalog.cs#L45-L681) |
| 单轮执行器 | [ProjectPassRunner.cs](file:///D:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/ProjectPassRunner.cs#L28-L214) |
| 文件级契约 | [ProjectPassAdapter.cs](file:///D:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/ProjectPassAdapter.cs#L49-L59) |
| 验证实现 | [ProjectPassRunner.cs](file:///D:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/ProjectPassRunner.cs#L631-L698) |
| 默认 TPA 引用 | [ProjectUnsignedTypePreprocessor.cs](file:///D:/code/cs2j/src/CSharpToJava.CLI/ProjectUnsignedTypePreprocessor.cs#L81-L105) |
| async lambda skip | [AsyncNormalizePreprocessor.cs](file:///D:/code/cs2j/src/CSharpToJava.Core/AsyncNormalize/AsyncNormalizePreprocessor.cs#L72-L103) |
| unsafe-ffm 收口策略 | [UnsafeFfmPassAdapter.cs](file:///D:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/Adapters/UnsafeFfmPassAdapter.cs#L95-L103) |
| enum Deferred | [EnumDeclarationAnalyzer.cs](file:///D:/code/cs2j/src/CSharpToJava.Core/EnumDeclaration/EnumDeclarationAnalyzer.cs#L1321-L1345) |
| enum Deferred 落盘 | [EnumDeclarationAnalyzer.cs](file:///D:/code/cs2j/src/CSharpToJava.Core/EnumDeclaration/EnumDeclarationAnalyzer.cs#L508-L516) |
| R4 预检/residual | [UnsafeFfmPreprocessor.cs](file:///D:/code/cs2j/src/CSharpToJava.Core/UnsafeFfm/UnsafeFfmPreprocessor.cs#L340-L350) |
| R4 执行/residual | [UnsafeFfmPreprocessor.cs](file:///D:/code/cs2j/src/CSharpToJava.Core/UnsafeFfm/UnsafeFfmPreprocessor.cs#L530-L561) |
| 外部指针签名保留 | [PointerSignatureRewriter.cs](file:///D:/code/cs2j/src/CSharpToJava.Core/UnsafeFfm/PointerSignatureRewriter.cs#L91-L98) |
| Java async 门禁 | [LambdaTransformer.cs](file:///D:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/LambdaTransformer.cs#L52-L53) |
| Java stackalloc 门禁 | [ObjectCreationTransformer.cs](file:///D:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/ObjectCreationTransformer.cs#L548-L549) |
| Java ref struct 门禁 | [StructTransformer.cs](file:///D:/code/cs2j/src/CSharpToJava.Core/Transformers/Type/StructTransformer.cs#L60-L63) |
| Java ref local/解构门禁 | [StatementTransformer.Declarations.cs](file:///D:/code/cs2j/src/CSharpToJava.Core/Transformers/Statement/StatementTransformer.Declarations.cs#L27-L37) |
| Java enum 算术门禁 | [BinaryExpressionTransformer.cs](file:///D:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/BinaryExpressionTransformer.cs#L191-L192) |
| Java 指针解引用门禁 | [UnaryExpressionTransformer.cs](file:///D:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/UnaryExpressionTransformer.cs#L50) |
| 转换日志 | `D:\cs\202611040733.log` |
| 最终树 | `D:\cs\cs-sdk-78e810c6\.cs2j-java-keywords-src` |
| unsafe-ffm 单 pass 证据 | `D:\cs\tmp-unsafe-check` 复跑输出 |
| async-normalize 单 pass 证据 | `D:\cs\tmp-async-check` 复跑输出 |

## 附录 B：诊断码速查

| 前缀/码 | 归属 | 含义 |
|---------|------|------|
| `CS2J9001` | 框架 | 显式启用了未实现的 Stub |
| `CS2J9002` | 框架 | 未知 pass id / 同时 enable+disable |
| `UF010` | unsafe-ffm | 内部规则缺口/residual gate |
| `UF020` | unsafe-ffm | 指针来源/逃逸无法证明，原文件保留 |
| `UF021` | unsafe-ffm | 指针 cast 取绝对地址/改变指针种类 |
| `UF040` | unsafe-ffm | stackalloc 无法堆化，原文件保留 |
| `UF050` | unsafe-ffm | 带 ref-only 成员的用户 ref struct 拒绝 |
| `UF052` | unsafe-ffm | 纯数据型 ref struct 去 `ref` 降级 |
| `ED020` | enum-declaration | enum 不可表示，保真为真 enum |
| `CS2J18xx` | async-normalize | async 形态不支持/残留 |
| `CS2J04xx` | iterator-lowering | 迭代器形态不支持/残留 |
| `ROI099` | ref-out-in | 改写后语法校验失败，保留原文 |
| `CS2J53xx` | numeric-boundary | checked/数值边界/残留 |
| `CS2J normalization required` | Java 转换器 | 预处理未完成的硬门禁 |

## 附录 C：新增 pass 的最小骨架

```csharp
// PassCatalog
new(
    "normalization-audit",
    "Normalization readiness audit",
    ".cs2j-normalization-audit-src",
    PassStage.FinalNaming,
    PassStatus.Implemented,
    defaultEnabled: true,
    runAfterIds: new[] { PassIds.JavaKeywords },
    needsSemanticModel: true,
    verifyBuildByDefault: false,
    verifyTestsByDefault: false,
    description: "Read-only final-tree audit: every NORM-* residual must have an owner and policy.")
```

```csharp
// 只读 pass：不复制、不改写；Runner 需要新增 AuditOnly 执行模式
internal sealed class NormalizationAuditPassAdapter : ProjectPassAdapter
{
    public override PassDescriptor Descriptor => ...;
    public override Task<PassFileRewrite?> RewriteFileAsync(PassFileContext c)
        => Task.FromResult<PassFileRewrite?>(null);
    public override Task AfterFilesAsync(PassTreeContext tree)
    {
        // 1) 构建 final tree 编译与 semantic models
        // 2) 跑 ResidualRegistry.Scan
        // 3) 写 preprocess-residual-report.json
        // 4) java-ready profile 下把 Fail/DeclaredUnsupported 转成 pass 失败
    }
}
```

```csharp
// 新增 residual 的登记模板
new ResidualDescriptor(
    Code: "NORM-STACKALLOC",
    OwnerPassId: PassIds.UnsafeFfm,
    Policy: ResidualPolicy.Fail,
    Probe: new StackAllocProbe(),
    JavaGate: "stackalloc in StackAllocArrayCreationExpression");
```

---

## 附录 D：本次方案不改变的东西

- 不改变 42 个 pass 的既有外部行为与 CLI 开关语义。
- 不把 Java 转换器的规则搬进 preprocess；只把“Java 需要什么”变成可机器校验的契约。
- 不为单一 SDK 语料写死特判；所有新增规则/shape 都走通用语法/语义判定。
- 不在本方案中承诺 ref struct 一定能 lowering；它先以“显式分类 + 报告”落地，再按域决策推进。
