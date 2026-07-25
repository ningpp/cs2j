## Iteration 1 — ClassCastException: Integer[] cannot be cast to int[]
- **Java 文件**: `system-private-xml/src/main/java/dotnet/xml/schema/XmlUntypedStringConverter.java`
- **行号**: 263
- **错误信息**: `Caused by: java.lang.ClassCastException: class [Ljava.lang.Integer; cannot be cast to class [I`
- **代码片段**:
  ```java
  if (itemTypeDst == s_int32Type) {
      return (int[]) toArray(dotnet.xml.XmlConvert.splitString(value, IntegerHelper.RemoveEmptyEntries), nsResolver, Integer.class);
  }
  ```
- **对应 C# 文件**: `d:\csharpxml\System\Xml\Schema\XmlUntypedStringConverter.cs`（第 216 行 `return ToArray<int>(...)`）
- **根因分类**: Transformer / RuntimeClassParameterHelper
- **涉及组件**: `src/CSharpToJava.Core/Transformers/Utilities/RuntimeClassParameterHelper.cs`、`src/CSharpToJava.Core/Transformers/Expression/Utilities/ExpressionTransformerHelpers.cs`
- **分析**: C# 泛型方法 `ToArray<T>()` 被显式实例化为 `ToArray<int>()`，转换器为类型参数生成运行期 `Class<?>` 实参时，通过 `ToRuntimeTypeForClassLiteral` 把 `int` 映射为 `Integer.class`。由于 `toArray` 内部用 `TypeHelper.newArrayInstance(clazz, length)` 创建数组，`Integer.class` 生成 `Integer[]`，而调用点却被推断为返回 `int[]` 并插入了 `(int[])` 强制转换，导致运行时 `ClassCastException`。
- **修复**: 在 `RuntimeClassParameterHelper.ToClassArgument` 增加 `preferPrimitiveClassLiteral` 参数：为方法级类型参数（调用点）传 `true`，直接返回 `int.class` 等原始类型 class literal；类型级参数仍传 `false`，保持 `Integer.class` 以保证泛型类型擦除后的兼容性。
- **验证**: 新增回归测试 `GenericArrayMethodInstantiatedWithPrimitive_CallSiteUsesPrimitiveClassLiteral`（28 个 GenericArrayTypeParameterTests 全部通过），重新生成 `XmlUntypedStringConverter.java` 后调用点变为 `int.class`。
- **状态**: ✅ Fixed

