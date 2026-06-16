# URI Error Analysis

## Iteration 1 — CSharpArray Unsupported
- **Java 文件**: dotnet/system/Uri.java (line 3851)
- **行号**: 3851
- **错误信息**: java.lang.IllegalArgumentException: Unsupported array type: class io.github.ningpp.compat.CSharpArray
- **代码片段**:
  ```java
  Buffer.blockCopy(CSharpArray.of(dest), lastSlash << 1, CSharpArray.of(dest), (i + 1) << 1, (destLength.value - lastSlash) << 1);
  ```
- **对应 C# 文件**: d:\csharpuri\src\System\Uri.cs (line 5043)
- **根因分类**: Transformer
- **涉及组件**: src/CSharpToJava.Core/Transformers/Expression/Transformers/ArgumentTransformer.cs, src/CSharpToJava.Core/Transformers/Expression/Utilities/ExpressionTransformerHelpers.cs
- **分析**: Buffer.BlockCopy 的 C# 签名接受 Array 类型参数，转换器在参数类型转换时将 char[] 包装为 CSharpArray.of()，但 Java 的 Buffer.blockCopy 只接受原始 Java 数组类型
- ✅ Fixed: 在 ArgumentTransformer.CoerceArgumentType 中添加了 System.Buffer 方法的特殊处理，跳过 CSharpArray.of() 包装
