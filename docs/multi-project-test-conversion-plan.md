# 多项目与单元测试转换支持计划

## 目标

当前转换器能够把一个目录下的 C# 源文件批量转换为 Java，但它并不理解 `.sln` / `.csproj` 的项目边界，也不会把测试项目输出到 Maven 标准测试目录。本文给出一份面向实现的计划，用于支持：

1. 解析多项目 C# 解决方案。
2. 保留项目依赖关系与编译边界。
3. 识别单元测试项目与测试源码。
4. 将 Java 产物按 `src/main/java`、`src/test/java`、`src/main/resources`、`src/test/resources` 输出。
5. 为测试代码生成对应的 Java 测试依赖和基础语义映射。

## MSAGLTests 依赖结构结论

基于对 `MSAGLTests.csproj` 及其直接引用项目的检查，可以确认：

1. `MSAGLTests` 是独立测试项目，而不是主项目内嵌测试代码。
2. 测试框架是 MSTest，直接依赖 `Microsoft.NET.Test.Sdk` 和 `MSTest`。
3. `MSAGLTests` 直接引用了 3 个生产项目：
   - `AutomaticGraphLayout.csproj`
   - `AutomaticGraphLayout.Drawing.csproj`
   - `Dot2graph.csproj`
4. 这 3 个生产项目之间本身也有依赖链：
   - `AutomaticGraphLayout.Drawing` -> `AutomaticGraphLayout`
   - `Dot2Graph` -> `AutomaticGraphLayout.Drawing`
   - `Dot2Graph` -> `AutomaticGraphLayout`
   - `Dot2Graph` -> `QUT.ShiftReduceParser`
5. `MSAGLTests` 包含大量资源文件，并依赖 `CopyToOutputDirectory` 语义，这些资源不应该被当作 Java 源码处理。
6. 测试源码使用 MSTest 特性风格，至少包含：
   - `[TestClass]`
   - `[TestMethod]`
   - `[ClassInitialize]`
   - `[ClassCleanup]`

这说明 MSAGL 不是“单项目 + 少量测试文件”的场景，而是“多生产项目 + 独立测试项目 + 资源文件 + 跨项目依赖”的典型解决方案结构。

## 当前系统与目标的差距

当前系统的行为可以从以下实现直接看出：

1. `convert-project` CLI 接收的是目录路径，而不是 `.sln` / `.csproj`。[src/CSharpToJava.CLI/Program.cs](src/CSharpToJava.CLI/Program.cs#L494)
2. CLI 在开启 `generate-pom` 时，只定义了单一输出根目录 `src/main/java`。[src/CSharpToJava.CLI/Program.cs](src/CSharpToJava.CLI/Program.cs#L134)
3. CLI 调用的仍然是“给定目录，收集所有 `.cs` 文件”的批量转换接口。[src/CSharpToJava.CLI/Program.cs](src/CSharpToJava.CLI/Program.cs#L152)
4. `ConvertProjectAsync` 直接对目录做 `Directory.GetFiles(..., "*.cs", AllDirectories)`，没有项目边界。[src/CSharpToJava.Core/Pipeline/ConversionPipeline.cs](src/CSharpToJava.Core/Pipeline/ConversionPipeline.cs#L224)
5. `ConvertProjectWithPartialMergeAsync` 也是同样的目录级扫描模式。[src/CSharpToJava.Core/Pipeline/ConversionPipeline.cs](src/CSharpToJava.Core/Pipeline/ConversionPipeline.cs#L266)
6. 项目级转换内部只拿到一个 `sourceFiles` 列表构建单个 Roslyn 编译，不理解项目引用、包引用、测试项目或资源项。[src/CSharpToJava.Core/Pipeline/ProjectConversionPipeline.cs](src/CSharpToJava.Core/Pipeline/ProjectConversionPipeline.cs#L33)

因此，当前实现的本质是：

1. 输入模型是“目录”。
2. 编译模型是“一个大 compilation”。
3. 输出模型是“一个 Java 源码树”。
4. 测试模型是“不存在”。

这与 MSAGL 这类实际解决方案的结构不匹配。

## 目标输出模型

### 生产代码

每个 Java 生产模块必须输出到：

1. `src/main/java`
2. `src/main/resources`

### 测试代码

Java 单元测试必须输出到：

1. `src/test/java`
2. `src/test/resources`

### 测试项目映射规则

因为 C# 中“测试项目”和“主项目”是分离的，而 Java 通常要求测试代码位于模块内的 `src/test/java`，所以必须定义明确的折叠规则。

建议采用以下规则：

1. 测试项目仅引用 1 个生产项目时：
   - 将该测试项目的源码和资源并入被引用 Java 模块的 `src/test/java` 与 `src/test/resources`。
2. 测试项目引用多个生产项目时：
   - 生成一个专用 Java 测试模块。
   - 该模块不要求有 `src/main/java`。
   - 测试源码放到该模块的 `src/test/java`。
   - 对所有被测生产模块添加 test 依赖。
3. 测试项目没有生产项目引用时：
   - 当作独立测试工具模块处理，仍然使用 `src/test/java`。

对于 MSAGLTests，应走第 2 条。因为它同时依赖 `AutomaticGraphLayout`、`AutomaticGraphLayout.Drawing`、`Dot2Graph`，不适合简单塞进某一个主模块的 `src/test/java`。

## 建议的实现方案

## 阶段 1：引入解决方案与项目发现层

新增一层输入模型，不再让 CLI 直接把“目录”传给转换流水线，而是先做发现与归类。

建议新增：

1. `SolutionModel`
2. `ProjectModel`
3. `ProjectReferenceModel`
4. `PackageReferenceModel`
5. `ResourceItemModel`
6. `ProjectKind`

其中 `ProjectKind` 至少包含：

1. `Production`
2. `Test`
3. `Tool`
4. `Unknown`

项目识别优先级建议为：

1. `.csproj` 中显式测试信号：`<IsTestProject>true</IsTestProject>`
2. 测试包引用：`Microsoft.NET.Test.Sdk`、`MSTest`、`xunit`、`NUnit`
3. 项目命名约定：`*.Tests`、`*.Test`、`*.UnitTests`
4. 源码特征兜底：存在 `[Fact]`、`[Theory]`、`[TestMethod]` 等测试特性

输入入口扩展为：

1. 支持 `.sln` / `.slnx`
2. 支持单个 `.csproj`
3. 保留现有目录模式作为兼容兜底

## 阶段 2：改为按项目构建 Roslyn 编译图

当前的根本问题不是输出目录，而是编译边界错误。必须先让编译模型按项目切分。

建议优先采用 `MSBuildWorkspace` 加载解决方案或项目，而不是手工 XML + `Directory.GetFiles`，原因如下：

1. 能正确拿到项目引用图。
2. 能继承 TargetFramework、常量、全局 using、Analyzer config 等编译上下文。
3. 能区分测试项目、生产项目、工具项目。
4. 能解析跨项目符号引用，避免把整个目录误合并成一个 compilation。

输出需要形成：

1. `LoadedSolutionModel`
2. 每个 `Project` 对应一个 Roslyn compilation
3. 生产项目之间按拓扑排序转换
4. 测试项目在生产项目之后转换

## 阶段 3：建立 Java 输出规划器

新增 `JavaOutputPlanner`，专门决定每个 C# 项目最终落在哪个 Java 模块和哪个 source set。

规划结果至少包含：

1. `JavaModuleName`
2. `OutputRoot`
3. `MainJavaDir`
4. `TestJavaDir`
5. `MainResourcesDir`
6. `TestResourcesDir`
7. `ModuleDependencies`
8. `TestDependencies`

建议支持两种输出模式：

1. `single-module`
   - 所有生产项目折叠到一个 Java 模块的 `src/main/java`
   - 所有测试项目折叠到同一模块的 `src/test/java`
   - 适合快速迁移和单仓验证
2. `multi-module`
   - 每个生产项目生成一个 Java 模块
   - 测试项目根据前述规则映射到某个生产模块或专用测试模块
   - 适合类似 MSAGL 的真实解决方案

对于你的目标，建议默认使用 `multi-module`，并保留 `single-module` 作为兼容模式。

## 阶段 4：扩展转换流水线以支持项目上下文

需要把当前 `ProjectConversionPipeline` 的输入从“文件列表”提升为“项目上下文”。

建议新增：

1. `ProjectConversionRequest`
   - `ProjectModel`
   - `Compilation`
   - `ReferencedProjects`
   - `OutputPlan`
   - `IsTestProject`
2. `ProjectConversionResult`
   - `GeneratedSources`
   - `GeneratedResources`
   - `ModuleMetadata`
   - `Diagnostics`

关键行为调整：

1. partial 合并只在当前 C# 项目内部进行，不能跨项目合并。
2. 语义模型引用必须来自该项目自身 compilation。
3. 处理跨项目类型时，保留对 Java 目标模块的依赖信息，而不是把源文件混在一起。
4. Holder 类、兼容性重写、导入补全等逻辑需要按 Java 模块隔离，避免不同项目之间相互污染。

## 阶段 5：加入测试框架转换层

当前系统没有测试框架语义。要支持单元测试，必须至少覆盖 MSTest、xUnit、NUnit 的核心映射。

建议先做 JUnit 5 目标映射。

### MSTest 到 JUnit 5 的首批映射

1. `[TestClass]` -> 可忽略或保留为普通类
2. `[TestMethod]` -> `@Test`
3. `[DataTestMethod]` -> `@ParameterizedTest`
4. `[DataRow(...)]` -> `@CsvSource` / `@MethodSource`
5. `[TestInitialize]` -> `@BeforeEach`
6. `[TestCleanup]` -> `@AfterEach`
7. `[ClassInitialize]` -> `@BeforeAll`
8. `[ClassCleanup]` -> `@AfterAll`
9. `Assert.AreEqual` -> `Assertions.assertEquals`
10. `Assert.AreNotEqual` -> `Assertions.assertNotEquals`
11. `Assert.IsTrue` -> `Assertions.assertTrue`
12. `Assert.IsFalse` -> `Assertions.assertFalse`
13. `Assert.IsNull` -> `Assertions.assertNull`
14. `Assert.IsNotNull` -> `Assertions.assertNotNull`
15. `Assert.Fail` -> `Assertions.fail`

第一版不要追求覆盖全部断言 API，先覆盖 80% 常见路径，并对未支持断言输出清晰诊断。

## 阶段 6：资源文件映射

MSAGLTests 明显依赖资源文件，因此必须引入资源复制规划。

建议规则：

1. 生产项目中的 `None` / `Content` 且带 `CopyToOutputDirectory` 的项 -> `src/main/resources`
2. 测试项目中的同类资源 -> `src/test/resources`
3. 保留相对目录结构，避免测试代码中路径常量失效
4. 对 zip、dot、txt、geom 等非源码资源一律复制，不做内容级转换

需要特别注意：

1. 不要把 `.csproj` 中列出的资源目录整体误判为 Java package。
2. 路径分隔符和大小写在 Java 侧要保持稳定。
3. 若测试使用基于当前目录的相对路径，需要在测试运行器生成策略中补一个兼容说明。

## 阶段 7：构建文件生成

当前 CLI 只生成单个 `pom.xml`，且没有测试依赖。[src/CSharpToJava.CLI/Program.cs](src/CSharpToJava.CLI/Program.cs#L366)

需要扩展为：

1. `single-module` 时生成带测试依赖的单模块 `pom.xml`
2. `multi-module` 时生成父 `pom.xml` + 子模块 `pom.xml`
3. 测试模块或测试 source set 自动加入：
   - `org.junit.jupiter:junit-jupiter`
   - `org.junit.jupiter:junit-jupiter-params`
4. Maven Surefire 插件默认启用
5. 生产依赖与测试依赖分离

若后续需要 Gradle，可在模型稳定后再增加第二种构建文件生成器。

### 多模块覆盖率设计

如果把 `MSAGLTests` 转成专用测试模块，Maven 仍然可以统计覆盖率，但不能只依赖测试模块自己的普通报告目标，而应统一使用 JaCoCo 聚合报告。

建议固定使用以下插件版本：

```xml
<plugin>
   <groupId>org.jacoco</groupId>
   <artifactId>jacoco-maven-plugin</artifactId>
   <version>0.8.14</version>
</plugin>
```

建议的职责划分如下：

1. 父 POM 统一声明 `jacoco-maven-plugin` 版本和执行策略
2. 各生产模块只负责参与被测，不单独承载测试源码
3. 专用测试模块负责运行测试，并产生执行数据
4. 聚合报告模块或父工程负责生成最终覆盖率报告

### 推荐 Maven 结构

针对 MSAGL 这类场景，建议生成如下结构：

1. `msagl-parent`
2. `msagl-core`
3. `msagl-drawing`
4. `dot2graph`
5. `msagl-tests`
6. 可选：`coverage-aggregate`

其中：

1. `msagl-tests` 只包含 `src/test/java` 和 `src/test/resources`
2. `msagl-tests` 以 test 范围外的普通模块依赖引用被测生产模块
3. `coverage-aggregate` 不放业务源码，只负责聚合覆盖率报告

### 插件执行建议

建议生成的 Maven 配置满足以下原则：

1. `prepare-agent` 在测试前注入 JaCoCo agent
2. `report` 用于单模块调试时查看局部覆盖率
3. `report-aggregate` 用于多模块场景生成最终报告
4. `maven-surefire-plugin` 负责执行单元测试

在多模块场景中，推荐使用：

1. 父 POM 中通过 `pluginManagement` 固定 JaCoCo 版本
2. 在测试执行模块启用 `prepare-agent`
3. 在聚合模块执行 `report-aggregate`

### 推荐父 POM 片段

```xml
<build>
   <pluginManagement>
      <plugins>
         <plugin>
            <groupId>org.jacoco</groupId>
            <artifactId>jacoco-maven-plugin</artifactId>
            <version>0.8.14</version>
            <executions>
               <execution>
                  <id>prepare-agent</id>
                  <goals>
                     <goal>prepare-agent</goal>
                  </goals>
               </execution>
            </executions>
         </plugin>
      </plugins>
   </pluginManagement>
</build>
```

### 推荐聚合模块片段

```xml
<build>
   <plugins>
      <plugin>
         <groupId>org.jacoco</groupId>
         <artifactId>jacoco-maven-plugin</artifactId>
         <version>0.8.14</version>
         <executions>
            <execution>
               <id>report-aggregate</id>
               <phase>verify</phase>
               <goals>
                  <goal>report-aggregate</goal>
               </goals>
            </execution>
         </executions>
      </plugin>
   </plugins>
</build>
```

### 生成器需要保证的事实

为了让专用测试模块的覆盖率正确归集到生产模块，生成器必须保证：

1. 被测生产模块作为正常 Maven 模块参与 reactor build
2. 专用测试模块在测试时依赖这些生产模块
3. 聚合报告模块能看到被测模块的源码和 class 输出
4. 测试执行数据来自 `msagl-tests`，但报告归属到所有被测模块

换句话说，覆盖率统计的关键不在于测试源码是否和生产源码位于同一个模块，而在于 JaCoCo 是否拿到了：

1. 被测模块字节码
2. 被测模块源码路径
3. 测试模块执行生成的 coverage data

### 对生成策略的直接要求

因此，后续 Maven 生成器应增加以下规则：

1. `single-module` 模式下：直接生成 `prepare-agent` + `report`
2. `multi-module` 模式下：生成父 POM，并默认启用 `report-aggregate`
3. 若存在专用测试模块：默认生成 `coverage-aggregate` 模块，避免把聚合职责塞进业务模块
4. 若用户不需要覆盖率：允许通过开关关闭 JaCoCo 片段生成

### CLI 建议补充

后续 CLI 可以增加可选参数：

1. `--enable-coverage`
2. `--coverage-plugin-version 0.8.14`
3. `--coverage-aggregate-module-name coverage-aggregate`

默认建议：

1. `single-module` 时 `--enable-coverage=true`
2. `multi-module` 时 `--enable-coverage=true`
3. JaCoCo 默认版本固定为 `0.8.14`

## 阶段 8：CLI 设计调整

建议保留现有命令名，但重构参数含义。

建议新增参数：

1. `--solution <path>`
2. `--project <path>`
3. `--mode single-module|multi-module`
4. `--include-tests`
5. `--test-framework junit5`
6. `--merge-test-projects auto|always|never`

兼容策略：

1. 传目录时继续走旧模式
2. 传 `.sln` / `.csproj` 时走新模式
3. 默认 `--include-tests=false`，待稳定后再改默认值

## 建议的测试计划

当前仓库已经有大量转换测试，但主要是单文件与语法级行为测试。要支撑新功能，需要新增“项目结构级”测试。

### A. 发现与分类测试

放在现有测试项目中，验证：

1. 能从 `.csproj` 识别生产项目与测试项目
2. 能识别 MSTest / xUnit / NUnit 项目
3. 能提取项目引用、包引用、资源项
4. 能处理多层项目引用图

### B. 输出规划测试

验证：

1. 单生产项目 + 单测试项目 -> 同模块 `src/main/java` + `src/test/java`
2. 多生产项目 + 单测试项目多引用 -> 专用测试模块
3. 测试资源正确进入 `src/test/resources`
4. 生产资源正确进入 `src/main/resources`

### C. 测试框架转换测试

验证：

1. MSTest 特性到 JUnit 5 注解的转换
2. 常见 `Assert.*` 到 `Assertions.*` 的映射
3. `ClassInitialize` / `TestInitialize` 生命周期转换
4. 参数化测试的基本转换

### D. 端到端集成测试

建议加入一个缩小版 fixture 解决方案，至少包含：

1. `CoreLib`
2. `DrawingLib` -> `CoreLib`
3. `ToolingLib` -> `CoreLib`, `DrawingLib`
4. `CoreLib.Tests` -> 上述多个生产项目

断言内容：

1. 输出目录正确
2. 生成的 `pom.xml` 正确
3. Java 文件数量与目标模块对应
4. 测试源码不进入 `src/main/java`
5. 资源文件出现在正确目录

## 推荐实施顺序

建议按以下顺序推进，避免一次改太多：

1. 先做项目发现和 `ProjectKind` 分类
2. 再做 `JavaOutputPlanner`
3. 然后把转换流水线改成按项目运行
4. 接着补资源复制
5. 最后加入测试框架转换与测试依赖生成

原因很直接：

1. 如果项目边界不对，测试输出目录再正确也没有意义。
2. 如果输出规划不稳定，后续测试框架转换会频繁返工。
3. 资源文件处理必须在模块落点确定后才能稳定实现。

## 最小可交付版本

第一阶段建议只交付以下能力：

1. 支持读取 `.csproj`
2. 支持识别测试项目
3. 支持 `single-module` 输出
4. 生产代码输出到 `src/main/java`
5. 测试代码输出到 `src/test/java`
6. 测试资源输出到 `src/test/resources`
7. 生成带 JUnit 5 依赖的 `pom.xml`
8. 对 MSTest 的 `TestMethod` 和常用 `Assert` 做基础映射

这样可以先覆盖“主项目与测试项目分离，但 Java 要进入 `src/test/java`”这一核心需求。等这条链路稳定后，再扩展到真正的多模块输出。

## 对 MSAGL 场景的落地建议

针对 MSAGL 这样的结构，建议默认策略是：

1. 先把生产项目按依赖关系转成多个 Java 模块
2. 将 `MSAGLTests` 转成一个专用测试模块
3. 该测试模块仅使用 `src/test/java` 与 `src/test/resources`
4. 测试模块以 test scope 依赖所有被测生产模块
5. 暂不尝试把一个跨多个被测项目的测试项目，强行塞回某个单一主模块

这个策略最符合原始 C# 依赖结构，也最符合 Java 构建工具对测试源码目录的约束。