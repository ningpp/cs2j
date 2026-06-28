package io.github.ningpp.compat;

public final class TypeConverterAttribute {
    private final String converterTypeName;
    private final Class<?> converterType;

    public TypeConverterAttribute(Class<?> converterType) {
        this.converterType = converterType;
        this.converterTypeName = converterType == null ? "" : converterType.getName();
    }

    public TypeConverterAttribute(String converterTypeName) {
        this.converterTypeName = converterTypeName == null ? "" : converterTypeName;
        this.converterType = null;
    }

    public String getConverterTypeName() {
        return converterTypeName;
    }

    Class<?> getConverterType() {
        return converterType;
    }
}
