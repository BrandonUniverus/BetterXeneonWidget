namespace BetterXeneonWidget.Installer;

/// <summary>
/// Tiny console-output helpers — keeps Program/Steps focused on the actual
/// install logic rather than ANSI/Console.ForegroundColor plumbing.
/// </summary>
internal static class Pretty
{
    private static int _stepNumber = 0;
    private static int _totalSteps = 0;

    public static void BeginPlan(int totalSteps)
    {
        _stepNumber = 0;
        _totalSteps = totalSteps;
    }

    public static void Step(string message)
    {
        _stepNumber++;
        var prefix = _totalSteps > 0 ? $"[{_stepNumber}/{_totalSteps}] " : "";
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.Write(prefix);
        Console.ResetColor();
        Console.WriteLine(message);
    }

    public static void Detail(string message)
    {
        Console.WriteLine($"      {message}");
    }

    public static void WriteAccent(string message)
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine(message);
        Console.ResetColor();
    }

    public static void WriteSuccess(string message)
    {
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine(message);
        Console.ResetColor();
    }

    public static void WriteWarning(string message)
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine(message);
        Console.ResetColor();
    }

    public static void WriteError(string message)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine(message);
        Console.ResetColor();
    }
}
