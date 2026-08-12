# URI ReadOnly 迭代修复分析记录

## Iteration 1 — CS0305 泛型缺参 [build]
- **C# 文件**: d:\cs-uri-812\.cs2j-readonly-src\src\Common\System\Collections\Generic\ArrayBuilder.cs
- **行号**: 68, 140, 151, 182, 183
- **错误信息**: error CS0305: 使用泛型 类型"ArrayBuilder<T>"需要 1 个类型参数
- **代码片段**:
  ```csharp
  public ArrayBuilder Add(T item)          // ← 应为 ArrayBuilder<T>
  ...
  public ArrayBuilder With_array(T[] _array) => new ArrayBuilder(_array: _array, _count: this._count);
  ```
- **对应原始 C# 文件**: src\Common\System\Collections\Generic\ArrayBuilder.cs
- **根因分类**: 方法迁移
- **涉及组件**:
  - src\CSharpToJava.Core\ReadOnlyStructMaker\MethodMigrator.cs (83, 93 行生成裸 struct 名)
  - src\CSharpToJava.Core\ReadOnlyStructMaker\ReadOnlyStructRewriter.cs (602, 606, 782 行)
  - src\CSharpToJava.Core\ReadOnlyStructMaker\ReadOnlyStructMaker.cs (With_ 检测)
  - src\CSharpToJava.Core\ReadOnlyStructMaker\CallSiteUpdater.cs (383 行 default())
- **分析**: 泛型 struct `ArrayBuilder<T>` 的 void 方法迁移/WithXxx 生成把返回类型与对象创建写成裸 `ArrayBuilder`（缺 `<T>`），产生 CS0305。所有生成类型处需携带类型参数。
- **修复**:
  1. `MethodMigrator` 新增 `TypeSyntax? structType` 参数，替代裸 `IdentifierName(structName)`
  2. `ReadOnlyStructRewriter` 新增 `StructTypeSyntax(node)` helper（有 TypeParameterList 时生成 `GenericName`），用于 WithXxx 返回类型、`new ArrayBuilder<T>(...)`、setter 版 WithXxx
  3. `ReadOnlyStructMaker` With_ 方法检测同时接受 `GenericNameSyntax`（并修复 `&&`/`||` 优先级引入的匹配错误）
  4. `CallSiteUpdater` 新增 `_structTypeParams` 字典，`default(...)` 生成完整泛型类型
- **测试**: `GenericStruct_MigratedMethodsKeepTypeParameter`、`GenericStruct_SetterWithMethod_KeepsTypeParameter`（先红后绿）
- **验证**: 全量 dotnet test 2683+691+7 = 3381 通过，0 失败
- **状态**: ✅ Fixed

## Iteration 2 — Segments 空数组 [test]
- **C# 文件**: d:\cs-uri-812\.cs2j-readonly-src\tests\ExtendedFunctionalTests\UriTests.cs
- **行号**: 88（`Assert.Equal<int>(ss.Length, 3)`，Expected 0 / Actual 3）
- **错误信息**: Assert.Equal() Failure: Values differ — Expected: 0, Actual: 3（uri.Segments 返回空数组）
- **代码片段**:
  ```csharp
  var pathSegments = new ArrayBuilder<string>();
  ...
  pathSegments.Add(path.Substring(current, (next - current) + 1));  // ← 未被改写为赋值
  ...
  segments = pathSegments.ToArray();
  ```
- **对应原始 C# 文件**: src\System\Uri.cs（Segments getter）
- **根因分类**: CallSite
- **涉及组件**: src\CSharpToJava.CLI\ProjectReadOnlyStructPreprocessor.cs (933-950, 1095 行)
- **分析**: 项目级 `UpdateCrossFileCallSites` 扫描重写后树时只识别 `IdentifierNameSyntax` 返回类型；Iteration 1 修复后泛型 struct 的迁移方法返回 `ArrayBuilder<T>`（GenericNameSyntax），Add 未被登记为已迁移方法 → `migratedMethodToStruct.Count == 0` → 整个调用点更新 pass 直接 return → `pathSegments.Add(...)` 结果被丢弃，Segments 恒为空。且诊断 StructName 为 "ArrayBuilder<T>"（ToDisplayString 带泛型参数），与扫描端裸标识符 "ArrayBuilder" 不匹配导致门禁失配，整轮扫描被跳过。
- **修复**:
  1. `UpdateCrossFileCallSites` 改为 internal 可测试
  2. 返回类型/out 参数类型/WithXxx 检测同时接受 `GenericNameSyntax`
  3. 门禁容忍带泛型参数的 key（"ArrayBuilder<T>"），并在构建 `globalMigratedMethods` 时规范化 key（剥离 `<...>`）
- **测试**: `GenericMigratedStruct_VoidCallSite_UpdatedToAssignment`（先红后绿；跨文件 pass 输出无空格 `pathSegments=pathSegments.Add(`）
- **验证**: 全量 dotnet test 2684+691+7 = 3382 通过，0 失败；`make-readonly` Release 自检 build+test 全部成功（686 测试 0 失败）
- **状态**: ✅ Fixed
