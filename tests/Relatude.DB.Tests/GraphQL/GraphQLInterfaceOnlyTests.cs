using Relatude.DB.Datamodels;
using Relatude.DB.DataStores;
using Relatude.DB.GraphQL;
using Relatude.DB.GraphQL.Schema;
using Relatude.DB.IO;
using Relatude.DB.Nodes;
using static Relatude.GraphQL.GraphQLTestHelper;

namespace Relatude.GraphQL {
    // a model of interfaces only, as an application using Create<IGqlTicket>() has: every node's type is an interface
    [Node]
    public interface IGqlTicket {
        Guid Id { get; set; }
        [StringProperty(DisplayName = true)]
        string Title { get; set; }
        int Priority { get; set; }
    }
    [Node]
    public interface IGqlUrgentTicket : IGqlTicket {
        string Escalation { get; set; }
    }
    // an interface the classes implement: a contract, not a type of its own nodes
    [Node]
    public interface IGqlNamed {
        string Name { get; set; }
    }
    [Node]
    public class GqlNamedThing : IGqlNamed {
        public Guid Id { get; set; }
        public string Name { get; set; } = "";
    }

    /// <summary>
    /// Nodes whose type is a datamodel interface. A GraphQL interface cannot be the runtime type of a value, so each
    /// exposed datamodel interface gets an object type for its own nodes; without it every such node came back null
    /// ("totalCount": 3, "items": [null, null, null]).
    /// </summary>
    [TestClass]
    public class GraphQLInterfaceOnlyTests {

        static GraphQLEndpointDefinition exact(bool mutations = false)
            => new() { Name = "t", Mode = GraphQLEndpointMode.WholeDatamodel, ExactNames = true, AllowMutations = mutations };

        static (NodeStore store, IDataStore data) open() {
            var dm = new Datamodel();
            dm.Add<IGqlTicket>();
            dm.Add<IGqlUrgentTicket>();
            var data = DataStoreLocal.Open(dm, null, new IOProviderMemory());
            var store = new NodeStore(data);
            for (var i = 1; i <= 3; i++) {
                var t = store.Create<IGqlTicket>();
                t.Title = "Ticket " + i;
                t.Priority = i;
                store.Insert(t);
            }
            var urgent = store.Create<IGqlUrgentTicket>();
            urgent.Title = "Fire";
            urgent.Priority = 9;
            urgent.Escalation = "now";
            store.Insert(urgent);
            return (store, data);
        }

        [TestMethod]
        public void NodesOfAnInterfaceType_AreProjected() {
            var (store, data) = open();
            try {
                var gql = new RelatudeGraphQL(data, exact());
                var result = RequireData(gql.Execute("""
                    { IGqlTickets(orderBy: Priority) {
                        totalCount
                        items { __typename id Title Priority ... on IGqlUrgentTicket { Escalation } }
                    } }
                    """));
                Assert.AreEqual(4, Get(result, "IGqlTickets", "totalCount"));
                var items = (List<object?>)Get(result, "IGqlTickets", "items")!;
                Assert.AreEqual(4, items.Count);
                Assert.IsTrue(items.All(i => i != null), "every node must be projected");
                Assert.AreEqual("Ticket 1", Get(items[0], "Title"));
                Assert.AreEqual(1, Get(items[0], "Priority"));
                Assert.AreEqual("GqlTicket", Get(items[0], "__typename"), "the object type is the interface's name without the I");
                Assert.IsFalse(((Dictionary<string, object?>)items[0]!).ContainsKey("Escalation"));
                Assert.AreEqual("GqlUrgentTicket", Get(items[3], "__typename"), "a sub-interface's node keeps its own type");
                Assert.AreEqual("now", Get(items[3], "Escalation"));

                var id = (string)Get(items[1], "id")!;
                var one = RequireData(gql.Execute($$"""{ IGqlTicket(id: "{{id}}") { Title ... on GqlTicket { Priority } } }"""));
                Assert.AreEqual("Ticket 2", Get(one, "IGqlTicket", "Title"));
                Assert.AreEqual(2, Get(one, "IGqlTicket", "Priority"));

                var sdl = gql.ToSDL();
                StringAssert.Contains(sdl, "interface IGqlTicket implements Node");
                StringAssert.Contains(sdl, "type GqlTicket implements Node & IGqlTicket");
                StringAssert.Contains(sdl, "type GqlUrgentTicket implements Node & IGqlTicket & IGqlUrgentTicket");
            } finally { store.Dispose(); }
        }

        [TestMethod]
        public void InterfaceNodes_InCamelCaseMode() {
            var (store, data) = open();
            try {
                var gql = new RelatudeGraphQL(data);
                var result = RequireData(gql.Execute("{ iGqlTickets { totalCount items { title } } }"));
                Assert.AreEqual(4, Get(result, "iGqlTickets", "totalCount"));
                Assert.IsTrue(((List<object?>)Get(result, "iGqlTickets", "items")!).All(i => i != null));
            } finally { store.Dispose(); }
        }

        [TestMethod]
        public void SelectedMode_UnexposedSubInterface_IsProjectedAsTheExposedOne() {
            var (store, data) = open();
            try {
                var dm = data.Datamodel;
                var def = new GraphQLEndpointDefinition {
                    Name = "t", Mode = GraphQLEndpointMode.Selected, ExactNames = true,
                    Types = [new GraphQLTypeDefinition { NodeTypeId = dm.NodeTypesByFullName[typeof(IGqlTicket).FullName!].Id }],
                };
                var gql = new RelatudeGraphQL(data, def);
                var result = RequireData(gql.Execute("{ IGqlTickets { totalCount items { __typename Title } } }"));
                var items = (List<object?>)Get(result, "IGqlTickets", "items")!;
                Assert.AreEqual(4, items.Count);
                Assert.IsTrue(items.All(i => i != null && (string)Get(i, "__typename")! == "GqlTicket"));
            } finally { store.Dispose(); }
        }

        [TestMethod]
        public void Mutations_CreateUpdateDelete_OnAnInterfaceType() {
            var (store, data) = open();
            try {
                var gql = new RelatudeGraphQL(data, exact(mutations: true));
                var created = RequireData(gql.Execute("""mutation { CreateIGqlTicket(input: { Title: "New", Priority: 5 }) { __typename id Title Priority } }"""));
                Assert.AreEqual("GqlTicket", Get(created, "CreateIGqlTicket", "__typename"));
                Assert.AreEqual(5, Get(created, "CreateIGqlTicket", "Priority"));
                var id = (string)Get(created, "CreateIGqlTicket", "id")!;
                Assert.AreEqual("New", store.Get<IGqlTicket>(Guid.Parse(id)).Title, "the typed API reads the node back");

                var updated = RequireData(gql.Execute($$"""mutation { UpdateIGqlTicket(id: "{{id}}", input: { Priority: 6 }) { Title Priority } }"""));
                Assert.AreEqual(6, Get(updated, "UpdateIGqlTicket", "Priority"));
                Assert.AreEqual("New", Get(updated, "UpdateIGqlTicket", "Title"));

                var deleted = RequireData(gql.Execute($$"""mutation { DeleteIGqlTicket(id: "{{id}}") }"""));
                Assert.AreEqual(true, Get(deleted, "DeleteIGqlTicket"));
                Assert.AreEqual(4, store.Query<IGqlTicket>().Count());
            } finally { store.Dispose(); }
        }

        [TestMethod]
        public void AnInterfaceClassesImplement_GetsNoCreateMutation() {
            var dm = new Datamodel();
            dm.Add<IGqlNamed>();
            dm.Add<GqlNamedThing>();
            var schema = RelatudeGraphQL.BuildSchema(dm, exact(mutations: true));
            var names = schema.MutationType!.Fields.Select(f => f.Name).ToList();
            CollectionAssert.Contains(names, "CreateGqlNamedThing");
            CollectionAssert.DoesNotContain(names, "CreateIGqlNamed");
            Assert.IsTrue(schema.Types.ContainsKey("GqlNamed"), "its own nodes, should any exist, still have a type");
        }

        [TestMethod]
        public void InstanceTypeName_OfANameWithoutIPrefix() {
            var dm = new Datamodel();
            dm.Add<IGqlTicket>();
            dm.Add<IGqlUrgentTicket>();
            dm.EnsureInitalization();
            var def = new GraphQLEndpointDefinition {
                Name = "t", Mode = GraphQLEndpointMode.Selected, ExactNames = true,
                Types = [
                    new GraphQLTypeDefinition { NodeTypeId = dm.NodeTypesByFullName[typeof(IGqlTicket).FullName!].Id, Name = "Ticket" },
                    new GraphQLTypeDefinition { NodeTypeId = dm.NodeTypesByFullName[typeof(IGqlUrgentTicket).FullName!].Id, Name = "UrgentTicket" },
                ],
            };
            var schema = RelatudeGraphQL.BuildSchema(dm, def);
            Assert.IsInstanceOfType<GqlInterfaceType>(schema.Types["Ticket"]);
            Assert.IsInstanceOfType<GqlObjectType>(schema.Types["TicketNode"], "a name without the I prefix gets Node appended");
            Assert.IsInstanceOfType<GqlObjectType>(schema.Types["UrgentTicketNode"]);
        }
    }

    /// <summary>
    /// The same over a RuntimeTypes (JSON) datamodel source whose types have no CLR type anywhere: the store generates
    /// them into its mapper assembly, and the endpoint must work from the datamodel alone.
    /// </summary>
    [TestClass]
    public class GraphQLRuntimeTypesTests {
        const string Namespace = "Relatude.GraphQL.RuntimeOnly";
        string _root = "";

        [TestInitialize]
        public void Setup() {
            _root = Path.Combine(Path.GetTempPath(), "RelatudeDBTests", "GraphQLRuntime_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        [TestCleanup]
        public void Cleanup() {
            try { Directory.Delete(_root, true); } catch { }
        }

        [TestMethod]
        public void JsonSourceWithoutClasses_QueriesAndMutations() {
            var gen = new Datamodel();
            gen.Add<RuntimeGen.IGqlRtTopic>();
            gen.Add<RuntimeGen.IGqlRtLesson>();
            gen.Add<RuntimeGen.GqlRtTopicLessons>();
            gen.Add<RuntimeGen.IGqlRtThing>();
            gen.Add<RuntimeGen.GqlRtFolder>();
            var json = DatamodelJson.Serialize(gen).Replace("Relatude.GraphQL.RuntimeGen", Namespace);
            var source = new DatamodelSource {
                Id = new Guid("22222222-0000-0000-0000-000000000001"), Name = "Json", Type = DatamodelSourceType.RuntimeTypes,
            };
            var folder = Path.Combine(_root, DatamodelSourceLoader.DefaultPath(source));
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "model.json"), json);

            var dm = new Datamodel();
            DatamodelSourceLoader.Load(dm, source, _root);
            Assert.IsFalse(AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetType(Namespace + ".IGqlRtTopic", false) != null),
                "the test needs types no loaded assembly declares");
            var dataFolder = Path.Combine(_root, "data");
            Directory.CreateDirectory(dataFolder);
            var data = DataStoreLocal.Open(dm, new SettingsLocal(), new IOProviderDisk(dataFolder));
            using var store = new NodeStore(data);

            // one node created the way application code does it, through the class the store generated
            var topic = store.Create("IGqlRtTopic");
            topic.GetType().GetProperty("Title")!.SetValue(topic, "Rivers");
            topic.GetType().GetProperty("Rank")!.SetValue(topic, 2);
            store.Insert(topic, out Guid riversId);
            var folderNode = store.Create("GqlRtFolder");
            folderNode.GetType().GetProperty("Name")!.SetValue(folderNode, "Inbox");
            store.Insert(folderNode);

            var gql = new RelatudeGraphQL(data, new GraphQLEndpointDefinition {
                Name = "t", Mode = GraphQLEndpointMode.WholeDatamodel, ExactNames = true, AllowMutations = true,
            });
            Assert.AreEqual(0, gql.Warnings.Count, string.Join(" | ", gql.Warnings));

            var created = RequireData(gql.Execute("""mutation { CreateIGqlRtTopic(input: { Title: "Lakes", Rank: 1 }) { __typename id Title } }"""));
            Assert.AreEqual("GqlRtTopic", Get(created, "CreateIGqlRtTopic", "__typename"));
            var lakesId = (string)Get(created, "CreateIGqlRtTopic", "id")!;
            var lesson = RequireData(gql.Execute($$"""mutation { CreateIGqlRtLesson(input: { Name: "Canoe", Topic: "{{lakesId}}" }) { Name Topic { Title } } }"""));
            Assert.AreEqual("Lakes", Get(lesson, "CreateIGqlRtLesson", "Topic", "Title"));

            var result = RequireData(gql.Execute("""
                { IGqlRtTopics(orderBy: Rank) { totalCount items { __typename id displayName Title Rank Lessons { Name } } } }
                """));
            Assert.AreEqual(2, Get(result, "IGqlRtTopics", "totalCount"));
            Assert.AreEqual("Lakes", Get(result, "IGqlRtTopics", "items", 0, "Title"));
            Assert.AreEqual("Lakes", Get(result, "IGqlRtTopics", "items", 0, "displayName"));
            Assert.AreEqual("Canoe", Get(result, "IGqlRtTopics", "items", 0, "Lessons", 0, "Name"));
            Assert.AreEqual(riversId.ToString(), Get(result, "IGqlRtTopics", "items", 1, "id"));
            Assert.AreEqual(2, Get(result, "IGqlRtTopics", "items", 1, "Rank"));

            // the class implementing a JSON interface: its nodes are the class's, and the interface has no create
            var things = RequireData(gql.Execute("{ IGqlRtThings { items { __typename Name } } }"));
            Assert.AreEqual("GqlRtFolder", Get(things, "IGqlRtThings", "items", 0, "__typename"));
            Assert.AreEqual("Inbox", Get(things, "IGqlRtThings", "items", 0, "Name"));
            Assert.IsFalse(gql.Schema.MutationType!.Fields.Any(f => f.Name == "CreateIGqlRtThing"));

            // and the typed side sees the node GraphQL created
            Assert.AreEqual(2, store.QueryType("IGqlRtTopic").Execute().Count());
        }
    }
}

namespace Relatude.GraphQL.RuntimeGen {
    // attributed twins used only to produce a JSON model file; renamed to ...RuntimeOnly they have no class anywhere
    [Node]
    public interface IGqlRtTopic {
        Guid Id { get; set; }
        [StringProperty(DisplayName = true)]
        string Title { get; set; }
        int Rank { get; set; }
        GqlRtTopicLessons.Lessons Lessons { get; }
    }
    [Node]
    public interface IGqlRtLesson {
        Guid Id { get; set; }
        string Name { get; set; }
        GqlRtTopicLessons.Topic Topic { get; }
    }
    public class GqlRtTopicLessons : OneToMany<IGqlRtTopic, IGqlRtLesson> {
        public class Topic : One { }
        public class Lessons : Many { }
    }
    [Node]
    public interface IGqlRtThing {
        string Name { get; set; }
    }
    [Node]
    public class GqlRtFolder : IGqlRtThing {
        public Guid Id { get; set; }
        public string Name { get; set; } = "";
    }
}
