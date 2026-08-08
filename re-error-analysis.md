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

