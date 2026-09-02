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
 */
public final class GroupCollection {
    private final Group[] groups;

    public GroupCollection(Matcher matcher, boolean success) {
        if (!success || matcher == null) {
            this.groups = new Group[] { Group.Empty };
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
    }

    public Group get(String name) {
        if (groups.length == 1 && groups[0] == Group.Empty) return Group.Empty;
        try {
            // Java regex doesn't support named groups the same way C# does;
            // delegate to numeric lookup is not possible by name in Matcher.
            return Group.Empty;
        } catch (Exception ex) { return Group.Empty; }
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
