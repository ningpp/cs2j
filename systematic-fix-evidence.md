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
| P1 | 数组目标类型走 TypeReference 路径 + compat 支持 `GenericArrayType`（**必须同批落地**） | 子代理实测覆盖 15 条 `JsonElement -> X`；单独落地会回归全部 15 条 |
| P2 | 元素类信息取自转换器元素类型（`options.getOrAddClass(toWrapperType(getElementType()))`） | 子代理实测 5 个 `IllegalArgumentException` → 3，2 测试转绿 |
| P3 | "该抛未抛" 68 条：逐条定性（产品缺陷 vs 测试期望）后补校验点 | 最大断言簇 |
| P4 | 引用保留 `$id/$ref/$values`（`ReferenceHandlerTests` 71，含 2 条 `StackOverflowError`） | 见 §2 |
| P5 | `Utf8JsonWriterTests` 61 条：**根因在测试预言机**，不是 writer 输出 | 见 §4.4 |

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
