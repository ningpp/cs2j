# Phase 5 细化设计：输入指纹与全量 No-op 复用保护

## 背景

输出级增量缓存已经解决了“同样的内容不要重复写盘”，但它还没有解决一个更上游的问题：

- 当输入完全没有变化时，CLI 仍然会重新跑整轮项目转换。

这意味着当前缓存仍然主要发生在 `emit` 末端，而不是在进入整条转换主线之前就做 no-op 短路。

## 本批目标

本批只做一个保守但正确的增量前移能力：

1. 为项目级转换输出 `cs2j-input-fingerprints.json`。
2. 让指纹覆盖输入文件、mapping config 和当前工具程序集。
3. 在下一次执行前，若输入指纹与上一轮完全一致，且输出 manifest 仍存在，则直接复用既有输出并跳过整轮转换。

## 本批非目标

这一次不做：

- 不做 per-project 局部失效传播
- 不做 pass output cache
- 不做 descriptor / symbol index cache
- 不在部分项目变化时复用其余项目的中间结果

## 设计要点

### 1. 指纹内容

`cs2j-input-fingerprints.json` 至少包含：

- `sourceName`
- `sourcePath`
- `optionsHash`
- `entries[]`
  - `path`
  - `kind`
  - `contentHash`

其中 `kind` 包括：

- `source-input`
- `mapping-configuration`
- `tool-assembly`

### 2. no-op 复用条件

只有同时满足以下条件时，才允许直接跳过转换：

1. destination 下存在上一轮 `cs2j-input-fingerprints.json`
2. destination 下存在 `cs2j-output-manifest.json`
3. 当前构建出的输入指纹与上一轮完全一致

其中工具程序集也参与指纹，这样在转换器代码本身变化后，不会误复用旧输出。

### 3. 输入范围策略

本批不追求最小失效集合，而追求正确和稳定：

- MSBuild / project graph 路径按公共源码根枚举输入文件
- 跳过 `.git`、`.vs`、`bin`、`obj`、`node_modules` 等明显非输入目录
- 若 destination 位于源码树内，则显式排除 destination 本身

这样虽然比理想的 per-project 指纹更保守，但不会把输出目录反向纳入输入指纹。

## 验证点

本批至少覆盖：

1. 输入指纹快照可稳定序列化并回读。
2. options 变化会导致指纹不匹配。
3. 工具程序集变化会进入指纹项。
4. CLI 能在输入完全不变时直接复用既有输出。

## 后续衔接

在这一批之后，增量体系的下一步优先级为：

1. 把全量 no-op 复用前推为 per-project 指纹
2. 基于项目依赖图做失效传播
3. 缓存 descriptor、symbol index 与 pass outputs