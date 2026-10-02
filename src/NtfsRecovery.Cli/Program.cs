using System.CommandLine;
using NtfsRecovery.Cli.Commands;

namespace NtfsRecovery.Cli;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var rootCommand = new RootCommand("NTFS carving and recovery tool (read-only on the source device).")
        {
            InspectCommand.Build(),
            PartitionsCommand.Build(),
            ScanCommand.Build(),
            TreeCommand.Build(),
            ListCommand.Build(),
            InfoCommand.Build(),
            ExtractRecordCommand.Build(),
            RecoverCommand.Build(),
        };

        if (args.Length == 0)
        {
            PrintFullHelp(rootCommand);
            return 0;
        }

        return await rootCommand.InvokeAsync(args);
    }

    /// <summary>
    /// Prints every command's full parameter list in one screen. Running a command with
    /// just "--help" only shows that one command's options; this is the "show me
    /// everything at once" view, useful for discovering what the tool can do without
    /// knowing a command name up front.
    /// </summary>
    private static void PrintFullHelp(RootCommand rootCommand)
    {
        Console.WriteLine(rootCommand.Description);
        Console.WriteLine();

        foreach (Command command in rootCommand.Children.OfType<Command>())
        {
            Console.WriteLine($"=== {command.Name} ===");
            if (!string.IsNullOrEmpty(command.Description))
                Console.WriteLine(command.Description);

            foreach (Argument argument in command.Arguments)
            {
                string defaultSuffix = argument.HasDefaultValue ? $" [default: {argument.GetDefaultValue()}]" : "";
                Console.WriteLine($"  <{argument.Name}>{new string(' ', Math.Max(1, 20 - argument.Name.Length))}{argument.Description}{defaultSuffix}");
            }

            foreach (Option option in command.Options)
            {
                string aliases = string.Join(", ", option.Aliases);
                string requiredSuffix = option.IsRequired ? " (required)" : "";
                Console.WriteLine($"  {aliases,-26}{option.Description}{requiredSuffix}");
            }

            Console.WriteLine();
        }

        Console.WriteLine("Use '<command> --help' for the full usage line of a single command.");
    }
}
