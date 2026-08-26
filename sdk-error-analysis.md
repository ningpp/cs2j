# SDK Conversion Error Analysis

## Iteration 1 — final 字段重复赋值（value class 隐式 final 降级失效）
- **Java 文件**: `system-private-uri/src/main/java/dotnet/system/Collections/Generic/ArrayBuilder.java`
- **行号**: 59
- **错误信息**: `/D:/cs-java-816-sdk/system-private-uri/src/main/java/dotnet/system/Collections/Generic/ArrayBuilder.java:[59,9] 可能已分配变量_array`
- **代码片段**:
  ```java
  public ArrayBuilder(int capacity, Class<?> tClass) {
          this.tClass = tClass;
          this._array = null;
          this._count = 0;
          // Debug.Assert(capacity >= 0);
          if (capacity > 0) {
          _array = (T[]) TypeHelper.newArrayInstance(tClass, capacity);
          }
      }
  ```
- **对应 C# 文件**: `D:\csharp-sdk\uri\src\Common\System\Collections\Generic\ArrayBuilder.cs`
- **根因分类**: Transformer
- **涉及组件**: `src/CSharpToJava.Core/Transformers/Type/StructTransformer.cs`（`FixReadonlyStructConstructorChaining` / `DropFinalFromRepeatedlyAssignedFields`）
- **分析**: readonly struct 在转成 value class 时，`ArrayBuilder(int capacity) : this()` 的 `this()` 链被 `AddRuntimeClassConstructorParameters` 改写成 `this(tClass)`，随后被 `FixReadonlyStructConstructorChaining` 内联，把默认构造函数中的 `this._array = null` 拼进参数化构造函数，接着 `if` 内再次给 `_array` 赋值。普通类场景下 `DropFinalFromRepeatedlyAssignedFields` 会把 `_array` 降为非 final 绕过该错误；但 value class 的实例字段隐式 final，降级后仍然非法。同类问题还包括：final 字段只在 `if` 内被赋值（无顶层默认赋值）时，javac 报“可能尚未初始化变量”。
- **状态**: ✅ Fixed

## Iteration 2 — `First()/Last()` 返回数组后误加 `.boxed()`
- **错误信息**: 回归测试 `FirstElementContainsOnArrayTests.cs` 失败：`result.First().Contains(1)` 生成 `...findFirst().orElseThrow().boxed()...`，`orElseThrow()` 已返回 `int[]`，`int[].boxed()` 非法。
- **根因分类**: Transformer
- **涉及组件**: `src/CSharpToJava.Core/Transformers/Expression/Transformers/InvocationExpressionTransformer.cs`
- **分析**: `Contains` 处理用 `ContainsStreamMethodAtTopLevel` 判断 Java 接收表达式是否已是流；`...findFirst().orElseThrow()` 含 `.stream()` 被误判为流，但该链终态是数组元素，导致追加非法 `.boxed()`。
- **修复**: C# 接收表达式为返回数组的方法调用（`First()`/`Last()`/`ToArray()` 等）时，统一用 `Arrays.stream(receiver).boxed().collect(Collectors.toSet()).contains(...)`；直接 `int[].Contains(...)` 的双重包装保护保持不变。
- **状态**: ✅ Fixed

## 最终复验（2026-08-16 第三次）

- 使用同一份转换器代码重新转换 `D:\csharp-sdk` → `d:\cs-java-816-sdk`
- `mvn clean install -e` → **BUILD SUCCESS**（7102 个测试，0 失败）
- 同一份代码同时验证 AGL：`mvn clean package -e` → **BUILD SUCCESS**（609 个测试，0 失败 0 错误；`CSharpGenericIterable.from(null)` 修复后 Rectilinear 10 个 NPE 全部消失）
- compat：`mvn test` → **1500 个测试全部通过，0 失败**

## Iteration 3 — 局部函数捕获变量 IsWritten 传递闭包未提升
- **Java 文件**: `dotnet-text-regularexpressions/src/main/java/dotnet/text/regularexpressions/RegexCompiler.java`
- **行号**: 2084（首批错误之一，同模式共 14+ 处）
- **错误信息**: `不兼容的类型: io.github.ningpp.compat.Label无法转换为io.github.ningpp.compat.ObjectHolder<io.github.ningpp.compat.Label>`（同模式还有 `int无法转换为io.github.ningpp.compat.IntHolder`）
- **代码片段**:
  ```java
  // emitNode 参数 _cs2j_cap_doneLabel 类型为 Label（只读）
  // 但 emitSingleCharAtomicLoop 参数 _cs2j_cap_doneLabel 类型为 ObjectHolder<Label>（写入）
  // emitNode 内调用 emitSingleCharAtomicLoop 时传入 Label → 类型不兼容
  private void emitNode(RegexNode node, Label _cs2j_cap_doneLabel, ...) {
      case RegexNode.Oneloopatomic, ...:
      emitSingleCharAtomicLoop(node, _cs2j_cap_doneLabel, ...); // ← Label 传给 ObjectHolder<Label>
  }
  ```
- **对应 C# 文件**: `D:\csharp-sdk\regular-expressions\src\System\Text\RegularExpressions\RegexCompiler.cs`
- **根因分类**: Transformer
- **涉及组件**: `src/CSharpToJava.Core/Transformers/Statement/StatementTransformer.LocalFunctions.cs`（`PrescanLocalFunctions` 传递闭包）
- **分析**: C# 局部函数 `EmitNode` 只读取捕获变量 `doneLabel`（`IsWritten=false`），但它调用的 `EmitSingleCharAtomicLoop` 写入 `doneLabel`（`IsWritten=true`）。`PrescanLocalFunctions` 的 fixpoint 传播 callee 的 captures 给 caller 时，如果 caller 已有同名 capture（`IsWritten=false`），直接跳过不更新——导致 caller 的参数类型保持为 `Label` 而非 `ObjectHolder<Label>`，调用 callee 时传 `Label` 给期望 `ObjectHolder<Label>` 的参数。
