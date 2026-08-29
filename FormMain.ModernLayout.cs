using MeetingInterpreter.Controls;
using MeetingInterpreter.Models;

namespace MeetingInterpreter;

public partial class FormMain
{
    private Panel _mainContent = null!;
    private TableLayoutPanel _mainLayout = null!;
    private Label _lblHeaderTitle = null!;
    private Label _lblHeaderSubtitle = null!;
    private Label _lblHeaderStatus = null!;
    private Label _lblInputStatus = null!;
    private Label _lblOutput1Status = null!;
    private Label _lblOutput2Status = null!;
    private Label _lblQueueStatus = null!;
    private Label _lblCurrentSourceTitle = null!;
    private Label _lblCurrentTargetTitle = null!;
    private Label _lblCurrentRoute = null!;
    private Label _lblCurrentElapsed = null!;
    private RichTextBox _rtbCurrentSource = null!;
    private RichTextBox _rtbCurrentTarget = null!;
    private ToolTip _toolTip = null!;

    private void BuildModernLayout()
    {
        SuspendLayout();

        Controls.Clear();
        BackColor = UiTheme.Background;
        Font = UiTheme.BodyFont;
        Text = "Phiên dịch cuộc họp Việt - Hàn";
        MinimumSize = new Size(1080, 760);
        AutoScaleMode = AutoScaleMode.Font;

        _toolTip = new ToolTip();

        _mainContent = new Panel
        {
            BackColor = UiTheme.Background
        };
        Controls.Add(_mainContent);

        _mainLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            BackColor = UiTheme.Background
        };
        _mainLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 76));
        _mainLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 300));
        _mainLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 216));
        _mainLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _mainContent.Controls.Add(_mainLayout);

        BuildHeader();
        BuildDashboardCards();
        BuildCurrentTranslationCard();
        BuildHistoryCard();
        HideAdvancedMainControls();
        ApplyCommonControlStyle();

        ResumeLayout(false);
    }

    private void BuildHeader()
    {
        var header = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = UiTheme.Background,
            Padding = new Padding(0, 4, 0, 10)
        };
        _mainLayout.Controls.Add(header, 0, 0);

        _lblHeaderTitle = new Label
        {
            AutoSize = true,
            Text = "Phiên dịch cuộc họp Việt - Hàn",
            Font = UiTheme.AppTitleFont,
            ForeColor = UiTheme.TextPrimary,
            Location = new Point(2, 3)
        };
        header.Controls.Add(_lblHeaderTitle);

        _lblHeaderSubtitle = new Label
        {
            AutoSize = true,
            Text = "Phiên dịch 2 chiều thời gian thực",
            Font = UiTheme.SubtitleFont,
            ForeColor = UiTheme.TextSecondary,
            Location = new Point(4, 42)
        };
        header.Controls.Add(_lblHeaderSubtitle);

        btnSettings = new Button
        {
            Text = "Cài đặt",
            Size = new Size(108, 34)
        };
        btnSettings.Click += btnSettings_Click;
        UiTheme.StyleSecondaryButton(btnSettings);
        header.Controls.Add(btnSettings);
        _toolTip.SetToolTip(btnSettings, "Mở cài đặt kết nối, nhận dạng và âm thanh nâng cao");

        _lblHeaderStatus = new Label
        {
            AutoSize = true,
            Text = "● Sẵn sàng",
            Font = UiTheme.StrongBodyFont,
            ForeColor = UiTheme.Success,
            TextAlign = ContentAlignment.MiddleRight
        };
        header.Controls.Add(_lblHeaderStatus);

        header.Resize += (_, _) =>
        {
            btnSettings.Location = new Point(header.Width - btnSettings.Width, 18);
            _lblHeaderStatus.Location = new Point(
                Math.Max(0, btnSettings.Left - _lblHeaderStatus.Width - 18),
                25);
        };
    }

    private void BuildDashboardCards()
    {
        var dashboard = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 1,
            BackColor = UiTheme.Background,
            Padding = new Padding(0, 0, 0, 16)
        };
        dashboard.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
        dashboard.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 47));
        dashboard.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 28));
        _mainLayout.Controls.Add(dashboard, 0, 1);

        dashboard.Controls.Add(BuildModelCard(), 0, 0);
        dashboard.Controls.Add(BuildAudioCard(), 1, 0);
        dashboard.Controls.Add(BuildStatusCard(), 2, 0);
    }

    private Control BuildModelCard()
    {
        var card = CreateCard();
        lblEngineTitle = CreateCardTitle("MÔ HÌNH PHIÊN DỊCH");
        cmbInterpreterEngine = new ComboBox { Width = 245 };
        cmbInterpreterEngine.SelectedIndexChanged += cmbInterpreterEngine_SelectedIndexChanged;
        lblEngineDescription = new Label
        {
            Location = new Point(18, 94),
            Size = new Size(260, 72),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            ForeColor = UiTheme.TextSecondary,
            Font = UiTheme.BodyFont,
            Text = "Gemini trực tiếp nghe, nhận dạng và dịch nội dung."
        };

        card.Controls.Add(lblEngineTitle);
        card.Controls.Add(cmbInterpreterEngine);
        card.Controls.Add(lblEngineDescription);
        lblEngineTitle.Location = new Point(18, 18);
        cmbInterpreterEngine.Location = new Point(18, 54);
        cmbInterpreterEngine.Size = new Size(245, 30);
        cmbInterpreterEngine.BringToFront();
        lblEngineTitle.BringToFront();
        return card;
    }

    private Control BuildAudioCard()
    {
        var card = CreateCard();
        _lblAudioDevicesSection = CreateCardTitle("THIẾT BỊ ÂM THANH");
        card.Controls.Add(_lblAudioDevicesSection);

        var grid = new TableLayoutPanel
        {
            Location = new Point(18, 48),
            Size = new Size(card.Width - 36, 180),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            ColumnCount = 1,
            RowCount = 3
        };
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
        card.Controls.Add(grid);

        _lblInputMicrophone = new Label();
        _lblOutput1 = new Label();
        _lblOutput2 = new Label();
        _lblInputStatus = new Label();
        _lblOutput1Status = new Label();
        _lblOutput2Status = new Label();
        grid.Controls.Add(CreateDeviceRow(_lblInputMicrophone, "Microphone phòng họp", cmbInputDevice, btnRefreshDevices, _lblInputStatus, "Đã kết nối"), 0, 0);
        grid.Controls.Add(CreateDeviceRow(_lblOutput1, "Loa phòng họp", cmbOutput1Device, btnTestOutput1, _lblOutput1Status, "Phát bản dịch tiếng Việt"), 0, 1);
        grid.Controls.Add(CreateDeviceRow(_lblOutput2, "Tai nghe quản lý", cmbOutput2Device, btnTestOutput2, _lblOutput2Status, "Phát bản dịch tiếng Hàn"), 0, 2);
        return card;
    }

    private Control BuildStatusCard()
    {
        var card = CreateCard();
        _lblSessionControlsSection = CreateCardTitle("TRẠNG THÁI");
        card.Controls.Add(_lblSessionControlsSection);

        lblState.Font = UiTheme.StatusFont;
        lblState.ForeColor = UiTheme.Success;
        lblState.Text = "● Sẵn sàng";
        lblState.Location = new Point(18, 48);
        lblState.Size = new Size(260, 32);
        lblState.TextAlign = ContentAlignment.MiddleLeft;
        card.Controls.Add(lblState);

        lblStatus.Font = UiTheme.BodyFont;
        lblStatus.ForeColor = UiTheme.TextSecondary;
        lblStatus.Location = new Point(20, 80);
        lblStatus.Size = new Size(310, 38);
        lblStatus.Text = "Chọn thiết bị và nhấn Bắt đầu.";
        card.Controls.Add(lblStatus);

        UiTheme.StylePrimaryButton(btnStart);
        UiTheme.StylePrimaryButton(btnStop);
        btnStart.Text = "BẮT ĐẦU PHIÊN DỊCH";
        btnStop.Text = "DỪNG PHIÊN DỊCH";
        btnStart.Location = new Point(18, 124);
        btnStop.Location = btnStart.Location;
        btnStart.Size = new Size(294, 46);
        btnStop.Size = btnStart.Size;
        btnStop.Visible = false;
        card.Controls.Add(btnStart);
        card.Controls.Add(btnStop);

        _lblMicLevelSection = new Label
        {
            AutoSize = true,
            Text = "Mức Microphone",
            Font = UiTheme.StrongBodyFont,
            ForeColor = UiTheme.TextPrimary,
            Location = new Point(18, 174)
        };
        card.Controls.Add(_lblMicLevelSection);

        lblMicLevel.Font = UiTheme.BodyFont;
        lblMicLevel.ForeColor = UiTheme.TextSecondary;
        lblMicLevel.Location = new Point(18, 197);
        lblMicLevel.Size = new Size(120, 22);
        card.Controls.Add(lblMicLevel);

        prgMicLevel.Location = new Point(142, 198);
        prgMicLevel.Size = new Size(170, 16);
        card.Controls.Add(prgMicLevel);

        lblMicQuality.Font = UiTheme.BodyFont;
        lblMicQuality.ForeColor = UiTheme.Neutral;
        lblMicQuality.Location = new Point(18, 220);
        lblMicQuality.Size = new Size(220, 22);
        card.Controls.Add(lblMicQuality);

        _lblQueueStatus = new Label
        {
            Text = "Đang chờ: 0 câu",
            Font = UiTheme.BodyFont,
            ForeColor = UiTheme.TextSecondary,
            Location = new Point(18, 246),
            Size = new Size(260, 22)
        };
        card.Controls.Add(_lblQueueStatus);

        return card;
    }

    private void BuildCurrentTranslationCard()
    {
        var card = CreateCard();
        _mainLayout.Controls.Add(card, 0, 2);

        lblCurrentContentTitle.Font = UiTheme.CardTitleFont;
        lblCurrentContentTitle.ForeColor = UiTheme.TextPrimary;
        lblCurrentContentTitle.Text = "NỘI DUNG PHIÊN DỊCH";
        lblCurrentContentTitle.Location = new Point(18, 16);
        card.Controls.Add(lblCurrentContentTitle);

        _lblCurrentSourceTitle = CreateSectionLabel("NỘI DUNG NÓI");
        _lblCurrentSourceTitle.Location = new Point(18, 52);
        card.Controls.Add(_lblCurrentSourceTitle);

        _rtbCurrentSource = CreateReadOnlyTranscriptBox();
        _rtbCurrentSource.Location = new Point(18, 78);
        _rtbCurrentSource.Size = new Size(540, 80);
        card.Controls.Add(_rtbCurrentSource);

        var arrow = new Label
        {
            Text = "→",
            Font = new Font("Segoe UI", 18F, FontStyle.Bold),
            ForeColor = UiTheme.Neutral,
            TextAlign = ContentAlignment.MiddleCenter,
            Location = new Point(570, 92),
            Size = new Size(44, 44)
        };
        card.Controls.Add(arrow);

        _lblCurrentTargetTitle = CreateSectionLabel("NỘI DUNG DỊCH");
        _lblCurrentTargetTitle.Location = new Point(626, 52);
        card.Controls.Add(_lblCurrentTargetTitle);

        _rtbCurrentTarget = CreateReadOnlyTranscriptBox();
        _rtbCurrentTarget.Location = new Point(626, 78);
        _rtbCurrentTarget.Size = new Size(540, 80);
        card.Controls.Add(_rtbCurrentTarget);

        _lblCurrentRoute = new Label
        {
            Text = "Chưa có nội dung phiên dịch. Hệ thống sẽ hiển thị nội dung tại đây khi cuộc họp bắt đầu.",
            Font = UiTheme.BodyFont,
            ForeColor = UiTheme.TextSecondary,
            Location = new Point(18, 166),
            Size = new Size(850, 24)
        };
        card.Controls.Add(_lblCurrentRoute);

        _lblCurrentElapsed = new Label
        {
            Text = string.Empty,
            Font = UiTheme.StrongBodyFont,
            ForeColor = UiTheme.TextSecondary,
            TextAlign = ContentAlignment.MiddleRight,
            Location = new Point(1030, 166),
            Size = new Size(130, 24)
        };
        card.Controls.Add(_lblCurrentElapsed);

        txtCurrentContent.Visible = false;
    }

    private void BuildHistoryCard()
    {
        var card = CreateCard();
        _mainLayout.Controls.Add(card, 0, 3);

        _lblTranslationHistory = CreateCardTitle("LỊCH SỬ PHIÊN DỊCH");
        _lblTranslationHistory.Location = new Point(18, 16);
        card.Controls.Add(_lblTranslationHistory);

        btnExportHistory = new Button
        {
            Text = "Xuất Excel",
            Size = new Size(112, 32),
            Anchor = AnchorStyles.Top | AnchorStyles.Right
        };
        btnExportHistory.Click += btnExportHistory_Click;
        UiTheme.StyleSecondaryButton(btnExportHistory);
        card.Controls.Add(btnExportHistory);

        dgvTranslations.Location = new Point(18, 58);
        dgvTranslations.Size = new Size(1140, 260);
        dgvTranslations.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        card.Controls.Add(dgvTranslations);

        card.Resize += (_, _) =>
        {
            btnExportHistory.Location = new Point(Math.Max(18, card.ClientSize.Width - btnExportHistory.Width - 18), 14);
            dgvTranslations.Width = Math.Max(300, card.ClientSize.Width - 36);
            dgvTranslations.Height = Math.Max(160, card.ClientSize.Height - 76);
        };
    }

    private Panel CreateDeviceRow(Label label, string title, ComboBox comboBox, Button button, Label statusLabel, string subtitle)
    {
        var row = new Panel { Dock = DockStyle.Fill, Padding = new Padding(0, 0, 0, 3) };

        label.Text = title;
        label.Font = UiTheme.StrongBodyFont;
        label.ForeColor = UiTheme.TextPrimary;
        label.Location = new Point(0, 0);
        label.Size = new Size(160, 20);
        row.Controls.Add(label);

        statusLabel.Text = "● " + subtitle;
        statusLabel.Font = new Font("Segoe UI", 8.5F);
        statusLabel.ForeColor = UiTheme.TextSecondary;
        statusLabel.Location = new Point(164, 1);
        statusLabel.Size = new Size(280, 19);
        row.Controls.Add(statusLabel);

        comboBox.Location = new Point(0, 25);
        comboBox.Size = new Size(360, 30);
        comboBox.Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right;
        row.Controls.Add(comboBox);

        button.Location = new Point(370, 24);
        button.Size = new Size(128, 30);
        button.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        row.Controls.Add(button);

        row.Resize += (_, _) =>
        {
            button.Left = Math.Max(0, row.Width - button.Width);
            comboBox.Width = Math.Max(180, button.Left - 10);
        };

        return row;
    }

    private static CardPanel CreateCard()
        => new()
        {
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 0, 16, 0)
        };

    private static Label CreateCardTitle(string text)
        => new()
        {
            AutoSize = true,
            Text = text,
            Font = UiTheme.CardTitleFont,
            ForeColor = UiTheme.TextPrimary
        };

    private static Label CreateSectionLabel(string text)
        => new()
        {
            AutoSize = true,
            Text = text,
            Font = new Font("Segoe UI", 9F, FontStyle.Bold),
            ForeColor = UiTheme.TextSecondary
        };

    private static RichTextBox CreateReadOnlyTranscriptBox()
        => new()
        {
            ReadOnly = true,
            BorderStyle = BorderStyle.None,
            BackColor = UiTheme.CardBackground,
            ForeColor = UiTheme.TextPrimary,
            Font = new Font("Segoe UI", 12F),
            DetectUrls = false,
            ScrollBars = RichTextBoxScrollBars.Vertical
        };

    private void ApplyCommonControlStyle()
    {
        foreach (var combo in new[] { cmbInterpreterEngine, cmbInputDevice, cmbOutput1Device, cmbOutput2Device, cmbDisplayLanguage, cmbSpeechModel })
        {
            UiTheme.StyleCombo(combo);
        }

        foreach (var button in new[] { btnRefreshDevices, btnTestOutput1, btnTestOutput2, btnBrowseCredential, btnTestGemini })
        {
            UiTheme.StyleSecondaryButton(button);
        }

        _toolTip.SetToolTip(btnRefreshDevices, "Làm mới danh sách thiết bị âm thanh");
        _toolTip.SetToolTip(btnTestOutput1, "Phát thử âm thanh qua loa phòng họp");
        _toolTip.SetToolTip(btnTestOutput2, "Phát thử âm thanh qua tai nghe quản lý");
        _toolTip.SetToolTip(cmbInterpreterEngine, "Chọn engine phiên dịch đang dùng cho phiên họp");
    }

    private void ApplyModernUiLanguage()
    {
        if (_lblHeaderTitle is null)
        {
            return;
        }

        Text = T("Phiên dịch cuộc họp Việt - Hàn", "Vietnamese - Korean Meeting Interpreter", "베트남어 - 한국어 회의 통역");
        _lblHeaderTitle.Text = T("Phiên dịch cuộc họp Việt - Hàn", "Vietnamese - Korean Meeting Interpreter", "베트남어 - 한국어 회의 통역");
        _lblHeaderSubtitle.Text = T("Phiên dịch 2 chiều thời gian thực", "Real-time two-way interpretation", "실시간 양방향 통역");
        lblEngineTitle.Text = T("MÔ HÌNH PHIÊN DỊCH", "INTERPRETER ENGINE", "통역 엔진");
        _lblAudioDevicesSection!.Text = T("THIẾT BỊ ÂM THANH", "AUDIO DEVICES", "오디오 장치");
        _lblInputMicrophone!.Text = T("Microphone phòng họp", "Meeting room microphone", "회의실 마이크");
        _lblOutput1!.Text = T("Loa phòng họp", "Room speaker", "회의실 스피커");
        _lblOutput2!.Text = T("Tai nghe quản lý", "Manager headset", "관리자 헤드셋");
        _lblInputStatus.Text = T("● Đã kết nối", "● Connected", "● 연결됨");
        _lblOutput1Status.Text = T("● Phát bản dịch tiếng Việt", "● Plays Vietnamese translation", "● 베트남어 번역 재생");
        _lblOutput2Status.Text = T("● Phát bản dịch tiếng Hàn", "● Plays Korean translation", "● 한국어 번역 재생");
        _lblSessionControlsSection!.Text = T("TRẠNG THÁI", "STATUS", "상태");
        _lblMicLevelSection!.Text = T("Mức Microphone", "Microphone level", "마이크 레벨");
        _lblQueueStatus.Text = T("Đang chờ: 0 câu", "Pending: 0", "대기: 0");
        lblCurrentContentTitle.Text = T("NỘI DUNG PHIÊN DỊCH", "CURRENT TRANSLATION", "현재 통역");
        _lblCurrentSourceTitle.Text = T("NỘI DUNG NÓI", "SPOKEN CONTENT", "말한 내용");
        _lblCurrentTargetTitle.Text = T("NỘI DUNG DỊCH", "TRANSLATED CONTENT", "번역 내용");
        if (string.IsNullOrWhiteSpace(_rtbCurrentSource.Text) && string.IsNullOrWhiteSpace(_rtbCurrentTarget.Text))
        {
            _lblCurrentRoute.Text = T(
                "Chưa có nội dung phiên dịch. Hệ thống sẽ hiển thị nội dung tại đây khi cuộc họp bắt đầu.",
                "No interpretation content yet. Content will appear here when the meeting starts.",
                "아직 통역 내용이 없습니다. 회의가 시작되면 여기에 표시됩니다.");
        }

        _lblTranslationHistory!.Text = T("LỊCH SỬ PHIÊN DỊCH", "TRANSLATION HISTORY", "통역 기록");
        btnExportHistory.Text = T("Xuất Excel", "Export Excel", "Excel 내보내기");
        _toolTip.SetToolTip(btnSettings, T("Mở cài đặt kết nối, nhận dạng và âm thanh nâng cao", "Open connection, recognition, and advanced audio settings", "연결, 인식 및 고급 오디오 설정 열기"));
        _toolTip.SetToolTip(btnRefreshDevices, T("Làm mới danh sách thiết bị âm thanh", "Refresh audio device list", "오디오 장치 목록 새로 고침"));
        _toolTip.SetToolTip(btnTestOutput1, T("Phát thử âm thanh qua loa phòng họp", "Test audio through the room speaker", "회의실 스피커로 테스트 재생"));
        _toolTip.SetToolTip(btnTestOutput2, T("Phát thử âm thanh qua tai nghe quản lý", "Test audio through the manager headset", "관리자 헤드셋으로 테스트 재생"));
        _toolTip.SetToolTip(cmbInterpreterEngine, T("Chọn engine phiên dịch đang dùng cho phiên họp", "Choose the interpreter engine for this meeting", "회의에 사용할 통역 엔진 선택"));
    }

    private void HideAdvancedMainControls()
    {
        _lblCredentialFile = new Label { Visible = false };
        _lblGoogleSection = new Label { Visible = false };
        _lblConnectionStatus = new Label { Visible = false };
        _lblDisplayLanguage = new Label { Visible = false };
        _lblVadSection = new Label { Visible = false };
        _lblSilenceDuration = new Label { Visible = false };

        txtGoogleCredentialPath.Visible = false;
        btnBrowseCredential.Visible = false;
        lblGoogleStatus.Visible = false;
        cmbDisplayLanguage.Visible = false;
        trkVadThreshold.Visible = false;
        lblVadThreshold.Visible = false;
        nudSilenceDuration.Visible = false;
        chkSuppressHeadset.Visible = false;
        chkRecognitionOnly.Visible = false;
        chkSaveDebugAudio.Visible = false;
        cmbSpeechModel.Visible = false;
        lblSpeechModel.Visible = false;

        lblGeminiSection = new Label { Visible = false };
        lblGeminiApiKey = new Label { Visible = false };
        txtGeminiApiKey = new TextBox { UseSystemPasswordChar = true, Visible = false };
        btnTestGemini = new Button { Visible = false };
        lblGeminiStatus = new Label { Visible = false };
    }

    private void ApplyModernResponsiveLayout()
    {
        if (_mainContent is null || ClientSize.Width <= 0)
        {
            return;
        }

        var contentWidth = Math.Min(1380, Math.Max(1020, ClientSize.Width - 48));
        var left = Math.Max(24, (ClientSize.Width - contentWidth) / 2);
        _mainContent.SetBounds(left, 18, contentWidth, Math.Max(640, ClientSize.Height - 36));

        if (_rtbCurrentSource is not null && _rtbCurrentTarget is not null)
        {
            var cardWidth = _rtbCurrentSource.Parent?.ClientSize.Width ?? contentWidth;
            var gap = 68;
            var columnWidth = Math.Max(280, (cardWidth - 36 - gap) / 2);
            _rtbCurrentSource.Width = columnWidth;
            _rtbCurrentTarget.Left = 18 + columnWidth + gap;
            _rtbCurrentTarget.Width = columnWidth;
            _lblCurrentTargetTitle.Left = _rtbCurrentTarget.Left;
            _lblCurrentElapsed.Left = Math.Max(18, cardWidth - _lblCurrentElapsed.Width - 18);
            _lblCurrentRoute.Width = Math.Max(300, _lblCurrentElapsed.Left - 28);
        }
    }

    private void UpdateCurrentPreview(string title, string content)
    {
        if (_rtbCurrentSource is null || _rtbCurrentTarget is null)
        {
            txtCurrentContent.Text = content;
            return;
        }

        if (title.Contains("dịch", StringComparison.OrdinalIgnoreCase)
            || title.Contains("Translated", StringComparison.OrdinalIgnoreCase))
        {
            _lblCurrentTargetTitle.Text = T("NỘI DUNG DỊCH", "TRANSLATED CONTENT", "번역 내용");
            _rtbCurrentTarget.Text = content;
        }
        else
        {
            _lblCurrentSourceTitle.Text = T("NỘI DUNG NÓI", "SPOKEN CONTENT", "말한 내용");
            _rtbCurrentSource.Text = content;
            _rtbCurrentTarget.Clear();
            _lblCurrentElapsed.Text = string.Empty;
        }

        _lblCurrentRoute.Text = T("Đang xử lý nội dung phiên dịch...", "Processing interpretation content...", "통역 내용을 처리 중입니다...");
        txtCurrentContent.Text = content;
    }

    private void ShowCurrentCellValue(string title, string content)
    {
        _lblCurrentSourceTitle.Text = title.ToUpperInvariant();
        _rtbCurrentSource.Text = content;
        _lblCurrentTargetTitle.Text = T("NỘI DUNG ĐẦY ĐỦ", "FULL CONTENT", "전체 내용");
        _rtbCurrentTarget.Clear();
        _lblCurrentRoute.Text = T("Đang xem nội dung từ lịch sử phiên dịch.", "Viewing content from translation history.", "통역 기록의 내용을 보는 중입니다.");
        _lblCurrentElapsed.Text = string.Empty;
        txtCurrentContent.Text = content;
    }

    private void UpdateStateVisual(InterpreterState state)
    {
        if (_lblHeaderStatus is null)
        {
            return;
        }

        var color = state switch
        {
            InterpreterState.Error => UiTheme.Error,
            InterpreterState.Idle => UiTheme.Success,
            InterpreterState.Listening or InterpreterState.SpeechDetected => UiTheme.Success,
            InterpreterState.ProcessingSpeech or InterpreterState.Translating or InterpreterState.Synthesizing => UiTheme.Warning,
            InterpreterState.Playing => UiTheme.Primary,
            _ => UiTheme.Neutral
        };

        lblState.ForeColor = color;
        _lblHeaderStatus.ForeColor = color;
        _lblHeaderStatus.Text = "● " + GetStateDisplayName(state).Replace("● ", string.Empty);
    }

    private void SetDashboardStatus(string stateText, string detailText, Color color)
    {
        if (_lblHeaderStatus is null)
        {
            return;
        }

        var text = "● " + stateText.Replace("● ", string.Empty).Replace("â— ", string.Empty);
        lblState.Text = text;
        lblState.ForeColor = color;
        lblStatus.Text = detailText;
        _lblHeaderStatus.Text = text;
        _lblHeaderStatus.ForeColor = color;
    }

    private static string FormatSeconds(double milliseconds)
        => $"{milliseconds / 1000.0:0.00} giây";

    private void SyncAdvancedControlsFromSettings()
    {
        trkVadThreshold.Value = Math.Clamp((int)Math.Round(_settings.VadThreshold * 1000), trkVadThreshold.Minimum, trkVadThreshold.Maximum);
        nudSilenceDuration.Value = Math.Clamp(_settings.SilenceDurationMs, (int)nudSilenceDuration.Minimum, (int)nudSilenceDuration.Maximum);
        chkSuppressHeadset.Checked = _settings.SuppressMicDuringHeadsetPlayback;
        chkRecognitionOnly.Checked = _settings.RecognitionOnlyMode;
        chkSaveDebugAudio.Checked = _settings.SpeechRecognition.SaveRecognitionAudioForDebug;
        cmbSpeechModel.SelectedItem = _settings.SpeechRecognition.Model;

        foreach (var item in cmbDisplayLanguage.Items.OfType<DisplayLanguageSelectionItem>())
        {
            if (item.Language == _displayLanguage)
            {
                cmbDisplayLanguage.SelectedItem = item;
                break;
            }
        }

        UpdateSettingsLabels();
    }
}
