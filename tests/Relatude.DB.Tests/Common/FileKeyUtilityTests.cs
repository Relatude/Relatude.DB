using System.Globalization;
using Relatude.DB.IO;

namespace Relatude.Common;

/// <summary>
/// The storage naming rules that persisted data depends on: a name is how the data is found again
/// after a restart, so any drift in these rules silently orphans a store's files — the engine then
/// looks at an empty folder, believes the index is new, and rebuilds it from the log.
/// </summary>
[TestClass]
public class FileKeyUtilityTests {

    // a realistic word index id: property guid, culture code, sub key (see IndexFactory.getUniqueKey)
    const string _indexId = "B835577E-84A2-4FA3-A850-44AB2112E6CF_it-IT_words";

    [TestMethod]
    public void LuceneIndexFolderKey_IsLowerCaseAndAValidFileKey() {
        var key = FileKeyUtility.IndexEngine_LuceneIndexFolderKey(_indexId);
        Assert.AreEqual("b835577e-84a2-4fa3-a850-44ab2112e6cf_it-it_words", key);
        Assert.IsTrue(FileKeyUtility.IsFileKeyValid(key), "the folder name must be a legal file key: " + key);
    }

    [TestMethod]
    public void LuceneIndexFolderKey_DoesNotDependOnTheCurrentCulture() {
        // Turkish lowercases 'I' to 'Ä±', so a culture sensitive ToLower() would name the folder
        // differently depending on the locale the process happens to run under, and the index would
        // look fresh (and be rebuilt) the next time it is opened elsewhere.
        var invariant = FileKeyUtility.IndexEngine_LuceneIndexFolderKey(_indexId);
        var previous = CultureInfo.CurrentCulture;
        try {
            CultureInfo.CurrentCulture = new CultureInfo("tr-TR");
            Assert.AreEqual(invariant, FileKeyUtility.IndexEngine_LuceneIndexFolderKey(_indexId));
        } finally {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [TestMethod]
    public void LuceneIndexFolderKey_KeepsDistinctIndexesApart() {
        var a = FileKeyUtility.IndexEngine_LuceneIndexFolderKey("11111111-1111-1111-1111-111111111111_words");
        var b = FileKeyUtility.IndexEngine_LuceneIndexFolderKey("22222222-2222-2222-2222-222222222222_words");
        var cultureA = FileKeyUtility.IndexEngine_LuceneIndexFolderKey("11111111-1111-1111-1111-111111111111_nb-NO_words");
        Assert.AreNotEqual(a, b, "two properties must not share a folder");
        Assert.AreNotEqual(a, cultureA, "two cultures of one property must not share a folder");
    }

    [TestMethod]
    public void TempFileKey_ReplacesABinaryExtensionAndAppendsOtherwise() {
        // replacing (not appending to) ".bin" keeps the temp file out of the index.*.bin search
        // pattern and keeps the key within the maximum file name length
        Assert.AreEqual("index.abc.tmp", FileKeyUtility.TempFileName("index.abc.bin"));
        Assert.AreEqual("engine.walid.tmp", FileKeyUtility.TempFileName("engine.walid"));
    }
}
