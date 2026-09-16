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
        MinimumSize = new Size(1080, 720);
        AutoScaleMode = AutoScaleMode.Font;
        _toolTip = new ToolTip();
        _mainContent = new Panel { BackColor = UiTheme.Background };
        Controls.Add(_mainContent);

        _mainLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            BackColor = UiTheme.Background
        };
        _mainLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 72));
        _mainLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 92));
        _mainLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 64));
        _mainLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 36));
        _mainContent.Controls.Add(_mainLayout);

        BuildHeader();
        BuildSessionBar();
        BuildCurrentTranslationSurface();
        BuildHistorySurface();
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
            Padding = new Padding(2, 2, 0, 8)
        };
        _mainLayout.Controls.Add(header, 0, 0);

        _lblHeaderTitle = new Label
        {
            AutoSize = true,
            Text = "Phiên dịch cuộc họp Việt - Hàn",
            Font = UiTheme.AppTitleFont,
            ForeColor = UiTheme.TextPrimary,
            Location = new Point(2, 2)
        };
        header.Controls.Add(_lblHeaderTitle);

        _lblHeaderSubtitle = new Label
        {
            AutoSize = true,
            Text = "Phiên dịch hai chiều theo thời gian thực",
            Font = UiTheme.SubtitleFont,
            ForeColor = UiTheme.TextSecondary,
            Location = new Point(4, 40)
        };
        header.Controls.Add(_lblHeaderSubtitle);

        btnSettings = new Button
        {
            Text = "Cài đặt",
            Size = new Size(108, 36),
            Anchor = AnchorStyles.Top | AnchorStyles.Right
        };
        btnSettings.Click += btnSettings_Click;
        UiTheme.StyleSecondaryButton(btnSettings);
        header.Controls.Add(btnSettings);
        _toolTip.SetToolTip(btnSettings, "Mở cài đặt mô hình, kết nối và thiết bị âm thanh");
        header.Resize += (_, _) => btnSettings.Location = new Point(Math.Max(0, header.ClientSize.Width - btnSettings.Width), 14);
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
        _mainLayout.Controls.Add(bar, 0, 1);

        lblState.Font = UiTheme.StatusFont;
        lblState.ForeColor = UiTheme.Warning;
        lblState.Text = "Cần cấu hình";
        lblState.Location = new Point(20, 15);
        lblState.Size = new Size(310, 30);
        lblState.TextAlign = ContentAlignment.MiddleLeft;
        bar.Controls.Add(lblState);

        lblStatus.Font = UiTheme.BodyFont;
        lblStatus.ForeColor = UiTheme.TextSecondary;
        lblStatus.Location = new Point(22, 47);
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
            var x = Math.Max(740, bar.ClientSize.Width - btnStart.Width - 20);
            btnStart.Location = new Point(x, 15);
            btnStop.Location = btnStart.Location;
            lblStatus.Width = Math.Max(320, x - 42);
        };
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
        _mainLayout.Controls.Add(surface, 0, 2);
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = UiTheme.CardBackground,
            ColumnCount = 1,
            RowCount = 3
        };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        surface.Controls.Add(layout);

        lblCurrentContentTitle.Font = UiTheme.CardTitleFont;
        lblCurrentContentTitle.ForeColor = UiTheme.TextPrimary;
        lblCurrentContentTitle.Text = "NỘI DUNG PHIÊN DỊCH";
        lblCurrentContentTitle.Dock = DockStyle.Fill;
        lblCurrentContentTitle.TextAlign = ContentAlignment.MiddleLeft;
        layout.Controls.Add(lblCurrentContentTitle, 0, 0);

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
        layout.Controls.Add(transcriptLayout, 0, 1);
        transcriptLayout.Controls.Add(BuildTranscriptColumn(isSource: true), 0, 0);
        transcriptLayout.Controls.Add(BuildTranscriptColumn(isSource: false), 1, 0);

        var footer = new Panel { Dock = DockStyle.Fill, BackColor = UiTheme.CardBackground };
        _lblCurrentRoute = new Label
        {
            Dock = DockStyle.Fill,
            Text = "Transcript trực tiếp sẽ xuất hiện tại đây khi bắt đầu.",
            Font = UiTheme.BodyFont,
            ForeColor = UiTheme.TextSecondary,
            TextAlign = ContentAlignment.MiddleLeft
        };
        _lblCurrentElapsed = new Label
        {
            Dock = DockStyle.Right,
            Width = 130,
            Text = string.Empty,
            Font = UiTheme.StrongBodyFont,
            ForeColor = UiTheme.TextSecondary,
            TextAlign = ContentAlignment.MiddleRight
        };
        footer.Controls.Add(_lblCurrentRoute);
        footer.Controls.Add(_lblCurrentElapsed);
        layout.Controls.Add(footer, 0, 2);
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
        _mainLayout.Controls.Add(surface, 0, 3);
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
        if (_lblHeaderTitle is null)
        {
            return;
        }

        Text = T("Phiên dịch cuộc họp Việt - Hàn", "Vietnamese - Korean Meeting Interpreter", "베트남어 - 한국어 회의 통역");
        _lblHeaderTitle.Text = Text;
        _lblHeaderSubtitle.Text = T("Phiên dịch hai chiều theo thời gian thực", "Real-time two-way interpretation", "실시간 양방향 통역");
        btnSettings.Text = T("Cài đặt", "Settings", "설정");
        btnStart.Text = T("BẮT ĐẦU", "START", "시작");
        btnStop.Text = T("DỪNG", "STOP", "중지");
        lblCurrentContentTitle.Text = T("NỘI DUNG PHIÊN DỊCH", "LIVE INTERPRETATION", "실시간 통역");
        _lblCurrentSourceTitle.Text = T("NỘI DUNG NÓI", "SPOKEN CONTENT", "말한 내용");
        _lblCurrentTargetTitle.Text = T("NỘI DUNG DỊCH", "TRANSLATED CONTENT", "번역 내용");
        _lblTranslationHistory!.Text = T("LỊCH SỬ PHIÊN DỊCH", "TRANSLATION HISTORY", "통역 기록");
        btnExportHistory.Text = T("Xuất Excel", "Export Excel", "Excel 내보내기");
        _toolTip.SetToolTip(btnSettings, T("Mở cài đặt mô hình, kết nối và thiết bị âm thanh", "Open engine, connection, and audio settings", "엔진, 연결 및 오디오 설정 열기"));
        if (_sourceTranscriptHistory.Length == 0 && string.IsNullOrWhiteSpace(_liveSourcePreview))
        {
            _lblCurrentRoute.Text = T("Transcript trực tiếp sẽ xuất hiện tại đây khi bắt đầu.", "Live transcript will appear here after you start.", "시작하면 실시간 자막이 여기에 표시됩니다.");
        }
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
            if (language != SupportedLanguage.Unknown)
            {
                _lblCurrentSourceTitle.Text = GetLanguageDisplayName(language).ToUpperInvariant();
            }
        }

        RenderTranscriptPanels();
        _lblCurrentRoute.Text = isInterim
            ? T("Đang nghe và cập nhật transcript...", "Listening and updating transcript...", "듣고 자막을 업데이트하는 중...")
            : T("Đang xử lý nội dung phiên dịch...", "Processing interpretation...", "통역 내용을 처리하는 중...");
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
        _lblCurrentSourceTitle.Text = GetLanguageDisplayName(result.SourceLanguage).ToUpperInvariant();
        _lblCurrentTargetTitle.Text = GetLanguageDisplayName(result.TargetLanguage).ToUpperInvariant();
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
        _lblCurrentSourceTitle.Text = sourceTitle.ToUpperInvariant();
        _lblCurrentTargetTitle.Text = targetTitle.ToUpperInvariant();
        SetTranscriptText(_rtbCurrentSource, originalText);
        SetTranscriptText(_rtbCurrentTarget, translatedText);
        _lblCurrentRoute.Text = T("Đang xem nội dung từ lịch sử phiên dịch.", "Viewing translation history.", "통역 기록의 내용을 보고 있습니다.");
        _lblCurrentElapsed.Text = elapsed;
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
