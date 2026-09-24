using CommandLine;

var parser = new Parser(settings => settings.AllowMultiInstance = true);
var result = parser.ParseArguments<Options>(args);
return result.MapResult(
    options =>
    {
        Console.WriteLine(string.Join("|", options.Define));
        return 0;
    },
    _ => 1);

internal sealed class Options
{
    [Option("define")]
    public IEnumerable<string> Define { get; set; } = Enumerable.Empty<string>();
}
