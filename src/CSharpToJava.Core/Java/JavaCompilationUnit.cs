using System.Text;
using System.Text.RegularExpressions;

namespace CSharpToJava.Core.Java;

/// <summary>
/// Java 编译单元（文件）
/// </summary>
public class JavaCompilationUnit : JavaSyntaxNode
{
    public string? Package { get; set; }
    /// <summary>
    /// 文件级头注释（如 C# 文件开头的 copyright / 文件级 &lt;summary&gt;），
    /// 来自 CompilationUnitSyntax 的 leading trivia，输出在 package 之前。
    /// </summary>
    public string? FileHeaderComment { get; set; }
    public List<JavaImport> Imports { get; } = new();
    public List<JavaTypeDeclaration> TypeDeclarations { get; } = new();

    public JavaCompilationUnit(string? package = null)
    {
        Package = package;
    }

    public override string ToString(string indentation)
    {
        var sb = new StringBuilder();

        // File header comment (copyright / file-level summary from C# leading trivia),
        // emitted before the package declaration.
        JavaCommentEmitter.AppendLeadingComment(sb, indentation, FileHeaderComment);
        if (!string.IsNullOrWhiteSpace(FileHeaderComment))
        {
            sb.AppendLine();
        }

        if (!string.IsNullOrEmpty(Package))
        {
            sb.AppendLine($"package {Package};");
            sb.AppendLine();
        }

        // Collect wildcard package names for standard Java packages to avoid emitting redundant explicit imports.
        // Only dedup java.*/javax.* packages; project-specific explicit imports are preserved to
        // disambiguate against java.util.* conflicts (e.g., project Set vs java.util.Set).
        var jdkWildcardPackages = new HashSet<string>(
            Imports.Where(i => i.IsWildcard && !i.IsStatic
                && (i.Name.StartsWith("java.") || i.Name.StartsWith("javax.")))
                .Select(i => i.Name));

        // BCL 运行时通配（dotnet.System.Collections(.Generic) 等）与 java.util.*
        // 同时存在且文件体用到同名类型（List/ArrayList/...）时，Java 规定两个按需
        // 导入中的同名类型对简单名构成歧义（javac: reference to List is
        // ambiguous）。BCL 映射面优先：撤掉 java.util.* 通配，改为按文件体内实际
        // 出现的 java.util 类型名补发单类型导入（单类型导入优先于按需导入）。
        var typesText = string.Join(Environment.NewLine,
            TypeDeclarations.Select(t => t.ToString(indentation)));
        var suppressJavaUtilWildcard = Imports.Any(i =>
            i.IsWildcard && !i.IsStatic
            && i.Name.StartsWith("dotnet.System.Collections", StringComparison.Ordinal));
        var explicitJavaUtilNames = new HashSet<string>();
        var explicitDotnetCollectionNames = new Dictionary<string, string>(StringComparer.Ordinal);
        if (suppressJavaUtilWildcard)
        {
            foreach (Match match in Regex.Matches(
                         typesText, @"(?<![\p{L}\p{N}_\.])[A-Z][A-Za-z0-9_]*"))
            {
                // dotnet 集合通配包提供的同名类型必须留给 BCL 面（由门/运行时
                // stub 提供具体实现），不能再单导入 java.util 的同名抽象类型。
                if (KnownJavaUtilTypes.Contains(match.Value)
                    && !DotnetCollectionSimpleNames.Contains(match.Value))
                {
                    explicitJavaUtilNames.Add(match.Value);
                }
            }

            // 已有显式导入或本文件声明了同名类型时不补发。
            explicitJavaUtilNames.RemoveWhere(name =>
                Imports.Any(i => !i.IsWildcard
                    && i.Name.EndsWith("." + name, StringComparison.Ordinal)));
            explicitJavaUtilNames.RemoveWhere(name =>
                Regex.IsMatch(typesText,
                    $@"(class|interface|enum|record)\s+{Regex.Escape(name)}\b"));

            // dotnet.System.Collections 与 dotnet.System.Collections.Generic 两个
            // 按需导入里的同名接口（IEnumerator/IEnumerable/...）对简单名构成歧义。
            // 带类型实参的引用（IEnumerator<T>）取泛型面；裸引用取非泛型面，用单
            // 类型导入压过按需导入。两种形式同文件并存时不猜测，留给 javac 暴露。
            var hasNonGenericCollectionsWildcard = Imports.Any(i =>
                i.IsWildcard && !i.IsStatic
                && i.Name == "dotnet.System.Collections");
            var hasGenericCollectionsWildcard = Imports.Any(i =>
                i.IsWildcard && !i.IsStatic
                && i.Name == "dotnet.System.Collections.Generic");
            if (hasNonGenericCollectionsWildcard && hasGenericCollectionsWildcard)
            {
                foreach (var name in GenericAndNonGenericCollectionInterfaceNames)
                {
                    var genericUse = Regex.IsMatch(typesText,
                        $@"(?<![\p{{L}}\p{{N}}_\.]){Regex.Escape(name)}\s*<");
                    var rawUse = Regex.IsMatch(typesText,
                        $@"(?<![\p{{L}}\p{{N}}_\.]){Regex.Escape(name)}(?![\p{{L}}\p{{N}}_<])");
                    var alreadyExplicit = Imports.Any(i => !i.IsWildcard
                        && i.Name.EndsWith("." + name, StringComparison.Ordinal));
                    if (alreadyExplicit || genericUse == rawUse)
                        continue;
                    explicitDotnetCollectionNames[name] = genericUse
                        ? "dotnet.System.Collections.Generic." + name
                        : "dotnet.System.Collections." + name;
                }
            }
        }

        // JDK wildcards that actually reach the output: the java.util wildcard is
        // suppressed below in favor of dotnet collection on-demand imports, so an
        // explicit java.util.SomeType import it would otherwise shadow must survive.
        var emittedJdkWildcardPackages = new HashSet<string>(jdkWildcardPackages, StringComparer.Ordinal);
        if (suppressJavaUtilWildcard)
            emittedJdkWildcardPackages.Remove("java.util");

        // 同文件顶级类型名：与「非 compat」单类型导入同简名时丢弃该导入（JLS
        // single-type-import 冲突；真实形态：partial 合并文件声明了
        // ParameterModifier 等，而按用途收集的导入带不可解析的
        // System.Reflection.* 残留 using）。compat 助手导入例外：项目可能同时
        // 声明并调用 compat 同名助手（Cs2jUnsigned 的 UnsignedMath 形态）。
        var ownTopLevelTypeNames = new HashSet<string>(StringComparer.Ordinal);
        CollectTopLevelTypeNames(TypeDeclarations, ownTopLevelTypeNames);

        // 同简名单类型导入去重：两个不同包的同名类型都被登记为单类型导入时
        // javac 直接拒编（javac: X 的 single-type-import 已定义具有相同简名的
        // 类型，真实链 dotnet-xml 106 处：项目自转换的 XmlException/XsltException/
        // XPathException 与 compat 映射导入并存）。保留与文件自身包一致的导入
        // （裸简名引用大概率指向项目内类型），否则保留最先登记者。
        var keptSimpleNameImport = new Dictionary<string, JavaImport>(StringComparer.Ordinal);
        var droppedSimpleNameImports = new HashSet<JavaImport>();
        int PackagePreference(string importName)
        {
            var pkg = importName[..importName.LastIndexOf('.')];
            // ① 本文件自身包（裸简名引用大概率指向项目内类型）；② compat 模块
            // （类路径恒存在，且映射语义以 compat 实现为准）；③ 其余（含
            // dotnet.System.* 虚拟 stub 包——对应模块未必参与构建，真实链
            // dotnet-uri 的 KeyNotFoundException 导入劫持）。
            if (string.Equals(pkg, Package, StringComparison.Ordinal)) return 2;
            if (pkg.StartsWith("io.github.ningpp.compat.", StringComparison.Ordinal)) return 1;
            return 0;
        }
        foreach (var import in Imports)
        {
            if (import.IsWildcard || import.IsStatic)
                continue;
            var simpleName = import.Name[(import.Name.LastIndexOf('.') + 1)..];
            if (!keptSimpleNameImport.TryGetValue(simpleName, out var kept))
            {
                keptSimpleNameImport[simpleName] = import;
                continue;
            }

            var keptPreference = PackagePreference(kept.Name);
            var currentPreference = PackagePreference(import.Name);
            if (currentPreference > keptPreference)
            {
                droppedSimpleNameImports.Add(kept);
                keptSimpleNameImport[simpleName] = import;
            }
            else
            {
                droppedSimpleNameImports.Add(import);
            }
        }

        foreach (var import in Imports)
        {
            if (suppressJavaUtilWildcard
                && import.IsWildcard && !import.IsStatic
                && import.Name == "java.util")
            {
                continue;
            }

            if (droppedSimpleNameImports.Contains(import))
            {
                continue;
            }

            if (!import.IsWildcard && !import.IsStatic)
            {
                var ownSimpleName = import.Name[(import.Name.LastIndexOf('.') + 1)..];
                if (ownTopLevelTypeNames.Contains(ownSimpleName)
                    && !import.Name.StartsWith("io.github.ningpp.compat.", StringComparison.Ordinal))
                {
                    continue;
                }
            }

            // Drop on-demand imports for dotnet.* BCL stub packages when the file
            // body contains no fully-qualified reference into that package. Modern
            // javac rejects wildcard imports of non-existent packages, and once all
            // types from the corresponding C# namespace have explicit compat
            // mappings (e.g. System.Collections.* → CSharpGenericIterable/...), the
            // wildcard is pure residue of the translated `using` directive.
            if (import.IsWildcard && !import.IsStatic
                && import.Name.StartsWith("dotnet.", StringComparison.Ordinal)
                && !typesText.Contains(import.Name + ".", StringComparison.Ordinal))
            {
                continue;
            }

            // Skip explicit (non-wildcard) JDK imports whose package is covered by an EMITTED JDK wildcard
            if (!import.IsWildcard && !import.IsStatic)
            {
                var lastDot = import.Name.LastIndexOf('.');
                if (lastDot > 0 && emittedJdkWildcardPackages.Contains(import.Name[..lastDot]))
                    continue;
            }
            sb.AppendLine(import.ToCodeString(indentation));
        }

        foreach (var name in explicitJavaUtilNames.OrderBy(n => n, StringComparer.Ordinal))
        {
            sb.AppendLine($"import java.util.{name};");
        }

        foreach (var fullName in explicitDotnetCollectionNames.Values
                     .OrderBy(n => n, StringComparer.Ordinal))
        {
            sb.AppendLine($"import {fullName};");
        }

        if (Imports.Count > 0)
        {
            sb.AppendLine();
        }

        sb.Append(typesText);

        return sb.ToString();
    }

    /// <summary>
    /// 常用 java.util 公共类型目录：当 java.util.* 通配被 dotnet 集合面压制时，
    /// 按文件体内出现的简单名补发单类型导入。
    /// </summary>
    private static void CollectTopLevelTypeNames(
        IEnumerable<JavaTypeDeclaration> declarations, HashSet<string> names)
    {
        foreach (var declaration in declarations)
        {
            names.Add(declaration.Name);
            if (declaration is JavaClassDeclaration { NestedTypes: { } nested })
                CollectTopLevelTypeNames(nested, names);
        }
    }

    private static readonly HashSet<string> KnownJavaUtilTypes = new(StringComparer.Ordinal)
    {
        "ArrayDeque", "ArrayList", "Arrays", "BitSet", "Calendar", "Collection",
        "Collections", "Comparator", "Currency", "Date", "Deque", "Dictionary",
        "DoubleSummaryStatistics", "EnumMap", "EnumSet", "EventListener",
        "EventObject", "Formattable", "Formatter", "GregorianCalendar",
        "HashMap", "HashSet", "Hashtable", "IdentityHashMap", "IntSummaryStatistics",
        "Iterator", "LinkedHashMap", "LinkedHashSet", "LinkedList", "List",
        "ListIterator", "LongSummaryStatistics", "Map", "NavigableMap",
        "NavigableSet", "Objects", "Optional", "OptionalDouble", "OptionalInt",
        "OptionalLong", "PriorityQueue", "Properties", "PropertyPermission",
        "Queue", "Random", "Scanner", "Set", "SimpleTimeZone", "SortedMap",
        "SortedSet", "Spliterator", "Stack", "StringJoiner", "StringTokenizer",
        "Timer", "TimerTask", "TimeZone", "TreeMap", "TreeSet", "UUID", "Vector",
        "WeakHashMap",
    };

    /// <summary>
    /// 简单名同时存在于 dotnet.System.Collections(.Generic) BCL 面与 java.util
    /// 的类型。dotnet 集合通配在作用域内时，这些名字必须解析到 BCL 面
    /// （List<T> 是可 new 的具体类型等），不补发 java.util 单类型导入。
    /// </summary>
    private static readonly HashSet<string> DotnetCollectionSimpleNames = new(StringComparer.Ordinal)
    {
        "ArrayList", "BitArray", "Collection", "Dictionary", "HashSet",
        "IComparer", "ICollection", "IDictionary", "IEnumerable",
        "IEnumerator", "IEqualityComparer", "IList", "LinkedList", "List",
        "Queue", "ReadOnlyCollection", "SortedList", "Stack",
    };

    /// <summary>
    /// 同时存在于泛型与非泛型 dotnet 集合命名空间的接口简单名。
    /// </summary>
    private static readonly string[] GenericAndNonGenericCollectionInterfaceNames =
    {
        "ICollection", "IComparer", "IDictionary", "IEnumerable",
        "IEnumerator", "IEqualityComparer", "IList",
    };
}

/// <summary>
/// Java 导入声明
/// </summary>
public class JavaImport
{
    public string Name { get; set; } = string.Empty;
    public bool IsStatic { get; set; }
    public bool IsWildcard { get; set; }

    public JavaImport(string name, bool isStatic = false, bool isWildcard = false)
    {
        Name = name;
        IsStatic = isStatic;
        IsWildcard = isWildcard;
    }

    public string ToCodeString(string indentation = "")
    {
        var staticPart = IsStatic ? "static " : "";
        var wildcardPart = IsWildcard ? ".*" : "";
        return $"{indentation}import {staticPart}{Name}{wildcardPart};";
    }

    public override string ToString()
    {
        return ToCodeString();
    }
}
