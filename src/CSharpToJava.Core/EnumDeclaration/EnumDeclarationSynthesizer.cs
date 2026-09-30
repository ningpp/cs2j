using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace CSharpToJava.Core.EnumDeclaration;

/// <summary>
/// 生成 enum-like sealed class 声明（M1 实测锁定形态，设计 D1 回退档）：
/// private readonly U 真值字段、私有 ctor、GetValue()、DEFAULT、静态只读成员实例、
/// FromValue、Equals 族、ToString 核心成员无条件；其余按 Analyzer 盘点按需生成（D6）。
/// 方法体只用「数组/foreach/if/字符串相等/Concat/StringBuilder/整数字面值与强转」子集。
/// </summary>
internal static class EnumDeclarationSynthesizer
{
    public static ClassDeclarationSyntax Synthesize(EnumDeclarationPlan plan)
    {
        var U = EnumDeclarationSyntaxFacts.GetTypeKeyword(plan.Underlying);
        var E = plan.EnumShortName;
        var flags = plan.IsFlags;
        var unsigned = EnumDeclarationSyntaxFacts.IsUnsigned(plan.Underlying);
        var hexWidth = EnumDeclarationSyntaxFacts.HexWidth(plan.Underlying);

        // ── D16 命名消解 ──
        var used = new HashSet<string>(
            plan.Members.Select(m => m.Name), StringComparer.Ordinal);
        string Take(string preferred)
        {
            var name = EnumDeclarationSyntaxFacts.UniqueMemberName(preferred, used);
            used.Add(name);
            return name;
        }

        var bf = Take("_value");
        var gv = Take("GetValue");
        var fv = Take("FromValue");
        var dn = Take("DEFAULT");
        var hex = Take("__Hex");
        var pt = Take("__ParseToken");
        var tpt = Take("__TryParseToken");
        var names = new EnumSynthesizedNames
        {
            BackingField = bf,
            GetValue = gv,
            FromValue = fv,
            DefaultInstance = dn,
            HexHelper = hex,
            ParseTokenHelper = pt,
            TryParseTokenHelper = tpt,
        };
        string Opt(EnumOptionalMembers kind, string preferred)
        {
            var name = Take(preferred);
            names.MethodNames[kind] = name;
            return name;
        }

        var sb = new StringBuilder();

        // ── 核心：字段/ctor/GetValue/DEFAULT ──
        sb.AppendLine($"    private readonly {U} {bf};");
        sb.AppendLine($"    private {E}({U} value) {{ {bf} = value; }}");
        sb.AppendLine($"    public {U} {gv}() {{ return {bf}; }}");
        sb.AppendLine($"    public static readonly {E} {dn} = new {E}(({U})0);");
        sb.AppendLine();

        // ── 成员实例（声明顺序、重复值各一份；字段级特性迁移，D5/D10） ──
        var memberDecls = plan.Declaration.Members;
        foreach (var member in plan.Members)
        {
            var syntax = memberDecls.FirstOrDefault(m => m.Identifier.Text == member.Name);
            var trivia = syntax?.GetLeadingTrivia().ToFullString();
            if (!string.IsNullOrWhiteSpace(trivia)) sb.Append(trivia);
            var attrs = syntax?.AttributeLists.Count > 0
                ? string.Concat(syntax!.AttributeLists.Select(a => a.ToFullString().TrimEnd())) + " "
                : string.Empty;
            var literal = EnumDeclarationSyntaxFacts.RenderIntegerLiteral(
                member.ConstantValue, plan.Underlying);
            sb.AppendLine($"    {attrs}public static readonly {E} {FieldName(member.Name)} = " +
                $"new {E}(({U}){literal});");
        }
        sb.AppendLine();
        sb.AppendLine($"    public static {E} {fv}({U} value) {{ return new {E}(value); }}");
        sb.AppendLine();

        // ── 核心：Equals 族（值相等，Dictionary 键语义） ──
        sb.AppendLine($"    public bool Equals({E}? other) " +
            $"{{ return !(other is null) && {bf} == other.{bf}; }}");
        sb.AppendLine($"    public override bool Equals(object? obj) " +
            $"{{ return obj is {E} other && Equals(other); }}");
        sb.AppendLine($"    public override int GetHashCode() {{ return {bf}.GetHashCode(); }}");
        sb.AppendLine();

        if (plan.Needs(EnumOptionalMembers.HasFlag))
        {
            var hasFlag = Opt(EnumOptionalMembers.HasFlag, "HasFlag");
            sb.AppendLine($"    public bool {hasFlag}({E} flag) " +
                $"{{ return ({bf} & flag.{bf}) == flag.{bf}; }}");
            sb.AppendLine();
        }

        // ── 核心：ToString（§2.4 G 格式；flags 组合名分解） ──
        AppendToString(sb, plan, U, E, bf, flags);

        // ── ToString(string?) + X 十六进制 ──
        if (plan.Needs(EnumOptionalMembers.ToStringFormat))
        {
            sb.AppendLine($"    public string ToString(string? format) {{");
            sb.AppendLine("        if (format == null || format.Length == 0 " +
                "|| format == \"G\" || format == \"F\") { return ToString(); }");
            sb.AppendLine("        if (format == \"D\") { return \"\" + " + bf + "; }");
            sb.AppendLine("        if (format[0] == 'X') {");
            sb.AppendLine($"            int width = {hexWidth.ToString(System.Globalization.CultureInfo.InvariantCulture)};");
            sb.AppendLine("            if (format.Length > 1) { width = int.Parse(format.Substring(1)); }");
            sb.AppendLine($"            return {hex}({bf}, width);");
            sb.AppendLine("        }");
            sb.AppendLine($"        throw new System.FormatException(string.Concat(\"Format string '\", format, \"' is not valid for enum {E}.\"));");
            sb.AppendLine("    }");
            sb.AppendLine();

            sb.AppendLine($"    private static string {hex}({U} v, int width) {{");
            sb.AppendLine("        string s = \"\";");
            // 先按 U 位宽 mask：有符号窄类型负值经 (ulong) 会符号扩展（sbyte -1 会被
            // do/while 吐成 16 个 F）；mask 后循环位数恰为 max(U 位宽, 显式宽度)。
            var mask = EnumDeclarationSyntaxFacts.HexMaskLiteral(plan.Underlying);
            sb.AppendLine(mask == null
                ? "        ulong x = (ulong)v;"
                : $"        ulong x = (ulong)v & {mask};");
            sb.AppendLine("        do {");
            sb.AppendLine("            int d = (int)(x & 15UL);");
            sb.AppendLine("            s = (char)(d < 10 ? '0' + d : 'A' + d - 10) + s;");
            sb.AppendLine("            x >>= 4;");
            sb.AppendLine("            width--;");
            sb.AppendLine("        } while (x != 0UL || width > 0);");
            sb.AppendLine("        return s;");
            sb.AppendLine("    }");
            sb.AppendLine();
        }

        if (plan.Needs(EnumOptionalMembers.Format))
        {
            var format = Opt(EnumOptionalMembers.Format, "Format");
            sb.AppendLine($"    public static string {format}({E} value, string? valueFormat) " +
                "{ return value.ToString(valueFormat); }");
            sb.AppendLine();
        }

        if (plan.Needs(EnumOptionalMembers.GetValues))
        {
            var getValues = Opt(EnumOptionalMembers.GetValues, "GetValues");
            var elements = string.Join(", ", plan.Members.Select(m => FieldName(m.Name)));
            sb.AppendLine($"    public static {E}[] {getValues}() {{ return new {E}[] {{ {elements} }}; }}");
            sb.AppendLine();
        }

        if (plan.Needs(EnumOptionalMembers.GetNames))
        {
            var getNames = Opt(EnumOptionalMembers.GetNames, "GetNames");
            var namesArray = string.Join(", ",
                plan.Members.Select(m => SymbolDisplay.FormatLiteral(m.Name, true)));
            sb.AppendLine($"    public static string[] {getNames}() " +
                $"{{ return new string[] {{ {namesArray} }}; }}");
            sb.AppendLine();
        }

        if (plan.Needs(EnumOptionalMembers.GetName))
        {
            var getName = Opt(EnumOptionalMembers.GetName, "GetName");
            sb.AppendLine($"    public static string? {getName}({U} value) {{");
            var emitted = new HashSet<ulong>();
            foreach (var m in plan.Members)
            {
                if (!emitted.Add(m.Bits)) continue;
                sb.AppendLine($"        if (value == {LiteralForCompare(m, plan)}) " +
                    $"{{ return {SymbolDisplay.FormatLiteral(m.Name, true)}; }}");
            }
            sb.AppendLine("        return null;");
            sb.AppendLine("    }");
            sb.AppendLine();
        }

        if (plan.Needs(EnumOptionalMembers.IsDefinedValue))
        {
            var isDefined = Opt(EnumOptionalMembers.IsDefinedValue, "IsDefined");
            sb.AppendLine($"    public static bool {isDefined}({E} value) " +
                $"{{ return value != null && {isDefined}(value.{gv}()); }}");
            sb.AppendLine($"    public static bool {isDefined}({U} value) {{");
            var emitted = new HashSet<ulong>();
            foreach (var m in plan.Members)
            {
                if (!emitted.Add(m.Bits)) continue;
                sb.AppendLine($"        if (value == {LiteralForCompare(m, plan)}) {{ return true; }}");
            }
            sb.AppendLine("        return false;");
            sb.AppendLine("    }");
            sb.AppendLine();
        }

        if (plan.Needs(EnumOptionalMembers.IsDefinedName))
        {
            var isDefined = names.MethodNames.TryGetValue(
                EnumOptionalMembers.IsDefinedValue, out var n) ? n : "IsDefined";
            var clauses = string.Join(" || ",
                plan.Members.Select(m => $"value == {SymbolDisplay.FormatLiteral(m.Name, true)}"));
            sb.AppendLine($"    public static bool {isDefined}(string value) {{ return {clauses}; }}");
            sb.AppendLine();
        }

        if (plan.Needs(EnumOptionalMembers.Parse))
        {
            var parse = Opt(EnumOptionalMembers.Parse, "Parse");
            sb.AppendLine($"    public static {E} {parse}(string value) " +
                $"{{ return {parse}(value, false); }}");
            sb.AppendLine($"    public static {E} {parse}(string value, bool ignoreCase) {{");
            sb.AppendLine("        if (value == null) { throw new System.ArgumentNullException(\"value\"); }");
            sb.AppendLine("        string t = value.Trim();");
            if (flags)
            {
                sb.AppendLine($"        {U} acc = ({U})0;");
                sb.AppendLine("        bool any = false;");
                sb.AppendLine("        foreach (string raw in t.Split(',')) {");
                sb.AppendLine($"            acc = unchecked(({U})(acc | {pt}(raw.Trim(), ignoreCase)));");
                sb.AppendLine("            any = true;");
                sb.AppendLine("        }");
                sb.AppendLine("        if (!any) { throw BadValue(t); }");
                sb.AppendLine($"        return {fv}(acc);");
            }
            else
            {
                sb.AppendLine($"        return {fv}({pt}(t, ignoreCase));");
            }
            sb.AppendLine("    }");
            sb.AppendLine();
            AppendParseToken(sb, plan, U, E, fv, pt, returnsBool: false, unsigned);
        }

        if (plan.Needs(EnumOptionalMembers.TryParse))
        {
            var tryParse = Opt(EnumOptionalMembers.TryParse, "TryParse");
            sb.AppendLine($"    public static bool {tryParse}(string? value, out {E} result) " +
                $"{{ return {tryParse}(value, false, out result); }}");
            sb.AppendLine($"    public static bool {tryParse}(string? value, bool ignoreCase, " +
                $"out {E} result) {{");
            sb.AppendLine("        if (value == null) { result = " + dn + "; return false; }");
            sb.AppendLine("        string t = value.Trim();");
            if (flags)
            {
                sb.AppendLine($"        {U} acc = ({U})0;");
                sb.AppendLine("        bool any = false;");
                sb.AppendLine("        foreach (string raw in t.Split(',')) {");
                sb.AppendLine($"            if (!{tpt}(raw.Trim(), ignoreCase, out {U} part)) " +
                    "{ result = " + dn + "; return false; }");
                sb.AppendLine($"            acc = unchecked(({U})(acc | part));");
                sb.AppendLine("            any = true;");
                sb.AppendLine("        }");
                sb.AppendLine("        if (!any) { result = " + dn + "; return false; }");
                sb.AppendLine($"        result = {fv}(acc);");
                sb.AppendLine("        return true;");
            }
            else
            {
                sb.AppendLine($"        if ({tpt}(t, ignoreCase, out {U} v)) " +
                    $"{{ result = {fv}(v); return true; }}");
                sb.AppendLine($"        result = {dn};");
                sb.AppendLine("        return false;");
            }
            sb.AppendLine("    }");
            sb.AppendLine();
            AppendParseToken(sb, plan, U, E, fv, tpt, returnsBool: true, unsigned);
        }

        if (plan.Needs(EnumOptionalMembers.ToObject))
        {
            var toObject = Opt(EnumOptionalMembers.ToObject, "ToObject");
            sb.AppendLine($"    public static {E} {toObject}({U} value) {{ return {fv}(value); }}");
            sb.AppendLine();
        }

        if (plan.Needs(EnumOptionalMembers.CompareTo))
        {
            var compareTo = Opt(EnumOptionalMembers.CompareTo, "CompareTo");
            sb.AppendLine($"    public int {compareTo}({E}? other) {{");
            sb.AppendLine("        if (other is null) { return 1; }");
            sb.AppendLine($"        if ({bf} < other.{bf}) {{ return -1; }}");
            sb.AppendLine($"        if ({bf} > other.{bf}) {{ return 1; }}");
            sb.AppendLine("        return 0;");
            sb.AppendLine("    }");
            sb.AppendLine();
        }

        if (flags && (plan.Needs(EnumOptionalMembers.Parse)
            || plan.Needs(EnumOptionalMembers.TryParse)))
        {
            sb.AppendLine("    private static System.Exception BadValue(string token) {");
            sb.AppendLine($"        return new System.ArgumentException(string.Concat(\"'\", token, \"' is not a valid {E}.\"));");
            sb.AppendLine("    }");
        }

        // ── 类型级特性（除 [Flags] 外迁移）与修饰符；整段一次解析 ──
        var typeAttrs = plan.Declaration.AttributeLists
            .Where(al => al.Attributes.All(a => !EnumDeclarationSyntaxFacts.IsFlagsAttributeSyntax(a)))
            .Select(al => al.ToFullString().Trim())
            .ToList();
        var access = plan.Declaration.Modifiers
            .Where(m => m.Kind() is SyntaxKind.PublicKeyword or SyntaxKind.InternalKeyword
                or SyntaxKind.PrivateKeyword or SyntaxKind.ProtectedKeyword
                or SyntaxKind.FileKeyword or SyntaxKind.PartialKeyword)
            .Select(m => m.Text)
            .ToList();
        var modifierText = access.Count > 0 ? string.Join(" ", access) + " " : string.Empty;

        var fullText = new StringBuilder();
        foreach (var a in typeAttrs) fullText.AppendLine(a);
        fullText.AppendLine($"{modifierText}sealed class {E}");
        fullText.AppendLine("{");
        fullText.Append(sb.ToString());
        fullText.AppendLine("}");

        var unit = ParseCompilationUnit(fullText.ToString());
        var parsed = unit.Members.OfType<ClassDeclarationSyntax>().First()
            .WithAdditionalAnnotations(EnumDeclarationSyntaxFacts.RewriteAnnotation);

        // 保留原 enum 的前导 trivia（含 XML doc/注释）。
        var leading = plan.Declaration.GetLeadingTrivia();
        if (leading.Count > 0) parsed = parsed.WithLeadingTrivia(leading);

        plan.Names = names;
        return parsed;
    }

    private static void AppendToString(
        StringBuilder sb, EnumDeclarationPlan plan, string U, string E, string bf, bool flags)
    {
        sb.AppendLine("    public override string ToString() {");
        if (flags)
        {
            var zeroName = plan.Members.FirstOrDefault(m => m.Bits == 0)?.Name;
            sb.AppendLine($"        if ({bf} == 0) " +
                $"{{ return {(zeroName != null ? SymbolDisplay.FormatLiteral(zeroName, true) : "\"0\"")}; }}");
            sb.AppendLine("        ulong rest = (ulong)" + bf + ";");
            sb.AppendLine("        System.Text.StringBuilder sb = new System.Text.StringBuilder();");
            // .NET flags 名按位值降序分解（声明顺序通常即数值序，按数值降序与运行时一致）。
            foreach (var m in plan.Members.Where(m => m.Bits != 0)
                         .GroupBy(m => m.Bits).Select(g => g.First())
                         .OrderByDescending(m => m.Bits))
            {
                var bits = m.Bits.ToString(System.Globalization.CultureInfo.InvariantCulture);
                var nameLit = SymbolDisplay.FormatLiteral(m.Name, true);
                sb.AppendLine($"        if ((rest & {bits}UL) == {bits}UL) " +
                    $"{{ if (sb.Length > 0) {{ sb.Append(\", \"); }} sb.Append({nameLit}); " +
                    "rest &= ~" + bits + "UL; }");
            }
            sb.AppendLine("        if (rest != 0UL || sb.Length == 0) { return \"\" + " + bf + "; }");
            sb.AppendLine("        return sb.ToString();");
        }
        else
        {
            var emitted = new HashSet<ulong>();
            foreach (var m in plan.Members)
            {
                if (!emitted.Add(m.Bits)) continue;
                sb.AppendLine($"        if ({bf} == {LiteralForCompare(m, plan)}) " +
                    $"{{ return {SymbolDisplay.FormatLiteral(m.Name, true)}; }}");
            }
            sb.AppendLine("        return \"\" + " + bf + ";");
        }
        sb.AppendLine("    }");
        sb.AppendLine();
    }

    private static void AppendParseToken(
        StringBuilder sb, EnumDeclarationPlan plan, string U, string E,
        string fv, string methodName, bool returnsBool, bool unsigned)
    {
        var zero = $"({U})0";

        if (returnsBool)
        {
            sb.AppendLine($"    private static bool {methodName}(string token, bool ignoreCase, " +
                $"out {U} value) {{");
            sb.AppendLine("        if (ignoreCase) {");
            foreach (var m in plan.Members)
            {
                sb.AppendLine($"            if (token.Equals({SymbolDisplay.FormatLiteral(m.Name, true)}, " +
                    "System.StringComparison.OrdinalIgnoreCase)) " +
                    $"{{ value = {LiteralForCompare(m, plan)}; return true; }}");
            }
            sb.AppendLine("        } else {");
            foreach (var m in plan.Members)
            {
                sb.AppendLine($"            if (token == {SymbolDisplay.FormatLiteral(m.Name, true)}) " +
                    $"{{ value = {LiteralForCompare(m, plan)}; return true; }}");
            }
            sb.AppendLine("        }");
            AppendNumericParse(sb, U, unsigned,
                "value = unchecked(({0}){1}); return true;");
            sb.AppendLine($"        value = {zero};");
            sb.AppendLine("        return false;");
            sb.AppendLine("    }");
        }
        else
        {
            sb.AppendLine($"    private static {U} {methodName}(string token, bool ignoreCase) {{");
            sb.AppendLine("        if (ignoreCase) {");
            foreach (var m in plan.Members)
            {
                sb.AppendLine($"            if (token.Equals({SymbolDisplay.FormatLiteral(m.Name, true)}, " +
                    "System.StringComparison.OrdinalIgnoreCase)) " +
                    $"{{ return {LiteralForCompare(m, plan)}; }}");
            }
            sb.AppendLine("        } else {");
            foreach (var m in plan.Members)
            {
                sb.AppendLine($"            if (token == {SymbolDisplay.FormatLiteral(m.Name, true)}) " +
                    $"{{ return {LiteralForCompare(m, plan)}; }}");
            }
            sb.AppendLine("        }");
            AppendNumericParse(sb, U, unsigned,
                "return unchecked(({0}){1});");
            sb.AppendLine($"        throw new System.ArgumentException(string.Concat(\"'\", token, \"' is not a valid {E}.\"));");
            sb.AppendLine("    }");
        }
        sb.AppendLine();
    }

    /// <summary>
    /// 数字 token 解析（设计 §2.4）：无符号 U 先走 long.TryParse（覆盖 "-1" 等负数），
    /// 失败再走 ulong.TryParse（覆盖 &gt;long.MaxValue 的 ulong 极值），统一 unchecked((U)) 回绕
    /// （byte 枚举 Parse("-1") → 255）；有符号 U 只走 long.TryParse。
    /// </summary>
    private static void AppendNumericParse(
        StringBuilder sb, string U, bool unsigned, string onSuccess)
    {
        string Success(string var) => string.Format(
            System.Globalization.CultureInfo.InvariantCulture, onSuccess, U, var);

        if (unsigned)
        {
            // Inline out-variable declarations (no separate pre-declaration): the
            // predeclared `long n; if (long.TryParse(token, out n))` shape is not
            // holder-lowered by the Java converter's if-condition path, while the
            // inline `out long n` shape is.
            sb.AppendLine($"        if (long.TryParse(token, out long n)) {{ {Success("n")} }}");
            sb.AppendLine($"        if (ulong.TryParse(token, out ulong un)) {{ {Success("un")} }}");
        }
        else
        {
            sb.AppendLine($"        if (long.TryParse(token, out long n)) {{ {Success("n")} }}");
        }
    }

    /// <summary>与 U 字段比较用的字面量（同 RenderIntegerLiteral，零值与负字面量均安全）。</summary>
    private static string LiteralForCompare(EnumMemberInfo member, EnumDeclarationPlan plan)
        => EnumDeclarationSyntaxFacts.RenderIntegerLiteral(member.ConstantValue, plan.Underlying);

    /// <summary>成员名可能是 C# 保留字（如 @class），生成字段名带 @ 前缀。</summary>
    private static string FieldName(string name)
        => SyntaxFacts.IsReservedKeyword(SyntaxFacts.GetKeywordKind(name)) ? "@" + name : name;
}
