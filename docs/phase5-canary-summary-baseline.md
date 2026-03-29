# Phase 5 细化设计：canary 回归摘要基线

## 背景

当前 `convert-project` 已经能输出：

- `cs2j-workspace-plan.json`
- `cs2j-pass-profile.json`

但如果要把 MSAGL、Roslyn、.NET SDK 真正纳入持续回归，仅有这些底层文件还不够。原因是：

1. `workspace-plan` 更偏构建映射，不直接回答“这次转换成了多少、失败了多少、主要卡在哪类 diagnostics”。
2. `pass-profile` 更偏中间层 profiling，不直接给出 canary 视角下的成功率、诊断类别分布和总体 rewrite 规模。
3. 外部 CI 如果每次都要自己拼接这些原始文件，脚本成本高，而且很容易在聚合口径上再次跑偏。

因此需要一个更高层、一次运行一份的 `canary summary`，把回归基线真正做成一等输出。

## 本批目标

本批只做 canary 基线的最小闭环：

1. 为项目级转换新增 `cs2j-canary-summary.json`。
2. 摘要中至少包含成功数、失败数、诊断严重度分布、诊断 category/code 分布。
3. 复用已有 `pass-profile` aggregate，把耗时、内存、rewrite-count 直接纳入 canary 摘要。
4. 单模块和多模块路径都写出同一种摘要结构。
5. 用测试固定摘要聚合语义。

## 本批非目标

这一次不做：

- 不直接接入 GitHub Actions
- 不要求仓库内置 MSAGL / Roslyn / .NET SDK 源码副本
- 不做跨运行 diff 或阈值告警
- 不把外部资源复制量、POM 细节、全部 workspace 元数据都塞进摘要

## 摘要结构

`cs2j-canary-summary.json` 至少包含：

- `sourceName`
- `sourcePath`
- `moduleCount`
- `resultCount`
- `successCount`
- `failureCount`
- `diagnosticSeverities`
- `diagnosticCategories`
- `diagnosticCodes`
- `passAggregates`

说明：

1. `passAggregates` 直接复用 `PassProfileSnapshot.Aggregates`，避免重复定义 timing / memory / rewrite-count 口径。
2. `moduleCount` 反映本次转换涉及的业务模块数；单模块路径固定为 `1`，多模块路径从 profile entry 的模块名集合推导。
3. `diagnosticCategories` 和 `diagnosticCodes` 用于快速回答“当前 canary 主要被哪类平台边界/interop/unsupported-domain 阻塞”。

## 设计约束

1. 摘要必须建立在现有转换结果和 profile snapshot 之上，不能额外重复执行一遍转换。
2. 聚合逻辑要放在可测试的 builder/serializer 中，而不是散落在 CLI 字符串拼接里。
3. 即使某次运行没有 profile entries，也应能生成只包含成功率和 diagnostics 的摘要。

## 验证点

本批至少覆盖：

1. 摘要能统计 success / failure / result 总数。
2. 摘要能按 severity/category/code 聚合 diagnostics。
3. 摘要能携带 `pass-profile` 的 aggregate 结果。
4. 多模块情况下 `moduleCount` 取自 profile entry 的模块集合，而不是输出 helper 模块或文件数。

## 后续衔接

在这一批之后，Phase 5 的下一步优先级为：

1. 把 canary summary 接入 CI 与基线比较
2. 在摘要层增加资源复制量、compat pack 使用量等更高层指标
3. 基于已有 summary/profile 基线再推进缓存、增量和并行化