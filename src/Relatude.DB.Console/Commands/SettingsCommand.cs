using Relatude.DB.DataStores;
using Relatude.DB.IO;
using Relatude.DB.NodeServer;
using Relatude.DB.NodeServer.Settings;

namespace Relatude.DB.Cli.Commands;

/// <summary>
/// Prints relatude.db.json with its paths resolved, and what is actually on disk. Secrets are never
/// printed: this output is meant to be safe to paste into an issue or hand to a tool.
/// </summary>
public static class SettingsCommand {
    public static Task<int> RunAsync(CommandArgs args) {
        args.Accept([.. Target.Options, "all"]);
        var target = Target.Resolve(args);
        if (!target.SettingsExists) {
            throw new CliException("No settings file." + Environment.NewLine + target.Describe()
                + Environment.NewLine + "Create one with: relatude init");
        }
        var settings = SettingsReader.Read(target);
        var containers = settings.ContainerSettings ?? [];
        var selected = args.Flag("all") ? containers : [SettingsReader.SelectContainer(settings, target.Store)];

        if (args.Flag("json")) {
            Output.Json(new {
                File = target.SettingsPath,
                ContentRoot = target.Root,
                Server = new {
                    settings.Name,
                    settings.Id,
                    settings.DefaultStoreId,
                    AdminUrlPath = settings.DBAdminUIUrlPath ?? "/" + Defaults.AdminUrlRoot,
                    MasterUserSet = !string.IsNullOrEmpty(settings.MasterUserName),
                    MasterPasswordSet = !string.IsNullOrEmpty(settings.MasterPassword),
                    TokenEncryptionSecretSet = !string.IsNullOrEmpty(settings.TokenEncryptionSecret),
                },
                Databases = selected.Select(c => describeJson(c, settings, target)).ToArray(),
            });
            return Task.FromResult(0);
        }
        Output.WriteLine("Settings  " + target.SettingsPath);
        Output.Table([
            ("server", settings.Name ?? "-"),
            ("content root", target.Root),
            ("admin UI", settings.DBAdminUIUrlPath ?? "/" + Defaults.AdminUrlRoot),
            ("admin user", string.IsNullOrEmpty(settings.MasterUserName) ? "not set" : settings.MasterUserName + " (password " + (string.IsNullOrEmpty(settings.MasterPassword) ? "not set" : "set") + ")"),
            ("token secret", string.IsNullOrEmpty(settings.TokenEncryptionSecret) ? "not set - logins do not survive a restart" : "set"),
            ("databases", containers.Length.ToString()),
        ]);
        foreach (var c in selected) {
            Output.WriteLine();
            Output.WriteLine("Database \"" + c.Name + "\"" + (c.Id == settings.DefaultStoreId ? "  (default)" : string.Empty));
            Output.Table([
                ("id", c.Id.ToString()),
                ("auto open", c.AutoOpen + (c.WaitUntilOpen ? ", blocking start up" : string.Empty)),
                ("log", provider(c, c.IoDatabase, target)),
                ("indexes", provider(c, c.IoIndexes, target) + (c.IoIndexes == null ? " (falls back to the log provider)" : string.Empty)),
                ("backup", provider(c, c.IoBackup, target)),
                ("logging", provider(c, c.IoLog, target)),
                ("secondary log", provider(c, c.IoDatabaseSecondary, target)),
            ]);
            var local = c.LocalSettings;
            if (local != null) {
                Output.WriteLine("  engines");
                Output.Table([
                    ("value index", engine(local.DefaultValueEngine)),
                    ("text index", engine(local.DefaultTextEngine) + (local.EnableTextIndexByDefault ? ", enabled by default" : ", off by default")),
                    ("semantic index", (c.AISettings == null ? "no AI provider" : engine(local.DefaultVectorEngine)) + (local.EnableSemanticIndexByDefault ? ", enabled by default" : ", off by default")),
                    ("task queue", local.PersistedQueueStoreEngine + (local.AutoDequeTasks ? ", running" : ", not running")),
                    ("index folder", local.PersistedValueIndexFolderPath ?? "(with the index provider)"),
                    ("auto backup", local.AutoBackUp ? "on" : "off"),
                    ("auto truncate", local.AutoTruncate ? "on" : "off"),
                    ("default culture", local.DefaultCultureCode ?? "(none)"),
                ], "    ");
                var engines = (local.ValueIndexes ?? []).Select(e => ("value", e, e.Id == local.DefaultValueIndex))
                    .Concat((local.TextIndexes ?? []).Select(e => ("text", e, e.Id == local.DefaultTextIndex)))
                    .Concat((local.VectorIndexes ?? []).Select(e => ("vector", e, e.Id == local.DefaultVectorIndex)))
                    .ToArray();
                if (engines.Length > 0) {
                    Output.WriteLine("  index engines");
                    Output.Table(engines.Select(x => (x.Item1, x.Item2 + "  " + x.Item2.Id + (x.Item3 ? "  (default)" : string.Empty))), "    ");
                }
            }
            if (c.FileStoreSettings is { Length: > 0 }) {
                Output.WriteLine("  file stores");
                Output.Table(c.FileStoreSettings.Select(f => (f.StoreType.ToString(), provider(c, f.IoProviderId, target))), "    ");
            }
            var ai = c.AISettings;
            if (ai != null) {
                Output.WriteLine("  AI provider");
                Output.Table([
                    ("name", ai.Name ?? "-"),
                    ("type", ai.TypeName ?? "-"),
                    ("embedding model", ai.EmbeddingModel ?? "-"),
                    ("key", string.IsNullOrEmpty(ai.ApiKey) ? "not set" : "set (not shown)"),
                ], "    ");
            }
            Output.WriteLine("  datamodel sources");
            if (c.DatamodelSources is { Length: > 0 }) {
                Output.Table(c.DatamodelSources.Select(s => (s.Name ?? s.Id.ToString(),
                    s.Type + "  " + (s.Reference ?? "(entry assembly)") + (s.Namespace == null ? string.Empty : "  namespace " + s.Namespace)
                    + (s.Enabled ? string.Empty : "  (turned off, not loaded)"))), "    ");
            } else {
                Output.WriteLine("    none - the database has no datamodel");
            }
            writeFiles(c, target);
        }
        return Task.FromResult(0);
    }

    static void writeFiles(NodeStoreContainerSettings c, Target target) {
        var io = tryCreate(c, c.IoDatabase, target);
        if (io == null) return;
        FileMeta[] files;
        try {
            files = io.GetFiles();
        } catch (Exception err) {
            Output.Warn("Could not list the database files: " + err.Message);
            return;
        }
        Output.WriteLine("  files");
        if (files.Length == 0) {
            Output.WriteLine("    none yet - the database has never been opened");
            return;
        }
        Output.Table(files.OrderBy(f => f.Key).Select(f => (f.Key, Output.Bytes(f.Size) + "   " + f.LastModifiedUtc.ToString("u"))), "    ");
        Output.WriteLine("    " + files.Length + " file(s), " + Output.Bytes(files.Sum(f => f.Size)));
    }

    static object describeJson(NodeStoreContainerSettings c, RelatudeDBServerSettings settings, Target target) {
        var ai = c.AISettings;
        return new {
            c.Id,
            c.Name,
            c.AutoOpen,
            c.WaitUntilOpen,
            IsDefault = c.Id == settings.DefaultStoreId,
            Providers = (c.IOSettings ?? []).Select(io => new {
                io.Id,
                io.Name,
                Type = io.IOType.ToString(),
                Path = resolvedPath(io, target),
                UsedFor = new[] {
                    c.IoDatabase == io.Id ? "log" : null,
                    c.IoIndexes == io.Id ? "indexes" : null,
                    c.IoBackup == io.Id ? "backup" : null,
                    c.IoLog == io.Id ? "logging" : null,
                    c.IoDatabaseSecondary == io.Id ? "secondary log" : null,
                    (c.FileStoreSettings ?? []).Any(f => f.IoProviderId == io.Id) ? "files" : null,
                }.Where(u => u != null).ToArray(),
            }).ToArray(),
            c.LocalSettings,
            DatamodelSources = (c.DatamodelSources ?? []).Select(s => new {
                s.Id, s.Name, Type = s.Type.ToString(), s.Reference, s.Namespace, s.Enabled,
            }).ToArray(),
            AISettings = ai == null ? null : new {
                ai.Name, ai.TypeName, ai.EmbeddingModel,
                ApiKeySet = !string.IsNullOrEmpty(ai.ApiKey),
            },
        };
    }

    // the default engine of a kind as one line, or the memory index when the default names none
    static string engine(IndexEngineSettings? e) => e == null ? "Memory" : e.ToString();

    static string provider(NodeStoreContainerSettings c, Guid? id, Target target) {
        if (id == null || id == Guid.Empty) return "(not set)";
        var io = (c.IOSettings ?? []).FirstOrDefault(s => s.Id == id.Value);
        if (io == null) return "(missing provider " + id + ")";
        return (io.Name ?? io.IOType.ToString()) + "  " + resolvedPath(io, target);
    }
    static string resolvedPath(IOSettings io, Target target) => io.IOType switch {
        IOTypes.LocalDisk => Path.GetFullPath(io.Path is null or "" ? target.Root : Path.Combine(target.Root, io.Path.TrimStart('~', '/', '\\'))),
        IOTypes.AzureBlobStorage => "azure blob container " + (io.BlobContainerName ?? "?") + " (connection string not shown)",
        _ => io.IOType.ToString(),
    };
    static IIOProvider? tryCreate(NodeStoreContainerSettings c, Guid? id, Target target) {
        if (id == null || id == Guid.Empty) return null;
        var io = (c.IOSettings ?? []).FirstOrDefault(s => s.Id == id.Value);
        if (io == null || io.IOType != IOTypes.LocalDisk) return null; // only the local disk is listed without opening anything
        try {
            return IOSettings.Create(io, target.Root);
        } catch (Exception err) {
            Output.Detail("Could not create the IO provider: " + err.Message);
            return null;
        }
    }
}
