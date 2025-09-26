# Relatude.DB

**Relatude.DB** is an open-source, **C#-native database engine** designed to give you **one unified storage engine** with everything you need to build modern applications. It combines multiple database paradigms into a one cohesive system. It is best described as an **object-oriented graph database** with rich indexing and query capabilities.

It is currently early in the development, but is already used in daily use in several live projects and products. In the long term it will replace the data layer in our commercial CMS and E-Commerce solution [Relatude](https://relatude.com). 

We have chosen to publish the DB as open source because we want to build trust and be transparent in how your data is stored. We think this is fundamental in giving you control and total ownership. We will offer cloud hosting as an option, but you may equally well store the data in your own environment.

The second reason for publishing it as open source is to foster an active community. We see the database as a general storage solution for any Web Applications, and not just as a data layer for our own CMS. The system aims to solve and simplify the typical challenges found in building web applications. This includes having one easy to install storage and query engine for all your data. In todays applications you typically need to manage multiple storage systems to provide the functionality you need: text indexing, vector and ai searches (RAG), structured queries, model generation, faceted searches, GraphQL endpoints, SPA, image scaling and media handling, file indexing etc, access control, revisions, multiple languages, backup.  Relatude DB provides all of this in one simple Nuget package. Bring it into any project where it is relevant, small or large, for prototyping or production.

## Technical implementation details
The underlying storage is an append only log file with an in memory hash based index system. This greatly reduce the risk of ever loosing data and ensures high transaction thruput and instant queries. Each object is stored to disk in a binary format. The property values of the object is stored as a list of values that are mapped and casted to the current schema on read. This ensures flexibility in handling changes in schema to existing data,

To reduce memory usage, you have the option of using traditional disk based indexes where wanted to tune your speed vs memory balance. As the system incorporate alle the storage engines in one system you get the benefit of having smarter caches as the system is able to keep detailed track of which update invalidates which cache. To you as a developer, every query represent the latest data, and the logic of the cache is not something you need to think about. Internally every query is broken down to set operations (A ∪ B etc,) and every set operation is cached. This ensure high reuse of cached data across multiple queries.

---
## Key Features

### Multiple Engines in One System
- **Object-Oriented Data Modeling** — model your domain naturally using classes and relationships.
- **Graph Support** — first-class object relationships and graph queries.
- **Full-Text Indexing** — BM25 ranking and fuzzy search.
- **Vector Indexing** — built-in support for AI-driven semantic search.
- **Flexible File Store** — manage files alongside structured data.
---
### Simple, Powerful API
- **Typed query expressions** in C# and TypeScript.
- **String-based query API** for REST integrations.
- **GraphQL endpoints** for frontend-friendly querying.
- Expressive filters, range queries, and aggregations.
- BM25-powered **full-text search** with fuzzy matching.
- **Semantic search** using cosine similarity of vectors.
- Adaptive **faceted search** for large, varied datasets.
---
### Cross-Platform Support
- Run the **server** on **Linux, macOS, or Windows**.
- Develop clients in **C#, TypeScript, React**
---
### Flexible Schema Modeling
- Combine **code-first** models with an internal schema.
- Automatic **model generation** in C# and TypeScript.
- Supports **classes, interfaces, records, and structs**.
- Multiple inheritance and expressive value constraints.
---
### Plugin System
- Intercept queries and transactions to customize functionality and develop triggers.
---
### Flexible Hosting & Deployment
- Run **in-process** or as an **external server**.
- Includes a **built-in web-based DBA UI**.
---
### Flexible Storage Options
- Store data on the **local file system** for performance or **remote blob storage** to reduce cost.
- The in-memory index and queued disk writes plays well with stores that have a higher latency .
---

### Transactions & Reliability
- **ACID-compliant transactions**
- Built-in **data recovery** from file corruption or unexpected shutdown or powerloss.
- Log based storage system, supporting rollback to any point in time.

---

### High Performance
- Built-in **in-memory indexes** using tries, hashmaps, and bit arrays.
- Optional **disk-based indexes** via **Lucene**, **SQLite**, or custom implementations.
- Intelligent caching based on set operations.
- Benchmarks available — **don’t just take our word for it!**

---

### Scalability & Fault Tolerance
- **Append-only transaction file format** for durability.
- Automatic backups to external storage.

---

### Built-in Media Handling
- Integrated **image scaling engine**.
- AI-powered **image indexing and manipulation** plugins.

---

### Persistent Task Queue
- Automatic batching of background tasks for improved performance.
- Used for **file indexing** and other long-running operations.
- Create and run **custom background tasks**.

---

### Logging & Statistics
- Track queries and usage per request.
- View statistics and logs via the **built-in DBA UI**.
- Extend logging with your own custom data.

---


---

## Why Open Source?

We’re a **Norwegian company** with ~25 employees and limited resources. By **open-sourcing** Relatude.DB under a **liberal license**, we aim to:

- Build **trust** through transparency.
- Foster an **active community**.
- Accelerate adoption of an **innovative database solution**.

---

## Project Status & Roadmap

Relatude.DB is currently in **pre-release**.  
Over the next year, expect **minor breaking changes** as we stabilize the engine.

- Already used in several **commercial projects** with great success.
- Soon replacing the data layer in our **Relatude CMS**.
- Our goal: make Relatude.DB a **general-purpose data layer** for modern web apps.
- Long-term vision: build an **open-source**, **free**, and **vibrant community**.

Our commercial offering focuses on:
- Selling licenses for our **CMS** and **e-commerce platform**.
- Optional **cloud hosting**.
- **Enterprise support agreements**.

---

## Example Usage

```csharp
var store = RelatudeDB.DefaultStore;

// Query users and include their friends
var users = store.Query<User>()
                 .Include(u => u.Friends)
                 .Where(u => u.Company == "Microsoft")
                 .Execute();
