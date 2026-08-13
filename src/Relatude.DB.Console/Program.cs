using Relatude.DB.Cli.Commands;

namespace Relatude.DB.Cli;

/// <summary>
/// Command line tool for Relatude.DB. It works on a database and a datamodel from the outside: no
/// application code has to run, only the model assembly (or the model source files) have to be readable.
/// </summary>
public static class Program {
    /// <summary>Options every command accepts.</summary>
    public static readonly string[] GlobalOptions = ["json", "verbose", "quiet", "help", "h"];

    public static async Task<int> Main(string[] rawArgs) {
        CommandArgs args;
        try {
            args = CommandArgs.Parse(rawArgs);
        } catch (UsageException err) {
            System.Console.Error.WriteLine("error: " + err.Message);
            return 2;
        }
        Con.Initialize(args.Flag("quiet"), args.Flag("verbose"));
        if (args.Command == "help") {
            Help.Write(args.Positional.FirstOrDefault());
            return 0;
        }
        if (args.Command.Length == 0) {
            Help.Write(null);
            return rawArgs.Length == 0 || args.Flag("help") || args.Flag("h") ? 0 : 2;
        }
        if (args.Flag("help") || args.Flag("h")) {
            Help.Write(args.Command);
            return 0;
        }
        try {
            return await runAsync(args);
        } catch (UsageException err) {
            Con.Error(err.Message);
            Con.Info(Help.Knows(args.Command) ? "Try: relatude help " + args.Command : "Try: relatude help");
            return 2;
        } catch (CliException err) {
            Con.Error(err.Message);
            if (Con.Verbose && err.InnerException != null) Con.Info(err.InnerException.ToString());
            return 1;
        } catch (Exception err) {
            Con.Error(err.Message);
            if (Con.Verbose) Con.Info(err.ToString());
            else Con.Info("Run again with --verbose for the full exception.");
            return 1;
        } finally {
            Con.Flush();
        }
    }

    static Task<int> runAsync(CommandArgs args) => args.Command switch {
        "info" => InfoCommand.RunAsync(args),
        "schema" => SchemaCommand.RunAsync(args),
        "query" => QueryCommand.RunAsync(args),
        "codegen" => CodeGenCommand.RunAsync(args),
        "validate" => ValidateCommand.RunAsync(args),
        "init" => InitCommand.RunAsync(args),
        "settings" => SettingsCommand.RunAsync(args),
        "maintenance" => MaintenanceCommand.RunAsync(args),
        "insert" => WriteCommands.InsertAsync(args),
        "delete" => WriteCommands.DeleteAsync(args),
        "version" => version(args),
        _ => throw new UsageException("Unknown command \"" + args.Command + "\". Run \"relatude help\" for the list."),
    };

    static Task<int> version(CommandArgs args) {
        args.Accept();
        var v = typeof(Relatude.DB.Nodes.NodeStore).Assembly.GetName().Version?.ToString() ?? "unknown";
        if (args.Flag("json")) Con.Json(new { Version = v });
        else Con.WriteLine("Relatude.DB " + v);
        return Task.FromResult(0);
    }
}

/// <summary>An error meant for the user: reported as a single line, without a stack trace.</summary>
public class CliException(string message, Exception? inner = null) : Exception(message, inner) { }
