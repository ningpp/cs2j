package io.github.ningpp.compat;

public class ParameterExpression extends Expression {
    private final Class<?> type;
    private final String name;

    public ParameterExpression(Class<?> type) {
        this(type, null);
    }

    public ParameterExpression(Class<?> type, String name) {
        this.type = type;
        this.name = name;
    }

    public Class<?> getType() {
        return type;
    }

    public String getName() {
        return name;
    }
}
