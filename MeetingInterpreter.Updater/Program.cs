namespace MeetingInterpreter.Updater;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        using var mutex = new Mutex(initiallyOwned: true, "MeetingInterpreter.Update.Singleton", out var createdNew);
        if (!createdNew)
        {
            return;
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new UpdaterForm(new UpdaterLogger()));
    }
}
