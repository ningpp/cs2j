# C# 项目转 TypeScript 项目 — 设计文档

**日期**: 2026-08-06
**状态**: 已完成全部 6 部分，待用户整体验收
**范围**: 在现有 CSharpToJavaConverter 基础上新增 TypeScript 目标后端，支持将 C# 项目转换为可编译、可运行的完整 TypeScript 工程（npm workspace）。

---

## 第 1 部分：背景、目标与语言差异分析

### 1.1 背景

本项目现有管线为 **C# → Java**：Roslyn 解析与语义分析 → 检查/去糖 passes → HIR 生成（生产 `Java` 命名空间 IR，`JavaSyntaxNode` 树）→ IR rewriters → Java 发射 → Maven 多模块工程输出，配套 `csharptojava-compat` Java 运行时库。管线前端（解析、语义、检查、去糖、partial 合并、goto 消除）与目标语言无关，具备扩展第二目标的基础。

注意：代码库中另有一套**未启用**的实验路径（`Java2` IR + `NewConversionPipeline`），本设计不涉及、不依赖、不迁移该路径。

### 1.2 目标

1. 将 C# 项目（`.csproj`/`.sln`/单文件）转换为**可编译运行**的完整 TypeScript 工程。
2. 转换产物通过 `tsc --noEmit` 严格编译（`strict: true`），测试项目可用 vitest 执行。
3. 提供 `csharptotypescript-compat` 兼容运行时库，桥接 C# 与 TS/JS 的语义差异。
4. **忠实翻译 + 低成本 TS 惯用法**：类/接口/泛型保持 1:1 结构（命名空间→模块目录），在天然优势点采用 TS 惯用法（原生 `async/await`、委托→函数类型、`yield`→generator、字符串插值→模板字符串）。
5. Java 路径**零回归**：所有 TS 改动通过 `TargetLanguage` 分支隔离。

**验证基准**：第一里程碑以 `tests/CSharpToJava.Tests` 单元测试语料为基准（语言特性覆盖优先）；GraphLayout 等大型项目端到端验证为后续里程碑。

### 1.3 非目标（YAGNI）

- `unsafe`/指针/`fixed` 语句支持
- WPF/WinForms/Blazor/ASP.NET 运行时映射
- Source map、调试体验优化
- 第三目标语言实现（仅预留扩展点）
- C# → 地道函数式/联合类型 TS 风格重写

### 1.4 C# 与 TypeScript 语言差异系统分析

本节是转换器设计的基础。差异按"对转换器的影响等级"分为三级：
**S 级**（语义根本不同，必须专门设计）、**A 级**（有对应物但行为有差异，需映射/补偿）、**B 级**（语法差异，机械翻译即可）。

#### 1.4.1 类型系统根本差异【S 级】

| 差异点 | C# | TypeScript | 对转换器的影响 |
|---|---|---|---|
| 名义类型 vs 结构类型 | 名义类型：兼容性由声明的继承/实现关系决定 | 结构类型：兼容性由成员形状决定 | 影响较小（C# 显式声明的关系在结构类型下依然成立）；但可能产生 C# 中不合法的额外兼容，可接受 |
| 类型擦除 | **具现泛型（reified）**：`List<int>` 与 `List<string>` 是运行时不同类型，可反射、可 `new T()`、可 `typeof(T)` | **类型擦除**：编译后类型注解全部消失，运行时无法区分 `Array<number>` 与 `Array<string>` | `typeof(T)`、`new T()`、`default(T)`、`is T`（泛型参数）均无法直接翻译，需兼容库辅助或诊断 |
| 运行时类型查询 | `GetType()`、`is`、`as` 对所有类型（含接口）有效 | `instanceof` 仅对 **class** 有效；**interface 在运行时不存在** | `is IInterface` 无法用 `instanceof`；需要运行时类型标记策略（见第 3 部分接口检查设计） |
| 值类型 vs 引用类型 | struct 为值语义：赋值即拷贝、按值比较、不可为 null | 一切对象均为引用语义 | struct 转 class 后值语义丢失，需诊断 + 兼容库 `clone` 辅助（关键场景） |

#### 1.4.2 基础类型与数值语义【A 级】

| C# | TypeScript | 语义差异与处理 |
|---|---|---|
| `int/uint/short/byte/...` | `number` | ① **整数除法**：C# `7/2=3`，TS `7/2=3.5` → 整型操作数的 `/` 需包 `Math.trunc()`；② **溢出**：C# unchecked 环绕，JS 变为浮点不环绕 → 记入已知限制；③ `(byte)` 等窄化转换需掩码（沿用 Java 目标的 `& 0xFF` 策略） |
| `float/double` | `number` | 精度模型不同（JS 只有 double），可接受 |
| `long/ulong` | `bigint` | 字面量 `L`→`n`；bigint 与 number 不能混合运算，跨类型算术需显式转换，发射器需检查操作数类型 |
| `decimal` | 无原生 | 兼容库 `CsDecimal`（定点数实现） |
| `char` | `string`（单字符） | `'a'` → `"a"`；`char` 算术（`c+1`）需 `charCodeAt`/`String.fromCharCode` 辅助 |
| `bool` | `boolean` | 直接对应 |
| `string` | `string` | 都不可变。差异：① C# `==` 是值比较，TS 对字符串 `===` 也是值比较（一致）；② `string.Format`、`Split` 重载、`PadLeft` 等需兼容库 `CsString`；③ C# `null` 字符串与空串语义需保留 |
| `object` | `any`（默认） | 保持可编译性优先；`Equals`/`GetHashCode` 走兼容库 `CsObject` |
| 可空值类型 `int?` | `number \| undefined` | 原生表达；`.HasValue`/`.Value` → 兼容辅助或内联展开 |
| `DateTime/TimeSpan/Guid` | 无原生等价 | 兼容库类（JS `Date` 语义与 `DateTime` 差异过大，不直接使用） |

#### 1.4.3 面向对象构造差异【A/S 级】

| C# 特性 | TypeScript 现状 | 影响等级 | 处理策略 |
|---|---|---|---|
| 类/单继承/接口 | 同构（`class/extends/implements`） | B | 机械翻译 |
| **方法重载** | TS 仅有编译期重载签名 + **单一运行时实现**；JS 无真重载 | **S** | C# 多实现重载无法 1:1 表达。策略：按参数数量/类型**名称改写**（如 `Parse(string)`→`Parse$1`、`Parse(string, int)`→`Parse$2`）并生成统一分发入口，或按签名差异直接改名；调用点由转换器同步改写（转换器掌握全部调用点，可行） |
| **命名参数** | JS/TS 无命名实参 | A | 发射期将命名实参**重排为位置实参**（转换器掌握形参顺序，纯语法重排） |
| 默认参数 | 支持（`= value`） | B | 直接翻译；注意 C# 默认值必须是编译期常量，TS 同理 |
| `params T[]` | rest 参数 `...args: T[]` | B | 直接翻译 |
| 属性 `get/set` | 原生 getter/setter | B | 直接翻译；C# 属性访问在两端都是方法调用语义，一致 |
| 索引器 | 无 | A | `get_Item`/`set_Item` 方法（与 Java 目标一致） |
| 运算符重载 | 无 | A | 静态方法 `op_Add` 等；调用点 `a + b`（运算符被重载时）改写为 `A.op_Add(a, b)` |
| 事件 | 无对应物 | A | 兼容库 `CsEvent<T>`（add/remove/invoke） |
| 委托 | 无对应物，函数即一等公民 | B | **函数类型**直接表达（TS 优势点） |
| 静态构造器 | 无 `static { }` 之外的静态构造器 | A | C# 静态构造器 → 静态字段初始化器合并，或兼容库延迟初始化辅助（静态构造器时序语义记入已知限制） |
| 嵌套类型 | 类内可定义类（TS 支持但少见） | B | 直接翻译；访问修饰语义差异（C# 嵌套 private vs TS）记入限制 |
| 密封类 `sealed` | 无 | B | 仅注释标记 `/* sealed */` |
| 抽象类/成员 | 支持（`abstract`） | B | 直接翻译 |
| 泛型约束 | `extends` 仅部分表达 | A | `where T : X`→`extends X`；`new()` 约束因类型擦除**无法表达** → 调用处改为显式工厂参数或诊断；`struct/class` 约束无对应 |
| record | 无 | A | class + 兼容库 `CsRecord` 基类（值相等 `equals`/`hashCode`/`toString`） |
| 扩展方法 | 无 | A | 静态宿主调用 `ExtClass.M(instance, ...)`（与 Java 目标一致） |
| `ref/out/in` 参数 | 无 | A | 兼容库 `Ref<T>` 包装（`{ value: T }`），与 Java 目标策略对齐 |
| `ref return` | 无 | S | 不支持，诊断 + 降级为值返回（语义差异记入限制） |

#### 1.4.4 控制流与语句差异【B/A 级】

| C# 特性 | TypeScript 现状 | 处理策略 |
|---|---|---|
| `if/while/do/for/foreach` | 同构 | `foreach` → `for...of`（集合需可迭代：数组原生、兼容集合类实现 `Iterable`） |
| `switch` 语句/表达式 | `switch` 语句同构；switch 表达式无 | switch 表达式降级为 if 链或立即调用辅助 |
| 模式匹配（`is` 类型模式、属性模式、`switch` 模式） | 无 | 类型模式→`instanceof` + 类型守卫变量；复杂模式降级为 if 链（复用 Java 目标 lowering 思路） |
| `try/catch/finally` | 同构 | 差异：C# catch 可按异常类型过滤（`when`）→ TS 在 catch 体内 `instanceof` 判断 + 重新抛出 |
| 异常层次 | 丰富层次（`ArgumentException` 等） | 兼容库提供 C# 异常类层次（继承 `Error`）；`throw` 语义一致 |
| `yield return` | generator `function*` | **原生翻译**（TS 优势点）；`IEnumerable<T>` → `IterableIterator<T>` |
| `goto` | 有 label 但语义受限 | 复用 GotoEliminator 预处理（目标无关） |
| `using` 语句 | 有 `using`（ES2025 显式资源管理）但语义要求 `Symbol.dispose` | 保守策略：`try/finally` + `dispose()` 约定（与 Java 目标一致） |
| 局部函数 | 无（闭包近似） | 转局部箭头函数；注意捕获语义与递归 |
| `checked/unchecked` | 无 | 忽略并诊断 |

#### 1.4.5 异步与并发【A 级】

| C# | TypeScript | 处理策略 |
|---|---|---|
| `Task/Task<T>`、`async/await` | `Promise<T>`、原生 `async/await` | **原生翻译**（TS 优势点）；语义差异：C# Task 可冷启动/多次 await 语义、`Task.Run` 无对应（单线程模型）→ `Task.Run` 诊断或降级同步调用；`Task.WhenAll`→`Promise.all` |
| `ValueTask` | `Promise<T>` | 同上 |
| `lock`/`Monitor` | 无（单线程） | 移除并诊断（单线程模型下无意义） |
| `Thread`/`Interlocked` | 无直接对应 | 不支持，诊断 |

#### 1.4.6 集合与 LINQ【A 级】

| C# | TypeScript/JS | 处理策略 |
|---|---|---|
| `List<T>` | `Array` | 行为一致处用数组 + memberMappings（`Count`→`length`、`Add`→`push`…）；迭代中修改等边界语义差异记入限制 |
| `Dictionary<K,V>` | `Map` 语义不同（引用键、无自定义比较器、遍历序为插入序） | 兼容库 `CsDictionary`（第一里程碑默认；后续可按键类型优化为原生 Map） |
| `HashSet<T>` | `Set` 部分对应 | 兼容库 `CsSet` 或原生 Set + 诊断 |
| `IEnumerable<T>` | `Iterable<T>` | 直接对应（`yield` → generator） |
| LINQ（`Where/Select/Aggregate/...`） | 数组方法 `filter/map/reduce` | 惰性求值差异：LINQ 惰性，数组方法积极 → 有副作用场景记入限制；复杂算子（`GroupBy/Join/ToLookup`）走兼容库 `Enumerable` |

#### 1.4.7 其他语义差异【S/A 级】

| 差异点 | 说明 | 处理策略 |
|---|---|---|
| **相等语义** | C# `Equals` 值相等（record/struct/字符串）；JS `===` 对对象是引用相等 | record/struct 的比较调用点改写为 `CsObject.equals(a, b)`；原始类型保持 `===` |
| **null vs undefined** | C# 只有 `null`；TS 有 `null` 与 `undefined` | 统一使用 `null`（`strictNullChecks` 下 `T \| null`）；`undefined` 仅用于可选参数缺省，避免混用 |
| **反射** | TS 运行时类型信息被擦除 | 兼容库最小集合（基于显式注册），不支持场景诊断 |
| **字符串插值格式化** | C# `$"{x:F2}"` 格式说明符 | 简单插值→模板字符串；带格式符→兼容库 `CsString.format` |
| **整数字面量** | `0xFF`、`1_000` | TS 同支持（十六进制、数字分隔符），直接翻译 |
| **枚举** | TS `enum` 数值语义接近；`[Flags]` 无对应 | 普通 enum 直接翻译；flags enum → 数值常量对象 + 位运算辅助 |
| **命名空间** | TS 的 `namespace` 已不推荐，模块化用 ESM | 命名空间 → 目录结构 + ESM import/export（第 3 部分详述） |

#### 1.4.8 差异分析结论

1. **可直接机械翻译的（B 级）**：类/接口/继承/属性/委托（函数类型）/async/foreach/基本控制流——构成 M1-M2 的主体，转换风险低。
2. **需要发射期映射/轻量改写的（A 级）**：数值语义（整数除法、窄化掩码）、命名参数重排、索引器、运算符、集合成员——复用 Java 目标已验证的策略 + TS 特化的映射表。
3. **需要专门设计的（S 级）**：方法重载的名称改写与调用点同步、泛型擦除（`typeof(T)`/`new T()`/`default(T)`）、接口运行时检查（`is IInterface`）、相等语义、值类型语义——这四项是本设计的核心难点，第 3 部分逐一给出方案。
4. **明确不支持的**：`unsafe`/指针、`ref return`、线程原语、真反射——统一走"诊断 + 降级桩"策略，保证转换始终产出可编译、可诊断的产物。

---

## 第 2 部分：架构、Pass 裁决与发射器设计

### 2.1 管线分叉模型

```
C# 源码
  │ ① Roslyn 解析 + 语义模型                          【复用，不变】
  ▼
  │ ② 预处理：GotoEliminator / make-readonly /           【复用，目标无关】
  │    PartialType 合并 / ProjectConversionExclusionPlanner
  ▼
  │ ③ 检查 passes：Compilation / UnsupportedDomain /       【复用；NativeInterop 对 TS
  │    PlatformBoundary / NativeInterop / ExtensionMethod     目标降级为警告】
  ▼
  │ ④ 去糖 passes：LinqDesugar 等                         【M1-M3 复用；M4 起 LINQ 策略按目标分叉】
  ▼
  │ ⑤ HIR 生成：C# → Java IR（JavaSyntaxNode 树）         【复用 Visitors/Transformers】
  ▼
  │ ⑥ IR rewriters（JavaSyntaxRewriter）                  【按目标组装：TS 跳过 Java 专属】
  ▼
  │ ⑦ Codegen 分叉（ITargetEmitter 抽象）
  │    ├─ Java 发射（现有 JavaCompilationUnit.ToString 路径，包装后不动）
  │    └─ TypeScriptEmitter（新增）
  ▼
  │ ⑧ 工程输出分叉
  │    ├─ JavaModulePlan → pom.xml（现有）
  │    └─ NpmProjectPlan → package.json / tsconfig.json（新增）
```

### 2.2 关键架构决策

| # | 决策 | 理由 |
|---|---|---|
| D1 | **分叉点在 Codegen 层（⑦）**，基于生产 `Java` IR | 复用①-⑥约 80% 基础设施；避免 IR 重构对 Java 路径的回归风险 |
| D2 | IR 保持 Java 风味，TS 发射器内置 **Java→TS 二次映射表** | IR 中已含 Java 化 API 调用（如 `.size()`），发射期翻译代价可接受；避免改造 HIR 生成 |
| D3 | `ConversionOptions` 新增 `TargetLanguage` 枚举（`Java` 默认），passes/rewriters 按目标组装 | 单一事实来源；Java 默认值保证向后兼容 |
| D4 | 重载/泛型擦除等 S 级问题的处理放在 **⑥ TS 专属 rewriter** 中，而非发射器内联 | rewriter 拥有整树视野（重载改写需要跨成员/跨文件调用点同步），发射器保持无状态逐节点输出 |
| D5 | 新增目录 `src/CSharpToJava.Core/TypeScript/`，**项目名不改** | 避免大规模命名重构；文档注明多目标职责 |
| D6 | 每类型一文件，命名空间→相对路径模块，ESM 导入由发射器计算 | TS/ESM 无 Java 包导入模型，必须自建模块解析 |

### 2.3 新增代码模块布局

```
src/CSharpToJava.Core/
├── TypeScript/
│   ├── TypeScriptEmitter.cs            # IR → .ts 文本（总入口）
│   ├── TsTypeWriter.cs                 # 类型声明发射（class/interface/enum）
│   ├── TsMemberWriter.cs               # 字段/方法/属性/构造器发射
│   ├── TsStatementWriter.cs            # 语句发射
│   ├── TsExpressionWriter.cs           # 表达式发射 + Java→TS 成员映射应用
│   ├── TsImportCollector.cs            # ESM import 收集与相对路径计算
│   ├── TsMemberMappingTable.cs         # Java→TS 二次映射（代码内置 + JSON 扩展）
│   ├── TsNaming.cs                     # 标识符安全化、TS 保留字处理、重载名改写
│   ├── Rewriters/
│   │   ├── TsOverloadDisambiguationRewriter.cs   # S 级：重载名称改写 + 调用点同步
│   │   ├── TsGenericErasureRewriter.cs           # S 级：typeof(T)/new T()/default(T) 降级
│   │   ├── TsInterfaceProbeRewriter.cs           # S 级：接口 instanceof → 运行时探针
│   │   └── TsEqualityRewriter.cs                 # S 级：Equals 覆写类的相等调用点改写（§2.6.4）
│   └── NpmProjectPlan.cs               # npm workspace 工程规划
config/typescript/
├── TypeMappings.json                   # C#/Java → TS 类型映射
└── MemberMappings.json                 # 成员调用映射（拆分降低单文件体积）
typescript/csharptotypescript-compat/   # 兼容运行时（独立 npm 包）
tests/CSharpToTypeScript.Tests/         # 转换单元测试
```

### 2.4 项目管线 Pass 逐项裁决

现有 `ProjectConversionPipeline` pass 列表对 TS 目标的处置：

| Pass | TS 目标处置 | 说明 |
|---|---|---|
| `linqPass`（LinqDesugar） | **M1-M3 复用；M4 分叉** | 初期接受去糖中间形态再发射；M4 引入 TS 策略（数组方法链） |
| `ProjectCompilationCheckPass` | 复用 | 目标无关 |
| `ProjectUnsupportedDomainCheckPass` | 复用 | 领域检查目标无关 |
| `ProjectPlatformBoundaryCheckPass` | 复用 | |
| `ProjectNativeInteropCheckPass` | **降级为警告** | TS 无 JNI 对应物，interop 代码统一走"不支持"诊断 |
| `ProjectExtensionMethodCheckPass` | 复用 | 扩展方法统一降级为静态宿主调用（两目标一致） |
| `ProjectPartialTypeNormalizationPass` | 复用 | 目标无关 |
| `ProjectTypeEmitPass(_irRewriters)` | **替换为 TS emit pass** | rewriter 列表 = 目标无关 rewriters + TS 专属 rewriters（§2.6） |
| `ProjectCompatibilityEmitPass` | **TS 变体** | 输出 compat 包引用声明而非 Java compat pack 依赖 |
| `ProjectCrossPackageImportEmitPass` | **TS 变体** | 跨项目引用 → workspace 包 import（见 §3.3） |
| `ProjectStructClonePatchPass` | **TS 变体** | struct 值拷贝语义 → 兼容库 `cloneStruct()` 调用 |
| `ProjectJavaModuleDependencyPass` | **替换为 `NpmDependencyPass`** | 收集项目引用图 → package.json dependencies |

单文件管线（`CreateSingleFilePasses`）同构处理：`SingleFileJavaEmitPass` → `SingleFileTsEmitPass`，其余检查 pass 复用。

### 2.5 TypeScriptEmitter：IR 节点级发射映射

发射器遍历生产 IR（`JavaSyntaxNode` 树）的全部节点类型：

**类型声明**

| IR 节点 | TS 输出 | 细节 |
|---|---|---|
| `JavaCompilationUnit` | 单个 `.ts` 文件 | 头部生成 ESM imports（§3.3）；`Package` → 目录路径 |
| `JavaClassDeclaration` | `export class X` | `abstract` 直译；`sealed` → 注释标记；基类 `extends`、接口 `implements`；接口基类同时生成运行时探针注册（§2.6.3） |
| `JavaInterfaceDeclaration` | `export interface X` | 默认接口成员 → TS 接口方法带实现（TS 支持）；同时生成伴随探针函数 `is$IXxx(v)`（§2.6.3） |
| `JavaEnumDeclaration` | `export enum X` | `[Flags]` → `const` 对象 + 位运算辅助（§3.2） |
| `JavaRecordComponent` | 构造器参数 + 字段 | 走 `CsRecord` 基类路径（§4） |
| `JavaTypeParameter` | `<T extends X>` | 约束映射；`new()` 约束触发诊断 + 工厂参数降级 |
| `JavaAnnotation` | 丢弃或注释保留 | `@Override` 等 Java 注解无 TS 对应 |

**成员**

| IR 节点 | TS 输出 | 细节 |
|---|---|---|
| `JavaFieldDeclaration` | `public/protected/private x: T = init` | 可见性修饰符直译；`readonly`/`const` 映射；`static` 直译 |
| `JavaMethodDeclaration` | `m(p: T): R { }` | 重载名改写已在 rewriter 完成（§2.6.1）；`params` → rest；默认参数直译 |
| `JavaConstructorDeclaration` | `constructor()` | 基类构造调用 `super()` 一致 |
| `JavaStaticInitializerBlock` | 静态字段初始化序列 | C# 静态构造器语义（§1.4.3）：合并入静态字段声明顺序，复杂场景诊断 |
| `JavaParameter` | `p: T` / `...p: T[]` / 可选 `p?: T` | `out/ref` 参数 → `Ref<T>` 包装类型 |

**语句**

| IR 节点 | TS 输出 | 细节 |
|---|---|---|
| `JavaBlockStatement` | `{ }` | 直译 |
| `JavaVariableDeclarationStatement` | `let x: T = e` | 未被重新赋值时可选 `const`（M2 优化项，默认 `let` 保安全） |
| `JavaForEachStatement` | `for (const x of c)` | 集合必须可迭代；兼容集合类实现 `Iterable` |
| `JavaForStatement`/`While`/`DoWhile` | 直译 | do-while 同构 |
| `JavaIfStatement` | 直译 | |
| `JavaSwitchStatement`/`JavaSwitchSection` | `switch/case` + 显式 `break` | fallthrough 语义两端一致；case 值表达式受 TS 限制（需常量），非恒定 case 降级为 if 链 |
| `JavaTryCatchStatement`/`JavaCatchClause` | `try/catch/finally` | catch 按类型过滤 → `catch (e) { if (e instanceof X) {...} else throw e; }` |
| `JavaThrowStatement`/`Return`/`Break`/`Continue` | 直译 | |
| `JavaRawStatement` | 原样输出 | 兼容已有 lowering 产出的原生片段 |

**表达式**

| IR 节点 | TS 输出 | 细节 |
|---|---|---|
| `JavaLiteralExpression` | 直译 + 特化 | `long` 字面量 `L`→`n`；`char` → 单字符字符串；字符串转义差异处理（`\u` 一致；逐字字符串 `@""` 在上游已展开） |
| `JavaBinaryExpression` | 直译 + 语义补偿 | **整数除法**：两操作数均为整型时 `/` → `Math.trunc(a / b)`；`%` 语义差异（C# 结果符号随被除数，JS 同——一致）；字符串 `+` 拼接一致；相等比较按 §2.6.4 处理 |
| `JavaUnaryExpression` | 直译 | |
| `JavaConditionalExpression` | `c ? a : b` | 直译 |
| `JavaCastExpression` | `e as T` / `Number(e)` 等 | 引用类型 → `as`（配合运行时诊断可选）；数值窄化 → 掩码/截断辅助（沿用 Java 目标策略）；`is`-as-null 模式由上游 lowering 处理 |
| `JavaNewExpression` | `new X()` / 字面量 | `new T()` 泛型场景已由 rewriter 降级（§2.6.2） |
| `JavaMethodCallExpression` | 调用 + 成员映射 | 应用 `TsMemberMappingTable`（`.size()`→`.length` 等，§3.1）；未命中映射 → 原样 + `/* cs2j: unmapped */` 诊断注释 |
| `JavaMemberAccessExpression` | 同上 | 静态访问 `X.member` 一致；枚举成员 `E.A` 一致 |
| `JavaLambdaExpression` | `(p): R => body` | 直译（委托→函数类型是 TS 优势点） |
| `JavaArrayAccessExpression` | `a[i]` | 越界行为差异（C# 抛异常，JS 返回 undefined）记入已知限制 |
| `JavaAssignmentExpression` | 直译 | 复合赋值同构 |
| `JavaInstanceOfExpression` | `e instanceof T` | class 直译；**interface 场景已由 rewriter 替换为探针调用**（§2.6.3） |
| `JavaIdentifierExpression`/`JavaThisExpression`/`JavaParenthesizedExpression` | 直译 | 标识符经 `TsNaming` 安全化（TS 保留字：`let/any/never/unknown/namespace` 等与 C# 交集处理；C# `@` 转义标识符去前缀后冲突则加 `$` 后缀） |
| `JavaRawExpression` | 原样输出 | |

### 2.6 S 级难点的专项设计

#### 2.6.1 方法重载改写（TsOverloadDisambiguationRewriter）

**问题**：C# 同名多实现重载在 TS/JS 中无法共存（TS 重载签名只能有一个运行时实现）。

**方案**：项目级 rewriter，在发射前完成：
1. **检测**：同一类型内按名称分组方法；单一实现无需处理。
2. **改写规则**：保留第一个重载原名（按参数数量升序稳定排序），其余追加类型缩写后缀：`Parse(s: string)` 保留 `Parse`，`Parse(s: string, style: number)` → `Parse_string_number`。后缀由参数类型的 TS 映射名生成，保证唯一且可读。
3. **调用点同步**：rewriter 遍历全部 `JavaMethodCallExpression`，按实参数量与静态类型选择目标重载名并改写。转换器掌握全部调用点（单项目内），跨项目调用在 M5 通过 workspace 依赖项目的全量转换保证一致。
4. **构造器重载**：C# 允许多构造器，TS 仅一个 `constructor`。方案：主构造器保留，其余转静态工厂 `Create$N(...)`，`new` 调用点同步改写。
5. **诊断**：无法静态判定目标重载的调用（如经 `object` 传递的委托调用）→ 诊断 + 保留首个重载并警告。

#### 2.6.2 泛型擦除应对（TsGenericErasureRewriter）

**问题**：`typeof(T)`、`new T()`、`default(T)`、`is T`（T 为类型参数）在运行时不可用。

**方案（逐项）**：

| C# 构造 | TS 降级方案 |
|---|---|
| `default(T)` | 已知具体类型 → 类型特定默认值（`0/""/false/null`）；泛型上下文 → 兼容库 `csDefault<T>(typeInfo?)` + 诊断（返回 `undefined as unknown as T`，行为差异记入限制） |
| `new T()` | **工厂参数注入**：为含 `new T()` 的泛型方法/类追加可选工厂参数 `factory?: () => T`；调用点若可静态解析具体类型则直接传入 `() => new X()`；无法解析 → 诊断 |
| `typeof(T)` | 降级为兼容库 `TypeToken<T>` 显式传递模式；无法解析 → 诊断 + `any` 桩 |
| `is T`（T 为类型参数） | 同上，依赖 `TypeToken`；否则诊断 |
| `where T : new()` | 移除约束 + 上述工厂参数机制联动 |
| `where T : struct/class` | 移除约束 + 注释说明 |

#### 2.6.3 接口运行时检查（TsInterfaceProbeRewriter）

**问题**：TS 接口编译后不存在，`x is IInterface` 无法用 `instanceof`。

**方案：结构化探针（structural probe）**：
1. 每个接口 `IFoo` 生成伴随探针函数：`export function is$IFoo(v: unknown): boolean`，实现为**必需方法成员存在性检查**（`typeof (v as IFoo).method === "function"`）。方法集合取自接口声明（含继承链合并）。
2. `x is IFoo` → `is$IFoo(x)`；`x as IFoo` → `(is$IFoo(x) ? x as IFoo : null)`；`catch (IFoo e)` 同构处理。
3. 类声明发射时同时生成可选的**标记载入**（`CsRuntime.markImplements(obj, "IFoo")`）作为探针的快路径：探针先查标记，未命中再走结构检查。这解决了"鸭子类型误判"问题（结构巧合的类型不会误匹配已标记对象；未标记对象回退结构检查）。
4. **纯属性接口**（无可调用成员）：结构探针无法区分 → 仅依赖标记，未标记场景发诊断。
5. 探针是保守近似：C# 显式接口实现（`void IFoo.M()`）转普通成员后探针行为可能偏宽，记入已知限制。

#### 2.6.4 相等语义（发射期规则 + 兼容库）

| 场景 | 处理 |
|---|---|
| 原始类型（number/string/boolean/bigint）的 `==`/`!=` | `===`/`!==`（字符串值比较两端一致） |
| 可空类型的 `== null` | `=== null`（设计约定：转换产物统一用 `null` 不用 `undefined`，§1.4.7） |
| record/struct 实例的 `==`/`.Equals()` | `CsObject.equals(a, b)`（深比较，优先走实例 `equals` 方法） |
| 普通 class 的 `==` | `===`（C# 默认引用相等，一致）；覆写 `Equals` 的类 → 调用点改写为 `CsObject.equals` |
| `GetHashCode()` | 实例 `hashCode()` 方法（record/struct 由 `CsRecord` 基类提供）；哈希表场景由 `CsDictionary` 内部消费 |
| `ReferenceEquals` | `===` |

### 2.7 目标抽象接口（为未来扩展预留）

```csharp
public interface ITargetEmitter
{
    string Emit(JavaCompilationUnit unit, ConversionContext context);
    string FileExtension { get; }            // ".java" / ".ts"
    string EmitProjectPlan(...);             // Maven / npm
}
```

现有 Java 路径以薄包装实现该接口，行为不变；TS 目标实现 `TypeScriptEmitter`。第一里程碑**不引入第二后端注册机制**（YAGNI），`TargetLanguage` switch 在管线装配处集中分叉。

---

## 第 3 部分：类型映射系统、特性转换规则与模块解析

### 3.1 TS 类型映射系统

#### 3.1.1 配置格式

与现有 `config/TypeMappings.json` 结构对齐，拆分为两个文件（降低单文件体积）：

**`config/typescript/TypeMappings.json`**（类型映射）：

```json
{
  "typeMappings": [
    { "csharp": "System.Int32",  "typescript": "number", "imports": [] },
    { "csharp": "System.Int64",  "typescript": "bigint", "imports": [] },
    { "csharp": "System.String", "typescript": "string", "imports": [] },
    { "csharp": "System.DateTime", "typescript": "CsDateTime",
      "imports": ["@csharptotypescript/compat"] },
    { "csharp": "System.Collections.Generic.Dictionary`2",
      "typescript": "CsDictionary", "imports": ["@csharptotypescript/compat"] }
  ]
}
```

**`config/typescript/MemberMappings.json`**（成员调用映射）：

```json
{
  "memberMappings": [
    { "owner": "System.String", "csharp": "Length", "typescript": "length", "kind": "property" },
    { "owner": "System.String", "csharp": "ToUpper", "typescript": "toUpperCase", "kind": "method" },
    { "owner": "System.String", "csharp": "Format", "typescript": "CsString.format", "kind": "static", "imports": ["@csharptotypescript/compat"] },
    { "owner": "System.Collections.Generic.List`1", "csharp": "Add", "typescript": "push", "kind": "method" }
  ]
}
```

#### 3.1.2 解析顺序（发射期）

IR 中的类型/调用已是 Java 风味（HIR 阶段已应用 Java 映射），TS 发射期按以下顺序解析：

```
① TsMemberMappingTable 内置表（代码，覆盖最高频项：size→length、add→push 等）
② MemberMappings.json（配置，可扩展）
③ TypeMappings.json 的 csharp→typescript 直查（用于类型注解）
④ 未命中 → 原样输出 + `/* cs2j: unmapped */` 诊断注释 + Diagnostics.Warning
```

诊断遵循现有诊断等级规范；未映射项不阻断转换，汇总报告供人工补配置（与 Java 目标的 analyze 命令思路一致，后续可增 `analyze --target typescript`）。

#### 3.1.3 核心类型映射表（第一里程碑）

| C# | TypeScript | 说明 |
|---|---|---|
| `int/uint/short/ushort/byte/float/double` | `number` | 原生；整型参与除法时包 `Math.trunc` |
| `long/ulong` | `bigint` | 字面量 `L`→`n`；与 number 混算处插入显式转换 |
| `sbyte` | `number` | 窄化掩码 `& 0xFF` 沿用 Java 策略 |
| `string` | `string` | 复杂成员走 `CsString` |
| `bool` | `boolean` | |
| `char` | `string` | 算术场景走 `CsChar` 辅助 |
| `decimal` | `CsDecimal` | 兼容库 |
| `object` | `any` | 可编译性优先 |
| `int?` 等可空值类型 | `number \| null` | 统一用 `null`（不用 `undefined`） |
| `List<T>` | `T[]` | 行为一致处用数组；迭代中修改等边界差异记入限制 |
| `Dictionary<K,V>` | `CsDictionary<K,V>` | M4 默认全走兼容库；后续按键类型优化 |
| `HashSet<T>` | `CsSet<T>` | |
| `IEnumerable<T>/IEnumerator<T>` | `Iterable<T>/Iterator<T>` | |
| `Task/Task<T>/ValueTask<T>` | `Promise<void>/Promise<T>` | |
| `DateTime/DateTimeOffset` | `CsDateTime` | |
| `TimeSpan` | `CsTimeSpan` | |
| `Guid` | `CsGuid` | |
| `StringBuilder` | `CsStringBuilder` | JS 字符串拼接语义不同（无容量概念），保行为用兼容库 |
| `Exception` 层次 | 兼容库异常类（继承 `Error`） | |
| `Func<...>/Action<...>/Predicate<T>` | 函数类型 | `Func<T,R>` → `(x: T) => R` |
| `IDisposable` | 兼容库接口 `IDisposable` | `Dispose()` → `dispose()` |
| 用户定义类型 | 同名 | 模块路径由模块解析器计算 |

### 3.2 特性转换示例（C# → TS）

**类/属性/构造器**

```csharp
namespace App.Models;
public class Point {
    public double X { get; set; }
    public double Y { get; private set; }
    public Point(double x, double y) { X = x; Y = y; }
    public double DistanceTo(Point other) => Math.Sqrt(...);
}
```

```typescript
// App/Models/Point.ts
export class Point {
    X: number;
    private y: number;
    constructor(x: number, y: number) { this.X = x; this.y = y; }
    get Y(): number { return this.y; }        // 只读属性保留 getter
    DistanceTo(other: Point): number { return Math.sqrt(...); }
}
```

命名约定：**保持 C# 原名**（PascalCase 成员不改 camelCase）——保证与源码逐字对照、降低调用点改写风险；仅标识符冲突时安全化（`TsNaming`）。模块目录按命名空间分层。

**委托/事件**

```csharp
public event EventHandler<Args> Changed;
Func<int, string> fmt = i => i.ToString();
```

```typescript
Changed: CsEvent<Args> = new CsEvent<Args>();   // 兼容库
fmt: (i: number) => string = (i) => CsString.toString(i);
```

**异步 / 迭代器**

```csharp
public async Task<int> LoadAsync() { ... }
public IEnumerable<int> Range() { yield return 1; yield return 2; }
```

```typescript
async LoadAsync(): Promise<number> { ... }
*Range(): IterableIterator<number> { yield 1; yield 2; }
```

**ref/out 参数**

```csharp
bool TryParse(string s, out int value);
```

```typescript
TryParse(s: string, value: Ref<number>): boolean;   // Ref<T> = { value: T }
```

**运算符重载**

```csharp
public static Vector operator +(Vector a, Vector b) => ...;
var c = a + b;
```

```typescript
static op_Add(a: Vector, b: Vector): Vector { ... }
const c = Vector.op_Add(a, b);    // 调用点由 rewriter 改写，成员名保持 PascalCase
```

**flags 枚举**

```csharp
[Flags] public enum Style { None = 0, Bold = 1, Italic = 2 }
```

```typescript
export const Style = { None: 0, Bold: 1, Italic: 2 } as const;
export type Style = (typeof Style)[keyof typeof Style];
// 位运算直接用；HasFlag → (v & flag) === flag（兼容库 CsEnum.hasFlag 可选）
```

### 3.3 模块解析（命名空间 → ESM）

**目录规则**：命名空间 → 相对于包 `src/` 的目录路径（`App.Models` → `src/App/Models/`），每类型一文件（`Point.ts`），与 Java 目标的包目录规则同构，便于对照。

**import 生成算法**（`TsImportCollector`）：

1. 发射时逐类型引用收集：同包内 → 相对路径 `import { Point } from "./Point.js"`（NodeNext 模块解析要求 `.js` 后缀，`tsconfig` 配 `module: "NodeNext"`）。
2. 跨包引用（项目引用）→ workspace 包名导入 `import { X } from "@proj/core"`，由 `NpmDependencyPass` 写入 package.json（对应 Java 目标的 `ProjectCrossPackageImportEmitPass` 变体）。
3. 兼容库符号 → `import { CsDateTime } from "@csharptotypescript/compat"`，按文件去重。
4. **循环引用**：TS/ESM 允许模块循环（class 场景下延迟绑定可用）；发射不做环检测，tsc/运行时暴露的问题记入已知限制，后续里程碑可加 barrel 优化。
5. 不生成 `index.ts` barrel 文件（YAGNI，避免大项目构建性能问题）。

**同名冲突**：同目录两个命名空间下同名类型→保留 C# 原始目录结构自然隔离；单文件多类型（C# 允许）按主类型命名文件，其余类型同文件导出（与 Java 目标处理一致）。

---

## 第 4 部分：兼容运行时库设计（csharptotypescript-compat）

### 4.1 定位与原则

- **定位**：桥接 C# 与 TS/JS 语义差异的运行时库，仅收录"原生无法等价表达"的部分；行为一致处一律用原生（数组、Promise、模板字符串等）。
- **设计原则**：
  1. API 名称与 C# 尽量一致（`Add`/`ContainsKey`/`ToString`），便于与源码对照；
  2. 纯 TS 实现，**零第三方运行时依赖**；
  3. 与 `java/csharptojava-compat` 目录结构对称（src/main + tests）；
  4. 可迭代：第一里程碑只实现转换器实际发射引用的符号（按需增长，不预建）。

### 4.2 包结构

```
typescript/csharptotypescript-compat/
├── package.json            # name: @csharptotypescript/compat, ESM
├── tsconfig.json           # strict, declaration 输出
├── vitest.config.ts
└── src/
    ├── index.ts            # 统一导出
    ├── primitives/         # CsDecimal, CsChar, CsGuid
    ├── time/               # CsDateTime, CsTimeSpan
    ├── text/               # CsString, CsStringBuilder
    ├── collections/        # CsDictionary, CsSet, Linq(Enumerable)
    ├── core/               # Ref, CsObject, CsRecord, CsRuntime(探针标记), TypeToken
    ├── events/             # CsEvent
    ├── exceptions/         # CsException 层次
    └── io/                 # IDisposable/using 辅助（后续里程碑扩展）
```

### 4.3 核心组件 API 设计（第一里程碑）

#### 4.3.1 `Ref<T>`（ref/out/in 参数）

```typescript
export interface Ref<T> { value: T; }
export function refOf<T>(value: T): Ref<T>;
export function outRef<T>(): Ref<T>;   // 初始值为 default
```

与 Java 目标的 ref 包装策略对齐，调用端约定：`out` 参数由被调方写入后调用方读取。

#### 4.3.2 `CsObject` / `CsRecord`（相等语义）

```typescript
export class CsObject {
    static equals(a: unknown, b: unknown): boolean;      // 原始类型 ===；对象优先调实例 Equals
    static getHashCode(v: unknown): number;              // 原始类型稳定哈希；对象调实例 GetHashCode
    static referenceEquals(a: unknown, b: unknown): boolean;
}
export abstract class CsRecord {
    Equals(other: unknown): boolean;       // 逐字段值相等（子类发射字段列表）
    GetHashCode(): number;
    ToString(): string;
}
```

record 子类发射时生成 `protected recordFields(): unknown[]` 提供字段列表，基类通用实现比较/哈希。

#### 4.3.3 `CsRuntime`（接口探针标记，配合 §2.6.3）

```typescript
export class CsRuntime {
    static markImplements(obj: object, ifaceName: string): void;
    static isMarked(obj: unknown, ifaceName: string): boolean;
}
```

标记存储于 WeakMap（不污染对象），探针函数先查标记再回退结构检查。

#### 4.3.4 `CsDateTime` / `CsTimeSpan`

- 内部以 ticks（微秒精度整数，bigint）存储，不依赖 JS `Date`（避免时区/精度陷阱）。
- 核心 API：`Now`、`UtcNow`、`Year/Month/Day/Hour/...`、`Add/AddDays/...`、`Subtract`（返回 `CsTimeSpan`）、`CompareTo`、`ToString(format?)`、运算符降级方法 `op_Addition/op_Subtraction`。
- 与 C# 行为对齐项：`DateTime.MinValue/MaxValue`、闰年规则、`DayOfWeek` 枚举。
- 时区：第一里程碑按 **UTC 语义**处理（`DateTimeKind` 简化），本地时区换算记入限制。

#### 4.3.5 `CsDictionary<K, V>`

- 内部双索引：原始类型键（string/number/boolean/bigint）走 JS `Map`；对象键走 `CsObject.getHashCode` 桶 + `Equals` 链（等价 C# 默认比较器语义）。
- 核心 API：`Add`（重复键抛 `ArgumentException`，对齐 C#）、索引器 `get_Item/set_Item`、`TryGetValue`、`ContainsKey`、`Remove`、`Count`、`Keys/Values`、`[Symbol.iterator]`（按插入序，对齐 .NET 实现行为）。
- 自定义比较器：`constructor(comparer?: IEqualityComparer<K>)` 预留。

#### 4.3.6 `CsString` / `CsStringBuilder`

- `CsString`：静态方法集合——`format(fmt, ...args)`（`{0}`/`{0:F2}` 格式符）、`join`、`padLeft/padRight`、`split`（对齐 C# 重载：字符串分隔符、`StringSplitOptions`）、`isNullOrEmpty/isNullOrWhiteSpace`。
- `CsStringBuilder`：`Append/AppendLine/Insert/Remove/Replace/ToString/Length/Clear`，行为对齐 C#（含 `Replace` 区间重载的简化诊断）。

#### 4.3.7 `CsEvent<T>`

```typescript
export class CsEvent<TArgs> {
    Add(handler: (sender: unknown, args: TArgs) => void): void;    // += 语义：重复添加不去重（对齐 C# 多播委托）
    Remove(handler: (sender: unknown, args: TArgs) => void): void;
    Invoke(sender: unknown, args: TArgs): void;                     // 空事件安全
    GetInvocationList(): ReadonlyArray<(sender: unknown, args: TArgs) => void>;
}
```

命名约定：库内公共方法一律 PascalCase（与原则 1 一致）；仅纯 TS 侧自由辅助函数（`refOf`/`outRef`/`csDefault`/`cloneStruct`）用 camelCase，因为它们无 C# 对应物。

#### 4.3.8 `Enumerable`（LINQ 惰性链，M4）

- 基于 generator 实现惰性求值（对齐 LINQ 惰性语义）：`Where/Select/SelectMany/OrderBy/GroupBy/First/FirstOrDefault/Single/Any/All/Count/Sum/Average/Min/Max/ToList/ToArray/ToDictionary/Aggregate/Zip/Skip/Take/Distinct/Contains`。
- 发射策略：简单链（where/select 且无副作用风险）→ 数组方法 `filter/map`；含复杂算子或需惰性保证 → `Enumerable.xxx` 链（由 M4 的 LINQ 目标分叉 pass 决策）。

#### 4.3.9 异常层次

```typescript
export class CsException extends Error { ... }            // 基类，含 InnerException/Message/StackTrace
export class ArgumentException extends CsException {}
export class ArgumentNullException extends ArgumentException {}
export class ArgumentOutOfRangeException extends ArgumentException {}
export class InvalidOperationException extends CsException {}
export class NotSupportedException extends CsException {}
export class NotImplementedException extends CsException {}
export class IndexOutOfRangeException extends CsException {}
export class KeyNotFoundException extends CsException {}
export class ArithmeticException extends CsException {}
export class DivideByZeroException extends ArithmeticException {}
export class OverflowException extends ArithmeticException {}
export class FormatException extends CsException {}
export class IOException extends CsException {}
// 后续里程碑按需扩展
```

`catch` 按类型过滤由发射器生成 `instanceof` 链（§2.5），异常类为 class 故 `instanceof` 可用。

#### 4.3.10 其他辅助

- `CsDecimal`：定点数实现（bigint 存尾数 + 固定 28 位小数精度），`Add/Subtract/Multiply/Divide/CompareTo/ToString` + 发射期运算符降级；性能敏感场景记入限制。
- `CsGuid`：不可变，`NewGuid()/parse/toString("D")/equals/compareTo`。
- `CsSet<T>`：基于 `CsDictionary` 实现（对齐 C# 对象键语义）。
- `IDisposable` + `using` 辅助：`interface IDisposable { dispose(): void }`、`async usingBlock<T>(res, fn)` 辅助函数。
- `cloneStruct<T>(v: T): T`：struct 赋值拷贝点由 `ProjectStructClonePatchPass` TS 变体插入（浅拷贝 + 嵌套 struct 递归，基于发射期已知字段列表）。
- `csDefault<T>()` / `TypeToken<T>`：泛型擦除降级辅助（§2.6.2）。

---

## 第 5 部分：单元测试设计

### 5.1 测试体系总览

四层测试金字塔，自底向上：

| 层 | 位置 | 工具 | 验证对象 |
|---|---|---|---|
| L1 发射器单元测试 | `tests/CSharpToTypeScript.Tests/` | xUnit（与现有测试项目一致） | C# 片段 → TS 文本的逐特性映射 |
| L2 Rewriter 单元测试 | 同上 | xUnit | S 级 rewriter 的 IR 改写正确性 |
| L3 tsc 编译回归 | 同上（子进程调用） | `npx tsc --noEmit` | 生成产物在 strict 模式下可编译 |
| L4 兼容库语义测试 | `typescript/csharptotypescript-compat/` | vitest | 运行时行为与 C# 对齐 |

另有贯穿约束：**Java 零回归**——每次提交全量运行现有 `tests/CSharpToJava.Tests` 等全部测试项目。

### 5.2 测试基础设施

新建 `tests/CSharpToTypeScript.Tests/CSharpToTypeScript.Tests.csproj`（镜像现有测试项目结构），提供公共基类：

```csharp
public abstract class TsConversionTestBase
{
    // 单文件转换（TargetLanguage = TypeScript），对齐现有测试的 Convert 助手模式
    protected ConversionResult Convert(string csharpSource);

    // 断言助手
    protected static string StripExports(string code);          // 去除 export 修饰便于断言
    protected void AssertCompiles(string tsCode);               // L3：写临时工程 + npx tsc --noEmit
    protected void AssertDiagnostic(ConversionResult r, string contains);
}
```

**tsc 回归 fixture 设计**：测试会话级共享一个临时目录（预生成 `package.json` + `tsconfig.json`，`@csharptotypescript/compat` 通过 file: 路径链接），每个测试方法写入待验证 `.ts` 文件后调用 `npx tsc --noEmit`；无 Node 环境时该层测试自动跳过（`Skip` 标记）并在 CI 强制启用。

### 5.3 L1 发射器测试矩阵（按特性分组，每组一个测试类）

命名沿用现有约定（`XxxTests.cs`，一个 `[Fact]` 一个用例）：

**基础发射（M1）**

| 测试类 | 核心用例示例 |
|---|---|
| `TsBasicClassEmissionTests` | 空类 → `export class X {}`；字段/方法/构造器；修饰符（public/private/static/abstract） |
| `TsPrimitiveTypeMappingTests` | `int`→`number`；`long`→`bigint`（含字面量 `100L`→`100n`）；`char`→单字符 string；`int?`→`number \| null` |
| `TsLiteralEmissionTests` | 字符串转义；`null`；布尔；十六进制/数字分隔符直译 |
| `TsControlFlowEmissionTests` | if/while/for/do-while/switch；foreach→`for...of`；`var`→`let` |
| `TsNamingSafetyTests` | C# `@class` 转义标识符；TS 保留字冲突（如成员名 `let`）→ 后缀化；PascalCase 保持验证 |

**核心特性（M2）**

| 测试类 | 核心用例示例 |
|---|---|
| `TsGenericEmissionTests` | 泛型类/方法；`where T : X`→`extends X`；多约束 |
| `TsInterfaceEmissionTests` | 接口声明；implements；默认接口方法；**伴随探针函数生成验证** |
| `TsPropertyEmissionTests` | 自动属性；只读属性→getter；`private set` |
| `TsEnumEmissionTests` | 普通 enum；显式值；`[Flags]`→const 对象形态 |
| `TsExceptionEmissionTests` | try/catch/finally；按类型 catch→instanceof 链；throw 兼容库异常 |
| `TsStringInterpolationTests` | 简单插值→模板字符串；带格式符→`CsString.format` |
| `TsNumericSemanticsTests` | **整数除法包 `Math.trunc`**；`(byte)` 窄化掩码；bigint/number 混算处显式转换 |
| `TsNullableHandlingTests` | `HasValue/Value` 展开；`??`→`??`；null 统一性（不出现 `undefined`） |

**语义降级特性（M3）**

| 测试类 | 核心用例示例 |
|---|---|
| `TsDelegateToFunctionTypeTests` | `Func<int,string>`→`(x: number) => string`；`Action`；自定义委托 |
| `TsEventEmissionTests` | 事件声明→`CsEvent`；`+=`→`Add`；`-=`→`Remove`；触发→`Invoke` |
| `TsRefOutParamTests` | `out int`→`Ref<number>`；调用端 `refOf` 包装；读回断言 |
| `TsIndexerTests` | 索引器→`get_Item/set_Item`；调用点改写 |
| `TsOperatorOverloadTests` | `operator +`→`op_Add`；调用点 `a + b`→`Vector.op_Add(a,b)` |
| `TsPatternMatchingTests` | `is` 类型模式→instanceof+守卫；属性模式→if 链；switch 表达式降级 |
| `TsYieldToGeneratorTests` | `yield return`→`function*`；`IEnumerable<T>`→`IterableIterator<T>` |
| `TsAsyncAwaitTests` | `Task<T>`→`Promise<T>`；`await` 直译；`Task.WhenAll`→`Promise.all` |
| `TsExtensionMethodTests` | 扩展方法→静态宿主调用 |
| `TsRecordEmissionTests` | record→class + `CsRecord` 基类；值相等断言 |

### 5.4 L2 Rewriter 测试（S 级难点专项）

| 测试类 | 核心用例示例 |
|---|---|
| `TsOverloadRewriterTests` | 同名两重载→第二个改名（后缀规则断言）；调用点按实参数量/类型同步改写；**构造器重载→`Create$N` 工厂 + new 点改写**；无歧义单实现不改名；无法静态判定→诊断 |
| `TsGenericErasureRewriterTests` | `default(int)`→`0`（类型特定）；`default(T)`→`csDefault` + 诊断；`new T()`→工厂参数注入；`typeof(T)`→TypeToken 降级 |
| `TsInterfaceProbeRewriterTests` | `x is IFoo`→`is$IFoo(x)`；`as IFoo`→条件表达式；纯属性接口→标记依赖 + 诊断；继承链接口合并探针 |
| `TsEqualityRewriterTests` | record 相等→`CsObject.equals`；普通 class `==`→`===`；字符串 `==`→`===`；`ReferenceEquals`→`===` |

### 5.5 L3 tsc 编译回归

- 覆盖策略：L1/L2 每个测试类的"代表性产出"（非全部用例，控制运行时长）进入编译集；另维护 `TsSmokeCompileTests` 将全部特性样本合并为一个多文件工程整体编译（验证 import 生成、模块解析）。
- 断言内容：`tsc` 退出码 0；失败时输出完整诊断信息到断言消息（沿用现有测试的 `Assert.True(result.Success, result.GeneratedCode)` 风格）。

### 5.6 L4 兼容库语义测试（vitest）

位于 `typescript/csharptotypescript-compat/src/**/*.test.ts`，重点验证与 C# 的行为对齐：

| 测试文件 | 关键用例 |
|---|---|
| `CsDateTime.test.ts` | 闰年（2000/1900）；`AddDays` 跨月跨年；`MinValue/MaxValue`；`Subtract` 返回 TimeSpan |
| `CsDictionary.test.ts` | 对象键的 Equals/HashCode 语义；重复 Add 抛异常；TryGetValue；遍历插入序 |
| `CsString.test.ts` | `format` 格式符（`{0:F2}`）；split 重载对齐 |
| `CsRecord/CsObject.test.ts` | 逐字段值相等；哈希稳定性；嵌套 record |
| `CsEvent.test.ts` | 多播顺序；重复添加不去重；remove 后不触发 |
| `Enumerable.test.ts` | 惰性验证（副作用执行时机）；GroupBy/OrderBy 与 C# 稳定性对齐 |
| `CsDecimal.test.ts` | `0.1+0.2 === 0.3`（定点精度）；除法舍入模式 |

### 5.7 双目标对照测试（Java 零回归保障）

新增 `DualTargetParityTests`：同一批 C# 语料分别以 Java/TS 目标转换，断言两者均成功且关键结构特征（类型数、方法数、诊断数）对齐——防止 TS 分支改动意外污染共享前端。该测试类同时是 Java 回归的安全网。

### 5.8 测试数据与基线管理

- 断言风格：**行为断言为主**（`Assert.Contains/DoesNotContain`，沿用现有项目风格），避免全文 snapshot 的脆弱性；仅 `TsSmokeCompileTests` 维护少量 golden 文件。
- 里程碑验收标准直接绑定测试：M1-M5 各自的验收 = 对应测试类全绿 + L3 编译通过（见第 6 部分里程碑表）。
- CI：现有 GitHub Actions 工作流增加 TS 测试目标；Node 环境安装纳入 CI 步骤。

---

## 第 6 部分：工程生成、CLI、里程碑、风险与已知限制

### 6.1 npm 工程规划器（NpmProjectPlan）

对标 `JavaModulePlan`：

- 每个 C# 项目 → 一个 npm 包目录；解决方案 → npm **workspaces** 单仓多包（对应 Maven 多模块）。
- 项目引用 → workspace 依赖（`"@proj/core": "workspace:*"`）。
- 输出结构：

```
output/
├── package.json          # workspaces 根（"workspaces": ["modules/*"]）
├── tsconfig.base.json    # strict: true, target ES2022, module NodeNext, declaration
├── modules/<project>/
│   ├── package.json      # 依赖 @csharptotypescript/compat + 项目引用
│   ├── tsconfig.json     # extends base
│   └── src/<命名空间目录>/Class.ts
```

- 测试项目（`IsTestProject`）→ 独立包 + vitest 配置；xUnit/MSTest 特性映射：`[Fact]`→`it(...)`，`[Theory]+InlineData`→参数化 `it.each`，`Assert.Equal/True/Throws`→`expect(...).toEqual/toBeTruthy/toThrow`。
- `--force` 清理、增量构建缓存（`OutputIncrementalWriteSession`）直接复用，产物指纹按目标区分（避免 Java/TS 产物指纹互踩）。
- 资源文件拷贝：沿用 Java 目标的 resources 拷贝策略（目标侧为包内 `resources/` 目录）。

### 6.2 CLI 集成

- `convert` / `convert-project` 新增 `--target java|typescript`（默认 `java`，完全向后兼容）；单文件模式 `-o` 默认后缀随目标变为 `.ts`。
- `ConversionOptions` 新增字段：`TargetLanguage`（枚举）、TS 映射配置路径默认指向 `config/typescript/`。
- TS 目标转换完成后自动验证链（对标 Java 的 mvn 强制验证约束）：
  1. `npm install`（workspace 根）
  2. `npx tsc --noEmit -p <每个模块>`（可用 `--no-verify-build` 关闭，与现有选项命名风格对齐）
  3. 测试包：`npx vitest run`（可用 `--no-verify-tests` 关闭）
- 验证失败即中止并输出诊断（沿用现有双端构建验证强制约束的思路）。
- 不修改 C# 源码的约束不变；所有修复通过改进转换器实现。

### 6.3 交付里程碑（含验收绑定）

| 里程碑 | 内容 | 验收标准（绑定 §5 测试） |
|---|---|---|
| **M1 骨架** | `TargetLanguage` 选项 + 管线分叉；`TypeScriptEmitter` 基础发射（类/字段/方法/字面量/控制流）；TypeMappings.json 雏形；`TsNaming` | §5.3 基础发射组测试全绿；简单类产物过 L3 tsc |
| **M2 核心特性** | 泛型/接口/属性/枚举/异常/字符串插值/数值语义/可空；接口探针 v1 | §5.3 核心特性组全绿 + L3；Java 全量测试零回归 |
| **M3 语义降级** | 四个 S 级 rewriter（重载/擦除/探针/相等）；委托/事件/ref-out/索引器/运算符/模式匹配/yield/async | §5.4 全部 rewriter 测试全绿 + §5.3 M3 组全绿 + L3 |
| **M4 兼容库 + BCL** | `csharptotypescript-compat` v1（§4.3 全部组件）；MemberMappings 生效；LINQ 目标分叉 | §5.6 vitest 全绿；含 BCL 调用样本转换后过 L3 |
| **M5 项目级** | NpmProjectPlan、workspaces、测试项目→vitest、CLI 自动验证链、增量缓存适配 | 单测语料项目级转换 → `npm install` + `tsc` + `vitest` 全通过；`DualTargetParityTests` 全绿 |
| M6（后续） | 大型项目端到端（GraphLayout 等）、映射覆盖率扩充、性能优化 | 另行立项，不在本设计验收内 |

每个里程碑独立可交付、独立验收；M1-M5 顺序执行（依赖关系：M3 依赖 M2 的接口发射，M4 依赖 M1 的发射骨架，M5 依赖前四者）。

### 6.4 风险分析与缓解

| 风险 | 等级 | 缓解 |
|---|---|---|
| Java IR 的 Java 语义残留导致 TS 发射遗漏翻译 | 高 | 二次映射表 + 未命中诊断兜底；L3 tsc 回归充当安全网；DualTargetParityTests 监控 |
| 重载改写误伤（调用点类型推断不准） | 高 | 仅按静态类型改写，不确定时保留首重载 + 诊断（宁可诊断不可静默错误）；专项测试覆盖 |
| 接口探针结构检查误判（鸭子类型巧合） | 中 | 标记快路径优先；纯属性接口发诊断；记入已知限制 |
| tsc strict 下生成代码类型不完备（大量 `any` 扩散） | 中 | 可编译优先策略明确接受 `any`；后续里程碑渐进收紧 |
| 兼容库行为与 C# 偏差 | 中 | L4 vitest 逐项对齐 C# 行为；偏差记入限制清单 |
| 共享前端改动引发 Java 回归 | 中 | TargetLanguage 分支隔离 + 全量 Java 测试 + DualTargetParityTests |
| Node/npm 环境依赖引入 CI 不稳定性 | 低 | L3 测试无 Node 时自动跳过；CI 锁定 Node LTS 版本 |

### 6.5 已知限制清单（转换产物需告知用户）

1. **值语义**：struct 转 class 后赋值共享引用，仅关键拷贝点插入 `cloneStruct`（覆盖不完整风险）。
2. **整数溢出**：number 不环绕，unchecked 溢出行为与 C# 不一致。
3. **静态构造器时序**：不保证与 C# 完全一致的触发时机。
4. **泛型运行时信息**：`typeof(T)`/`default(T)`/`new T()` 降级后行为受限（诊断标注）。
5. **数组越界**：JS 返回 `undefined` 而非抛异常。
6. **LINQ 副作用时序**：简单链用积极求值的数组方法，副作用执行时机可能与惰性 LINQ 不同。
7. **DateTime 时区**：第一里程碑仅 UTC 语义。
8. **显式接口实现**：探针可能偏宽。
9. **循环模块引用**：不做环检测，问题暴露于运行时。
10. **Task 冷启动/`Task.Run`**：单线程模型下无对应，降级为同步或诊断。

### 6.6 演进路线（本设计不实现）

- 若未来引入第三目标，将 `ITargetEmitter` 升级为后端注册机制，并考虑将 Java IR 中目标无关部分抽离为中性 MIR（即方案 C，仅在有第三个目标时论证成本收益）。
- `analyze --target typescript` 映射覆盖率分析命令。
- 映射表驱动的性能优化（Dictionary 原生 Map 分派）、source map、增量 tsc。

---

## 附录：决策记录（用户确认项）

| 决策点 | 选择 | 备选 |
|---|---|---|
| 目标定位 | 可编译运行的完整 TS 工程 | 仅核心逻辑迁移 |
| 代码风格 | 忠实翻译 + 低成本 TS 惯用法 | 完全镜像 / 地道 TS |
| BCL 策略 | 原生映射 + 兼容运行时库混合 | 纯原生 / 全兼容库 |
| 验证基准 | 先单元测试语料 | 复用大型 C# 语料 |
| 实现路线 | 方案 A：复用管线 + TS 后端 | 独立管线 / 先重构 MIR |
| IR 基座 | 生产 `Java` IR（JavaSyntaxNode） | Java2（未启用，不采用） |
