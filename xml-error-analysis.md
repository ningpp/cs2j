# XML 转换编译错误分析

## Iteration 1 — 枚举器类型不兼容 (CSharpEnumerator vs CSharpGenericEnumerator)

### 错误组 A: 嵌套枚举器类未升级为泛型

- **Java 文件**: `system-private-xml/src/main/java/dotnet/xml/schema/XmlSchemaObjectTable.java`
- **行号**: 260, 302
- **错误信息**: 不兼容的类型: dotnet.xml.schema.XmlSchemaObjectTable.XSOEnumerator无法转换为io.github.ningpp.compat.CSharpGenericEnumerator<java.lang.Object>
- **代码片段**:
  ```java
  // line 259-261
  public CSharpGenericEnumerator<Object> iterator() {
      return new XSOEnumerator(_entries, _size, EnumeratorType.Keys);
  }
  // line 311
  public static class XSOEnumerator implements CSharpEnumerator {
  ```

- **对应 C# 文件**: `D:\csharpxml\System\Xml\Schema\XmlSchemaObjectTable.cs`
- **根因分类**: Transformer 逻辑缺陷
- **涉及组件**: `src/CSharpToJava.Core/Transformers/Type/ClassTransformer.cs` — `UpgradeIteratorReturnTypeForGenericIterable` + `UpgradeNestedEnumeratorToGeneric`
- **分析**: `UpgradeIteratorReturnTypeForGenericIterable` 只在顶级类上调用。当顶级类本身不实现 IEnumerable/ICollection 但其嵌套类实现了 ICollection 时，`TryGetIterableElementType` 返回 false，导致嵌套类的枚举器未被升级。`XSOEnumerator implements CSharpEnumerator` 但 `NamesCollection.iterator()` 返回类型被接口 `CSharpICollection<Object>` 要求为 `CSharpGenericEnumerator<Object>`。

### 错误组 B: 顶级类 iterator() 返回类型未升级

- **Java 文件**: `system-private-xml/src/main/java/dotnet/xml/serialization/XmlSchemas.java`
- **行号**: 106, 116, 246, 288, 325
- **错误信息**: 
  - line 288: `iterator()` 返回 `CSharpEnumerator` 但接口要求 `CSharpGenericEnumerator<Object>`
  - line 106,116,246,325: `CSharpIList` 无法转换为 `CSharpGenericIList<Object>`
- **代码片段**:
  ```java
  // line 105-106
  public dotnet.xml.schema.XmlSchema get(String ns) {
      CSharpGenericIList<Object> values = (CSharpIList)(getSchemaSet().schemas(ns));
  // line 288
  public CSharpEnumerator iterator() {
      return CSharpEnumerator.from(new XmlSchemaEnumerator(this));
  }
  ```
- **对应 C# 文件**: `D:\csharpxml\System\Xml\Serialization\XmlSchemas.cs`
- **根因分类**: Transformer 逻辑缺陷
- **涉及组件**: 同上 `ClassTransformer.cs`
- **分析**: `XmlSchemas` 实现了 `CSharpICollection<Object>`（来自 C# `IList`），但 `iterator()` 返回 `CSharpEnumerator` 而非 `CSharpGenericEnumerator<Object>`。`UpgradeIteratorReturnTypeForGenericIterable` 未被触发或 `TryGetIterableDataType` 未正确识别。

### 错误组 C: XPathArrayIterator 枚举器返回类型不兼容

- **Java 文件**: `system-private-xml/src/main/java/MS/Internal/Xml/XPath/XPathArrayIterator.java`
- **行号**: 96
- **错误信息**: 不兼容的类型: io.github.ningpp.compat.CSharpEnumerator无法转换为io.github.ningpp.compat.CSharpGenericEnumerator<java.lang.Object>
- **代码片段**:
  ```java
  // line 95-96
  public CSharpGenericEnumerator<Object> iterator() {
      return CSharpEnumerator.from(list.iterator());
  }
  ```
- **对应 C# 文件**: `D:\csharpxml\MS\Internal\Xml\XPath\XPathArrayIterator.cs`
- **根因分类**: Transformer 逻辑缺陷
- **涉及组件**: 同上 `ClassTransformer.cs`
- **分析**: 返回类型已升级为 `CSharpGenericEnumerator<Object>` 但 body 中的 `CSharpEnumerator.from(...)` 返回 `CSharpEnumerator`，未替换为 `CSharpGenericEnumerator.fromEnumerator(...)`。这表明 `UpgradeIteratorReturnTypeForGenericIterable` 的 body 替换逻辑可能未覆盖到这种情况。

### 错误 3: XmlSchemaCollection — 缺少返回值

- **Java 文件**: `system-private-xml/src/main/java/dotnet/xml/schema/XmlSchemaCollection.java`
- **行号**: 159
- **错误信息**: 不兼容的类型: 缺少返回值
- **代码片段**:
  ```java
  // line 154-160
  public boolean add(XmlSchemaCollection schema) {
      if (schema == null) {
      throw new ArgumentNullException("schema");
      }
      if (this == schema) {
      return;  // ← line 159: 方法声明返回 boolean 但此处 return; 无返回值
      }
  ```
- **对应 C# 文件**: `D:\csharpxml\System\Xml\Schema\XmlSchemaCollection.cs`
- **根因分类**: Transformer 逻辑缺陷
- **分析**: C# `if (this == schema) return;` 中的 `return;` 在返回 `bool` 的方法中合法（等价于 `return false;` 或 `return default;`），但 Java 不允许。转换器需要将 void return 转为 `return false;`。

### 错误 5-6: StringConcat — var 推断 + switch 布尔

- **Java 文件**: `system-private-xml/src/main/java/dotnet/xml/StringConcat.java`
- **行号**: 137, 174
- **错误信息**: 
  - line 137: 无法推断本地变量 result 的类型 (变量初始化程序为 'null')
  - line 174: switch 有布尔值和一个 default 标签
- **代码片段**:
  ```java
  // line 137
  var result = null;
  // line 143-174
  switch (result._idxStr) {  // switch on boolean
  ...
  default: { __state = 6; continue; }
  }
  ```
- **对应 C# 文件**: `D:\csharpxml\System\Xml\StringConcat.cs`
- **根因分类**: Transformer 逻辑缺陷
- **分析**: `var result = null;` 需要显式类型。`result._idxStr` 可能是 `bool` 类型，Java 不允许 switch on bool with default。

### 错误 7-9: XmlSqlBinaryReader — struct 变异 + 方法签名

- **Java 文件**: `system-private-xml/src/main/java/dotnet/xml/XmlSqlBinaryReader.java`
- **行号**: 1855, 2628, 4534
- **错误信息**: 
  - line 1855: `SymbolTables` 无法转换为 `int`
  - line 2628: `getLocalnameAndNamespaceUriAndHash` 参数数量不匹配 (3 vs 4)
  - line 4534: `AttrInfo` 无法转换为 `int`
- **代码片段**:
  ```java
  // line 1855
  _symbolTables = _symbolTables.withsymCount(_symbolTables = _symbolTables.withqnameCount(1));
  // line 4534
  return ((result = result.withhashCode(result.getName().getNSHashCode(hasher)))).clone();
  ```
- **对应 C# 文件**: `D:\csharpxml\System\Xml\BinaryXml\XmlBinaryReader.cs`
- **根因分类**: Transformer 逻辑缺陷 (struct 变异 lowering)
- **分析**: C# struct 变异赋值 `_symbolTables.symCount = (_symbolTables.qnameCount = 1)` 被转为函数式风格但嵌套赋值返回值类型错误。`return this.hashCode = ...` 被转为 `return ((result = result.withhashCode(...))).clone();` 但 `.clone()` 返回 Object 而非 int。

### 错误 10: Processor — Activator 引用不明确

- **Java 文件**: `system-private-xml/src/main/java/dotnet/xml/Xsl/XsltOld/Processor.java`
- **行号**: 171
- **错误信息**: 对Activator的引用不明确 — `io.github.ningpp.compat.Activator` 和 `dotnet.system.Activator` 都匹配
- **代码片段**:
  ```java
  _scriptExtensions.add(namespaceUri, Activator.createInstance(...));
  ```
- **对应 C# 文件**: `D:\csharpxml\System\Xml\Xsl\XsltOld\Processor.cs`
- **根因分类**: 类型映射冲突 / import 歧义
- **分析**: `Activator` 类在 `io.github.ningpp.compat` 和 `dotnet.system` 两个包中都存在，转换器同时导入了两个，导致歧义。

### 错误 16: ReflectionXmlSerializationReaderHelper — 找不到符号 BinaryExpression

- **Java 文件**: `system-private-xml/src/main/java/dotnet/xml/serialization/ReflectionXmlSerializationReaderHelper.java`
- **行号**: 79
- **错误信息**: 找不到符号 — 类 BinaryExpression
- **代码片段**:
  ```java
  var assignExpr = (BinaryExpression) Expression.assign(fieldExpr, valueParam);
  ```
- **对应 C# 文件**: 转换器生成的辅助类（无直接 C# 源文件）
- **根因分类**: 类型映射缺失 / import 缺失
- **分析**: `BinaryExpression`、`MemberExpression`、`ParameterExpression` 等 `System.Linq.Expressions` 类型缺少 import 或类型映射。
