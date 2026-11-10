namespace CSharpToJava.Core.BigLiteralResource;

/// <summary>
/// Options for the lexical pre-pass <c>big-literal-resource</c> (stage
/// <see cref="CSharpToJava.Core.Preprocessing.PassStage.Lexical"/>, the second
/// pass of the chain, right after <c>conditional</c>).
/// <para>
/// The pass moves oversized literal payloads (primitive/string array
/// initializers and single very long string literals) out of the C# source file
/// into per-payload resource files and replaces the literal with a call into the
/// <c>Cs2jPrelude.Cs2jResource</c> loader. Two independent problems drive it:
/// a 1 MB <c>DemoBigBytes.cs</c>/<c>DemoBigStrings.cs</c> makes every later
/// semantic pass pay for a huge syntax tree, and the Java emitter cannot express
/// the result at all — a <c>static</c> initializer over 64 KB of elements exceeds
/// the JVM method-size limit and a string constant over 65535 modified-UTF-8
/// bytes exceeds the constant-pool entry limit.
/// </para>
/// </summary>
public sealed class BigLiteralResourceOptions
{
    /// <summary>Master switch; when false every file is returned byte-for-byte.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// Per-declaration "always externalize" threshold (bytes) for an array
    /// initializer. 4096 keeps ordinary small tables inline: a <c>byte[4096]</c>
    /// or a <c>long[512]</c> is the smallest array that moves on its own.
    /// <para>
    /// A declaration below this size still moves when its FILE is over
    /// <see cref="MaxInlinePayloadBytes"/>; see the two-tier rule on
    /// <see cref="MaxInlinePayloadBytes"/>.
    /// </para>
    /// </summary>
    public int MinArrayPayloadBytes { get; init; } = 4096;

    /// <summary>
    /// Per-declaration "always externalize" threshold (UTF-8 bytes) for a single
    /// string literal. A literal above 65535 modified-UTF-8 bytes cannot be a
    /// Java constant at all, so this only decides how early the payload leaves
    /// the source file.
    /// </summary>
    public int MinStringPayloadBytes { get; init; } = 4096;

    /// <summary>
    /// Smallest payload (bytes) worth its own resource file. Declarations under
    /// this size never move: a 20-byte lookup table entry is cheaper inline than
    /// as a file plus a loader call.
    /// </summary>
    public int MinCandidatePayloadBytes { get; init; } = 256;

    /// <summary>
    /// Per-FILE inline budget (bytes). Two-tier rule:
    /// <list type="number">
    /// <item>a single declaration at or above <see cref="MinArrayPayloadBytes"/> /
    /// <see cref="MinStringPayloadBytes"/> always moves — that is the per-literal
    /// limit (<c>&lt;clinit&gt;</c> method size, Java constant-pool string size);</item>
    /// <item>when the sum of all movable candidate payloads in the file exceeds
    /// this budget, EVERY candidate at or above
    /// <see cref="MinCandidatePayloadBytes"/> moves — that is the per-file limit.</item>
    /// </list>
    /// <para>
    /// The second tier exists because the failure is often aggregate, not local:
    /// a class with 200 <c>int[1000]</c> fields has no single oversized literal,
    /// yet the generated <c>&lt;clinit&gt;</c> blows past the JVM 64 KB method
    /// limit ("code too large"), and a file of thousands of 1 KB strings converts
    /// into a multi-megabyte .java plus a multi-megabyte .class. 64 KB keeps a
    /// normal file's small tables untouched while guaranteeing that a file which
    /// is large *because of literals* is emptied out completely, instead of
    /// leaving an arbitrary half behind.
    /// </para>
    /// </summary>
    public int MaxInlinePayloadBytes { get; init; } = 64 * 1024;

    /// <summary>
    /// Name of the directory (created next to the declaring source file) that
    /// receives the extracted payload files.
    /// </summary>
    public string ResourceDirectoryName { get; init; } = "Cs2jResources";

    /// <summary>
    /// Logical resource-name prefix, i.e. the string handed to the loader and
    /// used as the MSBuild <c>LogicalName</c>. The Java side resolves the same
    /// string as a classpath resource.
    /// </summary>
    public string LogicalNamePrefix { get; init; } = "cs2j/";

    /// <summary>
    /// Fully qualified (alias-qualified) loader type the rewritten call targets.
    /// The C# tree resolves it through the injected <c>Cs2jPrelude</c> project;
    /// the Java half maps it through <c>config/TypeMappings.json</c> onto
    /// <c>io.github.ningpp.compat.Cs2jResource</c>.
    /// </summary>
    public string LoaderTypeName { get; init; } = "global::Cs2jPrelude.Cs2jResource";

    /// <summary>
    /// Emit the per-payload success diagnostics (CS2J4310). Off by default so a
    /// large corpus stays quiet; the adapter enables it in verbose mode.
    /// </summary>
    public bool ReportExtractions { get; init; }
}
