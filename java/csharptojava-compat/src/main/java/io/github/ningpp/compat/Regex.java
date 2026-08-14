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

    public static boolean isMatch(String input, String pattern) {        return new Regex(pattern).isMatch(input);
      }

    /** Mirrors Regex.Replace(input, replacement). */
    public String replace(String input, String replacement) {
        return pattern.matcher(input == null ? "" : input).replaceAll(replacement);
    }

    /** Mirrors Regex.Replace(input, replacement) — static form. */
    public static String replace(String input, String pattern, String replacement) {
        return new Regex(pattern).replace(input, replacement);
    }


    public static String[] split(String input, String pattern) {
        return Pattern.compile(pattern).split(input == null ? "" : input);
    }

    /** Mirrors Regex.Escape(string). */
    public static String escape(String str) {
        if (str == null) {
            throw new ArgumentNullException("str");
        }
        for (int i = 0; i < str.length(); i++) {
            if (isMetachar(str.charAt(i))) {
                return escapeImpl(str, i);
            }
        }
        return str;
    }

    private static String escapeImpl(String input, int i) {
        StringBuilder vsb = new StringBuilder(input.length() + 200);
        char ch = input.charAt(i);
        vsb.append(input, 0, i);
        do {
            vsb.append('\\');
            switch (ch) {
                case '\n':
                    ch = 'n';
                    break;
                case '\r':
                    ch = 'r';
                    break;
                case '\t':
                    ch = 't';
                    break;
                case '\f':
                    ch = 'f';
                    break;
                default:
                    break;
            }
            vsb.append(ch);
            i++;
            int lastpos = i;
            while (i < input.length()) {
                ch = input.charAt(i);
                if (isMetachar(ch)) {
                    break;
                }
                i++;
            }
            vsb.append(input, lastpos, i);
        } while (i < input.length());
        return vsb.toString();
    }

    /** Mirrors Regex.Unescape(string). */
    public static String unescape(String str) {
        if (str == null) {
            throw new ArgumentNullException("str");
        }
        int i = str.indexOf('\\');
        return i >= 0 ? unescapeImpl(str, i) : str;
    }

    private static String unescapeImpl(String input, int i) {
        StringBuilder vsb = new StringBuilder(input.length());
        vsb.append(input, 0, i);
        do {
            i++;
            if (i < input.length()) {
                IntHolder pos = new IntHolder(i);
                vsb.append(scanCharEscape(input, pos));
                i = pos.value;
            }
            int lastpos = i;
            while (i < input.length() && input.charAt(i) != '\\') {
                i++;
            }
            vsb.append(input, lastpos, i);
        } while (i < input.length());
        return vsb.toString();
    }

    private static char scanCharEscape(String input, IntHolder pos) {
        char ch = input.charAt(pos.value++);
        if (ch >= '0' && ch <= '7') {
            pos.value--;
            return scanOctal(input, pos);
        }
        switch (ch) {
            case 'x':
                return scanHex(input, pos, 2);
            case 'u':
                return scanHex(input, pos, 4);
            case 'a':
                return '\u0007';
            case 'b':
                return '\b';
            case 'e':
                return '\u001B';
            case 'f':
                return '\f';
            case 'n':
                return '\n';
            case 'r':
                return '\r';
            case 't':
                return '\t';
            case 'v':
                return '\u000B';
            case 'c':
                return scanControl(input, pos);
            default:
                if (isWordChar(ch)) {
                    throw new ArgumentException("Unrecognized escape sequence \\" + ch + ".");
                }
                return ch;
        }
    }

    private static char scanOctal(String input, IntHolder pos) {
        int c = 3;
        if (c > input.length() - pos.value) {
            c = input.length() - pos.value;
        }
        int i = 0;
        while (c > 0) {
            int d = input.charAt(pos.value) - '0';
            if (d < 0 || d > 7) {
                break;
            }
            pos.value++;
            i = (i * 8) + d;
            c--;
        }
        i &= 0xFF;
        return (char) i;
    }

    private static char scanHex(String input, IntHolder pos, int c) {
        int i = 0;
        if (input.length() - pos.value >= c) {
            while (c > 0) {
                int d = hexDigit(input.charAt(pos.value));
                if (d < 0) {
                    break;
                }
                pos.value++;
                i = (i * 0x10) + d;
                c--;
            }
        }
        if (c > 0) {
            throw new ArgumentException("Insufficient or invalid hex digits.");
        }
        return (char) i;
    }

    private static int hexDigit(char ch) {
        int d = ch - '0';
        if (d >= 0 && d <= 9) {
            return d;
        }
        d = ch - 'a';
        if (d >= 0 && d <= 5) {
            return d + 0xa;
        }
        d = ch - 'A';
        if (d >= 0 && d <= 5) {
            return d + 0xa;
        }
        return -1;
    }

    private static char scanControl(String input, IntHolder pos) {
        if (pos.value >= input.length()) {
            throw new ArgumentException("Missing control character.");
        }
        char ch = input.charAt(pos.value++);
        if (ch >= 'a' && ch <= 'z') {
            ch = (char) (ch - ('a' - 'A'));
        }
        ch = (char) (ch - '@');
        if (ch < ' ') {
            return ch;
        }
        throw new ArgumentException("Unrecognized control character.");
    }

    private static boolean isWordChar(char ch) {
        if (ch < 128) {
            return (ch >= 'A' && ch <= 'Z') || (ch >= 'a' && ch <= 'z')
                || (ch >= '0' && ch <= '9') || ch == '_';
        }
        switch (Character.getType(ch)) {
            case Character.UPPERCASE_LETTER:
            case Character.LOWERCASE_LETTER:
            case Character.TITLECASE_LETTER:
            case Character.MODIFIER_LETTER:
            case Character.OTHER_LETTER:
            case Character.NON_SPACING_MARK:
            case Character.DECIMAL_DIGIT_NUMBER:
            case Character.CONNECTOR_PUNCTUATION:
                return true;
            default:
                return ch == '\u200C' || ch == '\u200D';
        }
    }

    private static boolean isMetachar(char ch) {
        switch (ch) {
            case ' ':
            case '\t':
            case '\n':
            case '\f':
            case '\r':
            case '#':
            case '$':
            case '(':
            case ')':
            case '*':
            case '+':
            case '.':
            case '?':
            case '[':
            case '\\':
            case '^':
            case '{':
            case '|':
                return true;
            default:
                return false;
        }
    }

    private static int toJavaFlags(int options) {
        int flags = 0;
        if ((options & RegexOptions.IgnoreCase) != 0) {
            flags |= Pattern.CASE_INSENSITIVE;
        }
        if ((options & RegexOptions.Singleline) != 0) {
            flags |= Pattern.DOTALL;
        }
        return flags;
    }
}
