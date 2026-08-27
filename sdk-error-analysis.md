# SDK Conversion Error Analysis

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

