namespace MeetingInterpreter;

static class Program
{
    private const string SkipUpdaterArgument = "--skip-updater";

    [STAThread]
    static void Main(string[] args)
    {
        if (!args.Contains(SkipUpdaterArgument, StringComparer.OrdinalIgnoreCase)
            && TryStartUpdater())
        {
            return;
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new FormMain());
    }

    private static bool TryStartUpdater()
    {
        var updaterPath = Path.Combine(AppContext.BaseDirectory, "Update.exe");
        if (!File.Exists(updaterPath))
        {
            return false;
        }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = updaterPath,
                WorkingDirectory = AppContext.BaseDirectory,
                UseShellExecute = true
            });
            return true;
        }
        catch
        {
            return false;
        }
    }
}
