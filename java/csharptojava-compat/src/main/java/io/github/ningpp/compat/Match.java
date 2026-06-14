package io.github.ningpp.compat;

import java.util.regex.Matcher;

public final class Match {
    public static final Match Empty = new Match(null, false);

    public final boolean Success;
    public final GroupCollection Groups;

    public Match(Matcher matcher, boolean success) {
        this.Success = success;
        this.Groups = new GroupCollection(matcher, success);
    }

    public boolean getSuccess() {
        return Success;
    }

    public GroupCollection getGroups() {
        return Groups;
    }

    public int getIndex() {
        if (Groups != null && Groups.get(0) != null) {
            return Groups.get(0).getIndex();
        }
        return -1;
    }

    public String getValue() {
        if (Groups != null && Groups.get(0) != null) {
            return Groups.get(0).getValue();
        }
        return "";
    }
}
