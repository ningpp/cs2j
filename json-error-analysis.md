# C# → Java 转换错误分析日志

## Iteration 1 — InvalidCastException (MemberAccessExpressionSyntax → TypeSyntax)
- **Java 文件**: N/A（转换器在 C#→Java 转换阶段的 ReadOnlyStructMaker 预处理中崩溃，未产出 Java 文件）
- **行号**: MethodMigrator.cs:130（`VisitLocalDeclarationStatement` 调用 `base.Visit...` 时下层 Roslyn `VisitVariableDeclaration` 将 visit 结果强转回 `TypeSyntax` 失败）
- **错误信息**: `Unable to cast object of type 'Microsoft.CodeAnalysis.CSharp.Syntax.MemberAccessExpressionSyntax' to type 'Microsoft.CodeAnalysis.CSharp.Syntax.TypeSyntax'.`
- **代码片段**:
  ```csharp
  // WriteStackFrame.InitializeReEntry
  public JsonConverter InitializeReEntry(Type type, JsonSerializerOptions options, string? propertyName = null)
  {
      JsonClassInfo classInfo = options.GetOrAddClass(type);   // ← 局部声明的类型名 = 结构体字段名
      ...
  }
  // WriteStackFrame 结构体字段: public JsonClassInfo JsonClassInfo;  // 字段名与类型名相同
  ```
- **对应 C# 文件**: `d:\csharpjson\src\System\Text\Json\Serialization\WriteStackFrame.cs`
- **根因分类**: Transformer（ReadOnlyStructMaker 重写器逻辑缺陷）
- **涉及组件**: `src/CSharpToJava.Core/ReadOnlyStructMaker/MethodMigrator.cs` — `ImplicitFieldToResultRewriter.VisitIdentifierName`（第 145-163 行）
- **分析**: `WriteStackFrame` 结构体存在与类型同名（无下划线）的公共字段（如 `JsonClassInfo JsonClassInfo;`、`MetadataPropertyName MetadataPropertyName;`）。`ImplicitFieldToResultRewriter` 将方法体内所有与字段/属性同名的标识符一律重写为 `result.<name>` 成员访问表达式，但未区分**类型位置**（局部变量声明的类型、泛型实参等）。局部声明 `JsonClassInfo classInfo = ...` 的类型 `JsonClassInfo` 被改写成 `result.JsonClassInfo`（MemberAccessExpression），而 Roslyn `CSharpSyntaxRewriter.VisitVariableDeclaration` 内建强转 `(TypeSyntax)Visit(node.Type)` 随即抛出 InvalidCastException，导致整个转换进程崩溃。

- **状态**: ✅ Fixed

## Iteration 2 — ArgumentException: Syntax node is not within syntax tree
- **Java 文件**: N/A（ReadOnlyStructMaker 预处理阶段崩溃）
- **行号**: StructAnalyzer.cs:470（`_model.GetDeclaredSymbol(method)`）
- **错误信息**: `Syntax node is not within syntax tree`
- **代码片段**:
  ```csharp
  // StructAnalyzer.AnalyzeCore — 成员遍历
  var methodSyntax = method.DeclaringSyntaxReferences
      .Select(r => r.GetSyntax()).OfType<MethodDeclarationSyntax>().FirstOrDefault();  // ← 可能选中 ref 桩声明
  ...
  if (IsMutating(methodSyntax))   // ← _model.GetDeclaredSymbol(methodSyntax) 因节点不在当前树而抛异常
  ```
- **对应 C# 文件**: `d:\csharpjson\ref\System.Text.Json.cs`（GenAPI 风格的 ref 程序集桩文件，与 `d:\csharpjson\src\System\Text\Json\Document\JsonElement.ArrayEnumerator.cs` 的 `ArrayEnumerator` 为 partial 合并）
- **根因分类**: 语义丢失（转换预处理把 ref 桩程序集文件并入语义编译，导致 partial 类型符号的 `DeclaringSyntaxReferences` 首项落在桩文件中）
- **涉及组件**: `src/CSharpToJava.Core/ReadOnlyStructMaker/StructAnalyzer.cs`、`src/CSharpToJava.CLI/ProjectConversionExclusionPlanner.cs`、`src/CSharpToJava.Core/ReadOnlyStructMaker/ReadOnlyStructRewriter.cs:915`
- **分析**: `ref\System.Text.Json.cs` 是 ref 程序集桩（`AssemblyName=dotnet.text.json.Ref`，`EnableDefaultCompileItems=false`，仅含 API 表面与 `throw null` 实现）。预处理器将其纳入语义编译后，`JsonElement.ArrayEnumerator.Dispose` 的合并符号把 ref 桩声明列为第一个 `DeclaringSyntaxReference`；`AnalyzeCore` 取其语法节点传给 `IsMutating`，而 `_model` 绑定的是 src 文件树 → `GetDeclaredSymbol` 抛 "Syntax node is not within syntax tree"，转换进程崩溃。若放任不管，ref 桩文件还会被转换成重复的 Java 类型（重复类错误）。

- **状态**: ✅ Fixed

## Iteration 3 — 无法找到合适的方法 / 不兼容的类型 (deserialize 重载被丢弃)
- **Java 文件**: system-text-json/src/main/java/dotnet/text/json/JsonSerializer.java
- **行号**: 762, 807, 1172, 1391
- **错误信息**:
  - `[762,16] 对于deserialize(java.lang.Class<TValue>,java.lang.String,java.lang.Class<?>,dotnet.text.json.JsonSerializerOptions), 找不到合适的方法`
  - `[807,24] 对于deserialize(java.lang.Class<java.lang.Object>,java.lang.String,java.lang.Class,dotnet.text.json.JsonSerializerOptions), 找不到合适的方法`
- **代码片段**:
  ```java
  public static <TValue> TValue deserialize(Class<TValue> _cs2j_TValue, String json, JsonSerializerOptions options) {
      if (json == null) { throw new ArgumentNullException("json"); }
      return deserialize(_cs2j_TValue, json, TypeHelper.toWrapperType(_cs2j_TValue), options);   // ← 引用不存在的 4 参方法
  }
  public static Object deserialize_0tp(String json, Class returnType, JsonSerializerOptions options) {
      ...
      Object value = deserialize(Object.class, json, returnType, options);   // ← 同样引用不存在的 4 参方法
      return value;
  }
  ```
- **对应 C# 文件**: `d:\csharpjson\src\System\Text\Json\Serialization\JsonSerializer.Read.String.cs`
- **根因分类**: Transformer（partial 成员去重键缺陷）
- **涉及组件**: `src/CSharpToJava.Core/Transformers/Type/ClassTransformer.cs:346`（`seenMemberKeys` 的 `ParamKey` 去重键只含参数语法类型，不含泛型 arity）
- **分析**: JsonSerializer 是跨 ~15 文件的 partial 类，成员去重键 `m:{iface}:{name}:{paramTypes}` 对 `Deserialize<TValue>(string, Type, options)`（private, 1 个类型参数）与 `Deserialize(string, Type, options)`（public, 0 个类型参数）产生完全相同键，后者先被处理 → 前者作为"重复"被丢弃。但调用点仍按"泛型方法调用前置 Class 令牌"约定生成 `deserialize(Class<TValue>, String, Class, options)` 4 参调用，与输出中的任何方法都不匹配 → 编译失败。
- **状态**: 🔴 待修复

## Iteration 4 — 类型变量数目错误 (嵌套委托同名不同 arity)
- **Java 文件**: system-text-json/src/main/java/dotnet/text/json/MemberAccessor.java:[31,99], Serialization/ReflectionEmitMemberAccessor.java:[86,90], Serialization/ReflectionMemberAccessor.java:[81,90]
- **行号**: MemberAccessor.java:31
- **错误信息**: `类型变量数目错误; 需要1`
- **代码片段**:
  ```java
  public abstract <T, TArg0, TArg1, TArg2, TArg3> JsonClassInfo.ParameterizedConstructorDelegate<T, TArg0, TArg1, TArg2, TArg3> createParameterizedConstructor(ConstructorInfo constructor);
  // 但 JsonClassInfo.java:800 只声明了: @FunctionalInterface public interface ParameterizedConstructorDelegate<T>
  ```
- **对应 C# 文件**: `d:\csharpjson\src\System\Text\Json\Serialization\JsonClassInfo.cs`（第 17/19 行两个同名不同 arity 的 delegate）
- **根因分类**: Transformer（partial 成员去重键 + 嵌套类型重命名缺失）
- **涉及组件**: `src/CSharpToJava.Core/Transformers/Type/ClassTransformer.cs:353`（`del:` 键）、`Transformers/Type/DelegateTransformer.cs`、`Context/TypeMappingService.cs:744`（`TryMapNestedType` 引用映射）
- **分析**: 同一去重键缺陷——`del:{name}` 不含类型参数个数，6-arity 的 `ParameterizedConstructorDelegate<T, TArg0..3>` 被当作 1-arity 的重复而丢弃；且即使保留，Java 也不允许两个同名嵌套类型，必须对非最小 arity 重命名为 `Name_arity`，并保证定义点（DelegateTransformer）与所有引用点（TryMapNestedType）一致。
- **状态**: ✅ Fixed

## Iteration 5 — 找不到符号 toByteArray() + 无法推断 ReadOnlyMemory<> 类型参数 (ReadOnlyMemory<byte> 缺 compat API)
- **Java 文件**: system-text-json/src/main/java/dotnet/text/json/JsonDocument.java:[598,99]、JsonSerializer.java:[1199,45]
- **行号**: JsonDocument.java:598
- **错误信息**: `找不到符号 方法 toByteArray() 位置: 类 io.github.ningpp.compat.ReadOnlyMemory<java.lang.Integer>`
- **代码片段**:
  ```java
  // JsonDocument.CloneElement — C# ReadOnlyMemory<byte> segmentCopy = GetRawValue(...).ToArray();
  ReadOnlyMemory<Integer> segmentCopy = new ReadOnlyMemory<Integer>(getRawValue(index, true).toByteArray());
  // JsonSerializer — C# return output.WrittenMemory.ToArray();   (byte[] 返回)
  return output.getWrittenMemory().toByteArray();
  ```
- **对应 C# 文件**: `d:\csharpjson\src\System\Text\Json\Document\JsonDocument.cs:772`、`d:\csharpjson\src\System\Text\Json\Serialization\JsonSerializer.Write.Stream.cs`（`WrittenMemory.ToArray()`）
- **根因分类**: 类型映射缺失（compat 库 API 缺口，非转换器输出错误）
- **涉及组件**: `java/csharptojava-compat/.../ReadOnlyMemory.java`（缺 `byte[]` 构造器与 `toByteArray()`）；转换器侧 `src/CSharpToJava.Core/Transformers/Expression/Transformers/InvocationExpressionTransformer.cs:2943-2955`（`ReadOnlyMemory<byte>.ToArray()` → `.toByteArray()`，与 `ExpressionTransformerHelpers.cs:348-352`（隐式 `byte[]`→`ReadOnlyMemory<byte>` 包 `new ReadOnlyMemory<Integer>(...)`）
- **分析**: 转换器按设计约定 `byte` 泛型实参装箱为 `Integer`，`ToArray()` 输出 `toByteArray()`；但 compat `ReadOnlyMemory<T>` 只有 `toArray()`/`T[]` 构造器，缺少 `byte[]` 构造器与 `toByteArray()`（`ReadOnlySpan`/`Span` 已有）。compat 补齐后生成代码可直接编译。
- **测试**: `java/csharptojava-compat/.../ReadOnlyMemoryByteCtorTest.java`（RED→GREEN）、`tests/CSharpToJava.Tests/ReadOnlyMemoryToByteArrayTests.cs`
- **状态**: ✅ Fixed

## Iteration 6 — 无法取消引用 int/long (指针强转非法追加 .address())
- **Java 文件**: system-text-json/src/main/java/dotnet/text/json/JsonReaderHelper.java:[116,60][236,62][237,48][251,27][257,24][258,47][275-296]
- **行号**: JsonReaderHelper.java:116
- **错误信息**: `无法取消引用int`；`无法取消引用long`
- **代码片段**:
  ```java
  // C#: int unaligned = (int)Unsafe.AsPointer(ref searchSpace) & (Vector<byte>.Count - 1);
  int unaligned = (int)(Unsafe.asPointer(searchSpace).address()) & (Vector.getCount() - 1);  // asPointer 返回 int
  // C#: if (((int)(byte*)index < length)) { ... return (int)(byte*)index; }
  if ((int)(index.address()) < length) { ... }   // index 是 long（IntPtr 映射）
  return (int)((index + 1).address());
  ```
- **对应 C# 文件**: `d:\csharpjson\src\System\Text\Json\Reader\JsonReaderHelper.cs`（`IndexOfOrLessThan`）
- **根因分类**: Transformer（指针→整型强转规则无条件追加 `.address()`）
- **涉及组件**: `src/CSharpToJava.Core/Transformers/Expression/Transformers/TypeOperationTransformer.cs:278-285`
- **分析**: `(int)ptr` 规则假设指针操作数在 Java 中必为 MemorySegment；但对 IntPtr→long 的整数值、数字字面量指针 `(byte*)8`、以及 compat 返回 int 的 `Unsafe.asPointer(...)`，值本身就是裸地址，追加 `.address()` 生成非法 Java。修复：解析嵌套指针强转链到叶子（整型/IntPtr → 裸值）并识别 `Unsafe.AsPointer` 调用，仅对真实 MemorySegment 追加 `.address()`（与 BinaryExpressionTransformer.TransformPointerComparisonOperand 的既有约定一致）。
- **测试**: `tests/CSharpToJava.Tests/PointerCastToIntegralTests.cs`（RED→GREEN，含真实指针仍追加 `.address()` 的防回归用例）
- **状态**: ✅ Fixed

## Iteration 7 — 找不到符号: 变量 converters (hoisted 局部函数捕获注册泄漏)
- **Java 文件**: system-text-json/src/main/java/dotnet/text/json/Serialization/Converters/IEnumerableDefaultConverter.java:[70,29][83,29][147,29]、DictionaryDefaultConverter.java:[96,43][115,43][192,43]
- **行号**: IEnumerableDefaultConverter.java:70
- **错误信息**: 找不到符号 符号: 变量 converters
- **代码片段**:
  `java
  // C#: Add(element!, ref state);        → 生成:
  add(element, state, converters);        // converters 是幽灵实参（C# 中不存在）
  `
- **对应 C# 文件**: d:\csharpjson\src\System\Text\Json\Serialization\JsonSerializerOptions.Converters.cs（GetDefaultSimpleConverters 内局部函数 oid Add(JsonConverter converter) => converters.Add(...) 捕获了局部变量 converters）
- **根因分类**: 语义丢失（MethodState 按名称登记的捕获表跨方法/跨文件泄漏）
- **涉及组件**: src/CSharpToJava.Core/Context/MethodConversionState.cs（Reset() 漏清 _hoistedLocalFunctionCaptures/_hoistedLocalFunctionDefaults）
- **分析**: 局部函数提升机制把"Add→捕获 converters"登记在 per-context 的 MethodState 中；EnterMethod→Reset() 清除了局部函数列表却漏掉捕获表。后续任何文件/方法中名为 Add 的裸调用都会命中捕获表，把陈旧的 converters 追加为实参（JsonSerializerOptions 自己因巧合"正确"，其他类全部报错）。
- **测试**: 	ests/CSharpToJava.Tests/HoistedLocalFunctionCaptureScopeTests.cs（RED→GREEN）
- **状态**: ✅ Fixed

## Iteration 8 — 不是抽象的, 并且未覆盖抽象方法 createCollection (ref 参数 Holder 包裹不一致)
- **Java 文件**: system-text-json/src/main/java/dotnet/text/json/Serialization/Converters/ArrayConverter.java:[26,14]（同族：ListOfTConverter、QueueOfTConverter、StackOfTConverter、ConcurrentQueue/StackOfTConverter、ImmutableEnumerableOfTConverter）
- **行号**: ArrayConverter.java:26
- **错误信息**: ArrayConverter不是抽象的, 并且未覆盖IEnumerableDefaultConverter中的抽象方法createCollection(ObjectHolder<Utf8JsonReader>,ObjectHolder<ReadStack>,JsonSerializerOptions)
- **代码片段**:
  `java
  // 基类抽象（无方法体 → 保守判定 → ref 参数包 Holder）:
  protected abstract void createCollection(ObjectHolder<Utf8JsonReader> reader, ObjectHolder<ReadStack> state, JsonSerializerOptions options);
  // 子类覆写（有方法体且未重赋值 → 判定只读 → 裸参数）:
  protected void createCollection(Utf8JsonReader reader, ReadStack state, JsonSerializerOptions options) { ... }
  `
- **对应 C# 文件**: d:\csharpjson\src\System\Text\Json\Serialization\Converters\Collection\ArrayConverter.cs（CreateCollection(ref Utf8JsonReader reader, ref ReadStack state, ...) override）
- **根因分类**: Transformer（ref 参数只读分析未沿覆写链对齐）
- **涉及组件**: src/CSharpToJava.Core/Context/ConversionContext.cs（EnterMethod 的 readOnlyParams 计算）、src/CSharpToJava.Core/Transformers/StructCloneHelper.cs（IsRefParamEffectivelyReadOnly）
- **分析**: "ref 结构体参数从不重赋值 → 跳过 ObjectHolder"的分析对每个方法独立计算：抽象基类无方法体 → 保守 → 包 Holder；覆写有方法体且不重赋值 → 裸参数。同一 C# 签名渲染成两个 Java 签名，javac 报"未覆盖抽象方法"。修复：IsRefParamEffectivelyReadOnlyInHierarchy 要求整条覆写链都满足才跳过 Holder。
- **测试**: 	ests/CSharpToJava.Tests/RefHolderOverrideConsistencyTests.cs（RED→GREEN，含非抽象基类走自身分析的防回归用例）
- **状态**: ✅ Fixed

## Iteration 9 — 不兼容的类型: CSharpGenericIterable 无法转换为 TElement[] ((T[])(IEnumerable) 双重强转)
- **Java 文件**: system-text-json/src/main/java/dotnet/text/json/Serialization/Converters/ArrayConverter.java:[48,33]
- **行号**: ArrayConverter.java:48
- **错误信息**: 不兼容的类型: io.github.ningpp.compat.CSharpGenericIterable<capture#1, 共 ?>无法转换为TElement[]
- **代码片段**:
  `java
  // C#: TElement[] array = (TElement[])(IEnumerable)value;
  var array = (TElement[])((CSharpGenericIterable<?>)(value));   // 接口类型不能强转为数组
  `
- **对应 C# 文件**: d:\csharpjson\src\System\Text\Json\Serialization\Converters\Collection\ArrayConverter.cs（OnWriteResume）
- **根因分类**: Transformer（枚举接口→数组强转漏判）
- **涉及组件**: src/CSharpToJava.Core/Transformers/Expression/Transformers/TypeOperationTransformer.cs（数组强转规则只查 AllInterfaces，未覆盖"源类型本身是 IEnumerable"）
- **分析**: System.Collections.IEnumerable 映射为 CSharpGenericIterable<?>；随后 (TElement[]) 强转该接口类型在 Java 非法。C# 语义是引用强转（运行时对象就是 TElement[]），应经 Object 中转：(TElement[]) (Object) (expr)。
- **测试**: 	ests/CSharpToJava.Tests/EnumerableInterfaceToArrayCastTests.cs（RED→GREEN）
- **状态**: ✅ Fixed

## Iteration 10 — 不是抽象的, 并且未覆盖抽象方法 write/read + Utf8Parser 找不到合适方法 (泛型基类覆写未装箱 + compat bool 解析缺失)
- **Java 文件**: system-text-json/src/main/java/dotnet/text/json/Serialization/Converters/BooleanConverter.java:[24,14][29,20][35,20][39,36]（同族：ByteConverter、CharConverter、DateTimeConverter 等全部值类型 converter）
- **行号**: BooleanConverter.java:24
- **错误信息**: BooleanConverter不是抽象的, 并且未覆盖JsonConverter1中的抽象方法write(Utf8JsonWriter,java.lang.Boolean,JsonSerializerOptions)；eadWithQuotes(Utf8JsonReader)无法覆盖；对于tryParse(ReadOnlySpan<Integer>,BoolHolder,IntHolder), 找不到合适的方法
- **代码片段**:
  `java
  // 基类泛型槽位（T=Boolean 代入）: abstract T read(...); abstract void write(writer, T value, ...);
  // 覆写却渲染基本类型:
  public boolean read(...) { ... }        // 不匹配 Boolean read(...)
  public void write(writer, boolean value, ...) { ... }   // 不匹配 write(writer, Boolean, ...)
  // C#: Utf8Parser.TryParse(propertyName, out bool value, out int bytesConsumed) → compat 无 BoolHolder 重载
  `
- **对应 C# 文件**: d:\csharpjson\src\System\Text\Json\Serialization\Converters\Value\BooleanConverter.cs
- **根因分类**: Transformer（覆写签名未按泛型基类槽位装箱）+ 类型映射缺失（compat Utf8Parser bool 重载）
- **涉及组件**: src/CSharpToJava.Core/Transformers/Member/MethodTransformer.cs（GetReturnType/ConvertParameter — 注意 OverriddenMethod 已代入类型实参，需查 OriginalDefinition）、java/csharptojava-compat/.../Utf8Parser.java
- **分析**: 基类 JsonConverter<T> 的抽象方法槽位是类型参数 T，Java 在派生类视角代入为包装类型（Boolean/Integer/...），覆写必须声明包装类型；此前按 C# 具体类型映射为基本类型（boolean）导致整批值类型 converter 未覆盖抽象方法。另 compat Utf8Parser 缺 	ryParse(ReadOnlySpan<Integer>, BoolHolder, IntHolder)。
- **测试**: 	ests/CSharpToJava.Tests/GenericOverrideBoxingTests.cs（RED→GREEN）、java/csharptojava-compat/.../Utf8ParserBoolTest.java（RED→GREEN）
- **状态**: ✅ Fixed

## Iteration 11 — writeStringValue 找不到合适方法 + Utf8JsonReader无法转换为ObjectHolder (CreateSpan(ref char) + ref 转发)
- **Java 文件**: system-text-json/src/main/java/dotnet/text/json/Serialization/Converters/CharConverter.java:[41,15][44,135][47,15]
- **行号**: CharConverter.java:41
- **错误信息**: 对于writeStringValue(ReadOnlySpan<CharHolder>), 找不到合适的方法；Utf8JsonReader无法转换为ObjectHolder<Utf8JsonReader>
- **代码片段**:
  `java
  // C#: writer.WriteStringValue(MemoryMarshal.CreateSpan(ref value, 1));
  io.github.ningpp.compat.CharHolder _valueRef = new io.github.ningpp.compat.CharHolder(value);
  writer.writeStringValue(new ReadOnlySpan<>(MemoryMarshal.createSpan(_valueRef, 1)));   // Span<CharHolder>
  // C#: ReadWithQuotes(ref reader) => Read(ref reader, ...);
  public Character readWithQuotes(ObjectHolder<Utf8JsonReader> reader) { return read(reader.value, null, null); }  // 应传 holder 本身
  `
- **对应 C# 文件**: d:\csharpjson\src\System\Text\Json\Serialization\Converters\Value\CharConverter.cs
- **根因分类**: Transformer（ref 实参转发优化未用层次化只读分析）+ 类型映射缺失（compat MemoryMarshal 缺 CharHolder 重载）
- **涉及组件**: src/CSharpToJava.Core/Transformers/Expression/Transformers/ArgumentTransformer.cs:410、java/csharptojava-compat/.../MemoryMarshal.java
- **分析**: (1) ref 实参转发优化按目标方法"自身"只读分析决定解包，覆写自抽象槽位（槽位渲染为 Holder）时误把 eader 解包为 eader.value；(2) CreateSpan(ref char, 1) 的 ref 降级把 char 包进 CharHolder，compat createSpan 泛型版本推断为 Span<CharHolder>，writeStringValue/writePropertyName 不接受。
- **测试**: 	ests/CSharpToJava.Tests/RefHolderForwardingToAbstractSlotTests.cs（RED→GREEN）、java/csharptojava-compat/.../MemoryMarshalCharHolderTest.java（RED→GREEN）
- **状态**: ✅ Fixed

## Iteration 12 — 找不到符号 enqueue (ConcurrentQueue<T> 映射到无 enqueue 的 Java 类型)
- **Java 文件**: system-text-json/src/main/java/dotnet/text/json/Serialization/Converters/ConcurrentQueueOfTConverter.java:[38,62]
- **行号**: ConcurrentQueueOfTConverter.java:38
- **错误信息**: 找不到符号（enqueue 不存在于 ConcurrentLinkedQueue）
- **代码片段**:
  `java
  // 类声明约束: TCollection extends ConcurrentLinkedQueue<TElement>   ← java.util 队列无 enqueue
  ((TCollection)(state.value.Current.getReturnValue())).enqueue(value);
  `
- **对应 C# 文件**: d:\csharpjson\src\System\Text\Json\Serialization\Converters\Collection\ConcurrentQueueOfTConverter.cs
- **根因分类**: 类型映射缺失（System.Collections.Concurrent.ConcurrentQueue1 → ConcurrentLinkedQueue，缺 C# 队列 API）
- **涉及组件**: config/TypeMappings.json
- **分析**: ConcurrentQueue<T> 曾映射为 java.util.concurrent.ConcurrentLinkedQueue，该类型无 enqueue/dequeue/tryDequeue；compat CSharpQueue（Queue<T> 的映射）提供完整 C# 队列 API。改映射为 CSharpQueue。
- **测试**: 	ests/CSharpToJava.Tests/ConcurrentQueueMappingTests.cs（RED→GREEN）
- **状态**: ✅ Fixed

## Iteration 13 — 找不到符号 Dictionary.Enumerator (嵌套类型外层名未走映射表)
- **Java 文件**: system-text-json/src/main/java/dotnet/text/json/Serialization/Converters/DictionaryOfTKeyTValueConverter.java:[48,19][50,33][55,33]
- **行号**: DictionaryOfTKeyTValueConverter.java:48
- **错误信息**: 找不到符号（Dictionary — 无此类型/导入）
- **代码片段**:
  `java
  // C#: Dictionary<TKey, TValue>.Enumerator enumerator;
  Dictionary.Enumerator enumerator;                       // 应为 CSharpDictionary.Enumerator
  enumerator = (Dictionary.Enumerator) value.iterator();
  `
- **对应 C# 文件**: d:\csharpjson\src\System\Text\Json\Serialization\Converters\Collection\DictionaryOfTKeyTValueConverter.cs
- **根因分类**: Transformer（嵌套类型引用未将外层类型走映射表）
- **涉及组件**: src/CSharpToJava.Core/Context/TypeMappingService.cs（TryMapNestedType 外层名用 MapSimpleTypeName 而非 MapType）
- **分析**: Dictionary<K,V>.Enumerator 的 Java 形式应为 CSharpDictionary.Enumerator（compat 静态嵌套类），但外层名只做了简单名映射，丢掉了 Dictionary2 → CSharpDictionary 的配置映射。
- **测试**: 	ests/CSharpToJava.Tests/DictionaryNestedEnumeratorMappingTests.cs（RED→GREEN）
- **状态**: ✅ Fixed

## Iteration 14 — 不兼容的类型: Object 无法转换为 TKey (嵌套泛型类引用丢失外层类型实参)
- **Java 文件**: system-text-json/src/main/java/dotnet/text/json/Serialization/Converters/DictionaryOfTKeyTValueConverter.java:[66,46][67,70][78,46][80,58]
- **行号**: DictionaryOfTKeyTValueConverter.java:66
- **错误信息**: 不兼容的类型: java.lang.Object无法转换为TKey（raw CSharpDictionary.Enumerator 的 getKey() 返回 Object）
- **代码片段**:
  `java
  // C#: Dictionary<TKey, TValue>.Enumerator enumerator; ... key = enumerator.Current.Key;
  CSharpDictionary.Enumerator enumerator;          // raw → getCurrent().getKey() 返回 Object
  var key = enumerator.getCurrent().getKey();      // Object 传给 writeWithQuotes(..., TKey, ...) 报错
  `
- **对应 C# 文件**: d:\csharpjson\src\System\Text\Json\Serialization\Converters\Collection\DictionaryOfTKeyTValueConverter.cs
- **根因分类**: Transformer（嵌套泛型类型引用未携带外层类型实参）
- **涉及组件**: src/CSharpToJava.Core/Context/TypeMappingService.cs（TryMapNestedType）
- **分析**: 嵌套类型引用的"外层参数扫描"跑在已构造符号上（实参已被替换为 string/int），扫描不到 TKey/TValue；且即使扫描到，追加的也是参数符号而非外层实参。改为在 OriginalDefinition 上扫描、并追加外层映射后的类型实参：CSharpDictionary.Enumerator<TKey, TValue>。
- **测试**: 	ests/CSharpToJava.Tests/DictionaryNestedEnumeratorMappingTests.cs（扩展）
- **状态**: ✅ Fixed

## Iteration 15 — EnumConverter: Unsafe.As 无符号装箱链缺失 + Enum.GetNames 映射缺失 + TypeCode 无显式值
- **Java 文件**: system-text-json/src/main/java/dotnet/text/json/Serialization/Converters/EnumConverter.java:[38,62][56,30][107,24][119,24][144,24][157,38]
- **行号**: EnumConverter.java:107
- **错误信息**: 不兼容的类型: int无法转换为T；找不到符号 getValue()；找不到符号 getNames(java.lang.Class) 位置: java.lang.Enum
- **代码片段**:
  `java
  // C#: return Unsafe.As<uint, T>(ref uint32);
  var _ret = ((T)(Unsafe.as(_uint32Ref)));           // 缺少 (T)(Object)(Integer)(...) 装箱链
  // C#: (int)s_enumTypeCode
  private static final String s_negativeSign = ((s_enumTypeCode.getValue() % 2) == 0 ? ...);  // TypeCode 无 getValue
  // C#: Enum.GetNames(...)
  String[] names = Enum.getNames(getTypeToConvert());  // java.lang.Enum 无 getNames
  `
- **对应 C# 文件**: d:\csharpjson\src\System\Text\Json\Serialization\Converters\Value\EnumConverter.cs
- **根因分类**: Transformer（Unsafe.As 装箱开关缺无符号类型）+ 类型映射缺失（Enum.GetNames）+ compat API 缺口（TypeCode 显式值）
- **涉及组件**: src/CSharpToJava.Core/Transformers/Expression/Transformers/InvocationExpressionTransformer.cs（两处 Unsafe.As 装箱 switch）、config/TypeMappings.json、java/csharptojava-compat/.../TypeCode.java、EnumHelper.java
- **分析**: Unsafe.As<uint/ushort/ulong/sbyte, T> 的装箱链 switch 只有有符号类型；System.Enum.GetNames 无方法映射（解析到 java.lang.Enum）；compat TypeCode 无显式值，转换器按显式值枚举发出 getValue()。
- **测试**: 	ests/CSharpToJava.Tests/UnsafeAsUnsignedBoxingTests.cs（RED→GREEN）、java/csharptojava-compat/.../EnumHelperNamesTypeCodeTest.java（RED→GREEN）
- **状态**: ✅ Fixed

## Iteration 16 — 不兼容的类型: int无法转换为Byte (Unsafe.As 装箱链未先收窄映射基本类型)
- **Java 文件**: system-text-json/src/main/java/dotnet/text/json/Serialization/Converters/EnumConverter.java:[157,38][183,39]
- **行号**: EnumConverter.java:157
- **错误信息**: 不兼容的类型: int无法转换为java.lang.Byte
- **代码片段**:
  `java
  // C#: Unsafe.As<byte, T>(ref ubyte8Value) — byte 映射 int → ref 降级为 IntHolder
  var _ret = ((T)(Object)(Byte)(Unsafe.as(_ubyte8ValueRef)));   // (Byte)(int) 非法
  // 修复后: (T)(Object)(Byte)(((byte)(Unsafe.as(...))))          // 先收窄再装箱
  `
- **对应 C# 文件**: d:\csharpjson\src\System\Text\Json\Serialization\Converters\Value\EnumConverter.cs
- **根因分类**: Transformer（Unsafe.As 装箱链缺少映射基本类型收窄）
- **涉及组件**: src/CSharpToJava.Core/Transformers/Expression/Transformers/InvocationExpressionTransformer.cs（两处 Unsafe.As 装箱链）
- **分析**: 源类型映射后的基本类型（byte→int、ushort 等）与装箱目标的 primitive 不一致时，(Byte)(int) 强转非法；需先 ((byte)(expr)) 收窄再装箱。
- **测试**: 	ests/CSharpToJava.Tests/UnsafeAsUnsignedBoxingTests.cs（扩展 byte/ushort 用例）
- **状态**: ✅ Fixed

## Iteration 17 — 找不到合适的方法 tryParse (EnumHelper.tryParse 需接受 Class<?>)
- **Java 文件**: system-text-json/src/main/java/dotnet/text/json/Serialization/Converters/EnumConverter.java:[322,37][322,96]
- **行号**: EnumConverter.java:322
- **错误信息**: 对于tryParse(String, ObjectHolder<T>, Class<capture#1, 共 ?>), 找不到合适的方法
- **代码片段**:
  `java
  var _ifCond125 = !EnumHelper.tryParse(enumString, _valueHolder1, tClass) && ...;   // tClass 是 Class<?>
  `
- **对应 C# 文件**: d:\csharpjson\src\System\Text\Json\Serialization\Converters\Value\EnumConverter.cs（ReadWithQuotes）
- **根因分类**: 类型映射缺失（compat EnumHelper.tryParse 签名过严）
- **涉及组件**: java/csharptojava-compat/.../EnumHelper.java
- **分析**: 转换器传入运行时 Class 字段（Class<?>），compat 	ryParse(String, ObjectHolder<T>, Class<T>) 的泛型推断得到捕获类型而无法匹配。改为 Class<?>（T 从 ObjectHolder 推断）。
- **测试**: java/csharptojava-compat/.../EnumHelperTryParseWildcardClassTest.java（RED→GREEN）
- **状态**: ✅ Fixed

## Iteration 18 — 不兼容的类型: Object[] 无法转换为 Tuple5<...>[] (ArrayPool.Rent 结果未强转)
- **Java 文件**: system-text-json/src/main/java/dotnet/text/json/Serialization/Converters/ObjectWithParameterizedConstructorConverter.java:[178,41][302,46]
- **行号**: ObjectWithParameterizedConstructorConverter.java:178
- **错误信息**: 不兼容的类型: java.lang.Object[]无法转换为Tuple5<...>[]
- **代码片段**:
  `java
  var newCache = ArrayPool.rentShared(Tuple5.class, len * 2);   // 推断出 raw/Object[]
  argumentState.FoundProperties = newCache;                     // Tuple5<...>[] = Object[] 非法
  `
- **对应 C# 文件**: d:\csharpjson\src\System\Text\Json\Serialization\Converters\Object\ObjectWithParameterizedConstructorConverter.cs（ArrayPool<FoundProperty>.Shared.Rent(...)，FoundProperty = ValueTuple<5>）
- **根因分类**: Transformer（ArrayPool.Rent 调用未带目标数组类型强转）
- **涉及组件**: src/CSharpToJava.Core/Transformers/Expression/Transformers/InvocationExpressionTransformer.cs（ArrayPool.Rent 路径）
- **分析**: entShared(T.class, n) 只能推断出 raw 元素类型，ar 承接后赋值给参数化 T[] 失败。生成 ({javaElem}[]) ArrayPool.rentShared(...) 显式强转。
- **测试**: 	ests/CSharpToJava.Tests/ArrayPoolRentTypedCastTests.cs（RED→GREEN；既有 ArrayPoolTypeWitnessTests 子串断言兼容）
- **状态**: ✅ Fixed

## Iteration 19 — 不兼容的类型: Object 无法转换为 TArg0 (成员访问接收者泛型实参被剥离)
- **Java 文件**: system-text-json/src/main/java/dotnet/text/json/Serialization/Converters/SmallObjectWithParameterizedConstructorConverter.java:[99,84][103,84][107,84][111,84]
- **行号**: SmallObjectWithParameterizedConstructorConverter.java:99
- **错误信息**: 不兼容的类型: java.lang.Object无法转换为TArg0
- **代码片段**:
  `java
  // C#: arguments.Arg0 = ((JsonParameterInfo<TArg0>)parameterInfo).TypedDefaultValue!;
  arguments.Arg0 = ((JsonParameterInfo1)(parameterInfo)).getTypedDefaultValue();   // raw → Object
  // 同文件另一处裸强转保留实参: var info = (JsonParameterInfo1<TArg>)(jsonParameterInfo);
  `
- **对应 C# 文件**: d:\csharpjson\src\System\Text\Json\Serialization\Converters\Object\ObjectWithParameterizedConstructorConverter.Small.cs
- **根因分类**: Transformer（成员访问对实例接收者误剥泛型实参）
- **涉及组件**: src/CSharpToJava.Core/Transformers/Expression/Transformers/IdentifierExpressionTransformer.cs（TransformMemberAccess 的 StripTypeArguments）
- **分析**: Type<T>.member() 的静态类型限定符剥离逻辑被应用到所有接收者：强转接收者 ((JsonParameterInfo1<TArg0>)x).getTypedDefaultValue() 被剥成 raw 类型 → getter 返回 Object。改为仅对静态类型接收者剥离。
- **测试**: 	ests/CSharpToJava.Tests/CastReceiverTypeArgsTests.cs（RED→GREEN，含静态接收者仍剥离的防回归）；既有 KeyValuePairMemberAccessPrecedenceTests 断言同步为带实参形态
- **状态**: ✅ Fixed

## Iteration 4 - 找不到符号 (Nullable.GetUnderlyingType 映射为 Object.getUnderlyingType)
- **Java 文件**: system-text-json/src/main/java/dotnet/text/json/JsonSerializerOptions.java
- **行号**: 383（另见 672; JsonClassInfo.java 156/349/361/365; Utf8JsonWriter.java 538/544/920/926; Utf8JsonReader.java 613/640; TypeExtensions.java 33; JsonConverter1.java 64/151; NullableConverterFactory.java 32）
- **错误信息**: `[ERROR] [383,42] 找不到符号 / 符号: 方法 getUnderlyingType(java.lang.Class) / 位置: 类 java.lang.Object`
- **代码片段**:
  ```java
  if (!converter.canConvert(typeToConvert)) {
      Class underlyingType = Object.getUnderlyingType(typeToConvert);   // 应为 TypeHelper.getUnderlyingType(...)
      if (underlyingType != null && converter.canConvert(underlyingType)) {
  ```
- **对应 C# 文件**: d:\csharpjson\src\System\Text\Json\Serialization\JsonSerializerOptions.Converters.cs（另见 TypeExtensions.cs、JsonConverterOfT.cs、JsonConverterOfT.WriteCore.cs、Converters\Value\NullableConverterFactory.cs）
- **根因分类**: 类型映射缺失（TypeMappings.json 缺 `System.Nullable.GetUnderlyingType` 方法映射）
- **涉及组件**: `config/TypeMappings.json`（方法映射）、`src/CSharpToJava.Core/Context/TypeMappingService.cs:437-443`（无类型实参的 Nullable 降级为 Object）
- **分析**: `Nullable.GetUnderlyingType(type)` 是 System.Nullable 静态类的调用。InvocationExpressionTransformer 的静态接收者方法映射查不到 `(System.Nullable, GetUnderlyingType)` 条目，方法名保持原样；同时静态接收者 `System.Nullable` 经 MapType 时因 `TypeArguments.Length == 0` 被降级为 `Object`，最终产出 `Object.getUnderlyingType(...)`。compat 库的 `TypeHelper.getUnderlyingType` 已有镜像方法但为 stub。修复：TypeMappings.json 增加方法映射到 `TypeHelper.getUnderlyingType`（复用 TypeHelper 兼容 helper 机制，自动导入），并在 compat 库实现该方法（boxed wrapper → 对应 primitive，其余返回 null）。

- **状态**: ✅ Fixed

## Iteration 5 - 找不到符号 (ConcurrentDictionary.GetOrAdd 未映射到 ConcurrentHashMapHelper)
- **Java 文件**: system-text-json/src/main/java/dotnet/text/json/JsonSerializerOptions.java
- **行号**: 672
- **错误信息**: `[ERROR] [672,26] 找不到符号 / 符号: 方法 getOrAdd(java.lang.Class,dotnet.text.json.JsonClassInfo) / 位置: 类型为java.util.concurrent.ConcurrentHashMap<java.lang.Class,dotnet.text.json.JsonClassInfo>的变量 _classes`
- **代码片段**:
  ```java
  JsonClassInfo result = _classes.get(type);
  if (result == null) {
      result = _classes.getOrAdd(type, new JsonClassInfo(type, this));   // 应为 ConcurrentHashMapHelper.getOrAdd(_classes, type, ...)
  }
  ```
- **对应 C# 文件**: d:\csharpjson\src\System\Text\Json\Serialization\JsonSerializerOptions.cs（GetOrAddClass，_classes 为 ConcurrentDictionary<Type, JsonClassInfo>）
- **根因分类**: 类型映射缺失（TypeMappings.json 缺 `ConcurrentDictionary`2.GetOrAdd` 方法映射）
- **涉及组件**: `config/TypeMappings.json`（methodMappings）
- **分析**: `TryAdd → ConcurrentHashMapHelper.tryAdd` 已有映射先例（实例方法 + receiver 前置，经 IsMappedCompatibilityHelperMethod 路径输出静态调用），但 `GetOrAdd` 无映射，生成 `_classes.getOrAdd(...)` 实例调用而 ConcurrentHashMap 无此方法。修复：按 TryAdd 模式添加 `GetOrAdd → ConcurrentHashMapHelper.getOrAdd` 映射（compat 库已有两个重载：value 与 valueFactory）。
- **测试**: tests/CSharpToJava.Tests/ConcurrentDictionaryGetOrAddTests.cs（RED→GREEN）

- **状态**: ✅ Fixed

## Iteration 6 - 找不到符号 (Dictionary.Values.CopyTo 未重写为 CSharpArray.copyTo)
- **Java 文件**: system-text-json/src/main/java/dotnet/text/json/JsonClassInfo.java
- **行号**: 156（另见 Utf8JsonReader.java/Utf8JsonWriter.java 的 ArrayPool 泛型错误、tryParse 重载错误等）
- **错误信息**: `[ERROR] [156,23] 找不到符号 / 符号: 方法 copyTo(dotnet.text.json.JsonPropertyInfo[],int) / 位置: 接口 java.util.Collection<dotnet.text.json.JsonPropertyInfo>`
- **代码片段**:
  ```java
  // Copy the dictionary cache to the array cache.
  cache.values().copyTo(cacheArray, 0);   // 应为 CSharpArray.copyTo(cache.values().toArray(), 0, cacheArray, 0, cache.size())
  ```
- **对应 C# 文件**: d:\csharpjson\src\System\Text\Json\Serialization\JsonClassInfo.cs（cache.Values.CopyTo(cacheArray, 0)，cache 为 Dictionary<string, JsonPropertyInfo>）
- **根因分类**: Transformer（IsFrameworkCollectionCopyTo 白名单未覆盖 Dictionary 嵌套集合视图）
- **涉及组件**: src/CSharpToJava.Core/Transformers/Expression/Transformers/InvocationExpressionTransformer.cs（IsFrameworkCollectionCopyTo，第 9260 行）
- **分析**: `cache.Values` 的类型是 `Dictionary<TKey,TValue>.ValueCollection`（嵌套 struct），`IsFrameworkCollectionCopyTo` 的显式类型白名单只含 List/HashSet/Queue/Stack/ICollection 等，未命中 → CopyTo 重写被跳过，生成 Java Collection 上不存在的 `copyTo(...)` 实例调用。修复：命中白名单后追加接口检查——实现 `ICollection<T>`/`ICollection` 的类型的 CopyTo 同样走 CSharpArray.copyTo 重写（`{receiver}.toArray()` + size 参数，语义与 .NET CopyTo 一致）。
- **测试**: tests/CSharpToJava.Tests/DictionaryValueCollectionCopyToTests.cs（RED→GREEN）

- **状态**: ✅ Fixed

## Iteration 7 - 找不到符号 (Unsafe.As<T>(object) 单类型实参重载未加目标类型强转)
- **Java 文件**: system-text-json/src/main/java/dotnet/text/json/JsonClassInfo.java
- **行号**: 350（另见 362、366）
- **错误信息**: `[ERROR] [350,121] 找不到符号 / 符号: 方法 getFieldType() / 位置: 类 java.lang.Object`
- **代码片段**:
  ```java
  return (memberInfo instanceof PropertyInfo propertyInfo ? propertyInfo.getPropertyType() : Unsafe.as(memberInfo).getFieldType());
  //                                                                              ^^^^^^^^ Unsafe.as 返回 Object，getFieldType 解析失败
  // 应为 (FieldInfo)(Object)Unsafe.as(memberInfo)
  ```
- **对应 C# 文件**: d:\csharpjson\src\System\Text\Json\Serialization\JsonClassInfo.cs（GetMemberType 等：`Unsafe.As<FieldInfo>(memberInfo)!.FieldType`）
- **根因分类**: Transformer（Unsafe.As 单类型实参重载未处理）
- **涉及组件**: src/CSharpToJava.Core/Transformers/Expression/Transformers/InvocationExpressionTransformer.cs（Unsafe.As 特殊分支，第 4876 行）
- **分析**: 转换器只处理 `Unsafe.As<TFrom,TTo>(ref TFrom)`（TypeArguments.Length == 2）的装箱链；`Unsafe.As<T>(object)`（Length == 1，普通引用强转重载）走通用路径生成 `Unsafe.as(arg)`，Java 无法从调用点推断 T → T=Object，后续成员访问 `getFieldType()` 解析失败。修复：Length == 1 时生成 `(T)(Object)Unsafe.as(arg)`，使接收者类型具体化。
- **测试**: tests/CSharpToJava.Tests/UnsafeAsSingleTypeArgTests.cs（RED→GREEN）

- **状态**: ✅ Fixed

## Iteration 8 - 不兼容的类型 / 显式类型参数无效 (ArrayPool<byte> 生成 int 类型见证与 int[] 强转)
- **Java 文件**: system-text-json/src/main/java/dotnet/text/json/Utf8JsonReader.java
- **行号**: 613（另见 640；Utf8JsonWriter.java 538/544/920/926；JsonDocument.java 多处）
- **错误信息**:
  - `[613,58] 不兼容的类型: java.lang.Object[]无法转换为int[]`
  - `[640,18] 无法将 ArrayPool<T> 显式类型参数int不符合声明的范围java.lang.Object 中的方法 getShared 应用于给定类型`
- **代码片段**:
  ```java
  byte[] otherUtf8TextArray = null;
  ...
  otherUtf8TextArray = (int[]) ArrayPool.rentShared(int.class, length);   // rentShared 推断 T=Object → Object[] → int[] 不可强转
  ...
  ArrayPool.<int>getShared().returnValue(otherUtf8TextArray);            // int 不能作为泛型类型实参
  ```
- **对应 C# 文件**: d:\csharpjson\src\System\Text\Json\Reader\Utf8JsonReader.cs（`ArrayPool<byte>.Shared.Rent(length)` / `Return(array)`，全库 108 处 byte 池 + 40 处 char 池）
- **根因分类**: Transformer（ArrayPool 特殊分支只处理 char，未处理 byte/sbyte）
- **涉及组件**: src/CSharpToJava.Core/Transformers/Expression/Transformers/InvocationExpressionTransformer.cs（ArrayPool Rent/Return 分支，第 4936 行）
- **分析**: `System.Byte` 类型映射为 Java `int`，但 `byte[]` 特意保留 Java `byte[]`（TypeMappingService.cs:496 注释 "C# byte[] stays Java byte[] for API compatibility"）。ArrayPool 分支仅对 char 路由到 `rentSharedCharArray`，byte 池走通用路径：`rentShared(int.class, ...)` 的 T 从调用点推断失败回退 Object → 返回 Object[]，强转 `(int[])` 属不可转换；`ArrayPool.<int>getShared()` 中 int 不是合法类型实参。修复：仿 char，将 byte/sbyte 路由到 compat 已有的 `rentSharedByteArray`/`returnSharedByteArray`（含 clearArray 重载）。

- **状态**: ✅ Fixed

## Iteration 9 - 找不到合适的方法 (Utf8Parser.TryParse 未按 out 参数类型选择专用 helper)
- **Java 文件**: system-text-json/src/main/java/dotnet/text/json/Utf8JsonReader.java
- **行号**: 5594（另见 5995、6144；Utf8JsonWriter.java 318/322；JsonDocument.java 500/579）
- **错误信息**: `对于tryParse(io.github.ningpp.compat.ReadOnlySpan<java.lang.Integer>,io.github.ningpp.compat.ObjectHolder<io.github.ningpp.compat.Decimal>,io.github.ningpp.compat.IntHolder,char), 找不到合适的方法`
- **代码片段**:
  ```java
  var _ifCond85 = Utf8Parser.tryParse(span, _valueHolder1, _bytesConsumedHolder1, numberFormat) && ...
  // ObjectHolder<Decimal> 参数应调用 tryParseDecimal（compat 无 tryParse(ObjectHolder<Decimal>,...) 重载）
  ```
- **对应 C# 文件**: d:\csharpjson\src\System\Text\Json\Reader\Utf8JsonReader.TryGet.cs（`Utf8Parser.TryParse(span, out decimal value, out int bytesConsumed, numberFormat)`；out 类型含 decimal/Guid/byte/sbyte/short/int/long/uint/ulong/float/double/bool）
- **根因分类**: Transformer（Utf8Parser.TryParse 未按 out 实参类型选择 helper 名）
- **涉及组件**: src/CSharpToJava.Core/Transformers/Expression/Transformers/InvocationExpressionTransformer.cs（方法名 camelCase 后统一为 tryParse，第 4390 行附近）
- **分析**: compat Utf8Parser 的 helper 按 out 类型命名：`tryParseDecimal`（ObjectHolder<Decimal>）、`tryParseUuid`（ObjectHolder<UUID>）、`tryParseDateTime`/`tryParseDateTimeOffset`；裸 `tryParse` 只接受 IntHolder/LongHolder/ShortHolder/ByteHolder/BoolHolder/FloatHolder/DoubleHolder。转换器把所有 TryParse 一律 camelCase 成 `tryParse`，导致 decimal/Guid 调用无匹配重载。修复：对 System.Buffers.Text.Utf8Parser.TryParse 按 out 参数类型选择专用 helper 名。

- **状态**: ✅ Fixed

## Iteration 10 - 找不到合适的方法 / 类型不兼容 (Stream.WriteAsync/FlushAsync 的 CancellationToken 未映射为 boolean)
- **Java 文件**: system-text-json/src/main/java/dotnet/text/json/Utf8JsonWriter.java
- **行号**: 318、322（另见 JsonDocument.java 1407/1422；PooledByteBufferWriterExtensions.java 32）
- **错误信息**:
  - `对于writeAsync(io.github.ningpp.compat.ReadOnlyMemory<java.lang.Integer>,io.github.ningpp.compat.CancellationToken), 找不到合适的方法`
  - `不兼容的类型: io.github.ningpp.compat.CancellationToken无法转换为boolean`
- **代码片段**:
  ```java
  _stream.writeAsync(_arrayBufferWriter.getWrittenMemory(), cancellationToken).join();  // compat 签名为 (ReadOnlyMemory<Integer>, boolean)
  _stream.flushAsync(cancellationToken).join();                                        // compat 签名为 (boolean)
  ```
- **对应 C# 文件**: d:\csharpjson\src\System\Text\Json\Writer\Utf8JsonWriter.cs（`await _stream.WriteAsync(_arrayBufferWriter.WrittenMemory, cancellationToken)`；同模式出现在 JsonDocument.cs、JsonSerializer.Write.Stream.cs 等）
- **根因分类**: Transformer（Stream 异步方法未映射 compat 的 boolean 型 CancellationToken 参数）
- **涉及组件**: src/CSharpToJava.Core/Transformers/Expression/Transformers/InvocationExpressionTransformer.cs（TextWriter 异步特例存在但 Stream 无对应处理）
- **分析**: compat StreamWrapper 把 CancellationToken 参数建模为 boolean（实现中忽略该值）：`writeAsync(ReadOnlyMemory<Integer>, boolean)`、`writeAsync(ReadOnlySpan<Integer>, boolean)`、`flushAsync(boolean)`，而 byte[] 重载 `writeAsync(byte[], int, int)` 直接无 token 参数。转换器只对 TextWriter 有异步特例，Stream 走通用路径原样传 CancellationToken 对象 → 无匹配重载。修复：为 System.IO.Stream 的 WriteAsync/FlushAsync 加特例——丢弃/转译尾部 CancellationToken：byte[] 重载去掉 token，Memory/Span 重载与 FlushAsync 传 `false`（compat 忽略该值）。

- **状态**: ✅ Fixed

## Iteration 11 - 不兼容的类型 (byte[].AsSpan() 结果赋给可变 Span<T> 未包装)
- **Java 文件**: system-text-json/src/main/java/dotnet/text/json/JsonDocument.java
- **行号**: 1303（另见 1483/1832/1841/1852；JsonSerializer.Read.Utf8JsonReader 等）
- **错误信息**: `不兼容的类型: io.github.ningpp.compat.ReadOnlySpan<java.lang.Integer>无法转换为io.github.ningpp.compat.Span<java.lang.Integer>`
- **代码片段**:
  ```java
  Span<Integer> rentedSpan = MemoryExtensions.asSpan(rented, 0, length);  // asSpan(byte[],int,int) 返回 ReadOnlySpan<Integer>
  ```
- **对应 C# 文件**: d:\csharpjson\src\System\Text\Json\Document\JsonDocument.Parse.cs（`Span<byte> rentedSpan = rented.AsSpan(0, length);`；同模式 JsonDocument.MetadataDb.cs、JsonSerializer.Read.Utf8JsonReader.cs）
- **根因分类**: Transformer（AdaptExpressionToTargetType 的 asSpan→Span 重包装在 IArrayTypeSymbol 守卫内，未覆盖 AsSpan() 调用结果）
- **涉及组件**: src/CSharpToJava.Core/Transformers/Expression/Utilities/ExpressionTransformerHelpers.cs（AdaptExpressionToTargetTypeCore，第 167-216 行）
- **分析**: `rented.AsSpan(0, length)` 表达式的 C# 类型是 `Span<byte>`（AsSpan 的返回值），不是 `byte[]`，所以数组→Span 适配分支的 `sourceType is IArrayTypeSymbol` 守卫未命中，`MemoryExtensions.asSpan(...)`（compat 返回 ReadOnlySpan<Integer>）原样赋给 `Span<Integer>` 声明。修复：在守卫外补充——目标为 System.Span<T> 且表达式以 `MemoryExtensions.asSpan(` 开头时用 `new Span<>(...)` 重包装（Span(ReadOnlySpan) 构造器）。

- **状态**: ✅ Fixed

## Iteration 13 - 不兼容的类型 (MemoryMarshal.Read<T> 结构体目标未附加 Class 实参)
- **Java 文件**: system-text-json/src/main/java/dotnet/text/json/JsonDocument.java
- **行号**: 1867（另见 1878、1996；JsonElement.java 264/297 为 tryGetValue 另一问题）
- **错误信息**: `不兼容的类型: int无法转换为dotnet.text.json.JsonDocument.DbRow`
- **代码片段**:
  ```java
  DbRow row = MemoryMarshal.read(new ReadOnlySpan<>(data.slice(i)));  // 匹配到 int 重载 read(ReadOnlySpan<Integer>)
  // 应为 MemoryMarshal.read(new ReadOnlySpan<>(data.slice(i)), DbRow.class)
  ```
- **对应 C# 文件**: d:\csharpjson\src\System\Text\Json\Document\JsonDocument.MetadataDb.cs（`MemoryMarshal.Read<DbRow>(data.Slice(i))`、`MemoryMarshal.Read<DbRow>(_data.AsSpan(index))`；`Read<int>/<uint>/<long>` 走 int/long 重载）
- **根因分类**: Transformer（MemoryMarshal.Read 泛型结构体目标缺少 Class 参数）
- **涉及组件**: src/CSharpToJava.Core/Transformers/Expression/Transformers/InvocationExpressionTransformer.cs（无 MemoryMarshal 特例，走通用静态路径）
- **分析**: compat MemoryMarshal 有 `read(ReadOnlySpan<Integer>)`（int）与 `read(ReadOnlySpan<Integer>, Class<T>)`（结构体）两个重载。转换器把 `Read<T>` 一律生成 `MemoryMarshal.read(span)`，T=DbRow 时匹配到 int 重载 → 类型错误。修复：MemoryMarshal.Read 特例——T 为非 int/long 等基本类型时附加 `, T.class` 实参。

- **状态**: ✅ Fixed

## Iteration 14 - 无法将方法应用到给定类型 (JsonDocument.TryGetValue 被误当自定义字典 1 参 tryGetValue)
- **Java 文件**: system-text-json/src/main/java/dotnet/text/json/JsonElement.java
- **行号**: 264（另见 297/328/361 等，共 15+ 处）
- **错误信息**: `无法将类 dotnet.text.json.JsonDocument中的方法 tryGetValue应用到给定类型`（`tryGetValue(int)` 单参调用，但重载都是 `tryGetValue(int, holder)`）
- **代码片段**:
  ```java
  return (_parent.tryGetValue(_idx) != null ? ... : ...);   // JsonDocument.tryGetValue 无单参重载
  // 应为 _parent.tryGetValue(_idx, value)（out holder 直传）
  ```
- **对应 C# 文件**: d:\csharpjson\src\System\Text\Json\Document\JsonElement.cs（`return _parent.TryGetValue(_idx, out value);`）
- **根因分类**: Transformer（TryGetValue 重写的 useGeneratedTryGetValue 守卫过宽）
- **涉及组件**: src/CSharpToJava.Core/Transformers/Expression/Transformers/InvocationExpressionTransformer.cs（第 3269-3277 行）
- **分析**: 自定义字典（实现 IDictionary/IReadOnlyDictionary 接口）在 ClassTransformer 中会被合成 1 参 `tryGetValue(K)` 委托方法，因此调用点可以改写为 `tryGetValue(key) != null` 形态。原守卫只检查"接收者声明了 2 参 TryGetValue 且 Java 类型非 Map"，JsonDocument.TryGetValue(int, out byte[]) 恰好也满足 → 被错误改写成单参调用。修复：追加 `IsDictionaryInterfaceImplementer`（接收者实现字典接口）守卫，JsonDocument 走标准 out-holder 调用路径。

- **状态**: ✅ Fixed

## Iteration 15 - 推论变量 T 具有不兼容的上限 (ref MemoryMarshal.GetReference 未包装回 holder)
- **Java 文件**: system-text-json/src/main/java/dotnet/text/json/JsonReaderHelper.cs
- **行号**: 94
- **错误信息**: `不兼容的类型: 推论变量 T 具有不兼容的上限`（`indexOfOrLessThan(MemoryMarshal.getReference(span), ...)` 参数应为 IntHolder）
- **代码片段**:
  ```java
  return indexOfOrLessThan(MemoryMarshal.getReference(span), JsonConstants.Quote & 0xFF, ...);
  // 应为 indexOfOrLessThan(new IntHolder(MemoryMarshal.getReference(span)), ...)
  ```
- **对应 C# 文件**: d:\csharpjson\src\System\Text\Json\Reader\JsonReaderHelper.cs（`return IndexOfOrLessThan(ref MemoryMarshal.GetReference(span), ...)`，参数为 `ref byte searchSpace`）
- **根因分类**: Transformer（ref 复杂表达式回退路径缺少 getReference→holder 重包装）
- **涉及组件**: src/CSharpToJava.Core/Transformers/Expression/Transformers/ArgumentTransformer.cs（TransformSingleArgument ref 分支第 531 行；CoerceArgumentType 第 674-676 行对 ref 参数整体跳过）
- **分析**: compat `MemoryMarshal.getReference(span)` 返回元素值（Integer），而 ref 参数在 Java 侧渲染为 holder（IntHolder）。CoerceArgumentType 的 getReference→holder 重包装（第 1100-1106 行）因开头对 ref/out/in 参数的跳过守卫永远不执行；`ref <invocation>` 落入"复杂表达式按值传递"回退。修复：回退路径中检测 `getReference(` 并包成参数类型的 holder。

- **状态**: ✅ Fixed

## Iteration 16 - 不兼容的类型 (Unsafe.ReadUnaligned<Vector<byte>> 未走 readUnalignedVector)
- **Java 文件**: system-text-json/src/main/java/dotnet/text/json/JsonReaderHelper.java
- **行号**: 244
- **错误信息**: `不兼容的类型: int无法转换为io.github.ningpp.compat.Vector<java.lang.Integer>`
- **代码片段**:
  ```java
  Vector<Integer> vData = Unsafe.readUnaligned(Unsafe.addByteOffset(searchSpace, index));  // 应为 Unsafe.readUnalignedVector(...)
  ```
- **对应 C# 文件**: d:\csharpjson\src\System\Text\Json\Reader\JsonReaderHelper.cs（`Vector<byte> vData = Unsafe.ReadUnaligned<Vector<byte>>(ref Unsafe.AddByteOffset(ref searchSpace, index));`）
- **根因分类**: Transformer（Unsafe.ReadUnaligned<Vector<T>> 无特例，走通用路径）
- **涉及组件**: src/CSharpToJava.Core/Transformers/Expression/Transformers/InvocationExpressionTransformer.cs（Unsafe 特例区，第 4870 行附近）
- **分析**: compat Unsafe 的 `readUnaligned(IntHolder)` 返回 int；`readUnaligned<Vector<byte>>` 的语义由 compat `readUnalignedVector(int)`（标量回退，返回 Vector<Integer>）承担。转换器无 ReadUnaligned 特例 → 生成 `readUnaligned(...)` 赋值给 Vector<Integer>。修复：T 为 Vector 时路由到 `Unsafe.readUnalignedVector(arg)`。

- **状态**: ✅ Fixed

## Iteration 17 - 无法从静态上下文中引用非静态变量 (静态字段初始化器引用运行时 Class 令牌字段)
- **Java 文件**: system-text-json/src/main/java/dotnet/text/json/Serialization/Converters/DictionaryDefaultConverter.java
- **行号**: 51（另见 EnumConverter.java 46/48、KeyValuePairConverter.java 46）
- **错误信息**: `无法从静态上下文中引用非静态 变量 tValueClass`（`private static Class s_valueType = TypeHelper.toWrapperType(tValueClass);`）
- **代码片段**:
  ```java
  private static Class s_valueType = TypeHelper.toWrapperType(tValueClass);  // 静态初始化器引用实例字段 tValueClass
  ```
- **对应 C# 文件**: d:\csharpjson\src\System\Text\Json\Serialization\Converters\DictionaryDefaultConverter.cs（`private static Type s_valueType = typeof(TValue);`；EnumConverter.cs `s_enumTypeCode = Type.GetTypeCode(typeof(T))` 等）
- **根因分类**: Transformer（运行时 Class 令牌字段提升机制未处理静态字段）
- **涉及组件**: src/CSharpToJava.Core/Transformers/Type/ClassTransformer.cs（LiftRuntimeClassFieldInitializers，第 1273-1287 行）
- **分析**: `typeof(T)` 生成对实例运行时 Class 字段 `tClass` 的引用（`TypeHelper.toWrapperType(tClass)`），但 C# 的 `static readonly` 字段初始化器保持 static 修饰，Java 静态初始化器不能引用实例字段。Java 没有 per-closed-type 静态，修复：静态字段若初始化器引用运行时 Class 令牌字段，降级为实例字段并把初始化提升进构造器（委托构造器 this(...) 链已在现有逻辑中跳过提升）。

- **状态**: ✅ Fixed

## Iteration 18 - 无法从类型变量中进行选择 (基类实参 T? 未解包导致 super(T.class))
- **Java 文件**: system-text-json/src/main/java/dotnet/text/json/Serialization/Converters/NullableConverter.java
- **行号**: 38
- **错误信息**: `无法从类型变量中进行选择`（`super(T.class);`）
- **代码片段**:
  ```java
  public NullableConverter(JsonConverter1<T> converter, Class<?> tClass) {
      super(T.class);   // 应为 super(tClass)
  ```
- **对应 C# 文件**: d:\csharpjson\src\System\Text\Json\Serialization\Converters\Value\NullableConverter.cs（`internal class NullableConverter<T> : JsonConverter<T?> where T : struct`）
- **根因分类**: Transformer（ResolveRuntimeTypeArgument 未解包 Nullable 实参）
- **涉及组件**: src/CSharpToJava.Core/Transformers/Utilities/RuntimeClassParameterHelper.cs（ResolveRuntimeTypeArgument(ITypeParameterSymbol, INamedTypeSymbol)）
- **分析**: 基类 `JsonConverter<T?>` 的类型实参是 `Nullable<T>`。基类运行时 Class 参数解析把实参原样交给 ToClassArgument：非类型参数 → 映射为 `T.class` 字面量（T 无界类型变量不可 .class）。EnumConverter 的基类实参是裸 T（类型参数）→ 命中 `TryGetRuntimeClassParameter("T")` → 用 tClass 参数。修复：解析基类类型实参时解包 Nullable，得到裸 T 后走 tClass 参数路径。

- **状态**: ✅ Fixed

## Iteration 19 - 无法将 Object.getClass 应用到给定类型 (Type.GetType(name, bool) 两参重载未处理)
- **Java 文件**: system-text-json/src/main/java/dotnet/text/json/Serialization/IEnumerableConverterFactoryHelpers.java
- **行号**: 229
- **错误信息**: `无法将类 java.lang.Object中的方法 getClass应用到给定类型`（`Class.getClass(name, false)`）
- **代码片段**:
  ```java
  private static Class getTypeIfExists(String name) { return Class.getClass(name, false); }  // java.lang.Class 无静态 getClass
  ```
- **对应 C# 文件**: d:\csharpjson\src\System\Text\Json\Serialization\Converters\Collection\IEnumerableConverterFactoryHelpers.cs（`private static Type? GetTypeIfExists(string name) => Type.GetType(name, false);`）
- **根因分类**: Transformer（Type.GetType 两参重载无特例，方法名被映射为 getClass）
- **涉及组件**: src/CSharpToJava.Core/Transformers/Expression/Transformers/InvocationExpressionTransformer.cs（GetType 特例只覆盖 1 参，第 3054 行）
- **分析**: `Type.GetType(string)`（1 参）→ `Class.forName(...)` 有特例；`Type.GetType(name, false)`（throwOnError 重载）落入通用路径，方法名映射 `GetType→getClass` 产生 `Class.getClass(name, false)`。修复：2 参重载路由到 compat 新增的 `TypeHelper.getType(name, throwOnError)`（catch ClassNotFoundException，throwOnError=false 时返回 null，匹配 C# 语义）。

- **状态**: ✅ Fixed

## Iteration 20 - 不兼容的类型 (new T() 降级为 new Object() 无法转 ReferenceResolver)
- **Java 文件**: system-text-json/src/main/java/dotnet/text/json/Serialization/ReferenceHandler1.java
- **行号**: 28
- **错误信息**: `不兼容的类型: java.lang.Object无法转换为dotnet.text.json.Serialization.ReferenceResolver`（`public ReferenceResolver createResolver() { return new Object(); }`）
- **代码片段**:
  ```java
  public ReferenceResolver createResolver() { return new Object(); }  // 应为 (T) Activator.createInstance(tClass)
  ```
- **对应 C# 文件**: d:\csharpjson\src\System\Text\Json\Serialization\ReferenceHandlerOfT.cs（`public override ReferenceResolver CreateResolver() => new T();`，`where T : ReferenceResolver, new()`）
- **根因分类**: Transformer（new() 约束类型参数实例化无运行时 Class 支持）
- **涉及组件**: src/CSharpToJava.Core/Transformers/Expression/Transformers/ObjectCreationTransformer.cs（第 293 行）；src/CSharpToJava.Core/Transformers/Utilities/RuntimeClassParameterHelper.cs（AddObjectCreationRequirements）
- **分析**: `new T()`（new() 约束）在 Java 无法直接实例化类型参数，原实现降级为 `new Object()`，而方法返回类型是 ReferenceResolver → 赋值失败。修复：AddObjectCreationRequirements 把 new() 约束类型参数的 new T() 记为运行时 Class 需求（生成 tClass 字段/构造参数）；ObjectCreationTransformer 在有 tClass 时发射 `(T) Activator.createInstance(tClass)`（compat Activator 已有 (Class<T>) 重载）。

- **状态**: ✅ Fixed

## Iteration 21 - 未覆盖抽象方法 + createDelegate 约束 + OpCodes 缺失 (ReflectionEmitMemberAccessor 覆盖签名带运行时 Class 参数)
- **Java 文件**: system-text-json/src/main/java/dotnet/text/json/Serialization/ReflectionEmitMemberAccessor.java
- **行号**: 37（另见 62/83/89/111/124/134/144/171/188/194/213/227）
- **错误信息**: `ReflectionEmitMemberAccessor不是抽象的, 并且未覆盖MemberAccessor中的抽象方法<TProperty>createFieldSetter(FieldInfo)`；`无法将...createDelegate应用到给定类型`；`找不到符号`（Unbox_Any）
- **代码片段**:
  ```java
  public <TProperty> BiConsumer<Object, TProperty> createFieldSetter(Class<TProperty> _cs2j_TProperty, FieldInfo fieldInfo) { ... }
  // 抽象基类声明为 <TProperty> ... createFieldSetter(FieldInfo) — 擦除不匹配 → 未覆盖
  ```
- **对应 C# 文件**: d:\csharpjson\src\System\Text\Json\Serialization\ReflectionEmitMemberAccessor.cs（override 方法体使用 typeof(TProperty) 传给 private static 助手）
- **根因分类**: Transformer（override 方法被注入前导 Class<T> 运行时参数，破坏 Java 擦除覆盖匹配）
- **涉及组件**: src/CSharpToJava.Core/Transformers/Member/MethodTransformer.cs（AddPendingRuntimeClassParameters）、src/CSharpToJava.Core/Context/ConversionContext.cs（GetClassLiteral）
- **分析**: 方法级 typeof(TProperty) 会为方法注入前导 `Class<T> _cs2j_T` 参数（AddPendingRuntimeClassParameters）。抽象基类 MemberAccessor 的声明无此参数（基类方法体无 typeof(T)），派生 override 带参数后擦除签名不匹配 → "未覆盖抽象方法"。修复：override/接口实现方法（OverriddenMethod/ExplicitInterfaceImplementations/AllInterfaces 命中）不再注入前导 Class 参数，方法体中的 typeof(T)（方法级 T）降级为 null（反射发射路径本就是 compat stub）。类级 T 不受影响（走 tClass 字段）。

- **状态**: ✅ Fixed

## Iteration 12 - 不兼容的类型 (Span<byte>.ToArray() 扩展方法形式未走 toByteArray)
- **Java 文件**: system-text-json/src/main/java/dotnet/text/json/JsonDocument.java
- **行号**: 1756
- **错误信息**: `不兼容的类型: java.lang.Integer[]无法转换为byte[]`
- **代码片段**:
  ```java
  _data = MemoryExtensions.asSpan(source._data, 0, getLength()).toArray();  // 应为 .toByteArray()
  ```
- **对应 C# 文件**: d:\csharpjson\src\System\Text\Json\Document\JsonDocument.MetadataDb.cs（`_data = source._data.AsSpan(0, Length).ToArray();`，_data 为 byte[]）
- **根因分类**: Transformer（Span ToArray 的 toXxxArray 映射只覆盖实例方法，未覆盖扩展方法绑定）
- **涉及组件**: src/CSharpToJava.Core/Transformers/Expression/Transformers/InvocationExpressionTransformer.cs（第 2691-2710 行）
- **分析**: C# 的 `Span<byte>` 没有实例 ToArray，`.ToArray()` 绑定到 `MemoryExtensions.ToArray<T>(this ReadOnlySpan<T>)` 扩展方法（隐式转换）。转换器 2691 行的 span→toXxxArray 分支要求 `IsExtensionMethod: false`，扩展形式落入 6409 行的通用回退 `{receiver}.toArray()` → compat 的 `ReadOnlySpan<Integer>.toArray()` 返回 Integer[]，赋给 byte[] 失败。修复：同一分支接受"首参数为 ReadOnlySpan<T>/ReadOnlyMemory<T> 的扩展方法"形态，按元素类型发射 `toByteArray()` 等。
