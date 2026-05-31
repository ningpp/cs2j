# LabelStatement & GotoStatement 系统性支持设计

**日期**: 2026-05-31
**范围**: 旧管线 (Transformers/Statement)
**状态**: 待审核

## 1. 背景与目标

C# 支持 `label:` 和 `goto label`，但 Java 不支持 `goto`。Java 只有 `label:` + `break label;` / `continue label;`，且语义受限：
- `break label;` 只能跳出标签标注的块
- `continue label;` 只能跳到标签标注的循环起始

**目标**：在旧管线中系统性支持 LabelStatement 和 GotoStatement，覆盖简单常见模式（标签直接映射 + goto 转为 break/continue label），并补充完善的单元测试。

**不支持的 goto 模式**（输出 TODO 注释）：
- `goto case X;` / `goto default;`
- 跨作用域 goto（goto 不在标签的作用域内）
- 后向跳转到非循环非块结尾

## 2. 语义映射规则

### 2.1 GotoStatement 映射

| C# goto 模式 | Java 映射 | 语义等价性 |
|---|---|---|
| `goto label;` 其中 label 标注循环，goto 在循环内 | `continue label;` | 等价 |
| `goto label;` 其中 label 标注块，goto 在块内 | `break label;` | 等价（label 后紧跟块结束时） |
| `goto label;` 其中 label 标注普通语句，goto 在作用域内 | `break label;` | 等价（label 后紧跟语句结束时） |
| `goto case X;` | `/* TODO: goto case - unsupported */` | 不等价 |
| `goto default;` | `/* TODO: goto default - unsupported */` | 不等价 |
| `goto label;` 跨作用域 | `/* TODO: goto label - cross-scope goto unsupported */` | 不等价 |
| `goto label;` 未知标签 | `/* TODO: goto label - label not found in scope */` | 不等价 |

### 2.2 LabelStatement 映射

| C# 模式 | Java 输出 | 说明 |
|---|---|---|
| `label: for(...){}` | `label: for(...){}` | 直接输出 |
| `label: while(...){}` | `label: while(...){}` | 直接输出 |
| `label: do{...}while(...)` | `label: do{...}while(...)` | 直接输出 |
| `label: foreach(...){}` | `label: for(...){}` | 走已有 foreach 转换 |
| `label: { ... }` | `label: { ... }` | 直接输出 |
| `label: stmt;` (非循环非块) | `label: { stmt; }` | 包装为块，使 break label 可用 |

## 3. 架构设计

### 3.1 新增组件 — LabelRegistry

标签注册表，存储方法级标签信息，与 `MethodConversionState` 集成。

```
LabelRegistry
├── Dictionary<string, LabelInfo> _labels
├── Register(name, kind)    // kind: Loop, Block, Other
├── TryGetLabel(name) → LabelInfo?
├── Contains(name) → bool
└── Clear()                 // 每个方法转换结束后清理

LabelInfo
├── string Name
└── LabelKind Kind          // Loop, Block, Other
```

**集成方式**：作为 `MethodConversionState` 的属性挂在 `ConversionContext` 上，与现有 `_methodStack` 模式一致。每个 `PushMethod()` 时创建新实例，`PopMethod()` 时自动丢弃。

### 3.2 预扫描流程

在 `StatementTransformer.TransformBlock()` 处理语句列表之前，调用 `PrescanLabels(statements, context)`：

```
PrescanLabels(statements, context):
  for each stmt in statements:
    if stmt is LabeledStatementSyntax labeled:
      kind = 判断 labeled.Statement 的类型:
        ForStatement/WhileStatement/DoStatement/ForEachStatement → Loop
        BlockSyntax → Block
        其他 → Other
      context.LabelRegistry.Register(labeled.Identifier.Text, kind)
    if stmt contains nested blocks (if/for/while etc.):
      递归 PrescanLabels 内部语句列表
```

与现有 `PreScanLambdaCaptures` 模式一致。

### 3.3 转换方法

**TransformLabelStatement**：

```
TransformLabelStatement(stmt, context):
  labelName = stmt.Identifier.Text
  innerResult = Transform(stmt.Statement, context)
  
  if stmt.Statement is loop or block:
    return "labelName: innerResult"
  else:
    return "labelName: { innerResult; }"
```

**TransformGotoStatement**：

```
TransformGotoStatement(stmt, context):
  if stmt.Kind() == GotoCaseStatement:
    return "/* TODO: goto case - unsupported */"
  if stmt.Kind() == GotoDefaultStatement:
    return "/* TODO: goto default - unsupported */"
  
  targetLabel = (stmt.Expression as IdentifierNameSyntax)?.Text
  if targetLabel == null:
    return "/* TODO: goto - unsupported pattern */"
  
  if !context.LabelRegistry.Contains(targetLabel):
    return "/* TODO: goto {targetLabel} - label not found in scope */"
  
  labelInfo = context.LabelRegistry[targetLabel]
  
  if !IsInLabelScope(stmt, targetLabel):
    return "/* TODO: goto {targetLabel} - cross-scope goto unsupported */"
  
  if labelInfo.Kind == Loop:
    return "continue {targetLabel};"
  else:
    return "break {targetLabel};"
```

**IsInLabelScope**：从 GotoStatement 节点向上遍历 `Parent` 链，查找是否存在名为 targetLabel 的 `LabeledStatementSyntax`。若找到 → 在作用域内；遍历到方法根节点未找到 → 跨作用域。

## 4. 文件修改清单

### 4.1 修改的现有文件

| 文件 | 修改内容 |
|---|---|
| `StatementTransformer.cs` | switch 中添加 `LabelStatement`、`GotoStatement`、`GotoCaseStatement`、`GotoDefaultStatement` 条目；`TransformBlock` 中调用 `PrescanLabels` |
| `ConversionContext.cs` | `MethodConversionState` 中添加 `LabelRegistry` 属性；`PushMethod` 时初始化 |

### 4.2 新增文件

| 文件 | 内容 |
|---|---|
| `StatementTransformer.LabelAndGoto.cs` | partial class 方法：`PrescanLabels`、`TransformLabelStatement`、`TransformGotoStatement`、`IsInLabelScope` |
| `Context/LabelRegistry.cs` | `LabelRegistry` 和 `LabelInfo` 类 |
| `tests/CSharpToJava.Tests/LabelGotoStatementTests.cs` | 端到端单元测试 |

### 4.3 无需修改的文件

- `StatementTransformer.SwitchAndResource.cs` — `IsSwitchSectionTerminal()` 已正确识别 GotoStatement
- `StatementTransformer.Utilities.cs` — 无需改动

## 5. 测试设计

新建 `tests/CSharpToJava.Tests/LabelGotoStatementTests.cs`，使用项目现有的 `ConversionPipeline` 端到端测试模式。

### 5.1 LabelStatement 测试

| # | 场景 | C# 输入 | 期望 Java 输出 |
|---|---|---|---|
| 1 | 标签标注 for 循环 | `label: for(int i=0;...)` | `label: for(int i=0;...)` |
| 2 | 标签标注 while 循环 | `label: while(...)` | `label: while(...)` |
| 3 | 标签标注 do-while 循环 | `label: do{...}while(...)` | `label: do{...}while(...)` |
| 4 | 标签标注 foreach 循环 | `label: foreach(var x in arr)` | `label: for(var x : arr)` |
| 5 | 标签标注块 | `label: { ... }` | `label: { ... }` |
| 6 | 标签标注普通语句 | `label: x = 1;` | `label: { x = 1; }` |
| 7 | 嵌套标签 | `outer: for(...){ inner: for(...){} }` | 两层标签均保留 |

### 5.2 GotoStatement 测试

| # | 场景 | C# 输入 | 期望 Java 输出 |
|---|---|---|---|
| 8 | goto 循环标签 → continue | `outer: for(...){ if(x) goto outer; }` | `continue outer;` |
| 9 | goto 块标签 → break | `label: { if(x) goto label; }` | `break label;` |
| 10 | goto 普通语句标签 → break | `label: stmt; if(x) goto label;` | `break label;` |
| 11 | goto case → TODO 注释 | `goto case 1;` | `/* TODO: goto case */` |
| 12 | goto default → TODO 注释 | `goto default;` | `/* TODO: goto default */` |
| 13 | goto 跨作用域 → TODO 注释 | `label: { } if(x) goto label;` | `/* TODO: goto label - cross-scope */` |
| 14 | goto 未知标签 → TODO 注释 | `goto nonexistent;` | `/* TODO: goto nonexistent - label not found */` |

### 5.3 break/continue 标签测试

| # | 场景 | C# 输入 | 期望 Java 输出 |
|---|---|---|---|
| 15 | break label | `outer: for(...){ break outer; }` | `break outer;` |
| 16 | continue label | `outer: for(...){ continue outer; }` | `continue outer;` |

### 5.4 注册表隔离测试

| # | 场景 | 验证点 |
|---|---|---|
| 17 | 连续转换两个方法 | 第二个方法的标签不影响第一个方法的转换 |

## 6. 实现约束

- 必须在旧管线中实现（用户明确要求）
- 不涉及新管线 (HIR/Java2) 的改动
- 遵循现有 partial class 拆分模式（`StatementTransformer.LabelAndGoto.cs`）
- 遵循现有预扫描模式（`PreScanLambdaCaptures`）
- 遵循现有测试模式（`ConversionPipeline` 端到端测试）
