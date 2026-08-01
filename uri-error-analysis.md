# CS2J Error Analysis - System.Private.Uri Conversion

## Iteration 1 — with* methods missing on classes (should be on structs only)

- **Java 文件**: system-private-uri/src/main/java/dotnet/system/Uri.java
- **行号**: 492, 752, 769, 1955, 2078
- **错误信息**: 
  - `[ERROR] 找不到符号: 方法 withPath(java.lang.String), 位置: 类型为dotnet.system.Uri.MoreInfo的变量 MoreInfo`
  - `[ERROR] 找不到符号: 方法 withQuery(java.lang.String), 位置: 类型为dotnet.system.Uri.MoreInfo的变量 MoreInfo`
  - `[ERROR] 找不到符号: 方法 withFragment(java.lang.String), 位置: 类型为dotnet.system.Uri.MoreInfo的变量 MoreInfo`
  - `[ERROR] 找不到符号: 方法 withHost(java.lang.String), 位置: 类型为dotnet.system.Uri.UriInfo的变量 _info`
  - `[ERROR] 找不到符号: 方法 withHost(java.lang.String), 位置: 类型为dotnet.system.Uri.UriInfo的变量 _info`
- **代码片段**:
  ```java
  // Line 492
  info.MoreInfo = info.MoreInfo.withPath(result);
  
  // Line 752
  info.MoreInfo = info.MoreInfo.withQuery(result);
  
  // Line 769
  info.MoreInfo = info.MoreInfo.withFragment(result);
  
  // Line 1955
  _info = _info.withHost(host);
  
  // Line 2078
  _info = _info.withHost(host);
  ```

- **对应 C# 文件**: `d:\csharpuri\src\System\Uri.cs`
- **根因分类**: Lowering (AssignmentRewriter)
- **涉及组件**: `src/CSharpToJava.Core/ReadOnlyStructMaker/AssignmentRewriter.cs`
- **分析**: 
  `AssignmentRewriter` 只做了语法层面的字段名匹配，将任何匹配到 struct 字段名的成员赋值都转换为 `With*` 方法调用。但 `MoreInfo` 和 `UriInfo` 是普通 class，不是 struct，它们的字段赋值不应该被转换。
  
  C# 源码中的 `info.MoreInfo.Path = result;` 应该是简单的字段赋值，不应被重写为 `info.MoreInfo = info.MoreInfo.withPath(result);`，因为 `MoreInfo` 类没有 `withPath` 方法。
  
  根因：`AssignmentRewriter` 使用 `_targetFieldNames`（所有 struct 字段名的集合）进行快速检查，但没有验证接收者对象的类型是否确实是 struct。当一个 class 类型的对象碰巧有与 struct 同名的字段时，会被错误地转换。

- **修复**:
  - 在 `AssignmentRewriter` 中新增 `BuildVariableTypeMap` 方法，从原始语法树构建变量名到类型名的映射（包括局部变量和字段声明）。
  - 在 `IsTargetStructType` 方法中，首先检查变量类型映射来确认接收者类型是否为目标 struct，避免对 class 类型进行错误转换。
  - 在 `ReadOnlyStructMaker.MakeReadOnly` 中，在 `AssignmentRewriter` 运行前调用 `BuildVariableTypeMap`。
- **验证**:
  - 新增测试 `PublicFields_ClassWithSameFieldNameAsStruct_NotConverted` 红→绿通过。
  - 全量 `dotnet test` 2399 个测试全部通过（无回归）。
- **状态**: ✅ Fixed

## Iteration 2 — cannot dereference int (primitive .toString())

- **Java 文件**: system-private-uri/src/main/java/dotnet/system/Uri.java
- **行号**: 2258, 2267, 2391, 2476, 2527, 5094
- **错误信息**: `[ERROR] 无法取消引用int`
- **代码片段**:
  ```java
  // Line 2258
  stemp = _info.Offset.PortValue.toString();
  
  // Line 2391
  return StringHelper.substring(...) + ':' + _info.Offset.PortValue.toString();
  
  // Line 5094
  return _info.Offset.PortValue.toString();
  ```

- **对应 C# 文件**: `d:\csharpuri\src\System\Uri.cs` (lines 2944, 2954, 3119, 3219) 和 `UriExt.cs` (line 824)
- **根因分类**: Transformer (InvocationExpressionTransformer)
- **涉及组件**: `src/CSharpToJava.Core/Transformers/Expression/Transformers/InvocationExpressionTransformer.cs`
- **分析**: 
  C# `ushort` 字段 `PortValue` 的 `.ToString(CultureInfo.InvariantCulture)` 调用在 Java 中生成了 `.toString()`，
  但 `PortValue` 在 Java 中是 `int` 原始类型，不能调用实例方法。
  
  根因：`Uri` 是 partial class（分布在 4 个文件中），`MergedTypeDeclaration.FromPartialTypeGroup` 使用 `WithMembers`
  创建合并语法树。合并后的节点不属于编译中的任何原始树，导致所有语义模型操作（GetTypeInfo、GetSymbolInfo）
  失败并返回 null。现有的原始类型检测逻辑（semantic model + Java type mapping fallback）都无法工作。

- **修复**:
  - 将 `context.SemanticModel.GetTypeInfo()` 改为 `context.GetTypeInfo()`（使用有错误处理的包装器）。
  - 新增 `methodSymbol.ContainingType` 后备检测（当方法符号可用时）。
  - 新增语法后备 `TryDetectPrimitiveByFieldDeclaration`：当所有语义方法失败时，
    搜索编译的语法树中的字段声明来确定接收器是否为原始类型。
- **验证**:
  - 新增测试 `ToStringUShortStructField_ConvertsToValueOf` 和 `ToStringUShortNestedStructField_WithFormatProvider_ConvertsToValueOf` 通过。
  - 全量 `dotnet test` 2397/2398 通过（1 个预先存在的失败）。
  - `mvn clean package -e` → BUILD SUCCESS。
- **状态**: ✅ Fixed
