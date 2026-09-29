using System;

namespace SamplePatternLib;

/// <summary>
/// Catch-side constructs the C03 preprocessor must lower: catch filters become
/// guarded catch bodies, several clauses merge into one ordered chain, and
/// type-parameter catches bind through `is T`.
/// </summary>
public static class CatchSamples
{
    /// <summary>Filter false must fall through to the later same-type clause.</summary>
    public static string FilterThenSameTypeFallback(bool matchFilter)
    {
        try
        {
            throw new InvalidOperationException(matchFilter ? "expected" : "other");
        }
        catch (InvalidOperationException ex) when (ex.Message == "expected")
        {
            return "filtered";
        }
        catch (InvalidOperationException)
        {
            return "fallback";
        }
    }

    /// <summary>Filter false must keep the exception propagating to the outer handler.</summary>
    public static string FilterFalsePropagates()
    {
        try
        {
            try
            {
                throw new InvalidOperationException("no-match");
            }
            catch (InvalidOperationException ex) when (ex.Message == "expected")
            {
                return "wrongly-caught:" + ex.Message;
            }
        }
        catch (InvalidOperationException ex)
        {
            return "propagated:" + ex.Message;
        }
    }

    /// <summary>Ordered multi-type multi-filter chain.</summary>
    public static string MultipleFiltersInOrder(int kind)
    {
        try
        {
            throw kind switch
            {
                0 => new FormatException("format"),
                1 => new ArgumentException("argument"),
                _ => new InvalidOperationException("operation"),
            };
        }
        catch (FormatException f) when (f.Message == "format")
        {
            return "format-first";
        }
        catch (FormatException)
        {
            return "format-second";
        }
        catch (ArgumentException a) when (a.Message == "argument")
        {
            return "argument";
        }
        catch (Exception e)
        {
            return "other:" + e.Message;
        }
    }

    /// <summary>`finally` must keep running regardless of which branch wins.</summary>
    public static string FilterWithFinally(bool matchFilter)
    {
        var log = "";
        try
        {
            try
            {
                throw new InvalidOperationException(matchFilter ? "expected" : "other");
            }
            catch (InvalidOperationException ex) when (ex.Message == "expected")
            {
                log += "caught;";
            }
            finally
            {
                log += "finally;";
            }
        }
        catch (InvalidOperationException)
        {
            log += "propagated;";
        }
        return log;
    }

    /// <summary>catch (T) requires `where T : Exception` (an unconstrained type parameter is CS0155).</summary>
    public static string CatchTypeParameter<T>(T exception) where T : Exception
    {
        try
        {
            throw exception;
        }
        catch (T ex) when (ex.Message == "typed")
        {
            return "typed-catch";
        }
    }
}
