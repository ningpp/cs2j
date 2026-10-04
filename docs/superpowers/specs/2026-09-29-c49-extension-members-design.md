# C49 C# 14 扩展成员归一设计：extension 块 → 经典静态类成员

**日期**: 2026-09-29
**范围**: catalog C49（P1，`.NET 10` 目标基线直接命中）：顶层非泛型静态类中的 `extension(ReceiverType receiver) { … }` 块——实例扩展属性、扩展运算符、静态扩展方法/属性、块/成员两级泛型——的声明侧归一与调用点显式化；扩展索引器（C# 15 预览）显式拒绝归 C52
**归属**: C19 `extension-methods` pass 的**声明侧规则族**（catalog 名称 `ExtensionMemberNormalize`；规则前缀 EMD，Extension Member Declarations），不新增 stub、不新增 pass、不新增中间目录；本文是 [2026-09-29-c19-extension-methods-design.md](file:///d:/code/cs2j/docs/superpowers/specs/2026-09-29-c19-extension-methods-design.md) 的姊妹设计
**状态**: 设计定稿；实施中（独立 worktree，见 §12；2026-10-04 起按 M1/M2/M3 分批由 Sub Agent 实现、分批提交），本文不改动转换器代码
**关联资料**: [preprocessor-catalog.md](file:///d:/code/cs2j/prompts/preprocessor-catalog.md) C49（L953-966）/C19（L530-542）/C11（L419-431）/C14 对齐 /C52（L997-1010）；[preprocessor-foundation_plan.md](file:///d:/code/cs2j/.trae/documents/preprocessor-foundation_plan.md) L60-139；[PassStubCatalog.cs](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassStubCatalog.cs#L180-L206)；官方语言参考 https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/keywords/extension 、 https://learn.microsoft.com/en-us/dotnet/csharp/whats-new/csharp-14 ；特性规范 csharp-14.0/extensions

---

## 1. 背景与目标

### 1.1 C# 14 extension 块是什么

C# 14 起，顶层、非嵌套、**非泛型** `static class` 内可用 `extension` 块成组声明扩展成员（官方语言参考与 csharp-14.0/extensions 规范，2026-09-29 WebFetch 已核）：

```csharp
public static class Enumerable
{
    // 实例扩展：receiver 具名，成员"看起来像"接收者实例的成员
    extension<TSource>(IEnumerable<TSource> source)
    {
        public bool IsEmpty => !source.Any();                       // 扩展属性
        public IEnumerable<TSource> Where(Func<TSource,bool> p){…} // 块内实例扩展方法
        public static IEnumerable<TSource> operator +(             // 扩展运算符
            IEnumerable<TSource> l, IEnumerable<TSource> r) => l.Concat(r);
    }

    // 静态扩展：receiver 可无名，成员"看起来像"接收者类型的静态成员
    extension<TSource>(IEnumerable<TSource>)
    {
        public static IEnumerable<TSource> Generate(int low, int n, int d) { … }
        public static IEnumerable<TSource> Identity => Enumerable.Empty<TSource>();
    }
}
// 调用形态：
var xs = numbers.IsEmpty ? … : numbers.Where(x => x);          // x.P / x.M(...)
var ys = new[] { 1 } + new[] { 2 };                             // 用户定义运算符决议
var zs = IEnumerable<int>.Generate(0, 10, 1);                   // 接收者类型名限定
var id = IEnumerable<int>.Identity;                             // 类型名限定静态属性
```

官方已核的关键语言事实（全部写入本设计规则依据）：

1. **成员种类**：方法、属性、运算符（C# 14）；**索引器自 C# 15 预览起**（官方页 "Extension indexers" 节，规范 csharp-15.0/extension-indexers）；语法上块内**无字段、无常量、无事件、无嵌套类型**（`extension_member_declaration` 仅 method/property/operator）。
2. **同一静态类内（含多个 extension 块）签名必须唯一**；生成签名把 receiver 类型（静态成员）或 receiver 参数（实例成员）计入。
3. **泛型参数位置规则**：receiver 使用 → 声明在块上；仅成员使用 → 声明在成员上；两处不得同名。
4. **静态扩展以接收者类型名限定调用**（含泛型类型名 `IEnumerable<int>.Generate(...)`）。
5. **与经典 `this` 参数扩展方法 IL 相同、源兼容、二进制兼容**——官方原文："Both forms of extension methods generate the same intermediate language (IL) … binary and source compatible"。这是本条目"改写回经典形态即最简物化路径"的语言层保证。
6. 同一类中 extension 块与经典扩展方法、普通静态成员可共存；一个块可含任意数量成员，一个类可含任意多个块。
7. receiver 类型可为非泛型、开放泛型、封闭泛型；仅静态成员的块 receiver 允许省略参数名。规范 grammar 中 `receiver_parameter : attributes? parameter_modifiers? type identifier?`——receiver 的 `ref/in/scoped/readonly` 允许集与指针 receiver 限制集为 **M0 探针项**（§4.6/§7），不凭记忆写规则。

### 1.2 现状：转换器与预处理链对 extension 块零认知

| # | 事实 | 证据（2026-09-29 只读核实） |
|---|---|---|
| 1 | 全 src grep `ExtensionBlock`/`ExtensionMember` **零命中**；无任何 C# 14 扩展成员识别代码 | 仓库 grep |
| 2 | 既有扩展索引只扫经典 `this` 形态：[ExtensionMethodIndex.cs](file:///d:/code/cs2j/src/CSharpToJava.Core/Pipeline/ExtensionMethodIndex.cs) 全文 199 行，`root.DescendantNodes().OfType<MethodDeclarationSyntax>()` + 首参 `SyntaxKind.ThisKeyword` + `IsExtensionMethod` 符号确认；对块内成员无发现路径 | L151-196 |
| 3 | 属性链路只认识类内 `PropertyDeclarationSyntax`：[PropertyTransformer.cs](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Member/PropertyTransformer.cs#L7-L13) 入口 `node is not PropertyDeclarationSyntax p` 即抛；块嵌套的属性即使被枚举也无 host 类型可挂后备字段；调用点 `x.P` 走普通成员绑定，对扩展属性将解析失败或静默错绑（C49 catalog 已登记的事故面） | L7-13 |
| 4 | C19 设计把 C# 14 扩展块定为 **CS2J1206 Error**（EM-EXT14，[C19 设计 §2/§6](file:///d:/code/cs2j/docs/superpowers/specs/2026-09-29-c19-extension-methods-design.md)）：`v1 仅支持经典 static class + 首个 this 形参`。本任务把该报错**升格为物化**，1206 退为"规则关闭/遇到不支持子集"时的码 | C19 §2 EM-EXT14、§6 码表 |
| 5 | 注册表已有 `extension-methods` stub（catalogItems `"C19"`，RunAfter 五锚），C49 与其一体；`linq-channel` stub 的 RunAfter 引用该 id | [PassStubCatalog.cs:180-206](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassStubCatalog.cs#L180-L206) |
| 6 | Java 无扩展成员任何形态（无扩展属性/扩展运算符/类型静态扩展语法） | catalog C49 |

四类调用点在现状链路上的具体失败形态（C49 不实施时）：

| 调用点 | 现状解析路径 | 失败形态 |
|---|---|---|
| `x.P`（扩展属性读） | MemberAccess → 普通属性/字段绑定；receiver 类型上无此实例成员 | 绑定失败兜底或静默错绑同名成员；[ProjectExtensionMethodCheckPass](file:///d:/code/cs2j/src/CSharpToJava.Core/Pipeline/Passes/ProjectPasses.cs#L740-L830) 只查 `MethodKind.ReducedExtension`，属性站点不在其告警面，**连 CS2J_EXT001 都收不到** |
| `x.P = v` / `x.P += 1` | 赋值左路普通属性查找 | 同上，且复合赋值会错误走实例字段/属性配方 |
| `a + b`（绑到扩展运算符） | BinaryExpression 走原语/用户运算符渲染；源内运算符声明在块内、OperatorTransformer 看不到 | 用户运算符解析落空，产出 receiver 类型上不存在的运算/方法 |
| `IEnumerable<int>.Generate(...)` | 左部为类型符号的 MemberAccess invocation；普通静态成员查找 | 接收者类型上不存在该静态方法 → 绑定失败 |
| 声明侧 | IMemberTransformer 按类成员派发，ExtensionBlockDeclarationSyntax 不是已知成员节点 | 整块及其成员**静默不产出**（无 fail-fast、无占位），是最危险的一类——能走完转换但 Java 缺类成员 |

### 1.3 ★先决核实结论：Roslyn parse 层（全条目第一风险）

工程现状（[CSharpToJava.Core.csproj](file:///d:/code/cs2j/src/CSharpToJava.Core/CSharpToJava.Core.csproj#L12-L14)、[conditional-plan-validation.csproj](file:///d:/code/cs2j/tools/conditional-plan-validation/conditional-plan-validation.csproj#L10)）：

| 项 | 现状 | 结论 |
|---|---|---|
| `Microsoft.CodeAnalysis.CSharp` / `.Workspaces` / `Workspaces.MSBuild` | **5.0.0**（Core 三处同版；tools 一处 5.0.0；2026-09-30 由 4.12.0 升级，提交 3c0d37bd） | 见下版本对照 |
| TargetFramework | `net10.0` | 运行时满足，无需改 |
| 工程 LangVersion | Core/CLI 不设（由 parse options 控制）；支撑库 latest/13/11 不等 | 与 parse 先决无关 |
| 全部 parse 入口 | 统一 `CSharpParseOptions(LanguageVersion.Latest)`（[ProjectCompilationBuilder.cs:30](file:///d:/code/cs2j/src/CSharpToJava.Core/Pipeline/ProjectCompilationBuilder.cs#L30)、各 pass 适配器、Program.cs 两处） | 升级包后 Latest 自动取 C# 14 |

版本对照（xUnit 探针实测 + Microsoft Learn 官方页核实，2026-09-30；**实测推翻了本文档早先"4.14 即 C# 14 GA"的假设**）：

| Roslyn 包版本 | VS 代 | C# 14 extension 块 |
|---|---|---|
| **4.12.0** | VS 2022 17.12（2025-01） | **不支持**：extension 块语法解析尚未进入编译器；`extension(…){…}` 在类成员位置按标识符解析失败（CS1001/CS1519 类语法诊断），**.NET 10 源码在 parse 层即全量失败** |
| 4.13.x | VS 2022 17.13 | 同为 17.14 之前，不支持 |
| **4.14.0** | VS 2022 17.14 / .NET 10 | **探针实测：extension 块在 4.14 中仍是预览特性，并非 GA**——`LanguageVersion` 枚举最高只到 **CSharp13（无 CSharp14）**；`extension(…){…}` 在 `Latest` 下 **16 个语法错误**，仅 `LanguageVersion.Preview` 下 0 错误；预览节点类名 **`ExtensionBlockSyntax`**，SyntaxKind=`ExtensionBlockDeclaration` |
| **5.0.0（现 pin）** | Roslyn 5.x 新一代稳定线 | **C# 14 GA 载体（实测）**：`LanguageVersion.CSharp14` 存在；extension 块在 `Latest` 下 **0 错误**；GA 节点类名 **`ExtensionBlockDeclarationSyntax`**，SyntaxKind=`ExtensionBlockDeclaration`（另有 ExtensionKeyword/ExtensionMemberCref）；形状属性 TypeParameterList/ParameterList/ConstraintClauses/Members 全部存在。NuGet 另有 5.3.0/5.6.0/5.9.0 可选，本期取 5.0.0 |

**先决结论（M0 已执行）**：

1. 四个 PackageReference 已升级到 **5.0.0**（三个 Core 包与 tools 一个包同版，不混版；提交 3c0d37bd）。不需要改 VS/构建工具——NuGet 包自带编译器。
2. 升级后 API 形态以探针实测为准：GA 节点类名 **`ExtensionBlockDeclarationSyntax`**（4.14 预览期曾用 `ExtensionBlockSyntax`，勿再引用旧名），含 TypeParameterList/ParameterList/ConstraintClauses/Members；后续代码按实测形状编码。
3. 启动期 parse 自检：用最小片段 `static class E { extension(int x) { public int P => x; } }` 在 `LanguageVersion.Latest` 下 parse，若节点树中无 extension 块节点或含语法错误诊断 → **CS2J1213 Error**："Microsoft.CodeAnalysis ≥ 5.0 required to parse C# 14 extension blocks"，本规则拒绝运行（不是静默 no-op）。
4. Roslyn 4.12→5.0 的升级回归已按 M0 套跑（Tests 2102 / ProjectTests 592 / ConversionTests 671 全绿）。实测发现两处语法形状变化并已适配：① extension 块（见上）；② **空条件赋值升格为正式语法后，赋值节点内嵌于 `ConditionalAccessExpression.WhenNotNull`**（旧形态是 AssignmentExpression 左路包 ConditionalAccess），RecordReadOnly 通道 MS-5 已同步修复。这是 C50/C51 共用的"Roslyn 版本先决批"，C49 不独占升级动作但负责驱动。

### 1.4 目标

1. 在 C19 `extension-methods` pass 内新增**声明侧规则族 EMD**：整树一次编译，把每个 extension 块就地改写为其所在静态类中的经典成员（属性→`get_X/set_X` 静态方法对、运算符→C14 命名表静态方法、实例方法→经典 `this` 参数静态方法、静态成员→普通静态方法/属性归一对），调用点同步显式改写为 host 全限定静态调用；产物与 C11/C12/C14/C19 的目标形态逐一对齐，**下游转换器与兄弟 pass 零特判**。
2. SemanticModel 是唯一真相：成员归属、receiver、泛型实参、静态扩展类型名限定解析、运算符决议全部消费 Roslyn 绑定，不做名字/形状猜测。
3. C# 15 扩展索引器与一切探针确认不支持的子集：**显式诊断拒绝**（联动 C52 防线），禁止注释/占位/静默透传。
4. 不新增 pass/stub/中间目录/支撑库；不修改转换器代码；默认关，随 C19 与 .NET 10 基线节奏开门（§8）。

---

## 2. 关键设计决策表

| # | 决策点 | 结论 | 依据/备注 |
|---|---|---|---|
| 1 | **总体物化策略** | **源到源改写回经典形态**：extension 块在原静态类中原地展开为经典静态成员；与经典 `this` 扩展方法 IL 相同、源兼容（官方已核） | 这是全表最底层决策；Java 侧不存在任何扩展概念，改写后全部成为普通静态/实例绑定 |
| 2 | **调用点是否需要同步显式改写** | **方案 A（采纳）：声明改写 + 调用点同步显式改写，两侧计划均来自改写前同一 SemanticModel；二次编译只用于验证**。方案 B（仅声明改写、靠二次编译让调用点自动重绑）否决：块内方法的 receiver 名/块泛型参数在改写后消失，Roslyn 是否对属性/静态限定/运算符各调用形态逐位重绑到同名生成成员需逐形验证，验证成本高于显式改写，且一次编译失败即不可归属 | A 与 C19 EM-BIND/C11/C14 的"先计划后纯执行"同构，回退归属简单；§4.6 同一性断言兜底 |
| 3 | ① 实例扩展属性 | `int P { get; set; }` → 同 host 类中 `public static int get_P(ReceiverType r)` + `public static void set_P(ReceiverType r, int value)`；表达式体/get-only 同 C11 规则只生成存在的一侧；调用点 `x.P`→`global::Host.get_P(x)`、`x.P=v`→`global::Host.set_P(x,v)`、复合/`++--` 按 C11 读写配方展开。**命名严格用 C11 决策 1 的 C# 规范名 `get_X/set_X`（小写前缀带下划线），不用 Pascal `GetX/SetX`**——catalog 措辞 GetP 是简称；C11 已论证 `get_X` 槽位天然零碰撞而 `GetX` 会撞用户方法 | 与 C11 产物同形，C11 翻默认开后的"Java 半区消费改造"（get_X→getX 派生）自动覆盖本族 |
| 4 | ② 扩展运算符 | 块内 `public static R operator +(A a, B b)` → host 类普通静态方法 `public static R Add(A a, B b)`；一元/比较/转换（`implicit/explicit`→`ToR`/`ToRExplicit`）/`checked`（`Checked` 后缀）**逐名复用 C14 命名镜像表与 SB 站点配方**；调用点 `a+b`→`global::Host.Add(a,b)`，转换/复合赋值/提升形态同 C14 规则。**注意链序**：member-desugar（C14）在 Declarative 阶段、先于本 pass，不会二次处理新生成成员——故 C49 必须在本 pass 内直接产出 C14 **最终形态**而非"经典 operator 声明"，C14 的 OperatorFacts/命名表须作为可复用 Core 组件被引用（§11） | 任务书"经典静态 op 方法（并入 C14 调用点物化）"的落实方式；运算符声明位置保持在原块处 |
| 5 | ③ 静态扩展方法/属性 | 块内 `static M(...)` → host 类普通静态方法 `M(...)`（无 receiver 参数）；静态属性 → `get_X()`（/`set_X`）普通静态方法对（C11 同形）。调用点**接收者类型名限定** `IEnumerable<int>.Generate(args)` → `global::Host.Generate(args)`、`IEnumerable<int>.Identity` → `global::Host.get_Identity()`。绑定判据：MemberAccess 的左侧表达式经语义模型是**类型符号**且所绑成员 `IsExtension`（API 名 M0 探针确认） | 不生成 receiver 形参；泛型类型名实参（`IEnumerable<int>`）在改写后消失，由方法自身泛型推断/显式实参表承接（EMD-GEN） |
| 6 | ④ 块内实例扩展方法 | 块内 `R M(T a)`（receiver 由块提供）→ 经典 `public static R M(ReceiverType __r, T a)`（带 `this` 修饰），receiver 形参名取块 receiver 原名（保持方法体引用零改写）或确定性合成名并同步替换方法体引用（M0 按符号可读性二选一，默认保原名）。**调用点交 C19 既有物化器**：Roslyn 4.14 下块扩展方法调用点绑定即 `MethodKind.ReducedExtension`（M0 确认），C19 EM-BIND/EM-CALL 不改即覆盖；EMD-METHOD 只补声明侧 | 与任务书⑤完全一致；声明改写与调用点改写同 pass，计划均在原 compilation 上取得 |
| 7 | 成员种类全集 | 支持：方法（实例/静态）、属性（实例/静态，get/set/init?/表达式体）、运算符（静态）。init 访问器跟随 C11 决策 4 归约为 set。**拒绝并显式报码**：索引器（C# 15 预览，CS2J1209）、字段/常量/事件/嵌套类型（C# 14 grammar 不允许，若探针发现可解析形态同样 CS2J1209）、`partial extension`（grammar 无 partial，M0 探针钉死，若未来预览加入则 CS2J1209 而非静默） | 常量问题在任务书中为开放项；结论以 grammar 为准、探针兜底 |
| 8 | 泛型：块上参数 | **映射到每个生成成员的方法类型参数表**（不是静态类泛型——扩展方法容器法定为非泛型静态类，官方经典等价示例即 `Spread<T>(this IEnumerable<T> source,…)`）：块参数按块上声明序前置于成员自身参数；约束（含 `where`）合并平移到方法；静态成员同样携带（静态扩展 IL 本就是带类型参数的静态方法） | 经典扩展方法"静态类可泛型"的假设不成立：含扩展方法的静态类必须非泛型；M0 用官方 GenericExtensions 样本钉死 |
| 9 | 泛型：成员上参数 | 保持为方法类型参数，接在块参数之后；两处同名本就被编译器拒（规范 scoping），输入可编译即无碰撞 | 调用点泛型实参沿用 C19 EM-GEN 策略：源码省略则继续省略（让编译器从显式首参重新推断），源码显式则原样保留 |
| 10 | receiver `ref/in/readonly/scoped` | 规范 grammar 允许 `parameter_modifiers?`；逐成员映射为生成静态方法首参的同款修饰（属性 `get_P(ref Receiver r)` / `set_P(ref Receiver r, v)`；ref 写入语义经 setter 首参 ref 保住——具体哪些成员/访问器组合合法以 M0 探针矩阵为准，不合法组合由原编译拒绝，本规则不额外发明）；`scoped` 是逃逸元数据不进签名但保留在形参 | 不支持组合（如指针 receiver——经典扩展方法即有指针首参限制，M0 钉死允许集）→ CS2J1211 显式拒绝 |
| 11 | host 类与命名/可见性 | **不新建类、不新建文件**：成员全部生成进块所在的原静态类（同类多块合并进同类；host 可 partial，成员落在块所在源文件的该类部件中）；保持原 `public/internal` 可访问性与原命名空间；调用点一律 `global::` 全限定（复用 C19 ExtensionHostSyntaxFactory，FullyQualifiedFormat，禁止手拼字符串、禁止向源文件加 using） | 零 using 丢失/别名遮蔽问题；internal host 对同图调用点可见（merged compilation 同编）；metadata 侧 host 不可达分类沿用 C19 EM-EXTERNAL（1203） |
| 12 | 同类多块 / 多成员 | 各块独立展开、成员按源序插入原块位置；同签名冲突在**输入编译期**已被 Roslyn 拒（receiver 计入签名），改写后若重编译出现 CS0102/CS0111 级碰撞（理论只可能来自生成名映射缺陷）→ CS2J1212 并按 plan 回退 | residual 以重编译零新增错误为闸门 |
| 13 | 运算符决议 | 调用点分类只认真实绑定符号：Binary/Unary/Conversion/Compound 操作绑定到 `MethodKind.UserDefinedOperator/Conversion` 且声明语法引用位于 ExtensionBlockDeclarationSyntax（M0 确认符号判据）才选中；原语运算、metadata 运算符、接口 static abstract 运算符不选（沿用 C14 SB-Metadata/SB-InterfaceStatic 归属与码段） | 不重新实现候选决议；C14 的 SB-JK-Bool/SB-IncDec/SB-GateAndOr 排除表原样继承 |
| 14 | LINQ 边界 | 块内扩展方法若被 LINQ 链以 reduced 形态调用，继续归 C19 **EM-LINQ 豁免集**（Enumerable/Queryable host 不重写）；C49 声明改写不区分用途，豁免只作用于调用点选择器 | 与 C19 §3.4 结论一致；混合链策略继承 C19 |
| 15 | 表达式树 | 扩展属性/运算符调用点位于 `Expression<>` lambda 内：**默认整 plan 跳过**（成员访问/运算符节点→MethodCall 会改变表达式树形态），口径同 C11 决策 12/C14 CS2J5173；扩展方法（方法调用形态）按 C19 EM-EXPRTREE 可改写 + 同一性断言 | overrides.force 解除时风险用户自担 |
| 16 | 幂等 | 产物中无块、无扩展糖：经典 `this` 方法/普通静态方法/get_X/Add 全部不是扩展语法，重复运行零 diff；residual 零 ExtensionBlockDeclarationSyntax 断言锁死 | EMD-IDEAL |
| 17 | CS2J1206 过渡 | 选项 `MaterializeExtensionBlocks`（默认 **false**）：false 时维持 C19 现状——遇块即 CS2J1206 Error；true 时按子开关物化，不支持子集走 1209/1211。**不用 `--strict` 承载行为差异**（strict 只升级 Warning），旧行为由显式策略开关保留，便于灰度与回滚 | 默认开评审（M3）前所有环境行为 = 今天 |
| 18 | 与 member-desugar 的门控 | 属性/运算符子规则物化产物依赖 C11/C14 转换器消费链（get_X→getX 派生、op 名映射）；启用 EMD-PROP/EMD-OP 时若 member-desugar 不在本次启用 pass 集且转换器消费改造未合入 → **CS2J1208 拒绝该子规则**（不是产出不可消费 Java）。EMD-METHOD/EMD-STATIC 无此依赖 | C11 设计 §2 决策 1b 已把 Java 半区消费改造列为其开门禁；C49 不提前透支 |
| 19 | nameof/反射/动态 | 块内成员的 `nameof` 处理跟随 C11 决策 11（替换为原名常量）；dynamic 不发生扩展绑定，不选中；反射按名面限制声明同 C11/C14 | 不新增风险面类别 |
| 20 | XML doc / attribute / trivia | 块节点上的 XML doc（官方风格：编译器把块节点 doc 复制到每个成员）与块上 attribute 的分发：成员级 doc/attribute 随成员平移；块级 doc 复制到每个生成成员（对齐编译器行为）；块级 attribute 若 M0 探针证明存在且目标为方法，则平移到每个生成方法，否则 CS2J1211 同族报告；leading trivia 跟随首个生成成员 | 官方 extension 页 Note 明确 doc 复制语义 |
| 21 | 可访问性 | 生成成员取块内成员原修饰符；成员缺省访问修饰符时按 C# 类成员默认 `private` 处理（extension 块成员默认访问级以 M0 探针钉死——规范倾向与类成员一致）；host 类自身可见性不动 | internal/嵌套场景经 merged compilation 直接验证 |
| 22 | marker 类型丢弃 | Roslyn 对扩展块向 metadata 发射"扩展标记类型"（CLR equivalency group，规范 Lowering 节）；源到源改写为经典形态后该合成类型**自然不存在**——这是官方"源/二进制兼容、IL 等价经典形态"的另一面，无需模拟 marker | 重编译后反射面变化（按扩展 marker 的反射代码）登记为 C#14 固有面，同 C11 反射限制声明 |
| 23 | async / yield / unsafe / unchecked 方法体 | 成员方法体原样平移进生成静态方法：`async`/`yield`/`unsafe` 修饰符随方法声明保留，方法体内 receiver 引用改名规则同形参重命名；`checked` 运算符的命名后缀归 C14 表；块成员中的本地函数/lambda 捕获 receiver 等价捕获首参，语义天然保持 | 不为这些形态做额外物化；C04/C18/C45 在后续链位照常消费 |
| 24 | 与 host 类既有成员撞名 | 生成名（含 receiver 签名）若与 host 类既有经典成员碰撞：可编译输入在 C# 语义下本就允许（receiver 计入签名的重载），重编译以 CS0102/CS0111 判定；撞名且签名相同意味着源程序非法（编译器已拒）。生成器不做额外避名，碰撞即 CS2J1212 回退 | 与决策 12 互证；不引入 C11 后备字段式防撞命名 |
| 25 | NRT 与可空上下文 | `#nullable` 指令、receiver/返回类型 `?` 注解随语法平移；扩展属性 get/set 拆分不改变可空性；块/成员泛型约束中的 `notnull`/`class`/`struct` 随约束平移 | NRT 注解本体的词法剥离归 C28/C52，本规则不代行 |

---

## 3. 注册表与链位契约

### 3.1 不新增 stub、不新增 pass 的论证

1. C49 与 C19 共用同一语义事实（扩展成员绑定）、同一 compilation、同一中间目录与 verify 双开、同一 host 限定工厂；拆成独立 pass 意味着多一环一 restore、两次全树编译、两个 stub 计数与链序锁，收益为零。
2. C19 设计已把 EM-EXT14（CS2J1206）与"C# 14 探针/是否另注册观察 stub"登记为自身开放问题（Q4/M5），C49 是该问题的正解而不是并列条目。
3. foundation plan 的 pass 注册表是活清单但不为同族子规则开 pass（C50 入 C11、C51 入 C35 同理）。

### 3.2 挂载方式与 catalogItems

- C19 翻转时 [PassCatalog.cs](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassCatalog.cs) 描述符 **`CatalogItems` 建议文本为 `"C19, C49"`**（本设计的推荐值；`--list-passes` 同列展示）。
- 若实施排期上 C19 先合、C49 后补，则 C49 以**同 pass 内规则开关**落地（§4.1 Options），翻转 C19 时 catalogItems 可先写 `"C19"`，C49 合入时改 `"C19, C49"`——一处文本改动，无注册结构变化。
- `Id`/目录/Stage/RunAfter/verify 等全部沿用 C19 §3.1 描述符，不新增 `PassSupportLibrary`。

### 3.3 链位结论：五锚 + EM-ORDER 两锚已充分，不追加

| 依赖面 | 结论 |
|---|---|
| 五锚（delegate/tuple/ref-out-in/unsigned/array-method） | 原样保留（可见性不变量，同 C19 §3.2） |
| `make-readonly` / `iterator-lowering`（C19 EM-ORDER 建议追加） | 保留追加：E13 的 reduced 启发式、迭代器展开产新站点都在本 pass 前完成；**C49 不引入新锚**——声明侧改写对象（extension 块）不可能被 E13/E14 产生或消费（grep 零命中、语法新），struct receiver 的只读化交互在 E13 阶段已定型，C49 只平移其结果 |
| member-desugar（C11/C14，Declarative） | Stage 序天然在 Expression 之前；其分析器须**跳过 extension 块内声明**（`node.FirstAncestorOrSelf<ExtensionBlockDeclarationSyntax>() != null` 或符号 `IsExtension` 判据），把成员留给 C49（§11，C11/C14 落地时的硬 checklist） |
| E17 java-keywords | 仍在链尾：生成的 `get_X/set_X/Add/…` 标识符若撞 Java 关键字由 JK 对普通静态方法/调用点的既有规则覆盖；C49 生成节点不引入 `__cs2j_` 新标识，无新增碰撞面 |

落点 = C19 落点：`iterator-lowering` 之后、`eliminate-goto` 之前；规则在 pass 内的执行序见 §4.5。

### 3.4 规则门控

- `extension-methods` pass 总开关（默认关，过 §8 翻）。
- pass 内 C49 子开关：`MaterializeExtensionBlocks`（默认 false）+ `EnableExtensionMethods/Properties/Operators/StaticMembers`（M1/M2 分批，默认随里程碑）。
- 子规则依赖检查（1208）、LINQ 豁免不可删空（继承 C19）、C52 联动拒绝不可关（索引器永远 1209）。

---

## 4. Core 组件设计

目录：挂在 C19 Core 目录下的声明侧规则组 **`src/CSharpToJava.Core/ExtensionMethodResolve/Declarations/`**（任务书所称 `ExtensionDeclarations/` 规则目录；与 C19 调用点代码共享 binder/编译/工厂，故为子目录而非并列目录），命名空间 `CSharpToJava.Core.ExtensionMethodResolve.Declarations`。

### 4.1 文件清单

| 文件 | 职责 |
|---|---|
| `ExtensionBlockOptions.cs` | 规则 record：`MaterializeExtensionBlocks`、`EnableMethods/Properties/Operators/StaticMembers`、`Strict`、`ReportInfo`、`RequireMemberDesugarChain`（默认 true，1208）、`ExpressionTreePolicy`（默认 Skip）、索引器拒绝不可关 |
| `ExtensionBlockStatistics.cs` | `BlocksScanned/BlocksRewritten`、`MembersEmittedByKind`（Method/PropertyAccessor/Operator/StaticMethod/StaticAccessor）、`PropertyGetSites/PropertySetSites/OperatorSites/StaticQualifiedSites`、`GenericBlockArityMappings/RefReceiverBlocks`、`IndexersRejected/UnsupportedConstructs`、`SkippedByReason`；经 C19 适配器并入通用统计 |
| `ExtensionBlockDiagnostics.cs` | EMD 码表（§6），与 C19 同码段 CS2J12xx，只追加 |
| `ExtensionBlockSyntaxFacts.cs` | 反射/语法事实层（M0 产出）：识别块节点（不硬编码类型名，按 `SyntaxKind`/反射名双重判定）、枚举块成员、取块 TypeParameterList/ParameterList/ConstraintClauses、判定节点是否位于块内、判定索引器节点（C#15 预览）；Roslyn 版本不足由启动探针（CS2J1213）拦截 |
| `ExtensionBlockBinding.cs` | `record ExtensionBlockPlan(...)`：块节点、host 类型符号、receiver 参数符号（含 RefKind）、块类型参数表、成员计划列表（逐成员：种类/声明符号/目标名/目标签名/访问器拆分）；`ExtensionMemberPlan` 为多态（Method/Property/Operator/Static） |
| `ExtensionBlockAnalyzer.cs` | 整树分析：枚举块 → 符号校验 → 逐成员产出计划；同步枚举全树调用点（属性读/写/复合、运算符四类操作、类型名限定静态访问）并与计划配对；不支持子集在此落诊断（1209/1211）；与 C19 `ExtensionCallBinder` 共享 SemanticModel 遍历（一次 Analyze 两套计划） |
| `GenericParameterMapping.cs` | 块参数→成员方法参数表的合并算法（EMD-GEN）：块参数前置于成员参数、约束合并平移、调用点实参表省略/保留判定（复用 C19 EM-GEN 原则） |
| `ExtensionBlockDeclarationRewriter.cs` | 块节点 → 零到多个经典成员声明（就地替换块节点；trivia/XML doc/attribute/修饰符平移；属性访问器体中 receiver 引用保持原名）；属性拆分严格调 C11 命名契约（`get_X/set_X`）；运算符命名调 C14 `OperatorMemberNaming`/命名镜像表（以公共组件方式引用，不复制表） |
| `ExtensionBlockCallSiteRewriter.cs` | 按计划纯执行调用点改写：限定名走 C19 `ExtensionHostSyntaxFactory` 的 `global::` NameSyntax；属性复合赋值/`++--` 的临时提升配方引用 C11 既有实现；运算符站点配方引用 C14 SB 表；重写期不做任何绑定推断 |
| `ExtensionBlockResidualScanner.cs` | AfterFiles：①零 `ExtensionBlockDeclarationSyntax`；②零未配对扩展调用点（属性/运算符/类型名限定）；③改写点重绑定同一性（§4.6）；④索引器拒绝计数与诊断配对；⑤幂等二次运行零 diff |

### 4.2 成员映射规格（速查）

| 源（块内） | 目标（host 类经典成员） | 调用点 |
|---|---|---|
| 实例方法 `R M(A a)` | `static R M(Receiver r, A a)`（`this` 修饰；块泛型前置于方法泛型） | C19 EM-CALL：`global::Host.M(x, a)` |
| 实例属性 `T P { get; set; }` | `static T get_P(Receiver r)` + `static void set_P(Receiver r, T value)`（init→set 同 C11） | `get_P(x)` / `set_P(x,v)`；复合/++-- 同 C11 |
| 静态方法 `static R M(A a)` | `static R M(A a)` | `global::Host.M(a)`（左为接收者类型名的 MemberAccess 重绑 host） |
| 静态属性 `static T P` | `static T get_P()`（+set） | `global::Host.get_P()` |
| `static R operator +(A,B)` | `static R Add(A a, B b)`（C14 全表；checked→`Checked` 后缀；implicit/explicit→`ToR`/`ToRExplicit`） | `global::Host.Add(a,b)`（C14 SB 配方） |
| receiver 修饰 `ref/in/readonly/scoped` | 首参同款（scoped 保留形参） | 实参加注 `ref`/`in`（同 C19 EM-REF） |

### 4.3 host 解析与元数据边界

- host = 块所在静态类符号（`block.Parent` 绑定的 `INamedTypeSymbol`）；嵌套泛型场景不适用（法定顶层非嵌套非泛型），但 host 自身可能嵌套在泛型类型中（与 C19 EM-GEN-HOST 同形，已构造宿主实参从符号取）。
- metadata 中声明的扩展成员（外部程序集，无源码块）：**声明侧无块可改**；调用点按 C19 EM-EXTERNAL 处理（方法形态已被 C19 覆盖；属性/运算符 metadata 形态 v1 报 CS2J1203 同族 Warning，待 M3 按 sdk 数据决定是否生成兼容声明）。

### 4.4 泛型映射规则（EMD-GEN）

1. 块参数表 `TBlock₁..TBlockₙ` 前置于每个生成方法；成员自身参数 `TMember₁..TMemberₘ` 接后；方法 arity = n+m，顺序固定（IL 等价经典形态即此序，官方 GenericExtensions 示例佐证）。
2. 约束 clauses 平移到对应参数；源中成员对块参数的引用零改写（名不变）。
3. 调用点：源省略实参则目标省略（`x.Spread(0,1)` → `Host.Spread(x,0,1)`，由显式 receiver 首参重新推断，与 C19 EM-GEN 同构论证）；源显式 `x.M<int>('a')` 实参表按 n+m 位置原样保留（M0 探针确认块/成员两级实参在语法上的书写形态，不可书写推断结果不物化）。
4. 静态扩展 `IEnumerable<int>.Generate(...)` 的类型实参服务于块参数推断；改写后 receiver 类型名消失，推断改为从普通实参/返回目标进行——探针必须覆盖"仅出现在 receiver 类型、成员签名其他位置不出现"的块参数（静态成员专属场景），若无法从新形态推断则该站点 CS2J1202 同族报告并保留原计划不重写。

### 4.5 同 pass 两阶段执行序

1. **Analyze（整树一次，原 compilation）**：C19 调用点绑定与 C49 块分析同轮完成，两套计划按文件+span 确定性排序；所有改写目标（host、名字、签名、配方）此刻全部定案。
2. **Rewrite（逐文件，单 rewriter）**：块声明节点与调用点表达式在树上互不相交，单轮 `CSharpSyntaxRewriter` 各走各的 Visit；块方法体内对其他扩展成员的调用同按计划改（计划来自原模型，不依赖改写后形态——决策 2 方案 A）。Changed=false 字节级复制（保 BOM）。
3. **Residual（AfterFiles 再编译）**：§4.6 闸门。
4. **回退**：沿用 C19 文件归属回退（出错文件即责任文件，块声明与其调用点跨文件时 plan 记录 TouchedFiles，按 C11/C14 plan 原子单位整组回退）；不可归属 → CS2J1200 全回退。

### 4.6 ★改写后符号同一性验证（plan verify 核心）

重编译中间树，对每个改写点断言：

1. 调用点重绑符号的 `OriginalDefinition` doc-id == 计划目标符号（属性 getter/setter、运算符目标方法、静态方法、reduced→unreduced 方法）。
2. 参数序列：receiver 首参类型/ref-kind、其余实参顺序、泛型 arity 与计划一致。
3. 声明侧：每个原块成员恰有一个生成成员配对；host 类成员集无意外增减（块外成员零触碰）。
4. 表达式树区内零改写（跳过计数配对）。
5. 全树 `ExtensionBlockDeclarationSyntax` 计数 = 0（C#15 preview 语言版本下另测索引器拒绝路径）。

任一失败：该 plan CS2J1210 同族回退（与 C19 表达式树回退码同段，新增 EMD 明细码 1222）。

### 4.7 声明改写细节

1. **块节点替换**：ExtensionBlockDeclarationSyntax 在 ClassDeclaration 的 Members 中被替换为 0..N 个成员声明；成员源序 = 块内源序，多个块各自就地展开不重排；块的分号/括号 trivia 按 C14 OD-1 声明替换构造同款处理。
2. **receiver 形参注入**：实例成员首参 = `[this] [ref/in] ReceiverType receiverName`；形参名默认沿用块 receiver 标识符（方法体零改名）；块 receiver 无名但含实例成员属源程序非法（编译器已拒）；仅静态成员的块不注入形参。
3. **属性拆分**：block-bodied get/set、表达式体、访问器可访问性、init 归约全部沿用 C11 PD 规则的实现口径（引用共享实现，不复制）；生成方法位于原属性位置，getter 先于 setter。
4. **运算符拆分**：引用 C14 `OperatorDeclarationRewriter` 的声明构造逻辑处理参数表/返回类型/体平移；差别仅在最终宿主位置（普通类成员，声明来源标注为 extension block）与首参无 `this`（扩展运算符经决议而非 receiver 实例查找）。
5. **静态成员**：去掉对 receiver 的引用合法性依赖（规范：静态成员引用 receiver 名即编译错误，除 `nameof`）；方法/属性签名不注入首参。
6. **格式化与校验**：改动区格式化→整文件格式化→原文三级（同 JK/RR/C02/C19）。

### 4.8 规则全集（EMD-x）

- **EMD-BLOCK**：块识别、host/receiver/块泛型绑定；不支持成员种类 → 1209。
- **EMD-METHOD**：块内实例方法 → 经典 this 静态方法（调用点归 C19 EM-CALL/EM-LINQ/EM-EXPRTREE）。
- **EMD-PROP**：实例扩展属性 get/set 拆分与读写/复合/++-- 调用点（命名/配方引用 C11）。
- **EMD-OP**：扩展运算符声明与四类操作站点（命名/配方引用 C14；排除表继承 SB-JK-Bool/SB-IncDec/SB-GateAndOr/SB-Metadata/SB-InterfaceStatic/SB-Tree/SB-Dynamic）。
- **EMD-STATIC**：静态方法/属性声明与"接收者类型名限定"调用点（含泛型类型名）。
- **EMD-GEN**：块/成员两级泛型参数与约束映射、调用点实参省略策略。
- **EMD-REF**：receiver ref/in/scoped/readonly 首参映射与实参加注。
- **EMD-DOC**：块/成员 XML doc 与 attribute 平移分发。
- **EMD-GUARD**：索引器（C#15）等拒绝面（1209/1211）、版本探针（1213）、门控（1208）。
- **EMD-IDEAL**：零块残余 + 幂等 + 重绑定同一性（§4.6）。

---

## 5. CLI 适配器与集成

1. **不新增适配器文件**：随 C19 [ExtensionMethodResolvePassAdapter](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/Adapters)（C19 设计 §5.1）扩展；构造参数增加 `ExtensionBlockOptions?`（null = 1206 旧行为）。
2. `CompilationReadyAsync` 中同一次 BindCompilation/Analyze 产出 C19+C49 两套计划；`RewriteFileAsync` 同一 rewriter；`AfterFilesAsync` 合并 residual。
3. **不产生新文件、不新增中间目录、不注入支撑库**：生成成员落在块所在源文件的原静态类中；verify build/tests 双开沿用 C19 描述符。
4. 开关面：通用 `--enable-pass extension-methods` 即达 C19 全集；C49 子规则经 pass options（v1 可先用环境/配置载荷，不新增 CLI verb）；`--list-passes` 展示 `C19, C49`。
5. 单文件 verb 模式：引用不全/版本不足分别 CS2J1290（继承）/CS2J1213。
6. 指纹：仍由注册表全集令牌承载（无新 pass）；C49 选项进 options 指纹片段（防灰度期串缓存）。
7. PassChainFactory：无新 case（C19 翻转时的 case 内承载）。

---

## 6. 诊断策略

沿用 C19 CS2J12xx 码段（只追加不改号），MSBuild 格式同 C19：

| 码 | 级别 | 含义 |
|---|---|---|
| CS2J1206 | Error（默认） | `MaterializeExtensionBlocks=false` 时遇 extension 块：**C19 旧行为**（v1 现状锁死）；规则开启后此码路径注销，仅保留给"块存在但全部子规则关闭"的配置 |
| CS2J1208 | Error | EMD-PROP/EMD-OP 启用但 member-desugar（C11/C14）消费链不在启用集/未合入：拒绝运行该子规则（决策 18） |
| CS2J1209 | Error（不分级） | 不支持的扩展成员构造：**扩展索引器（C# 15 预览，C52 联动码）**、块内字段/事件/嵌套类型等 grammar 外形态、未来预览中的 `partial extension` |
| CS2J1211 | Warning / strict Error | 探针确认不支持的 receiver 形态（如指针 receiver、不允许的 ref 成员组合），该块不改 |
| CS2J1212 | Warning / strict Error | 改写后重编译签名碰撞（CS0102/CS0111 归属到 EMD 计划），整组回退 |
| CS2J1213 | **Error（启动期）** | Roslyn 版本不足：parse 探针未发现 extension 块节点/存在语法诊断，提示升级 Microsoft.CodeAnalysis ≥ 5.0 |
| CS2J1222 | Warning / strict Error | residual 同一性断言失败（§4.6），plan 回退 |
| 复用 | — | 歧义 1201、绑定失败 1202、metadata 无落点 1203、ref 1204、表达式树 1210、residual 1220、verify 1230/1231、单文件 1290、EM001 Info，全部沿用 C19，EMD 明细进 Info |

原则：不可判定即显式诊断；C#15 防线只能响不能关；`--strict` 只统一升级，不承载功能开关。

诊断分发流程（分析期一次分类，重写期不再判定）：

1. parse 探针失败 → 1213（启动期，整个 pass 拒绝运行）。
2. 块节点存在但总开关关 → 1206（每块一条，Error；等同 C19 现状）。
3. 总开关开：逐成员过子开关与门控（1208）→ 成员种类白名单（1209）→ receiver 允许集（1211）→ 入计划。
4. 重写后重编译碰撞/同一性失败 → 1212/1222，plan 原子回退。
5. 每站点明细（种类/host/泛型/ref/跳过原因）走 Info（EM001 同族），仅 verbose 输出。

---

## 7. 测试计划

### 7.1 M0 探针测试（parse 先决，硬前提）

1. 升级 5.0.0 后 parse 探针：`LanguageVersion.Latest` 解析官方四类样本（实例方法/属性/运算符、静态方法/属性、块泛型/成员泛型）成功；记录节点类型名、属性形状与符号 API（`IsExtension`、访问器符号 host/名字、调用点 MethodKind、扩展运算符 ContainingType 与 marker 类型形态）。
2. `LanguageVersion.Preview` 下解析扩展索引器成功、`Latest` 下报语法错——钉死 CS2J1209 的双语言版本行为。
3. receiver 修饰矩阵：`ref`/`in`/`readonly ref`/`scoped` × 方法/属性(get/set)/运算符/静态成员的合法集；指针 receiver；无名 receiver 仅静态成员、有名 receiver 静态成员引用 receiver 名（规范禁止，除 nameof）。
4. 静态扩展推断边界（§4.4-4）：块参数仅出现在 receiver 类型的静态成员。
5. 4.12 兼容性反向验证（可选，CI 不保留）：探针失败文案 CS2J1213。

### 7.2 Core 单测（`tests/CSharpToJava.Tests/ExtensionBlockRewriteTests.cs`）

- 属性：get-only/set/表达式体/init→set/`a.ExtProp.B` 链式/复合赋值/`++--`/`?.` 与条件形态（按 C19 EM-COND 同策略）；生成名严格断言 `get_X/set_X`。
- 运算符：算术、比较、一元、implicit/explicit（命名 `ToR`/`ToRExplicit`）、checked 后缀、提升形式；与 C14 共处（同类中普通运算符 + 扩展运算符分别绑定）。
- 静态扩展：`IEnumerable<int>.Generate(...)`、`IEnumerable<int>.Identity`、嵌套泛型 receiver、using static/别名下可见性。
- 实例方法：块方法改写后被 C19 EM-CALL 选中（断言最终形态静态首参调用，而非中间 this 形态）。
- 泛型：块级/成员级/两级组合、约束平移、省略与显式实参、静态扩展块参数推断。
- ref/struct receiver（探针允许集内）。
- 同类多块（不同 receiver、含经典扩展方法共存）、partial host 类、块内多成员源序。
- 声明细节：块级/成员级 XML doc 平移、块级 attribute 分发、访问修饰符缺省、async/yield/unsafe 块方法体、静态成员非法引用 receiver（编译器诊断保持）。
- 撞名：host 类经典成员与生成成员 receiver 签名重载共存；与 host 类既有 `get_X` 用户方法的关系（C11 零碰撞论证在扩展场景复验）。
- 块方法体互调：一个块成员调用同块另一成员（属性/方法），改写后调用点显式化且单次求值保持。
- 拒绝面：索引器 1209、`partial extension`（按探针）、指针 receiver 1211、版本不足 1213、门控 1208、表达式树跳过。
- 同一性：每个用例重编译跑 §4.6 断言；二次运行零 diff。
- LINQ 豁免：块扩展方法在 Enumerable 链中零改写（计数）。

### 7.3 项目级 fixture

新增 `tests/SampleExtensionMembersSolution/`（Lib+App+Tests，**net10.0**，仿 SampleGenericDeepenSolution 以 Content 挂入 ProjectTests；另含一个 `<LangVersion>preview</LangVersion>` 的负样本工程，断言索引器被 1209 拒绝）：

- Lib：四类成员全覆盖、泛型两级、ref receiver、多块/partial host、与经典扩展方法共存。
- App：using 引入、类型名限定调用、链式、运算符运算、跨工程。
- Tests（中间树 xUnit 运行时语义）：扩展属性读写结果、运算符结果、静态扩展结果、null receiver（扩展方法语义继承 C19）。
- 断言：`result.Success`、统计计数、residual 五项全 0、`dotnet build -c Release`/`dotnet test -c Release` 全绿、clean 文件字节/BOM 保持。

### 7.4 串行与回归

- 与 C19 fixture（SampleExtensionMethodsSolution）同树：本任务实施时 C19 已合入（§12 前置），在其用例上加 C# 14 样本。
- 与 member-desugar 共处串跑（C11/C14 落地后）：块内属性/运算符不被 C11/C14 分析器误选、C49 产物被其消费链正确承接；C11/C14 未落地期间 EMD-PROP/EMD-OP 门控保持。
- E13/E14 样本：struct receiver 扩展属性在只读化后改写，结果不变。
- Release build/test 三测试工程全绿（含既有两份 ExtensionMethodTests 零改动通过——C49 不扫经典形态声明）。

### 7.5 CLI / residual / sdk

- 开关矩阵：默认（1206）/全开/仅方法/门控缺失 1208；`--list-passes` 显示 `C19, C49`。
- residual 闸门：fixture 与合成树零 ExtensionBlockSyntax；strict 下残余非 0 退出非 0。
- preview 渗入：合成 `<LangVersion>preview</LangVersion>` 工程（含 C#15 索引器）在 runner 固定 Latest 编译下产出语法诊断/1209，绝不误解析通过。
- .NET 10 目标源码（D:\csharp-sdk 只读）命中率扫描：块数、成员种类分布、调用点形态分布、1209/1211 站点数——回填 §8 开门禁。

---

## 8. 默认开启验收门槛

1. **M0 硬前提**：Roslyn 升级到 4.14.x 且探针全绿（parse/符号 API/限制集）；升级后既有 18 pass Release build/test 零新增回归（C50/C51 同批受益，回归责任在升级批）。
2. **C19 pass 先合入**：C49 不先于 C19 翻转；合入顺序 = C19 分支 → C49（§12）。
3. fixture sln 与 sdk 预处理树 Release build/test 全绿、sdk 新增失败为零；§4.6 同一性断言全过、residual 零块。
4. 属性/运算符产物的消费链就绪：C11/C14 已合入或其转换器消费改造（get_X→getX、op 命名表）先行；否则两子规则保持门控关闭（1208 保护）。
5. .NET 10 目标源码命中率扫描完成并 triage：1209/1211/1203 站点分类有主，无"未知残余"。
6. **建议默认关持续到 .NET 10 基线正式启动**；在那之前 `MaterializeExtensionBlocks` opt-in，1206 保持默认行为。

---

## 9. 里程碑

| 里程碑 | 内容 | 独立验证 |
|---|---|---|
| **M0** | Roslyn 4.12→4.14.x 升级（四包）+ 既有链回归；parse/符号探针（§7.1）+ 语法/限制盘点报告；CS2J1213 启动探针与门控骨架（规则体仍为空） | 既有全量测试；探针文档回填本设计；退出标准：探针报告覆盖节点/API/限制集三表，1213 在旧包环境下可复现 |
| **M1** | EMD-METHOD + EMD-PROP：块→经典 this 方法/get_X/set_X 对；调用点（含链式/复合）；C19 物化器对接；Core 单测 + fixture（方法/属性）build/test 全绿；residual 零块与同一性断言 | 不支持面仍走 1206/1209 |
| **M2** | EMD-OP（引用 C14 命名/SB 组件）+ EMD-STATIC（类型名限定，含泛型类型名）+ 两级泛型映射 + ref receiver；门控 1208；fixture 完整 | 全成员种类单测 |
| **M3** | 多块/partial/表达式树/metadata 边界收尾；sdk 命中率扫描与 triage；strict/overrides；与 C11/C14/E13 串跑；默认开启评审（§8） | 候选默认开 |

---

## 10. 风险

1. **★Roslyn 版本与预览 API 滚动（最高）**：4.12 完全不识别语法（parse 层全量失败）；4.14 GA 节点/符号 API 相对预览期可能改名（如块节点、receiver 参数符号、`IsExtension`  API 面）。缓解：M0 先升级先探针、SyntaxFacts 反射适配层隔离 API 形状、CI 锁 4.14.x 具体 patch。
2. **二次编译绑定漂移**：块参数/receiver 名消失后静态扩展推断边界（§4.4-4）可能存在无法重推断形态。缓解：方案 A 显式改写（不依赖重绑成功）+ §4.6 同一性断言 + 失败 plan 回退报码。
3. **泛型映射错位**：块/成员两级参数顺序、约束、静态成员专属推断。缓解：官方 GenericExtensions 等价样本逐形对照（IL 经典形态是标准答案），M0 探针覆盖。
4. **链序/门控**：EMD-PROP/EMD-OP 产物早于 C11/C14 消费链会产出不可消费 Java。缓解：1208 硬门控 + member-desugar 分析器排除块内声明的 checklist（§11）。
5. **JK 改名**：属性名撞 Java 关键字（`get_new` 等）。缓解：链尾 JK 对普通静态方法既有规则覆盖；fixture 含关键字命名样本。
6. **语言新预览边界滚动**：C# 15 扩展索引器及未来 partial/事件等扩展形态随 SDK 预览进入 parse 层。缓解：1209 拒绝清单随 C52 每次 Roslyn/基线升级重审（catalog C52 方法论），`Latest` 与 `Preview` 双版本探针。
7. **升级外溢**：4.12→4.14 带来的编译器行为变化（隐式 Span 转换等 C52 登记项）影响无关 pass。缓解：升级作为独立提交、全量 Release 回归先行，C49 代码不与升级混提交。
8. **热文件**：注册表三文件 + 链序/stub 计数测试与 C19 同期热点；C49 不新增注册行，冲突面限于 C19 翻转同一组文件（随 C19 分支 rebase）。
9. **metadata 扩展属性/运算符无声明可改**：外部程序集（BCL/NuGet）提供的扩展成员只有调用点在树内，方案 A 的声明侧不适用；误按"生成同名 host 声明"处理会造成重复定义/错误重载决议。缓解：v1 只报告（1203 同族），M3 按 sdk 计数决策，禁止 v1 凭名字合成声明。
10. **静态扩展调用与普通类型静态成员的同名歧义**：`IEnumerable<int>.Generate(...)` 若接收者类型未来真有同名静态成员，Roslyn 按 C# 规则决议（实例类型成员优先等），选择器只认真实绑定符号，语法形状不参与——风险在错误实现"见类型限定就改"，以 Core 单测（同名静态成员存在/不存在两样本）锁死。
11. **preview 语言版本渗入**：若上游某工程以 `<LangVersion>preview</LangVersion>` 进入编译（C# 15 索引器可解析），`LanguageVersion.Latest` 单版本探针之外必须有 preview 树的识别与拒绝路径；缓解：runner 固定 Latest 编译，preview 工程在 parse 诊断/块节点种类检查处落 1209，不依赖工程设置。

---

## 11. 并行协调

| 并行条目 | 关系 | 协调结论 |
|---|---|---|
| **C19 extension-methods** | **硬依赖、同 pass**：C49 是其声明侧规则族 | C19 分支先合（§12）；共享 compilation、binder、host 工厂、residual/回退骨架；catalogItems 改 `"C19, C49"`；CS2J1206 由 Error 升格为可物化（策略开关保留旧行为） |
| **C11 member-desugar（属性）** | 产物命名契约 `get_X/set_X`、init 归约、复合赋值配方 | C49 直接产 C11 最终形态；C11 分析器落地时必须跳过块内 PropertyDeclaration（祖先为 ExtensionBlockSyntax / 符号 IsExtension），不得为其建后备字段 plan；C11 的 Java 半区消费改造是 EMD-PROP 开门禁之一 |
| **C14 member-desugar（运算符）** | 命名镜像表/SB 站点配方/提升与转换规则 | C14 须把 OperatorMemberNaming/OperatorFacts 命名表做成可被 C49 公共引用的 Core 组件（不复制第二份表）；C14 分析器同样跳过块内运算符声明；SB-JK-Bool/SB-IncDec/SB-GateAndOr/SB-Metadata 排除表 C49 原样继承 |
| **C12 indexer** | 扩展索引器归 C52 拒绝，不进 C12 | C12 不处理块内索引器；1209 与 C52 防线码联动，Roslyn 升级评审时同步复核 |
| **E13 make-readonly / C07** | struct receiver 扩展属性与只读化 | C49 在 EM-ORDER 链位之后运行，只读化结果已定型；fixture 串跑锁定；不新增锚 |
| **C52 版本防线** | C#15 扩展索引器拒绝联动 | 每次 Roslyn/基线升级，C52 差集审计必须重跑 extension 块成员种类全集；1209 清单两边回链 |
| **C08/E17 java-keywords** | 链尾改名 | 生成标识符纳入 JK 既有方法/属性符号规则；无新标识面 |
| **C45 closure-lift / C26 linq-channel / C35** | lambda 体内扩展调用、LINQ reduced 糖、空条件 | lambda 体内按原模型计划照常（不引入新局部）；LINQ host 豁免继承 EM-LINQ；`r?.ExtProp` 跟 C19 EM-COND 同探针同策略 |
| **热文件（只读记录不改写）** | [ExtensionMethodIndex.cs](file:///d:/code/cs2j/src/CSharpToJava.Core/Pipeline/ExtensionMethodIndex.cs) 与 [PropertyTransformer.cs](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Member/PropertyTransformer.cs) | 本任务只记录为现状证据；转换器侧退役/扩展随 C19 M5 后续 PR，本特性 PR 一字不动 |

---

## 12. Worktree 分配

- 分支：`feature/preproc-c49-extension-members`
- 目录：`.worktrees/preproc-c49-extension-members`
- base：**`e50d9874`**（dev-3.x-26100408 最新提交，"feat(preprocessing): 波次0接线激活 C24默认接通+C27/C50/C46 CLI开关+catalogItems订正"；2026-10-04 核验：C19 分支 `feature/preproc-c19-extension-methods` 已合入该基点，§8 前置 2 满足；M0 Roslyn 5.0.0 升级亦在该基点内）
- 创建命令（2026-10-04 已执行，worktree 已创建）：

```bash
git worktree add .worktrees/preproc-c49-extension-members -b feature/preproc-c49-extension-members e50d9874
```

> 本次 base 更新（`9b99c60b` → dev-3.x-26100408 HEAD `e50d9874`）取代文末"附：开放问题裁决"⑧中"维持自有 base 9b99c60b、创建后再 rebase"的旧口径——直接以 dev-3.x-26100408 最新提交为基点，无需 rebase。

实施约束（写入实施 plan）：仅在该 worktree 内改码；M0 升级提交独立于规则提交；默认关先行（1206 行为不变），过 §8 门槛且 .NET 10 基线启动后再翻；转换器代码改动留 C19 M5 同族后续 PR；探针结论先回填本设计再写规则代码。**实施方式（2026-10-04）：以 Sub Agent 按里程碑（M1/M2/M3）分批实现，实现一部分、提交一部分。**

---

## 附：开放问题裁决（2026-09-29 拍板）

> **拍板目标**：支持全部 C# 语法/语义、便利下游 C#→Java 转换。统一口径：① 每种合法 C# 构造必须有语义等价的可编译 Java 终态，v1 可显式诊断门控但必须登记终态去向，禁止长期静默丢弃；② 中间树始终是普通合法 C#，Java 侧差异集中到 compat/映射层收口；③ 语义精确优先于分配/性能，优化变体须有画像证据；④ 预处理默认开前转换器旧臂由独立 PR 退役、不留双轨窗口，CheckPass 安全网永久保留；⑤ 合成类型名 `Cs2j<Role>_<Member>_<n>`、合成成员/临时 `_cs2j…`、让位改名 `<Name>_cs2j<Purpose>`（单下划线后缀）；⑥ 跨 pass 顺序依赖一律显式 RunAfter 固化；⑦ Implemented+默认关观察期不超过两个里程碑窗口；⑧ worktree 创建命令维持本文 §12 登记的自有 base `9b99c60b`（不改动），创建后第一步 rebase 到当时 dev-3.x HEAD（本裁决时为 `ef70e5a2`），stub 计数红灯由首个锚点翻转 PR 按真值收口。

**裁决（按原题号）：**

1. **改写策略**：**确认方案 A**（声明 + 调用点同步显式改写，二次编译仅作验证）；**否决方案 B**——纯声明改写靠重绑的逐形验证成本无界且把正确性押在重绑稳定性上，与"中间树必须自身语义确定"口径冲突。实施中发现方案 A 无法覆盖的形态时发显式诊断登记，不回退到 B。
2. **Roslyn 4.14.x 落点版本**：实施时取的**精确 patch 版本由 M0 探针决定并锁定 CI global_versions**（探针同时确认 GA API 中块节点/receiver/`IsExtension` 的确切形状）；探针结果回填 §4 后才允许写规则代码，组件名/判据按探针微调不算设计变更。
3. **receiver 修饰允许集**：以 **M0 探针矩阵实测为准**（ref/in/readonly/scoped × 成员种类、指针 receiver、set 访问器 ref 首参）；矩阵不支持的组合**收窄 1211 范围**（显式诊断）而非勉强物化，set 访问器 ref 首参物化形态同样按探针定，禁止超证承诺。
4. **catalogItems 时机**：**C19 翻转 PR 即写 `"C19, C49"`**（翻转时即登记承接关系，避免 catalog 再次滞后）；若 EMD-PROP/EMD-OP 与 C11/C14 落地次序倒挂，门控 **1208 长期保留**（倒挂期内持续拦截），待次序补齐且画像干净后才允许评估退役，退役另走门槛。
5. **metadata 扩展属性/运算符**：外部程序集声明、无源码块的扩展属性/运算符 **v1 只报告**（显式诊断，指物到程序集与成员）；**M3 按 sdk 扫描计数升级**——有真实命中即生成兼容绑定声明（companion 式），零命中则维持只报告并在两个画像周期后复核，不无限期只报告。

---

### 原问题记录（裁决前）

1. **改写策略**：推荐已采纳方案 A（声明+调用点同步显式改写，二次编译仅验证）；是否有评审意见愿意承担方案 B（纯声明改写靠重绑）的逐形验证成本（§2 决策 2）。
2. **Roslyn 4.14.x 落点版本**：实施时取哪个具体 patch（M0 决定，锁 CI）；GA API 中块节点/receiver/`IsExtension` 的确切形状以探针回填，可能微调 §4 组件名与判据。
3. **receiver 修饰允许集**（ref/in/readonly/scoped × 成员种类、指针 receiver）以 M0 探针矩阵为准，可能收窄 1211 范围或调整 set 访问器 ref 首参物化形态。
4. **catalogItems 时机**：C19 翻转即写 `"C19, C49"` 还是 C49 规则合入时再改（§3.2 两案）；EMD-PROP/EMD-OP 与 C11/C14 落地次序若倒挂，门控 1208 的长期保留策略。
5. **metadata 扩展属性/运算符**（外部程序集声明、无源码块）v1 只报告；是否在 M3 按 sdk 扫描数升级为生成兼容绑定声明。
