package io.github.ningpp.compat;

/**
 * Compat stub for System.Runtime.CompilerServices.TypeForwardedFromAttribute.
 */
public class TypeForwardedFromAttribute {

    private final String assemblyName;

    public TypeForwardedFromAttribute(String assemblyName) {
        this.assemblyName = assemblyName;
    }

    public String getAssemblyName() {
        return assemblyName;
    }

    public String getAssemblyFullName() {
        return assemblyName;
    }
}
