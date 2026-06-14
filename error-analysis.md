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
