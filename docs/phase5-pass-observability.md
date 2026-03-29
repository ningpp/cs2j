# Phase 5 细化设计：Pass 可观测性第一批

## 背景

当前显式 Pass 主线已经能输出 `Cs2jPassMetric`，但信息仍然偏少：

- 有耗时
- 有诊断计数前后值
- 没有内存快照
- 没有办法快速判断某个 pass 是否引入了异常的托管内存增长

在进入缓存、增量、并行之前，先把基础观测面补齐更稳妥。否则后续就算做了性能优化，也缺少统一的量化基线。

## 本批目标

本批只做 Phase 5 的第一段：

1. 为每个 pass 记录执行前后的托管内存快照。
2. 把内存增量与已有耗时、诊断增量并列放进 `Cs2jPassMetric`。
3. 用测试固定这些字段的存在与基本语义。

## 本批非目标

这一次不做：

- 不实现增量缓存
- 不实现并行调度
- 不统计 rewrite 次数
- 不接入 CLI 或文件级 profiling 报表

这些内容留给 Phase 5 后续子批次处理。

## 指标定义

新增字段：

- `ManagedMemoryBytesBefore`
- `ManagedMemoryBytesAfter`
- `ManagedMemoryDelta`

采集方式：

- 在 pass 执行前使用 `GC.GetTotalMemory(false)` 记录一次
- 在 pass 执行后再次记录
- 不主动触发 GC，避免把观测本身变成额外扰动

## 设计约束

1. 指标采集必须保留在 `Cs2jPassExecutor` 内部统一完成，不能让每个 pass 自己重复采样。
2. 即使 pass 抛异常，也必须记录耗时和内存快照，保持指标结构完整。
3. 对调用方保持兼容：`ConversionResult.PassMetrics` 的消费方不需要额外改动即可拿到新字段。

## 验证点

本批至少覆盖：

1. `Cs2jPassExecutor` 仍按顺序记录 metrics。
2. 每条 metric 都有非负的内存前后值。
3. `ManagedMemoryDelta` 等于 after - before。

## 后续衔接

在这一批之后，Phase 5 的下一步优先级为：

1. 给 rewrite pass 增加 rewrite-count 指标
2. 输出可落盘的 profiling snapshot
3. 评估缓存和并行化需要的最小侵入改造