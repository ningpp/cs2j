# Phase 5 细化设计：输出级增量缓存

## 背景

当前项目级转换虽然已经具备：

- 显式 pass 主线
- per-pass profile / canary summary
- tree-local pass 并行化

但在重复执行时，CLI 仍然会大量执行以下低价值工作：

1. 删除现有输出目录。
2. 重新写入内容完全相同的 Java 文件。
3. 重新复制内容完全相同的资源文件。
4. 重新生成内容完全相同的 `pom.xml`、workspace manifest、profile 和 canary summary。

这还不能算真正的“增量与缓存”。即使上游 conversion 逻辑暂时仍全量执行，输出层至少也应该具备稳定的产物级缓存与陈旧文件清理能力。

## 本批目标

本批只做 Phase 5 的输出级增量缓存：

1. 为项目级转换新增 `cs2j-output-manifest.json`。
2. 对 generated source、copied resource、POM、workspace/profile/canary manifest 建立内容指纹。
3. 重复运行时若内容未变化，则跳过实际写盘/复制。
4. 依据上一轮 manifest 删除当前运行不再产生的陈旧文件。
5. 保持 single-module 和 multi-module 路径使用同一套输出缓存逻辑。

## 本批非目标

这一次不做：

- 不缓存 Roslyn compilation 或 descriptor
- 不做 pass 级失效传播
- 不让 conversion 主线按文件短路跳过
- 不引入跨 destination 的共享缓存目录

## 设计要点

### 1. 输出 manifest

`cs2j-output-manifest.json` 至少记录：

- `sourceName`
- `sourcePath`
- `entries[]`
  - `relativePath`
  - `kind`
  - `contentHash`

其中 `kind` 需要区分：

- `generated-source`
- `copied-resource`
- `build-file`
- `workspace-manifest`
- `pass-profile`
- `canary-summary`

### 2. 写盘策略

对每个目标文件：

1. 先算本轮内容 hash。
2. 若上一轮 manifest 中存在同路径同 hash，且目标文件仍存在，则跳过写盘。
3. 否则写盘并记录为本轮 entry。

### 3. 陈旧文件清理

在整轮转换结束后：

1. 对比上一轮与本轮 manifest 的 `relativePath` 集合。
2. 删除上一轮有、这一轮没有的文件。
3. 仅删除 destination 内由 manifest 管理的文件，不触碰未知外部文件。

### 4. force 与缓存的关系

启用输出缓存时，`--force` 不再等价于“先删整个目录”，而是等价于：

- 允许复用已有 destination
- 对需要变化的文件覆盖写入
- 对陈旧文件做定向删除

这样既保留原有“允许覆盖”的语义，又不会破坏增量收益。

## 验证点

本批至少覆盖：

1. manifest 能稳定序列化并回读。
2. 相同内容文件会被判定为可跳过写盘。
3. manifest 能识别并删除陈旧输出文件。
4. CLI 重复运行时不会因为开启缓存而丢失 profile / canary / POM 等辅助产物。

## 后续衔接

在这一批之后，Phase 5 的下一步优先级为：

1. 基于 source/project 指纹把输出级缓存向 conversion 主线前推
2. 建立 project graph 级失效传播
3. 把 canary 回归工作流接入 CI，并消费 output/profile/summary 三类 manifest