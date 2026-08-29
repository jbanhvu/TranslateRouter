namespace MeetingInterpreter.Controls;

public static class UiTheme
{
    public static readonly Color Background = Color.FromArgb(245, 246, 248);
    public static readonly Color CardBackground = Color.White;
    public static readonly Color Border = Color.FromArgb(224, 228, 234);
    public static readonly Color Primary = Color.FromArgb(28, 92, 170);
    public static readonly Color PrimaryHover = Color.FromArgb(21, 76, 145);
    public static readonly Color TextPrimary = Color.FromArgb(31, 41, 55);
    public static readonly Color TextSecondary = Color.FromArgb(92, 103, 118);
    public static readonly Color Success = Color.FromArgb(24, 128, 72);
    public static readonly Color Warning = Color.FromArgb(172, 112, 20);
    public static readonly Color Error = Color.FromArgb(190, 50, 50);
    public static readonly Color Neutral = Color.FromArgb(107, 114, 128);

    public static readonly Font AppTitleFont = new("Segoe UI", 19F, FontStyle.Bold);
    public static readonly Font SubtitleFont = new("Segoe UI", 9.75F, FontStyle.Regular);
    public static readonly Font CardTitleFont = new("Segoe UI", 10.5F, FontStyle.Bold);
    public static readonly Font BodyFont = new("Segoe UI", 9.75F, FontStyle.Regular);
    public static readonly Font StrongBodyFont = new("Segoe UI", 9.75F, FontStyle.Bold);
    public static readonly Font StatusFont = new("Segoe UI", 15F, FontStyle.Bold);

    public static void StylePrimaryButton(Button button)
    {
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderSize = 0;
        button.BackColor = Primary;
        button.ForeColor = Color.White;
        button.Font = new Font("Segoe UI", 10.5F, FontStyle.Bold);
        button.Height = 46;
        button.Cursor = Cursors.Hand;
    }

    public static void StyleSecondaryButton(Button button)
    {
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderColor = Border;
        button.FlatAppearance.BorderSize = 1;
        button.BackColor = Color.White;
        button.ForeColor = TextPrimary;
        button.Font = BodyFont;
        button.Cursor = Cursors.Hand;
    }

    public static void StyleCombo(ComboBox comboBox)
    {
        comboBox.DropDownStyle = ComboBoxStyle.DropDownList;
        comboBox.Font = BodyFont;
        comboBox.IntegralHeight = false;
        comboBox.Height = 28;
    }
}
