using System.Text;
using CSharpToJava.Core.BigLiteralResource;
using CSharpToJava.Core.Preprocessing;

namespace CSharpToJava.Tests;

/// <summary>
/// Core matrix for the lexical pre-pass <c>big-literal-resource</c>: oversized
/// literal payloads leave the C# file and become content-addressed resource
/// files, everything else stays byte-identical.
/// </summary>
public sealed class BigLiteralResourceTests
{
    private static BigLiteralResourceResult Run(
        string source,
        BigLiteralResourceOptions? options = null)
        => new BigLiteralResourceExtractor(options).RewriteFile(source, "Demo.cs");

    private static string ByteArrayField(int count, string indent = "        ")
    {
        var builder = new StringBuilder();
        builder.AppendLine("public static class Demo");
        builder.AppendLine("{");
        builder.Append(indent).Append("public static readonly byte[] Data = new byte[]");
        builder.AppendLine();
        builder.Append(indent).AppendLine("{");
        for (var index = 0; index < count; index++)
        {
            builder.Append(indent).Append("    0x").Append((index % 256).ToString("X2"));
            builder.AppendLine(index == count - 1 ? string.Empty : ",");
        }

        builder.Append(indent).AppendLine("};");
        builder.AppendLine("}");
        return builder.ToString();
    }

    [Fact]
    public void Byte_array_over_the_threshold_is_externalized_to_a_bin_payload()
    {
        const int count = 8192;
        var source = ByteArrayField(count);

        var result = Run(source);

        Assert.True(result.Changed);
        Assert.Equal(1, result.Statistics.FilesTransformed);
        Assert.Equal(1, result.Statistics.ArraysExtracted);
        Assert.Equal(count, result.Statistics.PayloadBytes);

        var payload = Assert.Single(result.Payloads);
        Assert.EndsWith(".bin", payload.FileName, StringComparison.Ordinal);
        Assert.StartsWith("cs2j/", payload.ResourceName, StringComparison.Ordinal);
        Assert.Contains("Demo.Data.", payload.FileName, StringComparison.Ordinal);
        Assert.Equal(count, payload.Content.Length);
        for (var index = 0; index < count; index++)
        {
            Assert.Equal((byte)(index % 256), payload.Content[index]);
        }

        // The literal is gone; the loader call took its place.
        Assert.DoesNotContain("0x00, 0x01", result.OutputCode, StringComparison.Ordinal);
        Assert.Contains(
            $"global::Cs2jPrelude.Cs2jResource.LoadBytes(\"{payload.ResourceName}\")",
            result.OutputCode,
            StringComparison.Ordinal);
        Assert.True(result.OutputCode.Length < source.Length / 4);
    }

    [Fact]
    public void Array_below_the_threshold_stays_byte_identical()
    {
        var source = ByteArrayField(16);

        var result = Run(source);

        Assert.False(result.Changed);
        Assert.Empty(result.Payloads);
        Assert.Equal(source, result.OutputCode);
        Assert.Equal(1, result.Statistics.FilesSkippedClean);
    }

    [Fact]
    public void Hex_binary_separators_negatives_and_casts_decode_exactly()
    {
        var source = """
            public static class Demo
            {
                public static readonly int[] Values = new int[]
                {
                    0x7FFF_FFFF, 0b1010, -1, (int)0x0000_0001, 0,
                    1234567890, -2147483648, 2147483647, 0x10, 0x20,
                    0x30, 0x40, 0x50, 0x60, 0x70, 0x80,
                    0x90, 0xA0, 0xB0, 0xC0, 0xD0, 0xE0,
                    0xF0, 0x100, 0x110, 0x120, 0x130, 0x140,
                    0x150, 0x160, 0x170, 0x180, 0x190, 0x1A0,
                };
            }
            """;

        var result = Run(source, new BigLiteralResourceOptions { MinArrayPayloadBytes = 1 });

        Assert.True(result.Changed);
        var payload = Assert.Single(result.Payloads);
        Assert.Equal(34 * 4, payload.Content.Length);

        var values = new int[34];
        for (var index = 0; index < values.Length; index++)
        {
            values[index] = BitConverter.ToInt32(payload.Content, index * 4);
        }

        Assert.Equal(int.MaxValue, values[0]);
        Assert.Equal(10, values[1]);
        Assert.Equal(-1, values[2]);
        Assert.Equal(1, values[3]);
        Assert.Equal(0, values[4]);
        Assert.Equal(1234567890, values[5]);
        Assert.Equal(int.MinValue, values[6]);
        Assert.Equal(int.MaxValue, values[7]);
        Assert.Equal(0x1A0, values[33]);
    }

    [Fact]
    public void String_array_payload_is_length_prefixed_utf8()
    {
        var entries = Enumerable.Range(0, 700)
            .Select(index => "value-" + index + "-π")
            .ToArray();
        var source = $$"""
            public static class Demo
            {
                public static readonly string[] Names = new string[]
                {
                    {{string.Join(",\n            ", entries.Select(entry => "\"" + entry + "\""))}}
                };
            }
            """;

        var result = Run(source);

        Assert.True(result.Changed);
        Assert.Equal(1, result.Statistics.ArraysExtracted);
        Assert.Equal(0, result.Statistics.StringsExtracted);

        var payload = Assert.Single(result.Payloads);
        Assert.Equal(entries, DecodeStrings(payload.Content));

        Assert.Contains(
            $"global::Cs2jPrelude.Cs2jResource.LoadStrings(\"{payload.ResourceName}\")",
            result.OutputCode,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Very_long_string_literal_becomes_a_txt_payload()
    {
        var text = new string('x', 5000);
        var source = $$"""
            public static class Demo
            {
                public static readonly string Blob = "{{text}}";
            }
            """;

        var result = Run(source);

        Assert.True(result.Changed);
        Assert.Equal(1, result.Statistics.StringsExtracted);
        var payload = Assert.Single(result.Payloads);
        Assert.EndsWith(".txt", payload.FileName, StringComparison.Ordinal);
        Assert.Equal(text, Encoding.UTF8.GetString(payload.Content));
        Assert.Contains(
            $"global::Cs2jPrelude.Cs2jResource.LoadString(\"{payload.ResourceName}\")",
            result.OutputCode,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Short_string_literal_is_not_touched()
    {
        const string source = """
            public static class Demo
            {
                public static readonly string Blob = "short";
            }
            """;

        var result = Run(source);

        Assert.False(result.Changed);
        Assert.Empty(result.Payloads);
        Assert.Equal(source, result.OutputCode);
    }

    [Fact]
    public void Const_declarations_are_preserved_with_a_warning()
    {
        var text = new string('y', 5000);
        var source = $$"""
            public static class Demo
            {
                public const string Blob = "{{text}}";
            }
            """;

        var result = Run(source);

        Assert.False(result.Changed);
        Assert.Empty(result.Payloads);
        Assert.Equal(source, result.OutputCode);
        var diagnostic = Assert.Single(
            result.Diagnostics,
            entry => entry.Code == PreprocessDiagnosticCodes.BigLiteralConstDeclaration);
        Assert.Equal(BigLiteralResourceSeverity.Warning, diagnostic.Severity);
    }

    [Fact]
    public void Arrays_mixing_literals_with_expressions_stay_inline()
    {
        var source = """
            public static class Demo
            {
                private const int Seed = 7;

                public static readonly int[] Values = new int[]
                {
                    1, 2, 3, Seed, 5, 6, 7, 8,
                    9, 10, 11, 12, 13, 14, 15, 16,
                };
            }
            """;

        var result = Run(source, new BigLiteralResourceOptions { MinArrayPayloadBytes = 1 });

        Assert.False(result.Changed);
        Assert.Empty(result.Payloads);
        Assert.Equal(source, result.OutputCode);
        Assert.Equal(1, result.Statistics.SkippedNonLiteral);
        Assert.Contains(
            result.Diagnostics,
            entry => entry.Code == PreprocessDiagnosticCodes.BigLiteralNonLiteralElements);
    }

    [Theory]
    [InlineData("uint[]", "new uint[] { 1u, 2u }")]
    [InlineData("sbyte[]", "new sbyte[] { 1, 2 }")]
    [InlineData("System.Collections.Generic.List<int>", "new System.Collections.Generic.List<int>()")]
    [InlineData("byte[,]", "new byte[2, 2]")]
    [InlineData("byte[][]", "new byte[][] { new byte[] { 1 } }")]
    public void Unsupported_declaration_shapes_are_never_rewritten(string type, string initializer)
    {
        var source = $$"""
            public static class Demo
            {
                public static readonly {{type}} Value = {{initializer}};
            }
            """;

        var result = Run(source, new BigLiteralResourceOptions { MinArrayPayloadBytes = 1 });

        Assert.False(result.Changed);
        Assert.Empty(result.Payloads);
        Assert.Equal(source, result.OutputCode);
    }

    [Fact]
    public void Char_bool_and_long_arrays_use_their_native_widths()
    {
        var source = """
            public static class Demo
            {
                public static readonly char[] Letters = new char[] { 'a', 'π', '\n', '\uD83D', '\uDE00' };
                public static readonly bool[] Flags = new bool[] { true, false, true };
                public static readonly long[] Big = new long[] { 1, -2, 9223372036854775807 };
                public static readonly float[] Singles = new float[] { 1.5f, -0.25f, 3f };
                public static readonly double[] Doubles = new double[] { 1.5, -2.25, 1e10 };
            }
            """;

        var result = Run(
            source,
            new BigLiteralResourceOptions { MinArrayPayloadBytes = 1 });

        Assert.True(result.Changed);
        Assert.Equal(5, result.Payloads.Count);

        var letters = result.Payloads.Single(payload => payload.FileName.Contains("Letters", StringComparison.Ordinal));
        Assert.Equal(10, letters.Content.Length);
        Assert.Equal('a', (char)BitConverter.ToUInt16(letters.Content, 0));
        Assert.Equal('π', (char)BitConverter.ToUInt16(letters.Content, 2));
        Assert.Equal('\n', (char)BitConverter.ToUInt16(letters.Content, 4));
        // Lone surrogate halves survive because the payload stores raw UTF-16 units.
        Assert.Equal(0xD83D, BitConverter.ToUInt16(letters.Content, 6));
        Assert.Equal(0xDE00, BitConverter.ToUInt16(letters.Content, 8));

        var flags = result.Payloads.Single(payload => payload.FileName.Contains("Flags", StringComparison.Ordinal));
        Assert.Equal(new byte[] { 1, 0, 1 }, flags.Content);

        var big = result.Payloads.Single(payload => payload.FileName.Contains("Big", StringComparison.Ordinal));
        Assert.Equal(24, big.Content.Length);
        Assert.Equal(1L, BitConverter.ToInt64(big.Content, 0));
        Assert.Equal(-2L, BitConverter.ToInt64(big.Content, 8));
        Assert.Equal(long.MaxValue, BitConverter.ToInt64(big.Content, 16));

        var singles = result.Payloads.Single(payload => payload.FileName.Contains("Singles", StringComparison.Ordinal));
        Assert.Equal(1.5f, BitConverter.ToSingle(singles.Content, 0));
        Assert.Equal(-0.25f, BitConverter.ToSingle(singles.Content, 4));

        var doubles = result.Payloads.Single(payload => payload.FileName.Contains("Doubles", StringComparison.Ordinal));
        Assert.Equal(1e10, BitConverter.ToDouble(doubles.Content, 16));
    }

    [Fact]
    public void Nested_types_and_locals_contribute_their_owner_path_to_the_resource_name()
    {
        var builder = new StringBuilder("0x01");
        for (var index = 0; index < 4096; index++)
        {
            builder.Append(", 0x02");
        }

        var source = $$"""
            public static class Outer
            {
                public static class Inner
                {
                    public static void M()
                    {
                        byte[] buffer = new byte[] { {{builder}} };
                        System.Console.WriteLine(buffer.Length);
                    }
                }
            }
            """;

        var result = Run(source);

        Assert.True(result.Changed);
        var payload = Assert.Single(result.Payloads);
        Assert.Contains("Outer.Inner.M.buffer.", payload.FileName, StringComparison.Ordinal);
        Assert.Contains(
            $"global::Cs2jPrelude.Cs2jResource.LoadBytes(\"{payload.ResourceName}\")",
            result.OutputCode,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Rewrite_is_idempotent_and_preserves_trivia_outside_the_payload()
    {
        var source = ByteArrayField(4096).Replace(
            "        public static readonly byte[] Data",
            "        // leading comment\r\n        public static readonly byte[] Data",
            StringComparison.Ordinal);

        var first = Run(source);
        Assert.True(first.Changed);
        Assert.Contains("// leading comment", first.OutputCode, StringComparison.Ordinal);

        var second = Run(first.OutputCode);
        Assert.False(second.Changed);
        Assert.Equal(first.OutputCode, second.OutputCode);
        Assert.Empty(second.Payloads);
    }

    [Fact]
    public void Resource_names_are_content_addressed_so_identical_reruns_produce_identical_names()
    {
        var source = ByteArrayField(4096);

        var first = Run(source);
        var second = Run(source);

        Assert.Equal(
            Assert.Single(first.Payloads).ResourceName,
            Assert.Single(second.Payloads).ResourceName);
    }

    [Fact]
    public void Trivia_inside_the_replaced_expression_is_kept()
    {
        var elements = string.Join(", ", Enumerable.Repeat("0x01", 4096));
        var source = $$"""
            public static class Demo
            {
                public static readonly byte[] Data =
                    /* payload */
                    new byte[] { {{elements}} };
            }
            """;

        var result = Run(source);

        Assert.True(result.Changed);
        Assert.Contains("/* payload */", result.OutputCode, StringComparison.Ordinal);
    }

    [Fact]
    public void Disabled_options_leave_the_file_untouched()
    {
        var source = ByteArrayField(4096);

        var result = Run(source, new BigLiteralResourceOptions { Enabled = false });

        Assert.False(result.Changed);
        Assert.Empty(result.Payloads);
        Assert.Equal(source, result.OutputCode);
    }

    [Fact]
    public void Unpaired_surrogate_strings_are_preserved_with_a_warning()
    {
        var text = new string('z', 5000) + "\\uD800";
        var source = $$"""
            public static class Demo
            {
                public static readonly string Blob = "{{text}}";
            }
            """;

        var result = Run(source);

        Assert.False(result.Changed);
        Assert.Empty(result.Payloads);
        Assert.Contains(
            result.Diagnostics,
            entry => entry.Code == PreprocessDiagnosticCodes.BigLiteralUnpairedSurrogate);
    }

    [Fact]
    public void Threshold_options_are_honoured()
    {
        var source = ByteArrayField(1024);

        Assert.False(Run(source).Changed);
        Assert.True(Run(source, new BigLiteralResourceOptions { MinArrayPayloadBytes = 512 }).Changed);
    }

    [Fact]
    public void Multiple_declarators_in_one_statement_are_handled_independently()
    {
        var elements = string.Join(", ", Enumerable.Repeat("0x02", 4096));
        var source = $$"""
            public static class Demo
            {
                public static readonly byte[] A = new byte[] { {{elements}} }, B = new byte[] { 1 };
            }
            """;

        var result = Run(source);

        Assert.True(result.Changed);
        var payload = Assert.Single(result.Payloads);
        Assert.Contains("Demo.A.", payload.FileName, StringComparison.Ordinal);
        Assert.Contains("LoadBytes(", result.OutputCode, StringComparison.Ordinal);
        // The tiny second declarator keeps its literal.
        Assert.Contains("B = new byte[] { 1 }", result.OutputCode, StringComparison.Ordinal);
    }

    [Fact]
    public void Files_with_preprocessor_directives_round_trip_byte_exactly()
    {
        var elements = string.Join(", ", Enumerable.Repeat("0x03", 4096));
        var source = $$"""
            #define FEATURE
            #if FEATURE
            public static class Demo
            {
                public static readonly byte[] Data = new byte[] { {{elements}} };
            }
            #else
            public static class Demo { }
            #endif
            """;

        var result = Run(source);

        Assert.True(result.Changed);
        Assert.Single(result.Payloads);
        Assert.Contains("#define FEATURE", result.OutputCode, StringComparison.Ordinal);
        Assert.Contains("#endif", result.OutputCode, StringComparison.Ordinal);
    }

    private static string[] DecodeStrings(byte[] content)
    {
        var values = new List<string>();
        var offset = 0;
        while (offset < content.Length)
        {
            var length = BitConverter.ToInt32(content, offset);
            offset += 4;
            values.Add(Encoding.UTF8.GetString(content, offset, length));
            offset += length;
        }

        return values.ToArray();
    }
}
