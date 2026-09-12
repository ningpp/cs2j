# 转换后 Java 工程单测失败 — 系统性全面修复计划

> 目标：把 `d:\cs-json-0911-sdk` 的 `mvn clean install -e` 从当前
> `Tests run: 7902, Failures: 425, Errors: 430, Skipped: 18`（855 条失败）
> 推进到 **0 失败 + BUILD SUCCESS**，且 `D:\code\cs2j` 本身的 dotnet 单测全绿。
> 约束：**绝不改** `D:\csharp-sdk\json` 与 `d:\cs-json-0911-sdk` 的任何源码；
> 所有修复只能落在**转换器**（`D:\code\cs2j\src\...`）或**compat 库**
> （`D:\code\cs2j\java\csharptojava-compat`）。跑 dotnet/mvn 一律 240s 超时。

---

## 一、测量方法论（先立规矩，否则所有收益都不可信）

本轮已发现并修正三个会让结论翻车的陷阱，必须固化为纪律：

| 陷阱 | 现象 | 对策 |
|---|---|---|
| **控制台日志被截断** | `mvn ... > log` 的失败清单只列 250 条；据此得出"`Utf8JsonWriterTests` 61 条全是时区格式问题"的**错误结论** | 一律以 `*/target/surefire-reports/TEST-*.xml` 为准（`tools/triage_xml.py`） |
| **多行 Expected/Actual** | 用 `^Expected: (.*)$` 单行正则会截断，导致差异定位错误 | 用 `tools/triage_full.py` 取到 `^\s+at ` 之前的完整块 |
| **套件计数漂移** | 同一份源码两次全量分别得到 855 / 812 条失败（共享可变状态导致顺序敏感） | 只看**逐用例集合差**：`tools/passfail.py`（`snapshot` / `diff`），新增失败一处都不许有 |
| **并发覆盖报告** | 多个 `mvn` 同时跑会互相覆盖 `surefire-reports` | 全量测量串行执行；改完先 `snapshot` 再改 |

专用工具（本轮新建，均已验证）：

```
tools/triage_xml.py     # 权威分簇：读 surefire XML -> triage-xml.json（855 条）
tools/triage_report.py  # 每个簇的规模/测试类分布/最深 SDK 帧/样例
tools/triage_full.py    # 多行 Expected/Actual 完整提取 + 首个差异位置
tools/passfail.py       # snapshot / diff：逐用例集合差，输出 KEEP/REVERT 判定
```

**每轮的标准闭环**（沿用 `convert-prompt-json.md` 的 Step 1→8）：

1. `python tools/passfail.py snapshot before-<tag>`
2. 只改**一处**转换器/compat 逻辑
3. 重新转换：`dotnet run -c Release --project src/CSharpToJava.CLI/CSharpToJava.CLI.csproj -- convert-project -s D:\csharp-sdk\json -d d:\cs-json-0911-sdk --extra-deps io.github.ningpp.system-private-xml:0.0.1-SNAPSHOT --no-verify-build --no-verify-tests`（240s）
4. compat 有改动则先 `mvn -o install "-Dmaven.repo.local=D:\mvnrepo"`（240s）
5. `mvn -o clean install -e > mvn-build-r<NN>.log`（240s）
6. `python tools/passfail.py snapshot after-<tag>` + `diff` → **KEEP 需满足：新增通过 > 0 且新增失败 == 0**
7. 不满足则回退并记录被否假设；满足则跑 `D:\code\cs2j` dotnet 全量单测后 `git commit`

---

## 二、当前失败画像（855 条，基线 `mvn-build-r140.log`）

### 2.1 按异常类型

| 条数 | 异常 | 性质 |
|---:|---|---|
| 424 | `AssertionError` | 断言不符（值/异常/空值） |
| 165 | `JsonException` | 反序列化失败，**多数由类型擦除引起** |
| 144 | `ClassCastException` | 读/写用了错误的元素转换器 |
| 22 | `UnsupportedOperationException` | 缺校验 / 不支持的类型 |
| 21 | `TargetInvocationException` | 反射包装吞掉了原始异常类型 |
| 21 | `ArrayIndexOutOfBoundsException` | 空集合读取（多为连带失败） |
| 16 | `IllegalStateException` | 本该在校验处抛出 |
| 14 | `NullPointerException` | 多为"本该抛 InvalidOperationException" |
| 12 | `IllegalArgumentException` | 反射成员（属性/字段）不匹配 |
| 2 | `StackOverflowError` | 引用循环未处理 |

### 2.2 按测试类（前 15）

`CollectionTests` 159 · `ReferenceHandlerTests` 73 · `PropertyVisibilityTests` 69 ·
`CustomConverterTests` 68 · `ObjectTests` 66 · `Utf8JsonWriterTests` 61 · `SpanTests` 47 ·
`ArrayTests` 38 · `ExtensionDataTests` 34 · `DictionaryTests` 31 · `Utf8JsonReaderTests` 27 ·
`PropertyNameTests` 21 · `JsonDocumentTests` 19 · `NumberHandlingTests` 17 · `OptionsTests` 16

### 2.3 按根因簇（前 20，含最深 SDK 帧）

| 条数 | 簇 | 主要测试类 | 最深 SDK 帧 |
|---:|---|---|---|
| 68 | 该抛未抛（`Assert.throws`） | CustomConverterTests:17, CollectionTests:14, PropertyVisibilityTests:10 | 测试断言点 |
| 55 | `JsonConvert CSharpList` | ArrayTests:20, SpanTests:15, ObjectTests:11 | `JsonConverter1.readCore:130` |
| 38 | `Assert.equal` | **Utf8JsonWriterTests:27**, JsonDocumentTests:3 | `JsonTestHelper.assertContentsAgainstJsonNet:720` |
| 34 | `ClassCast Integer -> String` | CollectionTests, Utf8JsonWriterTests | `StringConverter.write:33` |
| 30 | `Assert.null` | 多个 `verify()` | 测试断言点 |
| 30 | `JsonConvert String` | ReferenceHandlerTests:15, SpanTests:15 | `JsonConverter1.readCore:130` |
| 22 | `Assert.true` | Utf8JsonReaderTests:19 | `Utf8JsonReaderTests.testJsonReaderUtf8:270` |
| 21 | `TargetInvocationException` | ExtensionDataTests:16 | `compat.MethodInfo.invoke:112` |
| 21 | `ArrayIndexOutOfBounds` | ObjectTests:16 | `ClassWithComplexObjects.validateArray:57` |
| 19 | `ClassCast JsonElement -> Integer` | CollectionTests:18 | 测试断言点 |
| 18 | `Assert.contains` | OptionsTests:5, JsonPropertyTests:3 | `OptionsTests.customEncoderAllowLatin1Supplement:376` |
| 16 | `JsonConvert CSharpDictionary` + `CSharpHashSet` | CollectionTests, ReferenceHandlerTests | `DictionaryDefaultConverter.onTryRead:162` |
| 13 | `ClassCast CSharpList -> String` | ObjectTests:7, ArrayTests:5 | `StringConverter.write:33` |
| 13 | `ClassCast SimpleTestClass -> String` | ObjectTests:9, ArrayTests:4 | `StringConverter.write:33` |
| 11 | `ClassCast JsonElement -> String` | PropertyNameTests:10 | `StringConverter.write:33` |
| 11 | `IllegalStateException (无消息)` | JsonPropertyTests:5 | `JsonElement.checkValidInstance:950` |
| 9 | `Throws IllegalStateException -> JsonException` | PropertyVisibilityTests:7 | 测试断言点 |
| 7 | `JsonConvert Integer` | CustomConverterTests:4 | `JsonConverter1.readCore:130` |
| 7 | `Expected InvalidOperationException when trying to get string value` | Utf8JsonReaderTests:7 | `testingGetStringInvalidUTF8:6503` |
| 6 | `JsonConvert SampleEnum` | 多处 | `EnumConverter.read:93` |

> 完整分簇见 `systematic-fix-evidence.md` 与 `triage-xml.json`。

---

## 三、簇清单与任务卡（按优先级）

### P0-1 泛型实参擦除 → 约 147 条（**最大单簇，且是多个其他簇的共同上游**）

**根因（已钉到代码行）**：`JsonClassInfo.getConverter` 计算 `runtimeType` 时用裸类，
`isSameType(CSharpList, CSharpList)` 成立 → 属性的 `runtimePropertyType` 退化为裸 `CSharpList.class`；
随后 `JsonPropertyInfo.getRuntimeClassInfo()` 的
`getOptions().getOrAddClass(getRuntimePropertyType())` 在
`ConcurrentHashMap<Class, JsonClassInfo> _classes` 里只能按裸类命中，
于是 `List<List<short>>` 复用了 `List<string>` 的 `JsonClassInfo`，内层数组被 `StringConverter` 读。

**证据**：`sdk-error-analysis.md` "Iteration 76"（含遍历 trace：`MyInt16TwoDimensionList runtime=class CSharpList element=class java.lang.String`）。

**已证伪的两条捷径（勿重试）**：
- 只把 `_classes` 的 key 改成实参感知 key → 无效（进来的就是裸类）。
- 只把 `_converters` 的读侧改成与写侧同 key（并去掉 `tryAdd` 里的 `map.remove(key)`）→
  全量 855 → 944，反而变差（预热阶段不写缓存，读侧裸类兜底把串味转换器重新放出来）。

**修复方向**：让 `getRuntimeClassInfo()` 查询时带上成员的闭合泛型实参。
`PropertyInfo.getPropertyType()` 已把实参发布到 `TypeHelper.MEMBER_ARGUMENTS`，
`JsonClassInfo.getConverter` 也已能通过 `getGenericArguments` 拿到实参。
落点：`ProjectPasses` 的生成物补丁通道（见 `PatchIEnumerableConverterCreateCollection` 的写法）
新增对 `JsonPropertyInfo.java` 的改写，把
`getOptions().getOrAddClass(getRuntimePropertyType())`
换成带实参的 compat 入口（如 `TypeHelper.classInfoForMember(options, getType(), getRuntimePropertyType())`）。

**验收**：`CollectionTests.writeIListOfIList`（写侧 Integer→String）、
`ObjectTests.readSimpleClass`（读侧 `MyInt16TwoDimensionList`）、
`SpanTests.readFromStream(Class, byte[])` 全部转绿，且 `passfail.py diff` 新增失败为 0。

**预估**：难度高，收益 ~147 条。这是全局最大杠杆。

---

### P0-2 `Utf8JsonWriterTests` 写出格式群 → 61 条（**5 个独立小 bug，性价比最高**）

初版误判为"1 个时区格式问题"，XML 复核后实为 5 类：

| 条数 | 差异 | 断言位置 |
|---:|---|---|
| ~20 | `\u0080`–`\u009F` 被转义为 `\u0080`，.NET 期望**原样输出**这些 Latin-1 字符 | `escapeCharacters` |
| ~12 | 写出的数字被**多加引号**：期望 `["1234567.0", …]`，实际 `[1234567.0, …]` | `writeNumberValueDecimal` |
| ~10 | 整数被写成浮点：期望 `1000`，实际 `1000.0`；期望 `1`，实际 `1.0` | `writeNumber_*` |
| ~8 | 非法 UTF-16 代理项未替换为 `\ufffd` | `utf16SurrogatePairReplacement_*` |
| ~6 | `escapeCharacters` 的 `IndexOf` 语义：期望 `1`，实际 `-1` | `writeStringInvalidCharacter` |
| 2 | `Z` vs `+00:00`（**只占 2 条**） | `writeString_NullPropertyName_DateTimeOffset` |

**修复方向**：前三类在 compat 的 `JavaScriptEncoder` / 数值写出路径
（`JsonEncodedText`、`Utf8JsonWriter.writeNumberValue*`、`WriterHelpers.normalizeToJsonNetFormat` 附近的语义）；
第 4 类在 UTF-16 解码/替换；第 5 类在编码器的 `IndexOf`。

**预估**：难度低-中，收益 61 条，彼此独立，可拆成 5 个独立小任务逐个 KEEP。

---

### P0-3 集合"无法实例化"群 → 约 30 条

`The collection type 'X' is abstract, an interface, or is read only`：
`CSharpObjQueue`/`CSharpObjStack` 各 4、`CSharpArrayList` 3、`WrapperForIList` 3、`StringHashSetWrapper` 2 …
出处在 `ThrowHelper.throwNotSupportedException_CannotPopulateCollection` 的调用方，
compat 侧的 `TypeHelper.newNonGenericCollection` 覆盖不全。

**修复方向**：补齐 compat 的可实例化集合表，或让 converter 的
`PatchIEnumerableConverterCreateCollection` 覆盖更多形态。
**预估**：难度中，收益 ~30。

---

### P0-4 异常语义系统偏移 → 约 100 条

- 68 条该抛未抛（缺校验）；
- `Throws … -> JsonException`(15)、`-> NullPointerException`(8)、`-> TargetInvocationException`(2)：
  读路径把 `InvalidOperationException`/`NotSupportedException` **包成** `JsonException`，
  或反射包成 `TargetInvocationException`，断言拿到的类型不是映射后的
  `IllegalStateException`/`UnsupportedOperationException`。

**修复方向**：① 统一异常映射表；② 读路径只在真正的解析错误处包 `JsonException`；
③ 反射调用点解包（`compat.MethodInfo.invoke` 是 21 条 `TargetInvocationException` 的唯一出点）；
④ 补缺失的校验抛点。
**预估**：难度中，收益 ~100。可与 P0-2/P0-3 并行。

---

### P1 组（收益 10–35 条/项）

| 簇 | 条数 | 线索 |
|---|---:|---|
| `JsonElement -> Integer/String/Comparable` 转换 | 33 | `CollectionTests` 18，多为 `IEnumerable`/`Immutable` 读取后类型不对 |
| `StringConverter.write:33` 写侧串味 | ~40 | 与 P0-1 同机制，P0-1 修好后需重新计数 |
| `Assert.null` 群 | 30 | 多为 `verify()` 里"本应为 null 却非 null"，常见于扩展数据/可空处理 |
| `TargetInvocationException` (`compat.MethodInfo.invoke:112`) | 21 | 反射解包（与 P0-4 ③ 同一处） |
| `ArrayIndexOutOfBounds: Index 0 out of bounds for length 0` | 21 | `ObjectTests` 16，`ClassWithComplexObjects.validateArray:57`，读出的数组为空 |
| `Assert.true` (`Utf8JsonReaderTests.testJsonReaderUtf8:270`) | 22 | reader 底层语义 |
| `ExtensionDataSignature` / 扩展数据写出 | ~10 | `IDictionary<string, JsonElement>` 签名判定 |
| `ReferenceHandlerTests` `$id/$ref/$values` | ~35 | 引用保留、循环（含 2 条 `StackOverflowError`） |
| 字典键类型不支持 / `CSharpDictionary -> CSharpIDictionary` | 10 | `Object` 作键 + 接口未实现 |
| 多维数组 `[[I` 不支持 | 3 | 锯齿 vs 矩形数组的建模决策 |
| `IllegalArgumentException: public ... CSharpGenericIList/CSharpDictionary ...` | 5 | 反射把非成员当成员暴露 |
| `EnumConverter.read:93/155` + `Integer -> Byte/Short` | 11 | 枚举反序列化与后备类型 |
| `PropertyVisibilityTests` | 69 | 非公开成员的可见性/序列化规则 |

---

## 四、推进阶段

**阶段 0（0.5 天）— 建立可信度量**
- 已完成 `tools/*` 四件套 + `tools/passfail.py`。
- 重新跑一次**独占**的全量构建并 `snapshot baseline`，作为唯一基线（当前 `surefire-reports` 已被并发构建搅乱）。
- 把 P0-1 的失败清单固化成回归用例集（约 10 个代表用例），后续每轮先跑它。

**阶段 1（1–2 天）— P0-2 写出格式群**
5 个独立小任务，逐个 KEEP。目标：855 → ~795，且新增失败 0。
理由：彼此独立、定位清晰、收益/风险比最高，可快速建立"每轮净正"的节奏。

**阶段 2（2–4 天）— P0-1 泛型实参擦除**
先做**最小验证**：只改一处（`JsonPropertyInfo.getRuntimeClassInfo`）跑 `ObjectTests` 单类，
确认 `readSimpleClass` 转绿再铺开。这是全局最大杠杆，也是 P1 中"写侧串味"簇的上游。
风险：改动落在核心热路径，需重点看 `passfail.py diff` 的回归列。

**阶段 3（2–3 天）— P0-3 + P0-4**
集合实例化 + 异常语义。二者互不依赖，可交替推进。
异常语义这一项要先补"缺失校验"清单（68 条逐条定性：产品缺陷 / 测试期望），再动手。

**阶段 4（3–5 天）— P1 组**
按"条数 × 定位清晰度"排序推进：反射解包 → `JsonElement` 转换 → 引用保留 →
扩展数据 → 字典键/多维数组 → 可见性 → 枚举。

**阶段 5 — 收口**
- `mvn -o clean install -e` 必须 `BUILD SUCCESS` 且 0 失败；
- `D:\code\cs2j` 的 dotnet 全量单测全绿；
- 复跑 compat 自身的 1717 个单测；
- 更新 `sdk-error-analysis.md`，`git commit`。

---

## 五、风险与对策

| 风险 | 对策 |
|---|---|
| 计数漂移让"净正"判定失真 | 只用逐用例集合差；新增失败 > 0 一律 REVERT |
| 并发构建互相覆盖报告 | 全量构建串行；改前 `snapshot` |
| 改动落在热路径引发大面积回归 | 每个 P0 任务先做单类/单模块验证，再全量 |
| 反射/擦除类修复互相影响 | 一次只改一处，KEEP 后再叠加下一处 |
| 修好 P0-1 后 P1 计数变化 | 每阶段结束重新跑 `triage_xml.py` 刷新分簇，不沿用旧数字 |
| 转换器补丁与后续 pass 冲突 | 补丁集中在 `ProjectPasses` 的生成物补丁通道，带正则精确锚点 + 失败即跳过 |

---

## 六、下一步（本轮之后的第一个动作）

> **本节已按 Round 1–34 的实测结果重写。** 原"先做 P0-2、再做 P0-1"的排序已被数据推翻：
> 最大收益来自**跨用例注册表污染**（Round 19，−67），而不是最先预估的写出格式群。

### 6.1 当前基线（已验证）

`Tests run: 7902, Failures: 430, Errors: 293, Skipped: 18` = **723**（起点 855，累计 **−132**）。

已 KEEP 的验证性提交（每一条都经过"全量 + 逐用例集合差 + 完整再生"验证）：

| 提交 | 内容 | 收益 |
|---|---|---:|
| `0665a07b` | `getType` 解析 C# 程序集限定名 + 接口传递性遍历 | −13 |
| `ff7e08dc` | 闭合泛型数组目标走 TypeReference + compat 支持 `GenericArrayType` | −20 |
| `c5bb7de0` | 写路径元素属性取自成员（3 个调用点） | −11 |
| `2b6bab6c` | 类信息查找按**成员实参**命中（只读探针） | −4 |
| `e8a29f38`+`60a56e0a` | **`serialize` 入口清空 per-thread 注册表** | **−67** |

并修掉一个**工程性缺陷**：`ef568d4f` —— 补丁分派用文件名后缀，
`"JsonPropertyInfo.java".endsWith("PropertyInfo.java")` 为真导致补丁被吞、
**只在手工改过的树上生效**；已修顺序 + 加"失配即报错"。

### 6.2 已**机制级否定**的方向（勿再投入）

| 方向 | 否定依据 |
|---|---|
| 转换器缓存"读写同 key" | 855 → 934（两次复现） |
| 元素类信息按实参作用域查找 | 855 → 934 |
| `DateTimeOffset` 往返格式 `XXX`→`xxx` | 1 修复 / **9 回归** |
| 读路径急切建类信息条目 | Round 12、Round 33 **两次更差** |
| 预热期修正成员实参槽位 | Round 34**正确但中性**（探针证实槽位已对，失败数不动） |
| 构造函数守卫（4 个变体） | 守卫本身 +16；"顺序依赖""过度拒绝""位置错误"三种归因**逐一被否证** |
| 字典裸 token 的实参推导 | 裸 `CSharpDictionary.class` **不携带实参**，`registerConcreteType` 无效 |
| `ObjectConverter.readWithQuotes` 放宽 | 三测试类失败总数**一字不差**，只是 Error→Failure |

### 6.3 剩余最大簇与**观察优先**的下一步

`JsonConvert CSharpList` **65 条**（`SpanTests` 23、`ArrayTests` 19、`ObjectTests` 11、
`ReferenceHandlerTests` 9），路径形状全部是 `$.MyInt16TwoDimensionList[0]`。

**下一步不是再猜机制，而是先观察**（Round 34 已明确要求）：
把 Round 34 的预热期槽位修正**保留在树上**，再跑 Round 31 的探针，
**实测"槽位正确之后，二维属性在读取时解析到了哪一格"**——
即打印 `_runtimeClassInfo` 的实际 `elementType` 与 `_classes` 中的键集合，
用观测结果决定下一步，而不是从机制反推。

Round 31 已确证的事实（可直接复用）：该成员的 **computed key 完全正确**
（`CSharpList[CSharpList<Integer>]`），但 `found=false` —— **条目在该次查找时不存在**。

### 6.4 已知限制（建议接受并转投他处）

裸 token 字典簇（5 项 + `DictKeyNotSupported` 小簇）：信息在 Java 侧**本就不存在**，
运行时无法推导；正确出路只有生成侧改传 `TypeReference`。

`Utf8JsonWriterTests` 61 条：根因在**测试预言机**（`JsonTextWriter` 移植）而非 writer 输出。

## 八、交接摘要（Round 37，供后续会话直接接手）

### 8.1 状态

- **基线**：`Tests run: 7902, Failures: 407, Errors: 295, Skipped: 18` = **702**（起点 855，**−153**）。
- 本轮（Round 41）在诊断之外**新增两条 KEEP**（均"逐用例集合差 +0 回归"）：
  - `e777d254` 把 C# `[JsonPropertyName]` 送进运行时 → **723 → 709（修 14 / 坏 0）**；
  - `5540ec46` 非公开访问器可见性（`CompatNonPublicAccessor` + `[JsonInclude]` 例外）
    → **709 → 702（修 7 / 坏 0）**。
- 工作树干净；所有改动已提交。
- **本轮新增的两条可复用"桥"模式**：C# 特性在生成物里是**类**而不是注解，所以
  `CustomAttributeExtensions.getCustomAttribute` 看不到它 —— 转换器发一个
  `@CompatXxx` 标记，compat 再从标记**物化**出那个特性类。
  已有：`CompatJsonIgnore`、`CompatJsonInclude`、`CompatJsonExtensionData`、
  `CompatJsonNumberHandling`、`CompatPropertyName`、`CompatSByteArray`、
  `CompatNonPublicProperty`；**本轮新增** `CompatJsonPropertyName`（带值）、
  `CompatNonPublicAccessor`（按访问器）。
  **凡是"C# 语义没生效"的簇，优先查这个通道缺了哪个标记。**

### 8.2 已闭合的开放问题（Round 41：创建层测量做完了）

**Round 39 的结论经 Round 41 的探针证实并补全**。三个探针（全部插在生成物里、测完即撤）
给出了完整因果链：

1. `getOrAddClass(CSharpList)` 在整个探测过程中**只被进入一次**，键是
   `CSharpList[class java.lang.String]`——即 `List<string>` 成员的键。二维键
   `CSharpList[CSharpList<Integer>]` **从未被创建**（确认了 Round 39）。
2. 二维属性的属性信息本身是**对的**：`addProperty MyInt16TwoDimensionList … converter=ListOfTConverter`，
   且 `PropertyInfo.getPropertyType()` 在反射时已发布**正确的实参**
   `CSharpList<CSharpList<Integer>>`（`registerMemberType` 也把它记到了成员上）。
3. **断点在读时的实参上下文**：`getElementClassInfo` 打印出
   `owner=CSharpList ownerArgs=[] elem=java.lang.Object`——类信息创建时用的那层实参
   在**读取时已经不存在**（被后一个成员覆盖），所以元素退化成 `Object`，
   嵌套列表用错转换器读，报
   `Cannot get the value of a token type 'StartArray' as a string`。
   追加探针证实：读时 `currentGenericArgumentsLayer(CSharpList)` 返回
   `[class java.lang.String]`——**`List<string>` 成员的实参**。

**结论**：这不是"查找选错格子"，也不只是"条目没被创建"，而是
**创建与消费之间隔着一个会被后续成员覆盖的、每线程单槽的实参注册表**。
问题已闭合；下面两条已实现并实测，**净负，已回滚**（见 §8.2.1）。

### 8.2.1 已实测并回滚（Round 41，勿重复）

以**同一棵再生树的干净基线**为参照（这一点很关键，见 §8.3 的"基线必须同源"）：

| 尝试 | 干净基线 | 结果 | 判定 |
|---|---|---|---|
| 成员键**创建**类信息 + 按自身实参推导元素类型 | 737 | 756（修 20 / 坏 39） | **回滚** |
| 仅成员键创建（`getOrAddClass` 委托） | 737 | 766（坏 44） | **回滚** |
| 仅元素类型按实参推导 | 737 | 757（修 3 / 坏 37） | **回滚** |

失败集中在 `SpanTests#read(Class,byte[])[1][2][24][28..32]`、
`ReferenceHandlerTests#readTestClassesWithExtensionOption(...)` 同索引、
`ObjectTests#write(ITestClass)[9..32]`、`DictionaryTests#hashtable`、
`CollectionTests#readSimpleClassWithImmutableArray`、
`ExtensionDataTests#nestedClassWithJsonElementExtensionDataProperty`。
**其中 `readGenericIEnumerableOfIEnumerable`、`readGenericIReadOnlyListOfGenericIReadOnlyList`
等在隔离运行下也失败，是真回归**（不是顺序噪声）；`hashtable` 隔离下通过。

机理：成员键创建会**多出一个 `_classes` 条目**，而按实参推导元素类型会把
字典/接口/抽象集合的元素位换成键或接口类型（`ImmutableDictionary` 的元素从
`String` 变成 `Object`；`IReadOnlyList<IReadOnlyList<int>>` 的元素被换成接口，读取即失败）。

### 8.2.2 已排除的"读侧"尝试（勿重复）

| 尝试 | 轮次 | 结果 |
|---|---|---|
| 读路径 miss 时急切建条目 | R12（无写侧）、R33（有写侧） | **两次更差** |
| 预热期修正成员实参槽位 | R34 | **正确但中性**（探针证实槽位已对，失败数不动） |
| 只改读侧缓存键读写一致 | R1 | 855 → 934 |
| 元素类信息按实参作用域查找 | R1 | 855 → 934 |
| 成员键创建 + 自身实参元素类型 | R41 | **737 → 756**（本节） |

### 8.3 必须遵守的操作纪律（都是踩过的坑）

| 纪律 | 原因 |
|---|---|
| 判定用**逐用例集合差**，不看总数 | 同源码两次全量会有 16–43 的漂移；站点级差异常是同一失败的断言行位移（假回归） |
| 计数以 `mvn -e` 的聚合清单为准 | 本仓库 surefire XML **不完整**（部分类只落 1 个 testcase） |
| 每次改完**先全量重新生成再测** | 手工改过的生成物**不是干净基线**；Round 10 曾因此得到假的"再生验证通过" |
| 移除生成代码插桩要用**针对块的 edit** | 按行过滤会删掉续行并破坏文件（Round 36 踩过），恢复办法是删文件后重新生成 |
| 新增补丁分派时**具体后缀在前** | `"JsonPropertyInfo.java".endsWith("PropertyInfo.java")` 为真，会把补丁静默吞掉 |
| compat 不能用生成类型，用**反射** | 形成环状依赖 |
| 转换需作为**后台任务**运行 | 超过 240s |
| **基线必须与当前生成树同源** | Round 41 踩过：拿 Round 40 存下的失败集去比对一棵**重新生成过**的树，得出"坏 39"里有假项。正确做法是把本轮改动用开关关掉、**在同一天同一棵树上测一遍**得到干净基线，再开关对比 |
| **compat 改完必须强制重建** | `mvn -q install` 会因时间戳判定"无需重编"，`-q` 又吞掉输出，于是**测的是旧 jar**。Round 41 因此白测两轮。改用 `mvn -o clean install -DskipTests` 并**核对 jar 时间戳** |
| **探针要放在会真正执行的路径上** | comapt 的 `TypeHelper.trace` 是可用的，但它只在真被调用时才有输出；先确认目标方法在跑（`Count` 一下），再解读分支 |
| PowerShell 传 `-D` 要给整个开关**加引号** | `-Dcs2j.trace.typeargs=true` 被解析成别的东西，java 报"找不到主类"，看日志像"探针无输出" |

### 8.4 已否证方向清单

见 §6.2（8 条，含各自的实测数据）与 §4.34/§4.35 的两个"封案"结论。
**不要在未读 §6.2 的情况下重试这些方向。**

### 8.5 已建议接受的限制

- 裸 token 字典簇（5 项 + `DictKeyNotSupported` 小簇）：信息在 Java 侧不存在，运行时无法推导。
- `Utf8JsonWriterTests` 61 条：根因在测试预言机（`JsonTextWriter` 移植），不在 writer 输出。

## 七、待并入（并行子代理分析）

| 子代理 | 负责簇 | 状态 |
|---|---|---|
| nested-generic | P0-1 泛型擦除 + 读/写串味 | 运行中 |
| writer-dates | P0-2 `Utf8JsonWriterTests` | 运行中 |
| throw-validation | P0-4 异常语义 + 该抛未抛 | 运行中 |
| collections-dicts | P0-3 集合实例化 + 字典 + 多维数组 + `JsonElement` 转换 | 运行中 |
