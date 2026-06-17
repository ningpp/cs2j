# URI Error Analysis

## Iteration 1 — NullPointerException (getSchemeName returns null)

- **Java 文件**: System.Private.Uri.Functional.Tests/src/test/java/dotnet/system/PrivateUri/Tests/UriParserTest.java
- **行号**: 228 (onRegister), 241 (onRegister2), 327 (register), 358 (register_Minus1Port), 364 (register_UInt16PortMinus1), 380 (reRegister)
- **错误信息**: NullPointerException: Cannot invoke "String.length()" because the return value of "dotnet.system.UriParser.getSchemeName()" is null
- **代码片段**:
  ```java
  // UriParser.java:296
  if (syntax.getSchemeName().length() != 0) {
      throw new IllegalStateException(...);
  }
  ```
  ```java
  // TestUriParser.java:38-40
  public String getSchemeName() {
      return scheme_name;  // scheme_name is null initially
  }
  ```

- **对应 C# 文件**: tests/FunctionalTests/UriParserTest.cs (TestUriParser class)
- **根因分类**: Transformer
- **涉及组件**:
  - src/CSharpToJava.Core/Transformers/Member/MethodTransformer.cs (line 1156)
  - src/CSharpToJava.Core/Transformers/Member/PropertyTransformer.cs (line 293)
- **分析**: C# 中 `TestUriParser.SchemeName` 使用 `new` 关键字隐藏基类属性（非虚分派），而转换器将 `new` 映射为 Java `@Override`（虚分派）。导致 `fetchSyntax()` 中 `syntax.getSchemeName()` 被多态分派到 `TestUriParser.getSchemeName()` 返回 `null`，而 C# 中应调用基类 `UriParser.SchemeName` 返回 `""`。

**状态**: ✅ Fixed — 在 PropertyTransformer 中添加 `IsHidingBaseMember` 检测，跳过隐藏属性的 getter/setter 生成。NPE 从 6 errors 降到 0。

## Iteration 1 — getComponents_BadUriFormat (no exception thrown)

- **Java 文件**: System.Private.Uri.Functional.Tests/src/test/java/dotnet/system/PrivateUri/Tests/UriParserTest.java
- **行号**: 133
- **错误信息**: Assert.throws() failure: No exception thrown. Expected: java.lang.IllegalArgumentException
- **代码片段**:
  ```java
  TestUriParser parser = new TestUriParser();
  Assert.throws_(IllegalArgumentException.class, () -> {
      parser.getComponents(http, UriComponents.Host, UriFormat.fromValue((int)(Integer.MIN_VALUE)));
  });
  ```
- **对应 C# 文件**: tests/FunctionalTests/UriParserTest.cs:206-210
- **根因分类**: Transformer
- **涉及组件**: 枚举转换逻辑
- **分析**: C# 枚举强转 `(UriFormat)Int32.MinValue` 产生非法枚举值，`getComponents` 中 `format & ~SafeUnescaped != 0` 检查会抛异常。Java 中 `UriFormat.fromValue(Integer.MIN_VALUE)` 返回 `_UNMAPPED`(值为0)，导致 `0 & ~3 == 0` 检查通过，不抛异常。

## Iteration 1 — IriTest factory method not found

- **Java 文件**: System.Private.Uri.Functional.Tests/src/test/java/dotnet/system/PrivateUri/Tests/IriTest.java
- **行号**: 386-448
- **错误信息**: PreconditionViolationException: Could not find factory method [iri_ExpandingContents_AllowedSize] / [iri_ExpandingContents_TooLong]
- **代码片段**:
  ```java
  @ParameterizedTest
  @MethodSource("iri_ExpandingContents_AllowedSize")
  public void iri_ExpandingContents_DoesNotThrowIfSizeAllowed(String uri) { ... }
  ```
- **对应 C# 文件**: tests/FunctionalTests/IriTest.cs:619-688
- **根因分类**: Transformer
- **涉及组件**: MemberData/MethodSource 转换逻辑
- **分析**: C# 的 `[MemberData("iri_ExpandingContents_AllowedSize")]` 转换为 Java 的 `@MethodSource("iri_ExpandingContents_AllowedSize")`，但工厂方法名或签名不匹配，导致 JUnit 找不到对应的工厂方法。

## Iteration 1 — UriTests.testCtor_BackwardSlashInPath (factory method not found)

- **Java 文件**: System.Private.Uri.Functional.Tests/src/test/java/dotnet/system/PrivateUri/Tests/UriTests.java
- **行号**: 23-37
- **错误信息**: PreconditionViolationException: Could not find factory method [uri_TestData]
- **代码片段**:
  ```java
  @ParameterizedTest
  @MethodSource("uri_TestData")
  public void testCtor_BackwardSlashInPath(String uriString, String ... ) { ... }
  ```
- **对应 C# 文件**: tests/FunctionalTests/UriTests.cs:14-33
- **根因分类**: Transformer
- **涉及组件**: MemberData/MethodSource 转换逻辑
- **分析**: 同 IriTest 问题，`[MemberData("uri_TestData")]` 转换后工厂方法名或签名不匹配。

## Iteration 1 — UriTests.testCompare (assertion failure)

- **Java 文件**: System.Private.Uri.Functional.Tests/src/test/java/dotnet/system/PrivateUri/Tests/UriTests.java
- **行号**: 647
- **错误信息**: Assert.equal() failure: Values differ. Expected: -65, Actual: -1
- **代码片段**:
  ```java
  Assert.equal(-65, dotnet.system.Uri.compare(uri1, uri2));
  ```
- **对应 C# 文件**: tests/FunctionalTests/UriTests.cs:803-844
- **根因分类**: Transformer
- **涉及组件**: 字符串比较逻辑 / Uri.compare 实现
- **分析**: `Uri.compare()` 的字符串比较行为与 C# 不一致，可能因 `StringHelper.compare()` 的 `StringComparison.CurrentCulture` 实现差异导致。
