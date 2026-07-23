package io.github.ningpp.compat;

public class MemberExpression extends Expression {
    private final ParameterExpression objectParam;
    private final FieldInfo fieldInfo;

    public MemberExpression(ParameterExpression objectParam, FieldInfo fieldInfo) {
        this.objectParam = objectParam;
        this.fieldInfo = fieldInfo;
    }

    public ParameterExpression getObjectParam() {
        return objectParam;
    }

    public FieldInfo getFieldInfo() {
        return fieldInfo;
    }
}
