# Java 保留字归一化预处理器设计

**日期**: 2026-09-28
**范围**: 新增 CLI 级 C#→C# 预处理器（Core `JavaKeywords\` + CLI `ProjectJavaReservedWordPreprocessor`），不涉及任何 C#→Java 转换代码
**状态**: 设计已确认，待实施
**对应提交**: 86ed95592f9631bfadeaf8a91ed94044e94fa624（转换器侧 true/false/null 与 op_True/op_False 特例的根因修复）

## 1. 背景与目标

提交 86ed955 在转换器内部修了两件事：

1. `JavaNaming.IsJavaKeyword` 补入 `true/false/null/_/yield`，`EscapeJavaKeyword` 对保留字追加 `"Value"`，避免 C# 成员 `True()`/`False()`/`Null()` 经转换器命名派生（方法名首字母小写）后成为 Java 保留字面量。
2. `MethodTransformer.ConvertOperatorName`（重构后位于 `OperatorTransformer`）补 `op_True→isTrue`、`op_False→isFalse`；`UnaryExpressionTransformer` 改用统一 `OpSymbolToJavaName` 查表。

本设计把这两类处理**前移到一个独立的 C#→C# 预处理器**：转换器输入的是"已经安全的 C#"——成员名不再是 Java 保留字，源码中不再存在 `operator true/false` 声明与隐式布尔使用点。转换器侧特例的删除为后续 PR（本特性不动转换器代码，冗余清单见 §10）。

本特性严格复用已落地的 record-readonly 预处理器架构（`src/CSharpToJava.Core/RecordReadOnly/` + `src/CSharpToJava.CLI/ProjectRecordReadOnlyPreprocessor.cs`）：三阶段（分析→计划→逐文件重写）、整树重编译比对基线、plan 级原子回退、`FormatAndValidate`、`ProjectPreprocessSource` 全套驱动。不另起一套。

**两类转换的性质不同**：

| 族 | 规则 | 可失败性 | 回退 |
|---|---|---|---|
| 关键字逃逸 | JK-1~JK-4 | 永不破坏可编译性（计划期确定性消歧） | 不参与回退，仅 `skip` 配置亦不可屏蔽（必做） |
| operator true/false 降级 | JK-5~JK-8 | 保守可失败（表达式树/dynamic/绑定不可判定） | plan 原子跳过（FB-1），整树验证失败按 plan 回退（FB-2） |

**硬不变式**（与 record-readonly 相同）：

1. 中间源码树在预处理后永远可编译（整树重编译相对基线无新增错误）。
2. 每个 plan 的转换是原子的（全转或全不转）。

## 2. 已确认的决策

| 决策点 | 结论 | 备注 |
|---|---|---|
| 总体架构 | 方案 A：语义驱动三阶段（分析→计划→重写），自研 `CSharpSyntaxRewriter` + `ISymbol` 改名表，完全复刻 record-readonly | 否决 Roslyn `Renamer`（Workspace 文本 API，与 plan 排除/回退模型不合；nameof 策略相反）与纯文本替换（作用域/重载/别名不可辨） |
| 保留字判定真相源 | 直接引用 `JavaNaming.IsJavaKeyword` / `JavaNaming.EscapeJavaKeyword`，禁止自维护保留字副本 | 新名规则 = 原名追加 `"Value"`，与 `EscapeJavaKeyword` 完全一致 |
| 大小写口径 | 剥 `@` 后原名**及其首字母小写形态**任一被逃逸即命中；新名保留原大小写追加 `Value` | 见 §4.1 JK-0；首字母小写表达式与转换器 `MethodTransformer.JavaName` 同一派生 |
| nameof | 字符串值保持原样：引用被改名符号的 `nameof(...)` 替换为原名常量字符串字面量 | 既保外部契约又保可编译（§4.2 JK-4a） |
| `&&`/`||` 降级 | 与 C# 编译器同构的精确降级（`IsFalse` 短路门 + `&`/`|`），保持 T 类型与三值逻辑语义 | 见 §4.3 JK-6 |
| 布尔上下文口径 | 只改写语义模型判定**编译器实际绑定 op_true/op_false** 的站点；自定义 bool 转换、`operator !` 站点不动 | 漏网由整树重编译捕获并回退 plan |
| 其它运算符 | op_Xxx（Addition 等）一律不处理 | JK-7 |
| 管线集成 | 独立预处理器、convert-project **默认关**（`--java-keywords` 显式开启）+ 独立 verb `java-keywords`；位于预处理链**最后**（EliminateGoto 之后、转换之前） | 分析对象是其它预处理器产出的最终中间树 |
| overrides | `config/JavaKeywordsOverrides.json`，`force`/`skip` **仅作用于运算符降级 plan**；关键字改名不可屏蔽 | 默认空，文件可选 |
| C#→Java | 完全不动 | §10 列冗余清单 |

## 3. 总体架构与组件

### 3.1 组件划分

```
src\CSharpToJava.Core\JavaKeywords\
  JavaReservedWordPreprocessor.cs  # 对外入口：RewriteFile/BindCompilation/ExcludePlans/Plans/
                                   #   TryAttributeDiagnostic/FormatAndValidate
  JavaKeywordModels.cs             # JavaKeywordOptions/Statistics/Diagnostic(JK0xx)/FileResult/Severity
  JavaKeywordAnalyzer.cs           # Phase 1：全编译改名符号清单 + 运算符布尔站点分类
  JavaKeywordPlan.cs               # JavaKeywordPlan/ConversionPlan/PlanBuilder + 碰撞消歧
  KeywordRenamingRewriter.cs       # Phase 3a：声明点与引用点改名、nameof 字面量化
  OperatorTrueFalseRewriter.cs     # Phase 3b：operator 声明→方法 + 布尔上下文改写
  JavaKeywordSyntaxFacts.cs        # RewriteAnnotation、NeedsEscape/NewName、站点分类小工具

src\CSharpToJava.CLI\ProjectJavaReservedWordPreprocessor.cs
  # 中间树复制/枚举/编译/RewriteFile/回写/restore，全套复用 ProjectPreprocessSource
  # IntermediateDirectoryName = ".cs2j-java-keywords-src"

config\JavaKeywordsOverrides.json  # { "force": ["Ns.Type"], "skip": ["Ns.Type"] }，默认 []
```

公开契约（与 `RecordReadOnlyPreprocessor` 同形）：

- `JavaReservedWordPreprocessor(JavaKeywordOptions? options = null)`
- `JavaKeywordFileResult RewriteFile(string sourceCode, SemanticModel semanticModel, JavaKeywordStatistics statistics, List<JavaKeywordDiagnostic> diagnostics)` → `{ OutputCode, Changed }`
- `void BindCompilation(Compilation compilation)`
- `void ExcludePlans(IEnumerable<string> planIds)`
- `IReadOnlyList<JavaKeywordPlan> Plans`（每个 plan 含 `Id` / `Symbol` / `TouchedFiles` / `Outcome` / `FailCode` / `FailReason` / `FailLocation` / `OwnsTouch(file, typeName, memberName)`）
- `bool TryAttributeDiagnostic(Diagnostic, IReadOnlyDictionary<string,string> rewrittenByFile, out string planId)`
- `internal static string? FormatAndValidate(CompilationUnitSyntax root)`：与 record-readonly 同款三候选（改动区间格式化→整文件格式化→原文），重解析零错误取首个。

### 3.2 三阶段数据流

1. **分析**（首次 `RewriteFile`/`BindCompilation` 时基于 `Compilation` 构建一次并缓存）：
   - 遍历编译中全部**源内声明符号**（`IsInSource`，partial 多部件按符号天然合并），对每个名字 `NeedsEscape` 命中的符号登记一条改名候选。
   - 以 `ISymbol`（`SymbolEqualityComparer.Default`）为键建立**全编译唯一改名表** `ISymbol → newName`，新名经碰撞消歧（§4.2 JK-4b），同一符号在所有文件、所有出现（声明 + 引用）同查一表。
   - 枚举每个声明了 `operator true`/`operator false`（C# 强制成对声明，CS0216/0217）的类型；用语义模型（`SemanticModel.GetOperation`/`GetSymbolInfo`/`GetTypeInfo`）找出编译器**实际绑定**这两个运算符的全部使用点并分类；含不可行站点（§4.3 JK-8）的类型判 Infeasible。
   - 编译方式与 record-readonly 相同：`CSharpParseOptions(LanguageVersion.Latest)` + 合成 `__cs2j_global_usings.g.cs`（`ImplicitGlobalUsings.BuildForProject`）+ `ProjectUnsignedTypePreprocessor.GetBasicReferences()`，`allowUnsafe: true` 的 DLL 编译。
2. **计划**（`JavaKeywordPlan`）：
   - `KeywordRename` plan：每个被改名符号一份；永不 Infeasible；登记符号全部源内位置所在文件为 TouchedFiles。
   - `OperatorTrueFalse` plan：每个声明 op_true/false 的类型一份；Infeasible 时 `Outcome=Infeasible, FailCode=JK020`；TouchedFiles = 声明文件 + 全部布尔使用点文件。
   - plan 是回退的最小单位；plan 间相互独立（运算符 plan 的排除不影响同类型的关键字改名，反之亦然）。
3. **重写**（每文件）：取本文件的 activePlans（未排除且 TouchedFiles 命中本文件），依次执行 `KeywordRenamingRewriter` → `OperatorTrueFalseRewriter`；改动节点统一挂 `RecordSyntaxFacts.RewriteAnnotation` 同款标注（新建 `JavaKeywordSyntaxFacts.RewriteAnnotation = new("Cs2jJavaKeywordRewrite")`），随后 `FormatAndValidate`。CLI 驱动侧流程与 record-readonly 逐行同构：复制源码树 → 基线错误集 → 多轮迭代 { 每轮从原始副本重放 → 整树建编译 → 逐文件 RewriteFile → 整树重建编译 → DiffErrors → TryAttributeDiagnostic 归属 → ExcludePlans 回退该 plan 的 TouchedFiles → 收敛 }，无法归属单 plan 的新增错误 → 本次运行全部回退（JK030）。单文件 `FormatAndValidate` 仅作快速预筛，不作回退粒度依据。
   - 稳态下 JK031 只会归属到运算符 plan（关键字改名经 JK-4a/JK-4b 静态保证不产生错误）；归属机制对两类 plan 统一生效纯属防御性安全网——一旦关键字改名 plan 被归属，说明基线/驱动异常，此时 JK030 失败中止优于产出不可信中间树。

### 3.3 CLI 接线（Program.cs 四处 + 独立 verb，均按 record-readonly 模式）

1. `ConvertProjectOptions`：`[Option("java-keywords", Default = false)]`（默认关）；另有 `[Option("java-keywords-overrides")]` 指向 overrides JSON（缺省解析 `config/JavaKeywordsOverrides.json`，查找逻辑同 `BuildRecordReadOnlyOptions`：CWD 起向上找）。
2. `ConvertProject` 执行块：位置在 **EliminateGoto 块（Program.cs 现状 line 645-682）之后、`new ConversionOptions(...)`（line 684）之前**——预处理链的最后一环。`if (opts.JavaKeywords)` → `PreprocessAsync`，打印统计，逐条输出诊断（Info 仅 verbose），失败 `return 1`，成功后 `opts.Source = result.PreprocessedSourcePath`，并设置 fingerprint 回填（仿同块）。
3. `GetPreservedDestinationDirectories`：`if (opts.JavaKeywords) preserved.Add(ProjectJavaReservedWordPreprocessor.IntermediateDirectoryName);`
4. 独立 verb `java-keywords`（`JavaKeywordsVerbOptions`）：单文件 `-i/-o`（缺省 stdout）+ 项目 `-s/-d` 双模式；`-v/--verbose`、`--overrides`、`--force a,b`、`--skip a,b`、`--strict`、`-m/--mapping`、`--no-filter-projects`，与 record-readonly verb 同构；注册进 `ParseArguments<...>` 泛型列表与 `MapResult` 分支。单文件模式自包含闸门（见 §3.4）。

### 3.4 单文件模式限制

单文件编译看不到跨文件引用/站点。凡 active plan 的 TouchedFiles 含输入文件之外路径者，一律排除并补 **JK090** 诊断（仿 RR090）。关键字改名的 plan 因此在单文件模式只覆盖声明与引用都在本文件内的符号；运算符 plan 要求声明与全部布尔使用点同文件。

## 4. 转换语义规则全集

规则编号 JK-x；诊断码 JK0xx。

### 4.1 判定谓词与命名派生（JK-0）

```
strip(name)  = name 以 '@' 开头则去首字符，否则原样            // 与 EscapeJavaKeyword 同款剥前缀
decap(s)     = s.Length > 0 ? char.ToLowerInvariant(s[0]) + s[1..] : s   // 与转换器 MethodTransformer.JavaName 同款
NeedsEscape(name):
    s = strip(name)
    return EscapeJavaKeyword(s) != s || EscapeJavaKeyword(decap(s)) != decap(s)
NewName(name) = strip(name) + "Value"
```

- 保留字表（含 `true/false/null/_/yield` 及 `abstract…while` 全表）只存在于 `JavaNaming.IsJavaKeyword`，本特性不复制、不改写。
- `decap` 分支用于捕获转换器会首字母小写的种类：`True→true→trueValue`、`Assert→assert→assertValue`。谓词对所有符号种类**统一**适用（JK-3 明确要求局部/形参/类型同理），代价是类型/局部等 Java 本可接受的 PascalCase 名字（如 `class Assert`）也会被改名——这是确定性的安全超逼近，零语义风险（仅多一些改名）。
- 不命中示例：`NULL`（decap 为 `nULL`，非保留字）、`WHILE`（`wHILE`）、`foo`、`@Foo`（剥后 `Foo`/`foo` 均非保留字）。
- 命中示例与新名：

| 原名 | strip | decap(strip) | 新名 |
|---|---|---|---|
| `True` | True | true ✅ | `TrueValue` |
| `FALSE`（字段，全大写） | FALSE | fALSE ❌ | 不改名（转换器字段不 decap，Java 标识符 `FALSE` 合法；统一谓词下 decap 不命中） |
| `Null` | Null | null ✅ | `NullValue` |
| `assert` | assert | assert ✅ | `assertValue` |
| `Assert` | Assert | assert ✅ | `AssertValue` |
| `@event` | event | event ✅ | `eventValue` |
| `@true` | true | true ✅ | `trueValue` |
| `_` | _ | _ ✅ | `_Value` |
| `yield` | yield | yield ✅ | `yieldValue` |

> 注：上表 `FALSE` 行为是统一谓词的必然推论；是否改名只取决于"转换器派生出的 Java 名是否为保留字"，而各符号种类的转换器派生可能不同（方法 decap、字段不 decap）。统一谓词是**超集**（只多改名、不漏改名），满足"下游永不产出保留字"的总目标；碰撞消歧保证超集改名不破坏编译。

### 4.2 Java 保留字标识符逃逸（JK-1~JK-4，始终必做）

**改名符号种类（声明点）**：

| 种类 | 符号/语法 | 备注 |
|---|---|---|
| 方法 | `IMethodSymbol`（普通方法、静态构造外的方法、局部函数、显式接口实现） | 显式接口实现的限定名部分（`void I.assert.M()`）随接口成员改名同步 |
| 属性 | `IPropertySymbol`（含显式接口实现、索引器除外——索引器无用户标识符） | |
| 字段 | `IFieldSymbol`（显式声明字段、枚举成员、字段式事件的后备、`fixed` 缓冲区、常量） | 隐式自动属性后备字段不改（隐式名，随属性转换链路处理） |
| 事件 | `IEventSymbol`（`event D E;` 字段式与 `add/remove` 式） | |
| 局部/变量 | `ILocalSymbol`（局部变量、模式变量、`out var`、解构变量、catch 变量、using 变量）、LINQ 范围变量（`IRangeVariableSymbol`） | 丢弃 `_` 无符号，天然跳过；名为 `_` 的真实参数/变量是符号，正常改 |
| 形参 | `IParameterSymbol`（含 lambda/局部函数/匿名方法/索引器/属性访问器形参） | |
| 类型 | `INamedTypeSymbol`（class/struct/record/interface/enum/delegate，含嵌套）、`ITypeParameterSymbol` | 构造函数、静态构造、终结符的名称标识符随类型名同步改写 |
| 命名空间段 | `INamespaceSymbol` 的源内段（块限定式 `namespace A.B.C` 与文件作用域式） | 段以符号为键独立改名；`global::` 前缀不动 |
| using 别名 | `IAliasSymbol`（`using assert = ...;`） | 别名声明点与所有引用点同步 |

**不纳入改名的种类**（静态不可声明为用户标识符或下游自管）：goto 标签（`ILabelSymbol`）、元组元素名、匿名类型成员名（含推断与显式 `@x =`）、预处理宏、XML doc 文本。

**引用点改写（一律以语义绑定为准，禁止按文本替换）**：

- JK-1 成员名 `True/False/Null/assert/…`：方法/属性/字段/事件的调用、成员访问、委托/方法组绑定（`Func<T> f = obj.True;`）、事件订阅/退订、字段地址（`&`）等所有绑定到该符号的 `IdentifierNameSyntax` / `MemberAccessExpressionSyntax.Name` / `QualifiedNameSyntax` 右段。
- JK-2 逐字标识符 `@event/@object/@true/…`：剥 `@` 后逃逸，所有引用点的 `@` 一并去除（Java 中 `@` 是注解符号；C# 中间树内新名非关键字，无需 `@`）。
- JK-3 局部变量/形参/类型（含类型形参、嵌套类型、命名空间段、别名）同理；引用点包括 `base.M()`（绑定基类符号）、`new Null()`、类型约束、`typeof(Null)`、`is Null`/`as Null`、数组元素类型 `Null[]`、可空 `Null?` 等所有类型语法位置（经语义模型反查到同一符号的 NameSyntax/IdentifierToken）。
- JK-4 同一符号所有出现使用同一新名（全编译一张 `ISymbol→newName` 表，遍历时查表，绝不逐处重新派生）。
- JK-4a **nameof（字符串值保持原样）**：`nameof(...)` 参数（含限定链 `C.True`、`Ns.assert.C`）中任一段绑定到被改名符号时，把整个 `nameof(...)` 表达式替换为**原名常量字符串字面量**（即 nameof 自身的常量值：参数最右一段剥 `@` 后的原文，如 `nameof(C.True)→"True"`、`nameof(@event)→"event"`）。字符串字面量在特性实参等常量上下文同样合法。绑定不到被改名符号的 nameof 不动。
- 重载组：组内每个命中的成员独立建表项，新名一致（`True` 组全部 → `TrueValue`），签名不变故重载关系保持。
- 外部程序集符号（`Locations` 无源位置）不改名也不重写其引用。

**JK-4b 碰撞消歧（计划期确定性完成，保证改名永不破坏编译）**：

目标名若在同一声明空间已被占用，追加十进制序号：`Value2, Value3, …`（首撞从 2 起）。占用判定基于"改名后宇宙"不动点：移除本组符号的旧名、加入其新名后，迭代至无碰撞。各声明空间的探测方式：

- 类型成员（方法/属性/字段/事件/嵌套类型）：`INamedTypeSymbol.GetMembers()` 的成员名集合（注意 C# 方法与嵌套类型允许同名，须按实际声明空间关系判定碰撞——同类成员中方法/属性/字段/事件/常量/嵌套类型共享名字冲突规则，以"改后树重编译零错误"为最终裁决）。
- 类型/命名空间：容器（类型或命名空间）的子类型/子命名空间名集合。
- 形参：同参数表（含 lambda 形参表）。
- 局部/范围变量：声明点的作用域内既有名字（`SemanticModel.LookupSymbols` + 同作用域局部表保守近似），含标签名与形参名。
- 别名：编译/源单元别名集合。

消歧顺序按符号的源位置（文件路径、span）排序，保证确定性。新名若本身再撞保留字不可能（`Value` 后缀必然脱离保留字表）。

**JK-4c 引用识别的防误改**：只改语义模型能反查到改名表中符号的节点；`GetSymbolInfo` 为多候选方法组时，候选集合与表中符号相交即改写为该表项新名；动态绑定（`SymbolInfo.CandidateReason=Dynamic`）的节点不改（动态场景见 §7/§9 已知限制）。

### 4.3 operator true/false 降级（JK-5~JK-8，保守 plan 化）

**JK-5 声明降级**：

```csharp
public static bool operator true(Foo x) => BODY;   →   public static bool IsTrue(Foo x) => BODY;
public static bool operator false(Foo x) => BODY;  →   public static bool IsFalse(Foo x) => BODY;
```

- 保持修饰符（operator 必为 `public static`）、形参表、返回类型、attributes、XML doc、trivia、体（块体/表达式体）不变。
- 方法名固定 PascalCase `IsTrue`/`IsFalse`（转换器随后自动 camelCase 为 `isTrue`/`isFalse`，与 86ed955 的目标一致）。
- 同一类型上两个 operator 属于**同一个 plan**（成对出现、成对转换）。
- 方法名碰撞（类型已有 `IsTrue`）→ JK020 跳过该 plan（不做即兴改名，保持规则简单可预期；可由 overrides.force 放开为 `IsTrue_` 消歧——默认不做）。

**JK-6 使用点改写（编译器同构，保持短路与三值语义）**：

只改写语义模型判定为**编译器实际调用 op_true/op_false** 的站点。C# 中 op_true/op_false 的绑定上下文是封闭集合：`if`/`while`/`do`/`for` 的控制表达式、`?:` 条件、`catch when` 与 `switch`/switch-expression 的 `when` 守卫、用户自定义 `&&`/`||`。判定手段：取该布尔表达式到 bool 的隐式转换，若 `Conversion.IsUserDefined && Conversion.MethodSymbol` 为该类型的 op_true（`&&`/`||` 为用户自定义条件逻辑运算，运算符方法经 `SymbolInfo`/IOperation 的 `OperatorMethod` 取得其 `&`/`|`，短路门方法为 op_false/op_true）。接收者 `Foo` 取运算符所属声明类型，按站点最小限定生成（`type.ToMinimalDisplayString(semanticModel, position)`），同类型内可省限定。

| # | 输入 | 输出（布尔位置） | T 结果位置（值仍按 T 消费，如 `T z = x && y;`、传 T 形参） |
|---|---|---|---|
| JK-6a | `if (x)` / `while(x)` / `do…while(x)` / `for(;x;)` | `if (Foo.IsTrue(x))` 等 | — |
| JK-6b | `x ? a : b` 的条件 | `Foo.IsTrue(x) ? a : b` | — |
| JK-6c | `when (x)`（catch/switch 守卫） | `when (Foo.IsTrue(x))` | — |
| JK-6d | `x && y` | `!Foo.IsFalse(x) && Foo.IsTrue(x & y)` | `Foo.IsFalse(x) ? x : x & y` |
| JK-6e | `x \|\| y` | `Foo.IsTrue(x) \|\| Foo.IsTrue(x \| y)` | `Foo.IsTrue(x) ? x : x \| y` |
| JK-6f | 嵌套/混合 `(a && b) \|\| c`、`x && BoolExpr` | 递归套用 JK-6d/e，布尔字面量/布尔变量运算元不包裹 | 同构三元展开 |
| JK-6g | `(bool)x`、`bool b = x;`、`return x;`、实参传 bool | **不动**——这些位置只能由用户自定义 bool 转换绑定，非 op_true；若编译器实际报可隐式转换则走转换运算符（转换器既有路径） | — |
| JK-6h | `!x` | **不动**——绑定的是 `operator !`（op_LogicalNot）；其结果若为 T 再进入布尔上下文，由 JK-6a~f 在外层处理 | — |

等价性依据：

- `x && y` 的 C# 降级即 `op_false(x) ? x : x & y`（结果类型 T），其布尔测试等价于 `!op_false(x) && op_true(x & y)`；op_true/op_false 不要求互为否定（SqlBoolean 三值逻辑），精确保留门控运算符与求值顺序。
- C# 语言要求二者不可同时为真（可同时为假，即三值"未知"态）：`op_false(x)` 为真时 `op_true(x)` 必为假——故门控取反分支与编译器降级结果的布尔值恒一致；`||` 对称。
- `x || y` 同构：`op_true(x) ? x : x | y`，布尔测试 `op_true(x) || op_true(x | y)`。
- 保留 `&&`/`||` 语法即保留 C# 短路；绝不退化为 `&`/`|`。`&`/`|` 调用符号从语义模型取（类型未声明则原代码本身不可编译，属基线错误）。
- 副作用次数：`x` 在 JK-6d 右列三元中求值一次（条件与结果分支共享同一求值），与编译器降级一致；三元分支中重复出现的 `x` 在 C# 源码层面看似两次出现，但条件与分支互斥且条件仅一次求值、分支直接复用 `x` 原文——`x` 作为表达式文本在三元中出现两次会导致两次求值。因此重写器对复杂（非纯标识符/纯成员访问）运算元**先提升为局部临时变量**：`var t = <expr>; IsFalse(t) ? t : t & y`（在可插入语句的语句上下文）；表达式上下文无法提升时按 JK-8 判不可行。布尔位置（JK-6d 左列）同理复用临时变量。纯标识符/无副作用简单成员访问直接复用文本。

**JK-7 范围限制**：类型上的其它 `op_Xxx`（op_Addition、op_Implicit、op_LogicalNot…）一律不处理，声明与使用点原样保留，转换器既有 `OpSymbolToJavaName` 路径继续负责。op_true/false 的降级不以"类型是否还有别的运算符"为条件。

**JK-8 不可行情形（该运算符 plan 原子跳过，JK020 诊断）**：

- 布尔使用点位于**表达式树 lambda**（赋给 `Expression<TDelegate>` 的 lambda，语义模型可判 `ArgumentKind`/lambda 的转换目标）内：改写会改变表达式树节点构成（不同方法调用），查询提供方等运行时语义静态不可证。
- 站点为 `dynamic` 绑定或静态类型不可解析，无法确定运算符所属类型（运行时经动态分派调用 op_true/op_false，属已知限制）。
- 需要提升临时变量但处于不可插入语句的表达式上下文（如字段初始化器、特性实参、查询表达式内的不可提升位置）。
- 站点分类与编译器实际绑定不一致的任何残余情形：由整树重编译兜底（FB-2 回退该 plan + JK031），不产出不可编译树。
- `IsTrue`/`IsFalse` 与类型既有成员碰撞（见 JK-5）。

诊断 JK020 携带：文件:行列、类型完全限定名、原因（上述分类）、建议（手工改写为显式 IsTrue/IsFalse 后重跑，或加入 overrides.force 评估）。

**overrides.force 的语义**：对表达式树站点这一项默认风险，force 表示用户知情接受表达式树形态变化（按 JK-6 照常包裹）；其余不可行项（dynamic、不可提升、名字碰撞）force 不解除。

## 5. 边界与兜底（FB）

| # | 情形 | 行为 |
|---|---|---|
| FB-1 | 运算符 plan 存在任一 JK-8 不可行站点（非 force） | 该 plan 原子跳过：operator 声明与全部已计划使用点改写全部不生效；JK020 诊断。同类型/同文件的关键字改名不受影响 |
| FB-2 | 整树重编译相对基线出现新增错误 | 归属唯一 plan（文件 + 最近类型/成员对 TouchedFiles/OwnsTouch 求唯一）→ 回退该 plan 全部 TouchedFiles + JK031，迭代重放收敛；无法归属单 plan → 本次运行全部回退 + JK030（仿 RR030/RR031） |
| FB-3 | 关键字改名 | 稳态不回退：碰撞在计划期确定性消解（JK-4b），重命名不改变签名/语义，C# 侧永远可编译；nameof 字面量化保证字符串契约与编译双成立。若整树验证仍把新增错误归属到改名 plan（理论上不应发生），按防御性安全网处理：JK031 回退该 plan 或 JK030 失败中止本次运行（不静默交付） |
| FB-4 | `config/JavaKeywordsOverrides.json` | `skip`：按类型完全限定名排除运算符 plan；`force`：解除表达式树项的保守跳过（见 JK-8）。JSON 解析失败 → Warning 并按空集继续（仿 record-readonly），不致命 |
| FB-5 | 单文件模式跨文件 plan | ExcludePlans + JK090（§3.4） |

不变式成立路径：(a) FB-1 在计划阶段作废运算符 plan，改动尚未落盘；(b) FB-2 以 plan 为单位回退且整树重编译通过才交付；(c) 关键字改名经 JK-4b/JK-4a 静态保证，稳态不经回退通道；(d) `FormatAndValidate` 仅预筛，交付以整树重编译为准。

## 6. 边界情况清单（完整性核对表）

| 情况 | 处理 | 规则 |
|---|---|---|
| 方法/属性/字段/事件 `True/False/Null` | 改名 + 全部调用/访问/方法组/订阅点同步 | JK-1/4 |
| 全大写 `FALSE`/`TRUE` 成员 | 不改名（Java 大小写敏感，转换器派生名非保留字） | JK-0 |
| `assert` 等全小写关键字标识符（C# 合法） | `assertValue` 等 | JK-1 |
| PascalCase `Assert()` | `AssertValue()`（转换器 decap 后撞 assert） | JK-0/1 |
| 逐字 `@event/@object/@true/@null` 等 | 剥 `@` 逃逸，引用点去 `@` | JK-2 |
| `@_` / 真名 `_` | `_Value`（丢弃模式无符号，跳过） | JK-0/3 |
| `yield` 标识符 | `yieldValue` | JK-0 |
| 局部/形参/catch/using/模式/out var/解构/LINQ 范围变量 | 改名同步 | JK-3 |
| 类型形参 `<assert>`、嵌套类型、delegate/enum/interface/record | 改名同步，ctor/终结符标识符随类型 | JK-3 |
| 命名空间段 `namespace x.assert`（块式/文件作用域） | 段改名 + using/限定名/别名同步 | JK-3 |
| using 别名 `using assert = …` | 别名与引用同步 | JK-3 |
| 显式接口实现 `void I.assert.M()` | 接口成员与限定名同步（接口在树内） | JK-1 |
| 接口成员在外部程序集 | 不改名、不改引用 | JK-4c |
| 枚举成员 `enum E { @null, Null }` | 字段符号逐个按谓词处理 | JK-1/0 |
| 字段式事件 / add-remove 事件 | 改名同步 | JK-1 |
| 重载组多名命中 | 各自表项同新名，签名不变 | JK-4 |
| 方法组/委托绑定、`nameof` 以外的特性实参 | 符号反查命中即改 | JK-4c |
| `nameof(X.True)`（任一段被改名） | 替换为原名字符串字面量 | JK-4a |
| `nameof` 不涉改名符号 | 不动 | JK-4a |
| 新名与既有成员碰撞 | `Value2/3…` 确定性消歧 | JK-4b |
| XML doc `<see cref="True"/>` | 不动（可能产生警告；错误集只比对 error） | §4.2 |
| 反射 `GetMethod("True")`/`GetField`/字符串成员名 | 不改（运行时已知限制，文档化） | §9 |
| `operator true/false` 基础降级 | 声明 → IsTrue/IsFalse | JK-5 |
| `if/while/do/for/? :/when` 中 T | IsTrue 包裹（经绑定确认） | JK-6a~c |
| `x && y` / `x \|\| y`（布尔位置与 T 结果位置） | 编译器同构三元/短路展开；复杂运算元提升临时变量 | JK-6d~f |
| `(bool)x`/`bool b=x`/`return x`/实参 | 绑定非 op_true，不动 | JK-6g |
| `!x` | 绑定 op_LogicalNot，不动；外层布尔上下文照常处理 | JK-6h |
| 类型同时有 op_true 与隐式 bool 转换 | 各站点按实际绑定分别处理；残余由整树编译兜底 | JK-6g/FB-2 |
| 同类型还有 op_Addition 等 | 不处理其它运算符，true/false 正常降级 | JK-7 |
| 接口中的 `static abstract bool operator true/false`（C#11 静态抽象成员） | 不建运算符 plan（无具体声明体、使用点经接口分派），声明与使用点原样保留；转换器既有路径负责。文档化为范围外 | §4.3 |
| `operator true/false` 与 `IsTrue/IsFalse` 既有成员碰撞 | JK020 跳过该 plan（不做即兴消歧） | JK-5 |
| 表达式树 lambda 内布尔站点 | plan 跳过 JK020（force 可解除该项） | JK-8 |
| dynamic 站点/动态分派 | plan 跳过 JK020；纯动态使用文档化 | JK-8/§9 |
| 字段初始化器/特性实参内需提升临时变量 | plan 跳过 JK020 | JK-8 |
| 运算符 plan 回退 | 声明与站点全部恢复原状 | FB-1/FB-2 |
| goto 标签/匿名类型成员/元组元素名 | 不处理 | §4.2 |
| partial 类型多部件 | 符号天然合并；plan 跨部件 | §3.2 |
| 泛型运算符类型 `struct S<T>` | IsTrue 保持签名与类型形参 | JK-5 |

## 7. 诊断与统计

**诊断码 JK0xx**（MSBuild 格式 `文件(行,列): warning/error JK0xx: [限定名] 描述`，码位只追加不改号）：

| 码 | 级别 | 含义 |
|---|---|---|
| JK001 | Info | 符号已改名（消息含种类、原名→新名） |
| JK002 | Info | 类型的 operator true/false 已降级为 IsTrue/IsFalse |
| JK010 | Info | overrides.skip 显式排除运算符 plan |
| JK020 | Warning | 运算符 plan 原子跳过（JK-8；原因与建议在消息中） |
| JK030 | Error | 整树验证失败且新增错误无法归属单 plan → 本次运行全部回退（仿 RR030） |
| JK031 | Warning | 整树新增错误归属某 plan → 该 plan 原子回退（仿 RR031） |
| JK040 | Error | `dotnet restore` 失败 |
| JK090 | Warning | 单文件模式：plan 非自包含（跨文件声明/引用/站点），跳过 |

**统计 `JavaKeywordStatistics`**：`FilesScanned`、`FilesTransformed`、`FilesSkippedClean`、`SymbolsRenamed`（及分种类计数：`MethodsRenamed/PropertiesRenamed/FieldsRenamed/EventsRenamed/LocalsRenamed/ParametersRenamed/TypesRenamed/NamespacesRenamed/AliasesRenamed`）、`NameofLiteralized`、`CollisionSuffixes`、`OperatorTypesDowngraded`、`BooleanSitesRewritten`、`PlansSkipped`、`PlansRolledBack`、`SkippedByReason: Dictionary<JavaKeywordDiagnosticCode,int>`。CLI convert-project 汇总行至少打印：改名符号总数、运算符降级类型数、布尔站点数、跳过/回退 plan 数。

## 8. 测试策略（测试先行）

1. **单元测试** `tests/CSharpToJava.ProjectTests/JavaKeywordRewriterUnitTests.cs`（仿 `RecordReadOnlyRewriterUnitTests`：内联源码 → `CSharpCompilation` → `RewriteFile` → 文本断言（忽略空白辅助方法同款）+ `AssertOutputCompiles`）：
   - 正例：`True()→TrueValue()` 且同文件调用点同步；`False()/Null()`；`True`+`False` 同类共存；`assert→assertValue` 回归；`Assert→AssertValue`；全大写 `FALSE` 不动；非关键字 `foo` 原样；`@event→eventValue`（声明+引用去 `@`）；`@true`；`_`/`yield`；属性/字段/事件/局部/形参/类型形参/嵌套类型/枚举成员/命名空间段/using 别名各一；跨文件引用（`extra` 编译单元）；显式接口实现联动；方法组绑定；碰撞 `True` 与既有 `TrueValue` → `TrueValue2`；`nameof(X.True)→"True"` 与不涉改名的 nameof 不动。
   - 运算符正例：`operator true/false→IsTrue/IsFalse`；`if(f)→if(Foo.IsTrue(f))`；while/do/for/`?:`/catch-when/switch-when 各一；三值逻辑类型（IsTrue/IsFalse 不互补，附 `operator &`/`operator |`）的 `x && y`/`x || y` 同构展开（布尔位置与 T 结果位置两种断言）；混合嵌套；复杂运算元临时变量提升；`!x` 不动；`(bool)x` 走自定义转换不动；同类型带 op_Addition 时 true/false 仍降级而 op_Addition 不动（JK-7）。
   - 反例与诊断码：表达式树内站点 → Changed=false 于运算符 plan + JK020（关键字改名仍生效）；IsTrue 名字碰撞 → JK020；单文件模式跨文件 → JK090；`skip` 配置排除；`force` 解除表达式树项。
   - `AssertOutputCompiles` 对每个正例输出断言零 error；额外断言输出不含裸保留字成员声明（如 `bool True()`）。
2. **项目级测试** `JavaKeywordProjectTests.cs` + 新 fixture `tests/SampleJavaKeywordSolution/`（`SampleJavaKeywordLib` + `SampleJavaKeywordApp` + `SampleJavaKeywordSolution.sln`，`Content Include` 进 `CSharpToJava.ProjectTests.csproj`，TFM net10.0，仿 SampleRecordSolution）：
   - Lib：含 `True/False/Null` 方法与属性、`@event` 字段、`assert` 方法、名为保留字的类型/命名空间段、operator true/false 三值逻辑类型（覆盖 if/&&/||/?:/when）、一个表达式树内使用触发 JK020 的类型（断言跳过与警告、中间树仍可编译）。
   - App：跨文件调用/成员访问/方法组。
   - 断言：`result.Success`、统计计数、中间树文件**不含**裸保留字成员名与 `operator true`、**含** `TrueValue/IsTrue/IsFalse` 与同构展开；对中间树 `dotnet build` 退出码 0（仿 `RecordReadOnlyProjectTests.RunDotNetBuildAsync`）。
   - 干净项目字节级复制用例（仿 `CleanProject_NoRecords_CopiedByteIdentical`，含 BOM 保持）。
3. **一致性测试**：`tests/CSharpToJava.Tests/ProjectPreprocessorsConsistentTests.cs` 追加类型存在性与 `IntermediateDirectoryName == ".cs2j-java-keywords-src"` 断言（仿 record-readonly/enum 段）。
4. **CLI 测试**：`JavaKeywordCLITests.cs`（仿 `RecordReadOnlyCLITests`）：`--java-keywords` 开关默认关、verb 双模式、保留目录注册、统计输出、JK090/JK020 退出码语义（`--strict`）。

## 9. 已知限制（文档化，不阻断）

- 反射/动态对改名成员的运行时一致性不保证：`GetMethod("True")`、`dynamic` 调用、XAML/序列化中按字符串匹配的成员名静态不可见；`nameof` 已按原名保留字符串值，其余字符串形态由用户负责。
- 表达式树内成员引用随改名保持同一方法（仅名字变化），但依赖特定方法名/树形态的 LINQ 提供方可能受影响；运算符降级在表达式树内默认跳过（JK-8）。
- 外部程序集的保留字命名成员不在范围内（Java 侧库不存在此类成员）。
- 不保证二进制兼容（源码级转换边界）。
- 标签、匿名类型成员名、元组元素名不处理。

## 10. 明确不做与转换器侧冗余清单

**本特性不改任何 C#→Java 转换代码**（RecordTransformer/StructTransformer/TypeGroupResolver/Pipeline/MethodTransformer/OperatorTransformer/UnaryExpressionTransformer/JavaNaming 均不动）。

预处理器落地后，转换器侧以下特例在"以 `--java-keywords` 预处理过的项目"路径上变为冗余，可在后续 PR 评估下掉（直连 ConversionPipeline 的单文件入口仍可能需要它们作为安全网，删除时需保留测试覆盖或迁移）：

1. [JavaNaming.cs](file:///d:/code/cs2j/src/CSharpToJava.Core/Context/JavaNaming.cs#L132-L165)：`IsJavaKeyword` 中 `true/false/null`（以及 `_`/`yield`）条目的实战命中由预处理器消除；`EscapeJavaKeyword` 的 `Value` 追加对预处理后输入成为恒等映射。
2. [OperatorTransformer.cs](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Member/OperatorTransformer.cs#L44-L45)：`OpSymbolToJavaName` 的 `op_True→isTrue`/`op_False→isFalse` 两行；直通表 L81-82 的 `op_True/op_False`；一元算子表 L284-285 的 `"true"/"false"` 行。
3. 提交 86ed955 所述 `MethodTransformer.ConvertOperatorName` 的 op_True/op_False 映射（当前树中该职责已位于 `OperatorTransformer`）。
4. `UnaryExpressionTransformer` 的统一查表机制本身保留（其它一元运算符仍使用），仅 true/false 特例行可随 2 一并下掉；`ExpressionTransformerHelpers.IsJavaKeyword` 对 `JavaNaming` 的委托无需改动。

其它不做项：true/false 之外的运算符（JK-7）；转换链其它预处理器的任何调整；Java 侧代码生成。

## 11. 工作量与里程碑（供 writing-plans 展开）

| 里程碑 | 内容 | 预估规模 |
|---|---|---|
| M1 | Core 骨架（Models/SyntaxFacts/Preprocessor 契约/FormatAndValidate）+ Analyzer 改名符号清单 + 改名表与碰撞消歧 + CLI 驱动全套（复制 record-readonly 改命名空间/目录名）+ Program.cs 四处接线 + verb | Core ~600 行, CLI ~450 行 |
| M2 | KeywordRenamingRewriter 全引用形态（含显式接口/别名/命名空间段/nameof 字面量化）+ 单测 | ~700 行 |
| M3 | OperatorTrueFalseRewriter（声明降级 + 布尔站点语义分类 + &&/`||` 同构展开 + 临时变量提升 + JK-8 闸门）+ 单测 | ~500 行 |
| M4 | overrides 配置、单文件模式/JK090、诊断打磨、SampleJavaKeywordSolution + 项目级/CLI/一致性测试、dotnet build 验证 | ~400 行 + fixture |
| M5 | 规格/计划文档与交付说明（§10 冗余清单）、全量测试与回归 | 文档 |
