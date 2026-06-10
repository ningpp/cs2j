package io.github.ningpp.compat;

/**
 * Bridges C# System.Globalization.UnicodeCategory to Java Character.UnicodeCategory.
 * C# uses UnicodeCategory enum values (e.g., Format, Control, LetterUppercase).
 * Java uses Character.UnicodeCategory enum with the same values but different casing.
 */
public enum UnicodeCategory {

    UppercaseLetter,
    LowercaseLetter,
    TitlecaseLetter,
    ModifierLetter,
    OtherLetter,
    NonSpacingMark,
    SpacingCombiningMark,
    EnclosingMark,
    DecimalDigitNumber,
    LetterNumber,
    OtherNumber,
    SpaceSeparator,
    LineSeparator,
    ParagraphSeparator,
    Control,
    Format,
    Surrogate,
    PrivateUse,
    ConnectorPunctuation,
    DashPunctuation,
    OpenPunctuation,
    ClosePunctuation,
    InitialQuotePunctuation,
    FinalQuotePunctuation,
    OtherPunctuation,
    MathSymbol,
    CurrencySymbol,
    ModifierSymbol,
    OtherSymbol,
    OtherNotAssigned;

    public static UnicodeCategory of(int javaType) {
        return switch (javaType) {
            case java.lang.Character.UPPERCASE_LETTER -> UppercaseLetter;
            case java.lang.Character.LOWERCASE_LETTER -> LowercaseLetter;
            case java.lang.Character.TITLECASE_LETTER -> TitlecaseLetter;
            case java.lang.Character.MODIFIER_LETTER -> ModifierLetter;
            case java.lang.Character.OTHER_LETTER -> OtherLetter;
            case java.lang.Character.NON_SPACING_MARK -> NonSpacingMark;
            case java.lang.Character.COMBINING_SPACING_MARK -> SpacingCombiningMark;
            case java.lang.Character.ENCLOSING_MARK -> EnclosingMark;
            case java.lang.Character.DECIMAL_DIGIT_NUMBER -> DecimalDigitNumber;
            case java.lang.Character.LETTER_NUMBER -> LetterNumber;
            case java.lang.Character.OTHER_NUMBER -> OtherNumber;
            case java.lang.Character.SPACE_SEPARATOR -> SpaceSeparator;
            case java.lang.Character.LINE_SEPARATOR -> LineSeparator;
            case java.lang.Character.PARAGRAPH_SEPARATOR -> ParagraphSeparator;
            case java.lang.Character.CONTROL -> Control;
            case java.lang.Character.FORMAT -> Format;
            case java.lang.Character.SURROGATE -> Surrogate;
            case java.lang.Character.PRIVATE_USE -> PrivateUse;
            case java.lang.Character.CONNECTOR_PUNCTUATION -> ConnectorPunctuation;
            case java.lang.Character.DASH_PUNCTUATION -> DashPunctuation;
            case java.lang.Character.START_PUNCTUATION -> OpenPunctuation;
            case java.lang.Character.END_PUNCTUATION -> ClosePunctuation;
            case java.lang.Character.INITIAL_QUOTE_PUNCTUATION -> InitialQuotePunctuation;
            case java.lang.Character.FINAL_QUOTE_PUNCTUATION -> FinalQuotePunctuation;
            case java.lang.Character.OTHER_PUNCTUATION -> OtherPunctuation;
            case java.lang.Character.MATH_SYMBOL -> MathSymbol;
            case java.lang.Character.CURRENCY_SYMBOL -> CurrencySymbol;
            case java.lang.Character.MODIFIER_SYMBOL -> ModifierSymbol;
            case java.lang.Character.OTHER_SYMBOL -> OtherSymbol;
            default -> OtherNotAssigned;
        };
    }
}
