using System;

namespace SampleEnumDeclarationLib;

// [Flags] + byte backing: normalized to enum-like sealed class.
[Flags]
public enum Color : byte
{
    None = 0,
    Red = 1,
    Green = 2,
    Blue = 4,
}

// Non-int backing with explicit values: normalized.
public enum Code : long
{
    Ok = 200,
    NotFound = 404,
    Error = 500,
}

// Plain int enum without [Flags]: must stay a real enum (byte-level untouched path).
public enum Priority
{
    Low,
    Normal,
    High,
}

public static class EnumSamples
{
    public const Color FixedColor = Color.Red;

    // E? 字段（设计 §7.2）：class 形态下即可空引用字段。
    public static Color? MaybeColor;

    public static string Describe(Color c)
    {
        Color combo = Color.Red | Color.Blue;
        bool hasRed = c.HasFlag(Color.Red);
        string hex = $"{combo:X2}";
        Color parsed = Enum.Parse<Color>("Red, Green");
        bool parsedOk = Enum.TryParse<Color>("7", out var byValue);
        string? name = Enum.GetName(typeof(Code), (long)404);
        Code def = default(Code);
        long raw = (long)Code.NotFound;
        Code back = (Code)raw;
        Color? maybe = c;
        MaybeColor = maybe;
        bool maybeNull = maybe == null;

        switch (c)
        {
            case Color.Red:
                return "red";
            case Color.Green:
            case Color.Blue:
                return "cool";
            default:
                return c.ToString();
        }
    }

    public static int Rank(Code code) => code switch
    {
        Code.Ok => 0,
        Code.NotFound => 1,
        _ => 2,
    };

    public static bool IsNormal(Priority p) => p == Priority.Normal;

    public static string TouchUnused(
        bool hasRed, string hex, Color parsed, bool parsedOk,
        string? name, Code def, Code back, bool maybeNull, Color fixedColor)
        => string.Concat(
            hasRed, hex, parsed.ToString(), parsedOk, name,
            def.ToString(), back.ToString(), maybeNull, fixedColor.ToString());
}
