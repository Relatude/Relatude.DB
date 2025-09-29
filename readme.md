# Relatude.DB

**Relatude.DB** is an open-source, **C#-native database engine** designed to provide a **unified storage solution** with everything you need to build the backend for your web applications. It combines multiple database paradigms into one cohesive system. The best way to describe it is as an **object-oriented graph database** with rich indexing and query capabilities.

The project is in early development, but it is already used daily in several live products. In the long term, it will replace the data layer in our commercial CMS and E-Commerce platform [Relatude](https://relatude.com) from the Norwegian company [Proventus](https://proventus.no).

We chose to release Relatude.DB as open source because we want to build trust and be transparent about how your data is stored. We believe this transparency is fundamental for giving you control and true ownership of your data. We will offer cloud hosting as an option, but you are equally free to store the data in your own environment.

Another reason for publishing it as open source is to foster an active community. We see Relatude.DB as a **general-purpose storage solution** for any web application, not just a data layer for our own CMS. The system is designed to solve and simplify the typical challenges in building web applications. This includes offering a single, easy-to-install storage and query engine that covers all your data needs.

In today’s applications, you often need to combine multiple storage systems to get the required functionality: text indexing, vector/AI search (RAG), structured queries, model generation, faceted search, GraphQL endpoints, image scaling and media handling, file indexing, access control, revision management, multilingual support, backup, etc.

Relatude.DB provides all of this in **one NuGet package** you can bring it into any project — small or large, prototyping or production.

----------

## Technical implementation

The underlying storage is a binary **append-only log file** with an **in-memory index system**. This greatly reduces the risk of losing data and ensures high transaction throughput with instant response to queries. All data is stored in ONE file and this file is all you need to copy or move the database. There are other temporary files, but these regenerates automatically. The log file contains every transaction executed, so every operation can be rolled back. As the file grows you can set limits to when it will be shortened in background processes. Backups also run in the background without affecting the live database. The **append only** architecture works well with high latency storages like blob as data can be streamed in the background over time.

The file format is binary and object properties are casted to the current schema on read. This provides flexibility in handling schema changes with existing data, similar to document databases.

To reduce memory usage, you can use more traditional disk-based indexes, allowing you to balance best speed / memory consumption for your project. 

As the system combines multiple storages in one engine, you benefit from a **smarter built-in cache**: it keeps detailed track of which updates invalidate which cache entries. For you as a developer, every query always represents the latest data. The cache is completely transparent. Internally, every query is broken down into set operations (A ∪ B, etc.), and every set operation is cached. This enables high cache reuse across multiple queries.

----------

## Projects

Database:

-   **Relatude.DB.Common** — Common utilities
    
-   **Relatude.DB.DataStore** — Defining the DataStore and query language
    
-   **Relatude.DB.DataStoreLocal** — DataStore implementation
    
-   **Relatude.DB.DataStoreRemote** — DataStore remote client
    
-   **Relatude.DB.FileStorage** — File storage provider
    
-   **Relatude.DB.GraphQL** — GraphQL endpoint _(not started)_
    
-   **Relatude.DB.IO** — IO providers
    
-   **Relatude.DB.Logger** — Logging and statistics
    
-   **Relatude.DB.Model** — Schema definitions
    
-   **Relatude.DB.NodeServer** — Server runtime and admin UI backend
    
-   **Relatude.DB.NodeStore** — Typed wrapper of DataStore and main API
    
-   **Relatude.DB.Server.UI** — Admin UI frontend _(in early development)_
    

Plugins:

-   **Relatude.DB.Azure** — Azure Open AI and Azure Blob storage
    
-   **Relatude.DB.Sqlite** — Index providers based on SQLite
    
-   **Relatude.DB.Lucene** — Index providers based on Lucene
    

Examples:

-   **Website.Simple** — Basic website running the database server
    

----------

## NuGets

The database is distributed with the following NuGet packages:

-   **Relatude.DB.Server** — Database server with no external dependencies
    
-   **Relatude.DB.Plugins.Azure** — AI and IO provider based on Azure OpenAI and Blob
    
-   **Relatude.DB.Plugins.Lucene** — Text index provider based on Lucene
    
-   **Relatude.DB.Plugins.Sqlite** — Value index provider based on Sqlite
    

Planned NuGets:

-   **Relatude.DB.Local** — In-process local database engine (NodeStore + DataStoreLocal)
    
-   **Relatude.DB.Remote** — Client for connecting to a remote database (NodeStore + DataStoreRemote)
    

----------

## API Example
```c#
var users = store.Query<User>()
                 .Include(u => u.Friends)
                 .Where(u => u.Company == "Microsoft")
                 .Execute(); 
```
----------

## Try it out

It’s easy to incorporate the server in your exiting web project. 
See the included example or follow these steps:

1.  Create a C# web project in .NET 8  (any type)
    
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
4.  Run the project and access the web UI at `/relatude.db`
    

More examples and documentation will follow...

----------

## Features

_(under development)_

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