﻿# SDK Error Analysis

## Iteration 1 — 泛型方法调用参数不匹配 (cannot find suitable method / 找不到合适的方法)

### 错误概述
- **错误总数**: 2344 个编译错误
- **主要错误类型**:
  - `找不到合适的方法` (448 个) — `deserializeWrapper`/`serializeWrapper` 方法参数不匹配
  - `无法推断类型变量 T` (338 个) — 泛型类型推断失败
  - `实际参数列表和形式参数列表长度不同` (293 个) — 参数数量不匹配
  - `找不到符号` (8 个) — 未定义的变量 `_cs2j_T`

### 第一个错误
- **Java 文件**: `system-text-json-tests/src/test/java/dotnet/text/json/Serialization/Tests/ConstructorTests.java`
- **行号**: 92
- **错误信息**: 找不到符号 (`_cs2j_T`)
- **代码片段**:
  ```java
  UnsupportedOperationException ex = ((UnsupportedOperationException) Assert.throwsAsync(UnsupportedOperationException.class, () -> getSerializer().deserializeWrapper(_cs2j_T, "{}")).join());
  ```

### 根因分析
- **对应 C# 文件**: `d:\csharp-sdk\json\tests\Serialization\DeserializationWrapper.cs`
- **根因分类**: Transformer 逻辑缺陷
- **涉及组件**:
  - `src/CSharpToJava.Core/Transformers/Member/MethodTransformer.cs` — `AddPendingRuntimeClassParameters` 方法
  - `src/CSharpToJava.Core/Transformers/Expression/Transformers/InvocationExpressionTransformer.cs` — `GetClassTokensFromExplicitTypeArguments` 方法
- **分析**:
  1. C# 泛型方法 `DeserializeWrapper<T>(string json, JsonSerializerOptions options = null)` 被 abstract 声明，override 实现中调用 `JsonSerializer.Deserialize<T>(json, options)`
  2. 调用端 `Serializer.DeserializeWrapper<T>(json)` 通过 `GetClassTokensFromExplicitTypeArguments` 正确地生成了 `T.class` token 并 prepend 到参数列表
  3. 但 `GetClassTokensFromExplicitTypeArguments` 没有调用 `context.RequireClassTypeParam()` 注册 pending requirement
  4. 方法声明端的 `AddPendingRuntimeClassParameters` 通过 `DrainClassTypeParamsForMethod()` 检查 pending requirements，但为空
  5. `GetOverridePendingTypeParams` 和 `EnsurePendingParamsInBaseAndImpl` 方法已定义但**从未被调用**
  6. 结果：调用端传了 `Class<T>` 参数，但方法签名没有对应的 `_cs2j_T` 参数
