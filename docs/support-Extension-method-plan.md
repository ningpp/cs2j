## Plan: 跨项目扩展方法支持

推荐把扩展方法收敛为“声明侧保持静态宿主 + 调用侧按 Roslyn 绑定结果重写为静态调用 + 在项目图层补充扩展方法索引与诊断”。不要把扩展方法注入被扩展类型本身；这样 A 项目可以独立转换并落盘，B/C 对 A 类型新增扩展方法时，不需要回写 A 的 Java 输出，只影响 B/C 及其消费者的调用点与依赖关系。

**Steps**
1. Phase 1: 收敛语义与输出策略。把用户自定义扩展方法的 canonical lowering 明确为“保留在声明它的静态类中输出为 Java 静态方法，调用侧重写为 Host.method(receiver, ...)”；将 RewriteExtensionMethods 限制为单项目/实验性能力，或在多项目模式下直接禁用并发出 warning。此步骤阻塞后续所有实现。
2. Phase 2: 建立项目图级扩展方法索引。基于解决方案/项目图，在进入逐项目转换前扫描所有待转换项目的 compilation，提取 ExtensionMethodDescriptor 元数据：SymbolKey 或 DocumentationCommentId、声明项目、声明类型、接收器类型、方法签名、泛型参数个数、Java 包名、Java 宿主类名、Java 方法名、可见性。该索引只负责映射与校验，不负责重做重载决议。扫描可以按项目并行执行。
3. Phase 3: 完成调用侧 lowering。补全 InvocationExpressionTransformer 对 ReducedExtension 的静态调用路径，统一在语义模型已绑定到扩展方法时生成 Host.method(receiver, args...)，并复用现有 ArgumentTransformer 的 argStartIndex 机制避免重复传入 receiver。同步覆盖 named arguments、params、泛型、lambda/delegate、nullable/boxing、using alias 等边界场景。
4. Phase 4: 增加项目级扩展方法校验 pass。在 ProjectCompilationCheckPass 之后、ProjectTypeEmitPass 之前插入新的 ProjectExtensionMethodBindingPass 或 ProjectExtensionMethodNormalizationPass，职责是扫描当前项目里所有已绑定的扩展方法调用，确认其声明宿主位于当前项目、已引用且会被转换的其他项目、或受支持的外部映射中；同时收集后续 emit/import 需要的跨项目元数据。无法映射、项目被排除、命名冲突、或多项目模式误用 RewriteExtensionMethods 时都在这里产出诊断，并通过 ProjectPassState.RegisterFileDiagnostics 汇总。
5. Phase 5: 调整多项目 orchestration。保留按项目/模块拓扑顺序转换，但在 CLI 入口先构建完整项目图和扩展方法索引，再把索引传入每个项目的转换上下文。关键点是不再依赖“先看见所有扩展方法才能生成 A 类型”，因为 A 不再承载这些方法；真正需要扩展方法信息的是声明项目和消费项目。这样文件写出顺序不再影响扩展方法完整性。
6. Phase 6: 明确模块归属与 import 策略。用户自定义扩展方法默认留在声明它的项目/模块/包中，不进入 SharedCompatibilityPackage；兼容包只承载生成器自己的 runtime/helper。消费方根据语义绑定结果导入声明宿主类，并沿用原始 ProjectReferences 形成编译依赖，不向 A 反向注入对 B/C 的隐藏依赖。需要同步更新跨包 import 解析与 symbol-to-Java 输出映射，避免声明侧与调用侧计算出的宿主类路径不一致。
7. Phase 7: 补齐测试与回归验证。新增最少一组同项目测试、两组跨项目测试和一组多模块输出测试：A 定义类型/B 定义扩展并消费；A 定义类型/B 定义扩展/C 引用 A+B 并消费；同一接收器上多个扩展类和重载；泛型扩展；using/命名空间控制的歧义；项目被排除或外部扩展库缺失时的诊断。再增加一条回归：先转换 A 再转换 B/C 时，无需回写 A 的已生成 Java 文件。
8. Phase 8: Rollout 与兼容策略。先把 RewriteExtensionMethods 从“通用扩展方法方案”降级为“受限优化选项”，避免继续误导调用方；待跨项目静态 lowering 稳定后，再决定是否保留该选项仅用于少量内建 helper/已知类型。若短期不支持任意第三方程序集中的扩展方法，则在文档和诊断中明确只保证当前转换图内源码项目与已知映射库。

**Relevant files**
- d:\code\cs2j\src\CSharpToJava.Core\Transformers\Expression\Transformers\InvocationExpressionTransformer.cs — 现有扩展方法调用判定入口，已有 ReducedExtension 检测与静态路径占位，适合作为调用侧 lowering 主落点。
- d:\code\cs2j\src\CSharpToJava.Core\Transformers\Expression\Transformers\ArgumentTransformer.cs — 已有 argStartIndex 机制，可直接复用于静态扩展调用，避免 receiver 重复入参。
- d:\code\cs2j\src\CSharpToJava.Core\Transformers\Member\MethodTransformer.cs — 当前声明侧已能识别扩展方法，且 RewriteExtensionMethods 只是可选分支，需要在这里收紧默认策略。
- d:\code\cs2j\src\CSharpToJava.Core\Context\ConversionOptions.cs — 需要明确 RewriteExtensionMethods 在多项目模式下的行为边界。
- d:\code\cs2j\src\CSharpToJava.Core\Pipeline\Cs2jLibrary.cs — 当前 Phase 1 模型已具备 library/project/document 抽象，是挂接扩展方法索引或项目级元数据的首选位置。
- d:\code\cs2j\src\CSharpToJava.Core\Pipeline\ProjectConversionPipeline.cs — 需要把扩展方法索引与新的 pass 接入 CreateProjectPasses 和转换入口。
- d:\code\cs2j\src\CSharpToJava.Core\Pipeline\Passes\ProjectPasses.cs — ProjectPassState、诊断聚合和项目级 pass 链都在这里，是新增扩展方法校验/归一化 pass 的主位置。
- d:\code\cs2j\src\CSharpToJava.Core\Context\ConversionContext.cs — 如需把扩展方法索引暴露给 transformer/pass，这里是最自然的查询入口。
- d:\code\cs2j\src\CSharpToJava.Core\Pipeline\CrossPackageImportResolver.cs — 需要保证跨项目扩展宿主类的 import 能正确补齐。
- d:\code\cs2j\src\CSharpToJava.CLI\ProjectDiscovery.cs — 复用解决方案与项目依赖图发现能力，作为扩展方法索引扫描的输入。
- d:\code\cs2j\src\CSharpToJava.CLI\MultiModulePlanner.cs — 复用现有模块依赖规划，确保扩展方法宿主模块的依赖关系与原始项目引用一致。
- d:\code\cs2j\src\CSharpToJava.CLI\Program.cs — 需要在项目/解决方案转换入口增加“先建项目图和扩展方法索引，再逐项目转换”的接线。
- d:\code\cs2j\tests\CSharpToJava.Tests — 需要新增扩展方法跨项目、多模块、冲突与诊断测试。

**Verification**
1. 为同项目扩展方法增加回归测试，验证声明保持静态宿主输出，调用点被重写为静态调用，且参数顺序、泛型和 named arguments 正确。
2. 为 A/B 跨项目场景增加测试，验证 B 对 A 类型声明扩展方法时，A 的输出不新增实例方法，B 的调用点和导入正确。
3. 为 A/B/C 三项目场景增加测试，验证 C 在引用 A+B 时能正确绑定到 B 的扩展方法宿主，并保留对 B 模块的编译依赖。
4. 为多扩展类、重载、using/namespace 歧义增加测试，验证始终以 Roslyn 已解析的 IMethodSymbol 为准，不在转换阶段重新做方法选择。
5. 为项目被平台规则排除、或第三方扩展方法宿主不在当前转换图中的情况增加诊断测试，验证能输出明确错误而不是生成错误 Java 调用。
6. 运行现有 dotnet test 全量回归，并额外做一次解决方案级 canary 转换，确认多模块输出中的 POM/依赖未因扩展方法支持产生反向依赖。

**Decisions**
- 包含范围：项目级与解决方案级转换的扩展方法声明/调用支持、跨项目宿主映射、诊断与模块依赖保持。
- 包含范围：单文件模式下当前 compilation 内可见扩展方法的正确 lowering；跨程序集不可见扩展方法只做诊断，不承诺魔法补全。
- 明确排除：不回写已生成的 A 项目 Java 类型；不把用户扩展方法汇总进共享 compatibility 包；不引入运行时注册或反射式分发。
- 推荐决策：扩展方法的重载决议始终信任 Roslyn 语义模型；转换器只负责把“已选中的符号”映射到 Java 输出。
- 推荐决策：RewriteExtensionMethods 不再作为通用扩展方法方案，最多保留为受限优化开关。

**Further Considerations**
1. 扩展方法索引放在 Cs2jLibrary 还是单独的 workspace conversion plan。推荐：如果近期会继续推进真正的多项目执行器，单独抽 WorkspaceConversionPlan 更干净；如果追求最小改动，先挂在 Cs2jLibrary/ConversionContext 上也可接受。
2. 第三方程序集里的扩展方法是否首批支持。推荐：首批只保证当前转换图内源码项目和已知映射库；其余情况先给出明确诊断，避免输出不可编译的 Java。
3. 是否彻底移除 RewriteExtensionMethods。推荐：先在多项目模式下禁用并保留兼容开关，等跨项目方案稳定后再决定是否彻底废弃。