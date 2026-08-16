package io.github.ningpp.compat;

import org.junit.jupiter.api.Test;

import java.io.FileNotFoundException;

class XDocumentMissingFileTest {

    @Test
    void load_MissingFile_ThrowsFileNotFoundException() {
        String missingFile = System.getProperty("java.io.tmpdir")
                + "cs2j-missing-" + System.nanoTime() + ".dgml";
        Microsoft.VisualStudio.TestTools.UnitTesting.Assert.throwsException(
                FileNotFoundException.class,
                () -> XDocument.load(missingFile));
    }
}
