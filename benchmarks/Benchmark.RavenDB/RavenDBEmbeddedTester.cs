using Benchmark.Base;
using Raven.Client;
using Raven.Client.Documents;
using Raven.Client.Documents.Conventions;
using Raven.Client.Documents.Indexes;
using Raven.Client.Documents.Linq;
using Raven.Client.Documents.Operations;
using Raven.Client.Documents.Queries;
using Raven.Client.Documents.Session;
using Raven.Embedded;

namespace Benchmark.RavenDB;

public enum RavenIndexingMode {
    /// <summary>
    /// Writes and queries wait for the RavenDB indexes to catch up, so a call only returns when its
    /// result is visible to queries. This is what the other databases in this benchmark do, as they
    /// all update their indexes as a part of the write transaction.
    /// </summary>
    Synchronous,
    /// <summary>
    /// RavenDB default: indexes are updated in the background. Writes return before their changes are
    /// visible to queries and queries may return stale results. Faster, but not comparable to the
    /// other databases tested here.
    /// </summary>
    Eventual,
}

/// <summary>
/// RavenDB Embedded runs as a separate server process talking HTTP over loopback, and it indexes
/// asynchronously. To keep the numbers comparable with the other testers, indexing time is included in
/// the timed calls by default ( see <see cref="RavenIndexingMode"/> ), and starting and stopping the
/// server process is included in Open and Close, as that is what opening and closing this database costs.
/// Documents are modelled the RavenDB way: the related document id is stored on the document itself,
/// and queried properties are covered by a static index.
/// </summary>
public class RavenDBEmbeddedTester : ITester {
    const int _patchBatchSize = 1024; // number of patches sent to the server in one transaction
    const string _userIdPrefix = "users/";
    const string _companyIdPrefix = "companies/";
    const string _documentIdPrefix = "documents/";
    static readonly TimeSpan _indexTimeout = TimeSpan.FromMinutes(5);
    static readonly TimeSpan _operationTimeout = TimeSpan.FromMinutes(5);
    static readonly TimeSpan _serverStartupTimeout = TimeSpan.FromMinutes(2);
    static readonly string _usersByAgeIndexName = new Users_ByAge().IndexName;

    // EmbeddedServer.Instance is a process wide singleton, so only one server and one store can be
    // in use at any time. They are kept in static fields to be able to clean up after an aborted run:
    static bool _serverRunning;
    static IDocumentStore? _openStore;

    readonly RavenIndexingMode _indexingMode;
    readonly string _databaseName;
    string _dataFolderPath = null!;

    public RavenDBEmbeddedTester(RavenIndexingMode indexingMode = RavenIndexingMode.Synchronous) {
        _indexingMode = indexingMode;
        _databaseName = indexingMode == RavenIndexingMode.Synchronous ? "benchmark" : "benchmark_eventual";
    }
    public string Name => _indexingMode == RavenIndexingMode.Synchronous ? "Raven DB Embedded" : "Raven DB Embedded, eventual indexing";
    bool _waitForIndexes => _indexingMode == RavenIndexingMode.Synchronous;
    IDocumentStore _store => _openStore ?? throw new InvalidOperationException("The database is not open. ");

    public void Initalize(string dataFolderPath, TestOptions options) {
        _dataFolderPath = dataFolderPath;
    }
    public void Open() {
        Directory.CreateDirectory(_dataFolderPath);
        startServer();
        // GetDocumentStore blocks until the server process is up, creates the database if it is
        // missing and returns an initialized store, so Open covers the full startup cost:
        _openStore = EmbeddedServer.Instance.GetDocumentStore(new DatabaseOptions(_databaseName) {
            Conventions = createConventions(),
        });
    }
    public void CreateSchema() {
        // RavenDB is schemaless, but filtered queries need an index. Deploying it up front, before any
        // data is written, is the equivalent of creating tables and indexes in the other testers:
        new Users_ByAge().Execute(_store);
    }
    public void InsertUsers(TestUser[] users) {
        using (var bulk = _store.BulkInsert()) {
            foreach (var user in users) bulk.Store(toDoc(user), userDocId(user.Id));
        }
        waitForIndexes(); // users are indexed by age
    }
    public void InsertCompanies(TestCompany[] companies) {
        using (var bulk = _store.BulkInsert()) {
            foreach (var company in companies) bulk.Store(toDoc(company), companyDocId(company.Id));
        }
        // no index covers companies, so there is nothing to wait for
    }
    public void InsertDocuments(TestDocument[] documents) {
        using (var bulk = _store.BulkInsert()) {
            foreach (var document in documents) bulk.Store(toDoc(document), documentDocId(document.Id));
        }
        // no index covers documents, so there is nothing to wait for
    }
    public void RelateUsersToCompanies(IEnumerable<Tuple<Guid, Guid>> relations) {
        // in a document database the relation is the id of the related document, stored on the
        // document itself. A patch lets the server update it without loading the document first:
        patchInBatches(relations, (session, relation) => session.Advanced.Patch<UserDoc, string?>(
            userDocId(relation.Item1), u => u.CompanyId, companyDocId(relation.Item2)));
        waitForIndexes(); // every user document is rewritten, so all of them are re - indexed
    }
    public void RelateDocumentsToUsers(IEnumerable<Tuple<Guid, Guid>> relations) {
        patchInBatches(relations, (session, relation) => session.Advanced.Patch<DocumentDoc, string?>(
            documentDocId(relation.Item1), d => d.AuthorId, userDocId(relation.Item2)));
        // no index covers documents, so there is nothing to wait for
    }
    public TestUser[] GetAllUsers() {
        using var session = _store.OpenSession();
        // a query without any filter reads the collection directly and can never be stale:
        var docs = session.Query<UserDoc>().Customize(c => c.NoTracking()).ToArray();
        return Array.ConvertAll(docs, fromDoc);
    }
    public TestUser? GetUserById(Guid id) {
        using var session = _store.OpenSession();
        var doc = session.Load<UserDoc>(userDocId(id));
        return doc == null ? null : fromDoc(doc);
    }
    public int CountUsersOfAge(int age) {
        using var session = _store.OpenSession();
        return usersOfAge(session, age).Count(); // counted server side, the documents are not read
    }
    public TestUser[] GetUsersAtAge(int age) {
        using var session = _store.OpenSession();
        var docs = usersOfAge(session, age).ToArray();
        return Array.ConvertAll(docs, fromDoc);
    }
    public void UpdateUserAge(Guid userId, int newAge) {
        using var session = _store.OpenSession();
        // the server holds the response until the age index includes the new value:
        if (_waitForIndexes) session.Advanced.WaitForIndexesAfterSaveChanges(_indexTimeout, throwOnTimeout: true);
        session.Advanced.Patch<UserDoc, int>(userDocId(userId), u => u.Age, newAge);
        session.SaveChanges();
    }
    public void DeleteUsersOfAge(int age) {
        var query = new IndexQuery {
            Query = $"from index '{_usersByAgeIndexName}' where Age = $age",
            QueryParameters = new Parameters { { "age", age } },
        };
        var options = new QueryOperationOptions { AllowStale = !_waitForIndexes };
        if (_waitForIndexes) options.StaleTimeout = _indexTimeout; // wait for the index instead of deleting by a stale one
        var operation = _store.Operations.Send(new DeleteByQueryOperation(query, options));
        operation.WaitForCompletion(_operationTimeout); // the delete itself runs as a server side operation
        waitForIndexes(); // and the index has to catch up with the deleted documents
    }
    public void FlushToDisk() {
        // RavenDB writes every transaction to its journal before acknowledging it and there is no
        // client API to force an additional flush, so there is nothing to do here.
    }
    public void Close() {
        // shutting the server process down is what closing an embedded RavenDB costs. It is a fixed
        // cost of several seconds, it does not depend on the amount of data written:
        stopServer();
    }
    public void DeleteDataFiles() {
        stopServer(); // the server locks its files while it is running
        for (var attempt = 0; Directory.Exists(_dataFolderPath); attempt++) {
            try {
                Directory.Delete(_dataFolderPath, true);
            } catch when (attempt < 20) {
                Thread.Sleep(100); // the server process may not have released every file yet
            }
        }
    }

    // ---- server and store ----

    void startServer() {
        stopServer(); // the singleton may still run after an aborted run, holding a lock on its data folder
        var options = new ServerOptions {
            DataDirectory = _dataFolderPath,
            LogsPath = Path.Combine(_dataFolderPath, "Logs"),
            MaxServerStartupTimeDuration = _serverStartupTimeout,
        };
        options.Licensing.DisableAutoUpdate = true; // no license traffic to the outside world while benchmarking
        options.Licensing.DisableAutoUpdateFromApi = true;
        EmbeddedServer.Instance.StartServer(options); // lazy, the process is started on first use
        _serverRunning = true;
    }
    static void stopServer() {
        if (_openStore != null) {
            _openStore.Dispose(); // also drops it from the store cache of the embedded server
            _openStore = null;
        }
        if (!_serverRunning) return;
        EmbeddedServer.Instance.Dispose();
        _serverRunning = false;
    }
    DocumentConventions createConventions() => new() {
        MaxNumberOfRequestsPerSession = _patchBatchSize * 2,
        WaitForIndexesAfterSaveChangesTimeout = _indexTimeout,
        WaitForNonStaleResultsTimeout = _indexTimeout,
        FindCollectionName = type =>
            type == typeof(UserDoc) ? "Users" :
            type == typeof(CompanyDoc) ? "Companies" :
            type == typeof(DocumentDoc) ? "Documents" :
            DocumentConventions.DefaultGetCollectionName(type),
    };

    // ---- queries and indexing ----

    IRavenQueryable<UserDoc> usersOfAge(IDocumentSession session, int age) {
        return session.Query<UserDoc, Users_ByAge>()
            .Customize(c => {
                c.NoTracking(); // read only, no need for the change tracker of the session
                // the server holds the response until the index has caught up with every write so far:
                if (_waitForIndexes) c.WaitForNonStaleResults(_indexTimeout);
            })
            .Where(u => u.Age == age);
    }
    void waitForIndexes() {
        if (!_waitForIndexes) return;
        using var session = _store.OpenSession();
        // an empty page of results, only used to let the server wait until the index is up to date:
        session.Advanced.DocumentQuery<UserDoc, Users_ByAge>()
            .WaitForNonStaleResults(_indexTimeout)
            .Take(0)
            .ToList();
    }
    void patchInBatches(IEnumerable<Tuple<Guid, Guid>> relations, Action<IDocumentSession, Tuple<Guid, Guid>> patch) {
        var session = _store.OpenSession();
        try {
            var count = 0;
            foreach (var relation in relations) {
                patch(session, relation);
                if (++count % _patchBatchSize > 0) continue;
                session.SaveChanges(); // one transaction and one request per batch
                session.Dispose();
                session = _store.OpenSession();
            }
            session.SaveChanges();
        } finally {
            session.Dispose();
        }
    }

    // ---- documents ----

    static string userDocId(Guid id) => _userIdPrefix + id;
    static string companyDocId(Guid id) => _companyIdPrefix + id;
    static string documentDocId(Guid id) => _documentIdPrefix + id;
    static Guid guidOfDocId(string docId, string prefix) => Guid.Parse(docId.AsSpan(prefix.Length));
    static UserDoc toDoc(TestUser user) => new() { Name = user.Name, Age = user.Age };
    static CompanyDoc toDoc(TestCompany company) => new() { Name = company.Name };
    static DocumentDoc toDoc(TestDocument document) => new() { Title = document.Title, Content = document.Content };
    static TestUser fromDoc(UserDoc doc) => new() {
        Id = guidOfDocId(doc.Id, _userIdPrefix),
        Name = doc.Name,
        Age = doc.Age,
    };

    // The Id property holds the RavenDB document id, it is stored as the id of the document and not as
    // a part of its content. Relations are the ids of the related documents:
    class UserDoc {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public int Age { get; set; }
        public string? CompanyId { get; set; }
    }
    class CompanyDoc {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
    }
    class DocumentDoc {
        public string Id { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string Content { get; set; } = string.Empty;
        public string? AuthorId { get; set; }
    }
    class Users_ByAge : AbstractIndexCreationTask<UserDoc> {
        public Users_ByAge() {
            Map = users => from user in users select new { user.Age };
        }
    }
}
