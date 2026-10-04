namespace CSharpToJava.Core.ExpressionDesugar.NullConditional.Assignment;

/// <summary>Operator of an assignment terminal (design §2.2 / NA-1...NA-6).</summary>
internal enum AssignmentOperatorKind
{
    /// <summary>Unknown operator token (safety net).</summary>
    Unknown,

    /// <summary>Simple assignment <c>=</c>.</summary>
    Simple,

    /// <summary>Compound assignment <c>+=</c> ... <c>&gt;&gt;=</c> (10 operators).</summary>
    Compound,

    /// <summary>Null-coalescing assignment <c>??=</c> (NA-6).</summary>
    Coalesce,
}

/// <summary>Shape of the assignment LHS target (design §2.1 matrix).</summary>
internal enum AssignmentTargetShape
{
    /// <summary>Unrecognized target (safety net).</summary>
    Unknown,

    /// <summary>
    /// Member target: <c>a?.F = c</c>, <c>a?.P = c</c>, incl. the ordinary
    /// tail M7 <c>a?.Inner.Val = d</c>.
    /// </summary>
    Member,

    /// <summary>Index target: <c>a?[i] = v</c>.</summary>
    Index,

    /// <summary>Ref-returning method target M10: <c>a?.M() = 42</c>.</summary>
    RefReturningMethod,

    /// <summary>Event compound target M5: <c>a?.E += h</c>.</summary>
    Event,
}
