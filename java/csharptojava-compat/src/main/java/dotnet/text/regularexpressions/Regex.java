package dotnet.text.regularexpressions;

/**
 * Minimal System.Text.RegularExpressions.Regex compat used by the converted
 * JSON tests (whitespace stripping helpers only).
 */
public final class Regex {
    private final java.util.regex.Pattern pattern;

    public Regex(String pattern) {
        this(pattern, RegexOptions.None);
    }

    public Regex(String pattern, int options) {
        int flags = 0;
        if ((options & RegexOptions.IgnoreCase) != 0) {
            flags |= java.util.regex.Pattern.CASE_INSENSITIVE;
        }
        if ((options & RegexOptions.Multiline) != 0) {
            flags |= java.util.regex.Pattern.MULTILINE;
        }
        if ((options & RegexOptions.Singleline) != 0) {
            flags |= java.util.regex.Pattern.DOTALL;
        }
        this.pattern = java.util.regex.Pattern.compile(pattern, flags);
    }

    public boolean isMatch(String input) {
        return input != null && pattern.matcher(input).find();
    }

    public Match match(String input) {
        return new Match(pattern.matcher(input == null ? "" : input));
    }

    public String replace(String input, String replacement) {
        if (input == null) {
            return null;
        }
        return pattern.matcher(input).replaceAll(java.util.regex.Matcher.quoteReplacement(replacement));
    }

    public String[] split(String input) {
        return pattern.split(input == null ? "" : input);
    }

    /** C# Regex.Split(string input, string pattern). */
    public static String[] split(String input, String pattern) {
        return new Regex(pattern).split(input);
    }

    /** C# Regex.Unescape. */
    public static String unescape(String value) {
        return value == null ? null : java.util.regex.Pattern.compile("\\\\.").matcher(value)
                .replaceAll(match -> java.util.regex.Matcher.quoteReplacement(match.group()));
    }

    /** Minimal Match view over a Matcher. */
    public static final class Match {
        private final java.util.regex.Matcher matcher;

        Match(java.util.regex.Matcher matcher) {
            this.matcher = matcher;
        }

        public boolean getSuccess() {
            return matcher.find();
        }

        public String getValue() {
            return matcher.group();
        }
    }
}
