namespace MeetingInterpreter.Helpers;

public static class UiThreadHelper
{
    public static void Run(Control control, Action action)
    {
        if (control.IsDisposed)
        {
            return;
        }

        if (control.InvokeRequired)
        {
            control.BeginInvoke(action);
            return;
        }

        action();
    }
}
