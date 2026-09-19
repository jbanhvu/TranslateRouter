using System.Drawing.Imaging;
using MeetingInterpreter;

namespace LayoutSnapshot;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        var outputPath = args.Length > 0
            ? Path.GetFullPath(args[0])
            : Path.GetFullPath("main-layout.png");
        var width = args.Length > 1 && int.TryParse(args[1], out var parsedWidth) ? parsedWidth : 1536;
        var height = args.Length > 2 && int.TryParse(args[2], out var parsedHeight) ? parsedHeight : 864;
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

        using var form = new FormMain
        {
            WindowState = FormWindowState.Normal,
            StartPosition = FormStartPosition.Manual,
            Location = new Point(-10000, -10000),
            Size = new Size(width, height)
        };
        form.Show();
        Application.DoEvents();
        form.PerformLayout();

        using var bitmap = new Bitmap(form.Width, form.Height);
        form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
        bitmap.Save(outputPath, ImageFormat.Png);
        form.Close();

        Console.WriteLine(outputPath);
    }
}
