# 错误18: Process API 未正确转换 (Process API Not Converted)

## 受影响文件
- `SteinerCdt.java:[328-341]` — ProcessStartInfo 和 Process 相关属性/方法

## 错误信息
```
找不到符号
  符号: 方法 setCreateNoWindow(boolean)
  符号: 方法 setUseShellExecute(boolean)
  符号: 方法 setFileName(String)
  符号: 变量 ProcessWindowStyle
  符号: 方法 setArguments(String)
  符号: 方法 Start(Object)
  符号: 方法 WaitForExit()
  符号: 方法 ExitCode()
```

## 原始 C# 代码
```csharp
// SteinerCdt.cs
using System.Diagnostics;

void RunExternalProcess(string exePath, string arguments) {
    var startInfo = new ProcessStartInfo {
        CreateNoWindow = true,
        UseShellExecute = false,
        FileName = exePath,
        WindowStyle = ProcessWindowStyle.Hidden,
        Arguments = arguments
    };

    var process = Process.Start(startInfo);
    process.WaitForExit();
    var exitCode = process.ExitCode;
}
```

C# 的 `System.Diagnostics.Process` 用于启动和管理外部进程：
- `ProcessStartInfo`: 进程启动参数配置
- `Process.Start()`: 启动进程
- `WaitForExit()`: 等待进程结束
- `ExitCode`: 获取退出码

## 生成的 Java 代码
```java
// SteinerCdt.java
var startInfo = new Object();  // ← 对象初始化器转为 Object，类型丢失
startInfo.setCreateNoWindow(true);       // ← Object 没有此方法
startInfo.setUseShellExecute(false);     // ← Object 没有此方法
startInfo.setFileName(exePath);          // ← Object 没有此方法
startInfo.setWindowStyle(ProcessWindowStyle.Hidden); // ← 找不到 ProcessWindowStyle
startInfo.setArguments(arguments);       // ← Object 没有此方法

var process = Process.Start(startInfo);  // ← Process 类不存在
process.WaitForExit();                   // ← 方法不存在
var exitCode = process.ExitCode();       // ← 方法不存在
```

## 错误原因分析

### 根本原因：两个问题叠加

#### 1. Object 初始化器类型丢失（同错误08）
C# 的对象初始化器语法 `new ProcessStartInfo { ... }` 被转换为 `new Object()`，丢失了实际类型。所有属性赋值都变成对 `Object` 调用 setter。

#### 2. Process/ProcessStartInfo 无类型映射
转换器没有为 `System.Diagnostics.Process` 和 `System.Diagnostics.ProcessStartInfo` 配置对应的 Java 类型映射。Java 中对应的是 `ProcessBuilder`，但 API 完全不同。

### 正确的 Java 代码
```java
ProcessBuilder processBuilder = new ProcessBuilder(exePath, arguments);
processBuilder.redirectErrorStream(true);
// ProcessBuilder 没有直接对应 CreateNoWindow/UseShellExecute 的属性
// 需要根据具体需求设置

Process process = processBuilder.start();
int exitCode = process.waitFor();
```

### 转换器修复建议
1. 修复对象初始化器转换（错误08），确保保留正确的类型
2. 添加 `System.Diagnostics.Process` → `java.lang.ProcessBuilder` 的类型映射
3. 添加方法映射：`Start()` → `start()`，`WaitForExit()` → `waitFor()`，`ExitCode` → `exitValue()`
4. 注意 API 差异：ProcessBuilder 使用 builder 模式配置参数，与 ProcessStartInfo 属性模式不同
