using Relatude.DB.Common;
using Relatude.DB.FileConversion;
using Relatude.DB.GraphQL;
using Relatude.DB.Nodes;
using Relatude.DB.Web;
using Relatude.Utils;
using static Relatude.GraphQL.GraphQLTestHelper;

namespace Relatude.GraphQL;

/// <summary>FileInfo.url: the store's url for a file, or for an image made from it, as db.GetUrl gives them.</summary>
[TestClass]
public class GraphQLFileUrlTests {

    static async Task<(PropertyPath Path, string Id)> upload(NodeStore store, int number, string fileName) {
        var article = store.Query<Article>().Where(a => a.Id == number).Execute().First();
        var data = new byte[256];
        new Random(number).NextBytes(data);
        await store.FileUploadAsync(article, a => a.File, data, fileName);
        var path = new PropertyPath(article.PId, store.Datastore.Datamodel.NodeTypes.Values.First(t => t.CodeName == nameof(Article)).AllProperties.Values.First(p => p.CodeName == nameof(Article.File)).Id);
        return (path, article.PId.ToString());
    }

    [TestMethod]
    public void TheSchemaGivesFileInfoAUrlWithImageArguments() {
        var (store, gql, _) = Open();
        try {
            var sdl = gql.ToSDL().Replace("\r\n", "\n");
            StringAssert.Contains(sdl, "url(width: Int, height: Int, crop: ImageCropMode, format: ImageFormat, quality: Int, absolute: Boolean = false): String!");
            StringAssert.Contains(sdl, "enum ImageFormat");
            StringAssert.Contains(sdl, "enum ImageCropMode");
        } finally { store.Dispose(); }
    }

    [TestMethod]
    public async Task UrlIsTheStoresUrlForTheFileAndForResizedImages() {
        var (store, gql, _) = Open();
        try {
            var (path, id) = await upload(store, 3, "photo.jpg");
            var data = RequireData(gql.Execute($$"""
                {
                  article(id: "{{id}}") {
                    file {
                      name
                      url
                      thumbnail: url(width: 200, height: 100, crop: Fit)
                      webp: url(width: 640, format: Webp, quality: 70)
                    }
                  }
                }
                """));
            Assert.AreEqual("photo.jpg", Get(data, "article", "file", "name"));
            var url = (string)Get(data, "article", "file", "url")!;
            Assert.AreEqual(store.Datastore.GetUrl(path, false), url);
            StringAssert.Contains(url, "photo.jpg");

            var thumbnail = (string)Get(data, "article", "file", "thumbnail")!;
            Assert.AreEqual(store.Datastore.GetUrl(path, new FileAdjustmentImage { Width = 200, Height = 100, CropMode = ImageCropMode.Fit }, false), thumbnail);
            Assert.AreNotEqual(url, thumbnail);

            var webp = (string)Get(data, "article", "file", "webp")!;
            Assert.AreEqual(store.Datastore.GetUrl(path, new FileAdjustmentImage { Width = 640, RequestedFormat = FileFormat.Webp, Quality = 70 }, false), webp);
            StringAssert.EndsWith(webp.Split('?')[0], ".webp");

            // the urls resolve back to the file and the adjustment asked for
            Assert.IsTrue(store.Datastore.TryParseUrl(url, out var plain));
            Assert.AreEqual(UrlTarget.Property, plain.Target);
            Assert.IsTrue(store.Datastore.TryParseUrl(webp, out var adjusted));
            Assert.AreEqual(UrlTarget.PropertyAdjusted, adjusted.Target);
            var adjustment = (FileAdjustmentImage)adjusted.Adjustment!;
            Assert.AreEqual(640, adjustment.Width);
            Assert.AreEqual(FileFormat.Webp, adjustment.RequestedFormat);
        } finally { store.Dispose(); }
    }

    [TestMethod]
    public async Task UrlTakesItsArgumentsFromVariables() {
        var (store, gql, _) = Open();
        try {
            var (path, id) = await upload(store, 4, "photo.png");
            var result = gql.Execute(new GraphQLRequest {
                Query = """
                    query Thumb($id: ID!, $w: Int, $f: ImageFormat, $c: ImageCropMode) {
                      article(id: $id) { file { url(width: $w, format: $f, crop: $c) } }
                    }
                    """,
                Variables = System.Text.Json.JsonSerializer.SerializeToElement(new { id, w = 120, f = "Avif", c = "Fill" }),
            });
            var data = RequireData(result);
            Assert.AreEqual(store.Datastore.GetUrl(path, new FileAdjustmentImage { Width = 120, RequestedFormat = FileFormat.Avif, CropMode = ImageCropMode.Fill }, false),
                Get(data, "article", "file", "url"));
        } finally { store.Dispose(); }
    }

    [TestMethod]
    public async Task AbsoluteAddsTheOriginTheRequestCameInOn() {
        var (store, gql, _) = Open();
        try {
            var (path, id) = await upload(store, 5, "photo.jpg");
            var query = $$"""{ article(id: "{{id}}") { file { relative: url absolute: url(absolute: true) } } }""";
            var relative = store.Datastore.GetUrl(path, false);
            Assert.IsTrue(relative.StartsWith('/'), relative);

            var data = RequireData(gql.Execute(new GraphQLRequest { Query = query, Origin = "https://api.example.com/app/" }));
            Assert.AreEqual(relative, Get(data, "article", "file", "relative"));
            Assert.AreEqual("https://api.example.com/app" + relative, Get(data, "article", "file", "absolute"));

            // outside HTTP there is no origin, so the url stays as the store gives it
            var plain = RequireData(gql.Execute(query));
            Assert.AreEqual(relative, Get(plain, "article", "file", "absolute"));
        } finally { store.Dispose(); }
    }

    [TestMethod]
    public void TheOriginIsNeverReadFromTheRequestBody() {
        var request = System.Text.Json.JsonSerializer.Deserialize<GraphQLRequest>("""{ "query": "{ __typename }", "origin": "https://evil.example" }""",
            new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        Assert.IsNull(request.Origin);
    }

    [TestMethod]
    public async Task ImageArgumentsAreIgnoredForFilesThatMakeNoImages() {
        var (store, gql, _) = Open();
        try {
            var (path, id) = await upload(store, 6, "report.pdf");
            var data = RequireData(gql.Execute($$"""{ article(id: "{{id}}") { file { url thumbnail: url(width: 200) } } }"""));
            var plain = store.Datastore.GetUrl(path, false);
            Assert.AreEqual(plain, Get(data, "article", "file", "url"));
            Assert.AreEqual(plain, Get(data, "article", "file", "thumbnail"));
        } finally { store.Dispose(); }
    }

    [TestMethod]
    public void ANodeWithoutAFileHasNoFileAndNoUrl() {
        var (store, gql, _) = Open();
        try {
            var data = RequireData(gql.Execute($$"""{ article(id: "{{PublicId(store, 7)}}") { name file { url } } }"""));
            Assert.AreEqual("Article 07", Get(data, "article", "name"));
            Assert.IsNull(Get(data, "article", "file"));
        } finally { store.Dispose(); }
    }
}
