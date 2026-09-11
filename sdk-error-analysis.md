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

- **Step 1（转换）**: `Conversion complete (MSBuild): 618 succeeded, 0 failed, 3 resources copied, modules=4`，退出码 **0**（耗时 276s）。
  - 产物模块：`system-private-uri`、`system-text-json`、`system-text-json-tests`（+ 根 parent pom）。
- **Step 2（`mvn clean install -e`，完整日志 `d:\cs-json-0911-sdk\mvn-build.log`）**:
  - **编译阶段：0 个 javac 错误**（`system-private-uri`、`system-text-json` 138 文件、`system-text-json-tests` 447 文件全部编译通过）→ "零错误编译"目标达成。
  - **测试阶段：`Tests run: 7904, Failures: 499, Errors: 543, Skipped: 16` → BUILD FAILURE**（`SurefirePlugin` 生成 `testFailureIgnore=false`，故测试失败即构建失败）。
  - 说明：`mvn clean install -e` 的完整执行（含 7904 个测试）耗时约 441s，超出单命令 300s 预算，故该项以更长超时在后台运行一次以取得完整日志；日志已保存在目标目录。

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

## 已定位但**尚未修复**的深层根因（下一轮起点）

**泛型实参在成员读取处被擦除 + 转换器缓存按原始类共享**（两者叠加才是真正的瓶颈）：

1. `PropertyInfo.getPropertyType()` / `FieldInfo.getFieldType()` 返回擦除后的 `Class`（`CSharpDictionary.class`），转换器工厂只能拿 `TypeHelper.getGenericArguments(raw)`，在无会话注册时退化为类型参数上界 → 键/元素类型变成 `Object`。实测探针（`D:\code\cs2j\probe\TypeArgProbe.java`）：
   `MyStringToStringGenericDict` declared=`CSharpDictionary<String,String>` recovered=`[Object, Object]`。
2. 尝试在 `PropertyInfo`/`FieldInfo` 里发布声明泛型实参（`TypeHelper.registerMemberType`，按 raw 类保留"外层优先"链并在查询时消费）后：`dictionary key type` 日志行 262→26、`JsonElement cannot be cast` 211→72 **明显改善**，但 `Integer cannot be cast to String`（写入路径）与 `could not be converted to ...CSharpList` 反而上升，失败总数 `1006 → 1081`，因此**已回退**（该实现见本轮迭代记录，可复现）。
3. 原因是生成的 `JsonSerializerOptions._converters` 是 `ConcurrentHashMap<Class, JsonConverter>`——**只按原始 Class 缓存**，`CSharpList<String>` 与 `CSharpList<CSharpInteger>` 共用同一个转换器实例。把实参发布正确后，缓存里第一个转换器变成"具体但可能属于别的成员"的类型；此前所有转换器都是 `Object` 型（宽松但错误表现为 JsonElement）。要真正修好，需要让转换器缓存按**闭合泛型**（raw + 实参）区分，例如在生成代码的 `getConverter` 缓存键上引入 compat 的复合键，或在转换器侧改写 `GetConverter` 缓存访问。

其他仍然存在的主要失败簇（最新一轮全量：`Tests run: 7904, Failures: 496, Errors: 511`）：
`java.lang.AssertionError`（453，绝大多数是"元素类型变成 JsonElement 导致断言值不等"）、`JsonElement cannot be cast to SimpleTestClass/String/Integer`（33+26+24 行）、`not a supported Dictionary key type`（85 行）、`could not be converted to ...CSharpList`（38 行）、`CSharpDictionary.getChecked` 等 NPE（22 行）。

**另一个已定位但未修复的簇（`ImmutableDictionary cannot be cast to CSharpReadOnlyDict`，27 行）**：
`ImmutableDictionaryOfTKeyTValueConverter.onWriteResume` 的调用点会对值做 `checkcast CSharpReadOnlyDict`（`TCollection` 的擦除上界，来自 C# `where TCollection : IReadOnlyDictionary<TKey,TValue>`），而 compat 的 `ImmutableDictionary<K,V>` 只实现 `IImmutableDictionary`。
尝试让 `ImmutableDictionary`/`ImmutableSortedDictionary` 实现 `CSharpReadOnlyDict<K,V>` 会编译失败：compat 的 `CSharpGenericIterable` 继承 `java.util.Collection`，于是要求 `void clear()` 与 `tryGetValue(K)` 等，而不可变字典的 `clear()` 返回新实例（C# 语义）→ 签名冲突。
真正的修法是让 `IReadOnlyDictionary<TKey,TValue>` 在 compat 里映射到一个**只读**接口（不含 `Collection` 的可变方法），但该接口被全部生成代码使用，属于跨项目改动，本轮未做（改动与失败原因已记录，避免重复尝试）。




