package io.github.ningpp.compat;

/** Replacement for System.IO.FileNotFoundException with C#-compatible two-string constructor. */
public class FileNotFoundException extends java.io.FileNotFoundException {
    private String fileName;

    public FileNotFoundException() {
        super();
    }

    public FileNotFoundException(String message) {
        super(message);
    }

    public FileNotFoundException(String message, Throwable cause) {
        super(message);
        if (cause != null) {
            initCause(cause);
        }
    }

    /** Mirrors C# FileNotFoundException(string message, string fileName). */
    public FileNotFoundException(String message, String fileName) {
        super(message != null ? message : fileName);
        this.fileName = fileName;
    }

    /** Mirrors C# FileNotFoundException(string message, string fileName, Exception innerException). */
    public FileNotFoundException(String message, String fileName, Throwable cause) {
        super(message != null ? message : fileName);
        this.fileName = fileName;
        if (cause != null) {
            initCause(cause);
        }
    }

    public String getFileName() {
        return fileName;
    }

    public void setFileName(String fileName) {
        this.fileName = fileName;
    }
}
