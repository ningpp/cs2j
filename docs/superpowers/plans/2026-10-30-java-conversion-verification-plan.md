# Java 转换验证计划（csharp-sdk / AGL）

日期：2026-10-30
状态：进行中

## 目标

SDK 与 AGL 转成 Java 后：

1. `mvn clean test-compile -e` 无编译错误；
2. `mvn clean install -e` 单元测试全部通过。

Java 前提条件（已满足）：两棵 `--unified-preprocessed-src` 树
（`D:\cs\cs-sdk-e756c1f2\.cs2j-preprocessed-src`、`D:\cs\cs-agl-e756c1f2\.cs2j-preprocessed-src`）
`dotnet build -c Release` 0 error、`dotnet test -c Release` 0 失败
（SDK 17,724 通过；AGL 613/613）。

## 环境

- Maven 3.9.9 / JDK 28-ea；本地仓库 `D:\mvnrepo`（settings.xml 注释了 localRepository，
  由 `mvn help:evaluate` 确认为 d:\mvnrepo）。
- compat 源码：`D:\code\cs2j\java\csharptojava-compat`（1.0-SNAPSHOT，已安装）。
- AGL 依赖 SDK 的 `io.github.ningpp:dotnet-*:0.0.1-SNAPSHOT`，验证 AGL 前先安装 SDK：
  `cd <sdk-java-root>; mvn clean install -e`（或安装步骤的 skipTests 变体用于编译期验证）。

## 复现命令

```powershell
# 生成（用户命令 + fix 后的 CLI）
dotnet run -c Release --project src/CSharpToJava.CLI/CSharpToJava.CLI.csproj -- convert-project `
  -s D:\csharp-sdk\csharp-sdk-without-json.sln -d d:\cs\cs-sdk-<rev> `
  --no-verify-build --no-verify-tests --unified-preprocessed-src

dotnet run -c Release --project src/CSharpToJava.CLI/CSharpToJava.CLI.csproj -- convert-project `
  -s E:\agl-master\GraphLayout\agl.slnx -d d:\cs\cs-agl-<rev> `
  --no-verify-build --no-verify-tests --unified-preprocessed-src `
  --extra-deps io.github.ningpp:dotnet-xml:0.0.1-SNAPSHOT,io.github.ningpp:dotnet-text-regularexpressions:0.0.1-SNAPSHOT

# 验证
cd <java-root>; mvn clean test-compile -e    # 编译
cd <sdk-java-root>; mvn clean install -e     # SDK 测试 + 安装
cd <agl-java-root>; mvn clean install -e     # AGL 测试（先装 SDK）
```

## 工作树

- 基线（用户已有）：`D:\cs\cs-sdk-e756c1f2`、`D:\cs\cs-agl-e756c1f2`
- fix #1 验证树：`D:\cs\cs-sdk-fix1`、`D:\cs\cs-agl-fix1`
- 后续 fix 使用新的 `cs-sdk-fixN` / `cs-agl-fixN`，避免改动基线目录；最终验证回到基线目录重新生成。

## SDK javac 错误清单（fix #1 后 → 当前）

| 阶段 | 错误 | 状态 |
| --- | --- | --- |
| fix #1 后 | 82 行：78× `set(String)`（setValue 全局改写误伤）、2× `contains(Object)` 弱访问、2× 缺少返回值 | 已修（#2/#3） |
| fix #2+#3 后 | 1× `CSharpConcurrentDictionary.containsKey` 缺失 | 已修（#4 compat） |
| fix #4 后 | 1× `SetCompat.isSubsetOf(Set<?>)` 不接受 CSharpHashSet | 已修（#5 compat） |
| fix #5 后 | 13 个（regexp-tests）：9× IList 实现类 → CSharpICollection 赋值；4× `CSharpHashSet(char[])` | 修复中（#6 converter） |

## 修复记录

| # | 问题 | Commit | 验证 |
| --- | --- | --- | --- |
| 1 | StringBuilder.Length 写入被发成 `length(v)`（读映射当 setter） | `68f4ff0f` | StringBuilder 5/5；SDK 预处理器树 build/test 绿；AGL 预处理器树 build/test 绿；AGL `mvn clean test-compile -e` BUILD SUCCESS；SDK 仅剩上表后三类错误；单元套件 14 个既有失败不变 |
| 2 | 全局 `.setValue(`→`.set(` 误伤用户方法 | `f161f57f` | 4 个真实回归转绿；全量 4123/4142（10 个既有）；SDK 78 个 setValue 错误消失 |
| 3 | 集合 compat 桥裸 return / 私有 helper 擦除 | `4e691130` | 新增 4 测试；全量 4127/4146（同 10 个既有）；SDK 主源码仅余 1 个 compat 缺口 |
| 4 | `CSharpConcurrentDictionary.containsKey` 缺失 | `abcb02a3` | compat 测试 5/5；SDK 主模块全部编译，仅 regexp-tests 余 13 错 |
| 5 | `SetCompat` 首参 `Set<?>` 不接受 CSharpHashSet | `b3949a5a` | compat 测试 8/8；SDK regexp-tests 余 13 错（AttRegex 已过）；AGL 编译 8/8 |
| 6 | IList 实现类 → CSharpICollection 赋值未桥接；`CSharpHashSet(char[])` 构造实参未适配 | 待提交 | 新增 4 个 ConversionTests；SDK/AGL 重新生成后 `mvn clean test-compile -e` 均 **BUILD SUCCESS（0 javac 错误）**；预处理器树 build/test 均 0 错 0 失败 |