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
        Output.Initialize(args.Flag("quiet"), args.Flag("verbose"));
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
            Output.Error(err.Message);
            Output.Info(Help.Knows(args.Command) ? "Try: relatude help " + args.Command : "Try: relatude help");
            return 2;
        } catch (CliException err) {
            Output.Error(err.Message);
            if (Output.Verbose && err.InnerException != null) Output.Info(err.InnerException.ToString());
            return 1;
        } catch (Exception err) {
            Output.Error(err.Message);
            if (Output.Verbose) Output.Info(err.ToString());
            else Output.Info("Run again with --verbose for the full exception.");
            return 1;
        } finally {
            Output.Flush();
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
        "timestamp" => RevertCommand.TimestampAsync(args),
        "revert" => RevertCommand.RunAsync(args),
        "insert" => WriteCommands.InsertAsync(args),
        "delete" => WriteCommands.DeleteAsync(args),
        "version" => version(args),
        _ => throw new UsageException("Unknown command \"" + args.Command + "\". Run \"relatude help\" for the list."),
    };

    static Task<int> version(CommandArgs args) {
        args.Accept();
        var v = typeof(Relatude.DB.Nodes.NodeStore).Assembly.GetName().Version?.ToString() ?? "unknown";
        if (args.Flag("json")) Output.Json(new { Version = v });
        else Output.WriteLine("Relatude.DB " + v);
        return Task.FromResult(0);
    }
}

/// <summary>An error meant for the user: reported as a single line, without a stack trace.</summary>
public class CliException(string message, Exception? inner = null) : Exception(message, inner) { }
