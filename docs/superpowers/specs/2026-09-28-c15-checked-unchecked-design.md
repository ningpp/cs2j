# C15 checked / unchecked 归一（numeric-boundary 锚点任务）设计

**日期**: 2026-09-28
**范围**: 新增 CLI 级 C#→C# 预处理器族 `numeric-boundary` 的共享基建与 checked/unchecked 规则（Core `NumericBoundary/` + CLI 单适配器），把 checked 上下文/表达式显式化为溢出检查辅助调用、把 unchecked 上下文/表达式剥离为裸算术；本特性不改任何 C#→Java 转换代码
**状态**: 已实施完成（M2/M3/M4，2026-09-30）：numeric-boundary 已翻转为默认开启（PassCatalog/PassChainFactory），checked 站点显式化、unchecked 剥离、工程级 /checked 改写并剥离 csproj 标志、Cs2jUnsignedMath 双侧支撑库（含 checked 算术与 29 件转换族）、NB-12/NB-13 闸门、member-desugar voucher 共处均已落地；三工程测试 3457 全绿、compat mvn 1584 全绿、C#→Java 冒烟验证 ArithmeticException。实施记录见 [2026-09-28-c15-checked-unchecked.md](file:///d:/code/cs2j/.worktrees/preproc-c15-checked-unchecked/docs/superpowers/plans/2026-09-28-c15-checked-unchecked.md)
**校订（2026-09-29）**: ①catalog 链接行号按当前文件重算（§3.3 数值边界族 L1084；§4 风险 8/10/11/12/14 为 L1106/1108/1109/1110/1112）；②转换器锚点行号校正（checked/unchecked 块抛点实为 StatementTransformer.SwitchAndResource.cs L303-L309，消息行 L305/L309；其余链接逐一核实）；③注册表事实更正：numeric-boundary stub 条目在 PassStubCatalog.cs L150-L159（非 L161-L170），Ids.NumericBoundary 常量仍在 L33；④翻转清单改按 IteratorLowering 先例——id 常量**保留**在 PassStubCatalog.Ids，不新增 PassIds 成员、默认不新增 Program.cs 独立开关（是否要 `--numeric-boundary` 专用开关列开放问题 7）；⑤stub 计数更正：现状实有 21 条，PassStubCatalogTests.cs:24 断言 22 已与实现漂移（26ea367c 漏改），翻转后 20 条、断言改 20；⑥C14 状态更新：member-desugar 已翻转为 Implemented（默认关），op_Checked* 已在 member-desugar 处理，C15/C14 链序交接补入决策 4/NB-11/NB-13/§11/开放问题 6；⑦链位预期序列与测试行号对齐当前主干（含 generic-deepen/iterator-lowering/member-desugar）；⑧worktree base 由 f68a783c 更新为当前 HEAD 2d25700e。
**校订（2026-09-30）**: worktree base 由 2d25700e 更新为 dev-3.x 最新提交 `30d13211`（`feat(preprocessing): C31 Index/Range 落地 + 已落地 pass 默认开启收口`）；§12 的 base 与创建命令同步。base 前进后本文所引代码行号可能漂移，实施时一律以 30d13211 实测为准（§3.2 链序亦以实测拓扑序为准）。
**族属**: catalog P2「数值边界族」（E09 后缀规则 + C15 + C17 + C22 + C34 + C41 + enum 运算，[preprocessor-catalog.md §3.3](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L1084-L1084)）。**C15 是该族首个落地规则与锚点任务**：共享基建与注册表翻转在本任务完成，C17/C22/C34/C41 基于本任务合入后的 pass 增量扩展

---

## 1. 背景与目标

### 1.1 checked / unchecked 的 C# 语义

C# 整数算术的溢出行为由溢出检查上下文（overflow checking context）决定：

- **checked 上下文**：内置整数运算（二元 `+ - *`、一元 `-`、`++ --`）与整数间显式数值转换一旦溢出，运行时抛 `System.OverflowException`；
- **unchecked 上下文**：上述运算按二补宽回绕，不抛异常；
- **默认上下文**：非常量表达式默认 unchecked（除非工程开启 `<CheckForOverflowUnderflow>true</CheckForOverflowUnderflow>`）；**常量表达式无论工程设置一律按 checked 求值**，须显式 `unchecked(...)` 包裹（CS0220/CS0221）。

不受 checked/unchecked 影响的运算：整数 `/ %`（除零恒抛 `DivideByZeroException`；`int.MinValue / -1` 恒抛 `OverflowException`）、位运算与移位、`decimal` 算术（始终检查）、浮点算术（不抛溢出）、用户自定义运算符（本质是方法调用）、enum 与底层类型间的转换（C# 规范保证 enum 转换不抛溢出异常；但 enum 的 `++ --` 按底层整数运算接受 checked 检查）。

### 1.2 Java 侧的对应面

Java 整数运算符（`+ - *` 等）**默认回绕、永不抛溢出异常**，与 C# unchecked 语义同构；JDK 仅以 `java.lang.Math` 的 `addExact/subtractExact/multiplyExact/incrementExact/decrementExact/negateExact/toIntExact`（int/long 域）提供显式检查，违例抛 `java.lang.ArithmeticException`。Java 没有 checked 上下文语法，也没有窄化转换的检查 API（`(byte)intValue` 恒回绕）。

因此 checked 语义**必须在 C#→C# 层显式化**为方法调用（Java 侧 1:1 映射 `Math.*Exact`/手写范围检查），unchecked 则保证剥离后即为 Java 裸算术语义。这正是 catalog §5.3「凡是能放 C#→C# 层做的 lowering，就绝不要放到 Java emit 层」的典型场景。

### 1.3 现状：命中即失败 + 静默错误输出并存

- **checked 表达式：静默丢语义**。[TypeOperationTransformer.cs:31](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/TypeOperationTransformer.cs#L31-L31) 对 `CheckedExpressionSyntax` 仅产出前缀注释：

  ```csharp
  CheckedExpressionSyntax x => (x.IsKind(SyntaxKind.CheckedExpression) ? "/* checked */ " : "") + Expr(c, x.Expression),
  ```

  `checked(a + b)` 产 Java `/* checked */ a + b`：**溢出检查消失、无诊断、产物还能「看起来正常」**——被 [catalog §4 风险 12](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L1110-L1110) 点名的「静默错误输出」站点。同一 switch 臂也吞掉 `unchecked(...)` 表达式（剥成内部表达式，单表达式场景侥幸等价，但未经任何语义判定）。
- **checked / unchecked 块：直接抛异常（命中即失败）**。[StatementTransformer.SwitchAndResource.cs:303-309](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Statement/StatementTransformer.SwitchAndResource.cs#L303-L309) 的 `TransformCheckedStatement`/`TransformUncheckedStatement` 一律 `throw new NotSupportedException("CS2J normalization required: ...")`（两条 throw 消息行即 [L305](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Statement/StatementTransformer.SwitchAndResource.cs#L305-L305)/[L309](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Statement/StatementTransformer.SwitchAndResource.cs#L309-L309)，与 catalog C15 行所引一致）。
- **旧丢弃判别残留**：[StatementTransformer.cs:309-314](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Statement/StatementTransformer.cs#L309-L314) `ShouldEmitStatementText` 仍以 `/* TODO: UncheckedStatement` 前缀做文本过滤，属同一站点的历史残迹。
- **同族占位**：`sizeof(用户类型/Int128 等)` 产 `"/* sizeof */"`（[TypeOperationTransformer.cs:155-164](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/TypeOperationTransformer.cs#L155-L164)），catalog C15「三次补全追加」要求并入同一「数值语义显式化」规则集；本设计对其归属见 §2 决策 16 与文末开放问题 5。
- **异常映射已就位**：[ExceptionApiRewriter.cs:93](file:///d:/code/cs2j/src/CSharpToJava.Core/Java/Rewriters/ExceptionApiRewriter.cs#L93-L93) 已把 `OverflowException` 映射为 `ArithmeticException`；compat 仓内**不存在** `OverflowException.java`（全仓 grep 仅有 `StackOverflowException` 等无关类），checked 算术支撑方法面在 Java 侧尚不存在（[catalog §4 风险 14](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L1112-L1112)：映射存在 ≠ 语义可用，方法面须先盘点补齐）。

### 1.4 上游产物中已存在大量 checked/unchecked 节点

本 pass 不是只面对用户手写代码：

- **enum pass 会发射 `unchecked(...)`**：窄枚举位取反归约显式包裹（[EnumSyntaxRewriter.cs:498-500](file:///d:/code/cs2j/src/CSharpToJava.Core/EnumPreprocessor/EnumSyntaxRewriter.cs#L498-L500) 与 [L524-L540](file:///d:/code/cs2j/src/CSharpToJava.Core/EnumPreprocessor/EnumSyntaxRewriter.cs#L524-L540)，`MaybeUnchecked` 在 [L206-L215](file:///d:/code/cs2j/src/CSharpToJava.Core/EnumPreprocessor/EnumSyntaxRewriter.cs#L206-L215)）。
- **unsigned pass 大量发射 `unchecked(...)` 包裹**（重解释转换与包裹算术，单测锁定的形态如 `Cs2jUnsigned.UnsignedMath.AddUInt32(unchecked((int)a), unchecked((int)b))`，见 UnsignedTypeRewriterUnitTests 多处）；支撑库本体 [UnsignedMath.cs](file:///d:/code/cs2j/src/Cs2jUnsignedMath/UnsignedMath.cs) 也以 `unchecked(a + b)` 实现回绕方法。同时 unsigned pass **刻意不改写 checked 上下文内的节点**（[UnsignedTypeSyntaxRewriter.cs:119-135](file:///d:/code/cs2j/src/CSharpToJava.Core/UnsignedTypeRewriter/UnsignedTypeSyntaxRewriter.cs#L119-L135)，且在 L599/L629/L738/L837/L889/L912 各改写分支以 `IsInCheckedContext` 跳过）——checked 区的 `uint/ulong` 以真身抵达本 pass。
- **LinqRewriter 会合成 checked 块**（[LinqRewriter.Rules.cs:72](file:///d:/code/cs2j/src/CSharpToJava.Core/LinqRewrite/LinqRewriter.Rules.cs#L72-L72)、[L233](file:///d:/code/cs2j/src/CSharpToJava.Core/LinqRewrite/LinqRewriter.Rules.cs#L233-L233)），该合成体当前会撞上 §1.3 的块级抛点，属转换器侧协调项（§11）。

结论：unchecked 剥离是整个数值边界链的**收口需求**，不是可选美化；residual 闸门（零 checked/unchecked 节点）在本 pass 之前不可能成立。

### 1.5 目标

在 C#→C# 层产出：

1. **checked 显式化**：checked 上下文/表达式内所有受检查的内置整数运算与整数转换，重写为支撑库静态方法调用（.NET 半区真身抛 `OverflowException`，Java 半区映射 `Math.*Exact`/范围检查抛 `ArithmeticException`）；
2. **unchecked 剥离**：unchecked 表达式剥壳、unchecked 块解包为普通块；其中编译期可判定的常量转换按 C# 编译器已算好的值**物化为类型正确的字面量**，保证剥离后中间树仍可编译；
3. **上下文保真**：嵌套 checked/unchecked、复合赋值、`++ --`、工程级 `/checked` 设置一律按 C# 语义精确处理；
4. **响亮失败**：无法在 v1 显式化的 checked 站点（特殊数值类型、浮点/decimal→整数转换、dynamic、复杂自增目标等）给 **Error 级 CS2J53xx 诊断**，禁止注释/占位/原样透传流入 Java 产物；
5. 产物仍是可编译 C#，由中间树 `dotnet build -c Release` + `dotnet test -c Release`（含溢出运行时行为断言）证明 .NET 半区等价（catalog §5.1 两半区方法论）。

明确不做（留边界）：复杂自增/自减目标归一（C17）、可空 enum/lifted 数值（C22）、decimal 运算符方法面（C34）、Half/Int128/UInt128/nint/IntPtr 算术（C41）、任何 C#→Java 转换代码改动。

### 1.6 硬不变式

1. 输出树（用户代码范围，支撑工程除外，§2 决策 12）中不存在任何 `CheckedExpressionSyntax`/`CheckedStatementSyntax`/`unchecked` 关键字节点——residual 闸门按语法树判定，不靠文本 grep；唯一例外是决策 4/NB-13 登记的「用户自定义运算符 checked 壳」，在 member-desugar（C14）同链启用时计数豁免、由 C14 收口（开放问题 6）；
2. checked 语义零丢失：每个受检查站点要么变为 helper 调用、要么有 Error 诊断使本次转换失败，不存在第三种去向；
3. unchecked 剥离保持 C# 语义：默认上下文本就回绕；常量转换必须经字面量物化（裸剥会导致中间树 CS0221 编译失败）；
4. 一切类型/运算分类来自 SemanticModel（`GetTypeInfo`/`GetSymbolInfo`/`GetOperation`/`GetConstantValue`），禁止按 token 文本替换；
5. 幂等：产物二次执行零 diff；干净项目字节级复制（含 BOM 保持）。

---

## 2. 关键设计决策表

| # | 决策点 | 结论 | 依据/备注 |
|---|---|---|---|
| 1 | checked 的目标形态 | **算术方法族**：checked 运算重写为支撑库静态调用（如 `Cs2jUnsigned.UnsignedMath.AddCheckedInt32(a, b)`），不做每运算符内联条件展开 | 内联展开每站点 3-5 条比较+分支，膨胀且易错；方法族与 unsigned 既有 `AddInt32/AddUInt32` 同构，调用点经现有 TypeMappings 路由到 compat 手写类（[TypeMappings.json:335-338](file:///d:/code/cs2j/config/TypeMappings.json#L335-L338)），转换器零改动。Java 侧直接对应 `Math.addExact` 系列 |
| 2 | 运算所在域与类型 | 只在 **Int32 域与 Int64 域**（含 UInt32/UInt64 无符号域）插入 helper；`sbyte/byte/short/ushort/char` 运算按 C# 提升规则本就在 int/long 上执行，**窄类型只在结果转换点**插转换 helper | 与 C# 编译器提升规则一致；checked 区内 unsigned pass 不改写，`uint/ulong` 以真身抵达，故 helper 族必须含 UInt32/UInt64 域（C# 签名取真 `uint/ulong`，Java 侧以 int/long 表示 + 无符号范围检查） |
| 3 | 受检查运算符集合 | 二元 `+ - *`、一元 `-`、`++ --`（前缀/后缀）、复合赋值的运算部分与隐式窄化转换、整数间显式转换 | `/ %` 不插入 helper（除零恒抛、不发生回绕溢出）；位运算/移位不溢出；`int.MinValue / -1` 由运行时自然抛 `OverflowException`，两侧一致，无需特判 |
| 4 | 不受影响的运算 | `decimal`/`double`/`float` 算术、enum↔底层类型转换：checked/unchecked 包装**剥壳但内部运算原样**。用户自定义运算符（`IOperation.OperatorMethod != null`）：内部调用**永不插 helper**；`unchecked(...)` 壳可安全剥除（C# 无 unchecked 运算符变体），但 **`checked(...)` 壳 v1 保留不剥、交 member-desugar（C14）剥除**——链序上 numeric-boundary 先于 member-desugar，提前剥壳会使下游在重建树上改绑普通运算符变体（op_Checked* 选择丢失，详见 §11 与开放问题 6） | decimal 始终检查，归 C34 的 compat `Decimal` 方法面（[catalog §4 风险 14](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L1112-L1112)）；浮点不抛；enum 转换规范保证不抛。enum 的 `++ --` 例外（决策 9）。C14 已随 member-desugar 落地（默认关），其 checked 壳处理见 [OperatorUseSiteRewriter.cs:236-253](file:///d:/code/cs2j/src/CSharpToJava.Core/MemberDesugar/Operators/OperatorUseSiteRewriter.cs#L236-L253) |
| 5 | unchecked 表达式/块剥离 | `unchecked(expr)` → 去壳后的 `expr`；`unchecked { … }` → 块内语句直接展开到所在块（块为单语句上下文时保留裸块花括号）；嵌套按上下文栈处理 | Java 裸算术即回绕语义。剥离后 enum/unsigned 上游产物中的同款包裹一并消失（§1.4），满足 residual 闸门 |
| 6 | 常量折叠/字面量物化 | 凡被剥除的 checked/unchecked 包裹或 checked 上下文运算，其内节点 `SemanticModel.GetConstantValue` 有值时，**以编译器算定的值物化为目标类型合法字面量**（如 `unchecked((int)4294967295u)` → `-1`；窄转换 `unchecked((byte)(…常量…))` → `(byte)N` 截断字面量） | checked 上下文中能编译的常量本就不溢出，物化后无需运行时 helper；unchecked 常量转换不物化则中间树 CS0221 编译失败。物化只发生在包裹/checked 站点内部，v1 不做全局常量传播 |
| 7 | 除零 | 不特殊处理：整数 `/0`、`%0` 两侧都抛（.NET `DivideByZeroException`、Java `ArithmeticException`），异常映射沿用现状 | 与 checked 上下文无关；helper 族不含除法 |
| 8 | 与 unsigned pass 产物的衔接 | ① checked **区外**：uint 运算已是 `UnsignedMath.AddUInt32(...)` 回绕调用，本 pass **不改调用本身**，只剥其嵌套的 `unchecked(...)` 壳；② checked **区内**：unsigned 未改写，节点是真 `uint/ulong` 算术，本 pass 插入 `AddCheckedUInt32/AddCheckedUInt64`；③ 支撑工程 `Cs2jUnsignedMath/` 整目录 clean copy 跳过（同 unsigned 适配器自身的 own-release 跳过纪律） | unsigned 的 `IsInCheckedContext` 跳过契约（§1.4）使两 pass 职责无重叠；回绕方法与检查方法并列于同一支撑类，调用点不会出现同一运算两次包装 |
| 9 | `++ --` 与复合赋值 | 简单目标（局部/字段/参数/`this.X` 之外的裸标识）：前缀直接 `x = AddCheckedXxx(x, 1)`；后缀在可插入语句上下文做旧值临时提升（`var __cs2j_old = x; x = AddCheckedXxx(x, 1); …使用 __cs2j_old`，临时名沿用全仓 `__cs2j` 前缀）；复合赋值 `x op= y` = 运算 helper + 窄化转换 helper。enum 简单目标的 `++ --` 按底层整数域处理（`e = (E)AddCheckedInt32((int)e, 1)`，回转 enum 的转换不检查） | 属性/索引器/事件/decimal/无符号窄类型等复杂目标的读-改-写归 **C17**；在 C17 落地前于 checked 上下文遇到 → CS2J5324 Error（不静默生成重复求值代码）。不可插入临时变量的表达式上下文中的后缀 → CS2J5324 |
| 10 | checked 转换的目标形态 | 整数窄化/同宽跨符号转换走**转换方法族**，命名沿用支撑库既有 `To<Target><SourceDomain>` 惯例加 `Checked`：`ToByteCheckedInt32(int)`、`ToInt32CheckedInt64(long)`、`ToUInt32CheckedInt32(int)`（负值抛）、`ToInt32CheckedUInt32(uint)`（超 `int.MaxValue` 抛）等；C# 实现即 `checked((byte)v)`，Java 实现为范围比较后强转 | JDK 无窄化 exact API，必须手写；源域编入方法名避免 Java 擦除后签名冲突（既有 `ToByteInt32/ToByteInt64` 已立此惯例，[UnsignedMath.cs:101-114](file:///d:/code/cs2j/src/Cs2jUnsignedMath/UnsignedMath.cs#L101-L114)）。enum 转换不插入（决策 4） |
| 11 | 不支持的 checked 转换 | 浮点/decimal→整数的 checked 转换、`Half/Int128/UInt128/nint/nuint/IntPtr` 上的 checked 运算或转换、nullable 数值 lifted checked：v1 一律 **CS2J53xx Error**，码位分桶计数，留 M 后续/族内规则收编 | C# 对 `(int)double.NaN` checked 抛异常而 Java `(int)NaN == 0`，无语义廉价映射；nint 位宽依赖平台归 C41。Error 而非 Warning-skip：跳过 checked 等于静默丢语义，违反不变式 2，且不提供 force 逃生口 |
| 12 | 支撑库归属 | **复用并扩展 `Cs2jUnsignedMath` 单文件支撑工程**（`Cs2jUnsigned.UnsignedMath` 增 checked 方法族 + compat `UnsignedMath.java` 增对应方法），不新建支撑工程 | 注入/释放/碰撞检测/ProjectReference 改写/Maven 模块过滤/TypeMappings/compat 对应物六条链路全部现成；descriptor 直接声明 `supportLibrary: PassSupportLibrary.Cs2jUnsignedMath`，[CliPreprocessChain.cs:108-111](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/CliPreprocessChain.cs#L108-L111) 自动把库计入执行集、[Program.cs:257-267](file:///d:/code/cs2j/src/CSharpToJava.CLI/Program.cs#L257-L267) 自动过滤。代价是类名「Unsigned」承载 signed checked 方法，文档注释说明该类是「数值边界支撑面」。是否另立新库 `Cs2jCheckedMath` 列开放问题 1 |
| 13 | 注入条件 | 适配器在「本 pass 实际产生 ≥1 个 checked helper 调用」时才释放库并给宿主项目加 ProjectReference（复用 unsigned 的 `ComputeAllExtensionHostProjectDirectories`/`EnsureExtensionProjectReferences` 机制）；仅剥 unchecked/常量物化的项目不引库 | 与 unsigned「有改写才注入」同纪律；与 unsigned 同时执行时释放幂等（同内容覆盖，碰撞检测识别自身释放副本，参 [UnsignedPassAdapter.cs:169-197](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/Adapters/UnsignedPassAdapter.cs#L169-L197)） |
| 14 | OverflowException 兼容 | .NET 半区 helper 真身抛 `System.OverflowException`（支撑库内 `checked` 实现，中间树测试可精确捕获）；Java 半区 compat 方法以 `Math.*Exact`/显式范围检查抛 `java.lang.ArithmeticException`，沿用 [ExceptionApiRewriter.cs:93](file:///d:/code/cs2j/src/CSharpToJava.Core/Java/Rewriters/ExceptionApiRewriter.cs#L93-L93) 既有类型映射；compat 不新增 `OverflowException.java` | 已知近似：`catch (OverflowException)` 在 Java 侧等价于捕 `ArithmeticException`，会连带捕获除零（两者在 Java 同型）。是否需要 compat 细分异常子类列开放问题 4，v1 文档化不保证区分捕获 |
| 15 | 工程级 `/checked` 设置 | Analyzer 逐项目解析 csproj `CheckForOverflowUnderflow`，按文件所属项目为上下文栈播种默认 checked/unchecked；机制与普通上下文完全相同（默认值翻转） | runner 的整树单一 compilation 不携带逐项目编译选项，故必须自行解析（仿 unsigned 解析 TFM 的 csproj 读法）。M1 可先实现检测+CS2J5326 Error 占位，M2 完成全量支持；sdk 命中稀少但静默当作 unchecked 是错误语义，不允许 |
| 16 | `sizeof` 占位归属 | v1 **不实现** sizeof 收编；`/* sizeof */` 站点（[TypeOperationTransformer.cs:163](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/TypeOperationTransformer.cs#L163-L163)）维持转换器现状，登记为族内后续小规则（常量折叠基建就位后仅差用户类型报错），码位在 §4.5 预留 | 本任务范围锁定 checked/unchecked；catalog 三次补全的并入要求以「同规则集后续增量」满足，stub CatalogItems 翻转时只宣称 C15（§3.4）。是否同 PR 收编列开放问题 5 |
| 17 | 执行形态 | **逐文件语义 pass + 标准 spine**（同 enum/unsigned），不走 JK/RR 的 plan/整树迭代回退 | 改写是局部、确定性的表达式替换，类型判定全部来自语义模型，无跨文件改名/签名变化；正确性由「整树重编译零新增错误 + verify build/test 双开」兜底。无 plan 因而无 CS2J5300/5301 回退码 |
| 18 | 表达式树/dynamic | 语义模型判定 checked 站点位于 `Expression<Lambda>` 树内，或绑定为 dynamic → CS2J5323/CS2J5322 Error | 表达式树内 checked 节点改写会改变树形态；dynamic 无法静态定域。沿用 [catalog §4 风险 8](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L1106-L1106) 共享判定（v1 在 NumericBoundarySyntaxFacts 自持一份，登记技术债），但 checked 不提供 force（对比 JK 的 force 语义）：丢溢出检查不可由配置静默 |

---

## 3. 注册表与链位契约

### 3.1 现状（stub）

[PassStubCatalog.cs:150-159](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassStubCatalog.cs#L150-L159) 已登记唯一条目：

| 字段 | 值 |
|---|---|
| Id | `numeric-boundary`（常量 `PassStubCatalog.Ids.NumericBoundary`，[L33](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassStubCatalog.cs#L33-L33)） |
| DisplayName | `Numeric boundary explicitization` |
| IntermediateDirectoryName | `.cs2j-numeric-boundary-src` |
| Stage | `PassStage.Expression` |
| Status | `Stub`（显式启用 → CS2J9001，[PreprocessDiagnosticCodes.cs:12](file:///d:/code/cs2j/src/CSharpToJava.Core/Preprocessing/PreprocessDiagnosticCodes.cs#L12-L12)） |
| DefaultEnabled | `false` |
| RunAfterIds | `{ "unsigned" }`（`PassIds.Unsigned`） |
| CatalogItems | `"C15, C17, C22, C34, C41"` |
| Description | `Make narrow/special numeric operations explicit (decimal, Half, Int128, nint and enum arithmetic).` |

全仓 grep 确认 `NumericBoundary`/`numeric-boundary` 标识符当前仅出现在该文件内（L33 常量、L151/L153 条目，共 3 处，无其它 RunAfter 引用）。**按 IteratorLowering 先例翻转时保留 `Ids.NumericBoundary` 常量**（见 §3.4），翻转后该常量被 PassCatalog/PassChainFactory/测试引用，属预期接线而非改名负担。

### 3.2 链位与顺序锁

- **RunAfter 锚边保持 `{ unsigned }` 不变**。翻转后描述符进入 [PassCatalog.cs](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassCatalog.cs) Implemented 表，**物理位置紧随 unsigned 条目插入**（`ref-out-in → unsigned → numeric-boundary → generic → …`）。语义依据：
  - 必须晚于 unsigned（消费其回绕产物、跳过其支撑工程、接管 checked 区真身 uint/ulong）；
  - 必须晚于 enum（消费其窄枚举 `unchecked` 包裹；enum 在链中位于 unsigned 之前，锚边传递满足）；
  - 对 generic/make-readonly 等后续 pass 无形态依赖（泛型类型参数上不成立内置整数算术；struct 变异迁移不产生 checked 节点）。
- Implemented 子序列预期（对齐 2026-09-29 主干已有的 generic-deepen/iterator-lowering/member-desugar）：`…, ref-out-in, unsigned, numeric-boundary, generic, generic-deepen, record-readonly, array-method, make-readonly, iterator-lowering, eliminate-goto, member-desugar, java-keywords`。**实现时以实测拓扑序为准**更新 [PassChainOrderTests.cs](file:///d:/code/cs2j/tests/CSharpToJava.Tests/Preprocessing/PassChainOrderTests.cs) 锁序断言（Kahn + 声明序决胜，参 [PassRegistry.cs](file:///d:/code/cs2j/src/CSharpToJava.Core/Preprocessing/PassRegistry.cs)）。
- 默认关期间该 pass 不进执行计划：无目录、无耗时、无库注入、链行为零变化（[PassStubCatalogTests.cs:44-69](file:///d:/code/cs2j/tests/CSharpToJava.Tests/Preprocessing/PassStubCatalogTests.cs#L44-L69) 的「默认关/默认计划不含 stub/目录唯一」模式）。

### 3.3 翻转后的描述符字段

```
Id                       = "numeric-boundary"        # 复用 PassStubCatalog.Ids.NumericBoundary（保留常量，IteratorLowering 先例）
Stage                    = PassStage.Expression
Status                   = PassStatus.Implemented
DefaultEnabled           = false                     # 锚点翻转即 Implemented，但默认关；§8 门槛达成后单行翻 true
RunAfterIds              = { PassIds.Unsigned }
NeedsSemanticModel       = true
SupportLibrary           = PassSupportLibrary.Cs2jUnsignedMath
VerifyBuildByDefault     = true
VerifyTestsByDefault     = true
IntermediateDirectoryName= ".cs2j-numeric-boundary-src"
CatalogItems             = "C15"                     # 收窄：族锚点只宣称本规则；后续按 §3.4 追加
Description              = "Explicitize checked/unchecked integer arithmetic (overflow checks and stripping)."
```

verify 双默认开取最严档（.NET 半区守门，同 local-function-lift/enum/generic 等语义 pass）；支撑库字段复用 unsigned 枚举值，不新增 `PassSupportLibrary` 枚举成员，故 Program.cs 的库过滤、Maven 模块排除、指纹豁免三处零改动。**id 接线按 IteratorLowering 先例**：常量留在 `PassStubCatalog.Ids`（[PassStubCatalog.cs:32](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassStubCatalog.cs#L32-L32) 的 IteratorLowering 翻转后即如此），Implemented 描述符直接引用它（[PassCatalog.cs:209-219](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassCatalog.cs#L209-L219)），不新增 `PassIds` 成员。仓库另有 member-desugar 模板（新增 `PassIds.MemberDesugar` + 删除 stub 常量 + Program.cs 独立开关，26ea367c），仅在开放问题 7 拍板要求专用开关时启用。

### 3.4 翻转文件清单

> 本清单以 2026-09-29 主干为准，默认逐字照搬 IteratorLowering 翻转（b784b47f）的 id 处理。

1. [PassCatalog.cs](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassCatalog.cs)：**不增 `PassIds` 成员**；`Implemented` 表在 unsigned 条目（[L146-L155](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassCatalog.cs#L146-L155)）之后插入 §3.3 描述符，Id 参数直接写 `PassStubCatalog.Ids.NumericBoundary`（先例 [L210](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassCatalog.cs#L210-L210)），含 `supportLibrary: PassSupportLibrary.Cs2jUnsignedMath`、双 verify 开。
2. [PassStubCatalog.cs](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassStubCatalog.cs)：仅删除 numeric-boundary stub 条目（[L150-L159](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassStubCatalog.cs#L150-L159)）；**`Ids.NumericBoundary` 常量（[L33](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassStubCatalog.cs#L33-L33)）保留**——翻转后由第 1/3/7 项引用（IteratorLowering 常量在其 stub 条目删除后保留至今，即先例）。
3. [PassChainFactory.cs](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassChainFactory.cs)：`CreateAdapter` 增 `PassStubCatalog.Ids.NumericBoundary => new NumericBoundaryPassAdapter(new NumericBoundaryOptions(), config.Verbose, config.FilterUnsupportedProjects, config.CompatNamespacePrefixes, config.VerifyBuild, config.VerifyTests, config.Strict)`（先例 [L226-L234](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassChainFactory.cs#L226-L234)；options 无 overrides 懒加载需求，直接构造，不增 configuration factory）。
4. [PassChainFactory.BuildLegacyToggles](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassChainFactory.cs#L64-L85)：仿 iterator-lowering 增**硬编码** `[PassStubCatalog.Ids.NumericBoundary] = false`（先例 [L81](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassChainFactory.cs#L81-L81)），即不读 ConvertProjectOptions、convert-project 默认不启用，仅保证 toggle 键集与 LockedChain 一致。注意：[PassChainOrderTests.cs:172-185](file:///d:/code/cs2j/tests/CSharpToJava.Tests/Preprocessing/PassChainOrderTests.cs#L172-L185) **并不要求每个 Implemented pass 都有 legacy toggle**——generic-deepen/pattern-desugar 经 L182 排除列表豁免；本清单取硬编码 false 是为了与 LockedChain 插位零冲突且不碰 Program.cs。
5. Program.cs `ConvertProjectOptions`：**本清单默认零改动**——入口由描述符默认关 + 通用 `--enable-pass numeric-boundary`/`--disable-pass` 与 `preprocess --pass numeric-boundary` 覆盖（与 iterator-lowering/generic-deepen 同）。若评审拍板要 convert-project 专用 `--numeric-boundary` 开关（开放问题 7），则整体改走 member-desugar 模板：增 `PassIds.NumericBoundary`、删 `Ids.NumericBoundary`、第 4 项改为 `opts.NumericBoundary`、仿 [Program.cs:4844-4846](file:///d:/code/cs2j/src/CSharpToJava.CLI/Program.cs#L4844-L4846) 增独立 get/set 属性（不使用 unsigned 那种 `!NoXxx` 反写形态）。preserved 目录与指纹令牌两种模板下都由描述符全集驱动，无需手写接线；`--list-passes` 自动显示。
6. 新增 [Adapters/NumericBoundaryPassAdapter.cs](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/Adapters)（§5）。
7. 链序/注册表测试更新：
   - [PassChainOrderTests.cs:18-40](file:///d:/code/cs2j/tests/CSharpToJava.Tests/Preprocessing/PassChainOrderTests.cs#L18-L40) `LockedChain` 在 `Unsigned`（L30）与 `Generic`（L31）之间插入 `PassStubCatalog.Ids.NumericBoundary`；`EnabledByDefault`（[L42-L57](file:///d:/code/cs2j/tests/CSharpToJava.Tests/Preprocessing/PassChainOrderTests.cs#L42-L57)）**不变**（默认关）；`ChainFactory_creates_an_adapter_for_every_implemented_pass`（L153-L170）自动覆盖；`BuildLegacyToggles_covers_every_registered_pass`（L172-L185）因第 4 项硬编码 toggle 自动满足，排除列表不动。
   - [PassStubCatalogTests.cs:24](file:///d:/code/cs2j/tests/CSharpToJava.Tests/Preprocessing/PassStubCatalogTests.cs#L24-L24) stub 总数断言：**现状已漂移**——PassStubCatalog.cs 实有 21 条 stub（23 经 iterator-lowering、generic-deepen（同时新增 closed-metadata/variance-audit）、pattern-desugar、member-desugar 历次翻转后为 21），而断言仍写 `22`（26ea367c 从 22→21 时漏改，提交信息「23 -> 22」亦误）；本任务删除 numeric-boundary 后为 **20 条**，断言直接改为 `20`（一次修正，勿保留 22）。CS2J9001 用例只启用 closure-lift（[L77](file:///d:/code/cs2j/tests/CSharpToJava.Tests/Preprocessing/PassStubCatalogTests.cs#L77-L77)/[L105](file:///d:/code/cs2j/tests/CSharpToJava.Tests/Preprocessing/PassStubCatalogTests.cs#L105-L105)），与 iterator-lowering 无关，不受影响。
   - [ProjectPreprocessorsConsistentTests.cs](file:///d:/code/cs2j/tests/CSharpToJava.Tests/ProjectPreprocessorsConsistentTests.cs) 追加断言：`PassCatalog.ImplementedById(PassStubCatalog.Ids.NumericBoundary).IntermediateDirectoryName == ".cs2j-numeric-boundary-src"`、适配器类型存在（仿最近翻转的 member-desugar 段 [L134-L138](file:///d:/code/cs2j/tests/CSharpToJava.Tests/ProjectPreprocessorsConsistentTests.cs#L134-L138)；java-keywords 门面类段在 L115-L121，不适用本 pass）。
8. §8 门槛达成后单行翻 `defaultEnabled: true`（指纹令牌 `pass:numeric-boundary=false→true` 一次性失效，可接受，[catalog §4 风险 11](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L1109-L1109)）。
9. **CatalogItems 后续只追加不改写**：锚点翻转先收窄为 `"C15"`（§3.3）；C17 合入时 `"C15" → "C15, C17"`，C22/C34/C41 依次；每次仅一行字符串冲突，属预期协调点（§11）。

---

## 4. Core 组件设计

### 4.1 目录与文件所有权总览

```
src\CSharpToJava.Core\NumericBoundary\
  NumericBoundaryPreprocessor.cs   # 对外入口：Rewrite(sourceCode, semanticModel, options) → NumericBoundaryFileResult
  NumericBoundaryModels.cs         # NumericBoundaryOptions/Statistics/Diagnostic/Severity/FileResult
  NumericBoundaryAnalyzer.cs       # 上下文栈（checked/unchecked + 工程级 /checked 种子）、
                                   #   运算分类（域/运算符/转换种类）、常量判定、表达式树/dynamic 判定、
                                   #   按文件归属项目的 CheckForOverflowUnderflow 解析
  CheckedExprRewriter.cs           # CSharpSyntaxRewriter：unchecked 剥壳/块解包、checked 运算→helper、
                                   #   checked 转换→ToXxxCheckedYyy、++--/复合赋值、常量字面量物化、旧值临时提升
  NumericBoundarySyntaxFacts.cs    # 整数域谓词、helper 名选择（AddCheckedInt32/…）、类型正确字面量生成、
                                   #   __cs2j 临时名、支撑工程路径跳过谓词、RewriterAnnotation
  Rules\                           # 族内规则分目录（物理隔离，消除后续规则文件冲突，仿 C11 §4.1）
    CheckedArithmetic\             # 【C15 独占】本任务的规则实现可整体落根目录或本子目录（M1 定）
    IncrementTargets\              # 【C17 预留，本任务不创建文件】
    NullableNumeric\               # 【C22 预留】
    DecimalOps\                    # 【C34 预留】
    SpecialNumeric\                # 【C41 预留：Half/Int128/UInt128/nint/IntPtr；sizeof 占位后续规则亦在此】
```

规则目录所有权是硬约束：C17/C22/C34/C41 落地时只增各自子目录与自有测试，不改 `NumericBoundary/` 根目录文件与适配器（共享组件需要放宽时先扩根层并补 C15 回归，同 C11 §4.5 纪律）。

### 4.2 对外契约（与 UnsignedTypeRewriter 同形，逐文件语义）

- `NumericBoundaryPreprocessor(NumericBoundaryOptions? options = null)`
- `NumericBoundaryFileResult Rewrite(string sourceCode, SemanticModel semanticModel, NumericBoundaryOptions options)` → `{ OutputCode, Changed, Statistics, Diagnostics }`
- 内部为单次语法树遍历：`NumericBoundaryAnalyzer` 随重写维护上下文栈（进入 `CheckedExpression/CheckedStatement` 压栈，块结束出栈；`Unchecked*` 同理），不缓存跨编译状态——每文件 SemanticModel 独立，可直接被适配器逐文件调用。
- 输出前做格式化与重解析校验（复用 JK/RR 的 `FormatAndValidate` 三候选：改动区间格式化 → 整文件格式化 → 原文；v1 可直接搬运 internal 工具或在本目录自持，M1 定）。

### 4.3 重写规则全集（NB-x）

| 规则 | 输入 | 输出 | 要点/边界 |
|---|---|---|---|
| NB-1 unchecked 表达式剥壳 | `unchecked(e)` | `e`（内部先按 unchecked 栈重写） | 内部常量转换按 NB-6 物化；enum/unsigned 上游产物同款处理 |
| NB-2 unchecked 块解包 | `unchecked { s1; s2; }` | 语句序列展开到外层块；单语句上下文保留 `{ … }` 裸块 | 块内声明的局部变量作用域必须保持（裸块花括号兜底） |
| NB-3 checked 二元算术 | checked 上下文内 `a + b`/`-`/`*`（内置整数、结果为 Int32/Int64/UInt32/UInt64 域） | `UnsignedMath.AddCheckedInt32(a, b)` 等 | 运算分类以 `IBinaryOperation.OperatorMethod == null` + 结果特殊类型判定；用户自定义运算符不动；窄类型运算经提升后自然落 Int32 域 |
| NB-4 checked 一元负 | `-x`（int/long 域） | `UnsignedMath.NegateCheckedInt32(x)` | `int.MinValue` 溢出抛；uint 一元负 C# 结果为 long（边角，M2 支持或 CS2J5320 计数） |
| NB-5 checked 复合赋值 | `x += y`（x 为整数简单目标） | `x = UnsignedMath.AddCheckedInt32(x, y)`；x 为窄类型时追加 `ToByteCheckedInt32(...)` 转换 | 严格复刻 `x = (T)(x op y)` 的「运算检查 + 转换检查」两段；`/=` `%=` 原样（仅除零语义） |
| NB-6 checked/unchecked 常量物化 | 包裹或 checked 上下文内、`GetConstantValue.HasValue` 的运算/转换节点 | 目标类型合法字面量（必要时带一次同类型 cast 保形） | unchecked 窄转换物化截断值；checked 常量能编译即不溢出，物化无需 helper；字面量后缀（L/UL…）与负号按类型生成 |
| NB-7 checked 显式整数转换 | checked 上下文内 `(T)v`（T/v 为整数域、非常量、非同型恒等、非 enum 目标） | `UnsignedMath.To<Target>Checked<SourceDomain>(v)`（决策 10） | 同宽跨符号（int↔uint、long↔ulong）含范围检查；宽化恒等不插调用；enum 目标/源的转换不检查（保持裸 cast） |
| NB-8 checked `++ --` 简单目标 | 决策 9 | `AddCheckedXxx(x, 1)` + 后缀旧值临时提升 | enum 简单目标经底层域；复杂目标 CS2J5324；不可提升表达式上下文 CS2J5324 |
| NB-9 checked 表达式剥壳 | `checked(e)` | 内部在 checked 栈下整体重写，外壳消失 | 与块共用上下文栈；嵌套 checked/unchecked 自然处理；外壳仅包裹用户自定义运算符调用时不消失（NB-11/决策 4） |
| NB-10 checked 块 | `checked { … }` | 块语句保留（裸块），体内按 checked 栈重写 | 不做块级 helper；检查全部落在各运算站点 |
| NB-11 非整数 checked 包裹 | checked/unchecked 内为 decimal/float/double 算术 | 剥壳，运算原样 | decimal 归 C34；不插入 helper、不报错。**用户自定义运算符（`OperatorMethod != null`）另按决策 4：unchecked 壳剥除、checked 壳保留交 C14（member-desugar）剥除，内部调用永不插 helper** |
| NB-12 不支持站点 | 决策 11/18 所列 | **Error 诊断，节点原样保留**（本次转换失败） | 分码 CS2J5320-5324；不产生「半改写」文件 |
| NB-13 residual 对账 | 输出树重扫 | 用户代码范围零 checked/unchecked 节点，否则 CS2J5327（strict 即 Error） | 支撑工程目录除外（决策 8/§5.2）；**另豁免「仅包裹绑定到用户自定义运算符调用」的 checked 壳**——该壳归 member-desugar（C14）收口（决策 4/§11），仅当 member-desugar 同批启用时豁免成立，且须计数（`CheckedUserOpShellsDeferred`）；member-desugar 不启用的组合处置见开放问题 6 |

### 4.4 支撑库方法面（本任务需新增的清单）

C# 侧（[src/Cs2jUnsignedMath/UnsignedMath.cs](file:///d:/code/cs2j/src/Cs2jUnsignedMath/UnsignedMath.cs)，实现体用 `checked(...)`，保持该文件既有的「可转换 C# 子集」纪律——它同时是嵌入资源真源与 Java 半区转换输入过滤对象）：

```
# 算术（Int32/Int64 域）
int  AddCheckedInt32(int a, int b)      => checked(a + b)
int  SubtractCheckedInt32(int a, int b) => checked(a - b)
int  MultiplyCheckedInt32(int a, int b) => checked(a * b)
int  NegateCheckedInt32(int a)          => checked(-a)
long AddCheckedInt64/SubtractCheckedInt64/MultiplyCheckedInt64/NegateCheckedInt64
# 算术（无符号域，checked 区内真身抵达）
uint   AddCheckedUInt32(uint a, uint b) …（+ - *）
ulong  AddCheckedUInt64(ulong a, ulong b) …
# 转换（命名沿用 To<Target>Checked<SourceDomain>）
sbyte/byte/short/ushort/char  ToSByteCheckedInt32(int) …（int 域窄化）
uint   ToUInt32CheckedInt32(int)   # 负值抛
int    ToInt32CheckedUInt32(uint)  # 超 int.MaxValue 抛
…Int64/UInt64 域对称全套
```

Java 侧（[java/csharptojava-compat/.../UnsignedMath.java](file:///d:/code/cs2j/java/csharptojava-compat/src/main/java/io/github/ningpp/compat/UnsignedMath.java)，camelCase 同名）：Int32/Int64 算术委托 `Math.addExact/subtractExact/multiplyExact/incrementExact?/negateExact`；UInt32/UInt64 与转换族手写范围检查后回绕强转，违例 `throw new ArithmeticException("overflow")`。**方法面必须先于默认开启补齐并附 compat JUnit**（[catalog §4 风险 14](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L1112-L1112)：禁止生成不存在的方法名）。TypeMappings 无需新增条目（类型映射 `Cs2jUnsigned.UnsignedMath → io.github.ningpp.compat.UnsignedMath` 已在，方法名由转换器 camelCase 派生）；M5 须抽样核实派生结果与 compat 方法名逐字一致（§8-3）。

### 4.5 诊断码段与族内预留

新增 **CS2J53xx** 段（已核实 CS2J51xx 属 member-desugar、CS2J52xx 在 C11 文档中被非正式预留给 C39，本段取 53xx 不冲突；码位只追加不改号）：

| 子段 | 归属 |
|---|---|
| CS2J5300-5309 | 族共享（restore/build/test/单文件闸门） |
| CS2J5310-5329 | **C15 checked/unchecked** |
| CS2J5330-5349 | C17 预留 |
| CS2J5350-5359 | C22 预留 |
| CS2J5360-5379 | C34 预留 |
| CS2J5380-5399 | C41 预留（含 sizeof 后续规则） |

### 4.6 Options / Statistics

- `NumericBoundaryOptions`：`Strict = true`（Warning→Error 升级，同 enum）、`SkipSupportLibraryProjects = true`；v1 无 overrides JSON（Error 码不接受 skip/force；若 sdk triage 证明需要白名单，M5 再加）。
- `NumericBoundaryStatistics`：通用五字段（`FilesScanned/FilesTransformed/FilesSkippedClean` 等 IPassStatistics 契约字段，[preprocessor-foundation_plan.md:55](file:///d:/code/cs2j/.trae/documents/preprocessor-foundation_plan.md#L55-L55)）+ `CheckedBinarySites`、`CheckedUnarySites`、`CheckedConversionSites`、`CheckedIncrementSites`、`CompoundAssignments`、`UncheckedExpressionsStripped`、`UncheckedBlocksUnwrapped`、`ConstantsMaterialized`、`HelpersByDomain: Dictionary<string,int>`、`ErrorsByReason: Dictionary<code,int>`、`CheckedUserOpShellsDeferred`（决策 4：保留交 member-desugar 收口的用户运算符 checked 壳数，NB-13 豁免对账凭证；member-desugar 未启用时的处置未定，开放问题 6）。

---

## 5. CLI 适配器与集成

### 5.1 NumericBoundaryPassAdapter

新增 `src/CSharpToJava.CLI/Preprocessing/Adapters/NumericBoundaryPassAdapter.cs`（预计 180-260 行），**走标准 spine**（不重写 `ExecuteAsync`），形态参照 [UnsignedPassAdapter.cs](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/Adapters/UnsignedPassAdapter.cs)：

- `Descriptor => PassCatalog.ImplementedById(PassStubCatalog.Ids.NumericBoundary)`（IteratorLowering 先例，常量留 PassStubCatalog.Ids，§3.4）；`CreateParseOptions` 用 `new()`（默认；如遇 C# 最新语法节点解析需要再升 `LanguageVersion.Latest`，M1 实测定）；`FilterUnsupportedProjects`/`CompatNamespacePrefixes` 沿用全局配置；verify 取 descriptor 双开（不重写 `VerifyBuild/VerifyTests`）。
- `OnTreePreparedAsync`：复用 unsigned 的 own-release 目录扫描，收集树中**所有 cs2j 已释放支撑工程**目录（`Cs2jUnsignedMath`，以及链式共存时的 `Cs2jArray`/`Cs2jRefOutIn`）用于 clean copy 跳过；同时逐 csproj 解析 `CheckForOverflowUnderflow` 构建「项目目录 → 默认上下文」表传给 analyzer（决策 15）。
- `RewriteFileAsync`：支撑工程路径下文件返回 null（字节复制，同 [UnsignedPassAdapter.cs:56-64](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/Adapters/UnsignedPassAdapter.cs#L56-L64)）；其余调 `_rewriter.Rewrite(...)`，聚合计数与诊断（诊断消息前缀相对路径，同 unsigned L69-L72），changed 时返回带 `PreserveBom = true` 的 `PassFileRewrite`；记录被改写文件路径供注入宿主计算。
- `AfterFilesAsync`：当 checked helper 调用计数 > 0 时，复用 `UnsignedMathSupportFiles.ReleaseAsync`（[UnsignedMathSupportFiles.cs:21-33](file:///d:/code/cs2j/src/CSharpToJava.CLI/UnsignedMathSupportFiles.cs#L21-L33)）释放到 `tree.IntermediateRoot/Cs2jUnsignedMath/`，并复用 unsigned 的宿主项目目录计算 + csproj ProjectReference 改写（建议把 UnsignedPassAdapter 中对应 private static 提为共享 internal 助手，本适配器与 unsigned 共用，不复制逻辑；提取时 unsigned 行为零变化由其现有测试守门）。
- `HasErrors`：存在 Error 级诊断即真（含 strict 升级）。
- 摘要行：`numeric-boundary: N file(s), B binary/U unary/C conversion/I increment site(s) explicitized, X unchecked expr(s)/Y block(s) stripped, K constant(s) materialized, E error(s)`。
- restore 失败映射 CS2J5302；`OnBuildFailed`→CS2J5303、`OnTestsFailed`→CS2J5304（verify 默认开，spine 自动调用，参 [ProjectPassRunner.cs:239-249](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/ProjectPassRunner.cs#L239-L249)）。

### 5.2 verify 默认、支撑库声明、指纹

- verify：descriptor 双默认开（最严档）；`DeleteBuildOutputsAfterRestore` 取 true（同 enum/make-readonly，verify 后清 bin）。
- 支撑库：`SupportLibrary = Cs2jUnsignedMath` 使「已执行库集合」自动包含本 pass（[CliPreprocessChain.cs:108-111](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/CliPreprocessChain.cs#L108-L111)），Java 转换规划过滤与指纹豁免自动生效；库内 `checked`/`unchecked` 关键字属工具代码，**不计入 NB-13 residual 闸门**（闸门范围 = 非支撑工程用户源；枚举谓词复用适配器 §5.1 的跳过目录集；决策 4 的用户运算符 checked 壳豁免另计）。
- 指纹：随描述符全集令牌自动纳管；`.cs2j-numeric-boundary-src` 由实际执行集合自动 preserve；禁用时零痕迹。
- 入口面（默认走 IteratorLowering 模板，§3.4-4/5）：convert-project **不设专用开关**，经通用 `--enable-pass numeric-boundary`/`--disable-pass` 启用（BuildLegacyToggles 硬编码 false 占位）；独立运行走通用 `preprocess --pass numeric-boundary -s … -d …`，不新建独立 verb。若评审要求 convert-project `--numeric-boundary`（默认 false）专用开关，则按开放问题 7 整体改走 member-desugar 模板（同 C11 方向）。
- 单文件模式：v1 不提供 `-i/-o`。通用 verb 单文件编译下，仅含 unchecked 剥离/常量物化的文件可处理；出现 checked helper 站点时受 CS2J5309 闸门拒绝（支撑库不在单文件编译内），提示改走项目模式（同 JK-090 纪律）。

---

## 6. 诊断策略（消灭 `/* checked */`，无法表达即响亮失败）

MSBuild 格式：`文件(行,列): warning/error CS2J53xx: [规则/类型] 描述`。

| 码 | 级别 | 含义 |
|---|---|---|
| CS2J5302 | Error | 中间树 `dotnet restore` 失败 |
| CS2J5303 | Error | 中间树 `dotnet build` 失败（含改写引入的编译错误——标准 spine 下以 verify build 兜底，不做逐站点回退） |
| CS2J5304 | Error | 中间树 `dotnet test` 失败 |
| CS2J5309 | Warning | 单文件模式：checked helper 站点非自包含（需支撑库），拒绝处理 |
| CS2J5310 | Info | checked 站点显式化（聚合计数：二元/一元/转换/自增分桶），仅 verbose |
| CS2J5311 | Info | unchecked 表达式剥离 / 块解包计数，仅 verbose |
| CS2J5312 | Info | 常量字面量物化计数，仅 verbose |
| CS2J5320 | Error | checked 运算/转换落在不支持类型（Half/Int128/UInt128/nint/nuint/IntPtr 及未识别数值类型）——归 C41，v1 响亮失败 |
| CS2J5321 | Error | checked 浮点/decimal→整数转换（NaN/范围语义 Java 不可 1:1，决策 11） |
| CS2J5322 | Error | dynamic 绑定的 checked 站点，无法静态定域（force 不解除） |
| CS2J5323 | Error | 表达式树 lambda 内 checked 站点（决策 18；force 不解除） |
| CS2J5324 | Error | checked 上下文内复杂 `++ --`/后缀旧值目标（属性/索引器/事件/decimal/不可提升表达式上下文）——归 C17，落地前显式失败 |
| CS2J5325 | Error | nullable 数值 lifted checked 运算——归 C22，v1 显式失败 |
| CS2J5326 | Error | 工程级 `CheckForOverflowUnderflow=true`（M1 占位码；M2 全量支持后停用该报错，见决策 15） |
| CS2J5327 | Warning（strict 即 Error） | residual 闸门：输出用户代码仍存在 checked/unchecked 节点（NB-13） |

原则（[catalog §4 风险 12](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L1110-L1110) 第一原则）：C# 侧无法等价表达时显式报错，**禁止注释/占位/原样透传流入 Java 产物**。转换器侧 `/* checked */` 前缀站点（[TypeOperationTransformer.cs:31](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/TypeOperationTransformer.cs#L31-L31)）与块级抛点（[SwitchAndResource.cs:303-309](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Statement/StatementTransformer.SwitchAndResource.cs#L303-L309)）在本 pass 落地后对「经预处理的项目」成为不可达路径；其删除/保留为后续 PR（同 C11 §5.3 模式），直连 ConversionPipeline 的单文件入口仍保留它们作安全网。ShouldEmitStatementText 的 `/* TODO: UncheckedStatement` 旧过滤（[StatementTransformer.cs:309-314](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Statement/StatementTransformer.cs#L309-L314)）随该后续 PR 一并清理。

---

## 7. 测试计划

### 7.1 Core 单测矩阵

新增 `tests/CSharpToJava.ProjectTests/NumericBoundaryRewriterUnitTests.cs`（仿 UnsignedTypeRewriterUnitTests/JavaKeywordRewriterUnitTests：内联源码 → `CSharpCompilation` → Rewrite → 文本断言 + `AssertOutputCompiles`）：

1. **unchecked 剥离**：`unchecked(a + b)` → `a + b`；`unchecked { … }` 块解包（含嵌套局部作用域花括号保持）；checked/unchecked 多层嵌套（`checked { unchecked(…); … }` 上下文栈正确性）；enum pass 形态的窄枚举 `unchecked((U)(…))` 包裹剥除后可编译；unsigned 形态 `UnsignedMath.AddUInt32(unchecked((int)a), unchecked((int)b))` 剥壳文本断言。
2. **checked 二元算术**：int/long 的 `+ - *` → `AddCheckedInt32/…`；一元 `-` → `NegateChecked*`；`/` `%` 不插 helper（除零保留自然运算）；checked 表达式与 checked 块两种写法；`byte/short/char` 操作数提升后走 Int32 域。
3. **复合赋值**：`x += y`（int）、`b += 1`（byte：运算 helper + `ToByteCheckedInt32` 转换）、`l *= k`；`/=` 不插。
4. **`++ --`**：int/long/简单 enum 目标的前缀/后缀；后缀旧值临时提升（断言旧值只消费一次、提升位置在语句首）；属性/索引器目标 → CS2J5324；表达式体/字段初始化器内后缀 → CS2J5324。
5. **checked 转换**：`checked((byte)i)`、`(short)`、`(char)`、`(int)l`、同宽跨符号 `(uint)i`/`(int)u` 各自 helper；宽化与同型转换不插调用；enum 目标转换保持裸 cast 不检查。
6. **常量折叠**：`unchecked((int)4294967295u)` → `-1`；`unchecked((byte)256)` → `(byte)0`（截断字面量）；checked 上下文内不溢出常量直接物化、无 helper；`const int C = …` 引用点；ulong/long 后缀字面量正确性。
7. **循环/边界**：`for (int i = 0; i < n; i++) checked 累加`、`int.MaxValue` 边界的 helper 站点；checked 块包住整个循环体。
8. **不支持族**：`dynamic` 站点 → CS2J5322；`checked((int)d)`（double）→ CS2J5321；`nint`/`Int128` → CS2J5320；`int?` lifted → CS2J5325；表达式树 lambda → CS2J5323；`CheckForOverflowUnderflow=true` 工程（M2 后转为全量显式化正例，M1 断言 CS2J5326）。
9. **边界零误伤**：decimal `checked(a + b)` 只剥壳不动运算（C34 面）；普通回绕算术（默认上下文无包裹）零改动；用户自定义 `operator +`：`unchecked(...)` 壳剥除、调用不动，`checked(...)` 壳在 member-desugar 同链启用时保留并由其剥除（断言 `CheckedUserOpShellsDeferred` 计数与最终零残留，决策 4/§11）；`/0` 保持。
10. **工程性质**：幂等（二次 RewriteFile 零 diff）；干净文件字节级一致 + BOM 保持；支撑工程路径文件 clean copy。

### 7.2 项目级 fixture 与溢出运行时行为测试

新增 `tests/SampleNumericBoundarySolution/`（`SampleNumericBoundaryLib` + `SampleNumericBoundaryApp` + `SampleNumericBoundaryTests` + `.sln`，net10.0，Content Include 进 [CSharpToJava.ProjectTests.csproj](file:///d:/code/cs2j/tests/CSharpToJava.ProjectTests/CSharpToJava.ProjectTests.csproj)，仿 SampleJavaKeywordSolution）：

- Lib：checked/unchecked 嵌套块、checked 表达式、复合赋值、`++ --`、窄化转换、循环边界求和、unchecked 常量窄化、一个仅含 unchecked 剥离（不引库）的模块、一个含不支持站点的反例文件（供错误断言）。
- Tests（**溢出运行时行为，.NET 半区守门**）：xunit 断言——checked helper 路径在溢出输入下抛 `OverflowException`（int/long/uint/ulong 各一 + 窄化转换一例）；非溢出输入值与直接算术一致；unchecked 剥离路径在 `int.MaxValue + 1` 等输入下回绕为预期值；后缀自增旧值/新值语义正确；除零仍抛 `DivideByZeroException`。**这些断言直接在去糖后的 C# 上跑，helper 实现错误会在 .NET 侧立即红。**
- 项目级测试 `NumericBoundaryProjectTests.cs`：`result.Success`、统计计数、中间树文本断言（含 `AddChecked`/`ToByteChecked`、不含 checked/unchecked 关键字节点）、**中间树 `dotnet build` 与 `dotnet test -c Release` 退出码 0**；库仅在有 checked 站点的项目被引用（仅剥离模块不引库）；干净工程字节复制与 BOM；不支持反例的退出码/诊断断言。

### 7.3 Release build/test

- `dotnet build -c Release`（CSharpToJavaConverter.slnx 全绿）；
- `dotnet test -c Release` 三个工程：CSharpToJava.Tests、CSharpToJava.ProjectTests、CSharpToJava.ConversionTests。

### 7.4 Java 半区冒烟（默认开门禁之一，非阻塞锚点合入）

- compat `UnsignedMath.java` 新增 checked 方法的 JUnit：边界值对拍（`addExact` 委托面、UInt32/UInt64 自定义检查、转换族范围边界），溢出断言 `ArithmeticException`；
- 代表性样本（checked 块 + 转换 + 自增）经完整 C#→Java 链路产出可编译 Java，helper 调用名与 compat 逐字一致；`catch (OverflowException)` 样本确认映射到 `catch (ArithmeticException)`（并记录除零同捕的近似限制，决策 14）。

### 7.5 residual 闸门

1. 语法树扫描型测试：对输出中间树所有非支撑工程 .cs 文件 parse，断言零 `CheckedExpressionSyntax`/`CheckedStatementSyntax`/`SyntaxKind.UncheckedKeyword`；存在即 CS2J5327，strict 下退出码非零（NB-13）。
2. 支撑工程豁免断言：`Cs2jUnsignedMath/UnsignedMath.cs` 原样释放（其内部 `checked`/`unchecked` 实现不动），闸门扫描明确排除该目录。
3. 上游产物回归：对 enum/unsigned 既有 fixture（SampleProject、含窄枚举与 uint 的样例）启用本 pass 后，中间树 build/test 全绿且无残留 unchecked 节点；上游各 pass 统计零变化。
4. **与 member-desugar（C14）共处回归**（硬项）：含 `operator checked +`（`op_CheckedAddition` 族）样例在 numeric-boundary + member-desugar 同链启用下，C15 输出保留 checked 壳（`CheckedUserOpShellsDeferred` 计数 > 0），C14 收口后调用绑定 checked 变体、方法名含 `Checked` 后缀（参 [OperatorNames.cs:36-47](file:///d:/code/cs2j/src/CSharpToJava.Core/MemberDesugar/Operators/OperatorNames.cs#L36-L47)），最终树零 checked 节点；仅启用 numeric-boundary、不启用 member-desugar 的组合行为按开放问题 6 定案后补断言。

### 7.6 CLI / 一致性 / 链序

- 默认关：convert-project 不产生 `.cs2j-numeric-boundary-src`、无库注入、无耗时、链序不变；启用入口为 `--enable-pass numeric-boundary`（默认模板无 `--numeric-boundary` 专用开关，§5.2/开放问题 7）；`--list-passes` 显示 Implemented/默认关/目录/说明/C15 catalog 项；CS2J9001 不再对本 id 触发；preserved 目录注册。
- [PassChainOrderTests.cs](file:///d:/code/cs2j/tests/CSharpToJava.Tests/Preprocessing/PassChainOrderTests.cs) 插位 + [PassStubCatalogTests.cs](file:///d:/code/cs2j/tests/CSharpToJava.Tests/Preprocessing/PassStubCatalogTests.cs) stub 数 21→20（含修正既有 22 漂移断言，§3.4-7）+ [ProjectPreprocessorsConsistentTests.cs](file:///d:/code/cs2j/tests/CSharpToJava.Tests/ProjectPreprocessorsConsistentTests.cs) 目录名断言，按 §3.4-7 一次改到位。

---

## 8. 默认开启验收门槛

`defaultEnabled: false → true` 必须同时满足：

1. slnx `dotnet build -c Release` 全绿；三个测试工程 `dotnet test -c Release` 全绿（含 §7 全部新增单测/fixture/CLI/链序/residual）。
2. fixture sln 中间树 build + test 全绿；幂等、字节复制、BOM、residual 闸门全绿；溢出运行时行为断言（§7.2）全绿。
3. **Java 半区就绪**：§7.4 冒烟通过——compat checked 方法面齐备且 JUnit 全绿；checked 样本经完整链路产出可编译 Java 且双端溢出行为一致；转换器 `/* checked */` 站点（[TypeOperationTransformer.cs:31](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/TypeOperationTransformer.cs#L31-L31)）在预处理路径上证实不可达（删除留后续 PR）。
4. csharp-sdk（只读训练集）启用扫描：checked/unchecked 站点总量、显式化分桶、CS2J5320-5326 命中清单；**每条 Error 命中人工定性**（真不支持 vs 分析器过保守），过保守项修到清零或进入明确后续里程碑；工程级 `/checked` 命中率给出实测（决策 15 的 M2 范围据此调整）。
5. sdk 基线不退化：启用后既有 fixture 与 sdk 抽样工程中间树 build/test 不劣于未启用；上游 pass（enum/unsigned 等）统计零变化；convert-project 总耗时增量可接受（helper 注入仅影响有 checked 站点的项目；verify 串行成本纳入指纹缓存评估，[catalog §4 风险 11](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L1109-L1109)）。
6. `--list-passes` 描述、README/ROADMAP 条目、本设计与 plan 文档齐备。

未达门槛前长期保持默认关，每个评审周期复跑第 4/5 项，避免 [catalog §4 风险 10](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L1108-L1108) 的「多套事实标准」固化。

---

## 9. 里程碑

| 里程碑 | 内容 | 预估规模 |
|---|---|---|
| M1 锚点基建 + unchecked 剥离 | `NumericBoundary/` 骨架（Models/Analyzer/SyntaxFacts/Preprocessor）+ NB-1/2/6/9/11（unchecked 剥壳、块解包、checked 表达式内非整数剥壳、常量物化）+ 适配器 + 注册表翻转（默认关、verify 双开）+ CLI 入口（`--enable-pass`/`--pass`，默认模板无专用开关，§5.2/开放问题 7）/目录/指纹接线 + 工程级 /checked 检测报 CS2J5326；空 checked 树端到端跑通（含 enum/unsigned 产物共处单测、用户运算符 checked 壳保留计数与 member-desugar 共处单测、residual 闸门对 unchecked 一侧生效） | Core ~400 行，CLI ~250 行 |
| M2 checked 二元算术与自增 | NB-3/4/5/8/10 + 支撑库 AddChecked/SubtractChecked/MultiplyChecked/NegateChecked（Int32/Int64/UInt32/UInt64）C# 与 compat Java 双侧 + 复合赋值/窄化运算 + `++ --` 简单目标（含 enum）+ 后缀临时提升 + 工程级 /checked 全量支持（停用 CS2J5326）+ 溢出运行时 fixture；§7.1-2/3/4/7/9 单测 | ~600 行 + 支撑库双侧 |
| M3 checked 转换 | NB-7 + 转换方法族双侧（窄化与同宽跨符号全套）+ CS2J5320-5325 不支持站点闸门打磨 + fixture 转换反例 + compat JUnit 补齐 + Release build/test 全绿 | ~350 行 + fixture |
| M4 默认开评审 | sdk 扫描与 ErrorsByReason 报表、Java 半区冒烟（§7.4）、转换器消费核对（§6 后续 PR 底稿）、本设计定稿 + plan 文档；门槛通过后单行翻默认开；sizeof 收编评审（开放问题 5） | 文档/数据 |

---

## 10. 风险

1. **静默错误输出族**（[catalog §4 风险 12](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L1110-L1110)）：`/* checked */` 前缀是「不报错却丢语义」的典型。缓解：本 pass 以 residual 闸门物理消灭全部 checked/unchecked 节点；无法显式化即 CS2J53xx Error 失败；转换器侧站点删除前，直连单文件路径保留安全网。
2. **映射存在 ≠ 语义可用**（[catalog §4 风险 14](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L1112-L1112)）：checked helper 在 compat 是**全新方法面**，先发射后补类会产出不可编译 Java。缓解：支撑库双侧方法清单（§4.4）与 JUnit 是 M2/M3 交付物的一部分，§8-3 Java 冒烟为默认开硬门禁；TypeMappings 方法名派生 M5 抽样逐字核对。
3. **性能开销**：每个 checked 站点从内联运算符变为静态调用。.NET 半区小方法被 JIT 内联（`checked` 算术体即单条 overflow IL）；Java 半区 `Math.*Exact` 在 HotSpot 同样常被内联；窄化转换多一次比较分支。缓解：helper 全部 `static`、无分配、无异常路径对象构造以外开销；fixture 中保留一个热点循环微基准作为抽样（不设硬性阈值，防过度工程）。
4. **catch 粒度近似**：`OverflowException` 与 `DivideByZeroException` 在 Java 同映射为 `ArithmeticException`，`catch(OverflowException)` 会额外捕获除零。缓解：v1 文档化（决策 14），Java 冒烟记录该近似；是否引入 compat 异常子类留开放问题 4，由 sdk catch 命中扫描决定。
5. **与 unsigned 产物的交互误判**：回绕 helper 调用被二次包裹、或 checked 区内 uint 真身被漏判都会造成「编译能过、行为错」。缓解：以 unsigned 的 `IsInCheckedContext` 跳过契约为界（§1.4），单测覆盖「区外回绕调用不动 / 区内真身插检查」两侧；共存 fixture 为硬回归项。
6. **常量物化的字面量正确性**：ulong/long/负数值物化错误会制造可编译但值错的程序。缓解：字面量生成集中在 SyntaxFacts 单点，逐类型单测（含 `0xFFFFFFFF`、min/max、负数）；verify build 之外由 §7.2 运行时值断言双保险。
7. **工程级 /checked+ 的逐项目判定**：整树单一 compilation 丢失逐项目编译选项。缓解：csproj 自行解析 + 文件→项目归属表（决策 15）；M1 先显式报错不静默；多项目混编（同 sln 不同设置）单测覆盖。
8. **上游节点形态变化**：enum/unsigned 调整其 `unchecked` 发射形态、或 C17/C34/C41 提前落地都可能影响本 pass 输入。缓解：本 pass 只认语义模型与语法节点种类，不认上游方法名（回绕 helper 调用按普通调用透传）；规则分目录所有权（§4.1）隔离并行修改。

---

## 11. 并行协调

- **族内后续规则（C17/C22/C34/C41，同 pass 增量扩展）**：
  - **合入顺序：C15 先行**。C17/C22/C34/C41 的分支基于 C15 合入后的主干 rebase，各自只增 `Rules/IncrementTargets/`、`Rules/NullableNumeric/`、`Rules/DecimalOps/`、`Rules/SpecialNumeric/` 子目录与自有诊断码段（§4.5）、测试/fixture，不碰根目录文件与适配器；共享组件不够用时先在主干扩根层并补 C15 回归。
  - 分工边界：C15 只处理内置整数简单目标；复杂自增目标（属性/索引器/事件/enum 读-改-写）归 **C17**——C17 落地前这些站点在 checked 区由 CS2J5324 显式失败（C17 落地时把该码对应站点转为自己的重写并停用该报错）；nullable/lifted 归 **C22**（CS2J5325）；decimal 运算符（checked 对其无增减效应，但表达式仍需归约到 compat `Decimal.add/…` 方法面）归 **C34**——C15 只保证剥壳与零关键字，不动 `+`；Half/Int128/UInt128/nint/IntPtr 与 sizeof 归 **C41**（CS2J5320）。
  - 每条规则落地时 CatalogItems 追加一个编号（§3.4-9），描述符其余字段不变；链序测试不动（同 pass 内规则不进注册表）。
- **与 unsigned（E09，已实现）**：RunAfter 锚点锁死；本 pass 复用其支撑工程、释放与 ProjectReference 机制（§5.1 共享提取）；checked 区内的真身 uint/ulong 是本 pass 职责，两 pass 对 `IsInCheckedContext` 的理解必须一致，M1 提交中以共处单测钉死。
- **与 enum（E04，已实现）**：本 pass 是其窄枚举 `unchecked` 包裹（[EnumSyntaxRewriter.cs:498-540](file:///d:/code/cs2j/src/CSharpToJava.Core/EnumPreprocessor/EnumSyntaxRewriter.cs#L498-L540)）的消费收口；enum `++ --` 在 checked 区的简单目标由 C15 处理、复杂目标归 C17。
- **与 C14 operator（member-desugar，已翻转 Implemented、默认关，26ea367c；C14 规则随该 pass 交付，2d25700e 标记已实现）**：①声明侧——C#11 checked 用户运算符（`operator checked +`，元数据名 `op_CheckedAddition` 族）已由 C14 去糖为 `AddChecked` 命名（[OperatorNames.cs:36-47](file:///d:/code/cs2j/src/CSharpToJava.Core/MemberDesugar/Operators/OperatorNames.cs#L36-L47)，与转换器 [OperatorTransformer.cs:96-98](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Member/OperatorTransformer.cs#L96-L98) 的 `Checked` 后缀对齐）；②站点侧——member-desugar 已在 [OperatorUseSiteRewriter.cs:236-253](file:///d:/code/cs2j/src/CSharpToJava.Core/MemberDesugar/Operators/OperatorUseSiteRewriter.cs#L236-L253) 对「仅包裹已规划用户运算符调用」的 checked 壳做冗余剥除（checked 语义由变体选择承载），catalog C14 六次补全①所述联动由此落地；③**链序事实（关键）**：numeric-boundary（RunAfter unsigned，链前段）先跑，member-desugar（RunAfter struct-value-semantics/closure-lift，实测在 eliminate-goto 之后）后跑——C15 先见到用户运算符 checked 壳。故分工为：C15 以 `OperatorMethod != null` 切分，对用户运算符**不插 helper、不剥 checked 壳**（`unchecked` 壳照剥），原样留给 C14；NB-13 闸门对这批壳计数豁免。**待 M1 共处单测证实的风险**：C14 在 C15 输出的重建树上做语义绑定，checked 壳是其选中 `op_Checked*` 变体的唯一语法上下文，若 C15 误剥会导致改绑普通变体（方法名丢 `Checked` 后缀、溢出语义丢失）；C14 文档（[2026-09-28-c14-operator-desugar-design.md](file:///d:/code/cs2j/docs/superpowers/specs/2026-09-28-c14-operator-desugar-design.md) 风险 9）亦只信绑定符号元数据名、不读语法推断。仅启用 C15 不启用 C14 的组合见开放问题 6。
- **与转换器侧 LinqRewriter**：[LinqRewriter.Rules.cs:72](file:///d:/code/cs2j/src/CSharpToJava.Core/LinqRewrite/LinqRewriter.Rules.cs#L72-L72)/[L233](file:///d:/code/cs2j/src/CSharpToJava.Core/LinqRewrite/LinqRewriter.Rules.cs#L233-L233) 合成 checked 块发生在转换器内部、**不经过预处理链**，本 pass 看不到它们；当前合成体还会撞块级抛点。登记为转换器侧后续协调项：其求和溢出策略（显式 helper 形态或 unchecked）须在 Java 半区消费 PR 中定死，不阻塞 C15。
- **热文件冲突解法**：PassStubCatalog.cs 仅本任务删除 numeric-boundary stub 条目一条（`Ids.NumericBoundary` 常量**保留**，§3.4-2）；PassCatalog.cs/PassChainFactory.cs 各只增自己条目/工厂分支，默认模板不碰 Program.cs（若开放问题 7 选专用开关则增 Option 一行）；PassChainOrderTests/PassStubCatalogTests 由本任务一次改到位（插位 + stub 计数改到 20 并修正既有 22 漂移），同期翻转任务互不重叠（行级）；CatalogItems 字符串的一行差异是族内后续规则的预期冲突点，按合入顺序追加。
- **与 java-keywords（C08/E17，终命名）**：helper 名是固定英文标识符，不撞 Java 关键字；支撑类名既已通过 TypeMappings 路由，无需 JK 介入。

---

## 12. Worktree 分配

- 分支：`feature/preproc-c15-checked-unchecked`
- 目录：`.worktrees/preproc-c15-checked-unchecked`
- base：`30d13211`（2026-09-30 dev-3.x 最新提交，`feat(preprocessing): C31 Index/Range 落地 + 已落地 pass 默认开启收口`；2026-09-29 校订曾定为 `2d25700e`，更早的 `f68a783c` 仅为 stub 初登点——从旧 base 切支会缺失 §3.4 全部先例与 PassCatalog 当前链位，故随 dev-3.x 前进更新；实施时代码行号以该 base 实测为准）
- 创建命令（在仓库根、PowerShell 下执行；本设计阶段不执行）：

```powershell
git worktree add .worktrees/preproc-c15-checked-unchecked -b feature/preproc-c15-checked-unchecked 30d13211
```

- worktree 内交付面遵循 foundation plan「并行开发交付包模板」（[preprocessor-foundation_plan.md:108-116](file:///d:/code/cs2j/.trae/documents/preprocessor-foundation_plan.md#L108-L116)）：Core `NumericBoundary/`（共享基建 + CheckedArithmetic 规则）、CLI 单适配器 + 注册表翻转、支撑库双侧方法（Cs2jUnsignedMath + compat UnsignedMath.java + JUnit）、本设计文档与后续 plan 文档（`docs/superpowers/plans/2026-09-28-c15-checked-unchecked.md`）、Core 单测 + fixture sln + Release build/test + residual 闸门 + CLI/链序测试；不新建支撑工程；不触转换器代码（§6 消费清单为后续 PR 输入）。

---

## 附：关键开放问题（供评审拍板）

1. **支撑库形态**：复用扩展 `Cs2jUnsignedMath`（本设计推荐，六条链路零新增）还是新建独立 `Cs2jCheckedMath`（命名干净但要新增 PassSupportLibrary 枚举、释放/过滤/指纹/Maven 排除全套接线与独立 compat 类）？若评审选新库，§3.3/§5.1 的注入与过滤设计需整体改写。
2. **checked 浮点/decimal→整数转换的收编时点**：v1 显式 CS2J5321 失败（推荐，NaN 语义 Java 无 1:1）；若 sdk 扫描显示高命中，是否提前到 M3——helper 侧可用范围比较实现（NaN 即抛），代价是 Java 半区每站点一次 `Double.isNaN` + 边界分支。
3. **工程级 `CheckForOverflowUnderflow=true` 的支持深度**：M2 全量显式化（推荐，机制相同）还是长期维持 CS2J5326 显式拒绝？取决于 §8-4 sdk 实测命中率。
4. **`catch(OverflowException)` 的异常粒度**：接受 `ArithmeticException` 合并映射（v1 推荐，零 compat 新增），还是在 compat 引入 `OverflowException extends ArithmeticException` 并让 checked helper 抛该子类以区分除零？后者需同步评估既有 ExceptionApiRewriter 映射与全 compat 异常层次。
5. **`sizeof` 占位是否同 PR 收编**：常量折叠（NB-6）就位后，预定义类型 sizeof 仅差「编译期整数字面量替换 + 用户类型/Int128 显式报错」一小块；纳入可更早消灭 [TypeOperationTransformer.cs:163](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/TypeOperationTransformer.cs#L163-L163) 的同族静默站点，但会使锚点任务超出 checked/unchecked 纯范围。建议 M4 视余量决定，默认留给 C41。
6. **用户自定义运算符 checked 壳的跨 pass 交接（校订新增）**：决策 4/NB-13 已定「C15 保留、C14（member-desugar）收口」，仍有两点待 M1 验证/评审：①「C15 剥壳会使 C14 在重建树上改绑普通变体」目前是基于 Roslyn 上下文绑定规则的推断，需以共处 fixture 在中间树重编译后实测绑定符号（若证伪则 C15 可直接剥壳、豁免取消）；②**numeric-boundary 启用而 member-desugar 不启用**时，残留的用户运算符 checked 壳如何处置——三选：(a) CS2J5327 按 Error 失败（最严，强制两 pass 同开）；(b) 计数 Warning 放行（转换器对方法调用本就透传，checked 变体声明侧已有 `Checked` 后缀映射，可能天然可工作，但需 Java 半区抽样证实）；(c) C15 自行判 `IBinaryOperation.IsChecked`/元数据名并对纯调用壳剥除。v1 倾向 (a)，待 sdk 命中与 M1 证据定案。
7. **是否要 convert-project 专用 `--numeric-boundary` 开关（校订新增）**：默认清单按 IteratorLowering 先例不设（描述符默认关 + `--enable-pass`/`--pass` 可达，零 Program.cs 改动）；若评审要求与 unsigned/member-desugar 一致的显式开关体验，则整体改走 member-desugar 模板（PassIds 常量、删 stub 常量、`opts.NumericBoundary` toggle、[Program.cs:4844-4846](file:///d:/code/cs2j/src/CSharpToJava.CLI/Program.cs#L4844-L4846) 式 Option），§3.3/§3.4-1~5/§5.2/§7.6 随之一致改写。
