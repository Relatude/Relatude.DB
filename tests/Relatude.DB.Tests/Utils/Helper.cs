using Tests.Utils;
using Relatude.DB.Datamodels;
using Relatude.DB.DataStores;
using Relatude.DB.IO;
using Relatude.DB.Nodes;
namespace Relatude.DB.Utils {
    public static class Helper {
        static TextGenerator _generator = new();
        public static Datamodel GetDatamodel() {
            var dm = new Datamodel();
            dm.Add<Article>();
            dm.Add<Article2>();
            dm.Add<User>();
            dm.Add<Group>();
            return dm;
        }
        public static NodeStore Open(string? baseFolder = null, IIOProvider? io = null, bool deleteOld = true) {
            var datamodel = GetDatamodel();
            if (baseFolder != null) {
                if (deleteOld && Directory.Exists(baseFolder)) Directory.Delete(baseFolder, true);
                io = new IOProviderDisk(baseFolder);
            }

            SettingsLocal settings = new() {
                NodeCacheSizeGb = 0.5,
                PersistedValueIndexEngine = PersistedValueIndexEngine.Native,
                UsePersistedValueIndexesByDefault = true,
            };

            var storeData = DataStoreLocal.Open(datamodel, settings, io);
            var store = new NodeStore(storeData);
            return store;
        }
        public static List<Article> GenerateArticles(int count, bool fromWikipedia = false) {
            var list = new List<Article>();
            var random = new Random(100); // always the same
            if (fromWikipedia) {
                var reader = new WikipediaReader("C:\\WAF_Sources\\wikipedia\\wiki-articles.json");
                for (int i = 1; i < count + 1; i++) {
                    reader.ReadNext(out var w);
                    var a = new Article();
                    a.Id = i;
                    a.IntegerNum = random.Next(10);
                    a.DoubleNum = random.NextDouble() * 10;
                    a.Id2 = (int)i;
                    a.Name = "Test " + i;
                    a.Body = _generator.GenerateText(10000);
                    a.Size = (Sizes)(i % 3);
                    list.Add(a);
                }
            } else {
                for (int i = 1; i < count + 1; i++) {
                    var a = new Article();
                    a.Id = i;
                    a.IntegerNum = random.Next(10);
                    a.DoubleNum = random.NextDouble() * 10;
                    a.Id2 = (int)i;
                    a.Name = "Test " + i;
                    a.Body = _generator.GenerateText(10000);
                    a.Size = (Sizes)(i % 3);
                    list.Add(a);
                }
            }
            return list;
        }
    }
}
