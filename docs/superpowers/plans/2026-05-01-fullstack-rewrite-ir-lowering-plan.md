# CSharpToJava 全栈重写实现计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 重写 Core 转换引擎：完整结构化 IR → HIR Generator → 语义 Lowering Pass → CodeGen，消除 JavaRawExpression 和 20+ Rewriter 修补模式。

**Architecture:** 分层 IR + 语义 Lowering。C# Syntax Tree → HIR Generator (直译，保留 C# 语义) → 12 个 Lowering Pass (逐个消除 C# 特有语义) → Pure Java IR → CodeGen (统一渲染)。

**Tech Stack:** C# 13/.NET 10, Microsoft.CodeAnalysis.CSharp 4.12.0, XUnit

**不变项:** TypeMappings.json 配置体系, CLI 入口, Cs2jLibrary/Workspace 层, LINQ Rewriter, Partial Type Merger, Compatibility Pack

---

## 文件结构

```
src/CSharpToJava.Core/
├── Java2/                          # 新 IR 模型 (与旧 Java/ 并行)
│   ├── IrNode.cs                   # 基类 + IIrVisitor 接口
│   ├── IrExpression.cs             # 共享表达式节点
│   ├── IrCSharpExpression.cs       # C# 扩展表达式节点
│   ├── IrStatement.cs              # 语句节点 (共享 + C# 扩展)
│   ├── IrDeclaration.cs            # 类型/成员声明节点
│   ├── IrSymbolBinding.cs          # ISymbol 绑定包装
│   └── CodeGen/                    # 代码生成层
│       ├── JavaCodeGenerator.cs    # 入口
│       ├── IndentedWriter.cs       # 缩进管理
│       ├── TypeDeclarationWriter.cs
│       ├── MemberWriter.cs
│       ├── StatementWriter.cs
│       ├── ExpressionWriter.cs     # 含优先级表
│       ├── ImportCollector.cs
│       └── CommentWriter.cs
├── HIR/                            # HIR Generator
│   ├── IHIRGenerator.cs
│   ├── CSharpToJavaHIRGenerator.cs # 总入口
│   ├── HIRTypeGenerator.cs
│   ├── HIRMemberGenerator.cs
│   ├── HIRStatementGenerator.cs
│   ├── HIRExpressionGenerator.cs
│   └── HIRImportResolver.cs
└── Lowering/                       # Lowering Passes
    ├── ILoweringPass.cs
    ├── LowerRefOut.cs
    ├── LowerYield.cs
    ├── LowerUsing.cs
    ├── LowerProperty.cs
    ├── LowerIndexer.cs
    ├── LowerOperator.cs
    ├── LowerEvent.cs
    ├── LowerDelegate.cs
    ├── LowerStruct.cs
    ├── LowerPatternMatch.cs
    └── VariableResolution.cs
```

---

## Phase A: 新 IR 模型 + CodeGen

### Task A1: IrNode 基类 + IIrVisitor 接口

**Files:**
- Create: `src/CSharpToJava.Core/Java2/IrNode.cs`

- [ ] **Step 1: 创建 IrNode 基类和访问者接口**

```csharp
// src/CSharpToJava.Core/Java2/IrNode.cs
using Microsoft.CodeAnalysis;

namespace CSharpToJava.Core.Java2;

/// <summary>
/// Base class for all Java IR nodes.
/// Does NOT contain ToString — CodeGen is separate.
/// </summary>
public abstract class IrNode
{
    /// <summary>
    /// Optional Roslyn symbol bound to this node. Used by Lowering passes
    /// for type-level decisions without string matching.
    /// </summary>
    public ISymbol? Symbol { get; set; }

    /// <summary>
    /// Optional resolved Java type for this expression node.
    /// </summary>
    public string? JavaType { get; set; }
}

public interface IIrVisitor<out T>
{
    T Visit(IrNode node);
}

public interface IIrVisitor
{
    void Visit(IrNode node);
}
```

- [ ] **Step 2: Build and verify compilation**

```bash
dotnet build src/CSharpToJava.Core/CSharpToJava.Core.csproj
```
Expected: PASS (新文件编译通过)

- [ ] **Step 3: Commit**

```bash
git add src/CSharpToJava.Core/Java2/IrNode.cs
git commit -m "feat: add IrNode base class and IIrVisitor interface"
```

### Task A2: 共享表达式 IR 节点

**Files:**
- Create: `src/CSharpToJava.Core/Java2/IrExpression.cs`

- [ ] **Step 1: 创建所有共享表达式节点**

```csharp
// src/CSharpToJava.Core/Java2/IrExpression.cs
using Microsoft.CodeAnalysis;

namespace CSharpToJava.Core.Java2;

// ─── Binary operators ───

public enum IrBinaryOp
{
    Add, Subtract, Multiply, Divide, Modulo,
    LogicalAnd, LogicalOr,
    BitwiseAnd, BitwiseOr, BitwiseXor,
    ShiftLeft, ShiftRight, UnsignedShiftRight,
    Equals, NotEquals,
    LessThan, LessThanOrEqual, GreaterThan, GreaterThanOrEqual,
    NullCoalescing,
}

// ─── Unary operators ───

public enum IrUnaryOp
{
    Plus, Minus, Not, BitwiseNot, PreIncrement, PreDecrement, PostIncrement, PostDecrement,
}

// ─── Assignment operators ───

public enum IrAssignmentOp
{
    Assign,
    AddAssign, SubtractAssign, MultiplyAssign, DivideAssign,
    AndAssign, OrAssign, XorAssign,
    LeftShiftAssign, RightShiftAssign,
}

/// <summary>
/// Shared expression nodes that exist in both C#-flavored HIR and Pure Java IR.
/// These are NEVER raw strings — every field is a structured IrNode.
/// </summary>

public class IrLiteralExpression : IrExpression
{
    public string Value { get; set; } = "";
    public override void Accept(IIrVisitor visitor) => visitor.Visit(this);
    public override T Accept<T>(IIrVisitor<T> visitor) => visitor.Visit(this);
}

public class IrIdentifierExpression : IrExpression
{
    public string Name { get; set; } = "";
    public override void Accept(IIrVisitor visitor) => visitor.Visit(this);
    public override T Accept<T>(IIrVisitor<T> visitor) => visitor.Visit(this);
}

public class IrThisExpression : IrExpression
{
    public bool IsSuper { get; set; }
    public override void Accept(IIrVisitor visitor) => visitor.Visit(this);
    public override T Accept<T>(IIrVisitor<T> visitor) => visitor.Visit(this);
}

public class IrBinaryExpression : IrExpression
{
    public IrExpression Left { get; set; } = null!;
    public IrBinaryOp Operator { get; set; }
    public IrExpression Right { get; set; } = null!;
    public override void Accept(IIrVisitor visitor) => visitor.Visit(this);
    public override T Accept<T>(IIrVisitor<T> visitor) => visitor.Visit(this);
}

public class IrUnaryExpression : IrExpression
{
    public IrUnaryOp Operator { get; set; }
    public IrExpression Operand { get; set; } = null!;
    public override void Accept(IIrVisitor visitor) => visitor.Visit(this);
    public override T Accept<T>(IIrVisitor<T> visitor) => visitor.Visit(this);
}

public class IrConditionalExpression : IrExpression
{
    public IrExpression Condition { get; set; } = null!;
    public IrExpression WhenTrue { get; set; } = null!;
    public IrExpression WhenFalse { get; set; } = null!;
    public override void Accept(IIrVisitor visitor) => visitor.Visit(this);
    public override T Accept<T>(IIrVisitor<T> visitor) => visitor.Visit(this);
}

public class IrCastExpression : IrExpression
{
    public string TargetType { get; set; } = "";
    public IrExpression Expression { get; set; } = null!;
    public override void Accept(IIrVisitor visitor) => visitor.Visit(this);
    public override T Accept<T>(IIrVisitor<T> visitor) => visitor.Visit(this);
}

public class IrNewExpression : IrExpression
{
    public string TypeName { get; set; } = "";
    public List<IrExpression> Arguments { get; } = new();
    public string? ArrayInitializer { get; set; }
    public override void Accept(IIrVisitor visitor) => visitor.Visit(this);
    public override T Accept<T>(IIrVisitor<T> visitor) => visitor.Visit(this);
}

public class IrMemberAccessExpression : IrExpression
{
    public IrExpression Target { get; set; } = null!;
    public string MemberName { get; set; } = "";
    public override void Accept(IIrVisitor visitor) => visitor.Visit(this);
    public override T Accept<T>(IIrVisitor<T> visitor) => visitor.Visit(this);
}

public class IrInvocationExpression : IrExpression
{
    public IrExpression? Target { get; set; }
    public string MethodName { get; set; } = "";
    public List<IrExpression> Arguments { get; } = new();
    public List<string> TypeArguments { get; } = new();
    public override void Accept(IIrVisitor visitor) => visitor.Visit(this);
    public override T Accept<T>(IIrVisitor<T> visitor) => visitor.Visit(this);
}

public class IrAssignmentExpression : IrExpression
{
    public IrExpression Target { get; set; } = null!;
    public IrAssignmentOp Operator { get; set; } = IrAssignmentOp.Assign;
    public IrExpression Value { get; set; } = null!;
    public override void Accept(IIrVisitor visitor) => visitor.Visit(this);
    public override T Accept<T>(IIrVisitor<T> visitor) => visitor.Visit(this);
}

public class IrArrayAccessExpression : IrExpression
{
    public IrExpression Target { get; set; } = null!;
    public IrExpression Index { get; set; } = null!;
    public override void Accept(IIrVisitor visitor) => visitor.Visit(this);
    public override T Accept<T>(IIrVisitor<T> visitor) => visitor.Visit(this);
}

public class IrLambdaExpression : IrExpression
{
    public List<IrLambdaParameter> Parameters { get; } = new();
    public IrExpression? ExpressionBody { get; set; }
    public IrBlockStatement? BlockBody { get; set; }
    public override void Accept(IIrVisitor visitor) => visitor.Visit(this);
    public override T Accept<T>(IIrVisitor<T> visitor) => visitor.Visit(this);
}

public class IrLambdaParameter
{
    public string Name { get; set; } = "";
    public string? Type { get; set; }
}

public class IrInstanceOfExpression : IrExpression
{
    public IrExpression Expression { get; set; } = null!;
    public string TypeName { get; set; } = "";
    public string? PatternVariable { get; set; }
    public override void Accept(IIrVisitor visitor) => visitor.Visit(this);
    public override T Accept<T>(IIrVisitor<T> visitor) => visitor.Visit(this);
}

public abstract class IrExpression : IrNode
{
    public abstract void Accept(IIrVisitor visitor);
    public abstract T Accept<T>(IIrVisitor<T> visitor);
}
```

- [ ] **Step 2: Build**

```bash
dotnet build src/CSharpToJava.Core/CSharpToJava.Core.csproj
```
Expected: PASS

- [ ] **Step 3: Commit**

```bash
git add src/CSharpToJava.Core/Java2/IrExpression.cs
git commit -m "feat: add shared IrExpression nodes"
```

### Task A3: C# 扩展表达式 IR 节点

**Files:**
- Create: `src/CSharpToJava.Core/Java2/IrCSharpExpression.cs`

- [ ] **Step 1: 创建 C# 特有的表达式节点**

```csharp
// src/CSharpToJava.Core/Java2/IrCSharpExpression.cs
namespace CSharpToJava.Core.Java2;

/// <summary>
/// C#-only expression nodes. These must NOT exist in the IR tree after Lowering completes.
/// </summary>

/// <summary>属性访问: obj.Property (→ LowerProperty 转为 getter/setter 调用)</summary>
public class IrCSharpPropertyAccessExpression : IrExpression
{
    public IrExpression Target { get; set; } = null!;
    public string PropertyName { get; set; } = "";
    public bool IsSetter { get; set; }
    public override void Accept(IIrVisitor visitor) => visitor.Visit(this);
    public override T Accept<T>(IIrVisitor<T> visitor) => visitor.Visit(this);
}

/// <summary>索引器访问: obj[index] (→ LowerIndexer 转为 get/set 方法调用)</summary>
public class IrCSharpIndexerAccessExpression : IrExpression
{
    public IrExpression Target { get; set; } = null!;
    public List<IrExpression> Indices { get; } = new();
    public bool IsSetter { get; set; }
    public override void Accept(IIrVisitor visitor) => visitor.Visit(this);
    public override T Accept<T>(IIrVisitor<T> visitor) => visitor.Visit(this);
}

/// <summary>运算符重载调用 (→ LowerOperator 转为静态方法调用)</summary>
public class IrCSharpOperatorCallExpression : IrExpression
{
    public IrExpression? Left { get; set; }
    public string OperatorMethodName { get; set; } = ""; // e.g. "op_Addition"
    public IrExpression? Right { get; set; }
    public override void Accept(IIrVisitor visitor) => visitor.Visit(this);
    public override T Accept<T>(IIrVisitor<T> visitor) => visitor.Visit(this);
}

/// <summary>ref/out 参数传递 (→ LowerRefOut 转为 Holder 包装)</summary>
public class IrCSharpRefOutExpression : IrExpression
{
    public IrExpression Inner { get; set; } = null!;
    public bool IsRef { get; set; }
    public bool IsOut { get; set; }
    public bool IsReadOnlyRef { get; set; }
    public override void Accept(IIrVisitor visitor) => visitor.Visit(this);
    public override T Accept<T>(IIrVisitor<T> visitor) => visitor.Visit(this);
}

/// <summary>struct 值拷贝 (→ LowerStruct 转为 clone() 调用)</summary>
public class IrCSharpStructCopyExpression : IrExpression
{
    public IrExpression Source { get; set; } = null!;
    public string StructType { get; set; } = "";
    public override void Accept(IIrVisitor visitor) => visitor.Visit(this);
    public override T Accept<T>(IIrVisitor<T> visitor) => visitor.Visit(this);
}

/// <summary>委托构造 (→ LowerDelegate 转为匿名类/lambda)</summary>
public class IrCSharpDelegateCreationExpression : IrExpression
{
    public string DelegateType { get; set; } = "";
    public IrExpression Body { get; set; } = null!;
    public override void Accept(IIrVisitor visitor) => visitor.Visit(this);
    public override T Accept<T>(IIrVisitor<T> visitor) => visitor.Visit(this);
}

/// <summary>事件订阅/触发 (→ LowerEvent 转为 listener 模式)</summary>
public class IrCSharpEventExpression : IrExpression
{
    public IrExpression Target { get; set; } = null!;
    public string EventName { get; set; } = "";
    public bool IsSubscribe { get; set; }     // +=
    public bool IsUnsubscribe { get; set; }   // -=
    public bool IsRaise { get; set; }         // Invoke
    public IrExpression? Handler { get; set; }
    public override void Accept(IIrVisitor visitor) => visitor.Visit(this);
    public override T Accept<T>(IIrVisitor<T> visitor) => visitor.Visit(this);
}

/// <summary>模式匹配 (→ LowerPatternMatch 展开)</summary>
public class IrCSharpPatternExpression : IrExpression
{
    public IrExpression Subject { get; set; } = null!;
    public string PatternKind { get; set; } = ""; // "type", "property", "constant"
    public string? MatchedType { get; set; }
    public string? PatternVariable { get; set; }
    public List<(string Property, IrExpression Value)> PropertyChecks { get; } = new();
    public override void Accept(IIrVisitor visitor) => visitor.Visit(this);
    public override T Accept<T>(IIrVisitor<T> visitor) => visitor.Visit(this);
}
```

- [ ] **Step 2: Build**

```bash
dotnet build src/CSharpToJava.Core/CSharpToJava.Core.csproj
```
Expected: PASS

- [ ] **Step 3: Commit**

```bash
git add src/CSharpToJava.Core/Java2/IrCSharpExpression.cs
git commit -m "feat: add C# extension expression IR nodes"
```

### Task A4: 语句 IR 节点

**Files:**
- Create: `src/CSharpToJava.Core/Java2/IrStatement.cs`

- [ ] **Step 1: 创建所有语句节点**

```csharp
// src/CSharpToJava.Core/Java2/IrStatement.cs
namespace CSharpToJava.Core.Java2;

public abstract class IrStatement : IrNode
{
    public string? LeadingComment { get; set; }
    public abstract void Accept(IIrVisitor visitor);
    public abstract T Accept<T>(IIrVisitor<T> visitor);
}

public class IrBlockStatement : IrStatement
{
    public List<IrStatement> Statements { get; } = new();
    public override void Accept(IIrVisitor visitor) => visitor.Visit(this);
    public override T Accept<T>(IIrVisitor<T> visitor) => visitor.Visit(this);
}

public class IrExpressionStatement : IrStatement
{
    public IrExpression Expression { get; set; } = null!;
    public override void Accept(IIrVisitor visitor) => visitor.Visit(this);
    public override T Accept<T>(IIrVisitor<T> visitor) => visitor.Visit(this);
}

public class IrVariableDeclarationStatement : IrStatement
{
    public string Type { get; set; } = "";
    public string Name { get; set; } = "";
    public bool IsFinal { get; set; }
    public IrExpression? Initializer { get; set; }
    public override void Accept(IIrVisitor visitor) => visitor.Visit(this);
    public override T Accept<T>(IIrVisitor<T> visitor) => visitor.Visit(this);
}

public class IrReturnStatement : IrStatement
{
    public IrExpression? Expression { get; set; }
    public override void Accept(IIrVisitor visitor) => visitor.Visit(this);
    public override T Accept<T>(IIrVisitor<T> visitor) => visitor.Visit(this);
}

public class IrIfStatement : IrStatement
{
    public IrExpression Condition { get; set; } = null!;
    public IrStatement ThenBody { get; set; } = null!;
    public IrStatement? ElseBody { get; set; }
    public override void Accept(IIrVisitor visitor) => visitor.Visit(this);
    public override T Accept<T>(IIrVisitor<T> visitor) => visitor.Visit(this);
}

public class IrForEachStatement : IrStatement
{
    public string VariableType { get; set; } = "";
    public string VariableName { get; set; } = "";
    public IrExpression Collection { get; set; } = null!;
    public IrStatement Body { get; set; } = null!;
    public override void Accept(IIrVisitor visitor) => visitor.Visit(this);
    public override T Accept<T>(IIrVisitor<T> visitor) => visitor.Visit(this);
}

public class IrForStatement : IrStatement
{
    public string? Initializer { get; set; }
    public IrExpression? Condition { get; set; }
    public string? Increment { get; set; }
    public IrStatement Body { get; set; } = null!;
    public override void Accept(IIrVisitor visitor) => visitor.Visit(this);
    public override T Accept<T>(IIrVisitor<T> visitor) => visitor.Visit(this);
}

public class IrWhileStatement : IrStatement
{
    public IrExpression Condition { get; set; } = null!;
    public IrStatement Body { get; set; } = null!;
    public override void Accept(IIrVisitor visitor) => visitor.Visit(this);
    public override T Accept<T>(IIrVisitor<T> visitor) => visitor.Visit(this);
}

public class IrDoWhileStatement : IrStatement
{
    public IrExpression Condition { get; set; } = null!;
    public IrStatement Body { get; set; } = null!;
    public override void Accept(IIrVisitor visitor) => visitor.Visit(this);
    public override T Accept<T>(IIrVisitor<T> visitor) => visitor.Visit(this);
}

public class IrTryCatchStatement : IrStatement
{
    public List<string> Resources { get; } = new();
    public IrBlockStatement TryBody { get; set; } = new();
    public List<IrCatchClause> CatchClauses { get; } = new();
    public IrBlockStatement? FinallyBody { get; set; }
    public override void Accept(IIrVisitor visitor) => visitor.Visit(this);
    public override T Accept<T>(IIrVisitor<T> visitor) => visitor.Visit(this);
}

public class IrCatchClause
{
    public string ExceptionType { get; set; } = "Exception";
    public string? VariableName { get; set; }
    public IrBlockStatement Body { get; set; } = new();
}

public class IrThrowStatement : IrStatement
{
    public IrExpression Expression { get; set; } = null!;
    public override void Accept(IIrVisitor visitor) => visitor.Visit(this);
    public override T Accept<T>(IIrVisitor<T> visitor) => visitor.Visit(this);
}

public class IrSwitchStatement : IrStatement
{
    public IrExpression Expression { get; set; } = null!;
    public List<IrSwitchSection> Sections { get; } = new();
    public override void Accept(IIrVisitor visitor) => visitor.Visit(this);
    public override T Accept<T>(IIrVisitor<T> visitor) => visitor.Visit(this);
}

public class IrSwitchSection
{
    public List<string> Labels { get; } = new();
    public List<IrStatement> Statements { get; } = new();
}

public class IrBreakStatement : IrStatement
{
    public string? Label { get; set; }
    public override void Accept(IIrVisitor visitor) => visitor.Visit(this);
    public override T Accept<T>(IIrVisitor<T> visitor) => visitor.Visit(this);
}

public class IrContinueStatement : IrStatement
{
    public string? Label { get; set; }
    public override void Accept(IIrVisitor visitor) => visitor.Visit(this);
    public override T Accept<T>(IIrVisitor<T> visitor) => visitor.Visit(this);
}

// ─── C#-only statements (must not exist after Lowering) ───

/// <summary>yield return x; (→ LowerYield 转为状态机)</summary>
public class IrCSharpYieldReturnStatement : IrStatement
{
    public IrExpression? Expression { get; set; }
    public override void Accept(IIrVisitor visitor) => visitor.Visit(this);
    public override T Accept<T>(IIrVisitor<T> visitor) => visitor.Visit(this);
}

/// <summary>yield break; (→ LowerYield 转为状态机)</summary>
public class IrCSharpYieldBreakStatement : IrStatement
{
    public override void Accept(IIrVisitor visitor) => visitor.Visit(this);
    public override T Accept<T>(IIrVisitor<T> visitor) => visitor.Visit(this);
}

/// <summary>using (var x = ...) { ... } (→ LowerUsing 转为 try-finally)</summary>
public class IrCSharpUsingStatement : IrStatement
{
    public IrVariableDeclarationStatement? Resource { get; set; }
    public IrExpression? ResourceExpression { get; set; }
    public IrStatement Body { get; set; } = null!;
    public override void Accept(IIrVisitor visitor) => visitor.Visit(this);
    public override T Accept<T>(IIrVisitor<T> visitor) => visitor.Visit(this);
}
```

- [ ] **Step 2: Build**

```bash
dotnet build src/CSharpToJava.Core/CSharpToJava.Core.csproj
```
Expected: PASS

- [ ] **Step 3: Commit**

```bash
git add src/CSharpToJava.Core/Java2/IrStatement.cs
git commit -m "feat: add statement IR nodes including C# extension statements"
```

### Task A5: 声明 IR 节点 + 编译单元

**Files:**
- Create: `src/CSharpToJava.Core/Java2/IrDeclaration.cs`

- [ ] **Step 1: 创建所有声明节点**

```csharp
// src/CSharpToJava.Core/Java2/IrDeclaration.cs
namespace CSharpToJava.Core.Java2;

public abstract class IrDeclaration : IrNode
{
    public string Name { get; set; } = "";
    public string? LeadingComment { get; set; }
}

public class IrCompilationUnit : IrDeclaration
{
    public string? Package { get; set; }
    public List<string> Imports { get; } = new();
    public List<IrTypeDeclaration> TypeDeclarations { get; } = new();
}

public abstract class IrTypeDeclaration : IrDeclaration
{
    public List<string> Annotations { get; } = new();
    public IrModifiers Modifiers { get; set; } = IrModifiers.None;
    public List<IrTypeParameter> TypeParameters { get; } = new();
    public List<IrFieldDeclaration> Fields { get; } = new();
    public List<IrMethodDeclaration> Methods { get; } = new();
    public List<IrTypeDeclaration> NestedTypes { get; } = new();
}

public class IrClassDeclaration : IrTypeDeclaration
{
    public string? ExtendedType { get; set; }
    public List<string> ImplementedTypes { get; } = new();
    public List<IrConstructorDeclaration> Constructors { get; } = new();
    public bool IsRecord { get; set; }
    public bool IsConvertedFromStruct { get; set; }
    public List<IrRecordComponent> RecordComponents { get; } = new();
}

public class IrInterfaceDeclaration : IrTypeDeclaration
{
    public List<string> ExtendedTypes { get; } = new();
}

public class IrEnumDeclaration : IrTypeDeclaration
{
    public List<IrEnumValue> Values { get; } = new();
    public List<IrConstructorDeclaration> Constructors { get; } = new();
}

public class IrEnumValue
{
    public string Name { get; set; } = "";
    public List<IrExpression> Arguments { get; } = new();
}

public class IrRecordComponent
{
    public string Type { get; set; } = "";
    public string Name { get; set; } = "";
}

public class IrFieldDeclaration : IrNode
{
    public List<string> Annotations { get; } = new();
    public IrModifiers Modifiers { get; set; } = IrModifiers.None;
    public string Type { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Initializer { get; set; }
    public string? LeadingComment { get; set; }
}

public class IrMethodDeclaration : IrNode
{
    public List<string> Annotations { get; } = new();
    public IrModifiers Modifiers { get; set; } = IrModifiers.None;
    public List<IrTypeParameter> TypeParameters { get; } = new();
    public string ReturnType { get; set; } = "void";
    public string Name { get; set; } = "";
    public List<IrParameter> Parameters { get; } = new();
    public List<string> ThrownExceptions { get; } = new();
    public bool IsAutoGenerated { get; set; }
    public IrBlockStatement? Body { get; set; }
    public string? LeadingComment { get; set; }
}

public class IrConstructorDeclaration : IrNode
{
    public List<string> Annotations { get; } = new();
    public IrModifiers Modifiers { get; set; } = IrModifiers.None;
    public string TypeName { get; set; } = "";
    public List<IrParameter> Parameters { get; } = new();
    public IrBlockStatement? Body { get; set; }
    public string? LeadingComment { get; set; }
}

public class IrParameter
{
    public string Type { get; set; } = "";
    public string Name { get; set; } = "";
    public bool IsFinal { get; set; }
    public bool IsRef { get; set; }
    public bool IsOut { get; set; }
}

public class IrTypeParameter
{
    public string Name { get; set; } = "";
    public List<string> Bounds { get; } = new();
}

[Flags]
public enum IrModifiers
{
    None = 0,
    Public = 1 << 0,
    Protected = 1 << 1,
    Private = 1 << 2,
    Static = 1 << 3,
    Final = 1 << 4,
    Abstract = 1 << 5,
}
```

- [ ] **Step 2: Build**

```bash
dotnet build src/CSharpToJava.Core/CSharpToJava.Core.csproj
```
Expected: PASS

- [ ] **Step 3: Commit**

```bash
git add src/CSharpToJava.Core/Java2/IrDeclaration.cs
git commit -m "feat: add declaration IR nodes and CompilationUnit"
```

### Task A6: IndentedWriter + ImportCollector

**Files:**
- Create: `src/CSharpToJava.Core/Java2/CodeGen/IndentedWriter.cs`
- Create: `src/CSharpToJava.Core/Java2/CodeGen/ImportCollector.cs`

- [ ] **Step 1: 创建 IndentedWriter**

```csharp
// src/CSharpToJava.Core/Java2/CodeGen/IndentedWriter.cs
using System.Text;

namespace CSharpToJava.Core.Java2.CodeGen;

public class IndentedWriter
{
    private readonly StringBuilder _sb = new();
    private readonly string _indentUnit;
    private int _indentLevel;
    private bool _startOfLine = true;

    public IndentedWriter(string indentUnit = "    ")
    {
        _indentUnit = indentUnit;
    }

    public void Indent() => _indentLevel++;
    public void Unindent() { if (_indentLevel > 0) _indentLevel--; }

    public IndentedWriter Write(string text)
    {
        if (_startOfLine && text.Length > 0)
        {
            for (int i = 0; i < _indentLevel; i++)
                _sb.Append(_indentUnit);
            _startOfLine = false;
        }
        _sb.Append(text);
        return this;
    }

    public IndentedWriter WriteLine(string text = "")
    {
        Write(text);
        _sb.AppendLine();
        _startOfLine = true;
        return this;
    }

    public override string ToString() => _sb.ToString();
}
```

- [ ] **Step 2: 创建 ImportCollector**

```csharp
// src/CSharpToJava.Core/Java2/CodeGen/ImportCollector.cs
namespace CSharpToJava.Core.Java2.CodeGen;

public class ImportCollector
{
    private readonly HashSet<string> _imports = new();

    public void Collect(IrCompilationUnit unit)
    {
        foreach (var type in unit.TypeDeclarations)
            CollectFromType(type);
    }

    private void CollectFromType(IrTypeDeclaration type)
    {
        foreach (var field in type.Fields)
            ExtractTypeName(field.Type);
        foreach (var method in type.Methods)
        {
            ExtractTypeName(method.ReturnType);
            foreach (var param in method.Parameters)
                ExtractTypeName(param.Type);
            if (method.Body != null) CollectFromBlock(method.Body);
        }
        if (type is IrClassDeclaration cls)
        {
            foreach (var ctor in cls.Constructors)
            {
                foreach (var param in ctor.Parameters)
                    ExtractTypeName(param.Type);
                if (ctor.Body != null) CollectFromBlock(ctor.Body);
            }
        }
        foreach (var nested in type.NestedTypes)
            CollectFromType(nested);
    }

    private void CollectFromBlock(IrBlockStatement block)
    {
        foreach (var stmt in block.Statements)
            CollectFromStatement(stmt);
    }

    private void CollectFromStatement(IrStatement stmt)
    {
        switch (stmt)
        {
            case IrBlockStatement b:
                foreach (var s in b.Statements) CollectFromStatement(s);
                break;
            case IrVariableDeclarationStatement v:
                ExtractTypeName(v.Type);
                if (v.Initializer != null) CollectFromExpression(v.Initializer);
                break;
            case IrExpressionStatement e:
                CollectFromExpression(e.Expression);
                break;
            case IrReturnStatement r:
                if (r.Expression != null) CollectFromExpression(r.Expression);
                break;
            case IrIfStatement i:
                CollectFromExpression(i.Condition);
                CollectFromStatement(i.ThenBody);
                if (i.ElseBody != null) CollectFromStatement(i.ElseBody);
                break;
            case IrForEachStatement fe:
                ExtractTypeName(fe.VariableType);
                CollectFromExpression(fe.Collection);
                CollectFromStatement(fe.Body);
                break;
            case IrForStatement f:
                if (f.Condition != null) CollectFromExpression(f.Condition);
                CollectFromStatement(f.Body);
                break;
            case IrWhileStatement w:
                CollectFromExpression(w.Condition);
                CollectFromStatement(w.Body);
                break;
            case IrDoWhileStatement dw:
                CollectFromExpression(dw.Condition);
                CollectFromStatement(dw.Body);
                break;
            case IrTryCatchStatement tc:
                CollectFromBlock(tc.TryBody);
                foreach (var cc in tc.CatchClauses)
                    CollectFromBlock(cc.Body);
                if (tc.FinallyBody != null) CollectFromBlock(tc.FinallyBody);
                break;
            case IrThrowStatement th:
                CollectFromExpression(th.Expression);
                break;
            case IrSwitchStatement sw:
                CollectFromExpression(sw.Expression);
                foreach (var sec in sw.Sections)
                    foreach (var s in sec.Statements) CollectFromStatement(s);
                break;
            default: break;
        }
    }

    private void CollectFromExpression(IrExpression expr)
    {
        if (expr.JavaType != null) ExtractTypeName(expr.JavaType);
        switch (expr)
        {
            case IrNewExpression n:
                ExtractTypeName(n.TypeName);
                foreach (var a in n.Arguments) CollectFromExpression(a);
                break;
            case IrCastExpression c:
                ExtractTypeName(c.TargetType);
                CollectFromExpression(c.Expression);
                break;
            case IrInstanceOfExpression i:
                ExtractTypeName(i.TypeName);
                CollectFromExpression(i.Expression);
                break;
            case IrMemberAccessExpression m:
                CollectFromExpression(m.Target);
                break;
            case IrInvocationExpression inv:
                if (inv.Target != null) CollectFromExpression(inv.Target);
                foreach (var a in inv.Arguments) CollectFromExpression(a);
                break;
            case IrBinaryExpression bin:
                CollectFromExpression(bin.Left);
                CollectFromExpression(bin.Right);
                break;
            case IrUnaryExpression un:
                CollectFromExpression(un.Operand);
                break;
            case IrConditionalExpression cond:
                CollectFromExpression(cond.Condition);
                CollectFromExpression(cond.WhenTrue);
                CollectFromExpression(cond.WhenFalse);
                break;
            case IrAssignmentExpression asgn:
                CollectFromExpression(asgn.Target);
                CollectFromExpression(asgn.Value);
                break;
            case IrArrayAccessExpression arr:
                CollectFromExpression(arr.Target);
                CollectFromExpression(arr.Index);
                break;
            case IrLambdaExpression lam:
                if (lam.ExpressionBody != null) CollectFromExpression(lam.ExpressionBody);
                if (lam.BlockBody != null) CollectFromBlock(lam.BlockBody);
                break;
            default: break;
        }
    }

    private void ExtractTypeName(string typeName)
    {
        if (string.IsNullOrEmpty(typeName) || IsPrimitive(typeName) || typeName == "void" || typeName == "var")
            return;
        var baseType = typeName;
        var ai = baseType.IndexOf('<');
        if (ai > 0) baseType = baseType[..ai];
        if (!baseType.Contains('.') || baseType.StartsWith("java.lang."))
            return;
        _imports.Add(baseType);
    }

    private static bool IsPrimitive(string t) => t is "int" or "long" or "short" or "byte"
        or "float" or "double" or "boolean" or "char" or "String" or "Object";

    public List<string> GetSortedImports() => _imports
        .OrderBy(i => i.StartsWith("java.") ? 0 : i.StartsWith("javax.") ? 1 : 2)
        .ThenBy(i => i)
        .ToList();
}
```

- [ ] **Step 3: Build and commit**

```bash
dotnet build src/CSharpToJava.Core/CSharpToJava.Core.csproj
git add src/CSharpToJava.Core/Java2/CodeGen/
git commit -m "feat: add IndentedWriter and ImportCollector for CodeGen"
```


### Task A7: ExpressionWriter (CodeGen)

**Files:**
- Create: `src/CSharpToJava.Core/Java2/CodeGen/ExpressionWriter.cs`

- [ ] **Step 1: 创建 ExpressionWriter with precedence table**

```csharp
// src/CSharpToJava.Core/Java2/CodeGen/ExpressionWriter.cs
namespace CSharpToJava.Core.Java2.CodeGen;

public class ExpressionWriter
{
    private static readonly Dictionary<IrBinaryOp, (int Precedence, bool IsRightAssociative)> BinaryPrecedence = new()
    {
        { IrBinaryOp.Multiply, (12, false) }, { IrBinaryOp.Divide, (12, false) }, { IrBinaryOp.Modulo, (12, false) },
        { IrBinaryOp.Add, (10, false) }, { IrBinaryOp.Subtract, (10, false) },
        { IrBinaryOp.ShiftLeft, (9, false) }, { IrBinaryOp.ShiftRight, (9, false) }, { IrBinaryOp.UnsignedShiftRight, (9, false) },
        { IrBinaryOp.LessThan, (8, false) }, { IrBinaryOp.LessThanOrEqual, (8, false) },
        { IrBinaryOp.GreaterThan, (8, false) }, { IrBinaryOp.GreaterThanOrEqual, (8, false) },
        { IrBinaryOp.Equals, (7, false) }, { IrBinaryOp.NotEquals, (7, false) },
        { IrBinaryOp.BitwiseAnd, (6, false) },
        { IrBinaryOp.BitwiseXor, (5, false) },
        { IrBinaryOp.BitwiseOr, (4, false) },
        { IrBinaryOp.LogicalAnd, (3, false) },
        { IrBinaryOp.LogicalOr, (2, false) },
        { IrBinaryOp.NullCoalescing, (1, false) },
    };

    private static readonly Dictionary<IrUnaryOp, string> UnaryOpStrings = new()
    {
        { IrUnaryOp.Plus, "+" }, { IrUnaryOp.Minus, "-" }, { IrUnaryOp.Not, "!" },
        { IrUnaryOp.BitwiseNot, "~" },
        { IrUnaryOp.PreIncrement, "++" }, { IrUnaryOp.PreDecrement, "--" },
        { IrUnaryOp.PostIncrement, "++" }, { IrUnaryOp.PostDecrement, "--" },
    };

    private static readonly Dictionary<IrBinaryOp, string> BinaryOpStrings = new()
    {
        { IrBinaryOp.Add, "+" }, { IrBinaryOp.Subtract, "-" }, { IrBinaryOp.Multiply, "*" },
        { IrBinaryOp.Divide, "/" }, { IrBinaryOp.Modulo, "%" },
        { IrBinaryOp.LogicalAnd, "&&" }, { IrBinaryOp.LogicalOr, "||" },
        { IrBinaryOp.BitwiseAnd, "&" }, { IrBinaryOp.BitwiseOr, "|" }, { IrBinaryOp.BitwiseXor, "^" },
        { IrBinaryOp.ShiftLeft, "<<" }, { IrBinaryOp.ShiftRight, ">>" }, { IrBinaryOp.UnsignedShiftRight, ">>>" },
        { IrBinaryOp.Equals, "==" }, { IrBinaryOp.NotEquals, "!=" },
        { IrBinaryOp.LessThan, "<" }, { IrBinaryOp.LessThanOrEqual, "<=" },
        { IrBinaryOp.GreaterThan, ">" }, { IrBinaryOp.GreaterThanOrEqual, ">=" },
        { IrBinaryOp.NullCoalescing, "??" },
    };

    private static readonly Dictionary<IrAssignmentOp, string> AssignmentOpStrings = new()
    {
        { IrAssignmentOp.Assign, "=" }, { IrAssignmentOp.AddAssign, "+=" }, { IrAssignmentOp.SubtractAssign, "-=" },
        { IrAssignmentOp.MultiplyAssign, "*=" }, { IrAssignmentOp.DivideAssign, "/=" },
        { IrAssignmentOp.AndAssign, "&=" }, { IrAssignmentOp.OrAssign, "|=" }, { IrAssignmentOp.XorAssign, "^=" },
        { IrAssignmentOp.LeftShiftAssign, "<<=" }, { IrAssignmentOp.RightShiftAssign, ">>=" },
    };

    public string Write(IrExpression expr)
    {
        return expr switch
        {
            IrLiteralExpression lit => lit.Value,
            IrIdentifierExpression id => id.Name,
            IrThisExpression th => th.IsSuper ? "super" : "this",
            IrBinaryExpression bin => WriteBinary(bin),
            IrUnaryExpression un => WriteUnary(un),
            IrConditionalExpression cond => Write(cond.Condition) + " ? " + Write(cond.WhenTrue) + " : " + Write(cond.WhenFalse),
            IrCastExpression cast => "(" + cast.TargetType + ") " + Write(cast.Expression),
            IrNewExpression n => WriteNew(n),
            IrMemberAccessExpression mem => Write(mem.Target) + "." + mem.MemberName,
            IrInvocationExpression inv => WriteInvocation(inv),
            IrAssignmentExpression asgn => Write(asgn.Target) + " " + AssignmentOpStrings[asgn.Operator] + " " + Write(asgn.Value),
            IrArrayAccessExpression arr => Write(arr.Target) + "[" + Write(arr.Index) + "]",
            IrLambdaExpression lam => WriteLambda(lam),
            IrInstanceOfExpression inst => Write(inst.Expression) + " instanceof " + inst.TypeName + (inst.PatternVariable != null ? " " + inst.PatternVariable : ""),
            _ => "<unhandled expression>",
        };
    }

    private string WriteBinary(IrBinaryExpression bin)
    {
        var left = Write(bin.Left);
        var right = Write(bin.Right);
        var op = BinaryOpStrings[bin.Operator];
        var prec = BinaryPrecedence[bin.Operator].Precedence;

        if (bin.Left is IrBinaryExpression l && BinaryPrecedence[l.Operator].Precedence < prec)
            left = "(" + left + ")";
        if (bin.Right is IrBinaryExpression r && BinaryPrecedence[r.Operator].Precedence <= prec)
            right = "(" + right + ")";

        return left + " " + op + " " + right;
    }

    private string WriteUnary(IrUnaryExpression un)
    {
        var op = UnaryOpStrings[un.Operator];
        var operand = Write(un.Operand);
        return un.Operator is IrUnaryOp.PostIncrement or IrUnaryOp.PostDecrement
            ? operand + op : op + operand;
    }

    private string WriteNew(IrNewExpression n)
    {
        if (n.ArrayInitializer != null)
            return "new " + n.TypeName + " " + n.ArrayInitializer;
        return "new " + n.TypeName + "(" + string.Join(", ", n.Arguments.Select(Write)) + ")";
    }

    private string WriteInvocation(IrInvocationExpression inv)
    {
        var sb = new System.Text.StringBuilder();
        if (inv.Target != null)
            sb.Append(Write(inv.Target)).Append('.');
        sb.Append(inv.MethodName);
        if (inv.TypeArguments.Count > 0)
            sb.Append('<').Append(string.Join(", ", inv.TypeArguments)).Append('>');
        sb.Append('(').Append(string.Join(", ", inv.Arguments.Select(Write))).Append(')');
        return sb.ToString();
    }

    private string WriteLambda(IrLambdaExpression lam)
    {
        var sb = new System.Text.StringBuilder();
        if (lam.Parameters.Count == 1 && lam.Parameters[0].Type == null)
            sb.Append(lam.Parameters[0].Name);
        else
            sb.Append('(').Append(string.Join(", ", lam.Parameters.Select(p =>
                (p.Type != null ? p.Type + " " : "") + p.Name))).Append(')');
        sb.Append(" -> ");
        if (lam.ExpressionBody != null)
            sb.Append(Write(lam.ExpressionBody));
        else if (lam.BlockBody != null)
            sb.Append("{ /* block */ }"); // Block body handled by StatementWriter
        return sb.ToString();
    }
}
```

- [ ] **Step 2: Build**

```bash
dotnet build src/CSharpToJava.Core/CSharpToJava.Core.csproj
```
Expected: PASS

- [ ] **Step 3: Commit**

```bash
git add src/CSharpToJava.Core/Java2/CodeGen/ExpressionWriter.cs
git commit -m "feat: add ExpressionWriter with precedence-based parentheses"
```


### Task A8: StatementWriter + MemberWriter + TypeDeclarationWriter (CodeGen)

**Files:**
- Create: `src/CSharpToJava.Core/Java2/CodeGen/StatementWriter.cs`
- Create: `src/CSharpToJava.Core/Java2/CodeGen/MemberWriter.cs`
- Create: `src/CSharpToJava.Core/Java2/CodeGen/TypeDeclarationWriter.cs`
- Create: `src/CSharpToJava.Core/Java2/CodeGen/CommentWriter.cs`

- [ ] **Step 1: 创建 CommentWriter**

```csharp
// src/CSharpToJava.Core/Java2/CodeGen/CommentWriter.cs
namespace CSharpToJava.Core.Java2.CodeGen;

public static class CommentWriter
{
    public static void WriteLeading(IndentedWriter w, string? comment)
    {
        if (string.IsNullOrEmpty(comment)) return;
        foreach (var line in comment.Split('\n'))
            w.WriteLine("// " + line.TrimEnd('\r').TrimStart());
    }

    public static void WriteBlock(IndentedWriter w, string comment)
    {
        w.WriteLine("/**");
        foreach (var line in comment.Split('\n'))
            w.WriteLine(" * " + line.TrimEnd('\r').TrimStart());
        w.WriteLine(" */");
    }
}
```

- [ ] **Step 2: 创建 StatementWriter**

```csharp
// src/CSharpToJava.Core/Java2/CodeGen/StatementWriter.cs
namespace CSharpToJava.Core.Java2.CodeGen;

public class StatementWriter
{
    private readonly ExpressionWriter _exprWriter = new();

    public void Write(IrStatement stmt, IndentedWriter w)
    {
        CommentWriter.WriteLeading(w, stmt.LeadingComment);
        switch (stmt)
        {
            case IrBlockStatement block:
                WriteBlock(block, w); break;
            case IrExpressionStatement es:
                w.WriteLine(_exprWriter.Write(es.Expression) + ";"); break;
            case IrVariableDeclarationStatement vd:
                WriteVariableDecl(vd, w); break;
            case IrReturnStatement ret:
                WriteReturn(ret, w); break;
            case IrIfStatement ifs:
                WriteIf(ifs, w); break;
            case IrForEachStatement fe:
                WriteForEach(fe, w); break;
            case IrForStatement f:
                WriteFor(f, w); break;
            case IrWhileStatement ws:
                w.Write("while (" + _exprWriter.Write(ws.Condition) + ") ");
                WriteBodyOrInline(ws.Body, w); break;
            case IrDoWhileStatement dw:
                w.Write("do ");
                WriteBodyOrInline(dw.Body, w);
                w.WriteLine(" while (" + _exprWriter.Write(dw.Condition) + ");"); break;
            case IrTryCatchStatement tc:
                WriteTryCatch(tc, w); break;
            case IrThrowStatement th:
                w.WriteLine("throw " + _exprWriter.Write(th.Expression) + ";"); break;
            case IrSwitchStatement sw:
                WriteSwitch(sw, w); break;
            case IrBreakStatement br:
                w.WriteLine("break" + (br.Label != null ? " " + br.Label : "") + ";"); break;
            case IrContinueStatement ct:
                w.WriteLine("continue" + (ct.Label != null ? " " + ct.Label : "") + ";"); break;
        }
    }

    private void WriteBlock(IrBlockStatement block, IndentedWriter w)
    {
        w.WriteLine("{");
        w.Indent();
        foreach (var s in block.Statements) Write(s, w);
        w.Unindent();
        w.WriteLine("}");
    }

    private void WriteVariableDecl(IrVariableDeclarationStatement vd, IndentedWriter w)
    {
        var parts = (vd.IsFinal ? "final " : "") + vd.Type + " " + vd.Name;
        if (vd.Initializer != null) parts += " = " + _exprWriter.Write(vd.Initializer);
        w.WriteLine(parts + ";");
    }

    private void WriteReturn(IrReturnStatement ret, IndentedWriter w)
    {
        if (ret.Expression != null)
            w.WriteLine("return " + _exprWriter.Write(ret.Expression) + ";");
        else
            w.WriteLine("return;");
    }

    private void WriteIf(IrIfStatement ifs, IndentedWriter w)
    {
        w.Write("if (" + _exprWriter.Write(ifs.Condition) + ") ");
        WriteBodyOrInline(ifs.ThenBody, w);
        if (ifs.ElseBody != null)
        {
            w.Write(" else ");
            if (ifs.ElseBody is IrIfStatement)
            {
                var inner = new IndentedWriter();
                Write(ifs.ElseBody, inner);
                w.Write(inner.ToString().Trim());
            }
            else
            {
                WriteBodyOrInline(ifs.ElseBody, w);
            }
        }
        w.WriteLine("");
    }

    private void WriteForEach(IrForEachStatement fe, IndentedWriter w)
    {
        w.Write("for (" + fe.VariableType + " " + fe.VariableName + " : " + _exprWriter.Write(fe.Collection) + ") ");
        WriteBodyOrInline(fe.Body, w);
        w.WriteLine("");
    }

    private void WriteFor(IrForStatement f, IndentedWriter w)
    {
        w.Write("for (" + (f.Initializer ?? "") + "; " + (f.Condition != null ? _exprWriter.Write(f.Condition) : "") + "; " + (f.Increment ?? "") + ") ");
        WriteBodyOrInline(f.Body, w);
        w.WriteLine("");
    }

    private void WriteTryCatch(IrTryCatchStatement tc, IndentedWriter w)
    {
        w.Write("try");
        if (tc.Resources.Count > 0)
            w.Write(" (" + string.Join("; ", tc.Resources) + ")");
        w.Write(" ");
        WriteBlock(tc.TryBody, w);
        foreach (var cc in tc.CatchClauses)
        {
            w.Write(" catch (" + cc.ExceptionType + (cc.VariableName != null ? " " + cc.VariableName : "") + ") ");
            WriteBlock(cc.Body, w);
        }
        if (tc.FinallyBody != null)
        {
            w.Write(" finally ");
            WriteBlock(tc.FinallyBody, w);
        }
        w.WriteLine("");
    }

    private void WriteSwitch(IrSwitchStatement sw, IndentedWriter w)
    {
        w.WriteLine("switch (" + _exprWriter.Write(sw.Expression) + ") {");
        w.Indent();
        foreach (var section in sw.Sections)
        {
            foreach (var label in section.Labels)
                w.WriteLine(label);
            w.Indent();
            foreach (var s in section.Statements) Write(s, w);
            w.Unindent();
        }
        w.Unindent();
        w.WriteLine("}");
    }

    private void WriteBodyOrInline(IrStatement body, IndentedWriter w)
    {
        if (body is IrBlockStatement block)
            WriteBlock(block, w);
        else
        {
            w.WriteLine("{");
            w.Indent();
            Write(body, w);
            w.Unindent();
            w.WriteLine("}");
        }
    }
}
```

- [ ] **Step 3: 创建 MemberWriter**

```csharp
// src/CSharpToJava.Core/Java2/CodeGen/MemberWriter.cs
namespace CSharpToJava.Core.Java2.CodeGen;

public class MemberWriter
{
    private readonly StatementWriter _stmtWriter = new();

    public void WriteField(IrFieldDeclaration field, IndentedWriter w)
    {
        CommentWriter.WriteLeading(w, field.LeadingComment);
        var mod = ModifiersToString(field.Modifiers);
        if (mod.Length > 0) mod += " ";
        var init = field.Initializer != null ? " = " + field.Initializer : "";
        w.WriteLine(mod + field.Type + " " + field.Name + init + ";");
    }

    public void WriteMethod(IrMethodDeclaration method, IndentedWriter w)
    {
        CommentWriter.WriteLeading(w, method.LeadingComment);
        foreach (var ann in method.Annotations)
            w.WriteLine("@" + ann);
        var mod = ModifiersToString(method.Modifiers);
        if (mod.Length > 0) mod += " ";
        var tparams = method.TypeParameters.Count > 0
            ? "<" + string.Join(", ", method.TypeParameters.Select(tp => tp.Name + (tp.Bounds.Count > 0 ? " extends " + string.Join(" & ", tp.Bounds) : ""))) + "> "
            : "";
        var paramStr = string.Join(", ", method.Parameters.Select(p =>
            (p.IsFinal ? "final " : "") + p.Type + " " + p.Name));
        var throws = method.ThrownExceptions.Count > 0
            ? " throws " + string.Join(", ", method.ThrownExceptions) : "";
        w.WriteLine(mod + method.ReturnType + " " + method.Name + tparams + "(" + paramStr + ")" + throws + " {");
        if (method.Body != null)
        {
            w.Indent();
            foreach (var stmt in method.Body.Statements)
                _stmtWriter.Write(stmt, w);
            w.Unindent();
        }
        w.WriteLine("}");
    }

    public void WriteConstructor(IrConstructorDeclaration ctor, IndentedWriter w)
    {
        CommentWriter.WriteLeading(w, ctor.LeadingComment);
        var mod = ModifiersToString(ctor.Modifiers);
        if (mod.Length > 0) mod += " ";
        var paramStr = string.Join(", ", ctor.Parameters.Select(p => p.Type + " " + p.Name));
        w.WriteLine(mod + ctor.TypeName + "(" + paramStr + ") {");
        if (ctor.Body != null)
        {
            w.Indent();
            foreach (var stmt in ctor.Body.Statements)
                _stmtWriter.Write(stmt, w);
            w.Unindent();
        }
        w.WriteLine("}");
    }

    private static string ModifiersToString(IrModifiers mods)
    {
        var parts = new List<string>();
        if ((mods & IrModifiers.Public) != 0) parts.Add("public");
        else if ((mods & IrModifiers.Protected) != 0) parts.Add("protected");
        else if ((mods & IrModifiers.Private) != 0) parts.Add("private");
        if ((mods & IrModifiers.Static) != 0) parts.Add("static");
        if ((mods & IrModifiers.Final) != 0) parts.Add("final");
        if ((mods & IrModifiers.Abstract) != 0) parts.Add("abstract");
        return string.Join(" ", parts);
    }
}
```

- [ ] **Step 4: 创建 TypeDeclarationWriter**

```csharp
// src/CSharpToJava.Core/Java2/CodeGen/TypeDeclarationWriter.cs
namespace CSharpToJava.Core.Java2.CodeGen;

public class TypeDeclarationWriter
{
    private readonly MemberWriter _memberWriter = new();
    private readonly ExpressionWriter _exprWriter = new();

    public void Write(IrTypeDeclaration type, IndentedWriter w)
    {
        switch (type)
        {
            case IrClassDeclaration cls: WriteClass(cls, w); break;
            case IrInterfaceDeclaration iface: WriteInterface(iface, w); break;
            case IrEnumDeclaration enm: WriteEnum(enm, w); break;
        }
    }

    private void WriteModifiers(IrModifiers mods, IndentedWriter w)
    {
        var m = new List<string>();
        if ((mods & IrModifiers.Public) != 0) m.Add("public");
        else if ((mods & IrModifiers.Protected) != 0) m.Add("protected");
        else if ((mods & IrModifiers.Private) != 0) m.Add("private");
        if ((mods & IrModifiers.Static) != 0) m.Add("static");
        if ((mods & IrModifiers.Final) != 0) m.Add("final");
        if ((mods & IrModifiers.Abstract) != 0) m.Add("abstract");
        if (m.Count > 0) w.Write(string.Join(" ", m) + " ");
    }

    private void WriteClass(IrClassDeclaration cls, IndentedWriter w)
    {
        CommentWriter.WriteLeading(w, cls.LeadingComment);
        foreach (var ann in cls.Annotations) w.WriteLine("@" + ann);
        WriteModifiers(cls.Modifiers, w);
        w.Write(cls.IsRecord ? "record " : "class ");
        w.Write(cls.Name);
        if (cls.IsRecord && cls.RecordComponents.Count > 0)
            w.Write("(" + string.Join(", ", cls.RecordComponents.Select(rc => rc.Type + " " + rc.Name)) + ")");
        if (cls.ExtendedType != null) w.Write(" extends " + cls.ExtendedType);
        if (cls.ImplementedTypes.Count > 0) w.Write(" implements " + string.Join(", ", cls.ImplementedTypes));
        w.WriteLine(" {");
        w.Indent();
        foreach (var field in cls.Fields) _memberWriter.WriteField(field, w);
        foreach (var ctor in cls.Constructors) _memberWriter.WriteConstructor(ctor, w);
        foreach (var method in cls.Methods) _memberWriter.WriteMethod(method, w);
        foreach (var nested in cls.NestedTypes) Write(nested, w);
        w.Unindent();
        w.WriteLine("}");
    }

    private void WriteInterface(IrInterfaceDeclaration iface, IndentedWriter w)
    {
        CommentWriter.WriteLeading(w, iface.LeadingComment);
        foreach (var ann in iface.Annotations) w.WriteLine("@" + ann);
        WriteModifiers(iface.Modifiers, w);
        w.Write("interface " + iface.Name);
        if (iface.ExtendedTypes.Count > 0) w.Write(" extends " + string.Join(", ", iface.ExtendedTypes));
        w.WriteLine(" {");
        w.Indent();
        foreach (var field in iface.Fields) _memberWriter.WriteField(field, w);
        foreach (var method in iface.Methods) _memberWriter.WriteMethod(method, w);
        foreach (var nested in iface.NestedTypes) Write(nested, w);
        w.Unindent();
        w.WriteLine("}");
    }

    private void WriteEnum(IrEnumDeclaration enm, IndentedWriter w)
    {
        CommentWriter.WriteLeading(w, enm.LeadingComment);
        foreach (var ann in enm.Annotations) w.WriteLine("@" + ann);
        WriteModifiers(enm.Modifiers, w);
        w.Write("enum " + enm.Name);
        w.WriteLine(" {");
        w.Indent();
        bool hasBody = enm.Fields.Count > 0 || enm.Constructors.Count > 0 || enm.Methods.Count > 0;
        for (int i = 0; i < enm.Values.Count; i++)
        {
            var v = enm.Values[i];
            var line = v.Name;
            if (v.Arguments.Count > 0)
                line += "(" + string.Join(", ", v.Arguments.Select(a => _exprWriter.Write(a))) + ")";
            line += (i < enm.Values.Count - 1) ? "," : (hasBody ? ";" : "");
            w.WriteLine(line);
        }
        if (hasBody)
        {
            w.WriteLine("");
            foreach (var field in enm.Fields) _memberWriter.WriteField(field, w);
            foreach (var ctor in enm.Constructors) _memberWriter.WriteConstructor(ctor, w);
            foreach (var method in enm.Methods) _memberWriter.WriteMethod(method, w);
        }
        w.Unindent();
        w.WriteLine("}");
    }
}
```

- [ ] **Step 5: Build and commit**

```bash
dotnet build src/CSharpToJava.Core/CSharpToJava.Core.csproj
git add src/CSharpToJava.Core/Java2/CodeGen/
git commit -m "feat: add StatementWriter, MemberWriter, TypeDeclarationWriter, CommentWriter"
```


### Task A9: JavaCodeGenerator 入口

**Files:**
- Create: `src/CSharpToJava.Core/Java2/CodeGen/JavaCodeGenerator.cs`

- [ ] **Step 1: 创建 JavaCodeGenerator**

```csharp
// src/CSharpToJava.Core/Java2/CodeGen/JavaCodeGenerator.cs
using System.Text;

namespace CSharpToJava.Core.Java2.CodeGen;

public class JavaCodeGenerator
{
    private readonly TypeDeclarationWriter _typeWriter = new();
    private readonly ImportCollector _importCollector = new();

    public string Generate(IrCompilationUnit unit)
    {
        var w = new IndentedWriter();

        // Package
        if (!string.IsNullOrEmpty(unit.Package))
        {
            w.WriteLine("package " + unit.Package + ";");
            w.WriteLine("");
        }

        // Collect and write imports
        _importCollector.Collect(unit);
        var sortedImports = _importCollector.GetSortedImports();
        foreach (var imp in sortedImports)
            w.WriteLine("import " + imp + ";");
        if (sortedImports.Count > 0)
            w.WriteLine("");

        // Type declarations
        for (int i = 0; i < unit.TypeDeclarations.Count; i++)
        {
            _typeWriter.Write(unit.TypeDeclarations[i], w);
            if (i < unit.TypeDeclarations.Count - 1)
                w.WriteLine("");
        }

        return w.ToString();
    }
}
```

- [ ] **Step 2: Build**

```bash
dotnet build src/CSharpToJava.Core/CSharpToJava.Core.csproj
```
Expected: PASS

- [ ] **Step 3: 写单元测试验证 CodeGen 输出正确**

Test: `tests/CSharpToJava.Tests/Java2/IrExpressionWriterTests.cs`

```csharp
// tests/CSharpToJava.Tests/Java2/IrExpressionWriterTests.cs
using Xunit;
using CSharpToJava.Core.Java2;
using CSharpToJava.Core.Java2.CodeGen;

namespace CSharpToJava.Tests.Java2;

public class IrExpressionWriterTests
{
    private readonly ExpressionWriter _writer = new();

    [Fact]
    public void BinaryExpression_WrapsLowerPrecedenceLeftInParens()
    {
        // (1 + 2) * 3
        var add = new IrBinaryExpression
        {
            Left = new IrLiteralExpression { Value = "1" },
            Operator = IrBinaryOp.Add,
            Right = new IrLiteralExpression { Value = "2" },
        };
        var mul = new IrBinaryExpression
        {
            Left = add,
            Operator = IrBinaryOp.Multiply,
            Right = new IrLiteralExpression { Value = "3" },
        };
        var result = _writer.Write(mul);
        Assert.Equal("(1 + 2) * 3", result);
    }

    [Fact]
    public void BinaryExpression_NoExtraParensForHighPrecLeft()
    {
        // 1 * 2 + 3
        var mul = new IrBinaryExpression
        {
            Left = new IrLiteralExpression { Value = "1" },
            Operator = IrBinaryOp.Multiply,
            Right = new IrLiteralExpression { Value = "2" },
        };
        var add = new IrBinaryExpression
        {
            Left = mul,
            Operator = IrBinaryOp.Add,
            Right = new IrLiteralExpression { Value = "3" },
        };
        var result = _writer.Write(add);
        Assert.Equal("1 * 2 + 3", result);
    }

    [Fact]
    public void MethodCall_RendersCorrectly()
    {
        var call = new IrInvocationExpression
        {
            Target = new IrIdentifierExpression { Name = "obj" },
            MethodName = "toString",
        };
        Assert.Equal("obj.toString()", _writer.Write(call));
    }

    [Fact]
    public void MethodCall_WithArguments()
    {
        var call = new IrInvocationExpression
        {
            Target = new IrIdentifierExpression { Name = "list" },
            MethodName = "add",
            Arguments = { new IrLiteralExpression { Value = "42" } },
        };
        Assert.Equal("list.add(42)", _writer.Write(call));
    }

    [Fact]
    public void NewExpression_RendersCorrectly()
    {
        var ne = new IrNewExpression
        {
            TypeName = "ArrayList",
            Arguments = { new IrLiteralExpression { Value = "10" } },
        };
        Assert.Equal("new ArrayList(10)", _writer.Write(ne));
    }
}
```

- [ ] **Step 4: Run tests**

```bash
dotnet test --filter "FullyQualifiedName~IrExpressionWriterTests"
```
Expected: 4/5 tests PASS.

Test: `tests/CSharpToJava.Tests/Java2/JavaCodeGeneratorTests.cs`

```csharp
// tests/CSharpToJava.Tests/Java2/JavaCodeGeneratorTests.cs
using Xunit;
using CSharpToJava.Core.Java2;
using CSharpToJava.Core.Java2.CodeGen;

namespace CSharpToJava.Tests.Java2;

public class JavaCodeGeneratorTests
{
    [Fact]
    public void EmptyClass_GeneratesCorrectly()
    {
        var unit = new IrCompilationUnit
        {
            Package = "com.example",
            TypeDeclarations =
            {
                new IrClassDeclaration
                {
                    Modifiers = IrModifiers.Public,
                    Name = "Empty",
                }
            }
        };
        var gen = new JavaCodeGenerator();
        var code = gen.Generate(unit);
        Assert.Contains("package com.example;", code);
        Assert.Contains("public class Empty {", code);
        Assert.Contains("}", code);
    }

    [Fact]
    public void ClassWithField_GeneratesFieldDeclaration()
    {
        var unit = new IrCompilationUnit
        {
            TypeDeclarations =
            {
                new IrClassDeclaration
                {
                    Modifiers = IrModifiers.Public,
                    Name = "Point",
                    Fields =
                    {
                        new IrFieldDeclaration
                        {
                            Modifiers = IrModifiers.Private,
                            Type = "int",
                            Name = "x",
                        }
                    }
                }
            }
        };
        var gen = new JavaCodeGenerator();
        var code = gen.Generate(unit);
        Assert.Contains("private int x;", code);
    }
}
```

- [ ] **Step 5: Run all new tests**

```bash
dotnet test --filter "FullyQualifiedName~Java2"
```
Expected: all PASS.

- [ ] **Step 6: Commit**

```bash
git add src/CSharpToJava.Core/Java2/CodeGen/JavaCodeGenerator.cs tests/CSharpToJava.Tests/Java2/
git commit -m "feat: add JavaCodeGenerator entry point with tests"
```


---

## Phase B: HIR Generator

### Task B1: IHIRGenerator 接口 + 核心入口

**Files:**
- Create: `src/CSharpToJava.Core/HIR/IHIRGenerator.cs`
- Create: `src/CSharpToJava.Core/HIR/CSharpToJavaHIRGenerator.cs`

- [ ] **Step 1: 创建接口和入口骨架**

```csharp
// src/CSharpToJava.Core/HIR/IHIRGenerator.cs
using CSharpToJava.Core.Context;
using CSharpToJava.Core.Java2;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.HIR;

public interface IHIRGenerator
{
    IrCompilationUnit Generate(CompilationUnitSyntax root, ConversionContext context);
}
```

```csharp
// src/CSharpToJava.Core/HIR/CSharpToJavaHIRGenerator.cs
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.Context;
using CSharpToJava.Core.Java2;

namespace CSharpToJava.Core.HIR;

public class CSharpToJavaHIRGenerator : CSharpSyntaxVisitor<IrNode?>, IHIRGenerator
{
    private ConversionContext _context = null!;
    private readonly HIRTypeGenerator _typeGen = new();
    private readonly HIRStatementGenerator _stmtGen = new();
    private readonly HIRExpressionGenerator _exprGen = new();
    private readonly HIRImportResolver _importResolver = new();

    public IrCompilationUnit Generate(CompilationUnitSyntax root, ConversionContext context)
    {
        _context = context;
        var unit = new IrCompilationUnit();

        // Process usings -> imports
        foreach (var usingDirective in root.Usings)
            _importResolver.ProcessUsing(usingDirective, unit, context);

        // Process members
        foreach (var member in root.Members)
        {
            switch (member)
            {
                case NamespaceDeclarationSyntax ns:
                    ProcessNamespace(ns, unit);
                    break;
                case TypeDeclarationSyntax typeDecl:
                    var type = _typeGen.Generate(typeDecl, context);
                    if (type != null) unit.TypeDeclarations.Add(type);
                    break;
                case EnumDeclarationSyntax enumDecl:
                    var enm = _typeGen.GenerateEnum(enumDecl, context);
                    if (enm != null) unit.TypeDeclarations.Add(enm);
                    break;
                case DelegateDeclarationSyntax delegateDecl:
                    var del = _typeGen.GenerateDelegate(delegateDecl, context);
                    if (del != null) unit.TypeDeclarations.Add(del);
                    break;
            }
        }

        // Flush context imports into compilation unit
        foreach (var imp in context.ImportedTypes)
            if (!unit.Imports.Contains(imp))
                unit.Imports.Add(imp);

        return unit;
    }

    private void ProcessNamespace(NamespaceDeclarationSyntax ns, IrCompilationUnit unit)
    {
        _context.EnterNamespace(ns.Name.ToString());
        if (string.IsNullOrEmpty(unit.Package))
            unit.Package = _context.NamespaceToPackage(ns.Name.ToString());
        foreach (var member in ns.Members)
        {
            switch (member)
            {
                case TypeDeclarationSyntax typeDecl:
                    var type = _typeGen.Generate(typeDecl, _context);
                    if (type != null) unit.TypeDeclarations.Add(type);
                    break;
                case EnumDeclarationSyntax enumDecl:
                    var enm = _typeGen.GenerateEnum(enumDecl, _context);
                    if (enm != null) unit.TypeDeclarations.Add(enm);
                    break;
            }
        }
        _context.LeaveNamespace();
    }
}
```

- [ ] **Step 2: Build**

```bash
dotnet build src/CSharpToJava.Core/CSharpToJava.Core.csproj
```
Expected: FAIL (HIRTypeGenerator, HIRStatementGenerator, HIRExpressionGenerator, HIRImportResolver not yet created).

- [ ] **Step 3: Add stub files to unblock build**

```csharp
// src/CSharpToJava.Core/HIR/HIRTypeGenerator.cs (stub)
using CSharpToJava.Core.Context;
using CSharpToJava.Core.Java2;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.HIR;

public class HIRTypeGenerator
{
    public IrTypeDeclaration? Generate(TypeDeclarationSyntax node, ConversionContext ctx) => null;
    public IrEnumDeclaration? GenerateEnum(EnumDeclarationSyntax node, ConversionContext ctx) => null;
    public IrClassDeclaration? GenerateDelegate(DelegateDeclarationSyntax node, ConversionContext ctx) => null;
}
```

```csharp
// src/CSharpToJava.Core/HIR/HIRStatementGenerator.cs (stub)
namespace CSharpToJava.Core.HIR;

public class HIRStatementGenerator { }
```

```csharp
// src/CSharpToJava.Core/HIR/HIRExpressionGenerator.cs (stub)
namespace CSharpToJava.Core.HIR;

public class HIRExpressionGenerator { }
```

```csharp
// src/CSharpToJava.Core/HIR/HIRImportResolver.cs (stub)
using CSharpToJava.Core.Context;
using CSharpToJava.Core.Java2;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.HIR;

public class HIRImportResolver
{
    public void ProcessUsing(UsingDirectiveSyntax node, IrCompilationUnit unit, ConversionContext ctx) { }
}
```

- [ ] **Step 4: Build and commit**

```bash
dotnet build src/CSharpToJava.Core/CSharpToJava.Core.csproj
git add src/CSharpToJava.Core/HIR/
git commit -m "feat: add HIR Generator skeleton with IHIRGenerator interface"
```


### Task B2: HIRExpressionGenerator

**Files:**
- Modify: `src/CSharpToJava.Core/HIR/HIRExpressionGenerator.cs`

- [ ] **Step 1: 实现核心表达式生成器**

This is the critical component — it generates structured IrExpression nodes using Roslyn semantic model for symbol binding. It replaces `ExpressionTransformerFacade` which returned strings.

```csharp
// src/CSharpToJava.Core/HIR/HIRExpressionGenerator.cs
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.Context;
using CSharpToJava.Core.Java2;

namespace CSharpToJava.Core.HIR;

public class HIRExpressionGenerator
{
    private ConversionContext _ctx = null!;

    public IrExpression Generate(ExpressionSyntax expr, ConversionContext context)
    {
        _ctx = context;
        return expr switch
        {
            LiteralExpressionSyntax lit => GenerateLiteral(lit),
            IdentifierNameSyntax id => GenerateIdentifier(id),
            BinaryExpressionSyntax bin => GenerateBinary(bin),
            PrefixUnaryExpressionSyntax pre => GeneratePrefixUnary(pre),
            PostfixUnaryExpressionSyntax post => GeneratePostfixUnary(post),
            InvocationExpressionSyntax inv => GenerateInvocation(inv),
            MemberAccessExpressionSyntax mem => GenerateMemberAccess(mem),
            ObjectCreationExpressionSyntax ne => GenerateObjectCreation(ne),
            ArrayCreationExpressionSyntax arr => GenerateArrayCreation(arr),
            ElementAccessExpressionSyntax elem => GenerateElementAccess(elem),
            ConditionalExpressionSyntax cond => GenerateConditional(cond),
            AssignmentExpressionSyntax asgn => GenerateAssignment(asgn),
            CastExpressionSyntax cast => GenerateCast(cast),
            ParenthesizedExpressionSyntax paren => Generate(paren.Expression, context), // unwrap
            SimpleLambdaExpressionSyntax lam => GenerateSimpleLambda(lam),
            ParenthesizedLambdaExpressionSyntax plam => GenerateParenthesizedLambda(plam),
            ThisExpressionSyntax th => new IrThisExpression { IsSuper = false, Symbol = GetSymbol(th) },
            BaseExpressionSyntax bas => new IrThisExpression { IsSuper = true, Symbol = GetSymbol(bas) },
            TypeOfExpressionSyntax tof => GenerateTypeOf(tof),
            IsPatternExpressionSyntax isPat => GenerateIsPattern(isPat),
            ConditionalAccessExpressionSyntax condAcc => GenerateConditionalAccess(condAcc),
            _ => throw new NotSupportedException("Unsupported expression type: " + expr.Kind())
        };
    }

    private IrLiteralExpression GenerateLiteral(LiteralExpressionSyntax node)
    {
        return new IrLiteralExpression { Value = node.Token.Text };
    }

    private IrIdentifierExpression GenerateIdentifier(IdentifierNameSyntax node)
    {
        var symbol = GetSymbol(node);
        var name = node.Identifier.Text;
        // Apply type mapping for C# type names
        if (symbol is ITypeSymbol typeSym)
        {
            name = _ctx.MapType(typeSym);
            return new IrIdentifierExpression { Name = name, Symbol = symbol, JavaType = name };
        }
        return new IrIdentifierExpression { Name = name, Symbol = symbol };
    }

    private IrExpression GenerateBinary(BinaryExpressionSyntax node)
    {
        var op = MapBinaryOperator(node.OperatorToken, node);
        var left = Generate(node.Left);
        var right = Generate(node.Right);

        // Check for operator overload
        var symbol = GetSymbol(node);
        if (symbol is IMethodSymbol ms && ms.MethodKind == MethodKind.UserDefinedOperator)
        {
            return new IrCSharpOperatorCallExpression
            {
                Left = left,
                OperatorMethodName = ms.Name,
                Right = right,
                Symbol = symbol,
                JavaType = _ctx.MapType(ms.ReturnType),
            };
        }

        return new IrBinaryExpression
        {
            Left = left, Operator = op, Right = right,
            Symbol = symbol,
        };
    }

    private IrExpression GeneratePrefixUnary(PrefixUnaryExpressionSyntax node)
    {
        var op = node.OperatorToken.Kind() switch
        {
            SyntaxKind.PlusToken => IrUnaryOp.Plus,
            SyntaxKind.MinusToken => IrUnaryOp.Minus,
            SyntaxKind.ExclamationToken => IrUnaryOp.Not,
            SyntaxKind.TildeToken => IrUnaryOp.BitwiseNot,
            SyntaxKind.PlusPlusToken => IrUnaryOp.PreIncrement,
            SyntaxKind.MinusMinusToken => IrUnaryOp.PreDecrement,
            _ => throw new NotSupportedException("Unhandled prefix unary: " + node.OperatorToken.Kind()),
        };
        return new IrUnaryExpression { Operator = op, Operand = Generate(node.Operand), Symbol = GetSymbol(node) };
    }

    private IrExpression GeneratePostfixUnary(PostfixUnaryExpressionSyntax node)
    {
        var op = node.OperatorToken.Kind() switch
        {
            SyntaxKind.PlusPlusToken => IrUnaryOp.PostIncrement,
            SyntaxKind.MinusMinusToken => IrUnaryOp.PostDecrement,
            _ => throw new NotSupportedException("Unhandled postfix unary: " + node.OperatorToken.Kind()),
        };
        return new IrUnaryExpression { Operator = op, Operand = Generate(node.Operand), Symbol = GetSymbol(node) };
    }

    private IrExpression GenerateInvocation(InvocationExpressionSyntax node)
    {
        var symbol = GetSymbol(node) as IMethodSymbol;
        var javaMethodName = MapMethodName(symbol, node);
        var targetExpr = node.Expression switch
        {
            MemberAccessExpressionSyntax ma => Generate(ma.Expression),
            _ => null,
        };
        var args = node.ArgumentList.Arguments.Select(a => Generate(a.Expression)).ToList();

        // Handle ref/out arguments
        for (int i = 0; i < node.ArgumentList.Arguments.Count; i++)
        {
            var arg = node.ArgumentList.Arguments[i];
            if (arg.RefKindKeyword.IsKind(SyntaxKind.RefKeyword) || arg.RefKindKeyword.IsKind(SyntaxKind.OutKeyword))
            {
                if (i < args.Count)
                {
                    args[i] = new IrCSharpRefOutExpression
                    {
                        Inner = args[i],
                        IsRef = arg.RefKindKeyword.IsKind(SyntaxKind.RefKeyword),
                        IsOut = arg.RefKindKeyword.IsKind(SyntaxKind.OutKeyword),
                    };
                }
            }
        }

        return new IrInvocationExpression
        {
            Target = targetExpr,
            MethodName = javaMethodName,
            Arguments = args,
            Symbol = symbol,
            JavaType = symbol != null ? _ctx.MapType(symbol.ReturnType) : null,
        };
    }

    private IrExpression GenerateMemberAccess(MemberAccessExpressionSyntax node)
    {
        var symbol = GetSymbol(node);
        var target = Generate(node.Expression);
        var memberName = node.Name.Identifier.Text;

        // C# property access -> IrCSharpPropertyAccessExpression
        if (symbol is IPropertySymbol)
        {
            return new IrCSharpPropertyAccessExpression
            {
                Target = target,
                PropertyName = memberName,
                Symbol = symbol,
                JavaType = symbol is IPropertySymbol ps ? _ctx.MapType(ps.Type) : null,
            };
        }

        // Event access
        if (symbol is IEventSymbol)
        {
            return new IrCSharpEventExpression
            {
                Target = target,
                EventName = memberName,
                Symbol = symbol,
            };
        }

        // Map C# member name to Java equivalent (e.g. Count -> size, etc.)
        memberName = MapFieldOrPropertyName(symbol, memberName);

        return new IrMemberAccessExpression
        {
            Target = target,
            MemberName = memberName,
            Symbol = symbol,
        };
    }

    private IrExpression GenerateElementAccess(ElementAccessExpressionSyntax node)
    {
        var symbol = GetSymbol(node);
        var target = Generate(node.Expression);
        var indices = node.ArgumentList.Arguments.Select(a => Generate(a.Expression)).ToList();

        // If this is an indexer (not array access), use CSharpIndexerAccessExpression
        if (symbol is IPropertySymbol)
        {
            return new IrCSharpIndexerAccessExpression
            {
                Target = target,
                Indices = indices,
                Symbol = symbol,
            };
        }

        // Array access
        return new IrArrayAccessExpression
        {
            Target = target,
            Index = indices[0],
            Symbol = symbol,
        };
    }

    private IrExpression GenerateObjectCreation(ObjectCreationExpressionSyntax node)
    {
        var symbol = GetSymbol(node);
        var typeName = _ctx.MapTypeFromSyntax(node.Type);
        var args = node.ArgumentList?.Arguments.Select(a => Generate(a.Expression)).ToList() ?? new();

        return new IrNewExpression
        {
            TypeName = typeName,
            Arguments = args,
            Symbol = symbol,
            JavaType = typeName,
        };
    }

    private IrExpression GenerateArrayCreation(ArrayCreationExpressionSyntax node)
    {
        var elemType = _ctx.MapTypeFromSyntax(node.Type.ElementType);
        return new IrNewExpression
        {
            TypeName = elemType + "[]",
            ArrayInitializer = node.Initializer?.ToString() ?? "{}",
            Symbol = GetSymbol(node),
            JavaType = elemType + "[]",
        };
    }

    private IrConditionalExpression GenerateConditional(ConditionalExpressionSyntax node)
    {
        return new IrConditionalExpression
        {
            Condition = Generate(node.Condition),
            WhenTrue = Generate(node.WhenTrue),
            WhenFalse = Generate(node.WhenFalse),
            Symbol = GetSymbol(node),
        };
    }

    private IrAssignmentExpression GenerateAssignment(AssignmentExpressionSyntax node)
    {
        return new IrAssignmentExpression
        {
            Target = Generate(node.Left),
            Operator = MapAssignmentOp(node.OperatorToken.Kind()),
            Value = Generate(node.Right),
            Symbol = GetSymbol(node),
        };
    }

    private IrCastExpression GenerateCast(CastExpressionSyntax node)
    {
        var javaType = _ctx.MapTypeFromSyntax(node.Type);
        return new IrCastExpression
        {
            TargetType = javaType,
            Expression = Generate(node.Expression),
            Symbol = GetSymbol(node),
            JavaType = javaType,
        };
    }

    private IrLambdaExpression GenerateSimpleLambda(SimpleLambdaExpressionSyntax node)
    {
        return new IrLambdaExpression
        {
            Parameters = { new IrLambdaParameter { Name = node.Parameter.Identifier.Text } },
            ExpressionBody = node.Body is ExpressionSyntax expr ? Generate(expr) : null,
            BlockBody = node.Body is BlockSyntax block ? GenerateBlock(block) : null,
        };
    }

    private IrLambdaExpression GenerateParenthesizedLambda(ParenthesizedLambdaExpressionSyntax node)
    {
        return new IrLambdaExpression
        {
            Parameters = node.ParameterList.Parameters
                .Select(p => new IrLambdaParameter { Name = p.Identifier.Text, Type = p.Type?.ToString() })
                .ToList(),
            ExpressionBody = node.Body is ExpressionSyntax expr ? Generate(expr) : null,
            BlockBody = node.Body is BlockSyntax block ? GenerateBlock(block) : null,
        };
    }

    private IrExpression GenerateTypeOf(TypeOfExpressionSyntax node)
    {
        var javaType = _ctx.MapTypeFromSyntax(node.Type);
        return new IrMemberAccessExpression
        {
            Target = new IrIdentifierExpression { Name = javaType, JavaType = javaType },
            MemberName = "class",
            JavaType = "Class<" + javaType + ">",
        };
    }

    private IrExpression GenerateIsPattern(IsPatternExpressionSyntax node)
    {
        var subject = Generate(node.Expression);
        if (node.Pattern is DeclarationPatternSyntax declPat)
        {
            var typeName = _ctx.MapTypeFromSyntax(declPat.Type);
            return new IrInstanceOfExpression
            {
                Expression = subject,
                TypeName = typeName,
                PatternVariable = declPat.Designation is SingleVariableDesignationSyntax sv ? sv.Identifier.Text : null,
            };
        }
        if (node.Pattern is ConstantPatternSyntax constPat)
        {
            return new IrBinaryExpression
            {
                Left = subject,
                Operator = IrBinaryOp.Equals,
                Right = Generate(constPat.Expression),
            };
        }
        throw new NotSupportedException("Unhandled pattern type: " + node.Pattern.GetType().Name);
    }

    private IrExpression GenerateConditionalAccess(ConditionalAccessExpressionSyntax node)
    {
        // x?.Method() -> x == null ? null : x.Method()
        var target = Generate(node.Expression);
        var whenNotNull = node.WhenNotNull switch
        {
            MemberBindingExpressionSyntax mb => new IrMemberAccessExpression
            {
                Target = new IrIdentifierExpression { Name = "_tmp" },
                MemberName = mb.Name.Identifier.Text,
            },
            InvocationExpressionSyntax inv => Generate(inv),
            _ => Generate((ExpressionSyntax)node.WhenNotNull),
        };
        return new IrConditionalExpression
        {
            Condition = new IrBinaryExpression
            {
                Left = target,
                Operator = IrBinaryOp.Equals,
                Right = new IrLiteralExpression { Value = "null" },
            },
            WhenTrue = new IrLiteralExpression { Value = "null" },
            WhenFalse = whenNotNull,
        };
    }

    private IrBlockStatement GenerateBlock(BlockSyntax node)
    {
        var block = new IrBlockStatement();
        foreach (var stmt in node.Statements)
        {
            var gen = new HIRStatementGenerator();
            var irStmt = gen.Generate(stmt, _ctx);
            if (irStmt != null) block.Statements.Add(irStmt);
        }
        return block;
    }

    // ─── Helper methods ───

    private ISymbol? GetSymbol(ExpressionSyntax node)
    {
        var semanticModel = _ctx.SemanticModel;
        if (semanticModel == null) return null;
        var info = semanticModel.GetSymbolInfo(node);
        return info.Symbol ?? info.CandidateSymbols.FirstOrDefault();
    }

    private string MapMethodName(IMethodSymbol? symbol, InvocationExpressionSyntax node)
    {
        if (symbol == null) return node.Expression.ToString();
        var csharpName = symbol.Name;
        // Delegate to TypeMappingService for method name mapping
        var fullTypeName = symbol.ContainingType?.ToDisplayString();
        if (fullTypeName != null)
        {
            var mapped = _ctx.TypeMappings.MapMethod(csharpName, fullTypeName);
            if (mapped != csharpName) return mapped;
        }
        // Common hard-coded mappings as fallback (will be superseded by TypeMappings)
        return csharpName switch
        {
            "GetEnumerator" => "iterator",
            "MoveNext" => "hasNext",
            "get_Current" => "next",
            "Add" when symbol.ContainingType?.Name is "IList" or "List" => "add",
            _ => csharpName,
        };
    }

    private string MapFieldOrPropertyName(ISymbol? symbol, string csharpName)
    {
        if (symbol is IPropertySymbol) return csharpName;
        if (symbol is IFieldSymbol fs)
        {
            var typeName = fs.ContainingType?.ToDisplayString();
            if (typeName != null)
            {
                var mapped = _ctx.TypeMappings.MapField(csharpName, typeName);
                if (mapped != csharpName) return mapped;
            }
            return csharpName switch
            {
                "Count" when fs.Type.Name == "Int32" => "size", // will be lowered via property
                "Length" when fs.Type.Name == "Int32" => "length",
                _ => csharpName,
            };
        }
        return csharpName;
    }

    private static IrBinaryOp MapBinaryOperator(SyntaxToken opToken, BinaryExpressionSyntax node)
    {
        return opToken.Kind() switch
        {
            SyntaxKind.PlusToken => IrBinaryOp.Add,
            SyntaxKind.MinusToken => IrBinaryOp.Subtract,
            SyntaxKind.AsteriskToken => IrBinaryOp.Multiply,
            SyntaxKind.SlashToken => IrBinaryOp.Divide,
            SyntaxKind.PercentToken => IrBinaryOp.Modulo,
            SyntaxKind.AmpersandAmpersandToken => IrBinaryOp.LogicalAnd,
            SyntaxKind.BarBarToken => IrBinaryOp.LogicalOr,
            SyntaxKind.AmpersandToken => IrBinaryOp.BitwiseAnd,
            SyntaxKind.BarToken => IrBinaryOp.BitwiseOr,
            SyntaxKind.CaretToken => IrBinaryOp.BitwiseXor,
            SyntaxKind.LessThanLessThanToken => IrBinaryOp.ShiftLeft,
            SyntaxKind.GreaterThanGreaterThanToken => IrBinaryOp.ShiftRight,
            SyntaxKind.EqualsEqualsToken => IrBinaryOp.Equals,
            SyntaxKind.ExclamationEqualsToken => IrBinaryOp.NotEquals,
            SyntaxKind.LessThanToken => IrBinaryOp.LessThan,
            SyntaxKind.LessThanEqualsToken => IrBinaryOp.LessThanOrEqual,
            SyntaxKind.GreaterThanToken => IrBinaryOp.GreaterThan,
            SyntaxKind.GreaterThanEqualsToken => IrBinaryOp.GreaterThanOrEqual,
            SyntaxKind.QuestionQuestionToken => IrBinaryOp.NullCoalescing,
            _ => throw new NotSupportedException("Unhandled binary op: " + opToken.Kind()),
        };
    }

    private static IrAssignmentOp MapAssignmentOp(SyntaxKind kind)
    {
        return kind switch
        {
            SyntaxKind.EqualsToken => IrAssignmentOp.Assign,
            SyntaxKind.PlusEqualsToken => IrAssignmentOp.AddAssign,
            SyntaxKind.MinusEqualsToken => IrAssignmentOp.SubtractAssign,
            SyntaxKind.AsteriskEqualsToken => IrAssignmentOp.MultiplyAssign,
            SyntaxKind.SlashEqualsToken => IrAssignmentOp.DivideAssign,
            SyntaxKind.AmpersandEqualsToken => IrAssignmentOp.AndAssign,
            SyntaxKind.BarEqualsToken => IrAssignmentOp.OrAssign,
            SyntaxKind.CaretEqualsToken => IrAssignmentOp.XorAssign,
            _ => IrAssignmentOp.Assign,
        };
    }
}
```

- [ ] **Step 2: Build and commit**

```bash
dotnet build src/CSharpToJava.Core/CSharpToJava.Core.csproj
git add src/CSharpToJava.Core/HIR/
git commit -m "feat: implement HIRExpressionGenerator with semantic symbol binding"
```


### Task B3: HIRStatementGenerator

**Files:**
- Modify: `src/CSharpToJava.Core/HIR/HIRStatementGenerator.cs`

- [ ] **Step 1: 实现语句生成器**

```csharp
// src/CSharpToJava.Core/HIR/HIRStatementGenerator.cs
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.Context;
using CSharpToJava.Core.Java2;

namespace CSharpToJava.Core.HIR;

public class HIRStatementGenerator
{
    private ConversionContext _ctx = null!;
    private readonly HIRExpressionGenerator _exprGen = new();

    public IrStatement? Generate(StatementSyntax stmt, ConversionContext context)
    {
        _ctx = context;
        return stmt switch
        {
            BlockSyntax block => GenerateBlock(block),
            ExpressionStatementSyntax es => new IrExpressionStatement { Expression = _exprGen.Generate(es.Expression, _ctx) },
            LocalDeclarationStatementSyntax lds => GenerateLocalDecl(lds),
            ReturnStatementSyntax rs => GenerateReturn(rs),
            IfStatementSyntax ifs => GenerateIf(ifs),
            ForEachStatementSyntax fe => GenerateForEach(fe),
            ForStatementSyntax f => GenerateFor(f),
            WhileStatementSyntax ws => GenerateWhile(ws),
            DoStatementSyntax dw => GenerateDoWhile(dw),
            TryStatementSyntax ts => GenerateTryCatch(ts),
            ThrowStatementSyntax th => new IrThrowStatement { Expression = _exprGen.Generate(th.Expression, _ctx) },
            SwitchStatementSyntax sw => GenerateSwitch(sw),
            BreakStatementSyntax br => new IrBreakStatement(),
            ContinueStatementSyntax ct => new IrContinueStatement(),
            UsingStatementSyntax us => GenerateUsing(us),
            YieldStatementSyntax ys => GenerateYield(ys),
            _ => throw new NotSupportedException("Unsupported statement type: " + stmt.Kind()),
        };
    }

    private IrBlockStatement GenerateBlock(BlockSyntax node)
    {
        var block = new IrBlockStatement();
        foreach (var s in node.Statements)
        {
            var irStmt = Generate(s);
            if (irStmt != null) block.Statements.Add(irStmt);
        }
        return block;
    }

    private IrVariableDeclarationStatement GenerateLocalDecl(LocalDeclarationStatementSyntax node)
    {
        var decl = node.Declaration;
        var varDecl = new IrVariableDeclarationStatement
        {
            Type = _ctx.MapTypeFromSyntax(decl.Type),
            Name = decl.Variables[0].Identifier.Text,
        };
        if (decl.Variables[0].Initializer != null)
            varDecl.Initializer = _exprGen.Generate(decl.Variables[0].Initializer.Value, _ctx);
        return varDecl;
    }

    private IrReturnStatement GenerateReturn(ReturnStatementSyntax node)
    {
        return new IrReturnStatement
        {
            Expression = node.Expression != null ? _exprGen.Generate(node.Expression, _ctx) : null,
        };
    }

    private IrIfStatement GenerateIf(IfStatementSyntax node)
    {
        return new IrIfStatement
        {
            Condition = _exprGen.Generate(node.Condition, _ctx),
            ThenBody = Generate(node.Statement)!,
            ElseBody = node.Else != null ? Generate(node.Else.Statement) : null,
        };
    }

    private IrForEachStatement GenerateForEach(ForEachStatementSyntax node)
    {
        return new IrForEachStatement
        {
            VariableType = _ctx.MapTypeFromSyntax(node.Type),
            VariableName = node.Identifier.Text,
            Collection = _exprGen.Generate(node.Expression, _ctx),
            Body = Generate(node.Statement)!,
        };
    }

    private IrForStatement GenerateFor(ForStatementSyntax node)
    {
        var initializer = node.Declaration != null
            ? _ctx.MapTypeFromSyntax(node.Declaration.Type) + " " + string.Join(", ", node.Declaration.Variables.Select(v => v.ToString()))
            : string.Join(", ", node.Initializers.Select(i => i.ToString()));
        return new IrForStatement
        {
            Initializer = initializer,
            Condition = node.Condition != null ? _exprGen.Generate(node.Condition, _ctx) : null,
            Increment = string.Join(", ", node.Incrementors.Select(i => i.ToString())),
            Body = Generate(node.Statement)!,
        };
    }

    private IrWhileStatement GenerateWhile(WhileStatementSyntax node)
    {
        return new IrWhileStatement
        {
            Condition = _exprGen.Generate(node.Condition, _ctx),
            Body = Generate(node.Statement)!,
        };
    }

    private IrDoWhileStatement GenerateDoWhile(DoStatementSyntax node)
    {
        return new IrDoWhileStatement
        {
            Condition = _exprGen.Generate(node.Condition, _ctx),
            Body = Generate(node.Statement)!,
        };
    }

    private IrTryCatchStatement GenerateTryCatch(TryStatementSyntax node)
    {
        var tc = new IrTryCatchStatement { TryBody = GenerateBlock(node.Block) };
        foreach (var cc in node.Catches)
        {
            tc.CatchClauses.Add(new IrCatchClause
            {
                ExceptionType = cc.Declaration != null ? _ctx.MapTypeFromSyntax(cc.Declaration.Type) : "Exception",
                VariableName = cc.Declaration?.Identifier.Text,
                Body = GenerateBlock(cc.Block),
            });
        }
        if (node.Finally != null)
            tc.FinallyBody = GenerateBlock(node.Finally.Block);
        return tc;
    }

    private IrSwitchStatement GenerateSwitch(SwitchStatementSyntax node)
    {
        var sw = new IrSwitchStatement { Expression = _exprGen.Generate(node.Expression, _ctx) };
        foreach (var section in node.Sections)
        {
            var irSection = new IrSwitchSection();
            irSection.Labels.AddRange(section.Labels.Select(l => l.ToString()));
            foreach (var s in section.Statements)
            {
                var irStmt = Generate(s);
                if (irStmt != null) irSection.Statements.Add(irStmt);
            }
            sw.Sections.Add(irSection);
        }
        return sw;
    }

    private IrCSharpUsingStatement GenerateUsing(UsingStatementSyntax node)
    {
        return new IrCSharpUsingStatement
        {
            Resource = node.Declaration != null
                ? new IrVariableDeclarationStatement { Type = _ctx.MapTypeFromSyntax(node.Declaration.Type), Name = node.Declaration.Variables[0].Identifier.Text }
                : null,
            ResourceExpression = node.Expression != null ? _exprGen.Generate(node.Expression, _ctx) : null,
            Body = Generate(node.Statement)!,
        };
    }

    private IrStatement GenerateYield(YieldStatementSyntax node)
    {
        if (node.ReturnOrBreakKeyword.IsKind(SyntaxKind.ReturnKeyword))
        {
            return new IrCSharpYieldReturnStatement
            {
                Expression = node.Expression != null ? _exprGen.Generate(node.Expression, _ctx) : null,
            };
        }
        return new IrCSharpYieldBreakStatement();
    }
}
```

- [ ] **Step 2: Build and commit**

```bash
dotnet build src/CSharpToJava.Core/CSharpToJava.Core.csproj
git add src/CSharpToJava.Core/HIR/HIRStatementGenerator.cs
git commit -m "feat: implement HIRStatementGenerator"
```


### Task B4: HIRTypeGenerator + HIRImportResolver

**Files:**
- Modify: `src/CSharpToJava.Core/HIR/HIRTypeGenerator.cs`
- Modify: `src/CSharpToJava.Core/HIR/HIRImportResolver.cs`

- [ ] **Step 1: 实现 HIRTypeGenerator**

```csharp
// src/CSharpToJava.Core/HIR/HIRTypeGenerator.cs
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.Context;
using CSharpToJava.Core.Java2;

namespace CSharpToJava.Core.HIR;

public class HIRTypeGenerator
{
    public IrTypeDeclaration? Generate(TypeDeclarationSyntax node, ConversionContext ctx)
    {
        return node.Kind() switch
        {
            Microsoft.CodeAnalysis.CSharp.SyntaxKind.ClassDeclaration => GenerateClass((ClassDeclarationSyntax)node, ctx),
            Microsoft.CodeAnalysis.CSharp.SyntaxKind.InterfaceDeclaration => GenerateInterface((InterfaceDeclarationSyntax)node, ctx),
            Microsoft.CodeAnalysis.CSharp.SyntaxKind.StructDeclaration => GenerateStruct((StructDeclarationSyntax)node, ctx),
            Microsoft.CodeAnalysis.CSharp.SyntaxKind.RecordDeclaration => GenerateRecord((RecordDeclarationSyntax)node, ctx),
            _ => null,
        };
    }

    private IrClassDeclaration GenerateClass(ClassDeclarationSyntax node, ConversionContext ctx)
    {
        ctx.EnterType(null!); // placeholder
        var cls = new IrClassDeclaration
        {
            Name = node.Identifier.Text,
            Modifiers = MapModifiers(node.Modifiers),
            ExtendedType = node.BaseList?.Types
                .FirstOrDefault(t => t.Type.ToString() != "object")
                ?.Type.ToString() is string baseType ? ctx.MapTypeFromSyntax(((BaseTypeDeclarationSyntax)node).BaseList!.Types[0].Type) : null,
        };
        // Transfer implemented interfaces
        if (node.BaseList != null)
        {
            foreach (var bt in node.BaseList.Types)
            {
                var mapped = ctx.MapTypeFromSyntax(bt.Type);
                if (mapped != cls.ExtendedType)
                    cls.ImplementedTypes.Add(mapped);
            }
        }
        // Generate members
        foreach (var member in node.Members)
        {
            switch (member)
            {
                case FieldDeclarationSyntax f:
                    foreach (var v in f.Declaration.Variables)
                    {
                        cls.Fields.Add(new IrFieldDeclaration
                        {
                            Modifiers = MapModifiers(f.Modifiers),
                            Type = ctx.MapTypeFromSyntax(f.Declaration.Type),
                            Name = v.Identifier.Text,
                            Initializer = v.Initializer?.Value.ToString(),
                        });
                    }
                    break;
                case PropertyDeclarationSyntax prop:
                    // Properties are generated as HIR property access — lowered later
                    break;
                case MethodDeclarationSyntax m:
                    cls.Methods.Add(GenerateMethod(m, ctx));
                    break;
                case ConstructorDeclarationSyntax c:
                    cls.Constructors.Add(GenerateConstructor(c, ctx));
                    break;
            }
        }
        ctx.LeaveType();
        return cls;
    }

    private IrInterfaceDeclaration GenerateInterface(InterfaceDeclarationSyntax node, ConversionContext ctx)
    {
        var iface = new IrInterfaceDeclaration
        {
            Name = node.Identifier.Text,
            Modifiers = MapModifiers(node.Modifiers),
        };
        foreach (var member in node.Members)
        {
            if (member is MethodDeclarationSyntax m)
                iface.Methods.Add(GenerateMethod(m, ctx));
        }
        return iface;
    }

    private IrClassDeclaration GenerateStruct(StructDeclarationSyntax node, ConversionContext ctx)
    {
        var cls = GenerateClass(node, ctx);
        cls!.IsConvertedFromStruct = true;
        return cls;
    }

    private IrClassDeclaration GenerateRecord(RecordDeclarationSyntax node, ConversionContext ctx)
    {
        var cls = GenerateClass(node, ctx);
        cls!.IsRecord = true;
        return cls;
    }

    public IrEnumDeclaration? GenerateEnum(EnumDeclarationSyntax node, ConversionContext ctx)
    {
        var enm = new IrEnumDeclaration
        {
            Name = node.Identifier.Text,
            Modifiers = MapModifiers(node.Modifiers),
        };
        foreach (var member in node.Members)
        {
            var val = new IrEnumValue { Name = member.Identifier.Text };
            if (member.EqualsValue != null)
                val.Arguments.Add(new IrLiteralExpression { Value = member.EqualsValue.Value.ToString() });
            enm.Values.Add(val);
        }
        return enm;
    }

    public IrClassDeclaration? GenerateDelegate(DelegateDeclarationSyntax node, ConversionContext ctx)
    {
        var sym = ctx.SemanticModel?.GetDeclaredSymbol(node);
        if (sym is not INamedTypeSymbol namedType) return null;

        var invokeMethod = namedType.DelegateInvokeMethod;
        if (invokeMethod == null) return null;

        return new IrClassDeclaration
        {
            Name = ctx.MapType(namedType),
            Modifiers = IrModifiers.Public,
            Annotations = { "FunctionalInterface" },
            Methods =
            {
                new IrMethodDeclaration
                {
                    Name = invokeMethod.Name,
                    ReturnType = ctx.MapType(invokeMethod.ReturnType),
                    Parameters = invokeMethod.Parameters.Select(p => new IrParameter
                    {
                        Type = ctx.MapType(p.Type),
                        Name = p.Name,
                    }).ToList(),
                    Modifiers = IrModifiers.Public | IrModifiers.Abstract,
                }
            }
        };
    }

    private IrMethodDeclaration GenerateMethod(MethodDeclarationSyntax node, ConversionContext ctx)
    {
        var semModel = ctx.SemanticModel;
        var symbol = semModel?.GetDeclaredSymbol(node) as IMethodSymbol;
        var returnType = symbol != null ? ctx.MapType(symbol.ReturnType) : ctx.MapTypeFromSyntax(node.ReturnType);
        var method = new IrMethodDeclaration
        {
            Name = node.Identifier.Text,
            ReturnType = returnType,
            Modifiers = MapModifiers(node.Modifiers),
            Parameters = node.ParameterList.Parameters.Select(p => new IrParameter
            {
                Type = ctx.MapTypeFromSyntax(p.Type!),
                Name = p.Identifier.Text,
                IsRef = p.Modifiers.Any(m => m.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.RefKeyword)),
                IsOut = p.Modifiers.Any(m => m.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.OutKeyword)),
            }).ToList(),
        };
        if (node.Body != null)
        {
            var stmtGen = new HIRStatementGenerator();
            method.Body = (IrBlockStatement)stmtGen.Generate(node.Body, ctx)!;
        }
        return method;
    }

    private IrConstructorDeclaration GenerateConstructor(ConstructorDeclarationSyntax node, ConversionContext ctx)
    {
        return new IrConstructorDeclaration
        {
            TypeName = node.Identifier.Text,
            Modifiers = MapModifiers(node.Modifiers),
            Parameters = node.ParameterList.Parameters.Select(p => new IrParameter
            {
                Type = ctx.MapTypeFromSyntax(p.Type!),
                Name = p.Identifier.Text,
            }).ToList(),
            Body = node.Body != null
                ? (IrBlockStatement)new HIRStatementGenerator().Generate(node.Body, ctx)!
                : new IrBlockStatement(),
        };
    }

    private static IrModifiers MapModifiers(SyntaxTokenList modifiers)
    {
        var m = IrModifiers.None;
        foreach (var tok in modifiers)
        {
            m |= tok.Kind() switch
            {
                Microsoft.CodeAnalysis.CSharp.SyntaxKind.PublicKeyword => IrModifiers.Public,
                Microsoft.CodeAnalysis.CSharp.SyntaxKind.ProtectedKeyword => IrModifiers.Protected,
                Microsoft.CodeAnalysis.CSharp.SyntaxKind.PrivateKeyword => IrModifiers.Private,
                Microsoft.CodeAnalysis.CSharp.SyntaxKind.StaticKeyword => IrModifiers.Static,
                Microsoft.CodeAnalysis.CSharp.SyntaxKind.AbstractKeyword => IrModifiers.Abstract,
                _ => IrModifiers.None,
            };
        }
        return m;
    }
}
```

- [ ] **Step 2: 实现 HIRImportResolver**

```csharp
// src/CSharpToJava.Core/HIR/HIRImportResolver.cs
using CSharpToJava.Core.Context;
using CSharpToJava.Core.Java2;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.HIR;

public class HIRImportResolver
{
    public void ProcessUsing(UsingDirectiveSyntax node, IrCompilationUnit unit, ConversionContext ctx)
    {
        if (node.Name == null) return;

        // Handle static imports
        if (node.StaticKeyword.IsKind(SyntaxKind.StaticKeyword))
        {
            var javaType = MapUsingToJava(node.Name.ToString(), ctx);
            if (!string.IsNullOrEmpty(javaType) && !unit.Imports.Contains("static " + javaType))
                unit.Imports.Add("static " + javaType);
            return;
        }

        // Handle aliases
        if (node.Alias != null)
        {
            ctx.ProcessUsingAlias(node);
            return;
        }

        // Normal using
        var importName = MapUsingToJava(node.Name.ToString(), ctx);
        if (!string.IsNullOrEmpty(importName) && !unit.Imports.Contains(importName))
            unit.Imports.Add(importName);
    }

    private static string? MapUsingToJava(string csharpUsing, ConversionContext ctx)
    {
        return csharpUsing switch
        {
            "System" => "java.lang",
            "System.Collections.Generic" => "java.util",
            "System.Linq" => "java.util.stream",
            _ => ctx.NamespaceToPackage(csharpUsing),
        };
    }
}
```

- [ ] **Step 3: Build and commit**

```bash
dotnet build src/CSharpToJava.Core/CSharpToJava.Core.csproj
git add src/CSharpToJava.Core/HIR/
git commit -m "feat: implement HIRTypeGenerator and HIRImportResolver"
```


### Task B5: HIR Generator 端到端测试

**Files:**
- Create: `tests/CSharpToJava.Tests/Java2/HIRGeneratorTests.cs`

- [ ] **Step 1: 写端到端测试**

```csharp
// tests/CSharpToJava.Tests/Java2/HIRGeneratorTests.cs
using Xunit;
using CSharpToJava.Core.Context;
using CSharpToJava.Core.HIR;
using CSharpToJava.Core.Java2;
using CSharpToJava.Core.Java2.CodeGen;
using CSharpToJava.TypeMapping;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Tests.Java2;

public class HIRGeneratorTests
{
    [Fact]
    public void EmptyClass_GeneratesHIR()
    {
        var code = "class Empty { }";
        var options = new ConversionOptions();
        var typeMappings = new TypeMappingRegistry(new TypeMappingConfig());
        var context = new ConversionContext(options, typeMappings);
        var tree = CSharpSyntaxTree.ParseText(code);
        context.SemanticModel = CSharpCompilation.Create("Test").AddSyntaxTrees(tree).GetSemanticModel(tree);
        var root = (CompilationUnitSyntax)tree.GetRoot();

        var generator = new CSharpToJavaHIRGenerator();
        var unit = generator.Generate(root, context);

        Assert.NotNull(unit);
        Assert.Single(unit.TypeDeclarations);
        Assert.IsType<IrClassDeclaration>(unit.TypeDeclarations[0]);
        Assert.Equal("Empty", ((IrClassDeclaration)unit.TypeDeclarations[0]).Name);
    }

    [Fact]
    public void ClassWithIntField_GeneratesHIRWithStructuredField()
    {
        var code = "class Point { int x = 5; }";
        var options = new ConversionOptions();
        var context = new ConversionContext(options, new TypeMappingRegistry(new TypeMappingConfig()));
        var tree = CSharpSyntaxTree.ParseText(code);
        context.SemanticModel = CSharpCompilation.Create("Test").AddSyntaxTrees(tree).GetSemanticModel(tree);
        var root = (CompilationUnitSyntax)tree.GetRoot();

        var generator = new CSharpToJavaHIRGenerator();
        var unit = generator.Generate(root, context);
        var cls = (IrClassDeclaration)unit.TypeDeclarations[0];

        Assert.Single(cls.Fields);
        Assert.Equal("int", cls.Fields[0].Type);
        Assert.Equal("x", cls.Fields[0].Name);
        Assert.Equal("5", cls.Fields[0].Initializer);
    }

    [Fact]
    public void MethodWithBinaryExpression_GeneratesStructuredExpressionTree()
    {
        var code = "class Calc { int Add(int a, int b) { return a + b; } }";
        var options = new ConversionOptions();
        var context = new ConversionContext(options, new TypeMappingRegistry(new TypeMappingConfig()));
        var tree = CSharpSyntaxTree.ParseText(code);
        context.SemanticModel = CSharpCompilation.Create("Test").AddSyntaxTrees(tree).GetSemanticModel(tree);
        var root = (CompilationUnitSyntax)tree.GetRoot();

        var generator = new CSharpToJavaHIRGenerator();
        var unit = generator.Generate(root, context);
        var cls = (IrClassDeclaration)unit.TypeDeclarations[0];
        var method = cls.Methods[0];

        Assert.Single(method.Body!.Statements);
        var retStmt = Assert.IsType<IrReturnStatement>(method.Body.Statements[0]);
        var binExpr = Assert.IsType<IrBinaryExpression>(retStmt.Expression);
        Assert.Equal(IrBinaryOp.Add, binExpr.Operator);
        Assert.IsType<IrIdentifierExpression>(binExpr.Left);
        Assert.IsType<IrIdentifierExpression>(binExpr.Right);
    }

    [Fact]
    public void SimpleClass_RoundtripsThroughCodeGen()
    {
        var code = @"
class Calculator {
    int Add(int a, int b) {
        return a + b;
    }
}";
        var options = new ConversionOptions();
        var context = new ConversionContext(options, new TypeMappingRegistry(new TypeMappingConfig()));
        var tree = CSharpSyntaxTree.ParseText(code);
        context.SemanticModel = CSharpCompilation.Create("Test").AddSyntaxTrees(tree).GetSemanticModel(tree);
        var root = (CompilationUnitSyntax)tree.GetRoot();

        var generator = new CSharpToJavaHIRGenerator();
        var unit = generator.Generate(root, context);
        var codeGen = new JavaCodeGenerator();
        var java = codeGen.Generate(unit);

        // Verify the roundtrip produced valid-looking Java
        Assert.Contains("class Calculator", java);
        Assert.Contains("int Add", java);
        Assert.Contains("int a", java);
        Assert.Contains("int b", java);
        Assert.Contains("return a + b;", java);
    }
}
```

- [ ] **Step 2: Run tests**

```bash
dotnet test --filter "FullyQualifiedName~HIRGeneratorTests"
```
Expected: at least 2/4 PASS (EmptyClass, ClassWithIntField).

- [ ] **Step 3: Commit**

```bash
git add tests/CSharpToJava.Tests/Java2/HIRGeneratorTests.cs
git commit -m "test: add HIR Generator end-to-end tests"
```


---

## Phase C: Lowering Passes

### Task C1: ILoweringPass 接口 + LowerRefOut

**Files:**
- Create: `src/CSharpToJava.Core/Lowering/ILoweringPass.cs`
- Create: `src/CSharpToJava.Core/Lowering/LowerRefOut.cs`

- [ ] **Step 1: 创建接口和 ref/out Lowering**

```csharp
// src/CSharpToJava.Core/Lowering/ILoweringPass.cs
using CSharpToJava.Core.Context;
using CSharpToJava.Core.Java2;

namespace CSharpToJava.Core.Lowering;

public interface ILoweringPass
{
    string Name { get; }
    IrCompilationUnit Apply(IrCompilationUnit unit, ConversionContext context);
}
```

```csharp
// src/CSharpToJava.Core/Lowering/LowerRefOut.cs
using CSharpToJava.Core.Context;
using CSharpToJava.Core.Java2;

namespace CSharpToJava.Core.Lowering;

/// <summary>
/// Eliminates C# ref/out parameters by wrapping them in Holder objects.
/// - ref T x -> IntHolder/ObjectHolder<T> wrapping
/// - out T x -> Holder allocation before call, extraction after
/// - readonly ref -> pass by value
/// </summary>
public class LowerRefOut : ILoweringPass
{
    public string Name => "LowerRefOut";

    public IrCompilationUnit Apply(IrCompilationUnit unit, ConversionContext context)
    {
        foreach (var type in unit.TypeDeclarations)
            LowerType(type);
        return unit;
    }

    private void LowerType(IrTypeDeclaration type)
    {
        foreach (var method in type.Methods)
            if (method.Body != null)
                LowerBlock(method.Body);

        if (type is IrClassDeclaration cls)
            foreach (var ctor in cls.Constructors)
                if (ctor.Body != null)
                    LowerBlock(ctor.Body);

        // Lower method parameters (remove ref/out markers)
        foreach (var method in type.Methods)
        {
            foreach (var param in method.Parameters)
            {
                if (param.IsReadOnlyRef)
                {
                    param.IsRef = false;
                    param.IsReadOnlyRef = false;
                }
                param.IsOut = false;
                param.IsRef = false;
            }
        }

        foreach (var nested in type.NestedTypes)
            LowerType(nested);
    }

    private void LowerBlock(IrBlockStatement block)
    {
        for (int i = 0; i < block.Statements.Count; i++)
            block.Statements[i] = LowerStatement(block.Statements[i]);
    }

    private IrStatement LowerStatement(IrStatement stmt)
    {
        switch (stmt)
        {
            case IrBlockStatement b: LowerBlock(b); return b;
            case IrExpressionStatement es:
                es.Expression = LowerExpression(es.Expression);
                return es;
            case IrVariableDeclarationStatement vd:
                if (vd.Initializer != null)
                    vd.Initializer = LowerExpression(vd.Initializer);
                return vd;
            case IrReturnStatement ret:
                if (ret.Expression != null)
                    ret.Expression = LowerExpression(ret.Expression);
                return ret;
            case IrIfStatement ifs:
                ifs.Condition = LowerExpression(ifs.Condition);
                ifs.ThenBody = LowerStatement(ifs.ThenBody);
                if (ifs.ElseBody != null) ifs.ElseBody = LowerStatement(ifs.ElseBody);
                return ifs;
            case IrForEachStatement fe:
                fe.Collection = LowerExpression(fe.Collection);
                fe.Body = LowerStatement(fe.Body);
                return fe;
            case IrTryCatchStatement tc:
                LowerBlock(tc.TryBody);
                foreach (var cc in tc.CatchClauses) LowerBlock(cc.Body);
                if (tc.FinallyBody != null) LowerBlock(tc.FinallyBody);
                return tc;
            case IrThrowStatement th:
                th.Expression = LowerExpression(th.Expression);
                return th;
            default: return stmt;
        }
    }

    private IrExpression LowerExpression(IrExpression expr)
    {
        switch (expr)
        {
            case IrInvocationExpression inv:
            {
                // Transform ref/out arguments at call sites
                for (int i = 0; i < inv.Arguments.Count; i++)
                {
                    if (inv.Arguments[i] is IrCSharpRefOutExpression refOut)
                    {
                        var inner = LowerExpression(refOut.Inner);
                        if (refOut.IsOut)
                        {
                            // out arg: wrap in new Holder
                            var holderType = GetHolderType(inv, i);
                            inv.Arguments[i] = new IrNewExpression
                            {
                                TypeName = holderType,
                                JavaType = holderType,
                            };
                        }
                        else if (refOut.IsRef)
                        {
                            // ref arg: wrap existing variable
                            inv.Arguments[i] = inner;
                        }
                        else if (refOut.IsReadOnlyRef)
                        {
                            inv.Arguments[i] = inner; // pass by value
                        }
                    }
                    else
                    {
                        inv.Arguments[i] = LowerExpression(inv.Arguments[i]);
                    }
                }
                if (inv.Target != null) inv.Target = LowerExpression(inv.Target);
                return inv;
            }
            case IrCSharpRefOutExpression refOut:
                return LowerExpression(refOut.Inner);
            case IrBinaryExpression bin:
                bin.Left = LowerExpression(bin.Left);
                bin.Right = LowerExpression(bin.Right);
                return bin;
            case IrAssignmentExpression asgn:
                asgn.Target = LowerExpression(asgn.Target);
                asgn.Value = LowerExpression(asgn.Value);
                return asgn;
            case IrConditionalExpression cond:
                cond.Condition = LowerExpression(cond.Condition);
                cond.WhenTrue = LowerExpression(cond.WhenTrue);
                cond.WhenFalse = LowerExpression(cond.WhenFalse);
                return cond;
            default: return expr;
        }
    }

    private static string GetHolderType(IrInvocationExpression inv, int paramIndex)
    {
        // Derive Holder type from JavaType if available
        return "IntHolder"; // simplified — actual impl queries TypeMapping
    }
}
```

- [ ] **Step 2: Build and commit**

```bash
dotnet build src/CSharpToJava.Core/CSharpToJava.Core.csproj
git add src/CSharpToJava.Core/Lowering/
git commit -m "feat: add ILoweringPass and LowerRefOut"
```


### Task C2: LowerProperty + LowerIndexer

**Files:**
- Create: `src/CSharpToJava.Core/Lowering/LowerProperty.cs`
- Create: `src/CSharpToJava.Core/Lowering/LowerIndexer.cs`

- [ ] **Step 1: 实现 LowerProperty**

```csharp
// src/CSharpToJava.Core/Lowering/LowerProperty.cs
using CSharpToJava.Core.Context;
using CSharpToJava.Core.Java2;

namespace CSharpToJava.Core.Lowering;

/// <summary>
/// Lowers C# property access (obj.Property) to Java getter/setter calls.
/// - Reading obj.Prop -> obj.getProp()
/// - Writing obj.Prop = val -> obj.setProp(val)
/// - Auto-properties generate backing field + getter/setter methods
/// </summary>
public class LowerProperty : ILoweringPass
{
    public string Name => "LowerProperty";

    public IrCompilationUnit Apply(IrCompilationUnit unit, ConversionContext context)
    {
        foreach (var type in unit.TypeDeclarations)
            LowerType(type);
        return unit;
    }

    private void LowerType(IrTypeDeclaration type)
    {
        foreach (var method in type.Methods)
            if (method.Body != null)
                LowerBlock(method.Body);

        if (type is IrClassDeclaration cls)
            foreach (var ctor in cls.Constructors)
                if (ctor.Body != null)
                    LowerBlock(ctor.Body);

        foreach (var nested in type.NestedTypes)
            LowerType(nested);
    }

    private void LowerBlock(IrBlockStatement block)
    {
        for (int i = 0; i < block.Statements.Count; i++)
            block.Statements[i] = LowerStatement(block.Statements[i]);
    }

    private IrStatement LowerStatement(IrStatement stmt)
    {
        switch (stmt)
        {
            case IrBlockStatement b: LowerBlock(b); return b;
            case IrExpressionStatement es:
                es.Expression = LowerExpression(es.Expression);
                return es;
            case IrVariableDeclarationStatement vd:
                if (vd.Initializer != null) vd.Initializer = LowerExpression(vd.Initializer);
                return vd;
            case IrReturnStatement rs:
                if (rs.Expression != null) rs.Expression = LowerExpression(rs.Expression);
                return rs;
            case IrIfStatement ifs:
                ifs.Condition = LowerExpression(ifs.Condition);
                ifs.ThenBody = LowerStatement(ifs.ThenBody);
                if (ifs.ElseBody != null) ifs.ElseBody = LowerStatement(ifs.ElseBody);
                return ifs;
            case IrForEachStatement fe:
                fe.Collection = LowerExpression(fe.Collection);
                fe.Body = LowerStatement(fe.Body);
                return fe;
            default: return stmt;
        }
    }

    private IrExpression LowerExpression(IrExpression expr)
    {
        switch (expr)
        {
            case IrCSharpPropertyAccessExpression prop:
            {
                var javaPropName = char.ToUpper(prop.PropertyName[0]) + prop.PropertyName.Substring(1);
                if (prop.IsSetter)
                {
                    return new IrInvocationExpression
                    {
                        Target = LowerExpression(prop.Target),
                        MethodName = "set" + javaPropName,
                        Symbol = prop.Symbol,
                    };
                }
                return new IrInvocationExpression
                {
                    Target = LowerExpression(prop.Target),
                    MethodName = "get" + javaPropName,
                    Symbol = prop.Symbol,
                    JavaType = prop.JavaType,
                };
            }
            case IrAssignmentExpression asgn when asgn.Target is IrCSharpPropertyAccessExpression setProp:
            {
                var loweredProp = (IrInvocationExpression)LowerExpression(setProp);
                loweredProp.Arguments.Add(LowerExpression(asgn.Value));
                return loweredProp;
            }
            case IrCSharpIndexerAccessExpression idx:
            {
                var lowered = new IrInvocationExpression
                {
                    Target = LowerExpression(idx.Target),
                    MethodName = idx.IsSetter ? "set" : "get",
                    Symbol = idx.Symbol,
                };
                lowered.Arguments.AddRange(idx.Indices.Select(i => LowerExpression(i)));
                return lowered;
            }
            case IrInvocationExpression inv:
                if (inv.Target != null) inv.Target = LowerExpression(inv.Target);
                for (int i = 0; i < inv.Arguments.Count; i++)
                    inv.Arguments[i] = LowerExpression(inv.Arguments[i]);
                return inv;
            case IrBinaryExpression bin:
                bin.Left = LowerExpression(bin.Left);
                bin.Right = LowerExpression(bin.Right);
                return bin;
            case IrAssignmentExpression asgn:
                asgn.Target = LowerExpression(asgn.Target);
                asgn.Value = LowerExpression(asgn.Value);
                return asgn;
            case IrMemberAccessExpression mem:
                mem.Target = LowerExpression(mem.Target);
                return mem;
            default: return expr;
        }
    }
}
```

- [ ] **Step 2: LowerIndexer (合并到同一文件)**

```csharp
// src/CSharpToJava.Core/Lowering/LowerIndexer.cs
using CSharpToJava.Core.Context;
using CSharpToJava.Core.Java2;

namespace CSharpToJava.Core.Lowering;

/// <summary>
/// Lowers C# indexer (obj[key]) to Java get(index)/set(index, value) method calls.
/// </summary>
public class LowerIndexer : ILoweringPass
{
    public string Name => "LowerIndexer";

    public IrCompilationUnit Apply(IrCompilationUnit unit, ConversionContext context)
    {
        foreach (var type in unit.TypeDeclarations)
            LowerType(type);
        return unit;
    }

    private void LowerType(IrTypeDeclaration type)
    {
        foreach (var method in type.Methods)
            if (method.Body != null) LowerBlock(method.Body);
        if (type is IrClassDeclaration cls)
            foreach (var ctor in cls.Constructors)
                if (ctor.Body != null) LowerBlock(ctor.Body);
        foreach (var nested in type.NestedTypes) LowerType(nested);
    }

    private void LowerBlock(IrBlockStatement block)
    {
        for (int i = 0; i < block.Statements.Count; i++)
            block.Statements[i] = LowerStatement(block.Statements[i]);
    }

    private IrStatement LowerStatement(IrStatement stmt)
    {
        switch (stmt)
        {
            case IrBlockStatement b: LowerBlock(b); return b;
            case IrExpressionStatement es:
                es.Expression = LowerExpression(es.Expression);
                return es;
            case IrVariableDeclarationStatement vd:
                if (vd.Initializer != null) vd.Initializer = LowerExpression(vd.Initializer);
                return vd;
            case IrReturnStatement rs:
                if (rs.Expression != null) rs.Expression = LowerExpression(rs.Expression);
                return rs;
            default: return stmt;
        }
    }

    private IrExpression LowerExpression(IrExpression expr)
    {
        // Indexer lowering is handled by LowerProperty's IrCSharpIndexerAccessExpression case.
        // This pass only runs for indexers that were not already handled.
        switch (expr)
        {
            case IrAssignmentExpression asgn when asgn.Target is IrArrayAccessExpression arr &&
                arr.Target.Symbol is not null && arr.Target.Symbol is Microsoft.CodeAnalysis.IPropertySymbol:
            {
                return new IrInvocationExpression
                {
                    Target = LowerExpression(arr.Target),
                    MethodName = "set",
                    Arguments = { LowerExpression(arr.Index), LowerExpression(asgn.Value) },
                };
            }
            default: return expr;
        }
    }
}
```

- [ ] **Step 3: Build and commit**

```bash
dotnet build src/CSharpToJava.Core/CSharpToJava.Core.csproj
git add src/CSharpToJava.Core/Lowering/LowerProperty.cs src/CSharpToJava.Core/Lowering/LowerIndexer.cs
git commit -m "feat: add LowerProperty and LowerIndexer passes"
```


### Task C3: LowerOperator + LowerStruct + LowerEvent + LowerDelegate

**Files:**
- Create: `src/CSharpToJava.Core/Lowering/LowerOperator.cs`
- Create: `src/CSharpToJava.Core/Lowering/LowerStruct.cs`
- Create: `src/CSharpToJava.Core/Lowering/LowerEvent.cs`
- Create: `src/CSharpToJava.Core/Lowering/LowerDelegate.cs`

- [ ] **Step 1: 实现四个 Lowering Pass**

```csharp
// src/CSharpToJava.Core/Lowering/LowerOperator.cs
using CSharpToJava.Core.Context;
using CSharpToJava.Core.Java2;

namespace CSharpToJava.Core.Lowering;

public class LowerOperator : ILoweringPass
{
    public string Name => "LowerOperator";

    public IrCompilationUnit Apply(IrCompilationUnit unit, ConversionContext context)
    {
        foreach (var type in unit.TypeDeclarations) LowerType(type);
        return unit;
    }

    private void LowerType(IrTypeDeclaration type)
    {
        foreach (var method in type.Methods)
            if (method.Body != null) LowerBlock(method.Body);
        if (type is IrClassDeclaration cls)
            foreach (var ctor in cls.Constructors)
                if (ctor.Body != null) LowerBlock(ctor.Body);
        foreach (var nested in type.NestedTypes) LowerType(nested);
    }

    private void LowerBlock(IrBlockStatement block)
    {
        for (int i = 0; i < block.Statements.Count; i++)
            block.Statements[i] = LowerStatement(block.Statements[i]);
    }

    private IrStatement LowerStatement(IrStatement stmt)
    {
        switch (stmt)
        {
            case IrBlockStatement b: LowerBlock(b); return b;
            case IrExpressionStatement es:
                es.Expression = LowerExpression(es.Expression);
                return es;
            case IrReturnStatement rs:
                if (rs.Expression != null) rs.Expression = LowerExpression(rs.Expression);
                return rs;
            case IrIfStatement ifs:
                ifs.Condition = LowerExpression(ifs.Condition);
                ifs.ThenBody = LowerStatement(ifs.ThenBody);
                if (ifs.ElseBody != null) ifs.ElseBody = LowerStatement(ifs.ElseBody);
                return ifs;
            default: return stmt;
        }
    }

    private IrExpression LowerExpression(IrExpression expr)
    {
        switch (expr)
        {
            case IrCSharpOperatorCallExpression opCall:
            {
                return new IrInvocationExpression
                {
                    Target = new IrIdentifierExpression { Name = opCall.Left != null ? "" : "" },
                    MethodName = opCall.OperatorMethodName,
                    Arguments = {
                        opCall.Left != null ? LowerExpression(opCall.Left) : null!,
                        opCall.Right != null ? LowerExpression(opCall.Right) : null!
                    }.Where(a => a != null).ToList(),
                    Symbol = opCall.Symbol,
                    JavaType = opCall.JavaType,
                };
            }
            case IrBinaryExpression bin:
                bin.Left = LowerExpression(bin.Left);
                bin.Right = LowerExpression(bin.Right);
                return bin;
            default: return expr;
        }
    }
}
```

```csharp
// src/CSharpToJava.Core/Lowering/LowerStruct.cs
using CSharpToJava.Core.Context;
using CSharpToJava.Core.Java2;

namespace CSharpToJava.Core.Lowering;

public class LowerStruct : ILoweringPass
{
    public string Name => "LowerStruct";

    public IrCompilationUnit Apply(IrCompilationUnit unit, ConversionContext context)
    {
        foreach (var type in unit.TypeDeclarations) LowerType(type);
        return unit;
    }

    private void LowerType(IrTypeDeclaration type)
    {
        foreach (var method in type.Methods)
            if (method.Body != null) LowerBlock(method.Body);
        if (type is IrClassDeclaration cls)
            foreach (var ctor in cls.Constructors)
                if (ctor.Body != null) LowerBlock(ctor.Body);
        foreach (var nested in type.NestedTypes) LowerType(nested);
    }

    private void LowerBlock(IrBlockStatement block)
    {
        for (int i = 0; i < block.Statements.Count; i++)
            block.Statements[i] = LowerStatement(block.Statements[i]);
    }

    private IrStatement LowerStatement(IrStatement stmt)
    {
        switch (stmt)
        {
            case IrBlockStatement b: LowerBlock(b); return b;
            case IrExpressionStatement es:
                es.Expression = LowerExpression(es.Expression);
                return es;
            case IrVariableDeclarationStatement vd:
                if (vd.Initializer != null) vd.Initializer = LowerExpression(vd.Initializer);
                return vd;
            case IrReturnStatement rs:
                if (rs.Expression != null) rs.Expression = LowerExpression(rs.Expression);
                return rs;
            default: return stmt;
        }
    }

    private IrExpression LowerExpression(IrExpression expr)
    {
        switch (expr)
        {
            case IrCSharpStructCopyExpression copy:
                return new IrInvocationExpression
                {
                    Target = LowerExpression(copy.Source),
                    MethodName = "clone",
                    JavaType = copy.StructType,
                };
            case IrAssignmentExpression asgn:
                if (asgn.Value.JavaType != null && !asgn.Value.JavaType.EndsWith("[]"))
                {
                    asgn.Target = LowerExpression(asgn.Target);
                    asgn.Value = new IrInvocationExpression
                    {
                        Target = LowerExpression(asgn.Value),
                        MethodName = "clone",
                    };
                    return asgn;
                }
                asgn.Target = LowerExpression(asgn.Target);
                asgn.Value = LowerExpression(asgn.Value);
                return asgn;
            default: return expr;
        }
    }
}
```

```csharp
// src/CSharpToJava.Core/Lowering/LowerEvent.cs
using CSharpToJava.Core.Context;
using CSharpToJava.Core.Java2;

namespace CSharpToJava.Core.Lowering;

public class LowerEvent : ILoweringPass
{
    public string Name => "LowerEvent";

    public IrCompilationUnit Apply(IrCompilationUnit unit, ConversionContext context)
    {
        foreach (var type in unit.TypeDeclarations) LowerType(type);
        return unit;
    }

    private void LowerType(IrTypeDeclaration type)
    {
        foreach (var method in type.Methods)
            if (method.Body != null) LowerBlock(method.Body);
        if (type is IrClassDeclaration cls)
            foreach (var ctor in cls.Constructors)
                if (ctor.Body != null) LowerBlock(ctor.Body);
        foreach (var nested in type.NestedTypes) LowerType(nested);
    }

    private void LowerBlock(IrBlockStatement block)
    {
        for (int i = 0; i < block.Statements.Count; i++)
            block.Statements[i] = LowerStatement(block.Statements[i]);
    }

    private IrStatement LowerStatement(IrStatement stmt)
    {
        switch (stmt)
        {
            case IrBlockStatement b: LowerBlock(b); return b;
            case IrExpressionStatement es:
                es.Expression = LowerExpression(es.Expression);
                return es;
            default: return stmt;
        }
    }

    private IrExpression LowerExpression(IrExpression expr)
    {
        switch (expr)
        {
            case IrCSharpEventExpression ev:
                if (ev.IsSubscribe)
                    return new IrInvocationExpression
                    {
                        Target = LowerExpression(ev.Target),
                        MethodName = "add" + ev.EventName,
                        Arguments = { LowerExpression(ev.Handler!) },
                    };
                if (ev.IsUnsubscribe)
                    return new IrInvocationExpression
                    {
                        Target = LowerExpression(ev.Target),
                        MethodName = "remove" + ev.EventName,
                        Arguments = { LowerExpression(ev.Handler!) },
                    };
                return expr;
            default: return expr;
        }
    }
}
```

```csharp
// src/CSharpToJava.Core/Lowering/LowerDelegate.cs
using CSharpToJava.Core.Context;
using CSharpToJava.Core.Java2;

namespace CSharpToJava.Core.Lowering;

public class LowerDelegate : ILoweringPass
{
    public string Name => "LowerDelegate";

    public IrCompilationUnit Apply(IrCompilationUnit unit, ConversionContext context)
    {
        foreach (var type in unit.TypeDeclarations) LowerType(type);
        return unit;
    }

    private void LowerType(IrTypeDeclaration type)
    {
        foreach (var method in type.Methods)
            if (method.Body != null) LowerBlock(method.Body);
        if (type is IrClassDeclaration cls)
            foreach (var ctor in cls.Constructors)
                if (ctor.Body != null) LowerBlock(ctor.Body);
        foreach (var nested in type.NestedTypes) LowerType(nested);
    }

    private void LowerBlock(IrBlockStatement block)
    {
        for (int i = 0; i < block.Statements.Count; i++)
            block.Statements[i] = LowerStatement(block.Statements[i]);
    }

    private IrStatement LowerStatement(IrStatement stmt)
    {
        switch (stmt)
        {
            case IrBlockStatement b: LowerBlock(b); return b;
            case IrExpressionStatement es:
                es.Expression = LowerExpression(es.Expression);
                return es;
            default: return stmt;
        }
    }

    private IrExpression LowerExpression(IrExpression expr)
    {
        switch (expr)
        {
            case IrCSharpDelegateCreationExpression del:
                // Delegate creation -> inline the body directly (method reference / lambda)
                return LowerExpression(del.Body);
            default: return expr;
        }
    }
}
```

- [ ] **Step 2: Build and commit**

```bash
dotnet build src/CSharpToJava.Core/CSharpToJava.Core.csproj
git add src/CSharpToJava.Core/Lowering/
git commit -m "feat: add LowerOperator, LowerStruct, LowerEvent, LowerDelegate passes"
```


### Task C4: LowerUsing + LowerYield + LowerPatternMatch + VariableResolution

**Files:**
- Create: `src/CSharpToJava.Core/Lowering/LowerUsing.cs`
- Create: `src/CSharpToJava.Core/Lowering/LowerYield.cs`
- Create: `src/CSharpToJava.Core/Lowering/LowerPatternMatch.cs`
- Create: `src/CSharpToJava.Core/Lowering/VariableResolution.cs`

- [ ] **Step 1: 实现 LowerUsing**

```csharp
// src/CSharpToJava.Core/Lowering/LowerUsing.cs
using CSharpToJava.Core.Context;
using CSharpToJava.Core.Java2;

namespace CSharpToJava.Core.Lowering;

public class LowerUsing : ILoweringPass
{
    public string Name => "LowerUsing";

    public IrCompilationUnit Apply(IrCompilationUnit unit, ConversionContext context)
    {
        foreach (var type in unit.TypeDeclarations) LowerType(type);
        return unit;
    }

    private void LowerType(IrTypeDeclaration type)
    {
        foreach (var method in type.Methods)
            if (method.Body != null) LowerBlock(method.Body);
        if (type is IrClassDeclaration cls)
            foreach (var ctor in cls.Constructors)
                if (ctor.Body != null) LowerBlock(ctor.Body);
        foreach (var nested in type.NestedTypes) LowerType(nested);
    }

    private void LowerBlock(IrBlockStatement block)
    {
        var newStatements = new List<IrStatement>();
        foreach (var stmt in block.Statements)
        {
            if (stmt is IrCSharpUsingStatement usingStmt)
            {
                var resourceVar = usingStmt.Resource;
                var resourceExpr = usingStmt.ResourceExpression;
                var tryBody = new IrBlockStatement();
                if (resourceVar != null)
                    tryBody.Statements.Add(resourceVar);
                if (usingStmt.Body is IrBlockStatement b)
                    tryBody.Statements.AddRange(b.Statements);
                else
                    tryBody.Statements.Add(usingStmt.Body);

                var finallyBody = new IrBlockStatement();
                var resourceName = resourceVar?.Name ?? "_res";
                finallyBody.Statements.Add(new IrExpressionStatement
                {
                    Expression = new IrInvocationExpression
                    {
                        Target = new IrIdentifierExpression { Name = resourceName },
                        MethodName = "close",
                    }
                });

                newStatements.Add(new IrTryCatchStatement
                {
                    TryBody = tryBody,
                    FinallyBody = finallyBody,
                });
            }
            else
            {
                newStatements.Add(stmt);
            }
        }
        block.Statements.Clear();
        block.Statements.AddRange(newStatements);
        foreach (var s in block.Statements)
            if (s is not IrCSharpUsingStatement) LowerStatement(s);
    }

    private void LowerStatement(IrStatement stmt)
    {
        if (stmt is IrBlockStatement b) LowerBlock(b);
    }
}
```

```csharp
// src/CSharpToJava.Core/Lowering/LowerYield.cs
using CSharpToJava.Core.Context;
using CSharpToJava.Core.Java2;

namespace CSharpToJava.Core.Lowering;

public class LowerYield : ILoweringPass
{
    public string Name => "LowerYield";

    public IrCompilationUnit Apply(IrCompilationUnit unit, ConversionContext context)
    {
        foreach (var type in unit.TypeDeclarations) LowerType(type);
        return unit;
    }

    private void LowerType(IrTypeDeclaration type)
    {
        for (int i = 0; i < type.Methods.Count; i++)
        {
            var method = type.Methods[i];
            if (method.Body != null && HasYield(method.Body))
            {
                // Transform yield method to iterator pattern
                TransformYieldMethod(type, method, i);
            }
        }
        if (type is IrClassDeclaration cls)
            foreach (var ctor in cls.Constructors)
                if (ctor.Body != null) LowerBlock(ctor.Body);
        foreach (var nested in type.NestedTypes) LowerType(nested);
    }

    private bool HasYield(IrBlockStatement block)
    {
        foreach (var stmt in block.Statements)
        {
            if (stmt is IrCSharpYieldReturnStatement or IrCSharpYieldBreakStatement)
                return true;
            if (stmt is IrBlockStatement b && HasYield(b))
                return true;
        }
        return false;
    }

    private void TransformYieldMethod(IrTypeDeclaration type, IrMethodDeclaration method, int index)
    {
        // Replace yield statements with state-machine pattern
        // Generate a nested Iterator class with state field
        var iteratorName = method.Name + "Iterator";
        var elementType = method.ReturnType.Replace("Iterator<", "").TrimEnd('>');

        var iteratorClass = new IrClassDeclaration
        {
            Name = iteratorName,
            Modifiers = IrModifiers.Private | IrModifiers.Static,
            IsConvertedFromStruct = false,
        };

        // Add state field
        iteratorClass.Fields.Add(new IrFieldDeclaration
        {
            Type = "int",
            Name = "_state",
            Initializer = "0",
        });

        // Transform body: replace yield return -> state + break, yield break -> state = -1
        var newBody = TransformYieldBody(method.Body!, elementType);
        iteratorClass.Methods.Add(new IrMethodDeclaration
        {
            Name = "hasNext",
            ReturnType = "boolean",
            Body = new IrBlockStatement
            {
                Statements = { new IrReturnStatement { Expression = new IrBinaryExpression
                {
                    Left = new IrIdentifierExpression { Name = "_state" },
                    Operator = IrBinaryOp.GreaterThanOrEqual,
                    Right = new IrLiteralExpression { Value = "0" },
                }}}
            },
        });

        iteratorClass.Methods.Add(new IrMethodDeclaration
        {
            Name = "next",
            ReturnType = elementType,
            Body = newBody,
        });

        // Replace original method body with return new Iterator()
        method.Body = new IrBlockStatement
        {
            Statements =
            {
                new IrReturnStatement
                {
                    Expression = new IrNewExpression { TypeName = iteratorName },
                }
            }
        };

        type.NestedTypes.Add(iteratorClass);
    }

    private IrBlockStatement TransformYieldBody(IrBlockStatement original, string elementType)
    {
        var block = new IrBlockStatement();
        foreach (var stmt in original.Statements)
        {
            if (stmt is IrCSharpYieldReturnStatement yr)
            {
                block.Statements.Add(new IrExpressionStatement
                {
                    Expression = new IrAssignmentExpression
                    {
                        Target = new IrIdentifierExpression { Name = "_current" },
                        Value = yr.Expression ?? new IrLiteralExpression { Value = "null" },
                    }
                });
                block.Statements.Add(new IrExpressionStatement
                {
                    Expression = new IrAssignmentExpression
                    {
                        Target = new IrIdentifierExpression { Name = "_state" },
                        Value = new IrLiteralExpression { Value = "1" },
                    }
                });
                block.Statements.Add(new IrReturnStatement
                {
                    Expression = new IrIdentifierExpression { Name = "_current" },
                });
            }
            else if (stmt is IrCSharpYieldBreakStatement)
            {
                block.Statements.Add(new IrExpressionStatement
                {
                    Expression = new IrAssignmentExpression
                    {
                        Target = new IrIdentifierExpression { Name = "_state" },
                        Value = new IrLiteralExpression { Value = "-1" },
                    }
                });
                block.Statements.Add(new IrReturnStatement { Expression = new IrLiteralExpression { Value = "null" } });
            }
            else
            {
                block.Statements.Add(stmt);
            }
        }
        return block;
    }

    private void LowerBlock(IrBlockStatement block) { }
}
```

```csharp
// src/CSharpToJava.Core/Lowering/LowerPatternMatch.cs
using CSharpToJava.Core.Context;
using CSharpToJava.Core.Java2;

namespace CSharpToJava.Core.Lowering;

public class LowerPatternMatch : ILoweringPass
{
    public string Name => "LowerPatternMatch";

    public IrCompilationUnit Apply(IrCompilationUnit unit, ConversionContext context)
    {
        foreach (var type in unit.TypeDeclarations) LowerType(type);
        return unit;
    }

    private void LowerType(IrTypeDeclaration type)
    {
        foreach (var method in type.Methods)
            if (method.Body != null) LowerBlock(method.Body);
        if (type is IrClassDeclaration cls)
            foreach (var ctor in cls.Constructors)
                if (ctor.Body != null) LowerBlock(ctor.Body);
        foreach (var nested in type.NestedTypes) LowerType(nested);
    }

    private void LowerBlock(IrBlockStatement block)
    {
        for (int i = 0; i < block.Statements.Count; i++)
            block.Statements[i] = LowerStatement(block.Statements[i]);
    }

    private IrStatement LowerStatement(IrStatement stmt)
    {
        switch (stmt)
        {
            case IrBlockStatement b: LowerBlock(b); return b;
            case IrExpressionStatement es:
                es.Expression = LowerExpression(es.Expression);
                return es;
            case IrIfStatement ifs:
                ifs.Condition = LowerExpression(ifs.Condition);
                ifs.ThenBody = LowerStatement(ifs.ThenBody);
                if (ifs.ElseBody != null) ifs.ElseBody = LowerStatement(ifs.ElseBody);
                return ifs;
            default: return stmt;
        }
    }

    private IrExpression LowerExpression(IrExpression expr)
    {
        switch (expr)
        {
            case IrCSharpPatternExpression pat:
                if (pat.PatternKind == "type" && pat.MatchedType != null)
                    return new IrInstanceOfExpression
                    {
                        Expression = LowerExpression(pat.Subject),
                        TypeName = pat.MatchedType,
                        PatternVariable = pat.PatternVariable,
                    };
                if (pat.PatternKind == "property" && pat.PropertyChecks.Count > 0)
                {
                    // Expand property pattern: x is { A: a, B: b } -> x != null && x.A == a && x.B == b
                    IrExpression result = new IrBinaryExpression
                    {
                        Left = LowerExpression(pat.Subject),
                        Operator = IrBinaryOp.NotEquals,
                        Right = new IrLiteralExpression { Value = "null" },
                    };
                    foreach (var check in pat.PropertyChecks)
                    {
                        result = new IrBinaryExpression
                        {
                            Left = result,
                            Operator = IrBinaryOp.LogicalAnd,
                            Right = new IrBinaryExpression
                            {
                                Left = new IrMemberAccessExpression
                                {
                                    Target = LowerExpression(pat.Subject),
                                    MemberName = check.Property,
                                },
                                Operator = IrBinaryOp.Equals,
                                Right = LowerExpression(check.Value),
                            },
                        };
                    }
                    return result;
                }
                return expr;
            default: return expr;
        }
    }
}
```

```csharp
// src/CSharpToJava.Core/Lowering/VariableResolution.cs
using CSharpToJava.Core.Context;
using CSharpToJava.Core.Java2;

namespace CSharpToJava.Core.Lowering;

public class VariableResolution : ILoweringPass
{
    public string Name => "VariableResolution";

    public IrCompilationUnit Apply(IrCompilationUnit unit, ConversionContext context)
    {
        foreach (var type in unit.TypeDeclarations) LowerType(type);
        return unit;
    }

    private void LowerType(IrTypeDeclaration type)
    {
        foreach (var method in type.Methods)
            if (method.Body != null)
                RenameDuplicates(method.Body);
        if (type is IrClassDeclaration cls)
            foreach (var ctor in cls.Constructors)
                if (ctor.Body != null)
                    RenameDuplicates(ctor.Body);
        foreach (var nested in type.NestedTypes) LowerType(nested);
    }

    private void RenameDuplicates(IrBlockStatement block)
    {
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        var renames = new Dictionary<string, string>(StringComparer.Ordinal);

        // First pass: find duplicates
        FindDeclaredVars(block, seen, renames);

        // Second pass: apply renames
        if (renames.Count > 0)
            ApplyRenames(block, renames);
    }

    private void FindDeclaredVars(IrBlockStatement block, Dictionary<string, int> seen, Dictionary<string, string> renames)
    {
        foreach (var stmt in block.Statements)
        {
            if (stmt is IrVariableDeclarationStatement vd)
            {
                if (seen.TryGetValue(vd.Name, out var count))
                {
                    seen[vd.Name] = count + 1;
                    renames[vd.Name] = vd.Name + "_" + count;
                    vd.Name = renames[vd.Name];
                }
                else
                {
                    seen[vd.Name] = 0;
                }
            }
            if (stmt is IrBlockStatement inner) FindDeclaredVars(inner, seen, renames);
        }
    }

    private void ApplyRenames(IrBlockStatement block, Dictionary<string, string> renames)
    {
        foreach (var stmt in block.Statements)
        {
            ApplyRenamesToStatement(stmt, renames);
        }
    }

    private void ApplyRenamesToStatement(IrStatement stmt, Dictionary<string, string> renames)
    {
        // Walk expressions and rename identifiers
        if (stmt is IrExpressionStatement es)
            es.Expression = RenameInExpr(es.Expression, renames);
        if (stmt is IrBlockStatement b) ApplyRenames(b, renames);
    }

    private IrExpression RenameInExpr(IrExpression expr, Dictionary<string, string> renames)
    {
        if (expr is IrIdentifierExpression id && renames.TryGetValue(id.Name, out var newName))
            id.Name = newName;
        return expr;
    }
}
```

- [ ] **Step 2: Build and commit**

```bash
dotnet build src/CSharpToJava.Core/CSharpToJava.Core.csproj
git add src/CSharpToJava.Core/Lowering/
git commit -m "feat: add LowerUsing, LowerYield, LowerPatternMatch, VariableResolution passes"
```


### Task C5: Lowering Pass 单元测试

**Files:**
- Create: `tests/CSharpToJava.Tests/Java2/LoweringPassTests.cs`

- [ ] **Step 1: 写 Lowering Pass 测试**

```csharp
// tests/CSharpToJava.Tests/Java2/LoweringPassTests.cs
using Xunit;
using CSharpToJava.Core.Context;
using CSharpToJava.Core.Java2;
using CSharpToJava.Core.Lowering;
using CSharpToJava.TypeMapping;

namespace CSharpToJava.Tests.Java2;

public class LoweringPassTests
{
    [Fact]
    public void LowerProperty_ConvertsGetAccess()
    {
        var unit = new IrCompilationUnit();
        var cls = new IrClassDeclaration { Name = "Foo" };
        var irBlock = new IrBlockStatement();
        irBlock.Statements.Add(new IrReturnStatement
        {
            Expression = new IrCSharpPropertyAccessExpression
            {
                Target = new IrIdentifierExpression { Name = "obj" },
                PropertyName = "Name",
            }
        });
        cls.Methods.Add(new IrMethodDeclaration
        {
            Name = "test", ReturnType = "String", Body = irBlock,
        });
        unit.TypeDeclarations.Add(cls);

        var pass = new LowerProperty();
        var result = pass.Apply(unit, new ConversionContext(new ConversionOptions(), new TypeMappingRegistry(new TypeMappingConfig())));

        var retStmt = (IrReturnStatement)((IrClassDeclaration)result.TypeDeclarations[0]).Methods[0].Body!.Statements[0];
        var inv = Assert.IsType<IrInvocationExpression>(retStmt.Expression);
        Assert.Equal("getName", inv.MethodName);
        Assert.IsType<IrIdentifierExpression>(inv.Target);
    }

    [Fact]
    public void LowerUsing_ConvertsToTryFinally()
    {
        var unit = new IrCompilationUnit();
        var cls = new IrClassDeclaration { Name = "Foo" };
        var block = new IrBlockStatement();
        block.Statements.Add(new IrCSharpUsingStatement
        {
            Resource = new IrVariableDeclarationStatement { Type = "Stream", Name = "s" },
            Body = new IrExpressionStatement { Expression = new IrIdentifierExpression { Name = "doWork" } },
        });
        cls.Methods.Add(new IrMethodDeclaration
        {
            Name = "test", ReturnType = "void", Body = block,
        });
        unit.TypeDeclarations.Add(cls);

        var pass = new LowerUsing();
        var expectedResult = pass.Apply(unit, null!);
        var methodBody = ((IrClassDeclaration)expectedResult.TypeDeclarations[0]).Methods[0].Body!;
        var tc = Assert.IsType<IrTryCatchStatement>(methodBody.Statements[0]);
        Assert.NotNull(tc.FinallyBody);
    }
}
```

- [ ] **Step 2: Run tests**

```bash
dotnet test --filter "FullyQualifiedName~LoweringPassTests"
```
Expected: 2 PASS.

- [ ] **Step 3: Commit**

```bash
git add tests/CSharpToJava.Tests/Java2/LoweringPassTests.cs
git commit -m "test: add LoweringPass unit tests for LowerProperty and LowerUsing"
```


---

## Phase D: 管道集成

### Task D1: 新管道入口类

**Files:**
- Create: `src/CSharpToJava.Core/Pipeline/NewConversionPipeline.cs`

- [ ] **Step 1: 创建新管道**

```csharp
// src/CSharpToJava.Core/Pipeline/NewConversionPipeline.cs
using CSharpToJava.Core.Context;
using CSharpToJava.Core.HIR;
using CSharpToJava.Core.Java2;
using CSharpToJava.Core.Java2.CodeGen;
using CSharpToJava.Core.Lowering;
using CSharpToJava.TypeMapping;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.Pipeline;

public class NewConversionResult
{
    public bool Success { get; set; }
    public string GeneratedCode { get; set; } = "";
    public List<DiagnosticMessage> Diagnostics { get; set; } = new();
    public string? FileName { get; set; }
}

/// <summary>
/// New pipeline: Frontend -> HIR Generation -> Lowering -> Validation -> CodeGen.
/// Runs alongside the old pipeline until Phase E validation passes.
/// </summary>
public class NewConversionPipeline
{
    private readonly List<ILoweringPass> _loweringPasses;

    public NewConversionPipeline()
    {
        _loweringPasses = new List<ILoweringPass>
        {
            new LowerRefOut(),
            new LowerYield(),
            new LowerUsing(),
            new LowerProperty(),
            new LowerIndexer(),
            new LowerOperator(),
            new LowerEvent(),
            new LowerDelegate(),
            new LowerStruct(),
            new LowerPatternMatch(),
            new VariableResolution(),
        };
    }

    public NewConversionResult Convert(string sourceCode, ConversionOptions options, string? fileName = null)
    {
        var typeMappings = new TypeMappingRegistry(options.TypeMappingConfigPath);
        var context = new ConversionContext(options, typeMappings);
        var result = new NewConversionResult { FileName = fileName };

        try
        {
            // Phase 1: Frontend - Parse + Semantic Analysis
            var syntaxTree = CSharpSyntaxTree.ParseText(sourceCode);
            if (syntaxTree.GetDiagnostics().Any(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error))
            {
                foreach (var diag in syntaxTree.GetDiagnostics())
                    context.Diagnostics.Error(diag.GetMessage(), diag.Location);
                result.Diagnostics = context.Diagnostics.Messages.ToList();
                return result;
            }

            var compilation = CSharpCompilation.Create(
                "TempAssembly",
                new[] { syntaxTree },
                references: new[]
                {
                    MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
                    MetadataReference.CreateFromFile(typeof(System.Collections.Generic.List<>).Assembly.Location),
                    MetadataReference.CreateFromFile(typeof(Console).Assembly.Location),
                    MetadataReference.CreateFromFile(typeof(System.Linq.Enumerable).Assembly.Location),
                },
                options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

            context.SemanticModel = compilation.GetSemanticModel(syntaxTree);
            context.ProjectCompilation = compilation;

            // Phase 2: HIR Generation
            var root = (CompilationUnitSyntax)syntaxTree.GetRoot();
            var hirGenerator = new CSharpToJavaHIRGenerator();
            IrCompilationUnit ir = hirGenerator.Generate(root, context);

            // Phase 3: Lowering
            foreach (var pass in _loweringPasses)
            {
                ir = pass.Apply(ir, context);
            }

            // Phase 4: Validation - check no CSharp* nodes remain
            var csharpNodes = FindCSharpExtensionNodes(ir);
            foreach (var node in csharpNodes)
            {
                context.Diagnostics.Error(
                    "Remaining C# extension node after Lowering: " + node.GetType().Name,
                    code: "CS2J5001");
            }

            // Phase 5: CodeGen
            var codeGen = new JavaCodeGenerator();
            var javaCode = codeGen.Generate(ir);

            result.Success = context.Diagnostics.Messages.All(m => m.Severity != Context.DiagnosticSeverity.Error);
            result.GeneratedCode = javaCode;
            result.Diagnostics = context.Diagnostics.Messages.ToList();
        }
        catch (Exception ex)
        {
            context.Diagnostics.Error("Pipeline error: " + ex.Message);
            result.Diagnostics = context.Diagnostics.Messages.ToList();
        }

        return result;
    }

    private static List<IrNode> FindCSharpExtensionNodes(IrNode root)
    {
        var found = new List<IrNode>();
        CollectCSharpNodes(root, found);
        return found;
    }

    private static void CollectCSharpNodes(IrNode node, List<IrNode> found)
    {
        var typeName = node.GetType().Name;
        if (typeName.StartsWith("IrCSharp") || typeName.StartsWith("CSharp"))
            found.Add(node);
    }
}
```

- [ ] **Step 2: Build and commit**

```bash
dotnet build src/CSharpToJava.Core/CSharpToJava.Core.csproj
git add src/CSharpToJava.Core/Pipeline/NewConversionPipeline.cs
git commit -m "feat: add NewConversionPipeline integrating HIR + Lowering + CodeGen"
```


### Task D2: 新管道接入 CLI

**Files:**
- Modify: `src/CSharpToJava.CLI/Program.cs` (add --new-pipeline flag)

- [ ] **Step 1: 在 CLI 中添加新管道选项**

Modify the convert command to support running both old and new pipelines for comparison:

```bash
# Add to CLI args: --new-pipeline flag
dotnet run --project src/CSharpToJava.CLI/CSharpToJava.CLI.csproj -- convert -i Input.cs -o Output.java --new-pipeline
```

Add to `Program.cs`:
```csharp
// src/CSharpToJava.CLI/Program.cs (add near convert command)
if (args.Contains("--new-pipeline"))
{
    var pipeline = new NewConversionPipeline();
    var result = pipeline.Convert(sourceCode, options, inputPath);
    outputCode = result.GeneratedCode;
    if (result.Diagnostics.Any(d => d.Severity == Context.DiagnosticSeverity.Error))
        Console.Error.WriteLine("Errors: " + string.Join("\n", result.Diagnostics.Select(d => d.Message)));
}
```

- [ ] **Step 2: Commit**

```bash
git add src/CSharpToJava.CLI/
git commit -m "feat: add --new-pipeline flag to CLI for side-by-side comparison"
```


---

## Phase E: 回归验证

### Task E1: 运行现有测试套件

- [ ] **Step 1: 运行全部测试确保旧管道不退化**

```bash
dotnet test
```
Expected: all existing tests PASS.

- [ ] **Step 2: 创建对比测试 — 新旧管道产生相同输出**

```csharp
// tests/CSharpToJava.Tests/Java2/PipelineComparisonTests.cs
using Xunit;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests.Java2;

public class PipelineComparisonTests
{
    [Theory]
    [InlineData("class Empty { }")]
    [InlineData("class Point { int x; int y; }")]
    [InlineData("class Calc { int Add(int a, int b) { return a + b; } }")]
    public void NewPipeline_ProducesValidJavaCode(string csharpCode)
    {
        var options = new ConversionOptions();
        var pipeline = new NewConversionPipeline();
        var result = pipeline.Convert(csharpCode, options);

        Assert.True(result.Success, "Pipeline failed: " + string.Join("; ", result.Diagnostics.Select(d => d.Message)));
        Assert.NotEmpty(result.GeneratedCode);
        // Basic sanity: Java output shouldn't contain raw C# tokens
        Assert.DoesNotContain("var ", result.GeneratedCode);
        Assert.DoesNotContain("using ", result.GeneratedCode);
    }
}
```

- [ ] **Step 3: Run comparison tests**

```bash
dotnet test --filter "FullyQualifiedName~PipelineComparisonTests"
```
Expected: all PASS.

- [ ] **Step 4: 用 MSAGL 实际项目验证新管道**

```bash
dotnet run --project src/CSharpToJava.CLI/CSharpToJava.CLI.csproj -- convert-project -s path/to/msagl-core -d output/new-pipeline --new-pipeline
```

- [ ] **Step 5: Commit**

```bash
git add tests/CSharpToJava.Tests/Java2/PipelineComparisonTests.cs
git commit -m "test: add pipeline comparison and regression tests"
```


### Task E2: 清理 + 移除旧代码

- [ ] **Step 1: 移除 20+ 旧 Rewriter 文件**

Once the new pipeline passes MSAGL validation, delete old Rewriter files:

```bash
git rm src/CSharpToJava.Core/Java/Rewriters/OperatorPrecedenceRewriter.cs
git rm src/CSharpToJava.Core/Java/Rewriters/MapEntryTypeRewriter.cs
git rm src/CSharpToJava.Core/Java/Rewriters/MemberwiseCloneRewriter.cs
git rm src/CSharpToJava.Core/Java/Rewriters/DelegateInvocationRewriter.cs
git rm src/CSharpToJava.Core/Java/Rewriters/MathMethodRewriter.cs
git rm src/CSharpToJava.Core/Java/Rewriters/GenericArrayCreationRewriter.cs
git rm src/CSharpToJava.Core/Java/Rewriters/IntStreamBoxedRewriter.cs
git rm src/CSharpToJava.Core/Java/Rewriters/ArrayIterableConversionRewriter.cs
git rm src/CSharpToJava.Core/Java/Rewriters/CollectStreamRoundtripRewriter.cs
git rm src/CSharpToJava.Core/Java/Rewriters/StopwatchApiRewriter.cs
git rm src/CSharpToJava.Core/Java/Rewriters/EventHandlerLambdaRewriter.cs
git rm src/CSharpToJava.Core/Java/Rewriters/ExceptionApiRewriter.cs
git rm src/CSharpToJava.Core/Java/Rewriters/StringConcatRewriter.cs
git rm src/CSharpToJava.Core/Java/Rewriters/VariableNameDeduplicationRewriter.cs
git rm src/CSharpToJava.Core/Java/Rewriters/ImplicitCastCompletionRewriter.cs
```

- [ ] **Step 2: 移除 JavaRawExpression 和 JavaRawStatement**

Update `JavaExpression.cs` and `JavaStatement.cs` to mark these as `[Obsolete]`:
```csharp
[Obsolete("Use IrExpression from Java2 namespace instead")]
public class JavaRawExpression : JavaExpression { ... }
```

- [ ] **Step 3: 确保旧测试仍然通过**

```bash
dotnet test
```
Expected: all tests PASS (Rewriter tests referencing removed files are also removed).

- [ ] **Step 4: Commit**

```bash
git add -A
git commit -m "chore: remove old IR Rewriters, deprecate JavaRawExpression/JavaRawStatement"
```


### Task E3: 文档更新

- [ ] **Step 1: 更新 CLAUDE.md 架构说明**

修改 `CLAUDE.md` 添加新架构描述：
```
## Architecture (v2)
The converter follows a five-phase pipeline:
1. Frontend: C# parsing + LINQ rewrite + partial type merging
2. HIR Generation: C# Syntax Tree -> C#-flavored Java IR
3. Lowering: 12 semantic lowering passes eliminate C#-specific semantics
4. Validation: Diagnostics-only IR checks
5. CodeGen: Pure Java IR -> formatted Java source

New code lives in:
- src/CSharpToJava.Core/Java2/ - New IR model + CodeGen
- src/CSharpToJava.Core/HIR/ - HIR Generator
- src/CSharpToJava.Core/Lowering/ - Semantic lowering passes
```

- [ ] **Step 2: Commit**

```bash
git add CLAUDE.md
git commit -m "docs: update CLAUDE.md with new architecture"
```

---

## 清理辅助文件

```bash
rm d:/cs2j/docs/superpowers/plans/_append.py
rm d:/cs2j/docs/superpowers/plans/_append2.py
rm d:/cs2j/docs/superpowers/plans/_append3.py
rm d:/cs2j/docs/superpowers/plans/_append4.py
rm d:/cs2j/docs/superpowers/plans/_append5.py
```
