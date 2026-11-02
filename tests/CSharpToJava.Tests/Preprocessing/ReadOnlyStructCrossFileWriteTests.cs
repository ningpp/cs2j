using CSharpToJava.CLI.Preprocessing.Adapters;
using Xunit;

namespace CSharpToJava.Tests.Preprocessing;

/// <summary>
/// Regression for the cross-file write pass: an L4-converted struct whose fields
/// became underscore-prefixed get-only properties (internal state) must not be
/// mistaken for an L3 data container. Treating it as L3 registers an empty
/// writable-member surface, so external writes emitted by other passes
/// (<c>tmp._field = value</c>, Utf8JsonReader.CurrentState) survive as CS0200.
/// </summary>
public class ReadOnlyStructCrossFileWriteTests
{
    private const string StateSource = """
        namespace N
        {
            public readonly struct State
            {
                internal long _lineNumber { get; }

                internal State(long lineNumber)
                {
                    _lineNumber = lineNumber;
                }

                internal State With_lineNumber(long _lineNumber) => new State(_lineNumber);
            }
        }
        """;

    private const string UserSource = """
        namespace N
        {
            internal static class User
            {
                internal static State Build(long value)
                {
                    var state = new State(0);
                    state._lineNumber = value;
                    return state;
                }
            }
        }
        """;

    [Fact]
    public async Task InternalFieldOnlyStruct_ExternalWrite_RewrittenToWithMethod()
    {
        var root = Path.Combine(Path.GetTempPath(), "cs2j-readonly-" + Guid.NewGuid().ToString("N"));
        var sourceRoot = Path.Combine(root, "src");
        var intermediateRoot = Path.Combine(root, "out");
        var stateRelative = Path.Combine("N", "State.cs");
        var userRelative = Path.Combine("N", "User.cs");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(intermediateRoot, stateRelative))!);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(intermediateRoot, userRelative))!);
        File.WriteAllText(Path.Combine(intermediateRoot, stateRelative), StateSource);
        File.WriteAllText(Path.Combine(intermediateRoot, userRelative), UserSource);
        var sourceFiles = new List<string>
        {
            Path.Combine(sourceRoot, stateRelative),
            Path.Combine(sourceRoot, userRelative),
        };

        try
        {
            await ReadOnlyStructPassEngine.UpdateCrossFileFieldReads(intermediateRoot, sourceRoot, sourceFiles);

            var rewritten = File.ReadAllText(Path.Combine(intermediateRoot, userRelative));
            Assert.Contains("state = state.With_lineNumber(value)", rewritten);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
