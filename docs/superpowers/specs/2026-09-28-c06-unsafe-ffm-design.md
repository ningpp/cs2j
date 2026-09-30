# C06 Unsafe/FFM 归一设计（pointer/fixed/unsafe/stackalloc/extern/ref struct）

- 日期：2026-09-28（2026-09-29 核订：证据行号/路径重定位、C01-C04 与 C11-C14 落地状态同步、RefStructByRef holder 形态纠错）
- 范围：C#→C# 预处理 pass `unsafe-ffm`（catalog C06，P1）从 stub 翻转为分规则可实施的实现设计；不含编码
- 状态：已核订（2026-09-29 对照 dev-3.x@2d25700e 逐条核验全部代码证据并修正：§1.1 表逐行实测，1 处**路径错误**（TypeMappingService 在 `Context/` 而非 `Services/`）、1 处**产物形态错误**（C01 已落地的 ref struct holder 是泛型 `RefStructByRef<T>` 而非 `RefStructByRef_<Mangled>`）、Span 映射区间重定位；§3 契约表 9 处行号漂移与 2 处错误落点（CS2J9001 产出点、启用判定点）已改正；C01-C04/C11-C14 落地现状写入 §11；实测红灯计数为 2026-09-28 快照未重测；仍待实施）
- 链位：`PassStage.Abi`，`RunAfter = struct-value-semantics`，中间目录 `.cs2j-unsafe-ffm-src`
- 证据基准：[f68a783c](file:///d:/code/cs2j)（`feat(preprocessing): register 23 merged-family stub passes`；原文记「当前 HEAD」，**2026-09-29 核订：主干已推进至 2d25700e，f68a783c 现为 HEAD 的祖先**）；除标注「2026-09-29 核订」的条目外，全部测试数与 CLI 行为均为 2026-09-28 Release 实测

---

## 1. 背景与目标

### 1.1 子规则现状证据表

当前转换器对 unsafe 家族是「一处兜底存活 + 多处 fail-fast + 多处静默错误输出」的混合状态。下表每一行均为实读复核（行号对应 f68a783c）。

| 子规则 | 现状行为 | 证据 |
|---|---|---|
| unsafe 块 | fail-fast：`"unsafe blocks must be lowered by a C# preprocessor"` | [StatementTransformer.SwitchAndResource.cs:300-301](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Statement/StatementTransformer.SwitchAndResource.cs#L300-L301)（2026-09-29 核订重定位） |
| fixed 语句 | 同一处 fail-fast：`"fixed statements must be lowered by a C# preprocessor"` | [StatementTransformer.SwitchAndResource.cs:296-297](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Statement/StatementTransformer.SwitchAndResource.cs#L296-L297)（2026-09-29 核订重定位） |
| unsafe 修饰符（类/方法/字段/构造/接口/记录） | **静默剥离**：各成员转换器把 `UnsafeKeyword` 映射为 None；`ClassTransformer.ModifierMap` 不含 unsafe | [FieldTransformer.cs:411](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Member/FieldTransformer.cs#L411)、[ConstructorTransformer.cs:329](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Member/ConstructorTransformer.cs#L329)、[InterfaceTransformer.cs:133](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Type/InterfaceTransformer.cs#L133)、[RecordTransformer.cs:292](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Type/RecordTransformer.cs#L292)、[ClassTransformer.cs:23-29](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Type/ClassTransformer.cs#L23-L29) |
| extern 修饰符 | MethodTransformer 有 `extern → Native` 映射，但被 ClassTransformer 前置异常拦截，生产不可达 | [MethodTransformer.cs:169](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Member/MethodTransformer.cs#L169) |
| extern/DllImport 方法声明 | fail-fast：`"CS2J normalization required: extern/DllImport method … must be lowered by a preprocessor"`（`IsExternLike`：extern 修饰符或属性名含 `DllImport`） | [ClassTransformer.cs:346-350](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Type/ClassTransformer.cs#L346-L350)（`IsExternLike` 判定在 :390-392） |
| 指针局部变量 | fail-fast：`"pointer types require a C# preprocessor normalization pass"` | [StatementTransformer.Declarations.cs:51-55](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Statement/StatementTransformer.Declarations.cs#L51-L55) |
| `&` 取址 / `*` 解引用 | fail-fast | [UnaryExpressionTransformer.cs:47-48](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/UnaryExpressionTransformer.cs#L47-L48)、[UnaryExpressionTransformer.cs:63-64](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/UnaryExpressionTransformer.cs#L63-L64) |
| 指针类型签名（参数/返回/字段） | 静默映射为 Java `MemorySegment` | [TypeMappingService.cs:216-221](file:///d:/code/cs2j/src/CSharpToJava.Core/Context/TypeMappingService.cs#L216-L221)、[TypeMappingService.cs:278-282](file:///d:/code/cs2j/src/CSharpToJava.Core/Context/TypeMappingService.cs#L278-L282)（**2026-09-29 核订：路径由 `Services/` 改正为 `Context/`**，行号经查证完全吻合） |
| 指针下标 `p[i]` | 有 FFM `segment.get(...)` 发射分支，但依赖 `FindPointerInfo` 注册信息；而 fixed 语句直接 throw，`PushFixedScope` 全 src **零调用方**，故注册信息恒为空——该分支生产中退化为裸段访问 | [ElementAccessTransformer.cs:99-150](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/ElementAccessTransformer.cs#L99-L150)、[ConversionContext.cs:57-112](file:///d:/code/cs2j/src/CSharpToJava.Core/Context/ConversionContext.cs#L57-L112) |
| 指针算术/比较 | 调 `FfmHelper.TryTransformPointerBinary`；helper 为 379 行 Java 文本发射工具（layout 表、读写、算术、bounds check），同样依赖空注册的 fixed scope | [BinaryExpressionTransformer.cs:69](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/BinaryExpressionTransformer.cs#L69)、[FfmHelper.cs](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Utilities/FfmHelper.cs)（379 行，2026-09-29 核订实测；catalog 的「346 行」为陈旧值） |
| `p->m`（struct 指针成员） | 按普通成员访问渲染成 `p.m`，无任何 layout/解引用语义——**静默错误输出** | [IdentifierExpressionTransformer.cs:36](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/IdentifierExpressionTransformer.cs#L36) |
| `(T*)x` 指针强转 | 原样返回表达式（静默） | [TypeOperationTransformer.cs:58](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/TypeOperationTransformer.cs#L58) |
| 显式 `stackalloc T[n]` | fail-fast | [ObjectCreationTransformer.cs:146-148](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/ObjectCreationTransformer.cs#L146-L148) |
| **隐式** stackalloc | **不 throw**：按数组创建发成 `new T[]{...}` 堆数组——栈分配静默降为堆分配 | [ExpressionTransformerRegistry.cs:108-133](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/ExpressionTransformerRegistry.cs#L108-L133) |
| `ref struct` 声明 | fail-fast | [StructTransformer.cs:60-64](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Type/StructTransformer.cs#L60-L64) |
| `ref struct` 签名位置（参数/返回） | 已被 C01 收编为 **泛型 holder `RefStructByRef<T>`**（**2026-09-29 核订纠错**：原文写 `RefStructByRef_<Mangled>`，那是 C01 早期的 per-type 生成方案；C01 落地时按 Q2-A 定案改为「只释放 `ByRef.cs` 与 `RefStructByRef.cs` 两个泛型源，不再按类型生成 mangled 名」，见 [RefOutInSupportFiles.cs:9-22](file:///d:/code/cs2j/src/CSharpToJava.CLI/RefOutInSupportFiles.cs#L9-L22)、[Cs2jRefOutIn/RefStructByRef.cs:11](file:///d:/code/cs2j/src/Cs2jRefOutIn/RefStructByRef.cs#L11)） | [RefOutInAliasAnnotator.cs:142-144](file:///d:/code/cs2j/src/CSharpToJava.Core/RefOutInPreprocessor/RefOutInAliasAnnotator.cs#L142-L144)（`IsRefLikeType` → `RefStructParamsRewritten` 计数，核订重定位）、[RefOutInSupportFiles.cs:45-71](file:///d:/code/cs2j/src/CSharpToJava.CLI/RefOutInSupportFiles.cs#L45-L71) |
| fixed 字段缓冲区（`fixed byte buf[256];`） | **唯一存活的 FFM 路径**：发 `MemorySegment` 字段 + `Arena.ofAuto().allocate(byteSize, ValueLayout.X)` | [FieldTransformer.cs:70-101](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Member/FieldTransformer.cs#L70-L101) |
| `Span<T>` / `ReadOnlySpan<T>` | 已映射 compat；compat 类注释明言「Does not simulate C# ref-struct lifetime semantics」 | [TypeMappings.json:395-429](file:///d:/code/cs2j/config/TypeMappings.json#L395-L429)（2026-09-29 核订重定位：`System.Span\`1` 块 L395-401、`System.ReadOnlySpan\`1` 块 L423-429）、[Span.java](file:///d:/code/cs2j/java/csharptojava-compat/src/main/java/io/github/ningpp/compat/Span.java)、[ReadOnlySpan.java](file:///d:/code/cs2j/java/csharptojava-compat/src/main/java/io/github/ningpp/compat/ReadOnlySpan.java) |
| P/Invoke 族边界诊断 | `NativeInteropAnalyzer`：CS2J3201（DllImport/LibraryImport/ComImport/MarshalAs/UnmanagedCallersOnly/GeneratedComInterface 属性）、CS2J3203（extern 方法）、CS2J3202（Marshal/NativeLibrary/HandleRef/SafeHandle 类型） | [UnsupportedDomainPasses.cs:243-358](file:///d:/code/cs2j/src/CSharpToJava.Core/Pipeline/Passes/UnsupportedDomainPasses.cs#L243-L358) |
| NativeMethods.cs 文件名豁免 | 文件名恰为 `NativeMethods.cs` 则跳过 native-interop 检查；项目管线在 [ProjectPasses.cs:517](file:///d:/code/cs2j/src/CSharpToJava.Core/Pipeline/Passes/ProjectPasses.cs#L517) 与 [SingleFilePasses.cs:200](file:///d:/code/cs2j/src/CSharpToJava.Core/Pipeline/Passes/SingleFilePasses.cs#L200) 调用；其注释声称 ClassTransformer「已为 extern/DllImport 发抛 UnsupportedOperationException 的 stub」 | [UnsupportedDomainPasses.cs:373-381](file:///d:/code/cs2j/src/CSharpToJava.Core/Pipeline/Passes/UnsupportedDomainPasses.cs#L373-L381) |
| 状态机内的 stackalloc 先例 | goto 消除器对「提升的 Span 局部遇 stackalloc 赋值」已有降级 `new T[n]` 先例（绕 CS8353） | [StateMachineBuilder.cs:395-425](file:///d:/code/cs2j/src/CSharpToJava.Core/GotoEliminator/StateMachineBuilder.cs#L395-L425) |
| InlineArray（C# 12） | **零处理**：src/config/java/tests 全仓 grep 仅命中 catalog 一处，转换器无任何防线（落入默认分支） | 全仓 `InlineArray` 仅 [preprocessor-catalog.md](file:///d:/code/cs2j/prompts/preprocessor-catalog.md) 提及 |
| compat 互操作资产 | `Unsafe.java`、`MemoryMarshal.java`、`GCHandle.java`（alloc/addrOfPinnedObject 返回 MemorySegment，无真实 pin）、`MemorySegmentHelper.java` 均已存在 | [compat 目录](file:///d:/code/cs2j/java/csharptojava-compat/src/main/java/io/github/ningpp/compat) |

现有测试簇实测红绿（2026-09-28，`dotnet test -c Release --no-build`，既有 Release 产物）：

| 测试类 | 结果 |
|---|---|
| `FixedStatementFfmTests` | 64 中 **4 过 60 败** |
| `UnsafeMethodPointerTests` | 40 中 **18 过 22 败**（过的多因签名静默映射 MemorySegment，方法体未触指针表达式） |
| `StackAllocArrayCreationTests` | 1 中 **0 过 1 败** |
| `PointerPostIncrementShortCircuitTests` | 6 中 0 过 6 败 |
| `PointerCastToIntegralTests` | 1 中 0 过 1 败 |
| `JsonByteSpanPointerValueTaskTests` | 3 中 0 过 3 败 |
| `UnsafeReadUnalignedVectorTests` / `UnsafeAsUnsignedBoxingTests` | 各 1 过 0 败 |

核心三类合计 105 个测试 22 过 83 败。结论：转换器内 FFM 兜底是大面积失修的半成品（fixed scope 注册 API 零调用即根因）。C06 的主张因此是**以 C#→C# 侧归一为主战场**：指针/fixed/stackalloc 在 C# 层消灭为数组/Span/索引；转换器内 FFM 语句级代码列长期退役清单（§10），仅保留字段缓冲区与 M4 白名单两条受控 Java 侧路径。

### 1.2 extern 矛盾实测澄清（2026-09-28，Release CLI 实跑）

catalog §4 风险16 标记了「ClassTransformer 抛异常」与「IsPInvokeContainerFile 注释声称已发 stub」的矛盾。为消除歧义，使用 `src/CSharpToJava.CLI/bin/Release/net10.0/CSharpToJava.CLI.dll` 对三类入口实跑（临时探测目录已删除，结论如下）：

1. **单文件 `convert`，文件名 `Sample.cs`（含 `[DllImport]` + `extern`）**：检查阶段即报 CS2J3201（DllImport 属性，:7:6）+ CS2J3203（extern 方法，:8），exit 1，无 Java 输出。根本原因在 [ConversionPipeline.cs:148](file:///d:/code/cs2j/src/CSharpToJava.Core/Pipeline/ConversionPipeline.cs#L148)：`CSharpSyntaxTree.ParseText(request.SourceCode)` **未传 FileName**（虽然 Program.cs 把输入路径放进了 request.FileName），导致 SyntaxTree.FilePath 为空，`IsPInvokeContainerFile` 永不命中——**豁免在单文件管线恒不生效**。
2. **单文件 `convert NativeMethods.cs`**：与上相同，仍报 CS2J3201/CS2J3203，同样不豁免。
3. **项目管线 `convert-project`（最小 net10.0 工程 ProbeLib：`NativeMethods.cs` + `Other.cs` + `AllowUnsafeBlocks`）**：15 个预处理 pass 全过、中间树 build 绿；边界检查因文件名豁免未发 3201/3203，文件流入 emit；最终 [ClassTransformer.cs:349](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Type/ClassTransformer.cs#L349) 抛 `NotSupportedException`：「Failed to convert type 'NativeMethods' (Class): CS2J normalization required: extern/DllImport method 'Beep' … must be lowered by a preprocessor」，管线捕获为 `[Error]`，exit 1；输出目录仅有 `Other.java` 与 `CsPrelude.java`，**没有 `NativeMethods.java`，任何路径都不产生 stub**。
4. 附带实测：含 `unsafe { fixed (int* p = arr) ... }` 的单文件在 emit 阶段报「unsafe blocks must be lowered」，与源码 fail-fast 文案一致。

**实测结论（本设计以其为准，不采信旧注释任何一方的转述）**：

- 矛盾双方中**抛异常一方为真**，[UnsupportedDomainPasses.cs:373-381](file:///d:/code/cs2j/src/CSharpToJava.Core/Pipeline/Passes/UnsupportedDomainPasses.cs#L373-L381) 的「already emits stub bodies」为陈旧假陈述；
- 行为分两层：非 `NativeMethods.cs` 命名文件在检查 pass 即被 CS2J3201/3203 拦截；`NativeMethods.cs` 命名文件在**项目管线**逃过检查后在 ClassTransformer 硬失败；**单文件管线豁免恒不生效**（独立的 FileName 丢失 bug）；
- 不存在「静默 stub」，但存在「注释引导错误预期」+「单文件/项目管线行为不一致」两个必须在 C06 修正的问题。

另：catalog C06 条目引用的 4 份旧设计文档中，[2026-05-30-csharp-stack-design.md](file:///d:/code/cs2j/docs/superpowers/specs/2026-05-30-csharp-stack-design.md) 实际讲的是 `Stack<T>` 集合→`CSharpStack` 替换类，与 stackalloc 完全无关（catalog 错挂）；`2026-06-02-address-of-local-pointer-design.md` 在 specs 目录不存在；[2026-05-31-fixed-statement-ffm-design.md](file:///d:/code/cs2j/docs/superpowers/specs/2026-05-31-fixed-statement-ffm-design.md) 与 [2026-05-31-unsafe-method-pointer-params-design.md](file:///d:/code/cs2j/docs/superpowers/specs/2026-05-31-unsafe-method-pointer-params-design.md) 是旧 Java 管线 FFM 直翻方案（fixed→MemorySegment + 方法级 fixed scope 注入），其测试簇 60/64 红灯即该方案失修的证据，本设计仅复用其 layout 表，不复用其主路线。

### 1.3 目标与分规则迭代范围

目标：新增 C#→C# 归一 pass `unsafe-ffm`，在中间树中**消灭** unsafe/fixed/指针/stackalloc 构造（ref struct 与 extern 为受控拒绝/白名单归一），使下游转换器不再接触这些构造；一切不可安全归一者显式诊断拒绝，禁止 TODO 注释、占位实现与原样透传（catalog §4 风险12）。

按 catalog 建议与风险递增顺序分四段迭代，每段独立可提交：

1. **M1 fixed + unsafe 剥离**：unsafe 块/修饰符剥除、fixed 语句（托管源）、fixed 字段缓冲区→数组字段；
2. **M2 stackalloc**：显式/隐式统一→堆数组/Span 包装；
3. **M3 pointer**：指针签名白名单、取址/解引用/下标/算术/比较→数组索引；
4. **M4 extern / ref struct / InlineArray**：默认显式拒绝 + 受控 FFM 白名单（opt-in 子段）+ C# 12 盘点。

---

## 2. 关键设计决策表

| # | 子规则 | 决策 | 拒绝/边界 |
|---|---|---|---|
| D1 | 指针主形态 | 纯托管指针一律归一为**数组/`Span<T>` + int 索引**，不走 Java FFM `MemorySegment`（旧直翻路线被 83 红灯证伪） | 指针指向不可静态证明（存字段、逃逸、返回）→ UF020 |
| D2 | fixed 语句（数组/`&arr[0]`/指针别名） | 块退化为普通块；指针→数组引用，偏移→int 索引；`fixed(int* p = q)` 别名在数据流模型中传播 | — |
| D2b | fixed 语句（`&scalar`） | 提升为单元素数组槽（`T[] _p = new T[]{x};`，块末 `x = _p[0];` 写回），语义等价于固定标量地址 | 取址逃逸出块 → UF020 |
| D2c | fixed 语句（`string`） | 归一为 `char[] _p = s.ToCharArray()` 拷贝，按**只读**处理（C# 对固定字符串指针写入本属未定义行为） | 体内对 `_p[i]` 赋值 → UF030 |
| D3 | fixed 字段缓冲区 | 纯托管结构：`fixed byte buf[256];` → `byte[] buf = new byte[256];` | 所在类型带 `[StructLayout(Sequential/Explicit)]`/`[FieldOffset]` 且进入 extern 签名（布局锁）→ UF031，交 M4 白名单 |
| D4 | unsafe 块/修饰符 | 块剥除保留语句；类型/方法/字段/构造/接口/记录的 unsafe 修饰符剥除（unsafe 仅是编译许可，无语义）；中间树 `allowUnsafe: true` 保持（适配器默认已是，[ProjectPassAdapter.cs:184](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/ProjectPassAdapter.cs#L184)） | 无拒绝面，M1 即做 |
| D5 | stackalloc | 统一改写 `new T[n]` 堆数组；`Span<T>`/`ReadOnlySpan<T>` 目标包装为 compat 可映射的 Span（底层仍是数组）；**隐式 stackalloc 静默路径一并收编**（消灭 [ExpressionTransformerRegistry.cs:108-133](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/ExpressionTransformerRegistry.cs#L108-L133) 的静默堆化） | 目标是裸指针且无 Span 化可能、`T` 为非可封送类型 → UF040 |
| D6 | 指针签名 | 白名单模式：`byte* p, int len` 等「指针+长度」→ `byte[]`/`Span<byte>`；返回指针→数组/Span。元素步长由 `IPointerTypeSymbol.PointedAtType` 计算（保持 C# 元素单位语义） | `void*`、`delegate*`、泛型指针、非基元 struct 指针形参 → UF021（默认拒绝，不做通用 struct 指针） |
| D7 | 指针表达式 | `&x`/`*p`/`p->m`/`p[i]`/`p±n`/`p-q`/比较/`++ --`/`(int*)p`：在 D1 分类表内全部索引化 | struct 指针 `p->m` 在 M3 默认 UF020（无法证明指向数组元素时）；指针↔整数转换 UF021，除「同数组内差转 int」 |
| D8 | extern/DllImport | **默认显式拒绝**（UF060，取代两层不一致现状）；opt-in FFM 白名单：配置驱动（library+entrypoint+可证明封送布局），`[LibraryImport]`（源生成、编组可控）优先；白名单在 C# 层归一为受控 interop facade（见 §4 R8），Java 侧由 FFM 发射新通道承载 | 非白名单 UF060（消息指引 C42 隔离通道）；布局不可证 UF061；`Marshal`/`NativeLibrary`/`SafeHandle`/`HandleRef` UF070（与 CS2J3202 归并指引）；**永不发静默 stub** |
| D9 | ref struct | 用户自定义 `ref struct` 声明默认拒绝（Java 无栈-only 不可装箱类型）；BCL 白名单 `Span<T>`/`ReadOnlySpan<T>` 映射 compat；签名位置归 C01 既有 holder，本 pass 只处理声明与方法体使用点 | 白名单外声明 → UF050；**`using ref struct` 站点由 C06 M4 新增 R9（`RefStructUsingRewriter` + UF055）拥有**（2026-09-29 裁决，见 C44 §13.1 裁决 1）：Span 族白名单 v1 随 R9 给空操作归一，非白名单 ref struct using → UF055 显式诊断；await using 归 C38、yield+using 归 C04 |
| D10 | 封送布局锁定不变式 | 任何进入 D8 白名单的改写必须冻结：① 布局以 [FfmHelper.cs](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Utilities/FfmHelper.cs) 的 ValueLayout 表为单一真相；② `char`=UTF-16 两字节、`bool`=1 字节（非平台默认 4 字节）；③ 字符串固定为只读拷贝（写穿不回写，旧设计已知差异）；④ `StructLayout`/`Pack`/`FieldOffset` 不变；⑤ ref/out 实参不穿 ABI（复用 C01 `IsLocked` 谓词） | 任一不可证明 → UF061 |
| D11 | InlineArray | 全仓零处理；M4 前独立盘点（sdk 命中率扫描），默认显式拒绝 | 命中 → UF051，不自动改写 |
| D12 | 支撑库归属 | **首选零新 C# 支撑工程**：产物仅用 BCL 数组/Span；Java 侧复用既有 compat（Span/ReadOnlySpan/GCHandle/MemoryMarshal/Unsafe/MemorySegmentHelper）。`PassSupportLibrary` 枚举**不新增**；若 M2 证明需要 C# helper 再走 UnsignedPassAdapter 式注入 | — |
| D13 | 语义模型 | `needsSemanticModel = true`；全量 `CSharpCompilation`（allowUnsafe）绑定一次；指针分类、封送布局、ref struct 判定全部基于 `ISymbol`/`ITypeSymbol`，禁止文本匹配 | 跨方法信息在单文件模式缺失 → UF090 跳过项（同 JK090 模式） |
| D14 | 产物 residual 闸门 | 每个文件改写后做语法节点扫描：不含 pointer/fixed/stackalloc/unsafe/extern（白名单 interop facade 与 UF 拒绝文件除外）；命中即 UF010 Error，文件不产出 | 直连转换器 fail-fast 全部保留为安全网（同 C01/C08 范式） |

开放问题（里程碑中回答，不阻塞 M1）：白名单配置格式（独立 json 还是 csproj 标注项）、`void*` 是否对 `Span<byte>` 透传场景开窄口、`p->m` struct 指针是否值得 M3 后追加规则、D2b 单元素槽对 `in` 参数取址的写法。

**2026-09-29 拍板追加（开放问题已决，见 C44 §13.1 裁决 1）**：M4 新增 **R9 `RefStructUsingRewriter` + UF055**，拥有全部 ref struct `using` 站点——原"该子题归 C44、C06 不触碰"的措辞（D9/R7/§10/§11 四处）以本裁决为准作废；Span 族白名单 v1 随 R9 给空操作归一，非白名单 ref struct using 发 UF055；await using 归 C38、yield+using 归 C04。

---

## 3. 注册表与链位契约

[PassStubCatalog.cs:130-139](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassStubCatalog.cs#L130-L139) 现状（2026-09-29 核订重定位）：id `unsafe-ffm`，目录 `.cs2j-unsafe-ffm-src`，`PassStage.Abi`，`PassStatus.Stub`，`defaultEnabled: false`，`runAfterIds: { struct-value-semantics }`，catalogItems `"C06"`，描述 "Lower pointers/stackalloc/fixed buffers/extern to index, Span and FFM forms."。

**冻结契约表**（仿 C01 §3，实现中不得变更；变更需另开评审）：

| 契约项 | 当前值（证据） | 本任务动作 |
|---|---|---|
| pass id | `unsafe-ffm`（[PassStubCatalog.cs:131](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassStubCatalog.cs#L131)） | 不动 |
| Stage | `PassStage.Abi`（:134） | 不动 |
| Status | `Stub`（:135）；启用即 CS2J9001（[PreprocessDiagnosticCodes.cs:12](file:///d:/code/cs2j/src/CSharpToJava.Core/Preprocessing/PreprocessDiagnosticCodes.cs#L12) 常量定义、[ProjectPassRunner.cs:74](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/ProjectPassRunner.cs#L74) 消息构造、[CliPreprocessChain.cs:133-140](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/CliPreprocessChain.cs#L133-L140) 在 `foreach (plan.Enabled)` 内落盘前拒绝——**2026-09-29 核订改正**：原文引的 `CliPreprocessChain.cs:131-136` 并非消息产出点） | 翻转为 `Implemented`，stub 条目移到 `PassCatalog.Implemented` 序列；CS2J9001 对本 id 不再适用 |
| 默认 | `defaultEnabled: false`（:136） | **保持默认关**（论证见 §8）；经 `--enable-pass unsafe-ffm` 或独立 verb 开启 |
| 顺序锚点 | `RunAfter = { struct-value-semantics }`（:137） | 锚点不动。C07 同为 stub（[PassStubCatalog.cs:120-129](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassStubCatalog.cs#L120-L129)，其锚点为 make-readonly）。禁用 stub 不进 Enabled 计划（**2026-09-29 核订改正裁决点**：判定在 [PassPlanResolver.cs:70](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassPlanResolver.cs#L70) 回落到 `DefaultEnabled`，启用集合消费在 [CliPreprocessChain.cs:104-111](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/CliPreprocessChain.cs#L104-L111)；原文引的 `PassRegistry.cs:95-133` 实为 `TopologicalSort`，与启用判定无关）：翻转后本 pass 的实际执行位在 make-readonly 之后、eliminate-goto 之后（concat 声明序+拓扑推导）。C07 落地后拓扑自然变为 `… → struct-value-semantics → unsafe-ffm`，**链序测试只断言相邻已知锚点，不断言中间夹未实现 pass**；若 M2 fixture 证明与 goto 提升有时序交互，补 `RunAfter eliminate-goto`（在本任务评审范围内） |
| 中间目录 | `.cs2j-unsafe-ffm-src`（:133） | 不动；由 [CliPreprocessChain.cs:104-106](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/CliPreprocessChain.cs#L104-L106) 的 ExecutedIntermediateDirectoryNames 统一保留 |
| 语义模型/verify | stub 无值；语义 pass 惯例 build/test verify 均 true | `needsSemanticModel = true`，verify build/test 默认 true；禁止靠关 verify 过闸 |
| 支撑库 | — | `PassSupportLibrary.None`（D12）；[PassChainFactory.cs:264-272](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassChainFactory.cs#L264-L272) 的支撑库过滤无需改动 |
| catalogItems | `"C06"`（:139） | 不动 |
| 顺序回归锁 | [PassChainOrderTests.cs](file:///d:/code/cs2j/tests/CSharpToJava.Tests/Preprocessing/PassChainOrderTests.cs) | 新增 unsafe-ffm 顺序断言；默认关计划中不出现、启用计划中出现 |

**翻转清单**（stub→Implemented 时的全部触点）：

1. 删除 [PassStubCatalog.cs:130-139](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassStubCatalog.cs#L130-L139) 的 UnsafeFfm 条目（`Ids.UnsafeFfm` 常量可保留为私有引用或随条目删除，C07 落地前无 RunAfter 方需要它；**2026-09-29 核订提示**：C04 已确立「翻转后仍把 id 常量留在 `PassStubCatalog.Ids`」的实例——`PassCatalog.cs:210` 反向引用 `PassStubCatalog.Ids.IteratorLowering`；C02 则迁到了 `PassIds`。两种先例并存，本 pass 需与评审选定的 id 常量归属保持一致）；
2. 在 [PassCatalog.cs](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassCatalog.cs) Implemented 序列按拓扑位新增描述符（id、Stage.Abi、目录、defaultEnabled false、RunAfter struct-value-semantics、support None、语义模型/verify）；
3. [PassChainFactory.CreateAdapter](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassChainFactory.cs#L98) switch 新增 `unsafe-ffm` 分支 → `new UnsafeFfmPassAdapter()`（核订重定位：方法现起于 L98，`MemberDesugar`/`JavaKeywords` 分支已在其后落地）；
4. `BuildLegacyToggles`（[PassChainFactory.cs:64-85](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassChainFactory.cs#L64-L85)）**不加**旧开关（新 pass 无历史开关，只走 `--enable-pass/--pass/--list-passes` 统一面）；
5. `ProjectPreprocessorsConsistentTests` 追加描述符/目录一致性断言；
6. Program.cs **零手写接线**（f68a783c 已落地统一链，convert-project 自动消费 descriptor——这是相对早期 C08 设计四处手写接线的基建红利）。

默认关时现链行为零变化：无目录、无耗时、无诊断。

---

## 4. Core 组件设计

新建目录 `src/CSharpToJava.Core/UnsafeFfm/`，按 foundation plan 交付包模板（Options/Statistics/Diagnostic/Result/Preprocessor/Rewriter）组织：

| 文件 | 职责 |
|---|---|
| `UnsafeFfmOptions.cs` | 规则开关 `EnableFixed`/`EnableStackAlloc`/`EnablePointer`/`EnableExtern`（里程碑分段开关，默认随 pass 开启已交付段）、`ExternWhitelistPath`、`Strict` |
| `UnsafeFfmDiagnostics.cs` | UF0xx 诊断描述（见 §6） |
| `UnsafeFfmStatistics.cs` | 按规则命中数、拒绝数、residual 数、隐式 stackalloc 收编数 |
| `UnsafeFfmResult.cs` | 诊断+统计+Changed 汇总 |
| `UnsafeFfmPreprocessor.cs` | 门面：`RewriteFile(code, SemanticModel, options)`，仿 [UnsignedTypeRewriter](file:///d:/code/cs2j/src/CSharpToJava.Core/UnsignedTypeRewriter/UnsignedTypeRewriter.cs)；单次 parse、内存中链式跑 R1–R8、末端 residual 闸门 |
| `UnsafeFfmAnalyzer.cs` | 编译级分析（跨文件编译绑定一次）：① PointerFlowModel：每个指针符号 → 指向源（数组元素/Span/固定缓冲区字段/标量槽/参数模式/未知）+ 偏移表达式；② extern 声明表与白名单匹配；③ ref struct 声明清单；④ fixed 字段所属类型的 StructLayout/extern 使用分类 |
| `MarshalLayoutTable.cs` | ValueLayout/字节数/char/bool 规则表，真相与 [FfmHelper.cs](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Utilities/FfmHelper.cs) 对齐（M4 抽共享，避免双份分叉） |
| `UnsafeContextStripper.cs`（R1） | unsafe 块退化为内部语句（嵌套递归）；类型/成员/接口/记录 unsafe 修饰符剥除 |
| `FixedBufferFieldRewriter.cs`（R2） | fixed 字段→数组字段声明与所有 `s.buf[i]` 用法不变（数组字段天然支持索引）；StructLayout+extern 使用分类 UF031 |
| `FixedStatementRewriter.cs`（R3） | 五类 fixed 初始式（D2/D2b/D2c/D3 字段/指针别名）→ 数组或槽局部 + 别名注册进 PointerFlowModel；块体改普通 Block |
| `StackAllocRewriter.cs`（R4） | 显式/隐式 stackalloc 统一：`Span<T> x = stackalloc T[n]` → `T[] x = new T[n]`（经 compat Span 包装由下游既有映射承担）；初始化器、维度省略一并支持；与 [StateMachineBuilder.cs:395-425](file:///d:/code/cs2j/src/CSharpToJava.Core/GotoEliminator/StateMachineBuilder.cs#L395-L425) 既有 CS8353 降级形态保持一致（同一目标形态，避免两 pass 互相返工） |
| `PointerSignatureRewriter.cs`（R5） | 指针参数/返回/局部按 D6 白名单改数组/Span；`void*`/`delegate*`/泛型指针 → UF021 |
| `PointerExprRewriter.cs`（R6） | D7 表达式族索引化：`*p`→`arr[idx]`、`p[i]`→`arr[idx+i]`、`p+n` 模型内传播（不立即物化）、`p-q`→同数组 int 差、比较/`++ --`→int 索引、`&arr[i]` 取址模式消解；无法证明 → UF020/UF021 |
| `RefStructDeclRewriter.cs`（R7） | 白名单外 `ref struct` 声明 → UF050；使用点不重写（签名位置属 C01）；**不碰 using 站点——`using ref struct` 由 M4 新增 R9 `RefStructUsingRewriter` 拥有**（2026-09-29 裁决，见 C44 §13.1 裁决 1） |
| `RefStructUsingRewriter.cs`（R9，M4 新增） | 全部 ref struct `using` 站点：Span 族白名单（`Span<T>`/`ReadOnlySpan<T>`）v1 **空操作归一**（该 using 无资源释放语义）；非白名单 ref struct using → **UF055** 显式诊断；await using 不在本规则（归 C38），yield+using 归 C04 |
| `ExternInteropRewriter.cs`（R8） | D8：非白名单 extern/DllImport/LibraryImport → UF060/UF061；白名单 → 规范化 interop facade（删除原 P/Invoke 声明，替换为受控 partial 方法声明 + 白名单 id 标注，供 Java 侧 FFM 发射通道识别）；`Marshal` 等 → UF070 |
| `UnsafeFfmResidualGate.cs` | D14 输出树节点扫描（`PointerTypeSyntax`/`FixedStatementSyntax`/`FixedPointer`/`StackAllocArrayCreationSyntaxBase`/`UnsafeStatementSyntax`/extern 修饰符），命中 UF010 |

关键工程约束：

- **一次 parse 内存合批**：R1–R8 在单次 RewriteFile 内顺序执行（每规则基于上一规则输出树），不做多次磁盘往返，与 catalog §4 风险19 的统一引擎方向一致；
- **拒绝优先**：任一规则产出 Error 即该文件 `Changed=false` + 整体失败，不允许「部分改写 + TODO 注释」；
- **幂等**：输出二次 RewriteFile 必须 Changed=false、无诊断（所有重写节点不可被自身再识别）；
- **C# 层产物不引用 Java 类型**：D8 白名单 facade 在 C# 层是可编译的受控 C# 声明（extern 方法体的 FFM 实现在 Java 发射阶段生成），因此 .NET 半区 build/test 可过，但白名单条目的**运行时**等价性只能由 Java 半区验证（§7.4），这是白名单 opt-in 而非默认开的根本原因；
- 单文件独立 verb：方法内规则（R1/R3/R4/R6 主体）完整可用；跨文件 extern 白名单与字段布局分类发 UF090 跳过（D13）。

---

## 5. CLI 适配器与集成

新建 [Adapters/UnsafeFfmPassAdapter.cs](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing)（约 150–250 行），继承 `ProjectPassAdapter`，仿 [UnsignedPassAdapter.cs](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/Adapters/UnsignedPassAdapter.cs)：

- 标准 spine 直接复用：parse → compile（allowUnsafe 默认 true）→ 逐文件 `RewriteFileAsync`（调 `UnsafeFfmPreprocessor`，传 SemanticModel/Options/统计收集器）→ `AfterFilesAsync`（**无支撑库释放/csproj 注入**，support=None）→ restore → verify（build/test 默认开）；
- 名字碰撞 fail-fast 等既有保护随 spine 继承；
- UF0xx 诊断经适配器既有诊断通道落盘报告；
- Program.cs `convert-project` 不加手写选项块：统一 `--enable-pass unsafe-ffm`（[Program.cs:4458](file:///d:/code/cs2j/src/CSharpToJava.CLI/Program.cs#L4458) 起的通用 `preprocess` verb 已支持，核订重定位）；独立用法 `preprocess --pass unsafe-ffm -s <proj> -d <out>`；`--list-passes` 自动显示 Implemented/默认关；
- 中间目录保留、指纹（pass 全集令牌）、过滤全部由 descriptor 驱动，无额外接线；
- 独立 bug 修复（M1 顺手项或单独 PR）：[ConversionPipeline.cs:148](file:///d:/code/cs2j/src/CSharpToJava.Core/Pipeline/ConversionPipeline.cs#L148) 的 `ParseText` 传入 `request.FileName`，使单文件管线 `IsPInvokeContainerFile` 等一切路径敏感判定与项目管线一致；修正 [UnsupportedDomainPasses.cs:373-381](file:///d:/code/cs2j/src/CSharpToJava.Core/Pipeline/Passes/UnsupportedDomainPasses.cs#L373-L381) 的陈旧 stub 注释为实测行为描述。

---

## 6. 诊断策略

新增 UF0xx 码段（Error 为主，体现「响亮失败」原则）：

| 码 | 级别 | 触发 |
|---|---|---|
| UF010 | Error | residual 闸门：输出树仍残留 unsafe 家族节点（内部错误性质，指向规则缺口） |
| UF020 | Error | 指针指向不可静态证明（存入字段/返回托管地址/逃逸出 fixed 块/struct 指针 `p->m`） |
| UF021 | Error | 不支持的指针种类：`void*`、`delegate*`、泛型指针、指针↔非 int 整数转换、非基元 struct 指针形参 |
| UF022 | Error | 指针算术产生不可回填的逃逸偏移 |
| UF030 | Error | 对 fixed 字符串缓冲写入（写穿不可回写） |
| UF031 | Error | fixed 字段缓冲区所在类型带显式 StructLayout 且进入 P/Invoke 签名（布局锁，交白名单/手工） |
| UF040 | Error | stackalloc 目标为裸指针或元素类型不可堆化 |
| UF050 | Error | 用户自定义 `ref struct` 声明（白名单外） |
| UF051 | Error | InlineArray 构造（盘点期拒绝） |
| UF060 | Error | extern/DllImport/LibraryImport 不在 FFM 白名单；消息含「平台隔离通道（C42）」指引 |
| UF061 | Error | 白名单条目封送布局不可证明（union/无布局 struct/字符串写穿/ref 实参穿 ABI/bool 宽度冲突） |
| UF070 | Error | `Marshal`/`NativeLibrary`/`SafeHandle`/`HandleRef` 使用，指引与 CS2J3202 相同的隔离去向 |
| UF090 | Warning | 单文件模式下跨文件 plan 跳过（extern 白名单、字段布局分类） |

与既有诊断的协同：

- CS2J3201/CS2J3203（[UnsupportedDomainPasses.cs:243-358](file:///d:/code/cs2j/src/CSharpToJava.Core/Pipeline/Passes/UnsupportedDomainPasses.cs#L243-L358)）在**未启用** C06 时行为不变；启用 C06 后，非白名单 extern 由 UF060 先报（C06 在 Abi 段先跑），白名单归一后的 facade 形态需与 C42 共享一个「已归一互操作声明」判定谓词，避免 3201/3203 重复报；
- CS2J3001/3101/3102 与 UF060/UF070 的分工：3xxx 是平台边界族（C42/C10 维护）的通用拒绝，UF 码是 C06 的归一判定；消息互相引用，不重复诊断同一节点；
- 转换器侧 fail-fast（§1.1 表中 throw 点）在 C06 后**全部保留**：预处理未跑/直连单文件/白名单外残留时它们仍是最后安全网；M3/M4 完成后评估其中因输入树已归一而不可达的分支，按 C08 冗余清单范式退役（注释标注「C06 residual gate 前置」），不删除异常本身。

---

## 7. 测试计划

### 7.1 Core 单测（tests/CSharpToJava.ProjectTests）

新建 `UnsafeFfmRewriterUnitTests.cs`，内联源码→编译（allowUnsafe）→RewriteFile→文本断言 + `AssertOutputCompiles`（仿 RecordReadOnlyRewriterUnitTests/UnsignedTypeRewriterUnitTests 惯例），按规则分组：

- R1：unsafe 块嵌套剥除、unsafe class/method/field/ctor/interface/record 修饰符；
- R2：byte/char/int fixed 字段→数组；StructLayout+extern 使用→UF031；多字段；
- R3：数组、`&arr[0]`、`null`、多变量、嵌套 fixed、指针别名；`fixed(char* p = s)` 只读；写入→UF030；`&scalar` 单元素槽写回；体内 `p[i]/*p/p+n/p++/p-q/比较` 全索引化；
- R4：显式/隐式 stackalloc、`Span<byte>`/`ReadOnlySpan<char>` 目标、带初始化器（覆盖 StackAllocArrayCreationTests 现有红灯形态）、维度省略、裸指针目标→UF040；
- R5/R6：`byte*`+len → `byte[]`；指针局部源自数组；`p->m`→UF020；`(int*)p`→UF021；同数组差值；元素步长（`int*` +1 = 4 字节）；
- R7：自定义 ref struct→UF050；`Span<T>` 使用点原样；
- R8：白名单 LibraryImport 归一为 facade；非白名单 DllImport→UF060；`Marshal.PtrToString...`→UF070；union 布局→UF061；
- 横切：residual UF010、幂等二次跑、统计计数、各里程碑开关隔离。

### 7.2 项目级 fixture

新建 `tests/SampleUnsafeFfmSolution/`（Lib+App+sln，net10.0，AllowUnsafeBlocks=true），仿 SampleRefOutInSolution：fixed 缓冲区结构（消息帧拆包）、Span/stackalloc 文本解析（移植 RegexNode 风格）、指针遍历数组白名单签名、一个白名单 extern（kernel32 `Beep` 签名，不实际调用）+ 一个非白名单 extern（断言 UF060 路径）。

### 7.3 中间树与回归

- 每里程碑末：`dotnet build -c Release` slnx 全绿 + `dotnet test -c Release` 三工程（tests/CSharpToJava.Tests、tests/CSharpToJava.ProjectTests、tests/CSharpToJava.ConversionTests）全绿；verify build/test 不得关闭；
- `ProjectPreprocessorsConsistentTests`：描述符/目录/开关一致性；
- [PassChainOrderTests.cs](file:///d:/code/cs2j/tests/CSharpToJava.Tests/Preprocessing/PassChainOrderTests.cs)（`LockedChain` 现位于 [L18-40](file:///d:/code/cs2j/tests/CSharpToJava.Tests/Preprocessing/PassChainOrderTests.cs#L18-L40)，含 20 个 Implemented id）：默认关计划不含本 pass；启用计划含且位在 make-readonly 与 eliminate-goto 之后；C07 stub 翻转场景的相邻锚点断言。**2026-09-29 核订定位**：`LockedChain` 断言的是「注册表 `Ordered` 中 Status=Implemented 的子序列」，stub 按锚点夹入但不参与该断言（测试注释 L64-65 明示）——故本 pass 翻转时须把 `unsafe-ffm` 插入该数组的正确槽位，其拓扑位由 `RunAfter struct-value-semantics`（现仍为 stub，`RunAfter make-readonly`）推导，即 `make-readonly → struct-value-semantics → unsafe-ffm`，而 `member-desugar`/`java-keywords` 因锚定 stub `closure-lift`/`platform-isolation` 等被拓扑推到链尾（与 `LockedChain` 末三位一致）；
- residual 闸门专项：fixture 全量 grep 产物 `.cs2j-unsafe-ffm-src` 中 `unsafe|fixed|stackalloc|*` 指针节点为 0（白名单 facade 与拒绝文件除外）。

### 7.4 布局封送与 Java 半区（M4 白名单子段专属）

- ValueLayout 表一致性：byte/ushort/uint mask、char UTF-16、bool 1 字节、字符串只读拷贝、StructLayout Pack；
- 从 FixedStatementFfmTests 60 个红灯中挑出仍反映真实 FFM 需求的断言，重写为「白名单 facade → Java FFM 调用」新形态测试；
- 旧红灯簇处置（M1/M3 分批完成，不允许长期红灯）：83 个直接吃 unsafe 的红灯按两类改期望——预处理未跑场景改为断言 fail-fast 消息（安全网），语义场景迁移到 ProjectTests 断言 C# 中间树；`UnsafeMethodPointerTests` 的 18 个「签名映射 MemorySegment」测试改为断言数组/Span 签名。

### 7.5 CLI

仿 PreprocessVerbCLITests：`--enable-pass unsafe-ffm`、`preprocess --pass`、`--list-passes` 状态（Implemented/默认关）、默认关时输出目录零新增、UF060 时退出码与报告位置。

---

## 8. 默认开启验收门槛

决策：**长期 opt-in；仅安全子集在门槛达成后可评估默认开；extern/指针/ref struct 白名单之外永不默认开。**

安全子集候选（R1 unsafe 剥离、R4 stackalloc→堆数组、R3 托管数组 fixed 与只读字符串、R2 纯托管 fixed 字段、R6 可证明的同数组索引化）：

1. 子集规则在 ProjectTests/ConversionTests 全绿，且无 UF010 内部错误残留；
2. csharp-sdk 基线命中率画像：统计 unsafe/fixed/stackalloc/pointer/extern/ref struct 分布，子集启用后的新增拒绝率（UF020/UF040）接近 0——被拒绝文件在未启用时本就 fail-fast 才允许默认开；
3. 跑完一个发布周期的 opt-in 观察（catalog §4 风险10 门槛）；
4. 隐式 stackalloc 静默堆化旧路径退役确认（风险12：默认开后任何路径都不再静默）。

extern 白名单（D8）与用户 ref struct（D9）不设默认开时点：ABI 等价性只能逐案 Java 半区验证，默认开会把平台相关输入从「显式 fail-fast」变成「不可预测的阻断/错误映射」，与 C42 平台隔离分工相悖；这也继承 catalog §4 风险3（fixed→MemorySegment 仅字段缓冲区一条路径被验证过）的教训。

---

## 9. 里程碑

| 段 | 内容（独立可提交，默认关，verify 绿） |
|---|---|
| M0 | Core 骨架（Options/Diagnostics/Statistics/Result/Preprocessor）+ R1 unsafe 剥离全量（使翻转后的 pass 名副其实，不留 no-op 翻转）+ §3 翻转清单全部触点 + 顺序/一致性测试；顺手修 ConversionPipeline FileName bug 与陈旧注释（可拆独立 PR） |
| M1 fixed | R2 fixed 字段 + R3 fixed 语句五类初始式 + UF030/UF031；fixture 帧拆包场景；FixedStatementFfmTests 第一批期望迁移 |
| M2 stackalloc | R4 显式/隐式统一收编（消灭静默堆化路径的触发面）；与 goto 状态机 CS8353 降级形态互操作 fixture；StackAllocArrayCreationTests 迁移 |
| M3 pointer | R5 签名白名单 → R6 表达式索引化，内部再按「局部数组源 → 参数签名 → 算术/比较族」拆小提交；UF020/UF021/UF022；UnsafeMethodPointerTests/PointerPostIncrement/PointerCast/JsonByteSpan 簇改期望；评估转换器 FFM 语句级分支退役清单 |
| M4 extern/ref struct/InlineArray | R7/R8 + InlineArray 盘点报告；UF050/UF051/UF060/UF061/UF070；默认拒绝上线；FFM 白名单 facade + Java 侧 FFM 发射通道作为**独立 opt-in 子段**，按 §7.4 Java 半区验收，可晚于本里程碑交付；与 C42 的协同谓词在此段落地 |

每段 Definition of Done：Release build/test 全绿、本段开关开启时 fixture 过、关闭时现链零变化、红灯测试不留新增失败。

---

## 10. 风险

- **R-ABI（catalog §4 风险3）**：封送布局等价性是全任务风险最高面。对策=D10 冻结不变式 + 白名单 opt-in + Java 半区专项；fixed 字段→MemorySegment 旧路径仅在 70-101 这一条被实际验证，语句级 fixed FFM 已被 60 红灯证伪，不再作为路线。
- **R-extern 矛盾（风险16，已实测澄清）**：见 §1.2。现状从不发 stub；M0 修正注释与 FileName bug，M4 用 UF060/白名单取代两层不一致行为。
- **R-静默（风险12）**：现存四处静默——隐式 stackalloc 堆化（ExpressionTransformerRegistry:108-133）、`p->m` 发 `p.m`、`(T*)` 原样、unsafe 修饰符无声剥离（剥离正确但无统计）。C06 第一原则=显式拒绝+统计可见；隐式 stackalloc 路径在 M2 后对启用 pass 的输入不可达，M4 后评估直接从转换器移除该分支。
- **R-yield/using 耦合（风险2 关联）**：fixed/Span 局部与 C04 状态机提升、ref struct using 的释放时序交叉。M2 必须复用（而非另造）[StateMachineBuilder.cs:395-425](file:///d:/code/cs2j/src/CSharpToJava.Core/GotoEliminator/StateMachineBuilder.cs#L395-L425) 既有降级形态。**2026-09-29 裁决（见 C44 §13.1 裁决 1）**：ref struct using 站点由 **C06 M4 R9（`RefStructUsingRewriter` + UF055）拥有**（不再是"C44 子题、不进本任务"），Span 族白名单 v1 空操作归一；**await using 归 C38、yield+using 归 C04**，R9 与三方各出交叉 fixture。
- **R-半存活资产误判**：TypeMappingService 指针映射、FfmHelper、ElementAccess 指针分支、ConversionContext 的 PushFixedScope 零调用 API 构成「看似可用实则断链」的代码区。C06 产物树不含指针后这些路径仅服务直连安全网；M3/M4 产出退役清单（同 C08 §10 范式），防止后人误以为 FFM 直翻仍受支持。
- **R-测试债**：83 红灯的期望迁移工作量与实现等量（§7.4），不随实现自动消失；列入每段 DoD。
- **R-AllowUnsafeBlocks 交互**：链上 local-function-lift 会向提升函数加 unsafe 上下文（既有先例）；C06 在其后运行，R1 必须覆盖被提升出的 unsafe 局部函数，M1 加专项 fixture。
- **R-opt-in 长期化（风险10）**：默认关 pass 的腐烂风险（f68a783c 之前 stub 无闸门）；对策=每段 fixture+顺序测试+默认关零变化断言。
- **R-性能**：全编译语义模型+指针数据流；依赖指纹缓存与既有 spine，不引入额外编译次数。

---

## 11. 并行协调

- **C01 ref-out-in（前置依赖，已落地）**：C01 §11 已定 C06 在 C01 M2 之后 rebase、建议合并序 C01→C02→C06→C14。**2026-09-29 核订状态同步**：C01 已完成接线（`ref-out-in` 在 [PassCatalog.cs:134-145](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassCatalog.cs#L134-L145)，Status=Implemented、默认开、SupportLibrary=Cs2jRefOutIn），C02 已落地（默认关）、C14 已落地（member-desugar，默认关）——原四条合并序中的前两项与末项均已入主干，**本 pass 开工时直接 rebase 最新主干即可，不再有「等 C01 M2」的等待面**。具体共享：① ABI 锁定谓词从 [RefOutInAliasAnnotator.cs:265](file:///d:/code/cs2j/src/CSharpToJava.Core/RefOutInPreprocessor/RefOutInAliasAnnotator.cs#L265)（`IsLocked`，已识别 DllImport/LibraryImport、override 链、接口实现；核订重定位，原文引 L188-250 为陈旧区间）抽为共享谓词，C06 R8 与 C42 同用，不得复制分叉；② ref struct 签名位置的 `RefStructByRef<T>` 泛型 holder 属 C01，R7 不重复处理（形态见 §1.1 表的核订纠错）；③ D2b 单元素槽写回与 C01 调用点提升模式互操作（取址代理内调用需提升时交 C01 既有机制）。**原「M0–M2 不依赖 C01，可先做；M3 签名段开始 rebase C01 M2」的排期约束现已解除**（C01 已合）。
- **C07 struct-value-semantics（顺序锚点）**：RunAfter 锚点保留；C07 未实现期按 §3 拓扑处理（相邻锚点断言）。交叉点：R2 数组字段使 struct 持有引用类型字段，克隆/值语义与只读化归 C07；C06 只保证数组字段初始大小与索引可编译，需联合 fixture（fixed-buffer struct 经 C07 后防御性 clone 行为）。
- **C42 platform-isolation（平台边界族）**：按 catalog §3.3，C42 + C06 extern 子项 + C10 共用 CS2J3001/3101/3102/3201-3203 族。分工：**C06 负责 extern 的归一/白名单/UF060 语义判定，C42 负责非互操作输入的隔离与拒绝基建**；「已归一 facade」共享判定谓词；C06 先合（默认关不改现状），C42 落地时消费 UF 码契约。
- **C10 attribute-normalize**：C10 stub 描述含 P/Invoke annotations。分治：`DllImport/LibraryImport/MarshalAs/StructLayout/FieldOffset` 的**封送语义**归 C06 R8/布局表，其它属性归一归 C10；按属性名白名单互不重写，联合 fixture 防双改。
- **E12 array-method（unsafe 家族交叉）**：R4/R5 产物的数组静态方法归 E12；Span 切片与 `MemoryMarshal.Cast` 复用 compat Span.int32View 既有视图；两 pass 无硬链序（不同语法节点），但共享同一「数组/Span 目标形态」约定。
- **C44（2026-09-29 裁决收口，原"观察项"措辞作废）**：ref struct using 站点归属**已拍板归 C06 M4 新增 R9（`RefStructUsingRewriter` + UF055）**（见 C44 §13.1 裁决 1；本文 D9/开放问题/R7/§10 四处"归 C44、不进 C06"旧措辞已同步回填）：R7 只判声明、不碰 using；Span 族白名单 v1 随 R9 空操作归一，非白名单 → UF055。常规（引用类型/普通 struct）using 归 C44，**await using 归 C38、yield+using 归 C04**。
- **C04/C32/C45**：fixed 块内 lambda 捕获指针、yield 中 Span 局部的逃逸，M2/M3 fixture 覆盖；状态机提升发生在 C06 之前，提升出的 unsafe 局部函数由 R1/R6 兜底。
- **rebase 顺序**：base f68a783c；M0–M2 分支独立；M3 rebase C01 M2；M4 只依赖与 C42 的诊断码契约（UF060/共享谓词签名先冻结），不要求 C42 代码先合。

---

## 12. Worktree 分配

- 分支名：`feature/preproc-c06-unsafe-ffm`
- worktree 目录：`.worktrees/preproc-c06-unsafe-ffm`
- base 提交：`f68a783c`（`feat(preprocessing): register 23 merged-family stub passes`，全哈希 `f68a783cd74cbfc398204bcdabe000e4f881ad30`，已验证存在）。**2026-09-29 核订**：原文记「且为当前 HEAD」——主干现已推进到 2d25700e（C01-C04、C08、C11-C14 落地），f68a783c 成为祖先；base 值本身仍正确，但开工前须 rebase 到最新 `dev-3.x`，冲突热点集中在 `PassStubCatalog.cs`/`PassCatalog.cs`/`PassChainFactory.cs`/`Program.cs`/`PassChainOrderTests.cs` 五个文件（各分支只增自己的条目）。
- 创建命令：

```powershell
git worktree add .worktrees/preproc-c06-unsafe-ffm -b feature/preproc-c06-unsafe-ffm f68a783c
```

（`.worktrees/` 已被 .gitignore 忽略；本设计阶段不执行该命令。）
