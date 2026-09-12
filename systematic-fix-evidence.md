# 系统性修复计划 — 证据与分簇基线

> 生成方式：`mvn-build-r140.log`（UTF-16LE）+ `*/target/surefire-reports/TEST-*.xml`，
> 由 `tools/triage_xml.py` / `tools/triage_report.py` / `tools/triage_drill.py` 解析。
> 基线：`Tests run: 7902, Failures: 425, Errors: 430, Skipped: 18` → **855**。

## 0. 重要基线说明（测量纪律）

1. **控制台日志的失败清单被截断**（只列出 250 条），不可作为全量依据；`surefire-reports/TEST-*.xml` 才是权威。
2. **同一份代码的失败数会波动**：同一轮内先后两次全量解析分别得到 855 和 812 条失败用例
   （两次都来自完整的 `clean install`，源码未变），说明套件内部存在共享可变状态导致的顺序/并发相关抖动。
   → 任何改动都必须用「新增通过 / 新增失败」的**集合差**判定，禁止只看总数。
3. `mvn clean install` 会重写 surefire XML；**不要并发跑多个全量构建**，否则互相覆盖报告。

## 1. 按异常类型分簇（855 条）

| 条数 | 异常 | 说明 |
|---:|---|---|
| 424 | `java.lang.AssertionError` | 断言不符（值/异常/空值） |
| 165 | `dotnet.text.json.JsonException` | 反序列化失败（多数由类型擦除引起） |
| 144 | `java.lang.ClassCastException` | 转换器串味（读/写用了错误的元素转换器） |
| 22 | `java.lang.UnsupportedOperationException` | 缺校验/不支持类型 |
| 21 | `io.github.ningpp.compat.TargetInvocationException` | 反射调用包装丢失了原始异常类型 |
| 21 | `java.lang.ArrayIndexOutOfBoundsException` | 空数组读取（连带失败） |
| 16 | `java.lang.IllegalStateException` | 该校验抛错处未抛 |
| 14 | `java.lang.NullPointerException` | 空引用（多数是"本该抛 InvalidOperationException"） |
| 12 | `java.lang.IllegalArgumentException` | 反射成员（字段/属性）不匹配 |
| 2 | `java.lang.StackOverflowError` | 引用循环 |

## 2. 按根因分簇（前 40 桶，含最深 SDK 帧）

| 条数 | 桶 | 主要测试类 | 最深 SDK 帧 |
|---:|---|---|---|
| 68 | `Assert.throws` | CustomConverterTests:17, CollectionTests:14, PropertyVisibilityTests:10 | 测试自身断言（未抛异常） |
| 55 | `JsonConvert CSharpList` | ArrayTests:20, SpanTests:15, ObjectTests:11 | `JsonConverter1.readCore:130` |
| 38 | `Assert.equal` | Utf8JsonWriterTests:27, JsonDocumentTests:3 | `JsonTestHelper.assertContentsAgainstJsonNet:720` |
| 30 | `Assert.null` | CustomConverterTests:4, NullTests:4, DictionaryTests:3 | 各测试类 `verify()` |
| 30 | `JsonConvert String` | ReferenceHandlerTests:15, SpanTests:15 | `JsonConverter1.readCore:130` |
| 22 | `Assert.true` | Utf8JsonReaderTests:19 | `Utf8JsonReaderTests.testJsonReaderUtf8:270` |
| 21 | `TargetInvocationException` | ExtensionDataTests:16 | `io.github.ningpp.compat.MethodInfo.invoke:112` |
| 21 | `ArrayIndexOutOfBounds` | ObjectTests:16 | `ClassWithComplexObjects.validateArray:57` |
| 19 | `ClassCast JsonElement -> Integer` | CollectionTests:18 | 测试类断言点 |
| 18 | `Assert.contains` | OptionsTests:5, JsonPropertyTests:3 | `OptionsTests.customEncoderAllowLatin1Supplement:376` |
| 13 | `ClassCast CSharpList -> String` | ObjectTests:7, ArrayTests:5 | `StringConverter.write:33` |
| 13 | `ClassCast SimpleTestClass -> String` | ObjectTests:9, ArrayTests:4 | `StringConverter.write:33` |
| 11 | `Expect 1 -> 0` | PropertyVisibilityTests:3 等 | 各测试类 `verify()` |
| 11 | `IllegalStateException (no message)` | JsonPropertyTests:5 | `JsonElement.checkValidInstance:950` |
| 11 | `ClassCast JsonElement -> String` | PropertyNameTests:10 | `StringConverter.write:33` |
| 10 | `JsonConvert CSharpDictionary` | ReferenceHandlerTests:4 | `DictionaryDefaultConverter.onTryRead:162` |
| 9 | `Throws IllegalStateException -> JsonException` | PropertyVisibilityTests:7 | 测试断言异常类型 |
| 7 | `JsonConvert CSharpHashSet` | CollectionTests:6 | `IEnumerableDefaultConverter.onTryRead:76` |
| 7 | `JsonConvert Integer` | CustomConverterTests:4 | `JsonConverter1.readCore:130` |
| 7 | `AssertionError: Expected InvalidOperationException when trying to get string value for` | Utf8JsonReaderTests:7 | `testingGetStringInvalidUTF8:6503` |
| 6 | `Throws UnsupportedOperationException -> JsonException` | CollectionTests:6 | 测试断言异常类型 |
| 6 | `Throws UnsupportedOperationException -> NullPointerException` | CustomConverterTests:2 等 | 测试断言异常类型 |
| 6 | `JsonConvert SampleEnum` | ExtensionDataTests:2 等 | `EnumConverter.read:93` |
| 6 | `ClassCast StreamTests$FlushThresholdTestClass -> String` | StreamTests:6 | `StringConverter.write:33` |
| 6 | `Expect 1 -> -1` | Utf8JsonWriterTests:6 | `writeStringInvalidCharacter:382` |
| 5 | `DictKeyNotSupported` | ReferenceHandlerTests:2 等 | `JsonConverter1.readCore:142` |
| 5 | `ClassCast CSharpDictionary -> CSharpIDictionary` | DictionaryTests:5 | 测试类断言点 |
| 5 | `ClassCast Integer -> Byte` / `-> Short` | EnumTests:5 | `EnumConverter.read:155` |
| 4 | `JsonConvert CustomConverterTests$AttributedPoint` | CustomConverterTests:4 | 自定义转换器未生效 |
| 4 | `AbstractCollection CSharpObjStack` / `CSharpObjQueue` | CollectionTests 等 | `throwNotSupportedException_CannotPopulateCollection` |
| 3 | `NotSupportedType [[I` | 多处 | 多维数组不支持 |
| 3 | `IllegalArgumentException: public ... CSharpGenericIList ...` | 多处 | 反射成员不匹配 |
| 3 | `IllegalArgumentException: Can not set ... CSharpIDictionary field ...` | 多处 | 反射成员不匹配 |
| 2 | `StackOverflowError` | CyclicTests 等 | 引用循环未处理 |

## 3. 已确认的高价值突破点

### 3.0 测量修正：`Utf8JsonWriterTests` 不是单一原因
初版从**截断的控制台日志**得出"61 条全是 `Z` vs `+00:00`"的结论，**该结论错误**。
用 surefire XML 的完整 `Expected/Actual`（多行）复核后，`Utf8JsonWriterTests` 的 61 条实际分为：

| 条数 | 真实差异 | 位置 |
|---:|---|---|
| ~20 | `\u0080`-`\u009F` 被转义成 `\u0080`，.NET 期望**原样输出**这些 Latin-1 字符 | `escapeCharacters` 等 |
| ~12 | 写出的数字被**多加引号**：期望 `["1234567.0", ...]`，实际 `[1234567.0, ...]` | `writeNumberValueDecimal` 等 |
| ~10 | 整数值被写成浮点：期望 `1000`，实际 `1000.0`；期望 `1`，实际 `1.0` | `writeNumber_*` 族 |
| ~8 | 非法 UTF-16 代理项：期望替换为 `\ufffd`，实际保留原代理 | `utf16SurrogatePairReplacement_*` |
| ~6 | `escapeCharacters` 的 `IndexOf` 语义：期望 `1`，实际 `-1` | `writeStringInvalidCharacter` |
| 2 | `Z` vs `+00:00`（**只有 2 条**，不是 61 条） | `writeString_NullPropertyName_DateTimeOffset` |
| 其余 | 同族的其他格式噪声 | — |

→ **教训**：`Expected/Actual` 是多行块，用 `^Expected: (.*)$` 单行正则会截断，必须取到 `^\s+at ` 之前的全部文本。
本仓库已提供 `tools/triage_full.py` 做正确的多行提取。

### 3.1 嵌套泛型/类型擦除 = 约 121 条（最大单簇）
`List<List<short>>` 这类成员的 `runtimePropertyType` 退化成裸 `CSharpList.class`，
`_classes` 按裸类命中 `List<string>` 的 `JsonClassInfo`，于是内层数组用 `StringConverter` 读/写。
详见 `sdk-error-analysis.md` 的 "Iteration 76"。命中的桶：
- `JsonConvert CSharpList` 55（`ArrayTests` 20 / `SpanTests` 15 / `ObjectTests` 11）
- `JsonConvert String` 30（`ReferenceHandlerTests` 15 / `SpanTests` 15，路径**全部**是 `...MyInt16TwoDimensionList[...]`）
- `JsonConvert CSharpDictionary` 10、`JsonConvert CSharpHashSet` 7
- `ClassCast CSharpList/SimpleTestClass -> String` 26、`ClassCast Integer -> String` 34

### 3.2 读/写两侧转换器串味 = `StringConverter.write:33` 一族
`ClassCast CSharpList/SimpleTestClass/JsonElement/StreamTests$FlushThresholdTestClass -> String`，
全部落在 `StringConverter.write:33`，即写出时元素转换器选错（与 3.1 同源或同机制）。

### 3.3 异常语义系统性偏移
- 68 条该抛未抛；`Throws UnsupportedOperationException -> JsonException`(6)、
  `Throws IllegalStateException -> JsonException`(9)、
  `Throws UnsupportedOperationException -> NullPointerException`(6)、
  `Throws IllegalStateException -> NullPointerException`(2)、
  `Throws IllegalStateException -> TargetInvocationException`(2)：读路径先把
  `InvalidOperationException`/`NotSupportedException` 包成 `JsonException`，或反射包成
  `TargetInvocationException`，于是断言拿到的类型不是映射后的
  `IllegalStateException`/`UnsupportedOperationException`。

---

## 4. 实施进展（Round 1，目标：0 失败）

### 4.1 测量方法定案（三层，已工具化）

| 层次 | 工具 | 用途 |
|---|---|---|
| 权威计数 | `mvn -e` 聚合清单（`[ERROR]   Class.method:line » Kind`） | 每轮给出**精确** failures+errors 总数 |
| 逐测试集合差 | `tools/logdiff.py snapshot/diff` | KEEP/REVERT 判定：输出**测试级**新增通过/失败 + **站点级**差异 |
| 分簇 | `tools/triage_xml.py` / `triage_report.py` / `triage_full.py` | 失败画像与样例 |

**关键修正：本仓库的 surefire XML 不完整**——部分类只落 1 个 testcase
（`CustomConverterTests` 143→1、`CollectionTests` 342→5），所以 XML 只能做参考，
计数与判定一律以 `mvn -e` 的输出为准。

**判定规则（重要）**：verdict 必须用**测试级**集合差。站点级差异经常是"同一个失败测试的断言行号
位移"造成的**假回归**（例：`readGenericIReadOnlyListOfGenericIReadOnlyList` 在两次运行中
分别于 `:440` 与 `:438` 失败，测试始终是失败的）。

**基线（已两次独立复核）**：`Tests run: 7902, Failures: 425, Errors: 430, Skipped: 18` = **855**。

**已实测并回退的假设（勿重试）**：

| 假设 | 实测结果 | 结论 |
|---|---|---|
| `CSharpDateTimeOffset` 往返格式 `XXX`→`xxx`（`Z` → `+00:00`） | 测试级 1 修复 / **9 回归** | REVERT |
| 转换器缓存"读写同 key"（`converterCacheHas/Get` + 去掉 `tryAdd` 的 `map.remove`） | 855 → **934/935**（两次复现） | REVERT |
| 元素类信息按实参作用域查找（`beginElementLookup`/`classInfoCacheKey`） | 855 → **934** | REVERT |
| `IDictionaryConverter.createCollection` 接口分支改用 `newNonGenericDictionary` | 局部 2 绿 4 红；全量 843 → 842 | REVERT 调用点（保留 compat） |
| `JsonClassInfo.getElementClassInfo` 改用"活转换器"的元素类型（P4） | 测试级 **5 修复 / 1 真回归**，总数 842 → 842 | REVERT（见 §4.5） |

### 4.2 已 KEEP 的修复

**提交 `0665a07b`（P1+P2）— 全量 855 → 842**

1. **`TypeHelper.getType` 支持 C# 程序集限定名**（8 测试）
   `Class.forName("System.Collections.Stack, System.Collections.NonGeneric")` 永远失败，使
   `IEnumerableConverterFactoryHelpers.isNonGenericStackOrQueue` 成为**死代码**：Stack/Queue
   成员落到通用 `IEnumerableConverter`，其 `createCollection` 无法实例化该类型并抛
   `The collection type '...CSharpObjQueue' is abstract, an interface, or is read only`。
   修复：`ClassNotFoundException` 时按 `,` 截断类型名并映射到 compat 手写类型。
   局部实测：`CollectionTests` 159 → 132 失败。

2. **`getCompatibleGenericInterface` 改为传递性接口遍历**（4 测试，0 回归）
   `Class.getInterfaces()` 只返回**直接**接口，而移植的 .NET helper 依赖
   `Type.GetInterfaces()` 的**传递闭包**。`StringHashSetWrapper` 不直接实现任何接口，
   helper 返回 null → 落到通用转换器 → 同上抛错。
   修复：compat 新增 `TypeHelper.getAllInterfaces`，并**已写入转换器补丁通道**
   （`ProjectPasses.PatchTransitiveInterfaceWalk`），可从 C# 源再生。
   实测：`CollectionTests` 132 → 104；测试级 10 修复 / 1 假回归。

**提交 `77fbefb5`（P5 的 compat 部分）**
- `CSharpIDictionary.from` 增加 `size()/getCount()` 覆盖：`CSharpCollection` 的两个 default
  互相调用，任何都不覆盖的适配器会 `StackOverflowError`。
- 新增 `TypeHelper.newNonGenericDictionary`（`Dictionary` → `IDictionary` 桥）；
  调用点补丁已在 `ProjectPasses.PatchDictionaryCreateCollection` 实现但**未启用**。

### 4.3 下一步（按实测价值排序）

| 优先级 | 项 | 依据 |
|---|---|---|
| P1 | 写路径元素类信息取自转换器元素类型（4 个 converter 文件的 `DeclaredJsonPropertyInfo` 赋值点） | 子代理实测 5 个 `IllegalArgumentException` → 3，2 测试转绿。**注意**：Round 2 试过"通用版"（改 `JsonClassInfo.getElementClassInfo` 统一取活转换器元素类型）→ 测试级 5 修复 / 1 真回归、总数持平，已回退（§4.5）。需按写路径定点改，不要改读路径共用的那个方法 |
| P2 | `ClassCast Integer -> String`（34 条，`StringConverter.write:33`）：写侧元素转换器串味 | 与 Iteration 74/76 同源 |
| P3 | "该抛未抛" 70 条：逐条定性（产品缺陷 vs 测试期望）后补校验点 | 最大断言簇 |
| P4 | 引用保留 `$id/$ref/$values`（`ReferenceHandlerTests` ~70） | 含 `StackOverflowError` |
| P5 | `Utf8JsonWriterTests` 61 条：**根因在测试预言机** `JsonTextWriter`，不是 writer 输出 | 见 §4.4 |

### 4.6 Round 2 结果：数组目标 TypeReference（P3，已 KEEP）

**提交 `ff7e08dc` — 全量 842 → 822（net +20，8 测试转绿）**

C# `Deserialize<Dictionary<string,int>[]>(json)` 被发射成
`deserialize(json, opts, CSharpDictionary[].class)`：数组类字面量不携带元素实参，
Jackson 产出 `JsonElement` 元素，调用方强转即 `JsonElement cannot be cast to Integer`。

TypeReference 通道本就存在，但守卫要求 `INamedTypeSymbol { IsGenericType: true }`，
`IArrayTypeSymbol` 永远不匹配。修复：
1. 发射器守卫额外接受"元素为闭合泛型的数组"，并映射为 `TypeReference<X<...>[]>`；
2. compat `registerParameterizedType` 递归 `GenericArrayType`（注册元素层）；
3. compat `toRawClass` 把 `GenericArrayType` 映射为其擦除分量的数组类（原来返回 `Object.class`）。

**端到端验证了再生链路**：重跑转换器（618 文件，后台任务，>240s）后，
SDK 被重新生成，`ProjectPasses` 的 `IEnumerableConverterFactoryHelpers` 补丁**自动重新落上**
（line 94 `TypeHelper.getAllInterfaces`），compat jar 提供 `getAllInterfaces` /
`newNonGenericDictionary`，P5 的调用点改写按设计**未**被应用（它仍是禁用状态）。

**两个"回归"假象**：`DictionaryTests.arrayOfDictionary` 与
`dictionaryOfArrayOfDictionary` 在站点级看是新增失败，实际是**同一个测试的失败阶段后移**
（r152 在读阶段就失败；r154 已经读完，改为在写阶段失败——`MethodInfo.invoke` 的 receiver 类型
不匹配，即 P1 那个缺陷）。测试始终是失败的，不构成回归。

### 4.4 `Utf8JsonWriterTests` 61 条的真实根因（重要修正）

`Expected` 由测试内的 Json.NET 移植 `JsonTextWriter` 生成，`Actual` 是 `Utf8JsonWriter` 的输出。
实测差异方向是**预言机写错**，不是 writer 写错：

```
writeNumberValueDecimal:
  E: [\n  "1234567.0",\n ... ]     <- JsonTextWriter.writeValue(Decimal) 把数字写成了字符串
  A: [\n  1234567.0,\n ... ]       <- Utf8JsonWriter.writeNumberValue(Decimal) 正确
```
`escapeCharacters` 同理：`E` 在 0x80–0x9F 处是**裸字节**，`A` 是 `\u0080`，
与 `StringEscapeHandling.EscapeNonAscii` 的语义自相矛盾。

→ 这一类不能靠改 writer 修好；要修的是 `JsonTextWriter` / `getEscapedExpectedString` 的移植
代码。它们在 test 源里、由转换器生成，因此修复入口仍是转换器。

### 4.5 P4 实测细节（已回退，但值得记录）

把 `JsonClassInfo.getElementClassInfo` 的元素类型从**构造时快照**
（`converter.getElementType()` 存入 `elementType` 字段）改为**每次向
`options.getConverter(getType())` 重新取活转换器的元素类型**：

- 修复 5 个测试：`CollectionTests.readGenericICollectionOfICollection`、
  `readGenericIEnumerableOfIEnumerable`、`readGenericIListOfIList`、
  `ReferenceHandlerTests.arrayPreserveDuplicateObjects`、`dictionaryKeyLengthZero`
- **真回归 1 个**：`ReferenceHandlerTests.arrayWithMetadataWithinArray_UsingPreserve:792`
  → `Properties that start with '$' are not allowed on preserve mode`，即
  `List<List<Employee>>` 的元素被换成了错误类型，preserve 元数据不再被识别
- 总数 842 → 842（净 0）

机制说明：`options.getConverter(CSharpList.class)` 在运行时会被
`converterCacheKey` 解析成**当时环境**的实参键，因此"活转换器"未必是该 classInfo 自己的那个
——这正是 Iteration 74/76 的擦除问题在反向咬人。要拿到这 5 个修复，必须同时解决
"classInfo 与 converter 的实参一致性"，不能单点取巧。

### 4.7 Round 3 结果：写路径元素类信息（已 KEEP，net +11）

**提交 `c5bb7de0` — 全量 822 → 811**

生成的写路径用 `JsonClassInfo.getElementClassInfo().getPropertyInfoForClassInfo()` 安装
集合元素帧的 property info；该 class info 是预热期用**擦除后的元素 Class** 建的。
当多个成员共用同一裸元素类但实参不同时，它可能属于别的成员，元素帧因此拿到错误类型的
property，反射随后用**错误的 receiver** 调 getter：
`IllegalArgumentException: public ... getChild2IList() target=...`。

修复**刻意只落在 3 个写路径调用点**（`DictionaryDefaultConverter` 1 处、
`IEnumerableDefaultConverter` 2 处）：compat 新增
`TypeHelper.writeElementPropertyInfo(converter, options, fallback)`，用**纯反射**解析
转换器自己的元素类型（compat 不能引用生成的 SDK 类型），查不到就原样返回生成代码的表达式。

> **方法论要点**：Round 1 曾把同一个想法实施在**共用的** `JsonClassInfo.getElementClassInfo`
> 上 → 5 修复 / 1 真回归、总数持平（§4.5）。**写路径定点改**才是对的，因为 receiver 问题只发生在写路径。

**再生链路再次验证**：重跑转换器（618 文件）后，3 个调用点的补丁**全部自动重新落上**
（`DictionaryDefaultConverter:254`、`IEnumerableDefaultConverter:137/214`）。

**一个必须记住的教训**：*手工打过补丁的树*（r155）测出"15 修复 / 0 回归"，
而**完全再生后的树**（r157）是"811，但含 7 项 `DictionaryTests` 差异"。
两者总数相同（811），说明手工补丁树上仍残留更早实验的状态、**不能作为干净基线**。
**此后所有结论一律以完全再生后的树为准。**

**Round 4 的 A/B 复核（补上了缺失的对照）**：在**已完全再生**的树上，把 3 个写路径调用点
手工还原成生成代码的原样再跑全量，得到 **822**；重新落上补丁再跑，得到 **811**。
→ 写路径补丁的真实净收益 = **+11**，与 r157 的总数一致，结论成立。

同样重要的是：**写路径补丁并不是"元素类型取自转换器"**——实测
`JsonConverter1.getElementType()` 对列表转换器返回 **null**
（只有 `DictionaryDefaultConverter` 覆写它，返回 `s_valueType`），
所以 `writeElementPropertyInfo` 对多数列表其实是走**回退分支**（返回生成代码的表达式）。
真正的收益来源仍是"写路径少了一次基于错误 classInfo 的取用"。
这也解释了它为何与 Round 1 的"共用方法"版本表现不同：**改动面更小**，不是机制更强。
后续若要真正修掉元素类型问题，需要给 `IEnumerableDefaultConverter` 补一个能报告
`tElementClass` 的入口，而不是依赖 `getElementType()`。

### 4.8 `ClassCast Integer -> String`（34 条）的根因已钉到具体调用点

代表栈（`CollectionTests.writeArrayOfIList`、`writeClassWithObjectIEnumerableConstructibleTypes`）：

```
StringConverter.write(StringConverter.java:33)
  ListOfTConverter.onWriteResume(ListOfTConverter.java:62)   <- elementConverter.write(...)
  IEnumerableDefaultConverter.onTryWrite(IEnumerableDefaultConverter.java:216)
```

调用点是 `ListOfTConverter.onWriteResume:58` 的
`JsonConverter1<TElement> elementConverter = getElementConverter(state);`，
它取的是
`IEnumerableDefaultConverter.getElementConverter(ObjectHolder<WriteStack>)` →
`state.value.Current.DeclaredJsonPropertyInfo.getConverterBase()`，
即**元素类信息里的那个 PropertyInfo 的转换器**。

**为什么它会是 `StringConverter`**：`JsonClassInfo.getElementClassInfo()` 用
`getOptions().getOrAddClass(getElementType())` 建元素类信息，而
`getOrAddClass` 内部的 `ConcurrentHashMapHelper.getOrAdd` 会走
`TypeHelper.converterCacheKey(key)`；该键**优先读 `MEMBER_ARGUMENTS`**——
也就是"当前线程上最后一个被读取的成员"的实参。因此元素类信息常常是**按别的成员的实参**注册的。
`writeArrayOfIList` 里 `CSharpGenericIList<Object>[]` 的元素应当是 `Object`，
却命中了一个 `CSharpGenericIList<String>` 的转换器 → 对 `Integer` 元素调用
`StringConverter.write` → ClassCastException。

**修复方向（下一轮）**：进入元素类信息查找时，把 `MEMBER_ARGUMENTS`/`CONSUMED_ARGUMENTS`
临时切换成**元素自身的已声明实参**，查完恢复。compat 里加
`beginElementArgumentsScope(elementType, elementArgs)` / `endElementArgumentsScope(token)`
（save/restore 这三个 ThreadLocal 中该 raw 类的条目），生成代码在
`JsonClassInfo.getElementClassInfo()` 的两侧调用它。
预期能同时覆盖本簇与 `JsonElement -> Integer` 那一簇——两者都是同一个"实参来源错位"。

> 注意：Round 1 试过的"只改读侧缓存键"（`converterCacheHas/Get`）在**预热路径**上会让情况变差
> （855 → 934），已回退；本条不同，它改的是**实参来源**而不是缓存键的读写一致性，不要混为一谈。

### 4.9 Round 5 结果：元素参数作用域（已 KEEP，net +15，零回归）

**提交 `27bebf39` — 全量 811 → 796**

按 §4.8 的方向实施：`JsonClassInfo` 在构造时记录**属主自己的已声明实参**
（`captureDeclaredTypeArguments`），元素查找时
① `pushArgumentContext` 清空并恢复上下文、
② `beginElementArgumentsScope` 把**元素自身的实参**（由属主第一个实参派生，
`elementArgumentsOfOwner`）设为 `MEMBER_ARGUMENTS`，查完 `finally` 里还原。

**一次性解决了整个嵌套集合家族**（`readListOfList`、`readIListTOfIListT`、
`readHashSetTOfHashSetT`、`readImmutableArrayOfImmutableArray`、
`readIImmutableQueueTOfIImmutableQueueT` … 共 24 项转绿），
因为它们的失败机制完全相同：元素类信息被别的成员的实参注册。

**限定的必要性**：第一版**无条件**套作用域 → 24 修复但 2 回归
（`ImmutableCollectionsTests.serializeList/serializeStack` 在**全量顺序**下对 `String`
调用 `SimpleTestClass` 的 getter，单独跑却是过的）。把作用域**限定为"属主与元素同裸类"**
（即真正的 `List<List<T>>` 型嵌套）后，回归归零、收益保留 15 项。
`beginElementArgumentsScope` / `pushArgumentContext` 因此都接收 owner 与 element 两个类型，
不同裸类直接返回 null。

**再生链路第三次验证**：重跑转换器后 4 个补丁点全部从 C# 源自动落上，
重新全量得到**完全相同**的 796 / 零回归。

### 4.10 Round 6 结果：非泛型字典适配器的守卫写错（已 KEEP，net +2，零回归）

**提交 `21d8c849` — 全量 796 → 794，5 项转绿**

Round 1 引入的 `TypeHelper.newNonGenericDictionary` 用了
`typeToConvert.isAssignableFrom(CSharpDictionary.class)` 做守卫。
但 **`CSharpDictionary` 只实现泛型字典接口**（`implements CSharpGenericIDictionary<K,V>, ...`），
与 `CSharpIDictionary` 没有 Java 类型关系，所以该判断**恒为 false**，
方法**永远返回 null** → `IDictionaryConverter.createCollection` 把 null 放进 returnValue →
`add()` 里 `fromAny(null)` 返回 null → 每个非泛型 `IDictionary` 成员都炸：

```
NullPointerException: Cannot invoke "CSharpIDictionary.put(Object, Object)"
  because the return value of "...getReturnValue()" is null
```
共 **50 条**（`ArrayTests`、`ObjectTests`、`SpanTests`、`ReferenceHandlerTests` 等）。

真正该用的一致性判断是 **C# 侧的**——调用方已经通过 compat 的 `isAssignableFrom` 桥算好并作为
`assignableFromRuntime` 传进来了。改成守卫桥接口 `CSharpIDictionary.isAssignableFrom(typeToConvert)`，
并**重新启用** Round 1 因"守卫是坏的"而被判定为负收益、当时禁用的调用点补丁。

**方法论要点**：Round 1 测出该调用点补丁"2 绿 4 红"就禁用了它，但**没有查为什么**——
真实原因是它在给一个**坏掉的**适配器接线。先修被依赖的底层，再评估上层；否则会把
"底层缺陷导致的上层负收益"误记成"上层方案不行"。这正是本轮把 `PatchDictionaryCreateCollection`
重新打开的依据。

### 4.11 `JsonConvert CSharpList`（72 条）为什么没被 Round 5 修掉——路径根本没执行

Round 5 的元素参数作用域**只覆盖"属主与元素同裸类且元素类信息在查找时才创建"**的情形。
失败样例（`ArrayTests.readObjectArray`、`readClassWithObjectList`、
`readNull(...)`、`SpanTests` 23 项、`ReferenceHandlerTests` 14 项）路径全是
`...MyInt16TwoDimensionList[0]`，即 `SimpleTestClass.MyInt16TwoDimensionList : List<List<short>>`。

用 `probe/NestedListProbe.java` 打开 `-Dcs2j.trace.typeargs=true` 跑，得到**决定性证据**：

```
scope traces: 0            <- beginElementArgumentsScope / pushArgumentContext 一次都没被调用
classInfoCacheKey traces: 0   <- classInfoCacheKey 也没被调用（那个补丁从未提交）
converterCacheKey(CSharpList) = ... [member layer]   <- 只有转换器缓存键在跑
```

**原因**：外层列表的 `JsonClassInfo` 在**预热期**就被建好了，`_elementClassInfo` 那时已经填充，
所以反序列化时 `JsonClassInfo.getElementClassInfo()` 直接返回缓存值，**整个补丁块被短路**。
而预热期填充进去的正是**错误的那一格**（`CSharpList` 的按裸类/别的成员实参建的那个）——
这与 Round 14 / Iteration 76 记录的现象完全一致。

**因此**：Round 5 的作用域只对"元素类信息在查找时才建"的嵌套集合有效（这就解释了它为何能修
`readListOfList` 等 24 项），而"元素类信息在预热期已被错误填充"的这一族**必须**从
`JsonPropertyInfo.getRuntimeClassInfo()` 一侧解决——即让属性一开始就拿到**正确的**
`JsonClassInfo`，而不是在元素查找时打补丁。

**下一步（明确方向）**：修 `runtimePropertyType`。Round 14 已确认它在
`JsonClassInfo.getConverter` 里被退化成裸类：
```java
Class converterRuntimeType = converter.getRuntimeType();   // == CSharpList.class（tClass 被擦除）
if (TypeHelper.isSameType(type, converterRuntimeType)) runtimeType.value = type;   // CSharpList == CSharpList
```
再加上 `_classes` 按裸类缓存，属性必然取回别人的那一格。
Round 1 试过"只把 `_classes` 的键改成实参感知"→ 无效（进来的就是裸类）；
Round 3 试过"元素类信息取自活转换器"→ 5 修复 / 1 回归、总数持平，已回退。
**可行方向**：在**建 `JsonClassInfo` 时**就把该 class info 自己的实参固化下来
（而不是每次靠环境推断），并让 `getRuntimeClassInfo()` 用这个固化实参去查 `_classes`。
这需要 `JsonPropertyInfo` 记住它属于哪个闭合泛型，属于结构性改动，应单独立项、单独测量。

### 4.12 Round 7：把 72 条那一簇的"最后一厘米"测出来了

在 `JsonPropertyInfo.getRuntimeClassInfo()` 上装探针（跑 `probe/NestedListProbe.java`）：

```
[cs2j.pi] runtimePropertyType=class io.github.ningpp.compat.CSharpList
          key=io.github.ningpp.compat.CSharpList[class java.lang.String]
          info.elementType=class java.lang.String
```

**关键点**：`key` 在 `getRuntimeClassInfo()` 里**第一次计算时就已经是 `[String]`**，
说明此刻 `MEMBER_ARGUMENTS[CSharpList]` 已经是 `[String]` —— 也就是二维列表成员的实参**已经丢失**，
而不是"查询时被覆盖"。它本该是 `[[Integer]]`。

**于是本簇的完整因果链**（比 Iteration 76 更精确）：

1. `JsonClassInfo.getConverter` 把 `runtimeType` 退化成裸 `CSharpList.class`（实参被擦除）；
2. `getRuntimeClassInfo()` 拿裸类去查，`converterCacheKey` 返回
   `CSharpList[MEMBER_ARGUMENTS[CSharpList]]`，而此时该槽位是**别的成员**留下的 `[String]`；
3. `getOrAddClass` 于是命中 `CSharpList<String>` 那一格，属性的 `JsonClassInfo` 变成 `List<string>`；
4. 其 `_elementClassInfo` 在**预热期**就指向 `String`，于是 Round 5 的元素查找补丁
   **整块被短路**（§4.11 的 0 次 trace），内层 `[10,11]` 被 `StringConverter` 读。

**为什么"查一次键"治不了**：我实测把 `getOrAddClass` 改成"只算一次 key、`get` 与 `putIfAbsent` 共用"
并把 `_classes` 键类型放宽为 `Object` → `ArrayTests`(38) 与 `ObjectTests`(66) **计数完全不变**。
因为问题出在**那次计算的输入**（`MEMBER_ARGUMENTS` 已错），不是计算的次数。

**结论（下一轮的结构性任务）**：`MEMBER_ARGUMENTS` 是**按裸类单槽**的
（`Map<Class, Type[]>`，后读的成员覆盖先读的）。只要两个成员的成员类型是同一个裸类
（`List<string>` 与 `List<List<short>>`），先者的实参就会污染后者。正解是让**成员实参与成员绑定**，
而不是与裸类绑定：

- 方案 A：`PropertyInfo`/`FieldInfo` 在 `getPropertyType()` 时把自己的实参**记在实例上**
  （而不是推进全局槽位），`JsonPropertyInfo` 在 `initialize` 时取走并保存；
  `getRuntimeClassInfo()` 用保存的实参去查 `_classes`。
- 方案 B：给 `_classes` 的键从"闭合泛型键"改为"成员身份 + 裸类"，使每个成员各有独立 class info。

两者都要动 `JsonPropertyInfo` + compat 的实参发布方式，**属于结构性改动**，
必须先做"单类验证（`ArrayTests`+`ObjectTests`）→ 再全量"，不可直接铺开。

### 4.13 Round 8：把"成员实参绑定"方案做出来了，实测是 wash（已回退，含关键机理）

完整实现了 §4.12 的方案 A：

- compat：`PropertyInfo.getPropertyType()` / `FieldInfo.getFieldType()` 在反射到成员类型时
  调 `bindMemberArguments(this, 该成员的泛型式)`，把**成员自己的实参**记进
  `MEMBER_DECLARED_ARGUMENTS`（按成员键，不再是按裸类的单槽）；
- compat：新增 `LOOKUP_ARGUMENTS` 显式作用域，`converterCacheKey` **优先**读它；
- 生成代码：`JsonPropertyInfo.getRuntimeClassInfo()` 用
  `beginLookupArgumentsScope(getMemberInfo(), getRuntimePropertyType())` 包住查找。

**实测结果**：

| 阶段 | 结果 |
|---|---|
| 单类验证 | `ArrayTests` 38 → **35**（−3）；`ObjectTests` 66 → 66（**不变**） |
| 全量 | 794 → **792**（净 +2），但**同时 10 项修复 / 8 项回归** → 判为 wash |

**回退后的关键对照**：只把 `converterCacheKey` 里"优先读 `LOOKUP_ARGUMENTS`"那两行去掉
（保留 `bindMemberArguments`），全量立刻回到 **794、零回归**。
→ 说明那 10/8 的交换**完全由"显式实参作用域"这一步引入**，`bindMemberArguments` 本身无害。

**为什么方案 A 在这里会自我拆台**（机理，留给下一轮）：

`runtimePropertyType` 已经是**裸 `CSharpList.class`**，它**无法表达"我是第几层"**。
属性按自己的实参键成 `[[Short]]` 建出 `JsonClassInfo` 后，该 class info 的
**元素查找**（`getElementClassInfo()`）还在同一个显式作用域里，于是元素也用 `[[Short]]` 去查——
而 `[[Short]]` 正是**外层**的键，元素于是命中**外层自己**，`_elementClassInfo` 指向父级，
内层数组继续被 `StringConverter` 读（`ArrayTests` 剩下那 35 条仍是 `MyInt16TwoDimensionList[0]`）。

**正解必须在同一次查找里区分"外层实参"与"元素实参"**：
即 `getRuntimeClassInfo()` 用成员实参建外层，而**元素查找必须切换到
`elementArgumentsOfOwner(成员实参)`**（这正是 Round 5 已有、且已验证正确的推导），
两者不能共用同一个作用域。下一轮应把这两级显式分开，再单类验证后再全量。

### 4.14 Round 9：用 trace 把"实参在什么时候被覆盖"钉死了

给 `registerMemberType` 加 trace（`-Dcs2j.trace.typeargs=true`，跑 `NestedListProbe`），
得到**按时间顺序**的完整发布序列：

```
registerMemberType(CSharpList) <- [CSharpList<Integer>]              <- 可能是 IList<int> 之类
converterCacheKey(CSharpList)  = CSharpList[CSharpList<Integer>]     [member layer]
registerMemberType(CSharpList) <- [CSharpList<CSharpList<Integer>>]  <- 二维成员，正确发布
converterCacheKey(CSharpList)  = CSharpList[CSharpList<CSharpList<Integer>>] [member layer]
registerMemberType(CSharpList) <- [class java.lang.String]           <- 单层 List<string> 成员
converterCacheKey(CSharpList)  = CSharpList[class java.lang.String] [member layer]
... 之后全部是 [class java.lang.String] ...
```

**结论（本轮的核心事实）**：二维成员的实参**发布是对的**（第 2 行），
但 `MEMBER_ARGUMENTS` 是"每裸类一个槽"，**后反射到的 `List<string>` 成员把它覆盖成 `[String]`**。
到读取阶段 `getRuntimeClassInfo()` 才去查，此时槽位里已经是 `[String]`——
所以 Round 7 探针看到"第一次算 key 就是 `[String]`"完全解释得通：
**不是查询时被覆盖，而是查询发生得太晚，槽位早就被后来的成员改掉了。**

**为什么 Round 8 的"显式作用域"会自我拆台（补充机理）**：
`getOrAddClass(type)` 内部用 `new JsonClassInfo(type, this)` 建实例，而构造函数里
**元素查找也在同一个作用域内**（§4.13 已述）。若外层作用域持久生效，
元素查找就会拿外层实参（`[[Short]]`）去查，命中外层自己，`_elementClassInfo` 指向父级——
这正好解释了 Round 8 的 10 修复 / 8 回归。

**下一轮的正确做法（设计已定，尚未实现）**：

1. `registerMemberType(type, member)` 增加 member 参数（`PropertyInfo`/`FieldInfo` 传 `this`），
   把每个成员的实参**按成员**记进 `MEMBER_DECLARED_ARGUMENTS`；
2. 新增 `TypeHelper.classInfoKeyForMember(member, rawType)`，用**成员自己的实参**算出
   `ClosedGenericKey`；
3. `JsonPropertyInfo.getRuntimeClassInfo()` 改成：
   **先算一次 key（用成员实参）→ `_classes.get(key)` 命中就直接返回 → 未命中才回落
   `getOptions().getOrAddClass(runtimePropertyType)`**。
   即：**只把 key 算对，不去改 `getOrAddClass` 内部的作用域**，从而完全避开
   "外层/元素共用作用域"的自拆台问题；
4. 未命中时的建实例路径仍走原有逻辑（元素查找由 Round 5 的 same-raw-class 作用域负责）。

关键点：**命中路径必须只做一次 `get`**，不能落进 `getOrAddClass`，
否则又会在建实例时触发元素查找。实现时要把这条写进补丁与注释。

**本轮收口**：所有 trace 与实验代码已回退，`git checkout -- java/` 后重装 compat、
重跑全量复核 = **794（431+363）**，与 Round 6 收口一致，零未验证残留。

### 4.15 Round 10：命中路径探针落地（已 KEEP，net +4，零回归）

**提交 `2b6bab6c` — 全量 794 → 790**

按 §4.14 的设计实现，并**严格遵守"命中路径只读、不写"**：

1. compat：`registerMemberType` 增加 member 重载，把每个成员的实参**按成员**
   记进 `MEMBER_DECLARED_ARGUMENTS`（`PropertyInfo`/`FieldInfo` 传 `this`）；
2. compat：新增 `cachedClassInfoForMember(member, options, rawType)`——
   用成员自己的实参算出 `ClosedGenericKey`，**只做一次 `_classes` 反射读**，
   命中就返回，未命中返回 null；
3. 生成代码：`JsonPropertyInfo.getRuntimeClassInfo()` 先问这个探针，
   未命中才回落原来的 `getOptions().getOrAddClass(...)`；
4. 转换器补丁：`ProjectPasses.PatchMemberClassInfoLookup`（可再生产）。
   `PropertyInfo`/`FieldInfo` 属 compat 源码，其改动本身就是持久的，无需补丁通道。

**关键设计约束（已验证有效）**：探针**只读缓存、不进入 `getOrAddClass`**。
Round 8 的持久作用域之所以 10 修 8 回，就是因为它在
`new JsonClassInfo(...)` 构造期间仍然生效，使**元素查找**拿外层实参命中外层自身。
"只读命中"从结构上杜绝了这条路径。

**限定为"单类型参数成员"**：对多参数成员（`Dictionary<K,V>` 等）应用同一个键会回归 3 项
`NullTests` 可空字典用例（实测 790 → 791 但 3 回归）。限定后回归归零。

**再生链路第四次验证**：重跑转换器后 `JsonPropertyInfo:373` 自动落上，
重新全量得到**完全相同**的 790 / 零回归。

**为什么只拿到 4 项**：本探针只在"正确键的条目**已存在**"时生效。
剩余 `MyInt16TwoDimensionList` 族（`ArrayTests` 仍 34）说明**正确键的条目在预热期就没被建出来**——
即外层二维列表的 `JsonClassInfo` 从未以 `CSharpList[[Integer]]` 注册成功。
**下一轮方向**：查预热期为什么没有建出该条目（而不是继续在查询侧加探针）。

### 4.16 Round 11：条目其实**存在**，是**时序**问题（关键结论）

在探针里把 `_classes` 的所有 `CSharpList` 键打出来，按**读取顺序**看到真相：

```
[cs2j.hit] member=MyInt16TwoDimensionList key=CSharpList[CSharpList<Integer>] hit=false listKeys=
[cs2j.hit] member=MyStringList              key=CSharpList[String]            hit=false listKeys=[CSharpList[CSharpList<Integer>]]
[cs2j.hit] member=MyStringIEnumerableT      key=CSharpList[String]            hit=true  listKeys=[CSharpList[[Integer]]][CSharpList[String]]
```

**纠正 §4.15 的推断**：`CSharpList[[Integer]]` 这个条目**并非从未建立**——它是在
**第一个成员（`MyInt16TwoDimensionList`）自己那次读取过程中被惰性创建的**，
所以它自己的探针运行时还不存在（`listKeys` 为空），而排在它后面的成员就能看到了。

**因果链的最后一环**：`getRuntimeClassInfo()` 把结果缓存在 `_runtimeClassInfo`，
**只算一次**。二维列表成员是**第一个**被读到的，此刻正确条目尚未存在 →
探针 miss → 回落 `getOrAddClass(裸类)` → 建/取到 `CSharpList[String]` 那一格 →
**此后永不重试**，即使正确条目马上就被建出来了。

**试过并否证的修法（勿重试）**：
1. 在 `getRuntimeClassInfo()` 的 miss 分支包 `beginMemberScope` → 无效：
   miss 发生在**读取阶段**，此时转换器与 class info 早已定型。
2. 在预热期 `JsonClassInfo.cacheMember` 外再包一层 `beginMemberScope`（手工改生成码验证）→
   二维成员的探针**仍然 miss**。说明该条目不是由 `cacheMember` 这条路径建出来的，
   或建立时机仍晚于第一次读取。

**由此得出的下一轮方向（明确且可执行）**：
不要继续在"查询侧"加探针，而要**让正确条目在第一次读取之前就存在**。三条候选：
- **A**：在 `JsonClassInfo` 构造完成后，对"成员实参键"做一次**预热建条目**
  （遍历每个成员，按 `memberArguments` 显式建 class info，使后续读取必然命中）；
- **B**：`getRuntimeClassInfo()` 在**miss 且该成员只有一个类型参数**时，**不缓存**结果，
  每次重试探针（代价是重复查找，但能自愈掉这个时序差）；
- **C**：定位真正建立该条目的代码路径（用断点/trace 打 `_classes.put` 的调用栈），
  把成员实参在该路径上就位。

**本轮收口**：全部实验代码已回退（含手工改动过的 `JsonPropertyInfo`/`JsonClassInfo`），
重跑全量复核 = **790（431+359）**、零回归、`git status` 干净。
