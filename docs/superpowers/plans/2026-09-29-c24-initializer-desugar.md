# C24 对象/集合初始化器归一 — 实施计划（2026-10-02）

**设计**：[2026-09-29-c24-initializer-desugar-design.md](../specs/2026-09-29-c24-initializer-desugar-design.md)
**worktree**：`.worktrees/preproc-c24-initializer-desugar`，分支 `feature/preproc-c24-initializer-desugar`
**base**：`4ea50d44`（= dev-3.x `10aae03f` + C24 设计文档 base 前移提交）

## 0. 基线裁决（与设计文档差异点）

截至 base：C23 锚点未合入；C36 已按独立落地先例把 `expression-desugar` stub 翻转
（Implemented、**默认关**、catalogItems `"C36"`）并在 dev-3.x 落地最小族基建
（`src/CSharpToJava.Core/ExpressionDesugar/Shared/` + `CollectionExpr/`，
`ExpressionDesugarRuleSet` 已按固定序 `Interpolations(C23)→Initializers(C24)→NullConditional(C35)→CollectionExpr(C36)` 为 C24 预留第二追加位）。
C24 沿同一先例**只增落地**：

1. **不改 pass 注册状态**：`expression-desugar` 已 Implemented/默认关；不翻转 stub、不新增
   pass id/适配器/verb/中间目录；RunAfterIds 沿用 `null-coalesce`；中间目录沿用
   `.cs2j-expression-desugar-src`。
2. **族基建以 C36 实物为准**（设计正文中指向 C23 的前向引用全部替换为以下签名）：
   - `ExpressionDesugarRuleSet.Rules`：在第二追加位注册 `new InitializersRule()`（C36 末位不动）；
   - `ExpressionDesugarOptions`：追加 `public InitializerOptions? Initializers { get; init; }`
     （nullable；`InitializerOptions` 为 public sealed class，缺省视为 `new()`）；
   - `ExpressionDesugarStatistics`：追加 `public InitializerStatistics Initializers { get; } = new();`
     + `MergeFrom` 一行；
   - `ExpressionDesugarPreprocessor`：按 C36 同款**只增**补齐 Initializers 段计数
     （FilesSkippedClean/FilesDirtyNoRewrite/FilesTransformed/TempsLifted）；C36 行为零改动；
   - CLI `ExpressionDesugarPassAdapter`：**只增** init 摘要段 + AfterFiles residual 按语法种类
     扩展（新增 `InitializerTreeGate`，不改 C36 既有配对口径）；`PassCatalog.cs` 一行
     catalogItems 加宽 `"C36, C24"`；`PassChainOrderTests` 断言一行。
3. **复用**（不改签名）：`ExpressionDesugarTempLifter`（`PendingTemp` 语句序列 + `Apply` +
   锚点注解）、`ExpressionDesugarTempNameAllocator`（`__ed_` 文件级发号）、
   `ExpressionDesugarSyntaxFacts.IsInExpressionTree/IsInLiftableContext`、
   `ExplainedAnnotation`/`ExplainedSubtreeAnnotation`。
4. **C24 自带 `InitializerContextLowering`**（C36 的 `CollectionContextLowering` 是其最小先行版）：
   不重构、不覆盖 C36 文件；如需泛化提取留族评审（设计 §3.2-4、裁决 4）。
5. **规则实现全部落 `Initializers/`**；不触碰 `Interpolations/*`（不存在）、`CollectionExpr/*`、
   `Transformers/**`、`TypeMappings.json`、compat 库；不新建支撑工程。
6. **Roslyn 版本以仓内实物 5.0.0 为准**（`CSharpToJava.Core.csproj`）；设计按 4.12 写的 API
   外形一律以 M0 探针实测收口（同 C36 M0 纪律）。
7. **诊断码** CS2J0530–CS2J0545（设计 §6），与 C36 057x/058x 码段不交叉；级别语义与 C36
   同口径：strict Error → 整文件原子不改写；非 strict 降 Warning 且节点保留（裁决 5 的
   摘要单列）。
8. **不在本次交付面**：M4（sdk 画像/默认开证据、构造函数迁移）、Java 半区核查（设计 §8-3）、
   转换器特判下掉 PR——登记为后续证据/消费 PR；本计划交付 M0→M3。

## 1. 目标目录结构

```
src/CSharpToJava.Core/ExpressionDesugar/Initializers/
  InitializerOptions.cs           # public：Strict/Scope(All|Object|Collection|TargetTypedNew)/RewriteInExpressionTrees/UnsupportedPolicy
  InitializerModels.cs            # InitializerPlan / InitializerMemberPlan(PropertySet/FieldSet/IndexSet/NestedCreate/NestedMemberInit/CollectionAdd) / TargetTypeInfo / InitializerContextKind / Outcome
  InitializerDiagnostics.cs       # CS2J0530-CS2J0545 常量 + 消息模板 + ResolveSeverity（同 C36 口径）
  InitializerStatistics.cs        # 设计 §5-3 全字段 + AddSkip + MergeFrom
  InitializerSyntaxFacts.cs       # residual 三类节点判定、初始化器项语法形状、排除谓词（数组/匿名对象）、Explained 配对
  InitializerBindingAnalyzer.cs   # IOperation 逐项绑定分类（决策 2/7/8/10/11/12）+ 目标类型推断 + 可提升性
  ObjectInitRewriter.cs           # 对象初始化器：构造语句 + 属性/字段/索引赋值序列 + 嵌套
  CollectionInitRewriter.cs       # 集合初始化器：构造实参保留 + Add（单参/多参/扩展/fluent/params）
  TargetTypedNewRewriter.cs       # 裸 new()/带初始化器 new(){…} 显式化（可空目标剥注解）
  InitializerContextLowering.cs   # 决策 11 语句化：声明/return/throw/赋值/表达式语句/using/三元 if-else/实参交错
  InitializerTreeGate.cs          # M3 AfterFiles 全树 residual 配对（三类节点 vs 配对 skip）
  InitializersRule.cs             # IExpressionDesugarRule 实现（Id="Initializers"）
tests/CSharpToJava.Tests/InitializerDesugarUnitTests.cs          # 设计 §7.1 矩阵
tests/CSharpToJava.ProjectTests/InitializerDesugarProjectTests.cs # M3 项目级（范式：CollectionExprProjectTests）
tests/SampleExpressionDesugarSolution/                            # M2/M3 增 Initializers* 前缀夹具（不建第二套 sln）
```

## 2. 集成点清单（全部只增不改）

| 文件 | 改动 |
|---|---|
| `Core/ExpressionDesugar/Shared/ExpressionDesugarRuleSet.cs` | 第二追加位 `new InitializersRule()` + 注释更新 |
| `Core/ExpressionDesugar/Shared/ExpressionDesugarOptions.cs` | 一个 nullable 属性 `Initializers` |
| `Core/ExpressionDesugar/Shared/ExpressionDesugarStatistics.cs` | 一个段属性 + `MergeFrom` 一行 |
| `Core/ExpressionDesugar/Shared/ExpressionDesugarPreprocessor.cs` | 只增 Initializers 段计数行（含 no-model 警告路径） |
| `CLI/Preprocessing/PassCatalog.cs` | `catalogItems: "C36"` → `"C36, C24"`（一行） |
| `CLI/Preprocessing/Adapters/ExpressionDesugarPassAdapter.cs` | 摘要 init 段 + AfterFiles 闸门按种类扩展 |
| `tests/CSharpToJava.Tests/Preprocessing/PassChainOrderTests.cs` | L243 断言改 `"C36, C24"` |

不加 convert-project legacy toggle、不动 `PassChainFactory.cs`/`Program.cs`（工厂已存在）、
不动 `PassStubCatalog.Stubs` 计数。

## 3. 阶段划分（每阶段：实现 → Release build/test 全绿 → 提交）

### M0 探针 + 骨架 + 注册登记（空转，默认关）

- `Initializers/` 全部文件骨架（类型/枚举/常量/字段齐备；Analyze 只产站点计数与 stats，
  **不发射任何改写、不产 Error**）；RuleSet/Options/Statistics/Preprocessor/catalogItems/
  PassChainOrderTests 集成点落地。
- **Roslyn 5.0.0 探针**（写成可运行的 xunit 事实，结果回填本文 §6）：
  1. `IObjectCreationOperation.Initializer` 成员项分类外形：属性 `IPropertyReferenceOperation`
     （SetValue）、字段写、嵌套成员初始化 `IMemberInitializerOperation`、集合元素
     `ICollectionElementInitializerOperation`（AddMethod/Arguments）、字典索引
     `ImplicitElementAccess`；
  2. Add 绑定外形：单参/多参（`ComplexElementInitializer`）/扩展 reduced/fluent/params 展开/
     IEnumerable 无 Add 的基线报错（CS1922/CS0117 同族）；
  3. init-only（`IPropertySymbol.SetMethod.IsInitOnly`）与 readonly 字段/get-only 属性基线
     CS0191/CS0200 复现；C11 放宽后的输入树形态（普通 set 方法 + 字段）对照；
  4. target-typed `new()` 各上下文 `GetTypeInfo(node).ConvertedType`：局部声明/return/实参/
     三元/字段初始化器/`: this()`；可空目标注解取底层 T；
  5. 字典索引初始化器 Operation 外形（`ImplicitElementAccessOperation` 或实际类型）；
  6. dirty 门谓词：`ObjectCreationExpressionSyntax.Initializer` / 集合初始化器元素 /
     `ImplicitObjectCreationExpressionSyntax`。
- 单测：探针事实 + clean 文件 `Changed=false` 字节保真 + 注册清单断言 + 含初始化器的 dirty
  文件 M0 空转（`Changed=false`、计数正确、输出原样）。
- 验收：`dotnet build -c Release CSharpToJavaConverter.slnx` + Tests 工程全绿。
- 提交：`feat(preprocessing): C24 initializers M0 探针+规则骨架+注册登记（默认关、空转）`

### M1 对象初始化器 + Add 主路径

- IN-1/2/3/6/9/12 主体：对象初始化器序列（属性保持属性写、字段直写，按运行时树符号；
  成员严格源码序）；构造实参保留（`new List<int>(8){…}` 不丢实参）；单参 Add 以
  `AddMethod` 绑定为准（不猜 `put`）；裸 `new()` 显式化为 `new T(…)`（含可空目标）；
  基础上下文：局部/using 声明 RHS、return、表达式语句。
- 诊断：CS2J0530/0531/0532/0533/0535/0536（strict Error → 整文件原子不动）。
- 单测：设计 §7.1 矩阵 1/2/3(部分)/5/7(基础)/9/11/12。
- 验收：Release build + Tests 全绿。
- 提交：`feat(preprocessing): C24 initializers M1 对象初始化器+Add 主路径（CS2J0530-0536）`

### M2 嵌套 + 字典 + 上下文语句化 + Add 变体

- IN-4/5/7/8/10/11：嵌套创建自底向上（内层临时先于外层成员赋值）；嵌套成员初始化 getter
  链单次求值临时；字典索引 `[k]=v` 索引赋值（键值各一次、源码序）；多参/扩展/fluent/params
  Add（CS2J0539 分类计数）；上下文语句化：三元 if-else 双支定指、`??`、赋值 RHS、throw、
  实参交错（前序实参提升）；CS2J0537/0538/0541/0542 跳过族。
- 单测：矩阵 2(行为探针)/3/4/6/7(全)/8/10；`SampleExpressionDesugarSolution` 增
  `Initializers*` 夹具初版（原生/预处理双跑）。
- 验收：Release build + Tests 全绿。
- 提交：`feat(preprocessing): C24 initializers M2 嵌套/字典/上下文语句化/Add 变体（CS2J0537-0542）`

### M3 residual 收口 + 共处回归 + fixture/CLI 交付

- IN-13：residual 三类节点闸门（对象/集合初始化器 + 裸 `new()`）CS2J0545：文件级自检 +
  AfterFiles 全树复扫（`InitializerTreeGate`，与 C36 闸门并列只增）；幂等断言（二次运行
  `Changed=false`）；BOM/字节复制。
- 共处矩阵：C11/C27 产物树输入（手写等价形态）、C36/C23 混排（临时序号/锚点不冲突）、
  数组/匿名对象零误伤。
- CLI 摘要 init 段 + `--list-passes` catalogItems `C36, C24`；项目级 fixture 双跑扩展 +
  ProjectTests + CLI 测试。
- 验收：全量 build + 三测试工程（除 §7 已知基线失败外）全绿 + 高价值 grep 三禁。
- 提交：`feat(preprocessing): C24 initializers M3 residual 全树闸门+共处回归+fixture/CLI 收口（默认关）`

## 4. 跨阶段硬纪律

1. 判定一律走全编译 SemanticModel/IOperation（Add 符号、init 标记、索引器 set、target-typed
   目标类型）；**禁止按文本猜 `add`/`put`**（设计硬不变式 #4）。
2. 求值顺序：构造实参先于初始化器；成员/Add/索引赋值严格源码序；任何子表达式单次出现；
   条件位置（三元/`??`/lambda）不外提（S6 两族回归的正面收口）。
3. 禁止静默：无法等价显式化即显式诊断 + 整文件原子不改写；禁止注释/占位/半成品透传。
4. 产物是普通合法 C#：每轮 `AdhocWorkspace` 格式化；干净文件字节级复制（含 BOM）；
   二次运行零 diff。
5. 规则代码不按语法种类 `OfType` 静默过滤；每类初始化器项必须有处置或显式诊断。
6. 不改 `CollectionExpr/*`、`Transformers/**`、E09、C01/C11/C12 已落地代码；共享文件只增不改。
7. 每阶段提交前必须本地跑过 Release build + 相关测试；提交信息带阶段与码段。
8. 临时名统一 `__ed_` 族发号（复用 `ExpressionDesugarTempNameAllocator`），禁止自造前缀。

## 5. 验证命令

```powershell
dotnet build -c Release CSharpToJavaConverter.slnx
dotnet test -c Release tests/CSharpToJava.Tests/CSharpToJava.Tests.csproj
dotnet test -c Release tests/CSharpToJava.ProjectTests/CSharpToJava.ProjectTests.csproj
dotnet test -c Release tests/CSharpToJava.ConversionTests/CSharpToJava.ConversionTests.csproj
```

## 6. M0 探针记录（2026-10-02 回填）

环境：.NET 10 / `Microsoft.CodeAnalysis.CSharp` **5.0.0**（`CSharpToJava.Core.csproj`）；
探针固定在 `tests/CSharpToJava.Tests/InitializerDesugarUnitTests.cs`（7 个探针 Fact + 5 个 M0
行为 Fact，共 12 个，全绿）。设计正文按 4.12 记录的 API 外形有一处**实质偏差**（见 6.2），
以本节实测为准。

### 6.1 对象初始化器成员项分类（`IObjectCreationOperation.Initializer`）

- `ObjectCreationOperation.Initializer` 的运行时具体类型是
  **`Microsoft.CodeAnalysis.Operations.ObjectOrCollectionInitializerOperation`**
  （`Kind=ObjectOrCollectionInitializer`，实现 `IObjectOrCollectionInitializerOperation`）；
  对象/集合/字典索引初始化器**共用同一类型**。成员项集合是 **`IEnumerable<IOperation>`**
  （5.0.0 实测；用 `foreach`/`Count()`，**不是** `ImmutableArray` 的 `.Length`）。
- 逐项外形（`new Dto { Prop = 1, Field = 2, Inner = { Prop = 3 }, Items = { 4, 5 } }`）：

  | 源码项 | Item 具体类型（FullName） | 关键成员 |
  | --- | --- | --- |
  | 属性写 `Prop = 1` | `SimpleAssignmentOperation`（`ISimpleAssignmentOperation`） | `.Target` = `PropertyReferenceOperation`（`IPropertyReferenceOperation`），`.Property.Name="Prop"`、`.Property.SetMethod.Name="set_Prop"`、`IsInitOnly=false`、`.Arguments` 空 |
  | 字段写 `Field = 2` | `SimpleAssignmentOperation` | `.Target` = `FieldReferenceOperation`（`IFieldReferenceOperation`），`.Field.Name="Field"`、`.Field.IsReadOnly=false` |
  | 嵌套成员初始化 `Inner = { … }` | **`MemberInitializerOperation`**（`IMemberInitializerOperation`） | `.InitializedMember` = `PropertyReferenceOperation`；`.Initializer.Initializers` = 内层项 |
  | 集合成员初始化 `Items = { 4, 5 }` | `MemberInitializerOperation` | `.Initializer.Initializers` 为 2 个 Add 项（见 6.2） |
  | 嵌套创建 RHS `P = new I { … }` | `SimpleAssignmentOperation`，`.Value` = `ObjectCreationOperation`（自带 `.Initializer`） | — |

- **字典索引初始化器** `new D { [k] = v }`：item 也是 `SimpleAssignmentOperation`，`.Target`
  = `PropertyReferenceOperation`，`Property.IsIndexer=true`、`Property.Name="this[]"`、
  `SetMethod.Name="set_Item"`、`.Arguments` 含键实参（多维同形）。**不存在**
  `ImplicitElementAccessOperation` 类型的成员项；消费端按 `IPropertyReferenceOperation.IsIndexer`
  识别（设计 §4 决策 9 的"ImplicitElementAccessOperation 或实际类型"以实测收口为属性引用）。

### 6.2 Add 绑定外形（重大 API 变化：`ICollectionElementInitializerOperation` 已废弃）

- **`ICollectionElementInitializerOperation` 在 Roslyn 5.0.0 已标记 `[Obsolete]`（使用即
  CS0619 编译错误）**，替代物为 **`IInvocationOperation` / `IDynamicInvocationOperation`**。
  设计 §2 决策 8/§4 表格中"`AddMethod`/`Arguments`"的引用对象**改为
  `IInvocationOperation.TargetMethod` / `.Arguments`**（`IArgumentOperation`）。
- 集合初始化器元素项实测（含顶层集合初始化器与成员集合初始化器，形状一致）：

  | 形态 | Item 具体类型 | `TargetMethod` 观测 |
  | --- | --- | --- |
  | 单参 `new List<int> { 1, 2 }` | `InvocationOperation`（`Kind=Invocation`） | `List<int>.Add(int)`，`ReturnsVoid=true`，`ReducedFrom=null`，`Arguments.Length=1`，`ArgumentKind=Explicit` |
  | 多参 `new Pairs { { 1, 2 } }` | `InvocationOperation` | `Pairs.Add(int, int)`，`Arguments.Length=2`（**不**发射 `put`） |
  | 扩展 Add（泛型集合 + 扩展方法） | `InvocationOperation` | `GenExt.Add(GenBag, int)`，`IsExtensionMethod=true`、**`ReducedFrom=null`**（**非** reduced 形式），`Arguments[0].Value.Type=GenBag` 为接收器；重发 reduced 语法 `tmp.Add(x)` 即可复现绑定 |
  | fluent（非 void 返回） | `InvocationOperation` | `FluentBag.Add(int)`，`ReturnsVoid=false`；表达式语句丢弃返回值 |
  | params Add `Add(params int[])` 配 `{1,2,3}` | **3 个** `InvocationOperation`（每个源码元素一次调用） | 每次 `Arguments.Length=1`，`Parameter.IsParams=true`、`ArgumentKind=ParamArray`、实参值类型 `int[]`（**展开为单元素数组调用**，非一次 `Add(a,b,c)`）——设计 §2 决策 8⑦ 的"展开形式"以实测为准 |
  | IEnumerable 无 Add `new NoAdd { 1 }` | **`InvalidOperation`**（`IInvalidOperation`） | 基线报错 **CS1929**（"不包含 Add 的定义"，CS1922/CS0117 族）；M2 映射 CS2J0533 |

  （注：扩展 Add 的最小合法夹具需集合实现 `IEnumerable<T>` 的空洞实现；非泛型空 `IEnumerable`
  夹具会因无绑定 Add 报 CS1950，不构成扩展 Add 探针。）

### 6.3 init / readonly 基线

- init-only 属性：初始化器内合法；`IPropertyReferenceOperation.Property.SetMethod.IsInitOnly
  == true`（`ISimpleAssignmentOperation.Target`）。C11 放宽后输入树为普通 `set`（`IsInitOnly=false`）
  + 字段，探针以对照输入确认。
- readonly 字段 / get-only 属性出现在初始化器：基线 **CS0191**（只读字段）/**CS0200**（只读属性），
  且该初始化器项的左侧 `GetSymbolInfo(...).Symbol == null`（绑定失败）——M1 无法依赖符号做
  防御性 CS2J0531 分类，只能按语法 + 基线错误位置归因。

### 6.4 target-typed `new()` 的 ConvertedType

- 全部上下文（字段初始化器 / 属性初始化器 / `: this(...)` 实参 / 局部声明 / `return` /
  实参位 / 三元双支 / 可空 `return`）实测 `GetTypeInfo(node).ConvertedType` 均为
  `System.Collections.Generic.List<int>`（`NullableAnnotation=NotAnnotated`，可空目标亦取底层 T，
  **无** `List<int>?` 注解串）；`GetOperation` 统一为 `ObjectCreationOperation`。
- 因此决策 10 的"可空目标剥注解"在 ConvertedType 层已天然成立（UnderlyingType == ConvertedType）。

### 6.5 dirty 门 / residual 谓词

- trigger：`ObjectCreationExpressionSyntax{Initializer:not null}` 或
  `ImplicitObjectCreationExpressionSyntax`（裸 `new()` 与带初始化器均算）。
- **零误伤**：`new int[]{…}`、`new[]{…}`（`ArrayInitializerExpression`）、裸 `new C()`、
  匿名对象 `new { A = 1 }` 均不触发。
- residual 三类节点：`ObjectInitializerExpression`、`CollectionInitializerExpression`
  （含 `ComplexElementInitializerExpression` 元素）、`ImplicitObjectCreationExpressionSyntax`；
  `ArrayInitializerExpression` 排除；配对用共享 `ExpressionDesugarSyntaxFacts.ExplainedAnnotation`
  （祖先链查询）。
- 模板：`new Dto { A = 1 }` 源码的 trigger 计数=4（a/b/`new(){…}`/`new()`）；
  `new { A = 1 }` 若计入会与匿名对象冲突，实测排除。

### 6.6 M0 行为锁定（同测试文件）

clean 文件 `Changed=false` + `FilesSkippedClean=1` 且字节不变；仅含初始化器（无 C36 语法）的
dirty 文件 `Changed=false` + `FilesDirtyNoRewrite=1`、输出原样、**零诊断**（Initializers M0 不产
任何码；CheckResiduals 恒 0）；初始化器与 C36 混排文件 C36 站点照常改写（`new int[]{…}`、计数不变）
且二次运行对 C24 站点字节稳定；`SemanticModel=null` 的初始化器 dirty 文件不崩、`FilesDirtyNoRewrite=1`。

## 7. 已知基线失败（开工前既有，与 C24 无关）

- Tests 工程 5 个：SystemPrivateXmlRuntimeResource ×1、CSharpXmlCompileRegression ×2、
  EnumDeclaration UnsignedNumericToken ×2；
- ProjectTests 1 个：EqualityConcat e2e（缺 compat jar）；
- ConversionTests 0。

（依据 C36 计划 §7.4 终验记录；每阶段提交前以实际失败清单复核，只增不掩盖。）

**M0 实际复跑（2026-10-02，Release）**：`dotnet build -c Release CSharpToJavaConverter.slnx`
= **0 错误**；`dotnet test -c Release tests/CSharpToJava.Tests/CSharpToJava.Tests.csproj`
= **2548 通过 / 0 失败**（§7 列出的 5 个基线失败在本基线未复现，未新增任何失败）；
`CollectionExprProjectTests` 4/4 通过、`ListPasses_IncludesExpressionDesugar_WithC36CatalogItem`
（断言已更新为 `rules=C36, C24`）通过。
ProjectTests 全量复跑：959 通过 / 5 失败，均为**与 C24 无关的既有/环境失败**——
3 例 `LexicalNormalizeProjectTests` 系 `SampleLexicalNormalizeLiteralTests` 的 CRLF/LF
行尾断言（`Expected "line1\nline2" / Actual "line1\r\nline2"`，Windows checkout 环境面，隔离复跑
稳定复现），2 例（StripDefault `Rerun_DoesNotLeaveOldArtifacts`、ForeachSource
`Fixture_WrapsStringSites`）在隔离复跑通过（全量并发下的构建/临时目录资源争用）。C24 不触碰
lexical-normalize/foreach-source/strip-default 任一 pass。
