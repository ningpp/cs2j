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

---

## Iteration 2 — override 方法被注入类级类型参数的 Class token，方法级 `<T>` 遮蔽类级 T (`不兼容的类型: T无法转换为T`)

### 错误概述
- **错误总数**: 256 条 `[ERROR]` 行（javac 默认上限 100 个文件级错误，实际更多）
- **按消息分类（Top）**:
  - `不兼容的类型: T无法转换为T` (30)
  - `无法将类 JsonConverter1<T> 中的方法 write 应用到给定类型` (18)
  - `不兼容的类型: 无法推断类型变量 T` (18)
  - `不兼容的类型: TCollection无法转换为TCollection` (16)
  - `writeWithQuotes / read 应用到给定类型` (各 16)
  - `不兼容的类型: ObjectHolder<T>无法转换为ObjectHolder<T>` (8)
  - `无法将类 JsonConverter 中的方法 canConvert 应用到给定类型` (8)
- **按文件分类（Top）**: `JsonPropertyInfo1.java` (36)、`DictionaryDefaultConverter.java` (20)、
  `JsonConverter1.java` (20)、`ObjectDefaultConverter.java` (20)、`IEnumerableDefaultConverter.java` (18)

### 第一个错误
- **Java 文件**: `system-text-json/src/main/java/dotnet/text/json/JsonPropertyInfo1.java`
- **行号**: 126
- **错误信息**: `不兼容的类型: T无法转换为T`
- **代码片段**:
  ```java
  public <T> boolean getMemberAndWriteJson(Class<T> _cs2j_T, Object obj, io.github.ningpp.compat.ObjectHolder<WriteStack> state, Utf8JsonWriter writer) {
      T value = getGet().apply(obj);          // getGet() 返回 Function<Object, T>（类级 T）
      if (getIgnoreDefaultValuesOnWrite() && ((_cs2jDefault_T() == null ? value == null : CSharpDefaultEqualityComparerGeneric.getDefault().equals(_cs2jDefault_T(), value)))) {
      ...
  ```
  类声明：`public final class JsonPropertyInfo1<T> extends JsonPropertyInfo { private Class<?> tClass; ... }`

### 根因分析
- **对应 C# 文件**: `d:\csharp-sdk\json\src\System\Text\Json\Serialization\JsonPropertyInfoOfT.cs`
  ```csharp
  public override bool GetMemberAndWriteJson(object obj, ref WriteStack state, Utf8JsonWriter writer)
  {
      T value = Get!(obj);                 // T = 类级类型参数
      if (IgnoreDefaultValuesOnWrite && (
          default(T) == null ? value == null : EqualityComparer<T>.Default.Equals(default, value)))
      ...
  ```
  基类 `GetMemberAndWriteJson` 声明于 `JsonPropertyInfo.cs`（另一个文件，非泛型类）。
- **根因分类**: Transformer 逻辑缺陷
- **涉及组件**:
  - `src/CSharpToJava.Core/Transformers/Member/MethodTransformer.cs`
    — `GetOverridePendingTypeParams`（来源三：共享 `RuntimeClassParameters` 注册表）
    — `EnsurePendingParamsInBaseAndImpl` / `InjectLeadingClassTokens`
  - `src/CSharpToJava.Core/Transformers/Utilities/RuntimeClassParameterHelper.cs`
    — `AddTypeOfDefaultRequirements`（扫描 `typeof(T)` / `default(T)` / 裸 `default`）
    — `GetRequiredTypeParameters(IMethodSymbol)` 末尾的 `RegisterMethodRequirements`
  - `src/CSharpToJava.Core/Transformers/Type/ClassTransformer.cs` — `AddRuntimeClassFields` / `AddDefaultFactoryMethods`
- **分析**:
  1. 类级类型参数 `T` 的运行时信息由 `AddRuntimeClassFields` 在声明类上生成的 `private Class<?> tClass` 字段提供，
     `AddDefaultFactoryMethods` 也据此生成 `protected T _cs2jDefault_T() { return (T) DefaultValue.ofClass(tClass); }`。
     生成的 `JsonPropertyInfo1` 里二者都已存在 —— 说明类级 `T` **已经**有可用的运行时 token。
  2. 但 `RuntimeClassParameterHelper.GetRequiredTypeParameters(IMethodSymbol)` 扫描方法体时，
     `AddTypeOfDefaultRequirements` 对 `default(T)` / 裸 `default` 命中的**类级**类型参数也调用 `AddUnique`，
     并在末尾 `RegisterMethodRequirements(originalMethod, result)` 把类级 `T` 写进了方法级注册表。
     （该注册表同时服务于构造器的后置 `Class<?> clazz` 参数，所以不能直接在这里剔除。）
  3. `GetOverridePendingTypeParams` 的「来源二」有 `req.DeclaringMethod != null` 过滤，能挡住类级参数；
     但「来源三」直接读取 `RuntimeClassParameters.TryGetMethodRequirements(...)`，**没有做类级过滤**，于是 `T` 进入 pending。
  4. `EnsurePendingParamsInBaseAndImpl` → `InjectLeadingClassTokens` 向 override 与 base 声明同时插入前导
     `Class<T> _cs2j_T` 参数，并且**无条件** `javaMethod.TypeParameters.Add(new JavaTypeParameter("T"))`
     （非 override 分支 `AddPendingRuntimeClassParameters` 里已有的 `isClassLevelTypeParam` 守卫在这里缺失）。
  5. 结果：方法级 `<T>` 遮蔽了类级 `T` —— 方法体里 `T value = getGet().apply(obj);` 用的是方法级 `T`，
     而 `getGet()` 返回 `Function<Object, T>`（类级 `T`），二者在 javac 看来是两个不同的类型变量，
     报 `不兼容的类型: T无法转换为T`。同时 override 签名不再匹配基类槽位，产生 `不是抽象的, 并且未覆盖...` 与
     `无法将类 ... 中的方法 ... 应用到给定类型` 的连带错误。

### 修复
- `GetOverridePendingTypeParams` 来源三新增过滤：跳过符号所在类型（含所有外层类型）声明的类型参数名。
- `InjectLeadingClassTokens` 补充 `classLevelNames` 守卫：类级类型参数不再被重新声明为方法级类型参数。
- 新增 `GetEnclosingTypeParameterNames(ISymbol)` 辅助方法。
- 顺带移除 `EnsurePendingParamsInBaseAndImpl` 里残留的 `[DEBUG]` `Console.WriteLine`（严重污染转换日志）。

### 测试（红 → 绿）
- `tests/CSharpToJava.Tests/ClassLevelRuntimeClassFieldTests.cs`
  - `TypeOfOnClassTypeParameter_UsesClassField_NotMethodTypeParam`（既有，改动前即失败）
  - `OverrideInGenericClass_DefaultTForClassTypeParam_DoesNotInjectClassToken`（新增，镜像 `JsonPropertyInfo<T>` 场景）
- 状态: ✅ Fixed（2 个测试均通过；全量测试无新增回归，
  既有的 `OverrideGenericMethodClassTokenTests` 与 `CSharpXmlCompileRegressionTests.MissingMethodException_...` 两条失败均与本次改动无关，改动前即失败）

---
