# XML 转换器测试失败分析

## Iteration 1 — 多个测试失败修复

### 修复 1: IsPositiveInfinity/IsNegativeInfinity 映射错误 [198]

- **Java 文件**: system-private-xml/src/main/java/dotnet/xml/XmlConvert.java
- **行号**: 495-499, 507-511
- **错误信息**: `toString(float/double)` 中 `IsNegativeInfinity` 和 `IsPositiveInfinity` 都映射为 `isInfinite`，导致无法区分正负无穷
- **对应 C# 文件**: System/Xml/XmlConvert.cs
- **根因分类**: Transformer 逻辑缺陷
- **涉及组件**: 
  - src/CSharpToJava.Core/Transformers/Expression/Transformers/InvocationExpressionTransformer.cs
  - src/CSharpToJava.Core/Transformers/Expression/Transformers/IdentifierExpressionTransformer.cs
- **分析**: 转换器的 `MapPrimitiveStaticMethodName` fallback 将 `IsPositiveInfinity` 和 `IsNegativeInfinity` 都映射为 Java 的 `isInfinite`，在语义模型不可用时走了 fallback 路径

### 修复 2: SByte ToString & 0xFF 掩码错误 [200]

- **Java 文件**: system-private-xml/src/main/java/dotnet/xml/XmlConvert.java
- **行号**: 482-484
- **错误信息**: `toString(byte)` 对 sbyte 值做了 `& 0xFF` 掩码，导致 -58 变成 198
- **对应 C# 文件**: System/Xml/XmlConvert.cs
- **根因分类**: Transformer 逻辑缺陷
- **涉及组件**: src/CSharpToJava.Core/Transformers/Expression/Transformers/InvocationExpressionTransformer.cs
- **分析**: `BuildPrimitiveToString` 中 `System_SByte` 也做了 `& 0xFF` 掩码，但 sbyte 是有符号类型不应掩码

### 修复 3: UInt64/UInt32 ToString 无符号格式化 [203]

- **Java 文件**: system-private-xml/src/main/java/dotnet/xml/XmlConvert.java
- **行号**: 491-493
- **错误信息**: `toString(long)` 对 ulong.MaxValue (0xFFFFFFFFFFFFFFFFL = -1 in Java) 返回 "-1" 而非 "18446744073709551615"
- **对应 C# 文件**: System/Xml/XmlConvert.cs
- **根因分类**: Transformer 逻辑缺陷
- **涉及组件**: src/CSharpToJava.Core/Transformers/Expression/Transformers/InvocationExpressionTransformer.cs
- **分析**: `BuildPrimitiveToString` 在有格式参数路径中未对 UInt32/UInt64 做无符号字符串转换

### 修复 4: UUID.fromString 修剪和异常包装 [194][195]

- **Java 文件**: system-private-xml/src/main/java/dotnet/xml/XmlConvert.java
- **行号**: 897-899
- **错误信息**: UUID.fromString 对带空格的输入抛 "UUID string too large"，对无效输入抛 IllegalArgumentException 而非 FormatException
- **对应 C# 文件**: System/Xml/XmlConvert.cs
- **根因分类**: 语义丢失
- **涉及组件**: java/csharptojava-compat/src/main/java/io/github/ningpp/compat/UUID.java
- **分析**: C# Guid 构造函数会修剪空格并抛 FormatException，但 Java UUID.fromString 不会

### 修复 5: Float.ToString("R") 往返格式化 [197]

- **Java 文件**: system-private-xml/src/main/java/dotnet/xml/XmlConvert.java
- **行号**: 494-504
- **错误信息**: `toString(float)` 对 -4582.24f 返回 "-4582.24023" 而非 "-4582.24"
- **对应 C# 文件**: System/Xml/XmlConvert.cs
- **根因分类**: 语义丢失
- **涉及组件**: java/csharptojava-compat/src/main/java/io/github/ningpp/compat/MathHelper.java
- **分析**: formatRoundTrip 对 Float 使用 Double.parseDouble 检查往返，应使用 Float.parseFloat

### 修复 6: ArgumentNullException 映射为 NullPointerException [335-340][355][367][389]

- **Java 文件**: xmlconvert-tests/src/test/java/dotnet/xml/Tests/VerifyNameTests4.java
- **行号**: 467
- **错误信息**: catch (ArgumentNullException) 被转换为 catch (NullPointerException)，导致 ArgumentNullException 未被捕获
- **对应 C# 文件**: tests/XmlConvert/VerifyNameTests4.cs
- **根因分类**: Transformer 逻辑缺陷
- **涉及组件**: src/CSharpToJava.Core/Java/Rewriters/ExceptionApiRewriter.cs
- **分析**: ExceptionApiRewriter 将 ArgumentNullException 映射为 NullPointerException，但 compat 库已有 ArgumentNullException 类
