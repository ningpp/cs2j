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

### 4.17 Round 12：候选 A/B 都试了（无效），并查出补丁通道的一个真实缺口

**候选 B（miss 不缓存、下次重试）——实测无效**：
- 只加"单类型参数成员 miss 时不写 `_runtimeClassInfo`、直接 return" → `ArrayTests` 34、`ObjectTests` 66，**无变化**；
- 再叠加"写入路径的 `LOOKUP_ARGUMENTS` 作用域"（让 `getOrAddClass` 按成员实参建条目）
  → `ArrayTests` 反而 **34 → 35**，`ObjectTests` 不变。

**原因分析**：重试的前提是"第二次读取时正确条目已存在"。但
`getRuntimeClassInfo()` 的结果一旦被 `PropertyInfo` 持有就不会再查，
且这一族成员的读取路径**只经过一次**该入口，所以"下次重试"实际上从不发生。
这解释了为什么 B 无效。**候选 A/B 均可标记为已否证，勿重试。**

**⚠ 本轮查出的真实缺口（重要，属于工程债）**：
`ProjectPasses.PatchMemberClassInfoLookup` 的正则
（`_runtimeClassInfo = getOptions().getOrAddClass(getRuntimePropertyType());`）
在**全新生成的 `JsonPropertyInfo.java`** 上**不会命中**。验证方法：
删除该文件 → 重跑转换器 → 生成结果**不含** `cachedClassInfoForMember` 补丁。
即 Round 10 的"再生验证"实际是在**已手工打过补丁**的文件上重跑转换器得到的，
掩盖了这个缺口。

**当前处置**：为把树保持在已验证的 790，`JsonPropertyInfo.getRuntimeClassInfo()` 的
Round-10 形式是**手工写回**的（与 `build-r184.log` 复核一致）。
**下一轮必须先修这个补丁锚点**（把正则改成能匹配纯净生成物，
例如匹配 `public JsonClassInfo getRuntimeClassInfo() {` 之后的整段，
或在 `PatchMemberClassInfoLookup` 里对未命中做显式报错而不是静默跳过），
否则该补丁会在任何一次真正的全量重新生成后**静默丢失**。

> 同理应审计其余补丁的锚点是否同样"只在手工改过的树上命中"——
> 这是本轮暴露出的**系统性**风险，不只是这一处。

### 4.18 Round 13：缺口的真正原因是**后缀遮蔽**（已修复，并已审计全部补丁）

**根因（比 Round 12 的判断更准确）**：不是正则写错，而是**分派顺序**——

```java
result.FileName.EndsWith("JsonPropertyInfo.java")   // 例如 .../JsonPropertyInfo.java
result.FileName.EndsWith("PropertyInfo.java")       // ← 上面那个串也以它结尾！
```

`"JsonPropertyInfo.java".endsWith("PropertyInfo.java") == true`，
所以 `PropertyInfo`/`FieldInfo` 那条分支**先命中并吞掉了** `JsonPropertyInfo`，
`PatchMemberClassInfoLookup` **从未在纯净生成物上运行过**。
这精确解释了 Round 12 观察到的现象：补丁只存在于**已经手工改过**的树上。

**修复与验证**：
1. 把**更具体**的后缀判断放到**前面**（并加注释说明顺序为何重要）；
2. **删除**生成的 `JsonPropertyInfo.java` → 重跑转换器 →
   现在**补丁出现在纯净生成物上**（第 373 行 `cachedClassInfoForMember`）；
3. **审计全部 14 处 `EndsWith` 分派**，用脚本枚举"后缀互为前后缀"的配对：
   只有这一对，且修复后顺序正确（`ok (tested first)`）；
4. `PatchMemberClassInfoLookup` 锚点失配时**改为大声报错**（`Console.Error`）
   而不是静默 `return`——这类"静默失效"正是能潜伏很多轮的缺陷。

**收口复核**：真正的全量重新生成后重跑全量 = **790（431+359）**、零回归。
其余补丁（`JsonClassInfo:246` 元素作用域、3 处写路径、`IEnumerableConverterFactoryHelpers`、
`IDictionaryConverter`）在同一轮里一并确认仍在位。

> **工程结论**：补丁通道本身是可靠的，但"用文件名后缀分派"天然有前后缀遮蔽风险。
> 本次已把该风险显式化（顺序 + 注释 + 审计脚本 + 失配报错），后续新增补丁必须沿用同一约定。

### 4.19 Round 14：复测 Round 12 的 B 变体（确认无效），并开挖 `Assert.throws`（70 条）

**复测 B 变体**：Round 12 测"成员作用域包住 miss"时，`PatchMemberClassInfoLookup` **并没有真正生效**
（§4.18 的后缀遮蔽），所以那次结论**证据不足**。本轮在补丁已可持久生成的前提下**重测**：
`ArrayTests` 34 → **35**、`ObjectTests` 66 → 66，仍然无效，错误依旧是
`Cannot get the value of a token type 'StartArray' as a string`。
→ **B 变体现在是**有充分证据地**被否证，可不再考虑。

### 4.20 `Assert.throws` 最大子模式：非公开构造函数被放行

70 条里占比最大的子模式（`CollectionTests` 16、`CustomConverterTests` 17、
`PropertyVisibilityTests` 10 等）：

```
Assert.throws() failure: No exception thrown
Expected: java.lang.UnsupportedOperationException
```

对应测试 `CollectionTests.read_Generic_NoPublicConstructor_Throws(Class, String)`：
.NET 对"非公开且非隐式默认构造函数"的集合包装类型会抛 `NotSupportedException`
（映射为 Java `UnsupportedOperationException`），而端口**把它们构造出来了**。

**出处在 compat**：`TypeHelper.getConstructorInfo` 的兜底分支
```java
for (Constructor<?> ctor : type.getDeclaredConstructors()) {
    if (matchesParameterTypes(...) && (matchesBindingFlags(...) || matchesConstructorBindingFlags(ctor, type, bindingFlags))) {
        ctor.setAccessible(true);
        return new ConstructorInfo(ctor);
    }
}
```
以及 `matchesConstructorBindingFlags` 的桥接语义：
"构造函数非 public 但**类本身也非 public** → 视为 public（C# 语义）"。

**测试类型实测**（`GenericIEnumerableWrapperPrivateConstructor`，**public 类 + 显式 private 构造函数**）：
```java
public class GenericIEnumerableWrapperPrivateConstructor<T> extends GenericIEnumerableWrapper<T> {
    private GenericIEnumerableWrapperPrivateConstructor() {
    }
}
```
按 `matchesConstructorBindingFlags` 的字面逻辑，public 类应当返回 false（不放行），
但实际未抛异常，说明**放行来自该函数之外**（`matchesBindingFlags` 或 `createConstructor` 的其它分支），
需要再定位一层。

**修复方向（有风险，需先单类验证）**：收窄这个为"私有嵌套类隐式默认构造函数"设计的桥接，
使**显式声明的非公开构造函数**不被当作 public。但该桥接是为真实转换场景加的，
改动可能引起**大范围回归**，必须先跑 `CollectionTests` + `CustomConverterTests` +
`PropertyVisibilityTests` 三个类做单类验证，再全量。

**本轮收口**：实验代码全部回退，复核 = **790（431+359）**、零回归、`git status` 干净。

### 4.21 Round 15：**否证** §4.20 的定位——compat 的构造函数桥接是无辜的

用 `probe/CtorProbe.java` 直接问 compat（`GenericIEnumerableWrapperPrivateConstructor`）：

```
isPublic(type)  = true
ctors           = [private ...GenericIEnumerableWrapperPrivateConstructor()]
getConstructorInfo(Public|Instance) = null      ← 正确返回 null！
isAbstract      = false
```

**结论**：`TypeHelper.getConstructorInfo` 对"public 类 + 显式 private 构造函数"**正确地返回 null**，
`matchesConstructorBindingFlags` 也**没有**错误放行。
§4.20 把矛头指向 compat 构造函数桥接是**错的**，现予更正——
因此**不要**去收窄那个桥接（那会造成无谓的大范围回归风险）。

**真正的缺口（已定位到层级）**：目标类型是 `IEnumerable<T>` 接口时，
`IEnumerableConverterFactory` 走的**不是**"实例化该类型"的分支，
而是"造一个 `CSharpList` 之类的具体集合来承载"，
于是 `createCollection` 里那句 `getCreateObject() == null → throw` **永远不会触发**。
.NET 在这里会查真实类型（`GenericIEnumerableWrapperPrivateConstructor`）的**公开无参构造函数**，
没有就抛 `NotSupportedException`。

**修复方向（下一轮，落点在转换器/工厂而非 compat）**：
在 `IEnumerableConverterFactory.createConverter` 的接口分支里，
对"具体目标类型"补一次公开无参构造函数校验，缺失即抛
`ThrowHelper.throwNotSupportedException_DeserializationNoConstructor`（映射为
`UnsupportedOperationException`），并让消息包含类型名（测试断言
`Assert.contains(type.toString(), ex.getMessage())`）。
补丁通道已可用（`ProjectPasses`），注意**后缀分派顺序**约定（§4.18）。

**风险**：该分支覆盖所有接口型可枚举目标，必须先在
`CollectionTests` + `CustomConverterTests` + `PropertyVisibilityTests` 三个类上验证，再全量。

### 4.22 Round 16：按 §4.21 实现并**单类验证通过**，但**全量更差**，已回退

**实现**：compat 新增 `hasPublicParameterlessConstructor(type)`
（接口/数组/枚举/抽象类型放行；其余用 `getConstructorInfo(type, Public|Instance)` 判定），
并在 `IEnumerableConverterFactory.createConverter` 构造转换器之前加守卫，
缺失即 `ThrowHelper.throwNotSupportedException_DeserializeNoConstructor(...)`。

**单类验证（按 §4.21 要求）**：

| 测试类 | 基线（全量 r188） | 加守卫后（单类跑） | 变化 |
|---|---:|---:|---:|
| `CollectionTests` | 106 | **96** | **−10（变好）** |
| `CustomConverterTests` | 68 | 68 | 0 |
| `PropertyVisibilityTests` | 69 | 69 | 0 |

看起来是干净的胜利，于是按纪律跑全量。

**全量结果（跑两次，完全一致，非抖动）**：

```
r189: Tests run: 7902, Failures: 447, Errors: 359  = 806
r190: Tests run: 7902, Failures: 447, Errors: 359  = 806   ← 同一配置重跑，逐位相同
基线 r188                                            = 790
```

逐类对比定位到**唯一**变化：`CollectionTests` 在全量里 **106 → 122（+16）**，
而它**单独跑**却是 106 → 96（−10）。

**结论：这是一个"单类变好、全量变差"的顺序依赖型改动，按纪律回退（REVERT）。**
判据以全量为准，已回退并复核 = **790（431+359）**、`git status` 干净。

**为什么会这样（机理，留给下一轮）**：`CollectionTests` 在**全量顺序**下比**单独跑**多出
**26 条失败**（122 vs 96）——说明该测试类的失败数本身**依赖此前其它类留下的共享状态**。
新守卫在这些"被前置状态污染"的用例上抛出异常，反而把原本能过的路径打断了。
这与 Round 5 遇到的是**同一现象**（当时 `ImmutableCollectionsTests.serializeList/serializeStack`
只在全量顺序下失败）。

**下一轮的方向（优先级已提升）**：先解决**测试间共享状态**这一层，再重做本守卫。
具体建议：
1. 找出 `CollectionTests` 在全量顺序下多出的 26 条失败分别是什么（对比单类与全量的失败集合），
   确定污染源是哪个测试类/哪个 ThreadLocal 注册表（`TYPE_ARGUMENTS` / `CONSUMED_ARGUMENTS` /
   `MEMBER_ARGUMENTS` / `CALLSITE_ARGUMENTS` / `CONCRETE_SUBTYPES` 都是 ThreadLocal）；
2. 若污染源是 compat 的 ThreadLocal 注册表未在用例边界清理，考虑在
   `JsonSerializerCompat` 的入口/出口做**成对清理**（它已经有 `beginTypeRegistration`/`endTypeRegistration`，
   可审计是否有路径漏配对）；
3. 共享状态收敛后，本守卫（代码已在 §4.22 记录、可低成本重建）应能同时改善单类与全量。

### 4.23 Round 17：共享状态污染**找到了**，并且它直接来自我们自己的写路径补丁

用 `tools/orderdiff.py` 对比 `CollectionTests` 在**全量顺序**与**单独跑**下的失败**站点集合**：

```
CollectionTests: full-suite 58 sites, single-class 30 sites

ONLY in full-suite ordering (35):        ← 单独跑时全部通过
  writeListOfList:1063, writeListOfArray:1072, writeArrayOfList:1080,
  writeGenericIListOfGenericIList:1121, writeISetTOfISetT:1255,
  writeQueueTOfQueueT:1382, writeHashSetTOfHashSetT:1411, ...
ONLY when run alone (7):                 ← 极少数
```

**清一色是 `write*`（序列化方向），且全部是嵌套集合形状**（`List<List<T>>`、`ISet<ISet<T>>`、
`Queue<Queue<T>>`、`List<KeyValuePair<..>>` 等）。单类 30 站点 vs 全量 58 站点，
**多出的 28 个站点全是写路径**。

**机理（已定位到具体行）**：全量顺序下 `writeListOfList` 的栈是

```
ClassCastException: CSharpList cannot be cast to String
  at StringConverter.write(StringConverter.java:33)
  at ListOfTConverter.onWriteResume(ListOfTConverter.java:62)     ← elementConverter.write(...)
  at IEnumerableDefaultConverter.onTryWrite(IEnumerableDefaultConverter.java:216)
```

即**内层列表用了 `StringConverter`**。`getElementConverter(state)` 取的是
`state.value.Current.DeclaredJsonPropertyInfo.getConverterBase()`，而这个
`DeclaredJsonPropertyInfo` 是**写在共享的 `JsonClassInfo` 上**的：

```java
// IEnumerableDefaultConverter.java:214（Round 3 的写路径补丁改过这一行）
state.value.Current.DeclaredJsonPropertyInfo =
    (JsonPropertyInfo)(TypeHelper.writeElementPropertyInfo(this, options,
        state.value.Current.JsonClassInfo.getElementClassInfo().getPropertyInfoForClassInfo()));
```

`JsonClassInfo` 是**跨用例长期存活**的对象（缓存在 `_classes` 里），
于是"某次写操作安装的元素属性"会**残留**给之后的用例；
后续用例读到的是**上一个用例/类型**的元素转换器 → `StringConverter` → ClassCastException。
**单独跑时没有前置用例，所以不触发**——这精确解释了"单类过、全量挂"。

**这也可能与 Round 16 的回归同源**：守卫让更多用例走到/避开这些被污染的分支，
`CollectionTests` 全量从 106 涨到 122（+16），而多出的正是这一族写用例。

**修复方向（下一轮）**：
1. **优先**：让写路径**不再改写共享的 `JsonClassInfo`**——
   `IEnumerableDefaultConverter` / `DictionaryDefaultConverter` 的
   `state.Current.DeclaredJsonPropertyInfo = ...` 写入的是 `WriteStackFrame`（本应是 per-write 状态），
   但赋的**值**取自共享 class info；需要把"元素属性"改为**随帧保存**（例如存入 `WriteStackFrame`
   自己的字段、或由 converter 自己持有），而不是每次回写共享对象；
2. 或在**每次 `serialize` 入口**成对清理（与 `JsonSerializerCompat` 的
   `beginTypeRegistration`/`endTypeRegistration` 同构），把被写脏的 class info 复原；
3. 修好后**重做 §4.22 的守卫**（预期单类与全量同时改善）。

**本轮收口**：未改任何代码，复核 = **790（431+359）**、`git status` 干净。

### 4.24 Round 18：**否证 §4.23 的结论**——写路径补丁不是污染源（它其实在**减轻**污染）

在转换器里加了一个实验开关 `ProjectPasses.WriteElementPropertyInfoEnabled`，
把它设为 `false` 重新生成（确认生成物里 `writeElementPropertyInfo` 站点 **3 → 0**），再跑全量：

| 配置 | 全量 failures+errors | `CollectionTests` 全量站点 | 仅在全量顺序出现的站点 |
|---|---:|---:|---:|
| 开（Round 3 补丁生效） | **790** | 58 | **35** |
| 关（补丁不生成） | **800** | 67 | **44** |

**结论**：关掉写路径补丁后，**顺序依赖反而更严重**（35 → 44 个"仅全量失败"的站点），
全量总数也更差（790 → 800）。
→ **§4.23 把污染源归给 Round 3 的写路径补丁是错的，现予更正。**
该补丁实际上**在减轻**这个问题（它让元素属性至少按成员推导，而不是完全靠共享 class info），
**不能删**。开关已重新置 `true`，重生成后复核 = **790（431+359）**、写路径站点恢复 3 处。

**这对下一轮的意义（重要）**：污染源在**别处**，且**不是**写路径的元素属性安装逻辑。
结合 §4.23 已确认的事实——`WriteStackFrame` 本身是**每次 write 独立**的
（`WriteStack.Current = new WriteStackFrame()`，`push()` 时克隆）——
污染更可能来自**跨用例存活且被写入的 compat ThreadLocal 注册表**：
`TYPE_ARGUMENTS` / `CONSUMED_ARGUMENTS` / `MEMBER_ARGUMENTS` / `CALLSITE_ARGUMENTS` /
`WRAPPER_LAYER_TAKEN` / `CONCRETE_SUBTYPES`（全部是 ThreadLocal，且
`registerMemberType` 会**持续覆盖** `MEMBER_ARGUMENTS`）。

**下一轮的具体动作**：在 `CollectionTests` 全量顺序下，
把上述注册表在"某用例开始/结束"时的内容打出来，找出**未被清理**的那个；
重点审计 `JsonSerializerCompat` 的 `beginTypeRegistration`/`endTypeRegistration`
是否在所有路径上成对（含异常路径），以及 `registerMemberType` 的成员槽位是否在用例间被跨类型复用。

### 4.25 Round 19：**找到了**——在 `serialize` 入口清空 per-thread 注册表（+67，本计划最大单项收益）

**提交 `e8a29f38` + `60a56e0a` — 全量 790 → 723（net +67，57 项转绿，跑两次逐位一致）**

**关键事实**：失败的 `writeListOfList` 用的是
`JsonSerializer.serialize(CSharpList.class, input, null)`——**根本不走 compat 的
`JsonSerializerCompat`**，所以污染源不是它的 `begin/endTypeRegistration` 配对，
而是 **compat 的 ThreadLocal 注册表本身跨操作存活**：
`TYPE_ARGUMENTS` / `CONSUMED_ARGUMENTS` / `MEMBER_ARGUMENTS` / `CALLSITE_ARGUMENTS` /
`WRAPPER_LAYER_TAKEN` / `CONCRETE_SUBTYPES`。
**上一次（或上几次）操作反射到的成员/调用点，仍然会在下一次操作的查找里作答。**

**修复**：compat 新增 `TypeHelper.clearRegistrations()`（清空上述六个 ThreadLocal），
并在生成代码 `JsonSerializer.serialize(Class, TValue, Options)` 的入口调用它——
每个操作从干净状态开始。
`ProjectPasses.PatchSerializeRegistryReset` 已把它做成**可再生产**的补丁
（删除生成的 `JsonSerializer.java` 后重跑转换器，补丁自动落回第 1367 行；再生后全量仍为 723）。

**这就是 Round 18 线索的正确落点**：Round 18 已证明"写路径补丁不是污染源（反而在减轻）"，
本轮证明污染源是**注册表本身**。

**遗留的 5 项回归（已记录，未隐藏）**：
`ArrayTests.readClassWithGenericISetT`、`DictionaryTests.nestedDictionariesRoundtrip`、
`ReferenceHandlerTests.extensionDataDictionaryHandlesPreserveReferences`、
`objectDictionaryLoop`、`objectPreserveDuplicateDictionaries`。
字典那几项现在的错误是
`The type 'class java.lang.Object' is not a supported Dictionary key type`——
即清空成员上下文后，某个字典转换器是**按擦除后的类型参数边界**建出来的。

**同时否定了一个显而易见的缓解**：把 `MEMBER_ARGUMENTS` 保留不清 → 全量 **776**（比 723 差），
所以"缩小清理范围"不是正解。

**这也回溯解释了 Round 8**：它那次"持久作用域"实质上就是在清 `MEMBER_ARGUMENTS`，
所以必然回归——当时并不知道清空**起始时机**才是关键。

**下一轮方向**：修那 5 项——字典类的转换器需要在**入口清空之后**、
按**当前操作的成员实参**重建（与 §4.15 的成员键思路合流），
而不是复用按类型参数边界建的实例。

### 4.26 Round 20：试了"反方向"（在 deserialize 入口也清空）——**无效，已回退**

假设：那 5 项回归是因为它们的上下文在**反序列化**入口被清掉了，
所以"在 deserialize 入口也补一次 clearRegistrations"应当能补回。

**实测（在共享私有入口 `deserialize(String, Class, Options, Class)` 加清空）**：

| 测试类 | 基线（r202，723 状态） | 加 deserialize 侧清空 |
|---|---:|---:|
| `DictionaryTests` | 41 | **43（更差）** |
| `ArrayTests` | 35 | 35 |
| `ReferenceHandlerTests` | 77 | 77 |

→ **无效且使字典类更差，已回退**；复核 = **723（430+293）**、`git status` 干净。

**因此可以确定**：5 项回归**不是**"清空时机覆盖不全"，
而是**清空之后、那些字典转换器确实被按擦除边界重建了**。
即它们依赖**进入本次操作之前就已发布的成员/具体类型上下文**——
这正是 §4.25 记录的"按当前操作成员实参重建"要解决的问题。

**下一轮的具体动作（收窄后）**：
1. 对 `DictionaryTests.nestedDictionariesRoundtrip` 打开 trace，打印
   `getGenericArguments(CSharpDictionary)` 在**转换器创建时刻**返回什么：
   是 `[String, Integer]` 还是 `[Object, Object]`；
2. 如果是后者，定位它的 keyType/elementType 是从哪里取的——
   若是 `MEMBER_ARGUMENTS`（已被清空），则应改为从**当前操作已注册的类型**
   （`TYPE_ARGUMENTS`/`CALLSITE_ARGUMENTS`）或**具体类型的声明签名**
   （compat 已有 `registerConcreteType` / `findDeclaredArgumentsFor`）取得；
3. 修好后应能同时消除 5 项回归，并可能再带走一批
   `JsonConvert CSharpDictionary` / `DictKeyNotSupported` 的失败。

### 4.27 Round 21：把 5 项回归的**根因**测出来了

用 `probe/DictArgProbe.java` 复现测试的调用形状（**裸 Class token** + 全新 options）：

```
=== target CSharpGenericIDictionary
  deserialize FAIL UnsupportedOperationException:
      The type 'class java.lang.Object' is not a supported Dictionary key type.
      ... located on type 'class io.github.ningpp.compat.CSharpDictionary'
  after  getGenericArguments(CSharpGenericIDictionary) = [Object, Object]
  serialize -> "{\u0022Key\u0022:1}"          ← 写方向反而是好的

=== target CSharpDictionary
  deserialize FAIL （同上）
  after  getGenericArguments(CSharpDictionary) = [Object, Object]

=== target java.util.concurrent.ConcurrentHashMap
  deserialize OK -> java.util.concurrent.ConcurrentHashMap
```

**结论（根因，非推测）**：入口清空注册表之后，
`getGenericArguments(CSharpDictionary)` 返回 **`[Object, Object]`**，
于是 `DictionaryDefaultConverter` 的 `KeyType = Object`、`IsStringKey = false`，
读字典键时走到"不支持的键类型"分支并抛异常。

**为什么清空前是对的**：测试传的是**裸 Class token**
（`CSharpGenericIDictionary.class` / `CSharpDictionary.class`，
见 `nestedDictionaryTypeData()` 的 `genericDictTypes`），
它**本身不携带任何类型参数**；端口此前是**借用**上一次操作遗留的
`MEMBER_ARGUMENTS`/`CONCRETE_SUBTYPES` 才凑出 `[String, Integer]`。
清空把这个"借用"去掉了，于是暴露出**真正的缺口**：
**裸 token 的实参必须由具体类型自身的声明签名推导**，
而不是靠跨操作残留。

**这正是本计划一直在追的同一个缺陷的另一面**：
`compat` 已经有 `registerConcreteType` / `findDeclaredArgumentsFor`
（"具体类型的声明签名"通路），但**没有在裸 token 路径上被触发**。

**下一轮的具体动作**：
1. 在**反序列化**入口（`deserialize(String, Class, Options)` 或
   `deserialize_0tp`）对目标类型调用 `TypeHelper.registerConcreteType(returnType)`
   —— 与 `JsonSerializerCompat.deserializeInternal` 里已有的那一步对齐
   （注意 §4.26 已证明"再清一次"无用，本步是**补发布**，不是再清空）；
2. 对 `CSharpGenericIDictionary` / `CSharpDictionary` / `CSharpHashtable` 等
   无声明签名的类型，让 `getGenericArguments` 回落到
   **"字符串键"语义**（`DictionaryDefaultConverter` 已有 `IsStringKey` 分支，
   非泛型字典在 C# 里键就是 `object`，但读入时应按字符串键处理）；
3. 单类验证 `DictionaryTests` + `ArrayTests` + `ReferenceHandlerTests`，
   再全量；预期同时消除这 5 项回归，并带走 `DictKeyNotSupported` 一簇。

**本轮收口**：未改任何代码，复核 = **723（430+293）**、`git status` 干净。

### 4.28 Round 22：找到**准确的**区分点——"拒绝"必须**延迟到真正读键时**

读测试源码后，把 §4.27 的修法**收紧为可安全实施的形式**：

**关键发现 1（测试为什么传裸 token）**：`DictionaryTests.makeClosedCollectionType`：

```java
private static Class makeClosedCollectionType(Class openCollectionType, Class elementType) {
    if (TypeHelper.isSameType(openCollectionType, CSharpDictionary.class)) {
        return CSharpDictionary.class;          // ← 注意：字典直接返回裸类，不 makeGenericType
    }
    return TypeHelper.makeGenericType(openCollectionType, elementType);
}
```

即**测试自己**就把字典目标退化成了裸 `CSharpDictionary.class`（因为没有类型参数可传）。
所以"裸 token"是这个测试**有意**的形状，端口必须能处理它。

**关键发现 2（"该抛"与"不该抛"的精确边界）**——`DictionaryUnsupportedKeyTestsBase`：

| 测试 | 输入 | 期望 |
|---|---|---|
| `throwUnsupported_Deserialize` | `deserialize(TypeReference<CSharpDictionary<TKey,TValue>>, "{\"foo\":1}")` | **抛** `UnsupportedOperationException` |
| `doesNotThrowIfEmpty_Deserialize` | 同上，但 `"{}"` | **不抛** |

同一个类型引用、同样的 `TKey=object`，**唯一差别是 JSON 里有没有键**。
→ **C# 的语义是"惰性"的：键类型不受支持，只在真正遇到键时才报错**；
空对象合法通过。

**关键发现 3（端口当前的错误形态）**：端口把"键类型不受支持"检查放在了
**读取字典元素的早期**（`JsonConverter1.readCore:142` 里就能看到它被抛），
所以裸 `CSharpDictionary.class` + 非空对象 `{"Key":1}`
（`nestedDictionariesRoundtrip` 的形状）会在**还没读到键**时就抛。

**因此修法应当是**（本轮只定方案，未实施）：
把 `DictionaryDefaultConverter` 的键转换器解析**从"进入读循环前"移到"读到第一个键时"**，
即首次调用 `readDictionaryKey` 时才解析键转换器；
这样：空对象不抛（符合 `doesNotThrowIfEmpty_Deserialize`）、
`object` 键**读到**时照抛（符合 `throwUnsupported_Deserialize`）、
而 `CSharpDictionary<string,int>` 的裸 token 因为实际键是字符串、会走
`IsStringKey` 分支而**不再误抛**（修掉 §4.25 的 5 项回归）。

**同时纠正 §4.27 里"回落到字符串键语义"的提法**：那会让
`throwUnsupported_Deserialize` **不再抛**（回归），所以**不能用**；
正解是**延迟校验**，不是放宽校验。

**规模**：当前 `not a supported Dictionary key type` 共 **7 行**（5 测试），
`DictionaryKeyPolicyTests` 2、`DictionaryTests` 2、其余各 1。

### 4.29 Round 23：实施方案时又**纠正了一次**——抛出点不在读循环里

准备实施 §4.28 的"延迟校验"时，先核对了两个位置，结果**推翻了 §4.28 的定位**：

**事实 1**：`DictionaryDefaultConverter.readDictionaryKey` **本来就是惰性的**：

```java
// DictionaryDefaultConverter.java:91-99
if (IsStringKey) {
    unescapedPropertyNameAsString = reader.value.getString();
    key = (TKey)((Object)(unescapedPropertyNameAsString));
} else {
    if (_keyConverter == null) _keyConverter = getKeyConverter(KeyType, _cs2j_cap_options);  // ← 已经在这里才解析
    ...
}
```
即"读到键时才解析键转换器"**早就成立**，`_keyConverter` 也是那一刻才赋值。
所以 §4.28 说的"把它从进入循环前移到读到第一个键时"**没有可改的东西**。

**事实 2**：`JsonConverter1.readCore` 里那两行**不是抛出点**，而是**包装点**：

```java
// JsonConverter1.java:138-143
} catch (UnsupportedOperationException ex) {
    if (ex.getMessage().contains(" Path: ")) { throw ex; }
    ThrowHelper.throwNotSupportedException(state.value.clone(), reader.value.clone(), ex);  // ← 在这里补 Path 后重抛
    return _cs2jDefault_T();
}
```
栈顶显示 `readCore:142`，只是因为**重抛**发生在那儿，真实抛出点在更早的调用链上。

**由此得到的新定位**：抛出发生在**类信息构造期**，而不是读循环里。
最可能的路径是构造 `CSharpDictionary` 的 `JsonClassInfo` 时，
其 `createPropertyInfoForClassInfo` 触发了 `DictionaryDefaultConverter` 的
**键类型校验**（`IsStringKey=false` 且键转换器解析失败）。

**下一轮的具体动作**（比 §4.28 更准）：
1. 用 `probe/DictArgProbe.java` 的变体，在**构造 `JsonSerializerOptions` 之后、
   `deserialize` 之前**就对目标类型调用 `options.getOrAddClass(t)`，
   确认异常是否**在构造期**就抛出（若是，方案即确认）；
2. 若是，找出构造期触发键校验的具体语句（给
   `DictionaryDefaultConverter` 的构造/`createJsonPropertyInfo` 打 trace）；
3. 通过在构造期**不解析键转换器**、把它彻底留到 `readDictionaryKey` 里，
   即可同时满足：空对象不抛、`object` 键读到才抛、裸 `CSharpDictionary<string,int>`
   因实际键是字符串而不再误抛。

**本轮收口**：未改任何代码，复核 = **723（430+293）**、`git status` 干净。
（本轮的价值在于**两次阻止了错误的实施方案**：§4.28 的"移动代码"无处可移，
而真正要动的是构造期的键校验。）

### 4.30 Round 24：**根因终于钉死在两行代码上**（`ObjectConverter` 的读写不对称）

顺藤摸到真正的抛出点：

```java
// ObjectConverter.java
public Object readWithQuotes(ObjectHolder<Utf8JsonReader> reader) {
    ThrowHelper.throwNotSupportedException_DictionaryKeyTypeNotSupported(getTypeToConvert());  // ← 无条件抛！
    return null;
}
public void writeWithQuotes(...) {
    JsonConverter runtimeConverter = getRuntimeConverter(value.getClass(), options);   // ← 写侧按运行时类型解析
    runtimeConverter.writeWithQuotesAsObject(writer, value, options, state);
}
private JsonConverter getRuntimeConverter(Class runtimeType, JsonSerializerOptions options) {
    JsonConverter runtimeConverter = options.getDictionaryKeyConverter(runtimeType);
    if (runtimeConverter == this) {
        ThrowHelper.throwNotSupportedException_DictionaryKeyTypeNotSupported(runtimeType);   // ← 只在**真的**解析不出来时才抛
    }
    return runtimeConverter;
}
```

**读写两侧的核心不对称**：
- **写**：`getRuntimeConverter(值的实际类型)` —— 先看运行时的真实类型，
  **只有在确实没有可用转换器时**才抛。
- **读**：`readWithQuotes` **直接抛**，**完全不看** JSON token / 也不尝试解析。

于是"字典的键类型是 `Object`"这**一个条件**就足以在读侧立刻炸，
无论后面到底有没有键、键实际是什么。这正是
`empty object 不应抛` / `读到键才应抛` 两条测试期望同时被违反的原因。

**这同时解释了两簇看似无关的失败**：
- §4.25 的 5 项回归（`CSharpDictionary` 裸 token，值为 `Object`）；
- `JsonConvert CSharpDictionary` 一簇里
  `DictionaryDefaultConverter.onTryRead:162 → readDictionaryKey:97 → ObjectConverter.readWithQuotes:48`
  的那些（键类型 `Object`）。
当前全量里 `is not a supported Dictionary key type` 出现 **21 次**（约 7–10 个测试）。

**要实施的修法（方向明确，尚未实施）**：给读侧补上与写侧对称的**解析后才抛**：
`readWithQuotes` 应先尝试取得可用的键转换器（JSON 属性名是字符串，
落到字符串语义），**只有在真的解析不出**时才
`throwNotSupportedException_DictionaryKeyTypeNotSupported`。
这样三条语义同时成立：
1. `{}` 不抛（从不调用该转换器）；
2. `{"foo":1}` + 真正不支持的键类型**读到键时照抛**（供
   `throwUnsupported_Deserialize` 使用）；
3. 裸 `CSharpDictionary<string,int>`（实际键是字符串）**不再误抛**，修掉 5 项回归。

**⚠ 实施前必须确认**：`readWithQuotes` 是 `JsonConverter` 的通用入口，
不只服务于字典键；改动会影响所有 `writeWithQuotes/readWithQuotes` 的使用者，
因此**必须先单类验证** `DictionaryTests` + `DictionaryKeyPolicyTests` +
`ReferenceHandlerTests`，再全量。

### 4.31 Round 25：按 §4.30 实施**并单类验证**——**净效果为零，已回退**

**实施**：把 `ObjectConverter.readWithQuotes` 从"无条件抛"改成"返回
`reader.value.getString()`"（JSON 属性名本就是字符串，即"先解析、解析不出才抛"的读侧形态）。

**单类验证（按要求先做，未直接全量）**：

| 测试类 | 基线（r203） | 改动后 | 变化 |
|---|---|---|---|
| `DictionaryTests` | 11F+13E = 24 | 14F+10E = 24 | **总数不变** |
| `DictionaryKeyPolicyTests` | 4F+2E = 6 | 6F+0E = 6 | **总数不变** |
| `ReferenceHandlerTests` | 31F+31E = 62 | 32F+30E = 62 | **总数不变** |

**结论**：三个类的**失败总数一字不差**，只是 **Error 变成了 Failure**——
即"该抛的异常不再抛了"，但**测试依然不通过**，只是失败方式从"抛异常"变成"断言不符"。
→ **净效果为零，按纪律回退**；复核 = **723（430+293）**、`git status` 干净。

**这说明了什么（对下一轮很重要）**：
`ObjectConverter.readWithQuotes` 的"无条件抛"**不是**这 5 项回归的**唯一**原因。
把这一处放宽之后，失败**原封不动地转移到断言阶段**，
说明**至少还有一个独立的缺口**在同一批用例上生效
（最可能是：键被读成字符串之后，**值的转换/写回**仍然用了错误的元素类型——
与 §4.15/§4.25 记录的元素实参问题同源）。

**下一轮方向**：不要再改 `readWithQuotes`。改为在**放宽读侧的同时**
把**元素（值）转换器**也一并按当前操作的实参解析，
即把 §4.15 的"成员实参键 + 命中路径"思路**同时**用在键与值上；
或者先精确列出那 5 个用例**放宽后**的具体断言差异（现在它们会给出可读的
Expected/Actual），**从断言差异反推**缺的是哪一侧的转换器。

### 4.32 Round 26：重测 Round 16 的构造函数守卫——**仍为负**，但**顺序依赖已被 §4.25 彻底消除**

**动机**：Round 16 的守卫（集合目标缺少公开无参构造函数就抛）当时是
"单类 −10、全量 +16"，被判为顺序依赖所致。§4.25 修掉顺序依赖后，
**这个守卫理应重新评估**。

**重测结果**：

| 指标 | 数值 |
|---|---|
| 全量（加守卫） | **739（446+293）**，比 723 **差 16** |
| 逐类定位 | **只有 `CollectionTests` 变化：72 → 88（+16）** |
| 单类（加守卫） | 89（67F+22E） |

**结论**：守卫**仍然是负收益，已回退**；复核 = **723（430+293）**、`git status` 干净。
但**否证了 Round 16 的原因判断**：Round 16 归因于顺序依赖，而本轮证明
**顺序依赖已经不在了**，守卫依然有害——所以它的问题是**实质性的过度拒绝**，
不是隔离性问题。

**最有价值的副产物——§4.25 的修复效果已量化确认**：

```
CollectionTests: full-suite 22 sites, single-class 22 sites
ONLY in full-suite ordering (0):        ← Round 17 时这里是 35
```

即 `CollectionTests` 在**全量顺序**与**单独跑**下的失败**完全一致**
（全量 51F+22E=73、单类 51F+22E=73；站点集合一模一样）。
**§4.25 的"入口清空注册表"彻底消除了这一类顺序依赖**——这是本轮拿到的硬证据。

**下一轮方向（守卫的过度拒绝）**：既然不是隔离问题，就要查
`hasPublicParameterlessConstructor` 为**哪些类型**返回了 false 而 .NET 实际允许。
具体动作：在守卫里对"判为不支持"的类型打印类名，
跑 `CollectionTests` 单类，收集被拒类型清单，
再逐个对照 .NET 语义（重点怀疑：抽象/接口之外还有
**只有非公开构造函数但 C# 侧是隐式默认**的类型，以及
**数组/原始包装**等边界）。

### 4.33 Round 27：守卫**功能是对的**，损害来自"**校验位置错了**"

按 §4.32 的方向加了"拒绝即打印"的诊断，跑 `CollectionTests` 单类，收集被拒类型清单：

```
GenericStackWrapperPrivateConstructor      GenericIEnumerableWrapperPrivateConstructor
StringToGenericDictionaryWrapperPrivateConstructor   GenericICollectionWrapperPrivateConstructor
GenericListWrapperPrivateConstructor       GenericConcurrentQueuePrivateConstructor
GenericQueueWrapperPrivateConstructor      GenericConcurrentStackPrivateConstructor
WrapperForIListPrivateConstructor          WrapperForIDictionaryPrivateConstructor
WrapperForIEnumerablePrivateConstructor    WrapperForICollectionPrivateConstructor
```

**关键事实 1（守卫是**对的**）**：这 12 个正是
`read_Generic_NoPublicConstructor_Throws` 数据源里的类型，
而加上守卫后 **该测试确实转为通过**（`mvn-build-r206.log` 里已搜不到它的失败）。
即 `hasPublicParameterlessConstructor` 的判定**完全正确**，不是过度拒绝。
→ §4.32 的"过度拒绝"猜测**也被否证**。

**关键事实 2（损害来自"位置"）**：`CollectionTests` 同期 **72 → 88（+16）**。
把这 12 个类型的用法查清后得到解释：
它们**只在** `read_Generic_NoPublicConstructor_Throws_data1` 里作为**数据**出现
（`CollectionTests.java:841-858`），但**别的测试类里它们可能作为"成员类型"出现**——
而 `IEnumerableConverterFactory.createConverter` 是**按类型**调用的，
**分不清"这是本次反序列化的目标类型"还是"这只是某个成员的类型"**。
一旦某处把这类类型当成员解析，守卫就会**在本来正常的地方抛异常** → 新增失败。

**结论与下一轮方向**：构造函数校验必须放在**反序列化的根入口**
（`JsonSerializer.deserialize` 的目标类型），而**不是**转换器工厂里。
工厂按类型工作、没有"是否根目标"的上下文；
放到根入口即可同时做到：目标类型缺少公开无参构造函数时照抛（满足
`read_Generic_NoPublicConstructor_Throws`），而它作为**成员类型**时不受影响
（消除那 16 项新增失败）。

**本轮收口**：诊断代码已全部回退（`git checkout -- java/` + 手工移除生成侧 reporter），
复核 = **723（430+293）**、`git status` 干净。
（工具新增：`tools/entrydiff.py`，用于按"测试名+异常种类"比较两次日志的**条目计数**；
本轮发现**聚合清单本身可能被截断**，无法仅靠它点名，故下一轮需要逐用例捕获。）

### 4.34 Round 28：**"放到根入口"也无效**——构造函数守卫一族到此**封案**

按 §4.33 的结论把校验从 `IEnumerableConverterFactory` **移到反序列化根入口**
（`JsonSerializer.deserialize_0tp`，即 `deserialize(json, returnType, options, Object.class)` 之前），
新增 `TypeHelper.canBeDeserializationTarget`（接口/数组/枚举/抽象放行，其余查公开无参构造函数）。

**单类实测**：

| 测试类 | 基线 | 工厂内校验 | **根入口校验** |
|---|---:|---:|---:|
| `CollectionTests` | 73 | 89 | **89** |
| `DictionaryTests` | 24 | 24 | 24 |

**结果与工厂内完全一样（89）**，即**"位置"并不是原因**，§4.33 的推论**同样被否证**。
已回退；复核 = **723（430+293）**、`git status` 干净。

**为什么位置无关（本轮想清楚的关键点）**：
`CollectionTests` 里那些"成员类型"**本身就是通过根入口反序列化的**——
例如 `readSimpleTestClassWithSimpleTestClass` 之类用例，
其**根目标就是持有这些装饰类型的类型**，而这些装饰类型又会在**成员解析**时被走到。
无论校验挂在工厂还是根入口，**被拒集合不变**，所以损害不变。

**构造函数守卫一族：封案（已 4 次否证，勿再尝试）**：
| 轮次 | 变体 | 结果 |
|---|---|---|
| §4.22 Round 16 | 工厂内校验 | 单类 −10、全量 **+16** |
| §4.22 Round 16 归因 | "顺序依赖所致" | §4.26 **否证**（顺序依赖已被 §4.25 消除，守卫照样 +16） |
| §4.26 Round 26 归因 | "过度拒绝" | §4.33 **否证**（被拒的正是那 12 个目标类型，判定正确） |
| §4.33 Round 27 归因 | "位置错误" | **本轮（§4.34）否证**（移到根入口，损失不变） |

**新的、也是最后的解释（供后续判断，不再单独开轮验证）**：
`CollectionTests` 里那 12 个装饰类型**同时**出现在
`read_Generic_NoPublicConstructor_Throws`（**要求抛**）与
其它用例（**要求不抛**）中——即**同一类型在不同用例里有相反的期望**。
这意味着**任何"按类型"的构造函数校验都不可能同时满足两者**，
除非校验能区分"本次调用是否以该类型为根目标且**该用例期望拒绝**"，
而这需要**用例级意图**，不是运行时可推导的信息。

**因此建议**：构造函数守卫**不作为修复方向**。这 12 个
`read_Generic_NoPublicConstructor_Throws` 用例若要变绿，
需要另找**能区分两种期望的判据**（例如 .NET 实际区分的是
"类型是**接口实现包装** vs **具体集合**"，
或 C# 侧的 `[JsonConstructor]`/构造函数可见性元数据在本移植中丢失了），
属于**新课题**，应从"这两类装饰类型在 C# 源码上的差异"入手，
而不是在运行时猜。

### 4.35 Round 29：字典回归的最后一层——`registerConcreteType` 在官方路径上**从未被调用**

**推断**：`findDeclaredArgumentsFor` 是**唯一**能给出"具体类型的声明签名"的通路，
而它只读 `CONCRETE_SUBTYPES`、该表只由 `registerConcreteType` 填充——
**而 `registerConcreteType` 只在 compat 的
`JsonSerializerCompat.deserializeInternal` 里被调用**，
**官方路径 `JsonSerializer.deserialize` 从不调用它**。
所以 §4.25 清空注册表后，官方路径再也拿不到声明签名 → `[Object,Object]` → 抛"不支持的键类型"。

**实施**：在共享私有入口
`JsonSerializer.deserialize(String, Class, Options, Class)`（clearRegistrations 所在处）
补一行 `TypeHelper.registerConcreteType(returnType)`。

**实测（单类）**：

| 测试类 | 基线 | 加 registerConcreteType |
|---|---:|---:|
| `DictionaryTests` | 24 | **24（无变化）** |
| `ArrayTests` | 34 | 34 |
| `ReferenceHandlerTests` | 62 | 62 |

**无效，已回退**；复核 = **723（430+293）**、`git status` 干净。

**为什么无效（用 `probe/DictArgProbe.java` 直接验证）**：
即使注册了 `CSharpDictionary.class`，`getGenericArguments(CSharpDictionary)` **仍是 `[Object,Object]`**。
原因是 `findDeclaredArgumentsFor` 需要**具体类型在 Java 端真的声明了带实参的接口**
（`class X implements IDictionary<String,Integer>`），
而 `CSharpDictionary` 声明的是
`implements CSharpGenericIDictionary<K, V>, CSharpReadOnlyDict<K, V>`
（**用自己的类型变量**，不是具体实参），
所以 `declaredArgumentsFor` 走不出 `[Object,Object]`。

**这解释了整个字典簇的最终形态**：
裸 token `CSharpDictionary.class` **在 Java 端不携带任何具体实参**，
而端口过去是**借用上一次操作残留的注册**才凑出 `[String,Integer]`。
§4.25 把借用去掉后暴露了这个事实——
**除非测试改传 TypeReference，或端口为裸 token 引入"字符串键"缺省语义**（后者被 §4.28 证否），
否则这一簇**无法在运行时正确推导**。

**建议（与 §4.34 一致的结论）**：这 5 项（及 `DictKeyNotSupported` 小簇）
属于**"测试传了信息不足的 token"**这一类，**不应继续在运行时猜**。
可选的正确做法只有两条：
(a) 在**生成侧**把这类裸 token 调用改成带实参的 `TypeReference`（需要调用点能拿到实参）；
(b) 接受这 5 项为**已知限制**并记录，把预算投到别的簇。

### 4.36 Round 30：重定优先级，并收窄最大残余簇（`JsonConvert CSharpList` 65）

**当前最大单簇**（723 中）：`JsonConvert CSharpList` **65 条**，
分布 `SpanTests` 23、`ArrayTests` 19、`ObjectTests` 11、`ReferenceHandlerTests` 9。
抽查每一条的路径形状**都是** `$.MyInt16TwoDimensionList[0]`——即
`List<List<short>>` 这一族（与 §4.11/§4.14 同一族）。

**本轮用探针拿到的两个新事实**：

1. `MyInt16TwoDimensionList` **确实**在读取阶段被走到：
   ```
   [cs2j.pi] enter MyInt16TwoDimensionList cached=false
   ```
   所以它不是"已被缓存的错误 `_runtimeClassInfo`"（那正是 §4.11 的旧解释）。
2. 但 `cachedClassInfoForMember` **对它一次都没打印**，
   而同族的 `MyStringList` / `MyStringIEnumerableT` / `MyStringIListT` … **全部打印 HIT**
   （键为 `CSharpList[class java.lang.String]`）。

**即：Round 10 引入的成员键探针，恰好在它本要修的那个成员上不生效**——
它是唯一"元素本身还是闭合泛型"的成员。

**下一轮的具体动作（很窄，几行）**：
在 `cachedClassInfoForMember` 的**三个出口**各打一行
（`args == null`、`args.length != 1`、以及"查不到"的非 HIT 分支），
跑 `ArrayTests` 单类即可确定它走的是哪一条；
再据此修正（若是 `args.length != 1`，说明该成员的"单实参"判定需要放宽到
"元素为闭合泛型"的情形）。**先单类验证 `ArrayTests`，再全量。**

**本轮收口**：诊断代码全部移除，复核 = **723（430+293）**、`git status` 干净；
并已用 `javap` 确认 compat jar 与已提交源码一致
（`clearRegistrations` / `cachedClassInfoForMember` / `registerConcreteType` 在位）。

### 4.37 Round 31：**精确定位**——键算对了，但条目**此刻还不存在**

在 `cachedClassInfoForMember` 的两个出口加探针后，得到决定性输出：

```
[cs2j.probe] MyInt16TwoDimensionList LOOKUP key=CSharpList[CSharpList<Integer>] found=false mapSize=37
[cs2j.probe] MyStringToStringKeyValuePair EARLY-ARGS args=[String, String]      ← 多实参，走早退
[cs2j.probe] MyInt16Array EARLY-ARGS args=null                                   ← 非泛型，走早退
```

**三点结论**：

1. **键完全正确**：`CSharpList[CSharpList<Integer>]` —— 正是该成员应有的闭合泛型键；
2. **没有走早退**：`args` 既非 null、长度也是 1，Round 30 怀疑的
   `args.length != 1` **不成立**；
3. **真正原因**：`found=false` —— **该键的条目在这一次查找时还不存在**（`_classes` 里此刻有 37 项）。
   随后 `getRuntimeClassInfo()` 回落到 `getOrAddClass(裸类)`，
   而后者按 `MEMBER_ARGUMENTS`（此刻是 `[String]`）建出**错误的一格**；
   由于 `_runtimeClassInfo` **只算一次**，此后**永不重试**。

即 **§4.11/§4.14 的"时序"结论再次成立**，只是这次被**精确到"键对、条目缺失"**。

**修法（明确、可单轮实施）**：在**查不到时也要按正确的键把条目建出来**，
而不是回落到"按环境实参建"。具体是在 `cachedClassInfoForMember` 的 miss 分支里
用 **computed key + `JsonClassInfo`** 写入 `_classes`（`putIfAbsent`，幂等），
再返回该实例。这会让：
- 该成员的 `_runtimeClassInfo` 直接是**正确的一格**；
- 其 `_elementClassInfo` 的内层列表也因此拿到**正确的元素类型**（Round 5 的
  同裸类元素作用域已就位）。

**⚠ 已知风险（必须单类验证）**：Round 12 曾试过"miss 后重试"（变体 B）而**无效**，
原因是当时**写侧没有按正确键建条目**；本轮方案补上了写侧，与此前不同。
但仍须**先单类验证 `ArrayTests` + `ObjectTests`**，再全量——因为
`putIfAbsent` 到 `_classes` 会影响所有后续查找。

### 4.38 Round 32：开始实施 miss 建条目，因补丁锚点不齐导致编译失败，已干净回退

按 §4.37 动手实施"miss 时按 computed key 建条目"。**两个字符串替换不一致**：
第二个锚点命中、第一个未命中，于是新加的 helper 引用了
`endLookupArgumentsScope` 而**用到它的 miss 分支并没有插进去**，模块编译失败：
`找不到符号: 方法 endLookupArgumentsScope(java.lang.Object)`。

**已 `git checkout -- java/` 回退并重装 compat，全量复核 = 723（430+293）**、
`git status` 干净，**没有留下半成品改动**。

**下一轮实施要点（避免重复本轮的错误）**：
1. 必须**整体 read-then-edit** `cachedClassInfoForMember`，因为 miss 分支与 helper
   必须**成对插入**，不能用两次互不相关的字符串替换；
2. 已提交的 compat 里**可用的作用域 API 只有**
   `beginElementArgumentsScope(owner, element, args)` / `endElementArgumentsScope(token)`；
   **没有** `endLookupArgumentsScope`（那个名字只存在于更早的一次未提交实验里，
   本轮误用了它——这是编译失败的直接原因）；
3. 建 `JsonClassInfo` 时必须让**成员实参处于作用域内**，
   这样它内部的元素查找才会解析出**元素自己的实参**。

### 4.39 Round 33：按 §4.38 的要点实施成功（编译通过），**但单类验证为负，已回退**

这次**先整段读了 `cachedClassInfoForMember`**，再用一次 edit 把 miss 路径整体替换，
并新增 helper `createClassInfoForMemberUnderScope`（在**成员实参作用域内**
反射构造 `dotnet.text.json.JsonClassInfo`，再 `putIfAbsent` 到 `_classes`）。
**编译通过**（Round 32 的编译错误未重现）。

**单类验证（纪律要求的那一步）**：

| 测试类 | 基线 | 加 miss 建条目 | 变化 |
|---|---:|---:|---:|
| `ArrayTests` | 2F+32E = 34 | 5F+31E = **36** | **+2 更差** |
| `ObjectTests` | 13F+53E = 66 | 18F+57E = **75** | **+9 更差** |

**净为负，且 `ObjectTests` 明显变差**，故**未跑全量**即回退；
复核 = **723（430+293）**、`git status` 干净。

**这是对"键对、条目缺失"问题的第二次机制级尝试**（第一次是 Round 12 的变体 B，
那次没有写侧）。**两次都失败**，说明**"条目缺失"是症状而不是病因**：
**急切地把条目建出来，会改变后续成员的解析结果**——
原本正确的成员现在拿到**另一格** class info，于是 `ObjectTests` 变差、
而 `ArrayTests` 略好（两类在**同一张共享 map** 上走不同的成员）。

**下一轮方向（明确否定两条路之后）**：
**不要在读路径上急切建条目**。若要修这一族，必须改**类信息的产出时机（预热期）**，
让**每个成员的条目在任何读取之前就已存在**，
而不是在读取时打补丁。具体落点应转向
"预热期遍历成员时为每个成员按其成员实参建条目"，
并且**同样要单类验证**（预期比读路径方案更稳，因为它不改读取时的语义）。

### 4.40 Round 34：**预热期实参槽位已修正**（探针证实），**但实测中性**，已回退

按 §4.39 的方向实施：compat 新增
`beginMemberArgumentScope(member, rawType)` / `endMemberArgumentScope(token)`，
并让 `JsonClassInfo.cacheMember` 在调用 `addProperty` 期间**把该成员的实参占住
`MEMBER_ARGUMENTS` 槽位**，使"该成员的转换器与类信息"在**正确实参**下建立。

**设计目的达成（探针证据）**：

```
beginMemberArgumentScope(MyInt16TwoDimensionList)CSharpList = [CSharpList<Integer>]
beginMemberArgumentScope(MyInt16ThreeDimensionList)CSharpList = [[Integer]]
beginMemberArgumentScope(MyStringList)CSharpList = [class java.lang.String]
```

即**二维成员终于在自己的实参下建条目**（此前该槽位已被更早的 `List<string>` 成员覆盖）。

**但实测中性**：`ArrayTests` 34、`ObjectTests` 66、**全量 723**——与基线**逐位相同**。
→ **"预热期实参槽位错误"本身并不是这些失败的原因**。已回退，保持基线最小；
复核 = **723（430+293）**、`git status` 干净。

**本轮的价值（重要的否定结果）**：
§4.31→§4.33→§4.39 一路指向的**"预热期顺序"方向现在也被排除**：
槽位已确证正确，失败数**一点没动**。
至此**两条主路都被机制级否定**：
- **读路径急切建条目**（Round 12 变体 B、Round 33）：两次都更差；
- **预热期修正实参槽位**（本轮）：正确但中性。

**下一轮方向（必须换方法，不要再猜机制）**：
把本轮改动**保留在树上**、重跑 §4.31 的探针，
**实测"槽位正确之后，二维属性在读取时到底解析到了哪一格"**，
再据此决定下一步；而不是继续假设下一种机制。
（本轮已把该改动回退，若要复用需重新落一遍——代码与探针输出都已留档。）

### 4.41 Round 36：按 §4.40 的要求**先观察**——实参在读取时已正确，问题收窄到"查到了什么"

重新落上 §4.40 的预热期作用域，并在属性上加观察点，得到干净的横幅：

```
[cs2j.obs] before lookup: cached=false
                          rawType=class io.github.ningpp.compat.CSharpList
                          memberArgs=[io.github.ningpp.compat.CSharpList<java.lang.Integer>]
```

**这是实质进展**：加上预热期修正后，二维成员在**读取时刻的成员实参是正确的**
（`[CSharpList<Integer>]`，既非 null 也非 `[String]`）——
而 Round 31 时同一位置拿到的是错误的环境实参。

**但这一族仍然失败**，于是开放问题被收窄成**单一问题**：
既然
- `_runtimeClassInfo == null`（要查）、
- `rawType = CSharpList`、
- `memberArgs` 正确，

那么 **`getOrAddClass` 对它返回了什么？返回的那个 class info 的 `elementType`
是内层列表还是 `String`？** 这**只差一个探针**，就是下一轮的确切测量动作。

**⚠ 本轮的清理教训（必须记住）**：我试图用**按行过滤**去掉生成代码里的插桩，
结果把两行横幅的续行一起删掉，**破坏了 `JsonPropertyInfo.java`**
（出现"语句不应在方法和初始化程序外部"）。恢复办法是
**删除 `JsonPropertyInfo.java` 与 `JsonClassInfo.java` 两个文件并用转换器重新生成**，
补丁通道干净地重建了它们。
→ **规则：移除生成代码里的插桩必须用针对确切块的 edit，绝不能用按行过滤。**

**收口**：重新生成后复核 = **723（430+293）**、`git status` 干净。

### 4.42 Round 38：**开放问题已答**——`getOrAddClass` 在实参正确的情况下**仍返回 `String` 那一格**

按 §8.2 执行那个"只差一个探针"的测量（**同时保留 Round 34 的预热期作用域**，
以保证成员实参在读取时确实正确）：

```
[cs2j.q] rawType=class io.github.ningpp.compat.CSharpList cached=false
[cs2j.q] after lookup: info.elementType=class java.lang.String
                       info.type=class io.github.ningpp.compat.CSharpList
```

**结论**：属性带着**正确的成员实参**去查 `CSharpList`，
`getOrAddClass` **仍然交回 `CSharpList[String]` 那一格**（其 `elementType = String`）。

**这把 §4.31 的问题彻底闭合，并排除了最后一个"读侧"解释**：
错误的槽位**不是**由"属性处实参缺失/错误"造成的——
实参已确证正确，是**查找本身**产出的就是这个错格子。

**三条事实合起来只剩一种可能**：
- §4.31：正确键（`CSharpList[CSharpList<Integer>]`）的条目**在该次查找时不存在**；
- §4.34：把预热期实参槽位改对，**对结果无影响**；
- §4.38（本轮）：实参正确，查找仍返回错误格子。

→ 剩下的可能是：**该键的条目在这条路径上从未被创建**，
因此 `getOrAddClass` 每次都落到"环境实参算出的键"上。

**下一次测量（仍是"一个探针"，不猜机制）**：
在 `getRuntimeClassInfo` 那一刻把 `_classes` 里**所有含 `CSharpList` 的键**打印出来。
- 若 `CSharpList[CSharpList<Integer>]` **不存在** → 问题转为
  "预热期到底是谁创建类信息、为什么跳过这个成员"；
- 若**存在** → 说明 `getOrAddClass` 内部算出的键与探针读到的键**不一致**，
  那就是一行对比的事。

**探针清理方式**：按 §4.41 的规则，**删除生成的 `JsonPropertyInfo.java` 并用转换器重新生成**
（未使用按行过滤）。复核 = **723（430+293）**、`git status` 干净。

### 4.43 Round 39：**诊断完成**——正确键的条目**从未被创建**

按 §4.42 的下一步，在 `getRuntimeClassInfo` 处 dump `_classes` 中所有含 `CSharpList` 的键：

```
[cs2j.keys] [io.github.ningpp.compat.CSharpList[class java.lang.String]]
```

**只剩 `CSharpList[String]` 一个键**。`CSharpList[CSharpList<Integer>]` **根本不存在**。

**至此这一族的诊断完全闭合，四个事实互相印证**：

| 轮次 | 事实 |
|---|---|
| §4.31 | 正确键（`CSharpList[CSharpList<Integer>]`）在该次查找时 `found=false` |
| §4.34 | 把预热期实参槽位改对 → **对失败数无影响** |
| §4.38 | 实参已确证正确，`getOrAddClass` **仍返回 `String` 那一格** |
| **§4.43（本轮）** | 那一刻 `_classes` 里**只有 `String` 那一格** |

**结论**：`getOrAddClass` 不是"选错了格子"，而是**唯一存在的那一格就是错的**——
**该成员的正确条目从未被创建过**。所以后续一切查找都只能落到 `String` 上，
这也解释了为什么改实参、改查表键、改时机**都无效**：**没有东西可命中**。

**因此问题的正确形态是**：
> **预热期到底由谁创建类信息，为什么跳过了 `List<List<short>>` 这个成员？**

这是与前面所有尝试**不同层级**的问题（"创建缺失"而非"查找错误"），
且**不能**在读路径或参数槽位上解决——两者都已实测无效。

**下一轮的测量动作**：
在 `JsonSerializerOptions.getOrAddClass` 里打印 `type` 与算出的 `cacheKey`
（每次创建时一行），跑 `NestedListProbe`，观察二维成员**是否曾经走到创建**：
- 若从未走到 → 上游（属性/成员解析）就把它跳过了，去查那一层；
- 若走到但键是别的 → 该次算 key 的输入仍不对，回到 §4.15 的成员实参通路。

**收口**：探针按规则用"删文件 + 重新生成"清除；复核 = **723（430+293）**、`git status` 干净。

### 4.44 Round 41：创建层测量做完 —— 断点在**读时的实参上下文**，两条实现均**净负已回滚**

§4.43 要求的那一个探针做完了，而且比预期多拿到两段信息。

**探针 1（`getOrAddClass` 打 `type` + 算出的 `cacheKey`）**

```
[cs2j.mk] getOrAddClass CSharpList -> key=io.github.ningpp.compat.CSharpList[class java.lang.String]
```

整个探测过程中 `getOrAddClass(CSharpList)` **只被进入一次**，键是 `String` 那一格。
`CSharpList[CSharpList<Integer>]` **从未被创建** —— 确认 §4.43。

**探针 2（`JsonClassInfo.getProperty` 打成员名 + 缓存里的属性信息）**

```
[cs2j.mk] this=SimpleTestClass pn=MyInt16TwoDimensionList inCache=true cachedInfo=class io.github.ningpp.compat.CSharpList
```

属性**在缓存里**，但 `cachedInfo` 的运行时类型是**擦除后**的 `CSharpList`。

**探针 3（`JsonClassInfo.addProperty` 打成员实参）—— 关键**

```
[cs2j.mk] addProperty MyInt16TwoDimensionList memberType=class ...CSharpList runtimeType=class ...CSharpList
          converter=ListOfTConverter typeToConvert=class ...CSharpList classType=8
[cs2j.mk] getPropertyType MyInt16TwoDimensionList
          generic=io.github.ningpp.compat.CSharpList<io.github.ningpp.compat.CSharpList<java.lang.Integer>>
          CSharpListLayer=[io.github.ningpp.compat.CSharpList<java.lang.Integer>] memberArgs=[...Integer>]
```

**创建时一切都对**：转换器是 `ListOfTConverter`，`PropertyInfo.getPropertyType()` 发布的实参
是 `CSharpList<CSharpList<Integer>>`，`registerMemberType` 也把它记到了成员上。

**探针 4（`getElementClassInfo`）—— 断点在这里**

```
[cs2j.mk] getElementClassInfo owner=class CSharpList elem=class java.lang.Object ownerArgs=[] elemArgs=[] scope=false
```

元素被解析成 `java.lang.Object`：**类信息创建时用的那层实参在读取时已经不在了**。
追加探针确认读时 `currentGenericArgumentsLayer(CSharpList)` = `[class java.lang.String]`，
即 `List<string>` 成员的实参。

**结论**：不是"查找选错格子"（§4.42），也不只是"条目没创建"（§4.43），而是
**创建与消费之间隔着一个每线程单槽、会被后续成员覆盖的实参注册表**；到读取时
元素类型只能退化成 `Object`，嵌套列表于是用错转换器读。

**两条实现（都做了，都实测净负，都已回滚）**

以**同一棵再生树的干净基线**为参照（把本轮改动用开关关掉、当天同一棵树测一遍，
得 `737`；这一步是必须的，见下方纪律）：

| 实现 | 基线 | 结果 | 判定 |
|---|---|---|---|
| 成员键**创建**类信息（直接 `putIfAbsent` 闭键，不动擦除键） | 737 | 766 → **756** | 回滚 |
| 元素类型按类信息**自身**的实参推导（`__cs2jOwnerTypeArguments`） | 737 | 757 | 回滚 |
| 两者同时 | 737 | 756（修 20 / 坏 39） | 回滚 |

失败集中在 `SpanTests#read(Class,byte[])[1][2][24][28..32]`、
`ReferenceHandlerTests#readTestClassesWithExtensionOption(...)` 同索引、
`ObjectTests#write(ITestClass)[9..32]`、`DictionaryTests#hashtable`、
`CollectionTests#readSimpleClassWithImmutableArray`、
`ExtensionDataTests#nestedClassWithJsonElementExtensionDataProperty`。
**隔离运行判定**：`readGenericIEnumerableOfIEnumerable`、
`readGenericIReadOnlyListOfGenericIReadOnlyList` **隔离下也失败 → 真回归**；
`DictionaryTests#hashtable` **隔离下通过 → 顺序噪声**。这条"隔离运行"判据本轮第一次用，
建议保留。

机理：成员键创建会**多出一个 `_classes` 条目**；按实参推导元素类型会把
字典/接口/抽象集合的元素位换成键或接口类型 —— 实测 `ImmutableDictionary` 的元素从
`String` 变成 `Object`（因为实参是 `[K, V]`，第一位是键），
`IReadOnlyList<IReadOnlyList<int>>` 的元素被换成接口，读取即失败。

**回滚后复核**：`Tests run: 7902, Failures: 430, Errors: 293, Skipped: 18` = **723**，
与 §4.43 收口时**逐用例集合完全一致**（fixed 0 / regressed 0）；`git status` 仅剩
文档与注释改动。

**本轮新增的三条纪律（都已写进计划 §8.3）**

1. **基线必须与当前生成树同源**：拿 Round 40 存的失败集去比一棵**重新生成过**的树，
   把 39 项里的一批假项当成"本轮弄坏的"。正确做法是把本轮改动开关关掉、在**同一棵树**上
   测一遍得到干净基线（本次 737），再开关对比。
2. **compat 改完必须强制重建**：`mvn -q install` 会因时间戳判定"无需重编"，`-q` 又吞掉输出，
   于是连着两轮测的是**旧 jar**（时间戳没变才发现）。改用
   `mvn -o clean install -DskipTests` 并**核对 jar 时间戳**。
3. **PowerShell 传 `-D` 要给整个开关加引号**：`-Dcs2j.trace.typeargs=true` 会被 PowerShell
   解析坏，java 报"找不到主类 `.trace.typeargs=true`"，日志看起来像"探针没有输出"，
   白白多花一轮。
### 4.45 Round 42：**推翻"预言机"结论**——从 writer 簇拿到 4 条真修复（702 → 674）

§6.4 曾把 `Utf8JsonWriterTests` 61 条建议"作为测试预言机问题接受"。**这是错的**。
本轮从同一簇里拿到 **49 条真修复**，根因全部在 compat 的两个类里。

| 提交 | 内容 | 收益 |
|---|---|---|
| `e777d254` | C# `[JsonPropertyName]` 送进运行时 | 723 → 709（修 14 / 坏 0） |
| `5540ec46` | 非公开访问器可见性 + `[JsonInclude]` 例外 | 709 → 702（修 7 / 坏 0） |
| `bdf54f07` | **孤立代理项转义为 U+FFFD** | 702 → 692（修 10 / 坏 0） |
| `01a85535` | **数字按 .NET 格式写** | 692 → 678（修 14 / 坏 0） |
| `b38263ff` | **U+FFFD 在所有编码器下都转义** | 678 → 674（修 4 / 坏 0） |

**三条根因（都在 compat，都不在断言里）**

1. `JavaScriptEncoder.encode(char-span)` 把每个 UTF-16 **码元**独立处理，
   于是孤立代理项被原样写成它自己的转义。实测：属性名 `{0xD801,'a'}` 输出
   `{"\ud801a":"\ud801\udc37"}`，而 .NET 输出 `{"\uFFFDa":"\uD801\uDC37"}`。
   正确做法是**按标量**处理：合法代理对是一个标量（转义成配对的两个单元），
   孤立代理项是非法 UTF-16（替换为 U+FFFD）。
2. `Utf8Formatter.tryFormat(double/float)` 用了 Java 的 `Double.toString`/`Float.toString`，
   整数值必带小数点：写 `1.0` 得到 `"1.0"`，.NET 的默认 `G` 写 `"1"`。
   double 改用已有的 `DoubleHelper`；float 新建 `FloatHelper`——
   **不能先加宽到 double**：float 的精确二进制值的最短 double 表示长得多，
   `0.1f` 会变成 `0.10000000149011612`。
3. `willEncode` / `needsEscapingUtf16` 让 U+FFFD 走了"非 ASCII 不管"的分支，
   但 U+FFFD 是**所有**编码器都转义的字符（包括 `UnsafeRelaxedJsonEscaping`）。

**本轮踩到并记入纪律的两个坑**

- **javac 在注释里也会处理 `\u` 转义**：注释里写 `\uXXXX` 占位符会报
  "非法的 Unicode 转义"（`\uD801` 更是直接变成控制字符）。注释里只能写
  "backslash-u"，不能写真的反斜杠加 u。
- **`destination.set(written++, cond ? charExpr : intExpr)` 会退化到
  `set(int, Object)` 重载**，在 char 后备的 Span 上抛 `ArrayStoreException`；
  两个分支必须分开写。

**一次被实测否掉的小实验（勿重复）**：放宽 `willEncode` 让
`UnsafeRelaxedJsonEscaping` 不转义增补平面标量 —— **中性（674→674，集合完全相同）**，已回滚。
原因已查明并写进代码注释：`UnicodeRanges.getUnsafeRelaxedRanges()` 只到 U+FFFD，
因为 `UnicodeRange.create` 收的是 `char`，所以那条分支在现有数据结构下**永远不可能为假**。

**回滚/最终复核**：`Tests run: 7902, Failures: 379, Errors: 295, Skipped: 18` = **674**（起点 855，**−181**）；
`git status` 干净。
### 4.46 Round 44：**`internal` 构造函数未被遵守**（674 → 664，修 10 / 坏 0）

§8.2.4 定位的线索实施完毕，提交 `2dce1154`。

**定位依据**：`CollectionTests.read_Generic_NoPublicConstructor_Throws` 的 20 个参数用例里
**只有 `*InternalConstructor` 那 6 个失败**，`*PrivateConstructor` 全部通过。
这个"一半通过"的切分直接指出：私有构造函数被如实发成 `private`（既有守卫已覆盖），
而 `internal` 被转换器统一发成 `public`（为了跨包可达），于是
`TypeHelper.getConstructorInfo(type, Public|Instance)` 答"找到了"，
.NET 该抛的 `NotSupportedException` 没有抛。

**做法**：新增 compat 标记 `CompatInternalCtor`，`ConstructorTransformer` 对
"internal 且非 public" 的构造函数发出；`TypeHelper.getConstructorInfo` 遵守它。

**本轮最贵的坑（值得记住）**：第一版只改了 `getDeclaredConstructors` 回退循环，
实测**完全中性（674 → 674，集合相同）**。原因是
`type.getConstructor(...)` 的**快路径在循环之前就 return 了**，
循环里的 `continue` 根本没机会执行。是 `[cs2j.i] ENTER ... publicCtor=1`
这一行探针（`publicCtor=1` 表示 `getConstructor()` 拿到了构造函数）暴露的。
**教训：修"查找"逻辑时，先确认有几个返回点，不要只改最后那个。**

**验证**：`Tests run: 7902, Failures: 369, Errors: 295, Skipped: 18` = **664**
（起点 855，**−191**）；逐用例集合差 = 修 10 / 坏 0。

**残留**：同族另有 8 条 `UnsupportedOperationException`，但分散在 5 个类
（`ArrayTests`、`ExceptionTests`×3、`NullTests`×2、`DictionaryTests`），形状各异，
**不是同一根因**，不要当簇处理。
### 4.47 Round 45：`csFormat` 的 double 渲染（664 → 648，修 16 / 坏 0）

提交 `15828ffc`。

**定位方式（本轮最有效的一步）**：不是从失败清单猜，而是**写一个探针把测试自己的
往返路径完整重放一遍，并打印第一个不同的下标**。结果一次就给出：
`testJsonReaderUtf8` 在 offset 7 处 `expected "age, 30,"` vs `actual "age, 30.0,"`。

**根因**：C# 的 `string.Format("{0}", v)` 调 `v.ToString()`，`30.0` 渲染成 `"30"`
（.NET 不变文化会去掉末尾 `.0`）；Java 的 `%s` 调 `Double.toString`，永远保留 → `"30.0"`。
`JsonTestHelper.dictionaryToString` 用 `StringHelper.csFormat("%1$s, %2$s, ", key, value)`，
于是**JSON 里每个整数在往返后都变成 `30.0`**。测试拿这个往返结果与 Json.NET 原文逐字符
比较，第一个不同的字符就判整例失败 —— 所以**一个字符的差异会带走一整组参数用例**。

**做法**：`StringHelper.csFormat` 在已有的 Boolean→"True"/"False" 转换旁边，
把 `Double` 交给 `DoubleHelper`、`Float` 交给 `FloatHelper` 渲染。

**代价评估**：先生成树里 `StringHelper.csFormat(` 只有 **17 个调用点**（5 个文件），
所以改动面足够小，可以一次全量测完 —— 这个"先数调用点再改公共 API"的习惯值得保持。

**已建立的第三条通道（写进 §8.1）**：`%s` / `string.Format` 的默认渲染。
凡是"整段文本只差一个 `.0` / 一处数字格式"的失败，先查 compat 的格式化入口。

**已知未修的独立问题**：超出 double 精确表示的整数（`9223372036854775807`）。
.NET 的最短往返 double 格式仍然输出原整数文本，Java 输出 `9.223372036854776E18`。
这是 `testJsonReaderUtf8` 残留 2 例的原因；**不要顺手改 `DoubleHelper`**，
它会波及所有数字输出。探针 `DblProbe` 已确认 `DoubleHelper` 在
`1e7`、`1e18`、`1e22`、`4.9e-324`、`1.7976931348623157e308` 上均与 .NET 一致，
只在"超出精确整数"这一点上无法弥合。

**验证**：`Tests run: 7902, Failures: 353, Errors: 295, Skipped: 18` = **648**
（起点 855，**−207**）；逐用例集合差 = 修 16 / 坏 0。
### 4.48 Round 46：两次**中性**尝试 + 一条**结构性**发现（648 → 648）

本轮没有净收益，但把两件容易被重复浪费的事记下来。

**尝试 1（中性）**：`EnumTests.parse_WithinRange_Unsigned_ReturnsExpected` /
`..._Signed_...` 共 20 条在 `EnumConverter.read` 抛
"Integer cannot be cast to Byte" / "cannot be cast to Short"。
推断是生成的 `(Byte)(((byte)(Unsafe.as(x))))` 里窄化转换作用在**装箱值**上，
于是改成把窄化作用在拆箱后的 int 上：
`(Byte)(((byte)((int)(Unsafe.as(x)))))`。
结果 **修 10 / 坏 10，净 0** —— 无符号那 10 条好了，有符号那 10 条坏了
（`Byte cannot be cast to Integer`）。

**尝试 2（中性）**：既然两种写法各修一半坏一半，那可能是"窄化本身就不该有"。
去掉窄化只留装箱：`(Byte)(Unsafe.as(x))`。结果 **修 0 / 坏 0，集合完全相同**。

**结论**：`EnumConverter` 那个转换异常的**真实来源不在这个表达式构造里**，
两次改动分别证明了"改错了地方"和"这里根本不是断点"。已回滚。
**下一次要碰这一簇，先打印 `Unsafe.as(x)` 的实际返回类型和
`EnumHelper.toObject` 的实参类型，不要再猜表达式形状。**

**结构性发现（未实施，值得单独一轮）**：
`ImmutableDictionary`（11 条）、`ImmutableSortedDictionary`（3 条）报
"cannot be cast to `CSharpReadOnlyDict`"，与 `ArrayIndexOutOfBoundsException`
（21 条，`IEnumerableConverterFactory.createConverter` 里
`TypeHelper.getGenericArguments(...)[0]` 拿到**空数组**）**很可能是同一个根因**：

> compat 的不可变类型没有建模成 `Iterable`/`Map`：
> `ImmutableDictionary<K,V> implements IImmutableDictionary<K,V>`，
> `ImmutableList<T> implements IImmutableList<T>`，而这些接口**都不继承
> `Iterable`/`Map`**。于是
> (a) 生成的转换器把值 cast 成 `CSharpReadOnlyDict` 时失败；
> (b) `TypeHelper.isCollectionType(raw)` 判定失败 →
> `findTypeArgumentsInHierarchy` 找不到实参 → `getGenericArguments` 返回**空数组** →
> 工厂里 `[0]` 越界。

**建议路线**：让 `IImmutableList<T> extends CSharpReadOnlyList<T>`、
`IImmutableDictionary<K,V> extends CSharpReadOnlyDict<K,V>`（按 .NET 的真实层次：
`IImmutableList<T> : IReadOnlyList<T>`、`IImmutableDictionary<K,V> : IReadOnlyDictionary<K,V>`），
**但这是接口层的大改，必须单独立项并逐用例验证**。
`IImmutableDictionary` 上有一条既有注释说"故意不做成 Iterable，
因为转换器生成的包装类实现 `Iterable<CSharpKeyValuePair<K,V>>` 会与 `Map.Entry` 迭代冲突"——
动手前**先读那条注释并确认冲突是否仍然存在**。

**验证**：本轮回滚后复核 `Tests run: 7902, Failures: 353, Errors: 295, Skipped: 18` = **648**，
与 r067（本轮起点）**逐用例集合完全相同**；`git status` 干净。
### 4.49 Round 47：**先探运行时类型**，一次修掉 10 条（648 → 638）；并纠正 Round 43 的错误结论

**收益**：`648 → 638（修 10 / 坏 0）`，提交 `9317fc8a`。

**方法正确性的直接对比**：Round 46 靠**猜表达式形状**改了两次，分别得到 10/10 与 0/0；
本轮听自己上一轮写下的建议，**先打印实际类型**，一次命中：

| 生成的实参 | `Unsafe.as(x)` 实际返回 | 后续转换 |
|---|---|---|
| `int` 变量 | `java.lang.Integer` | `(Byte)(…)` 与 `(Byte)((byte)(…))` **都 CCE** |
| `byte` 变量 | `java.lang.Byte` | `(Byte)(…)` 可以，`(byte)(…)` **CCE** |
| `short` 变量 | `java.lang.Short` | 同上形状 |

**为什么结果侧无解**：Java 里没有任何转换能把 `Integer` 变成 `Byte`/`Short`，
而同一个生成形状必须同时服务 int 实参（需窄化）与已窄化实参（再窄化即 CCE）。

**修正点**：C# 写了 `Unsafe.As<TFrom,TTo>`，但编译器调用前已把 `byte`/`short`
局部变量提升为 int，Java 实参因此是 int，`Unsafe.as` 走泛型 `Object` 重载装箱成 `Integer`。
**把实参还原成声明的 `TFrom`** 后，各分支自洽。

**纠正 Round 43 的错误结论**（重要）：
Round 43 曾记录"成员级 `[JsonConverter]` 的通路根本没被走到"，
并据此把 `CompatJsonConverter` 桥整条回滚。**该判断是错的**。Round 47 探针：

```
[cs2j.dc] determineConverter-with-member parent=PocoWithInvalidConverter runtime=Integer member=PropertyInfo:MyInt
[cs2j.dc] member-attr lookup -> null
```

`determineConverter`（`JsonClassInfo:441` 调用）**确实被调用且拿到了 member**——
只是 `getAttributeThatCanHaveMultiple(..., JsonConverterAttribute.class, memberInfo)` 返回 null。
原因是测试用的是**派生特性**（`class InvalidConverterAttribute : JsonConverterAttribute`），
成员上挂的是派生类，查询问的是基类，而当前 compat 查找**不匹配子类**
（.NET 的 `GetCustomAttribute<T>()` 会匹配派生类型）。

**教训**：Round 43 是在 `getCustomAttributes` 上打点、看到"没有进入
`materializeFromCompatAnnotation`"就推断"通路没走到"。**打点位置选错了**——
真正该看的是**查询的返回值**，而不是某个下游函数有没有被调用。
记录于此以免再次误判。

**验证**：`Tests run: 7902, Failures: 353, Errors: 285, Skipped: 18` = **638**
（起点 855，**−217**）；插桩移除后复核逐用例集合与 r071 **完全相同**；`git status` 干净。
### 4.50 Round 48：成员级 `[JsonConverter]` 打通（638 → 631），并纠正 Round 43

**收益**：`638 → 631（修 7 / 坏 0）`，提交 `316989da`；另有一条推进性修复 `2ecd893b`。

**三个缺口必须一起补，单独补任何一个都实测中性**：

1. 转换器发 `@CompatJsonConverter`，并**沿 C# 特性继承链**解析转换器类型。
   派生特性把类型放在基类构造调用里：
   `class InvalidConverterAttribute : JsonConverterAttribute { ... : base(typeof(int)) }`，
   所以特性自身的实参是空的，必须读**构造函数初始化器**
   （`PropertyTransformer.TryGetConverterTypeFromConstructorInitializer`）。
2. `PropertyInfo` 从标记物化出生成的 `JsonConverterAttribute`。
3. **`PropertyInfo` 覆写 `getCustomAttributes(Class, boolean)`**。
   此前只实现了不带类型参数的版本，于是**每一次带类型的查找都落到
   `ICustomAttributeProvider` 的默认实现并返回空数组**——特性被构造出来后又被丢弃。
   **这正是 Round 43 误判为"成员通路没被走到"的真实原因**：通路走到了，
   只是两参重载没被覆写。新的覆写还按请求类型过滤（与 .NET 一致），
   避免查一种特性时把成员的其他特性也报出来而触发重复特性检查。

**Round 43 的结论已正式推翻**：那不是"通路没走到"，而是"重载没覆写"。
当时的打点选在了下游函数上，看到"没进 `materializeFromCompatAnnotation`"就下了结论；
正确做法是**打印查找的返回值**。

**推进性修复 `2ecd893b`（本轮失败数不变，但测试前进了一步）**：
`ThrowHelper` 用 `Class.toString()` 拼位置，得到
`class ...CustomConverterTests$PocoWithInvalidConverter.MyInt`，
而 SDK 断言的是 `...CustomConverterTests+PocoWithInvalidConverter.MyInt`
（.NET 用 `+`、Java 用 `$`）。校验**本身已经正确触发**，只是拼写不对。
新增 `TypeHelper.getMemberLocation`（取 C# 全名 + `.` + 成员名，并把 `$` 规范成 `+`）
与 `ProjectPasses.PatchAttributeLocationFormatting`（改写生成语句，并删除紧随其后的
`location += ...`，否则成员名会被追加两次）。
实测该用例的失败**从 `Sub-string not found` 变成 `No exception thrown`** ——
消息已匹配、测试推进到后面的断言。

**本轮又踩一次的坑（已写进纪律）**：改 compat 源码后必须
`mvn -o clean install`（不能用 `-q install`）再测量。
本轮有**两次测量**跑在旧 jar 上，输出与改动前逐字相同，极易误读为"补丁没生效"。
### 4.51 Round 49：类型级 `[JsonConverter]`（631 → 628）；两个用例的失败点继续前移

**收益**：`631 → 628（修 3 / 坏 0）`，提交 `4c0be592`。

Round 48 只打通了**成员级** `[JsonConverter]`；**类型级**仍然到不了运行时，
于是 `getAttributeThatCanHaveMultiple(typeToConvert, ...)` 找不到特性，
"转换器没有公开无参构造"不会被拒 —— `converterWithoutDefaultCtor` 断言的正是这个抛出。

- `ClassTransformer` 现在对类型级 `[JsonConverter]` 也发 `@CompatJsonConverter`，
  复用 `PropertyTransformer` 已有的**继承链解析器**（改为 internal），
  因此派生特性同样解析到基类构造函数里写的转换器类型。
- `TypeHelper.materializeTypeAttributeFromCompatAnnotation` 增加
  `JsonConverterAttribute` 分支（与原有的 `JsonNumberHandlingAttribute` 并列）。

**两个用例的失败点继续前移**（本轮未修完，但方向已明确）：

- `attributeOnPropertyFail`：从"子串找不到"变成**另一个更靠后的子串**找不到 ——
  现在缺的是 `System.Collections.Generic.ICollection`1[dotnet...CustomConverterTests+InvalidTypeConverterEnum]`
  这种 **C# 泛型类型名**（含反引号元数 `\`1` 与方括号实参、`+` 嵌套分隔符）。
  与 Round 48 修的 `getMemberLocation` 是**同一类"拼写"问题**，只是发生在
  泛型类型参数的渲染上。
- `attributeCreateConverterFail`：仍然是 `No exception thrown`，
  说明它的第二次 serialize/deserialize 路径还没有触发校验。

**建议下一步**：做 `compatTypeName`（C# 风格类型名渲染：泛型元数 `\`N`、
实参用 `[...]`、嵌套用 `+`）并让 `ThrowHelper` 的
`SerializationConverterOnAttributeNotCompatible`/`SerializationConverterNotCompatible`
使用它。**注意**：这类渲染会被多个断言依赖，务必逐用例验证。

**验证**：`Tests run: 7902, Failures: 345, Errors: 283, Skipped: 18` = **628**
（起点 855，**−227**）；逐用例集合差 = 修 3 / 坏 0。
### 4.52 Round 50：C# 风格类型名渲染（628 → 626）

提交 `d18e4827`（泛型名）+ `9d25d891`（BCL 名）。

**症状**：`attributeOnClassFail` / `attributeOnPropertyFail` 的消息里，
被转换的类型用 Java 名渲染：
`io.github.ningpp.compat.CSharpICollection`1[...]`，
而 SDK 断言的是 `System.Collections.Generic.ICollection`1[...+InvalidTypeConverterEnum]`。

**做法**：`TypeHelper.getCsTypeName`：
- C# 全名 + 嵌套 `+`，再拼 `` `arity[arg,...] ``；
- **类型实参从转换器注册表取**——Java 把实参从 `Class` 上擦掉了，
  但构造这条消息时工厂**刚刚消费**完描述该类型的那一层（`CONSUMED_ARGUMENTS`），
  所以那一层正是该显示的实参；实参递归处理 `ParameterizedType` / `GenericArrayType`；
- **BCL 兼容类不注册进 `CompatTypeIdentity`**：那个注册表同时喂给
  `isImmutableCollection`，把 BCL 接口注册进去会让不可变集合工厂误判，
  因此改用 `TypeHelper` 内的局部映射。

**验证**：`Tests run: 7902, Failures: 343, Errors: 283, Skipped: 18` = **626**
（起点 855，**−229**）；两轮逐用例集合差均为 **修 1 / 坏 0**。

**残留**：`attributeCreateConverterFail` 仍为 `No exception thrown`——
它的第二次 serialize/deserialize 未触发校验，与拼写无关，需单独查。
### 4.53 Round 51：`[JsonConverter(null)]` 与装箱基元类型名（626 → 625，`attributeCreateConverterFail` 全绿）

提交 `06c47712`。该用例本轮**连续推进两次**（227 → 230 → 通过），两步都是真实修复：

**1. C# `[JsonConverter(null)]` 需要哨兵**
`[NullConverter]`（`class NullConverterAttribute : JsonConverterAttribute { ... : base(null) }`）
是一个**存在但不指定转换器类型**的特性，它通过覆写 `createConverter` 接管创建。
此前它**完全不发标记**（转换器类型是 null，注解存不下 null），
于是"特性存在但无类型"与"没有特性"无法区分，该抛的序列化没有抛。
`CompatJsonConverter.value()` 改为默认 `Void.class` 作哨兵，两处物化点把它映射回 `null`
（即 `JsonConverterAttribute.getConverterType() == null` 的语义）。

**2. 装箱基元要按 C# 拼写**
消息里被转换的类型写成 `java.lang.Integer`，断言要 `System.Int32`。
`getCsTypeName` 的名字映射表补上装箱基元（`Integer/Long/Short/Byte/Boolean/Double/Float/
Character`）与 `String`/`Object`。

**验证**：`Tests run: 7902, Failures: 342, Errors: 283, Skipped: 18` = **625**
（起点 855，**−230**）；逐用例集合差 = 修 1 / 坏 0。

**至此 §8.2.6 的 `[JsonConverter]` 簇（成员级 + 类型级 + 派生特性 + null 哨兵 + 消息拼写）
全部收口**：`attributeCreateConverterFail`、`attributeOnClassFail`、`attributeOnPropertyFail`、
`converterWithoutDefaultCtor` 均已通过。
### 4.54 Round 52：不可变集合线索**一半被否证**；`Assert.null Actual: 0` 判为固有代价

本轮**无净收益**（625 → 625，回滚后复核逐用例集合完全相同）。

**探针 `probe/w42/ImmIfaceProbe.java` 实测的接口矩阵**：

| 类型 | `Iterable` | `CSharpReadOnlyDict` | `getGenericArguments` 长度 |
|---|---|---|---|
| `ImmutableList`/`HashSet`/`SortedSet`/`Stack`/`Queue`/`Array` | **true** | false | **1** |
| `ImmutableDictionary`/`ImmutableSortedDictionary` | false | **false** | 2 |

- **列表侧否证**：它们已经是 `Iterable`，实参也正常返回，**21 条
  `ArrayIndexOutOfBoundsException` 不来自这条线索**。
- **字典侧证实**：两个字典 `CSharpReadOnlyDict=false` → 工厂落到
  `ImmutableEnumerableOfTConverter` → 写路径 cast 失败，**与 14 条 cast 失败吻合**。

**但修复有既存阻碍**（加 `implements CSharpReadOnlyDict` 后**编译失败**）：

```
ImmutableDictionary 未覆盖 java.util.Collection 的抽象方法 clear()
ImmutableDictionary 的 clear() 无法实现 CSharpGenericIterable 的 clear()   // 返回类型冲突
ImmutableSortedDictionary 未覆盖 CSharpReadOnlyDict 的抽象方法 tryGetValue(K)
```

`CSharpReadOnlyDict` → `CSharpReadOnlyCollection` → `CSharpGenericIterable` →
**`java.util.Collection`**，接口因此强制要求 `add/remove/clear` 等**可变**方法，
而 `ImmutableDictionary.clear()` 返回 `ImmutableDictionary`，与 `Collection.clear()` 的
`void` 冲突。**这与 `IImmutableDictionary` 上那条"故意不做成 Iterable"的注释是同一类冲突。**
→ 该方向需要**设计**（让 `CSharpReadOnlyDict` 不再继承 `Collection`，或加只读适配层），
不是补丁。**本轮已回滚，未提交。**

**第二条判为"固有代价、不建议投入"**：`Assert.null() ... Actual: 0`（29 条中约 11 条，
如 `nullableValueTypeConverter_OptOut`、`defaultIgnoreNullValuesOnRead`）。
`Nullable<T>` 在 Java 侧映射为装箱类型，**"可空值类型的 null"与"装箱的 0"运行时不可区分**，
除非引入独立的 Nullable 包装类型（架构级改动）。

**验证**：`Tests run: 7902, Failures: 342, Errors: 283, Skipped: 18` = **625**，
与 r084 逐用例集合**完全相同**；`git status` 干净。
### 4.55 Round 53：C# 保证非 null 而 Java 返回 null 的异常消息（625 → 619，修 6 / 坏 0）

提交 `7ca010f2`。

**先探针、后改动**（上一轮刚确立的纪律）：`probe/w42/NpeProbe.java` 一次性证明
`FormatException` / `ArgumentException` / `RuntimeException` / `IllegalStateException` /
`IndexOutOfBoundsException` / `UnsupportedOperationException` **无参构造后
`getMessage()` 全部为 null**，而 `msg.contains(" Path: ")` 随即抛 NPE。

**语义差异**：C# 的 `Exception.Message` **保证非 null**（无消息时是 `""`），
所以 `ex.Message.Contains(...)` 与 `String message = ex.Message;` 都是安全的；
Java 的 `Throwable.getMessage()` 返回 **null**，直译过来的代码在第一次触碰消息时就崩。

**这正好解释了 `CustomConverterTests.FailConverter`**：
它 `throw Activator.createInstance(tExceptionClass)` 抛的是**无消息**的异常，
于是断言"应抛我自己的异常类型"的用例拿到的是
`Wrong exception type ... Actual: NullPointerException`。

**两个站点（各自都有独立的失败运行确认）**：

1. `JsonConverter1.readCore`（及 `writeCore`）的 "already has a path" 检查
   → `StringHelper.containsOrEmpty`。修完这一处**测试仍失败**，
   栈变成 `ThrowHelper.throwNotSupportedException:749`，这才暴露第二处。
2. `ThrowHelper.throwNotSupportedException`（生成物里有 3 份）
   把消息读进局部变量后立刻 `contains` / `length` → `StringHelper.nullToEmpty`。

**教训（值得复用）**：同一语义缺陷常在同一文件族里出现多次，
修完第一处后**必须重跑该用例并读新的栈**，不要因为"失败数没变"就判定无效——
本轮第一次测量就是 625 → 625，但栈已经换了位置。

**验证**：`Tests run: 7902, Failures: 336, Errors: 283, Skipped: 18` = **619**
（起点 855，**−236**）；逐用例集合差 = 修 6 / 坏 0。
### 4.56 Round 54：异常传播 + 泛型扩展数据（619 → 589，修 30 / 坏 0）

提交 `99151aa6`。**本会话单轮最大收益**，而且**第一步单独测是中性**——
正是 Round 53 刚写下的纪律救了这一轮。

**1. `MethodInfo.invoke` 吞掉了被调方自己的异常**
Java 的 `Method.invoke` 把被调方抛出的异常包进 `InvocationTargetException`，
而 `InvocationTargetException` **本身就是 `ReflectiveOperationException` 的子类**，
于是既有的 `catch (ReflectiveOperationException) → "Method invocation failed"`
把它整个吞掉。C# 的 `MethodInfo.Invoke` 是**原样传播被调方异常**。
修法：为 `InvocationTargetException` 加一个**排在前面**的 catch，
把 cause（`RuntimeException` / `Error`）原样 rethrow。

**单独测这一处：619 → 619，逐用例集合完全相同**——看起来是无效改动。
**但栈已经换了**：按 Round 53 的纪律去读新栈，发现 `ExtensionDataTests`
已经不再死在反射里，而是死在
`ThrowHelper.throwInvalidOperationException_SerializationDataExtensionPropertyInvalid`。
**如果没有重读栈，这一轮就会把真正的问题连同 30 条收益一起丢掉。**

**2. 声明的类型是类型参数时，扩展数据签名检查用错类型**
`[JsonExtensionData] public T Overflow` 在 Java 侧擦除为 `Object`，
于是 `isAssignableFrom(CSharpGenericIDictionary, declaredPropertyType)` 为假，
一个在构造类型上**确实是** `Dictionary<string, object>` 的属性被拒。
修法：声明类型不是字典时，改用**运行时属性类型**（`getRuntimePropertyType()`）。

**验证**：`Tests run: 7902`，失败用例 **589**，逐用例集合差 = **修 30 / 坏 0**
（28 条来自 `ExtensionDataTests`）。起点 855，累计 **−266**。
### 4.57 Round 55：**更正一次假测量**（Round 54 的"589 / −30"不成立）

**必须更正 Round 54（§4.56）的结论**。所谓 `619 → 589（修 30）`是**假测量**：

- 那次运行（`mvn-build-r089.log`）**在 `system-text-json` 编译阶段就失败**——
  当时 `PatchExtensionDataSignatureType` 的正则把 `Class declaredPropertyType = ...`
  这一行**一起吃掉了**，生成出引用未声明变量的代码。日志末尾是
  `mvn <args> -rf :system-text-json`，**测试模块从未执行**。
- `tools/triage_xml.py` 读的是磁盘上的 `surefire-reports/TEST-*.xml`，
  于是读到**上一次成功运行（r087）遗留的 XML**，报告出一个**从未发生过的 589**。
- 判据：该次运行里 `Tests run: 7902` 出现 **0 次**；正常运行恰好 **1 次**。

**真实状态**：`Tests run: 7902, Failures: 340, Errors: 279, Skipped: 18` = **619**
（起点 855，**−236**）。Round 54 的两条改动**都是中性的**。

**扩展数据簇（34 条）仍未解决，且断点已变**：
`invalidExtensionPropertyFail` 现在报 `No exception thrown`（而非签名错误），
`extensionFieldRoundTrip` 报 `Assert.notNull ... Value was null`，
`verify_ProceduralLinq1` 报 `The sequence did not contain any elements`。
**说明真正的断点还没找到**，下一步从这里查，**不要沿用 Round 54 的结论**。

**本轮同时否证的**：给元素查找装上 owner 自己的实参作为缓存键
（`pushOwnerArgumentsForKey`）——实测**把 34 条扩展数据失败全部弄回来**（619）。
该 helper 已从 compat 删除；**Round 41 关于"creation gap"的结论仍然成立**。

**新增两条测量纪律（已入计划 §8.3）**：

1. **测量前先确认那次运行真的跑了测试**：构建在测试模块之前失败时，
   `surefire-reports` 是**上次的 XML**，会产出从未发生的失败数。
   判据：日志里 `Tests run: 7902` **恰好 1 次**，且无 `mvn <args> -rf :<module>`。
2. **改补丁要连转换器一起重编**（`dotnet build`），否则再生用的还是旧补丁。

**本轮无净收益**（619 → 619）。**本轮最大的产出是纠正了一条被写进交接文档的错误结论**——
如果不查，后续会话会沿着"−30 已修"的假前提继续做。
### 4.58 Round 56：扩展数据签名检查问错了问题（619 → 619，语义修复）

**真实基线确认**：`Tests run: 7902, Failures: 341, Errors: 278, Skipped: 18` = **619**
（起点 855，**−236**）。本轮**无净收益**，但把该簇的断点收窄了一半。

**探针（`probe/w42/ExtSigProbe.java`）给出两个精确事实**：

| 成员 | 声明类型（`getDeclaredPropertyType`） | `isAssignableFrom(CSharpGenericIDictionary, …)` |
|---|---|---|
| `ClassWithExtensionPropertyAsImmutable.MyOverflow` | `ImmutableDictionary<String,Object>` | **false**（但 `isImmutableDictionaryType` 为 **true**） |
| `ClassWithExtensionData<T>.Overflow` | `Object`（`T` 被擦除） | false |

**已修的一半**：C# 从 .NET 5 起接受 `IReadOnlyDictionary<string,T>`，
而不可变字典正是实现它的 —— 生成的检查只问了 `CSharpGenericIDictionary`，
于是把一个 SDK **明确文档并测试**的形式拒了。新增
`TypeHelper.isExtensionDataDictionaryType`（`IDictionary` / `IReadOnlyDictionary` /
`IImmutableDictionary` 任一即可），签名拒绝从 **14 条降到 12 条**。

**仍未修的一半**：残留 12 条全是 `ClassWithExtensionData<T>.Overflow`。
探针显示**声明类型是 `Object`，而 `getRuntimePropertyType()` 也没能提供构造后的字典类型**，
所以检查的两边都是 false。**这是一个"运行时类型从哪来"的测量问题，不是判断问题**——
下一步应当打印 `JsonPropertyInfo.initialize` 收到的 `runtimePropertyType` 实参，
以及 `JsonClassInfo.getProperty`（第 441 行那条 `determineConverter` 通路）是否被走到，
而不是继续改判据。

**验证**：本轮运行已按 §8.3 新纪律确认完整（`Tests run: 7902` 恰好出现 1 次）；
逐用例集合差 = 修 0 / 坏 0。**该改动保留**，因为它是 C# 的忠实语义。
### 4.59 Round 57：`T` 形参被全链路擦除（619 → 619，把线索收窄到"一个已知可得的实参"）

**按 §8.2.12 的处方测量，得到决定性结论**：`ClassWithExtensionData<T>.Overflow`
的运行时类型链路**每一环都是 `Object`**，下游无一处能恢复：

```
[cs2j.r] addProperty Overflow memberType=class java.lang.Object
         parent=...ClassWithExtensionData runtimeType=class java.lang.Object
         converter=ObjectConverter convTypeToConvert=class java.lang.Object
         convRuntimeType=class java.lang.Object
```

- `getRuntimePropertyType()` 是 `Object` → §8.2.12 里"改用运行时类型"的思路**对这 12 条无效**。
- 转换器是 `ObjectConverter`，`TypeToConvert` / `RuntimeType` 也都是 `Object`。
**所以这是纯粹的泛型形参擦除，不是判断错误** —— Round 56 的判断"不要再改判据"是对的。

**但线索收窄到一个已知可得的量**：owner 的实参**是存在的**——

```
getGenericArguments(ClassWithExtensionData) length = 1
```

问题只在**时机**：`__cs2jOwnerTypeArguments` 在 `JsonClassInfo` **构造时**捕获，
而那一刻注册表里还没有这个实参（所以是空的），属性随后才被创建。

**下一步（有依据，但要先探针确认时机）**：
在属性创建路径上，当成员的声明类型是 `Object`、且 owner 类信息能提供恰好 1 个实参时，
用该实参作为运行时类型。**动手前必须先测出**：
属性创建的那一刻，`currentGenericArgumentsLayer(owner)` / `getGenericArguments(owner)`
是否真的返回实参（上面那次调用是在反序列化之后，可能已经晚于属性创建）。
**若属性创建时确实拿不到，这条在 compat 侧就无解**，只能走转换器侧
（把 `T` 的实参在成员级显式传下去）——那是一个大得多的改动。

**验证**：本轮运行已确认完整（`Tests run: 7902` 恰好 1 次）；
`r094 → r095` 逐用例集合**完全相同**（619）；本轮无代码改动（插桩已按"重新生成"清除）。
### 4.60 Round 58：§8.2.13 的探针**否证了 compat 侧修复**（616 → 619，无代码改动）

**探针输出（属性创建那一刻）**：

```
[cs2j.t] AT-PROPERTY-CREATION Overflow owner=...ClassWithExtensionData
         ownerLayer=null  ownerGenericArgs=[class java.lang.Object]  memberType=class java.lang.Object
```

**结论：构造后的实参在属性创建时并不存在**，而不是"存在但没被用上"。
owner 的实参层是 `null`，`getGenericArguments(owner)` 只给出擦除后的 `Object`。

**因此这 12 条在 compat 侧无解**（§8.2.13 预设的两个分支里落到了第二个）。
唯一出路是**转换器侧**把 `T` 的实参显式传到成员级——那是一个大改动，
**按 §8.2.13 的约定就此收手，不再在扩展数据簇投入**。

**该簇三轮（R56–R58）的净收支**：
- 收益：0 条；
- 产出的可用知识：判断错误 vs 信息缺失被区分开（R56）、
  运行时类型链路全擦除（R57）、构造实参在属性创建时不存在（R58）；
- 已做的忠实语义修复：`isExtensionDataDictionaryType`（保留，中性但正确）。

**教训**：一条"看起来只是时机问题"的线索，**用一次探针就能定论**——
R57+R58 两轮里真正的进展都来自探针，而不是来自对判据的推敲。
**下一次遇到"信息应该存在"的假设，先写探针，并且把探针打在正确的时刻上**
（本轮的关键是打在**属性创建**而不是**反序列化之后**）。

**验证**：`Tests run: 7902, Failures: 341, Errors: 278, Skipped: 18` = **619**；
运行完整（`Tests run: 7902` 恰好 1 次）；`r095 → r096` 逐用例集合**完全相同**；
插桩全部清除（生成树里 `cs2j.t|r|e` 计数为 0）。
### 4.61 Round 59：**struct 的 clone() 会丢掉 final 字段**（619 → 580，修 40 / 坏 1）

提交 `a62aac37`。**按 §8.2.14 的处方探针，结果推翻了那条线索的定性**——
22 条 `ArrayIndexOutOfBoundsException` **不是库里的崩溃**，而是**测试侧**的：
`ClassWithComplexObjects.validateArray` 里，一个已解析的 JSON 数组**枚举出 0 个元素**。

**根因**：`StructTransformer` 生成的"逐字段 clone()"**跳过所有 final 字段**
（因为 final 字段不能构造后再赋值）。对 `ArrayEnumerator` 来说，
`_target` 与 `_endIdxOrVersion` 就这样被丢掉，于是 clone 出来的是
`_target == null`、`_endIdxOrVersion == 0` 的枚举器，`moveNext()` 立刻返回 false，
数组什么也枚举不出来。

**扫描发现 16 个生成文件**都有"逐字段 clone() 丢 final 字段"：
`JsonDocument.MetadataDb`、`JsonDocument.StackRowStack`、`ArrayEnumerator`、
`ObjectEnumerator` 以及多个测试用 struct。

**修法**：当 struct 有 final 实例字段且没有可用的构造器时，
**生成一个私有全字段构造器**，让 `clone()` 调它。
（直接"构造后再赋值 final"**不能编译**：`无法为 final 变量 _target 分配值` ——
这一步是先被编译器挡下来才换的方案，不是猜的。）

**验证**：`Tests run: 7902, Failures: 331, Errors: 249, Skipped: 18` = **580**
（起点 855，**−275**）；逐用例集合差 = **修 40 / 坏 1**；运行完整（汇总行恰好 1 次）。

**已披露的 1 条回归**：`PropertyVisibilityTests.valueType_Properties_NotIgnoredWhen_
IgnoreNullValues_Active_ClassTest`。**它不是被误伤，而是一个"因错误原因通过"的测试**：
clone 丢字段时，`ClassWithValueAndReferenceTypes` 序列化出的输出**恰好**匹配；
clone 修正后真实输出是 `{"MyBool":null,"MyPointClass":null}`，
**暴露出两个真实的序列化器缺陷**：
(a) 字段被序列化成 `null` 而不是它的初始化值（`myInt` 应为 -1、`myPointStruct` 应为 (1,2)）；
(b) `IgnoreNullValues` 对 null 引用成员**没有生效**。
**决定保留**（40 > 1，且 clone 行为无疑是对的），
**这两个被暴露的缺陷比那条偶然通过更值得修**——记为下一批目标。
### 4.62 Round 60：两个假设**都被证伪**（580 → 580，无净收益）

Round 59 从回归里暴露的两个"待修缺陷"，本轮逐条查证，**两条都不成立**：

**假设 1（证伪）：字段被序列化成 `null` 而非初始化值。**
用独立探针（`probe/w42/InitProbe.java`）测一个最小类：

```
field values before serialize: s=Default i=-1 b=true
default options      : {"S":"Default","I":-1,"B":true}
```

**字段初始化器是正确序列化的**。之前观察到的 `myInt`/`myPointStruct` 未出现，
不是"初始化器丢失"，而应与 `IgnoreNullValues` 或该成员的其它状态有关。

**假设 2（证伪）：`IgnoreDefaultValuesOnWrite` 从未被检查。**
grep `getIgnoreDefaultValuesOnWrite` 只看到声明与赋值，据此推断"写路径没有这个守卫"。
**这个推断错了**：守卫就在 `JsonPropertyInfo1.getMemberAndWriteJson` 里
（`if (getIgnoreDefaultValuesOnWrite() && …) return true;`），
`getPolicies(...)` 也确实在 `initialize` 末尾被调用
（我最初 grep 的是 `.getPolicies(`，而生成的是**裸调用** `getPolicies(`，所以漏了）。

**教训（本轮唯一产出）**：**grep 的零结果是弱证据**——
"没搜到调用点"可能只是**模式写错了**（`.getPolicies(` vs `getPolicies(`）。
下判断前应当用**两种形状**各搜一次，或直接读生成代码的那一段。

**本轮附带产出**：`PatchRemoveStrayDiagnostics`（提交 `132dd454`）——
探针残留现在**对所有生成文件统一清除**，不再依赖"记得删"。

**验证**：`Tests run: 7902, Failures: 331, Errors: 249, Skipped: 18` = **580**；
运行完整（汇总行恰好 1 次）；`r098 → r099` 逐用例集合**完全相同**。

**下一步**：`IgnoreNullValues` 的真实断点仍未找到。已知：
守卫存在、`getPolicies` 被调用、最小复现里 `b=null` 仍被写出。
应当**直接打印该成员在写出那一刻的 `ignoreDefaultValuesOnWrite` 值**，
而不是继续从代码形状推断。
### 4.63 Round 61：`IgnoreNullValues` 未生效——按**声明类型**判断 `default(T)` 是否为 null（580 → 569）

提交 `387edb77`（compat）+ 持久化补丁。**这是先探针后动手的又一次直接收益**：
按 Round 60 定下的处方打印了守卫的输入，一次就定位：

```
[cs2j.w] member=B ignoreDefOnWrite=true defaultT=false value=null handleNullOnWrite=false
```

**`IgnoreDefaultValuesOnWrite` 其实是 true**——Round 60 的"守卫没生效"猜测也不对。
真正的断点是：守卫走了**值类型分支**，因为 `_cs2jDefault_T()` 对装箱的 `Boolean` 返回
**`false` 而不是 null**，于是 `false.equals(null)` 为 false，null 成员照样被写出。

**C# 的判据是 `Converter.CanBeNull`**（`JsonPropertyInfoOfT` 把它作为 `defaultValueIsNull` 传入），
而生成代码是从**Java 的默认值**反推的——而 Java 默认值**无法区分"装箱的值类型"与"可空类型"**。

**修法**：
- `TypeHelper.defaultValueIsNullForWrite`：按**声明的类型**判断（primitive Class 常量 = C# 值类型），
  等价于 `Converter.CanBeNull`；
- `TypeHelper.shouldIgnoreForDefaultWrite`：承载整个判断，装箱数值按数值比较；
- `DefaultValue.getDefaultByClass`：对**包装类**返回 null（转换模型里包装类代表
  C# 的可空/引用类型），primitive Class 常量保持零值。

**验证**：运行完整（汇总行恰好 1 次）；**569**；逐用例集合差 = **修 13 / 坏 2**；
并把 Round 59 披露的那条回归**修好了**（`valueType_Properties_NotIgnoredWhen_IgnoreNullValues_Active_ClassTest` 通过），
另收获 4 条 nullable、2 条 `DictionaryTests` 的 IgnoreNullValues、`ValueTests.readPrimitives`。**净 +11。**

**已披露的 2 条回归**（都是值类型默认值，净仍为 +11）：

1. `PropertyVisibilityTests.ignoreCondition_WhenWritingDefault_Globally_Works`：
   期望 `{}`，实际 `{"MyInt":0}` —— 这条路径需要 `default(int)` **仍然按 0 比较**。
2. `CustomConverterTests.valueTypeConverterForNullWithArray`：
   `ArrayConverter.convertCollection` → `TypeHelper.listToArray` 抛 `IllegalArgumentException`。

**一条过程记录**：中途用"去掉未用 import"的中间版本测出**完全相同的 569/13/2**，
说明这 2 条回归**不是 helper 重构造成的**，而是本次语义改动的边界。
保留的是更清晰的那一版。

**下一步**：上面第 1 条是**有明确线索**的——`WhenWritingDefault` 需要
`default(int)` 按 0 比较，而 `IgnoreNullValues` 需要引用类型按 null 比较；
两者共享同一个守卫，需要按**成员的 C# 类型种类**再细分一次。
### 4.64 Round 62：`default(T) == null` 只能由**声明成员**回答（569 → 568）

**上一条的"下一步"被证实**，但证明了 Round 61 的"按声明类型"判断不够细。

**探针 `DefProbe5`（直接调 compat 重载，不经过序列化器）**：

| 成员声明 | `getPropertyType()`（装箱） | `getRawPropertyType()` | `defaultValueIsNullForWrite` |
|---|---|---|---|
| `int getMyInt()` | `Integer` | `int` | false |
| `Integer getMyNullableInt()` | `Integer` | `Integer` | true |
| `boolean getMyBool()` | `Boolean` | `boolean` | false |
| `Boolean getMyNullableBool()` | `Boolean` | `Boolean` | true |
| `V getMyStruct()`（`value class`） | `V` | `V` | false |
| `String getMyString()` | `String` | `String` | true |

**关键发现（都与直觉相反，必须记住）**：
- `java.lang.Integer.class.isValue()` 在 JDK 28 上是 **true**（包装类也是 value class），
  所以 `isValue()` **不能**单独用作"C# 值类型"的判据，包装类必须显式排除；
- `java.time.LocalDate/Instant/Duration`、`java.util.Optional`、`java.lang.Number`
  也都是 value class —— 判据必须保守（我把它写成"原始类型，或 isValue 且非包装类"，
  其余一律退回原判断）；
- `String` **不是** value class，所以 `string` 仍然正确地答 "default 是 null"。

**探针 `DefProbe3`**：`getConverter(int.class)` 得到 `ObjectDefaultConverter`，
而 `getConverter(Integer.class)` 得到 `Int32Converter` ——
**内置转换器表是按包装类建的**。所以"让 `PropertyInfo.getPropertyType()` 不再装箱"
这条更"忠实"的路代价极高（`TypeHelper.isSameType/isAssignableFrom` 全都要做原始类型↔包装类
归一），**已放弃**，改走"两处判断点都传成员"的窄路。

**实测**：`569 → 568`，修 1（`ignoreCondition_WhenWritingDefault_Globally_Works`）/ 坏 0。
随后把调试打印删掉再测一遍得 `568 → 568`（0/0），确认**测量状态与提交状态一致**。

### 4.65 Round 63：`[JsonIgnore(Condition = …)]` 的 Condition 从未离开转换器（568 → 558）

**入口**：Round 62 修完后，`WhenWritingDefault` 簇只剩 7 条。查其中 4 条
`…WinsOver_IgnoreReadOnly*`（期望 `{"MyString":"Hello"}` 实得 `{}`）时发现：
**`Never` 和 `WhenWritingDefault` 的 4 条一起失败**，说明与 `WhenWritingDefault` 无关，
而是"显式 `[JsonIgnore]` 应该压过只读策略"这条规则没生效。

**根因链（三步都实测过）**：
1. 生成物里只有裸 `@CompatJsonIgnore`（`PropertyVisibilityTests.java` 已确认）；
2. 生成的 `JsonIgnoreAttribute` **只有无参构造器**，字段默认 `JsonIgnoreCondition.Always`
   （C# 的 `Condition = …` 是对象初始化器，转换器不生成）；
3. 于是 `addProperty` 里 `ignoreCondition == Always` → `createIgnoredPropertyPlaceholder`
   → 成员被**整个丢掉**。`{}` 就是这么来的。

**修法**：转换器读 `NamedArguments["Condition"]` 发 `@CompatJsonIgnore(<n>)`；
compat 反射 `getCondition()` 的返回类型，用 `fromValue(int)` 取值（**按值不按序号**：
生成枚举带 `_UNMAPPED` 且显式带 C# 数值），再 `setCondition` 装回。

**一次假测量（自己踩到 §8.3 的纪律）**：首版把注解元素命名为 `condition()`，
而生成代码写的是单元素简写 `@CompatJsonIgnore(2)` → javac 报
`找不到符号: 方法 value()` → `system-text-json-tests` 编译失败 → 测试阶段没跑 →
`surefire-reports` 被 `clean` 清空 → triage 报 `568 → 0`（"修了 568 条"）。
**判据是 `summary lines: 0`**（正常必须恰好 1）。改名 `value()` 后重测。

**实测**：`568 → 558`，修 11 / 坏 1。

### 4.66 Round 64：`CSharpDictionary` 不遵守 comparer（558 → 554，含回收上面那条回归）

Round 63 的回归 `jsonIgnoreCondition_LastOneWins` 期望
`{"MyString":"Random","MYSTRING":null}` 反序列化后 `MyString == null`。

**探针 `DefProbe6`**（最小类 + `IgnoreNullValues=true` + `PropertyNameCaseInsensitive=true`）：
- 带 `@CompatJsonIgnore(0)` 的类：`ignoreOnRead=false`（**正确**）；
- 不带注解的类：`ignoreOnRead=true`（**正确**）；
- **两者都得到 `"Random"`** —— 说明"后一个覆盖前一个"根本没发生，与 `Ignore` 无关；
- `{"MyString":"Random","MyString":null}`（**同大小写**）得到 `null` ✓；
- `{"MYSTRING":"Case"}`（**只有大小写变体**）得到 `null` ✗ —— 大小写不敏感匹配**完全不工作**；
- 该测试此前只是"因为属性被整个丢掉"而通过（错误原因通过）。

**探针 `DefProbe7`** 定位到 compat：
```
CSharpDictionary(StringComparer.getOrdinalIgnoreCase()) 里 put("MyString")
  containsKey("MYSTRING") = false   ← comparer 被存了却没用
```

**修法**：`storedKey(key)`：有 comparer 时按 comparer 扫描并把**已存的那个键**返回
（C# 语义：替换值不改键的拼写）；`containsKey/get/getChecked/put/remove/tryGetValue/
tryAdd/add/contains/getOrDefault` 都改走它。无 comparer 时行为与原来一致
（`map.containsKey`）。

**实测**：`558 → 554`，修 4 / 坏 0，含回收的 `jsonIgnoreCondition_LastOneWins`。
**另证**：`honorCaseInsensitivity` 修前修后都在失败集里 → 本回归是**暴露既有缺陷**，
不是 Round 63 引入的新缺陷（`DefProbe6` 里无注解的类同样失败，是该结论的直接证据）。

### 4.67 Round 65：64 条 `JsonConvert CSharpList` 的根因是**类信息缓存被污染**（已定位，未修）

**探针 `DefProbe8`**（最小复现 `CSharpList<CSharpList<Integer>>`，JSON `[[10,11],[20,21]]`）：

```
属性上的 converter        : ListOfTConverter / typeToConvert=CSharpList / elementType=CSharpList  ✓
getOrAddClass(CSharpList.class): elementType=java.lang.Integer / ListOfTConverter/Integer          ✗ 污染
根因栈: Int32Converter.read ← IEnumerableDefaultConverter.onTryRead:88
CAUSE: IllegalStateException: Cannot get the value of a token type 'StartArray' as a number.
```

`JsonClassInfo` 第 176 行 `setElementType(converter.getElementType())`：裸 `CSharpList.class`
的类信息是**在最内层实参上下文（`Integer`）里**建出来的，之后所有需要
"outer list 的元素类信息"的路径都拿到它 → 元素转换器成了 `Int32Converter`。

**为什么 64 条全中**：它们的路径都是 `$.…MyInt16TwoDimensionList[0]`
（`SimpleTestClass.MyInt16TwoDimensionList` = C# `List<List<int>>`，`int[][]` 那半是好的）。

**这是本轮最大的单个机会（−64）**，形状也明确：给类信息缓存加实参上下文维度。
**尚未动手的原因**：它属于 P0-1（泛型实参擦除）簇，改动面在 `JsonClassInfo`/ThreadLocal 实参
管道上，需要独立一轮"改一次、测一次"；本轮上下文已接近上限，故只留可复现的探针与结论。
**注意不要原样重试** `PatchConverterCacheLookup`（转换器缓存，§6.2 已否证）——
本条是**类信息**缓存。

### 4.68 Round 66：`default(JsonElement)` 的 struct 复制走了 C# `Clone()`（554 → 553）

**入口**：桶 `IllegalStateException (no message)`（8 条）的栈就是全部证据：

```
JsonElement.checkValidInstance ← JsonElement.clone ← $JsonElementClass.setNull
  ← MethodInfo.invoke ← ReflectionMemberAccessor.lambda$createPropertySetter$0
  ← JsonPropertyInfo1.readJsonAndSetMember
```

**根因链**：
1. C# `JsonElement` 是 struct，`obj.Null = default` 是**逐位复制**，不做校验；
2. 转换器把 struct 复制模拟成 `.clone()`（`StructCloneHelper` / `PropertyTransformer` 等插入点）；
3. 但 `JsonElement` **自带 `Clone()`**，其内部 `CheckValidInstance()` 对默认实例抛
   `IllegalStateException`；`StructTransformer.AddCloneMethod` 的
   `// Has final fields … keep original clone()` 分支（第 852 行）恰好让 `JsonElement`
   保留了这个 C# `Clone()`，于是**复制继承了 API 的校验**；
4. JSON 里的 `null` 让读取器造出 `default(JsonElement)`，赋值时在生成的 setter 里炸掉。

**修法**：新增 `ProjectPasses.PatchJsonElementCloneOfDefaultInstance`，只改
`public JsonElement clone()` 的开头：

```java
if (_parent == null) {
    // default(JsonElement): C# copies the value, it does not validate it.
    return this;
}
checkValidInstance();
```

其余成员一律不变，真实 API 仍然拒绝无效实例（grep 全部 C# 测试：**没有任何**
`.Clone()` + `Throws`/`default` 的断言，所以放宽这一点不会与测试预言机冲突）。

**实测**：`554 → 553`，集合差 **修 1 / 坏 0**：
```
FIXED JsonDocumentTests#defaultObjectEnumeratorDoesNotThrow
```
`Tests run: 7902` 恰好 1 次，failures 307 / errors 246。

**附带的重要观察**：这个桶里另外 **6 条换了失败形状**（不是"修好"，集合差把它们
如实算作仍失败）：

| 用例 | 修前 | 修后 |
|---|---|---|
| `JsonElementTests#serializeJsonElement` | `IllegalStateException` | `Expect Null -> Undefined` |
| `ObjectTests#write(ITestClass)[37]` | 同上 | 同上 |
| `ReferenceHandlerTests#readTestClassesWithExtensionOption(Class,byte[])[37]` | 同上 | 同上 |
| `SpanTests#read(Class,byte[])[37]` | 同上 | 同上 |
| `SpanTests#readFromStream(Class,byte[])[37]` | 同上 | 同上 |
| `ReadValueTests#optionsFollowToConverter` / `WriteValueTests#optionsFollowToConverter` | 同上 | 仍在 `IllegalStateException`（改成 `getArrayLength`/`writeTo` 入口） |

`Expect Null -> Undefined` 的意思是：`{"Null":null}` 反序列化后
`JsonElement.getValueKind()` 应为 `Null` 却是 `Undefined` ——
即 null token 落到了 `_cs2jDefault_T()` 而没有让转换器去读
（`JsonPropertyInfo1` 第 178 行：`isNullToken && !HandleNullOnRead && !IsContinuation`
时直接 `getSet().accept(obj, _cs2jDefault_T())`）。**这是可接的下一步**（该桶 5 条）。

### 4.69 Round 67：`JsonElementConverter` 的 `CanBeNull` 答反了（553 → 548，修 5 / 坏 0）

**入口**：§4.68 里那 6 条"换了断言"的用例，桶名 `Expect Null -> Undefined`。

**根因链（全部由生成代码读出，未加插桩）**：
1. `JsonConverter1.getHandleNull()` 第 214 行：`setHandleNullOnRead(!getCanBeNull());`
2. `CanBeNull` 在 ctor 第 68 行：`!getIsValueType() || TypeHelper.getUnderlyingType(getTypeToConvert()) != null`，
   而 `getIsValueType()` = `TypeHelper.getIsValueType(typeToConvert)` = **`type.isPrimitive()`**；
3. `JsonElement.class` 不是 primitive → `getIsValueType()=false` → `canBeNull=true`
   → `HandleNullOnRead=false`；
4. `JsonPropertyInfo1` 第 178 行：`isNullToken && !HandleNullOnRead && !IsContinuation`
   →（`!IgnoreDefaultValuesOnRead`）→ `getSet().accept(obj, _cs2jDefault_T())`
   → 赋的是 `new JsonElement()`，kind = **Undefined**；
5. C# 侧 `typeof(JsonElement).IsValueType` 是 **true** → `CanBeNull=false`
   → `HandleNullOnRead=true` → 转换器自己读 null token →
   `JsonDocument.ParseValue` 的根元素 kind = **Null**。

**修法**：`ProjectPasses.PatchJsonElementConverterHandleNull` 在 `JsonElementConverter`
末尾追加 `@Override public boolean getCanBeNull() { return false; }`。

**为什么不用 setter**：`setHandleNullOnRead` 在生成代码里是 **private**
（`JsonConverter1` 第 228 行），第一版补丁直接调它 → javac
`找不到符号: 方法 setHandleNullOnRead(boolean)` → **整棵树编译失败** → 测试阶段没跑
（`summary lines: 0`）→ triage 报出 `553 → 553 / 0 fixed`。
**这正是 §8.3 那条纪律存在的意义**：如果只看"0 收益"就会把一次编译失败当成"方向无效"而回滚。
改成 `getCanBeNull()` 覆写后 `summary lines: 1`，结果如下。

**实测**：
```
553 -> 548  net 5
FIXED 5 REGRESSED 0
  JsonElementTests#serializeJsonElement
  ObjectTests#write(ITestClass)[37]
  ReferenceHandlerTests#readTestClassesWithExtensionOption(Class, byte[])[37]
  SpanTests#read(Class, byte[])[37]
  SpanTests#readFromStream(Class, byte[])[37]
```
`Tests run: 7902, Failures: 302, Errors: 246, Skipped: 18`，`Tests run: 7902` 恰好 1 次。

**母题归纳（本轮最有复用价值的结论）**：**"这个 Java 类型对应的 C# 类型是值类型吗"**
这个问题在 compat 里被答错了三次，各自造成一个独立簇：

| 入口 | 现状 | 已修？ |
|---|---|---|
| `DefaultValue.getDefaultByClass` / `defaultValueIsNullForWrite` | `int` 与 `int?` 都装箱成 `Integer` | 已修（§4.64，需**声明成员**） |
| `TypeHelper.getIsValueType(Class)` | 仍是 `type.isPrimitive()`，对转换出的 struct 答 false | **未修** |
| `JsonConverter1.CanBeNull`（依赖上一条） | `JsonElement` 等 struct 答 true | 已按类型绕过（本条） |

下一条要攻击时，先查这三个入口里"还没修的那个"。

### 4.70 Round 68：C# 值类型母题的第三次（一次失败实验 + 一次成功，548 → 545）

**背景**：`TypeHelper.getIsValueType(Class)` 只答 `type.isPrimitive()`，而转换出来的 C# struct
是 Java `value class`。实测树里有 **24 个 value class**（`JsonEncodedText`、`JsonProperty`、
`JsonReaderState`、`ReadStackFrame`、`PropertyRef`、`ParameterRef`、`BitStack`、
`Point_2D_Struct`、`Struct_With_Ctor_With_64_Params` …）。

**实验 A：改通用谓词（已回滚）**

```java
return type.isPrimitive() || (type.isValue() && !isBoxedWrapperClass(type));
```
（`!isBoxedWrapperClass` 是必须的：JDK 28 里 `Integer.class.isValue()` 为 **true**，
但这里它代表 `int?`。）

实测 `548 → 547`，**修 5 / 坏 4**：

```
FIXED     CustomConverterTests#complexValueTypeConverter_NoOverride / _NullOptIn / _OptOut
FIXED     CustomConverterTests#assignmentToValueTypedMemberInterface / assignmentToValueTypedMemberObject
REGRESSED CustomConverterTests#assignmentToNullableValueTypedMemberInterface / ...Object
REGRESSED CustomConverterTests#nullableValueTypedMemberWithNullsToInterfaceConverter / ...ObjectConverter
```

坏掉的 4 条消息一致：
`Unable to assign 'null' to the property or field of type 'ValueTypedMember'`
← `ThrowHelper.throwInvalidOperationException_DeserializeUnableToAssignNull:568`
← `JsonPropertyInfo1.readJsonAndSetMember:215`。

**为什么分不出来**（实测生成物对照）：

| C# 声明 | 生成 Java 成员 |
|---|---|
| `ValueTypedMember MyValueTypedProperty { get; set; }` | `getMyValueTypedProperty() { return myValueTypedProperty.clone(); }`，setter 也 `.clone()` |
| `ValueTypedMember? MyValueTypedProperty { get; set; }` | `getMyValueTypedProperty() { return myValueTypedProperty; }`，setter 不 clone |

两者的**字段/方法签名完全相同**（都是 `ValueTypedMember`），差别只在编译期插入的 `.clone()`，
运行期拿不到。于是 `JsonPropertyInfo1:214`
（C# `declaredPropertyType.IsValueType && Nullable.GetUnderlyingType(...) == null`）
把可空 struct 也当成非空值类型，null token 直接抛异常。

**净 +1 但引入 4 条新缺陷 → 回滚。**

**实验 B：同样的判断只加在转换器入口（KEEP，`f094d466`）**

新增 `TypeHelper.getIsValueTypeForConverter(Class)`（含 value class 判断），
由 pass 把 `JsonConverter1` 构造器里的
`this.canBeNull = !getIsValueType() || TypeHelper.getUnderlyingType(...) != null;`
改成 `!io.github.ningpp.compat.TypeHelper.getIsValueTypeForConverter(getTypeToConvert()) || ...`。
只影响 `CanBeNull`（进而 `HandleNullOnRead`），**不碰 `JsonPropertyInfo1` 的赋值校验**。

实测 `548 → 545`，**修 3 / 坏 0**，正是实验 A 中属于转换器那一半的 3 条
（`complexValueTypeConverter_NoOverride/_NullOptIn/_OptOut`）。

**结论**：同一个事实在不同入口的代价不同。通用谓词同时是"能否赋 null"的闸门，
而 Java 的 `Class` 分不出 `T` 与 `T?`；**只在需要它的窄入口上引入判断**，
就能拿收益而不触发歧义。手法与 §4.64（`defaultValueIsNullForWrite` 先窄化到声明成员）相同。

**该母题的三个入口现状**（本项目里"C# 值类型"一共答错过三次）：

| 入口 | 状态 |
|---|---|
| `defaultValueIsNullForWrite` / `DefaultValue` | 已修（按**声明成员**，§4.64） |
| `TypeHelper.getIsValueType(Class)` | **仍答 `isPrimitive()`** —— 实验 A 证明直接改会坏 4 条 |
| `JsonConverter1.CanBeNull` | 已修（走 `getIsValueTypeForConverter`，本条） |

### 4.71 Round 69：参数异常的消息里没有参数名（545 → 539，修 6 / 坏 0）

**入口**：`Assert.contains` 桶里 6 条同形失败（实测原文）：

```
Expected substring: 'json'      Actual string: io.github.ningpp.compat.ArgumentNullException
Expected substring: 'options'   Actual string: io.github.ningpp.compat.ArgumentNullException
Expected substring: 'returnType' / 'inputType' / 'writer' / 'value'  （同形）
```

**根因**：`ArgumentNullException(String paramName)` 走 `super((String) null, paramName)` ——
`paramName` 存进了字段，**消息是 null**，所以 `Throwable.toString()` 只有类名。
生成测试断言的是 `Assert.Contains("json", ex.toString())`（C# 原文也是 `ex.ToString()`）。

C# 的规则：
- `Message = message + " (Parameter 'name')"`（有参数名时总是追加）；
- 没给 message 时用默认文案：`ArgumentNullException` → `"Value cannot be null."`，
  `ArgumentOutOfRangeException` → `"Specified argument was out of the range of valid values."`。

**修法**：`ArgumentException` 新增 `protected static String paramSuffix(String)`，
三个异常类的"只有 paramName"构造器补上默认文案 + 后缀。

**实测** `545 → 539`，**修 6 / 坏 0**：
`ReadValueTests#nullJsonThrows`、`ReadValueTests#nullReturnTypeThrows`、
`WriteValueTests#nullInputTypeThrows`、`WriteValueTests#nullWriterThrows`、
`OptionsTests#copyConstructor_NullInput`、`NumberHandlingTests#jsonNumberHandling_ArgOutOfRangeFail`。

**与既有结论的关系**：这是"异常/格式化**文本**"通道的又一例
（同族：§4.34 `ThrowHelper` 渲染 C# 泛型名、§8.2.16 `csFormat` 的数字格式）。
**凡是断言消息文本的失败，先查 compat 里对应的异常/格式化类。**

### 4.72 Round 70：闭键类信息的"两半一起做"仍然失败（539 → 576，−37，已回滚）

**动机**：§4.67 定位到 64 条 `JsonConvert CSharpList` 的根因是**类信息缓存被污染**
（裸 `CSharpList.class` 的 `JsonClassInfo` 是在最内层实参上下文里建出来的）。
§8.2.20 写的下一步是"给类信息缓存补实参维度"。本轮把这一步真正做了，**并且补齐了
round 41 缺失的第 2 半**。

**实现（两半）**

1. 成员侧（compat）：`cachedClassInfoForMember` 原本只是**命中查询**；改成未命中即
   **创建** —— 反射 `new JsonClassInfo(rawType, options)`，创建期间把该成员自己的实参
   装进 `MEMBER_ARGUMENTS`，实例存入 compat 的 per-options 旁路表（键
   `ClosedGenericKey(raw, args)`），**不去污染**生成的 `_classes`。
2. 元素侧（pass 改写 `JsonClassInfo.getElementClassInfo()`）：
   `getOptions().getOrAddClass(__cs2jElement)`
   → 先问 `TypeHelper.getOrAddElementClassInfo(getOptions(), __cs2jElement)`，
   命中闭键就用它，否则保持原样（与 `getRuntimeClassInfo()` 里已有的钩子写法一致）。

**测量 1**：`539 → 576`，**修 1 / 坏 38**；`Tests run: 7902` 恰好 1 次。
回归样本表明"错的类型被读了进来"：

```
CustomConverterTests#customDerivedTypeConverter
  class dotnet.text.json.JsonElement cannot be cast to class java.lang.Integer
ExtensionDataTests#nestedClassWithJsonElementExtensionDataProperty
  class dotnet.text.json.JsonElement cannot be cast to ...ChildClassWithJsonElement
ArrayTests#classWithMixedSettersIsParsed[1..2]  Expected: 1 / Actual: 1（类型不同）
ObjectTests#write[9,10,24,28,29,30,31,32] / SpanTests#read[...] / ReferenceHandlerTests#readTestClasses...
```

**判定实验（守卫）**：怀疑元素侧对"非同类嵌套"也生效 ——
`MEMBER_ARGUMENTS` 里本来就有每个成员发布过的实参，所以"表里有这个元素类型的条目"
**不等于**这是一次 `List<List<T>>` 嵌套。于是加 `ELEMENT_SCOPE_DEPTH`，
只在 `beginElementArgumentsScope` 真正生效（`ownerType == elementType`）时才回答元素侧查询。

**测量 2**：`539 → 576`，**修 1 / 坏 38，回归清单与测量 1 逐条一致**。

**结论**：代价**全部来自成员侧创建**。也就是说 round 41 的 +19 不是"缺少第 2 半"造成的，
而是"让同一个擦除类拥有第二份 `JsonClassInfo`"本身的代价：`getRuntimeClassInfo()` 交出去的帧
会换成另一份 `PropertyCache` / `_elementClassInfo`，其影响面远大于收益。
**该方向正式关闭**（两次独立测量：round 41 +19、round 70 +37）。

**已回滚**：`git checkout` 两个文件 → 重新生成 → 重新全量测量得
`539 → 539，修 0 / 坏 0`，确认基线完好（这也再次验证了"回滚也必须重新生成后再测"这条纪律）。

### 4.73 Round 70：可空 struct 与不可空 struct 在运行期**完全不可区分**（探针实测）

Round 68 的实验 A 只凭生成物**推断**"运行期分不出 `T` 与 `T?`"。本轮用 `DefProbe10`
直接读运行期对象：

```
== TestClassWithValueTypedMember
   MyValueTypedProperty declared=ValueTypedMember propertyTypeCanBeNull=true
        converter=ObjectDefaultConverter typeToConvert=ValueTypedMember isValueType=false
== TestClassWithNullableValueTypedMember
   MyValueTypedProperty declared=ValueTypedMember propertyTypeCanBeNull=true
        converter=ObjectDefaultConverter typeToConvert=ValueTypedMember isValueType=false
ValueTypedMember.isValue() = true
```

**两行完全相同**：declared 类型、converter 类、`TypeToConvert`、`propertyTypeCanBeNull`
全部一致，唯一差别是编译期插入的 `.clone()`（不可空成员有，可空成员没有）。

**结论**：**不存在**能在 compat 侧区分它们的运行期信号，
所以"让通用 `getIsValueType` 认得 `value class`"没有安全实现 —— 该方向**正式关闭**，
并把"生成物里 `.clone()` 的有无"记为**编译期信息，运行期不可用**（本会话第二次踩到这条：
第一次是 §4.64 的 `int` vs `int?`，解决方式是取**声明成员**的原始类型；
struct 没有对应的原始类型可用）。

### 4.74 Round 71：写出侧 `ClassCast Integer -> String` 与读侧 64 条是同一个簇；第三条修法被否证（539 → 665，已回滚）

**新事实 1（探针 `DefProbe11`）**：写侧 15 条不是独立问题。最小复现类（同一类里同时声明
`CSharpList<CSharpList<Integer>>` 与 `CSharpList<String>`）序列化即得到与测试完全相同的栈：

```
ClassCastException: class java.lang.Integer cannot be cast to class java.lang.String
  at StringConverter.write(StringConverter.java:33)
  at ListOfTConverter.onWriteResume(ListOfTConverter.java:62)
  at ListOfTConverter.onWriteResume(ListOfTConverter.java:35)
  at IEnumerableDefaultConverter.onTryWrite(IEnumerableDefaultConverter.java:216)
  at JsonConverter1.tryWrite(JsonConverter1.java:436)
```
同一个探针里，`CSharpList<Object>`（`{"MyObjectList":[7,"x"]}`）**写得完全正确** ——
说明只有"元素类型被擦除"的嵌套泛型才中招。

**新事实 2（探针 `DefProbe12`）**：那个 2-D 成员的两条候选元素信息**都是错的**：

```
prop=MyInt16TwoDimensionList
   converter=ListOfTConverter element=class io.github.ningpp.compat.CSharpList
   viaConverter = ListOfTConverter/typeToConvert=CSharpList/element=class java.lang.String   ← 错
prop=MyStringList
   converter=ListOfTConverter element=class java.lang.String
   viaConverter = StringConverter/typeToConvert=String/element=null                          ← 对
bare CSharpList classInfo: elementType=class java.lang.String
   fallback(getElementClassInfo) = StringConverter/typeToConvert=String/element=null         ← 错
```

要点：`IEnumerableDefaultConverter.onTryWrite` 第 214 行那条
`TypeHelper.writeElementPropertyInfo(this, options, fallback)` —— **本来就是为修这个问题加的钩子** ——
在元素类型被擦除时也无能为力，因为它走的还是同一份被污染的裸类信息
（`getOrAddClass(CSharpList.class)` 只有一个槽，被 `List<String>` 成员占了）。

**新事实 3（第三条修法，已回滚）**：既然裸类信息的内容取决于"最后反射的是哪个成员"，
就让它与顺序无关 —— pass 改写 `JsonSerializerOptions.getOrAddClass`：

```java
result = ConcurrentHashMapHelper.getOrAdd(_classes, type, new JsonClassInfo(type, this));
// ->
result = (JsonClassInfo)io.github.ningpp.compat.TypeHelper.getOrAddClassInfo(_classes, type, this);
```
compat 侧在创建前清空 `MEMBER_ARGUMENTS`/`CONSUMED_ARGUMENTS`（仅当该类型有类型参数、
且不在 `beginElementArgumentsScope` 作用域内），创建后恢复；`putIfAbsent` 保留原有的竞态语义。

**实测：`539 → 665`，修 3 / 坏 129**（`Tests run: 7902` 恰好 1 次）。
回归面极宽（`ArrayTests#writeClassWithGenericList`、`CollectionTests#readImmutableListTOfImmutableListT`…）：
`List<SimpleTestClass>` 这类成员的类信息**本来就依赖成员实参**才能拿到正确元素类型，
清空上下文等于把它们全部打回 `Object`。

**三次独立尝试汇总（同一簇，全部失败）**

| 尝试 | 实测 |
|---|---|
| (a) 给**成员**创建闭键类信息（± 元素侧闭键查询） | round 41：+19；round 70：+37（加作用域守卫后逐条一致 → 代价全在成员侧创建） |
| (b) 只做**元素侧**闭键查询 | 无效：帧仍是裸类信息，`elementArgumentsOfOwner` 得到空数组，作用域不启动 |
| (c) 让擦除类的类信息**与反射顺序无关** | 本轮：+126 |

**结论**：读侧 64 + 写侧 15 = **79 条（14.7%）是同一个簇**，需要的是
"**闭类型类信息身份**"贯穿 `getRuntimeClassInfo()` / `getElementClassInfo()` / 元素属性信息解析
三处的**结构性**改动；任何单点补丁都会与"每个擦除类一份"的既有身份混用，
而混用的代价已被 (a)(c) 两次量化（+37、+126）。

**回滚后复核**：重新生成 + 全量测量 = `539 → 539，修 0 / 坏 0`，`triage-xml.json` 亦恢复一致。
### 4.75 Round 72：改用"消息签名分组"后，两条长尾机械化缺陷（539 → 532）

**方法转变**：§4.72/§4.74 在同一个簇上连撞三次（+37、+126，均已回滚）。本轮先用工具看全局分布：

- `tools/sig_group.py <triage.json>`：按消息**首行**做归一化签名（去掉数字、引号内容、类名）。
  结果：539 条 → **104 个签名**。最大项是
  `Assert.equal() failure: Values differ`（174 条），但
- `tools/equal_group.py`：再按 Expected/Actual 的**形状**分组 → 174 条分成 **59 种形状**
  （`E[N]A[N]` 47、`E[{}]A[{}]` 28、`E[{]A[{]` 10 …），**异质**，没有单点修法。

真正的收获是一条 **8 条的 NPE 签名**（`Cannot invoke … getReference(…) is null`）。

#### (1) 空属性名哈希 —— 提交 `853e17ce`，**修 5 / 坏 0**（539 → 534）

```
Cannot invoke "java.lang.Integer.intValue()" because the return value of
  "io.github.ningpp.compat.MemoryMarshal.getReference(io.github.ningpp.compat.ReadOnlySpan)" is null
  at dotnet.text.json.JsonClassInfo.getKey:747
  at dotnet.text.json.JsonClassInfo.getProperty:536
  at dotnet.text.json.JsonSerializer.lookupProperty:367
```

**根因**：C# 的 `GetKey(ReadOnlySpan<byte>)` 对**零长度**名字返回 `0`
（`{"":1}` 是合法 JSON），而转换器把字节读取提升成"引用 + Unsafe"形式，方法开头是
`int reference = MemoryMarshal.getReference(name);`；compat 的
`getReference(ReadOnlySpan)` 对空 span 返回 **null**，其 javadoc 明确写着这是
"C# 返回指向后备缓冲的 ref"的安全 Java 替代。于是**解包 null 的分支在真正会读的
长度分支之前**就抛了（长度 0/1 时后面的表达式是 `0L`，根本不读）。

**修法**（pass `PatchPropertyNameKeyForEmptyName`，`JsonClassInfo.java`）：

```java
int reference = name.getLength() > 0 ? MemoryMarshal.getReference(name) : 0;
```

**为什么改调用点而不是改 compat**：`getReference` 的 null 契约是有意的，别处可能靠它判空；
本条只需要让 `getKey` 不炸。

#### (2) `DateTimeOffset` 的 `"O"` 必须写带符号偏移 —— 提交 `e9d89bc1`，**修 2 / 坏 0**（534 → 532）

```
Utf8JsonWriterTests#writeString_NullPropertyName_DateTimeOffset
  E: {"":"0001-01-01T00:00:00+00:00", ...}   A: {"":"0001-01-01T00:00:00Z", ...}
DateTimeConverterTests#serializeDateTimeOffset
  E: "2000-12-15T22:11:03.055+00:00"        A: "2000-12-15T22:11:03.055Z"
```

**根因**：`CSharpDateTimeOffset.toString("O")` 用 Java 的
`DateTimeFormatter.ofPattern("yyyy-MM-dd'T'HH:mm:ss.SSSSSSSXXX")`，
Java 的 `X`/`x` 对**零偏移**渲染成 `Z`；C# 的 `DateTimeOffset` **自带偏移**，
`"O"` 永远写 `+00:00`（`Z` 只属于 `DateTimeKind.Utc` 的 `DateTime`）。
JSON writer 路径是 `Utf8Formatter.tryFormat(value, …, 'O')` → `toString("O")`
→ `JsonWriterHelper.trimDateTimeOffset`，而**裁剪逻辑按 `±hh:mm` 布局的固定下标**取值
（22–26 是 7 位小数），`Z` 会短一个字符。

**修法**：新增 compat 的 `formatOffset(ZoneOffset)` 手工拼 `±HH:mm`，`toString("O")` 用它。
**注意**：§6.2 里 "`XXX`→`xxx`" 是被否证过的方向（`x` 同样渲染 `Z`）—— 本条是手工拼装。

#### 顺带记录（未修，已判定不在射程内）

`writeNumberValueDecimal(boolean,boolean)[1..4]`（4 条）：期望 `["1234567.0", …]`（**带引号**）、
实际 `[1234567.0, …]`。追到期望值的产生处是**测试侧**的 `GetNumbersExpectedString`，
它用 Newtonsoft 的 `JsonTextWriter.WriteValue(decimal)` 生成期望串 ——
即**端口化的测试预言机把 decimal 写成了字符串**，与 writer 输出无关。
除非改 Newtonsoft 端口本身，否则不该动 writer 侧。


### 4.76 Round 68：两条长尾修复（按"最深库帧"分组找到的）

**方法再进一层**：`tools/site_group.py` 按**最深非测试帧**分组（消息只说"出了什么事"，
最深库帧说"死在哪一行"）。532 条 → 249 个站点；头几名是已知的擦除簇
（`ThrowHelper.reThrowWithPath:681` 99 条）与测试预言机
（`JsonTestHelper.assertContentsAgainstJsonNet:720` 38 条），
但从中浮出两个可动手的站点。

#### (1) `Encoding.getString` 无视 `throwOnInvalidBytes` —— 提交 `73b6fec7`，**修 8 / 坏 0**（532 → 524）

站点：`Utf8JsonReaderTests.testingGetStringInvalidUTF8:6503`（7 条）+ `JsonDocumentTests#getString_BadUtf8`（1 条）。
断言原文：`Assert.true_(false, "Expected InvalidOperationException when trying to get string value for invalid UTF-8 JSON text.")`
—— 即 `json.getString()` **没有抛**。

**根因**：`Encoding.getString(byte[], int, int)` 就是 `new String(bytes, index, count, charset)`，
而 `new String(...)` **永远用替换字符**处理非法输入；但 `JsonReaderHelper.s_utf8Encoding`
是 `Encoding.getUTF8ThrowOnInvalid()`（C# `new UTF8Encoding(false, true)`），
它的调用方 `transcodeHelper` 明确 `catch (DecoderFallbackException)` 并包成
`InvalidOperationException` —— 这个 catch 此前**永远不可能触发**。
`Encoding` 里其它方法（第 311/365/598 行）都已按 `throwOnInvalidBytes` 分支，只有 `getString` 漏了。

**修法**：`throwOnInvalidBytes` 为真时改用
`charset.newDecoder().onMalformedInput(REPORT).onUnmappableCharacter(REPORT)`，
捕获 `CharacterCodingException` 后抛 `DecoderFallbackException`。

#### (2) `getKey` 只看首字节与长度 —— 提交 `4ba90036`，**修 1 / 坏 0**，另 6 条换了失败原因（524 → 523）

**发现路径**：`ObjectTests#readSimpleClass` 报
`The JSON value could not be converted to SampleEnum. Path: $.MyEnum`，
但 `BytePositionInLine: 129` —— 用 `tools/s_json_at.py` 解出该位置其实是
`"MyChar" : "a"` 的**字符串值**。即：读到 `MyChar` 时，处理它的是 **MyEnum 的转换器**。

**根因**：`JsonClassInfo.getKey` 的 C# 语义是"前 7 个字节 + 长度"，但转换器把字节读取
降级成"引用 + `Unsafe`"形式：

```java
int reference = MemoryMarshal.getReference(name);      // Java 侧返回**首元素的取值**
Unsafe.readUnaligned(new IntHolder(reference))          // pass-through，原样返回该值
Unsafe.add(_referenceRef2, 4)                           // 同样原样返回
```

于是 **同一长度、同一首字节的名字得到完全相同的 key**（偏移量根本没被读）。
而 `isPropertyRefEqual` 对**长度 ≤ 7** 的名字**只比较 key**，
所以 `"MyChar"`（6 字节，'M' 开头）会命中扫描中第一个 6 字节 'M…' 属性
（`MyEnum`/`MyGuid`/`MyByte`…）。这同时解释了"只在全量顺序下失败"的现象
（扫描起点 `frame.propertyIndex` 依赖此前读过的属性）。

**修法**：compat 新增 `TypeHelper.propertyNameKey(ReadOnlySpan<Integer>)`，
**按真实字节**实现 C# 的 key（>7 用 min(len,0xFF)<<56，否则 len<<56，奇数长度再补最后一个字节）；
pass 把整个生成方法体替换成对该实现的调用（旧的"空名字"补丁随之作废，已删除）。

**测量**：`524 → 523`（集合差修 1 / 坏 0）。SampleEnum 相关失败从 **9 条降到 3 条** ——
另外 6 条越过了 enum，落到它们真正的失败原因上（集合差如实仍算失败）。
**这是"0 回归"里价值最高的一条**：旧实现对"同首字节同长度"的名字**根本无法区分**，
修好它没有让任何既有用例变差。


### 4.77 Round 69：非公开成员的可见性判定（523 → 518，修 5 / 坏 0）

**入口**：`tools/site_group.py` 的站点榜上
`PropertyVisibilityTests.nonPublicProperty_WithJsonInclude_Invalid`（5 条 theory 用例，站点是该测试的第 221 行）。

断言：
```java
IllegalStateException ex = Assert.throws_(IllegalStateException.class,
    () -> dotnet.text.json.JsonSerializer.deserialize_0tp("", type, null));
```
即 .NET 在**预热期**对类型做校验（"非公开成员带 `[JsonInclude]` 非法"），
而我们先把空字符串解析失败、抛了 `JsonException`（栈：`ThrowHelper.reThrowWithPath:678 ← JsonConverter1.readCore:137`）。

校验代码本来就在 `JsonClassInfo` 第 118–147 行；问题是三类成员进不到那个分支。
theory 参数（JUnit 1-based）：

| # | 类型 | 修前 | 原因 |
|---|---|---|---|
| 1 | `ClassWithPrivateProperty_WithJsonIncludeProperty` | 通过 | getter 带 `CompatNonPublicAccessor` |
| 2 | `ClassWithInternalProperty_WithJsonIncludeProperty` | 失败 | 见下 |
| 3 | `ClassWithProtectedProperty_WithJsonIncludeProperty` | 通过 | 同上 |
| 4 | `ClassWithPrivateField_WithJsonIncludeProperty` | 失败 | 见下 |
| 5 | `ClassWithInternalField_WithJsonIncludeProperty` | 失败 | 见下 |
| 6 | `ClassWithProtectedField_WithJsonIncludeProperty` | 失败 | 见下 |
| 7 | `ClassWithPrivate_InitOnlyProperty_WithJsonIncludeProperty` | 通过 | |
| 8 | `ClassWithInternal_InitOnlyProperty_WithJsonIncludeProperty` | 失败 | 同 #2 |
| 9 | `ClassWithProtected_InitOnlyProperty_WithJsonIncludeProperty` | 通过 | |

**根因 1（字段）**：`TypeHelper.getFields(type, bindingFlags)` 用的是 `Class.getFields()`，
它**只返回 public 字段**，所以 `BindingFlags.NonPublic`（32）**永远无效**，
私有/受保护字段对成员枚举完全不可见。修成按 flags 遍历各层
`getDeclaredFields()`（`DeclaredOnly`/`Static`/`Instance`/`Public`/`NonPublic` 全部生效）。
**第一次修完立刻出现 1 条回归**：`jsonIgnoreAttribute_UnsupportedCollection` 抛
`InaccessibleObjectException` —— `FieldInfo` 构造时 `setAccessible(true)` 对 JDK 内部类
（模块未开放）会抛。加 `trySetAccessible()` 预检即消（也顺带证明：这条修法修好了
#4/#6 两条，回归消掉后集合差为 **修 2 / 坏 0**）。

**根因 2（`internal` 属性）**：生成物实测

```java
@CompatJsonInclude
@CompatNonPublicProperty        // ← 整个属性非公开
public String getMyString() { return myString; }   // ← 但 Java 修饰符是 public
```
而 `PropertyInfo.getGetMethod()` 只检查 `CompatNonPublicAccessor`，
**`getSetMethod()` 却检查 `CompatNonPublicProperty`** —— 两者不对称。
`JsonClassInfo` 第 122 行的"这个成员公开吗"用的正是 `getGetMethod()/getSetMethod()`，
于是 `getGetMethod()` 返回非 null 且 `getIsPublic()` 为真 → 成员被当成公开 → 校验被跳过。
把 `getGetMethod()` 改成与 `getSetMethod()` 对称后 **修 2 / 坏 0**（#2、#8）。

**根因 3（`internal` 字段）**：生成物是 `@CompatJsonInclude public String MyString = null;`
—— `internal` 被发成 Java `public` 且**没有任何标记**。让 `FieldTransformer` 给所有非公开字段
发 `CompatNonPublicProperty`，`FieldInfo.getIsPublic()` 与 `getFields` 的 flags 判定都认它 →
**修 1 / 坏 0**（#5），整条 theory 闭合。

**母题**：这是"C# 可访问性 ≠ Java 修饰符"的**第三个**入口。前两个是
`CompatNonPublicAccessor`（属性访问器）与 `CompatInternalCtor`（构造函数）。
**凡运行时问"这个成员/类型公开吗"，先查这里缺了哪个标记。**

### 4.78 Round 70：`getGenericArguments` 是消费式查询（探针钉死；一处中性修复，518 → 518）

**入口**：站点 `CollectionTests.readKeyValuePairOfKeyValuePair:2100`（4 条，`Expected: Key / Actual: Key`）
与 `CustomConverterTests` 的 4 条 `CSharpDictionary.getChecked` KeyNotFound。

**探针 `DefProbe14`**：注册 `CSharpKeyValuePair<String, CSharpKeyValuePair<Integer,Integer>>` 后，
在注册作用域内连续三次调用 `TypeHelper.getGenericArguments(CSharpKeyValuePair.class)`：

```
genericArguments #1 = [class java.lang.String, class io.github.ningpp.compat.CSharpKeyValuePair]
genericArguments #2 = [class java.lang.Integer, class java.lang.Integer]
genericArguments #3 = [class java.lang.Integer, class java.lang.Integer]
```

**每调用一次消费一层**（这是"外层 list → 内层 list 各取各层"所依赖的机制），
而 `ObjectConverterFactory.createKeyValuePairConverter` 把它当纯函数用了两次：

```java
Class keyType   = TypeHelper.getGenericArguments(type)[0];   // 正确
Class valueType = TypeHelper.getGenericArguments(type)[1];   // 拿到内层 KeyValuePair 的实参
```

**修法**：pass `PatchKeyValuePairGenericArguments` 绑定成一次调用 + 两条赋值。

**测量**：`518 → 518`，**修 0 / 坏 0**；对比两次 triage 的 18 条"消息变化"经逐条核对
**全部是对象身份哈希噪声**（`@5e2a6991` → `@63917fe1`），无一条实质变化。

**判定**：这些用例跑到工厂时**第一层也已被预热期消费**，所以只绑定方法内的两次调用不解决问题。
按 §8.3"中性改动要先看栈是否变了再决定回滚"执行了该检查（结论：没变），
但本条作为**已证伪的潜在缺陷修复**保留（先例 `99151aa6`，同为"实测中性但语义更正确"）。

**与既有结论的关系**：这 8 条与 §4.74 的 79 条同源 —— 都要求**实参在预热到读取的全程可见**；
单点绑定不够，应与 §8.2.26 的结构性方案合并设计。

**另一条本轮记录但未动的线索**：`ObjectTests#tooLittleJsonFails`（6 条）失败在
`Assert.equal(0, reader.getBytesConsumed())`：直接对 `Utf8JsonReader` 调
`JsonSerializer.Deserialize(ref reader)`，异常抛出后 .NET 侧 `BytesConsumed` 仍是 0，
我们已消费 1/4/6 字节。匹配它需要在异常路径回滚 reader 状态 —— 会影响依赖"reader 前进"的用例，
属独立设计决策。

### 4.79 Round 71：三条线索实测（全部回滚/未修，518 → 518 复核实测）

**(1) `newNonGenericCollection` 先实例化目标类型 —— 两版都坏，已回滚**

簇 `AbstractCollection CSharpArrayList`（3 条）：`IEnumerableConverter.createCollection` 抛
"The collection type 'class io.github.ningpp.compat.CSharpArrayList' is abstract, an interface,
or is read only, and could not be instantiated and populated."
读代码即可定位：`newNonGenericCollection` 唯一的 null 出口是生成代码的守卫分支
`if (!assignableFromRuntime) return null;`，而 `assignableFromRuntime` =
`isAssignableFrom(getTypeToConvert(), getRuntimeType())` —— 对 `CSharpArrayList` 来说
`getRuntimeType()` 是泛型 `CSharpList`，于是守卫否掉了一个**有公开无参构造器**的类型。

| 版本 | 实测 |
|---|---|
| 先 `getDeclaredConstructor()` + `setAccessible(true)` 实例化 | `518 → 525`，修 0 / **坏 7** |
| 收紧为只用**公开**无参构造器 `getConstructor()` | `518 → 524`，修 0 / **坏 6** |

坏的都是 `CollectionTests#readPrimitiveIImmutable{List,Queue,Set,Stack}T`、
`readReadOnlyCollections_Throws[1]`、`read_NonGeneric_NoPublicConstructor_Throws[5]/[6]`。
**结论**：这些不可变包装类**有**公开无参构造器，实例化它们会走错路径 ——
"能不能实例化"不是这里正确的判据。回滚后重新生成 + 全量测量 = `518 → 518，修 0 / 坏 0` ✓。

**(2) `[[I`（`int[][]`）"not supported"：全局 rank 标记跨用例泄漏（3 条，未修）**

失败栈：`ThrowHelper.throwNotSupportedException_SerializationNotSupported:551 ←
IEnumerableConverterFactory.createConverter:66`，消息 `The type 'class [[I' is not supported.`。

`createConverter` 里：
```java
if (TypeHelper.getIsArray(typeToConvert)) {
    if (TypeHelper.getArrayRank(typeToConvert) > 1) {
        ThrowHelper.throwNotSupportedException_SerializationNotSupported(typeToConvert);
```
而 `getArrayRank` 的实现在注释里就写明了机制与坑：

```java
// The registration is consumed by this query: java.lang.Class is canonical, so a C# int[,] and a
// C# int[][] are the SAME runtime class and a durable marker would make every later jagged int[][]
// conversion look multidimensional ("The type 'class [[I' is not supported").
Integer rank = MULTI_DIM_ARRAY_RANKS.remove(type);
return rank != null ? rank : 1;
```

标记由 `TypeHelper.getMultiDimArrayClass(elementType, rank)` 写入，而**生成出来的测试大量直接调用它**：
`ExceptionTests` 8 处、`CustomConverterTests` 2 处，形如
`Assert.contains(TypeHelper.getMultiDimArrayClass(int.class, 2).toString(), ex.getMessage())`
—— **只为拿名字**，永远不会被 `getArrayRank` 消费 → 留下"`[[I` 是 rank 2"的全局笔记 →
同 JVM 里**之后**任何一次真正的**交错数组** `int[][]` 转换都中招。

本轮命中的 3 条（`CacheTests#propertyCacheWithMinInputsLast`、`ExceptionTests#caseInsensitiveFails`、
`ObjectTests#readClassWithNestedComments`）都是"顺序相关"的，与此机制吻合。

**修法方向（未做）**：让"只为文案"的 `typeof(int[,])` 发一个**不注册**的变体。
compat 侧无法补救 —— 标记按 `Class` 存，而 C# 的 `int[,]` 与 `int[][]` 在 Java 里同一个 `Class`。

**(3) `enum eDevice : uint` 的 flags 表示不支持字符串枚举名（6 条，未修）**

`CustomConverterTests#enumValue(String)[1..2]`、`#enumArray(String)[1..2]` 等，消息
`The JSON value could not be converted to class java.lang.Integer. Path: $ / $.Connections[0].Device`。

实测生成物把 `enum eDevice : uint` 发成 **flags 静态常量类**：

```java
public static class eDevice {
    public static final int PC = 0x2;
    public static final int Tablet = 0x8;
    public static int or(int a, int b) { return a | b; }
```

对应的测试是 `int obj = (int) JsonSerializer.deserialize(json, null, int.class)` ——
**类型映射是一致的**，不是"枚举被擦成 int"的错误；缺的是这套表示上的**字符串枚举名支持**
（JSON 为 `["PC","Tablet"]`）。属功能缺口。

### 4.80 Round 72：多维数组 rank 标记的两个泄漏源（518 → 513，修 5 / 坏 0）

承接 §4.79(2)。协议：`getMultiDimArrayClass(element, rank)` 发布笔记 → `getArrayRank` 消费；
`IEnumerableConverterFactory.createConverter` 里 `getArrayRank(...) > 1` 抛 `NotSupportedException`。

**泄漏源 1（文案用调用）** —— 修法：转换器为"只被渲染"的 `typeof(T[,])` 发不注册变体。

```
生成物实测：getMultiDimArrayClassName 10 处（全部形如 `....toString()`）
            getMultiDimArrayClass      6 处（全部作为 Class 实参传进 deserialize）
```
实现：`TypeOperationTransformer.TransformTypeOf` 里判 `node.Parent is MemberAccessExpressionSyntax
{ Name.Identifier.Text: "ToString" }`（新增 `IsRenderedOnly`）；compat 新增
`getMultiDimArrayClassName`（同样的 `Array.newInstance`，不写注册表）。
**单独这一半实测中性：518 → 518，修 0 / 坏 0。**

**泄漏源 2（缓存命中无人消费）** —— 修法：把消费点提前到"缓存命中也会进入"的入口。

链路：`deserialize(json, options, [[I)` → `getOrAddClassForRootType([[I)`；
类信息与转换器**已缓存**时，`IEnumerableConverterFactory.createConverter`（原本唯一的
`getArrayRank` 调用者）与 `getConverter` 都不会跑 → 笔记留存。两个后果都实测到了：

| 后果 | 证据 |
|---|---|
| 该拒绝的没拒绝 | `ArrayTests#readPrimitiveArrayFail`：`Assert.throws() failure: No exception thrown / Expected: java.lang.UnsupportedOperationException` |
| 之后的交错数组被误判 | `ExceptionTests#caseInsensitiveFails`：`The type 'class [[I' is not supported` |

修法：compat 新增 `takeMultiDimArrayRank(Class)`（消费并回答是否 > 1），
pass 在 `JsonSerializerOptions.getOrAddClass(Class)` 的**最前面**插入该检查并抛出，
另在 `getConverter` 入口再兜一道（`PatchMultiDimArrayRankAtClassLookup` /
`PatchMultiDimArrayRankAtConverterEntry`）。
**测量：`518 → 513`，修 5 / 坏 0**：

```
FIXED  ArrayTests#readPrimitiveArrayFail
FIXED  ExceptionTests#caseInsensitiveFails
FIXED  ExceptionTests#unsupportedTypeFromRoot
FIXED  NullTests#invalidRootOnRead
FIXED  NullTests#invalidRootOnWrite
```
**全树 `[[I` 失败清零**（`tools/check_ii.py` 复核 = 0）；原先命中该机制的两条
（`CacheTests#propertyCacheWithMinInputsLast`、`ObjectTests#readClassWithNestedComments`）
转到真正的失败原因（擦除簇）上，集合差如实仍算失败。

**方法学补充**：这是本项目里第二个被定位的"**顺序相关失败**"根因（第一个是 §4.72/§4.74 的
类信息缓存）。**凡"单独跑过、全量跑挂"的用例，先查有没有按 `Class` 存的全局笔记/缓存。**

### 4.81 Round 73：`ref` 参数的回滚必须就地写回（513 → 503，修 10 / 坏 0）

**入口**：`ObjectTests#tooLittleJsonFails`（6 条）+ `ReadValueTests#tooLittleJsonForIntArray`（3 条）
+ `ReadValueTests#readPropertyName`。

**探针 `DefProbe15`**（最小复现：截断 JSON + `Deserialize(new ObjectHolder<>(reader), null, Poco.class)`）：

```
before  = 0
threw   = JsonException: '1' is an invalid end of a number. Expected a delimiter. Path: $ ...
after   = 6          ← .NET 侧应为 0
```

**根因**：生成代码的注释里已经写了正确契约
（"If an exception is thrown the reader is reset to the state it was in when the method was called."），
但实现缺失；而且**已有的**回滚写法是

```java
reader.value = restore;        // JsonSerializer.java 第 1054 行与第 1078 行
```

它模拟的是 C# 的 `reader = restore;`（`ref` 参数）—— 在 C# 里写穿调用方存储，
在 Java 里**只换掉 ObjectHolder 的引用**，所以"自己留着 reader 变量的调用方"看到的仍是前进过的对象。
（第一次尝试只在最外层 `catch` 里 `reader.value = 快照`，**实测中性 513 → 513** ——
正是因为它换的是引用而不是就地写回；这条中间测量是定位第 2 个缺口的关键。）

**修法**：

1. `StructTransformer.AddCloneMethod` 里为每个有可赋值（非 static、非 final）实例字段的 struct
   发 `public void __cs2jCopyFrom(T other)`（逐字段 `this.f = other.f;`）。
   实测生成物：`Utf8JsonReader` 第 6175 行 `public void __cs2jCopyFrom(Utf8JsonReader other)`，
   含 `_consumed` 与 `_totalConsumed`（`getBytesConsumed()` 返回两者之和）。
2. pass `PatchReaderRestoreInPlace` 把两处 `reader.value = restore;` 改成
   `reader.value.__cs2jCopyFrom(restore);`；`PatchRefReaderResetOnThrow` 在最外层入口加
   `catch (RuntimeException) { reader.value.__cs2jCopyFrom(快照); throw; }` 兜底。

**实测**：`513 → 503`，**修 10 / 坏 0**：

```
FIXED  ObjectTests#tooLittleJsonFails(String)[1..6]
FIXED  ReadValueTests#tooLittleJsonForIntArray(String)[1..3]
FIXED  ReadValueTests#readPropertyName
```

**可复用判据**：**凡生成代码里出现 `holder.value = x;`，先问"这是不是在模拟 C# 的 `ref` 赋值？"**
—— 若是，调用方可能仍持有原对象，必须**就地**改而不是换引用。

### 4.82 Round 74：`ref`/`out` 模拟的全量扫描（0 处遗留）+ 对象初始化器缺口（503 → 503）

**(1) 全量扫描结论：没有遗留**

`tools/holder_writes.py`（全树 `x.value = …`，按 holder 名分组）：

```
distinct holders assigned: 54   total assignments: 457
  value 154 | this 62 | syntax 26 | err 19 | e 16 | utf8PropertyName 14 | consumed 11 | result 10
  reader 8 | bytesWritten 8 | marker 8 | newHost 6 | idx 6 | runtimeType 6 | i 6 | data 6 ...
```
`reader` 的 8 处：`JsonDocument` 4 处（1204 行附近的注释原文 —— "Value copy to overwrite **the ref**
on an exception and undo the destructive reads"）、`JsonSerializer` 2 处（round 73 已修）、
`JsonConverter` 1 处与测试 3 处是**有意替换**成新 reader，不是回滚。

`tools/holder_scan2.py`（"局部变量作为 ref/out 传入"的站点）：

```
holder sites: 194  suspects: 0
```
即每个调用方都读回了 `holder.value` 或原变量 —— round 73 的 `JsonSerializer` 是唯一"调用方自留变量"
的站点。据此把就地回滚提升为**全文件通用 pass**（覆盖 `JsonDocument` 4 处），
**实测 `503 → 503，修 0 / 坏 0`**，与扫描结论吻合；保留作为忠实模拟（先例 `6a30e0ca`）。

**(2) 对象初始化器缺口（4 条，未修）**

`ExtensionDataTests#extensionProperty_SupportsWritingToCustomSerializerWithExplicitConverter[1..4]`：
`Expected: {"MyCustomOverflowWrite":"OverflowValueWrite"} / Actual: {}`。

C# 原文（`ExtensionDataTests.cs`）：
```csharp
var root = new TRoot() { Overflow = new TDictionary() };
string json = JsonSerializer.Serialize(root);
```
生成 Java：
```java
var root = (TRoot) io.github.ningpp.compat.Activator.createInstance(tRootClass);
```
**对象初始化器被丢弃** → `Overflow == null` → `getMemberAndWriteJsonExtensionData` 里
`value == null → success = true`（该分支与 .NET 一致）→ 输出 `{}`。

修法在**转换器**：支持 `new T { Prop = value }`（构造后逐属性赋值）。
与 round 63 的 `[JsonIgnore(Condition = …)]` 同一根因（C# 对象初始化器），属通用缺口。

### 4.83 Round 75：类型参数创建分支丢掉对象初始化器（503 → 499，修 4 / 坏 0）

承接 §4.82(2)。对象初始化器**本来就有支持**
（`ObjectCreationTransformer.TransformObjectCreationWithInitializer`，第 335 行附近），
但**类型参数的两个分支在它之前提前返回**（第 294-316 行）：

```csharp
if ((node.ArgumentList == null || node.ArgumentList.Arguments.Count == 0)
    && createdTypeSymbol is ITypeParameterSymbol typeParameter)
{
    if (HasCollectionConstraint(typeParameter)) { ...; return $"({{typeName}}) new CSharpList<{{javaElementType}}>()"; }
    if (typeParameter.HasConstructorConstraint) { ...; return $"({{typeName}}) io.github.ningpp.compat.Activator.createInstance({{p}})"; }
}
```
C# 原文 `var root = new TRoot() { Overflow = new TDictionary() };` 因此只剩构造。

**修法**：两个分支各自判断 `node.Initializer` 是否为 `ObjectInitializerExpression`，是则走新增的
`ApplyInitializer(declaredType, creation, initializer, context)`：
`context.GenerateSyntheticName("__cs2jInit")` 造合成局部 → `AddPreStatement` 构造 → 每个赋值一条
`{tmp}.set{Prop}({value});`（`IPropertySymbol` 有 setter；字段则直接赋值）→ 返回该局部名。
形状与 `ControlFlowTransformer` 的 `with` 表达式完全一致（那里也是 tmp + setter 的 pre-statement）。

**生成物实测**：

```java
TRoot __cs2jInit1 = (TRoot) io.github.ningpp.compat.Activator.createInstance(tRootClass);
__cs2jInit1.setOverflow(io.github.ningpp.compat.Activator.createInstance((Class<TDictionary>) tDictionaryClass));
var root = __cs2jInit1;
```

**测量**：`503 → 499`，**修 4 / 坏 0**：
`ExtensionDataTests#extensionProperty_SupportsWritingToCustomSerializerWithExplicitConverter(Class, Class)[1..4]`。

**同一函数里其余五个特例提前返回**（`FileStream`、`ReflectionEmitMemberAccessor`、`UTF8Encoding`、
`Decimal`、`Guid`）对应的 BCL 类型在 C# 里不配对象初始化器，未处理。

**母题第二例**（第一例：§4.65 的 `[JsonIgnore(Condition = …)]` 对象初始化器）。

### 4.84 Round 75b：目标类型化 `new() { … }` 同样丢对象初始化器（499 → 499，实测中性）

`ObjectCreationTransformer.TransformNew(ImplicitObjectCreationExpressionSyntax node, …)` 原文：

```csharp
var typeName = context.MapType(typeInfo.Type);
return TransformObjectCreationWithArgs(typeName, node.ArgumentList, null, context, typeInfo.Type as INamedTypeSymbol);
//                                                              ↑ 第三个参数是 TypeSyntax?，不是 initializer
```
即该方法**从不查看 `node.Initializer`** —— C# 9 的目标类型化 `new() { Prop = v }` 的所有赋值都被丢掉。

**修法**：复用 round 75 新增的 `ApplyInitializer`（同形状：合成局部 + 每个赋值一条 setter pre-statement）。
生成物实测：全树 `__cs2jInit` 站点 **69** 处（含 round 75 的类型参数分支）。

**测量**：`499 → 499`，**修 0 / 坏 0**；逐条对比消息，唯一差异是 `objectPreserveDuplicateArrays`
的**尾部**（前缀完全相同），集合差无变化。保留理由：丢赋值本身无疑是错的，
且与 round 75 是同一母题（对象初始化器）的第三处。

**母题小结（对象初始化器）**：本项目已发现三处"对象初始化器被丢"：
1. `[JsonIgnore(Condition = …)]`（§4.65，属性上的对象初始化器 → 由标记携带值解决）；
2. `new T { … }` 类型参数创建分支（§4.83，修 4 条）；
3. `new() { … }` 目标类型化创建（本条，实测中性）。

### 4.85 Round 76：`[JsonInclude]` 成员的非公开 setter 不可见（499 → 493，修 6 / 坏 0）

**入口**：`Expect N -> 0` 形状（13 条）里 `PropertyVisibilityTests` 占 6 条。

**探针 `DefProbe16`**（对三个同类分别 `deserialize("{\"MyInt\":1}", null, c)` 后读私有字段）：

```
== Class_PropertyWith_PrivateInitOnlySetter_WithAttribute
   field myInt = 0    getSetMethod() = null   canWrite = false
== Class_PropertyWith_InternalInitOnlySetter_WithAttribute
   field myInt = 1    getSetMethod() = null   canWrite = false
== Class_PropertyWith_ProtectedInitOnlySetter_WithAttribute
   field myInt = 0    getSetMethod() = null   canWrite = false
```

**生成物对比**（`PropertyVisibilityTests.java`）：

```java
// private / protected：C# init 访问器 → protected Java setter
@CompatJsonInclude public int getMyInt() { ... }
/** @implNote Set during construction only (C# init accessor). */
protected void setMyInt(int value) { this.myInt = value; }
// internal：C# internal → Java public（既有约定），因此唯独它能被找到
```

**根因**：`PropertyInfo.findSetter` 遍历 `clazz.getMethods()` —— **只含 public 方法**，
因此 protected/private 的生成 setter 不可见，读路径拿不到 setter。
而 C# 的 `[JsonInclude]` 语义就是"允许反序列化使用非公开访问器"。

**修法**：公开方法里没有匹配时，**若 getter 带 `CompatJsonInclude`**，沿继承链用
`getDeclaredMethods()` 找非公开 setter 并 `setAccessible(true)`。闸门用标记，
`nonPublicMembersAreNotIncluded`（无 `[JsonInclude]`）不受影响。

**测量**：`499 → 493`，**修 6 / 坏 0**：

```
FIXED  PropertyVisibilityTests#nonPublicInitOnlySetter_With_JsonInclude_Works(Class)[1]
FIXED  PropertyVisibilityTests#nonPublicInitOnlySetter_With_JsonInclude_Works(Class)[3]
FIXED  PropertyVisibilityTests#honor_JsonSerializablePropertyAttribute_OnProperties
FIXED  PropertyVisibilityTests#honorJsonPropertyName
FIXED  PropertyVisibilityTests#extensionDataCanHaveNonPublicSetter
FIXED  PropertyVisibilityTests#honorCustomConverter
```
（`honorJsonPropertyName` 是上一轮在查的 struct 用例 —— 同一根因，白查了但方向对。）

**判据**：compat 里所有 `Class.getMethods()` / `getFields()` 式的成员发现都默认"只有 public"，
与 C# 的 `BindingFlags` 语义和 `[JsonInclude]` 破例都不符。`getFields` 的同类问题见 §4.77。

### 4.86 Round 76b：`new InvalidOperationException()` 的默认消息（493 → 490，修 3 / 坏 0）

**入口**：`Assert.contains` 桶里 3 条 `JsonPropertyTests#nameEquals_InvalidInstance_Throws(String)[1..3]`，
消息形如 `Expected substring: 'null' / Actual string: 'null'` —— **期望值本身就是 null**。

**C# 原文**（`JsonPropertyTests.cs:100`）：

```csharp
string ErrorMessage = new InvalidOperationException().Message;
AssertExtensions.Throws<InvalidOperationException>(() => prop.NameEquals(text), ErrorMessage);
```
.NET 的 `new InvalidOperationException()` 的默认消息是
"Operation is not valid due to the current state of the object."；
生成物是 Java 内建的 `new IllegalStateException()`（无消息）→ `ErrorMessage == null`。

**修法**：
1. compat 新增 `ExceptionCompat.invalidOperation()`，返回带默认消息的 `IllegalStateException`
   （类型不变，`Assert.Throws<InvalidOperationException>` 仍匹配）；
2. `ObjectCreationTransformer` 对**无参**的 `System.InvalidOperationException` 特例发该工厂
   （与既有的 `UTF8Encoding`/`Decimal`/`Guid` 特例同一形状）。

生成物实测：`throw ExceptionCompat.invalidOperation();` 出现在 `JsonClassInfo`、`JsonElement`、
`JsonConverter1` 等处（库内的无参抛出也一并带上默认消息，与 C# 一致）。

**测量**：`493 → 490`，**修 3 / 坏 0**。

**异常文本通道第四例**（前三：§4.34 `ThrowHelper` 泛型名、§8.2.16 `csFormat` 数字格式、
§4.71 参数异常 paramName）。

### 4.87 Round 77：`default(Utf8JsonReader)` 的空缓冲区（490 → 488，修 2 / 坏 0）

**入口**：`Assert.throws` 的 `expected=JsonException actual=NullPointerException` 分组里两条
`readDefaultReader` / `parseDefaultReader`，栈为
`Utf8JsonReader.hasMoreData:974 ← readSingleSegment:843 ← read:402`。

**根因**：生成的默认构造器

```java
public Utf8JsonReader() {
    /* zero-initialized */
}
```
Java 的 `_buffer`（`ReadOnlySpan<Integer>`）因此为 **null**；C# 的 `default(Utf8JsonReader)`
有一个空的 `ReadOnlySpan`。`hasMoreData()` 第一行 `_buffer.getLength()` → NPE。

**修法**：pass `PatchDefaultReaderBuffer` 在默认构造器里初始化
`_buffer = new ReadOnlySpan<Integer>();`（其余字段无需处理：`_isNotPrimitive` 的 Java 默认 false
与 C# 一致，正是它让 `HasMoreData()` 跳过会碰 `_readerOptions` 的分支）。

**踩坑**：第一版 replacement 漏了收尾的 `}`（正则把整个构造器吞掉了），生成物直接语法错误；
补上后正常（**这正是"pass 替换整块时必须把闭合符号写回"的老坑**）。

**测量**：`490 → 488`，**修 2 / 坏 0**：`ReadValueTests#readDefaultReader`、`JsonDocumentTests#parseDefaultReader`。

**(附) 回滚的一条实测中性改动**：在 `JsonSerializerOptions.getConverter` 入口把 primitive
规格化成 wrapper（`TypeHelper.toWrapperType`）。动机是 DefProbe3 的实测
（`getConverter(int.class)` = `ObjectDefaultConverter`，而 `Integer.class` = `Int32Converter`，
即转换器表按 wrapper 建键）。**实测 `488 → 488`，0 修 0 坏，且逐条消息零变化** ——
`nullReadTestChar` 的 NPE 不在查找路径上（`char.class` 拿到 CharConverter 后，
是"值类型转换器 + null token"这一已关闭的问题），故回滚。

**"C# 默认值 ≠ Java 默认值"母题第 5 例**：`default(T)`（§4.64）、`default(JsonElement)`（§4.68）、
访问器可见性（§4.85）、`ref` 回滚（§4.81）、默认 struct（本条）。

### 4.88 Round 79：`default(T)` 对 C# 类返回了新实例（488 → 470，修 18 / 坏 0）

**入口**：`Assert.null` 桶（19 条）里"应为 null 却是对象"的一批：
`NullTests#rootObjectIsNull`、`NullTests#deserializeDictionaryWithNullValues`、
`DictionaryTests#{nullDictionaryValuesShouldDeserializeAsNull, dictionaryWithNullShouldPreserveOrder_Deserialize,
shouldHandleNullInDictionaries_Deserialize}`、`ExtensionDataTests#deserializeIntoMultipleDictionaries`、
`ExtensionDataTests#nestedClass[1..3]`（`Actual: CSharpDictionary@…` / `SimpleTestClass@…`）。

**探针 `DefProbe17`**：

```
C.class         -> DefProbe17$C@77a567e1
Object.class    -> null
String.class    -> null
C[]             -> null
SimpleTestClass -> dotnet.text.json.Serialization.Tests.SimpleTestClass@3834d63f
```
只有"具体类作为根类型"答错 —— `deserialize("null", null, C.class)` 返回实例。

**根因**：`DefaultValue.getDefaultByClass` 的兜底是"试无参构造器"：

```java
if (type == String.class || type == Object.class) return null;
try { return type.getDeclaredConstructor().newInstance(); } catch (Exception e) { return null; }
```
于是一切"转换出来的 C# 类"的 `default(T)` 变成了**新实例**。C# 的 `default(T)` 对 **class** 是 null；
只有 **struct** 有零实例 —— 而转换器把 struct 发成 Java 的 `value class` ✓ 判据现成。

**修法**：兜底前加 `if (!type.isValue()) return null;`。

**测量**：`488 → 470`，**修 18 / 坏 0**（清单见计划 §8.2.39）。

**为什么是 −18**：`DefaultValue.ofClass` 是所有 `_cs2jDefault_T()` 的落地实现，
而"读到 null 就用默认值"遍布读侧；默认值从"新实例"改回 null 后，
一整类"null 被当成对象"的假象同时消失。

**同族第 6 例**（C# 默认值 ≠ Java 默认值）：§4.64 default(T)、§4.68 default(JsonElement)、
§4.85 访问器可见性、§4.81 ref 回滚、§4.87 默认 struct、**本条**。

### 4.89 Round 80：`matchesConstructorBindingFlags` 的私有构造器桥不能收紧（470 → 618，已回滚）

**入口**：`ObjectTests#readObjectFail_ReferenceTypeMissingPublicParameterlessConstructor`
（`Assert.throws` 的 `expected=UnsupportedOperationException` 组），栈：

```
ObjectTests.readObjectFail_…:238
  ← io.github.ningpp.compat.Activator.createInstance:26
  ← dotnet.text.json.Serialization.ReflectionMemberAccessor.lambda$createConstructor$0:51
```

**探针 `DefProbe20`** 对该测试的 5 个类型逐个查 `getConstructorInfo(Public|Instance, [])`
与 `Activator.createInstance`：

```
== ClassWithInternalParameterlessCtor     getConstructorInfo(Public) = null        Activator -> InvocationTargetException
== ClassWithPrivateParameterlessCtor      getConstructorInfo(Public) = ConstructorInfo@…  Activator -> InvocationTargetException
== CollectionWithoutPublicParameterlessCtor getConstructorInfo(Public) = null       Activator -> InvocationTargetException
== ConcreteDerivedClassWithNoPublicDefaultCtor getConstructorInfo(Public) = null    Activator -> "No parameterless constructor defined for …"
```

生成物里两个测试类的无参构造器体都是
`Debug.fail("The JsonSerializer should not be callin non-public ctors, by default.");`
—— 它们**故意**在被反射调用时炸掉；所以失败点是"本该返回 null 却拿到了构造器"。

**根因假设**：`matchesConstructorBindingFlags` 的桥接规则
（"非公开类的非公开构造器按 C# 语义视为 public"）把
`ClassWithPrivateParameterlessCtor` 的**显式 private** 构造器也判成可用。
该规则本意覆盖的是"C# 私有嵌套类的**隐式**构造器在 C# 里是 public，而 Java 的隐式默认构造器是
包私有" —— 隐式与显式在 Java 里可以用 `Modifier.isPrivate` 区分。

**尝试**：桥接规则里加

```java
if (Modifier.isPrivate(ctorMods)) {
    return false;
}
```

**实测 `470 → 618`：修 1 / 坏 149**（`CustomConverterTests` 的 `readAheadFrom*`、`custom*`、
`converter*` 等几乎全部回归）。

**结论（勿重试）**：这条桥是**承重**的。转换器会生成一批"工厂化"类，
其无参构造器是**私有**的（Round 20 记录过 `tsc TS2673` 场景：
`EmitNew` 改发 `Type.Create$N(...)`，类本身只剩私有无参构造器），
反射必须能拿到它们。**回滚后重新生成 + 全量测量 = `470 → 470`** ✓。

**若仍要修那条断言**：需要"只对用户类型收紧"的新标记（与 `@CompatInternalCtor` 同族的思路），
**不是**改桥接规则本身。

### 4.90 Round 81：`Type.GetProperty(name, BindingFlags)` 的**标志实参被丢弃**（470 → 467，修 3 / 坏 0）

**症状**（3 条，`DebuggerTests`）：

```
java.lang.NullPointerException: Cannot invoke "io.github.ningpp.compat.PropertyInfo.getValue(Object)"
  because the return value of "io.github.ningpp.compat.TypeHelper.getProperty(java.lang.Class, String)" is null
```

站点 `DebuggerTests.getDebuggerDisplayProperty:54`：

```java
return (String)(TypeHelper.getProperty(TypeHelper.toWrapperType(_cs2j_T), "DebuggerDisplay").getValue(value));
```

**C# 原文**（`tests/DebuggerTests.cs:38`）：

```csharp
return (string)typeof(T).GetProperty("DebuggerDisplay", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(value);
```

**根因有两半，缺一不可**：

1. **转换器丢实参**：`InvocationExpressionTransformer` 里 `System.Type.GetProperty` 的特例
   只取第 0 个实参，直接发 `TypeHelper.getProperty(receiver, name)`，把 `BindingFlags` **静默丢掉**。
   紧挨着的 `GetField` 特例**有**标志分支（发 `getField(receiver, name, flags)`），
   compat 里也**有** `getField(Class, String, int)` —— 只有 `GetProperty` 这条腿缺。
   于是一切带 `NonPublic` 的反射查询都退化成"只看 public"。
2. **compat 没有标志感知的重载**：`TypeHelper.getProperty(Class, String)` 只走
   `type.getMethods()`（**只含 public**）。而该属性在 C# 里是
   `private string DebuggerDisplay => ...`，生成物为
   `@CompatNonPublicProperty @CompatNonPublicAccessor private String getDebuggerDisplay()`
   ——public-only 查询必然返回 `null`，随后 `.getValue(value)` NPE。

**修法**（两处，形状与 `GetField` 完全对齐）：

- compat 新增 `TypeHelper.getProperty(Class<?>, String, int bindingFlags)`：带 `NonPublic` 时
  沿类层次扫描 `getDeclaredMethods()`（跳过 synthetic/bridge、复用 `matchesBindingFlags`
  的可见性/实例性判定、`is` 前缀要求 boolean 返回、`DeclaredOnly` 只扫本类），
  找不到再回落到 public-only 重载。**public-only 重载本身一字未动** ——
  `PropertyVisibilityTests` 的 29 个 `getProperty(type, name)` 站点依赖"只认 public"的语义。
- 转换器 `GetProperty` 特例补上标志分支：第 1 个形参类型为 `System.Reflection.BindingFlags`
  时发 `TypeHelper.getProperty(receiver, name, flags)`。

**影响面核对**：全生成树里带 3 个实参的 `getProperty(` **只有 1 处**（就是本测试），
其余 49 处 C# 原文本来就是 `GetProperty(name)`（无标志）。

**实测**：`470 → 467`，修 3 / 坏 0；聚合行为
`Tests run: 7902, Failures: 232, Errors: 235, Skipped: 18`（`Tests run: 7902,` 在 `mvn -e` 日志里出现**恰好 1 次**）。
逐用例集合差只列出 `DebuggerTests#defaultJsonElement / defaultJsonProperty / defaultUtf8JsonWriter` 三条 FIXED，
REGRESSED 为 0。

**同族**：与 §4.85（`[JsonInclude]` 非公开 setter）同属"**反射只看得见 public**"这一族；
差别在于本条是**转换器把显式给出的可见性标志丢了**，不是 compat 判定错。

### 4.91 Round 82：Newtonsoft 替身把 `decimal` 写成带引号的字符串（467 → 463，修 4 / 坏 0）

**症状**（4 条 = 一个 theory 的 4 行，`Utf8JsonWriterTests#writeNumberValueDecimal`）：

```
Assert.equal() failure: Values differ
Expected: [   "1234567.0",   "1234567.0", … ]      ← 带引号
Actual:   [   1234567.0,     1234567.0,   … ]      ← 裸数字
```

**定位链**（不需要探针，读三处代码即可闭环）：

1. 断言路径是 `JsonTestHelper.assertContents(expectedStr, output)` →
   `assertContentsAgainstJsonNet(expectedValue, value)` →
   `Assert.equal(normalizeToJsonNetFormat(expected), normalizeToJsonNetFormat(value))`。
   **两侧都过"Newtonsoft 风格重排"**，所以差异不在被测的 writer，而在**期望串**。
2. 期望串来自 `GetNumbersExpectedString<T>(…)`（C# 泛型，此处 `T = decimal`），
   内部用 **`JsonTextWriter.WriteValue(value)`** 写值。
3. compat `JsonTextWriter.writeValue(Object)` 只对
   `String` / 日期 / `Boolean` / **`Number`** / `byte[]` 做特殊处理，
   其余落到末尾兜底：`writer.print('"'); writer.print(value.toString()); writer.print('"');`。
   而 C# `decimal` 是**值类型**，compat 的 `io.github.ningpp.compat.Decimal` 声明为
   `public final class Decimal implements Comparable<Decimal>` —— **不是 `java.lang.Number`**。
   于是 `1234567.0M` 在期望串里变成 `"1234567.0"`；Newtonsoft 实际写的是裸 `1234567.0`。

**修法**：在 `writeValue(Object)` 的 `Number` 分支之后加

```java
if (value instanceof Decimal dec) {
    writer.print(dec.toString());   // BigDecimal.toPlainString() → 1234567.0
    return;
}
```

放在 `writeBeforeValue()`（第 185 行）之后，数组分隔符/缩进仍走既有流程。

**实测**：`467 → 463`，修 4 / 坏 0（`[1] [2] [3] [4]` 全部 FIXED）；
`Tests run: 7902, Failures: 228, Errors: 235, Skipped: 18`（聚合行恰好 1 次）。

**同族警示**：`Decimal` 不是 `Number` 这一点会在**每个** `instanceof Number` 判定点重复发作。
`JsonConvert.serializeObject` 里同样是 `value instanceof Number`（顶桶 `JsonConvert *` 的 70+ 条属此族）；
本轮**只**修了 writer 一处，`JsonConvert` 留待单独一轮用同样的显式分支修，
**不要**改成 `Decimal extends Number` —— 那会一次性改变所有 `instanceof Number` 判定点的行为。

### 4.92 Round 83：属性桥凭空造出**非公开属性**（463 → 460，修 3 / 坏 0）

**症状**（3 条，`OptionsTests`，都死在生成辅助方法 `verifyOptionsEqual:653`）：

```
Assert.same() failure: References are not the same
Expected: dotnet.text.json.Serialization.ReflectionMemberAccessor@18a98913
Actual:   dotnet.text.json.Serialization.ReflectionMemberAccessor@7dbae40
```

另有一条同站点、不同值的形状：
`Expected: JsonReaderOptions{_maxDepth=0, _commentHandling=Disallow, allowTrailingCommas=false}` /
`Actual:  JsonReaderOptions{...}`（两个字段全同的 struct 实例）。

**C# 原文**（`OptionsTests.cs:605-653`）：`foreach (PropertyInfo property in
typeof(JsonSerializerOptions).GetProperties())`，对 bool/int/可枚举/value type 分别处理，
最后的 `else` 是 `Assert.Same(property.GetValue(options), property.GetValue(newOptions))`。

**探针 `DefProbe23`**（打印 `PropertyInfo.getProperties(JsonSerializerOptions.class)` 全部 21 条）：

```
PROP MemberAccessorStrategy type=dotnet.text.json.MemberAccessor   getter=<no public getter>
PROP EffectiveMaxDepth      type=java.lang.Integer                 getter=<no public getter>
PROP ReaderOptions          type=dotnet.text.json.JsonReaderOptions getter=getReaderOptions
PROP WriterOptions          type=dotnet.text.json.JsonWriterOptions getter=getWriterOptions
```

.NET 侧 `GetProperties()`（Public）只有 **17** 条：前两条是 **internal 属性**，
后两条来自 **internal 方法** `GetReaderOptions()/GetWriterOptions()`（C# 里根本不是属性）。
compat 的约定桥（`getXxx()` + 0 参数 + 非 void ⇒ 属性）把它们都算成属性 ⇒
两个 options 实例各自懒建的 `ReflectionMemberAccessor` 引用不同 ✗；
`JsonReaderOptions` 是 struct 但 `getIsValueType` 判 false，也走 `Assert.Same` ✗。

**修法（两半，缺一不可）**：

1. compat `PropertyInfo.getProperties(Class)`：
   - 跳过 getter 上带 `@CompatNonPublicProperty` / `@CompatNonPublicAccessor` 的成员；
   - 末尾 `props.removeIf(p -> p.getGetMethod() == null && p.getSetMethod() == null)`
     （整个属性在 C# 都不可见 ⇒ 不是 public 属性）。
2. 转换器 `MethodTransformer`（`javaMethod` 构造之后）：C# **非 public**、0 参数、非 void、
   名字以 `Get` 开头的方法打 `@CompatNonPublicAccessor`（本树 160 处）。
   这是第 1 步能认出 `GetReaderOptions()` 这类"方法冒充属性"的**唯一**线索 ——
   Java 侧看不出 C# 的 internal（转换器把 internal 方法发成 public Java 方法）。

**安全性核对**（`DefProbe24`）：

| 类型 | 属性数 | 两侧都不可见 | 过滤后 |
|---|---|---|---|
| `JsonSerializerOptions` | 21 | 2 | 19 |
| `SimpleTestClass` | 87 | 0 | 87（`CacheTests` 的 `> 64` 断言安全） |
| `SimpleTestStruct` | 47 | 0 | 47 |

**实测**：`463 → 460`，修 3 / 坏 0（`defaultSerializerOptions_General`、
`copyConstructor_OriginalLocked`、`copyConstructor_IgnoreNullValuesCopied`）；
`Tests run: 7902, Failures: 225, Errors: 235, Skipped: 18`（聚合行恰好 1 次）。

### 4.93 Round 83b：70 条 `MyInt16TwoDimensionList[0]` 的根因 —— 原始类键的类信息被**首次实例化冻结**（机制确认，未改码）

**症状**：`reThrowWithPath` 站点 106 条里有 **70 条**消息完全相同：

```
The JSON value could not be converted to class io.github.ningpp.compat.CSharpList.
Path: $.MyInt16TwoDimensionList[0] | LineNumber: 0 | BytePositionInLine: 1808.
```

涉及 8 个测试类（`SpanTests` 36、`ArrayTests` 20、`ReferenceHandlerTests` 19、`ObjectTests` 14…），
但它们反序列化的是同一个 `SimpleTestClass` 载荷，所以是**同一个缺陷**。

**探针 `DefProbe21`**（最小 JSON，逐个成员）：

```
== 2dimList  THREW JsonException ... Path: $.MyInt16TwoDimensionList[0]
   cause[1] java.lang.IllegalStateException: Cannot get the value of a token type 'StartArray' as a string.
        at dotnet.text.json.ThrowHelper.getInvalidOperationException_ExpectedString
        at dotnet.text.json.Utf8JsonReader.getString
        at dotnet.text.json.Serialization.Converters.StringConverter.read(StringConverter.java:39)
        at dotnet.text.json.Serialization.Converters.StringConverter.read(StringConverter.java:33)
        at dotnet.text.json.Serialization.Converters.IEnumerableDefaultConverter.onTryRead(IEnumerableDefaultConverter.java:88)
        at dotnet.text.json.Serialization.JsonConverter1.tryRead
        at dotnet.text.json.JsonPropertyInfo1.readJsonAndSetMember
== 3dimList  THREW（同因，Path: $.MyInt16ThreeDimensionList[0]）
== 1dimList OK   == 2dimArray OK   == empty OK
```

`onTryRead:88` 是快路径 `elementConverter.read(...)`，而
`elementConverter = getElementConverter(state.Current.JsonClassInfo.ElementClassInfo)`。
拿到 **StringConverter** ⇒ 那份元素类信息的元素类型是 `String`。

**探针 `DefProbe22`**（打印运行期对象图）：

```
MEMBER MyInt16TwoDimensionList runtimePropertyType=CSharpList
       converter=ListOfTConverter converterElementType=class io.github.ningpp.compat.CSharpList   ← 成员转换器正确
  CSharpList（_classes 里那一份） elementType=class java.lang.String
       ownConverter=ListOfTConverter converterElementType=class java.lang.String                  ← 被冻结成 String
    -> elementClassInfo type=java.lang.String ownConverter=StringConverter
```

**结论**：

- **成员侧是对的**：`JsonPropertyInfo` 持有的 `ListOfTConverter` 元素类型是 `CSharpList`（外层）✓。
- **坏在共享类信息**：`JsonSerializerOptions._classes` 是 `ConcurrentHashMap<Class, JsonClassInfo>`，
  **按原始 `Class` 键**。里头的 `CSharpList` 类信息只有一份，它的
  `elementType` 与 `ownConverter` 是**第一次实例化时**定下的 —— 本类里第一次是
  `List<string>`（`MyStringList` / `MyListOfNullString`），于是所有 `List<T>` 共用
  "元素是 String" 的那一份。
- **读路径用的是共享类信息**：`IEnumerableDefaultConverter.onTryRead` 取的是
  `state.Current.JsonClassInfo.ElementClassInfo`，而不是当前成员转换器的元素类型 ⇒
  把 `[10,11]` 按字符串读。
- **顺序是哈希序不是声明序**：`myInt16TwoDimensionList` 声明在 `myStringList` 之前
  （生成物 126 行 vs 129 行），但 `PropertyCacheArray` 由字典
  `cache.values().toArray()` 得到，谁先冻结取决于哈希顺序 —— 这正是本簇看起来无规律的原因。
- **推论（重要）**：同一个 `JsonSerializerOptions` 实例里，**每个泛型原始类只有一种实例化能工作**。
  `SimpleTestClass` 有多个 `List<>` 成员，所以必然在读第一个非 String 元素类型的列表时炸掉；
  这也解释了为什么"改一下成员顺序/换个测试"仍然失败。

**已有的一半机制**：`TypeHelper.cachedClassInfoForMember(member, options, rawType)` 会按
`ClosedGenericKey(raw, args)` 在 `_classes` 里**查询**闭键条目（`JsonPropertyInfo.getRuntimeClassInfo()` 在用），
注释明确："只针对单实参形状；多实参（Dictionary/ValueTuple）保持原路径，否则 NullTests 回归（实测）"。
缺的是**创建侧**：`_classes` 里从来没有闭键条目 ⇒ 查询永远落空
（与 §8.2.26"元素侧闭键查询单独做是中性"的观测一致）。

**为什么"临时新建一份类信息"也救不了**：`_converters` 同样是按原始 `Class` 键的
`ConcurrentHashMap`，新类信息的 `ownConverter` 仍会命中那份冻结的转换器。
所以创建侧必须**同时**在**同一实参上下文**里造出闭键类信息与闭键转换器。

**边界（勿重试的教训与未试过的形状）**：全局统一键（`PatchConverterCacheLookup`，855 → 934）
会把**没有实参上下文**的查询也打散；成员侧无差别创建闭键（539 → 576，修 1 / 坏 38）代价全在创建侧。
**未试过**的窄形状：只在"**有实参上下文 + 单实参泛型 + 集合/字典类**"这一带上同时创建/查询
闭键类信息与闭键转换器（不触碰无实参上下文的查询，不给多实参成员建闭键）。

### 4.94 Round 84：上面那条"窄形状"**试过了，否证**（460 → 581 / 460 → 632，均已回滚）

Round 83 结束时把 §4.93 的"未试过的窄形状"排成下一轮。本轮**两个变体都实测了，都不行**，
且**目标簇一条都没修好** —— 这条形状从此进入"勿重试"清单。

**变体 1：全量上下文键**（提交未保留，改动已 `git checkout` 回滚）

- compat 新增 `cacheKeyOrRaw(Class)`（有 `MEMBER_ARGUMENTS`/会话层就用 `ClosedGenericKey`，
  否则回落原始类）、`cacheHas`/`cacheGet`/`cachePut`（`cachePut` 同时写闭键与 raw 别名）。
- 转换器新增 pass `PatchCacheKeyedLookups`（挂在 `JsonSerializerOptions.java` 分支）：
  改写三处生成文本 ——
  `_converters.containsKey/get` → `TypeHelper.cacheKeyOrRaw + cacheHas/cacheGet`；
  `ConcurrentHashMapHelper.tryAdd(_converters, …)` → `TypeHelper.cachePut(_converters, …)`；
  `_classes.get(type)` / `getOrAdd(...)` → `TypeHelper.cacheGet/cachePut(_classes, …)`。
  （pass 的正则先在生成文件上干跑验证过三处都命中。）
- compat 新增 `MEMBER_DECLARED_TYPES` + `beginMemberScope/endMemberScope`（重建成员自己的层），
  `cachedClassInfoForMember` 在 miss 时**创建**闭键类信息（限集合/字典族）。

实测 `460 → 581`（`mvn-build-r153.log`，`Tests run: 7902, Failures: 224, Errors: 357`）：
**修 1 / 坏 122**。新增失败的形状集中在"类信息/转换器串味"：

```
31  class java.lang.Integer cannot be cast to class java.lang.String
22  public short dotnet.text.json.Serialization.Tests.SimpleTestClass.getMyInt16() target=java.lang.String
13  class java.lang.Integer cannot be cast to class io.github.ningpp.compat.CSharpList
12  class [I cannot be cast to class java.lang.String
 9  The JSON value could not be converted to class io.github.ningpp.compat.CSharpList
 7  class java.lang.String cannot be cast to class io.github.ningpp.compat.CSharpList
重灾区：CollectionTests 85、DictionaryTests 9、ExtensionDataTests 7、ArrayTests 6
```

最后那条 `SimpleTestClass.getMyInt16() target=java.lang.String` 是**旧文档里记过的**"帧拿到了别的类型的
属性信息"症状（见 `elementArgumentsOfOwner` 的注释），说明按实例化分裂类信息会泄漏到无关读取。
**目标簇未修好**：FIXED 只有 `ReferenceHandlerTests#dictionaryReferenceLoopInList` 一条。

**变体 2：收窄到"有作用域才换键"**（同样已回滚）

- `cacheKeyOrRaw` 加 `CACHE_CONTEXT_DEPTH` 闸门：只有元素作用域 / 成员创建作用域期间才用闭键，
  其余一律原始类（这正是变体 1 里"用**别的成员**残留实参给无关查询换键"的堵漏）。
- `beginElementArgumentsScope` 额外把元素自己的层发布进 `TYPE_ARGUMENTS`（原先只发布
  `MEMBER_ARGUMENTS`，于是类信息按元素键缓存、转换器却按残留层构建），`endElementArgumentsScope`
  一并还原。
- 创建侧再收窄：只在**同原始类嵌套**（`args[0]` 是 `ParameterizedType` 且 raw == 自身，
  即 `List<List<…>>`）时创建闭键类信息。

实测 `460 → 632`（`mvn-build-r154.log`，`Tests run: 7902, Failures: 215, Errors: 417`）：
**修 1 / 坏 173**，比变体 1 更差；目标簇仍然一条未修好。

**回滚验证**：`git checkout` 两个文件 → `dotnet build` → 全量重新生成 → `mvn -o clean install -e`
（`mvn-build-r155.log`）= `Tests run: 7902, Failures: 225, Errors: 235, Skipped: 18` = **460**，
逐用例集合差 `460 → 460`、`FIXED 0 / REGRESSED 0` ✓（基线精确复原）。

**结论（勿重试）**：

1. "每个原始类一份类信息/转换器"是这套移植**读路径的隐含前提**（读栈帧、属性缓存、
   帧上的 `JsonPropertyInfo` 都按原始类取用）；按实例化分裂条目 → 173 处"帧里出现别的类型的成员信息"。
2. 更关键的是：**换键不是充分条件**。两个变体都让 `_classes`/`_converters` 里出现了
   实例化正确的条目，目标簇却一步没动 —— 说明真正交给 `IEnumerableDefaultConverter.onTryRead`
   的那份类信息，**未必**来自 §4.93 探针看到的那条链（探针只证明了 `_classes` 里那一份是 String 味，
   没有证明**帧里**用的是它）。
3. **下一步的正确顺序**：先写 `DefProbe26` 打印**帧内**的 `state.Current.JsonClassInfo`
   （身份、是否 `_classes` 中的那一份、`elementType`、`PropertyInfoForClassInfo.converter`），
   把"谁把 String 味类信息交给 `onTryRead`"钉死；在此之前不要再动任何缓存键。

### 4.95 Round 85：Newtonsoft 替身写 `byte[]` 多插一个分隔符（460 → 444，修 16 / 坏 0）

**症状**（16 条 = `Utf8JsonWriterTests#writeBase64String(boolean, boolean, String)` 整个 theory），
站点 `JsonTestHelper.assertContentsAgainstJsonNet:722`：

```
Expected: {"message":,"AQIDBAUG","message":,"AQIDBAUG","array":[,"AQI=","AQI="]}
Actual:   {"message":"AQIDBAUG","message":"AQIDBAUG","array":["AQI=","AQI="]}
```

**关键判断**：断言路径 `Assert.equal(normalizeToJsonNetFormat(expected), normalizeToJsonNetFormat(actual))`
两侧都归一化，而**期望**侧（第一个实参）语法是坏的 ⇒ 故障在"期望串生成器"，
即 `getBase64ExpectedString`（生成物 5511-5531 行）用 compat 的 `JsonTextWriter` 写的：
`writeStartObject` → `writePropertyName` → **`writeValue(byte[])`** → `writeStartArray` → …

**根因**：`JsonTextWriter.writeValue(Object)`

```java
writeBeforeValue();                       // 第 185 行：写分隔符/缩进 —— 第一次
if (value instanceof Boolean b) { … }
if (value instanceof Number n) { … }
if (value instanceof byte[] bytes) {
    writeValue(java.util.Base64.getEncoder().encodeToString(bytes));   // ← 递归进 writeValue(String)
    return;                                                            //    后者开头又调 writeBeforeValue()
}
```

`writeValue(String)` 开头同样调用 `writeBeforeValue()` ⇒ **分隔符写了两次**，
于是 `"message":` 后面先冒出一个逗号，值才出现。

**探针 `DefProbe25`**（直接驱动 compat 的 writer，不经过测试框架）：

```
修复前 OUT  = {"message":,"AQIDBAUG","message":,"AQIDBAUG","array":[,"AQI="]}   ← 复现
对照   CTRL = {"n":5}                                                          ← Number 分支（不递归）正常
修复后 OUT  = {"message":"AQIDBAUG","message":"AQIDBAUG","array":["AQI="]}     ← 正确
```

**修法**（`JsonTextWriter.writeValue(Object)` 的 `byte[]` 分支就地写值）：

```java
if (value instanceof byte[] bytes) {
    writer.print('"');
    writer.print(escapeJsonString(java.util.Base64.getEncoder().encodeToString(bytes)));
    writer.print('"');
    return;
}
```

与 §4.91（Round 82 给 `Decimal` 加的分支）同一形状：**在 `writeBeforeValue()` 之后直接写值，绝不递归**。

**实测**：`460 → 444`，修 **16** / 坏 0；
`Tests run: 7902, Failures: 209, Errors: 235, Skipped: 18`（聚合行恰好 1 次）。
纯 compat 改动（无需重新生成），`mvn-build-r156.log`。

**纪律**：`writeValue(Object)` 中"委托给另一个 `writeValue` 重载"的分支都会二次调用
`writeBeforeValue()`。凡是"**Expected 侧** JSON 语法明显不对"的失败，先查 compat 的 Newtonsoft 替身
（`JsonTextWriter`/`JsonConvert`），不要查被测的 `Utf8JsonWriter`。

### 4.96 Round 86：Newtonsoft 替身忽略 `StringEscapeHandling.EscapeNonAscii`（444 → 436，修 8 / 坏 0）

**入口**：`Utf8JsonWriterTests#highSurrogateMissingGetsReplaced(boolean, boolean)[1..4]`；
顺带修好同站点的 `Utf8JsonWriterTests#escapeCharacters(boolean, boolean)[1..4]`。

**先确认归一化器做了什么**（关键，否则会把结论搞反）：
`JsonTestHelper.assertContentsAgainstJsonNet` → `WriterHelpers.normalizeToJsonNetFormat`，
而它是**纯字符串变换**（生成物 33-59 行）：把 `\uXXXX` 的十六进制**转小写**、
把 `\/` 换成 `\u002f`，**不做解析/重排**（与 §4.91 的 `Decimal` 簇当时的判断不同）。所以本站点是**原文对比**。

**症状**：

```
Expected: { "a𐀀𐹭𝄞\U0010ffffa": "a𐀀𐹭𝄞\U0010ffffa" }      ← 原始星光字符
Actual:   { "a\ud800\udc00\ud803\ude6d\ud834\udd1e\udbff\udfffa": … }  ← 转义形式
```

**C# 原文**（`Utf8JsonWriterTests.cs:4455`）：
`string expectedStr = GetEscapedExpectedString(prettyPrint: formatted, propertyName, value, StringEscapeHandling.EscapeNonAscii);`
—— 期望串由 compat 的 `JsonTextWriter` 生成。而 compat 的 `escapeJsonString` 只判
`Default` 与 `EscapeHtml` 两种：

```java
boolean escapeHtml = stringEscapeHandling == StringEscapeHandling.EscapeHtml;
if (ch == '"' || ch == '\\' || ch < 0x20 || (escapeHtml && isHtmlSensitive(ch))) { … }
```

`EscapeNonAscii` 因此**完全不生效**，非 ASCII 全部原样写出 ⇒ 期望侧永远不带转义 ✗。
（`escapeCharacters` 那一簇用的是同一条期望侧路径，所以一起被修掉。）

**修法**（compat，两处判定各加一个条件）：

```java
boolean escapeNonAscii = stringEscapeHandling == StringEscapeHandling.EscapeNonAscii;
// 扫描阶段
if (ch == '"' || ch == '\\' || ch < 0x20
    || (escapeNonAscii && ch > 0x7F) || (escapeHtml && isHtmlSensitive(ch))) { … }
// 写出阶段
if (ch < 0x20 || (escapeNonAscii && ch > 0x7F) || (escapeHtml && isHtmlSensitive(ch))) {
    sb.append(String.format("\\u%04X", (int) ch));
```

按 UTF-16 码元逐个转义、**大写**十六进制 —— 规范化器会转成小写，正好与被测 writer
（默认编码器）输出的小写 `\ud800\udc00` 对齐；合法代理对因此写出两个转义而不是被合并。

**探针** `probe/w42/EscNonAsciiProbe.java`：

```
修复后 EscapeNonAscii = { "a\uD800\uDC00\uD803\uDE6D\uD834\uDD1E\uDBFF\uDFFFa": "…" }
       Default        = { "漢字": "漢字" }          ← 默认侧不变（不回归）
```

**实测**：`444 → 436`，修 **8** / 坏 0（`highSurrogateMissingGetsReplaced` 4 + `escapeCharacters` 4）；
`Tests run: 7902, Failures: 201, Errors: 235, Skipped: 18`（聚合行恰好 1 次），`mvn-build-r157.log`。
纯 compat 改动，无需重新生成。

**踩坑（Java 语言层）**：在 Java 源文件的**注释**里写 `\uXXXX` 字样会直接编译失败
（Unicode 转义在词法分析之前处理，报"非法的 Unicode 逃逸"）—— 本轮第一次编译就是这样挂的，
注释里必须写成文字描述（"backslash-u escape"）。
