package io.github.ningpp.compat;

import java.util.regex.Matcher;
import java.util.regex.Pattern;

public final class Regex {
    private final Pattern pattern;

    public Regex(String pattern) {
        this(pattern, RegexOptions.None);
    }

    public Regex(String pattern, int options) {
        this.pattern = Pattern.compile(pattern, toJavaFlags(options));
    }

    public Match match(String input) {
        Matcher matcher = pattern.matcher(input == null ? "" : input);
        return matcher.find() ? new Match(matcher, true) : Match.Empty;
    }

    public boolean isMatch(String input) {
        return pattern.matcher(input == null ? "" : input).find();
    }

    public MatchCollection matches(String input) {
        MatchCollection collection = new MatchCollection();
        java.util.regex.Matcher matcher = pattern.matcher(input == null ? "" : input);
        while (matcher.find()) {
            collection.add(new Match(matcher, true));
        }
        return collection;
    }

    public MatchCollection matches(String input, int startat) {
        MatchCollection collection = new MatchCollection();
        java.util.regex.Matcher matcher = pattern.matcher(input == null ? "" : input);
        int start = startat;
        while (matcher.find(start)) {
            collection.add(new Match(matcher, true));
            start = matcher.end();
        }
        return collection;
    }

    public static Match match(String input, String pattern) {
        return new Regex(pattern).match(input);
    }

    public static boolean isMatch(String input, String pattern) {
        return new Regex(pattern).isMatch(input);
    }

    public static String[] split(String input, String pattern) {
        return Pattern.compile(pattern).split(input == null ? "" : input);
    }

    private static int toJavaFlags(int options) {
        int flags = 0;
        if ((options & RegexOptions.IgnoreCase) != 0) {
            flags |= Pattern.CASE_INSENSITIVE;
        }
        return flags;
    }
}
