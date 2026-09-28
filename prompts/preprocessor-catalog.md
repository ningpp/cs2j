# cs2j Preprocessor 盘点清单（C# → C# 前置转换候选）

> 盘点日期：2026-09-28
> 盘点范围：仓库 `d:\code\cs2j`（commit 工作区现状）。
> 本文档**只盘点、不设计、不实现**：所有条目均给出代码库真实证据（路径 + 行号）。
> 「preprocessor」在本文的含义：主转换（C#→Java）之前运行的、产出**语义等价且可编译 C#** 的纯源码重写通道；验证手段是中间目录 `dotnet build -c Release` / `dotnet test -c Release` 全绿。

---

## 0. 摘要

### 0.1 已接入 `convert-project` 的 preprocessor：14 个

当前在 `src/CSharpToJava.CLI/Program.cs` 的 `ConvertProject` 中按固定顺序串联（入口块行号见 §2.A）：

| # | 名称 | 中间目录 | 默认 | 状态 |
|---|------|----------|------|------|
| E01 | ConditionalDirectiveStrip（条件编译指令剥离） | `.cs2j-conditional-src` | 开 | 已实现 |
| E02 | LocalFunctionLift（局部函数提升） | `.cs2j-local-fn-src` | 开 | 已实现（允许 residual，strict 可控） |
| E03 | NullCoalesceRewriter（`??` / `??=`） | `.cs2j-null-coalesce-src` | 开 | 已实现 |
| E04 | EnumPreprocessor（enum 位运算/HasFlag） | `.cs2j-enum-src` | 开 | 部分（仅运算，**不处理 enum 声明**，见 C05） |
| E05 | DelegatePreprocessor（委托 `+/-/+=/-=`、匿名方法） | `.cs2j-delegate-src` | 开 | 已实现 |
| E06 | TupleToStruct（值元组→生成 struct） | `.cs2j-tuple-src` | 开 | 已实现 |
| E07 | ForeachDeconstruction（解构 foreach） | `.cs2j-foreach-deconstruction-src` | 开 | 已实现（仅 KeyValuePair / Deconstruct 族） |
| E08 | DefaultParameterStripper（默认参数→重载） | `.cs2j-strip-default-src` | 开 | 已实现 |
| E09 | UnsignedTypeRewriter（无符号类型类化） | `.cs2j-unsigned-src` | 开 | 已实现（配套 Cs2jUnsignedMath 支撑库） |
| E10 | GenericPreprocess（泛型 P0 三模式） | `.cs2j-generic-src` | 开 | 部分（`default(T)`/`new T()`/`new T[n]`；`typeof(T)`/`is/as T` 明确未做，见 C02） |
| E11 | RecordReadOnly（record→readonly record） | `.cs2j-record-readonly-src` | **关（opt-in）** | 已实现（plan/回滚最成熟范式） |
| E12 | ArrayMethodRewriter（数组方法/多维数组） | `.cs2j-array-src` | **关（opt-in）** | 已实现（配套 Cs2jArray 支撑库） |
| E13 | ReadOnlyStructMaker（struct 只读化） | `.cs2j-readonly-src` | 开 | 已实现 |
| E14 | GotoEliminator（goto/label→状态机） | `.cs2j-no-goto-src` | 开 | 已实现 |

### 0.2 未接入但已存在/已设计：3 个

- **E15 RefOutInPreprocessor —— 部分实现（Core + 单测完成，CLI/流水线未集成，Java 衔接未定）**。Core 9 个文件齐备（`src/CSharpToJava.Core/RefOutInPreprocessor/`），有设计与实施文档（`docs/superpowers/specs/2026-09-28-ref-out-in-preprocessor-design.md`、`docs/superpowers/plans/2026-09-28-ref-out-in-preprocessor.md`），但**没有** `ProjectRefOutInPreprocessor.cs`，`Program.cs` 无任何接线；唯一引用是 `tests/CSharpToJava.ProjectTests/RefOutInRewriterUnitTests.cs`。生产路径仍走 Java 侧 holder（见 §2.A E15）。
- **E16 LinqRewrite / LinqQueryDesugarer —— 已实现但属"嵌入式通道"**：它本身是 `CSharpSyntaxRewriter`，但运行在主转换 pass 内部（`ProjectPasses.cs:164,301,351`、`SingleFilePasses.cs:40,64,82`），没有独立 verb、没有中间目录与 build/test 验证。规模极大（`LinqRewriter.Rules.cs` 2156 行、`LinqRewriter.cs` 1624 行）。
- **E17 JavaKeywords（Java 保留字归一化）—— 设计已确认，完全未实施**：`docs/superpowers/specs/2026-09-28-java-keywords-preprocessor-design.md`（状态行 L5「设计已确认，待实施」），规划的 `src/CSharpToJava.Core/JavaKeywords/` 目录当前不存在。

### 0.3 实验性 Java2 管线下的 11 个 Lowering pass

`src/CSharpToJava.Core/Lowering/`（`LowerRefOut/LowerYield/LowerUsing/LowerProperty/LowerIndexer/LowerOperator/LowerEvent/LowerDelegate/LowerStruct/LowerPatternMatch/VariableResolution`）仅在 `NewConversionPipeline.cs:27-33` 注册；全仓搜索该管线**只被** `tests/CSharpToJava.Tests/Java2/PipelineComparisonTests.cs` 使用，不在生产路径（生产用 `ProjectConversionPipeline` + `Transformers/` + `Java/`）。它们的价值是**证明了各特性的目标降级形态**，是前置 preprocessor 的需求与形态证据，而非可复用实现。

### 0.4 建议新增候选：22 个（含对既有通道的扩展）

| 优先级 | 数量 | 编号 |
|--------|------|------|
| P0（消除现存 fail-fast / 完成半截工程） | 3 | C01 RefOutIn 接线收尾、C02 Generic 深化、C03 控制流去糖（pattern switch / catch-when 等） |
| P1（转换器最厚的复杂度块，已有明确信号） | 6 | C04 yield、C05 enum 声明归一、C06 unsafe/FFM、C07 struct 值语义补全、C08 JavaKeywords 落地、C10 JSON/Attribute 归一 |
| P2（成员/数值/并发等中等收益） | 12 | C09 ThreadStatic、C11 Property、C12 Indexer、C13 Event、C14 Operator、C15 checked/unchecked、C16 foreach 补全、C17 自增目标归一、C18 async/await、C19 扩展方法显式化、C26 LINQ 独立化、C30 closed generic metadata 前置化 |
| P3（推测性/收益小或被兼容层充分吸收） | 8 | C20 partial 合并前置、C21 匿名类型提升、C22 `Nullable<T>` 提升、C23 插值字符串展开、C24 初始化器归一、C25 dynamic、C27 record `with`/init、C28 指令/using trivia 清扫 |

---

## 1. 分类目录

### 1.1 类型系统类
E09 UnsignedTypeRewriter（已实现）、E10 GenericPreprocess（部分）、**C02 Generic 深化（typeof/is-as/静态泛型字段）**、E04/C05 enum（运算已做、声明缺口）、E06 TupleToStruct（已实现）、**C21 匿名类型提升**、**C25 dynamic**、**C22 `Nullable<T>` 提升**。

### 1.2 值语义 / 结构体类
E13 ReadOnlyStructMaker（已实现）、E11 RecordReadOnly（已实现、opt-in）、**C07 struct 值语义补全（防御性 clone）**、**C06 的 ref struct 子项**、E15/C01 ref-out-in（Core 已实现未接线）、实验管线 `LowerStruct.cs`。

### 1.3 控制流类
E14 GotoEliminator（已实现）、E02 LocalFunctionLift（已实现）、**C03 控制流去糖（pattern/非整型 switch、catch-when、catch T）**、**C04 yield 迭代器降级**、E07/C16 foreach（部分/补全）、**C18 async/await**。

### 1.4 成员 / 语法糖类
E08 默认参数剥离（已实现）、E05 委托（已实现）、**C11 Property 去糖**、**C12 Indexer 去糖**、**C13 Event 去糖**、**C14 Operator 去糖**、**C17 复杂自增目标归一**、**C19 扩展方法显式化**、**C27 record `with`/init**、**C15 checked/unchecked**、**C10 attribute 归一**。

### 1.5 异步 / 并发类
**C18 async/await**、**C09 `[ThreadStatic]`**（现由转换器内联处理）；lock/Interlocked/Volatile 当前由转换器 + compat 吸收（`Interlocked.java`、`ThreadHelper.java`、`StatementTransformer.cs:52` 的 lock 分支），暂列观察项（见 §4）。

### 1.6 反射 / 序列化 / 属性类
**C10 JSON/Attribute 归一**（`[JsonConverter]`/`[JsonPropertyName]` 等）、**C30 closed generic type metadata**（ROADMAP #10）、C02 的 `typeof(T)` 子项；compat 桥接见 `java/csharptojava-compat/.../Activator.java`、`TypeHelper`（设计文档引用）、`FieldInfo/MethodInfo/ConstructorInfo.java`、`JsonSerializer*.java`、`CompatJson*.java`、`DefaultContractResolver.java`。

### 1.7 编译指令 / 资源类
E01 ConditionalDirectiveStrip（已实现；`#if` 族 + 可选 `#region`，并剥 `#pragma/#nullable/#warning`，见 `ConditionalDirectiveStripper.cs:75-83`）、**C28 其余指令/using trivia 清扫（`#line`/`#error`/global using 别名）**、**C20 partial 类型合并前置**。
注：`.resx → .properties` 属资源管线（`Program.cs:965-982`），不是 C#→C# preprocessor，不列入候选。

### 1.8 其他
**C06 unsafe/fixed/pointer/stackalloc/Span**、E12 ArrayMethodRewriter（已实现、opt-in；多维数组/Resize/Rank）、**C23 插值字符串**、**C24 对象/集合初始化器**、nameof（转换器已处理，见 `InvocationExpressionTransformer.cs:37-88`，仅在 E17 改名时需联动字面量化）、E16/C26 LINQ、E17 Java 保留字。

---

## 2. 候选条目详表

### 2.A 现存 preprocessor 现状（家底）

#### E01 ConditionalDirectiveStrip
| 字段 | 内容 |
|------|------|
| 分类 | 1.7 编译指令 |
| 目标模式 | `#if/#elif/#else/#endif` 按符号求值后剥离；`#pragma warning/checksum`、`#nullable`、`#error`（L77-83）恒剥；`#region` 受 `RemoveRegions` 开关控制（L77）；`#warning` 保留为诊断（L38-45） |
| 证据 | Core `ConditionalDirectiveStrip/ConditionalDirectiveStripper.cs:8-56,75-107`；CLI `ProjectConditionalDirectivePreprocessor.cs:35`（`.cs2j-conditional-src`）；接线 `Program.cs:174-206`；选项 `no-strip-conditional`（`Program.cs:4744-4746`）；verb `strip-conditional`（`Program.cs:32,44`，`StripConditionalCommand.cs`） |
| 状态 | 已实现，默认开。预处理链**第一环**（必须最先跑，后续所有语义分析都基于存活分支） |
| 验证 | 项目级测试 `ConditionalDirectiveStripTests.cs`、`StripConditionalCliTests.cs`、`DebugConditionalExclusionTests.cs` |

#### E02 LocalFunctionLift
| 字段 | 内容 |
|------|------|
| 分类 | 1.3 控制流 |
| 目标模式 | 局部函数（含捕获 by-value/by-ref、静态局部函数、嵌套）提升为类级 private 方法 |
| 证据 | Core `LocalFunctionLift/`（10 文件，Analyzer/Plan/Naming/Rewriter）；统计 `LocalFunctionsLifted/LocalFunctionsResidual/CapturesByValue/CapturesByRef`（`LocalFunctionLiftStatistics.cs:8-12`）；接线 `Program.cs:208-240`；CLI `ProjectLocalFunctionLiftPreprocessor.cs:36`；设计 `docs/superpowers/specs/2026-09-06-local-function-lift-design.md` |
| 与现有通道关系 | 转换器内仍保留残余路径（`StatementTransformer.LocalFunctions.cs` 全文 321 行、`MethodTransformer.cs:37-46`），preprocessor 未覆盖时兜底；preprocessor 失败可由 `--no-local-function-strict` 降级为 warning（`Program.cs:4760-4762`） |
| 状态 | 已实现，默认开；存在 residual 出口（严格模式闸门） |
| 验证 | `LocalFunctionLiftTests.cs`、`LocalFunctionHoistingTests.cs`、`HoistedLocalFunctionCaptureScopeTests.cs`、`GenericLocalFunctionRenamedCallSiteTests.cs` 等十余项 |

#### E03 NullCoalesceRewriter
| 字段 | 内容 |
|------|------|
| 分类 | 1.4 语法糖（空合并） |
| 目标模式 | `??` → 显式三元；`??=` → if-赋值；复杂被提升表达式提临时变量；两遍（改写 + 临时变量提升） |
| 证据 | Core `NullCoalesceRewriter/`（`NullCoalesceRewriter.cs:35-45`，统计 L8-13）；接线 `Program.cs:242-278`；设计 `docs/superpowers/specs/2026-09-26-null-coalesce-preprocessor-design.md` |
| 状态 | 已实现，默认开 |
| 验证 | `NullCoalesceRewriterUnitTests.cs`、`NullCoalesceAssignmentParensTests.cs`、`NullCoalesceWithAsArrayTests.cs`、`CoalesceArgumentTests.cs` |

#### E04 EnumPreprocessor（注意：仅"运算"层）
| 字段 | 内容 |
|------|------|
| 分类 | 1.1 类型系统（enum） |
| 目标模式 | enum 上的 `\|/&/^/~`、复合赋值、`HasFlag`、可空 enum 提升 → 底层整数显式运算 + `(E)` 强转（窄底层类型包 `unchecked`） |
| 证据 | `EnumPreprocessor/EnumPreprocessor.cs:8-13`；`EnumSyntaxRewriter.cs:81-111,208,279-371`（全部围绕运算，**无 EnumDeclaration 改写**）；统计 `EnumPreprocessorStatistics.cs:9-17`；接线 `Program.cs:280-319` |
| 缺口 | `[Flags]` enum、显式底层类型、显式成员值三类**声明**仍被转换器直接拒绝（`Transformers/Type/EnumTransformer.cs:29-45`），见 **C05**；`++/--` enum 目标仍 fail-fast（`UnaryExpressionTransformer.cs:181-182`），见 C17 |
| 状态 | 已实现（窄），默认开 |
| 验证 | `EnumPreprocessorUnitTests.cs`、`ProjectEnumPreprocessorTests.cs`、`ByteEnumArithmeticWrapTests.cs` |

#### E05 DelegatePreprocessor
| 字段 | 内容 |
|------|------|
| 分类 | 1.4 成员/语法糖 |
| 目标模式 | 委托 `d1 + d2`/`d1 - d2`/`+=`/`-=` → `Cs2jPrelude.CsPrelude.Combine/Remove`（`DelegateSyntaxRewriter.cs:122,132`，支撑库 `src/Cs2jPrelude/CsPrelude.cs`，经 csproj 嵌入 `CSharpToJava.CLI.csproj:37-38`）；匿名方法 `delegate(...) {}` → lambda |
| 证据 | `DelegatePreprocessor/DelegatePreprocessor.cs:7-11`；统计 `DelegatePreprocessorStatistics.cs:8-13`；接线 `Program.cs:321-359`；设计 `docs/superpowers/specs/2026-09-26-delegate-preprocessor-design.md` |
| 与现有通道关系 | 只做委托组合/匿名方法；**event** 的 `+=/-=` 与字段式事件仍由 `EventFieldTransformer.cs`（447 行）处理 → C13；delegate 类型→函数式接口仍由 `Type/DelegateTransformer.cs` + 实验 `LowerDelegate.cs` 负责 |
| 状态 | 已实现，默认开；有 `SkippedUnsupported/ResidualCount` 残余统计 |
| 验证 | `DelegatePreprocessorUnitTests.cs`、`ProjectDelegatePreprocessorTests.cs`、`AnonymousMethodParameterTests.cs` |

#### E06 TupleToStruct
| 字段 | 内容 |
|------|------|
| 分类 | 1.1 类型系统（tuple） |
| 目标模式 | 元组类型/字面量/命名元素/`ValueTuple` 工厂 → 生成的 `Cs2jTuples.*` readonly struct；解构声明/赋值/`out var`/foreach 解构展开；跨文件形状去重生成单一 struct 文件 |
| 证据 | `TupleToStruct/TupleToStructPreprocessor.cs:9-19`；`TupleShapeRegistry.cs`、`TupleStructFileContent.cs`；接线 `Program.cs:361-404`（注释 L363-364 明确「放在 default 参数剥离之前」）；默认命名空间 `Cs2jTuples`（`Program.cs:4752-4754`） |
| 状态 | 已实现，默认开 |
| 验证 | `TupleToStructProjectTests.cs`、`TupleToStructRewriterUnitTests.cs`、`MutableTupleAssignmentTests.cs`、`TupleDeconstructionDiscardTests.cs` |

#### E07 ForeachDeconstruction
| 字段 | 内容 |
|------|------|
| 分类 | 1.3 控制流 |
| 目标模式 | 解构 foreach：KeyValuePair 族（含实现 `IEnumerable<KeyValuePair<,>>`）与自定义 `Deconstruct` 族 → 普通 foreach + 显式局部声明 |
| 证据 | `ForeachDeconstruction/ForeachDeconstructionPreprocessor.cs:8-13,31-41`；族判定 `ForeachDeconstructionRewriter.cs:61-68,112-122`（Tuple 族显式 skip 并提示「run TupleToStruct first」——顺序依赖证据）；接线 `Program.cs:406-441` |
| 缺口 | 字符串 foreach 仍 fail-fast（`StatementTransformer.Loops.cs:121`）；非 KV/Deconstruct 的解构 foreach 残余 → **C16** |
| 状态 | 已实现，默认开；必须排在 E06 之后 |
| 验证 | `ForeachDeconstructionTests.cs`、`ProjectForeachDeconstructionPreprocessorTests.cs` |

#### E08 DefaultParameterStripper
| 字段 | 内容 |
|------|------|
| 分类 | 1.4 成员/语法糖 |
| 目标模式 | 剥离方法默认参数并生成重载（两遍：Collect 建签名目录 → Strip + 改调用点具名实参）；跳过无 body/冲突/隐藏 abstract 等 |
| 证据 | `DefaultParameterStripper/DefaultParameterStripper.cs:12-35`；统计 `DefaultParameterStripperStatistics.cs:8-15`；接线 `Program.cs:443-477`；设计 `docs/superpowers/specs/2026-09-06-strip-default-parameter-design.md` |
| 状态 | 已实现，默认开；转换器侧另有 `Transformers/Utilities/DefaultParameterHelper.cs` 兜底 |
| 验证 | `DefaultParameterStripperTests.cs`、`DefaultParameterCatalogTests.cs`、`DefaultParameterOverloadBuilderTests.cs`、`StripDefaultProjectTests.cs` |

#### E09 UnsignedTypeRewriter
| 字段 | 内容 |
|------|------|
| 分类 | 1.1 类型系统（无符号） |
| 目标模式 | `byte/ushort/uint/ulong` 关键字 → `System.Byte/UInt16/UInt32/UInt64`（映射 compat `CSharpByte/CSharpUInt16/...`）；运算符→扩展方法（注入 Cs2jUnsignedMath 支撑库工程）；cast/数组/泛型实参/字面量/const/enum/后缀自增全覆盖 |
| 证据 | Core `UnsignedTypeRewriter/`（11 文件，含 `UnsignedPostfixRewriter.cs`、`UnsignedMathSyntax.cs`、`UnsignedLiteralSyntax.cs`）；统计 `UnsignedTypeRewriterStatistics.cs:8-13`；接线 `Program.cs:479-512`；支撑库过滤 `Program.cs:750-756`；背景 `prompts/unsigned-type-rewriter-prompt.md:5`（双重映射冲突）、`config/TypeMappings.json:24-46`（有损映射）；设计 `docs/superpowers/specs/2026-08-15-unsigned-math-helper-design.md`、`2026-09-24-unsigned-math-lib-project-design.md` |
| 状态 | 已实现，默认开。是「注入支撑库工程 + 过滤不转换」范式的样板（与 E12 同） |
| 验证 | `UnsignedTypeRewriterUnitTests.cs`、`UnsignedTypeProjectTests.cs`、`UnsignedConversionEndToEndTests.cs`、`tests/CSharpToJava.Tests/Cs2jUnsigned/*` |

#### E10 GenericPreprocess（部分实现）
| 字段 | 内容 |
|------|------|
| 分类 | 1.1 类型系统（泛型） |
| 目标模式（已做） | `default(T)` → `default` 字面量（歧义处包 `(object)`）；`new T()` → `Activator.CreateInstance<T>()`；`new T[n]` → `(T[])Array.CreateInstance(typeof(T), n)`；并为泛型 override 兄弟方法注入 `__cs2j_typeof_T` 哨兵以统一 Class token 注入 |
| 证据 | 类注释即覆盖清单：`GenericPreprocess/GenericPreprocessor.cs:8-24`（L18 明写「typeof(T) / is T / as T 暂不降级（仅统计）」）；override 同步 `GenericPreprocessor.cs:69-100`；接线 `Program.cs:514-551`；prompt `prompts/generic-preprocess-prompt.md:5-19` |
| 缺口 | 见 **C02**（`typeof(T)/is/as T`、泛型静态字段、closed metadata） |
| 状态 | 部分实现，默认开 |
| 验证 | `GenericPreprocessTests.cs`、`ProjectGenericPreprocessorTests.cs`、`GenericNewConstraintInliningTests.cs`、`DefaultValueProjectTests.cs` |

#### E11 RecordReadOnly（opt-in）
| 字段 | 内容 |
|------|------|
| 分类 | 1.2 值语义（record） |
| 目标模式 | 可变 record class/struct → readonly record；三阶段（分析→计划→逐文件重写）+ plan 级原子回退 + 整树重编译基线比对；含接口 ripple、变异点改写、方法迁移 |
| 证据 | Core `RecordReadOnly/`（11 文件；`RecordReadOnlyPreprocessor.cs:9-26`、`RecordReadOnlyAnalyzer.cs` 887 行、`MutationSiteRewriter.cs` 985 行、`InterfaceRippler.cs` 530 行）；接线 `Program.cs:553-593`；开关默认 false（`Program.cs:4783-4785`）；overrides `config/RecordReadOnlyOverrides.json`；设计 `docs/superpowers/specs/2026-09-24-record-readonly-preprocessor-design.md` |
| 与现有通道关系 | 与 E13 同属值语义链；转换器 `Type/RecordTransformer.cs`（513 行，决策分支 L25-59）仍承担 record struct/abstract record/额外成员等 fallback |
| 状态 | 已实现，**默认关**；是 E17 JavaKeywords 明确要复刻的架构样板（JavaKeywords spec §1） |
| 验证 | `RecordReadOnlyProjectTests.cs`、`RecordReadOnlyCLITests.cs`、`RecordReadOnlyRewriterUnitTests.cs` |

#### E12 ArrayMethodRewriter（opt-in）
| 字段 | 内容 |
|------|------|
| 分类 | 1.8 其他（数组） |
| 目标模式 | 数组实例方法/静态调用、`Array.Resize`、多维数组 Rank/length、数组 foreach 展平等 → `Cs2jArray.ArrayHelper.XXX(...)`（注入 Cs2jArray 支撑工程） |
| 证据 | Core `ArrayMethodRewriter/`（7 文件；入口注释 `ArrayMethodRewriter.cs:8-13`；统计 `ArrayMethodRewriterStatistics.cs:8-13`）；接线 `Program.cs:595-634`；开关默认 false（`Program.cs:4791-4793`）；支撑库过滤 `Program.cs:755`；设计 `docs/superpowers/specs/2026-09-25-array-helper-preprocessor-design.md` |
| 状态 | 已实现，**默认关** |
| 验证 | `ArrayMethodRewriterUnitTests.cs`、`ArrayMethodProjectTests.cs`、`ArrayMethodConversionEndToEndTests.cs`、`ArrayHelperSupportFilesTests.cs` |

#### E13 ReadOnlyStructMaker
| 字段 | 内容 |
|------|------|
| 分类 | 1.2 值语义（struct） |
| 目标模式 | 可变 struct → readonly struct（装箱可达性分析、构造器生成/归一化、变异点改写、调用点更新、方法迁移），为 Java Value Class（JEP 401）映射铺路 |
| 证据 | Core `ReadOnlyStructMaker/`（13 文件；`ReadOnlyStructRewriter.cs` 1688 行、`StructAnalyzer.cs` 977 行、`CallSiteUpdater.cs` 691 行、`BoxingReachabilityAnalyzer.cs`）；CLI `ProjectReadOnlyStructPreprocessor.cs:49`（`.cs2j-readonly-src`）并写 `.cs2j-iface-members.json` 元数据（L1702-1707）；接线 `Program.cs:636-677`（注释 L638「runs BEFORE goto elimination」）；overrides `config/ReadOnlyStructOverrides.json`；VerifyBuild/VerifyTests 范式出处 |
| 与现有通道关系 | 与 C07（可变 struct clone）互补；转换器 `Type/StructTransformer.cs:14-19,60-67` 明确「值语义差异由 C#→C# 预处理器消除，只做一一映射」，ref struct 直接拒绝（L60-64） |
| 状态 | 已实现，默认开 |
| 验证 | `ReadOnlyStructMakerTests.cs`、`ReadOnlyStructMakerL4/L5Tests.cs`、`ReadOnlyStructPreprocessorVerifyTests.cs`、`ReadOnlyStructPreprocessorCallSiteTests.cs` |

#### E14 GotoEliminator
| 字段 | 内容 |
|------|------|
| 分类 | 1.3 控制流 |
| 目标模式 | `goto/label`、`goto case/default`（Pass1 去糖 `GotoCaseDesugarer` → Pass2 `StateMachineBuilder` 2076 行状态机）→ 无 goto C# |
| 证据 | `GotoEliminator/GotoEliminator.cs:8-39`；CLI `ProjectGotoPreprocessor.cs:44`；接线 `Program.cs:679-716`（预处理链最后一环，`UsingGotoPreprocessedSource` 标记 L715）；设计 `docs/superpowers/specs/2026-06-27-goto-eliminator-design.md`、`2026-06-03-goto-conversion-redesign.md` |
| 状态 | 已实现，默认开 |
| 验证 | `GotoEliminatorTests.cs`、`ProjectGotoPreprocessorTests.cs`、`ProjectGotoPreprocessorRestoreTests.cs`、`ConvertProjectGotoIntegrationTests.cs`、`ContinueInSwitchGotoCaseTests.cs` |

#### E15 RefOutInPreprocessor（未接线的半截工程）
| 字段 | 内容 |
|------|------|
| 分类 | 1.2 值语义（ref/out/in） |
| 目标模式 | 统一 ByRef 位置模型：内部 ref/out/in 参数、ref 局部/ref 返回/ref 字段/委托 ref 参数/ref struct holder/数组元素代理；外部元数据符号与 extern/接口虚实现**锁定不改**（设计 D1/D6） |
| 证据 | Core `RefOutInPreprocessor/` 9 文件；统计 19 项（`RefOutInRewriterStatistics.cs:5-23`：RefParams/OutParams/RefReturns/RefLocals/RefFields/ArrayElementProxies/RefStructParams/CallSitesLifted/HoldersInjected/AccessorProxies/LockedContractMembers…）；入口 `RefOutInRewriter.cs:8-74`；设计 `docs/superpowers/specs/2026-09-28-ref-out-in-preprocessor-design.md:1-49`（状态「已评审」；D3「convert-project 默认开启；Java 产物暂不可编译为已知中间状态，Java 映射另开后续 spec」；L15 明说不依赖实验管线 `LowerRefOut.cs`） |
| 现状反差 | **无 CLI 包装、Program.cs 无接线**；全仓仅 `RefOutInRewriterUnitTests.cs` 引用。生产仍走 Java 侧 holder：`Transformers/Expression/Transformers/ArgumentTransformer.Ref.cs` + `HolderTypeResolver.cs`，项目级断言仍是 IntHolder/ObjectHolder（`tests/CSharpToJava.ProjectTests/RefOutInProjectTests.cs:24-40`）。转换器仍对 ref local（`StatementTransformer.Declarations.cs:30`）、lambda ref 参数（`LambdaTransformer.cs:99`）、using/foreach/循环条件中的 ref read-back（`SwitchAndResource.cs:236`、`Loops.cs:139,208`）fail-fast |
| 状态 | **部分实现**（Core + 单测；CLI 集成与 Java 映射缺口） |
| 验证 | 已具单测；接线后按中间目录 `dotnet build/test -c Release` + 设计 D5 的 csharp-sdk 32 项目基线比对 |

#### E16 LinqRewrite（嵌入式 C# 重写通道，非独立 preprocessor）
| 字段 | 内容 |
|------|------|
| 分类 | 1.8 LINQ |
| 目标模式 | 查询表达式去糖（`LinqQueryDesugarer`）+ 方法链 LINQ → 过程式 foreach（`LinqRewriter`，源自 roslyn-linq-rewrite，见文件头版权 `LinqRewriter.cs:1-21`）；Stream 偏好下旁路 |
| 证据 | `LinqRewrite/LinqRewriter.Rules.cs`（2156 行）、`LinqRewriter.cs`（1624 行）；调用点 `Pipeline/Passes/ProjectPasses.cs:164,301,351` 与 `SingleFilePasses.cs:40,64,82`；门控 `ConversionOptions.EnableLinqRewrite`（默认 true，`Context/ConversionOptions.cs:54`）+ `--no-linq-rewrite`（`Program.cs:4690-4691`） |
| 与 preprocessor 范式的差距 | 无独立 verb、无 `.cs2j-*-src` 中间树、不做 `dotnet build/test` 等价性验证；产物直接喂给主转换器。提升为独立通道的候选见 **C26** |
| 状态 | 已实现（嵌入式） |
| 验证 | `LinqProceduralPhase1..10Tests.cs`、`LinqChainRefactoringTests.cs`、`LinqQueryDesugarTests.cs`、`LinqObservabilityPhase10Tests.cs` 等数十项 |

#### E17 JavaKeywords（已设计未实施）
| 字段 | 内容 |
|------|------|
| 分类 | 1.8 其他（命名安全） |
| 目标模式 | ① 全部符号种类中派生为 Java 保留字的标识符（含 `true/false/null/_/yield`、`@event`、PascalCase `Assert` 等）统一改名 `XxxValue` + 碰撞消歧 + `nameof` 字面量化（规则 JK-0~JK-4）；② `operator true/false` 声明与全部布尔使用点（`if/?:/when/&&/||`）编译器同构降级为 `IsTrue/IsFalse`（JK-5~JK-8） |
| 证据 | 完整设计 `docs/superpowers/specs/2026-09-28-java-keywords-preprocessor-design.md`（状态 L5；管线位置 L92-97「EliminateGoto 之后、转换之前，默认关，独立 verb `java-keywords`」；规划目录 L50-65）；转换器侧现存兜底：`Context/JavaNaming.cs:132-165`（`IsJavaKeyword`）、`Transformers/Member/OperatorTransformer.cs:44-45,81-82,284-285`；相关测试 `JavaReservedWordTests.cs`、`KeywordBackingFieldAssignmentTests.cs`、`OperatorMethodNameCollisionTests.cs` |
| 状态 | **缺失（设计完成待实施）**；按设计即 **C08** |
| 验证 | 设计 §8 已规划：`JavaKeywordRewriterUnitTests.cs` + `SampleJavaKeywordSolution` 项目级 fixture + CLI/一致性测试 |

---

### 2.B 建议新增候选

#### C01 RefOutIn 接线收尾（P0）
| 字段 | 内容 |
|------|------|
| 名称 | `ProjectRefOutInPreprocessor`（复用 Core `RefOutInPreprocessor/`） |
| 分类 | 1.2 值语义 |
| 目标 C# 模式 | 内部 ref/out/in 全套（参数/局部/返回/字段/数组元素代理/ref struct holder）；锁定外部符号与 extern/接口虚契约 |
| 解决的转换难点 | 消除 `ArgumentTransformer.Ref.cs` + `HolderTypeResolver.cs` 的字符串拼接式 holder 降低，以及 5 处 fail-fast：`StatementTransformer.Declarations.cs:30`、`LambdaTransformer.cs:99,200`、`SwitchAndResource.cs:236`、`Loops.cs:139,208`；实验 `Lowering/LowerRefOut.cs` 随之彻底无意义 |
| 证据 | Core 9 文件 + 设计文档已评审（见 E15）；ROADMAP #3 |
| 与现有通道关系 | 依赖：应在 E06 TupleToStruct、E04 Enum、E05 Delegate、E09 Unsigned 之后（这些会引入新调用形态）；建议先于 E13 ReadOnlyStruct（holder struct 与只读化有交互，需原型确认）；**必须完成设计 D3 遗留的 Java 映射 spec**（ByRef 在 Java 侧如何落地）才能默认开启 |
| 优先级 | **P0**（半截工程，完成边际成本最低、fail-fast 面最广） |
| 状态 | 部分（Core 完成，CLI/Java 缺） |
| 验证 | 中间树 build/test 全绿；`RefOutInRewriterUnitTests.cs` 已在；新增项目级 fixture 与 csharp-sdk 32 项目「新增失败为零」比对（设计 D5） |

#### C02 Generic 深化：typeof(T)/is-as T/静态泛型字段（P0）
| 字段 | 内容 |
|------|------|
| 名称 | `GenericPreprocess` 扩展（不新建通道） |
| 分类 | 1.1 类型系统 |
| 目标 C# 模式 | `typeof(T)` 显式 Class token 化、`is T`/`as T`/类型参数 catch、引用封闭类型参数的**静态字段**（Java 静态成员不得引用类类型参数）、嵌套/递归泛型中的类型实参传播 |
| 解决的转换难点 | 转换器最厚的特判簇：Class token 注入/传播（`StaticFieldRuntimeClassTokenTests.cs`、`TrailingClassTokenCallSiteTests.cs`、`GenericMethodInferredTypeArgClassTokenTests.cs` 等 20+ 测试）、静态泛型字段硬拒绝（`FieldTransformer.cs:112-119`）、擦除冲突（`SameTypeErasureConflictTests.cs`、`CrossInheritanceErasureTests.cs`）；ROADMAP #5/#10 |
| 证据 | `GenericPreprocessor.cs:18`（明确未做）；prompt `prompts/generic-preprocess-prompt.md:7-15,49`；静态字段 fail-fast `FieldTransformer.cs:115-118`；closed metadata 设计 `docs/superpowers/specs/2026-09-22-closed-type-metadata-chain-design.md` |
| 与现有通道关系 | 是 E10 的直接续作；与 C30（closed metadata）互为表里——C02 在 C# 侧显式化 token，C30 在 Java 反射侧消费闭型；必须在 E09 Unsigned 之后（无符号类化改变泛型实参种类） |
| 优先级 | **P0** |
| 状态 | 部分（3 个 P0 模式已做，本候选为缺口） |
| 验证 | 中间树 build/test；fixture 复用 `TypeOfGenericTests`、`StaticGenericFieldTests`、`NestedGenericMappingTests`、`CrossProjectGenericClassTokenTests` 的 C# 形态 |

#### C03 控制流去糖：pattern switch / 非整型 switch / catch-when（P0）
| 字段 | 内容 |
|------|------|
| 名称 | `ControlFlowDesugar`（建议单通道多规则，参考 EnumPreprocessor 组织） |
| 分类 | 1.3 控制流 |
| 目标 C# 模式 | ① 带模式标签/`when` 的 switch 语句 → if-else 链；② 非整型（bool/long/float/double/decimal/string/枚举）governing 的 switch → if-链；③ `catch (T) when (…)` → catch + if/rethrow；④ 对类型参数的 catch；⑤ 字符串 foreach（可并入本通道或 C16） |
| 解决的转换难点 | 全部 5 类今天都是**转换即抛异常**：`StatementTransformer.SwitchAndResource.cs:25,40,60,144,155`；`Loops.cs:121`。switch *表达式*侧已自行降三元链（`ControlFlowTransformer.cs:449-507`），但 L505 对未知模式留 `/* TODO ... */ true`，statement 侧无对应能力 |
| 证据 | 同上行号；模式测试需求 `ConversionTests/Categories/PatternMatchingTests.cs`、`PatternBulkTests.cs`、`SwitchCaseWhenClausePrecedenceTests.cs`、`CatchFilterRethrowTests.cs` |
| 与现有通道关系 | 独立但建议排在 E14 Goto 之前（goto case 去糖与 switch 去糖会争抢同一语法面——实际应让 C03 先把 pattern switch 归一成普通 switch/if，再交 E14 处理残余 goto case）；无其他强依赖 |
| 优先级 | **P0**（每命中一处就是一次转换失败，且降级形态机械、可编译性易保持） |
| 状态 | 缺失 |
| 验证 | 中间树 build/test；fixture 覆盖关系/属性/元组/逻辑/关系模式、when 守卫、类型参数 catch、string foreach |

#### C04 Yield 迭代器降级（P1）
| 字段 | 内容 |
|------|------|
| 名称 | `IteratorLowering`（yield state machine） |
| 分类 | 1.3 控制流 |
| 目标 C# 模式 | `yield return` / `yield break` 的迭代器方法/属性 → 显式 `IEnumerator<T>`/`IEnumerable<T>` 实现（`_state/_current`、`MoveNext/Current/Dispose`）的可编译 C#（即 C# 编译器自身的 lowering 形态） |
| 解决的转换难点 | 当前硬失败：`StatementTransformer.Declarations.cs:167,171`；实验管线已有同形态证明 `Lowering/LowerYield.cs`（218 行，IR 级，仅测试可达）；Java 侧兜底桥 `IteratorBridgeTests.cs`、`YieldReturnRefArgTests.cs` 说明存量痛点 |
| 证据 | 见上；ROADMAP 未列但转换器明确「must be lowered by a preprocessor (iterator lowering)」 |
| 与现有通道关系 | 必须在 E02 LocalFunctionLift、C01 RefOutIn 之后（迭代器捕获与 ref 参数会进状态机字段）；try/yield 交互、`using` 内 yield 需与 IDisposable 形态联动（`LowerUsing.cs` 同构问题） |
| 优先级 | **P1**（收益大但状态机综合复杂，需先做原型验证 try-catch/finally 语义） |
| 状态 | 缺失 |
| 验证 | 中间树 build/test；fixture：嵌套循环 yield、try/finally 中 yield、带参迭代器、`yield break` 早退 |

#### C05 Enum 声明归一（[Flags]/底层类型/显式值）（P1）
| 字段 | 内容 |
|------|------|
| 名称 | 扩展 `EnumPreprocessor`（不新建通道） |
| 分类 | 1.1 类型系统（enum） |
| 目标 C# 模式 | `[Flags]` enum、非 int 底层类型 enum、带显式值/重复值的 enum → 「静态字段 + 整数值 + 显式成员集合」的 enum-like class（或在 C# 侧改写为 struct/常量类），同时改写字典/switch/`ToString/Parse/HasFlag` 使用点 |
| 解决的转换难点 | 三类声明 100% 转换失败：`EnumTransformer.cs:29-45`；Java 侧桥已很厚（`EnumHelper.java`、`EnumHelperTryParseTests.cs`、`FlagsEnumToStringTests.cs`、`ReflectionFlagEnumMappingTests.cs`、`EnumValueMemberCollisionTests.cs`） |
| 证据 | 见上；E04 已把位运算使用点归一（`EnumSyntaxRewriter.cs`），声明侧是同一特性的另一半 |
| 与现有通道关系 | 并入 E04 同一 preprocessor；须在 E10 Generic 之前（enum 作泛型实参/数组元素）；与 C03 switch 去糖有交集（enum switch 不再被 C03 判为非法后仍需成员值比较） |
| 优先级 | **P1** |
| 状态 | 缺失（E04 只做运算） |
| 验证 | 中间树 build/test；fixture：`[Flags]` 位组合、byte 底层、显式跳跃值、enum 嵌套泛型集合 |

#### C06 Unsafe/FFM 归一（pointer/fixed/unsafe/stackalloc/extern/ref struct）（P1）
| 字段 | 内容 |
|------|------|
| 名称 | `UnsafeFfiNormalize`（单通道多规则） |
| 分类 | 1.8 / 1.2（ref struct 子项） |
| 目标 C# 模式 | 指针局部/参数/返回、`&` 取址/`->`/解引用/指针算术、`fixed` 块、`unsafe` 块、`stackalloc`、`[DllImport]/[LibraryImport] extern`、`ref struct`/`Span<T>` 边界 → 数组/Span/索引/MemorySegment（FFM）形态的可编译 C# |
| 解决的转换难点 | 今天的 fail-fast 群：`StatementTransformer.SwitchAndResource.cs:297,301`（fixed/unsafe）、`StatementTransformer.Declarations.cs:54`（指针局部）、`UnaryExpressionTransformer.cs:196`（取址/指针一元）、`ObjectCreationTransformer.cs:148`（stackalloc）、`ClassTransformer.cs:350`（extern/DllImport）、`StructTransformer.cs:60-64`（ref struct）；现有 FFM 兜底只覆盖 fixed *字段缓冲区*（`FieldTransformer.cs:70-101` + `Expression/Utilities/FfmHelper.cs` 346 行） |
| 证据 | 设计文档已有 4 份：`docs/superpowers/specs/2026-05-31-fixed-statement-ffm-design.md`、`2026-05-31-unsafe-method-pointer-params-design.md`、`2026-06-02-address-of-local-pointer-design.md`、`2026-05-30-csharp-stack-design.md`；测试簇 `FixedStatementFfmTests.cs`、`FixedPointerSpanRoundTripTests.cs`、`StackAllocArrayCreationTests.cs`、`PointerCastToIntegralTests.cs`、`UnsafeMethodPointerTests.cs`、`UnsafeReadUnalignedVectorTests.cs`；compat `Span.java`、`ReadOnlySpan.java`、`Unsafe.java`、`MemoryMarshal.java`、`GCHandle.java` |
| 与现有通道关系 | 依赖 C01（ref/out 位置模型是指针取址代理的基础）；与 E12 ArrayMethod、E09 Unsigned 同属「注入支撑形态」族；语义等价风险最高的一族（必须 extern/ABI 锁定，设计 D6 已有原则） |
| 优先级 | **P1**（高频基础设施代码命中，但等价性需逐项验证，建议按 fixed→stackalloc→指针→extern 分规则迭代） |
| 状态 | 缺失（仅有分散的转换器内 FFM 兜底） |
| 验证 | 中间树 build/test；fixture 即上述 4 份 spec 与现有 unsafe 测试簇 |

#### C07 Struct 值语义补全（可变 struct 防御性 clone）（P1）
| 字段 | 内容 |
|------|------|
| 名称 | 扩展 `ReadOnlyStructMaker` 或新建 `StructValueSemantics` |
| 分类 | 1.2 值语义 |
| 目标 C# 模式 | 未被只读化的可变 struct 在变量初始化、赋值、属性/索引器取值、链式赋值、返回值等处插入显式 `Clone()`（C# 侧可定义 copy ctor/`Clone` 并保证编译通过） |
| 解决的转换难点 | 生产转换器今天靠 `Transformers/StructCloneHelper.cs`（399 行）在 Java 侧补 clone（`StructCloneReturnThisTests.cs`、`JaggedArraySelectCloneDebugTests.cs` 等大量回归）；实验管线同形 pass `Lowering/LowerStruct.cs:39-255`（含链式赋值展开 L139-166） |
| 证据 | `StructTransformer.cs:14-19`（值语义差异被明确划给预处理器）；`StructCloneHelper.cs` 全文；E13 的 `MethodMigrator.cs`/`AssignmentRewriter.cs` 已积累变异分析基础设施 |
| 与现有通道关系 | E13 的天然续作（只读化成功的类型不需要 clone；失败/跳过类型进入本通道）；应在 E06/E12 之后（tuple struct、数组元素是 clone 高频点） |
| 优先级 | **P1** |
| 状态 | 缺失（Java 侧代偿中） |
| 验证 | 中间树 build/test；fixture：属性返回 struct、结构体数组元素改值、链式赋值、readonly 字段内变异 |

#### C08 JavaKeywords 落地（P1）
| 字段 | 内容 |
|------|------|
| 名称 | `JavaKeywords`（即 E17 的实施项） |
| 分类 | 1.8 命名安全 + 1.4（op_true/false） |
| 目标/证据/验证 | 全部见 E17 与设计文档 JK-0~JK-8、§8 测试规划 |
| 与现有通道关系 | 设计定死：**预处理链绝对最后一环**（E14 之后），分析对象是其它所有通道产出的最终中间树；默认关 + 独立 verb；落地后转换器 4 处兜底可评估退役（spec §10） |
| 优先级 | **P1**（设计/验收/里程碑已完备，实施风险低；直接消除 `true/false/null` 命名类 bug） |
| 状态 | 缺失（设计待实施） |

#### C09 `[ThreadStatic]` 前置化（P2）
| 字段 | 内容 |
|------|------|
| 名称 | `ThreadStaticRewriter` |
| 分类 | 1.5 并发 |
| 目标 C# 模式 | `[ThreadStatic] static T x;` + 全部读写点 → `static readonly ThreadLocal<T> x = new ThreadLocal<T>();` + `.Value` 读写（语义等价的可编译 C#） |
| 解决的转换难点 | 现状在转换器内跨组件特判：字段声明改写（`FieldTransformer.cs:51-57,123-129`）+ 上下文注册表（`ConversionContext.cs:397-422`）+ 读写点在 Identifier/Assignment transformer 分散改 `.get()/.set()`；ROADMAP #6 明确要求前置化 |
| 证据 | 同上 |
| 与现有通道关系 | 须在 E13/C07 struct 通道之前或之后需原型确认（ThreadLocal<结构体> 的变异语义）；与 C02 静态字段归一有近邻关系（都是静态字段重写），可考虑合并为「静态字段归一」单通道 |
| 优先级 | **P2**（现路径可用，前置化主要是简化转换器、消除跨组件状态） |
| 状态 | 缺失（转换器内代偿） |
| 验证 | 中间树 build/test；fixture：线程内独立副本、静态构造初始化、跨类读写点 |

#### C10 JSON / Attribute 归一（P2）
| 字段 | 内容 |
|------|------|
| 名称 | `AttributeNormalize`（先聚焦序列化 attribute 族） |
| 分类 | 1.6 反射/序列化 |
| 目标 C# 模式 | `[JsonConverter]`、`[JsonPropertyName]`、`[JsonIgnore]`、`[JsonInclude]`、`[JsonNumberHandling]`、`[JsonExtensionData]`、`[DataContract/DataMember]`、`[Obsolete]`、`[DefaultValue]` 等 → 统一形态（例如归一为转换器稳定识别的标记 attribute，或把构造期需要的反射信息改写为显式注册代码） |
| 解决的转换难点 | attribute 在转换器里只有零星处理（全 Transformers 目录仅 `FieldTransformer.cs`/`PropertyTransformer.cs`/`EnumTransformer.cs` 7 处 AttributeListSyntax 命中），其余靠 compat 注解群硬桥：`CompatJsonConverter.java`、`CompatJsonPropertyName.java`、`CompatJsonIgnore.java`、`CompatJsonInclude.java`、`CompatJsonNumberHandling.java`、`CompatJsonExtensionData.java`、`CompatAttributeTypes.java`、`DefaultContractResolver.java`；映射配置 `config/TypeMappings.json:78-89,361-365`；ROADMAP #7「attribute(json)」 |
| 证据 | 测试 `JsonNumberHandlingAnnotationProjectTests.cs`、`ReflectionAttributeCompatTests.cs`、`SystemTextJsonDeserializeTypeReferenceTests.cs`、`DataContractSerializerCompatibilityTests.cs` |
| 与现有通道关系 | 应排在 C02（closed type token）之后；反射/字符串契约风险与 E17 nameof 同类（见 §4） |
| 优先级 | **P2**（桥接厚但当前可用；优先做「枚举化/白名单化」窄版本） |
| 状态 | 缺失 |
| 验证 | 中间树 build/test；fixture：STJ + Newtonsoft（`JsonConvert.java`/`JsonSerializerSettings.java`）+ DataContract 三套注解 |

#### C11 Property 去糖（P2）
| 字段 | 内容 |
|------|------|
| 名称 | `MemberDesugar`（property 规则） |
| 分类 | 1.4 成员 |
| 目标 C# 模式 | 显式/自动属性（含 init、表达式体、外显接口实现、hiding）→ 后备字段 + `GetX/SetX` 普通方法的可编译 C# |
| 解决的转换难点 | `Member/PropertyTransformer.cs`（192 行高密度单行代码，自动属性后备字段、init、显式接口、hiding、struct clone 耦合，见 L9-40 即有 10+ 分支）；实验 `Lowering/LowerProperty.cs`（255 行）证明目标形态；相关回归密集（`PropertyHidingTests.cs`、`ExplicitInterfacePropertyConflictTests.cs`、`AutoPropertyFieldAccessTests.cs`、`PropertySetterExpressionBodyTests.cs` 等） |
| 证据 | 同上 |
| 与现有通道关系 | 与 C12/C13/C14 同属一族，建议一个 preprocessor 四条规则；必须晚于 E13/C07（属性是 struct 变异迁移主战场）；被 E17 的改名表覆盖（JK-1 属性符号） |
| 优先级 | **P2** |
| 状态 | 缺失 |

#### C12 Indexer 去糖（P2）
| 字段 | 内容 |
|------|------|
| 名称 | `MemberDesugar`（indexer 规则） |
| 分类 | 1.4 成员 |
| 目标 C# 模式 | `this[...]` 声明 → `GetItem/SetItem` 方法；调用点 `obj[i]`（含多维、字典式、ref 返回）→ 方法调用 |
| 解决的转换难点 | `Member/IndexerTransformer.cs`（304 行，显式接口/符号兜底/partial 合并分支 L25-40）+ `ElementAccessTransformer.cs`（343 行）的属性-数组二义；实验 `Lowering/LowerIndexer.cs:37-62` 即同形降级；测试簇 `DictionaryIndexerReadCheckedTests.cs`、`MultiArgIndexerCompoundAssignmentTests.cs`、`InterfaceIndexerReturnTypeTests.cs`、`ArrayIndexerAccessTests.cs` |
| 证据 | 同上 |
| 与现有通道关系 | 同 C11；复合赋值/自增（C17）依赖 indexer 先方法化 |
| 优先级 | **P2** |
| 状态 | 缺失 |

#### C13 Event 去糖（P2）
| 字段 | 内容 |
|------|------|
| 名称 | `MemberDesugar`（event 规则） |
| 分类 | 1.4 成员 |
| 目标 C# 模式 | 字段式事件 + 显式 add/remove + 触发点 → 委托字段 + `AddX/RemoveX`/raise 方法；调用点统一改写（语义等价 C#） |
| 解决的转换难点 | `Member/EventFieldTransformer.cs`（447 行，后备存储判定 L15-44、监听器签名/接口限定）+ `Java/Rewriters/EventHandlerLambdaRewriter.cs` + 实验 `Lowering/LowerEvent.cs:35-47`（`add/remove/...Listeners.forEach` 形态）；测试 `EventDelegateInvokeMethodTests.cs`、`InterfaceEventListenerNamingTests.cs`、`EventHandlerEventArgsMappingTests.cs`、`EventAndStaticPropertyRegressionTests.cs` |
| 证据 | 同上 |
| 与现有通道关系 | 与 E05 DelegatePreprocessor 边界：E05 已处理委托 `+=` 组合；事件规则消费其产物。一族实现 |
| 优先级 | **P2** |
| 状态 | 缺失 |

#### C14 Operator 去糖（P2）
| 字段 | 内容 |
|------|------|
| 名称 | `MemberDesugar`（operator 规则） |
| 分类 | 1.4 成员 |
| 目标 C# 模式 | 用户自定义 `op_*`（含隐式/显式转换、算术、比较；op_true/false 除外——归 E17）声明 → 命名方法；调用点按符号绑定改方法调用 |
| 解决的转换难点 | `Member/OperatorTransformer.cs`（290 行，`OpSymbolToJavaName` 表 L44-45 等）+ `BinaryExpressionTransformer.cs:133-144`（enum 操作数硬拒绝）+ `UnaryExpressionTransformer.cs:150-151`（decimal 一元硬拒绝）+ `Java/Rewriters/OperatorPrecedenceRewriter.cs`；实验 `Lowering/LowerOperator.cs:38-63`；测试 `OperatorMethodNameCollisionTests.cs`、`DelegateConstraintMappingTests.cs` |
| 证据 | 同上 |
| 与现有通道关系 | op_true/false 归 C08（已有精确绑定设计可复用其语义分类基建）；须晚于 E09（无符号扩展方法已接管数值运算）与 C05（enum 运算已被 E04 归并） |
| 优先级 | **P2** |
| 状态 | 缺失 |

#### C15 checked / unchecked 归一（P2）
| 字段 | 内容 |
|------|------|
| 名称 | `CheckedArithmetic`（可并入 C14 数值族或 E09 同族扩展） |
| 分类 | 1.4 / 数值 |
| 目标 C# 模式 | `checked(...)`/checked 块 → 显式溢出检查辅助调用（对应 Java `Math.*Exact`）；`unchecked(...)`/块 → 剥离 |
| 解决的转换难点 | 今天直接拒绝：`StatementTransformer.SwitchAndResource.cs:305,309`；与 E09 的无符号窄类型 `unchecked` 包裹（`EnumSyntaxRewriter.cs:208` 提到同款需求）是同一机制 |
| 证据 | 同上 |
| 与现有通道关系 | 小而独立；建议作为「数值边界归一」规则集与 E09/C17 同通道 |
| 优先级 | **P2**（规则简单、命中即失败，性价比高） |
| 状态 | 缺失 |
| 验证 | fixture：checked 表达式溢出、unchecked 常量折叠、嵌套块 |

#### C16 foreach 补全（string / 残余解构）（P2）
| 字段 | 内容 |
|------|------|
| 名称 | 扩展 `ForeachDeconstruction` |
| 分类 | 1.3 控制流 |
| 目标 C# 模式 | 字符串 foreach → `ToCharArray()` 后 foreach；E07 未覆盖的其它解构 foreach 形态 |
| 解决的转换难点 | `StatementTransformer.Loops.cs:121`（硬拒绝）、`Loops.cs:159`；E07 对 Tuple 族显式 skip（`ForeachDeconstructionRewriter.cs:67-68`）依赖 E06 先跑——顺序契约已在，但非 KV/Deconstruct 残余仍在 |
| 证据 | 同上；测试 `ArrayForeachNotCollectedTests.cs`、`ForeachNonGenericCollectionCastTests.cs` |
| 与现有通道关系 | E07 的直接补全；排在 E06 之后（现状已满足） |
| 优先级 | **P2**（工作量极小，顺手消灭一类 fail-fast） |
| 状态 | 缺失（窄） |

#### C17 复杂自增/自减目标归一（P2）
| 字段 | 内容 |
|------|------|
| 名称 | `IncrementTarget`（数值族规则） |
| 分类 | 1.4 / 数值 |
| 目标 C# 模式 | 属性/事件/索引器/enum/decimal/无符号窄类型/IntPtr/自定义 `++/--` 目标 → 读-改-写显式语句 |
| 解决的转换难点 | `UnaryExpressionTransformer.cs:154-193` 对上述目标逐个抛异常（L169,173,176,182,184,189,192）；E09 只解决了无符号后缀（`UnsignedPostfixRewriter.cs`），其余目标仍硬失败 |
| 证据 | 同上 |
| 与现有通道关系 | 依赖 C11/C12（属性/索引器方法化后自增归约自然消失大半）；与 C05 enum、C14 decimal 运算同族 |
| 优先级 | **P2** |
| 状态 | 缺失 |

#### C18 async / await 归一（P2，高不确定）
| 字段 | 内容 |
|------|------|
| 名称 | `AsyncNormalize`（形态需原型选型） |
| 分类 | 1.5 异步 |
| 目标 C# 模式 | 两条可选形态：① 轻量版——async lambda/async 局部函数提升为命名 async 方法、统一 `ConfigureAwait` 形态；② 重量版——源码级 async 状态机消解（生成显式状态机驱动 Task-like），使输入再无 `await` |
| 解决的转换难点 | async lambda **今天直接拒绝**（`LambdaTransformer.cs:52`）；await 映射为 `CompletableFuture.join()` 并带上下文特判（`ControlFlowTransformer.cs:360-397`）；方法级 async 标记（`MethodTransformer.cs:31`）；测试簇 `ConversionTests/Categories/AsyncAwaitTests.cs`、`ConfigureAwaitConversionTests.cs`、`GetResultConversionTests.cs`、`StreamAsyncTokenTests.cs`、`TaskRunGenericMappingTests.cs`；compat `Task.java`、`ValueTask.java`、`TaskFactory.java`、`CancellationToken*.java` |
| 证据 | 同上 |
| 与现有通道关系 | 必须在 E02 之后；形态①可先做且低风险，形态②等价性成本接近重写编译器 lowering |
| 优先级 | **P2**（需求真实——async lambda 即失败；但全量降级收益/成本存疑，先做原型，见 §4） |
| 状态 | 缺失 |

#### C19 扩展方法显式化（P2）
| 字段 | 内容 |
|------|------|
| 名称 | `ExtensionMethodResolve` |
| 分类 | 1.4 成员 |
| 目标 C# 模式 | 把扩展方法调用 `x.M(a)` 按语义绑定改写为静态调用 `ExtType.M(x, a)`（含嵌套命名空间/using 引入的扩展） |
| 解决的转换难点 | 主转换需自建扩展方法索引与解析：`Pipeline/ExtensionMethodIndex.cs`、`LinqRewrite/ExtensionMethods.cs`、测试 `ExtensionMethodTests.cs`（Tests 与 ConversionTests 各一份）；Java 侧静态方法分派与 C# 扩展接收者语义在重载/泛型处反复出回归 |
| 证据 | 同上 |
| 与现有通道关系 | 须在所有会引入新扩展方法的通道**之后**（E09 注入 Cs2jUnsignedMath、E12 注入 Cs2jArray、E05 注入 Cs2jPrelude——它必须看得见这些绑定），故位置靠后；与 E16 LINQ 有交互（LINQ 即扩展方法） |
| 优先级 | **P2** |
| 状态 | 缺失 |

#### C20 partial 类型/方法合并前置（P3）
| 字段 | 内容 |
|------|------|
| 名称 | `PartialMerge` |
| 分类 | 1.7 |
| 目标 C# 模式 | partial class/struct/interface、partial 方法 → 合并后的单一类型声明 C#（中间树） |
| 解决的转换难点 | 现合并逻辑在主转换期：`PartialType/`（`PartialTypeMerger.cs` 300 行、`PartialMethodMerger.cs`）、`Context/PartialTypeMergeStore.cs`，大量测试（`PartialStructMergeTests.cs`、`PartialInterfaceTupleMappingTests.cs`）；每个下游 preprocessor 都要各自面对多部件语法 |
| 证据 | 同上 |
| 与现有通道关系 | 若前置，应放在**最前几位**（E01 之后），让后续全部通道只面对合并类型；但会与「逐文件重写 + 整树回滚」范式冲突（合并产物不可逆），需评估 |
| 优先级 | **P3**（当前路径成熟，前置主要是架构简化） |
| 状态 | 缺失 |

#### C21 匿名类型提升（P3）
| 字段 | 内容 |
|------|------|
| 名称 | `AnonymousTypeLift` |
| 分类 | 1.1 类型系统 |
| 目标 C# 模式 | `new { X = 1, Y = 2 }` → 生成具名不可变类型（跨方法/跨文件形状去重，类似 E06 的 tuple shape registry） |
| 解决的转换难点 | 主转换器现合成 record：`Transformers/AnonymousTypeRecordSynthesizer.cs`；LINQ 与 foreach 高频耦合（`ForeachAnonymousTypeTests.cs`、`LinqAnonymousTypeExtractedMethodTests.cs`） |
| 证据 | 同上 |
| 与现有通道关系 | 应在 E16 LINQ 之前（LINQ 过程式化会大量暴露匿名形状）；可复用 E06 shape registry 基建 |
| 优先级 | **P3**（Java 28 record 路径已能吸收，仅低版本/复杂 LINQ 场景受益） |
| 状态 | 缺失 |

#### C22 `Nullable<T>` 值类型提升（P3）
| 字段 | 内容 |
|------|------|
| 名称 | `NullableValueLift` |
| 分类 | 1.1 类型系统 |
| 目标 C# 模式 | `T?`（值类型）上的运算/比较/`GetValueOrDefault`/`bool?` 三值逻辑 → 显式 `.HasValue/.Value` 或辅助调用 |
| 解决的转换难点 | 转换器侧特判分散（`NullableBoolComparisonTests.cs`、`NullableGetUnderlyingTypeTests.cs`、`NullableBaseTypeRuntimeClassTests.cs`、`GetValueOrDefaultMappingTests.cs`）；E04 已含「NullableLiftedRewritten」一类规则（`EnumPreprocessorStatistics.cs:15`）说明模式有效 |
| 证据 | 同上 |
| 与现有通道关系 | 与 E04 数值/枚举提升同族，可吸收 |
| 优先级 | **P3**（compat `Nullable*` + 现有特判基本覆盖） |
| 状态 | 缺失 |

#### C23 插值字符串显式展开（P3）
| 字段 | 内容 |
|------|------|
| 名称 | `InterpolationExpand` |
| 分类 | 1.8 表达式 |
| 目标 C# 模式 | `$"..."`（含插值、对齐、格式串、raw string）→ `string.Format`/`string.Concat` 显式调用或 DefaultInterpolatedStringHandler 形态 |
| 解决的转换难点 | `Expression/Transformers/StringExpressionTransformer.cs`（336 行）+ `Java/Rewriters/StringConcatRewriter.cs`；回归密集（`StringFormatDoubleTests.cs`、`StringConcat*` 等） |
| 证据 | 同上 |
| 优先级 | **P3**（现路径成熟，前置收益主要是删掉一整个大 transformer） |
| 状态 | 缺失 |

#### C24 对象/集合初始化器归一（P3）
| 字段 | 内容 |
|------|------|
| 名称 | `InitializerDesugar` |
| 分类 | 1.8 表达式 |
| 目标 C# 模式 | 对象初始化器、集合初始化器、`with` 字典初始化、target-typed new → 构造 + 逐属性 set/Add 的语句序列 |
| 解决的转换难点 | `ObjectCreationTransformer.cs`（含 `stackalloc` 分支）、测试 `ObjectInitializerArrayToIListTests.cs`、`ObjectInitializerMethodChainTests.cs`、`TernaryObjectInitializerNullGuardTests.cs` |
| 证据 | 同上 |
| 优先级 | **P3** |
| 状态 | 缺失 |

#### C25 dynamic（P3，长期观察）
| 字段 | 内容 |
|------|------|
| 名称 | `DynamicDesugar` |
| 分类 | 1.1 类型系统 |
| 目标 C# 模式 | `dynamic` 绑定点 → 显式反射/对象分派（仅在符号可静态推断处） |
| 解决的转换难点 | Transformers 内几乎无 dynamic 处理（仅 `ElementAccessTransformer.cs:275` 注释提及 unknown/dynamic fallback）；Java 无动态子类型；E17 设计把 dynamic 站点明确列为不可行（JK-8） |
| 证据 | 同上 |
| 优先级 | **P3**（静态不可证场景多，建议长期仅做「可证明子集 + 其余报错」） |
| 状态 | 缺失 |

#### C26 LINQ 通道独立化（P2，结构性候选）
| 字段 | 内容 |
|------|------|
| 名称 | 复用 `LinqRewrite`，新增 `ProjectLinqPreprocessor` |
| 分类 | 1.8 LINQ |
| 目标模式 | 把 E16 的嵌入式重写提升为标准 preprocessor：独立中间目录 `.cs2j-linq-src`、独立 verb、`dotnet build/test` 等价性闸门、skipped-chain 报告（现有 `--linq-report` 已产统计，`Program.cs:875-876`） |
| 解决的转换难点 | 今天 LINQ 重写与主转换共享 SemanticModel、失败不阻断、无数 C# 侧等价性证明；独立后可在 .NET 侧直接验证过程式化正确性，并让 Stream/Procedural 两条策略变成两个可独立验证的输入 |
| 证据 | 调用点 `ProjectPasses.cs:164,286-351`；规则文件 2156 行；报告基建 `LinqRewrite/LinqRewriteObservability.cs` |
| 与现有通道关系 | 依赖 C19（扩展方法解析）若同批做需排序：LINQ 先（消费扩展方法糖）、C19 在后；应在 E02/E03/E06 之后 |
| 优先级 | **P2**（不新增功能，但显著提升可验证性与流水线一致性） |
| 状态 | 缺失（重写器存在，通道化未做） |

#### C27 record `with` / init/required 补充去糖（P3）
| 字段 | 内容 |
|------|------|
| 名称 | 可并入 C11 `MemberDesugar` 或 E11 |
| 分类 | 1.4 / 1.2 |
| 目标 C# 模式 | `with` 表达式 → clone + 初始化；`init` 访问器、`required` 成员 → 普通可设置形态 |
| 解决的转换难点 | `Type/RecordTransformer.cs:25-59` 的额外成员 fallback；E11 已处理只读化但 `with`/init 使用点仍在表达式/属性 transformer 分散 |
| 证据 | `RecordTransformerTests.cs`、`RecordTupleTests.cs`、`PropertyTransformer.cs:21`（init 当 set 处理） |
| 优先级 | **P3** |
| 状态 | 缺失 |

#### C28 指令/using trivia 清扫扩展（P3）
| 字段 | 内容 |
|------|------|
| 名称 | 扩展 `ConditionalDirectiveStrip` |
| 分类 | 1.7 |
| 目标 C# 模式 | `#line`、`#error`（非恒剥场景）、`#define` 限界、extern alias、global using 注入/别名归一、文件作用域命名空间可选归一等 trivia 层清扫 |
| 解决的转换难点 | E01 已剥 `#pragma/#nullable/#region(可选)`（`ConditionalDirectiveStripper.cs:75-83`）；using 别名解析在转换器内有专门注册表（`Context/UsingAliasRegistry.cs`、测试 `UsingAliasResolutionTests.cs`、`GlobalAliasQualifiedNameTests.cs`） |
| 证据 | 同上 |
| 优先级 | **P3**（整洁度/一致性，不解决 fail-fast） |
| 状态 | 缺失（窄扩展） |

#### C30 closed generic type metadata 前置化（P2，需先核实现状）
| 字段 | 内容 |
|------|------|
| 名称 | 不单独新建 C# 通道；作为 **C02 的产出契约**登记（备选：生成「闭型元数据注册」C# 代码） |
| 分类 | 1.6 反射/序列化 |
| 目标 C# 模式 | 让闭型泛型（如 `List<List<short>>`）的类型实参在 C# 阶段就显式可追溯（显式 Type token/注册代码），供 Java 反射侧零 ThreadLocal 消费 |
| 解决的转换难点 | 嵌套泛型 JSON 序列化长期靠 ThreadLocal 队列（设计 §1 描述的 `[String]` 误解析根因）；compat 已落 ThreadLocal-free 键控（`ConcurrentHashMapHelper.java:10-31,63-72`、设计列出的 `TypeHelper.closedCacheKey/createConverterForClosedType` 等 API） |
| 证据 | 设计 `docs/superpowers/specs/2026-09-22-closed-type-metadata-chain-design.md`（状态标注「已实现(设计定稿)」，§3.2 列 `ProjectPasses.cs` 的 PatchClosed* 系列）；**但当前工作区 `src/` 全仓 grep 不到 PatchClosed*/ClosedTypeMetadata/ThreadLocal 任何 C# 落点**——生成码补丁不在本树（可能在下游 dotnet-text-json 仓或未合入），列为开放问题（§4）；ROADMAP #10 |
| 与现有通道关系 | 与 C02 一体两面；落地前需先做一次代码考古确认补丁去向，再决定是「C# 前置注册」还是「Java 侧反射闭型」路线 |
| 优先级 | **P2**（路线已被验证有效，但本仓工作量待核实） |
| 状态 | 设计完成；compat 侧有落点；cs2j 生成码侧现状不明 |

---

## 3. 推荐实施路线

### 3.1 建议顺序（仅排序，不含实现设计）

1. **C01 RefOutIn 接线收尾**（P0，Core 已就绪；先补 Java 映射 spec 与 CLI 包装）。
2. **C03 控制流去糖**（P0，纯机械降级、消灭一整组 fail-fast；注意排在 E14 之前）。
3. **C02 Generic 深化**（P0，ROADMAP #5/#10 的 C# 侧，连带核实 C30）。
4. **C05 enum 声明归一**（P1，扩 E04；与 C03 同批验证 enum switch）。
5. **C07 struct 值语义补全**（P1，扩 E13，回收 StructCloneHelper）。
6. **C04 yield 迭代器降级**（P1，先原型 try/finally 子题）。
7. **C08 JavaKeywords 落地**（P1，设计完备；按 spec 必须在链尾，默认关）。
8. **C06 unsafe/FFM**（P1，按 fixed→stackalloc→指针→extern 分规则滚动，依赖 C01）。
9. **C10 JSON/Attribute 窄版** 与 **C09 ThreadStatic**（P2，可并行，互不依赖）。
10. **成员去糖族 C11→C12→C13→C14**（P2，一个通道四条规则，按此顺序因后者依赖前者方法化）。
11. **数值边界族 C15→C17→C16**（P2，可挂在 E09/E04 既有通道内扩展）。
12. **C19 扩展方法显式化 → C26 LINQ 通道独立化**（P2，LINQ 先重写、扩展解析后固化）。
13. **C18 async**：先做形态①原型再决定是否投形态②。
14. P3 项按下游实际命中率排序：C21/C22/C23/C24/C27/C20/C28/C25。

### 3.2 与现有链的依赖/顺序总览

现状 `Program.cs` 顺序（已核对行号）：

```text
E01 strip-conditional (174)
 → E02 local-function-lift (208)
 → E03 null-coalesce (242)
 → E04 enum ops (280)
 → E05 delegate (321)
 → E06 tuple-to-struct (361)        [注释要求先于 E08：Program.cs:363-364]
 → E07 foreach-deconstruction (406) [Tuple 族依赖 E06：Rewriter:67-68]
 → E08 strip-default (443)
 → E09 unsigned-type (479)
 → E10 generic (514)
 → E11 record-readonly (553, opt-in)
 → E12 array-method (595, opt-in)
 → E13 make-readonly (636)          [注释要求先于 E14：Program.cs:638]
 → E14 eliminate-goto (679)
 → 主转换
```

候选插入建议（依赖理由见各条目）：

```text
E01 → [C20 若做，放这里] → E02 → E03 → C03(控制流去糖) → E04+C05 → E05
 → E06 → E07+C16 → E08 → E09 → E10+C02(+C30) → C01(RefOutIn，位置需原型确认)
 → E11 → E12 → E13+C07 → C06(unsafe) → C04(yield)
 → C09/C10 → C11→C12→C13→C14(成员去糖) → C15/C17(数值族)
 → C19(扩展方法) → [E16/C26 LINQ 独立化后移到此处] → C18(async)
 → E14 goto → C08(JavaKeywords，设计定死最后) → 主转换
```

横切约束：
- 凡**注入支撑库**的通道（E05 Cs2jPrelude、E06 Cs2jTuples、E09 Cs2jUnsignedMath、E12 Cs2jArray、C01 Cs2jByRef）都要复用 `ProjectPreprocessSource.cs` 的 restore/资源准备，并在主转换前过滤支撑工程（现做法见 `Program.cs:750-756`）。
- 凡**改名/改签名**的通道（C01、C02、C11-C14、C19、C08）必须基于 SemanticModel 的全编译符号表，不能文本替换——E11/C08 的 plan+回滚范式是指定骨架。

### 3.3 「同一底层难点」的合并建议

| 合并族 | 成员 | 共同底层 |
|--------|------|----------|
| 控制流去糖 | C03 + C16（string foreach）+ 部分 C05（enum switch） | 语句/模式到 if-链的机械归约 |
| 成员去糖 | C11 + C12 + C13 + C14（+C27） | 「带访问器语法的成员 → 普通方法 + 后备字段」，对应实验 Lowering 四件套 |
| 数值边界 | E09 后缀规则 + C15 + C17 + C22 + enum 运算（E04） | 数值运算在窄类型/特殊目标上的显式化；已有 UnsignedPostfix/EnumSyntaxRewriter 基建 |
| 值语义 | E11 + E13 + C07 + C01 的 ref struct 子项 | 复制/变异/只读边界；复用 StructAnalyzer/BoxingReachability/MethodMigrator |
| 泛型擦除补偿 | E10 + C02 + C09（可合并静态字段规则）+ C30 | reified→erased 的运行时信息显式贯穿 |
| unsafe 家族 | C06 全部子规则 + E12 的 Span/数组交叉 | 指针模型→索引/Span/FFM；共用 FfmHelper |
| enum | E04 + C05 + C17 enum 自增 + C22 可空 enum | 同一 EnumSyntaxRewriter 扩展，不新建通道 |

---

## 4. 风险与开放问题

1. **C18 async 全量降级的等价性/成本**：源码级 async 状态机 ≈ 重写编译器 lowering，且与 `using`/try-finally/锁交互复杂；建议先做形态①（提升 async lambda、归一 ConfigureAwait），用真实命中率决定形态②去留。
2. **C04 yield 与 try/finally、using、ref 的组合**：Dispose 时序与 C01 holder、C06 Span 释放相互耦合，必须先出原型 + 专项 fixture，不能直接进默认链。
3. **C06 unsafe 的 ABI/外部契约**：`[DllImport]`/`[LibraryImport] extern`、对外部接口的 ref 实现必须锁定（RefOutIn 设计 D6 已确立原则），任何指针改写都不能改变封送布局；fixed→MemorySegment 只在字段缓冲区被验证过（`FieldTransformer.cs:70-101`）。
4. **C05 enum-as-class 的外显契约**：`Enum.ToString/Parse/Format/GetValues`、switch、字典键、`[Flags]` 序列化都会随表示形态变化；compat `EnumHelper` 虽厚但仍是「Java enum」假设，前置归一后需要同步重定义桥接面。
5. **C01 RefOutIn 的 Java 映射缺口是真实阻断**：设计 D3 自述「Java 产物暂不可编译为已知中间状态」；`RefOutInProjectTests.cs:29-40` 现断言的是旧 IntHolder/ObjectHolder 路径——接线意味着要同时改测试断言与 holder 体系，必须先补 Java 侧 spec。
6. **C30 closed metadata 的代码考古**：设计声称 PatchClosed* 落在 `ProjectPasses.cs`，当前工作区不存在（全仓 grep 无命中），compat 侧却有 `ConcurrentHashMapHelper.putIfAbsentUntyped` 等落点。实施前必须先确认补丁是在下游仓、已回退还是未合入，避免重复建设。
7. **反射/字符串契约对所有「改名通道」的风险**：`GetMethod("True")`、XAML、JSON 按名匹配、表达式树对 LINQ 提供方的形态依赖——E17 spec §9 已文档化（nameof 字面量化、反射不保证）；C10/C11-C14 同理会触碰成员名，需沿用同一限制声明与 overrides 机制。
8. **表达式树与 dynamic 是多通道共同禁区**：C08（JK-8）、C25、C14 的转换运算符在 `Expression<T>` lambda 内改写会改变树形态；各通道需共享「表达式树节点内跳过」判定工具。
9. **实验性 Lowering 不是实现资产**：`Lowering/*` 仅被 Java2 对比测试引用（`NewConversionPipeline.cs:27-33` ↔ `PipelineComparisonTests.cs`），前置 preprocessor 只能借其**形态**，不能尝试共用代码（IR 不同、生产不可达）。
10. **opt-in 通道的长期默认化**：E11 RecordReadOnly、E12 ArrayMethod 当前默认关——新增通道若也长期 opt-in，会形成「多套事实标准」；建议每个 P0/P1 通道落地时同时定义默认开启的验收门槛（build/test 全绿 + csharp-sdk 基线不退化）。
11. **中间树验证成本**：每环都跑 restore + build + test 的串行链会显著拉长 convert-project 耗时；`VerifyBuild/VerifyTests` 已可关（`Program.cs:4773-4777`），但默认开启的通道越多越需要指纹缓存正确——新增中间目录须同步接入现有的 fingerprint/preserve 机制（现状通过 `opts.Source` 串联与指纹回填实现，见各块 `FingerprintSourcePath/FingerprintInputRoot` 赋值）。

---

## 附：ROADMAP 十项待办对照

| ROADMAP 条目 | 现状 | 对应本清单 |
|--------------|------|------------|
| 1. pre process `default(T)` | 已实现（`GenericPreprocessor.cs:13-14`） | E10 |
| 2. pre process struct | 大部分实现：只读化（E13）已做；可变 struct clone 仍在 Java 侧 | E13 + C07；ref struct 在 C06 |
| 3. pre process ref/out | Core 完成、CLI/Java 未接 | E15 + C01 |
| 4. pre process goto | 已实现 | E14 |
| 5. some generics | 仅 3 模式；typeof/is-as/静态字段未做 | E10 + C02 |
| 6. `[ThreadStatic]` | 转换器内联处理中，未前置 | C09 |
| 7. attribute(json) | compat 注解桥接为主，无前置通道 | C10 |
| 8. method parameter default value | 已实现 | E08 |
| 9. local function | 已实现（带 residual 闸门） | E02 |
| 10. closed generic type metadata（Json、去 ThreadLocal） | 设计定稿、compat 有落点；cs2j 生成码补丁本树未见 | C30 + C02 |
