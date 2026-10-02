# C17 复杂自增/自减目标归一（numeric-boundary 族增量规则）设计

**日期**: 2026-09-29
**范围**: 在 C15 创建的 `numeric-boundary` pass 内，以**族内增量规则**形态新增 `IncrementTargets` 规则（Core `NumericBoundary/Rules/IncrementTargets/` + 自有测试 + fixture 追加），把属性/索引器/enum/decimal/无符号残余/IntPtr/自定义 `++/--` 等转换器不可直映的目标归约为「单次求值的读-改-写显式语句」；本任务**不注册新 pass、不翻转 stub、不改任何 C#→Java 转换代码**
**状态**: 2026-10-01 进入实施（worktree base 前移至 dev-3.x `0ba86416`；该基线 C15 已合入、pass 已 Implemented 且默认开，C34 DecimalOps 亦已合入，见 §12）；原状态：设计稿，待评审；实施前置 = C15（锚点）分支合入
**校订（2026-09-29）**: ①全部 file:/// 链接按当前仓库逐一复核：[UnaryExpressionTransformer.cs](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/UnaryExpressionTransformer.cs) L154-193 各 throw 行（L169/L173/L176/L182/L184/L189/L192、enum 判定 L181-182）、UnsignedPostfixRewriter（L37-39/L57-62/L66-115/L140-148）、UnsignedTypeSyntaxRewriter（L228-246/L248-255/L182-204/L879-989，原锚点即准确）、EnumSyntaxRewriter、NullCoalesce、BinaryExpressionTransformer、compat Decimal.java 行号均核实无误；②catalog 文档内引用行号按当前重算（§3.3 数值边界族 L1084、§3.1 建议链 L1030-1031、§5.1 L1127-1134、§4 风险 8 L1106/风险 12 L1110）；③numeric-boundary stub 实在 [PassStubCatalog.cs:150-159](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassStubCatalog.cs#L150-L159)（原误引 L162-171），catalogItems `"C15, C17, C22, C34, C41"`、RunAfter=unsigned、Stage=Expression 均核实；④member-desugar 现状改写：C11/C12/C13/C14 **已实现**（单一 member-desugar pass，默认关，[PassCatalog.cs:230-241](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassCatalog.cs#L230-L241)），属性/索引器**语句位**自增已被去糖、值消费位致 plan 失败（CS2J5119/CS2J5137），`op_Increment`/`op_Decrement` 经 CS2J5181 显式移交 C17——正文中「未落地/未合入」类表述按此更新；⑤更正链序事实：当前注册表全序（静态推演）member-desugar 在 numeric-boundary **之前**（§3.3）；⑥新增决策 21 与开放问题 6：C# 14 用户定义复合赋值运算符边界；⑦跨设计文档锚点更正（C11 PD-6 L173、C12 IX-8 L189/D9 L66、C14 开放问题 4 L395、OC-8 L188）。
**族属**: catalog P2「数值边界族」（[preprocessor-catalog.md §3.3](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L1084-L1084)）。**C15 是该族锚点**：[PassStubCatalog.cs:150-159](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassStubCatalog.cs#L150-L159) 的 `numeric-boundary` stub（catalogItems `"C15, C17, C22, C34, C41"`、RunAfter=unsigned、Stage=Expression）由 C15 翻转并创建 `src/CSharpToJava.Core/NumericBoundary/`（含 `Rules/` 子目录预留、CS2J53xx 诊断码段、`NumericBoundaryPassAdapter`；2026-09-29 核实该目录尚不存在）；C17 无独立 stub，只在 C15 合入后向 `Rules/IncrementTargets/` 增量

---

## 1. 背景与目标

### 1.1 `++/--` 的 C# 语义：编译器隐藏的读-改-写

C# 前缀/后缀自增自减不是一条「原地加法」，而是编译器规定的一次**读（取值）→ 算（求新值）→ 写（回目标）**序列：

- **后缀** `x++` / `x--`：对目标变量 `x` 求值得**旧值**作为表达式结果；用旧值经选定的「递增运算」求新值并写回 `x`；副作用（写回）在操作数求值完成后、外围表达式继续求值前发生。
- **前缀** `++x` / `--x`：同样读-算-写，表达式结果是**新值**。
- **递增运算的选择**：内建整数为底层域 `± 1`（窄类型运算经提升在 int/long 上执行，写回时窄化）；enum 为「底层整数递增后显式转换回 enum」；decimal 为 decimal 域 `±1m`；nint 为平台尺寸域；绑定 `op_Increment`/`op_Decrement` 时是一次用户方法调用，且**结果经可能存在的用户转换回转到目标类型**（C# 规范的 ++ 运算符选择规则）。
- **checked 上下文**：内建整数与 enum 的 `++/--` 接受溢出检查（C15 [设计 §1.1](file:///d:/code/cs2j/docs/superpowers/specs/2026-09-28-c15-checked-unchecked-design.md#L20-L20) 已核实）；用户运算符是方法调用，不受 checked/unchecked 影响。
- **C# 14 用户定义复合赋值运算符与本规则的界分（2026-09-29 补，catalog 六次补全 C14/C52 已核）**：C# 14 起 `operator +=` 等复合赋值可由用户直接声明，`a += b` **不再必然等价于读-改-写拆解** `a = a + b`，物化前须经 SemanticModel 查显式复合运算符绑定；但 `++/--` **不存在用户定义复合形态**——自定义自增的唯一绑定仍是 `op_Increment`/`op_Decrement`，复合运算符不参与 `++/--` 决议。故本规则只拥有 `++/--` 站点，不拆解任何复合赋值（决策 21）。
- **关键隐藏约束——单次求值**：当目标是属性/索引器时，接收者、索引实参、getter 都**严格求值一次**（C# 变量引用先求值得存储位置；随后 getter 读一次、setter 写一次），索引实参按从左到右顺序求值。任何 `p.Prop = p.Prop + 1` 式的「文本展开」都必须先把带副作用的接收者/实参提升为临时变量，否则静默改变求值次数。

Java 侧只对「简单可变左值的内建数值」保留 `++/--` 语义（局部/参数/字段/数组元素，且窄类型靠复合赋值的隐式窄化）；属性是 getter/setter 方法调用、索引器是 `getItem/setItem`、enum 是对象、decimal 是 `Decimal` 引用类型、无符号以带符号类型表示、自定义运算符无运算符语法——这些目标上 Java 根本不存在合法的 `++/--`。因此与 checked/unchecked 同理（[catalog §5.1](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L1127-L1134)）：**读-改-写归约必须在 C#→C# 层完成**，Java 侧只做 1:1 翻译。

### 1.2 现状：逐目标 fail-fast，且只堵不疏

[UnaryExpressionTransformer.cs:154-193](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/UnaryExpressionTransformer.cs#L154-L193) 的 `EnsureSimpleIncrementTarget` 对不可直映目标逐个抛 `CS2J normalization required`：

| 行 | 判定（syntax + symbol/type） | 失败消息 | 目标种类 |
|---|---|---|---|
| [L169](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/UnaryExpressionTransformer.cs#L169-L169) | `MemberAccessExpressionSyntax` 且符号为 `IPropertySymbol`/`IEventSymbol` | increment/decrement on a property or event | **属性 / 事件**（与类型无关：int 属性同样失败） |
| [L173](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/UnaryExpressionTransformer.cs#L173-L173) | `ElementAccessExpressionSyntax` 且符号为 `IPropertySymbol` | increment/decrement on an indexer | **索引器** |
| [L176](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/UnaryExpressionTransformer.cs#L176-L176) | 其余语法形态（指针解引用、指针成员访问、条件访问等） | increment/decrement target `{kind}` | **指针目标（C06）/ 其他** |
| [L182](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/UnaryExpressionTransformer.cs#L182-L182) | `TypeKind.Enum` | increment/decrement on an enum target | **enum（含 `[Flags]`）** |
| [L184](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/UnaryExpressionTransformer.cs#L184-L184) | `SpecialType.System_Decimal` | increment/decrement on a decimal target | **decimal** |
| [L189](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/UnaryExpressionTransformer.cs#L185-L190) | `UInt32/UInt64/UInt16/Byte/IntPtr/UIntPtr` | unsigned/narrow integer target | **无符号窄类型 / 平台尺寸整数** |
| [L192](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/UnaryExpressionTransformer.cs#L191-L192) | `IMethodSymbol.MethodKind == UserDefinedOperator` | user-defined increment/decrement operator | **自定义 `op_Increment`/`op_Decrement`** |

同文件 [L141-149](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/UnaryExpressionTransformer.cs#L137-L152) 另对 enum/decimal 上的一元 `-/+ /~` fail-fast（非本规则范围，仅记录同站点；用户自定义一元在 L141-144、enum L148-149、decimal L150-151）。

> **2026-09-29 现状校注（member-desugar 已实现）**：C11/C12/C13/C14 已随单一 `member-desugar` pass 落地（[PassCatalog.cs:230-241](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassCatalog.cs#L230-L241)，`PassStatus.Implemented`、默认关）。启用该 pass 后：属性/索引器**语句位** `++/--` 已在 C# 层方法化（[PropertyUseSiteRewriter.cs:295-331](file:///d:/code/cs2j/src/CSharpToJava.Core/MemberDesugar/Properties/PropertyUseSiteRewriter.cs#L295-L331)、复杂接收者提升 [L180-210](file:///d:/code/cs2j/src/CSharpToJava.Core/MemberDesugar/Properties/PropertyUseSiteRewriter.cs#L180-L210)、[IndexerUseSiteRewriter.cs:212-237](file:///d:/code/cs2j/src/CSharpToJava.Core/MemberDesugar/Indexers/IndexerUseSiteRewriter.cs#L212-L237)），这些站点不再到达上表；但 pass 默认关，且**值消费位**站点会令整个 plan Infeasible（属性 CS2J5119 [PropertyRule.cs:333-344](file:///d:/code/cs2j/src/CSharpToJava.Core/MemberDesugar/Properties/PropertyRule.cs#L333-L344)、索引器 CS2J5137 [IndexerRule.cs:205-219](file:///d:/code/cs2j/src/CSharpToJava.Core/MemberDesugar/Indexers/IndexerRule.cs#L205-L219)）而保留真身 `++`，自定义 `op_Increment`/`op_Decrement` 经 CS2J5181 显式挂起（§3.2、决策 11）。故默认链路与上述残余上，本表 fail-fast 仍是现行安全网，行号 2026-09-29 逐一复核无误。

### 1.3 E09 已覆盖的部分与明确残余

E09（unsigned pass，已实现）只解决了**无符号简单目标**的一部分，是本规则的直接前置参照：

- **前缀**：[UnsignedTypeSyntaxRewriter.cs:905-989](file:///d:/code/cs2j/src/CSharpToJava.Core/UnsignedTypeRewriter/UnsignedTypeSyntaxRewriter.cs#L905-L989) 把 checked 区外、非常量、非 nullable、非 enum、域非 Native 的安全目标（[IsSafeAssignmentTarget, L228-246](file:///d:/code/cs2j/src/CSharpToJava.Core/UnsignedTypeRewriter/UnsignedTypeSyntaxRewriter.cs#L228-L246)：局部/参数/字段标识，接收者无副作用的字段访问）上的 `++u/--u` 改写为 `u = UnsignedMath.AddUInt32(u, 1)` 形态；**属性/索引器目标、checked 区、nint 域全部不改**。
- **后缀**：[UnsignedTypeSyntaxRewriter.cs:879-903](file:///d:/code/cs2j/src/CSharpToJava.Core/UnsignedTypeRewriter/UnsignedTypeSyntaxRewriter.cs#L879-L903) 仅打标注，由第二遍 [UnsignedPostfixRewriter.cs](file:///d:/code/cs2j/src/CSharpToJava.Core/UnsignedTypeRewriter/UnsignedPostfixRewriter.cs) 提升；该遍只处理**无条件执行语句**（[L140-148](file:///d:/code/cs2j/src/CSharpToJava.Core/UnsignedTypeRewriter/UnsignedPostfixRewriter.cs#L140-L148)：ExpressionStatement/LocalDeclaration/Return/Throw/Yield），并在注释中明确承认条件语句中的 `i++` 不能提前拆出（[L37-39](file:///d:/code/cs2j/src/CSharpToJava.Core/UnsignedTypeRewriter/UnsignedPostfixRewriter.cs#L37-L39)）。落在 if/while/do/for 条件（[IsInRepeatedEvaluationContext, L182-204](file:///d:/code/cs2j/src/CSharpToJava.Core/UnsignedTypeRewriter/UnsignedTypeSyntaxRewriter.cs#L182-L204)）、匿名函数/局部函数/查询中的无符号后缀保持原生 `i++`，到主转换器仍按 L189 fail-fast。
- E09 的两遍形态、`__cs2j_unsigned_` 临时名、trivia/`#if` 保持纪律（[UnsignedPostfixRewriter.cs:66-115](file:///d:/code/cs2j/src/CSharpToJava.Core/UnsignedTypeRewriter/UnsignedPostfixRewriter.cs#L66-L115)）是本规则 TempLifter 的直接范式。

**结论（catalog C17 证据）**：E09 之后，属性/索引器/事件/enum/decimal/nint/自定义运算符目标，以及无符号目标在条件位、复合表达式中的残余，仍全部硬失败。**2026-09-29 补**：member-desugar（默认关）启用后，属性/索引器语句位自增提前消失于 C11 PD-6/C12 IX-8，但其值消费位残余（CS2J5119/CS2J5137 跳过）、外部元数据成员、ref 返回索引器形态及 enum/decimal/nint/自定义核仍硬失败，残余集合只是收窄而非消失。

### 1.4 等价目标：读-改-写显式化

对任意 `++/--` 站点，目标形态等价于（`INC`/`DEC` 为按 1.1 选定的递增/递减运算，`<v>` 为表达式结果的消费点）：

```text
后缀：  var __old = READ(target);                 // 接收者/实参/getter 单次求值
        WRITE(target, INC(__old));
        <v> = __old
前缀：  WRITE(target, INC(READ(target)));
        <v> = 新值（INC 的结果）
```

本规则的全部工作是：①按语义模型把目标分类为可绑定的存储位置；②把 READ/WRITE 渲染成合法 C#（属性赋值语法、索引器赋值语法、数组元素、ref 别名、去糖方法调用）；③保证接收者/索引实参/getter/setter 各一次、求值序不变；④把「算」渲染成正确域的 C# 表达式（内建窄化 cast、enum cast 链、`±1m`、`UnsignedMath.*`、checked helper、自定义运算符去糖方法调用）；⑤在语句级提升临时变量；无法等价时给 CS2J533x Error，禁止注释/占位/原样透传（[catalog §4 风险 12 第一原则](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L1110-L1110)）。

### 1.5 目标

1. 消灭 [UnaryExpressionTransformer.cs:154-193](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/UnaryExpressionTransformer.cs#L154-L193) 中**本规则所属**的全部 fail-fast 面（经预处理路径不可达；直连单文件入口保留安全网，见 §6）；
2. 属性/索引器目标在任意数值/enum/自定义元素类型上的前缀、独立后缀、可提升值用后缀，全部产出单次求值的读-改-写；
3. enum 自增（默认/unchecked 上下文的全部目标形态；checked 复杂目标承接 C15 的 CS2J5324 移交，见 §2 决策 17）、decimal 形态展开、E09 残余无符号站点收编；
4. 给出 `op_Increment`/`op_Decrement` 归属的**结论建议**（回答 [C14 设计开放问题 4](file:///d:/code/cs2j/docs/superpowers/specs/2026-09-28-c14-operator-desugar-design.md#L395-L395)；C14 已落地并在 [§11 C17 协调节](file:///d:/code/cs2j/docs/superpowers/specs/2026-09-28-c14-operator-desugar-design.md#L363-L363) 先行划界、以 CS2J5181 挂起）；
5. 产物可编译、行为等价（.NET 半区 `dotnet build/test -c Release` + 副作用探针/回绕运行时断言），幂等，零残余（§7.5）。

明确不做（留边界）：可空数值/可空 enum 的 lifted 自增（C22）、decimal 二元运算的 compat 方法发射（C34）、Half/Int128/UInt128/nint/IntPtr 上的自增算术核（C41）、指针目标（C06）、事件订阅 `+=/-=`（C13，已随 member-desugar 落地）、ref 目标的 Java 半区 holder（C01）、任何复合赋值 `+=/-=` 等的拆解或物化（含 C# 14 用户定义复合赋值运算符，决策 21）、member-desugar 的成员声明去糖增量（C11/C12/C13/C14 已落地且默认关；含推动 C14 把 `op_Increment`/`op_Decrement` 纳入 plan）、任何注册表结构改动。

### 1.6 硬不变式

1. **单次求值**：输出中接收者、每个索引实参、getter、setter 的求值次数与顺序与 C# 语义逐字一致；以副作用探针测试（非纯文本断言）守门；
2. **零残余（本规则所属类）**：输出树中不存在操作数绑定到「本规则所有权集合」（§4.2）的 `++/--` 节点；允许保留的仅限转换器可直映集合（int/long/sbyte/short/char 域的局部/参数/字段/数组元素简单目标）；
3. 一切目标分类与运算核选择来自 SemanticModel（`GetSymbolInfo`/`GetTypeInfo`/`GetOperation`/`GetDeclaredSymbol`），禁止按 token 文本替换；
4. 只输出**当前中间树可编译**的合法 C#：不手动调用属性访问器元数据名（`get_X`）、不手动调用 `op_Increment`——成员尚未去糖时对应站点显式失败而非伪造调用；
5. 条件执行安全：临时变量只能提升到「该站点必然被执行」的锚点；不可提升即 CS2J5339，绝不把条件分支内的副作用提前；
6. 幂等：产物二次执行零 diff；干净文件字节级复制（含 BOM）；E09/C15 产物零改写、统计零变化。

---

## 2. 关键设计决策表

| # | 决策点 | 结论 | 依据/备注 |
|---|---|---|---|
| 1 | 总体形态 | **逐文件语义规则 + 两遍重写**：Pass1 自底向上把 `++/--` 站点替换为读-改-写表达式并收集待提升声明，Pass2 在锚点语句前插入；不做整树 plan/回退（无改名/签名变化） | 复刻 [NullCoalesceSyntaxRewriter](file:///d:/code/cs2j/src/CSharpToJava.Core/NullCoalesceRewriter/NullCoalesceSyntaxRewriter.cs#L13-L30) 与 [C31 §4.3 TempLifter 约定](file:///d:/code/cs2j/docs/superpowers/specs/2026-09-28-c31-index-range-design.md#L190-L198)；正确性由语义分类 + verify build/test + residual 闸门兜底。规则在 C15 pass 的单文件管线内有序执行（决策 17） |
| 2 | 目标×运算核二维分类 | Analyzer 对每站点产出 `(TargetKind, ArithmeticCore, Checkedness, Liftability)` 四元组；TargetKind 决定 READ/WRITE 渲染，ArithmeticCore 决定「算」渲染，两维正交组合 | 目标与数值域是两个独立失败轴（int 属性与 uint 属性同样撞 L169；enum 局部撞 L182）；二维表避免按语法 if-else 堆叠 |
| 3 | **属性目标** | 接收者无副作用（`this`/`base`/简单名链，仿 [IsSideEffectFreeReceiver](file:///d:/code/cs2j/src/CSharpToJava.Core/UnsignedTypeRewriter/UnsignedTypeSyntaxRewriter.cs#L248-L255)）：`R.P = CORE(R.P)`；否则先提升 `var __r = R;` 再 `__r.P = CORE(__r.P)`。getter 一次（RHS）、setter 一次（赋值）、`R` 一次。静态属性省略接收者临时 | C# 不允许源码手动调用 `get_P/set_P`，故输出必须保持属性赋值语法；**C11 已实现（member-desugar，默认关）**：启用后其 PD-6（[C11 设计 L173](file:///d:/code/cs2j/docs/superpowers/specs/2026-09-28-c11-property-desugar-design.md#L173-L173)）在语句位直接把同形态方法化为 `set_P(get_P() ± 1)`（[PropertyUseSiteRewriter.cs:295-331](file:///d:/code/cs2j/src/CSharpToJava.Core/MemberDesugar/Properties/PropertyUseSiteRewriter.cs#L295-L331)），值消费位以 CS2J5119 令 plan 失败、真身留存——后者仍由本规则收编，两序等价（§3.3）。无 setter 的只读属性自增基线即 CS0443/CS0200 编译错误，诊断 CS2J5332 兜底 |
| 4 | **索引器目标** | 接收者与全部索引实参先提升：`var __r = R; var __a0 = a0; …; __r[__a0…] = CORE(__r[__a0…]);`；实参按源序声明，副作用实参保证只出现一次；多维索引器逐参提升；`this[i]` 省略接收者临时 | C# 同样不允许源码手调 `get_Item/set_Item`；**C12 已实现（member-desugar，默认关）**：IX-8（[C12 设计 L189](file:///d:/code/cs2j/docs/superpowers/specs/2026-09-28-c12-indexer-desugar-design.md#L189-L189)）在语句位已做同形 get→算→set 展开（[IndexerUseSiteRewriter.cs:212-237](file:///d:/code/cs2j/src/CSharpToJava.Core/MemberDesugar/Indexers/IndexerUseSiteRewriter.cs#L212-L237)），值消费位以 CS2J5137 令 plan 失败（[IndexerRule.cs:205-219](file:///d:/code/cs2j/src/CSharpToJava.Core/MemberDesugar/Indexers/IndexerRule.cs#L205-L219)），ref 索引器语句位保留 `++get_Item(...)` 形态（L232-237）——后两类残余由本规则收编，两序等价。数组元素（符号非 IPropertySymbol）走决策 15，不走本规则 |
| 5 | **事件目标？** | **不展开，显式失败（CS2J5332 防御性 Error）**：C# 事件只允许 `+=`/`-=`（类外 CS0079）；field-like event 在类内视为委托字段，而委托类型无 `++/--` 运算符（基线编译错误）；custom event 类内同样 CS0079。**可编译输入中该站点不可达**，L169 的 event 分支只是防御性代码 | 事件订阅/退订 `e += handler` 归 C13（add_X/remove_X 去糖）；本规则消息中明确指路 C13，不做任何「事件自增」语义猜测 |
| 6 | **enum 目标（C# enum 真身形态）** | READ/WRITE 同决策 3/4/15；算术核：`(E)((U)old ± 1)`，`U = EnumUnderlyingType`（八种整数），窄底层写回自然截断回绕；checked 上下文经 C15 helper：`(E)UnsignedMath.AddChecked{Domain}((U)old, 1)`（回转 enum 的显式转换不检查，对齐 [C15 决策 9](file:///d:/code/cs2j/docs/superpowers/specs/2026-09-28-c15-checked-unchecked-design.md#L86-L86)）；`[Flags]` 形态完全相同（`++` 在 flags enum 上合法但无语义特殊性，不特判） | E04 只重写 enum 位运算/`~`/HasFlag（[EnumSyntaxRewriter.cs:7-12](file:///d:/code/cs2j/src/CSharpToJava.Core/EnumPreprocessor/EnumSyntaxRewriter.cs#L7-L12)），其 `VisitPrefixUnaryExpression` 仅处理 `BitwiseNot`（[L461-464](file:///d:/code/cs2j/src/CSharpToJava.Core/EnumPreprocessor/EnumSyntaxRewriter.cs#L461-L464)），**不碰 `++/--`**；unchecked 简单 enum 目标 C15 亦不拥有，归本规则。与 E04/C05 交界见 §11 |
| 7 | **enum 目标（C05 去糖后形态）** | 当操作数类型已非 `TypeKind.Enum`、但具有 C05 生成契约成员（实例 `Value` 属性 + 静态 `FromValue(U)`，[C05 D6/D12](file:///d:/code/cs2j/docs/superpowers/specs/2026-09-28-c05-enum-declaration-design.md#L87-L87)）时，算术核渲染为 `E.FromValue(unchecked((U)(old.Value ± 1)))`（宽类型省略 `unchecked`，遵循 C05 D14） | C05 在链上早于本 pass（enum→enum-declaration→…→unsigned→numeric-boundary）；Analyzer 以「符号成员指纹」双态识别，保证 C05 落地前后都可编译。C05 未合入时该分支无命中，M2 联调 |
| 8 | **decimal 目标** | READ/WRITE 正常展开；算术核**只渲染 `old + 1m` / `old - 1m`**（不直接发射 compat `Decimal` 调用）；simple decimal 局部/字段同样归约为 `d = d + 1m` | decimal `++` 的读-改-写形状归本规则；二元运算的方法面（compat [Decimal.add/subtract](file:///d:/code/cs2j/java/csharptojava-compat/src/main/java/io/github/ningpp/compat/Decimal.java#L261-L275) 与 `Decimal.ONE`）归 C34，与 C15「checked 壳剥离、decimal 运算原样」同界（[C15 NB-11](file:///d:/code/cs2j/docs/superpowers/specs/2026-09-28-c15-checked-unchecked-design.md#L210-L210)）。C34 落地前每站点发 **CS2J5334 Warning（strict 即 Error）**，把「Java 半区未收口」从静默变为响亮；是否改为整体 Error 移交列开放问题 2 |
| 9 | **无符号/窄类型残余** | 目标展开后，算术核复用 E09/C15 既有面：uint/ulong 域 `UnsignedMath.AddUInt32/AddUInt64(old, 1)`（unchecked）/`AddChecked*`（checked）；byte/ushort 简单域 `(byte)(old + 1)`/`(ushort)(old - 1)` 窄化写回（与 [UnsignedPostfixRewriter.cs:70-83](file:///d:/code/cs2j/src/CSharpToJava.Core/UnsignedTypeRewriter/UnsignedPostfixRewriter.cs#L70-L83) 同形）；sbyte/short/char 简单目标转换器本可直映，**仅在复杂目标上展开** | 本规则运行在 E09 之后，输入里已无 E09 改写过的站点（赋值表达式/helper 调用，无 `++` 节点），只处理 E09 跳过的残余：复杂目标、条件位、checked 区真身。**不新增支撑库方法**（Add*/AddChecked* 已由 E09/C15 备好） |
| 10 | **IntPtr / nint / UIntPtr / nuint** | v1 一律 **CS2J5333 Error**（简单与复杂目标同），消息指路 C41；读-改-写脚手架（接收者/旧值提升）在 Analyzer 中预留分类 `NativeIntTarget`，C41 落地后仅替换算术核渲染 | nint 位宽依赖平台，Java 无对应原语；与 C15 对 nint checked 的 CS2J5320（[C15 决策 11](file:///d:/code/cs2j/docs/superpowers/specs/2026-09-28-c15-checked-unchecked-design.md#L88-L88)）同策略同边界。E09 对 Native 域亦显式跳过（[UnsignedPostfixRewriter.cs:57-62](file:///d:/code/cs2j/src/CSharpToJava.Core/UnsignedTypeRewriter/UnsignedPostfixRewriter.cs#L57-L62)），现状即 fail-fast，无回归 |
| 11 | **自定义 `op_Increment`/`op_Decrement`**（含属性/索引器上的自定义类型目标） | **站点归本规则**：去糖后渲染为 `Qual.Increment(old)` / `Qual.Decrement(old)`（限定名与方法名取绑定符号经 C14 命名契约的去糖名），结果到目标类型的回转转换链按 `IIncrementOrDecrementOperation`（含用户转换）渲染；前缀结果=方法返回值，后缀先存旧值。**现状（2026-09-29 核实）**：C14 已落地但明确不碰 `++/--` 站点——[OperatorRule.cs:188](file:///d:/code/cs2j/src/CSharpToJava.Core/MemberDesugar/Operators/OperatorRule.cs#L188-L188) `IsIncrementOrDecrement` 不建 plan，[OperatorUseSiteRewriter.cs:80-86](file:///d:/code/cs2j/src/CSharpToJava.Core/MemberDesugar/Operators/OperatorUseSiteRewriter.cs#L80-L86) 在声明处发 Info **CS2J5181（IncrementDecrementDeferred，"deferred to C17"）**，`IsRewritableUnary` 只含 `-/+ /!/~`（L429-433），且命名表 `OperatorNames.cs` **尚无 Increment/Decrement 条目——命名契约尚未冻结**（列开放问题 1）。契约冻结前站点仍发 **CS2J5338 Error**（C# 源码不允许手调 `op_Increment`，无法预展开） | 这是对 [C14 开放问题 4（L395）](file:///d:/code/cs2j/docs/superpowers/specs/2026-09-28-c14-operator-desugar-design.md#L395-L395) 的结论建议，详见 §11-C14。checked 用户自增变体（未来语言版本）的命名随 C14 的 checked 后缀契约，本规则不预设 |
| 12 | **前缀返回值** | 独立语句：赋值语句 `target = NEW;`（NEW 已含 READ）。值被消费：替换为**赋值表达式** `(target = NEW)`——C# 赋值表达式结果即新值，可位于实参/初始化器/return 等位置；属性/索引器形态同构（`F(__r.P = CORE(__r.P))`），接收者/实参临时仍按决策 3/4 提升 | 赋值表达式对属性/索引器同样精确触发一次 get+set；仅当 NEW 内部含语句级临时（自定义转换链等）时退回后缀同款锚点提升；字段初始化器/特性实参等无锚点位置 → CS2J5339 |
| 13 | **后缀返回值与临时提升** | 独立表达式语句：与前缀同形（旧值不消费，不声明旧值临时）。值被消费：声明旧值临时 `__o` + 写回语句，节点替换为 `__o`；提升锚点与顺序见决策 14。旧值读取点 = READ 结果（自定义运算符时旧值是入参前的目标值，不是方法返回值） | 严格模拟编译器：写回副作用发生在操作数求值之后、外围继续之前；后缀旧值与新值类型可能不同（自定义运算符回转），旧值临时类型取操作数静态类型 |
| 14 | **复合表达式中的提升与条件安全** | 两遍 lifter（§4.4），锚点 = 站点所在最近 Block 直接语句；同一锚点多个临时按求值序（接收者→索引实参→旧值→…）稳定排序。v1 只在**无条件执行语句白名单**（沿用 [UnsignedPostfixRewriter L140-148](file:///d:/code/cs2j/src/CSharpToJava.Core/UnsignedTypeRewriter/UnsignedPostfixRewriter.cs#L140-L148)：表达式语句/局部声明/return/throw/yield）内做后缀值用提升；且 Pass1 须证明「锚点语句中先于该站点的表达式无副作用」或经 M2 的语句求值序列线性化（决策 14b），否则 CS2J5339。if/while/do/for 条件、`?:`/`&&`/`||`/`??` 内、查询子句、lambda/局部函数体、字段/构造初始化器、特性实参、表达式树：v1 无安全锚点 → CS2J5339 | 直接复刻 UnsignedPostfixRewriter 的条件执行教训（提前拆出会改变语义）。C31 TempLifter 对「无锚点位置零临时」同款纪律（[C31 §4.3](file:///d:/code/cs2j/docs/superpowers/specs/2026-09-28-c31-index-range-design.md#L195-L195)） |
| 14b | 语句求值序列线性化（M2 候选） | 对白名单语句内「前序有副作用表达式」的值用后缀，把整条语句表达式按 C# 从左到右求值顺序线性化为临时声明序列（如 `M(F(), p++, G())` → `var __t0=F(); var __o=p; p=CORE(p); var __t1=G(); M(__t0,__o,__t1);`）；仅支持机械可重建的语句形态，重建不完整即 CS2J5339 | M1 先以保守安全条件交付；M2 再扩大命中率。**不采用**「ref 形参 PostIncrement helper」绕路（`static uint PostInc(ref uint p)` 会把 C01 的 Java ref 缺口引入数值族），列开放问题 3 |
| 15 | **数组元素目标** | `arr[i]++` 对内建 int/long/sbyte/short/char 域**不处理**（转换器直映集合，[L171-174](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/UnaryExpressionTransformer.cs#L171-L174) 允许数组元素）；对 enum/decimal/自定义类型元素：索引单次求值（`var __i = i; arr[__i] = CORE(arr[__i]);`）；自定义类型元素用 ref 别名避免重复索引：`ref T __e = ref arr[__i]; __e = T.Increment(__e);` | 普通数值数组上 Java `arr[i]++` 逐位一致，重写纯属膨胀；enum/decimal 数组元素当前撞 L182/L184，是真实缺口。多维数组 `arr[i,j]` 逐维提升；锯齿数组同普通元素规则 |
| 16 | **ref 返回目标**（ref 属性/ref 索引器） | 成员去糖前：以 **ref local 别名**展开（`ref T __f = ref R.P;` / `ref T __f = ref R[args];`，接收者/实参单次提升），随后对别名做读-改-写；C12 去糖后若输入已是 ref 返回方法调用变量形态（[C12 D9](file:///d:/code/cs2j/docs/superpowers/specs/2026-09-28-c12-indexer-desugar-design.md#L66-L66)），保留该形态，仅处理其运算核。输出 C# 合法可编译，但 Java 半区依赖 C01 holder → 每站点 **CS2J5340 Warning（strict 即 Error）** | ref 返回方法调用结果在 C# 中是变量，`++` 合法；Java 无 ref，属 C01 领地，本规则不建设 holder。`M(ref p)` 传址位不产生自增站点（C# 语法上 `ref x++` 非法） |
| 17 | **checked 上下文：与 C15 的分工与规则内顺序** | 简单内建整数目标（含简单 enum）在 checked 区由 C15 NB-8 拥有（[C15 §4.3](file:///d:/code/cs2j/docs/superpowers/specs/2026-09-28-c15-checked-unchecked-design.md#L207-L207)）；**复杂目标的 checked 读-改-写由本规则拥有**，算术核调用 C15 同款 checked helper（决策 6/9）。文件内规则执行顺序：**C17 站点展开先跑，C15 checked/unchecked 规则后跑**——本规则产出的新算术节点落在 checked 栈内时由 C15 接管插 helper，本规则不自行判 checked 算术名；C15 的 CS2J5324 在本规则对应站点落地后停用（由规则所有权表驱动，不删码） | 避免两规则对同一站点双写；新节点必须被后续遍历访问到（Pass1 结果经 C15 rewriter 二次访问，而非同一 Visit 内返回），M0 接缝（§4.5）保证此顺序。unchecked 区枚举 `++`（含简单目标）无 C15 对应物，全部本规则拥有 |
| 18 | nullable / dynamic / 表达式树 / 常量上下文 | nullable 数值/enum 目标 → **CS2J5335 Error**（lifted 语义归 C22，对齐 C15 CS2J5325）；dynamic 绑定 → **CS2J5336 Error**；位于 `Expression<Lambda>` 树内 → **CS2J5337 Error**（改写改变树形态，[catalog §4 风险 8](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L1106-L1106)）；编译期常量自增本就非法（CS0131/CS0133），无需处理 | Error 不提供 force：与 C15 同纪律，丢语义/改树形态不可由配置静默 |
| 19 | 指针/其他 default 分支目标 | `*p++`、`p->x++` 等 → **CS2J5332 Error**，消息指路 C06（unsafe-ffm）；条件访问 `x?.P++` 属 C35 语法面，命中即 CS2J5339 并在消息指路 C35（与 [C31 对 `?[]` 的 IR130 处理](file:///d:/code/cs2j/docs/superpowers/specs/2026-09-28-c31-index-range-design.md#L88-L88)同模式） | 不做半成品；各归其族 |
| 20 | 幂等与 trivia | 全部新节点挂 `RewriterAnnotation`；二次执行时所有权分类器对赋值语句/helper 调用不命中；语句首 trivia（含 `#if/#endif`）随首条提升声明走（仿 [UnsignedPostfixRewriter.cs:66-68](file:///d:/code/cs2j/src/CSharpToJava.Core/UnsignedTypeRewriter/UnsignedPostfixRewriter.cs#L66-L68)） | 与 E09/C15 的幂等/指令保持纪律一致 |
| 21 | **C# 14 用户定义复合赋值运算符（2026-09-29 补）** | 本规则**不拥有、不拆解任何复合赋值** `+=/-=/*=/…`：①C# 14 起这些运算符可由用户声明（catalog 六次补全 C14 行 [L471](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L471-L471)、C52 [L1002/L1010](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L1002-L1010)），`a += b` 可能绑定显式运算符而非 `a = a + b`；②Analyzer 若在未来任何读-改-写/复合展开路径遇到需要渲染复合/二元拆解的节点，须先经 SemanticModel（`ICompoundAssignmentOperation`）查显式复合运算符，命中即不拆解（交 C14 物化或显式诊断）；③`++/--` 无用户定义复合形态，本规则对 `++/--` 的分类与展开不因此改变——v1 自定义 `++/--` 站点维持 CS2J5338（决策 11）。Roslyn 对 C# 14 的支持与 `ICompoundAssignmentOperation` 的具体暴露面**待实现期核实**（开放问题 6） | 把查询义务登记进 Analyzer 设计，避免在属性/索引器元素类型携带用户 `+=` 时自行拆解决策 3/4 的 CORE；catalog 已两次写明该前提被打破，本规则不得回退该结论 |

---

## 3. 链位与族契约

### 3.1 归属：numeric-boundary 族 Rules/IncrementTargets/

- 物理位置：`src/CSharpToJava.Core/NumericBoundary/Rules/IncrementTargets/`（C15 [§4.1 已预留](file:///d:/code/cs2j/docs/superpowers/specs/2026-09-28-c15-checked-unchecked-design.md#L179-L187)）。本规则只增该子目录、自有测试文件与 fixture 追加文件；**不新建 pass、不新增适配器、不新增中间目录、不新增支撑工程**。
- **不翻转 stub**：`numeric-boundary` 的 Stub→Implemented 由 C15 完成（2026-10-01 基线核实：已 Implemented、默认开）；本任务开始前 C15 必须已合入 base（§12，已满足）。
- 注册表唯一允许触碰：[PassCatalog.cs](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassCatalog.cs) 中该描述符的 `catalogItems` 单行追加——2026-10-01 基线真值为 `"C15, C34"`（C34 已随族合入），故实际追加为 `"C15, C34" → "C15, C34, C17"`（设计初稿记的 `"C15" → "C15, C17"` 是 C34 合入前的预期一行冲突，以基线真值为准）。Status/DefaultEnabled/RunAfterIds/Stage/verify/支撑库字段一律不动；链序测试（`LockedChain`/`EnabledByDefault`）不动。

### 3.2 前置依赖

1. **C15（硬前置）**：共享 `NumericBoundaryAnalyzer`（checked/unchecked 上下文栈、工程级 `/checked` 种子、表达式树/dynamic 判定、支撑工程跳过谓词）、`NumericBoundaryModels`（Options/Statistics/Diagnostic/Severity/FileResult）、`NumericBoundarySyntaxFacts`（`__cs2j` 命名、字面量、FormatAndValidate）、适配器与支撑库注入链路；CS2J5330-5349 码段已预留（C15 [§4.5](file:///d:/code/cs2j/docs/superpowers/specs/2026-09-28-c15-checked-unchecked-design.md#L241-L248)）。
2. **E09（链上既成事实）**：本 pass 锚边 RunAfter=`{unsigned}` 已保证 E09 先执行；输入中 E09 处理过的无符号站点不再是 `++/--` 节点，本规则只收残余。
3. **C14（已落地但契约缺口仍在）**：member-desugar（含 C11/C12/C13/C14）已 **Implemented**（[PassCatalog.cs:230-241](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassCatalog.cs#L230-L241)，默认关），但 `op_Increment`/`op_Decrement` 被 C14 显式排除在 plan 外并经 CS2J5181 移交本规则（[OperatorUseSiteRewriter.cs:80-86](file:///d:/code/cs2j/src/CSharpToJava.Core/MemberDesugar/Operators/OperatorUseSiteRewriter.cs#L80-L86)）；其 Increment/Decrement 去糖命名契约尚未冻结（开放问题 1），故自定义运算符站点的去糖调用形态仍**暂不可生成**，维持 CS2J5338 显式失败。
4. C05（enum 声明）、C11/C12（属性/索引器方法化，已落地、默认关）、C34（decimal）、C41（nint）、C22（nullable）均为**非硬前置**：缺失/关闭时本规则以 C# 真身形态工作或显式失败，不等待；外部程序集（BCL/第三方）属性/索引器成员不在源内，C11/C12 本就不动（C12 D5/D6），其站点始终由本规则拥有。

### 3.3 与 C11/C12 的链序（两序等价论证，2026-09-29 按现状重写）

链序相关三方现状（均已核实）：catalog §3.1 路线图把「成员去糖族 C11→C12→C13→C14」列为第 12 项、「数值边界族 C15→C17…」为第 13 项（[preprocessor-catalog.md:1030-1031](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L1030-L1031)，§3.3 族表在 [L1082-1084](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L1082-L1084)），即建议 member-desugar 在先；foundation plan Stub 表的声明序是 numeric-boundary（L92）在 member-desugar（L95）之前（[preprocessor-foundation_plan.md:92-95](file:///d:/code/cs2j/.trae/documents/preprocessor-foundation_plan.md#L92-L95)），但该表是规划期 stub 声明序，不等于后来的实际执行序。**当前注册表实测（按 [PassRegistry.cs:95-133](file:///d:/code/cs2j/src/CSharpToJava.Core/Preprocessing/PassRegistry.cs#L95-L133) Kahn 拓扑手工推演）**：注册表以 `Implemented.Concat(Stubs)`（[PassCatalog.cs:268-269](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassCatalog.cs#L268-L269)）喂入、声明序为 tiebreak；member-desugar 已 `Implemented`、默认关（[PassCatalog.cs:230-241](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassCatalog.cs#L230-L241)），其两个 RunAfter 依赖 struct-value-semantics（[PassStubCatalog.cs:121-129](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassStubCatalog.cs#L121-L129)）与 closure-lift（[L140-149](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassStubCatalog.cs#L140-L149)）在 stub 声明序上都早于 numeric-boundary（[L150-159](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassStubCatalog.cs#L150-L159)，仅 RunAfter unsigned）——两依赖一旦出队，位于 Implemented 段的 member-desugar 立即先于 numeric-boundary 出队。故**两 pass 同启时实际全序为 member-desugar → numeric-boundary（与 catalog 建议序一致；此为静态推演，最终以运行时/链序测试复核，开放问题 5）**。本设计的立场：

- **不新增 RunAfter 依赖，维持 `{unsigned}` 锚边**（与 C15 一致，零描述符/链序测试改动）。
- 本规则对两种链序都正确：
  - *member-desugar 在先（当前注册表推演序，同 catalog 建议序）*：源内属性/索引器**语句位**站点大半已被去糖消失，本规则输入只剩：①外部程序集（BCL/第三方）可写属性/索引器上的自增（成员不在源内，C11/C12 本就不动，C12 D5/D6）；②C11/C12 plan 不可提升而保留真身的**值消费位**（属性 **CS2J5119**、索引器 **CS2J5137**，及 ref 索引器保留的 `++get_Item(...)` 形态）；③enum/decimal/nint/自定义运算符目标。分类器全部以 SemanticModel 绑定为准，对方法形态/属性形态双兼容。
  - *numeric-boundary 在先（假设未来注册表重排后的备选序，如 foundation plan 表序被显式固化）*：输入为属性/索引器真身，输出决策 3/4 的**属性赋值语法形态**（`__r.P = CORE(__r.P)`、`__r[__a] = CORE(__r[__a])`），是合法 C#；member-desugar 之后运行时，PD-6/IX-8 对其中的读/写分别方法化，得 `set_P(get_P())` / `SetItem/GetItem`，与它们直接处理 `++/--` 的目标形态语义同构。
- 若未来注册表重排为相反序，只需重跑本规则测试与一次 sdk 残余命中率对比，无代码设计变更；member-desugar 默认关翻转评审时应把此两序等价性作为核对项，本规则不主动推动重排。
- 自定义运算符站点与两序无关：无论 member-desugar 先后，C14 都不碰 `++/--`（CS2J5181 移交），在命名契约冻结前一律 CS2J5338（决策 11）。

---

## 4. Core 组件设计

### 4.1 目录与文件清单（C17 独占 `Rules/IncrementTargets/`）

```
src\CSharpToJava.Core\NumericBoundary\Rules\IncrementTargets\
  IncrementTargetRule.cs         # 规则入口（INumericBoundaryRule 实现，Order=20；C15 CheckedArithmetic=10）：
                                 #   驱动 Analyze→Pass1→Pass2，汇出 RuleFragment（统计/诊断/摘要/库需求）
  IncrementTargetAnalyzer.cs     # 四元组分类（TargetKind / ArithmeticCore / Checkedness / Liftability）、
                                 #   访问器 get/set 符号与 RefKind、op_Increment 绑定与回转转换（IOperation）、
                                 #   enum 真身/C05 双态指纹、接收者副作用谓词、锚点与条件执行可行性
  IncrementTargetRewriter.cs     # Pass1 CSharpSyntaxRewriter（自底向上）：站点 → 读-改-写表达式替换，
                                 #   收集 PendingLift；前缀赋值表达式内联、后缀旧值替换
  IncrementTargetExpansion.cs    # 纯函数渲染：READ/WRITE（属性/索引器/数组/ref 别名/方法形态）、
                                 #   NEW 算术核（窄化 cast/enum cast 链/FromValue/±1m/UnsignedMath/checked/Custom）
  IncrementTargetLifter.cs       # Pass2：PendingLift 锚点插入（VisitBlock），形状对齐
                                 #   NullCoalesceStatementLifter / C31 TempLifter（§4.4）
  IncrementTargetSyntaxFacts.cs  # 临时命名 __cs2j_inc_r/a/o/n/f、作用域碰撞避让、白名单语句谓词、
                                 #   表达式树/查询/初始化器祖先判定、RewriterAnnotation、trivia 保持
  IncrementTargetDiagnostics.cs  # CS2J5330-5349 码位常量与消息模板
  IncrementTargetModels.cs       # 规则 Options 节（EnableIncrementTargets 默认 true）、规则强类型统计桶
```

测试新增（§7）：`tests/CSharpToJava.ProjectTests/NumericBoundaryIncrementTargetUnitTests.cs`；fixture 向 C15 建立的 `tests/SampleNumericBoundarySolution/` 追加 `IncrementTargets/` 子目录文件（Lib/App/Tests 各一），不新建 sln。

### 4.2 Analyzer：目标与运算核分类

**TargetKind**（以 `GetSymbolInfo(operand)` + 语法形态双判）：

| TargetKind | 判据 | READ/WRITE 渲染 |
|---|---|---|
| SimpleDirect | 局部/参数/字段标识，或接收者无副作用的字段访问；数组元素（内建直映类型） | 原样（不重写） |
| Property | `MemberAccess`→`IPropertySymbol`（非 indexer，RefKind=None） | 决策 3 |
| Indexer | `ElementAccess`→`IPropertySymbol.IsIndexer`（RefKind=None） | 决策 4 |
| ArrayElementSpecial | 数组元素但元素类型为 enum/decimal/自定义运算类型 | 决策 15 |
| RefReturning | 属性/索引器/方法形态符号 `RefKind != RefKind.None` | 决策 16 |
| Event | `IEventSymbol` | CS2J5332（决策 5） |
| Pointer | `PointerIndirection`/`PointerMemberAccess` | CS2J5332→C06（决策 19） |
| Conditional | `?.` 条件访问目标 | CS2J5339→C35（决策 19） |
| Other | 其余语法形态 | CS2J5332 |

**ArithmeticCore**（以操作数静态类型 + `GetOperation(node)` 的方法绑定判）：

| ArithmeticCore | 判据 | NEW(old) 渲染 |
|---|---|---|
| BuiltinNarrow | sbyte/byte/short/ushort/char/int/long 域（含简单目标上的直映） | `(T)(old ± 1)`，int/long 省略 cast |
| UnsignedDomain | uint/ulong/byte/ushort 域且需要 helper | unchecked：`UnsignedMath.Add{UInt32/UInt64}(old,1)`；窄域 cast（决策 9）；checked：C15 `AddChecked*`（由 C15 后扫插入，本规则只产裸算术节点） |
| EnumCast | `TypeKind.Enum` | 决策 6 |
| EnumFromValue | C05 契约成员指纹 | 决策 7 |
| Decimal | System_Decimal | `old ± 1m` + CS2J5334（决策 8） |
| NativeInt | IntPtr/UIntPtr/nint/nuint | CS2J5333（决策 10） |
| CustomOp | `IIncrementOrDecrementOperation.OperatorMethod` 非空 | 决策 11；声明未去糖 → CS2J5338 |
| LiftedNullable | nullable 值类型 | CS2J5335 |
| Dynamic | dynamic 绑定 | CS2J5336 |

**Checkedness/Liftability**：checked 判定不自行读语法栈——复用 C15 `NumericBoundaryAnalyzer` 的上下文 API（文件内规则共享一个 Analyzer 实例）；Liftability 计算锚点语句、条件执行祖先、白名单语句形态与「前序副作用」证明（决策 14）。

### 4.3 重写规则全集（IT-x）

| 规则 | 输入 | 输出 | 要点/边界 |
|---|---|---|---|
| IT-1 属性前缀 | `R.P++` / `--R.P` | 独立：`[__r;] __r.P = CORE(__r.P)`；值用：`(__r.P = CORE(__r.P))` | get/set/接收者各一次；只读属性 CS2J5332 |
| IT-2 属性后缀 | `R.P--` 等 | 独立同 IT-1；值用：`__o` 替换 + `__r.P = CORE(__o)` 等提升序列 | 旧值临时类型=属性类型；锚点不满足 → CS2J5339 |
| IT-3 索引器前缀/后缀 | `R[a0,…]++` | 决策 4 的实参提升序列 + IT-1/2 同形 | 实参源序、各一次；多维 |
| IT-4 特殊数组元素 | `arr[i]++`（enum/decimal/自定义元素） | 索引提升；自定义元素用 ref 别名（决策 15） | 普通数值数组零改动 |
| IT-5 enum 自增 | 任意目标形态 + EnumCast/EnumFromValue 核 | cast 链 / FromValue（决策 6/7） | 窄底层回绕、`[Flags]` 同形、checked helper 由 C15 后插 |
| IT-6 无符号残余 | E09 跳过的 uint/ulong/窄域站点 | helper/窄化 cast（决策 9） | 仅处理仍为 `++/--` 的节点；E09 产物零改写 |
| IT-7 decimal 自增 | decimal 任意目标 | `old ± 1m` + CS2J5334 | 不发 compat 调用；归 C34 |
| IT-8 自定义运算符自增 | CustomOp 核 | `Qual.Increment/Decrement(old)` + 回转转换链 | 依赖 C14；未去糖 CS2J5338 |
| IT-9 ref 返回目标 | RefReturning | ref local 别名 + CS2J5340 | 决策 16 |
| IT-10 前缀值用内联 | 赋值表达式可位的任意已支持目标 | `(target = NEW)` | NEW 含语句级临时时转提升 |
| IT-11 后缀值用提升 | 白名单语句内后缀 | Pass1 收集 + Pass2 插入 | 决策 14；M2 线性化（14b） |
| IT-12 响亮失败 | 决策 5/10/16/18/19 各站点 | Error/Warning 诊断，节点原样保留 | 不产生「半改写」文件；错误计数桶 |
| IT-13 residual 对账 | 输出树重扫 | 所有权集合内零 `++/--` 节点，否则 CS2J5341（strict Error） | 直映集合白名单（SimpleDirect 内建直映域）；支撑工程目录豁免（同 C15 NB-13） |

### 4.4 语句级临时变量提升（Pass2）

- `PendingLift(StatementSyntax Declaration, StatementSyntax AnchorStatement, int Order)` 与 [NullCoalesceSyntaxRewriter.PendingDeclaration](file:///d:/code/cs2j/src/CSharpToJava.Core/NullCoalesceRewriter/NullCoalesceSyntaxRewriter.cs#L27-L30)、C31 `PendingTemp(Declaration, Anchor, Order)`（[C31 §4.3](file:///d:/code/cs2j/docs/superpowers/specs/2026-09-28-c31-index-range-design.md#L192-L192)）同构；`IncrementTargetLifter.VisitBlock` 复刻 [NullCoalesceStatementLifter](file:///d:/code/cs2j/src/CSharpToJava.Core/NullCoalesceRewriter/NullCoalesceStatementLifter.cs#L21-L38)：锚点前按 Order 插入，递归处理内嵌块，作用域自然正确。
- 临时角色与命名（文件内计数，作用域碰撞避让，沿用 `__cs2j` 前缀但加规则后缀）：
  - `__cs2j_inc_r<N>` 接收者、`__cs2j_inc_a<N><k>` 索引实参、`__cs2j_inc_o<N>` 后缀旧值、`__cs2j_inc_n<N>` 新值（前缀值用且 NEW 含提升时）、`__cs2j_inc_f<N>` ref 别名（显式 `ref T`）。
- 声明类型：接收者/实参用 `var`；ref 别名必须显式 `ref T`；旧值/新值在 custom-op 回转链上用 `var`（取静态类型）。
- trivia：锚点语句的语句首 trivia（含 `#if/#endif` 与缩进）随首条插入声明走，仿 E09 第二遍（[UnsignedPostfixRewriter.cs:102-115](file:///d:/code/cs2j/src/CSharpToJava.Core/UnsignedTypeRewriter/UnsignedPostfixRewriter.cs#L102-L115)）。
- **共享上收约定**：本 lifter 首期留在 `NumericBoundary/Rules/IncrementTargets/` 内；其对外形状遵循 C31 冻结的最小契约（`PendingTemp + Collect + Apply`），待 expression-desugar 族（C23/C24/C35/C36）立项时与 C31 TempLifter 一并上收到 `src/CSharpToJava.Core/Preprocessing/Expressions/SingleEvaluationTempLifter.cs`（[C31 §4.3 末](file:///d:/code/cs2j/docs/superpowers/specs/2026-09-28-c31-index-range-design.md#L198-L198)）；本任务不迁移 NullCoalesce/C31 既有实现。
- 跨 pass 的自定义运算符「转换链渲染器」（C14 OC-8 已提出同族渲染器概念，[C14 §4.4 OC-8](file:///d:/code/cs2j/docs/superpowers/specs/2026-09-28-c14-operator-desugar-design.md#L188-L188)）跨 member-desugar/numeric-boundary 两族，建议同样落在未来的 `Core/Preprocessing/Expressions/` 共享位；v1 本规则在 `IncrementTargetExpansion.cs` 内基于 IOperation 自持一份最小实现并登记技术债（开放问题 1）。

### 4.5 对 C15 共享层的复用与必要接缝（M0）

C15 [§4.1](file:///d:/code/cs2j/docs/superpowers/specs/2026-09-28-c15-checked-unchecked-design.md#L187-L187) 授权「共享组件不够用时先扩根层并补 C15 回归」。本规则需要的最小接缝（由本任务在 C15 合入基上实施，携带 C15 回归更新）：

1. **规则发现与顺序**：`NumericBoundary/` 根层增 internal `INumericBoundaryRule`（`int Order`、文件级 `Analyze` 上下文注册、`RewriteNode` 钩子、`RuleFragment`（统计桶/诊断/摘要片段/是否需要支撑库）），CheckedArithmetic 以 Order=10 注册、IncrementTargets Order=20；保证「C17 先展开站点、C15 后扫新节点」的文件内顺序（决策 17）。若 C15 落地版已内置等价管线则零接缝，仅挂规则。

   > **2026-10-01 实施校订（M0 落地面）**：C15 落地版未内置规则管线（C34 以硬编码段接在 C15 之后）。决策 17 的执行序（IncrementTargets → 重绑模型 → CheckedArithmetic → 重绑模型 → DecimalOps）与本条「CheckedArithmetic=10、IncrementTargets=20」的登记数字相反；**以决策 17 的语义执行序为准**：`INumericBoundaryRule.Order` 按执行序赋值（IncrementTargets=10、CheckedArithmetic=20、DecimalOps=30），驱动方按 Order 升序调用、规则间树变更即 `RebuildSemanticModel`。C15/C34 既有 rewriter 内部代码零改动，仅以薄包装规则接入（携带 C15/C34 全量回归）；早期返回闸门族化为「!c15Dirty && !EnableDecimalOps && !EnableIncrementTargets → Clean」。
2. **共享 Analyzer 只读复用**：checked 上下文查询、工程 `/checked` 种子、表达式树/dynamic 谓词、支撑工程目录跳过谓词、FormatAndValidate 三候选。
3. **注入判定族化（一行适配器收口）**：C15 适配器的支撑库释放条件从「checked helper 计数 > 0」改为「族级 UnsignedMath 调用需求（含 C17 的 IT-6 残余站点）> 0」；该计数经 RuleFragment 聚合，[NumericBoundaryPassAdapter.cs](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/Adapters) 仅改判定表达式一处，并补「仅 C17 无符号站点也引库」回归。除此外适配器文件不新增本规则专属代码。
4. Options/Statistics：根 `NumericBoundaryOptions` 经规则节扩展 `rules.incrementTargets`（`EnableIncrementTargets` 默认 **true**：规则随 pass 启停，不新增 CLI 开关；另留 `strict` 复用族值）；族 Statistics 增规则段（§4.6）。

### 4.6 诊断码段与统计

使用 C15 预留的 **CS2J5330-5349**（[C15 §4.5](file:///d:/code/cs2j/docs/superpowers/specs/2026-09-28-c15-checked-unchecked-design.md#L245-L245)），码位只追加不改号（§6 表）。

规则统计桶（经 RuleFragment 汇入族 Statistics 与适配器摘要，不改适配器摘要骨架）：`PropertySites`、`IndexerSites`、`SpecialArrayElementSites`、`EnumSites`（Cast/FromValue 分桶）、`DecimalSites`、`UnsignedResidualSites`、`CustomOperatorSites`、`RefAliasSites`、`PrefixInlineSites`、`PostfixStandaloneSites`、`PostfixLiftedSites`、`TempsByRole: Dictionary<string,int>`、`DeferredByReason: Dictionary<code,int>`、`EventSites`（稳态应为 0）。

---

## 5. CLI 集成（零注册表结构改动）

1. **无新 pass/适配器/开关/中间目录**：本规则随 `numeric-boundary` pass 执行，入口面完全继承 C15（`--numeric-boundary` / `--enable-pass numeric-boundary` / 通用 `preprocess --pass`）；规则节 kill-switch `EnableIncrementTargets=false` 时零执行、零痕迹（2026-10-01 基线：pass 本身已默认开，§7.6「默认关」验收项改以 kill-switch 关闭态验证）；不新增 legacy toggle、不进 [PassChainFactory](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassChainFactory.cs)。
2. **注册表唯一改动**：PassCatalog 描述符 `catalogItems: "C15, C34" → "C15, C34, C17"`（§3.1，2026-10-01 基线真值）；`--list-passes` 自动反映；`PassChainOrderTests`/`PassStubCatalogTests` 不动。
3. **统计汇入**：RuleFragment 自渲染摘要片段，建议族摘要行追加 `increment-targets: P property/I indexer/E enum/D decimal/U unsigned-residual/X custom site(s), L postfix lifted, T temp(s), R deferred`；适配器文件除 §4.5-3 的一行注入判定外不改。
4. **支撑库**：复用 `Cs2jUnsignedMath`（descriptor 已声明 `PassSupportLibrary.Cs2jUnsignedMath`）；本规则**不新增库方法**——IT-6 调用的 `AddUInt32/SubtractUInt32/…` 属 E09 既有面，checked 域属 C15 新增面。仅当 IT-6 产生调用时参与「有改写才注入」判定（§4.5-3）；支撑工程目录文件 clean copy、residual 豁免、Maven 过滤/指纹豁免全部继承。
5. **verify**：继承描述符双默认开（verifyBuild/verifyTests=true）；本规则的语句提升最易引入编译错误，verify build 是硬守门，不允许规则级关闭。
6. **单文件模式**：继承族 CS2J5309 闸门；含 helper 调用的站点走项目模式。

---

## 6. 诊断策略

MSBuild 格式：`文件(行,列): warning/error CS2J53xx: [IT-x/分类] 描述`。族共享码（restore/build/test 5302/5303/5304、单文件 5309、C15 各码）不另立。

| 码 | 级别 | 含义 |
|---|---|---|
| CS2J5330 | Info | 自增站点展开（按目标/运算核分桶计数），仅 verbose |
| CS2J5331 | Info | 临时变量提升计数（按角色），仅 verbose |
| CS2J5332 | Error | 事件/指针/其他非法或不归属目标的自增（事件仅 `+=/-=` 归 C13；指针归 C06；只读属性自增基线非法） |
| CS2J5333 | Error | nint/nuint/IntPtr/UIntPtr 目标——算术核归 C41 |
| CS2J5334 | Warning（strict 即 Error） | decimal 自增已展开为 `±1m`；Java 半区方法面归 C34，未启用 C34 不保证产物正确 |
| CS2J5335 | Error | nullable 数值/enum lifted 自增——归 C22 |
| CS2J5336 | Error | dynamic 绑定的自增目标（force 不解除） |
| CS2J5337 | Error | 表达式树 lambda 内自增（改写改变树形态；force 不解除） |
| CS2J5338 | Error | 自定义 op_Increment/op_Decrement 站点：C14 已落地但显式不拥有 ++/--（CS2J5181 移交），且 Increment/Decrement 去糖命名契约尚未冻结，源码不可手调运算符方法 |
| CS2J5339 | Error | 不可安全提升的值用后缀/读-改-写（条件位、查询、lambda 表达式体、字段/构造初始化器、特性实参、`?.` 目标等） |
| CS2J5340 | Warning（strict 即 Error） | ref 返回目标经 ref 别名展开；Java 半区依赖 C01 holder |
| CS2J5341 | Warning（strict 即 Error） | residual 闸门：输出仍存在所有权集合内的 `++/--` 节点（IT-13） |
| CS2J5342-5349 | — | 预留（M2 条件位线性化、C05 双态细化等） |

转换器侧 fail-fast 站点（[UnaryExpressionTransformer.cs:154-193](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/UnaryExpressionTransformer.cs#L154-L193)）在本规则随默认开生效后对「经预处理的项目」成为不可达路径；删除/保留属后续 PR（同 C15 [§6 原则](file:///d:/code/cs2j/docs/superpowers/specs/2026-09-28-c15-checked-unchecked-design.md#L303-L303)），直连 ConversionPipeline 单文件入口继续以其为安全网，相关既有测试一行不改。

---

## 7. 测试计划

全部以 `-c Release` 执行；先写单测矩阵再实现。

### 7.1 Core 单测矩阵（目标种类 × 前缀/后缀 × 上下文）

新增 [NumericBoundaryIncrementTargetUnitTests.cs](file:///d:/code/cs2j/tests/CSharpToJava.ProjectTests)（内联源码 → `CSharpCompilation` → 族 Rewrite（启用 C15+本规则）→ 文本断言忽略空白 + `AssertOutputCompiles`）：

1. **属性**：int/long/enum/byte/uint/decimal 类型可写属性的前缀、独立后缀、值用后缀；接收者带副作用（`GetObj().P++`）断言接收者临时只调一次；`this.P++`、静态属性；只读属性 → CS2J5332；嵌套 `a.B.C++` 逐级接收者提升。
2. **索引器**：单参/多参 `m[i]++`、`m[Next(), k]++`（实参各求值一次、源序正确）、`this[i]++`、接收者带副作用；get-only/set-only 边界；`[IndexerName]` 不影响本规则（member-desugar 默认关或成员在外部程序集时以真身工作；绑定符号始终取 SemanticModel）。
3. **事件**：`e++` 形态输入 → CS2J5332（以 SemanticModel 构造/错误符号防御两条路径）。
4. **enum**：int/byte/long/uint 底层枚举的简单/属性/索引器/数组元素目标，前缀/后缀；回绕边界（byte 枚举 255→0）运行时断言；`[Flags]` 同形；checked 区内复杂目标产出 C15 helper 形态（联调断言）；C05 FromValue 双态（M2，以最小 C05 风格手型模拟成员指纹）。
5. **decimal**：局部/属性/数组元素展开为 `+ 1m`/`- 1m`；CS2J5334 计数；strict 下升级。
6. **无符号残余**：构造 E09 跳过形态——`if (u++ > 0)` 类条件位 v1 CS2J5339；lambda/查询内同；属性 uint 目标 → UnsignedMath 调用 + get/set 单次；byte/ushort 属性窄化 cast；E09 已改写形态（`u = UnsignedMath.AddUInt32(u,1)`）零改写。
7. **nint/IntPtr**：简单/属性目标 → CS2J5333。
8. **自定义运算符**：声明带 `op_Increment` 的类型，在「已去糖」输入（手造 `Increment` 静态方法）下局部/属性目标前缀/后缀展开正确、回转转换链保留；未去糖真身 `++` → CS2J5338；带用户转换回转一例。
9. **ref 返回**：ref 索引器/ref 属性 → ref 别名输出可编译 + CS2J5340。
10. **上下文矩阵**：表达式语句/局部声明/return/throw/yield（支持）；实参列表（M1 仅前序无副作用；M2 线性化多参）；if/while/do/for 条件、`?:`、`&&`、`??`、查询、lambda、字段初始化器、特性实参、表达式树（CS2J5339/5337/5336）。
11. **工程性质**：幂等（二次零 diff）；干净文件字节一致 + BOM；支撑工程路径 clean copy；trivia/`#if` 跟随；临时名碰撞避让（同作用域预置同名变量）。
12. **直映集合零误伤**：int/long/sbyte/short/char 局部/字段/数组元素 `++` 原样；普通数值数组 `arr[i]++` 原样。

### 7.2 项目级 fixture（向 SampleNumericBoundarySolution 追加，不新建 sln）

在 C15 建立的 `tests/SampleNumericBoundarySolution/` 内新增 `IncrementTargets/`：

- Lib：带副作用接收者/索引实参的埋点类（计数器断言 getter/setter/接收者/实参调用次数）、各类型属性/索引器、byte 底层枚举（回绕）、`[Flags]` 枚举、decimal 属性、uint 属性、ref 返回索引器、自定义 op_Increment 类型（与 C14 fixture 协调命名，M2 接入）、一个含全部 CS2J533x 反例的文件。
- Tests（**运行时行为，.NET 半区守门**）：xunit 断言——前缀新值/后缀旧值；getter/setter/接收者/实参**各一次且源序正确**；byte 枚举 255 自增回绕为 0；属性 uint 经 helper 回绕；decimal 展开后值精确；ref 别名写回生效；反例文件诊断码与退出码。探针在去糖后的 C# 上跑，读-改-写错误在 .NET 侧立即红。
- 项目级测试：`result.Success`/统计桶/中间树文本（含 `__cs2j_inc_`、无所有权残余 `++/--`）、**中间树 `dotnet build` 与 `dotnet test -c Release` 退出码 0**；仅 IT-6 站点的项目也注入 Cs2jUnsignedMath（§4.5-3 回归）；干净工程字节复制/BOM。

### 7.3 Release build/test

- `dotnet build -c Release`（CSharpToJavaConverter.slnx 全绿）；
- `dotnet test -c Release` 三工程：CSharpToJava.Tests、CSharpToJava.ProjectTests、CSharpToJava.ConversionTests。

### 7.4 Java 半区冒烟（默认开门禁项，非阻塞合入）

- 代表性样本（属性/索引器 enum/uint 自增 + 后缀值用）经完整链路产出可编译 Java：getter/setter/`getItem/setItem` 调用次数形态正确，`UnsignedMath.add*` 方法名与 compat 逐字一致，回绕行为双端一致；
- 自定义运算符样本待 Increment/Decrement 命名契约冻结（开放问题 1）后与 C14 联调；decimal/ref 样本记录 CS2J5334/5340 的依赖门（C34/C01），不纳入「可编译 Java」断言。

### 7.5 residual 闸门（IT-13）

1. 输出中间树所有非支撑工程 .cs 文件 parse 后扫描 `PreIncrement/PreDecrement/PostIncrement/PostDecrement`，经所有权分类器断言：残留节点的 `(TargetKind, ArithmeticCore)` 全部属于直映白名单；否则 CS2J5341，strict 退出码非零。
2. 支撑工程豁免：`Cs2jUnsignedMath/` 目录不扫描（继承 C15 豁免集）。
3. 上游回归：E09/C15/enum 既有 fixture 启用本规则后统计零变化、中间树 build/test 全绿；E09 产物（赋值/helper 调用）中无 `++/--` 被误改。

### 7.6 CLI / 一致性 / 转换器回归

- 关闭态（2026-10-01 基线 pass 已默认开，以规则节 `EnableIncrementTargets=false` 模拟原「默认关」验收）：规则零执行、无 C17 新增行为、链序不变；启用后 `--list-passes` 显示 catalogItems 含 C17；无新 toggle、无新适配器。
- 转换器既有测试（[tests/CSharpToJava.Tests](file:///d:/code/cs2j/tests/CSharpToJava.Tests) 中直连 ConversionPipeline 的自增相关用例）持续全绿、零改动；默认开评审时再统一处置预期，本规则不迁就旧断言改测试。

---

## 8. 默认开启验收门槛

本规则**无独立默认开关位**（随 `numeric-boundary` pass；`EnableIncrementTargets` 仅为规则节 kill-switch）。pass 级 `defaultEnabled: false → true` 的总门槛由 C15 [§8](file:///d:/code/cs2j/docs/superpowers/specs/2026-09-28-c15-checked-unchecked-design.md#L355-L366) 统一持有；本规则追加的必备条件：

1. §7 全部单测/fixture/Release build/test/residual/CLI 绿；副作用单次求值探针与回绕运行时断言全绿；
2. 与 C15 共处的 checked 复杂自增联调通过（CS2J5324 对应站点全部由 IT 规则接管或显式失败，无双重处理）；
3. **Java 半区**：§7.4 冒烟通过；转换器 L169/L173/L182/L184/L189/L192 在预处理路径上证实不可达的本规则所属类（删除留后续 PR）；
4. **csharp-sdk 扫描**：目标种类×上下文命中矩阵、`DeferredByReason`（5333/5334/5335/5338/5339/5340）实测清单，每条 Error/Warning 人工定性（真不支持 vs 分析器过保守）；条件位后缀（CS2J5339）与 decimal 门（5334）的命中率作为 M2/开放问题 2、3 的排期输入；
5. sdk 基线不退化：上游 pass（E09/enum/C15）统计零变化；总耗时增量可接受（逐文件两遍遍历，无跨文件状态）。

---

## 9. 里程碑

| 里程碑 | 内容 | 预估规模 |
|---|---|---|
| M0 接缝与骨架 | C15 合入基上增 `INumericBoundaryRule` 接缝（§4.5-1）、规则 Options 节/统计段、fixture 目录占位；Analyzer 四元组分类 + 直映白名单零误伤单测；C15 回归全绿 | Core ~250 行（含接缝） |
| M1 属性/索引器/enum/无符号残余 | IT-1~IT-6、IT-10、IT-11（保守锚点）+ lifter + 副作用探针 fixture + residual 闸门 + CS2J5330/5331/5332/5339 + 注入判定族化（§4.5-3） | Core ~600 行 + fixture |
| M2 特殊核与硬位置 | IT-7/8/9（decimal 门、自定义运算符联 C14、ref 别名）、C05 FromValue 双态、CS2J5333-5341 全套、M2 语句线性化（决策 14b）按 sdk 命中率取舍、Java 冒烟 | Core ~400 行 |
| M3 默认开评审 | sdk 命中矩阵、开放问题收口、转换器消费核对底稿；随 C15 §8 总门槛单行默认开 | 文档/数据 |

---

## 10. 风险

1. **双求值/求值序错误（最高危）**：属性/索引器展开后接收者或实参出现两次，会「能编译、行为错」。缓解：接收者/实参一律临时化（白名单除外），fixture 埋点计数探针为硬回归；Analyzer 的副作用谓词直接对标 E09 已验证实现。
2. **条件执行被提前**：把 if/while 条件内的后缀副作用提到语句首会改变语义（E09 已踩坑）。缓解：锚点可行性白名单 + CS2J5339；M2 线性化只重建整条无条件语句，绝不跨条件边界；条件位 v1 显式失败不硬做。
3. **与 E09/C15 产物的交互误判**：把 helper 调用或已赋值节点二次展开、checked 算术核选错名。缓解：只认 `++/--` 语法节点与语义绑定；文件内规则顺序钉死（决策 17）；共处 fixture 与「E09/C15 统计零变化」为硬回归。
4. **C05 落地形态变化 / C11/C12/C14 契约后续调整**：C11/C12/C14 已落地（默认关），但 enum C05 双态未落地、C14 的 Increment/Decrement 命名契约尚未冻结，后续均可能调整。缓解：分类全部以 SemanticModel 符号为据，不认文本/方法名；真身形态与去糖形态双兼容（§3.3）；成员指纹（FromValue/Value、Increment/Decrement）集中在 SyntaxFacts 单点。
5. **decimal 静默换形**：展开后 `d + 1m` 在 C34 缺失时仍会流入 Java 静默错误面（[BinaryExpressionTransformer.cs:88-94](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/BinaryExpressionTransformer.cs#L88-L94)）。缓解：CS2J5334 逐站点响亮计数、strict 即失败；是否整体 Error 移交留开放问题 2，默认开前必须拍板。
6. **临时变量作用域/碰撞/trivia**：提升位置错误导致编译失败或 `#if` 失衡。缓解：锚点解析与 lifter 直接复刻 NullCoalesce/C31/E09 三套已验证实现；碰撞避让、trivia 随首声明单测；verify build 双开兜底。
7. **ref 别名的 Java 半区幻觉**：C# 合法的 ref local 输出可能被误判为「已解决」。缓解：CS2J5340 显式标记 C01 依赖，strict 下失败，不列入 Java 可编译冒烟。
8. **自定义运算符回转转换链**：`op_Increment` 结果经用户转换回目标类型的链重建错误会产生值偏差。缓解：只从 `IIncrementOrDecrementOperation` 取绑定与转换，不自推规则；与 C14 的共享渲染器（开放问题 1）在 M2 前定案；.NET 双端逐值断言。

---

## 11. 并行协调

- **C15 锚点（硬前置，同族）**：base 必须含已合入的 C15 分支（§12）；本任务只增 `Rules/IncrementTargets/`、自有测试与 fixture 追加；根层扩展走 §4.5 授权流程并携带 C15 回归；checked 复杂自增从 CS2J5324 移交本规则（码位不删，规则所有权表驱动停用）；CatalogItems 单行追加由本任务完成。
- **C11 Property / C12 Indexer / C13 Event（member-desugar 族，均已实现、默认关）**：C11 PD-6（[C11 L173](file:///d:/code/cs2j/docs/superpowers/specs/2026-09-28-c11-property-desugar-design.md#L173-L173)）与 C12 IX-8（[C12 L189](file:///d:/code/cs2j/docs/superpowers/specs/2026-09-28-c12-indexer-desugar-design.md#L189-L189)）已承担属性/索引器**语句位**自增的方法化展开——两序等价（§3.3），链上谁先运行结果同构；本规则负责两规则未覆盖的残余（外部元数据成员、CS2J5119/CS2J5137 值消费位 plan 跳过、ref 索引器保留形态、枚举/decimal/无符号元素类型）。事件 `++/--` 基线非法（CS2J5332），事件 `+=/-=` 完全归 C13。
- **C14 Operator（member-desugar 族，已实现、默认关）——对其[开放问题 4（L395）](file:///d:/code/cs2j/docs/superpowers/specs/2026-09-28-c14-operator-desugar-design.md#L395-L395) 的结论建议**：C14 已按其 [§11 C17 协调节](file:///d:/code/cs2j/docs/superpowers/specs/2026-09-28-c14-operator-desugar-design.md#L363-L363) 先行划界（SB-IncDec 行 [L168](file:///d:/code/cs2j/docs/superpowers/specs/2026-09-28-c14-operator-desugar-design.md#L168-L168)），建议维持「**声明归 C14、站点归 C17**」的拆分：
  1. `op_Increment`/`op_Decrement`（及未来 checked 变体）的**声明去糖**物理上留在 `MemberDesugar/Operators/`，作为 C14 的增量规则，由 C14 owner 实施——现状 C14 对其不建 plan（[OperatorRule.cs:188](file:///d:/code/cs2j/src/CSharpToJava.Core/MemberDesugar/Operators/OperatorRule.cs#L188-L188)）、仅发 CS2J5181 Info（[OperatorUseSiteRewriter.cs:80-86](file:///d:/code/cs2j/src/CSharpToJava.Core/MemberDesugar/Operators/OperatorUseSiteRewriter.cs#L80-L86)）；
  2. `++/--` **使用站点**（含属性/索引器自定义类型目标的读-改-写、旧值/新值、临时提升、回转转换链）归本规则；C14 维持其 CS2J5181 计数与 OC-9（[C14 L189](file:///d:/code/cs2j/docs/superpowers/specs/2026-09-28-c14-operator-desugar-design.md#L189-L189)）残余白名单不变；**待办**：C14 冻结去糖命名契约（`Increment`/`Decrement`，checked 后缀随 C14 决策表；现状 `OperatorNames.cs` 无此条目，开放问题 1），本规则按绑定符号限定名调用；
  3. 跨族共享的「运算符应用渲染器」上收到 `Core/Preprocessing/Expressions/`（§4.4、开放问题 1）；命名契约冻结前本规则对真身站点发 CS2J5338，不做半成品。
- **E04 enum（已实现）/ C05 enum-declaration（未落地）**：E04 不碰 `++/--`（[EnumSyntaxRewriter L461-464](file:///d:/code/cs2j/src/CSharpToJava.Core/EnumPreprocessor/EnumSyntaxRewriter.cs#L461-L464)），enum 自增站点完整留给本规则；与 E04 产物共处（`(U)`/`(E)` cast 形态）单测钉死。C05 落地后按 D12 消费 FromValue/Value 双态（决策 7），C05 的 plan 级回滚与本规则逐文件改写互不重写同一节点（链序天然分层）。
- **C34 decimal**：本规则只产 `±1m` 形状并以 CS2J5334 标记依赖；C34 负责全部 decimal 二元（含本规则产物）的 compat 方法发射；两规则在同一 pass 的 Rules 目录并列（`DecimalOps/`），算术核归属以「节点种类」切分（本规则只拥有 `++/--` 站点，展开后不再识别其子节点）。
- **C41 nint / C22 nullable / C06 unsafe / C01 ref / C35 null-conditional**：分别对应 CS2J5333/5335/5332/5340/5339 显式指路，不做半成品；各规则落地后仅替换本规则 Analyzer 的算术核/目标渲染，码位与诊断消息保持稳定。
- **C31（Index/Range）/ C35（null-conditional）/ expression-desugar 族**：TempLifter 形状遵循 C31 冻结契约，未来共同上收（§4.4）；`c[^1]++` 类组合：C31 先显式化索引（接收者临时），本规则后做自增读改写，两 pass 临时变量并存不冲突（[C31 决策表](file:///d:/code/cs2j/docs/superpowers/specs/2026-09-28-c31-index-range-design.md#L74-L74)已声明算术归约归 C17）。
- **热文件冲突解法**：本任务生产代码改动集 = `NumericBoundary/Rules/IncrementTargets/*.cs`（全新目录）+ §4.5 根层小接缝（C15 文件，带其回归）+ 适配器一行注入判定 + PassCatalog CatalogItems 一行 + 测试/fixture 追加；与 C22/C34/C41 无文件重叠（各自 Rules 子目录）；fixture 以 `IncrementTargets/` 子目录隔离。

---

## 12. Worktree 分配（2026-10-01 更新 base 并进入实施）

- 分支：`feature/preproc-c17-increment-target`
- 目录：`.worktrees/preproc-c17-increment-target`
- base：**`0ba86416`**（`docs: C40 worktree base 重选为 dev-3.x 最新提交 28ae4594`，dev-3.x HEAD @ 2026-10-01，完整 SHA `0ba86416b2fc3aa49551616208ec1b90f65c76bb`，已 `git rev-parse --verify` 校验存在）。~~原 base `953293b9`~~（完整 SHA `953293b908e9143437c45f287fb4277e890c398e`，2026-10-01 前移）。
- **前置现状（2026-10-01 基线核实，原「实现前先合入 C15 分支」要求已满足，无需再合）**：
  1. C15 已合入 dev-3.x：`NumericBoundary/` 根层（[NumericBoundaryPreprocessor.cs](file:///d:/code/cs2j/src/CSharpToJava.Core/NumericBoundary/NumericBoundaryPreprocessor.cs)、Analyzer/Models/SyntaxFacts/CheckedExprRewriter）与适配器存在，pass 已 **Implemented 且 defaultEnabled=true**；
  2. C34 已合入：`NumericBoundary/Rules/DecimalOps/` 七文件在树，descriptor catalogItems 真值为 `"C15, C34"`，本任务单行加宽为 `"C15, C34, C17"`；C34 既已落地，按开放问题**裁决 2，CS2J5334 直接以 Info 级别实施**（不再经 Warning 过渡）；
  3. fixture `tests/SampleNumericBoundarySolution/`（Lib/App/sln，含 DecimalOps 子目录）与测试工程 `tests/CSharpToJava.ProjectTests/`（含 NumericBoundary* 用例）均在树，本任务按 §7 追加，不新建 sln/工程。
- 创建命令（在仓库根、PowerShell 下执行；2026-10-01 实施时实际执行；base 文档更新提交落在 dev-3.x 的 `0ba86416` 之上，故分支从更新后的 dev-3.x HEAD 切出，内容 = `0ba86416` + 本文档修订）：

```powershell
git worktree add .worktrees/preproc-c17-increment-target -b feature/preproc-c17-increment-target dev-3.x
```

- worktree 内交付面遵循 foundation plan「并行开发交付包模板」（[preprocessor-foundation_plan.md:108-117](file:///d:/code/cs2j/.trae/documents/preprocessor-foundation_plan.md#L108-L117)）：Core 仅新增 `NumericBoundary/Rules/IncrementTargets/`（+ §4.5 授权的根层小接缝与适配器一行收口）、Core 单测 + SampleNumericBoundarySolution 追加 + Release build/test + residual 闸门 + CLI 断言；不新建支撑工程、不新增支撑库方法；注册表除 CatalogItems 单行外零改动；不触转换器代码（§6 消费清单为后续 PR 输入）。按 §9 里程碑逐提交（M0→M2，实现一部分提交一部分）；M3 为默认开评审/数据采集门，随 C15 §8 总门槛统一持有，无独立生产代码。

---

## 附：开放问题裁决（2026-09-29 拍板）

> **拍板目标**：支持全部 C# 语法/语义、便利下游 C#→Java 转换。统一口径：① 每种合法 C# 构造必须有语义等价的可编译 Java 终态，v1 可显式诊断门控但必须登记终态去向，禁止长期静默丢弃；② 中间树始终是普通合法 C#，Java 侧差异集中到 compat/映射层收口；③ 语义精确优先于分配/性能，优化变体须有画像证据；④ 预处理默认开前转换器旧臂由独立 PR 退役、不留双轨窗口，CheckPass 安全网永久保留；⑤ 合成类型名 `Cs2j<Role>_<Member>_<n>`、合成成员/临时 `_cs2j…`、让位改名 `<Name>_cs2j<Purpose>`（单下划线后缀）；⑥ 跨 pass 顺序依赖一律显式 RunAfter 固化；⑦ Implemented+默认关观察期不超过两个里程碑窗口；⑧ worktree 创建命令维持 base `953293b9`，创建后第一步 rebase 到当时 dev-3.x HEAD（本裁决时为 `ef70e5a2`），stub 计数红灯由首个锚点翻转 PR 按真值收口。【2026-10-01 实施更新：base 直接前移至 dev-3.x HEAD `0ba86416`，命令见 §12；C15 锚点与 C34 均已合入（pass 已 Implemented、默认开），无需再合分支，catalogItems 按真值登记 `"C15, C34" → "C15, C34, C17"`】

**裁决（按原题号）：**

1. **运算符命名契约与共享渲染器**：`Increment/Decrement`（含 checked 变体）去糖命名由 C14 owner 在 `OperatorNames.cs` 冻结，作为 C17 M1 开工的**阻塞前置**；跨族「运算符应用/转换链渲染器」与共享 TempLifter 上收到 `Core/Preprocessing/Expressions/`，时点定为 C17 M2（第三套同构实现出现即收口，不等待）；CS2J5338 维持至契约冻结。
2. **decimal 站点（C34 落地前）**：展开为 `±1m` + CS2J5334 Warning（strict 下 Error），不走 C15 式整体 CS2J5334 Error 阻断；C34 合入后该诊断降 Info。【2026-10-01 实施更新：C34 已合入 dev-3.x（`ec882491`，`Rules/DecimalOps/` 在树），CS2J5334 直接按 **Info** 实施，不再经 Warning 过渡】
3. **条件位后缀值用 `x++`**：v1 CS2J5339 显式失败；**终态做条件提取/语句求值序列线性化收编**（sdk 出现真实命中即 M3 立项）；ref 形参 PostInc helper 路线否决（不引 C01 Java ref 依赖），CS2J5339 不作为永久拒绝。
4. **规则开关粒度**：`EnableIncrementTargets` 默认 true 随 pass，仅保留 pass 级 kill-switch；不暴露 CLI、不按目标种类细分。
5. **链序终局**：加显式 RunAfter 边固化 member-desugar → numeric-boundary（由本 pass PR 写入 numeric-boundary descriptor；若 C15 翻转 PR 已先写则核对一致），不靠声明序 tiebreak；foundation plan Stub 表声明序（NB 先于 MD）与实际序背离处在计划文档回填说明。
6. **C# 14 用户定义复合赋值**：M1 首步以探针核实仓库 Roslyn 对 `ICompoundAssignmentOperation` 用户复合绑定的支持；不支持时 Analyzer 降级为保守显式失败 + 诊断指路 C14。此为执行预案，不再悬置。

---

### 原问题记录（裁决前）

1. **C14 开放问题 4 的物理归属与跨族渲染器**：本设计建议「op_Increment/op_Decrement 声明去糖归 C14（`MemberDesugar/Operators/` 增量）、`++/--` 站点归 C17」。**2026-09-29 现状**：C14 已按此方向落地——不建 inc/dec plan、声明处发 CS2J5181（[OperatorUseSiteRewriter.cs:80-86](file:///d:/code/cs2j/src/CSharpToJava.Core/MemberDesugar/Operators/OperatorUseSiteRewriter.cs#L80-L86)），但 `Increment/Decrement`（含 checked 变体）去糖命名契约**尚未在 `OperatorNames.cs` 冻结**，仍需 C14 owner 拍板，本规则 CS2J5338 在冻结前维持；同时需定案跨族「运算符应用/转换链渲染器」与共享 TempLifter 的上收时点与落点（建议 `Core/Preprocessing/Expressions/`）。
2. **decimal 站点在 C34 落地前的策略**：展开为 `±1m` + CS2J5334 Warning（strict Error，本设计推荐，先消 fail-fast 且依赖响亮可见）还是维持 C15 式整体 CS2J5334 Error 移交（零 Java 半区风险但 sdk 命中即阻断）？取决于 sdk 中 decimal 自增命中率与 C34 排期。
3. **条件位后缀（if/while/`?:`/`&&` 内的值用 `x++`）**：v1 显式 CS2J5339 失败（推荐，与 E09 现状一致无回归）；M2 是否投入「条件提取/语句求值序列线性化」扩大覆盖，或评估 ref 形参 PostInc helper 路线（会引入 C01 Java ref 依赖，本设计不推荐），以 sdk CS2J5339 命中率决定。
4. **规则开关粒度**：`EnableIncrementTargets` 默认 true（规则随 pass，本设计推荐）还是独立 `--` 开关/overrides 节支持按目标种类 skip？后者会形成族内多套事实标准，建议仅保留 kill-switch 不暴露 CLI。
5. **链序终局**：2026-09-29 按注册表 Kahn 拓扑手工推演，两 pass 同启时实际序已是 **member-desugar → numeric-boundary**（同 catalog §3.1/§3.3 建议序；推演依据见 §3.3，**待运行时/链序测试复核**）。待拍板：①是否在 member-desugar 或 numeric-boundary 翻转时加显式 RunAfter 边固化该序，还是继续依赖声明序 tiebreak（本规则两种序都兼容，零改动）；②foundation plan Stub 表声明序（NB L92 先于 MD L95）与实际序的背离是否需要回填说明。建议在 member-desugar 默认开翻转评审用两序等价论证（§3.3）+ sdk 残余命中率拍板。
6. **C# 14 用户定义复合赋值的工具链前提（2026-09-29 新增）**：决策 21 要求 Analyzer 经 SemanticModel（`ICompoundAssignmentOperation`）识别显式用户复合运算符绑定。仓库当前 Roslyn 版本对 C# 14 的语言支持程度、以及 `ICompoundAssignmentOperation` 在用户复合绑定下暴露的符号/转换形态，**未在本次核对中证实，待 C17 实现期核实**；若该版本不支持，Analyzer 的查询义务降级为「保守显式失败 + 诊断指路 C14」。
