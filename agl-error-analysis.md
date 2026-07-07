# AGL Error Analysis

## Iteration 1 — Event Invocation (AlgorithmBase.progressChanged)
- **Java 文件**: d:\agl202607-MSAGL\automaticgraphlayout\src\main\java\Microsoft\Msagl\Core\AlgorithmBase.java
- **行号**: 286
- **错误信息**: 找不到符号 方法 progressChanged(Microsoft.Msagl.Core.AlgorithmBase,Microsoft.Msagl.Core.ProgressChangedEventArgs)
- **代码片段**:
  ```java
  progressChanged(this, new ProgressChangedEventArgs(stageStartRatio + stageProgress));
  ```
- **对应 C# 文件**: E:\agl-master\GraphLayout\MSAGL\Core\AlgorithmBase.cs
- **根因分类**: Transformer
- **涉及组件**: src/CSharpToJava.Core/Transformers/Expression/Transformers/InvocationExpressionTransformer.cs
- **分析**: In the project pipeline, `ProgressChanged(this, args)` (C# old-style event invocation inside `if (Event != null)`) was not resolved as DelegateInvoke by the semantic model, so the existing event detection code was skipped. The identifier was treated as a regular method call and camelCased to `progressChanged()` instead of `fireProgressChanged()`.
- **修复**: Added last-resort event detection after the DelegateInvoke check: if a bare identifier matches an event on the enclosing type, generate `fireXxx()` instead of camelCasing it.
- ✅ Fixed
