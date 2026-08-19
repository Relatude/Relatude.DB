namespace Relatude.DB.Cli;

/// <summary>Usage text. Written to stdout so it can be piped and read by a tool or an agent.</summary>
public static class Help {
    const string modelOptions = """

      Model source (the datamodel lives in your code, not in the database files):
        --project <path>      application folder or .csproj (default: the current folder, or the
                              nearest folder above it that has a relatude.db.json)
        --assembly <file>     assembly holding the model types, repeatable
        --source <path>       .cs file or folder of model source, compiled in memory, repeatable
        --namespace <ns>      add every model type in this namespace, repeatable
        --model-type <name>   add this model type by full name, and everything it references,
                              repeatable
        --bin <folder>        where to load the application's assemblies from, repeatable
                              (default: the newest build output under <project>/bin)
        --no-native           leave the engine's own model (Relatude.DB.Native.Models) out of the
                              datamodel, not only out of the output
        --auto-deduce-relations   treat plain node typed members without a relation as relations
      When neither --namespace nor --model-type is given, the model types in the assembly are
      detected by the Relatude attributes and member types they use.
    """;
    const string databaseOptions = """

      Database:
        --settings <file>     path to relatude.db.json (default: <project>/relatude.db.json)
        --data <folder>       content root the settings paths are resolved against
        --store <name|id>     which database in the settings file (default: DefaultStoreId)
        --environment <name>  environment for the RelatudeDB section in appsettings.json,
                              appsettings.{environment}.json and environment variables, which
                              overrides relatude.db.json (default: DOTNET_ENVIRONMENT,
                              ASPNETCORE_ENVIRONMENT or Production)
        --no-ai               open without the configured AI provider
        --allow-background    keep auto backup, auto truncate, index state snapshots and the task
                              queue enabled while the command runs (off by default)
      The database is opened exactly as the application opens it, so it must not be running: the log
      file is locked by a single writer.
    """;
    const string settingsFileOptions = """

      Settings file:
        --settings <file>     path to relatude.db.json (default: <project>/relatude.db.json)
        --data <folder>       content root the settings paths are resolved against
        --store <name|id>     which database in the settings file (default: DefaultStoreId)
        --environment <name>  environment for the RelatudeDB section in appsettings.json,
                              appsettings.{environment}.json and environment variables, which
                              overrides relatude.db.json (default: DOTNET_ENVIRONMENT,
                              ASPNETCORE_ENVIRONMENT or Production)
    """;
    const string globalOptions = """

      Everywhere:
        --json                machine readable output
        --verbose             database log and full exceptions on stderr
        --quiet               no diagnostics
        --help                this text
      Results go to stdout, everything else to stderr. Exit code 0 ok, 1 failed, 2 wrong usage.
    """;

    static readonly Dictionary<string, string> _commands = new(StringComparer.OrdinalIgnoreCase) {
        ["info"] = $"""
        relatude info [options]

          Opens a database and reports what is in it: state, node count per type, file sizes, log and
          index status, cache use and the datamodel size.
        {databaseOptions}
        {modelOptions}
        {globalOptions}
        """,
        ["schema"] = $"""
        relatude schema [options]

          Prints the datamodel: node types with their properties, ids, index flags and inheritance, and
          the relations with their shape and endpoints. Reads model code only, so it does not open or
          lock the database.

          Options:
            --format text|md|json   default text
            --type <name>           only this node type or relation, matched on short or full name,
                                    repeatable
            --ids                   show the guids in text output (always present in json)
            --include-native        include the engine's own model in the output
            --properties-only       skip relations
        {modelOptions}
        {globalOptions}
        """,
        ["query"] = $$"""
        relatude query "<query>" [options]

          Runs a query against the database and prints the result as JSON, the same shape the HTTP API
          returns. The query is the text form of the query API, starting with a node type:

            relatude query "Product.Where(p => p.Price > 100).OrderBy(p => p.Name).Take(10)"
            relatude query "Product.Count()"
            relatude query "Article.WhereSearch(\"backpack\").Page(0, 20)"
            relatude query "Product.Where(p => p.Name == Name).Take(5)" --param Name=Rucksack

          Options:
            --file <path>           read the query from a file instead of the command line
            --param name=value      query parameter, repeatable. Numbers, true/false and guids are
                                    passed as such, everything else as a string
            --raw                   do not indent the JSON

          A projection (Select(a => new { a.Name, a.Price })) comes back as a JSON object per row,
          with the member names exactly as they were written in the query.
        {{databaseOptions}}
        {{modelOptions}}
        {{globalOptions}}
        """,
        ["codegen"] = $"""
        relatude codegen [options]

          Generates C# model code from a datamodel. Every node type and relation is written with its
          ids spelled out, which is what you want when you take over a model that was generated, or
          when you want to pin ids that are currently derived from type and member names.

            relatude codegen --out Models/Model.g.cs
            relatude codegen --out-dir Models --namespace MyApp.Models
            relatude codegen --no-attributes            (plain interfaces, no ids or index settings)

          Options:
            --out <file>            write one file (default: stdout)
            --out-dir <folder>      write one file per node type and relation
            --no-attributes         leave the attributes out
            --include-native        include the engine's own model in the output
            --type <name>           only this node type or relation, repeatable
            --force                 overwrite existing files
        {modelOptions}
        {globalOptions}
        """,
        ["validate"] = $"""
        relatude validate [options]

          Builds the datamodel from your model code and reports what the model builder would complain
          about at startup, plus warnings worth acting on: ids that are derived from names (renaming
          such a type or member loses its data), type names that are ambiguous in queries, indexes that
          cannot be used. Compiles the generated model code as a last check. Exit code 1 when the model
          does not build.
        {modelOptions}
        {globalOptions}
        """,
        ["settings"] = $"""
        relatude settings [options]

          Prints the effective content of relatude.db.json: databases, storage providers with their
          resolved folders, index engines, file stores, datamodel sources and the files on disk.
          Passwords, keys and connection strings are never printed.

          Options:
            --all                   every database in the file, not only the selected one
        {settingsFileOptions}
        {globalOptions}
        """,
        ["init"] = $"""
        relatude init [options]

          Writes a relatude.db.json next to your application, pointing at a local disk folder and at
          your own model namespace. Refuses to overwrite an existing file unless --force is given.

          Options:
            --name <name>           database name (default: MyDatabase)
            --namespace <ns>        namespace of your model types
            --assembly-name <name>  assembly the model types live in (default: the project name)
            --path <folder>         data folder, relative to the settings file (default: relatude.db)
            --user <name>           admin user for the admin UI
            --password <password>   admin password
            --force                 overwrite an existing file
            --settings <file>       where to write it (default: <project>/relatude.db.json)
            --project <path>        application folder or .csproj
        {globalOptions}
        """,
        ["maintenance"] = $"""
        relatude maintenance <action> [options]

          Actions:
            flush                write everything buffered to disk
            truncate-log         rewrite the log so it holds the current state only, shrinking it
            save-state           write index and node state files, making the next start faster
            update-caches        update the persisted caches
            clear-cache          drop in memory caches and collect garbage
            backup               write a backup to the backup provider (--truncate, --keep-forever)
            reset-indexes        delete state and index files, forcing a full rebuild from the log
                                 (needs --yes, the next start will be slow)

          Options:
            --yes                confirm an action that deletes files
            --truncate           backup: store the truncated form
            --keep-forever       backup: exclude from backup rotation
            --delete-old         truncate-log: delete the log files it replaces
        {databaseOptions}
        {modelOptions}
        {globalOptions}
        """,
        ["timestamp"] = $"""
        relatude timestamp [options]

          Prints the head of the transaction log: the timestamp of the last transaction, as a bare
          number on stdout so a script (or an agent) can capture it. This is the value to remember
          BEFORE making changes you may want to undo with "relatude revert".

            ts=$(relatude timestamp)
            ... experiment: run the app, insert, update, delete ...
            relatude revert --after $ts --yes
        {databaseOptions}
        {modelOptions}
        {globalOptions}
        """,
        ["revert"] = $"""
        relatude revert --after <timestamp> [options]

          Puts the database back to an earlier point by permanently deleting every transaction made
          after the timestamp - as if they never happened. The timestamp is the number printed by
          "relatude timestamp" (taken before the changes), or a UTC date/time like
          2026-08-19T14:30:00Z. The log file is truncated at that point and the database reloads;
          state or indexes that were persisted after the point are rebuilt from the log, which the
          command reports (on a large database that rebuild can take a while).

          Nothing is deleted until --yes is given; without it the command only reports what would
          go. Files uploaded by the deleted transactions are not removed from the file store.

          Options:
            --after <timestamp>  the last transaction to KEEP; everything after it is deleted
            --dry-run            only report what would be deleted
            --yes                actually delete, this cannot be undone
        {databaseOptions}
        {modelOptions}
        {globalOptions}
        """,
        ["insert"] = $$"""
        relatude insert --type <node type> [json] [options]

          Inserts nodes from JSON, for seeding and fixtures. One object or an array of objects, with
          member names as they are in the model:

            relatude insert --type Product "{ \"Name\": \"Rucksack\", \"Price\": 249 }"
            relatude insert --type Product --file products.json

          Scalar members are supported: text, numbers, bool, guid, dates, timespan, enums (name or
          number), arrays of those, and geo coordinates as [latitude, longitude]. Relations,
          references, files and embedded values are not: use the query API from code for those.

          Options:
            --type <name>           node type to insert, short or full name (required)
            --file <path>           read the JSON from a file
            --flush                 wait until the transaction is on disk
        {{databaseOptions}}
        {{modelOptions}}
        {{globalOptions}}
        """,
        ["delete"] = $"""
        relatude delete --id <id> [options]

          Deletes nodes by id. The id is a guid or the internal integer id.

          Options:
            --id <id>               repeatable, at least one
            --yes                   required, deleting cannot be undone
            --flush                 wait until the transaction is on disk
        {databaseOptions}
        {modelOptions}
        {globalOptions}
        """,
        ["version"] = """
        relatude version

          Prints the Relatude.DB version this tool was built against.
        """,
    };

    /// <summary>Whether there is help for this command, so a caller knows what to suggest.</summary>
    public static bool Knows(string command) => _commands.ContainsKey(command);

    /// <summary>Every command's help in one go: "relatude help all", the whole reference in one read.</summary>
    static void writeAll() {
        Output.WriteLine("relatude - complete command reference");
        foreach (var (name, text) in _commands) {
            Output.WriteLine();
            Output.WriteLine(new string('-', 78));
            Output.WriteLine();
            Output.WriteLine(text);
        }
    }

    public static void Write(string? command) {
        if (command is "all" or "reference") {
            writeAll();
            return;
        }
        if (command != null && _commands.TryGetValue(command, out var text)) {
            Output.WriteLine(text);
            return;
        }
        if (command != null && command.Length > 0) {
            Output.WriteLine("No help for \"" + command + "\".");
            Output.WriteLine();
        }
        Output.WriteLine($"""
        relatude - command line tool for Relatude.DB

        Usage: relatude <command> [options]

          info          open a database and report its state and contents
          schema        print the datamodel: node types, properties, relations, ids
          query         run a query and print the result as JSON
          codegen       generate C# model code from a datamodel
          validate      check the model code and report problems worth fixing
          settings      print relatude.db.json, resolved and without secrets
          init          create a relatude.db.json
          maintenance   flush, truncate the log, save state, back up, rebuild indexes
          timestamp     print the head of the transaction log, to remember before experimenting
          revert        delete every transaction after a timestamp, restoring an earlier state
          insert        insert nodes from JSON
          delete        delete nodes by id
          version       print the Relatude.DB version
          help <command>, help all

        A database is named by its relatude.db.json, a datamodel by the code it lives in:

          relatude schema --project ../MyApp
          relatude info
          relatude query "Product.Count()"
          relatude codegen --out Models/Model.g.cs

        "relatude help all" prints the reference for every command in one go.
        {globalOptions}
        """);
    }
}
