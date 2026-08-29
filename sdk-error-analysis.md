﻿# SDK Conversion Error Analysis

## Iteration 1 — final 字段重复赋值（value class 隐式 final 降级失效）
- **Java 文件**: `system-private-uri/src/main/java/dotnet/system/Collections/Generic/ArrayBuilder.java`
- **行号**: 59
- **错误信息**: `/D:/cs-java-816-sdk/system-private-uri/src/main/java/dotnet/system/Collections/Generic/ArrayBuilder.java:[59,9] 可能已分配变量_array`
- **代码片段**:
  ```java
  public ArrayBuilder(int capacity, Class<?> tClass) {
          this.tClass = tClass;
          this._array = null;
          this._count = 0;
          // Debug.Assert(capacity >= 0);
          if (capacity > 0) {
          _array = (T[]) TypeHelper.newArrayInstance(tClass, capacity);
          }
      }
  ```
- **对应 C# 文件**: `D:\csharp-sdk\uri\src\Common\System\Collections\Generic\ArrayBuilder.cs`
- **根因分类**: Transformer
- **涉及组件**: `src/CSharpToJava.Core/Transformers/Type/StructTransformer.cs`（`FixReadonlyStructConstructorChaining` / `DropFinalFromRepeatedlyAssignedFields`）
- **分析**: readonly struct 在转成 value class 时，`ArrayBuilder(int capacity) : this()` 的 `this()` 链被 `AddRuntimeClassConstructorParameters` 改写成 `this(tClass)`，随后被 `FixReadonlyStructConstructorChaining` 内联，把默认构造函数中的 `this._array = null` 拼进参数化构造函数，接着 `if` 内再次给 `_array` 赋值。普通类场景下 `DropFinalFromRepeatedlyAssignedFields` 会把 `_array` 降为非 final 绕过该错误；但 value class 的实例字段隐式 final，降级后仍然非法。同类问题还包括：final 字段只在 `if` 内被赋值（无顶层默认赋值）时，javac 报“可能尚未初始化变量”。
- **状态**: ✅ Fixed

## Iteration 2 — `First()/Last()` 返回数组后误加 `.boxed()`
- **错误信息**: 回归测试 `FirstElementContainsOnArrayTests.cs` 失败：`result.First().Contains(1)` 生成 `...findFirst().orElseThrow().boxed()...`，`orElseThrow()` 已返回 `int[]`，`int[].boxed()` 非法。
- **根因分类**: Transformer
- **涉及组件**: `src/CSharpToJava.Core/Transformers/Expression/Transformers/InvocationExpressionTransformer.cs`
- **分析**: `Contains` 处理用 `ContainsStreamMethodAtTopLevel` 判断 Java 接收表达式是否已是流；`...findFirst().orElseThrow()` 含 `.stream()` 被误判为流，但该链终态是数组元素，导致追加非法 `.boxed()`。
- **修复**: C# 接收表达式为返回数组的方法调用（`First()`/`Last()`/`ToArray()` 等）时，统一用 `Arrays.stream(receiver).boxed().collect(Collectors.toSet()).contains(...)`；直接 `int[].Contains(...)` 的双重包装保护保持不变。
- **状态**: ✅ Fixed

## 最终复验（2026-08-16 第三次）

- 使用同一份转换器代码重新转换 `D:\csharp-sdk` → `d:\cs-java-816-sdk`
- `mvn clean install -e` → **BUILD SUCCESS**（7102 个测试，0 失败）
- 同一份代码同时验证 AGL：`mvn clean package -e` → **BUILD SUCCESS**（609 个测试，0 失败 0 错误；`CSharpGenericIterable.from(null)` 修复后 Rectilinear 10 个 NPE 全部消失）
- compat：`mvn test` → **1500 个测试全部通过，0 失败**

## Iteration 3 — 局部函数捕获变量 IsWritten 传递闭包未提升
- **Java 文件**: `dotnet-text-regularexpressions/src/main/java/dotnet/text/regularexpressions/RegexCompiler.java`
- **行号**: 2084（首批错误之一，同模式共 14+ 处）
- **错误信息**: `不兼容的类型: io.github.ningpp.compat.Label无法转换为io.github.ningpp.compat.ObjectHolder<io.github.ningpp.compat.Label>`（同模式还有 `int无法转换为io.github.ningpp.compat.IntHolder`）
- **代码片段**:
  ```java
  // emitNode 参数 _cs2j_cap_doneLabel 类型为 Label（只读）
  // 但 emitSingleCharAtomicLoop 参数 _cs2j_cap_doneLabel 类型为 ObjectHolder<Label>（写入）
  // emitNode 内调用 emitSingleCharAtomicLoop 时传入 Label → 类型不兼容
  private void emitNode(RegexNode node, Label _cs2j_cap_doneLabel, ...) {
      case RegexNode.Oneloopatomic, ...:
      emitSingleCharAtomicLoop(node, _cs2j_cap_doneLabel, ...); // ← Label 传给 ObjectHolder<Label>
  }
  ```
- **对应 C# 文件**: `D:\csharp-sdk\regular-expressions\src\System\Text\RegularExpressions\RegexCompiler.cs`
- **根因分类**: Transformer
- **涉及组件**: `src/CSharpToJava.Core/Transformers/Statement/StatementTransformer.LocalFunctions.cs`（`PrescanLocalFunctions` 传递闭包）
- **分析**: C# 局部函数 `EmitNode` 只读取捕获变量 `doneLabel`（`IsWritten=false`），但它调用的 `EmitSingleCharAtomicLoop` 写入 `doneLabel`（`IsWritten=true`）。`PrescanLocalFunctions` 的 fixpoint 传播 callee 的 captures 给 caller 时，如果 caller 已有同名 capture（`IsWritten=false`），直接跳过不更新——导致 caller 的参数类型保持为 `Label` 而非 `ObjectHolder<Label>`，调用 callee 时传 `Label` 给期望 `ObjectHolder<Label>` 的参数。

## Iteration 4 — `new T[n]` 泛型数组创建误用 `T.class`
- **Java 文件**: `system-private-uri/src/main/java/dotnet/system/Collections/Generic/ArrayBuilder.java`
- **行号**: 59（同模式共 3 处：59、131、168）
- **错误信息**: `无法从类型变量中进行选择`（cannot select from a type variable）
- **代码片段**:
  ```java
  // 行 59
  _array = CSharpArray.of(java.lang.reflect.Array.newInstance(TypeHelper.toWrapperType(tClass), capacity)).toArray(T.class);
  // 行 131
  result = CSharpArray.of(java.lang.reflect.Array.newInstance(TypeHelper.toWrapperType(tClass), _count)).toArray(T.class);
  // 行 168
  T[] next = CSharpArray.of(java.lang.reflect.Array.newInstance(TypeHelper.toWrapperType(tClass), nextCapacity)).toArray(T.class);
  ```
- **对应 C# 文件**: `D:\csharp-sdk\uri\src\Common\System\Collections\Generic\ArrayBuilder.cs`
- **根因分类**: Transformer
- **涉及组件**: `src/CSharpToJava.Core/GenericPreprocess/GenericPreprocessor.cs`（`VisitArrayCreationExpression`：`new T[n]` → `(T[])Array.CreateInstance(typeof(T), n)`）、`src/CSharpToJava.Core/Transformers/Expression/Transformers/InvocationExpressionTransformer.cs`（`Array.CreateInstance` 处理）、`src/CSharpToJava.Core/Transformers/Expression/Transformers/TypeOperationTransformer.cs`（`(T[])` cast → `.toArray(T.class)`）
- **分析**: GenericPreprocessor 将 `new T[n]` 重写为 `(T[])Array.CreateInstance(typeof(T), n)`。然后 `InvocationExpressionTransformer` 将 `Array.CreateInstance(typeof(T), n)` 转成 `CSharpArray.of(java.lang.reflect.Array.newInstance(TypeHelper.toWrapperType(tClass), n))`，外部的 `(T[])` cast 被 `TypeOperationTransformer` 转成 `.toArray(T.class)`，但 `T.class` 在 Java 中对类型变量是非法的。修复方案：(1) `InvocationExpressionTransformer` 中检测 `Array.CreateInstance(typeof(T), n)` 当 T 有 runtime class parameter 时直接返回 `TypeHelper.newArrayInstance(tClass, n)`；(2) `TypeOperationTransformer` 中当目标数组元素类型是类型参数时直接用 `(T[])` cast 代替 `.toArray(T.class)`。
- **状态**: ✅ Fixed

## Iteration 5 — 方法级类型参数的 `Array.CreateInstance(typeof(T), n)` 未触发特殊情况
- **Java 文件**: `system-private-xml/src/main/java/dotnet/xml/schema/XmlUntypedStringConverter.java`
- **行号**: 311
- **错误信息**: `java.lang.ClassCastException: class io.github.ningpp.compat.CSharpArray cannot be cast to class [Ljava.lang.Object;`（运行时，编译无错）
- **代码片段**:
  ```java
  private <T> T[] toArray(Class<T> _cs2j_T, String[] stringArray, ...) {
      try {
          T[] arrDst = (T[]) (Object) (CSharpArray.of(java.lang.reflect.Array.newInstance(TypeHelper.toWrapperType(_cs2j_T), stringArray.length)));
  ```
- **对应 C# 文件**: `D:\csharp-sdk\xml\src\System\Xml\Schema\XmlUntypedStringConverter.cs`（`ToArray<T>` 方法）
- **根因分类**: Transformer
- **涉及组件**: `src/CSharpToJava.Core/Transformers/Expression/Transformers/InvocationExpressionTransformer.cs`（`Array.CreateInstance` 特殊处理路径）、`src/CSharpToJava.Core/Transformers/Expression/Transformers/TypeOperationTransformer.cs`（`(T[])` cast 处理）
- **分析**: GenericPreprocessor 将 `new T[n]` 重写为 `(T[])Array.CreateInstance(typeof(T), n)`。`InvocationExpressionTransformer` 的特殊情况检查 `TryGetRuntimeClassParameter(typeParam.Name, ...)`，但该方法级类型参数 T 的 runtime class parameter (`_cs2j_T`) 是在方法体转换**之后**由 `AddPendingRuntimeClassParameters` 注册的。转换方法体时 `TryGetRuntimeClassParameter` 返回 false，特殊情况不触发，走了通用 fallback 路径 `CSharpArray.of(...)`。同样，`TypeOperationTransformer` 中 `(T[])` cast 也因 `TryGetRuntimeClassParameter` 返回 false 走了 `(T[]) (Object)` 路径。结果：`CSharpArray` 对象无法 cast 为 `Object[]`，运行时 `ClassCastException`。
- **状态**: ✅ Fixed

## Iteration 6 — `Activator.CreateInstance<T>()` 生成无参数调用 & `system-private-uri` 依赖缺失
- **Java 文件**: `system-text-json/src/main/java/dotnet/text/json/Serialization/ReferenceHandler1.java`
- **行号**: 29
- **错误信息**: `无法将类 io.github.ningpp.compat.Activator中的方法 createInstance应用到给定类型; 需要: java.lang.Class<T> 找到: 没有参数`（同时有 `Uri`/`UriKind` 符号未找到错误，根因是 `pom.xml` 缺少 `system-private-uri` 依赖）
- **代码片段**:
  ```java
  public ReferenceResolver createResolver() { return Activator.createInstance(); }
  ```
- **对应 C# 文件**: `D:\csharp-sdk\json\src\System\Text\Json\Serialization\ReferenceHandlerOfT.cs`（`new T()` → `Activator.CreateInstance<T>()`）
- **根因分类**: Transformer
- **涉及组件**: `src/CSharpToJava.Core/GenericPreprocess/GenericPreprocessor.cs`（`new T()` → `Activator.CreateInstance<T>()`）、`src/CSharpToJava.Core/Transformers/Expression/Transformers/InvocationExpressionTransformer.cs`（`Activator.CreateInstance<T>()` 泛型调用处理）、`src/CSharpToJava.Core/Transformers/Utilities/RuntimeClassParameterHelper.cs`（`AddObjectCreationRequirements` 仅扫描 `BaseObjectCreationExpressionSyntax`，不扫描重写后的 `Activator.CreateInstance<T>()`）
- **分析**: GenericPreprocessor 将 `new T()` 重写为 `Activator.CreateInstance<T>()`。转换器未检测此泛型调用的类型参数 T 需要运行时 Class 参数，因为：(1) `AddObjectCreationRequirements` 仅扫描 `BaseObjectCreationExpressionSyntax`，重写后已变为 `InvocationExpressionSyntax`；(2) `InvocationExpressionTransformer` 的 `Activator.CreateInstance` 处理仅覆盖非泛型形式（1 个参数），不覆盖 `CreateInstance<T>()`（0 个参数）。同时 `system-text-json` 的 `pom.xml` 缺少对 `system-private-uri` 模块的依赖，导致 `Uri`/`UriKind` 符号找不到。
- **修复**: (1) 在 `InvocationExpressionTransformer.cs` 中添加 `Activator.CreateInstance<T>()` 泛型调用处理，检测类型参数 T 并生成 `Activator.createInstance(_cs2j_T)` 或 `Activator.createInstance(tClass)`。(2) 在 `Program.cs` 中添加 `DiscoverImplicitModuleDependencies` 后处理步骤：在所有 Java 源码生成完毕后，扫描各模块 `.java` 文件的 `package` 声明和 `import` 语句，构建 包名→模块名 映射，自动发现 C# 项目引用未覆盖的隐式模块间依赖（如框架类型 `System.Uri` 映射到 `system-private-uri` 模块），并重新生成受影响模块的 `pom.xml`。
- **状态**: ✅ Fixed

## Iteration 7 — EnumValue.ToString() 被误识别为 [Flags] 枚举
- **Java 文件**: system-text-json-tests/src/test/java/dotnet/text/json/Serialization/Tests/PropertyVisibilityTests.java`n- **行号**: 2606, 2648
- **错误信息**: 无法将类 java.lang.Enum<E>中的方法 toString应用到给定类型; 需要: 没有参数 找到: dotnet.text.json.Serialization.Tests.PropertyVisibilityTests.MyEnum`n- **代码片段**:
  `java
  public String getEnumString() { return PropertyVisibilityTests.MyEnum.toString(getEnumValue()); }
  ``n- **对应 C# 文件**: D:\csharp-sdk\json\tests\Serialization\PropertyVisibilityTests.cs（第 1413 行 get => EnumValue.ToString();）
- **根因分类**: Transformer
- **涉及组件**: src/CSharpToJava.Core/Transformers/Expression/Transformers/InvocationExpressionTransformer.cs（第 4727-4740 行 flags 枚举 fallback）
- **分析**: MyEnum（非 [Flags] 枚举，在 PropertyVisibilityTests 中）被误识别为 [Flags] 枚举。原因是 EnumConverterTests.cs 第 272 行有一个同名的 [Flags] public enum MyEnum。当 TypeMappingService.MapType 处理 EnumConverterTests.MyEnum 时，调用 RegisterFlagsEnum("MyEnum", ...) 注册了简单名。然后在 InvocationExpressionTransformer 的 flags 枚举 fallback 中，context.IsFlagsEnum(receiverSymbol.Name) 使用简单名 MyEnum 检查，匹配到了 EnumConverterTests.MyEnum 的注册。这导致非 [Flags] 的 PropertyVisibilityTests.MyEnum 被误处理为 [Flags] 枚举，调用 BuildFlagsEnumToString 生成了 PropertyVisibilityTests.MyEnum.toString(getEnumValue())（静态调用）。
- **修复**: 移除 fallback 中的简单名检查 context.IsFlagsEnum(receiverSymbol.Name)，只使用完全限定名 context.IsFlagsEnum(receiverSymbol.ToDisplayString()) 检查，避免同名但不同 [Flags] 属性的枚举误匹配。
- **状态**: ✅ Fixed


## Iteration 8 — Stream.concat 参数类型不匹配（byte[] Concat）
- **Java 文件**: system-text-json-tests/src/test/java/dotnet/text/json/Tests/JsonDocumentTests.java`n- **行号**: 282, 288, 294, 300
- **错误信息**: 无法将接口 java.util.stream.Stream<T>中的方法 concat应用到给定类型; 需要: Stream<? extends T>,Stream<? extends T> 找到: IntStream,Stream<Object>
- **代码片段**:
  `java
  Stream.concat(IntStream.range(0, Utf8Bom.length).map(i -> Utf8Bom[i]), IntStream.range(0, bytes.length).mapToObj(i -> bytes[i]))
  ``n- **对应 C# 文件**: D:\csharp-sdk\json\tests\JsonDocumentTests.cs（第 369 行 Utf8Bom.Concat(bytes).ToArray()）
- **根因分类**: Transformer
- **涉及组件**: src/CSharpToJava.Core/Transformers/Expression/Transformers/InvocationExpressionTransformer.cs（第 6405-6451 行 Concat 处理）、src/CSharpToJava.Core/Transformers/Expression/Utilities/ExpressionTransformerHelpers.cs（BuildArrayStreamExpression）
- **分析**: yte[] 数组通过 BuildArrayStreamExpression 转换为 IntStream.range(0, n).map(i -> arr[i])（IntStream），因为 Java 的 Arrays.stream() 不支持 yte[]。在 Concat 处理中，receiver 是 IntStream，参数也是 stream，但 Stream.concat 需要两个 Stream<? extends T> 参数，IntStream 不兼容。修复前的 .boxed() 检查只处理 int[]/long[]/double[]，不覆盖 yte[] 等。
- **修复**: (1) 在 Concat 处理中扩展 .boxed() 检查范围，覆盖所有原始类型数组（byte[]/short[]/char[]/float[]/boolean[]）；(2) 添加 ReceiverLooksLikePrimitiveStream 方法检测已转换为原始 stream 的 receiver 字符串，对 IntStream/LongStream/DoubleStream 添加 .boxed()；(3) 将 ExpressionTransformerHelpers.IsPrimitiveSpecialTypeForArrayStream 从 private 改为 public。
- **状态**: ✅ Fixed

## Iteration 9-11 - Fixed constructor accessibility, SByteConverter type token, and Activator Class arg truncation

## Iteration 12 — sbyte 属性被路由到 ByteConverter（Byte→CSharpByte 令牌反向映射错误）
- **Java 文件**: system-text-json-tests/src/test/java/dotnet/text/json/Serialization/Tests/ArrayTests.java
- **行号**: 795
- **错误信息**: `java.lang.ClassCastException: class java.lang.Byte cannot be cast to class java.lang.Integer`（at ByteConverter.write(ByteConverter.java:31)）
- **代码片段**:
  ```java
  // ByteConverter.java (generated)
  public final class ByteConverter extends JsonConverter1<Integer> {
      public ByteConverter() { super(io.github.ningpp.compat.CSharpByte.class); ... }
      public void write(Utf8JsonWriter writer, Integer value, JsonSerializerOptions options) {
          writer.writeNumberValue((int) (value));   // ← line 31: value 实际是 java.lang.Byte → CCE
      }
  }
  ```
  复现：`JsonSerializer.serialize(SimpleTestClass.class, obj)` 即可触发（MySByte 属性）。
- **对应 C# 文件**: D:\csharp-sdk\json\tests\Serialization\TestClasses\TestClasses.SimpleTestClass.cs（`public sbyte MySByte { get; set; }`）
- **根因分类**: 语义丢失（compat 层类型令牌反向映射）
- **涉及组件**: java/csharptojava-compat/src/main/java/io/github/ningpp/compat/TypeHelper.java（denormalizeToCSharpTypeToken，63df7e0e 引入）
- **分析**: 转换器类型映射为 `C# byte → Java int/Integer`、`C# sbyte → Java byte/Byte`（TypeMappings.json: System.Byte→int, System.SByte→byte）。运行时 compat 反射对 sbyte 属性（Java 字段 byte）返回 `Byte.class`，转换器查找 `getConverter(Byte.class)` 时 CSharpDictionary 走 denormalizeToCSharpTypeToken 回退，把 `Byte.class` 映射到 `CSharpByte.class`，命中 ByteConverter（JsonConverter1<Integer>），而实际值是装箱 `java.lang.Byte` → `(int) value` 拆箱 CCE。正确语义：Java Byte 只可能代表 C# sbyte（C# byte 已拓宽为 int），故 `denormalizeToCSharpTypeToken(Byte.class)` 应返回 `CSharpSByte.class`（SByteConverter 是 JsonConverter1<Byte>，可正确处理 Byte 值）。生成代码中 `typeof(sbyte)`/`typeof(byte)` 均直接使用 CSharpSByte/CSharpByte 令牌（字典直查命中），不受影响。
- **状态**: 🔧 Fixing

## Iteration 13 — 默认 BindingFlags=0 的反射查找恒为空（getConstructor 返回 null → JsonClassInfo NPE）
- **Java 文件**: system-text-json/src/main/java/dotnet/text/json/Serialization/Converters/KeyValuePairConverter.java（触发点）；根因在 compat TypeHelper
- **行号**: KeyValuePairConverter.java:54（s_constructorInfo = null）
- **错误信息**: `java.lang.NullPointerException: Cannot invoke "io.github.ningpp.compat.ConstructorInfo.getParameters()" because "constructorInfo" is null`（at JsonClassInfo.initializeConstructorParameters(JsonClassInfo.java:305)）
- **代码片段**:
  ```java
  // KeyValuePairConverter.java (generated)
  this.s_constructorInfo = TypeHelper.getConstructor(CSharpKeyValuePair.class,
      new Class[] { TypeHelper.toWrapperType(tKeyClass), TypeHelper.toWrapperType(tValueClass) });
  // initialize() 里 setConstructorInfo(s_constructorInfo) → null
  // → JsonClassInfo.<init>: converter.getConstructorIsParameterized()==true（继承自
  //   SmallObjectWithParameterizedConstructorConverter）但 getConstructorInfo()==null → NPE
  ```
  复现：`JsonSerializer.serialize(SimpleTestClass.class, obj)`（迭代 12 修复后暴露）。
- **对应 C# 文件**: json/src/System/Text/Json/Serialization/Converters/Object/KeyValuePairConverter.cs（`ConstructorInfo = s_constructorInfo;` + Debug.Assert 非空）
- **根因分类**: 语义丢失（compat 反射默认 BindingFlags 语义）
- **涉及组件**: java/csharptojava-compat/src/main/java/io/github/ningpp/compat/TypeHelper.java（matchesBindingFlags / getConstructor / getMember / getMembers）
- **分析**: C# `Type.GetConstructor(Type[])` 默认只查 public 实例构造函数（等价 BindingFlags.Public|BindingFlags.Instance）。compat 的 `getConstructor(type, params...)` 委托 `getConstructorInfo(type, 0, ...)`，而 `matchesBindingFlags(mods, 0)` 中 wantPublic/wantNonPublic 均为 false，可见性条件 `!((wantPublic&&isPublic)||(wantNonPublic&&!isPublic)||fallback)` 恒 false → 任何构造函数都不匹配 → 永远返回 null。受影响调用点：KeyValuePairConverter（本 NPE）、JsonSerializerOptions.getConverterFromAttribute:375（[JsonConverter] 特性处理被静默破坏）、RegexCompileToAssemblyTests。同样默认 flags=0 的 getMember(type,name)/getMembers(type,name) 也恒空。修复：无 flags 重载按 C# 语义默认 Public|Instance（getMember/getMembers 额外含 Static）。
- **状态**: 🔧 Fixing

## Iteration 14 — IDictionary 约束类型参数的 GetEnumerator 走了 fromAny 通用分支（CSharpDictEnumerator CCE）
- **Java 文件**: system-text-json/src/main/java/dotnet/text/json/Serialization/Converters/IDictionaryConverter.java
- **行号**: 78
- **错误信息**: `java.lang.ClassCastException: class io.github.ningpp.compat.CSharpGenericEnumerator$IteratorBackedCSharpEnumerator cannot be cast to class io.github.ningpp.compat.CSharpDictEnumerator`
- **代码片段**:
  ```java
  // IDictionaryConverter.onWriteResume (generated)
  CSharpDictEnumerator enumerator;
  if (state.value.Current.getCollectionEnumerator() == null) {
      enumerator = (CSharpDictEnumerator) CSharpGenericIterable.fromAny(value).iterator();  // ← line 78
  ```
  复现：`JsonSerializer.serialize(SimpleTestClass.class, obj)`（迭代 12/13 修复后暴露，写 MyStringToStringIDict 属性时）。
- **对应 C# 文件**: json/src/System/Text/Json/Serialization/Converters/Collection/IDictionaryConverter.cs（`enumerator = value.GetEnumerator();`，value 为 `TCollection : IDictionary` 类型参数，目标类型 IDictionaryEnumerator）
- **根因分类**: Transformer
- **涉及组件**: src/CSharpToJava.Core/Transformers/Expression/Transformers/InvocationExpressionTransformer.cs（GetEnumerator 降级逻辑，第 1899-1914 行 fromAny 分支）
- **分析**: C# 源中 `value.GetEnumerator()` 的 receiver 是约束为 `IDictionary` 的类型参数 TCollection。转换器 GetEnumerator 处理的第一分支检测"类型参数约束到 IEnumerable"（IDictionary 继承 IEnumerable 故命中）并只做 `CSharpGenericIterable.fromAny(receiver)` 包装；后续字典分支的 `IsDictionaryLikeExpression` 对类型参数返回 false（GetTypeInfo(...).Type 不是 INamedTypeSymbol），导致最终生成 `(CSharpDictEnumerator) fromAny(value).iterator()`，运行时实际是 IteratorBackedCSharpEnumerator → CCE。修复：fromAny 分支内先检测 `IsIDictionaryEnumeratorTarget`，命中则生成 `CSharpDictEnumerator.from(CSharpGenericIterable.fromAny(receiver).iterator())`（运行时迭代 CSharpDictionary 产出 CSharpKeyValuePair，from(CSharpGenericEnumerator) 适配器提供 getKey/getValue/getEntry）。
- **状态**: ✅ Fixed（转换器修复 + compat 库修复）

### 补充修复 — CSharpDictEnumerator.from 运行时类型不兼容
- **问题**: 修复后生成的 `CSharpDictEnumerator.from(CSharpGenericIterable.fromAny(value).iterator())` 在运行时仍然抛出 `ClassCastException: CSharpDictEntry cannot be cast to CSharpKeyValuePair`。
- **根因**: 当 `value` 是通过 `CSharpIDictionary.from(CSharpGenericIDictionary)` 桥接的非泛型字典时，其 `iterator()` 返回 `CSharpGenericEnumerator<Object>`，`getCurrent()` 返回 `CSharpDictEntry`（不是 `CSharpKeyValuePair`）。由于泛型擦除，`CSharpDictEnumerator.from(CSharpGenericEnumerator<? extends CSharpKeyValuePair<?, ?>>)` 的签名在编译期接受了这个参数，但运行时 checkcast 失败。
- **修复**: 修改 `CSharpDictEnumerator.from(CSharpGenericEnumerator)` 方法，不再假设 `getCurrent()` 一定返回 `CSharpKeyValuePair`，改为运行时检查 `CSharpDictEntry` 和 `Map.Entry` 两种类型（与 `from(CSharpEnumerator)` 重载相同的逻辑）。
- **涉及组件**: `java/csharptojava-compat/src/main/java/io/github/ningpp/compat/CSharpDictEnumerator.java`

## Iteration 15 — PropertyInfo.getProperties 未解析 setter（属性反序列化全跳过）
- **复现**: `JsonSerializer.deserialize(SimpleTestClass.class, "{\"MyInt32\":2}")` → MyInt32=0（默认值，无异常）
- **对应 C# 文件**: json/src/System/Text/Json/JsonPropertyInfo（ShouldDeserialize = HasSetter）
- **根因分类**: compat 层语义缺失
- **涉及组件**: java/csharptojava-compat/src/main/java/io/github/ningpp/compat/PropertyInfo.java
- **分析**: `PropertyInfo.getProperties` 只用 getter 构造 PropertyInfo（setter=null），JSON 序列化器的 `determineSerializationCapabilities` 以 `HasSetter` 决定 `ShouldDeserialize` → 全部 87 个属性 shouldDeserialize=false，读取时被静默跳过。ObjectTests/PropertyVisibilityTests/NumberHandlingTests 等属性反序列化全线失败（写正常、读全默认值）。
- **修复**: getProperties 为每个 getter 按命名约定（getFoo/isBar → setFoo/setBar）解析匹配 setter（装箱类型兼容），静态方法排除（C# Instance 语义）。**该单点修复解锁了全部属性反序列化**。
- **状态**: ✅ Fixed

## Iteration 16 — immutable 集合类型身份不可见（用户报告的 IImmutableSet JsonException）
- **Java 文件**: system-text-json-tests/src/test/java/dotnet/text/json/Serialization/Tests/CollectionTests.java
- **错误信息**: `dotnet.text.json.JsonException: The JSON value could not be converted to interface io.github.ningpp.compat.IImmutableSet. Path: $ | LineNumber: 0 | BytePositionInLine: 1.`
- **对应 C# 文件**: json/tests/Serialization/CollectionTests/CollectionTests.Immutable.Read.cs（ReadIImmutableSetTOfIImmutableSetT）
- **根因分类**: compat 层类型系统缺失（C# 类型身份）
- **涉及组件**: java/csharptojava-compat/src/main/java/io/github/ningpp/compat/{CompatTypeIdentity(新增),TypeHelper,AssemblyCompat}.java
- **分析**: `IEnumerableConverterFactory.canConvert` 以 `CSharpGenericIterable` 可赋值性判定集合类型；compat 的 immutable 类型（IImmutableSet/ImmutableList/ImmutableHashSet 等 13 个）不在该继承树下（继承 java.lang.Iterable），且 `isImmutableEnumerableType` 按 C# 全名（"System.Collections.Immutable.ImmutableHashSet\`1"）+ 程序集名（"System.Collections.Immutable,"）判定，compat 类两者皆不匹配 → factory 拒收 → 落到 ObjectDefaultConverter → 对 `[` 开头的 JSON 抛 JsonException。
- **修复**: (1) 新增 CompatTypeIdentity 注册表：compat 类 ↔ C# 全名（含 arity）↔ 程序集全名双向映射；(2) `TypeHelper.getFullName`/`AssemblyCompat.fromClass`/`getClass` 查注册表；(3) `TypeHelper.isAssignableFrom` 桥接"实现了 immutable 接口的类"（含生成的测试 wrapper 类）可赋值给 CSharpGenericIterable；(4) immutable 类添加 C# 命名的静态泛型 `CreateRange` 工厂（生成的 factory 按 C# 方法名反射查找）。修复后 converter 解析为 ImmutableEnumerableOfTConverter，immutable 写入端到端通过；读取端进一步受限于 erased element type（见 Iteration 20）与 override 泛型 typeof(T) null 降级（createImmutableEnumerableCreateRangeDelegate 传入 null → 已做 compat 防御,干净失败为 NotSupportedException）。
- **状态**: ✅ Fixed（converter 解析层；读取端元素类型见 Iteration 20 待办）

## Iteration 17 — 枚举读取 (T)(Object) checkcast CCE（Integer→Enum）
- **错误信息**: `java.lang.ClassCastException: class java.lang.Integer cannot be cast to class java.lang.Enum`（EnumConverter.read）
- **对应 C# 文件**: json/src/System/Text/Json/Serialization/Converters/Value/EnumConverter.cs（`Unsafe.As<int, T>(ref int32)`）
- **根因分类**: Transformer
- **涉及组件**: src/CSharpToJava.Core/Transformers/Expression/Transformers/InvocationExpressionTransformer.cs（Unsafe.As 处理）
- **分析**: `Unsafe.As<int, T>`（`T : struct, Enum`）生成为 `(T)(Object)(Integer)(Unsafe.as(int32))`——javac 对 `(T)` 插入 checkcast 到 T 的擦除边界 java.lang.Enum，对装箱 Integer 必然 CCE。改为 `EnumHelper.toObject(tClass, ...)`（值语义转换：经生成枚举的 fromValue/_UNMAPPED 机制支持未定义值，与 C# reinterpret 语义等价）；对无约束/类约束 T 分别保留装箱链/Unsafe.as(Object)（擦除为 Object,无 checkcast）。
- **状态**: ✅ Fixed（含回归测试 UnsafeAsEnumTypeParameterTests）

## Iteration 18 — Utf8Parser 无符号解析路由缺失（ulong/uint Get 全线 FormatException）
- **错误信息**: `io.github.ningpp.compat.FormatException: Either the JSON value is not in a supported format, or is out of bounds for a UInt32.`（Utf8JsonReader.getUInt32）
- **对应 C# 文件**: json/src/System/Text/Json/Reader/Utf8JsonReader.TryGet.cs（TryGetUInt32Core: `Utf8Parser.TryParse(span, out uint, ...)`）
- **根因分类**: Transformer + compat
- **涉及组件**: InvocationExpressionTransformer.cs（Utf8Parser.TryParse 路由规则）、compat Utf8Parser.java（tryParseUInt32）
- **分析**: out-arg 降级为 IntHolder/LongHolder 后,`TryParse(out uint/ulong)` 都路由到有符号 tryParse——`18446744073709551615`、`4294967295` 解析失败。扩展既有路由规则：uint→tryParseUInt32、ulong→tryParseUInt64；compat tryParseUInt32 修正为无符号域 [0, 2^32-1]。
- **状态**: ✅ Fixed

## Iteration 19 — 无符号数格式化缺失（uint/ulong/ushort/byte 写出有符号十进制）
- **错误信息**: 312 个 JsonReaderException（testingNumbers_GetMethods 等）：JSON 数据被写成 `{"uint0": -1,...}`（应为 4294967295）后读取失败；另有 Assert.equal(4294967295, -1) 类断言失败
- **对应 C# 文件**: json/tests/Tools/JsonNumberTestData.cs（`builder.Append(UInts[i])`，C# uint）
- **根因分类**: Transformer
- **涉及组件**: InvocationExpressionTransformer.cs（StringBuilder.Append/String.Concat/BuildPrimitiveToString）、StringExpressionTransformer.cs（插值洞/String.format 参数）
- **分析**: C# `uint/ulong/ushort/byte` 的字符串化输出无符号十进制；Java 映射复用 int/long 位模式（uint.MaxValue→Java int -1），StringBuilder.Append(int)/String.valueOf 打印有符号形式。修复：Append/Concat/ToString()/插值/格式化参数按 C# 类型以宽度掩码包装（byte→&0xFF、ushort→&0xFFFF、uint→&0xFFFFFFFFL、ulong→Long.toUnsignedString——其无符号域超出 long.MaxValue）。
- **状态**: ✅ Fixed（含回归测试 UnsignedNumberFormattingTests）

## Iteration 20 — 构造参数名丢失导致 ctor 绑定失败（-parameters）
- **错误信息**: `IllegalStateException: Each parameter in constructor 'ConstructorInfo' on type 'CSharpKeyValuePair' must bind to an object property or field on deserialization.`
- **对应 C# 文件**: json/src/System/Text/Json/Serialization/Converters/Object/KeyValuePairConverter.cs
- **根因分类**: 构建（pom 生成）
- **涉及组件**: src/CSharpToJava.Core/Pipeline/Planning/MavenPomGenerator.cs、java/csharptojava-compat/pom.xml
- **分析**: JSON 序列化器按（大小写不敏感）构造参数名绑定属性；Java 反射参数名需 `-parameters` 编译标志，否则为 arg0/arg1 → 与 Key/Value 不匹配 → KeyValuePairConverter 等参数化构造反序列化全部抛 IllegalStateException。
- **修复**: MavenPomGenerator 与 compat pom 的 maven-compiler-plugin 增加 `<parameters>true</parameters>`（javap 验证 MethodParameters: key, value）。
- **状态**: ✅ Fixed

## Iteration 21（待办）— 顶层 Deserialize<ClosedGeneric> 擦除元素类型
- **错误信息**: `java.lang.ClassCastException: class dotnet.text.json.JsonElement cannot be cast to class [I / io.github.ningpp.compat.CSharpISet / ...`（CollectionTests 全部 119 个 read* 测试）
- **对应 C# 文件**: json/tests/Serialization/CollectionTests/*.cs（`Deserialize<List<int[]>>` / `Deserialize<IImmutableSet<IImmutableSet<int>>>` 等顶层闭合泛型调用）
- **根因分类**: 架构（运行时类型模型）
- **涉及组件**: InvocationExpressionTransformer.cs（JsonSerializer.Deserialize 调用点）、生成的 ReadStack/JsonClassInfo（Class 键控转换器缓存）
- **分析**: `Deserialize<X<A,B>>(json)` 转换为 `deserialize(X.class, json)`——Java `.class` 字面量不可携带泛型实参,元素/键值类型在运行时不可恢复,converter factory 退化为 Object 元素（JsonElement）。写端按实例运行时类型多态解析故正常；读端（含属性元数据路径——JsonPropertyInfo 也是擦除 Class）均退化。同一裸类不同实参（IImmutableSet<int> vs IImmutableSet<IImmutableSet<int>>）被 Class 键控 `_converters` 缓存冲突,故不能用线程局部/注册表方案。
- **设计方向**: (1) 转换器后处理为每个闭合泛型类型生成"closed-type 载体类"（`class _Closed_X_Of_A extends X<A>`——Java 类声明保留泛型签名,getGenericSuperclass 可恢复实参;接口型需生成委托适配器）；(2) TypeHelper.getGenericArguments/getPropertyType 按泛型超类签名解析闭合实参；(3) Deserialize 调用点发射 closed 类字面量。属性路径经 PropertyInfo.getPropertyType() 映射闭合类后同机制复用。
- **状态**: 📋 设计中（本轮未实施——影响面: CollectionTests read 全家 + 属性泛型集合读）
﻿﻿# SDK Conversion Error Analysis

## Iteration 1 — final 字段重复赋值（value class 隐式 final 降级失效）
- **Java 文件**: `system-private-uri/src/main/java/dotnet/system/Collections/Generic/ArrayBuilder.java`
- **行号**: 59
- **错误信息**: `/D:/cs-java-816-sdk/system-private-uri/src/main/java/dotnet/system/Collections/Generic/ArrayBuilder.java:[59,9] 可能已分配变量_array`
- **代码片段**:
  ```java
  public ArrayBuilder(int capacity, Class<?> tClass) {
          this.tClass = tClass;
          this._array = null;
          this._count = 0;
          // Debug.Assert(capacity >= 0);
          if (capacity > 0) {
          _array = (T[]) TypeHelper.newArrayInstance(tClass, capacity);
          }
      }
  ```
- **对应 C# 文件**: `D:\csharp-sdk\uri\src\Common\System\Collections\Generic\ArrayBuilder.cs`
- **根因分类**: Transformer
- **涉及组件**: `src/CSharpToJava.Core/Transformers/Type/StructTransformer.cs`（`FixReadonlyStructConstructorChaining` / `DropFinalFromRepeatedlyAssignedFields`）
- **分析**: readonly struct 在转成 value class 时，`ArrayBuilder(int capacity) : this()` 的 `this()` 链被 `AddRuntimeClassConstructorParameters` 改写成 `this(tClass)`，随后被 `FixReadonlyStructConstructorChaining` 内联，把默认构造函数中的 `this._array = null` 拼进参数化构造函数，接着 `if` 内再次给 `_array` 赋值。普通类场景下 `DropFinalFromRepeatedlyAssignedFields` 会把 `_array` 降为非 final 绕过该错误；但 value class 的实例字段隐式 final，降级后仍然非法。同类问题还包括：final 字段只在 `if` 内被赋值（无顶层默认赋值）时，javac 报“可能尚未初始化变量”。
- **状态**: ✅ Fixed

## Iteration 2 — `First()/Last()` 返回数组后误加 `.boxed()`
- **错误信息**: 回归测试 `FirstElementContainsOnArrayTests.cs` 失败：`result.First().Contains(1)` 生成 `...findFirst().orElseThrow().boxed()...`，`orElseThrow()` 已返回 `int[]`，`int[].boxed()` 非法。
- **根因分类**: Transformer
- **涉及组件**: `src/CSharpToJava.Core/Transformers/Expression/Transformers/InvocationExpressionTransformer.cs`
- **分析**: `Contains` 处理用 `ContainsStreamMethodAtTopLevel` 判断 Java 接收表达式是否已是流；`...findFirst().orElseThrow()` 含 `.stream()` 被误判为流，但该链终态是数组元素，导致追加非法 `.boxed()`。
- **修复**: C# 接收表达式为返回数组的方法调用（`First()`/`Last()`/`ToArray()` 等）时，统一用 `Arrays.stream(receiver).boxed().collect(Collectors.toSet()).contains(...)`；直接 `int[].Contains(...)` 的双重包装保护保持不变。
- **状态**: ✅ Fixed

## 最终复验（2026-08-16 第三次）

- 使用同一份转换器代码重新转换 `D:\csharp-sdk` → `d:\cs-java-816-sdk`
- `mvn clean install -e` → **BUILD SUCCESS**（7102 个测试，0 失败）
- 同一份代码同时验证 AGL：`mvn clean package -e` → **BUILD SUCCESS**（609 个测试，0 失败 0 错误；`CSharpGenericIterable.from(null)` 修复后 Rectilinear 10 个 NPE 全部消失）
- compat：`mvn test` → **1500 个测试全部通过，0 失败**

## Iteration 3 — 局部函数捕获变量 IsWritten 传递闭包未提升
- **Java 文件**: `dotnet-text-regularexpressions/src/main/java/dotnet/text/regularexpressions/RegexCompiler.java`
- **行号**: 2084（首批错误之一，同模式共 14+ 处）
- **错误信息**: `不兼容的类型: io.github.ningpp.compat.Label无法转换为io.github.ningpp.compat.ObjectHolder<io.github.ningpp.compat.Label>`（同模式还有 `int无法转换为io.github.ningpp.compat.IntHolder`）
- **代码片段**:
  ```java
  // emitNode 参数 _cs2j_cap_doneLabel 类型为 Label（只读）
  // 但 emitSingleCharAtomicLoop 参数 _cs2j_cap_doneLabel 类型为 ObjectHolder<Label>（写入）
  // emitNode 内调用 emitSingleCharAtomicLoop 时传入 Label → 类型不兼容
  private void emitNode(RegexNode node, Label _cs2j_cap_doneLabel, ...) {
      case RegexNode.Oneloopatomic, ...:
      emitSingleCharAtomicLoop(node, _cs2j_cap_doneLabel, ...); // ← Label 传给 ObjectHolder<Label>
  }
  ```
- **对应 C# 文件**: `D:\csharp-sdk\regular-expressions\src\System\Text\RegularExpressions\RegexCompiler.cs`
- **根因分类**: Transformer
- **涉及组件**: `src/CSharpToJava.Core/Transformers/Statement/StatementTransformer.LocalFunctions.cs`（`PrescanLocalFunctions` 传递闭包）
- **分析**: C# 局部函数 `EmitNode` 只读取捕获变量 `doneLabel`（`IsWritten=false`），但它调用的 `EmitSingleCharAtomicLoop` 写入 `doneLabel`（`IsWritten=true`）。`PrescanLocalFunctions` 的 fixpoint 传播 callee 的 captures 给 caller 时，如果 caller 已有同名 capture（`IsWritten=false`），直接跳过不更新——导致 caller 的参数类型保持为 `Label` 而非 `ObjectHolder<Label>`，调用 callee 时传 `Label` 给期望 `ObjectHolder<Label>` 的参数。

## Iteration 4 — `new T[n]` 泛型数组创建误用 `T.class`
- **Java 文件**: `system-private-uri/src/main/java/dotnet/system/Collections/Generic/ArrayBuilder.java`
- **行号**: 59（同模式共 3 处：59、131、168）
- **错误信息**: `无法从类型变量中进行选择`（cannot select from a type variable）
- **代码片段**:
  ```java
  // 行 59
  _array = CSharpArray.of(java.lang.reflect.Array.newInstance(TypeHelper.toWrapperType(tClass), capacity)).toArray(T.class);
  // 行 131
  result = CSharpArray.of(java.lang.reflect.Array.newInstance(TypeHelper.toWrapperType(tClass), _count)).toArray(T.class);
  // 行 168
  T[] next = CSharpArray.of(java.lang.reflect.Array.newInstance(TypeHelper.toWrapperType(tClass), nextCapacity)).toArray(T.class);
  ```
- **对应 C# 文件**: `D:\csharp-sdk\uri\src\Common\System\Collections\Generic\ArrayBuilder.cs`
- **根因分类**: Transformer
- **涉及组件**: `src/CSharpToJava.Core/GenericPreprocess/GenericPreprocessor.cs`（`VisitArrayCreationExpression`：`new T[n]` → `(T[])Array.CreateInstance(typeof(T), n)`）、`src/CSharpToJava.Core/Transformers/Expression/Transformers/InvocationExpressionTransformer.cs`（`Array.CreateInstance` 处理）、`src/CSharpToJava.Core/Transformers/Expression/Transformers/TypeOperationTransformer.cs`（`(T[])` cast → `.toArray(T.class)`）
- **分析**: GenericPreprocessor 将 `new T[n]` 重写为 `(T[])Array.CreateInstance(typeof(T), n)`。然后 `InvocationExpressionTransformer` 将 `Array.CreateInstance(typeof(T), n)` 转成 `CSharpArray.of(java.lang.reflect.Array.newInstance(TypeHelper.toWrapperType(tClass), n))`，外部的 `(T[])` cast 被 `TypeOperationTransformer` 转成 `.toArray(T.class)`，但 `T.class` 在 Java 中对类型变量是非法的。修复方案：(1) `InvocationExpressionTransformer` 中检测 `Array.CreateInstance(typeof(T), n)` 当 T 有 runtime class parameter 时直接返回 `TypeHelper.newArrayInstance(tClass, n)`；(2) `TypeOperationTransformer` 中当目标数组元素类型是类型参数时直接用 `(T[])` cast 代替 `.toArray(T.class)`。
- **状态**: ✅ Fixed

## Iteration 5 — 方法级类型参数的 `Array.CreateInstance(typeof(T), n)` 未触发特殊情况
- **Java 文件**: `system-private-xml/src/main/java/dotnet/xml/schema/XmlUntypedStringConverter.java`
- **行号**: 311
- **错误信息**: `java.lang.ClassCastException: class io.github.ningpp.compat.CSharpArray cannot be cast to class [Ljava.lang.Object;`（运行时，编译无错）
- **代码片段**:
  ```java
  private <T> T[] toArray(Class<T> _cs2j_T, String[] stringArray, ...) {
      try {
          T[] arrDst = (T[]) (Object) (CSharpArray.of(java.lang.reflect.Array.newInstance(TypeHelper.toWrapperType(_cs2j_T), stringArray.length)));
  ```
- **对应 C# 文件**: `D:\csharp-sdk\xml\src\System\Xml\Schema\XmlUntypedStringConverter.cs`（`ToArray<T>` 方法）
- **根因分类**: Transformer
- **涉及组件**: `src/CSharpToJava.Core/Transformers/Expression/Transformers/InvocationExpressionTransformer.cs`（`Array.CreateInstance` 特殊处理路径）、`src/CSharpToJava.Core/Transformers/Expression/Transformers/TypeOperationTransformer.cs`（`(T[])` cast 处理）
- **分析**: GenericPreprocessor 将 `new T[n]` 重写为 `(T[])Array.CreateInstance(typeof(T), n)`。`InvocationExpressionTransformer` 的特殊情况检查 `TryGetRuntimeClassParameter(typeParam.Name, ...)`，但该方法级类型参数 T 的 runtime class parameter (`_cs2j_T`) 是在方法体转换**之后**由 `AddPendingRuntimeClassParameters` 注册的。转换方法体时 `TryGetRuntimeClassParameter` 返回 false，特殊情况不触发，走了通用 fallback 路径 `CSharpArray.of(...)`。同样，`TypeOperationTransformer` 中 `(T[])` cast 也因 `TryGetRuntimeClassParameter` 返回 false 走了 `(T[]) (Object)` 路径。结果：`CSharpArray` 对象无法 cast 为 `Object[]`，运行时 `ClassCastException`。
- **状态**: ✅ Fixed

## Iteration 6 — `Activator.CreateInstance<T>()` 生成无参数调用 & `system-private-uri` 依赖缺失
- **Java 文件**: `system-text-json/src/main/java/dotnet/text/json/Serialization/ReferenceHandler1.java`
- **行号**: 29
- **错误信息**: `无法将类 io.github.ningpp.compat.Activator中的方法 createInstance应用到给定类型; 需要: java.lang.Class<T> 找到: 没有参数`（同时有 `Uri`/`UriKind` 符号未找到错误，根因是 `pom.xml` 缺少 `system-private-uri` 依赖）
- **代码片段**:
  ```java
  public ReferenceResolver createResolver() { return Activator.createInstance(); }
  ```
- **对应 C# 文件**: `D:\csharp-sdk\json\src\System\Text\Json\Serialization\ReferenceHandlerOfT.cs`（`new T()` → `Activator.CreateInstance<T>()`）
- **根因分类**: Transformer
- **涉及组件**: `src/CSharpToJava.Core/GenericPreprocess/GenericPreprocessor.cs`（`new T()` → `Activator.CreateInstance<T>()`）、`src/CSharpToJava.Core/Transformers/Expression/Transformers/InvocationExpressionTransformer.cs`（`Activator.CreateInstance<T>()` 泛型调用处理）、`src/CSharpToJava.Core/Transformers/Utilities/RuntimeClassParameterHelper.cs`（`AddObjectCreationRequirements` 仅扫描 `BaseObjectCreationExpressionSyntax`，不扫描重写后的 `Activator.CreateInstance<T>()`）
- **分析**: GenericPreprocessor 将 `new T()` 重写为 `Activator.CreateInstance<T>()`。转换器未检测此泛型调用的类型参数 T 需要运行时 Class 参数，因为：(1) `AddObjectCreationRequirements` 仅扫描 `BaseObjectCreationExpressionSyntax`，重写后已变为 `InvocationExpressionSyntax`；(2) `InvocationExpressionTransformer` 的 `Activator.CreateInstance` 处理仅覆盖非泛型形式（1 个参数），不覆盖 `CreateInstance<T>()`（0 个参数）。同时 `system-text-json` 的 `pom.xml` 缺少对 `system-private-uri` 模块的依赖，导致 `Uri`/`UriKind` 符号找不到。
- **修复**: (1) 在 `InvocationExpressionTransformer.cs` 中添加 `Activator.CreateInstance<T>()` 泛型调用处理，检测类型参数 T 并生成 `Activator.createInstance(_cs2j_T)` 或 `Activator.createInstance(tClass)`。(2) 在 `Program.cs` 中添加 `DiscoverImplicitModuleDependencies` 后处理步骤：在所有 Java 源码生成完毕后，扫描各模块 `.java` 文件的 `package` 声明和 `import` 语句，构建 包名→模块名 映射，自动发现 C# 项目引用未覆盖的隐式模块间依赖（如框架类型 `System.Uri` 映射到 `system-private-uri` 模块），并重新生成受影响模块的 `pom.xml`。
- **状态**: ✅ Fixed

## Iteration 7 — EnumValue.ToString() 被误识别为 [Flags] 枚举
- **Java 文件**: system-text-json-tests/src/test/java/dotnet/text/json/Serialization/Tests/PropertyVisibilityTests.java`n- **行号**: 2606, 2648
- **错误信息**: 无法将类 java.lang.Enum<E>中的方法 toString应用到给定类型; 需要: 没有参数 找到: dotnet.text.json.Serialization.Tests.PropertyVisibilityTests.MyEnum`n- **代码片段**:
  `java
  public String getEnumString() { return PropertyVisibilityTests.MyEnum.toString(getEnumValue()); }
  ``n- **对应 C# 文件**: D:\csharp-sdk\json\tests\Serialization\PropertyVisibilityTests.cs（第 1413 行 get => EnumValue.ToString();）
- **根因分类**: Transformer
- **涉及组件**: src/CSharpToJava.Core/Transformers/Expression/Transformers/InvocationExpressionTransformer.cs（第 4727-4740 行 flags 枚举 fallback）
- **分析**: MyEnum（非 [Flags] 枚举，在 PropertyVisibilityTests 中）被误识别为 [Flags] 枚举。原因是 EnumConverterTests.cs 第 272 行有一个同名的 [Flags] public enum MyEnum。当 TypeMappingService.MapType 处理 EnumConverterTests.MyEnum 时，调用 RegisterFlagsEnum("MyEnum", ...) 注册了简单名。然后在 InvocationExpressionTransformer 的 flags 枚举 fallback 中，context.IsFlagsEnum(receiverSymbol.Name) 使用简单名 MyEnum 检查，匹配到了 EnumConverterTests.MyEnum 的注册。这导致非 [Flags] 的 PropertyVisibilityTests.MyEnum 被误处理为 [Flags] 枚举，调用 BuildFlagsEnumToString 生成了 PropertyVisibilityTests.MyEnum.toString(getEnumValue())（静态调用）。
- **修复**: 移除 fallback 中的简单名检查 context.IsFlagsEnum(receiverSymbol.Name)，只使用完全限定名 context.IsFlagsEnum(receiverSymbol.ToDisplayString()) 检查，避免同名但不同 [Flags] 属性的枚举误匹配。
- **状态**: ✅ Fixed


## Iteration 8 — Stream.concat 参数类型不匹配（byte[] Concat）
- **Java 文件**: system-text-json-tests/src/test/java/dotnet/text/json/Tests/JsonDocumentTests.java`n- **行号**: 282, 288, 294, 300
- **错误信息**: 无法将接口 java.util.stream.Stream<T>中的方法 concat应用到给定类型; 需要: Stream<? extends T>,Stream<? extends T> 找到: IntStream,Stream<Object>
- **代码片段**:
  `java
  Stream.concat(IntStream.range(0, Utf8Bom.length).map(i -> Utf8Bom[i]), IntStream.range(0, bytes.length).mapToObj(i -> bytes[i]))
  ``n- **对应 C# 文件**: D:\csharp-sdk\json\tests\JsonDocumentTests.cs（第 369 行 Utf8Bom.Concat(bytes).ToArray()）
- **根因分类**: Transformer
- **涉及组件**: src/CSharpToJava.Core/Transformers/Expression/Transformers/InvocationExpressionTransformer.cs（第 6405-6451 行 Concat 处理）、src/CSharpToJava.Core/Transformers/Expression/Utilities/ExpressionTransformerHelpers.cs（BuildArrayStreamExpression）
- **分析**: yte[] 数组通过 BuildArrayStreamExpression 转换为 IntStream.range(0, n).map(i -> arr[i])（IntStream），因为 Java 的 Arrays.stream() 不支持 yte[]。在 Concat 处理中，receiver 是 IntStream，参数也是 stream，但 Stream.concat 需要两个 Stream<? extends T> 参数，IntStream 不兼容。修复前的 .boxed() 检查只处理 int[]/long[]/double[]，不覆盖 yte[] 等。
- **修复**: (1) 在 Concat 处理中扩展 .boxed() 检查范围，覆盖所有原始类型数组（byte[]/short[]/char[]/float[]/boolean[]）；(2) 添加 ReceiverLooksLikePrimitiveStream 方法检测已转换为原始 stream 的 receiver 字符串，对 IntStream/LongStream/DoubleStream 添加 .boxed()；(3) 将 ExpressionTransformerHelpers.IsPrimitiveSpecialTypeForArrayStream 从 private 改为 public。
- **状态**: ✅ Fixed

## Iteration 9-11 - Fixed constructor accessibility, SByteConverter type token, and Activator Class arg truncation

## Iteration 12 — sbyte 属性被路由到 ByteConverter（Byte→CSharpByte 令牌反向映射错误）
- **Java 文件**: system-text-json-tests/src/test/java/dotnet/text/json/Serialization/Tests/ArrayTests.java
- **行号**: 795
- **错误信息**: `java.lang.ClassCastException: class java.lang.Byte cannot be cast to class java.lang.Integer`（at ByteConverter.write(ByteConverter.java:31)）
- **代码片段**:
  ```java
  // ByteConverter.java (generated)
  public final class ByteConverter extends JsonConverter1<Integer> {
      public ByteConverter() { super(io.github.ningpp.compat.CSharpByte.class); ... }
      public void write(Utf8JsonWriter writer, Integer value, JsonSerializerOptions options) {
          writer.writeNumberValue((int) (value));   // ← line 31: value 实际是 java.lang.Byte → CCE
      }
  }
  ```
  复现：`JsonSerializer.serialize(SimpleTestClass.class, obj)` 即可触发（MySByte 属性）。
- **对应 C# 文件**: D:\csharp-sdk\json\tests\Serialization\TestClasses\TestClasses.SimpleTestClass.cs（`public sbyte MySByte { get; set; }`）
- **根因分类**: 语义丢失（compat 层类型令牌反向映射）
- **涉及组件**: java/csharptojava-compat/src/main/java/io/github/ningpp/compat/TypeHelper.java（denormalizeToCSharpTypeToken，63df7e0e 引入）
- **分析**: 转换器类型映射为 `C# byte → Java int/Integer`、`C# sbyte → Java byte/Byte`（TypeMappings.json: System.Byte→int, System.SByte→byte）。运行时 compat 反射对 sbyte 属性（Java 字段 byte）返回 `Byte.class`，转换器查找 `getConverter(Byte.class)` 时 CSharpDictionary 走 denormalizeToCSharpTypeToken 回退，把 `Byte.class` 映射到 `CSharpByte.class`，命中 ByteConverter（JsonConverter1<Integer>），而实际值是装箱 `java.lang.Byte` → `(int) value` 拆箱 CCE。正确语义：Java Byte 只可能代表 C# sbyte（C# byte 已拓宽为 int），故 `denormalizeToCSharpTypeToken(Byte.class)` 应返回 `CSharpSByte.class`（SByteConverter 是 JsonConverter1<Byte>，可正确处理 Byte 值）。生成代码中 `typeof(sbyte)`/`typeof(byte)` 均直接使用 CSharpSByte/CSharpByte 令牌（字典直查命中），不受影响。
- **状态**: 🔧 Fixing

## Iteration 13 — 默认 BindingFlags=0 的反射查找恒为空（getConstructor 返回 null → JsonClassInfo NPE）
- **Java 文件**: system-text-json/src/main/java/dotnet/text/json/Serialization/Converters/KeyValuePairConverter.java（触发点）；根因在 compat TypeHelper
- **行号**: KeyValuePairConverter.java:54（s_constructorInfo = null）
- **错误信息**: `java.lang.NullPointerException: Cannot invoke "io.github.ningpp.compat.ConstructorInfo.getParameters()" because "constructorInfo" is null`（at JsonClassInfo.initializeConstructorParameters(JsonClassInfo.java:305)）
- **代码片段**:
  ```java
  // KeyValuePairConverter.java (generated)
  this.s_constructorInfo = TypeHelper.getConstructor(CSharpKeyValuePair.class,
      new Class[] { TypeHelper.toWrapperType(tKeyClass), TypeHelper.toWrapperType(tValueClass) });
  // initialize() 里 setConstructorInfo(s_constructorInfo) → null
  // → JsonClassInfo.<init>: converter.getConstructorIsParameterized()==true（继承自
  //   SmallObjectWithParameterizedConstructorConverter）但 getConstructorInfo()==null → NPE
  ```
  复现：`JsonSerializer.serialize(SimpleTestClass.class, obj)`（迭代 12 修复后暴露）。
- **对应 C# 文件**: json/src/System/Text/Json/Serialization/Converters/Object/KeyValuePairConverter.cs（`ConstructorInfo = s_constructorInfo;` + Debug.Assert 非空）
- **根因分类**: 语义丢失（compat 反射默认 BindingFlags 语义）
- **涉及组件**: java/csharptojava-compat/src/main/java/io/github/ningpp/compat/TypeHelper.java（matchesBindingFlags / getConstructor / getMember / getMembers）
- **分析**: C# `Type.GetConstructor(Type[])` 默认只查 public 实例构造函数（等价 BindingFlags.Public|BindingFlags.Instance）。compat 的 `getConstructor(type, params...)` 委托 `getConstructorInfo(type, 0, ...)`，而 `matchesBindingFlags(mods, 0)` 中 wantPublic/wantNonPublic 均为 false，可见性条件 `!((wantPublic&&isPublic)||(wantNonPublic&&!isPublic)||fallback)` 恒 false → 任何构造函数都不匹配 → 永远返回 null。受影响调用点：KeyValuePairConverter（本 NPE）、JsonSerializerOptions.getConverterFromAttribute:375（[JsonConverter] 特性处理被静默破坏）、RegexCompileToAssemblyTests。同样默认 flags=0 的 getMember(type,name)/getMembers(type,name) 也恒空。修复：无 flags 重载按 C# 语义默认 Public|Instance（getMember/getMembers 额外含 Static）。
- **状态**: 🔧 Fixing

## Iteration 14 — IDictionary 约束类型参数的 GetEnumerator 走了 fromAny 通用分支（CSharpDictEnumerator CCE）
- **Java 文件**: system-text-json/src/main/java/dotnet/text/json/Serialization/Converters/IDictionaryConverter.java
- **行号**: 78
- **错误信息**: `java.lang.ClassCastException: class io.github.ningpp.compat.CSharpGenericEnumerator$IteratorBackedCSharpEnumerator cannot be cast to class io.github.ningpp.compat.CSharpDictEnumerator`
- **代码片段**:
  ```java
  // IDictionaryConverter.onWriteResume (generated)
  CSharpDictEnumerator enumerator;
  if (state.value.Current.getCollectionEnumerator() == null) {
      enumerator = (CSharpDictEnumerator) CSharpGenericIterable.fromAny(value).iterator();  // ← line 78
  ```
  复现：`JsonSerializer.serialize(SimpleTestClass.class, obj)`（迭代 12/13 修复后暴露，写 MyStringToStringIDict 属性时）。
- **对应 C# 文件**: json/src/System/Text/Json/Serialization/Converters/Collection/IDictionaryConverter.cs（`enumerator = value.GetEnumerator();`，value 为 `TCollection : IDictionary` 类型参数，目标类型 IDictionaryEnumerator）
- **根因分类**: Transformer
- **涉及组件**: src/CSharpToJava.Core/Transformers/Expression/Transformers/InvocationExpressionTransformer.cs（GetEnumerator 降级逻辑，第 1899-1914 行 fromAny 分支）
- **分析**: C# 源中 `value.GetEnumerator()` 的 receiver 是约束为 `IDictionary` 的类型参数 TCollection。转换器 GetEnumerator 处理的第一分支检测"类型参数约束到 IEnumerable"（IDictionary 继承 IEnumerable 故命中）并只做 `CSharpGenericIterable.fromAny(receiver)` 包装；后续字典分支的 `IsDictionaryLikeExpression` 对类型参数返回 false（GetTypeInfo(...).Type 不是 INamedTypeSymbol），导致最终生成 `(CSharpDictEnumerator) fromAny(value).iterator()`，运行时实际是 IteratorBackedCSharpEnumerator → CCE。修复：fromAny 分支内先检测 `IsIDictionaryEnumeratorTarget`，命中则生成 `CSharpDictEnumerator.from(CSharpGenericIterable.fromAny(receiver).iterator())`（运行时迭代 CSharpDictionary 产出 CSharpKeyValuePair，from(CSharpGenericEnumerator) 适配器提供 getKey/getValue/getEntry）。
- **状态**: ✅ Fixed（转换器修复 + compat 库修复）

### 补充修复 — CSharpDictEnumerator.from 运行时类型不兼容
- **问题**: 修复后生成的 `CSharpDictEnumerator.from(CSharpGenericIterable.fromAny(value).iterator())` 在运行时仍然抛出 `ClassCastException: CSharpDictEntry cannot be cast to CSharpKeyValuePair`。
- **根因**: 当 `value` 是通过 `CSharpIDictionary.from(CSharpGenericIDictionary)` 桥接的非泛型字典时，其 `iterator()` 返回 `CSharpGenericEnumerator<Object>`，`getCurrent()` 返回 `CSharpDictEntry`（不是 `CSharpKeyValuePair`）。由于泛型擦除，`CSharpDictEnumerator.from(CSharpGenericEnumerator<? extends CSharpKeyValuePair<?, ?>>)` 的签名在编译期接受了这个参数，但运行时 checkcast 失败。
- **修复**: 修改 `CSharpDictEnumerator.from(CSharpGenericEnumerator)` 方法，不再假设 `getCurrent()` 一定返回 `CSharpKeyValuePair`，改为运行时检查 `CSharpDictEntry` 和 `Map.Entry` 两种类型（与 `from(CSharpEnumerator)` 重载相同的逻辑）。
- **涉及组件**: `java/csharptojava-compat/src/main/java/io/github/ningpp/compat/CSharpDictEnumerator.java`

## Iteration 15 — PropertyInfo.getProperties 未解析 setter（属性反序列化全跳过）
- **复现**: `JsonSerializer.deserialize(SimpleTestClass.class, "{\"MyInt32\":2}")` → MyInt32=0（默认值，无异常）
- **对应 C# 文件**: json/src/System/Text/Json/JsonPropertyInfo（ShouldDeserialize = HasSetter）
- **根因分类**: compat 层语义缺失
- **涉及组件**: java/csharptojava-compat/src/main/java/io/github/ningpp/compat/PropertyInfo.java
- **分析**: `PropertyInfo.getProperties` 只用 getter 构造 PropertyInfo（setter=null），JSON 序列化器的 `determineSerializationCapabilities` 以 `HasSetter` 决定 `ShouldDeserialize` → 全部 87 个属性 shouldDeserialize=false，读取时被静默跳过。ObjectTests/PropertyVisibilityTests/NumberHandlingTests 等属性反序列化全线失败（写正常、读全默认值）。
- **修复**: getProperties 为每个 getter 按命名约定（getFoo/isBar → setFoo/setBar）解析匹配 setter（装箱类型兼容），静态方法排除（C# Instance 语义）。**该单点修复解锁了全部属性反序列化**。
- **状态**: ✅ Fixed

## Iteration 16 — immutable 集合类型身份不可见（用户报告的 IImmutableSet JsonException）
- **Java 文件**: system-text-json-tests/src/test/java/dotnet/text/json/Serialization/Tests/CollectionTests.java
- **错误信息**: `dotnet.text.json.JsonException: The JSON value could not be converted to interface io.github.ningpp.compat.IImmutableSet. Path: $ | LineNumber: 0 | BytePositionInLine: 1.`
- **对应 C# 文件**: json/tests/Serialization/CollectionTests/CollectionTests.Immutable.Read.cs（ReadIImmutableSetTOfIImmutableSetT）
- **根因分类**: compat 层类型系统缺失（C# 类型身份）
- **涉及组件**: java/csharptojava-compat/src/main/java/io/github/ningpp/compat/{CompatTypeIdentity(新增),TypeHelper,AssemblyCompat}.java
- **分析**: `IEnumerableConverterFactory.canConvert` 以 `CSharpGenericIterable` 可赋值性判定集合类型；compat 的 immutable 类型（IImmutableSet/ImmutableList/ImmutableHashSet 等 13 个）不在该继承树下（继承 java.lang.Iterable），且 `isImmutableEnumerableType` 按 C# 全名（"System.Collections.Immutable.ImmutableHashSet\`1"）+ 程序集名（"System.Collections.Immutable,"）判定，compat 类两者皆不匹配 → factory 拒收 → 落到 ObjectDefaultConverter → 对 `[` 开头的 JSON 抛 JsonException。
- **修复**: (1) 新增 CompatTypeIdentity 注册表：compat 类 ↔ C# 全名（含 arity）↔ 程序集全名双向映射；(2) `TypeHelper.getFullName`/`AssemblyCompat.fromClass`/`getClass` 查注册表；(3) `TypeHelper.isAssignableFrom` 桥接"实现了 immutable 接口的类"（含生成的测试 wrapper 类）可赋值给 CSharpGenericIterable；(4) immutable 类添加 C# 命名的静态泛型 `CreateRange` 工厂（生成的 factory 按 C# 方法名反射查找）。修复后 converter 解析为 ImmutableEnumerableOfTConverter，immutable 写入端到端通过；读取端进一步受限于 erased element type（见 Iteration 20）与 override 泛型 typeof(T) null 降级（createImmutableEnumerableCreateRangeDelegate 传入 null → 已做 compat 防御,干净失败为 NotSupportedException）。
- **状态**: ✅ Fixed（converter 解析层；读取端元素类型见 Iteration 20 待办）

## Iteration 17 — 枚举读取 (T)(Object) checkcast CCE（Integer→Enum）
- **错误信息**: `java.lang.ClassCastException: class java.lang.Integer cannot be cast to class java.lang.Enum`（EnumConverter.read）
- **对应 C# 文件**: json/src/System/Text/Json/Serialization/Converters/Value/EnumConverter.cs（`Unsafe.As<int, T>(ref int32)`）
- **根因分类**: Transformer
- **涉及组件**: src/CSharpToJava.Core/Transformers/Expression/Transformers/InvocationExpressionTransformer.cs（Unsafe.As 处理）
- **分析**: `Unsafe.As<int, T>`（`T : struct, Enum`）生成为 `(T)(Object)(Integer)(Unsafe.as(int32))`——javac 对 `(T)` 插入 checkcast 到 T 的擦除边界 java.lang.Enum，对装箱 Integer 必然 CCE。改为 `EnumHelper.toObject(tClass, ...)`（值语义转换：经生成枚举的 fromValue/_UNMAPPED 机制支持未定义值，与 C# reinterpret 语义等价）；对无约束/类约束 T 分别保留装箱链/Unsafe.as(Object)（擦除为 Object,无 checkcast）。
- **状态**: ✅ Fixed（含回归测试 UnsafeAsEnumTypeParameterTests）

## Iteration 18 — Utf8Parser 无符号解析路由缺失（ulong/uint Get 全线 FormatException）
- **错误信息**: `io.github.ningpp.compat.FormatException: Either the JSON value is not in a supported format, or is out of bounds for a UInt32.`（Utf8JsonReader.getUInt32）
- **对应 C# 文件**: json/src/System/Text/Json/Reader/Utf8JsonReader.TryGet.cs（TryGetUInt32Core: `Utf8Parser.TryParse(span, out uint, ...)`）
- **根因分类**: Transformer + compat
- **涉及组件**: InvocationExpressionTransformer.cs（Utf8Parser.TryParse 路由规则）、compat Utf8Parser.java（tryParseUInt32）
- **分析**: out-arg 降级为 IntHolder/LongHolder 后,`TryParse(out uint/ulong)` 都路由到有符号 tryParse——`18446744073709551615`、`4294967295` 解析失败。扩展既有路由规则：uint→tryParseUInt32、ulong→tryParseUInt64；compat tryParseUInt32 修正为无符号域 [0, 2^32-1]。
- **状态**: ✅ Fixed

## Iteration 19 — 无符号数格式化缺失（uint/ulong/ushort/byte 写出有符号十进制）
- **错误信息**: 312 个 JsonReaderException（testingNumbers_GetMethods 等）：JSON 数据被写成 `{"uint0": -1,...}`（应为 4294967295）后读取失败；另有 Assert.equal(4294967295, -1) 类断言失败
- **对应 C# 文件**: json/tests/Tools/JsonNumberTestData.cs（`builder.Append(UInts[i])`，C# uint）
- **根因分类**: Transformer
- **涉及组件**: InvocationExpressionTransformer.cs（StringBuilder.Append/String.Concat/BuildPrimitiveToString）、StringExpressionTransformer.cs（插值洞/String.format 参数）
- **分析**: C# `uint/ulong/ushort/byte` 的字符串化输出无符号十进制；Java 映射复用 int/long 位模式（uint.MaxValue→Java int -1），StringBuilder.Append(int)/String.valueOf 打印有符号形式。修复：Append/Concat/ToString()/插值/格式化参数按 C# 类型以宽度掩码包装（byte→&0xFF、ushort→&0xFFFF、uint→&0xFFFFFFFFL、ulong→Long.toUnsignedString——其无符号域超出 long.MaxValue）。
- **状态**: ✅ Fixed（含回归测试 UnsignedNumberFormattingTests）

## Iteration 20 — 构造参数名丢失导致 ctor 绑定失败（-parameters）
- **错误信息**: `IllegalStateException: Each parameter in constructor 'ConstructorInfo' on type 'CSharpKeyValuePair' must bind to an object property or field on deserialization.`
- **对应 C# 文件**: json/src/System/Text/Json/Serialization/Converters/Object/KeyValuePairConverter.cs
- **根因分类**: 构建（pom 生成）
- **涉及组件**: src/CSharpToJava.Core/Pipeline/Planning/MavenPomGenerator.cs、java/csharptojava-compat/pom.xml
- **分析**: JSON 序列化器按（大小写不敏感）构造参数名绑定属性；Java 反射参数名需 `-parameters` 编译标志，否则为 arg0/arg1 → 与 Key/Value 不匹配 → KeyValuePairConverter 等参数化构造反序列化全部抛 IllegalStateException。
- **修复**: MavenPomGenerator 与 compat pom 的 maven-compiler-plugin 增加 `<parameters>true</parameters>`（javap 验证 MethodParameters: key, value）。
- **状态**: ✅ Fixed

## Iteration 21（待办）— 顶层 Deserialize<ClosedGeneric> 擦除元素类型
- **错误信息**: `java.lang.ClassCastException: class dotnet.text.json.JsonElement cannot be cast to class [I / io.github.ningpp.compat.CSharpISet / ...`（CollectionTests 全部 119 个 read* 测试）
- **对应 C# 文件**: json/tests/Serialization/CollectionTests/*.cs（`Deserialize<List<int[]>>` / `Deserialize<IImmutableSet<IImmutableSet<int>>>` 等顶层闭合泛型调用）
- **根因分类**: 架构（运行时类型模型）
- **涉及组件**: InvocationExpressionTransformer.cs（JsonSerializer.Deserialize 调用点）、生成的 ReadStack/JsonClassInfo（Class 键控转换器缓存）
- **分析**: `Deserialize<X<A,B>>(json)` 转换为 `deserialize(X.class, json)`——Java `.class` 字面量不可携带泛型实参,元素/键值类型在运行时不可恢复,converter factory 退化为 Object 元素（JsonElement）。写端按实例运行时类型多态解析故正常；读端（含属性元数据路径——JsonPropertyInfo 也是擦除 Class）均退化。同一裸类不同实参（IImmutableSet<int> vs IImmutableSet<IImmutableSet<int>>）被 Class 键控 `_converters` 缓存冲突,故不能用线程局部/注册表方案。
- **设计方向**: (1) 转换器后处理为每个闭合泛型类型生成"closed-type 载体类"（`class _Closed_X_Of_A extends X<A>`——Java 类声明保留泛型签名,getGenericSuperclass 可恢复实参;接口型需生成委托适配器）；(2) TypeHelper.getGenericArguments/getPropertyType 按泛型超类签名解析闭合实参；(3) Deserialize 调用点发射 closed 类字面量。属性路径经 PropertyInfo.getPropertyType() 映射闭合类后同机制复用。
- **状态**: 📋 设计中（本轮未实施——影响面: CollectionTests read 全家 + 属性泛型集合读）

## Iteration 22 — ref 结构体参数 holder/raw 形状族不一致（字典键读取 dispatch 失效）
- **错误信息**: `java.lang.IllegalStateException`（空消息）×76，@ `JsonConverter1.readWithQuotes:508`；加 66 个"期望 JsonException 实得 IllegalStateException"、30 个"期望抛未抛"
- **对应 C# 文件**: json/src/System/Text/Json/Serialization/JsonConverterOfT.cs（`internal virtual T ReadWithQuotes(ref Utf8JsonReader reader) => throw new InvalidOperationException();`）+ Value/ 下 19 个转换器的 override
- **根因分类**: Transformer（虚方法族签名一致性）
- **涉及组件**: src/CSharpToJava.Core/Transformers/StructCloneHelper.cs（GetOverrideHierarchy）
- **分析**: `ref Utf8JsonReader` 参数的 holder/raw 优化按"单个方法体是否重新赋值"决定（IsReadOnlyRefStructParam）。CharConverter.ReadWithQuotes 把 reader 按 ref 转发给会推进 reader 的 Read（→Holder 形态），Guid/Int32/String 等 18 个只读（→裸 Utf8JsonReader 形态）——裸参数在 Java 里是"重载"而非"重写"，虚分派全部落到基类 `throw new IllegalStateException()`。GetOverrideHierarchy 原实现只在被分析方法为基类时才收集兄弟 override，分析派生 override 时看不到改变形状的兄弟，族内形状混用。
- **修复**: GetOverrideHierarchy 改为以"族根方法"为目标收集全部成员（所有 override 链到达同一根的方法），任一成员 mutation → 全族 Holder 形态。含回归测试 RefStructVirtualFamilyHolderTests。
- **状态**: ✅ Fixed
- **残余**: 修复后 CustomConverterTests/DictionaryTests 的字典键用例仍失败但异常形态转移（IllegalStateException 126→43，UnsupportedOperationException 59→138）——转移原因是这些用例依赖**闭合泛型类型匹配**：自定义工厂 canConvert 检查 `GetGenericArguments()[0]==typeof(Guid)`、键注册表按擦除后的 KeyType=Object 查找，均属 Iteration 21 擦除闭合泛型设计缺口，非 dispatch 问题。**字典键读取族与 JsonElement CCE 族同根，需 Iteration 21 一并解决。**

## Iteration 23 — goto 消除器产出不可编译的 C#（阶段树 .cs2j-no-goto-src 构建失败）
- **现象**: 在 `D:\cs-java-828-sdk\.cs2j-no-goto-src` 执行 `dotnet build/test -c Release` 失败,10 个唯一编译错误(20 处),涉及 4 个文件 4 类模式。
- **归因方法**: 二分构建各阶段树——`.cs2j-readonly-src`(上一阶段)构建 EXIT=0 零错误,`.cs2j-no-goto-src` 失败 → **引入者为 goto 消除器(eliminate-goto/GotoEliminator)**,非 unsigned/generic/make-readonly。
- **证据**: 全部错误点均携带消除器状态机标记(`__state`/`__cs2jBlockState4`/`__cs2jSwitchState0`);对应 readonly 阶段同区域仍是原始 for/loop 结构。
- **错误模式分类**(StateMachineBuilder 四类缺陷):
  1. **CS8353 stackalloc 逃逸**(Utf8JsonReader.MultiSegment.cs:643): 局部 `Span<byte> readSoFar` 被提升到 `while(true)` 状态机循环外,case 内 `readSoFar = stackalloc Byte[...]` → 赋值可逃逸迭代。修复方向:对提升的 Span/ReadOnlySpan 局部加 `scoped` 修饰,或将 stackalloc 赋值降级为 `new T[n]`。
  2. **CS0177 out 参数未赋值**(JsonWriterHelper.Transcoding.cs:490): 状态机某些退出路径未给 `bytesWritten`/`bytesConsumed` 赋值。修复方向:状态机序言处对 out 参数发 `= default`。
  3. **CS8172/CS1510/CS1073 非法 ref 局部初始化**(RegexInterpreter.cs:595): 提升的 ref 局部被发成为 `ref int[]? setLookup = default(ref int[]?);`——非法语法。修复方向:ref 局部提升时改发 `ref int[]? setLookup = ref <目标>` 或退化为按值局部 `int[]? setLookup = null`。
  4. **CS0163 case 贯穿**(IPv6AddressHelper.cs:224): 状态机 case 内嵌套 `switch(name[i])` 的 `case '%':` 缺少终止跳转。修复方向:StateMachineBuilder 为每个发射的 case 保证以控制转移语句结尾。
- **影响**: 阶段树 C# 侧无法 `dotnet test`(验证流水线正确性的手段失效);Java 侧虽可编译,但同源状态机语义存疑的四个位置(Utf8JsonReader 多段续读/转码写入器/RegexInterpreter/IPv6 解析)正是 Java 测试失败集中区(Utf8JsonReaderTests 485 个失败等),需在 Java 测试通过后回溯核对这四处语义。
- **状态**: ✅ Fixed(四类发射缺陷全部修复)
- **修复内容**:
  1. CS8353: 提升的 Span 局部的 stackalloc 赋值降级为堆分配 `new T[n]`(scoped 方案不可行——scoped 值不得流向非 scoped 的 span 参数,CS8350/8352 与 CS8353 约束互相冲突);
  2. CS0177: VisitMethodDeclaration(内联副本)/RewriteBaseMethod/RewriteAccessors 三条改写路径的"嵌套消除后提前返回"分支统一补 out 参数 default 初始化;
  3. CS8172/CS1510/CS1073: RefTypeSyntax 局部(ref 关键字属于类型节点而非修饰符)以 `Unsafe.NullRef<T>()` 初始化,T 取底层类型剥掉可空注解;ref var/readonly ref/函数指针形态抛异常跳过;
  4. CS0163: GotoCaseDesugarer 重设计——不再生成 per-switch 状态机,改为在目标 section 开头合成标签、`goto case X` 改写为普通标签 goto(旧的 `{state=N; break;}` 中 break 绑定到内层循环导致贯穿),由方法级状态机统一消除。
- **验证**:
  - C# 阶段树(.cs2j-no-goto-src): 构建 0 错误(修复前 10 个唯一错误),`dotnet test` 19262 个测试全部通过(0 失败);
  - Java: 829-verify 树 mvn 构建 + 测试,7903 测试 777F+881E=1658 失败(与修复前 1658 持平,无回归——C# 侧编译错误在 Java 侧本就以等价语义生成);
  - 转换器全量测试 3022/3026(仅 4 个存量失败);
  - 828 目标树已重新转换并同步(stage 树构建 0 错误)。

## Iteration 24 — byte 底层枚举算术未按底层类型截断（Utf8JsonReader 注释后误分发 StartArray）
- **错误信息**: 312 个 `dotnet.text.json.JsonReaderException`（Utf8JsonReaderTests 数据驱动用例）："'…' is invalid after a single JSON value. Expected end of data." 与 "'…' is an invalid start of a property name"
- **对应 C# 文件**: json/src/System/Text/Json/Reader/JsonReaderHelper.cs（`IsTokenTypePrimitive`）、Utf8JsonReader.cs（ConsumeNextTokenFromLastNonCommentToken）
- **根因分类**: Transformer（枚举算术底层类型语义丢失）
- **涉及组件**: src/CSharpToJava.Core/Transformers/Expression/Transformers/BinaryExpressionTransformer.cs（TryTransformEnumArithmeticOperation）
- **分析**: C# 枚举算术的结果类型是枚举的**底层类型**。JsonTokenType 底层为 byte：`(int)(JsonTokenType.None - JsonTokenType.String)` = unchecked `(byte)(0-7)` = **249**（不是 -7），故 `IsTokenTypePrimitive(None)` = `249 <= 4` = **false**。Java 转换版用 `ordinal()` 相减保持 int：`0-7=-7 <= 4` = **true** —— 语义翻转！后果：Allow 注释模式下 read 注释后的 token，`_previousTokenType=None` 被误判为 primitive → `_tokenType` 被设为 StartArray → 单值后遇多余数据抛 ExpectedEndAfterSingleJson（C# 中 None 非 primitive，正确恢复为 None 并走 ReadFirstToken 路径）。这正是 312 个 JsonReaderException 的根因。
- **修复**: TryTransformEnumArithmeticOperation 对 `+`/`-` 结果按底层类型加掩码（byte/sbyte→`& 0xFF`，ushort/short→`& 0xFFFF`）。含回归测试 ByteEnumArithmeticWrapTests（byte 枚举加掩码、int 枚举不加）。
- **验证**: Java 侧 7903 测试 1658→1487 失败（-171）；JsonReaderException 312→159（-153）。**剩余 159 个是另一类：多段续读（isFinalBlock=false）场景**（testPartialJsonReaderWithDepthPadding 等 68+ 个），报错位置在数据起始处（BytePositionInLine 1/14），属 MultiSegment 状态恢复问题，待下一轮。
- **重要发现**: .cs2j-no-goto-src 的 `dotnet test` 因 `BUILDING_INBOX_LIBRARY` 宏 + 框架内置 System.Text.Json 优先，**实际运行的是框架原版库而非转换版**（Assembly.Location 验证）——C# 侧"测试全绿"是假验证，只有 `dotnet build`（语法）与 Java 侧测试（语义）真实有效。分析框架原版行为可用 ilspycmd 反编译（`~/.dotnet/tools/ilspycmd -t <Type> <dll>`）。
- **状态**: ✅ Fixed（enum 截断；多段续读为 Iteration 25 待办）

## Iteration 25 — resx 资源未转 properties（SR 字符串全部 fallback，159 个假 JsonReaderException）
- **现象**: 上轮归因"多段续读"的 159 个 JsonReaderException 在本轮**全部消失**——真实根因是资源缺失。csharp-sdk 移除 BUILDING_INBOX_LIBRARY 后，测试引用本地转换库，SR.getString 经 ResourceManager→ResourceBundle 加载 `dotnet/text/json/Resources/Strings.properties`——转换器把 .resx **原样复制**且只收集 csproj 显式条目（SDK 默认 **/*.resx 未扫），导致所有 SR 字符串返回 fallback `{Name}`，FullJsonSchema1 变成 `{FullJsonSchema1}` 非法 JSON，reader 在 'F' 处报 "invalid start of a property name"。
- **修复**: (1) SolutionLoader.ScanProjectResources 补扫 SDK 默认 **/*.resx；(2) 新增 ResxConverter，复制循环把 .resx 解析为 Java .properties（XML data/value → key=value，转义），目标路径 = 嵌入 LogicalName（RootNamespace + 相对路径点分隔 + .properties）。
- **验证**: JsonReaderException 159→0；总失败 1489→1456。**C# 阶段树 dotnet test 19262 全绿为真验证**（BUILDING_INBOX_LIBRARY 已从测试移除，测试钉住本地转换库）。

## Iteration 26 — 枚举底层类型标记（UInt64/UInt32 枚举误判 Int64/Int32）
- **错误信息**: `The JSON value could not be converted to class SampleEnumUInt64. Path: $.MyUInt64Enum`（38 个）
- **根因**: compat TypeHelper.getTypeCode 对枚举按 value 字段类型推断（long→Int64、int→Int32），无法区分 UInt64/Int64、UInt32/Int32。UInt64 枚举走 Int64 case，`18446744073709551615` 超出 long 范围 → tryGetInt64 失败 → 抛错。
- **修复**: EnumTransformer 给显式值枚举生成 `getUnderlyingTypeCode()` 静态方法（返回底层 TypeCode）；compat getTypeCode 反射调用，fallback 旧推断。
- **验证**: SampleEnumUInt64 失败 38→1（剩余 1 个是 read-ahead 错位的下游）。

## Iteration 27（待办）— read-ahead 状态错位（属性读取串位）
- **现象**: SimpleTestClass 完整反序列化在 MyChar 处 `IllegalStateException: Cannot get the value of a token type 'String' as a number`——数字属性(MyByte)→char 属性(MyChar)组合触发。单属性均正常。C# 转换版(真验证)全绿 → Java 生成差异。
- **根因方向**: read-ahead 恢复后 reader 位置/属性查找错位——MyChar 的 jsonPropertyInfo 解析到数字型 converter，读到 String token。`readAheadPropertyValue`/`singleValueReadWithReadAhead` 的 Java 转换状态机与 C# 语义有偏差。
- **状态**: 📋 待修（Iteration 27）

## Iteration 28 — Exception.Source 标记失效 / 多维数组 Rank 丢失 / 泛型数组元素装箱 / ref reader 异常路径不回写 / 补充字符未转义
- **目标**: 按用户指令"先不处理泛型,先把总错误数量降下来"处理非泛型簇。总量 1090 → 993（-97，本迭代）。
- **修复 1（ExceptionCompat.Source 标记）**: `ExceptionCompat.getSource/setSource` 原为 no-op，导致 ThrowHelper 标记的 InvalidOperationException/FormatException（Source="dotnet.text.json.Rethrowable"）无法被 JsonConverter1.readCore/writeCore 的 catch 识别并重抛为 JsonException。改为 WeakHashMap 弱侧表记录。修复 40 个 "Expected JsonException Actual IllegalStateException"。
- **修复 2（多维数组 Rank）**: C# `int[,]` → Java `int[][]` 丢失 Rank，`Type.GetArrayRank()>1` 检查失效（System.Text.Json 拒收多维数组）。转换器 GetClassLiteral/TransformTypeOf 对 Rank>1 数组发出 `TypeHelper.getMultiDimArrayClass(element, rank)`（反射建类 + WeakHashMap 登记 rank，返回 `Class<int[][]>` 带 unchecked cast 保泛型推断）；compat getArrayRank 查表。修复 readPrimitiveArrayFail 第 3 断言与 NullTests.invalidRootOnWrite。
- **修复 3（泛型数组元素装箱 → 基元数组）**: `List<T>.ToArray()`（T=泛型参数）生成的 `stream().toArray(size -> (T[]) newArrayInstance(tClass, size))` 对 C# 基元 T 产出装箱数组（Integer[] 而非 int[]），22 个 `[Integer cannot be cast to [I`。compat 新增 `TypeHelper.listToArray(collection, elementClass)`（基元反射建数组 + Array.set 拆箱；引用类型经 toArray(T[]) 用 a.getClass() 建真实 T[]）；转换器两个 ToArray 泛型分支改发该调用。配套：`TypeHelper.getElementType` 不再包箱（.NET GetElementType 语义，`int[].GetElementType()==int`），`castArray` 对基元源数组总是装箱（T[] 型调用方无法持 int[]）。
- **修复 4（ref reader 异常路径不回写）**: `JsonDocument.ParseValue(ref reader)` 的 ref 写回 post-statement 只在成功路径执行；callee 内部 restore 只写回 holder，调用方局部对象保留 mid-call 突变（BytesConsumed 12 vs 11）。ArgumentTransformer 对 Utf8JsonReader 类型的 ref 实参用 `valueExpr.clone()` 播种 holder——callee 突变不触及调用方对象，异常路径天然保持调用前状态（等价 .NET restore）。修复 parseReaderTerminatesAfterPropertyName ×4。
- **修复 5（补充字符恒转义）**: .NET 全局 block list 恒转义非 BMP 字符（即便 UnsafeRelaxedJsonEscaping/Create(All)，无内置 encoder 可不转义）。compat JavaScriptEncoder.willEncode 对 scalar>=0x10000 恒 true；tryEncodeUnicodeScalar 补代理对形式 `\uD801\uDC37`。修复 escapingTestWhileWritingSurrogate ×4。
- **剩余大簇**: 83 个 JsonElement→X CCE（IList<T> 等泛型集合属性元素类型丢失——泛型冻结区）；30+14 个 attribute 相关（转换器不发射 C# attribute → JsonConverterAttribute/JsonNumberHandlingAttribute 校验缺失）；8 个 sbyte[] 被 ByteArrayConverter 拦截（C# byte[]/sbyte[] 均映射 Java byte[]，运行时不可区分——需 per-member C# 类型元数据）；ExceptionTests 多维数组属性路径 rank 丢失（同类设计缺口）。
