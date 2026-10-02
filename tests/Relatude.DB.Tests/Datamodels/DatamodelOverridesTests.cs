using System.Text.Json;
using Relatude.DB.Datamodels;
using Relatude.DB.Datamodels.Properties;
using Relatude.DB.DataStores;
using Relatude.DB.DataStores.Transactions;
using Relatude.DB.IO;
using Relatude.DB.Nodes;

namespace Relatude.OverrideModels {
    public enum OvStatus { Draft = 0, Published = 1, Archived = 2 }

    // the base: text indexed, which every type below inherits unless it says otherwise
    [Node(TextIndex = BoolValue.True)]
    public interface IOvContent {
        [PublicIdProperty] Guid Id { get; set; }
        [StringProperty(DefaultValue = "Untitled", IndexedByWords = true)] string Title { get; set; }
        [StringProperty] string Body { get; set; }
        [IntegerProperty(Indexed = true, DefaultValue = 1)] int Rank { get; set; }
        [IntegerProperty] OvStatus Status { get; set; }
    }
    // overrides on the type, naming the inherited property
    [Node]
    [PropertyOverride(nameof(IOvContent.Title), DefaultValue = "News", TextIndexBoost = 2)]
    [PropertyOverride(nameof(IOvContent.Status), DefaultValue = OvStatus.Published)]
    public class OvNews : IOvContent {
        public Guid Id { get; set; }
        public string Title { get; set; } = "";
        public string Body { get; set; } = "";
        public int Rank { get; set; }
        public OvStatus Status { get; set; }
    }
    // an override on the member implementing the interface property, and its own text index switch
    [Node(TextIndex = BoolValue.False)]
    public class OvNote : IOvContent {
        public Guid Id { get; set; }
        public string Title { get; set; } = "";
        [PropertyOverride(ExcludeFromTextIndex = BoolValue.True)]
        public string Body { get; set; } = "";
        public int Rank { get; set; }
        public OvStatus Status { get; set; }
    }
    // inherits everything
    [Node]
    public class OvPage : IOvContent {
        public Guid Id { get; set; }
        public string Title { get; set; } = "";
        public string Body { get; set; } = "";
        public int Rank { get; set; }
        public OvStatus Status { get; set; }
    }
    // an interface nobody implements: the engine generates the class, and reads defaults at runtime
    [Node]
    [PropertyOverride(nameof(IOvContent.Title), DefaultValue = "Tag")]
    public interface IOvTag : IOvContent { }

    // a class chain: the override reaches the grandchild
    [Node]
    public class OvBase {
        [PublicIdProperty] public Guid Id { get; set; }
        [StringProperty(DefaultValue = "base")] public string Label { get; set; } = "";
        [DecimalProperty(DefaultValue = "0")] public decimal Price { get; set; }
    }
    [Node]
    [PropertyOverride(nameof(OvBase.Label), DefaultValue = "derived \"quoted\" \\ text")]
    [PropertyOverride(nameof(OvBase.Price), DefaultValue = "1.5")]
    public class OvDerived : OvBase { }
    [Node]
    public class OvGrandchild : OvDerived { }

    // two unrelated bases that disagree
    [Node(TextIndex = BoolValue.True)]
    [PropertyOverride(nameof(IOvContent.Title), DefaultValue = "from A")]
    public interface IOvA : IOvContent { }
    [Node(TextIndex = BoolValue.False)]
    [PropertyOverride(nameof(IOvContent.Title), DefaultValue = "from B")]
    public interface IOvB : IOvContent { }
    [Node]
    public class OvBoth : IOvA, IOvB {
        public Guid Id { get; set; }
        public string Title { get; set; } = "";
        public string Body { get; set; } = "";
        public int Rank { get; set; }
        public OvStatus Status { get; set; }
    }
}

// invalid on purpose, each failing the model build with its own message; a namespace of their own so
// no test adding a namespace picks them up
namespace Relatude.OverrideModels.Invalid {
    [Node] [PropertyOverride(nameof(Name), DefaultValue = "x")]
    public class BadOverrideOwnProperty { [PublicIdProperty] public Guid Id { get; set; } public string Name { get; set; } = ""; }
    [Node] [PropertyOverride("Nope", DefaultValue = "x")]
    public class BadOverrideUnknown : OvBase { }
    [Node] [PropertyOverride(nameof(OvBase.Id), DefaultValue = "x")]
    public class BadOverrideId : OvBase { }
    [Node] [PropertyOverride(nameof(OvBase.Price), DefaultValue = "a lot")]
    public class BadOverrideValue : OvBase { }
}

namespace Relatude.Datamodels {
    using Relatude.OverrideModels;
    using Relatude.OverrideModels.Invalid;

    [TestClass]
    public class DatamodelOverridesTests {

        static Datamodel model() {
            var dm = new Datamodel();
            dm.Add<IOvContent>();
            dm.Add<OvNews>();
            dm.Add<OvNote>();
            dm.Add<OvPage>();
            dm.Add<IOvTag>();
            dm.Add<OvBase>();
            dm.Add<OvDerived>();
            dm.Add<OvGrandchild>();
            return dm;
        }
        static Datamodel withConflict() {
            var dm = model();
            dm.Add<IOvA>();
            dm.Add<IOvB>();
            dm.Add<OvBoth>();
            return dm;
        }
        static NodeTypeModel type<T>(Datamodel dm) => dm.NodeTypes[BuildUtils.GetOrCreateNodeTypeId(typeof(T))];
        static PropertyModel prop(Datamodel dm, string fullName) => dm.PropertiesByFullName[fullName];

        // ---- [PropertyOverride] in code ----

        [TestMethod]
        public void Attribute_OnTheType_AndOnTheMember_LandInPropertyOverrides() {
            var dm = model();
            dm.EnsureInitalization();
            var title = prop(dm, "IOvContent.Title");
            var news = type<OvNews>(dm);
            Assert.AreEqual("News", news.PropertyOverrides![title.Id].DefaultValue);
            Assert.AreEqual(2, news.PropertyOverrides[title.Id].IndexBoost);
            Assert.AreEqual((int)OvStatus.Published, news.PropertyOverrides[prop(dm, "IOvContent.Status").Id].DefaultValue, "an enum default is stored as its integer");
            var note = type<OvNote>(dm);
            Assert.AreEqual(true, note.PropertyOverrides![prop(dm, "IOvContent.Body").Id].ExcludeFromTextIndex);
            Assert.IsNull(type<OvPage>(dm).PropertyOverrides, "a type overriding nothing carries no overrides");
        }

        [TestMethod]
        public void Attribute_Errors_NameTheProblem() {
            var e1 = Assert.ThrowsExactly<Exception>(() => new Datamodel().Add<BadOverrideOwnProperty>());
            StringAssert.Contains(e1.Message, "declares itself");
            var e2 = Assert.ThrowsExactly<Exception>(() => new Datamodel().Add<BadOverrideUnknown>());
            StringAssert.Contains(e2.Message, "no member of that name");
            var e3 = Assert.ThrowsExactly<Exception>(() => new Datamodel().Add<BadOverrideId>());
            StringAssert.Contains(e3.Message, "system member");
            var e4 = Assert.ThrowsExactly<Exception>(() => new Datamodel().Add<BadOverrideValue>());
            StringAssert.Contains(e4.Message, "could not be read");
        }

        // ---- what each type sees ----

        [TestMethod]
        public void Resolution_TheMostSpecificTypeWins_AndTheRestKeepTheDeclaration() {
            var dm = model();
            dm.EnsureInitalization();
            var title = prop(dm, "IOvContent.Title");
            Assert.AreEqual("News", type<OvNews>(dm).GetDefaultValue(title));
            Assert.AreEqual("Untitled", type<OvPage>(dm).GetDefaultValue(title));
            Assert.AreEqual("Tag", type<IOvTag>(dm).GetDefaultValue(title));
            Assert.AreEqual(2, type<OvNews>(dm).GetIndexBoost(title));
            Assert.AreEqual(0, type<OvPage>(dm).GetIndexBoost(title));
            Assert.AreEqual("Untitled", title.GetDefaultValue(), "the shared property object is not touched");
            var body = prop(dm, "IOvContent.Body");
            Assert.IsFalse(type<OvNote>(dm).TextIndexProperties.Contains(body));
            Assert.IsTrue(type<OvPage>(dm).TextIndexProperties.Contains(body));
            // a class chain: the override on the parent reaches the grandchild
            var label = prop(dm, "OvBase.Label");
            Assert.AreEqual("base", type<OvBase>(dm).GetDefaultValue(label));
            Assert.AreEqual("derived \"quoted\" \\ text", type<OvGrandchild>(dm).GetDefaultValue(label));
            Assert.AreEqual(1.5m, type<OvGrandchild>(dm).GetDefaultValue(prop(dm, "OvBase.Price")));
        }

        [TestMethod]
        public void Resolution_UnrelatedBasesThatDisagree_FallBackToTheDeclaration_AndSaySo() {
            var dm = withConflict();
            dm.EnsureInitalization();
            var title = prop(dm, "IOvContent.Title");
            Assert.AreEqual("Untitled", type<OvBoth>(dm).GetDefaultValue(title));
            Assert.AreEqual("from A", type<IOvA>(dm).GetDefaultValue(title));
            Assert.IsTrue(dm.OverrideNotices.Any(n => n.Contains("OvBoth") && n.Contains("IOvA") && n.Contains("IOvB")), string.Join("\n", dm.OverrideNotices));
        }

        [TestMethod]
        public void Switches_AreInherited_FromTheMostSpecificBase_ThenTheDatabaseDefault() {
            var dm = withConflict();
            dm.EnsureInitalization();
            Assert.IsNull(type<OvPage>(dm).TextIndex, "initializing does not fill in inherited switches: the editor writes initialized models back");
            dm.SetIndexDefaults(enableTextIndexByDefault: false, enableSemanticIndexByDefault: false, enableInstantIndexing: false);
            Assert.AreEqual(true, type<OvPage>(dm).TextIndex, "inherited from IOvContent");
            Assert.AreEqual(true, type<IOvTag>(dm).TextIndex);
            Assert.AreEqual(false, type<OvNote>(dm).TextIndex, "its own value wins");
            Assert.AreEqual(false, type<OvBase>(dm).TextIndex, "nothing to inherit: the database default");
            Assert.AreEqual(false, type<OvBoth>(dm).TextIndex, "IOvA and IOvB disagree: the database default");
            Assert.IsTrue(dm.OverrideNotices.Any(n => n.Contains("OvBoth") && n.Contains("TextIndex")), string.Join("\n", dm.OverrideNotices));
        }

        // ---- the database's overrides ----

        static DatamodelOverrides overrides(Datamodel dm, Action<DatamodelOverrides> fill) {
            var o = new DatamodelOverrides();
            fill(o);
            return o;
        }
        static Guid id<T>() => BuildUtils.GetOrCreateNodeTypeId(typeof(T));
        static Guid propertyId(string typeFullName, string name) {
            var dm = model();
            dm.EnsureInitalization();
            return dm.NodeTypesByFullName[typeFullName].AllPropertiesByName[name].Id;
        }

        [TestMethod]
        public void Overrides_ApplyAsIfTheSourceSaidSo() {
            var dm = model();
            var title = propertyId("Relatude.OverrideModels.IOvContent", "Title");
            var rank = propertyId("Relatude.OverrideModels.IOvContent", "Rank");
            dm.Overrides = new DatamodelOverrides();
            var page = dm.Overrides.NodeTypes[id<OvPage>()] = new NodeTypeOverride { TextIndex = false };
            var content = dm.Overrides.NodeTypes[id<IOvContent>()] = new NodeTypeOverride();
            content.Properties = new() { [title] = new PropertyOverride { DefaultValue = "Fresh" }, [rank] = new PropertyOverride { Indexed = false, MinValue = "0" } };
            var news = dm.Overrides.NodeTypes[id<OvNews>()] = new NodeTypeOverride();
            news.Properties = new() { [title] = new PropertyOverride { DefaultValue = "Breaking" }, [rank] = new PropertyOverride { Indexed = true } };
            dm.ApplyOverrides();
            dm.EnsureInitalization();
            dm.SetIndexDefaults(false, false, false);
            var titleProp = prop(dm, "IOvContent.Title");
            Assert.AreEqual(false, type<OvPage>(dm).TextIndex, "a node type setting");
            Assert.AreEqual("Fresh", type<OvPage>(dm).GetDefaultValue(titleProp), "the declaring type's override is what the declaration says");
            Assert.AreEqual("Breaking", type<OvNews>(dm).GetDefaultValue(titleProp), "on one type the runtime value replaces what the code says");
            var rankProp = (IntegerPropertyModel)prop(dm, "IOvContent.Rank");
            Assert.IsFalse(rankProp.Indexed, "a whole property setting, on the declaring type");
            Assert.AreEqual(0, rankProp.MinValue);
            Assert.IsTrue(dm.OverrideNotices.Any(n => n.Contains("Indexed") && n.Contains("OvNews") && n.Contains("can only be overridden on")), string.Join("\n", dm.OverrideNotices));
        }

        [TestMethod]
        public void Overrides_ThatCannotApply_AreSkipped_NeverFatal() {
            var dm = model();
            var rank = propertyId("Relatude.OverrideModels.IOvContent", "Rank");
            var json = """
                {
                  "NodeTypes": {
                    "11111111-2222-3333-4444-555555555555": { "Name": "Gone.Type", "TextIndex": true },
                    "TYPE": {
                      "Name": "Relatude.OverrideModels.IOvContent",
                      "Hiden": true,
                      "Properties": {
                        "RANK": { "Name": "Rank", "DefaultValue": "abc", "MaxLength": 5 },
                        "99999999-2222-3333-4444-555555555555": { "Name": "Gone", "Indexed": true }
                      }
                    }
                  }
                }
                """.Replace("TYPE", id<IOvContent>().ToString()).Replace("RANK", rank.ToString());
            dm.Overrides = JsonSerializer.Deserialize<DatamodelOverrides>(json, DatamodelJson.Options);
            dm.ApplyOverrides();
            dm.EnsureInitalization();
            var notices = string.Join("\n", dm.OverrideNotices);
            StringAssert.Contains(notices, "Gone.Type");
            StringAssert.Contains(notices, "\"Hiden\"");
            StringAssert.Contains(notices, "\"abc\" is not a valid integer");
            StringAssert.Contains(notices, "MaxLength does not apply");
            StringAssert.Contains(notices, "Gone");
            Assert.AreEqual(1, ((IntegerPropertyModel)prop(dm, "IOvContent.Rank")).DefaultValue, "an invalid value leaves the declaration alone");
        }

        // ---- serialization ----

        [TestMethod]
        public void Json_ModelsWithoutOverrides_SerializeAsBefore() {
            var dm = new Datamodel();
            dm.Add<OvBase>();
            var json = DatamodelJson.Serialize(dm);
            Assert.IsFalse(json.Contains("PropertyOverrides"), "an unused feature must not change the files or the checksums of existing models");
            Assert.IsFalse(json.Contains("\"Overrides\""));
        }

        [TestMethod]
        public void Json_RoundTripsBothLayers() {
            var dm = model();
            dm.Overrides = new DatamodelOverrides();
            dm.Overrides.NodeTypes[id<OvPage>()] = new NodeTypeOverride { Name = "OvPage", TextIndex = false, DefaultReadAccess = Guid.Empty };
            var json = DatamodelJson.Serialize(dm);
            var back = DatamodelJson.Deserialize(json);
            Assert.AreEqual(false, back.Overrides!.NodeTypes[id<OvPage>()].TextIndex);
            Assert.IsNull(back.Overrides.NodeTypes[id<OvPage>()].Hidden, "unset members stay unset");
            var title = propertyId("Relatude.OverrideModels.IOvContent", "Title");
            var newsBack = back.NodeTypes[id<OvNews>()];
            back.EnsureInitalization();
            Assert.AreEqual("News", newsBack.GetDefaultValue(back.Properties[title]), "a default read back from JSON is a JsonElement until it is converted");
            dm.EnsureInitalization();
            Assert.AreEqual(DatamodelJson.Checksum(dm), DatamodelJson.Checksum(back), "both layers survive the round trip unchanged");
        }

        [TestMethod]
        public void Json_NumbersTheBrowserRounded_ReadAsTheLimitsTheyStandFor() {
            // what JSON.stringify makes of long.MinValue / long.MaxValue and the decimal limits
            var json = """{ "PropertyType": "Long", "Id": "11111111-2222-3333-4444-555555555555", "CodeName": "L", "MinValue": -9223372036854776000, "MaxValue": 9223372036854776000 }""";
            var p = (LongPropertyModel)JsonSerializer.Deserialize<PropertyModel>(json, DatamodelJson.Options)!;
            Assert.AreEqual(long.MinValue, p.MinValue);
            Assert.AreEqual(long.MaxValue, p.MaxValue);
            var dec = """{ "PropertyType": "Decimal", "Id": "11111111-2222-3333-4444-555555555556", "CodeName": "D", "MinValue": -7.922816251426434e+28, "MaxValue": 7.922816251426434e+28, "DefaultValue": 1.25 }""";
            var d = (DecimalPropertyModel)JsonSerializer.Deserialize<PropertyModel>(dec, DatamodelJson.Options)!;
            Assert.AreEqual(decimal.MinValue, d.MinValue);
            Assert.AreEqual(decimal.MaxValue, d.MaxValue);
            Assert.AreEqual(1.25m, d.DefaultValue, "values in range read exactly as before");
        }

        [TestMethod]
        public void GeneratedCode_CarriesThePropertyOverrides_AndReadsBackTheSame() {
            var dm1 = model();
            dm1.EnsureInitalization();
            var code = DB.CodeGeneration.ModelGen.GenerateCSharpModelCode(dm1);
            StringAssert.Contains(code, "[Relatude.DB.Nodes.PropertyOverride(nameof(IOvContent.Title), DefaultValue = \"News\", TextIndexBoost = 2)]");
            byte[] dll;
            try {
                dll = DB.Nodes.Compiler.BuildDll([("OverrideModels", code)], dm1);
            } catch (Exception ex) {
                Assert.Fail("Generated model code does not compile: " + ex.Message + "\n----- generated code -----\n" + code);
                return;
            }
            var asm = new System.Runtime.Loader.AssemblyLoadContext(null).LoadFromStream(new MemoryStream(dll));
            var dm2 = new Datamodel();
            dm2.AddAssembly(asm, typeof(IOvContent).Namespace!);
            dm2.EnsureInitalization();
            foreach (var t1 in dm1.NodeTypes.Values.Where(t => t.PropertyOverrides != null)) {
                var t2 = dm2.NodeTypes[t1.Id];
                Assert.AreEqual(DatamodelJson.CanonicalJson(t1.PropertyOverrides, DatamodelJson.CompareOptions), DatamodelJson.CanonicalJson(t2.PropertyOverrides, DatamodelJson.CompareOptions), t1.FullName + " did not round-trip its overrides");
            }
        }

        // ---- literals the mapper compiles ----

        [TestMethod]
        public void Literals_AreEscaped_InvariantAndSuffixed() {
            Assert.AreEqual("\"a \\\"b\\\" \\\\ c\\n\"", new StringPropertyModel().GetValueAsCode("a \"b\" \\ c\n"));
            Assert.AreEqual("1.5m", new DecimalPropertyModel().GetValueAsCode(1.5m));
            Assert.AreEqual("1.5f", new FloatPropertyModel().GetValueAsCode(1.5f));
            Assert.AreEqual("0.1d", new DoublePropertyModel().GetValueAsCode(0.1));
            Assert.AreEqual("((Relatude.OverrideModels.OvStatus)(2))", new IntegerPropertyModel { IsEnum = true, FullEnumTypeName = typeof(OvStatus).FullName }.GetValueAsCode(2));
            var culture = Thread.CurrentThread.CurrentCulture;
            try {
                Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("nb-NO");
                Assert.AreEqual("2.25m", new DecimalPropertyModel { DefaultValue = 2.25m }.GetDefaultValueAsCode(), "a comma would not compile");
            } finally {
                Thread.CurrentThread.CurrentCulture = culture;
            }
        }

        // ---- a store opened with all of it ----

        static NodeStore open(Datamodel dm, SettingsLocal? settings = null) {
            var data = new DataStoreLocal(dm, settings ?? new SettingsLocal(), new IOProviderMemory());
            data.Open(true, true);
            return new NodeStore(data);
        }

        [TestMethod]
        public void Store_NewNodesStartWithTheirTypesDefaults_ClassesAndGeneratedInterfaces() {
            using var db = open(model());
            Assert.AreEqual("News", db.Create<OvNews>().Title, "a class: the mapper bakes the type's default");
            Assert.AreEqual(OvStatus.Published, db.Create<OvNews>().Status, "an enum default compiles");
            Assert.AreEqual("Untitled", db.Create<OvPage>().Title);
            Assert.AreEqual("derived \"quoted\" \\ text", db.Create<OvGrandchild>().Label, "an escaped string compiles");
            Assert.AreEqual(1.5m, db.Create<OvGrandchild>().Price, "a decimal default compiles");
            Assert.AreEqual("Tag", db.Create<IOvTag>().Title, "an interface the engine implements reads the default at runtime");
        }

        [TestMethod]
        public void Store_AppliesTheDatabaseOverrides_WhenItOpens() {
            var dm = model();
            var title = propertyId("Relatude.OverrideModels.IOvContent", "Title");
            dm.Overrides = new DatamodelOverrides();
            dm.Overrides.NodeTypes[id<OvPage>()] = new NodeTypeOverride { Properties = new() { [title] = new PropertyOverride { DefaultValue = "Page" } } };
            using var db = open(dm);
            Assert.IsTrue(dm.OverridesApplied);
            Assert.AreEqual("Page", db.Create<OvPage>().Title);
            Assert.AreEqual(true, type<OvPage>(dm).TextIndex, "the inherited switch is filled in by the store");
            var stored = db.Create<OvPage>();
            stored.Title = "Kept";
            db.Insert(stored);
            Assert.AreEqual("Kept", db.Query<OvPage>().Execute().Single().Title);
        }

        [TestMethod]
        public void Store_TextExtract_FollowsTheNodesType() {
            var dm = model();
            using var db = open(dm);
            var local = (DataStoreLocal)db.Datastore;
            var title = prop(dm, "IOvContent.Title");
            var body = prop(dm, "IOvContent.Body");
            string extract<T>() {
                var values = new Properties<object>(2);
                values.Add(title.Id, "giraffe");
                values.Add(body.Id, "zebra");
                return UtilsText.GetTextExtract(local, new NodeData(Guid.NewGuid(), 0, id<T>(), DateTime.UtcNow, DateTime.UtcNow, values, null));
            }
            var note = extract<OvNote>();
            StringAssert.Contains(note, "giraffe");
            Assert.IsFalse(note.Contains("zebra"), "OvNote leaves Body out of its text");
            StringAssert.Contains(extract<OvPage>(), "zebra");
            var news = extract<OvNews>();
            Assert.AreEqual(3, news.Split("giraffe").Length - 1, "boost 2: the title goes in three times");
        }
    }
}
