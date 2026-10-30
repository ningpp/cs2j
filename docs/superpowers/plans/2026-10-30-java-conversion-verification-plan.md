# Java 转换验证计划（csharp-sdk / AGL）

日期：2026-10-30
状态：已完成（2026-10-30）

## 目标

SDK 与 AGL 转成 Java 后：

1. `mvn clean test-compile -e` 无编译错误；
2. `mvn clean install -e` 单元测试全部通过。

Java 前提条件：`--unified-preprocessed-src` 树（`.cs2j-preprocessed-src`）中
`dotnet build -c Release` 0 error、`dotnet test -c Release` 0 失败。

## 最终结论

两套语料均用同一转换器版本（HEAD `53e5d6fb`，全部修复已提交）重新生成并验证：

| 语料 | 生成目录 | 预处理器树 dotnet build/test | `mvn clean test-compile -e` | `mvn clean install -e` |
| --- | --- | --- | --- | --- |
| SDK | `D:\cs\cs-sdk-fix6` | 0 error / 0 失败 | **BUILD SUCCESS** | **BUILD SUCCESS**，15 个测试模块 17,724 tests / 0 failures / 0 errors |
| AGL | `D:\cs\cs-agl-fix6` | 0 error / 0 失败 | **BUILD SUCCESS** | **BUILD SUCCESS**，613 tests / 0 failures / 0 errors |

AGL 的 `mvn clean install -e` 在 SDK 的 `mvn clean install -e` 安装
`io.github.ningpp:dotnet-*:0.0.1-SNAPSHOT` 到 `D:\mvnrepo` 之后执行。

日志：`D:\cs\_logs\sdk-fix6-*.log`、`D:\cs\_logs\agl-fix6-*.log`
（`*-testcompile-plain.log`、`*-install.log`、`*-dotnet-build.log`、`*-dotnet-test.log`）。

## 环境

- Maven 3.9.9 / JDK 28-ea；本地仓库 `D:\mvnrepo`。
- compat 源码：`D:\code\cs2j\java\csharptojava-compat`（1.0-SNAPSHOT，多次安装）。
- 生成命令（当前 CLI）：

```powershell
dotnet run -c Release --project src/CSharpToJava.CLI/CSharpToJava.CLI.csproj -- convert-project `
  -s D:\csharp-sdk\csharp-sdk-without-json.sln -d d:\cs\cs-sdk-fix6 `
  --no-verify-build --no-verify-tests --unified-preprocessed-src

dotnet run -c Release --project src/CSharpToJava.CLI/CSharpToJava.CLI.csproj -- convert-project `
  -s E:\agl-master\GraphLayout\agl.slnx -d d:\cs\cs-agl-fix6 `
  --no-verify-build --no-verify-tests --unified-preprocessed-src `
  --extra-deps io.github.ningpp:dotnet-xml:0.0.1-SNAPSHOT,io.github.ningpp:dotnet-text-regularexpressions:0.0.1-SNAPSHOT
```

## 修复记录（全部已提交）

| # | 问题 | Commit | 验证 |
| --- | --- | --- | --- |
| 1 | `StringBuilder.Length` 写入被发成 `length(v)`（读映射当 setter） | `68f4ff0f` | StringBuilder 5/5；SDK/AGL 预处理器树绿 |
| 2 | 全局 `.setValue(`→`.set(` 误伤用户 `SetValue` 方法 | `f161f57f` | 4 个真实回归转绿；全量 4123/4142（10 既有）；SDK 78 个 setValue 错误消失 |
| 3 | 集合 compat 桥裸 `return;` / 私有 helper 被擦成接口形状 | `4e691130` | 4 个新测试；全量 4127/4146（同 10 既有） |
| 4 | `CSharpConcurrentDictionary.containsKey` 缺失 | `abcb02a3` | compat 5/5；SDK 主模块全部编译 |
| 5 | `SetCompat` 首参 `Set<?>` 不接受 CSharpHashSet | `b3949a5a` | compat 8/8；SDK AttRegex 错误消失；AGL 编译 8/8 |
| 6 | IList 实现类 → CSharpICollection 赋值未桥接；`CSharpHashSet(char[])` 实参 | `ae84e802` | 4 个 ConversionTests；SDK/AGL test-compile BUILD SUCCESS |
| 7 | `CSharpICollection.contains` 默认抛 UOE | `36ecd280` | compat 5/5；3 个 iCollectionOfT_Contains 通过 |
| 8 | compat `Activator` 不识别 CSharpCollection→CSharpICollection 构造实参 | `e89f4673` | 4/4 compat；3 个 debuggerAttributeTests 通过（3 类 67/67） |
| 9 | LINQ Stream 被误判为已物化，强转 `(Iterable)` 运行期 CCE | `c2c17aa4` | LinqTests 12/12；javac/JVM 运行验证；修复 34 个 SDK 失败 |
| 10 | `XmlAttributeCollection.Remove` 的 `_2` 改名只在 per-project 注册表 | `69f588ee` | 跨工程 ConversionTests + 派生单元测试；CLI 探针一致 |
| 11 | 复合赋值 RHS 未加括号，Java 重新结合（Uri `end -= pchIndex - pnew`） | `a05767d1` | javac/JVM E2E（-18→18）；2 个 Uri 用例根因消除 |
| 12 | C# 枚举哨兵 `(DtdProcessing)(-1)` 生成 `values()[-1]`；`(int)e` 不识别 null 哨兵 | `53e5d6fb` | 6 个新 ConversionTests；compat EnumHelper 35/35；AGL 13 个 XML 用例转绿 |

## 关键错误清单（历史）

| 阶段 | 错误 | 修复 |
| --- | --- | --- |
| 初始 SDK test-compile | 82 行：78× `set(String)`、2× `contains(Object)` 弱访问、2× 缺少返回值 | #2/#3 |
| 其后 | `CSharpConcurrentDictionary.containsKey` 缺失 | #4 |
| 其后 | `SetCompat.isSubsetOf(Set<?>)` 不接受 CSharpHashSet | #5 |
| 其后 | 13 个 regexp-tests 错误：IList 实现类 → CSharpICollection、CSharpHashSet(char[]) | #6 |
| SDK install 测试 | regexp：stream CCE ×34、contains ×3、debugger ×3；uri：CCE ×4、AIOOBE ×2；xml-document：Remove ×2 | #9/#7/#8/#11/#10 |
| AGL install 测试 | XmlReaderSettings `values()[-1]` AIOOBE ×13 | #12 |

## 备注

- 转换器单元测试既有失败与本任务无关：`CSharpToJava.Tests` 10 个既有失败
  （ObjectsImport 陈旧断言、Iter3Uri、EnumerableAny、CompatCollectionConformance 陈旧断言、
  DictionaryLinqEntrySet、RegexEngineCompatBatch2），`CSharpToJava.ConversionTests` 5 个既有失败；
  均已对照 HEAD/临时 worktree 确认非本任务引入。
- 未手工修改生成树源码；所有生成树均由 `convert-project` 重新生成，修复只落在转换器与 compat 源码。
- 用户原始目录 `D:\cs\cs-sdk-e756c1f2` / `cs-agl-e756c1f2` 为旧转换器输出，未覆盖；
  最终验证使用当前 HEAD 生成的 `cs-sdk-fix6` / `cs-agl-fix6`。
