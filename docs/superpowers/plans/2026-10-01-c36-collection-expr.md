# C36 集合表达式去糖 — 实施计划（2026-10-01）

**设计**：[2026-09-29-c36-collection-expr-design.md](../specs/2026-09-29-c36-collection-expr-design.md)
**worktree**：`.worktrees/preproc-c36-collection-expr`，分支 `feature/preproc-c36-collection-expr`
**base**：`0a8d8606`（= dev-3.x `28ae4594` + C36 文档 base 前移提交）

## 0. 基线裁决（与设计文档差异点）

截至 base，C23 锚点/C24/C35 均未合入（`expression-desugar` 仍为 Stub，仓内无 `ExpressionDesugar/`）。
按 **C15（numeric-boundary）/C47（equality-concat）先例独立落地**：

1. 翻转 `expression-desugar` stub 为 Implemented，**默认关**（`--enable-pass expression-desugar` 启用）；
   `catalogItems: "C36"`；RunAfterIds 沿用 `[null-coalesce]`；中间目录 `.cs2j-expression-desugar-src`。
2. C36 自带最小族基建（`ExpressionDesugar/Shared/`），全部接口只增不改，为 C23/C24/C35 预留追加位；
   规则集固定序 `Interpolations(C23) → Initializers(C24) → NullConditional(C35) → CollectionExpr(C36)`，
   本期只注册 `CollectionExpr` 末位。
3. C24 未合入：M1 自带最小上下文语句化（`CollectionContextLowering` + 族 `TempLifter`），登记技术债；
   C24 合入时只增提取合并（设计 §3.3-4）。
4. 不改任何 C#→Java 转换器代码（`Transformers/**` 窄规则保留为默认关安全网，退役另立 PR）；不新建支撑工程。
5. 临时名前缀按设计硬不变式 #4：`__ed_`（文件级发号 + 碰撞避让）。
6. Core 单测落 `tests/CSharpToJava.Tests/CollectionExprDesugarUnitTests.cs`（仓内 C47 范式，且该工程有
   InternalsVisibleTo）；项目级 fixture 测试落 `tests/CSharpToJava.ProjectTests/`（范式：EqualityConcatProjectTests）。

## 1. 目标目录结构

```
src/CSharpToJava.Core/ExpressionDesugar/
  Shared/
    ExpressionDesugarOptions.cs          # 族 options：含 CollectionExpr 子节（nullable → 默认 new()）
    ExpressionDesugarStatistics.cs       # 族统计聚合（本期仅 coll 段）
    ExpressionDesugarDiagnostic.cs       # 诊断 record（Severity/Code/Message/FilePath/Line/Column）
    ExpressionDesugarResult.cs           # record(OutputCode, Changed, Diagnostics, Statistics)
    ExpressionDesugarTempLifter.cs       # PendingTemp（语句序列，不止声明）/Apply；锚点查找；
                                         # 嵌入语句包块；expression-bodied 展开（参考 C47 TempLifter）
    ExpressionDesugarSyntaxFacts.cs      # IsInExpressionTree、可提升位置谓词、residual 注解
    IExpressionDesugarRule.cs            # Id/TriggerSyntax/Analyze/CreateRewriter 最小契约
    ExpressionDesugarRuleSet.cs          # 固定规则序；本期仅 CollectionExpr
    ExpressionDesugarPreprocessor.cs     # public 入口：Preprocess(source, model, options)
  CollectionExpr/
    CollectionExprOptions.cs             # Strict/Scope(灰度)/Builder/RewriteSpanAsArray/EnableGuard
    CollectionExprModels.cs              # Plan/ElementPlan/SpreadPlan/TargetKind/SpreadSourceKind/Outcome
    CollectionExprDiagnostics.cs         # CS2J0570–CS2J0589 常量+级别+消息模板
    CollectionExprStatistics.cs          # 设计 §6 全字段
    CollectionExprSyntaxFacts.cs         # residual 种类、元素种类穷举（禁止 OfType 静默过滤）
    TargetTypeResolver.cs                # ConvertedType/IOperation 六类分派 + 接口表
    CollectionBuilderAnalyzer.cs         # CollectionBuilderAttribute 读取 + IOperation 工厂绑定
    SpreadExpander.cs                    # 源分类 + AddRange/foreach-Add 发射
    CollectionExprEmitter.cs             # 零临时 + 语句化发射
    CollectionContextLowering.cs         # 【C24 技术债位】最小表达式位语句化
    CollectionExprRewriter.cs            # 自底向上 CSharpSyntaxRewriter
    NewSyntaxGuardAnalyzer.cs            # CE-GUARD：0585/0586/0587/0588
    CollectionExprRule.cs                # IExpressionDesugarRule 实现
src/CSharpToJava.CLI/Preprocessing/Adapters/ExpressionDesugarPassAdapter.cs  # 仿 EqualityConcatPassAdapter（无支撑库）
tests/CSharpToJava.Tests/CollectionExprDesugarUnitTests.cs
tests/CSharpToJava.ProjectTests/CollectionExprProjectTests.cs（M3 fixture）
tests/SampleExpressionDesugarSolution/（M2/M3，仿 SampleEqualityConcatSolution）
```

## 2. 集成点清单（注册翻转）

- `src/CSharpToJava.CLI/Preprocessing/PassStubCatalog.cs`：删除 ExpressionDesugar stub 条目
  （常量 `Ids.ExpressionDesugar` 保留），加 C47 式翻转注释。
- `src/CSharpToJava.CLI/Preprocessing/PassCatalog.cs`：在 NullCoalesce 描述符之后插入 Implemented 描述符
  （物理位置保证拓扑并列时紧随 null-coalesce）：defaultEnabled:false、needsSemanticModel:true、
  verifyBuildByDefault:true、verifyTestsByDefault:true、catalogItems:"C36"。
- `PassChainFactory.cs`：加 `ExpressionDesugarOptionsFactory`（required）+ switch 分支；
  **不**加 convert-project legacy toggle（仿 enum-declaration，仅 descriptor 默认 + --enable-pass 可达）。
- `CliPreprocessChain.cs`：透传 factory。
- `Program.cs`：两处 `PassChainConfiguration`/`CliChainRequest` 构造点（L185 区块、L3600 区块）补 factory。
- `tests/CSharpToJava.Tests/Preprocessing/PassChainOrderTests.cs`：
  LockedChain 在 null-coalesce 后插 `expression-desugar`；EnabledByDefault 排除之；
  catalogItems 断言加 `("C36", byId[Ids.ExpressionDesugar])`；ChainFactory config 补 factory；
  BuildLegacyToggles 覆盖断言的排除列表加 expression-desugar。
- 其余可能断言 pass 清单/计数的测试：以 `dotnet test` 实际失败为准只增修正，不削弱既有断言。

## 3. 阶段划分（每阶段：实现 → Release build/test 全绿 → 提交）

### M0 探针 + 骨架 + 注册（不翻转语义；pass 注册但空转）

- Shared 全部最小骨架 + CollectionExpr 全部文件骨架（类型/枚举/常量/字段齐备，方法可 NotImplemented 或安全空转）。
- **Roslyn 5.0 探针**（写成可运行的 xunit 事实，锁死 API 外形；结果追加到本文 §6）：
  - 数组/List/接口/Span/CollectionBuilder 各目标的 `GetTypeInfo(node).ConvertedType` 与
    `GetOperation(node)`（`ICollectionExpressionOperation`）子操作外形；
  - spread 元素 operation（`ISpreadOperation` 或实际外形）与源元素类型取法；
  - `CollectionBuilderAttribute` 在 INamedTypeSymbol 上的构造参数（builder 类型+工厂名）与
    IOperation 中编译器绑定的工厂方法符号外形；
  - CS9175/CS9176 基线复现；接口具体类表逐行核实。
- dirty 门：文件无 `CollectionExpressionSyntax` 且无防线语法 → Changed=false 字节保留。
- 规则 Analyze 产出站点计数（CS2J0570 Info 内部统计），**不发射任何改写**；pass 端到端可被
  `--enable-pass expression-desugar` 选中、空转、clean copy、build/test 绿。
- 单测：探针事实测试 + clean 文件 BOM/Changed=false + 注册表测试更新。
- 验收：`dotnet build -c Release CSharpToJavaConverter.slnx` 与三个测试工程全绿。
- 提交：`feat(preprocessing): C36 collection-expr M0 探针+规则骨架+pass 注册（默认关、空转）`

### M1 数组 + 可变集合 + spread（P3 首位 bug 收口）

- CE-1/2/3：T1 数组零临时 `new T[]{…}`（空 `new T[]{}`）；T3 可 Add 具体类
  （无参 ctor + 可访问 Add，IOperation/符号核实）`new C<T>(){…}`（空 `new C<T>()`），保可变；
  未知目标 → 0571/0581；元素遍历覆盖 **全部 CollectionElementSyntax 种类**，未知种类显式报错（禁 OfType 丢数据）。
- CE-5/6：SpreadExpander 源分类（数组/IEnumerable/string→AddRange；Span→foreach Add；
  自定义 IEnumerable 无 AddRange→foreach Add；不可枚举→0576）；源单次出现、严格保序。
- CE-7：含 spread → `__ed_n` List 临时 + Add/AddRange/foreach 语句序列；数组目标 `.ToArray()` 收尾；
  可提升位置：局部声明 RHS/return/yield return/throw/赋值/表达式语句/三元分支/实参交错；
  不可提升（字段/属性初始化器、特性、默认参数、查询子句）→ 0583。
- CE-12：residual 闸门 0589；二次运行零 diff 幂等；任一 Error → 整文件原子不改写。
- 单测：设计 §7.1 矩阵 1-3/10/11/15；文本断言 + 统计分桶 + 输出可编译断言 + 幂等。
- 验收：Release build + 三工程 test 全绿。
- 提交：`feat(preprocessing): C36 M1 数组/可变集合/spread 展开+语句化（CS2J0570-0583/0589）`

### M2 接口目标 + 嵌套 + Span + CollectionBuilder

- CE-4：接口→规范具体类表（List<T> / Dictionary<K,V> / List<object?>），重编译为最终裁决，表外 0581。
- CE-8：嵌套自底向上，内层按自身 ConvertedType 独立分派，锚点先于外层。
- CE-9：Span/ReadOnlySpan 目标 → 数组形态 + 0578；ref struct 元素 → 0582。
- CE-10：CollectionBuilder 按开放问题裁决 1 做 IOperation 通用工厂发射
  （`BuilderType.CreateMethod(…)`，无 spread 零临时数组传参；含 spread 先 List 再 ToArray；
  ReadOnlySpan 隐式转换由编译器完成；字典 builder/自定义形参以探针为准，不能覆盖形态保留 0580）。
- 单测：矩阵 4/6/7/8/9/12/13；SampleExpressionDesugarSolution 初版（原生/预处理双跑：可变性、内容、顺序）。
- 验收：Release build + 三工程 test 全绿。
- 提交：`feat(preprocessing): C36 M2 接口具体类/嵌套/Span 数组形态/CollectionBuilder 工厂发射`

### M3 新语法防线 + 画像收口 + 交付

- CE-11：static abstract/virtual 接口成员声明 0585 strict Error（调用站点 Warning 盘点）；
  scoped 参数 0586 Info / 局部 Warning；泛型 attribute 声明 0587 Error / 应用 Warning；
  表达式树内集合表达式 0588 防御 Info；0584 汇总。
- §5 CLI 收口：摘要 coll 段 + new-syntax inventory 小节；AfterFiles residual 全树复扫（按两类语法配对）；
  strict 报错不留半成品目录（沿用 runner 语义，加测试钉）。
- fixture 补 builder 报错 sln/static-abstract 拒绝用例；ProjectTests：Success/诊断/退出码/零 residual；
  BOM 字节复制；`--list-passes` catalogItems 含 C36（以实际输出测试钉）。
- prompts/preprocessor-catalog.md C36 状态按实际更新（只增状态字段，沿用既有条目格式）。
- 验收门槛：设计 §8 第 1/2/4/6 项（Java 半区核查 §8-3 与 sdk 画像属后续 PR，本文登记不做）。
- 全量：`dotnet build -c Release CSharpToJavaConverter.slnx`；
  `dotnet test -c release` 三个测试工程（Tests / ProjectTests / ConversionTests）全绿。
- 提交：`feat(preprocessing): C36 M3 新语法防线+residual 全树闸门+fixture/CLI 收口（默认关）`

## 4. 跨阶段硬纪律

1. 通用优先：禁止按标识符/文本猜目标类型，一律 ConvertedType + IOperation；禁止 `List.of` 式兜底。
2. 禁止 `OfType<ExpressionElementSyntax>()` 式静默过滤；每个元素语法种类必须有处置或显式诊断。
3. spread 源表达式在产物中只出现一次；严格源码序；不引入 System.Linq 依赖形态。
4. 产物为普通合法 C#：每轮输出经 AdhocWorkspace 格式化；干净文件字节级复制（含 BOM）。
5. 错误即门：strict Error → 整文件 Changed=false；非 strict 降 Warning 且节点保留。
6. 不碰 Transformers/E09/C01 代码；E09 byte 衔接只出结论（测试观察），不改 E09。
7. 每阶段提交前必须本地跑过 Release build + 相关测试；提交信息带阶段与诊断码范围。

## 5. 验证命令

```powershell
dotnet build -c Release CSharpToJavaConverter.slnx
dotnet test -c Release tests/CSharpToJava.Tests/CSharpToJava.Tests.csproj
dotnet test -c Release tests/CSharpToJava.ProjectTests/CSharpToJava.ProjectTests.csproj
dotnet test -c Release tests/CSharpToJava.ConversionTests/CSharpToJava.ConversionTests.csproj
```

## 6. M0 探针记录

环境：.NET 10 / `Microsoft.CodeAnalysis.CSharp` 5.0.x；探针固定在
`tests/CSharpToJava.Tests/CollectionExprDesugarUnitTests.cs`（12 个 Fact）。
集合表达式需 `LanguageVersion >= CSharp12`；实测**不指定 ParseOptions 的默认版本**
（LatestMajor）即可解析并绑定集合表达式，探针与 M0 行为测试显式使用 `LanguageVersion.Preview`。

### 6.1 IOperation 运行时外形（5 类目标 + 元素路径）

- `model.GetOperation(CollectionExpressionSyntax)` 对所有目标（数组 / `List<int>` /
  `IEnumerable<int>` / `Span<byte>` / 自定义 builder 类）统一返回公共具体类型
  **`Microsoft.CodeAnalysis.Operations.CollectionExpressionOperation`**，实现
  `ICollectionExpressionOperation`；元素取 **`.Elements`**（`ImmutableArray<IOperation>`）。
- 同型元素为裸 **`Microsoft.CodeAnalysis.Operations.LiteralOperation`**
  （`Kind=Literal`，`Type=int`），无转换包裹；`int -> byte` 收窄元素为
  **`Microsoft.CodeAnalysis.Operations.ConversionOperation`**（`Type=byte`，
  `.Operand` 为 int 字面量）。
- spread 元素为 **`Microsoft.CodeAnalysis.Operations.SpreadOperation`**
  （`Kind=Spread`，**自身 `Type` 为 null**），实现 `ISpreadOperation`；
  源表达式取 **`.Operand`**。实测 `.Operand` **无隐式转换包裹**，直接是
  `ParameterReferenceOperation`（`Microsoft.CodeAnalysis.Operations.ParameterReferenceOperation`），
  `Type` 为源实参的自然类型。六类源实测：

  | 源实参 | Operand.Type（ToDisplayString） |
  | --- | --- |
  | `int[]` | `int[]` |
  | `List<int>` | `System.Collections.Generic.List<int>` |
  | `IEnumerable<int>` | `System.Collections.Generic.IEnumerable<int>` |
  | `Span<int>` | `System.Span<int>` |
  | `ReadOnlySpan<int>` | `System.ReadOnlySpan<int>` |
  | `string`（目标 `char[]`） | `string` |

  → M1 元素类型必须从 `ISpreadOperation.Operand.Type` 自行分类
  （数组取 `IArrayTypeSymbol.ElementType`、Span 取类型实参、string→char、
  IEnumerable\<T\> 取类型实参），不能依赖转换节点。

### 6.2 ConvertedType 形态

| 目标 | `GetTypeInfo(site).ConvertedType.ToDisplayString()` |
| --- | --- |
| `int[]` | `int[]` |
| `List<int>` | `System.Collections.Generic.List<int>` |
| `Span<byte>` | `System.Span<byte>` |
| 自定义 `[CollectionBuilder]` 类 | `MyCollection`（自身名，非 builder 类型） |

### 6.3 CollectionBuilder 外形与 Create 绑定位置

- 属性读取：`INamedTypeSymbol.GetAttributes()` 取
  `System.Runtime.CompilerServices.CollectionBuilderAttribute`，**2 个构造参数**：
  - `ConstructorArguments[0]`：`TypedConstantKind.Type`，`Type` 显示名 **`System.Type`**
    （注意 `SpecialType` 不含 System_Type），`Value` 为 builder 的 `INamedTypeSymbol`；
  - `ConstructorArguments[1]`：`TypedConstantKind.Primitive`，`SpecialType.System_String`，
    值为工厂方法名（`"Create"`）。
- **`CollectionExpressionOperation` 子树内不存在任何 `IInvocationOperation`**
  （探针实测计数 0）：builder 的 Create 绑定**不暴露在 IOperation 树中**。
  → M1 只能依据特性元数据（builder 类型符号 + 方法名 + ReadOnlySpan 形参元素类型）
  自行解析，`CollectionBuilderAnalyzer.FindBoundFactoryInvocation` 的
  IOperation 扫描路径在 5.0 上不可用，M1 改走符号解析。
- 合成 builder 类若未声明元素类型，编译给 **CS9188**（"具有 CollectionBuilderAttribute，
  但没有元素类型"）Warning，不影响特性读取；M1 fixture 需让集合实现
  IEnumerable\<T\> 以保持干净编译。

### 6.4 接口目标核实表 + CS9174

| 声明目标 | ConvertedType 全名 | 集合表达式可否构造 |
| --- | --- | --- |
| `IEnumerable<int>` | `System.Collections.Generic.IEnumerable<int>` | 可 |
| `IList<int>` | `System.Collections.Generic.IList<int>` | 可 |
| `ICollection<int>` | `System.Collections.Generic.ICollection<int>` | 可 |
| `IReadOnlyList<int>` | `System.Collections.Generic.IReadOnlyList<int>` | 可 |
| `IReadOnlyCollection<int>` | `System.Collections.Generic.IReadOnlyCollection<int>` | 可 |
| `IDictionary<int,string>` | `System.Collections.Generic.IDictionary<int, string>` | **CS9174 不可构造** |
| 非泛型 `IEnumerable` | `System.Collections.IEnumerable` | **CS9174 不可构造** |

CS9174 消息："无法使用集合表达式初始化类型 X，因为该类型不可构造。"
→ M1 对 IDictionary / 非泛型 IEnumerable 站点走 skip 诊断（0570 系），不得生成构造。

### 6.5 CS9175 / CS9176 实测

- `var x = [];` → **CS9176** "集合表达式没有目标类型。"（语义错误，语法仍解析为
  `CollectionExpressionSyntax`）。
- `Expression<Func<int[]>> e = () => [1];` → **CS9175** "表达式树不能包含集合表达式。"

### 6.6 M0 行为锁定（同测试文件）

clean 文件 `Changed=false` + `FilesSkippedClean=1` 且字节不变；dirty 文件
（含 `[1,2,3]`）M0 空转：`Changed=false` + `FilesDirtyNoRewrite=1`，
`CollectionSites/ByTargetArray` 等计数正确，输出原样；`SemanticModel=null` 时
dirty 文件不崩，返回 **CS2J0571 Warning**；仅含 CE-GUARD 语法（接口
`static abstract` 成员）的文件同样判 dirty 但站点数为 0。
