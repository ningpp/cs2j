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
        var rewritten = await RunCrossFileRewriteAsync(StateSource, UserSource);
        Assert.Contains("state = state.With_lineNumber(value)", rewritten);
    }

    // Tuple/deconstruction write: AssignmentRewriter's statement-level handler
    // only understood single member-access assignments, so the raw
    // (a.X, a.Y) = (v1, v2) statement survived external to an L4 struct and
    // produced CS0200 on the now get-only properties.
    private const string L4StructSource = """
        namespace N
        {
            public readonly struct Frame
            {
                internal int _index { get; }
                internal object? _state { get; }

                internal Frame(int index, object? state)
                {
                    _index = index;
                    _state = state;
                }

                internal Frame With_index(int index) => new Frame(index, _state);
                internal Frame With_state(object? state) => new Frame(_index, state);
            }
        }
        """;

    private const string L4TupleUserSource = """
        namespace N
        {
            internal static class User
            {
                internal static Frame Build(int count, object? newState)
                {
                    var frame = new Frame(0, null);
                    (frame._index, frame._state) = (count, newState);
                    return frame;
                }
            }
        }
        """;

    private const string L3StructSource = """
        namespace N
        {
            public readonly struct Frame
            {
                internal int Index { get; }
                internal object? State { get; }

                internal Frame(int index, object? state)
                {
                    Index = index;
                    State = state;
                }
            }
        }
        """;

    private const string L3TupleUserSource = """
        namespace N
        {
            internal static class User
            {
                internal static Frame Build(int count, object? newState)
                {
                    var frame = new Frame(0, null);
                    (frame.Index, frame.State) = (count, newState);
                    return frame;
                }
            }
        }
        """;

    private const string L3TupleVariableUserSource = """
        namespace N
        {
            internal static class User
            {
                private static (int, object?) GetPair(int count, object? newState) => (count, newState);

                internal static Frame Build(int count, object? newState)
                {
                    var frame = new Frame(0, null);
                    (frame.Index, frame.State) = GetPair(count, newState);
                    return frame;
                }
            }
        }
        """;

    [Fact]
    public async Task TupleAssignment_ToL4StructMembers_RewrittenToWithChain()
    {
        var rewritten = await RunCrossFileRewriteAsync(L4StructSource, L4TupleUserSource);

        Assert.DoesNotContain("(frame._index, frame._state)", rewritten);
        Assert.Contains("frame = frame.With_index(count).With_state(newState)", rewritten);
    }

    [Fact]
    public async Task TupleAssignment_ToL3StructMembers_RewrittenToSingleConstructor()
    {
        var rewritten = await RunCrossFileRewriteAsync(L3StructSource, L3TupleUserSource);

        Assert.DoesNotContain("(frame.Index, frame.State)", rewritten);
        Assert.Contains("frame = new Frame(index: count, state: newState)", rewritten);
    }

    [Fact]
    public async Task TupleAssignment_FromValueTupleVariable_HoistsAndWritesBackSequentially()
    {
        var rewritten = await RunCrossFileRewriteAsync(L3StructSource, L3TupleVariableUserSource);

        Assert.Contains("var __cs2jTuple", rewritten);
        Assert.Contains("frame = new Frame(index: __cs2jTuple", rewritten);
        Assert.Contains("state: __cs2jTuple", rewritten);
    }

    private static async Task<string> RunCrossFileRewriteAsync(
        string structSource, string userSource)
    {
        var root = Path.Combine(Path.GetTempPath(), "cs2j-readonly-" + Guid.NewGuid().ToString("N"));
        var sourceRoot = Path.Combine(root, "src");
        var intermediateRoot = Path.Combine(root, "out");
        var structRelative = Path.Combine("N", "Frame.cs");
        var userRelative = Path.Combine("N", "User.cs");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(intermediateRoot, structRelative))!);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(intermediateRoot, userRelative))!);
        File.WriteAllText(Path.Combine(intermediateRoot, structRelative), structSource);
        File.WriteAllText(Path.Combine(intermediateRoot, userRelative), userSource);
        var sourceFiles = new List<string>
        {
            Path.Combine(sourceRoot, structRelative),
            Path.Combine(sourceRoot, userRelative),
        };

        try
        {
            await ReadOnlyStructPassEngine.UpdateCrossFileFieldReads(intermediateRoot, sourceRoot, sourceFiles);
            return File.ReadAllText(Path.Combine(intermediateRoot, userRelative));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
