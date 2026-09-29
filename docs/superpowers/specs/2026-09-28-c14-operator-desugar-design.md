# C14 Operator 去糖（转换运算符为重点难点）设计

**日期**: 2026-09-28
**范围**: 在已合入的 C11 property 锚点任务所建的 `member-desugar` 族基建上，新增 Core `MemberDesugar/Operators/` 规则目录：把用户自定义运算符（算术/比较/位运算/一元）与 `implicit`/`explicit` 转换运算符的**声明归约为普通静态方法**，并把全部调用点（二元、一元、复合赋值、隐式/显式转换站点）按 SemanticModel 绑定**显式化**为静态方法调用与显式转换链；产物为可编译 C#。本特性不改任何 C#→Java 转换代码，不新增/翻转 pass 注册
**状态**: 设计稿，待评审；实施前置 = C11 分支合入
**族属**: catalog P2「成员去糖族」（[preprocessor-catalog.md §3.3](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L904-L904)），归唯一已注册 pass `member-desugar`（[PassStubCatalog.cs:191-200](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassStubCatalog.cs#L191-L200)，catalogItems 含 C14）。**C11 Property 是锚点任务**（[C11 设计](file:///d:/code/cs2j/docs/superpowers/specs/2026-09-28-c11-property-desugar-design.md)）：共享基建、适配器、stub 翻转均由 C11 完成；C14 只在 C11 合入后的主干上增加 `Operators/` 规则，**不单独注册、不翻转 stub、不新增适配器、本分支对 PassCatalog/PassChainFactory 零改动**（§3、§11）

---

## 1. 背景与目标

### 1.1 用户自定义运算符的现状

Java 没有运算符重载，C# 用户自定义运算符必须在 Java 产物中表现为静态方法 + 显式调用。当前这件事压在主转换器的单文件路径上，且声明侧与使用点侧分裂：

- **声明侧**：[OperatorTransformer.cs](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Member/OperatorTransformer.cs)（290 行）把 `operator +` 转成 Java 静态方法，核心是 [OpSymbolToJavaName L23-L51](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Member/OperatorTransformer.cs#L23-L51)（Roslyn 元数据名 `op_Addition` → Java 名 `add` 等 25 项）与转换运算符的 [TransformConversion L181-L246](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Member/OperatorTransformer.cs#L181-L246)（`implicit operator double` → `toDouble`，后缀算法 [BuildConversionMethodSuffix L293-L314](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Member/OperatorTransformer.cs#L293-L314)）；C# 11 checked 运算符靠 [L96-L98](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Member/OperatorTransformer.cs#L96-L98) 追加 `Checked` 后缀消歧；同名碰撞靠 Java 侧冲突解决（[OperatorMethodNameCollisionTests.cs](file:///d:/code/cs2j/tests/CSharpToJava.Tests/OperatorMethodNameCollisionTests.cs) 锁定的「运算符保 camelCase、用户方法改 PascalCase」是 Java 侧行为，C# 侧 `operator +` 与用户方法 `Add` 本就合法共存）。
- **二元使用点**：[BinaryExpressionSupport.cs:155-186](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Utilities/BinaryExpressionSupport.cs#L155-L186) 经 `MethodKind.UserDefinedOperator` + `HasSourceDeclaration` 解析后反查同一张表渲染 `Type.add(a, b)`（[BinaryExpressionTransformer.cs:243-249](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/BinaryExpressionTransformer.cs#L243-L249)）；但 enum 操作数在 [L135-L145](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/BinaryExpressionTransformer.cs#L135-L145) 硬拒绝（归 E04/C05）。
- **一元使用点**：用户自定义一元运算符直接 fail-fast——[UnaryExpressionTransformer.cs:140-144](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/UnaryExpressionTransformer.cs#L140-L144) 对 `MethodKind.UserDefinedOperator` 抛 `NormalizationRequired("user-defined unary operator")`；decimal 一元在 [L148-L152](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/UnaryExpressionTransformer.cs#L148-L152) 硬拒绝（归 C34/numeric-boundary）。
- **复合赋值**：`+=` 等的用户运算符应用没有独立展开（C# 语义是 `x = (T)(x op v)`，含一次可能绑定用户转换的「回转」），散落在赋值路径里，属性/索引器目标与 C11/C12/C17 耦合。
- 实验形态 [LowerOperator.cs:38-63](file:///d:/code/cs2j/src/CSharpToJava.Core/Lowering/LowerOperator.cs#L38-L63) 证明「运算符调用 → 普通静态调用」目标形态可行；但依据 [catalog §4 风险 9](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L928-L928)，Lowering 仅被 Java2 对比测试引用、生产不可达，**本规则只借形态、零 using Lowering**。

### 1.2 转换运算符的「静默错误面」（本任务重点难点）

转换运算符比算术运算符危险得多，因为它的使用点**没有专属语法**，今天既不 fail-fast 也无专门转换路径：

- **显式 cast 站点静默产出错误 Java**：[TypeOperationTransformer.Cast L53-L86](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/TypeOperationTransformer.cs#L53-L86) 只特判指针与 enum，绑定用户转换的 `(Target)x` 落入 [L84](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/TypeOperationTransformer.cs#L84-L84) 的兜底 `(Target)(e)`——一个 Java 引用强转，语义完全错误且能生成文本，属 [catalog §4 风险 12](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L931-L931) 所列「静默错误输出」同族。
- **隐式转换站点完全无形**：`Target t = x;`、`return x;`、实参传值、`? :` 分支归并、初始化器等位置的用户定义隐式转换在 C# 源码里没有任何节点变化，Java 侧不存在对应隐式转换，当前产物静默丢语义；声明侧虽由 [ClassTransformer.cs:361-362](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Type/ClassTransformer.cs#L361-L362)、[StructTransformer.cs:247-248](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Type/StructTransformer.cs#L247-L248)、[RecordTransformer.cs:467-469](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Type/RecordTransformer.cs#L467-L469) 发出 `toXxx` 方法，却没有任何调用点改写与之配对。
- **提升（lifted）转换/运算符链**：`S? → T?`、`S? + S?` 的空提升由 C# 编译器按规则合成（关系运算结果是 `bool?`、`==/!=` 有空相等规则、`&`/`|` 在门控类型上有三值规则），纯语法无法识别，必须经 `SemanticModel.GetOperation`/`GetConversion` 拿到实际绑定方法与提升标记后逐站重建。
- **表达式树是共同禁区**：[catalog §4 风险 8](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L927-L927) 明确——C14 的转换运算符在 `Expression<T>` lambda 内改写会改变表达式树形态（cast/二元节点变成方法调用节点；声明改名后树里引用的 `op_Implicit`/`op_Addition` 元数据名消失，整个 lambda 不可编译），必须与 C08（JK-8）共享同一「表达式树节点内跳过」判定。
- 外部元数据类型的转换另有转换器专门路径（如 `byte[] → ReadOnlyMemory<byte>` 包 compat 构造函数，[UserDefinedConversionGenericTargetTests.cs](file:///d:/code/cs2j/tests/CSharpToJava.Tests/UserDefinedConversionGenericTargetTests.cs)），C14 只处理**源内声明**符号，不碰这些站点（§2 决策 9）。

### 1.3 目标

在 C#→C# 层把全部可处理运算符归约为：

1. **普通静态方法**：`operator +(T a, T b)` → `public static T Add(T a, T b)`；`implicit/explicit operator R(T x)` → `public static R ToR(T x)`（命名严格对齐转换器现发射名，见 §2 决策 1/3），修饰符/属性/文档/trivia 平移；
2. **调用点全部显式化**：二元/一元 → `T.Add(a, b)` / `T.Negate(a)`；复合赋值 → 只求值一次左值的 `tmp = (X)(T.Add(tmp, v))` 回转链；隐式/显式转换站点 → `T.ToR(x)`（必要时保留外层标准转换 cast）；提升运算/提升转换 → 显式三目空传播展开；
3. 一切站点判定以全编译 SemanticModel **实际绑定的 MethodSymbol** 为准，禁止文本替换、禁止自行实现重载决策；
4. 产物仍是可编译 C#，由中间树 `dotnet build -c Release` + `dotnet test -c Release` 证明 .NET 半区等价（catalog §5.1 两半区方法论）。

覆盖：算术（`+ - * / %`）、关系与相等（`== != > < >= <=`）、位/移位（`& | ^ ~ << >> >>>`）、一元（`-x +x !x ~x`）、C# 11 checked 运算符对、implicit/explicit 转换（直接与提升/链式）、复合赋值（变量/字段/静态/属性目标）、泛型类型中的运算符、同类型多重载。

明确不做（边界，§2 决策 6/12、§11）：`operator true`/`operator false`（**已实现的 java-keywords 通道 JK-5~JK-8 所有**，C14 不碰声明与布尔使用点）；`operator ++`/`--` 声明与站点（**归 C17** 自增族，本规则只划界并留残余白名单）；接口 `static abstract/virtual` 运算符（C# 11 generic math，v1 显式跳过 CS2J5175）；enum 与内建 decimal 运算（E04/C05、E09、C34 既有归属）；元数据-only 运算符站点（转换器既有路径）；Java 半区任何代码改动。

### 1.4 硬不变式

1. 中间源码树预处理后永远可编译（整树重编译相对基线无新增错误），**operator plan 为原子单位**（声明与全部站点同生同灭，全转或全不转），范式与 record-readonly/java-keywords/C11 完全相同；
2. 不静默丢任何运算符语义：不可判定/不可重建即显式诊断、plan 整体不动，禁止注释/占位/原样透传（[catalog §4 风险 12](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L931-L931)）；
3. 一切站点判定来自全编译 SemanticModel（[catalog §3 横切约束](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L896-L896)）；
4. 幂等：产物二次执行零 diff；干净文件字节级复制（含 BOM 保持）。

---

## 2. 关键设计决策表

| # | 决策点 | 结论 | 依据/备注 |
|---|---|---|---|
| 1 | 算术/比较/位/一元运算符方法命名 | C# 半区方法名 = 转换器现发射 Java 名的 **PascalCase 形态**，由 C14 新建静态表/函数 `ConvertOperatorName(IMethodSymbol)` 给出；它必须与转换器历史函数 `MethodTransformer.ConvertOperatorName`（86ed955 后重构为 [OperatorTransformer.OpSymbolToJavaName L23-L51](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Member/OperatorTransformer.cs#L23-L51)，[JK 设计 §背景 L13](file:///d:/code/cs2j/docs/superpowers/specs/2026-09-28-java-keywords-preprocessor-design.md#L13-L13)）**逐行对齐**：`op_Addition→Add`、`op_Subtraction→Subtract`、`op_Multiply→Multiply`、`op_Division→Divide`、`op_Modulus→Mod`、`op_Equality→ValueEquals`、`op_Inequality→NotEquals`、`op_GreaterThan→GreaterThan`、`op_LessThan→LessThan`、`op_GreaterThanOrEqual→GreaterThanOrEqual`、`op_LessThanOrEqual→LessThanOrEqual`、`op_BitwiseAnd→And`、`op_BitwiseOr→Or`、`op_ExclusiveOr→Xor`、`op_LogicalNot→Not`、`op_OnesComplement→OnesComplement`、`op_LeftShift→LeftShift`、`op_RightShift→RightShift`、`op_UnsignedRightShift→UnsignedRightShift`、`op_UnaryNegation→Negate`、`op_UnaryPlus→Plus` | 对齐原理：转换器普通方法路径 [MethodTransformer.JavaName L178-L187](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Member/MethodTransformer.cs#L178-L187) 对普通标识符仅首字母小写、无运算符特例，因此去糖 C# 方法 `Add` 经转换器必得 `add`，与 OperatorTransformer 当前发射**逐名相等**——无碰撞主路径 Java 半区零漂移。否决直接保留 Roslyn 元数据名 `op_Addition`：该族名为 C# 保留名，手写普通方法声明会被编译器拒绝（运算符槽位名），M1 以探针单测钉死该事实与确切诊断号；`And/Or` 等是普通标识符（非保留字），合法 |
| 1b | checked 运算符（C# 11） | `op_CheckedAddition→AddChecked`、`op_CheckedSubtraction→SubtractChecked`… 规则 = 决策 1 基名 + `Checked` 后缀，与转换器 [OperatorTransformer.cs:96-98](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Member/OperatorTransformer.cs#L96-L98) `javaName += "Checked"` 对齐（Java 侧得 `addChecked`）。站点按 `IBinaryOperation.IsChecked` / 转换语义的 checked 上下文绑定选方法：checked 上下文绑 `op_Checked*`，unchecked 绑普通版 | 同一类型可同时声明两版（不同元数据名），去糖后是两个普通重载，C# 合法。checked **表达式/块对内建数值**的归一归 C15；本规则只负责「checked 用户运算符」的声明与站点 |
| 2 | 二元/一元调用点改写 | 按 `GetSymbolInfo(node).Symbol`（MethodKind.UserDefinedOperator、源内声明）把 `a + b` → `Type.Add(a, b)`、`-a` → `Type.Negate(a)`；接收类型限定名用 `ToMinimalDisplayString`（同 [OperatorTrueFalseRewriter.cs:192-194](file:///d:/code/cs2j/src/CSharpToJava.Core/JavaKeywords/OperatorTrueFalseRewriter.cs#L192-L194)），无歧义时允许非限定。内建数值运算（含 decimal）与 enum 运算不绑用户运算符符号，**天然不命中**（E04/E09/C34 边界靠符号谓词保证，不靠语法名） | 多重载（`Point*(Point,Point):double` 与 `*(double,Point):Point`）绑定即所得，去糖后为普通重载，[OperatorMethodNameCollisionTests](file:///d:/code/cs2j/tests/CSharpToJava.Tests/OperatorMethodNameCollisionTests.cs) 场景天然成立；严格区分二元 `-`/`+` 与一元（以符号名 `op_Subtraction` vs `op_UnaryNegation` 判定，不参数目） |
| 2b | 复合赋值调用点 | `x += v`（绑定用户二元运算符 m 与可选回转转换 c）→ 左值只求值一次：简单局部/参数直写 `x = c(Type.Add(x, v))`；带副作用接收者 `Get().X += v` 先提升 `__cs2j_tmp`（临时名沿用全仓 `__cs2j` 前缀惯例，同 C11 PD-6/JK-6d 纪律）；回转转换 c 为用户定义时按决策 3 显式化，为标准隐式转换时不写 cast，仅当需要显式标准转换时保留 `(X)` | 复合赋值语义恒等 `x = (X)(x op v)`，回转 cast 必须按 `ICompoundAssignmentOperation` 的实际转换生成，禁止凭空补/省。事件 `+=`/`-=` 绑 IEventSymbol 不命中（C13）；属性目标由 C11 复合重写承载、通过共享运算符调用渲染器生成内层（§4.4 OC-8）；索引器目标在 C12 合入前 CS2J5179 跳过该 plan |
| 3 | implicit/explicit 声明 → 显式转换方法链 | `public static implicit operator R(T x)` → `public static R ToR(T x)`；explicit 同名方法：v1 名为 `ToRExplicit`（两版若在同类型共存则 C# 半区必须不同名；M1 探针确认是否允许同 (S,T) 对同时声明两版并钉死规则）。转换名后缀取**目标类型**：基元/常用类型用与 Java 映射一致的固定别名镜像表（`Double→Double`、`Int32→Int`、`Int64→Long`、`Single→Float`、`Boolean→Boolean`、`String→String`、`Decimal→Decimal`、`Byte→Byte`、数组 `Byte[]→ByteArray`），算法复刻 [BuildConversionMethodSuffix L293-L314](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Member/OperatorTransformer.cs#L293-L314)，保证经 [MethodTransformer.JavaName](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Member/MethodTransformer.cs#L178-L187) 派生后 == 转换器今发 `toR`（如 `ToInt32?` 否——镜像表把 Int32 映为 `Int`，C# 方法名即 `ToInt`，Java 得 `toInt`，对齐今发）。镜像表外目标（泛型实例化/tuple/指针等）v1 → CS2J5177 plan 跳过并列 sdk triage | 命名与 Java 映射耦合沿用 C11 决策 3 先例（复用 JavaNaming 保后备字段零漂移）；交叉核对测试对每个别名断言「C# 半区名 → JavaName 派生 == TransformConversion 今发名」。外部元数据类型（ReadOnlyMemory 等）的转换不属本规则，转换器 compat 构造函数路径不受影响 |
| 4 | implicit/explicit 站点枚举与显式化 | 以 `SemanticModel.GetOperation` 全树枚举 `IConversionOperation`：凡 `Conversion.IsUserDefined` 且 `MethodSymbol` 为**源内声明**的转换运算符，即改写为 `DeclType.ToR(inner)`。覆盖：显式 cast、赋值/变量与字段初始化器、return/yield-return、实参（含 params/扩展方法实参）、`? :` 分支与 switch 表达式臂归并、`??`/`??=`、数组/集合初始化器元素、属性/索引器 set、base ctor 初始化器、lambda 体推断、foreach 转换等一切隐式站点。嵌套结构（标准转换 ⊃ 用户转换 ⊃ 标准转换）只替换用户转换层；用户方法返回类型到目标类型之间残留的**必要外层标准转换**保留为显式 cast（按「方法返回类型是否可隐式转到目标类型」判定） | 用户转换在 C# 链中至多一层、两侧可夹标准转换，绝不自行做重载/提升决策——方法符号、IsNullable/IsUserDefined 标记全以 Roslyn operation 为准；operation 缺失/绑定错误/动态 → CS2J5176/5174 跳过 |
| 5 | 用户定义提升（lifted）转换/运算符的歧义消解 | 判定：operation 的转换/运算符 `IsUserDefined` 且处于 nullable 形态（`Conversion.IsNullable`；二元 `IBinaryOperation.IsLifted`、一元同理），`MethodSymbol` 为底层非空运算符。一律按 C# 规范显式展开，公式锁定：①提升转换 `S?→T?`：`s.HasValue ? D.ToR(s.GetValueOrDefault()) : (R?)null`；②提升一元：`a.HasValue ? D.Negate(a.Value) : null`；③提升算术/移位/位运算（非门控 `&`/`|`、非相等）：`(a.HasValue & b.HasValue) ? D.Add(a.Value, b.Value) : null`；④提升关系运算（结果 `bool?`）：`(a.HasValue & b.HasValue) ? D.GreaterThan(a.Value, b.Value) : (bool?)null`；⑤提升相等：`(!a.HasValue && !b.HasValue) ? true : (a.HasValue && b.HasValue) ? D.ValueEquals(a.Value, b.Value) : false`，`!=` 取三值互补（双 null→false、一 null→true）；⑥提升复合赋值在左值 null 时写回 null，非 null 时按③/⑤取值；门控类型的提升 `&`/`|` 三值表随决策 7 整体留 JK/转换器 | 展开只做「语法显式化」，公式正确性由 fixture 的 .NET xunit 双端断言（与不重写基准逐值比对，覆盖双 null/单 null/双值）。链式提升（S?→T? 经 S→T 外加标准 nullable 包裹）只改写用户转换节点，外层标准转换保留；operation 无法给出底层方法符号 → CS2J5176 plan 跳过，禁止猜链 |
| 6 | 表达式树 / dynamic 跳过 | 运算符或转换的任一站点位于 `Expression<Lambda>` 树内 → 该运算符符号的 plan **原子跳过**（声明与全部站点不动），CS2J5173；复用 C11 共享 `MemberDesugarSyntaxFacts` 的表达式树包含判定（[catalog §4 风险 8](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L927-L927) 多通道共享工具）。`dynamic` 绑定站点（运行时分派，编译期无确定 MethodSymbol）→ CS2J5174 plan 跳过。overrides.force **仅解除表达式树一项**（用户知情接受树形态变化，同 JK-8 force 语义）；dynamic/接口静态抽象/不可重建链 force 不解除 | 声明改名会使树 lambda 引用的 op_* 元数据方法消失而整体不可编译，故跳过粒度是「一个运算符符号 + 其全部站点」而非单站点 |
| 7 | 与 JK op_true/op_false 的精确边界 | **按绑定符号名 + 链序双重切分**：①C14 拥有：除 `op_True`/`op_False`/`op_Increment`/`op_Decrement` 外的全部 MethodKind.UserDefinedOperator，与全部 MethodKind.Conversion（含 `(bool)x`、`bool b = x;`、`return x;`、bool 实参——JK-6g 明确这些站点 JK 不动）；②JK（已实现，链最后）拥有：op_True/op_False 声明与 if/while/do/for/`?:`/when/`&&`/`||` 布尔站点（[JK 设计 JK-5~JK-8](file:///d:/code/cs2j/docs/superpowers/specs/2026-09-28-java-keywords-preprocessor-design.md#L179-L228)）；③裸 `&&`/`||` 即使结果类型为 T（非布尔位置）也归 JK-6d/e；`!x`（op_LogicalNot）归 C14；④**门控运算符组合规则**：凡声明了 op_True/op_False 的类型，其 `op_BitwiseAnd`/`op_BitwiseOr` 的声明与站点 C14 **保留不处理**（CS2J5180 Info 计数）——否则 JK-6d/e 在后续通道合成的 `x & y`/`x | y` 将因声明已方法化而无法绑定，JK 整树验证破裂；门控类型上的 `^`/算术/比较/一元/转换仍正常去糖；⑤`if(x)` 经 op_Implicit→bool（非 op_True）成立时归 C14：重写为 `if (D.ToBoolean(x))`，JK 之后见到的是普通 bool 表达式，不包裹 | 链序 member-desugar（Declarative）先、java-keywords（FinalNaming）后。组合启用 fixture（SqlBoolean 型三值类型：op_True/op_False + `&`/`|` 保留 + 算术/转换去糖）为硬测试；仅启用 member-desugar 时门控运算符维持运算符语法，转换器既有路径可消费，现状不退化 |
| 8 | 方法名碰撞 | 首选决策 1/3 基名；同类型已存在同名/同签名成员（用户可合法手写 `Add(T,T)`，见 [OperatorMethodNameCollisionTests](file:///d:/code/cs2j/tests/CSharpToJava.Tests/OperatorMethodNameCollisionTests.cs)）时，**生成方法**在共享命名预留表中取确定性消歧名：一级后缀 `Operator`（`Add→AddOperator`，转换方法 `ToInt→ToIntOperator`），仍撞则序号升级 `AddOperator2/3…`；CS2J5172 Info 留痕。**用户既有成员一律不改名**（保其反射契约），运算符站点按符号绑定走生成名、用户方法站点零改动 | 与 Java 现状碰撞语义（运算符保 camel、用户方法改 Pascal）不同形：无碰撞主路径零漂移；碰撞路径 Java 名会变为 `addOperator`，列入 §8 Java 半区消费核对项，由转换器冲突解决/映射在消费 PR 收口；不做即兴二义改名，预留表在分析期一次算定 |
| 9 | 元数据-only 运算符 | 站点绑定的 MethodSymbol 无源码声明（BCL、compat 库、外部程序集，如 ReadOnlyMemory 构造函数转换）→ 不改写，CS2J5182 Info 计数；与 [BinaryOperatorResolution.IsSourceUserDefinedOperator L158-162](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Utilities/BinaryExpressionSupport.cs#L158-L162) 同一 `HasSourceDeclaration` 口径 | 声明不在树中，改站点会指向不存在的方法；转换器对这些形态有专门路径，保持不退化 |
| 10 | 接口静态抽象/虚运算符 | 接口中的 `static abstract/virtual operator +`（C# 11 generic math，`IAdditionOperators<>` 族）声明与经接口约束解析的站点 v1 整 plan 跳过，CS2J5175 | 改写会触碰 generic math 的 op_ 元数据契约且涉及接口静态分派显式化，与 C36 新语法防线同族；sdk 命中后单独立项 |
| 11 | nameof / 反射契约 | 运算符没有 nameof 形态（`nameof(operator +)` 非法），无 nameof 站点。反射字符串契约 `GetMethod("op_Addition")`/`GetMethod("op_Implicit")`、表达式树 Method 元数据名、按名序列化/XAML **不保证**，沿用 E17/JK/C11 同一限制声明与 overrides 机制（[catalog §4 风险 7](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L926-L926)） | 去糖后运行时不再有 op_* MethodInfo，这是运算符去糖的固有反射面；fixture 内以 `GetMethod("op_Addition")==null`、`GetMethod("Add")!=null` 钉死新契约 |
| 12 | ++/-- 与其它通道边界 | `op_Increment`/`op_Decrement` 声明与全部 `++`/`--` 站点**原样保留**，CS2J5181 Info 计数，残余闸门白名单放行，归 **C17 自增族**（catalog 把 C17 登记在 numeric-boundary pass）；物理归属（声明重写天然属于 member-desugar 规则目录）列为开放问题（§11/附录 4）。enum 运算归 E04/C05；无符号窄类型运算归 E09；decimal 二元/一元归 C34；checked 表达式/块归 C15；事件 += 归 C13；属性/索引器复合归 C11/C12 | 一元 fail-fast [UnaryExpressionTransformer.cs:141-144](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/UnaryExpressionTransformer.cs#L141-L144) 的 `++/--` 分支仍由 C17 消除 |
| 13 | 支撑库 | **无**：不注入 C# 支撑工程，不新建 Java compat 类 | 目标形态全是 BCL 合法 C#（提升展开只用 `.HasValue`/`.Value`/条件表达式）；无注入/过滤/指纹豁免成本 |
| 14 | attribute / 修饰符 | 声明上的 attribute 整体平移到方法（运算符声明只有 method/return 目标，不涉及 C11 PD-10 的 field/property 路由）；`public static` 等修饰符平移；checked 关键字转为命名后缀而非修饰符；XML doc/trivia 跟随 | C10 attribute-normalize 落地后改为消费其归一标记，规则幂等（两套输入单测） |
| 15 | 执行形态 | 复用 C11 三阶段（分析→计划→逐文件重写）+ 整树重编译 + plan 级原子回退；声明重写与调用点重写在同一 plan 内分两步；规则经 `[MemberDesugarRule(Kind=Operator, Order=40)]` 自动发现，仅新增 `Operators/` 目录文件 | 不新增 pass/适配器；plan 按运算符符号粒度（每个源内运算符符号一 plan，转换运算符同），TouchedFiles = 声明文件 + 全部站点文件 |

---

## 3. 链位与族契约

### 3.1 归属与前置

- **唯一 pass 归属**：`member-desugar`（[PassStubCatalog.cs:191-200](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassStubCatalog.cs#L191-L200)，Stage=`Declarative`，中间目录 `.cs2j-member-desugar-src`，RunAfter `{ struct-value-semantics, closure-lift }`，catalogItems `"C11, C12, C13, C14, C27, C39, C46"`）。C14 不新增 pass id、不新增中间目录、不新增 RunAfter 边。
- **硬前置：C11 锚点合入**。本任务分支从 `f68a783c` 切出后，**实现前先合入 C11 分支**（§12）；C11 提供共享符号表、plan/回退骨架、`IMemberDesugarRule` 发现机制、共享 `MemberDesugarSyntaxFacts`（表达式树/dynamic 判定、临时提升、RewriteAnnotation、FormatAndValidate）与族适配器。C14 的开发与验收全部基于 C11 合入后的代码形态。
- 与 C12/C13 为**同 pass 内并列规则**，目录物理隔离（§4.1），各自合入只追加自有目录与测试。
- 语义顺序依据 catalog：C14「须晚于 E09（无符号扩展方法已接管数值运算）与 C05（enum 运算已被 E04 归并）」（[preprocessor-catalog.md:428](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L428-L428)）——两者均为链上已实现的早序 pass，member-desugar 天然在后；符号谓词（MethodKind + HasSourceDeclaration）保证即使同树共处也零误伤。

### 3.2 本分支的注册面零改动承诺

C14 分支**不修改**以下文件：

| 文件 | 承诺 |
|---|---|
| [PassCatalog.cs](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassCatalog.cs) | 零改动（描述符由 C11 翻转；C14 不新增条目） |
| [PassChainFactory.cs](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassChainFactory.cs) / [CliPreprocessChain.cs](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/CliPreprocessChain.cs) | 零改动（无 options 工厂、无 switch 分支） |
| [Program.cs](file:///d:/code/cs2j/src/CSharpToJava.CLI/Program.cs) | 零改动（开关、overrides 装载、目录/指纹均由 C11 接线） |
| [Adapters/](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/Adapters)（含 C11 新增的 MemberDesugarPassAdapter） | 零改动（规则程序集自动发现） |
| PassChainOrderTests / PassStubCatalogTests / ProjectPreprocessorsConsistentTests | 零改动（同 pass 内规则不进注册表、不改链序与 stub 数） |

> 与 [C11 设计 §3.3-10](file:///d:/code/cs2j/docs/superpowers/specs/2026-09-28-c11-property-desugar-design.md#L108-L108)「CatalogItems 后续追加 C14」的差异说明：本任务按授权约束采取**更严格口径**——`CatalogItems` 字符串的 `C14` 追加不由 C14 分支执行，移交族锚点（C11 owner）在合入协调时单行追加（或在 C12/C13/C14 全部就绪后统一改为 `"C11, C12, C13, C14"`）；本分支对 PassCatalog.cs 保持字节级零改动。

### 3.3 默认开关

无独立开关：`--member-desugar`（C11 注册，默认 false）/ 通用 `--enable-pass member-desugar` 即同时启用 C11+C14 规则。C14 不定义自身默认开启动作；族 pass 何时翻默认开由 C11 §8 门槛统管，C14 规则在该门槛下提供自己的验收项（§8）。

---

## 4. Core 组件设计

### 4.1 目录与文件清单（C14 独占 `Operators/`）

```
src\CSharpToJava.Core\MemberDesugar\            # C11 锚点创建；C14 对根目录只读
  Operators\                                    # 【C14 独占新增】
    OperatorRule.cs                             # [MemberDesugarRule(Kind.Operator, Order=40)]
                                                #   IMemberDesugarRule：驱动分析/计划，无注册热文件
    OperatorPlan.cs                             # OperatorPlan : MemberDesugarPlan（每运算符符号一 plan，
                                                #   Kind=Operator；携带 Outcome/FailCode/TouchedFiles
                                                #   与 plan 多态重写入口，声明+站点两步）
    OperatorAnalyzer.cs                         # 枚举源内运算符/转换声明符号 + 全树站点分类（见 §4.3）
    OperatorFacts.cs                            # 谓词：IsGateType（声明 op_True/op_False）、
                                                #   IsSourceOperator、IsIncrementDecrement、
                                                #   IsInterfaceStaticAbstract、checked 上下文
    OperatorNames.cs                            # ConvertOperatorName 表（决策 1/1b，与 OpSymbolToJavaName
                                                #   逐行对齐）+ 转换命名镜像表（决策 3）+ OperatorMemberNaming
                                                #   预留/碰撞升级（决策 8）
    OperatorDeclarationRewriter.cs              # OD-1/OD-2：operator/ConversionOperator 声明 → 静态方法
    OperatorCallSiteRewriter.cs                 # OC-1~OC-7：二元/一元/复合/转换站点显式化
    ConversionResolver.cs                       # IConversionOperation/ICompoundAssignmentOperation 解析：
                                                #   绑定方法、提升标记、嵌套分层、残留外层 cast 判定；
                                                #   输出 ConversionSiteRewrite（含 lifted 展开公式选择）
    LiftedOperatorExpansion.cs                  # 决策 5 六类提升公式的语法构造（三目/HasValue 展开）
    OperatorDiagnostics.cs                      # CS2J5170-5189 码位常量与消息
  Properties\  Indexers\  Events\               # C14 不碰（Properties 为 C11 所有）
```

规则实现对 C11 共享组件的依赖全部为**同程序集 internal 只读引用**：`MemberDesugarAnalyzer` 的成员/站点索引、`MemberDesugarPlanBuilder`、`MemberDesugarSyntaxFacts`（表达式树包含、dynamic、RewriteAnnotation、临时提升、FormatAndValidate）、`MemberDesugarModels`（Options/Statistics/Diagnostic/Severity/FileResult）。若接缝不足（见 §4.6），按 C11 规定走「锚点扩共享层 + property 回归」流程，不在 Operators/ 复制共享逻辑。

### 4.2 复用的共享契约（C11 已定，C14 实现同形）

- `OperatorRule.Analyze(MemberDesugarAnalysisContext, MemberDesugarPlanBuilder)`：在共享 Analyzer 一次 BindCompilation 的索引上追加 operator plan；规则由程序集扫描发现、`Order=40`（Property=10 之后；Indexer/Event 预留 20/30），单测断言规则集合与顺序。
- `OperatorPlan` 复用基类字段 `Id`/`Kind`/`Symbol`/`Outcome`/`TouchedFiles`/`FailCode`/`FailReason`/`FailLocation`/`OwnsTouch(file,typeName,memberName)`；plan Id 确定性 = `类型完全限定名 :: 元数据名 :: 签名源序`。
- 统计经共享 Statistics 的**按规则计数袋**输出（假设 C11 共享 Statistics 提供 `RuleCounters`/SkippedByReason 通用槽；若 C11 落地版只有具体属性，由 §4.6 接缝流程补一个通用袋，而非在 C14 分叉统计类型）。
- 重写产物挂 `MemberDesugarSyntaxFacts.RewriteAnnotation`，过共享 `FormatAndValidate` 三候选快筛，交付以整树重编译为准；回退迭代（原件重放→Bind→ExcludePlans→逐文件 RewriteFile→重建编译→DiffErrors→归属→回退 TouchedFiles，上限 1024 轮）完全复用族适配器。

### 4.3 OperatorAnalyzer：站点分类（与 JK 不重叠的保证）

分析期对编译中**源内声明**的每个 `IMethodSymbol`（MethodKind.UserDefinedOperator / Conversion）建声明索引，并经 `GetOperation` 全树枚举站点，按下表分类。分类第一判据永远是**实际绑定符号**，第二判据才是语法形态：

| 站点类 | 语法/operation 形态 | 绑定符号 | 归属 |
|---|---|---|---|
| SB-Binary | BinaryExpression（非 `&&`/`\|\|`）的 IBinaryOperation | UserDefinedOperator（源内、非 inc/dec/true/false） | C14 |
| SB-Unary | Prefix/PostfixUnary 的 `- + ! ~` | op_UnaryNegation/Plus/LogicalNot/OnesComplement | C14 |
| SB-Compound | ICompoundAssignmentOperation（`+= -= *= …`） | UserDefinedOperator + 回转转换 | C14（属性目标见 OC-8，事件归 C13，索引器未决 CS2J5179） |
| SB-Conversion | 一切 IConversionOperation.IsUserDefined（cast/赋值/return/实参/分支归并/初始化器/ctor 初始化器…） | MethodKind.Conversion 源内声明 | C14 |
| SB-Lifted | 上述 operation 的 IsLifted/Conversion.IsNullable 形态 | 底层源内方法 | C14（决策 5 展开） |
| SB-GateAndOr | `&`/`\|`（含提升）且运算符所属类型声明 op_True/op_False | op_BitwiseAnd/Or | **保留**（CS2J5180，决策 7④） |
| SB-JK-Bool | if/while/do/for/`?:`/when/`&&`/`\|\|` 条件 | op_True/op_False（或 JK 三值展开涉及的门控方法） | **JK**（C14 不索引） |
| SB-IncDec | `++`/`--` | op_Increment/op_Decrement | **C17**（CS2J5181 白名单） |
| SB-Metadata | 任意形态但 MethodSymbol 无源码声明 | BCL/compat/外部 | 不动（CS2J5182） |
| SB-InterfaceStatic | 接口 static abstract/virtual 运算符及经约束解析站点 | 接口运算符 | CS2J5175 跳过 |
| SB-Tree / SB-Dynamic | 表达式树 lambda 内 / dynamic 绑定 | — | CS2J5173 / CS2J5174 跳过 |

重叠消解硬规则：当一个条件表达式到 bool 的绑定是 `op_Implicit`/`op_Explicit`（非 op_True）时，站点落 SB-Conversion 不落 SB-JK-Bool（JK-6g 同义）；`!x` 即使出现在布尔条件内也只落 SB-Unary（JK-6h）；裸 `&&`/`||` 一律 SB-JK-Bool（含 T 结果位置）。分析器对每个站点记录所属 plan 与精确重写配方（方法符号、限定名、提升公式、残留 cast、临时提升需求），供 CallSiteRewriter 纯执行——**重写期不再做任何绑定推断**。

### 4.4 规则全集（OD/OC-x）

| 规则 | 内容 | 要点/边界 |
|---|---|---|
| OD-1 算术/比较/位/一元声明归约 | `operator` 声明 → `MethodDeclarationSyntax`：名出 `ConvertOperatorName`（含碰撞升级）、形参表/返回类型/修饰符/attribute/XML doc/体（块体或表达式体）平移；checked 关键字 → `Checked` 后缀 | 形态参照 JK-5 的声明替换构造（[OperatorTrueFalseRewriter.cs:54-65](file:///d:/code/cs2j/src/CSharpToJava.Core/JavaKeywords/OperatorTrueFalseRewriter.cs#L54-L65)）但不共享代码；abstract/extern 运算符在 C# 中不合法（运算符必须有体），无空体形态 |
| OD-2 转换声明归约 | ConversionOperatorDeclaration → 静态方法 `ToR`/`ToRExplicit`（决策 3）；镜像表外目标 → plan Infeasible CS2J5177（声明与站点同不动） | implicit/explicit 关键字不向方法传递（编译期约束放宽同 C11 init 思路，运行行为由显式调用点保住） |
| OC-1 二元显式化 | `a op b` → `Qual.Add(a, b)`；限定名最小化 | 转换器今发名交叉核对；原语运算不命中 |
| OC-2 一元显式化 | `-a`/`+a`/`!a`/`~a` → `Qual.Negate(a)` 等 | 消除 [UnaryExpressionTransformer.cs:141-144](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/UnaryExpressionTransformer.cs#L141-L144) 的 fail-fast 面 |
| OC-3 复合赋值 | 决策 2b：左值一次求值 + 回转转换链 | 临时提升；不可提升上下文 CS2J5179 |
| OC-4 显式转换站点 | cast 绑定用户转换 → 方法调用 + 必要外层标准 cast | 消灭 [TypeOperationTransformer.cs:84](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/TypeOperationTransformer.cs#L84-L84) 静默错误路径的输入来源 |
| OC-5 隐式转换站点 | 决策 4 的全部无形站点经 IConversionOperation 显式化 | 这是 C14 最大站点面；只替换用户转换层 |
| OC-6 提升展开 | 决策 5 六类公式 | .NET 双端逐值断言 |
| OC-7 跳过闸门 | 表达式树/dynamic/接口静态抽象/不可重建链/镜像表外/不可提升 → plan 原子跳过且必留诊断 | 禁止只改声明不改站点 |
| OC-8 与属性/索引器复合的协作 | 属性目标的 `p.X += v`：C11 属性复合重写（PD-6，set/get 包裹）负责外层，**内层运算符应用**经 C14 暴露的 internal 共享渲染器（「给定运算符符号 + 运算元 syntax → 显式调用 syntax」纯函数，供 Properties/Indexers 同程序集直接调用，不修改 C11 文件）按 SB-Compound 配方生成；C11 rule Order=10 的合成节点由 OperatorRule(Order=40) 在同文件同轮重写中消费，族迭代回退循环兜底跨轮收敛 | C12 未合入时索引器目标复合 → CS2J5179；事件复合不命中本规则。需新增 plan 基类虚方法/共享渲染入口时走 §4.6 接缝流程 |
| OC-9 残余对账 | 输出树中每个源内运算符/转换声明必须属于 Planned / Skipped(有诊断) / JK(op_True/False) / C17(op_Inc/Dec) / GateAndOr 五类之一；每个绑定源内运算符符号的站点语法不得残留（被 C14 拥有的类） | strict 下残余 → CS2J5183 Error（§7.5） |

### 4.5 两阶段重写顺序

单文件内取命中本文件的 active operator plans：先 **OD 声明重写**（成员位置保持，方法落在原运算符声明位置；同类型多运算符按源序），再 **OC 站点重写**（按 SB 配方，内层先于外层的自底向上顺序由 CSharpSyntaxRewriter 默认遍历保证）；OC-8 的属性/索引器合成内层在同一轮链中完成。重写后过 FormatAndValidate；plan 失败按 TouchedFiles 整文件重放。

### 4.6 接缝不足时的处理（对 C11 共享层的可能回写）

只允许三类锚点层扩展，且必须带 C11 property 回归、由 C11 owner 评审合入（C14 分支不直接改）：①plan 基类增加一个多态重写挂点（若 C11 落地版不是 plan 多态派发）；②共享 Statistics 增加按规则计数袋；③共享 Options 的 `rules.operators` 节支持（skip/force 载荷：`Ns.Type::op_Addition`、`Ns.Type::conversion(R)`）。这些在 C11 设计 §11「共享组件缺口先扩共享层」中已有授权路径。

---

## 5. CLI 集成（无新适配器）

- 统计/诊断全部汇入 C11 族适配器 [MemberDesugarPassAdapter.cs](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/Adapters)（C11 新增）的既有通道：C14 计数器经共享按规则计数袋出现在族摘要（建议族摘要追加段：`operators: O planned, B binary/U unary/C compound/V conversion site(s), L lifted, G gate-retained, D deferred(++/--), A skipped, R rolled back`）；诊断 MSBuild 格式、Info 仅 verbose、restore/build/test 失败映射复用 C11 的 CS2J5102/5103/5104。
- verify：沿用族描述符双默认开（verifyBuild/verifyTests=true，C11 翻转时设定），.NET 半区守门；无支撑库、无 AfterFilesAsync、无指纹豁免。
- 入口：`--member-desugar` / `--enable-pass member-desugar` / 独立 `preprocess --pass member-desugar`；单文件模式受族 CS2J5109 闸门约束（运算符 plan 几乎必然跨文件）。
- overrides：`config/MemberDesugarOverrides.json` 的 `rules.operators` 节（v1：`skip`（按 plan Id/类型）、`force`（仅解除 CS2J5173 表达式树项））；文件可选、解析失败 Warning 按空集继续。
- **本分支 CLI 代码零改动**（§3.2）。

### 5.1 Java 半区消费清单（转换器后续 PR 的输入，本任务不实施）

默认开门禁项（在 C11 §8 总门槛下逐项核实）：

1. 命名零漂移主路径：对决策 1 全表逐名断言「去糖 C# 方法名经 [MethodTransformer.JavaName](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Member/MethodTransformer.cs#L178-L187) 派生 == [OpSymbolToJavaName](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Member/OperatorTransformer.cs#L23-L51) 今发值」；checked 后缀同理；
2. 转换方法名镜像表（决策 3）与 [TransformConversion](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Member/OperatorTransformer.cs#L181-L246) 今发名逐别名核对；碰撞路径 `*Operator` 后缀的 Java 收口策略（转换器冲突解决现状见 [OperatorMethodNameCollisionTests](file:///d:/code/cs2j/tests/CSharpToJava.Tests/OperatorMethodNameCollisionTests.cs)）需 triage；
3. OperatorTransformer/TransformConversion 在预处理路径上成为不可达分支（输入已无 operator/ConversionOperatorDeclarationSyntax），其删除/保留为后续 PR；直连 ConversionPipeline 单文件入口继续以其为安全网，相关测试一行不改；
4. [UnaryExpressionTransformer.cs:141-144](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/UnaryExpressionTransformer.cs#L141-L144) 用户一元 fail-fast 与 [TypeOperationTransformer.cs:53-86](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/TypeOperationTransformer.cs#L53-L86) 用户转换 cast 兜底在去糖树上证实不可达；
5. enum（[BinaryExpressionTransformer.cs:135-145](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/BinaryExpressionTransformer.cs#L135-L145)）/decimal 拒绝面归属不变（E04/E09/C34）；元数据-only 转换（ReadOnlyMemory 构造函数路径）零回归；
6. 与 JK 组合：门控类型 `&`/`||` 经 JK-6d/e 合成、算术经 C14 显式化的混合产物可编译 Java。

---

## 6. 诊断策略

C14 使用 C11 §4.5 预留的 **CS2J5170-5189** 码段（共享 CS2J5100-5109 回退/verify 族码直接复用，不另立）。MSBuild 格式：`文件(行,列): warning/error CS2J51xx: [Ns.Type::op_Xxx] 描述`。

| 码 | 级别 | 含义 |
|---|---|---|
| CS2J5170 | Info | 运算符 plan 建立（元数据名/去糖名/站点分类计数），仅 verbose |
| CS2J5171 | Info | 转换运算符 plan 建立（implicit/explicit、目标类型、去糖名），仅 verbose |
| CS2J5172 | Info | 碰撞消歧（基名 → 实际生成名与升级序号），仅 verbose |
| CS2J5173 | Warning（strict Error） | 运算符/转换站点位于表达式树 lambda 内，plan 原子跳过；overrides.force 可解除本项 |
| CS2J5174 | Warning（strict Error） | dynamic 绑定站点无法确定运算符方法，plan 跳过（force 不解除） |
| CS2J5175 | Warning（strict Error） | 接口 static abstract/virtual 运算符（C# 11 generic math），plan 跳过 |
| CS2J5176 | Warning（strict Error） | 提升/链式转换无法从 operation 重建（底层方法符号缺失等），plan 跳过 |
| CS2J5177 | Warning（strict Error） | 转换目标类型在命名镜像表外（泛型实例化/tuple/指针等），plan 跳过；sdk triage 后逐类收编 |
| CS2J5178 | Warning（strict Error） | 生成名预留升级耗尽（理论兜底），plan 跳过 |
| CS2J5179 | Warning（strict Error） | 复合赋值目标 v1 不支持（C12 前的索引器目标、不可临时提升上下文），plan 跳过 |
| CS2J5180 | Info | 门控类型（声明 op_True/op_False）的 `&`/`|` 运算符保留，供 JK-6d/e 合成绑定 |
| CS2J5181 | Info | op_Increment/op_Decrement 声明与站点移交 C17（残余白名单计数） |
| CS2J5182 | Info | 元数据-only 运算符站点未改写（BCL/compat 既有路径） |
| CS2J5183 | Warning（strict Error） | residual 闸门：输出中存在无 plan/无诊断归属的源内运算符声明或绑定站点（OC-9） |
| CS2J5184-5189 | — | 预留（C17 移交细化、checked 对等未来计数如需） |

原则同 C11 §6：不可判定即显式诊断、plan 整体不动；跳过必留痕且声明/站点同生同灭；Options.Strict 默认 true，Warning 升级 Error；全部跳过按 SkippedByReason 分桶进统计与摘要，供 csharp-sdk 基线盘点。

---

## 7. 测试计划

### 7.1 Core 单测矩阵

新增 `tests/CSharpToJava.ProjectTests/MemberDesugarOperatorUnitTests.cs`（仿 MemberDesugarPropertyUnitTests/JavaKeyword 单测：内联源码 → CSharpCompilation → Bind/RewriteFile → 忽略空白断言 + AssertOutputCompiles）：

1. **算术**：`+ - * / %` 二元（含不对称操作数 `*(double,Point)`/`*(Point,double)`、点积返回异类类型多重载）；static 接收；限定名/global:: 接收；跨文件；运算符体内原语运算保持原语运算符（对照 [OperatorMethodNameCollisionTests:350-404](file:///d:/code/cs2j/tests/CSharpToJava.Tests/OperatorMethodNameCollisionTests.cs#L350-L404)）。
2. **比较/相等**：`== != > < >= <=` 成对声明；结果 bool；与 `Objects.equals` 转换器路径无关（C# 半区只断言 ValueEquals/NotEquals 调用）。
3. **位/移位**：`& | ^ ~ << >> >>>`；`And/Or/Xor` 标识符合法性；门控类型 `&`/`|` 保留（CS2J5180）而 `^`/算术去糖。
4. **一元**：`-x +x !x ~x` → Negate/Plus/Not/OnesComplement；与二元 `-`/`+` 同型共存；`!x` 在 if 条件内只做方法化、不被 JK 逻辑包裹（C14 单独运行视角）。
5. **复合赋值**：变量/字段/static/属性目标；`+= -= *= /=`；回转绑定用户转换的复合（`x += v` 回转型为声明类型）；副作用接收者只求值一次；不可提升上下文 → CS2J5179；事件 `+=` 不命中；索引器目标按 C12 是否合入两态断言。
6. **implicit/explicit**：声明 → `ToR`/`ToRExplicit`；显式 cast 站点；隐式站点全集（赋值、变量/字段初始化器、return、实参、`?:` 归并、switch 表达式臂、`??`、集合初始化器元素、base ctor 初始化器）；嵌套「标准转换 ⊃ 用户转换 ⊃ 标准转换」只换中层且残留必要 cast；implicit/explicit 同 (S,T) 对共存探针（M1 钉死合法性与命名）。
7. **链式/提升**：`S?→T?` 提升转换；提升一元；提升算术；提升关系（`bool?` 结果）；提升 `==`/`!=` 三值表；提升复合赋值；双 null/单 null/双值逐值与未重写基准行为一致；非提升直链不被误判；无法重建的人造残缺模型 → CS2J5176。
8. **泛型操作数**：`struct Vec<T>` 的运算符（签名含类类型形参）去糖后普通静态泛型方法 + 站点 `Vec<int>.Add`；泛型转换目标 → CS2J5177。
9. **checked 运算符**：同型双声明 `operator +`/`checked operator +` → Add/AddChecked；checked/unchecked 上下文站点各绑其名；checked 块内复合。
10. **碰撞**：`operator +` 与手写 `Add(T,T)` 共存 → 生成 `AddOperator`、用户 `Add` 零改动且各自站点正确；二级升级；转换方法同名碰撞。
11. **跳过族**：表达式树内二元/转换站点 → CS2J5173 且声明站点全不动；force 解除；dynamic → CS2J5174；接口 static abstract 运算符 → CS2J5175；镜像表外转换 → CS2J5177；inc/dec 原样（CS2J5181）。
12. **边界零误伤**：enum/decimal/内建数值站点不动；元数据-only（ReadOnlyMemory 隐式转换）不动；属性/事件/索引器声明与非运算符站点不动（C11/C12/C13 边界）；op_True/op_False 声明与 `if(x)`（绑 op_True）、`x && y` 不动（JK 边界），但同类型 `(bool)x`（绑 op_Implicit）改写、`if(x)` 绑 op_Implicit(bool) 时改写为 ToBoolean 包裹。
13. **JK 组合**：同一中间树连续应用 member-desugar 与 java-keywords（直接复用 JK 预处理器对 C14 产物再跑一轮）：SqlBoolean 型类型断言算术已显式化、`&`/`||` 经 JK 展开且可编译、op_True 声明终成 IsTrue。
14. **工程性质**：幂等（二次 RewriteFile 零 diff）；干净文件字节级一致 + BOM；record/struct/class/nested type 中的运算符；partial 类型多部件（运算符符号天然合并）。

### 7.2 项目级 fixture（在 C11 fixture 上追加，不新建 sln）

在 C11 新增的 `tests/SampleMemberDesugarSolution/`（SampleMemberDesugarLib/App/Tests）内**追加文件**（不改动 C11 既有 fixture 源）：

- Lib：`Operators/` 群——算术值类型（Point/Vector，含多重载、checked 对、泛型 `Vec<T>`）、比较/相等类型、位标志类型、三值门控类型（op_True/op_False + `&`/`|` + 算术，JK 组合用）、转换类型群（Temperature↔double 隐式/显式、提升用 `Snowflake?` 链、与 bool 隐式转换类型）；一个含表达式树引用运算符的类型（跳过断言）；一个 `Add` 与运算符共存的碰撞类型。
- App：跨文件二元/复合/隐式转换实参与 return/初始化器站点；门控类型的 if/`&&` 站点（留给 JK）。
- Tests（.NET 半区守门）：去糖后用 xunit 断言算术/比较/复合求值、提升三值表（双 null/单 null/双值）、转换链结果、副作用接收者只求值一次——去糖错误在 .NET 侧直接红。
- `MemberDesugarProjectTests`（C11 建）追加 C14 断言：`result.Success`、C14 计数、中间树文本（含 `Add(`/`ToR(` 显式调用、不含非跳过 OperatorDeclarationSyntax/ConversionOperatorDeclarationSyntax；门控 `operator &` 保留）、中间树 `dotnet build`/`dotnet test -c Release` 退出码 0；**叠加启用 java-keywords** 的组合中间树 build/test 0。

### 7.3 Release build/test

- `dotnet build -c Release`（CSharpToJavaConverter.slnx 全绿）；
- `dotnet test -c Release` 三个工程：CSharpToJava.Tests、CSharpToJava.ProjectTests、CSharpToJava.ConversionTests。

### 7.4 反射契约抽样

fixture 内断言去糖后 `typeof(Point).GetMethod("op_Addition")` 返回 null、`GetMethod("Add")` 非 null；转换 `GetMethod("op_Implicit")` 为 null、`GetMethod("ToDouble")` 非 null——把新契约钉死为预期（[catalog §4 风险 7](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L926-L926)，同 E17「反射不保证」）。

### 7.5 residual 闸门（operator 计数）

1. 扫描型测试：输出树每个源内 `OperatorDeclarationSyntax`/`ConversionOperatorDeclarationSyntax` 必须落入 Planned / Skipped(CS2J51xx) / JK(op_True/op_False) / C17(op_Increment/op_Decrement) / GateAndOr 五类；每个「绑定源内 C14 所属符号」的站点语法不得残留——strict 下 CS2J5183 非零即失败。
2. 跳过反例集：CS2J5173-5179 每类断言 plan 原子性（声明与站点同不动）；CS2J5100 全量回退路径演练。
3. 既有 fixture 回归：SampleProject、SampleJavaKeywordSolution（**JK 组合重点**）、SampleRecordSolution、SampleRefOutInSolution 等启用 member-desugar 后中间树 build/test 全绿；上游各 pass（E04/E09/E11/E13 等）统计零变化。

### 7.6 CLI / 一致性 / 红灯对照

- 无独立开关面：`--enable-pass member-desugar` 与 `--member-desugar` 行为一致；启用后摘要含 operators 段、`--list-passes` 不出现新 id；本分支不引入任何链序/stub 数变化（相关测试零改动即绿）。
- **与现有转换器测试红灯对照**：不改 [OperatorMethodNameCollisionTests](file:///d:/code/cs2j/tests/CSharpToJava.Tests/OperatorMethodNameCollisionTests.cs)/[UserDefinedConversionGenericTargetTests](file:///d:/code/cs2j/tests/CSharpToJava.Tests/UserDefinedConversionGenericTargetTests.cs) 一行（直连单文件管线安全网）；为每个用例在 §7.1/§7.2 建 C# 半区对应断言，形成「Java 侧行为 ↔ C# 侧原生形态」对照表，作为 §5.1 Java 消费 PR 与族默认开门禁核对底稿。

---

## 8. 默认开启验收门槛

C14 不持有 pass 默认开关（族开关归 C11），但族 pass 翻 `defaultEnabled: true` 前 C14 范围必须同时满足：

1. slnx `dotnet build -c Release` 全绿；三个测试工程 `dotnet test -c Release` 全绿（含 §7 全部新增项，C11 既有测试零回归）。
2. fixture sln 中间树 build + test 全绿；**member-desugar × java-keywords 组合**中间树 build + test 全绿；幂等、字节复制、residual 闸门（CS2J5183）、CS2J5100/5101 回退演练通过。
3. **Java 半区就绪**：§5.1 六项逐项核实——决策 1 全表与 checked 后缀零漂移、转换镜像表逐别名核对、碰撞路径收口、Unary/Cast 两处 fail-fast/静默面在去糖树证实不可达、元数据-only 与 enum/decimal 零回归、JK 组合 Java 产物可编译且双端测试通过；OperatorTransformer 在预处理路径上证实不可达（删除留后续 PR）。
4. csharp-sdk（只读训练集）启用扫描：源内运算符/转换总量、Planned/SkippedByReason（CS2J5173-5179）分布、CS2J5180/5181/5182 计数、镜像表外转换目标类型清单；**每条 language-infeasible 命中人工定性**（真不可行 vs 分析器过保守），过保守项修到清零或进入明确里程碑；接口 static abstract 运算符（generic math）命中率决定 CS2J5175 后续优先级。
5. sdk 基线不退化：启用后各既有 fixture 与 sdk 抽样工程中间树 build/test 不劣于未启用；上游 pass 统计零变化；组合启用 JK 时无新增回退。
6. overrides（`rules.operators` skip/force）、限制声明（反射 op_* 契约）、C11 文档中族成员状态更新齐备。

---

## 9. 里程碑

| 里程碑 | 内容 | 预估规模 |
|---|---|---|
| M0 前置与探针 | C11 分支合入；探针单测钉死：op_* 保留名手写诊断、implicit/explicit 同对共存规则；§4.6 接缝缺口盘点并回提 C11 owner | 调研/小补丁 |
| M1 声明与直站点 | OperatorNames（ConvertOperatorName + 镜像表 + 碰撞升级）、OD-1/OD-2、OC-1/OC-2、OperatorPlan/Rule 接线；§7.1-1/2/3/4/10/14 单测 | ~500 行 |
| M2 转换链（重点） | ConversionResolver + OC-4/OC-5：IConversionOperation 全站点形态、嵌套分层、残留 cast；显式 cast 静默面关闭；§7.1-6 单测 | ~550 行 |
| M3 复合与提升（难点） | OC-3/OC-6/OC-8：复合回转链、临时提升、六类 lifted 展开公式、属性目标协作；checked 对；§7.1-5/7/9 单测；fixture 追加 + .NET 行为测试 | ~600 行 + fixture |
| M4 闸门与组合 | OC-7/OC-9：表达式树/dynamic/接口静态抽象/镜像表外/不可重建跳过族、CS2J5183 residual 闸门、门控保留与 **JK 组合**全链路、sdk 扫描报表、红灯对照表归档；Release build/test 全绿 | ~400 行 + 数据 |

---

## 10. 风险

1. **表达式树/dynamic 共同禁区**（[catalog §4 风险 8](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L927-L927)）：转换/二元节点在 `Expression<T>` 内改写直接改变树形态且声明改名使 lambda 不可编译。缓解：plan 级原子跳过（CS2J5173）+ 复用 C11 共享判定工具 + force 仅此一项；与 JK-8 同判据同口径。
2. **反射/字符串契约变化**（[catalog §4 风险 7](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L926-L926)）：op_*/op_Implicit 元数据名消失。缓解：fixture 钉死新契约、沿用 E17 限制声明与 overrides；无 nameof 面。
3. **Lowering 误用**（[catalog §4 风险 9](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L928-L928)）：[LowerOperator.cs](file:///d:/code/cs2j/src/CSharpToJava.Core/Lowering/LowerOperator.cs) IR 不同、生产不可达。缓解：只借形态，Operators/ 零 using Lowering，评审查 import。
4. **提升语义错误**：lifted 关系结果是 `bool?`、提升相等空值规则、门控 `&`/`|` 三值逻辑都反直觉，公式写错会「能编译、行为错」。缓解：公式在决策 5 锁定、.NET xunit 与未重写基准双 null/单 null/双值逐值比对；不可重建即 CS2J5176 跳过，宁假阳不漏改。
5. **隐式转换站点遗漏**：隐式用户转换无语法节点，靠语法遍历必然漏；漏网后声明已改名会在整树重编译暴露为错误 → 可能触发大面积 CS2J5100 回退。缓解：以 GetOperation 全树枚举为唯一站点来源；OC-9 反向 residual 扫描（输出树不得再存在绑定已去糖方法名的隐式转换错误形态）；M2 起以 sdk 抽样验证站点枚举完备率。
6. **与 JK 的组合耦合**（决策 7④）：门控类型 `&`/`|` 若被 C14 方法化，JK-6d/e 后续合成节点无法绑定。缓解：v1 门控保留 + CS2J5180 计数 + 组合中间树硬测试；长期若改为 JK 合成显式调用需 JK 侧跟进，列开放问题。
7. **转换命名与 Java 映射的耦合/漂移**：镜像表外目标（泛型等）v1 跳过可能在 sdk 高命中；碰撞后缀路径 Java 名与现状不完全同形。缓解：sdk triage 决定收编顺序；零漂移主路径有逐名交叉测试；收口列入 §8-3 硬门禁，不碰巧放行。
8. **C12/C17 跨族目标耦合**：索引器复合在 C12 前只能跳过；`++/--` 声明归 C17 但声明重写天然在 member-desugar 目录。缓解：CS2J5179/CS2J5181 显式计数与白名单；物理归属作为开放问题在 C17 立项时拍板。
9. **checked 上下文判定**：站点绑错 checked/unchecked 版本会改变溢出行为。缓解：只信 IBinaryOperation.IsChecked 与 operation 绑定的 MethodSymbol，不读语法上下文推断；M3 专项测试。
10. **整树迭代成本**：同 C11/JK（上限 1024 轮）。缓解：分析期一次算尽 plan/TouchedFiles/重写配方，稳态一轮收敛；保留轮次与耗时统计。

---

## 11. 并行协调

- **C11 锚点（硬前置）**：C14 分支实现前先合入 C11；只增 `MemberDesugar/Operators/` 与自有测试、fixture 追加文件；不碰 `MemberDesugar/` 根目录 9 个共享文件与适配器（[C11 §4.5 所有权表](file:///d:/code/cs2j/docs/superpowers/specs/2026-09-28-c11-property-desugar-design.md#L199-L212)）；接缝缺口走 §4.6 授权流程（锚点扩层 + property 回归），不在规则目录复制共享逻辑。
- **C12 Indexer / C13 Event（同 pass 并行规则）**：三目录并列、规则 Order 10/20/30/40 确定性排序、自动发现；C14 不碰 `Indexers/`/`Events/`。交互点仅两处：①复合赋值目标分类（属性→C11、索引器→C12、事件→C13、运算符应用→C14，OC-8 渲染器共享）；②CatalogItems 单行追加统一由锚点侧收口（§3.2）。
- **java-keywords（已实现，C08/E17）**：边界见决策 7——op_True/op_False 全部归 JK；C14 在前去糖其余 op_*，JK 在最后只按 op_True/op_False 绑定动作；门控 `&`/`|` 保留是两通道组合启用的正确性条件；`(bool)x`/隐式 bool 转换归 C14（JK-6g），`!x` 归 C14（JK-6h）。两通道组合中间树列为 C14 必测项（JK 默认关期间同样要测，防其翻转时破裂）。
- **C15 numeric-boundary（checked/unchecked）**：C14 负责 checked 用户运算符的声明/站点（AddChecked 对齐）；checked 表达式/块对内建数值的归一归 C15；两规则若未来同树共处，以绑定符号（用户 vs 内建）切分，无语法面争抢。
- **C17 自增族（numeric-boundary）**：op_Increment/op_Decrement 声明与 `++/--` 站点整体移交，CS2J5181 计数、残余白名单；开放问题：该规则物理上落 `Operators/`（随 member-desugar 交付）还是 numeric-boundary 自有声明重写——C17 立项时拍板，C14 文档先行划界并保证其声明字节不动。
- **C10 attribute-normalize**：C14 只做运算符声明 attribute 整体平移；C10 落地后消费归一标记，两套输入幂等测试。
- **E04/E09/C34（enum/无符号/decimal）**：以 MethodKind+HasSourceDeclaration 符号谓词保证不相交；既有 fixture 统计零变化是硬回归项。
- **热文件冲突解法**：
  - C14 分支的生产代码改动集 = `MemberDesugar/Operators/*.cs`（全新目录）+ 测试新文件 + fixture 追加文件；与 C11/C12/C13 无文件重叠，git 层面零冲突。
  - PassCatalog/PassChainFactory/CliPreprocessChain/Program.cs/适配器/链序测试：**本分支零改动**（§3.2）；CatalogItems 追加由锚点侧单行处理，C14 不占该冲突点。
  - 共享根文件若必须按 §4.6 回写：由 C11 owner 在锚点分支/跟进小 PR 落，property 单测守门，C14 rebase 消费。
  - fixture sln 同目录追加：各任务用独立子目录/独立文件名（`Operators/` vs `Indexers/` vs `Events/`）避免追加冲突。

---

## 12. Worktree 分配

- 分支：`feature/preproc-c14-operator-desugar`
- 目录：`.worktrees/preproc-c14-operator-desugar`
- base：`f68a783c`（`feat(preprocessing): register 23 merged-family stub passes`，C11 设计已核实存在）；**实现前在该 worktree 内合入 C11 分支**（feature/preproc-c11-property-desugar），再开始 M0
- 创建命令（在仓库根、PowerShell 下执行；**本设计阶段不执行**，原样记录）：

```powershell
git worktree add .worktrees/preproc-c14-operator-desugar -b feature/preproc-c14-operator-desugar f68a783c
```

- worktree 内交付面遵循 foundation plan「并行开发交付包模板」（[preprocessor-foundation_plan.md:108-117](file:///d:/code/cs2j/.trae/documents/preprocessor-foundation_plan.md#L108-L117)）：Core 仅新增 `MemberDesugar/Operators/`（规则 + 两阶段重写 + 诊断）、Core 单测 + 在 SampleMemberDesugarSolution 追加 operator fixture + Release build/test + residual 闸门 + JK 组合测试；不新建支撑工程；不新增/修改任何 CLI 注册、适配器、链序文件；不触转换器代码（§5.1 消费清单为后续 PR 输入）。

---

## 附：关键开放问题（供评审拍板）

1. **转换方法名与 Java 类型映射的耦合度**：v1 用固定别名镜像表（泛型等表外目标 CS2J5177 跳过），还是值得把转换器类型映射抽成 Core 共享 helper 让 C# 半区直接按 Java 名派生（覆盖面全但 C#→C# pass 引入 Java 映射依赖）？取决于 sdk 中泛型目标转换命中率。
2. **方法名碰撞的收口形态**：v1 生成方法取 `*Operator` 后缀（保用户成员不改名、保反射契约），Java 半区碰撞路径从「用户方法 Pascal」变为「运算符 addOperator」；是否接受由 Java 消费 PR 统一，还是应在 C# 半区改用户方法名（更贴现状但破坏用户反射契约）？
3. **JK 门控 `&`/`|` 的长期归属**：v1 门控类型保留运算符（JK 零改动）；长期是否改为 JK-6d/e 直接合成 `Type.And(...)` 显式调用以解除这一跨通道特判（需 JK 侧感知 member-desugar 产物）。
4. **op_Increment/op_Decrement 的物理归属**：声明重写天然属于 member-desugar 规则目录，但 catalog 把 `++/--` 划在 numeric-boundary（C17）；C17 落地时是向 `Operators/` 贡献规则还是在 numeric-boundary 内自建声明重写，需两任务 owner 提前定。
5. **`rules.operators` overrides 接缝**：C11 共享 Options/Statistics 是否原生支持按规则节与按规则计数袋；若不支持，§4.6 锚点扩层的最小形态（plan 挂点/计数袋/options 节三项）需在 M0 与 C11 owner 一次定死。
