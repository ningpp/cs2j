# Phase 4 细化设计：显式 Runtime Bridge 需求清单

## 背景

当前 Phase 4 已经完成：

- compatibility pack 需求分析
- shared compatibility module 输出
- Maven 构建映射与 workspace manifest

但 runtime bridge 仍然只是隐含在 compatibility pack 描述和 helper 生成逻辑里，没有成为显式规划对象。这会导致输出清单里只能看到 `requiredCompatPacks`，看不到“这些 pack 实际在桥接什么运行时语义”。

## 本批目标

本批只做 runtime bridge 的显式化：

1. 在模块规划中新增 `requiredRuntimeBridges`。
2. 在工作区规划中聚合出顶层 `requiredRuntimeBridges`。
3. 让 compatibility pack 分析同时输出 bridge id、描述和依赖信息。
4. 让 `cs2j-workspace-plan.json` 直接暴露这些 bridge 需求。

## 本批非目标

这一次不做：

- 不重写 compatibility helper 生成器
- 不引入新的 bridge 代码生成逻辑
- 不把 bridge 再拆成独立 artifact 发布单元
- 不实现 bridge 级别的 CI 阈值

## 设计要点

### 1. bridge 与 pack 的关系

当前实现里，compatibility pack 已经天然承担 runtime bridge 的角色：

- 有稳定 `id`
- 有语义描述
- 有 Maven 依赖
- 有按需启用判定

因此本批不再额外发明第二套来源，而是直接把 pack 元数据提升为 bridge requirement。

### 2. 规划模型

新增 `JavaRuntimeBridgeRequirement`，至少包含：

- `bridgeId`
- `description`
- `requiredCompatPacks`
- `dependencies`

其中：

- module 级保留本模块直接需要的 bridge
- workspace 级对所有模块做稳定聚合

### 3. 输出效果

这样生成的 `cs2j-workspace-plan.json` 不再只有“需要哪些兼容包”，还会明确展示：

- 需要哪些 runtime bridge
- 每个 bridge 在解决什么语义差异
- 它是否引入额外依赖

## 验证点

本批至少覆盖：

1. compatibility pack 分析会返回 runtime bridge requirement。
2. workspace plan JSON 会序列化 module/workspace 级 runtime bridge。
3. shared compatibility module 能聚合所有子模块的 runtime bridge。

## 后续衔接

在这一批之后，Phase 4 / 5 交界处的下一步优先级为：

1. 让 canary 回归直接消费 runtime bridge requirement
2. 为更深层增量缓存补上 source/project 指纹与失效传播
3. 继续压缩 post-generation rewrite 对 bridge 语义修补的依赖