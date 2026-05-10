namespace BetterXeneonWidget.Installer;

internal static class Program
{
    public const string AppName = "BetterXeneonWidget";

    public static int Main(string[] args)
    {
        var uninstall = HasFlag(args, "--uninstall");
        var quiet = HasFlag(args, "--quiet");

        Console.OutputEncoding = System.Text.Encoding.UTF8;
        WriteHeader();

        try
        {
            if (uninstall)
                Steps.Uninstall();
            else
                Steps.Install();

            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine();
            Pretty.WriteError($"Failed: {ex.Message}");
            Console.WriteLine();
            Console.WriteLine(ex);
            return 1;
        }
        finally
        {
            if (!quiet)
            {
                Console.WriteLine();
                Console.WriteLine("Press any key to close...");
                try { Console.ReadKey(intercept: true); } catch { /* no console attached */ }
            }
        }
    }

    private static bool HasFlag(string[] args, string name) =>
        args.Any(a => a.Equals(name, StringComparison.OrdinalIgnoreCase));

    private static void WriteHeader()
    {
        Console.WriteLine();
        Pretty.WriteAccent("BetterXeneonWidget Installer");
        Pretty.WriteAccent("============================");
        Console.WriteLine();
    }
}
