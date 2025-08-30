# Relatude.DB

**Relatude.DB** is an open-source, **C#-native database engine** designed to give you **one unified storage engine** with everything you need to build modern applications. It combines multiple database paradigms into a cohesive system — **one engine, one API**.  
The database is best described as an **object-oriented graph database** with rich indexing and query capabilities.

---

## Key Features

### 🧩 Multiple Engines in One System
- **Object-Oriented Data Modeling** — model your domain naturally using classes and relationships.
- **Graph Support** — first-class object relationships and graph queries.
- **Full-Text Indexing** — BM25 ranking and fuzzy search.
- **Vector Indexing** — built-in support for AI-driven semantic search.
- **Flexible File Store** — manage files alongside structured data.

---

### 💻 Simple, Powerful API
- **Typed query expressions** in C# and TypeScript.
- **String-based query API** for REST integrations.
- **GraphQL endpoints** for frontend-friendly querying.
- Expressive filters, range queries, and aggregations.
- BM25-powered **full-text search** with fuzzy matching.
- **Semantic search** using cosine similarity of vectors.
- Adaptive **faceted search** for large, varied datasets.

---

### 🌐 Cross-Platform Support
- Run the **server** on **Linux, macOS, or Windows**.
- Develop clients in **C#, Java, Node.js, TypeScript, React**, and more.

---

### 🏗️ Flexible Schema Modeling
- Combine **code-first** models with an internal schema.
- Automatic **model generation** in C# and TypeScript.
- Supports **classes, interfaces, records, and structs**.
- Multiple inheritance and expressive value constraints.

---

### 🔌 Plugin System
- Intercept queries and transactions to customize functionality.
- Embed custom logic deep into the data layer using **C#** or **TypeScript**.

---

### ☁️ Flexible Hosting & Deployment
- Run **in-process** or as an **external server**.
- Includes a **built-in web-based DBA UI**.

---

### 🗄️ Flexible Storage Options
- Store data on the **local file system**.
- Integrate with **remote blob storage**.

---

### 🔒 Transactions & Reliability
- **ACID-compliant transactions** with rollback to any point in time.
- Built-in **data recovery** from corruption or unexpected shutdowns.
- Optional **queued disk flush** for optimized performance.

---

### ⚡ High Performance
- Built-in **in-memory indexes**: tries, hashmaps, and bit arrays.
- Optional **disk-based indexes** via **Lucene**, **SQLite**, or custom implementations.
- Intelligent caching based on set operations.
- Benchmark results available — **don’t just take our word for it!**

---

### 📈 Scalability & Fault Tolerance
- **Append-only transaction file format** for durability.
- Automatic backups to external storage.
- **Two-way replication** and content synchronization.

---

### 🌍 Multilingual, Versioning & Access Control
- Manage **content in multiple languages** with version history.
- **Role-based access control** for secure, filtered queries.

---

### 🖼️ Built-in Media Handling
- Integrated **image scaling engine**.
- AI-powered **image indexing and manipulation** plugins.

---

### 🕒 Persistent Task Queue
- Automatic batching of background tasks for improved performance.
- Used for **file indexing** and other long-running operations.
- Create and run **custom background tasks**.

---

### 📊 Logging & Statistics
- Track queries and usage per request.
- View statistics and logs via the **built-in DBA UI**.
- Extend logging with your own custom data.

---

## Why Relatude.DB?

Build **better web applications faster**. Spend less time on infrastructure and more time on **UI** and **business logic**.

---

## Who Is It For?

- **Backend Developers** — especially **C#**, but we also support **Java** and **Node.js**.
- **Frontend Developers** — any framework that supports **REST** or **GraphQL**.
- Teams building apps that need rich data modeling, semantic search, and strong indexing.

---

## Best Suited For

- **Small to medium-sized projects**.
- Applications with **complex data models**.
- Projects handling **up to ~50 million objects**.

---

## Not Suited For

- Very large-scale projects with **50M+ objects**.
- Architectures requiring **clustered deployments**.

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
