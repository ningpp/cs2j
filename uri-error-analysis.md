# URI Conversion Error Analysis

## Iteration 1 — 多维数组初始化维度丢失
- **Java 文件** : `d:\cs-uri-20260725\system-private-uri-functional-tests\src\test\java\dotnet\system\PrivateUri\Tests\IriEncodingDecodingTest.java`
- **行号** : 26
- **错误信息** : `java.lang.String的初始化程序不合法` / `不兼容的类型: java.lang.String[]无法转换为java.lang.String[][]`
- **代码片段** :
  ```java
  private static String[][] RFC3986CompliantDecoding = new String[] {  
      { "%3B%2F%3F%3A%40%26%3D%2B%24%2C", "%3B%2F%3F%3A%40%26%3D%2B%24%2C" },
      ...
  };
  ```
- **对应 C# 文件** : `D:\csharpuri\tests\FunctionalTests\IriEncodingDecodingTests.cs`
- **根因分类** : Transformer 逻辑缺陷
- **涉及组件** :
  - `src/CSharpToJava.Core/Transformers/Expression/Transformers/ObjectCreationTransformer.cs` — `TransformArrayInitializer` / `ResolveBareArrayInitializerElementType`（旧 IR 管道，实际触发 URI 转换错误）
  - `src/CSharpToJava.Core/HIR/HIRExpressionGenerator.cs` — `GenerateArrayCreation`（新 HIR 管道，同类隐患）
- **分析** : C# 矩形数组 `string[,]` 在声明处已被正确映射为 `String[][]`，但裸字段初始化 `{ ... }` 被 `TransformArrayInitializer` 包装时始终只追加一组 `[]`（`new {javaElementType}[] { ... }`），导致生成 `new String[] { ... }`，类型不兼容。同步修复新 HIR 管道中 `GenerateArrayCreation` 对多维数组只加一组 `[]` 的同类问题。
- **修复** :
  - `ResolveBareArrayInitializerElementType` 增加 `out int rankCount`，从语义模型或语法推导出数组最外层维度数。
  - `TransformArrayInitializer` 增加 `rankCount` 参数，裸初始化时按实际维度数生成 `[]`。
  - `GenerateArrayCreation` 按 `RankSpecifiers` 累计维度数生成对应数量的 `[]`。
- **状态** : ✅ Fixed
- **验证** :
  - 单元测试 `MultidimensionalArrayInitializerTests.RectangularStringArrayField_BareInitializer_EmitsCorrectJavaRanks` 红→绿通过。
  - 全量 `dotnet test` 2325 个测试全部通过。
  - 重新转换后 `mvn clean package -e` 返回 `BUILD SUCCESS`（日志见 `d:\code\cs2j\mvn-build-uri.out.log`）。
