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

## Iteration 2 — uint Comparison Semantics
- **Java 文件**: dotnet/system/Uri.java (line 1085), dotnet/system/DomainNameHelper.java (line 424)
- **行号**: 1085, 424
- **错误信息**: isHexDigit(':') returns true (should be false); IPv6 validation fails; IdnCheckHostName returns Dns instead of IPv6
- **代码片段**:
  ```java
  public static boolean isHexDigit(char character) { return (int)(((character - '0')) & 0xFFFFFFFFL) <= '9' - '0' || (int)(((character - 'A')) & 0xFFFFFFFFL) <= 'F' - 'A' || (int)(((character - 'a')) & 0xFFFFFFFFL) <= 'f' - 'a'; }
  ```
- **对应 C# 文件**: d:\csharpuri\src\System\Uri.cs (line 1544-1547)
- **根因分类**: Transformer
- **涉及组件**: src/CSharpToJava.Core/Transformers/Expression/Transformers/BinaryExpressionTransformer.cs, src/CSharpToJava.Core/Transformers/Expression/Transformers/TypeOperationTransformer.cs
- **分析**: C# 的 `(uint)x <= y` 使用无符号比较语义。转换器将 `(uint)` cast 转换为 `(int)((x) & 0xFFFFFFFFL)`，但 `(int)` 截断将 long 正值变回了负的 int，导致有符号比较结果错误。例如 `(int)((-7) & 0xFFFFFFFFL)` = -7，而 `-7 <= 5` 为 true（应为 false）。
- ✅ Fixed: 在 BinaryExpressionTransformer 中添加了 uint 比较运算符的特殊处理，使用 Integer.compareUnsigned 替代直接比较
