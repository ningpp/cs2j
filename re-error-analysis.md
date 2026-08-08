# RegularExpressions C#→Java 转换错误分析

## Iteration 1 — ValueListBuilder compat import 遮蔽（已由转换器修复并随 Iteration 2 一并提交）

- **Java 文件**: dotnet-text-regularexpressions/src/main/java/dotnet/text/regularexpressions/RegexReplacement.java
- **行号**: 58 (Maven 报告) / 97 (javac -Xdiags:verbose 报告)
- **错误信息**: `[58,27] 不兼容的类型: 不存在类型变量T的实例, 以使io.github.ningpp.compat.Span<T>与int一致` (Maven); `[97] Integer[]无法转换为int[]` / `[98] 找不到符号: 方法 close()` (javac)
- **代码片段**:
  ```java
  var strings = new ValueListBuilder<String>(MemoryMarshal.createSpan(stackStrings.Item1, 4));
  var rules = new ValueListBuilder<Integer>(new Span<>(new Integer[64]));
  // ...
  _rules = rules.asSpan().toArray();   // line 97: Integer[] → int[]
  rules.close();                        // line 98: compat has dispose() not close()
  ```
- **对应 C# 文件**: d:\csharp-RegularExpressions\src\System\Text\RegularExpressions\RegexReplacement.cs (lines 32-43, 90-92)
- **根因分类**: 类型映射缺失（TypeMappings.json 配置缺陷）
- **涉及组件**:
  - d:\code\cs2j\config\TypeMappings.json (line 99-105: `System.Collections.Generic.ValueListBuilder\`1` → compat)
  - d:\code\cs2j\src\CSharpToJava.Core\Context\TypeMappingService.cs (MapTypeInternal, AddImportsForType)
- **分析**: TypeMappings.json 将 `System.Collections.Generic.ValueListBuilder\`1` 无条件映射到 `io.github.ningpp.compat.ValueListBuilder` 并添加 compat import。但本项目 C# 源码包含自己的 `ValueListBuilder.cs`（src\Common\System\Collections\Generic\ValueListBuilder.cs），转换器会将其转成 Java 类 `System.Collections.Generic.ValueListBuilder`。compat import（单类型导入）遮蔽了项目自己生成的同名类，导致：(1) 调用 `close()`（生成的类有，compat 用 `dispose()`）找不到；(2) compat 多出的 `(int)` 构造函数在 Maven 编译时干扰 `var` 泛型推断，产生 line 58 错误。修复：当类型符号在项目源码中定义（`Locations.Any(loc => loc.IsInSource)`）时，跳过 compat 类型映射的 import 添加，让按需导入 `import System.Collections.Generic.*;` 解析到项目自己生成的类。

## Iteration 2 — 找不到符号 thisRef（lambda 参数遮蔽元组元素名解析）

- **Java 文件**: dotnet-text-regularexpressions/src/main/java/dotnet/text/regularexpressions/RegexReplacement.java
- **行号**: 171（以及 187）
- **错误信息**: `找不到符号 符号: 变量 thisRef 位置: 类型为T的变量 value`
- **代码片段**:
  ```java
  state_1.value.thisRef.replacementImpl(state_1.value._2(), match);
  ```
  （`state.thisRef` 的命名元组元素未被映射为 `._1()`，而 `state.segments` 正确映射为 `._2()`）
- **对应 C# 文件**: d:\csharp-RegularExpressions\src\System\Text\RegularExpressions\RegexReplacement.cs（第 208、227 行）
- **根因分类**: Transformer 逻辑缺陷（ConversionContext.LocalTypeOverrides 遮蔽）
- **涉及组件**:
  - src/CSharpToJava.Core/Transformers/Expression/Transformers/IdentifierExpressionTransformer.cs（ResolveReceiverType、TryGetSimpleIdentifierReceiverType、新增 TryGetLambdaParameterType）
  - src/CSharpToJava.Core/Context/ConversionContext.cs（LocalTypeOverrides 字典）
- **分析**: 外层 `var state = (replacement: this, ...)` 的类型被记录进 `LocalTypeOverrides`；lambda 参数 `(ref (RegexReplacement thisRef, ...) state, ...)` 与它同名并遮蔽外层局部后，`ResolveReceiverType` 先命中 `LocalTypeOverrides` 返回外层元组类型（元素名 replacement/segments/...），导致 `state.thisRef` 的命名元素查找失败而原样输出。修复：`LocalTypeOverrides` 命中时若标识符经 Roslyn 绑定为参数符号，则优先采用参数的权威类型。
- **测试**: `RefLambdaParameterProjectTests.ProjectPath_TupleElementMemberCall_RefParamElementNameMismatch`（先红后绿，全量 2476 通过）
- **状态**: ✅ Fixed


## Iteration 3 — 不兼容的类型: char无法转换为String（TextInfo.ToLower(char) 缺少 compat 重载）

- **Java 文件**: dotnet-text-regularexpressions/src/main/java/dotnet/text/regularexpressions/RegexBoyerMoore.java
- **行号**: 270（以及 317、341）
- **错误信息**: `不兼容的类型: char无法转换为java.lang.String`
- **代码片段**:
  ```java
  if (Pattern.charAt(i) != textinfo.toLowerCase(text.charAt(index + i))) {
  ```
- **对应 C# 文件**: d:\csharp-RegularExpressions\src\System\Text\RegularExpressions\RegexBoyerMoore.cs（第 245、305、334 行 `culture.TextInfo.ToLower(char)`）
- **根因分类**: 语义模型信息丢失（compat API 缺口）
- **涉及组件**:
  - java/csharptojava-compat/src/main/java/io/github/ningpp/compat/TextInfo.java
  - src/CSharpToJava.Core/Transformers/Expression/Transformers/InvocationExpressionTransformer.cs（方法名映射 ToLower → toLowerCase）
- **分析**: 转换器将 `ToLower` 统一映射为 `toLowerCase`，但 compat `TextInfo` 只有 `toLowerCase(String)` 与 `toLower(char)`，没有 `toLowerCase(char)`；`textinfo.ToLower(text[i])` 生成的 `toLowerCase(char)` 调用解析到 String 重载导致 char→String 编译错误。修复：在 compat TextInfo 中补充 `toLowerCase(char)`/`toUpperCase(char)`（镜像 C# ToLower(char)/ToUpper(char)）。
- **测试**: `TextInfoCompatibilityTests.TextInfoToLowerChar_CompilesAgainstCompatRuntime`（先红后绿，javac 对生成的 Java + compat 源码编译验证）
- **状态**: ✅ Fixed

## Iteration 4 — 条件表达式类型错误: String无法转换为ReadOnlySpan<Character>（隐式 string→span 转换缺失）- **Java 文件**: dotnet-text-regularexpressions/src/main/java/dotnet/text/regularexpressions/RegexCharClass.java
- **行号**: 326（以及 333、340）
- **错误信息**: `[326,26] 不兼容的类型: 条件表达式中的类型错误; String无法转换为ReadOnlySpan<Character>`
- **代码片段**:
  ```java
  public void addWord(boolean ecma, boolean negate) {
      if (ecma) {
          addSet((negate ? NotECMAWordSet : ECMAWordSet));
      } else {
          addCategory((negate ? NotWord : Word));
      }
  }
  ```
- **对应 C# 文件**: d:\csharp-RegularExpressions\src\System\Text\RegularExpressions\RegexCharClass.cs（第 639、651、663 行 `AddSet(negate ? NotECMAWordSet : ECMAWordSet);`，`AddSet` 声明为 `private void AddSet(ReadOnlySpan<char> set)`）
- **根因分类**: Transformer 逻辑缺陷
- **涉及组件**: src/CSharpToJava.Core/Transformers/Expression/Utilities/ExpressionTransformerHelpers.cs（AdaptExpressionToTargetTypeCore）
- **分析**: C# 中 `string` 可隐式转换为 `ReadOnlySpan<char>`，但 `AdaptExpressionToTargetTypeCore` 只处理了 `T[] → Span<T>/ReadOnlySpan<T>`（`new ReadOnlySpan<>(...)`），没有处理 `String → ReadOnlySpan<Character>/Span<Character>`（应包 `MemoryExtensions.asSpan(...)`）。方法实参经由 ArgumentTransformer.CoerceArgumentType → AdaptExpressionToTargetType 的路径因此不包装字符串，生成的 `addSet("...")` 无法编译。显式 cast 路径（`(ReadOnlySpan<char>)s`）已有处理，隐式路径缺失。
- **测试**: `StringArgumentToSpanParameterTests`（先红后绿）
- **状态**: ✅ Fixed

## Iteration 5 — 无法将 asSpan 应用到给定类型（compat 缺 2 参重载）

- **Java 文件**: dotnet-text-regularexpressions/src/main/java/dotnet/text/regularexpressions/RegexCharClass.java
- **行号**: 357（以及 514、912 中的 `MemoryExtensions.asSpan(set, 1)`）
- **错误信息**: `[357,25] 无法将类 MemoryExtensions 中的方法 asSpan 应用到给定类型; 需要: T[]; 找到: String,int`
- **代码片段**:
  ```java
  MemoryExtensions.asSpan(state._1(), 2).copyTo(span.slice(SetStartIndex));
  ```
- **对应 C# 文件**: d:\csharp-RegularExpressions\src\System\Text\RegularExpressions\RegexCharClass.cs（第 689 行 `state.set.AsSpan(2).CopyTo(...)`、第 898 行 `set1.AsSpan(1)`）
- **根因分类**: 语义模型信息丢失（compat API 缺口）
- **涉及组件**: java/csharptojava-compat/src/main/java/io/github/ningpp/compat/MemoryExtensions.java
- **分析**: C# `string.AsSpan(int)`（从 start 切片到末尾）被转换器发射为 `MemoryExtensions.asSpan(String, int)`，但 compat 只有 1 参与 3 参重载。修复：补充 `asSpan(String,int)`（以及对称的 `asSpan(char[],int)`、`asSpan(byte[],int)`、`asMemory(String,int)`），2 参重载委托 3 参实现。
- **测试**: `MemoryExtensionsAsSpanStartTests.StringAsSpanStart_CompilesAgainstCompatRuntime`（先红后绿，javac 对 compat 源码 + API 检查代码编译验证）
- **状态**: ✅ Fixed

## Iteration 6 — 找不到符号 fromValue(int)（compat UnicodeCategory 缺 C# 枚举强转入口）

- **Java 文件**: dotnet-text-regularexpressions/src/main/java/dotnet/text/regularexpressions/RegexCharClass.java
- **行号**: 402（以及 409、708、719、742、751）
- **错误信息**: `[402,41] 找不到符号 符号: 方法 fromValue(int) 位置: 类 UnicodeCategory`
- **代码片段**:
  ```java
  category.value = UnicodeCategory.fromValue((int)((c - 1)));
  ```
- **对应 C# 文件**: d:\csharp-RegularExpressions\src\System\Text\RegularExpressions\RegexCharClass.cs（第 763、772、1226、1241、1271、1284 行 `(UnicodeCategory)(c - 1)` 等枚举强转）
- **根因分类**: 语义模型信息丢失（compat API 缺口）
- **涉及组件**: java/csharptojava-compat/src/main/java/io/github/ningpp/compat/UnicodeCategory.java
- **分析**: 转换器将 C# 整型→枚举强转发射为 `{EnumType}.fromValue(...)`（AdaptExpressionToTargetTypeCore 枚举规则），但 compat UnicodeCategory 只有 `of(int)`（Java Character 类别映射）。修复：新增 `fromValue(int)` 按 .NET UnicodeCategory 序号取值（越界回落 OtherNotAssigned）。
- **测试**: `UnicodeCategoryFromValueTests.UnicodeCategoryEnumCast_CompilesAgainstCompatRuntime`（先红后绿）
- **状态**: ✅ Fixed

## Iteration 7 — byte[]无法转换为ReadOnlySpan<Integer>（表达式体局部函数未做目标类型适配）

- **Java 文件**: dotnet-text-regularexpressions/src/main/java/dotnet/text/regularexpressions/RegexCharClass.java
- **行号**: 597
- **错误信息**: `[597,65] 不兼容的类型: byte[]无法转换为io.github.ningpp.compat.ReadOnlySpan<java.lang.Integer>`
- **代码片段**:
  ```java
  private static ReadOnlySpan<Integer> asciiLookup() { return new byte[] { (byte)0x00, ... }; }
  ```
- **对应 C# 文件**: d:\csharp-RegularExpressions\src\System\Text\RegularExpressions\RegexCharClass.cs（第 1044 行 `static ReadOnlySpan<byte> AsciiLookup() => new byte[] {...}`，为局部函数）
- **根因分类**: Transformer 逻辑缺陷
- **涉及组件**: src/CSharpToJava.Core/Transformers/Member/MethodTransformer.cs（TransformLocalFunction 表达式体路径）
- **分析**: 普通表达式体方法（MethodTransformer 第 349 行）会调用 `AdaptExpressionToTargetType` 做 byte[]→ReadOnlySpan 包装，但局部函数表达式体路径（TransformLocalFunction）直接透传表达式，漏掉适配，导致原始 byte[] 被返回。修复：局部函数表达式体同样应用 WrapExpressionBodyForIterableReturn + AdaptExpressionToTargetType。
- **测试**: `LocalFunctionExpressionBodySpanAdaptationTests`（先红后绿）
- **状态**: ✅ Fixed

## Iteration 8 — 找不到符号 Interlocked.or(IntHolder,int)（compat 缺 ref-holder 位运算重载）

- **Java 文件**: dotnet-text-regularexpressions/src/main/java/dotnet/text/regularexpressions/RegexCharClass.java
- **行号**: 630
- **错误信息**: `[630,20] 找不到符号 符号: 方法 or(IntHolder,int) 位置: 类 Interlocked`
- **代码片段**:
  ```java
  io.github.ningpp.compat.IntHolder _slotRef = new io.github.ningpp.compat.IntHolder(slot);
  Interlocked.or(_slotRef, bitsToSet);
  slot = _slotRef.value;
  ```
- **对应 C# 文件**: d:\csharp-RegularExpressions\src\System\Text\RegularExpressions\RegexCharClass.cs（第 1118 行 `Interlocked.Or(ref slot, bitsToSet);`）
- **根因分类**: 语义模型信息丢失（compat API 缺口）
- **涉及组件**: java/csharptojava-compat/src/main/java/io/github/ningpp/compat/Interlocked.java
- **分析**: 转换器将 C# `Interlocked.Or(ref int, int)` 经 ref-holder 模式发射为 `Interlocked.or(IntHolder, int)`，但 compat 只有 increment/decrement 的 holder 重载。修复：补充 `or(IntHolder,int)` 与对称的 `and(IntHolder,int)`。
- **测试**: `InterlockedOrCompatTests.InterlockedOr_CompilesAgainstCompatRuntime`（先红后绿）
- **状态**: ✅ Fixed

## Iteration 9 — 找不到符号 Character.getUnicodeCategory（char 静态方法未映射到 compat helper）

- **Java 文件**: dotnet-text-regularexpressions/src/main/java/dotnet/text/regularexpressions/RegexCharClass.java
- **行号**: 687
- **错误信息**: `[687,47] 找不到符号 符号: 方法 getUnicodeCategory(char) 位置: 类 Character`
- **代码片段**:
  ```java
  UnicodeCategory chcategory = Character.getUnicodeCategory(ch);
  ```
- **对应 C# 文件**: d:\csharp-RegularExpressions\src\System\Text\RegularExpressions\RegexCharClass.cs（第 1199 行 `UnicodeCategory chcategory = char.GetUnicodeCategory(ch);`）
- **根因分类**: Transformer 逻辑缺陷（预定义类型静态接收者路径短路配置映射）
- **涉及组件**: src/CSharpToJava.Core/Transformers/Expression/Transformers/InvocationExpressionTransformer.cs（预定义类型静态调用路径）；config/TypeMappings.json
- **分析**: `char.xxx` 静态调用走预定义类型路径（`{boxedReceiver}.{mappedMethod}`），在 TypeMappings 查找之前短路，`GetUnicodeCategory` 落入通用 camelCase 规则产出 `Character.getUnicodeCategory`，而 java.lang.Character 无此方法。修复：在预定义类型路径特判 `char.GetUnicodeCategory` → `CharUnicodeInfo.getUnicodeCategory(...)`（compat helper 返回 C# UnicodeCategory 枚举），并在 TypeMappings.json 为 System.Char/char 补充同名映射条目。
- **测试**: `CharGetUnicodeCategoryMappingTests`（先红后绿）
- **状态**: ✅ Fixed

## Iteration 10 — 找不到符号 getLength()（StringBuilder.Length 属性未映射为 length()）

- **Java 文件**: dotnet-text-regularexpressions/src/main/java/dotnet/text/regularexpressions/RegexCharClass.java
- **行号**: 798
- **错误信息**: `[798,66] 找不到符号 符号: 方法 getLength()`
- **代码片段**:
  ```java
  int categoriesLength = (_categories != null ? _categories.getLength() : 0);
  ```
- **对应 C# 文件**: d:\csharp-RegularExpressions\src\System\Text\RegularExpressions\RegexCharClass.cs（第 1354 行 `int categoriesLength = _categories?.Length ?? 0;`，`_categories` 为 StringBuilder）
- **根因分类**: 类型映射缺失（TypeMappings.json 配置缺陷）
- **涉及组件**: config/TypeMappings.json（System.Text.StringBuilder 条目）
- **分析**: System.Text.StringBuilder 映射到 java.lang.StringBuilder，其长度属性是 `length()` 方法，但 TypeMappings 缺少 `Length`/`get_Length` 条目，属性访问按默认规则发射为 `getLength()`。修复：为 System.Text.StringBuilder 补充 `get_Length`/`Length` → `length` 映射（与 Capacity → capacity 条目一致）。
- **测试**: `StringBuilderLengthMappingTests`（先红后绿）
- **状态**: ✅ Fixed

## Iteration 11 — 找不到符号 getChunks()（StringBuilder.GetChunks 无 Java 等价物）

- **Java 文件**: dotnet-text-regularexpressions/src/main/java/dotnet/text/regularexpressions/RegexCharClass.java
- **行号**: 820
- **错误信息**: `[820,59] 找不到符号 符号: 方法 getChunks()`
- **代码片段**:
  ```java
  for (ReadOnlyMemory<Character> chunk : _categories.getChunks()) {
      vsb.append(chunk.getSpan());
  }
  ```
- **对应 C# 文件**: d:\csharp-RegularExpressions\src\System\Text\RegularExpressions\RegexCharClass.cs（第 1383 行 `foreach (ReadOnlyMemory<char> chunk in _categories!.GetChunks())`）
- **根因分类**: Transformer 逻辑缺陷（C# 专属 API 缺 Java 等价物路由）
- **涉及组件**: src/CSharpToJava.Core/Transformers/Expression/Transformers/InvocationExpressionTransformer.cs；java/csharptojava-compat/src/main/java/io/github/ningpp/compat/StringBuilderExtensions.java（新增）
- **分析**: System.Text.StringBuilder 映射到 java.lang.StringBuilder，没有 `getChunks()`。修复：新增 compat `StringBuilderExtensions.getChunks(StringBuilder)` 返回覆盖整个内容的单个 ReadOnlyMemory 分块；转换器对 StringBuilder 接收者的 `GetChunks()` 调用（语义模型守卫）重写为 `StringBuilderExtensions.getChunks(...)` 并加 import。循环体 `vsb.append(chunk.getSpan())` 绑定到 `append(Object)`（ReadOnlySpan.toString 返回字符内容，语义等价）。
- **测试**: `StringBuilderGetChunksTests`（先红后绿，javac 编译验证）
- **状态**: ✅ Fixed

## Iteration 12 — 条件表达式类型错误（目标类型化条件表达式到 ReadOnlySpan 分支未适配）

- **Java 文件**: dotnet-text-regularexpressions/src/main/java/dotnet/text/regularexpressions/RegexCharClass.java
- **行号**: 912/913
- **错误信息**: `[912,27] 不兼容的类型: 条件表达式中的类型错误; [912,111] 不兼容的类型: 条件表达式中的类型错误`
- **代码片段**:
  ```java
  return (!invert ? set : (set.charAt(0) == NullChar ? MemoryExtensions.asSpan(set, 1) : NullCharString + set));
  ```
- **对应 C# 文件**: d:\csharp-RegularExpressions\src\System\Text\RegularExpressions\RegexCharClass.cs（第 1513 行附近 `return !invert ? set : set[0] == NullChar ? set.AsSpan(1) : NullCharString + set;`，SetFromProperty 返回 ReadOnlySpan<char>）
- **根因分类**: Transformer 逻辑缺陷（目标类型化条件表达式的分支未按 span 目标适配）
- **涉及组件**: src/CSharpToJava.Core/Transformers/Expression/Transformers/ControlFlowTransformer.cs（TransformConditional）；src/CSharpToJava.Core/Transformers/Expression/Utilities/ExpressionTransformerHelpers.cs（string→span 规则跳过条件表达式，防双重包装）
- **分析**: C# 目标类型化条件表达式（ConvertedType 为 ReadOnlySpan<char>）中 string 分支隐式转换为 span，但 Java 三元两个分支类型必须一致；TransformConditional 只对枚举/Decimal 做了分支适配，没有处理 span 目标。修复：当条件表达式 ConvertedType 是 Span/ReadOnlySpan 时对每个分支调用 AdaptExpressionToTargetType（string 分支包 MemoryExtensions.asSpan）。同时修复实参 string→span 规则对整体三元表达式的重复包装（asSpan(cond) 分支类型不兼容）。
- **测试**: `ConditionalSpanBranchAdaptationTests`（先红后绿）
- **状态**: ✅ Fixed

## Iteration 13 — 找不到符号 Class.makeGenericMethodParameter/makeGenericType（反射元数据擦除发射）

- **Java 文件**: dotnet-text-regularexpressions/src/main/java/dotnet/text/regularexpressions/RegexCompiler.java
- **行号**: 76-79、82
- **错误信息**: `[76,179] 找不到符号 符号: 方法 makeGenericMethodParameter(int) 位置: 类 Class`
- **代码片段**:
  ```java
  private static final MethodInfo s_spanIndexOf = TypeHelper.getMethod(MemoryExtensions.class, "indexOf",
      new Class[] { ReadOnlySpan.class.makeGenericType(Class.makeGenericMethodParameter(0)), Class.makeGenericMethodParameter(0) })
      .makeGenericMethod(Character.class);
  ```
- **对应 C# 文件**: d:\csharp-RegularExpressions\src\System\Text\RegularExpressions\RegexCompiler.cs（第 59-65 行 `typeof(ReadOnlySpan<>).MakeGenericType(Type.MakeGenericMethodParameter(0))` 反射元数据）
- **根因分类**: Transformer 逻辑缺陷（.NET 反射构造型 API 无 Java 等价物）
- **涉及组件**: src/CSharpToJava.Core/Transformers/Expression/Transformers/InvocationExpressionTransformer.cs
- **分析**: `Type.MakeGenericMethodParameter(i)` 被发射为 `Class.makeGenericMethodParameter(i)`、`typeof(X<>).MakeGenericType(T)` 被发射为 `X.class.makeGenericType(...)`，两个方法在 java.lang.Class 上都不存在（内层符号错误遮蔽了外层 makeGenericType 错误）。修复：由于 Java `Class.getMethod` 按擦除匹配，转换器对这两类调用发射擦除形态——方法类型变量发射 `Object.class`，MakeGenericType 调用剥离为裸 `X.class` 字面量。
- **测试**: `TypeReflectionMetadataErasureTests`（先红后绿）
- **状态**: ✅ Fixed

## Iteration 14 — 无法推断本地变量 _last 的类型（LINQ 重写生成的 var + default(T) → null）

- **Java 文件**: dotnet-text-regularexpressions-tests/src/test/java/System/Diagnostics/DebuggerAttributes.java
- **行号**: 200
- **错误信息**: `[200,13] 无法推断本地变量 _last 的类型 (变量初始化程序为 'null')`
- **代码片段**:
  ```java
  static CustomAttributeData getDebuggerBrowsableState_ProceduralLinq1(CSharpGenericIterable<CustomAttributeData> _linqitems, MemberInfo info) {
      ...
      var _last = null;      // ← line 200
      var _found = false;
      ...
      _last = _linqitem;
      return _last;
  ```
- **对应 C# 文件**: d:\csharp-RegularExpressions\tests\TestUtilities\System\Diagnostics\DebuggerAttributes.cs（第 63 行 `info.Attributes.SingleOrDefault(a => a.AttributeType == typeof(DebuggerBrowsableAttribute))`）
- **根因分类**: Transformer 逻辑缺陷（var 类型推断未识别 default(T) 引用类型会发射 null）
- **涉及组件**: src/CSharpToJava.Core/Transformers/Statement/StatementTransformer.Declarations.cs（TransformLocalDeclaration）
- **分析**: LINQ 重写器将 SingleOrDefault/FirstOrDefault/LastOrDefault 等重写为 `var _last = default(T)`（LinqRewriter.cs:1570 CreateLocalVariableDeclaration）。声明变换器只把 `NullLiteralExpression` 识别为"var 无法推断"的初始化器（原 hasNullLiteralInitializer），而 `default(CustomAttributeData)`（DefaultExpressionSyntax，引用类型经 TypeOperationTransformer.GetDefaultValueForType 发射 null）被漏掉，且 `wasImplicitVar && !hasNoInitializer` 路径把已解析类型强制回写为 var，最终输出 `var _last = null;` 导致 javac 报错。
- **修复**: 新增 `InitializerEmitsNull` 帮助方法——`null` 字面量，或 `default(T)` 且 T 为引用类型/Nullable<T>/仅引用约束类型参数时视为发射 null；null 发射初始化器时跳过"回写 var"并保留解析出的具体类型（`CustomAttributeData _last = null;`），纯 null 字面量（无解析类型）仍回退 `Object`。一处修复覆盖所有 LINQ 重写的 _last/_acc/bestItem_ 声明。
- **测试**: `TestModuleBatch3Tests.VarWithDefaultReferenceTypeInitializer_KeepsResolvedType`、`SingleOrDefaultRewrittenLast_NullInitializerTyped`（先红后绿，全量 2525 通过；1 个存量 TypeScript CLI 测试失败与本次无关）
- **状态**: ✅ Fixed

## Iteration 15 — 意外的类型: catch (T ex)（Java 禁止 catch 类型变量）

- **Java 文件**: dotnet-text-regularexpressions-tests/src/test/java/System/Tests/AssertExtensions.java
- **行号**: 36（以及 46、54、68、76 共 5 处）
- **错误信息**: `[36,18] 意外的类型 需要: 类 找到: 类型参数T`
- **代码片段**:
  ```java
  public static <T extends ArgumentException> T throwsValue(Class<T> _cs2j_T, String paramName, Runnable testCode) {
      try {
          testCode.run();
      } catch (T ex) {          // ← line 36: Java 禁止 catch 类型变量
          return ex;
      }
      throw new ArgumentException("...");
  }
  ```
- **对应 C# 文件**: d:\csharp-RegularExpressions\tests\TestUtilities\System\Tests\AssertExtensions.cs（`catch (T ex)` 出现在 Throws<T> 的 5 个重载，T 有 ArgumentException/Exception 约束）
- **根因分类**: Transformer 逻辑缺陷（catch 类型为类型参数时无降级）
- **涉及组件**: src/CSharpToJava.Core/Transformers/Statement/StatementTransformer.SwitchAndResource.cs（TransformTryStatement）；src/CSharpToJava.Core/Java/JavaStatement.cs（JavaCatchClause）
- **分析**: C# 允许 `catch (T)`（T 有 Exception 约束），Java 禁止 catch 类型变量。转换器对 catch 类型只做 MapType（类型参数原样输出 "T"）。修复：检测 catch 类型为 ITypeParameterSymbol 时，(1) 取第一个 class 约束作为 catch 类型（System.ArgumentException → compat ArgumentException，System.Exception → RuntimeException）；(2) 用 `ConversionContext.GetClassLiteral` 注册并取得 `Class<T>` token（`_cs2j_T`），catch 子句整段以 JavaCatchClause.RawOverride 原样发射：`catch (约束 _cs2j_xCatch) { if (!_cs2j_T.isInstance(_cs2j_xCatch)) throw ...; T ex = (T) _cs2j_xCatch; <body 原样> }`——主体引用绑定到 T 类型局部，保持 C# catch(T) 语义（不匹配的异常继续传播）。主体用原始语法树变换（不做语法重命名），避免语义模型节点越界异常；filter 时 body 包 if(filter)。
- **测试**: `ExceptionCatchOrderTests.TypeParameterCatch_UsesConstraintTypeWithRuntimeGuard`（先红后绿，全量 2526 通过）
- **状态**: ✅ Fixed

## Iteration 16 — 找不到符号 getParamName()（ArgumentOutOfRangeException 映射到 java.lang.IllegalArgumentException）

- **Java 文件**: dotnet-text-regularexpressions-tests/src/test/java/System/Tests/AssertExtensions.java
- **行号**: 73（以及 110、121 的下游错误）
- **错误信息**: `[73,83] 找不到符号 符号: 方法 getParamName() 位置: 类型为java.lang.IllegalArgumentException的变量 aore`
- **代码片段**:
  ```java
  if (ex instanceof IllegalArgumentException aore && Objects.equals(aore.getParamName(), paramName)) {
  ```
- **对应 C# 文件**: d:\csharp-RegularExpressions\tests\TestUtilities\System\Tests\AssertExtensions.cs（第 49 行 `ex is ArgumentOutOfRangeException aore && aore.ParamName == paramName`）
- **根因分类**: 类型映射缺失（TypeMappings.json 配置缺陷）
- **涉及组件**: config/TypeMappings.json（System.ArgumentOutOfRangeException 条目）；src/CSharpToJava.Core/Java/Rewriters/ExceptionApiRewriter.cs；java/csharptojava-compat（新增 ArgumentOutOfRangeException.java）
- **分析**: TypeMappings 把 System.ArgumentOutOfRangeException 映射到 java.lang.IllegalArgumentException（Java 标准类无 getParamName），导致 `aore.ParamName` 发射的 `getParamName()` 编译失败。修复：(1) compat 新增 `ArgumentOutOfRangeException extends ArgumentException`（保持 .NET 层次，继承 ParamName 支持，提供 (String)、(String,String) 等构造器）；(2) TypeMappings 改为映射到 compat 类；(3) 从 ExceptionApiRewriter.ExceptionTypeMap 删除 ArgumentOutOfRangeException→IndexOutOfBoundsException（否则 new/catch 会被改写成丢失 ParamName 的 Java 标准类）。全项目仅 ThrowHelper 的 `new` 使用受影响，1 参 String 构造器兼容。
- **测试**: `ExceptionCatchOrderTests.ArgumentOutOfRangeException_Pattern_CompilesAgainstCompatRuntime`（先红后绿，javac 对生成的 Java + compat 源码编译验证，全量 2527 通过）
- **状态**: ✅ Fixed

## Iteration 17 — 找不到符号 isInstanceOfType（Type.IsInstanceOfType 未映射到 Class.isInstance）

- **Java 文件**: dotnet-text-regularexpressions-tests/src/test/java/System/Tests/AssertExtensions.java
- **行号**: 111
- **错误信息**: `[111,30] 找不到符号 符号: 方法 isInstanceOfType(java.lang.RuntimeException) 位置: 类型为java.lang.Class的变量 exceptionType`
- **代码片段**:
  ```java
  if (exceptionType.isInstanceOfType(ex)) {
  ```
- **对应 C# 文件**: d:\csharp-RegularExpressions\tests\TestUtilities\System\Tests\AssertExtensions.cs（第 90 行 `exceptionType.IsInstanceOfType(ex)`）
- **根因分类**: Transformer 逻辑缺陷（System.Type 接收者方法缺 Java 等价物路由）
- **涉及组件**: src/CSharpToJava.Core/Transformers/Expression/Transformers/InvocationExpressionTransformer.cs（IsSubclassOf 分支旁）
- **分析**: System.Type → java.lang.Class 的映射已有 IsSubclassOf → isAssignableFrom 的模板，但 IsInstanceOfType 没有对应分支，落入通用 camelCase 产出 `isInstanceOfType`（java.lang.Class 无此方法）。修复：新增 IsInstanceOfType → `{receiver}.isInstance({arg})` 分支（与 IsSubclassOf 相同的守卫条件：methodSymbol 属于 System.Type 或 IsReceiverOfType 命中）。
- **测试**: `TestModuleBatch3Tests.TypeIsInstanceOfType_MapsToIsInstance`（先红后绿，全量 2528 通过）
- **状态**: ✅ Fixed

## Iteration 18 — RuntimeException 无法安全地转换为 T1（instanceof 类型参数非法）

- **Java 文件**: dotnet-text-regularexpressions-tests/src/test/java/System/Tests/AssertExtensions.java
- **行号**: 122
- **错误信息**: `[122,18] java.lang.RuntimeException 无法安全地转换为 T1`（及 122,40 的 T2）
- **代码片段**:
  ```java
  public static <T1 extends RuntimeException, T2 extends RuntimeException> RuntimeException throwsAny(Class<T2> _cs2j_T2, Class<T1> _cs2j_T1, Runnable testCode) {
      ...
      if ((ex instanceof T1) || (ex instanceof T2)) {
  ```
- **对应 C# 文件**: d:\csharp-RegularExpressions\tests\TestUtilities\System\Tests\AssertExtensions.cs（第 107 行 `if (ex is T1 || ex is T2)`，ThrowsAny<T1,T2>）
- **根因分类**: Transformer 逻辑缺陷（is 类型参数无 Java 等价物路由）
- **涉及组件**: src/CSharpToJava.Core/Transformers/Expression/Transformers/TypeOperationTransformer.cs（TransformIs 759-778、TransformToIR IsExpression 分支 84-99；并统一 TypePattern 发射）
- **分析**: `ex is T1` 在 Roslyn 中解析为 BinaryExpressionSyntax(IsExpression)（非 IsPatternExpression），走 TransformIs/TransformToIR 发射 `ex instanceof T1`，而 Java 禁止 instanceof 类型变量（T1 擦除为 RuntimeException，报"无法安全地转换为"）。修复：三处发射点统一处理 ITypeParameterSymbol —— TransformIs 与 TransformToIR 的 IsExpression 分支、以及 TransformTypePattern（真 TypePattern 语境）在模式类型为类型参数时发射 `_cs2j_T1.isInstance(ex)`（GetClassLiteral 自动注册 Class<T> 签名参数）；具体类型保持 instanceof。
- **测试**: `ExceptionCatchOrderTests.IsTypeParameterPattern_UsesRuntimeClassToken`（先红后绿，全量 2529 通过）
- **状态**: ✅ Fixed