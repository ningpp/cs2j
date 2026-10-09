# C27 record `with` / init / required 补充去糖（member-desugar Records 规则）设计

**日期**: 2026-09-29
**校订（2026-09-29）**: 按当前磁盘证据逐条核对、手术式修订，改动枚举：① catalog 行号全部重算——C27 条目实为 [L637-L648](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L637-L648)、§3.3「成员去糖」族行 [L1082](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L1082-L1082)、横切约束 [L1072-L1074](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L1072-L1074)、风险 7-12 为 [L1105-L1110](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L1105-L1110)；② **member-desugar stub 已由 C11 翻转，C11-C14 均已落地**（[PassCatalog.cs L230-L241](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassCatalog.cs#L230-L241) Implemented、`catalogItems="C11"`；[PassStubCatalog.cs](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassStubCatalog.cs) 现 21 个 stub、已无 member-desugar 条目；`MemberDesugar/` 下 Properties/Indexers/Events/Operators 四规则目录在盘），注册表叙述按真值改写，C27 开工不再有「等锚点分支合入」前置；③ member-desugar 对 with/init/required 覆盖实判：**with 未覆盖**（[PropertyRule.cs L287-L293](file:///d:/code/cs2j/src/CSharpToJava.Core/MemberDesugar/Properties/PropertyRule.cs#L287-L293) 对 with 内属性赋值显式判 CS2J5124 跳过）、**init 已覆盖（属性面）**（[PropertyDeclarationRewriter.cs L110-L123](file:///d:/code/cs2j/src/CSharpToJava.Core/MemberDesugar/Properties/PropertyDeclarationRewriter.cs#L110-L123)，CS2J5120）、**required 部分覆盖**（仅属性面，CS2J5121；字段 required 与 `[SetsRequiredMembers]` 全 src 零处理）、位置 record 属性 CS2J5123 显式跳过（[PropertyRule.cs L64-L69/L257-L259](file:///d:/code/cs2j/src/CSharpToJava.Core/MemberDesugar/Properties/PropertyRule.cs#L64-L69)）；④ `MemberDesugarKind` 枚举实位 [MemberDesugarPlan.cs L6-L13](file:///d:/code/cs2j/src/CSharpToJava.Core/MemberDesugar/MemberDesugarPlan.cs#L6-L13)（非 MemberDesugarRules.cs），规则发现机制已定为程序集扫描 `[MemberDesugarRule]`（[MemberDesugarRules.cs L10-L35](file:///d:/code/cs2j/src/CSharpToJava.Core/MemberDesugar/MemberDesugarRules.cs#L10-L35)）；⑤ E11 链位行号（[PassCatalog.cs L179-L187](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassCatalog.cs#L179-L187)）、E11 单测 with 断言数（实测 45 行含 `with {`、其中 42 行为断言，非「约 28 处」）等行号/数字订正；⑥ tests/ 现无任何 Record*With*/Init*/Required* 专项测试文件（以当前磁盘为准，本任务测试均为新增）；src 与 java/csharptojava-compat grep `RequiredMember`/`IsExternalInit`/`SetsRequiredMembers` **零命中**（compat 无对应支撑，本特性也不需要支撑库）；⑦ 删除仓内不存在的本地 `file:///d:/code/cs2j/dotnet/.../record` 链接，换官方 URL；⑧ 官方语义复核：CS8858/CS8859/CS8868/CS8878 码号无误；复制 ctor 仅在 record 存在 required 成员时带 `[SetsRequiredMembers]`（决策 7 措辞收紧）；位置 record 参数不可加 `required`（官方 required 关键字文档确认）。本次仅改本文档，未碰任何代码与测试。
**范围**: 在已落地的 `member-desugar` 族 pass 下新增 **Records 规则集**（Core `MemberDesugar/Records/`）：把 `with` 表达式改写为「克隆工厂/值拷贝 + 成员赋值」的可编译 C#，补齐 record 位置参数成员的去糖、record 面上 init/required 收尾；本任务**不新增 pass、不注册/翻转 stub、不改 RunAfterIds、不改 C#→Java 转换器代码**
**状态**: 设计待评审；原硬前置 C11 锚点**已合入主干**（C11-C14 已 Implemented、默认关），C27 直接在已落地的 `MemberDesugar/` 骨架上加 `Records/`（分支 base 见 §12）
**Catalog**: C27（P3，分类 1.4/1.2）[preprocessor-catalog.md L637-L648](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L637-L648)；族归属「成员去糖」（[§3.3 L1082](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L1082-L1082)）；横切约束（改名通道必须基于全编译 SemanticModel，[L1072-L1074](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L1072-L1074)）、风险 7 反射/字符串契约（[L1105](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L1105-L1105)）、风险 8 表达式树（[L1106](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L1106-L1106)）、风险 9 Lowering 非资产（[L1107](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L1107-L1107)）、风险 12 禁止静默错误输出（[L1110](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L1110-L1110)）
**注册表现状（2026-09-29 核实）**: `member-desugar` **已从 stub 翻转为 Implemented**——[PassCatalog.cs L230-L241](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassCatalog.cs#L230-L241)，Stage=`Declarative`，RunAfter=`{struct-value-semantics, closure-lift}`（[L237](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassCatalog.cs#L237-L237)，二者仍是 stub），目录 `.cs2j-member-desugar-src`，**`catalogItems="C11"`（[L241](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassCatalog.cs#L241-L241)）**，`PassIds.MemberDesugar` 常量在 [PassCatalog.cs L25](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassCatalog.cs#L25-L25)，默认关；[PassStubCatalog.cs](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassStubCatalog.cs) 现存 **21 个** stub，已无 member-desugar 条目（翻转先例 iterator-lowering：id 常量留 [PassStubCatalog.Ids L32](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassStubCatalog.cs#L32-L32)、在 PassCatalog 注册为 Implemented；member-desugar 因翻转时无其它 RunAfter 引用，其 Ids 常量已按 C11 翻转清单删除）。**C27 无需改注册表结构**：合入时仅把 L241 字符串按 C11 §3.3-10 约定追加为 `"C11, C27"`（一行），C27 只在族 pass 内追加 `Records/` 规则

---

## 1. 背景与目标

### 1.1 `with` 今天由转换器表达式层单文件处理，语义面不成立

Java 没有非破坏式变异语法。当前 `with` 表达式的全部处理压在主转换器单文件路径上，只有一个入口：

- 分派：[ControlFlowTransformer.cs:54](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/ControlFlowTransformer.cs#L54-L54)；实现 [TransformWithExpression L574-L638](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/ControlFlowTransformer.cs#L574-L638)。
- **按名字符串启发式**收集覆写：`Dictionary<string,string>(OrdinalIgnoreCase)`（[L580](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/ControlFlowTransformer.cs#L580-L580)），不绑定符号、不判字段/属性、不校验可访问性。
- Java record 路径（[L599-L625](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/ControlFlowTransformer.cs#L599-L625)）：枚举目标类型全部成员、挑「只读属性且构造器有同名参数」者重建构造调用。它对**额外成员 record、非位置属性、嵌套初始化器、继承多态**全部不成立——派生 record 经基类静态类型 `with` 时产出基类构造调用，运行时类型直接改变。
- 非 Java record 路径（[L628-L637](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/ControlFlowTransformer.cs#L628-L637)）发 `sourceExpr.clone()` + setter 前置语句，`.clone()` 是否存在、是否公开、是否复制全部状态均无保证。
- **已被测试锁定为错误输出**：[RecordTupleTests.cs:26-31](file:///d:/code/cs2j/tests/CSharpToJava.ConversionTests/Categories/RecordTupleTests.cs#L26-L31) 中 `r with { X = 2 }` 断言产物为 `return new R();`——覆写值 `2` 与源对象 `r` **双双丢失**，只断言不含 `with` 字样。这是 catalog 风险 12 所称「不报错、却产出错误 Java」的静默错误面。

C# 语义对照（设计依据，[Microsoft Learn: with expression](https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/operators/with-expression)）：

| 接收者 | 克隆机制 | 继承/多态 |
|---|---|---|
| record class | 编译器合成 **protected 复制构造函数** + **虚克隆方法 `<Clone>$`**；`with` = 调虚克隆再施加初始化器；用户可自定义复制 ctor 接管 | 派生 record 各覆写 `<Clone>$` 调自己的复制 ctor，复制 ctor 链向基类复制 ctor；**结果运行时类型与接收者相同** |
| record struct / struct / 元组 | **内存按值拷贝**（同传值语义），无虚方法、不可定制、struct 隐式密封 | 无继承 |
| anonymous type | 编译器合成克隆（引用类型特殊形态） | — |
| 非 record 普通 class | **编译错误 CS8858**（合法输入中不存在） | — |

另有两个语言约束直接影响命名设计：record 中禁止声明名为 `Clone` 的成员（**CS8859**）；非密封 record 的复制 ctor 必须 public/protected（CS8878），派生复制 ctor 必须链基类复制 ctor（CS8868）。

### 1.2 init / required 在转换器侧没有系统处理

- init（转换器侧）：[PropertyTransformer.cs:21](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Member/PropertyTransformer.cs#L21-L21) 把 `InitAccessorDeclaration` 与 set 同槽处理；[L73-L74](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Member/PropertyTransformer.cs#L73-L74) 仅追加一条 doc 注释。Java 侧无 init 强制，外部写入在 Java 产物中无任何防护。
- required（转换器侧）：**全转换器零处理**——`Transformers/` 目录 grep `IsRequired|SetsRequiredMembers|RequiredKeyword|IsExternalInit` 无任何业务命中。`required` 成员漏初始化是纯编译期错误，漏过转换器即静默。
- record 声明侧：[RecordTransformer.cs](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Type/RecordTransformer.cs) 现 570 行，[L25-L59](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Type/RecordTransformer.cs#L25-L59) 的形态分派（record struct→可变类、abstract→不可变类、有额外实例成员→不可变类、否则 Java record）决定了下游路径；位置参数 getter 在 [L248-L258](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Type/RecordTransformer.cs#L248-L258) 单独发；immutable-class 形态只在 [L223-L245](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Type/RecordTransformer.cs#L223-L245) 发一个主构造（含 `super(...)`），**从不发射复制构造函数**（全文件无 copy/original 命中），因此即便表达式侧想调用克隆也没有 Java 侧目标。Equals/GetHashCode/PrintMembers/Deconstruct 由转换器既有路径处理，**不在本条**。
- Lowering/ 目录**无任何 with/init/required 实验**（12 个文件 grep `WithExpression|IsExternalInit|RequiredMember|record` 零命中，已核实）；按 [catalog §4 风险 9](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L1107-L1107)，本设计只借形态、不共用 Lowering 代码。

**member-desugar（C11-C14，已 Implemented）覆盖现状（2026-09-29 grep 实判，结论：with 未覆盖 / init 已覆盖（属性面）/ required 部分覆盖）**：

| 面 | 结论 | 磁盘证据 |
|---|---|---|
| `with` 表达式 | **未覆盖** | 全 `MemberDesugar/` 无任何 WithExpressionSyntax 重写；C11 反而把「with 初始化器内的属性赋值」显式判失败跳过 CS2J5124（[PropertyRule.cs L287-L293](file:///d:/code/cs2j/src/CSharpToJava.Core/MemberDesugar/Properties/PropertyRule.cs#L287-L293)，文案 [PropertyDiagnostics.cs L38-L40](file:///d:/code/cs2j/src/CSharpToJava.Core/MemberDesugar/Properties/PropertyDiagnostics.cs#L38-L40) 「C27 prerequisite missing」），被引用属性的整个 plan 原子不动 |
| 位置 record 参数属性 | **未覆盖（显式留白给 C27）** | CS2J5123 两处闸门：参数表扫描 [PropertyRule.cs L64-L69](file:///d:/code/cs2j/src/CSharpToJava.Core/MemberDesugar/Properties/PropertyRule.cs#L64-L69)、声明判定 [L257-L259](file:///d:/code/cs2j/src/CSharpToJava.Core/MemberDesugar/Properties/PropertyRule.cs#L257-L259)（`IsPositionalRecordProperty`），文案 [PropertyDiagnostics.cs L34-L36](file:///d:/code/cs2j/src/CSharpToJava.Core/MemberDesugar/Properties/PropertyDiagnostics.cs#L34-L36) 点名「left to RecordTransformer; C27」 |
| init（属性） | **已覆盖** | [PropertyFacts.cs L22-L24](file:///d:/code/cs2j/src/CSharpToJava.Core/MemberDesugar/Properties/PropertyFacts.cs#L22-L24) 识别 init；[PropertyDeclarationRewriter.cs L110-L113/L122](file:///d:/code/cs2j/src/CSharpToJava.Core/MemberDesugar/Properties/PropertyDeclarationRewriter.cs#L110-L122) 放宽为普通 set 方法并计 CS2J5120 Info（统计 `InitAccessorsWidened`）；C12 索引器另有 CS2J5132 |
| required（属性） | **已覆盖** | [PropertyFacts.cs L26-L28](file:///d:/code/cs2j/src/CSharpToJava.Core/MemberDesugar/Properties/PropertyFacts.cs#L26-L28) 识别 required 关键字；[PropertyDeclarationRewriter.cs L115-L121/L123](file:///d:/code/cs2j/src/CSharpToJava.Core/MemberDesugar/Properties/PropertyDeclarationRewriter.cs#L115-L123) 不传修饰并计 CS2J5121 Info（`RequiredConstraintsDropped`） |
| required（**字段**） | **未覆盖** | 全 `MemberDesugar/` 仅 [AccessorsToMethods.cs L60](file:///d:/code/cs2j/src/CSharpToJava.Core/MemberDesugar/AccessorsToMethods.cs#L60-L60) 在属性改写时过滤 RequiredKeyword；无字段声明规则认领 `required int F;`（E11 [DeclarationRewriter.cs L203-L204](file:///d:/code/cs2j/src/CSharpToJava.Core/RecordReadOnly/DeclarationRewriter.cs#L203-L204) 是**生产方**） |
| `[SetsRequiredMembers]` | **未覆盖** | 全 `src/` grep 零命中（无剥离、无识别）；java/csharptojava-compat 同样零命中 |
| init/required 的 Java/compat 支撑 | **无、也不需要** | `IsExternalInit`/`RequiredMemberAttribute` 在 src 与 compat 全树零命中；二者纯编译期约束，决策 13 支撑库为「无」与此一致 |

### 1.3 E11 只读化是 `with` 表达式的增量生产方

E11 record-readonly（已实现、默认关，链位 [PassCatalog.cs L179-L187](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassCatalog.cs#L179-L187)，RunAfter=`generic`（[L186](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassCatalog.cs#L186-L186)），Declarative 阶段早于 member-desugar）把大量变异点改写为 `with`：

- 站点重写器直接拼 `recv with { M = … }`：[MutationSiteRewriter.cs L873-L880](file:///d:/code/cs2j/src/CSharpToJava.Core/RecordReadOnly/MutationSiteRewriter.cs#L873-L880)；其单测实测 **45 行含 `with {`（其中 42 行为断言）**锁定 `with {` 产物（[RecordReadOnlyRewriterUnitTests.cs](file:///d:/code/cs2j/tests/CSharpToJava.ProjectTests/RecordReadOnlyRewriterUnitTests.cs)，含嵌套链 `a = a with { B = a.B with { C = 8 } }`（L1014）、数组元素、span、lambda、空条件赋值、`WithP__cs2j` 接口 ripple（L2354））。
- mutating 方法迁移出口合成 `return this with { … }`（MM-4）；接口 ripple 合成 `WithXxx` 方法，方法体仍是 `this with { … }`（E11 设计 MM-4 / IR-2，[record-readonly-design L160](file:///d:/code/cs2j/docs/superpowers/specs/2026-09-24-record-readonly-preprocessor-design.md#L160-L160) 与 [L187-L188](file:///d:/code/cs2j/docs/superpowers/specs/2026-09-24-record-readonly-preprocessor-design.md#L187-L188)；IR-3 非 record 实现者补齐在 [L189](file:///d:/code/cs2j/docs/superpowers/specs/2026-09-24-record-readonly-preprocessor-design.md#L189-L189)）。
- E11 声明侧还会**制造** init/required：[DeclarationRewriter.cs L203-L213](file:///d:/code/cs2j/src/CSharpToJava.Core/RecordReadOnly/DeclarationRewriter.cs#L203-L213)（字段提升属性时加 `required`（L203-L204）与 init 访问器（L209-L213））、[RecordConversionPlan.cs L319/L332](file:///d:/code/cs2j/src/CSharpToJava.Core/RecordReadOnly/RecordConversionPlan.cs#L319-L332)（SetToInit / FieldToInitProperty）。
- E11 已明确登记边界 RC-10「私有/不可访问复制 ctor → FB-1」、MS-12「`with` 不动」、MS-17 嵌套链——**它只生产、不消费 with**。

结论：C27 是 E11 产物的必然下游收口；E11 默认开启的可行性部分取决于 C27。C11 已在门槛第 10 条与开放问题 4 点名本任务（[c11-design L371/L416](file:///d:/code/cs2j/docs/superpowers/specs/2026-09-28-c11-property-desugar-design.md#L371-L416)；跳过码定义 [L275-L276](file:///d:/code/cs2j/docs/superpowers/specs/2026-09-28-c11-property-desugar-design.md#L275-L276)），并以 CS2J5123（位置 record 参数属性）、CS2J5124（被 with 初始化器引用的属性）两条跳过码留白——**C27 落地即承接并退役这两个跳过面**。

### 1.4 目标

在 C#→C# 层把以下构造归约为转换器无需特殊识别的普通 C#，由中间树 `dotnet build` + `dotnet test` 证明 .NET 半区等价：

1. **record class 的 `with`**：每 record 合成源内合法的虚克隆工厂（调复制 ctor），使用点改写为「工厂调用 + 成员赋值序列」，保持运行时类型多态；
2. **record struct / struct / 元组的 `with`**：局部值拷贝 + 成员赋值序列；
3. **record 位置参数成员**：位置 record 声明 reshape 为「显式成员 + 主构造函数 + 复制 ctor + Deconstruct + 克隆工厂」，使位置属性进入 C11 既有的字段/get_X/set_X 形态（退役 CS2J5123）；
4. **init/required 收尾**：record 面 init 放宽与 C11 PD-3 合流（不另立机制）；`required`（含字段）与 `[SetsRequiredMembers]` 在去糖树中移除（编译期约束解除、留痕计数，退役 CS2J5124 的 with 前提）；
5. 产物**零裸 `with` 表达式**（除显式诊断登记的 skip 项）。

明确不做：Equals/GetHashCode/PrintMembers（record 关键字保留时继续由 C# 编译器合成，见决策 8）、Deconstruct 以外的 record 合成成员重造、Java record 目标形态决策、转换器代码修改（§5.4 消费清单为后续 PR）、匿名类型 `with`（归 C21 提升后再议，v1 显式跳过）。

### 1.5 硬不变式（与 C11/C13 族契约一致）

1. 中间源码树预处理后永远可编译（整树重编译相对基线无新增 error）；record plan 为原子改写单位（声明 + 全部 with 站点全改或全不改）；
2. 禁止注释/占位/半成品流入下游：无法等价表达即显式诊断并原子不动（[catalog §4 风险 12](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L1110-L1110)）；
3. 一切站点判定来自全编译 SemanticModel（符号绑定，禁文本替换；[catalog §3.2 横切约束](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L1072-L1074)）；
4. 幂等：产物二次执行零 diff；干净文件字节级复制（含 BOM 保持）。

---

## 2. 关键设计决策表

| # | 决策点 | 结论 | 依据/备注 |
|---|---|---|---|
| 1 | record class `with` 总体形态 | 声明侧每 record 合成**源内可调用的虚克隆工厂**，使用点改写为「源表达式只求值一次 → 工厂调用 → 逐成员赋值」语句序列；空初始化器直接降为工厂调用表达式 | 编译器真实 lowering 即「虚 `<Clone>$`（体内 `new R(this)`）+ 初始化器」。`<Clone>$` 是源内非法标识符、`Clone` 被 CS8859 预留，故必须合成新名成员（决策 2）。外部站点不能直接调 protected 复制 ctor，public 工厂是唯一源内表达 |
| 2 | 克隆工厂命名与防撞 | 固定基名 **`Cs2jWithClone`**；跨 record 预留表（复用 C11 BackingFieldNaming 预留机制）发现用户已占用同名时追加确定性后缀（按裁决④统一命名规则为**单下划线**让位形 `Cs2jWithClone_cs2j`，再撞再序号；实现时若族命名收口 PR 尚未落地，临时沿用 E11/GotoEliminator 的 `__cs2j` 双下划线惯例 `Cs2jWithClone__cs2j` 并在收口 PR 统一改名——两形均不进 Java 侧硬编码）；派生覆写名必须与基类一致，碰撞解析按继承链整体取同一结果名 | `__cs2j` 前缀/后缀为现存全仓惯例（GotoEliminator、E11 `With__cs2j` 等），但 2026-09-29 裁决④收口为单下划线词表。工厂结果名进入 plan 与统计，站点重写只按 plan 中的定名发射，禁运行时拼名 |
| 3 | 多态克隆：virtual clone vs switch 类型 | **采用虚工厂**：抽象 record 发 `public abstract R Cs2jWithClone();`；非密封具体 record 发 `public virtual R Cs2jWithClone() => new R(this);`；派生 record 发 `public override D Cs2jWithClone() => new D(this);`；**密封 record 发非虚普通方法**。**否决 switch-on-type**：它是闭世界假设，树外派生 record 不可枚举，会把 `Base b = new Derived(); b with {…}` 的运行时类型静默改为 Base，直接违反「结果与接收者同运行时类型」语言保证 | 工厂体内 `new R(this)` 调复制 ctor：编译器合成 ctor（protected，类型内可访问）或用户自定义 ctor（自动接管，定制复制语义零额外处理）；派生 ctor 由 C# 语言强制链基类复制 ctor（CS8868）。E11 RC-10「复制 ctor 不可访问」在本形态下结构性消解——工厂位于类型内部，可访问同类型任何可见性的复制 ctor；树外派生类型的残余洞见决策 4 |
| 4 | 树外（metadata-only）record 的虚分派洞 | 分析器对每个含 with 站点的静态接收者类型枚举**编译内**派生 record：密封/抽象链完整→正常；非密封且编译引用中存在树外派生类型（或分析器不能证明闭包）→ 该 record plan 的相关 with 站点判 **CS2J5221 跳过**（strict Error），overrides.force 仅在用户书面接受「树外派生克隆退化为基类」时解除 | 虚方法对未覆写它的树外派生类会回落到最近树内基类工厂——这是向既有类型加虚成员的固有版本化问题，与 E11 IR-7/FB-3 的树外接口 ripple 同类。不偷偷 switch、不假装多态成立 |
| 5 | record struct / struct / 元组 `with` | **不发工厂、不调 ctor**：`s with { X = e }` → `var __cs2j_wsrc = s; var __cs2j_wclone = __cs2j_wsrc; __cs2j_wclone.set_X(e);`（局部按值拷贝即语言规定的克隆语义；struct 隐式密封无多态问题）。字段成员直写字段，属性成员经 C11 set_X。空初始化器对变量接收者可消歧为接收者本身时仍保守发拷贝（防 ref 别名可观察差异） | readonly struct/readonly record struct 的克隆局部是**可变局部**，init 放宽后的普通 set 调用合法（决策 6）。E06 tuple pass 早于本 pass，元组多已为生成 struct；残余 `ValueTuple` 站点同路径（Item1 等为字段）。record struct 位置 reshape 特有项见决策 8b |
| 6 | init-only 归宿 | **不保留 init、不引入任何「别的机制」**：与 C11 PD-3/决策 4 完全合流——record 位置/额外属性的 init 访问器在去糖树中一律为普通 set_X；record 面计数并入 C11 InitAccessorsWidened（CS2J5120 Info），C27 不另设码。**该路径 C11 已实现**（[PropertyFacts.cs L22-L24](file:///d:/code/cs2j/src/CSharpToJava.Core/MemberDesugar/Properties/PropertyFacts.cs#L22-L24) + [PropertyDeclarationRewriter.cs L110-L113](file:///d:/code/cs2j/src/CSharpToJava.Core/MemberDesugar/Properties/PropertyDeclarationRewriter.cs#L110-L113)），C27 只是让 record 位置属性进入同一路径 | init 是纯编译期 IsExternalInit/modreq 约束（全 src/compat grep `IsExternalInit` 零命中），C# 源内方法级无法表达，Java 侧也无强制；放宽只扩大「能编译的程序超集」，不改变任何原合法程序运行时行为。with 初始化器对 init 成员的赋值因此直接是 set_X 调用 |
| 7 | required 归宿 | **移除约束 + 解除强制，不注入构造期校验，不并入 ctor 参数**：(a) 属性 required 随 C11 决策 5（关键字不传方法，**已实现**：[PropertyDeclarationRewriter.cs L115-L121](file:///d:/code/cs2j/src/CSharpToJava.Core/MemberDesugar/Properties/PropertyDeclarationRewriter.cs#L115-L121)）；(b) C27 补 **record/struct 字段 required**（`required int F;`）——字段保留、去 `required` 修饰；(c) 移除源上的 `[SetsRequiredMembers]`（构造器/类型）用法（**当前全 src 零处理，C27 新建**）；(d) 统一计数 RequiredConstraintsDropped（CS2J5121 Info，并入 C11 统计） | 否决「required → ctor 参数」：普通 class/struct DTO 无位置语法，改 ctor 签名会摧毁无参构造与对象初始化器/序列化生态；record 位置参数**语法上不可加 required**（官方 required 文档：「不能在 record 的位置参数声明上使用 required 修饰符」；但可为位置属性另写显式 required 属性声明——由 RC-3 显式成员路径覆盖）。`[SetsRequiredMembers]` 仅编译器/分析器语义、无运行时行为，留着只会成为转换器侧未知注解。**with 不触发 required 强制**：官方语义——合成复制 ctor **仅在 record 存在 required 成员时**带 `[SetsRequiredMembers]`，克隆复制全部成员、with 初始化器只赋值被覆写项；另注意官方链式规则「链到带 SetsRequiredMembers ctor 的 ctor 也必须带该特性」，reshape 显式主构造/复制 ctor 因 required 已整体移除而不受影响 |
| 8a | 位置 record class reshape 形态 | **保留 `record` 关键字**，把参数表移入类型体：`record R(int X, int Y)` → 无参表 `record R { 显式属性 X/Y + 主构造 R(int x,int y) + 显式复制 ctor protected R(R o) + Deconstruct(out int x,out int y) + 克隆工厂 }`；属性随后在**同一 plan 的声明阶段直接降为终形**（后备字段 + get_X/set_X，复用 C11 AccessorsToMethods/BackingFieldNaming，不再产生 PropertyDeclarationSyntax 交给 Properties 规则二轮）；派生 `record D(int X,int B): Base(X)` 同形，主构造 `: base(x)`、复制 ctor `: base(o)` | 保留 record 即保留编译器合成的 Equals/GetHashCode/**PrintMembers**（按实例字段，显式自动属性的后备字段计入），不重造等值面；仅 **Deconstruct 是位置参数专属合成物**，须显式补发（签名按参数序 out）。主构造/复制 ctor/Deconstruct 的显式形态都是 C# 合法语法，整树重编译守门 |
| 8b | 位置 record struct reshape 差异 | 同构但：位置属性是 get/**set**（非 init）；**必须补发无参构造函数**（record struct 编译器合成无参 ctor，去掉参数表即丢失）且 ctor 赋全部字段默认值；不发复制 ctor 与克隆工厂（决策 5）；Deconstruct 同样补发；其余 struct 约束（字段初始化器限制等）由整树编译守门 | record struct 语义清单见官方文档 [Learn: record types](https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/builtin-types/record)（2026-09-29 核：仓内无本地镜像，原 `file:///d:/code/cs2j/dotnet/...` 链接在盘不存在已删）：合成位置属性（可写）、主构造、无参 ctor、Deconstruct |
| 8c | 位置 reshape 的 v1 闸门 | 出现以下任一形态的位置 record，整个 record plan 判 **CS2J5220 跳过**：① 字段初始化器或 record 体内语句**捕获主构造参数**（C#12 捕获语义，含 `private int _f = X;`，base 参数列表除外）；② 用户已声明同签名 Deconstruct/复制 ctor 但形态无法机械合并；③ 主构造参数与类型内既有成员/显式属性同名冲突无法按 C11 命名表消解。纯 DTO 形位置 record（绝大多数，含 `: base(a,b)`）完整支持 | 捕获参数迁移要把初始化器/体内语句搬进显式 ctor 体并保序，属可做但高风险的二期项；v1 宁假阳不漏改，命中率进 sdk 测量决定是否提前 |
| 9 | with 初始化器语法面 | v1 支持：成员赋值 `X = e`（左值绑定 IPropertySymbol→set_X、IFieldSymbol→字段直写，按符号不按文本）、多成员、空初始化器、**嵌套 with**（内层 RHS 先提升：内层克隆/赋值语句先于外层 set，内外源接收者各只求值一次）。v1 不支持→CS2J5224 整站点跳过：嵌套对象初始化器 `X = { … }`、嵌套集合初始化器（`Add` 形态） | 嵌套 with 是 E11 MS-17 的标准产物（`a with { B = a.B with { C = 8 } }`），必须支持；`X = { … }` 语义是对克隆的既有成员实例做就地变异，可表达（`clone.get_X().set_Y(..)`）但罕见，先测量 |
| 10 | 临时提升与不可提升上下文 | 与 C11 PD-8/决策 7 同一「单次求值临时提升」纪律：源接收者含副作用或非可重读表达式时先提升 `__cs2j_wsrc`；克隆体 `__cs2j_wclone`；语句插在最近可插入语句位置。字段初始化器、表达式体成员、特性实参、查询表达式等不可提升位置的 with → **CS2J5223** 原子跳过 | 不发「能编译、求值次数错」的程序；嵌套 with 的提升顺序在 plan 期算定（后序遍历），不靠运行时拼接 |
| 11 | 表达式树 / dynamic / 非 record class / 匿名类型 | 表达式树 lambda 内 with→CS2J5226（合法 C# 本不允许，防御性兜底，共享判定工具）；dynamic 接收者→CS2J5227；静态类型为非 record 普通 class 的 with→CS2J5228（CS8858，合法输入不应出现，残余兜底）；**匿名类型 with→CS2J5225**，等 C21 提升为命名类型后再消缺，C27 不提前消费 | 与 C11 PD-11 跳过族同构：站点与声明同生同灭、必留诊断 |
| 12 | 与 C24（初始化器归一）顺序 | member-desugar 在 **Declarative** 阶段先跑，expression-desugar（C23/C24/C35/C36，Expression 阶段）后跑：**required 与 with 初始化器在本族先归一**（with→语句序列；required 对象初始化器经 C11 PD-8→set 序列），C24 落地时面对的是普通语句 + 残余 `new C {…}` 一般形态，**不得引入 required/record 感知**；C24 对本族产物必须幂等 | 两 pass 分属不同 Stage，链序天然成立（member-desugar RunAfter 锚点在 [PassCatalog.cs L237](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassCatalog.cs#L237-L237)；expression-desugar 仍是 stub，锚点 NullCoalesce 在 [PassStubCatalog.cs L224](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassStubCatalog.cs#L224-L224)，二者无交叉）；C24 设计文档尚不存在，本约定作为给 C24 的前置契约登记 |
| 13 | 支撑库 | **无**：复制 ctor、值拷贝、普通方法/字段全是 BCL/C# 语言形态 | 无注入、无过滤、无指纹豁免成本 |
| 14 | nameof/反射契约 | with/克隆不引入新名称引用面；reshape 后位置属性名 X 仍以 get_X/set_X/后备字段存在；绑定 record 成员的 nameof 由 C11 PD-9 字面量化规则统一处理。反射契约（`<Clone>$`/GetProperty/按名 JSON）沿用 E17 同款「反射不保证」限制声明 + overrides skip 名单 | [catalog §4 风险 7](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L1105-L1105)。去糖后编译器合成 `<Clone>$` 仍在（我们只是多加一个源内工厂），record 的反射等值面不变 |
| 15 | 计划粒度与回退 | 每个 record **命名类型符号**一条 `RecordClonePlan`：含 reshape 决策、工厂定名、TouchedFiles（声明部件 + 该类型静态接收者的全部 with 站点文件）；struct/tuple 的 with 站点无声明侧改写，按接收者类型聚合为站点组 plan。含任一不可行站点 → 整 plan 原子不动；整树重编译新增错误经族迭代器归属/回退（机制随 C11） | 站点可能由 E11 新生成，plan 构建于「本 pass 运行时编译」，只看符号不看语法来源，因此 E11 产物与手写 with 同处理 |
| 16 | 转换器代码 | 本特性 PR **不动**任何 `Transformers/` 代码；§5.4 Java 半区消费清单是默认开门禁，旧 with/record 特例删除留后续 PR | 与 C11/C13 同范式；直连单文件管线保留现有（错误）行为作安全网，红灯测试一行不改 |

---

## 3. 链位与族契约

### 3.1 归属 `member-desugar`，不新增 pass、不翻转 stub、不动 RunAfterIds

- C27 没有自己的 descriptor/adapter/中间目录/CLI 开关。member-desugar **已由 C11 翻转为 Implemented**（[PassCatalog.cs L230-L241](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassCatalog.cs#L230-L241)，翻转清单执行情况见 [c11-design §3.3](file:///d:/code/cs2j/docs/superpowers/specs/2026-09-28-c11-property-desugar-design.md#L94-L108)）；[PassStubCatalog.cs](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassStubCatalog.cs) 中已无该条目（现 21 个 stub）。
- **Stage=Declarative、RunAfterIds=`{struct-value-semantics, closure-lift}` 全部沿用不动**（[PassCatalog.cs L234/L237](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassCatalog.cs#L234-L237)）；目录/指纹/保留目录/verify 全用族 pass 一份 `.cs2j-member-desugar-src`，不新增中间树环节（[catalog §4 风险 11](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L1109-L1109)）。
- **CatalogItems 只改一行**：现值 `"C11"`（[L241](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassCatalog.cs#L241-L241)，翻转时已按 [c11-design §3.3-1](file:///d:/code/cs2j/docs/superpowers/specs/2026-09-28-c11-property-desugar-design.md#L96-L96) 收窄）；C27 合入时按 C11 §3.3-10（[c11-design L108](file:///d:/code/cs2j/docs/superpowers/specs/2026-09-28-c11-property-desugar-design.md#L108-L108)）约定**追加一行**为 `"C11, C27"`（C12-C14 已合入但 CatalogItems 未追加其编号——当前实测仅 `"C11"`，合入协调按当时真值一行处理）。**2026-09-30 勘误**：catalogItems 已对齐为 `"C11, C12, C13, C14"`，C27 合入时须在此真值上追加为 `"C11, C12, C13, C14, C27"`，勿回退 C12–C14。
- Records 规则开关只以扁平属性存在于族 `MemberDesugarOptions`（§5）；族 pass 默认关期间（当前即默认关），C27 合入后仍无任何端到端行为，直到显式启用 member-desugar。

### 3.2 对已落地族基建的接缝请求

C11-C14 已在主干落地，直接在既有 `MemberDesugar/` 骨架上加 `Records/`（§12）。对照磁盘后的接缝需求：

1. `MemberDesugarKind` 枚举需含 `Record` 一类——**枚举实位是 [MemberDesugarPlan.cs L6-L13](file:///d:/code/cs2j/src/CSharpToJava.Core/MemberDesugar/MemberDesugarPlan.cs#L6-L13)**（现值 `Property/Indexer/Event/Operator/Signature`，与 [c11-design §4.5 L186](file:///d:/code/cs2j/docs/superpowers/specs/2026-09-28-c11-property-desugar-design.md#L186-L186) 一致；非原设计所写的 MemberDesugarRules.cs）。`MemberDesugarKind.Record` 是本任务对**共享文件 MemberDesugarPlan.cs** 的唯一一行追加请求；建议规则 Order=5。规则发现机制**已定为程序集扫描**（[MemberDesugarRules.cs L10-L35](file:///d:/code/cs2j/src/CSharpToJava.Core/MemberDesugar/MemberDesugarRules.cs#L10-L35)：带 `[MemberDesugarRule(kind, order)]` 特性 + 无参构造 + 实现 `IMemberDesugarRule`，按 Kind/Order 排序），Records 规则类在独占目录内加特性即被发现，无需注册点热文件；C27 不改共享文件的任何既有行为。
2. 复用（均已在盘）：全编译符号索引与站点枚举、[MemberDesugarPlan.cs](file:///d:/code/cs2j/src/CSharpToJava.Core/MemberDesugar/MemberDesugarPlan.cs) plan 基类（Id/Symbol/Outcome/TouchedFiles/OwnsTouch/Fail*）、[BackingFieldNaming.cs](file:///d:/code/cs2j/src/CSharpToJava.Core/MemberDesugar/BackingFieldNaming.cs) 跨规则预留表、[AccessorsToMethods.cs](file:///d:/code/cs2j/src/CSharpToJava.Core/MemberDesugar/AccessorsToMethods.cs) 修饰符/可访问性/显式接口骨架、[MemberDesugarSyntaxFacts.cs](file:///d:/code/cs2j/src/CSharpToJava.Core/MemberDesugar/MemberDesugarSyntaxFacts.cs)（RewriteAnnotation、表达式树/dynamic 判定、临时提升、FormatAndValidate 三候选）、整树 Analyze→Plan→逐文件 Rewrite→回退迭代器（[MemberDesugarPassAdapter.cs](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/Adapters/MemberDesugarPassAdapter.cs) 已自驱动文件环）。
3. Records 规则在族内的执行次序约定：**声明侧 Records reshape 先于 Properties/Indexers 等声明规则**（reshape 直接产终形字段+方法，不产新属性语法，故与 Properties 规则无节点争抢）；**使用点侧重写顺序**：WithExpressionRewriter 先（with 站点展开为 set_X/字段赋值），Properties/Indexers 使用点规则随后只按符号绑定扫残余——去糖后 with 已不存在，两者不在同一语法节点上交集。该顺序通过规则 Order 与族执行器保证，不靠文件遍历巧合。

### 3.3 文件所有权

| 文件/目录 | 现状归属 | C27 行为 |
|---|---|---|
| PassStubCatalog.cs / PassCatalog.cs / PassChainFactory.cs / CliPreprocessChain.cs / Program.cs | 注册表（C11 翻转已完成） | 只读；唯一改动是合入时把 [PassCatalog.cs L241](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassCatalog.cs#L241-L241) CatalogItems 改一行 `"C11, C27"`（按 C11 §3.3-10，由合入者执行） |
| `Adapters/MemberDesugarPassAdapter.cs` | C11 建立、已落地 | 不改；统计段/扁平 options 属性/诊断经规则接缝汇入（C13/C14 已验证该模式：适配器 L281-L285 汇总各 residual 计数） |
| `MemberDesugar/` 根目录共享文件 | C11-C14 共享，C27 只读复用 | **唯一例外**：[MemberDesugarPlan.cs L6-L13](file:///d:/code/cs2j/src/CSharpToJava.Core/MemberDesugar/MemberDesugarPlan.cs#L6-L13) 枚举追加 `Record` 一行（§3.2-1，订正：不是 MemberDesugarRules.cs）；命名/碰撞能力不足时扩共享层，禁止在 `Records/` 复制 |
| `MemberDesugar/Properties/**` | C11 独占（已落地） | 退役 CS2J5123/5124 需要既有三处闸门改认「已被 Records plan 认领」：[PropertyRule.cs L64-L69](file:///d:/code/cs2j/src/CSharpToJava.Core/MemberDesugar/Properties/PropertyRule.cs#L64-L69)（参数表扫描）、[L257-L259](file:///d:/code/cs2j/src/CSharpToJava.Core/MemberDesugar/Properties/PropertyRule.cs#L257-L259)（IsPositionalRecordProperty）、[L287-L293](file:///d:/code/cs2j/src/CSharpToJava.Core/MemberDesugar/Properties/PropertyRule.cs#L287-L293)（with 祖先判定）。理想路径是在共享层加 plan 归属/OwnsTouch 互斥查询、Properties 只加一个「已被 Records 认领则不判失败」挂点；**C27 无法在独占目录内单独完成此挂点**，属必须与 C11 维护者协调的一行级改动（开放问题 6） |
| `MemberDesugar/Records/**` | **C27 独占（新建）** | 全部新增代码落此目录；规则类标 `[MemberDesugarRule(MemberDesugarKind.Record, 5)]` 即被发现，无需独立注册文件 |
| `Indexers/Events/Operators` | C12/C13/C14（均已落地） | 不触碰 |
| 测试/夹具 | 各自 | 族 fixture [tests/SampleMemberDesugarSolution/](file:///d:/code/cs2j/tests/SampleMemberDesugarSolution/SampleMemberDesugarSolution.sln) **已存在**（Lib/App/Tests + sln，含 DtoSamples/InterfaceSamples 等），直接加 `Records*` 前缀夹具，不建第二套 sln |

### 3.4 硬不变式

同 §1.5：整树可编译、record plan 原子、幂等（去糖后无 WithExpressionSyntax；二次运行分析器发现 `Cs2jWithClone` 已存在即不重发、reshape 后的 record 无参数表即不再 reshape）、干净文件字节复制 + BOM。

---

## 4. Core 组件设计

目录：`src/CSharpToJava.Core/MemberDesugar/Records/`，命名空间 `CSharpToJava.Core.MemberDesugar.Records`。

### 4.1 文件清单

| 文件 | 职责 |
|---|---|
| `RecordWithOptions.cs` | `bool Strict`（默认 true，同 C11）、`bool ReportInfo`、`IReadOnlySet<string>? SkipRecords`（完全限定名）、`bool AllowMetadataDerivedCloneFallback`（force，默认 false，决策 4）、单文件模式开关 |
| `RecordWithModels.cs` | `RecordReceiverKind`（PositionalRecordClass/ExplicitRecordClass/AbstractRecordClass/RecordStruct/Struct/ValueTuple/Anonymous/NonRecordClass）、`WithSiteKind`（ClassClone/StructCopy/Nested/EmptyInitializer/UnsupportedNestedInitializer/ExpressionTree/Dynamic）、`RecordWithDiagnostic`（CS2J52xx）、`RecordWithStatistics`（通用 5 字段投影 + 特有计数）、`RecordWithFileResult` |
| `CloneCtorResolver.cs` | 对接收者 `INamedTypeSymbol`：判定 record class/struct（IsRecord/IsRecordStruct）、定位复制 ctor（合成或用户自定义，单参本类型）与可访问性、解析继承链（基 record、抽象/密封、`: base(...)` 参数）、给出工厂形态（abstract/virtual/override/sealed/none）与定名（预留表防撞，决策 2）、枚举编译内派生 record 并探测树外派生洞（决策 4）、匿名/非 record 分类 |
| `PositionalRecordShapeAnalyzer.cs` | 位置参数清单与属性符号对应；检测 reshape 闸门（决策 8c：主构造参数捕获点扫描——字段初始化器/体内语句中的参数引用，base 参数列表白名单）；既有 Deconstruct/复制 ctor/无参 ctor 盘点；派生链参数映射；泛型参数透传 |
| `RecordClonePlan.cs` | `RecordClonePlan`：接收者类型符号、kind、工厂定名与形态、reshape 决策（位置参数/ctor/Deconstruct 物料）、`IReadOnlyList<WithSite>`（节点文件+span+种类+绑定成员符号列表）、Outcome/FailCode/FailReason/TouchedFiles/OwnsTouch；确定性排序（类型完全限定名 + 站点 span） |
| `RecordDeclarationRewriter.cs` | **阶段一（声明侧）**：① 注入克隆工厂（抽象/虚/覆写/密封形，体内 `new T(this)`）；② 位置 reshape（去参数表、发显式属性并**直接降终形**：后备字段+get_X/set_X、主构造、复制 ctor、无参 ctor（struct）、Deconstruct、base 链）；③ record 字段去 required、SetsRequiredMembers 用法移除（属性面由 C11 规则负责，本文件只处理字段与 record 特有承载）；全部挂族 RewriteAnnotation，保 XML doc/trivia/特性（attribute 路由复用 C11 策略：`[field:]`→后备字段等） |
| `WithExpressionRewriter.cs` | **阶段二（使用点）**：只接受语义反查命中 plan 的 WithExpressionSyntax；record class→工厂调用 + 赋值序列；struct/tuple→值拷贝局部 + 赋值；空初始化器→纯工厂/纯拷贝；嵌套 with 递归后序提升；字段/属性按 IFieldSymbol/IPropertySymbol 分别发射直写/set_X；源接收者单次求值（决策 10）；不可提升上下文/不支持初始化器→交由 plan 期定案跳过，本文件不产出半成品 |
| `RecordResidualScanner.cs` | 重写后整树扫描：WithExpressionSyntax 总数、按接收者 kind 分桶、克隆工厂覆盖率（每个被克隆的 record 都有同定名工厂）、位置参数表残留；与 skip plan 集合对账 |
| `RecordDiagnostics.cs` | CS2J5210-5230 码位常量与消息模板（§6） |
| `RecordRule.cs` | `IMemberDesugarRule` 实现（类标 `[MemberDesugarRule(MemberDesugarKind.Record, /*order*/5)]`，经既有程序集扫描自动发现，见 [MemberDesugarRules.cs L18-L35](file:///d:/code/cs2j/src/CSharpToJava.Core/MemberDesugar/MemberDesugarRules.cs#L18-L35)）：贡献 plan 列表、挂两阶段 rewriter、统计段与摘要片段、OwnsTouch 归属、与 Properties 规则的站点认领互斥查询（§3.2-3） |
| `RecordRuleRegistration.cs` | **可不建**：发现机制已定为程序集扫描（§3.2-1），规则类加特性即注册；若需要目录内组织说明/内部聚合，可留为同目录普通静态类，不含任何热文件注册 |

编译构造沿用族标准（`CSharpParseOptions(LanguageVersion.Latest)` + 合成 global usings + `GetBasicReferences()` DLL、`allowUnsafe: true`）；分析只查询**当轮原始** SemanticModel。

### 4.2 两阶段数据流

1. **Analyze（整树一次，经族执行器）**：枚举编译内全部 record 类型符号（partial 多部件按符号合并）与全部 WithExpressionSyntax，经 `GetTypeInfo/GetSymbolInfo/GetOperation` 分类绑定；CloneCtorResolver 产出每类型克隆形态；PositionalRecordShapeAnalyzer 产出 reshape 决策或闸门码；跨文件站点天然闭合（同 CSharpCompilation）。
2. **Plan（每接收者类型一条）**：定案工厂定名（预留表跨 record 一次算定，派生链同名）、TouchedFiles、Outcome；任一不可行站点/闸门命中 → 整 plan `Skipped` + 诊断，声明与站点全不动。
3. **Rewrite（逐文件两小阶段）**：RecordDeclarationRewriter → WithExpressionRewriter，随后族 FormatAndValidate 三候选；Changed=false 字节复制。
4. **ResidualScan（AfterFiles）**：残余裸 with 无对应 skip → CS2J5230（strict Error）。
5. **回退**：整树重编译新增错误按 TouchedFiles/OwnsTouch 归属唯一 plan，族迭代器 ExcludePlans 后原件重放；无法归属走族级 CS2J5100 全回退。稳态预期零回退（reshape 后符号关系由整树编译强保证）。
6. **单文件模式**：plan 越出输入文件 → CS2J5290 排除（同 JK090/MD290 族惯例）。

### 4.3 规则全集（RC-x / WR-x）

**声明侧（RC-x）**

- **RC-1 显式成员 record class 克隆工厂**：非抽象非密封 record 注入 `public virtual T Cs2jWithClone() => new T(this);`；抽象 record 注入 `public abstract T Cs2jWithClone();`；密封 record 注入非虚 `public T Cs2jWithClone() => new T(this);`。
- **RC-2 派生 record 覆写**：`public override D Cs2jWithClone() => new D(this);`，结果名沿基类链定名（决策 2/3）。
- **RC-3 位置 record class reshape**：决策 8a；示例（同 plan 内属性直降终形，下例为说明性中间形）：

  ```csharp
  // 输入
  public record Point(int X, int Y);
  public record NamedPoint(string Name, int X, int Y) : Point(X, Y);
  // 声明侧产物（概念形；实际属性已为后备字段 + get_X/set_X）
  public record Point
  {
      public int X { get; set; }
      public int Y { get; set; }
      public Point() {}
      public Point(int x, int y) { X = x; Y = y; }
      protected Point(Point original) { X = original.X; Y = original.Y; }
      public void Deconstruct(out int x, out int y) { x = X; y = Y; }
      public virtual Point Cs2jWithClone() => new Point(this);
  }
  public record NamedPoint : Point
  {
      public string Name { get; set; }
      public NamedPoint() {}
      public NamedPoint(string name, int x, int y) : base(x, y) { Name = name; }
      protected NamedPoint(NamedPoint original) : base(original) { Name = original.Name; }
      public void Deconstruct(out string name, out int x, out int y) { name = Name; x = X; y = Y; }
      public override NamedPoint Cs2jWithClone() => new NamedPoint(this);
  }
  ```
  说明：set_X 终形即 init 放宽落点（CS2J5120）；无参 ctor 服务对象初始化器；用户已显式声明同形 ctor/Deconstruct 时不重复发（符号盘点）。
- **RC-4 位置 record struct reshape**：决策 8b——去参数表、显式可写属性（直降字段+get/set）、主构造、**无参 ctor（全字段默认值）**、Deconstruct；**无复制 ctor/工厂**。
- **RC-5 record 字段 required / SetsRequiredMembers**：去字段 `required` 修饰（后备字段命名走共享表）；移除构造器与类型声明上的 `[SetsRequiredMembers]`；计数 CS2J5121。属性 required 已归 C11，本规则不重复认领。
- **RC-6 闸门**：决策 8c（CS2J5220）、决策 4（CS2J5221）、元数据 record 接收者无法补发工厂（CS2J5222）；整 plan 原子不动。

**使用点侧（WR-x）**

- **WR-1 record class with**：`r with { X = 1, Y = e }`（语句位）→
  ```csharp
  var __cs2j_wsrc = r;                    // 仅接收者非常量/不可安全重读时
  var __cs2j_wclone = __cs2j_wsrc.Cs2jWithClone();
  __cs2j_wclone.set_X(1);
  __cs2j_wclone.set_Y(e);
  // 表达式取值 __cs2j_wclone
  ```
  字段成员为 `__cs2j_wclone.field = e;`（仅原程序可访问的字段——克隆体与源同类型，类型内 with 可访问私有字段，外部 with 访问私有字段在原程序即 CS0122）。
- **WR-2 多态 with**：`Point p = new NamedPoint(...); var q = p with { X = 5 };` → 工厂虚分派，q 运行时类型为 NamedPoint；.NET 半区 `Assert.IsType<NamedPoint>(q)` 守门。
- **WR-3 空初始化器**：`r with { }` → `r.Cs2jWithClone()`（表达式内联，无需语句）；struct 空 with → 值拷贝局部（保守）。
- **WR-4 struct/tuple with**：决策 5，双局部值拷贝 + 赋值序列；record struct 属性可写→set_X，元组残余→ItemN 字段直写。
- **WR-5 嵌套 with**：`a with { B = a.B with { C = 8 } }` 后序提升——内层源（外层源的 B）先取、内层克隆与赋值先于外层 set_B；每段接收者只求值一次；E11 MS-17 链式产物同规则覆盖。
- **WR-6 record 内部 with**：E11 合成的 `this with { P = v }`（MM 出口、WithXxx 方法体内）与类型内任何 with 同形改写；类型内站点工厂调用同样走虚形态（密封时非虚直调）。
- **WR-7 不可提升/不支持站点**：决策 9/10/11 全部在 plan 期定案（CS2J5223/5224/5225/5226/5227/5228），使用点与声明同不动。
- **WR-8 残余对账**：输出树每个 WithExpressionSyntax 必须属于已重写或带码 skip 两类之一（CS2J5230）。

---

## 5. CLI 集成

C27 **不改 Program.cs、不增适配器、不增开关/verb、不改注册表结构**，全部 CLI 面由 C11 建立的族适配器承载：

1. **执行**：族适配器整树自驱动骨架（[ProjectPassAdapter.cs:79-108](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/ProjectPassAdapter.cs#L79-L108) PassExecutionContext + [ProjectPassRunner.cs](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/ProjectPassRunner.cs) 阶段助手）；Records 规则不请求支撑库（决策 13）。
2. **选项透传**：族 [MemberDesugarOptions L151-L169](file:///d:/code/cs2j/src/CSharpToJava.Core/MemberDesugar/MemberDesugarModels.cs#L151-L169) 现为**扁平**类（SkipQualifiedNames/ForceQualifiedNames/Strict/ReportInfo/EventSubscriptionPolicy，规则特有项以扁平属性加在同一类上，C13 的 EventSubscriptionPolicy 即先例，**无 `rules.xxx` 子节机制**）；C27 沿用此惯例：record/with 站点的 skip/force 复用既有 SkipQualifiedNames（`Ns.Type`/`Ns.Type::Member`）与 ForceQualifiedNames，规则特有开关以一个扁平属性加入该类（如 `AllowMetadataDerivedCloneFallback`，对应 CS2J5221 force），经通用 `preprocess --pass member-desugar` 与 convert-project 链到达规则，不新增 CLI 参数。
3. **统计汇入**：RecordWithStatistics 投影通用 5 字段进族统计；族摘要行追加 records 段，建议形如：
   `member-desugar: … records: <RecordPlans> reshaped, <WithSites> with-site(s) rewritten (<classClone>/<structCopy>/<nested>/<empty>), <CloneFactories> clone factor(ies), <PositionalReshaped> positional, <RequiredDropped>, <Skipped> skipped, <ResidualWith> residual`。
   特有计数：RecordClassPlans/RecordStructPlans/PositionalReshaped/CloneFactoriesSynthesized（abstract/virtual/override/sealed 分项）、WithSitesTotal/WithSitesClass/WithSitesStruct/WithSitesNested/WithSitesEmpty、FieldAssignments/PropertyAssignments、TemporariesPromoted、RequiredFieldDropped、SetsRequiredMembersRemoved、PlansSkipped（按码分桶）、ResidualWithCount。
4. **诊断汇入**：经族适配器统一选择器映射通用 PassDiagnostic；Info 仅 verbose。
5. **指纹/目录**：沿用族一份；非启用零痕迹；CatalogItems 展示随 C11/合入序追加。

### 5.4 Java 半区消费清单（转换器后续 PR 的输入，本特性不实施）

默认开启前逐项核实/补齐（缺则补转换器，不凭空假设；对齐 C11 §5.3/C13 §5.4 纪律）：

1. **复制构造函数的 Java 发射**：去糖 record 的 `protected R(R original)`（及派生 `: base(original)` 链）经 ConstructorTransformer/ClassTransformer 必须产出同形 Java 构造（immutable-class 形态方法 [CreateImmutableClass L155](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Type/RecordTransformer.cs#L155-L155) 现仅在 [L223-L245](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Type/RecordTransformer.cs#L223-L245) 发一个主构造含 `super(...)`，全文件无复制 ctor 发射，需补发）；这是克隆工厂 `new R(this)` 的 Java 侧目标，**无此前置则去糖 record 的 Java 产物不可编译**——列头号核查项。
2. **克隆工厂识别**：`Cs2jWithClone()`（含 `__cs2j` 防撞后缀）经 MethodTransformer 按普通方法发射；其体内 `new R(this)` 与复制构造配对；abstract/override 修饰走现有虚方法分派（Java 万物虚，override 形态天然成立）。
3. **with 使用点零特殊路径**：去糖树无 WithExpressionSyntax，[ControlFlowTransformer.cs:54/574-638](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/ControlFlowTransformer.cs#L574-L638) 在预处理输入上证实不可达；[RecordTupleTests.cs:26-31](file:///d:/code/cs2j/tests/CSharpToJava.ConversionTests/Categories/RecordTupleTests.cs#L26-L31) 的错误断言（`new R()` 丢覆写）改写为「去糖 C# → Java」断言，值必须保留（拷贝 + X=2）；旧分支删除留后续 PR。
4. **位置 reshape 后的 record 形态**：无参数表、有额外字段/方法的 record 命中既有 immutable-class fallback（[L44-L58](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Type/RecordTransformer.cs#L44-L58)）；Deconstruct、get_X/set_X（C11 消费改造联动）、后备字段名经既有路径；record struct reshape 后走 CreateMutableRecordStruct（[L477](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Type/RecordTransformer.cs#L477-L477)），无参 ctor 不重复发。
5. **init/required 注解清扫**：确认去糖树无 `required`/`[SetsRequiredMembers]` 残留进入注解桥；init doc 注释特例（[PropertyTransformer.cs:73-74](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Member/PropertyTransformer.cs#L73-L74)）在 C11 消费 PR 中随路径不可达自然失效。
6. **引用类型成员浅拷贝**：C# record 克隆本即浅拷贝（克隆与原共享引用成员实例，语言文档明示）；Java 复制构造须同形（引用复制，不做深克隆），.NET/Java 双端对照测试守门。

---

## 6. 诊断策略

Records 规则级诊断占 **CS2J52xx** 子段提案：c11 设计登记表为 property 留 5110-5129、C12 5130-5149、C13 5150-5169、C14 5170-5189、C46 建议 5190-5199（[c11-design §4.5/§11 码位行 L207](file:///d:/code/cs2j/docs/superpowers/specs/2026-09-28-c11-property-desugar-design.md#L207-L207)）；磁盘现状（[MemberDesugarModels.cs L12-L75](file:///d:/code/cs2j/src/CSharpToJava.Core/MemberDesugar/MemberDesugarModels.cs#L12-L75)）：property 已占 5110-5124（含 CS2J5123/5124 两个 C27 留白码）、indexer 5130-5141、event 5150-5158、operator 5170-5183，5184-5199 空闲；**C27 提议 5200-5230**（C39 及以后顺延），与已占码位无冲突，最终码位表在共享枚举登记确认，调整仅做常量改名。族级公共码（CS2J5100-5104 回退/restore/build/test、5109 单文件）不重复定义。MSBuild 格式：`文件(行,列): warning/error CS2J52xx: [Ns.Type] 描述`。

| 码 | 级别 | 含义 |
|---|---|---|
| CS2J5210 | Info | with 站点已改写（接收者类型/kind/成员数/是否嵌套），仅 verbose |
| CS2J5211 | Info | record 克隆工厂已合成（类型/形态 abstract/virtual/override/sealed/结果名），仅 verbose |
| CS2J5212 | Info | record 位置 reshape 完成（参数数/补发 ctor/Deconstruct 计数），仅 verbose |
| CS2J5213 | Info | record 字段 required 移除 / SetsRequiredMembers 用法剥离（聚合计数；属性面计入 C11 CS2J5121），仅 verbose |
| CS2J5220 | Warning（strict Error） | 位置 record 触发 reshape 闸门（主构造参数捕获/同签名成员冲突等，决策 8c），整 record plan 跳过 |
| CS2J5221 | Warning（strict Error） | 非密封 record 存在树外派生类型风险，虚克隆无法保证运行时类型（决策 4），相关 with 站点整 plan 跳过；force 可解除（用户接受退化） |
| CS2J5222 | Warning（strict Error） | 接收者为元数据 record（声明不在编译树内），无法补发克隆工厂，with 站点跳过 |
| CS2J5223 | Warning（strict Error） | with 位于不可临时提升的表达式上下文（字段初始化器/表达式体/特性实参/查询等，决策 10），plan 跳过 |
| CS2J5224 | Warning（strict Error） | with 初始化器含 v1 不收编的嵌套对象/集合初始化器（决策 9），站点跳过 |
| CS2J5225 | Warning（strict Error） | 匿名类型 with（待 C21 提升后消缺，决策 11），站点跳过 |
| CS2J5226 | Warning（strict Error） | with 位于表达式树 lambda 内（防御性，决策 11），plan 跳过 |
| CS2J5227 | Warning（strict Error） | dynamic 接收者 with 不可判定，plan 跳过（force 不解除） |
| CS2J5228 | Warning（strict Error） | 非 record 普通 class 接收者 with（CS8858 残余兜底）/成员左值绑定不可解析，站点跳过 |
| CS2J5230 | Warning（strict Error） | residual 闸门：输出残留无对应 skip 诊断的 WithExpressionSyntax，或被克隆 record 缺同定名工厂 |
| CS2J5290 | Warning | 单文件模式：record plan 非自包含（声明/站点跨文件），排除 |

原则（[catalog §4 风险 12](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L1110-L1110)）：不可判定即显式诊断、整 plan 不动；**禁止只改声明留 with、禁止 with 原样透传、禁止 `.clone()` 式想当然发射**；Warning 经 Strict 升 Error；SkippedByReason 分桶进统计与 sdk 盘点。已知限制文档化：反射（合成 `<Clone>$` 仍在但源内工厂是新增成员）、树外派生多态（CS2J5221）、匿名类型（CS2J5225）不保证。

---

## 7. 测试计划

### 7.1 Core 单测矩阵

新增 `tests/CSharpToJava.ProjectTests/MemberDesugarRecordWithUnitTests.cs`（内联源码 → CSharpCompilation → 族规则 Bind/Analyze/RewriteFile → 忽略空白断言 + AssertOutputCompiles，仿 RecordReadOnlyRewriterUnitTests/JavaKeyword 单测模式）：

1. **record class with**：位置/显式属性 record 的单成员/多成员/空初始化器；字段成员 with（类型内私有字段）；属性经 set_X、字段直写分别断言；源接收者副作用（`Get() with {…}`、`arr[i] with {…}`）断言只求值一次。
2. **继承多态（重点）**：`Point p = new NamedPoint(…); p with { X = 5 }` 输出调虚工厂且 .NET 侧运行时类型为 NamedPoint；抽象 record 基类发 abstract 工厂；密封 record 发非虚方法；三层继承链；泛型 record `Box<T>`；自定义复制 ctor 被工厂调用（拷贝计数/定制语义断言）。
3. **record struct / struct / 元组**：record struct 位置/显式成员 with（值拷贝局部、set 序列）；readonly record struct with（可变克隆局部可写）；普通 struct with；`ValueTuple` 残余站点（Item1 字段直写）；嵌套 struct 链。
4. **位置 reshape（class）**：参数→后备字段/get_X/set_X、主构造、protected 复制 ctor（含 `: base(o)` 链）、Deconstruct（out 序）、无参 ctor；派生 `: base(x)` 参数映射；reshape 后 Deconstruct 解构调用与 PrintMembers/ToString 行为在 .NET 侧保持；抽象位置 record。
5. **位置 reshape（struct）**：补发无参 ctor（`new R()` 可用且字段默认值正确）、主构造、Deconstruct；无工厂/无复制 ctor；可写属性形态。
6. **嵌套 with**：`a with { B = a.B with { C = 8 } }`（内层先提升、内外源各求值一次）；三层嵌套；嵌套中一侧为 struct；与 E11 MS-17 产物逐字同形的用例集。
7. **record 内 with**：方法体/WithXxx 成员中的 `this with { P = v }`（E11 MM-4/IR 形态）；构造器外 init 放宽后 set 调用可编译。
8. **init/required**：位置属性 init→set_X（CS2J5120 计数）；record 上 `required` 属性（经 C11 路径）与 **required 字段**（RC-5）；`[SetsRequiredMembers]` 构造器/类型剥离；with 中不要求全 required（克隆后只覆写单项，.NET 侧其余值来自源）；required + 对象初始化器组合经 C11 PD-8 后无 required 残留。
9. **不可提升/跳过族**：字段初始化器内 with→CS2J5223；嵌套对象/集合初始化器→CS2J5224；匿名类型→CS2J5225；表达式树→CS2J5226；dynamic→CS2J5227；非 record class→CS2J5228；树外派生洞→CS2J5221（含 force 解除用例）；元数据 record→CS2J5222；位置闸门三类→CS2J5220；每种断言 plan 原子性（声明与站点同不动）。
10. **工程性质**：幂等（二次零 diff：无 with、工厂不重发、无参表 record 不再 reshape）；干净文件字节级一致 + BOM；partial record 多部件按符号合并；跨文件声明/站点一次 Bind 两边都改；单文件越界→CS2J5290。
11. **边界零误伤**：非 with 的 `with` 字样（注释/字符串/using 别名）不动；不含 record/with 的文件字节复制；C11/C12/C13/C14 语法面（普通属性、索引器、事件、运算符）不被 Records 规则认领。

### 7.2 项目级 fixture

并入**已存在**的族共享解决方案 [tests/SampleMemberDesugarSolution/](file:///d:/code/cs2j/tests/SampleMemberDesugarSolution/SampleMemberDesugarSolution.sln)（2026-09-29 在盘：Lib+App+Tests+.sln，net 样例含 DtoSamples/InterfaceSamples/IndexerSamples/EventSamples/OperatorSamples），C27 只增 `Records*` 前缀夹具；无需另建第二套 sln（若 Records 夹具规模证明需要独立 sln，再仿 SampleRecordSolution 建 `tests/SampleRecordWithSolution/` 并后续评估并入）：

- Lib：位置 record 群（class/struct、泛型、三层继承、抽象基 + 两个派生、自定义复制 ctor、带额外属性/字段的 record、required/init record）；一个主构造参数捕获的反例 record（独立文件，断言 CS2J5220 跳过且中间树可编译）；普通 struct 与元组 with 用例；一个树外派生洞对照（引用元数据 record 的独立程序集，断言 CS2J5221）。
- App/Tests（.NET 半区行为守门，xunit）：
  - **运行时 with 语义对照**：克隆是新对象（`!ReferenceEquals`/struct 值独立）、未覆写成员逐字段相等、覆写成员取新值、**源对象不变**（新旧独立性）、引用类型成员浅拷贝共享（与 C# 原生 with 结果一致）；
  - **多态**：基类静态类型 with 后运行时类型保持派生；
  - 嵌套 with 内外独立、数组/集合元素 with、Deconstruct/ToString 不回归。
- 项目级测试 `MemberDesugarRecordWithProjectTests.cs`：`result.Success`、统计计数、中间树文本断言（**零 ` with {`**、含 `Cs2jWithClone`、含复制 ctor、位置 record 无参数表 + 有 Deconstruct）、`dotnet build -c Release` 与 `dotnet test -c Release` 退出码 0；干净树字节复制/BOM。

### 7.3 Release build/test

- `dotnet build -c Release`（CSharpToJavaConverter.slnx 全绿）；
- `dotnet test -c Release` 三个工程：CSharpToJava.Tests、CSharpToJava.ProjectTests、CSharpToJava.ConversionTests。

### 7.4 E11 产物共处回归（关键）

- 对 SampleRecordSolution（E11 fixture，其 App 已断言含 `with {`，[RecordReadOnlyProjectTests.cs:101](file:///d:/code/cs2j/tests/CSharpToJava.ProjectTests/RecordReadOnlyProjectTests.cs#L101-L101)）启用 member-desugar（Records 规则）：E11 产出的全部 with（MS-1~MS-17、MM 出口、WithXxx 方法体）被消费，中间树 build/test 全绿；E11 自身统计**零变化**（C27 在下游不回改 E11 决策）。
- RecordReadOnlyRewriterUnitTests 实测 45 行含 `with {`（42 行为断言：含 MS 站点、MM 出口 `return this with`、IR-2 `WithP`/`WithP__cs2j` 等形态）逐条在 C27 单测中有「with→无 with」对应断言，形成上下游对照表。

### 7.5 residual 闸门（零 with）

1. 扫描型测试：输出树每个 WithExpressionSyntax 属于 Rewritten 或带 CS2J52xx skip 两类；被克隆 record 工厂覆盖率 100%；strict 下 CS2J5230 非零即退出非零。
2. 跳过反例集：§7.1-9 每类 plan 原子性 + 无中间目录残留（CS2J5100 全回退演练沿用族）。
3. 既有 fixture 回归：SampleProject、SampleRecordSolution、SampleRefOutInSolution、SampleArraySolution、SampleJavaKeywordSolution、SampleMemberDesugarSolution 启用本族后中间树 build/test 全绿；C11-C14 各规则统计零变化。

### 7.6 CLI / 红灯对照

- C11 已合入、member-desugar 已是 Implemented（默认关），CLI 测试直接按启用形态写：`preprocess --pass member-desugar` 时 Records 规则随族执行、摘要含 records 段、`--list-passes` catalogItems 含 C27（合入后 L241 为 `"C11, C27"`）、`--strict` 下 CS2J5230/522x 退出码语义、禁用时无中间目录。
- **不改红灯测试一行**：[RecordTupleTests.cs](file:///d:/code/cs2j/tests/CSharpToJava.ConversionTests/Categories/RecordTupleTests.cs)、[RecordTransformerTests.cs](file:///d:/code/cs2j/tests/CSharpToJava.Tests/RecordTransformerTests.cs) 走直连单文件管线，是转换器安全网（含当前错误行为锁定）；为每用例建 C# 半区正确对照（尤其 L29 丢值反例），作为 §5.4 Java 消费 PR 的核对底稿。

---

## 8. 默认开启验收门槛

Records 规则无独立开关默认态，随 member-desugar 族 pass 默认开而开；族默认开评审中本规则须额外满足：

1. slnx `dotnet build -c Release` 全绿；三个测试工程 `dotnet test -c Release` 全绿（含 §7 全部新测、CLI/一致性/链序）。
2. fixture sln 中间树 build + test 全绿；幂等、字节复制、residual 零 with 闸门全绿；回退演练通过。
3. **Java 半区就绪**：§5.4 六项逐项核实——**复制构造 Java 发射（第 1 项）与 with 旧路径不可达（第 3 项）为硬门禁**；代表性样本（位置 class/struct、三层继承多态、自定义复制 ctor、嵌套 with、required/init、E11 产物）经完整 C#→Java 链路产出可编译 Java 且双端测试通过（含浅拷贝与新旧独立性）。
4. csharp-sdk（只读训练集）启用扫描：with 站点总量与按接收者 kind 分布、位置 record 数、CS2J5220-5228 命中清单与 SkippedByReason 报表；**每条 language-infeasible 命中人工定性**（真不可行 vs 过保守），过保守项清零或进明确里程碑；重点给 CS2J5220（主构造捕获）与 CS2J5221（树外派生）命中率。
5. sdk 基线不退化：启用后各 fixture 与 sdk 抽样工程中间树 build/test 不劣于未启用；E11 及其余上游 pass 统计零变化；convert-project 耗时增量可接受（指纹缓存评估）。
6. overrides/逃生口、`--list-passes` 描述、README/ROADMAP 条目齐备；本设计与 plan 文档定稿。

未达门槛长期随族 opt-in，每评审周期复跑第 4/5 项（[catalog §4 风险 10](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L1108-L1108)）。

---

## 9. 里程碑

C11-C14 已落地，C27 直接开工，建议四段：

| 里程碑 | 内容 | 验证/规模 |
|---|---|---|
| M0 测量与骨架 | `Records/` 骨架（models/options 扁平项/plan/规则类标 `[MemberDesugarRule]`，**无独立注册文件**；Properties 挂点协调见开放问题 6）；CloneCtorResolver 全分类（含树外派生探测）；sdk with/位置 record 命中率测量轮 | Core ~300 行；M0 数据 |
| M1 显式成员 record with | RC-1/RC-2 + WR-1/WR-2/WR-3/WR-6/WR-7：克隆工厂（abstract/virtual/override/sealed/自定义复制 ctor）+ 类/record class with 改写 + 多态 + 空初始化器 + 临时提升 + 跳过族主体；Core 单测 1/2/7/9 主体 | ~500 行；基本样本中间树 build 通过；多态 .NET 测试绿 |
| M2 struct 与位置 reshape | RC-3/RC-4 + WR-4/WR-5：位置 class/struct reshape（ctor/复制 ctor/无参 ctor/Deconstruct/基链/泛型）、闸门 CS2J5220、struct/tuple 值拷贝、嵌套 with 后序提升；fixture（含继承层次）+ residual 闸门 + 回退测试 | ~700 行 + fixture；fixture Release build/test 全绿 |
| M3 required/init 收尾与 E11 回归 | RC-5、CS2J5120/5121 record 面计数、SetsRequiredMembers 剥离；E11 产物共处回归（§7.4）；CS2J5221-5228 全部跳过码打磨；元数据/匿名/表达式树边界；幂等/BOM 审计；Release build/test 全绿 | ~300 行；sdk 新增失败为零 |
| M4 默认开证据包 | sdk 全量 triage、§5.4 Java 半区核查（只核查不改转换器，清理另开 PR）、CatalogItems 追加、红灯对照归档、plan 文档；门槛通过随族评审默认开 | 文档/数据 |
| M4+（不承诺） | 转换器旧 with/record 特例删除与复制 ctor 发射（§5.4）；主构造参数捕获 reshape（退役 CS2J5220）；嵌套对象/集合初始化器（退役 CS2J5224）；C21 落地后匿名类型 with（退役 CS2J5225） | 独立后续 PR |

---

## 10. 风险

1. **位置 reshape 的语义保真**：Deconstruct 补发遗漏、record struct 无参 ctor/字段默认值、派生 `: base(...)` 参数映射、主构造参数捕获（CS2J5220 闸门）、reshape 后 PrintMembers/Equals 字段集变化。缓解：保留 record 关键字让编译器继续合成等值/打印面，reshape 只补发 ctor/Deconstruct；.NET 半区 xunit 对 ToString/Deconstruct/相等性逐项断言；闸门宁可跳过不半改。
2. **多态克隆的树外派生洞**（决策 4）：虚工厂对未覆写的元数据派生类回落到基类，静默改变运行时类型。缓解：编译内派生枚举 + CS2J5221 显式跳过/force 知情解除；sdk 实测非密封 record 占比决定是否需要更强闭包分析。
3. **浅拷贝误读为深拷贝**：引用成员在克隆与源间共享是 C# 语义（[Learn 文档示例](https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/operators/with-expression)），不是缺陷。缓解：fixture 双端断言共享行为；Java 复制构造核查（§5.4-6）明确引用复制。
4. **临时提升/求值次序**：副作用接收者、嵌套 with、数组元素接收者处理错误会造「能编译、行为错」。缓解：plan 期后序算定提升序列、源临时单发；不可提升即 CS2J5223；整树编译 + .NET 行为测试双保险。
5. **E11 上下游耦合**：E11 是 with 大生产方，其 MM/IR/MS 形态（含 `WithXxx__cs2j` 改名、接口 ripple）若演进会增减站点形态。缓解：站点只按 SemanticModel 绑定分类、不按文本/不按 E11 注解；E11 统计零变化为硬回归项；§7.4 共处回归固定。
6. **init/required 放宽的编译期盲区**：中间树合法程序超集，漏初始化不在 .NET 侧报红（与 C11 风险 4 同源）。缓解：纯编译期约束、运行时行为不变；计数留痕（CS2J5120/5121）；正确性由初始化器/克隆路径的行为测试覆盖。
7. **克隆工厂命名碰撞与 JK 终命名**：用户成员占用 `Cs2jWithClone`（低频但合法）。缓解：跨 record 预留表 + 确定性后缀；结果名若撞 Java 保留字由 C08 java-keywords 链尾统一逃逸，C27 不自做终命名。
8. **Lowering 误用**（[catalog §4 风险 9](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L1107-L1107)）：Lowering/ 12 个文件无 with/init/required 实验（已 grep 核实），本规则 Core 新目录零 using Lowering。
9. **Java 半区复制构造缺口**：去糖 record 依赖 Java 侧复制构造与工厂配对发射，消费 PR 未完成前默认不可开。缓解：§5.4-1 列为硬门禁；pass 默认关；红灯测试留证。
10. **residual 闸门与既有错误输出的冲突**：当前转换器容忍裸 with（产出错误 Java）；reshape/跳过边界外的残余必须零漏网。缓解：CS2J5230 strict Error + sdk 全量扫描；禁止「扫不到就放过」。

---

## 11. 并行协调

| 并行条目 | 关系 | 协调动作 |
|---|---|---|
| **C11 Property** | ~~硬前置~~ **已落地**：族基建、stub 翻转、plan/回退/格式化/命名表/规则发现均已在盘（PassCatalog Implemented、`[MemberDesugarRule]` 程序集扫描）；CS2J5123/5124 留白码仍由 C27 承接退役 | C27 直接在主干开工；请求共享枚举 [MemberDesugarPlan.cs L6-L13](file:///d:/code/cs2j/src/CSharpToJava.Core/MemberDesugar/MemberDesugarPlan.cs#L6-L13) 加 `Record`（一行）与站点认领互斥挂点（开放问题 6，三处挂点在 PropertyRule.cs）；规则 id/Order 与发现机制已无须再对齐（沿用特性即可）；init/required 计数并入 C11 统计不另立；**不改 Properties/ 规则行为与适配器**（挂点除外，按开放问题 6 裁决执行） |
| **E11 RecordReadOnly（已实现，默认关）** | C27 是 E11 所产 with 的消费收口；E11 链位在前（RunAfter=generic，同 Declarative 阶段早于 member-desugar） | 以 E11 输出树为必跑回归（§7.4），E11 统计零变化；E11 的 RC-10 复制 ctor 可访问性阻断由本特性结构性消解（决策 3），E11 侧不因此改规则；E11 接口 ripple 的 `WithXxx` 方法体（`this with`）由 WR-6 消费 |
| **E13 make-readonly / C07 struct-value-semantics** | member-desugar RunAfter 已锁（struct-value-semantics）；struct with 值拷贝以变异迁移完成后的形态为输入 | C27 不改写 E13/C07 产物语义；readonly struct 的 with 在可变克隆局部上经放宽 setter 成立（对 readonly record 天然成立，catalog C27 条目已述）；以其输出树做共处回归 |
| **C12 Indexer / C13 Event / C14 Operator** | 同一族 pass、同一共享层、不同独占目录；**三者均已 Implemented**（在盘目录 Indexers/Events/Operators，诊断 5130-5183 段已占） | 目录互不触碰；聚合面（规则发现/扁平 options 属性/统计段/摘要）各带各的，冲突只在共享文件追加行；**不翻转/注册 stub**；record 索引器不在 with 面（with 语法不支持索引器，E11 RS-19 已在声明级排除） |
| **C24 InitializerDesugar（P3，设计未落地）** | required 与对象/with 初始化器的归一顺序 | 本设计 §2 决策 12 即给 C24 的前置契约：required/with 在 Declarative 先归一，C24 在 Expression 阶段只处理一般 `new C{…}`/集合初始化器且对本族产物幂等；C24 设计评审时引用本条 |
| **C21 anonymous-type-lift（stub）** | 匿名类型 with v1 跳过（CS2J5225） | C21 提升匿名类型为命名 record-like 类型后，其 with 自动落入 WR-1/WR-4；C27 不提前改 C21，跳过码随 C21 落地退役 |
| **C46 签名收口（同 catalogItems）** | 诊断码段 5190-5199 建议归 C46；克隆工厂是新虚方法/覆写，属 C46 Java 分派收口的输入面 | C27 占 52xx（§6），码位表在族共享码位枚举统一登记；工厂保留 abstract/virtual/override 原生 C# 形态，不造 Java 注解；C46 落地时消费普通方法 |
| **C39 终结器（同 catalogItems）** | 无功能交叉 | 码位顺延（5231+ 或族 owner 另定）；仅共用 Analyzer 外壳 |
| **C08 JavaKeywords（链尾，默认关）** | 新方法名 `Cs2jWithClone` 与 reshape 后成员名的 Java 保留字风险 | 由 JK-1 符号改名链尾兜底；C27 不内联关键字逃逸（BackingFieldNaming 复用除外） |
| 公共热文件 | MemberDesugarPlan.cs（`MemberDesugarKind` 枚举一行，非 MemberDesugarRules.cs）、族 options/统计聚合、CatalogItems 字符串 | C27 改动面压到「一行枚举 + 独占目录 + 合入时一行 CatalogItems」；合并门禁 = rebase 最新主干 + 注册表校验/链序断言全绿；不重排他规则顺序 |

---

## 12. Worktree 分配（2026-10-03 更新 base，与 C50 共用同一 worktree 进入实施）

- 分支：`feature/preproc-c27-c50-record-with-field-keyword`（**C50 field-keyword 与本特性 C27 共用同一分支/worktree**：两 preprocessor 在同一 worktree 内同批实施、按 §9 里程碑各自分别提交；实施序为先 C50（建立 Properties 前置 normalizer 钩子）后 C27（新增 Records 规则 + Properties 归属挂点），降低同改 PropertyRule 的冲突）
- 目录：`.worktrees/preproc-c27-c50-record-with-field-keyword`
- base：**`840010e2`**（`docs: C46 设计文档 worktree base 前移至 dev-3.x HEAD 4ea50d44`，dev-3.x HEAD @ 2026-10-03，完整 SHA `840010e2c1d6311424c89e8c597f5147abec2fbc`，已 `git rev-parse --verify` 校验存在；worktree 切出后基线 `dotnet build CSharpToJavaConverter.slnx` 退出码 0）。~~原校订 base 锚点 `953293b9`（`docs: update doc base commit`）/ dev-3.x `ef70e5a2`~~（2026-10-03 前移；原校订「实际开工 base 直接取开工当日主干 HEAD」即落在此值，无需再等锚点合入或先行 rebase）。
- 创建命令（在仓库根、PowerShell 下执行；2026-10-03 实施时已实际执行）：

```powershell
git worktree add .worktrees/preproc-c27-c50-record-with-field-keyword -b feature/preproc-c27-c50-record-with-field-keyword dev-3.x
```

- **前置现状（2026-10-03 基线核实）**：
  1. **族 pass 已默认开**：member-desugar 为 Implemented 且 **defaultEnabled=true**（`src/CSharpToJava.CLI/Preprocessing/PassCatalog.cs:355-365`，Stage=Declarative，RunAfter=`{struct-value-semantics, closure-lift}`，目录 `.cs2j-member-desugar-src`），catalogItems 真值 **`"C11, C12, C13, C14, C39"`**（C39 已合入；C46 尚在其独立 worktree、枚举位 `Signature` 已预留但规则未在盘）。本特性合入时在该真值上追加 `C27`；与同分支 C50 一并落地后的终值为 `"C11, C12, C13, C14, C39, C50, C27"`（最终顺序以合入时 PassCatalog 真值为准，**勿回退 C12–C14/C39**）。
  2. `MemberDesugarKind` 枚举现值 `Property, Indexer, Event, Operator, Signature(C46 预留), Finalizer(C39, 置最后)`（`src/CSharpToJava.Core/MemberDesugar/MemberDesugarPlan.cs:6-14`）；规则发现排序为 `OrderBy(Kind).ThenBy(Order)`（`MemberDesugarRules.cs:32`）。本任务按 §3.2-1 追加 `Record`；Records「声明 reshape 先于 Properties、使用点 WithExpressionRewriter 先于 Properties 使用点」的次序，以「reshape 直产终形字段+方法、不产新 PropertyDeclarationSyntax」＋开放问题 6 的 plan 归属互斥挂点共同保证，不靠文件遍历巧合；`Record` 的枚举插入位与规则 Order 在实现时以不扰动 Finalizer「置最后」语义确定。
  3. `MemberDesugar/Records/` 目录尚不存在（本任务新建）；Properties 三处 CS2J5123/5124 闸门挂点仍在 `PropertyRule.cs`（开放问题 6 由本任务以最小共享面钩子收口，候选①kind 查询钩子优先）。
  4. **默认开口径变化（不抢跑）**：§8 门槛（尤其 §5.4 Java 半区第 1 项「复制构造 Java 发射」与第 3 项「with 旧路径不可达」两项硬门禁）未完成前，Records 规则不得以默认行为改写输出。实施时仿 C39 `rules.finalizers`（`Program.cs:3321`）与 C46 `rules.signatures.enabled` 子集闸门口子，为 Records 加一个默认 **false** 的子集开关（`rules.records` 形态，Program.cs 配置透传到 `MemberDesugarOptions`，**不新增 pass/适配器/verb/描述符**）；关闭时 Records 零执行零 diff，显式启用才参与分析/改写，§8 门槛通过后再翻默认值。

实施约束（交实施 worktree）：仅在上述（与 C50 共用的）worktree 内改码；按 §9 里程碑逐提交；不注册/翻转任何 stub、不动 RunAfterIds、不新增 pass/适配器/verb；共享文件仅 `MemberDesugarPlan.cs` 枚举一行 + Properties 归属挂点（开放问题 6）+ 子集开关透传，注册表仅 CatalogItems 一行；转换器代码改动（§5.4）留后续 PR；族 pass 已默认开，Records 子集默认关、不抢跑（前置现状 4）；交付面遵循 foundation plan「并行开发交付包模板」（[preprocessor-foundation_plan.md L108-L117](file:///d:/code/cs2j/.trae/documents/preprocessor-foundation_plan.md#L108-L117)）：Core `MemberDesugar/Records/`、Core 单测（`tests/CSharpToJava.ProjectTests/MemberDesugarRecordWithUnitTests.cs`）+ fixture（`Records*` 前缀，并入已存在的族共享 sln `tests/SampleMemberDesugarSolution`）+ Release build/test + residual 零 with 闸门 + CLI 测试、本设计与 plan 文档（`docs/superpowers/plans/2026-09-29-c27-record-with.md`）；不新建支撑工程。

---

## 附：开放问题裁决（2026-09-29 拍板）

> **拍板目标**：支持全部 C# 语法/语义、便利下游 C#→Java 转换。统一口径：① 每种合法 C# 构造必须有语义等价的可编译 Java 终态，v1 可显式诊断门控但必须登记终态去向，禁止长期静默丢弃；② 中间树始终是普通合法 C#，Java 侧差异集中到 compat/映射层收口；③ 语义精确优先于分配/性能，优化变体须有画像证据；④ 预处理默认开前转换器旧臂由独立 PR 退役、不留双轨窗口，CheckPass 安全网永久保留；⑤ 合成类型名 `Cs2j<Role>_<Member>_<n>`、合成成员/临时 `_cs2j…`、让位改名 `<Name>_cs2j<Purpose>`（**单下划线后缀**）；⑥ 跨 pass 顺序依赖一律显式 RunAfter 固化；⑦ Implemented+默认关观察期不超过两个里程碑窗口；⑧ worktree 创建命令维持 base `953293b9`，创建后第一步 rebase 到当时 dev-3.x HEAD（本裁决时为 `ef70e5a2`），stub 计数红灯由首个锚点翻转 PR 按真值收口。**（校订注：该收口已完成——member-desugar 已翻转为 Implemented，PassStubCatalog 现为 21 个 stub；两 commit 均核实存在，见 §12。）【2026-10-03 实施更新：base 前移至 dev-3.x HEAD `840010e2`，并与 C50 共用同一 worktree/分支（名见 §12），命令见 §12 且已执行；member-desugar 已 **defaultEnabled=true**、catalogItems 真值 `"C11, C12, C13, C14, C39"`，本特性合入追加 `C27`；枚举现值含 Signature/Finalizer 而无 Record；族 pass 既已默认开，Records 在 §8（含 §5.4 Java 硬门禁）达标前以默认 **false** 的 `rules.records` 子集开关承接「零执行零 diff」，不抢跑，详见 §12 前置现状】**

**裁决（按原题号）：**

1. **保留 `record` 关键字**：编译器继续保 Equals/PrintMembers，只补 ctor/Deconstruct，reshape 面最小；不降为普通 class。
2. **树外派生 record 多态闸门**：M2 实现同编译 + 引用程序集派生类型全枚举（全编译符号 API 可支撑，不接受「非密封即跳过」的过度保守）；仅枚举后仍不能证明闭包时 CS2J5221 Warning + force 范式。
3. **主构造参数捕获（决策 8c）**：捕获迁移**提前到 M2**（位置 record 在字段初始化器/体内引用主构造参数是高频形态，不等 M4+）。
4. **码段与命名**：占 CS2J5200-5230（避 C46 的 5190-5199；磁盘现状 5184-5199 空闲，见 §6），以族共享码位枚举统一登记为准；克隆基名 `Cs2jWithClone`、防撞后缀按全局命名规则用**单下划线** `WithXxx_cs2j`——E11 现用的 `WithXxx__cs2j` 双下划线形态在族命名收口 PR（默认开前）统一改名，C27/C46 不各自发明词表。
5. **Java 半区复制构造**：M2 即出转换器消费原型，v1 逐字段构造（不复用未来统一 copy 机制）；转换器消费 PR 合入是本特性**默认开的头号硬门禁**，但不阻塞 C# 侧实现与默认关先行（stub 翻转已由 C11 完成）。

---

### 原问题记录（裁决前）

1. **位置 record reshape 是否保留 `record` 关键字**：本设计推荐保留（编译器继续保 Equals/PrintMembers，只补 ctor/Deconstruct，reshape 面最小）；备选是降为普通 class（连等值面一起显式化，工作量与风险均大，且与转换器现有 immutable-class 路径重复）。
2. **树外派生 record 的多态闸门强度**：CS2J5221 v1 对「非密封且不能证明闭包」即跳过是否过保守；是否值得做跨引用程序集的派生类型全枚举（编译成本），还是接受 warning + force 范式。
3. **主构造参数捕获（决策 8c）v1 跳过的命中率与排期**：若 sdk 中位置 record 大量在字段初始化器/体内引用主构造参数，M2 覆盖率会受限，可能需把捕获迁移提前到 M2 而非 M4+。
4. **CS2J52xx 码段与克隆工厂定名**：5200-5230 段位需 C11 锚点在统一码位表确认（避开 C46 的 5190-5199）；`Cs2jWithClone` 基名与 `__cs2j` 防撞后缀是否符合族命名惯例（E11 现用 `WithXxx__cs2j`）。
5. **Java 半区复制构造的目标形态**：§5.4-1 要求转换器为 immutable-class record 发 Java 复制构造——是逐字段构造还是复用未来的统一 copy 机制，需转换器消费 PR 早做原型，因其是本特性默认开的头号硬门禁。

### 校订新增开放问题（2026-09-29 核对时登记）

6. **Properties 三处闸门归属挂点的认领方式（待与 C11 维护者协调）**：CS2J5123/5124 退役要求把 record/struct 位置属性从 Properties 规则改由 Records 规则认领，但三处闸门挂点都在共享文件 [PropertyRule.cs L64-L69](file:///d:/code/cs2j/src/CSharpToJava.Core/MemberDesugar/Properties/PropertyRule.cs#L64-L69)、[L257-L259](file:///d:/code/cs2j/src/CSharpToJava.Core/MemberDesugar/Properties/PropertyRule.cs#L257-L259)、[L287-L293](file:///d:/code/cs2j/src/CSharpToJava.Core/MemberDesugar/Properties/PropertyRule.cs#L287-L293) 内，与 C27「独占 `MemberDesugar/Records/` 目录」原则冲突。候选：① Properties 三处加 kind 查询钩子（最小共享面，推荐）；② 站点互斥表从 RecordRule 反向回调；③ 直接在共享文件加 `kind == Record` 分支（最脏）。M0 需与 C11 维护者拍板挂点形式并锁定接口；在拍板前 C27 不擅自改共享文件。
7. **CatalogItems 补登（实施期机械动作，非设计阻塞）**：member-desugar [PassCatalog.cs L241](file:///d:/code/cs2j/src/CSharpToJava.Core/Passes/PassCatalog.cs#L241-L241) 现仅登记 `"C11"`（C12-C14 已实现但均未追加，属族内既有欠账）；C27 合入时应把 `C27` 追加进同一字符串，是否顺带补 C12-C14 由族 owner 决定。

---

## 13. M4 画像实施记录（2026-09-29 拍板口径，2026-10-09 数据落盘）

> 工具：`tools/PreprocessCorpusProbe`（画像 id `c27-c50-ip`，提交 `013ab5e8`；in-proc 仅调 public API `MemberDesugarPreprocessor.RewriteFile`，逐 msbuild 工程一个 session 共享分析器，按 `-getItem:Compile` 干净编译项分析，**无 CLI 目录枚举合并、无整树 accept/rollback 循环**——CLI 口径下接受/回滚会把族摘要冲成全 0，无法取得诚实 plan/skip 计数，故画像必须走 in-proc，详见 13.5）。口径：non-strict（Warning/Error 全计）、`Records.Enabled=true` + `FieldKeyword.Enabled=true`（子集开关实测默认仍为 false，见 13.4）；共享链接源在各 csproj 下重复计入；所有计数为实测值，无估计。原始报表（gitignored）：`probe-out/profiles/c27c50-sdk/c27-c50-ip.{json,md}`、`probe-out/profiles/c27c50-ef/c27-c50-ip.{json,md}`、`probe-out/profiles/c27c50-yaml/c27-c50-ip.{json,md}`、`probe-out/profiles/c27c50-runtime/c27-c50-ip.{json,md}`。

### 13.1 语料版本与规模

| 语料 | 版本/路径 | csproj（不可用） | 编译项 | 改写文件（全族并集） |
|---|---|---:|---:|---:|
| csharp-sdk | `D:\csharp-sdk` 四库（json/uri/xml-tiny/regular-expressions） | 21（0） | 1457 | 792 |
| EF Core | `D:\efcore-10.0.12` 代表子集 6 csproj（与 C17 §13.1 同口径，全路径过滤） | 6（2） | 2141 | 1458 |
| YamlDotNet | `D:\YamlDotNet-18.0.0` | 8（5） | 17 | 10 |
| runtime | `D:\runtime-10.0.12` 4 个代表性产品工程采样 | 4（4） | 0 | 数据未取得（见 13.6） |

不可用原因与 C17 §13.1 完全同形（环境项，与规则无关）：EF 侧 EFCore.Sqlite.Core **NETSDK1004**（artifacts/obj 缺 project.assets.json）、Microsoft.Data.Sqlite.Core **MSB4057**（无 ResolveAssemblyReferences 目标）；YamlDotNet 5 项同为未 restore/无目标（含主库 YamlDotNet.csproj MSB4057），有效编译项仅来自 YamlDotNet.Analyzers.StaticGenerator（13）与两个 AoTCompileTest 工程（2+2）。

### 13.2 Records/with 命中矩阵（全部实测）

| 桶 | sdk | EF Core | YamlDotNet |
|---|---:|---:|---:|
| RecordPlans | 0 | 9（其中 RecordClassPlans=4） | 0 |
| PositionalRecordsReshaped | 0 | 0 | 0 |
| CloneFactoriesSynthesized | 0 | 0 | 0 |
| WithSitesTotal / Class / Struct / Empty | 0 / 0 / 0 / 0 | 0 / 0 / 0 / 0 | 0 / 0 / 0 / 0 |
| PropertyAssignments / FieldAssignments / TemporariesPromoted | 0 / 0 / 0 | 0 / 0 / 0 | 0 / 0 / 0 |
| ResidualWithCount（FK/WR residual） | 0 | 0 | 0 |
| InitAccessorsWidened（C11 面，CS2J5120） | 15 | 260 | 0 |
| RequiredConstraintsDropped（CS2J5215） | 0 | 0 | 0 |
| CS2J5123 PositionalRecordProperty（C11 让位 Records 挂点） | 4（json-tests） | 41（EFCore 21/Relational 14/SqlServer 6） | 0 |

产品语料自然形态下**没有任何一个树内 record 的 with 站点进入可改写态**：EF 虽建了 9 个 record plan（4 个 record class），但全部 with 使用的接收者/上下文落在 13.3 的既有闸门；E11（with 的主要生产方）不在这三语料内。sdk 侧本族 5212-5230 段诊断零命中——报表里 sdk 的 CS2J5200×2/5202×2/5206×2 经核对是 **C39 终结器**命中（FinalizerRejected/FinalizerDisposePattern/FinalizerGcLifecycleSite；实现码位上 C39 占 5200-5211，C27 实际占 5212-5230，与本文 §6 提案段错位 12，以 `MemberDesugarModels.cs` 枚举登记为准，此处勘误）。

### 13.3 CS2J5220-5228 with 跳过逐条人工定性（§8 门槛 4）

EF 合计 105 条 with 相关命中（5124 配套挂点计入），全部人工定性为设计既定闸门，**无一条「语义可行却被分析器过保守跳过」**：

| 码 | 计数（分布） | 逐条定性 |
|---|---:|---|
| CS2J5222 MetadataRecordReceiver | 40（Relational 39/SqlServer 1） | 接收者为引用元数据里声明的 record（样例 `CSharpRuntimeAnnotationCodeGeneratorParameters`，声明在 EFCore.Design 程序集），树内无法补发克隆工厂——§决策 4/诊断表既定 v1 形态，真残余；终态需跨程序集克隆策略，归 M4+ |
| CS2J5228 NonRecordClassWith | 25（Relational 21/SqlServer 4） | 元数据 **record struct** 接收者（`CoreTypeMappingParameters`/`TypeMappingInfo`），树外声明且无 set_X 访问器可调；与 5222 同类，真残余 |
| CS2J5223 NonLiftableWithContext | 22（全在 EFCore） | 全部位于 `ExpressionTreeFuncletizer.PathNode` 的字段/属性初始化器等无语句锚点上下文，临时提升无安全发射点——§6 表既定不可行（对应开放问题 CS2J5223），真残余 |
| CS2J5220 PositionalReshapeGate | 11（EFCore 7/Relational 4） | **readonly struct 非空 with**：克隆局部仍是只读值类型的只读成员（CS8340/CS8341），set_X/字段直写无合法发射面（`RecordClaimIndex.cs:444-451`）；样例 `ColumnValueSetter`、`RelationalTypeMappingInfo`；空 with 纯值拷贝不受限（本轮零空 with 命中）。语言事实闸门，非过保守 |
| CS2J5124 WithSite（C11↔C27 配套挂点） | 7（EFCore 4/Relational 3） | with 初始化器引用的属性属于 5222/5228 无法补工厂的 record（样例 `ElementTypeMapping`、`TypeMapping`），C11 属性 plan 据此原子保留；是上三类的**配套原子性诊断**，不是独立缺口 |

未命中码：5221（树外派生风险）、5224（嵌套初始化器）、5225（匿名类型）、5226（表达式树 with）、5227（dynamic）、5230（residual 零 with 闸门）三语料均 **0**。`SkippedByReason` 实测键与诊断一一对应（PositionalReshapeGate 11/MetadataRecordReceiver 40/NonLiftableWithContext 22/NonRecordClassWith 25/WithSite 7）；ResidualWithCount=0 且无 5230，§7.5「输出残留 with 必须有诊断归属」在全部已测语料满足。

### 13.4 §5.4 Java 半区核查（只核查，不改转换器）

2026-10-09 对主干转换器源码逐项核查（本特性不实施 Java 改动）：

1. **复制构造 Java 发射（硬门禁）：未实施**。[RecordTransformer.cs:223-246](file:///d:/code/cs2j/.worktrees/p3-profiles/src/CSharpToJava.Core/Transformers/Type/RecordTransformer.cs#L223-L246) 仍只为 record 发一个主构造（含 `super(...)`），全文件无 `protected R(R original)` 及 `: base(original)` 链发射。
2. 克隆工厂识别：`Transformers/` 全目录 grep 无 `Cs2jWithClone` 特判（普通方法经 MethodTransformer 发射的假设成立但无端到端验证），阻塞于第 1 项。
3. **with 旧路径不可达（硬门禁）：未满足**。[ControlFlowTransformer.cs:54](file:///d:/code/cs2j/.worktrees/p3-profiles/src/CSharpToJava.Core/Transformers/Expression/Transformers/ControlFlowTransformer.cs#L54) 仍分派 `TransformWithExpression`（[L633](file:///d:/code/cs2j/.worktrees/p3-profiles/src/CSharpToJava.Core/Transformers/Expression/Transformers/ControlFlowTransformer.cs#L633)，旧「new + clone + setter」臂），直连红灯路径仍可达；旧臂删除是后续独立 PR。
4. reshape 后 record 形态路由、5. required/SetsRequiredMembers 注解清扫（`Transformers/` 零 `SetsRequiredMembers` 引用；本轮 RequiredConstraintsDropped=0）、6. 浅拷贝双端测试：均未做端到端核查/落地，随第 1 项的转换器消费 PR 完成。

**拍板（M4）**：§8 门槛 3 的两个硬门禁（§5.4-1 复制构造发射、§5.4-3 旧 with 路径不可达）当前不满足；§8 门槛 4 的三语料 with 分桶+逐条定性由 13.2/13.3 满足，但产品语料零可改写站点意味着 Java 侧即便补齐也缺真实链路样本（样本只能来自 E11 fixture）。**`rules.records` 维持默认 false（不抢跑口径不变，`RecordOptions` 默认值核实仍 false）；本次不翻默认、不改转换器一行**。CatalogItems 机械动作已由早前提交完成：[PassCatalog.cs:549](file:///d:/code/cs2j/.worktrees/p3-profiles/src/CSharpToJava.CLI/Preprocessing/PassCatalog.cs#L549) 现值 `"C11, C12, C13, C14, C39, C46, C50, C27"`。

### 13.5 CLI 整树口径两条 CS2J5100 残余（登记后续，M4 不改 CLI 树准备/不改转换器）

画像取证同时闭环了 CLI 路径的两个 5100（证据日志在 gitignored `probe-out/profiles/cli/`）：

1. **镜像被目录枚举合入同一编译（CLI 树准备口径，后续项）**：`repro-json2` 重跑 trace 显示 json `tests/TrimmingTests`（`EnableDefaultCompileItems=false`，本应每文件独立成程序）的镜像类型 `ClassWithDay` 同见于 EnumConverterTest.cs:21 与 JsonConverterAttributeTest.cs:23，被 `ProjectPreprocessSource` 按目录递归枚举合入同一 Roslyn 编译，属性去糖两侧 `get_Day` 撞 CS0082，叠加缺引用级联（newErrors iter0 1688→iter2 1243，其中可归属单 plan 回退 5101 计 220）；最终残余 **1×CS2J5100** [JsonConverterAttributeTest.cs(26,32) CS0082 `ClassWithDay.get_Day`]。提交 `f40cee3c` 已做族内**最小收编**：CS0082 报点落在 get/set 访问器槽位、`GetNameString()` 对 AccessorDeclaration 返回 null 时补登记所属属性/事件声明名（既有 xUnit `Phantom_Mirror_Types_Outside_CompileItems_Do_Not_Trigger_WholeTree_Rollback` 覆盖）；剩余 5100 的根因是 CLI 编译项口径（应对齐 msbuild Compile 项而非目录枚举），按任务纪律**不在本族大改 CLI**，登记为后续工作项。trace 临时埋点已全部移除（`src/` 零 `CS2J_MD_TRACE`），适配器已还原至已提交版，测试/正常路径不吐 trace；带 trace 的旧日志仅作为历史证据留在 gitignored 目录。
2. **YamlDotNet `OrderedDictionary<TKey,TValue>`（真实 pass 缺口，与树合并无关）**：`single-yaml` 单产品工程重跑仍 1×CS2J5100（CS0470：去糖出的普通方法 `get_Item(TKey)` 不能实现元数据接口访问器 `IDictionary<TKey,TValue>.this[TKey].get`，伴 CS0535）+ 41×CS2J5101 + 1×CS2J5103；正确去糖需显式接口实现，且重载索引器 `this[TKey]`/`this[int]` 同产 `get_Item`、当前 `IndexerPlan.OwnsTouch` 不辨签名致归属命中 2 plan 而放弃。该轮 262 文件 transformed 但族摘要被回滚冲成全 0——这正是 13.1 工具段所述 in-proc 画像必要性的直接证据。两项修复（显式接口实现发射 + 签名敏感 plan 归属）超 M4 最小范围，登记后续项。

画像还暴露 1 个 C11 面分析器缺陷（非本族、本轮不修）：EF `src/EFCore/Metadata/Conventions/InversePropertyAttributeConvention.cs` 触发 `PropertyUseSiteRewriter.VisitPostfixUnaryExpression`（[PropertyUseSiteRewriter.cs:409](file:///d:/code/cs2j/.worktrees/p3-profiles/src/CSharpToJava.Core/MemberDesugar/Properties/PropertyUseSiteRewriter.cs#L409)）`InvalidCastException`（null 条件绑定 + 后缀形态：InvocationExpressionSyntax 强转 SimpleNameSyntax），probe 按文件记 driverErrors=1、该文件跳过改写；登记为 C11 后续缺陷，复现命令与完整堆栈见汇总报告。

### 13.6 未取得数据声明

- **runtime-10.0.12 全部不可得**：全仓 `artifacts\obj` 下 project.assets.json 数为 **0**；采样 System.Text.Json、System.Collections.Immutable、Microsoft.Extensions.DependencyInjection、Microsoft.Extensions.Caching.Memory 4 个产品工程全部 MSB4057（无 ResolveAssemblyReferences 目标；restore 会写入只读语料，纪律禁止）。record/with 在 runtime Collections/Text 等高频产品库的真实频率本轮无法测量，列入下一评审周期复跑项（需在允许写入的环境 restore 后执行同一画像命令）。
- YamlDotNet 主库等 5 工程未 restore/无目标，17 有效编译项集中在 analyzer/AoT 三工程；其 0 record/0 with 不代表整个库使用面。

### 13.7 M4 结论

§8 第 4 项（语料 triage）在可测语料上闭环：with 命中 105 条全部为设计已知闸门、residual 闸门为 0、过保守项为 0；§8 第 3 项（Java 半区）硬门禁未达标。C27 继续随族 **opt-in（rules.records 默认 false）**；后续路线固定为：① §5.4-1/5.4-3 转换器消费 PR（另开）；② CLI Compile 项口径修复后复跑 CLI 全树画像；③ runtime restore 后补 Collections/Text 扫描；④ 5221/5224-5227 在更多语料复跑时持续观测（本轮 0 命中不降低闸门）。
