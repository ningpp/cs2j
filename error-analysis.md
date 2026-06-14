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
