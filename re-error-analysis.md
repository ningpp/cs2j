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