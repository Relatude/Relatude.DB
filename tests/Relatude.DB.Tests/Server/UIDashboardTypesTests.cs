using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Relatude.DB.Datamodels;
using Relatude.DB.NodeServer;
using Relatude.DB.NodeServer.Json;
using Relatude.DB.Nodes;

namespace Relatude.Server.DashboardModels {
    // A model with real inheritance, which the demo model has none of: two classes under one
    // interface is the case the content panel's "include inherited" switch exists for.
    [Node]
    public interface IAnimal {
        [PublicIdProperty]
        public Guid Id { get; set; }
        [StringProperty(Indexed = true)]
        public string Name { get; set; }
    }
    [Node]
    public class Dog : IAnimal {
        public Guid Id { get; set; }
        public string Name { get; set; } = string.Empty;
    }
    [Node]
    public class Cat : IAnimal {
        public Guid Id { get; set; }
        public string Name { get; set; } = string.Empty;
    }
}

namespace Relatude.Server {
    using Relatude.Server.DashboardModels;

    /// <summary>
    /// The per-type counts the dashboard's content panel draws, which are the one place in the admin
    /// UI where a node is counted twice on purpose: once under the type it is, and again under every
    /// type above it. Both numbers come from the same command and the panel picks between them, so
    /// what has to hold here is that they are what they claim - the own count is only the type
    /// itself, the inherited count is the type plus everything below it - and that the parents come
    /// with them, since that is what lets a picture of shares drop what it would otherwise count twice.
    /// </summary>
    [TestClass]
    public class UIDashboardTypesTests {

        static TestServerHost start(string root) {
            var host = TestServerHost.Start(root, configure: settings => {
                foreach (var container in settings.ContainerSettings!) {
                    container.DatamodelSources = [.. container.DatamodelSources ?? [], new DatamodelSource {
                        Id = new Guid("33333333-0000-0000-0000-000000000001"),
                        Name = "Animals",
                        Type = DatamodelSourceType.TypeReference,
                        Reference = typeof(IAnimal).Assembly.GetName().Name,
                        Namespace = typeof(IAnimal).Namespace,
                    }];
                }
            });
            // the admin API (and with it the UI command endpoint) is mapped by the app's own
            // UseRelatudeDB, which goes through the static runtime; the test host maps it directly
            typeof(RelatudeDBServer).GetMethod("MapAdminAPI", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(host.Server, [host.App]);
            return host;
        }

        // posts a command the way the browser does and reads the json it would have received
        static async Task<JsonElement> command(TestServerHost host, string type, object payload) {
            var http = new DefaultHttpContext();
            var body = JsonSerializer.Serialize(new { type, payload }, RelatudeDBJsonOptions.Default);
            http.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
            var result = await host.Server.UI!.Commands.Execute(http);
            var value = ((IValueHttpResult)result).Value;
            var status = ((IStatusCodeHttpResult)result).StatusCode ?? 200;
            var json = JsonSerializer.SerializeToElement(value, RelatudeDBJsonOptions.Default);
            Assert.AreEqual(200, status, "command " + type + " failed: " + json);
            return json;
        }
        static JsonElement prop(JsonElement e, string name) {
            foreach (var p in e.EnumerateObject()) if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) return p.Value;
            throw new AssertFailedException("No property \"" + name + "\" in " + e);
        }
        static JsonElement? typeOrNull(JsonElement dashboard, string name)
            => prop(dashboard, "types").EnumerateArray().Cast<JsonElement?>().FirstOrDefault(t => prop(t!.Value, "name").GetString() == name);
        static JsonElement typeOf(JsonElement dashboard, string name)
            => typeOrNull(dashboard, name) ?? throw new AssertFailedException("No type \"" + name + "\" among the dashboard types");

        [TestMethod]
        public async Task Dashboard_CountsATypesOwnNodesAndWhatIsUnderItApart() {
            var root = Path.Combine(Path.GetTempPath(), "relatude-dash-types-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var host = start(root);
            try {
                var storeId = host.Settings.Settings.ContainerSettings![0].Id;
                var store = host.Server.Containers[storeId].Store!;
                for (var i = 0; i < 3; i++) store.Insert(new Dog { Id = Guid.NewGuid(), Name = "dog" + i });
                for (var i = 0; i < 2; i++) store.Insert(new Cat { Id = Guid.NewGuid(), Name = "cat" + i });

                var dashboard = await command(host, "dashboard", new { storeId });

                var dog = typeOf(dashboard, nameof(Dog));
                Assert.AreEqual(3, prop(dog, "count").GetInt32(), "the nodes that are of this type");
                Assert.AreEqual(3, prop(dog, "countAll").GetInt64(), "nothing is under it, so both counts agree");
                Assert.AreEqual("Class", prop(dog, "kind").GetString());
                Assert.IsFalse(prop(dog, "isInterface").GetBoolean());
                Assert.AreEqual(2, prop(typeOf(dashboard, nameof(Cat)), "count").GetInt32());

                // the interface holds no nodes of its own and would never appear without the
                // inherited count - which is the whole reason the panel has the switch
                var animal = typeOf(dashboard, nameof(IAnimal));
                Assert.AreEqual(0, prop(animal, "count").GetInt32(), "no node's own type is the interface");
                Assert.AreEqual(5, prop(animal, "countAll").GetInt64(), "but every dog and cat is one");
                Assert.IsTrue(prop(animal, "isInterface").GetBoolean());

                // the parents let the page drop what already sits inside something else it is
                // drawing, so a treemap of the inherited counts still adds up
                var animalId = prop(animal, "id").GetGuid();
                CollectionAssert.Contains(prop(dog, "parents").EnumerateArray().Select(p => p.GetGuid()).ToArray(), animalId, "Dog is under IAnimal");
                Assert.AreEqual(0, prop(animal, "parents").GetArrayLength(), "the built-in base type is not a parent anyone shows");

                // a type with nothing in it either way is not in the list at all
                Assert.IsNull(typeOrNull(dashboard, "DemoArticle"), "a type with no nodes, of it or under it, is left out");

                // and the sources ride along, for the colour a type is marked with
                var sources = prop(dashboard, "sources").EnumerateArray().ToArray();
                Assert.IsTrue(sources.Any(s => prop(s, "name").GetString() == "Animals"), "the source the test added is one of them");
            } finally {
                await host.DisposeAsync();
                try { Directory.Delete(root, true); } catch { }
            }
        }

        /// <summary>
        /// What the activity graph is drawn from. The counters are cumulative and the memory figures
        /// are levels, and the page treats the two differently - a level plotted as a difference is
        /// not a rate of anything. All of it comes from the cheap command, so the one thing that has
        /// to hold is that every field the graph names is actually there: a renamed or dropped one
        /// reads as a permanent zero in the browser rather than as an error anyone would notice.
        /// </summary>
        [TestMethod]
        public async Task DashboardLive_ReportsTheCountersAndTheProcessMemory() {
            var root = Path.Combine(Path.GetTempPath(), "relatude-dash-live-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var host = start(root);
            try {
                var storeId = host.Settings.Settings.ContainerSettings![0].Id;
                var store = host.Server.Containers[storeId].Store!;
                store.Insert(new Dog { Id = Guid.NewGuid(), Name = "dog" });

                var live = await command(host, "dashboard-live", new { storeId });

                Assert.IsTrue(prop(live, "open").GetBoolean());
                foreach (var counter in new[] { "queries", "transactions", "actions", "nodeReads" }) {
                    Assert.IsTrue(prop(live, counter).GetInt64() >= 0, counter + " is a cumulative counter the page takes the difference of");
                }
                // the server process, not this database: one heap serves every database on it
                Assert.IsTrue(prop(live, "managedMemory").GetInt64() > 0, "the managed heap always holds something while a database is open");
                Assert.IsTrue(prop(live, "processMemory").GetInt64() > 0, "the working set of the process this database runs in");
            } finally {
                await host.DisposeAsync();
                try { Directory.Delete(root, true); } catch { }
            }
        }

        /// <summary>
        /// The Databases section: the list, adding one, and re-pointing the default. All three write
        /// or read the settings the server holds, and the trap they share is that
        /// <see cref="Relatude.DB.NodeServer.RelatudeDBServer.UpdateWAFServerSettingsFile"/> rebuilds
        /// the container array from the live containers - so a database added to the settings but not
        /// to the dictionary is written straight back out of existence, and one added to the
        /// dictionary after the file is written never reaches the file at all.
        ///
        /// Nothing here opens anything: creating a database must not touch a folder, which is what
        /// makes it safe to offer as a button rather than as a wizard.
        /// </summary>
        [TestMethod]
        public async Task Databases_ListsAddsAndRepointsTheDefault() {
            var root = Path.Combine(Path.GetTempPath(), "relatude-dbs-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var host = start(root);
            try {
                var firstId = host.Settings.Settings.ContainerSettings![0].Id;

                var before = await command(host, "databases", new { });
                Assert.AreEqual(1, prop(before, "databases").GetArrayLength(), "the host starts with one database");
                var only = prop(before, "databases")[0];
                Assert.IsTrue(prop(only, "isDefault").GetBoolean(), "and it is the default one");

                var created = await command(host, "database-create", new { name = "Second", autoOpen = false });
                var newId = prop(created, "storeId").GetGuid();
                Assert.AreNotEqual(firstId, newId);
                Assert.IsTrue(host.Server.Containers.ContainsKey(newId), "the container is live, not only in the file");
                // the file is rebuilt from the live containers, so the new one has to survive a write
                host.Server.UpdateWAFServerSettingsFile();
                CollectionAssert.Contains(host.Settings.Settings.ContainerSettings!.Select(c => c.Id).ToArray(), newId,
                    "a container added at runtime must still be there after the settings are written");

                var second = prop(prop(created, "list"), "databases").EnumerateArray().Single(d => prop(d, "id").GetGuid() == newId);
                Assert.AreEqual("Closed", prop(second, "state").GetString(), "created closed: opening writes files, which is a separate press");
                Assert.AreEqual(0, prop(second, "modelSources").GetInt32(), "and with no model of its own");
                Assert.IsFalse(prop(second, "isDefault").GetBoolean());
                // its own folder, or the two would write over each other's log and state files
                var folder = prop(created, "folder").GetString();
                Assert.IsNotNull(folder);
                var firstFolder = host.Settings.Settings.ContainerSettings!.Single(c => c.Id == firstId).IOSettings!.First().Path;
                Assert.AreNotEqual(firstFolder, folder, "two databases must never share a folder");
                Assert.IsFalse(Directory.Exists(folder), "creating one touches no disk: that waits for the first open");

                // a name already taken is refused rather than quietly making a second "Second"
                await Assert.ThrowsExceptionAsync<AssertFailedException>(async () => await command(host, "database-create", new { name = "second", autoOpen = false }));

                var after = await command(host, "database-set-default", new { storeId = newId });
                var rows = prop(after, "databases").EnumerateArray().ToArray();
                Assert.IsTrue(prop(rows.Single(d => prop(d, "id").GetGuid() == newId), "isDefault").GetBoolean(), "the new one is the default now");
                Assert.IsFalse(prop(rows.Single(d => prop(d, "id").GetGuid() == firstId), "isDefault").GetBoolean(), "and the old one is not");
                Assert.AreEqual(newId, host.Settings.Settings.DefaultStoreId, "which is one id in the settings, nothing opened or moved");
            } finally {
                await host.DisposeAsync();
                try { Directory.Delete(root, true); } catch { }
            }
        }
    }
}
