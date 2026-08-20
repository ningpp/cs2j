package io.github.ningpp.compat;

/**
 * Compat for System.IO.SearchOption.
 */
public enum SearchOption {
    TopDirectoryOnly(0),
    AllDirectories(1);

    private final int value;

    SearchOption(int value) {
        this.value = value;
    }

    public int getValue() {
        return value;
    }

    public static SearchOption fromValue(int value) {
        for (SearchOption option : values()) {
            if (option.value == value) {
                return option;
            }
        }
        return TopDirectoryOnly;
    }
}
