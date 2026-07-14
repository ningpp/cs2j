# YamlDotNet Error Analysis

## Iteration 1 — Supplier<Object> 类型推导缺失

- **Java 文件**: YamlDotNet/Core/AnchorName.java
- **行号**: 67
- **错误信息**: 不兼容的类型: 条件表达式中的类型错误, java.lang.Object无法转换为java.lang.String
- **代码片段**:
  ```java
  public String getValue() { return value != null ? value : ((java.util.function.Supplier<Object>) () -> { throw new IllegalStateException("Cannot read the Value of an empty anchor"); }).get(); }
  ```

- **对应 C# 文件**: YamlDotNet/Core/AnchorName.cs
- **根因分类**: Transformer 逻辑缺陷
- **涉及组件**: src/CSharpToJava.Core/Transformers/Expression/Transformers/ControlFlowTransformer.cs (L291-336)
- **分析**: `TransformThrowExpression` 方法在确定 `Supplier<T>` 的类型参数 `T` 时，只处理了原始类型（int→Integer, long→Long 等），对于 String 等引用类型默认回退到 `Object`，导致 `Supplier<Object>.get()` 返回 Object 而非 String，在三元表达式中类型不兼容。C# 原始代码 `value ?? throw new InvalidOperationException(...)` 中 throw 表达式的上下文类型是 `string`，应生成 `Supplier<String>`。

✅ Fixed (1556 → 1410 errors)

## Iteration 2 — 只读属性构造函数赋值生成不存在 setter

- **Java 文件**: YamlDotNet/Core/YamlException.java
- **行号**: 62
- **错误信息**: 找不到符号 - 方法 setStart(YamlDotNet.Core.Mark)
- **代码片段**:
  ```java
  public YamlException(Mark start, Mark end, String message, RuntimeException innerException) {
      super(message, innerException);
      setStart(start);
      setEnd(end);
  }
  ```

- **对应 C# 文件**: YamlDotNet/Core/YamlException.cs
- **根因分类**: Transformer 逻辑缺陷
- **涉及组件**: src/CSharpToJava.Core/Transformers/Expression/Transformers/AssignmentTransformer.cs
- **分析**: C# 只读属性 (`Mark Start { get; }`) 在构造函数中可以赋值 (`Start = start;`)，但 Java 没有等价特性。转换器 `AssignmentTransformer` 对所有属性赋值统一生成 `setXxx()` 调用，但只读属性不生成 setter 方法。修复：当属性没有 setter (`SetMethod == null`) 时，生成 `this.field = value` 直接字段赋值。影响 118 个 setter 缺失错误。

✅ Fixed (1410 → 1260 errors)

## Iteration 3 — Struct 中 compareTo(Object) 与 Comparable<T> 桥方法冲突

- **Java 文件**: YamlDotNet/Core/Mark.java
- **行号**: 111
- **错误信息**: 名称冲突: Mark 中的 compareTo(Object) 和 Comparable 中的 compareTo(Mark) 具有相同疑符
- **代码片段**:
  ```java
  public int compareTo(Object obj) { return compareTo((Mark)obj); }
  public int compareTo(Mark other) { ... }
  ```

- **对应 C# 文件**: YamlDotNet/Core/Mark.cs
- **根因分类**: Transformer 逻辑缺陷
- **涉及组件**: src/CSharpToJava.Core/Transformers/Type/StructTransformer.cs
- **分析**: C# struct `Mark` 同时实现 `IComparable<Mark>` 和 `IComparable`，生成两个 `compareTo` 方法。Java 中 `Comparable<Mark>` 会自动生成桥方法 `compareTo(Object)`，与显式的 `compareTo(Object)` 冲突。`ClassTransformer` 已有 `RemoveCompareToBridgeConflicts` 处理此问题，但 `StructTransformer` 未调用。修复：在 `StructTransformer` 中添加 `ClassTransformer.RemoveCompareToBridgeConflicts` 调用。影响 10 个名称冲突错误中的 compareTo 类。

✅ Fixed (compareTo 冲突已消除，1260 → 1298 因转换文件数变化，但实际 Mark 冲突已修复)
