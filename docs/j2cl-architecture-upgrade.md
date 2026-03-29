# 吸收 J2CL 管线经验的 Java-only 大型 C# 解决方案转译架构升级方案

## 1. 文档定位

本文定义 `cs2j` 的下一阶段架构方向：以 Java 为唯一输出语言，以大型、复杂、全语义 C# 解决方案转换为首要目标，并吸收 J2CL 在批处理模型、显式 Pass、限制前置检查上的有效经验。

J2CL 在这里是架构参考源，不是产品目标。本方案不追求 Closure、Wasm、Kotlin 等多目标后端，也不把 Web 平台约束作为核心设计前提。

## 2. 项目目标与范围

### 2.1 核心目标

1. 唯一目标语言是 Java。
2. 首要目标是稳定转换复杂、巨大的 C# 项目，例如 MSAGL、Roslyn、.NET SDK 的非 UI、非原生互操作部分。
3. 转换必须建立在 Roslyn 语义分析之上，支持解决方案级、多项目、多模块、跨文件、跨程序集语义解析。
4. 架构设计要优先服务“大项目可转换、可回归、可扩展、可观测”，而不是优先服务多平台扩展。

### 2.2 范围内

- `.sln` / `.csproj` 级加载与项目依赖图分析
- `partial` type 合并、跨项目符号解析、多模块规划
- LINQ、`async/await`、delegates、lambda、records、异常流、泛型、nullability 等核心 C# 语义
- Java 源码输出、包结构、模块布局、compatibility packs、runtime bridge
- 大项目场景下的增量、缓存、并行、内存治理、符号索引、构建映射

### 2.3 范围外

- WinForms、WPF、MAUI、XAML 等 UI 框架
- Win32、COM、PInvoke 等原生互操作
- 非 Java 输出
- 为所有 .NET 平台特定 API 提供兼容实现
- 对源项目业务逻辑错误或线程安全问题进行自动修复

## 3. 从 J2CL 借鉴什么，不借鉴什么

### 3.1 借鉴什么

J2CL 对本项目最有价值的不是“多后端”，而是编译器架构纪律。

1. 借鉴批处理根对象。
   J2CL 使用 `Library` 作为一批 compilation units 的统一载体。`cs2j` 也应引入同类的批处理根对象，把单文件、项目、解决方案三类入口统一到同一转换模型中。

2. 借鉴显式主线。
   J2CL 的主线是 `parse -> desugar -> check -> normalize -> generate`。对于 `cs2j`，应该固定成 `load/parse -> desugar -> check -> normalize -> emit Java`。

3. 借鉴 Pass 体系。
   J2CL 的 Pass 是一致、可排序、可测试的执行单元。`cs2j` 当前很多逻辑还散落在 visitor、rewriter 和后处理器中，后续应收束为显式 Pass。

4. 借鉴限制前置。
   J2CL 会明确写出 limitations，并且把不兼容能力前置处理。`cs2j` 也应明确 unsupported-domain，并在 `check` 阶段给出诊断，而不是把问题拖到 `emit` 后或运行期。

5. 借鉴边界隔离。
   J2CL 的 best practices 强调不兼容能力应通过边界隔离或替代实现处理，而不是运行时 stub。`cs2j` 对平台特定代码也应采取同样原则。

### 3.2 不借鉴什么

1. 不引入多后端架构。
   本项目不设计 Closure、Wasm、Kotlin 等目标路径。

2. 不复制 JsInterop 和 Web 生态。
   本项目目标不是浏览器运行时，而是稳定生成 Java 源码。

3. 不复制 Bazel 绑定。
   本项目可以借鉴构建分层思想，但不需要照搬 J2CL 的构建工具链。

4. 不以 Web 语义裁剪为默认前提。
   `cs2j` 的限制边界来自“Java-only + 大项目可落地”，不是来自浏览器平台。

## 4. 当前架构基线

### 4.1 单文件转换基线

当前单文件转换由 [ConversionPipeline](../src/CSharpToJava.Core/Pipeline/ConversionPipeline.cs) 驱动，已经具备以下基础能力：

- 基于 Roslyn 构建 `SyntaxTree`、`Compilation` 和 `SemanticModel`
- 可选执行 LINQ 预处理
- 通过 [CSharpToJavaVisitor](../src/CSharpToJava.Core/Visitors/CSharpToJavaVisitor.cs) 生成结构化 Java IR
- 通过 [JavaSyntaxRewriter](../src/CSharpToJava.Core/Java/JavaSyntaxRewriter.cs) 在 IR 层做重写
- 最终生成 Java 源码

这说明系统已经不是简单的文本替换器，而是一个语义驱动的转译器。

### 4.2 项目级转换基线

当前项目级转换由 [ProjectConversionPipeline](../src/CSharpToJava.Core/Pipeline/ProjectConversionPipeline.cs) 驱动，已经具备：

- 完整 compilation 上的批量转换
- `partial` type merge
- compatibility helper 生成
- 跨包 `import` 处理
- post-generation compatibility rewrites

工作区和解决方案加载由 [SolutionLoader](../src/CSharpToJava.Workspace/SolutionLoader.cs) 提供，多模块规划由 [MultiModulePlanner](../src/CSharpToJava.CLI/MultiModulePlanner.cs) 提供。

### 4.3 当前基线的主要问题

1. 缺少 Library 级批处理根对象。
   单文件、项目、解决方案三条入口还没有被统一到同一核心模型里。

2. Pass 仍不够显式。
   很多转换逻辑隐含在 visitor、helper 生成器和 rewrite 中，执行顺序和依赖关系不够透明。

3. 缺少 unsupported-domain 检查。
   平台边界、原生互操作、UI 框架、反射重度依赖等问题没有被明确前置诊断。

4. 后处理负担过重。
   `PostGenerationRewriteEngine` 说明仍有相当一部分问题没有在语义层或 IR 层根治。

5. 大项目能力还不是一等设计对象。
   增量、缓存、并行、内存和符号索引仍不是核心架构的一部分。

## 5. 面向 Java-only 大项目的目标架构

### 5.1 目标主线

目标流水线固定为：

1. `load/parse`
   统一加载文件、项目、解决方案，并构建完整语义视图。

2. `desugar`
   把不适合直接映射到 Java 的 C# 语义先降级。

3. `check`
   检查 unsupported-domain、平台边界、映射缺口和不可接受的 fallback。

4. `normalize`
   在 Java-only 目标下统一类型、命名、异常、nullability、compatibility packs 和模块输出决策。

5. `emit`
   生成 Java IR、Java 源码、包结构、模块布局和构建映射。

这条主线直接吸收 J2CL 的 `parse -> desugar -> check -> normalize -> generate` 纪律，但最后一步明确是 `emit Java`。

### 5.2 Library 级批处理模型

引入 `Cs2jLibrary` 作为整个批次的根对象。它表示“同一次转换中需要被一起分析、一起规范化、一起输出的一批 compilation units 和项目”。

`Cs2jLibrary` 至少应包含：

- 项目图和模块图
- 每个项目的 `Compilation`、`Document` 集合和源文件指纹
- 类型、成员、命名空间描述符索引
- 跨项目符号引用索引
- diagnostics 集合
- compatibility pack 与 runtime bridge 需求清单
- 输出计划和资源释放接口

单文件转换只是 `Cs2jLibrary` 的退化形式，而不是独立的核心架构。

### 5.3 Descriptor 与符号索引层

在 Java IR 之前增加稳定的 descriptor 与 symbol inventory 层，用来表达：

- `TypeDescriptor`
- `MemberDescriptor`
- `NamespaceDescriptor`
- `ProjectDescriptor`
- `ModuleDescriptor`
- `RuntimeRequirement`

这层的职责不是生成 Java 语法，而是稳定表达“语义是什么、依赖什么、需要哪些兼容层、归属哪个模块”。

### 5.4 Java-only IR 策略

本项目不应该为未来不存在的后端引入过度抽象的通用 AST。更合理的分层是：

1. Descriptor 层
   负责稳定表达语义和依赖。

2. Normalized Library 层
   负责在 Pass 之间传递已经降级、已经检查过的规范化状态。

3. Java IR
   仍作为唯一目标 IR，用于结构化生成 Java 源码。

因此，Java IR 不会被废弃，但会退到“唯一目标 IR”的位置，而不是在它上面继续承载所有上游语义决策。

### 5.5 显式 Pass 体系

Pass 是后续架构的核心执行单元。建议分为四类：

#### Desugar Passes

- LINQ desugar
- `async/await` lowering
- iterator / `yield` lowering
- `record` / property / event lowering
- query / pattern / `using` 语义消解

#### Check Passes

- `UnsupportedDomainPass`
- `PlatformBoundaryPass`
- `NativeInteropPass`
- `ReflectionDynamicPass`
- `MappingCoveragePass`
- `DependencyAuditPass`

#### Normalize Passes

- `TypeMappingPass`
- `NullabilityPass`
- `DelegateLambdaPass`
- `ExceptionNormalizationPass`
- `PackageImportPass`
- `CompatibilitySelectionPass`
- `PartialTypeNormalizationPass`

#### Emit Passes

- `JavaIrLoweringPass`
- `JavaSourceEmitPass`
- `ModuleLayoutEmitPass`
- `BuildMappingEmitPass`

### 5.6 Unsupported-domain 与平台边界检查

`check` 阶段必须明确识别并拒绝以下内容：

- WinForms、WPF、MAUI、XAML
- Win32、COM、PInvoke 等原生互操作
- 依赖窗口系统、消息循环、注册表、设备能力、桌面生命周期的代码
- 无可靠 Java 语义映射的动态与反射重度模式
- 当前 compatibility packs 和 runtime bridge 无法覆盖的 API 族

原则是：

1. 能隔离的边界，隔离。
2. 能替代的能力，替代。
3. 不能可靠转换的代码，直接报错。

### 5.7 Compatibility packs、type mapping 与 runtime bridge

Java-only 架构里的兼容层由三部分组成：

1. Type Mapping
   把 .NET 类型、方法和命名空间映射到 Java 世界。当前基础在 [TypeMappingRegistry](../src/CSharpToJava.TypeMapping/TypeMappingRegistry.cs)。

2. Compatibility Packs
   把常见语义差异收敛为模块级可复用的 Java 支撑代码，而不是分散生成在每个文件里。

3. Runtime Bridge
   处理无法仅靠重命名解决的语义差异，例如集合行为、delegates、tasks、异常、基础库契约差异等。

这三部分应在 Library 级统一决策，而不是在文件末端补丁式注入。

### 5.8 输出与构建映射

`emit` 阶段输出的不只是 Java 文件，还包括：

- Java 源文件
- 包和目录布局
- 模块依赖与测试源码归属
- compatibility packs / runtime bridge 输出
- Maven / Gradle 构建映射建议

当前 [MultiModulePlanner](../src/CSharpToJava.CLI/MultiModulePlanner.cs) 已有模块规划雏形，后续应与 `emit` 阶段统一。

## 6. 大型项目专项设计

### 6.1 增量与缓存

- 对 solution、project、document 建立稳定指纹
- 缓存 descriptor、symbol index、pass outputs 和 emitted files
- 按项目依赖图做失效传播，而不是全量重跑
- 单文件变化只重跑受影响的 `desugar / check / normalize / emit` 子集

### 6.2 并行与确定性

- 按项目、模块或 type group 并行执行安全的 Pass
- 共享只读 symbol index，避免并发状态污染
- 结果合并必须稳定，不能因为线程调度改变文件顺序或 diagnostics 排序
- 单文件模式和大项目模式使用同一套 Pass 实现

### 6.3 内存治理与符号索引

- `Cs2jLibrary` 负责明确的资源释放时机
- 建立 declaration inventory 和 cross-project symbol index，减少重复扫描
- Pass 只持有必要 snapshot，避免把整棵语法树长期挂在上下文里
- 对超大 solution 支持分模块输出和受控缓存上限

### 6.4 依赖与构建映射

- 把 `.sln` / `.csproj` 图映射为 Java 包、模块和构建依赖
- 保留 production / test project 的边界
- 对 NuGet / BCL 依赖建立 Java 侧映射或 unsupported diagnostics
- 输出可理解的模块边界，而不是扁平文件堆

### 6.5 Canary 项目

以下项目应作为持续回归对象：

1. MSAGL
   验证图算法、跨包引用、compatibility packs 和多模块输出。

2. Roslyn
   验证超大 solution、深层语义分析、visitor / descriptor 压力和增量表现。

3. .NET SDK
   验证构建图、工具链代码、依赖映射和 platform boundary diagnostics。

这些 canary 不是示例，而是架构验收基线。

## 7. 分阶段实施路线

### Phase 1：统一输入与 Library 模型

主要工作：

- 引入 `Cs2jLibrary`
- 统一单文件、项目、解决方案入口
- 固化 project graph、document inventory、symbol inventory、diagnostics container

完成标志：

- 所有入口都能构造同一种批处理根对象
- 单文件转换不再绕开项目级核心模型

### Phase 2：显式化 `desugar / check / normalize / emit`

主要工作：

- 把 LINQ rewrite、async lowering、partial merge 后规范化、compatibility selection、import 处理逐步迁移为 Pass
- 明确 Pass 顺序、依赖和 diagnostics 契约
- 逐步前移通用 post-generation rewrites

完成标志：

- 核心转换逻辑不再依赖隐式 side effects
- 每个阶段都能输出独立 metrics 和 diagnostics

### Phase 3：平台边界与 unsupported-domain 检查

主要工作：

- 新增 `UnsupportedDomainPass`、`PlatformBoundaryPass`、`NativeInteropPass`
- 明确 UI、原生互操作、平台特定 API 的拒绝策略
- 建立可解释的 diagnostics 分类

完成标志：

- 不支持域在 `emit` 前即可稳定报出
- canary 项目中的平台边界问题可以被批量识别

### Phase 4：compatibility packs、runtime bridge、构建映射

主要工作：

- 收敛 helper 生成策略
- 形成模块级 compatibility packs
- 建立 runtime bridge 规范
- 输出 Maven / Gradle 级别的构建映射信息

完成标志：

- compatibility 支撑代码不再零散生成
- 大项目输出具备可理解的模块和依赖关系

### Phase 5：大项目性能与可观测性

主要工作：

- 引入缓存、增量、并行执行
- 加入 per-pass timing、memory、rewrite-count 指标
- 把 MSAGL、Roslyn、.NET SDK 纳入持续回归

完成标志：

- canary 项目在时间、内存、成功率上形成稳定基线
- 后处理修补规模持续下降

## 8. 验证与验收标准

### 8.1 验证层次

1. 文件级 golden tests
   验证单文件输入到 Java 输出的稳定性。

2. 项目级回归测试
   验证 `partial` merge、跨项目引用、多模块输出、compatibility packs。

3. Library / Pass 测试
   验证 `desugar`、`check`、`normalize` 的顺序、幂等性和 diagnostics。

4. Unsupported-domain 测试
   验证 UI、原生互操作、平台边界在 `check` 阶段稳定报错。

5. Canary 回归
   在 MSAGL、Roslyn、.NET SDK 上验证成功率、耗时、内存、rewrite 规模。

### 8.2 核心验收指标

- 项目级转换成功率持续上升
- unresolved diagnostics 比例下降
- post-generation rewrite 命中数持续下降
- Raw 节点或字符串逃逸使用率下降
- canary 项目的时间与内存基线可重复
- 输出的模块布局和依赖图稳定且可解释

## 9. 非目标

以下内容不是本轮架构升级的交付目标：

1. WinForms、WPF、MAUI、XAML 等 UI 框架支持
2. Win32、COM、PInvoke、unsafe/native interop 的语义映射
3. Java 之外的任何输出目标
4. 一次性覆盖全部 .NET API
5. 自动修复源项目中的业务逻辑或线程并发问题
6. 复制 J2CL 的生态集成与构建系统

## 10. 结论

`cs2j` 下一阶段最重要的不是“更多目标平台”，而是“更强的 Java-only 工业级转译能力”。

因此，架构升级应聚焦于四件事：

1. 用 `Cs2jLibrary` 统一批处理根模型
2. 用显式 `desugar -> check -> normalize -> emit` Pass 主线替代隐式逻辑
3. 把 unsupported-domain 和平台边界检查前置
4. 把 compatibility packs、runtime bridge、构建映射和大项目能力做成一等架构对象

做到这几点之后，`cs2j` 才更有可能稳定承载 MSAGL、Roslyn、.NET SDK 这类复杂 C# 代码库的 Java 转换，而不是停留在“能处理一批中小型样例”的阶段。