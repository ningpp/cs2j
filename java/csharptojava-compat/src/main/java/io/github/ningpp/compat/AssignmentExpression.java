package io.github.ningpp.compat;

public class AssignmentExpression extends Expression {
    private final MemberExpression member;
    private final Expression value;

    public AssignmentExpression(MemberExpression member, Expression value) {
        this.member = member;
        this.value = value;
    }

    public MemberExpression getMember() {
        return member;
    }

    public Expression getValue() {
        return value;
    }

    public FieldInfo getFieldInfo() {
        return member.getFieldInfo();
    }
}
