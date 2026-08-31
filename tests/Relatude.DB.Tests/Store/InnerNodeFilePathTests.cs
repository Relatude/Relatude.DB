using Relatude.DB.Common;
using Relatude.DB.Datamodels;
using Relatude.DB.DataStores;
using Relatude.DB.Nodes;

namespace Relatude.Store;

[Node]
public class FileArticle {
    [PublicIdProperty]
    public Guid Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public FileValue File { get; set; } = FileValue.Empty;
    [EmbeddedMapProperty(KeyProperty = nameof(FileParagraph.Code))]
    public EmbeddedMap<string, FileParagraph> Paragraphs { get; set; } = [];
}
public class FileParagraph {
    public Guid Id { get; set; }
    public string Code { get; set; } = string.Empty;
    public FileValue File { get; set; } = FileValue.Empty;
}

// A file property is addressed by the property path it sits at, and that is the whole of what the
// admin UI hands to its media route: the form sends the path back, the route reads the file from it.
// A file on an inner node of an embedded property is reached the same way, through the property the
// inner node hangs off - which is what these tests pin down, because a preview built on a path the
// store cannot resolve would show nothing.
[TestClass]
public class InnerNodeFilePathTests {

    static NodeStore open() {
        var dm = new Datamodel();
        dm.Add<FileArticle>();
        dm.Add<FileParagraph>();
        return new NodeStore(DataStoreLocal.Open(dm, new SettingsLocal(), null));
    }

    static readonly byte[] someBytes = [1, 2, 3, 4, 5, 6, 7, 8];

    [TestMethod]
    public async Task FileOnTheNodeIsReadBackFromItsPath() {
        using var store = open();
        var id = Guid.NewGuid();
        store.Insert(new FileArticle { Id = id, Title = "A" });
        var property = store.Datastore.Datamodel.NodeTypesByFullName[typeof(FileArticle).FullName!].AllPropertiesByName["File"];

        var path = new PropertyPath(id, property.Id);
        await store.Datastore.FileUploadAsync(path, new MemoryStream(someBytes), "picture.png");

        Assert.IsTrue(store.Datastore.TryGetValue<FileValue>(path, out var file));
        Assert.AreEqual("picture.png", file.Name);
        Assert.AreEqual(FileType.Image, file.FileType);
        Assert.AreEqual(someBytes.Length, file.Size);
    }

    [TestMethod]
    public async Task FileOnAnInnerNodeIsReadBackFromItsPath() {
        using var store = open();
        var id = Guid.NewGuid();
        var paragraphId = Guid.NewGuid();
        var article = new FileArticle { Id = id, Title = "A" };
        article.Paragraphs.Add(new FileParagraph { Id = paragraphId, Code = "p1" });
        store.Insert(article);
        var dm = store.Datastore.Datamodel;
        var paragraphs = dm.NodeTypesByFullName[typeof(FileArticle).FullName!].AllPropertiesByName["Paragraphs"];
        var innerFile = dm.NodeTypesByFullName[typeof(FileParagraph).FullName!].AllPropertiesByName["File"];

        // exactly how the node form builds the path of a file inside an embedded property
        var path = new PropertyPath(id, paragraphs.Id).CreatePathToInnerNode(paragraphId).CreatePropertyPath(innerFile.Id);
        await store.Datastore.FileUploadAsync(path, new MemoryStream(someBytes), "clip.mp4");

        Assert.IsTrue(store.Datastore.TryGetValue<FileValue>(path, out var file));
        Assert.AreEqual("clip.mp4", file.Name);
        Assert.AreEqual(FileType.Video, file.FileType);
        // and the path survives the round trip through the url, which is how it reaches the route
        Assert.IsTrue(PropertyPath.TryParse(path.ToUrlString(), out var parsed));
        Assert.AreEqual(path, parsed);
        Assert.IsTrue(store.Datastore.TryGetValue<FileValue>(parsed, out var again));
        Assert.AreEqual("clip.mp4", again.Name);
    }
}
