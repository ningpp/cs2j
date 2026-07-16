# cs-xml 转换错误分析日志

## Iteration 0 — goto-preprocessor-error
- **阶段**: C# → Java 转换（goto 预处理）
- **Java 文件**: N/A（转换未生成 Java 文件）
- **出错信息**: `System\Xml\Xsl\XPathConvert.cs: goto/label syntax remains after preprocessing`
- **代码片段**:
  ```csharp
  #else
      goto LDone;
  }
  #endif
  ```
- **对应 C# 文件**: `d:\csharpxml\System\Xml\Xsl\XPathConvert.cs` 等 4 个文件
- **根因分类**: Transformer
- **涉及组件**: `src/CSharpToJava.Core/GotoEliminator/StateMachineBuilder.cs`
- **分析**: `StateMachineBuilder` 重写了 `VisitMethodDeclaration`、`VisitConstructorDeclaration`、`VisitOperatorDeclaration` 等，但缺少 `VisitConversionOperatorDeclaration`。`XPathConvert.cs` 中 `FloatingDecimal` 的 `explicit operator double` 等方法体含 goto/label，因此未被状态机改写，残留 goto 被 `ProjectGotoPreprocessor` 检测为 Error，导致转换失败。
