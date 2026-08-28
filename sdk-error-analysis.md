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
