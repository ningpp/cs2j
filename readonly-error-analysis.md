# Readonly Struct Conversion — Error Analysis

## Iteration 1 — CS0128 / CS0136 / CS0165

- **C# 文件**: 
  - `regular-expressions\src\System\Text\RegularExpressions\RegexInterpreter.cs` (lines 848-1305)
  - `regular-expressions\src\System\Text\RegularExpressions\RegexCompiler.cs` (line 964, 998)

- **错误信息**: 
  - CS0128: 已在此范围定义了名为"mark"的局部变量或函数 (56 occurrences)
  - CS0136: 无法在此范围中声明名为"success"的局部变量或参数 (2 occurrences)
  - CS0165: 使用了未赋值的局部变量"mark"/"count"/"c"/"ch" (22 occurrences)

- **代码片段** (RegexInterpreter.cs, original lines 849-871):
  ```csharp
  case RegexCode.Lazybranchcount:
      StackPop(2);
      {                                   // <-- this block is removed by the converter
          int mark = StackPeek();
          int count = StackPeek(1);
          // ...
      }
      continue;

  case RegexCode.Lazybranchcount | RegexCode.Back:
      TrackPop(3);
      {                                   // <-- this block is removed too
          int mark = TrackPeek();          // CS0128: duplicate "mark"
          // ...
      }
  ```

- **对应原始 C# 文件**: `D:\cs-java-0908-sdk\.cs2j-generic-src\regular-expressions\src\System\Text\RegularExpressions\RegexInterpreter.cs`

- **根因分类**: 转换器在非目标 struct 文件中错误展平了 switch case 的 `{ }` 块

- **涉及组件**: `src/CSharpToJava.Core/ReadOnlyStructMaker/AssignmentRewriter.cs` — `VisitSwitchSection` 方法

- **分析**: `AssignmentRewriter.VisitSwitchSection` 展平了 switch case 中的嵌套 `{ }` 块。这些块为 case 内的局部变量提供独立作用域。当 case 中有多个语句（如 `{ }` 块 + `break;` 或 `continue;`），`isSingleBlockCase` 为 false，导致所有嵌套块被展平。这导致不同 case 中同名变量（如 `mark`, `count`, `success`）在同一个作用域中重复声明（CS0128），且变量在使用前未在当前路径上赋值（CS0165）。这些文件是 class（`RegexCompiler`, `RegexInterpreter`），不是 struct，但 `AssignmentRewriter` 被应用是因为它们引用了被转换的 struct。

**修复**: 使用 `SyntaxAnnotation` 标记由转换器生成的 `BlockSyntax`（来自 `VisitExpressionStatement`/`VisitReturnStatement`/`VisitLocalDeclarationStatement`），在 `VisitSwitchSection` 中只展平带标记的块，保留原始代码中的块。

✅ Fixed — Build succeeded with 0 errors after re-running make-readonly.
