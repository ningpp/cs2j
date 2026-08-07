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
