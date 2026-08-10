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
