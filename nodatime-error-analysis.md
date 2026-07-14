# NodaTime Java Conversion Error Analysis

## Iteration 1 — NotAStatement (setter expression body)
- **Java 文件**: NodaTime/DateTimeZoneProviders.java
- **行号**: 92
- **错误信息**: 不是语句
- **代码片段**:
  ```java
  public static void setSerialization(IDateTimeZoneProvider value) { _chainVal1; }
  ```
- **对应 C# 文件**: D:\nodatime-3.3.3\src\NodaTime\DateTimeZoneProviders.cs
- **根因分类**: Transformer
- **涉及组件**: src/CSharpToJava.Core/Transformers/Member/PropertyTransformer.cs
- **分析**: C# 属性 setter 使用箭头表达式体 `set => Xml.XmlSerializationSettings.DateTimeZoneProvider = value;`。AssignmentTransformer 将其识别为属性赋值并调用 HoistChainedPropertyAssignment，生成 pre-statements (`var _chainVal1 = value; XmlSerializationSettings.setDateTimeZoneProvider(_chainVal1);`) 和返回值 `_chainVal1`。但 PropertyTransformer 在处理 setter 表达式体时，仅取了返回值作为 Body，未处理 pre-statements，导致生成无效的 `_chainVal1;` 语句。
- **状态**: ✅ Fixed

## Iteration 2 — IntegerLiteralOverflow (leading zeros + missing L suffix)
- **Java 文件**: NodaTime/Demo/InstantDemo.java, NodaTime/NodaConstants.java, NodaTime/Test/HighPerformance/Instant64Test.java 等
- **行号**: 多处
- **错误信息**: 整数太大 / 八进制文字中的数字非法
- **代码片段**:
  ```java
  // InstantDemo.java:92 — 整数太大
  Assert.areEqual(12760929000000000, instant.toUnixTimeTicks());
  // Instant64Test.java:237 — 八进制数字非法
  Instant64 x = Instant64.fromUtc(2011, 08, 18, 20, 53);
  ```
- **对应 C# 文件**: D:\nodatime-3.3.3\src\NodaTime.Demo\InstantDemo.cs 等
- **根因分类**: Transformer
- **涉及组件**: src/CSharpToJava.Core/Transformers/Expression/Transformers/LiteralExpressionTransformer.cs
- **分析**: TransformNumericLiteral 未处理：(1) C# 允许前导零（如 08=8），Java 将其解释为八进制（08=非法）；(2) C# 整数字面量超出 int 范围自动升级为 long，Java 需要 L 后缀。
