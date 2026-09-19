using System.Text;
using MeetingInterpreter.Controls;
using MeetingInterpreter.Models;
using MeetingInterpreter.Services;

namespace MeetingInterpreter;

public partial class FormMain
{
    private const int MaximumTranscriptCharacters = 24000;
    private Panel _mainContent = null!;
    private TableLayoutPanel _mainLayout = null!;
    private Label _lblHeaderStatus = null!;
    private PictureBox _picAppLogo = null!;
    private Label _lblInputLevelTitle = null!;
    private Label _lblInputLevelValue = null!;
    private AudioLevelMeter _inputLevelMeter = null!;
    private Label _lblInputStatus = null!;
    private Label _lblOutput1Status = null!;
    private Label _lblOutput2Status = null!;
    private Label _lblQueueStatus = null!;
    private Label _lblCurrentSourceTitle = null!;
    private Label _lblCurrentTargetTitle = null!;
    private TranscriptView _rtbCurrentSource = null!;
    private TranscriptView _rtbCurrentTarget = null!;
    private ToolTip _toolTip = null!;
    private readonly StringBuilder _sourceTranscriptHistory = new();
    private readonly StringBuilder _targetTranscriptHistory = new();
    private string _liveSourcePreview = string.Empty;
    private string _liveTargetPreview = string.Empty;
    private SupportedLanguage _liveSourceLanguage = SupportedLanguage.Unknown;
    private string _lastCommittedSourceText = string.Empty;
    private DateTime _lastCommittedSourceAtUtc;
    private SupportedLanguage _lastCommittedSourceLanguage = SupportedLanguage.Unknown;
    private readonly object _advancedRealtimePreviewSyncRoot = new();
    private InterpreterContentPreviewEventArgs? _latestAdvancedRealtimePreview;
    private System.Windows.Forms.Timer? _advancedRealtimePreviewTimer;

    private void BuildModernLayout()
    {
        SuspendLayout();
        Controls.Clear();
        BackColor = UiTheme.Background;
        Font = UiTheme.BodyFont;
        Text = "Phiên dịch cuộc họp Việt - Hàn";
        Icon = LoadEmbeddedAppIcon();
        MinimumSize = new Size(1080, 720);
        AutoScaleMode = AutoScaleMode.Font;
        _toolTip = new ToolTip();
        _mainContent = new Panel { BackColor = UiTheme.Background };
        Controls.Add(_mainContent);

        _mainLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            BackColor = UiTheme.Background
        };
        _mainLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 92));
        _mainLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 70));
        _mainLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 30));
        _mainContent.Controls.Add(_mainLayout);

        BuildSessionBar();
        BuildCurrentTranslationSurface();
        BuildHistorySurface();
        HideAdvancedMainControls();
        ApplyCommonControlStyle();
        ResumeLayout(false);
    }

    private void BuildSessionBar()
    {
        var bar = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = UiTheme.CardBackground,
            Padding = new Padding(20, 14, 20, 14),
            Margin = new Padding(0, 0, 0, 12)
        };
        bar.Paint += (_, e) =>
        {
            using var pen = new Pen(UiTheme.Border);
            e.Graphics.DrawRectangle(pen, 0, 0, Math.Max(0, bar.ClientSize.Width - 1), Math.Max(0, bar.ClientSize.Height - 1));
        };
        _mainLayout.Controls.Add(bar, 0, 0);

        var logoImage = LoadEmbeddedAppLogo();
        _picAppLogo = new PictureBox
        {
            Location = new Point(20, 15),
            Size = new Size(48, 48),
            BackColor = UiTheme.CardBackground,
            Image = logoImage,
            SizeMode = PictureBoxSizeMode.Zoom,
            TabStop = false
        };
        bar.Controls.Add(_picAppLogo);
        if (logoImage is not null)
        {
            Disposed += (_, _) => logoImage.Dispose();
        }

        lblState.Font = UiTheme.StatusFont;
        lblState.ForeColor = UiTheme.Warning;
        lblState.Text = "Cần cấu hình";
        lblState.Location = new Point(82, 15);
        lblState.Size = new Size(310, 30);
        lblState.TextAlign = ContentAlignment.MiddleLeft;
        bar.Controls.Add(lblState);

        lblStatus.Font = UiTheme.BodyFont;
        lblStatus.ForeColor = UiTheme.TextSecondary;
        lblStatus.Location = new Point(84, 47);
        lblStatus.Size = new Size(700, 24);
        lblStatus.AutoEllipsis = true;
        bar.Controls.Add(lblStatus);

        _lblHeaderStatus = new Label
        {
            Text = "✓ Cần cấu hình",
            Font = UiTheme.StrongBodyFont,
            ForeColor = UiTheme.Warning,
            TextAlign = ContentAlignment.MiddleRight,
            Visible = false
        };
        bar.Controls.Add(_lblHeaderStatus);

        var inputLevelPanel = new Panel
        {
            Size = new Size(246, 50),
            BackColor = UiTheme.CardBackground,
            Anchor = AnchorStyles.Top | AnchorStyles.Right
        };
        _lblInputLevelTitle = new Label
        {
            Text = "ÂM THANH ĐẦU VÀO",
            Font = new Font("Segoe UI", 8.5F, FontStyle.Bold),
            ForeColor = UiTheme.TextSecondary,
            Location = new Point(0, 0),
            Size = new Size(142, 18)
        };
        _lblInputLevelValue = new Label
        {
            Text = "Chưa bắt đầu",
            Font = UiTheme.StrongBodyFont,
            ForeColor = UiTheme.Neutral,
            Location = new Point(144, 0),
            Size = new Size(102, 18),
            TextAlign = ContentAlignment.TopRight,
            AutoEllipsis = true
        };
        _inputLevelMeter = new AudioLevelMeter
        {
            Location = new Point(0, 26),
            Size = new Size(246, 14),
            Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top
        };
        inputLevelPanel.Controls.Add(_lblInputLevelTitle);
        inputLevelPanel.Controls.Add(_lblInputLevelValue);
        inputLevelPanel.Controls.Add(_inputLevelMeter);
        bar.Controls.Add(inputLevelPanel);

        btnSettings = new Button
        {
            Text = "Cài đặt",
            Size = new Size(108, 50),
            Anchor = AnchorStyles.Top | AnchorStyles.Right
        };
        btnSettings.Click += btnSettings_Click;
        UiTheme.StyleSecondaryButton(btnSettings);
        bar.Controls.Add(btnSettings);
        _toolTip.SetToolTip(btnSettings, "Mở cài đặt mô hình, kết nối và thiết bị âm thanh");

        UiTheme.StylePrimaryButton(btnStart);
        UiTheme.StylePrimaryButton(btnStop);
        btnStart.Text = "BẮT ĐẦU";
        btnStop.Text = "DỪNG";
        btnStart.Size = new Size(210, 50);
        btnStop.Size = btnStart.Size;
        btnStop.Visible = false;
        btnStart.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        btnStop.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        bar.Controls.Add(btnStart);
        bar.Controls.Add(btnStop);
        bar.Resize += (_, _) =>
        {
            var startX = Math.Max(740, bar.ClientSize.Width - btnStart.Width - 20);
            var settingsX = startX - btnSettings.Width - 10;
            var meterX = settingsX - inputLevelPanel.Width - 16;
            btnStart.Location = new Point(startX, 15);
            btnStop.Location = btnStart.Location;
            btnSettings.Location = new Point(settingsX, 15);
            inputLevelPanel.Location = new Point(meterX, 15);
            lblState.Width = Math.Max(220, meterX - 102);
            lblStatus.Width = Math.Max(220, meterX - 104);
        };
    }

    private static Image? LoadEmbeddedAppLogo()
    {
        using var stream = typeof(FormMain).Assembly.GetManifestResourceStream("MeetingInterpreter.Assets.AppLogo.png");
        if (stream is null)
        {
            return null;
        }

        using var source = Image.FromStream(stream);
        return new Bitmap(source);
    }

    private static Icon? LoadEmbeddedAppIcon()
    {
        using var stream = typeof(FormMain).Assembly.GetManifestResourceStream("MeetingInterpreter.Assets.AppIcon.ico");
        if (stream is null)
        {
            return null;
        }

        using var source = new Icon(stream);
        return (Icon)source.Clone();
    }

    private void BuildCurrentTranslationSurface()
    {
        var surface = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = UiTheme.CardBackground,
            Margin = new Padding(0, 0, 0, 12),
            Padding = new Padding(20, 14, 20, 12)
        };
        _mainLayout.Controls.Add(surface, 0, 1);
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = UiTheme.CardBackground,
            ColumnCount = 1,
            RowCount = 1
        };
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        surface.Controls.Add(layout);

        lblCurrentContentTitle.Visible = false;

        var transcriptLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = UiTheme.CardBackground,
            ColumnCount = 2,
            RowCount = 1,
            Padding = new Padding(0)
        };
        transcriptLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        transcriptLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        layout.Controls.Add(transcriptLayout, 0, 0);
        transcriptLayout.Controls.Add(BuildTranscriptColumn(isSource: true), 0, 0);
        transcriptLayout.Controls.Add(BuildTranscriptColumn(isSource: false), 1, 0);

        txtCurrentContent.Visible = false;
    }

    private Control BuildTranscriptColumn(bool isSource)
    {
        var panel = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = isSource ? Color.White : Color.FromArgb(247, 250, 254),
            Padding = isSource ? new Padding(0, 0, 14, 0) : new Padding(14, 0, 0, 0),
            Margin = new Padding(0)
        };
        panel.Controls.Add(new Panel
        {
            Dock = isSource ? DockStyle.Right : DockStyle.Left,
            Width = 1,
            BackColor = UiTheme.Border
        });
        var column = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            BackColor = panel.BackColor,
            Padding = new Padding(4, 0, 4, 4)
        };
        column.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        column.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        panel.Controls.Add(column);
        column.BringToFront();
        var title = CreateSectionLabel(isSource ? "NỘI DUNG NÓI" : "NỘI DUNG DỊCH");
        title.Dock = DockStyle.Fill;
        title.TextAlign = ContentAlignment.MiddleLeft;
        column.Controls.Add(title, 0, 0);
        var transcript = CreateTranscriptView(panel.BackColor);
        transcript.Dock = DockStyle.Fill;
        column.Controls.Add(transcript, 0, 1);

        if (isSource)
        {
            _lblCurrentSourceTitle = title;
            _rtbCurrentSource = transcript;
        }
        else
        {
            _lblCurrentTargetTitle = title;
            _rtbCurrentTarget = transcript;
        }
        return panel;
    }

    private void BuildHistorySurface()
    {
        var surface = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = UiTheme.CardBackground,
            Padding = new Padding(20, 10, 20, 14)
        };
        _mainLayout.Controls.Add(surface, 0, 2);
        _lblTranslationHistory = CreateCardTitle("LỊCH SỬ PHIÊN DỊCH");
        _lblTranslationHistory.Location = new Point(20, 14);
        surface.Controls.Add(_lblTranslationHistory);
        btnExportHistory = new Button
        {
            Text = "Xuất Excel",
            Size = new Size(112, 32),
            Anchor = AnchorStyles.Top | AnchorStyles.Right
        };
        btnExportHistory.Click += btnExportHistory_Click;
        UiTheme.StyleSecondaryButton(btnExportHistory);
        surface.Controls.Add(btnExportHistory);
        dgvTranslations.Location = new Point(20, 52);
        dgvTranslations.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        surface.Controls.Add(dgvTranslations);
        surface.Resize += (_, _) =>
        {
            btnExportHistory.Location = new Point(Math.Max(20, surface.ClientSize.Width - btnExportHistory.Width - 20), 8);
            dgvTranslations.Size = new Size(Math.Max(300, surface.ClientSize.Width - 40), Math.Max(90, surface.ClientSize.Height - 66));
        };
    }

    private static Label CreateCardTitle(string text) => new()
    {
        AutoSize = true,
        Text = text,
        Font = UiTheme.CardTitleFont,
        ForeColor = UiTheme.TextPrimary
    };

    private static Label CreateSectionLabel(string text) => new()
    {
        AutoSize = false,
        Text = text,
        Font = new Font("Segoe UI", 10F, FontStyle.Bold),
        ForeColor = UiTheme.TextSecondary
    };

    private static TranscriptView CreateTranscriptView(Color background) => new()
    {
        BackColor = background,
        ForeColor = UiTheme.TextPrimary,
        Font = new Font("Segoe UI", 18F, FontStyle.Regular)
    };

    private void ApplyCommonControlStyle()
    {
        foreach (var combo in new[] { cmbInterpreterEngine, cmbInputDevice, cmbOutput1Device, cmbOutput2Device, cmbDisplayLanguage, cmbSpeechModel })
        {
            UiTheme.StyleCombo(combo);
        }
        _toolTip.SetToolTip(btnSettings, "Mở cài đặt mô hình, kết nối và thiết bị âm thanh");
    }

    private void ApplyModernUiLanguage()
    {
        if (_lblCurrentSourceTitle is null)
        {
            return;
        }

        Text = T("Phiên dịch cuộc họp Việt - Hàn", "Vietnamese - Korean Meeting Interpreter", "베트남어 - 한국어 회의 통역");
        btnSettings.Text = T("Cài đặt", "Settings", "설정");
        btnStart.Text = T("BẮT ĐẦU", "START", "시작");
        btnStop.Text = T("DỪNG", "STOP", "중지");
        _lblCurrentSourceTitle.Text = T("NỘI DUNG NÓI", "SPOKEN CONTENT", "말한 내용");
        _lblCurrentTargetTitle.Text = T("NỘI DUNG DỊCH", "TRANSLATED CONTENT", "번역 내용");
        _lblInputLevelTitle.Text = T("ÂM THANH ĐẦU VÀO", "INPUT AUDIO", "입력 오디오");
        _lblTranslationHistory!.Text = T("LỊCH SỬ PHIÊN DỊCH", "TRANSLATION HISTORY", "통역 기록");
        btnExportHistory.Text = T("Xuất Excel", "Export Excel", "Excel 내보내기");
        _toolTip.SetToolTip(btnSettings, T("Mở cài đặt mô hình, kết nối và thiết bị âm thanh", "Open engine, connection, and audio settings", "엔진, 연결 및 오디오 설정 열기"));
        UpdateDashboardMicrophoneLevel(
            0,
            sessionActive: _service is not null && _service.State != InterpreterState.Idle);
    }

    private void UpdateDashboardMicrophoneLevel(int level, bool sessionActive)
    {
        if (_inputLevelMeter is null || _lblInputLevelValue is null)
        {
            return;
        }

        level = Math.Clamp(level, 0, 100);
        if (!sessionActive)
        {
            _inputLevelMeter.ResetLevel();
            _lblInputLevelValue.Text = T("Chưa bắt đầu", "Not started", "시작 전");
            _lblInputLevelValue.ForeColor = UiTheme.Neutral;
            return;
        }

        _inputLevelMeter.SetLevel(level);
        var (text, color) = level switch
        {
            0 => (T("Không tín hiệu", "No signal", "신호 없음"), UiTheme.Neutral),
            < 8 => (T("Quá nhỏ", "Too low", "너무 작음"), UiTheme.Warning),
            < 25 => (T("Nhỏ", "Low", "작음"), UiTheme.Warning),
            <= 75 => (T("Tốt", "Good", "좋음"), UiTheme.Success),
            <= 90 => (T("Lớn", "High", "큼"), UiTheme.Warning),
            _ => (T("Quá lớn", "Too high", "너무 큼"), UiTheme.Error)
        };
        _lblInputLevelValue.Text = $"{text} {level}%";
        _lblInputLevelValue.ForeColor = color;
        _toolTip.SetToolTip(
            _inputLevelMeter,
            T($"Mức âm thanh đầu vào: {level}%", $"Input audio level: {level}%", $"입력 오디오 레벨: {level}%"));
    }

    private void HideAdvancedMainControls()
    {
        _lblCredentialFile = new Label { Visible = false };
        _lblGoogleSection = new Label { Visible = false };
        _lblConnectionStatus = new Label { Visible = false };
        _lblDisplayLanguage = new Label { Visible = false };
        _lblVadSection = new Label { Visible = false };
        _lblSilenceDuration = new Label { Visible = false };
        _lblAudioDevicesSection = new Label { Visible = false };
        _lblInputMicrophone = new Label { Visible = false };
        _lblOutput1 = new Label { Visible = false };
        _lblOutput2 = new Label { Visible = false };
        _lblSessionControlsSection = new Label { Visible = false };
        _lblMicLevelSection = new Label { Visible = false };
        _lblInputStatus = new Label { Visible = false };
        _lblOutput1Status = new Label { Visible = false };
        _lblOutput2Status = new Label { Visible = false };
        _lblQueueStatus = new Label { Visible = false };

        cmbInterpreterEngine = new ComboBox { Visible = false };
        cmbInterpreterEngine.SelectedIndexChanged += cmbInterpreterEngine_SelectedIndexChanged;
        lblEngineTitle = new Label { Visible = false };
        lblEngineDescription = new Label { Visible = false };
        lblGeminiSection = new Label { Visible = false };
        lblGeminiApiKey = new Label { Visible = false };
        txtGeminiApiKey = new TextBox { UseSystemPasswordChar = true, Visible = false };
        btnTestGemini = new Button { Visible = false };
        lblGeminiStatus = new Label { Visible = false };

        foreach (var control in new Control[]
        {
            txtGoogleCredentialPath, btnBrowseCredential, lblGoogleStatus, cmbDisplayLanguage,
            cmbInputDevice, cmbOutput1Device, cmbOutput2Device, btnRefreshDevices,
            btnTestOutput1, btnTestOutput2, prgMicLevel, lblMicLevel, lblMicQuality,
            trkVadThreshold, lblVadThreshold, nudSilenceDuration, chkSuppressHeadset,
            chkRecognitionOnly, chkSaveDebugAudio, cmbSpeechModel, lblSpeechModel, txtCurrentContent
        })
        {
            control.Visible = false;
        }
    }

    private void ApplyModernResponsiveLayout()
    {
        if (_mainContent is null || ClientSize.Width <= 0)
        {
            return;
        }
        var horizontalMargin = ClientSize.Width >= 1440 ? 38 : 24;
        var contentWidth = Math.Min(1540, Math.Max(1020, ClientSize.Width - horizontalMargin * 2));
        var left = Math.Max(horizontalMargin, (ClientSize.Width - contentWidth) / 2);
        _mainContent.SetBounds(left, 14, contentWidth, Math.Max(660, ClientSize.Height - 28));
    }

    private void ConfigureAdvancedRealtimePreviewRendering()
    {
        _advancedRealtimePreviewTimer = new System.Windows.Forms.Timer(components)
        {
            Interval = 40
        };
        _advancedRealtimePreviewTimer.Tick += (_, _) => RenderLatestAdvancedRealtimePreview();
        _advancedRealtimePreviewTimer.Start();
    }

    private void QueueAdvancedRealtimePreview(InterpreterContentPreviewEventArgs args)
    {
        lock (_advancedRealtimePreviewSyncRoot)
        {
            _latestAdvancedRealtimePreview = args;
        }
    }

    private void RenderLatestAdvancedRealtimePreview()
    {
        InterpreterContentPreviewEventArgs? latest;
        lock (_advancedRealtimePreviewSyncRoot)
        {
            latest = _latestAdvancedRealtimePreview;
            _latestAdvancedRealtimePreview = null;
        }

        if (latest is not null)
        {
            ApplyContentPreview(latest);
        }
    }

    private void ClearAdvancedRealtimePreviewQueue()
    {
        lock (_advancedRealtimePreviewSyncRoot)
        {
            _latestAdvancedRealtimePreview = null;
        }
    }

    private void ApplyContentPreview(InterpreterContentPreviewEventArgs args)
    {
        var title = args.IsTranslation
            ? T("Nội dung dịch", "Translated text", "번역 내용")
            : T("Nội dung nói", "Spoken content", "말한 내용");
        UpdateCurrentPreview(title, args.Content, args.IsTranslation, args.Language, args.IsInterim);
    }

    private void UpdateCurrentPreview(string title, string content, bool isTranslation, SupportedLanguage language, bool isInterim)
    {
        if (_rtbCurrentSource is null || _rtbCurrentTarget is null)
        {
            txtCurrentContent.Text = content;
            return;
        }

        var normalized = content.Trim();
        if (isTranslation)
        {
            _liveTargetPreview = PreferMoreCompletePreview(_liveTargetPreview, normalized);
        }
        else
        {
            if (_liveSourceLanguage != SupportedLanguage.Unknown && language != SupportedLanguage.Unknown && _liveSourceLanguage != language)
            {
                _liveSourcePreview = string.Empty;
            }

            if (DateTime.UtcNow - _lastCommittedSourceAtUtc <= TimeSpan.FromSeconds(8)
                && (_lastCommittedSourceLanguage == SupportedLanguage.Unknown
                    || language == SupportedLanguage.Unknown
                    || _lastCommittedSourceLanguage == language)
                && AreTranscriptVersionsRelated(_lastCommittedSourceText, normalized))
            {
                if (_settings.EngineType is not (InterpreterEngineType.GoogleCloudAdvancedHybridPipeline
                        or InterpreterEngineType.GoogleCloudAdaptiveHybridPipeline
                        or InterpreterEngineType.GoogleCloudPhysicalMuteHybridPipeline)
                    && CountWords(normalized) > CountWords(_lastCommittedSourceText))
                {
                    ReplaceLastTranscriptEntry(_sourceTranscriptHistory, _lastCommittedSourceText, normalized);
                    _lastCommittedSourceText = normalized;
                    RenderTranscriptPanels();
                }
                return;
            }

            _liveSourceLanguage = language;
            _liveSourcePreview = PreferMoreCompletePreview(_liveSourcePreview, normalized);
        }

        RenderTranscriptPanels();
        txtCurrentContent.Text = normalized;
    }

    private string CommitTranslationToTranscript(TranslationResult result)
    {
        var displayOriginal = result.OriginalText.Trim();
        var sourcePreviewBelongsToResult = AreTranscriptVersionsRelated(displayOriginal, _liveSourcePreview);

        AppendTranscript(_sourceTranscriptHistory, displayOriginal);
        AppendTranscript(_targetTranscriptHistory, result.TranslatedText);
        _lastCommittedSourceText = displayOriginal;
        _lastCommittedSourceAtUtc = DateTime.UtcNow;
        _lastCommittedSourceLanguage = result.SourceLanguage;
        if (sourcePreviewBelongsToResult)
        {
            _liveSourcePreview = string.Empty;
            _liveSourceLanguage = SupportedLanguage.Unknown;
        }

        if (IsSameSentenceProgress(result.TranslatedText, _liveTargetPreview))
        {
            _liveTargetPreview = string.Empty;
        }
        RenderTranscriptPanels();
        return displayOriginal;
    }

    private static string PreferMoreCompletePreview(string current, string incoming)
    {
        if (string.IsNullOrWhiteSpace(current) || string.IsNullOrWhiteSpace(incoming))
        {
            return incoming;
        }

        return IsSameSentenceProgress(current, incoming)
            && CountWords(current) > CountWords(incoming)
                ? current
                : incoming;
    }

    private static bool IsSameSentenceProgress(string first, string second)
    {
        var firstWords = NormalizeTranscriptWords(first);
        var secondWords = NormalizeTranscriptWords(second);
        if (firstWords.Length == 0 || secondWords.Length == 0)
        {
            return false;
        }

        var shorterLength = Math.Min(firstWords.Length, secondWords.Length);
        var longerLength = Math.Max(firstWords.Length, secondWords.Length);
        if (longerLength - shorterLength > 8)
        {
            return false;
        }

        var anchorLength = Math.Min(2, shorterLength);
        for (var index = 0; index < anchorLength; index++)
        {
            if (!string.Equals(firstWords[index], secondWords[index], StringComparison.Ordinal))
            {
                return false;
            }
        }

        if (shorterLength <= 3)
        {
            return firstWords.Take(shorterLength).SequenceEqual(secondWords.Take(shorterLength));
        }

        var commonWordCount = GetLongestCommonSubsequenceLength(firstWords, secondWords);
        return commonWordCount >= Math.Max(3, (int)Math.Ceiling(shorterLength * 0.7));
    }

    private static bool AreTranscriptVersionsRelated(string first, string second)
    {
        var firstWords = NormalizeTranscriptWords(first);
        var secondWords = NormalizeTranscriptWords(second);
        if (firstWords.Length == 0 || secondWords.Length == 0)
        {
            return false;
        }

        var shorter = firstWords.Length <= secondWords.Length ? firstWords : secondWords;
        var longer = firstWords.Length <= secondWords.Length ? secondWords : firstWords;
        if (shorter.Length <= 3)
        {
            return shorter.SequenceEqual(longer.Take(shorter.Length));
        }

        var commonWordCount = GetLongestCommonSubsequenceLength(firstWords, secondWords);
        var sameOpening = firstWords.Take(2).SequenceEqual(secondWords.Take(2));
        var shorterIsContained = commonWordCount == shorter.Length;
        return (sameOpening || shorterIsContained)
            && commonWordCount >= Math.Max(3, (int)Math.Ceiling(shorter.Length * 0.7));
    }

    private static int GetLongestCommonSubsequenceLength(string[] first, string[] second)
    {
        var previous = new int[second.Length + 1];
        var current = new int[second.Length + 1];
        for (var firstIndex = 1; firstIndex <= first.Length; firstIndex++)
        {
            for (var secondIndex = 1; secondIndex <= second.Length; secondIndex++)
            {
                current[secondIndex] = string.Equals(first[firstIndex - 1], second[secondIndex - 1], StringComparison.Ordinal)
                    ? previous[secondIndex - 1] + 1
                    : Math.Max(previous[secondIndex], current[secondIndex - 1]);
            }

            (previous, current) = (current, previous);
            Array.Clear(current);
        }
        return previous[second.Length];
    }

    private static string[] NormalizeTranscriptWords(string text)
    {
        var normalized = new StringBuilder(text.Length);
        foreach (var character in text.Normalize(NormalizationForm.FormC).ToLowerInvariant())
        {
            normalized.Append(char.IsLetterOrDigit(character) ? character : ' ');
        }

        return normalized
            .ToString()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static int CountWords(string text) => NormalizeTranscriptWords(text).Length;

    private static void ReplaceLastTranscriptEntry(StringBuilder history, string previousText, string replacementText)
    {
        if (string.IsNullOrWhiteSpace(previousText)
            || history.Length < previousText.Length
            || !history.ToString().EndsWith(previousText, StringComparison.Ordinal))
        {
            return;
        }

        history.Remove(history.Length - previousText.Length, previousText.Length);
        history.Append(replacementText.Trim());
    }

    private static void AppendTranscript(StringBuilder history, string text)
    {
        var normalized = text.Trim();
        if (normalized.Length == 0)
        {
            return;
        }
        if (history.Length > 0)
        {
            history.AppendLine();
            history.AppendLine();
        }
        history.Append(normalized);
        if (history.Length > MaximumTranscriptCharacters)
        {
            var removeLength = history.Length - MaximumTranscriptCharacters;
            var nextBreak = history.ToString().IndexOf('\n', removeLength);
            history.Remove(0, nextBreak >= 0 ? nextBreak + 1 : removeLength);
        }
    }

    private void RenderTranscriptPanels()
    {
        SetTranscriptText(_rtbCurrentSource, ComposeTranscript(_sourceTranscriptHistory, _liveSourcePreview));
        SetTranscriptText(_rtbCurrentTarget, ComposeTranscript(_targetTranscriptHistory, _liveTargetPreview));
    }

    private static string ComposeTranscript(StringBuilder history, string liveText)
    {
        if (history.Length == 0)
        {
            return liveText;
        }
        return string.IsNullOrWhiteSpace(liveText) ? history.ToString() : history + Environment.NewLine + Environment.NewLine + liveText;
    }

    private static void SetTranscriptText(TranscriptView box, string text) => box.SetContent(text);

    private void ShowTranslationHistoryRow(
        string sourceTitle,
        string originalText,
        string targetTitle,
        string translatedText,
        string elapsed)
    {
        SetTranscriptText(_rtbCurrentSource, originalText);
        SetTranscriptText(_rtbCurrentTarget, translatedText);
        txtCurrentContent.Text = originalText;
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
        var cleanState = GetStateDisplayName(state).Replace("● ", string.Empty).Replace("✓ ", string.Empty);
        lblState.Text = (color == UiTheme.Success ? "✓ " : "● ") + cleanState;
        _lblHeaderStatus.ForeColor = color;
        _lblHeaderStatus.Text = lblState.Text;
    }

    private void SetDashboardStatus(string stateText, string detailText, Color color)
    {
        if (_lblHeaderStatus is null)
        {
            return;
        }
        var cleanState = stateText.Replace("● ", string.Empty).Replace("✓ ", string.Empty);
        lblState.Text = (color == UiTheme.Success ? "✓ " : "● ") + cleanState;
        lblState.ForeColor = color;
        lblStatus.Text = detailText;
        _lblHeaderStatus.Text = "✓ " + cleanState;
        _lblHeaderStatus.ForeColor = color;
    }

    private static string FormatSeconds(double milliseconds) => $"{milliseconds / 1000.0:0.00} giây";

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
