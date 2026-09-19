namespace MeetingInterpreter.Controls;

public sealed class AudioLevelMeter : Control
{
    private const int SegmentCount = 12;
    private int _level;

    public AudioLevelMeter()
    {
        DoubleBuffered = true;
        SetStyle(ControlStyles.ResizeRedraw, true);
        MinimumSize = new Size(120, 12);
        Size = new Size(156, 14);
        BackColor = Color.White;
    }

    public int Level => _level;

    public void SetLevel(int level)
    {
        level = Math.Clamp(level, 0, 100);
        var smoothing = level > _level ? 0.55 : 0.22;
        var smoothed = (int)Math.Round(_level + ((level - _level) * smoothing));
        if (level == 0 && smoothed <= 1)
        {
            smoothed = 0;
        }

        if (smoothed == _level)
        {
            return;
        }

        _level = smoothed;
        Invalidate();
    }

    public void ResetLevel()
    {
        _level = 0;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        const int gap = 3;
        var segmentWidth = Math.Max(2, (ClientSize.Width - ((SegmentCount - 1) * gap)) / SegmentCount);
        var activeSegments = (int)Math.Ceiling(_level / 100d * SegmentCount);
        var inactive = Color.FromArgb(226, 230, 235);

        for (var index = 0; index < SegmentCount; index++)
        {
            var x = index * (segmentWidth + gap);
            var width = index == SegmentCount - 1
                ? Math.Max(1, ClientSize.Width - x)
                : segmentWidth;
            var color = index < activeSegments ? GetActiveColor(index) : inactive;
            using var brush = new SolidBrush(color);
            e.Graphics.FillRectangle(brush, x, 0, width, ClientSize.Height);
        }
    }

    private static Color GetActiveColor(int segmentIndex)
        => segmentIndex switch
        {
            >= 10 => UiTheme.Error,
            >= 8 => UiTheme.Warning,
            _ => UiTheme.Success
        };
}
