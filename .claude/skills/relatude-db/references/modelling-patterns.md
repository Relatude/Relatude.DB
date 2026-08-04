# Modelling patterns

How to shape a real Relatude.DB model. Read this before designing a domain with more than two or three node types — the composition decisions made here are what determine whether querying is one indexed call or N calls merged in memory.

## Contents

- [Multiple inheritance with facet interfaces](#multiple-inheritance-with-facet-interfaces)
- [Querying across the hierarchy](#querying-across-the-hierarchy)
- [Two constraints to design around](#two-constraints-to-design-around)
- [Choosing between relation, reference and embedded](#choosing-between-relation-reference-and-embedded)
- [The complete example model — flat form](#the-complete-example-model--flat-form)
- [The same model with facet interfaces](#the-same-model-with-facet-interfaces)

## Multiple inheritance with facet interfaces

When the engine builds a node type it records **every interface the type implements as a parent node type**. Interfaces are therefore not just a declaration style; they are the inheritance mechanism. And because a C# type may implement any number of interfaces, this gives modelling shapes a single-base-class hierarchy cannot express.

Define small, focused facet interfaces:

```csharp
// A thing that sits somewhere on the map.
public interface ILocatable {
    [GeoCoordinateProperty(Indexed = true)]
    GeoCoordinate Location { get; set; }

    [StringProperty(Indexed = true, MaxLength = 2)]
    string CountryCode { get; set; }
}

// A thing with an identity, a name and a slug.
public interface INamedNode {
    Guid Id { get; set; }
    NodeMeta Meta { get; }

    [DisplayNameProperty]
    [StringProperty(Indexed = true, IndexedByWords = true, PrefixSearch = true)]
    string Title { get; set; }

    [AddressProperty]
    [StringProperty(Indexed = true, UniqueValues = true)]
    string Slug { get; set; }
}

// A thing with a rich-text body that should be searchable.
public interface IDescribed {
    [HtmlProperty(IndexedByWords = true, IndexedBySemantic = true)]
    string Description { get; set; }
}

// A thing that can be tagged.
public interface ITagged {
    [StringArrayProperty(Indexed = true)]
    string[] Tags { get; set; }
}
```

…then compose them freely, mixing and matching per type:

```csharp
public interface IVenue    : INamedNode, IDescribed, ILocatable, ITagged { /* venue-only */ }
public interface IEvent    : INamedNode, IDescribed, ITagged            { /* event-only */ }
public interface IAttendee : INamedNode, ILocatable                     { /* attendee-only */ }
```

Each facet's properties are declared **exactly once**, with their indexing and validation attached, and every implementing type inherits them. Change `ILocatable.Location` to `Indexed = false` and both venues and attendees follow. Add a fifth node type that should appear on the map and it is one interface in the declaration — no property copying, no query to update.

A class-based model cannot express this. `Venue` can inherit one base class, so the moment you want "locatable" *and* "described" *and* "tagged" you are copying properties between types and keeping their attributes in sync by hand.

## Querying across the hierarchy

This is where the composition pays off at runtime. `Query<T>()` matches `T` **and every type descending from it**, so you can query a facet interface directly and get heterogeneous results:

```csharp
// Every locatable node of any type within 5 km — venues and attendees together
var nearby = db.Query<ILocatable>()
    .Where(x => x.Location.IsWithin(oslo, 5_000))
    .Execute();

foreach (var x in nearby) {
    Console.WriteLine(x switch {
        IVenue v    => $"Venue: {v.Title}",
        IAttendee a => $"Attendee: {a.Title}",
        _           => "Something else"
    });
}

// One search box over everything that has a title
var hits = db.Query<INamedNode>()
    .WhereSearch("outdoor jazz", semanticRatio: 0.5)
    .Execute();

// One tag cloud over everything taggable
var tagCloud = db.Query<ITagged>().Facets().AddValueFacet(x => x.Tags).Execute();

// …narrowed back down to specific types when you want that
var venuesOnly = db.Query<ILocatable>()
    .WhereTypes(new[] { typeof(IVenue) }, includeDescendants: true)
    .Where(x => x.CountryCode == "NO")
    .Execute();
```

A cross-cutting search page, a global "recently changed" feed, a map view that plots anything with coordinates — each becomes **one indexed query** against a facet interface, rather than one query per concrete type merged and re-sorted in memory, and re-merged every time a type is added.

## Two constraints to design around

The model builder is strict about ambiguity, and both rules bite exactly where you would expect. Both are checked at **model-build time**, not at runtime, so you find out immediately.

1. **Two parent interfaces may not declare the same property name.** If `ILocatable` and `ITagged` both declared `CountryCode`, any type implementing both fails to build. Keep facets disjoint, or hoist the shared member into a base interface that both extend.
2. **Property overriding is not supported.** A property is defined once, on the type that first declares it. You cannot redeclare it further down to change its attributes.

The practical consequence when designing facets: decide early which interface owns each cross-cutting name, and treat that as final. Because `INamedNode` declares `Title`, no other facet may declare `Title`, and no composed type may redeclare it.

## Choosing between relation, reference and embedded

|  | Embedded | Reference | Relation |
|---|---|---|---|
| Identity | none — owned by parent | target is an independent node | both sides are independent nodes |
| Direction | n/a | one-way | bidirectional, automatic |
| Reverse lookup | n/a | no | yes, indexed |
| Traversable (`Traverse`, `ShortestPath`) | no | no | yes |
| Filter by target | no | yes, with `Indexed = true` | yes, `WhereRelates` |
| Order preserved | yes | `References<T>`: yes | yes, per side — reorder with the `MoveRelation…` family |
| Duplicates | yes | `References<T>`: yes | no — relating an existing pair throws |
| Cost to change | rewrites parent node | rewrites parent node | index update, no node rewrite |
| Lifecycle | dies with parent | independent | independent |

Decision guide:

- The child has no meaning outside the parent, and you never query it independently → **embedded**.
- You need the **same target more than once**, and you never need the reverse lookup → **`References<T>`**. Note that ordering by itself is *not* a reason to pick this: relation lists are ordered too, and come with reorder operations that references do not have.
- You point at one thing, cheaply, and never ask "what points at me?" → **`Reference<T>`**.
- You need reverse navigation, traversal, relation filters, relation facets, reorderable ordering, or referential bookkeeping → **relation**. When in doubt, this is the right default.

Applied to the example domain below:

- `Venue.Hours` → **embedded**. Opening hours are meaningless without their venue.
- `Event.Cover` → **`Reference<IMediaAsset>`**. One-way pointer; nobody asks "which events use this image?" from the image side.
- `Event.Sponsors` → **`References<IOrganizer>`**. The same organizer may appear twice under different billing tiers, and we do not need the reverse lookup from that property. Order matters here, but that alone would not decide it — a relation would give ordering too, plus reorder operations; duplicates are what rule a relation out.
- `Event ↔ Venue`, `Event ↔ Attendee` → **relations**. Both directions are navigated constantly, and the venue's event list is a curated running order maintained with `MoveRelation…`.

## The complete example model — flat form

Every property is declared directly on each type, so each type reads as a self-contained unit. Useful for teaching and for small models; compare with the composed version below.

```csharp
using Relatude.DB.Common;
using Relatude.DB.Datamodels;
using Relatude.DB.Nodes;

namespace VenueApp.Models;

public enum EventStatus { Draft = 0, Published = 1, SoldOut = 2, Cancelled = 3 }
public enum VenueKind { Indoor = 0, Outdoor = 1, Hybrid = 2 }

// ── Node types ──────────────────────────────────────────────────────────────

public interface IOrganizer {
    Guid Id { get; set; }
    NodeMeta Meta { get; }

    [DisplayNameProperty]
    [StringProperty(Indexed = true, MaxLength = 200, IndexedByWords = true)]
    string Name { get; set; }

    [StringProperty(StringType = StringValueType.Email, UniqueValues = true)]
    string ContactEmail { get; set; }

    [AddressProperty]
    [StringProperty(Indexed = true, UniqueValues = true, RegularExpression = @"^[a-z0-9-]+$")]
    string Slug { get; set; }

    [CreatedUtcProperty] DateTime CreatedUtc { get; set; }

    OrganizerEvents.Events Events { get; }
}

public interface IVenue {
    Guid Id { get; set; }
    NodeMeta Meta { get; }

    [DisplayNameProperty]
    [StringProperty(Indexed = true, IndexedByWords = true, PrefixSearch = true)]
    string Name { get; set; }

    [AddressProperty]
    [StringProperty(Indexed = true, UniqueValues = true)]
    string Slug { get; set; }

    [HtmlProperty(IndexedByWords = true, IndexedBySemantic = true)]
    string Description { get; set; }

    [GeoCoordinateProperty(Indexed = true)]
    GeoCoordinate Location { get; set; }

    [StringProperty(Indexed = true, MaxLength = 2)]
    string CountryCode { get; set; }

    [IntegerProperty(Indexed = true, MinValue = 0)]
    int Capacity { get; set; }

    [IntegerProperty(Indexed = true)]
    VenueKind Kind { get; set; }

    [BooleanProperty(Indexed = true)]
    bool IsAccessible { get; set; }

    [FileProperty]
    FileValue Photo { get; set; }

    [EmbeddedMapProperty(KeyProperty = nameof(OpeningHours.DayCode))]
    EmbeddedMap<string, OpeningHours> Hours { get; }

    VenueTree.Parent Parent { get; }        // e.g. a hall inside a complex
    VenueTree.Children Halls { get; }
    EventsAtVenue.Events Events { get; }
}

public interface IEvent {
    Guid Id { get; set; }
    NodeMeta Meta { get; }

    [DisplayNameProperty]
    [StringProperty(Indexed = true, IndexedByWords = true, IndexedBySemantic = true, PrefixSearch = true)]
    string Title { get; set; }

    [HtmlProperty(IndexedByWords = true, IndexedBySemantic = true)]
    string Description { get; set; }

    [DateTimeProperty(Indexed = true)]
    DateTime StartsUtc { get; set; }

    [TimeSpanProperty(Indexed = true, MaxValue = "1.00:00:00")]
    TimeSpan Duration { get; set; }

    [DecimalProperty(Indexed = true, MinValue = "0", DefaultValue = "0",
                     FacetRangePowerBase = 2.0, FacetRangeCount = 6)]
    decimal Price { get; set; }

    [IntegerProperty(Indexed = true)]
    EventStatus Status { get; set; }

    [StringArrayProperty(Indexed = true)]
    string[] Tags { get; set; }

    [ReferenceProperty(Indexed = true)]
    Reference<IMediaAsset> Cover { get; }

    [ReferencesProperty(Indexed = true)]
    References<IOrganizer> Sponsors { get; }

    EventsAtVenue.Venue Venue { get; }
    OrganizerEvents.Host Host { get; }
    Attendance.Attendees Attendees { get; }
}

public interface IAttendee {
    Guid Id { get; set; }
    NodeMeta Meta { get; }

    [DisplayNameProperty]
    [StringProperty(Indexed = true, IndexedByWords = true)]
    string FullName { get; set; }

    [StringProperty(StringType = StringValueType.Email, Indexed = true, UniqueValues = true)]
    string Email { get; set; }

    [GeoCoordinateProperty(Indexed = true)]
    GeoCoordinate HomeLocation { get; set; }

    Attendance.Events Attending { get; }
    Friends.Peers Friends { get; }
}

public interface IMediaAsset {
    Guid Id { get; set; }
    NodeMeta Meta { get; }

    [DisplayNameProperty]
    [StringProperty(Indexed = true)]
    string FileName { get; set; }

    [FileProperty]
    FileValue File { get; set; }
}

// ── Embedded types ──────────────────────────────────────────────────────────

public class OpeningHours {
    public Guid Id { get; set; }
    public string DayCode { get; set; } = string.Empty;   // "mon" … "sun"
    public TimeSpan Opens { get; set; }
    public TimeSpan Closes { get; set; }
}

// ── Relations ───────────────────────────────────────────────────────────────

public class EventsAtVenue : OneToMany<IVenue, IEvent> {
    public class Venue : One { }
    public class Events : Many { }
}

public class OrganizerEvents : OneToMany<IOrganizer, IEvent> {
    public class Host : One { }
    public class Events : Many { }
}

public class Attendance : ManyToMany<IEvent, IAttendee> {
    public class Attendees : ManyTo { }
    public class Events : ManyFrom { }
}

public class Friends : ManyMany<IAttendee> {
    public class Peers : Many { }
}

[Relation(DisallowCircularReferences = true)]
public class VenueTree : OneToMany<IVenue, IVenue> {
    public class Parent : One { }
    public class Children : Many { }
}
```

## The same model with facet interfaces

**This is the shape to reach for in a real project.** Notice how much duplication disappears — and what it buys at query time.

One deliberate change along the way: `Venue.Name`, `Event.Title` and `Attendee.FullName` all become a single `INamedNode.Title`. Unifying the name is the price of the shared facet, and it is usually worth paying — it is what makes a single search box and a single "recently changed" feed possible. Flag this trade-off explicitly when proposing a composed model to a user, because it is a real modelling concession, not a free win.

```csharp
// ── Facets ──────────────────────────────────────────────────────────────────

public interface INamedNode {
    Guid Id { get; set; }
    NodeMeta Meta { get; }

    [DisplayNameProperty]
    [StringProperty(Indexed = true, IndexedByWords = true, PrefixSearch = true)]
    string Title { get; set; }

    [AddressProperty]
    [StringProperty(Indexed = true, UniqueValues = true)]
    string Slug { get; set; }

    [CreatedUtcProperty] DateTime CreatedUtc { get; set; }
    [ChangedUtcProperty] DateTime ChangedUtc { get; set; }
}

public interface IDescribed {
    [HtmlProperty(IndexedByWords = true, IndexedBySemantic = true)]
    string Description { get; set; }
}

public interface ILocatable {
    [GeoCoordinateProperty(Indexed = true)]
    GeoCoordinate Location { get; set; }

    [StringProperty(Indexed = true, MaxLength = 2)]
    string CountryCode { get; set; }
}

public interface ITagged {
    [StringArrayProperty(Indexed = true)]
    string[] Tags { get; set; }
}

// ── Composed node types ─────────────────────────────────────────────────────

public interface IVenue : INamedNode, IDescribed, ILocatable, ITagged {
    [IntegerProperty(Indexed = true, MinValue = 0)] int Capacity { get; set; }
    [IntegerProperty(Indexed = true)] VenueKind Kind { get; set; }
    [BooleanProperty(Indexed = true)] bool IsAccessible { get; set; }
    [FileProperty] FileValue Photo { get; set; }

    [EmbeddedMapProperty(KeyProperty = nameof(OpeningHours.DayCode))]
    EmbeddedMap<string, OpeningHours> Hours { get; }

    VenueTree.Parent Parent { get; }
    VenueTree.Children Halls { get; }
    EventsAtVenue.Events Events { get; }
}

public interface IEvent : INamedNode, IDescribed, ITagged {
    [DateTimeProperty(Indexed = true)] DateTime StartsUtc { get; set; }
    [TimeSpanProperty(Indexed = true)] TimeSpan Duration { get; set; }
    [IntegerProperty(Indexed = true)] EventStatus Status { get; set; }

    [DecimalProperty(Indexed = true, MinValue = "0", DefaultValue = "0",
                     FacetRangePowerBase = 2.0, FacetRangeCount = 6)]
    decimal Price { get; set; }

    [ReferenceProperty(Indexed = true)] Reference<IMediaAsset> Cover { get; }
    [ReferencesProperty(Indexed = true)] References<IOrganizer> Sponsors { get; }

    EventsAtVenue.Venue Venue { get; }
    OrganizerEvents.Host Host { get; }
    Attendance.Attendees Attendees { get; }
}

public interface IAttendee : INamedNode, ILocatable {
    [StringProperty(StringType = StringValueType.Email, Indexed = true, UniqueValues = true)]
    string Email { get; set; }

    Attendance.Events Attending { get; }
    Friends.Peers Friends { get; }
}

public interface IOrganizer : INamedNode, IDescribed {
    [StringProperty(StringType = StringValueType.Email, UniqueValues = true)]
    string ContactEmail { get; set; }

    OrganizerEvents.Events Events { get; }
}

public interface IMediaAsset : INamedNode {
    [FileProperty] FileValue File { get; set; }
}
```

`Id`, `Meta`, `Title`, `Slug` and the timestamps are now written once, in `INamedNode`, and the whole model inherits them. `Location` is written once and shared by venues and attendees.

And the payoff at query time — each of these is a single indexed query:

```csharp
// One search box across venues, events, organizers and assets
db.Query<INamedNode>().WhereSearch("nordic jazz", semanticRatio: 0.5).Execute();

// One map query across venues and attendees
db.Query<ILocatable>().Where(x => x.Location.IsWithin(oslo, 5_000)).Execute();

// One tag cloud across venues and events
db.Query<ITagged>().Facets().AddValueFacet(x => x.Tags).Execute();

// Global "recently changed" feed
db.Query<INamedNode>().OrderByDescending(x => x.ChangedUtc).Page(0, 50).Execute();
```

After any modelling change, open the admin UI datamodel browser and check the parent chain of each type. It is the fastest way to confirm that facet interfaces actually landed as parent node types and that a property you expected to be indexed really is.
