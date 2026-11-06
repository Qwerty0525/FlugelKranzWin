using System.Runtime.InteropServices;

namespace FlugelKranz.Platform;

/// <summary>
/// The app is a GUI-subsystem exe (WinExe), so Windows opens no console window for it. When it is
/// started with command-line options (--diagnose, --bindings, --help, --version, a parse error …),
/// this attaches to the terminal that started it so the text still shows there, or opens a
/// console of its own when there is none (for example a shortcut with --diagnose). Output that is
/// already redirected to a file or pipe keeps going there.
/// </summary>
internal static class CommandLineConsole
{
    private const int AttachParentProcess = -1;
    private static bool allocated;

    /// <summary>True when there are arguments; the GUI starts without any.</summary>
    public static bool IsCommandLine(string[] args) => args.Length > 0;

    public static void Attach()
    {
        if (!OperatingSystem.IsWindows()) return;
        if (AttachConsole(AttachParentProcess)) return;
        allocated = AllocConsole();
    }

    /// <summary>A console opened just for this run would vanish with the text; wait for Enter first.</summary>
    public static void HoldIfOwnWindow()
    {
        if (!allocated) return;
        try
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine("Enter キーで閉じます。 / Press Enter to close.");
            Console.ReadLine();
        }
        catch (IOException) { }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AllocConsole();
}
