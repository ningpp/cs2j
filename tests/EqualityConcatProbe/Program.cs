// M0 探针（C47 设计 §4.1）：在 Roslyn（本仓实际引用版本，见 csproj）上
// 实测相等/拼接站点的公开 API 外形。只读探针，不属生产代码。
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

var source = """
using System;
using System.Linq.Expressions;
namespace ProbeNs;

public struct SPoint
{
    public int X;
    public static bool operator ==(SPoint a, SPoint b) => a.X == b.X;
    public static bool operator !=(SPoint a, SPoint b) => !(a == b);
    public override bool Equals(object? o) => o is SPoint p && p.X == X;
    public override int GetHashCode() => X;
}
public record class Rc(int X);
public record struct Rs(int X);
public class Sample
{
    public event Action? E;
    public string Fld = "";
    public string Prop { get; set; } = "";
    public string this[int i] { get => ""; set { } }
    public void Edge()
    {
        var t = (1, "a");
        var v22 = t == null;
        (int, string)? nt = null;
        var v22b = nt == null;
        var v22c = t == (1, (string)null);
        Prop += "x";
        this[0] += "x";
        Fld += "x";
        Action? d2 = null;
        var v23 = null == d2;
        var v24 = d2 != null;
        System.Delegate? dg = null;
        var v25 = dg == null;
        System.MulticastDelegate? md = null;
        var v26 = md == md;
    }
    public void Run(string a, string b, Action d, bool? x, int? i,
        SPoint p, Rc r1, Rc r2, Rs s1, Rs s2, dynamic z)
    {
        object o = a;
        var v01 = a == b;
        var v02 = a != b;
        var v03 = a == null;
        var v04 = (object)a == b;
        var v05 = o == "x";
        var v06 = a + "x";
        var v07 = "x" + 1;
        var v08 = null + "a";
        a += b;
        var v10 = d == d;
        var v11 = (d + d) == (d + d);
        var v12 = x == true;
        var v13 = i == i;
        var v14 = r1 == r2;
        var v15 = s1 == s2;
        var v16 = p == p;
        var v17 = (1, "a") == (1, "a");
        var v18 = E == null;
        var v19 = object.ReferenceEquals(a, b);
        Expression<Func<string, string, bool>> et = (qa, qb) => qa == qb;
        var v21 = z == a;
    }
}
""";

Console.WriteLine("=== C# compound eval order: Rcv().P += Val() ===");
OrderProbeTarget.Run();
Console.WriteLine();

var tree = CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Latest));
var references = new[]
{
    MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
    MetadataReference.CreateFromFile(typeof(Console).Assembly.Location),
    MetadataReference.CreateFromFile(Path.Combine(
        Path.GetDirectoryName(typeof(object).Assembly.Location)!, "System.Linq.Expressions.dll")),
};
var compilation = CSharpCompilation.Create("probe", new[] { tree }, references,
    new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));

var model = compilation.GetSemanticModel(tree);
var root = tree.GetRoot();

var errors = compilation.GetDiagnostics()
    .Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
Console.WriteLine($"=== compile errors: {errors.Count} ===");
foreach (var e in errors.Take(10))
    Console.WriteLine($"  {e.Id}: {e.GetMessage()}");

Console.WriteLine();
Console.WriteLine("=== BinaryOperatorKind public enum names ===");
foreach (var name in Enum.GetNames(typeof(BinaryOperatorKind)))
    Console.WriteLine("  " + name);

var nodes = root.DescendantNodes()
    .Where(n => n is BinaryExpressionSyntax or AssignmentExpressionSyntax)
    .ToList();

foreach (var node in nodes)
{
    Console.WriteLine();
    Console.WriteLine($"--- {node.GetType().Name}: {node.ToString().Trim()}");

    var inExprTree = node.Ancestors().OfType<LambdaExpressionSyntax>()
        .Any(l => IsExpressionTreeLambda(model, l));
    Console.WriteLine($"  ancestor expression-tree lambda: {inExprTree}");

    var symbolInfo = model.GetSymbolInfo(node);
    DescribeSymbol("GetSymbolInfo", symbolInfo.Symbol);
    if (symbolInfo.CandidateSymbols.Length > 0)
    {
        foreach (var c in symbolInfo.CandidateSymbols)
            DescribeSymbol("GetSymbolInfo(candidate)", c);
    }

    var ti = model.GetTypeInfo(node);
    Console.WriteLine($"  TypeInfo.Type={Format(ti.Type)} ConvertedType={Format(ti.ConvertedType)}");

    var operation = model.GetOperation(node);
    Console.WriteLine($"  GetOperation: {(operation == null ? "<null>" : operation.GetType().Name)}");
    switch (operation)
    {
        case IBinaryOperation b:
            Console.WriteLine($"    OperatorKind={b.OperatorKind} IsLifted={b.IsLifted} IsChecked={b.IsChecked} IsCompareText={b.IsCompareText}");
            DescribeSymbol("OperatorMethod", b.OperatorMethod);
            break;
        case ICompoundAssignmentOperation ca:
            Console.WriteLine($"    OperatorKind={ca.OperatorKind} IsLifted={ca.IsLifted} IsChecked={ca.IsChecked}");
            DescribeSymbol("OperatorMethod", ca.OperatorMethod);
            break;
        case ITupleBinaryOperation tb:
            Console.WriteLine($"    OperatorKind={tb.OperatorKind} Left={tb.LeftOperand?.GetType().Name} Right={tb.RightOperand?.GetType().Name}");
            break;
        case IDynamicInvocationOperation di:
            Console.WriteLine($"    IDynamicInvocationOperation Operation={di.Operation?.GetType().Name} args={di.Arguments.Length}");
            break;
        default:
            if (operation!.GetType().Name.Contains("Dynamic", StringComparison.Ordinal))
            {
                foreach (var property in operation.GetType().GetProperties())
                    Console.WriteLine($"    [dynamic prop] {property.Name}={property.GetValue(operation)}");
            }
            break;
    }
}

static void DescribeSymbol(string label, ISymbol? symbol)
{
    if (symbol == null)
    {
        Console.WriteLine($"  {label}: <null>");
        return;
    }

    var extra = symbol switch
    {
        IMethodSymbol m => $" MethodKind={m.MethodKind} IsImplicitlyDeclared={m.IsImplicitlyDeclared} IsRecord={m.ContainingType?.IsRecord} ContainingType={Format(m.ContainingType)}",
        _ => $" Kind={symbol.Kind} IsImplicitlyDeclared={symbol.IsImplicitlyDeclared} ContainingType={Format(symbol.ContainingType)}",
    };
    Console.WriteLine($"  {label}: {symbol.ToDisplayString()} |{extra}");
}

static string Format(ISymbol? symbol)
    => symbol == null ? "<null>" : symbol.ToDisplayString();

static bool IsExpressionTreeLambda(SemanticModel m, LambdaExpressionSyntax lambda)
{
    var convertedType = m.GetTypeInfo(lambda).ConvertedType;
    return convertedType != null
        && convertedType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
            .StartsWith("global::System.Linq.Expressions.Expression<", StringComparison.Ordinal);
}

file class OrderProbeTarget
{
    public string F;
    public string P
    {
        get { Console.WriteLine("  get"); return "old"; }
        set { Console.WriteLine("  set <- " + value); }
    }
    public string this[int i]
    {
        get { Console.WriteLine("  idxget(" + i + ")"); return "old"; }
        set { Console.WriteLine("  idxset(" + i + ") <- " + value); }
    }

    static OrderProbeTarget Rcv()
    {
        Console.WriteLine("  receiver");
        return new OrderProbeTarget();
    }

    static int Idx()
    {
        Console.WriteLine("  idxarg");
        return 1;
    }

    static string Val()
    {
        Console.WriteLine("  value");
        return "v";
    }

    public static void Run()
    {
        Console.WriteLine("property:");
        Rcv().P += Val();
        Console.WriteLine("field:");
        Rcv().F += Val();
        Console.WriteLine("indexer:");
        Rcv()[Idx()] += Val();
    }
}
