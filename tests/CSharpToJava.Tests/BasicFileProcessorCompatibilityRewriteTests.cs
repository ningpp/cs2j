using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class BasicFileProcessorCompatibilityRewriteTests
{
    [Fact]
    public void ApplyCompatibilityRewritesForTesting_BasicFileProcessor_RewritesSystemIoPatterns()
    {
        const string generated = """
            package Microsoft.Msagl.UnitTests;

            import java.io.*;
            import java.nio.file.Path;

            public abstract class BasicFileProcessor {
                public void processFiles(String strPathFileSpec) {
                    // strPathFileSpec may be with or without directory or wildcards:
                    //   x.txt
                    //   Test\Data\x.txt
                    //   Test\Data\Rand*.txt
                    // Break out the directory and filename specification.
                    String strFileSpec = Paths.getFileName(strPathFileSpec);
                    String strDirectory = Paths.getDirectoryName(strPathFileSpec);
                    if (StringHelper.isNullOrEmpty(strDirectory)) {
                    strDirectory = ".";
                    }
                    strDirectory = Paths.getFullPath(strDirectory);
                    processFiles(strDirectory, strFileSpec);
                }

                private void processFiles(String strDirectory, String strFileSpec) {
                    var di = new Path(strDirectory);
                    FileSystemInfo[] fis = di.getFileSystemInfos(strFileSpec);
                    for (FileSystemInfo fi : fis) {
                    this.WriteLineFunc.accept(String.format("( {0} )", fi.getFullName()));
                    processFile(fi.getFullName());
                    }
                    if (getRecursive()) {
                    for (String strSubdir : Files.getDirectories(strDirectory)) {
                    processFiles(Paths.getFullPath(strSubdir), strFileSpec);
                    }
                    }
                }

                private void processFile(String fileName) {
                    try {
                    this.loadAndProcessFile(fileName);
                    } catch (Exception ex) {
                    var innerEx = ex.getInnerException() != null ? ex.getInnerException() : ex;
                    }
                }
            }
            """;

        var output = ProjectConversionPipeline.ApplyCompatibilityRewritesForTesting("BasicFileProcessor.java", generated).Replace("\r\n", "\n");

        Assert.Contains("import java.nio.file.Paths;", output);
        Assert.Contains("Path _path = Paths.get(strPathFileSpec);", output);
        Assert.Contains("var di = new File(strDirectory);", output);
        Assert.Contains("File[] fis = di.listFiles", output);
        Assert.Contains("String.format(\"( %s )\", fi.getAbsolutePath())", output);
        Assert.Contains("ex.getCause() != null ? ex.getCause() : ex", output);
        Assert.DoesNotContain("new Path(strDirectory)", output);
        Assert.DoesNotContain("FileSystemInfo[]", output);
        Assert.DoesNotContain("getInnerException()", output);
    }
}