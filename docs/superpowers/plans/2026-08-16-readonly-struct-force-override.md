# ReadOnlyStruct 配置驱动强制转换 + LINQ First/Last 数组 Contains 修复

**日期**: 2026-08-16

## 1. 背景与目标

`E:\agl-master\GraphLayout\MSAGL\Core\Geometry\Point.cs` 是带 `[Serializable]`、含公共可变字段 `X`/`Y` 的普通 struct。`ReadOnlyStructMaker` 的反射安全规则（boxing / 序列化特性）会把它跳过，导致生成的 Java 是可变 `public class Point`，而不是 readonly struct 对应的 value class。

目标：

1. 用同一份 cs2j 代码把 Point 正确转换为 readonly struct（Java value class，final 字段 + withX/withY）。
2. 子任务一（SDK 转换 + `mvn clean install -e`）与子任务二（AGL 转换 + `mvn clean package -e`）全部成功。
3. 全量 `dotnet test -c Release` 无失败。

## 2. 设计决策

### 2.1 配置驱动的 force-readonly 覆盖

- `ReadOnlyStructMakerOptions` 新增 `ForceReadOnlyQualifiedNames`（完全限定名集合，默认空）。
- `StructAnalyzer` 命中集合时仅绕过反射安全（boxing / 序列化特性）跳过规则；其余不可转换守卫（ref/out 外部字段访问、构造函数多赋值、`this` 赋值、接口迁移等）仍然生效。
- 新增默认配置文件 `config/ReadOnlyStructOverrides.json`，格式：

  ```json
  {
    "forceReadOnly": ["Microsoft.Msagl.Core.Geometry.Point"]
  }
  ```

- CLI（`make-readonly` 与 `convert-project`）新增 `--readonly-overrides` 选项，默认自动解析 `config/ReadOnlyStructOverrides.json`（当前目录或仓库根）。
- SDK 转换共享同一份代码与配置；配置中不存在 SDK 类型，故 SDK 的 41 converted / 53 skipped / 15 failed 统计保持不变。

### 2.2 LINQ `First()/Last()` 返回数组时避免误加 `.boxed()`

- 回归测试（`FirstElementContainsOnArrayTests.cs`）暴露：`result.First().Contains(1)`（元素为 `int[]`）生成 `...findFirst().orElseThrow().boxed()...`，而 `orElseThrow()` 已返回 `int[]`。
- `Contains` 处理中，当 C# 接收表达式是返回数组的方法调用（`First()`/`Last()`/`ToArray()` 等）时，一律用 `Arrays.stream(receiver).boxed().collect(Collectors.toSet()).contains(...)`。
- 直接 `int[].Contains(...)` 场景（接收表达式为标识符、Java 文本已是 `Arrays.stream(...)`）保留原有防双重包装逻辑。

## 3. 修改文件清单

| 文件 | 修改类型 | 说明 |
|------|----------|------|
| `config/ReadOnlyStructOverrides.json` | 新增 | 强制转换 AGL `Point` |
| `src/CSharpToJava.Core/ReadOnlyStructMaker/ReadOnlyStructMakerOptions.cs` | 修改 | 新增 `ForceReadOnlyQualifiedNames` |
| `src/CSharpToJava.Core/ReadOnlyStructMaker/StructAnalyzer.cs` | 修改 | 命中强制集合时跳过反射安全规则 |
| `src/CSharpToJava.Core/ReadOnlyStructMaker/AssignmentRewriter.cs` | 修改 | `GetQualifiedTypeName` 改为 internal 复用 |
| `src/CSharpToJava.CLI/Program.cs` | 修改 | 加载默认 overrides 配置、新增 CLI 选项 |
| `src/CSharpToJava.Core/Transformers/Expression/Transformers/InvocationExpressionTransformer.cs` | 修改 | 数组值接收表达式强制 `Arrays.stream(...)` |
| `tests/CSharpToJava.Tests/ReadOnlyStructMakerTests.cs` | 修改 | 新增 Serializable force 转换红绿测试 |
| `tests/CSharpToJava.Tests/FirstElementContainsOnArrayTests.cs` | 新增 | LINQ First/Last 数组 Contains 回归测试 |

## 4. 验证结果

- 红：`SerializableMutableStruct_WithForce_ConvertsToReadonly` 在实现前失败（`Changed == false`）；`ContainsOnIntArrayElementFrom*` 在修复前失败。
- 绿：`dotnet test -c Release` → **2830 + 51 全部通过，0 失败**。
- 子任务二：AGL 重新转换 → `mvn clean package -e` → **BUILD SUCCESS**；`Point.java` 为 `public value class`，`public final double X/Y`，含 `withX/withY`。
- 子任务一：SDK 重新转换 → `mvn clean install -e` → **BUILD SUCCESS**（7102 个测试，0 失败）。
