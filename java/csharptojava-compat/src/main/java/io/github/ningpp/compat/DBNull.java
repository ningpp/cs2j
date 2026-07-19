package io.github.ningpp.compat;

/**
 * Compat stub for System.DBNull.
 */
public final class DBNull {

    public static final DBNull VALUE = new DBNull();
    public static final DBNull Value = VALUE;

    private DBNull() {
    }

    @Override
    public String toString() {
        return "";
    }

    @Override
    public boolean equals(Object obj) {
        return obj instanceof DBNull;
    }

    @Override
    public int hashCode() {
        return 0;
    }
}
