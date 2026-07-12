# XML Error Analysis

## Iteration 1 — ClassCastException (primitive array component type mismatch)
- **Java 文件**: system-private-xml/src/main/java/dotnet/xml/schema/XmlUntypedStringConverter.java
- **行号**: 208 (getComponentType), 289 (Array.newInstance)
- **错误信息**: Content cannot be converted to the type class [I. Line 1, position 27. / ClassCastException: Xml type 'List of xdt:untypedAtomic' does not support a conversion from Clr type 'java.lang.String' to Clr type '[I'.
- **代码片段**:
  ```java
  Class itemTypeDst = destinationType.getComponentType(); // line 208
  Object arrDst = java.lang.reflect.Array.newInstance(clazz, stringArray.length); // line 289
  ```
- **对应 C# 文件**: System/Xml/Schema/XmlUntypedStringConverter.cs
- **根因分类**: 类型映射缺失 + Transformer 逻辑缺陷
- **涉及组件**:
  - config/TypeMappings.json (GetElementType → getComponentType mapping)
  - CSharpToJava.Core/Transformers/Expression/Transformers/ObjectCreationTransformer.cs (new T[] → Array.newInstance)
  - CSharpToJava.Core/Transformers/Expression/Transformers/InvocationExpressionTransformer.cs (LINQ ToArray generator)
  - java/csharptojava-compat/src/main/java/io/github/ningpp/compat/TypeHelper.java (missing getElementType/newArrayInstance)
- **分析**: C# `typeof(int[]).GetElementType()` returns `typeof(int)`, but Java `int[].class.getComponentType()` returns `int.class` (primitive), while the converted code compares against `Integer.class` (wrapper). This causes the `if (itemTypeDst == s_int32Type)` comparison to fail, falling through to `throw createInvalidClrMappingException`. Additionally, `Array.newInstance(Integer.class, length)` creates `Integer[]` which cannot be cast to `int[]`.

### Fix
1. Added `TypeHelper.getElementType()` that boxes primitive component types (`int.class → Integer.class`)
2. Added `TypeHelper.newArrayInstance()` that creates primitive arrays for wrapper class types
3. Updated TypeMappings.json: `GetElementType → TypeHelper.getElementType`
4. Updated ObjectCreationTransformer + InvocationExpressionTransformer: `Array.newInstance → TypeHelper.newArrayInstance`

✅ Fixed
