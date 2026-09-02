package io.github.ningpp.compat;

import java.util.regex.Matcher;

/**
 * Replacement for System.Text.RegularExpressions.Match.
 * <p>
 * The index is captured eagerly at construction time (via GroupCollection's
 * snapshot) so that it remains correct even after the underlying Matcher
 * advances to a subsequent match.
 */
public final class Match {
    public static final Match Empty = new Match(null, false);

    public final boolean Success;
    public final GroupCollection Groups;
    private final int index;

    public Match(Matcher matcher, boolean success) {
        this.Success = success;
        this.Groups = new GroupCollection(matcher, success);
        // Capture index eagerly from the GroupCollection snapshot
        this.index = this.Groups.get(0) != null ? this.Groups.get(0).getIndex() : -1;
    }

    public boolean getSuccess() {
        return Success;
    }

    public GroupCollection getGroups() {
        return Groups;
    }

    public int getIndex() {
        return index;
    }

    public String getValue() {
        if (Groups != null && Groups.get(0) != null) {
            return Groups.get(0).getValue();
        }
        return "";
    }
}
