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
