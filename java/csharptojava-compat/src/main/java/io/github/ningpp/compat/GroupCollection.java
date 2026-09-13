package io.github.ningpp.compat;

import java.util.regex.Matcher;

/**
 * Replacement for System.Text.RegularExpressions.GroupCollection.
 * <p>
 * IMPORTANT: This class captures a snapshot of the Matcher state at construction
 * time. The previous implementation stored the Matcher reference directly, which
 * caused all Match objects created in a loop (e.g. in Regex.matches) to share
 * the same Matcher and return stale/wrong indices after the Matcher advanced
 * to the next match or exhausted.
 * <p>
 * Named groups are also snapshotted at construction time by extracting group
 * names from the pattern string and calling {@code matcher.group(name)} for
 * each. This avoids holding a live Matcher reference that could become stale.
 */
public final class GroupCollection {
    private final Group[] groups;
    private final java.util.Map<String, Group> namedGroups;

    public GroupCollection(Matcher matcher, boolean success) {
        if (!success || matcher == null) {
            this.groups = new Group[] { Group.Empty };
            this.namedGroups = java.util.Collections.emptyMap();
            return;
        }
        int count = matcher.groupCount() + 1;
        this.groups = new Group[count];
        for (int i = 0; i < count; i++) {
            try {
                String val = matcher.group(i);
                if (val != null) {
                    int start = matcher.start(i);
                    this.groups[i] = new Group(matcher, true, start, val);
                } else {
                    this.groups[i] = Group.Empty;
                }
            } catch (Exception ex) {
                this.groups[i] = Group.Empty;
            }
        }
        // Snapshot named groups eagerly so we don't need to hold the Matcher reference.
        this.namedGroups = snapshotNamedGroups(matcher);
    }

    /**
     * Extracts named-group names from the pattern and snapshots their values.
     * Java's {@link Matcher#group(String)} supports named groups since Java 7,
     * and C#'s {@code (?<name>...)} syntax is identical to Java's {@code (?<name>...)}.
     */
    private static java.util.Map<String, Group> snapshotNamedGroups(Matcher matcher) {
        java.util.Set<String> names = extractNamedGroupNames(matcher.pattern().pattern());
        if (names.isEmpty()) {
            return java.util.Collections.emptyMap();
        }
        java.util.Map<String, Group> map = new java.util.HashMap<>();
        for (String name : names) {
            try {
                String val = matcher.group(name);
                if (val != null) {
                    int start = matcher.start(name);
                    map.put(name, new Group(matcher, true, start, val));
                } else {
                    map.put(name, Group.Empty);
                }
            } catch (Exception ex) {
                // Group exists in pattern but didn't participate in this match
                map.put(name, Group.Empty);
            }
        }
        return map;
    }

    /**
     * Extracts named group names from a regex pattern string.
     * Handles both Java/C# named group syntaxes:
     * <ul>
     *   <li>{@code (?<name>...)} — Java and C# named group</li>
     *   <li>{@code (?'name'...)} — C# single-quote named group</li>
     *   <li>{@code (?P<name>...)} — Python-style named group (accepted by Java)</li>
     * </ul>
     * This parser respects character classes {@code [...]} where parentheses
     * and angle brackets are literal, and escape sequences {@code \(} / {@code \\}.
     */
    private static java.util.Set<String> extractNamedGroupNames(String pattern) {
        java.util.Set<String> names = new java.util.LinkedHashSet<>();
        int len = pattern.length();
        int i = 0;
        boolean inCharClass = false;
        while (i < len) {
            char ch = pattern.charAt(i);
            // Track escape sequences — skip next char
            if (ch == '\\') {
                i += 2;
                continue;
            }
            // Track character class entry/exit
            if (!inCharClass) {
                if (ch == '[') {
                    inCharClass = true;
                    i++;
                    // A ']' immediately after '[' is literal
                    if (i < len && pattern.charAt(i) == ']') {
                        i++;
                    }
                    // A '^' as first char negates — skip it
                    if (i < len && pattern.charAt(i) == '^') {
                        i++;
                    }
                    // A ']' right after '[' or "[^" is literal
                    if (i < len && pattern.charAt(i) == ']') {
                        i++;
                    }
                    continue;
                }
            } else {
                if (ch == ']') {
                    inCharClass = false;
                }
                i++;
                continue;
            }
            // Look for (?<name>...) or (?'name'...) or (?P<name>...)
            if (ch == '(' && i + 1 < len && pattern.charAt(i + 1) == '?') {
                // Check for (?<name>...
                if (i + 2 < len && pattern.charAt(i + 2) == '<') {
                    int nameStart = i + 3;
                    int nameEnd = pattern.indexOf('>', nameStart);
                    if (nameEnd > nameStart) {
                        String name = pattern.substring(nameStart, nameEnd);
                        if (isValidGroupName(name)) {
                            names.add(name);
                        }
                    }
                    i = (nameEnd >= 0 ? nameEnd + 1 : i + 3);
                    continue;
                }
                // Check for (?'name'...
                if (i + 2 < len && pattern.charAt(i + 2) == '\'') {
                    int nameStart = i + 3;
                    int nameEnd = pattern.indexOf('\'', nameStart);
                    if (nameEnd > nameStart) {
                        String name = pattern.substring(nameStart, nameEnd);
                        if (isValidGroupName(name)) {
                            names.add(name);
                        }
                    }
                    i = (nameEnd >= 0 ? nameEnd + 1 : i + 3);
                    continue;
                }
                // Check for (?P<name>... (Python-style, accepted by Java)
                if (i + 3 < len && pattern.charAt(i + 2) == 'P' && pattern.charAt(i + 3) == '<') {
                    int nameStart = i + 4;
                    int nameEnd = pattern.indexOf('>', nameStart);
                    if (nameEnd > nameStart) {
                        String name = pattern.substring(nameStart, nameEnd);
                        if (isValidGroupName(name)) {
                            names.add(name);
                        }
                    }
                    i = (nameEnd >= 0 ? nameEnd + 1 : i + 4);
                    continue;
                }
            }
            i++;
        }
        return names;
    }

    private static boolean isValidGroupName(String name) {
        if (name.isEmpty()) {
            return false;
        }
        // Java/C# named groups: letter followed by letters/digits/underscores
        // Also allow digits-only names (Java supports numeric group names in some versions)
        for (int i = 0; i < name.length(); i++) {
            char c = name.charAt(i);
            if (!(Character.isLetterOrDigit(c) || c == '_')) {
                return false;
            }
        }
        return true;
    }

    public Group get(String name) {
        if (name == null) {
            return Group.Empty;
        }
        if (namedGroups != null) {
            Group g = namedGroups.get(name);
            if (g != null) {
                return g;
            }
        }
        return Group.Empty;
    }

    public Group get(int index) {
        if (index < 0 || index >= groups.length) return Group.Empty;
        Group g = groups[index];
        return g != null ? g : Group.Empty;
    }

    public Group getItem(int index) {
        return get(index);
    }

    public int getCount() {
        return groups.length;
    }
}
