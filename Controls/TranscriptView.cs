namespace MeetingInterpreter.Controls;

public sealed class TranscriptView : UserControl
{
    private const int ScrollStep = 42;
    private readonly VScrollBar _scrollBar;
    private string _content = string.Empty;
    private int _contentHeight;

    public TranscriptView()
    {
        DoubleBuffered = true;
        ResizeRedraw = true;
        SetStyle(
            ControlStyles.AllPaintingInWmPaint
            | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.UserPaint,
            true);

        _scrollBar = new VScrollBar
        {
            Dock = DockStyle.Right,
            SmallChange = ScrollStep,
            LargeChange = ScrollStep * 4,
            Visible = false
        };
        _scrollBar.ValueChanged += (_, _) => Invalidate();
        Controls.Add(_scrollBar);
        Padding = new Padding(2, 4, 8, 4);
    }

    public string Content => _content;

    public void SetContent(string content)
    {
        var next = content ?? string.Empty;
        if (!string.Equals(_content, next, StringComparison.Ordinal))
        {
            _content = next;
            RecalculateScrollRange();
        }

        ScrollToBottom();
        Invalidate();
        if (IsHandleCreated && Visible)
        {
            Update();
        }
    }

    protected override void OnFontChanged(EventArgs e)
    {
        base.OnFontChanged(e);
        RecalculateScrollRange();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        RecalculateScrollRange();
        ScrollToBottom();
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        if (!_scrollBar.Visible)
        {
            return;
        }

        var delta = e.Delta > 0 ? -ScrollStep * 3 : ScrollStep * 3;
        SetScrollValue(_scrollBar.Value + delta);
        base.OnMouseWheel(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.Clear(BackColor);
        if (_content.Length == 0)
        {
            return;
        }

        var textWidth = Math.Max(1, ClientSize.Width - Padding.Horizontal - (_scrollBar.Visible ? _scrollBar.Width : 0));
        var bounds = new Rectangle(
            Padding.Left,
            Padding.Top - _scrollBar.Value,
            textWidth,
            Math.Max(_contentHeight, ClientSize.Height));
        TextRenderer.DrawText(
            e.Graphics,
            _content,
            Font,
            bounds,
            ForeColor,
            TextFormatFlags.TextBoxControl
            | TextFormatFlags.WordBreak
            | TextFormatFlags.PreserveGraphicsClipping);
    }

    private void RecalculateScrollRange()
    {
        if (_scrollBar is null || !IsHandleCreated || ClientSize.Width <= 0)
        {
            return;
        }

        var widthWithoutScrollBar = Math.Max(1, ClientSize.Width - Padding.Horizontal);
        var viewportHeight = Math.Max(1, ClientSize.Height - Padding.Vertical);
        var measuredHeight = MeasureContentHeight(widthWithoutScrollBar);
        var needsScrollBar = measuredHeight > viewportHeight;

        if (needsScrollBar != _scrollBar.Visible)
        {
            _scrollBar.Visible = needsScrollBar;
            var measuredWidth = Math.Max(1, widthWithoutScrollBar - (needsScrollBar ? _scrollBar.Width : 0));
            measuredHeight = MeasureContentHeight(measuredWidth);
            needsScrollBar = measuredHeight > viewportHeight;
            _scrollBar.Visible = needsScrollBar;
        }

        _contentHeight = measuredHeight + (needsScrollBar ? Math.Max(10, Font.Height) : 0);

        _scrollBar.Minimum = 0;
        _scrollBar.LargeChange = viewportHeight;
        _scrollBar.Maximum = Math.Max(0, _contentHeight - 1);
    }

    private int MeasureContentHeight(int width)
        => _content.Length == 0
            ? 0
            : TextRenderer.MeasureText(
                _content,
                Font,
                new Size(width, int.MaxValue),
                TextFormatFlags.TextBoxControl | TextFormatFlags.WordBreak).Height;

    private void ScrollToBottom()
    {
        if (!_scrollBar.Visible)
        {
            _scrollBar.Value = 0;
            return;
        }

        SetScrollValue(_scrollBar.Maximum - _scrollBar.LargeChange + 1);
    }

    private void SetScrollValue(int value)
    {
        var maximumValue = Math.Max(_scrollBar.Minimum, _scrollBar.Maximum - _scrollBar.LargeChange + 1);
        _scrollBar.Value = Math.Clamp(value, _scrollBar.Minimum, maximumValue);
    }
}
