using CommandLine;

namespace CSharpToJava.CLI;

public class Program
{
    internal static async Task<int> MainImpl(string[] args)
    {
        using var parser = new Parser(settings => settings.AllowMultiInstance = true);
        return await parser.ParseArguments<StripConditionalOptions>(args)
            .MapResult(
                (StripConditionalOptions options) => StripConditionalCommand.RunAsync(options),
                _ => Task.FromResult(1));
    }
}
