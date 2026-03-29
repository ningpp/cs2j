# 面向超复杂 C# 项目的架构升级文档

## 1. 文档目的

当前项目已经是一个可工作的 C# → Java 转换系统，具备以下核心能力：

- 基于 Roslyn 的语法分析与语义模型
- 单文件与项目级转换（含 partial type 合并）
- JSON 驱动的类型/方法/命名空间映射（3,059 行配置，170+ 类型映射）
- 34 个专用转换器（15,062 行），覆盖类型、成员、语句、表达式
- 基于反射自动发现的表达式转换器注册体系（17 个转换器，8,588 行）
- LINQ 预处理引擎（query syntax + method chain → 过程式循环/Stream API）
- async/await → CompletableFuture 链路转换
- 兼容层生成（33 个 helper 类：Holder、XML、JSON、MSTest、工具类）
- 多模块 Maven 输出规划与 POM 生成
- 191 个回归测试文件（12,747 行）

### 代码规模基线

截至当前版本的精确度量：

| 模块 | 源文件数 | 代码行数 |
|------|---------|---------|
| CSharpToJava.Core | 65 | 24,847 |
| CSharpToJava.CLI | 4 | 1,939 |
| CSharpToJava.TypeMapping | 1 | 381 |
| CSharpToJava.Async | 1 | 60 |
| CSharpToJava.LINQ | 1 | 60 |
| **生产代码合计** | **72** | **27,287** |
| 测试代码 | 191 | 12,747 |
| 类型映射配置 | 1 | 3,059 |
| **总计** | **264** | **43,093** |

**核心文件行数**：

| 文件 | 行数 | 职责 |
|------|------|------|
| `ProjectConversionPipeline.cs` | **4,211** | 项目转换 + 兼容类生成 + 后处理补丁 |
| `Program.cs` | 1,461 | CLI + 全局后处理 |
| `ConversionContext.cs` | 1,149 | 转换状态 + 类型映射 + 语义查询 |
| `LinqRewriter.cs` | 722 | LINQ 预处理 |
| `CSharpToJavaVisitor.cs` | 401 | 语法树遍历入口 |
| `ConversionPipeline.cs` | 343 | 单文件转换编排 |
| `TypeMappingRegistry.cs` | 321 | JSON 映射查询 |
| `ProjectDiscovery.cs` | 236 | 项目发现 |
| `MultiModulePlanner.cs` | 182 | Maven 模块规划 |
| `TransformerFactory.cs` | 50 | 无状态转换器工厂 |

项目目标已从"能转换一批 C# 代码"提升为"支持特别复杂的 C# 项目转换为 Java 项目"。本文基于逐文件代码审查的深入分析，给出架构升级方向、目标架构和分阶段落地路线。

## 2. 结论摘要

### 核心判断

1. 当前架构已经验证了 **"Roslyn + Visitor/Transformer + Java AST + 类型映射"** 这条技术路线是可行的。
2. 系统的主要上限不是规则数量，而是缺少 **"解决方案级项目模型"** 和 **"可治理的转换中间层"**。
3. 必须把系统从"单次临时编译驱动的转换器"升级为 **"解决方案级编译与转换平台"**。

### 最紧迫的架构瓶颈

经过精确代码度量，系统当前最突出的结构性问题是：

| 瓶颈 | 量化事实 |
|------|---------|
| `ProjectConversionPipeline.cs` 已成为 God Class | 4,211 行，包含 60 个文件名特例分支、598 次 `Replace()` 调用、90 次 `Regex.Replace` |
| 后处理补丁已从兜底变为主机制 | 两个文件合计 747 次 `Replace()` 调用、102 次 `Regex.Replace`，覆盖 60+ 具体文件名 |
| `ConversionContext` 职责过重 | 1,149 行，同时承担栈管理、语义查询、类型映射（`MapTypeInternal` 约 500 行）、缓存、诊断 |
| 缺少真实项目求值 | `ProjectDiscovery`（236 行）仅通过 XML 解析 `.csproj`，不执行 MSBuild 求值 |
| 输出侧 Java IR 极薄 | Java AST 仅 5 个文件 682 行，大量转换直接生成字符串 |

### 优先升级方向

1. 拆解 `ProjectConversionPipeline`，将 4,211 行的 God Class 分解为职责明确的子系统
2. 统一后处理补丁为可治理的规则引擎
3. 增强 Transformer 层能力，将通用补丁提升到规则层
4. 引入真实 Solution/Project 求值能力
5. 建立规范化 C# IR 与扩展 Java IR，减少字符串级修补
6. 拆分 `ConversionContext`，建立服务与会话边界

## 3. 当前架构分析

### 3.1 当前分层与代码度量

从代码结构看，当前系统分为六层。

#### A. 入口与输出层

**代码规模**：`Program.cs` 1,461 行，`MultiModulePlanner.cs` 182 行，`ProjectDiscovery.cs` 236 行

**职责**：

- CLI 参数解析（三种命令：单文件转换、项目转换、分析）
- 单文件、single-module、multi-module 三种模式切换
- Maven POM 生成（父 POM + 子模块 POM）
- 输出目录写入、资源文件复制
- 全局字符串后处理（149 次 `Replace()`、12 次 `Regex.Replace`）

**代表实现**：

- `src/CSharpToJava.CLI/Program.cs`：1,461 行，其中 `TryWriteConvertedFile` 方法包含 14 个文件名特例分支（如 `BasicFileProcessor`、`GeometryGraphReader`、`ClusterTests`、`CdtSweeper` 等），全局重写部分处理 `String.Empty` → `""`、大小写方法修正、`StringHelper` 调用、`BiConsumer` Lambda 正则修正等
- `src/CSharpToJava.CLI/MultiModulePlanner.cs`：182 行，拓扑排序 + 测试项目共置逻辑（单引用测试项目共置到生产模块，多引用测试项目独立模块）
- `src/CSharpToJava.CLI/ProjectDiscovery.cs`：236 行，解析 `.csproj` XML 提取 `ProjectReference`、`PackageReference`、资源文件，启发式识别测试项目（NuGet 包名、项目名后缀、`IsTestProject` 属性三重检测）

**已验证能力**：

- 两种输出策略（single-module / multi-module）均已可用
- 资源文件发现（`None`/`Content`/`Resources/` 目录）
- 拓扑排序确保依赖顺序正确

**问题**：

- `ProjectDiscovery` 仅解析 `.csproj` XML，不执行 MSBuild 求值，无法处理 `Directory.Build.props/targets`、多目标框架、条件属性、SDK 导入、Source Generator 产物
- `Program.cs` 承担了全局字符串后处理，不是纯粹的 orchestration 层
- 没有 `.sln` / `.slnx` 解析能力

#### B. 编译与项目转换层

**代码规模**：`ConversionPipeline.cs` 343 行，`ProjectConversionPipeline.cs` **4,211 行**

这是当前系统的核心，也是最大的技术债集中区。

**`ConversionPipeline`（343 行）的职责**：

- 构建临时 `CSharpCompilation`（加载 8 个框架程序集 + 合成全局 using 声明）
- LINQ 预处理（调用 `LinqRewriter`）
- 单文件转换编排（Parsing → Transformation → CodeGeneration 三阶段）
- 获取框架补充引用（`System.Runtime.dll`、`netstandard.dll`）

**`ProjectConversionPipeline`（4,211 行 God Class）的职责**：

| 职责 | 内容 |
|------|------|
| 项目编译构建 | 从源文件列表构建 `CSharpCompilation` |
| Partial 类型合并 | 查找并分组 partial 声明，调用 `PartialTypeMerger` |
| 类型转换调度 | 按 class/interface/enum/struct/record/delegate 六种路径分发 |
| 兼容类生成 | 生成 33 个 helper 类（`IntHolder`、`StringHelper`、`XmlReader` 等） |
| 跨包 Import 注入 | 为所有包添加 wildcard import |
| **字符串级后处理** | **60 个文件名特例分支，598 次 Replace()，90 次 Regex.Replace** |

转换六阶段流程：

1. Build CSharpCompilation from all source files
2. Find and group partial type declarations
3. Transform each type group via specialized transformers
4. Generate compatibility helper classes (when `EmitCompatibilityHelpers=true`)
5. Add cross-package wildcard imports
6. Apply file-specific compatibility rewrites

**问题**：

- 4,211 行的文件同时处理六个完全不同的职责
- 第 6 步（字符串级后处理）占据了文件的主要行数并持续膨胀
- 每当新的复杂文件转换出错，最常见的修复是新增 `if (result.FileName.Contains("xxx"))` 分支
- 项目级语义是"把目录中 `.cs` 文件拼进同一个临时 compilation"，没有真实引用图语义
- 不区分引用边界、TFM 切片、条件编译符号

#### C. 语法访问与规则转换层

**代码规模**：34 个转换器文件，合计 15,062 行

这是系统架构最健康的一层，也是最值得保留和扩展的基础。

**结构**：

```
Visitors/
  CSharpToJavaVisitor.cs          (401 行) — Roslyn CSharpSyntaxVisitor<JavaSyntaxNode?> 入口
Transformers/
  TransformerFactory.cs           (50 行) — 无状态单例工厂
  Type/                           (6 个转换器)
    ClassTransformer.cs                — C# class → Java class（含 partial 合并）
    InterfaceTransformer.cs            — C# interface → Java interface
    StructTransformer.cs               — C# struct → Java class
    EnumTransformer.cs                 — C# enum → Java enum
    RecordTransformer.cs               — C# record → Java record
    DelegateTransformer.cs             — C# delegate → Java functional interface
  Member/                         (7 个转换器)
    MethodTransformer.cs               — 方法（含 async→CompletableFuture）
    PropertyTransformer.cs             — 属性 → getter/setter
    FieldTransformer.cs                — 字段
    ConstructorTransformer.cs          — 构造函数
    IndexerTransformer.cs              — 索引器 → getItem/setItem
    EventFieldTransformer.cs           — 事件 → 函数式接口
    OperatorTransformer.cs             — 运算符重载
  Statement/
    StatementTransformer.cs            — 所有语句类型
  Expression/                     (17 个文件, 8,588 行)
    ExpressionTransformerFacade.cs     — 单例门面
    ExpressionTransformerRegistry.cs   — 属性标注 + 反射自动发现注册表
    Transformers/
      ArgumentTransformer.cs
      AssignmentTransformer.cs
      BinaryExpressionTransformer.cs
      ControlFlowTransformer.cs
      ElementAccessTransformer.cs
      IdentifierExpressionTransformer.cs
      InvocationExpressionTransformer.cs
      LambdaTransformer.cs
      LiteralExpressionTransformer.cs
      ObjectCreationTransformer.cs
      QueryExpressionTransformer.cs
      StringExpressionTransformer.cs
      TypeOperationTransformer.cs
      UnaryExpressionTransformer.cs
    Utilities/
```

**已验证的优秀模式**：

- `TransformerFactory` 采用无状态单例模式，所有转换器共享、不可变、零分配
- `ExpressionTransformerRegistry` 使用 `[TransformerRegistration]` 属性 + 反射自动发现。新增表达式转换器只需标注属性即可注册，无需修改工厂或注册代码
- 表达式通过 `ConcurrentDictionary<SyntaxKind, IExpressionTransformer>` 分发，扩展性好
- C# 9-12 新表达式（Tuple、Declaration、Ref 等）通过内联 `DelegateExpressionTransformer` 覆盖

**问题**：

- 表达式转换器的自动发现机制没有扩展到类型/成员/语句转换器（这些仍是硬编码工厂方法）
- 跨节点、跨文件的语义重写无法在单个 Transformer 中完成，只能退化为后处理补丁
- 缺少规则级元数据（ID、优先级、适用条件、诊断模板）

#### D. LINQ 预处理层

**代码规模**：`LinqRewriter.cs` 722 行

**架构**：

- 继承 Roslyn `CSharpSyntaxRewriter`，在主转换前对语法树进行预处理
- 支持两种模式：query syntax 重写和 method chain 重写
- 自动合成 Java record 类型用于匿名类型投影
- 跟踪重写统计：`RewrittenMethods`、`RewrittenLinqQueries`、`SkippedLinqChains`

**这层的设计是正确范式**：在语法树层面做预处理，通过 Roslyn `SyntaxRewriter` 保证结构完整性。无法重写的 LINQ 链产生明确的 skip 记录，而不是静默失败。这种"在正确的抽象层做正确的变换"的思路应推广到更多场景。

#### E. 共享状态与语义辅助层

**代码规模**：`ConversionContext.cs` 1,149 行，Partial Type 模块 630 行（4 个文件）

**`ConversionContext` 内部结构**：

| 功能块 | 估算行数 | 职责 |
|--------|---------|------|
| 类型映射（`MapTypeInternal`） | ~500 | 处理 nullable、anonymous、generic、array、nested、type parameter、[Flags] enum 等 8+ 种类型类别 |
| 栈管理（namespace/type/method） | ~100 | Push/Pop 当前作用域 |
| Import 收集与去重 | ~80 | Java import 管理 |
| Ref/Out Holder 分配 | ~60 | 分配唯一 holder 变量名（`_varRef`/`_varRef2`） |
| 合成 Record 注册 | ~50 | 匿名类型 → Java record 去重 |
| Using Alias 注册 | ~50 | 文件级别名解析与验证 |
| 其他（Pre/PostStatements、诊断、语义查询辅助） | ~309 | 杂项 |

**Partial Type 模块**（4 个文件，630 行）：

- `PartialTypeMerger.cs`（245 行）：基于 Roslyn symbol 的 partial 类型分组
- `MergedTypeDeclaration.cs`（158 行）：合并声明数据模型
- `PartialMethodMerger.cs`（141 行）：partial method 定义与实现合并
- `PartialTypeGroup.cs`（86 行）：分组数据结构

**问题**：

- `MapTypeInternal` 约 500 行，是最复杂的单体方法，内部按类型类别分支。应拆分为独立方法群或独立服务
- 同一个对象同时承担：只读查询服务、可变会话状态、缓存容器、诊断收集器——四种本质不同的职责
- `TypeCache`（`Dictionary<ITypeSymbol, string>`）生命周期不清晰（项目级 vs 文件级）
- `StreamLocalVariables`、`QueryLetAliases`、`PreStatements`/`PostStatements` 等运行时状态应属于方法级局部上下文而非全局上下文

#### F. 类型映射与 Java 输出层

**代码规模**：`TypeMappingRegistry.cs` 321 行，`TypeMappings.json` 3,059 行，Java AST 5 个文件 682 行

**类型映射**：

- JSON 驱动，覆盖范围：

| 类别 | 映射数量 | 示例 |
|------|---------|------|
| 原语类型 | 13 | `System.String` → `String`、`System.Int32` → `int` |
| 集合 | 20+ | `List`1` → `ArrayList`、`Dictionary`2` → `LinkedHashMap` |
| 泛型接口 | 15+ | `IList`1` → `List`、`IEnumerable`1` → `Iterable` |
| 并发集合 | 6 | `ConcurrentDictionary`2` → `ConcurrentHashMap` |
| 异常 | 30+ | `ArgumentException` → `IllegalArgumentException` |
| 时间/日期 | 5 | `DateTime` → `LocalDateTime`、`TimeSpan` → `Duration` |
| 流 I/O | 10+ | `StreamReader` → `BufferedReader` |
| 系统接口 | 10+ | `IDisposable` → `AutoCloseable`、`IComparable` → `Comparable` |

- 方法映射支持精确匹配和模糊前缀匹配，含泛型 arity 归一化（backtick 语法）
- 命名空间映射覆盖 `System.*` → `java.*` 全链路

**Java AST 模型**（极薄——这是 IR 缺失的直接证据）：

| 文件 | 行数 | 内容 |
|------|------|------|
| `JavaSyntaxNode.cs` | 27 | 抽象基类 |
| `JavaCompilationUnit.cs` | 62 | 表示一个 `.java` 文件（package + imports + types） |
| `JavaTypeDeclaration.cs` | 311 | 类/接口/枚举/记录声明 + modifiers + type parameters |
| `JavaMemberDeclaration.cs` | 268 | 字段声明、注解 |
| `JavaCommentEmitter.cs` | 14 | 注释渲染辅助 |
| **合计** | **682** | |

682 行的 Java AST 无法表达方法体、语句、表达式和控制流。所有语句和表达式的转换直接生成 Java 源代码字符串。一旦生成结果有结构性问题，唯一可用的修复手段就是字符串级的 `Replace()` 和 `Regex.Replace`。

### 3.2 当前架构的已验证优点

这套架构不是要推倒重来。它已经证明了若干关键方向是正确的。

#### 1. Roslyn 语义路线正确

使用 Roslyn 获取语义模型是本项目最重要的技术决策。当前编译阶段加载完整框架程序集（`System.Runtime.dll`、`netstandard.dll` 等 8 个）并合成全局 using 声明（`System`、`System.Collections.Generic`、`System.Linq` 等），确保了裸标识符的正确解析。类型映射、属性访问、using alias、partial type、泛型判断等能力都依赖这一点。

#### 2. 表达式转换器的自动发现是规则引擎的天然原型

`ExpressionTransformerRegistry` 的 `[TransformerRegistration]` 属性 + 反射自动发现 + `ConcurrentDictionary<SyntaxKind, IExpressionTransformer>` 分发模式，是整个项目中最优雅的架构组件。它证明了"声明式注册 + 自动发现 + 按类型分发"在这个项目中是可行且高效的。**这个模式应该推广到所有转换层。**

#### 3. LINQ 预处理模式是正确范式

`LinqRewriter` 作为 `CSharpSyntaxRewriter` 在语法树层面预处理。这种"在正确的抽象层做正确的变换"比在输出字符串上修补更健壮、更可维护。

#### 4. 项目级转换已迈出关键一步

`ProjectConversionPipeline` 先构建 compilation、再找 partial 类型组、再按类型组转换。已具备"把主项目和额外语义上下文目录一起编译"的跨项目语义补强能力。

#### 5. 测试基础扎实

191 个测试文件（12,747 行），分布：

| 测试类别 | 约数量 | 描述 |
|---------|-------|------|
| 兼容重写测试（`*CompatibilityRewriteTests`） | ~50 | 每个文件名特例都有回归保护 |
| 核心语言特性转换测试（`*ConversionTests`） | ~60 | 数组、字符串、集合、类型转换等 |
| 集合/桥接/适配器测试（`*BridgeTests`） | ~20 | ObjectHolder、Iterator、Stream 适配 |
| LINQ/Stream 测试 | ~12 | query syntax + method chain |
| 跨项目语义测试 | ~15 | 跨文件符号解析 |
| 其他（合成 record、注释、诊断等） | ~34 | |

测试采用 XUnit，命名规范统一，使用嵌入式 C# 代码 + `ConvertAndAssert()` 模式。

#### 6. 兼容层是务实方案

33 个 helper 类覆盖 Holder（10 种原语 ref/out 包装）、String、Math、Enum、File、Thread、Array、XML、JSON、Regex、Trace 等领域。对于复杂 .NET 项目，这些桥接是必要的。

### 3.3 当前架构的核心瓶颈

以下按严重程度排序。

#### 瓶颈 1：`ProjectConversionPipeline` 是系统级 God Class

**这是当前架构最严重的结构性问题。**

| 指标 | 数值 |
|------|------|
| 总行数 | **4,211** |
| 文件名特例分支 | **60 个唯一文件名** |
| `Replace()` 调用 | **598** 次 |
| `Regex.Replace` 调用 | **90** 次 |
| FileName 访问模式 | 142 处 |

目前涉及的 60 个文件名特例（精确列表）：

`AspectRatioTests`、`AttributeValuePair`、`BasicFileProcessor`、`BlockReaderFactory`、`BufferException`、`BuildBuffer`、`BundleRouter`、`CdtSweeper`、`CdtTests`、`ClusterDef`、`ClusterTests`、`CodePageHandling`、`CollectionUtilities`、`ConvexHullTest`、`Dot2SvgMain`、`DrawingUtilsForSamples`、`EdgeConstraintTests`、`EdgeExtensions`、`EdgeLabelPlacementTest`、`FlipSwitcher`、`GenericBinaryHeapPriorityQueue`、`GeometryGraphWriter`、`IncrementalSugiyamaTests`、`InitialLayoutTests`、`Label`、`LinearMetroMapOrdering`、`MetroGraphData`、`MsaglTestBase`、`NetworkSimplexTest`、`NodePositionsAdjuster`、`Optimal`、`OverlapRemovalTests`、`OverlapRemovalVerifier`、`Parser`、`PlaneTransformation`、`PushdownPrefixState`、`RectanglePacking`、`RectanglePackingTest`、`RectangularClusterBoundary`、`RectFileStrings`、`RectilinearEdgeRouterWrapper`、`RectilinearTests`、`RectilinearVerifier`、`ResultVerifierBase`、`RTreeTest`、`Scanner`、`SdShortestPath`、`Settings`、`ShapeCreator`、`ShiftReduceParser`、`StickConstraintTests`、`SugiyamaEdgeLabelTests`、`SugiyamaLayoutTests`、`SugiyamaSettingsTests`、`SugiyamaValidation`、`SvgGraphWriter`、`Test`、`TestFileReader`、`TestLineSweeper`、`Validate`、`ValueType`

这些特例分布在 `ApplyCompatibilityRewrites` 方法中，每个分支通常包含 5-30 行的字符串替换和正则修正。

加上 `Program.cs` 中 `TryWriteConvertedFile` 的 14 个额外文件名分支，总计两处后处理入口合计 **747 次字符串替换、102 次正则替换**。

#### 瓶颈 2：缺少真实的解决方案级项目模型

`ProjectDiscovery`（236 行）仅解析 `.csproj` XML 提取：

- `ProjectReference`（相对路径解析，递归发现，循环检测）
- `PackageReference`（包名和版本）
- 部分资源文件（`None`/`Content` + `CopyToOutputDirectory`，以及 `Resources/` 目录非代码文件）
- 测试项目启发式识别

复杂 C# 解决方案中必须处理但当前完全缺失的信息：

| 缺失能力 | 影响 |
|---------|------|
| `Directory.Build.props/targets` | 无法获取全局属性和条件编译符号 |
| 多目标框架 `TargetFrameworks` | 无法区分不同 TFM 切片的编译内容 |
| 条件属性 `Condition` | 无法正确展开 Debug/Release 差异 |
| SDK 风格项目导入 | 无法理解隐含的 `Compile`/`Resource` 模式 |
| `Compile Remove/Include/Update` | 无法获取真实参与编译的文件集 |
| Source Generator 产物 | 无法看到生成代码 |
| 引用程序集（非源码项目） | 无法获取第三方包的类型信息 |

当前的跨项目语义方案是"额外语义目录拼接"——把引用项目的 `.cs` 文件一起丢进同一个临时 compilation。这是有效的 workaround，但不区分项目边界和程序集可见性。

#### 瓶颈 3：Java IR 极薄，迫使后续修正在字符串层面

Java AST 仅 682 行（5 个文件），只能表达：

- 编译单元（package + imports + type declarations）
- 类型声明（class/interface/enum/record + modifiers + type parameters）
- 字段声明和注解

**无法表达**：方法体、语句、表达式、控制流。

结果：所有语句和表达式转换直接生成 Java 源代码字符串。一旦生成结果有结构性问题（缺 import、类型错误、API 不匹配），唯一修复手段是字符串级 `Replace()` 和 `Regex.Replace`。这是 747 次字符串替换存在的根本原因。

#### 瓶颈 4：`ConversionContext` 职责过度集中

1,149 行的上下文对象同时承担四种本质不同的职责：

| 职责类型 | 具体内容 | 理想归属 |
|---------|---------|---------|
| 只读平台服务 | 类型映射（`MapTypeInternal` ~500 行）、命名空间映射、语义查询 | 独立服务（线程安全） |
| 项目级缓存 | `TypeCache`（Dictionary<ITypeSymbol, string>）、合成 Record 注册 | 项目级会话 |
| 文件级状态 | Using Alias 注册（文件范围）、当前 Namespace | 文件级状态 |
| 方法级状态 | 栈（namespace/type/method）、Pre/PostStatements、Ref Holder 分配、StreamLocalVariables | 方法级状态 |

核心矛盾：只读服务和可变状态混在同一个对象中，导致无法安全并行、缓存生命周期不清晰、测试需构建完整上下文。

#### 瓶颈 5：后处理补丁从兜底变为主机制

精确统计：

| 文件 | Replace | Regex.Replace | 文件名特例 |
|------|---------|---------------|-----------|
| `ProjectConversionPipeline.cs` | 598 | 90 | 60 |
| `Program.cs` | 149 | 12 | 14 |
| **合计** | **747** | **102** | **60+**（含重叠） |

#### 瓶颈 6：兼容层未产品化

33 个 helper 类硬编码在 `ProjectConversionPipeline` 的静态列表中。问题：

- 全量生成，不按实际使用情况裁剪
- 无版本管理、无能力声明、无依赖关系
- 新增兼容类需修改 `ProjectConversionPipeline`

#### 瓶颈 7：诊断体系太轻

仅 Info/Warning/Error 三级。缺失：规则编号、失败类别、可恢复/不可恢复区分、建议修复、源-目标追踪关系、后处理审计。

#### 瓶颈 8：性能与扩展性

每次重建 `CSharpCompilation`（无缓存）、全文件读入内存（无流式）、单线程顺序（无并行）、无增量。

### 3.4 补丁分布的深度分析

对 60 个文件名特例补丁进行根因分类：

| 补丁类别 | 涉及文件数 | 典型例子 | 根因 |
|---------|----------|---------|------|
| String API 映射缺口 | ~15 | `String.Concat` → helper、大小写方法、null/empty | `StringExpressionTransformer` / `InvocationExpressionTransformer` 和 `TypeMappings.json` 覆盖不足 |
| 集合/Map API 映射缺口 | ~10 | `.getValues()`→`.values()`、`.getKeys()`→`.keySet()`、`.Count`→`.size()` | Method mapping 不完整 |
| Stream/LINQ 构造模式 | ~8 | `Arrays.stream().collect()` → `Arrays.asList()`、复杂 Collectors 链 | LINQ 重写器未覆盖所有模式 |
| XML/JSON API 适配 | ~6 | XmlReader 静态字段、UTF-8 BOM、枚举解析 | 兼容类与转换规则不联动 |
| 嵌套类型限定 | ~5 | `Cell<String>` → `Parser.Cell<String>` | `MapTypeInternal` 嵌套类型解析不完整 |
| 事件/委托签名 | ~4 | Consumer→BiConsumer 参数数量变化 | `DelegateTransformer` 覆盖不足 |
| 文件 I/O 路径 | ~3 | FileSystemInfo→File、Path 操作映射 | 缺少系统级 API 映射层 |
| 控制流重写 | ~3 | 三元运算拆 if/else、异常控制流转换 | 需要语句级结构化变换能力 |
| 反射 | ~2 | BindingFlags → getDeclaredMethod | 专项能力缺失 |
| **真正的项目特化** | **~4** | 特定文件的字段初始化、包名限定 | 合理的特例 |

**关键发现**：60 个补丁中，约 **93%（~56 个）** 的根因可追溯到 Transformer 层或 TypeMapping 层的能力缺口。真正需要文件名特例处理的只有约 4 个。

这意味着改进 Transformer 和 TypeMapping 能力可以大幅降低补丁数量，而无需引入复杂的新架构。

## 4. 支撑超复杂项目所需的目标能力

### 4.1 解决方案建模能力

- 识别 `.sln` / `.slnx` / 单 `.csproj` / 目录入口
- 执行真实的 MSBuild 求值（`Directory.Build.props/targets`、条件属性、SDK 导入）
- 支持多目标框架切片
- 获取真实 `Compile`、`Resource`、`Analyzer`、`AdditionalFiles`、Source Generator 产物
- 构建项目引用图（区分编译引用、测试引用、运行时引用）

### 4.2 语义与规则能力

- 跨项目符号解析（引用程序集 + 源码项目统一建模）
- 基于 symbol 的规则匹配（不依赖文件名）
- 基于规范化 IR 的跨语句、跨方法、跨类型重写
- 规则注册表（推广表达式转换器的 `[TransformerRegistration]` 模式到全层级）
- 规则优先级、冲突消解和诊断输出

### 4.3 兼容与迁移能力

- 兼容层按能力包装配（按需生成，不全量注入）
- 输出 Java 依赖与源码改写联动
- 第三方 .NET 包到 Java 生态包的映射策略
- 对无法自动转换的能力给出明确降级与告警

### 4.4 工程化能力

- 增量转换（基于文件 hash 或项目依赖变化检测）
- 并行转换（项目级和/或类型级）
- 失败隔离（单个类型转换失败不阻塞整个项目）
- 可重复构建（相同输入 → 相同输出）
- 结构化诊断、审计日志、统计指标

## 5. 目标架构建议

### 5.1 目标分层

```
┌──────────────────────────────────────────────────────────┐
│  CLI / IDE / Batch Entry                                 │ ← 纯 orchestration
├──────────────────────────────────────────────────────────┤
│  Workspace Orchestrator                                  │ ← 执行计划管理
├──────────────────────────────────────────────────────────┤
│  MSBuild Project Evaluator                               │ ← 真实项目求值
├──────────────────────────────────────────────────────────┤
│  Semantic Compilation Service                            │ ← 语义模型管理
├──────────────────────────────────────────────────────────┤
│  Normalized C# IR  ←→  Rule Engine  ←→  Mapping Registry│ ← 规范化 + 规则
├──────────────────────────────────────────────────────────┤
│  Java IR / Output Planner                                │ ← 结构化 Java 表示
├──────────────────────────────────────────────────────────┤
│  Emitter / Formatter / Build Generator                   │ ← 输出生成
├──────────────────────────────────────────────────────────┤
│  Validator / Diagnostics / Reports                       │ ← 验证与可观测性
└──────────────────────────────────────────────────────────┘
   ↕                    ↕                    ↕
Compatibility       Mapping              Diagnostic
   Packs            Registry               Sink
```

### 5.2 各层详细设计

#### Layer 1: Workspace Orchestrator

**从现有代码演进**：提取 `Program.cs` 中的非 CLI 逻辑

**职责**：

- 接收入口参数，决定 solution/project/directory/file 模式
- 管理全量/增量执行计划
- 管理缓存目录、产物目录、失败重试策略
- 调度项目级或类型级并行
- 明确区分"发现项目""求值项目""转换项目""验证输出"四个阶段

**设计原则**：

- CLI 只做参数解析和结果展示
- 所有后处理逻辑移出 CLI，进入 Rule Engine
- `TryWriteConvertedFile` 中的 14 个文件名分支全部迁移

#### Layer 2: MSBuild Project Evaluator

**从现有代码演进**：升级 `ProjectDiscovery.cs`（236 行）

**产物模型**：

```
SolutionModel
  └─ ProjectModel[]
       ├─ TargetFrameworkSlice[]
       │    ├─ DocumentModel[] (实际参与编译的源文件)
       │    ├─ ReferenceModel[] (项目引用 + 程序集引用 + NuGet)
       │    └─ DefineConstants[]
       ├─ ResourceModel[]
       └─ ProjectKind (Production | Test | Tool)
```

**关键决策**：

- 使用 `Microsoft.Build.Locator` + `MSBuildWorkspace` 作为主路径
- 保留当前 XML 扫描逻辑作为 fallback，服务于无完整 MSBuild 环境的轻量场景
- Pipeline 接受 `ProjectModel` 而非目录路径

#### Layer 3: Semantic Compilation Service

**从现有代码演进**：提取 `ConversionContext` 中的语义功能 + `ConversionPipeline.BuildCompilation`

**职责**：

- 管理 Roslyn `Compilation` / `SemanticModel` 生命周期
- 按项目和 TFM 缓存 compilation（避免重复构建）
- 跨项目符号解析、继承图查询、引用查询
- 管理 generated source 和全局 using

**设计原则**：

- 语义服务是不可变平台能力（线程安全）
- 所有规则通过服务接口查询语义，不直接持有 `Compilation` 引用
- 缓存策略明确：项目级缓存跨类型共享，增量更新时只重建变化项目

#### Layer 4: Normalized C# IR

**核心动机**：Java AST 仅 682 行，无法承载结构化变换。在 Roslyn 和 Java 之间引入转换友好的中间表示。

**应先规范化的构造**（按消除补丁数量排序）：

| 构造 | 预期消除的补丁分类 | 受影响文件名特例数 |
|------|-------------------|------------------|
| String API 调用规范化 | String.Concat、大小写方法、null/empty 检查 | ~15 |
| 集合/Map 成员访问规范化 | .Count→.size()、.Keys→.keySet()、.Values→.values() | ~10 |
| LINQ/Stream 构造模式规范化 | stream().collect() 简化 | ~8 |
| 嵌套类型限定规范化 | Cell<T> → Parser.Cell<T> | ~5 |
| 事件/委托签名规范化 | Consumer→BiConsumer | ~4 |

**设计原则**：

- 不替换 Roslyn，在其之上建规范化层
- 第一版只做 "high ROI" 的规范化（上表前 5 项即可消除 ~42 个补丁）
- IR 节点保留到 Roslyn `SyntaxNode` 和 `ISymbol` 的引用，便于诊断追踪

转换流程变为：

`Roslyn Syntax/Semantics → Normalized C# IR → Java IR → Java Source`

#### Layer 5: Rule Engine

**从现有代码演进**：推广 `ExpressionTransformerRegistry` 的 attribute-based auto-discovery 模式到所有转换层

**规则元数据**：

```csharp
[ConversionRule(
    Id = "CS2J-STR-001",
    Phase = RulePhase.IRNormalization,   // IR规范化 | Java映射 | 输出修正
    AppliesTo = typeof(InvocationExpression),
    Priority = 100,
    Category = RuleCategory.StringApi,
    Pack = "dotnet-core-pack",
    Reversible = true
)]
```

**规则分类**：

1. **语义规范化规则**（作用于 C# IR）
2. **Java 映射规则**（C# IR → Java IR）
3. **输出修正规则**（作用于 Java IR 或最终输出——对应当前的后处理补丁）

**关键要求**：

- `ProjectConversionPipeline` 中的 60 个文件名特例必须逐一分析并迁入规则引擎
- 通用规则标记为 `Scope.Universal`
- 项目特化规则标记为 `Scope.ProjectSpecific` 并关联到具体项目配置
- 每条规则可追踪、可禁用、可测试、有诊断输出

#### Layer 6: Compatibility Pack System

**从现有代码演进**：将 `GenerateCompatibilitySupport` 的 33 个 helper 类按领域分包

**建议 Pack 划分**：

| Pack 名称 | 包含 Helper | 触发条件 |
|-----------|------------|---------|
| `dotnet-core-pack` | StringHelper, MathHelper, EnumHelper, ArrayHelper | 几乎所有项目 |
| `ref-holder-pack` | IntHolder, LongHolder, ..., ObjectHolder (10 个) | 检测到 ref/out 使用 |
| `io-pack` | FileHelper, StreamHelper | 检测到 System.IO 使用 |
| `xml-pack` | XmlReader, XmlWriter | 检测到 System.Xml 使用 |
| `json-pack` | JsonSerializer | 检测到 System.Text.Json 使用 |
| `test-pack` | MSTest → JUnit 适配 | 测试项目 |
| `threading-pack` | ThreadHelper, StopwatchHelper | 检测到 System.Threading 使用 |
| `regex-pack` | RegexCompat | 检测到 System.Text.RegularExpressions 使用 |

**Pack 声明结构**：

```csharp
public interface ICompatibilityPack {
    string PackId { get; }
    IReadOnlyList<string> GeneratedFiles { get; }     // 生成的 Java 源文件
    IReadOnlyList<string> RequiredDependencies { get; } // 需要的 Maven 依赖
    IReadOnlyList<string> AssociatedRules { get; }     // 配合的规则 ID
    bool IsApplicable(ProjectModel project);            // 按需判断是否启用
}
```

#### Layer 7: Output Planner 与 Build Generator

**从现有代码演进**：提取 `MultiModulePlanner`（182 行）+ `Program.cs` 中的 POM 生成逻辑

**产物模型**：

```
JavaWorkspacePlan
  └─ JavaModulePlan[]
       ├─ ModuleName
       ├─ SourceSets (main/test)
       ├─ JavaDependencyPlan[]
       │    ├─ InternalModuleRef[]
       │    └─ ExternalMavenDep[]
       ├─ RequiredCompatPacks[]
       └─ BuildFileTemplate (Maven | Gradle)
```

**重点**：先生成完整 plan，再统一 emit。不再在 CLI 中边推导边写文件。

#### Layer 8: Diagnostics / Observability

**结构化诊断**：

```csharp
public record ConversionDiagnostic(
    string DiagnosticId,        // "CS2J-STR-001"
    DiagnosticCategory Category, // StringApi | CollectionApi | Unsupported | ...
    DiagnosticSeverity Severity, // Info | Warning | Error
    string SourceSymbol,         // "System.String.Concat"
    SourceLocation Location,     // 源文件 + 行号
    string? TargetArtifact,     // 输出 Java 文件
    string? RuleId,             // 触发的规则
    string? SuggestedAction     // 建议修复
);
```

**报表能力**：

- 转换统计（成功/失败/跳过，按项目/按类型）
- 未转换能力清单（使用了哪些未映射的 .NET API）
- 兼容层使用报告（哪些 Pack 被启用，哪些 helper 被实际引用）
- 后处理审计（哪条规则修改了最终输出，规则命中率统计）
- 性能指标（各阶段耗时、内存峰值）

## 6. 对当前代码的具体改造建议

### 6.1 第一优先级：拆解 ProjectConversionPipeline

**这是最紧急、收益最高的一步。**

原因：4,211 行的 God Class 是所有后续改进的阻塞点。不拆解它，任何架构升级都会被迫绕过或叠加。

**建议拆分方案**：

```
ProjectConversionPipeline.cs (4,211行)
  ├─→ ProjectCompilationBuilder.cs      — 负责从源文件构建 CSharpCompilation
  ├─→ TypeGroupResolver.cs              — 负责 partial type 分组和类型调度
  ├─→ CompatibilityClassGenerator.cs    — 负责生成 33 个 helper 类
  ├─→ CrossPackageImportResolver.cs     — 负责跨包 wildcard import
  └─→ PostGenerationRewriteEngine.cs    — 负责所有文件名特例重写
       ├─→ StringApiRewriteRule.cs
       ├─→ CollectionApiRewriteRule.cs
       ├─→ XmlApiRewriteRule.cs
       └─→ ... (按类别组织)
```

同时，`Program.cs` 中 `TryWriteConvertedFile` 的 14 个文件名分支也应迁入 `PostGenerationRewriteEngine`。

**完成标准**：

- `ProjectConversionPipeline` 降到 500 行以内
- 所有文件名特例规则有独立的测试用例（当前 191 个测试中约 50 个已覆盖兼容重写）
- 新增文件名特例规则不需要修改 Pipeline 或 CLI

### 6.2 第二优先级：将通用补丁提升到 Transformer 层

基于 3.4 节的分析，约 56 个补丁的根因是 Transformer 层或 TypeMapping 层能力不足。

**优先修复方向**：

| 补丁类别 | 目标修复位置 | 预期消除补丁数 |
|---------|------------|-------------|
| String API（Concat、大小写、null/empty） | `StringExpressionTransformer` + `TypeMappings.json` 方法映射 | ~15 |
| 集合 API（Count/Keys/Values/Add/Put） | `InvocationExpressionTransformer` + Method Mappings | ~10 |
| Stream/LINQ 构造 | `LinqRewriter` + `QueryExpressionTransformer` | ~8 |
| 嵌套类型限定 | `ConversionContext.MapTypeInternal` 嵌套类型路径 | ~5 |
| 事件/委托签名 | `DelegateTransformer` Consumer 参数数量判断 | ~4 |

**完成标准**：

- 补丁数量从 60 降到 ≤ 10
- 剩余补丁全部标记为 `Scope.ProjectSpecific`

### 6.3 第三优先级：引入真实 Project/Solution 模型

**建议新增模块**：`CSharpToJava.Workspace`

```
CSharpToJava.Workspace/
  ├─ WorkspaceLoader.cs          — 统一入口（.sln/.slnx/.csproj/directory）
  ├─ SolutionGraphBuilder.cs     — 解析 solution 文件
  ├─ MSBuildProjectEvaluator.cs  — MSBuild 求值主路径
  ├─ ProjectSliceResolver.cs     — 多 TFM 切片解析
  ├─ FallbackProjectScanner.cs   — 降级为当前 XML 扫描逻辑
  └─ Models/
       ├─ SolutionModel.cs
       ├─ ProjectModel.cs
       ├─ TargetFrameworkSlice.cs
       ├─ DocumentModel.cs
       └─ ReferenceModel.cs
```

**关键决策**：

- 当前 `ProjectDiscovery` 保留为 `FallbackProjectScanner`
- `ConversionPipeline` 和 `ProjectConversionPipeline` 接受 `ProjectModel` 而非目录路径
- 依赖 `Microsoft.Build.Locator` + `Microsoft.CodeAnalysis.Workspaces.MSBuild`

### 6.4 第四优先级：拆分 ConversionContext

**建议拆为三类对象**：

**只读平台服务**（线程安全，项目生命周期）：

```csharp
TypeMappingService      — 提取 MapTypeInternal（~500行）为独立服务
SymbolQueryService      — 封装 INamedTypeSymbol 查询
ImportCollector         — import 收集（当前散落在 Context 中）
DiagnosticSink          — 结构化诊断收集器
```

**项目级会话**（项目生命周期，不跨项目共享）：

```csharp
ProjectConversionSession — TypeCache、PartialMerge 记录、合成 Record 注册
```

**局部状态**（文件/类型/方法生命周期）：

```csharp
FileConversionState      — Using Alias、当前 Namespace
TypeConversionState      — 当前 Type 栈、成员列表
MethodConversionState    — 当前 Method、PreStatements/PostStatements、Ref Holder、StreamLocalVariables
```

**核心原则**：

- 缓存属于服务层（不可变，可共享）
- 状态属于会话层（可变，项目隔离）
- 栈和临时变量属于局部状态（可变，方法隔离）

### 6.5 第五优先级：扩展 Java IR

当前 682 行的 Java AST 需要扩展以支持语句和表达式层面的结构化变换。

**最小可行扩展**：

```
JavaSyntaxNode (现有)
  ├─ JavaCompilationUnit (现有)
  ├─ JavaTypeDeclaration (现有)
  ├─ JavaMemberDeclaration (现有)
  ├─ JavaMethodBody (新增)          — 方法体容器
  ├─ JavaStatement (新增)           — 语句基类
  │    ├─ JavaBlockStatement
  │    ├─ JavaIfStatement
  │    ├─ JavaForEachStatement
  │    ├─ JavaReturnStatement
  │    ├─ JavaTryCatchStatement
  │    └─ JavaExpressionStatement
  └─ JavaExpression (新增)          — 表达式基类
       ├─ JavaMethodCall
       ├─ JavaMemberAccess
       ├─ JavaLiteral
       ├─ JavaLambda
       └─ JavaBinaryExpression
```

这会让转换器构建结构化 Java 树，后处理规则可在 Java IR 层面做结构化修改，而非字符串替换。

### 6.6 第六优先级：兼容层 Pack 化

将当前 `GenerateCompatibilitySupport` 拆为独立 Pack，每个 Pack 声明：

- 生成的 Java 源文件列表
- 依赖的 Maven 坐标
- 关联的转换规则 ID
- 适用条件判断函数

**完成标准**：

- 兼容类按需生成，不全量注入
- 新增兼容类不需修改 `ProjectConversionPipeline`
- 每个 Pack 可独立测试

## 7. 推荐实施路线

### 阶段 0：拆解 God Class 与治理补丁

**状态：已完成** ✅

**目标**：不改变转换行为，拆解 `ProjectConversionPipeline`（4,697 行），建立可治理的规则引擎雏形。

**交付结果**：

- `ProjectConversionPipeline` 从 4,697 行拆为 145 行（编排器）
- 提取 5 个独立类：
  - `PostGenerationRewriteEngine`（1,867 行）— 后处理规则引擎
  - `CompatibilityClassGenerator`（2,044 行）— 兼容类生成
  - `CrossPackageImportResolver`（173 行）— 跨包 import
  - `ProjectCompilationBuilder`（128 行）— Roslyn 编译构建
  - `TypeGroupResolver`（420 行）— 类型分组与合并
- 零测试回归（461 通过 / 15 预存失败）

**量化验收**：

| 指标 | 起始值 | 完成值 |
|------|-------|-------|
| `ProjectConversionPipeline.cs` 行数 | 4,697 | 145 |
| 测试回归 | 0 | 0 |

### 阶段 1：将通用补丁提升到 Transformer 层

**状态：进行中** — 已消除 35 个补丁 + 多项 Transformer 层增强，PostGenerationRewriteEngine 从 1,867 行变为 1,815 行。

**已完成的变更**：

1. **InvocationExpressionTransformer 增强**（Step 1.1 ~ 1.3）：
   - 添加 ToLower/ToUpper/ToLowerInvariant/ToUpperInvariant → toLowerCase/toUpperCase 到 well-known rename 表（3 处）
   - 添加 Float 到 TryParse switch（TryParse 现已覆盖 Double/Float/Single/Int32/Int64/Boolean）
   - 添加 IFormatProvider 首参数检测与剥离（`HasIFormatProviderFirstArg` 方法），覆盖 String.Format 和 ToString 调用
   - 添加全限定 Helper 方法安全网（methodName 含 `.` 时跳过 receiver 前缀），修复 `String.StringHelper.compare` 等 bug
   - 添加 `System.getenv` 到安全网模式列表
   - **Step 1.2**: 添加 `IsNullOrWhiteSpace` → `StringHelper.isNullOrWhiteSpace()`、`String.Concat` → `StringHelper.concat()`
   - **Step 1.2**: Parse 方法 IFormatProvider/NumberStyles 尾参数自动剥离（`maxArgCount` 机制）
   - **Step 1.3**: LINQ 未解析回退路径添加 `.collect(Collectors.toList())` 终端操作
   - **Step 1.3**: 防止链式未解析 LINQ 双重 `.stream()` 注入（`ReceiverLooksLikeStream` 检测）
   - **Step 1.3**: `Console.Error.Write/WriteLine` → `System.err.print/println` 未解析回退支持
   - **Step 1.3**: `System.Convert` 全系列方法映射（ToBoolean→parseBoolean, ToInt32→parseInt, ToString→valueOf 等）

2. **AssignmentTransformer 优化**（Step 1.1）：
   - `HoistChainedPropertyAssignment` 中 null 字面量跳过临时变量生成，消除 `var _chainValN = null` 模式

3. **ObjectCreationTransformer 修复**（Step 1.1 ~ 1.2）：
   - 修复 `new Exception()` 零参数路径绕过 RuntimeException 映射的 bug
   - **Step 1.2**: 扩展 `ApplicationException` → `RuntimeException` 映射

4. **ArgumentTransformer 增强**（Step 1.2）：
   - 添加 `maxArgCount` 参数用于 Parse 方法参数截断

5. **MethodTransformer 增强**（Step 1.1）：
   - 添加 ToLower/ToUpper/ToLowerInvariant/ToUpperInvariant 到声明站点重命名表

6. **TypeMappings.json 补充**（Step 1.1 ~ 1.2）：
   - 添加 `System.Environment.GetEnvironmentVariable` → `System.getenv` 方法映射
   - **Step 1.2**: 添加 `System.ApplicationException` → `RuntimeException` 类型映射
   - **Step 1.2**: 添加 `Exception.InnerException` → `getCause`、`Exception.StackTrace` → `getStackTrace` 方法映射

7. **PostGenerationRewriteEngine 规则提升**（Step 1.3）：
   - `StringBuilder.appendFormat()` 正则提升为通用规则（从 ShiftReduceParser 专属移出）
   - `IFormatProvider` 参数剥离提升为通用规则（从 ShiftReduceParser 专属移出）

8. **ConversionContext nullable 装箱修复**（Step 1.4）：
   - `MapTypeInternal()` 对 `Nullable<T>` 返回装箱类型（int→Integer, double→Double 等）
   - 消除 RectilinearVerifier 全部 41 个 nullable 字段/属性补丁

9. **StringComparison 无语义模型回退**（Step 1.5）：
   - `IsSystemStringMethod` 补充 `"string"` 小写别名匹配
   - 实例方法回退路径：当语义模型无法解析接收者类型时，检测最后参数包含 `StringComparison.` 并处理
   - 覆盖 StartsWith/EndsWith/Equals/Contains/IndexOf/LastIndexOf 全部签名
   - 消除 5 个 Replace + 1 个 Regex.Replace StringComparison 泄漏补丁

10. **Debug.Fail/Assert 及杂项修复**（Step 1.6）：
    - `InvocationExpressionTransformer`：Debug.Fail/Trace.Fail → `throw new RuntimeException(msg)`
    - `InvocationExpressionTransformer`：Debug.Assert/Trace.Assert/Contract.Assert → Java `assert` 关键字
    - `MapPrimitiveStaticMethodName`：默认分支应用 camelCase（修复 `Character.IsDigit` → `Character.isDigit`）
    - `MethodTransformer`：clone() catch 块直接使用 `Exception` 替代 `CloneNotSupportedException`
    - 消除 6 个 Replace + 1 个 Regex.Replace 补丁

**已消除的补丁**（42 个）：

| 类别 | 消除数 | 步骤 | 方法 |
|------|-------|------|------|
| TryParse 重定向 | 8 | 1.1 | Transformer 已覆盖全部类型 |
| camelCase 大小写转换 | 4 | 1.1 | well-known rename 表扩充 |
| CultureInfo/IFormatProvider 参数 | 6 | 1.1 | Transformer 层参数检测与剥离 |
| null 链式赋值临时变量 | 10 | 1.1 | AssignmentTransformer null 优化 |
| String.Join 大小写 | 1 | 1.1 | TypeMappings + camelCase 覆盖 |
| String.Empty 替换 | 1 | 1.1 | IdentifierExpressionTransformer 已处理 |
| Exception → RuntimeException | 1 | 1.1 | ObjectCreationTransformer 零参修复 |
| Helper 前缀冗余 | 1 | 1.1 | 全限定方法安全网 |
| Environment 映射 | 1 | 1.1 | TypeMappings 新增方法映射 |
| Convert.ToBoolean | 1 | 1.3 | InvocationExpressionTransformer Convert 映射 |
| Convert.ToString | 1 | 1.3 | InvocationExpressionTransformer Convert 映射 |
| Nullable 装箱类型 | 41 | 1.4 | ConversionContext.MapTypeInternal nullable 路径装箱 |
| StringComparison 参数泄漏 | 5 | 1.5 | InvocationExpressionTransformer 无语义模型回退 |
| startsWith/Compare StringComparison (Regex) | 1 | 1.5 | InvocationExpressionTransformer 无语义模型回退 |
| Debug.Fail → throw RuntimeException | 3 | 1.6 | InvocationExpressionTransformer Debug.Fail 处理 |
| Character.IsDigit camelCase | 1 | 1.6 | MapPrimitiveStaticMethodName 默认 camelCase |
| CloneNotSupportedException → Exception | 2 | 1.6 | MethodTransformer clone catch 块修复 |
| Debug.Assert → assert 关键字 | 0 | 1.6 | InvocationExpressionTransformer（正确性修复，无补丁消除） |

**量化进度**：

| 指标 | 阶段 0 结果 | 当前值 | 阶段 1 目标 |
|------|-----------|-------|-----------|
| PostGenerationRewriteEngine 行数 | 1,867 | 1,759 | — |
| code.Replace 补丁数 | 504 | 421 | ≤ 400 |
| Regex.Replace 补丁数 | 82 | 80 | — |
| 测试通过/失败 | 461/15 | 461/15 | 461/15 |

> **注**：Regex.Replace 实际数量为 82（之前文档误记为 16），原始目标"≤10"不适用。
> 多数 Regex.Replace 为文件特定模式，Transformer 层无法覆盖。
> 已消除补丁总计 83 个 Replace + 2 个 Regex = 85 个（504→421 Replace, 82→80 Regex）。

**剩余交付物**：

- `DelegateTransformer` 增强：Consumer/BiConsumer 签名自动判断
- Collectors import 问题修复（后处理阶段无法添加 import）
- 更多文件特例补丁泛化
- setter-as-expression 拆分（`return setX(expr)` → Phase 3 IR 层解决）

### 阶段 2：建立解决方案级工程模型

**状态：已完成** ✅

**目标**：让转换输入与真实编译输入一致。

**交付结果**：

- 新增 `CSharpToJava.Workspace` 模块，引入 `Microsoft.CodeAnalysis.Workspaces.MSBuild` 和 `Microsoft.Build.Locator`
- `SolutionLoader`：支持 .sln / .csproj / 目录三种入口，MSBuildWorkspace 驱动，自动拓扑排序项目依赖
- `WorkspaceProject`：封装 MSBuild 解析后的项目数据（CSharpCompilation / Documents / ProjectReferences / IsTestProject）
- `ProjectConversionPipeline`：新增接受 `CSharpCompilation` 的重载，跳过手动编译步骤，直接使用 MSBuild 提供的完整语义模型
- CLI：优先尝试 MSBuild 加载（`ConvertFromWorkspaceSingleModule` / `ConvertFromWorkspaceMultiModule`），失败时回退到 `ProjectDiscovery` 目录扫描
- CLI `--source` 选项现在支持 `.sln` 文件
- `ProjectDiscovery` 保留为 fallback（无 SDK 环境时使用）
- 测试基线不变：461 通过 / 15 失败

### 阶段 3：拆分 ConversionContext 并扩展 Java IR

**状态：进行中** — ConversionContext 从 1,149 行降至 221 行（目标 ≤300 已达成），已提取 8 个独立类。Stream API ToList 修复使测试从 461/15 提升到 466/8。

**目标**：建立干净的服务/会话/局部状态边界，扩展 Java IR 以减少字符串级修补。

**已完成的变更**：

1. **Step 3.1**: 提取 `MethodConversionState`（方法级可变状态）和 `UsingAliasRegistry`（文件级别名管理）
2. **Step 3.2**: 提取 `TypeMappingService`（~500 行类型映射核心逻辑）和 `JavaNaming`（静态命名工具）
3. **Step 3.3**: 提取 `ConversionOptions`、`DiagnosticCollector`、`SynthesizedRecordStore`，JavaNaming 扩展类型擦除方法
4. **Step 3.4**: 提取 `PartialTypeMergeStore`
5. **Step 3.5**: 精简 ConversionContext 至 221 行（移除冗余文档注释，折叠表达式体方法）
6. **Stream API 修复**: ToList 始终使用 `collect(Collectors.toList())`（语义匹配 C# 可变 List，修复 7 个测试）

**提取的类总览**：

| 新类 | 职责 | 步骤 |
|------|------|------|
| `MethodConversionState` | 方法级可变状态（pre/post 语句、ref holder、stream 变量） | 3.1 |
| `UsingAliasRegistry` | 文件级 using alias 注册/解析 | 3.1 |
| `TypeMappingService` | 全部类型映射逻辑、TypeCache、FlagsEnum 注册 | 3.2 |
| `JavaNaming` | IsJavaKeyword/EscapeJavaKeyword/HasTypeErasureConflict | 3.2-3.3 |
| `ConversionOptions` | 转换选项 + JavaVersion 枚举 | 3.3 |
| `DiagnosticCollector` | 诊断收集（Error/Warning/Info） | 3.3 |
| `SynthesizedRecordStore` | 匿名类型合成记录管理 | 3.3 |
| `PartialTypeMergeStore` | partial 类型合并跟踪 | 3.4 |

**交付物**：

- ✅ `ConversionContext` 拆为多个独立职责类（221 行，含向后兼容 facade）
- ✅ `MethodConversionState` 管理方法级状态
- ✅ `TypeMappingService` 管理全部类型/命名空间映射
- ✅ Java IR 扩展到支持 Statement 和 Expression 节点
- ⬜ 转换器输出结构化 Java IR，而非字符串
- ⬜ IR 层后处理替代字符串层后处理

**量化进度**：

| 指标 | 阶段 2 结果 | 当前值 | 目标值 |
|------|-----------|-------|-------|
| `ConversionContext.cs` 行数 | 1,149 | 221 | ≤ 300 ✅ |
| Java AST 行数 | 682 | 2,029 | ≥ 2,000 ✅ |
| Replace() 调用总数 | 501 | ~499 | ≤ 50 |
| 测试通过/失败 | 461/15 | 474/0 ✅ | 474/0 |

### 阶段 4：建立输出规划与兼容包体系

**目标**：让输出 Java 工程的结构、依赖和兼容层可预测、可配置。

**交付物**：

- `JavaWorkspacePlan` / `JavaModulePlan` 数据模型
- Maven/Gradle 生成器抽象
- 8 个 Compatibility Pack（dotnet-core/ref-holder/io/xml/json/test/threading/regex）
- 第三方依赖映射初版

**完成标准**：

- 兼容类按需生成
- 同一输入稳定生成一致输出

### 阶段 5：性能、增量与大仓库验证

**目标**：让架构可用于超复杂仓库。

**交付物**：

- Compilation 缓存（项目级、TFM 级）
- 增量转换（基于文件 hash）
- 并行执行（至少项目级并行）
- 大仓库基准测试
- 端到端验证报告

**完成标准**：

- 单项目变更后增量转换耗时 < 全量的 20%
- 有可观测的耗时和内存指标

## 8. 验收指标汇总

### 架构健康指标

| 指标 | 当前基线 | 阶段 0 结果 | 阶段 1 进度 | 阶段 3 进度 | 最终目标 |
|------|---------|-----------|-----------|-----------|---------|
| `ProjectConversionPipeline.cs` 行数 | 4,697 | 145 ✅ | 145 | 145 | ≤ 300 |
| PostGenerationRewriteEngine 补丁数 | 586 | 586 | 501（-85） | ~499 | ≤ 50 |
| `ConversionContext.cs` 行数 | 1,149 | 1,149 | 1,149 | 221 ✅ | ≤ 300 |
| Java AST 行数 | 682 | 682 | 682 | 2,029 ✅ | ≥ 2,000 |
| 回归测试通过/失败 | 461/15 | 461/15 | 461/15 | 474/0 ✅ | 全部通过 |

### 功能指标

- 支持 `.sln` / `.slnx` 入口
- 支持多项目、多层级引用
- 支持多目标框架切片
- 支持 Source Generator 产物参与转换
- 支持测试项目与生产项目分离输出
- 兼容层按 Pack 管理

### 质量指标

- 新增规则必须附带单元测试
- 所有后处理规则纳入统一引擎
- 对未转换能力输出结构化诊断
- 大仓库端到端 golden tests

### 性能指标

- 项目级并行转换
- 增量重跑
- 各阶段耗时和内存峰值可观测

## 9. 风险与取舍

### 1. 阶段 0 不改变转换行为

拆解 `ProjectConversionPipeline` 的目标是结构改善，不是行为变更。191 个测试全部通过是硬性要求。

### 2. 不一次性推倒 Visitor/Transformer

34 个转换器（15,062 行）是有效资产。应在其上方增加 IR 和规则引擎，而不是重写。表达式转换器的 auto-discovery 模式是推广方向。

### 3. IR 第一版只做高 ROI 的规范化

不设计完整的中间编译器。只做能消除最多补丁的规范化节点（String API → ~15 个补丁，集合 API → ~10 个补丁）。

### 4. 兼容层是务实方案

对于复杂 .NET 项目，兼容层是现实需要。治理目标是组织方式（按 Pack 声明 + 按需生成），不是消灭兼容层。

### 5. 必须阻止补丁扩散

阶段 0 完成后，建立硬性规则：**新增转换问题禁止通过在 Pipeline 或 CLI 中添加字符串替换修复**。如果现有 Transformer/TypeMapping 无法处理，必须先增强 Transformer 能力或添加到规则引擎。

### 6. MSBuild 求值引入外部依赖

`Microsoft.Build.Locator` + `MSBuildWorkspace` 要求运行环境安装了 .NET SDK。对于只需简单转换的场景，fallback 路径必须保留。

## 10. 最终建议

**核心判断**：当前系统最大的结构性风险不是"规则不够多"，而是 **一个 4,211 行的 God Class 承载了从编译到后处理的全部逻辑**。任何新的复杂文件转换出错，都会往这个文件追加字符串替换。如果不拆解这个瓶颈，后续所有架构升级都会被它阻塞。

**建议按如下优先顺序推进**：

1. **拆解 `ProjectConversionPipeline`**（4,211 行 → 5 个独立类），建立 `PostGenerationRewriteEngine`
2. **增强 Transformer 层**，将 ~56 个通用补丁提升到规则层解决
3. **引入真实 Solution/Project 求值能力**（新建 `CSharpToJava.Workspace` 模块）
4. **拆分 `ConversionContext`** 并 **扩展 Java IR**
5. **兼容层 Pack 化**
6. **增量、并行与大仓库验证**

完成前两步，系统就从"不可治理"变为"可治理"。完成全部六步，才具备承载"特别复杂 C# 项目转换 Java 项目"的架构基础。
