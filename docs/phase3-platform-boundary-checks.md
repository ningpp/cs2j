# Phase 3 细化设计：平台边界与原生互操作检查

## 背景

Phase 2 已经把单文件和项目级主线收敛为显式 Pass，并在 `check` 阶段接入了第一版 `UnsupportedDomain` 检查。但当前实现仍有三个明显问题：

1. 规则混在一个 analyzer 中，UI 框架、平台边界、native interop 没有分层。
2. diagnostics 只有自然语言消息，没有稳定的分类标识，不利于批量筛查和 canary 回归。
3. 项目级如果多个边界规则同时命中同一文件，失败结果需要合并，而不是生成多条互相割裂的失败记录。

Phase 3 的第一批落地目标不是一次性覆盖所有平台能力，而是先把“规则分层、诊断分类、项目级阻断合并”这三个基础点做实。

## 本阶段目标

本批实现聚焦三类 `check` pass：

1. `UnsupportedDomainPass`
   负责明确拒绝当前 Java-only 管线完全不支持的 UI / XAML 族能力。
2. `PlatformBoundaryPass`
   负责识别依赖操作系统、桌面环境、注册表或平台能力探测的 API / attribute。
3. `NativeInteropPass`
   负责识别 P/Invoke、COM、`Marshal`、`NativeLibrary` 等原生互操作入口。

对应目标：

- 单文件与项目级主线都在 `emit` 前阻断这些输入。
- diagnostics 具备稳定的 `Code` 和 `Category`。
- 项目级同一文件命中多个边界规则时，只保留一条失败结果，但聚合所有 diagnostics。

## Pass 设计

单文件主线顺序调整为：

1. `SingleFileLinqDesugarPass`
2. `SingleFileCompilationCheckPass`
3. `SingleFileUnsupportedDomainCheckPass`
4. `SingleFilePlatformBoundaryCheckPass`
5. `SingleFileNativeInteropCheckPass`
6. `SingleFileContextNormalizationPass`
7. `SingleFileJavaEmitPass`

项目级主线顺序调整为：

1. `ProjectLinqDesugarPass`
2. `ProjectCompilationCheckPass`
3. `ProjectUnsupportedDomainCheckPass`
4. `ProjectPlatformBoundaryCheckPass`
5. `ProjectNativeInteropCheckPass`
6. `ProjectPartialTypeNormalizationPass`
7. `ProjectTypeEmitPass`
8. `ProjectCompatibilityEmitPass`
9. `ProjectCrossPackageImportEmitPass`
10. `ProjectPostGenerationRewriteEmitPass`

三个 `check` pass 都遵守同一条原则：

- 只做识别和阻断，不在本阶段尝试自动修复。
- 诊断直接挂到 `ConversionContext.Diagnostics`。
- 项目级额外写入 `ProjectPassState.DiagnosticsByFile`，并对同一文件合并失败结果。

## 首批规则范围

### 1. Unsupported Domain

首批继续覆盖这些命名空间前缀：

- `System.Windows.Forms`
- `System.Windows.Controls`
- `System.Windows`
- `Microsoft.Maui`
- `Windows.UI.Xaml`
- `System.Xaml`

诊断分类：

- `CS2J3001`
- category: `unsupported-domain`

### 2. Platform Boundary

首批覆盖三类信号：

1. 平台属性
   - `SupportedOSPlatform`
   - `UnsupportedOSPlatform`
   - `SupportedOSPlatformGuard`
   - `UnsupportedOSPlatformGuard`
2. 平台探测 API
   - `System.OperatingSystem.IsWindows/IsLinux/IsMacOS`
   - `System.Runtime.InteropServices.RuntimeInformation.*`
   - `System.Environment.OSVersion`
3. 平台设施类型
   - `Microsoft.Win32.Registry*`
   - `System.Diagnostics.EventLog`
   - `System.Management.*`
   - `System.ServiceProcess.ServiceController`
   - `System.DirectoryServices.*`
   - `System.IO.Ports.SerialPort`

诊断分类：

- `CS2J3101`: 平台属性
- `CS2J3102`: 平台能力 API / 类型
- category: `platform-boundary`

### 3. Native Interop

首批覆盖：

1. 原生互操作属性
   - `DllImport`
   - `LibraryImport`
   - `ComImport`
   - `MarshalAs`
   - `StructLayout`
   - `FieldOffset`
   - `UnmanagedCallersOnly`
   - `GeneratedComInterface`
2. 原生互操作 API / 类型
   - `System.Runtime.InteropServices.Marshal`
   - `System.Runtime.InteropServices.NativeLibrary`
   - `System.Runtime.InteropServices.GCHandle`
   - `System.Runtime.InteropServices.HandleRef`
   - `System.Runtime.InteropServices.SafeHandle`
3. `extern` 方法

诊断分类：

- `CS2J3201`: 原生互操作属性
- `CS2J3202`: 原生互操作 API / 类型
- `CS2J3203`: `extern` 方法
- category: `native-interop`

## 结果语义

### 单文件

- 任一边界检查命中 `Error` 后，设置 `BlockEmit = true`
- `JavaEmitPass` 不再生成空壳输出
- CLI 继续沿用现有“失败即返回非零，不写输出文件”的行为

### 项目级

- 任一边界检查命中文件时，把路径写入 `BlockedFilePaths`
- 同时把 diagnostics 归档到 `DiagnosticsByFile`
- 若同一文件命中多个 Pass，只维护一条 `Success = false` 的失败结果，并累加 diagnostics
- `ProjectTypeEmitPass` 对被阻断路径直接跳过 emit

## 验证点

这一批至少要覆盖以下回归：

1. 单文件 metrics 中出现 `PlatformBoundary` 和 `NativeInterop` pass。
2. `OperatingSystem.IsWindows()` 会在单文件路径被阻断。
3. `DllImport` 会在项目级路径被阻断。
4. 同一文件同时命中平台边界和原生互操作时，只生成一条失败结果，并包含两类 diagnostics。

## 非目标

本批不做以下工作：

- 不引入自动修复或桥接生成。
- 不对反射 / dynamic 做完整分类。
- 不改造 `TypeMappingRegistry` 或 compatibility packs。
- 不在本批定义完整的 target profile 模型。

这些内容继续留给 Phase 3 后续批次和 Phase 4 处理。