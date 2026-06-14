package io.github.ningpp.compat;

import java.util.ArrayList;
import java.util.Iterator;

/** Replacement for System.Text.RegularExpressions.MatchCollection. */
public final class MatchCollection implements Iterable<Match> {
    private final ArrayList<Match> matches = new ArrayList<>();

    public void add(Match match) {
        matches.add(match);
    }

    public int getCount() {
        return matches.size();
    }

    public int getLength() {
        return matches.size();
    }

    public Match get(int index) {
        return matches.get(index);
    }

    public Iterator<Match> iterator() {
        return matches.iterator();
    }

    public IEnumerator enumerator() {
        return new IEnumerator(matches);
    }

    public static class IEnumerator {
        private final ArrayList<Match> matches;
        private int index = -1;

        IEnumerator(ArrayList<Match> matches) {
            this.matches = matches;
        }

        public boolean moveNext() {
            index++;
            return index < matches.size();
        }

        public Match getCurrent() {
            return matches.get(index);
        }

        public void reset() {
            index = -1;
        }
    }
}
