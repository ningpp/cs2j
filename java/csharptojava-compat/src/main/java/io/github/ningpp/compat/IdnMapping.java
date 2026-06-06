package io.github.ningpp.compat;

import java.net.IDN;

/**
 * Facade for System.Globalization.IdnMapping.
 * Implements all public instance methods from .NET's System.Globalization.IdnMapping
 * using java.net.IDN for international domain name (IDN) support.
 *
 * <p>.NET's IdnMapping provides methods to convert domain names between
 * Unicode and ASCII-compatible encoding (Punycode). This Java implementation
 * uses java.net.IDN which provides similar functionality.</p>
 */
public class IdnMapping {

    private boolean allowUnassigned;
    private boolean useStd3AsciiRules;

    /**
     * Initializes a new instance of the IdnMapping class.
     * Default values: allowUnassigned = false, useStd3AsciiRules = false.
     */
    public IdnMapping() {
        this.allowUnassigned = false;
        this.useStd3AsciiRules = false;
    }

    // ---- Properties ----

    /**
     * Gets whether unassigned Unicode code points are used in operations.
     *
     * @return true if unassigned code points are allowed; otherwise, false.
     */
    public boolean getAllowUnassigned() {
        return allowUnassigned;
    }

    /**
     * Sets whether unassigned Unicode code points are used in operations.
     *
     * @param value true if unassigned code points should be allowed; otherwise, false.
     */
    public void setAllowUnassigned(boolean value) {
        this.allowUnassigned = value;
    }

    /**
     * Gets whether standard ANSI rules for ASCII strings are used.
     *
     * @return true if STD3 ASCII rules are enforced; otherwise, false.
     */
    public boolean getUseStd3AsciiRules() {
        return useStd3AsciiRules;
    }

    /**
     * Sets whether standard ANSI rules for ASCII strings are used.
     *
     * @param value true if STD3 ASCII rules should be enforced; otherwise, false.
     */
    public void setUseStd3AsciiRules(boolean value) {
        this.useStd3AsciiRules = value;
    }

    // ---- GetAscii methods ----

    /**
     * Converts a Unicode domain name to an ASCII-compatible domain name (Punycode).
     *
     * @param unicode The Unicode domain name to convert.
     * @return The ASCII-compatible encoding (ACE) of the domain name.
     * @throws IllegalArgumentException if the domain name contains invalid characters.
     */
    public String getAscii(String unicode) {
        return getAscii(unicode, 0, unicode.length());
    }

    /**
     * Converts a substring of a Unicode domain name to an ASCII-compatible domain name (Punycode).
     *
     * @param unicode The Unicode domain name to convert.
     * @param index The starting index of the substring to convert.
     * @return The ASCII-compatible encoding (ACE) of the specified substring.
     * @throws IllegalArgumentException if the domain name contains invalid characters.
     * @throws IndexOutOfBoundsException if index is out of range.
     */
    public String getAscii(String unicode, int index) {
        return getAscii(unicode, index, unicode.length() - index);
    }

    /**
     * Converts a specified number of characters from a Unicode domain name to an ASCII-compatible domain name (Punycode).
     *
     * @param unicode The Unicode domain name to convert.
     * @param index The starting index of the substring to convert.
     * @param count The number of characters to convert.
     * @return The ASCII-compatible encoding (ACE) of the specified substring.
     * @throws IllegalArgumentException if the domain name contains invalid characters.
     * @throws IndexOutOfBoundsException if index or count is out of range.
     */
    public String getAscii(String unicode, int index, int count) {
        if (unicode == null) {
            throw new IllegalArgumentException("unicode cannot be null");
        }
        if (index < 0 || count < 0 || index + count > unicode.length()) {
            throw new IndexOutOfBoundsException();
        }

        String substring = unicode.substring(index, index + count);

        // Java's IDN.toASCII() behavior:
        // - Always allows unassigned code points (Java doesn't have a flag for this)
        // - STD3 rules are not enforced by the Java IDN class
        // We use IDN.DEFAULT which is the standard behavior

        // For compatibility with .NET behavior regarding UseStd3AsciiRules,
        // we validate the result if useStd3AsciiRules is true
        String result = IDN.toASCII(substring);

        if (useStd3AsciiRules && result != null) {
            validateStd3AsciiRules(result);
        }

        if (!allowUnassigned) {
            // Check for unassigned code points in the input
            // Unassigned code points are those not assigned in Unicode
            // Java IDN.toASCII allows them, but .NET can reject them
            // This is a best-effort validation
            validateNoUnassigned(substring);
        }

        return result;
    }

    // ---- GetUnicode methods ----

    /**
     * Converts an ASCII-compatible domain name (Punycode) to a Unicode domain name.
     *
     * @param ascii The ASCII-compatible encoding (ACE) domain name to convert.
     * @return The Unicode domain name.
     * @throws IllegalArgumentException if the domain name contains invalid characters.
     */
    public String getUnicode(String ascii) {
        return getUnicode(ascii, 0, ascii.length());
    }

    /**
     * Converts a substring of an ASCII-compatible domain name (Punycode) to a Unicode domain name.
     *
     * @param ascii The ASCII-compatible encoding (ACE) domain name to convert.
     * @param index The starting index of the substring to convert.
     * @return The Unicode domain name for the specified substring.
     * @throws IllegalArgumentException if the domain name contains invalid characters.
     * @throws IndexOutOfBoundsException if index is out of range.
     */
    public String getUnicode(String ascii, int index) {
        return getUnicode(ascii, index, ascii.length() - index);
    }

    /**
     * Converts a specified number of characters from an ASCII-compatible domain name (Punycode) to a Unicode domain name.
     *
     * @param ascii The ASCII-compatible encoding (ACE) domain name to convert.
     * @param index The starting index of the substring to convert.
     * @param count The number of characters to convert.
     * @return The Unicode domain name for the specified substring.
     * @throws IllegalArgumentException if the domain name contains invalid characters.
     * @throws IndexOutOfBoundsException if index or count is out of range.
     */
    public String getUnicode(String ascii, int index, int count) {
        if (ascii == null) {
            throw new IllegalArgumentException("ascii cannot be null");
        }
        if (index < 0 || count < 0 || index + count > ascii.length()) {
            throw new IndexOutOfBoundsException();
        }

        String substring = ascii.substring(index, index + count);

        // Java's IDN.toUnicode() converts Punycode to Unicode
        String result = IDN.toUnicode(substring);

        if (useStd3AsciiRules && result != null) {
            validateStd3AsciiRules(result);
        }

        return result;
    }

    // ---- Object overrides ----

    @Override
    public boolean equals(Object obj) {
        if (this == obj) return true;
        if (!(obj instanceof IdnMapping other)) return false;
        return this.allowUnassigned == other.allowUnassigned
                && this.useStd3AsciiRules == other.useStd3AsciiRules;
    }

    @Override
    public int hashCode() {
        return (allowUnassigned ? 1 : 0) * 31 + (useStd3AsciiRules ? 1 : 0);
    }

    // ---- Private helper methods ----

    /**
     * Validates that the string conforms to STD3 ASCII rules.
     * STD3 rules allow only letters, digits, and hyphens (LDH characters).
     *
     * @param s The string to validate.
     * @throws IllegalArgumentException if the string violates STD3 rules.
     */
    private void validateStd3AsciiRules(String s) {
        for (int i = 0; i < s.length(); i++) {
            char c = s.charAt(i);
            // STD3 allows only letters (a-z, A-Z), digits (0-9), and hyphens (-)
            // Underscores and other special characters are not allowed
            if (!((c >= 'a' && c <= 'z') ||
                    (c >= 'A' && c <= 'Z') ||
                    (c >= '0' && c <= '9') ||
                    c == '-')) {
                throw new IllegalArgumentException(
                        "The string contains invalid STD3 ASCII characters: " + s);
            }
        }
    }

    /**
     * Validates that the string does not contain unassigned Unicode code points.
     * This is a best-effort validation as Java's Character class may not have
     * the exact same notion of "unassigned" as .NET.
     *
     * @param s The string to validate.
     * @throws IllegalArgumentException if the string contains unassigned code points.
     */
    private void validateNoUnassigned(String s) {
        for (int i = 0; i < s.length(); i++) {
            char c = s.charAt(i);
            // Check if the character is unassigned in Unicode
            // Character.getType() returns Character.UNASSIGNED for unassigned code points
            if (Character.getType(c) == Character.UNASSIGNED) {
                throw new IllegalArgumentException(
                        "The string contains unassigned Unicode code points: " + s);
            }
            // Also check supplementary characters (4-byte UTF-16)
            if (Character.isHighSurrogate(c) && i + 1 < s.length()) {
                char low = s.charAt(i + 1);
                if (Character.isLowSurrogate(low)) {
                    int codePoint = Character.toCodePoint(c, low);
                    if (Character.getType(codePoint) == Character.UNASSIGNED) {
                        throw new IllegalArgumentException(
                                "The string contains unassigned Unicode code points: " + s);
                    }
                    i++; // Skip the low surrogate
                }
            }
        }
    }
}
