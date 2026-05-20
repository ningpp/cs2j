package io.github.ningpp.compat;

import java.util.regex.Matcher;

public final class GroupCollection {
    private final Matcher matcher;
    private final boolean success;

    public GroupCollection(Matcher matcher, boolean success) {
        this.matcher = matcher;
        this.success = success;
    }

    public Group get(String name) {
        if (!success || matcher == null) return Group.Empty;
        try { return new Group(matcher.group(name)); }
        catch (Exception ex) { return Group.Empty; }
    }

    public Group get(int index) {
        if (!success || matcher == null) return Group.Empty;
        try { return new Group(matcher.group(index)); }
        catch (Exception ex) { return Group.Empty; }
    }

    public Group getItem(int index) {
        return get(index);
    }
}
