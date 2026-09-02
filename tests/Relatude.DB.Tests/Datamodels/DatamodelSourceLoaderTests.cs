using Relatude.DB.Datamodels;
using Relatude.DB.DataStores;
using Relatude.DB.IO;
using Relatude.DB.Nodes;

namespace Relatude.SourceLoaderModels {
    // small dedicated model used to produce JSON datamodel files:
    [Node]
    public class SlAuthor {
        [PublicIdProperty]
        public Guid Id { get; set; }
        [StringProperty]
        public string Name { get; set; } = "";
        public SlBooksRel.Wrote Books { get; set; } = new();
    }
    [Node]
    public class SlBook {
        [PublicIdProperty]
        public Guid Id { get; set; }
        [StringProperty]
        public string Title { get; set; } = "";
        public SlBooksRel.WrittenBy Author { get; set; } = new();
    }
    public class SlBooksRel : OneToMany<SlAuthor, SlBook> {
        public class Wrote : Many { }
        public class WrittenBy : One { }
    }
}

namespace Relatude.SourceLoaderModels.JsonGen {
    // attributed twin used only to produce a JSON model file for the plain POCO below:
    [Node]
    public class SlReview {
        [PublicIdProperty]
        public Guid Id { get; set; }
        [StringProperty(Indexed = true)]
        public string Author { get; set; } = "";
        [IntegerProperty(Indexed = true)]
        public int Rating { get; set; }
    }
}
namespace Relatude.SourceLoaderModels.JsonPoco {
    // the class backing a JSON-defined model carries no Relatude attributes - the JSON file is
    // the model definition, the class only has to match it by full name and property names:
    public class SlReview {
        public Guid Id { get; set; }
        public string Author { get; set; } = "";
        public int Rating { get; set; }
    }
}

namespace Relatude.Datamodels {
    using Relatude.SourceLoaderModels;

    [TestClass]
    public class DatamodelSourceLoaderTests {

        string _root = "";
        [TestInitialize]
        public void Setup() {
            _root = Path.Combine(Path.GetTempPath(), "RelatudeDBTests", "SourceLoader_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }
        [TestCleanup]
        public void Cleanup() {
            try { Directory.Delete(_root, true); } catch { }
        }

        static string buildJsonModel() {
            var dm = new Datamodel();
            dm.Add<SlAuthor>();
            dm.Add<SlBook>();
            dm.Add<SlBooksRel>();
            return DatamodelJson.Serialize(dm);
        }
        static DatamodelSource jsonSource(string? filepath, string? reference = null) => new() {
            Id = new Guid("11111111-0000-0000-0000-000000000001"),
            Name = "JsonModel",
            Type = DatamodelSourceType.JsonFile,
            Filepath = filepath,
            Reference = reference,
        };

        [TestMethod]
        public void JsonFileSource_LoadsTagsAndStoresFilename() {
            var folder = Path.Combine(_root, "models");
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "library.json"), buildJsonModel());
            var dm = new Datamodel();
            var source = jsonSource("models/library.json");
            DatamodelSourceLoader.Load(dm, source, _root);
            dm.EnsureInitalization();

            Assert.IsTrue(dm.NodeTypesByFullName.ContainsKey("Relatude.SourceLoaderModels.SlAuthor"));
            Assert.IsTrue(dm.NodeTypesByFullName.ContainsKey("Relatude.SourceLoaderModels.SlBook"));
            Assert.AreEqual(1, dm.Sources.Count, "The inner Sources of the JSON file must be dropped; only the configured source is kept. ");
            Assert.AreEqual(source.Id, dm.Sources[0].Id);
            foreach (var t in dm.NodeTypes.Values.Where(t => t.Id != NodeConstants.BaseNodeTypeId)) {
                Assert.AreEqual(source.Id, t.DatamodelSourceId, t.FullName + " is not tagged with the source id. ");
                Assert.AreEqual("library.json", t.DatamodelSourceFilename, t.FullName + " does not carry the file name. ");
            }
            foreach (var r in dm.Relations.Values) {
                Assert.AreEqual(source.Id, r.DatamodelSourceId, r.CodeName + " is not tagged with the source id. ");
                Assert.AreEqual("library.json", r.DatamodelSourceFilename, r.CodeName + " does not carry the file name. ");
            }
        }

        [TestMethod]
        public void JsonFileSource_EmptyFilepathUsesDefaultFolder() {
            var folder = Path.Combine(_root, DatamodelSourceLoader.DefaultJsonFolder);
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "library.json"), buildJsonModel());
            var dm = new Datamodel();
            DatamodelSourceLoader.Load(dm, jsonSource(null), _root);
            dm.EnsureInitalization();
            Assert.IsTrue(dm.NodeTypesByFullName.ContainsKey("Relatude.SourceLoaderModels.SlAuthor"));
            Assert.AreEqual("library.json", dm.NodeTypesByFullName["Relatude.SourceLoaderModels.SlAuthor"].DatamodelSourceFilename);
        }

        [TestMethod]
        public void JsonFileSource_SameModelTwice_FailsWithCollisionMessage() {
            var folder = Path.Combine(_root, "models");
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "library.json"), buildJsonModel());
            var dm = new Datamodel();
            DatamodelSourceLoader.Load(dm, jsonSource("models/library.json"), _root);
            var second = jsonSource("models/library.json");
            second.Id = new Guid("11111111-0000-0000-0000-000000000002");
            var ex = Assert.ThrowsException<Exception>(() => DatamodelSourceLoader.Load(dm, second, _root));
            StringAssert.Contains(ex.Message, "same id");
        }

        [TestMethod]
        public void Loader_RejectsEmptyIdCodeTypeAndDuplicateSourceIds() {
            var dm = new Datamodel();
            var noId = jsonSource("x.json");
            noId.Id = Guid.Empty;
            StringAssert.Contains(Assert.ThrowsException<Exception>(() => DatamodelSourceLoader.Load(dm, noId, _root)).Message, "no Id");
            var codeType = new DatamodelSource() { Id = Guid.NewGuid(), Type = DatamodelSourceType.Code };
            StringAssert.Contains(Assert.ThrowsException<Exception>(() => DatamodelSourceLoader.Load(dm, codeType, _root)).Message, "reserved");
            var folder = Path.Combine(_root, "models");
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "library.json"), buildJsonModel());
            DatamodelSourceLoader.Load(dm, jsonSource("models/library.json"), _root);
            StringAssert.Contains(Assert.ThrowsException<Exception>(() => DatamodelSourceLoader.Load(dm, jsonSource("models/library.json"), _root)).Message, "unique id");
        }

        [TestMethod]
        public void CodeAddedTypes_AreTaggedAsCodeSource() {
            var dm = new Datamodel();
            dm.Add<SlAuthor>();
            dm.Add<SlBook>();
            dm.Add<SlBooksRel>();
            dm.EnsureInitalization();
            Assert.IsTrue(dm.Sources.Any(s => s.Id == DatamodelSource.CodeSourceId && s.Type == DatamodelSourceType.Code),
                "Types added directly from code must register the synthetic Code source. ");
            foreach (var t in dm.NodeTypes.Values.Where(t => t.Id != NodeConstants.BaseNodeTypeId)) {
                Assert.AreEqual(DatamodelSource.CodeSourceId, t.DatamodelSourceId);
                Assert.IsNull(t.DatamodelSourceFilename);
            }
            foreach (var r in dm.Relations.Values) Assert.AreEqual(DatamodelSource.CodeSourceId, r.DatamodelSourceId);
        }

        const string csPersonFile = """
            using Relatude.DB.Nodes;
            namespace My.CsModels;
            [Node]
            public class CsPerson {
                [PublicIdProperty]
                public Guid Id { get; set; }
                [StringProperty]
                public string Name { get; set; } = "";
                public CsEmploymentRel.EmployedBy Employer { get; set; } = new();
            }
            """;
        const string csCompanyFile = """
            using Relatude.DB.Nodes;
            namespace My.CsModels;
            [Node]
            public class CsCompany {
                [PublicIdProperty]
                public Guid Id { get; set; }
                [StringProperty]
                public string CompanyName { get; set; } = "";
                public CsEmploymentRel.Employees Staff { get; set; } = new();
            }
            public class CsEmploymentRel : OneToMany<CsCompany, CsPerson> {
                public class Employees : Many { }
                public class EmployedBy : One { }
            }
            """;
        DatamodelSource csharpSource(string? filepath) => new() {
            Id = new Guid("22222222-0000-0000-0000-000000000001"),
            Name = "CsModel",
            Type = DatamodelSourceType.CSharpCodeFile,
            Filepath = filepath,
        };
        string writeCsFiles() {
            var folder = Path.Combine(_root, DatamodelSourceLoader.DefaultCSharpFolder);
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "CsPerson.cs"), csPersonFile);
            File.WriteAllText(Path.Combine(folder, "CsCompany.cs"), csCompanyFile);
            return folder;
        }

        [TestMethod]
        public void CSharpFileSource_CompilesTagsAndStoresFilenamePerFile() {
            writeCsFiles();
            var dm = new Datamodel();
            var source = csharpSource(null); // empty Filepath: the default folder
            DatamodelSourceLoader.Load(dm, source, _root);
            dm.EnsureInitalization();

            var person = dm.NodeTypesByFullName["My.CsModels.CsPerson"];
            var company = dm.NodeTypesByFullName["My.CsModels.CsCompany"];
            Assert.AreEqual(source.Id, person.DatamodelSourceId);
            Assert.AreEqual(source.Id, company.DatamodelSourceId);
            Assert.AreEqual("CsPerson.cs", person.DatamodelSourceFilename, "Each type must carry the file it is declared in. ");
            Assert.AreEqual("CsCompany.cs", company.DatamodelSourceFilename);
            var relation = dm.Relations.Values.Single(r => r.CodeName == "CsEmploymentRel");
            Assert.AreEqual(source.Id, relation.DatamodelSourceId);
            Assert.AreEqual("CsCompany.cs", relation.DatamodelSourceFilename);
            Assert.AreEqual(1, dm.Sources.Count);
        }

        [TestMethod]
        public void CSharpFileSource_SameContentReusesLoadedAssembly() {
            writeCsFiles();
            var dm1 = new Datamodel();
            DatamodelSourceLoader.Load(dm1, csharpSource(null), _root);
            var dm2 = new Datamodel();
            DatamodelSourceLoader.Load(dm2, csharpSource(null), _root);
            var t1 = dm1.NodeTypes.Values.First(t => t.CodeName == "CsPerson");
            // the CLR types must be identical instances, or compiled mappers would bind to another copy:
            Assert.AreSame(dm1.Assemblies.Single(a => a.GetName().Name!.StartsWith("RelatudeModel.")),
                           dm2.Assemblies.Single(a => a.GetName().Name!.StartsWith("RelatudeModel.")));
        }

        [TestMethod]
        public void CSharpFileSource_CompileErrorNamesFileAndLine() {
            var folder = Path.Combine(_root, DatamodelSourceLoader.DefaultCSharpFolder);
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "Broken.cs"), "namespace X;\npublic class Broken { this does not compile }\n");
            var dm = new Datamodel();
            var ex = Assert.ThrowsException<Exception>(() => DatamodelSourceLoader.Load(dm, csharpSource(null), _root));
            StringAssert.Contains(ex.Message, "Broken.cs");
            StringAssert.Contains(ex.Message, "(2)");
        }

        [TestMethod]
        public void CSharpFileSource_MissingPathNamesResolvedPath() {
            var dm = new Datamodel();
            var ex = Assert.ThrowsException<Exception>(() => DatamodelSourceLoader.Load(dm, csharpSource("does/not/exist"), _root));
            StringAssert.Contains(ex.Message, "does not exist");
        }

        [TestMethod]
        public void JsonFileSource_ModelForPlainPocoClass_StoreOpensInsertsAndReads() {
            // the JSON file defines the model, an attribute-free POCO with the same full name backs
            // it at runtime; the loader must find the POCO's assembly for the mapper compilation:
            var gen = new Datamodel();
            gen.Add<Relatude.SourceLoaderModels.JsonGen.SlReview>();
            var json = DatamodelJson.Serialize(gen).Replace("Relatude.SourceLoaderModels.JsonGen", "Relatude.SourceLoaderModels.JsonPoco");
            var folder = Path.Combine(_root, DatamodelSourceLoader.DefaultJsonFolder);
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "review.json"), json);

            var dm = new Datamodel();
            DatamodelSourceLoader.Load(dm, jsonSource(null), _root);
            var dataFolder = Path.Combine(_root, "data");
            Directory.CreateDirectory(dataFolder);
            var storeData = DataStoreLocal.Open(dm, new SettingsLocal(), new IOProviderDisk(dataFolder));
            using var store = new NodeStore(storeData);
            store.Insert(new Relatude.SourceLoaderModels.JsonPoco.SlReview() { Author = "Ada", Rating = 5 }, out Guid id);
            var read = store.Get<Relatude.SourceLoaderModels.JsonPoco.SlReview>(id);
            Assert.AreEqual("Ada", read.Author);
            Assert.AreEqual(5, read.Rating);
            Assert.AreEqual("review.json", dm.NodeTypesByFullName["Relatude.SourceLoaderModels.JsonPoco.SlReview"].DatamodelSourceFilename);
        }

        [TestMethod]
        public void CSharpFileSource_StoreOpensInsertsAndReads() {
            // end to end: the store must be able to compile its mappers against the in-memory
            // model assembly (no file on disk), open, insert and read a node:
            writeCsFiles();
            var dm = new Datamodel();
            DatamodelSourceLoader.Load(dm, csharpSource(null), _root);
            var dataFolder = Path.Combine(_root, "data");
            Directory.CreateDirectory(dataFolder);
            var storeData = DataStoreLocal.Open(dm, new SettingsLocal(), new IOProviderDisk(dataFolder));
            using var store = new NodeStore(storeData);
            var personType = dm.NodeTypesByFullName["My.CsModels.CsPerson"];
            var person = Activator.CreateInstance(dm.Assemblies.Single(a => a.GetName().Name!.StartsWith("RelatudeModel.")).GetType("My.CsModels.CsPerson")!)!;
            person.GetType().GetProperty("Name")!.SetValue(person, "Ada");
            store.Insert(person, out Guid id);
            var read = store.Get(id);
            Assert.AreEqual("Ada", read.GetType().GetProperty("Name")!.GetValue(read));
        }

        /// <summary>
        /// A source that is turned off contributes nothing and is not registered on the model either,
        /// so a half configured one - what the admin UI writes the moment a source is added - cannot
        /// stop the database from opening. Even a source that would throw is simply skipped, since the
        /// flag is read before anything else about it is.
        /// </summary>
        [TestMethod]
        public void DisabledSource_IsSkippedEntirely() {
            var folder = Path.Combine(_root, "models");
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "library.json"), buildJsonModel());

            var off = jsonSource("models/library.json");
            off.Enabled = false;
            var dm = new Datamodel();
            DatamodelSourceLoader.Load(dm, off, _root);
            Assert.AreEqual(0, dm.Sources.Count, "A source that is not loaded must not be registered on the model. ");
            Assert.IsFalse(dm.NodeTypesByFullName.ContainsKey("Relatude.SourceLoaderModels.SlAuthor"));

            // unfinished and pointing nowhere: exactly what "Add model source" leaves behind
            var unfinished = new DatamodelSource() { Id = Guid.NewGuid(), Type = DatamodelSourceType.AssemblyNameReference, Enabled = false };
            DatamodelSourceLoader.Load(dm, unfinished, _root);
            Assert.AreEqual(0, dm.Sources.Count);

            // and turning it back on loads it, so nothing about the definition was lost
            off.Enabled = true;
            DatamodelSourceLoader.Load(dm, off, _root);
            dm.EnsureInitalization();
            Assert.IsTrue(dm.NodeTypesByFullName.ContainsKey("Relatude.SourceLoaderModels.SlAuthor"));
        }
    }
}
