# C# → Java 转换错误分析日志

## Iteration 1 — InvalidCastException (MemberAccessExpressionSyntax → TypeSyntax)
- **Java 文件**: N/A（转换器在 C#→Java 转换阶段的 ReadOnlyStructMaker 预处理中崩溃，未产出 Java 文件）
- **行号**: MethodMigrator.cs:130（`VisitLocalDeclarationStatement` 调用 `base.Visit...` 时下层 Roslyn `VisitVariableDeclaration` 将 visit 结果强转回 `TypeSyntax` 失败）
- **错误信息**: `Unable to cast object of type 'Microsoft.CodeAnalysis.CSharp.Syntax.MemberAccessExpressionSyntax' to type 'Microsoft.CodeAnalysis.CSharp.Syntax.TypeSyntax'.`
- **代码片段**:
  ```csharp
  // WriteStackFrame.InitializeReEntry
  public JsonConverter InitializeReEntry(Type type, JsonSerializerOptions options, string? propertyName = null)
  {
      JsonClassInfo classInfo = options.GetOrAddClass(type);   // ← 局部声明的类型名 = 结构体字段名
      ...
  }
  // WriteStackFrame 结构体字段: public JsonClassInfo JsonClassInfo;  // 字段名与类型名相同
  ```
- **对应 C# 文件**: `d:\csharpjson\src\System\Text\Json\Serialization\WriteStackFrame.cs`
- **根因分类**: Transformer（ReadOnlyStructMaker 重写器逻辑缺陷）
- **涉及组件**: `src/CSharpToJava.Core/ReadOnlyStructMaker/MethodMigrator.cs` — `ImplicitFieldToResultRewriter.VisitIdentifierName`（第 145-163 行）
- **分析**: `WriteStackFrame` 结构体存在与类型同名（无下划线）的公共字段（如 `JsonClassInfo JsonClassInfo;`、`MetadataPropertyName MetadataPropertyName;`）。`ImplicitFieldToResultRewriter` 将方法体内所有与字段/属性同名的标识符一律重写为 `result.<name>` 成员访问表达式，但未区分**类型位置**（局部变量声明的类型、泛型实参等）。局部声明 `JsonClassInfo classInfo = ...` 的类型 `JsonClassInfo` 被改写成 `result.JsonClassInfo`（MemberAccessExpression），而 Roslyn `CSharpSyntaxRewriter.VisitVariableDeclaration` 内建强转 `(TypeSyntax)Visit(node.Type)` 随即抛出 InvalidCastException，导致整个转换进程崩溃。

- **状态**: ✅ Fixed

## Iteration 2 — ArgumentException: Syntax node is not within syntax tree
- **Java 文件**: N/A（ReadOnlyStructMaker 预处理阶段崩溃）
- **行号**: StructAnalyzer.cs:470（`_model.GetDeclaredSymbol(method)`）
- **错误信息**: `Syntax node is not within syntax tree`
- **代码片段**:
  ```csharp
  // StructAnalyzer.AnalyzeCore — 成员遍历
  var methodSyntax = method.DeclaringSyntaxReferences
      .Select(r => r.GetSyntax()).OfType<MethodDeclarationSyntax>().FirstOrDefault();  // ← 可能选中 ref 桩声明
  ...
  if (IsMutating(methodSyntax))   // ← _model.GetDeclaredSymbol(methodSyntax) 因节点不在当前树而抛异常
  ```
- **对应 C# 文件**: `d:\csharpjson\ref\System.Text.Json.cs`（GenAPI 风格的 ref 程序集桩文件，与 `d:\csharpjson\src\System\Text\Json\Document\JsonElement.ArrayEnumerator.cs` 的 `ArrayEnumerator` 为 partial 合并）
- **根因分类**: 语义丢失（转换预处理把 ref 桩程序集文件并入语义编译，导致 partial 类型符号的 `DeclaringSyntaxReferences` 首项落在桩文件中）
- **涉及组件**: `src/CSharpToJava.Core/ReadOnlyStructMaker/StructAnalyzer.cs`、`src/CSharpToJava.CLI/ProjectConversionExclusionPlanner.cs`、`src/CSharpToJava.Core/ReadOnlyStructMaker/ReadOnlyStructRewriter.cs:915`
- **分析**: `ref\System.Text.Json.cs` 是 ref 程序集桩（`AssemblyName=dotnet.text.json.Ref`，`EnableDefaultCompileItems=false`，仅含 API 表面与 `throw null` 实现）。预处理器将其纳入语义编译后，`JsonElement.ArrayEnumerator.Dispose` 的合并符号把 ref 桩声明列为第一个 `DeclaringSyntaxReference`；`AnalyzeCore` 取其语法节点传给 `IsMutating`，而 `_model` 绑定的是 src 文件树 → `GetDeclaredSymbol` 抛 "Syntax node is not within syntax tree"，转换进程崩溃。若放任不管，ref 桩文件还会被转换成重复的 Java 类型（重复类错误）。

- **状态**: ✅ Fixed

## Iteration 3 — 无法找到合适的方法 / 不兼容的类型 (deserialize 重载被丢弃)
- **Java 文件**: system-text-json/src/main/java/dotnet/text/json/JsonSerializer.java
- **行号**: 762, 807, 1172, 1391
- **错误信息**:
  - `[762,16] 对于deserialize(java.lang.Class<TValue>,java.lang.String,java.lang.Class<?>,dotnet.text.json.JsonSerializerOptions), 找不到合适的方法`
  - `[807,24] 对于deserialize(java.lang.Class<java.lang.Object>,java.lang.String,java.lang.Class,dotnet.text.json.JsonSerializerOptions), 找不到合适的方法`
- **代码片段**:
  ```java
  public static <TValue> TValue deserialize(Class<TValue> _cs2j_TValue, String json, JsonSerializerOptions options) {
      if (json == null) { throw new ArgumentNullException("json"); }
      return deserialize(_cs2j_TValue, json, TypeHelper.toWrapperType(_cs2j_TValue), options);   // ← 引用不存在的 4 参方法
  }
  public static Object deserialize_0tp(String json, Class returnType, JsonSerializerOptions options) {
      ...
      Object value = deserialize(Object.class, json, returnType, options);   // ← 同样引用不存在的 4 参方法
      return value;
  }
  ```
- **对应 C# 文件**: `d:\csharpjson\src\System\Text\Json\Serialization\JsonSerializer.Read.String.cs`
- **根因分类**: Transformer（partial 成员去重键缺陷）
- **涉及组件**: `src/CSharpToJava.Core/Transformers/Type/ClassTransformer.cs:346`（`seenMemberKeys` 的 `ParamKey` 去重键只含参数语法类型，不含泛型 arity）
- **分析**: JsonSerializer 是跨 ~15 文件的 partial 类，成员去重键 `m:{iface}:{name}:{paramTypes}` 对 `Deserialize<TValue>(string, Type, options)`（private, 1 个类型参数）与 `Deserialize(string, Type, options)`（public, 0 个类型参数）产生完全相同键，后者先被处理 → 前者作为"重复"被丢弃。但调用点仍按"泛型方法调用前置 Class 令牌"约定生成 `deserialize(Class<TValue>, String, Class, options)` 4 参调用，与输出中的任何方法都不匹配 → 编译失败。
- **状态**: 🔴 待修复
