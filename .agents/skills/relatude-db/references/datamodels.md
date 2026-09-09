# Data modelling reference

Full detail on declaring node types, properties, embedded data, references and relations. `SKILL.md` has the summary; this file is the authority.

## Contents

- [Namespaces to import](#namespaces-to-import)
- [Type-level attributes](#type-level-attributes)
- [Scalar property attributes](#scalar-property-attributes)
- [Marker properties](#marker-properties)
- [What lives in NodeMeta](#what-lives-in-nodemeta)
- [Geo coordinates](#geo-coordinates)
- [Files](#files)
- [Embedded data](#embedded-data)
- [References — lightweight pointers](#references--lightweight-pointers)
- [Relations — the graph edges](#relations--the-graph-edges)
- [Validation the model builder enforces](#validation-the-model-builder-enforces)
- [Native types you must not redefine](#native-types-you-must-not-redefine)

## Namespaces to import

```csharp
using Relatude.DB.Common;      // FileValue, GeoCoordinate, IdKey
using Relatude.DB.Datamodels;  // NodeMeta, RevisionType
using Relatude.DB.Nodes;       // attributes, relation bases, Reference, References, EmbeddedMap
```

## Type-level attributes

### `[Node]`

Optional on interfaces, classes, records and structs. Everything on it is opt-in tuning.

```csharp
[Node(
    Id = "6a1d9f2e-0b41-4c8a-9d7b-3f2c5e8a1b40", // stable type id, survives renames
    TextIndex = BoolValue.True,                   // include in the BM25 index
    SemanticIndex = BoolValue.False,              // skip the vector index
    TextIndexBoost = 1.5
)]
public interface IOrganizer { /* ... */ }
```

- `BoolValue` is tri-state: `Default` (engine decides), `True`, `False`.
- Also accepts `MinNoInstances` / `MaxNoInstances`, enforced at write time.
- **Without `Id`, the type id is a hash of the full type name.** Rename the type or move its namespace and the engine sees a brand-new type with no data. Pin ids at project start.

### `[Relation]`

```csharp
[Relation(
    Id = "9f4e2c11-77a3-4d5e-8b21-0c6f9a3e7d54",
    DisallowCircularReferences = true   // enforce acyclicity on self-referential relations
)]
public class VenueTree : OneToMany<IVenue, IVenue> {
    public class Parent : One { }
    public class Children : Many { }
}
```

Also accepts `SourceTypes` / `TargetTypes` as full type-name strings, but the generic parameters already carry that information — you rarely need them.

### `[Exclude]`

On a type or a property, tells the datamodel to skip it.

## Scalar property attributes

Declaring a property with no attribute at all works — the engine infers a property model from the CLR type. Attributes exist to add indexing, validation, faceting and search behaviour.

| CLR type | Attribute |
|---|---|
| `string` | `[StringProperty]` |
| `string` (rich text) | `[HtmlProperty]` — a `[StringProperty]` pre-set to `StringType = HTML` |
| `int` / enum | `[IntegerProperty]` |
| `long` | `[LongProperty]` |
| `double` | `[DoubleProperty]` |
| `float` | `[FloatProperty]` |
| `decimal` | `[DecimalProperty]` |
| `bool` | `[BooleanProperty]` |
| `Guid` | `[GuidProperty]` |
| `DateTime` | `[DateTimeProperty]` |
| `DateTimeOffset` | `[DateTimeOffsetProperty]` |
| `TimeSpan` | `[TimeSpanProperty]` |
| `GeoCoordinate` | `[GeoCoordinateProperty]` |
| `byte[]` | `[ByteArrayProperty]` |
| `float[]` | `[FloatArrayProperty]` |
| `string[]` | `[StringArrayProperty]` |
| `Guid[]` | `[GuidArrayProperty]` |
| `TEnum[]` | `[EnumArrayProperty]` |
| `FileValue` | `[FileProperty]` |

All of them inherit shared options from `PropertyAttribute`:

```csharp
public abstract class PropertyAttribute : Attribute {
    public string? Id { get; set; }              // stable property id, rename-proof
    public string? ReadAccess { get; set; }      // ACL slot
    public string? WriteAccess { get; set; }
    public bool ExcludeFromTextIndex { get; set; }
    public int TextIndexBoost { get; set; }
    public bool DisplayName { get; set; }
}
```

**`Indexed` is the flag that matters.** A property must be indexed to be filtered, sorted or faceted efficiently. Without it the engine scans.

### Strings

`[StringProperty]` is the richest of the set:

```csharp
[StringProperty(
    MinLength = 0,
    MaxLength = 4000,
    StringType = StringValueType.AnyString,   // AnyString | HTML | Url | Email | ...
    Indexed = true,                            // value index: equality, range, sort, StartsWith, Contains
    IndexedByWords = true,                     // BM25 full-text index; also what MatchesSearch needs
    IndexedBySemantic = true,                  // vector / semantic index
    PrefixSearch = true,                       // allow a "term*" wildcard in a search
    InfixSearch = false,                       // allow a "*term" wildcard in a search (expensive — opt in deliberately)
    PreloadWordIndex = false,
    MinWordLength = 3,
    MaxWordLength = 30,
    LegalValues = new[] { "draft", "published", "cancelled" },
    RegularExpression = @"^[a-z0-9-]+$",
    UniqueValues = true,
    IgnoreDuplicateEmptyValues = true,         // allow many empty values under UniqueValues
    NotFacet = false,                          // exclude from faceting even when indexed
    DefaultValue = ""
)]
public string Slug { get; set; } = string.Empty;
```

Use `[HtmlProperty]` for rich text so markup is stripped before word indexing:

```csharp
[HtmlProperty(IndexedByWords = true, IndexedBySemantic = true)]
public string Description { get; set; } = string.Empty;
```

### Numbers

Numeric attributes share `MinValue` / `MaxValue` / `DefaultValue` / `Indexed` / `NotFacet`, plus range-faceting controls:

```csharp
[IntegerProperty(MinValue = 0, MaxValue = 100000, Indexed = true)]
public int Capacity { get; set; }

[DoubleProperty(Indexed = true, FacetRangePowerBase = 2.0, FacetRangeCount = 8)]
public double AverageRating { get; set; }
```

`FacetRangePowerBase` and `FacetRangeCount` control how the engine auto-buckets a numeric property for a range facet.

**`decimal`, `DateTime`, `DateTimeOffset`, `TimeSpan` and `Guid` are not legal C# attribute parameter types**, so their bounds and defaults are passed as strings in a fixed format:

```csharp
// decimal — invariant culture
[DecimalProperty(MinValue = "0", MaxValue = "100000", DefaultValue = "0", Indexed = true)]
public decimal Price { get; set; }

// DateTime / DateTimeOffset — round-trip ("O") format
[DateTimeProperty(MinValue = "2000-01-01T00:00:00.0000000Z", Indexed = true)]
public DateTime StartsUtc { get; set; }

// TimeSpan — constant ("c") format
[TimeSpanProperty(MaxValue = "1.00:00:00", Indexed = true)]
public TimeSpan Duration { get; set; }

// Guid — plain string form
[GuidProperty(Indexed = true, UniqueValues = true)]
public Guid ExternalRef { get; set; }
```

### Enums

Declare the property as your enum type and use `[IntegerProperty]`. The engine stores it as an integer and auto-populates the enum metadata (`FullEnumTypeName`, `LegalValues`, `LegalValueNames`) so the admin UI and facets show names rather than numbers:

```csharp
public enum EventStatus { Draft = 0, Published = 1, SoldOut = 2, Cancelled = 3 }

[IntegerProperty(Indexed = true)]
public EventStatus Status { get; set; }

[EnumArrayProperty(Indexed = true)]
public AccessibilityFeature[] Accessibility { get; set; } = [];
```

### Validation happens on write

`LegalValues`, `RegularExpression`, `MinValue`/`MaxValue`, `MinLength`/`MaxLength`, `UniqueValues` and `MinNoInstances`/`MaxNoInstances` are all enforced by the engine at write time. A violating transaction **fails** rather than silently storing bad data.

## Marker properties

Six attributes tag a property as playing a special structural role. At most one property per type per role.

| Attribute | Meaning |
|---|---|
| `[DisplayNameProperty]` | The human-readable name. Surfaces in the admin UI, search highlighting and `Meta.DisplayName`. |
| `[AddressProperty]` | The URL slug / address. Used for routing and `Meta.Address`. |
| `[PublicIdProperty]` | The external id used in URLs and APIs. Defaults to `Id` (`Guid`). |
| `[InternalIdProperty]` | The internal `int` id. Defaults to `__Id`. |
| `[CreatedUtcProperty]` | Stamped with the creation time. |
| `[ChangedUtcProperty]` | Stamped with the last-change time. |

```csharp
[DisplayNameProperty]
[StringProperty(Indexed = true, IndexedByWords = true)]
public string Title { get; set; } = string.Empty;

[AddressProperty]
[StringProperty(Indexed = true, UniqueValues = true)]
public string Address { get; set; } = string.Empty;

[CreatedUtcProperty] public DateTime CreatedUtc { get; set; }
[ChangedUtcProperty] public DateTime ChangedUtc { get; set; }
```

Marker attributes must sit on a compatible type — `[DisplayNameProperty]` and `[AddressProperty]` on `string`, `[CreatedUtcProperty]` and `[ChangedUtcProperty]` on `DateTime`.

## What lives in NodeMeta

Every node carries system metadata. **Read it; never write it.** Never construct one — default to `NodeMeta.Empty`.

| Field | Meaning |
|---|---|
| `Id`, `InternalId` | Public `Guid` and internal `int` id. |
| `NodeTypeId` | The type id from `[Node(Id = …)]`. |
| `CreatedUtc`, `ChangedUtc` | Timestamps. |
| `DisplayName`, `Address` | Sourced from the marker properties above. |
| `CultureId` | Culture of this revision. |
| `RevisionId`, `RevisionType` | Revision tracking — `Draft`, `Published`, `Archived`, … |
| `CollectionId` | Logical grouping. |
| `ReadAccess`, `EditAccess`, `EditViewAccess`, `PublishAccess` | Guid-based ACL slots. |
| `CreatedBy`, `ChangedBy` | User Guids. |
| `ReleaseUtc`, `ExpireUtc` | Scheduled publishing. |
| `Deleted` | Soft-delete flag. |

## Geo coordinates

`GeoCoordinate` (in `Relatude.DB.Common`) is a first-class, indexable value type for WGS84 latitude/longitude. It is a `readonly struct`, so it costs nothing to pass around.

```csharp
[GeoCoordinateProperty(Indexed = true)]   // Indexed = true enables spatial query acceleration
GeoCoordinate Location { get; set; }
```

### Constructing and reading

```csharp
var oslo   = new GeoCoordinate(59.9139, 10.7522);
var bergen = new GeoCoordinate(60.3913,  5.3221);

double lat = oslo.Latitude;    // 59.9139… (NaN when empty)
double lon = oslo.Longitude;   // 10.7522… (NaN when empty)

Console.WriteLine(oslo);       // "59.9139, 10.7522"

GeoCoordinate.TryParse("59.9139, 10.7522", out var parsed);   // round-trips
```

### The empty value

`GeoCoordinate.Empty` is `default(GeoCoordinate)` and means "no location":

```csharp
var unknown = GeoCoordinate.Empty;
unknown.IsEmpty;                     // true
unknown.Latitude;                    // double.NaN
unknown.DistanceTo(oslo);            // double.PositiveInfinity
unknown.IsWithin(oslo, 100_000);     // false — never matches
```

Empty coordinates are **excluded from spatial indexes entirely**. This is what you want: a venue whose location has not been entered yet should never appear in a "within 5 km" search.

### Distance and radius tests

```csharp
double meters = oslo.DistanceTo(bergen);        // great-circle (haversine), in metres
bool near     = oslo.IsWithin(bergen, 500_000); // true — within 500 km
```

`IsWithin(center, meters)` is the important one: the query compiler recognises it inside a query lambda and accelerates it with the spatial index. See the geo section of `queries.md`.

### How it is stored, and what that implies

Coordinates snap to a ~1 cm grid (31 bits per axis) on construction and are stored as a 62-bit Morton / Z-order code. Three consequences:

1. Equality, hashing and ordering coincide exactly, and every value round-trips losslessly through `StorageValue` / `FromStorageValue`.
2. **Sort order follows the Z-order curve** — spatially coherent for index scans, meaningless as a user-facing sort. To sort by proximity, order by `DistanceTo` after materialising the page. Never `OrderBy(v => v.Location)`.
3. Radius searches over-scan slightly: the index cover is built from square Z-order cells and a circle is not square. The engine refines candidates with the exact haversine distance, so results are correct — but a very large radius touches more of the index than a small one.

### JSON shape

`GeoCoordinate` serialises as `{"latitude": 59.91, "longitude": 10.75}`, and `Empty` serialises as `null` so it survives a round trip. On read it also accepts `lat` / `lon` / `lng` aliases and a `"latitude, longitude"` string.

## Files

`FileValue` (in `Relatude.DB.Common`) is a slot into the file storage subsystem — local disk, Azure blob, and so on, configured separately in the admin UI. The property holds the reference; the bytes live in storage.

```csharp
[FileProperty]
public FileValue Photo { get; set; } = FileValue.Empty;

// pin a property to a specific storage provider (id comes from the admin UI):
[FileProperty(FileStorageProviderId = "b1c2d3e4-...")]
public FileValue Brochure { get; set; } = FileValue.Empty;
```

Uploading, serving and converting bytes: see `api-quickref.md`.

## Embedded data

Embedded objects are **owned sub-trees**. They are stored inline in the parent node, have no independent identity in the graph, and live and die with the parent. Reach for them when a value only makes sense in the context of its parent: opening hours on a venue, line items on an order, translations on a label.

### `Embedded<T>` — keyed by the embedded object's own `Guid Id`

```csharp
[EmbeddedProperty(IncludeTypes = IncludeTypeOptions.ThisTypeAndDescending)]
public Embedded<PriceTier> PriceTiers { get; set; } = [];
```

`IncludeTypeOptions` controls which subtypes are allowed in the slot.

### `EmbeddedMap<TKey, TValue>` — keyed by a property of the value

```csharp
public class OpeningHours {
    public Guid Id { get; set; }
    public string DayCode { get; set; } = string.Empty;   // "mon", "tue", …
    public TimeSpan Opens { get; set; }
    public TimeSpan Closes { get; set; }
}

public interface IVenue {
    [EmbeddedMapProperty(
        KeyProperty = nameof(OpeningHours.DayCode),
        KeyType = KeyPropertyType.NodeProperty)]   // or NodeGuidId / NodeIntegerId
    EmbeddedMap<string, OpeningHours> Hours { get; }
}
```

`KeyType` defaults to `NodeProperty` when you supply `KeyProperty`. Use `NodeGuidId` to key by the embedded value's `Guid Id` instead — which is exactly what `Embedded<T>` is shorthand for. The named key property must exist on the value type and its type must match the map's key type.

### Working with an embedded map

```csharp
var venue = db.Create<IVenue>();

var monday = new OpeningHours { DayCode = "mon", Opens = new(9, 0, 0), Closes = new(23, 0, 0) };
venue.Hours.Add(monday);

db.Insert(venue);

// only *after* the parent is persisted can you read by key:
var stored = db.Get<IVenue>(venue.Id);
var mon = stored.Hours["mon"];
int days = stored.Hours.Count();

foreach (var h in stored.Hours) {          // EmbeddedMap<TKey,TValue> is IEnumerable<TValue>
    Console.WriteLine($"{h.DayCode}: {h.Opens}–{h.Closes}");
}
```

**Gotcha:** before the parent is inserted, only `Add` is safe. Reading by key on an unpersisted parent will not find anything.

## References — lightweight pointers

A reference is a `Guid` (or an ordered `Guid[]`) stored directly on the node. It is a one-way pointer: cheap to store, cheap to set, and it does **not** create a reverse index.

| Type | Stores | Use for |
|---|---|---|
| `Reference<T>` | one `Guid` | "the cover image of this event" |
| `References<T>` | ordered `Guid[]`, **duplicates preserved** | "the sponsors of this event, where one organizer may appear at two billing tiers" |

Relations are ordered too, so ordering alone does not justify a reference — **duplicates and the absence of a reverse index are what distinguish `References<T>`**. See the comparison table in `modelling-patterns.md`.

```csharp
public interface IEvent {
    [ReferenceProperty(Indexed = true)]     // Indexed = true is required to filter / facet on it
    Reference<IMediaAsset> Cover { get; }

    [ReferencesProperty(Indexed = true)]
    References<IOrganizer> Sponsors { get; }
}
```

On a concrete class, initialise them:

```csharp
public Reference<IMediaAsset> Cover { get; set; } = new();
public References<IOrganizer> Sponsors { get; set; } = new();
```

### Reading and writing a `Reference<T>`

```csharp
var ev = db.Get<IEvent>(eventId);

ev.Cover.IsSet();                       // is a target set at all?
ev.Cover.Id;                            // the raw Guid (Guid.Empty when unset)

if (ev.Cover.TryGet(out var asset)) {   // lazily loads the target
    Console.WriteLine(asset.FileName);
}

var img = ev.Cover.Get();               // throws when unset

ev.Cover.Set(someAssetId);              // by id
ev.Cover.Set(someAsset, db);            // by instance
ev.Cover.Clear();
db.Update(ev);                          // references are node data — persist with a normal Update
```

### Reading and writing a `References<T>`

```csharp
ev.Sponsors.Ids;                        // Guid[] in stored order
ev.Sponsors.Count();
ev.Sponsors.Contains(organizerId);

ev.Sponsors.Add(organizerId);
ev.Sponsors.Add(organizerNode, db);
ev.Sponsors.Remove(organizerId);        // removes every occurrence
ev.Sponsors.Clear();
ev.Sponsors.Ids = new[] { a, b, c };    // replace wholesale, order preserved
db.Update(ev);

foreach (var sponsor in ev.Sponsors.Get()) {   // lazily loads every live target, in order
    Console.WriteLine(sponsor.Name);
}
```

### The enumeration trap

Both `Reference<T>` and `References<T>` implement `IEnumerable<T>`, but **`foreach` only yields preloaded data**. If you did not `.Preload(...)` in the query, the `foreach` silently yields nothing. Use `.Get()` / `.TryGet(out …)` for lazy loading, and `foreach` only after a preload. This is a deliberate design: it makes the N+1 query cost impossible to incur by accident.

Stale targets — deleted nodes, or nodes of the wrong type — are **skipped** by `References<T>.Get()` rather than throwing. With many references per value, stale entries are routine and the engine treats them as such.

## Relations — the graph edges

A relation is a real, **bidirectional, indexed, ordered** edge. Relating A to B automatically relates B back to A. Relations are what make this a graph database: they are traversable, filterable and countable without loading either side. Each node's related items are held in a fixed order per side, so a "many" side is an ordered list rather than a set.

**Relations are not foreign keys.** You never store `VenueId` on an event. You declare a relation class and expose one nested property class per side.

### The five shapes

```csharp
// 1. Symmetric 1:1 — "spouse". Both sides are the same property.
public class PairedWith : OneOne<IVenue> {
    public class Pair : One { }
}

// 2. Directional 1:1 — "husband ↔ wife".
public class PrimaryContact : OneToOne<IOrganizer, IAttendee> {
    public class Organizer : OneFrom { }
    public class Contact : OneTo { }
}

// 3. Directional 1:N — "parent ↔ children". The workhorse.
public class EventsAtVenue : OneToMany<IVenue, IEvent> {
    public class Venue : One { }      // goes on IEvent — the "one" side
    public class Events : Many { }    // goes on IVenue — the "many" side
}

// 4. Symmetric N:N — "friends".
public class Friends : ManyMany<IAttendee> {
    public class Peers : Many { }
}

// 5. Directional N:N — "teachers ↔ students".
public class Attendance : ManyToMany<IEvent, IAttendee> {
    public class Events : ManyFrom { }      // goes on IAttendee
    public class Attendees : ManyTo { }     // goes on IEvent
}
```

| Base class | Symmetry | Cardinality | Nested classes available |
|---|---|---|---|
| `OneOne<T>` | symmetric | 1↔1, same type | `One` |
| `OneToOne<TFrom, TTo>` | directional | 1↔1 | `OneFrom`, `OneTo` |
| `OneToMany<TOne, TMany>` | directional | 1↔N | `One`, `Many` |
| `ManyMany<T>` | symmetric | N↔N, same type | `Many` |
| `ManyToMany<TFrom, TTo>` | directional | N↔N | `ManyFrom`, `ManyTo` |

There is **no zero-or-one and no asymmetric many-to-one**. Model the asymmetric case as `OneToMany` in the appropriate direction. If a relationship genuinely does not fit — for instance because the edge itself carries data like a ticket price or a role — **promote the edge to a node type** with two relations hanging off it.

### Using them on node types

Each nested class becomes a property type. Give the property whatever name reads best:

```csharp
public interface IVenue {
    EventsAtVenue.Events Events { get; }        // many events at this venue
}

public interface IEvent {
    EventsAtVenue.Venue Venue { get; }          // the one venue of this event
    Attendance.Attendees Attendees { get; }     // many attendees
}

public interface IAttendee {
    Attendance.Events Attending { get; }
    Friends.Peers Friends { get; }
}
```

On concrete classes, initialise with `new()`:

```csharp
public EventsAtVenue.Events Events { get; set; } = new();
```

You may omit a side you do not need. Declare both only when you want navigation in both directions.

### The "one" side API — `OneProperty<T>`

```csharp
var ev = db.Get<IEvent>(id);

ev.Venue.IsSet();                       // is there a related node?
ev.Venue.Count();                       // 0 or 1
var venue = ev.Venue.Get();             // throws when unset
if (ev.Venue.TryGet(out var v)) { … }   // safe probe
ev.Venue.Contains(someVenueId);
```

### The "many" side API — `ManyProperty<T>`

```csharp
var venue = db.Get<IVenue>(id);

int n = venue.Events.Count();           // counted from the index — does not load the nodes
bool has = venue.Events.Contains(evId);

foreach (var e in venue.Events) { … }   // enumerates; loads lazily if not preloaded
var all = venue.Events.Get();           // IEnumerable<IEvent>

// …or keep composing as a real query, which is what you want for anything non-trivial:
var upcoming = venue.Events
    .Query()
    .Where(e => e.StartsUtc > DateTime.UtcNow)
    .OrderBy(e => e.StartsUtc)
    .Page(0, 20)
    .Execute();
```

`ManyProperty<T>.Query()` returns a full `IQueryOfNodes` rooted at that relation, so everything in `queries.md` applies to it.

Unlike `Reference<T>`, `foreach` over a `Many` side **does** load lazily when nothing was preloaded. It is still worth using `.Include(...)` when iterating many parents.

### Relation lists are ordered

Each node's related items are held in a fixed-order list per side. Consequences worth designing around:

- **`Relate` appends to the bottom.** The newest related item is last.
- **Enumeration follows the stored order.** `foreach`, `Get()` and preloaded includes all yield that order, so a curated sequence survives a round trip without a `SortIndex` property.
- **Duplicates are rejected.** Relating a pair that is already related throws, as does unrelating a pair that is not. Order plus no duplicates is exactly list semantics over a set of distinct targets.
- **Each side is ordered independently.** In a many-to-many relation, the order of targets on a source and the order of sources on a target are two separate orderings. Reordering one says nothing about the other.
- **One-sided relations have nothing to order** — a `One` / `OneFrom` / `OneTo` side holds at most one item, so ordering is a `Many`-side concept.

Reorder with the `MoveRelation…` family — full overload list in `api-quickref.md`:

```csharp
db.MoveRelationToTop<IVenue>(venue, v => v.Events, headliner);
db.MoveRelationToBottom<IVenue>(venue, v => v.Events, lateAddition);
db.MoveRelation<IVenue>(venue, v => v.Events, ev, offset: -1);         // negative = toward the top
db.MoveRelationBefore<IVenue>(venue, v => v.Events, ev, anchor: other);
db.MoveRelationAfter<IVenue>(venue, v => v.Events, ev, anchor: other);
db.SetRelationOrder<IVenue>(venue, v => v.Events, orderedEvents);      // replace the whole order
```

### Feeding the parent's text index from a relation

This is how you make a venue findable by the names of the events held there:

```csharp
[RelationProperty(
    TextIndexRelatedDisplayName = true,
    TextIndexRelatedContent = false,
    TextIndexRecursiveLevelLimit = 1,
    Facet = true                          // opt in to faceting on this relation
)]
public EventsAtVenue.Events Events { get; }
```

Relation properties need `Facet = true` to be facetable.

## Validation the model builder enforces

**At build time** — you find out on startup:

- Non-interface node types must have a parameterless constructor.
- Two parent interfaces may not declare the same property name.
- Two classes may not declare the same property name — overriding is not supported.
- Nullable value types (`int?`, `DateTime?`, `GeoCoordinate?`) are not supported.
- Only these value types are allowed: `bool`, `byte`, `int`, `long`, `double`, `float`, `decimal`, `DateTime`, `DateTimeOffset`, `Guid`, `TimeSpan`, `GeoCoordinate` and any enum.
- `Id` must be `Guid` or `string`; the internal id must be `int`, `long` or `string`.
- Marker attributes must sit on a compatible type.
- Two types with the same `[Node(Id = …)]` Guid but different full names → error.
- A relation class must have the right number of nested side classes for its shape.

**At write time** — the transaction fails:

- `LegalValues` / `RegularExpression` / min/max / length bounds.
- `MinNoInstances` / `MaxNoInstances` per type.
- `UniqueValues = true` uniqueness across the type.
- `DisallowCircularReferences = true` acyclicity on self-referential relations.

## Native types you must not redefine

The engine ships its own model in `Relatude.DB.Native.Models` — `ISystemUser`, `ISystemUserGroup`, `ISystemCollection`, `ISystemCulture`. They back the admin UI, auth and culture handling. **Do not redefine them.** If your domain needs a "user", model your own type and relate it to `ISystemUser` if you need the link.
