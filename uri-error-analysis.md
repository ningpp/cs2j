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
