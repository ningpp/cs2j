# C# → Java 转换错误分析报告

本文档记录 `csharp-RegularExpressions` 项目转换过程中的编译错误及修复过程。

---

## Iteration 1 — 三种编译错误（已修复 ✅）

### 1.1 `ref` 返回类型未正确转换

- **Java 文件**: `d:\cs-re-20260807\src\main\java\System\Text\ValueStringBuilder.java`
- **行号**: 97, 105
- **错误信息**: `需要<标识符>` (需要标识符)
- **对应 C# 文件**: `D:\csharp-RegularExpressions\src\Common\System\Text\ValueStringBuilder.cs`
- **根因分类**: Transformer 逻辑缺陷
- **涉及组件**: `src/CSharpToJava.Core/Transformers/Member/MethodTransformer.cs` (GetReturnType 方法)
- **分析**: 转换器将 C# 的 `ref char` 返回类型直接映射为 Java 的 `ref char`，但 Java 没有 `ref` 返回类型语法。
- **修复**: 在 `GetReturnType` 方法中添加对 `RefTypeSyntax` 的处理，提取内部类型并去掉 `ref` 关键字。

**状态**: ✅ Fixed

---

### 1.2 十六进制字面量 `U` 后缀未去除

- **Java 文件**: `d:\cs-re-20260807\src\main\java\dotnet\system\HexConverter.java`
- **行号**: 89-90, 165-166
- **错误信息**: `需要')'`, `需要';'`, `不是语句`
- **对应 C# 文件**: `D:\csharp-RegularExpressions\src\Common\System\HexConverter.cs`
- **根因分类**: Transformer 逻辑缺陷
- **涉及组件**: `src/CSharpToJava.Core/Transformers/Expression/Transformers/LiteralExpressionTransformer.cs` (TransformNumericLiteral 方法)
- **分析**: 对十六进制/二进制字面量（以 `0x` 或 `0b` 开头）直接原样返回，没有去除 C# 的 `U` 后缀。
- **修复**: 在 `TransformNumericLiteral` 方法中添加 `StripHexSuffix` 辅助方法来去除 `U`/`L` 后缀。

**状态**: ✅ Fixed

---

### 1.3 Java 参数名 `_` 不允许使用

- **Java 文件**: `d:\cs-re-20260807\src\main\java\csharp\xunit\ConditionalFactAttribute.java` 等
- **行号**: 46, 46, 48
- **错误信息**: `此处不允许使用下划线`
- **对应 C# 文件**: C# 测试工具类中的 discard 参数
- **根因分类**: Transformer 逻辑缺陷
- **涉及组件**: `src/CSharpToJava.Core/Transformers/Member/ConstructorTransformer.cs` 和 `IndexerTransformer.cs`
- **分析**: C# 使用 `_` 作为 discard 参数名，但 Java 9+ 开始 `_` 是保留关键字。构造函数和索引器转换器没有调用 `EscapeJavaKeyword` 方法。
- **修复**: 在 `ConstructorTransformer.cs` 和 `IndexerTransformer.cs` 中添加 `EscapeJavaKeyword` 调用。

**状态**: ✅ Fixed

---

## Iteration 2 — 表达式体 void 方法缺少分号

- **Java 文件**: 
  - `CaptureCollection.java:[200,24]`
  - `GroupCollection.java:[213,95]`
  - `MatchCollection.java:[207,24]`
- **行号**: 200, 213, 207
- **错误信息**: `需要';'`
- **对应 C# 文件**: 
  - `CaptureCollection.cs` - `void IEnumerator.Reset() => _index = -1;`
  - `GroupCollection.cs` - `void IEnumerator.Reset() => _index = -1;`
  - `MatchCollection.cs` - `void IEnumerator.Reset() => _index = -1;`
- **代码片段**:
  ```java
  public void reset() {
      _index = -1
      _iteratorHasNext = false;
  }
  ```
- **根因分类**: Transformer 逻辑缺陷
- **分析**: 表达式体 void 方法（如 `void Reset() => _index = -1;`）在转换时缺少分号。此外还出现了不存在的字段 `_iteratorHasNext`，疑似字段初始化代码被错误地插入到方法体中。

**状态**: 🔧 待修复

---

## Iteration 3 — make-readonly 预处理 CS0206（ref 字段被改写为属性）

- **阶段**: 转换预处理（readonly-struct-maker 后的 `dotnet build` 验证），尚未进入 Maven
- **错误文件**: `d:\cs-re-20260807\.cs2j-readonly-src\src\System\Text\RegularExpressions\RegexReplacement.cs`
- **行号**: 42
- **错误信息**: `error CS0206: 非引用返回属性或索引器不能用作 out 或 ref 值`
- **代码片段**:
  ```csharp
  FourStackStrings stackStrings = default;
  var strings = new ValueListBuilder<string>(MemoryMarshal.CreateSpan(ref stackStrings.Item1!, 4));
  ```
- **对应 C# 文件**: `d:\csharp-RegularExpressions\src\System\Text\RegularExpressions\RegexReplacement.cs`（`FourStackStrings` 嵌套结构，行 96-103）
- **根因分类**: Lowering pass 不足（ReadOnlyStructMaker 资格判定缺陷）
- **涉及组件**: `src/CSharpToJava.Core/ReadOnlyStructMaker/StructAnalyzer.cs`、`src/CSharpToJava.Core/ReadOnlyStructMaker/CallGraphBuilder.cs`
- **分析**: `FourStackStrings` 是 `[StructLayout(Sequential)]` 的裸内存块结构（公共字段经 `ref stackStrings.Item1` 交给 `MemoryMarshal.CreateSpan`）。分析器未检查外部 ref/out 字段访问，将其归入 L3/L4 路径把公共字段改写为 get-only 属性，导致调用点 `ref stackStrings.Item1` 非法（CS0206）。`CallGraphBuilder.HasExternalRefFieldAccess`（专为 CS0206 设计、已支持 `!` 解包）存在但从未被调用（死代码）。
- **修复**: 在 `StructAnalyzer.AnalyzeCore` 的无变异方法分支（L1–L4 资格判定）中接入 `HasExternalRefFieldAccess` 守卫：非私有字段被外部 ref/out 访问时返回 NotConvertible，保持原结构不变。新增红→绿测试 `PublicFields_ExternalRefAccess_NotConverted`，全量测试 2433 通过。commit `945942fb`。

**状态**: ✅ Fixed

---

## Iteration 4 — void 表达式体属性自减产生非法裸语句（不是语句）

- **Java 文件**: `d:\cs-re-20260807\dotnet-text-regularexpressions\src\main\java\dotnet\text\regularexpressions\RegexParser.java`
- **行号**: 2698
- **错误信息**: `不是语句`（javac: not a statement）
- **代码片段**:
  ```java
  private void popKeepOptions() {
      var _prev1 = _optionsStack.getLength();
      _optionsStack.setLength(_prev1 - 1);
      _prev1;   // ← 非法裸语句
  }
  ```
- **对应 C# 文件**: `d:\csharp-RegularExpressions\src\System\Text\RegularExpressions\RegexParser.cs`（行 2305）
  ```csharp
  private void PopKeepOptions() => _optionsStack.Length--;
  ```
- **根因分类**: Transformer 逻辑缺陷
- **涉及组件**: `src/CSharpToJava.Core/Transformers/Expression/Transformers/UnaryExpressionTransformer.cs`
- **分析**: `IsDiscardedValueContext` 只识别 `ExpressionStatementSyntax` 和 for 递增器，未覆盖表达式体成员（父节点为 `ArrowExpressionClauseSyntax`）。void 表达式体方法中的属性 `--` 被误判为“值消费上下文”，走 hoist 路径产生临时变量 `_prev1`，其结果又被 `MethodTransformer` 当作语句输出，形成非法裸语句。
- **修复**: 扩展 `IsDiscardedValueContext` 识别 void 方法/局部函数表达式体、set/init 访问器表达式体、void 委托 lambda；并把 postfix/prefix 中基于 `node.Parent is ExpressionStatementSyntax` 的守卫全部改用该谓词。新增红→绿测试 `VoidExpressionBodyPropertyIncrementTests`（2 个），全量测试 2435 通过。

**状态**: 🔧 验证中

