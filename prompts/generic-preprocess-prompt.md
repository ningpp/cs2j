# 开发 Generic Preprocess（C# → C# 泛型预处理通道）

## 背景与目标

本项目 `D:\code\cs2j` 是一个 C# → Java 转译器。C# 的泛型（reified/运行时具现化）与 Java 的泛型（type-erased/类型擦除）差异极大，导致转译器在以下场景需要大量推断与补偿逻辑，既复杂又容易出错：

- `typeof(T)` / `nameof` 相关、`is T` / `as T` 运行时类型判断
- `default(T)` 默认值表达式
- `new T()` / `Array.CreateInstance(typeof(T), n)` / `new T[n]` 受约束的实例化
- `where T : class` / `struct` / `new()` / 基类/接口约束
- 泛型方法类型推断、泛型静态字段、值类型特化
- 泛型数组元素类型、协变/逆变（`in`/`out`）
- **静态泛型方法**：`JsonSerializer.Deserialize<T>(json)`、`Activator.CreateInstance<T>()`、`List<T>.Something()` 等——依赖运行时 `typeof(T)` 具现化，在 JSON 反序列化等场景极常用，且转译器必须为其推断并传递 `Class<T>` 令牌
- **嵌套/递归泛型类型**：`Outer<T>.Inner<U>`、泛型类中的泛型方法、泛型树/链表（`Node<T> Next`）、泛型字典的嵌套值（`Dictionary<string, List<T>>`）、`Type`/`Class` 类型的开放/封闭泛型引用——嵌套层级深、跨成员传播，是转译器最易出错的地方

**目标**：新增一条 **Generic Preprocess** 通道——一个作用在 C# 源码上的 Roslyn 语法重写器，把上述“C#/Java 差异过大”的泛型用法，改写为语义等价、但**对转译器更友好**的 C# 代码（例如显式传递 `Type`/`Class` 令牌、用对象承载 `default(T)`、把运行时泛型操作下沉为可擦除形式等）。

**核心约束**：预处理后的 C# 必须仍然是一个**正确、可编译、行为等价**的 C# 程序。本任务不要求生成 Java，要求的是：**预处理产物在 .NET 上 `dotnet build -c Release` / `dotnet test -c Release` 必须零错误通过**。这样既能验证“语义等价性”，又能让后续转译器不再需要处理这些棘手模式。

---

## 必须遵循的硬性要求

1. **必须编写单元测试**（xUnit，放在 `tests/CSharpToJava.Tests/`）。
   - 至少包含：核心重写器的单文件 C#→C# 转换断言测试（参考 `tests/CSharpToJava.Tests/GotoEliminatorTests.cs` 与 `ReadOnlyStructMakerTests.cs` 的风格）。
   - 至少包含：项目级集成测试（参考 `tests/CSharpToJava.Tests/ProjectGotoPreprocessorTests.cs` 与 `ReadOnlyStructPreprocessorVerifyTests.cs`），验证 `PreprocessAsync` 能跑通并对产物执行 `dotnet build -c Release` / `dotnet test -c Release`。
2. **必须用 `D:\csharp-sdk` 项目验证处理结果**：
   - 将 `D:\csharp-sdk` 预处理到中间目录（命名约定 `.cs2j-generic-src`，与 `.cs2j-no-goto-src` / `.cs2j-readonly-src` 一致）。
   - 对中间目录产物运行 `dotnet build -c Release` 与 `dotnet test -c Release`（如含测试工程），**必须零错误通过**。
   - 每一步保留完整日志。
3. **必须先阅读系统代码再动手**（见下方“必读文件”）。严禁凭空猜测现有 API。
4. **不要修改 `D:\csharp-sdk` 任何文件**（只读输入）。
5. 每个修复都要有 红→绿 验证（先让单元测试失败，再修复到通过），并最终跑全量 `dotnet test -c Release` 确认无回归。

---

## 必读文件（先理解再实现）

实现前通读这些文件，复用既有基础设施，不要另起炉灶：

- `src/CSharpToJava.CLI/ProjectGotoPreprocessor.cs` —— **主要模板**：请求/结果 record、中间目录布局、`PreprocessAsync` 流程、diagnostics 合并、`ContainsGotoOrLabel` 这类“后置自检”示例。
- `src/CSharpToJava.CLI/ProjectReadOnlyStructPreprocessor.cs` —— 另一个模板，重点看其 `VerifyBuild` / `VerifyTests` 字段、`dotnet build -c Release`/`dotnet test -c Release` 验证流程，以及 `ProjectReadOnlyPreprocessRequest` / `ProjectReadOnlyPreprocessResult` 结构。
- `src/CSharpToJava.CLI/ProjectPreprocessSource.cs` —— **所有预处理器共用的源树准备**：`ResolveLayout`、`EnumerateSourceFiles`、`CopyProjectFilesAsync`、`CopyAncestorMsBuildDirectoryFiles`、`ResolveRestoreTarget`、`RunDotNetRestore`、`EnsurePreserializedResourceSupport`。新增的 generic 预处理器必须复用它，保证中间目录选择/拷贝行为一致（注释第 8–13 行明确说明了这一点）。
- `src/CSharpToJava.CLI/ProjectUnsignedTypePreprocessor.cs` —— 第三个模板，对照其 statistics/diagnostics 设计。
- `src/CSharpToJava.CLI/Program.cs` —— 看 `MakeReadonly` / `EliminateGoto` / `ConvertProject` 如何串联预处理器（`Program.cs:221-300`），以及 `ConvertProjectOptions` 上新增的 `MakeReadOnly`/`EliminateGoto` 式开关。你的新预处理器要能以同样方式接入 `convert-project` 流水线（在转译之前运行）。
- `src/CSharpToJava.Core/GotoEliminator/` 与 `src/CSharpToJava.Core/ReadOnlyStructMaker/` —— 核心重写器放在 `Core` 下的目录结构范式（Options / Statistics / Diagnostics / Rewriter 分文件）。
- `tests/CSharpToJava.Tests/ProjectGotoPreprocessorTests.cs` 与 `ReadOnlyStructPreprocessorVerifyTests.cs` —— 测试范式（临时目录、`IsDotNetAvailable` 守卫、`CreateTempDirectory`）。
- `tests/CSharpToJava.ConversionTests/Categories/GenericsTests.cs` —— 现有泛型转换预期，帮助你判断哪些模式应被预处理接管、避免在转译器侧重复处理。
- `tests/CSharpToJava.Tests/` 下的相关回归测试（如 `TypeOfGenericTests.cs`、`TypeOfUnboundGenericTests.cs`、`SystemTextJsonDeserializeTypeReferenceTests.cs`、`DeserializeGenericClassLiteralTests.cs`、`GenericMethodExplicitTypeArgTests.cs`、`NestedGenericMappingTests.cs`、`StaticGenericFieldTests.cs`）—— 这些覆盖了静态/嵌套/反射泛型在转译器侧的现有行为，预处理接管后应确保对应 C# 场景在 `.cs2j-generic-src` 中仍能 `dotnet build -c Release` / `dotnet test -c Release` 通过，且语义等价。

---

## 实现指引（让 agent 自行设计具体 lowering，但方向如下）

1. **新增目录** `src/CSharpToJava.Core/GenericPreprocess/`，包含：
   - `GenericPreprocessOptions.cs`、`GenericPreprocessStatistics.cs`、`GenericPreprocessDiagnostics.cs`（severity/diagnostic 模型，对齐 `GotoEliminator`/`ReadOnlyStructMaker`）。
   - `GenericPreprocessor.cs`（核心 `Preprocess(code, options) -> result`：用 `CSharpSyntaxTree` / `CSharpSyntaxRewriter` 做语法级改写；必要时结合 `SemanticModel` 判定符号）。
2. **新增 CLI 包装** `src/CSharpToJava.CLI/ProjectGenericPreprocessor.cs`：
   - `ProjectGenericPreprocessRequest` / `ProjectGenericPreprocessResult`（仿 `ProjectGotoPreprocessRequest/Result`，含 `VerifyBuild` / `VerifyTests`）。
   - `IntermediateDirectoryName = ".cs2j-generic-src"`。
   - `PreprocessAsync` 复用 `ProjectPreprocessSource.*`，对 `.cs` 文件调用 core 重写器，非 `.cs` 文件直接拷贝；最后可选地 `dotnet restore -c Release` + `dotnet build -c Release` + `dotnet test -c Release` 验证。
   - 增加“后置自检”：遍历产物确认改写确实消除了目标模式（类比 `ContainsGotoOrLabel`）。
 3. **接入 CLI**：在 `Program.cs` 增加一个**独立 verb** `GenericPreprocess`（对齐 `MakeReadOnlyOptions`/`EliminateGotoOptions`，见 `Program.cs:26` 的 verb 解析与 `Program.cs:31-32` 的 dispatch），它只执行 generic 预处理并写出中间目录。可选地把该 verb 也接入 `convert-project` 流水线（转译之前串联，更新 `opts.Source` 指向中间目录）。验证阶段只用独立 verb，不要走 `convert-project`。
 4. **重写范围（按优先级，覆盖最易导致转译复杂度的）**：
    - P0：`default(T)`、`typeof(T)`、`new T()`（受 `new()` 约束）、`new T[n]` / `Array.CreateInstance(typeof(T), n)`。
    - P1：`is T` / `as T` / `pattern is T` 的运行时类型分支；`where T : class`/`struct`/`new()` 约束处理。
    - P2：泛型方法显式类型实参推断辅助、泛型静态字段、`in`/`out` 变体的安全降级。
    - **P3（重点，JSON/序列化相关）**：
      - **静态泛型方法**：`JsonSerializer.Deserialize<T>(json)`、`Activator.CreateInstance<T>()`、`JsonConvert.DeserializeObject<T>(x)` 等。重写时必须把运行期需要的 `Type`/`Class<T>` 令牌显式落地（例如改为接受 `Type`/`Class` 实参的重载，或在调用点注入 `typeof(T)`），避免转译器在调用现场反复推断类型参数。
      - **嵌套/递归泛型类型**：`Outer<T>.Inner<U>`、泛型类内的泛型方法、泛型树/链表（`Node<T> Next`）、`Dictionary<string, List<T>>` 这类嵌套值类型、开放/封闭泛型 `Type` 引用。重写要保证类型参数在嵌套成员、字段、返回类型、基类列表中一致传播，不产生未绑定的开放泛型。
      - 对 `System.Text.Json` / `Newtonsoft.Json` 的反序列化入口做专项覆盖（在 `D:\csharp-sdk` 上 grep 这类调用，确认预处理后调用点仍编译且行为等价）。
    - 每一项都要保证**语义等价**：产物仍是可读、可编译、可测的 C#，且相对原始程序行为一致（测试套件 `dotnet test -c Release` 通过即证明）。
5. **参考既有测试命名与断言风格**，新增：
   - `tests/CSharpToJava.Tests/GenericPreprocessTests.cs`（核心改写单元断言）。
   - `tests/CSharpToJava.Tests/ProjectGenericPreprocessorTests.cs`（集成 + `dotnet build -c Release`/`dotnet test -c Release` 验证，参考 `ReadOnlyStructPreprocessorVerifyTests.cs` 的 `IsDotNetAvailable` 守卫与 `CreateTempDirectory`）。

---

## 验证循环（重复直到 `dotnet build -c Release` + `dotnet test -c Release` 全绿）

### Step 1 — 写失败的单元测试（Red）
在 `tests/CSharpToJava.Tests/GenericPreprocessTests.cs` 中，针对一个具体模式（如 `default(T)`）写断言：给定 C# 片段，断言重写后产物不含 `default(T)` 且等价。运行 `dotnet test -c Release --filter` 确认**失败**。

### Step 2 — 实现核心重写器（Green）
在 `src/CSharpToJava.Core/GenericPreprocess/` 实现该模式的 lowering。运行单测确认**通过**；运行 `dotnet test -c Release` 确认无回归。

### Step 3 — SDK 级验证
新增一个**独立的 CLI verb** `generic-preprocess`（对齐 `eliminate-goto` / `make-readonly`，见 `Program.cs:26` 的 verb 列表与 `Program.cs:31-32` 的 dispatch），它只跑 generic 预处理、把结果写入中间目录，**不要**走 `convert-project`（后者还包含转译、其他预处理、构建验证等，做的太多了）。

```bash
dotnet run -c Release --project src/CSharpToJava.CLI/CSharpToJava.CLI.csproj -- generic-preprocess -s D:\csharp-sdk -d d:\cs-java-generic-sdk
```
确认命令无异常退出，且 `.cs2j-generic-src` 中间目录已生成、结构与 `D:\csharp-sdk` 一致（仅 `.cs` 被改写，其余文件原样拷贝）。

### Step 4 — 产物 dotnet build -c Release / dotnet test -c Release
```bash
cd d:\cs-java-generic-sdk\.cs2j-generic-src
dotnet build -c Release  > sdk-generic-build.log 2>&1
dotnet test  -c Release  > sdk-generic-test.log  2>&1
```
- `dotnet build -c Release` 必须退出码 0、无错误。
- 若工程含测试，`dotnet test -c Release` 必须退出码 0、全部通过。
- 保存日志。若失败，回到 Step 2 修正重写器（保持语义等价），必要时先补一个最小复现单测。

### Step 5 — Commit
```
git add -A
git commit -m "feat: 新增 generic preprocess（C#→C# 泛型预处理通道）

目的：消除 C#/Java 泛型差异导致转译器复杂度，保证语义等价。
验证：D:\csharp-sdk 预处理产物 dotnet build -c Release / dotnet test -c Release 全绿。
"
```

---

## 成功标准
1. `tests/CSharpToJava.Tests/` 新增的 generic 预处理单测与项目级验证测试全部通过。
 2. `D:\csharp-sdk` 经 generic 预处理后，中间目录产物 `dotnet build -c Release` 零错误、`dotnet test -c Release`（含测试工程时）全部通过。
3. `dotnet test -c Release`（整个 cs2j 仓库）无回归。
4. 新预处理器已按 `MakeReadOnly`/`EliminateGoto` 的同等方式接入 `convert-project` 流水线，可在转译前运行。
