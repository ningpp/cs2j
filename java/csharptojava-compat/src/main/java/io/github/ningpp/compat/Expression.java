package io.github.ningpp.compat;

/**
 * Minimal shim for {@code System.Linq.Expressions.Expression}.
 * Supports the small subset of expression trees emitted by the converter,
 * currently limited to building a field-assignment lambda that can be
 * compiled into a Java functional interface backed by reflection.
 */
public abstract class Expression {

    public static ParameterExpression parameter(Class<?> type) {
        return new ParameterExpression(type);
    }

    public static ParameterExpression parameter(Class<?> type, String name) {
        return new ParameterExpression(type, name);
    }

    public static MemberExpression field(ParameterExpression objectParam, FieldInfo fieldInfo) {
        return new MemberExpression(objectParam, fieldInfo);
    }

    public static AssignmentExpression assign(MemberExpression member, Expression value) {
        return new AssignmentExpression(member, value);
    }

    public static LambdaExpression lambda(Expression body, ParameterExpression... parameters) {
        return new LambdaExpression(body, parameters);
    }
}
