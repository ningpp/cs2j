# C47 相等性与字符串拼接调用点语义物化（`equality-concat`）设计

**日期**: 2026-09-29
**范围**: 新增 C#→C# 预处理器 `equality-concat`（Core `EqualityConcat/` + CLI 薄适配器），按 **SemanticModel/IOperation 判定的操作数语义类别**（而非语法形态）把编译器隐式绑定的相等/拼接调用点物化为显式 BCL 调用：string `==`/`!=` → `string.Equals`、object 静态类型同一性（含 `(object)` 逃逸）→ `object.ReferenceEquals`、string `+`/`+=` 链 → `string.Concat`、委托 `==` → `Cs2jPrelude.DelegateOps.Equals`、record 合成 `==` → `object.Equals`。`bool?`/`T?` 提升相等划归 C22，元组 `==` 已归 E06，本 pass 只做边界 residual 策略。不改任何 C#→Java 转换代码；Java 半区缺口（compat/TypeMappings）以依赖清单形式登记，由独立 Java 半区 PR 收口
**状态**: 设计稿，待评审
**注册表**: 拟新增 stub `equality-concat`（实施期在 [PassStubCatalog.cs](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassStubCatalog.cs) 注册；本设计只提案不落地，§3.2 给完整字段）
**base**: `9b99c60b`（`docs: C46 成员签名显式化设计`，已 `git cat-file -t` 核为 commit；只读核实，未执行任何 git 写操作）

---

## 1. 背景与目标

### 1.1 catalog 定位与★核实门

C47 在 [preprocessor-catalog.md:924-937](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L924-L937) 带★核实门：若转换器已全部内联吸收，本条降为观察项（文档主体改写 CsCore 收敛计划）；若部分覆盖或静默错值，则与 C31/C34 同列 §4 风险 12/21 最高危静默错误输出族，P1 立即补。2026-09-29 按「证据」行完成全量只读核实，结论见 §1.2。

### 1.2 ★现状核实证据表（逐子项 A/B/C 档）

档位定义：**A** = 已正确内联（语义等价且有测试护栏）；**B** = 部分覆盖（主路径对、有明确错值缺口）；**C** = 静默错值/不可编译（不报错、产物语义错误或不可编译）。

| 子项 | 档 | 证据（文件行号） | 现状行为与缺口 |
|---|---|---|---|
| ① string `==`/`!=`（双侧静态 string、string 与 null 字面量） | **A**（谓词本身有缺陷，见②） | [BinaryExpressionTransformer.cs:96-104](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/BinaryExpressionTransformer.cs#L96-L104)、IR 路径 [:44-47](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/BinaryExpressionTransformer.cs#L44-L47)、[BuildObjectsEqualsIR :263-277](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/BinaryExpressionTransformer.cs#L263-L277)、谓词 [IsStringEquality :323-327](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/BinaryExpressionTransformer.cs#L323-L327) | 发 `Objects.equals(l, r)` / `!Objects.equals(...)`，null 安全且为序数比较，与 C# string `==` 语义一致；null 字面量操作数显式排除后落入同一性直映，结果恰好等价。测试已钉死：[ObjectsImportAndUnresolvedTypePropertyTests.cs:17-44](file:///d:/code/cs2j/tests/CSharpToJava.Tests/ObjectsImportAndUnresolvedTypePropertyTests.cs#L17-L44)、[StringTests.cs:185-191](file:///d:/code/cs2j/tests/CSharpToJava.ConversionTests/Categories/StringTests.cs#L185-L191) |
| ② object 静态类型比较 / `(object)` 逃逸 | **C** | 同一谓词 [IsStringEquality :323-327](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/BinaryExpressionTransformer.cs#L323-L327)（「任一操作数 `.Type` 为 string」，**不看实际绑定符号**）；全 `src` grep 无 `ReferenceEquals` 转换处理 | `(object)s == t`、`object o = s; o == "a"`：C# 重载决议绑定 object 同一性运算符，转换器因另一侧是 string 误发 `Objects.equals`（**值相等**）→ 运行时构造串静默错值。显式 `object.ReferenceEquals(a,b)` 调用无映射（TypeMappings 无 System.Object 条目），经 [:550](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/InvocationExpressionTransformer.cs#L550) 产 `Object.referenceEquals(...)` 不可编译 Java。零测试 |
| ③ string `+`（多操作数链、`+=`、与非字符串混合） | **C** | [BinaryExpressionTransformer.cs:106-116](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/BinaryExpressionTransformer.cs#L106-L116)（仅 double/single 包 `DoubleHelper.toString`，仍产 Java `+`）；`+=` 经 [AssignmentTransformer.cs:76](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/AssignmentTransformer.cs#L76) 与属性路径 [:95](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/AssignmentTransformer.cs#L95) | C# `null + "a"` 得 `"a"`（null 替换空串），Java 直映得 `"nulla"`；`s += null` 同病。测试反向钉死 Java `+`：[StringTests.cs:194-198](file:///d:/code/cs2j/tests/CSharpToJava.ConversionTests/Categories/StringTests.cs#L194-L198)，无任何 null 拼接用例。**桥已存在**：显式 `string.Concat` → [TypeMappings.json:6319-6323](file:///d:/code/cs2j/config/TypeMappings.json#L6319-L6323) → [StringConcatRewriter.cs:31-45](file:///d:/code/cs2j/src/CSharpToJava.Core/Java/Rewriters/StringConcatRewriter.cs#L31-L45)（两条管线均接线：[ProjectPasses.cs:682](file:///d:/code/cs2j/src/CSharpToJava.Core/Pipeline/Passes/ProjectPasses.cs#L682)、[SingleFilePasses.cs:353](file:///d:/code/cs2j/src/CSharpToJava.Core/Pipeline/SingleFilePasses.cs#L353)）→ [StringHelper.concat](file:///d:/code/cs2j/java/csharptojava-compat/src/main/java/io/github/ningpp/compat/StringHelper.java#L7-L17)，null 跳过已正确（[:1314-1317](file:///d:/code/cs2j/java/csharptojava-compat/src/main/java/io/github/ningpp/compat/StringHelper.java#L1314-L1317)） |
| ④ `bool?` 提升相等 | **A-**（转换器内联；归属 C22） | [BinaryExpressionSupport.TryNullableBoolEquality :82-115](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Utilities/BinaryExpressionSupport.cs#L82-L115)；[NullableBoolComparisonTests.cs](file:///d:/code/cs2j/tests/CSharpToJava.Tests/NullableBoolComparisonTests.cs) | 覆盖 `bool? == true/false`、双侧 `bool?`、条件访问三语法面，发 `Objects.equals(x, Boolean.TRUE/FALSE)`。一般 lifted 相等的完整真值表已由 [C22 决策 5](file:///d:/code/cs2j/docs/superpowers/specs/2026-09-29-c22-nullable-value-design.md) 收编 |
| ④ `int?`/其他 `T?` 提升相等 | **C**（归 C22，C47 只 residual） | 默认路径 [BinaryExpressionTransformer.cs:114-116](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/BinaryExpressionTransformer.cs#L114-L116) 裸 `==`；[C22 §1.3-1/2](file:///d:/code/cs2j/docs/superpowers/specs/2026-09-29-c22-nullable-value-design.md) 已定性 | Java 装箱类型 `==`：Integer 缓存侥幸 + null 拆箱 NPE；双 null 应为 true。属 C22 域，本 pass 不重写，但须显式 residual 诊断，禁止假装覆盖 |
| ⑤ 元组 `==`（含 `(a,b)==("x","y")`） | **A** | [TupleToStructPreprocessor.cs:741-780](file:///d:/code/cs2j/src/CSharpToJava.Core/TupleToStruct/TupleToStructPreprocessor.cs#L741-L780) 改 `.Equals(...)`；生成 struct 逐元素 `EqualityComparer<T>.Default.Equals`：[TupleStructFileContent.cs:120-152](file:///d:/code/cs2j/src/CSharpToJava.Core/TupleToStruct/TupleStructFileContent.cs#L120-L152)、[EqualExpr :206-207](file:///d:/code/cs2j/src/CSharpToJava.Core/TupleToStruct/TupleStructFileContent.cs#L206-L207)；测试 [TupleToStructRewriterUnitTests.cs:457](file:///d:/code/cs2j/tests/CSharpToJava.ProjectTests/TupleToStructRewriterUnitTests.cs#L457) | E06 默认开且链位早于本 pass；字符串元素经 EqualityComparer 自然序数比较，字符串元组特例被吸收。残留边角：元组与 `null` 字面量比较（一侧非 tuple 类型，不触发改写），列 EC-7 residual |
| ⑥ 委托 `==`（调用列表值相等） | **C** | E05 只改 `+/-/+=/-=`：[DelegateSyntaxRewriter.cs:33-93](file:///d:/code/cs2j/src/CSharpToJava.Core/DelegatePreprocessor/DelegateSyntaxRewriter.cs#L33-L93)、[:155-212](file:///d:/code/cs2j/src/CSharpToJava.Core/DelegatePreprocessor/DelegateSyntaxRewriter.cs#L155-L212)，无相等分支；[CsPrelude.cs](file:///d:/code/cs2j/src/Cs2jPrelude/CsPrelude.cs) 仅 Combine/Remove；Java 多播代理 equals 为同一性：[DelegateHelper.java:104-110](file:///d:/code/cs2j/java/csharptojava-compat/src/main/java/io/github/ningpp/compat/DelegateHelper.java#L104-L110)，唯一值相等实现是**私有** [:72-74](file:///d:/code/cs2j/java/csharptojava-compat/src/main/java/io/github/ningpp/compat/DelegateHelper.java#L72-L74) | Java 委托即函数式接口，`==` 恒同一性：`(a+a)==(a+a)` 两代理实例 → false（C# true）；`o1.M==o1.M` 两方法引用实例 → 可能 false（C# true）。零测试。**compat 无公共 DelegateOps.equals**，列入 §5.2 Java 半区依赖 |
| ⑦ record（class/struct）合成 `==` | **C** | 调用点解析要求源内声明运算符：[BinaryExpressionSupport.IsSourceUserDefinedOperator :158-162](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Utilities/BinaryExpressionSupport.cs#L158-L162)（要求 `HasSourceDeclaration`）；record 合成 op 为 `MethodKind.UserDefinedOperator` + `IsImplicitlyDeclared`（无 DeclaringSyntaxReference）→ 不命中 → 裸 Java `==` 同一性；record 侧只生成值语义 `equals()`：[RecordTransformer.cs:320-357](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Type/RecordTransformer.cs#L320-L357) | 同值两 record 实例 C# `==` 为 true、Java 同一性 false，静默错值。零调用点测试。与 E11 RecordReadOnly/C27 record-with 衔接（两者都不改调用点） |

**总结论：B 档（部分覆盖），不是观察项**。string 主路径（①）、bool? 特判（④一部分）、元组（⑤）已内联且有护栏；但 ②object 逃逸、③拼接 null 语义、⑥委托值相等、⑦record 值相等四个子面为 C 档静默错值，`int?` 一般提升相等（④）为 C 档且已划归 C22。按 catalog 核实门，**C47 以 P1 完整物化实施**（§3 选独立 pass），与 C31/C34 同级。转换器内 ① 的内联暂不删除（单文件直连路径安全网），默认开启后另立 PR 收敛（CsCore 方向，§8/§10）。

### 1.3 C#/Java 语义差异总表

| 构造 | C# 语义（事实源：Roslyn 绑定） | Java 直映语义 | 差异性质 |
|---|---|---|---|
| `string == / !=` | 绑定 `string.op_Equality`，大小写敏感序数比较，null 安全（null==null 为 true） | `String` 引用上 `==` = 同一性；字面量驻留侥幸 | 静默错值（① 已内联） |
| object 静态类型 `==`（含 `(object)` 逃逸） | 预定义引用同一性运算符 | 同一性（但转换器误判成 string 值相等） | 转换器误分类静默错值（②） |
| `string + x` / `+=` | 编译器 lowering 为 `string.Concat`；null 操作数→空串；值类型装箱 `ToString()` | `+` 把 null 转 `"null"`；其余多值经 `String.valueOf` | 静默错值（③） |
| `T? == T?`（值类型提升） | 双 null→true、单 null→false、有值比底层值 | 装箱引用同一性 + null 拆箱 NPE | 静默错值（C22 域） |
| 元组 `==` | 逐元素值相等（string 元素序数值相等） | 无运算符 | E06 已物化（⑤ A） |
| 委托 `==` | 同为 null，或调用列表等长且逐项（方法+接收者）相等；`(a+a)==(a+a)` 为 true | 函数式接口/代理同一性；多播代理 equals 也是同一性 | 静默错值（⑥ C） |
| record `==` | 编译器合成 op_Equality：全部字段/组件值相等 | record 有值 `equals()` 但 `==` 仍同一性 | 静默错值（⑦ C） |
| LINQ 表达式树内上述节点 | 树形态承载语义（`Expression.Equal` 等节点类型） | — | 共同禁区，跳过（§2 末行、§6） |

### 1.4 目标

预处理后中间树在用户代码范围内（支撑工程除外）：绑定为 string/object/委托/record 合成运算符的 `==`/`!=` 与绑定为字符串拼接的 `+`/`+=` 节点**零裸残留**（residual 闸门按 IOperation 分类计数，不做文本 grep），产物是可编译、可直接在 .NET 上跑运行时断言的 C#；每个被改写表达式的子表达式求值次数与顺序与 C# 严格一致；不可判定/提升待 C22/表达式树/dynamic 站点给显式诊断，禁止同一性直译与注释占位流入 Java 产物。

## 2. 关键设计决策表

| # | 决策点 | 结论 | 依据/备注 |
|---|---|---|---|
| 1 | **判定真相源** | 以 `SemanticModel.GetOperation(node)` 得到的 `IBinaryOperation.OperatorKind`（kind 掩码 + `UserDefined`/`Nullable`/`Dynamic`/`Checked` flag）为主判据，`GetSymbolInfo(node).Symbol`（`IMethodSymbol.MethodKind`/`ContainingType`/`IsImplicitlyDeclared`）与 `GetTypeInfo().ConvertedType` 为交叉验证；禁止按 token 文本或「操作数是不是 string」猜类别 | ② 的根因即 [IsStringEquality :323-327](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/BinaryExpressionTransformer.cs#L323-L327) 只看 `TypeInfo.Type` 不看绑定。Roslyn 4.12.0（[Core.csproj:12](file:///d:/code/cs2j/src/CSharpToJava.Core/CSharpToJava.Core.csproj#L12)）枚举外形以 M0 探针钉死（§9） |
| 2 | EC-1 string `==`/`!=` 物化 | `OperatorKind` 去 flag 后为 `StringEquals`/`StringNotEquals`（以探针实名为准）→ `string.Equals(a, b)` / `!(string.Equals(a, b))`（静态两参重载，ordinal、null 语义与 C# 一致）。**常量驻留串不豁免**：双侧字面量也统一改写，residual 判定不看字面量 | 静态两参而非实例 `a.Equals(b)`：后者 null 左值 NPE。Java 半区映射缺口见 §5.2-J1 |
| 3 | EC-2 object 同一性（含逃逸） | kind 为 `ObjectEquals`/`ObjectNotEquals` → `object.ReferenceEquals(a, b)` / `!…`。该 kind 由编译器在「任一操作数静态类型 object、绑定预定义引用运算符」时给出，天然覆盖 `(object)s == t`、`o == "a"`、引用类型无重载 `==` 的默认情形 | 不自行用 `ConvertedType == object` 推断，避免与用户隐式转换场景互相误判。Java 半区见 §5.2-J3 |
| 4 | EC-3 链式 `+` 的 Concat 折叠 | kind 为 `Concatenate` 的 Add 节点：自底向上收集左嵌套链全部操作数（`a+b+c` → 三参），折叠为**单个** `string.Concat(o1, o2, …)`；嵌套右链/条件表达式内的拼接节点各产独立 Concat 调用，不跨控制流边界展平。折叠只在「同一棵纯 Add 拼接子树」内进行 | C# 编译器自身即把整链 lowering 为单个 `string.Concat(params)`；不折叠则 null 语义仍错（Java 逐段 `+`）。右结合 `a + (b + c)` 保持嵌套 Concat（求值序一致，结果等价） |
| 5 | 非 string 操作数（混合拼接） | 保持操作数原样进 `string.Concat` 参数表：BCL `Concat(object?)` 隐式装箱后调虚 `ToString()`——char 印字符、enum 印名、值类型按重载实例 `ToString`。C# 侧不手写 cast/`ToString()`；**double/float 不发 `ToString()`**（避免中间树文化分歧） | 与 C23 IE-CONCAT 同形态（[C23 §2.2 值类型装箱/null 行](file:///d:/code/cs2j/docs/superpowers/specs/2026-09-29-c23-interpolation-expand-design.md)）。double `1.0→"1"` 与 bool `"True"` 的 Java 半区差异归 §5.2-J5（StringHelper.concat 内收口），C# 半区不偷换 |
| 6 | EC-4 `+=` 与单次求值 | `ICompoundAssignmentOperation.OperatorKind` 为 `Concatenate`：简单目标（局部/参数/无副作用接收者字段）→ `target = string.Concat(target, x)`；属性目标 → `target = string.Concat(target, x)` 形式的 C# 赋值（getter 一次/setter 一次，接收者为稳定 `this`/局部）；接收者含调用/条件访问/索引器目标 → 先提升 `__cs2j_ec_*` 临时（接收者临时 + 值临时），再 `tmpReceiver.Prop = string.Concat(tmpReceiver.Prop, tmpX)`；无法保证单次求值的目标 → CS2J0475 Error | 复刻 E03/E05「稳定引用直取 + 不稳定临时提升」纪律（[DelegateSyntaxRewriter.IsStableLeftValue :219-242](file:///d:/code/cs2j/src/CSharpToJava.Core/DelegatePreprocessor/DelegateSyntaxRewriter.cs#L219-L242)），禁止生成重复求值的属性展开 |
| 7 | EC-5 临时变量与副作用 | 折叠出的多操作数 Concat：默认**零临时**（参数表内每个表达式只出现一次，参数求值即源序）；仅当某操作数是「被本 pass 重写出的调用/含副作用且在产物中需出现多次」（仅 `+=` 展开场景）时经两遍地 pass2 提升到锚点语句前；语句锚点算法复用 E03 范式 | 纯 `GetA() + Next() + "x"` 直接 `Concat(GetA(), Next(), "x")`，不求值两次 |
| 8 | EC-6 委托 `==` | kind 为 `DelegateEquals`/`DelegateNotEquals` → `Cs2jPrelude.DelegateOps.Equals(a, b)` / `!…`。C# 支撑库新增（随 Cs2jPrelude 既有嵌入资源分发）：`public static bool Equals<T>(T? a, T? b) where T : Delegate => object.Equals(a, b);`（BCL `Delegate.Equals` 虚方法 = 调用列表逐项比较；双 null true）。操作数为 `IEventSymbol` 的 `evt == null` **跳过**（转换器有声明类内 isEmpty 专用路径：[BinaryExpressionTransformer.TryTransformEventNull :196-207](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/BinaryExpressionTransformer.cs#L196-L207)） | Java 多播代理的 `equals` 被覆写成同一性（[DelegateHelper.java:108](file:///d:/code/cs2j/java/csharptojava-compat/src/main/java/io/github/ningpp/compat/DelegateHelper.java#L108)），故**不能**走 object.Equals 的 Java 映射，必须显式独立方法；Java 半区见 §5.2-J4 |
| 9 | EC-7 record 合成 `==` | 谓词：绑定符号 `MethodKind == UserDefinedOperator` && `IsImplicitlyDeclared` && `ContainingType.IsRecord`（record class 与 record struct 均命中）→ `object.Equals(a, b)` / `!…`（record 覆写了值 `Equals`，虚分派即字段逐项值相等；双 null true）。record struct v1 接受装箱成本，M3 再评估强类型 `a.Equals(b)`+null 守卫形态 | 与 C14 的边界靠符号三元区分（决策 10）；record `Equals` 生成面 [RecordTransformer.cs:320-357](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Type/RecordTransformer.cs#L320-L357) 已就绪，Java 半区只补 object.Equals 静态映射（§5.2-J2） |
| 10 | **★分派边界：内置绑定 vs 用户运算符** | 四类各归其主：① **编译器内置绑定**（string/object/delegate 运算符 kind 为 `StringEquals/ObjectEquals/DelegateEquals`、拼接 `Concatenate`；即便符号呈现为 BCL 的 `MethodKind.BuiltinOperator`）→ 本 pass；② **源内用户运算符**（`UserDefinedOperator` flag 且 `HasSourceDeclaration`）→ C14（转换器现有路径 [IsSourceUserDefinedOperator :158-162](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Utilities/BinaryExpressionSupport.cs#L158-L162) 同口径）；③ **record 合成运算符**（UserDefinedOperator + IsImplicitlyDeclared + IsRecord）→ 本 pass EC-7；④ **元数据-only 用户运算符**（外部库声明，如 Unity 族重载 `==`）→ 不动（C14 决策 9 同口径），Info 计数 | 禁止把「操作数是引用类型」一律物化为 ReferenceEquals——用户可在任意类/结构体上重载 `==`，符号判定是唯一正确分界 |
| 11 | EC-8 提升相等与 C22 划界 | `OperatorKind` 带 `Nullable` flag（含 `bool?` 三值、`int?` 等一切 lifted `==/!=`）→ **本 pass 不重写**：strict 关闭时发 CS2J0474 Warning 计数，strict 模式 Error。结论：**提升算术与提升相等统一归 C22**（[C22 决策 5](file:///d:/code/cs2j/docs/superpowers/specs/2026-09-29-c22-nullable-value-design.md) 已含一般 lifted 相等公式并声明 bool? 特判对预处理路径成为不可达路径），本 pass 不重复实现 | 链序上本 pass 先于 numeric-boundary（§3.3）：C22 落地后 lifted 站点已展开为 `HasValue/Value` 守卫，内层基元 `==` 不命中本 pass 任何规则，残余 Warning 自然归零，届时闸门升级为 Error |
| 12 | EC-9 元组与 E06 划界 | 元组类型 `==`（E06 已在更早链位改写为 `.Equals`）本 pass **无规则**；residual 扫描只兜底一种边角：tuple 操作数与 `null` 字面量比较（E06 因另一侧非 tuple 不触发，生成 struct 又无 `op_==`）→ CS2J0476 Warning 并交由后续「元组 null 比较」小修（可在 E06 内修），不静默透传 | 字符串元组特例已由 E06 逐元素 EqualityComparer 覆盖，不另建规则 |
| 13 | EC-10 显式 `ReferenceEquals` 调用 | 源码显式 `object.ReferenceEquals(a,b)`/`ReferenceEquals(a,b)`（符号为 System.Object 静态方法）：C# 形态保持不动（语义已正确），其可编译性由 Java 半区新增映射 + compat 方法保证（§5.2-J3）；residual 扫描将其计入「已解释」类别 | 调用点规范化只做一件事：把非限定 `ReferenceEquals` 在必要时规范化为 `object.ReferenceEquals`（v1 可不做，映射两侧都收） |
| 14 | 表达式树 skip | 节点位于 `Expression<Lambda>` 内（祖先 lambda 经共享 [ExpressionTreeFacts.IsExpressionTreeLambda](file:///d:/code/cs2j/src/CSharpToJava.Core/Analysis/ExpressionTreeFacts.cs#L12-L19) 判定，IOperation 侧再以 operation 根类型交叉确认）→ 整节点跳过，CS2J0472 Info；force 不解除 | 与 C26/C45/C14/C08 共享同一禁区（§4 风险 8）；`Expression.Equal(stringA, stringB)` 必须保持树节点形态 |
| 15 | dynamic / 不可判定 | dynamic 绑定（`Dynamic` flag / `ConvertedType` 为 dynamic）→ CS2J0471 Error；GetOperation 返回 null 且语法节点非错误容忍模式 → CS2J0470 Error；错误类型符号（ErrorType）→ 跳过并依赖编译诊断，不自行补判 | 遵循「无法等价表达即显式报错」第一原则（§4 风险 12），不产注释占位 |
| 16 | 常量折叠与幂等 | 不做任何值折叠（含全字面量拼接/相等）；产物再次执行本 pass 零 diff（重写后节点为 InvocationExpression，语义谓词不再命中二元规则）；干净文件字节级复制（含 BOM/trivia 保持） | 与 C23 IE-FOLD 的常量折叠互不干扰：插值折叠归 C23，二元 `+` 字面量链由本 pass 统一成 Concat（两者产物同形，不产生分歧） |
| 17 | enum / Span / Type 等既有特判 | 不触碰：enum `==`（Java enum 同一性，正确）、Span/Type 值相等（转换器既有 [TryTransformSpanOrTypeEquality :183-194](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/BinaryExpressionTransformer.cs#L183-L194)）、TimeSpan 关系比较、模式 `is` 比较（[ControlFlowTransformer.cs:482](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/ControlFlowTransformer.cs#L482)） | kind/符号谓词保证不命中；新增测试锁定「不误伤」回归面 |
| 18 | 支撑库 | C# 半区复用既有 **Cs2jPrelude** 工程（仅新增一个 `DelegateOps` 静态类源码到嵌入资源，不新建工程）；string/object/record 物化全部只用 BCL（`string.Equals`/`object.ReferenceEquals`/`object.Equals`/`string.Concat`），不注入任何新 C# 库 | 适配器 descriptor 声明 `supportLibrary: Cs2jPrelude`（即使项目内只有委托相等、没有 E05 可识别的 `+/-`，也要保证释放，见 §5.1） |

## 3. 注册表与链位契约

### 3.1 pass 归属：推荐 (c) 新建独立 pass

| 候选 | 评估 | 结论 |
|---|---|---|
| (a) numeric-boundary 族（C15 锚点，Rules/） | 相等性与数值族相邻、C22 已在该族；但 C15 锚点本身尚未合入（C22 文档自述硬前置），本 pass 的 string/object/delegate/record/拼接均非数值域，P1 挂在未落地的 P2 族上等于自我阻塞 | 否 |
| (b) expression-desugar 族（C23 锚点） | Concat 物化确与 C23 同族、可共享未来 TempLifter；但 C23 同样「设计已定、待实施」，其 RunAfter 仅锚 null-coalesce，链位在 E05/E06 之前，与「必须晚于 delegate/tuple」冲突；且 C47 含相等/委托/record 三个非表达式糖子域 | 否（未来 CsCore 收敛时可评估合并，§8） |
| **(c) 新建独立 stub `equality-concat`** | **与 C31（index-range）完全同型：P1 静默错值、独立 Core 目录 + 薄适配器 + 独立码段 + 独立 residual 闸门；不依赖任何未合入锚点；RunAfter 显式锚 tuple，链位确定** | **推荐** |
| (d) C14/member-desugar 同族 | catalog 文字称「C14 调用侧互补规则」，但 C14 管源内用户运算符的**声明与调用绑定**，本 pass 管**编译器内置/合成绑定**，符号谓词不同族（决策 10）；member-desugar 尚未落地且为 Declarative 阶段 | 否 |

### 3.2 拟注册 stub 字段（实施期动作，本设计不落地）

在 [PassStubCatalog.Ids](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassStubCatalog.cs#L20-L46) 与 `Stubs` 表各加一条：

- id：`equality-concat`
- 名称：`Equality/concat call-site materialization`
- 中间目录：`.cs2j-equality-concat-src`
- Stage：`PassStage.Expression`
- 状态：`PassStatus.Stub` → 随本特性实现翻为 Implemented（同 C31 工序）
- defaultEnabled：注册时 `false`；§8 门槛满足后随实现 PR 翻 `true`
- RunAfter：`{ PassIds.Tuple }`（E06 隐含 E05/Enum/E03 全部在其之前）
- needsSemanticModel：true
- supportLibrary：`PassSupportLibrary.Cs2jPrelude`（复用既有枚举，只新增 DelegateOps 源文件）
- catalogItems：`"C47"`
- 翻转清单（实施 PR 内一次完成）：① PassStubCatalog 新增 Ids 常量 + Stubs 条目；② PassCatalog 在实现期把该 stub 替换为实例描述符（参照 C31/Tuple 适配器外形）；③ `--list-passes` 与链序测试（[PassChainOrderTests.cs](file:///d:/code/cs2j/tests/CSharpToJava.Tests/Preprocessing/PassChainOrderTests.cs)、PassStubCatalogTests）同步；④ 旧开关风格 `--no-equality-concat`/`--enable-equality-concat` 按 PassPlanResolver 既有通用映射保留。**在 PassStubCatalog 加注册是实施期动作，本设计分支不预占**

### 3.3 与 E03/C23/C22/C14 的顺序契约

- **晚于 E03**（null-coalesce，默认开）：`??` 先成三元，本 pass 只看到普通二元；
- **晚于 E05 delegate**（默认开）：委托 `+/-` 已为 `CsPrelude.Combine/Remove` 调用，本 pass 不会把委托 Add 当拼接（IOperation kind 上委托 Add 本就不是 Concatenate，双保险）；
- **晚于 E06 tuple**（默认开）：元组 `==` 已成 `.Equals`，本 pass 无元组规则，只兜底边角（决策 12）；
- **先于/独立于 C22（numeric-boundary，未落地）**：带 Nullable flag 的站点本 pass 只发诊断不重写（决策 11），两 pass 无规则重叠；C22 合入后其守卫内层是基元比较，不回流本 pass；
- **与 C23（expression-desugar，未落地）无先后依赖**：C23 消费 `InterpolatedStringExpression`，本 pass 只消费 `BinaryExpressionSyntax`/`AssignmentExpressionSyntax`；两者的 Concat 产物同形并共用 §5.2-J5 的 Java 半区收口；若同树共存，自底向上访问保证插值洞先展开、外层 `+` 再折叠；
- **与 C14（member-desugar，未落地）无先后依赖**：边界由符号谓词保证（决策 10），不以链序切分；C14 落地后，源内运算符站点在其 pass 内已成显式方法调用，本 pass 天然不命中。

## 4. Core 组件设计

目录 `src/CSharpToJava.Core/EqualityConcat/`（标准六件套 + 规则文件，外形对齐 E03/C31）：

| 文件 | 职责 |
|---|---|
| `EqualityConcatOptions.cs` | `Strict`（默认 true：CS2J0474/0475/0476 升 Error）、`SkipExpressionTrees`（恒 true，保留开关只为诊断分类）、`EnableDelegateRule`（默认 true；false 时委托站点 CS2J0473 Warning，用于 Java 半区未合入时的显式降级） |
| `EqualityConcatStatistics.cs` | 计数：`StringEqualsRewritten`、`ObjectReferenceRewritten`、`ConcatChainsFolded`（操作数总数/链数分计）、`ConcatCompoundRewritten`、`DelegateEqualsRewritten`、`RecordEqualsRewritten`、`TemporariesLifted`、`ExpressionTreeSitesSkipped`、`LiftedEqualityPendingC22`、`TupleNullEdgeSites`、`ResidualSites` |
| `EqualityConcatDiagnostics.cs` | 码段见 §6 |
| `EqualityConcatResult.cs` | `Changed`/Root/Statistics/Diagnostics（`IPassStatistics` 实现，供 runner 摘要） |
| `EqualityConcatPreprocessor.cs` | 入口：dirty 检查 → 整树/逐文件获取 SemanticModel → Analyze 一遍 → 两遍地 Rewrite（pass1 自底向上改写并登记 `PendingTemp`，pass2 语句级提升）→ Formatter 仅作用于变更节点 → 可编译性校验 → residual 闸门 |
| `SemanticKindClassifier.cs` | 唯一分类器（§4.1）；全部规则共享，规则文件不得自行猜类型 |
| `Rules/BinaryEqualityRewriter.cs` | EC-1/EC-2/EC-6/EC-7（`==/!=` 分派） |
| `Rules/StringConcatRewriter.cs` | EC-3/EC-4（注意与 Java 侧 [Java/Rewriters/StringConcatRewriter.cs](file:///d:/code/cs2j/src/CSharpToJava.Core/Java/Rewriters/StringConcatRewriter.cs) 同名不同命名空间，Core 文件 XML 注释中显式交叉引用防混淆） |
| `Rules/DelegateEqualRewriter.cs` | EC-6 的符号渲染与泛型实参推断（委托类型实参可省略，发 `Cs2jPrelude.DelegateOps.Equals(...)` 非限定全写形式） |
| `TempLifter.cs` | `__cs2j_ec_*` 临时登记与锚点语句插入（对齐 E03 两遍范式；C23 族 TempLifter 落地后可评估合并） |

### 4.1 SemanticKindClassifier 与 IBinaryOperation 探针计划（M0）

分类输出枚举：`StringEquality`、`ObjectIdentity`、`DelegateEquality`、`RecordValueEquality`、`StringConcat`、`StringConcatCompound`、`LiftedNullableEquality`（交 C22）、`TupleEqualityResidual`、`EventNullCheck`（跳过）、`UserDefinedOperator`（交 C14/转换器）、`PredefinedOther`（基元/枚举等，不动）、`ExpressionTree`、`Dynamic`、`Unknown`。

判定流水线（对每个 `BinaryExpressionSyntax`/`AssignmentExpressionSyntax`）：

1. 祖先链命中 `ExpressionTreeFacts.IsExpressionTreeLambda` → `ExpressionTree`（短路）；
2. `var op = model.GetOperation(node)`；`IBinaryOperation b` → 取 `b.OperatorKind`，拆 kind 掩码与 flag（`UserDefined`/`Nullable`/`Dynamic`/`Checked`）；复合赋值取 `ICompoundAssignmentOperation.OperatorKind`；
3. `Dynamic` flag → `Dynamic`；`Nullable` flag 且为相等 → `LiftedNullableEquality`；
4. 裸 kind 直接映射：`StringEquals/StringNotEquals`→StringEquality；`ObjectEquals/ObjectNotEquals`→ObjectIdentity；`DelegateEquals/DelegateNotEquals`→DelegateEquality（再排除 IEventSymbol 操作数→EventNullCheck）；`Concatenate`（Add/复合赋值）→StringConcat(_Compound)；
5. `UserDefined` flag：查 `GetSymbolInfo(node).Symbol`——`IsImplicitlyDeclared && ContainingType.IsRecord` → RecordValueEquality；其余（源内/元数据用户运算符）→ UserDefinedOperator；
6. 操作数类型为元组（`ITypeSymbol.IsTupleType`）且语法仍为 `==/!=` → TupleEqualityResidual（正常情况 E06 已改写，不应出现）；
7. operation 缺失：回退 `GetSymbolInfo`+`ConvertedType` 保守判定（string 两侧且非 object 逃逸 → StringEquality；其余 Unknown→CS2J0470），回退路径计数并在摘要中可见。

**M0 API 探针（写最小 xUnit/控制台探针，禁止凭记忆下结论）**，在 Roslyn **4.12.0** 上对下列 fixture 打印 `OperatorKind`（裸名+flag）、`GetSymbolInfo().Symbol?.MethodKind`、`IsImplicitlyDeclared`、`ContainingType.IsRecord`：

`string a,b; a==b` / `a!=b` / `a==null` / `(object)a==b` / `object o=a; o=="x"` / `a+"x"` / `"x"+1` / `null+"a"` / `a+=b` / `Action d; d==d` / `(d+d)==(d+d)` / `bool? x; x==true` / `int? i; i==i` / `record R(int X); var r1=…; r1==r2`（class 与 struct 两版）/ 用户 struct 重载 `==` / 元组 `(1,"a")==(1,"a")`（E06 前后各取一次）。探针输出作为 M1 实现的钉死基线提交到测试资源（不进生产代码）。

### 4.2 临时变量与单次求值

- 临时命名统一 `__cs2j_ec_{n}`（全仓 `__cs2j` 前缀惯例）；
- 只有两类提升：① `+=` 不稳定接收者/属性目标（决策 6）；② 锚点为表达式树外但目标是 `out`/ref 上下文等无法内联的位置（v1 报 CS2J0475，不硬做）；
- 纯 Concat 折叠零临时；每个实参在产物中出现一次即天然单次求值；
- pass2 提升复用 E03 的语句锚点查找（if/while/for 内嵌表达式提升到外层块），不新造锚点语义。

### 4.3 residual 闸门（语义判定）

输出树用户代码范围（Cs2jPrelude/Cs2jTuples 等支撑工程除外）内，Classifier 分类为以下类别的节点数必须为 0 或有对应显式诊断：`StringEquality`/`ObjectIdentity`/`DelegateEquality`/`RecordValueEquality`/`StringConcat(_Compound)` 零残留；`LiftedNullableEquality`/`TupleEqualityResidual` 允许但每个节点一条 Warning（strict=Error）；`ExpressionTree` 每个节点一条 Info。闸门统计来自 Analyzer 全量重跑，不做文本 grep。

## 5. CLI 适配器与集成

### 5.1 适配器（无新管线机制）

- 新增 `src/CSharpToJava.CLI/ProjectEqualityConcatPreprocessor.cs`（request/result 外形对齐既有 15 个包装类）与 `Preprocessing/Adapters/EqualityConcatPassAdapter.cs`（descriptor + 调 Core 入口 + 统计/诊断映射）；
- 支撑库：descriptor 声明 `Cs2jPrelude`；适配器复用现有 Cs2jPrelude 嵌入资源释放 + 主转换过滤机制（[Program.cs:750-756](file:///d:/code/cs2j/src/CSharpToJava.CLI/Program.cs#L750-L756) 同族惯例），资源内容追加 `DelegateOps.cs`（见 §5.2-C1）；释放判定不能只依赖 E05 是否改写了 `+/-`（项目可能只有委托 `==`）；
- 指纹：中间目录接入既有 fingerprint/preserve（§4 风险 11），支撑库文件不进指纹快照；
- `verifyBuildByDefault`/`verifyTestsByDefault` = true（默认开启门槛依赖）；
- 主转换器、Java rewriters 本期**零改动**；[BinaryExpressionTransformer.cs](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/BinaryExpressionTransformer.cs) 的 string/bool?/Span 特判簇保留为单文件直连与回滚安全网，默认开启一个发布周期后另立删除 PR（§8）。

### 5.2 Java 半区依赖清单（独立 PR 边界；C47 默认开门的子门）

| # | 缺口 | 落点 | 内容 |
|---|---|---|---|
| J1 | `string.Equals(a,b)` 静态两参现映射为无点 `equals`，静态调用渲染成 `String.equals(a,b)` 不可编译（[InvocationExpressionTransformer.cs:539-550](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/InvocationExpressionTransformer.cs#L539-L550)；现有无 arity 条目 [TypeMappings.json:6359-6263](file:///d:/code/cs2j/config/TypeMappings.json#L6259-L6263)） | TypeMappings（配置） | 为 `System.String/Equals` 增加 `signature: "2"` 条目，javaMethod 指向带点静态助手（`Objects.equals`，import java.util.Objects）；实例 1 参 Equals 继续走现有无 signature 条目。MapMethod 已支持按参数数消歧（[TypeMappingRegistry.cs:441-476](file:///d:/code/cs2j/src/CSharpToJava.Core/TypeMapping/TypeMappingRegistry.cs#L441-L476)），实现前探针确认带点静态名的 import 注入路径对该法有效 |
| J2 | `object.Equals(a,b)` 无映射 | TypeMappings（配置） | 新增 `System.Object/Equals`（signature 2）→ `Objects.equals`；record 物化与未来其它值相等场景共用 |
| J3 | `object.ReferenceEquals(a,b)` 无映射 | TypeMappings + compat | 新增 `System.Object/ReferenceEquals`（signature 2）→ `ObjectHelper.referenceEquals`；compat [ObjectHelper.java](file:///d:/code/cs2j/java/csharptojava-compat/src/main/java/io/github/ningpp/compat/ObjectHelper.java) 增 `public static boolean referenceEquals(Object a, Object b){ return a == b; }` |
| J4 | 委托调用列表值相等无公共 Java 面 | compat + TypeMappings + C# 支撑源 | ① compat：把 [DelegateHelper.java:72-74](file:///d:/code/cs2j/java/csharptojava-compat/src/main/java/io/github/ningpp/compat/DelegateHelper.java#L72-L74) 私有 `delegateEquals` 提升为 public（建议命名 `delegateEquals` 避开静态 `equals` 歧义），实现须展开 `MulticastInvocationHandler.delegates` 做列表等长、逐项「方法相同 + 接收者相等」比较，并修正多播代理 [:108](file:///d:/code/cs2j/java/csharptojava-compat/src/main/java/io/github/ningpp/compat/DelegateHelper.java#L108) 对 Object.equals 的覆写（或由本方法旁路之）；② TypeMappings：`Cs2jPrelude.DelegateOps` → `DelegateHelper`（与 [CsPrelude 条目 :349-354](file:///d:/code/cs2j/config/TypeMappings.json#L349-L354) 同形）；③ C# 嵌入源 Cs2jPrelude 增 `DelegateOps.Equals<T>`（决策 8） |
| J5 | StringHelper.concat 的值类型文本差 | compat | [appendConcatValue :1314-1345](file:///d:/code/cs2j/java/csharptojava-compat/src/main/java/io/github/ningpp/compat/StringHelper.java#L1314-L1345) 对 Double/Float 须走现有 `DoubleHelper.toString`（C# `1.0→"1"`，现 `builder.append` 得 `"1.0"`）；Boolean 须印 `True/False`（C# 文本）；char/enum 现状已等价。本项与 C23 共用，C47 只是新增大量调用方 |
| J6 | J1/J2 渲染形态探针 | 转换器（只读探针，不改逻辑） | M0 顺带验证：带点映射静态调用的 import 是否自动加入；不足时在 Java 半区 PR 补 import 规则（不进 C47 C# pass） |

J1–J4 是 C47 默认开启的**硬子门**（产物可编译且运行时正确），J5 可与 C23 默认开门联合验收。

## 6. 诊断策略

| 码 | 级别 | 触发 | 处置 |
|---|---|---|---|
| CS2J0470 | Error | IOperation/符号均不可判定且回退分类失败 | 节点不改写，本次转换失败；禁止裸 `==`/`+` 透传 |
| CS2J0471 | Error | dynamic 绑定的相等/拼接站点 | 同 C22/C14 dynamic 立场；force 不解除 |
| CS2J0472 | Info | 表达式树内跳过节点（逐节点计数） | 保持树形态；摘要可见 |
| CS2J0473 | Warning（`EnableDelegateRule=false`）/Error（rule 开但支撑源释放失败） | 委托相等物化被显式关闭或支撑库缺失 | 用于 Java 半区 J4 未合入时的受控降级，绝不静默直映 |
| CS2J0474 | Warning（strict=Error） | `T?`/`bool?` lifted 相等站点（C22 未落地期） | C22 合入后应归零；闸门随之升级为硬 Error |
| CS2J0475 | Error | `+=` 目标无法保证单次求值（复杂索引器/事件外的特殊目标） | 不生成重复求值半成品；M2 后借 C17 RMW 接缝扩展 |
| CS2J0476 | Warning | 元组与 null 字面量比较残留（E06 边角） | 交 E06 小修；不静默透传 |

总则：禁止注释/占位/原样透传流入 Java 产物（§4 风险 12 第一原则）；每个未改写站点都有一条诊断与统计计数。

## 7. 测试计划

### 7.1 catalog 验证行必做 fixture（运行时对拍，非文本断言）

全部用例同时进 .NET xUnit 项目与 Java JUnit 项目，断言**运行时值**（双半区行为预言机，本类正确性只能靠运行时对拍，§5.1 方法论）：

1. `new string("a".ToCharArray()) == "a"` → true；`!=` → false；再取 `Substring`/`Concat` 构造等值串；**严禁只用字面量**（驻留侥幸，§10-R1）；
2. `null + "a"`、`"a" + null`、`(string)null + (string)null`、`null + 'c'`、`1 + (string)null` → `"a"`/`"a"`/`""`/`"c"`/`"1"`；Java 侧钉死不出现 `"null"`；
3. `(object)s == t` 对构造等值串 → false；`(object)s == s` → true；`object o = s; o == "a"` 矩阵；显式 `object.ReferenceEquals` 同测；
4. 多段 `+=`：局部串 4 段链（含 null 段）、属性 `+=`（getter/setter 各一次计数）、`Get().P += "x"` 接收者单次计数；
5. `bool?` 三值矩阵（null/true/false × `== true`、`!= false`、双侧 `bool?`）——回归现状转换器行为 + 钉死本 pass「不重写、发 CS2J0474」边界；
6. `(a,b)==("x","y")`、`(1,(object)"a")==(1,(object)"a")` 字符串元组特例（E06 路径回归，确认本 pass 不误伤）；
7. 委托：`(a+a)==(a+a)` → true；同方法不同接收者 `o1.M==o2.M` → false；`o1.M==o1.M` → true；`null==d`/`d==null` 矩阵；多播不同顺序 → false；
8. record：同值两实例 `r1 == r2` → true（record class、record struct、含字符串/数组字段各一）；`!=`；null 操作数；
9. 跨类混合：string 与自定义重载 `==` 类型同文件（验证 C14 边界互不误伤）、enum `==`、Span/Type 既有特判回归。

### 7.2 机制测试（Core 单测）

- 副作用单次求值：`GetStr() + NextInt() + GetObj()` 产物中三调用各出现一次且源序不变；`+=` 不稳定目标临时数与提升位置；
- 折叠规则：左链/右链/三元分支内拼接/与插值混排（C23 未落地时用手写 `string.Concat` 模拟）；双层嵌套 Concat 不跨分支展平；
- 幂等：产物二次执行零 diff；无变更文件字节级复制（BOM/trivia）；
- Classifier 探针表：§4.1 全枚举分类单测（M0 探针输出即测试基线）；
- 表达式树：`Expression<Func<string,string,bool>> p = (a,b)=>a==b;` 跳过 + CS2J0472；
- 码段：CS2J0470/0471/0474/0475/0476 各自构造；
- 统计字段与摘要行快照。

### 7.3 项目级/链级测试

- 新建 fixture sln（形态参考 tests/SampleTupleSolution）：含委托相等项目（触发 Cs2jPrelude 注入，且项目内**无** `+/-` 委托运算，验证适配器释放判定）、record 库、字符串工具库；
- 中间树 `dotnet build -c Release` + `dotnet test -c Release` 全绿（.NET 半区预言机）；
- Java 半区：compat `DelegateHelperTest` 增调用列表用例；StringHelper.concat 增 null/double/bool 用例；转换后 JUnit 对拍 §7.1 全表（J1–J5 合入后）；
- residual 闸门测试（语义模型驱动，对齐 C22/C31 闸门写法）；
- CLI：`--list-passes` 含新条目与正确拓扑位（tuple 之后）；`--no-equality-concat`/`--enable-pass equality-concat`；默认关阶段启用 stub 仍 CS2J9001（注册未实现期）；
- 基线不退化：现有钉死用例 [StringTests.cs:185-198](file:///d:/code/cs2j/tests/CSharpToJava.ConversionTests/Categories/StringTests.cs#L185-L198)（转换器直连路径）保持绿色——本 pass 默认关/开启都不影响单文件转换路径；pass 开启后其行为由中间树产物经同一路径得到相同 `Objects.equals`/Concat 路由结果。

## 8. 默认开启验收门槛

本 pass 为 P1（B 档结论），按 C31 范式「实现即注册为 Implemented、defaultEnabled 随门槛翻转」：

1. §7.2 Core 单测 + §7.3 项目级 fixture 全绿；中间树 Release build/test 全绿；
2. Java 半区 **J1–J4 齐备且 JUnit 对拍通过**（硬子门；J4 委托列表语义是正确性关键）；J5 可与 C23 联合验收但须在默认开前有结论；
3. residual 闸门在基线样例 sln 上：C 档五类零残留、CS2J0474 仅出现在确有 `T?` 相等的站点且数量与扫描清单一致；
4. csharp-sdk/既定基线项目指纹不退化（新中间目录接入指纹机制）；
5. 转换器特判簇（[BinaryExpressionTransformer.cs:96-116](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/BinaryExpressionTransformer.cs#L96-L116) 等）**本期不删**；默认开一个发布周期 + 回归无回滚后，另立 PR 收敛（CsCore 最小语法面方向，catalog §5.2），收敛 PR 需同步修订 [StringTests.cs:194-198](file:///d:/code/cs2j/tests/CSharpToJava.ConversionTests/Categories/StringTests.cs#L194-L198) 等直连断言。

（A 档假设下的观察项门槛——本结论已排除——仅作记录：若核实为全 A，文档主体应为 CsCore 收敛计划，本 pass 不注册。）

## 9. 里程碑

- **M0 核实固化与探针**：合入 §4.1 IOperation/符号探针（Roslyn 4.12.0 枚举实名、flag 位、record/元组/E06 前后形态、复合赋值 kind、J1/J2/J6 映射渲染探针）；产出分类基线表；不改生产代码；
- **M1 EC-1/EC-2（string ==/!= 与 object 同一性）**：BinaryEqualityRewriter 两规则 + 诊断骨架 + residual 闸门 v1 + Core 单测；Java 半区 J1/J2/J3 可并行启动；
- **M2 EC-3/EC-4（Concat 折叠与 +=）**：StringConcatRewriter + TempLifter + 副作用单次求值全套测试；Java 半区 J5 联调；fixture 2/4；
- **M3 EC-6/EC-7（委托/record）+ 边界 residual**：DelegateEqualRewriter、record 合成运算符判定、CS2J0474/0476 策略、表达式树 skip；Java 半区 J4 齐备为子门；fixture 5-9；适配器接线 + Release 双半区对拍 + 默认开门评审。

## 10. 风险

- **R1 驻留串侥幸（最高危测试陷阱）**：`"a"=="a"`、甚至 `("a"+"b")=="ab"` 都可能被编译期驻留/常量折叠成全 true，发现不了②类错值；①的现有测试正是字面量形态。所有相等 fixture 必须含运行时构造串（`new string(char[])`、`Substring`、`StringBuilder.ToString()`、`Concat` 产物、Span 转串），双半区对拍；
- **R2 null 语义双向差异**：拼接 null→空串（J5 之外还要防 C# 侧误加 `?? ""` 改变 Concat 重载选择与装箱）；相等 null→string.Equals 两参形态，禁止实例 Equals；委托/record null 矩阵；
- **R3 重载符号误判**：「一侧是 string」不等于绑定 string 运算符（②的根因）；外部库（Unity 族）重载 `==`、用户隐式转换到 string、接口约束类型比较——一律以 IOperation kind + 绑定符号为准；回退路径（无 SemanticModel）宁报错不乱判；
- **R4 表达式树/dynamic**：树内改写改变节点形态致 LINQ provider 行为变化（与 C26/C45/C14 共享禁区）；dynamic 无编译期绑定；force 边界与各 pass 对齐；
- **R5 Concat 折叠的过度展平**：跨控制流/条件表达式边界展平会改变短路与求值顺序；折叠只在纯 Add 左链子树内；右结合保持嵌套；
- **R6 Java 半区不同步**：J1–J5 未合入时默认开会产出不可编译/错值 Java；默认开严格门控 + `EnableDelegateRule` 受控降级（CS2J0473），不提前翻转；
- **R7 多播代理既有行为依赖**：DelegateHelper 代理 `equals` 现为同一性（[DelegateHelper.java:108](file:///d:/code/cs2j/java/csharptojava-compat/src/main/java/io/github/ningpp/compat/DelegateHelper.java#L108)），J4 改动可能影响存量依赖该行为的用户代码——Java 半区 PR 需全量 compat 回归；
- **R8 bool 文本文化**：J5 的 `True/False` 与 C23 format 路径文化语义联动，收口时以 BCL 英文关键字布尔文本为准（与 `csFormat` 现状核对）。

## 11. 并行协调

| 通道 | 协调点 |
|---|---|
| **C14**（member-desugar） | 分派边界唯一真相 = 符号三元：内置绑定（本 pass）/源内用户运算符（C14，`HasSourceDeclaration`）/record 合成运算符（本 pass，IsImplicitlyDeclared+IsRecord）/元数据运算符（都不动）。两 pass 设计评审互签该谓词；C14 落地后共用一份分类器常量（可提到 Analysis/）防漂移 |
| **C22**（numeric-boundary） | lifted `==/!=` 全归 C22；本 pass 只发 CS2J0474 residual。两 pass 合入顺序无关，C22 落地后本 pass 闸门把 0474 升 Error；bool? 转换器特判在 C22 落地后成为不可达路径，删除归 C22 PR |
| **C23**（expression-desugar） | 共用 Concat 目标形态与 J5 Java 半区收口；语法面不重叠（InterpolatedString vs Binary）；C23 的 IE-CONCAT 白名单（IFormattable/无符号洞）结论本 pass 直接复用（`uint + ""` 同 unsigned 跨 pass 契约：本 pass 链位在 unsigned 之后，无符号洞已为真身类型） |
| **C26/C45**（LINQ/闭包提升） | 表达式树 skip 共用 [ExpressionTreeFacts](file:///d:/code/cs2j/src/CSharpToJava.Core/Analysis/ExpressionTreeFacts.cs)；C45 display class 提升后捕获变量上的 `==`/`+` 以提升后字段访问形态到达本 pass，分类器只看符号不看语法，天然兼容；加组合 fixture |
| **E06 元组 / E11 record-readonly / C27 record-with** | 元组相等 E06 已闭环（本 pass 仅 CS2J0476 边角）；record 调用点归本 pass、record 声明形态归 E11/C27；RecordReadOnly 整树回滚若在本 pass 之后重跑，需确认 record 识别对其产物（class+init 属性形态）仍有效——M3 fixture 覆盖「先 readonly 化后相等物化」链序（链序上 record-readonly 为 opt-in 且在 array-method 前后，本 pass 在 tuple 之后，实际先后以开关组合测试确认） |
| **E05 delegate** | 委托 `+/-` 已由 E05 物化为 Combine；本 pass 复用 Cs2jPrelude 工程并追加 DelegateOps；两 pass 的支撑库释放逻辑去重（descriptor 各声明，runner 幂等释放）；委托事件 `evt == null` 归转换器 isEmpty 路径，本 pass 排除 |
| **热文件** | 本 pass **不修改** [BinaryExpressionTransformer.cs](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/BinaryExpressionTransformer.cs)、[BinaryExpressionSupport.cs](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Utilities/BinaryExpressionSupport.cs)、[StringConcatRewriter.cs](file:///d:/code/cs2j/src/CSharpToJava.Core/Java/Rewriters/StringConcatRewriter.cs)（本期）；触碰面集中在新目录 + CLI 两个新文件 + PassStubCatalog/PassCatalog 注册行；TypeMappings.json 与 ObjectHelper.java/DelegateHelper.java/StringHelper.java 的修改全部在 Java 半区 PR，分支协调点明确 |

## 12. Worktree 分配（只写不执行）

实施分支（本设计不创建，以下为实施期指令原样登记）：

- 分支：`feature/preproc-c47-equality-concat`
- 目录：`.worktrees/preproc-c47-equality-concat`
- base：`9b99c60b`（已核实存在）
- 命令（实施期手工执行，本设计任务未执行任何 git 写操作）：

```bash
git worktree add .worktrees/preproc-c47-equality-concat -b feature/preproc-c47-equality-concat 9b99c60b
```

分支内建议提交序（仅规划）：M0 探针与分类基线 → Core 规则与单测（M1→M2→M3 各一提交）→ CsPrelude.DelegateOps 源与适配器（不含注册翻转）→ PassStubCatalog 注册 + 链序测试 → Java 半区 J1–J5 作为独立 PR/分支先行或并行，默认开启翻转以 J1–J4 合入为前提。
