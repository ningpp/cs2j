# Phase 5 细化设计：rewrite-count 与 profile 作用域收敛

## 背景

上一批已经把 `cs2j-pass-profile.json` 落成，但还有两个缺口没有补上：

1. `Cs2jPassMetric` 仍缺少 `rewrite-count`，无法区分“这个 pass 花了时间但几乎没改东西”和“这个 pass 确实改写了大量节点”。
2. 项目级 pass metrics 当前会复制到每个 `ConversionResult` 上，若 profiling snapshot 继续按文件直接聚合，就会把同一轮项目级 pass 重复累计多次，导致总耗时、诊断增量和内存增量被放大。

这两个问题不解决，Phase 5 的观测数据就不够可信，也不适合作为后续缓存、增量、并行优化的基线。

## 本批目标

本批只做 Phase 5 的第二段：

1. 在 `Cs2jPassMetric` 中加入 `RewriteCount`。
2. 先给已经存在的 rewrite pass 接入该指标，至少覆盖 LINQ desugar 主线。
3. 把 profiling snapshot 的条目模型从“只按文件”收敛为“按执行条目”，显式区分 file / project 两类 entry。
4. 修正项目级 snapshot 聚合，确保同一轮项目 pass 只累计一次。
5. 用测试固定 rewrite-count 和 project-scope profiling 的语义。

## 本批非目标

这一次不做：

- 不实现增量缓存
- 不实现并行调度
- 不新增 canary 基线采集器
- 不把所有 middle-end 统计一次性铺满，只先补最关键的 rewrite-count

## 设计要点

### 1. pass 指标扩展

`Cs2jPassMetric` 新增：

- `RewriteCount`

约束：

1. 字段默认值必须为 `0`，保持现有调用点兼容。
2. 指标仍由 `Cs2jPassExecutor` 统一落盘，调用方只消费结构化结果。
3. pass 自身只负责暴露“本次执行实际改写了多少次”，不负责拼装 metric 对象。

### 2. rewrite-count 的最小接线范围

本批先覆盖已有明确 rewrite 语义的 pass：

- `SingleFileLinqDesugarPass`
- `ProjectLinqDesugarPass`

计数口径：

- 使用 `LinqRewriter.RewrittenLinqQueries`
- 一个成功改写的 LINQ chain 记作一次 rewrite
- skipped chain 不计入 rewrite-count

### 3. profiling snapshot 条目模型

上一批的 `PassProfileFileEntry` 只适合单文件路径，不适合项目级 pass。该模型本批调整为更一般的 entry：

- `EntryKind = file`：单文件转换，或按文件独立执行的主线
- `EntryKind = project`：项目级 pass 执行一次、产出多个结果的主线

条目至少包含：

- `entryKind`
- `moduleName`
- `fileName` / `projectName`
- `success`
- `passMetrics`

其中：

- `file` entry 继续保留文件名
- `project` entry 使用项目名或模块名作为聚合对象，不再伪装成多个 file entries

### 4. aggregate 口径

aggregate 不再使用只适用于文件路径的 `FileCount`，改为更中性的 `EntryCount`。

同时增加：

- `TotalRewriteCount`

这样无论条目来自 file 还是 project，聚合字段的含义都保持一致。

## 验证点

本批至少覆盖：

1. LINQ desugar pass 的 `RewriteCount` 大于 `0`（存在可改写链时）。
2. 未发生改写的 pass，其 `RewriteCount` 仍为 `0`。
3. snapshot aggregate 能汇总 `TotalRewriteCount`。
4. 项目级 profiling snapshot 不会因多个输出文件而重复累计同一轮 project pass metrics。

## 后续衔接

在这一批之后，Phase 5 的下一步优先级为：

1. 扩展更多 middle-end 指标，例如 post-generation rewrite 明细和 IR rewrite 规模
2. 把 profiling snapshot 接入 canary 回归基线
3. 在可信观测基线之上再推进缓存、增量和并行化