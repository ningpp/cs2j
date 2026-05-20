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
}
