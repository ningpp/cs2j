﻿# SDK 编译错误分析

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
