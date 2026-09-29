# C05 Enum 声明归一预处理器设计（[Flags]/底层类型/显式值；enum-as-class 外显契约）

**日期**: 2026-09-28
**范围**: 新增注册表 pass `enum-declaration`（Core `EnumDeclaration/` + CLI `EnumDeclarationPassAdapter`），只做 C#→C# 预处理；本特性不修改 C#→Java 转换器与 Java compat 代码（桥接面重定义仅定契约，落地为后续工作项）
**状态**: 设计稿，待评审
**注册表锚点**: stub `enum-declaration` 已登记于 [PassStubCatalog.cs](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassStubCatalog.cs#L82-L90)（catalogItems `C05`，RunAfter=`enum`，默认关）
**catalog 对应**: `prompts/preprocessor-catalog.md` C05（L309-321）、§3.3 enum 族（L914）、§4 风险 4（L923）、§4 风险 12（L931）、优先级 P1（L51）
**前置设计**: [2026-09-26-enum-preprocessor-design.md](file:///d:/code/cs2j/docs/superpowers/specs/2026-09-26-enum-preprocessor-design.md)（E04，使用点侧，已实现）
**格式范本**: [2026-09-28-java-keywords-preprocessor-design.md](file:///d:/code/cs2j/docs/superpowers/specs/2026-09-28-java-keywords-preprocessor-design.md)

---

## 1. 背景与目标

### 1.1 声明侧缺口证据（已复核）

E04（pass id `enum`，[PassCatalog.cs](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassCatalog.cs#L66-L76)）已实现并默认开启，但它**只做使用点表达式**：位运算 `| & ^ ~`、复合赋值、`HasFlag`、可空 lifted 全部降级为「`(U)` 底层整数运算 + `(E)` 强转」（[EnumSyntaxRewriter.cs](file:///d:/code/cs2j/src/CSharpToJava.Core/EnumPreprocessor/EnumSyntaxRewriter.cs)，E04 设计 §3）。E04 的非目标明确写着「不重写 `Enum.Parse/IsDefined/GetValues/GetNames/Format/ToString`」（E04 设计 §4.10/§11），且**完全不触碰 enum 声明**。

声明侧现状（2026-09-28 复核）：

1. **三类声明 100% 转换失败**。主转换器唯一的 enum 入口 [EnumTransformer.cs](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Type/EnumTransformer.cs#L25-L58) 对三种形态直接 `throw NotSupportedException("CS2J normalization required: …")`：
   - `[Flags]`（L29-33）；
   - 显式底层类型（`enum E : byte` 的 BaseList，L35-39）；
   - 任意成员带显式值（L41-45，含重复值/跳跃值/自动递增接续）。
   异常在 [TypeGroupResolver.cs](file:///d:/code/cs2j/src/CSharpToJava.Core/Pipeline/TypeGroupResolver.cs#L640-L643) 被吞为 Warning 并返回 `null`——**类型文件被静默丢弃**，使用点仍产出半截 Java，属 catalog §4 风险 12 的同族「静默错误输出」。
2. **唯一能直转的形态（默认 int、无显式值、无 [Flags]）建立在 ordinal 假设上**，显式值即使不抛也是错的语义：
   - `(E)i → E.values()[i]`、`(int)e → e.ordinal()`、跨 enum 强转 `target.values()[e.ordinal()]`（[TypeOperationTransformer.cs](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/TypeOperationTransformer.cs#L60-L76)）；
   - `default(E) → E.values()[0]`（[TypeOperationTransformer.cs](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/TypeOperationTransformer.cs#L152)、[FieldTransformer.cs](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Member/FieldTransformer.cs#L220-L226)）——C# 语义是**数值 0**，与「第一个声明成员」并不等价（无 0 值成员时错误）；
   - const enum 字段按 ordinal 下标回填（[FieldTransformer.cs](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Member/FieldTransformer.cs#L316-L321)）；
   - enum governing 的 switch 不在 fail-fast 清单内（[StatementTransformer.SwitchAndResource.cs](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Statement/StatementTransformer.SwitchAndResource.cs#L28-L42) 只拒绝 bool/long/ulong/float/double/decimal），原样落为 Java enum switch，隐式依赖 ordinal 与声明序号一致。
3. **测试与实现已长期脱节**。`EnumTransformerTests` 断言的是一版已被删除的富实现（`Open(10)` 构造、`private final int value;`、`getValue()`、`fromValue(int)`、`_UNMAPPED` 可变单例、flags 常量类）。实测（2026-09-28，`dotnet test -c Release --filter EnumTransformerTests`）：**41 失败 / 2 通过 / 43 总计**；enum/flags 全过滤器（Tests 工程）：**77 失败 / 74 通过 / 1 跳过 / 152 总计**，红灯集中在 `EnumTransformerTests`（41）、`EnumExtremeValueTests`（4）、`EnumConditionalExpressionTests`（3）、`EnumPropertyDefaultTests`（3）、`FlagsEnumInterfacePropertyTests`（2）、`ByteEnumArithmeticWrapTests`（2）、`FlagsEnumToStringTests`（2）、`EnumValueMemberCollisionTests`（1）、`StructEnumFieldDefaultTests`（1）等。同一次实测中 **E04 单测 29/29 全绿**——「使用点已做、声明缺失」的两半区状态被测试结果直接证实。
4. Java 侧桥接厚但假设已锁死在「Java enum」形态：[EnumHelper.java](file:///d:/code/cs2j/java/csharptojava-compat/src/main/java/io/github/ningpp/compat/EnumHelper.java) 全部方法以 `Class.isEnum()`/`getEnumConstants()`/`Enum.name()`/ordinal 为前提（L5-16、L56-93、L95-146、L217-248），并反射寻找转换器生成的 `getValue()`/`fromValue()`（L164-248）、按常量值域反推底层位宽（L280-397）、过滤合成 `_UNMAPPED`（L148-150）；`format` 的 `D/X` 直接用 ordinal（L399-427），对显式值枚举语义错误。`System.Enum` 六个静态方法经 [TypeMappings.json](file:///d:/code/cs2j/config/TypeMappings.json#L6576-L6604) 映射到 `EnumHelper.tryParse/parse/getValues/getNames/format/toObject`。这正是 catalog §4 风险 4 指出的「compat `EnumHelper` 虽厚但仍是 Java enum 假设，前置归一后需要同步重定义桥接面」。

### 1.2 频率

C05 为 catalog P1（[L51](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L51)）。catalog 未登记 enum 声明三族在训练集 D:\csharp-sdk 的具体命中数（条目内无 sdk 计数，与 C04/C23 等带计数条目不同）；本设计不杜撰数字。**频率补扫是默认开启门槛之一**（§8）：在 sdk 基线上分别统计 `[Flags]`、非 int 底层类型、带显式值 enum 声明数，以及 `System.Enum` 反射 API/switch/字典键/enum 泛型实参的使用点密度，作为范围与优先级复核输入。

### 1.3 与 E04 的分工

| 维度 | E04 `enum`（已实现） | C05 `enum-declaration`（本设计） |
|---|---|---|
| 侧别 | **使用点**：enum 类型上的运算符与 `HasFlag` 表达式 | **声明侧**：enum 声明归一 + 因表示形态变化而必须跟进的全部使用点 |
| 输入/输出语法形态 | 输入输出都是真 C# `enum` 语义，类型仍是 enum | 输出中目标 enum 声明变为「带整数值的 enum-like 值类型」，该类型不再是 `TypeKind.Enum` |
| 改写粒度 | 逐文件 rewriter（dirty 超集 + 语义过滤） | **整树** Analyzer→Plan→Rewriter（声明与跨文件引用必须同生共死） |
| 中间目录 | `.cs2j-enum-src` | `.cs2j-enum-declaration-src` |
| 链位 | `null-coalesce` 之后 | `enum` 之后（本 pass 消费 E04 产物，见 §3） |
| `(E)`/`(U)` 强转 | 产物里大量保留 | C05 把针对**被改写类型**的强转改写成显式成员调用（§2 D7），使新形态自洽可编译 |

两半合起来覆盖 catalog §3.3 enum 族「E04 + C05 + C17 + C22，同一 EnumSyntaxRewriter 扩展，不新建通道」（[L914](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L914)）。注册表基础建设已把 C05 登记为**独立 id**（stub `enum-declaration`，foundation plan L84）而非并入 E04：本设计遵循注册表现状——**独立 pass、独立 Core 命名空间、共享 E04 已验证的事实逻辑**（底层类型八分类、窄类型 `unchecked`、可空解包等），不回填合并通道（理由见 §3.2）。

### 1.4 目标与非目标

**目标**：

1. 把三类「100% 失败」的 enum 声明（`[Flags]`、非 int 底层类型、显式/重复/跳跃值）归一为**带显式整数值与显式成员集合的 enum-like 值类型**（形态决策见 D1），产物是可编译 C#。
2. 建立 **enum-as-class 外显契约**：值访问、值构造、名字/值双向解析、枚举集合、格式化、标志判定全部是类型上**外显的普通成员**（D6），并把对应 `System.Enum` 反射式调用点在整树范围内改写到这些成员（D8）——不再依赖 ordinal、`_UNMAPPED` 单例与运行时反射。
3. 因表示形态变化而失效的语法（switch/常量模式）在同一 plan 内降级为值比较 if/三元链，保证中间树逐环可编译。
4. 不可表示的 enum 用法（`where T : struct, Enum`、运行时 `System.Type` 的反射 API、表达式树/动态场景等）**显式诊断报错**，禁止 TODO/占位/原样透传（对齐 catalog §4 风险 12 第一原则）。
5. Stub→Implemented 翻转，默认关起步，按 §8 门槛验收后默认开。

**非目标**：

- 不改 E04 任何已实现行为与测试；不做 `++/--`（C17）、不做可空 enum 的非位运算提升（C22）、不做 pattern/关系模式去糖（C03/C37）——边界见 §11。
- 本特性不修改转换器（`Transformers/`、`TypeMappings.json`）与 Java compat（`EnumHelper.java` 等）；桥接面重定义在本设计中只产出**契约与后续工作项**（D11），转换器能吃下新形态由验收测试证明，吃不下的站点由诊断拦截。
- 不做二进制兼容；不处理 TypeScript 路径。
- 不追求首期全量归一：默认 int、顺序赋值、无显式值的普通 enum 首期保留（D2）。

---

## 2. 关键设计决策表

> 标注「**待 M1 原型**」的条目是开放问题，须在 M1（见 §9）用最小原型 + 中间树编译证伪/证实后再锁死。

| # | 决策点 | 结论 | 依据与备注 |
|---|---|---|---|
| D1 | 目标表示形态 | **enum-like value type：C# `readonly struct`（转换器随后映射为带 `static final` 实例 + U 值字段的 Java final class）**。否决「int 常量类 + 全树 `E→U` 类型替换」 | 见 §2.1。struct 而非 sealed class：默认值/数组零填充/`E?`/`Dictionary<E,V>` 键/`: struct` 约束全部天然保持 C# 语义。sealed class 形态（引用相等、默认 null、需手填数组）列为备选，**待 M1 原型**比对 StructTransformer 对合成 struct 的转换质量后定 |
| D2 | 首期范围 | **ProblemOnly**：只归一命中至少一条的 enum——①声明 `[Flags]`（符号+语法双判，含别名/`System.FlagsAttribute`，沿用 [EnumTransformer.IsFlagsEnum](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Type/EnumTransformer.cs#L60-L72) 判定口径）；②U 非 `int`；③任一成员带 EqualsValue；④成员值重复。普通顺序 int enum 保留真 enum，走现有 Java enum 直转 | 控制爆炸半径；`All`（全量归一，彻底消灭双形态）作为 scope 选项与后续里程碑，默认关。双形态混装的代价（跨形态强转）见 §2.2 与风险 R3 |
| D3 | 底层类型 | **忠实保留 U 八种整数**（`byte/sbyte/short/ushort/int/uint/long/ulong`），值字段/参数/返回一律用 U；不统一拉成 int | 拉平会丢窄类型截断与位宽（`byte` 枚举 256 个值、`X2/X4/X8/X16` 格式宽度、无符号比较）；E04 已验证 `EnumUnderlyingType` 八分类。生成的 `byte/uint/...` 引用随后续 E09 unsigned pass 照常规范化（enum 链位在 unsigned 之前，与 E04 同构） |
| D4 | 显式值求值 | Analyzer 对每个成员读 `IFieldSymbol.ConstantValue`（编译期常量真值），自动递增、重复值、跳跃、负值、十六进制、位运算初始化器（含已被 E04 改写成 `(E)((U)a \| (U)b)` 的成员初始化，E04 §4.7 保证仍是常量）全部覆盖；取不到常量值 → ED010 Error，该 plan 不建立 | 不自行实现 C# 枚举值递增规则（容易在重复值/位运算上出错）；以符号模型常量值为唯一真相 |
| D5 | `[Flags]` 是否分叉表示 | **不分叉**：flags 与非 flags 同一 struct 形态。flags 只影响 ①是否生成「组合名分解/逗号组合解析」成员（G/F 与 Parse 的 flags 语义）；②是否生成 `HasFlag`（实际对所有类型都生成，成本极低）。声明上的 `[Flags]`/`[FlagsAttribute]` **移除**（AttributeUsage 仅 Enum，挂 struct 上无法编译），flags 事实编码进生成成员 | E04 对 flags/非 flags 已统一规则（E04 §0 修正 1），声明侧对齐；同时消灭「按位模式启发式猜 flags」（历史提交 `6e9e4c9f` 证明该启发式会出 `_UNMAPPED` 状态共享 bug） |
| D6 | 外显成员契约（生成形态） | 见 §2.3 完整成员表。**按需生成**：Analyzer 盘点使用点决定可选成员是否生成；核心成员（值字段/实例/Value/FromValue/Equals 族/ToString）无条件生成 | 所有成员都是普通 C# 字段/属性/方法，转换器无需识别「枚举」即可转换；命名经碰撞消解，不依赖转换器命名魔法 |
| D7 | 使用点改写 | 针对被改写类型的：成员引用、强转、switch/常量模式、`System.Enum` 静态调用、枚举格式化，全部在 plan 的 TouchedFiles 内改写到外显成员（规则全集 §4.5）。**不依赖用户自定义转换运算符**（不在 struct 上声明 implicit/explicit operator），所有跨表示边界都是普通方法调用 | 规避 C14（运算符转换）未专项收口的不确定性（catalog §4 风险 14「映射存在≠语义可用」）；E04 产物里的 `(U)e`/`(E)i` 由本 pass 二次改写成 `e.Value`/`E.FromValue(...)`，两 pass 自然咬合 |
| D8 | ToString/Parse/Format/GetValues/GetNames/IsDefined/ToObject/GetName/GetUnderlyingType 契约 | 在生成类型上提供同名外显成员并改写调用点（精确语义表见 §2.4）；`Enum.GetUnderlyingType(typeof(E))` 直接改写为 `typeof(U)`；运行时 `Type` 重载 → ED020 Error | 替代 TypeMappings.json 六条 EnumHelper 映射在新形态上的适用性；名字/值语义按 .NET 运行时规则实现（含 flags 组合名、ignoreCase、数字串直返） |
| D9 | switch / 字典键 / 装箱 | ①switch 语句与 switch 表达式（governing 为被改写类型）在 C# 侧降为值比较 if-链/三元链；常量模式 `x is E.A` 同改（§4.5.4）。②字典键：struct 值语义 + 生成 `IEquatable<E>`/`Equals`/`GetHashCode`（按 Value）保持 `Dictionary<E,V>` 行为。③装箱/拆箱 `object o = e; (E)o` 保持合法 C#（struct 天然支持） | 表示形态变化后 C# switch 不允许对 struct 用常量标签，而本 pass 中间树必须当场可编译（verifyBuild 默认开），不能等 C03；字典/装箱由 fixture 钉住（§7） |
| D10 | 序列化契约 | M1 不宣称自动支持按名序列化；Analyzer 盘点 `[JsonConverter(typeof(JsonStringEnumConverter))]`、`JsonStringEnumMemberName`、`[EnumMember]`、`[XmlEnum]` 等**携带名字契约**的特性与 flags 数值序列化站点，逐处 ED040 Warning（列类型/成员/特性）。默认数值 JSON 往返纳入 fixture（要求转换器对合成 struct 产出数值形态，不满足则阻止默认开启） | 序列化随表示形态变化是 catalog §4 风险 4 的明示项；先显式化、再逐契约收口，禁止静默改变 JSON 线格式 |
| D11 | EnumHelper 桥接重定义 | 新形态**不经 `EnumHelper`/不经反射**：`System.Enum.*` 调用点被改写为生成类型自己的成员，转换器按普通方法转换，产物 Java 类自包含。EnumHelper 与 TypeMappings.json 六条映射**原样保留**，仅服务未改写的普通 Java enum；是否抽共享接口（如 `Cs2jEnumLike<U>`）与 EnumHelper 新形态重载列为 Java 侧后续工作项（本特性不动 Java 代码） | 见 §2.5。风险 4 的「重定义桥接面」= 把桥从「反射 Java enum」搬到「编译期外显成员」 |
| D12 | 与 C17/C22 关系 | C05 只生成 FromValue/Value 供调用，**不做** `e++/e--/e+1`（C17 自增归约随后把目标改写为 `FromValue(e.Value + 1)` 读-改-写）；可空 `E?` 的非位运算（比较、`GetValueOrDefault`、三值逻辑）留 C22；E04 已落地的 lifted 位运算在 struct 形态下继续成立（`E?` 即 `Nullable<E>`），无需改动 | 见 §11 边界表 |
| D13 | 支撑库注入 | **不注入支撑库**（descriptor `SupportLibrary = None`）：每个被改写类型自带所需成员，无共享工程、无 csproj 改写、无过滤清单 | 与 E04 同原则；若 M2 发现 `X` 十六进制格式化等体在每个类型重复到不可接受，再评估共享 helper（届时需新增 `PassSupportLibrary` 枚举项，按 foundation 横切约束走） |
| D14 | checked/unchecked 截断 | `FromValue` 形参类型即 U，调用点的窄化在 C# 侧按「枚举显式转换恒为 unchecked 上下文」生成 `E.FromValue(unchecked((U)expr))`（宽类型省略 `unchecked`），与 E04 §3.2 同一实测结论；C15 checked/unchecked 归一（numeric-boundary，链更靠后）不破坏该形态 | enum→enum/int→enum 的显式转换在 C# 规范中按未检查计算，独立于源码 checked 上下文 |
| D15 | plan 粒度与回滚 | **需要 plan 级回滚**：一个被改写 enum = 一个 plan（Id 为完全限定名），TouchedFiles = 声明文件 + 全树所有引用该符号/其成员/`typeof(E)`/`System.Enum` 调用点文件；整树重编译 diff 基线→唯一归属→回滚单 plan 重放，无法归属→整轮中止。机制复刻 RecordReadOnly/JavaKeywords | 声明与引用跨文件强耦合，半改写必然不可编译；不能用 E04 的逐文件模型 |
| D16 | 标识符碰撞 | 生成成员的内部名（值属性、 backing、helper）做声明空间探测，碰撞则确定性加序号（`Value2`…）；所有调用点由本 pass 改写，故内部名可自由；`ToString()` 作为 override 固定签名（与同名静态字段 `enum E { ToString }` 可共存） | `EnumValueMemberCollisionTests` 已证明成员名与 `value/getValue` 碰撞是真实用例 |

### 2.1 D1 详述：为什么是 enum-like value type 而不是 int 常量类

catalog C05 给的两档候选是「enum-like class」或「C# 侧改写为 struct/常量类」（[L314](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L314)）。二者关键差异：

| 维度 | A. enum-like 值类型（推荐） | B. int 常量类 + `E→U` 全树替换 |
|---|---|---|
| 类型身份 | `E` 作为类型保留：字段/参数/返回/数组/泛型实参/`typeof(E)` 不变 | 所有 `E` 类型位置改写成 `U`，改写面覆盖全树每个签名 |
| switch | 需降 if-链（D9） | 天然整型 switch，无需动 |
| 位运算/算术 | 经 Value/FromValue 显式过界（E04 产物直接可用） | 天然整数运算 |
| `Dictionary<E,V>` 键 | 键仍是 E，值相等由生成成员保证 | 静默变成 `Dictionary<U,V>`：两个不同 enum 的键空间合并、与裸 int 键混同，可观察行为变化 |
| 默认值/数组 | `default(E)`=Value 0（含无 0 成员时仍正确）；`E[]` 零填充正确 | 同 int，但「默认=0 值」语义散落在所有 int 站点 |
| `E?` | 保持 `Nullable<E>` | 变 `U?`，并入 C22 改写面 |
| `where T: struct` | 满足 | 退化为整型约束信息丢失；`: Enum` 两种形态都无法满足 |
| 名字契约（ToString/Parse/序列化按名） | 成员挂在类型上，外显可查 | 必须每个调用点改走 helper，且无类型可挂 |
| 爆炸半径 | 声明 + 引用点（符号可索引，全集确定） | 类型系统级替换，含跨程序集边界（外部程序集的 public enum 不能改声明却要改引用——直接不可行） |

结论：B 对「源内声明、树内闭环」的类型才可设想，且字典键/泛型/默认值的静默语义漂移不可接受；A 的全部代价（switch 降级、成员生成）都是机械且可验证的。**M1 原型需验证的唯一形态子问题是 struct 与 sealed class 谁在 StructTransformer 下产物更可靠**（默认值、数组填充、装箱、泛型实例化四个用例），若 struct 路径在转换器侧缺口过深，退回 sealed class + `Equals/GetHashCode` 按值重写 + 显式默认实例 `DEFAULT = new(0)`，诊断/plan 模型不变。

### 2.2 双形态混装（D2 ProblemOnly 的已知代价）

普通 int enum 保持 Java enum、问题 enum 变 struct 后，跨形态强转 `(Plain)(Problem)e` 在 C# 中间树写为 `(PlainEnum)e.Value`（整数→普通 enum 的显式转换合法）。该站点下游仍走 ordinal 下标路径（[TypeOperationTransformer.cs](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/TypeOperationTransformer.cs#L60-L76) L70-73），对带跳跃值的普通 enum 仍是旧 ordinal 错误——这是**存量语义债**，不是 C05 引入的回归；Analyzer 对跨形态强转站点发 ED012 Warning 计数，作为推进 `All` scope 的频率证据。

### 2.3 D6 外显成员契约（生成成员全集）

设 `enum E : U { M1 = v1, … }`（U 默认 int；flags 位 `isFlags`），生成的 `readonly partial? struct E`（非 partial）成员如下。**核心**无条件生成；**按需**由 Analyzer 盘点调用点决定（避免零调用 enum 膨胀）：

| 成员 | 层次 | 语义 |
|---|---|---|
| `private readonly U _value;`（名经 D16 碰撞消解） | 核心 | 真值 |
| `public U Value => _value;`（Java 侧派生 `getValue()`） | 核心 | E04 产物 `(U)e` 的改写目标 |
| `private E(U value) { _value = value; }` | 核心 | |
| `public static readonly E M1 = new((U)v1); …` | 核心 | 成员名、顺序、重复值全部保留；每个声明成员一个实例（重复值两个实例，值相等） |
| `public static E FromValue(U value) => new E(value);` | 核心 | 任意值合法（C# 枚举不校验值域），**不设 `_UNMAPPED` 单例**——根除可变单例状态共享缺陷 |
| `public override string ToString()` | 核心 | G 格式：见 §2.4 |
| `public bool Equals(E other) => _value.Equals(other._value);` + `override bool Equals(object)` + `override int GetHashCode()` | 核心 | 值相等（含重复值成员互等、`FromValue(3)` 等于成员）；字典键语义 |
| `public static bool IsDefined(E value)` / `IsDefined(U value)` | 按需 | 值表判定（重复值算一个） |
| `public static bool IsDefined(string name)` | 按需 | 精确名 |
| `public static E[] GetValues()` | 按需 | `new E[]{ M1, … }`，声明顺序（含重复值各一份） |
| `public static string[] GetNames()` | 按需 | 平行名表 |
| `public static string? GetName(U value)` | 按需 | 值→单名（多义取声明顺序首个），未定义 null |
| `public static E Parse(string value)` / `Parse(string value, bool ignoreCase)` | 按需 | 名字精确/忽略大小写；flags 支持逗号组合；纯数字串直接 `FromValue(数字解析为 U)`（.NET 行为）；失败抛与 C# 同文异常 |
| `public static bool TryParse(string value, out E result)` / `TryParse(string value, bool ignoreCase, out E result)` | 按需 | 同上但返回 false；out 赋 default |
| `public static E ToObject(U value)` | 按需 | `FromValue` 转发 |
| `public string ToString(string? format)` | 按需 | G/F/D/X（X 宽度按 U：byte/sbyte X2、short/ushort X4、int/uint X8、long/ulong X16） |
| `public static string Format(E value, string? format)` | 按需 | `value.ToString(format)` |
| `public bool HasFlag(E flag) => (_value & flag._value) == flag._value;` | 按需（E04 在位运算场景通常已内联，HasFlag 用于未经 E04 的残余与 Java 侧直调） |
| `public int CompareTo(E other)` / `IComparable<E>` | 按需（检测到比较排序/`IComparable` 转换） | 按 U 值 |
| `IFormattable`/`IConvertible` 实现 | 按需 | 检测到对应接口转换/调用才生成；M1 未被需求命中则不生成，命中而无法等价时报 ED020 |

生成代码自我约束（合成器只准发射「转换器已稳定支持」的 C# 子集）：数组分配、foreach、if、字符串相等、`string.Concat`/`StringBuilder`、整数字面值与强制转换；**禁止在生成成员体内出现 enum switch、模式、插值 handler 等高危构造**。`X` 格式的十六进制渲染是否需要共享 helper 是 D13 的复查点（M2）。

### 2.4 D8 语义细则（.NET 对齐点）

- `ToString()` G 格式：非 flags——值命中某成员则返回其名（重复值取首个声明名），否则返回 U 的十进制字符串；flags——值为 0 时返回 0 值成员名或 `"0"`；非零时按值降序尝试成员位包含，全部位被解释则逗号连接名表（声明顺序排序），否则返回十进制；`F` 同 G 但不要求 `[Flags]`。
- `Parse/TryParse`：去除前后空白；按逗号拆 token（flags），逐 token 查名（ignoreCase 用 OrdinalIgnoreCase），名字 token 按位或；数字 token 允许（解析为 U 后直接成值，负数走 U 解析）；未知名失败。
- `GetValues` 声明顺序、含重复值成员；`Enum.GetValues` 返回 `Array` 的旧式用法（反射式索引/Length）走数组本体，检测到 `Array.CreateInstance`/`Enum.ToObject(Type,…)` 等运行时类型形态报 ED020。
- 泛型 `Enum.GetValues<E>()`/`GetNames<E>()`（现代 API）同样改写到 `E.GetValues()/GetNames()`。
- `Enum.GetUnderlyingType(typeof(E)) → typeof(U)`。
- `Enum.IsDefined(Type, Object)` 的装箱数值/字符串两重载映射到上表对应 IsDefined。

### 2.5 D11 桥接重定义后的三方形态

| 输入形态 | 预处理后 | 转换器视角 | Java 产物 |
|---|---|---|---|
| 问题三族 enum（C05 命中） | struct + 外显成员，无 `System.Enum` 调用残留 | 普通 readonly struct + 普通方法/数组 | 自包含 final class：`static final E M1`、U 值字段、`getValue/fromValue/parse/...`（由方法转换自然派生），**无 ordinal 依赖、无 `_UNMAPPED`、无 EnumHelper 反射** |
| 普通顺序 int enum（首期保留） | 真 enum（可能经过 E04 的运算改写） | 现有 EnumTransformer 直转 | Java enum + 既有 EnumHelper 路径（维持现状） |
| 桥接设施 | 不改 | TypeMappings.json 六条保留（仅普通 enum 路径可达） | EnumHelper 保留；是否新增面向 generated-class 的接口/重载为后续 Java 工作项，需与转换后方法名面同步设计（风险 R2） |

---

## 3. 注册表与链位契约

### 3.1 现状与目标描述符

stub 现状（[PassStubCatalog.cs](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassStubCatalog.cs#L82-L90)）：id `enum-declaration`，显示名 "Enum declaration rewrite"，目录 `.cs2j-enum-declaration-src`，`PassStage.Declarative`，`PassStatus.Stub`，`defaultEnabled: false`，`runAfterIds: { PassIds.Enum }`，`needsSemanticModel` 默认 true，catalogItems `C05`。

翻转后目标描述符（其余字段在 Stub→Implemented 时显式化）：

| 字段 | 值 | 理由 |
|---|---|---|
| Id | `enum-declaration` | 不变 |
| Stage | `Declarative` | 声明级改写（含跨文件用点），与 stub 登记一致 |
| Status | `Implemented` | Stub→Implemented 翻转 |
| DefaultEnabled | **false（落地起步）→ 满足 §8 后翻 true** | 新机制通道先 opt-in（对齐 record-readonly/array-method/java-keywords 既有策略与 catalog §4 风险 10） |
| RunAfterIds | `{ "enum" }`（维持） | 消费 E04 产物（见 3.2） |
| IntermediateDirectoryName | `.cs2j-enum-declaration-src`（维持） | |
| NeedsSemanticModel | true | 全编译符号表是声明分类/值求值/用点索引的前提 |
| SupportLibrary | `None` | D13 |
| VerifyBuildByDefault | **true** | 声明+用点同 plan 改写，必须逐环整树编译验证 |
| VerifyTestsByDefault | **true** | 行为等价（ToString/Parse/flags 组合、字典键）由中间树测试钉住 |

### 3.2 为什么独立 pass（而不是 catalog 文本说的「并入 E04」）

1. 注册表已按 foundation plan L84 把它登记为独立 id 与独立中间目录；`--list-passes`、指纹令牌、preserve 目录、拓扑校验均以 id 为单位，回填合并成本无谓。
2. 执行模型本质不同：E04 是**逐文件**语义 pass（标准 spine），C05 是**整树 Analyze→Plan→Rewriter + plan 回滚**（自定义 `ExecuteAsync`），与 record-readonly/java-keywords 同类。
3. 独立开关允许在 E04 默认开启的既成事实下单独灰度声明侧。
4. 共享通过代码复用实现：把 E04 中「U 八分类/keyword 生成/窄类型 unchecked/可空解包」等纯函数事实按需提取为内部公共工具（或在新命名空间内复制小函数），不改变 E04 公共契约与其 29 个单测。

链位副作用检查（RunAfter=`enum` 的拓扑后果）：本 pass 实际排在 `null-coalesce → enum → enum-declaration → delegate → tuple → … → unsigned → generic → …` 的早期位置——早于 E09 unsigned（生成的 U 类型引用由 unsigned pass 继续处理，与 E04 同款配合）、早于 E10 generic（满足 catalog「须在 Generic 之前：enum 作泛型实参/数组元素」，[L317](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L317)）、早于 strip-default（生成成员无默认参数，实际无交互）。与 pattern-desugar（C03，锚 `null-coalesce`）的相对顺序在注册表上未强约束——两 pass 都落地时需补 RunAfter 边，见 §11。

### 3.3 Stub→Implemented 翻转清单

1. [PassStubCatalog.cs](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassStubCatalog.cs)：删除 `Ids.EnumDeclaration` 常量与 Stubs 表中的 enum-declaration 条目（常量迁移至 [PassCatalog.cs](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassCatalog.cs#L6-L24) 的 `PassIds`）；全局检索对 `PassStubCatalog.Ids.EnumDeclaration` 的引用（当前仅 stub 自身）。
2. `PassCatalog.Implemented`：按 §3.1 增加描述符（`RunAfterIds: new[] { PassIds.Enum }`，verifyBuild/Tests=true，defaultEnabled=false）。
3. [PassChainFactory.cs](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassChainFactory.cs)：`CreateAdapter` switch 增加 `PassIds.EnumDeclaration => new EnumDeclarationPassAdapter(...)`；`PassChainConfiguration` 增加 `Func<EnumDeclarationOptions>` 懒加载工厂（仿 JavaKeywordOptionsFactory，L46-47）。无 legacy toggle（不新增 `--no-xxx` 老开关），启停只走描述符默认值 + 通用 `--enable-pass/--disable-pass`。
4. 新增 CLI `Adapters/EnumDeclarationPassAdapter.cs`（§5）。
5. 注册表顺序断言/`--list-passes` 测试更新（状态 Stub→Implemented、默认值可见）。
6. 文档与 `.gitignore`：中间目录 `.cs2j-enum-declaration-src/` 加入忽略清单（与既有 `.cs2j-enum-src/` 同处，顺手核对历史遗漏目录，E04 设计 §8.4 已列同类债务）。

stub 显式启用即 CS2J9001（[PreprocessDiagnosticCodes.cs](file:///d:/code/cs2j/src/CSharpToJava.Core/Preprocessing/PreprocessDiagnosticCodes.cs#L12)）的行为在翻转后自然消失；翻转 PR 必须与适配器同批交付，不允许中间态。

---

## 4. Core 组件设计

### 4.1 新文件清单（`src/CSharpToJava.Core/EnumDeclaration/`）

命名空间 `CSharpToJava.Core.EnumDeclaration`，组织方式对齐 `RecordReadOnly/`（9 文件）与 `JavaKeywords/`：

```
EnumDeclarationPreprocessor.cs   # 公共入口：
                                 #   EnumDeclarationPreprocessor(options)
                                 #   void BindCompilation(CSharpCompilation)
                                 #   IReadOnlyList<EnumDeclarationPlan> Plans
                                 #   void ExcludePlans(IEnumerable<string> planIds)
                                 #   EnumDeclarationFileResult RewriteFile(
                                 #       string sourceCode, SemanticModel model,
                                 #       EnumDeclarationStatistics stats,
                                 #       List<EnumDeclarationDiagnostic> diags)
                                 #   bool TryAttributeDiagnostic(...)
                                 #   internal static string? FormatAndValidate(CompilationUnitSyntax)
EnumDeclarationModels.cs         # EnumDeclarationOptions / Statistics / Diagnostic(ED0xx) /
                                 #   Severity / FileResult / PlanOutcome
EnumDeclarationAnalyzer.cs       # Phase 1：整树盘点（§4.3），输出 EnumDeclarationAnalysis
EnumDeclarationPlan.cs           # EnumDeclarationPlan / PlanBuilder（plan=单枚举；
                                 #   Id/Symbol/UnderlyingType/IsFlags/Members/TouchedFiles/
                                 #   Outcome/FailCode/FailReason/OwnsTouch）
EnumDeclarationSynthesizer.cs    # 生成 struct 声明与外显成员（D6/D16），flags 名表与
                                 #                                 解析/格式化方法体合成
EnumDeclarationSyntaxFacts.cs    # IsFlags（符号+语法双判）、U 分类与 keyword、窄类型、
                                 #   常量值读取、System.Enum 调用分类、碰撞命名、
                                 #   RewriteAnnotation("Cs2jEnumDeclarationRewrite")
EnumDeclarationRewriter.cs       # Phase 3a：EnumDeclarationSyntax → struct 声明
EnumUseSiteRewriter.cs           # Phase 3b：成员引用/强转/switch 与模式/System.Enum 调用/
                                 #   格式化/默认值等使用点改写（§4.5）
```

### 4.2 三阶段数据流（整树一次分析、plan 化重写、整树验证回滚）

复刻 java-keywords spec §3.2 的骨架：

1. **分析（整树一次）**：CLI runner 已按标准方式 parse 全树 + 建 `CSharpCompilation`（`LanguageVersion.Latest`/`NETCOREAPP` 符号、allowUnsafe、`GetBasicReferences()`，与 RecordReadOnly 同款编译选项；合成 global usings 视夹具需要）。Analyzer 遍历编译中所有源内 `INamedTypeSymbol { TypeKind.Enum }`（partial 不存在于 enum；多 declaring syntax 理论上单件）。
2. **计划**：对 D2 命中的每个枚举建 plan；求值全部成员常量（D4）；枚举其全树使用点（引用、成员访问、强转、switch governing、模式、`typeof(E)`、`System.Enum` 静态调用、数组/泛型实例化、可空形态、跨形态强转），TouchedFiles 取并集；不可表示站点（§6）直接判该 plan Infeasible（ED020，不重写——保持真 enum，交回现有失败路径，不产出半残类型）。
3. **重写（逐文件）**：文件命中哪些 active plan 就依次跑 `EnumDeclarationRewriter`（仅声明文件）+ `EnumUseSiteRewriter`（全部 TouchedFiles）；挂统一 RewriteAnnotation；`FormatAndValidate` 三候选（改动区间格式化→整文件格式化→原文，零 error 取首）。
4. **验证回滚（CLI 驱动，adapter 自定义 ExecuteAsync）**：基线错误集 → 每轮从原始副本重放 → 整树重编译 → DiffErrors → `TryAttributeDiagnostic` 按文件 + plan 的 OwnsTouch 唯一归属 → ExcludePlans 回滚该 plan 的 TouchedFiles → 迭代收敛；不可唯一归属 → 本轮全部回退 + ED030。plan 回滚后该 enum 维持真 C# enum（回到今天的转换器行为，不产生新损坏）。

### 4.3 整树符号表（EnumDeclarationAnalysis）内容

- 每个源内 enum：`INamedTypeSymbol`、完全限定名、U（SpecialType）、IsFlags（符号 attribute + 语法别名双判）、成员有序列表（名、`ConstantValue` 装箱值、是否 EqualsValue、重复值组）、声明位置与语法引用。
- 使用点倒排索引（按枚举符号）：`IdentifierName/MemberAccess` 成员引用、`CastExpression`（enum↔int/enum↔enum/enum↔object）、switch governing/switch 表达式、ConstantPattern、`typeof(E)`、`System.Enum` 调用（方法名 + 实参是否 `typeof(E)` 常量）、数组类型 `E[]`/泛型实参/Nullable/E?、`default(E)`、特性实参、`Enum?` 与 `.Value/.HasValue`。
- 可行性标记：enum 是否作为 `where T : struct, Enum`/`where T : Enum` 约束方法的类型实参（全树解析泛型调用）；是否被表达式树 lambda 捕获（`(E)` 强转/成员访问在 Expression 树内——重写会改变树节点，沿用 JK-8 同款判定共享工具，落地时与 C08 对齐）；是否有 `dynamic` 站点；名字契约特性清单（D10）。
- 按需生成清单：按使用点集合决定每个 plan 生成哪些可选成员（D6 右列），保证「无调用不生成」。

### 4.4 声明改写示例（产物形状，示意）

```csharp
// before
[Flags]
public enum Direction : byte
{
    None = 0,
    North = 1,
    East = 1 << 1,      // 常量求值 = 2
    South = 4,
    West = 8,
    All = North | East | South | West,   // E04 之后已是 (Direction)((byte)... | ...)
}

// after（示意；生成方法体仅列形状，命名经碰撞消解）
public readonly struct Direction : IEquatable<Direction>
{
    private readonly byte _value;
    private Direction(byte value) { _value = value; }
    public byte Value => _value;

    public static readonly Direction None = new((byte)0);
    public static readonly Direction North = new((byte)1);
    public static readonly Direction East = new((byte)2);
    public static readonly Direction South = new((byte)4);
    public static readonly Direction West = new((byte)8);
    public static readonly Direction All = new((byte)15);

    public static Direction FromValue(byte value) => new Direction(value);
    public bool Equals(Direction other) => _value == other._value;
    public override bool Equals(object? obj) => obj is Direction other && Equals(other);
    public override int GetHashCode() => _value.GetHashCode();
    public bool HasFlag(Direction flag) => (_value & flag._value) == flag._value;

    public override string ToString() { /* flags 组合名分解，无 0 成员时 "0"，
                                           解释不了返回 _value.ToString() */ }
    public string ToString(string? format) { /* G/F/D，X → X2（U=byte） */ }
    public static string Format(Direction value, string? format) => value.ToString(format);
    public static Direction[] GetValues() => new[] { None, North, East, South, West, All };
    public static string[] GetNames() => new[] { "None", "North", "East", "South", "West", "All" };
    public static bool IsDefined(Direction value) { /* 值表 */ }
    public static bool IsDefined(byte value) { /* 值表 */ }
    public static bool IsDefined(string name) { /* 名表 */ }
    public static Direction Parse(string value) { /* 名表 + 逗号组合 + 数字串 */ }
    public static Direction Parse(string value, bool ignoreCase) { /* 同上 */ }
    public static bool TryParse(string value, out Direction result) { /* ... */ }
    public static bool TryParse(string value, bool ignoreCase, out Direction result) { /* ... */ }
}
```

访问修饰符按原声明映射（internal 保留 internal；嵌套保持嵌套位置与外层可见性）；`[Obsolete]`/`[Description]`/`[EnumMember]`/`[XmlEnum]` 等字段级特性迁移到对应 static readonly 字段（类型级 `[Flags]` 移除，D5）；XML doc/trivia 尽量保留。

### 4.5 使用点改写规则全集（EnumUseSiteRewriter）

所有判定以原树 `SemanticModel` 符号绑定为准（禁止文本猜测），针对「符号在 plan 表中且 active」的枚举：

1. **成员引用** `E.M` / `using static` 下的 `M`：保持原表达式文本即可绑定到生成的 static readonly 字段（字段同名），原则上零改写；例外：作为**特性实参/const 初始化/case 旧值**等要求编译期常量的上下文——static readonly 非常量，这些位置必须随 §4.5.4 的 switch 降级与 const 场景重写（const 字段 `const E c = E.M` → 改为 `static readonly E c = E.M`；特性实参里出现 enum 成员在 C# 中本就只能是编译期常量，改写到 struct 后**不可表示** → ED020 列点报错，回滚该 plan，不局部凑合）。
2. **强转**：
   - `(U)e`（含 E04 产物、enum→整型）→ `e.Value`；可空 `(U)e?` 已被 E04 lifted 成 `.Value` 链，遇 `E?` 取 `.Value.Value` 形态按实际树处理；
   - `(E)integerExpr` → `E.FromValue(unchecked((U)(integerExpr)))`（窄类型才包 unchecked，D14；宽类型 `FromValue(integerExpr)`）；
   - `(OtherProblemE)e`（两侧都被改写）→ `OtherProblemE.FromValue((OtherU)e.Value)`；
   - 到**未改写**普通 enum 的跨形态强转 → 保留 `(PlainEnum)(object?)`/`(PlainEnum)integerExpr` 整数桥 + ED012 Warning（§2.2）；
   - `(E)(object)o` 拆箱 → 保留（struct 拆箱合法 C#），转换器侧由夹具验证。
3. **构造/默认**：`default(E)`/`new E()` → `default(E)`（struct 默认即 0 值，文本可留；转换器 struct 默认值路径处理）；`E[]` new/数组填充保持（零值=Value 0）。
4. **switch 与模式**（因 struct 非常量标签，必须当场降级，保证本环可编译）：
   - `switch (e) { case E.M1: …; default: … }` → `if (e.Value == (U)v1) {…} else …` 链（值重复的成员不允许同值多 case，C# 编译器本就拒绝）；goto case/goto default 存在时本 pass 不处理控制流跳转 → ED020（交 C03/E14 先去糖后再跑，或回滚 plan，§11）；
   - switch 表达式 → 嵌套三元链（与转换器现有 switch-expression 降级同形）；
   - `e is E.M` 常量模式 → `e.Value == (U)v`；`e is E.M or E.N` → 布尔或；带 `when` 的模式臂整体保留 when 表达式、仅换判等子式；
   - 声明模式 `is E x`、属性/位置模式作用于被改写 enum → ED020（归 C03/C37 语法面，本 pass 不造语义）。
5. **System.Enum 调用**（按 D8/§2.4 表）：`Enum.GetValues(typeof(E))` → `E.GetValues()`；`GetNames`、`GetName`、`Parse`、`TryParse`、`IsDefined`（三重载）、`Format`、`ToObject`、泛型 `GetValues<E>()` 同；`Enum.GetUnderlyingType(typeof(E))` → `typeof(U)`；第一个实参不是常量 `typeof(planEnum)` 的调用（运行时 Type、`enumType` 变量）→ ED020。
6. **实例格式化**：`e.ToString()` 保留（override 已生成）；`e.ToString("G"/"F"/"D"/"X")`/`$"{e:G}"` 插值格式 → 生成的 `ToString(string)`；插值的格式槽由本 pass 显式改写（不依赖 StringExpressionTransformer 对 struct 的格式派发）。
7. **比较**：`e1 == e2`/`!=` 在 struct 上无默认 `==` 运算符——改写为 `e1.Value == e2.Value`（`< > <= >=` 同改 Value 比较；E04 未覆盖枚举比较）；可空比较按 lifted Value 比较。
8. **HasFlag**：E04 已把绝大多数 `x.HasFlag(f)` 内联为整数比较；本 pass 把残余整数比较中的 `(U)x` 再落到 `.Value`；未被 E04 覆盖的形态（nullable receiver、跨类型）沿用 E04 的跳过+Warning 口径并升级为 ED013 计数；生成的 `HasFlag(E)` 成员供 Java 侧与未改写场景调用。
9. **不触碰**：`nameof(E.M)`（按名契约保留；如与 C08 同时启用，C08 在链尾，nameof 处理互不冲突）、表达式树内站点（ED020 或共享跳过判定）、`System.Enum` 派生类型形参约束内的泛型方法体（站点在树内但 T 非具体类型——不改）。

### 4.6 plan 级回滚结论

**需要**，且粒度为单枚举：声明改写与用点改写必须原子（只改声明 = 全树不可编译；只改用点 = 语义损坏）。回滚机制与 RecordReadOnly/JavaKeywords 完全同构（ExcludePlans + 原始副本重放 + 整树重编译归属），不发明新机制；单文件模式下跨文件 plan 按既有惯例排除并出对应 Warning 诊断（仿 JK090/RR090，码位 ED090）。

---

## 5. CLI 适配器与集成

### 5.1 EnumDeclarationPassAdapter

新增 `src/CSharpToJava.CLI/Preprocessing/Adapters/EnumDeclarationPassAdapter.cs`，继承 [ProjectPassAdapter](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/ProjectPassAdapter.cs)，形态对齐 [RecordReadOnlyPassAdapter.cs](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/Adapters/RecordReadOnlyPassAdapter.cs)：

- `Descriptor => PassCatalog.ImplementedById(PassIds.EnumDeclaration)`；
- `Override ExecuteAsync`：走自定义整树迭代（PrepareTree 已由 runner 完成）：`BuildStandardCompilation` → `preprocessor.BindCompilation` → 按文件排序重放（.cs 走 `RewriteFile`，非 .cs `CopyVerbatim`）→ 重建编译 → diff 基线 → 归属/回滚循环 → `Restore` → `VerifyBuildAndTestsAsync`；
- `CreateParseOptions` 用 `LanguageVersion.Latest`（枚举成员位运算初始化、现代 `Enum.GetValues<E>()` 需要）；
- `VerifyBuild/VerifyTests` 取构造入参（convert-project 全局 verify 开关），`DeleteBuildOutputsAfterRestore = true`（与 EnumPassAdapter 一致，verify 型 pass）；
- 无支撑库注入/过滤判定（D13）：`SupportLibrary.None`，`PassChainFactory.IsExecutedSupportLibraryProject` 无需改；
- 统计映射、诊断行、摘要行沿用适配器钩子（`BuildChainSummaryLine` 建议形如 `enum-declaration: N enums rewritten (flags=x, non-int=y, explicit=z), M use-sites, K plans skipped/rolled back, R residual`）。
- 保留旧包装类惯例：本 pass 无历史 `Project*Preprocessor` facade（stub 是注册表先建的），**不新增**薄包装类；独立入口只走通用 `preprocess --pass enum-declaration` 与 `convert-project` 链（foundation 已提供通用 verb，无需专属 verb）。

### 5.2 verify 默认、指纹与开关

- verifyBuild/verifyTests 默认 true（§3.1），受全局 `--no-verify-build/--no-verify-tests` 控制。
- 指纹：注册表全集令牌机制自动为新 pass 产生 `pass:enum-declaration=on/off` 令牌（foundation plan L72），无需手工 token；中间目录经 descriptor 驱动自动进入 preserve 集合与「本次实际执行目录」累计。
- 开关：落地后默认关，经 `--enable-pass enum-declaration` 开启；§8 门槛达成后改 descriptor `defaultEnabled: true`，老链上无对应 legacy toggle，`BuildLegacyToggles` 无需改。
- `--list-passes` 翻转后显示 Implemented/默认值/目录/RunAfter。

### 5.3 集成边界

本 pass 不创建生成文件（不跨文件分发新 .cs——成员都内联进声明文件），不需要 tuple 式生成文件钩子；对 MSBuild 目录文件（`Directory.Build.props` 等）的复制沿用 runner 标准准备。

---

## 6. 诊断策略（不可表示即显式报错）

码位 ED0xx（MSBuild 格式，码位只追加；与 E04 的松散 Warning 不同，本 pass 对齐 JK/RR 编码化诊断）：

| 码 | 级别 | 含义 |
|---|---|---|
| ED001 | Info | 枚举已归一（消息含完全限定名、分类 flags/non-int/explicit/dup、U） |
| ED002 | Info | 使用点改写分类计数（cast/switch/pattern/Enum-API/format/compare） |
| ED010 | Error | 成员常量值无法求值（D4），plan 不建立 |
| ED011 | Warning | 语法命中分类但符号信息不全（IErrorTypeSymbol），plan 跳过 |
| ED012 | Warning | 跨形态强转站点（被改写 enum ↔ 保留普通 enum），计数作为 All-scope 推进证据（§2.2） |
| ED013 | Warning | E04 残余口径站点（nullable receiver HasFlag 等）透传计数 |
| ED020 | Error | 不可表示的枚举用法（plan Infeasible，整体跳过该枚举，保持真 enum）：特性实参/case 等编译期常量上下文要求、`where T: struct, Enum`/`: Enum` 实参绑定、运行时 `System.Type` 的 Enum API、switch 中 goto case、声明/属性模式作用于被改写 enum、表达式树内改写站点（与 C08 共享判定）、`IFormattable/IConvertible` 命中而无法等价生成 |
| ED030 | Error | 整树验证新增错误无法归属单 plan → 本轮全部回退（仿 JK030/RR030） |
| ED031 | Warning | 新增错误唯一归属某 plan → 该 plan 原子回退（仿 JK031/RR031） |
| ED040 | Warning | 名字契约/序列化站点盘点结果（JsonStringEnumConverter、JsonStringEnumMemberName、EnumMember、XmlEnum 等，D10），逐类型/成员列出 |
| ED041 | Warning | dotnet restore 失败（沿用各 pass 惯例） |
| ED090 | Warning | 单文件模式 plan 非自包含（跨文件），跳过（仿 JK090） |

原则（对齐 catalog §4 风险 12）：任何 C# 侧无法等价表达的站点一律显式诊断 + plan 不落地，**禁止**注释/TODO 占位/把半截 struct 透传到 Java 产物；`--strict` 语义下 ED012/ED013/ED040 升级为 Error（沿用 E04 strict 口径，供「输入只用受支持构造」的 CI 断言使用）。

---

## 7. 测试计划

### 7.1 Core 单测矩阵（`tests/CSharpToJava.Tests/EnumDeclarationRewriterUnitTests.cs`，仿 RecordReadOnly/JavaKeyword 单测：内联源码→整树编译→RewriteFile→忽略空白文本断言 + `AssertOutputCompiles`）

**声明分类**：`[Flags]`（含 `[FlagsAttribute]`/`using System` 别名/全限定名）；`:byte/:sbyte/:short/:ushort/:uint/:long/:ulong`；显式值、跳跃值、自动递增接续、重复值、负值、十六进制、位运算初始化（含 E04 改写后的 `(E)((U)a|(U)b)`）、无 0 值成员；普通顺序 int enum 不改（Plan 不建立、字节保真）；嵌套 enum；internal/嵌套可见性；成员名与 `Value` 碰撞（D16）。
**生成成员**：核心成员必在；可选成员按调用点按需出现/缺席；`GetValues` 顺序与重复值；`FromValue` 任意值合法且无 `_UNMAPPED`；`Equals/GetHashCode` 值语义（重复值互等）；flags `ToString`（0 值/组合/不可解释→数字）；非 flags `ToString`；`Parse/TryParse`（精确/ignoreCase/逗号组合/数字串/失败异常文）；`Format` G/F/D 与 X2/X4/X8/X16 宽度；`GetName/IsDefined/GetNames/ToObject/GetUnderlyingType→typeof(U)`。
**使用点**：四类强转（含窄类型 unchecked 包裹、宽类型无包裹）；switch 语句→if 链、switch 表达式→三元链、`is E.M`/or 模式/when 守卫；`==/!=/< >` 值比较；`Enum.*` 六 API + 泛型 API；`e.ToString("X")` 与插值格式槽；`E?` lifted 与 E04 产物串联（先跑 E04 再跑 C05 的双 pass 组合用例）；数组 `E[]` 默认填充；`Dictionary<E,V>` 增查与装箱 `(E)(object)`。
**回滚/诊断**：ED020 各子类（特性参数、Enum 约束、运行时 Type、goto case、表达式树）；ED010 非常量成员；ED031 归属回滚（注入基线后新增错误）；ED030 不可归属整轮回退；ED090 单文件跨文件；plan 回滚后树回到真 enum 且可编译；`FormatAndValidate` 三候选。
**幂等**：对输出再跑一次 C05，零 plan/零改写。

### 7.2 项目级 fixture（`tests/SampleEnumDeclarationSolution/`，仿 SampleJavaKeywordSolution：Lib+App+sln，纳入 ProjectTests 工程 Content）

- Lib：flags 位组合（byte 与 int 各一）、long/ulong 极值成员、显式跳跃/重复值枚举、无 0 成员枚举、成员名碰撞枚举、嵌套枚举；Parse/TryParse/Format/GetValues 的实际调用；`Dictionary<E,int>`；`E?` 字段；与普通 int enum 共存及跨形态强转（接收 ED012）；序列化 DTO（数值往返 + 一处 `[JsonConverter(typeof(JsonStringEnumConverter))]` 接收 ED040）。
- App：跨文件成员引用/switch/泛型集合/数组填充。
- 断言：`Success`、统计与诊断码、中间树 `.cs2j-enum-declaration-src` 中目标声明已为 struct、无 `[Flags]` 残留、无针对被改写类型的 `System.Enum` 调用文本；`dotnet build -c Release` 退出码 0；含测试工程时 `dotnet test -c Release` 通过（行为等价：flags 组合名、Parse 往返、字典键）。
- 干净工程字节级复制用例（含 BOM 保持，仿各 pass 的 CleanProject 用例）。

### 7.3 Release build/test 与全量回归

- `dotnet build -c Release`（CSharpToJavaConverter.slnx）；三工程 `dotnet test -c Release`（Tests/ProjectTests/ConversionTests）全绿或红灯集不扩大。
- 双 pass 顺序专项：同一 fixture 连跑 E04→C05（真实链序），验证强转/位运算/lifted 产物的咬合。

### 7.4 序列化往返

- 数值默认 JSON（System.Text.Json 默认）在 C# 中间树行为测试；Java 侧端到端**以转换产物可编译 + 数值线格式一致**为默认开门条件之一（不满足即保持默认关，不等同于本特性改转换器——门槛驱动的是开关决策）。
- 按名序列化仅要求 ED040 准确报警；线格式收口列为后续工作（R5）。

### 7.5 residual 闸门

- 中间树语义扫描：被改写 enum 符号集上，不存在残余 `EnumDeclarationSyntax`（已为 struct）、不存在绑定到这些类型的 `System.Enum` 调用、不存在 governing 为这些类型的 switch/常量模式、不存在以其为类型的未处理强转；计数入 Statistics 并在 strict 下为 Error。
- 闸门必须是**语义级**（E04 设计 §5.5 教训：枚举与整数语法同形，纯文本扫描误报）。

### 7.6 与现有 enum 测试的衔接

- E04 资产不动且必须保持绿：`EnumPreprocessorUnitTests`（29/29，实测 2026-09-28）、`ProjectEnumPreprocessorTests`、`ByteEnumArithmeticWrapTests` 中 E04 相关断言。
- 转换器侧存量红灯（实测 `EnumTransformerTests` 41 红、enum 过滤集 77 红/74 绿/1 跳）：这些断言面向**已删除的转换器富实现**（getValue/fromValue/_UNMAPPED/常量类）。本 pass 不改转换器，**单文件直转路径（ConversionPipeline）不经预处理**，故这些红灯不会因本特性转绿。处置方案（M5 前定案，列为开放问题 Q4）：① 保持现状并在测试类标注「待 C05 项目级路径 + 转换器收口」分类；② 迁移可复用断言到项目级 fixture（C05 路径下的等价断言）；③ 转换器收口 PR 中删除/重写。默认建议 ①+②，不在本 pass 内大面积改测试期望。
- `ProjectPreprocessorsConsistentTests`：追加 adapter/描述符存在性与 `.cs2j-enum-declaration-src` 断言（仿 enum 段 L122-126）；注册表顺序断言更新。
- CLI 测试：`--enable-pass enum-declaration` 启停、`--list-passes` Implemented/默认关、preserve 目录、ED030/ED020 退出码语义。
- `tests/DualTargetCorpus/Enums.cs`：补 flags byte/long、显式跳跃值、重复值、Parse/Format 双目标用例。

---

## 8. 默认开启验收门槛

默认关落地；只有同时满足以下条件才翻 descriptor `defaultEnabled: true`（对齐 catalog §4 风险 10 与 foundation 交付包模板第 5 条）：

1. 三工程 `dotnet build/test -c Release` 全绿；存量红灯集不扩大（§7.6 的 77 个红为已知基线，需分类登记并确保无新增）。
2. SampleEnumDeclarationSolution 在 `--enable-pass enum-declaration` 下 build/test 通过；关闭时链输出与今天字节一致（clean 工程零触碰）。
3. residual 闸门在启用时为 0（strict 通过）；ED020 在 sdk 扫描样本中的命中类型已分类，其中「特性常量/Enum 约束/运行时 Enum API」三类有明确人工处置文档，无静默透传。
4. sdk 基线（D:\csharp-sdk）补扫完成（§1.2）：三类声明与使用点频率、回滚率、ED040 序列化站点清单产出；启用后基线转换不退化（成功率、产物可编译率不降低；普通 int enum 路径行为零变化）。
5. 双 pass（E04→C05）与 unsigned/generic 的链序组合在基线样本上验证通过；指纹/增量缓存正确（preserve 目录与 pass 令牌生效）。
6. 序列化数值往返端到端用例通过，或明确接受「按名字场景一律 ED040 + 文档化」并经评审确认。
7. D1 的 struct/class 形态选择（M1 原型）、D13 是否需要共享格式化 helper（M2）两个待决项已锁死。

---

## 9. 里程碑

| 里程碑 | 内容 | 粗估规模 |
|---|---|---|
| M1 | **原型定形态**：最小 Synthesizer（核心成员 + GetValues/ToString/Parse 非 flags）+ 单个显式值 int enum 的 Analyzer/plan/rewrite 端到端；同一例分别用 struct 与 sealed class 走通「中间树编译 + 主转换 + javac」，锁定 D1；提取 E04 共享事实 | Core ~400 行 + 原型代码（可丢弃） |
| M2 | Core 骨架（Models/SyntaxFacts/Preprocessor 契约/FormatAndValidate）+ 全分类 Analyzer（常量值/使用点索引/可行性/序列化特性盘点）+ Plan/回滚骨架 + Synthesizer 全成员（flags 名表/Parse/Format X 宽度/IsDefined 族）；定 D13 | ~900 行 |
| M3 | EnumUseSiteRewriter 全集（强转/比较/switch 与模式/Enum API/插值格式/双 pass 组合）+ ED0xx 诊断 + 单测矩阵主体 | ~800 行 |
| M4 | CLI adapter（整树迭代/归属回滚/verify）+ 注册表翻转（默认关）+ `--list-passes`/CLI 测试 + SampleEnumDeclarationSolution + 一致性测试 + residual 闸门 + 幂等 | ~400 行 + fixture |
| M5 | sdk 补扫与数据评审、红灯基线分类处置（§7.6 Q4）、文档（桥接契约面向转换器/Java 侧的后续工作清单）、双目标语料扩充 | 文档/扫描 |
| M6 | 按 §8 门槛评审默认开启；（后续、独立 PR）转换器侧收口：新形态的 Java 映射验证、EnumHelper 桥接重定义（D11）、EnumTransformer 三类 throw 的退路调整 | 独立排期 |

---

## 10. 风险

| # | 风险 | 证据/说明 | 缓解 |
|---|---|---|---|
| R1 | **enum-as-class 外显契约随表示形态全面变化**（catalog §4 风险 4，[L923](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L923)）：`Enum.ToString/Parse/Format/GetValues`、switch、字典键、`[Flags]` 序列化全部依赖表示 | EnumHelper 全方法 Java enum 假设（isEnum/ordinal/反射 getValue/_UNMAPPED）；转换器 ordinal 假设散布 5+ 站点（§1.1） | 外显成员契约（D6）+ 使用点全改写（§4.5）+ 桥接重定义（D11）；不靠运行时反射；逐契约 fixture |
| R2 | 转换器对「合成 readonly struct + 静态只读实例 + 数组成员/Parse 方法体」的转换可能有自身缺口（struct 默认值、泛型实例化、StringBuilder/数组转换） | catalog §4 风险 14「映射存在≠语义可用」；StructTransformer 路径复杂 | M1 原型先行证伪；不可转换的构造不生成（合成器语言子集自律）；默认关 + 门槛 4；发现转换器缺口时报 ED020 而非硬闯 |
| R3 | 双形态混装（ProblemOnly）：被改写 enum 与保留 Java enum 之间强转/数组/字典混用边界 | §2.2；下游对普通 enum 仍有 ordinal 债 | ED012 计数；scope=All 作为已登记的演进路线；不在首期追求全量 |
| R4 | 编译期常量上下文：enum 成员是 C# 编译期常量，struct static readonly 不是——特性参数、`const`、旧 case、attribute、默认参数值、`switch` 标签全面受影响 | §4.5 规则 1/4 | switch 当场降级；const 降级为 static readonly；**特性参数等不可降级站点 ED020 + plan 回滚**，不静默凑合 |
| R5 | 序列化线格式：默认数值 JSON 依赖转换器把 struct 按值写出；按名契约（JsonStringEnumConverter/EnumMember/XmlEnum）无自动等价 | D10；风险 4 明示 | ED040 显式盘点；数值往返入门槛；按名收口独立工作项 |
| R6 | `[Flags]` 移除影响反射发现 flags 的代码（`Attribute.GetCustomAttribute(typeof(E), typeof(FlagsAttribute))`、JSON 库按 FlagsAttribute 决定逗号拼接） | D5 移除特性是编译必需 | 盘点反射查 FlagsAttribute 的站点并入 ED040 清单；生成成员的 flags 行为自包含；需要保留信号时由后续桥接以别的注解承担（Java 侧重定义时设计） |
| R7 | 表达式树/动态：Expression<T> 内的 `(E)x`/成员访问改写会改变表达式树节点 | 与 JK-8 同族（catalog §4 风险 8） | 共享表达式树判定；命中 ED020/回滚；dynamic 同 |
| R8 | 测试期望大面积脱节：77 个 enum 相关红灯断言旧富实现 | §1.1 实测 | §7.6 处置策略；不把「转绿旧测试」混入本 pass 验收，防止为迁就旧断言扭曲设计 |
| R9 | 中间树验证成本（catalog §4 风险 11）：整树多轮重编译 + restore/build/test 拉长链耗时 | record-readonly 同模式已有代价 | 沿用指纹/缓存；verify 可关；Analyzer 一次建表；无命中工程零触碰（clean 字节复制） |
| R10 | E04 与 C05 对同一表达式的二次改写相互作用（E04 产物 cast 形态假设） | E04 输出 `(U)e`/`(E)i`/lifted 三元 | 双 pass 组合单测（§7.1/7.3）；C05 以原树符号为准（E04 产物在同一 compilation 内仍可正确绑定）；M3 专项 |

---

## 11. 并行协调

| 相邻工作 | 边界 | 合并顺序/依赖 |
|---|---|---|
| **E04 `enum`（已实现）** | E04 表达式使用点；C05 声明 + 表示形态用点；两者都会见 `(U)e` 形态——E04 生产、C05 消解为 `.Value` | C05 RunAfter=`enum`（已登记），不改 E04；共享事实函数以复制/提取不破坏 E04 契约为限（E04 29 单测必须持续绿） |
| **C15 checked/unchecked + C34 decimal（numeric-boundary，RunAfter unsigned）** | 枚举窄化截断的 unchecked 包裹由 C05 按 D14 自产；C15 统一 checked 上下文归一；C34 与 enum 无直接交叠 | 顺序上 C05 远早于 numeric-boundary，无语法面争抢；C15 落地时把 C05 产物视同既有 unsigned 产出的 unchecked 形态（E04 已在产） |
| **C17 自增目标归一** | `e++/e--/e+1` 不在 C05；C17 把 enum 目标降为读-改-写 | C17 在 numeric-boundary（更晚）；C05 提供的 `FromValue/Value` 是 C17 enum 分支的目标形态；C17 落地前这些站点维持转换器 fail-fast（[UnaryExpressionTransformer.cs](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/UnaryExpressionTransformer.cs#L181-L182)），不属 C05 回退 |
| **C22 可空值类型提升** | C05 后 `E?` 是普通 `Nullable<struct>`；lifted 位运算已由 E04 处理；比较/转换/GOV 留 C22 | C22 通用规则对 Nullable<E> 自然生效，无需 enum 特例；与 §3.3 enum 族成员关系不变 |
| **C03 pattern-desugar / C37 表达式模式（P0）** | C03 把 pattern switch/非整型 switch 降 if-链；C05 只对**被本 pass 改写类型**的常量 switch/常量模式做最小降级（中间树当场可编译所需）；关系/属性/位置模式归 C03/C37 | 两 stub 均锚 `null-coalesce`；C03 落地时注册表需补「`pattern-desugar` RunAfter `enum-declaration`（或反向边，评审定）」——倾向 C05 在前（C03 见到的已是 struct if-链/残余模式），避免两 pass 争同一 SwitchSyntax；goto case 在 C03/E14 之前由 ED020 拦截 |
| **E10 generic / C02 generic-deepen** | enum 作泛型实参/数组元素须在 generic 之前完成（catalog C05 注记） | C05 链位已早于 generic；`: Enum` 约束交互点由 ED020 盘点，generic-deepen 落地时共享判定 |
| **C11-C14 成员去糖（member-desugar）** | C05 生成的是普通方法/属性/字段，恰是成员去糖的**目标形态**而非输入；晚于 C05 的成员 pass 不应再改写生成 struct（生成代码在 plan 标注/命名空间可识别） | member-desugar RunAfter struct/closure（远晚）；约定：后续 pass 对带 `Cs2jEnumDeclarationRewrite` 标注产物幂等跳过（标注落盘策略与 JK/RR 一致：仅语法标注，不进产物文本） |
| **C20 partial-merge（观察项）** | enum 不可 partial，无交集 | 无 |
| **C08/E17 java-keywords（链尾）** | 生成成员名（Value/FromValue/Parse…）非 Java 保留字；被改写枚举的成员名若撞 Java 保留字，由 C08 在链尾统一改名（包括生成 struct 的字段名——C08 的全符号改名表覆盖字段种类） | C05 在前、C08 在后；nameof 策略互不冲突（§4.5 规则 9） |

---

## 12. Worktree 分配

- 分支：`feature/preproc-c05-enum-declaration`
- 目录：`.worktrees/preproc-c05-enum-declaration`
- base：`67477551`（`feat(preprocessing): register 23 merged-family stub passes`，已核实存在：`67477551d74cbfc398204bcdabe000e4f881ad30`）

```powershell
git worktree add .worktrees/preproc-c05-enum-declaration -b feature/preproc-c05-enum-declaration 67477551
```
