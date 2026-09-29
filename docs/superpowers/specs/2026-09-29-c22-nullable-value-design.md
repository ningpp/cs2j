# C22 `Nullable<T>` 值类型提升（numeric-boundary 族规则）设计

**日期**: 2026-09-29
**范围**: 在已合入的 `numeric-boundary` 族 pass（C15 锚点）内新增规则子目录 `Rules/Nullable/`，把 `T?`（值类型）上的 lifted 算术/一元运算、关系/相等比较、`bool?` 三值逻辑、`GetValueOrDefault()`/`.Value`/`.HasValue`、可空枚举位运算兜底、可空类型转换、lifted 自增/复合赋值显式化为守卫形态的合法 C#（`HasValue`/`Value`/三元/临时变量）。本任务**不注册新 pass、不翻转 stub、不改链位**，不改任何 C#→Java 转换代码
**状态**: 设计已确认，待实施（硬前置：C15 分支合入主干）
**族属**: catalog P3「数值边界族」（[preprocessor-catalog.md:520-530](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L520-L530) C22 条目；[§3.3 数值边界族与 enum 族](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L906-L914)）。注册表 stub `numeric-boundary`（[PassStubCatalog.cs:163-171](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassStubCatalog.cs#L163-L171)，CatalogItems `"C15, C17, C22, C34, C41"`，RunAfter=`unsigned`，Stage=`Expression`）；C15 是锚点，本任务在 C15 合入后以 `INumericBoundaryRule`（Order=30）增量挂接

---

## 1. 背景与目标

### 1.1 C# lifted（提升）运算符语义速览

`Nullable<T>`（`T : struct`）上的预定义运算按 C# 规范「提升」：

- **lifted 算术/位运算/一元**（`+ - * / % & | ^ << >>`、一元 `- + ~`）：任一操作数为 null → 结果为 null；双方都有值 → 对底层值做运算，结果类型 `T?`。
- **lifted 关系运算**（`< > <= >=`）：任一操作数为 null → 结果为 **`false`**（注意：结果类型是非可空 `bool`，不是 `bool?`）；双方有值 → 底层值比较。
- **lifted 相等运算**（`== !=`）：双方均 null → `==` 为 `true`；恰一方 null → `false`；双方有值 → 底层值比较。与 null 字面量比较 `x == null` 等价于 `!x.HasValue`。
- **`bool?` 三值逻辑**：lifted `&`/`|`/`^` 与 lifted `!` 遵循 Kleene 三值真值表（§2 决策 7-8）；**C# 不存在 lifted `&&`/`||`**（条件逻辑运算符不提升）。
- **lifted 自增/复合赋值**：`x++`（`x: T?`）——x 为 null 时结果与新值均为 null；否则按底层值自增。
- **转换**：`T → T?` 包装恒成立；`T? → T` 显式解包等价于 `.Value`（null 抛 `InvalidOperationException`）；`S? → T?` 为 lifted 转换（null 传播）。
- **`GetValueOrDefault()`/`GetValueOrDefault(d)`**：分别返回 `Value` 或 `default(T)`/`d`，**不抛异常**。
- **`.Value`**：`!HasValue` 时抛 `System.InvalidOperationException`。

### 1.2 Java 半区的对应面

转换器现状把 `T?` 映射为 Java 装箱类型（`int? → Integer`、`bool? → Boolean`、…，[TypeMappingService.cs:408-434](file:///d:/code/cs2j/src/CSharpToJava.Core/Context/TypeMappingService.cs#L408-L434)，`UseOptionalForNullable` 时可选 `Optional`；[TypeMappingRegistry.cs:238-242](file:///d:/code/cs2j/src/CSharpToJava.Core/TypeMapping/TypeMappingRegistry.cs#L238-L242) 解包映射）。装箱类型上：

- Java 的 `+ - * < > ==` 等对操作数**自动拆箱**：null 操作数直接抛 `NullPointerException`，而 C# lifted 算术要求 null 传播为「结果 null」、lifted 关系要求「结果 false」——**同形运算符语义不同**，且不报错、产物还能「看起来正常」；
- `Boolean` 上的 Java `&`/`|` 同样拆箱，null 即 NPE，且三值真值表（`false & null == false`）无法表达；
- `.Value` 经拆箱在 null 时抛 `NullPointerException`，与 C# 的 `InvalidOperationException` 异常类型不同（后者现状映射 `IllegalStateException`，[ExceptionApiRewriter.cs:36](file:///d:/code/cs2j/src/CSharpToJava.Core/Java/Rewriters/ExceptionApiRewriter.cs#L36-L36)）。

结论：lifted 语义**必须在 C#→C# 层显式化**为 `HasValue` 守卫 + `.Value` 取值（与 [C15 §1.2](file:///d:/code/cs2j/docs/superpowers/specs/2026-09-28-c15-checked-unchecked-design.md#L22-L26) 同属 catalog §5.3「能放 C#→C# 层做的 lowering 就不放 Java emit 层」）。显式化后，Java 半区只需面对：引用比较（`!= null`）、守卫后的安全拆箱、三元表达式——全部是现成映射。

### 1.3 现状：特判分散 + 三值逻辑静默错值面

转换器侧 nullable 处理是一组互不闭合的点状特判：

1. **仅 `bool? == true/false` 被特判**：[BinaryExpressionSupport.TryNullableBoolEquality](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Utilities/BinaryExpressionSupport.cs#L82-L115) 发 `Objects.equals(x, Boolean.TRUE/FALSE)`（测试 [NullableBoolComparisonTests.cs](file:///d:/code/cs2j/tests/CSharpToJava.Tests/NullableBoolComparisonTests.cs)）。覆盖面只有「相等 + 字面量/双侧 `bool?`/条件访问」三种语法面；`bool?` 的 `& | ^ !`、`int?` 算术、lifted `< >`、lifted `==` 均不在内。
2. **lifted 算术/关系落入裸运算符默认路径**：[BinaryExpressionTransformer.cs:114-116](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/BinaryExpressionTransformer.cs#L114-L116) 直接产 `{left} {op} {right}`，对 `Integer`/`Boolean` 自动拆箱——null 输入 NPE 或错值（lifted 关系应得 `false`），无诊断；decimal 路径同样裸 `+`（[L88-94](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/BinaryExpressionTransformer.cs#L88-L94)，C34 面）。
3. **`GetValueOrDefault()` 无任何映射**：全仓 grep 转换器内无该方法调用面的处理；[GetValueOrDefaultMappingTests.cs](file:///d:/code/cs2j/tests/CSharpToJava.Tests/GetValueOrDefaultMappingTests.cs) 只剩类壳（零测试方法）；而 [LinqRewriter.Rules.cs:1465](file:///d:/code/cs2j/src/CSharpToJava.Core/LinqRewrite/LinqRewriter.Rules.cs#L1465-L1465) 还在**合成** `.GetValueOrDefault()` 调用送入下游——流入 Java 装箱类型是不存在的方法（§10 风险 8）。
4. **`Nullable.GetUnderlyingType` 单点已映射**：[TypeMappings.json:8234-8240](file:///d:/code/cs2j/config/TypeMappings.json#L8234-L8240) → `TypeHelper.getUnderlyingType`（[NullableGetUnderlyingTypeTests.cs](file:///d:/code/cs2j/tests/CSharpToJava.Tests/NullableGetUnderlyingTypeTests.cs)）；compat 侧 [TypeHelper.java](file:///d:/code/cs2j/java/csharptojava-compat/src/main/java/io/github/ningpp/compat/TypeHelper.java) 已实现。这是**已闭合**的孤点，本规则只做回归锁定。
5. **null 字面量比较特判**：[BinaryExpressionSupport.IsStructNullLiteralComparison](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Utilities/BinaryExpressionSupport.cs#L303-L306) 让值类型与 null 的 `==/!=` 走 HasValue 形态（回归 [StructTransformerTests.cs:449-455](file:///d:/code/cs2j/tests/CSharpToJava.Tests/StructTransformerTests.cs#L449-L455)）；通用 lifted 相等（双 `T?`、单侧 `T`）无处理。
6. **可空转换裸 cast**：[TypeOperationTransformer.Cast](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/TypeOperationTransformer.cs#L53-L86) 默认臂产 `(目标Java类型)(e)`；`T? → T` 解包渲染为对装箱类型的强拆（null 时 NPE 而非 `InvalidOperationException`），`S? → T?` lifted 转换无 null 守卫。`default(T?)` 已正确产 `null`（[L134](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/TypeOperationTransformer.cs#L134-L134)）。
7. **泛型基类 `T?` 运行时类**特判（[NullableBaseTypeRuntimeClassTests.cs](file:///d:/code/cs2j/tests/CSharpToJava.Tests/NullableBaseTypeRuntimeClassTests.cs)）——属反射/泛型面，本规则不改，列入回归。
8. **上游已局部验证形态有效**：E04 enum pass 的 lifted 位运算重写（[EnumSyntaxRewriter.RewriteLiftedBinary](file:///d:/code/cs2j/src/CSharpToJava.Core/EnumPreprocessor/EnumSyntaxRewriter.cs#L365-L456)、lifted `~` [L480-L521](file:///d:/code/cs2j/src/CSharpToJava.Core/EnumPreprocessor/EnumSyntaxRewriter.cs#L480-L521)、lifted 复合赋值 [L594-L628](file:///d:/code/cs2j/src/CSharpToJava.Core/EnumPreprocessor/EnumSyntaxRewriter.cs#L594-L628)）已产 `x.HasValue ? (E?)(…) : (E?)null` 内联三元，统计 `NullableLiftedRewritten`（[EnumPreprocessorStatistics.cs:15](file:///d:/code/cs2j/src/CSharpToJava.Core/EnumPreprocessor/EnumPreprocessorStatistics.cs#L15-L15)）——正是本规则的目标形态原型；但其「非稳定引用即 Warning 跳过」纪律（[L374-L384](file:///d:/code/cs2j/src/CSharpToJava.Core/EnumPreprocessor/EnumSyntaxRewriter.cs#L374-L384)）会留下未改写节点。
9. **unsigned pass 显式跳过 nullable 操作数**：`uint?` 运算以真身抵达转换器（[UnsignedTypeSyntaxRewriter.cs:635-639](file:///d:/code/cs2j/src/CSharpToJava.Core/UnsignedTypeRewriter/UnsignedTypeSyntaxRewriter.cs#L635-L639)、[L747](file:///d:/code/cs2j/src/CSharpToJava.Core/UnsignedTypeRewriter/UnsignedTypeSyntaxRewriter.cs#L747-L747)、[L891](file:///d:/code/cs2j/src/CSharpToJava.Core/UnsignedTypeRewriter/UnsignedTypeSyntaxRewriter.cs#L891-L891)）——lifted `uint?` 是本规则必须接住的输入面。
10. compat 仓**不存在** `Nullable*.java` 支撑类（全仓仅 TypeHelper/Assert/xunit 命中 Nullable 字样），也不存在 `NullableHelper`/`Cs2jNullable` 标识符（全仓 grep 零命中）。

### 1.4 目标

在 C#→C# 层（`numeric-boundary` pass 内）产出：

1. **null 传播显式化**：全部受支持的 lifted 内置运算/比较/三值逻辑/自增/复合赋值，重写为「`HasValue` 守卫 + `.Value` 取值 + `(T?)` 保形」的三元或布尔表达式，null 路径与有值路径都按 C# 规范精确；
2. **成员面显式化**：`GetValueOrDefault()/GetValueOrDefault(d)` 重写为等价三元；`.HasValue`/`.Value` 保持（守卫后站点的 `.Value` 永不落空）；
3. **转换显式化**：`T? → T` 解包 cast 改写为 `.Value`；`S? → T?` lifted 转换加守卫；`T → T?` 包装 cast 原样；
4. **副作用单次求值**：以「稳定引用直取 + 不稳定操作数临时提升」取代 E04 的「不稳定即跳过」，临时变量在锚点语句前一次性声明；
5. **响亮失败**：无法在 v1 显式化的 lifted 站点（用户自定义 lifted 运算符、dynamic、表达式树、复杂读-改-写目标、C41 特殊数值）给 CS2J535x Error/Warning，禁止原样透传流入 Java 产物；
6. 产物仍是可编译 C#，由中间树 `dotnet build -c Release` + `dotnet test -c Release`（含 null/有值矩阵的运行时断言）证明 .NET 半区与原生 lifted 语义逐格等价（catalog §5.1 两半区方法论）。

明确不做（留边界）：`??`/`??=` 语法归 E03（已实现，本规则只消费其产物）；条件访问 `?.`/`?[]` 归 C35；`enum` 声明形态归 C05；decimal 内层运算归 C34（本规则只产守卫，内层裸 `+` 不动）；Half/Int128/UInt128/nint 的 `T?` 归 C41；模式匹配归 C03/C37；任何 C#→Java 转换代码改动；`Nullable.GetUnderlyingType`/`typeof(T?)` 不改写（已闭合，仅回归）。

### 1.5 硬不变式

1. **residual 闸门**（语义模型判定，不靠文本 grep）：输出用户代码范围（支撑工程除外）内不存在「操作数静态类型为 `Nullable<T>`、绑定为内置 lifted 运算符（`OperatorMethod == null`）」的二元/一元运算节点；不存在接收者为 `Nullable<T>` 的 `GetValueOrDefault` 调用；不存在 `T? → T` 解包 cast；不存在 nullable 简单目标的 lifted `++/--`/算术复合赋值；
2. null 语义零丢失：每个 lifted 站点要么变为守卫表达式、要么有 Error 诊断使本次转换失败，不存在第三种去向；
3. **单次求值**：任一被重写表达式中的子表达式（接收者、实参、自增目标、`GOV(d)` 的默认值参数）在运行时的求值次数与原生 C# 严格一致（不多不少、源序不变）；
4. 一切类型/提升判定来自 SemanticModel（`GetTypeInfo`/`GetSymbolInfo`/`GetOperation`），禁止按 token 文本猜测；
5. 幂等：产物二次执行零 diff；干净文件字节级复制（含 BOM 保持）。

---

## 2. 关键设计决策表

| # | 决策点 | 结论 | 依据/备注 |
|---|---|---|---|
| 1 | 总体目标形态 | **内联守卫形态**：`a.HasValue && b.HasValue ? (T?)(a.Value OP b.Value) : (T?)null`（算术/位/一元）与布尔守卫链（关系/相等）；不做每站点 helper 调用 | 与 E04 已落地并验证的 lifted enum 形态同构（§1.3-8）；守卫内是普通 `T` 运算，unsigned/C15/C34 各规则照常作用于内层；不新增支撑库、TypeMappings、compat 类，Java 半区零新方法面 |
| 2 | 多次取值与副作用 | **稳定引用直取，不稳定操作数临时提升**：局部/参数/字段（接收者链稳定）直接多次访问；其余（属性/索引器/调用/条件访问/被本规则重写出的三元等）先提升为 `__cs2j_n_*` 临时，守卫与取值都只读临时量 | 取代 E04「不稳定即 Warning 跳过」（[EnumSyntaxRewriter.cs:374-384](file:///d:/code/cs2j/src/CSharpToJava.Core/EnumPreprocessor/EnumSyntaxRewriter.cs#L374-L384)），消灭其残余面；lifter 形态对齐 E03 [NullCoalesceSyntaxRewriter.cs:27-30](file:///d:/code/cs2j/src/CSharpToJava.Core/NullCoalesceRewriter/NullCoalesceSyntaxRewriter.cs#L27-L30) 与 C17 IncrementTargetLifter |
| 3 | lifted 算术/位/移位真值表 | 二元 `+ - * / % & \| ^ << >>`：`__a.HasValue && __b.HasValue ? (T?)(__a.Value op __b.Value) : (T?)null`；一元 `- + ~`：`__a.HasValue ? (T?)(op __a.Value) : (T?)null` | null 传播；外层 `(T?)` 保形同 E04；内层按底层域分类：内建整数/浮点直接写，`uint?/ulong?` 内层是真身 unsigned 运算（与 §11 unsigned 协调），decimal 内层裸算留 C34 |
| 4 | lifted 关系 `< > <= >=` | `__a.HasValue && __b.HasValue && __a.Value op __b.Value`（结果 `bool`） | null 即 `false`，**不产 `bool?`**；单侧为非可空 `T` 时该侧无 HasValue 分支 |
| 5 | lifted 相等 `== !=` | 双侧 `T?`：`__a.HasValue ? (__b.HasValue && __a.Value == __b.Value) : !__b.HasValue`，`!=` 整体取反；单侧 `T`（字面量/非可空操作数）：`__a.HasValue && __a.Value == b`；null 字面量：`x == null → !x.HasValue`、`x != null → x.HasValue` | 保留「null == null 为 true」的 C# 特例；`bool? == true/false` 由此一般规则自然覆盖（`flag == true → flag.HasValue && flag.Value`），转换器 `Objects.equals` 特判对预处理路径成为不可达路径（§6） |
| 6 | `bool?` 三值 `&` | `((a.HasValue && !a.Value) \|\| (b.HasValue && !b.Value)) ? false : ((!a.HasValue \|\| !b.HasValue) ? (bool?)null : (bool?)true)` | 真值表：任一为 `false` → `false`（即使另一方 null）；双方 `true` → `true`；其余 → null。Java `Boolean.&` 拆箱遇 null NPE，必须重写 |
| 7 | `bool?` 三值 `\|` | `((a.HasValue && a.Value) \|\| (b.HasValue && b.Value)) ? true : ((!a.HasValue \|\| !b.HasValue) ? (bool?)null : (bool?)false)` | 任一为 `true` → `true`；双方 `false` → `false`；其余 → null |
| 8 | `bool?` 的 `^` 与 lifted `!` | `^` 按普通 lifted 二元（决策 3，任一 null → null）；`!a → a.HasValue ? (bool?)!a.Value : (bool?)null` | C# 规范：`^` 对 `bool?` 是 null 传播而非三值特殊规则；`&&`/`||` **不提升、源程序也写不出**，本规则不处理 |
| 9 | `??`/`??=` 边界 | **不改写**。E03 已把 `a ?? b` 归约为「临时 + `t != null ? t : b`」（链位早于本规则）；其左操作数若含 lifted 运算（如 `(x + y) ?? 0`），E03 先把 lifted 节点包进临时初始化器，本规则随后在该初始化器内照常显式化 | 两遍范式天然咬合，测试双 pass 组合（§7.5）；E03 不做 lifted 算术（其 rewriter 只认 CoalesceExpression，[NullCoalesceSyntaxRewriter.cs:44-47](file:///d:/code/cs2j/src/CSharpToJava.Core/NullCoalesceRewriter/NullCoalesceSyntaxRewriter.cs#L44-L47)），边界无重叠 |
| 10 | `GetValueOrDefault()` | `x.GetValueOrDefault() → __t.HasValue ? __t.Value : default(T)`；`x.GetValueOrDefault(d) → __t.HasValue ? __t.Value : __d`（`d` 有副作用同样提升一次） | GOV 不抛异常；Java 装箱类型无此方法（§1.3-3）。`default(T)` 由转换器现有默认值面消费（[TypeOperationTransformer.cs:132-153](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/TypeOperationTransformer.cs#L132-L153)） |
| 11 | `.Value`/`.HasValue` | **保持原样**。守卫三元内的 `.Value` 仅在 HasValue 为真时求值，永不抛；用户直接手写的 `.Value` 也保留——其 null 抛异常是 C# 语义 | 不新增「throw helper」。Java 半区把裸 `.Value` 拆箱成 NPE 而非 `IllegalStateException`（现状 `InvalidOperationException → IllegalStateException` 映射），属已知近似，列开放问题 3；本规则不承诺纠正手写 `.Value` 的异常类型 |
| 12 | 可空枚举 `E?` | ① E04 已重写的稳定 lifted 位运算产物（三元形态）本规则**不再改写**（三元不是 lifted 运算符节点，语义谓词不命中）；② E04 因不稳定跳过而残留的 lifted `& \| ^ ~`/复合赋值，本规则**兜底重写**（临时提升 + E04 同款底层 cast/窄类型 `unchecked` 形态）；③ `E?` 的比较/相等/lifted 转换走通用规则；④ C05 struct 形态下 `Nullable<E>` 是普通可空 struct，规则自然生效，无 enum 特例 | [C05 D12/§11](file:///d:/code/cs2j/docs/superpowers/specs/2026-09-28-c05-enum-declaration-design.md#L481-L481) 已划界：lifted 位运算 E04、比较/转换/GOV 留 C22；兜底时窄枚举回绕包裹复刻 [EnumSyntaxRewriter.cs:497-500](file:///d:/code/cs2j/src/CSharpToJava.Core/EnumPreprocessor/EnumSyntaxRewriter.cs#L497-L500)，交给 C15 NB-1 剥壳 |
| 13 | 转换四象限 | ① `T → T?` 包装 cast：**原样**（Java 半区装箱，合法）；② `T? → T` 显式解包 cast：改写为 `__t.Value`；③ `S? → T?` lifted 转换：`__s.HasValue ? (T?)(T)__s.Value : (T?)null`（内层 `(T)` 为底层转换，checked 时由 C15 后扫插 helper）；④ `S? → T` 解包+转换：`__s.HasValue ? (T)__s.Value : throw 形态`——v1 改写为 `__s.Value` 经一次显式 cast 等价链（null 走 `.Value` 抛异常语义），不内联 throw | 以 `IConversionOperation` 的 NullableFlow/转换种类判定，区分包装/解包/lifted，不靠语法 cast 文本；枚举互转、`bool?→int?` 等非法组合源程序本就不编译 |
| 14 | 链式/嵌套 lifted | 自底向上重写：内层 lifted 先变三元；外层把「三元形态的 `T?` 操作数」判为**不稳定**，提升为临时后再做外层守卫。`a + b + c`（均 `T?`）→ 内层一临时、外层一临时的嵌套链 | 临时数 = 不稳定 nullable 中间点数，线性膨胀；嵌套真值（`null + 1 + 2 → null`）由 C# 三元短路天然保持；fixture 覆盖三层嵌套与混合 `bool?` |
| 15 | lifted 自增/自减（简单目标） | 独立后缀 `x++;`：`var __o = x; x = __o.HasValue ? (T?)(__o.Value + 1) : (T?)null;`；值用后缀以 `__o`（类型 `T?`，即旧值）原地替换；前缀 `++x`：`(x = x.HasValue ? (T?)(x.Value + 1) : (T?)null)`；`--` 同形。简单目标 = 局部/参数/无副作用接收者字段 | 收口 C17 的 CS2J5335（[C17 §6](file:///d:/code/cs2j/docs/superpowers/specs/2026-09-29-c17-increment-target-design.md#L265-L265)）；lifted 后缀旧值为 null 时表达式值即 null，`__o` 直接承载；checked 区内层 `+1` 由 C15 后扫为 `AddChecked*`（null 路径不检查，符合 lifted checked 规范） |
| 16 | lifted 复合赋值（简单目标） | `x op= y` → `x = (x.HasValue && __y.HasValue ? (T?)(x.Value op __y.Value) : (T?)null)`；`y` 不稳定则提升；目标为简单目标时 x 两次读访问合法 | 与决策 3 同真值表；`bool?` 的 `&= \|=`、`E?` 位运算复合同形（enum 复合先看 E04 是否已处理）；属性/索引器目标 v1 走决策 17 |
| 17 | 复杂读-改-写目标 | v1：属性/索引器/事件/`?.`/数组特殊元素等 nullable 目标的 lifted `++/--`/复合赋值 → **CS2J5355 Error**；M2 经 C17 RMW 渲染接缝（`IncrementTargetRule` 的 READ/WRITE 展开）支持 | 不允许生成重复求值的半成品；C17 的 LiftedNullable 分类（[C17 §4.2](file:///d:/code/cs2j/docs/superpowers/specs/2026-09-29-c17-increment-target-design.md#L193-L193)）与本规则在同一 Analyzer 上按站点交接 |
| 18 | checked 上下文组合 | lifted checked 不单独造 helper：守卫内的 `T` 裸算术/转换由 C15 checked 后扫统一插入 `AddCheckedXxx/ToXxxCheckedYyy`。形态 `a.HasValue && b.HasValue ? (int?)UnsignedMath.AddCheckedInt32(a.Value, b.Value) : null` | 精确对应 C# lifted checked：仅双方有值时做溢出检查，null 直接传播；C15 的 CS2J5325（[C15 §6](file:///d:/code/cs2j/docs/superpowers/specs/2026-09-28-c15-checked-unchecked-design.md#L299-L299)）在本规则合入后对可解站点停用（码位保留不复用） |
| 19 | `decimal?` | 守卫照产，内层 `decimal` 运算保持裸 `+ - * …`（归 C34 compat `Decimal` 方法面）；本规则不为 decimal 发任何 helper、不报错 | 守卫与算术核解耦使两族可独立落地；C34 落地后内层被其规则收编，外层守卫零改动。未启用 C34 时 Java 半区 decimal 正确性不保证——同 C17 CS2J5334 的既有立场，本规则不重复发码 |
| 20 | 特殊数值可空（C41 面） | `nint?/nuint?/Int16?` 以外的 C41 域（`Half?/Int128?/UInt128?` 等）上的 lifted 站点 → **CS2J5356 Error**，归 C41 | nint 平台位宽问题在 lifted 形态同样成立；不静默透传 |
| 21 | 用户自定义 lifted 运算符 | v1：`OperatorMethod != null` 的 lifted 二元/一元（用户 struct 上的提升运算符）→ **CS2J5353 Error**；目标形态预留为「守卫 + 运算符方法调用」（`a.HasValue && b.HasValue ? Op(a.Value, b.Value) : null`），待 C14 运算符去糖落地后收编 | 手写 `op_Addition` 调用不是合法 C# 源码形态（运算符不可直接当方法名调），必须等 C14；Error 而非跳过 |
| 22 | dynamic / 表达式树 | dynamic 绑定的 lifted 站点 → CS2J5352 Error；`Expression<Lambda>` 树内 lifted 节点 → CS2J5354 Error | 沿用 [catalog §4 风险 8](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L927-L927) 共享判定（v1 在本规则 Analyzer 自持，复用 C15 根层谓词）；force 不解除 |
| 23 | 支撑库 `NullableHelper` | **v1 不建**：不新增支撑工程、不新增 `PassSupportLibrary` 枚举、compat 不新增 `Nullable*.java`、TypeMappings 不增条 | 守卫/三元/拆箱全部是 C# 与 Java 原生语法面；单文件模式因此可完整工作（无库依赖）。若 sdk 扫描证明用户自定义 lifted（决策 21）或 `.Value` 异常类型区分（决策 11）高命中，再评审建 `Cs2jNullable`，列开放问题 1 |
| 24 | `Nullable.GetUnderlyingType` / `typeof(T?)` | **透传不改写**，纳入回归锁定：前者经 TypeMappings 走 `TypeHelper.getUnderlyingType`；后者保持 `typeof` 由转换器现有路径处理 | 已闭合孤点（§1.3-4/7）；本规则只在 residual 扫描中明确豁免，不误伤 |
| 25 | 执行形态 | **族内规则碎片**（C17 §4.5 同款 `INumericBoundaryRule` + `RuleFragment`），逐文件语义、单树两遍（重写 + 提升），不新增 pass/适配器/开关/中间目录；规则 Order=30（C15 CheckedArithmetic=10、C17 IncrementTargets=20） | 本任务不翻转任何 stub（§3）；统计/诊断/摘要经 fragment 汇入族级对象，适配器文件除注入判定一行外零改动 |

---

## 3. 链位与族契约

### 3.1 不注册、不翻转

- stub `numeric-boundary`（[PassStubCatalog.cs:163-171](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassStubCatalog.cs#L163-L171)）由 C15 任务负责翻转；本任务实施时 C15 已合入，**本任务不碰** `PassStubCatalog.cs`、不新增 pass id、不改 RunAfter（仍为 `{ unsigned }`）、不改 Stage（`Expression`）、不新增中间目录（仍 `.cs2j-numeric-boundary-src`）、不新增适配器与 CLI 开关。
- 规则随 `numeric-boundary` pass 启停：pass 默认关期间本规则零执行、零目录、零耗时、零痕迹；默认开启门槛是 C15 族级决策（§8 只追加本规则的验收项，不单独翻 `defaultEnabled`）。

### 3.2 规则顺序与文件内交互

- `NullableLiftRule` 注册 `Order = 30`（C15 CheckedArithmetic=10，C17 IncrementTargets=20，[C17 §4.5-1](file:///d:/code/cs2j/docs/superpowers/specs/2026-09-29-c17-increment-target-design.md#L230-L230)）。文件内共享一个 `NumericBoundaryAnalyzer`：
  - **C15 后扫新内层节点**：本规则展开守卫后，内层 `T` 裸算术/转换在 checked 上下文由 C15 规则后扫插 helper（决策 18）；
  - **C17 站点交接**：C17 分类器对 `LiftedNullable` 站点不发 CS2J5335 当本规则已注册且可处理；简单目标由本规则 NL-10/11 处理，复杂目标由本规则发 CS2J5355（M2 前）或经 RMW 接缝处理（M2 后）；
  - **E04 产物透传**：enum lifted 三元在本规则看来是 `ConditionalExpression`，非 lifted 运算节点，天然不重复改写。
- 若实施时 C17 尚未合入：Order=30 仍预留注册；CS2J5335 不存在，nullable 自增站点全部由本规则按决策 15/17 处理，C17 合入时零冲突（不同子目录、不同码段）。

### 3.3 对 C15 共享层的依赖（M0 接缝）

实施基 = C15 合入后的主干。需要 C15 根层提供（若 C15 落地版已具备则零接缝）：

1. `INumericBoundaryRule`（`int Order`、文件级 Analyze 注册、`RewriteNode` 钩子、`RuleFragment`：统计桶/诊断/摘要片段/库需求）与规则发现管线；
2. `NumericBoundaryAnalyzer` 的只读 API：checked/unchecked 上下文查询、工程 `/checked` 种子、表达式树/dynamic 谓词、支撑工程目录跳过谓词、`FormatAndValidate` 三候选；
3. 族 Statistics/Options 的规则节扩展点；
4. 适配器注入判定已族化（[C17 §4.5-3](file:///d:/code/cs2j/docs/superpowers/specs/2026-09-29-c17-increment-target-design.md#L232-L232)）。本规则**不产生任何支撑库调用**，不改变注入判定表达式。

### 3.4 规则目录命名：`Rules/Nullable/`

C15 [§4.1](file:///d:/code/cs2j/docs/superpowers/specs/2026-09-28-c15-checked-unchecked-design.md#L182-L182) 预留名为 `Rules/NullableNumeric/`（仅文档预留，**无任何文件**）。本任务按任务契约定名 **`Rules/Nullable/`**：覆盖面含 `bool?` 三值逻辑、可空 enum、`GetValueOrDefault`、转换面，不止 numeric。C15 合入前预留目录未创建，更名零成本，本文档即更名记录；C17/C34/C41 的 `Rules/IncrementTargets/`、`Rules/DecimalOps/`、`Rules/SpecialNumeric/` 命名不受影响。

### 3.5 注册表唯一改动

[PassCatalog.cs](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassCatalog.cs) 中 numeric-boundary 描述符 CatalogItems 单行追加：`"C15, C17" → "C15, C17, C22"`（若 C17 未合入则为 `"C15" → "C15, C22"`；编号按合入序追加，[C15 §3.4-9](file:///d:/code/cs2j/docs/superpowers/specs/2026-09-28-c15-checked-unchecked-design.md#L160-L160) 约定的预期一行冲突）。`PassChainOrderTests`/`PassStubCatalogTests` 等链序与 stub 计数测试**不动**。

---

## 4. Core 组件设计

### 4.1 目录与文件所有权（C22 独占 `Rules/Nullable/`）

```
src\CSharpToJava.Core\NumericBoundary\Rules\Nullable\
  NullableLiftRule.cs        # INumericBoundaryRule（Order=30）：驱动 Analyze→Pass1→Pass2，
                             #   汇出 RuleFragment（统计/诊断/摘要片段/库需求恒 false）
  NullableLiftAnalyzer.cs    # T? 判定与解包（System_Nullable_T）、lifted 运算分类（IOperation +
                             #   OperatorMethod）、域分类（整数/无符号/浮点/decimal/bool/enum/C41）、
                             #   稳定引用谓词、转换四象限分类（决策 13）、自增目标分类、
                             #   表达式树/dynamic 谓词（复用根层）、E04 产物识别
  NullableLiftRewriter.cs    # Pass1 CSharpSyntaxRewriter（自底向上）：NL-1…NL-12 站点替换，
                             #   收集 PendingNullableLift（临时声明 + 锚点 + Order）
  NullableLiftExpansion.cs   # 纯函数渲染：守卫三元/布尔链/三值真值表/GOV/解包/lifted 转换/
                             #   自增与复合赋值/(T?) 保形 cast；enum 底层 cast 与窄类型 unchecked
  NullableLiftLifter.cs      # Pass2：VisitBlock 锚点前插入临时声明（复刻 NullCoalesceStatementLifter
                             #   与 C17 IncrementTargetLifter 契约），递归内嵌块
  NullableLiftSyntaxFacts.cs # 临时名 __cs2j_n_a/b/o/d、作用域碰撞避让、trivia 保持、
                             #   白名单语句/可提升上下文谓词、RewriterAnnotation
  NullableLiftDiagnostics.cs # CS2J5350-5359 码位常量与消息模板
  NullableLiftModels.cs      # 规则 Options 节（EnableNullable 默认 true，随 pass 启停）、强类型统计桶
```

测试新增（§7）：`tests/CSharpToJava.ProjectTests/NumericBoundaryNullableLiftUnitTests.cs`；fixture 向 C15 建立的 `tests/SampleNumericBoundarySolution/` 追加 `Nullable/` 子目录（Lib/App/Tests 各一），不新建 sln。

所有权纪律（同 C17）：只增 `Rules/Nullable/` 与自有测试；共享组件不够用时扩 C15 根层并携带 C15/C17 回归；不改 C15/C17 规则子目录文件。

### 4.2 对外契约（规则碎片，无独立入口）

- 无独立 Preprocessor 类：由族入口 `NumericBoundaryPreprocessor` 经规则发现调用；
- `RuleFragment` 内容：规则统计桶（§4.7）、规则诊断列表（带规则标签 `[NL-x]`）、摘要片段（§5-3）、`RequiresSupportLibrary = false`（恒）；
- 每文件两趟：Pass1 重写 + 收集提升，Pass2 锚点插入；输出前走族级格式化与重解析校验；
- SemanticModel 每文件独立，不缓存跨编译状态。

### 4.3 重写规则全集（NL-x）

| 规则 | 输入 | 输出 | 要点/边界 |
|---|---|---|---|
| NL-1 lifted 算术/位/移位二元 | `a op b`，任一操作数 `T?`，内置绑定 | 决策 3 守卫三元 | `/ %` 同形（除零仅在双方有值路径自然抛，两侧一致）；浮点同形；`uint?/ulong?` 内层真身无符号运算；enum 位运算见 NL-9；decimal 内层留 C34 |
| NL-2 lifted 一元 | `-a`/`+a`/`~a`（`T?`） | 决策 3 一元守卫 | `~` 对可空枚举归 NL-9 兜底 |
| NL-3 lifted 关系 | `a < > <= >= b` | 决策 4 布尔链 | 结果非可空 `bool`；单侧非空分支裁剪 |
| NL-4 lifted 相等 | `a == != b`（双侧 `T?`/单侧 `T`/null 字面量、值类型域） | 决策 5 | 引用类型相等不动；`bool?` 字面量子集由此覆盖；`IsStructNullLiteralComparison` 站点对预处理路径收口 |
| NL-5 `bool?` 三值逻辑 | `& \|`（`bool?` 域）、`^`、lifted `!` | 决策 6/7/8 | `&& \|\|` 不命中（C# 无此形态） |
| NL-6 GetValueOrDefault | `x.GetValueOrDefault()`/`(d)` | 决策 10 | 默认值参数 `d` 副作用单次；residual 零调用 |
| NL-7 解包转换 | `(T)x`，`x: T?`（同底层或其兼容解包） | `__t.Value`（不稳定先提升） | 异常语义等同 `.Value`；包装转换 `(T?)v` 原样 |
| NL-8 lifted 转换 | `(T?)x`，`x: S?`，`S≠T` | 决策 13-③ 守卫 + 内层 `(T)` | 含数值跨域、`E?↔U?`、checked 内层交 C15；非法组合源不编译 |
| NL-9 可空 enum 兜底 | E04 Warning 跳过残留的 `E?` 位运算/`~`/位复合 | 决策 12 兜底形态 | 复用 E04 底层 cast 与窄类型 `unchecked(...)` 包裹（随后由 C15 NB-1 剥壳）；稳定且已被 E04 重写的三元不命中 |
| NL-10 lifted 自增简单目标 | `x++/x--/++x/--x`（`x: T?`，简单目标） | 决策 15 | 后缀旧值临时 `__o` 值用替换；独立语句/值用两形态；枚举 `E?` 自增同形（内层 cast） |
| NL-11 lifted 复合赋值简单目标 | `x op= y`（`x: T?`，简单目标） | 决策 16 | 算术/位/`bool?` 三类内层 |
| NL-12 透传与豁免 | `Nullable.GetUnderlyingType(...)`、`typeof(T?)`、`(T?)v` 包装、`.HasValue`、`.Value` | 原样 | 决策 11/24；支撑工程目录全豁免 |
| NL-13 不支持站点 | 决策 17/20/21/22 所列 | **Error 诊断，节点原样保留**（本次转换失败） | CS2J5352-5356；不产「半改写」文件 |
| NL-14 residual 对账 | 输出树语义重扫 | §1.5 不变式 1；违例 CS2J5357（strict 即 Error） | 支撑工程目录豁免（同 C15 NB-13） |

### 4.4 目标形态示例集

```csharp
// NL-1 算术（稳定标识直取）
int? z = (a.HasValue && b.HasValue) ? (int?)(a.Value + b.Value) : (int?)null;

// NL-1 不稳定操作数（Pass2 在锚点语句前插入）
int? __cs2j_n_a = Next();
int? __cs2j_n_b = Other();
int? z = (__cs2j_n_a.HasValue && __cs2j_n_b.HasValue)
    ? (int?)(__cs2j_n_a.Value + __cs2j_n_b.Value) : (int?)null;

// NL-3 关系 → bool
bool r = a.HasValue && b.HasValue && a.Value < b.Value;

// NL-4 相等：双侧 / null 字面量 / bool? 字面量
bool r1 = a.HasValue ? (b.HasValue && a.Value == b.Value) : !b.HasValue; // a == b
bool r2 = a.HasValue;            // a != null
bool r3 = flag.HasValue && flag.Value;   // flag == true（等价 Objects.equals 特判）

// NL-5 三值
bool? r4 = ((a.HasValue && !a.Value) || (b.HasValue && !b.Value))
    ? false : ((!a.HasValue || !b.HasValue) ? (bool?)null : (bool?)true);  // a & b
bool? r5 = a.HasValue ? (bool?)!a.Value : (bool?)null;                     // !a

// NL-6 GOV
int r6 = a.HasValue ? a.Value : default(int);
int r7 = a.HasValue ? a.Value : __cs2j_n_d;

// NL-7/NL-8 转换
int r8 = a.Value;                                   // (int)a，a: int?
int? r9 = x.HasValue ? (int?)(int)x.Value : (int?)null;  // (int?)x，x: long?

// NL-10 自增：独立后缀 / 值用后缀 / 前缀
var __cs2j_n_o = x;
x = __cs2j_n_o.HasValue ? (int?)(__cs2j_n_o.Value + 1) : (int?)null;
y = __cs2j_n_o;                                    // y = x++; 的值用部分
++x;  // → x = x.HasValue ? (int?)(x.Value + 1) : (int?)null;

// NL-11 复合赋值
x = (x.HasValue && y.HasValue) ? (int?)(x.Value * y.Value) : (int?)null;  // x *= y

// NL-9 可空枚举兜底（窄底层保持 E04 的 unchecked 形态，随后由 C15 剥壳）
E? r10 = (__cs2j_n_e.HasValue)
    ? (E?)(E)(unchecked((byte)(~(byte)__cs2j_n_e.Value))) : (E?)null;
```

### 4.5 临时变量提升（Pass2）

- `PendingNullableLift(StatementSyntax Declaration, StatementSyntax Anchor, int Order)` 与 [NullCoalesceStatementLifter](file:///d:/code/cs2j/src/CSharpToJava.Core/NullCoalesceRewriter/NullCoalesceStatementLifter.cs) / C17 `IncrementTargetLifter` 同构；`NullableLiftLifter.VisitBlock` 锚点前按 Order 插入、递归内嵌块，作用域自然正确；
- 命名（文件内计数 + 作用域碰撞避让，沿用 `__cs2j` 前缀）：`__cs2j_n_a<N>` 左/接收者、`__cs2j_n_b<N>` 右操作数、`__cs2j_n_o<N>` 后缀旧值、`__cs2j_n_d<N>` GOV 默认值；声明一律 `var`（取静态类型，可空标注自然保留）；
- 稳定性谓词提取自 E04 [IsStableReference](file:///d:/code/cs2j/src/CSharpToJava.Core/EnumPreprocessor/EnumSyntaxRewriter.cs#L146-L170)（标识/字段且接收者链稳定），纯函数落本规则 SyntaxFacts（M2 可与 E04 上收到共享位，v1 自持，登记技术债）；
- 不可提升上下文（字段/构造初始化器、特性实参、查询子句、lambda 表达式体、无锚点表达式位）内出现不稳定 lifted 站点 → CS2J5355（与 C17 CS2J5339 同纪律），不生成重复求值代码；
- trivia：锚点语句首 trivia（含 `#if`、缩进）随首条声明走，仿 E09/E03 两遍范式。

### 4.6 诊断码段

使用 C15 [§4.5](file:///d:/code/cs2j/docs/superpowers/specs/2026-09-28-c15-checked-unchecked-design.md#L246-L246) 为 C22 预留的 **CS2J5350-5359**；族共享码（5302/5303/5304/5309）不另立。码位只追加不改号（§6 表）。

### 4.7 Options / Statistics

- 规则 Options 节：`rules.nullable.enable = true`（随 pass 启停，无独立 CLI 开关）；`strict` 复用族值；
- 规则统计桶（经 RuleFragment 汇入族 Statistics）：`LiftedBinarySites`、`LiftedUnarySites`、`LiftedRelationalSites`、`LiftedEqualitySites`（`NullLiteral`/`OneSided`/`BothNullable` 分桶）、`LiftedBoolLogicSites`（And/Or/Xor/Not 分桶）、`GetvalueOrDefaultSites`、`UnwrapConversions`、`LiftedConversions`、`LiftedIncrementSites`（Prefix/Postfix/Compound 分桶）、`NullableEnumBackstopSites`、`GuardedCheckedInnerSites`（守卫内被 C15 插 helper 的计数，联调用）、`TempsLifted`、`DeferredByReason: Dictionary<code,int>`。

---

## 5. CLI 集成（零注册表结构改动）

1. **无新 pass/适配器/开关/中间目录/支撑库**：入口完全继承 C15（`--numeric-boundary` / `--enable-pass numeric-boundary` / 通用 `preprocess --pass numeric-boundary`）；pass 默认关期间规则零执行；不进 [PassChainFactory.cs](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassChainFactory.cs)、不增 legacy toggle。
2. **注册表唯一改动**：PassCatalog 描述符 CatalogItems 追加 C22（§3.5）；`--list-passes` 自动反映；链序/stub 测试不动。
3. **统计汇入**：RuleFragment 自渲染摘要片段，建议族摘要行追加 `nullable-lift: A arith/U unary/R rel/E eq/B bool/G gov/C conv/I inc site(s), X enum backstop, T temp(s), D deferred`；[NumericBoundaryPassAdapter.cs](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/Adapters) 除既有族级注入判定外**零改动**（本规则库需求恒 false）。
4. **支撑库**：不声明、不释放、不过滤新增项；descriptor 的 `PassSupportLibrary.Cs2jUnsignedMath` 与本规则无关（守卫内 checked helper 属 C15 注入条件，沿用其既有判定）。
5. **verify**：继承描述符双默认开（verifyBuild/verifyTests=true）；三元展开与临时提升最易引入类型/作用域编译错误，verify build 是硬守门，不允许规则级关闭。
6. **单文件模式**：与 C15/C17 不同，本规则产物**全部自包含**（无 helper 调用），通用 `preprocess --pass` 单文件编译下可完整工作；仅当守卫内层含 C15 helper（checked 站点）时才受族 CS2J5309 闸门约束走项目模式。

---

## 6. 诊断策略

MSBuild 格式：`文件(行,列): warning/error CS2J53xx: [NL-x/分类] 描述`。

| 码 | 级别 | 含义 |
|---|---|---|
| CS2J5350 | Info | lifted 站点显式化（按 §4.7 分桶聚合计数），仅 verbose |
| CS2J5351 | Info | 临时变量提升计数（按角色），仅 verbose |
| CS2J5352 | Error | dynamic 绑定的 lifted 站点，无法静态定域（force 不解除，决策 22） |
| CS2J5353 | Error | 用户自定义 lifted 运算符（`OperatorMethod != null`）——目标形态待 C14，v1 响亮失败（决策 21） |
| CS2J5354 | Error | 表达式树 lambda 内 lifted 站点（改写改变树形态；force 不解除） |
| CS2J5355 | Error | 复杂目标（属性/索引器/事件/`?.`/不可提升表达式位）的 lifted 自增/复合赋值——M2 经 C17 RMW 接缝收编（决策 17、§4.5） |
| CS2J5356 | Error | C41 特殊数值域的 lifted 站点（Half/Int128/UInt128/nint 等） |
| CS2J5357 | Warning（strict 即 Error） | residual 闸门：输出仍存在未显式化的 lifted 内置运算/GOV/解包 cast（NL-14） |
| CS2J5358-5359 | — | 预留 |

原则同 C15 [§6](file:///d:/code/cs2j/docs/superpowers/specs/2026-09-28-c15-checked-unchecked-design.md#L281-L303)：无法等价表达即显式报错，**禁止注释/占位/原样透传流入 Java 产物**。转换器侧既有 nullable 特判（`Objects.equals` 臂、null 字面量比较臂）在本规则随默认开生效后对「经预处理的项目」成为不可达路径；删除/保留属后续 PR，直连 ConversionPipeline 的单文件入口继续以其为安全网，[NullableBoolComparisonTests.cs](file:///d:/code/cs2j/tests/CSharpToJava.Tests/NullableBoolComparisonTests.cs) 等既有测试一行不改。C15 CS2J5325 与 C17 CS2J5335 在本规则合入后对可解站点停用（码位保留），停用条件以共处单测钉死。

---

## 7. 测试计划

全部以 `-c Release` 执行；先写单测矩阵再实现。

### 7.1 Core 单测矩阵

新增 `tests/CSharpToJava.ProjectTests/NumericBoundaryNullableLiftUnitTests.cs`（内联源码 → `CSharpCompilation` → 族 Rewrite（C15+C22，C17 存在时三规则同开）→ 忽略空白文本断言 + `AssertOutputCompiles`）：

1. **lifted 算术**：int/long/float/double 的 `+ - * / %` 守卫形态；null 字面量操作数；单侧非可空；一元 `- + ~`；`uint?/ulong?` 内层真身无符号运算（与 unsigned 产物共处）；嵌套 `a + b + c` 的临时链与求值序。
2. **三值 `bool?`**：`&`/`|` 九格真值表文本与运行时断言；`^` 与 `!` 的 null 传播；`&&` 源（若构造得出）不命中；`flag == true/false`、双侧 `bool? ==`、`!=`。
3. **关系/相等**：`< > <= >=` 四种 + null 即 false；双侧 `T?` 相等九格（含 null==null=true）；null 字面量两侧；单侧 `T`。
4. **`??` 边界**：`(a + b) ?? 0` 经 E03→本规则双 pass 组合；`a ?? b` 单独零改写（E03 产物形态保持）；嵌套 `??`。
5. **GOV/Value/HasValue**：`GOV()`/`GOV(d)`（`d` 带副作用断言仅求值一次）；`.HasValue`/`.Value` 原样。
6. **可空枚举**：`E?` 的 `& | ^ ~`、复合赋值（稳定直取，确认与 E04 产物不重复改写）；E04 跳过面（属性返回 `E?`）由 NL-9 兜底；窄底层 `unchecked` 内层在 C15 合入基上被剥壳；`E?` 比较/相等。
7. **转换**：`(int?)v` 包装原样；`(int)x` 解包 → `.Value`；`(int?)longVar` lifted 跨域；`(byte?)intVar`；`(E?)u`；checked 区内 lifted 转换内层出现 C15 helper；非法组合不构造。
8. **自增/复合**：简单目标前缀/独立后缀/值用后缀（旧值临时、null 时旧值为 null）；复合赋值 `+= *= &=`；属性目标 → CS2J5355；初始化器内值用后缀 → CS2J5355。
9. **副作用单次求值**：`M() + N()`、`arr[Next()] < x`、`Get().P | flag`、`GOV(Next())`、自增接收者带方法调用——计数桩断言各子表达式求值次数与源序与原生 C# 一致。
10. **嵌套/混合**：lifted 算术嵌套 lifted 比较；`bool? r = (a + b) > 0 & flag`；条件访问产出的 `bool?` 入比较；E03/E04/unsigned/C15 产物共处单树。
11. **不支持族**：dynamic → CS2J5352；用户 struct 的 lifted 自定义运算符 → CS2J5353；表达式树 → CS2J5354；`Int128?` → CS2J5356；decimal? 守卫产出但内层裸 `+` 保留（不报错）。
12. **工程性质**：幂等（二次零 diff）；干净文件字节一致 + BOM 保持；支撑工程路径 clean copy；residual CS2J5357 正/反例。

### 7.2 fixture 与运行时对照原生 C#（.NET 半区守门）

向 C15 建立的 `tests/SampleNumericBoundarySolution/` 追加 `Nullable/` 子目录（`SampleNullableLib` 模块或 Lib 子目录 + App + Tests，net10.0，Content Include 进 [CSharpToJava.ProjectTests.csproj](file:///d:/code/cs2j/tests/CSharpToJava.ProjectTests/CSharpToJava.ProjectTests.csproj)）：

- **原生预言机**：同一 fixture 内成对声明——`NativeAdd(int? a, int? b) => a + b;`（**保持 C# 真身 lifted 语法**）与被本规则重写后的等价方法；xunit `[Theory]` 在 null/有值矩阵（含边界值、除零、溢出边界）上断言两者结果逐格一致。矩阵覆盖：算术/一元、关系、相等九格、`& | ^ !` 全真值表、GOV 两形、`.Value` 在 null 下两方法同抛 `InvalidOperationException`、lifted 自增（null 保持/有值自增/后缀旧值）、复合赋值、lifted 转换（含溢出转换）。
- **运行时行为断言直接跑在去糖后的 C# 上**：守卫写错（如三值表一格写反、null 路径取了 `.Value`）在 .NET 侧立即红。
- 项目级测试 `NumericBoundaryNullableProjectTests.cs`：`result.Success`、§4.7 统计计数、中间树文本断言（含 `HasValue`/`.Value`、不含 lifted 裸运算符）、中间树 `dotnet build` 与 `dotnet test -c Release` 退出码 0；本规则不触发支撑库注入（断言中间树根无 `Cs2jUnsignedMath` 新增——checked 组合站点除外，该情形属 C15 注入）。

### 7.3 Release build/test

- `dotnet build -c Release`（CSharpToJavaConverter.slnx 全绿）；
- `dotnet test -c Release` 三个工程：CSharpToJava.Tests、CSharpToJava.ProjectTests、CSharpToJava.ConversionTests。

### 7.4 Java 半区冒烟（默认开门禁之一）

- 代表性样本（lifted 算术 + 三值 `bool?` + 关系 + GOV + 自增 + 可空枚举）经完整 C#→Java 链路产出可编译 Java；
- null 输入行为矩阵对照：null 传播得装箱 null（不 NPE）、lifted 关系得 `false`、`false & null` 得 `false`、GOV 得默认值；
- 回归锁定已闭合面：`TypeHelper.getUnderlyingType(Integer.class)`（[NullableGetUnderlyingTypeTests](file:///d:/code/cs2j/tests/CSharpToJava.Tests/NullableGetUnderlyingTypeTests.cs)）、`T?` 基类 runtime class（[NullableBaseTypeRuntimeClassTests](file:///d:/code/cs2j/tests/CSharpToJava.Tests/NullableBaseTypeRuntimeClassTests.cs)）；
- 记录近似：手写 `.Value` null 路径在 Java 为 `NullPointerException` 而非 `IllegalStateException`（决策 11，开放问题 3）。

### 7.5 residual 闸门与上游共处

1. 语义模型扫描型测试：输出树所有非支撑工程 .cs 编译后遍历 `IBinaryOperation`/`IUnaryOperation`/`IIncrementOrDecrementOperation`/`ICompoundAssignmentOperation`/`IInvocationOperation`/`IConversionOperation`，按 §1.5 谓词断言零残留；违例 CS2J5357 strict 退出码非零；
2. 上游共处回归：对 enum/unsigned/null-coalesce 既有 fixture（SampleUnsignedSolution、SampleProject 可空枚举样例）启用本 pass 后中间树 build/test 全绿；E04 三元产物、E03 临时形态、unsigned helper 调用零二次改写，各 pass 统计零变化（本规则自计 Backstop/Temp 桶除外）；
3. 转换器侧测试不回归：直连 ConversionPipeline 的 [NullableTests.cs](file:///d:/code/cs2j/tests/CSharpToJava.ConversionTests/Categories/NullableTests.cs) 等四个既有测试文件一行不改。

### 7.6 CLI / 一致性

- pass 默认关：不产生目录/注入/耗时；`--list-passes` numeric-boundary 行 catalog 项含 C22；规则无独立开关；
- CatalogItems 单行追加后 [ProjectPreprocessorsConsistentTests.cs](file:///d:/code/cs2j/tests/CSharpToJava.Tests/ProjectPreprocessorsConsistentTests.cs) 既有断言保持绿色（不断言 catalog 文本等值，否则随本任务单行更新）。

---

## 8. 默认开启验收门槛

本规则无独立 descriptor，不单独翻默认开；`numeric-boundary` 的 `defaultEnabled: false → true`（C15 门槛）必须**额外**满足本规则各项：

1. slnx `dotnet build -c Release` 与三个测试工程 `dotnet test -c Release` 全绿（含 §7 全部新增单测/fixture/residual/CLI）。
2. fixture 中间树 build + test 全绿；§7.2 原生预言机矩阵全格一致；幂等、字节复制、BOM、residual CS2J5357 闸门全绿。
3. **Java 半区就绪**：§7.4 冒烟通过——null 矩阵无 NPE/错值；四个已闭合特判测试在预处理路径与直连路径双态成立；无新增 compat 方法依赖（决策 23）。
4. csharp-sdk（只读训练集）启用扫描：lifted 站点总量与分桶、CS2J5352-5356 命中清单；**每条 Error 人工定性**（真不支持 vs 分析器过保守），过保守项修到清零或进入明确后续；重点产出三值 `bool?`、GOV、可空枚举、自定义 lifted 运算符四类命中率，作为开放问题 1/2 的拍板输入。
5. sdk 基线不退化：启用后既有 fixture 与 sdk 抽样工程中间树 build/test 不劣于未启用；E03/E04/unsigned 统计零变化；无支撑库注入增量（守卫零库依赖）。
6. `--list-passes` 描述、README/ROADMAP、本设计与 plan 文档齐备。

---

## 9. 里程碑

| 里程碑 | 内容 | 预估规模 |
|---|---|---|
| M0 族内接缝（C15 合入基上） | 规则发现注册（Order=30）、`Rules/Nullable/` 骨架（Models/Analyzer/SyntaxFacts/Rule）、T? 解包与 lifted 分类谓词、CatalogItems 追加 C22、空树端到端跑通 + residual 扫描器骨架 | Core ~250 行 |
| M1 核心守卫 | NL-1/2/3/4/5 + Pass2 临时提升（算术/一元/关系/相等/三值 bool?）+ 稳定性谓词 + §7.1-1/2/3/9/10/12 单测；E03/E04/unsigned 共处回归 | ~550 行 |
| M2 成员面/转换/enum 兜底/自增 | NL-6/7/8/9/10/11 + fixture 原生预言机矩阵（§7.2）+ CS2J5325/CS2J5335 简单目标停用共处测试 + checked 组合（GuardedCheckedInnerSites）+ §7.4 Java 冒烟 | ~500 行 + fixture |
| M3 复杂目标接缝与闸门打磨 | 经 C17 RMW 接缝收编 CS2J5355（若 C17 已合入）+ CS2J5352-5356 错误站点打磨 + residual 全语义扫描 + Release build/test 全绿 + sdk 试扫 | ~300 行 |
| M4 默认开评审 | sdk 全量扫描与 DeferredByReason 报表、开放问题拍板（NullableHelper/异常类型/自定义 lifted）、本设计定稿与 plan 文档；门槛汇入 C15 族级默认开评审 | 文档/数据 |

---

## 10. 风险

1. **三值/提升真值表写错即静默错值**（[catalog §4 风险 12](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L931-L931) 同族）：`false & null`、null==null、lifted 关系返 bool 都是反直觉格。缓解：真值表进设计（§2）+ 原生预言机全格矩阵（§7.2）双保险；residual 语义扫描兜底漏改。
2. **副作用重复求值**：不稳定操作数若漏提升，`M() + M()` 会两次调用。缓解：稳定性谓词单点 + 计数桩单测（§7.1-9）；不可提升上下文宁可 CS2J5355 失败不生成半成品。
3. **与 E04 的双重改写/漏底**：E04 三元被误判为 lifted 会重复包裹；E04 Warning 跳过面漏接则残留。缓解：规则只认 `IOperation` lifted 绑定（三元条件表达式不命中）；NL-9 以 E04 skip 同款谓词反选；enum 共处为硬回归（§7.5-2）。
4. **与 C15 checked 的内层交互**：守卫内层 helper 插错位置（插在守卫外）会改变 null 路径（null 也做溢出检查）。缓解：形态锁定为「守卫内 lambda 位点」，`GuardedCheckedInnerSites` 联调桶 + 溢出/null 交叉矩阵断言；CS2J5325 停用以共处单测钉死。
5. **临时提升的编译正确性**：锚点选择/作用域/trivia 错误导致中间树编译失败。缓解：lifter 复刻 E03 冻结契约；verify build 双开硬守门；幂等测试。
6. **链式展开膨胀**：长 lifted 链产生多层三元与临时，可读性与编译规模上升。缓解：仅不稳定点提升（稳定标识零临时，覆盖绝大多数局部变量场景）；fixture 保留深链样本人工评审，不做过度优化。
7. **`.Value` 异常类型近似**：Java 半区手写 `.Value` null 抛 NPE 而非 `IllegalStateException`，`catch(InvalidOperationException)` 粒度有差。缓解：v1 文档化（决策 11）；sdk catch 命中扫描结果决定是否上 NullableHelper（开放问题 3）。
8. **转换器内合成 GOV 不经过本规则**：[LinqRewriter.Rules.cs:1465](file:///d:/code/cs2j/src/CSharpToJava.Core/LinqRewrite/LinqRewriter.Rules.cs#L1465-L1465) 合成的 `.GetValueOrDefault()` 发生在转换器内部、预处理链之后，本规则看不到。缓解：登记为转换器侧后续协调项（§11），Java 冒烟覆盖该路径现状；不承诺在本任务闭合。
9. **C05 struct enum 双态**：真身 enum 与 C05 struct 形态下 `E?` 的 cast/FromValue 形状不同。缓解：分类全部基于 SemanticModel 符号（[C05 R10](file:///d:/code/cs2j/docs/superpowers/specs/2026-09-28-c05-enum-declaration-design.md#L470-L470) 同款立场）；双 pass 组合 fixture 为 M2 硬项。
10. **decimal? 守卫内裸算**：C34 未落地时 `decimal?` 算术经本规则后 Java 半区仍错（守卫对、内层错）。缓解：不报错但测试与发布说明明确依赖 C34；与 C17 CS2J5334 同立场，sdk 命中率驱动 C34 排期。

---

## 11. 并行协调

- **C15 checked/unchecked（锚点，硬前置）**：本任务在 C15 合入后的主干上开工；使用其 `NumericBoundary/` 根层（Analyzer/规则管线/格式化/码段/适配器）；CS2J5325 对可解站点在本规则合入后停用；守卫内 checked 内层由其后扫收编；根层接缝变更携带 C15 回归。
- **C17 自增（同族，Order=20）**：CS2J5335 的 nullable 简单目标由本规则 NL-10/11 收口；复杂目标 v1 由本规则发 CS2J5355，M2 经 C17 RMW 渲染接缝收编；两规则经同一 Analyzer 的站点标记交接，不重复处理；工作集仅 `Rules/Nullable/` vs `Rules/IncrementTargets/`，零文件重叠；CatalogItems 字符串按合入序追加。
- **C34 decimal（同族，未落地）**：`decimal?` 守卫/内层边界（决策 19）；C34 落地时只收编内层裸算，不改守卫；本规则不为其预留码（沿用 C15 5360-5379 段）。
- **C41 特殊数值（同族，未落地）**：`Half?/Int128?/nint?` 站点本规则发 CS2J5356 指路；C41 落地后转为守卫 + 其算术核。
- **E04 enum（已实现，链中先于 unsigned→本规则）**：稳定 lifted 位运算由 E04 完成，本规则透传；不稳定残留 NL-9 兜底（以临时提升取代 Warning 跳过）；`NullableLiftedRewritten` 统计与本规则 `NullableEnumBackstopSites` 互不重复计数；M2 评估把稳定性谓词上收共享。
- **E03 null-coalesce（已实现）**：`??` 语法边界清晰（决策 9）；双 pass 组合（lifted 运算在 `??` 两侧/内部）为 fixture 硬项；lifter 形态沿用其冻结契约。
- **C05 enum 声明（未落地/设计已确认）**：struct 形态 `Nullable<E>` 经通用规则自然生效（[C05 §11 C22 行](file:///d:/code/cs2j/docs/superpowers/specs/2026-09-28-c05-enum-declaration-design.md#L481-L481)）；双 pass 顺序组合测试进 M2。
- **E09 unsigned（已实现）**：`uint?/ulong?` 真身输入由 NL-1 接住，内层是真身无符号运算；checked 区内层交 C15；unsigned 跳过 nullable 的契约（[UnsignedTypeSyntaxRewriter.cs:635-639](file:///d:/code/cs2j/src/CSharpToJava.Core/UnsignedTypeRewriter/UnsignedTypeSyntaxRewriter.cs#L635-L639)）不变。
- **C35 null 条件（未落地）**：`?.`/`?[]` 产 `T?` 后进入本规则的 lifted 站点按一般规则处理；`?.` 目标的自增走 CS2J5355 指路 C35/C17。
- **转换器侧 LinqRewriter**：其合成 GOV（§10-8）不经过预处理链，登记转换器侧后续 PR；不阻塞本任务。
- **热文件冲突解法**：生产代码改动集 = `NumericBoundary/Rules/Nullable/*.cs`（全新目录）+ C15 根层小接缝（若需要，带 C15 回归）+ PassCatalog CatalogItems 一行 + 测试/fixture 追加；与 C17/C34/C41 无文件重叠；fixture 以 `Nullable/` 子目录隔离；不动 `PassStubCatalog.cs`、适配器、`PassChainFactory.cs`、Program.cs。

---

## 12. Worktree 分配（只写不执行）

- 分支：`feature/preproc-c22-nullable-value`
- 目录：`.worktrees/preproc-c22-nullable-value`
- base：`953293b9`（`docs: update doc base commit`，已核实存在）；**实现前先把 C15 分支（`feature/preproc-c15-checked-unchecked`）合入该 base 或在其合入主干后 rebase**——本任务硬依赖 C15 的 `NumericBoundary/` 根层与规则管线
- 创建命令（仓库根、PowerShell；本设计阶段不执行，实施者按 C15 合入时序执行）：

```powershell
git worktree add .worktrees/preproc-c22-nullable-value -b feature/preproc-c22-nullable-value 953293b9
```

- worktree 内交付面遵循 foundation plan「并行开发交付包模板」（[preprocessor-foundation_plan.md:108-117](file:///d:/code/cs2j/.trae/documents/preprocessor-foundation_plan.md#L108-L117)）：Core `NumericBoundary/Rules/Nullable/` 八个文件 + C15 根层接缝（如需，带回归）+ PassCatalog CatalogItems 单行 + 本设计文档与后续 plan（`docs/superpowers/plans/2026-09-29-c22-nullable-value.md`）+ Core 单测 + fixture `Nullable/` 子目录 + 原生预言机矩阵 + Release build/test + residual 闸门 + CLI 一致性；不新建支撑工程/compat 类；不触转换器代码；不做任何 git 写操作由设计阶段承诺（实施阶段的提交遵循仓库常规评审流程）。

---

## 附：关键开放问题（供评审拍板）

1. **是否需要 `NullableHelper` 支撑库**：v1 推荐纯内联守卫（零新 Java 面，决策 23）；若 sdk 扫描显示用户自定义 lifted 运算符（决策 21）或 `.Value` 异常类型区分（决策 11）高命中，是否提前建 `Cs2jNullable`（方法调用天然单次求值，可消灭部分临时提升，但要新增 PassSupportLibrary/释放/过滤/指纹/Maven 全套接线与 compat 类）。
2. **复杂 nullable 读-改-写目标的支持时点**：v1 CS2J5355 失败、M2 等 C17 RMW 接缝（推荐）；还是在本规则内自持最小属性/索引器读-改-写展开（提前覆盖但与 C17 渲染逻辑重复）。
3. **手写 `.Value`/解包的异常类型**：接受 Java 拆箱 NPE 近似（v1 推荐，零 compat 新增），还是在 compat 引入统一抛 `IllegalStateException` 的取值助手并让解包站点改发调用（需同步评估 catch 粒度与 ExceptionApiRewriter 现状）。
4. **`bool?` 既有 `Objects.equals` 特判的退役节奏**：本规则默认开后该转换器臂对预处理项目不可达；是随 C22 同 PR 删除转换器特判（违反「不改转换器」边界），还是严格留待族级消费 PR（推荐后者，同 C15 §6 模式）。
5. **E04「不稳定即跳过」纪律是否同步升级**：NL-9 只兜底 E04 残留在枚举位运算面；E04 本体的 Warning-skip 是否在本任务顺手改为临时提升（跨 pass 改 E04 代码与测试），还是长期维持「E04 稳定直取 + C22 兜底」双轨（推荐后者，隔离改动半径，M2 以命中率复核）。
