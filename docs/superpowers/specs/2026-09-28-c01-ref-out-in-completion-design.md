# C01 RefOutIn 接线收尾设计（C# 位置模型闭环 + Java holder 映射）

**日期**: 2026-09-28
**状态**: 已核订（2026-09-28 对照 dev-3.x@ff216bcc 逐条核验全部代码证据并修正；Q3/Q5/Q6 已定案，见 §2.2）
**对应 catalog 条目**: `prompts/preprocessor-catalog.md` §2.B C01（P0，「半截工程，完成边际成本最低、fail-fast 面最广」）
**前置设计**: [2026-09-28-ref-out-in-preprocessor-design.md](file:///d:/code/cs2j/docs/superpowers/specs/2026-09-28-ref-out-in-preprocessor-design.md)（下称 **D3**，决策 D1–D6 来源）、[2026-07-08-ref-out-in-comprehensive-tests-design.md](file:///d:/code/cs2j/docs/superpowers/specs/2026-07-08-ref-out-in-comprehensive-tests-design.md)（旧 holder 测试设计）
**范围**: 只做「完成接线」的设计——补齐 C# 侧重写器覆盖面与可编译性闭环、设计并落地 Java 侧 holder 映射、迁移旧断言。**不新增 pass、不翻转 stub、不重排链序、不删除旧转换器 holder 路径。**

---

## 1. 背景与目标

### 1.1 现状证据

统一预处理基建已落地：[PassDescriptor.cs](file:///d:/code/cs2j/src/CSharpToJava.Core/Preprocessing/PassDescriptor.cs) 定义注册表契约，[PassCatalog.cs](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassCatalog.cs#L119-L130) 中 `ref-out-in` 已是 **Status=Implemented、defaultEnabled=true、Stage=Abi、RunAfter={strip-default}、SupportLibrary=Cs2jRefOutIn、verifyBuild/TestsByDefault=true** 的 16 个在链 pass 之一；旧包装 [ProjectRefOutInPreprocessor.cs](file:///d:/code/cs2j/src/CSharpToJava.CLI/ProjectRefOutInPreprocessor.cs) 已薄化为 `ProjectPassRunner` + [RefOutInPassAdapter.cs](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/Adapters/RefOutInPassAdapter.cs) 的委托门面。Core 9 文件、支撑库 [src/Cs2jRefOutIn/ByRef.cs](file:///d:/code/cs2j/src/Cs2jRefOutIn/ByRef.cs)、独立 verb `ref-out-in` 均已存在。

但 catalog §4 风险 5 定性的「真实阻断」经本次核实**比 D3 自述更严重**：不仅 Java 产物不可编译，C# 中间树闭环本身在自己的 fixture 上就不成立。

**实测证据（2026-09-28，release 构建 verb 直跑 V2 fixture）**：

```text
dotnet CSharpToJava.CLI.dll ref-out-in -s tests/SampleRefOutInV2Solution/SampleRefOutInV2.sln -d <tmp>
→ ROI101: dotnet build failed
  Calculator.cs(34,9): error CS8149: 通过引用返回只能在通过引用返回的方法中使用
```

临时修复 `GetRef` 后再编译 App，暴露完整错误面：

| 错误 | 位置 | 根因（代码行号见 §4） |
|---|---|---|
| CS8149 ×1 | 中间树 `Calculator.cs:34`（源文件 L33 `return ref arr[index]`，注入 using 后 +1 行）——全 fixture 唯一 ref 返回点 | 返回类型已去 ref，方法体未改写（C4） |
| CS0103 ×3 | `Program.cs:9,10,38` `ByRef` 未找到 | 只有调用点改写的文件不注入 `using Cs2jByRef;` |
| CS1615 | `Program.cs:15` `if (TryParse(..., out var p))` | 条件内声明式 out 未提升 |
| CS1615 | `Program.cs:21` `if (calc.TryGet(out int result))` | 同上 |
| CS1061 ×2 | `Program.cs:17` `p.X`/`p.Y` | 上一条的连锁后果（`p` 被推断为 `ByRef<Point>`） |
| CS1615 | `Program.cs:29` `holder.Update(ref holder.Value)` | 成员链 ref 实参未提升 |
| CS8805 | `Program.cs:4` 顶级语句 | fixture 自身缺 `<OutputType>Exe</OutputType>`，**基线就不可编译** |

而项目级测试 [RefOutInPreprocessorTests.cs](file:///d:/code/cs2j/tests/CSharpToJava.ProjectTests/RefOutInPreprocessorTests.cs#L27-L29) 三个用例**全部显式 `VerifyBuild=false, VerifyTests=false`**，与 descriptor 的 `verifyBuildByDefault: true` 直接矛盾——等于把 D5「中间树 build/test 全绿」的验收门绕过了。Core 单测 [RefOutInRewriterUnitTests.cs](file:///d:/code/cs2j/tests/CSharpToJava.ProjectTests/RefOutInRewriterUnitTests.cs) 编译时只引用 mscorlib（L16），只做文本断言，从不验证产物可编译。

**Java 侧缺口（grep 实证）**：

- compat 库只有 8 个基本类型 holder（[IntHolder.java](file:///d:/code/cs2j/java/csharptojava-compat/src/main/java/io/github/ningpp/compat/IntHolder.java) 等——注意布尔 holder 实名 `BoolHolder`；除 `IntHolder` 为非 final 外均为 `public final class`，public 字段 `value`、无共同抽象层）+ [ObjectHolder.java](file:///d:/code/cs2j/java/csharptojava-compat/src/main/java/io/github/ningpp/compat/ObjectHolder.java)（`final class ObjectHolder<T>`）；**不存在 ByRef 位置层次**。
- 旧路径集中在 [HolderTypeResolver.cs](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/HolderTypeResolver.cs)（字符串拼 FQN）、[ArgumentTransformer.Ref.cs](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/ArgumentTransformer.Ref.cs)（341 行 pre/post 提升 + 数据流明确赋值）、[MethodTransformer.cs:148](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Member/MethodTransformer.cs#L148)（签名 holder 化）、[ArgumentTransformer.Coercion.cs:158](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/ArgumentTransformer.Coercion.cs#L158)、[JavaNaming.cs:441-445](file:///d:/code/cs2j/src/CSharpToJava.Core/Context/JavaNaming.cs#L441-L445)、[StructCloneHelper.cs](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/StructCloneHelper.cs)（effectively-read-only 优化）。
- [config/TypeMappings.json](file:///d:/code/cs2j/config/TypeMappings.json#L334-L354) 已为 `Cs2jUnsigned.UnsignedMath`、`Cs2jArray.ArrayHelper`、`Cs2jPrelude.CsPrelude` 注册「C# 支撑类型 → compat Java 类型」映射，**唯独没有任何 `Cs2jByRef.*` 映射**。转换器当前会把 `ByRef<int>` 当普通用户泛型类裸转，产出无 import、无映射的 Java。
- 支撑工程过滤已正确接线（[PassChainFactory.cs:233-234](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassChainFactory.cs#L233-L234)、[Program.cs:258-265](file:///d:/code/cs2j/src/CSharpToJava.CLI/Program.cs#L258-L265)、[MultiModulePlanner.cs:37](file:///d:/code/cs2j/src/CSharpToJava.CLI/MultiModulePlanner.cs#L37)），`ByRef.cs` 本身不参与 Java 转换——缺口只在「用户代码对 ByRef 的引用如何映射」。
- 旧断言仍在：[RefOutInProjectTests.cs:29-40](file:///d:/code/cs2j/tests/CSharpToJava.ProjectTests/RefOutInProjectTests.cs#L29-L40) 断言直连 pipeline 的 Java 产物含 `IntHolder`/`ObjectHolder`；[RefOutInComprehensiveTests.cs](file:///d:/code/cs2j/tests/CSharpToJava.ConversionTests/Categories/RefOutInComprehensiveTests.cs) 实测 26 个用例直接喂原始 C# 给转换器，全部断言旧 holder 形态。
- [RefOutInPostVerifier.cs](file:///d:/code/cs2j/src/CSharpToJava.Core/RefOutInPreprocessor/RefOutInPostVerifier.cs) 只检查参数位残留（L31-47），且全仓 grep 确认**生产路径从不调用它**（仅单测 L307 引用）。
- ref struct holder 生成器 [RefOutInSupportFiles.cs:49-71](file:///d:/code/cs2j/src/CSharpToJava.CLI/RefOutInSupportFiles.cs#L49-L71) 产出的字段是 `object _slot` / `object[] _array`——ref struct 不能装箱（CS0029/CS0306），该产物对任何真实 ref struct 类型都不可用，且缺设计 §3.3 要求的强类型字段与全部 `Create` 工厂。

### 1.2 fail-fast 面

catalog C01 点名的 5 处转换器 fail-fast（行号本次逐一核实，均为 `NotSupportedException("CS2J normalization required: ...")`）：

1. [StatementTransformer.Declarations.cs:29-31](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Statement/StatementTransformer.Declarations.cs#L29-L31)——ref 局部；
2. [LambdaTransformer.cs:96-100](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/LambdaTransformer.cs#L96-L100)——ref/out lambda 形参；
3. [LambdaTransformer.cs:197-201](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/LambdaTransformer.cs#L197-L201)——lambda 体内 ref/out 写回/语句膨胀；
4. [SwitchAndResource.cs:233-237](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Statement/StatementTransformer.SwitchAndResource.cs#L233-L237)——using resource 表达式中的 ref/out 读回；
5. [Loops.cs:136-139](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Statement/StatementTransformer.Loops.cs#L136-L139) 与 [Loops.cs:205-208](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Statement/StatementTransformer.Loops.cs#L205-L208)——foreach 表达式与循环条件中的 ref/out 读回。

这些站点每命中一次就是一次转换失败；接线完成后它们的输入应只包含外部/锁定残留（旧路径继续兜底），内部形态全部在 C# 层消失。

### 1.3 本任务范围边界

| 侧 | 做什么 | 不做什么 |
|---|---|---|
| C# 侧（Core + 支撑库 + 适配器） | 补齐 D3 §4 18 类矩阵中半成品/缺失的重写规则；修通中间树可编译闭环；ref struct holder 按 §2 Q2 定案重做；后置自检升级并接入生产路径；fixture 修复 | 不新增 pass、不改 id/目录名/Stage/默认值/链序；不碰实验性 [Lowering/LowerRefOut.cs](file:///d:/code/cs2j/src/CSharpToJava.Core/Lowering/LowerRefOut.cs)（catalog §4 风险 9） |
| Java 侧（compat + 转换器映射） | compat 新增 ByRef 位置层次；TypeMappings 注册；转换器识别 `ByRef<T>`/`.Value`/`Create`/holder 调用形态；未覆盖形态显式 fail-fast | 不删除 `ArgumentTransformer.Ref.cs`/`HolderTypeResolver`/旧 holder（外部/锁定残留与直连 pipeline 安全网仍需要，D1/D6）；不改写外部 BCL 在 compat 中的既有 holder 签名 |
| 测试 | Core 单测补到矩阵全覆盖且断言真实编译；fixture 打开 verify；Java 侧 compat 单测 + 端到端；旧断言按 §7.4 迁移 | 不降低既有旧路径测试覆盖（迁移而非删除） |

目标两半区（catalog §5.1）：.NET 半区以中间树 `dotnet build/test -c Release` 与 csharp-sdk 基线守门；Java 半区以转译后同一套测试 + compat JUnit 守门。

---

## 2. 已确认决策与开放问题

### 2.1 已确认决策（继承 D3 / 基建现状，本任务不再翻案）

| # | 决策点 | 结论 | 证据 |
|---|---|---|---|
| CD1 | 外部符号边界 | **仅内部消除**：外部方法/索引器调用点原样保留 `ref/out/in`；H 槽转发外部 API 时取 `.Value` 地址 | D3-D1；[RefOutInSyntaxRewriter.cs](file:///d:/code/cs2j/src/CSharpToJava.Core/RefOutInPreprocessor/RefOutInSyntaxRewriter.cs) 现方向错误需按 §4-C6 修正 |
| CD2 | 普通存储策略 | **代理机制**：普通数组/字段/局部零提升，取址点构造可写代理；异常路径即时写回 | D3-D2；[ByRef.cs](file:///d:/code/cs2j/src/Cs2jRefOutIn/ByRef.cs) 已实现三形态 |
| CD3 | 外部契约成员 | 外部接口实现/外部虚链 override/`[DllImport]`/`[LibraryImport] extern` **一律锁定**，签名方法体调用点全保留，Info 审计诊断 | D3-D6；[RefOutInAliasAnnotator.cs:188-250](file:///d:/code/cs2j/src/CSharpToJava.Core/RefOutInPreprocessor/RefOutInAliasAnnotator.cs#L188-L250) |
| CD4 | 链上身份 | id=`ref-out-in`、Stage=Abi、默认开、目录 `.cs2j-refoutin-src`、支撑库 Cs2jRefOutIn、verify 默认开——全部维持 | [PassCatalog.cs:119-130](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassCatalog.cs#L119-L130) |
| CD5 | SDK 验证口径 | 全量 `D:\csharp-sdk`（`csharp-sdk.sln` 等两 sln，目录实测存在）+ 动手前基线快照，「新增失败为零、通过项目数不降」 | D3-D5；§8 |
| CD6 | 旧 Java 路径 | 本任务**不删除、不弱化** `ArgumentTransformer.Ref.cs` 等旧路径；它是残留外部/锁定形态与直连 pipeline 入口的兜底 | D3 §9.1；catalog §4 风险 5 |
| CD7 | 任务性质 | **完成实现，不是 stub 翻转**：[PassStubCatalog.cs](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassStubCatalog.cs) 中本就没有 ref-out-in 条目；本任务不新增/修改任何注册表行 | foundation plan L77-106 |
| CD8 | in 碰撞兜底 | 去 `in` 后构成重载/调用歧义的参数保留 `in` + Warning，不做跳过式放弃 | D3 §2；现实现缺失（统计 `InOverloadKept` 恒 0），列入 §4 |
| CD9 | 支撑工程 Java 过滤 | Cs2jRefOutIn 只作符号源，不转 Java、不规划 Maven、不进指纹快照 | [Program.cs:258-265](file:///d:/code/cs2j/src/CSharpToJava.CLI/Program.cs#L258-L265) 已生效，维持 |

### 2.2 开放问题与候选方案

#### Q1（核心）Java holder 体系选型

| 方案 | 形态 | 优点 | 缺点 | 结论 |
|---|---|---|---|---|
| **A. 同构多态位置层次（推荐）** | compat 新增 `abstract class ByRef<T>`（`getValue/setValue` + 四个静态 `create` 工厂），工厂构造 private nested `Slot/ArrayElement/Accessor` 三实现；C# 侧 [ByRef.cs](file:///d:/code/cs2j/src/Cs2jRefOutIn/ByRef.cs) 与之一一对应；ref struct 位置用普通 `RefStructByRef<T>` 承接（见 Q2） | 与 C# 中间形态 1:1，转换器只需类型+成员映射，无调用点分叉；别名/异常写回语义由库保证；旧 holder 完全不受影响 | 基本类型走 `ByRef<Integer>` 装箱（中间产物，性能非目标，D3 §9.8 已接受） | **推荐** |
| B. 复用旧 8+1 holder 家族 | 内部 `ByRef<int>` 在 Java 侧也落到 `IntHolder`，引用类型落 `ObjectHolder<T>` | 无新类 | 旧 holder 全是 `final`、无共同抽象、无数组/访问器变体与工厂；槽直传与代理两种 C# 形态无法统一表达；签名按 T 是否基本类型分裂，泛型 `ByRef<T>`（T 为类型参数）擦除后无法选家族，必产双轨发射 | 不推荐 |
| C. 仅接口薄皮 | 新增 `interface Ref<T>{T get();void set(T)}`，旧 holder 全部改造实现之 | 一套抽象 | 要改 9 个已发布 compat 类的签名（外部契约面），且仍补不了三工厂形态 | 不推荐 |

**推荐 A**：新增类型独立于旧家族，零破坏；映射全部走 TypeMappings + 少量转换器识别规则（§5.3）。

#### Q2 C# ref struct holder 形态（直接决定 Q1 映射数量）

- **方案 A（推荐，已定案）：单个泛型 `ref struct RefStructByRef<T>`（仅槽模式 + 双工厂 `Create()`/`Create(T)`）**。C# 允许 ref struct 持有不受限类型参数 `T` 的实例字段（`Span<T>` 自身即如此）；一次定义即可，删除 [RefOutInMangling.cs](file:///d:/code/cs2j/src/CSharpToJava.Core/RefOutInPreprocessor/RefOutInMangling.cs)、删除按类型代码生成，Java 侧映射收敛为一个 `RefStructByRef<T>` 普通类。**对 D3 §3.3 的修正：去掉 `(T[] array,int index)` 数组模式**——ref struct 不能作为数组元素（CS0611），`RefStructByRef<Span<int>>.Create(spanArr, i)` 永远不可实例化，双模式属死代码；且 csharp-sdk 实测（Utf8JsonReader 等 10+ 处 `ref ReadOnlySpan<byte>`）全是「ref struct 本体按槽传递 + `ref int` 按普通 ByRef」的混合形态，槽模式足够。
- 方案 B：维持 D3 §3.3 按类型生成 `RefStructByRef_<Mangled>`，但把 [RefOutInSupportFiles.cs:49-71](file:///d:/code/cs2j/src/CSharpToJava.CLI/RefOutInSupportFiles.cs#L49-L71) 修为强类型实现；Java 侧需 demangle 或读特性还原元素类型，映射碎片化。

推荐 A，M2 开工时先用 10 行探针验证 `ref struct RefStructByRef<T>` 在 net10.0 对 `T=Span<int>` 可编译可运行；若探针失败再退回 B。

#### Q3 Java 侧 `.Value` 的 API 形态（已定案）

**定案：抽象属性 `Value` + 转换器既有属性约定自动产出 `getValue()`/`setValue(T)`，无需专用分支、无需 methodMappings 条目。** 核验依据：C# 侧 `ByRef<T>.Value` 是 **属性**（`public abstract T Value { get; set; }`，IPropertySymbol），而转换器对未映射属性本就按 Java bean 约定发射——读路径经 [ExpressionTransformerFacade.cs](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/ExpressionTransformerFacade.cs) 的属性→getter 约定（`TryMapPropertyToMethod` 未命中后回落 `get+PascalName()`），写路径经 [AssignmentTransformer.cs](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/AssignmentTransformer.cs) `Property()`（`set+PascalName(...)`，含复合赋值 get-op-set 展开）。因此 Java 类只需按 `getValue/setValue` 命名即可命中两条约定。备选的 methodMappings 方案已否决：[TypeMappingRegistry.cs:43-56](file:///d:/code/cs2j/src/CSharpToJava.Core/TypeMapping/TypeMappingRegistry.cs#L43-L56) 每条目仅一个 `javaMethod`，无法区分读写。M3 以回归测试锁死该约定（读→`getValue()`、赋值 LHS→`setValue(...)`、复合 `+=` 展开正确）。

#### Q4 accessor 代理 lambda 的 Java 落地

C# 工厂实参 `() => x`、`v => x = v` 推荐映射到 JDK `java.util.function.Supplier<T>` / `Consumer<T>`。核验更正：[TypeGroupResolver.cs:556-564](file:///d:/code/cs2j/src/CSharpToJava.Core/Pipeline/TypeGroupResolver.cs#L556-L564) 是 `AddStandardJdkImports` 的**无条件标准 import 注入**（`java.util.function.{Supplier,Consumer,Function,...}` 全在其中），即 import 面零成本；Func/Action 家族 → `java.util.function.*` 的类型映射由既有 TypeMappings 提供。风险：setter lambda `v => x = v` 是表达式 lambda（Java 赋值是表达式，合法）但捕获形态（捕获局部/字段/this/数组）必须在 C# 中间树实测转换；这正是 LambdaTransformer 两处 fail-fast 的对接面。备选：compat 自定义 `Cs2jGetter/Setter` 接口——仅当 Q4 实测 JDK 函数式路径不通时启用。

#### Q5 ref 字段 / 含 ByRef 字段的 ref struct 降级（矩阵 #15/#18）（已定案：显式 Error）

现实现只改了字段声明类型（[RefOutInSyntaxRewriter.cs:597-612](file:///d:/code/cs2j/src/CSharpToJava.Core/RefOutInPreprocessor/RefOutInSyntaxRewriter.cs#L597-L612)），构造/初始化适配与「ref struct 含类字段自动降级为普通 struct」均未做。**2026-09-28 对全量 `D:\csharp-sdk`（两个 sln 的全部 .cs）扫描：ref 字段声明 0 命中**——按预设走 catalog §4 风险 12 原则：ref 字段改为**显式 Error（CS2J3303 族）**，删除半成品声明改写（改写但不改初始化/用法会产出更难诊断的半坏代码）。

#### Q6 多维/交错数组元素 ref（已定案：默认 fail-fast）

[ByRef.cs:14](file:///d:/code/cs2j/src/Cs2jRefOutIn/ByRef.cs#L14) 只有一维数组工厂，rewriter 却会对任意维数发射 `ByRef<T>.Create(arr, i, j)`（[RefOutInSyntaxRewriter.cs:427](file:///d:/code/cs2j/src/CSharpToJava.Core/RefOutInPreprocessor/RefOutInSyntaxRewriter.cs#L427)）。**2026-09-28 对全量 csharp-sdk 扫描：多维数组 ref 取址 0 命中**——默认 fail-fast（CS2J3303）定案；未来命中再按需补 `Create(T[,],int,int)` 重载。

---

## 3. 链上契约

以下字段为本任务的**冻结契约**，实现中不得变更（变更即超出 C01 范围，需另开评审）：

| 契约项 | 当前值（证据） | 本任务动作 |
|---|---|---|
| pass id | `ref-out-in`（[PassCatalog.cs:16](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassCatalog.cs#L16)） | 不动 |
| Stage | `PassStage.Abi`（[PassCatalog.cs:123](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassCatalog.cs#L123)） | 不动 |
| Status | `Implemented`（L124） | 不动——**本任务不是 stub 翻转**；[PassStubCatalog.cs](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassStubCatalog.cs) 无此 id，CS2J9001 与本任务无关 |
| 默认 | `defaultEnabled: true`（L125） | 保持开启；回退开关 `--no-ref-out-in` 保留 |
| 顺序锚点 | `RunAfter = { strip-default }`（L126）；现链：`strip-default → ref-out-in → unsigned → generic` | 不动。catalog C01 建议「在 unsigned 之后」是未来话题，foundation plan L133 已明确顺序保真，本任务不重排 |
| 支撑库 | `Cs2jRefOutIn`（L127；枚举 [PassDescriptor.cs:57](file:///d:/code/cs2j/src/CSharpToJava.Core/Preprocessing/PassDescriptor.cs#L57)） | 维持注入/过滤/指纹豁免机制；只修库内容 |
| verify | build/test 均默认 true（L128-129） | 不动值；**修实现让默认 verify 真能过**（当前 fixture 实跑 ROI101 失败） |
| 中间目录 | `.cs2j-refoutin-src`（[ProjectRefOutInPreprocessor.cs:40](file:///d:/code/cs2j/src/CSharpToJava.CLI/ProjectRefOutInPreprocessor.cs#L40)） | 不动 |
| 关联 stub | `closure-lift`（C32/C45）锚定 `RunAfter = ref-out-in`（[PassStubCatalog.cs:148](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassStubCatalog.cs#L148)） | 不改锚点；交互见 §11 |
| 顺序回归锁 | [PassChainOrderTests.cs:28,48](file:///d:/code/cs2j/tests/CSharpToJava.Tests/Preprocessing/PassChainOrderTests.cs#L28) | 维持绿 |

---

## 4. C# 侧剩余工作

> 编号 C-x，M1/M2 认领关系见 §9。除 C8/C9 外均以「产物 `dotnet build` 零错误」为完工标准，禁止靠关掉 verify 过闸。

### 4.1 Core（[src/CSharpToJava.Core/RefOutInPreprocessor/](file:///d:/code/cs2j/src/CSharpToJava.Core/RefOutInPreprocessor)）

| # | 缺口（代码证据） | 补全内容（D3 规则号） |
|---|---|---|
| C1 | 调用点提升只覆盖**表达式语句顶层调用**：[RefOutInSyntaxRewriter.cs:348-528](file:///d:/code/cs2j/src/CSharpToJava.Core/RefOutInPreprocessor/RefOutInSyntaxRewriter.cs#L348-L528) 只在 `VisitExpressionStatement` 内处理一个 `InvocationExpressionSyntax`。实测 `if (TryParse(out var p))`、`if (TryGet(out int r))` 原样残留（CS1615/CS1061） | 提升下沉为**语句级通用改写**：在语句入口检测子树含需提升的内部 ref/out 调用，整条改 Block（前置 + 改写后原语句）；覆盖局部声明初始化、`return`、if/while/do/for 条件、三元/二元/赋值右值、嵌套实参；保持短路（`&&/\|\||??/?:`）条件求值与从左到右实参副作用顺序（§5.3 #9/#10/#11） |
| C2 | 声明式 out 与丢弃未实现：只识别 `IdentifierNameSyntax`/`ElementAccessExpressionSyntax` 实参，无 `DeclarationExpressionSyntax` 分支 | `M(out var x)`/`M(out int x)` → 槽 + 声明读回；`out _` → `ByRef<T>.Create()`（§5.3 #4/#5） |
| C3 | 成员链实参未提升：`MemberAccessExpressionSyntax`（`ref this.f`、`ref holder.Value`）落到 L510 原样带 `ref` 输出，实测 CS1615 | 访问器代理 `Create(() => e, v => e = v)`，字段无读回语句（§5.3 #6） |
| C4 | ref 返回只改签名不改体：[L92-98](file:///d:/code/cs2j/src/CSharpToJava.Core/RefOutInPreprocessor/RefOutInSyntaxRewriter.cs#L92-L98) 去了返回类型 ref，无 `VisitReturnStatement`，实测 CS8149；ref 属性/ref 索引器声明完全未处理 | `return ref e` → 按位置种类 `return <proxy(e)>`；补 property/indexer 声明与外部使用点 `.Value`（矩阵 #13/#16；§5.5） |
| C5 | lambda / 局部函数 ref-out 形参未改写（只有 method/ctor/delegate 三个 Visit） | 同一符号表一致改写，消除 [LambdaTransformer.cs:99](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/LambdaTransformer.cs#L99) 的输入面（矩阵 #8） |
| C6 | 外部转发方向错误：[L270-274 与 L318-344](file:///d:/code/cs2j/src/CSharpToJava.Core/RefOutInPreprocessor/RefOutInSyntaxRewriter.cs#L270-L344) 对外部 ref/out 实参里的 H 槽**不加** `.Value`，产出非法的 `Ext(ref a)`（a 已为 ByRef<T>） | 改为 `Ext(ref a.Value)` / `Ext(out a.Value)`（类字段取址合法，§5.4）；普通存储外部调用继续原样 |
| C7 | ref struct 路径端到端断裂：形参签名改写为 `RefStructByRef_<Mangled>`（[L116-119](file:///d:/code/cs2j/src/CSharpToJava.Core/RefOutInPreprocessor/RefOutInSyntaxRewriter.cs#L116-L119)），但生成 holder 无 `Create` 工厂、调用点也从不构造该类型——实参原样裸传，中间树必编译失败。`ByRef<T>.Create(...)` 的发射点（L427/L472/L487/L562/L569）是**数组元素/访问器代理**（与类型无关的通用代理形态），并非 ref struct 专用路径；ref 局部重绑定 `r = ref e2` 未处理 | 按 Q2 统一发 `RefStructByRef<T>`（槽模式 + 调用点工厂 + 读回）；重绑定改普通赋值（§5.5） |
| C8 | `using Cs2jByRef;` 只在声明改写时经 `UsedHolder` 注入（[RefOutInRewriter.cs:43-47](file:///d:/code/cs2j/src/CSharpToJava.Core/RefOutInPreprocessor/RefOutInRewriter.cs#L43-L47)），纯调用点文件缺 using，实测 CS0103 ×3 | 提升代码一旦引用 holder 工厂/类型即置位；或在收口阶段按编译诊断补注入（§5.6） |
| C9 | `in` 碰撞保留 + Warning 未实现（`InOverloadKept` 恒 0） | CD8：去 `in` 后用符号表查重复声明/调用歧义，碰撞则保留并 Warning（§2） |
| C10 | 后置自检过弱且未接线：[RefOutInPostVerifier.cs:31-47](file:///d:/code/cs2j/src/CSharpToJava.Core/RefOutInPreprocessor/RefOutInPostVerifier.cs#L31-L47) 只查参数；适配器从不调用 | 升级到 D3 §7 四项断言（声明/返回/局部/字段/数组取址零内部残留；using/ProjectReference/`<Compile Remove>` 齐套），适配器 verify 阶段强制调用，违例 Error（§7.5） |
| C11 | 两类语言无解边界未发诊断（D3 §9.3/9.4） | ref struct 字段链跨签名取址、ref struct H 槽转发外部 ref API → CS2J3303/CS2J3304，保留原表达式让构建暴露并给出可读消息 |
| C12 | ref 字段只改声明（[L597-612](file:///d:/code/cs2j/src/CSharpToJava.Core/RefOutInPreprocessor/RefOutInSyntaxRewriter.cs#L597-L612)），初始化适配/含 ByRef 字段的 ref struct 自动降级未做；`RefFieldsRewritten` 计数不区分内外部（[RefOutInAliasAnnotator.cs:80-86](file:///d:/code/cs2j/src/CSharpToJava.Core/RefOutInPreprocessor/RefOutInAliasAnnotator.cs#L80-L86)） | 按 Q5 命中率决定补全或显式 Error；计数只记内部 |
| C13 | 多维数组发射不存在的工厂重载（Q6） | 默认 fail-fast CS2J3303 同族；sdk 扫描命中再补 |
| C14 | 单文件模式保守化未声明诊断：无全编译时未知符号按外部处理（D3 §9.9）应可审计 | 每文件输出分类计数（Internal/External/Locked），verbose 可见 |

注解器跨文件一致性补充说明：适配器虽逐文件 new annotator，但判定全部走 SemanticModel 符号的 `Locations.IsInSource`（[RefOutInAliasAnnotator.cs:183-184](file:///d:/code/cs2j/src/CSharpToJava.Core/RefOutInPreprocessor/RefOutInAliasAnnotator.cs#L183-L184)），共享同一棵全树 compilation，重写不修改语法树，顺序无关；需补「同一内部接口的声明与实现在两个文件分别改写后签名一致」专项测试锁死。

### 4.2 支撑库与 CLI

| # | 缺口 | 补全内容 |
|---|---|---|
| S1 | [RefOutInSupportFiles.cs:49-71](file:///d:/code/cs2j/src/CSharpToJava.CLI/RefOutInSupportFiles.cs#L49-L71) 生成 `object` 字段的伪 holder，缺工厂，对 ref struct 不可用 | Q2 定案后：推荐改为释放**一个**泛型 `RefStructByRef<T>` 源文件（真源放 `src/Cs2jRefOutIn/`，嵌入资源分发，与 [ByRef.cs](file:///d:/code/cs2j/src/Cs2jRefOutIn/ByRef.cs) 同机制），删除按类型生成循环与 `RequiredRefStructTypes` 并集；若保留方案 B 则生成强类型 direct/array 双模式 + 三工厂 |
| S2 | [ByRef.cs](file:///d:/code/cs2j/src/Cs2jRefOutIn/ByRef.cs) 已具备四工厂三形态（无 seed 槽 / 带 seed 槽 / 一维数组元素 / 访问器闭包），缺口仅剩多维数组（Q6 已定案 fail-fast，无需补） | 维持现状；仅随 S1 一起作为嵌入资源分发，注释对齐 CS2J3303 报错口径 |
| S3 | 单文件模式只写 ByRef.cs（[Program.cs:2602-2604](file:///d:/code/cs2j/src/CSharpToJava.CLI/Program.cs#L2602-L2604)），不含 RefStructByRef | 按 Q2 后形态合并写入 `_Cs2jRefOutIn.cs`（D3 §6.2） |
| S4 | fixture 不可构建：[SampleRefOutInV2App.csproj](file:///d:/code/cs2j/tests/SampleRefOutInV2Solution/SampleRefOutInV2App/SampleRefOutInV2App.csproj) 用顶级语句但缺 `<OutputType>Exe</OutputType>`（CS8805）；三个项目测试显式关 verify；**fixture 无测试工程，`VerifyTests` 即使打开也无实义** | 修 csproj；**新增 `SampleRefOutInV2Tests` xunit 测试工程**（覆盖 ByRef 语义断言，使 `dotnet test` 有实际断言）；用例打开 VerifyBuild/Tests；fixture 补齐矩阵代表场景（成员链、条件内 out、ref 返回、ref 局部），内容须与 C1-C13 同步演进 |
| S5 | 适配器后置钩子未做自检调用；ROI 码位与 CS2Jxxxx 风格不一致 | 接入 C10 自检；诊断码按 §6 映射（保留 ROI 旧码为消息别名一个版本，新码并行发射） |
| S6 | 指纹/过滤/排除已接线，无需改动 | 仅回归确认 Cs2jRefOutIn 不进指纹快照、不规划 Maven |

---

## 5. Java 侧 holder 映射设计（C01 核心）

### 5.1 holder 类型族（compat 新增）

在 `io.github.ningpp.compat`（与 8 个旧 holder 同包）新增：

```java
// io/github/ningpp/compat/ByRef.java
public abstract class ByRef<T> {
    public abstract T getValue();
    public abstract void setValue(T value);

    public static <T> ByRef<T> create() { return new SlotByRef<>(null); }
    public static <T> ByRef<T> create(T initial) { return new SlotByRef<>(initial); }
    public static <T> ByRef<T> create(T[] array, int index) {
        return new ArrayElementByRef<>(array, index);
    }
    public static <T> ByRef<T> create(java.util.function.Supplier<T> getter,
                                      java.util.function.Consumer<T> setter) {
        return new AccessorByRef<>(getter, setter);
    }

    private static final class SlotByRef<T> extends ByRef<T> {
        private T slot;
      SlotByRef(T v) { this.slot = v; }
      public T getValue() { return slot; }
      public void setValue(T v) { this.slot = v; }
    }
    private static final class ArrayElementByRef<T> extends ByRef<T> { /* 持 T[]+index，读写穿透 */ }
    private static final class AccessorByRef<T> extends ByRef<T> { /* 持 Supplier/Consumer */ }
}
```

```java
// io/github/ningpp/compat/RefStructByRef.java —— Java 无 ref struct 概念，普通 final 类即可
public final class RefStructByRef<T> {
    private T slot; private T[] array; private int index; private final int mode;
    public T getValue() { return mode == 0 ? slot : array[index]; }
    public void setValue(T v) { if (mode == 0) slot = v; else array[index] = v; }
    public static <T> RefStructByRef<T> create() { ... }
    public static <T> RefStructByRef<T> create(T initial) { ... }
    public static <T> RefStructByRef<T> create(T[] array, int index) { ... }
}
```

要点：

- 与 C# [ByRef.cs](file:///d:/code/cs2j/src/Cs2jRefOutIn/ByRef.cs) **同构同名同工厂形状**（仅语言惯例差异：`Value`→`getValue/setValue`、`Create`→`create`、internal 子类→private nested）。映射因此退化为「类型映射 + 成员映射」两条配置/规则，无调用点算法分叉。
- 三个实现类全部 private nested，**只允许工厂构造**——与 C# 侧「被改写签名只出现抽象类型」一致；转换器永不发射实现类名，故无需为它们注册映射。
- 不动 `IntHolder`/…/`ObjectHolder` 的一个字符（CD6，外部/边界路径）。

### 5.2 封送 / 布局 / 别名不变式

1. **ByRef 只是内部调用约定**：不得出现在映射到外部库（BCL/compat 既有类）的签名上。该边界在 C# 侧由锁定（CD3）保证，Java 侧以 CS2J3320 fail-fast 兜底。
2. **即时写回**：array/accessor 实现每次 `setValue` 立即穿透原存储，无缓存、无「调用后统一回写」。与 C# 侧共享同一异常语义——方法中途抛出时已发生的写入不丢失（D3 §3.2）。
3. **别名恒等**：代理持有原数组/访问器引用，`Swap(ref arr[i], ref arr[j])` 等别名场景读写同一存储；禁止在工厂内做防御性复制。
4. **值类型不 clone**：T 为 struct 映射的 Java 可变类（如 compat 的 Point 形态）时引用语义天然对齐；防御性 clone 是 C07 的职责，本族不做（catalog §3.3 值语义族边界）。
5. **基本类型接受装箱**：`ByRef<Integer>` 经 `getValue/setValue` 装箱，性能非目标（D3 §9.8）；基本类型专用 holder 只服务旧边界路径。
6. **ref struct 退化为普通对象**：C# 的 `Span<T>` 在 Java 侧已是普通类 [Span.java](file:///d:/code/cs2j/java/csharptojava-compat/src/main/java/io/github/ningpp/compat/Span.java)（类注释明确「Does not simulate C# ref-struct lifetime semantics」），故 `RefStructByRef<Span<T>>` 在 Java 就是普通 holder，无需模拟栈生存期。
7. **泛型擦除**：`ByRef<T>` 擦除为 Object 签名；工厂调用靠 Java 目标类型推断，**不发射 Class token**（与 C02 的 token 化划界，见 §11）。

### 5.3 转换器映射规则

| # | C# 中间树形态 | Java 产物 | 落点 |
|---|---|---|---|
| J1 | 类型/字段/形参/局部 `Cs2jByRef.ByRef<T>` | `io.github.ningpp.compat.ByRef<T>` + import | TypeMappings.json `typeMappings` 新增条目，形状仿 [L334-340](file:///d:/code/cs2j/config/TypeMappings.json#L334-L340) UnsignedMath 条目 |
| J2 | `Cs2jByRef.RefStructByRef<T>`（Q2 方案 A 后） | `io.github.ningpp.compat.RefStructByRef<T>` | 同上 |
| J3 | `a.Value` 读 / `a.Value = x` 写 | `a.getValue()` / `a.setValue(x)` | **零配置**：`Value` 是 C# 属性，转换器既有属性约定自动产出（读→`get+Name()` 回落、写→[AssignmentTransformer.Property](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/AssignmentTransformer.cs) `set+Name(...)`，Q3 定案）；不注册 methodMappings，仅以回归测试锁死 |
| J4 | `ByRef<T>.Create(...)` 四形态 | `ByRef.create(...)`（**去显式类型实参**，重载按参数形状解析） | 静态泛型调用发射规则：识别 receiver 为 ByRef/RefStructByRef 时省略类型见证；lambda 实参原样转 Java lambda，目标类型推断为 Supplier/Consumer |
| J5 | `RefStructByRef_<Mangled>`（仅当 Q2 走方案 B） | `RefStructByRef<T元素>` | 需经特性标注或 demangle 还原元素类型；方案 A 落地后本规则删除 |
| J6 | 旧路径签名 holder（外部/锁定残留） | `IntHolder`/`ObjectHolder<T>` 等 | 维持 [MethodTransformer.cs:148](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Member/MethodTransformer.cs#L148) + HolderTypeResolver 不动；回归测试锁死「ByRef 形参不被二次 holder 化」（参数已无 ref/out 关键字，天然不触发，加测试确认） |
| J7 | 内部 H 槽转发外部 API 的 `Ext(ref a.Value)` | 按旧 ArgumentTransformer.Ref 路径处理实参表达式 `a.Value`（=普通 int 存储读写） | 专项 fixture：内部方法持有 ByRef 槽 → 转发 `int.TryParse`/`Volatile.Read` 形态，验证两条路径在同一调用点正确缝合 |
| J8 | 任何未列入 J1-J7 的 ByRef 形态（手写子类、未知工厂重载、映射缺失） | **CS2J3320 Error，中止该文件转换** | 禁止裸发未限定 `ByRef` 或 TODO 注释（catalog §4 风险 12 不静默透传原则） |

### 5.4 与 D6 原则的关系

D6（外部契约锁定）在 Java 侧的推论：

- **holder 不穿越任何 ABI 边界**——P/Invoke/extern（C06/C42 域）、映射到外部接口/虚方法的签名上只允许原生 ref/out 残留，由旧路径产出旧 holder；ByRef 层次永远只连接「源树内部方法↔源树内部方法」。
- C# 侧锁定判定 [RefOutInAliasAnnotator.IsLocked](file:///d:/code/cs2j/src/CSharpToJava.Core/RefOutInPreprocessor/RefOutInAliasAnnotator.cs#L188-L250) 是这条边界的唯一执行者；C06/C42 未来需要同一判定时应抽共享谓词，不得复制分叉（§11）。
- 因此 5.1 的新类族不追求表达封送（无地址、无布局、无 native 互操作）；它只表达**托管可变位置**。

### 5.5 与旧路径的长期共存与收敛

- 本任务完成后旧路径的合法输入收窄为：① 不经预处理直连 pipeline 的单文件转换；② 预处理后残留的外部/锁定 ref/out。两者都必须长期可工作（catalog 要求转换器保留安全网，与 E17 设计 §10 同范式）。
- `Lowering/LowerRefOut.cs` 仍只属实验 Java2 管线（[NewConversionPipeline.cs](file:///d:/code/cs2j/src/CSharpToJava.Core/Lowering) 注册、生产不可达），本任务不引用不删除；其价值仅为形态参考（catalog §4 风险 9）。
- 未来「旧路径删除」是独立后续 PR 的议题，前提是直连入口也强制经过预处理，不在 C01 范围。

---

## 6. 诊断与失败策略

新诊断统一 **CS2Jxxxx** 风格（现网已用 CS2J10xx 类型/泛型、CS2J30xx/31xx 平台隔离、CS2J40xx Java 元数据、CS2J90xx pass 基建），本族分配 **CS2J33xx（ABI/holder 段）**。现有 ROIxxx 码在同一事件上并行保留一个版本（消息文本不变），随后续版本下线：

| 新码 | 旧码 | 级别 | 触发与行为 |
|---|---|---|---|
| CS2J3301 | ROI001 | Info | 成员命中锁定（外部契约），保留 ref/out/in；每声明一条，供审计 |
| CS2J3302 | — | Warning | `in` 去除引发重载/调用歧义，保留 `in`（CD8） |
| CS2J3303 | — | Error | 无法构造合法代理的位置：ref struct 字段链跨签名取址（D3 §9.3）、多维数组（Q6 未决期）、只读字段 return ref 等；保留原表达式让构建暴露并给修复建议 |
| CS2J3304 | — | Error | ref struct H 槽向外部 ref API 转发（C# 语言无解，D3 §9.4） |
| CS2J3305 | — | Error | 后置自检发现内部 ref/out 残留（C10）；该 pass 判失败，不交付中间树 |
| CS2J3310 | ROI099 | Error | 重写产物重解析非法，保原文件（[RefOutInRewriter.cs:54-58](file:///d:/code/cs2j/src/CSharpToJava.Core/RefOutInPreprocessor/RefOutInRewriter.cs#L54-L58)） |
| CS2J3311 | ROI100 | Error | 中间树 `dotnet restore` 失败 |
| CS2J3312 | ROI101 | Error | 中间树 `dotnet build` 失败（当前 V2 fixture 实测即此码） |
| CS2J3313 | ROI102 | Error | 中间树 `dotnet test` 失败 |
| CS2J3320 | — | Error | **Java 转换侧**遇到未映射 ByRef 形态（§5.3 J8）；显式中止，禁止静默透传/TODO 占位 |

通用原则：

1. **不静默透传**：C# 侧无法等价表达→CS2J3303/3304 显式报错；Java 侧不认识→CS2J3320。任何分支都不得以注释、原样 `ref`、裸类名流入产物（catalog §4 风险 12）。
2. **verify 即闸门**：descriptor 默认 verify build/test（已为 true），失败码 3312/3313 使 pass 失败、链中止（[ProjectPassRunner.cs:39](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/ProjectPassRunner.cs#L39) `!adapter.HasErrors`）；禁止在测试里长期关 verify 绕过（S4 修回）。
3. **Info 可审计、Warning 不阻断、Error 不交付**；锁定计数 `LockedContractMembers` 与外部保留计数 `ExternalRefArgumentsKept` 写入链摘要行（[RefOutInPassAdapter.cs:178-184](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/Adapters/RefOutInPassAdapter.cs#L178-L184)）。

---

## 7. 测试计划

### 7.1 Core 单测（扩充 [RefOutInRewriterUnitTests.cs](file:///d:/code/cs2j/tests/CSharpToJava.ProjectTests/RefOutInRewriterUnitTests.cs)）

- 现有 16 个文本断言用例保留，并把编译引用从「仅 mscorlib」升级为**带 Cs2jRefOutIn 源/程序集的真实编译**，每个正例断言输出 `CSharpCompilation` 零 error（仿 java-keywords 设计的 `AssertOutputCompiles`）。
- 新增（按 §4 缺口）：
  - C1：提升出现在 `if/while/do/for` 条件、`return` 右值、局部声明初始化、嵌套调用实参、`&&/||/??` 短路右侧（断言右侧不被无条件执行）、三元/二元；多实参前置左→右、读回逆序；
  - C2：`out var`/`out int x`/`out _`（含条件内与 foreach 体内）；
  - C3：`ref this.f`、`ref obj.F`、静态字段、`out obj.Prop` 成员链；
  - C4：`return ref arr[i]`/ref 形参/字段代理；ref 属性、ref 返回索引器；
  - C5：lambda 与局部函数 ref/out 形参与调用点；
  - C6：H 槽转发外部 API → `Ext(ref a.Value)`；普通存储外部调用文本不变；
  - C7：`ref Span<int>` 形参 → Q2 形态 holder，调用点双模式工厂；
  - C8：纯调用点文件含 `using Cs2jByRef;`（防 CS0103 回归）；
  - C9：`in` 碰撞保留 + CS2J3302；不碰撞正常去修饰；
  - C10：自检对返回/局部/字段/数组取址残留计数；CS2J3305；
  - C11/C13：CS2J3303/CS2J3304 触发用例；
  - 跨文件：内部接口声明与实现分两文件，改写后签名一致；partial 类；
  - 干净文件 `Changed=false`、不注入 using/不要 ProjectReference；统计计数逐项断言。

### 7.2 项目级 fixture 与中间树 Release build/test

- 修 [SampleRefOutInV2Solution](file:///d:/code/cs2j/tests/SampleRefOutInV2Solution)（S4：Exe + 新增 xunit 测试工程 + 场景补齐），[RefOutInPreprocessorTests.cs](file:///d:/code/cs2j/tests/CSharpToJava.ProjectTests/RefOutInPreprocessorTests.cs) 三个用例**打开 VerifyBuild/VerifyTests**，断言退出码 0 与测试全绿；保留对支撑库释放、ProjectReference、无内联 helper 的既有断言。
- 新增覆盖：内部↔外部混用（ByRef 槽转发 `int.TryParse`）、ref 返回属性、ref 局部重绑定、Span ref/in 重载、（按 Q5/Q6 结论）ref 字段/多维数组。
- CI 命令：`dotnet build -c Release`、`dotnet test -c Release` 于 `.cs2j-refoutin-src`；旧 [SampleRefOutInSolution](file:///d:/code/cs2j/tests/SampleRefOutInSolution) 保持直连 Java 路径基线不动（D3 §8.2 注记仍成立）。

### 7.3 Java 侧测试

1. compat 新增 `ByRefTest.java`（JUnit 5，仿 [HolderClassesTest.java](file:///d:/code/cs2j/java/csharptojava-compat/src/test/java/io/github/ningpp/compat/HolderClassesTest.java)）：slot 读写/默认值、seed；array 模式即时穿透与别名（同数组两代理交换）；accessor 模式字段/局部读写穿透；写后抛异常值已落地；`RefStructByRef` 双模式承载 `Span`；`mvn test` 全绿。
2. 转换器侧新增映射测试（ConversionTests 新类目）：输入**经 RefOutInRewriter 改写后的 C#**（在测试内先跑 Core rewriter，再走 ConversionTestBase），断言 Java 含 `ByRef<...>`/`getValue`/`setValue`/`ByRef.create(...)`/Supplier-Consumer lambda、无裸 `ByRef`、无重复包装；J7 缝合用例（ByRef 槽→外部 TryParse）。
3. 端到端：V2 fixture 经完整 `convert-project`（默认链）产出 Java 工程，`mvn compile` 零错误；若 fixture 带测试工程，转译后 JUnit 全绿（Java 半区预言机，catalog §5.1）。

### 7.4 旧断言迁移清单

| 测试文件 | 现状 | 迁移动作 |
|---|---|---|
| [RefOutInProjectTests.cs:29-40](file:///d:/code/cs2j/tests/CSharpToJava.ProjectTests/RefOutInProjectTests.cs#L29-L40) | 直连 pipeline，断言 `IntHolder`/`ObjectHolder`（catalog §4 风险 5 点名） | 拆为：① `SlnProject_FullConversion_AllFilesSucceed` 改为「先经 ref-out-in 预处理再转换」，断言新层次 `ByRef`；② 另留 1 个直连 pipeline 用例显式标注「旧路径安全网」，保留 IntHolder 断言直至旧路径退役 |
| [RefOutInComprehensiveTests.cs](file:///d:/code/cs2j/tests/CSharpToJava.ConversionTests/Categories/RefOutInComprehensiveTests.cs)（~40 用例） | 全部直接断言旧 holder | 按输入性质二分：外部/锁定形态（含 in 按值、丢弃 scratch、转发）留旧断言；内部形态（内部类的 ref/out 方法、struct/generic holder）改为两段式（rewrite→convert）断言 ByRef；Trait 分类标 `LegacyHolderPath` / `ByRefPath` 便于过滤 |
| [RefOutHolderConflictTests.cs](file:///d:/code/cs2j/tests/CSharpToJava.Tests/RefOutHolderConflictTests.cs) | 旧路径 FQN 防遮挡（用户嵌套类也叫 ObjectHolder） | **保留不动**——残留路径仍需要全限定名 |
| [RefOutSameVariableTests.cs](file:///d:/code/cs2j/tests/CSharpToJava.Tests/RefOutSameVariableTests.cs) | 空类，无任何测试方法（实测 22 行） | 删除，或按 2026-07-08 设计补「out 后再 ref 同变量」用例（分别覆盖旧路径与 ByRef 路径） |
| [RefOutInRewriterUnitTests.cs](file:///d:/code/cs2j/tests/CSharpToJava.ProjectTests/RefOutInRewriterUnitTests.cs) | 文本断言、不验编译 | 按 §7.1 升级 |
| [RefOutInPreprocessorTests.cs](file:///d:/code/cs2j/tests/CSharpToJava.ProjectTests/RefOutInPreprocessorTests.cs) | verify 全关 | 按 §7.2 打开 |
| [PassChainOrderTests.cs](file:///d:/code/cs2j/tests/CSharpToJava.Tests/Preprocessing/PassChainOrderTests.cs) | 锁链序 | 不动，保持绿 |

迁移期纪律：每个改断言的 PR 必须同时给出「旧断言为何移走/为何保留」的一行依据，禁止先删测试后补功能。

### 7.5 residual 闸门

- C10 升级版 [RefOutInPostVerifier.cs](file:///d:/code/cs2j/src/CSharpToJava.Core/RefOutInPreprocessor/RefOutInPostVerifier.cs) 在适配器 verify 前对全部产物跑**语义模型**断言（D3 §7 四条）：内部声明/返回/局部/字段/数组取址零残留；残留全部可归类为外部实参或锁定成员；using/ProjectReference/`<Compile Remove>` 齐套。违例 CS2J3305。
- 转换器侧加测试：默认链产物中 grep 语义断言「内部签名上零 IntHolder/ObjectHolder；ByRef 只出现在映射后带 import 的 Java 形态」。
- 直连 pipeline 入口不经过该闸门（无预处理），由旧路径测试独立守门。

---

## 8. 默认开启 / 保持开启的验收门槛

全部满足方可宣告 C01 完成（回退开关 `--no-ref-out-in` 保留，但默认档必须达标，对齐 catalog §4 风险 10）：

1. **C# 半区**：修复后的 V2 fixture 中间树 `dotnet build -c Release` + `dotnet test -c Release` 退出 0；[RefOutInPreprocessorTests.cs](file:///d:/code/cs2j/tests/CSharpToJava.ProjectTests/RefOutInPreprocessorTests.cs) 不再出现 `VerifyBuild=false`。
2. **矩阵覆盖**：D3 §4 的 18 类 + 本设计 C1-C14 全部有单测或 fixture 覆盖且通过；锁定/外部边界、短路求值、异常写回有专项。
3. **SDK 基线（D5）**：动手前对 `D:\csharp-sdk` 两个 sln 存档 build/test 基线（只读）；完成后同口径比对——**新增构建错误为零、新增测试失败为零、通过项目数不降**，日志归档；两 sln 均验，以全量 `csharp-sdk.sln` 为准。
4. **仓库回归**：全仓 `dotnet test -c Release` 无新增失败（含 §7.4 迁移用例）。
5. **Java 半区**：compat `mvn test`（含新 ByRefTest）绿；V2 fixture 默认链 Java 产物 `mvn compile`/test 绿；CS2J3320 有触发用例证明 fail-fast 有效。
6. **residual 闸门**：自检证明内部声明零残留，残留 ref/out/in 全归类外部/锁定；J6 回归证明旧 holder 不被二次包装。
7. **链契约不变**：`--list-passes` 中 id/stage/默认值/顺序与 §3 完全一致；`--no-ref-out-in` 回退可用；指纹令牌与支撑库过滤行为不变（一次性缓存失效可接受，foundation plan L137）。

---

## 9. 里程碑（每个可独立提交）

| 里程碑 | 内容 | 出口标准 |
|---|---|---|
| **M0** | 本设计评审定案；Q1/Q2/Q3/Q4 选型确认；sdk 基线快照（Q5/Q6 命中率扫描顺带完成） | 评审通过；基线日志归档 |
| **M1（C# 闭环修复）** | C1-C6、C8-C9、C14：调用点通用提升、声明式 out/丢弃、成员链、ref 返回/属性/索引器、lambda/局部函数、外部转发 `.Value`、using 注入、in 碰撞；§7.1 单测；S4 fixture 修复并打开 verify | V2 fixture 中间树 build/test 绿；Core 单测在真实编译引用下绿 |
| **M2（holder 体系与边界）** | Q2 定案落地（推荐泛型 `RefStructByRef<T>`，删除 mangling/代码生成）；S1-S3、S5；C7、C10-C13；后置自检接线；CS2J33xx 诊断；Q5/Q6 按扫描定补全或 fail-fast | csharp-sdk 两 sln「新增失败为零」；residual 闸门在生产路径生效 |
| **M3（Java 映射）** | compat `ByRef`/`RefStructByRef` + JUnit；TypeMappings J1-J4；转换器 ByRef 识别与 J8 fail-fast；J6/J7 缝合测试 | compat `mvn test` 绿；ByRefPath 转换器测试绿 |
| **M4（端到端与旧断言收口）** | §7.4 全量迁移；V2 fixture 默认链 Java 端到端；仓库全量回归；catalog/ROADMAP 状态更新（C01 关闭、风险 5 勾销） | §8 七项门槛全过 |
| M5（可选） | ROIxxx 旧码下线、旧 holder 路径删除评估（独立 PR，默认不在 C01 内） | 仅评估文档 |

M1/M2 只动 C# 侧即可独立交付价值（把「C# 闭环不成立」先修成真）；M3 是 catalog 风险 5 的真正解除点；M4 之前不得宣称默认开启的 Java 产物可用。

---

## 10. 风险与开放问题（catalog §4 对照）

| 来源 | 风险 | 本设计应对 |
|---|---|---|
| **catalog §4 风险 5（显式引用）** | 「C01 RefOutIn 的 Java 映射缺口是真实阻断……接线意味着要同时改测试断言与 holder 体系，必须先补 Java 侧 spec」 | 本文件 §5 即该 spec；§7.4 给出逐文件迁移清单；新增的 §1.1 实测证据表明阻断同时存在于 C# 半区（ROI101/CS8149），故 M1 先修 C# 闭环、M3 解除 Java 阻断，分两段验收 |
| catalog §4 风险 2 | C04 yield 与 try/finally/using/ref 组合，Dispose 时序与 C01 holder 耦合 | M1 的提升器必须先过 using/foreach/fixed 受限作用域用例（C1）；C04 落地前不允许 holder 写回跨 `}`；专项 fixture 前置 |
| catalog §4 风险 3 | C06 unsafe 的 ABI/外部契约：任何指针改写不能改变封送布局 | §5.4 明确 ByRef 不跨 ABI；锁定谓词未来抽共享（§11）；C06 依赖 C01 但不得复用 ByRef 表达 native 地址 |
| catalog §4 风险 10 | opt-in 通道长期默认化形成多套事实标准 | §8 七项门槛即本 pass 的默认开启标准；不新增开关，维持 `--no-ref-out-in` 单一回退 |
| catalog §4 风险 11 | 中间树验证成本与指纹 | verify 已默认开且本任务让其真跑；Cs2jRefOutIn 文件已豁免指纹（S6 回归确认）；串行耗时增长以 sdk 实测评估，不在本任务改机制 |
| catalog §4 风险 12 | 静默错误输出族 | §6 CS2J3303/3304/3320 三向 fail-fast；禁止注释/占位/裸类名 |
| catalog §4 风险 9 | 实验 Lowering 不是实现资产 | §5.5 明确不引用 [LowerRefOut.cs](file:///d:/code/cs2j/src/CSharpToJava.Core/Lowering/LowerRefOut.cs)，只借形态 |
| catalog C01 条目 | 「建议先于 E13 ReadOnlyStruct（holder struct 与只读化有交互，需原型确认）」 | 现链事实是 ref-out-in 远早于 make-readonly（§3）；Q2 的泛型 holder 与 E13 的交互（只读化是否改写 RefStructByRef 用法）列为 M2 原型项，发现冲突再按 catalog 建议提重排评审 |
| 本任务特有 | accessor lambda 经转换器到 Supplier/Consumer 的保真度（捕获字段/this/数组、setter 表达式 lambda） | Q4；M3 测试先行；不通则启用自定义函数式接口备选 |
| 本任务特有 | Q2 探针风险（`ref struct RefStructByRef<T>` 若编译器不接受 T 字段） | M2 第一步 10 行探针；失败回退方案 B（强类型 per-type 生成 + J5 映射） |
| 本任务特有 | 旧 holder（基本类型专用、public 字段）与新层次长期双轨，转换期混淆 | J6/J7 回归 + residual 语义闸门；包内共存但命名/映射严格分流 |
| 本任务特有 | 逐文件注解器对极端跨文件形态（partial、跨项目 ProjectReference）签名一致性 | §4.1 末专项测试；sdk 32 项目跨项目用例兜底 |

---

## 11. 并行开发协调（ABI 层交互与合并顺序）

| 待办 | 注册表锚点（证据） | 与 C01 的交互 | 协调结论 |
|---|---|---|---|
| **C02 generic-deepen**（stub `generic-deepen`，Abi，RunAfter=`generic`，[PassStubCatalog.cs:112-120](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassStubCatalog.cs#L112-L120)） | `ByRef<T>.Create(...)` 是显式泛型实参的静态调用；C02 的 Class token 化与本任务的「去类型见证」（J4）争抢同一调用面 | C01 先合并定死：ByRef/RefStructByRef 工厂调用**永不发射 Class token**（Java 推断足够），C02 的 token 规则按 receiver 类型豁免这两个类；交叉用例：泛型方法内 `ByRef<T>`（T 为类型参数）经两 pass 后 build/test 绿 |
| **C06 unsafe-ffm**（stub `unsafe-ffm`，Abi，[L132-140](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassStubCatalog.cs#L132-L140)；catalog 明确「依赖 C01」） | 指针取址代理与 ByRef 位置模型同族；extern 锁定共用 D6 | C06 **必须 rebase 在 C01 M2 之后**；`IsLocked` 判定抽为共享 ABI 谓词（C42/C06 共用 CS2J3xxx 基建，catalog §3.3 平台边界族）；ByRef 不表达 native 地址，指针代理允许并列新类型但不得复用 ByRef 穿 ABI |
| **C14 operator / member-desugar**（stub `member-desugar`，Declarative，[L192-200](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassStubCatalog.cs#L192-L200)） | 属性/索引器去糖会改写 ref 属性语法面；`ByRef.Value` 使用点要求在去糖后仍可被 J3 识别 | C14 实现时其 RunAfter 需纳入 `ref-out-in`（或证明声明去糖不触碰支撑库类型引用）；J3 映射测试在两条可能链序下各跑一次 |
| C32/C45 closure-lift（stub `closure-lift`，StateMachine，RunAfter=`ref-out-in`，[L142-150](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassStubCatalog.cs#L142-L150)） | accessor 代理 lambda（`() => x, v => x = v`）正是 display class 提升的输入形态 | 顺序锚点已正确（closure-lift 在 C01 之后）；closure-lift 设计须把「ByRef 工厂内 lambda 的捕获变量」列为已知输入并加 fixture；C01 侧保证这些 lambda 在 C# 层合法可编译（M1 覆盖） |
| C04 iterator-lowering（stub 锚 closure-lift 后） | yield 与 holder/using 时序耦合（风险 2） | C01 不阻塞，但受限作用域写回规则（C1）是其前置资产 |
| C03/C37 pattern-desugar、C08 java-keywords | 语法面不相交（控制流 / FinalNaming） | 无协调成本；C08 按设计固定在链尾，对 ByRef 等标识符改名规则照常生效（`Value` 非 Java 保留字，无碰撞预期，加一条观察用例即可） |

**建议合并顺序**：C01（M1→M4）→ C02 → C06 → C14；C01 期间其他待办不应修改 `RefOutInPreprocessor/`、`Cs2jRefOutIn/`、`HolderTypeResolver.cs`、`ArgumentTransformer.Ref.cs` 与 TypeMappings 中本任务新增的条目；如必须改，走同一 worktree 协商。

---

## 12. Worktree 分配

- **分支**：feature/preproc-c01-refout-finish
- **目录**：.worktrees/preproc-c01-refout-finish（仓库 d:\code\cs2j 内，已被 .gitignore 忽略；实测 `.gitignore:77` 与 `:85` 均含 `.worktrees/`）
- **base**：ff216bcc（dev-3.x HEAD，2026-09-28 核实：`docs: C09 [ThreadStatic] 前置化预处理器设计`。注：本设计核订版与实现计划以 docs 提交落在 dev-3.x，开工 base 应取「spec/plan 提交之后的 dev-3.x HEAD」，创建前用 `git rev-parse dev-3.x` 现场核实）
- **创建命令**：`git worktree add .worktrees/preproc-c01-refout-finish -b feature/preproc-c01-refout-finish ff216bcc`
- **说明**：开工时从该 worktree 启动；doc 本身在主工作区编写提交。

---

## 附：本设计引用的关键证据索引

- 链契约：[PassCatalog.cs:119-130](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassCatalog.cs#L119-L130)、[PassDescriptor.cs:50-58](file:///d:/code/cs2j/src/CSharpToJava.Core/Preprocessing/PassDescriptor.cs#L50-L58)、[PassStubCatalog.cs:142-150](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassStubCatalog.cs#L142-L150)
- Core：[RefOutInRewriter.cs](file:///d:/code/cs2j/src/CSharpToJava.Core/RefOutInPreprocessor/RefOutInRewriter.cs)、[RefOutInSyntaxRewriter.cs](file:///d:/code/cs2j/src/CSharpToJava.Core/RefOutInPreprocessor/RefOutInSyntaxRewriter.cs)、[RefOutInAliasAnnotator.cs](file:///d:/code/cs2j/src/CSharpToJava.Core/RefOutInPreprocessor/RefOutInAliasAnnotator.cs)
- 适配器/包装：[RefOutInPassAdapter.cs](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/Adapters/RefOutInPassAdapter.cs)、[ProjectRefOutInPreprocessor.cs](file:///d:/code/cs2j/src/CSharpToJava.CLI/ProjectRefOutInPreprocessor.cs)
- 旧 Java 路径：[HolderTypeResolver.cs](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/HolderTypeResolver.cs)、[ArgumentTransformer.Ref.cs](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/ArgumentTransformer.Ref.cs)
- 旧/新 holder：[ObjectHolder.java](file:///d:/code/cs2j/java/csharptojava-compat/src/main/java/io/github/ningpp/compat/ObjectHolder.java)、[ByRef.cs](file:///d:/code/cs2j/src/Cs2jRefOutIn/ByRef.cs)
- 实测：2026-09-28 release verb 跑 SampleRefOutInV2Solution → CS8149/CS0103/CS1615/CS1061/CS8805（§1.1）
