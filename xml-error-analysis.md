# XML Error Analysis

## Iteration 1 — DateTimeKind→int 类型不匹配

- **Java 文件**: ReadContentAs.Tests/src/test/java/dotnet/xml/Tests/DateTimeAttributeTests.java
- **行号**: 41
- **错误信息**: 不兼容的类型: io.github.ningpp.compat.DateTimeKind无法转换为int
- **代码片段**:
  ```java
  Assert.equal(new CSharpDateTime(CSharpDateTime.getNow().getYear(), CSharpDateTime.getNow().getMonth(), CSharpDateTime.getNow().getDay(), 0, 0, 0, DateTimeKind.Utc).toLocalTime(), (CSharpDateTime)(reader.readContentAs(CSharpDateTime.class, null)));
  ```
- **对应 C# 文件**: Tests/ReadContentAs/ReadAsDateTimeAttributeTests.cs
- **根因分类**: Transformer
- **涉及组件**: src/CSharpToJava.Core/Transformers/Expression/Transformers/ObjectCreationTransformer.cs
- **分析**: C# 的 `new DateTime(year, month, day, hour, minute, second, DateTimeKind.Utc)` 7参数构造函数在Java侧没有对应版本。CSharpDateTime只有6参数(无DateTimeKind)和8参数(含millisecond+DateTimeKind)构造函数。转换器直接透传参数，未做构造函数重载适配。
- **状态**: ✅ Fixed — ObjectCreationTransformer 添加了7参数→8参数转换逻辑（插入millisecond=0）

## Iteration 1 — Decimal 类缺失

- **Java 文件**: ReadContentAs.Tests/src/test/java/dotnet/xml/Tests/DecimalAttributeTests.java
- **行号**: 18
- **错误信息**: 找不到符号 - 类 Decimal, 位置: 程序包 dotnet.system
- **代码片段**:
  ```java
  Assert.equal((Decimal)reader.readContentAs(typeof(Decimal), null), (Decimal)reader.readContentAsObj());
  ```
- **对应 C# 文件**: Tests/ReadContentAs/ReadAsDecimalAttributeTests.cs
- **根因分类**: 语义丢失
- **涉及组件**: src/CSharpToJava.Core/Context/ConversionContext.cs (QualifyMappedTypeIfCurrentTypeNameCollides)
- **分析**: TypeMappings.json 已配置 System.Decimal→Decimal(io.github.ningpp.compat.Decimal)，但 QualifyMappedTypeIfCurrentTypeNameCollides 方法缺少 HasConfiguredTypeMapping 守卫，导致类型名碰撞时用命名空间兜底映射(dotnet.system)替代了显式映射。实际根因在 TypeMappingService.QualifyTypeReferenceIfNeeded 缺少 HasConfiguredTypeMapping 守卫。
- **状态**: ✅ Fixed — TypeMappingService 和 ConversionContext 均添加了 HasConfiguredTypeMapping 守卫

## Iteration 1 — CSharpDateTimeOffset.addTicks 缺失

- **Java 文件**: ReadContentAs.Tests/src/test/java/dotnet/xml/Tests/ExtendedDateTimeElementContentTests.java
- **行号**: 32
- **错误信息**: 找不到符号 - 方法 addTicks(long), 位置: 类 io.github.ningpp.compat.CSharpDateTimeOffset
- **代码片段**:
  ```java
  Assert.equal(new CSharpDateTimeOffset(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc).addTicks(ticks), ...);
  ```
- **对应 C# 文件**: Tests/ReadContentAs/ReadAsExtendedDateTimeElementContentTests.cs
- **根因分类**: 类型映射缺失
- **涉及组件**: java/csharptojava-compat/src/main/java/io/github/ningpp/compat/CSharpDateTimeOffset.java
- **分析**: CSharpDateTimeOffset 缺少 addTicks(long) 方法，CSharpDateTime 已有此方法。需要在 compat 库中添加。
- **状态**: ✅ Fixed — CSharpDateTimeOffset 添加了 addTicks(long) 方法和 DateTimeKind 构造函数
