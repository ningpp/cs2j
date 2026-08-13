package io.github.ningpp.compat;

/**
 * Compat for Xunit.Sdk.BeforeAfterTestAttribute.
 */
public abstract class BeforeAfterTestAttribute {
    public void before(MethodInfo methodUnderTest) {
    }

    public void after(MethodInfo methodUnderTest) {
    }
}
