# xml-error-analysis.md

## Iteration 1 — cannot find symbol (join)
- **Java 文件**: `system-private-xml/src/main/java/dotnet/xml/XmlEventCache.java`
- **行号**: 80
- **错误信息**: `找不到符号 符号: 方法 join() 位置: 类型为dotnet.xml.StringConcat的变量 _singleText`
- **代码片段**:
  ```java
  if (_singleText.getCount() != 0) {
      writer.writeString(_singleText.join());
      return;
  }
  ```
- **对应 C# 文件**: `System/Xml/Core/XmlEventCache.cs` 与 `System/Xml/Core/StringConcat.cs`
- **根因分类**: Transformer
- **涉及组件**: `src/CSharpToJava.Core/Transformers/Expression/Transformers/InvocationExpressionTransformer.cs` (L643-655)
- **分析**: `TransformMemberInvocation` 把所有无参 `.GetResult()` 调用都改写成 `.join()`，未限定接收者类型。`StringConcat.GetResult()` 是普通取值方法，应生成 `.getResult()`，却被错写成 `.join()`。
- **状态**: ✅ Fixed
