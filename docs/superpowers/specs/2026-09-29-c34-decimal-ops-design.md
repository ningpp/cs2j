# C34 decimal 运算归一收尾（numeric-boundary 族增量规则）设计

**日期**: 2026-09-29
**范围**: 在 C15 创建的 `numeric-boundary` pass 内，以**族内增量规则**形态新增 `DecimalOps` 规则（Core `NumericBoundary/Rules/DecimalOps/` + 自有测试 + fixture 追加 + compat 方法面校验），把 C# `decimal` 上的二元算术、比较、一元负号、复合赋值、混合数值提升与常量字段归约为 BCL `System.Decimal` 显式静态方法调用，使中间树为可编译 C#、Java 半区落到 `io.github.ningpp.compat.Decimal` 显式方法面；本任务**不注册新 pass、不翻转 stub、不新增 C# 支撑工程、不改任何 C#→Java 转换代码**
**状态**: 设计稿，待评审；实施前置 = C15（锚点）分支合入；建议在 C17 之后实施（见 §3.3 规则序）
**族属**: catalog P2「数值边界族」（[preprocessor-catalog.md §3.3](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L906-L906)）。[PassStubCatalog.cs:162-171](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassStubCatalog.cs#L162-L171) 的 `numeric-boundary` stub（catalogItems `"C15, C17, C22, C34, C41"`、RunAfter=unsigned、Stage=Expression）由 C15 翻转；C34 只在其合入后向 `Rules/DecimalOps/` 增量

---

## 1. 背景与目标

### 1.1 decimal 的语言位置：内置关键字，引用类型映射

`decimal` 是 C# 内置关键字（`SpecialType.System_Decimal`），但通过既有类型映射在 Java 半区成为 compat **引用类型** `io.github.ningpp.compat.Decimal`（[TypeMappings.json:3634-3639](file:///d:/code/cs2j/config/TypeMappings.json#L3634-L3639)，csharp `System.Decimal` → java `Decimal`）。Java 没有运算符重载，`Decimal` 引用上的 `+ - * / % == != < > <= >=` 与一元 `-` 全部不是合法 Java——这与整数原语（可直映）和用户 struct（走 C14 运算符去糖）都不同，必须在 C#→C# 层把运算符站点显式化。

### 1.2 现状：不可编译 Java 静默输出 + fail-fast 并存

类型/字面量映射已就绪，但**运算层是缺口**：

- **二元路径原样透传运算符**：[BinaryExpressionTransformer.cs:88-94](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/BinaryExpressionTransformer.cs#L88-L94) 在任一操作数为 `System_Decimal` 时仅 `AddDecimalImportIfNeeded` 后 `return $"{left} {op.Java} {right}"`——产出 `a + b` 形态的**不可编译 Java**，无诊断。IR 路径同样落裸 `JavaBinaryExpression`（[L56-L61](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/BinaryExpressionTransformer.cs#L56-L61)；`BinaryOperatorResolution.IsBuiltInType` 对 SpecialType 恒真，[BinaryExpressionSupport.cs:314-320](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Utilities/BinaryExpressionSupport.cs#L314-L320)，operator-call 路径不接收 decimal）。被 [catalog §4 风险 12](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L931-L931) 点名的「静默错误输出」站点。
- **一元与自增命中即抛**：[UnaryExpressionTransformer.cs:150-151](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/UnaryExpressionTransformer.cs#L150-L151) 对 decimal 一元 `-/+ /~` 抛 `CS2J normalization required: unary operator on decimal operand`；[L183-L184](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/UnaryExpressionTransformer.cs#L183-L184) 对 decimal `++/--` 抛 `increment/decrement on a decimal target`。
- **switch governing decimal 直接抛**：[StatementTransformer.SwitchAndResource.cs:38](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Statement/StatementTransformer.SwitchAndResource.cs#L38) 把 decimal 与 bool/long/float/double 并列要求预处理为 if-chain（本规则不亲自降 switch，见 §2/§11 与 C03 D4 的接缝）。
- **已就绪面**：`m` 后缀字面量 → `Decimal.parse("…")`（[LiteralExpressionTransformer.cs:80-85](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/LiteralExpressionTransformer.cs#L80-L85)）；默认值 `Decimal.ZERO`（[FieldTransformer.cs:228-232](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Member/FieldTransformer.cs#L228-L232)、[ControlFlowTransformer.cs:332-344](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/ControlFlowTransformer.cs#L332-L344)）；数值转换包装 `Decimal.valueOf(...)`（commit dba62eb0，`(decimal)` 转换路径）；`new decimal(ulong)` → `Decimal.createFrom_long`（[DecimalULongConversionTests.cs:37-58](file:///d:/code/cs2j/tests/CSharpToJava.Tests/DecimalULongConversionTests.cs#L37-L58)）。
- **目标契约测试已存在且当前必红**：[DecimalMappingTests.cs:71-210](file:///d:/code/cs2j/tests/CSharpToJava.Tests/DecimalMappingTests.cs#L71-L210) 已锁定最终 Java 形态（`a.add(b)`、`sum.compareTo(diff) > 0`、`value.negate()`、混合操作数 `Decimal.valueOf(delta)`、复合赋值、`value.add(Decimal.ONE)`），但 2026-09-27「clean codes」重构（9f09f426）删除了对应发射路径后这些断言与实现脱节——静态路径核对（裸 `+` 透传 + 一元抛异常）可确认其为红。它们是**转换器消费 PR 的验收件**，不在本 preprocessor 任务内翻绿（§8、附开放问题 2）。

### 1.3 compat 桥：方法面已备齐，C# 侧从未发射

[Decimal.java](file:///d:/code/cs2j/java/csharptojava-compat/src/main/java/io/github/ningpp/compat/Decimal.java) 是不可变 `final` 类（持有 canonical `BigDecimal`，96 位尾数/scale 0..28/越界 `ArithmeticException("Decimal overflow.")`）。本任务相关方法面（已逐行核实）：

| 方法 | 行 | 形态 |
|---|---|---|
| `add/subtract/multiply/divide/remainder` | [L261-L312](file:///d:/code/cs2j/java/csharptojava-compat/src/main/java/io/github/ningpp/compat/Decimal.java#L261-L312) | **实例 + 静态双份**；实例返回 `checked(...)` 即 `new Decimal(...)`，**绝不返回 this、不改操作数**（不可变） |
| `negate` | [L314-L320](file:///d:/code/cs2j/java/csharptojava-compat/src/main/java/io/github/ningpp/compat/Decimal.java#L314-L320) | 实例 + 静态双份 |
| `compareTo(Decimal)` | [L419-L422](file:///d:/code/cs2j/java/csharptojava-compat/src/main/java/io/github/ningpp/compat/Decimal.java#L419-L422) | 实例；`Comparable<Decimal>` |
| `equals`/`hashCode` | [L424-L432](file:///d:/code/cs2j/java/csharptojava-compat/src/main/java/io/github/ningpp/compat/Decimal.java#L424-L432) | `equals` 已覆写为 `compareTo==0`（scale 不敏感），hash 经 `stripTrailingZeros` |
| 常量/工厂 | [L23-L27](file:///d:/code/cs2j/java/csharptojava-compat/src/main/java/io/github/ningpp/compat/Decimal.java#L23-L27)、[L98-L112](file:///d:/code/cs2j/java/csharptojava-compat/src/main/java/io/github/ningpp/compat/Decimal.java#L98-L112) | `ZERO/ONE/MINUS_ONE/MAX_VALUE/MIN_VALUE`；`valueOf(int/long/double/BigDecimal)` |
| 缺口 | — | **无静态 `compare(Decimal, Decimal)`**（决策 8 的前置补齐项） |

### 1.4 目标

1. 消灭经预处理路径上全部 decimal 运算符站点：二元 `+ - * / %`、`== != < > <= >=`、一元 `-`（一元 `+` 消去）、复合赋值，输出 BCL `System.Decimal` 显式调用（§2 决策 1），中间树 `dotnet build/test -c Release` 在 **.NET 真身 decimal 语义**上验证等价；
2. 隐式数值提升显式化（整数族操作数插 `(decimal)`；E09 无符号域操作数复用既有 `ToDecimalUInt32/64` 面）；
3. `const decimal` 常量字段在初始化式被归约为调用后正确降级为 `static readonly`；
4. 与 C15（checked 壳剥离）、C17（`++/--` 展为 `±1m`）、C22（`decimal?`）、C03/C37（decimal switch）的接缝全部以共处单测钉死；
5. 无法判定/不支持站点给 **CS2J536x Error**，禁止原样运算符流入 Java 产物；residual 闸门保证 decimal 运算符零残余；
6. 不新增 C# 支撑工程（论证见决策 12），compat 侧只做「方法面校验 + 至多一处静态 `compare` 补齐」。

明确不做（留边界）：`decimal?` lifted 运算（C22，[C22 决策 19](file:///d:/code/cs2j/docs/superpowers/specs/2026-09-29-c22-nullable-value-design.md#L95-L95)）；`++/--` 读-改-写展开（C17 IT-7，本规则只消费其 `±1m` 产物）；decimal switch 的 if-chain 归约（C03 D4/C37，本规则只消费其 `==` 产物）；`ToString/Parse/格式串/IFormattable` 文化（仅盘点，决策 14）；用户自定义运算符（C14）；Half/Int128 等（C41）；转换器侧发射代码改动（消费 PR 另行立项，§8）。

### 1.5 硬不变式

1. 一切 decimal 判定来自 SemanticModel（`GetTypeInfo`/`GetSymbolInfo`/`GetOperation`/`GetConstantValue`）：`SpecialType.System_Decimal` 符号判定，**禁止按 `decimal` 关键字文本或 `Decimal` 名称**（`using D = System.Decimal;` 别名、用户类型 `class Decimal` 均不得误伤/漏判）；
2. residual：输出用户代码（支撑工程除外）中不存在绑定到 BCL decimal 内置运算的运算符节点（DC-13）；
3. 产物为当前中间树可编译 C#；仅发射 BCL 已有方法（`decimal.Add/Subtract/Multiply/Divide/Remainder/Negate/Compare`），不发明 C# 侧不存在的成员；
4. 幂等（二次执行零 diff）、干净文件字节级复制（含 BOM）、trivia/`#if` 保持；
5. E09/C15/C17/C22 产物零改写、统计零变化（本规则只拥有 decimal 内置运算符站点）。

---

## 2. 关键设计决策表

| # | 决策点 | 结论 | 依据/备注 |
|---|---|---|---|
| 1 | **目标 C# 形态** | 运算符归约为 **BCL `System.Decimal` 自身的静态方法调用**：`a+b` → `decimal.Add(a,b)`；`- * / %` → `Subtract/Multiply/Divide/Remainder`；一元负 → `decimal.Negate(a)`；比较 → `decimal.Compare(a,b) <op> 0`。**不建 wrapper 支撑类型、不做 decimal 类型替换** | BCL 方法中间树原生可编译，.NET 半区跑的是运行时真身 decimal 语义（零重实现漂移）；经主转换器既有静态调用泛化路径（receiver `decimal` 映射 `Decimal`、方法名 camelCase，[InvocationExpressionTransformer.cs:465](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/InvocationExpressionTransformer.cs#L465-L465)）落 compat **静态别名**（六个算术 + negate 双份齐备，§1.3）。对照 E09：uint 必须类化是因为 Java 有无符号语义空洞；decimal 两端语义载体都已存在（BCL + compat），故无需 C# 支撑面（决策 12） |
| 2 | 运算符→方法全表（算术） | `+ → Add`、`- → Subtract`、`* → Multiply`、`/ → Divide`、`% → Remainder`；绑定判据 = `GetSymbolInfo(node).Symbol` 为 `System.Decimal` 的内置运算符（`MethodKind.BuiltinOperator`，`op_Addition/op_Subtraction/op_Multiply/op_Division/op_Modulus`）或结果 `SpecialType.System_Decimal` 且非源码用户运算符 | 沿用 [BinaryExpressionSupport.cs:158-162](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Utilities/BinaryExpressionSupport.cs#L158-L162) 的「源码用户运算符」排除口径（`IsSourceUserDefinedOperator`），C14 族站点不碰 |
| 3 | **`==`/`!=` 形态（★scale 正确性）** | 一律 `decimal.Compare(a, b) == 0` / `!= 0`；**禁止 `.Equals(...)`/`Objects.equals` 形态** | Java 裸 `BigDecimal.equals` 受 scale 影响（`1.0.equals(1.00) == false`），数值相等必须走 compareTo——这是 C# `1.0m == 1.00m == true` 的硬语义。compat `Decimal.equals` 虽已覆写为 compareTo 语义（[L424-L427](file:///d:/code/cs2j/java/csharptojava-compat/src/main/java/io/github/ningpp/compat/Decimal.java#L424-L427)），发射规则也**不依赖该覆写**（防映射回退/裸 BigDecimal 泄漏的纵深防御） |
| 4 | 大小比较 | `< <= > >=` → `decimal.Compare(a, b) {<0,<=0,>0,>=0}` | 复刻主转换器 TimeSpan 既有 compareTo 形态（[BinaryExpressionTransformer.cs:209-217](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/BinaryExpressionTransformer.cs#L209-L217)）；C# 侧 `decimal.Compare` 返回 int，语义精确 |
| 5 | 一元负/一元加 | `-d` → `decimal.Negate(d)`（`-decimal.MinValue` 两侧均抛：.NET `OverflowException`、compat `ArithmeticException`）；`+d` 直接消去括号保形 | 消除 [UnaryExpressionTransformer.cs:150-151](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/UnaryExpressionTransformer.cs#L150-L151) fail-fast |
| 6 | 复合赋值（简单目标） | 局部/参数/字段/无副作用接收者字段：`d += x` → `d = decimal.Add(d, X)`；`/=` `%=` 同形（Remainder/Divide）；运算后无窄化（decimal 是唯一域） | C# 复合赋值的「读一次写一次」对简单目标即等价；X 按决策 8 做提升 |
| 7 | 复合赋值（复杂目标） | 属性/索引器目标复用族内单次求值临时提升（C17 §4.4 lifter 形状）：`R.P += x` → `var __cs2j_dec_r = R; __cs2j_dec_r.P = decimal.Add(__cs2j_dec_r.P, X);`；索引器逐参提升 `__cs2j_dec_a<n>`；找不到无条件执行锚点（条件位/lambda/初始化器/查询）→ **CS2J5365 Error** | 与 C17 决策 3/4/14 同纪律：接收者/索引实参/getter/setter 各一次。v1 锚点白名单沿用 [UnsignedPostfixRewriter.cs:140-148](file:///d:/code/cs2j/src/CSharpToJava.Core/UnsignedTypeRewriter/UnsignedPostfixRewriter.cs#L140-L148)；不引入 ref helper（避免 C01 依赖） |
| 8 | 混合数值类型提升 | C# 可隐式转 decimal 的仅**整数族**（sbyte/byte/short/ushort/char/int/uint/long/ulong；float/double/decimal 反向无隐式转换，`d + 1.0` 本就是 CS0019 编译错误）。重写时对静态类型非 decimal 的操作数：**有符号/窄整数域显式插 `(decimal)` cast**（`decimal.Add(d, (decimal)i)`），主转换器沿既有 cast 路径发 `Decimal.valueOf(...)`；**E09 包装域**（`Cs2jUnsigned.UInt/ULong` 指纹或 checked 区真身 uint/ulong）复用既有 `UnsignedMath.ToDecimalUInt32(v)`/`ToDecimalUInt64(v)`（[UnsignedMath.cs:113-114](file:///d:/code/cs2j/src/Cs2jUnsignedMath/UnsignedMath.cs#L113-L114) ↔ [UnsignedMath.java:80-81](file:///d:/code/cs2j/java/csharptojava-compat/src/main/java/io/github/ningpp/compat/UnsignedMath.java#L80-L81)，E09 在 decimal 目标位已自发同款，[UnsignedTypeSyntaxRewriter.cs:351](file:///d:/code/cs2j/src/CSharpToJava.Core/UnsignedTypeRewriter/UnsignedTypeSyntaxRewriter.cs#L351-L351)、[L361](file:///d:/code/cs2j/src/CSharpToJava.Core/UnsignedTypeRewriter/UnsignedTypeSyntaxRewriter.cs#L361-L361)） | 显式 cast 让 Java 半区参数包装走统一路径（红测试契约 `value.add(Decimal.valueOf(delta))`）；不可判定的操作数类型 → CS2J5361 |
| 9 | 字面量 | `1m`/`1.00m` 等 decimal 字面量**原样保留在 C# 树**（主转换器已发 `Decimal.parse("…")`，[LiteralExpressionTransformer.cs:80-85](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/LiteralExpressionTransformer.cs#L80-L85)）；整数族字面量操作数按决策 8 插 `(decimal)1`；C17 产物中的 `1m` 直接成为 `decimal.Add(d, 1m)` 实参 | 不在 C# 侧造 `Decimal.parse` 字符串——中间树是真身 decimal，保留精度/scale 文本（`1.00m` 的 scale 2 在 .NET 半区参与 ToString 断言） |
| 10 | `const decimal` 字段/局部常量 | 初始化式仅含字面量的 `const decimal` **保留**；一旦初始化式被本规则归约出调用/非纯字面量节点，字段降级 `const → static readonly`（保留初始化式），并级联处理引用它做初始化的其他 const decimal；局部 const 同形降级。case 标签引用的 const 随 C03 D4 的 if-chain 降为普通比较后不再要求编译期常量，合法 | `static readonly` 不能用于特性实参，但 decimal 本就不允许进特性（CS0182）；降级是 C# 合法形态，Java 半区 `static final Decimal X = Decimal.parse(...)` 既有发射可承接 |
| 11 | `decimal?` 边界 | 任一操作数为 `Nullable<decimal>` 的 lifted 站点**整体不碰**（含 `d == null`、`d? + x`），归 C22；其守卫三元内层的裸 decimal 运算在 C22 产物中由本规则后续收编（[C22 决策 19](file:///d:/code/cs2j/docs/superpowers/specs/2026-09-29-c22-nullable-value-design.md#L95-L95) 已承诺守卫不动） | 分类器以 `ConstructedType`/`INamedTypeSymbol(Nullable<>)` 特判先行，避免误收 |
| 12 | **支撑库归属（不新建论证）** | **C# 侧零支撑工程**；descriptor 不增不改 `SupportLibrary`（维持 C15 声明的 `Cs2jUnsignedMath` 不动）；compat 仓为唯一 Java 桥且已存在 | 论证：①E09/C15 建支撑库是因为 Java 存在语义空洞（无符号/checked），而 decimal 的 .NET 语义载体是 BCL 自身、Java 载体是已交付 compat；②重写目标是 BCL 原语方法，中间树无需任何释放文件即可 build/test；③六个算术/negate 的 compat 静态别名已在，泛化调用路径直接产出可编译 Java；④规避释放/ProjectReference/Maven 过滤/指纹豁免/工程过滤六条接线（对照 [C15 决策 12](file:///d:/code/cs2j/docs/superpowers/specs/2026-09-28-c15-checked-unchecked-design.md#L89-L89)）。仅决策 8 的 `ToDecimalUInt32/64` 引用 E09 既有方法：链序保证 E09 先跑且有改写才释放该库；E09 零改写时树中无包装类型，本规则不产该调用 |
| 13 | compat 前置补齐 | 默认开启硬前提：新增 `public static int compare(Decimal a, Decimal b){ return a.compareTo(b); }` + JUnit（决策 4 的静态落点；3 行，遵循既有静态别名先例）。若评审拒绝改 compat，则比较改发实例形态 `a.CompareTo(b)`（C# 绑 `CompareTo(object)` 装箱，可编译、值正确，Java 落 `compareTo(Decimal)`），二选一在 M0 定死 | 算术静态面无需补齐；方法面齐备性由 §5 的校验测试守门（[catalog §4 风险 14](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L933-L933)） |
| 14 | ToString/Parse/格式串 | **仅盘点不改写**：`ToString("F2")`/`IFormattable`/`Parse(…, culture)` 等调用站点透传主转换器既有映射（compat `toString(String,IFormatProvider)` → `MathHelper.formatNumeric`，[Decimal.java:408-417](file:///d:/code/cs2j/java/csharptojava-compat/src/main/java/io/github/ningpp/compat/Decimal.java#L408-L417)；parse 的 culture 重载当前忽略文化，[L177-L199](file:///d:/code/cs2j/java/csharptojava-compat/src/main/java/io/github/ningpp/compat/Decimal.java#L177-L199)） | 文化差异（CurrentCulture vs Locale 默认）是运行时近似，登记风险（§10-4）；sdk 命中率驱动是否单列后续 |
| 15 | checked decimal 与 C15 | decimal 算术**始终溢出检查**，checked/unchecked 对其无增减效应（[C15 §1.1](file:///d:/code/cs2j/docs/superpowers/specs/2026-09-28-c15-checked-unchecked-design.md#L20-L20)）；C15 NB-11 只剥壳不插整数 helper，本规则对剥壳后的裸 decimal 运算照常归约，最终落 compat 各方法内的 `checked(...)` 越界抛错 | 异常映射沿用 `OverflowException → ArithmeticException`（[ExceptionApiRewriter.cs:93](file:///d:/code/cs2j/src/CSharpToJava.Core/Java/Rewriters/ExceptionApiRewriter.cs#L93-L93)）；除零两侧同抛（.NET `DivideByZeroException`/compat `ArithmeticException`），与 C15 决策 14 同款近似，文档化不细分捕获 |
| 16 | `++/--` 接缝（C17） | 本规则**不拥有**任何 `++/--` 节点；C17 IT-7 把 decimal 自增展为 `d = d + 1m`（[C17 决策 8](file:///d:/code/cs2j/docs/superpowers/specs/2026-09-29-c17-increment-target-design.md#L96-L96)），本规则随后把其中二元 `+` 归约为 `decimal.Add(d, 1m)`；规则序保证 C17 产物被本规则访问到（§3.3）。启用本规则后 C17 的 CS2J5334 依赖标记在 decimal 站点上不再发出（规则所有权表驱动，不删码） | 最终 Java 形态 `d = d.add(Decimal.ONE)` 由转换器消费 PR 承接（红测试 [DecimalMappingTests.cs:190-210](file:///d:/code/cs2j/tests/CSharpToJava.Tests/DecimalMappingTests.cs#L190-L210)） |
| 17 | switch governing decimal | 不亲自降 switch：**switch 语句**归 C03 D4（非整型 governing 常量标签 → `__gov == c` if-chain，[C03 D4](file:///d:/code/cs2j/docs/superpowers/specs/2026-09-28-c03-pattern-statement-desugar-design.md#L82-L82)）；**switch 表达式**归 C37。两族在链上远早于本规则（ControlFlow 阶段 vs unsigned 之后的 Expression 阶段），其产物中的 decimal `==` 必然由本规则 DC-7 收编为 `decimal.Compare(...) == 0` | `goto case`/fallthrough 在 C03 if-chain 内消化；本规则以端到端 fixture 验证接缝，不新增 switch 代码 |
| 18 | 舍入/除法语义 | C# decimal 除法在精确商超过 28 位小数时舍入到 28 位；compat `divide` 先精确除、失败回落 `setScale(MAX_SCALE, RoundingMode.HALF_UP)`（[Decimal.java:291-L295](file:///d:/code/cs2j/java/csharptojava-compat/src/main/java/io/github/ningpp/compat/Decimal.java#L291-L295)），而历史设计 [2026-06-04-csharp-decimal-design.md:89](file:///d:/code/cs2j/docs/superpowers/specs/2026-06-04-csharp-decimal-design.md#L89-L89) 与测试计划（同文档 L158）写明 **ties to even**。该 HALF_UP vs HALF_EVEN 分歧由双半区预言机裁决，compat 修正是默认开门禁（§8-3、§10-2）；乘法 scale>28 的 canonicalize 现为 HALF_EVEN（[L555-L579](file:///d:/code/cs2j/java/csharptojava-compat/src/main/java/io/github/ningpp/compat/Decimal.java#L555-L579)），同批对拍 | 余数用 `BigDecimal.remainder`（向零截断取余，与 C# `%` 同号规则一致），同样入预言机 |
| 19 | dynamic/表达式树 | decimal 运算绑定为 dynamic，或位于 `Expression<Lambda>` 树内 → CS2J5362/CS2J5363 Error（force 不解除） | 沿用 [catalog §4 风险 8](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L927-L927) 与 C15/C17 同口径；v1 在 DecimalTypeFacts 自持判定，登记技术债 |
| 20 | 执行形态 | 逐文件语义规则，单趟 `CSharpSyntaxRewriter`（自底向上）+ 复杂复合赋值的语句级 lifter；无跨文件改名/签名、无 plan/回退 | 与 C17 同构；verify build/test 由族适配器双开兜底 |

---

## 3. 链位与族契约

### 3.1 归属：numeric-boundary 族 Rules/DecimalOps/

- 物理位置：`src/CSharpToJava.Core/NumericBoundary/Rules/DecimalOps/`（[C15 §4.1](file:///d:/code/cs2j/docs/superpowers/specs/2026-09-28-c15-checked-unchecked-design.md#L179-L187) 预留）。只增该子目录、自有测试与 fixture 追加文件；**不新建 pass、不新增适配器、不新增中间目录、不新增支撑工程**。
- **不翻转 stub**：`numeric-boundary` 的 Stub→Implemented 由 C15 完成。
- 注册表唯一允许触碰：描述符 `catalogItems` 单行追加（按 C15→C17→C22→C34 实际合入序，届时为 `"…, C22" → "…, C22, C34"`，[C15 §3.4-9](file:///d:/code/cs2j/docs/superpowers/specs/2026-09-28-c15-checked-unchecked-design.md#L160-L160) 预告的预期一行冲突）。Status/DefaultEnabled/RunAfterIds/Stage/verify/支撑库字段一律不动；链序测试不动。

### 3.2 RunAfter=unsigned 的语义

锚边 `{ unsigned }` 对本规则有实质含义而非继承摆设：

1. E09 把 uint/ulong 变量类化为 `Cs2jUnsigned.UInt/ULong`、把无符号算术改写为 `UnsignedMath.*` 调用，并在 decimal 目标位插 `ToDecimalUInt32/64` 转换——本规则的混合提升分类（决策 8）消费这一既成形态；E09 **从不把 `decimal` 关键字当无符号类型改写**（[UnsignedTypeSyntaxRewriter.cs:702-705](file:///d:/code/cs2j/src/CSharpToJava.Core/UnsignedTypeRewriter/UnsignedTypeSyntaxRewriter.cs#L702-L705) 把 decimal 划入浮点/目标类型侧），两规则无语法面争抢。
2. E09 在 checked 区跳过真身 uint/ulong（`IsInCheckedContext`），故决策 8 必须同时识别「真身 uint/ulong」与「包装类型」两种操作数。
3. E09 零改写的树中不存在包装类型，本规则不产 `ToDecimal*` 调用，因而不引入对 `Cs2jUnsignedMath` 的新注入依赖（决策 12）。

### 3.3 文件内规则序（INumericBoundaryRule）

按 [C17 §4.5-1](file:///d:/code/cs2j/docs/superpowers/specs/2026-09-29-c17-increment-target-design.md#L230-L230) 的规则管线：C17 站点展开先跑、C15 后扫新整数节点。**DecimalOps 必须在两者之后执行**（Order=30；若 M0 管线为「降序注册、先展开后扫」的多趟结构，则以独立第三趟挂在 C15 扫后——M0 按 C15 落地版实际驱动方式定，用两个共处用例锁死：`d++` 与 `checked { d++; }` 的最终产物含 `decimal.Add(d, 1m)` 且无 checked/unchecked 节点）。同样，C22 守卫三元的内层 decimal 运算必须被本规则访问到（与 C22 的共处用例：`decimal? a, b; var x = a + b;`）。

### 3.4 诊断码段

使用 C15 预留的 **CS2J5360-5379**（[C15 §4.5](file:///d:/code/cs2j/docs/superpowers/specs/2026-09-28-c15-checked-unchecked-design.md#L247-L247)），码位只追加不改号。

---

## 4. Core 组件设计

### 4.1 目录与文件清单（C34 独占 Rules/DecimalOps/）

```
src\CSharpToJava.Core\NumericBoundary\Rules\DecimalOps\
  DecimalOpRule.cs          # INumericBoundaryRule（Order=30）：Analyze→Rewrite→Lift 驱动，
                            #   汇出 RuleFragment（统计桶/诊断/摘要片段；不声明支撑库需求）
  DecimalTypeFacts.cs       # IsDecimalOperand（SpecialType 符号判定，含别名/用户类型反例）、
                            #   内置 decimal 运算符绑定判定（BuiltinOperator + op_* 名）、
                            #   整数提升域集合、E09 包装类型指纹（Cs2jUnsigned.UInt/ULong/
                            #   UnsignedBits）、Nullable<decimal> 排除、dynamic/表达式树谓词、
                            #   const 上下文判定、可提升锚点谓词
  DecimalOpAnalyzer.cs      # 逐站点分类：(SiteKind, 左/右操作数码型, 需要的提升渲染, 是否常量上下文)
  DecimalOpRewriter.cs      # CSharpSyntaxRewriter 自底向上：运算符节点→InvocationExpression，
                            #   操作数提升包装，复合赋值展开（简单/复杂目标），一元负，
                            #   const 降级；收集 PendingLift；RewriterAnnotation 挂接
  DecimalOpSyntaxFacts.cs   # 调用工厂（decimal.Add/Subtract/.../Compare 全限定 SimpleName）、
                            #   (decimal) cast 工厂、UnsignedMath.ToDecimalUInt32/64 调用工厂、
                            #   临时名 __cs2j_dec_r/a<k>/n、碰撞避让、FormatAndValidate 复用
  DecimalOpDiagnostics.cs   # CS2J5360-5379 码位常量与 MSBuild 消息模板
  DecimalOpModels.cs        # 规则 Options 节（EnableDecimalOps 默认 true，仅 kill-switch）、
                            #   强类型统计桶
```

测试新增：`tests/CSharpToJava.ProjectTests/NumericBoundaryDecimalOpUnitTests.cs`；fixture 向 C15 建立的 `tests/SampleNumericBoundarySolution/` 追加 `DecimalOps/` 子目录（不新建 sln）。

### 4.2 DecimalTypeFacts：站点分类（一切以 SemanticModel 为准）

- **算术站点**：`BinaryExpressionSyntax`（Add/Subtract/Multiply/Divide/Modulo），`GetSymbolInfo(node).Symbol` 为 `System.Decimal` 声明的 `op_Addition/op_Subtraction/op_Multiply/op_Division/op_Modulus`（MethodKind.BuiltinOperator）；符号缺失时退回 `GetTypeInfo(node).Type/ConvertedType` 双侧为 `System_Decimal` 且操作数非用户运算符类型。
- **比较站点**：EqualsExpression/NotEqualsExpression 与四种 Relational，同绑定判据；string 拼接（结果 string）、事件比较、用户 struct 运算符一律排除。
- **一元站点**：UnaryMinus（decimal）；UnaryPlus 直接保形消去。
- **复合赋值站点**：AssignmentExpression（AddAssignment…ModuloAssignment），左值类型/转换目标为 decimal。
- **排除先行**：任一操作数为 `Nullable<decimal>`（C22）、绑定 dynamic、位于表达式树 lambda、源码用户运算符（C14）、枚举（E04/C05）。
- **操作数码型**：Decimal / NarrowingSigned（sbyte/byte/short/ushort/char/int/long 真身）/ UnsignedNative（checked 区真身 uint/ulong）/ UnsignedWrapped（E09 指纹）/ LiteralInteger / Other（CS2J5361）。

### 4.3 重写规则全集（DC-x）

| 规则 | 输入 | 输出 | 要点/边界 |
|---|---|---|---|
| DC-1 加 | `a + b`（decimal 绑定） | `decimal.Add(A, B)` | A/B 经操作数提升（DC-9） |
| DC-2 减 | `a - b` | `decimal.Subtract(A, B)` | — |
| DC-3 乘 | `a * b` | `decimal.Multiply(A, B)` | scale 相加语义由运行时/ compat 承担 |
| DC-4 除 | `a / b` | `decimal.Divide(A, B)` | 除零/28 位舍入入预言机（决策 18） |
| DC-5 模 | `a % b` | `decimal.Remainder(A, B)` | 向零截断取余 |
| DC-6 关系比较 | `a < b` 等 | `decimal.Compare(A, B) {<0/<=0/>0/>=0}` | 决策 4 |
| DC-7 相等比较（★） | `a == b` / `a != b` | `decimal.Compare(A, B) == 0` / `!= 0` | `1.0m == 1.00m` 必须为真；禁 equals 形态 |
| DC-8 一元 | `-a` / `+a` | `decimal.Negate(a)` / `a` | `-MinValue` 双半区抛 |
| DC-9 操作数提升 | 非 decimal 操作数 | 有符号/窄整数/整数字面量 → `(decimal)x`；E09 包装/真身 uint/ulong → `UnsignedMath.ToDecimalUInt32(x)`/`ToDecimalUInt64(x)`；decimal 操作数原样 | C# 隐式提升显式化；保证 Java 半区参数为 Decimal |
| DC-10 复合赋值（简单） | `d += x` 等五种 | `d = decimal.Add(d, X)` … | 决策 6；`+=` on string 不属本规则 |
| DC-11 复合赋值（复杂目标） | `R.P %= x`、`m[i] /= x` | 接收者/索引实参临时提升 + 单次读-改-写；无安全锚点 → CS2J5365 | 决策 7；副作用探针为硬回归 |
| DC-12 常量降级 | `const decimal C = a + b;`（初始化式被归约） | `static readonly decimal C = decimal.Add(a, b);`（局部 const → 普通 `decimal` 局部） | 级联引用方；纯字面量 const 保留（决策 10） |
| DC-13 residual 对账 | 输出树重扫 | 用户代码零 decimal 内置运算符节点，否则 CS2J5367（strict Error） | `decimal?`/支撑工程/用户运算符白名单 |
| DC-14 响亮失败 | dynamic/表达式树/不可判定类型/无锚点复杂目标 | Error 诊断、节点原样、本次失败 | 不出半成品文件 |

### 4.4 输出形状示例

```csharp
// 输入
decimal r = (a + b) * 2 - c / d;
bool eq = x == 1.00m;
value += count;
R.Rate /= 2m;
const decimal Fee = 1.5m * Rate;
// 输出（中间树 C#，BCL 原生可编译）
decimal r = decimal.Subtract(decimal.Multiply(decimal.Add(a, b), (decimal)2), decimal.Divide(c, d));
bool eq = decimal.Compare(x, 1.00m) == 0;
value = decimal.Add(value, (decimal)count);
var __cs2j_dec_r = R;
__cs2j_dec_r.Rate = decimal.Divide(__cs2j_dec_r.Rate, 2m);
static readonly decimal Fee = decimal.Multiply(1.5m, Rate);
```

主转换器消费后的目标 Java（消费 PR 后，红测试契约）：`a.add(b).multiply(Decimal.valueOf(2)).subtract(c.divide(d))`、`x.compareTo(Decimal.parse("1.00")) == 0`、`value = value.add(Decimal.valueOf(count))` 等；静态别名形态 `Decimal.add(a,b)` 在消费 PR 前亦可编译（§8）。

### 4.5 Options / Statistics

- 规则节 `rules.decimalOps.EnableDecimalOps` 默认 **true**（随 pass 启停，仅 kill-switch，不暴露 CLI 独立开关，同 C17 立场）；`strict` 复用族值。
- 统计桶（经 RuleFragment 汇入族 Statistics 与适配器摘要）：`BinarySitesByOp`（add/sub/mul/div/rem）、`ComparisonSites`（equality/relational 分桶）、`UnarySites`、`CompoundSites`（simple/complex 分桶）、`PromotionSites`（cast/ToDecimalUInt32/ToDecimalUInt64 分桶）、`ConstantsDowngraded`、`TempsByRole`、`DeferredByReason: Dictionary<code,int>`。
- 摘要片段建议：`decimal-ops: B binary/C comparison/U unary/A compound site(s), P promotion(s), K const downgraded, T temp(s), R deferred`。

---

## 5. CLI 集成（零注册表结构改动）

1. **无新 pass/适配器/开关/中间目录/支撑工程**：规则随 `numeric-boundary` 执行，入口完全继承 C15（`--numeric-boundary`/`--enable-pass numeric-boundary`/通用 `preprocess --pass`）；pass 默认关期间规则零执行零痕迹；不新增 legacy toggle、不进 [PassChainFactory.cs](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassChainFactory.cs)。
2. **注册表唯一改动**：PassCatalog 描述符 `catalogItems` 追加 C34（§3.1）；`--list-passes` 自动反映；`PassChainOrderTests`/`PassStubCatalogTests` 不动。
3. **适配器零改动**：本规则不产生支撑库需求（RuleFragment 不请求库；决策 12）；仅族摘要行经 RuleFragment 自渲染追加片段。
4. **verify**：继承描述符双默认开；decimal 归约把运算符变为调用，最易暴露签名/转换错误，verify build 不允许规则级关闭。
5. **单文件模式**：继承族 CS2J5309 闸门；含 `UnsignedMath.ToDecimal*` 调用的站点走项目模式（纯 BCL 调用站点自包含，可处理）。
6. **compat 方法面校验清单（硬前提测试，非运行时脚本）**：新增一个读 `Decimal.java` 源码并断言方法面的测试（先例 [DecimalULongConversionTests.cs:52-57](file:///d:/code/cs2j/tests/CSharpToJava.Tests/DecimalULongConversionTests.cs#L52-L57)），清单：
   - 实例 `add/subtract/multiply/divide/remainder/negate` 与对应**静态双份**签名各一（[L261-L320](file:///d:/code/cs2j/java/csharptojava-compat/src/main/java/io/github/ningpp/compat/Decimal.java#L261-L320)）；
   - `compareTo(Decimal)`、`equals` 内含 compareTo 等价比（scale 不敏感）；
   - 静态 `compare(Decimal,Decimal)`（决策 13 补齐项，缺失即测试红）；
   - `ZERO/ONE/MINUS_ONE`、`valueOf(int/long/double)`、`parse(String)`；
   - `divide` 回落舍入模式常量（决策 18 对拍哨兵：若实现保持 HALF_UP 而 .NET 裁决为 to-even，该校验与 §7.4 预言机共同阻断默认开）。

---

## 6. 诊断策略（消灭原样运算符输出）

MSBuild 格式：`文件(行,列): warning/error CS2J53xx: [DC-x/分类] 描述`。族共享码（restore/build/test 5302/5303/5304、单文件 5309、C15/C17 各码）不另立。

| 码 | 级别 | 含义 |
|---|---|---|
| CS2J5360 | Info | decimal 站点归约计数（按 DC 规则分桶），仅 verbose |
| CS2J5361 | Error | 操作数类型不可判定/不支持的混合域（非整数族、未知包装），无法安全提升 |
| CS2J5362 | Error | dynamic 绑定的 decimal 运算（force 不解除） |
| CS2J5363 | Error | 表达式树 lambda 内 decimal 运算（改写改变树形态；force 不解除） |
| CS2J5364 | — | 预留（`decimal?` lifted 归 C22，正常路径 skip 不发码） |
| CS2J5365 | Error | 无安全提升锚点的复杂目标 decimal 复合赋值（条件位/lambda/初始化器/查询/特性实参） |
| CS2J5366 | Warning（strict Error） | 归约产物依赖 E09 `ToDecimal*` 但树中支撑工程不可用（链序异常哨兵，正常不可达） |
| CS2J5367 | Warning（strict Error） | residual 闸门：输出仍存在 decimal 内置运算符节点（DC-13） |
| CS2J5368 | Info | const→static readonly 降级计数，仅 verbose |
| CS2J5369 | Error | compat 方法面校验缺失（CI 测试级；列入表仅为码段完整） |
| CS2J5370-5379 | — | 预留 |

原则（[catalog §4 风险 12](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L931-L931) 第一原则）：无法显式化即 Error，禁止裸运算符流入 Java。转换器侧 [BinaryExpressionTransformer.cs:88-94](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/BinaryExpressionTransformer.cs#L88-L94) 与 [UnaryExpressionTransformer.cs:150-151](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/UnaryExpressionTransformer.cs#L150-L151)、[L183-L184](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/UnaryExpressionTransformer.cs#L183-L184) 在本规则随预处理生效后对「经预处理项目」成为不可达路径；删除/保留属转换器消费 PR（同 C15/C17 模式），直连 ConversionPipeline 单文件入口保留安全网，既有测试一行不改。

---

## 7. 测试计划

全部以 `-c Release` 执行；先写单测矩阵再实现。

### 7.1 Core 单测矩阵（NumericBoundaryDecimalOpUnitTests.cs）

内联源码 → `CSharpCompilation` → 族 Rewrite（C15+C17+C34 组合）→ 文本断言忽略空白 + `AssertOutputCompiles`：

1. **四则/取模**：`decimal.Add/Subtract/Multiply/Divide/Remainder` 形态文本；嵌套表达式括号保形；链式调用形状。
2. **比较 scale 陷阱（★）**：`1.0m == 1.00m` → `decimal.Compare(1.0m, 1.00m) == 0`；`!=`；`< <= > >=` 四个方向；零与负零；`1.0m < 1.00m` 为 false 形态；断言输出**不含** `.Equals(`。
3. **一元**：`-d` → `decimal.Negate(d)`；`+d` 消去；`-decimal.MinValue` 输出可编译（运行时抛归 fixture）。
4. **复合赋值**：简单目标五种 `+= -= *= /= %=`；属性目标接收者临时单次（`GetObj().Rate += x` 探针）；索引器目标实参单次/源序；无锚点位置 → CS2J5365。
5. **混合类型提升**：int/long/byte/char 变量与字面量操作数 → `(decimal)` cast；`d + 2` 与 `2 * d` 对称；E09 包装类型操作数 → `UnsignedMath.ToDecimalUInt32/64`（以最小手型模拟包装指纹两态）；checked 区真身 uint 同路径；float/double 混合基线即编译错误，不构造。
6. **字面量/常量**：`1m`/`1.00m` 原样；`const decimal A = 1.5m;` 保留；`const decimal B = A * 2m;` 降级 `static readonly` 且级联；局部 const 降级；case 标签常量经 C03 if-chain 后可编译（联调）。
7. **checked 组合**：`checked(a + b)`、`checked { d += x; }`、`checked { d++; }` 三处：最终无 checked/unchecked 节点、运算为 decimal 调用、自增为 `decimal.Add(d, 1m)`（与 C15 NB-11/C17 IT-7 共处）。
8. **C17 接缝**：`d++`/`--d`/属性 decimal 自增 → C17 展形后本规则收编；启用本规则时 decimal 站点不再计 CS2J5334。
9. **C22 接缝**：`decimal? a, b; var r = a + b;` → 守卫三元保留、内层裸算被本规则收编为 `decimal.Add(__a.Value, __b.Value)`；`d == null` 不动。
10. **switch 接缝（联调）**：decimal governing switch 语句（经 C03 风格 if-chain 输入）中 `==`/`<` 标签比较全部归约；goto case 形态随 C03 输出。
11. **不支持族**：dynamic → CS2J5362；表达式树 → CS2J5363；别名 `using D = System.Decimal;` 正确识别；用户 `class Decimal` 的 `+` 零误伤；用户 struct `operator +` 零误伤；string 拼接中的 decimal 操作数零误伤。
12. **工程性质**：幂等（二次零 diff）；干净文件字节一致 + BOM；支撑工程路径 clean copy；trivia/`#if` 跟随；临时名碰撞避让。

### 7.2 项目级 fixture（SampleNumericBoundarySolution/DecimalOps/ 追加）

- Lib：四则/取模/比较（含 scale 矩阵）/一元/复合（简单+属性+索引器埋点类）/混合整数/E09 无符号共处/const 字段/checked 块组合/switch on decimal；一个含全部 CS2J536x 反例的文件。
- Tests（**.NET 半区真身守门**）：xunit 在去糖后 C# 上断言——运算值与直接运算符逐例一致；`1.0m == 1.00m` 为真；`decimal.MaxValue + 1m`、`-decimal.MinValue`、乘法越界抛 `OverflowException`；`x/0`、`x%0` 抛 `DivideByZeroException`；属性/索引器复合赋值 get/set/接收者/实参各一次；高精度除法向量（§7.4 预言机同源输入）。
- 项目级测试：`result.Success`/统计桶/中间树文本（含 `decimal.Add`、`decimal.Compare`，不含 decimal 运算符残余）；**中间树 `dotnet build` 与 `dotnet test -c Release` 退出码 0**；干净工程字节复制/BOM；反例诊断码与退出码。

### 7.3 Release build/test

- `dotnet build -c Release`（CSharpToJavaConverter.slnx 全绿）；
- `dotnet test -c Release` 三工程：CSharpToJava.Tests、CSharpToJava.ProjectTests、CSharpToJava.ConversionTests。

### 7.4 Java 半区双半区数值预言机（默认开硬门禁）

1. **同一输入向量双半区对照**：以 §7.2 的输入表（含高精度除法 `1m/3m`、`2m/3m`、`1m/6m`、tie 候选如 `1m/20000000000000000000000000000m`（5e-29 等距点）、scale 加乘组合 `1.0m+1.00m`/`1.00m*1.0m`、余数正负号、越界、除零、negate MinValue）分别在 .NET xunit 与 compat `DecimalTest.java` JUnit 跑出结果逐值对拍（数值串比较，避免 double 中转）。
2. compat 侧新增/补齐 JUnit：六个算术/negate、静态 `compare`、scale 相等、divide tie 舍入方向；**若 .NET 裁决 ties-to-even，则 `Decimal.java:294` 的 `HALF_UP` 必须先修为 `HALF_EVEN`**（与历史设计 [2026-06-04:89](file:///d:/code/cs2j/docs/superpowers/specs/2026-06-04-csharp-decimal-design.md#L89-L89) 对齐）再放行。
3. 代表性样本经完整 C#→Java 链产出可编译 Java：BCL 静态调用经泛化路径落 `Decimal.add(...)` 静态别名（消费 PR 前的可编译底线）；参数位 `(decimal)` 转换实发 `Decimal.valueOf(...)`（若参数包装在静态形态下不完整，列入消费 PR 必做项，样本选最小集合）。

### 7.5 residual 闸门（DC-13）

1. 输出中间树所有非支撑工程 .cs parse 后扫描：绑定 BCL decimal 内置运算的二元/一元节点为零；残留白名单 = `decimal?`（C22 未启用时）、用户运算符、支撑工程目录（`Cs2jUnsignedMath/` 等，豁免集继承 C15）。
2. 上游回归：E09/C15/C17/enum 既有 fixture 启用本规则后统计零变化、中间树 build/test 全绿；`UnsignedMath.*`/`ToDecimal*` 调用零误改。

### 7.6 CLI / 一致性

- 默认关：无新增行为、无目录、无库；启用后 `--list-passes` catalogItems 含 C34；无新 toggle/适配器；CS2J9001 不针对本 id。
- 转换器侧直连测试（含当前必红的 [DecimalMappingTests.cs:71-210](file:///d:/code/cs2j/tests/CSharpToJava.Tests/DecimalMappingTests.cs#L71-L210)）在本任务内**不改不翻绿**（消费 PR 验收件）；其余既有用例（DecimalMappingTests 的类型/字面量/默认值正例、DecimalULongConversionTests 等）持续全绿。

---

## 8. 默认开启验收门槛

`defaultEnabled` 由 C15 总门槛统一持有（本规则无独立开关位）。在 C15 [§8](file:///d:/code/cs2j/docs/superpowers/specs/2026-09-28-c15-checked-unchecked-design.md#L355-L366) 之上，C34 追加必备条件：

1. §7 全部单测/fixture/Release build/test/residual/CLI 绿；.NET 半区真身数值断言全绿。
2. **compat 方法面齐备（硬前提）**：§5-6 校验测试绿；静态 `compare` 补齐并附 JUnit；六个算术/negate 双份签名零缺失。
3. **双半区预言机（§7.4）对拍全绿**：含 scale 陷阱、28 位高精度除法与 tie 舍入裁决；若需 compat HALF_UP→HALF_EVEN 修正，修正合入是前置条件。
4. **Java 半区可编译闭环**：静态形态样本经完整链路可编译且行为一致；转换器消费 PR 已立项且范围书面确认（BCL 静态→compat 实例/静态最终形态、参数 `Decimal.valueOf` 包装、红测试处置——附开放问题 2）。
5. **csharp-sdk 扫描**：decimal 运算符站点总量、归约分桶、提升类型分布、CS2J5361/5362/5363/5365 命中清单逐条人工定性；复杂目标复合赋值与条件位命中率决定 DC-11 覆盖面是否需提前扩。
6. sdk 基线不退化：上游 pass 统计零变化；无库注入/无新目录，耗时增量仅逐文件单趟遍历。

未达门槛前随族默认关；不形成「多套事实标准」（[catalog §4 风险 10](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L929-L929)）。

---

## 9. 里程碑

| 里程碑 | 内容 | 预估规模 |
|---|---|---|
| M0 接缝与 compat 前置 | C15 合入基上挂 `INumericBoundaryRule`（Order=30，§3.3 顺序钉死）、Options 节/统计段/fixture 目录占位；DecimalTypeFacts 分类单测（别名/用户类型/nullable/E09 指纹反例零误伤）；compat 静态 `compare` + JUnit；方法面校验测试；C15/C17/C22 共处骨架用例 | Core ~250 行 + compat 小改 |
| M1 二元算术与提升 | DC-1~DC-5、DC-9（cast 路径）、DC-12、DC-13 算术侧闸门、CS2J5360/5361/5367/5368；四则/混合/字面量/常量 fixture 与 .NET 真身断言；residual 算术侧 | Core ~450 行 + fixture |
| M2 比较/一元/复合 | DC-6~DC-8、DC-10/DC-11（lifter）、C17 ±1m 接缝（CS2J5334 移交）、CS2J5362/5363/5365；scale 陷阱矩阵；属性/索引器副作用探针；C22/C03 联调用例 | Core ~400 行 |
| M3 默认开评审 | E09 包装域提升全量（ToDecimal 双域）、checked 组合回归、双半区预言机（含舍入裁决/compat 修正）、sdk 扫描、转换器消费 PR 范围书；随族单行默认开 | 数据/文档 + compat |

---

## 10. 风险

1. **★scale equals 陷阱（最高危正确性点）**：裸 `BigDecimal.equals` 按 scale 判等，`1.0` 与 `1.00` 不等；任何 equals/Objects.equals 发射都会产生能编译但值错的 Java。缓解：DC-7 只允许 `compareTo==0` 形态并有文本断言；compat 侧 equals 覆写只作第二道防线；fixture 以 `1.0m==1.00m`/字典键/hash 一致性守门。
2. **除法精度与 tie 舍入**：compat `divide` 回落用 HALF_UP（[Decimal.java:294](file:///d:/code/cs2j/java/csharptojava-compat/src/main/java/io/github/ningpp/compat/Decimal.java#L294-L294)）与历史设计 ties-to-even 不一致；乘法 canonicalize 用 HALF_EVEN。缓解：双半区同向量预言机（§7.4）裁决，默认开前修齐；文档化受影响输入边界。
3. **scale 保真与 ToString**：C# `1.0m+1.00m` 结果 scale 2、`ToString()` 保留尾零（"2.00"）；compat `add` 借 BigDecimal max-scale 行为大体一致，但 canonicalize 对 scale>28/负 scale 的归一可能在边角产生文本差异。缓解：预言机含 ToString 文本对拍；差异项归入兼容清单（数值相等不受影响）。
4. **文化（IFormattable）**：`ToString("F2")`/Parse 文化重载在 compat 走默认 Locale/忽略 culture（[Decimal.java:177-199](file:///d:/code/cs2j/java/csharptojava-compat/src/main/java/io/github/ningpp/compat/Decimal.java#L177-L199)、[L408-L417](file:///d:/code/cs2j/java/csharptojava-compat/src/main/java/io/github/ningpp/compat/Decimal.java#L408-L417)），与 CurrentCulture 可能不同。缓解：本规则只盘点不改写；sdk 命中高时另立文化后续，不阻塞运算归一。
5. **转换器消费缺口**：本任务产物在消费 PR 前落 compat 静态别名（可编译但与红测试的实例形态不同）；参数位 `Decimal.valueOf` 包装可能不全；裸 `+` 透传站点仍在直连路径。缓解：§8-4 闭环门禁；消费 PR 范围书与红测试一并移交；预处理路径以 residual 闸门保证零裸运算符。
6. **异常类型合并**：Overflow/DivideByZero 在 Java 同映射 ArithmeticException，`catch(DivideByZeroException)` 会连带捕获溢出（与 [C15 决策 14](file:///d:/code/cs2j/docs/superpowers/specs/2026-09-28-c15-checked-unchecked-design.md#L91-L91) 同款近似）。缓解：文档化，sdk catch 命中扫描后决定是否要 compat 异常子类。
7. **E09 形态交互**：包装域识别漏判会产出不可编译的 `(decimal)uIntWrapper`；ToDecimal 调用在库未释放时失效。缓解：双指纹识别 + 链序证明（E09 有改写才会出现包装类型）+ CS2J5366 哨兵；共处 fixture 硬回归。
8. **复杂目标单次求值**：属性/索引器复合赋值文本展开导致接收者/实参双求值。缓解：强制临时化 + 埋点探针；无锚点即 CS2J5365，不硬做（C17/E09 已踩坑）。
9. **const 级联**：降级遗漏会制造「方法调用初始化 const」编译错误，过度降级则改变可见性语义。缓解：仅对含归约节点的初始化式降级，级联集合单测；verify build 兜底。

---

## 11. 并行协调

- **C15 锚点（硬前置，同族）**：base 必须含已合入 C15；只增 `Rules/DecimalOps/`、自有测试与 fixture 追加；根层若需接缝（规则注册/共享 lifter 上收）走 [C15 §4.1](file:///d:/code/cs2j/docs/superpowers/specs/2026-09-28-c15-checked-unchecked-design.md#L187-L187) 授权流程并携带 C15 回归；CatalogItems 单行追加；CS2J5360-5379 码段不越界。
- **C17 自增（同族）**：`++/--` 节点所有权归 C17；本规则收编其 `±1m` 二元产物；规则序与 CS2J5334 移交按决策 16/§3.3；两规则文件面零重叠（`IncrementTargets/` vs `DecimalOps/`）。
- **C22 nullable（同族）**：`decimal?` 守卫归 C22、内层运算归本规则（[C22 §11](file:///d:/code/cs2j/docs/superpowers/specs/2026-09-29-c22-nullable-value-design.md#L388-L388) 已确认）；C22 先落地时其 decimal 内层裸算在本规则启用前保持 CS2J5334 同款「依赖响亮可见」立场，不重复发码。
- **E09 unsigned（已实现）**：RunAfter 锚边的实质语义见 §3.2；复用 `ToDecimalUInt32/64` 双半区现成面，不新增方法；E09 的 checked 区跳过契约使真身 uint/ulong 两种输入都须识别。
- **C03/C37（pattern/control-flow 族）**：decimal switch 语句/表达式的 if-chain/三元归约分别归 C03 D4（[L82](file:///d:/code/cs2j/docs/superpowers/specs/2026-09-28-c03-pattern-statement-desugar-design.md#L82-L82)）与 C37；链序保证本规则后扫其比较产物；C03 默认开门禁已要求 decimal governing 样本在 Java 半区验证（[C03 §8/风险 6](file:///d:/code/cs2j/docs/superpowers/specs/2026-09-28-c03-pattern-statement-desugar-design.md#L349-L349)），与本规则 §7.4 合并执行。
- **C14 operator（member-desugar 族，未落地）**：用户自定义运算符站点语义模型带 `OperatorMethod`，本规则排除谓词保证不碰；C14 落地前后均无语法面争抢。
- **C41（同族，未落地）**：Half/Int128/nint 与 decimal 无交集；`sizeof(decimal)` 等占位归 C41/sizeof 后续规则（[TypeOperationTransformer.cs:162](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/TypeOperationTransformer.cs#L162-L162) 现状直接产 "16"，本规则不触碰）。
- **热文件冲突解法**：生产改动集 = `Rules/DecimalOps/*.cs`（全新目录）+ 规则注册一处（C15 根层文件，带 C15 回归）+ PassCatalog CatalogItems 一行 + 测试/fixture 追加；与 C22/C41 仅 CatalogItems 字符串预期同行冲突，按合入序追加。

---

## 12. Worktree 分配（只写不执行）

- 分支：`feature/preproc-c34-decimal-ops`
- 目录：`.worktrees/preproc-c34-decimal-ops`
- base：`953293b9`（`docs: update doc base commit`，已核实存在）；**实现前先在该 worktree 内合入 C15 分支**（feature/preproc-c15-checked-unchecked；若 C17 已先行合入则一并含之，规则序与 CS2J5334 接缝以含 C17 的基做 M2），再开始 M0——C15 未合入时 `NumericBoundary/` 根层与适配器尚不存在。
- 创建命令（在仓库根、PowerShell 下执行；**本设计阶段不执行，原样记录**）：

```powershell
git worktree add .worktrees/preproc-c34-decimal-ops -b feature/preproc-c34-decimal-ops 953293b9
```

- worktree 内交付面遵循 foundation plan「并行开发交付包模板」（[preprocessor-foundation_plan.md:108-117](file:///d:/code/cs2j/.trae/documents/preprocessor-foundation_plan.md#L108-L117)）：Core 仅新增 `NumericBoundary/Rules/DecimalOps/`（+ 经授权的根层小接缝）、Core 单测 + SampleNumericBoundarySolution/DecimalOps/ 追加 + Release build/test + residual 闸门 + CLI 断言；**不新建支撑工程、不新增支撑库方法**（compat 仓仅静态 `compare` 与 JUnit 小补）；注册表除 CatalogItems 单行外零改动；不触转换器代码（§6 不可达站点与红测试的处置为转换器消费 PR 输入）。

---

## 附：关键开放问题（供评审拍板）

1. **除法/乘法 tie 舍入裁决**：.NET decimal 28 位舍入在等距点究竟 to-even 还是 away-from-zero（历史设计与 compat HALF_UP 实现冲突）——以 §7.4 双半区预言机拍板；若 to-even，compat `divide` 回落须改 HALF_EVEN 并补 JUnit，作为默认开硬前提。
2. **最终 Java 形态与转换器消费 PR 范围**：BCL 静态调用自然落 compat 静态别名（`Decimal.add(a,b)`，现成可编译），而红测试契约是实例形态（`a.add(b)`）；消费 PR 是否在默认开前合入、静态/实例是否二选一统一、参数位 `Decimal.valueOf` 包装补齐范围，需与转换器 owner 定死；红测试 [DecimalMappingTests.cs:71-210](file:///d:/code/cs2j/tests/CSharpToJava.Tests/DecimalMappingTests.cs#L71-L210) 的翻绿时点随之确定。
3. **比较落点**：补 compat 静态 `compare`（推荐，统一静态形状）还是 C# 侧发 `a.CompareTo(b)` 实例形态（零 compat 改动但 .NET 半区装箱）——M0 二选一。
4. **复杂目标复合赋值 v1 覆盖面**：DC-11 全量单次求值提升（推荐，lifter 可复用）还是 v1 仅简单目标 + CS2J5365 显式拒绝，由 sdk 属性/索引器 decimal 复合命中率决定。
5. **文化面排期**：`ToString("F2")`/Parse 文化维持盘点透传（推荐）还是随本规则收口，取决于 sdk 格式串命中率与文化正确性要求。
