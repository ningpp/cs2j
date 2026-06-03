# Goto 转换全面重新设计

**日期**: 2026-06-03
**范围**: 旧管线 (Transformers/Statement) + GotoAnalyzer
**状态**: 待审核

## 1. 背景与问题

### 1.1 当前实现

当前 goto 转换分为三层：
1. **同作用域 goto 到循环标签** → `continue label;`（正确）
2. **同作用域 goto 到块/其他标签** → `break label;`（正确）
3. **跨作用域 goto** → 状态机（有严重 Bug）

### 1.2 已发现的 Bug

| # | Bug | 严重性 | 根因 |
|---|-----|--------|------|
| 1 | 状态机导致无限循环 | 严重 | case N 重置 `__gotoState=0` 后 break，重新进入 case 0 从头执行所有语句，包括 goto 本身 |
| 2 | 变量声明在状态机循环中被重新执行 | 严重 | case 0 包含所有语句，变量声明每次循环都重新执行 |
| 3 | IsInLabelScope 对同级标签误判 | 设计缺陷 | 只检查标签是否是 goto 的祖先，不检查同方法体同级 |
| 4 | case N 处理器是空壳 | 逻辑缺陷 | 只做 `__gotoState=0; break;`，不包含标签位置的代码 |
| 5 | 守卫代码永远不生效 | 逻辑缺陷 | `if (__gotoState==N)` 在 case 0 中永远为 false（已被 case N 重置） |

### 1.3 根本设计问题

状态机的核心假设存在根本缺陷：
1. 假设 case 0 可以通过守卫跳过标签前代码 — 但守卫在 `__gotoState` 被重置后永远不成立
2. 假设 case N 只需重置状态并 break — 但这导致重新进入 case 0 时从头执行
3. 没有机制在 case 0 中跳过标签之前的代码 — Java 没有 goto，无法"跳到中间"

## 2. 新设计

### 2.1 Goto 分类体系

| 类别 | 描述 | C# 示例 | Java 转换策略 |
|------|------|---------|--------------|
| A1 | goto 在标签标注的循环内 | `outer: for(){ if(x) goto outer; }` | `continue outer;` |
| A2 | goto 在标签标注的块内 | `target: { if(x) goto target; }` | `break target;` |
| B1 | 同方法体后向 goto 到循环标签 | `outer: for(){} if(x) goto outer;` | `continue outer;` |
| B2 | 同方法体后向 goto 到非循环标签 | `target: x=1; if(x) goto target;` | 基本块状态机 |
| B3 | 同方法体前向 goto | `goto skip; x=1; skip: x=2;` | 基本块状态机 |
| C | 真正跨作用域 goto | `target: x=1; for(){ if(x) goto target; }` | 基本块状态机 |
| D | goto case / goto default | `goto case 2;` | switch 重构 |

**统一策略**：B2、B3、C 类统一使用基本块状态机。A1、A2、B1 使用简单的 break/continue 映射。D 类使用 switch 重构。

### 2.2 IsInLabelScope 修复

**当前逻辑**：从 goto 向上遍历父链，只在标签是 goto 的祖先时返回 true。

**新逻辑**：
1. 从 goto 向上遍历父链，如果找到目标标签作为祖先 → 返回 true（A 类）
2. 找到 goto 所在的方法体 BlockSyntax
3. 在该方法体中递归搜索目标标签
4. 如果找到标签，检查标签的声明块是否是 goto 所在块的祖先或相同
5. 如果是 → 返回 true（B 类，同方法体）
6. 否则 → 返回 false（C 类，跨作用域）

```
IsInLabelScope(gotoStmt, targetLabel) → GotoScope enum:
  AncestorLabel   // A 类：标签是 goto 的祖先
  SameMethodBody  // B 类：同方法体但标签不是祖先
  CrossScope      // C 类：跨作用域
```

### 2.3 基本块状态机

#### 2.3.1 基本块定义

基本块（Basic Block）是具有单一入口和单一出口的语句序列。标签和 goto 创建基本块边界。

```
BasicBlock:
  int Index              // 基本块编号（0 起始）
  string? Label          // 起始标签名（如果有）
  List<StatementSyntax> Statements  // 语句列表
  BlockExit Exit         // 出口类型
  int? FallThroughTarget // fall-through 目标基本块编号

BlockExit enum:
  FallThrough   // 顺序执行到下一个基本块
  Goto          // 无条件跳转到另一个基本块
  ConditionalGoto  // 条件跳转 + fall-through
  Return        // return/throw
  Break         // break/continue（非 goto）
```

#### 2.3.2 基本块分割算法

```
SplitIntoBasicBlocks(statements):
  blocks = []
  currentBlock = new BasicBlock(Index: 0)
  
  for each stmt in statements:
    if stmt is LabeledStatementSyntax:
      // 标签开始新的基本块
      if currentBlock.Statements not empty:
        currentBlock.Exit = FallThrough
        currentBlock.FallThroughTarget = blocks.Count + 1
        blocks.Add(currentBlock)
      currentBlock = new BasicBlock(Index: blocks.Count, Label: stmt.Identifier.Text)
      // 标签的内部语句加入当前块
      currentBlock.Statements.Add(stmt.Statement)
    else if stmt is GotoStatementSyntax:
      currentBlock.Statements.Add(stmt)
      currentBlock.Exit = Goto
      blocks.Add(currentBlock)
      currentBlock = new BasicBlock(Index: blocks.Count)
    else if stmt is IfStatementSyntax with goto in body:
      // 条件 goto：分割为条件跳转
      currentBlock.Statements.Add(stmt)
      currentBlock.Exit = ConditionalGoto
      blocks.Add(currentBlock)
      currentBlock = new BasicBlock(Index: blocks.Count)
    else if stmt is ReturnStatementSyntax or ThrowStatementSyntax:
      currentBlock.Statements.Add(stmt)
      currentBlock.Exit = Return
      blocks.Add(currentBlock)
      currentBlock = new BasicBlock(Index: blocks.Count)
    else:
      currentBlock.Statements.Add(stmt)
  
  if currentBlock.Statements not empty:
    currentBlock.Exit = FallThrough  // 最后一个块 fall through 到退出
    blocks.Add(currentBlock)
  
  return blocks
```

#### 2.3.3 状态机生成

```
GenerateStateMachine(blocks, context):
  sb = new StringBuilder()
  sb.AppendLine("int __state = 0;")
  sb.AppendLine("__gotoLoop: while (true) {")
  sb.AppendLine("    switch (__state) {")
  
  for each block in blocks:
    sb.AppendLine($"        case {block.Index}: // {block.Label ?? "block_" + block.Index}")
    
    for each stmt in block.Statements:
      if stmt is GotoStatementSyntax:
        targetState = FindBlockIndexByLabel(blocks, stmt.TargetLabel)
        sb.AppendLine($"            __state = {targetState}; continue __gotoLoop;")
      else if stmt is IfStatementSyntax with goto in body:
        // 条件 goto：生成 if(__state = N; continue) else (fall through)
        sb.AppendLine(TransformConditionalGoto(stmt, blocks, context))
      else:
        sb.AppendLine($"            {Transform(stmt, context)}")
    
    // 处理出口
    switch block.Exit:
      case FallThrough:
        if block.FallThroughTarget != null:
          sb.AppendLine($"            __state = {block.FallThroughTarget}; continue __gotoLoop;")
        else:
          sb.AppendLine("            break __gotoLoop;")
      case Goto:
        // 已在语句中处理
      case Return:
        // return/throw 已在语句中处理
      case ConditionalGoto:
        // 已在条件 goto 中处理
  
  sb.AppendLine("    }")
  sb.AppendLine("}")
  
  return sb.ToString()
```

#### 2.3.4 变量声明提升

状态机中的变量声明需要提升到 while 循环外部，避免每次循环重新声明。

```
HoistVariableDeclarations(blocks):
  hoistedVars = []  // (type, name, initialValue)
  
  for each block in blocks:
    for each stmt in block.Statements:
      if stmt is LocalDeclarationStatementSyntax:
        // 提取变量声明
        for each variable in stmt.Declaration.Variables:
          type = MapType(stmt.Declaration.Type)
          name = variable.Identifier.Text
          initialValue = variable.Initializer != null ? TransformInitializer(...) : "null/0/false"
          hoistedVars.Add((type, name, initialValue))
        // 从块中移除声明，替换为赋值
  
  return hoistedVars
```

生成代码时，在 while 循环前声明所有提升的变量：
```java
int x = 0;  // 提升的变量声明
String y = null;
int __state = 0;
__gotoLoop: while (true) {
    switch (__state) {
        case 0:
            x = 1;  // 原来的声明变为赋值
            ...
    }
}
```

### 2.4 goto case / goto default 支持

#### 2.4.1 goto case 转换策略

将 goto case 转换为 switch 的 fall-through 或辅助变量：

**策略 1：Fall-through（简单情况）**

```csharp
// C#
switch (x) {
    case 1:
        y = 1;
        goto case 2;
    case 2:
        y = 2;
        break;
}
```

```java
// Java - 利用 fall-through
switch (x) {
    case 1:
        y = 1;
        // fall through to case 2
    case 2:
        y = 2;
        break;
}
```

**策略 2：辅助变量（复杂情况）**

当 goto case 的目标 case 在源 case 之前，或存在多个 goto case 时：

```csharp
// C#
switch (x) {
    case 1:
        goto case 3;
    case 2:
        goto case 1;
    case 3:
        y = 3;
        break;
}
```

```java
// Java
int __switchTarget = x;
boolean __switchRedo = false;
__switchLoop:
while (true) {
    switch (__switchTarget) {
        case 1:
            __switchTarget = 3;
            continue __switchLoop;
        case 2:
            __switchTarget = 1;
            continue __switchLoop;
        case 3:
            y = 3;
            break __switchLoop;
    }
}
```

#### 2.4.2 goto default 转换策略

与 goto case 类似，使用辅助变量跳转到 default 分支。

### 2.5 GotoAnalyzer 重新设计

```
GotoAnalysisResult:
  GotoScopeClassification ClassifyGoto(GotoStatementSyntax goto, string targetLabel)
  List<BasicBlock> BasicBlocks
  Dictionary<string, int> LabelToBlockIndex
  List<HoistedVariable> HoistedVariables
  bool NeedsStateMachine

GotoScopeClassification enum:
  InScopeLoop      // A1: goto 在标签标注的循环内
  InScopeBlock     // A2: goto 在标签标注的块/其他内
  SameBodyLoop     // B1: 同方法体后向 goto 到循环标签
  SameBodyOther    // B2/B3: 同方法体 goto 到非循环标签
  CrossScope       // C: 真正跨作用域
```

## 3. 转换规则

### 3.1 GotoStatement 转换

| C# goto 模式 | 分类 | Java 映射 |
|---|---|---|
| `goto label;` 标签标注循环，goto 在循环内 | A1 | `continue label;` |
| `goto label;` 标签标注块/其他，goto 在块内 | A2 | `break label;` |
| `goto label;` 同方法体，标签标注循环 | B1 | `continue label;` |
| `goto label;` 同方法体，标签标注非循环 | B2/B3 | `__state = N; continue __gotoLoop;`（状态机内） |
| `goto label;` 跨作用域 | C | `__state = N; continue __gotoLoop;`（状态机内） |
| `goto case X;` | D | `__switchTarget = X; continue __switchLoop;` |
| `goto default;` | D | `__switchTarget = -1; continue __switchLoop;` |

### 3.2 LabelStatement 转换

| C# 模式 | Java 输出 | 说明 |
|---|---|---|
| `label: for(){}` | `label: for(){}` | 直接输出 |
| `label: while(){}` | `label: while(){}` | 直接输出 |
| `label: do{}while()` | `label: do{}while()` | 直接输出 |
| `label: foreach(){}` | `label: for(){}` | 走已有 foreach 转换 |
| `label: { }` | `label: { }` | 直接输出 |
| `label: stmt;` | `label: { stmt; }` | 包装为块 |
| 状态机内的标签 | 不输出（由 case N 替代） | 标签语义由状态机 case 承载 |

### 3.3 方法体转换决策

```
TransformBlock(block, context):
  if scopeDepth == 0:
    PrescanLabels(block.Statements, context)
    analysis = GotoAnalyzer.Analyze(block, context.Labels)
    
    if analysis.HasOnlyInScopeGotos:
      // A 类：直接转换，不需要状态机
      return TransformStatements(block.Statements, context)
    
    if analysis.NeedsStateMachine:
      // B/C 类：需要状态机
      return GenerateStateMachine(analysis.BasicBlocks, context)
  
  // 无 goto 或嵌套块：正常转换
  return TransformStatements(block.Statements, context)
```

## 4. 文件修改清单

### 4.1 修改的文件

| 文件 | 修改内容 |
|---|---|
| `GotoAnalyzer.cs` | 重新设计：输出基本块分割结果、goto 分类、变量提升信息 |
| `StatementTransformer.LabelAndGoto.cs` | 重写状态机生成（基本块方式）；修改 IsInLabelScope 返回分类枚举；新增 goto case/default 转换 |
| `StatementTransformer.cs` | 更新 TransformBlock 中的 goto 处理逻辑 |
| `StatementTransformer.SwitchAndResource.cs` | 新增 goto case/default 的 switch 重构支持 |
| `LabelGotoStatementTests.cs` | 新增测试覆盖所有类别（A1-D） |

### 4.2 无需修改的文件

| 文件 | 原因 |
|---|---|
| `LabelRegistry.cs` | 接口不变 |
| `MethodConversionState.cs` | Labels 和 GotoAnalyzer 属性不变 |
| `ConversionContext.cs` | Labels facade 不变 |

## 5. 测试设计

### 5.1 A 类：同作用域 goto（已有测试，验证不回归）

| # | 场景 | C# 输入 | 期望 Java 输出 |
|---|---|---|---|
| 1 | A1: goto 循环标签 | `outer: for(){ if(x) goto outer; }` | `continue outer;` |
| 2 | A2: goto 块标签 | `target: { if(x) goto target; }` | `break target;` |

### 5.2 B 类：同方法体 goto

| # | 场景 | C# 输入 | 期望 Java 输出 |
|---|---|---|---|
| 3 | B1: 同方法体后向 goto 到循环 | `outer: for(){} if(x) goto outer;` | `continue outer;` |
| 4 | B2: 同方法体后向 goto 到非循环 | `target: x=1; if(x) goto target;` | 状态机，case 0 从 target 开始 |
| 5 | B3: 同方法体前向 goto | `goto skip; x=1; skip: x=2;` | 状态机，x=1 在 case 0，x=2 在 case 1 |
| 6 | B2+变量声明 | `int x=0; target: x=1; if(x) goto target;` | 变量提升到 while 外 |

### 5.3 C 类：跨作用域 goto

| # | 场景 | C# 输入 | 期望 Java 输出 |
|---|---|---|---|
| 7 | C: goto 从 for 内跳出到外部标签 | `target: x=1; for(){ if(x) goto target; }` | 状态机 |
| 8 | C: goto 从 if 内跳出到外部标签 | `target: x=1; if(x) goto target;` | 状态机 |
| 9 | C: goto 从 try 内跳出到外部标签 | `target: x=1; try { if(x) goto target; }` | 状态机 |

### 5.4 D 类：goto case / goto default

| # | 场景 | C# 输入 | 期望 Java 输出 |
|---|---|---|---|
| 10 | goto case（fall-through） | `case 1: goto case 2; case 2: break;` | Java fall-through |
| 11 | goto case（反向） | `case 2: goto case 1; case 1: break;` | 辅助变量 while+switch |
| 12 | goto default | `case 1: goto default; default: break;` | 辅助变量 |

### 5.5 复杂场景

| # | 场景 | C# 输入 | 期望 Java 输出 |
|---|---|---|---|
| 13 | 多标签状态机 | `s1: x=1; if(c1) goto s2; goto s1; s2: x=2;` | 基本块状态机 |
| 14 | 标签+循环混合 | `outer: for(){ s1: x=1; if(c) goto s1; }` | 内部状态机 |
| 15 | 无限循环验证 | `target: x=1; if(x) goto target;` | 状态机不产生无限循环 |

### 5.6 回归测试

| # | 场景 | 验证点 |
|---|---|---|
| 16 | 标签注册表隔离 | 第二个方法的标签不影响第一个方法 |
| 17 | 无 goto 的方法 | 不生成状态机代码 |
| 18 | 标签标注循环 | 标签正确保留在循环上 |

## 6. 实现约束

- 必须在旧管线中实现
- 不涉及新管线 (HIR/Java2) 的改动
- 遵循现有 partial class 拆分模式
- 遵循现有预扫描模式
- 遵循现有测试模式（ConversionPipeline 端到端测试）
- 状态机变量名使用 `__` 前缀避免与用户代码冲突
