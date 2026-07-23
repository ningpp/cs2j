package io.github.ningpp.compat;

/**
 * Minimal shim for {@code System.Linq.Expressions.LambdaExpression}.
 * Only field-assignment lambdas are supported; {@code compile()} returns
 * a Java functional interface that performs the assignment via reflection.
 */
public class LambdaExpression extends Expression {
    private final Expression body;
    private final ParameterExpression[] parameters;

    public LambdaExpression(Expression body, ParameterExpression[] parameters) {
        this.body = body;
        this.parameters = parameters;
    }

    public Expression getBody() {
        return body;
    }

    public ParameterExpression[] getParameters() {
        return parameters;
    }

    /**
     * Compiles the lambda into a runnable Java functional interface.
     * Currently only supports one- and two-parameter field-assignment lambdas
     * (object, value) -&gt; object.field = value, which are compiled to a
     * {@link UniversalConsumer} that can be assigned to either
     * {@link java.util.function.Consumer} or {@link java.util.function.BiConsumer}.
     */
    public UniversalConsumer compile() {
        if (body instanceof AssignmentExpression assignment) {
            FieldInfo fieldInfo = assignment.getFieldInfo();
            return new UniversalConsumer() {
                @Override
                public void accept(Object obj) {
                    fieldInfo.setValue(obj, null);
                }

                @Override
                public void accept(Object obj, Object value) {
                    fieldInfo.setValue(obj, value);
                }
            };
        }
        throw new UnsupportedOperationException(
            "Only field-assignment lambdas with one or two parameters are supported");
    }
}
