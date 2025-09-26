# Relatude.DB

**Relatude.DB** is an open-source, **C#-native database engine** designed to provide a **unified storage solution** with everything you need to build modern applications. It combines multiple database paradigms into one cohesive system. The best way to describe it is as an **object-oriented graph database** with rich indexing and query capabilities.

The project is still in early development, but it is already used daily in several live products. In the long term, it will replace the data layer in our commercial CMS and E-Commerce platform [Relatude](https://relatude.com).

We chose to release Relatude.DB as open source because we want to build trust and be transparent about how your data is stored. We believe this transparency is fundamental for giving you control and true ownership of your data. We will offer cloud hosting as an option, but you are equally free to store the data in your own environment.

Another reason for publishing it as open source is to foster an active community. We see Relatude.DB as a **general-purpose storage solution** for any web application, not just a data layer for our own CMS. The system is designed to solve and simplify the typical challenges in building web applications. This includes offering a single, easy-to-install storage and query engine that covers all your data needs.

In today’s applications, you often need to combine multiple storage systems to get the required functionality: text indexing, vector/AI search (RAG), structured queries, model generation, faceted search, GraphQL endpoints, SPAs, image scaling and media handling, file indexing, access control, revision management, multilingual support, backup, etc.

**Relatude.DB provides all of this in one NuGet package.** You can bring it into any project—small or large, prototyping or production.

----------

## Technical Implementation

The underlying storage is an **append-only log file** with an **in-memory hash-based index system**. This greatly reduces the risk of losing data and ensures high transaction throughput with instant queries.

Each object is stored to disk in binary format. The property values of an object are stored as a list of values that are mapped and casted to the current schema on read. This provides flexibility in handling schema changes in existing data.

To reduce memory usage, you can use traditional disk-based indexes, allowing you to balance speed and memory consumption for your project. Because the system integrates all storage engines into one, you benefit from a **smarter cache**: it keeps detailed track of which updates invalidate which cache entries.

For you as a developer, every query always represents the latest data. The cache is completely transparent. Internally, every query is broken down into set operations (A ∪ B, etc.), and every set operation is cached. This enables high cache reuse across multiple queries.

----------

## Projects

The solution folder **Database** contains the following projects:

-   **Relatude.DB.Common** — Utilities and enums shared across all projects
    
-   **Relatude.DB.DataStore** — Datastore interfaces, query parser, and expression tree
    
-   **Relatude.DB.DataStoreLocal** — In-process datastore implementation with text/value indexes, queue system, cache, etc.
    
-   **Relatude.DB.DataStoreRemote** — Remote datastore client communicating with a DataStoreLocal instance
    
-   **Relatude.DB.FileStorage** — File storage provider (remote/local communication)
    
-   **Relatude.DB.GraphQL** — GraphQL parser and implementation _(not started)_
    
-   **Relatude.DB.IO** — IO provider interfaces and implementations for local disk and memory (testing)
    
-   **Relatude.DB.Logger** — Logging and statistics system
    
-   **Relatude.DB.Model** — Base classes for schema definitions
    
-   **Relatude.DB.NodeServer** — Runtime/server for hosting multiple databases with an API for the admin UI
    
-   **Relatude.DB.NodeStore** — Typed API for working with the datastore, including query language, code generation, Roslyn compilation. Works with either DataStoreLocal or DataStoreRemote.
    
-   **Relatude.DB.Server.UI** — Web-based admin UI written in TypeScript and React _(in progress)_
    

Optional plugins:

-   **Relatude.DB.Azure** — AI provider and IO provider based on Azure OpenAI and Azure Blob Storage
    
-   **Relatude.DB.Sqlite** — Disk-based value and text indexes using SQLite
    
-   **Relatude.DB.Lucene** — Disk-based text indexes using Lucene
    

Example project:

-   **Website.Simple** — Minimal setup to run the database
    

----------

## NuGets

Distributed as NuGet packages:

-   **Relatude.DB.Server** — Complete database server with no external dependencies
    
-   **Relatude.DB.Plugins.Azure** — Plugins for Azure OpenAI services and Azure Blob Storage (vector indexing and blob storage)
    
-   **Relatude.DB.Plugins.Lucene** — Plugin for Lucene-based text indexing (disk-based alternative to memory)
    
-   **Relatude.DB.Plugins.Sqlite** — Plugin for SQLite-based property indexes (disk-based alternative to memory)
    

Planned:

-   **Relatude.DB.Local** — In-process local database engine (NodeStore + DataStoreLocal)
    
-   **Relatude.DB.Remote** — Client for connecting to a remote database (NodeStore + DataStoreRemote)
    

----------

## API Example
```c#
var store = RelatudeDB.DefaultStore; // Query users and include their friends  var users = store.Query<User>()
                 .Include(u => u.Friends)
                 .Where(u => u.Company == "Microsoft")
                 .Execute(); 
```
----------

## Try It Out

It’s very easy to incorporate the server into your project. See the example project or follow these steps:

1.  Create any C# web project in .NET 8
    
2.  Add the [Relatude.DB.Server](https://www.nuget.org/packages/Relatude.DB.Server) NuGet package
    
3.  Add these two lines to your "Program.cs":

After creating the builder:
```C#
    builder.AddRelatudeDB();
```
After creating the app:
```C#
    app.UseRelatudeDB();
```
4.  Access the web UI at `/relatude.db`. On first start, add your DBA username and password to the `relatude.db.json` file that will be created in the website root.
    

----------

## Features

_(Some still under development)_

### Multiple Engines in One

-   **Object-Oriented Data Modeling** — model your domain naturally with classes and relationships
    
-   **Graph Support** — first-class object relationships and graph queries
    
-   **Full-Text Indexing** — BM25 ranking and fuzzy search
    
-   **Vector Indexing** — AI-driven semantic search
    
-   **Flexible File Store** — manage files alongside structured data
    

----------

### Simple, Powerful API

-   **Typed query expressions** in C# and TypeScript
    
-   **String-based query API** for REST integrations
    
-   **GraphQL endpoints** for frontend-friendly querying
    
-   Expressive filters, range queries, and aggregations
    
-   BM25-powered **full-text search** with fuzzy matching
    
-   **Semantic search** with cosine similarity
    
-   Adaptive **faceted search** for large, varied datasets
    

----------

### Cross-Platform

-   Run the server on **Linux, macOS, or Windows**
    
-   Develop clients in **C#, TypeScript, React**
    

----------

### Flexible Schema Modeling

-   Combine **code-first** models with internal schema
    
-   Automatic **model generation** in C# and TypeScript
    
-   Supports **classes, interfaces, records, and structs**
    
-   Multiple inheritance and expressive value constraints
    

----------

### Plugin System

-   Intercept queries and transactions to customize behavior and implement triggers
    

----------

### Hosting & Deployment

-   Run **in-process** or as an **external server**
    
-   Includes a **built-in web-based DBA UI**
    

----------

### Storage Options

-   Store data on the **local file system** for performance, or **remote blob storage** for cost efficiency
    
-   In-memory index with queued disk writes works well with high-latency stores
    

----------

### Transactions & Reliability

-   **ACID-compliant transactions**
    
-   Built-in **data recovery** from file corruption or unexpected shutdowns/power loss
    
-   Log-based storage system with rollback support
    

----------

### High Performance

-   Built-in **in-memory indexes** using tries, hashmaps, and bit arrays
    
-   Optional **disk-based indexes** via Lucene, SQLite, or custom implementations
    
-   Intelligent caching based on set operations
    
-   Benchmarks available — **don’t just take our word for it!**
    

----------

### Scalability & Fault Tolerance

-   **Append-only transaction file format** for durability
    
-   Automatic backups to external storage
    

----------

### Media Handling

-   Integrated **image scaling engine**
    
-   AI-powered **image indexing and manipulation** plugins
    

----------

### Persistent Task Queue

-   Automatic batching of background tasks for performance
    
-   Used for **file indexing** and other long-running operations
    
-   Create and run **custom background tasks**
    

----------

### Logging & Statistics

-   Track queries and usage per request
    
-   View statistics and logs via the **DBA UI**
    
-   Extend logging with custom data