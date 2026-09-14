package io.github.ningpp.compat;

/**
 * Replacement for System.IO.FileNotFoundException.
 * <p>
 * Intentionally does NOT extend java.io.FileNotFoundException, because C# exceptions
 * and Java exceptions have different hierarchy and semantics. In C#, all exceptions are
 * unchecked; FileNotFoundException is part of the IOException family. In Java,
 * java.io.FileNotFoundException is a checked exception and is tied to real I/O operations.
 * A compat class for the C# type should behave as an unchecked exception.
 */
public class FileNotFoundException extends RuntimeException {
    private String fileName;
    private String fusionLog;

    public FileNotFoundException() {
        super();
    }

    public FileNotFoundException(String message) {
        super(message);
    }

    public FileNotFoundException(String message, Throwable cause) {
        super(message, cause);
    }

    /** Mirrors C# FileNotFoundException(string message, string fileName). */
    public FileNotFoundException(String message, String fileName) {
        super(message != null ? message : fileName);
        this.fileName = fileName;
    }

    /** Mirrors C# FileNotFoundException(string message, string fileName, Exception innerException). */
    public FileNotFoundException(String message, String fileName, Throwable cause) {
        super(message != null ? message : fileName, cause);
        this.fileName = fileName;
    }

    public String getFileName() {
        return fileName;
    }

    public void setFileName(String fileName) {
        this.fileName = fileName;
    }

    public String getFusionLog() {
        return fusionLog;
    }

    public void setFusionLog(String fusionLog) {
        this.fusionLog = fusionLog;
    }
}
