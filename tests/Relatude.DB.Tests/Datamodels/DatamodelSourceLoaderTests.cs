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
namespace Relatude.SourceLoaderModels.RuntimeGen {
    // attributed twins used only to produce a JSON model file whose types have NO class anywhere once the
    // namespace is renamed to ...RuntimeOnly: an interface, a class implementing it, and a relation
    [Node]
    public interface IRtThing {
        [StringProperty]
        string Name { get; set; }
    }
    [Node]
    public class RtFolder : IRtThing {
        [PublicIdProperty]
        public Guid Id { get; set; }
        public string Name { get; set; } = "";
        public RtFolderNotes.Notes Notes { get; set; } = new();
    }
    [Node]
    public class RtNote {
        [PublicIdProperty]
        public Guid Id { get; set; }
        [StringProperty]
        public string Title { get; set; } = "";
        [IntegerProperty]
        public int Count { get; set; }
        public RtFolderNotes.Folder Folder { get; set; } = new();
    }
    public class RtFolderNotes : OneToMany<RtFolder, RtNote> {
        public class Notes : Many { }
        public class Folder : One { }
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
            Type = DatamodelSourceType.RuntimeTypes,
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
            var folder = Path.Combine(_root, DatamodelSourceLoader.DefaultPath(jsonSource(null)));
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "library.json"), buildJsonModel());
            var dm = new Datamodel();
            DatamodelSourceLoader.Load(dm, jsonSource(null), _root);
            dm.EnsureInitalization();
            Assert.IsTrue(dm.NodeTypesByFullName.ContainsKey("Relatude.SourceLoaderModels.SlAuthor"));
            Assert.AreEqual("library.json", dm.NodeTypesByFullName["Relatude.SourceLoaderModels.SlAuthor"].DatamodelSourceFilename);
        }

        [TestMethod]
        public void JsonFileSource_DefaultPath_IsAFolderOfItsOwnNamedAfterTheSource() {
            var id = new Guid("11111111-0000-0000-0000-000000000009");
            string folderOf(string? name) => DatamodelSourceLoader.DefaultPath(new DatamodelSource { Id = id, Name = name, Type = DatamodelSourceType.RuntimeTypes });
            Assert.AreEqual("relatude.db/modelsources/JsonModel", DatamodelSourceLoader.DefaultPath(jsonSource(null)));
            Assert.AreEqual("relatude.db/modelsources/JsonModel/library.json", DatamodelSourceLoader.DefaultPath(jsonSource(null, "library.json")), "Reference names a file in it");
            Assert.AreEqual("relatude.db/modelsources/Shop Models", folderOf("Shop Models"));
            Assert.AreEqual("relatude.db/modelsources/a_b_c_d", folderOf("a/b\\c:d"), "separators and what Windows refuses become '_' on every system");
            Assert.AreEqual("relatude.db/modelsources/v1.2", folderOf("  v1.2. . "), "trailing dots and spaces go, as Windows would drop them");
            Assert.AreEqual("relatude.db/modelsources/_con", folderOf("con"), "a name Windows reserves");
            Assert.AreEqual("relatude.db/modelsources/_NUL.json", folderOf("NUL.json"), "reserved with an extension too");
            Assert.AreEqual("relatude.db/modelsources/" + id, folderOf(".."), "never a way out of the folder");
            Assert.AreEqual("relatude.db/modelsources/" + id, folderOf(null));
            Assert.AreEqual("relatude.db/modelsources/" + id, folderOf("   "));
        }

        [TestMethod]
        public void JsonFileSource_EmptyFilepath_NamesTheOldDefaultFolderWhenItIsStillThere() {
            var legacy = Path.Combine(_root, "Models", "Json");
            Directory.CreateDirectory(legacy);
            File.WriteAllText(Path.Combine(legacy, "library.json"), buildJsonModel());
            var dm = new Datamodel();
            DatamodelSourceLoader.Load(dm, jsonSource(null), _root);
            Assert.IsFalse(dm.NodeTypes.Values.Any(t => t.CodeName == "SlAuthor"), "the old folder is not read without being named");
            var notice = dm.SourceNotices.Single();
            StringAssert.Contains(notice, "set Filepath to \"Models/Json\"");
            StringAssert.Contains(notice, "relatude.db/modelsources/JsonModel");
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
            var ex = Assert.ThrowsExactly<Exception>(() => DatamodelSourceLoader.Load(dm, second, _root));
            StringAssert.Contains(ex.Message, "same id");
        }

        [TestMethod]
        public void Loader_RejectsEmptyIdCodeTypeAndDuplicateSourceIds() {
            var dm = new Datamodel();
            var noId = jsonSource("x.json");
            noId.Id = Guid.Empty;
            StringAssert.Contains(Assert.ThrowsExactly<Exception>(() => DatamodelSourceLoader.Load(dm, noId, _root)).Message, "no Id");
            var codeType = new DatamodelSource() { Id = Guid.NewGuid(), Type = DatamodelSourceType.Code };
            StringAssert.Contains(Assert.ThrowsExactly<Exception>(() => DatamodelSourceLoader.Load(dm, codeType, _root)).Message, "reserved");
            var folder = Path.Combine(_root, "models");
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "library.json"), buildJsonModel());
            DatamodelSourceLoader.Load(dm, jsonSource("models/library.json"), _root);
            StringAssert.Contains(Assert.ThrowsExactly<Exception>(() => DatamodelSourceLoader.Load(dm, jsonSource("models/library.json"), _root)).Message, "unique id");
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
        // C# files are not a configurable source kind any more: DatamodelSourceLoader.LoadCSharpFiles
        // compiles them for the data model editor's dry run, and that is what these tests cover.
        DatamodelSource csharpSource(string? filepath) => new() {
            Id = new Guid("22222222-0000-0000-0000-000000000001"),
            Name = "CsModel",
            Type = DatamodelSourceType.RuntimeTypes,
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
            DatamodelSourceLoader.LoadCSharpFiles(dm, source, _root);
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
            DatamodelSourceLoader.LoadCSharpFiles(dm1, csharpSource(null), _root);
            var dm2 = new Datamodel();
            DatamodelSourceLoader.LoadCSharpFiles(dm2, csharpSource(null), _root);
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
            var ex = Assert.ThrowsExactly<Exception>(() => DatamodelSourceLoader.LoadCSharpFiles(dm, csharpSource(null), _root));
            StringAssert.Contains(ex.Message, "Broken.cs");
            StringAssert.Contains(ex.Message, "(2)");
        }

        [TestMethod]
        public void CSharpFileSource_MissingPathLoadsEmpty_AndNamesResolvedPath() {
            // a folder that is not there yet is not an error: the source loads empty, so the data model
            // editor can write the first type into it - but the notice names the resolved path, so a typo
            // in Filepath still shows up in the log
            var dm = new Datamodel();
            var source = csharpSource("does/not/exist");
            DatamodelSourceLoader.LoadCSharpFiles(dm, source, _root);
            Assert.AreEqual(1, dm.Sources.Count, "an empty source is still registered on the model");
            Assert.AreEqual(source.Id, dm.Sources[0].Id);
            Assert.AreEqual(0, dm.NodeTypes.Values.Count(t => t.Id != NodeConstants.BaseNodeTypeId));
            var notice = dm.SourceNotices.Single();
            StringAssert.Contains(notice, "does not exist");
            StringAssert.Contains(notice, Path.Combine(_root, "does", "not", "exist"), "the notice names the path as resolved against the settings folder");
            StringAssert.Contains(notice, "*.cs");
        }

        [TestMethod]
        public void JsonFileSource_ModelForPlainPocoClass_StoreOpensInsertsAndReads() {
            // the JSON file defines the model, an attribute-free POCO with the same full name backs
            // it at runtime; the loader must find the POCO's assembly for the mapper compilation:
            var gen = new Datamodel();
            gen.Add<Relatude.SourceLoaderModels.JsonGen.SlReview>();
            var json = DatamodelJson.Serialize(gen).Replace("Relatude.SourceLoaderModels.JsonGen", "Relatude.SourceLoaderModels.JsonPoco");
            var folder = Path.Combine(_root, DatamodelSourceLoader.DefaultPath(jsonSource(null)));
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

        /// <summary>
        /// A runtime types source may define types the application has no class for at all. The store
        /// generates them (classes, an interface, a relation class) with its mappers and opens; the
        /// types are then used by name.
        /// </summary>
        [TestMethod]
        public void JsonFileSource_TypesWithoutAnyClass_AreGeneratedAndStoreOpens() {
            const string ns = "Relatude.SourceLoaderModels.RuntimeOnly";
            var gen = new Datamodel();
            gen.Add<Relatude.SourceLoaderModels.RuntimeGen.IRtThing>();
            gen.Add<Relatude.SourceLoaderModels.RuntimeGen.RtFolder>();
            gen.Add<Relatude.SourceLoaderModels.RuntimeGen.RtNote>();
            gen.Add<Relatude.SourceLoaderModels.RuntimeGen.RtFolderNotes>();
            var json = DatamodelJson.Serialize(gen).Replace("Relatude.SourceLoaderModels.RuntimeGen", ns);
            var folder = Path.Combine(_root, DatamodelSourceLoader.DefaultPath(jsonSource(null)));
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "notes.json"), json);

            var dm = new Datamodel();
            DatamodelSourceLoader.Load(dm, jsonSource(null), _root);
            Assert.IsFalse(AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetType(ns + ".RtNote", false) != null),
                "the test needs a type no loaded assembly declares");
            Relatude.DB.CodeGeneration.MapperCompileCheck.Verify(dm); // what the model editor's dry run calls

            var dataFolder = Path.Combine(_root, "data");
            Directory.CreateDirectory(dataFolder);
            var storeData = DataStoreLocal.Open(dm, new SettingsLocal(), new IOProviderDisk(dataFolder));
            using var store = new NodeStore(storeData);

            var note = store.Create("RtNote");
            Assert.AreEqual(ns + ".RtNote", note.GetType().FullName);
            note.GetType().GetProperty("Title")!.SetValue(note, "Hello");
            note.GetType().GetProperty("Count")!.SetValue(note, 3);
            var folderNode = store.Create("RtFolder");
            folderNode.GetType().GetProperty("Name")!.SetValue(folderNode, "Inbox");
            Assert.IsTrue(folderNode.GetType().GetInterfaces().Any(i => i.FullName == ns + ".IRtThing"), "the generated class implements the generated interface");
            store.Insert(note, out Guid noteId);
            store.Insert(folderNode, out Guid folderId);
            var notesProperty = dm.NodeTypesByFullName[ns + ".RtFolder"].AllPropertiesByName["Notes"];
            store.CreateTransaction().AddRelation(folderId, notesProperty.Id, noteId).Execute();

            var read = store.Get(noteId);
            Assert.AreSame(note.GetType(), read.GetType());
            Assert.AreEqual("Hello", read.GetType().GetProperty("Title")!.GetValue(read));
            Assert.AreEqual(3, read.GetType().GetProperty("Count")!.GetValue(read));
            Assert.AreEqual(1, store.QueryType("RtNote").Execute().Count());
            Assert.AreEqual(1, store.QueryType("IRtThing").Execute().Count(), "only the folder is an IRtThing");
            // the generated relation class works like a compiled one: the related notes load on demand
            var readFolder = store.Get(folderId);
            var notes = readFolder.GetType().GetProperty("Notes")!.GetValue(readFolder)!;
            var related = ((System.Collections.IEnumerable)notes).Cast<object>().Single();
            Assert.AreEqual("Hello", related.GetType().GetProperty("Title")!.GetValue(related));

            // the generated class is now loaded (in the mapper assembly), but it is not the application's own:
            // the loader and the model editor's validator must keep treating the type as one without a class,
            // or a property added in the editor would be reported missing from the generated class
            Assert.IsTrue(AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetType(ns + ".RtNote", false) != null));
            Assert.IsNull(DatamodelSourceLoader.FindBackingClrType(ns + ".RtNote"));
        }

        [TestMethod]
        public void CSharpFileSource_StoreOpensInsertsAndReads() {
            // end to end: the store must be able to compile its mappers against the in-memory
            // model assembly (no file on disk), open, insert and read a node:
            writeCsFiles();
            var dm = new Datamodel();
            DatamodelSourceLoader.LoadCSharpFiles(dm, csharpSource(null), _root);
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
            var unfinished = new DatamodelSource() { Id = Guid.NewGuid(), Type = DatamodelSourceType.CompiledTypes, Enabled = false };
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
