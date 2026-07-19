package io.github.ningpp.compat;

import org.junit.jupiter.api.Test;

import static org.junit.jupiter.api.Assertions.assertEquals;

class TypeForwardedFromAttributeTest {

    @Test
    void assemblyNameRoundTrip() {
        TypeForwardedFromAttribute attr = new TypeForwardedFromAttribute("MyAssembly");
        assertEquals("MyAssembly", attr.getAssemblyName());
    }
}
