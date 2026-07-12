# XML Project Error Analysis

## Iteration 1 — 意外的类型 (unexpected type in implements clause)

- **Java 文件**: XmlNamespaceManager.java:24, XmlNode.java:20, XmlAttributeCollection.java:17, XmlNamedNodeMap.java:18, XmlNodeList.java:15
- **行号**: 各文件 implements 行
- **错误信息**: 意外的类型 — `CSharpGenericIterable<?>` 中通配符 `?` 不能用于 implements 子句
- **代码片段**:
  ```java
  public class XmlNamespaceManager implements IXmlNamespaceResolver, CSharpGenericIterable<?>, Iterable<Object> {
  ```

- **对应 C# 文件**: D:\csharpxml 中实现 `System.Collections.IEnumerable`（非泛型）的类
- **根因分类**: 类型映射缺陷
- **涉及组件**: d:\code\cs2j\config\TypeMappings.json (L212-216)
- **分析**: TypeMappings.json 将非泛型 `IEnumerable`/`ICollection`/`IList` 映射为带 `<?>` 通配符的类型（如 `CSharpGenericIterable<?>`），`<?>` 在参数/变量声明中是合法的（且更灵活），但在 `implements`/`extends` 子句中 Java 禁止使用通配符。修复方案：在 ClassTransformer 和 InterfaceTransformer 中，当类型名被添加到 ImplementedTypes/ExtendedTypes/ExtendedType 时，将 `<?>` 替换为 `<Object>`。

**状态**: ✅ Fixed

## Iteration 2 — CSharpGenericEnumerator vs CSharpEnumerator Type Incompatibility

- **Java 文件**: XmlNamespaceManager.java:157, XmlNamedNodeMap.java:296, XmlDocument.java:1013+
- **行号**: 各 GetEnumerator 调用处
- **错误信息**: 不兼容的类型: CSharpGenericEnumerator<T>与CSharpEnumerator一致
- **代码片段**:
  ```java
  public CSharpEnumerator iterator() {
      ...
      return CSharpGenericEnumerator.from(prefixes.keySet().iterator());
  }
  ```

- **对应 C# 文件**: d:\csharpxml\System\Xml\XmlNamespacemanager.cs (line 203)
- **根因分类**: Transformer
- **涉及组件**: src/CSharpToJava.Core/Transformers/Expression/Transformers/InvocationExpressionTransformer.cs (lines 852-863, 7287-7301)
- **分析**: `IsGenericEnumeratorMethod` 检查 GetEnumerator 返回类型是否实现 `IEnumerator<T>`，如果实现则生成 `CSharpGenericEnumerator.from()`。但当包含方法的返回类型是 `IEnumerator`（非泛型）时，生成的 Java 返回类型是 `CSharpEnumerator`，而 `CSharpGenericEnumerator<T>` 不是 `CSharpEnumerator` 的子类型，导致类型不兼容。添加了 `IsContainingMethodNonGenericEnumerator` 检查来决定使用哪个工厂方法。

**状态**: ✅ Fixed (commit 88a6dde4) — errors reduced from 167 to 163

## Iteration 3 — System.Uri type mapping to io.github.ningpp.compat.Uri (missing methods)

- **Java 文件**: XmlResolver.java:23-37, XmlConvert.java:918-933, XmlDownloadManager.java:25-57
- **行号**: 多处 Uri 构造器和方法调用
- **错误信息**: 找不到符号 / 无法将类 Uri 中的构造器应用到给定类型
- **代码片段**:
  ```java
  if (baseUri == null || (!baseUri.getIsAbsoluteUri() && baseUri.getOriginalString().length() == 0)) {
      Uri uri = new Uri(relativeUri, UriKind.RelativeOrAbsolute);
  ```

- **对应 C# 文件**: d:\csharpxml\System\Xml\XmlResolver.cs
- **根因分类**: 类型映射缺失
- **涉及组件**: config/TypeMappings.json (System.Uri → io.github.ningpp.compat.Uri)
- **分析**: TypeMappings.json 将 `System.Uri` 映射到 `io.github.ningpp.compat.Uri`，但 compat 版本只有 `Uri(String)` 构造器和 `isWellFormedUriString` 静态方法，缺少 `getIsAbsoluteUri()`、`getOriginalString()`、`Uri(String, UriKind)` 构造器、`Uri(Uri, String)` 构造器。将映射改为 `dotnet.system.Uri`（来自 system-private-uri 项目），该类有完整实现。

**状态**: ✅ Fixed (commit 2f244436) — errors reduced from 163 to 73
