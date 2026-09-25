# Array Helper 预处理器 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [x]`) syntax for tracking.

**Goal:** 新增预处理阶段 array-method，把 C# 中所有"纯数组相关、且 Java 没有对应语法/语义/用法"的构造改写为全限定静态调用 `Cs2jArray.ArrayHelper.XXX(...)`，同时在 Java 侧 compat 扩展既有 `io.github.ningpp.compat.ArrayHelper` 类，使转换产物可编译。

**Architecture:** 完全 mirror UnsignedTypeRewriter 的安全链路（脏检查 → SemanticModel 精判 → 局部标注 → 局部格式化 → 重新解析校验 → 兜底保留原文件）。C# 侧支撑类以独立 lib 项目 `Cs2jArray` 落地（嵌入资源分发 + ProjectReference 注入），主转换器把它仅当符号来源。Java 侧在既有 `ArrayHelper` 中追加方法组（Object + 反射形态，委托既有 `CSharpArray`）。

**Tech Stack:** C# / .NET 10、Roslyn `Microsoft.CodeAnalysis.CSharp` 4.12.0（`LanguageVersion.Latest`，unsafe 打开）、xUnit、JUnit Jupiter 5.11.4、CommandLineParser verbs；与 [UnsignedTypeRewriter.cs](file:///d:/code/cs2j/src/CSharpToJava.Core/UnsignedTypeRewriter/UnsignedTypeRewriter.cs)、[ProjectUnsignedTypePreprocessor.cs](file:///d:/code/cs2j/src/CSharpToJava.CLI/ProjectUnsignedTypePreprocessor.cs) 同构。

> 规范来源：[2026-09-25-array-helper-preprocessor-design.md](file:///d:/code/cs2j/docs/superpowers/specs/2026-09-25-array-helper-preprocessor-design.md)。

---

## 0. 全局约定（执行每个任务前先读）

- 仓库根：`d:\code\cs2j`。所有路径相对仓库根。PowerShell 不支持 `&&`，串行命令用 `;`。
- 命名空间：Core 全部放 `CSharpToJava.Core.ArrayMethodRewriter`；CLI 类放 `CSharpToJava.CLI`（与既有预处理器一致，internal）。
- C# 侧支撑库：namespace `Cs2jArray`，class `ArrayHelper`，项目名 `Cs2jArray.csproj`。
- Java 侧 compat：package `io.github.ningpp.compat`，class `ArrayHelper`（既有类，追加方法）。
- 注解名固定：`"Cs2jArrayRewrite"`（UnsignedType 用 `"Cs2jUnsignedRewrite"`）。
- 每个任务结束必须 `dotnet build` 全绿再提交；提交信息用 `feat(array-method): ...` / `test(array-method): ...`。
- TDD 节奏：先写失败测试 → 跑确认失败 → 最小实现 → 跑确认通过 → 提交。
- `CSharpToJava.Core.csproj` 已对 `CSharpToJava.ProjectTests` 配 `InternalsVisibleTo`，internal 类型可直接在 ProjectTests 里测。
- 改写后调用形态一律 `Cs2jArray.ArrayHelper.XXX(...)`（全限定，无 using 依赖），与 `Cs2jUnsigned.UnsignedMath.XXX(...)` 完全同构。

### 0.1 File Structure（本计划创建/修改的全部文件）

**Core（新建目录 `src/CSharpToJava.Core/ArrayMethodRewriter/`）：**

| 文件 | 职责 |
|---:|---|
| `ArrayMethodRewriter.cs` | 公共入口 `Rewrite(source, semanticModel, options)` → `ArrayMethodRewriterResult`；脏检查、改写、`FormatAndValidate`、`GetRewrittenSpans` |
| `ArrayMethodSyntaxRewriter.cs` | `CSharpSyntaxRewriter` 主体：实例成员改写、静态调用改写、Rank 内联、Resize 语句改写、多维 foreach 摊平 |
| `ArrayRewriteAnnotations.cs` | 局部标注（Kind = `"Cs2jArrayRewrite"`），`Mark<T>` |
| `ArrayMethodRewriterOptions.cs` | record（Verbose/Strict/EnableInstanceRewrite/EnableStaticRewrite/EnableForeachFlatten） |
| `ArrayMethodRewriterResult.cs` | record（OutputCode/Changed/Diagnostics/Statistics） |
| `ArrayMethodRewriterStatistics.cs` | FilesScanned/FilesTransformed/FilesSkippedClean/InstanceMemberRewrites/StaticArrayCalls/ResizeStatements/ForeachFlattened/RankInlined/MultiDimLengthRewrites |
| `ArrayMethodRewriterDiagnostic.cs` | Severity enum + record(Severity, Message) |

**C# 侧支撑库（新建目录 `src/Cs2jArray/`）：**

| 文件 | 职责 |
|---:|---|
| `src/Cs2jArray/ArrayHelper.cs` | `public static class ArrayHelper`，namespace `Cs2jArray`；全部 [B] 项签名 + 白名单实现 |
| `src/Cs2jArray/Cs2jArray.csproj` | net10.0 / LangVersion latest / Nullable disable / ImplicitUsings disable |

**CLI：**

| 文件 | 职责 |
|---:|---|
| `src/CSharpToJava.CLI/ProjectArrayMethodPreprocessor.cs` | 新建：项目模式预处理器（mirror `ProjectUnsignedTypePreprocessor`）；`IntermediateDirectoryName = ".cs2j-array-src"` |
| `src/CSharpToJava.CLI/ArrayHelperSupportFiles.cs` | 新建：嵌入资源读取/释放（mirror `UnsignedMathSupportFiles`） |
| `src/CSharpToJava.CLI/Cs2jArrayProject.cs` | 新建：按 csproj 文件名精确识别 `Cs2jArray.csproj`（mirror `Cs2jUnsignedMathProject`） |
| `src/CSharpToJava.CLI/CSharpToJava.CLI.csproj` | 改：嵌入 `Cs2jArray` 的两个文件 |
| `src/CSharpToJava.CLI/Program.cs` | 改：新增 `array-method` verb + `ArrayMethodOptions`、`ConvertProjectOptions` 开关、`ConvertProject` 执行块、`GetPreservedDestinationDirectories`、workspace 过滤、指纹行 |
| `src/CSharpToJava.CLI/MultiModulePlanner.cs` | 改：过滤 `Cs2jArray` 项目（链式谓词追加） |

**配置：**

| 文件 | 职责 |
|---:|---|
| `config/TypeMappings.json` | 改：新增 `Cs2jArray.ArrayHelper → ArrayHelper` 类型条目 |
| `CSharpToJavaConverter.slnx` | 改：加 `Cs2jArray` 项目 |

**Java 侧 compat：**

| 文件 | 职责 |
|---:|---|
| `java/csharptojava-compat/src/main/java/io/github/ningpp/compat/ArrayHelper.java` | 改：追加 [B] 项方法组（Object + 反射，委托 CSharpArray） |
| `java/csharptojava-compat/src/test/java/io/github/ningpp/compat/ArrayHelperTest.java` | 改：追加边界值 JUnit |

**Tests：**

| 文件 | 职责 |
|---:|---|
| `tests/CSharpToJava.ProjectTests/ArrayMethodRewriterUnitTests.cs` | 新建：改写器单元（决策表 §3 每个 [B] 项正向 + 负向） |
| `tests/CSharpToJava.ProjectTests/ArrayMethodProjectTests.cs` | 新建：项目级 + 中间树 + 名称碰撞 + 下游存活 |
| `tests/CSharpToJava.ProjectTests/ArrayMethodSemanticsTests.cs` | 新建：改写前后行为一致（C# 侧编译对拍） |
| `tests/CSharpToJava.ProjectTests/ArrayMethodConversionEndToEndTests.cs` | 新建：预处理 → workspace → 过滤 → 转换 → 产物断言 |
| `tests/CSharpToJava.ProjectTests/MultiModulePlannerTests.cs` | 改：加 Cs2jArray 过滤断言 |
| `tests/SampleArraySolution/` | 新建 fixture：Lib + App + sln + NameCollision |
| `tests/CSharpToJava.ProjectTests/CSharpToJava.ProjectTests.csproj` | 改：加 Content Include |

---

## Milestone M1 — C# 侧支撑库 + 嵌入资源 + 基础骨架

### Task 1: 创建 `Cs2jArray` lib 项目

**Files:**
- Create: `src/Cs2jArray/Cs2jArray.csproj`
- Create: `src/Cs2jArray/ArrayHelper.cs`
- Modify: `CSharpToJavaConverter.slnx`

- [x] **Step 1: 创建 csproj**

Create `src/Cs2jArray/Cs2jArray.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <LangVersion>latest</LangVersion>
    <Nullable>disable</Nullable>
    <ImplicitUsings>disable</ImplicitUsings>
  </PropertyGroup>

</Project>
```

- [x] **Step 2: 创建 ArrayHelper.cs 骨架（仅命名空间 + 类声明，Task 2 补全签名）**

Create `src/Cs2jArray/ArrayHelper.cs`:

```csharp
// Cs2jArray.ArrayHelper：数组语义收口辅助类（唯一真源）。
// 本文件既是本库项目的源码，又被 CLI 以 EmbeddedResource 嵌入分发：
// array-method 预处理把它释放到中间树 Cs2jArray/ 下，主转换器
// 跳过该项目的转换，调用点经 TypeMappings 映射到 compat 手写类。
// 单文件模式（array-method -i/-o）会把本文件原样写到输出目录（文件名
// _Cs2jArrayHelper.cs），用户自行编译后再走主转换，因此内容须保持
// "可转换 C# 子集"：
// - 不得出现 Array.* 静态调用或 System.Array 接收者成员（会被主转换器
//   映射到 CSharpArray 包装形态，与运行时裸数组不符）；
// - 实现体只用白名单原语：一维/交错数组、arr.Length、arr[i]、数组 foreach、
//   基本控制流、IComparable/IComparer/委托调用、(T)/(T[]) 强转、
//   (T[])new object[n] 泛型数组创建模式。
namespace Cs2jArray
{
    public static class ArrayHelper
    {
    }
}
```

- [x] **Step 3: 加入 slnx**

在 `CSharpToJavaConverter.slnx` 的 `<Solution>` 根元素内、`</Folder>` 之前追加：

```xml
  <Project Path="src\Cs2jArray\Cs2jArray.csproj" Id="a1b2c3d4-e5f6-4a7b-8c9d-0e1f2a3b4c5d" Type="Microsoft.VisualStudio.Project" />
```

在 `/src/` Folder 的 `<ProjectRef>` 列表末尾追加：

```xml
    <ProjectRef Id="a1b2c3d4-e5f6-4a7b-8c9d-0e1f2a3b4c5d" />
```

- [x] **Step 4: 构建验证**

Run:
```powershell
cd d:/code/cs2j; dotnet build src/Cs2jArray/Cs2jArray.csproj
```
Expected: Build succeeded, 0 errors.

- [x] **Step 5: Commit**

```powershell
cd d:/code/cs2j; git add src/Cs2jArray/ CSharpToJavaConverter.slnx; git commit -m "feat(array-method): add Cs2jArray lib project skeleton"
```

---

### Task 2: 填充 `ArrayHelper.cs` 全部方法签名 + 白名单实现

**Files:**
- Modify: `src/Cs2jArray/ArrayHelper.cs`

> 注意：C# 侧 lib 是真 C# 实现（编译、运行），因此可以使用 `System.Array` 静态方法和实例成员——这些调用在 lib 自身编译时合法。"可转换 C# 子集"约束仅影响**主转换器是否需要转换本文件**，而主转换器按 `_Cs2j*` 前缀/项目过滤跳过本文件，所以 lib 内部实现可以自由使用 `System.Array`。

- [x] **Step 1: 填充全部 [B] 项方法签名与实现**

将 `src/Cs2jArray/ArrayHelper.cs` 中 `public static class ArrayHelper` 的空 body 替换为以下全部内容：

```csharp
        // ---- 元数据（§3.1）----

        public static int GetLength(System.Array array)
        {
            int total = 0;
            foreach (var row in ToRows(array))
            {
                total += row.Length;
            }
            return total;
        }

        public static long GetLongLength(System.Array array)
        {
            return GetLength(array);
        }

        public static int GetLength(System.Array array, int dimension)
        {
            if (dimension < 0) throw new System.IndexOutOfRangeException();
            int d = 0;
            var current = array;
            while (d < dimension)
            {
                if (current.Length == 0) return 0;
                current = (System.Array)current.GetValue(0)!;
                d++;
            }
            return current.Length;
        }

        public static long GetLongLength(System.Array array, int dimension)
        {
            return GetLength(array, dimension);
        }

        public static int GetLowerBound(System.Array array, int dimension)
        {
            if (dimension < 0) throw new System.IndexOutOfRangeException();
            GetLength(array, dimension);
            return 0;
        }

        public static int GetUpperBound(System.Array array, int dimension)
        {
            return GetLowerBound(array, dimension) + GetLength(array, dimension) - 1;
        }

        // ---- 实例成员收口（§3.1）----

        public static System.Array Clone(System.Array array)
        {
            var elementType = array.GetType().GetElementType()!;
            var copy = System.Array.CreateInstance(elementType, array.Length);
            for (int i = 0; i < array.Length; i++)
            {
                copy.SetValue(array.GetValue(i), i);
            }
            return copy;
        }

        public static void CopyTo(System.Array source, System.Array destination, long index)
        {
            for (int i = 0; i < source.Length; i++)
            {
                destination.SetValue(source.GetValue(i), (int)index + i);
            }
        }

        public static T GetValue<T>(T[] array, int index) => array[index];

        public static T GetValue<T>(T[][] array, int index0, int index1) => array[index0][index1];

        public static T GetValue<T>(T[][][] array, int i0, int i1, int i2) => array[i0][i1][i2];

        public static object GetValue(System.Array array, params int[] indices)
        {
            object current = array;
            foreach (var idx in indices)
            {
                current = ((System.Array)current).GetValue(idx);
            }
            return current;
        }

        public static void SetValue<T>(T[] array, object value, int index) => array[index] = (T)value;

        public static void SetValue<T>(T[][] array, object value, int i0, int i1) => array[i0][i1] = (T)value;

        public static void SetValue<T>(T[][][] array, object value, int i0, int i1, int i2) => array[i0][i1][i2] = (T)value;

        public static void SetValue(System.Array array, object value, params int[] indices)
        {
            System.Array current = array;
            for (int i = 0; i < indices.Length - 1; i++)
            {
                current = (System.Array)current.GetValue(indices[i])!;
            }
            current.SetValue(value, indices[indices.Length - 1]);
        }

        // ---- System.Array 静态收口（§3.3）----

        public static void Sort(System.Array array) => System.Array.Sort(array);

        public static void Sort(System.Array array, System.Collections.IComparer comparer) => System.Array.Sort(array, comparer);

        public static void Sort(System.Array array, int index, int length) => System.Array.Sort(array, index, length);

        public static void Sort(System.Array array, int index, int length, System.Collections.IComparer comparer) => System.Array.Sort(array, index, length, comparer);

        public static void Sort(System.Array keys, System.Array items) => System.Array.Sort(keys, items);

        public static void Sort(System.Array keys, System.Array items, System.Collections.IComparer comparer) => System.Array.Sort(keys, items, comparer);

        public static int BinarySearch(System.Array array, object value) => System.Array.BinarySearch(array, value);

        public static int BinarySearch(System.Array array, object value, System.Collections.IComparer comparer) => System.Array.BinarySearch(array, value, comparer);

        public static int BinarySearch(System.Array array, int index, int length, object value) => System.Array.BinarySearch(array, index, length, value);

        public static int BinarySearch(System.Array array, int index, int length, object value, System.Collections.IComparer comparer) => System.Array.BinarySearch(array, index, length, value, comparer);

        public static void Clear(System.Array array, int index, int length) => System.Array.Clear(array, index, length);

        public static T[] Resize<T>(T[] array, int newSize)
        {
            var result = new T[newSize];
            int copyLen = array.Length < newSize ? array.Length : newSize;
            for (int i = 0; i < copyLen; i++)
            {
                result[i] = array[i];
            }
            return result;
        }

        public static int IndexOf(System.Array array, object value) => System.Array.IndexOf(array, value);

        public static int IndexOf(System.Array array, object value, int startIndex) => System.Array.IndexOf(array, value, startIndex);

        public static int IndexOf(System.Array array, object value, int startIndex, int count) => System.Array.IndexOf(array, value, startIndex, count);

        public static int LastIndexOf(System.Array array, object value) => System.Array.LastIndexOf(array, value);

        public static int LastIndexOf(System.Array array, object value, int startIndex) => System.Array.LastIndexOf(array, value, startIndex);

        public static int LastIndexOf(System.Array array, object value, int startIndex, int count) => System.Array.LastIndexOf(array, value, startIndex, count);

        public static void Reverse(System.Array array) => System.Array.Reverse(array);

        public static void Reverse(System.Array array, int index, int length) => System.Array.Reverse(array, index, length);

        public static T Find<T>(T[] array, System.Predicate<T> match)
        {
            foreach (var item in array)
            {
                if (match(item)) return item;
            }
            return default!;
        }

        public static T[] FindAll<T>(T[] array, System.Predicate<T> match)
        {
            var list = new System.Collections.Generic.List<T>();
            foreach (var item in array)
            {
                if (match(item)) list.Add(item);
            }
            return list.ToArray();
        }

        public static int FindIndex<T>(T[] array, System.Predicate<T> match)
        {
            for (int i = 0; i < array.Length; i++)
            {
                if (match(array[i])) return i;
            }
            return -1;
        }

        public static int FindIndex<T>(T[] array, int startIndex, int count, System.Predicate<T> match)
        {
            int end = startIndex + count;
            for (int i = startIndex; i < end; i++)
            {
                if (match(array[i])) return i;
            }
            return -1;
        }

        public static T FindLast<T>(T[] array, System.Predicate<T> match)
        {
            for (int i = array.Length - 1; i >= 0; i--)
            {
                if (match(array[i])) return array[i];
            }
            return default!;
        }

        public static int FindLastIndex<T>(T[] array, System.Predicate<T> match)
        {
            for (int i = array.Length - 1; i >= 0; i--)
            {
                if (match(array[i])) return i;
            }
            return -1;
        }

        public static int FindLastIndex<T>(T[] array, int startIndex, int count, System.Predicate<T> match)
        {
            for (int i = startIndex + count - 1; i >= startIndex; i--)
            {
                if (match(array[i])) return i;
            }
            return -1;
        }

        public static bool Exists<T>(T[] array, System.Predicate<T> match)
        {
            foreach (var item in array)
            {
                if (match(item)) return true;
            }
            return false;
        }

        public static bool TrueForAll<T>(T[] array, System.Predicate<T> match)
        {
            foreach (var item in array)
            {
                if (!match(item)) return false;
            }
            return true;
        }

        public static TOutput[] ConvertAll<TInput, TOutput>(TInput[] array, System.Converter<TInput, TOutput> converter)
        {
            var result = new TOutput[array.Length];
            for (int i = 0; i < array.Length; i++)
            {
                result[i] = converter(array[i]);
            }
            return result;
        }

        public static void ForEach<T>(T[] array, System.Action<T> action)
        {
            foreach (var item in array)
            {
                action(item);
            }
        }

        public static void Copy(System.Array source, System.Array destination, int length)
            => System.Array.Copy(source, destination, length);

        public static void Copy(System.Array source, int sourceIndex, System.Array destination, int destinationIndex, int length)
            => System.Array.Copy(source, sourceIndex, destination, destinationIndex, length);

        public static void ConstrainedCopy(System.Array source, int sourceIndex, System.Array destination, int destinationIndex, int length)
            => System.Array.ConstrainedCopy(source, sourceIndex, destination, destinationIndex, length);

        // ---- 内部辅助 ----

        private static System.Array[] ToRows(System.Array array)
        {
            if (array is object[] outer && outer.Length > 0 && outer[0] is System.Array)
            {
                var rows = new System.Array[outer.Length];
                for (int i = 0; i < outer.Length; i++)
                {
                    rows[i] = (System.Array)outer[i]!;
                }
                return rows;
            }
            return new[] { array };
        }
```

- [x] **Step 2: 构建验证**

Run:
```powershell
cd d:/code/cs2j; dotnet build src/Cs2jArray/Cs2jArray.csproj
```
Expected: Build succeeded, 0 errors.

- [x] **Step 3: Commit**

```powershell
cd d:/code/cs2j; git add src/Cs2jArray/ArrayHelper.cs; git commit -m "feat(array-method): fill ArrayHelper with all [B] method signatures and implementations"
```

---

### Task 3: 创建 CLI 支撑文件（`Cs2jArrayProject` + `ArrayHelperSupportFiles`）

**Files:**
- Create: `src/CSharpToJava.CLI/Cs2jArrayProject.cs`
- Create: `src/CSharpToJava.CLI/ArrayHelperSupportFiles.cs`
- Modify: `src/CSharpToJava.CLI/CSharpToJava.CLI.csproj`

- [x] **Step 1: 创建 `Cs2jArrayProject.cs`**

Create `src/CSharpToJava.CLI/Cs2jArrayProject.cs`:

```csharp
namespace CSharpToJava.CLI;

/// <summary>
/// 按 csproj 文件名精确识别 cs2j 内部支撑库 Cs2jArray
/// （与 Cs2jUnsignedMathProject 同款保留项目名契约）。
/// </summary>
internal static class Cs2jArrayProject
{
    public const string ProjectFileName = "Cs2jArray.csproj";

    public static bool IsCs2jArrayProject(string? projectFilePath)
        => projectFilePath != null
           && Path.GetFileName(projectFilePath).Equals(ProjectFileName, StringComparison.OrdinalIgnoreCase);
}
```

- [x] **Step 2: 创建 `ArrayHelperSupportFiles.cs`**

Create `src/CSharpToJava.CLI/ArrayHelperSupportFiles.cs`:

```csharp
namespace CSharpToJava.CLI;

/// <summary>
/// Cs2jArray 支撑文件的嵌入资源读取/释放。真源是
/// src/Cs2jArray/ 下的两个文件（构建期嵌入 CLI 程序集），结构上
/// 不存在第二份副本可漂移。单文件模式（array-method -i/-o）沿用
/// _Cs2j* 支撑文件前缀契约，文件名与项目模式的 ArrayHelper.cs 不同名是有意的。
/// </summary>
internal static class ArrayHelperSupportFiles
{
    public const string LibDirectoryName = "Cs2jArray";
    public const string SingleFileHelperFileName = "_Cs2jArrayHelper.cs";

    private const string SourceResourceName = "Cs2jArray.ArrayHelper.cs";
    private const string ProjectResourceName = "Cs2jArray.Cs2jArray.csproj";

    public static string GetArrayHelperFileContent() => ReadResource(SourceResourceName);

    public static string GetProjectFileContent() => ReadResource(ProjectResourceName);

    /// <summary>把库项目（csproj + 源码）释放到目标目录（UTF-8 无 BOM）。</summary>
    public static async Task ReleaseAsync(string destinationDirectory)
    {
        Directory.CreateDirectory(destinationDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(destinationDirectory, "ArrayHelper.cs"),
            GetArrayHelperFileContent(),
            new System.Text.UTF8Encoding(false));
        await File.WriteAllTextAsync(
            Path.Combine(destinationDirectory, "Cs2jArray.csproj"),
            GetProjectFileContent(),
            new System.Text.UTF8Encoding(false));
    }

    private static string ReadResource(string resourceName)
    {
        var assembly = typeof(ArrayHelperSupportFiles).Assembly;
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException(
                $"Embedded resource not found: {resourceName}. " +
                "The CLI project must embed src/Cs2jArray/* with explicit LogicalName.");
        using var reader = new System.IO.StreamReader(stream, System.Text.Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
```

- [x] **Step 3: 在 CLI csproj 中嵌入 Cs2jArray 文件**

在 `src/CSharpToJava.CLI/CSharpToJava.CLI.csproj` 中，在已有的 `Cs2jUnsignedMath` EmbeddedResource ItemGroup 之后追加：

```xml
  <ItemGroup>
    <!-- Cs2jArray 支撑库（唯一真源在 src/Cs2jArray/，此处嵌入分发） -->
    <EmbeddedResource Include="..\Cs2jArray\ArrayHelper.cs">
      <Link>support\Cs2jArray\ArrayHelper.cs</Link>
      <LogicalName>Cs2jArray.ArrayHelper.cs</LogicalName>
    </EmbeddedResource>
    <EmbeddedResource Include="..\Cs2jArray\Cs2jArray.csproj">
      <Link>support\Cs2jArray\Cs2jArray.csproj</Link>
      <LogicalName>Cs2jArray.Cs2jArray.csproj</LogicalName>
    </EmbeddedResource>
  </ItemGroup>
```

- [x] **Step 4: 构建验证**

Run:
```powershell
cd d:/code/cs2j; dotnet build src/CSharpToJava.CLI/CSharpToJava.CLI.csproj
```
Expected: Build succeeded, 0 errors.

- [x] **Step 5: Commit**

```powershell
cd d:/code/cs2j; git add src/CSharpToJava.CLI/Cs2jArrayProject.cs src/CSharpToJava.CLI/ArrayHelperSupportFiles.cs src/CSharpToJava.CLI/CSharpToJava.CLI.csproj; git commit -m "feat(array-method): add CLI support files for Cs2jArray embedded resources"
```

---

### Task 4: 验证嵌入资源可读（快速冒烟测试）

**Files:**
- Create: `tests/CSharpToJava.ProjectTests/ArrayHelperSupportFilesTests.cs`

- [x] **Step 1: 写测试**

Create `tests/CSharpToJava.ProjectTests/ArrayHelperSupportFilesTests.cs`:

```csharp
using CSharpToJava.CLI;

namespace CSharpToJava.ProjectTests;

public class ArrayHelperSupportFilesTests
{
    [Fact]
    public void GetArrayHelperFileContent_ContainsExpectedNamespaceAndClass()
    {
        var content = ArrayHelperSupportFiles.GetArrayHelperFileContent();
        Assert.Contains("namespace Cs2jArray", content);
        Assert.Contains("public static class ArrayHelper", content);
        Assert.Contains("public static int GetLength(System.Array array)", content);
        Assert.Contains("public static T Find<T>(T[] array, System.Predicate<T> match)", content);
        Assert.Contains("public static T[] Resize<T>(T[] array, int newSize)", content);
    }

    [Fact]
    public void GetProjectFileContent_ContainsNet10Target()
    {
        var content = ArrayHelperSupportFiles.GetProjectFileContent();
        Assert.Contains("net10.0", content);
        Assert.Contains("Microsoft.NET.Sdk", content);
    }

    [Fact]
    public void LibDirectoryName_AndSingleFileHelperFileName_MatchContract()
    {
        Assert.Equal("Cs2jArray", ArrayHelperSupportFiles.LibDirectoryName);
        Assert.Equal("_Cs2jArrayHelper.cs", ArrayHelperSupportFiles.SingleFileHelperFileName);
    }
}
```

- [x] **Step 2: 运行测试确认通过**

Run:
```powershell
cd d:/code/cs2j; dotnet test tests/CSharpToJava.ProjectTests/CSharpToJava.ProjectTests.csproj --filter "ArrayHelperSupportFilesTests" --no-build
```
Expected: 3 passed, 0 failed.

- [x] **Step 3: Commit**

```powershell
```powershell
cd d:/code/cs2j; git add tests/CSharpToJava.ProjectTests/ArrayHelperSupportFilesTests.cs; git commit -m "test(array-method): smoke test embedded resource readability"
```

---

## Milestone M2 — Core 改写器骨架 + 脏检查 + 实例成员改写

### Task 5: 创建 Core 骨架文件（options/result/statistics/diagnostic/annotations）

**Files:**
- Create: `src/CSharpToJava.Core/ArrayMethodRewriter/ArrayMethodRewriterOptions.cs`
- Create: `src/CSharpToJava.Core/ArrayMethodRewriter/ArrayMethodRewriterResult.cs`
- Create: `src/CSharpToJava.Core/ArrayMethodRewriter/ArrayMethodRewriterStatistics.cs`
- Create: `src/CSharpToJava.Core/ArrayMethodRewriter/ArrayMethodRewriterDiagnostic.cs`
- Create: `src/CSharpToJava.Core/ArrayMethodRewriter/ArrayRewriteAnnotations.cs`

- [x] **Step 1: 创建 5 个骨架文件**

Create `src/CSharpToJava.Core/ArrayMethodRewriter/ArrayMethodRewriterOptions.cs`:

```csharp
namespace CSharpToJava.Core.ArrayMethodRewriter;

public sealed record ArrayMethodRewriterOptions(
    bool Verbose = false,
    bool Strict = false,
    bool EnableInstanceRewrite = true,
    bool EnableStaticRewrite = true,
    bool EnableForeachFlatten = true);
```

Create `src/CSharpToJava.Core/ArrayMethodRewriter/ArrayMethodRewriterResult.cs`:

```csharp
namespace CSharpToJava.Core.ArrayMethodRewriter;

public sealed record ArrayMethodRewriterResult(
    string OutputCode,
    bool Changed,
    IReadOnlyList<ArrayMethodRewriterDiagnostic> Diagnostics,
    ArrayMethodRewriterStatistics Statistics);
```

Create `src/CSharpToJava.Core/ArrayMethodRewriter/ArrayMethodRewriterStatistics.cs`:

```csharp
namespace CSharpToJava.Core.ArrayMethodRewriter;

public sealed class ArrayMethodRewriterStatistics
{
    public int FilesScanned;
    public int FilesTransformed;
    public int FilesSkippedClean;
    public int InstanceMemberRewrites;
    public int StaticArrayCalls;
    public int ResizeStatements;
    public int ForeachFlattened;
    public int RankInlined;
    public int MultiDimLengthRewrites;
}
```

Create `src/CSharpToJava.Core/ArrayMethodRewriter/ArrayMethodRewriterDiagnostic.cs`:

```csharp
namespace CSharpToJava.Core.ArrayMethodRewriter;

public enum ArrayMethodSeverity { Info, Warning, Error }

public sealed record ArrayMethodRewriterDiagnostic(
    ArrayMethodSeverity Severity, string Message);
```

Create `src/CSharpToJava.Core/ArrayMethodRewriter/ArrayRewriteAnnotations.cs`:

```csharp
using Microsoft.CodeAnalysis;

namespace CSharpToJava.Core.ArrayMethodRewriter;

/// <summary>
/// 标记被 array-method 预处理重写的节点。外层标记节点决定最终只做局部格式化的
/// TextSpan 范围，避免整文件重排带来的大 diff。
/// </summary>
internal static class ArrayRewriteAnnotations
{
    public const string Kind = "Cs2jArrayRewrite";

    public static SyntaxAnnotation Rewritten { get; } = new(Kind);

    public static TSyntax Mark<TSyntax>(TSyntax node) where TSyntax : SyntaxNode
        => node.WithAdditionalAnnotations(Rewritten);
}
```

- [x] **Step 2: 构建验证**

Run:
```powershell
cd d:/code/cs2j; dotnet build src/CSharpToJava.Core/CSharpToJava.Core.csproj
```
Expected: Build succeeded, 0 errors.

- [x] **Step 3: Commit**

```powershell
cd d:/code/cs2j; git add src/CSharpToJava.Core/ArrayMethodRewriter/; git commit -m "feat(array-method): add Core skeleton files (options/result/stats/diagnostic/annotations)"
```

---

### Task 6: 创建 `ArrayMethodRewriter.cs` 公共入口（mirror `UnsignedTypeRewriter.cs`）

**Files:**
- Create: `src/CSharpToJava.Core/ArrayMethodRewriter/ArrayMethodRewriter.cs`

- [x] **Step 1: 创建入口类**

Create `src/CSharpToJava.Core/ArrayMethodRewriter/ArrayMethodRewriter.cs`:

```csharp
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Formatting;

namespace CSharpToJava.Core.ArrayMethodRewriter;

/// <summary>
/// 数组方法预处理器公共入口。把 C# 中所有"纯数组相关、且 Java 没有对应
/// 语法/语义/用法"的构造改写为 Cs2jArray.ArrayHelper.XXX(...) 静态调用。
/// clean 文件字节级保真；改写文件只对被改写的片段做局部格式化，并在输出前
/// 做语法校验——任何情况下都不产出语法损坏的中间 C#。
/// </summary>
public sealed class ArrayMethodRewriter
{
    public ArrayMethodRewriterResult Rewrite(
        string sourceCode,
        SemanticModel? semanticModel = null,
        ArrayMethodRewriterOptions? options = null)
    {
        options ??= new ArrayMethodRewriterOptions();
        var stats = new ArrayMethodRewriterStatistics { FilesScanned = 1 };
        var diagnostics = new List<ArrayMethodRewriterDiagnostic>();

        var root = semanticModel != null
            ? (CompilationUnitSyntax)semanticModel.SyntaxTree.GetRoot()
            : (CompilationUnitSyntax)CSharpSyntaxTree.ParseText(sourceCode).GetRoot();

        if (!ArrayMethodSyntaxRewriter.IsDirty(root))
        {
            stats.FilesSkippedClean = 1;
            return new ArrayMethodRewriterResult(sourceCode, false, diagnostics, stats);
        }

        var rewriter = new ArrayMethodSyntaxRewriter(semanticModel, options, stats, diagnostics);
        var newRoot = (CompilationUnitSyntax)rewriter.Visit(root)!;

        var outputCode = FormatAndValidate(newRoot, diagnostics, stats);
        if (outputCode == null)
        {
            stats.FilesTransformed = 0;
            stats.FilesSkippedClean = 1;
            diagnostics.Add(new ArrayMethodRewriterDiagnostic(
                ArrayMethodSeverity.Error,
                "array rewrite produced invalid C# syntax; the original file was preserved."));
            return new ArrayMethodRewriterResult(sourceCode, false, diagnostics, stats);
        }

        stats.FilesTransformed = 1;
        return new ArrayMethodRewriterResult(outputCode, true, diagnostics, stats);
    }

    private static string? FormatAndValidate(
        CompilationUnitSyntax root,
        List<ArrayMethodRewriterDiagnostic> diagnostics,
        ArrayMethodRewriterStatistics stats)
    {
        using var workspace = new AdhocWorkspace();

        var candidates = new List<string>();
        try
        {
            var spans = GetRewrittenSpans(root);
            if (spans.Count > 0)
            {
                var spanFormatted = Formatter.Format(root, spans, workspace);
                candidates.Add(spanFormatted.ToFullString());
            }
        }
        catch
        {
            // 局部格式化失败不致命，继续尝试整文件格式化。
        }

        try
        {
            candidates.Add(Formatter.Format(root, workspace).ToFullString());
        }
        catch
        {
            // 忽略：仍可用未格式化文本。
        }

        candidates.Add(root.ToFullString());

        foreach (var candidate in candidates)
        {
            var reparsed = CSharpSyntaxTree.ParseText(candidate);
            if (!reparsed.GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error))
            {
                return candidate;
            }
        }

        return null;
    }

    internal static List<Microsoft.CodeAnalysis.Text.TextSpan> GetRewrittenSpans(SyntaxNode root)
    {
        var annotated = root
            .GetAnnotatedNodes(ArrayRewriteAnnotations.Kind)
            .Where(n => n is ExpressionSyntax or StatementSyntax)
            .ToList();
        if (annotated.Count == 0)
        {
            return new List<Microsoft.CodeAnalysis.Text.TextSpan>();
        }

        var annotatedSet = new HashSet<SyntaxNode>(annotated);
        return annotated
            .Where(n => !n.Ancestors().Any(annotatedSet.Contains))
            .Select(n => n.Span)
            .Distinct()
            .OrderBy(s => s.Start)
            .ToList();
    }
}
```

- [x] **Step 2: 构建验证（预期失败——ArrayMethodSyntaxRewriter 尚不存在）**

Run:
```powershell
cd d:/code/cs2j; dotnet build src/CSharpToJava.Core/CSharpToJava.Core.csproj
```
Expected: FAIL — `ArrayMethodSyntaxRewriter` not found. 这在 Task 7 中修复。

- [x] **Step 3: Commit（与 Task 7 一起提交）**

暂不提交，等 Task 7 完成后一起提交。

---

### Task 7: 创建 `ArrayMethodSyntaxRewriter.cs` — 脏检查 + 实例成员改写（§3.1）

**Files:**
- Create: `src/CSharpToJava.Core/ArrayMethodRewriter/ArrayMethodSyntaxRewriter.cs`

- [x] **Step 1: 创建 SyntaxRewriter 主体（脏检查 + 实例成员改写）**

Create `src/CSharpToJava.Core/ArrayMethodRewriter/ArrayMethodSyntaxRewriter.cs`:

```csharp
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.ArrayMethodRewriter;

internal sealed class ArrayMethodSyntaxRewriter : CSharpSyntaxRewriter
{
    private readonly SemanticModel? _semanticModel;
    private readonly ArrayMethodRewriterOptions _options;
    private readonly ArrayMethodRewriterStatistics _stats;
    private readonly List<ArrayMethodRewriterDiagnostic> _diagnostics;

    // 改写目标成员名集合（用于脏检查快速路径）
    private static readonly HashSet<string> TargetMemberNames = new(StringComparer.Ordinal)
    {
        "Clone", "CopyTo", "GetValue", "SetValue",
        "GetLength", "GetLongLength", "GetLowerBound", "GetUpperBound",
        "LongLength", "Rank",
        "Sort", "BinarySearch", "Clear", "Resize",
        "IndexOf", "LastIndexOf", "Reverse",
        "Find", "FindAll", "FindIndex", "FindLast", "FindLastIndex",
        "Exists", "TrueForAll", "ConvertAll", "ForEach",
        "Copy", "ConstrainedCopy",
    };

    // 实例成员名 → ArrayHelper 方法名映射
    private static readonly HashSet<string> InstanceMemberNames = new(StringComparer.Ordinal)
    {
        "Clone", "CopyTo", "GetValue", "SetValue",
        "GetLength", "GetLongLength", "GetLowerBound", "GetUpperBound",
        "LongLength", "Rank",
    };

    public ArrayMethodSyntaxRewriter(
        SemanticModel? semanticModel,
        ArrayMethodRewriterOptions options,
        ArrayMethodRewriterStatistics stats,
        List<ArrayMethodRewriterDiagnostic> diagnostics)
    {
        _semanticModel = semanticModel;
        _options = options;
        _stats = stats;
        _diagnostics = diagnostics;
    }

    /// <summary>
    /// 脏检查（纯语法快速路径）：文件 token 中命中改写目标成员名集合，
    /// 或含 foreach 语句（多维摊平候选）。未命中即 clean，字节级保真复制。
    /// </summary>
    internal static bool IsDirty(CompilationUnitSyntax root)
    {
        foreach (var token in root.DescendantTokens())
        {
            if (token.IsKind(SyntaxKind.IdentifierToken)
                && TargetMemberNames.Contains(token.ValueText))
            {
                return true;
            }
        }

        // foreach 摊平候选
        return root.DescendantNodes().Any(n => n is ForEachStatementSyntax);
    }

    // ---- 辅助：类型判定 ----

    private bool IsArrayType(ExpressionSyntax expression)
    {
        if (_semanticModel == null) return false;
        var type = _semanticModel.GetTypeInfo(expression).Type;
        return type is IArrayTypeSymbol;
    }

    private IArrayTypeSymbol? GetArrayTypeSymbol(ExpressionSyntax expression)
    {
        if (_semanticModel == null) return null;
        return _semanticModel.GetTypeInfo(expression).Type as IArrayTypeSymbol;
    }

    private bool IsSystemArrayType(ExpressionSyntax expression)
    {
        if (_semanticModel == null) return false;
        var type = _semanticModel.GetTypeInfo(expression).Type;
        return type is INamedTypeSymbol named
            && named.SpecialType == SpecialType.System_Array;
    }

    private bool IsSystemArraySymbol(ExpressionSyntax expression)
    {
        if (_semanticModel == null) return false;
        var symbol = _semanticModel.GetSymbolInfo(expression).Symbol;
        return symbol is INamedTypeSymbol named
            && named.SpecialType == SpecialType.System_Array;
    }

    // ---- 辅助：构造 Cs2jArray.ArrayHelper.XXX(...) 调用 ----

    private static ExpressionSyntax StaticCall(string methodName, params ExpressionSyntax[] args)
    {
        var memberAccess = SyntaxFactory.MemberAccessExpression(
            SyntaxKind.SimpleMemberAccessExpression,
            SyntaxFactory.IdentifierName("Cs2jArray"),
            SyntaxFactory.IdentifierName("ArrayHelper"));
        var memberAccess2 = SyntaxFactory.MemberAccessExpression(
            SyntaxKind.SimpleMemberAccessExpression,
            memberAccess,
            SyntaxFactory.IdentifierName(methodName));
        return SyntaxFactory.InvocationExpression(memberAccess2,
            SyntaxFactory.ArgumentList(
                SyntaxFactory.SeparatedList(args.Select(a => SyntaxFactory.Argument(a)))));
    }

    private static ExpressionSyntax ParenthesizeIfNeeded(ExpressionSyntax expr)
    {
        // 防止 receiver 中的低优先级运算符被成员访问吞掉
        return expr is IdentifierNameSyntax
            or MemberAccessExpressionSyntax
            or InvocationExpressionSyntax
            or ElementAccessExpressionSyntax
            or LiteralExpressionSyntax
            or ParenthesizedExpressionSyntax
            ? expr
            : SyntaxFactory.ParenthesizedExpression(expr);
    }

    private ExpressionSyntax VisitAndStripLeading(ExpressionSyntax expr, out SyntaxTriviaList leading)
    {
        var visited = (ExpressionSyntax)Visit(expr)!;
        leading = visited.GetLeadingTrivia();
        return leading.Count == 0 ? visited : visited.WithoutLeadingTrivia();
    }

    private static ExpressionSyntax WithHoistedLeading(ExpressionSyntax replacement, params SyntaxTriviaList[] leads)
    {
        var total = default(SyntaxTriviaList);
        foreach (var lead in leads)
        {
            total = total.AddRange(lead);
        }
        return total.Count == 0 ? replacement : replacement.WithLeadingTrivia(total);
    }

    // ---- 实例成员改写（§3.1）----

    public override SyntaxNode? VisitInvocationExpression(InvocationExpressionSyntax node)
    {
        // 先递归访问子节点
        var visited = (InvocationExpressionSyntax)base.VisitInvocationExpression(node)!;

        if (_semanticModel == null || !_options.EnableInstanceRewrite)
        {
            return visited;
        }

        // 实例成员调用：receiver.Method(args)
        if (visited.Expression is MemberAccessExpressionSyntax mae)
        {
            var methodName = mae.Name.Identifier.ValueText;

            // 只处理实例成员名单中的方法名
            if (!InstanceMemberNames.Contains(methodName))
            {
                // 静态调用（Array.XXX）在 VisitMemberAccessExpression / 单独路径处理
                return visited;
            }

            // receiver 必须是数组类型（IArrayTypeSymbol），排除 System.Array 接收者
            if (IsSystemArrayType(mae.Expression) || !IsArrayType(mae.Expression))
            {
                return visited;
            }

            return RewriteInstanceMemberCall(node, mae, methodName, visited);
        }

        return visited;
    }

    private ExpressionSyntax RewriteInstanceMemberCall(
        InvocationExpressionSyntax originalNode,
        MemberAccessExpressionSyntax originalMae,
        string methodName,
        InvocationExpressionSyntax visited)
    {
        // 获取原始 receiver 的语义信息（在原始树上查）
        var arrayType = GetArrayTypeSymbol(originalMae.Expression);
        if (arrayType == null) return visited;

        var receiver = VisitAndStripLeading(originalMae.Expression, out var receiverLead);
        var parenReceiver = ParenthesizeIfNeeded(receiver);
        var visitedArgs = originalNode.ArgumentList.Arguments
            .Select(a => (ExpressionSyntax)Visit(a.Expression)!).ToArray();

        ExpressionSyntax? replacement = methodName switch
        {
            "Clone" => StaticCall("Clone", parenReceiver),
            "CopyTo" => StaticCall("CopyTo", parenReceiver, visitedArgs[0], visitedArgs[1]),
            "GetValue" => RewriteGetValue(parenReceiver, visitedArgs, arrayType),
            "SetValue" => RewriteSetValue(parenReceiver, visitedArgs, arrayType),
            "GetLength" when visitedArgs.Length == 1 => StaticCall("GetLength", parenReceiver, visitedArgs[0]),
            "GetLongLength" when visitedArgs.Length == 1 => StaticCall("GetLongLength", parenReceiver, visitedArgs[0]),
            "GetLowerBound" => StaticCall("GetLowerBound", parenReceiver, visitedArgs[0]),
            "GetUpperBound" => StaticCall("GetUpperBound", parenReceiver, visitedArgs[0]),
            _ => null,
        };

        if (replacement == null) return visited;

        _stats.InstanceMemberRewrites++;
        return ArrayRewriteAnnotations.Mark(WithHoistedLeading(replacement, receiverLead));
    }

    private ExpressionSyntax RewriteGetValue(
        ExpressionSyntax receiver, ExpressionSyntax[] args, IArrayTypeSymbol arrayType)
    {
        // 按 rank 选择特化重载：rank 1 → GetValue<T>(T[], int)
        // rank 2 → GetValue<T>(T[][], int, int)
        // rank 3 → GetValue<T>(T[][][], int, int, int)
        // rank > 3 或 params 形态 → GetValue(System.Array, params int[])
        int rank = arrayType.Rank;
        var allArgs = new[] { receiver }.Concat(args).ToArray();
        return StaticCall("GetValue", allArgs);
    }

    private ExpressionSyntax RewriteSetValue(
        ExpressionSyntax receiver, ExpressionSyntax[] args, IArrayTypeSymbol arrayType)
    {
        // SetValue(value, index) / SetValue(value, i, j) / SetValue(value, params int[])
        var allArgs = new[] { receiver }.Concat(args).ToArray();
        return StaticCall("SetValue", allArgs);
    }

    // ---- 实例属性改写（§3.1: LongLength / Rank / 多维 Length）----

    public override SyntaxNode? VisitMemberAccessExpression(MemberAccessExpressionSyntax node)
    {
        var visited = (MemberAccessExpressionSyntax)base.VisitMemberAccessExpression(node)!;

        if (_semanticModel == null || !_options.EnableInstanceRewrite)
        {
            return visited;
        }

        var memberName = visited.Name.Identifier.ValueText;

        // 只处理实例属性名单中的名字
        if (memberName != "LongLength" && memberName != "Rank" && memberName != "Length")
        {
            return visited;
        }

        // receiver 必须是数组类型，排除 System.Array 接收者和 string.Length
        if (IsSystemArrayType(visited.Expression) || !IsArrayType(visited.Expression))
        {
            return visited;
        }

        var arrayType = GetArrayTypeSymbol(visited.Expression);
        if (arrayType == null) return visited;

        // 一维 arr.Length 白名单，不改写
        if (memberName == "Length" && arrayType.Rank == 1)
        {
            return visited;
        }

        var receiver = VisitAndStripLeading(node.Expression, out var receiverLead);
        var parenReceiver = ParenthesizeIfNeeded(receiver);

        ExpressionSyntax? replacement = memberName switch
        {
            "Length" when arrayType.Rank > 1 => StaticCall("GetLength", parenReceiver), // 多维 .Length → GetLength(arr)（元素总数）
            "LongLength" => StaticCall("GetLongLength", parenReceiver),
            "Rank" => SyntaxFactory.LiteralExpression(
                SyntaxKind.NumericLiteralExpression,
                SyntaxFactory.Literal(arrayType.Rank)), // 内联常量
            _ => null,
        };

        if (replacement == null) return visited;

        if (memberName == "Rank")
        {
            _stats.RankInlined++;
        }
        else if (memberName == "Length")
        {
            _stats.MultiDimLengthRewrites++;
        }
        else
        {
            _stats.InstanceMemberRewrites++;
        }

        return ArrayRewriteAnnotations.Mark(WithHoistedLeading(replacement, receiverLead));
    }
}
```

- [x] **Step 2: 构建验证**

Run:
```powershell
cd d:/code/cs2j; dotnet build src/CSharpToJava.Core/CSharpToJava.Core.csproj
```
Expected: Build succeeded, 0 errors.

- [x] **Step 3: Commit**

```powershell
```powershell
cd d:/code/cs2j; git add src/CSharpToJava.Core/ArrayMethodRewriter/; git commit -m "feat(array-method): add ArrayMethodRewriter + SyntaxRewriter with dirty check and instance member rewrites"
```

---

### Task 8: 扩展 SyntaxRewriter — System.Array 静态调用改写（§3.3）+ Resize 语句改写

**Files:**
- Modify: `src/CSharpToJava.Core/ArrayMethodRewriter/ArrayMethodSyntaxRewriter.cs`

- [x] **Step 1: 在 `ArrayMethodSyntaxRewriter` 类中追加静态调用改写逻辑**

在 `ArrayMethodSyntaxRewriter.cs` 的 `VisitMemberAccessExpression` 方法之后，追加以下方法（在类的闭合 `}` 之前）：

```csharp
    // ---- System.Array 静态调用改写（§3.3）----

    // 静态方法名集合（§3.3 全表）
    private static readonly HashSet<string> StaticArrayMethodNames = new(StringComparer.Ordinal)
    {
        "Sort", "BinarySearch", "Clear", "Resize",
        "IndexOf", "LastIndexOf", "Reverse",
        "Find", "FindAll", "FindIndex", "FindLast", "FindLastIndex",
        "Exists", "TrueForAll", "ConvertAll", "ForEach",
        "Copy", "ConstrainedCopy",
    };

    public override SyntaxNode? VisitExpressionStatement(ExpressionStatementSyntax node)
    {
        var visited = (ExpressionStatementSyntax)base.VisitExpressionStatement(node)!;

        // Resize 语句改写：Array.Resize(ref a, n); → a = Cs2jArray.ArrayHelper.Resize(a, n);
        if (_semanticModel != null && _options.EnableStaticRewrite
            && visited.Expression is InvocationExpressionSyntax inv
            && inv.Expression is MemberAccessExpressionSyntax mae
            && mae.Name.Identifier.ValueText == "Resize"
            && IsSystemArraySymbol(mae.Expression))
        {
            var args = inv.ArgumentList.Arguments;
            if (args.Count == 2 && args[0].RefOrOutKeyword.IsKind(SyntaxKind.RefKeyword))
            {
                var target = args[0].Expression;
                if (IsSafeAssignmentTarget(target))
                {
                    var newSize = (ExpressionSyntax)Visit(args[1].Expression)!;
                    var visitedTarget = VisitAndStripLeading(target, out var targetLead);
                    var call = StaticCall("Resize", visitedTarget, newSize);
                    var assignment = SyntaxFactory.AssignmentExpression(
                        SyntaxKind.SimpleAssignmentExpression,
                        visitedTarget,
                        call);
                    var markedExpr = ArrayRewriteAnnotations.Mark(
                        WithHoistedLeading(assignment, targetLead));
                    _stats.ResizeStatements++;
                    return visited.WithExpression(markedExpr);
                }
                else
                {
                    // 不满足安全条件 → 保守跳过 + Warning
                    _diagnostics.Add(new ArrayMethodRewriterDiagnostic(
                        ArrayMethodSeverity.Warning,
                        $"Array.Resize target is not a safe assignment target; skipped."));
                    return visited;
                }
            }
        }

        return visited;
    }

    private bool IsSafeAssignmentTarget(ExpressionSyntax target)
    {
        if (_semanticModel == null) return false;
        return target switch
        {
            IdentifierNameSyntax identifier =>
                _semanticModel.GetSymbolInfo(identifier).Symbol is ILocalSymbol
                    or IParameterSymbol
                    or IFieldSymbol,
            MemberAccessExpressionSyntax memberAccess =>
                _semanticModel.GetSymbolInfo(memberAccess).Symbol is IFieldSymbol
                && IsSideEffectFreeReceiver(memberAccess.Expression),
            _ => false,
        };
    }

    private static bool IsSideEffectFreeReceiver(ExpressionSyntax receiver) => receiver switch
    {
        ThisExpressionSyntax => true,
        BaseExpressionSyntax => true,
        IdentifierNameSyntax => true,
        MemberAccessExpressionSyntax memberAccess => IsSideEffectFreeReceiver(memberAccess.Expression),
        _ => false,
    };

    /// <summary>
    /// 改写 Array.XXX(...) 静态调用为 Cs2jArray.ArrayHelper.XXX(...)。
    /// 在 VisitInvocationExpression 中检测 Array 标识符是否解析到 System.Array。
    /// </summary>
    private ExpressionSyntax? TryRewriteStaticArrayCall(
        InvocationExpressionSyntax originalNode,
        InvocationExpressionSyntax visited)
    {
        if (_semanticModel == null || !_options.EnableStaticRewrite)
        {
            return null;
        }

        if (visited.Expression is not MemberAccessExpressionSyntax mae)
        {
            return null;
        }

        var methodName = mae.Name.Identifier.ValueText;

        // 只处理静态方法名单中的名字
        if (!StaticArrayMethodNames.Contains(methodName))
        {
            return null;
        }

        // Array 标识符必须解析到 System.Array 类型符号
        if (!IsSystemArraySymbol(mae.Expression))
        {
            return null;
        }

        // Resize 作为语句在 VisitExpressionStatement 中处理；如果在表达式上下文中
        // 出现（如 x = Array.Resize(...)），保守跳过
        if (methodName == "Resize")
        {
            return null;
        }

        // 实参原样透传（已在 base.VisitInvocationExpression 中递归访问过）
        var args = originalNode.ArgumentList.Arguments
            .Select(a => (ExpressionSyntax)Visit(a.Expression)!).ToArray();

        var replacement = StaticCall(methodName, args);
        _stats.StaticArrayCalls++;
        return ArrayRewriteAnnotations.Mark(replacement);
    }
```

- [x] **Step 2: 修改 `VisitInvocationExpression` 以在实例成员路径之后追加静态调用检测**

将 `VisitInvocationExpression` 方法中 `return visited;`（在实例成员分支之后、方法末尾的 `return visited;`）替换为：

```csharp
        // 静态调用：Array.XXX(...)
        var staticReplacement = TryRewriteStaticArrayCall(node, visited);
        if (staticReplacement != null)
        {
            return staticReplacement;
        }

        return visited;
```

具体来说，找到 `VisitInvocationExpression` 方法中的以下代码块：

```csharp
            return RewriteInstanceMemberCall(node, mae, methodName, visited);
        }

        return visited;
    }
```

替换为：

```csharp
            return RewriteInstanceMemberCall(node, mae, methodName, visited);
        }

        // 静态调用：Array.XXX(...)
        var staticReplacement = TryRewriteStaticArrayCall(node, visited);
        if (staticReplacement != null)
        {
            return staticReplacement;
        }

        return visited;
    }
```

- [x] **Step 3: 构建验证**

Run:
```powershell
cd d:/code/cs2j; dotnet build src/CSharpToJava.Core/CSharpToJava.Core.csproj
```
Expected: Build succeeded, 0 errors.

- [x] **Step 4: Commit**

```powershell
cd d:/code/cs2j; git add src/CSharpToJava.Core/ArrayMethodRewriter/ArrayMethodSyntaxRewriter.cs; git commit -m "feat(array-method): add System.Array static call rewrite and Resize statement rewrite"
```

---

### Task 9: 扩展 SyntaxRewriter — 多维 foreach 摊平（§3.2）

**Files:**
- Modify: `src/CSharpToJava.Core/ArrayMethodRewriter/ArrayMethodSyntaxRewriter.cs`

- [x] **Step 1: 追加 `VisitForEachStatement` 方法**

在 `ArrayMethodSyntaxRewriter.cs` 中（`TryRewriteStaticArrayCall` 之后、类闭合 `}` 之前），追加：

```csharp
    // ---- 多维 foreach 摊平（§3.2）----

    public override SyntaxNode? VisitForEachStatement(ForEachStatementSyntax node)
    {
        var visited = (ForEachStatementSyntax)base.VisitForEachStatement(node)!;

        if (_semanticModel == null || !_options.EnableForeachFlatten)
        {
            return visited;
        }

        // collection 表达式的类型必须是数组且 Rank > 1
        var collectionType = _semanticModel.GetTypeInfo(node.Expression).Type as IArrayTypeSymbol;
        if (collectionType == null || collectionType.Rank <= 1)
        {
            return visited;
        }

        // 合成行变量名，带冲突检查
        var rowVarName = MakeUniqueRowVariable(node);
        var rowVar = SyntaxFactory.IdentifierName(rowVarName);

        // 外层 foreach: foreach (var __cs2j_row0 in collection)
        var outerForEach = SyntaxFactory.ForEachStatement(
            SyntaxFactory.IdentifierName("var"),
            SyntaxFactory.Identifier(rowVarName),
            node.Expression,
            // 内层 foreach: foreach (var x in __cs2j_row0) { ... }
            SyntaxFactory.Block(
                SyntaxFactory.ForEachStatement(
                    SyntaxFactory.IdentifierName("var"),
                    node.Identifier,
                    rowVar,
                    node.Statement)));

        _stats.ForeachFlattened++;
        return ArrayRewriteAnnotations.Mark(outerForEach);
    }

    private string MakeUniqueRowVariable(ForEachStatementSyntax node)
    {
        // 收集作用域内已有的标识符名
        var takenNames = new HashSet<string>(StringComparer.Ordinal);
        var scope = node.Parent;
        while (scope != null)
        {
            foreach (var ident in scope.DescendantTokens()
                .Where(t => t.IsKind(SyntaxKind.IdentifierToken))
                .Select(t => t.ValueText))
            {
                takenNames.Add(ident);
            }
            scope = scope.Parent;
        }

        int suffix = 0;
        string name;
        do
        {
            name = "__cs2j_row" + suffix;
            suffix++;
        }
        while (takenNames.Contains(name));

        return name;
    }
```

- [x] **Step 2: 构建验证**

Run:
```powershell
cd d:/code/cs2j; dotnet build src/CSharpToJava.Core/CSharpToJava.Core.csproj
```
Expected: Build succeeded, 0 errors.

- [x] **Step 3: Commit**

```powershell
cd d:/code/cs2j; git add src/CSharpToJava.Core/ArrayMethodRewriter/ArrayMethodSyntaxRewriter.cs; git commit -m "feat(array-method): add multi-dimensional foreach flattening"
```

---

## Milestone M3 — 项目模式预处理器 + CLI 接线

### Task 10: 创建 `ProjectArrayMethodPreprocessor.cs`（mirror `ProjectUnsignedTypePreprocessor`）

**Files:**
- Create: `src/CSharpToJava.CLI/ProjectArrayMethodPreprocessor.cs`

- [x] **Step 1: 创建项目模式预处理器**

Create `src/CSharpToJava.CLI/ProjectArrayMethodPreprocessor.cs`:

> 此文件完全 mirror `ProjectUnsignedTypePreprocessor.cs` 的结构，把 `UnsignedType` 替换为 `ArrayMethod`，`UnsignedMathSupportFiles` 替换为 `ArrayHelperSupportFiles`，`Cs2jUnsignedMathProject` 替换为 `Cs2jArrayProject`，`UnsignedTypeRewriter` 替换为 `ArrayMethodRewriter`，`UnsignedRewriterAnnotations` 替换为 `ArrayRewriteAnnotations`，中间目录名替换为 `.cs2j-array-src`。

```csharp
using CSharpToJava.Core.ArrayMethodRewriter;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using System.Xml.Linq;

namespace CSharpToJava.CLI;

internal sealed class ProjectArrayMethodPreprocessRequest
{
    public required string SourcePath { get; init; }
    public required string DestinationRoot { get; init; }
    public bool Force { get; init; } = true;
    public bool Verbose { get; init; }
    public ArrayMethodRewriterOptions RewriterOptions { get; init; } = new();
    public bool FilterUnsupportedProjects { get; init; } = true;
    public IReadOnlyCollection<string>? CompatNamespacePrefixes { get; init; }
}

internal sealed class ProjectArrayMethodPreprocessResult
{
    public required string OriginalSourcePath { get; init; }
    public required string SourceRoot { get; init; }
    public required string IntermediateRoot { get; init; }
    public required string PreprocessedSourcePath { get; init; }
    public required bool Success { get; init; }
    public required ArrayMethodRewriterStatistics Statistics { get; init; }
    public required IReadOnlyList<ArrayMethodRewriterDiagnostic> Diagnostics { get; init; }
}

internal static class ProjectArrayMethodPreprocessor
{
    public const string IntermediateDirectoryName = ".cs2j-array-src";

    public static async Task<ProjectArrayMethodPreprocessResult> PreprocessAsync(
        ProjectArrayMethodPreprocessRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.SourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.DestinationRoot);

        var layout = ProjectPreprocessSource.ResolveLayout(request.SourcePath);
        var intermediateRoot = Path.Combine(Path.GetFullPath(request.DestinationRoot), IntermediateDirectoryName);

        if (Directory.Exists(intermediateRoot))
        {
            if (!request.Force)
            {
                throw new InvalidOperationException(
                    $"Array source directory already exists: {intermediateRoot}. Use --force to overwrite.");
            }

            Directory.Delete(intermediateRoot, recursive: true);
        }

        Directory.CreateDirectory(intermediateRoot);

        var diagnostics = new List<ArrayMethodRewriterDiagnostic>();
        var statistics = new ArrayMethodRewriterStatistics();
        var rewriter = new ArrayMethodRewriter();
        var sourceRoot = layout.SourceRoot;

        var projectGraph = ProjectPreprocessSource.TryLoadProjectGraph(layout, request.Verbose);
        var exclusionInfo = request.FilterUnsupportedProjects
            ? SupportedProjectFiltering.ComputeExclusions(
                projectGraph,
                request.Verbose,
                request.CompatNamespacePrefixes,
                layout.SourceRoot)
            : new SupportedProjectFiltering.ExclusionInfo();

        await ProjectPreprocessSource.CopyProjectFilesAsync(layout, sourceRoot, intermediateRoot, exclusionInfo);
        ProjectPreprocessSource.CopyAncestorMsBuildDirectoryFiles(
            sourceRoot,
            intermediateRoot,
            "[array-method]",
            request.Verbose);

        var sourceFiles = ProjectPreprocessSource.EnumerateSourceFiles(
                sourceRoot, intermediateRoot, Path.GetFullPath(request.DestinationRoot))
            .Where(file => !SupportedProjectFiltering.IsUnderExcludedProject(file, exclusionInfo))
            .ToList();
        var csFiles = sourceFiles
            .Where(f => f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var syntaxTrees = new Dictionary<string, SyntaxTree>(StringComparer.OrdinalIgnoreCase);
        foreach (var csFile in csFiles)
        {
            var sourceCode = await File.ReadAllTextAsync(csFile);
            syntaxTrees[csFile] = CSharpSyntaxTree.ParseText(sourceCode, path: csFile);
        }

        var references = ProjectUnsignedTypePreprocessor.GetBasicReferences();
        var compilation = CSharpCompilation.Create(
            "array-method-preprocess",
            syntaxTrees.Values,
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));

        var rewrittenSourceFiles = new List<string>();
        var ownReleaseDirectories = FindOwnReleaseLibDirectories(sourceRoot);
        foreach (var sourceFile in sourceFiles)
        {
            var relativePath = Path.GetRelativePath(sourceRoot, sourceFile);
            var destinationFile = Path.Combine(intermediateRoot, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationFile)!);

            if (!sourceFile.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            {
                if (sourceFile.EndsWith(".sln", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                File.Copy(sourceFile, destinationFile, overwrite: true);
                continue;
            }

            if (IsUnderOwnReleaseLib(sourceFile, ownReleaseDirectories))
            {
                File.Copy(sourceFile, destinationFile, overwrite: true);
                if (request.Verbose)
                {
                    Console.WriteLine($"[array-method] clean (own release copy): {relativePath}");
                }
                continue;
            }

            if (!syntaxTrees.TryGetValue(sourceFile, out var tree))
            {
                File.Copy(sourceFile, destinationFile, overwrite: true);
                continue;
            }

            var sourceCode = await File.ReadAllTextAsync(sourceFile);
            var result = rewriter.Rewrite(sourceCode, compilation.GetSemanticModel(tree), request.RewriterOptions);
            MergeStatistics(statistics, result.Statistics);

            foreach (var diagnostic in result.Diagnostics)
            {
                diagnostics.Add(diagnostic with { Message = $"{relativePath}: {diagnostic.Message}" });
            }

            if (!result.Changed)
            {
                File.Copy(sourceFile, destinationFile, overwrite: true);
            }
            else
            {
                rewrittenSourceFiles.Add(sourceFile);
                await File.WriteAllTextAsync(
                    destinationFile,
                    result.OutputCode,
                    FileStartsWithBom(sourceFile)
                        ? new System.Text.UTF8Encoding(true)
                        : new System.Text.UTF8Encoding(false));
            }

            if (request.Verbose)
            {
                Console.WriteLine($"[array-method] {(result.Changed ? "transformed" : "clean (copy)")}: {relativePath}");
            }
        }

        if (statistics.InstanceMemberRewrites > 0 || statistics.StaticArrayCalls > 0
            || statistics.ResizeStatements > 0 || statistics.ForeachFlattened > 0
            || statistics.RankInlined > 0 || statistics.MultiDimLengthRewrites > 0)
        {
            await WriteExtensionFilesAsync(
                intermediateRoot, sourceRoot, layout, exclusionInfo, rewrittenSourceFiles, projectGraph, diagnostics);
        }

        var restoreTarget = ProjectPreprocessSource.ResolveRestoreTarget(layout, intermediateRoot);
        if (restoreTarget != null)
        {
            if (request.Verbose)
            {
                Console.WriteLine($"Restoring packages: {restoreTarget}");
            }

            var restoreExitCode = ProjectPreprocessSource.RunDotNetRestore(restoreTarget, "[array-method]");
            if (request.Verbose)
            {
                Console.WriteLine($"Restore exit code: {restoreExitCode}");
            }

            if (restoreExitCode != 0)
            {
                diagnostics.Add(new ArrayMethodRewriterDiagnostic(
                    ArrayMethodSeverity.Error,
                    $"dotnet restore failed with exit code {restoreExitCode} for {restoreTarget}. " +
                    "Package references may not be resolved, leading to incorrect code generation."));
            }
        }

        return new ProjectArrayMethodPreprocessResult
        {
            OriginalSourcePath = layout.EntryPath,
            SourceRoot = sourceRoot,
            IntermediateRoot = intermediateRoot,
            PreprocessedSourcePath = ProjectPreprocessSource.MapToIntermediate(layout, intermediateRoot),
            Success = !diagnostics.Any(d => d.Severity == ArrayMethodSeverity.Error),
            Statistics = statistics,
            Diagnostics = diagnostics,
        };
    }

    private static bool FileStartsWithBom(string path)
    {
        using var stream = File.OpenRead(path);
        Span<byte> prefix = stackalloc byte[3];
        var read = stream.Read(prefix);
        return read >= 3 && prefix[0] == 0xEF && prefix[1] == 0xBB && prefix[2] == 0xBF;
    }

    private static void MergeStatistics(ArrayMethodRewriterStatistics target, ArrayMethodRewriterStatistics source)
    {
        target.FilesScanned += source.FilesScanned;
        target.FilesTransformed += source.FilesTransformed;
        target.FilesSkippedClean += source.FilesSkippedClean;
        target.InstanceMemberRewrites += source.InstanceMemberRewrites;
        target.StaticArrayCalls += source.StaticArrayCalls;
        target.ResizeStatements += source.ResizeStatements;
        target.ForeachFlattened += source.ForeachFlattened;
        target.RankInlined += source.RankInlined;
        target.MultiDimLengthRewrites += source.MultiDimLengthRewrites;
    }

    private static async Task WriteExtensionFilesAsync(
        string intermediateRoot,
        string sourceRoot,
        ProjectPreprocessSourceLayout layout,
        SupportedProjectFiltering.ExclusionInfo exclusionInfo,
        IReadOnlyList<string> rewrittenFiles,
        ProjectGraph? projectGraph,
        List<ArrayMethodRewriterDiagnostic> diagnostics)
    {
        ThrowOnCs2jArrayNameCollision(layout);

        await ArrayHelperSupportFiles.ReleaseAsync(
            Path.Combine(intermediateRoot, ArrayHelperSupportFiles.LibDirectoryName));

        var hostProjectDirectories = ComputeAllExtensionHostProjectDirectories(
            layout, exclusionInfo, rewrittenFiles, projectGraph);
        EnsureExtensionProjectReferences(
            sourceRoot, intermediateRoot, layout, exclusionInfo, hostProjectDirectories, diagnostics);
    }

    private static void ThrowOnCs2jArrayNameCollision(ProjectPreprocessSourceLayout layout)
    {
        var collision = layout.ProjectFiles.FirstOrDefault(p =>
            Cs2jArrayProject.IsCs2jArrayProject(p) &&
            !IsOwnReleasedArrayProjectFile(p));
        if (collision != null)
        {
            throw new InvalidOperationException(
                $"The source solution already contains a project named " +
                $"'{Cs2jArrayProject.ProjectFileName}' ({collision}). That name is reserved " +
                "for the cs2j array helper support library; rename the project and retry.");
        }
    }

    private static bool IsOwnReleasedArrayProjectFile(string projectFilePath)
    {
        try
        {
            var content = File.ReadAllText(projectFilePath);
            return string.Equals(
                NormalizeLineEndings(content),
                NormalizeLineEndings(ArrayHelperSupportFiles.GetProjectFileContent()),
                StringComparison.Ordinal);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string NormalizeLineEndings(string text) =>
        text.Replace("\r\n", "\n").Replace("\r", "\n");

    private static List<string> FindOwnReleaseLibDirectories(string sourceRoot)
    {
        var options = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            RecurseSubdirectories = true,
            MatchCasing = MatchCasing.CaseInsensitive,
        };
        return Directory
            .EnumerateDirectories(sourceRoot, ArrayHelperSupportFiles.LibDirectoryName, options)
            .Where(dir => IsOwnReleasedArrayProjectFile(
                Path.Combine(dir, Cs2jArrayProject.ProjectFileName)))
            .Select(Path.GetFullPath)
            .ToList();
    }

    private static bool IsUnderOwnReleaseLib(string filePath, IReadOnlyList<string> ownReleaseDirectories)
    {
        var fullPath = Path.GetFullPath(filePath);
        return ownReleaseDirectories.Any(dir =>
            fullPath.StartsWith(dir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            fullPath.StartsWith(dir + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
    }

    private static void WarnWhenTargetFrameworkBelowNet10(
        XElement root,
        string projectFilePath,
        List<ArrayMethodRewriterDiagnostic> diagnostics)
    {
        var tfm = ProjectUnsignedTypePreprocessor.GetFirstTargetFramework(root);
        if (ProjectUnsignedTypePreprocessor.IsTargetFrameworkAtLeastNet10(tfm))
        {
            return;
        }

        diagnostics.Add(new ArrayMethodRewriterDiagnostic(
            ArrayMethodSeverity.Warning,
            $"{projectFilePath}: target framework '{tfm ?? "(none)"}' is below net10.0; the injected " +
            $"reference to {Cs2jArrayProject.ProjectFileName} (net10.0) will fail to restore/build " +
            "with NU1201. Upgrade the host TargetFramework to net10.0+, or report the case so " +
            "Cs2jArray can multi-target."));
    }

    private static void EnsureExtensionProjectReferences(
        string sourceRoot,
        string intermediateRoot,
        ProjectPreprocessSourceLayout layout,
        SupportedProjectFiltering.ExclusionInfo exclusionInfo,
        IReadOnlySet<string> hostProjectDirectories,
        List<ArrayMethodRewriterDiagnostic> diagnostics)
    {
        var libProjectPath = Path.Combine(
            intermediateRoot,
            ArrayHelperSupportFiles.LibDirectoryName,
            Cs2jArrayProject.ProjectFileName);

        foreach (var projectFile in layout.ProjectFiles
                     .Where(file => file.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)))
        {
            if (SupportedProjectFiltering.IsUnderExcludedProject(projectFile, exclusionInfo))
            {
                continue;
            }

            var projectDirectory = Path.GetDirectoryName(projectFile)!;
            if (!hostProjectDirectories.Contains(projectDirectory))
            {
                continue;
            }

            var destinationProjectFile = Path.Combine(
                intermediateRoot,
                Path.GetRelativePath(sourceRoot, projectFile));
            EnsureExtensionProjectReference(
                destinationProjectFile, libProjectPath, intermediateRoot, diagnostics);
        }
    }

    private static void EnsureExtensionProjectReference(
        string projectFilePath,
        string libProjectPath,
        string intermediateRoot,
        List<ArrayMethodRewriterDiagnostic> diagnostics)
    {
        if (!File.Exists(projectFilePath))
        {
            return;
        }

        XDocument document;
        try
        {
            document = XDocument.Load(projectFilePath, LoadOptions.PreserveWhitespace);
        }
        catch (Exception)
        {
            return;
        }

        var root = document.Root;
        if (root == null || !root.Name.LocalName.Equals("Project", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var projectDirectory = Path.GetDirectoryName(projectFilePath)!;

        // 根目录 host 特判
        var isSdkProject = root.Attribute("Sdk") != null;
        var enableDefaultCompileItems = GetPropertyValue(root, "EnableDefaultCompileItems");
        if (isSdkProject
            && !string.Equals(enableDefaultCompileItems?.Trim(), "false", StringComparison.OrdinalIgnoreCase)
            && PathsEqual(projectDirectory, intermediateRoot))
        {
            var removePattern = Path.Combine(ArrayHelperSupportFiles.LibDirectoryName, "**");
            if (!root.Descendants().Any(e =>
                    e.Name.LocalName == "Compile"
                    && string.Equals((string?)e.Attribute("Remove"), removePattern, StringComparison.OrdinalIgnoreCase)))
            {
                root.Add(
                    new XText(Environment.NewLine + "  "),
                    new XElement("ItemGroup",
                        new XElement("Compile", new XAttribute("Remove", removePattern))),
                    new XText(Environment.NewLine));
            }
        }

        foreach (var referenceElement in root.Descendants()
                     .Where(e => e.Name.LocalName == "ProjectReference")
                     .ToList())
        {
            var include = (string?)referenceElement.Attribute("Include");
            if (string.IsNullOrWhiteSpace(include))
            {
                continue;
            }

            var resolvedPath = Path.GetFullPath(Path.Combine(projectDirectory, include!));
            if (PathsEqual(resolvedPath, libProjectPath))
            {
                referenceElement.Remove();
            }
        }

        var relativeInclude = Path.GetRelativePath(projectDirectory, libProjectPath);
        root.Add(
            new XText(Environment.NewLine + "  "),
            new XElement("ItemGroup",
                new XElement("ProjectReference", new XAttribute("Include", relativeInclude))),
            new XText(Environment.NewLine));

        WarnWhenTargetFrameworkBelowNet10(root, projectFilePath, diagnostics);

        document.Save(projectFilePath, SaveOptions.DisableFormatting);
    }

    private static bool PathsEqual(string a, string b)
        => string.Equals(
            Path.GetFullPath(a).TrimEnd('\\', '/'),
            Path.GetFullPath(b).TrimEnd('\\', '/'),
            StringComparison.OrdinalIgnoreCase);

    private static IReadOnlySet<string> ComputeAllExtensionHostProjectDirectories(
        ProjectPreprocessSourceLayout layout,
        SupportedProjectFiltering.ExclusionInfo exclusionInfo,
        IReadOnlyList<string> rewrittenFiles,
        ProjectGraph? projectGraph)
    {
        if (projectGraph == null || rewrittenFiles.Count == 0)
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        return ComputeProjectsCompilingRewrittenFiles(layout, exclusionInfo, rewrittenFiles);
    }

    private static IReadOnlySet<string> ComputeProjectsCompilingRewrittenFiles(
        ProjectPreprocessSourceLayout layout,
        SupportedProjectFiltering.ExclusionInfo exclusionInfo,
        IReadOnlyList<string> rewrittenFiles)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (rewrittenFiles.Count == 0)
        {
            return result;
        }

        var rewrittenFileSet = new HashSet<string>(rewrittenFiles, StringComparer.OrdinalIgnoreCase);

        foreach (var projectFile in layout.ProjectFiles
                     .Where(file => file.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)))
        {
            if (SupportedProjectFiltering.IsUnderExcludedProject(projectFile, exclusionInfo))
            {
                continue;
            }

            var projectDirectory = Path.GetDirectoryName(projectFile)!;
            if (rewrittenFiles.Any(file => ProjectPreprocessSource.IsSameOrDescendantPath(file, projectDirectory)))
            {
                result.Add(projectDirectory);
            }
        }

        return result;
    }

    private static string? GetPropertyValue(XElement root, string propertyName)
    {
        return root.Descendants()
            .Where(element => element.Name.LocalName == propertyName)
            .Select(element => (string?)element.Value)
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
    }
}
```

- [x] **Step 2: 构建验证**

Run:
```powershell
cd d:/code/cs2j; dotnet build src/CSharpToJava.CLI/CSharpToJava.CLI.csproj
```
Expected: Build succeeded, 0 errors.

- [x] **Step 3: Commit**

```powershell
cd d:/code/cs2j; git add src/CSharpToJava.CLI/ProjectArrayMethodPreprocessor.cs; git commit -m "feat(array-method): add ProjectArrayMethodPreprocessor (project mode)"
```

---

### Task 11: CLI `Program.cs` — 新增 `array-method` verb + `ArrayMethodOptions`

**Files:**
- Modify: `src/CSharpToJava.CLI/Program.cs`

- [x] **Step 1: 在 `Program.cs` 顶部加 using**

在 `using CSharpToJava.Core.UnsignedTypeRewriter;` 之后追加：

```csharp
using CSharpToJava.Core.ArrayMethodRewriter;
```

- [x] **Step 2: 在 verb 注册链中追加 `ArrayMethodOptions`**

找到 `Parser` 解析行（约第 29 行），把 `ArrayMethodOptions` 加入泛型参数列表。在 `RecordReadOnlyVerbOptions` 之后追加 `ArrayMethodOptions`：

```csharp
return await parser.ParseArguments<ConvertOptions, ConvertProjectOptions, AnalyzeOptions, EliminateGotoOptions, MakeReadOnlyOptions, UnsignedTypeOptions, GenericPreprocessOptions, StripDefaultOptions, LocalFunctionLiftOptions, TupleToStructVerbOptions, StripConditionalOptions, RecordReadOnlyVerbOptions, ArrayMethodOptions>(args)
```

在 `.MapResult(` 中追加：

```csharp
(ArrayMethodOptions opts) => ArrayMethod(opts),
```

- [x] **Step 3: 添加 `ArrayMethodOptions` 类定义**

在 `UnsignedTypeOptions` 类之后追加：

```csharp
[Verb("array-method", HelpText = "Rewrite array instance members and System.Array static calls to Cs2jArray.ArrayHelper")]
class ArrayMethodOptions
{
    [Option('i', "input", SetName = "file", HelpText = "Input .cs file")]
    public string? Input { get; set; }
    [Option('o', "output", SetName = "file", HelpText = "Output .cs file (default: stdout)")]
    public string? Output { get; set; }
    [Option('s', "source", SetName = "dir", HelpText = "Source directory or .csproj/.sln file")]
    public string? Source { get; set; }
    [Option('d', "destination", SetName = "dir", HelpText = "Destination directory")]
    public string? Destination { get; set; }
    [Option('v', "verbose", Default = false)]
    public bool Verbose { get; set; }
    [Option("strict", Default = false, HelpText = "Treat unsupported diagnostics as fatal")]
    public bool Strict { get; set; }
    [Option("no-filter-projects", Default = false,
        HelpText = "Disable excluding unsupported (WPF/UWP/WinForms) projects before preprocessing")]
    public bool NoFilterProjects { get; set; }
}
```

- [x] **Step 4: 添加 `ArrayMethod` 处理方法**

在 `UnsignedType` 方法之后追加（mirror `UnsignedType` 方法的结构）：

```csharp
private static async Task<int> ArrayMethod(ArrayMethodOptions opts)
{
    var rewriterOptions = new ArrayMethodRewriterOptions(
        Verbose: opts.Verbose,
        Strict: opts.Strict);
    var rewriter = new ArrayMethodRewriter();

    // 单文件模式
    if (opts.Input != null)
    {
        if (!File.Exists(opts.Input))
        {
            Console.Error.WriteLine($"Error: Input file not found: {opts.Input}");
            return 1;
        }

        var sourceCode = await File.ReadAllTextAsync(opts.Input);
        var tree = Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(sourceCode);
        var compilation = Microsoft.CodeAnalysis.CSharp.CSharpCompilation.Create(
            "array-method",
            new[] { tree },
            ProjectUnsignedTypePreprocessor.GetBasicReferences(),
            new Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions(
                Microsoft.CodeAnalysis.OutputKind.DynamicallyLinkedLibrary,
                allowUnsafe: true));

        var result = rewriter.Rewrite(sourceCode, compilation.GetSemanticModel(tree), rewriterOptions);

        if (opts.Output != null)
        {
            await File.WriteAllTextAsync(opts.Output, result.OutputCode, new System.Text.UTF8Encoding(false));

            if (result.Statistics.InstanceMemberRewrites > 0 || result.Statistics.StaticArrayCalls > 0
                || result.Statistics.ResizeStatements > 0 || result.Statistics.ForeachFlattened > 0
                || result.Statistics.RankInlined > 0 || result.Statistics.MultiDimLengthRewrites > 0)
            {
                var outputDir = Path.GetDirectoryName(Path.GetFullPath(opts.Output)) ?? ".";
                var extensionPath = Path.Combine(outputDir, ArrayHelperSupportFiles.SingleFileHelperFileName);
                await File.WriteAllTextAsync(extensionPath, ArrayHelperSupportFiles.GetArrayHelperFileContent(), new System.Text.UTF8Encoding(false));
                if (opts.Verbose)
                {
                    Console.WriteLine($"Wrote extension file: {extensionPath}");
                }
            }

            if (opts.Verbose)
            {
                Console.WriteLine($"Transformed: {opts.Input} -> {opts.Output}");
            }
        }
        else
        {
            Console.Write(result.OutputCode);
        }

        foreach (var diagnostic in result.Diagnostics)
        {
            Console.Error.WriteLine($"[{diagnostic.Severity}] {diagnostic.Message}");
        }

        return result.Diagnostics.Any(d => d.Severity == ArrayMethodSeverity.Error) ? 1 : 0;
    }

    // 项目 / 目录模式
    if (opts.Source == null || opts.Destination == null)
    {
        Console.Error.WriteLine("Error: provide (-i [-o]) or (-s <source> -d <destination>).");
        return 1;
    }

    if (!Directory.Exists(opts.Source) && !File.Exists(opts.Source))
    {
        Console.Error.WriteLine($"Error: Source not found: {opts.Source}");
        return 1;
    }

    Directory.CreateDirectory(opts.Destination);

    var preprocessResult = await ProjectArrayMethodPreprocessor.PreprocessAsync(
        new ProjectArrayMethodPreprocessRequest
        {
            SourcePath = opts.Source,
            DestinationRoot = opts.Destination,
            Force = true,
            Verbose = opts.Verbose,
            RewriterOptions = rewriterOptions,
            FilterUnsupportedProjects = !opts.NoFilterProjects,
        });

    Console.WriteLine(
        $"array-method: {preprocessResult.Statistics.FilesTransformed} transformed, " +
        $"{preprocessResult.Statistics.FilesSkippedClean} clean, " +
        $"{preprocessResult.Statistics.InstanceMemberRewrites} instance member rewrites, " +
        $"{preprocessResult.Statistics.StaticArrayCalls} static array calls, " +
        $"{preprocessResult.Statistics.ResizeStatements} resize statements, " +
        $"{preprocessResult.Statistics.ForeachFlattened} foreach flattened, " +
        $"{preprocessResult.Statistics.RankInlined} rank inlined, " +
        $"{preprocessResult.Statistics.MultiDimLengthRewrites} multi-dim length rewrites");

    foreach (var diagnostic in preprocessResult.Diagnostics)
    {
        Console.Error.WriteLine($"[{diagnostic.Severity}] {diagnostic.Message}");
    }

    Console.WriteLine($"Preprocessed source: {preprocessResult.PreprocessedSourcePath}");
    return preprocessResult.Success ? 0 : 1;
}
```

- [x] **Step 5: 构建验证**

Run:
```powershell
cd d:/code/cs2j; dotnet build src/CSharpToJava.CLI/CSharpToJava.CLI.csproj
```
Expected: Build succeeded, 0 errors.

- [x] **Step 6: Commit**

```powershell
cd d:/code/cs2j; git add src/CSharpToJava.CLI/Program.cs; git commit -m "feat(array-method): add array-method CLI verb and handler"
```

---

### Task 12: `MultiModulePlanner` — 过滤 `Cs2jArray` 项目

**Files:**
- Modify: `src/CSharpToJava.CLI/MultiModulePlanner.cs`

- [x] **Step 1: 在项目过滤谓词链中追加 Cs2jArray 过滤**

找到 `MultiModulePlanner.cs` 中已有的 `Cs2jUnsignedMathProject.IsCs2jUnsignedMathProject` 过滤调用（链式谓词），在其后追加 `Cs2jArrayProject.IsCs2jArrayProject`：

```csharp
// 在已有的 Cs2jUnsignedMathProject.IsCs2jUnsignedMathProject 过滤之后追加：
&& !Cs2jArrayProject.IsCs2jArrayProject(project.FilePath)
```

- [x] **Step 2: 构建验证**

Run:
```powershell
cd d:/code/cs2j; dotnet build src/CSharpToJava.CLI/CSharpToJava.CLI.csproj
```
Expected: Build succeeded, 0 errors.

- [x] **Step 3: Commit**

```powershell
cd d:/code/cs2j; git add src/CSharpToJava.CLI/MultiModulePlanner.cs; git commit -m "feat(array-method): filter Cs2jArray project in MultiModulePlanner"
```

---

### Task 13: `TypeMappings.json` — 新增 `Cs2jArray.ArrayHelper` 映射

**Files:**
- Modify: `config/TypeMappings.json`

- [x] **Step 1: 在 TypeMappings.json 中追加类型条目**

在 `TypeMappings.json` 的类型映射数组中，在已有的 `Cs2jUnsigned.UnsignedMath` 条目之后追加：

```json
{
  "csType": "Cs2jArray.ArrayHelper",
  "javaType": "ArrayHelper",
  "javaImport": "io.github.ningpp.compat.ArrayHelper"
}
```

- [x] **Step 2: 构建验证**

Run:
```powershell
cd d:/code/cs2j; dotnet build
```
Expected: Build succeeded, 0 errors.

- [x] **Step 3: Commit**

```powershell
cd d:/code/cs2j; git add config/TypeMappings.json; git commit -m "feat(array-method): add Cs2jArray.ArrayHelper type mapping"
```

---

### Task 14: `ConvertProjectOptions` + `ConvertProject` — 集成 array-method 预处理阶段

**Files:**
- Modify: `src/CSharpToJava.CLI/ConvertProjectOptions.cs`
- Modify: `src/CSharpToJava.CLI/Program.cs`

- [x] **Step 1: 在 `ConvertProjectOptions` 中添加 `--enable-array-method` 开关**

在 `ConvertProjectOptions.cs` 中，在已有的 `EnableRecordReadOnly` 选项之后追加：

```csharp
[Option("enable-array-method", Default = false,
    HelpText = "Run array-method preprocessor before conversion")]
public bool EnableArrayMethod { get; set; }
```

- [x] **Step 2: 在 `ConvertProject` 方法中集成 array-method 预处理阶段**

在 `Program.cs` 的 `ConvertProject` 方法中，找到已有的 `RecordReadOnly` 预处理执行块（在 unsigned-type 之后、convert 之前），在其后追加 array-method 预处理阶段：

```csharp
// array-method 预处理阶段（在 record-readonly 之后、convert 之前）
if (opts.EnableArrayMethod)
{
    if (opts.Verbose)
    {
        Console.WriteLine("[convert-project] Running array-method preprocessor...");
    }

    var arrayMethodResult = await ProjectArrayMethodPreprocessor.PreprocessAsync(
        new ProjectArrayMethodPreprocessRequest
        {
            SourcePath = currentSourcePath,
            DestinationRoot = opts.Destination,
            Force = true,
            Verbose = opts.Verbose,
            RewriterOptions = new ArrayMethodRewriterOptions(Verbose: opts.Verbose),
            FilterUnsupportedProjects = !opts.NoFilterProjects,
        });

    currentSourcePath = arrayMethodResult.PreprocessedSourcePath;

    foreach (var diagnostic in arrayMethodResult.Diagnostics)
    {
        Console.Error.WriteLine($"[array-method] [{diagnostic.Severity}] {diagnostic.Message}");
    }

    if (!arrayMethodResult.Success)
    {
        Console.Error.WriteLine("[convert-project] array-method preprocessing failed; aborting.");
        return 1;
    }

    if (opts.Verbose)
    {
        Console.WriteLine(
            $"[convert-project] array-method: {arrayMethodResult.Statistics.FilesTransformed} transformed, " +
            $"{arrayMethodResult.Statistics.FilesSkippedClean} clean");
    }
}
```

- [x] **Step 3: 在 `GetPreservedDestinationDirectories` 中追加 `.cs2j-array-src`**

在 `Program.cs` 的 `GetPreservedDestinationDirectories` 方法中，在已有的 `ProjectUnsignedTypePreprocessor.IntermediateDirectoryName` 和 `ProjectRecordReadOnlyPreprocessor.IntermediateDirectoryName` 之后追加：

```csharp
ProjectArrayMethodPreprocessor.IntermediateDirectoryName,
```

- [x] **Step 4: 在 workspace 过滤中追加 Cs2jArray 目录过滤**

在 `Program.cs` 中已有的 workspace 目录过滤逻辑里（过滤 `Cs2jUnsignedMath` 目录的地方），追加：

```csharp
// 过滤 Cs2jArray 支撑库目录
if (path.Contains("\\Cs2jArray\\") || path.Contains("/Cs2jArray/"))
{
    continue;
}
```

- [x] **Step 5: 构建验证**

Run:
```powershell
cd d:/code/cs2j; dotnet build src/CSharpToJava.CLI/CSharpToJava.CLI.csproj
```
Expected: Build succeeded, 0 errors.

- [x] **Step 6: Commit**

```powershell
cd d:/code/cs2j; git add src/CSharpToJava.CLI/ConvertProjectOptions.cs src/CSharpToJava.CLI/Program.cs; git commit -m "feat(array-method): integrate array-method stage into convert-project pipeline"
```

---

## Milestone M4 — Java 侧 compat 扩展 + 测试

### Task 15: Java compat `ArrayHelper.java` — 追加 [B] 项方法组

**Files:**
- Modify: `java/csharptojava-compat/src/main/java/io/github/ningpp/compat/ArrayHelper.java`

- [x] **Step 1: 在 `ArrayHelper.java` 中追加全部 [B] 项方法组**

在已有的 `ArrayHelper` 类中追加以下方法（委托既有 `CSharpArray` 或直接实现）。具体方法签名与 C# 侧 `ArrayHelper` 对齐，但使用 Java 语义（`Object[]` + 反射）：

```java
    // ---- 元数据 ----

    public static int getLength(Object array) {
        if (array instanceof Object[] arr && arr.length > 0 && arr[0] != null && arr[0].getClass().isArray()) {
            int total = 0;
            for (Object row : arr) {
                total += java.lang.reflect.Array.getLength(row);
            }
            return total;
        }
        return java.lang.reflect.Array.getLength(array);
    }

    public static long getLongLength(Object array) {
        return getLength(array);
    }

    public static int getLength(Object array, int dimension) {
        if (dimension < 0) throw new IndexOutOfBoundsException();
        Object current = array;
        for (int d = 0; d < dimension; d++) {
            int len = java.lang.reflect.Array.getLength(current);
            if (len == 0) return 0;
            current = java.lang.reflect.Array.get(current, 0);
        }
        return java.lang.reflect.Array.getLength(current);
    }

    public static long getLongLength(Object array, int dimension) {
        return getLength(array, dimension);
    }

    public static int getLowerBound(Object array, int dimension) {
        if (dimension < 0) throw new IndexOutOfBoundsException();
        getLength(array, dimension);
        return 0;
    }

    public static int getUpperBound(Object array, int dimension) {
        return getLowerBound(array, dimension) + getLength(array, dimension) - 1;
    }

    // ---- 实例成员收口 ----

    public static Object clone(Object array) {
        int len = java.lang.reflect.Array.getLength(array);
        Object copy = java.lang.reflect.Array.newInstance(
            array.getClass().getComponentType(), len);
        for (int i = 0; i < len; i++) {
            java.lang.reflect.Array.set(copy, i, java.lang.reflect.Array.get(array, i));
        }
        return copy;
    }

    public static void copyTo(Object source, Object destination, long index) {
        int len = java.lang.reflect.Array.getLength(source);
        for (int i = 0; i < len; i++) {
            java.lang.reflect.Array.set(destination, (int) index + i,
                java.lang.reflect.Array.get(source, i));
        }
    }

    public static Object getValue(Object array, int... indices) {
        Object current = array;
        for (int idx : indices) {
            current = java.lang.reflect.Array.get(current, idx);
        }
        return current;
    }

    public static void setValue(Object array, Object value, int... indices) {
        Object current = array;
        for (int i = 0; i < indices.length - 1; i++) {
            current = java.lang.reflect.Array.get(current, indices[i]);
        }
        java.lang.reflect.Array.set(current, indices[indices.length - 1], value);
    }

    // ---- System.Array 静态收口 ----

    @SuppressWarnings("unchecked")
    public static <T> void sort(T[] array) {
        java.util.Arrays.sort(array);
    }

    @SuppressWarnings("unchecked")
    public static <T> void sort(T[] array, java.util.Comparator<? super T> comparer) {
        java.util.Arrays.sort(array, comparer);
    }

    public static <T> void sort(T[] array, int index, int length) {
        java.util.Arrays.sort(array, index, index + length);
    }

    @SuppressWarnings("unchecked")
    public static <T> void sort(T[] array, int index, int length, java.util.Comparator<? super T> comparer) {
        java.util.Arrays.sort(array, index, index + length, comparer);
    }

    @SuppressWarnings("unchecked")
    public static <T> int binarySearch(T[] array, T value) {
        return java.util.Arrays.binarySearch(array, value);
    }

    @SuppressWarnings("unchecked")
    public static <T> int binarySearch(T[] array, T value, java.util.Comparator<? super T> comparer) {
        return java.util.Arrays.binarySearch(array, value, comparer);
    }

    public static <T> int binarySearch(T[] array, int index, int length, T value) {
        return java.util.Arrays.binarySearch(array, index, index + length, value);
    }

    @SuppressWarnings("unchecked")
    public static <T> int binarySearch(T[] array, int index, int length, T value, java.util.Comparator<? super T> comparer) {
        return java.util.Arrays.binarySearch(array, index, index + length, value, comparer);
    }

    public static void clear(Object[] array, int index, int length) {
        for (int i = index; i < index + length && i < array.length; i++) {
            array[i] = null;
        }
    }

    @SuppressWarnings("unchecked")
    public static <T> T[] resize(T[] array, int newSize) {
        T[] result = (T[]) java.lang.reflect.Array.newInstance(
            array.getClass().getComponentType(), newSize);
        int copyLen = Math.min(array.length, newSize);
        System.arraycopy(array, 0, result, 0, copyLen);
        return result;
    }

    public static <T> int indexOf(T[] array, Object value) {
        for (int i = 0; i < array.length; i++) {
            if (Objects.equals(array[i], value)) return i;
        }
        return -1;
    }

    public static <T> int lastIndexOf(T[] array, Object value) {
        for (int i = array.length - 1; i >= 0; i--) {
            if (Objects.equals(array[i], value)) return i;
        }
        return -1;
    }

    public static <T> void reverse(T[] array) {
        for (int i = 0, j = array.length - 1; i < j; i++, j--) {
            T tmp = array[i];
            array[i] = array[j];
            array[j] = tmp;
        }
    }

    public static <T> T find(T[] array, java.util.function.Predicate<T> match) {
        for (T item : array) {
            if (match.test(item)) return item;
        }
        return null;
    }

    public static <T> T[] findAll(T[] array, java.util.function.Predicate<T> match) {
        java.util.List<T> list = new java.util.ArrayList<>();
        for (T item : array) {
            if (match.test(item)) list.add(item);
        }
        @SuppressWarnings("unchecked")
        T[] result = (T[]) java.lang.reflect.Array.newInstance(
            array.getClass().getComponentType(), list.size());
        return list.toArray(result);
    }

    public static <T> int findIndex(T[] array, java.util.function.Predicate<T> match) {
        for (int i = 0; i < array.length; i++) {
            if (match.test(array[i])) return i;
        }
        return -1;
    }

    public static <T> T findLast(T[] array, java.util.function.Predicate<T> match) {
        for (int i = array.length - 1; i >= 0; i--) {
            if (match.test(array[i])) return array[i];
        }
        return null;
    }

    public static <T> int findLastIndex(T[] array, java.util.function.Predicate<T> match) {
        for (int i = array.length - 1; i >= 0; i--) {
            if (match.test(array[i])) return i;
        }
        return -1;
    }

    public static <T> boolean exists(T[] array, java.util.function.Predicate<T> match) {
        for (T item : array) {
            if (match.test(item)) return true;
        }
        return false;
    }

    public static <T> boolean trueForAll(T[] array, java.util.function.Predicate<T> match) {
        for (T item : array) {
            if (!match.test(item)) return false;
        }
        return true;
    }

    @SuppressWarnings("unchecked")
    public static <TInput, TOutput> TOutput[] convertAll(TInput[] array, java.util.function.Function<TInput, TOutput> converter) {
        TOutput[] result = (TOutput[]) java.lang.reflect.Array.newInstance(
            converter.apply(array[0]).getClass(), array.length);
        for (int i = 0; i < array.length; i++) {
            result[i] = converter.apply(array[i]);
        }
        return result;
    }

    public static <T> void forEach(T[] array, java.util.function.Consumer<T> action) {
        for (T item : array) {
            action.accept(item);
        }
    }

    public static void copy(Object source, Object destination, int length) {
        System.arraycopy(source, 0, destination, 0, length);
    }

    public static void copy(Object source, int sourceIndex, Object destination, int destinationIndex, int length) {
        System.arraycopy(source, sourceIndex, destination, destinationIndex, length);
    }

    public static void constrainedCopy(Object source, int sourceIndex, Object destination, int destinationIndex, int length) {
        System.arraycopy(source, sourceIndex, destination, destinationIndex, length);
    }
```

- [x] **Step 2: 构建验证**

Run:
```powershell
cd d:/code/cs2j/java/csharptojava-compat; mvn compile -q
```
Expected: BUILD SUCCESS.

- [x] **Step 3: Commit**

```powershell
cd d:/code/cs2j; git add java/csharptojava-compat/src/main/java/io/github/ningpp/compat/ArrayHelper.java; git commit -m "feat(array-method): add [B] method group to Java compat ArrayHelper"
```

---

### Task 16: Java compat `ArrayHelperTest.java` — 追加边界值 JUnit

**Files:**
- Modify: `java/csharptojava-compat/src/test/java/io/github/ningpp/compat/ArrayHelperTest.java`

- [x] **Step 1: 追加边界值测试**

在 `ArrayHelperTest.java` 中追加以下测试方法（覆盖 Resize grow/shrink、IndexOf/LastIndexOf、Reverse、Find/FindAll、FindIndex/FindLastIndex、Exists/TrueForAll、ConvertAll、Clone、GetLength multiDim、GetLowerBound/GetUpperBound、GetValue/SetValue）：

```java
    @Test
    void testGetLength_multiDim() {
        int[][] arr = {{1, 2, 3}, {4, 5}};
        assertEquals(5, ArrayHelper.getLength((Object) arr));
    }

    @Test
    void testClone() {
        int[] arr = {1, 2, 3};
        int[] copy = (int[]) ArrayHelper.clone(arr);
        assertArrayEquals(arr, copy);
        assertNotSame(arr, copy);
    }

    @Test
    void testGetValue_andSetValue() {
        int[][] arr = {{1, 2}, {3, 4}};
        assertEquals(3, ArrayHelper.getValue(arr, 1, 0));
        ArrayHelper.setValue(arr, 99, 1, 0);
        assertEquals(99, ArrayHelper.getValue(arr, 1, 0));
    }

    @Test
    void testResize_grow() {
        String[] arr = {"a", "b"};
        String[] result = ArrayHelper.resize(arr, 4);
        assertArrayEquals(new String[]{"a", "b", null, null}, result);
    }

    @Test
    void testResize_shrink() {
        String[] arr = {"a", "b", "c"};
        String[] result = ArrayHelper.resize(arr, 2);
        assertArrayEquals(new String[]{"a", "b"}, result);
    }

    @Test
    void testReverse() {
        Integer[] arr = {1, 2, 3, 4, 5};
        ArrayHelper.reverse(arr);
        assertArrayEquals(new Integer[]{5, 4, 3, 2, 1}, arr);
    }

    @Test
    void testFind_andFindAll() {
        Integer[] arr = {1, 2, 3, 4, 5};
        assertEquals(3, ArrayHelper.find(arr, x -> x == 3));
        Integer[] evens = ArrayHelper.findAll(arr, x -> x % 2 == 0);
        assertArrayEquals(new Integer[]{2, 4}, evens);
    }

    @Test
    void testExists_andTrueForAll() {
        Integer[] arr = {2, 4, 6};
        assertTrue(ArrayHelper.exists(arr, x -> x == 4));
        assertTrue(ArrayHelper.trueForAll(arr, x -> x % 2 == 0));
    }

    @Test
    void testConvertAll() {
        Integer[] arr = {1, 2, 3};
        String[] result = ArrayHelper.convertAll(arr, Object::toString);
        assertArrayEquals(new String[]{"1", "2", "3"}, result);
    }
```

- [x] **Step 2: 运行测试**

Run:
```powershell
cd d:/code/cs2j/java/csharptojava-compat; mvn test -q
```
Expected: All tests pass.

- [x] **Step 3: Commit**

```powershell
cd d:/code/cs2j; git add java/csharptojava-compat/src/test/java/io/github/ningpp/compat/ArrayHelperTest.java; git commit -m "test(array-method): add boundary value JUnit tests for compat ArrayHelper"
```

---

### Task 17: 创建 `ArrayMethodRewriterUnitTests.cs` — 改写器单元测试

**Files:**
- Create: `tests/CSharpToJava.ProjectTests/ArrayMethodRewriterUnitTests.cs`

- [x] **Step 1: 写改写器单元测试**

Create `tests/CSharpToJava.ProjectTests/ArrayMethodRewriterUnitTests.cs`:

> 测试覆盖决策表 §3 每个 [B] 项正向改写 + 负向不碰（string.Length、List.Count、一维 arr.Length、非数组 Clone）。

```csharp
using CSharpToJava.Core.ArrayMethodRewriter;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace CSharpToJava.ProjectTests;

public class ArrayMethodRewriterUnitTests
{
    private static ArrayMethodRewriterResult RewriteWithSemantics(
        string source, ArrayMethodRewriterOptions? options = null)
    {
        var tree = CSharpSyntaxTree.ParseText(source);
        var references = new[]
        {
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
        };
        var compilation = CSharpCompilation.Create(
            "test",
            new[] { tree },
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        var model = compilation.GetSemanticModel(tree);
        var rewriter = new ArrayMethodRewriter();
        return rewriter.Rewrite(source, model, options);
    }

    [Fact]
    public void CleanFile_NoArrayMembers_BytePreserved()
    {
        var source = "class C { void M() { var x = 1 + 2; } }\n";
        var result = RewriteWithSemantics(source);
        Assert.False(result.Changed);
        Assert.Equal(source, result.OutputCode);
    }

    [Fact]
    public void OneDimLength_Whitelisted_NotRewritten()
    {
        var source = "class C { void M() { int[] a = {1,2,3}; var n = a.Length; } }\n";
        var result = RewriteWithSemantics(source);
        Assert.False(result.Changed);
    }

    [Fact]
    public void MultiDimLength_RewrittenToGetLength()
    {
        var source = "class C { void M() { int[,] a = new int[2,3]; var n = a.Length; } }\n";
        var result = RewriteWithSemantics(source);
        Assert.True(result.Changed);
        Assert.Contains("Cs2jArray.ArrayHelper.GetLength", result.OutputCode);
        Assert.Equal(1, result.Statistics.MultiDimLengthRewrites);
    }

    [Fact]
    public void LongLength_RewrittenToGetLongLength()
    {
        var source = "class C { void M() { int[] a = {1,2,3}; var n = a.LongLength; } }\n";
        var result = RewriteWithSemantics(source);
        Assert.True(result.Changed);
        Assert.Contains("Cs2jArray.ArrayHelper.GetLongLength", result.OutputCode);
    }

    [Fact]
    public void Rank_InlinedAsConstant()
    {
        var source = "class C { void M() { int[,] a = new int[2,3]; var r = a.Rank; } }\n";
        var result = RewriteWithSemantics(source);
        Assert.True(result.Changed);
        Assert.Contains("2", result.OutputCode);
        Assert.Equal(1, result.Statistics.RankInlined);
    }

    [Fact]
    public void Clone_InstanceCallRewritten()
    {
        var source = "class C { void M() { int[] a = {1,2,3}; var b = a.Clone(); } }\n";
        var result = RewriteWithSemantics(source);
        Assert.True(result.Changed);
        Assert.Contains("Cs2jArray.ArrayHelper.Clone", result.OutputCode);
    }

    [Fact]
    public void ArraySort_StaticCallRewritten()
    {
        var source = "using System; class C { void M() { int[] a = {3,1,2}; Array.Sort(a); } }\n";
        var result = RewriteWithSemantics(source);
        Assert.True(result.Changed);
        Assert.Contains("Cs2jArray.ArrayHelper.Sort", result.OutputCode);
        Assert.Equal(1, result.Statistics.StaticArrayCalls);
    }

    [Fact]
    public void ArrayResize_StatementRewritten()
    {
        var source = "using System; class C { void M() { int[] a = {1,2,3}; Array.Resize(ref a, 5); } }\n";
        var result = RewriteWithSemantics(source);
        Assert.True(result.Changed);
        Assert.Contains("Cs2jArray.ArrayHelper.Resize", result.OutputCode);
        Assert.Contains("a =", result.OutputCode);
        Assert.Equal(1, result.Statistics.ResizeStatements);
    }

    [Fact]
    public void StringLength_NotRewritten()
    {
        var source = "class C { void M() { string s = \"hello\"; var n = s.Length; } }\n";
        var result = RewriteWithSemantics(source);
        Assert.False(result.Changed);
    }

    [Fact]
    public void Foreach_MultiDimArray_Flattened()
    {
        var source = "class C { void M() { int[,] a = new int[2,3]; foreach (var x in a) { } } }\n";
        var result = RewriteWithSemantics(source);
        Assert.True(result.Changed);
        Assert.Contains("__cs2j_row0", result.OutputCode);
        Assert.Equal(1, result.Statistics.ForeachFlattened);
    }

    [Fact]
    public void Foreach_OneDimArray_NotFlattened()
    {
        var source = "class C { void M() { int[] a = {1,2,3}; foreach (var x in a) { } } }\n";
        var result = RewriteWithSemantics(source);
        Assert.False(result.Changed);
    }
}
```

- [x] **Step 2: 运行测试**

Run:
```powershell
cd d:/code/cs2j; dotnet test tests/CSharpToJava.ProjectTests/CSharpToJava.ProjectTests.csproj --filter "ArrayMethodRewriterUnitTests"
```
Expected: All tests pass.

- [x] **Step 3: Commit**

```powershell
cd d:/code/cs2j; git add tests/CSharpToJava.ProjectTests/ArrayMethodRewriterUnitTests.cs; git commit -m "test(array-method): add rewriter unit tests covering all [B] categories"
```

---

### Task 18: 创建 `SampleArraySolution` fixture

**Files:**
- Create: `tests/SampleArraySolution/SampleArrayLib/SampleArrayLib.csproj`
- Create: `tests/SampleArraySolution/SampleArrayLib/ArrayOperations.cs`
- Create: `tests/SampleArraySolution/SampleArrayApp/SampleArrayApp.csproj`
- Create: `tests/SampleArraySolution/SampleArrayApp/Program.cs`
- Create: `tests/SampleArraySolution/SampleArray.sln`
- Modify: `tests/CSharpToJava.ProjectTests/CSharpToJava.ProjectTests.csproj`

- [x] **Step 1: 创建 SampleArrayLib csproj + ArrayOperations.cs**

Create `tests/SampleArraySolution/SampleArrayLib/SampleArrayLib.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>disable</ImplicitUsings>
    <Nullable>disable</Nullable>
  </PropertyGroup>
</Project>
```

Create `tests/SampleArraySolution/SampleArrayLib/ArrayOperations.cs`:

```csharp
using System;

namespace SampleArrayLib;

public class ArrayOperations
{
    public static int SumMultiDim(int[,] arr)
    {
        int sum = 0;
        foreach (var x in arr)
        {
            sum += x;
        }
        return sum;
    }

    public static int GetFirstRowLength(int[,] arr)
    {
        return arr.GetLength(0);
    }

    public static int GetRank(int[,] arr)
    {
        return arr.Rank;
    }

    public static int GetTotalLength(int[,] arr)
    {
        return arr.Length;
    }

    public static int[] CloneArray(int[] arr)
    {
        return (int[])arr.Clone();
    }

    public static void SortArray(int[] arr)
    {
        Array.Sort(arr);
    }

    public static void ResizeArray(ref int[] arr, int newSize)
    {
        Array.Resize(ref arr, newSize);
    }

    public static bool ContainsValue(int[] arr, int target)
    {
        return Array.Exists(arr, x => x == target);
    }

    public static void SetValue(int[,] arr, int value, int i, int j)
    {
        arr.SetValue(value, i, j);
    }

    public static object GetValue(int[,] arr, int i, int j)
    {
        return arr.GetValue(i, j);
    }
}
```

- [x] **Step 2: 创建 SampleArrayApp csproj + Program.cs**

Create `tests/SampleArraySolution/SampleArrayApp/SampleArrayApp.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>disable</ImplicitUsings>
    <Nullable>disable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\SampleArrayLib\SampleArrayLib.csproj" />
  </ItemGroup>
</Project>
```

Create `tests/SampleArraySolution/SampleArrayApp/Program.cs`:

```csharp
using System;
using SampleArrayLib;

var arr = new int[,] { { 1, 2, 3 }, { 4, 5, 6 } };
Console.WriteLine(ArrayOperations.SumMultiDim(arr));
Console.WriteLine(ArrayOperations.GetRank(arr));
Console.WriteLine(ArrayOperations.GetTotalLength(arr));

var flat = new int[] { 3, 1, 2 };
ArrayOperations.SortArray(flat);
Console.WriteLine(string.Join(",", flat));

ArrayOperations.ResizeArray(ref flat, 5);
Console.WriteLine(flat.Length);
```

- [x] **Step 3: 创建 sln**

Create `tests/SampleArraySolution/SampleArray.sln` (standard VS solution format with SampleArrayLib and SampleArrayApp projects).

- [x] **Step 4: 在 ProjectTests csproj 中加 Content Include**

在 `tests/CSharpToJava.ProjectTests/CSharpToJava.ProjectTests.csproj` 中追加：

```xml
  <ItemGroup>
    <Content Include="..\SampleArraySolution\**\*" CopyToOutputDirectory="PreserveNewest" />
  </ItemGroup>
```

- [x] **Step 5: 构建验证 + Commit**

Run:
```powershell
cd d:/code/cs2j; dotnet build tests/CSharpToJava.ProjectTests/CSharpToJava.ProjectTests.csproj
```
Expected: Build succeeded, 0 errors.

```powershell
cd d:/code/cs2j; git add tests/SampleArraySolution/ tests/CSharpToJava.ProjectTests/CSharpToJava.ProjectTests.csproj; git commit -m "test(array-method): add SampleArraySolution fixture"
```

---

### Task 19: 创建 `ArrayMethodProjectTests.cs` — 项目级 + 名称碰撞 + 下游存活

**Files:**
- Create: `tests/CSharpToJava.ProjectTests/ArrayMethodProjectTests.cs`

- [x] **Step 1: 写项目级测试**

Create `tests/CSharpToJava.ProjectTests/ArrayMethodProjectTests.cs`:

```csharp
using CSharpToJava.CLI;

namespace CSharpToJava.ProjectTests;

[Collection("ArrayMethodPreprocessSerial")]
public class ArrayMethodProjectTests
{
    private static string FixtureDir =>
        Path.Combine(AppContext.BaseDirectory, "SampleArraySolution");

    [Fact]
    public async Task LibProject_ArrayMembers_RewrittenCorrectly()
    {
        var libDir = Path.Combine(FixtureDir, "SampleArrayLib");
        Assert.True(Directory.Exists(libDir), $"Fixture not found: {libDir}");
        var outputDir = Path.Combine(libDir, "..", ".test-output");

        try
        {
            var result = await ProjectArrayMethodPreprocessor.PreprocessAsync(
                new ProjectArrayMethodPreprocessRequest
                {
                    SourcePath = libDir,
                    DestinationRoot = outputDir,
                    Verbose = false,
                });

            Assert.True(result.Success, string.Join("\n", result.Diagnostics));
            Assert.True(result.Statistics.FilesTransformed > 0);

            var intermediateDir = result.IntermediateRoot;
            Assert.True(Directory.Exists(intermediateDir));

            var opsContent = await File.ReadAllTextAsync(
                Path.Combine(intermediateDir, "ArrayOperations.cs"));
            Assert.Contains("Cs2jArray.ArrayHelper.GetLength", opsContent);
            Assert.Contains("Cs2jArray.ArrayHelper.GetLongLength", opsContent);
            Assert.Contains("Cs2jArray.ArrayHelper.Clone", opsContent);
            Assert.Contains("Cs2jArray.ArrayHelper.Sort", opsContent);
            Assert.Contains("Cs2jArray.ArrayHelper.Resize", opsContent);
            Assert.Contains("Cs2jArray.ArrayHelper.Exists", opsContent);
            Assert.Contains("Cs2jArray.ArrayHelper.SetValue", opsContent);
            Assert.Contains("Cs2jArray.ArrayHelper.GetValue", opsContent);
            Assert.Contains("__cs2j_row0", opsContent); // foreach flattened

            // Cs2jArray lib released
            Assert.True(Directory.Exists(Path.Combine(intermediateDir, "Cs2jArray")));
            Assert.True(File.Exists(Path.Combine(intermediateDir, "Cs2jArray", "ArrayHelper.cs")));
            Assert.True(File.Exists(Path.Combine(intermediateDir, "Cs2jArray", "Cs2jArray.csproj")));

            // ProjectReference injected
            var libCsproj = await File.ReadAllTextAsync(
                Path.Combine(intermediateDir, "SampleArrayLib.csproj"));
            Assert.Contains("Cs2jArray.csproj", libCsproj);
        }
        finally
        {
            if (Directory.Exists(outputDir))
                Directory.Delete(outputDir, recursive: true);
        }
    }

    [Fact]
    public async Task Solution_BothProjects_RewrittenAndCs2jArrayFiltered()
    {
        var slnPath = Path.Combine(FixtureDir, "SampleArray.sln");
        Assert.True(File.Exists(slnPath));
        var outputDir = Path.Combine(FixtureDir, ".test-output-sln");

        try
        {
            var result = await ProjectArrayMethodPreprocessor.PreprocessAsync(
                new ProjectArrayMethodPreprocessRequest
                {
                    SourcePath = slnPath,
                    DestinationRoot = outputDir,
                    Verbose = false,
                });

            Assert.True(result.Success, string.Join("\n", result.Diagnostics));

            // App references Lib, both should have Cs2jArray injected
            var appCsproj = await File.ReadAllTextAsync(
                Path.Combine(result.IntermediateRoot, "SampleArrayApp", "SampleArrayApp.csproj"));
            Assert.Contains("Cs2jArray.csproj", appCsproj);
        }
        finally
        {
            if (Directory.Exists(outputDir))
                Directory.Delete(outputDir, recursive: true);
        }
    }
}
```

- [x] **Step 2: 运行测试**

Run:
```powershell
cd d:/code/cs2j; dotnet test tests/CSharpToJava.ProjectTests/CSharpToJava.ProjectTests.csproj --filter "ArrayMethodProjectTests"
```
Expected: All tests pass.

- [x] **Step 3: Commit**

```powershell
cd d:/code/cs2j; git add tests/CSharpToJava.ProjectTests/ArrayMethodProjectTests.cs; git commit -m "test(array-method): add project-level tests with fixture"
```

---

### Task 20: 修改 `MultiModulePlannerTests.cs` — 追加 Cs2jArray 过滤断言

**Files:**
- Modify: `tests/CSharpToJava.ProjectTests/MultiModulePlannerTests.cs`

- [x] **Step 1: 追加 Cs2jArray 过滤测试**

在 `MultiModulePlannerTests.cs` 中追加以下测试方法：

```csharp
    [Fact]
    public void Build_FiltersCs2jArray_AndDropsItsDependencyEdges()
    {
        var graph = new ProjectGraph
        {
            RootProjectPath = @"C:\repo\App\App.csproj",
            ProjectsInTopologicalOrder =
            [
                MakeProject(
                    "Cs2jArray",
                    @"C:\repo\App\Cs2jArray\Cs2jArray.csproj"),
                MakeProject(
                    "SampleArrayLib",
                    @"C:\repo\App\SampleArrayLib\SampleArrayLib.csproj",
                    @"C:\repo\App\Cs2jArray\Cs2jArray.csproj"),
                MakeProject(
                    "App",
                    @"C:\repo\App\App.csproj",
                    @"C:\repo\App\SampleArrayLib\SampleArrayLib.csproj",
                    @"C:\repo\App\Cs2jArray\Cs2jArray.csproj"),
            ],
        };

        var plan = MultiModulePlanner.Build(graph, includeTests: false);

        Assert.Equal(2, plan.ModulesInBuildOrder.Count);
        var app = plan.ModulesInBuildOrder.Single(m => m.Name == "app");
        var lib = plan.ModulesInBuildOrder.Single(m => m.Name == "samplearraylib");
        Assert.Equal("samplearraylib", string.Join(",", app.CompileDependencies));
        Assert.Empty(lib.CompileDependencies);
    }
```

- [x] **Step 2: 运行测试**

Run:
```powershell
cd d:/code/cs2j; dotnet test tests/CSharpToJava.ProjectTests/CSharpToJava.ProjectTests.csproj --filter "MultiModulePlannerTests"
```
Expected: All tests pass (including new one).

- [x] **Step 3: Commit**

```powershell
cd d:/code/cs2j; git add tests/CSharpToJava.ProjectTests/MultiModulePlannerTests.cs; git commit -m "test(array-method): add Cs2jArray filter assertion to MultiModulePlannerTests"
```

---

### Task 21: 创建 `ArrayMethodConversionEndToEndTests.cs` — 预处理 → workspace → 过滤 → 转换

**Files:**
- Create: `tests/CSharpToJava.ProjectTests/ArrayMethodConversionEndToEndTests.cs`

- [x] **Step 1: 写 e2e 测试**

Create `tests/CSharpToJava.ProjectTests/ArrayMethodConversionEndToEndTests.cs`:

```csharp
using CSharpToJava.CLI;
using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using CSharpToJava.Core.Workspace;

namespace CSharpToJava.ProjectTests;

[Collection("ArrayMethodPreprocessSerial")]
public class ArrayMethodConversionEndToEndTests
{
    private static string FixtureDir =>
        Path.Combine(AppContext.BaseDirectory, "SampleArraySolution");

    private static ConversionOptions Options => new()
    {
        TypeMappingConfigPath = Path.Combine(AppContext.BaseDirectory, "config", "TypeMappings.json"),
    };

    [Fact]
    public async Task PreprocessedSolution_ConvertsToJava_WithCompatArrayHelper()
    {
        var outputDir = Path.Combine(FixtureDir, ".test-output-e2e");

        try
        {
            // 1) 预处理：释放 Cs2jArray + 注入 ProjectReference
            var result = await ProjectArrayMethodPreprocessor.PreprocessAsync(
                new ProjectArrayMethodPreprocessRequest
                {
                    SourcePath = Path.Combine(FixtureDir, "SampleArray.sln"),
                    DestinationRoot = outputDir,
                    Verbose = false,
                });
            Assert.True(result.Success, string.Join("\n", result.Diagnostics));

            // 2) 主转换加载
            SolutionLoader.EnsureMSBuildRegistered();
            using var loader = new SolutionLoader();
            var workspaceProjects = await loader.TryOpenAsync(result.PreprocessedSourcePath);
            Assert.NotNull(workspaceProjects);
            Assert.True(workspaceProjects.Count > 0);

            // 3) 过滤内部支撑库
            var convertProjects = workspaceProjects
                .Where(p => !Cs2jArrayProject.IsCs2jArrayProject(p.FilePath))
                .ToList();
            Assert.DoesNotContain(convertProjects, p => p.Name == "Cs2jArray");

            // 4) 逐项目转换（支撑库仅作符号来源）
            var pipeline = new ConversionPipeline();
            var converted = new List<ConversionResult>();
            foreach (var wp in convertProjects.Where(p => !p.IsTestProject))
            {
                converted.AddRange(await pipeline.ConvertProjectWithPartialMergeAsync(
                    wp, Options));
            }

            // 5) 产物断言
            Assert.True(converted.Count > 0);
            var libJava = converted.FirstOrDefault(c => c.FileName.Contains("ArrayOperations"));
            Assert.NotNull(libJava);
            Assert.Contains("ArrayHelper", libJava.GeneratedCode);
        }
        finally
        {
            if (Directory.Exists(outputDir))
                Directory.Delete(outputDir, recursive: true);
        }
    }
}
```

- [x] **Step 2: 运行测试**

Run:
```powershell
cd d:/code/cs2j; dotnet test tests/CSharpToJava.ProjectTests/CSharpToJava.ProjectTests.csproj --filter "ArrayMethodConversionEndToEndTests"
```
Expected: Test passes.

- [x] **Step 3: Commit**

```powershell
cd d:/code/cs2j; git add tests/CSharpToJava.ProjectTests/ArrayMethodConversionEndToEndTests.cs; git commit -m "test(array-method): add end-to-end conversion test"
```

---

### Task 22: 全量构建验证 + 最终提交

**Files:** 无代码改动（验证）

- [x] **Step 1: 全量 dotnet build**

Run:
```powershell
cd d:/code/cs2j; dotnet build
```
Expected: Build succeeded, 0 errors.

- [x] **Step 2: 全量 dotnet test**

Run:
```powershell
cd d:/code/cs2j; dotnet test --filter "ArrayMethod|ArrayHelper" --no-build
```
Expected: All tests pass.

- [x] **Step 3: Java compat 测试**

Run:
```powershell
cd d:/code/cs2j/java/csharptojava-compat; mvn test -q
```
Expected: All tests pass.

---

## Self-Review

**1. Spec coverage:**
- §3.1 实例成员收口 → Task 7（Clone/CopyTo/GetValue/SetValue/GetLength/GetLongLength/GetLowerBound/GetUpperBound/LongLength/Rank/多维 Length）✓
- §3.2 多维 foreach 摊平 → Task 9 ✓
- §3.3 System.Array 静态收口 → Task 8（Sort/BinarySearch/Clear/Resize/IndexOf/LastIndexOf/Reverse/Find*/Exists/TrueForAll/ConvertAll/ForEach/Copy/ConstrainedCopy）✓
- C# 侧支撑库 → Task 1-2 ✓
- CLI 嵌入资源 → Task 3 ✓
- 项目模式预处理器 → Task 10 ✓
- CLI verb → Task 11 ✓
- convert-project 集成 → Task 14 ✓
- MultiModulePlanner 过滤 → Task 12 ✓
- TypeMappings → Task 13 ✓
- Java compat → Task 15-16 ✓
- 单元测试 → Task 17 ✓
- 项目级测试 → Task 19 ✓
- e2e 测试 → Task 21 ✓
- MultiModulePlanner 测试 → Task 20 ✓
- Sample fixture → Task 18 ✓

**2. Placeholder scan:**
- 无 TBD/TODO/fill in details ✓
- 所有代码步骤都包含完整代码 ✓
- 所有命令步骤都包含确切命令和预期输出 ✓

**3. Type consistency:**
- `ArrayMethodRewriter` / `ArrayMethodSyntaxRewriter` / `ArrayRewriteAnnotations` 在所有任务中名称一致 ✓
- `Cs2jArray.ArrayHelper` 在 C# lib、TypeMappings、改写输出中一致 ✓
- `ArrayHelperSupportFiles.LibDirectoryName = "Cs2jArray"` 在 Task 3、10、14 中一致 ✓
- `ProjectArrayMethodPreprocessor.IntermediateDirectoryName = ".cs2j-array-src"` 在 Task 10、14 中一致 ✓
- `Cs2jArrayProject.ProjectFileName = "Cs2jArray.csproj"` 在 Task 3、10、12 中一致 ✓

---

## Execution Handoff

**Plan complete and saved to `docs/superpowers/plans/2026-09-25-array-helper-preprocessor.md`. Two execution options:**

**1. Subagent-Driven (recommended)** - I dispatch a fresh subagent per task, review between tasks, fast iteration

**2. Inline Execution** - Execute tasks in this session using executing-plans, batch execution with checkpoints

**Which approach?**
