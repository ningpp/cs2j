# Phase 5 细化设计：项目级 tree-local pass 并行化

## 背景

在当前项目级显式主线里，至少有一批 pass 天然按 `SyntaxTree` 独立工作：

- `ProjectLinqDesugarPass`
- `ProjectUnsupportedDomainCheckPass`
- `ProjectPlatformBoundaryCheckPass`
- `ProjectNativeInteropCheckPass`

这些 pass 的公共特点是：

1. 输入是不可变的 `Compilation` / `SyntaxTree` / `SemanticModel`。
2. 每棵树的分析或改写可以先在局部完成，再按固定顺序合并回主状态。
3. 它们不直接依赖 `partial` merge 结果，也不需要在并行阶段写共享 `ConversionContext.Diagnostics`。

这使它们成为 Phase 5 并行化最安全的第一段。

## 本批目标

本批只做项目级 tree-local pass 的并行执行能力：

1. 为项目级转换增加 `EnableParallelProjectPasses` 开关。
2. 让 LINQ desugar 与三类 boundary check 支持按 `SyntaxTree` 并行处理。
3. 并行阶段只生成局部结果，最终仍按原始 `SyntaxTree` 顺序顺序合并，保证确定性。
4. 用测试固定“并行开关打开后，结果与顺序执行语义一致”。

## 本批非目标

这一次不做：

- 不并行化 `partial` merge
- 不并行化 type emit
- 不并行化 compatibility / import / post-generation rewrite
- 不引入跨运行缓存或持久化增量索引

## 设计要点

### 1. 并行边界

仅对 tree-local pass 启用并行：

- 并行阶段：每棵语法树独立计算 rewritten tree 或 diagnostics
- 合并阶段：按原始 tree 顺序统一写回 `Compilation` / `Diagnostics` / `BlockedFilePaths`

这样可以同时满足：

- 利用多核并发
- 保持输出顺序稳定
- 避免共享状态竞争

### 2. 确定性约束

必须保证以下内容不受线程调度影响：

1. rewritten tree 在新 `Compilation` 中的顺序
2. `DiagnosticCollector` 的追加顺序
3. `RecordBlockingDiagnostics(...)` 的文件级结果顺序
4. `RewriteCount` 的累计值

因此实现策略必须是：

- 先把每个 tree 的结果写进与输入顺序一一对应的数组
- 再在单线程阶段按索引顺序合并

### 3. 开关策略

新增 `ConversionOptions.EnableParallelProjectPasses`。

CLI 项目转换默认开启，并提供关闭选项，以便出现回归时快速退回顺序路径。

## 验证点

本批至少覆盖：

1. 顺序模式与并行模式的 `GeneratedCode` 一致。
2. 顺序模式与并行模式的 diagnostics code/category 顺序一致。
3. 并行模式下 `ProjectLinqDesugarPass` 的 `RewriteCount` 仍正确累计。

## 后续衔接

在这一批之后，Phase 5 的下一步优先级为：

1. 继续把并行边界向 type group emit 扩展
2. 为并行执行引入更细的 per-pass worker 指标
3. 在稳定并行边界之上再做缓存与失效传播