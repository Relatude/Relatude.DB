using System.Runtime.Loader;
using System.Text.Json;
using System.Text.Json.Nodes;
using Relatude.DB.CodeGeneration;
using Relatude.DB.Common;
using Relatude.DB.Datamodels;
using Relatude.DB.Datamodels.Properties;
using Relatude.DB.Nodes;

namespace Relatude.RoundTripModels {

    #region round trip test datamodel, covering all property types and relation shapes
    public enum RtColor { Red = 1, Green = 2, Blue = 4 }

    public static class RtIds {
        public const string CarNodeId = "aaaaaaaa-0000-0000-0000-00000000c001";
        public const string BoatNodeId = "aaaaaaaa-0000-0000-0000-00000000b001";
    }

    // class inheritance:
    [Node]
    public class RtVehicle {
        [PublicIdProperty]
        public Guid Id { get; set; }
        [StringProperty]
        public string VIN { get; set; } = "";
    }
    [Node(Id = RtIds.CarNodeId)]
    public class RtCar : RtVehicle {
        [IntegerProperty(Indexed = true)]
        public int Wheels { get; set; }
        public RtCarsRel.OwnerOf Owner { get; set; } = new();
    }
    [Node(Id = RtIds.BoatNodeId)]
    public class RtBoat : RtVehicle {
        [DoubleProperty]
        public double Draft { get; set; }
    }

    // every value property type, system properties and property settings:
    [Node(TextIndex = BoolValue.True, InstantTextIndexing = BoolValue.True, TextIndexBoost = 1.5)]
    public class RtEverything {
        [PublicIdProperty]
        public Guid Id { get; set; }
        public NodeMeta Meta { get; set; } = NodeMeta.Empty;
        [CreatedUtcProperty]
        public DateTime Created { get; set; }
        [ChangedUtcProperty]
        public DateTime Changed { get; set; }
        [DisplayNameProperty]
        public string Title { get; set; } = "";
        [AddressProperty]
        public string Url { get; set; } = "";

        [BooleanProperty(Indexed = true, DefaultValue = true, NotFacet = true)]
        public bool Flag { get; set; }
        [IntegerProperty(Indexed = true, DefaultValue = 7, MinValue = -5, MaxValue = 5000, UniqueValues = true, FacetRangePowerBase = 2.5, FacetRangeCount = 6)]
        public int Number { get; set; }
        public RtColor Color { get; set; }
        [LongProperty(DefaultValue = 123456789012345, MinValue = -10, MaxValue = 9000000000000000000, FacetRangePowerBase = 1.5, FacetRangeCount = 5)]
        public long BigNumber { get; set; }
        [DoubleProperty(Indexed = true, DefaultValue = 2.5, FacetRangeCount = 8, NotFacet = true)]
        public double Ratio { get; set; }
        [FloatProperty(DefaultValue = 1.5f, MinValue = -100f, MaxValue = 100f, FacetRangePowerBase = 3)]
        public float Weight { get; set; }
        [DecimalProperty(DefaultValue = "1.25", MinValue = "-10.5", MaxValue = "99999.75", UniqueValues = true, FacetRangeCount = 4)]
        public decimal Price { get; set; }
        [DateTimeProperty(Indexed = true, DefaultValue = "2024-01-02T03:04:05.0000000Z", FacetRangeCount = 12)]
        public DateTime SomeDate { get; set; }
        [DateTimeOffsetProperty(DefaultValue = "2024-01-02T03:04:05.0000000+02:00", NotFacet = true, FacetRangeCount = 9)]
        public DateTimeOffset SomeOffset { get; set; }
        [TimeSpanProperty(DefaultValue = "01:02:03", FacetRangePowerBase = 2)]
        public TimeSpan Duration { get; set; }
        [GuidProperty(Indexed = true, DefaultValue = "eeeeeeee-1111-2222-3333-444444444444", UniqueValues = true)]
        public Guid ExternalId { get; set; }
        [StringProperty(Indexed = true, IndexedByWords = true, IndexedBySemantic = true, MinWordLength = 2, MaxWordLength = 20,
            MinLength = 1, MaxLength = 500, PrefixSearch = true, InfixSearch = true, IgnoreDuplicateEmptyValues = true,
            DefaultValue = "say \"hi\" in C:\\temp\\", TextIndexBoost = 3)] // hostile default: quotes and backslashes must be escaped in generated code
        public string Description { get; set; } = "";
        [StringProperty(ExcludeFromTextIndex = true, DisplayName = true, NotFacet = true)]
        public string Label { get; set; } = "";
        [StringProperty(ReadAccess = "99999999-1111-2222-3333-444444444444", WriteAccess = "88888888-1111-2222-3333-444444444444")]
        public string Restricted { get; set; } = "";
        [StringArrayProperty(Indexed = true, UniqueValues = true, NotFacet = true)]
        public string[] Tags { get; set; } = [];
        [GuidArrayProperty(Indexed = true, NotFacet = true)]
        public Guid[] RelatedIds { get; set; } = [];
        [EnumArrayProperty(Indexed = true, NotFacet = true)]
        public RtColor[] Colors { get; set; } = [];
        [ByteArrayProperty]
        public byte[] Blob { get; set; } = [];
        [FloatArrayProperty]
        public float[] Vector { get; set; } = [];
        [FileProperty(FileStorageProviderId = "ffffffff-1111-2222-3333-444444444444")]
        public FileValue Attachment { get; set; } = FileValue.Empty;
    }

    // embedded map on a class, embedded list on an interface:
    [Node]
    public class RtBook {
        [PublicIdProperty]
        public Guid Id { get; set; }
        [EmbeddedMapProperty(KeyProperty = nameof(RtChapter.Code))]
        public EmbeddedMap<string, RtChapter> Chapters { get; set; } = [];
    }
    [Node]
    public class RtChapter {
        [PublicIdProperty]
        public Guid Id { get; set; }
        [StringProperty]
        public string Code { get; set; } = "";
    }
    public interface IRtSection {
        Guid Id { get; set; }
        string Heading { get; set; }
        [EmbeddedProperty]
        Embedded<IRtParagraph> Paragraphs { get; }
    }
    public interface IRtParagraph {
        Guid Id { get; set; }
        string Text { get; set; }
    }

    // all five relation shapes (native), attribute bound relation properties, and reference shapes:
    [Node]
    public class RtPerson {
        [PublicIdProperty]
        public Guid Id { get; set; }
        [StringProperty]
        public string Name { get; set; } = "";

        public RtSpouseRel.Partner Spouse { get; set; } = new();       // OneOne
        public RtFriendsRel.Friends Friends { get; set; } = new();     // ManyMany
        public RtPassportRel.Passport Passport { get; set; } = new();  // OneToOne
        public RtCarsRel.Owned Cars { get; set; } = new();             // OneToMany
        public RtTeamsRel.Teams Teams { get; set; } = new();           // ManyToMany

        [RelationProperty<RtDocsRel>(Facet = true, TextIndexRelatedContent = true, TextIndexRelatedDisplayName = true, TextIndexRecursiveLevelLimit = 2)]
        public List<RtDoc>? Docs { get; set; }                         // non-native relation property

        [ReferenceProperty(Indexed = true, NotFacet = true)]
        public Reference<RtCar> FavoriteCar { get; set; } = new();     // Reference, wrapper
        [ReferencesProperty(Indexed = true, UniqueValues = true, NotFacet = true)]
        public References<RtCar> PastCars { get; set; } = new();       // References, wrapper
        public RtBoat? DreamBoat { get; set; }                         // Reference, Object shape
        [ReferenceProperty(TypeIds = new string[] { RtIds.CarNodeId }, IncludeTypes = IncludeTypeOptions.ThisTypeOnly)]
        public RtCar? CompanyCar { get; set; }                         // Reference with explicit target types
        public RtBoat[]? BoatArray { get; set; }                       // References, Array shape
        public List<RtBoat>? BoatList { get; set; }                    // References, List shape
        public IEnumerable<RtBoat>? BoatEnumerable { get; set; }       // References, Enumerable shape
        public ICollection<RtBoat>? BoatCollection { get; set; }       // References, Collection shape
        [ReferenceProperty(TypeIds = new string[] { RtIds.CarNodeId, RtIds.BoatNodeId })]
        public RtVehicle? AnyVehicle { get; set; }                     // multi-target reference: common ancestor is RtVehicle
    }
    [Node]
    public class RtPassport {
        [PublicIdProperty]
        public Guid Id { get; set; }
        public RtPassportRel.Person Owner { get; set; } = new();
    }
    [Node]
    public class RtTeam {
        [PublicIdProperty]
        public Guid Id { get; set; }
        public RtTeamsRel.Members Members { get; set; } = new();
    }
    [Node]
    public class RtDoc {
        [PublicIdProperty]
        public Guid Id { get; set; }
        [RelationProperty<RtDocsRel>(RightToLeft = true)]
        public RtPerson? DocOwner { get; set; }
    }
    public class RtSpouseRel : OneOne<RtPerson> {
        public class Partner : One { }
    }
    public class RtFriendsRel : ManyMany<RtPerson> {
        public class Friends : Many { }
    }
    [Relation(DisallowCircularReferences = true)]
    public class RtCarsRel : OneToMany<RtPerson, RtCar> {
        public class OwnerOf : One { }
        public class Owned : Many { }
    }
    public class RtPassportRel : OneToOne<RtPerson, RtPassport> {
        public class Person : OneFrom { }
        public class Passport : OneTo { }
    }
    public class RtTeamsRel : ManyToMany<RtPerson, RtTeam> {
        public class Members : ManyFrom { }
        public class Teams : ManyTo { }
    }
    public class RtDocsRel : OneToMany<RtPerson, RtDoc> { }

    // auto-deduced relation pair, added with autoDeduceRelations: true
    [Node]
    public class RtAutoParent {
        [PublicIdProperty]
        public Guid Id { get; set; }
        public IEnumerable<RtAutoChild>? Children { get; set; }
    }
    [Node]
    public class RtAutoChild {
        [PublicIdProperty]
        public Guid Id { get; set; }
        public RtAutoParent? Parent { get; set; }
    }

    // classes implementing model interfaces: the class model owns only its own members, the
    // interface model owns the shared ones, and generated classes must still implement them:
    public interface IRtNamed {
        Guid Id { get; set; }
        [StringProperty(Indexed = true)]
        string DisplayLabel { get; set; }
    }
    [Node]
    public class RtNamedThing : IRtNamed {
        public Guid Id { get; set; }
        public string DisplayLabel { get; set; } = "";
        [IntegerProperty]
        public int Rank { get; set; }
    }
    [Node]
    public class RtSection : IRtSection { // includes an embedded (list) implementation
        public Guid Id { get; set; }
        public string Heading { get; set; } = "";
        public Embedded<IRtParagraph> Paragraphs { get; } = [];
    }

    // record model type:
    [Node]
    public record RtNote {
        [PublicIdProperty]
        public Guid Id { get; set; }
        [StringProperty]
        public string Text { get; set; } = "";
    }
    #endregion
}

namespace Relatude.Querying {
    using Relatude.RoundTripModels;

    [TestClass]
    public class ModelGenRoundTripTests {

        const string ModelNamespace = "Relatude.RoundTripModels";

        static Datamodel buildOriginal() {
            var dm = new Datamodel();
            dm.Add<RtVehicle>();
            dm.Add<RtCar>();
            dm.Add<RtBoat>();
            dm.Add<RtEverything>();
            dm.Add<RtBook>();
            dm.Add<RtChapter>();
            dm.Add<IRtSection>();
            dm.Add<IRtParagraph>();
            dm.Add<RtPerson>();
            dm.Add<RtPassport>();
            dm.Add<RtTeam>();
            dm.Add<RtDoc>();
            dm.Add<RtNote>();
            dm.Add<IRtNamed>();
            dm.Add<RtNamedThing>();
            dm.Add<RtSection>();
            dm.Add<RtSpouseRel>();
            dm.Add<RtFriendsRel>();
            dm.Add<RtCarsRel>();
            dm.Add<RtPassportRel>();
            dm.Add<RtTeamsRel>();
            dm.Add<RtDocsRel>();
            dm.Add<RtAutoParent>(autoDeduceRelations: true);
            dm.Add<RtAutoChild>(autoDeduceRelations: true);
            return dm;
        }

        // Comparable projection of a node type. Properties and Parents are compared as sorted id
        // sets (declaration order is not significant, and property details are compared separately).
        static string nodeTypeJson(NodeTypeModel t) => JsonSerializer.Serialize(new {
            t.Id,
            t.CodeName,
            t.Namespace,
            t.ModelType,
            t.IsInnerNode,
            t.Hidden,
            t.NameOfPublicIdProperty,
            t.NameOfInternalIdProperty,
            t.DataTypeOfPublicId,
            t.DataTypeOfInternalId,
            t.NameOfMetaProperty,
            t.NameOfCreatedUtcProperty,
            t.NameOfChangedUtcProperty,
            t.NameOfDisplayNameProperty,
            t.NameOfAddressProperty,
            t.TextIndex,
            t.SemanticIndex,
            t.InstantTextIndexing,
            t.TextIndexBoost,
            Parents = t.Parents.OrderBy(x => x).ToArray(),
            PropertyIds = t.Properties.Keys.OrderBy(x => x).ToArray(),
        });

        static string relationJson(RelationModel r) => JsonSerializer.Serialize(new {
            r.Id,
            r.Namespace,
            r.CodeName,
            r.RelationType,
            SourceTypes = r.SourceTypes.OrderBy(x => x).ToArray(),
            TargetTypes = r.TargetTypes.OrderBy(x => x).ToArray(),
            r.CodeNameSources,
            r.CodeNameTargets,
            r.MaxCountTo,
            r.MaxCountFrom,
            r.CultureSpecific,
            r.DisallowCircularReferences,
        });

        // Full comparison of a property model using its runtime type, minus members that
        // legitimately differ between the two reflection passes: the original resolves target
        // types from CLR type names while regenerated code carries explicit ids.
        static string propertyJson(PropertyModel p) {
            var node = JsonSerializer.SerializeToNode(p, p.GetType())!.AsObject();
            node.Remove("AutoAssigned");        // set by relation auto-matching only on the original
            node.Remove("NodeTypesNames");      // original: type names, regenerated: ids (NodeTypes is compared)
            node.Remove("InnerNodeTypesNames"); // same for embedded inner types (InnerNodeTypes is compared)
            node.Remove("KeyPropertyName");     // original: property name, regenerated: resolved id (KeyProperty is compared)
            return node.ToJsonString();
        }

        [TestMethod]
        public void GeneratedCode_CompilesAndRebuildsSameDatamodel() {
            var dm1 = buildOriginal();
            dm1.EnsureInitalization();

            var code = ModelGen.GenerateCSharpModelCode(dm1);

            byte[] dll;
            try {
                dll = Compiler.BuildDll([("RoundTripModels", code)], dm1);
            } catch (Exception ex) {
                Assert.Fail("Generated model code does not compile: " + ex.Message + "\n----- generated code -----\n" + code);
                return;
            }
            var loader = new AssemblyLoadContext(null);
            using var ms = new MemoryStream(dll);
            var asm = loader.LoadFromStream(ms);

            // rebuild a datamodel from the compiled generated code; attributes must carry
            // everything, so auto-deduction stays off:
            var dm2 = new Datamodel();
            dm2.AddAssembly(asm, ModelNamespace);
            dm2.EnsureInitalization();

            // node types:
            CollectionAssert.AreEquivalent(dm1.NodeTypes.Keys.ToList(), dm2.NodeTypes.Keys.ToList(), "Node type ids differ. ");
            foreach (var id in dm1.NodeTypes.Keys) {
                var t1 = dm1.NodeTypes[id];
                var t2 = dm2.NodeTypes[id];
                Assert.AreEqual(nodeTypeJson(t1), nodeTypeJson(t2), "Node type " + t1.FullName + " did not round-trip. ");
            }

            // relations:
            CollectionAssert.AreEquivalent(dm1.Relations.Keys.ToList(), dm2.Relations.Keys.ToList(), "Relation ids differ. ");
            foreach (var id in dm1.Relations.Keys) {
                var r1 = dm1.Relations[id];
                var r2 = dm2.Relations[id];
                Assert.AreEqual(relationJson(r1), relationJson(r2), "Relation " + r1.CodeName + " did not round-trip. ");
            }

            // properties, compared with their full runtime models:
            CollectionAssert.AreEquivalent(dm1.Properties.Keys.ToList(), dm2.Properties.Keys.ToList(), "Property ids differ. ");
            foreach (var id in dm1.Properties.Keys) {
                var p1 = dm1.Properties[id];
                var p2 = dm2.Properties[id];
                var name = dm1.NodeTypes[p1.NodeType].CodeName + "." + p1.CodeName;
                Assert.AreEqual(p1.GetType(), p2.GetType(), "Property " + name + " changed model type. ");
                Assert.AreEqual(propertyJson(p1), propertyJson(p2), "Property " + name + " did not round-trip. ");
            }
        }
    }
}
