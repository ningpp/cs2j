# Phase 4 细化设计：构建映射与 compatibility 输出收敛

## 背景

仓库里已经有三块和 Phase 4 直接相关的能力，但目前仍然是半成品状态：

1. `JavaWorkspacePlan` / `JavaModulePlan`
   已经能描述 Java 侧模块、依赖、source sets 和 compat pack 占位字段。
2. `MavenPomGenerator`
   已经能根据 `JavaWorkspacePlan` 生成 root / child POM。
3. CLI 的多模块转换流程
   已经能规划模块、写入 `pom.xml`，但 child POM 生成仍依赖“重复模块”占位 hack，而且没有把构建映射落成可消费的显式输出物。

这意味着 Phase 4 已经不是“从零开始”，而是要把现有 planning 数据真正收敛成稳定产物。

## 本批目标

本批只做 Phase 4 的第一段落地，聚焦“构建映射可见化”和“POM 生成语义收口”：

1. 明确 `GenerateModuleBuildFile` 的语义就是“生成子模块构建文件”，不再依赖 `plan.IsSingleModule` 推断。
2. 为 single-module 和 multi-module 输出统一的 `cs2j-workspace-plan.json` manifest。
3. 让 root POM、child POM、workspace manifest 都从同一份 `JavaWorkspacePlan` 派生，而不是分别在 CLI 里拼接。
4. 从转换结果中回填模块级 `requiredCompatPacks`，并把 shared compatibility module 需要的外部依赖做聚合去重。

## 本批非目标

这一次不做以下内容：

- 不重写 shared compatibility module 的整体策略。
- 不把 legacy helper 生成一次性替换为完整的 pack-driven 输出。
- 不引入 Gradle 生成器。
- 不在本批解决所有外部 Maven 依赖的最小化推导。

这些内容留给 Phase 4 后续子批次处理。

## 设计约束

### 1. Child POM 生成语义

`MavenPomGenerator.GenerateModuleBuildFile(plan, module)` 的职责固定为：

- 生成子模块 POM
- 使用 `plan.GroupId / ArtifactId / Version` 作为 parent 坐标
- 使用 `module.ModuleName` 作为当前模块 artifactId

换句话说：

- root / parent POM 只由 `GenerateRootBuildFile` 负责
- child POM 只由 `GenerateModuleBuildFile` 负责

这样 CLI 不再需要构造“两个相同 module 的假 plan”来骗过 `plan.IsSingleModule`。

### 2. Workspace Manifest

新增 `cs2j-workspace-plan.json`，至少包含：

- `groupId`
- `artifactId`
- `version`
- `javaVersion`
- `modules[]`
  - `moduleName`
  - `isTestOnly`
  - `sourceSets`
  - `dependencies`
  - `requiredCompatPacks`

manifest 的目标不是替代 POM，而是提供一个：

- 更容易被测试断言的结构化输出
- 未来 Gradle 生成、IDE 集成、可视化检查的统一输入

### 3. 派生关系

同一轮项目转换里：

1. 先收集所有模块对应的 `JavaModulePlan`
2. 再组装成根级 `JavaWorkspacePlan`
3. 从这份 plan 同时生成：
   - root POM
   - child POM
   - workspace manifest

## 验证点

本批至少覆盖以下回归：

1. `GenerateModuleBuildFile` 在只有一个 module 的 plan 下也生成 child POM 形态。
2. `JavaWorkspacePlan` 能稳定序列化成 JSON manifest。
3. manifest 中保留模块依赖、scope 和 `requiredCompatPacks`。
4. compatibility pack 需求可以从转换结果稳定推导，并映射到 shared compatibility module 依赖。

## 下一批衔接

这批完成后，Phase 4 的后续实现优先级为：

1. 从 compatibility pack 检测结果反推模块级 `requiredCompatPacks`
2. 收紧默认依赖，减少所有模块一刀切依赖
3. 在 shared compatibility module 与 pack metadata 之间建立一致关系