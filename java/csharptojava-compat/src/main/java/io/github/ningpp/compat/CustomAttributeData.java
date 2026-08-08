package io.github.ningpp.compat;

/**
 * Stub for System.Reflection.CustomAttributeData.
 */
public class CustomAttributeData {
    private final Class<?> attributeType;
    private final CSharpList<CustomAttributeTypedArgument> constructorArguments;

    public CustomAttributeData(Class<?> attributeType, CSharpList<CustomAttributeTypedArgument> constructorArguments) {
        this.attributeType = attributeType;
        this.constructorArguments = constructorArguments;
    }

    public Class<?> getAttributeType() {
        return attributeType;
    }

    public CSharpList<CustomAttributeTypedArgument> getConstructorArguments() {
        return constructorArguments;
    }
}
