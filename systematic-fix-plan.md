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

> **⚠️ 2026 更新（Round 70/71）**：本节的 65 条与写侧 15 条
> `ClassCast Integer -> String` 已被证实是**同一个簇**（合计 79 条，占剩余 14.7%）。
> 三条局部修法都已实测并回滚：成员侧闭键类信息 +37、元素侧闭键查询无效、
> 擦除类信息去上下文创建 +126。**该簇需要结构性方案**，见 §8.2.20 / §8.2.25 / §8.2.26；
> 下面的"先观察"步骤仍然有效，但**不要**再按单点补丁的思路改。

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

~~`Utf8JsonWriterTests` 61 条：根因在**测试预言机**（`JsonTextWriter` 移植）而非 writer 输出。~~

**该结论已于 Round 42 被实测推翻**：其中 24 条是真 bug，根因在 compat 的
`JavaScriptEncoder`（孤立代理项未转义为 U+FFFD）与 `Utf8Formatter`（用 Java 的
`Double.toString`/`Float.toString`，把 `1.0` 写成 `"1.0"` 而 .NET 写 `"1"`）。
教训见 §8.1 末条：**大面积同形断言差异先怀疑 compat 的格式化/编码实现**，
"归因于预言机"是本项目代价最高的一次误判。

## 八、交接摘要（Round 37，供后续会话直接接手）

### 8.1 状态

- **基线**：`Tests run: 7902, Failures: 193, Errors: 235, Skipped: 18` = **428**（起点 855，**−427**）。
- 本会话累计 **46 条 KEEP**（40 条"逐用例集合差 +0 回归"；3 条实测中性：`99151aa6`、`6a30e0ca`、`43914058`；
  `a62aac37` 修 40 / 坏 1 已披露；`387edb77` 修 13 / 坏 2 已披露，两者净收益分别为 +39、+11；
  `18e24418` 的 1 条暴露已由 `fbed68d4` 真正修掉）：

| 提交 | 内容 | 收益 |
|---|---|---|
| `e777d254` | 把 C# `[JsonPropertyName]` 送进运行时 | 723 → 709（修 14） |
| `5540ec46` | 非公开访问器可见性（`CompatNonPublicAccessor` + `[JsonInclude]` 例外） | 709 → 702（修 7） |
| `bdf54f07` | 孤立代理项（lone surrogate）转义为 U+FFFD | 702 → 692（修 10） |
| `01a85535` | 数字按 .NET 格式写（`DoubleHelper` + 新 `FloatHelper`） | 692 → 678（修 14） |
| `b38263ff` | U+FFFD 在所有编码器下都转义 | 678 → 674（修 4） |
| `2dce1154` | C# `internal` 构造函数在可访问性查询里被遵守 | 674 → 664（修 10） |
| `15828ffc` | `csFormat` 里 double 按 .NET 不变文化格式渲染 | 664 → 648（修 16） |
| `9317fc8a` | `Unsafe.as` 实参还原声明的源类型 | 648 → 638（修 10） |
| `316989da` | 成员级 `[JsonConverter]` 送达运行时（含派生特性） | 638 → 631（修 7） |
| `4c0be592` | 类型级 `[JsonConverter]` 送达运行时 | 631 → 628（修 3） |
| `d18e4827` | `ThrowHelper` 消息渲染 C# 泛型类型名 | 628 → 627（修 1） |
| `9d25d891` | BCL 兼容类按 C# 名渲染（`System.Collections.Generic.*`） | 627 → 626（修 1） |
| `06c47712` | `[JsonConverter(null)]` 哨兵 + 装箱基元按 C# 名渲染 | 626 → 625（修 1） |
| `7ca010f2` | 异常消息的 null 安全（C# 保证非 null，Java 返回 null） | 625 → 619（修 6） |
| `99151aa6` | 传播被调方异常（不再包成 `TargetInvocationException`） | 619 → 619（**实测中性**；扩展数据部分见 §8.2.11 更正） |
| `a62aac37` | struct 的 `clone()` 不再丢 final 字段 | 619 → 580（修 40 / 坏 1，已披露） |
| `387edb77` | `default(T)` 是否为 null 按**声明类型**判断（`IgnoreNullValues`） | 580 → 569（修 13 / 坏 2，已披露） |
| `67b4e3a2` | `default(T)` 是否为 null 改按**声明成员**判断（`int` vs `int?`、struct） | 569 → 568（修 1） |
| `18e24418` | 把 `[JsonIgnore(Condition = …)]` 送进运行时 | 568 → 558（修 11 / 暴露 1） |
| `fbed68d4` | `CSharpDictionary` 查表遵守构造时给的 comparer | 558 → 554（修 4，含上面那条暴露） |
| `cd8fe08a` | `default(JsonElement)` 的 `.clone()` 走复制而非 C# `Clone()` | 554 → 553（修 1，另 6 条换成了真实断言） |
| `a516e191` | `JsonElementConverter` 自己处理 null token（`CanBeNull=false`） | 553 → 548（修 5） |
| `f094d466` | `Converter.CanBeNull` 认得转换出来的 struct | 548 → 545（修 3） |
| `e489db87` | 参数异常的消息里带上 C# 的参数名 | 545 → 539（修 6） |
| `853e17ce` | 空 JSON 属性名的哈希（`getKey` 不再解包 null） | 539 → 534（修 5） |
| `e9d89bc1` | `DateTimeOffset` 的 `"O"` 始终写带符号偏移（`+00:00`，不是 `Z`） | 534 → 532（修 2） |
| `73b6fec7` | `Encoding.getString` 遵守 `throwOnInvalidBytes` | 532 → 524（修 8） |
| `4ba90036` | `getKey` 按真实字节算属性名键（旧实现只看首字节 + 长度） | 524 → 523（修 1，另 6 条换了失败原因） |
| `ac40ded2` | 字段枚举遵守 `NonPublic`；非公开属性没有公开 getter | 523 → 519（修 4） |
| `784c5b3a` | C# 非公开字段带标记（`internal` ≠ Java `public`） | 519 → 518（修 1，闭合整条 theory） |
| `6a30e0ca` | `KeyValuePair` 的实参只取一次 | 518 → 518（**实测中性**，作为潜在缺陷保留） |
| `0d424a94` | 多维数组 rank 标记在**缓存命中**路径上也要消费 | 518 → 513（修 5） |
| `7f1ac92a` | `Deserialize(ref reader)` 抛异常时**就地**回滚 reader | 513 → 503（修 10） |
| `160a641b` | 类型参数创建分支也应用**对象初始化器** | 503 → 499（修 4） |
| `8070db44` | 目标类型化 `new() { … }` 也应用对象初始化器 | 499 → 499（**实测中性**，作为潜在缺陷保留） |
| `d930d7ff` | `[JsonInclude]` 时能看见**非公开 setter** | 499 → 493（修 6） |
| `8c2c15ff` | 保留 C# `new InvalidOperationException()` 的**默认消息** | 493 → 490（修 3） |
| `756d6cf6` | `default(Utf8JsonReader)` 的空缓冲区（null → 空 span） | 490 → 488（修 2） |
| `310552db` | `default(T)`：C# 类 → **null**（只有 struct 才有零实例） | 488 → 470（修 18） |
| `2ece1c64` | `Type.GetProperty(name, flags)` 的**标志实参**送进 compat | 470 → 467（修 3） |
| `c2ce9417` | Newtonsoft 替身把 `Decimal` 按裸数字写（不是加引号的字符串） | 467 → 463（修 4） |
| `9c5b4d82` | 属性桥不再把**非公开成员**当成属性（含非公开 `Get*()` 方法） | 463 → 460（修 3） |
| `1eea495a` | Newtonsoft 替身写 `byte[]` 时**多了一次分隔符**（`writeValue(String)` 二次调用 `writeBeforeValue`） | 460 → 444（修 16） |
| `8b64cd02` | Newtonsoft 替身支持 `StringEscapeHandling.EscapeNonAscii` | 444 → 436（修 8） |
| `43914058` | `StreamWriter` 把非法 UTF-16 换成 **U+FFFD**（.NET 语义；本集合**实测中性**，按先例保留并披露） | 436 → 436（**中性**） |
| `a6b174d8` | 注释在**第一个未配对代理项处停止**（复刻 .NET `ToUtf8` 的静默截断） | 436 → 432（修 4） |
| `2bab7e62` | Newtonsoft 替身的**引号形式随 `StringEscapeHandling` 变**（`EscapeHtml`→unicode 转义，其余→短形式） | 432 → 428（修 4） |

- **第三条可复用通道（本轮确立）：`%s` / `string.Format` 的默认渲染。**
  C# 的 `string.Format("{0}", v)` 调 `v.ToString()`，其**数值格式与 Java 不同**：
  `30.0` 在 .NET 是 `"30"`、Java 是 `"30.0"`。凡是"整段文本只差一个 `.0`"的失败，
  都先查 compat 的格式化入口（`StringHelper.csFormat`、`Utf8Formatter`、
  `DoubleHelper`/`FloatHelper`）。已修 `csFormat`（17 个调用点，代价可控）。
  **已知未修**：超出 double 精确表示的整数（`9223372036854775807`）在 .NET 的最短
  往返 double 格式下仍是原整数文本，Java 给 `9.223372036854776E18` ——
  这正是 `testJsonReaderUtf8` 残留 2 例的原因，属**独立且有深度**的问题，
  不要顺手改 `DoubleHelper`（会波及所有数字输出）。

- 工作树干净；所有改动已提交。

- **本轮新增的两条可复用"桥"模式**：C# 特性在生成物里是**类**而不是注解，所以
  `CustomAttributeExtensions.getCustomAttribute` 看不到它 —— 转换器发一个
  `@CompatXxx` 标记，compat 再从标记**物化**出那个特性类。
  已有：`CompatJsonIgnore`、`CompatJsonInclude`、`CompatJsonExtensionData`、
  `CompatJsonNumberHandling`、`CompatPropertyName`、`CompatSByteArray`、
  `CompatNonPublicProperty`；**新增** `CompatJsonPropertyName`（带值）、
  `CompatNonPublicAccessor`（按访问器）、`CompatInternalCtor`（按构造函数）。
  **凡是"C# 语义没生效"的簇，优先查这个通道缺了哪个标记。**

- **同一模式的第二条通道：C# 可访问性 ≠ Java 修饰符。**
  Java 没有 `internal`，转换器统一发成 `public`（跨包可达），
  于是**所有"这个成员/构造函数公开吗"的运行时问题都会答错**。
  已修两处：`CompatNonPublicAccessor`（属性访问器）、`CompatInternalCtor`（构造函数）。
  **下一个同类目标**：字段/方法/类型的可访问性查询（目前未发现对应失败簇，
  但 `TypeHelper` 里其他 `Modifier.isPublic` 判断值得在新增失败簇出现时优先检查）。

- **一条被推翻的旧结论**：§6.4 曾建议把 `Utf8JsonWriterTests` 61 条"作为测试预言机问题接受"。
  **这是错的**——Round 42 从中拿到 24 条真修复（代理项转义 10 + 数字格式 14），
  根因都在 `JavaScriptEncoder` / `Utf8Formatter` 这两个 compat 类里。
  **凡是"大面积同形断言差异"，先怀疑 compat 的格式化/编码实现，不要先归因于断言或预言机。**

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

### 8.2.3 Round 43 已实测**中性**（勿重复）

Round 43 试了一条看起来最像"属性桥"缺口的线索，实测**逐用例集合完全不变**，已回滚：

| 尝试 | 结果 | 原因（已查明） |
|---|---|---|
| 把 `[JsonConverter]`（含 `typeof(X)`）物化成 `@CompatJsonConverter` + compat 物化 | **674 → 674，集合完全相同** | **成员级 `[JsonConverter]` 的通路根本没被走到** |

**为什么中性（这是本轮最有价值的发现）**：实测在
`CustomAttributeExtensions.getCustomAttributes` 上打点，`PocoWithInvalidConverter.MyInt`
**确实**被查了属性，但 `JsonConverterAttribute` 的查询**从未进入**
`materializeFromCompatAnnotation`。也就是说
`JsonSerializerOptions.determineConverter(memberInfo)`（第 273 行，成员级
`[JsonConverter]` 的唯一入口）在这条序列化路径上没有被调用；走的是
`getConverter(runtimePropertyType)`（第 279 行）→ 第 328 行的**类型级**查询。
所以 21 条 `CustomConverterTests` 的 "No exception thrown" **不是缺标记**，
而是**成员级转换器属性通路缺失**。

**下一步若要碰这一簇**：先确认 `determineConverter` 的调用点，而不是再加标记。

### 8.2.4 已闭合：C# `internal` 构造函数未被遵守（Round 44，已修，−10）

§8.2.4 定位的线索**已实施并验证**：`674 → 664（修 10 / 坏 0）`，提交 `2dce1154`。

- 症状：`read_Generic_NoPublicConstructor_Throws` **只有 `*InternalConstructor` 参数用例失败**，
  而 `*PrivateConstructor` 全部通过 —— 这个"一半通过"的切分就是定位依据
  （私有构造函数被如实发成 `private`，既有的守卫已经覆盖；`internal` 被发成 `public`）。
- 做法：新增 compat 标记 `CompatInternalCtor`，由 `ConstructorTransformer` 对
  "internal 且非 public" 的构造函数发出；`TypeHelper.getConstructorInfo` 遵守它。
- **本轮最有价值的坑**：只改 `getDeclaredConstructors` 回退循环**不够**——
  `type.getConstructor(...)` 的**快路径**在循环之前就 return 了，
  所以第一版实测**完全中性**。两处都要挡。用 `[cs2j.i] ENTER ... publicCtor=1`
  这一行才看清（`publicCtor=1` 说明 `getConstructor()` 拿到了构造函数）。

**残留**：同族还剩 8 条 `UnsupportedOperationException`（`ArrayTests.readPrimitiveArrayFail`、
`ExceptionTests.classWithUnsupportedArray*`、`NullTests.invalidRootOn*`、
`DictionaryTests.dictionaryWith_ObjectWithNoParameterlessCtor_AsValue_Throws`），
但**分散在 5 个类、形状各异**，不是同一个根因；勿当作一个簇处理。

### 8.2.7 Round 46 两次**中性**（勿重复那两个尝试）

**中性尝试（已回滚，勿重复）**——详见 §4.48：

| 尝试 | 结果 |
|---|---|
| `Unsafe.as` 装箱链里把窄化转换移到拆箱之后 | 修 10 / 坏 10，**净 0** |
| 去掉窄化转换，只留 `(Byte)(Unsafe.as(x))` | 修 0 / 坏 0，**集合完全相同** |

**Round 47 已查明这两次为何都不成立**（见 §8.2.5）：结果侧无解，
断点在实参。此处保留记录仅为防止有人重复"改结果侧"这条路。

**结构性线索（未实施，建议单独立项）**：
`ImmutableDictionary`/`ImmutableSortedDictionary` 的 cast 失败（14 条）与
`ArrayIndexOutOfBoundsException`（21 条）**很可能是同一个根因**——
compat 的不可变类型没建模成 `Iterable`/`Map`：

- `ImmutableDictionary<K,V> implements IImmutableDictionary<K,V>`
- `ImmutableList<T> implements IImmutableList<T>`

这些接口都不继承 `Iterable`/`Map`，于是
(a) 转换器 cast 成 `CSharpReadOnlyDict` 失败；
(b) `TypeHelper.isCollectionType` 判否 → 实参恢复失败 →
`getGenericArguments` 返回**空数组** → 工厂里 `[0]` 越界。

建议路线（.NET 真实层次）：`IImmutableList<T> extends CSharpReadOnlyList<T>`、
`IImmutableDictionary<K,V> extends CSharpReadOnlyDict<K,V>`。
**动手前先读 `IImmutableDictionary` 上那条"故意不做成 Iterable"的既有注释**
（转换器生成的包装类实现 `Iterable<CSharpKeyValuePair<K,V>>` 可能与 `Map.Entry` 迭代冲突），
确认该冲突是否仍然存在。这是接口层大改，**必须逐用例验证**。

### 8.2.5 已修：`Unsafe.as` 参数未还原声明的源类型（Round 47，−10）

提交 `9317fc8a`。**这是"先探运行时类型"的直接收益**——Round 46 靠猜表达式形状，
两次都中性；本轮先打印类型，一次命中。

探针输出（`probe/w42/UnsafeTypeProbe.java`）：

| 生成的实参 | `Unsafe.as(x)` 的实际返回 | 后续转换 |
|---|---|---|
| `int` 变量 | `java.lang.Integer` | `(Byte)(…)` 与 `(Byte)((byte)(…))` **都 CCE** |
| `byte` 变量 | `java.lang.Byte` | `(Byte)(…)` 可以，`(byte)(…)` **CCE** |
| `short` 变量 | `java.lang.Short` | 同上形状 |

**关键推理**：Java 里**没有**任何转换能把 `Integer` 变成 `Byte`/`Short`；
而**同一个生成形状**必须同时服务"int 实参（需要窄化）"和"已经窄化过的实参
（再窄化会 CCE）"——所以**结果侧无论怎么改都不可能同时成立**。
这正是 Round 46 两次尝试分别得到 10/10 与 0/0 的原因。

**真正的断点在实参**：C# 显式写了 `Unsafe.As<TFrom,TTo>`，但编译器在调用前
已把 `byte`/`short` 局部变量**提升为 int**，于是 Java 实参是 int、
`Unsafe.as` 走了泛型 `Object` 重载并装箱成 `Integer`。
**把实参还原成声明的 `TFrom`**（`(byte)` / `(short)` 包裹），
每个分支就都自洽了：先窄化 → `Unsafe.as` 装箱的正是那个类型 → 单个 `(Wrapper)` 是恒等转换。

### 8.2.6 已修：`[JsonConverter]` 送达运行时（成员级 Round 48 −7，类型级 Round 49 −3）

提交 `316989da`（成员级）与 `4c0be592`（类型级）。**Round 43 的结论已推翻**：
那不是"成员通路没被走到"。

**成员级三个缺口必须一起补**（单独补任何一个都实测中性）：

1. 转换器发 `@CompatJsonConverter`，并**沿 C# 特性继承链**解析转换器类型
   （派生特性把类型放在基类构造调用里：`... : base(typeof(int))`，
   必须读构造函数初始化器，特性自身实参是空的）。
2. `PropertyInfo` 从标记物化出生成的 `JsonConverterAttribute`。
3. **`PropertyInfo` 覆写 `getCustomAttributes(Class, boolean)`** ——
   此前只实现了不带类型参数的版本，于是**每次带类型的查找都落到接口默认实现并返回空数组**，
   特性被构造出来后又被丢弃。**这就是 Round 43 误判的真实原因**。

**类型级**（Round 49）：`ClassTransformer` 对类型级 `[JsonConverter]` 也发标记，
`TypeHelper.materializeTypeAttributeFromCompatAnnotation` 增加对应分支，
复用同一个继承链解析器（`PropertyTransformer.TryGetJsonConverterType`，已改 internal）。

**残留（失败点已前移，方向明确）**：

- ~~`attributeOnPropertyFail` 需要 C# 泛型类型名~~ —— **Round 50 已修**（`d18e4827` + `9d25d891`）。
- ~~`attributeCreateConverterFail` 仍是 `No exception thrown`~~ —— **Round 51 已修**
  （`06c47712`：`[JsonConverter(null)]` 哨兵 + 装箱基元 C# 名）。

**该簇已全部收口**：`attributeCreateConverterFail`、`attributeOnClassFail`、
`attributeOnPropertyFail`、`converterWithoutDefaultCtor` 均通过。

### 8.2.7 Round 52 实测：不可变集合线索**一半被否证**

§8.2.7 曾提出"compat 的不可变类型没建模成 `Iterable`/`Map`，同时解释 cast 失败与
`ArrayIndexOutOfBoundsException`"。**探针（`probe/w42/ImmIfaceProbe.java`）实测结果**：

| 类型 | `Iterable` | `Map` | `CSharpReadOnlyDict` | 自身泛型参数 | `getGenericArguments` 长度 |
|---|---|---|---|---|---|
| `ImmutableList` / `ImmutableHashSet` / `ImmutableSortedSet` / `ImmutableStack` / `ImmutableQueue` / `ImmutableArray` | **true** | false | false | 1 | **1** |
| `ImmutableDictionary` / `ImmutableSortedDictionary` | false | false | **false** | 2 | 2 |

**结论：**
- **列表侧被否证**：它们**已经是 `Iterable`**，`getGenericArguments` 也**正常返回 1 个**实参
  （因为 `IImmutableList` 等接口**确实** `extends Iterable<T>`）。
  所以 `ArrayIndexOutOfBoundsException` **不是**这条线索造成的，别从这里查。
- **字典侧被证实**：两个字典的 `CSharpReadOnlyDict=false`，
  转换器工厂因此落到 `ImmutableEnumerableOfTConverter`，写路径再 cast 到
  `CSharpReadOnlyDict` 就抛 —— **与 14 条 cast 失败吻合**。

**但这条修复有既存阻碍（实测）**：给两个字典加 `implements CSharpReadOnlyDict` 后编译失败：

```
ImmutableDictionary 不是抽象的, 并且未覆盖 java.util.Collection 中的抽象方法 clear()
ImmutableDictionary 中的 clear() 无法实现 CSharpGenericIterable 中的 clear()   // 返回类型冲突
ImmutableSortedDictionary 未覆盖 CSharpReadOnlyDict 中的抽象方法 tryGetValue(K)
```

原因是 `CSharpReadOnlyDict` → `CSharpReadOnlyCollection` → `CSharpGenericIterable` →
**`java.util.Collection`**，于是接口强制要求 `add/remove/clear` 等**可变**方法，
而 `ImmutableDictionary.clear()` 返回 `ImmutableDictionary`（协变），与
`Collection.clear()` 的 `void` 冲突。**这正是 `IImmutableDictionary` 上那条
"故意不做成 Iterable"注释所指的同一类冲突。**

**因此该方向的正确做法是设计工作，不是补丁**（本轮已回滚，未提交）：
要么让 `CSharpReadOnlyDict` 不再继承 `Collection`（影响面大），
要么给字典加一层只读适配器/桥接。**动手前先确认这条改动不会波及
`isImmutableCollection` 与不可变集合工厂的判定。**

### 8.2.8 Round 52 实测：`Assert.null() ... Actual: 0`（29 条中约 11 条）不是可修缺陷

`Nullable<T>` 在 Java 侧映射为**装箱类型**（`int?` → `Integer`），
于是"C# 可空值类型的 null"与"装箱的 0"**在运行时不可区分**：
`Assert.null() ... Actual: 0` 这类失败（`nullableValueTypeConverter_OptOut`、
`defaultIgnoreNullValuesOnRead`、`nullableConverterIsNotPassedNull` 等）
是**转换模型的固有代价**，不是逻辑 bug。
**除非引入独立的 Nullable 包装类型（架构级改动），否则不要在这 11 条上投入。**

### 8.2.9 Round 53 已修：C# 保证非 null 而 Java 返回 null（−6）

提交 `7ca010f2`。**C# 的 `Exception.Message` 保证非 null**（无消息时为 `""`），
Java 的 `Throwable.getMessage()` 返回 **null**，于是直译的
`ex.Message.Contains(...)` / `String message = ex.Message;` 第一次触碰就抛 NPE。

两处站点（各自独立确认）：`JsonConverter1.readCore`/`writeCore` 的 "has a path" 检查
（→ `StringHelper.containsOrEmpty`），以及 `ThrowHelper.throwNotSupportedException`
（→ `StringHelper.nullToEmpty`）。

**教训**：同一语义缺陷常在同族文件里出现多次。修完第一处后
**必须重跑该用例并读新栈**——本轮第一次测量是 625 → 625（看似无效），
但栈已经从 `JsonConverter1:142` 换到 `ThrowHelper:749`，第二处才因此暴露。

### 8.2.10 Round 54：异常传播已修；**"−30" 是假测量（Round 55 更正）**

提交 `99151aa6`。**只有第 1 条是真实且已验证的修复**；第 2 条的"−30"结论
**已于 Round 55 被推翻**，详见 §8.2.11。

1. **`MethodInfo.invoke` 吞掉被调方异常（真修复，但仍为中性）**：
   `InvocationTargetException` 本身是 `ReflectiveOperationException` 的子类，
   被既有的通用 catch 吞掉；C# 的 `MethodInfo.Invoke` 原样传播。
   修法是加一个**排在前面**的 `InvocationTargetException` catch，把 cause 原样 rethrow。
   **实测逐用例集合完全不变（619 → 619）**——它没有独立收益，但**栈变了**，
   这本身是有价值的诊断信息，故保留（它是"C# 语义"的正确实现）。
2. ~~声明的类型是类型参数时用错类型~~ —— 该修复**已实现且生成物正确**
   （`JsonClassInfo.determineExtensionDataProperty` 现在会在声明类型不是字典时
   改用 `getRuntimePropertyType()`），但**它并没有带来 30 条收益**：
   §8.2.11 证明那 30 条来自一次**编译失败后被误读的陈旧 XML**。

### 8.2.11 Round 55 更正：一次**假测量**，以及新的测量纪律

**Round 54 记录的 "619 → 589（修 30）" 是错的。** 真相：

- 那次"589"的运行（`mvn-build-r089.log`）**在 `system-text-json` 编译阶段就失败了**
  （我当时的 `PatchExtensionDataSignatureType` 正则吃掉了变量声明行，
  生成出引用未声明变量的代码），日志末尾是
  `mvn <args> -rf :system-text-json`，**测试模块根本没执行**。
- `tools/triage_xml.py` 读的是磁盘上的 `surefire-reports/TEST-*.xml`，
  于是它读到了**上一次成功运行（r087）遗留的 XML**，报告出"589"这个从未发生过的数字。
- 该次运行的汇总行根本不存在：`Tests run: 7902` 出现 **0 次**（正常运行恰好 1 次）。

**新增纪律（已加入 §8.3）**：

| 纪律 | 原因 |
|---|---|
| **测量前先确认那次运行真的跑了测试** | 构建在测试模块之前失败时，`surefire-reports` 里是**上次的 XML**，`triage_xml.py` 会把它当成本次结果，产出**从未发生过的失败数**。判据：日志里 `Tests run: 7902` 必须**恰好出现 1 次**，且没有 `mvn <args> -rf :<module>`。 |
| **改补丁后必须先编译转换器再再生** | 只改 `ProjectPasses.cs` 而不 `dotnet build`，再生用的还是旧补丁；反过来，生成了新代码却没重编转换器，会让"我改了"和"生成物变了"脱节。 |

**真实基线**：`Tests run: 7902, Failures: 340, Errors: 279, Skipped: 18` = **619**。
扩展数据簇**仍有 34 条失败**，且 `invalidExtensionPropertyFail` 现在报的是
`No exception thrown`（而不是签名错误）——说明该簇的真实断点**还没找到**，
下一步应从这里查，**不要再相信 Round 54 的结论**。

### 8.2.12 Round 56：扩展数据签名检查**问错了问题**（一半已修，619 中性）

**探针 `probe/w42/ExtSigProbe.java` 的两个精确事实**：

| 成员 | 声明类型 | `isAssignableFrom(CSharpGenericIDictionary, …)` |
|---|---|---|
| `ClassWithExtensionPropertyAsImmutable.MyOverflow` | `ImmutableDictionary<String,Object>` | **false**（但 `isImmutableDictionaryType` 为 **true**） |
| `ClassWithExtensionData<T>.Overflow` | `Object`（`T` 擦除） | false |

**已修**：C# 从 .NET 5 起接受 `IReadOnlyDictionary<string,T>`（不可变字典正是实现它的），
而生成的检查只问了 `CSharpGenericIDictionary` → 新增
`TypeHelper.isExtensionDataDictionaryType`。签名拒绝 **14 → 12**，但**失败数不变**（619）。

**未修**：残留 12 条全是 `ClassWithExtensionData<T>.Overflow`，其
**声明类型是 `Object`，而 `getRuntimePropertyType()` 也没提供构造后的字典类型** ——
检查两边都是 false。**这是"运行时类型从哪来"的测量问题，不是判据问题。**

**下一步（有依据）**：打印 `JsonPropertyInfo.initialize` 收到的 `runtimePropertyType` 实参，
并确认 `JsonClassInfo.getProperty`（第 441 行 `determineConverter` 通路）是否被走到。
**不要再改判据。**

### 8.2.13 已否证：`T` 实参在属性创建时**并不存在**（Round 57–58，勿再投入）

**Round 58 的探针给出定论**（打在**属性创建**那一刻，而不是反序列化之后）：

```
[cs2j.t] AT-PROPERTY-CREATION Overflow owner=...ClassWithExtensionData
         ownerLayer=null  ownerGenericArgs=[class java.lang.Object]  memberType=class java.lang.Object
```

构造后的实参**在属性创建时根本不存在**（不是"存在但没被用上"）：
owner 的实参层是 `null`，`getGenericArguments(owner)` 只给擦除后的 `Object`。

**结论：这 12 条在 compat 侧无解。** 唯一出路是**转换器侧**把 `T` 的实参显式传到成员级
（大改动，需单独立项）。**扩展数据簇三轮（R56–R58）净收益 0 条，就此收手。**

**这三轮换取的知识**（有价值，但足以说明该方向性价比低）：
R56 区分了"判据错"与"信息缺失"；R57 证明运行时类型链路全擦除；
R58 证明构造实参在属性创建时不存在。

**方法论教训**：一条"看起来只是时机问题"的线索，**一次探针即可定论**——
R57/R58 两轮里真正的进展全部来自探针，而非对判据的推敲。
**下次遇到"信息应该存在"的假设：先写探针，并且把探针打在正确的时刻上。**

### 8.2.14 下一个目标（回到"窄而可一次证伪"的形状）

扩展数据簇已收手，**下一轮应回到单探针可证伪的窄目标**。当前最大的可动簇：

| 簇 | 条数 | 形状 | 是否已否证 |
|---|---|---|---|
| `JsonConvert CSharpList`（2-D 列表族） | ~62 | round 41/46/55 三次尝试均中性或负 | **是**（§8.2.1/8.2.2） |
| `ArrayIndexOutOfBoundsException` | 21 | 工厂里 `getGenericArguments(...)[0]` 拿到空数组 | 线索已在 R52 否证一半，**尚未直接测量** |
| `Assert.throws (No exception thrown)` | 20+ | 各种校验未触发 | 部分已修（属性/转换器类） |

**建议**：`ArrayIndexOutOfBoundsException` 这 21 条**从未被直接探针测过**——
先打印那次调用的 `typeToConvert`、`getGenericArguments(typeToConvert)` 的实际长度、
以及走到的是工厂的哪个分支。**这是当前最符合"一次探针可定论"形状的目标。**

### 8.2.15 Round 59 已修：struct 的 `clone()` 丢掉 final 字段（−40，另有 1 条**已披露**回归）

提交 `a62aac37`。**§8.2.14 的探针推翻了那条线索的定性**：22 条
`ArrayIndexOutOfBoundsException` **不是库崩溃**，而是测试侧
`ClassWithComplexObjects.validateArray` 里**一个已解析的 JSON 数组枚举出 0 个元素**。

**根因**：`StructTransformer` 的逐字段 `clone()` **跳过所有 final 字段**
（final 不能在构造后赋值）。`ArrayEnumerator` 的 `_target` 与 `_endIdxOrVersion`
因此被丢掉 → clone 出 `_target == null`、`_endIdxOrVersion == 0` 的枚举器
→ `moveNext()` 立刻 false → 数组枚举为空。
**扫描：16 个生成文件**都有此问题（含 `JsonDocument.MetadataDb`、`StackRowStack`、
`ArrayEnumerator`、`ObjectEnumerator`）。

**修法**：struct 有 final 实例字段且无可用构造器时，**生成私有全字段构造器**，`clone()` 调它。
（"构造后再赋 final"**不能编译**，是先被编译器挡下才换的方案。）

**已披露回归（1 条，故意保留）**：`valueType_Properties_NotIgnoredWhen_IgnoreNullValues_Active_ClassTest`。
它**因错误原因通过**：clone 丢字段时输出恰好匹配；clone 修好后真实输出是
`{"MyBool":null,"MyPointClass":null}`，**暴露两个真实缺陷**：

1. 字段被序列化成 `null` 而非初始化值（`myInt` 应为 -1、`myPointStruct` 应为 (1,2)）；
2. `IgnoreNullValues` 对 null 引用成员**未生效**。

**这两个缺陷是下一批首选目标**（它们是"因错误原因通过"的测试所隐藏的真问题）。

### 8.2.16 Round 60：两个假设**都被证伪**；`IgnoreNullValues` 真实断点仍未找到

Round 59 披露的回归所"暴露"的两个缺陷，本轮逐条查证，**两条都不成立**：

| 假设 | 查证结果 |
|---|---|
| 字段被序列化成 `null` 而非初始化值 | **证伪**：最小探针输出 `{"S":"Default","I":-1,"B":true}`，初始化器序列化正确 |
| `IgnoreDefaultValuesOnWrite` 从未被检查 | **证伪**：守卫就在 `JsonPropertyInfo1.getMemberAndWriteJson` 里；`getPolicies(...)` 也在 `initialize` 末尾被调用 |

**第二个假设错在 grep 模式**：我搜的是 `.getPolicies(`，而生成代码是**裸调用** `getPolicies(`。
**教训：grep 的零结果是弱证据** —— "没搜到调用点"可能只是模式写错了。
下判断前用**两种形状**各搜一次，或直接读生成代码那一段。

**`IgnoreNullValues` 仍然未解**。已确认的事实：守卫存在、`getPolicies` 被调用、
最小复现里 `b=null` 仍被写出。**下一步：直接打印该成员写出那一刻的
`ignoreDefaultValuesOnWrite` 值**，不要再从代码形状推断。

**本轮附带产出**：`PatchRemoveStrayDiagnostics`（`132dd454`）——
探针残留对所有生成文件**统一清除**。
**已知缺口**：`ReflectionMemberAccessor.java` 里仍有一条残留，
因为**该文件本次再生没有被重写**（mtime 仍是 Round 49），
探针**无害**（可编译、不影响任何用例），下次该文件再生时会被清除。

### 8.2.17 Round 62 已修：`default(T) == null` 只能由**声明成员**回答（569 → 568）

提交 `67b4e3a2`。Round 61 把 `default(T)` 是否为 null 交给**装箱后的声明类型**判断，
但 `int` 和 `int?` 都装箱成 `java.lang.Integer` —— 于是 `WhenWritingDefault` 不再省略 `0`，
`PropertyVisibilityTests#ignoreCondition_WhenWritingDefault_Globally_Works` 回归。

**探针证据**（`DefProbe5`，直接调 compat 的新重载）：

| 成员声明 | `declaredPropertyType` | `getRawPropertyType()` | `default(T)==null` |
|---|---|---|---|
| `int getMyInt()` | `Integer` | `int` | **false** ✓ |
| `Integer getMyNullableInt()` | `Integer` | `Integer` | **true** ✓ |
| `boolean getMyBool()` | `Boolean` | `boolean` | **false** ✓ |
| `V getMyStruct()`（`value class`） | `V` | `V` | **false** ✓ |
| `String getMyString()` | `String` | `String` | **true** ✓ |

**修法**：`ProjectPasses.PatchDefaultValueIsNullDecision` 现在把**两处**判断点都改成传成员
（`defaultValueIsNullForWrite(getDeclaredPropertyType(), getMemberInfo())`）：
写出守卫 **和** `getPolicies(..., getConverter().getCanBeNull())`（后者决定
`IgnoreNullValues`/`WhenWritingNull` 是否可以把 `IgnoreDefaultValuesOnWrite` 打开）。
compat 侧新增 `PropertyInfo.getRawPropertyType()` / `FieldInfo.getRawFieldType()`：
不装箱的声明类型才是 C# 的值类型证据（原始类型 Class 常量，或 JDK 28 的 `value class`）。
**注意 `Integer.class.isValue()` 为 true**（JDK 28 把包装类也变成了 value class），
所以必须把包装类显式排除，否则 `int?` 会被误判成非空值类型。

### 8.2.18 Round 63 已修：`[JsonIgnore(Condition = …)]` 的 Condition 从未离开转换器（568 → 558）

提交 `18e24418`，修 11 条。生成代码里 `[JsonIgnore]` 只发一个**裸** `@CompatJsonIgnore`，
而生成出来的 `JsonIgnoreAttribute` 只有无参构造器（C# 的 `Condition = …` 是对象初始化器，
转换器丢掉了），于是它永远答 `Always` —— 带 `Condition = Never` 的成员被**整个丢掉**：

- `ignoreConditionNever_WinsOver_IgnoreReadOnly*`（4 条）：应输出 `{"MyString":"Hello"}`，实得 `{}`；
- `jsonIgnoreConditionNever(Class)[1..2]`、`jsonIgnoreConditionNever_IgnoreNullValues_True[1..2]`；
- `jsonIgnoreCondition_WhenWritingNull_OnValueType_Fail`（2 条，现在会像 .NET 一样抛）。

**修法**：`PropertyTransformer` 读 `NamedArguments["Condition"]` 的整数值，发
`@CompatJsonIgnore(<n>)`；`CustomAttributeExtensions` 用 `getCondition()` 的返回类型反射找
`fromValue(int)`，再经 `setCondition` 装回去（**按值不按序号**，生成枚举带 `_UNMAPPED`）。

**踩坑**：注解只写 `@CompatJsonIgnore(2)` 时，单元素必须是 `value()`；我起初命名为
`condition()`，javac 报 **`找不到符号: 方法 value()`**，整个 `system-text-json-tests` 编译失败
（见 §8.3 的"summary lines: 0"纪律）。

### 8.2.19 Round 64 已修：`CSharpDictionary` 不遵守构造时给的 comparer（558 → 554）

提交 `fbed68d4`，修 4 条（含 §8.2.18 暴露的那条）。
`CSharpDictionary` 把 `CSharpGenericEqualityComparer` 存进字段却在 `LinkedHashMap` 上
用 `Object.equals` 查表 —— `StringComparer.OrdinalIgnoreCase` 造的字典（序列化器的属性缓存，
`PropertyNameCaseInsensitive` 就靠它）对**任何**大小写变体都答"没有这个键"。

**探针证据**（`DefProbe7`）：`containsKey("MYSTRING")` 修前 `false`、修后 `true`；
`Seq` 的 `get(MYSTRING)` 修前 `null`、修后取到值。
`DefProbe6` 另证：`{"MyString":"Random","MYSTRING":null}` 修前两条都留在 `"Random"`
（`honorCaseInsensitivity` 也是既有失败，与本次改动无关）。

**修法**：新增私有 `storedKey(key)` —— 有 comparer 时按 comparer 扫描并**返回已存的那个键**
（C# 语义：替换值不改键的拼写），`containsKey/get/getChecked/put/remove/tryGetValue/tryAdd/
add/contains/getOrDefault` 全部走它；无 comparer 时退化为原来的 `containsKey`。

**副产物**：Round 63 的唯一回归 `jsonIgnoreCondition_LastOneWins` **被真正修掉**
（它此前只是"因为属性被整个丢掉"而通过）。

### 8.2.20 已定位未修：64 条 `JsonConvert CSharpList` 的根因是**类信息缓存被污染**

**这是当前最大单簇**（554 条里 64 条，占 11.6%），且 64 条**全部**是同一条路径
`$.…MyInt16TwoDimensionList[0]`，即 `List<List<int>>`。

**探针证据**（`DefProbe8`，最小复现 `CSharpList<CSharpList<Integer>>`）：

```
属性上的 converter   : ListOfTConverter / typeToConvert=CSharpList / elementType=CSharpList   ← 正确
getOrAddClass(CSharpList.class) : elementType=java.lang.Integer / ListOfTConverter/Integer     ← 被污染
根因栈 : Int32Converter.read ← IEnumerableDefaultConverter.onTryRead:88
         （元素转换器被解析成了 Int32Converter，于是 `[10,11]` 被当数字读）
```

即：**裸 `CSharpList.class` 的 `JsonClassInfo` 是用最内层实参 `Integer` 建出来的**。
`JsonClassInfo` 第 176 行 `setElementType(converter.getElementType())`，
而 `converter` 是 `options.getOrAddClass(CSharpList.class)` 那一刻由
`IEnumerableConverterFactory` 依 ThreadLocal 实参造出来的 —— 类信息按**裸 Class** 缓存，
但它的内容**依赖当时的实参上下文**。

**下一步（形状明确、可一次证伪）**：给类信息缓存加实参上下文维度（key 里带上
当前 ThreadLocal 实参签名），或在 `getElementClassInfo()` 里绕过裸 Class 缓存。
**注意**：§8.2.11/§6.2 里"转换器缓存读写键统一"那条（`PatchConverterCacheLookup`）
是**被否证过的相邻方向**，不要再原样重试；本条是**类信息**缓存，不是转换器缓存。

**⚠️ Round 70/71 已把下面这条"下一步"**（以及另外两条同簇修法）**全部实测并回滚：
见 §8.2.25 与 §8.2.26。结论：该簇（读侧 64 + 写侧 15 = 79 条）需要"闭类型类信息身份"
贯穿三处的**结构性**改动，单点补丁的代价已被量化为 +37 与 +126 —— 不要再按下面的写法重试。**

### 8.2.25 Round 70 已实测并回滚：闭键类信息的**两半一起做**仍然失败（539 → 576，−37）

**做法**（正是 §8.2.20 里写的"下一步"，两半都实现了）：

1. **成员侧**（compat `cachedClassInfoForMember`）：原来只做**命中查询**（round 41 的结论是
   "键条目从未被创建"）。本轮改成**未命中就创建** —— 用反射
   `new JsonClassInfo(rawType, options)`，创建时把该成员自己的实参装进 `MEMBER_ARGUMENTS`
   （转换器工厂就是读它来定元素/键类型的），实例存进 **compat 自己的 per-options 旁路表**
   （键为 `ClosedGenericKey(raw, args)`），**不污染**生成的 `_classes`。
2. **元素侧**（pass 改写 `JsonClassInfo.getElementClassInfo()`）：
   `getOptions().getOrAddClass(__cs2jElement)` → 先问
   `TypeHelper.getOrAddElementClassInfo(getOptions(), __cs2jElement)`，
   命中闭键就用它，否则保持原样（写法与 `getRuntimeClassInfo()` 已有的那个钩子一致）。

**动机**：round 41 单独做第 1 半时"失败只是搬家（内层元素仍用错转换器）+ 代价 39 条"，
而当时**第 2 半还不存在**（`getElementClassInfo()` 仍按裸 Class 查表）。

**实测：`539 → 576`，修 1 / 坏 38 —— 与 round 41 的 +19 同向且更差。**
回归样本（错的类型被读进来）：
`JsonElement cannot be cast to Integer`、`JsonElement cannot be cast to ChildClassWithJsonElement`、
`Expected: 1 / Actual: 1`（类型不同的等值）。

**关键判定实验**：怀疑是第 2 半对"非同类嵌套"也生效（`MEMBER_ARGUMENTS` 里本来就有每个成员
发布过的实参，所以"表里有这个元素类型的条目"**不等于**这是一次 `List<List<T>>` 嵌套）。
于是加了 `ELEMENT_SCOPE_DEPTH` 守卫，只在 `beginElementArgumentsScope` 真正生效
（`ownerType == elementType`）时才回答元素侧查询。
**结果完全相同（539 → 576，修 1 / 坏 38，回归清单逐条一致）** ——
说明**全部代价来自第 1 半**（创建成员闭键类信息），与元素侧无关。

**结论**：52 条 `JsonConvert CSharpList` 的修法**不是**"给类信息缓存补实参维度"。
类信息一旦不是"每个擦除类一份"，`getRuntimeClassInfo()` 交出去的帧就会换成另一份
`PropertyCache`/`_elementClassInfo`，其副作用面远大于收益（round 41 与 round 70 两次独立测量，
+19 与 +37）。**该方向正式关闭**，见 §8.4。

### 8.2.21 Round 66 已修：`default(JsonElement)` 的 struct 复制走了 C# `Clone()`（554 → 553，另 6 条换了断言）

**探针证据来自失败栈本身**（无需额外探针）：

```
JsonElement.checkValidInstance ← JsonElement.clone ← $JsonElementClass.setNull
  ← MethodInfo.invoke ← ReflectionMemberAccessor.lambda$createPropertySetter$0
  ← JsonPropertyInfo1.readJsonAndSetMember
```

**根因**：C# 的 `JsonElement` 是 struct，给成员赋 `default` 是**逐位复制**；
转换器用 `.clone()` 模拟这个复制，但 `JsonElement` **自带一个 `Clone()` 方法**
（内部 `CheckValidInstance()`，对默认实例抛异常）。于是"复制"继承了 API 的校验：
JSON 里的 `null` 让读取器造出 `default(JsonElement)`，赋值时在生成的 setter 里炸掉。
`StructTransformer.AddCloneMethod` 里那条 `// Has final fields … keep original clone()`
分支（第 852 行）正是"不生成复制方法、保留 C# `Clone()`"的路径 —— `JsonElement` 走的它。

**修法（本轮取窄路）**：`ProjectPasses.PatchJsonElementCloneOfDefaultInstance` 只放宽
`clone()` 里的默认实例分支（`_parent == null` 时 `return this`），
**其他所有成员仍然走 `CheckValidInstance()`**，真实 API 依旧拒绝无效实例。

**实测**：`554 → 553`，逐用例集合差 **修 1 / 坏 0**。
另有 **6 条从 `IllegalStateException` 变成了真实断言失败**
（`Expect Null -> Undefined`：`{"Null":null}` 反序列化后 `ValueKind` 应是 `Null` 却是 `Undefined`）
—— 现在这个桶只剩 5 条，形状是"null token 落到了 `_cs2jDefault_T()` 而不是让转换器去读"，
**下一步可直接从这里接**（`JsonPropertyInfo1` 第 178 行：null token 且 `!HandleNullOnRead`
时直接赋 `_cs2jDefault_T()`）。

**更根本但更大的修法（未做）**：给"值复制"另起一个方法名（如 `_cs2jCopy()`），
让所有**编译器插入**的复制点都不再调用用户可见的 `clone()`。
目前插入点分布在 `StructCloneHelper`、`PropertyTransformer`、`AssignmentTransformer`、
`ArgumentTransformer`、`MemberwiseCloneRewriter` 等处，属独立一轮的工作量；
`JsonElement` 只是第一个被踩到的类型。

### 8.2.22 Round 67 已修：`JsonElementConverter` 的 `CanBeNull` 答反了（553 → 548，修 5 / 坏 0）

**入口**：§8.2.21 那 6 条"换了断言"里的 5 条，桶名 `Expect Null -> Undefined`
（`{"Null":null}` 反序列化后 `ValueKind` 应为 `Null` 却是 `Undefined`）。

**根因**：`JsonConverter<T>.HandleNull()` 里是 `setHandleNullOnRead(!getCanBeNull())`，
而 `CanBeNull = !typeof(T).IsValueType || Nullable.GetUnderlyingType(...) != null`。
C# 的 `JsonElement` **是值类型** → `CanBeNull=false` → `HandleNullOnRead=true`
→ **转换器自己去读 null token**，`JsonDocument.ParseValue` 给出 kind = Null 的根元素。
Java 侧 `JsonElement` 是普通 class，`getIsValueType()` 退化成 `isPrimitive()` → `CanBeNull=true`
→ 框架走"直接赋 `default(T)`"分支 → `Undefined`。

**修法**：`ProjectPasses.PatchJsonElementConverterHandleNull` 在
`JsonElementConverter` 里追加
```java
@Override public boolean getCanBeNull() { return false; }
```
（`setHandleNullOnRead` 是 **private**，只能从 `getCanBeNull()` 这个钩子入手 ——
第一版直接调 setter，javac 报 `找不到符号: 方法 setHandleNullOnRead(boolean)`，
整棵树编译失败、`summary lines: 0`，靠纪律判据当场识破，没有当成"0 收益"。）

**实测**：`553 → 548`，集合差 **修 5 / 坏 0**，正是预测的那 5 条
（`JsonElementTests#serializeJsonElement`、`ObjectTests#write(ITestClass)[37]`、
`ReferenceHandlerTests#readTestClassesWithExtensionOption[37]`、
`SpanTests#read[37]`、`SpanTests#readFromStream[37]`）。

**这一条与 §8.2.17 是同一个母题**：**"这个 Java 类型对应的是 C# 值类型吗"**
在 compat 里被答错了三次（`default(T)`、`getIsValueType`、`CanBeNull`）。
下次遇到"C# 值类型语义没生效"的簇，先查这三个入口。
**已知缺口**：`TypeHelper.getIsValueType(Class)` 目前仍是 `type.isPrimitive()`，
对转换出来的 struct（`Point_2D_Struct` 是 `value class`，`JsonElement` 是普通 class）
都答 false —— 这是同一母题的下一个可攻击点。

### 8.2.23 Round 68：C# 值类型母题的第三次（**一次失败实验 + 一次成功**，548 → 545）

**假设**：`TypeHelper.getIsValueType(Class)` 只答 `isPrimitive()`，而转换出来的 struct 是
Java `value class`（24 个：`JsonEncodedText`、`JsonProperty`、`JsonReaderState`、`ReadStackFrame`、
`PropertyRef`、`ParameterRef`、`BitStack`、`Point_2D_Struct` …），
所以 `Type.IsValueType` 对它们全答 false。

**实验 A（已回滚）**：把通用谓词改成 `isPrimitive() || (isValue() && !包装类)`。
实测 **548 → 547，修 5 / 坏 4**：

| 修好 | 坏掉 |
|---|---|
| `complexValueTypeConverter_NoOverride/NullOptIn/OptOut` | `assignmentToNullableValueTypedMember{Interface,Object}` |
| `assignmentToValueTypedMember{Interface,Object}` | `nullableValueTypedMemberWithNullsTo{Interface,Object}Converter` |

坏掉的都是**可空 struct 成员**：`ValueTypedMember?` 与 `ValueTypedMember` 在 Java 里是**同一个类**
（实测生成物：非空成员的 getter/setter 有 `.clone()`，可空成员没有 —— 这是编译期信息，
运行期拿不到），于是 `JsonPropertyInfo1` 第 214 行把它当成非空值类型，
null token 直接抛 `DeserializeUnableToAssignNull`。**净 +1 不值得引入 4 条新缺陷，已回滚。**

**实验 B（KEEP，`f094d466`）**：同样的判断只放在**转换器自己的入口**上 ——
新增 `TypeHelper.getIsValueTypeForConverter`，由 pass 改写 `JsonConverter1` 的
`this.canBeNull = !getIsValueType() || …` 为 `!TypeHelper.getIsValueTypeForConverter(getTypeToConvert()) || …`。
这样只影响 `CanBeNull`（→ `HandleNullOnRead`），**不碰赋值校验**。
实测 **548 → 545，修 3 / 坏 0**（正是实验 A 里属于转换器那一半的 3 条）。

**教训**：同一个事实（"这是 C# 值类型"）在不同入口上的**代价不同**。
通用谓词同时是"能否给成员赋 null"的闸门，而 Java 的 `Class` 分不出 `T` 与 `T?`；
只在需要它的窄入口上引入值类型判断，就能拿到收益而不触发那个歧义。
**这与 §8.2.17 是同一手法**（`defaultValueIsNullForWrite` 也是先窄化到"声明成员"）。

### 8.2.24 Round 69 已修：参数异常的消息里没有参数名（545 → 539，修 6 / 坏 0）

`ArgumentException` 家族把 `paramName` 存进字段，**消息却是 null**；
`ArgumentNullException("json")` 的 `toString()` 只有类名。
实测失败形状：`Assert.Contains("json", ex.ToString())`
→ `Actual string: io.github.ningpp.compat.ArgumentNullException`。

C# 的规则是：`Message = message + " (Parameter 'name')"`，且**没给 message 时有默认文案**
（`ArgumentNullException` → "Value cannot be null."，
`ArgumentOutOfRangeException` → "Specified argument was out of the range of valid values."）。
按这条补上后 **修 6 / 坏 0**：`ReadValueTests#nullJsonThrows`、`nullReturnTypeThrows`、
`WriteValueTests#nullInputTypeThrows`、`nullWriterThrows`、`OptionsTests#copyConstructor_NullInput`、
`NumberHandlingTests#jsonNumberHandling_ArgOutOfRangeFail`。

**这是"异常文本"通道的又一例**（与 §4.34 的 `ThrowHelper` 泛型名、§8.2.16 的 `csFormat` 同族）：
凡是 `Assert.Contains/equal` 断言**消息文本**的失败，先看 compat 里对应的异常/格式化类，
不要先怀疑逻辑。

### 8.2.26 Round 71：写出侧 `ClassCast Integer -> String` 与读侧 64 条是**同一个簇**；第三条修法也被否证（539 → 665，已回滚）

**新事实 1（探针 `DefProbe11`）**：写侧那 15 条不是独立问题。用最小类复现（同一类里同时有
`CSharpList<CSharpList<Integer>>` 和 `CSharpList<String>` 两个成员）即可得到完全相同的栈：

```
ClassCastException: Integer cannot be cast to String
  at StringConverter.write:33
  at ListOfTConverter.onWriteResume:62   ← 元素转换器被当成 String 列表
  at IEnumerableDefaultConverter.onTryWrite:216
  at JsonConverter1.tryWrite:436
```

**新事实 2（探针 `DefProbe12`）**：那个 2-D 成员的两条候选元素信息**都是错的**：

| 候选 | 结果 |
|---|---|
| 转换器路线（`writeElementPropertyInfo`，读 `converter.getElementType()` 再 `getOrAddClass`） | `ListOfTConverter/CSharpList/element=String` ❌ |
| 回退路线（`JsonClassInfo.getElementClassInfo()`） | `StringConverter` ❌ |
| 裸 `CSharpList` classInfo 的 `elementType` | `java.lang.String`（被 `List<String>` 成员污染） |

即 `writeElementPropertyInfo` 这条**专门为修这个问题写的**钩子在"元素类型被擦除"时也无能为力
—— 它查的还是同一份被污染的裸类信息。

**新事实 3（第三条修法，已回滚）**：既然裸类信息的内容取决于"最后反射的是哪个成员"，
那就让它**与顺序无关**：新增 pass 把
`getOrAddClass` 里的 `ConcurrentHashMapHelper.getOrAdd(_classes, type, new JsonClassInfo(type, this))`
换成 compat 的 `getOrAddClassInfo(...)`，在**没有成员实参上下文**时创建（擦除类的合法答案就是
`Object`，与全新 options 的首次创建一致；元素实参作用域内不动）。

**实测：`539 → 665`（修 3 / 坏 129）** —— 太宽：大量 `List<SimpleTestClass>` 这类成员的类信息
本来**依赖**成员实参才能拿到正确元素类型，清掉上下文等于把它们全打回 `Object`。

**三次独立尝试的汇总（都在这个簇上，都失败）**：

| 尝试 | 实测 |
|---|---|
| (a) 给成员创建闭键类信息（± 元素侧闭键查询） | round 41：+19；round 70：+37（带作用域守卫后逐条一致 → 代价全在成员侧创建） |
| (b) 只做元素侧闭键查询 | 无效：帧仍是裸类信息，`elementArgumentsOfOwner` 拿到空数组，作用域根本不启动 |
| (c) 让擦除类的类信息与顺序无关（清空实参上下文创建） | 本轮：+126 |

**结论（重要）**：读侧 64 条 + 写侧 15 条 = **79 条（14.7%）同一个簇**，
它的修法**不是**任何局部补丁，而是**结构性的**：需要"**闭类型类信息身份**"贯穿
`getRuntimeClassInfo()` / `getElementClassInfo()` / 元素属性信息解析三处，
且**不能**与"每个擦除类一份"的既有身份混用（(a)(c) 的代价正是混用造成的）。
下次要动这个簇，先写出这个身份方案（谁持有、谁查、帧怎么带）再改代码，
**不要**再逐个入口试补丁。

### 8.2.27 Round 70：可空 struct 在运行期**无法**与不可空区分（§8.2.23 实验 A 的方向正式关闭）

Round 68 的实验 A（把 `getIsValueType` 改成认得 `value class`）得 5 修 / 4 坏，坏的全是
`ValueTypedMember?` 成员。当时只凭生成物（不可空成员的 getter/setter 有 `.clone()`）**推断**
运行期分不出来。本轮用探针**直接验证**（`DefProbe10`）：

| 成员 | declaredPropertyType | converter | typeToConvert | propertyTypeCanBeNull |
|---|---|---|---|---|
| `TestClassWithValueTypedMember.MyValueTypedProperty` | `ValueTypedMember` | `ObjectDefaultConverter` | `ValueTypedMember` | true |
| `TestClassWithNullableValueTypedMember.MyValueTypedProperty` | `ValueTypedMember` | `ObjectDefaultConverter` | `ValueTypedMember` | true |

**两行完全相同**（比较器、转换器类型、`TypeToConvert`、可空标志全部一致），
唯一差别是编译期插入的 `.clone()`。
→ **不存在**能在 compat 侧区分它们的运行期信号，因此
"让通用 `getIsValueType` 认得 value class"这条方向**没有安全实现**，正式关闭。
（`Converter.CanBeNull` 那条窄路已在 `f094d466` 取到它的 3 条收益，见 §8.2.23。）

### 8.2.28 Round 72 已修两条（长尾机械化缺陷，539 → 532）

**方法转变**：前两轮在同一簇上连撞三次，本轮改用**按消息签名分组**（`tools/sig_group.py`
+ `tools/equal_group.py`）先看全局分布，再挑"一眼机械"的条目。539 条拆出 104 个签名，
最大的一类是 `Assert.equal() failure: Values differ`（174 条，但内部 59 种 E/A 形状，
异质），真正有价值的是一条 **7 条的 NPE 签名**。

**（1）空属性名的哈希 —— `853e17ce`，修 5 / 坏 0**

签名：`Cannot invoke "java.lang.Integer.intValue()" because the return value of
"io.github.ningpp.compat.MemoryMarshal.getReference(io.github.ningpp.compat.ReadOnlySpan)" is null`
→ `JsonClassInfo.getKey:747`。

C# 的 `GetKey` 对**零长度**名字答 `0`（`{"":1}` 是合法 JSON，.NET 能读）；
转换器把字节读取提升成"引用 + Unsafe"形式，于是方法开头是
`int reference = MemoryMarshal.getReference(name);`，而 compat 的 `getReference(ReadOnlySpan)`
对空 span 返回 **null**（javadoc 写明是 C# 悬垂 ref 的安全替代）。解包 null 在
**根本不会读**的长度分支之前就抛了。
修法（pass）：`int reference = name.getLength() > 0 ? MemoryMarshal.getReference(name) : 0;`

**（2）`DateTimeOffset` 的 `"O"` 必须写带符号偏移 —— `e9d89bc1`，修 2 / 坏 0**

`CSharpDateTimeOffset.toString("O")` 用 Java 的 `XXX`，而 Java 对零偏移渲染成 `Z`；
C# 的 `DateTimeOffset` **自带偏移**，`"O"` 永远写 `+00:00`（`Z` 只属于 `DateTimeKind.Utc` 的
`DateTime`）。JSON writer 走的正是 `Utf8Formatter.tryFormat(value, …, 'O')` →
`toString("O")` → `JsonWriterHelper.trimDateTimeOffset`，而**裁剪逻辑本身就按
`±hh:mm` 布局的固定下标**取值，`Z` 会短一个字符。
修法：`toString("O")` 末尾改成 compat 自己的 `formatOffset(ZoneOffset)`。
**注意**：§6.2 里"`XXX`→`xxx`"那条是被否证的错方向（`x` 同样渲染 `Z`），本条是**手工拼偏移**。

**顺带记录（尚未修）**：`writeNumberValueDecimal(boolean,boolean)[1..4]`（4 条）
期望 `["1234567.0",…]`（**带引号**）、实际 `[1234567.0,…]`。期望值由**测试侧**的
`GetNumbersExpectedString` 用 Newtonsoft 的 `JsonTextWriter.WriteValue(decimal)` 生成 ——
即**端口化的测试预言机把 decimal 写成了字符串**，与 writer 输出无关。属测试侧移植问题，
不在"改转换器/compat"的射程内（除非改 Newtonsoft 端口本身）。


### 8.2.29 Round 69 已修：非公开成员的可见性判定（523 → 518，修 5 / 坏 0）

**入口**：站点榜上 `PropertyVisibilityTests.nonPublicProperty_WithJsonInclude_Invalid`（5 条 theory 用例）。
断言是 `Assert.Throws<InvalidOperationException>(() => JsonSerializer.Deserialize("", type))`
—— .NET 在**预热期**就校验"非公开成员带 `[JsonInclude]` 是非法的"，我们却先解析空 JSON 失败，
抛了 `JsonException`。

校验代码（`JsonClassInfo` 第 118–147 行）本来就在，问题是有三类成员**根本进不到**那个分支：

| 用例 | 原因 | 修法 |
|---|---|---|
| 私有字段 / 受保护字段 | `TypeHelper.getFields(type, flags)` 用的是 `Class.getFields()` —— **只返回 public 字段**，`NonPublic` 位永远无效 | 改为按 flags 遍历 `getDeclaredFields()`（`DeclaredOnly`/`Static`/`Instance`/`Public`/`NonPublic` 全部生效）；用 `trySetAccessible()` 跳过 JDK 内部类，避免 `InaccessibleObjectException` |
| C# `internal` 属性 | `PropertyInfo.getGetMethod()` 只认 `CompatNonPublicAccessor`，不认 `CompatNonPublicProperty`，而 C# `internal` 的 Java accessor 是 **public** → 被判成公开成员 | 让它与 `getSetMethod()` 对称：整个属性非公开时也返回 null |
| C# `internal` 字段 | `FieldInfo.getIsPublic()` 只看 Java 修饰符，而 `internal` 被发成 `public` | `FieldTransformer` 给**所有非公开字段**发 `CompatNonPublicProperty`，`FieldInfo.getIsPublic()` 与 `getFields` 的 flags 判定都认它 |

**实测**：`523 → 519`（`ac40ded2`，修 4 / 坏 0）→ `519 → 518`（`784c5b3a`，修 1 / 坏 0）。
**5 条 theory 用例全部通过**。

**这是"C# 可访问性 ≠ Java 修饰符"母题的第三个入口**（前两个：`CompatNonPublicAccessor` 访问器、
`CompatInternalCtor` 构造函数）。**下次遇到"这个成员/类型公开吗"的运行时问题，先查这里有没有标记。**


### 8.2.30 Round 70：`getGenericArguments` 是**消费式**查询（一次探针钉死；一处中性修复）

**入口**：站点榜上 `CollectionTests#readKeyValuePairOfKeyValuePair`（4 条，`Expected: Key / Actual: Key`
—— 两侧都打印成 `Key`，实为 **`JsonElement` vs `String`** 的类型差）与
`CustomConverterTests` 的 4 条 `CSharpDictionary.getChecked` KeyNotFound，合计 8 条。

**探针 `DefProbe14`**（`KeyValuePair<String, KeyValuePair<Integer,Integer>>`，注册闭类型后连问三次）：

```
genericArguments #1 = [class java.lang.String, class io.github.ningpp.compat.CSharpKeyValuePair]
genericArguments #2 = [class java.lang.Integer, class java.lang.Integer]
genericArguments #3 = [class java.lang.Integer, class java.lang.Integer]
```

即 **`TypeHelper.getGenericArguments` 每调用一次就消费一层注册实参**（这正是"外层 list → 内层 list"
能各自拿到自己那一层所依赖的机制），而生成代码把它当**纯函数**用：

```java
Class keyType   = TypeHelper.getGenericArguments(type)[0];   // #1 → String
Class valueType = TypeHelper.getGenericArguments(type)[1];   // #2 → 内层 KeyValuePair 的实参
```

**已修（中性）**：pass `PatchKeyValuePairGenericArguments` 在 `ObjectConverterFactory` 里绑定一次。
**实测 `518 → 518`，修 0 / 坏 0，且 18 条消息变化全是对象哈希噪声** —— 说明这些用例跑到工厂时，
**第一层也已经被预热期消费掉了**（所以只绑定两次调用不解决问题）。
按 §8.3 的纪律先查了"栈是否变了"（没变），但本条作为**可证伪的潜在缺陷修复**保留（先例：`99151aa6`）。

**结论**：这 8 条与 §8.2.26 的 79 条同源 —— 都要求"**实参必须在整个预热/读取过程中可见**"。
**单点绑定不够**；真正的修法是让闭类型的实参身份贯穿预热到读取（与 §8.2.26 的结构性方案是同一件事，
可以合并成一个方案设计）。

**同时记录（未动）**：`ObjectTests#tooLittleJsonFails`（6 条）失败在
`Assert.equal(0, reader.getBytesConsumed())` —— 直接对 `Utf8JsonReader` 调
`Deserialize(ref reader)` 抛异常后，.NET 侧 `BytesConsumed` 仍是 **0**，我们已消费 1/4/6 字节。
要匹配就得在异常路径上**回滚 reader 状态**，属独立设计决策（会影响"reader 前进"依赖者），本轮未做。


### 8.2.31 Round 71：三条线索实测（全部回滚/未修，518 → 518 复核实测）

本轮没有净收益，但有三个**可复用的否证/定位**：

**(1) `newNonGenericCollection` 不能"先实例化目标类型"（已回滚，两版都坏）**

簇：`AbstractCollection CSharpArrayList`（3 条）——`IEnumerableConverter.createCollection` 抛
"The collection type 'CSharpArrayList' is abstract, an interface, or is read only"。
`TypeHelper.newNonGenericCollection` 里 null 只可能来自生成代码的守卫
`isAssignableFrom(getTypeToConvert(), getRuntimeType())`，而对 `CSharpArrayList` 来说
`getRuntimeType()` 是泛型 `CSharpList` → 守卫为假 → 拒绝了一个**明明可以实例化**的类型。

两版修法都失败：
- 先 `getDeclaredConstructor().setAccessible(true)` 实例化 → **修 0 / 坏 7**
  （`readPrimitiveIImmutable*T` 4 条 + `readReadOnlyCollections_Throws` + 两条 `_NoPublicConstructor_Throws`）
- 收紧成只用**公开**无参构造器 → **修 0 / 坏 6**（同样那批 immutable 包装类）

**结论**：这些不可变包装类**有**公开无参构造器，实例化它们会走错路径（C# 侧它们靠
`ConvertCollection` 或直接拒绝）。**"能不能实例化"不是这里正确的判据**；守卫表达的是别的语义
（目标类型与运行时类型的可赋值关系）。回滚后重新生成 + 全量测量 = `518 → 518，修 0 / 坏 0`。

**(2) `[[I`（`int[][]`）"not supported"（3 条）：全局 rank 标记**跨用例泄漏**

`IEnumerableConverterFactory.createConverter` 里 `getArrayRank(typeToConvert) > 1` 就抛。
`getArrayRank` 的语义是"注册表里查一次并**消费**，否则答 1"，而注册表由
`TypeHelper.getMultiDimArrayClass(elementType, rank)` 写入 —— **生成出来的测试大量直接调用它**，
例如 `Assert.contains(TypeHelper.getMultiDimArrayClass(int.class, 2).toString(), ex.getMessage())`
（`ExceptionTests` 8 处、`CustomConverterTests` 2 处）：这些调用**只为了拿名字**，
永远不会被 `getArrayRank` 消费，于是留下一条"`[[I` 是 rank 2"的全局笔记；
同 JVM 里**后面**任何一次真正的**交错数组** `int[][]` 转换（如 `SimpleTestClass.MyInt16TwoDimensionArray`）
就会看到它并抛出。**根因是 Java 把 C# 的 `int[,]` 与 `int[][]` 折叠成同一个 `Class`。**

**修法方向（未做）**：让"只为文案"的用法发一个**不注册**的变体
（转换器能看出 `typeof(int[,])` 是出现在 `ToString()`/消息参数里，还是被传进 serialize/deserialize）。
在 compat 侧无法补救：标记按 Class 存，而两类类型同 Class。

**(3) `enum eDevice : uint` 的表示不支持字符串枚举名（6 条，未修）**

簇：`CustomConverterTests#enumValue/enumArray`（6 条，`could not be converted to Integer`）。
实测生成物把 `enum eDevice : uint` 发成 **flags 静态常量类**：

```java
public static class eDevice {
    public static final int PC = 0x2;
    public static final int Tablet = 0x8;
    public static int or(int a, int b) { return a | b; }
```

测试因此是 `int obj = (int) JsonSerializer.deserialize(json, null, int.class)` —— 类型**一致**，
不是转换器的类型映射错误；缺的是**这套 flags-int 表示上的字符串枚举名支持**
（JSON 里是 `["PC","Tablet"]`）。属功能缺口，不是一行修法。


### 8.2.32 Round 72 已修：多维数组 rank 标记的**两个泄漏源**（518 → 513，修 5 / 坏 0）

§8.2.31 记下了这个簇（"`class [[I` is not supported"）的机制，本轮把它修掉。**两个泄漏源都要堵。**

**背景协议**：`TypeHelper.getMultiDimArrayClass(elementType, rank)` 发布一条"这个数组类其实是
C# 多维数组"的笔记，`getArrayRank` **消费**它（因为 Java 的 `Class` 是规范的，C# 的 `int[,]` 与
`int[][]` 是同一个运行时类）。`IEnumerableConverterFactory.createConverter` 里
`getArrayRank(...) > 1` 就抛 `NotSupportedException`。

**泄漏源 1：只为文案的调用**。生成出来的测试用
`Assert.contains(TypeHelper.getMultiDimArrayClass(int.class, 2).toString(), ex.getMessage())`
（`ExceptionTests` 8 处、`CustomConverterTests` 2 处）只为拿类型名，笔记**永远不会被消费**。
修法：转换器识别"父节点是 `.ToString()`"的 `typeof(T[,])`，改发**不注册**的
`TypeHelper.getMultiDimArrayClassName(...)`（新增）。生成物实测：10 处走 name-only、6 处保留注册版。
**单独这一半实测中性**（518 → 518）—— 但它是必要的，因为另一半修好后仍有残留。

**泄漏源 2：缓存命中时没人消费**。`deserialize(..., [[I)` 这种"真"用法，
一旦该类的类信息与转换器**已被缓存**，工厂与 `getConverter` 都不会再跑 → 笔记留下 →
**同 JVM 里之后**任何一次真正的**交错数组** `int[][]` 成员转换（如 `SimpleTestClass.MyInt16TwoDimensionArray`）
都会看到它并抛出。同一个原因还让"多维数组应当被拒绝"的用例**不再抛**
（`ArrayTests#readPrimitiveArrayFail`：`No exception thrown`）。
修法：把消费点提到**最早且缓存命中也会进入**的入口 —— `JsonSerializerOptions.getOrAddClass`，
未命中即 `takeMultiDimArrayRank(type)`，为真就抛（并从 `getConverter` 入口再兜一道）。

**实测**：`518 → 513`，**修 5 / 坏 0**：
`ArrayTests#readPrimitiveArrayFail`、`ExceptionTests#caseInsensitiveFails`、
`ExceptionTests#unsupportedTypeFromRoot`、`NullTests#invalidRootOnRead`、`NullTests#invalidRootOnWrite`。
**全树 `[[I` 失败清零**（两条原先命中的用例
`CacheTests#propertyCacheWithMinInputsLast`、`ObjectTests#readClassWithNestedComments`
转到了它们真正的失败原因上，集合差如实仍算失败）。

**方法论**：这是"**顺序相关失败**"的第二个已定位根因（第一个是 §8.2.26 的类信息缓存）。
**凡是"单独跑过、全量跑挂"的用例，先找有没有按 `Class` 存的全局笔记**。


### 8.2.33 Round 73 已修：`ref` 参数的回滚必须**就地**写回（513 → 503，修 10 / 坏 0）

**入口**：`ObjectTests#tooLittleJsonFails`（6 条，`Assert.equal(0, reader.getBytesConsumed())` 得到 1/4/5/6）
以及 `ReadValueTests#tooLittleJsonForIntArray`（3 条）、`ReadValueTests#readPropertyName`。

**根因（两个叠加的模拟缺口）**：

1. 生成代码的**注释里就写着** C# 契约：
   "Upon completion of this method reader will be positioned at the final token in the JSON value.
   **If an exception is thrown the reader is reset to the state it was in when the method was called.**"
   —— 但没有任何实现。
2. 已有的回滚写法是 `reader.value = restore;`，它模拟的是 C# 的 `reader = restore;`（`ref` 参数）。
   在 C# 里那会**写穿调用方的存储**；在 Java 里它只是把 **ObjectHolder 的引用**换个指向，
   于是**自己留着 reader 变量的调用方**看到的仍是**前进过**的那个对象。
   探针实测（`DefProbe15`）：`before = 0 / threw JsonException / after = 6`，
   而 .NET 侧应保持 0。

**修法（两步，缺一不可）**：

- `StructTransformer` 为每个"有可赋值实例字段"的 struct 发一个
  `public void __cs2jCopyFrom(T other)`（逐字段 `this.f = other.f;`）——
  Java 里没有 `ref`，就地写回只能靠"把字段拷进调用方仍持有的那个对象"。
- pass 把两处 `reader.value = restore;` 改成 `reader.value.__cs2jCopyFrom(restore);`，
  并在 `deserialize(ObjectHolder<Utf8JsonReader>, …)` 里加一层
  `catch (RuntimeException) { reader.value.__cs2jCopyFrom(快照); throw; }` 兜底。

**实测**：`513 → 503`，**修 10 / 坏 0**（6 条 `tooLittleJsonFails` + 3 条 `tooLittleJsonForIntArray`
+ `readPropertyName`）。

**可复用的判据**：**凡生成代码里出现 `holder.value = x;`，都要问一句"这是不是在模拟 C# 的
`ref` 赋值？"** —— 如果是，调用方可能仍持有原对象，必须**就地**改而不是换引用。
（同一母题的"字段级"版本见 §8.2.29 的 C# 可访问性；这里是"引用级"版本。）


### 8.2.34 Round 74：`ref`/`out` 模拟的**全量扫描**（0 处遗留）+ 对象初始化器缺口

**(1) 全量扫描：`ObjectHolder` 的 `ref`/`out` 模拟没有遗留问题**

Round 73 的判据（"`holder.value = x` 是不是在模拟 C# 的 `ref` 赋值"）本轮用两个工具做了全量核对：

- `tools/holder_writes.py`：全树 **457** 处 `x.value = …` 赋值，按 holder 名分组
  （`value` 154、`this` 62、`syntax` 26、`reader` 8 …）。`reader` 的 8 处里有 4 处在
  `JsonDocument`（注释原文："Value copy to overwrite **the ref** on an exception and undo the
  destructive reads"）—— 与 round 73 修的 `JsonSerializer` 两处同源。
- `tools/holder_scan2.py`：**194** 处"把局部变量作为 `ref`/`out` 传入"的站点，
  逐个检查调用方是否把 `holder.value` 或原变量读回 —— **0 处遗留**。

因此把就地回滚的 pass 从 `JsonSerializer.java` **提升为全文件通用**
（`PatchReaderRestoreInPlace` 现在对每个文件都跑），覆盖 `JsonDocument` 的 4 处。
**实测中性 `503 → 503`（修 0 / 坏 0）** —— 与扫描结论一致：当前没有调用方受影响。
保留的理由：它与 round 73 是同一个修法，且更忠实（C# 的 `reader = restore;` 本就写穿调用方）。

**(2) 新缺口：`new T() { Prop = value }` 的对象初始化器被丢弃（4 条）**

簇：`ExtensionDataTests#extensionProperty_SupportsWritingToCustomSerializerWithExplicitConverter[1..4]`，
消息 `Expected: {"MyCustomOverflowWrite":"OverflowValueWrite"} / Actual: {}`。

C# 原文：
```csharp
var root = new TRoot() { Overflow = new TDictionary() };
string json = JsonSerializer.Serialize(root);
Assert.Equal(@"{""MyCustomOverflowWrite"":""OverflowValueWrite""}", json);
```
生成物：
```java
var root = (TRoot) io.github.ningpp.compat.Activator.createInstance(tRootClass);   // ← 初始化器被丢掉
```
于是 `Overflow == null`，`getMemberAndWriteJsonExtensionData` 里 `value == null → success = true`
（这一分支与 .NET 一致），什么都没写。

**要修的是转换器**：支持 `new T { Prop = value }` 的对象初始化器（构造后逐个属性赋值）。
这是**通用缺口**（不是本簇特有），与 round 63 的 `[JsonIgnore(Condition = …)]`
（同样是对象初始化器被丢）同源。


### 8.2.35 Round 75 已修：类型参数创建分支丢掉对象初始化器（503 → 499，修 4 / 坏 0）

§8.2.34(2) 定位的缺口，本轮修掉。**对象初始化器的支持本来就存在**
（`ObjectCreationTransformer.TransformObjectCreationWithInitializer`，含只读 struct 的构造器形式），
问题出在**它之前的提前返回**：

```csharp
// new T() where T : new()  /  T : ICollection<...>
if ((node.ArgumentList == null || node.ArgumentList.Arguments.Count == 0)
    && createdTypeSymbol is ITypeParameterSymbol typeParameter)
{
    if (HasCollectionConstraint(typeParameter)) { return $"({{typeName}}) new CSharpList<...>()"; }   // ← 提前返回
    if (typeParameter.HasConstructorConstraint) { return $"({{typeName}}) Activator.createInstance(...)"; }  // ← 提前返回
}
// 下面才是对象初始化器分支（第 335 行）
```
于是 `new TRoot() { Overflow = new TDictionary() }` 只剩构造，`Overflow` 保持 null；
`ExtensionDataTests#extensionProperty_SupportsWritingToCustomSerializerWithExplicitConverter[1..4]`
期望自定义转换器写出的 `{"MyCustomOverflowWrite":"OverflowValueWrite"}`，实得 `{}`。

**修法**：两个分支在返回前判断 `node.Initializer` 是否为对象初始化器，是则走新的
`ApplyInitializer(...)` —— 生成一个合成局部变量 + 每个赋值一条 setter 调用
（与 `ControlFlowTransformer` 的 `with` 表达式同一形状）：

```java
TRoot __cs2jInit1 = (TRoot) io.github.ningpp.compat.Activator.createInstance(tRootClass);
__cs2jInit1.setOverflow(io.github.ningpp.compat.Activator.createInstance((Class<TDictionary>) tDictionaryClass));
var root = __cs2jInit1;
```

**实测**：`503 → 499`，**修 4 / 坏 0**。

**同一母题的第三处（本轮一并修，实测中性）**：`TransformNew(ImplicitObjectCreationExpressionSyntax)`从不看 `node.Initializer` —— 即目标类型化的 `new() { Prop = v }` 也会把赋值全部丢掉。复用 `ApplyInitializer` 后全树生成 69 处合成初始化，**`499 → 499`，修 0 / 坏 0**（仅 1 条消息尾部变化），说明当前没有断言依赖它；保留理由：丢赋值本身无疑是错的。

**检查过的其他提前返回**：该函数里还有 `FileStream` / `ReflectionEmitMemberAccessor` /
`UTF8Encoding` / `Decimal` / `Guid` 五个特例提前返回，它们对应的 BCL 类型在 C# 里不会配对象初始化器，
本轮未一并处理（如需再补，形状与本次相同）。

**这是"对象初始化器"母题的第二例**（第一例：round 63 的 `[JsonIgnore(Condition = …)]`）。
**凡"期望的赋值为空/默认"的失败，先查 C# 侧的 `new X { … }` 有没有被转换器丢掉。**


### 8.2.36 Round 76 已修：`[JsonInclude]` 成员的非公开 setter 不可见（499 → 493，修 6 / 坏 0）

**入口**：`Expect N -> 0` 这一形状里 `PropertyVisibilityTests` 占了 6 条 ——
反序列化后成员仍是 0。用探针 `DefProbe16` 直接看三个同类：

```
== Class_PropertyWith_PrivateInitOnlySetter_WithAttribute      field myInt = 0   ← 失败
== Class_PropertyWith_InternalInitOnlySetter_WithAttribute     field myInt = 1   ← 唯独它"对"
== Class_PropertyWith_ProtectedInitOnlySetter_WithAttribute    field myInt = 0   ← 失败
```

生成物里三者的 setter 形态不同：`private`/`protected` 的 C# `init` 访问器被发成
**`protected void setMyInt(int)`**（带 `/** @implNote Set during construction only (C# init accessor). */`），
而 `internal` 被发成 **`public`**（C# `internal` → Java `public` 的既有约定）——
所以只有它碰巧能用。

**根因**：`PropertyInfo.findSetter` 用的是 `Class.getMethods()`，**只返回 public 方法**，
于是那两个 protected setter 对 compat 完全不可见 → `getSetMethod()` 为 null → 读路径写不进去。
而 C# 侧 `[JsonInclude]` 的语义正是"**这个非公开 setter 允许反序列化时使用**"。

**修法**（compat）：`findSetter` 在公开方法里找不到时，**若 getter 带 `CompatJsonInclude` 标记**，
再按 `getDeclaredMethods()` 沿继承链找非公开 setter 并 `setAccessible(true)`。
用标记做闸门，保证 `nonPublicMembersAreNotIncluded`（无 `[JsonInclude]` 的非公开成员）不受影响。

**实测**：`499 → 493`，**修 6 / 坏 0**：
`nonPublicInitOnlySetter_With_JsonInclude_Works(Class)[1]/[3]`、
`honor_JsonSerializablePropertyAttribute_OnProperties`、
**`honorJsonPropertyName`**（原本以为要单独查的 struct 用例，同因）、
`extensionDataCanHaveNonPublicSetter`、`honorCustomConverter`。

**可复用判据**：compat 用 `Class.getMethods()`/`getFields()` 做成员发现的地方，
都默认"只有 public" —— 这与 C# 的 `BindingFlags` 语义和"`[JsonInclude]` 破例"都不符。
**凡是"反序列化后成员没被赋值"的失败，先查 setter 发现路径的可见性。**
（`getFields` 的同类问题已在 §8.2.29 修过。）


### 8.2.37 Round 76b 已修：`new InvalidOperationException()` 的默认消息（493 → 490，修 3 / 坏 0）

**形状**：`Assert.contains ... Expected substring: 'null' / Actual string: 'null'`（3 条 theory 用例）。
**"实际字符串"本身就是 `null`** —— 说明断言的**期望值**是 null。

C# 原文（`JsonPropertyTests.cs:100`）：
```csharp
string ErrorMessage = new InvalidOperationException().Message;   // ← C# 有默认消息
AssertExtensions.Throws<InvalidOperationException>(() => prop.NameEquals(text), ErrorMessage);
```
C# 的 `new InvalidOperationException()` 带默认消息
"Operation is not valid due to the current state of the object."，
而 Java 的 `new IllegalStateException()` 没有 → 生成物里 `ErrorMessage == null`
→ 抛出的异常消息与 null 比较，断言失败。

**修法**：对象创建转换器对**无参**的 `System.InvalidOperationException` 特例发
`ExceptionCompat.invalidOperation()`（compat 新增，返回带默认消息的 `IllegalStateException`）。
**类型仍是 `IllegalStateException`**，所以 `Assert.Throws<InvalidOperationException>` 照旧匹配。

**实测**：`493 → 490`，**修 3 / 坏 0**（`JsonPropertyTests#nameEquals_InvalidInstance_Throws(String)[1..3]`）。

**这是"异常文本"通道的第四例**（前三例：`ThrowHelper` 泛型名 §4.34、`csFormat` 数字格式 §8.2.16、
参数异常的 paramName §4.71）。**凡断言消息文本的失败，先查 compat/映射里那个异常类型的默认消息。**


### 8.2.38 Round 77 已修：`default(Utf8JsonReader)` 的缓冲区是 **null** 而不是空 span（490 → 488，修 2 / 坏 0）

**形状**：`Assert.throwsAny` 期望 JsonException，实得 NPE，栈顶是
`Utf8JsonReader.hasMoreData:974 ← readSingleSegment:843 ← read:402`（2 条：
`ReadValueTests#readDefaultReader`、`JsonDocumentTests#parseDefaultReader`）。

**根因**：生成的默认构造器是 `public Utf8JsonReader() { /* zero-initialized */ }` ——
Java 的引用字段因此是 **null**，而 C# 的 `default(Utf8JsonReader)` 有一个**空的** `ReadOnlySpan`。
`hasMoreData()` 第一行就 `_buffer.getLength()` → NPE。

**修法**（pass `PatchDefaultReaderBuffer`）：

```java
public Utf8JsonReader() {
    /* zero-initialized, but C#'s default struct has an EMPTY span, not a null one ... */
    _buffer = new ReadOnlySpan<Integer>();
}
```

**只需要 buffer**：`_isNotPrimitive` 的 Java 默认值已经是 `false`，与 C# 一致 ——
正是它让 C# 的 `HasMoreData()` 跳过会碰 `_readerOptions` 的那个分支。

**实测**：`490 → 488`，**修 2 / 坏 0**。

**同族的第 5 条"C# 默认值 ≠ Java 默认值"**：
`default(T)`（§8.2.17）、`default(JsonElement)`（§8.2.21）、访问器可见性（§8.2.29）、
`ref` 回滚（§8.2.33）、**本条的默认 struct**。

**(附) 本轮实测并回滚的一条**：`JsonSerializerOptions.getConverter` 的入口把
primitive 规格化成 wrapper（`getConverter(char.class)` 现在答 `ObjectDefaultConverter`，
因为转换器表按 wrapper 建键）。**实测 `488 → 488`，0 修 0 坏，逐条消息零变化** ——
说明 `nullReadTestChar` 的 NPE 不在查找路径上，而在"值类型转换器收到 null token"
（即 §8.2.23/§8.2.27 已关闭的 `T` vs `T?` 歧义），故回滚，保持改动面最小。


### 8.2.39 Round 79 已修：`default(T)` 对**C# 类**返回了新实例（488 → 470，修 18 / 坏 0）

**本会话单轮最大收益（−18）。**

**入口**：`Assert.null` 桶里一批"应该为 null 却是对象"的失败（4 条 `CSharpDictionary@…`、
3 条 `ExtensionDataTests#nestedClass`、`NullTests#rootObjectIsNull` 等）。
探针 `DefProbe17` 一行定位：

```
C.class         -> DefProbe17$C@77a567e1     ← 应为 null
Object.class    -> null
String.class    -> null
C[]             -> null
SimpleTestClass -> dotnet...SimpleTestClass@3834d63f   ← 应为 null
```

即**只有"具体类作为根类型"这一种情况答错**。

**根因**：`DefaultValue.getDefaultByClass` 对"既不是原始类型、也不是包装类/String/Object"的类型
**一律尝试无参构造器**并返回实例。对 C# 的 **class**，`default(T)` 是 **null**；
只有 **struct** 才有零实例。而转换器把 struct 发成 Java 的 `value class` ✓ ——
所以判据现成：`!type.isValue()` 就是"这是 C# 类"。

**修法**（compat 一行判据）：

```java
if (!type.isValue()) {
    return null;      // C# class → default(T) is null
}
try { return type.getDeclaredConstructor().newInstance(); } catch (Exception e) { return null; }
```

**实测**：`488 → 470`，**修 18 / 坏 0**：
`NullTests#rootObjectIsNull`、`NullTests#deserializeDictionaryWithNullValues`、
`DictionaryTests#{nullDictionaryValuesShouldDeserializeAsNull, dictionaryWithNullShouldPreserveOrder_Deserialize,
shouldHandleNullInDictionaries_Deserialize}`、`ExtensionDataTests#{deserializeIntoMultipleDictionaries,
nestedClass[1..3]}`、`CyclicTests#deepTypeCycleWithRoundTrip`、`ArrayTests#readNull[1]`、
`NullableTests#nullableCustomStructRoundtrip`、`ObjectTests#write(ITestClass)[6]`、
`PropertyVisibilityTests#classWithComplexObjectUsingIgnoreNeverAttribute`、
`ReferenceHandlerTests/SpanTests …[6]`、`NullTests#nullAcceptsLeadingAndTrailingTrivia`。

**为什么影响面这么大**：`DefaultValue.ofClass` 是所有 `_cs2jDefault_T()` 的落地实现，
"读到 null 就用默认值"的路径遍布读侧；把"默认值"从"新实例"改回 null 之后，
一整类"null 被当成对象"的假象同时消失。

**同族（C# 默认值 ≠ Java 默认值）第 6 例**：`default(T)`（§8.2.17）、`default(JsonElement)`（§8.2.21）、
访问器可见性（§8.2.29）、`ref` 回滚（§8.2.33）、默认 struct（§8.2.38）、**C# 类的 default(T)**（本条）。
**判据**：凡"期望 null / 期望空"的失败，先看 **C# 侧那个位置的默认值是什么**，
再对着生成物问"Java 的默认值是不是同一个"。


### 8.2.40 Round 80：否证 —— `matchesConstructorBindingFlags` 的"私有构造器桥"**不能收紧**（470 → 618，已回滚）

**动机**：`ObjectTests#readObjectFail_ReferenceTypeMissingPublicParameterlessConstructor`
（期望 `NotSupportedException`）失败在
`Activator.createInstance:26 ← ReflectionMemberAccessor.lambda$createConstructor$0:51`。
探针 `DefProbe20` 逐个查了该测试的 5 个类型：

| 类型 | `getConstructorInfo(Public)` | `Activator.createInstance` |
|---|---|---|
| `ClassWithInternalParameterlessCtor` | **null** ✓ | 调用被 `@CompatInternalCtor` 标记的构造器 → `Debug.fail` 抛出 |
| `ClassWithPrivateParameterlessCtor` | **非 null** ✗ | 同上（私有构造器被 `setAccessible` 后执行） |
| `CollectionWithoutPublicParameterlessCtor` | null ✓ | — |
| `ConcreteDerivedClassWithNoPublicDefaultCtor` | null ✓ | `No parameterless constructor defined` |

失败的是**第 2 个断言**：Java 侧显式 `private X()` 被
`matchesConstructorBindingFlags` 的"**非公开类里的非公开构造器按 C# 视为 public**"桥接规则
判成可用（该规则本意是覆盖"C# 私有嵌套类的**隐式**构造器在 C# 里是 public，而 Java 的隐式构造器是包私有"）。

**尝试**：在桥接规则里加"**显式 private 的 Java 构造器一律不算 public**"（隐式默认构造器是包私有，
可据此区分）。**实测 `470 → 618`：修 1 / 坏 149** —— `CustomConverterTests` 几乎全灭。

**结论（重要，勿重试）**：这条桥接规则是**承重**的 —— 转换器生成的一批"工厂化"类
（Round 20 记录过：`tsc TS2673` 场景下只剩**私有无参构造器**）正是靠它被反射找到。
任何"收紧私有构造器可见性"的改动都会把它们全部打掉。**回滚后重新生成 + 全量测量 = `470 → 470`** ✓。

**下一步若要修那条断言**，方向必须是"**只对用户类型收紧、对转换器工厂类保持宽松**"，
即需要一个新的标记（类似 `@CompatInternalCtor`）来区分两者 —— 而不是改桥接规则本身。

### 8.2.41 Round 81 已修：`Type.GetProperty(name, BindingFlags)` 丢了标志实参（470 → 467，修 3 / 坏 0）

**入口**：`DebuggerTests#defaultJsonElement / defaultJsonProperty / defaultUtf8JsonWriter`
（3 条，同一个辅助方法），栈是

```
DebuggerTests.getDebuggerDisplayProperty:54
  ← io.github.ningpp.compat.TypeHelper.getProperty(Class, String) 返回 null
  ← io.github.ningpp.compat.PropertyInfo.getValue(Object)
```

**两步定位**（都是"读原文"就能拿到的证据，不需要探针）：

1. C# 原文是 `typeof(T).GetProperty("DebuggerDisplay", BindingFlags.Instance | BindingFlags.NonPublic)`
   —— 生成物却只有 `getProperty(type, "DebuggerDisplay")`：**转换器把标志实参丢了**。
   同一文件里 `GetField` 的特例**有**标志分支，compat 也有 `getField(Class, String, int)`，
   只有 `GetProperty` 这条腿是残的。
2. 该属性在 C# 里是 `private string DebuggerDisplay => ...`，生成物是
   `@CompatNonPublicProperty @CompatNonPublicAccessor private String getDebuggerDisplay()`，
   而 `getProperty(Class, String)` 只扫 `type.getMethods()`（**只含 public**）→ 必然 null。

**修法**：① compat 加 `getProperty(Class, String, int)`：带 `NonPublic` 时沿类层次扫
`getDeclaredMethods()`（复用 `matchesBindingFlags`、跳过 synthetic/bridge、`DeclaredOnly` 只扫本类），
失败再回落 public-only 重载；**public-only 重载一字未改**（`PropertyVisibilityTests` 的 29 个
`getProperty(type, name)` 站点依赖"只认 public"）。② 转换器 `GetProperty` 特例补标志分支，
形状与 `GetField` 逐行对齐。全生成树带 3 实参的 `getProperty(` 只有本测试 **1** 处，影响面可控。

**实测**：`470 → 467`，修 3 / 坏 0；`Tests run: 7902, Failures: 232, Errors: 235, Skipped: 18`。
**纪律提示**：全文的 `GetXxx(name, flags)` 特例都要用同一个形状核对一遍 ——
"只有一个成员的特例补了标志、另一个没补"是本轮这种静默丢实参的温床。

### 8.2.42 Round 82 已修：Newtonsoft 替身把 `decimal` 当字符串写（467 → 463，修 4 / 坏 0）

**入口**：`Utf8JsonWriterTests#writeNumberValueDecimal(boolean, boolean)[1..4]`，
消息形状是"期望 `["1234567.0", …]`（**带引号**）、实际 `[1234567.0, …]`（裸数字）"。

**定位链**（纯读代码即可，无需探针）：

1. 断言走 `JsonTestHelper.assertContents` → `assertContentsAgainstJsonNet(expected, value)` →
   `Assert.equal(normalizeToJsonNetFormat(expected), normalizeToJsonNetFormat(value))`，
   即**两侧**都过一遍"Newtonsoft 风格重排"。所以差异不在 Utf8JsonWriter 侧，而在**期望串的生成侧**。
2. 期望串由 `GetNumbersExpectedString`（C# 泛型 `<T>`，此处 `T = decimal`）用
   **`JsonTextWriter.WriteValue(value)`** 生成。
3. compat 的 `JsonTextWriter.writeValue(Object)` 只对 `Number` / `Boolean` / `byte[]` / 日期
   做裸写，其余一律落到末尾的"**加引号 toString**"兜底。
   而 C# 的 `decimal` 是值类型，compat 的 `io.github.ningpp.compat.Decimal`
   **`implements Comparable<Decimal>`，不继承 `java.lang.Number`** —— 于是 `1234567.0M`
   被写成 `"1234567.0"`（Newtonsoft 会写裸数字）。

**修法**：在 `writeValue(Object)` 的 `Number` 分支后补 `Decimal` 分支，写 `dec.toString()`
（= `BigDecimal.toPlainString()` = `1234567.0`）。位置在 `writeBeforeValue()` **之后**，
这样数组分隔符/缩进仍由既有流程处理。

**实测**：`467 → 463`，修 4 / 坏 0（`writeNumberValueDecimal` 的 4 个 theory 行）；
`Tests run: 7902, Failures: 228, Errors: 235, Skipped: 18`。

**同族**（重要）：`Decimal` 不是 `Number` 这一点会在**所有** `instanceof Number` 判定处
重复咬人 —— `JsonConvert.serializeObject` 的 `value instanceof Number`（顶桶 `JsonConvert *`
的 70+ 条即此族）同样会把 `Decimal` 落到"加引号"分支。本轮只修了 writer 这一处；
若后续要修 `JsonConvert`，**优先考虑同样的显式分支**，而不是让 `Decimal extends Number`
（那会一次性改动所有 `instanceof Number` 判定点，代价不可控）。

### 8.2.43 Round 83 已修：属性桥**凭空造出非公开属性**（463 → 460，修 3 / 坏 0）

**入口**：`OptionsTests#defaultSerializerOptions_General / copyConstructor_OriginalLocked /
copyConstructor_IgnoreNullValuesCopied`，都死在同一个辅助方法
`OptionsTests.verifyOptionsEqual:653`（生成的）最后一行的 `Assert.same(...)`。

**定位**（探针 `DefProbe23`，打印 `PropertyInfo.getProperties(JsonSerializerOptions.class)` 的 21 条）：

```
PROP MemberAccessorStrategy type=dotnet.text.json.MemberAccessor  getter=<no public getter>
PROP ReaderOptions          type=dotnet.text.json.JsonReaderOptions getter=getReaderOptions
PROP WriterOptions          type=dotnet.text.json.JsonWriterOptions getter=getWriterOptions
PROP EffectiveMaxDepth      type=java.lang.Integer                 getter=<no public getter>
```

.NET 的 `typeof(JsonSerializerOptions).GetProperties()`（Public）**只返回 17 个**：
`MemberAccessorStrategy`/`EffectiveMaxDepth` 是 **internal 属性**，
`GetReaderOptions()/GetWriterOptions()` 是 **internal 方法**（根本不是属性）。
我们的桥按"`getXxx()` + 0 参数 + 非 void ⇒ 属性"的约定把它们全算成属性，
于是 `VerifyOptionsEqual` 对引用类型属性做 `Assert.Same` —— 两个 options 实例的
懒建 `MemberAccessor` 当然是两个对象 ✗；`JsonReaderOptions` 是 struct 但
`getIsValueType` 判 false，也会落到 `Assert.Same` ✗。

**修法**（两半，缺一不可）：

1. compat `PropertyInfo.getProperties(Class)`：跳过标了 `@CompatNonPublicProperty` /
   `@CompatNonPublicAccessor` 的 getter，并在最后 `removeIf(getGetMethod()==null &&
   getSetMethod()==null)`（整个属性在 C# 都不可见）。
2. 转换器 `MethodTransformer`：**非公开**、0 参数、非 void、名字以 `Get` 开头的 C# 方法
   打 `@CompatNonPublicAccessor`（本树 160 处）—— 这是第 1 步能认出
   `GetReaderOptions()` 这类**方法冒充属性**的唯一线索（Java 侧看不出 internal）。

**安全性核对**（`DefProbe24`）：`SimpleTestClass` 87 个属性、0 个被删（`CacheTests` 的
`> 64` 断言不受影响）；`JsonSerializerOptions` 21 → 19。

**实测**：`463 → 460`，修 3 / 坏 0；`Tests run: 7902, Failures: 225, Errors: 235, Skipped: 18`。

### 8.2.44 Round 83b：**机制确认** —— 70 条 `MyInt16TwoDimensionList[0]` 的根因是"**原始类**键的类信息被首次实例化冻结"

本轮顺带把最大簇（`reThrowWithPath` 106 条里的 70 条）查到了根因，**没有改代码**，
但这是后续实现"闭键类信息"设计的直接依据，务必先读本节再动手。

**症状**：70 条失败的消息都是
`The JSON value could not be converted to class io.github.ningpp.compat.CSharpList. Path: $.MyInt16TwoDimensionList[0]`
（`SimpleTestClass` 的 `List<List<short>>` 成员），栈顶都是
`StringConverter.read ← IEnumerableDefaultConverter.onTryRead:88`。

**探针 `DefProbe21`**（`probe/w42`）直接读最小 JSON：

```
== 2dimList THREW ... Path: $.MyInt16TwoDimensionList[0]
   cause[1] java.lang.IllegalStateException: Cannot get the value of a token type 'StartArray' as a string.
        at StringConverter.read(StringConverter.java:39)
        at IEnumerableDefaultConverter.onTryRead(IEnumerableDefaultConverter.java:88)
        at JsonConverter1.tryRead ... JsonPropertyInfo1.readJsonAndSetMember
== 1dimList（MyStringList）OK      == 2dimArray OK      == empty OK
```

`onTryRead:88` 是快路径里的 `elementConverter.read(...)`，而 `elementConverter` 取自
`state.Current.JsonClassInfo.ElementClassInfo.PropertyInfoForClassInfo.ConverterBase`。
拿到的是 **StringConverter** ⇒ 说明那份类信息的元素类型是 `String`。

**探针 `DefProbe22`** 打印运行期对象图（关键三行）：

```
MEMBER MyInt16TwoDimensionList  runtimePropertyType=CSharpList
      converter=ListOfTConverter  converterElementType=class io.github.ningpp.compat.CSharpList   ← 成员自己的转换器是对的
  CSharpList（_classes 里那一份） elementType=class java.lang.String
      ownConverter=ListOfTConverter  converterElementType=class java.lang.String                 ← 被冻成 String
    -> elementClassInfo type=java.lang.String ... StringConverter
```

**结论**：成员侧的转换器**本来就正确**（`element=CSharpList`），坏的是
`JsonSerializerOptions._classes`（`ConcurrentHashMap<Class, JsonClassInfo>`，**按原始 `Class` 键**）
里那一份 `CSharpList` 类信息：它的 `elementType`/`ownConverter` 是**第一次实例化**时定下的，
本类里第一次是 `List<string>`（`MyStringList`/`MyListOfNullString`）⇒ 全部 `List<T>` 共用
"元素是 String"的那一份。`IEnumerableDefaultConverter.onTryRead` 从**共享类信息**而不是从
**当前成员的转换器**取元素类型，于是把 `[10,11]` 当字符串读 ⇒ 抛异常。
（`_converters` 同样是按原始 `Class` 键的 `ConcurrentHashMap`，所以"临时新建一份类信息"
也救不了：新类信息里的转换器仍会命中那份冻结的转换器。）

**顺序是哈希序，不是声明序**：`myInt16TwoDimensionList` 声明在 `myStringList` **之前**
（生成物 126 行 vs 129 行），但 `PropertyCacheArray` 由 `cache.values().toArray()` 得到
（`JsonPropertyInfo` 字典序）⇒ "谁先冻上"取决于哈希顺序，所以这个簇看起来毫无规律。
**推论**：同一个 options 实例里，每个泛型原始类**只有一种实例化能工作**，
`SimpleTestClass` 这种"多成员共用 `List<>`"的类型必然失败 —— 这就是为什么它一次性吃掉 70 条。

**已有的一半机制**：`TypeHelper.cachedClassInfoForMember(member, options, rawType)`
（`JsonPropertyInfo.getRuntimeClassInfo()` 在用）已经会按 `ClosedGenericKey(raw, args)`
**查询**闭键条目，并且注释里明确写了"只针对单实参形状，多实参（Dictionary/ValueTuple）
保持原路径，否则 NullTests 回归（实测）"。缺的是**创建侧**：`_classes` 里从来没有闭键条目，
所以查询永远落空。往 `_classes` 写闭键条目必须同时在**同一实参上下文**里造出对应转换器
（`_converters` 也要闭键），否则新类信息仍旧拿到冻结的转换器 —— 这正是 §8.2.26/§8.2.30
记录过的"两侧同时补键"的设计，也是此前 `PatchConverterCacheLookup` 全局统一键
（855 → 934）失败的边界所在：**全局统一**会把无实参上下文的查询打散，
**只在"有实参上下文 + 单实参泛型 + 集合/字典类"这一窄带上创建与查询闭键**才是未试过的形状。

### 8.2.45 Round 84：否证 —— "把两个缓存换成上下文键"**修不动** 70 条嵌套列表簇（两个变体分别 460 → 581 / 460 → 632，均已回滚）

**动机**：§8.2.44 已用探针把 70 条簇定位到"`_classes` 里那份 `CSharpList` 类信息被首次实例化冻结"，
而 compat 侧已有一半机制（`cachedClassInfoForMember` 的闭键**查询**）。
本轮补上"创建侧 + 两个缓存都用闭键"，实测两遍，**均为强负收益且目标簇未修好**。

**变体 1（全量上下文键）**：compat 新增 `cacheKeyOrRaw`/`cacheHas`/`cacheGet`/`cachePut`；
转换器新增 pass `PatchCacheKeyedLookups`（`JsonSerializerOptions` 的
`_converters` 读/写 + `_classes` 取/存**全部**改走上下文键，无实参上下文时**回落原始类**并保留 raw 别名）；
`cachedClassInfoForMember` 在 miss 时**创建**闭键类信息（限集合/字典族）。

- 实测 `460 → 581`（`mvn-build-r153.log`）：**修 1 / 坏 122**。
- 新失败形状（都是"某个成员的类信息/转换器泄漏进无关读取"）：
  `Integer cannot be cast to String` 31 条、`public short SimpleTestClass.getMyInt16() target=java.lang.String` 22 条、
  `Integer cannot be cast to CSharpList` 13 条、`[I cannot be cast to String` 12 条；
  重灾区 `CollectionTests` 85 条。
- 目标簇**一条都没修好**（FIXED 只有 `ReferenceHandlerTests#dictionaryReferenceLoopInList`）。

**变体 2（收窄到"有作用域才换键"）**：`cacheKeyOrRaw` 只有在
`CACHE_CONTEXT_DEPTH > 0`（元素作用域 / 成员创建作用域）时才启用闭键，其余一律用原始类；
元素作用域额外把**元素自己的层**发布进 `TYPE_ARGUMENTS`（否则类信息按元素键缓存、
转换器却仍按线程上残留的层构建）；创建侧再收窄到**同原始类嵌套**（`List<List<…>>`，即"原始类槽位
可证明无法服务"的唯一形状）。

- 实测 `460 → 632`（`mvn-build-r154.log`）：**修 1 / 坏 173**，比变体 1 更差，目标簇仍未修好。
- 回滚后重新生成 + 全量测量 = `460 → 460`（逐用例集合差 0/0）✓。

**结论（勿再重试"换缓存键"这一形状）**：
在这套移植里，"每个原始类一份类信息/转换器"是**读路径的隐含前提** ——
读栈帧、属性缓存、帧上的 `JsonPropertyInfo` 都按**原始类**取用；
按实例化分裂条目会让别的类型的属性信息出现在帧里（上面那 22 条
`SimpleTestClass.getMyInt16() target=java.lang.String` 就是它）。同时，
**换键不是充分条件**：两次都没让目标簇前进一步，说明真正决定读路径的那份类信息
**根本不是** `getRuntimeClassInfo()`/`getOrAddClass` 这条链上的（§8.2.44 的探针只证明了
`_classes` 里那一份是 String 味，还没证明**帧里**用的是它）。

**下一步（若继续攻这 70 条）**：先用探针打印**读栈帧**里的 `JsonClassInfo`
（`state.Current.JsonClassInfo` 的身份 + 它是不是 `_classes` 里的那一份 + 它的 `elementType`），
把"谁把 String 味的类信息交给 `onTryRead`"钉死，再决定动哪里；
**不要**在没有这一步之前再动缓存键。

### 8.2.46 Round 85 已修：Newtonsoft 替身写 `byte[]` 时多插一个分隔符（460 → 444，修 16 / 坏 0）

**入口**：`Utf8JsonWriterTests#writeBase64String(boolean, boolean, String)[1..16]`（整个 theory 16 行），
站点 `JsonTestHelper.assertContentsAgainstJsonNet:722`，形状是"**期望**侧 JSON 语法坏掉、实际侧正常"：

```
Expected: {"message":,"AQIDBAUG","message":,"AQIDBAUG","array":[,"AQI=","AQI="]}
Actual:   {"message":"AQIDBAUG","message":"AQIDBAUG","array":["AQI=","AQI="]}
```

**定位**：断言两侧都过 `normalizeToJsonNetFormat`，且**期望**串由 `getBase64ExpectedString` 用
compat 的 `JsonTextWriter` 生成（`json.writePropertyName(...)` + `json.writeValue(byte[])`）。
`writeValue(Object)` 的分支顺序是：先在第 185 行调用一次 `writeBeforeValue()`，
随后 `byte[]` 分支**又递归调用 `writeValue(String)`**，而后者开头**再调用一次**
`writeBeforeValue()` ⇒ 值前面多出一个逗号。

**探针 `DefProbe25`** 直接复现（不经过测试框架）：

```
修复前 OUT={"message":,"AQIDBAUG","message":,"AQIDBAUG","array":[,"AQI="]}
对照 CTRL={"n":5}                     ← 走 Number 分支（不递归）时正常
修复后 OUT={"message":"AQIDBAUG","message":"AQIDBAUG","array":["AQI="]}
```

**修法**：`byte[]` 分支不再递归，改为**就地**写带引号的 Base64
（`writer.print('"') + escapeJsonString(Base64.encode) + writer.print('"')`）——
与 Round 82 给 `Decimal` 加的分支同一形状（都是"在 `writeBeforeValue()` 之后直接写值"）。

**实测**：`460 → 444`，修 **16** / 坏 0；`Tests run: 7902, Failures: 209, Errors: 235, Skipped: 18`。
纯 compat 改动，无需重新生成。

**纪律提示**：`writeValue(Object)` 里任何"委托给另一个 `writeValue` 重载"的分支都会二次调用
`writeBeforeValue()`；新增分支必须就地写值。本簇是"**期望侧**生成器坏了"而不是被测 writer 坏了 ——
凡是"Expected 侧语法明显不对"的失败，先查 compat 的 Newtonsoft 替身，不要查 `Utf8JsonWriter`。

### 8.2.47 已修（Round 86）：Newtonsoft 替身忽略 `StringEscapeHandling.EscapeNonAscii`（444 → 436，修 8 / 坏 0）

**入口**：`Utf8JsonWriterTests#highSurrogateMissingGetsReplaced(boolean, boolean)[1..4]`（本轮目标），
外加**顺带修好**的 `Utf8JsonWriterTests#escapeCharacters(boolean, boolean)[1..4]`。

**定位**：本站点的断言**不做解析**，`WriterHelpers.normalizeToJsonNetFormat` 只是字符串变换
（把 `\uXXXX` 的十六进制转小写、把 `\/` 换成 `\u002f`），所以两侧是**原文对比**。
失败形状是：

```
Expected: { "a𐀀𐹭𝄞�ffffa": "a𐀀𐹭𝄞�ffffa" }        ← 原始星光字符（期望侧）
Actual:   { "a\ud800\udc00…a": "a\ud800\udc00…a" }   ← 转义（被测 writer 的默认编码器）
```

C# 原文 `GetEscapedExpectedString(..., StringEscapeHandling.EscapeNonAscii)`：
期望串由 compat 的 `JsonTextWriter` 生成，而 `escapeJsonString` 只认 `Default` 与 `EscapeHtml`，
`EscapeNonAscii` 落到"原样输出非 ASCII" ⇒ 期望侧永远不带转义。

**修法**（compat，纯改一处）：`escapeJsonString` 增加 `escapeNonAscii` 判定 ——
扫是否需要转义时加 `(escapeNonAscii && ch > 0x7F)`，写值时同样加该条件（按 UTF-16 码元逐个转义，
大写十六进制，正好被规范化器转小写后与被测 writer 的小写输出对齐）。

**探针** `probe/w42/EscNonAsciiProbe.java`：

```
EscapeNonAscii = { "a\uD800\uDC00\uD803\uDE6D\uD834\uDD1E\uDBFF\uDFFFa": "…" }   ← 修复后
Default        = { "漢字": "漢字" }                                              ← 默认仍原样（不回归）
```

**实测**：`444 → 436`，修 **8** / 坏 0（`highSurrogateMissingGetsReplaced` 4 + `escapeCharacters` 4）；
`Tests run: 7902, Failures: 201, Errors: 235, Skipped: 18`。纯 compat 改动，无需重新生成。

**坑（Java 语言层）**：在 Java 源文件里**注释**中写 `\uXXXX` 字样会直接编译失败
（Unicode 转义在词法分析前处理，"非法的 Unicode 逃逸"）—— 注释里要用文字描述。

### 8.2.48 下一轮候选（Round 87）

`assertContentsAgainstJsonNet` 站点在 Round 86 后仍有 10 条，剩三簇：

| 簇 | 条数 | 观察 | 入口 |
|---|---|---|---|
| `writeStringsWithRelaxedEscaping(boolean, boolean, String, boolean)` | 4 | 期望名字里是 `\u0022`，实际名字位置是**裸 `"`**（归一化后仍是非法 JSON） | compat `JsonTextWriter.writePropertyName` 路径 vs `escapeJsonString`（注意本站点双方都用同一 `escapeJsonString`，先探针确认哪一侧写的名字） |
| `writeCommentsInvalidTextAllowed(boolean, boolean)` | 4 | 期望在未配对代理项处**截断**（`surrogate */`），实际是 `surrogate � this part no longer visible*/` | `JsonTextWriter.writeComment` 对未配对代理项的处理（写入端似乎提前结束） |
| `writeNumber_NullPropertyName_UInt32/UInt64` | 2 | `{"":2147483648}` vs `{"":-2147483648}`（`uint`/`ulong` 渲染） | 转换器把 `WriteNumber(string, uint)` 映射到 `int` 重载 ⇒ 需按 C# 重载区分（`writeNumberValue_uint` 之类）或让 `Cs2jUnsigned` 参与 |

**共同纪律**：这一站点的失败几乎都是"两侧都过**字符串级**归一化"，第一步永远先分清
"坏的是期望侧生成器（compat 的 Newtonsoft 替身）还是被测 writer" —— Round 82/85/86 三条都是前者。

### 8.2.49 已修（Round 87）：注释在未配对代理项处**不截断**（436 → 432，修 4 / 坏 0）

**入口**：`Utf8JsonWriterTests#writeCommentsInvalidTextAllowed(boolean, boolean)[1..4]`。形状：

```
Expected: /*comment is / * valid*//*comment is /* valid*//*comment is / * valid even with unpaired surrogate */
Actual:   /*comment is / * valid*//*comment is /* valid*//*comment is / * valid even with unpaired surrogate � this part no longer visible*/
```

**关键一步是"读原文"**：C# 测试的**期望串**用的是**短字面量**
（`Utf8JsonWriterTests.cs:4099`：`comment = "comment is / * valid even with unpaired surrogate ";`），
而被测 writer 收到的是**长字面量**（`:4064`，含 `\udc00 this part no longer visible`）。
即：**.NET 侧本来就会把长文本在非法代理项处丢掉尾巴**。
SDK 源码印证：`Utf8JsonWriter.WriteValues.Comment.cs:83-86` 调
`JsonWriterHelper.ToUtf8(...)`，只 `Debug.Assert(status != DestinationTooSmall)` ——
编码器在非法 UTF-16 处停下并只报告**有效前缀**的字节数，剩余内容**静默丢弃**。

**修法**（两半）：compat 加 `MemoryMarshal.truncateAtInvalidUtf16(ReadOnlySpan<Character>)`
（返回首个非法 UTF-16 单元之前的前缀），转换器新增 pass `PatchCommentInvalidUtf16Truncation`
（只挂在 `Utf8JsonWriter.java`，在 `writeCommentMinimized`/`writeCommentIndented` 入口注入一行），
**不去动那份机翻的 `toUtf8` 状态机**。

**实测**：`436 → 432`，修 4 / 坏 0；`Tests run: 7902, Failures: 197, Errors: 235, Skipped: 18`。

**同轮中性改动（保留并披露）**：`43914058` 让 compat 的 `StreamWriter`
用 **U+FFFD** 替换非法 UTF-16（Java 的 `OutputStreamWriter` 默认写 `?`，.NET 写 U+FFFD）。
本集合实测 `436 → 436`（逐用例集合差 0/0；同名用例的消息差异只有 `Object@hash` 这类
运行期不确定性），但它把 `StreamWriter` 的编码语义对齐到 .NET，按既有先例保留。
**残留**：`writeStringsWithRelaxedEscaping` 4 条与 `writeNumber_NullPropertyName_UInt32/UInt64` 2 条仍是开放项
（见 §8.2.48 的表格，relaxed 那条的入口是"属性名写路径的引号处理"）。

### 8.2.50 已修（Round 88）：Newtonsoft 替身的**引号形式随 `StringEscapeHandling` 变**（432 → 428，修 4 / 坏 0）

**入口**：`Utf8JsonWriterTests#writeStringsWithRelaxedEscaping(boolean, boolean, String, boolean)[9..12]`
（含引号的那四行）：期望 `">><++>>>\u0022>>…"`，实际 `">><++>>>\">>…"`。

**两次被否证的假设（都留了实测数据，勿重试）**：

| 假设 | 做法 | 实测 |
|---|---|---|
| "我们的 writer 该写 `\u0022`" | 把 compat `JavaScriptEncoder.asciiEscape` 的 relaxed 短形式去掉（引号一律 unicode 转义） | `432 → 434`：**修 4 坏 6**（`ValueTests#writeStringWithRelaxedEscaper`、`JsonDocument/JsonElementWriteTests#writeWithRelaxedEscaper`、`writingStringsWithCustomEscaping`） |
| "Newtonsoft 一律写短形式" | compat `JsonTextWriter` 的引号一律 `\"` | `432 → 432`：**修 4 坏 4**（`escapeAsciiCharacters[1..4]`） |

**决定性证据在 C# 原文里，两个编码器的行为是**不同**的**（`Utf8JsonWriterTests.cs:151-177`，同一个测试）：

```
默认编码器：      WriteStringValue("é\"") → 期望 "\u00E9\u0022"
UnsafeRelaxed：   WriteStringValue("é\"") → 期望 "\u00E9\""
```

⇒ 被测 writer 的两种输出都是**对的**；错的只能是**期望侧生成器**。
再看两个期望助手的差异：`getExpectedString_RelaxedEscaping`（C# `:7501`）**不设** `StringEscapeHandling`，
而 `escapeAsciiCharacters`（`:4338`）传 `StringEscapeHandling.EscapeHtml` ——
Newtonsoft 的引号是按 handling 走的：**`EscapeHtml` ⇒ unicode 转义；`Default`/`EscapeNonAscii` ⇒ 短形式 `\"`**。

**修法**（compat 一处）：`JsonTextWriter.escapeJsonString` 的引号分支改为

```java
sb.append(escapeHtml ? "\\u0022" : "\\\"");
```

**实测**：`432 → 428`，修 4 / 坏 0（relaxed 四行全 FIXED）；`Tests run: 7902, Failures: 193, Errors: 235, Skipped: 18`。

**纪律**：同一"期望侧生成器"的两个助手可能因为 `StringEscapeHandling` 不同而**要求不同的转义形式**；
先用 `Utf8JsonWriterTests.cs:151-177` 那种"同一输入、两个编码器"的原文断言把两侧行为钉死，再改代码。

### 8.2.51 Round 89：探针 `DefProbe26` 把"**帧用哪份类信息**"钉死（未改码）

§8.2.45 结尾要求"先打印**读栈帧**用的那份类信息"。`probe/w42/DefProbe26.java` 直接问
`JsonPropertyInfo.getRuntimeClassInfo()`（这正是帧在 `onTryRead` 里访问的那条链）：

```
shared CSharpList in _classes: elementType=class java.lang.String  ownConverterElement=class java.lang.String  identity=1635546341
member MyInt16TwoDimensionList
   own converter element      = class io.github.ningpp.compat.CSharpList      ← 成员转换器正确
   getRuntimeClassInfo()      = CSharpList identity=1635546341 sameAsShared=true  ← 帧拿到的是**共享那份**
   its elementType            = class java.lang.String
   its own converter element  = class java.lang.String
   elementClassInfo           = String ownConverterElement=null
```

**结论**：帧用的就是 `_classes` 里被首次实例化冻结的那一份（同一对象），
元素类型 String ⇒ `getElementClassInfo()` 直接给出 String 类信息 ⇒ `StringConverter` ⇒ 那 70 条。
所以"换缓存键"（§8.2.45 两个变体）**本应**有效，却一条未修 ⇒ 说明**闭键条目根本没被创建/命中**。

**下一轮的第一个动作（可验证的假设）**：在**读时**检查
`TypeHelper.memberArguments(<该 JsonPropertyInfo 的 memberInfo>)` 是否非空 ——
`cachedClassInfoForMember` 的创建分支依赖 `MEMBER_DECLARED_TYPES`（由 compat `PropertyInfo/FieldInfo`
构造器里的 `registerMemberType(type, this)` 填充），而生成物的 `JsonPropertyInfo` 持有的
`MemberInfo` **可能是另一个对象**（SDK 反射路径产生的），于是 `beginMemberScope` 直接返回 null、
创建退化为原始键、并把共享条目原样返回 —— 这与"变体 1 修 1 / 坏 122、变体 2 修 1 / 坏 173、目标簇不动"完全吻合。
**验证手段**：`java -Dcs2j.trace.typeargs=true` 已经会打印
`cachedClassInfoForMember(...) HIT/CREATED` 与 `cachePut(...)`，先看这两行有没有出现。

### 8.2.52 Round 90：`DefProbe27` 否证了 §8.2.51 的假设，并给出**真正的原因**（未改码）

`probe/w42/DefProbe27.java` 取生成物 `JsonPropertyInfo` 真正持有的 `MemberInfo`（反射调 `getMemberInfo()`）：

```
member MyInt16TwoDimensionList
   memberInfo class         = io.github.ningpp.compat.PropertyInfo        ← 就是注册过的那个对象
   memberArguments(...)     = [io.github.ningpp.compat.CSharpList<java.lang.Integer>]   ← 注册**在**，假设被否证
   cachedClassInfoForMember = null                                        ← 闭键条目确实不存在（当前树未实现创建）
member MyStringList
   memberArguments(...)     = [class java.lang.String]
   cachedClassInfoForMember = null
```

**修正后的解释（为什么 §8.2.45 的两个变体"创建了条目"却没修好目标）**：
生成物的 `getRuntimeClassInfo()` 把结果缓存在**每个 JsonPropertyInfo 实例**的
`_runtimeClassInfo` 字段里，而 `cachedClassInfoForMember` **只在 `_runtimeClassInfo == null` 时才会被调用**。
只要该成员的类信息在**预热阶段**（成员缓存构建时）就已经解析成共享那份，之后无论怎么创建闭键条目都**永远不会再查**。
⇒ 正确顺序必须是"**在第一次 `getRuntimeClassInfo()` 之前**就让闭键条目存在"
（即在 `JsonPropertyInfo` 构造/`determineConverter` 那段、**成员实参仍在上下文里**时创建，
或让 `_runtimeClassInfo` 的解析不走"先到先得"的缓存）。

**下一轮的第一个动作**：探针打印 `_runtimeClassInfo` 是**何时**被填充的
（在 `getMemberInfo()`/预热之后立刻读一次该字段：非 null ⇒ 预热期就定了，验证上述解释），
再决定改"创建时机"还是改"缓存策略"。

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
| **确认那次运行真的跑了测试，再读失败数** | Round 55 踩过：构建在测试模块之前失败时 `surefire-reports` 里是**上次的 XML**，`triage_xml.py` 把它当成本次结果，产出**从未发生过的失败数**（一个假的"−30"）。判据：日志里 `Tests run: 7902` 必须**恰好 1 次**，且无 `mvn <args> -rf :<module>` |
| **改补丁要连转换器一起重编** | 只改 `ProjectPasses.cs` 不 `dotnet build`，再生仍用旧补丁；反之生成物变了但转换器没重编，会让"我改了"与"生成物变了"脱节 |
| **grep 的零结果是弱证据** | Round 60 踩过：搜 `.getPolicies(` 没结果，就断定"从未被调用"；生成代码其实是**裸调用** `getPolicies(`。**换一种形状再搜一次，或直接读那段生成代码**，再下结论 |
| **再生不一定重写每个文件** | Round 60 发现 `ReflectionMemberAccessor.java` 的 mtime 仍是 Round 49 —— 该文件那次没有再生，所以针对它的补丁不会生效。判断"补丁没生效"前先看文件 mtime |
| **`summary lines: 0` 也要当成"没跑测试"** | Round 63 踩过：新的 `@CompatJsonIgnore(2)` 让 `system-text-json-tests` **编译失败**，测试阶段根本没执行，`triage_xml.py` 于是报出 `568 → 0`（"修了 568 条"）。判据同上：`Tests run: 7902` 必须恰好 1 次；`[ERROR] ... .java:[行,列]` + `BUILD FAILURE` 是最快的定位入口 |
| **改生成代码里的注解，先看注解元素名** | 单元素注解省略名字时调用的必须是 `value()`。Round 63 用了 `condition()`，javac 报"找不到符号: 方法 value()"。生成物里的注解都是单元素简写，所以 compat 侧统一用 `value()` |
| **改完先按"消息签名"看全局，再挑条目** | Round 72 起用 `tools/sig_group.py`（首行签名）+ `tools/equal_group.py`（Expected/Actual 形状）：539 条只有 104 个签名，其中 174 条挤在"Values differ"里但内部分 59 种形状（异质），真正可动手的是一条 7 条的 NPE 签名。**先分组再挑，比按桶名猜快得多** |
| **compat 返回 null 的"安全替代"会在生成代码里被解包** | `MemoryMarshal.getReference(ReadOnlySpan)` 对空 span 返回 null 是**有意的**（C# 悬垂 ref 的替代），但生成代码常写成 `int x = getReference(...)`，于是空输入直接 NPE，而 C# 那侧的分支**根本不读**。修法是让**调用点**先判长度，不是改 compat 的契约（别处可能靠 null 判空） |

### 8.4 已否证方向清单

见 §6.2（8 条，含各自的实测数据）与 §4.34/§4.35 的两个"封案"结论。
**不要在未读 §6.2 的情况下重试这些方向。**

本会话新增的三条（都有独立测量，勿重试）：

| 方向 | 实测 | 出处 |
|---|---|---|
| 给**类信息**缓存补实参维度（成员侧创建闭键条目 ± 元素侧闭键查询） | 539 → 576（修 1 / 坏 38），带 `ELEMENT_SCOPE_DEPTH` 守卫后**结果逐条一致** → 代价全部来自成员侧创建 | §8.2.25（round 41 的同向测量为 +19） |
| 让通用 `TypeHelper.getIsValueType` 认得 `value class` | 548 → 547（修 5 / 坏 4）；可空 struct 在运行期**无法**与不可空区分（探针两行完全相同） | §8.2.23 / §8.2.27 |
| 让擦除类的类信息**与反射顺序无关**（清空成员实参上下文创建） | 539 → 665（修 3 / 坏 129）：`List<SimpleTestClass>` 这类成员的类信息本来就依赖成员实参 | §8.2.26 |
| `ImmutableDictionary` 直接实现 `CSharpReadOnlyDict` | 不编译（`IImmutableDictionary.clear()` 返回自身 vs `Collection.clear()` void）；转换器的泛型上界会插入隐式 checkcast，13 条 `ClassCast` 由此而来 | §8.2.7 |
| 把 `_converters`/`_classes` 换成"上下文闭键"（含成员侧创建闭键条目） | `460 → 581`（修 1 / 坏 122）；收窄到"有作用域才换键 + 同原始类嵌套才创建"后 `460 → 632`（修 1 / 坏 173），**两个变体都没修好那 70 条**。读路径依赖"每个原始类一份类信息"这一前提 | §8.2.45 |

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
