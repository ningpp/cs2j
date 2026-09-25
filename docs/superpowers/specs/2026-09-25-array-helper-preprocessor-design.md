# 数组方法/构造预处理器（Array Helper Preprocessor）设计

日期：2026-09-25

全文统一命名：C# 侧支撑类为 `Cs2jArray.ArrayHelper`（lib 项目 `Cs2jArray`），
Java 侧对应物为 compat 既有类 `io.github.ningpp.compat.ArrayHelper`（扩展而非新建，见 §7.1）。

## 1. 背景与问题

C# 数组与 Java 数组的能力并不对称。主转换器现状是把数组特判散落在各 transformer 里，
通过对 `tests/` 既有夹具与转换器源码的核查，现状通路是：

- **可直译部分已工作**：`arr.Length` → `arr.length`（`IdentifierExpressionTransformer`
  的 `IsSystemArrayLengthOnConcreteArray` / `IsDeclaredAsConcreteArray` 特判）；
  `arr[i]` 一维下标直译；`T[][]` 交错类型直译；`new T[n]` 直译。
- **矩形多维走"降级为交错"通路**：`int[,]` 类型映射 `int[][]`
  （`ConversionContext` 注释明确），`arr[i,j]` → `arr[i][j]`
  （`ElementAccessTransformer` 多参数路径平铺方括号），`new int[3,4]` → `new int[3][4]`
  （`ObjectCreationTransformer` 尺寸平铺；Java 语义"3 行各 int[4]"恰好等价矩形），
  `typeof(T[,])` 经 `TypeHelper.getMultiDimArrayClass` 记录 rank。
- **System.Array 类型符号**经 `TypeMappings.json` 映射到 compat 手写类
  `io.github.ningpp.compat.CSharpArray`（类型级条目 `System.Array/Array → CSharpArray`），
  其静态方法（`sort/binarySearch/clear/copy/indexOf/lastIndexOf/reverse/resize/fill/
  empty/createInstance`）与实例包装器形态（`of/getLength/getRank/getValue/setValue/
  getLowerBound/getUpperBound`）均已实现且有测试。
- **数组实例成员无通路或语义有损**：`arr.Clone()`、`arr.CopyTo(...)`、`arr.GetValue(...)`、
  `arr.SetValue(...)`、`arr.GetLength(d)`、`arr.Rank`、`arr.LongLength` 在 receiver 为
  具体数组类型（`int[]`、`string[]`，而非 `System.Array`）时，主转换器没有对应处理，
  直译产物是非法 Java（Java 数组上没有这些实例成员）。
- **多维 `.Length` 语义缺陷**：C# 矩形数组的 `Length` 是全部维度元素总数，
  现状直译为 `arr.length`（仅外层长度），已是错误语义。
- **`System.Array` 静态方法**（`Array.Sort` 等）依赖"类型映射 + Pascal→camel 推导"
  路由到 `CSharpArray`——`CSharpArray` 未实现的方法（`Find`/`Exists`/`TrueForAll`/
  `ConvertAll`/`ForEach`/`ConstrainedCopy` 等）直接生成编译失败的调用。

问题归纳：

- "Java 缺位或语义不同"的数组构造没有统一收口点，主转换器里的数组特判持续膨胀
  （`IdentifierExpressionTransformer`/`ElementAccessTransformer`/
  `ObjectCreationTransformer`/`ArrayIterableConversionRewriter` 各管一段）；
- 同一语义（如 `Array.Sort`）在"receiver 是 `System.Array` 类型"与"receiver 是
  具体数组类型"两条路径下行为不一致，后者完全缺位；
- 调用点缺方法时只能在 compat 里补 `CSharpArray` 静态方法，类型映射配置与
  compat 实现互相追赶。

## 2. 目标

- 新增预处理阶段 **array-method**：把 C# 中所有"纯数组相关、且 Java 没有对应
  语法/语义/用法"的构造，在预处理阶段改写为全限定静态调用
  `Cs2jArray.ArrayHelper.XXX(...)`（不依赖 using，与 `Cs2jUnsigned.UnsignedMath`
  调用形态完全同构）；
- 改写后残留的 C# 中，数组相关只允许出现 Java 原生就支持的构造：数组 `length`
  字段（`arr.Length` 一维）、交错数组 `T[][]`、元素下标读写 `arr[i]`/`arr[i][j]`、
  数组 for-each——**以"Java 数组原生能力"为边界：边界之内（可直译）留给主转换器，
  边界之外（Java 缺位或语义不同）全部由本预处理器收口到 `Cs2jArray.ArrayHelper`**；
- C# 侧支撑类以独立 lib 项目 `Cs2jArray` 落地（嵌入资源分发 +
  ProjectReference 注入，完全 mirror `2026-09-24-unsigned-math-lib-project-design.md`
  §4/§5.1 的机制）；主转换器把它仅当符号来源：不规划 Maven 模块、不转换其源文件；
- Java 侧在 `csharptojava-compat` 的既有 `io.github.ningpp.compat.ArrayHelper` 中
  手写对应方法（camelCase，尽量委托既有 `CSharpArray`/JDK），调用点经
  `TypeMappings.json` 精确映射；
- 复用 UnsignedTypeRewriter 的"脏检查(IsDirty) → 局部标注(Annotations) →
  局部格式化 → 重新解析校验、绝不产出语法损坏中间 C#"的安全链路；
- clean 文件字节级保真；每个收口项均有改写器正向/负向断言与 compat 边界值 JUnit。

## 3. 构造清单与边界分类（决策表）

分类定义（每个 C# 数组构造必须且只能落入一类）：

- **[A] 可直译**：Java 有原生等价物，主转换器已有正确通路，预处理器**不碰**。
  注意 [A] 包含"主转换器既有规范化通路"（矩形→交错的类型/下标/创建降级），
  因其产物是 Java 原生形态且已被测试锁定。
- **[B] 必须 ArrayHelper 收口**：Java 缺位或语义不同，预处理器改写为
  `Cs2jArray.ArrayHelper.XXX(...)`（个别项改写为纯语法等价物，见表内注明）。
- **[C] 非数组专属/由其它通路处理**：本功能不覆盖（non-goal，理由见表）。

### 3.1 数组实例成员/属性（receiver 为具体数组类型 `int[]`/`int[][]` 等）

判定前提：receiver 的 `TypeInfo.Type` 是 `IArrayTypeSymbol`。receiver 静态类型为
`System.Array` 的情况整体归 [C]（见 3.4 第 1 行）。

| C# 构造 | 分类 | 改写形态 / 现状通路 | 理由 |
| --- | --- | --- | --- |
| `arr.Length`（一维） | [A] | 主转换器直译 `arr.length` | Java 原生 length 字段，白名单 |
| `arr.Length`（多维 `int[,]`） | [B] | `Cs2jArray.ArrayHelper.GetLength(arr)`（元素总数） | C# 语义是全维乘积；现状直译为外层长度是错误语义（§1） |
| `arr.LongLength` | [B] | `Cs2jArray.ArrayHelper.GetLongLength(arr)` | Java 无 long 形态 length；一维/多维统一收口 |
| `arr.Rank` | [B] | 内联常量字面量（`1`、`2`…），不产生调用 | receiver 是 `IArrayTypeSymbol` 时 rank 编译期已知；内联零运行时成本、残留为零 |
| `arr.Clone()` | [B] | `Cs2jArray.ArrayHelper.Clone(arr)` | Java 数组无 public `clone()` 调用形态（Object.clone 受保护），主转换器无通路 |
| `arr.CopyTo(dst, idx)` | [B] | `Cs2jArray.ArrayHelper.CopyTo(arr, dst, idx)` | Java 数组无实例 CopyTo；`System.arraycopy` 需静态化收口 |
| `arr.GetValue(i)` / `GetValue(i,j)` / `GetValue(int[])` | [B] | `Cs2jArray.ArrayHelper.GetValue(arr, i[, j…])` | Java 数组无 GetValue；多维/一维统一收口 |
| `arr.SetValue(v, i)` / `SetValue(v,i,j)` / `SetValue(v,int[])` | [B] | `Cs2jArray.ArrayHelper.SetValue(arr, v, i[, j…])` | 同上；参数序按 `SetValue(array, value, indices)` |
| `arr.GetLength(d)` | [B] | `Cs2jArray.ArrayHelper.GetLength(arr, d)` | Java 无按维长度；`d` 为常量也不内联（保留维度校验语义） |
| `arr.GetLongLength(d)` | [B] | `Cs2jArray.ArrayHelper.GetLongLength(arr, d)` | 同上 long 形态 |
| `arr.GetLowerBound(d)` | [B] | `Cs2jArray.ArrayHelper.GetLowerBound(arr, d)` | 值恒 0，但不内联——保留 C# 对 `d` 的越界校验（Rank 校验抛 IndexOutOfRange）语义 |
| `arr.GetUpperBound(d)` | [B] | `Cs2jArray.ArrayHelper.GetUpperBound(arr, d)` | = GetLowerBound + GetLength − 1；空维度上界为 −1 的 C# 语义由 compat 实现 |
| `arr.GetEnumerator()` | [C] | 数组→IEnumerable 桥接已有通路（主转换器生成 `ArrayHelper.asIterable`/`toList`，见 2026-05-23 spec） | 非数组专属成员（ICollection/IEnumerable 语义），既有通路覆盖 |
| `arr.ToString()` | [C] | object 默认语义直译 | 非数组专属 |
| `arr.GetHashCode()` | [C] | object 默认语义直译 | 非数组专属 |
| `arr.SyncRoot` | [C] | 不改写（罕见） | ICollection 同步原语成员，非数组专属；出现频率极低，留待单独需求 |
| `arr.IsFixedSize` / `arr.IsReadOnly` / `arr.IsSynchronized` | [C] | 不改写（常量 true/true/false） | ICollection 元数据属性，非数组专属；若后续出现真实需求按常量内联单独处理 |

### 3.2 数组创建、初始化器与多维/交错

| C# 构造 | 分类 | 改写形态 / 现状通路 | 理由 |
| --- | --- | --- | --- |
| `new T[n]` | [A] | 直译 `new T[n]` | Java 原生 |
| `T[] { ... }` 集合初始化器 / `new T[] { ... }` | [A] | 直译 `new T[] { ... }` | Java 原生数组初始化器 |
| `new T[n][]`（交错，显式外层尺寸） | [A] | 直译 `new T[n][]` | Java 原生；C# 外层元素为 null，Java 同为 null，一致。（注：C# 不允许 `new T[n][m]` 单表达式，该形态只存在于 Java 产物中） |
| `T[][]` 类型/嵌套初始化器 | [A] | 直译 | Java 原生交错数组 |
| `int[,]` 等矩形**类型声明**（字段/参数/局部/返回值） | [A] | 主转换器既有降级 `int[][]` | 既有通路正确且被测试锁定；预处理若再降级会造成"类型已降级、创建未降级"的非法 C#，必须成套，收益为负（见 §3.5 裁决 2） |
| `new int[3,4]`（矩形，无初始化器） | [A] | 主转换器既有降级 `new int[3][4]`（Java 语义恰好为"3 行各 4 列"全矩阵） | 同上 |
| `new int[,] { {1,2},{3,4} }`（矩形，带初始化器） | [A] | 主转换器既有降级（嵌套初始化器 → 交错初始化器） | 同上；该形态由既有转换测试锁定 |
| `arr[i,j]`（矩形下标读/写） | [A] | 主转换器既有降级 `arr[i][j]` | Java 原生交错下标即白名单；**不采用**改写为 `ArrayHelper.getValue` 的方案（语义正确但装箱 + 类型擦除 + 破坏 foreach 白名单，见 §3.5 裁决 3） |
| `foreach (var x in arr)`（arr 为矩形多维） | [B] | 改写为嵌套 foreach：`foreach (var __row in arr) foreach (var x in __row) …`（合成行变量名带冲突检查） | C# 多维 foreach 摊平迭代全部元素；降级为交错后单层 foreach 只迭代行——语义不同。改写为嵌套循环后残留是白名单内的数组 for-each |
| `foreach (var x in arr)`（一维/交错） | [A] | 直译 | Java 原生 for-each |
| 多维 `typeof(T[,])` / 反射 rank | [C] | 既有 `TypeHelper.getMultiDimArrayClass` 通路 | 反射通路，非本设计范围 |

### 3.3 System.Array 静态方法（`Array.XXX(...)` 调用）

判定前提：`Array` 标识符解析到 `System.Array` 类型符号（SemanticModel 判定，
用户自定义 `Array` 类不误伤）。按方法名 + 参数形态（参数个数/是否 ref/委托参数）改写。

| C# 构造 | 分类 | 改写形态 | 理由 |
| --- | --- | --- | --- |
| `Array.Sort(a)` 及重载（`(a, comparer)`、`(a, index, length)`、`(a, index, length, comparer)`、`(keys, items)`、`(keys, items, comparer)`） | [B] | `Cs2jArray.ArrayHelper.Sort(...)` 同形态透传实参 | 收口后不再依赖 `CSharpArray` 是否实现；Java 侧委托既有实现 |
| `Array.BinarySearch`（`(a,v)`、`(a,v,cmp)`、`(a,i,l,v)`、`(a,i,l,v,cmp)`） | [B] | `Cs2jArray.ArrayHelper.BinarySearch(...)` | 同上；补码插入点语义由 compat 保证 |
| `Array.Clear(a, i, l)` | [B] | `Cs2jArray.ArrayHelper.Clear(a, i, l)` | 同上 |
| `Array.Resize(ref a, n);`（语句） | [B] | `a = Cs2jArray.ArrayHelper.Resize(a, n);`（语句级改写） | Java 无 ref；返回值形态（§5 签名、§6.4 安全条件） |
| `Array.IndexOf`（`(a,v)`、`(a,v,start)`、`(a,v,start,count)`） | [B] | `Cs2jArray.ArrayHelper.IndexOf(...)` | 同 Sort |
| `Array.LastIndexOf`（同上三形态） | [B] | `Cs2jArray.ArrayHelper.LastIndexOf(...)` | 同上 |
| `Array.Reverse(a)` / `Reverse(a, i, l)` | [B] | `Cs2jArray.ArrayHelper.Reverse(...)` | 同上 |
| `Array.Find<T>(a, match)` | [B] | `Cs2jArray.ArrayHelper.Find(a, match)` | `CSharpArray` 未实现，现状生成编译失败调用 |
| `Array.FindAll<T>` / `FindIndex<T>`（+ start/count 重载）/ `FindLast<T>` / `FindLastIndex<T>` | [B] | `Cs2jArray.ArrayHelper.FindAll/FindIndex/FindLast/FindLastIndex(...)` | 同上 |
| `Array.Exists<T>(a, match)` | [B] | `Cs2jArray.ArrayHelper.Exists(a, match)` | 同上 |
| `Array.TrueForAll<T>(a, match)` | [B] | `Cs2jArray.ArrayHelper.TrueForAll(a, match)` | 同上 |
| `Array.ConvertAll<TIn,TOut>(a, conv)` | [B] | `Cs2jArray.ArrayHelper.ConvertAll(a, conv)` | 同上 |
| `Array.ForEach<T>(a, action)` | [B] | `Cs2jArray.ArrayHelper.ForEach(a, action)` | 同上 |
| `Array.Copy(s, d, n)` / `Copy(s, si, d, di, n)` | [B] | `Cs2jArray.ArrayHelper.Copy(...)` 同形态 | 收口统一 |
| `Array.ConstrainedCopy(s, si, d, di, n)` | [B] | `Cs2jArray.ArrayHelper.ConstrainedCopy(...)` | 原子性语义（全部成功或全部不复制）由 compat 实现 |
| `Array.Empty<T>()` | [C] | 既有通路：类型映射 + camel 推导 → `CSharpArray.empty()`（已实现） | 不在用户清单；既有通路工作，避免范围膨胀 |
| `Array.CreateInstance(...)` | [C] | 既有通路 → `CSharpArray.createInstance`（已实现） | 返回 `System.Array` 类型，天然属于 [C] 类型通路（3.4） |
| `Array.Fill(...)` | [C] | 既有通路 → `CSharpArray.fill`（已实现） | 同 Empty |
| `Array.AsReadOnly` / `Array.ConvertAll 之外的其余静态成员` | [C] | 未收口，出现时按"发现缺口补闭集"流程处理（§11） | 罕见；与 unsigned spec §9 的兜底策略一致 |

### 3.4 类型层与协变

| C# 构造 | 分类 | 处理 | 理由 |
| --- | --- | --- | --- |
| `System.Array` 作为**静态类型**（变量/参数/字段/返回值声明，及其上的成员访问 `a.Length`、`a.GetValue(...)`） | [C] | 既有 `System.Array → CSharpArray` 包装通路（实例方法 `getLength/getValue/setValue/getRank/getLowerBound/getUpperBound/binarySearch` 均已实现） | 该通路已完整工作且有测试；预处理器若把 `a.GetValue(i)` 也改写为 `ArrayHelper.GetValue(a, i)`，实参 `a`（CSharpArray wrapper）与 Java 侧 `getValue(Object)` 期望的裸数组类型不符，反而制造新缺陷。**receiver 为 `System.Array` 类型是本设计显式排除面** |
| `Array` 裸标识符引用（`typeof(Array)`、`is Array` 等） | [C] | 同上既有映射 | 类型符号通路 |
| 数组协变赋值 `object[] o = new string[10]` | [A] | 直译（Java 原生协变数组） | Java 数组同样协变，读侧语义一致 |
| 协变数组写入 `o[0] = 5;` | [A] | 直译（运行时拒绝写入，Java 抛 `ArrayStoreException`，C# 抛 `ArrayTypeMismatchException`，时机一致：写入时） | 拒绝行为等价；异常**类型名**差异属异常映射通路（compat 尚无 `ArrayTypeMismatchException` 类，见 §12） |
| LINQ `.Where/.Select/...` 在数组上 | [C] | 既有 LINQ/Stream 通路 + 数组→`IEnumerable` 桥接（`ArrayHelper.toList/asIterable` 生成点、`ArrayIterableConversionRewriter`） | 本质是 `IEnumerable<T>` 扩展方法，非数组专属；未发现"数组是其唯一未处理场景"的证据，故不收口（若端到端发现缺口按 §11 流程补） |
| `Span<T>`/`Memory<T>`/`ArraySegment`/`ArrayPool<T>`/`stackalloc`/`fixed`/`System.Buffer` | [C] | 既有独立通路（TypeMappings 已有条目；`Buffer` 有 methodMappings；fixed/unsafe 有独立 spec） | 非本设计范围 |

### 3.5 关键裁决（含对既有假设的修订说明）

1. **Java 侧复用既有 `io.github.ningpp.compat.ArrayHelper`，不新建同名新类**。
   该类已存在（2026-05-23 spec 引入，`toList/asListView/asIterable/copyArray/sortParallel`
   服务主转换器的数组↔集合桥接生成点）。新建同类名类会撞包内类名；而其定位
   "Array utilities" 与本设计收口方法职责兼容。裁决：在既有类中**新增**方法组，
   两组方法集不相交（见 §7.2），并保留各自调用来源的回归测试。
2. **矩形多维的类型/创建/下标三层降级保留在主转换器，不前移到预处理**。
   修订说明：预处理若降级类型（`int[,]` → `int[][]`），必须同步改写创建表达式
   `new int[3,4]`——但 C# 没有单表达式等价交错物（`new int[3][4]` 不是合法 C#），
   只能走 `ArrayHelper.newRectArray` 之类的 lib 调用，反而把 Java 原生可直译的
   `new int[3][4]` 产物变成反射分配 + 强转，属负收益。主转换器既有降级通路
   产物正确（`new int[3][4]` 在 Java 中恰好分配全矩阵）、被测试锁定，保留。
   预处理只收口该通路**覆盖不到**的部分：多维 `.Length`（总长，现状语义错误）、
   `GetLength(d)` 等元数据、`GetValue/SetValue`、多维 foreach 摊平。
3. **多维下标 `arr[i,j]` 不改写为 `ArrayHelper.getValue(...)`**（修订用户输入映射
   表中的该项）。理由：① 降级通路 `arr[i][j]` 零装箱零反射，是 Java 原生白名单
   形态；② `getValue` 返回 `Object` 会引入装箱与 unbox cast，值类型数组性能与
   类型安全性双损；③ foreach 白名单会被连带破坏。收口 getValue 只用于
   `GetValue/SetValue` **显式方法调用**（其 C# 语义本来就是 object 装箱，收口后
   无额外损失）。
4. **`arr.Rank` 内联常量，`GetLowerBound/GetUpperBound` 收口不内联**。Rank 在
   receiver 为 `IArrayTypeSymbol` 时编译期已知，内联后残留为零；GetLowerBound
   值虽恒 0，但内联会丢失 C# 对维度参数的越界校验，收口保留校验语义。
5. **`System.Array` 静态方法调用（receiver 无关）全部 [B] 收口**，即便
   `CSharpArray` 已有实现（Sort/Copy/IndexOf 等）。理由：收口后调用闭集由改写器
   生成（封闭集合），不再依赖"类型映射 + camel 推导 + compat 恰好实现"三者对齐；
   `CSharpArray` 的既有静态方法成为 Java 侧 `ArrayHelper` 的委托目标（§7.3），
   而非调用点路由目标。既有 `System.Array → CSharpArray` 类型条目保留，仅继续
   服务 3.4 第 1 行的 [C] 类型通路。

## 4. 非目标

- 不改主转换器既有的矩形→交错降级通路（类型/创建/下标，§3.5 裁决 2）；
- 不处理 receiver 静态类型为 `System.Array` 的成员访问（既有 `CSharpArray`
  包装通路，§3.4 第 1 行）；
- 不处理 LINQ/枚举/集合通路、`Span`/`Memory`/`ArraySegment`/`ArrayPool`/
  `Buffer`/unsafe/fixed；
- 不收口 `Array.Empty/CreateInstance/Fill`（既有通路覆盖，§3.3）；
- 不消除异常类型名差异（`ArrayTypeMismatchException` vs `ArrayStoreException`，
  属异常映射通路）；
- 不保证 `Array.Sort` 的排序稳定性与 C# 逐位一致（C# introsort 不稳定、
  Java 对象 TimSort 稳定——既有 `CSharpArray.sort` 同款差异，见 §12）；
- 不处理 TypeScript 路径（TS 转换不经过本预处理）；
- 不解决宿主 TFM 低于 net10.0 的引用兼容问题（与 unsigned 相同的 NU1201 策略）。

## 5. C# 侧：lib 项目 `Cs2jArray`

### 5.1 项目形态（与 `Cs2jUnsignedMath` 同构）

- 路径：`src/Cs2jArray/Cs2jArray.csproj` + `src/Cs2jArray/ArrayHelper.cs`；
  加入 `CSharpToJavaConverter.slnx`。
- `TargetFramework`：`net10.0`（与主链全部 csproj 一致）；`LangVersion`：latest
  （本项目不需要 `>>>`，无特殊语法需求）；`Nullable`：disable；
  `ImplicitUsings`：disable。
- `public static class ArrayHelper`，namespace `Cs2jArray`。分发机制与
  `Cs2jUnsignedMath` 完全一致：CLI csproj 以 `EmbeddedResource`（显式
  `LogicalName`）嵌入两份文件，机制上构建期从 `src/Cs2jArray/` 的同一物理文件
  读取，结构上不存在第二份副本可漂移。
- **推荐"独立 lib 项目 + 嵌入资源分发 + ProjectReference 注入"而非更轻量的
  单文件支撑**，理由与 unsigned spec §4 相同：① 多项目输入下保证唯一真源、
  单文件模式的 `_Cs2j*` 平文件方案已被 unsigned 项目证明存在"N 份重复转换、
  LangVersion 泄漏、字符串常量维护难"三问题；② 架构一致性——两个支撑库走同一
  套释放/注入/过滤机制，后续维护成本最低；③ 单文件模式仍保留（§6.6），
  只是项目模式不采用平文件。

### 5.2 方法签名（覆盖全部 [B] 项；PascalCase，调用闭集由改写器生成）

C# 侧签名用**泛型**（真 C#，语义精确、值类型数组无装箱读写）；Java 侧对应
方法用 `Object` + 反射形态（§7.2），调用点经泛型擦除后实参兼容 `Object` 参数，
两侧仅要求方法名与参数个数对齐。

```csharp
namespace Cs2jArray
{
    public static class ArrayHelper
    {
        // ---- 元数据（3.1）----
        public static int GetLength(System.Array array);            // 多维 .Length 总长
        public static long GetLongLength(System.Array array);
        public static int GetLength(System.Array array, int dimension);
        public static long GetLongLength(System.Array array, int dimension);
        public static int GetLowerBound(System.Array array, int dimension);   // 恒 0，保留维度校验
        public static int GetUpperBound(System.Array array, int dimension);   // GetLowerBound+GetLength-1

        // ---- 实例成员收口（3.1）----
        public static System.Array Clone(System.Array array);       // 浅拷贝
        public static void CopyTo(System.Array source, System.Array destination, long index);
        public static T GetValue<T>(T[] array, int index);
        public static T GetValue<T>(T[][] array, int index0, int index1);
        public static T GetValue<T>(T[][][] array, int i0, int i1, int i2);
        public static object GetValue(System.Array array, params int[] indices);   // 兜底：变量 rank / params 形态
        public static void SetValue<T>(T[] array, object value, int index);
        public static void SetValue<T>(T[][] array, object value, int i0, int i1);
        public static void SetValue<T>(T[][][] array, object value, int i0, int i1, int i2);
        public static void SetValue(System.Array array, object value, params int[] indices);

        // ---- System.Array 静态收口（3.3）----
        public static void Sort(System.Array array);
        public static void Sort(System.Array array, System.Collections.IComparer comparer);
        public static void Sort(System.Array array, int index, int length);
        public static void Sort(System.Array array, int index, int length, System.Collections.IComparer comparer);
        public static void Sort(System.Array keys, System.Array items);
        public static void Sort(System.Array keys, System.Array items, System.Collections.IComparer comparer);
        public static int BinarySearch(System.Array array, object value);
        public static int BinarySearch(System.Array array, object value, System.Collections.IComparer comparer);
        public static int BinarySearch(System.Array array, int index, int length, object value);
        public static int BinarySearch(System.Array array, int index, int length, object value, System.Collections.IComparer comparer);
        public static void Clear(System.Array array, int index, int length);
        public static T[] Resize<T>(T[] array, int newSize);        // 调用点改写为 a = Resize(a, n);
        public static int IndexOf(System.Array array, object value);
        public static int IndexOf(System.Array array, object value, int startIndex);
        public static int IndexOf(System.Array array, object value, int startIndex, int count);
        public static int LastIndexOf(System.Array array, object value);
        public static int LastIndexOf(System.Array array, object value, int startIndex);
        public static int LastIndexOf(System.Array array, object value, int startIndex, int count);
        public static void Reverse(System.Array array);
        public static void Reverse(System.Array array, int index, int length);
        public static T Find<T>(T[] array, System.Predicate<T> match);
        public static T[] FindAll<T>(T[] array, System.Predicate<T> match);
        public static int FindIndex<T>(T[] array, System.Predicate<T> match);
        public static int FindIndex<T>(T[] array, int startIndex, int count, System.Predicate<T> match);
        public static T FindLast<T>(T[] array, System.Predicate<T> match);
        public static int FindLastIndex<T>(T[] array, System.Predicate<T> match);
        public static int FindLastIndex<T>(T[] array, int startIndex, int count, System.Predicate<T> match);
        public static bool Exists<T>(T[] array, System.Predicate<T> match);
        public static bool TrueForAll<T>(T[] array, System.Predicate<T> match);
        public static TOutput[] ConvertAll<TInput, TOutput>(TInput[] array, System.Converter<TInput, TOutput> converter);
        public static void ForEach<T>(T[] array, System.Action<T> action);
        public static void Copy(System.Array source, System.Array destination, int length);
        public static void Copy(System.Array source, int sourceIndex, System.Array destination, int destinationIndex, int length);
        public static void ConstrainedCopy(System.Array source, int sourceIndex, System.Array destination, int destinationIndex, int length);
    }
}
```

参数/返回语义与 C# 原语义对齐方式：

- **装箱**：`GetValue/SetValue` 的 `System.Array + object` 兜底重载保持 C# 原生
  装箱语义；rank 已知的 `T[]`/`T[][]` 特化重载返回 `T`，改写点既有的
  `(int)arr.GetValue(0)` unbox cast 变为冗余但合法（C# 允许 `(int)(int)`）。
  改写器按 receiver 的 `IArrayTypeSymbol` rank 选择特化重载，rank > 3 或
  `params int[]` 实参形态走兜底重载。
- **多维**：`GetLength(array)`（总长）为矩形数组 `.Length` 收口专用；
  `GetLength(array, d)` 按维取长。lib 内部仅用白名单原语实现（§5.3）。
- **协变异常**：`SetValue` 内部 `(T)value` 强转在类型不匹配时抛
  `InvalidCastException`，与 C# `SetValue` 一致；`Copy/ConstrainedCopy` 的元素
  兼容性检查与 `ArrayStoreException` 传播行为在 Java 侧实现（§7.3）。
- **`System.Collections.IComparer` 参数**：保留 C# 原生接口类型，lib 内部经
  接口调用 `comparer.Compare(a, b)`；null 表示自然序（`IComparable`）。

### 5.3 实现约束（"可转换 C# 子集" + 防自递归）

`ArrayHelper.cs` 是唯一真源，同时被单文件模式原样释放（§6.6），因此内容必须
满足两条硬约束（与 `UnsignedMath.cs` 头注释同款契约）：

1. **可转换 C# 子集**：实现体只用白名单原语——一维/交错数组、`arr.Length`、
   `arr[i]`、数组 foreach、基本控制流、`IComparable`/`IComparer`/委托调用、
   `(T)`/`(T[])` 强转、`(T[])new object[n]` 泛型数组创建模式。**不得**出现
   `Array.*` 静态调用或 `System.Array` 接收者成员（会被主转换器映射到
   `CSharpArray` 包装形态，与运行时裸数组不符）。
2. **防自递归**：array-method 预处理对早前释放的 `Cs2jArray/` 目录只原样复制、
   不参与改写（mirror `FindOwnReleaseLibDirectories`/`IsUnderOwnReleaseLib`
   机制）——lib 实现本身不含改写触发名单成员，双保险。

典型实现示例（决策表 §3.1 项逐一有对应实现，此处示意风格）：

```csharp
public static int GetLength(System.Array array)
{
    // 交错嵌套求和；外层为空时内层长度信息已在矩形→交错降级中丢失，返回 0（已知限制）
    int total = 0;
    foreach (object? row in ToRows(array)) { /* ... */ }
    ...
}
public static T GetValue<T>(T[][] array, int index0, int index1) => array[index0][index1];
public static T Find<T>(T[] array, System.Predicate<T> match)
{
    foreach (var item in array) { if (match(item)) return item; }
    return default!;
}
```

## 6. 转换流水线改动：ArrayMethodRewriter 与预处理器接入

### 6.1 组件与文件布局（mirror UnsignedTypeRewriter）

```
src/CSharpToJava.Core/ArrayMethodRewriter/
    ArrayMethodRewriter.cs            # 公共入口（Rewrite(source, semanticModel, options)）
    ArrayMethodSyntaxRewriter.cs      # CSharpSyntaxRewriter 主体
    ArrayRewriteAnnotations.cs        # 局部标注（Kind = "Cs2jArrayRewrite"）
    ArrayMethodRewriterOptions.cs     # record（Verbose/Strict/EnableInstanceRewrite/EnableStaticRewrite/EnableForeachFlatten）
    ArrayMethodRewriterResult.cs      # record（OutputCode/Changed/Diagnostics/Statistics）
    ArrayMethodRewriterStatistics.cs  # FilesScanned/FilesTransformed/FilesSkippedClean/
                                      # InstanceMemberRewrites/StaticArrayCalls/ResizeStatements/
                                      # ForeachFlattened/RankInlined/MultiDimLengthRewrites
    ArrayMethodRewriterDiagnostic.cs  # Severity/Message（复用诊断形态）
src/CSharpToJava.CLI/ProjectArrayMethodPreprocessor.cs   # 项目模式（mirror ProjectUnsignedTypePreprocessor）
src/CSharpToJava.CLI/ArrayHelperSupportFiles.cs           # 嵌入资源读取/释放
src/CSharpToJava.CLI/Cs2jArrayProject.cs                  # csproj 文件名精确判定
```

### 6.2 触发判定（SemanticModel 精判，不误伤普通对象）

脏检查 `IsDirty`（纯语法快速路径）：文件 token 中命中改写目标成员名集合
（`Clone/CopyTo/GetValue/SetValue/GetLength/GetLongLength/GetLowerBound/
GetUpperBound/Sort/BinarySearch/Clear/Resize/IndexOf/LastIndexOf/Reverse/Find/
FindAll/FindIndex/FindLast/FindLastIndex/Exists/TrueForAll/ConvertAll/ForEach/
Copy/ConstrainedCopy/Rank/LongLength`），或含 `foreach` 语句（多维摊平候选）。
未命中即 clean，字节级保真复制。

改写精判（仅在脏文件上运行，全部依赖 SemanticModel）：

1. **实例成员**：`MemberAccessExpressionSyntax`/`InvocationExpressionSyntax` 的
   receiver 经 `GetTypeInfo(...)` 取 `IArrayTypeSymbol` → 命中 §3.1 名单才改写。
   一维 `arr.Length` 明确排除（白名单）；`string.Length`、`List.Sort()`、
   自定义类型的 `Clone()` 等因 receiver 类型不是数组而天然不命中。
2. **静态调用**：`Array.XXX(...)` 的 `Array` 经 `GetSymbolInfo` 解析为
   `System.Array` 类型符号（`INamedTypeSymbol`，`SpecialType.System_Array`）
   才改写——用户自定义 `Array` 类、`SomeNs.Array` 不误伤。方法名 + 参数个数
   决定目标重载；清单外方法名不动（后续按缺口流程补）。
3. **Rank 内联**：receiver 是 `IArrayTypeSymbol` 时取 `.Rank` 生成数值字面量。
4. **Resize 语句改写**：`Array.Resize(ref a, n)` 必须是 ExpressionStatement 且
   第一实参带 `ref`，且 `a` 满足安全赋值目标（`IsSafeAssignmentTarget`：
   局部/参数/无条件副作用的字段成员访问——复用 UnsignedTypeSyntaxRewriter 的
   判定逻辑）→ `a = Cs2jArray.ArrayHelper.Resize(a, n);`。不满足安全条件
   （ref 属性、嵌套表达式、多重引用）→ 保守跳过 + Warning 诊断（不产出
   语义损坏代码）。
5. **多维 foreach 摊平**：`ForEachStatementSyntax` 的 collection 类型
   `IArrayTypeSymbol.Rank > 1` → 嵌套展开，合成行变量名（`__cs2j_row0` 起，
   作用域冲突检查后缀递增）。

### 6.3 改写形态映射表（C# → C#，产物为可转换子集）

| 源形态 | 改写后 |
| --- | --- |
| `<arr>.Clone()` | `Cs2jArray.ArrayHelper.Clone(<arr>)` |
| `<arr>.CopyTo(dst, idx)` | `Cs2jArray.ArrayHelper.CopyTo(<arr>, dst, idx)` |
| `<arr>.GetValue(i)` / `(i, j)` | `Cs2jArray.ArrayHelper.GetValue(<arr>, i)` / `(<arr>, i, j)` |
| `<arr>.SetValue(v, i)` / `(v, i, j)` | `Cs2jArray.ArrayHelper.SetValue(<arr>, v, i)` / `(<arr>, v, i, j)` |
| `<arr>.GetLength(d)` / `GetLongLength(d)` / `GetLowerBound(d)` / `GetUpperBound(d)` | `Cs2jArray.ArrayHelper.GetLength(<arr>, d)` 等 |
| `<arr>.LongLength` | `Cs2jArray.ArrayHelper.GetLongLength(<arr>)` |
| `<arr>.Length`（receiver Rank>1） | `Cs2jArray.ArrayHelper.GetLength(<arr>)`（元素总数） |
| `<arr>.Rank` | 数值字面量（编译期常量） |
| `Array.Sort(a, cmp)` 等 3.3 全表 | `Cs2jArray.ArrayHelper.Sort(a, cmp)` 等（实参原样透传） |
| `Array.Resize(ref a, n);` | `a = Cs2jArray.ArrayHelper.Resize(a, n);` |
| `foreach (var x in mdArr) …`（Rank>1） | `foreach (var __cs2j_row0 in mdArr) foreach (var x in __cs2j_row0) …` |

全部调用为全限定 `Cs2jArray.ArrayHelper.XXX(...)`，无 using 依赖、无扩展接收者、
无泛型推断歧义（特化重载显式按 rank 选择）。

### 6.4 安全链路（复用 UnsignedTypeRewriter 的同款机制）

- 改写节点以 `ArrayRewriteAnnotations.Rewritten` 标注，`GetRewrittenSpans` 取
  最外层不重叠 Span，仅对被改写片段局部格式化（避免整文件重排放大 diff）；
- 输出前候选依次尝试：局部格式化 → 整文件格式化 → 未格式化文本，每个候选
  重新解析校验（无 Error 级诊断）才采纳；全部失败则保留原文件 + Error 诊断
  （"array rewrite produced invalid C# syntax; the original file was preserved."）；
- 保留原文件 BOM（`FileStartsWithBom` 同款逻辑），clean 文件字节级保真。

### 6.5 项目模式流水线接入

- CLI 新增子命令 `array-method`（Verb 形态同 `unsigned-type`）：
  `-s <sln/csproj/dir> -d <dest>` 项目模式、`-i <file> -o <file>` 单文件模式、
  `--force/--verbose`；
- 中间目录 `.cs2j-array-src`（常量 `IntermediateDirectoryName`）；
- `ProjectArrayMethodPreprocessor.PreprocessAsync`：镜像
  `ProjectUnsignedTypePreprocessor` 全流程——布局解析、项目图加载、不支持项目
  过滤、项目文件复制、全树枚举、逐文件改写（clean 复制 / dirty 重写）、
  统计与诊断合并、`dotnet restore`；
- 支撑文件释放：`ArrayHelperSupportFiles.ReleaseAsync` 把 `Cs2jArray.csproj` +
  `ArrayHelper.cs` 释放到 `intermediateRoot/Cs2jArray/`；给每个实际编译了被改写
  源文件的项目注入 `<ProjectReference>`（host 目录判定、相对路径动态计算、
  去重、根目录 host 的默认 glob 排除特判——全部 mirror unsigned 已实现机制）；
- **名称碰撞 fail-fast**：用户解决方案已含 `Cs2jArray.csproj` 即报错终止
  （保留项目名契约；与 `Cs2jUnsignedMath.csproj` 同款报错文案与"内容一致视为
  本体放行"的幂等重入逻辑）；
- **TFM 告警**：host TFM 低于 net10.0 时构建前 Warning（NU1201 提示），
  复用 `IsTargetFrameworkAtLeastNet10`；
- **convert-project 集成**：在现有 unsigned 阶段之后、tuple/generic 阶段之前
  串接 array-method 阶段（阶段间为逐文件全树复制，`Cs2jArray/` 目录随树存活、
  相对路径不变，与 `Cs2jUnsignedMath` 的存活论证相同）；对已预处理输入的重复
  运行幂等（改写统计为 0，释放以相同内容覆盖）。

### 6.6 单文件模式（-i/-o）

- 有 `-o` 时把 `_Cs2jArrayHelper.cs`（`_Cs2j*` 支撑文件前缀契约，主转换器按
  前缀跳过其类定义转换）写到输出文件同目录；无 `-o` 时仅 stdout、不落盘；
- 不释放 csproj：产物是改写后 C# 源 + helper 源码，后续编译由用户自行完成；
- 文件名与项目模式的 `ArrayHelper.cs` 不同名是有意的（同 `_Cs2jUnsignedMath.cs`
  vs `UnsignedMath.cs` 的先例）；内容为 public 真源，从嵌入资源读取。

## 7. Java 侧：compat `io.github.ningpp.compat.ArrayHelper`（扩展）

### 7.1 复用既有类的裁决

既有 `ArrayHelper.java`（2026-05-23 引入）定位 "Array utilities"，方法集
`toList/asListView/asIterable/copyArray/toXxxArray/sortParallel` 服务主转换器
的数组↔集合桥接生成点。本设计**在同文件追加**收口方法组：两组方法集不相交
（既有组 vs §7.2 新组），包名、类名、camelCase 约定均不变。取舍说明：新建
`ArrayUtil` 之类会造成"两个数组工具类"并存的认知负担与 import 分裂；扩展既有
类零迁移成本，且 C# 侧 `Cs2jArray.ArrayHelper` → Java 侧 `ArrayHelper` 的映射
与 `Cs2jUnsigned.UnsignedMath` → `UnsignedMath` 完全同形态（§8 精确匹配）。

### 7.2 方法形态：`Object` + 反射，委托既有实现

Java 侧统一以 `Object` 接数组（`java.lang.reflect.Array.get/set/getLength`
逐元素访问），**不做** C# 侧的泛型镜像——理由：Java 泛型不覆盖原始类型数组
（`int[]` 无法绑定 `T[]`），逐原始类型重载会方法爆炸；而调用点实参经擦除后
传给 `Object` 参数天然兼容。C# lib 的 `T[]` 特化与 Java 的 `Object` 形态只需
方法名 + 参数个数对齐（转换器生成调用不做参数类型校验）。

新方法组（camelCase，Pascal→camel 推导成立，见 §8.2）：

`getLength(Object)`、`getLongLength(Object)`、`getLength(Object,int)`、
`getLongLength(Object,int)`、`getLowerBound(Object,int)`、`getUpperBound(Object,int)`、
`clone(Object)`、`copyTo(Object,Object,long)`、`getValue(Object,int...)`、
`setValue(Object,Object,int...)`、`sort` 全重载、`binarySearch` 全重载、
`clear(Object,int,int)`、`resize(Object,int)`（返回新数组）、
`indexOf`/`lastIndexOf` 全重载、`reverse` 全重载、
`find/findAll/findIndex/findLast/findLastIndex/exists/trueForAll`（第二参
`java.util.function.Predicate`）、`convertAll(Object,Function)`、
`forEach(Object,Consumer)`、`copy` 全重载、`constrainedCopy(...)`。

### 7.3 委托关系表

| 方法组 | Java 实现 |
| --- | --- |
| `sort` 全重载 | 直接委托 `CSharpArray.sort(...)`（既有实现：对象走 `Arrays.sort`、原始装箱排序、comparer 反射适配、keys+items 索引重排） |
| `binarySearch` 全重载 | 委托 `CSharpArray.binarySearch(...)`（补码插入点语义已实现） |
| `clear` | 委托 `CSharpArray.clear(Object,int,int)`（按分量类型 fill 默认值） |
| `copy` / `copyTo` | 委托 `CSharpArray.copy/copyTo`（C# 参数校验 + `System.arraycopy` + 元素级兼容回退） |
| `indexOf` / `lastIndexOf` | 委托 `CSharpArray.indexOf/lastIndexOf(Object,Object,...)` |
| `reverse` | 委托 `CSharpArray.reverse(Object[,int,int])` |
| `resize` | 委托 `CSharpArray.resize(Object,int)`（按运行时分量类型 `Arrays.copyOf`） |
| `getLength(Object)`（多维总长） | 新实现：`java.lang.reflect.Array.getLength` 递归嵌套求和 |
| `getLength(Object,int)` / `getLongLength`* / `getLowerBound` / `getUpperBound` | 新实现：`Array.getLength` 按层下钻；getLowerBound 恒 0 但校验 dimension；getUpperBound 空/越界语义对齐 C# |
| `clone(Object)` | 新实现：`Array.newInstance(componentType, len)` + `System.arraycopy` |
| `getValue` / `setValue` | `java.lang.reflect.Array.get/set`（setValue 类型不匹配抛 `ClassCastException`，对齐 C# `InvalidCastException` 的 compat 映射形态） |
| `find` 族 / `forEach` | 新实现：反射遍历 + `java.util.function.Predicate/Consumer`（null 元素按 C# `Predicate` 语义：与 null 比较相等性） |
| `convertAll` | 反射遍历 + `java.util.function.Function`，结果数组按输出元素运行时类型分配 |
| `constrainedCopy` | 新实现：先探测全区间元素兼容性（元素级 `Array.set` 试探副本），再原子复制；任一不兼容抛 compat `ArrayTypeMismatchException` 语义的异常（复用 `ClassCastException`/compat 异常映射形态，见 §12） |

**Java 数组原生能力不经过本类**：`.length`、`T[][]`、`arr[i]`、数组 for-each
在预处理后残留 C# 中就是白名单形态，主转换器直译，不产生任何 `ArrayHelper`
调用——这是 §3 边界的直接推论，也是"转换 Java 极其容易"的落点。

### 7.4 与 `CSharpArray` 的边界

`CSharpArray` 保留双重角色：① `System.Array` **类型符号**的映射目标（[C] 通路：
包装器实例形态 + `CreateInstance/Empty/Fill` 静态）；② 本类 `ArrayHelper` 新方法
组的**委托目标**（实现复用，不重复造轮子）。调用点路由从此二分：
预处理改写点一律 `ArrayHelper.XXX`；未改写的 `System.Array` 类型通路仍
`CSharpArray.XXX`。两套 import 并存不冲突。

## 8. TypeMappings

### 8.1 类型级条目

```json
{
    "csharp": "Cs2jArray.ArrayHelper",
    "java": "ArrayHelper",
    "imports": [
        "io.github.ningpp.compat.ArrayHelper"
    ]
}
```

与 `Cs2jUnsigned.UnsignedMath` 条目同形态：`csharp` 键按 C# 侧全限定名精确匹配
（`TypeMappingService` 以 `ToDisplayString(FullyQualifiedFormat)` 构造键）；
`Cs2jArray.ArrayHelper` 为顶级类，直接命中。既有
`System.Array/Array → CSharpArray` 条目**保留不动**（服务 [C] 类型通路）。

### 8.2 方法名透传/Pascal→camel 论证（对照 unsigned spec §6.3 及其实测修正）

unsigned spec §6.3 曾论证"Pascal 透传"，实现计划（前置事实 2）以探针实测推翻：
**转换器对静态调用统一 Pascal→camel**（旧产物实测 `Cs2jUnsigned.UnsignedMath.addUInt32(...)`）。
本设计直接采信实测结论：`Cs2jArray.ArrayHelper.Clone(arr)` 转换为
`ArrayHelper.clone(arr)`——C# 侧 PascalCase 方法名经 camel 化后与 compat
camelCase 方法精确匹配；`ArrayHelper` 不在 `config/java/` JDK 索引中，无其它
推导干扰。若端到端测试发现某调用形态未被路由（如 `Find` 与 `FindAll` 的
前缀碰撞、重载参数个数歧义），按调用闭集补 `(type, method, paramCount)`
显式 methodMappings 条目（兜底策略与 unsigned 一致）。

## 9. 模块规划与主转换器

- **仅符号来源**：主转换器对 `Cs2jArray` 项目不规划 Maven 模块、不转换其源
  文件——与 `Cs2jUnsignedMath` 同款机制。符号可见性沿用同一前提：MSBuildWorkspace
  会沿 sln 外 ProjectReference 加载项目（unsigned plan 前置事实 1 已探针验证并
  由端到端测试锁定），`Cs2jArray` 经注入的引用边自然进图，host compilation 可
  解析 `Cs2jArray.ArrayHelper` 符号（含 `System.Predicate<T>` 等签名，供改写点
  lambda 的目标类型解析）。
- **过滤泛化**：`MultiModulePlanner.Build` 现有
  `!Cs2jUnsignedMathProject.IsCs2jUnsignedMathProject(p.ProjectFilePath)` 链式
  过滤（`MultiModulePlanner.cs:33-36`），新增
  `!Cs2jArrayProject.IsCs2jArrayProject(p.ProjectFilePath)` 同位追加；workspace
  路径（`Program.cs` 的 `workspaceProjects` 过滤）同位追加。两个判定类保持独立
  （文件名精确匹配各自的保留名），不抽公共基类——两行链式谓词的重复成本低于
  引入共享抽象的耦合；若第三个支撑库出现再抽 `Cs2jSupportProjects` 集合。
- 过滤后 host→`Cs2jArray` 的引用边在依赖收集处自然落空（`TryGetValue`/
  `FirstOrDefault` 找不到即跳过），生成 pom 不出现该依赖；`ProjectKind` 枚举
  无需扩展；指纹快照在过滤之后，helper 不参与缓存键（同 unsigned Task 7 论证）。
- 多维/协变场景的符号可见性：改写产物中 `GetValue` 特化重载的选择发生在
  **预处理阶段**（改写器按 receiver rank 生成具体重载调用），主转换器只见到
  无歧义的确定调用，不依赖重载决议——规避了泛型推断/协变接收者在转换期的
  符号解析风险。

## 10. 语义一致性

C# 侧 lib 是真 C# 实现（编译、运行、与改写前行为对拍）；Java 侧手写实现
委托 JDK/`CSharpArray`。两侧对齐由三层保证：

- **改写前后行为一致**（C# 侧）：`ArrayMethodSemanticsTests`（mirror
  `UnsignedTypeSemanticsTests`）把同一组夹具源码分别以"原始"与"改写后 +
  `Cs2jArray` 引用"编译运行，逐字节比对 stdout——覆盖每个 [B] 项；
- **Java 侧边界值对拍**（§11 JUnit）：多维（2D/3D、空外层）、空数组、
  协变数组写入异常、排序（自然序/comparer/keys+items/区间）、binarySearch
  补码、resize（null/0/负数/扩缩）、find 族（null 元素、无命中）、
  ConvertAll、ConstrainedCopy 原子性；
- **已知且接受的偏差**显式列于 §12（排序稳定性、空维度内层长度、
  `SetValue` 泛型强转的擦除时点），每条给出触发条件与影响面。

## 11. 测试策略

- **改写器单元**（`ArrayMethodRewriterUnitTests`）：决策表 §3 每个 [B] 项至少
  一条正向断言（改写后源码文本精确包含 `Cs2jArray.ArrayHelper.XXX(`）+
  一条负向断言：
  - 实例组负向：`string.Length`、`List<T>.Sort()`、自定义类 `Clone()`、
    `StringBuilder.CopyTo` 不改写；一维 `arr.Length` 不动；`arr[0]` 不动；
  - 静态组负向：用户自定义 `Array` 类的 `Array.Sort(...)`、`SomeNs.Array.Copy`
    不改写；清单外 `Array.Empty<T>()`/`Array.CreateInstance` 不动（[C]）；
  - Resize 负向：`x = Array.Resize(ref a, n)` 非语句上下文、ref 属性目标 →
    跳过 + Warning；
  - foreach：一维 foreach 不动，多维摊平后内层循环变量名与既有局部冲突时
    自动改名；
  - clean 文件字节级保真；改写产物重新解析无 Error。
- **语义测试**（`ArrayMethodSemanticsTests`）：§10 第一层。
- **项目级**（`ArrayMethodProjectTests`）：中间树存在 `Cs2jArray/` 项目；
  host csproj 含 ProjectReference；根目录无平文件；名称碰撞 fail-fast 夹具
  （`SampleNameCollision/Cs2jArray.csproj`）；TFM 告警不触发（net10.0 夹具）；
  下游阶段（`.cs2j-tuple-src` 等）中 `Cs2jArray` 存活。
- **compat JUnit**（`ArrayHelperTest`，JUnit Jupiter 5.11.4）：§10 第二层。
- **端到端**（`ArrayMethodConversionEndToEndTests`）：预处理 → workspace 加载
  （断言 `GetTypeByMetadataName("Cs2jArray.ArrayHelper")` 可解析）→ 过滤
  `Cs2jArray` → 逐项目转换 → 断言：
  - 产物 Java 无裸数组实例方法（不含 `.clone()` 接收者为数组的残留改写点
    形态、`CopyTo(` 数组接收者形态）；
  - 调用点带 `import io.github.ningpp.compat.ArrayHelper;` 且方法名 camelCase；
  - 多维 `.Length`/`GetLength` 调用点全部经 `ArrayHelper`；
  - 生成模块列表无 `cs2jarray`；`mvn compile` 产物可编译。
- **planner 过滤**（扩展 `MultiModulePlannerTests`）：`Cs2jArray` 进图后模块数
  与依赖边断言。
- **委托/lambda 前置验证**（实现计划第一批任务）：`Array.Find(arr, x => …)`
  改写点的 lambda 在主转换器中能否正确落到 `java.util.function.Predicate`
  目标类型（`System.Predicate\`1` 的既有 methodMappings：`Invoke → test`）；
  失败 fallback 为改写器把 lambda 显式包装为 `new System.Predicate<T>(...)`
  构造形态或按闭集补类型条目。

## 12. 已知限制与风险

- **多维降级的语义代价**（[A] 既有通路的固有限制，本设计显式继承）：
  空外层维度丢失内层长度（`new int[0,4].GetLength(1)` C# 为 4，降级后
  `getLength(arr, 1)` 返回 0）；矩形不变量（每行等长）降级后不再由 Java 类型
  系统保证（C# 侧类型系统在改写前仍保证初始化等长，运行时无 API 能构造
  不等长"矩形"，风险仅存在于与 `CSharpArray` 包装器互操作的场景）；
  `GetUpperBound` 对空维度返回 −1 的 C# 语义依赖降级数组的嵌套形状推断。
- **值/引用类型数组差异**：C# lib 的 `T[]` 特化在 Java 侧统一为 `Object` +
  反射——值类型元素经 `Array.get` 装箱（`getValue` 兜底重载本来就是 object
  语义，无额外损失；`find/exists` 等遍历装箱是新增成本，仅影响收口方法调用
  点，直译路径不受影响）；`SetValue` 的 `(T)value` 泛型强转经 Java 擦除后
  不在写入时点检查（延迟到读/use 时 `ClassCastException`）——C# 语义是立即
  `InvalidCastException`，时点差异记录为已知偏差（Java 侧 `setValue` 走
  `Array.set` 反射路径则保留写入时检查，仅 C# lib 单文件自转换路径有此偏差）。
- **协变异常语义**：`ArrayTypeMismatchException` 与 `ArrayStoreException` 的
  类型名差异不改写、不映射（拒绝行为与时机一致）；compat 尚无
  `ArrayTypeMismatchException` 类，`ConstrainedCopy` 的失败异常以
  `ClassCastException`/既有异常映射形态承载。
- **排序稳定性**：C# `Array.Sort` 不稳定（introsort），Java 对象排序稳定
  （TimSort）、原始类型不稳定（双轴快排）——相等元素的相对顺序不保证一致；
  与既有 `CSharpArray.sort` 行为相同，不作为回归。
- **ref 参数（`Array.Resize`）**：仅支持安全赋值目标（局部/参数/无条件副作用
  字段）；`ref` 属性、嵌套表达式、同一数组多次 resize 依赖语句顺序的场景
  保守跳过 + Warning。`ref` 结构体字段（`fixed` buffer 等）不在支持面。
- **与既有通路的重叠**：`System.Array` 类型接收者（`CSharpArray` 包装通路）、
  LINQ/枚举/集合、`Span`/`ArrayPool`/`Buffer` 的边界已在 §3.4/§4 划清；风险
  剩余在"改写器误伤"（自定义 `Array` 类、同名成员方法）——由 SemanticModel
  精判 + 单元负向用例守门。
- **委托参数转换风险**：`Find/ConvertAll/ForEach` 的 lambda 改写点依赖主转换
  器的委托→函数式接口通路（`System.Predicate\`1` 仅有 methodMappings
  `Invoke → test`，无类型级条目）；列为前置验证项（§11 末条），fallback 为
  补类型条目或闭集 methodMappings。
- **TFM 与名称碰撞**：与 unsigned 完全同款（NU1201 构建前告警；
  `Cs2jArray.csproj` 保留名 fail-fast）。
- **阶段复制与幂等**：`Cs2jArray/` 随逐文件全树复制存活（同
  `Cs2jUnsignedMath` 论证）；对早前释放的 `Cs2jArray` 目录原样复制防自递归
  改写；重复运行幂等。

## 13. 迁移

- 需要重新构建并安装 compat：
  `mvn install -f java/csharptojava-compat/pom.xml`
  （坐标 `io.github.ningpp:csharptojava-compat:1.0-SNAPSHOT`；生成模块 pom
  零改动——compat 已是每个生成模块的默认依赖）。
- `config/TypeMappings.json` 增加且不修改既有条目（§8.1）；既有
  `System.Array → CSharpArray` 及其 methodMappings 保留（[C] 通路继续使用）。
- 旧产物需重跑 cs2j 全流水线再生效；旧模块中经由 `CSharpArray.sort` 等路由的
  调用点与新 `ArrayHelper` 调用点并存（不同类名，不冲突；旧形态成为渐退路径，
  不自动清理）。
- convert-project 集成为新增阶段，默认开启（与 unsigned 阶段相同的内置策略）；
  手工修改过的下游 Maven 工程不兼容本变更（同 unsigned 迁移条款）。
- 取代范围：本设计不取代任何既有 spec 的机制条款；`2026-05-23-array-helper-tolist-design.md`
  的 `ArrayHelper` 定位由"数组→集合桥接"扩展为"数组→集合桥接 + System.Array
  语义收口"（§7.1），机制条款（toList 等）不变。
