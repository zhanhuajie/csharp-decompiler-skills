using System.Text;

namespace ApiLookup;

public static class Program
{
    public static int Main(string[] args)
    {
        try { Console.OutputEncoding = new UTF8Encoding(false); } catch { }
        return Run(args, Console.Out, Console.Error);
    }

    /// <summary>Runs the CLI with injectable output streams (used by tests).</summary>
    public static int Run(string[] args, TextWriter? stdout = null, TextWriter? stderr = null)
    {
        var outWriter = stdout ?? Console.Out;
        var errWriter = stderr ?? Console.Error;
        var wantsJson = args.Any(a => string.Equals(a, "--json", StringComparison.OrdinalIgnoreCase));

        CliOptions opts;
        try
        {
            opts = CliParser.Parse(args);
        }
        catch (ArgumentException ex)
        {
            OutputFormatter.WriteError(ex.Message, 1, wantsJson, outWriter, errWriter);
            return 1;
        }

        if (opts.ShowVersion)
        {
            BuildInfo.WriteVersion(outWriter, opts.Verbose);
            return 0;
        }

        if (opts.ShowHelp || string.IsNullOrEmpty(opts.Command))
        {
            HelpText.Write(outWriter);
            return 0;
        }

        if (!CommandCatalog.TryGet(opts.Command, out var command))
        {
            OutputFormatter.WriteError($"Unknown command: {opts.Command}. Run 'apilookup --help' for usage.", 1, opts.Json, outWriter, errWriter);
            return 1;
        }

        try
        {
            return command.Handler(new CommandContext(opts, outWriter, errWriter));
        }
        catch (Exception ex)
        {
            OutputFormatter.WriteError(ex.Message, 1, opts.Json, outWriter, errWriter);
            if (opts.Verbose)
                errWriter.WriteLine(ex.ToString());
            return 1;
        }
    }
}
