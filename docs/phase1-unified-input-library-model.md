# Phase 1 细化方案：统一输入与 Library 模型

## 1. 目标

`Phase 1` 的目标不是立刻重写整个转换器，而是先把当前分裂的输入边界统一起来，为后续 `desugar / check / normalize / emit` 显式 Pass 化打基础。

本阶段要解决的核心问题有三个：

1. 单文件、目录项目、MSBuildWorkspace 项目目前不是同一种核心输入模型。
2. 当前项目级转换和单文件转换虽然都依赖 Roslyn，但缺少统一的批处理根对象来承接 compilation、documents、diagnostics 和后续规划。
3. 后续所有架构工作都需要一个稳定的“批次对象”作为生命周期边界，否则 Pass、缓存、并行、metrics 和 diagnostics 很难收敛。

## 2. 本阶段范围

本阶段只做“统一输入根模型”和“第一轮接线”，不做完整重构。

范围内：

- 定义 `Cs2jLibrary`、`Cs2jLibraryProject`、`Cs2jLibraryDocument`
- 为单文件、`IEnumerable<SourceFile>`、`CSharpCompilation` 三类输入提供统一工厂
- 让 `ConversionPipeline` 和 `ProjectConversionPipeline` 开始围绕 `Cs2jLibrary` 工作
- 固化第一版字段边界：documents、project compilation、project references、test 标记、library 名称
- 为后续 workspace/sln 入口预留适配点

范围外：

- 不在本阶段引入完整 `Cs2jLibrary` 多项目编排执行器
- 不在本阶段重写 CLI 全部入口
- 不在本阶段引入缓存、并行执行、增量失效传播
- 不在本阶段落地完整 Pass 框架

## 3. 设计原则

### 3.1 单文件是退化的 Library

单文件转换不再被视作完全独立的工作流，而是“只有一个 project、一个 document 的 `Cs2jLibrary`”。

### 3.2 项目级转换先统一到单一批处理根对象

当前 `ProjectConversionPipeline` 仍然以“一个 compilation 对应一次执行”为中心。这在 `Phase 1` 是允许的，但外部入口必须先统一到 `Cs2jLibrary`，后续再向多项目执行器演进。

### 3.3 Java-only，不引入多后端抽象

`Cs2jLibrary` 是输入和生命周期模型，不是多后端抽象层。它的职责是承接语义输入和执行上下文，而不是为未来不存在的后端预留复杂接口。

### 3.4 先把资源生命周期显式化

参考 J2CL 的 `Library.dispose()` 模式，`Cs2jLibrary` 从第一版开始就应具备显式释放入口，哪怕当前实现里还没有真实资源需要释放。

## 4. 数据模型

### 4.1 `Cs2jLibrary`

职责：表示一次转换批次的顶层对象。

建议字段：

- `Name`：library 名称，默认由文件名、项目目录名或 assembly 名推导
- `InputKind`：`SingleFile` / `SourceSet` / `Compilation`
- `Projects`：library 内项目集合
- `Documents`：批次内文档平铺视图
- `PrimaryCompilation`：当前第一版执行入口使用的主 compilation
- `Dispose()`：生命周期结束时的释放入口

### 4.2 `Cs2jLibraryProject`

职责：承接单个 project 级输入视图。

建议字段：

- `Name`
- `ProjectFilePath`
- `ProjectDirectory`
- `Compilation`
- `Documents`
- `ProjectReferences`
- `IsTestProject`

### 4.3 `Cs2jLibraryDocument`

职责：承接源文件级输入视图。

建议字段：

- `FilePath`
- `ProjectName`
- `Content`
- `SyntaxTree`

## 5. 输入归一化策略

### 5.1 单文件入口

输入：`ConversionRequest`

归一化后：

- 一个 `Cs2jLibrary`
- 一个 `Cs2jLibraryProject`
- 一个 `Cs2jLibraryDocument`
- 一个 `CSharpCompilation`

### 5.2 `IEnumerable<SourceFile>` 入口

输入：源码文件列表

归一化后：

- 一个 `Cs2jLibrary`
- 一个 synthetic `Cs2jLibraryProject`
- 多个 documents
- 一个 project-wide compilation

### 5.3 `CSharpCompilation` 入口

输入：预编译 compilation，例如来自 MSBuildWorkspace

归一化后：

- 一个 `Cs2jLibrary`
- 一个 project 视图
- 多个从 syntax trees 提取的 documents
- 保留 project references 和 test 标记的扩展位

## 6. 本阶段实际实现内容

本次实现建议落到以下几个点：

1. 新增 `Cs2jLibrary` 模型和工厂。
2. 让 `ProjectConversionPipeline` 新增 `ConvertLibraryAsync(Cs2jLibrary library, ...)` 作为统一内部入口。
3. 保持现有 `ConvertProjectAsync(IEnumerable<SourceFile>)` 和 `ConvertProjectAsync(CSharpCompilation)` 签名不变，但先统一为“先建 library，再执行”。
4. 让 `ConversionPipeline.Convert()` 在单文件路径上也开始构造 `Cs2jLibrary`，哪怕第一版只用于统一 compilation/document 根对象。

## 7. 代码触点

本阶段涉及的代码范围建议控制在以下文件：

- `src/CSharpToJava.Core/Pipeline/ConversionPipeline.cs`
- `src/CSharpToJava.Core/Pipeline/ProjectConversionPipeline.cs`
- `src/CSharpToJava.Core/Pipeline/ProjectCompilationBuilder.cs`
- 新增 `src/CSharpToJava.Core/Pipeline/Cs2jLibrary.cs`
- 新增基础测试文件

暂不改动 CLI 的调用方式，只在底层先完成输入归一化。

## 8. 验收标准

完成 `Phase 1` 第一轮落地后，应满足：

1. 单文件、`SourceFile` 列表、`CSharpCompilation` 三类入口都能构造 `Cs2jLibrary`。
2. `ProjectConversionPipeline` 至少有一个以 `Cs2jLibrary` 为参数的统一入口。
3. 新模型有基本测试，能验证 documents、compilation 和输入种类的归一化行为。
4. 现有外部调用面保持兼容，不要求 CLI 同步重写。

## 9. 后续衔接

`Phase 1` 完成后，下一步直接进入 `Phase 2`：把现有隐式逻辑逐步迁移为显式 `desugar / check / normalize / emit` Pass。

到那时，`Cs2jLibrary` 会成为：

- Pass 调度的输入根对象
- diagnostics 和 metrics 的归档边界
- compatibility pack 选择和模块规划的承载对象
- 后续缓存和增量机制的基础键空间