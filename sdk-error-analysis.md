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

## 最终复验（2026-08-16）

- 使用同一份转换器代码重新转换 `D:\csharp-sdk` → `d:\cs-java-816-sdk`
- `mvn clean install -e` → **BUILD SUCCESS**
- 全量测试：`dotnet test -c Release` → **2826 + 51 全部通过，0 失败**
