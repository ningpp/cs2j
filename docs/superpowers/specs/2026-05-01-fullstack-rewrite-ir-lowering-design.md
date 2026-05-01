# CSharpToJava 全栈重写：分层 IR + 语义 Lowering 架构

**日期:** 2026-05-01
**状态:** 已批准
**范围:** Core 转换引擎全栈重写

## 动机

当前架构存在三个系统性问题：

1. **Java IR 建模不完整** — 大量表达式退化为 `JavaRawExpression`（裸字符串），后续 Rewriter 只能做不可靠的字符串匹配，无法进行深层的语义变换
2. **C# 语义分析不充分** — Visitor/Transformer 在转换时对 Roslyn 语义模型的利用不完整，很多地方依赖语法层面的模式匹配而非类型/符号级别的分析
3. **管道设计问题** — Emit 之后依赖 20+ 个 IR Rewriter 修补输出，核心转换器产出的 IR 本身就不正确

这导致了在转换 MSAGL 等复杂项目时产生大量编译错误和语义错误，需要人工介入修复。

## 目标

- 转换结果不需要人工介入修改，编译通过且运行正确
- 能够转换复杂、庞大的 C# 项目（Roslyn、ASP.NET Core 等）
- 保留现有 TypeMapping 配置体系、LINQ Rewriter、Partial Type Merger、Compatibility Pack

## 架构概览

采用**分层 IR + 语义 Lowering**架构：

```
┌─────────────────────────────────────────────────────┐
│                   Phase 1: Frontend                 │
│  C# Source → Roslyn Parse → Semantic Analysis       │
│  (保留 LINQ Rewriter、Partial Type Merger)           │
├─────────────────────────────────────────────────────┤
│                   Phase 2: HIR Generation           │
│  Roslyn Syntax Tree → CSharpToJavaHIRGenerator      │
│  产出：C#-flavored Java IR (保留C#语义概念)          │
├─────────────────────────────────────────────────────┤
│                   Phase 3: Lowering                 │
│  一系列 Lowering Pass，逐个消除 C# 特有语义          │
│  HIR → LowerRefOut → LowerYield → ... → Pure Java IR │
├─────────────────────────────────────────────────────┤
│                   Phase 4: Validation               │
│  类型/方法/异常检查（纯诊断，不修改 IR）             │
├─────────────────────────────────────────────────────┤
│                   Phase 5: CodeGen                  │
│  Pure Java IR → JavaCodeGenerator → .java 文件      │
└─────────────────────────────────────────────────────┘
```

**核心区别：** 当前 Rewriter 在**不完整的 IR 上做字符串修补**，而新架构 Lowering 在**完整结构化 IR 上做语义变换**。

## 第一部分：IR 模型设计

### 设计原则

- IR 中**永远不存在裸字符串**，每个节点都有明确的结构字段
- 不搞两套完全独立的 IR 树，而是**一套共享基类 + C# 扩展节点**
- 每个标识符/成员访问/方法调用节点必须携带 Roslyn 符号引用（`ISymbol`）

### 共享基础 IR 节点（C# 和 Java 通用）

**表达式类：**
- `JavaLiteralExpression` — 字面量
- `JavaBinaryExpression` — 二元运算（左操作数 + 运算符 + 右操作数）
- `JavaUnaryExpression` — 一元运算
- `JavaIdentifierExpression` — 标识符引用（绑定 `ISymbol`）
- `JavaMemberAccessExpression` — 成员访问（target + member name，绑定 `ISymbol`）
- `JavaInvocationExpression` — 方法调用（target + method name + 参数列表，绑定 `IMethodSymbol`）
- `JavaThisExpression` / `JavaBaseExpression`
- `JavaNewExpression` — 对象创建
- `JavaCastExpression` — 类型转换
- `JavaConditionalExpression` — 三元运算符
- `JavaAssignmentExpression` — 赋值
- `JavaArrayAccessExpression` — 数组访问
- `JavaLambdaExpression` — Lambda 表达式

**语句类：**
- `JavaBlockStatement` / `JavaIfStatement` / `JavaForStatement` / `JavaWhileStatement` / `JavaDoWhileStatement`
- `JavaForEachStatement` / `JavaReturnStatement` / `JavaThrowStatement` / `JavaTryCatchStatement`
- `JavaSwitchStatement` / `JavaBreakStatement` / `JavaContinueStatement`
- `JavaExpressionStatement` — 表达式语句包装
- `JavaVariableDeclarationStatement` — 局部变量声明

**声明类：**
- `JavaCompilationUnit` — 文件级：package + imports + 类型声明
- `JavaClassDeclaration` / `JavaInterfaceDeclaration` / `JavaEnumDeclaration` — 类型声明
- `JavaFieldDeclaration` / `JavaMethodDeclaration` / `JavaConstructorDeclaration` — 成员声明

### C# 扩展节点（HIR 中存在，Lowering 后消除）

- `CSharpRefExpression` / `CSharpOutExpression` — ref/out 参数传递
- `CSharpPropertyAccessExpression` — 属性访问
- `CSharpIndexerAccessExpression` — 索引器访问
- `CSharpOperatorCallExpression` — 运算符重载调用
- `CSharpEventSubscribeExpression` / `CSharpEventRaiseExpression` — 事件
- `CSharpStructValueCopyExpression` — struct 值拷贝
- `CSharpYieldReturnStatement` / `CSharpYieldBreakStatement` — yield 迭代器
- `CSharpDelegateCreationExpression` — 委托构造
- `CSharpUsingStatement` — using 语句
- `CSharpPatternExpression` / `CSharpSwitchExpression` — 模式匹配

### 关键约束

- **无 `JavaRawExpression` 类型**。如果 HIR Generator 遇到无法处理的 C# 构造，必须报告诊断错误，而非静默降级为裸字符串
- C# 扩展节点只存在于 HIR 阶段；Lowering 完成后，IR 树中不得存在任何 C# 扩展节点（可由 Validation 阶段验证）

## 第二部分：管道设计

### 三阶段管线

```
Phase 1: Frontend    — 解析 + 预处理（C# Syntax Tree 层）
Phase 2: HIR Gen     — C# Syntax Tree → C#-flavored Java IR
Phase 3: Lowering    — C#-flavored IR → Pure Java IR（逐 Pass 降级）
Phase 4: Validation  — 纯诊断 Pass，不修改 IR
Phase 5: CodeGen     — Pure Java IR → Java 源代码
```

### Phase 1: Frontend

保留并优化三个现有子系统：

- **LINQ Rewriter** — 操作 C# Syntax Tree，将 LINQ 降级为过程化代码。优化点：当 LINQ 无法完全降级时应生成 Stream API 调用而非报错/跳过
- **Partial Type Merger** — 将 partial class 合并为单一类型声明。优化点：跨多个项目文件的 partial 合并正确性
- **Compatibility Pack** — 为 Java 生成兼容性辅助类

### Phase 2: HIR Generation

`CSharpToJavaHIRGenerator` 将 Roslyn C# Syntax Tree 转换为 C#-flavored Java IR。详细设计见第三部分。

### Phase 3: Lowering

**Lowering Pass 接口：**

```csharp
interface ILoweringPass
{
    string Name { get; }
    JavaCompilationUnit Apply(JavaCompilationUnit unit, ConversionContext context);
}
```

每个 Pass 输入一个 CompilationUnit，返回修改后的 CompilationUnit。Pass 之间完全解耦，可独立开关和调试。

**执行顺序（顺序至关重要——后续 Pass 假设前面的 Pass 已消除对应语义）：**

| 序号 | Pass | 功能 |
|------|------|------|
| 1 | `LowerRefOut` | ref/out 参数 → Holder 包装 |
| 2 | `LowerYield` | yield 方法 → 迭代器状态机内部类 |
| 3 | `LowerUsing` | using 语句 → try-finally + close() |
| 4 | `LowerProperty` | 属性访问 → getter/setter 方法调用 |
| 5 | `LowerIndexer` | 索引器 → get(index)/set(index,value) |
| 6 | `LowerOperator` | 运算符重载 → 静态方法调用 |
| 7 | `LowerEvent` | event → listener 模式 |
| 8 | `LowerDelegate` | delegate → @FunctionalInterface |
| 9 | `LowerStruct` | struct 值拷贝 → clone() 调用 |
| 10 | `LowerPatternMatch` | 模式匹配展开 |
| 11 | `LowerAsync` (未来) | async/await → CompletableFuture 链 |
| 12 | `VariableResolution` | 变量名去重 + 冲突解决 |

### Phase 4: Validation

纯诊断层，不修改 IR：
- 类型存在性验证
- 方法存在性验证
- 异常声明验证
- 静态上下文验证
- 类型参数验证

### Phase 5: CodeGen

从 Pure Java IR 生成 Java 源码。详细设计见第五部分。

### 当前 20+ Rewriter 的去向

| 当前 Rewriter | 新架构去向 |
|---------------|-----------|
| OperatorPrecedenceRewriter | CodeGen 括号优先级 |
| MemberwiseCloneRewriter | LowerStruct Pass |
| MapEntryTypeRewriter | HIR Generator 直接生成 |
| DelegateInvocationRewriter | LowerDelegate Pass |
| MathMethodRewriter | HIR Generator 方法名映射 |
| GenericArrayCreationRewriter | HIR Generator 直接生成 |
| IntStreamBoxedRewriter | HIR Generator 直接生成 |
| ArrayIterableConversionRewriter | HIR Generator 直接生成 |
| CollectStreamRoundtripRewriter | HIR Generator 直接生成 |
| StopwatchApiRewriter | HIR Generator 直接生成 |
| EventHandlerLambdaRewriter | LowerEvent Pass |
| ExceptionApiRewriter | HIR Generator 方法名映射 |
| StringConcatRewriter | HIR Generator 直接生成 |
| VariableNameDeduplicationRewriter | VariableResolution Pass |
| ImplicitCastCompletionRewriter | HIR Generator 直接生成 |
| 3 x ValidationRewriters | Phase 4 保留 |

**核心变化：** 80% 的修补工作在 HIR Generator 阶段直接做对，剩余 C# 语义差异由 Lowering Pass 精确处理。

## 第三部分：HIR Generator 设计

### 架构

```
CSharpToJavaHIRGenerator : IHIRGenerator
    │
    ├── TypeGenerator         (Class/Interface/Enum/Record/Struct → JavaTypeDeclaration)
    │     ├── MemberGenerator (Field/Property/Method/Constructor/Indexer/Event/Operator)
    │     │     └── StatementGenerator
    │     │           └── ExpressionGenerator  ← 所有表达式生成结构化 IR 节点
    │     └── NestedTypeGenerator
    │
    ├── ImportResolver        (using → import，统一处理别名/静态导入/namespace→package)
    │
    └── SymbolResolver        (所有标识符解析的统一入口)
```

### ExpressionGenerator 关键原则

**每条 VisitXxxExpression 方法返回结构化 `JavaExpression` 节点，绝不返回 string。**

```csharp
// 二元运算 — 生成结构化节点
public JavaExpression VisitBinaryExpression(BinaryExpressionSyntax node)
{
    var left = VisitExpression(node.Left);
    var right = VisitExpression(node.Right);
    var op = GetBinaryOperator(node.OperatorToken, node);  // 用语义模型查运算符重载
    return new JavaBinaryExpression(left, op, right);
}
```

### 类型映射嵌入生成过程

`TypeMappingService` 在 ExpressionGenerator 生成节点时直接调用，生成即正确的 Java 类型：

```csharp
// 成员访问时同步完成方法名映射
public JavaExpression VisitMemberAccess(MemberAccessExpressionSyntax node)
{
    var symbol = SemanticModel.GetSymbolInfo(node).Symbol;
    var target = VisitExpression(node.Expression);
    var memberName = MapMemberName(symbol);  // Count → size(), Add → put()
    return new JavaMemberAccessExpression(target, memberName, symbol);
}
```

### 复用现有代码

现有 Transformer 中逻辑正确的部分迁移到对应 Generator 方法：
- `EnumTransformer` 的枚举值生成逻辑
- `FieldTransformer` 的初始化器转换逻辑
- `MethodTransformer` 的签名转换逻辑
- `ConstructorTransformer` 的初始化器链处理

## 第四部分：Lowering Pass 详细设计

### Pass 1: LowerRefOut

- **ref 参数**：调用点包装为 Holder 对象传递，调用后解包
- **out 参数**：声明未初始化变量，传入 Holder，调用后解包
- **readonly ref / in 参数**：降级为值传递（Java 无 const ref）
- 迁移来源：`MethodConversionState` 的 `AllocateOutHolderName` / `SetActiveRefHolder`

### Pass 2: LowerYield

- yield 方法 → 生成迭代器状态机内部类
- `yield return x` → 状态字段赋值 + break
- `yield break` → 状态标记完成 + break
- 迁移来源：`TransformYieldReturn`

### Pass 3: LowerUsing

- `using (var x = expr)` → `try { var x = expr; ... } finally { x.close(); }`
- 迁移来源：`StatementTransformer.SwitchAndResource.cs` 中 using 逻辑

### Pass 4: LowerProperty

- `obj.Property` 读取 → `getProperty()` 方法调用
- `obj.Property = value` → `setProperty(value)` 方法调用
- 自动属性 → 生成私有字段 + getter/setter
- 迁移来源：`PropertyTransformer`

### Pass 5: LowerIndexer

- `obj[index]` → `get(index)` / `set(index, value)`
- 迁移来源：`IndexerTransformer`

### Pass 6: LowerOperator

- `operator +` → 静态方法 `op_Addition`
- `operator ==` → `equals()` + null 检查
- implicit/explicit 转换 → 静态方法调用
- 迁移来源：`OperatorTransformer`

### Pass 7: LowerEvent

- `event EventHandler<T> E` → `List<Consumer<T>>` 字段 + addE/removeE 方法
- `E += handler` → `addE(handler)`
- `E -= handler` → `removeE(handler)`
- `E?.Invoke(args)` → 遍历 listener 列表调用
- 迁移来源：`EventFieldTransformer` / `EventHandlerLambdaRewriter`

### Pass 8: LowerDelegate

- delegate 声明 → `@FunctionalInterface` 接口
- 委托构造 → 匿名类 / lambda
- Invoke → 直接调用
- 迁移来源：`DelegateTransformer` / `DelegateInvocationRewriter`

### Pass 9: LowerStruct

- struct 赋值 / 传参 → `.clone()` 调用
- readonly struct 优化 → 跳过 clone
- ref struct 检测 → 编译错误（Java 无栈分配）
- 迁移来源：`StructCloneHelper` / `StructTransformer`

### Pass 10: LowerPatternMatch

- `x is Type t` → `x instanceof Type t`
- `x is { Prop: value }` → 展开为连续检查
- switch expression → switch 语句
- 新增能力，当前不支持

### Pass 11: LowerAsync (未来)

- async/await → CompletableFuture 链式调用

### Pass 12: VariableResolution

- 变量名去重（解决 C# 合法但 Java 不允许的同名变量）
- 变量引用冲突解决
- 迁移来源：`VariableNameDeduplicationRewriter`

## 第五部分：CodeGen 设计

### 设计原则

- IR 节点不需要知道 Java 语法格式
- CodeGen 层独立负责将 IR 渲染为合法 Java 代码
- 括号优先级、缩进、导包均由 CodeGen 统一处理

### 架构

```
JavaCodeGenerator
    ├── TypeDeclarationWriter     — 类/接口/枚举声明
    ├── MemberWriter              — 字段/方法/构造器
    ├── StatementWriter           — 所有语句类型
    ├── ExpressionWriter          — 所有表达式类型（含运算符优先级表）
    ├── ImportCollector           — 遍历 IR 收集类型引用，决定导入策略
    ├── CommentWriter             — JavaDoc / 行注释 / 块注释
    └── IndentedWriter            — 缩进管理 + 流式写入
```

### 运算符优先级

ExpressionWriter 内置优先级表，子表达式优先级低于父表达式时自动加括号——确定性规则，不依赖 HIR 阶段猜测：

- 12: 乘除取余 (`*`, `/`, `%`)
- 10: 加减 (`+`, `-`)
- 9: 位移 (`<<`, `>>`, `>>>`)
- 8: 比较 (`<`, `>`, `<=`, `>=`, `instanceof`)
- 7: 相等 (`==`, `!=`)
- 6: 位与 (`&`)
- 4: 逻辑与 (`&&`)
- 3: 逻辑或 (`||`)

### 导包管理

ImportCollector 遍历整个 CompilationUnit 的 IR 树，收集所有类型引用：
- 去重（同一类型只导入一次）
- 按 Java 规范排序（java.* → javax.* → 第三方 → 项目内）
- 冲突处理（同名不同包类型 → 使用处换全限定名）
- java.lang.* 隐式可用，不显式导入

### 与现有代码的迁移关系

- 各 `JavaSyntaxNode.ToString(string indentation)` → 迁移到对应 Writer 类
- 分散的 `string.Join("\n", ...)` 拼字符串 → `IndentedWriter` 流式写入
- `JavaCommentEmitter` → `CommentWriter`

## 不变更的部分

以下系统保持现有实现，不在本次重写范围内：

- **TypeMappings.json** 配置体系（`TypeMappingService` / `TypeMappingRegistry`）
- **CLI 入口**和命令行参数处理
- **Cs2jLibrary / Workspace** 抽象层
- **测试基础设施**（XUnit + 测试用例格式）
- **JavaMetadata** 标准库索引

## 迁移策略

建议分阶段实施，而非一次性替换：

1. **Phase A: 新 IR 模型 + CodeGen** — 实现新 IR 节点类和 CodeGen，但不修改现有管道
2. **Phase B: HIR Generator** — 实现新的 HIR Generator，与现有 Visitor 并行运行，对比输出
3. **Phase C: Lowering Pass** — 逐个实现 Lowering Pass，每个 Pass 可独立测试
4. **Phase D: 管道集成** — 用新管道替换旧管道，删除旧 Rewriter
5. **Phase E: 回归验证** — 用现有 20K 测试用例 + MSAGL 实际项目验证
