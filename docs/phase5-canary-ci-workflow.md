# Phase 5 细化设计：持续 Canary 回归工作流

## 背景

当前仓库已经能在项目级转换后输出：

- `cs2j-pass-profile.json`
- `cs2j-canary-summary.json`
- `cs2j-output-manifest.json`

但这些产物仍然只在本地或手工执行时可见，尚未进入持续回归。按照总架构文档，MSAGL、Roslyn、.NET SDK 需要成为长期 canary，而不是一次性验证样例。

## 本批目标

本批只做持续回归接入：

1. 新增 GitHub Actions workflow，按 matrix 拉起 MSAGL、Roslyn、.NET SDK 三个 canary。
2. 统一执行 `convert-project`，保留多模块输出、POM、profile、canary summary 和 output manifest。
3. 把每次 canary 运行的输出目录上传为 artifact。
4. 在 workflow summary 中汇总成功数、失败数、diagnostics 和 pass aggregate 数量。

## 本批非目标

这一次不做：

- 不在 CI 中编译生成后的 Java 工程
- 不做 canary 基线自动 diff / 阈值门禁
- 不引入外部结果存储
- 不把 canary workflow 绑定到每一次 push

## 设计要点

### 1. 触发方式

工作流使用两种触发：

1. `workflow_dispatch`
2. 夜间 `schedule`

这样既支持手工回归，也能形成持续基线，而不会把所有常规提交都变成超重任务。

### 2. Canary 矩阵

矩阵固定包含：

1. `msagl`
2. `roslyn`
3. `dotnet-sdk`

每个条目包含：

- GitHub 仓库名
- 分支
- 候选入口路径列表
- 是否包含测试项目

工作流不把入口路径写死为单一值，而是在候选列表里顺序解析，避免上游仓库轻微布局变动就整体失效。

### 3. 产物要求

每个 canary job 至少保留：

- 转换输出目录
- `cs2j-workspace-plan.json`
- `cs2j-pass-profile.json`
- `cs2j-canary-summary.json`
- `cs2j-output-manifest.json`

这样后续无论是接阈值门禁，还是做历史趋势分析，都不需要重新设计 artifact 结构。

### 4. 失败策略

转换步骤允许失败后继续执行 artifact 上传与摘要汇总。最终 job 仍然失败，但不会丢失最关键的诊断证据。

## 验证点

本批至少覆盖：

1. 仓库新增 `.github/workflows` 后能识别 workflow。
2. workflow matrix 能为三个 canary 解析输入仓库和入口路径。
3. 即使转换失败，也仍会上传输出 artifact。
4. workflow summary 能读取 `cs2j-canary-summary.json` 并输出关键统计。

## 后续衔接

在这一批之后，持续回归链路上的下一步优先级为：

1. 对 canary summary / pass profile 建立阈值与基线 diff
2. 结合 source/project 指纹，只重跑受影响 canary 或受影响模块
3. 将 runtime bridge / compatibility pack 需求纳入 canary 回归验收项