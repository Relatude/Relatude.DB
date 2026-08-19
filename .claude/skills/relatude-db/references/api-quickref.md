# NodeStore + Transaction cheatsheet

The write surface. `db` is `ctx.Database`, a `NodeStore`. Query methods are in `queries.md`.

## Contents

- [Create and insert](#create-and-insert)
- [Read](#read)
- [Update, upsert, delete](#update-upsert-delete)
- [The suffix convention](#the-suffix-convention)
- [Relating nodes](#relating-nodes)
- [Reordering related items](#reordering-related-items)
- [Transactions](#transactions)
- [Locks](#locks)
- [Reverting: rollback to an earlier point](#reverting-rollback-to-an-earlier-point)
- [Transaction plugins](#transaction-plugins)
- [Uploading files](#uploading-files)
- [Serving and converting files](#serving-and-converting-files)

Files get a reference file of their own — `files-and-media.md` — covering chunked uploads, the full `FileAdjustment` catalogue, URL options and the download middleware. What follows here is the short version.

## Create and insert

```csharp
// CREATE — hands you a proxy that tracks changes
var venue = db.Create<IVenue>();
venue.Name = "Sentrum Scene";
venue.Slug = "sentrum-scene";
venue.Location = new GeoCoordinate(59.9200, 10.7480);
venue.Capacity = 1750;
venue.Kind = VenueKind.Indoor;
venue.IsAccessible = true;

db.Insert(venue);                    // returns TransactionResult

// …or in one call
var ev = db.CreateAndInsert<IEvent>((e, t) => {
    e.Title = "Winter Session";
    e.StartsUtc = new DateTime(2026, 11, 14, 19, 0, 0, DateTimeKind.Utc);
    e.Duration = TimeSpan.FromHours(3);
    e.Price = 450m;
    e.Status = EventStatus.Published;
    e.Tags = ["live", "electronic"];
});
```

`Insert(node, ignoreRelated: true)` tells the engine **not** to walk relation properties looking for cascading inserts — useful when you have already inserted the related nodes yourself.

## Read

```csharp
var byGuid = db.Get<IVenue>(venueId);
var byInt  = db.Get<IVenue>(1234);                      // internal int id
var many   = db.Get<IVenue>(new[] { id1, id2 });
var nb     = db.Get<IVenue>(venueId, "nb-NO");          // a specific culture

if (db.TryGet<IVenue>(venueId, out var maybe)) { … }    // no throw

var refreshed = db.Get(venue);                          // re-fetch a known node

long total  = db.Count();
long venues = db.Count<IVenue>();
```

`Get` throws when the id is missing; `TryGet` returns `false`.

## Update, upsert, delete

Every mutating call returns a `TransactionResult` and accepts `flushToDisk: bool = false`. The default is queued/async, which is what you want in hot paths; pass `true` to force a disk sync before returning.

```csharp
venue.Capacity = 1800;
db.Update(venue);

db.Upsert(venue);            // insert or update, with a change comparison
db.ForceUpsert(venue);       // insert or update, skip the comparison
db.ForceUpdate(venue);       // always write, even if nothing changed
db.UpdateIfExists(venue);    // no-op when missing
db.UpdateOrFail(venue);      // throw when missing
db.InsertIfNotExists(venue);
db.InsertOrFail(venue);

db.Delete(venueId);
db.DeleteIfExists(venueId);
db.DeleteOrFail(venueId);
db.Delete(new[] { id1, id2 });
```

## The suffix convention

Consistent across the whole API:

| Suffix | Behaviour when the precondition fails |
|---|---|
| (none) / `OrFail` | throw |
| `IfExists` / `IfNotExists` | no-op |
| `Force…` | skip change detection and write anyway |

## Relating nodes

```csharp
// By expression — readable and type-checked. Preferred.
db.Relate<IVenue>(venue, v => v.Events, ev);

// By ids
db.Relate<IVenue>(venueId, v => v.Events, eventId);
db.Relate<IVenue>(venueId, v => v.Events, new[] { eventId1, eventId2 });

// Symmetric relations only need to be stated once
db.Relate<IAttendee>(alice, a => a.Friends, bob);   // bob.Friends now contains alice

// Remove
db.UnRelate<IVenue>(venue, v => v.Events, ev);

// Probe
bool related = db.RelationExists<IVenue>(venueId, v => v.Events, eventId);
```

Because relations are bidirectional, **it does not matter which side you relate from** — `db.Relate<IEvent>(ev, e => e.Venue, venue)` has exactly the same effect as the first line above.

References are node data, not edges, so they persist with a normal `Update` rather than `Relate`:

```csharp
ev.Cover.Set(assetId);
ev.Sponsors.Add(organizerId);
db.Update(ev);
```

Note that `Relate` **appends to the bottom** of the target's relation list, and relating a pair that is already related **throws** (as does unrelating a pair that is not). Use the reorder methods below to change position rather than un-relating and re-relating.

## Reordering related items

Each node's related items are an ordered list per side, and six method families reorder it. Every one exists on `NodeStore` — returning `TransactionResult`, taking `flushToDisk: bool = false` — and on `Transaction`, where it returns the `Transaction` so calls chain.

```csharp
// offset: negative moves toward the top, positive toward the bottom
db.MoveRelation<IVenue>(venue, v => v.Events, ev, offset: -1);

db.MoveRelationToTop<IVenue>(venue, v => v.Events, headliner);
db.MoveRelationToBottom<IVenue>(venue, v => v.Events, lateAddition);

// anchor is another item already in the same list
db.MoveRelationBefore<IVenue>(venue, v => v.Events, ev, anchor: other);
db.MoveRelationAfter<IVenue>(venue, v => v.Events, ev, anchor: other);

// replace the whole order — itemsInOrder must contain exactly the currently related ids
db.SetRelationOrder<IVenue>(venue, v => v.Events, orderedEvents);
```

Chained in a transaction:

```csharp
var t = db.CreateTransaction();
t.Relate<IVenue>(venue, v => v.Events, ev)
 .MoveRelationToTop<IVenue>(venue, v => v.Events, ev)
 .Execute();
```

### Semantics

- **Multi-item moves behave like a list UI.** Pass a collection instead of a single item and the selection keeps its internal order and compacts against the ends of the list.
- **Positions are clamped.** Moving past the top or bottom never throws.
- **Each side is ordered independently.** In a many-to-many relation, reordering the source's targets does not touch the target's list of sources.
- `SetRelationOrder` requires the supplied ids to be **exactly** the currently related ids — it reorders, it does not add or remove.

### Overload shapes

Each family takes one item or an `IEnumerable` of items, in three addressing styles:

```csharp
MoveRelationToTop<T>(T fromNode, Expression<Func<T, object?>> expression, object item)
MoveRelationToTop<T>(Guid fromId,  Expression<Func<T, object?>> expression, Guid item)
MoveRelationToTop  (Guid fromId,  Guid propertyId,                         Guid item)
```

`MoveRelation` adds `int offset`; `MoveRelationBefore` / `MoveRelationAfter` add an `anchor` of the same shape as `item`; `SetRelationOrder` takes only `itemsInOrder`. On `Transaction` the same families also accept `int` internal ids. Prefer the expression form — it is readable and type-checked.

For raw relation-id access there is `TransactionRelation.Move(relationId, owner, items, offset, reorderSourcesOfTarget = false)`, where `reorderSourcesOfTarget: true` reorders a target's list of sources instead of the owner's list of targets. Only meaningful for many-to-many, and rarely what you want from application code.

## Transactions

`Transaction` mirrors every mutating call on `NodeStore` — same names, same `OrFail` / `IfExists` / `Force…` variants — and commits them together:

```csharp
var t = db.CreateTransaction();

t.Insert(venue);
t.Insert(ev);
t.Relate<IVenue>(venue, v => v.Events, ev);
t.Relate<IEvent>(ev, e => e.Host, organizer);
t.Update(organizer);

TransactionResult result = t.Execute();
```

`TransactionResult` carries the per-operation outcomes, the ids generated by inserts, and timing.

## Locks

For a workflow that needs stricter isolation:

```csharp
var lockId = db.RequestLock(venue, lockDurationInMs: 10_000, maxWaitTimeInMs: 10_000);
// …do the work…            // the lock expires on its own; request again to extend

if (db.TryRequestLock(venueId, out var id)) { … }

var globalLockId = db.RequestGlobalLock(1000, 1000);   // for maintenance windows
```

## Reverting: rollback to an earlier point

For experiments, tests and seeding: put the database back by **permanently deleting every
transaction after a point in time** — the log is truncated as if they never happened. Two forms.

**The revert window** is the cheap, planned form. While it is active the store suspends state
snapshots, engine durability and log rewrites, so the rollback is a log truncation plus reload —
no index rebuild (exception: the SQLite index engine is durable per transaction and is reset and
rebuilt). Keep windows short-lived; closing the store ends the window as a commit.

```csharp
long ts = db.BeginRevertWindow();       // marks the rollback target (flushes + snapshots state first)
// …experiment freely: insert, update, delete, query…
db.RollbackRevertWindow();              // discard everything since Begin — restores the exact state
// …or db.CommitRevertWindow();         // keep everything, resume normal persistence
db.RevertWindow                          // the active window (RevertWindowInfo), or null
```

**DeleteTransactionsAfter** is the general form against any remembered timestamp — correct on any
store, but persisted state that advanced past the point (state snapshot, index engines) is reset
and rebuilt from the log, which can be slow on a large database:

```csharp
long ts = db.Timestamp;                                  // remember BEFORE the changes
// …changes…
var preview = db.DeleteTransactionsAfter(ts, dryRun: true);   // counts only, changes nothing
var result  = db.DeleteTransactionsAfter(ts);                 // truncate + reload
```

Both return a `DeleteTransactionsResult` (transactions/actions deleted, bytes truncated, which
engines were reset). Files uploaded by deleted transactions are not removed from the file store.
The CLI wraps the general form: `relatude timestamp` prints the head as a bare number, and
`relatude revert --after <timestamp> [--dry-run|--yes]` reverts a database from the outside
(the application must not be running).

## Older versions of a node

Every write appends the full node to the transaction log, linked to the node's previous version, so
strictly older versions can be read back — newest first, straight from the log files, never cached:

```csharp
NodeVersion<Article>[] history = store.FindOlderVersions<Article>(articleId);        // default max 100
foreach (var v in history) Console.WriteLine(v.EstimatedCreationUtc + ": " + v.Node.Title);
v.Timestamp   // log timestamp of the transaction that wrote the version
v.Source      // which log file it was read from (primary or secondary)
```

The primary log holds versions **since the last log rewrite** (a rewrite compacts history away by
design); with `SecondaryBackupLog` enabled the secondary log survives rewrites and extends the
reach. The current version is not included, deleted nodes are not supported, and relations are not
part of node data. Requires log file format 1001 — older files keep working but carry no version
chains until the next rewrite (primary) or reset (secondary) upgrades them.

## Transaction plugins

Cross-cutting concerns — audit trails, derived properties, computed timestamps — belong in a transaction plugin rather than scattered through your call sites:

```csharp
db.RegisterTransactionPlugin(myPlugin);   // INodeTransactionPlugin: BeforeExecute / AfterExecute
```

`BeforeExecute` can inspect, veto or augment the transaction before it commits.

## Uploading files

The node must already be stored — `FileValue.PropertyPath` is `null` until then, and every upload path throws on it.

```csharp
// From a local path, by expression — the readable form
await db.FileUploadAsync<IVenue>(venue, v => v.Photo, @"/tmp/sentrum.jpg");

// From a stream or byte array
await db.FileUploadAsync<IVenue>(venue, v => v.Photo, stream, "sentrum.jpg");
await db.FileUploadAsync<IVenue>(venue, v => v.Photo, bytes, "sentrum.jpg");

// When you already hold the FileValue slot
await db.FileUploadAsync(venue.Photo, @"/tmp/sentrum.jpg");
```

The upload writes the bytes *and* the `FileValue` on the node, so there is no `Update` to make afterwards.

### Very large files

```csharp
if (db.FileStoreSupportsMultipartUploads(venue.Photo)) {
    var uploadId = await db.InitiateMultipartUploadAsync(venue.Photo, "walkthrough.mp4");
    while (/* more data */) {
        await db.AppendMultipartUploadAsync(uploadId, buffer, length);   // strictly in order
    }
    FileValue value = await db.FinalizeMultipartUploadAsync(uploadId);
    // …or, on failure: await db.CancelMultipartUploadAsync(uploadId);
}
```

Chunks must be appended in order, the session lives in memory on that store instance, and it expires after 10 minutes idle. Full treatment, plus the HTTP endpoints and the browser side: `files-and-media.md`.

## Serving and converting files

Ask the store for a URL, describing the output you want. **Conversion runs asynchronously**, so check readiness rather than assuming the URL is immediately serveable.

```csharp
var path = venue.Photo.PropertyPath!;

var adjustment = new FileAdjustmentImage {
    Width = 1200,
    Height = 630,
    CropMode = ImageCropMode.Fill,
    RequestedFormat = FileFormat.Webp,
    Quality = 80
};

var url = db.GetUrl(venue.Photo, adjustment);      // or db.Datastore.GetUrl(path, adjustment)

bool ready = db.IsFileReady(path, adjustment, requestIfNot: true);
db.EnsureConversionRequested(path, adjustment);

if (db.TryGetConversionInfo(path, adjustment, queueConversionIfNotRequested: true, out var progress)) { … }
```

`FileAdjustmentVideo` is the equivalent for video, with `TargetBitRateInMbps`, `RequestedFormat = FileFormat.Mp4`, and so on. `FileAdjustmentMeta` returns conversion status and metadata as JSON.

Image and video converters are registered at startup — see `setup.md`. Serving the URL is a middleware you write — see `files-and-media.md`.
