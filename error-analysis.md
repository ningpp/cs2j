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

---

## Iteration 17 — System.Array length on Object receiver

- **Java 文件**: `D:\csharpxml-java\System.Private.Xml\src\main\java\dotnet\xml\XmlTextReaderImpl.java`
- **行号**: 6460
- **错误信息**: `找不到符号 符号: 变量 length 位置: 类型为java.lang.Object的变量 array`
- **代码片段**:
  ```java
          if (index < 0) {
          throw new IllegalArgumentException(((_incReadDecoder instanceof IncrementalReadCharsDecoder) ? "index" : "offset"));
          }
          if (array.length - index < count) {
          throw new ArgumentException(((_incReadDecoder instanceof IncrementalReadCharsDecoder) ? "count" : "len"));
          }
          if (count == 0) {
  ```
- **对应 C# 文件**: `D:\csharpxml\System\Xml\Core\XmlTextReaderImpl.cs`
- **C# 原始代码**: `if (array.Length - index < count)`
- **根因分类**: Transformer
- **涉及组件**: `src/CSharpToJava.Core/Transformers/Expression/Transformers/IdentifierExpressionTransformer.cs`, `src/CSharpToJava.Core/Lowering/LowerProperty.cs`
- **分析**: `System.Array` was mapped to Java `Object`, which lost the static type needed to model `Array.Length` and produced `array.length` on an `Object` receiver.

✅ **Fixed** — `System.Array`/`Array` now map to compact runtime `CSharpArray`; concrete Java arrays are wrapped with `CSharpArray.of(...)` when passed to `System.Array` parameters, and `System.Array.Length` lowers to `getLength()` while concrete arrays continue to use `.length`.

---
## Iteration 1 — cannot find symbol CSharpArray
- **Java 文件**: D:\csharpxml-java\System.Private.Xml\src\main\java\dotnet\xml\Base64Decoder.java
- **行号**: 8
- **错误信息**: [ERROR] /D:/csharpxml-java/System.Private.Xml/src/main/java/dotnet/xml/Base64Decoder.java:[8,31] 找不到符号; 符号: 类 CSharpArray; 位置: 程序包 io.github.ningpp.compat
- **代码片段**:
  ```java
  import java.util.stream.*;
  import java.io.*;
  import io.github.ningpp.compat.ArgumentNullException;
  import io.github.ningpp.compat.CSharpArray;
  import java.lang.foreign.MemorySegment;
  import java.lang.foreign.ValueLayout;
  import dotnet.system.*;
  ```
- **对应 C# 文件**: D:\csharpxml\System\Xml\Base64Decoder.cs
- **根因分类**: 类型映射缺失
- **涉及组件**: D:\code\cs2j\config\TypeMappings.json; D:\code\cs2j\src\CSharpToJava.Core\Pipeline\Planning\WorkspacePlanBuilder.cs; D:\code\cs2j\src\CSharpToJava.Core\Pipeline\Planning\MavenPomGenerator.cs
- **分析**: 转换器将 `System.Array` 映射为 `io.github.ningpp.compat.CSharpArray`，但生成的 Maven workspace 只声明外部 `csharptojava-compat:1.0-SNAPSHOT` 依赖，未确保该依赖来自当前转换器随附的 compat 源码/模块，导致生成代码引用的运行时类型在编译类路径中不可用。
✅ **Fixed** — `StreamWrapper.readAsync(byte[], int, int)` now exists in the compat runtime and returns a completed `CompletableFuture<Integer>` using the existing .NET-style `read(...)` EOF semantics. After reinstalling compat, regenerating, and rerunning Maven, the original `XmlTextReaderImpl.java:[8385,26]` compiler error disappeared; the next Maven first error is now `TextReader.readAsync(char[], int, int)` missing at `XmlTextReaderImpl.java:[8561,35]`.

---

## Iteration 2 — CSharpArray cast to concrete byte array
- **Java 文件**: D:\csharpxml-java\System.Private.Xml\src\main\java\dotnet\xml\Base64Decoder.java
- **行号**: 113
- **错误信息**: `[ERROR] /D:/csharpxml-java/System.Private.Xml/src/main/java/dotnet/xml/Base64Decoder.java:[113,27] 不兼容的类型: io.github.ningpp.compat.CSharpArray无法转换为byte[]`
- **代码片段**:
  ```java
          // Debug.Assert(buffer.getLength() - index >= count);
  
          // Debug.Assert(((buffer instanceof byte[] ? (byte[])(buffer) : null)) != null);
  
          _buffer = (byte[])(buffer);
          _startIndex = index;
          _curIndex = index;
          _endIndex = index + count;
  ```
- **对应 C# 文件**: D:\csharpxml\System\Xml\Base64Decoder.cs
- **C# 原始代码**: `_buffer = (byte[])buffer;`
- **根因分类**: Transformer + compat
- **涉及组件**: D:\code\cs2j\src\CSharpToJava.Core\Transformers\Expression\Transformers\TypeOperationTransformer.cs; D:\code\cs2j\java\csharptojava-compat\src\main\java\io\github\ningpp\compat\CSharpArray.java
- **分析**: 重新执行 Step 1/2 后，当前第一错误不再匹配末尾 `Iteration 1 — cannot find symbol CSharpArray`；`CSharpArray` 已进入 Maven 类路径。新的第一错误来自 `System.Array` 参数被映射为 `CSharpArray` 后，C# 的 `(byte[])buffer` 仍被 TypeOperationTransformer 直接生成为 Java cast `(byte[])(buffer)`。Java 不能把 wrapper 对象直接 cast 成底层数组，需要通过 compat API 暴露 wrapped array 并生成 `buffer.as(byte[].class)`。

✅ **Fixed** — `System.Array` references cast to concrete arrays now lower to `CSharpArray.as(arrayClass)`, and the compact runtime exposes that typed unwrap helper. The original incompatible cast error disappeared after regenerating and rebuilding; the next Maven error is now `CSharpArray.binarySearch(...)` missing.

---

## Iteration 3 — CSharpArray BinarySearch runtime API missing
- **Java 文件**: D:\csharpxml-java\System.Private.Xml\src\main\java\dotnet\xml\XmlTextReaderImpl.java
- **行号**: 7261
- **错误信息**: `[ERROR] /D:/csharpxml-java/System.Private.Xml/src/main/java/dotnet/xml/XmlTextReaderImpl.java:[7261,24] 找不到符号; 符号: 方法 binarySearch(dotnet.xml.XmlTextReaderImpl.NodeData[],dotnet.xml.IDtdDefaultAttributeInfo,java.util.Comparator<java.lang.Object>); 位置: 类 io.github.ningpp.compat.CSharpArray`
- **代码片段**:
  ```java
          String prefix = defAttrInfo.getPrefix();
          // check for duplicates
          if (nameSortedNodeData != null) {
          if (CSharpArray.binarySearch(nameSortedNodeData, defAttrInfo, DtdDefaultAttributeInfoToNodeDataComparer.getInstance()) >= 0) {
          return false;
          }
          } else {
  ```
- **对应 C# 文件**: D:\csharpxml\System\Xml\Core\XmlTextReaderImpl.cs
- **C# 原始代码**: `if (Array.BinarySearch<object>(nameSortedNodeData, defAttrInfo, DtdDefaultAttributeInfoToNodeDataComparer.Instance) >= 0)`
- **根因分类**: compat runtime API 缺失
- **涉及组件**: D:\code\cs2j\java\csharptojava-compat\src\main\java\io\github\ningpp\compat\CSharpArray.java; D:\code\cs2j\src\CSharpToJava.Core\Transformers\Expression\Transformers\InvocationExpressionTransformer.cs
- **分析**: `System.Array` 已映射为 compact runtime `CSharpArray`，因此未被专门 lower 的静态 `Array.BinarySearch<T>` 会自然生成为 `CSharpArray.binarySearch(...)`。生成位置的 C# 调用使用 `object` 泛型参数、`NodeData[]` 数组、`IDtdDefaultAttributeInfo` 查找值和 `IComparer<object>` 比较器；Java 端缺少对应静态 helper，所以 Maven 在第一处 `CSharpArray.binarySearch(...)` 编译失败。

✅ **Fixed** — `CSharpArray.binarySearch(array, value, Comparator<Object>)` now exists in the compat runtime and preserves .NET/Java binary-search insertion-point semantics. After reinstalling compat, regenerating, and rerunning Maven, the original `CSharpArray.binarySearch(...)` compiler error disappeared; the next Maven first error is now `StreamWrapper.readAsync(byte[], int, int)` missing.

---

## Iteration 4 — StreamWrapper ReadAsync runtime API missing
- **Java 文件**: D:\csharpxml-java\System.Private.Xml\src\main\java\dotnet\xml\XmlTextReaderImpl.java
- **行号**: 8385
- **错误信息**: `[ERROR] /D:/csharpxml-java/System.Private.Xml/src/main/java/dotnet/xml/XmlTextReaderImpl.java:[8385,26] 找不到符号; 符号: 方法 readAsync(byte[],int,int); 位置: 类型为io.github.ningpp.compat.StreamWrapper的变量 stream`
- **代码片段**:
  ```java
          // make sure we have at least 4 bytes to detect the encoding (no preamble of System.Text supported encoding is longer than 4 bytes)
          _ps.bytePos = 0;
          while (_ps.bytesUsed < 4 && _ps.bytes.length - _ps.bytesUsed > 0) {
          int read = stream.readAsync(_ps.bytes, _ps.bytesUsed, _ps.bytes.length - _ps.bytesUsed).join();
          if (read == 0) {
          _ps.isStreamEof = true;
          break;
  ```
- **对应 C# 文件**: D:\csharpxml\System\Xml\Core\XmlTextReaderImpl.cs
- **C# 原始代码**: `int read = stream.Read(_ps.bytes, _ps.bytesUsed, _ps.bytes.Length - _ps.bytesUsed);` in the synchronous path; the async converted path emits `stream.readAsync(...).join()`.
- **根因分类**: compat runtime API 缺失
- **涉及组件**: D:\code\cs2j\java\csharptojava-compat\src\main\java\io\github\ningpp\compat\StreamWrapper.java; D:\code\cs2j\config\TypeMappings.json; D:\code\cs2j\src\CSharpToJava.Core\Transformers\Expression\Transformers\ControlFlowTransformer.cs
- **分析**: `System.IO.Stream` maps to compat `StreamWrapper`, and the runtime already exposes synchronous `read(byte[], int, int)` with .NET EOF semantics. Async lowering maps awaited or joined task results to Java `CompletableFuture.join()`, so generated async XML reader code calls `StreamWrapper.readAsync(byte[], int, int).join()`. The compat runtime lacked that async facade, causing the first Maven compiler error.
- **状态**: In Progress

---

## Iteration 5 — TextReader ReadAsync runtime API missing
- **Java 文件**: D:\csharpxml-java\System.Private.Xml\src\main\java\dotnet\xml\XmlTextReaderImpl.java
- **行号**: 8561
- **错误信息**: `[ERROR] /D:/csharpxml-java/System.Private.Xml/src/main/java/dotnet/xml/XmlTextReaderImpl.java:[8561,35] 找不到符号; 符号: 方法 readAsync(char[],int,int); 位置: 类型为io.github.ningpp.compat.TextReader的变量 textReader`
- **代码片段**:
  ```java
          } else {
          if (_ps.textReader != null) {
          // read chars
          charsRead = _ps.textReader.readAsync(_ps.chars, _ps.charsUsed, _ps.chars.length - _ps.charsUsed - 1).join();
          _ps.charsUsed += charsRead;
          } else {
          charsRead = 0;
  ```
- **对应 C# 文件**: D:\csharpxml\System\Xml\Core\XmlTextReaderImplAsync.cs
- **C# 原始代码**: `charsRead = await _ps.textReader.ReadAsync(_ps.chars, _ps.charsUsed, _ps.chars.Length - _ps.charsUsed - 1).ConfigureAwait(false);`
- **根因分类**: compat runtime API 缺失
- **涉及组件**: D:\code\cs2j\java\csharptojava-compat\src\main\java\io\github\ningpp\compat\TextReader.java; D:\code\cs2j\java\csharptojava-compat\src\test\java\io\github\ningpp\compat\TextReaderTest.java
- **分析**: `System.IO.TextReader` maps to compat `TextReader`, and synchronous `TextReader.Read(char[], int, int)` already maps to `TextReader.read(char[], int, int)`. The async XML reader path lowers awaited `ReadAsync(char[], int, int)` to `TextReader.readAsync(char[], int, int).join()`, but compat `TextReader` exposes only the synchronous block-read API, so generated code compiles against a missing runtime method.

✅ **Fixed** — `TextReader.readAsync(char[], int, int)` now exists in the compat runtime and returns a completed `CompletableFuture<Integer>` using the existing .NET-style `read(...)` EOF semantics. After reinstalling compat, regenerating, and rerunning Maven, the original `XmlTextReaderImpl.java:[8561,35]` compiler error disappeared; the next Maven first error is now `CompletableFuture.getIsCompletedSuccessfully()` missing at `XmlTextReaderImpl.java:[9948,27]`.

---

## Iteration 23 — ValueTask IsCompletedSuccessfully property mapping missing
- **Java 文件**: D:\csharpxml-java\System.Private.Xml\src\main\java\dotnet\xml\XmlTextReaderImpl.java
- **行号**: 9948
- **错误信息**: `[ERROR] /D:/csharpxml-java/System.Private.Xml/src/main/java/dotnet/xml/XmlTextReaderImpl.java:[9948,27] 找不到符号; 符号: 方法 getIsCompletedSuccessfully(); 位置: 类型为java.util.concurrent.CompletableFuture<io.github.ningpp.compat.Tuple4<java.lang.Integer,java.lang.Integer,java.lang.Integer,java.lang.Boolean>>的变量 parseTextTask`
- **代码片段**:
  ```java
          // the whole value is in buffer
          CompletableFuture<Tuple4<Integer, Integer, Integer, Boolean>> parseTextTask = parseTextAsync(orChars);
          boolean fullValue = false;
          if (!parseTextTask.getIsCompletedSuccessfully()) {
          return _ParseTextAsync(parseTextTask.asTask());
          } else {
          var tuple_10 = parseTextTask.getResult();
          startPos = tuple_10._1();
          endPos = tuple_10._2();
  ```
- **对应 C# 文件**: D:\csharpxml\System\Xml\Core\XmlTextReaderImplAsync.cs
- **C# 原始代码**: `if (!parseTextTask.IsCompletedSuccessfully) { return _ParseTextAsync(parseTextTask.AsTask()); } ... var tuple_10 = parseTextTask.Result;`
- **根因分类**: Transformer property/method mapping
- **涉及组件**: D:\code\cs2j\config\TypeMappings.json; D:\code\cs2j\src\CSharpToJava.Core\Transformers\Expression\Transformers\IdentifierExpressionTransformer.cs
- **分析**: `System.Threading.Tasks.ValueTask<T>` is mapped to Java `CompletableFuture<T>`. The generated Java proves the property-access path is treating `IsCompletedSuccessfully` and `Result` as ordinary C# properties, emitting default getters `getIsCompletedSuccessfully()` and `getResult()`. The existing TypeMappings already map `Task/Task<T>.IsCompletedSuccessfully` to `isDone()` and `Task/Task<T>/ValueTask<T>.Result` to `join()`, but `ValueTask<T>.IsCompletedSuccessfully` is not configured. The real failing receiver type is `ValueTask<ValueTuple<int,int,int,bool>>`; TypeMappingRegistry's generic-arity matcher counted commas inside Roslyn tuple display syntax as top-level type parameters, so the `ValueTask`1` mapping was missed for tuple-valued tasks. `AsTask()` similarly remains `asTask()` in later lines and should be identity for the `CompletableFuture` representation.

✅ **Fixed** — `ValueTask<T>.IsCompletedSuccessfully` now maps to `CompletableFuture.isDone()`, `ValueTask<T>.Result` maps through the existing `join()` mapping even when `T` is a tuple, and `ValueTask<T>.AsTask()` is emitted as the existing `CompletableFuture` receiver. After regenerating and rerunning Maven, the original `getIsCompletedSuccessfully()` / `getResult()` / `asTask()` call sites disappeared; the next Maven first error is now `new CompletableFuture<T>(...)` constructor usage at `XmlTextReaderImpl.java:[10133,16]`.

---

## Iteration 24 — ValueTask constructor mapped to invalid CompletableFuture constructor
- **Java 文件**: D:\csharpxml-java\System.Private.Xml\src\main\java\dotnet\xml\XmlTextReaderImpl.java
- **行号**: 10133
- **错误信息**: `[ERROR] /D:/csharpxml-java/System.Private.Xml/src/main/java/dotnet/xml/XmlTextReaderImpl.java:[10133,16] 无法将类 java.util.concurrent.CompletableFuture<T>中的构造器 CompletableFuture应用到给定类型;`
- **代码片段**:
  ```java
          CompletableFuture<Tuple4<Integer, Integer, Integer, Boolean>> task = parseTextAsync(outOrChars, _ps.chars, _ps.charPos, 0, -1, outOrChars, (char)(0));
          while (true) {
          if (!AsyncHelper.isSuccess(task)) {
          return new CompletableFuture<Tuple4<Integer, Integer, Integer, Boolean>>(parseTextAsync_AsyncFunc(task));
          }
          outOrChars = _lastParseTextState.outOrChars;
          char[] chars = _lastParseTextState.chars;
  ```
- **对应 C# 文件**: D:\csharpxml\System\Xml\Core\XmlTextReaderImplAsync.cs
- **C# 原始代码**: `return new ValueTask<ValueTuple<int, int, int, bool>>(ParseTextAsync_AsyncFunc(task));`
- **根因分类**: Transformer object-creation/type mapping lowering
- **涉及组件**: D:\code\cs2j\src\CSharpToJava.Core\Transformers\Expression\Transformers\ObjectCreationTransformer.cs; D:\code\cs2j\tests\CSharpToJava.Tests\ConfigureAwaitConversionTests.cs
- **分析**: `System.Threading.Tasks.ValueTask<T>` 在当前转换器里映射到 Java `CompletableFuture<T>`，上一轮也已让其成员访问按 `CompletableFuture` 表示工作。但对象创建转换仍走通用构造器路径，把 C# `new ValueTask<T>(Task<T>)` 机械生成为 `new CompletableFuture<T>(future)`。Java `CompletableFuture` 没有接收另一个 future/result 的公开构造器；正确 lower 是：`new ValueTask<T>(Task<T>)`/`new ValueTask<T>(ValueTask<T>)` 直接返回已有 `CompletableFuture<T>` 表达式，`new ValueTask<T>(T result)` 则用 `CompletableFuture.completedFuture(result)`。
✅ **Fixed** — `new ValueTask<T>(Task<T>)` and `new ValueTask<T>(ValueTask<T>)` now lower to the existing `CompletableFuture<T>` expression, while `new ValueTask<T>(T result)` lowers to `CompletableFuture.completedFuture(result)`. After regenerating and rerunning Maven, the original `new CompletableFuture<T>(...)` constructor error at `XmlTextReaderImpl.java:[10133,16]` disappeared; the next Maven first error is now an `Iterable` generic inheritance conflict at `XmlTextReaderImpl.java:[12306,20]`.

---

## Iteration 25 — Inherited raw IEnumerable re-emitted as generic Iterable
- **Java 文件**: D:\csharpxml-java\System.Private.Xml\src\main\java\dotnet\xml\XmlTextReaderImpl.java
- **行号**: 12306
- **错误信息**: `[ERROR] /D:/csharpxml-java/System.Private.Xml/src/main/java/dotnet/xml/XmlTextReaderImpl.java:[12306,20] 无法使用以下不同的参数继承java.lang.Iterable: <java.lang.Object> 和 <>`
- **代码片段**:
  ```java
      //
      // NoNamespaceManager
      //
      private static class NoNamespaceManager extends XmlNamespaceManager implements Iterable<Object> {
          public NoNamespaceManager() {
          }
  ```
- **对应 C# 文件**: D:\csharpxml\System\Xml\Core\XmlTextReaderImplHelpers.cs
- **C# 原始代码**: `private class NoNamespaceManager : XmlNamespaceManager`; base type `XmlNamespaceManager` is declared in `D:\csharpxml\System\Xml\XmlNamespacemanager.cs` as `public class XmlNamespaceManager : IXmlNamespaceResolver, IEnumerable`.
- **根因分类**: Transformer class/interface emission
- **涉及组件**: D:\code\cs2j\src\CSharpToJava.Core\Transformers\Type\ClassTransformer.cs; D:\code\cs2j\config\TypeMappings.json
- **分析**: `XmlNamespaceManager` implements non-generic `System.Collections.IEnumerable`, which maps to raw Java `Iterable`. `NoNamespaceManager` only extends `XmlNamespaceManager` and overrides `GetEnumerator()`. During member conversion that override is lowered to an `iterator()` method, and `ClassTransformer.AddIterableBridgeFromIteratorMethod` treated any class with an iterator-like method as a pattern-enumerable class that must declare `implements Iterable<Object>`. That is correct for standalone pattern enumerators, but wrong when a base class already provides the enumerable contract: the generated subclass then inherits raw `Iterable` from the base and directly implements `Iterable<Object>`, which Java rejects as conflicting parameterizations of the same generic interface.

✅ **Fixed** — `AddIterableBridgeFromIteratorMethod` now receives the Roslyn class symbol and skips adding a synthetic `Iterable<T>` when any base type already implements `System.Collections.IEnumerable` or `System.Collections.Generic.IEnumerable<T>`. Standalone pattern enumerator classes still get `Iterable<T>`. After regenerating and rerunning Maven, `NoNamespaceManager` now emits `private static class NoNamespaceManager extends XmlNamespaceManager` with no duplicate `implements Iterable<Object>`, and the original `XmlTextReaderImpl.java:[12306,20]` Iterable inheritance conflict disappeared. The next Maven first error is now `XmlTextReaderImpl.java:[2273,9] 无法访问的语句`.

---

## Iteration 26 — Switch goto-case nested terminal branch emits unreachable reset
- **Java 文件**: D:\csharpxml-java\System.Private.Xml\src\main\java\dotnet\xml\XmlTextReaderImpl.java
- **行号**: 2273
- **错误信息**: `[ERROR] /D:/csharpxml-java/System.Private.Xml/src/main/java/dotnet/xml/XmlTextReaderImpl.java:[2273,9] 无法访问的语句`
- **代码片段**:
  ```java
          if (allowXmlDeclFragment) {
          _ps.appendMode = false;
          _parsingFunction = ParsingFunction.SwitchToInteractive;
          _nextParsingFunction = ParsingFunction.XmlDeclarationFragment;
          break;
          } else {
          _switch31State = 5; continue _switch31Loop;
          }
          _switch31State = -1;
          break _switch31Loop;
          case 5:
          throwValue(SR.getXml_PartialContentNodeTypeNotSupportedEx(), fragmentType.toString());
  ```
- **对应 C# 文件**: D:\csharpxml\System\Xml\Core\XmlTextReaderImpl.cs
- **C# 原始代码**: `case XmlNodeType.XmlDeclaration: if (allowXmlDeclFragment) { ... break; } else { goto default; }`
- **根因分类**: Transformer switch/goto-case state-machine lowering
- **涉及组件**: D:\code\cs2j\src\CSharpToJava.Core\Transformers\Statement\StatementTransformer.SwitchAndResource.cs; D:\code\cs2j\src\CSharpToJava.Core\Transformers\Statement\StatementTransformer.cs
- **分析**: `TransformSwitchWithGotoCase` lowers switches containing `goto case/default` into a `_switchNState`/`_switchNLoop` state machine. Its section-tail logic only treats top-level terminal statements as terminal. In this case the terminal behavior is nested inside an `if/else`: the `if` branch has a C# `break`, while the `else` branch has `goto default`. The nested `goto default` is correctly transformed to `_switch31State = 5; continue _switch31Loop;`, but the nested `break` is transformed by the generic statement path to a bare Java `break;`. Both branches already terminate the state-machine case, yet the section-tail detector does not recognize the `if/else` as terminal and appends `_switch31State = -1; break _switch31Loop;` after the `if`, which Java reports as unreachable. The same generic nested `break` lowering is also semantically wrong in the state-machine loop because it exits the inner Java `switch` rather than the `_switch31Loop`.

✅ **Fixed** — switch-with-goto-case lowering now keeps the source `SwitchStatementSyntax` in the switch-goto context, rewrites nested `break`/`continue` that target that switch into `{state} = -1; break {loop};`, and recognizes `if/else` and block statements whose all paths terminate the state-machine case. After regenerating and rerunning Maven, the original `XmlTextReaderImpl.java:[2273,9]` unreachable statement disappeared and the generated XmlDeclaration case now directly exits `_switch31Loop` without the extra reset. Maven still fails; the next first error is `XmlTextReaderImpl.java:[2961,9] 无法访问的语句`.

---

## Iteration 27 — Method goto state-machine keeps fallthrough after infinite loop with nested switch breaks
- **Java 文件**: D:\csharpxml-java\System.Private.Xml\src\main\java\dotnet\xml\XmlTextReaderImpl.java
- **行号**: 2961
- **错误信息**: `[ERROR] /D:/csharpxml-java/System.Private.Xml/src/main/java/dotnet/xml/XmlTextReaderImpl.java:[2961,9] 无法访问的语句`
- **代码片段**:
  ```java
          } else {
          throwValue((isTextDecl ? SR.getXml_InvalidTextDecl() : SR.getXml_InvalidXmlDecl()));
          }
          }
          ReadData: { if (_ps.isEof || readData() == 0) {
          throwValue(SR.getXml_UnexpectedEOF1());
          } }
          }
          __state = 2;
          continue __gotoLoop;
          case 2: // NoXmlDecl
          NoXmlDecl: { if (!isTextDecl) {
          _parsingFunction = _nextParsingFunction;
          } }
  ```
- **对应 C# 文件**: D:\csharpxml\System\Xml\Core\XmlTextReaderImpl.cs
- **C# 原始代码**: `ParseXmlDeclaration(bool isTextDecl)` 中 `for (;;)` 循环包含 `Continue:` / `ReadData:` 标签、`goto Continue`，并在循环后有 `NoXmlDecl:` 标签。
- **根因分类**: Transformer method goto state-machine lowering / unreachable cleanup
- **涉及组件**: D:\code\cs2j\src\CSharpToJava.Core\Transformers\Statement\StatementTransformer.LabelAndGoto.cs
- **分析**: 这次与上一轮 `switch goto case/default` 的 nested terminal reset 不同。当前错误来自普通方法级 `goto` state machine：`GotoAnalyzer` 将方法降成 `__gotoLoop` + `switch (__state)`，其中 `ParseXmlDeclaration` 的 `for (;;)` 生成 Java `while (true)`，循环之后又按 basic-block fall-through 追加 `__state = 2; continue __gotoLoop;` 跳到 `NoXmlDecl`。Java 对没有可达 `break` 的 `while (true)` 后续语句判定为不可达。转换器已有 `RemoveUnreachableCodeAfterInfiniteLoops` 后处理来删除这种无限循环后的 state transition，但它在判断循环体能否正常退出时只跟踪嵌套 loop，不跟踪嵌套 `switch`。本例循环体内的属性值解析 `switch (xmlDeclState)` 有多个普通 `break;`，这些 `break` 只退出内层 Java switch，不会退出外层 `while (true)`；后处理误把它们当作可退出无限循环的 break，因此保留了不可达的 fall-through state transition。

✅ **Fixed** — method-level goto state-machine cleanup now recognizes generated `for (; true; )` infinite loops and tracks nested breakable constructs so `break;` inside a nested switch/loop does not make code after the infinite loop appear reachable. After regenerating and rerunning Maven, the original `XmlTextReaderImpl.java:[2961,9]` unreachable statement disappeared; the next Maven first error is now `XmlTextReaderImpl.java:[3166,9] 无法访问的语句`.

---

## Iteration 28 — Method goto state-machine appends loop break after labeled block return
- **Java 文件**: D:\csharpxml-java\System.Private.Xml\src\main\java\dotnet\xml\XmlTextReaderImpl.java
- **行号**: 3166
- **错误信息**: `[ERROR] /D:/csharpxml-java/System.Private.Xml/src/main/java/dotnet/xml/XmlTextReaderImpl.java:[3166,9] 无法访问的语句`
- **代码片段**:
  ```java
          if (_fragmentType == XmlNodeType.None) {
          _fragmentType = (_rootElementParsed ? XmlNodeType.Document : XmlNodeType.Element);
          }
          onEof();
          return false;
          } }
          pos = _ps.charPos;
          chars = _ps.chars;
          }
          break __gotoLoop;
          default:
          break __gotoLoop;
  ```
- **对应 C# 文件**: D:\csharpxml\System\Xml\Core\XmlTextReaderImpl.cs
- **C# 原始代码**: `ParseDocumentContent()` 中 `for (;;)` 内部的 `ReadData:` 标签。该标签用于多个 `goto ReadData` 跳入缓冲区补读逻辑；`ReadData:` 标签块在 EOF 分支执行 `OnEof(); return false;`，否则更新 `pos/chars` 并回到外层无限循环。
- **根因分类**: Transformer method goto state-machine lowering / terminal labeled block analysis
- **涉及组件**: D:\code\cs2j\src\CSharpToJava.Core\Context\GotoAnalyzer.cs; D:\code\cs2j\src\CSharpToJava.Core\Transformers\Statement\StatementTransformer.LabelAndGoto.cs
- **分析**: 本轮与 Iteration 27 的差异是：上一轮不可达语句是 `for (;;)` 无限循环之后错误保留的 fall-through state transition；本轮不可达语句位于同一个 state-machine case 的尾部，是 block exit 被错误分类为 `FallThrough` 后补出的 `break __gotoLoop;`。`GotoAnalyzer.BuildBasicBlocks` 只识别顶层 `return`/`throw`/`break`/`continue`，对 `ReadData:` 这种标签内的复杂 `if/else` 块没有做“所有路径是否终止”的分析。转换后的 Java 中 EOF 分支含 `return false;`，非 EOF 分支只更新变量后落回无限循环；从 Java 可达性看，该 labeled block 后的 case 尾部 `break __gotoLoop;` 无法到达。前两轮都属于方法级或 switch 级 state-machine 的不可达清理，但本轮根因更早，出在 basic-block exit 分类没有理解 labeled statement body 的终止形态。

✅ **Fixed** — method-level goto state-machine cleanup now removes a `break __gotoLoop;` emitted immediately after a generated infinite loop whose body cannot normally exit. The focused regression first failed by matching `return false; ... break __gotoLoop;`, then passed after the cleanup was extended. After regenerating and rerunning Maven, the original `XmlTextReaderImpl.java:[3166,9]` unreachable `break __gotoLoop;` disappeared. Maven still fails; the next first error is now `XmlTextReaderImpl.java:[3168,9] 无法访问的语句` on the fallback `return false;` after the same nonbreaking state-machine loop.

---

## Iteration 29 — Method goto state-machine appends fallback return after nonbreaking infinite loop
- **Java 文件**: D:\csharpxml-java\System.Private.Xml\src\main\java\dotnet\xml\XmlTextReaderImpl.java
- **行号**: 3168
- **错误信息**: `[ERROR] /D:/csharpxml-java/System.Private.Xml/src/main/java/dotnet/xml/XmlTextReaderImpl.java:[3168,9] 无法访问的语句`
- **代码片段**:
  ```java
          }
          }
          }
          return false;

      }
          // Parses element content
  ```
- **对应 C# 文件**: D:\csharpxml\System\Xml\Core\XmlTextReaderImpl.cs
- **C# 原始代码**: `ParseDocumentContent()` 中 `for (;;)` 包含 `goto ReadData` 和 `ReadData:` 标签块；EOF 分支 `return false;`，非 EOF 分支更新 `pos/chars` 后继续外层无限循环，方法本身没有可正常落出的路径。
- **根因分类**: Transformer method goto state-machine lowering / fallback return reachability
- **涉及组件**: D:\code\cs2j\src\CSharpToJava.Core\Transformers\Statement\StatementTransformer.LabelAndGoto.cs
- **分析**: Iteration 28 已删除同一 state-machine case 尾部不可达的 `break __gotoLoop;`，但 `TransformBlockWithStateMachine` 随后仍按“非 void 方法 Java 需要兜底返回”的通用规则追加 `return false;`。这个兜底对 `switch goto case` 等可能落出 state-machine 的场景仍需要保留；但本例方法级 state-machine 主体中外层 `__gotoLoop` 的唯一可执行路径被一个不可正常退出的 C# `for (;;)` 包住，循环体只通过 `return/throw/continue` 或 state transition 继续执行，没有裸 `break` 能退出循环。Java 因此认定整个 `__gotoLoop` 后的 fallback return 不可达。应在 state-machine 清理后识别这种“不可正常落出”的主循环，并抑制方法尾部 fallback return；不要手改生成 Java。

✅ **Fixed** — method-level goto state-machine fallback generation now runs after unreachable infinite-loop cleanup and only emits a fallback when the cleaned state machine can still break out normally. The focused regression first failed by matching `__gotoLoop: while (true) ... return false;`, then passed after the fallback was suppressed for nonbreaking state machines while the existing goto-case fallback test stayed green. After regenerating and rerunning Maven, the original `XmlTextReaderImpl.java:[3168,9]` fallback `return false;` disappeared. Maven still fails; the next first error is now `XmlTextReaderImpl.java:[4292,9] 无法访问的语句` on `__state = 2; continue __gotoLoop;`.

---

## Iteration 30 — Method goto state-machine appends next-label transition after terminal labeled block
- **Java 文件**: D:\csharpxml-java\System.Private.Xml\src\main\java\dotnet\xml\XmlTextReaderImpl.java
- **行号**: 4292
- **错误信息**: `[ERROR] /D:/csharpxml-java/System.Private.Xml/src/main/java/dotnet/xml/XmlTextReaderImpl.java:[4292,9] 无法访问的语句`
- **代码片段**:
  ```java
          _curNode.setValueNode(nodeType, _stringBuilder.toString());
          _stringBuilder.setLength(0);
          if (!fullValue) {
          _nextParsingFunction = _parsingFunction;
          _parsingFunction = ParsingFunction.PartialTextValue;
          }
          return true;
          }
          }
          __state = 2;
          continue __gotoLoop;
          case 2: // IgnoredNode
          IgnoredNode: { if (_parsingFunction == ParsingFunction.ReportEndEntity) {
          setupEndEntityNodeInContent();
          ```
- **对应 C# 文件**: `D:\csharpxml\System\Xml\Core\XmlTextReaderImpl.cs`
- **C# 原始代码**: `ParseText()` 中根据 `_parsingMode`、`GetTextNodeType(...) == XmlNodeType.None` 等分支多次 `goto IgnoredNode`；主 `if/else` 文本解析分支在其他路径上都 `return true`，方法尾部 `IgnoredNode:` 根据 entity 状态 `return true` 或 `return false`。
- **根因分类**: Transformer method goto state-machine lowering / terminal labeled block analysis
- **涉及组件**: `D:\code\cs2j\src\CSharpToJava.Core\Context\GotoAnalyzer.cs`; `D:\code\cs2j\src\CSharpToJava.Core\Transformers\Statement\StatementTransformer.LabelAndGoto.cs`
- **分析**: `GotoAnalyzer.SplitIntoBasicBlocks` 只把顶层的 `goto`/`return`/`throw`/`break`/`continue` 识别为 block exit。`ParseText()` 的主 basic block 是复杂的顶层 `if/else`：若进入 `goto IgnoredNode` 分支，转换器已在嵌套位置生成 `__state = 2; continue __gotoLoop;`；其他路径都通过嵌套 `return true` 终止。Java 已能判定该 `if/else` 后不可达，但 analyzer 仍把该 block 标为 `FallThrough`，并因后面存在 `IgnoredNode:` basic block 而生成额外的 `__state = 2; continue __gotoLoop;`。这是 Iteration 29 之后剩下的同类 reachability 缺口：fallback return 已被抑制，但 case body 末尾为嵌套 block/if 后 `return` 时，next-label transition 仍按 basic-block fallthrough 补出。

✅ **Fixed** — method-level goto state-machine case-tail generation now suppresses synthetic fallthrough/conditional fallthrough transitions when the source statement at the end of a basic block cannot complete normally, including nested `if/else` blocks where both branches terminate via return/throw/goto/break/continue. The focused regression first failed by matching `return true; ... __state = N; continue __gotoLoop;`, then passed after the syntax-level terminal check was added; a companion test verifies a non-terminal labeled block still keeps a next-label transition. After regenerating and rerunning Maven, the original `XmlTextReaderImpl.java:[4292,9]` unreachable transition disappeared and line 4292 is now `case 2: // IgnoredNode`. Maven still fails; the next first error is now `XmlTextReaderImpl.java:[5086,9] 无法访问的语句` on `ReturnPartial: { ... }`.

---

## Iteration 31 — Goto-to-post-loop label inside nested switch exits only the switch
- **Java 文件**: `D:\csharpxml-java\System.Private.Xml\src\main\java\dotnet\xml\XmlTextReaderImpl.java`
- **行号**: 5086
- **错误信息**: `[ERROR] /D:/csharpxml-java/System.Private.Xml/src/main/java/dotnet/xml/XmlTextReaderImpl.java:[5086,9] 无法访问的语句`
- **代码片段**:
  ```java
          }
          throwInvalidChar(chars, _ps.charsUsed, pos);
          break;
          }
          }
          }
          ReturnPartial: { if (rcount > 0) {
          shiftBuffer(rpos + rcount, rpos, pos - rpos - rcount);
          outEndPos.value = pos - rcount;
          } else {
          outEndPos.value = pos;
  ```
- **对应 C# 文件**: `D:\csharpxml\System\Xml\Core\XmlTextReaderImpl.cs`
- **C# 原始代码**: `ParsePI(...)` 中 `for (;;)` 内部的 `switch (chars[pos])` 多处 `goto ReturnPartial;`，目标 `ReturnPartial:` 是紧跟在无限循环之后的标签块，最终设置 out 参数并 `return false;`。
- **根因分类**: Transformer goto-to-post-loop lowering / nested breakable construct handling
- **涉及组件**: `D:\code\cs2j\src\CSharpToJava.Core\Context\GotoAnalyzer.cs`; `D:\code\cs2j\src\CSharpToJava.Core\Transformers\Statement\StatementTransformer.LabelAndGoto.cs`
- **分析**: `GotoAnalyzer` 会把“goto 目标是当前外层 loop 的后继 sibling label”的情况分类为 `BreakFromEnclosingLoop`，转换器再把它降成裸 Java `break;`。这个降级只在 goto 不位于其他 breakable construct 内时成立；本例 `goto ReturnPartial` 位于 `for (;;)` 内层的 `switch` section 里，裸 `break;` 只退出 Java `switch`，不会退出外层无限循环。结果所有能到达 `ReturnPartial:` 的路径都被错误困在 `for (;;)` 中，Java 编译器把循环后的 `ReturnPartial: { ... }` 判为不可达。这里的标签块本身包含最终 `return false;`，但首错不是标签块 terminal 未识别导致尾部 fallback/transition 不可达；真正缺口是 sibling-label goto 在嵌套 `switch`/loop 中需要生成能退出正确外层 loop 的 labeled break（或等价 state transition），同时仍保留普通非嵌套 sibling-label goto 的落出语义。

✅ **Fixed** — goto-to-post-loop-label lowering now emits a synthetic Java loop label and `break <label>;` when the `goto` is nested inside another breakable construct such as `switch`, while preserving plain `break;` for non-nested loop exits. The focused regression first failed because `break __cs2jLoop` was absent, then passed after the loop-label context was added. After regenerating and rerunning Maven, the original `XmlTextReaderImpl.java:[5086,9]` unreachable `ReturnPartial:` error disappeared; Maven still fails and the next first error is now `XmlTextReaderImpl.java:[9326,9] 无法访问的语句`.

---

## Iteration 32 — Async Task implicit fallback after nonbreaking infinite loop
- **Java 文件**: `D:\csharpxml-java\System.Private.Xml\src\main\java\dotnet\xml\XmlTextReaderImpl.java`
- **行号**: 9326
- **错误信息**: `[ERROR] /D:/csharpxml-java/System.Private.Xml/src/main/java/dotnet/xml/XmlTextReaderImpl.java:[9326,9] 无法访问的语句`
- **代码片段**:
  ```java
          case Done:
          return CompletableFuture.completedFuture(null);
          }
          }
          return CompletableFuture.completedFuture(null);
      }
      private CompletableFuture parseEndElementAsync_CheckEndTag(int nameLen, NodeData startTagNode, LineInfo endTagLineInfo) {
  ```
- **对应 C# 文件**: `D:\csharpxml\System\Xml\Core\XmlTextReaderImplAsync.cs`
- **C# 原始代码**: `ParseEndElementAsync_Finish(Task task, ...)` 是 `async Task` helper；方法主体是 `while (true)`，循环内先 `await task.ConfigureAwait(false)`，再按 `_parseEndElement_NextFunc` 分支更新 `task` 或在 `Done` case 中 `return;`。循环没有可正常落出的 `break`。
- **根因分类**: async Task implicit completion fallback / syntax reachability analysis
- **涉及组件**: `D:\code\cs2j\src\CSharpToJava.Core\Transformers\Member\MethodTransformer.cs`
- **分析**: 转换器为 `async Task` 非泛型方法补 `return CompletableFuture.completedFuture(null);` 来模拟 C# 方法末尾隐式完成 Task。原判断只检查生成后的最后一条 Java 语句是否已经是 `return`，无法识别 C# 源最后一条 `while (true)` 本身不可正常完成。本例循环内部的 `Done` case 已经把 C# 的 `return;` 转为 `return CompletableFuture.completedFuture(null);`，循环没有 `break`，因此循环后的隐式 fallback 在 Java 中不可达。需要在补 async Task fallback 前基于 C# 语法判断方法尾部是否仍可能落出；不可落出的 `while(true)` / `for(;;)` 应抑制 fallback，而含可达 `break` 的循环、条件早退后仍可落出的 async Task 方法仍必须保留 fallback。

✅ **Fixed** — async `Task` fallback generation now checks whether the C# method body can complete normally before appending `CompletableFuture.completedFuture(null)`. The focused regression first failed by matching `while (true) ... return CompletableFuture.completedFuture(null);`, then passed after suppressing the fallback for nonbreaking infinite loops; a companion test verifies a `while(true)` with reachable `break` still emits the implicit Task completion fallback. After regenerating and rerunning Maven, the original `XmlTextReaderImpl.java:[9326,9]` unreachable fallback disappeared and line 9326 is now the method closing brace. Maven still fails; the next first error is now `XmlTextReaderImpl.java:[9650,9] 无法访问的语句` on `return CompletableFuture.completedFuture(null);` after `throw new IllegalStateException("Unexpected state");`.

---

## Iteration 33 — Async Task fallback appended after state-machine terminal throw
- **Java 文件**: `D:\csharpxml-java\System.Private.Xml\src\main\java\dotnet\xml\XmlTextReaderImpl.java`
- **行号**: 9650
- **错误信息**: `[ERROR] /D:/csharpxml-java/System.Private.Xml/src/main/java/dotnet/xml/XmlTextReaderImpl.java:[9650,9] 无法访问的语句`
- **代码片段**:
  ```java
          }
          }
          throw new IllegalStateException("Unexpected state");

          return CompletableFuture.completedFuture(null);
      }
      private CompletableFuture parseAttributeValueSlowAsync(int curPos, char quoteChar, NodeData attr) {
  ```
- **对应 C# 文件**: `D:\csharpxml\System\Xml\Core\XmlTextReaderImplAsync.cs`
- **C# 原始代码**: `ParseAttributesAsync()` 是 `async Task`，方法中包含 `continue;`、`ReadData:` 与 `End:` 标签；转换器因此把方法降成 `__gotoLoop` state machine。`End:` 标签完成默认属性、命名空间和重复属性处理后自然结束。
- **根因分类**: async Task implicit completion fallback / generated state-machine terminal statement detection
- **涉及组件**: `D:\code\cs2j\src\CSharpToJava.Core\Transformers\Member\MethodTransformer.cs`; `D:\code\cs2j\src\CSharpToJava.Core\Transformers\Statement\StatementTransformer.LabelAndGoto.cs`
- **分析**: 上一轮的 `AsyncTaskBodyCanFallThrough` 只基于 C# 源语法判断方法末尾是否可能落出，能识别不可正常完成的 `while(true)`，但看不到 `TransformBlockWithStateMachine` 后续为非预定义返回类型追加的 synthetic `throw new IllegalStateException("Unexpected state")`。本例 C# 源尾部的 `End:` 标签可自然完成，所以 async fallback 逻辑决定追加 `return CompletableFuture.completedFuture(null);`；但生成后的结构化方法体最后一条已经是 state-machine fallback `throw`，Java 将 throw 后的 completedFuture return 判为不可达。终止检测漏掉 throw 的原因是它检查的是转换前 `methodDecl.Body`，没有检查转换后的 `javaMethod.StructuredBody.Statements`。

✅ **Fixed** — async `Task` fallback generation now also checks the generated structured body's last non-empty line before appending `CompletableFuture.completedFuture(null)`, so a synthetic state-machine `throw new IllegalStateException("Unexpected state");` is treated as terminal. The focused regression first failed by matching `throw new IllegalStateException("Unexpected state"); ... return CompletableFuture.completedFuture(null);`, then passed after the generated-body terminal check was added. After regenerating and rerunning Maven, the original `XmlTextReaderImpl.java:[9650,9]` unreachable fallback disappeared and line 9650 is now the method closing brace. Maven still fails; the next first error is now `XmlCharType.java:[68,22] 找不到符号`.

---

## Iteration 34 — Generic Interlocked.CompareExchange mapped to AtomicInteger
- **Java 文件**: `D:\csharpxml-java\System.Private.Xml\src\main\java\dotnet\xml\XmlCharType.java`
- **行号**: 68
- **错误信息**: `[ERROR] /D:/csharpxml-java/System.Private.Xml/src/main/java/dotnet/xml/XmlCharType.java:[68,22] 找不到符号`
- **完整符号信息**:
  ```text
  符号:   方法 compareExchange(io.github.ningpp.compat.ObjectHolder<java.lang.Object>,java.lang.Object,<nulltype>)
  位置: 类 java.util.concurrent.atomic.AtomicInteger
  ```
- **代码片段**:
  ```java
          if (s_Lock == null) {
          Object o = new Object();
          ObjectHolder<Object> _s_LockRef = new ObjectHolder<>(s_Lock);
          AtomicInteger.compareExchange(_s_LockRef, o, null);
          s_Lock = _s_LockRef.value;
          }
          return s_Lock;
  ```
- **对应 C# 文件**: `D:\csharpxml\System\Xml\XmlCharType.cs`
- **C# 原始代码**: `StaticLock.get` 中 `Interlocked.CompareExchange<object>(ref s_Lock, o, null);`
- **根因分类**: BCL static method mapping / generic ref argument lowering
- **涉及组件**: `D:\code\cs2j\config\TypeMappings.json`; `D:\code\cs2j\src\CSharpToJava.Core\Transformers\Expression\Transformers\InvocationExpressionTransformer.cs`; `D:\code\cs2j\src\CSharpToJava.Core\Transformers\Expression\Transformers\ArgumentTransformer.cs`
- **分析**: `config\TypeMappings.json` 把整个 `System.Threading.Interlocked` 类型映射到 `java.util.concurrent.atomic.AtomicInteger`。这个映射只可能适合少量整数场景；本例是泛型引用类型重载 `Interlocked.CompareExchange<T>(ref T location1, T value, T comparand)`。`ArgumentTransformer` 正确地为 `ref s_Lock` 生成了 `ObjectHolder<Object>` 并在调用后写回，但 `InvocationExpressionTransformer` 随后把静态接收者改成 `AtomicInteger`，生成了 Java 标准库不存在的 `AtomicInteger.compareExchange(ObjectHolder<Object>, Object, null)`。根因不是缺 import，也不是 `ObjectHolder` 生成错误，而是对 `Interlocked.CompareExchange` 缺少按重载/参数形态的专门 lowering，需要生成 converter compat helper 调用以接收 holder 并返回/更新旧值。

✅ **Fixed** — `System.Threading.Interlocked.CompareExchange` calls are now lowered before the generic static type mapping path, producing `InterlockedHelper.compareExchange(...)` with the existing generated ref holder instead of `AtomicInteger.compareExchange(...)`. A new compat helper implements the holder update semantics for object, int, long, float, and double holders. The focused regression first failed because `InterlockedHelper.compareExchange(_s_LockRef, o, null)` was absent, then passed after the invocation special case and helper were added. After regenerating and rerunning Maven, the original `XmlCharType.java:[68,22]` `AtomicInteger.compareExchange(ObjectHolder<Object>, Object, null)` error disappeared; line 69 is now `InterlockedHelper.compareExchange(_s_LockRef, o, null);`. Maven still fails; the next first error is now `XmlCharType.java:[79,9] 找不到符号` for `UnmanagedMemoryStream`.

---

## Iteration 35 — Manifest resource stream keeps unmapped UnmanagedMemoryStream
- **Java 文件**: `D:\csharpxml-java\System.Private.Xml\src\main\java\dotnet\xml\XmlCharType.java`
- **行号**: 79
- **错误信息**: `[ERROR] /D:/csharpxml-java/System.Private.Xml/src/main/java/dotnet/xml/XmlCharType.java:[79,9] 找不到符号`
- **完整符号信息**:
  ```text
  符号:   类 UnmanagedMemoryStream
  位置: 类 dotnet.xml.XmlCharType
  [ERROR] /D:/csharpxml-java/System.Private.Xml/src/main/java/dotnet/xml/XmlCharType.java:[79,44] 找不到符号
  符号:   类 UnmanagedMemoryStream
  位置: 类 dotnet.xml.XmlCharType
  [ERROR] /D:/csharpxml-java/System.Private.Xml/src/main/java/dotnet/xml/XmlCharType.java:[79,95] 找不到符号
  符号:   方法 getManifestResourceStream(java.lang.String)
  位置: 类 java.lang.Package
  ```
- **代码片段**:
  ```java
          }
          UnmanagedMemoryStream memStream = (UnmanagedMemoryStream)(XmlWriter.class.getPackage().getManifestResourceStream("XmlCharType.bin"));
          // Debug.Assert(memStream.getLength() == CharPropertiesSize);

          MemorySegment chProps = memStream.getPositionPointer();
  ```
- **对应 C# 文件**: `D:\csharpxml\System\Xml\XmlCharType.cs`
- **C# 原始代码**: `UnmanagedMemoryStream memStream = (UnmanagedMemoryStream)typeof(XmlWriter).Assembly.GetManifestResourceStream("XmlCharType.bin");` followed by `byte* chProps = memStream.PositionPointer;`
- **根因分类**: type mapping missing + manifest resource stream lowering missing + compat class missing
- **涉及组件**: `D:\code\cs2j\config\TypeMappings.json`; `D:\code\cs2j\src\CSharpToJava.Core\Transformers\Expression\Transformers\InvocationExpressionTransformer.cs`; `D:\code\cs2j\java\csharptojava-compat`
- **分析**: `System.IO.UnmanagedMemoryStream` has no TypeMappings entry, so the generated local declaration and cast keep a raw Java `UnmanagedMemoryStream` simple name with no import. The receiver side is also wrong: `System.Type.Assembly` is globally mapped to `getPackage`, so `typeof(XmlWriter).Assembly.GetManifestResourceStream(...)` becomes `XmlWriter.class.getPackage().getManifestResourceStream(...)`, but Java `Package` has no such method and the existing `AssemblyCompat` class also lacks this API. This is not a case for `StreamWrapper` or `MemoryStream`: the C# use immediately consumes `PositionPointer`, so the Java surface must expose a `MemorySegment` over the resource bytes. The minimal fix is a compat `UnmanagedMemoryStream` with `getPositionPointer()`/`getLength()`, a mapping/import for the C# type, and a converter special case that lowers manifest resource reads to an `AssemblyCompat` helper instead of the generic `Type.Assembly` property mapping.

✅ **Fixed** — `System.IO.UnmanagedMemoryStream` now maps to a compat `UnmanagedMemoryStream`, the compat runtime exposes `getPositionPointer()` over resource bytes, and `typeof(T).Assembly.GetManifestResourceStream(name)` lowers to `AssemblyCompat.getManifestResourceStream(T.class, name)` instead of Java `Package`. The focused converter regression first failed because `AssemblyCompat` was absent from the generated imports and `.class.getPackage().getManifestResourceStream(...)` remained, then passed after the lowering special case and mapping were added. After regenerating and rerunning Maven, the original `XmlCharType.java:[79,9]` `UnmanagedMemoryStream` error disappeared; line 81 is now `UnmanagedMemoryStream memStream = (UnmanagedMemoryStream)(AssemblyCompat.getManifestResourceStream(XmlWriter.class, "XmlCharType.bin"));`. Maven still fails; the next first error is now `XmlDocument.java:[161,29] 找不到符号` for `Hashtable.add(XmlName, XmlName)`.

## Iteration 36 — Explicit event accessors emit delegate +=/-= as Java compound assignment
- **Java 文件**: `D:\csharpxml-java\System.Private.Xml\src\main\java\dotnet\xml\XmlDocument.java`
- **行号**: 1003
- **错误信息**: `[ERROR] /D:/csharpxml-java/System.Private.Xml/src/main/java/dotnet/xml/XmlDocument.java:[1003,34] 二元运算符 '+' 的操作数类型错误`
- **完整类型信息**:
  ```text
  第一个类型:  dotnet.xml.XmlNodeChangedEventHandler
  第二个类型: dotnet.xml.XmlNodeChangedEventHandler
  ```
- **代码片段**:
  ```java
          n.writeTo(xw);
          }
      }
      public void addNodeInsertingListener(XmlNodeChangedEventHandler handler) {
          _onNodeInsertingDelegate += handler;
      }
      public void removeNodeInsertingListener(XmlNodeChangedEventHandler handler) {
          _onNodeInsertingDelegate -= handler;
  ```
- **对应 C# 文件**: `D:\csharpxml\System\Xml\Dom\XmlDocument.cs`
- **C# 原始代码**: explicit event accessors for `public event XmlNodeChangedEventHandler NodeInserting` call `_onNodeInsertingDelegate += value;` and `_onNodeInsertingDelegate -= value;` against a private delegate backing field.
- **根因分类**: Transformer
- **涉及组件**: `D:\code\cs2j\src\CSharpToJava.Core\Transformers\Member\EventFieldTransformer.cs`; `D:\code\cs2j\src\CSharpToJava.Core\Transformers\Expression\Transformers\AssignmentTransformer.cs`; `D:\code\cs2j\java\csharptojava-compat\src\main\java\io\github\ningpp\compat\DelegateHelper.java`
- **分析**: `EventFieldTransformer.TransformExplicitEvent` preserves explicit accessor bodies by running the normal statement/expression transformers and replacing C# `value` with Java `handler`. `AssignmentTransformer` handles `event += handler` when the left side resolves to `IEventSymbol`, but here the left side is a private delegate field (`IFieldSymbol` whose type is a delegate). Since no branch recognizes delegate-valued fields, the generic compound assignment path emits `_onNodeInsertingDelegate += handler` / `-= handler`, which Java rejects for functional-interface values. The existing compat `DelegateHelper.combine/remove` indicates the intended lowering surface is available; the missing piece is transformer lowering for `+=`/`-=` where the left operand type is a delegate.

✅ **Fixed** — delegate-typed `+=`/`-=` assignments now lower through `DelegateHelper.combine/remove` when the left operand is a field/property/local/parameter whose type is a C# delegate. The focused regression first failed because the generated accessor body had no `DelegateHelper` import and still emitted raw `_changed += handler`; after the transformer fix it passed with `_changed = DelegateHelper.combine(_changed, handler);` and `_changed = DelegateHelper.remove(_changed, handler);`. `dotnet build`, the focused test, and full `dotnet test` pass. After regenerating and rerunning Maven, the original `XmlDocument.java:[1003,34]` delegate `+` error disappeared; line 1004 now uses `DelegateHelper.combine`. Maven still fails; the next first error is now `XmlAttributeCollection.java:[14,14] dotnet.xml.XmlAttributeCollection不是抽象的, 并且未覆盖java.util.Collection中的抽象方法clear()`.

---

## Iteration 37 — Non-generic ICollection emitted as Java Collection implements clause
- **Java 文件**: `D:\csharpxml-java\System.Private.Xml\src\main\java\dotnet\xml\XmlAttributeCollection.java`
- **行号**: 14
- **错误信息**: `[ERROR] /D:/csharpxml-java/System.Private.Xml/src/main/java/dotnet/xml/XmlAttributeCollection.java:[14,14] dotnet.xml.XmlAttributeCollection不是抽象的, 并且未覆盖java.util.Collection中的抽象方法clear()`
- **代码片段**:
  ```java
  import io.github.ningpp.compat.*;

  public final class XmlAttributeCollection extends XmlNamedNodeMap implements Collection {
      public XmlAttributeCollection(XmlNode parent) {
          super(parent);
      }
  ```
- **对应 C# 文件**: `D:\csharpxml\System\Xml\Dom\XmlAttributeCollection.cs`
- **C# 原始代码**: `public sealed class XmlAttributeCollection : XmlNamedNodeMap, ICollection`，并通过显式接口成员实现 `ICollection.CopyTo(Array,int)`、`ICollection.IsSynchronized`、`ICollection.SyncRoot` 和 `ICollection.Count`。
- **根因分类**: Transformer / declaration-site interface mapping
- **涉及组件**: `D:\code\cs2j\src\CSharpToJava.Core\Transformers\Type\ClassTransformer.cs`; `D:\code\cs2j\config\TypeMappings.json`
- **分析**: `config\TypeMappings.json` maps `System.Collections.ICollection` to Java `Collection`, which is acceptable for some use-site collection APIs but wrong as an implemented interface. C# non-generic `ICollection` is much smaller than `java.util.Collection`: it does not require `clear`, `add`, `remove`, `containsAll`, etc. `ClassTransformer` already has special declaration-site handling for `ICollection<T>` to avoid overpromising Java `Collection`, but the heuristic treats the presence of `Count` as a full collection implementation and keeps `implements Collection`. In this non-generic case the generated class correctly contains `copyTo`, `getIsSynchronized`, `getSyncRoot`, and `getCount`, yet Java requires the unrelated full `Collection` contract and fails on `clear()`. The fix should make declaration-site `System.Collections.ICollection` use a weaker iterable-compatible contract instead of Java `Collection`, while preserving `size()` bridging from `getCount()`.

✅ **Fixed** — declaration-site `System.Collections.ICollection` now maps to `Iterable` even when explicit non-generic collection members such as `ICollection.Count` are present. Generic `ICollection<T>` keeps the existing heuristic: full implementations may still bridge to Java `Collection`, while partial/custom collection classes use `Iterable`. The focused regression first failed with `NodeCollection extends NodeMap implements Collection`, then passed after the helper split non-generic and generic collection interface handling. `dotnet build`, the focused test, and full `dotnet test` pass. After regenerating and rerunning Maven, the original `XmlAttributeCollection.java:[14,14]` `clear()` contract error disappeared; the generated declaration is now `public final class XmlAttributeCollection extends XmlNamedNodeMap implements Iterable`. Maven still fails; the next first error is now `XmlEncodedRawTextWriter.java:[144,84] 不兼容的类型: dotnet.xml.CharEntityEncoderFallback无法转换为io.github.ningpp.compat.EncoderReplacementFallback`.

---

## Iteration 38 — Encoding.GetEncoding overload rejects custom EncoderFallback
- **Java 文件**: `D:\csharpxml-java\System.Private.Xml\src\main\java\dotnet\xml\XmlEncodedRawTextWriter.java`
- **行号**: 144
- **错误信息**: `[ERROR] /D:/csharpxml-java/System.Private.Xml/src/main/java/dotnet/xml/XmlEncodedRawTextWriter.java:[144,84] 不兼容的类型: dotnet.xml.CharEntityEncoderFallback无法转换为io.github.ningpp.compat.EncoderReplacementFallback`
- **代码片段**:
  ```java
          _charEntityFallback = new CharEntityEncoderFallback();
          // grab bom before possibly changing encoding settings
          ReadOnlySpan<Integer> bom = MemoryExtensions.asSpan(encoding.getPreamble());
          // the encoding instance this creates can differ from the one passed in
          this.encoding = Encoding.getEncoding(settings.getEncoding().getCodePage(), _charEntityFallback, settings.getEncoding().getDecoderFallback());
          encoder = (Encoder)(Object)(encoding.getEncoder());
          if (!stream.getCanSeek() || stream.getPosition() == 0) {
  ```
- **对应 C# 文件**: `D:\csharpxml\System\Xml\Core\XmlEncodedRawTextWriter.cs`
- **C# 原始代码**: The constructor creates `_charEntityFallback = new CharEntityEncoderFallback();` where `CharEntityEncoderFallback : EncoderFallback`, then calls `Encoding.GetEncoding(settings.Encoding.CodePage, _charEntityFallback, settings.Encoding.DecoderFallback);`.
- **根因分类**: compat runtime API shape
- **涉及组件**: `D:\code\cs2j\java\csharptojava-compat\src\main\java\io\github\ningpp\compat\Encoding.java`
- **分析**: The converter correctly maps `System.Text.EncoderFallback` and the custom C# subclass to compat `EncoderFallback`, and correctly lowers the static `Encoding.GetEncoding` call to `Encoding.getEncoding(...)`. The compile error comes from the compat overload signature itself: it accepts only `EncoderReplacementFallback`, even though the .NET API accepts any `EncoderFallback`. `CharEntityEncoderFallback` is a valid custom fallback subclass, so Java rejects passing it to the too-narrow compat overload. This is not a TypeMappings or transformer issue; the generated call exposes a semantic mismatch in the compat runtime surface. The minimal fix is to widen the encoder parameter to `EncoderFallback` while preserving replacement-string behavior when the fallback is an `EncoderReplacementFallback`.

✅ **Fixed** — `Encoding.getEncoding(int, ..., DecoderReplacementFallback)` now accepts the abstract compat `EncoderFallback` type instead of only `EncoderReplacementFallback`, while preserving replacement-string behavior for replacement fallback instances. The focused regression first failed because `Encoding.java` still contained the narrow `EncoderReplacementFallback` overload, then passed after the runtime signature was widened. `dotnet build`, the focused test, full `dotnet test`, and `mvn -f java\csharptojava-compat\pom.xml clean install -e` all pass. After regenerating and rerunning Maven for `D:\csharpxml-java`, the original `XmlEncodedRawTextWriter.java:[144,84]` `CharEntityEncoderFallback` mismatch disappeared. Maven still fails; the next first error is now `XmlEncodedRawTextWriter.java:[1333,34] 不兼容的类型: io.github.ningpp.compat.ObjectHolder<java.lang.foreign.MemorySegment>无法转换为java.lang.foreign.MemorySegment`.
---

## Iteration 1 - TextWriter async methods emitted on PrintWriter
- **Java file**: `D:\csharpxml-java\System.Private.Xml\src\main\java\dotnet\xml\XmlEncodedRawTextWriter.java`
- **Line**: 1965
- **Error**: `[ERROR] /D:/csharpxml-java/System.Private.Xml/src/main/java/dotnet/xml/XmlEncodedRawTextWriter.java:[1965,15] cannot find symbol`
- **Full symbol details**:
  ```text
  symbol:   method flushAsync()
  location: variable writer of type java.io.PrintWriter
  ```
- **Generated Java context**:
  ```java
          stream.flushAsync().join();
          } else {
          if (writer != null) {
          writer.flushAsync().join();
          }
          }
          return CompletableFuture.completedFuture(null);
  ```
- **C# source**: `D:\csharpxml\System\Xml\Core\XmlEncodedRawTextWriterAsync.cs`
- **Original C#**: `await writer.FlushAsync().ConfigureAwait(false);`
- **Root cause classification**: Transformer
- **Components**: `D:\code\cs2j\src\CSharpToJava.Core\Transformers\Expression\Transformers\InvocationExpressionTransformer.cs`; `D:\code\cs2j\src\CSharpToJava.Core\Transformers\Expression\Transformers\ControlFlowTransformer.cs`; `D:\code\cs2j\config\TypeMappings.json`
- **Analysis**: `System.IO.TextWriter` maps to `java.io.PrintWriter`, and the converter already maps synchronous `TextWriter.Write`/`WriteLine` overloads through `TypeMappings.json`. Async `TextWriter.FlushAsync()` and `TextWriter.WriteAsync(char[], int, int)` were not mapped, so `InvocationExpressionTransformer` fell through to the generic camelCase member-call path and emitted `writer.flushAsync()` / `writer.writeAsync(...)`. `ControlFlowTransformer` then lowered `await` to `.join()`, which is valid for compat async APIs such as `StreamWrapper.flushAsync()`, but invalid for `PrintWriter` because Java's `PrintWriter` only exposes synchronous `flush()` / `write(...)`. The semantic model correctly identifies the receiver type as `System.IO.TextWriter`; the missing piece was transformer/member mapping for TextWriter async calls to synchronous PrintWriter calls before the await `.join()` is added.

✅ **Fixed** - `TextWriter.FlushAsync()` and `TextWriter.WriteAsync(char[], int, int)` now lower to `CompletableFuture.runAsync(...)` around synchronous `PrintWriter.flush()` / `PrintWriter.write(...)`, so the existing `await` lowering can safely append `.join()`. The focused regression first failed because generated code contained `w.flushAsync().join()`, then passed after the transformer special case. `dotnet build`, the focused test, and full `dotnet test` pass. After regenerating and rerunning Maven, the original `XmlEncodedRawTextWriter.java:[1965,15]` `PrintWriter.flushAsync()` error disappeared; line 1965 is now `CompletableFuture.runAsync(() -> writer.flush()).join();`, and line 1998 is now `CompletableFuture.runAsync(() -> writer.write(bufChars, 1, bufPos - 1)).join();`. Maven still fails; the next first error is now `HtmlEncodedRawTextWriter.java:[257,181] int cannot be dereferenced`.
