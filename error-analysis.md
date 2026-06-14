# Error Analysis Log

## Iteration 1 — CopyTo mis-translated as arraycopy on non-collection type

- **Java 文件**: `System.Private.Xml/src/main/java/dotnet/xml/XmlTextReaderImpl.java`
- **行号**: 1908
- **错误信息**:
  1. `找不到符号: 方法 size()` — 位置: `NodeData` 类型变量 `_curNode`
  2. `找不到符号: 方法 toArray()` — 位置: `NodeData` 类型变量 `_curNode`
- **代码片段**:
  ```java
  System.arraycopy(_curNode.toArray(), 0, 0, sb, _curNode.size());
  ```

- **对应 C# 文件**: `System/Xml/Core/XmlTextReaderImplHelpers.cs`
- **C# 原始代码**: `_curNode.CopyTo(0, sb);`
- **根因分类**: Transformer 逻辑缺陷
- **涉及组件**: `src/CSharpToJava.Core/Transformers/Expression/Transformers/InvocationExpressionTransformer.cs:2025-2037`
- **分析**: 转换器在 `InvocationExpressionTransformer` 中对所有名为 `CopyTo` 且参数为 2 个的方法调用都套用了 `ICollection<T>.CopyTo(array, index)` → `System.arraycopy(...toArray()..., ...size())` 的模式。但 `NodeData` 类有自己的 `CopyTo(int valueOffset, StringBuilder sb)` 方法，不是集合类型的 CopyTo。转换器未检查方法所属类型是否为集合/数组类型，导致对自定义类型的 CopyTo 方法也错误地生成 `toArray()` 和 `size()` 调用。

✅ **Fixed** — commit `c7c79d6`: Added method symbol check to verify first parameter is an array type before applying CopyTo→arraycopy rewrite.

---

## Iteration 2 — new UTF8Encoding/ASCIIEncoding produces wrong type (Charset vs Encoding)

- **Java 文件**: `System.Private.Xml/src/main/java/dotnet/xml/XmlTextReaderImpl.java`
- **行号**: 2393, 2469, 2519
- **错误信息**: `不兼容的类型: java.nio.charset.Charset无法转换为io.github.ningpp.compat.Encoding`
- **代码片段**:
  ```java
  return StandardCharsets.UTF_8;  // line 2393 — in method returning Encoding
  newEncoding = getUTF8BomThrowing();  // line 2469 — returns Charset, assigned to Encoding
  switchEncoding(getUTF8BomThrowing());  // line 2519 — Charset passed where Encoding expected
  ```

- **对应 C# 文件**: `System/Xml/Core/XmlTextReaderImpl.cs`
- **C# 原始代码**:
  ```csharp
  return new UTF8Encoding(true, true);  // line 3158 — in method returning Encoding
  ```
- **根因分类**: 类型映射缺失 + Transformer 逻辑缺陷
- **涉及组件**: 
  - `config/TypeMappings.json:3579-3592` — UTF8Encoding/ASCIIEncoding mapped to Charset
  - `src/CSharpToJava.Core/Transformers/Expression/Transformers/ObjectCreationTransformer.cs:208-215`
- **分析**: `System.Text.UTF8Encoding` 继承自 `System.Text.Encoding`，但类型映射将其映射为 `Charset`（不是 `Encoding` 的子类型）。`ObjectCreationTransformer` 对 `new UTF8Encoding(true, true)` 生成 `StandardCharsets.UTF_8`（返回 `Charset`），但调用方期望 `Encoding` 类型（compat 层的 `io.github.ningpp.compat.Encoding`）。

✅ **Fixed** — commit `479435b`: Changed ObjectCreationTransformer to generate `Encoding.getUTF8()`/`Encoding.getASCII()`, and updated TypeMappings for UTF8Encoding/ASCIIEncoding to map to `Encoding` instead of `Charset`.

---

## Iteration 3 — Decoder inner class type mismatch

- **Java 文件**: `System.Private.Xml/src/main/java/dotnet/xml/XmlTextReaderImpl.java`
- **行号**: 2419
- **错误信息**: `不兼容的类型: io.github.ningpp.compat.Encoding.Decoder无法转换为io.github.ningpp.compat.Decoder`
- **代码片段**:
  ```java
  _ps.decoder = encoding.getDecoder();  // returns Encoding.Decoder, field type is Decoder
  ```

---

## Iteration 4 — ReadOnlySpan byte preamble type mismatch — ✅ Fixed

- **Java 文件**: `System.Private.Xml/src/main/java/dotnet/xml/XmlTextReaderImpl.java`
- **行号**: 2426
- **错误信息**: `不兼容的类型: byte[]无法转换为io.github.ningpp.compat.ReadOnlySpan<java.lang.Integer>`
- **代码片段**:
  ```java
      private void eatPreamble() {
          ReadOnlySpan<Integer> preamble = _ps.encoding.getPreamble();
          int preambleLen = preamble.getLength();
          int i;
      }
  ```
- **对应 C# 文件**: `D:\csharpxml\System\Xml\Core\XmlTextReaderImpl.cs`
- **C# 原始代码**: `ReadOnlySpan<byte> preamble = _ps.encoding.Preamble;`
- **根因分类**: Transformer 逻辑缺陷
- **涉及组件**: `src/CSharpToJava.Core/Transformers/Expression/Transformers/IdentifierExpressionTransformer.cs`
- **分析**: `System.Text.Encoding.Preamble` 在 C# 中是 `ReadOnlySpan<byte>` 属性，但转换器按普通属性生成了 `encoding.getPreamble()`；compat `Encoding.getPreamble()` 当前表示 `GetPreamble()` 的 `byte[]` 结果，导致 Java 初始化 `ReadOnlySpan<Integer>` 时收到 `byte[]`。

---

## Iteration 5 — Missing compat enum type mapping ✅ Fixed

- **Java 文件**: `System.Private.Xml/src/main/java/dotnet/xml/XmlConvert.java`
- **行号**: 7
- **错误信息**: `程序包dotnet.system.Globalization不存在`
- **代码片段**:
  ```java
  import java.util.function.*;
  import java.util.stream.*;
  import java.io.*;
  import dotnet.system.Globalization.DateTimeStyles;
  import io.github.ningpp.compat.NumberStyles;
  import dotnet.system.StringSplitOptions;
  import dotnet.system.Uri;
  ```
- **对应 C# 文件**: `D:\csharpxml\System\Xml\XmlConvert.cs`
- **C# 原始代码**: `using System.Globalization;` with `DateTimeStyles.AllowLeadingWhite | DateTimeStyles.AllowTrailingWhite`; `StringSplitOptions.RemoveEmptyEntries`
- **根因分类**: 类型映射缺失
- **涉及组件**: `config/TypeMappings.json`, `src/CSharpToJava.Core/Transformers/Expression/Utilities/ExpressionTransformerHelpers.cs`
- **分析**: 外部 BCL enum `System.Globalization.DateTimeStyles` 和 `System.StringSplitOptions` 缺少 compat 类型映射，静态 enum 成员访问回落到 `dotnet.system.*` 导入，生成了项目中不存在的包。

✅ **Fixed** — commit `5078fb6`: Added compat mappings for `DateTimeStyles`/`StringSplitOptions` and made enum member formatting honor explicit mappings.

---

## Iteration 6 — TextReader block Read overload missing ✅ Fixed

- **Java 文件**: `D:\csharpxml-java\System.Private.Xml\src\main\java\dotnet\xml\XmlTextReaderImpl.java`
- **行号**: 2614
- **错误信息**: `无法将类 io.github.ningpp.compat.TextReader中的方法 read应用到给定类型; 需要: 没有参数 找到: char[],int,int 原因: 实际参数列表和形式参数列表长度不同`
- **代码片段**:
  ```java
          } else {
          if (_ps.textReader != null) {
          // read chars
          charsRead = _ps.textReader.read(_ps.chars, _ps.charsUsed, _ps.chars.length - _ps.charsUsed - 1);
          _ps.charsUsed += charsRead;
          } else {
          charsRead = 0;
  ```
- **对应 C# 文件**: `D:\csharpxml\System\Xml\Core\XmlTextReaderImpl.cs`
- **根因分类**: 类型映射缺失
- **涉及组件**: `java/csharptojava-compat/src/main/java/io/github/ningpp/compat/TextReader.java`, `config/TypeMappings.json`
- **分析**: `System.IO.TextReader.Read(char[], int, int)` 被方法映射正常转换为 `TextReader.read(char[], int, int)`，但 compat runtime 的 `TextReader` 只实现了无参 `read()`，导致生成项目引用的 runtime API 不完整。

✅ **Fixed** — Added `TextReader.read(char[], int, int)` to the compat runtime and verified the generated Java project now advances past the original line 2614 overload error.

---

## Iteration 7 — State-machine hoisted bare local remains redeclared

- **Java 文件**: `D:\csharpxml-java\System.Private.Xml\src\main\java\dotnet\xml\XmlTextReaderImpl.java`
- **行号**: 2891
- **错误信息**: `已在方法 parseXmlDeclaration(boolean)中定义了变量 chars`
- **代码片段**:
  ```java
          // parse attribute value
          pos = _ps.charPos;
          char[] chars;
          Continue: { chars = _ps.chars; }
          while (_xmlCharType.isAttributeValueChar(chars[pos])) {
          pos++;
          }
  ```
- **对应 C# 文件**: `D:\csharpxml\System\Xml\Core\XmlTextReaderImpl.cs`
- **根因分类**: Lowering
- **涉及组件**: `src/CSharpToJava.Core/Transformers/Statement/StatementTransformer.LabelAndGoto.cs`
- **分析**: goto state-machine lowering hoists `char[] chars;` to `char[] chars = null;` before the switch, but the post-pass only rewrites hoisted declarations with initializers (`Type name = ...`), so the original bare declaration remains inside the state-machine case and duplicates the hoisted local.

✅ **Fixed** — Removed bare declarations for hoisted state-machine locals inside `__gotoLoop` while preserving initializer-to-assignment rewrites.

---

## Iteration 8 — Switch expression out holder used before declaration

- **Java 文件**: `D:\csharpxml-java\System.Private.Xml\src\main\java\dotnet\xml\XmlTextReaderImpl.java`
- **行号**: 3097
- **错误信息**: `找不到符号 符号: 变量 _iHolder1 位置: 类 dotnet.xml.XmlTextReaderImpl`
- **代码片段**:
  ```java
          if (_fragmentType == XmlNodeType.None) {
          _fragmentType = XmlNodeType.Element;
          }
          switch (handleEntityReference(false, EntityExpandType.OnlyGeneral, _iHolder1)) {
          case Unexpanded:
          IntHolder _iHolder1 = new IntHolder();
          var _ifCond1 = _parsingFunction == ParsingFunction.EntityReference;
  ```
- **对应 C# 文件**: `D:\csharpxml\System\Xml\Core\XmlTextReaderImpl.cs`
- **C# 原始代码**: `switch (HandleEntityReference(false, EntityExpandType.OnlyGeneral, out i))`
- **根因分类**: Transformer
- **涉及组件**: `src/CSharpToJava.Core/Transformers/Statement/StatementTransformer.SwitchAndResource.cs`, `src/CSharpToJava.Core/Transformers/Expression/Transformers/ArgumentTransformer.cs`
- **分析**: switch expression transformation lets an `out` argument enqueue holder pre/post statements, but plain switch lowering does not drain the switch expression pre-statements before emitting `switch (...)`, so the holder declaration is later emitted inside the first case after the holder is already used.

✅ **Fixed** — Captured switch expressions with pending pre/post side effects into a synthetic temporary before emitting the switch, so holder declarations and out read-backs are emitted before switch dispatch.

---

## Iteration 9 — DateTime formatted ToString emits missing compat format method

- **Java 文件**: `D:\csharpxml-java\System.Private.Xml\src\main\java\dotnet\xml\XmlWriter.java`
- **行号**: 174
- **错误信息**: `找不到符号 符号: 方法 format(java.time.format.DateTimeFormatter) 位置: 类型为io.github.ningpp.compat.CSharpDateTime的变量 value`
- **代码片段**:
  ```java
          // Writes out the specified value.
  public void writeValue(CSharpDateTime value) {
          writeString(value.format(DateTimeFormatter.ofPattern("o")));
      }
          // Writes out the specified value.
  public void writeValue(CSharpDateTimeOffset value) {
  ```
- **对应 C# 文件**: `D:\csharpxml\System\Xml\Core\XmlWriter.cs`
- **C# 原始代码**: `WriteString(value.ToString("o"));`
- **根因分类**: Transformer
- **涉及组件**: `src/CSharpToJava.Core/Transformers/Expression/Transformers/InvocationExpressionTransformer.cs`, `java/csharptojava-compat/src/main/java/io/github/ningpp/compat/CSharpDateTime.java`
- **分析**: `System.DateTime` 已映射为 compat `CSharpDateTime`，但 formatted `DateTime.ToString(format)` 的 transformer 仍按 Java `LocalDateTime` 生成 `.format(DateTimeFormatter.ofPattern(...))`，导致生成代码调用 compat 类型不存在的方法。

✅ **Fixed** — Formatted `DateTime`/`DateTimeOffset.ToString(...)` now targets compat `toString(...)` overloads, and the compat runtime implements round-trip `"o"` formatting.

---

## Iteration 10 — TimeSpan.Zero static constant casing ✅ Fixed

- **Java 文件**: `D:\csharpxml-java\System.Private.Xml\src\main\java\dotnet\xml\XmlWriter.java`
- **行号**: 180
- **错误信息**: `找不到符号 符号: 变量 Zero 位置: 类 io.github.ningpp.compat.CSharpTimeSpan`
- **代码片段**:
  ```java
          // might not have implemented it. This base implementation should call WriteValue(DateTime).
          // The following conversion results in the same string as calling ToString with DateTimeOffset.
          if (value.getOffset() != CSharpTimeSpan.Zero) {
          writeValue(value.getLocalDateTime());
          } else {
          writeValue(value.getUtcDateTime());
          }
  ```
- **对应 C# 文件**: `D:\csharpxml\System\Xml\Core\XmlWriter.cs`
- **C# 原始代码**: `if (value.Offset != TimeSpan.Zero)`
- **根因分类**: Transformer
- **涉及组件**: `src/CSharpToJava.Core/Transformers/Expression/Transformers/IdentifierExpressionTransformer.cs`, `config/TypeMappings.json`, `java/csharptojava-compat/src/main/java/io/github/ningpp/compat/CSharpTimeSpan.java`
- **分析**: `System.TimeSpan` 当前映射到 compat `CSharpTimeSpan`，但静态成员访问仍原样保留 `Zero`（旧特殊分支只处理 `Duration.Zero`），而 compat 常量名是 Java 风格的 `ZERO`。

✅ **Fixed** — `TimeSpan.Zero` now maps to `CSharpTimeSpan.ZERO`, and regenerated `XmlWriter.java` advanced past the original missing `Zero` symbol.

---

## Iteration 11 — DateTimeOffset.Parse missing compat runtime method ✅ Fixed

- **Java 文件**: `D:\csharpxml-java\System.Private.Xml\src\main\java\dotnet\xml\XmlReader.java`
- **行号**: 179
- **错误信息**: `找不到符号 符号: 方法 parse(java.lang.String) 位置: 类 io.github.ningpp.compat.CSharpDateTimeOffset`
- **代码片段**:
  ```java
          throw createReadContentAsException("ReadContentAsDateTimeOffset");
          }
          try {
              return CSharpDateTimeOffset.parse(internalReadContentAsString());
          } catch (FormatException e) {
              throw new XmlException(SR.getXml_ReadContentAsFormatException(), "DateTimeOffset", e, (this instanceof IXmlLineInfo ? (IXmlLineInfo)(this) : null));
          }
  ```
- **对应 C# 文件**: `D:\csharpxml\System\Xml\Core\XmlReader.cs`
- **C# 原始代码**: `return DateTimeOffset.Parse(InternalReadContentAsString(), CultureInfo.InvariantCulture);`
- **根因分类**: 类型映射缺失
- **涉及组件**: `config/TypeMappings.json`, `src/CSharpToJava.Core/Transformers/Expression/Transformers/InvocationExpressionTransformer.cs`, `java/csharptojava-compat/src/main/java/io/github/ningpp/compat/CSharpDateTimeOffset.java`
- **分析**: `System.DateTimeOffset` 已映射为 compat `CSharpDateTimeOffset`，静态 `Parse` 调用被转换为 `CSharpDateTimeOffset.parse(...)`，但 compat runtime 没有提供该静态方法。

✅ **Fixed** — Added `CSharpDateTimeOffset.parse(String)` to compat, installed the updated runtime artifact, and regenerated Maven now advances past the original `XmlReader.java:[179]` missing method.

---

## Iteration 12 — Labeled hoisted bare local redeclaration

- **Java 文件**: `D:\csharpxml-java\System.Private.Xml\src\main\java\dotnet\xml\XmlTextReaderImpl.java`
- **行号**: 3693
- **错误信息**: `已在方法 parseAttributes()中定义了变量 tmpch2`
- **代码片段**:
  ```java
          // case occurs (like end of buffer, invalid name char)
          pos += startNameCharSize; // start name char has already been checked
          // parse attribute name
          ContinueParseName: { char tmpch2; }
          for (; true; ) {
          if (_xmlCharType.isNCNameSingleChar(tmpch2 = chars[pos])) {
          pos++;
  ```
- **对应 C# 文件**: `D:\csharpxml\System\Xml\Core\XmlTextReaderImpl.cs`
- **C# 原始代码**: `ContinueParseName: char tmpch2;`
- **根因分类**: Transformer
- **涉及组件**: `src/CSharpToJava.Core/Transformers/Statement/StatementTransformer.LabelAndGoto.cs`
- **分析**: goto state-machine lowering hoists `tmpch2` to method scope, but nested normal statement transformation preserves the labeled bare declaration as `ContinueParseName: { char tmpch2; }`, and the hoisted-local cleanup only removes unlabeled bare declaration lines.

✅ **Fixed** — State-machine hoisted-local cleanup now removes single-line labeled bare declarations for hoisted variables, including renamed declaration clones such as `tmpch2_...`.

---

## Iteration 13 — Array.Sort range overload mapped to Object.sort

- **Java 文件**: `D:\csharpxml-java\System.Private.Xml\src\main\java\dotnet\xml\XmlTextReaderImpl.java`
- **行号**: 3847
- **错误信息**: `找不到符号 符号: 方法 sort(dotnet.xml.XmlTextReaderImpl.NodeData[],int,int) 位置: 类 java.lang.Object`
- **代码片段**:
  ```java
          _attrDuplSortingArray = new NodeData[_attrCount];
          }
          System.arraycopy(_nodes, _index + 1, _attrDuplSortingArray, 0, _attrCount);
          Object.sort(_attrDuplSortingArray, 0, _attrCount);
          NodeData attr1 = _attrDuplSortingArray[0];
          for (int i = 1; i < _attrCount; i++) {
          NodeData attr2 = _attrDuplSortingArray[i];
  ```
- **对应 C# 文件**: `D:\csharpxml\System\Xml\Core\XmlTextReaderImpl.cs`
- **C# 原始代码**: `Array.Sort(_attrDuplSortingArray, 0, _attrCount);`
- **根因分类**: Transformer
- **涉及组件**: `src/CSharpToJava.Core/Transformers/Expression/Transformers/InvocationExpressionTransformer.cs`
- **分析**: `System.Array` maps syntactically to `Object`, and the invocation transformer only special-cases the 1- and 2-argument `Array.Sort` overloads, so the 3-argument range overload falls through as a nonexistent `Object.sort(array, index, length)`.

✅ **Fixed** — `Array.Sort(array, index, length)` now maps to `Arrays.sort(array, index, index + length)`, preserving C# range length semantics with Java's exclusive end index.

---

## Iteration 14 — Hoisted multi-variable local redeclaration

- **Java 文件**: `D:\csharpxml-java\System.Private.Xml\src\main\java\dotnet\xml\XmlTextReaderImpl.java`
- **行号**: 4424
- **错误信息**: `已在方法 parseText(io.github.ningpp.compat.IntHolder,io.github.ningpp.compat.IntHolder,io.github.ningpp.compat.IntHolder)中定义了变量 charRefEndPos`
- **代码片段**:
  ```java
          case 4:
          break;
          case 5:
          int charRefEndPos, charCount;
          IntHolder _charCountHolder1 = new IntHolder();
          ObjectHolder<EntityType> _entityTypeHolder1 = new ObjectHolder<>();
          var _ifCond2 = (charRefEndPos = parseCharRefInline(pos, _charCountHolder1, _entityTypeHolder1)) > 0;
  ```
- **对应 C# 文件**: `D:\csharpxml\System\Xml\Core\XmlTextReaderImpl.cs`
- **C# 原始代码**: `int charRefEndPos, charCount;`
- **根因分类**: Transformer
- **涉及组件**: `src/CSharpToJava.Core/Transformers/Statement/StatementTransformer.LabelAndGoto.cs`
- **分析**: goto state-machine lowering already hoists both locals to the generated method prelude, but the state-machine cleanup only removes single-variable bare declarations inside the loop and leaves comma-separated declarations like `int charRefEndPos, charCount;` behind.

✅ **Fixed** — State-machine hoisted-local cleanup now removes comma-separated bare declarations when every declared variable has already been hoisted.

---

## Iteration 15 — Labeled local declaration scope

- **Java 文件**: `D:\csharpxml-java\System.Private.Xml\src\main\java\dotnet\xml\XmlTextReaderImpl.java`
- **行号**: 5652
- **错误信息**: `找不到符号 符号: 变量 tmp3 位置: 类 dotnet.xml.XmlTextReaderImpl`
- **代码片段**:
  ```java
          }
          }
          ReadData: { int tmp3 = pos - _ps.charPos; }
          if (tmp3 > 0) {
          if (sb != null) {
          sb.append(_ps.chars, _ps.charPos, tmp3);
          }
  ```
- **对应 C# 文件**: `D:\csharpxml\System\Xml\Core\XmlTextReaderImpl.cs`
- **C# 原始代码**: `ReadData: int tmp3 = pos - _ps.charPos;`
- **根因分类**: Transformer
- **涉及组件**: `src/CSharpToJava.Core/Transformers/Statement/StatementTransformer.LabelAndGoto.cs`
- **分析**: normal labeled-statement conversion wraps non-block labeled statements in a Java block, but a C# label on a local declaration does not create a scope, so `ReadData: { int tmp3 = ...; }` hides `tmp3` from following statements.

✅ **Fixed** — Labels on local declarations now emit an empty labeled statement followed by the declaration at the original scope, preserving Java syntax and C# variable visibility.

---

## Iteration 16 — Hoisted local after empty labeled statement

- **Java 文件**: `D:\csharpxml-java\System.Private.Xml\src\main\java\dotnet\xml\XmlTextReaderImpl.java`
- **行号**: 3693
- **错误信息**: `已在方法 parseAttributes()中定义了变量 tmpch2`
- **代码片段**:
  ```java
          // parse attribute name
          ContinueParseName: ; char tmpch2;
          for (; true; ) {
          if (_xmlCharType.isNCNameSingleChar(tmpch2 = chars[pos])) {
          pos++;
  ```
- **对应 C# 文件**: `D:\csharpxml\System\Xml\Core\XmlTextReaderImpl.cs`
- **C# 原始代码**: `ContinueParseName: char tmpch2;`
- **根因分类**: Transformer
- **涉及组件**: `src/CSharpToJava.Core/Transformers/Statement/StatementTransformer.LabelAndGoto.cs`
- **分析**: labeled local declarations are now converted to Java's `label: ; declaration` form, but the state-machine hoisted-local cleanup only removes older labeled block declarations and leaves the declaration after the empty label.

✅ **Fixed** — State-machine hoisted-local cleanup now also removes declarations emitted after empty labeled statements, matching the `label: ; declaration` form used for labeled local declarations.
