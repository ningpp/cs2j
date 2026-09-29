# C31 Index / Range（`^` 与 `..`）归一预处理器设计

**日期**: 2026-09-28
**范围**: 新增 C#→C# 预处理器 `index-range`（Core `IndexRange/` + CLI 薄适配器），在 C# 层把 from-end 索引（`^n`）与范围切片（`a..b`）显式化为长度相关的 `int` 索引与 `(offset, count)` 调用形态；不改任何 C#→Java 转换代码（转换器侧冗余分支的下掉为后续独立 PR）
**状态**: 设计已确认，待实施
**注册表**: stub `index-range`（[PassStubCatalog.cs](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassStubCatalog.cs#L91-L100)，catalogItems C31，Stage=Expression，RunAfter=`array-method`，中间目录 `.cs2j-index-range-src`）
**base**: `f68a783c`（`feat(preprocessing): register 23 merged-family stub passes`）

---

## 1. 背景与目标

### 1.1 静默坏码两站点（实测证据）

C31 属于 catalog §4 风险 12 点名的「静默错误输出」族：**不报错、却产出不可编译/错误 Java，比 fail-fast 更危险**。2026-09-28 复核代码，站点如下：

| # | 站点 | 现状行为 | 后果 |
|---|---|---|---|
| S1 | [ElementAccessTransformer.cs:219-225](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/ElementAccessTransformer.cs#L219-L225) | 元素访问参数为 `RangeExpression` 时**不看接收者类型**，一律产 `Arrays.copyOfRange(expr, lo, hi)`；右端缺省且非数组时 hi 取 `expr.size()` | 对 `string` 产 `Arrays.copyOfRange("abc", lo, "abc".size())`——String 既非数组也无 `size()`，**不可编译 Java**；对 Span/自定义切片类型同样错误 |
| S2 | [ElementAccessTransformer.cs:294-299](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/ElementAccessTransformer.cs#L294-L299) | 独立 `^n`（非元素访问参数位）只产注释 `/* C# from-end index ^n — requires array name to resolve */` | 表达式位被注释整体替换（如 `return ^1;` 的实参/返回场景），**不可编译 Java** |
| S3 | [ControlFlowTransformer.cs:642-651](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/ControlFlowTransformer.cs#L642-L651) | 独立范围表达式产 `Arrays.copyOfRange(/* array */, lo, /* length */)`；`..` 产 `/* full range */` | 占位注释流入 Java 产物，不可编译 |
| S4 | [ElementAccessTransformer.cs:228-236](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/ElementAccessTransformer.cs#L228-L236) | `x[^n]` 元素访问按 array/string/兜底三分支直映，但接收者文本 `expr` 在输出中出现两次（`expr[expr.length - n]`） | `GetArr()[^1]` 类**带副作用接收者被求值两次**；Span 等类型落入 `.get(size())` 兜底，方法不存在 |
| S5 | [TypeMappings.json](file:///d:/code/cs2j/config/TypeMappings.json#L2517) | 全仓无 `System.Index`/`System.Range` 映射（仅有 `System.IndexOutOfRangeException`） | 独立 Index/Range 类型的局部/参数/字段无 Java 对应 |
| S6 | [ArrayHelper.cs](file:///d:/code/cs2j/src/Cs2jArray/ArrayHelper.cs) / compat `ArrayHelper.java` | grep 无任何 `Slice`/`Range` 辅助（仅有 IndexOf/LastIndexOf 等同名前缀方法）；catalog 亦记录全仓无 Index/Range 专项测试 | 数组切片无「拷贝语义」助手可用；无回归护栏 |

S1/S2/S3 是本特性必须消灭的静默输出；S4 的双求值由本特性的单次求值临时变量机制顺带消除；S5/S6 决定支撑库边界（§2、§3）。

### 1.2 接收者类型矩阵

`^`/`..` 的合法接收者由 C# 编译器按「内置支持 + 模式约定（Length/Count + int 索引器；Range 另需 `Slice(int,int)` 或 Range 索引器）」界定。本 pass 全部以 SemanticModel 的实际绑定为准，不做文本猜测。

| ReceiverKind | 接收者 | `x[^n]` | `x[a..b]` | 长度成员 | 切片语义 |
|---|---|---|---|---|---|
| `SzArray` | 一维数组 `T[]`（含交错数组的每一维） | ✅ | ✅ | `.Length` | **拷贝**（新数组） |
| `String` | `string` | ✅（`char`） | ✅ | `.Length` | 新字符串（`Substring`；`s[..]` 恒等） |
| `Span` | `Span<T>` / `ReadOnlySpan<T>` | ✅ | ✅ | `.Length` | **视图**（`Slice`） |
| `Memory` | `Memory<T>` / `ReadOnlyMemory<T>` | ❌（无 int 索引器，基线即不可编译） | ✅ | `.Length` | **视图**（`Slice`） |
| `ArraySegment` | `ArraySegment<T>` | ✅（Count+索引器模式） | ✅（Slice 模式） | `.Count` | 视图 |
| `ListPattern` | `List<T>`/`IList<T>`/`IReadOnlyList<T>` 等 Count+int 索引器类型 | ✅（模式） | 仅当绑定到 `Slice(int,int)` 时合法 | `.Count` | 视绑定成员而定 |
| `ImmutableArray` | `ImmutableArray<T>` | ✅（Length+索引器） | 以 Range/Slice 绑定结果为准 | `.Length` | 视绑定成员而定 |
| `CustomIndex` | 显式声明 `this[Index]` 的自定义索引器 | ✅ | — | — | M4 处理（§9） |
| `CustomRange` | 显式 `this[Range]` 或可绑定 `Slice(int,int)`/扩展 Slice 模式 | — | ✅ | 绑定符号上的 Length/Count | 自定义 |
| `MultidimArray` | `T[,]` | ❌ C# 不合法 | ❌ C# 不合法 | — | 基线编译错误，非本 pass 对象；GetLength 归 `array-method` |
| `Pointer` | `T*` | ❌ | ❌ | — | C06 unsafe-ffm 域 |
| `Dynamic` / `ErrorType` | `dynamic`、不可解析 | 绑定不可判定 | 同左 | — | IR110 显式报错 |

### 1.3 目标显式形态

预处理后的中间树**零** `IndexExpression`/`RangeExpression` 节点（residual 闸门按语法种类计数，不按文本，避免误伤 C36 的 spread `..`），且产物仍是可编译、可在 .NET 上直接跑测试验证的 C#：

```csharp
// hat 元素访问
arr[^n]                                   // 简单形式 → arr[arr.Length - n]
GetArr()[^Next()]                         // 副作用形式 → 接收者/运算元单次求值临时变量（§2 IR-1）

// 一维切片（四种边界形态）
arr[i..j]  → Cs2jArray.ArrayHelper.Slice(arr, i, j - i)   // 数组：拷贝
arr[i..]   → Cs2jArray.ArrayHelper.Slice(arr, i, arr.Length - i)
arr[..j]   → Cs2jArray.ArrayHelper.Slice(arr, 0, j)
arr[..]    → Cs2jArray.ArrayHelper.Slice(arr, 0, arr.Length)   // 必须拷贝，不可恒等
s[i..j]    → s.Substring(i, j - i)        // string：Substring 族
span[i..j] → span.Slice(i, j - i)         // Span/Memory/ArraySegment：视图
```

全部形态满足：接收者求值一次、每个边界运算元求值一次、长度读取一次、副作用顺序与 C# 编译器降级一致（§2 IR-5）。

## 2. 关键设计决策表

| 决策点 | 结论 | 备注 |
|---|---|---|
| 总体架构 | 语义驱动**逐文件** pass：dirty 检查 → Pass1 自底向上表达式改写并收集临时变量 → Pass2 语句级提升 → 格式化；复刻 [NullCoalesceSyntaxRewriter](file:///d:/code/cs2j/src/CSharpToJava.Core/NullCoalesceRewriter/NullCoalesceSyntaxRewriter.cs) 两遍范式 | 不做整树 plan/回滚（无改名/签名变化）；正确性由确定性规则 + 中间树 build/test 双验 + residual 闸门保证 |
| 判定真相源 | 一律 `SemanticModel.GetTypeInfo/GetSymbolInfo/GetOperation`：接收者分类、Length/Count 属性符号、Range 绑定的 Slice 方法符号 | 禁止按标识符文本猜 Length/Count；S1 的根因就是不看类型 |
| `^n → len-n` 的长度获取 | 数组/string/Span/Memory/ImmutableArray 用绑定到的 `Length`；List 族/ArraySegment/自定义模式用编译器实际绑定的 `Length` 或 `Count`；多维 `GetLength(dim)` 不出现（`^` 对多维数组不合法，多维归 array-method） | 自定义类型同时有 Length/Count 时以编译器模式绑定的属性符号为准，不自定优先级 |
| `a[^n]` 元素访问 | 显式 int 索引：简单形式内联 `a[a.Length - n]`；副作用形式经临时变量（IR-1）。赋值/`ref`/`out`/`++`/`--` 目标同规则，接收者临时变量保持左值合法：`__ir_r[__ir_r.Length - 1] = v` | `arr[^1]++` 类复合目标的算术归约归 C17，本 pass 只把索引显式化 |
| `a[i..j]` 等四种切片 | 统一归一到 `(offset, count)` 二元形式：左缺省 offset=0；右缺省 end=len；hat 边界先解为 `len-k`；`count = end - offset`，count 只算一次 | 不产 Java 侧 `copyOfRange`；产物是 C# 助手/实例调用，Java 映射交给主转换器既有路径 |
| 数组切片助手 | `Cs2jArray.ArrayHelper.Slice(...)`：泛型 `T[]` + 8 个基元数组重载（int[]/byte[]/…，Java 泛型不能容纳基元数组），内部 `new T[count]` + `Array.Copy`/基元循环，保持 ArrayHelper 的「可转换 C# 白名单子集」 | 必须保留 C# 数组 range 的**拷贝**语义；严禁映射成任何视图 |
| 字符串特殊化 | `s[i..j]→s.Substring(i, j-i)`、`s[i..]→s.Substring(i)`、`s[..j]→s.Substring(0, j)`、`s[..]→s`（恒等，C# 中即同实例） | 复用主转换器既有 `Substring→substring` 映射（[SubstringConversionTests.cs](file:///d:/code/cs2j/tests/CSharpToJava.Tests/SubstringConversionTests.cs) 已钉死） |
| Span/Memory/ArraySegment 分派 | 产 C# 实例调用 `r.Slice(offset, count)`（右端缺省也统一二元形式）；`r[..]→r` 恒等 | 对应 compat `Span.slice(int,int)`（[Span.java:283-287](file:///d:/code/cs2j/java/csharptojava-compat/src/main/java/io/github/ningpp/compat/Span.java#L283-L287)）/`ReadOnlySpan.slice`（[:269-273](file:///d:/code/cs2j/java/csharptojava-compat/src/main/java/io/github/ningpp/compat/ReadOnlySpan.java#L269-L273)）；ArraySegment 映射在 Java 半区验证 |
| List/不可切片接收者 | List 族只处理 `^n`（Count+索引器模式，编译器已保证合法）；`list[a..b]` 在 C# 层本就不合法（无 Slice 绑定），属基线编译错误，不做发明 | 绑定到用户自定义/扩展 `Slice(int,int)` 的，按 CustomRange 产显式 `receiver.Slice(off,count)` / 扩展方法静态调用形式 |
| 自定义 Index/Range 成员模式 | M1–M3：显式 `this[Index]`/`this[Range]` 且无 int 模式可降级 → IR120 显式报错；M4：按 IOperation 识别模式降级为 int 索引/Slice 调用 | 保留绑定到的成员符号拼写；扩展 Slice 产 `ExtType.Slice(receiver, off, count)` 静态形态 |
| 独立 `^n` / `a..b` 与 Index/Range 变量 | M1–M3：脱离元素访问使用点的 `^n`/`RangeExpression`，以及 `System.Index`/`System.Range` 类型的局部/参数/字段/返回 → IR120 显式报错（消灭 S2/S3/S5）；M4 收编「可配对使用点」的局部形态 | 永不产注释/占位；不引入 `Cs2jIndex/Cs2jRange` 新值类型（是否引入列开放问题 Q2） |
| 临时变量提升与单次求值 | 共享「单次求值临时变量提升」基建形态（§4.3 TempLifter）：接收者 → 左边界 → 右边界 → 长度的顺序提升为 `__ir_*` 局部；语句锚点 = 最近外围语句；Pass2 在锚点前插入声明 | 与 catalog §3.3 表达式族（C23/C24/C35/C36）共享同一接口约定（§11） |
| 内联白名单（不提临时） | 接收者为简单名/`this`/参数且边界为字面量或常量符号时内联；`^k` 常量折叠为 `len-k`；全常量 range 不产任何临时 | 非白名单一律提升，宁可多临时不可双求值 |
| 副作用顺序 | 严格模拟编译器降级：① 接收者 ② 左边界运算元 ③ 右边界运算元 ④ 消费点前读长度（hat 的 `GetOffset(length)` 在运算元之后） | 嵌套切片（`a[..^F()][G()..]`）自底向上逐层提升，外层引用内层临时 |
| 常量索引与 `^0` | 常量折叠不改变语义：`^0=len`（元素访问越界、range 右端合法）等边界行为与原生 C# 完全一致——产物是普通 C# 算术/索引，越界异常由运行时自然抛出 | 不自造越界检查 |
| 越界语义 | 数组 → `IndexOutOfRangeException`；string `Substring`/List → `ArgumentOutOfRangeException`；助手 Slice 经原生索引/`Array.Copy` 获得同款异常 | .NET 半区测试以原生 C# 为预言机（§7），不手写 Java 侧行为 |
| 不可提升上下文 | 字段/构造初始化器、特性实参、表达式树 lambda、查询表达式内：仅允许「零临时」改写（全常量/白名单）；否则 IR130 | 表达式树同时受 catalog §4 风险 8 共享「表达式树内跳过」判定约束 |
| `?[]`/`?.` 组合 | `arr?[^n]`、`arr?[..]` 为 C35（NullConditionalDesugar）语法面；M1–M3 命中即 IR130 并在消息中指路 C35，不做半成品 null 检查展开 | 两族都落地后由共享 TempLifter 联合处理（§11） |
| 幂等 | 产物中无 `^`/`..` 节点，二次运行零 diff | 单测与项目级测试双断言 |

## 3. 注册表与链位契约

### 3.1 描述符翻转

实施完成后把 [PassStubCatalog.cs:91-100](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassStubCatalog.cs#L91-L100) 的 `index-range` stub 删除，在 [PassCatalog.cs](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassCatalog.cs) `Implemented` 表登记：

| 字段 | 值 |
|---|---|
| Id | `index-range`（`PassStubCatalog.Ids.IndexRange` 常量迁入 `PassIds` 或复用，最终以注册表评审为准） |
| IntermediateDirectoryName | `.cs2j-index-range-src`（与 stub 一致，零指纹迁移成本） |
| Stage | `Expression` |
| Status | `Implemented` |
| DefaultEnabled | **false**（落地即 opt-in；满足 §8 门槛后单独 PR 翻 true） |
| RunAfterIds | `[ "array-method" ]`（维持 stub 锚点）。描述符须**物理插入** `Implemented` 表 array-method 与 make-readonly 两条目之间：index-range 与 make-readonly 都只 RunAfter array-method，拓扑平局按注册表声明顺序裁决（[PassRegistry.cs:100-117](file:///d:/code/cs2j/src/CSharpToJava.Core/Preprocessing/PassRegistry.cs#L100-L117)），插入位置即执行位置，无需改 make-readonly 描述符；该顺序由 PassChainOrderTests 锁定 |
| SupportLibrary | `Cs2jArray`（复用，不新建支撑工程；见 §3.2） |
| NeedsSemanticModel | `true` |
| VerifyBuildByDefault / VerifyTestsByDefault | `true` / `true`（表达式语义改写，默认开中间树双验） |
| CatalogItems | `"C31"` |

### 3.2 支撑库归属：扩展 Cs2jArray，不新建工程

按 catalog C31 条目「并入 Cs2jArray 或新建 Cs2jIndexRange」的首选方案执行：

1. `Slice` 助手加入 [src/Cs2jArray/ArrayHelper.cs](file:///d:/code/cs2j/src/Cs2jArray/ArrayHelper.cs)（唯一真源，EmbeddedResource 分发机制已存在，见 [ArrayHelperSupportFiles.cs](file:///d:/code/cs2j/src/CSharpToJava.CLI/ArrayHelperSupportFiles.cs)），遵守该文件头部注释的「可转换 C# 子集」白名单（不得引入新语法面）。
2. compat 侧 `java/csharptojava-compat/.../ArrayHelper.java` 增加同名 `slice`（泛型 + 基元重载，底层 `Arrays.copyOfRange`）；类映射已存在（[TypeMappings.json:342-345](file:///d:/code/cs2j/config/TypeMappings.json#L342-L345) → `io.github.ningpp.compat.ArrayHelper`），方法名按既有 passthrough 约定核对，必要时补成员映射——发布前必须盘点方法面（catalog §4 风险 14：映射存在 ≠ 语义可用）。
3. 注入/过滤/指纹全部复用 array-method 既有机制：`AfterFilesAsync` 释放、项目引用注入、同名工程碰撞抛错、own-release 副本识别（[ArrayMethodPassAdapter.cs:157-218](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/Adapters/ArrayMethodPassAdapter.cs#L157-L218)）。
4. **组合语义**：`index-range` 启用而 `array-method` 关闭时，由 index-range 适配器自行释放 Cs2jArray（幂等：释放前按 csproj 内容识别 own-release 副本；ProjectReference 注入前先移除同路径引用，天然防重复）；两者同时启用时宿主工程集合取并集。实施时把 array-method 适配器中的释放/碰撞/引用注入私有逻辑提取为 CLI 内部共享类（建议 `Preprocessing/SupportLibraries/Cs2jArraySupportLibrary.cs`），两个适配器共用，提取须由既有 array-method 测试守门。

### 3.3 翻转清单（实施 PR 的完整改动面）

1. 新增 `src/CSharpToJava.Core/IndexRange/` 全部组件（§4）；
2. `src/Cs2jArray/ArrayHelper.cs` 增 `Slice` 重载；compat `ArrayHelper.java` 增 `slice` 重载；核对 TypeMappings；
3. 新增 `src/CSharpToJava.CLI/Preprocessing/Adapters/IndexRangePassAdapter.cs`（§5）；
4. `PassStubCatalog` 删除 IndexRange stub（`Ids.IndexRange` 常量随之迁入 `PassIds`，引用点同步改名）；`PassCatalog.Implemented` 在 array-method 与 make-readonly 条目**之间**插入描述符（§3.1）；`PassChainFactory.CreateAdapter` 增 switch 分支；`IsExecutedSupportLibraryProject` 对 Cs2jArray 的判定已按「实际执行 pass 的支撑库集合」驱动，无需改动；
5. 通用 CLI 面天然可达：`--enable-pass index-range`、`preprocess --pass index-range`、`--list-passes` 状态翻为 Implemented（不新增 legacy `--no-xxx` 开关）；
6. 测试：Core 单测、fixture、residual 闸门、CLI/一致性/链序测试（§7）——注意 [PassChainOrderTests.cs](file:///d:/code/cs2j/tests/CSharpToJava.Tests/Preprocessing/PassChainOrderTests.cs) 与 [PassStubCatalogTests.cs](file:///d:/code/cs2j/tests/CSharpToJava.Tests/Preprocessing/PassStubCatalogTests.cs) 对 stub 计数/顺序有断言，翻转时同步更新；
7. 转换器侧 S1–S4 分支在本特性**不删**（直连单文件入口仍需要安全网），在默认开启后由独立 PR 评估下掉（§8 第 6 条）。

## 4. Core 组件设计

目录 `src/CSharpToJava.Core/IndexRange/`，公开外形与 [NullCoalesceRewriter](file:///d:/code/cs2j/src/CSharpToJava.Core/NullCoalesceRewriter/NullCoalesceRewriter.cs) 同构（runner 标准 spine 逐文件驱动，无跨文件生成、无自定义 `ExecuteAsync`）：

```
src/CSharpToJava.Core/IndexRange/
  IndexRangePreprocessor.cs     # 对外入口：Preprocess(sourceCode, SemanticModel, Options) → IndexRangeResult
                                #   dirty 检查（含 Index/Range 命名类型）→ Pass1 → Pass2 → 格式化 → residual 闸门
  IndexRangeOptions.cs          # Strict（默认 true：residual/不可判定即 Error）
  IndexRangeStatistics.cs       # IPassStatistics 通用五字段 + 自有强类型计数（§6）
  IndexRangeDiagnostic.cs       # IndexRangeDiagnostic(Code, Severity, Message, FilePath?) + 码表 IR1xx
  IndexRangeResult.cs           # { OutputCode, Changed, Diagnostics, Statistics }
  ReceiverKindAnalyzer.cs       # 接收者分类 + 长度成员/Slice 绑定符号 + 上下文可行性
  IndexRewriter.cs              # hat：元素访问参数位（IR-1）+ 独立 ^ 判定（IR120）
  RangeRewriter.cs              # 切片：四边界形态 × ReceiverKind 分派（IR-2~IR-4）+ 独立 range 判定
  TempLifter.cs                 # 单次求值临时变量提升（两遍范式；PendingTemp/锚点解析/命名）
  IndexRangeSyntaxFacts.cs      # 语法判定、内联白名单、表达式树祖先判定、临时命名 __ir_
```

### 4.1 ReceiverKindAnalyzer

- 输入：`ElementAccessExpressionSyntax`/`IndexExpressionSyntax`/`RangeExpressionSyntax` 节点 + SemanticModel。
- 职责：
  1. `GetTypeInfo(receiver).Type` 分类为 §1.2 矩阵的 ReceiverKind；`dynamic`/error/unresolved → `Unknown`（IR110）。
  2. hat 长度解析：优先 `GetOperation(elementAccess)`——模式匹配的 `^n` 在 IOperation 中已降级为「Length/Count 属性访问 − 运算元」，直接取得编译器绑定的长度属性符号与运算元顺序，避免自行复刻模式解析。
  3. range 绑定解析：取元素访问的 `GetSymbolInfo`：数组/string/Span 为内置；其余拿到 `IPropertySymbol`（Range 索引器，M4）或 `IMethodSymbol`（`Slice(int,int)`，含 `IsExtensionMethod()`）；取不到合法绑定说明基线不可编译，按 IR120 报错而非猜测。
  4. 上下文可行性：祖先链判定表达式树 lambda（共享判定口径）、字段/初始化器/特性/查询等不可提升位置、`ConditionalAccessExpression`（C35 面）、赋值/ref/out/++/-- 目标位。
- 只读不改；分析结果随节点传给两个 Rewriter。

### 4.2 IndexRewriter / RangeRewriter 规则

**IR-1 hat 元素访问**（`e[^n]`，e 分类已知）：

```
内联（白名单且 n 为常量/简单名）：
  SzArray/String : e[e.Length - n]   （String 经主转换器既有 charAt 路径）
  Span           : e[e.Length - n]
  Count 模式      : e[e.Count  - n]
提升（其余）：
  var __ir_r = e;            // ① 接收者一次
  var __ir_n = n;            // ② 运算元一次（先于长度，模拟 GetOffset 顺序）
  int __ir_l = __ir_r.Length;   // 或 Count
  → __ir_r[__ir_l - __ir_n]
```

左值/`ref`/`out`/复合目标同形（接收者临时可作左值）；多维参数位不出现本节点（C# 不合法）。

**IR-2 边界归一**（RangeRewriter 内部，四形态）：`offset`/`end`/`count` 三个 C# 表达式按 §2 决策表构造；hat 边界复用 IR-1 的长度解析；`count` 合成为 `end - offset` 且在输出中只出现一次（必要时提 `int __ir_c = ...`）。

**IR-3 切片分派**：

| 形态 | SzArray | String | Span/Memory | ArraySegment/ListPattern 等绑定 Slice |
|---|---|---|---|---|
| `e[i..j]` | `ArrayHelper.Slice(__ir_r, off, cnt)` | `__ir_r.Substring(off, cnt)` | `__ir_r.Slice(off, cnt)` | `__ir_r.Slice(off, cnt)`（扩展方法 → 静态调用） |
| `e[i..]` | `Slice(r, off, len-off)` | `Substring(off)` | `Slice(off, len-off)` | `Slice(off, len-off)` |
| `e[..j]` | `Slice(r, 0, end)` | `Substring(0, end)` | `Slice(0, end)` | `Slice(0, end)` |
| `e[..]` | `Slice(r, 0, len)`（**拷贝**） | `e`（恒等） | `e`（恒等） | 按 Slice 绑定语义产 `Slice(0,len)`，不恒等 |

调用 `Cs2jArray.ArrayHelper` 时补 `using Cs2jArray;`（仿 array-method 既有 using 处理）；全常量且接收者白名单时允许零临时（可用于初始化器上下文）。

**IR-4 独立 Index/Range**：非元素访问参数位的 `PrefixUnaryExpressionSyntax(^)`、`RangeExpressionSyntax`，以及 `System.Index`/`System.Range` 类型声明引用 → M1–M3 一律 IR120；M4 才做「定义点与使用点同方法可配对」的局部降级。

### 4.3 语句级临时变量提升机制（与逐文件 rewriter 的衔接）

- Pass1（IndexRewriter/RangeRewriter，自底向上）只产出**表达式替换 + `PendingTemp` 列表**，不就地插语句。`PendingTemp(VariableDeclarationSyntax Declaration, StatementSyntax AnchorStatement, int Order)` 与 [NullCoalesceSyntaxRewriter.PendingDeclaration](file:///d:/code/cs2j/src/CSharpToJava.Core/NullCoalesceRewriter/NullCoalesceSyntaxRewriter.cs#L27-L30) 同构。
- 锚点解析 `FindAnchorStatement(node)`：向上找首个 `Block` 直接语句；若节点位于语句的表达式中，锚点即该语句。嵌套改写时内层临时 Order 在前；同一锚点多临时按「接收者→左界→右界→长度/count」稳定排序（与求值顺序一致）。
- Pass2（TempLifter，`VisitBlock`）在锚点语句前按序插入声明——直接复刻 [NullCoalesceStatementLifter](file:///d:/code/cs2j/src/CSharpToJava.Core/NullCoalesceRewriter/NullCoalesceStatementLifter.cs) 的机制；`if`/`for`/`while` 等内嵌块各自作用域正确。
- **无锚点位置**（字段初始化器、特性实参、表达式树、查询子句等）：Pass1 即判不可行，只允许零临时改写，否则 IR130，绝不产出跨语句悬挂临时。
- 临时名 `__ir_r<N>`/`__ir_n<N>`/`__ir_l<N>`/`__ir_c<N>`，文件内计数，带碰撞避让（同作用域已存在同名则递增）。
- 与 runner 的衔接：全部提升在**单文件内**闭合，`IndexRangePreprocessor.Preprocess` 返回最终文本；标准 spine 的「逐文件 Rewrite → 写回 → restore → build/test」无需定制（参考 [ProjectPassAdapter.cs:268-269](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/ProjectPassAdapter.cs#L268-L269)）。
- **共享接口约定（为 §3.3 表达式族预留）**：TempLifter 的对外形状设计为族可复用的最小契约——`PendingTemp(Declaration, Anchor, Order)` + `Collect(rewriter)` + `Apply(root, pending)` 两步；首期留在 `IndexRange/` 命名空间内，待 `expression-desugar`（C23/C24/C35/C36）立项时上收到 `src/CSharpToJava.Core/Preprocessing/Expressions/SingleEvaluationTempLifter.cs`，NullCoalesce 既有 lifter 列为后续收敛候选（本特性不改动它）。

## 5. CLI 适配器与集成

新增 [Adapters/IndexRangePassAdapter.cs](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/Adapters/IndexRangePassAdapter.cs)，骨架复制 [NullCoalescePassAdapter.cs](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/Adapters/NullCoalescePassAdapter.cs)，叠加 array-method 的支撑库释放职责：

1. 持有 `IndexRangePreprocessor`、`IndexRangeOptions`、累计 `IndexRangeDiagnostic`/`IndexRangeStatistics`；`Descriptor` 取注册表 Implemented 条目。
2. `RewriteFileAsync`：调 `Preprocess(context.SourceCode, context.SemanticModel!, options)`，合并统计、附加 `RelativePath` 到诊断，Changed=false → null（clean 文件字节级复制）。
3. `AfterFilesAsync`：当 `ArrayHelperCalls > 0`（或统计中任一 Slice 改写计数 > 0）时，经 §3.2 的共享 Cs2jArray 释放逻辑发布支撑库、向宿主工程注入 ProjectReference（复用 own-release 识别、碰撞抛错、TFM 预警）；之后对中间树做一次全树 residual 复扫（仿 NullCoalesce 适配器的 `CountResidual...` 做法）。
4. `VerifyBuild/VerifyTests` 取 config 开关（默认 true），`DeleteBuildOutputsAfterRestore = true`。
5. `HasErrors` = 存在 Severity.Error 诊断；`BuildChainSummaryLine` 输出 hat 站点、切片站点、跳过/错误计数；诊断行格式 `[index-range] [severity] message`。
6. `PassChainFactory.CreateAdapter` 增 `PassIds.IndexRange` 分支；`PassChainConfiguration` 无需新工厂委托（options 无外部配置文件）。
7. 通用 verb（项目/解决方案模式，唯一入口）：`preprocess --pass index-range -s src -d dst`、`--enable-pass index-range`、`--list-passes`（已核对 [Program.cs:4398-4420](file:///d:/code/cs2j/src/CSharpToJava.CLI/Program.cs#L4398-L4420)，通用 verb 仅 `-s/-d`，无 `-i/-o`）。**首期不新增单文件模式**：本 pass 强依赖整编译 SemanticModel（接收者分类跨文件），且单文件场景独立 `^`/`..` 多落 IR110/IR120；如后续确有需求，仿 array-method 独立 verb（`array-method -i/-o` 配 `_Cs2jArrayHelper.cs` 释放）另行立项，不列入默认开门槛。

## 6. 诊断策略

**第一原则（catalog §4 风险 12）**：无法在 C# 侧等价显式化时**显式报错**，禁止注释、占位、原样透传。任何 Error 诊断使本次运行失败（退出非零），与 S1–S3 的静默行为彻底切割。

码位 `IR1xx`（MSBuild 格式输出，码位只追加不改号）：

| 码 | 级别 | 含义 |
|---|---|---|
| IR101 | Info | hat 元素访问已显式化为 int 索引（计数用） |
| IR102 | Info | range 切片已显式化为 `(offset,count)` 形态（分接收者计数） |
| IR110 | Error | 接收者类型不可判定：`dynamic`、error type、单文件模式跨文件不可解析——不猜测长度成员 |
| IR120 | Error | 形态超出 M1–M3 范围：独立 `^n`/range 值、`System.Index`/`System.Range` 一等声明（参数/字段/返回/跨方法）、无 int 模式的自定义 Index/Range 索引器（消息给出 M4 预期与手工改写建议） |
| IR130 | Error | 不可提升上下文中出现非常量 `^`/`..`（字段/初始化器/特性/表达式树/查询）；`?[^n]`/`?[..]` 同码并在消息中指路 C35 |
| IR140 | Error（Strict）/Warning | residual 闸门：输出仍残留 `IndexExpression`/`RangeExpressionSyntax` 节点且无对应 IR120/IR130 诊断——理论不可达，达即中止 |
| IR201 | Warning | 中间树 `dotnet restore` 失败（语义同 array-method restore 诊断） |

统计 `IndexRangeStatistics`：通用五字段（`FilesScanned/FilesTransformed/FilesSkippedClean/FilesDirtyNoRewrite/ResidualCount`）+ `HatElementSites`、`RangeSliceSites`（内部分数组/字符串/Span/自定义）、`StandaloneIndexSites`、`StandaloneRangeSites`、`ReceiverTempsLifted`、`BoundTempsLifted`、`LengthTempReads`、`ArrayHelperCalls`、`StringSpecializations`、`SkippedByReason: Dictionary<code,int>`。链摘要行至少打印：hat 站点、切片站点（含分类）、临时提升数、跳过/错误数。

## 7. 测试计划

### 7.1 Core 单测 `tests/CSharpToJava.ProjectTests/IndexRangeRewriterUnitTests.cs`

内联源码 → `CSharpCompilation`（net10.0 语义，含 Span/数组/string）→ `Preprocess` → 文本断言（忽略空白）+ 输出可编译断言，矩阵：

1. **hat 全矩阵 × 接收者**：`T[]`、交错数组、`string`、`Span<T>`/`ReadOnlySpan<T>`、`List<T>`/`IReadOnlyList<T>`、`ArraySegment<T>`、`ImmutableArray<T>`、Count 自定义类、Length 自定义类；`^0/^1/^k` 常量折叠；
2. **range 四形态 × 接收者**：`i..j`/`i..`/`..j`/`..`，断言数组走 `ArrayHelper.Slice` 且 `..` 为拷贝形态、string 走 `Substring` 三变体 + 恒等、Span 走 `Slice` 且 `..` 恒等、hat 边界 `..^n`/`i..^n` 的 offset/count 算式；
3. **嵌套/复合**：`a[..^1][2..]`、`a[b()[^n]..]`、与 `??`/三元/调用实参/return/对象初始化器组合；
4. **副作用与单次求值（行为断言，非仅文本）**：`GetArr()[^Next()]`、`GetArr()[Next()..^Next2()]` 经探针计数验证接收者 1 次、各运算元各 1 次、长度 1 次；赋值目标 `GetArr()[^1] = v`（接收者仍 1 次）、`arr[^1]++`、`ref var x = ref span[^1]`、`out` 实参位；
5. **字符串/Span 专项**：空串/空段、`Substring` 参数顺序（offset,count 而非 offset,end）、视图型接收者 `..` 恒等不拷贝；
6. **多维/非法面**：`T[,]` 上 `^` 不产生改写（基线即编译错误，单测仅断言 pass 不发明代码）；指针位不命中；
7. **诊断反例**：`dynamic d; d[^1]` → IR110；`Index idx = ^1;`/`Range r = 0..1;`/`void M(Range r)`/字段类型 → IR120；字段初始化器中非常量切片 → IR130；表达式树 lambda → IR130；`arr?[^1]` → IR130（C35 指路）；不可解析接收者 → IR110；Strict 关闭时 IR140 降级为 Warning 且计数正确；
8. **residual 闸门**：改写后树按语法种类扫描零 `IndexExpression`/`RangeExpression`；构造 C36 spread `[.. x]` 的输入（若编译可用）断言**不被误计**；
9. **幂等**：输出二次 `Preprocess` 零 diff、Changed=false。

### 7.2 项目级 fixture `tests/SampleIndexRangeSolution/`

仿 SampleArraySolution：`SampleIndexRangeLib` + `SampleIndexRangeTests` + sln，`Content Include` 进 ProjectTests，TFM net10.0。覆盖跨文件接收者、库工程注入 Cs2jArray ProjectReference、clean 工程字节级复制（含 BOM 保持）、own-release 副本不被重写；断言：

- `result.Success`、统计分类计数、中间树文本含 `ArrayHelper.Slice`/`Substring`/`Span.Slice`、不含 `^`/`..` 语法节点；
- 中间树 `dotnet build -c Release` **和** `dotnet test -c Release` 退出码均为 0；
- **两半区预言机**（catalog §5.1）：① .NET 半区——同一套 xunit 在预处理后中间树上跑，结果与未改写原生 C# 完全一致（切片内容、长度、异常类型与消息断言），证明 desugar 正确；② Java 半区——仿 [ArrayMethodConversionEndToEndTests.cs](file:///d:/code/cs2j/tests/CSharpToJava.ProjectTests/ArrayMethodConversionEndToEndTests.cs) 跑转换后 Java 测试，重点钉数组基元切片（int[]/byte[]）、string `Substring`、Span `slice` 三组运行时结果；
- 与 array-method 的四象限组合（两 pass on/off）各跑一次：支撑库只释放一份、ProjectReference 不重复、碰撞检测仍生效。

### 7.3 一致性 / CLI / 链序

- [ProjectPreprocessorsConsistentTests.cs](file:///d:/code/cs2j/tests/CSharpToJava.Tests/ProjectPreprocessorsConsistentTests.cs) 增 index-range 条目（存在性、目录名 `.cs2j-index-range-src`）；
- 更新 `PassChainOrderTests`（拓扑序含 index-range 且位于 array-method 之后）与 `PassStubCatalogTests`（stub 总数 −1、index-range 不再是 stub）；
- 新增 `IndexRangeCLITests`（仿 `PreprocessVerbCLITests`/`JavaKeywordCLITests` 的项目级驱动）：默认关、`--enable-pass index-range` 开启、`preprocess --pass index-range -s/-d` 执行、保留目录注册、`--list-passes` 显示 Implemented/默认关、IR110/IR120 失败退出码非零且不残留中间目录（stub 拒绝语义已有先例：启用未实现 stub 在建目录前失败）。

### 7.4 验证命令

```powershell
dotnet build -c Release CSharpToJavaConverter.slnx
dotnet test -c Release tests/CSharpToJava.ProjectTests/CSharpToJava.ProjectTests.csproj
dotnet test -c Release tests/CSharpToJava.Tests/CSharpToJava.Tests.csproj
dotnet test -c Release tests/CSharpToJava.ConversionTests/CSharpToJava.ConversionTests.csproj
```

## 8. 默认开启验收门槛

opt-in 落地后翻 `DefaultEnabled=true` 的全部条件：

1. §7.1 矩阵全绿；fixture 与三测试工程 Release `build`/`test` 全绿；
2. residual 闸门零 `^`/`..` 节点；所有未处理站点必须有 IR110/IR120/IR130 显式诊断——抽样核对训练画像，确认无「静默未处理」；
3. .NET 半区与原生 C# 行为等价（含异常类型）；Java 半区 array/string/Span 三组 e2e 全绿，compat `ArrayHelper.slice` 方法面齐备（§3.2 第 2 条盘点通过）；
4. 幂等零 diff；四象限支撑库组合验证通过；指纹/保留目录/落盘行为与既有 pass 一致；
5. csharp-sdk 画像上以 opt-in 方式跑一轮：命中率统计、IR120 未知形态清单评审、基线 build/test 不退化（catalog §4 风险 10 的默认化约束）；
6. 转换器侧 S1–S4 冗余分支的下掉/重定向已有跟进 PR 排期（评估 `ElementAccessTransformer` range/hat 分支与 `ControlFlowTransformer.TransformRangeExpression` 在预处理路径上的移除），或明确保留为单文件入口安全网并附测试。

## 9. 里程碑

| 里程碑 | 内容 | 粗估规模 |
|---|---|---|
| M1 hat 索引（一维） | Options/Statistics/Diagnostic/Result/Preprocessor 骨架 + ReceiverKindAnalyzer（数组/string/Count/Length 模式）+ IR-1 + TempLifter 两遍机制 + IR110/IR130 + Core 单测主体 | Core ~700 行 |
| M2 一维 range 切片 | IR-2 边界归一（含 hat 边界、count 单次化）+ 数组 `ArrayHelper.Slice`（泛型+基元重载，Cs2jArray + compat + 映射核对）+ IR-3 数组分派四形态 + 注入接线 + IR120（独立 Index/Range）+ fixture 与单测 | ~600 行 + 支撑库 |
| M3 string/Span 特化 | `Substring` 三变体/恒等、Span/Memory/ArraySegment `Slice` 视图分派、`..` 拷贝 vs 恒等语义、Java 半区 e2e、四象限组合、副作用行为测试补齐、CLI/链序/一致性测试、Release 全绿 | ~400 行 + fixture |
| M4 自定义模式 + 默认开 | IOperation 模式识别收编自定义 int 索引器/Slice（含扩展方法）；评估独立 Index/Range 局部值配对降级（或保持 IR120，依 Q2 评审）；`?[]` 与 C35 联合策略；sdk 画像试跑与 IR120 清单评审；满足 §8 后翻默认开 | ~400 行/扫描 |

M1–M4 全程保持 `DefaultEnabled=false`；每个里程碑自身须 Release build/test 全绿，不留半残中间树。

## 10. 风险

| # | 风险 | 证据/说明 | 缓解 |
|---|---|---|---|
| R1 | **静默错误输出族**：现状不报错却产不可编译 Java，漏一个形态即延续风险 12 | S1–S3（[ElementAccessTransformer.cs:219-225](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/ElementAccessTransformer.cs#L219-L225)/[:294-299](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/ElementAccessTransformer.cs#L294-L299)、[ControlFlowTransformer.cs:642-651](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/ControlFlowTransformer.cs#L642-L651)） | 不可判定即 Error；residual 闸门 IR140；默认关 + §8 门槛 |
| R2 | **双求值**：现有 hat 直映把接收者文本输出两次，副作用场景静默语义错 | S4（[:228-236](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/ElementAccessTransformer.cs#L228-L236)）；range 分支默认 hi 亦重复接收者 | TempLifter 单次求值 + 探针行为测试（非文本断言）；白名单外一律提升 |
| R3 | **接收者误判**：var 解析失败/dynamic/跨工程元数据近似下分类错误会产错形态 | 转换器已有 VarTypeMap 兜底近似，本 pass 不得继承该猜测作风 | 只用 SemanticModel 绑定；Unknown→IR110；fixture 跨工程验证；整树 build/test 兜底 |
| R4 | **拷贝/视图语义混淆**：数组 range 是拷贝、Span 是视图、string `..` 恒等；若统一助手会出数据级 bug | catalog C31 验证条目明确点名「C# 切片拷贝语义 vs Java subList 视图语义」 | IR-3 按 ReceiverKind 分派；`arr[..]` 强制 Slice 拷贝；.NET 半区变异探针测试 |
| R5 | **支撑库方法面不齐**：compat `ArrayHelper` 无 slice；基元数组需独立重载；映射可能漏成员 | S6；catalog §4 风险 14 | M2 发布前盘点 + Java 半区 e2e；方法面不齐不进 M3 |
| R6 | **array-method 组合面**：两 pass 共享 Cs2jArray，释放/引用注入/碰撞逻辑若各写一份会漂移 | [ArrayMethodPassAdapter](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/Adapters/ArrayMethodPassAdapter.cs) 私有逻辑 | §3.2 提取共享类，既有 array-method 测试守门；四象限组合测试 |
| R7 | **不可提升位置误插语句**：字段初始化器/特性/表达式树内无法锚定 | NullCoalesce 对 `?.` 亦有同类限制（JK-8 同款） | 零临时白名单 + IR130；表达式树共享判定 |
| R8 | **`..` token 与 C36 spread 撞车**：residual 若按文本扫会误报 | `CollectionExpression` 的 `..e` 是 SpreadElement | 闸门只计 `RangeExpressionSyntax`/`IndexExpression` 语法种类；7.1 第 8 条专测 |
| R9 | **链位与 catalog 理想顺序不一致**：§3.2 建议 C31 在表达式族早期（E04 前），注册表锚在 array-method 之后 | foundation plan 明确「注册表是活清单，stub 锚点不改变现序」 | 以注册表为事实源；array-method 不改写元素访问节点，两 pass 无语法面争抢；如未来重排由注册表评审统一处理 |
| R10 | **链耗时/指纹**：新增语义 pass + 中间目录拉长 convert-project | catalog §4 风险 11 | clean 文件字节复制零成本；指纹按注册表令牌自动接入；verify 可关；支撑库不进快照 |
| R11 | **自定义 Slice 形态多样**（`Slice(Range)`、`AsSpan` 链、扩展方法、只读视图）误判分派 | SemanticModel 对扩展方法/模式绑定可判但覆盖面需实测 | M4 前一律 IR120；M4 以绑定符号为准，不认名字文本 |

## 11. 并行协调

| 相邻工作 | 边界 | 合并顺序/接口约定 |
|---|---|---|
| **E12 array-method（直接锚点）** | E12 管多维数组、数组静态方法收口、GetLength 与 Cs2jArray 注入；C31 管一维 `^`/`..` 表达式使用点与 Slice 助手。数组元素访问不经过 E12 改写，无语法面争抢 | RunAfter=`array-method` 已注册；两 pass 共用 Cs2jArray（§3.2）；共享释放逻辑提取为双方共同评审的小类；联合 fixture 钉四象限 |
| **C12 member-desugar（索引器成员归约）** | C12 把源内索引器归约为 `GetItem/SetItem` 普通方法、实参原样透传；C31 把索引器实参位的 `^`/`..` 先归约成 int/助手调用。C12 spec §11 已记录：两种先后顺序都产出可编译树，并倾向补 `index-range → member-desugar` 边 | 与 C12 的 R7 约定对齐：联合用例 `c[^n]`、`c[i..j]`；两族都在链时由注册表评审补 RunAfter 边（倾向先消表达式糖）；本分支不替 C12 翻 stub |
| **表达式族 C23/C24/C35/C36（expression-desugar）** | catalog §3.3 指定五族共用「单次求值临时变量提升」基建；C35 拥有 `?[]`/`?.` 去糖（C31 对 `?[^n]` 仅 IR130 指路）；C36 拥有集合表达式 spread（与本族共享 `..` 词法但语法节点不同） | TempLifter 按 §4.3 最小契约实现（PendingTemp + Collect/Apply），上收路径预留 `Core/Preprocessing/Expressions/`；C31 先落地即基建首个真实消费者，接口冻结后 C23/C24/C35/C36 复用；residual 按语法种类分界 |
| **C17 numeric-boundary（复合目标）** | `arr[^1]++`/`list[^n] += x` 的读-改-写归约归 C17；C31 只保证索引显式化 + 单次求值 | C17 落地后看到的是普通 int 索引表达式，无需感知 `^`；无 RunAfter 依赖 |
| **C06 unsafe-ffm** | 指针/`Span<byte>` 与 FFM 的指针侧归 C06；`^`/`..` 不对指针合法 | ReceiverKind=Pointer 不命中；Span 切片（托管侧）归 C31，零交叠 |
| **C08 java-keywords（链尾）** | `Slice`/`Substring` 与生成临时名 `__ir_*` 均非 Java 保留字 | 无需协调；若助手术名撞保留字由 C08 统一改名表覆盖 |
| **注册表/链序测试** | 本分支独占的注册表变更 = 删一行 stub + 加一行 Implemented + 工厂一分支；`PassChainOrderTests`/`PassStubCatalogTests` 断言同步更新 | 与同期翻转其他 stub 的工作可能在测试文件上小冲突，按注册表评审节奏 rebase 解决 |

## 12. Worktree 分配

- 分支：`feature/preproc-c31-index-range`
- 目录：`.worktrees/preproc-c31-index-range`
- base：`f68a783c`（`feat(preprocessing): register 23 merged-family stub passes`）

```powershell
git worktree add .worktrees/preproc-c31-index-range -b feature/preproc-c31-index-range f68a783c
```

---

## 附：开放问题（实现前需评审确认）

- **Q1 支撑库归属终裁**：本设计采用「扩展 Cs2jArray」（catalog 首选、注入机制零新增）。若评审认为切片助手与 array-method 静态收口的发布节奏应解耦（例如 array-method 长期 opt-in 而 C31 计划更早默认开），备选新建 Cs2jIndexRange 工程——这会新增一个 `PassSupportLibrary` 枚举值与过滤分支，需在 M2 前定案。
- **Q2 独立 Index/Range 一等值的去向**：M1–M3 对 `Index`/`Range` 变量、参数、字段显式报错（IR120）。M4 是坚持「只做同方法可配对局部降级、其余永久显式拒绝」，还是引入 `Cs2jIndex/Cs2jRange` 支撑结构（须同时新增 compat 类与 TypeMappings，成本显著增加）？取决于 sdk 画像中一等 Index/Range 的真实命中率（M4 扫描数据支撑决策）。
- **Q3 共享临时提升基建的落点时点**：TempLifter 首期放 `IndexRange/` 内、接口冻结后随 expression-desugar 上收；是否在 M1 即直接落 `Core/Preprocessing/Expressions/`（提前建共享目录、并把 NullCoalesce lifter 一并迁移），影响 M1 的改动半径与回归面。
- **Q4 默认开启是否以 Java 半区 e2e 齐备为硬前提**：§8 第 3 条当前要求 compat `slice` + string/Span 运行时全绿才翻默认开；若希望更早默认开，可退而以「.NET 半区全绿 + Java 半区仅数组/字符串两组」为门槛，Span 组保持 opt-in——需评审确认风险敞口。
- **Q5 `?[^n]`/`?[..]` 的处置强度**：M1–M3 直接 IR130 失败是否会卡住真实代码（C35 尚未落地）；备选在 C31 内提供「接收者临时 + null 条件三元」有限收编（提前实现 C35 的一小块），代价是两族接口耦合提前，需与 C35 未来设计对齐。
