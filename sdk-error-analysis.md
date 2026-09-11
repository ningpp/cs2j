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

