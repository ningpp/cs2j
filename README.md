# CS2J CSharp To Java Converter

基于 Roslyn 的 C# → Java 源码转换器。输入单个 `.cs` 文件、`.csproj`、`.sln`/`.slnx` 或源码目录，输出可由 Maven 直接构建、可运行 JUnit 测试的 Java 28 工程。

转换采用**两阶段**设计：

1. **C# → C# 预处理**：42 个可独立开关的 pass 把 Java 难以直接承载的 C# 语法归一化为可转换子集。每个 pass 输出独立的 C# 中间树，可对中间树执行 `dotnet build` / `dotnet test` 验证。
2. **C# → Java 语义转换**：用 Roslyn 语义模型把归一化后的编译单元映射为 Java AST，再经过 Java 层 IR 重写、类型分组和 Maven 工程规划输出最终工程。

> 当前仓库快照（2026-10）：42 个预处理 pass（全部 Implemented、默认开启）、20 个 CLI 命令、964 条类型映射 / 584 条方法映射 / 29 条命名空间映射、21 个 Java 模块共 4336 个类型元数据 JSON、Java 兼容运行时约 696 个源文件、3 个 xUnit 测试工程与 50 个 `Sample*Solution` 端到端语料。

---

## 目录

- [核心能力](#核心能力)
- [环境要求](#环境要求)
- [构建](#构建)
- [测试](#测试)
- [快速开始](#快速开始)
- [CLI 命令参考](#cli-命令参考)
- [输出结构](#输出结构)
- [系统架构](#系统架构)
- [配置体系](#配置体系)
- [测试体系与验证](#测试体系与验证)
- [目录结构](#目录结构)
- [已知边界](#已知边界)
- [第三方与许可](#第三方与许可)

---

## 核心能力

### 转换粒度

| 输入 | 命令 | 说明 |
|------|------|------|
| 单个 `.cs` 文件 | `convert` | 单文件语义转换，不执行 42-pass 预处理链 |
| 目录 / `.csproj` / `.sln` / `.slnx` | `convert-project` | 完整预处理 + 项目级语义转换 + Maven 工程输出 |

`convert-project` 优先用 MSBuild 加载完整工作区（含 NuGet 引用、项目引用、资源项、测试工程识别）；MSBuild 不可用时回退到手工工程图解析，再回退到目录扫描。

### 语义覆盖

- **类型系统**：基础类型、数组、`Nullable<T>`、元组、泛型（含擦除补偿与闭型元数据）、`unsafe`/指针（FFM）、`byte/ushort/uint/ulong`（支撑库 + 兼容运行时）。
- **类型成员**：属性、索引器、事件、运算符、委托、扩展方法、显式接口实现、`record`、`struct`、`enum`、`interface`。
- **控制流**：`goto`/`label`（switch 状态机）、模式匹配、`switch`、`foreach` 解构、`using`、`yield`（迭代器状态机）。
- **表达式**：LINQ（`linq-channel` 过程化改写）、`??`/`??=`、`?.`/`?[]`、插值字符串、对象/集合/目标类型 `new` 初始化器、`checked`/`unchecked`、`^`/`..`、委托组合。
- **并发与异步**：`async/await` → `CompletableFuture`（`await` 经兼容层同步等待，见[已知边界](#已知边界)）、`lock`/`Monitor`、`[ThreadStatic]`、闭包捕获。
- **平台与互操作**：`DllImport`/`LibraryImport` → FFM、平台 API / UI 命名空间的审计与隔离。

### Java 侧输出

- Java 28（预览特性，生成的 POM 自动加 `--enable-preview`）。
- C# `record` → Java `record`；abstract、带附加字段等不满足 Java record 约束时回退为普通类，`record struct` 走可变类路径。C# `struct` → Java 类，值语义由预处理与防御性拷贝保证。
- 默认依赖 `csharptojava-compat`、`vavr`、`jackson-databind`；测试模块附 JUnit 5 + surefire。
- 资源文件按 `CopyToOutputDirectory` / `EmbeddedResource` 复制到 `src/main/resources` 或 `src/test/resources`。

### 工程化与可观测性

- Maven 父子 POM、模块依赖拓扑、测试工程折叠或独立 test 模块、`test-jar`。
- 输入指纹快照、输出清单（内容哈希复用 + 陈旧文件清理）、workspace plan、pass profile、canary summary、可选 LINQ 报告。
- 诊断分级（Info/Warning/Error）与阻断式闸门：不支持的域、平台边界、原生互操作、编译错误都会阻止对应文件发射。
- 明确的退出码：全部成功为 `0`，存在失败为 `1`。

---

## 环境要求

| 场景 | 要求 |
|------|------|
| 运行转换器 / 单元测试 | .NET 10 SDK |
| `dotnet build` / `dotnet test` 生成的 Java 工程 | JDK 28（当前验证使用 28-ea）+ Maven 3.9+ |
| Java E2E 测试 | JDK 28 + 已编译的 `java/csharptojava-compat/target/classes` |
| 转换目标工程 | 目标工程所需的 .NET SDK / workload（由 MSBuild 解析） |

转换器本身不需要 JDK 或 Maven；它们只用于构建和验证生成的 Java 工程。

---

## 构建

```bash
dotnet build CSharpToJavaConverter.slnx -c Release
```

`slnx` 包含 5 个项目：`CSharpToJava.Core`、`CSharpToJava.CLI`、`Cs2jUnsignedMath`、`Cs2jArray`、`Cs2jUnsigned`。`Cs2jPrelude` 与 `Cs2jRefOutIn` 的源码以 `EmbeddedResource` 形式嵌入 CLI，不单独出现在解决方案中。

---

## 测试

三个测试工程相互独立，请显式指定：

```bash
dotnet test tests/CSharpToJava.Tests -c Release
dotnet test tests/CSharpToJava.ProjectTests -c Release
dotnet test tests/CSharpToJava.ConversionTests -c Release
```

> 注意：`dotnet test` 若指向根解决方案，不会运行任何测试，因为 `CSharpToJavaConverter.slnx` 只包含 `src/` 下的 5 个项目，不包含测试工程。

| 测试工程 | 角色 | 规模（Fact/Theory 声明数） |
|----------|------|---------------------------|
| `CSharpToJava.Tests` | 预处理器、转换器、Java2 IR、compat 行为的单元测试 | ~3859 |
| `CSharpToJava.ProjectTests` | 项目/解决方案级端到端转换与 Java 编译运行 | ~1792 |
| `CSharpToJava.ConversionTests` | 片段级转换正确性（成功的、无 C# 残留、Java 形态正确） | ~547 |

Java E2E 测试默认跳过，由 `SlowE2EGate` 控制；需要先编译兼容运行时，再显式开启：

```bash
mvn -f java/csharptojava-compat/pom.xml compile
CS2J_RUN_SLOW_E2E=1 dotnet test tests/CSharpToJava.ProjectTests -c Release
```

`JavaE2EHarness` 会用 JDK 28 的 `javac`（`--enable-preview --release 28`）编译生成的 Java 与 `java/csharptojava-compat/target/classes`，再运行 JVM 断言。

---

## 快速开始

### 1. 构建并安装兼容运行时（首次必需）

```bash
mvn -f java/csharptojava-compat/pom.xml install
```

生成工程的 POM 默认依赖 `io.github.ningpp:csharptojava-compat:1.0-SNAPSHOT`。

### 2. 转换单个文件

```bash
dotnet run --project src/CSharpToJava.CLI/CSharpToJava.CLI.csproj -c Release -- \
  convert -i Input.cs -o Output.java
```

省略 `-o` 时输出到 stdout。单文件模式不执行预处理链，适合快速验证表达式/语句级转换。

### 3. 转换项目或解决方案

```bash
dotnet run --project src/CSharpToJava.CLI/CSharpToJava.CLI.csproj -c Release -- \
  convert-project -s ./MySolution.slnx -d ./out --unified-preprocessed-src
```

- 默认从当前工作目录的 `config/TypeMappings.json` 读取映射配置；建议在仓库根目录执行，或用 `-m` 显式指定。
- `--unified-preprocessed-src` 让所有 pass 共用 `.cs2j-preprocessed-src` 目录，显著减少大型解决方案的磁盘占用。
- 首次转换会执行全部 42 个 pass；大型工程耗时较长属正常现象。

### 4. 构建并测试生成的 Java 工程

```bash
mvn -f out/pom.xml clean install
```

---

## CLI 命令参考

### 命令总览（共 20 个）

| 命令 | 模式 | 作用 |
|------|------|------|
| `convert` | 单文件 → Java | 不做预处理链，直接进行单文件语义转换 |
| `convert-project` | 目录/工程/解决方案 → Java 工程 | 完整预处理 + 项目级转换 + Maven 输出 |
| `preprocess` | C# → C# | 只运行注册的预处理 pass，不生成 Java；`--list-passes` 查看 pass 列表 |
| `analyze` | 只读分析 | 粗粒度扫描类型使用与映射覆盖，输出控制台摘要和 JSON 报告 |
| `strip-conditional` | C# → C# | 求值并移除 `#if/#elif/#else/#endif/#define/#undef`，可选移除 `#region` |
| `eliminate-goto` | C# → C# | `goto`/`label` → switch 状态机 |
| `make-readonly` | C# → C# | 将可判定的可变 struct 转为 readonly struct |
| `record-readonly` | C# → C# | record / record struct → readonly/不可变 record |
| `generic-preprocess` | C# → C# | `default(T)`、`new T()`、`new T[n]` 等泛型模式改写 |
| `tuple-to-struct` | C# → C# | ValueTuple 类型/字面量/解构 → 生成的 readonly struct |
| `strip-default` | C# → C# | 默认参数 → 重载；`params` 集合归一 |
| `ref-out-in` | C# → C# | `ref`/`out`/`in` 参数 → `ByRef<T>` 代理模型 |
| `unsigned-type` | C# → C# | `byte/ushort/uint/ulong` 关键字 → `System.*` 全名及运算改写 |
| `array-method` | C# → C# | 数组实例成员/`System.Array` 静态调用 → `Cs2jArray.ArrayHelper` |
| `foreach-deconstruction` | C# → C# | `KeyValuePair` 解构 foreach → 普通 foreach + 局部变量 |
| `delegate-preprocess` | C# → C# | 委托 `+/-/+=/-=` 与匿名方法改写 |
| `null-coalesce` | C# → C# | `??` / `??=` → 显式三元/if 赋值 |
| `enum-preprocess` | C# → C# | 枚举位运算、复合赋值、`HasFlag` → 显式整数运算 + 强转 |
| `java-keywords` | C# → C# | 与 Java 保留字冲突的标识符重命名；`operator true/false` 降级 |
| `local-function-lift` | C# → C# | 局部函数提升为类成员方法 |

单个预处理器命令只是“只跑一步”的入口；`convert-project` 默认会按锁定顺序执行全部 pass。多个命令还提供 `--strict`、`--no-filter-projects`、`--no-verify-build` / `--no-verify-tests` 等变体，具体以 `--help` 为准。

### `convert` — 单文件转换

| 选项 | 说明 |
|------|------|
| `-i, --input` | 输入 `.cs` 文件（必需） |
| `-o, --output` | 输出 `.java` 文件；省略则写 stdout |
| `-m, --mapping` | 类型映射配置文件 |
| `-j, --java-version` | 目标 Java 版本（默认 `Java28`，当前仅此一项） |
| `--no-records` | C# record 不使用 Java record |
| `--use-optional` | nullable 类型使用 `Optional` |
| `--no-javadoc` | 不生成 JavaDoc |
| `--no-linq-rewrite` | 关闭单文件嵌入式 LINQ 过程化改写 |
| `--use-value-class` | readonly struct 使用 Java Value Class（JEP 401）；**当前仅 HIR/Java2 实验管线生效** |
| `--no-parallel-project-passes` | 关闭项目级 pass 的并行执行 |
| `-v, --verbose` | 输出诊断信息 |

`--prefer-stream-api` 与 `--prefer-procedural` 仅为命令行兼容保留，**不再改变输出**（LINQ 策略已统一为过程化改写）。

### `convert-project` — 项目/解决方案转换

| 选项 | 说明 |
|------|------|
| `-s, --source` | 源码目录、`.csproj`、`.sln` 或 `.slnx`（必需） |
| `-d, --destination` | 输出目录（必需） |
| `-m, --mapping` | 类型映射配置文件 |
| `-j, --java-version` | 目标 Java 版本（默认 `Java28`） |
| `-f, --force` | 覆盖输出（默认 `true`；无历史清单时会清理目标源码目录） |
| `--no-cache` | 不复用历史输出（默认 `true`，即默认基于指纹的复用是关闭的） |
| `--unified-preprocessed-src` | 所有 pass 共用一个中间目录，节省磁盘 |
| `--no-preprocess` | 跳过全部预处理 pass，直接转换已预处理的源码 |
| `--enable-pass` / `--disable-pass` | 按 pass id 显式启用/禁用（可用 `preprocess --list-passes` 查询 id） |
| `--maven-group-id` | 生成工程的 groupId（默认 `io.github.ningpp`） |
| `--maven-version` | 生成工程的版本（默认 `0.0.1-SNAPSHOT`） |
| `--include-tests` | 转换测试工程（默认 `true`） |
| `--extra-deps` | 追加到每个模块的 Maven 坐标，格式 `groupId:artifactId:version`，逗号分隔 |
| `--linq-report` | 输出 `linq-channel` 的 schema v2 报告到 `cs2j-linq-report.json` |
| `--no-verify-build` / `--no-verify-tests` | 关闭预处理中间树的 `dotnet build` / `dotnet test` 验证 |
| `--no-filter-projects` | 不排除 WPF/UWP/WinForms 等不支持工程 |
| `--no-records` / `--use-optional` / `--no-javadoc` | 同单文件 |
| `--use-value-class` | readonly struct 使用 Java Value Class（项目模式默认 `true`）；**当前仅 HIR/Java2 实验管线生效** |
| `-v, --verbose` | 输出详细进度 |

各预处理 pass 另有历史开关（如 `--no-eliminate-goto`、`--no-ref-out-in`、`--no-make-readonly`、`--no-strip-default`、`--no-tuple-to-struct`、`--no-linq-rewrite` 等）；与 `--enable-pass` / `--disable-pass` 冲突时，显式 pass 开关优先。

### `preprocess` — 只运行预处理链

```bash
# 查看全部 pass 的执行顺序、阶段、状态、默认值与 RunAfter 关系
dotnet run --project src/CSharpToJava.CLI/CSharpToJava.CLI.csproj -c Release -- \
  preprocess --list-passes

# 运行默认全链
dotnet run --project src/CSharpToJava.CLI/CSharpToJava.CLI.csproj -c Release -- \
  preprocess -s ./MySolution.slnx -d ./preprocessed --unified-preprocessed-src

# 只运行指定 pass（按锁定顺序执行）
dotnet run --project src/CSharpToJava.CLI/CSharpToJava.CLI.csproj -c Release -- \
  preprocess -s ./src -d ./preprocessed --pass ref-out-in,eliminate-goto
```

| 选项 | 说明 |
|------|------|
| `-p, --pass` | 只运行指定 pass（逗号分隔/可重复），仍按锁定顺序执行 |
| `--enable-pass` / `--disable-pass` | 在默认链基础上增删 pass |
| `--list-passes` | 列出注册表并退出 |
| `-s, --source` / `-d, --destination` | 源与目标 |
| `--unified-preprocessed-src` | 共用中间目录 |
| `--tuple-namespace` | 生成元组 struct 的命名空间（默认 `Cs2jTuples`） |
| `--no-verify-build` / `--no-verify-tests` | 关闭中间树验证 |
| `-m, --mapping` | 映射配置（用于 compat 命名空间前缀判定） |

### `strip-conditional` — 条件编译清理

```bash
# 单文件；省略 -o 输出到 stdout
dotnet run --project src/CSharpToJava.CLI/CSharpToJava.CLI.csproj -c Release -- \
  strip-conditional -i Input.cs -o Output.cs --define FEATURE_A --define FEATURE_B

# 目录/工程/解决方案；非 .cs 文件按字节复制
dotnet run --project src/CSharpToJava.CLI/CSharpToJava.CLI.csproj -c Release -- \
  strip-conditional -s ./src -d ./clean --configuration Release --tfm net8.0 --remove-regions
```

目录模式下，符号按文件解析：最近的 `.csproj`、父级 `Directory.Build.props`、目标框架、重复的 `--define`。裸 `<DefineConstants>` 覆盖先前值，包含 `$(DefineConstants)` 时追加。解析失败的文件会报告并原样复制；错误始终返回 1，`--strict` 下警告也返回 1。

| 选项 | 说明 |
|------|------|
| `-i, --input` / `-o, --output` | 单文件输入/输出 |
| `-s, --source` / `-d, --destination` | 目录/工程/解决方案模式 |
| `--define` | 额外预处理符号（可重复，优先级最高） |
| `--configuration` | 工程属性求值配置（默认 `Release`） |
| `--tfm` | 显式目标框架 |
| `--remove-regions` | 同时移除 `#region/#endregion` |
| `--strict` | 警告视为失败 |
| `-v, --verbose` | 逐文件统计 |

### `eliminate-goto` / `analyze`

```bash
dotnet run --project src/CSharpToJava.CLI/CSharpToJava.CLI.csproj -c Release -- \
  eliminate-goto -i Input.cs -o Output.cs [--strict]

dotnet run --project src/CSharpToJava.CLI/CSharpToJava.CLI.csproj -c Release -- \
  analyze -s ./src -r report.json [-m config/TypeMappings.json]
```

- `eliminate-goto` 支持 `-i/-o` 或 `-s/-d`；`--strict` 把“无法消除的方法”从警告升级为失败。
- `analyze` 是**语法级**覆盖率扫描（不建语义模型）：统计类型使用频次、未命中映射的潜在类型，并写出 `GeneratedAt / TypeUsage / MissingMappings` JSON。

---

## 输出结构

### 多模块工程（sln/slnx 或工程图）

```text
out/
├── pom.xml                          # 父 POM（packaging=pom，modules 按名称排序）
├── .mvn/maven.config                # 默认 --also-make，模块构建保持在 reactor 内
├── .gitignore
├── cs2j-workspace-plan.json         # JavaWorkspacePlan（模块、依赖、source sets、Java 版本）
├── cs2j-input-fingerprints.json     # 源码/配置/模板/工具 DLL 的 SHA256 与选项哈希
├── cs2j-output-manifest.json        # 输出文件内容哈希，用于复用和陈旧文件清理
├── cs2j-pass-profile.json           # 每个转换 pass 的耗时/内存/改写量聚合
├── cs2j-canary-summary.json         # 成功/失败数、诊断分级与分类统计
├── cs2j-linq-report.json            # 可选：linq-channel schema v2 报告
├── .cs2j-<pass-id>-src/             # 各预处理 pass 的中间树（未使用统一目录时）
├── .cs2j-preprocessed-src/          # 可选：--unified-preprocessed-src 的统一中间树
├── ModuleA/
│   ├── pom.xml
│   └── src/
│       ├── main/java/...            # 按 Java package 落盘
│       └── main/resources/...       # CopyToOutputDirectory / EmbeddedResource
└── ModuleA.Tests/
    ├── pom.xml                      # 测试模块附 JUnit 5、surefire、test-jar
    └── src/test/java/...
```

- 生产工程与测试工程的映射规则：测试工程只引用一个生产模块时，测试源码折叠进该模块的 `src/test/java`；引用多个或独立测试工程时，生成独立 test-only 模块（自身产出 `test-jar`），测试工程之间互相引用时使用 `test-jar` 依赖。
- 单模块（目录扫描回退等）输出为 `<destination>/src/main/java` + 根 `pom.xml`。

---

## 系统架构

### 总览

```text
┌────────────────────────────── convert-project ──────────────────────────────┐
│                                                                              │
│  [1] 预处理链（C# → C#，42 pass，默认全开）                                  │
│      .cs/sln ─► conditional ─► … ─► java-keywords ─► 归一化 C# 树             │
│      每个 pass：独立中间目录、可单独开关、可 dotnet build/test 验证           │
│                                                                              │
│  [2] 工程加载与语义编译                                                       │
│      MSBuildWorkspace（完整引用）→ 手工工程图 → 目录扫描                      │
│      → CSharpCompilation（DEBUG 默认按 Release 语义剥离）                     │
│                                                                              │
│  [3] 项目转换 pass                                                            │
│      4 个闸门检查 → JavaPatternRewrite → PartialTypeNormalization             │
│      → TypeEmit（Visitor/Transformers → Java AST → IR rewriters）             │
│      → JavaModuleDependency                                                  │
│                                                                              │
│  [4] 类型分组与输出规划                                                       │
│      TypeGroupResolver（partial 合并/嵌套折叠）→ JavaWorkspacePlan            │
│      → Maven 父子 POM、资源、清单、指纹                                       │
│                                                                              │
│  [5] 生成工程依赖的运行时                                                     │
│      csharptojava-compat + vavr + jackson-databind → mvn clean install       │
│                                                                              │
└──────────────────────────────────────────────────────────────────────────────┘
```

### 1. 预处理层（C# → C#）

预处理是本项目与“直接语法翻译器”最大的区别：先在 C# 语义层面把难转换的构造降级为等价、可编译的 C# 子集，再交给 Java 发射器。好处是每个关注点可独立测试（`dotnet build` / `dotnet test` 中间树）、可独立回滚/开关，并让 Java 侧发射器保持简单。

#### Pass 生命周期

```text
PassDescriptor（id / 阶段 / 中间目录 / 默认值 / RunAfter / 是否需要语义模型 / 支撑库 / 验证默认值）
    ↓
PassRegistry（校验重复 id、未知锚点、环；按 RunAfter 贪心拓扑排序，声明顺序作 tiebreak）
    ↓
PassPlanResolver（descriptor 默认 → 历史 --no-xxx → 显式 --enable-pass/--disable-pass）
    ↓
PassChainFactory → ProjectPassAdapter（每个 pass 一个适配器）
    ↓
ProjectPassRunner（过滤不支持工程、按需构建整树编译、并行解析/重写、落盘、可选 dotnet build/test）
    ↓
CliPreprocessChain（顺序执行全部启用 pass；最终源码路径交给转换层）
```

要点：

- 数据以**磁盘上的 C# 源码树**在 pass 间传递，不是内存中的 Roslyn 语法树；每个 pass 默认产出 `.cs2j-<pass-id>-src`，`--unified-preprocessed-src` 时统一写入 `.cs2j-preprocessed-src`。
- `PassStage`（Lexical / Declarative / Expression / ControlFlow / StateMachine / Abi / FinalNaming）只用于展示和分组，**实际顺序由 `RunAfterIds` 拓扑排序决定**。
- 同一时刻只运行一个 pass；并行发生在 pass 内部（整树解析、纯文件重写），结果按源文件顺序合并以保证确定性。
- `ParsedTreeCache` 以“解析选项签名 + 绝对路径 + 内容哈希”缓存 `SyntaxTree`，避免统一目录链上每个 pass 重复解析整树。
- 需要语义模型的 pass 会构建整树 `CSharpCompilation`；纯文本 pass（如 `conditional`、`eliminate-goto`）跳过编译。
- 声明了 `verifyBuildByDefault` / `verifyTestsByDefault` 的 pass 默认对中间树执行 `dotnet build` / `dotnet test`；CLI 的 `--no-verify-build` / `--no-verify-tests` 全局关闭。
- 支撑库（`Cs2jPrelude`、`Cs2jRefOutIn`、`Cs2jUnsignedMath`、`Cs2jArray`）由相关 pass 释放到中间树作为符号来源，并在 Java 模块规划/转换/指纹中排除。
- `PassStatus.Stub` 是占位机制：默认关闭；若被显式启用，会在改动文件前以 `CS2J9001` 失败，而不是静默空转。当前注册表没有启用的 stub。
- `--no-preprocess` 跳过整个链，适用于“C# 已预处理、只调试 Java 发射”的场景。

#### 42 个 pass（锁定执行顺序）

| # | pass id | 阶段 | 作用 |
|---|---------|------|------|
| 1 | `conditional` | Lexical | 求值并裁剪 `#if/#elif/#else/#endif/#define/#undef` |
| 2 | `lexical-normalize` | Lexical | `#line` 清理、`#error`/`extern alias`/`global using` 审计；字面量与文件作用域 namespace 归一按选项启用（C28/C43） |
| 3 | `partial-merge` | Lexical | 跨文件合并 `partial` 类型与方法（C20） |
| 4 | `local-function-lift` | Declarative | 局部函数提升为类级私有方法 |
| 5 | `null-coalesce` | Expression | `??` / `??=` → 显式三元/if 赋值 |
| 6 | `expression-desugar` | Expression | 插值字符串、集合表达式、对象/集合初始化器、`?.`/`?[]`（C23/C24/C35/C36/C51） |
| 7 | `enum` | Expression | `[Flags]` 位运算 / `HasFlag` → 显式整数运算 |
| 8 | `enum-declaration` | Declarative | 枚举声明 → 可映射的 sealed class 形态（C05） |
| 9 | `delegate` | Declarative | 委托组合/拆解与匿名方法改写 |
| 10 | `tuple` | Declarative | ValueTuple → 生成的 readonly struct |
| 11 | `anonymous-type-lift` | Declarative | 匿名类型 → 生成的命名类型（C21） |
| 12 | `foreach-deconstruction` | Expression | `KeyValuePair` 解构 foreach 降级 |
| 13 | `foreach-source` | ControlFlow | foreach 源归一（如 string → char[]）与残余解构收口（C16/C40） |
| 14 | `pattern-desugar` | ControlFlow | 模式匹配语句/表达式降级（C03/C37） |
| 15 | `strip-default` | Declarative | 默认参数 → 重载；`params` 集合归一（C48） |
| 16 | `ref-out-in` | Abi | `ref/out/in` → `ByRef<T>` 代理模型（C01） |
| 17 | `unsigned` | Abi | unsigned 类型/运算/字面量改写为 `System.*` 形态 |
| 18 | `numeric-boundary` | Expression | `checked`/`unchecked`、decimal、特殊数值边界显式化（C15/C34/C17/C22） |
| 19 | `generic` | Abi | `default(T)`、`new T()`、`new T[n]` 等泛型模式改写 |
| 20 | `thread-static` | Declarative | `[ThreadStatic]` → `ThreadLocal`（C09） |
| 21 | `generic-deepen` | Abi | 泛型擦除补偿深化（Holder/工厂方法，C02） |
| 22 | `closed-metadata` | Abi | 闭型泛型元数据注册表生成（C30） |
| 23 | `variance-audit` | Declarative | 型变/约束使用点安全审计（C33） |
| 24 | `record-readonly` | Declarative | record → readonly/不可变 record |
| 25 | `array-method` | Expression | 数组实例成员/`System.Array` → `Cs2jArray.ArrayHelper` |
| 26 | `index-range` | Expression | `^` / `..` → 显式索引计算（C31） |
| 27 | `make-readonly` | Declarative | 可判定的可变 struct → readonly struct |
| 28 | `attribute-normalize` | Declarative | 属性元数据归一（Caller* 参数填充等，C10） |
| 29 | `struct-value-semantics` | Declarative | 残余可变 struct 的防御性 `Clone`（C07） |
| 30 | `unsafe-ffm` | Abi | unsafe/指针/`DllImport` → FFM（C06） |
| 31 | `closure-lift` | StateMachine | 捕获变量/闭包 → 显示类（C32/C45） |
| 32 | `iterator-lowering` | StateMachine | `yield` → 迭代器状态机（C04） |
| 33 | `async-normalize` | StateMachine | async lambda/局部函数提升、`ConfigureAwait` 剥离、`await foreach/using` 归一（C18/C38） |
| 34 | `extension-methods` | Expression | 扩展方法/扩展块 → 静态调用（C19/C49） |
| 35 | `eliminate-goto` | ControlFlow | `goto`/`label` → switch 状态机 |
| 36 | `member-desugar` | Declarative | 属性/索引器/事件/运算符/finalizer/record `with`/`field` 关键字（C11–C14/C39/C46/C50/C27） |
| 37 | `linq-channel` | ControlFlow | LINQ → 过程化序列，输出 schema v2 报告（C26） |
| 38 | `concurrency-normalize` | Declarative | `Monitor`/`lock` 等并发原语归一（C29） |
| 39 | `equality-concat` | Expression | `==`/`+` 调用点实体化（C47） |
| 40 | `platform-isolation` | Abi | UI/平台/原生边界审计与隔离，模式 `Report/Reject/Stub/Facade`（C42） |
| 41 | `dynamic-desugar` | Abi | `dynamic` 站点审计/具体化（C25） |
| 42 | `java-keywords` | FinalNaming | Java 保留字重命名、`operator true/false` 降级 |

顺序依赖示例：`tuple` 必须晚于 `delegate`；`ref-out-in` 晚于 `strip-default`/`foreach-source`；`extension-methods` 晚于大多数声明/类型 pass；`member-desugar` 晚于 `closure-lift`；`java-keywords` 固定在最后。

### 2. 工程加载与语义编译

1. **MSBuildWorkspace**：`SolutionLoader` 通过 `MSBuildLocator` 定位 SDK，加载 `.sln/.slnx/.csproj`，拿到每个工程的 `CSharpCompilation`、文档、项目引用、资源项和测试标记。
2. **手工工程图**：MSBuild 不可用时，`ProjectDiscovery` 解析 `.csproj` 的 `ProjectReference`、`PackageReference`、`CopyToOutputDirectory`、`EmbeddedResource`，做拓扑排序并识别测试工程（`IsTestProject`、测试包引用、`.Tests/.Test/.UnitTests` 命名）。
3. **目录扫描**：最后回退到递归 `.cs` 扫描（忽略 `.git/.vs/.idea/bin/obj/node_modules`）。
4. **语义修正**：
   - 默认剥离 `DEBUG` 预处理符号，按 Release 语义转换（`CS2J_KEEP_DEBUG=1` 可保留）。
   - 剥离 `global::` 别名限定符。
   - 按平台规则过滤 WPF/UWP/WinForms 等不支持工程（`--no-filter-projects` 关闭）。
   - 排除注入的 C# 支撑库工程，避免它们出现在 Java 输出中。

每个工程最终构建为 `CSharpCompilation`，交给 `ProjectConversionPipeline`；`Cs2jLibrary` 是“工程 + 文档 + 编译”的统一视图。

### 3. 转换层（C# → Java）

主线使用**第一代 Java AST + Transformer 体系**（`src/CSharpToJava.Core/Java/` + `Visitors/` + `Transformers/`）。

#### 项目转换 pass 链

```text
ProjectCompilationCheck          # 确认有语法树，绑定 Compilation
ProjectUnsupportedDomainCheck    # WPF/UWP/WinForms 等域闸门（CS2J3001）
ProjectPlatformBoundaryCheck     # OS/平台 API 闸门（CS2J3101/3102）
ProjectNativeInteropCheck        # 原生互操作闸门（CS2J3201…）
ProjectJavaPatternRewrite        # JavaGenericPatternRewriter：泛型模式保持完整类型
ProjectPartialTypeNormalization  # PartialTypeMerger：跨文件 partial 分组
ProjectTypeEmit                  # 逐类型组发射 Java AST → IR rewriter → 字符串
ProjectJavaModuleDependency      # 从 import 反查 Java 平台模块（需 config/java 元数据）
```

检查类 pass 会把阻断诊断写到文件级状态，后续发射阶段跳过该文件；其余文件继续转换，最终由 CLI 汇总成功/失败数并返回退出码。

#### 类型分组与 partial 合并

- `PartialTypeMerger` 把跨文件的 `partial class/struct/interface/record` 合并为一个类型组；`partial` 方法按声明/实现配对。
- `TypeGroupResolver` 保证“**一个顶层类型组 → 一个 `.java` 文件**”；嵌套类型组被折叠进父类型，由 `ClassTransformer.TransformMerged` 合并。
- 每个类型组独立清空并重建 import/alias 集合，避免跨文件状态污染；包名由 C# namespace + `namespaceMappings` 计算。

#### Visitor / Transformer

`CSharpToJavaVisitor`（`CSharpSyntaxVisitor<JavaSyntaxNode?>`）是转换入口：

- 类型：`ClassTransformer`、`StructTransformer`、`RecordTransformer`、`EnumTransformer`、`InterfaceTransformer`、`DelegateTransformer`。
- 成员/语句/表达式：`Transformers/Member`、`Transformers/Statement`、`Transformers/Expression` 下的专用 transformer，以及按签名分派的 `ExpressionTransformerRegistry`。
- 类型映射：`TypeMappingService` 处理别名、数组、`Nullable<T>`、指针、元组、泛型 arity；`config/TypeMappings.json` 提供类型/方法/命名空间映射，`config/java` 元数据提供 PascalCase → camelCase 方法推导、类型/方法合法性校验、规范名解析和模块归属。
- 注释：`CommentConversion` 把 XML/普通注释转换为 JavaDoc/行注释。

#### Java IR 重写

发射前对 Java AST 执行以下重写：

1. **内建兼容性 rewriter**（约 16 个）：运算符优先级、`Map.Entry` 类型、`MemberwiseClone`、`Math` 方法、委托调用、泛型数组创建、接口包装桥、残余扩展调用、`IntStream` 装箱、数组/Iterable 转换、Stream 往返、`Stopwatch` API、行续接清理、字符串拼接、事件处理器 lambda、异常 API。
2. **元数据校验 rewriter**（配置 `config/java` 时启用）：`JavaApiValidationRewriter`、`JavaExceptionCheckRewriter`。
3. **单文件路径的结构/正确性 rewriter**：变量名去重、隐式强转补全、若干已知转换缺陷的定向修补。

个别历史问题仍在**字符串层**做最终归一：`global::` 剥离、裸三元语句转 if/else、浮点字面量修正、条件括号配平（`StripRedundantConditionalParens`）。

#### 单文件 vs 项目 vs 第二套管线

| 路径 | 预处理 | 语义信息 | 状态 |
|------|--------|----------|------|
| `convert` → `ConversionPipeline` | 无 42-pass；仅嵌入式 LINQ 过程化改写 | 单文件 Compilation + 补充框架引用 + 全局 using | CLI 主路径 |
| `convert-project` → `ProjectConversionPipeline` | 42-pass（可用 `--no-preprocess` 跳过） | MSBuild 全工程 Compilation | CLI 主路径 |
| `NewConversionPipeline`（HIR → Lowering → Java2 CodeGen） | 无 | 单文件 Compilation | **实验性/未接入 CLI**，仅被测试引用 |

HIR/Java2 是并行重构：`IrNode` 体系携带 `Symbol`/`JavaType`，11 个 Lowering pass 消除 C# 语义后由 `JavaCodeGenerator` 写出。它尚未替代第一代 AST，维护时需要明确区分。

### 4. 输出规划层

- `MultiModulePlanner`：生产工程 → 模块（名称唯一化、按项目引用建立依赖）；测试工程按引用数量折叠或独立成 test-only 模块；模块按拓扑序输出。
- `JavaWorkspacePlan`：groupId/artifactId/version、Java 版本、模块、source sets、资源映射、依赖与 test-jar 标记。
- `MavenPomGenerator`：
  - 单模块：直接生成 `pom.xml`。
  - 多模块：父 POM `packaging=pom` + 按名称排序的 modules；子 POM 继承 parent，内部依赖用 `${project.version}`。
  - 编译器：`-parameters`、`-Xmaxerrs 1000000`，Java ≥ 27 时加 `--enable-preview`。
  - 测试：JUnit Jupiter 5.11.4 + surefire 3.3.1（扫描所有编译类，避免 C# 测试类命名被默认模式漏掉）。
  - 默认依赖：`io.vavr:vavr:0.10.4`、`com.fasterxml.jackson.core:jackson-databind:2.17.2`、`io.github.ningpp:csharptojava-compat:1.0-SNAPSHOT`。
- 资源复制：`CopyToOutputDirectory` 与 `EmbeddedResource` 按工程相对路径写入对应模块的 `main/test resources`。
- 增量与清单：
  - `InputFingerprintSnapshot`：对源码、映射配置、模板、工具 DLL 计算 SHA256，并与选项哈希一起写入 `cs2j-input-fingerprints.json`。
  - `OutputIncrementalWriteSession`：按内容哈希复用未变文件，`SaveAsync` 清理陈旧文件与空目录；清单为 `cs2j-output-manifest.json`。
  - `PassProfileSnapshot` / `CanarySummarySnapshot`：只用于性能与诊断统计，不参与缓存判定。
  - 注意：CLI 的 `--no-cache` 默认值为 `true`，即**默认不复用历史输出**；指纹/清单仍会写入，便于显式关闭该开关时复用。

### 5. 兼容运行时与支撑库

生成代码依赖 [java/csharptojava-compat](java/csharptojava-compat/pom.xml)（Maven：`io.github.ningpp:csharptojava-compat:1.0-SNAPSHOT`，Java 28 + preview）：

| 包 | 职责 |
|----|------|
| `io.github.ningpp.compat` | 核心桥接：`CSharp*` 集合/字典、异常、IO、URI、反射、JSON、`UnsignedMath`、`UInt/UShort/ULong`、`ByRef`、`ArrayHelper`、`DelegateHelper`、`Task` 等 |
| `dotnet.system` / `dotnet.system.Collections` | `System.*` 的小型桥接 |
| `csharp.xunit` / `csharp.xunit.Abstractions` / `csharp.xunit.Sdk` | xUnit 属性、`Assert`、`DataDiscoverer` |
| `Microsoft.VisualStudio.TestTools.UnitTesting` | MSTest `Assert`/`CollectionAssert`/`StringAssert`/`TestContext` 等 |
| `Cs2jByRef` | `ref` 槽的 `Interlocked` 语义桥 |

C# 侧支撑库（唯一真源在 `src/`，以 `EmbeddedResource` 嵌入 CLI）：

| 项目 | 作用 |
|------|------|
| `Cs2jPrelude` | 委托 `Combine`/`Remove` 辅助，调用点映射到 compat `DelegateHelper` |
| `Cs2jRefOutIn` | `ByRef<T>` 抽象与数组元素/访问器别名实现 |
| `Cs2jUnsignedMath` | unsigned 运算辅助，映射到 compat `UnsignedMath` |
| `Cs2jArray` | 数组方法辅助，映射到 compat `ArrayHelper` |
| `Cs2jUnsigned` | unsigned 语义参考/对拍实现，仅测试引用，不进入输出 |

这些支撑库只作为预处理阶段的**符号来源**；`MultiModulePlanner` 与转换阶段会把它们过滤掉，不为其规划 Java 模块。

---

## 配置体系

| 文件/目录 | 作用 |
|-----------|------|
| `config/TypeMappings.json` | 964 条类型映射、584 条方法映射、29 条命名空间映射 |
| `config/java/` | 21 个 Java 模块、4336 个公共类型 JSON；驱动方法名推导、类型/方法校验、规范名与模块归属 |
| `config/JavaKeywordsOverrides.json` | `force`/`skip`：强制或跳过保留字重命名/运算符降级 |
| `config/MemberDesugarOverrides.json` | 成员降级策略（当前 `Finalizers.Policy = Remove`） |
| `config/ReadOnlyStructOverrides.json` | `forceReadOnly`：强制按 readonly struct 处理 |
| `config/RecordReadOnlyOverrides.json` | `force`/`skip`：record readonly 转换白/黑名单 |

映射配置结构：

```json
{
  "typeMappings": [
    { "csharp": "System.String", "java": "String", "imports": [] }
  ],
  "methodMappings": [
    { "type": "System.Collections.Generic.List`1", "method": "Remove", "javaMethod": "removeValue" }
  ],
  "namespaceMappings": [
    { "csharp": "System.Collections.Generic", "java": "dotnet.System.Collections.Generic" }
  ]
}
```

说明：

- 未指定 `-m` 时，默认读取当前工作目录的 `./config/TypeMappings.json`。
- Java 元数据目录由映射文件所在目录下的 `java/` 推导；必须存在 `java/java.base/` 才会启用。缺少元数据时，静态类型/方法映射仍可用，但方法名推导、Java API 校验和模块归属分析会关闭。
- `config/java/<module>/<package path>/<Type>.json` 的字段包括 `moduleName`、`packageName`、`binaryName`、`canonicalName`、`simpleName`、`kind`、`modifiers`、`superClass`、`interfaces`、构造器与方法列表。

---

## 测试体系与验证

### 测试分层

- **单元测试**（`CSharpToJava.Tests`）：预处理 pass、转换器、Java2 IR、compat 行为；覆盖 42 个 pass 的规则、诊断、边界与幂等性。
- **片段转换测试**（`CSharpToJava.ConversionTests`）：`ConversionTestBase` 在进程内调用 `ConversionPipeline`，断言转换成功、无 C# 残留（`=>`、`?.`、`??` 等）且包含预期 Java 形态；默认不启动 JVM。
- **项目端到端**（`CSharpToJava.ProjectTests`）：`tests/Sample*Solution` 下的 50 个夹具覆盖各 pass；`JavaE2EHarness` 用 JDK 28 编译并运行生成代码；`SlowE2EGate` 默认跳过慢用例。
- **语料**：`DualTargetCorpus`（C# 双目标语料）、`EqualityConcatProbe`、`SampleAnonymousTypeLiftSolution`（Lib/App/Tests 跨工程）等用于行为基线。

### CI

- `ci.yml`：push 到 `dev-2.x`/`main` 或 PR 时，用 .NET 10 运行 `CSharpToJava.Tests` 与 `CSharpToJava.ProjectTests`；不安装 JDK/Maven（因此不跑 Java E2E）。
- `agl-convert-and-report.yml`：手动触发，克隆私有 AGL 仓库，执行多模块转换与 Maven 构建，并把错误汇总为 Issue。

### 全量验证记录

2026-10-30 使用同一转换器版本（HEAD `54dfa6b6`）对 csharp-sdk 与 AGL 全量转换并验证：

| 语料 | 中间树 `dotnet build/test` | `mvn clean test-compile` | `mvn clean install` |
|------|---------------------------|--------------------------|---------------------|
| csharp-sdk | 0 error / 0 失败 | BUILD SUCCESS | BUILD SUCCESS，15 个测试模块 17,724 tests / 0 failures |
| agl | 0 error / 0 失败 | BUILD SUCCESS | BUILD SUCCESS，613 tests / 0 failures |

[csharp-sdk](https://github.com/ningpp/csharp-sdk) commit `5f4dbf71`。

[agl](https://github.com/ningpp/automatic-graph-layout) commit `cb4b74e3`。



转csharp-sdk
```
dotnet run -c Release --project src/CSharpToJava.CLI/CSharpToJava.CLI.csproj -- convert-project -s D:\csharp-sdk\csharp-sdk-without-json.sln -d d:\cs\cs-sdk-54dfa6b6  --no-verify-build --no-verify-tests --unified-preprocessed-src
```

安装csharp-sdk
```
cd d:\cs\cs-sdk-54dfa6b6

mvn clean install -e
```

转agl
```
dotnet run -c Release --project src/CSharpToJava.CLI/CSharpToJava.CLI.csproj -- convert-project -s D:\automatic-graph-layout\GraphLayout\agl.slnx -d d:\cs\cs-agl-54dfa6b6  --no-verify-build --no-verify-tests --unified-preprocessed-src --extra-deps io.github.ningpp:dotnet-xml:0.0.1-SNAPSHOT,io.github.ningpp:dotnet-text-regularexpressions:0.0.1-SNAPSHOT  
```

验证agl转换结果
```
cd d:\cs\cs-agl-54dfa6b6

mvn spotless:apply

mvn clean package -e
```


---

## 目录结构

```text
src/
├── CSharpToJava.CLI/
│   ├── Program.cs                       # 20 个 [Verb] 及命令实现
│   └── Preprocessing/                   # pass 注册表、链编排、适配器、runner
├── CSharpToJava.Core/
│   ├── Context/                         # ConversionContext/Options、诊断、类型映射服务
│   ├── Preprocessing/                   # PassDescriptor / PassRegistry
│   ├── <40+ 个预处理目录>/               # 每个 pass 的 analyzer / rewriter / preprocessor
│   ├── Pipeline/
│   │   ├── Passes/                      # 单文件/项目转换 pass、闸门、模块依赖
│   │   ├── Planning/                    # workspace plan、POM、指纹、输出清单
│   │   ├── ConversionPipeline.cs        # 单文件管线
│   │   ├── ProjectConversionPipeline.cs # 项目管线
│   │   └── NewConversionPipeline.cs     # HIR/Java2 实验管线
│   ├── Workspace/                       # MSBuild SolutionLoader
│   ├── Visitors/ + Transformers/        # C# → Java AST（主线）
│   ├── Java/                            # 第一代 Java AST + IR rewriter
│   ├── Java2/ + HIR/ + Lowering/        # 第二代 IR/CodeGen（实验性）
│   ├── TypeMapping/                     # JSON 映射 + Java 元数据模型
│   ├── PartialType/ + PartialMerge/     # partial 合并与类型分组
│   ├── LinqRewrite/                     # 单文件 LINQ 过程化重写
│   └── Analysis/ + Abstractions/ + Utilities/ + Comments/
├── Cs2jPrelude/                         # C# 支撑库（嵌入 CLI）
├── Cs2jRefOutIn/                        # C# ref/out/in 支撑库（嵌入 CLI）
├── Cs2jUnsignedMath/                    # C# unsigned 数学支撑库（嵌入 CLI）
├── Cs2jArray/                           # C# 数组支撑库（嵌入 CLI）
└── Cs2jUnsigned/                        # unsigned 语义参考实现（仅测试）

java/csharptojava-compat/                # Java 兼容运行时（Maven jar）
config/                                  # 类型映射、Java 元数据、overrides
tests/                                   # 3 个 xUnit 工程 + 50 个 Sample 语料
docs/superpowers/                        # 94 份 specs、73 份 plans、1 份 report
prompts/                                 # 预处理目录与大型语料转换提示词
tools/                                   # 反射/语料/日志探针与分诊脚本
```

---

## 已知边界

1. **async/await 是阻塞式桥接**：`Task/Task<T>` 映射为 `CompletableFuture`，`await` 经 `AsyncAwait.join` 同步等待，不产生 continuation/调度；对“最终已完成”的 Task 结果等价，但并发/异常语义与原生 C# async 不完全一致。`async-normalize` 负责 async lambda/局部函数提升、`ConfigureAwait` 剥离与异步流归一。
2. **HIR/Java2 尚未投产**：`NewConversionPipeline` 只在测试中使用；生产路径是第一代 Java AST。修改转换行为时不要误改实验管线；`--use-value-class` 也只在该实验路径生效。
3. **单文件模式不做预处理**：`goto`、`ref/out`、`yield`、`enum` 等需要预处理链的构造，应使用 `convert-project` 或对应的独立预处理命令。
4. **平台/原生边界是硬闸门**：WPF/UWP/WinForms、平台探针、`DllImport` 等默认被阻断或隔离；需要显式配置/预处理后才能通过。
5. **Java 28 预览特性**：生成的 POM 对 Java ≥ 27 自动加 `--enable-preview`；运行 `mvn` 需要匹配的 JDK（当前为 28-ea）。
6. **兼容运行时需先安装**：生成的工程默认依赖 `csharptojava-compat:1.0-SNAPSHOT`，首次使用前需在 `java/csharptojava-compat` 执行 `mvn install`。
7. **缓存复用默认关闭**：`--no-cache` 默认 `true`；指纹与清单用于显式关闭该开关时的复用，不会被默认路径用来跳过转换。
8. **根目录 `dotnet test` 不跑测试**：根 `slnx` 只包含 5 个 `src/` 项目，测试需指定三个测试工程。
9. **Java 元数据依赖目录布局**：`config/java` 必须与映射文件同级且包含 `java.base`，否则方法名推导、API 校验与模块归属分析不会启用。
10. **Java 兼容层不是完整 BCL**：compat 覆盖常见集合、反射、IO、JSON 与测试框架形态，但并非所有 .NET API 都有等价实现；未覆盖 API 会被诊断或阻断。

---

## 第三方与许可

- **[roslyn-linq-rewrite](https://github.com/antiufo/roslyn-linq-rewrite)** by Michał Komorowski（MIT License）——`src/CSharpToJava.Core/LinqRewrite/` 的 LINQ 过程化改写引擎基于该项目改造，输出目标改为 Java 兼容形态。
- 本项目采用 [MIT License](LICENSE)。
