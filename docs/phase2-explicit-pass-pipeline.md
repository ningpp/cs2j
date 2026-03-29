# Phase 2 细化方案：显式 Pass 主线

## 1. 目标

`Phase 2` 的目标是把当前散落在 pipeline、visitor、rewriter 和后处理器中的隐式逻辑，收束为可排序、可计时、可测试的显式 Pass 主线。

本阶段不是一次性完成全部迁移，而是先建立一套真正运行中的 Pass 基础设施，并把当前最核心、最稳定的步骤接进去。

## 2. 本阶段要解决的问题

当前系统虽然已经具备单文件和项目级转换能力，但主线仍有几个明显缺口：

1. 关键步骤执行顺序主要依赖手写代码块，而不是统一的 Pass 合约。
2. 阶段边界不清晰，导致 diagnostics、耗时、后续缓存键空间都难以稳定归属。
3. `LINQ rewrite`、`partial merge`、compatibility helper 生成、跨包 import 处理、post-generation rewrite 仍是“能跑”，但还不是“显式可编排”。
4. 旧的 `ParsingPhase / TransformationPhase / CodeGenerationPhase` 只是占位接口，没有进入真实执行主线。

## 3. 本阶段范围

范围内：

- 定义统一的 Pass 抽象、阶段枚举和执行器
- 为单文件入口建立第一条显式 `desugar -> check -> normalize -> emit` Pass 链
- 为项目级入口建立第一条显式 `check -> normalize -> emit` Pass 链
- 为每个 Pass 记录执行耗时和 diagnostics 增量
- 把 Pass metrics 挂到 `ConversionResult`
- 删除旧的空壳 phase 占位实现，避免出现两套并行但只有一套真正生效的主线

范围外：

- 不在本阶段引入完整多项目 `Cs2jLibrary` 执行调度器
- 不在本阶段实现 `UnsupportedDomainPass` / `PlatformBoundaryPass`
- 不在本阶段重写全部 post-generation rewrite
- 不在本阶段引入缓存、增量、并行调度

## 4. 设计原则

### 4.1 Pass 必须是真正执行的主线，而不是新的占位层

如果 Pass 只是文档名词，仍由 pipeline 内联调度真实逻辑，那么架构并没有升级。

### 4.2 先迁移边界清晰、回归风险可控的步骤

第一轮优先迁移：

- 单文件 LINQ 降级
- 单文件 emit
- 项目级 partial merge
- 项目级类型 emit
- 项目级 compatibility helper 输出
- 项目级 import / post rewrite

### 4.3 指标必须随 Pass 同步落地

本阶段不要求 CLI 立刻展示全部指标，但框架内必须能稳定记录：

- Pass 名称
- Pass 阶段
- 执行耗时
- 执行前诊断数
- 执行后诊断数
- 诊断增量

## 5. Pass 模型

### 5.1 阶段枚举

首版固定四类阶段：

- `Desugar`
- `Check`
- `Normalize`
- `Emit`

### 5.2 Pass 接口

Pass 接口需要满足两个条件：

1. 对某一类状态对象执行一步确定性操作。
2. 不关心上层入口来自单文件还是项目，只关心自己收到的状态对象是否完整。

首版采用泛型接口：

- `ICs2jPass<TState>`

### 5.3 状态对象

为了避免把所有临时字段继续塞回 `ConversionContext`，`Phase 2` 第一轮引入两个执行态对象：

- `SingleFilePassState`
- `ProjectPassState`

它们承担“当前 compilation / syntax tree / library / emit 结果”等与一次 Pass 运行强相关的数据。

## 6. 第一轮实现边界

### 6.1 单文件 Pass 链

单文件入口在 `load/parse` 之后，接入以下 Pass：

1. `SingleFileLinqDesugarPass`
   - 迁移现有 LINQ rewrite 逻辑
   - 若 rewrite 失败，仅记录 warning，保持兼容行为

2. `SingleFileCompilationCheckPass`
   - 确保 library、compilation、semantic model 边界完整

3. `SingleFileContextNormalizationPass`
   - 统一刷新 `ProjectCompilation`、`SemanticModel`
   - 清理 imports / aliases / merged types / synthesized records 等上下文残留

4. `SingleFileJavaEmitPass`
   - 执行 visitor、IR rewriter 和最终 Java 文本输出

### 6.2 项目级 Pass 链

项目级入口在 `Cs2jLibrary` 归一化后，接入以下 Pass：

1. `ProjectLinqDesugarPass`
   - 在 `PreferStreamApi = false` 时，对项目内语法树逐个执行 LINQ rewrite
   - 保留现有 warning 兼容语义，不因单文件 rewrite 失败而中断整个项目

2. `ProjectCompilationCheckPass`
   - 检查 compilation 至少包含可处理的语法树

3. `ProjectPartialTypeNormalizationPass`
   - 迁移 partial merge 的分组逻辑

4. `ProjectTypeEmitPass`
   - 执行类型级 Java 输出

5. `ProjectCompatibilityEmitPass`
   - 迁移 compatibility helper 的集中输出

6. `ProjectCrossPackageImportEmitPass`
   - 迁移跨包 import 补全

7. `ProjectPostGenerationRewriteEmitPass`
   - 显式保留当前 post-generation rewrite 落点

## 7. 与后续阶段的衔接

这轮实现之后，`Phase 3` 可以直接沿着现成框架继续落：

- 在 `Check` 阶段增加 `UnsupportedDomainPass`
- 在 `Check` 阶段增加 `PlatformBoundaryPass`
- 在 `Normalize` 阶段继续前移通用 rewrite
- 在 `Emit` 阶段补模块布局和构建映射输出

换句话说，`Phase 2` 第一轮不是为未来画图，而是在当前代码上打出一条可持续扩展的执行骨架。

## 8. 验收标准

完成本轮后，应满足：

1. 单文件转换通过显式 Pass 执行，不再把 LINQ rewrite 和 emit 散落在 `Convert()` 中。
2. 项目级转换通过显式 Pass 执行，不再由一个大方法隐式串联 partial merge、emit 和 rewrite。
3. `ConversionResult` 能携带本次转换的 Pass metrics。
4. 旧的空壳 phase 占位实现被移除，仓库只保留一套真实执行主线。
5. 至少有基础测试验证 Pass 顺序与 metrics 已进入真实输出。

## 9. 下一轮建议

本轮结束后，下一步优先级建议如下：

1. 继续扩展项目级 `Desugar` Pass，把 `async/await` lowering 也纳入显式阶段。
2. 新增 `UnsupportedDomainPass` 和 `PlatformBoundaryPass`，把 UI / 原生互操作 / 平台 API 检查前移到 `Check`。
3. 开始从 `PostGenerationRewriteEngine` 抽离通用逻辑，向 `Normalize` 和 IR 层迁移。