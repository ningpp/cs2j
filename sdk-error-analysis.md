# SDK 编译错误分析

## Iteration 1 — MemberAccessor 方法签名不匹配

### 错误列表

1. **JsonPropertyInfo1.java:[78,51]** — `createPropertyGetter` 参数数量不匹配（需要 `Class<TProperty>, PropertyInfo`，实际只传 `PropertyInfo`）
2. **JsonPropertyInfo1.java:[83,51]** — `createPropertySetter` 同上
3. **JsonPropertyInfo1.java:[91,51]** — `createFieldGetter` 同上
4. **JsonPropertyInfo1.java:[94,51]** — `createFieldSetter` 同上
5. **ReflectionEmitMemberAccessor.java:[37,14]** — 未实现 `createParameterizedConstructor(Class<T>, Class<TArg0>, Class<TArg1>, Class<TArg2>, Class<TArg3>, ConstructorInfo)`（缺少 `Class<T>` 参数）
6. **ReflectionEmitMemberAccessor.java:[111,129]** — `BiConsumer<TCollection, Object>` 与 `DynamicMethod` 类型不兼容
7. **ReflectionMemberAccessor.java:[39,14]** — 未实现 `createFieldSetter(Class<TProperty>, FieldInfo)`（缺少 `Class<TProperty>` 参数）
8. **ImmutableDictionaryOfTKeyTValueConverter.java:[64,99]** — 泛型类型推断等式约束不兼容

### 根因分析

- **对应 C# 文件**:
  - `d:\csharp-sdk\json\src\System\Text\Json\Serialization\MemberAccessor.cs`（抽象基类）
  - `d:\csharp-sdk\json\src\System\Text\Json\Serialization\ReflectionMemberAccessor.cs`（override 实现）
  - `d:\csharp-sdk\json\src\System\Text\Json\Serialization\ReflectionEmitMemberAccessor.cs`（override 实现，在 `#if NETFRAMEWORK || NETCOREAPP` 内）
  - `d:\csharp-sdk\json\src\System\Text\Json\Serialization\JsonPropertyInfoOfT.cs`（调用方）

- **根因分类**: Transformer 逻辑缺陷

- **涉及组件**: `d:\code\cs2j\src\CSharpToJava.Core\Transformers\Member\MethodTransformer.cs` — `AddPendingRuntimeClassParameters` 方法

- **分析**: 
  转换器在处理泛型方法的 override 时，使用 `RuntimeClassParameterHelper` 扫描方法体中 `typeof(T)` 等模式来决定是否需要注入 `Class<T> _cs2j_T` 参数。当 override A（如 `ReflectionEmitMemberAccessor`）的方法体使用了 `typeof(T)` 时，会触发 `EnsurePendingParamsInBaseAndImpl` 向 override A 自身和 base 抽象方法注入 `_cs2j_T` 参数。但 override B（如 `ReflectionMemberAccessor`）的方法体可能不使用 `typeof(T)`，因此 `GetOverridePendingTypeParams` 返回空列表，不会注入参数。这导致 base 方法签名有 `_cs2j_T` 参数但 override B 没有，造成 Java 编译错误。

  此外，`ReflectionEmitMemberAccessor` 在 `#if NETFRAMEWORK || NETCOREAPP` 条件编译内，目标框架是 `net10.0`，条件不满足，但转换器仍然处理了它。而 `ReflectionMemberAccessor` 的 `createParameterizedConstructor` 方法体使用了 `typeof(T)` 但没有使用 `typeof(TArg0)` 等，所以 `T` 被注入但 `TArg0`-`TArg3` 没有，而 base 方法签名（由 `ReflectionEmitMemberAccessor` 触发）可能有 `TArg0`-`TArg3`。

  核心修复方向：当处理 override 方法时，即使当前 override 的方法体不需要 Class token，也必须检查 base 方法签名是否已有 `_cs2j_T` 参数，如果有则同步注入。

✅ Fixed（通过预处理器 abstract→virtual 改写 + override typeof(T) 同步）

## Iteration 2 — Class<X>无法转换为Class<Y> 类型不兼容

### 错误列表

1. **JsonPropertyInfo.java:[83,125]** — `Class<CSharpSByte>` 无法转换为 `Class<Byte>`
2. **ByteConverter.java:[33,49]** — `Class<CSharpByte>` 无法转换为 `Class<Integer>`
3. **IEnumerableConverterFactory.java:[49,173]** — `Class<CSharpGenericIterable>` 无法转换为 `Class<CSharpGenericIterable<?>>`
4. **IEnumerableConverterFactory.java:[50,156]** — `Class<CSharpGenericIList>` 无法转换为 `Class<CSharpGenericIList<Object>>`
5. **KeyValuePairConverter.java:[50,33]** — `Class<CSharpKeyValuePair>` 无法转换为 `Class<CSharpKeyValuePair<TKey,TValue>>`
6. **SByteConverter.java:[33,50]** — `Class<CSharpSByte>` 无法转换为 `Class<Byte>`
7. **UInt16Converter.java:[33,51]** — `Class<CSharpUInt16>` 无法转换为 `Class<Integer>`
8. **UInt32Converter.java:[34,51]** — `Class<CSharpUInt32>` 无法转换为 `Class<Integer>`
9. **UInt64Converter.java:[33,51]** — `Class<CSharpUInt64>` 无法转换为 `Class<Long>`
10. **ReflectionEmitMemberAccessor.java:[112,129]** — `BiConsumer<TCollection, Object>` 与 `DynamicMethod` 不一致

### 根因分析

- **对应 C# 文件**:
  - `D:\csharp-sdk\json\src\System\Text\Json\Serialization\Converters\Value\SByteConverter.cs`（`SByteConverter : JsonConverter<sbyte>`）
  - `D:\csharp-sdk\json\src\System\Text\Json\Serialization\Converters\Value\ByteConverter.cs`（`ByteConverter : JsonConverter<byte>`）
  - `D:\csharp-sdk\json\src\System\Text\Json\Serialization\Converters\Value\UInt16Converter.cs` 等
  - `D:\csharp-sdk\json\src\System\Text\Json\Serialization\Converters\Value\KeyValuePairConverter.cs`
  - `D:\csharp-sdk\json\src\System\Text\Json\Serialization\Converters\Collections\IEnumerableConverterFactory.cs`
  - `D:\csharp-sdk\json\src\System\Text\Json\Serialization\JsonConverterOfT.cs`（`JsonConverter<T>` 基类）

- **根因分类**: Transformer 逻辑缺陷

- **涉及组件**:
  - `d:\code\cs2j\src\CSharpToJava.Core\Transformers\Utilities\RuntimeClassParameterHelper.cs` — `GetClassLiteral` 方法（将 `typeof(sbyte)` 映射为 `CSharpSByte.class`）
  - `d:\code\cs2j\src\CSharpToJava.Core\Transformers\Expression\Transformers\IdentifierExpressionTransformer.cs` — 类型映射（`sbyte` → `Byte`, `byte` → `Integer`）

- **分析**:
  C# 的无符号/特殊数值类型（sbyte, byte, ushort, uint, ulong）在 Java 中有两种映射：
  1. **泛型类型参数** 映射为 Java 包装类型（Byte, Integer, Long 等）
  2. **typeof()** 映射为 CSharpXxx.class（CSharpSByte, CSharpByte 等）

  `JsonConverter<T>` 的 C# 构造函数是无参的，但转换器在 Java 版（`JsonConverter1<T>`）中添加了 `Class<T> tClass` 参数。当派生类（如 `SByteConverter : JsonConverter<sbyte>`）调用基类构造函数时，C# `sbyte` 映射为 Java `Byte`（泛型参数 T=Byte），但 `typeof(sbyte)` 映射为 `CSharpSByte.class`（Class<CSharpSByte>），导致 `Class<CSharpSByte>` 无法传给 `Class<Byte>` 参数。

  对于 `IEnumerableConverterFactory` 和 `KeyValuePairConverter`，问题是泛型类型参数的 Class 字面量缺少泛型参数信息（raw type vs parameterized type）。

## Iteration 3 — 转换回退到 flat 单模块扫描（非 Java 编译错误，Step 1 失败）

> 目标目录切换为 `d:\cs-json-0911-sdk`（prompt 更新为 0911）；本条为本轮循环的第一个阻塞项。
> 命令：`dotnet run -c Release --project src/CSharpToJava.CLI/CSharpToJava.CLI.csproj -- convert-project -s D:\csharp-sdk\json -d d:\cs-json-0911-sdk --extra-deps io.github.ningpp:system-private-xml:0.0.1-SNAPSHOT --no-verify-build --no-verify-tests`

- **现象**:
  1. 转换命令退出码 = 1：`Conversion complete: 1834 succeeded, 3 failed`，3 个文件因 `CS2J3102 [platform-boundary]` 被拦截（`json/tests/TestUtilities.cs`、`uri/tests/Common/System/PlatformDetection.cs`、`regular-expressions/tests/Regex.Match.Tests.cs`）。
  2. 输出形态错误：产物被拍平成**单模块**（根 `pom.xml` + 1832 个 `.java` 全在 `src/main/java`），而不是历史上 0905 循环使用的 **MSBuild 多模块**工作区（每 C# 工程一个 Maven module）。证据：`sdk-error-analysis.md` Iteration 1 中的 `ReflectionEmitMemberAccessor.java` 只存在于多模块产物中（`D:\a20260911\system-text-json\src\main\java\...\ReflectionEmitMemberAccessor.java`），flat 产物缺失该文件。
  3. 平铺产物 `mvn clean install -e` 报 **18532** 个编译错误（`dotnet/xml/**`、`dotnet/text/json/**`、`dotnet/text/regularexpressions/**` 等），大量根因为 `Decimal`/`MemoryExtensions`/`MemorySegmentHelper`/`StringHelper`/`DateTimeKind`/`ReadOnlySequence` 类缺失、`org.junit.jupiter` 缺失、`Utf8JsonReader` 引用不明确等 —— 这些都是多模块产物中本已通过编译的代码。

- **对应 C# 文件**: 不适用（流水线选择问题，非单个 C# 文件）

- **根因分类**: 语义模型信息丢失（预处理器把解决方案文件丢失，导致 `SolutionLoader` 无法用 MSBuild 打开工程）

- **涉及组件**:
  - `d:\code\cs2j\src\CSharpToJava.CLI\ProjectDiscovery.cs` — `TryResolveProjectEntry`（只查目录**顶层**的 `.sln`/`.csproj`）
  - `d:\code\cs2j\src\CSharpToJava.CLI\ProjectPreprocessSource.cs` — `MapToIntermediate` / `ResolveRestoreTarget`
  - `d:\code\cs2j\src\CSharpToJava.Core\Workspace\SolutionLoader.cs` — `TryOpenAsync`（`Source` 为目录时只查顶层，之后 `TryResolveNestedProjectEntry` 只查 `<dir>\<sub>\*.csproj`）

- **分析**:
  `-s D:\csharp-sdk\json` 是目录，`ResolveLayout` 解析到目录内的 `System.Text.Json.sln`，但布局的 `SourceRoot` 是工程闭包的公共根 `D:\csharp-sdk`，于是 `.sln` 被复制到 `<stage>\json\System.Text.Json.sln`（**嵌套一层**），`ProjectFiles` 也被记录为嵌套路径；`MapToIntermediate` 却把 `.sln` 入口映射成"拍平"到 stage 根目录的 `<stage>\System.Text.Json.sln`（不存在）。
  由于 stage 根目录既没有顶层 `.sln` 也没有顶层 `.csproj`，`ProjectDiscovery.TryResolveProjectEntry`、`SolutionLoader.TryOpenAsync`、`ResolveRestoreTarget` 三处全部返回失败：
  - 后续每个预处理阶段（strip-default → unsigned → generic → readonly → goto）拿到的 `SourcePath` 都是 stage **目录**，`ProjectFiles` 为空 → 复制工程文件时不再复制 `.sln`（各预处理器对非 `.cs` 文件里的 `.sln` 显式 `continue`，因为"上面已复制"）→ `.sln` 从第 2 阶段起彻底丢失；
  - 最终 `opts.Source` = `.cs2j-no-goto-src` 目录，`SolutionLoader.TryOpenAsync` 找不到任何工程入口 → 回退到 `ConversionPipeline.ConvertProjectWithPartialMergeAsync` 的**平铺目录扫描**（单 compilation、单 module、`isTestProject=false`）。
  - 平铺路径还导致 `ProjectPlatformBoundaryCheckPass` 用库级 `IsTestProject=false` 判定，把测试代码里的 `OperatingSystem.IsWindows` / `RuntimeInformation.IsOSPlatform` 探测当成 Error 拦截，于是 3 个测试文件既不产出也不报警告 → Step 1 退出码 1。

- **修复**（仅改转换器代码，未改任何生成产物）:
  1. `SolutionLoader.TryResolveNestedSolutionEntry`（新增，public 供 CLI 复用）：目录顶层无 `.sln` 时，在**直接子目录**里找唯一 `.sln`；顶层有 `.sln` 时优先顶层；多个候选则放弃（避免静默丢掉其它模块）。
  2. `SolutionLoader.TryOpenAsync`：目录分支在 `TryResolveNestedProjectEntry` 之前调用上述解析，保持 MSBuild 多模块流水线。
  3. `ProjectDiscovery.TryResolveProjectEntry`：顶层没有任何入口时调用同一解析，使每个预处理阶段都能拿到 `.sln` 入口 → `ProjectFiles` 非空 → `CopyProjectFilesAsync` 每阶段都把 `.sln` 复制到 `<stage>\json\...`，`.sln` 不再丢失。
  4. `ProjectPreprocessSource.MapToIntermediate`：`.sln` 入口改为按"相对 `SourceRoot` 的真实复制位置"映射（与 `CopyProjectFilesAsync` 一致），修掉拍平到 stage 根目录导致路径不存在的问题。
  5. `ProjectPreprocessSource.ResolveRestoreTarget`：目录分支增加嵌套 `.sln` 兜底，保证 `dotnet restore` 有目标（否则 MSBuildWorkspace 打开工程时 MetadataReferences 为空，会产出错误代码）。

- **红→绿验证**:
  - 红：`dotnet test -c Release --filter "FullyQualifiedName~ResolveRestoreTarget|FullyQualifiedName~TryResolveNestedSolutionEntry|FullyQualifiedName~TryResolveProjectEntry"` 编译失败（`SolutionLoader 未包含 TryResolveNestedSolutionEntry 的定义`）+ 断言失败（`ResolveRestoreTarget` 返回 null）。
  - 绿：同一筛选命令 17/17 通过。
  - 回归：全量 `dotnet test -c Release` → 3171 项中 2 项失败（`LocalFunctionLiftTests.StaticLocalFunction_LiftedAsPrivateStatic`、`LocalFunctionLiftTests.Skip_Iterator`）+ `StripDefaultProjectTests.CrossProject_CallSiteUnchanged_AndBuildSucceeds`，均已用 `git worktree` 在 HEAD（9f2db495）复现为**既有失败**，与本次修改无关；`StripDefaultProjectTests.Rerun_DoesNotLeaveOldArtifacts` 的失败经查为测试夹具目录被历史构建产物污染（清理 `tests/SampleStripDefaultSolution` 的 `bin/obj/.test-output-*` 后恢复通过）。

- **环境准备（本轮必需，非生成产物改动）**:
  `mvn clean install -e` 因 `io.github.ningpp:system-private-xml:jar:0.0.1-SNAPSHOT` 在本地仓库 `D:\mvnrepo` 缺失而 `DependencyResolutionException`。已用 `mvn install:install-file` 将既有 xml 桥接产物装入本地仓库：
  `mvn install:install-file -Dfile=D:\c29ddf4a\cs-xml\system-private-xml\target\system-private-xml-0.0.1-SNAPSHOT.jar -DgroupId=io.github.ningpp -DartifactId=system-private-xml -Dversion=0.0.1-SNAPSHOT -Dpackaging=jar -DgeneratePom=true`

✅ Fixed（流水线恢复为 MSBuild 多模块；后续迭代针对多模块产物的编译错误继续）

### 本轮循环状态（Step 1 → Step 2 结果）

- **Step 1（转换）**: `Conversion complete (MSBuild): 618 succeeded, 0 failed, 3 resources copied, modules=4`，退出码 **0**（耗时 276s～291s，300s 预算内）。
  - 产物模块：`system-private-uri`、`system-text-json`、`system-text-json-tests`（+ 根 parent pom）。
- **Step 2（`mvn clean install -e`，完整日志 `d:\cs-json-0911-sdk\mvn-build.log`）**:
  - **编译阶段：始终 0 个 javac 错误**（`system-private-uri`、`system-text-json` 138 文件、`system-text-json-tests` 447 文件）→ "零错误编译"目标保持达成。
  - **测试阶段**：最近一次 `Tests run: 7904, Failures: 483, Errors: 495, Skipped: 16` → **BUILD FAILURE**（生成 pom 的 `testFailureIgnore=false`）。
  - 失败数演进（每轮修复后全量重跑）：Round1 基线 `1042` → Round2（`toArrayAs` + 多维数组标记）`1006` → Round3（反射按名 + `ArraySegment<byte>`）`988` → Round4（`MemoryMarshal.Cast<byte,int>` + `MakeGenericMethod` 令牌）`976~978`。
  - 说明：`mvn clean install -e` 的完整执行（含 7904 个测试）约 5.5 分钟，超出单命令 300s 预算，故该项以更长超时在后台运行以取得完整日志；日志已保存在目标目录。

### 下一轮迭代的测试失败分类（供 Step 3 使用）

按异常类型（合计 1042 失败）：

| 异常 | 数量 | 典型信息 |
|------|------|----------|
| `java.lang.AssertionError` | 498 | 455 条无消息（`Assert.True` 断言失败） |
| `java.lang.ClassCastException` | 171 | `JsonElement cannot be cast to ...`（86+）、`int[] cannot be cast to Object[]`（35） |
| `java.lang.UnsupportedOperationException` | 140 | `The type 'class java.lang.Object' is not a supported Dictionary key type`（62）、`The type 'class [[I' is not supported`（49） |
| `dotnet.text.json.JsonException` | 84 | 反序列化失败 |
| `java.lang.NullPointerException` | 60 | `MethodInfo.makeGenericMethod`（16）、`CSharpDictionary.getChecked`（15） |
| 其他 | 89 | `ArrayIndexOutOfBoundsException`(23)、`RuntimeException`（`No matching constructor found` 21）等 |

失败最集中的测试类：`CollectionTests`(157)、`CustomConverterTests`(92)、`ReferenceHandlerTests`(85)、`ObjectTests`(80)、`PropertyVisibilityTests`(78)、`SpanTests`(65)、`Utf8JsonWriterTests`(61)、`ArrayTests`(54)、`ExtensionDataTests`(51)、`DictionaryTests`(45)。

已定位的首个高杠杆线索（下一轮从它开始）：
- `Dictionary<object, T>` 键反序列化：`dotnet/text/json/Serialization/Converters/ObjectConverter.java:48` `readWithQuotes()` 抛 `SR.getDictionaryKeyTypeNotSupported()`；C# 同路径见 `ObjectConverter.cs:23/38`。需确认是转换器产出的 `getTypeToConvert()`/字典键转换器查找（`JsonSerializerOptions.getDictionaryKeyConverter`）语义偏差，还是 ObjectConverter 的运行时转换器查找链在 Java 下走错分支。

另外，`d:\code\cs2j` 全量单元测试当前有 3 项**既有失败**（与本轮修改无关，已在 HEAD 9f2db495 复现）：`LocalFunctionLiftTests.StaticLocalFunction_LiftedAsPrivateStatic`、`LocalFunctionLiftTests.Skip_Iterator`、`StripDefaultProjectTests.CrossProject_CallSiteUnchanged_AndBuildSucceeds`（后者为 strip-default 跨工程调用点未重写：`SampleStripDefaultApp/Program.cs` 仍以旧实参调用已删默认值的方法，`error CS7036`）。

## Iteration 4 — `List<T>.ToArray()` 的 `(Object[])` 强转拒绝基元数组

- **Java 文件**: `system-text-json/src/main/java/dotnet/text/json/Serialization/Converters/ArrayConverter.java:54`
- **错误信息**: `java.lang.ClassCastException: class [I cannot be cast to class [Ljava.lang.Object;`（`ArrayTests.readObjectArray`、`readNull*` 等）
- **代码片段**:
  ```java
  // 修复前
  state.value.Current = state.value.Current.withReturnValue((Object[]) (Object) TypeHelper.listToArray(list, tElementClass));
  // 修复后
  state.value.Current = state.value.Current.withReturnValue(TypeHelper.toArrayAs(list, tElementClass));
  ```
- **对应 C# 文件**: `D:\csharp-sdk\json\src\System\Text\Json\Serialization\Converters\Collection\ArrayConverter.cs` — `state.Current.ReturnValue = list.ToArray();`
- **根因分类**: Transformer 逻辑缺陷（表达式生成）
- **涉及组件**:
  - `src/CSharpToJava.Core/Transformers/Expression/Transformers/InvocationExpressionTransformer.cs` — 两处 `List<T>.ToArray()` / `IEnumerable<T>.ToArray()` 生成分支（`TransformInstanceCollectionToArray` 与 LINQ stream 分支）
  - `java/csharptojava-compat/src/main/java/io/github/ningpp/compat/TypeHelper.java` — 新增 `toArrayAs`
- **分析**: C# `List<T>.ToArray()` 在 T 为基元类型时结果是 `int[]`/`byte[]` 等**基元数组**；`TypeHelper.listToArray` 正确地按运行时 `Class` 反射构造基元数组，但调用点被包了一层 `(javaType[]) (Object)`；当类型参数名被"泄漏防护"映射成 `Object`（如 `TElement`）时该强转即 `(Object[])`，Java 对 `int[]` 做 `checkcast [Ljava/lang/Object;` 必然抛 CCE。修复方式：新增按调用点定型（target-typed）的 `TypeHelper.toArrayAs(collection/stream, elementClass)`，在"擦除为 Object"的分支不再插入会失败的窄化强转；保留 `(T[]) (Object) TypeHelper.listToArray(...)` 分支（条件表达式场景仍需要，见 `ListToArrayConditionalExpressionTests` 与 `GenericArrayTypeParameterTests` 共 32 项断言）。
- **红→绿验证**: 定向测试 `dotnet test --filter "FullyQualifiedName~GenericArrayTypeParameter|FullyQualifiedName~ListToArrayConditionalExpression"` → 32/32 通过。
- **端到端验证**: `system-text-json-tests` 全量 → 失败数 `499 F + 543 E = 1042`（基线）降到 `500 F + 513 E = 1013`；`class [I cannot be cast` 日志行 72 → 1。

## Iteration 5 — 多维数组静态标记污染，交错数组 `int[][]` 被判为"不支持"

- **Java 文件**: `system-text-json/src/main/java/dotnet/text/json/Serialization/Converters/IEnumerableConverterFactory.java:64`
- **错误信息**: `java.lang.UnsupportedOperationException: The type 'class [[I' is not supported.`（`ObjectTests.write(ITestClass)[3]`、`PropertyNameTests.ignoreCase`、`CacheTests.propertyCacheWithMinInputs*` 等 **26 个**测试）
- **对应 C# 文件**: `.../Serialization/Converters/Collection/IEnumerableConverterFactory.cs`（`if (typeToConvert.GetArrayRank() > 1) ThrowHelper...`）
- **根因分类**: compat 库静态状态污染（非单文件语义问题）
- **涉及组件**: `java/csharptojava-compat/src/main/java/io/github/ningpp/compat/TypeHelper.java` — `MULTI_DIM_ARRAY_RANKS` / `getArrayRank` / `getMultiDimArrayClass`
- **分析**: C# `int[,]`（多维）与 `int[][]`（交错）在 Java 里是**同一个类** `int[][].class`。生成的测试用 `TypeHelper.getMultiDimArrayClass(int.class, 2)` 表达 `typeof(int[,])`，该方法把 `int[][].class → rank 2` **永久**写入静态 WeakHashMap；于是此后任何*交错* `int[][]`（如 `SimpleTestClass.MyInt16TwoDimensionArray`）在转换器工厂里都被判成 rank 2 并抛"不支持"。证据：单测隔离运行时这些测试通过（`mvn -o test -Dtest=PropertyNameTests#jsonOutputNotAffectedByCasingPolicy+ignoreCase+camelCaseDeserializeMatch` → 仅 `ignoreCase` 断言失败，无 `[[I` 错误），只有全量运行（同一 JVM 内先执行过 `getMultiDimArrayClass`）才报错。
- **修复**: `getArrayRank` 改为一次性消费标记（`MULTI_DIM_ARRAY_RANKS.remove(type)`），保留"显式声明多维数组不受支持"的意图语义，同时不再污染后续交错数组转换。
- **验证**: `class [[I' is not supported` 日志行 123 → 7；失败总数 1013 → **1006**。

## Iteration 6 — 反射按名查找使用 C# 名（PascalCase），找不到转换后的 Java 方法

- **Java 文件**: 生成测试中的反射调用点，例如 `system-text-json-tests/src/test/java/dotnet/text/json/Serialization/Tests/ExtensionDataTests.java:286`
- **错误信息**: `java.lang.NullPointerException: Cannot invoke "io.github.ningpp.compat.MethodInfo.makeGenericMethod(java.lang.Class[])" because the return value of "io.github.ningpp.compat.TypeHelper.getMethod(java.lang.Class, String, int)" is null`（32 处调用点 / 16 个测试）
- **代码片段**:
  ```java
  TypeHelper.getMethod(ExtensionDataTests.class,
      "ExtensionProperty_SupportsWritingToCustomSerializerWithOptionsInternal",
      BindingFlags.Static | BindingFlags.NonPublic)
      .makeGenericMethod(overflowType, converterType).invoke(null, (Object[]) null);
  ```
- **对应 C# 文件**: `D:\csharp-sdk\json\tests\Serialization\ExtensionDataTests.cs` — `.GetMethod(nameof(ExtensionProperty_SupportsWritingToCustomSerializerWithOptionsInternal), BindingFlags.Static | BindingFlags.NonPublic)`
- **根因分类**: 语义模型信息丢失（符号名字符串未随成员改名映射）
- **涉及组件**: `java/csharptojava-compat/src/main/java/io/github/ningpp/compat/TypeHelper.java` — `getMethod(Class, String, int)` / `getMethod(Class, String, Class...)`
- **分析**: C# 成员名是 PascalCase，转换后的 Java 成员名是 camelCase（`ExtensionProperty_...` → `extensionProperty_...`）；`nameof(...)` 生成的字符串字面量仍是 C# 名，而 `TypeHelper.getMethod` 只做精确匹配 → 返回 null → 调用方 NPE。
- **修复**: 精确匹配失败后，按 C#→Java 成员名规则（首字母小写）再查一次；两个 `getMethod` 重载都补上。保留精确匹配优先，不改变既有命中行为。
- **验证**: `mvn -o test -Dtest=ExtensionDataTests` 中 `TypeHelper.getMethod ... is null` 由 32 行降到 **0 行**；全量 `Tests run: 7904` 中该类 NPE 不再出现（失败总数仍在 ~1006 量级，因为这些测试随后被更深的类型擦除问题挡住）。

## Iteration 7 — `ArraySegment<byte>.AsMemory()` 用 `(T[])` 强转 `byte[]`

- **Java 文件**: `java/csharptojava-compat/src/main/java/io/github/ningpp/compat/ArraySegment.java:80`
- **错误信息**: `Caused by: java.lang.ClassCastException: class [B cannot be cast to class [Ljava.lang.Object;`（`JsonDocumentTests.parseJson_UnseekableStream_BadBOM` 等，20 行堆栈）
- **调用链**: `dotnet.text.json.JsonDocument.parse` → `MemoryExtensions.asMemory` → `ArraySegment.asMemory`
- **对应 C# 文件**: `D:\csharp-sdk\json\src\System\Text\Json\Document\JsonDocument.cs`（`ReadOnlyMemory<byte>`/`ArraySegment<byte>.AsMemory()` 路径）
- **根因分类**: compat 库类型擦除误用
- **涉及组件**: `java/csharptojava-compat/src/main/java/io/github/ningpp/compat/ArraySegment.java`
- **分析**: `asMemory()` 的 byte 分支执行 `T[] copy = (T[]) (Object) Arrays.copyOfRange(byteArray, ...)`；`T` 擦除为 `Object` 时该强转即 `(Object[])`，对 `byte[]` 必然抛 CCE。`ReadOnlyMemory` 已有 `(byte[], start, length)` 构造器（保持 byte[] 视图，与 C# `ArraySegment<byte>.AsMemory()` 语义一致），改用该构造器即可。
- **验证**: `mvn -o test -Dtest=JsonDocumentTests` 中 `[B cannot be cast` 由 20 行降到 **0**；全量失败数 1006 → **988**。

## Iteration 8 — `MemoryMarshal.Cast<byte,int>` 被实现为空操作，`CopySegment` 写坏行数据

- **Java 文件**: `system-text-json/src/main/java/dotnet/text/json/JsonDocument.java:1913`（`MetadataDb.copySegment`）
- **错误信息**: `java.lang.IndexOutOfBoundsException: offset=780624, length=-780264, arrayLength=360`（`JsonDocumentTests.readNestedObject` 等 20 行堆栈，且**单测隔离运行同样复现**，非测试顺序污染）
- **调用链**: `JsonElement.getProperty` → `JsonElement.tryGetProperty` → `JsonDocument.tryGetNamedPropertyValue` → `MetadataDb.get` → `ReadOnlySpan.<init>`
- **对应 C# 文件**: `D:\csharp-sdk\json\src\System\Text\Json\Document\JsonDocument.MetadataDb.cs:344` — `Span<int> newDbInts = MemoryMarshal.Cast<byte, int>(newDatabase.AsSpan());`
- **根因分类**: compat 库语义缺失（类型重解释被当成空操作）
- **涉及组件**: `java/csharptojava-compat/src/main/java/io/github/ningpp/compat/MemoryMarshal.java` — `cast`；`.../Span.java` — `get`/`set`
- **分析**: 该端口用 `Span<Integer>` 同时表示 byte 序列（逐字节寻址）与 int 序列。`MemoryMarshal.cast` 原实现直接 `return span;`（注释写"element reinterpretation is a no-op"），于是 C# 里按 **int** 下标的 `newDbInts[i] -= locationOffset` 变成**按字节**写入单字节：`-9` 被截断成 `0xF7` 写进行数据的第 0/3/6/9 字节，导致克隆文档 `copySegment` 第一行变成 `rows=63235` 之类的垃圾行；随后 `tryGetNamedPropertyValue` 用它算出天文数字的下标 → `IndexOutOfBoundsException`。
  证据（探针 `probe/MdProbe.java`，直接 dump `MetadataDb._data` 与 `MemoryMarshal.read` 结果）：
  原始库 7 行全部正确；修复前克隆库第 0 行为 `location=-150994944 sizeOrLength=16187395 rowsAndType=0x1000F703`，且第 1~3 行 location 未被减去偏移；修复后变为 `location=0/2/8/12`（正确）。
- **修复**:
  1. `Span` 增加"4 字节小端 int 视图"模式（`castToInt32`），`get`/`set` 在该模式下按 4 字节读写、`length` 按元素数计；视图与原始 `byte[]` 共享后备数组，写入可见。
  2. `MemoryMarshal.cast(Span<Integer>)` 委托给该视图；非 byte 后备的 span 原样返回。
  3. 说明：转换器把 `MemoryMarshal.Cast<byte,int>` 与 `Span<Integer>` 都擦除成同一签名，故只能按"byte→int"实现；当前全仓库仅此一处 Cast 调用点（`CopySegment`），已在注释中记录该限制。
- **验证**: `length=-` 的 `IndexOutOfBoundsException` 由 20 行降到 **0**；探针输出 `first.true = true`，类内断言可继续推进；全量失败数 988 → **977**。

## Iteration 9 — 反射 `MakeGenericMethod(...).Invoke(null, null)` 未提供转换器追加的 `Class` 令牌

- **Java 文件**: `system-text-json-tests/src/test/java/dotnet/text/json/Serialization/Tests/ExtensionDataTests.java:286`
- **错误信息**: `java.lang.NullPointerException: Cannot read the array length because "args" is null`（`io.github.ningpp.compat.MethodInfo.invoke(MethodInfo.java:97)`，16 行堆栈）
- **代码片段**:
  ```java
  TypeHelper.getMethod(ExtensionDataTests.class, "ExtensionProperty_...Internal",
      BindingFlags.Static | BindingFlags.NonPublic)
      .makeGenericMethod(overflowType, converterType).invoke(null, (Object[]) null);
  ```
  对应的生成方法签名（注意追加的 Class 令牌）：
  ```java
  private static <TDictionary, TConverter extends JsonConverter> void
      extensionProperty_SupportsWritingToCustomSerializerWithOptionsInternal(
          Class<?> tConverterClass, Class<?> tDictionaryClass) { ... }
  ```
- **对应 C# 文件**: `D:\csharp-sdk\json\tests\Serialization\ExtensionDataTests.cs` — `GetMethod(nameof(...), ...).MakeGenericMethod(overflowType, converterType).Invoke(null, null)`（C# 方法无参数）
- **根因分类**: 语义模型信息丢失（泛型实参未随反射调用传递）+ compat 错误处理 NPE
- **涉及组件**: `java/csharptojava-compat/src/main/java/io/github/ningpp/compat/MethodInfo.java`
- **分析**: 转换器把泛型方法的运行时类型令牌作为**追加参数**（命名形如 `t<T>Class` / `_cs2j_<T>`）注入到 C# 原签名之后；C# 侧通过 `MakeGenericMethod(t1, t2).Invoke(null, null)` 调用时既不传这些令牌，`makeGenericMethod` 旧实现又是空操作（直接 `return this`），于是参数数量不匹配 → `IllegalArgumentException` → 错误信息拼接时又对 `args`（显式传入的 `null`）取 `length` → 真正的失败被 NPE 掩盖。
- **修复**:
  1. `makeGenericMethod` 记录类型实参并返回带记录的 `MethodInfo`；
  2. `invoke` 在实参少于形参时，按**形参名到类型形参名**的映射（`t<T>Class` → `T<T>`、`_cs2j_<T>` → `<T>`）补齐转换器追加的 `Class` 令牌——按名字匹配而不是按位置猜测，避免顺序假设出错；名字无法匹配时不做补齐，宁可响亮失败；
  3. 错误信息拼接对 `args == null` 做保护。
- **验证**: `mvn -o test -Dtest=ExtensionDataTests` 中 `because "args" is null` 由 16 行降到 **0**；该类的失败原因由 NPE 变为 `TargetInvocationException` 包裹的**真实内部错误**（字典键类型擦除），说明反射调用已能正确进入被调方法；全量 977 → 976（这类测试随后被类型擦除问题挡住）。

## Iteration 10 — A+B 落地：发布成员泛型实参 + 转换器缓存按闭合泛型区分

- **涉及组件**:
  - `java/csharptojava-compat/src/main/java/io/github/ningpp/compat/TypeHelper.java` — 新增 `registerMemberType`（发布声明泛型实参）、`converterCacheKey`（闭合泛型键）
  - `.../PropertyInfo.java`、`.../FieldInfo.java` — 读取成员类型时发布实参
  - `.../ConcurrentHashMapHelper.java` — `tryAdd` 走 `converterCacheKey`
- **背景**: 见上一节"深层根因"——单独发布实参会被"按原始 Class 缓存"抵消（实测 1006→1081 后回退）。本轮把两半一起上线，且**无需重新转换**：生成代码只调用 `PropertyInfo.getPropertyType()` 与 `ConcurrentHashMapHelper.tryAdd(_converters, ...)`，两者行为都在 compat 内。
- **实现要点**:
  1. `registerMemberType(type)`：按 raw 类记录"外层优先"的层链（替换语义，重复读取同一成员幂等），并快照该成员实参供缓存键使用；
  2. `converterCacheKey(key)`：非 Class 键或**无类型参数**的类原样返回（如 `String`、`JsonElement`，缓存行为与之前完全一致）；泛型类则返回闭合泛型键（raw + 实参），**没有已发布实参时也使用空实参键**——因为生成代码的读取是 `_converters.containsKey(原始Class)`，这样写入的条目永远不会被读到，等于"泛型类型不复用缓存、按当前已发布实参重新创建转换器"，从而不会把某个成员的转换器发给另一个实参不同的成员；
  3. `tryAdd` 只改键的映射，`EnumConverter._nameCache`（String/enum 键）与 `_classes`（走 `getOrAdd`）行为不变。
- **测量**（`system-text-json-tests`，7904 项）:
  | 模式 | Round4 基线 | 仅 A+B | A+B+泛型不复用 |
  |---|---|---|---|
  | `dictionary key type` | 262 | 44 | **35** |
  | `JsonElement cannot be cast` | 211 | 85 | 115 |
  | `could not be converted to ...CSharpList` | 18 | 72 | **36** |
  | `Integer cannot be cast to String` | 0 | 58 | **30** |
  | `ImmutableDictionary cannot be cast` | 53 | 1 | **0** |
  | `No matching constructor found` | 43 | 29 | 41 |
  | 失败测试总数 | 976 | 972 | **964** |
- **结论**: 类型擦除症状大幅下降（字典键 -87%、跨类型转换错误 -98%），但会以"另一些成员拿不到实参"的形式转移，因此总收益有限（-12）。当前采用 A+B+泛型不复用 的组合（964，最优），并保留该方向的后续优化空间（真正的收敛仍取决于让 `_classes`/`_converters` 也按闭合泛型建键，属生成形态改动）。

## Iteration 11 — compat 自测中 1 项既有红灯：`getElementType` 期望值与端口语义相反

- **涉及组件**: `java/csharptojava-compat/src/test/java/io/github/ningpp/compat/TypeHelperTest.java`
- **现象**: `mvn -o test`（compat 模块，1608 项）中 `getElementType_returnsWrapperClassForPrimitiveArrays` 失败：`expected: <java.lang.Integer> but was: <int>`。
- **分析**: 该断言与实现相反，且实现是**端口需要**的语义：`IEnumerableConverterFactory` 用 `getElementType(typeToConvert)` 作为数组的元素类，`ArrayConverter.convertCollection` 再把它交给 `TypeHelper.listToArray(list, elementClass)`；只有元素类仍是**基元类**时才会得到 `int[]`，C# `int[]` 成员的 setter 才接受（这与 Iteration 4 修的 `[I cannot be cast` 是同一语义链）。历史提交 `21b56ac0` 已把实现固定为"保留基元类"，但未同步该测试。已用 `git diff HEAD` 确认该实现与本轮改动无关（既有失败）。
- **修复**: 更新测试为 `int.class/long.class/boolean.class`（重命名为 `getElementType_returnsPrimitiveClassForPrimitiveArrays`），并加注释说明基元/包装的边界（需要包装形式时走 `toWrapperType`/`newArrayInstance`）。
- **验证**: compat 模块 `Tests run: 1608, Failures: 0, Errors: 0` → **BUILD SUCCESS**。

## Iteration 12 — 局部函数提升把上下文关键字当成保留字（`Add` → `addLocal`）

- **涉及组件**: `src/CSharpToJava.Core/LocalFunctionLift/LocalFunctionLiftNaming.cs`
- **现象**: `LocalFunctionLiftTests.StaticLocalFunction_LiftedAsPrivateStatic` 失败：期望生成 `private static int add(int a, int b)`，实际是 `addLoc...`。
- **分析**: `GetBaseName` 的关键字表把 C# **上下文关键字**（`add`、`remove`、`get`、`set`、`value`、`var`、`async`、`await`、`from`、`select`、`where`、`yield` 等）也当成保留字，于是名为 `Add` 的局部函数被改写成 `addLocal` ✗。这些名字在 C#/Java 里都是合法标识符；同时该表还漏掉了真正的 Java 保留字（`assert`、`final`、`synchronized`、`super`、`package`…）。
- **修复**: 关键字判定改为「C# 保留字 ∪ Java 保留字与受限标识符」，剔除全部上下文关键字。这样既不会误改名（`Add` → `add`），也不会生成 Java 非法方法名（`Assert` → `assertLocal`）。
- **验证**: `StaticLocalFunction_LiftedAsPrivateStatic` 通过；同类测试 14/14 通过。

## Iteration 13 — 两项既有红灯测试与实现契约不一致（既有设计决策未同步到测试）

- **涉及组件**: `tests/CSharpToJava.Tests/LocalFunctionLiftTests.cs`、`tests/CSharpToJava.ProjectTests/StripDefaultProjectTests.cs`
- **分析**:
  1. `Skip_Iterator` 期望迭代器局部函数被**跳过**，但 `LocalFunctionLiftAnalyzer` 的 Gate #1 已被**有意禁用**（注释说明：迭代器局部函数可提升为类级迭代器方法，提升后的方法仍是合法迭代器），且实现工作正常 ✗ 测试陈旧。
  2. `StripDefaultProjectTests.CrossProject_CallSiteUnchanged_AndBuildSucceeds` 断言（设计文档中的 D1）"跨工程调用点必须逐字节不变"、并且要求生成无参构造 `Service()`；而实现（与设计文档同源的提交 `3a9ebdc`）**有意**在默认值可在调用点解析时把默认值以命名实参补进调用点（默认值只在声明类型内可解析时改由生成的转发重载兜底），并且**有意不合成无参构造**（避免反射驱动的序列化器/激活器看到 C# 类型本不存在的默认构造）。这两处都属于"实现契约已演进、测试未同步"，而端到端行为（剥离默认值 + 生成重载 + `dotnet build` 成功）本身是正确的。
- **修复**: 按实现契约重写这两处断言：
  - 迭代器测试改为 `Iterator_LiftedToClassLevelIteratorMethod`：断言已提升、未被跳过、无 `Iterator` 跳过诊断、类级方法保留 `yield return`、且不再残留局部函数；
  - strip-default 测试改为 `CrossProject_CallSitesFilledAndBuildSucceeds`：断言调用点被补全为命名实参（`new Service(name: "unnamed")`、`s.Process(1, b: "default", c: true)`、`s.Calculate(5, y: 10, z: 20)`、`Service.Greet(name: "world")`）、已完整的调用点保持不变，并断言**不**合成 `public Service()`。
- **验证**: `dotnet test -c Release` → **`CSharpToJava.Tests` 3171 项 + `CSharpToJava.ProjectTests` 58 项，失败 0、错误 0**；加上 compat 模块 1608 项全绿，`d:\code\cs2j` 的单元测试首次全绿（成功标准之一达成）。

## 转换确定性验证（Round 6 附加结论）

连续两次相同命令转换到不同目录后逐文件 SHA256 比对：
`d:\cs-json-0911-sdk` 与 `d:\cs-json-determinism` 各 618 个 `.java`，**differing=0 / onlyA=0 / onlyB=0** → 转换器输出**逐字节可复现**，因此"同一 compat 下跨转换的测量差异"可归因于转换器改动本身，而不是随机性。

同轮还发现：重新转换（含 Iteration 12 的命名修复）后全量测试为 `434 F + 545 E = 979`，而命名修复前的同一 compat 状态为 `964`（净 +15）。逐测试比对显示 **37 项新失败 / 22 项新通过**：新失败集中在 `CollectionTests` 的嵌套集合写入（`writeListOfList`、`writeIListOfIList`、`writeImmutableStackTOfImmutableStackT`…），新通过集中在 `ArrayTests` 的泛型集合读写。这类测试共享静态状态（同一 JVM 内的转换器缓存/注册表），失败集合本身会改变后续测试的执行路径，因此该 ±15 的差异包含级联成分，不能简单归因于命名本身；命名修复保留了（它修正了一个真实的 C# 语义问题：上下文关键字被误当保留字），后续若要进一步区分，需要在单测隔离下逐项复现。

## Iteration 14 — 定位两个真实缺陷（`sbyte[]`/`byte[]` 冲突、`IEnumerableConverter.RuntimeType` 无条件返回 CSharpList），但修法未落地

### 14a. C# `sbyte[]` 与 `byte[]` 在 Java 侧都是 `byte[]`，但 JSON 形态不同

- **现象**: `dotnet.text.json.JsonException: The JSON value could not be converted to class [B. Path: $.MyData[0].MySByteArray`（88 行），内层 `IllegalStateException: Cannot get the value of a token type 'StartArray' as a string`（`ByteArrayConverter.read` → `Utf8JsonReader.getBytesFromBase64`）。
- **对应 C# 文件**: `json\tests\Serialization\TestClasses\TestClasses.SimpleTestClass.cs:51` `public sbyte[] MySByteArray { get; set; }`，测试 JSON 为 `"MySByteArray" : [8]`（**数字数组**）；`MyByteArray` 则是 Base64 字符串。
- **根因**: 映射表 `config/TypeMappings.json` 里 `System.SByte → byte`、`System.Byte → int`，而**无符号预处理**把 `byte[]` 处理成 Java `byte[]`（Base64 语义）；于是 `sbyte[]` 与 `byte[]` 在 Java 里都是 `byte[]`，端口按 `s_defaultSimpleConverters[byte[].class]` 一律选 `ByteArrayConverter`（Base64）→ `sbyte[]` 成员读到 `[` 就抛错。
- **为何现在才暴露**: A+B 之后这些类不再卡在更早的属性上（`to [B` 计数：Round3 6 行 → Round4 10 行 → **A+B 后 96 行**），属"前一个阻塞被修好后暴露的下一个缺陷"，不是 A+B 引入的回归。
- **可行修法（未落地，需要更大改动）**: ①让 `sbyte[]` 在 Java 侧有独立表示（例如 `CSharpSByte[]`），代价是 `sbyte` 标量也要一致化并补齐运算符重写；②让 `byte[]` 的读取同时接受 Base64 字符串与数字数组——但 `ByteArrayConverter.read`/`Utf8JsonReader.getBytesFromBase64` 都是**生成代码**，需要转换器侧针对该调用点做 lowering。

### 14b. `IEnumerableConverter.getRuntimeType()` 无条件返回 `CSharpList`

- **现象**: `UnsupportedOperationException: The collection type 'class io.github.ningpp.compat.CSharpArrayList' is abstract, an interface, or is read only, and could not be instantiated and populated.`（`CollectionTests.readArrayOfArrayList` 等）。
- **分析**: 生成代码里各集合转换器对 `RuntimeType` 的处理不一致——`IListConverter`/`IListOfTConverter`/`ICollectionOfTConverter`/`IDictionaryConverter`/`IDictionaryOfTKeyTValueConverter` 都是"**仅当**声明类型是抽象/接口时才返回 CSharpList/CSharpDictionary"，而 `IEnumerableConverter`（`IEnumerableConverter.java:81`）与 `IEnumerableOfTConverter`（`:84`）**无条件**返回 `CSharpList.class`。C# 侧 `IEnumerableConverter<TCollection>.CreateCollection` 的检查是 `TypeToConvert.IsAssignableFrom(RuntimeType)`，对具体类型（`ArrayList`）必须成立；端口里 `getRuntimeType()` 恒为 CSharpList → 检查失败 → 抛错；同时 `JsonClassInfo.getConverter` 的运行时类型选择也会把 `ArrayList` 成员判成 CSharpList。
- **尝试过的修法（已回退）**: 在 compat 的 `TypeHelper.isAssignableFrom` 里加桥接（`CSharpIterable` 的具体实现 ← `CSharpList`）。效果：`is abstract, an interface` 错误 **降为 0**，**定向**运行 `CollectionTests` 由 183 → 151 失败；但**全量**运行反而变差：`979 → 1011`（`CollectionTests` 在全量上下文里 183 → 211）。原因是这些测试共享 JVM 内的转换器缓存/类型注册表，改变转换器选择会级联影响后续测试。遂回退，保持 Round 6 的较优状态（全量 979~980）。
- **结论/下一步**: 正确修法应让 `IEnumerableConverter`/`IEnumerableOfTConverter` 的 `RuntimeType` 与同类保持一致（抽象/接口才返回 CSharpList）——即需要转换器在生成这两个类时采用与其它集合转换器相同的规则；由于该 `getRuntimeType()` 覆写来自参考程序集元数据而非仓库源码，需要先定位转换器解析 `RuntimeType` 的具体路径。

### 本轮方法学结论（重要）

本代码库的失败数**强依赖同一 JVM 内的共享静态状态**（转换器缓存、`TypeHelper` 注册表、`MultiDim` 标记）：同一改动在"定向单测"与"全量运行"下可以给出相反结论（14b 就是实例）。因此后续所有修复都必须以**全量 `mvn -o test -pl system-text-json-tests`（7904 项）**为准，定向运行只用于快速定位。

## Iteration 15 — `Activator.CreateInstance` 转发实参数少于注入的 Class 令牌 → "No matching constructor found"

- **Java 文件**: `system-text-json/src/main/java/dotnet/text/json/Serialization/Converters/ObjectConverterFactory.java:76`
- **错误信息**: `java.lang.RuntimeException: No matching constructor found for dotnet.text.json.Serialization.Converters.SmallObjectWithParameterizedConstructorConverter`（`Caused by: NoSuchMethodException`；`CustomConverterTests.complexReferenceTypeConverter_NoOverride` 等 21~41 行）
- **对应 C# 文件**: `.../Serialization/Converters/Collection/ObjectConverterFactory.cs:43-76`（`Type[] typeArguments = new Type[UnboxedParameterCountThreshold + 1]` 逐项填充后 `typeof(SmallObjectWithParameterizedConstructorConverter<,,,,>).MakeGenericType(typeArguments)`，再 `Activator.CreateInstance(converterType, ..., args: null, ...)`）
- **根因分类**: compat 库（反射构造）语义不足
- **涉及组件**: `java/csharptojava-compat/src/main/java/io/github/ningpp/compat/Activator.java` — `createInstanceForGenericType`
- **分析**: 生成代码里 `typeArguments` 数组是在 `if/else` 分支内声明的（作用域不覆盖调用点），转换器只能把**标量组** `new Class<?>[]{ typeToConvert }` 转发给 compat；而 `SmallObjectWithParameterizedConstructorConverter` 的构造器需要 **5 个** `Class` 参数（转换器为每个 C# 泛型参数注入一个令牌），compat 的匹配条件 `params.length <= genericTypeArguments.length` 因此全部落空 → 抛 "No matching constructor found"。
  我先尝试改转换器让数组形式优先（并写了单测），但生成代码无法编译（`typeArguments` 不在作用域内，`找不到符号`）→ 回退转换器改动，改在 compat 侧处理。
- **修复**: `createInstanceForGenericType` 在**没有任何构造器参数数量被满足**、且该类**只有一个全 Class 参数的构造器**时，用 `Object.class` 补齐多出的令牌（这些令牌是占位符：`SmallObjectWithParameterizedConstructorConverter` 只把 `tClass` 用于 `getTypeToConvert()`，`tArg0..tArg3Class` 字段在生成代码中从未被读取）。多构造器的类型保持原有 "no matching constructor" 行为，避免歧义。
- **验证（全量为准）**: `No matching constructor found` 21~41 行 → **0**；新增/保留相关单测（compat 2 项 + 转换器 1 项）全绿；失败测试集合：**10 项新通过**（含目标用例 `CustomConverterTests.complexReferenceTypeConverter_NoOverride`、`CollectionTests.writeImmutableStackT*`、`NumberHandlingTests.number_AsObjectWithParameterizedCtor_RoundTrip`）、**9 项新失败**（`writeImmutableListT*`、`EnumConverterTests.serializeEnumClass*` 等共享静态状态的级联）；全量 979~980 → **978**。

## 已定位但**尚未修复**的深层根因（下一轮起点）

**泛型实参在成员读取处被擦除 + 转换器缓存按原始类共享**（两者叠加才是真正的瓶颈）：

1. `PropertyInfo.getPropertyType()` / `FieldInfo.getFieldType()` 返回擦除后的 `Class`（`CSharpDictionary.class`），转换器工厂只能拿 `TypeHelper.getGenericArguments(raw)`，在无会话注册时退化为类型参数上界 → 键/元素类型变成 `Object`。实测探针（`D:\code\cs2j\probe\TypeArgProbe.java`）：
   `MyStringToStringGenericDict` declared=`CSharpDictionary<String,String>` recovered=`[Object, Object]`。
2. 尝试在 `PropertyInfo`/`FieldInfo` 里发布声明泛型实参（`TypeHelper.registerMemberType`，按 raw 类保留"外层优先"链并在查询时消费）后：`dictionary key type` 日志行 262→26、`JsonElement cannot be cast` 211→72 **明显改善**，但 `Integer cannot be cast to String`（写入路径）与 `could not be converted to ...CSharpList` 反而上升，失败总数 `1006 → 1081`，因此**已回退**（该实现见本轮迭代记录，可复现）。
3. 原因是生成的 `JsonSerializerOptions._converters` 是 `ConcurrentHashMap<Class, JsonConverter>`——**只按原始 Class 缓存**，`CSharpList<String>` 与 `CSharpList<CSharpInteger>` 共用同一个转换器实例。把实参发布正确后，缓存里第一个转换器变成"具体但可能属于别的成员"的类型；此前所有转换器都是 `Object` 型（宽松但错误表现为 JsonElement）。要真正修好，需要让转换器缓存按**闭合泛型**（raw + 实参）区分，例如在生成代码的 `getConverter` 缓存键上引入 compat 的复合键，或在转换器侧改写 `GetConverter` 缓存访问。

其他仍然存在的主要失败簇（最新一轮全量：`Tests run: 7904, Failures: 481, Errors: 495`，即 976 项）：
`java.lang.AssertionError`（439，绝大多数是"元素类型变成 JsonElement 导致断言值不等"）、`JsonElement cannot be cast to ...`（105 行）、`not a supported Dictionary key type`（85 行）、`could not be converted to ...CSharpList`（38 行）、`ImmutableDictionary cannot be cast to CSharpReadOnlyDict`（26 行）、`No matching constructor found`（21 行）、`CSharpDictionary.getChecked` / `CSharpGenericIDictionary` NPE（33 行）。

### 下一轮的具体计划：让转换器缓存按"闭合泛型"区分（这是解锁 ~600 项失败的关键）

已实测的两半拼图：
- **A（已实现并被测量）**：`PropertyInfo.getPropertyType()` / `FieldInfo.getFieldType()` 发布声明泛型实参（`TypeHelper.registerMemberType`）→ 字典键/元素类型不再退化成 `Object`，`dictionary key type` 262→26 行、`JsonElement cannot be cast` 211→72 行。
- **B（尚未实现）**：生成的 `JsonSerializerOptions._converters` 只按原始 `Class` 建键，`CSharpList<String>` 与 `CSharpList<Integer>` 共用同一实例；A 单独上线会把"共享但宽松（Object）"变成"共享但具体（偶发类型不符）"，总数反而 1006→1081，因此 A 已回退。

计划（A+B 同时上线）：
1. compat 新增 `TypeHelper.cacheKey(Class<?> raw)`：该 raw 类当前有已发布的泛型实参时返回复合键（raw+实参，具备 `equals`/`hashCode`），否则原样返回 `raw`（对非泛型类型保持现有行为，零开销）。
2. 转换器侧把 `JsonSerializerOptions._converters` 的**读写键**都换成该复合键（字段类型放宽为 `ConcurrentHashMap<Object, JsonConverter>`）；这是唯一需要改生成形态的地方，且局限在 `JsonSerializerOptions` 这一个类型内。
3. 重新转换 + 全量测试，对比 `Tests run: 7904` 的失败数是否显著下降（预期 A 的收益不再被缓存共享抵消）。


**另一个已定位但未修复的簇（`ImmutableDictionary cannot be cast to CSharpReadOnlyDict`，27 行）**：
`ImmutableDictionaryOfTKeyTValueConverter.onWriteResume` 的调用点会对值做 `checkcast CSharpReadOnlyDict`（`TCollection` 的擦除上界，来自 C# `where TCollection : IReadOnlyDictionary<TKey,TValue>`），而 compat 的 `ImmutableDictionary<K,V>` 只实现 `IImmutableDictionary`。
尝试让 `ImmutableDictionary`/`ImmutableSortedDictionary` 实现 `CSharpReadOnlyDict<K,V>` 会编译失败：compat 的 `CSharpGenericIterable` 继承 `java.util.Collection`，于是要求 `void clear()` 与 `tryGetValue(K)` 等，而不可变字典的 `clear()` 返回新实例（C# 语义）→ 签名冲突。
真正的修法是让 `IReadOnlyDictionary<TKey,TValue>` 在 compat 里映射到一个**只读**接口（不含 `Collection` 的可变方法），但该接口被全部生成代码使用，属于跨项目改动，本轮未做（改动与失败原因已记录，避免重复尝试）。

## Iteration 16 — `DayOfWeek` 补齐 `_UNMAPPED` 语义；并量化"全量数字的运行间噪声"（方法学修正）

- **Java 文件**: `java/csharptojava-compat/src/main/java/io/github/ningpp/compat/DayOfWeek.java`（compat-only，无生成产物改动、无需重新转换）
- **错误信息（红）**: `java.lang.ArrayIndexOutOfBoundsException: Index -1 out of bounds for length 7` at `DayOfWeek.fromValue(DayOfWeek.java:20)`，由 `Serialization.Tests.EnumConverterTests.convertDayOfWeek`（C# `EnumConverterTests.cs:42` `JsonSerializer.Serialize((DayOfWeek)(-1), options)`）触发。
- **根因分类**: compat 库语义不足 —— `DayOfWeek` 是 compat 中**唯一**没有采用"`_UNMAPPED` 哨兵"约定的枚举；HEAD 实现 `fromValue(v) => values()[v % 7]` 对负数直接数组越界，对 `v >= 7` 则静默别名到错误的星期。
- **修复**: 与 `EnumTransformer` 生成形态对齐（见 `system-private-uri/.../UriKind.java`）：新增 `_UNMAPPED(0)` 哨兵 + 可变 `unmappedValue`，`getValue()` 对哨兵返回原值，`fromValue()` 线性查名后回退到哨兵并保存原值。
  - 新增红灯单测 `DayOfWeekTest`（compat）：`fromValue(-1)` 不得抛异常且 `getValue()==-1`、哨兵不得出现在 `EnumHelper.getNames/getValuesArray`。
  - 该单测额外抓出一个**真实映射错误**：`fromJava` 直接透传 `dow.getValue()` 是错的——JDK `java.time.DayOfWeek` 用 ISO-8601 编号（Monday=1 … Sunday=7），C# 用 Sunday=0 … Saturday=6，必须 `% 7`（原实现恰好靠 `% 7` 掩盖了这一点）。红：`expected: <Sunday> but was: <_UNMAPPED>` → 绿：`mvn -o install` 1615/1615 通过。
- **验证（同命令、同上下文的定向 A/B）**:

| 测试类 | 基线（HEAD jar） | 修复后 |
|--------|------------------|--------|
| `Serialization.Tests.EnumConverterTests` | 3F + 3E = 6 | 4F + 2E = 6（**AIOOBE 崩溃消失**） |
| `Tests.EnumConverterTests` | 4F ~ 6F（两次运行不同） | **1F** |

  ⇒ 定向证据为净改进，**保留**该改动。
- **官方 Step 2（`mvn clean install -e`，`d:\cs-json-0911-sdk\mvn-build.log`）**: `Tests run: 7904, Failures: 462, Errors: 503, Skipped: 16` = **965**（Round 8 官方 965：457F + 508E）⇒ 全量数字持平。
- **方法学修正（取代 Iteration 14 的"全量为唯一裁判"规则）**: 本套件存在**运行间不确定性**，实测：
  - 两次**完全相同**的定向命令（`-Dtest=EnumConverterTests,CollectionTests`）：`366 项 → 70F+88E` vs `72F+88E`，差集恰为 `serializeNegativeEnum` / `serializeNegativeFlagsEnum` —— 而这两项在本轮 A/B 中被记为"新失败"，属**误判**。
  - 两次同配置全量运行的差集约 **35 项**，且集中在与改动无因果链的集合转换错误（`ListOfTConverter.onWriteResume:35` 把 `java.lang.Integer` / `CSharpDictionary` / `BasicJsonAddress` 当作 `CSharpList` 强转）——同一批用例在基线运行通过、在修复运行失败。
  - 结论：**单个全量失败数的噪声下限约 ±35~40**，不足以判定 <40 的单项收益。keep/revert 判定改用"同命令 + 同上下文 + 类级计数"的 A/B，全量数字仅作记录；当一个判定确实依赖全量数字时，至少跑两次取交集。
- **本轮定位、尚未修复（下一轮起点）**:
  1. **枚举未定义值的写出语义**：`EnumConverter.write` 用 `String original = value.toString()` 复刻 C# `Enum.ToString()`；但生成的枚举与 compat 手工枚举都**没有覆写 `toString()`**，未定义值因此写出 `"_UNMAPPED"` / `"_unmapped"`，而 C# 期望数值（`convertDayOfWeek` 期望 `-1`、`enumWithNoValues` 期望 `-1` / `1`）。修法 = `EnumTransformer` 为枚举补 `toString()` 覆写（`_UNMAPPED` → 数值字符串；其余返回 `name()`，与 Java 默认 `toString()` 完全等价 ⇒ 纯增量、无回归面）。**属"生成产物形态改动"，按用户要求先通知、待批准后再实施**（需重新转换 ~280s）。
  2. **compat 反射读私有枚举**（compat-only，下一轮首选）：`EnumConverterTests.enumWithConverterAttribute` → `TargetInvocationException` ← `ClassCastException: Cannot cast ...EnumConverterTests$MyCustomEnum to a numeric value` ← `EnumHelper.toInt64` 的 `getMethod("getValue").invoke()`。C# 侧该枚举声明为 `private enum MyCustomEnum`，Java 生成 `private static enum`（正确）；公开方法落在非公开类上，反射 `invoke` 抛 `IllegalAccessException`。修法：`canAccess`/`setAccessible(true)`，并在无 `getValue()` 时回退 `ordinal()`；红灯单测 = compat 内一个 private 嵌套枚举。
  3. `convertFileAttributes`：`The JSON value could not be converted to class java.lang.Integer`（枚举与其底层 `Int32` 的转换器选择，疑与 `_converters` 只按原始 `Class` 建键同源）。
  4. `convertEnumUsingAttributes`：需按 C# 成员命名属性/命名策略输出名字、未命名成员输出数值（`EnumHelper` 目前只看常量名）。
  5. `deserializeEnumClass`：`Nullable<enum>` 期望 `null` 实得 `0`（既有 nullable 值类型阻塞项）。

## Iteration 17 — compat 反射读"非 public 枚举类"抛 `IllegalAccessException`（枚举转换器暖机崩溃）

- **错误信息（红，SDK）**: `dotnet.text.json.Serialization.Tests.EnumConverterTests.enumWithConverterAttribute` → `io.github.ningpp.compat.TargetInvocationException` ← `java.lang.ClassCastException: Cannot cast dotnet.text.json.Serialization.Tests.EnumConverterTests$MyCustomEnum to a numeric value`（`EnumHelper.toInt64`），抛出点在 `EnumConverter` 构造器的名字缓存暖机循环（`EnumConverter.java:80`）。
- **根因分类**: compat 库反射语义不足
- **分析**: C# 侧 `private enum MyCustomEnum`（`EnumConverterTests.cs:186`）转换为 Java `private static enum`（形态正确）；但 Java 的成员访问检查会同时看**声明类**的可访问性——public 方法声明在 private 嵌套类上时，从**其它包**反射调用会抛 `IllegalAccessException`（compat 包与测试包不同），而 `EnumHelper.toInt64` 把它统一折叠成 `ClassCastException("... to a numeric value")`，于是整个枚举转换器无法构造。
  同类缺陷共 **5 处**（同一 GitHub 风格的 `getMethod("getValue").invoke(...)` 写法）：`EnumHelper.toInt64`、`EnumHelper.isDefined`、`EnumHelper.scanEnumWidth`、`EnumHelper.toObject`（`fromValue` 静态查找）、`Unsafe.as`、`MemoryMarshal`（枚举选项扫描）、`IConvertible.toInt64`。
  其中 `Unsafe.as` 是**写出**路径的第二个崩溃点：`EnumConverter.write` 生成 `(int) Unsafe.as(value)`，反射失败时 `Unsafe.as` 静默 `return (T) value`（返回枚举本身）→ `ClassCastException: ...MyCustomEnum cannot be cast to ...Integer`（`EnumConverter.java:214`）。
- **修复（compat-only）**: 新增 `EnumHelper.invokeOpened(Method, target, args...)`：先 `canAccess`，需要时 `setAccessible(true)`（捕获强封装场景的 `RuntimeException` 后继续，让真实异常浮出）；再新增 `EnumHelper.readEnumValue(Enum<?>)` 便捷封装。上述 5 处调用点全部改走该入口。
- **红→绿**: 新增 compat 单测 `EnumHelperNonPublicEnumTest` + 跨包夹具 `io.github.ningpp.compat.fixtures.PrivateEnumFixture`（private 嵌套枚举，模拟转换产物）。红：3 项报 `ClassCastException: Cannot cast ...PrivateEnumFixture$PrivateValueEnum to a numeric value`（与 SDK 报错逐字一致）；绿：修复后 6/6 通过，compat 全量 **1621/1621**。
- **SDK 效果（同命令定向）**: `enumWithConverterAttribute` **ERROR → FAILURE**（崩溃消除，剩下语义差异：期望 `"Second"` 实得 `2`）；`Serialization.Tests.EnumConverterTests` 4F+2E=6 → 5F+1E=6（错误数 −1）。
- **官方 Step 2**: `Tests run: 7904, Failures: 455, Errors: 500, Skipped: 16` = **955**（Round 9 为 965）。改善 10 项，仍在噪声带内，故以定向证据为准。
- **本轮新定位的三个根因（下一轮优先级）**:
  1. **`[JsonConverter]` 类型级属性未被运行时认读**（`enumWithConverterAttribute` 残留）：`JsonSerializerOptions.determineConverter` 的 Priority 3 走 `getAttributeThatCanHaveMultiple(typeToConvert, JsonConverterAttribute.class)` → `TypeHelper.getCustomAttributes`；`MyCustomEnum` 上确实有 `[JsonConverter(typeof(JsonStringEnumConverter))]`，但 Java 侧最终用了 `EnumConverterFactory`（`AllowNumbers` 无 `AllowStrings`）→ 写出 `2` 而非 `"Second"`。需确认元数据（MetadataDb）是否为 private 嵌套类型记录了该 attribute。
  2. **成员/属性顺序不确定 ⇒ 运行间噪声的直接来源**：`serializeNegativeEnum` 的失败是**纯顺序差异**（期望 `{"Value1":-1,"Value2":-2147483648}`，实得 `Value2` 在前；内容完全一致），而该用例正是前面两次相同命令下结论相反的那一个。链条：`JsonClassInfo`（`JsonClassInfo.java:114`）用 `PropertyInfo.getProperties(currentType, 54)`.iterator 顺序建 `cache`/`PropertyCacheArray`，而 compat 的 `PropertyInfo.getProperties` 直接返回 `clazz.getMethods()` —— JDK 明确不保证顺序，实测随 JVM 运行变化。C# 契约是**元数据声明顺序**，因此修法是让 compat 按声明顺序（`getDeclaredMethods()` 的类文件顺序，逐层上溯）产出并给出确定性 tie-break。**这条同时能压低全量噪声，使后续 A/B 更可信**。
  3. `convertFileAttributes`：`JsonException: The JSON value could not be converted to class java.lang.Integer`（`Path: $.Attributes`）——枚举与其底层 `Int32` 之间的转换器选择错误。
- **策略提示（供用户决策）**: 定向统计显示集合簇（`CollectionTests` 隔离运行 148 项失败：63 `AssertionError` + 42 `JsonElement cannot be cast to ...` + 32 `could not be converted to ...`）是**元素类型擦除 + 转换器选择**同一根因，属最大杠杆；而枚举簇合计约 10 项。是否投入"闭合泛型转换器缓存/元素类型"结构性改造（可能触及生成产物形态），已在汇报中提请用户确认。

## Iteration 18 — 反射成员顺序逐次运行变化（运行间噪声的真正根因）：改按类文件声明顺序

- **错误信息（红，SDK）**: `dotnet.text.json.Tests.EnumConverterTests.serializeNegativeEnum` 期望 `{"Value1":-1,"Value2":-2147483648}`，实得 `{"Value2":-2147483648,"Value1":-1}` —— **内容完全相同，只有成员顺序不同**；该用例在 Iteration 16/17 中两次相同命令下结论相反，正是全量噪声的主要来源之一。
- **根因分类**: compat 反射顺序不确定（JDK 未定义顺序，HotSpot 实际逐次运行变化）
- **决定性验证（新增探针 `probe/PropOrderProbe.java`）**: 同一个 class 文件、同一台机器、连续 3 次 JVM 运行：
  - run1/run2：`getMethods=[getValue1,getValue2,getClass]` → compat 顺序 `[Value1,Value2]`（用例通过）
  - run3：`getMethods=[getValue2,getValue1,getClass]` → compat 顺序 `[Value2,Value1]`（用例失败）
  即 `Class#getMethods()`（`getDeclaredMethods()`、`getDeclaredConstructors()` 同理）的顺序**逐次运行都会变**。`javap -p` 证明类文件成员表稳定且等于源码声明顺序（`getValue1,setValue1,getValue2,setValue2`）。
- **影响面（为何值得优先修）**: ①`JsonClassInfo`（`JsonClassInfo.java:114`）按 `PropertyInfo.getProperties(...)` 的顺序写入 `CSharpDictionary`（`LinkedHashMap`）→ 对象 JSON 成员顺序 = 反射顺序 → 顺序断言类用例随机失败；②`TypeHelper.getMethods/getConstructors` 的顺序决定"首个匹配者获胜"的转换器/构造器选择 → 集合簇的 ClassCast/转换失败也随之随机。修掉它同时**压低全量噪声**，使后续 A/B 更可信。
- **修复（compat-only，无生成产物改动）**: 新增 `io.github.ningpp.compat.DeclarationOrder`：读取自身 class 文件（常量池 + fields/methods 表，`ClassValue` 缓存；读不到时退回空表并靠确定性 tie-break），提供 `methodIndex(name/descriptor)`、`constructorIndex`、`fieldIndex`、`hierarchyDepth`（派生成員先于继承成员，接口成员最后）。以下 4 处改为按 (声明层级, 类文件索引, 声明类名, 名称, 描述符) 排序：`PropertyInfo.getProperties`、`TypeHelper.getFields`、`TypeHelper.getMethods`、`TypeHelper.getConstructors`。
- **红→绿**: 新增 `PropertyOrderTest`（8 个乱序属性 + 继承 + 字段 + 重载方法 + 多构造器）。红：3/4 失败，实得 hash 顺序（如 `expected [Delta, Alpha, Zulu, ...] but was [Zulu, Bravo, Yankee, Xray, ...]`）；绿：修复后 6/6，compat 全量 **1627/1627**。
- **SDK 验证（同命令定向）**: 修复后**两次相同命令结果完全一致**（修复前同样两次差 2 项）：`Serialization.Tests.EnumConverterTests` 5F+1E、`Tests.EnumConverterTests` 1F；`serializeNegativeEnum`/`serializeNegativeFlagsEnum` 两次均通过；探针 5 次运行顺序稳定 `[Value1, Value2]`。
- **官方 Step 2**: `Tests run: 7904, Failures: 443, Errors: 504, Skipped: 16` = **947**（Round 10 为 955，Round 9 为 965）。
- **集合级证据（比总数更可靠）**: 与 Round 8 官方运行（**同一条命令**）比较失败用例集合：**新增失败 0 项、新增通过 18 项**（唯一失败数 723 → 705）。18 项中包含多个顺序敏感用例：`DictionaryTests.dictionaryWithNullShouldPreserveOrder_Serialize`、`CollectionTests.writeImmutableListTOfImmutableListT`、`CollectionTests.writeKeyValuePairOfKeyValuePair`、`ObjectTests.readSimpleClassWithObject`、`DateTimeConverterTests.nullableSerializeUTC` 等。
- **Round 11 失败族分布（官方日志）**: `AssertionError` 336、`ClassCastException` 141（`JsonElement cannot be cast ...` 为主）、`JsonException` 108、`NullPointerException` 40、`UnsupportedOperationException` 36、`IllegalArgumentException` 13、`KeyNotFoundException` 8、`ArrayIndexOutOfBoundsException` 7。
- **下一轮候选（延续优先级）**:
  1. 元素类型擦除 + 转换器选择（最大杠杆，集合簇）。
  2. `[JsonConverter]` 类型级属性未认读（`enumWithConverterAttribute`）。
  3. 枚举未定义值写出 `"_UNMAPPED"` 而非数值（`convertDayOfWeek`/`enumWithNoValues`）——**属生成产物形态改动，仍待用户批准**。
  4. `convertFileAttributes` 的枚举↔底层 `Int32` 转换器选择。

## Iteration 19 — 集合簇根因定位（类型实参在调用点被擦除）+ `JsonClassInfo` 按闭合泛型区分 + 全量结果已可复现

### 19.1 根因（最大杠杆）：参数化静态类型在调用点退化成裸 `.class`，元素/键类型无从恢复

- **证据（生成产物）** `system-text-json-tests/.../CollectionTests.java:1083-1086`：
  ```java
  public void writePrimitiveList() {
      var input = new CSharpList<Integer>(ArrayHelper.toList(new Integer[] { 1, 2 }));
      String json = dotnet.text.json.JsonSerializer.serialize(((Class<CSharpList<Integer>>) (Class<?>) CSharpList.class), input, null);
  ```
  Java 无法表达 `CSharpList<Integer>.class`，转换器只能发出**裸 raw class**（并配一个"说谎"的泛型强转），C# 侧的 `List<int>` 类型实参在调用点全部丢失。
- **下游链条**：`JsonSerializer` → `JsonSerializerOptions.getConverter/getOrAddClass` → `IEnumerableConverterFactory` 用 `TypeHelper.getGenericArguments(typeToConvert)` 取元素类型；compat 的 `getGenericArguments(rawClass)` 只有三个来源：
  1. 成员声明发布（`PropertyInfo`/`FieldInfo` → `registerMemberType`）——**只覆盖对象成员**；
  2. 会话发布（`registerParameterizedType`）——**转换器从未发出任何调用**（`grep registerParameterizedType` 在生成产物中 0 命中，属"死 API"；compat 注释明确写了它就是为 `TypeReference` 这类调用点准备的）；
  3. 都取不到时退回类型参数上界 → `Object` → 元素转换器退化为 `ObjectConverter`/JsonElement ✗。
- **失败表现（隔离运行 `CollectionTests`，342 项 142 失败）**：`JsonElement cannot be cast to Integer`(22)、`JsonException: The JSON value could not be converted to ...`(32)、`JsonElement cannot be cast to CSharp*`(7)、`Integer cannot be cast to String`(全量上下文中 29) —— 全是"元素/键类型错误"的同一根因。
- **结论**：集合簇要在 Java 侧真正修好，必须在**调用点把静态类型的类型实参发布出去**（即让转换器发出 `registerParameterizedType`）。compat 侧机制已就绪，缺的只是生成端 —— **属生成产物形态改动，已向用户提出待批准**（预计解锁集合簇 ~150 项及其它模块中的同类失败）。

### 19.2 本轮已落地的 compat 修复：`JsonClassInfo` 缓存按闭合泛型区分

- **问题**：`JsonSerializerOptions._classes`（`ConcurrentHashMap<Class, JsonClassInfo>`）经 `ConcurrentHashMapHelper.getOrAdd` 写入，键是**擦除后的裸 class** → `CSharpList<String>` 与 `CSharpList<Integer>` 共用一个 `JsonClassInfo`（连带共用一个元素类型）→ 后读的成员用前一个成员的元素转换器（`Integer cannot be cast to String`）。
- **修复（compat-only，仅 1 个调用点）**：`getOrAdd` 的两个重载都把键过一遍 `TypeHelper.converterCacheKey`（与 `tryAdd` 已有的 A+B 处理一致）；`Function` 重载仍把**调用方的原始键**传给工厂。
- **红→绿**: 新增 `ConcurrentHashMapHelperClosedGenericTest`：红——`expected: <integer-element> but was: <string-element>`（回到 HEAD 版 helper 复现）；绿——3/3 通过，compat 全量 **1630/1630**。
- **风险核对**: `JsonClassInfo.validateType` 的 `options.typeIsCached(type)` 对新键会返回 false，但 `isInvalidForSerialization` = `getIsPointer || isByRefLike || getContainsGenericParameters`，而 compat 的 `getContainsGenericParameters` 恒为 false（raw class 视为闭合运行时类型）→ 不会误抛 `CannotSerializeInvalidType` ✓。
- **隔离运行**：`CollectionTests` 342 项仍 142 失败（**预期**：隔离上下文没有成员发布，所有 `CSharpList` 都退化为"无实参"键 → 行为不变）。

### 19.3 方法学里程碑：全量结果**已可复现**（Round 11 排序修复的连带收益）

- 同一棵代码树、同一个 compat jar，连续两次官方 `mvn clean install -e`：
  - 计数完全相同：`Tests run: 7904, Failures: 440, Errors: 499, Skipped: 16`（两次均 939）；
  - 失败**用例集合完全相同**（697 项唯一名，`Compare-Object` 差集 = 0）。
- 结论：Round 11 的"按类文件声明顺序排序"不仅修了顺序断言，还**根除了运行间不确定性**——此前 r8/r9/r10/r11 之间观察到的 ±35 波动主要来自反射顺序，而非真正的抖动。**从现在起，同命令两次运行的差集是精确的**，keep/revert 判定不再需要重复运行。
- 因此 Round 12 的判定是**真实**的：相对 Round 11，**新修好 22 项、新弄坏 9 项**（净 −8 失败）：
  - 修好：`CollectionTests.read/writeSimpleClassWithImmutableArray`、`DictionaryTests.ignoreDictionaryProperty[1..7]`、`ObjectTests.classWithMixingSkippedTypes`、`ReferenceHandlerTests.dictionaryReferenceLoop` 等；
  - 弄坏（同一病根的另一种表现：元素/成员转换器错配）：`ArrayTests.readClassWithGenericICollection`、`ArrayTests.readClassWithGenericIListT`、`ArrayTests.readClassWithGenericIReadOnlyListT`、`ArrayTests.writeClassWithGenericIListT`、`ArrayTests.writeClassWithGenericIReadOnlyListT`、`CustomConverterTests.valueConverterForNullableWithoutJsonConverterAttribute`、`DictionaryKeyPolicyTests.customNameSerialize`、`DictionaryTests.dictionaryWith_ObjectWithNoParameterlessCtor_Serialize_Works`、`DictionaryTests.nullDictionaryValuesShouldDeserializeAsNull`；典型栈为 `JsonPropertyInfo1.getMemberAndWriteJson` 里 `MethodInfo.invoke` 的 `target=java.lang.String`（成员访问器拿到了别的类型的对象）。
  - **保留该改动的理由**: 它把"闭合泛型共用缓存"这一真实缺陷改正（与 A+B 设计方向一致），9 项回归与 22 项收益同源，都会在 19.1 的调用点类型实参发布落地后一起转为通过。
- **官方 Step 2（Round 12）**: `Failures: 440, Errors: 499` = **939**（Round 11: 947 → Round 10: 955 → Round 9: 965）。

## Iteration 20 — 用探针证明"调用点发布类型实参"的收益与边界；修掉会话注册被缓存键忽略的缺陷

### 20.1 探针实验（`probe/SessionTypeArgProbe.java`，每次用例独立 JVM，避免缓存掩盖效果）

| 模式 | 结果 | 含义 |
|------|------|------|
| `write-plain` | `[1,2]` ✓ | 写出路径靠运行时类型分发，本身已正确 |
| `write-reg` | `[1,2]` ✓ | 发布实参对写出无副作用 |
| `read-plain` | `[1, 2] elements=[JsonElement JsonElement]` ✗ | **读入路径**元素类型退化为 JsonElement（当前失败大簇的机制） |
| `read-reg`（先发布再反序列化） | `[1, 2] elements=[Integer Integer]` ✓✓ | **发布实参可修复读入路径**（22 项 `JsonElement cannot be cast ...` 家族的机制被证实） |
| `read-reg-after-lookup`（先查一次再发布） | `[1,2] elements=[JsonElement ...]` ✗ | 根类型的 LRU（`getOrAddClassForRootType` 的 `get_lastClass`）只比较 raw class，预热过的 JsonClassInfo 会绕过新实参 ⇒ 发布必须发生在**该类型任何查找之前** |
| `nested-write-reg`（注册 `List<List<int>>` 链） | **抛** `Integer cannot be cast to CSharpList` ✗ | 与全量日志中 `writeListOfList`/`writeIListOfIList` 的报错逐字一致：**嵌套同 raw 类的分层实参尚未被正确区分** |
| `peek-nested` | `getGenericArguments` 依次返回 `[CSharpList] → [Integer] → [Integer]` | 注册表本身的分层消费顺序是对的（外层优先 + 重复查询语义） |

结论：**调用点发布实参是有实证收益的**（读入路径：JsonElement → Integer），但落地时必须解决两件事：①发布要发生在任何查找之前（避免 LRU/缓存预热绕过）；②嵌套同 raw 类（`List<List<int>>`）的分层实参要能区分每一层的转换器——这正是 20.2 修掉的缺陷。这两点是给用户批准"生成端发布"提案时必须一并说明的边界。

### 20.2 本轮修复（compat-only）：缓存键必须反映"会话注册"的当前层

- **缺陷**：`TypeHelper.converterCacheKey` 只看 `MEMBER_ARGUMENTS`（成员发布），完全忽略会话注册（`registerParameterizedType`/`closedGeneric`）⇒ 嵌套类型的两层转换器拿到**同一个键**。红灯证据（compat 单测）：`expected: not equal but was: <io.github.ningpp.compat.CSharpList[]>`（两层都是空实参键）。
- **修复**：新增 `TypeHelper.peekGenericArguments(raw)`（**不消费**：优先返回待处理的队首层，其次返回已消费层，保证同一层的重复查询键稳定），`converterCacheKey` 在成员发布缺失时改用它。
- **红→绿**: `TypeHelperLayeredCacheKeyTest` 红（键相同）→ 绿 3/3；compat 全量 **1633/1633**。
- **对当前 SDK 是 no-op（已核对）**: `grep 'TypeHelper\.(closedGeneric|registerTypeArguments|beginTypeRegistration)'` 在生成产物中 **0 命中**，即当前 SDK 运行时根本不存在会话注册 ⇒ 该修复只有在"生成端发布实参"落地后才会生效（也正是为它铺路）。
- **尚未解决**: `nested-write-reg` 仍抛错 —— 排查显示嵌套场景下外层转换器创建期间对该 raw class 的 `getGenericArguments` 调用次数大于 1，导致在**内层**转换器查找时待处理队列已空、peek 回退到"已消费的外层"，两层键再次相同。修法需要在发布/消费链上引入"层序号"而不是仅靠队列消费顺序（下一轮 compat 侧继续），或由生成端直接传递带层信息的闭合泛型令牌。
- **官方 Step 2（Round 13）**: `Failures: 440, Errors: 499` = **939**，与 Round 12 **逐项相同**（预期如此：当前 SDK 不产生会话注册，该修复对现状是 no-op）——同时再次印证 Round 11 之后全量结果可复现。

## Iteration 21 — 分层实参的"层序"对齐：嵌套 `List<List<int>>` 探针从抛错转为 `[[1,2],[3,4]]`

- **诊断手段**: 给 compat 增加**可选**诊断开关 `-Dcs2j.trace.typeargs=true`（`TypeHelper.trace`，打印 `getGenericArguments`/`converterCacheKey` 的解析结果与来源），据此得到嵌套场景的真实调用序列：
  ```
  getGenericArguments(CSharpList) = [CSharpList]   [pending layer, 1 left behind]   ← 先解析"元素类型"，消费外层
  converterCacheKey(CSharpList)   = CSharpList[Integer] [session layer]            ← 键却取到了"下一层"
  getGenericArguments(CSharpList) = [Integer]      [pending layer, 0 left behind]
  converterCacheKey(CSharpList)   = CSharpList[Integer] [session layer]            ← 与外层同键 → 冲突
  ```
  **根因（Round 13 遗留）**: 转换器工厂是"**先**解析元素/键类型（消费一层）**再**计算缓存键"，而 Round 13 的 `peekGenericArguments` 优先取"待处理队首"⇒ 键比实际使用层**滞后一层**，外层与内层因此同键、复用同一个转换器 ⇒ `Integer cannot be cast to CSharpList`。
- **修复（compat-only）**: 改为 `currentGenericArgumentsLayer(raw)`——**已消费层优先，其次才是待处理队首**（都没消费时才用队首）：
  - 外层转换器：先消费外层 → 键 = `CSharpList[CSharpList[Integer]]` ✓
  - 内层转换器：再消费内层 → 键 = `CSharpList[Integer]` ✓ 两层不再冲突。
- **验证**:
  - compat 单测 `TypeHelperLayeredCacheKeyTest` 改为**忠实复刻真实序列**（先 `getGenericArguments` 再 `converterCacheKey`，两次）→ 断言两键不同且分别含 `CSharpList` / `Integer` ✓；compat 全量 **1633/1633**。
  - 探针（每个用例独立 JVM）: `nested-write-reg` 由**抛错**转为 **`[[1,2],[3,4]]`** ✓✓；`write-plain`/`write-reg` `[1,2]` ✓；`read-reg` 元素为 `Integer` ✓；`read-plain`/`read-reg-after-lookup` 仍为 `JsonElement`（后者为已知边界：查找先于发布时，根类型 LRU 只比较 raw class）。
- **意义**: 这条修复本身对当前 SDK 仍是 no-op（无会话注册），但它与 Round 13 一起把**"生成端发布类型实参"所需的 compat 语义完全打通并逐条验证**：发布先于任何查找 + 按层区分转换器 ⇒ 读入路径（`JsonElement → Integer`）与嵌套写出路径（`[[1,2],[3,4]]`，即 `writeListOfIList`/`writeListOfList` 家族）同时成立。
- **官方 Step 2（Round 14）**: `Failures: 440, Errors: 499` = **939**，与 Round 12/13 逐项相同（同一原因：无会话注册 ⇒ 修复不可达）。四次连续官方运行（r12、r12b、r13、r14）计数与用例集合完全一致，全量结果可复现已是稳定事实。

## Iteration 22 — 静态扫描"假覆写"（`MemberAccessor` 同类缺陷）：7 个类 / 12 处，其中一处已解释 35 项失败

- **工具**: `probe/OverrideScan.java`（反射扫描生成产物的 1707 个类）：对每个类与其所有父类/接口（同源生成树）比较方法签名；若子类方法名相同、参数表恰好相差一个**被注入的 `Class<?>` 令牌**（基类有/子类无，或反之），即判为"假覆写"。
- **结果（7 个类，12 处）**:

| 类 | 方法 | 现状 |
|----|------|------|
| `Serialization.ReflectionMemberAccessor` | `createFieldGetter` / `createFieldSetter` | 基类 `(Class<TProperty>, FieldInfo)`，覆写只有 `(FieldInfo)` ⇒ 不构成覆写，虚派发落到基类的抛异常体（`UnsupportedOperationException`）。**当前失败集中有 35 项栈内出现 `MemberAccessor.createField*`** |
| `Serialization.ReflectionEmitMemberAccessor` | `createParameterizedConstructor` / `createParameterizedConstructor_1tp` | 同类缺陷（5 参 vs 6 参、1 参 vs 2 参）；当前失败集中 0 命中（潜伏） |
| `Tests.SerializationWrapper$*SerializerWrapper`（5 个子类） | `serializeWrapper` | 基类 `(Class, Object, JsonSerializerOptions)`，覆写只有 `(Object, JsonSerializerOptions)`；0 命中（潜伏，但会静默绕过子类特化实现） |

- **发射机制定位**: 令牌注入是**按方法体**决定的 —— `ClassTransformer.cs:1495-1519`（`AddPendingClassTypeParams`，取 `context.DrainClassTypeParams()` 把 `Class<T>` 追加到"刚转换完的那个方法"）与 `MethodTransformer.cs:832/2847/2894/3080` 的多处同名逻辑；基类声明（抽象/接口映射）拿到了令牌，而具体覆写在**方法体没有引用 `typeof(T)`** 时不会拿到 ⇒ 两侧签名不一致。
- **精确修法（供批准后一次落地）**:
  1. 覆写/接口实现的方法，其注入令牌集合必须**与基类签名对齐**（把"按方法体"改为"按签名/覆写关系"，可从 C# 符号的 `IsOverride`/接口实现关系解析出基类方法已发射的参数表）；
  2. 同时为这类成员发射 `@Override` —— javac 会在**编译期**直接拦下任何残余签名不一致（本轮 12 处若早有此注解就会在 Step 2 的编译阶段暴露，而不是变成 35 项运行期失败）。
- **影响面小结（对批准决策）**: 单独修这一项即可直接消掉 35 项（`MemberAccessor.createField*`），并顺手消除 2 处潜伏缺陷；对比 (a) 调用点发布实参（数百项量级但改动面大）、(b) JSON 特性落地（20+ 项），(c) 是**纯 bug 修、风险最低、收益确定**的一项。

## Iteration 24 — (c) 第一次尝试失败并已回退：修假覆写只能"加法"，不能"减法"

- **背景**: 用户已批准 (a)/(b)/(c) 三项生成端改动，本轮先做 (c)。
- **尝试**: 把 `GenericPreprocessor.PrescanOverrideSiblings` 的"全局类型参数名集合"收窄为**按方法组**（base 方法符号 → 该组用到的 `typeof(T)` 名），并加语法键索引供重写阶段查表（重写节点可能已被上层重建，`GetDeclaredSymbol` 会抛"语法节点不在语法树中"）。
  - 单测：新增按组作用域的红→绿测试；`GenericPreprocessTests` **97/97**。
  - 全量 .NET：`CSharpToJava.Tests` 3154/3154、`ProjectTests` 58/58 ✓。
  - Step 1 重新转换：`618 succeeded, 0 failed, modules=4` ✓（276s）。
  - **Step 2 编译阶段失败（15s 内）**: `ConstructorTests.java:[82,187] 对于 deserializeWrapper(java.lang.String,<nulltype>), 找不到合适的方法`（多行同类）。
- **失败原因（重要结论）**: 声明端不再注入令牌，而**调用点仍按另一条注册链路传令牌** ⇒ 生成代码无法编译。**修假覆写必须走加法（让 override 补齐与基类一致的令牌），不能走减法（拿掉基类的令牌）。**
- **已回退**: `git revert`（提交 `fb6d81f4`），随后重新转换（`618 succeeded, 0 failed`）+ `mvn -o -q clean test-compile` 复核通过，工作区回到已知良好状态（官方指标 939）。
- **同时定位到 35 项失败的直接机制**: 哨兵注入在 `visited.Body == null` 时直接返回（`GenericPreprocessor.cs:549-550`），而 `ReflectionMemberAccessor.CreateFieldGetter/CreateFieldSetter` 是**表达式体**（`=> (obj) => ...`）⇒ 永远拿不到哨兵 ⇒ 永远没有 `Class<TProperty>` 令牌；基类却因全局名字集合被改写成 `virtual` 并拿到令牌 ⇒ 两侧签名不一致、覆写失效、虚派发落到基类抛异常体（35 项失败栈内均含 `MemberAccessor.createField*`）。
- **下一轮 (c) 的正确修法（加法、最小面）**:
  1. 保留现有全局集合行为（声明端与调用点都不动）；
  2. 对**表达式体** override：先把表达式体包装成块体（或在前面插入一条等价的 `var __cs2j_typeof_T = typeof(T);`），使 `Body == null` 不再是退出口；
  3. 注入范围用 Iteration 23 的**按组集合**，但只用于"给 override 补齐"，不再用于"收窄基类"；
  4. 重新转换后 `probe/OverrideScan.java` 复扫：目标 12 处假覆写 → 0，且编译零错误。

## Iteration 25 — (c) 落地成功（加法版）：表达式体 override 也注入哨兵 → `MemberAccessor.createField*` 35 项归零

- **修法（最小面、只加不减）**: 保留原有"全局类型参数名集合"行为（**声明端与调用点都不动**），只把注入路径的退出口去掉 —— 当方法没有块体但有**表达式体**时，先把表达式体包装为 `{ return <expr>; }` 再注入 `var __cs2j_typeof_<T> = typeof(<T>);`，并清除 `ExpressionBody`/分号（`GenericPreprocessor.cs` 注入尾部）。这正是 Iteration 24 定位到的"最后一环"。
- **红→绿**: 新增 `GenericPreprocessTests.OverrideSync_InjectsSentinelIntoExpressionBodiedOverride`（抽象基类 + 两个 override，其中一个为**表达式体且体内无 typeof**）：
  - 红（HEAD 退出口）: `表达式体 override 也应被注入哨兵（期望 >=3 处，实际 2 处）`；
  - 绿: 1/1 通过。
  - `GenericPreprocessTests` **96/96**；全量 `dotnet test -c Release` → **3154/3154 + 58/58**，0 失败。
- **Step 1**: `Conversion complete (MSBuild): 618 succeeded, 0 failed, 3 resources copied, modules=4`（276s，300s 预算内）。
- **Step 2（官方）**: 编译**零错误** ✓，`Tests run: 7904, Failures: 442, Errors: 487, Skipped: 16` = **929**（上一稳定值 939）。
- **集合级验证（决定性）**:
  - 失败栈中含 `MemberAccessor.createField*` 的用例：**35 → 0** ✓（机制完全按预期消除）；
  - 与 939 基线（Round 12/13/14 三次逐项一致）比较：**新增失败 0 项、新增通过 10 项**（唯一失败数 697 → 687）⇒ 纯收益、零回归。
- **说明**: 本轮收益（−10）小于预测的 35，因为 35 项中含 `MemberAccessor.createField*` 的那些原本还可能因**其它**根因失败（例如元素类型擦除），修复只解除了其中一层。剩余项仍在 929 中，随 (a)/(b) 落地继续收敛。

## Iteration 26 — 为 (a) 准备 compat 发射 API（本轮不改变生成产物，对现状 no-op）

- **新增 compat API**: `TypeHelper.parameterized(Class<?> raw, Type... args)`（构造可嵌套的 `ParameterizedType`，可表达 `List<List<int>>`）+ `TypeHelper.closedGenericType(Type closed)`（发布**每一层**并返回擦除后的 raw class，保持生成代码里既有强转可编译）。
- **拟用的发射形态（下一轮改转换器，一处 typeof 发射点）**:
  ```java
  JsonSerializer.serialize(
      (Class<CSharpList<CSharpList<Integer>>>) (Class<?>)
          TypeHelper.closedGenericType(TypeHelper.parameterized(
              CSharpList.class, TypeHelper.parameterized(CSharpList.class, Integer.class))),
      value, null);
  ```
  要点：注册发生在**任何查找之前**（探针已证明查找先于发布会因根类型 LRU 而失效），且每层都注册（Round 14 已证明按层区分才能让外层/内层转换器各就各位）。
- **验证**: 新增 `ClosedGenericEmissionApiTest`（3 项：多层外层优先、单层、非泛型直通）→ 绿；compat 全量 **1636/1636**。该 API 目前**无调用方**，因此对 SDK 现状是 no-op（官方指标仍为 929），纯粹是把 (a) 的落地成本压缩成"一处发射改动 + 一次重新转换"。

## Iteration 27 — (a) 发射点普查（为下一轮精确定位；本轮不改代码）

`grep '\) \(Class<\?>\) '` 在转换器中命中 **12 处**"说谎强转"发射点（生成代码里 `((Class<X<Y>>) (Class<?>) X.class)` 这一形态的来源）：

| 文件 | 行 | 形态 |
|------|----|------|
| `ConversionContext.cs` | 773 | `((Class<{MapType(typeSymbol)}>) (Class<?>) {helperCall})` |
| `HIR/HIRExpressionGenerator.cs` | 249 | 类型实参令牌包裹 |
| `InvocationExpressionTransformer.cs` | 864 | 泛型调用的**实参**类字面量包裹 |
| 同上 | 5690 / 5827 | `RefCountedReader<T>` / `Deserialize<T>` 的类字面量 |
| 同上 | 10663 | `((Class<Throwable>) (Class<?>) ...)`（异常路径，与本次无关） |
| 同上 | 12554 | `token = ((Class<{MapType(concreteType)}>) (Class<?>) {token})` |
| 同上 | 13068 | `((Class) (Class<?>) {t})`（注释说明为避免类型推断失败） |
| 同上 | 13538 / 13583 | 令牌注入（`literal` / null 令牌） |
| `StatementTransformer.Loops.cs` | 555 | 仅字符串匹配判断，非发射点 |

- **下一轮定位方法**: 目标生成形态是
  `JsonSerializer.serialize(((Class<CSharpList<Integer>>) (Class<?>) CSharpList.class), input, null)`
  —— 即"**参数化静态类型**被 `MapType` 映射成 `Class<X<Y>>` 后与裸 `X.class` 组合"。应先用一个最小样例（`WritePrimitiveList` 那类 `JsonSerializer.Serialize(List<int>)`）确定它由 864（泛型实参包裹）还是 12554/13538（令牌注入）产生，**只改命中的那一处**，把 `X.class` 换成
  `TypeHelper.closedGenericType(TypeHelper.parameterized(X.class, <递归的实参 Type>))`。
- **风险控制**: 上一轮 (c) 的教训是"改动面越大越容易在调用点失配"；(a) 同样只应改**实际命中**的发射点，改完立即用 Step 1 + Step 2 验证（编译零错误 + 集合差 0 回归），再考虑是否推广到其余同类点。

## Iteration 28 — (a) 命中点确认：`InvocationExpressionTransformer.cs:860-865`（附可直接落地的补丁设计）

- **确认依据**：该处正是"**显式泛型调用的类型实参** → 类字面量令牌"的发射点：
  ```csharp
  argLiteral = ConversionContext.GetClassLiteral(typeArg, context);      // → X.class
  if (typeArg is INamedTypeSymbol namedArg2 && namedArg2.IsGenericType)
  {
      var javaArgType2 = context.MapType(typeArg);
      argLiteral = $"((Class<{javaArgType2}>) (Class<?>) {argLiteral})";  // ← 生成物里的"说谎强转"
  }
  ```
  与失败样例 `JsonSerializer.serialize(((Class<CSharpList<Integer>>) (Class<?>) CSharpList.class), input, null)` 完全对应（`CSharpList<Integer>` 即 `List<int>` 的映射）。
- **补丁设计（只改这一处 + 一个递归小工具；仍返回 `Class`，故不改任何签名/调用点）**:
  ```csharp
  // 新增：把闭合泛型（含嵌套层）表达成 compat 可发布的 Type
  private static string EmitClosedGenericType(INamedTypeSymbol type, ConversionContext context)
  {
      var args = type.TypeArguments;
      if (args.Length == 0) return ConversionContext.GetClassLiteral(type, context);
      var parts = args.Select(a => a is INamedTypeSymbol n && n.IsGenericType
          ? EmitClosedGenericType(n, context)
          : ConversionContext.GetClassLiteral(a, context));
      return "io.github.ningpp.compat.TypeHelper.closedGenericType("
           + "io.github.ningpp.compat.TypeHelper.parameterized("
           + ConversionContext.GetClassLiteral(type, context) + ", " + string.Join(", ", parts) + "))";
  }
  // 864 处改为：
  argLiteral = $"((Class<{javaArgType2}>) (Class<?>) {EmitClosedGenericType(namedArg2, context)})";
  ```
- **为何这条路径安全**（对比 Round 16 的失败）：发射结果仍是 `Class`（外面那层 `(Class<X<Y>>) (Class<?>)` 强转保持不变）⇒ **不动任何方法签名**，只在求值时**多一次注册**；调用点、声明端、覆写关系都不受影响。
- **验证顺序（下一轮）**: `dotnet build -c Release` → `dotnet test -c Release`（应保持 3154+58 全绿）→ Step 1 转换（期望 `618 succeeded, 0 failed`）→ Step 2 官方（编译零错误是硬门槛）→ 与 `fails-r17-official.txt` 做集合差，要求**新增失败 0**；随后用 `probe/SessionTypeArgProbe.java` 的 `read-reg` 语义在真实用例上抽查（`JsonElement → Integer`、嵌套 `[[1,2],[3,4]]`）。




## Iteration 29 — (a) 第一次发射尝试被证伪：`InvocationExpressionTransformer.cs:860-865` 不是命中点（已回退）

- **改动（已回退）**: 把该处的泛型类型实参令牌从裸 `X.class` 换成
  `TypeHelper.closedGenericType(TypeHelper.parameterized(X.class, <实参类字面量...>))`（结果仍是 `Class`，不改签名）。
- **验证**: `dotnet test -c Release` 3154+58 全绿 ✓；Step 1 转换 `618 succeeded, 0 failed` ✓；官方 Step 2 **编译零错误** ✓，但 `Failures: 442, Errors: 487` = **929 与改变前逐项相同**，且失败用例集合差 **0**。
- **证伪证据（决定性）**: 生成产物 `CollectionTests.java:1085` 仍是
  `JsonSerializer.serialize(((Class<CSharpList<Integer>>) (Class<?>) CSharpList.class), input, null)`，
  且全文件 `closedGenericType` 出现次数 = **0** ⇒ 该处发射点**根本没有被这条调用路径命中**。
- **结论**: 该强转形态由**另一条路径**产生（显式泛型实参令牌不是它的来源）；候选回到 `ConversionContext.GetClassLiteral`（`ConversionContext.cs:773` 的 `((Class<{MapType(typeSymbol)}>) (Class<?>) {helperCall})`）与 `HIRExpressionGenerator.cs:249`。
- **下一轮定位法（更可靠）**: 直接以生成产物为准做**反向定位** —— 在转换器里给 `GetClassLiteral` 的返回处临时加"是否参数化"的诊断计数/日志，跑一次最小样例转换（`Serialize(List<int>)` 那类），看哪一处发射产生了带 `Class<X<Y>>` 强转的字面量；不要再用"读代码猜路径"的方式（本轮已证明会猜空）。
## Iteration 30 — (a) 命中点已找到（`InvocationExpressionTransformer.cs:12554`），但当前语义下**净负收益**，已回退

- **命中确认**: 在 12554（泛型方法调用的令牌发射，条件为"推断出的类型实参与某个实参静态类型一致"）处把裸令牌换成
  `TypeHelper.closedGenericType(TypeHelper.parameterized(X.class, <实参类字面量...>))` 后，生成产物随即变为
  `JsonSerializer.serialize(((Class<CSharpList<Integer>>) (Class<?>) TypeHelper.closedGenericType(TypeHelper.parameterized(CSharpList.class, int.class))), input, null)`
  且 `CollectionTests.java` 中 `closedGenericType` 出现 **74 次** ⇒ 这里就是产生该令牌的真实路径（Round 22 的证伪成立）。
- **验证**: `dotnet test -c Release` 3154+58 全绿 ✓；Step 1 `618 succeeded, 0 failed` ✓；Step 2 **编译零错误** ✓；
  **官方 935（438F + 497E）**，相对 r17 的 929：**新增失败 12 / 新增通过 6** ⇒ 净 −6 ✗。
- **失败原因（关键机制）**: compat 的会话注册在 `getGenericArguments` 中**优先于成员发布**（注释明确："Session registrations ... are consumed before member type lookup, so they keep priority"）。因此在**每个**调用点无条件注册会把**成员声明推导出的正确实参顶掉**：原本靠 `PropertyInfo/FieldInfo` 发布得到正确元素类型的成员路径，被调用点注册的（可能更粗或错层的）实参覆盖 ⇒ 12 项回归、6 项收益。
- **结论与下一轮方向**: (a) 不能"无条件在每个调用点注册"，必须是**非覆盖式**：
  1. 仅在**当前没有成员层可用**时注册（compat 侧可加"低优先级注册"入口，如 `registerCallSiteType`：填充但不覆盖已有成员/会话层）；
  2. 或让调用点注册**只对本层生效**并保留成员层优先级；
  3. 落地后仍需满足"发布先于任何查找"（否则根类型 LRU 会绕过）。
  compat 侧改动很小（新增低优先级注册 + 分层键沿用 Round 13/14 的实现），转换器侧仍是 12554 那一处包裹。
## Iteration 31 — (a) 非覆盖式注册：可直接落地的设计（本轮不改代码）

Round 23 已确认：命中点是 `InvocationExpressionTransformer.cs:12554`，但**无条件会话注册**会顶掉成员层实参（官方 929 → 935，净 −6）。修法是**低优先级注册**，compat 侧改动很小：

1. **compat 新增两张语义（约 20 行）**
   ```java
   private static ThreadLocal<Map<Class<?>, java.lang.reflect.Type[]>> CALLSITE_ARGUMENTS;
   /** 低优先级注册：只在"没有任何成员层/会话层生效"时被采用，绝不覆盖既有层。 */
   public static void registerCallSiteType(java.lang.reflect.Type type) {
       if (type instanceof java.lang.reflect.ParameterizedType pt) {
           Class<?> raw = (Class<?>) pt.getRawType();
           var args = pt.getActualTypeArguments();
           if (args.length > 0) CALLSITE_ARGUMENTS.get().put(raw, args);
           for (var a : args) registerCallSiteType(a);       // 嵌套逐层
       }
   }
   /** 发射形态：注册低优先级实参并返回擦除后的 raw class（保持既有强转可编译）。 */
   public static Class<?> closedCallSiteGenericType(java.lang.reflect.Type closed) {
       registerCallSiteType(closed);
       return toRawClass(closed);
   }
   ```
2. **`getGenericArguments` 的兜底位置插入**（必须在"类型参数上界/Object"之前、`CONSUMED`/`TYPE_ARGUMENTS` 之后）：
   ```java
   var callSite = CALLSITE_ARGUMENTS.get().get(type);
   if (callSite != null && callSite.length > 0) return eraseToClasses(callSite);
   ```
   `currentGenericArgumentsLayer`（缓存键用）同样把 call-site 作为**最低**回退，保证键与实参一致。
3. **生命周期**: `clearThreadRegistrations()` / `beginTypeRegistration()` / `endTypeRegistration()` 一并清理 `CALLSITE_ARGUMENTS`，避免跨调用残留。
4. **转换器（唯一一处）**: 12554 处把 `closedGenericType` 换成 `closedCallSiteGenericType`（其余包裹形态照 Round 23 已验证的样子）。
5. **预期**: 成员路径（deque/CONSUMED 存在）完全不受影响 ⇒ 消除 Round 23 的 12 项回归；无成员上下文的调用点（`Serialize(List<int>)`、嵌套 `List<List<int>>`）拿到调用点实参 ⇒ 保住 6 项收益并扩大（探针 `read-reg` 已证明 `JsonElement → Integer`）。
6. **验证顺序**: compat `mvn -o clean install`（期望 1636+ 全绿，新增 `ClosedCallSiteRegistrationTest` 覆盖"有成员层时不覆盖 / 无成员层时生效 / 嵌套逐层"）→ `dotnet build -c Release` → `dotnet test -c Release`（3154+58）→ Step 1（618/0）→ 生成产物复查 `CollectionTests.java` 出现 `closedCallSiteGenericType` → Step 2 官方（编译零错误；与 `mvn-build-r17.log` 集合差要求新增失败 0）。
