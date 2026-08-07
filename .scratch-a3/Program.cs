using CSharpToJava.Core.TypeScript;

Func<string, string> mapType = t => t switch
{
    "double[][]" => "number[][]",
    "double[]" => "number[]",
    "double" => "number",
    "boolean[]" => "boolean[]",
    "boolean" => "boolean",
    "int" => "number",
    _ => t,
};

var inputs = new[]
{
    "double[][] XX = new double[][] { { 1, 2 }, { -1, 2 }, { -1, -2 }, { 1, -2 } }, YY = new double[][] { { 0.07, 2.62 }, { 0.93, 3.12 }, { 1.93, 1.38 }, { 1.07, 0.88 } };",
    "double[][] XX = new double[][] [ { 1, 2 }, { -1, 2 }, { -1, -2 }, { 1, -2 } ], YY = new double[][] [ { 0.07, 2.62 }, { 0.93, 3.12 }, { 1.93, 1.38 }, { 1.07, 0.88 } ];",
};
foreach (var input in inputs)
{
    Console.WriteLine("INPUT : " + input);
    Console.WriteLine("OUTPUT: " + TsRawStatementNormalizer.Normalize(input, mapType));
    Console.WriteLine();
}
