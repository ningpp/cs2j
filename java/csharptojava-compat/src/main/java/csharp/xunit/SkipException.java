package csharp.xunit;

import csharp.xunit.Sdk.XunitException;

/**
 * Compat for Xunit.Sdk.SkipException (Xunit.SkippableFact).
 */
public class SkipException extends XunitException {
    public SkipException(String message) {
        super(message);
    }
}
