using System;
using ExtHosts;

class Program
{
    static int Main()
    {
        // External metadata host with no TypeMappings landing → CS2J1203,
        // but the site is still explicitized.
        Console.WriteLine("hi".ShoutMetadata());

        // BCL metadata host System.MemoryExtensions HAS a type mapping →
        // graded Mapped, silent.
        Span<char> span = "abc".AsSpan();
        Console.WriteLine(span.Length);
        return 0;
    }
}
