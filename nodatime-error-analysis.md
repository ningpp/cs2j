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
