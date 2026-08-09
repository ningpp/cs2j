package io.github.ningpp.compat;

/**
 * Stub for System.Diagnostics.DebuggerBrowsableAttribute.
 * Carries the DebuggerBrowsableState so reflection-based debugger tests
 * (DebuggerAttributes.ValidateDebuggerTypeProxyProperties) can filter
 * RootHidden members.
 */
public class DebuggerBrowsableAttribute {
    private final DebuggerBrowsableState state;

    public DebuggerBrowsableAttribute(DebuggerBrowsableState state) {
        this.state = state;
    }

    public DebuggerBrowsableAttribute(int state) {
        this.state = DebuggerBrowsableState.fromValue(state);
    }

    public DebuggerBrowsableState getState() {
        return state;
    }

    public CSharpList<CustomAttributeTypedArgument> getConstructorArguments() {
        CSharpList<CustomAttributeTypedArgument> args = new CSharpList<>();
        // Enums in attribute constructor arguments are boxed as their underlying int.
        args.add(new CustomAttributeTypedArgument(Integer.class, state.getValue()));
        return args;
    }
}
