using System.IO.Compression;
using System.Text;
using MeetingInterpreter.Helpers;
using MeetingInterpreter.Controls;
using MeetingInterpreter.Models;
using MeetingInterpreter.Services;

namespace MeetingInterpreter;

public partial class FormMain : Form
{
    private readonly InterpreterSettings _settings = new();
    private readonly AppLogger _logger = new();
    private readonly AudioService _audioService;
    private readonly GoogleTranslatePipeline _googlePipeline;
    private readonly GeminiApiClient _geminiClient;
    private readonly GeminiLiveSession _geminiLiveSession;
    private readonly GoogleStreamingSttService _googleStreamingSttService;
    private readonly DeepgramSttService _deepgramSttService;
    private readonly InterpreterEngineFactory _engineFactory;
    private readonly VoiceActivityDetector _vad;
    private InterpreterService? _service;
    private SavedAppSettings _savedSettings = new();
    private DisplayLanguage _displayLanguage = DisplayLanguage.Vietnamese;
    private Label? _lblCredentialFile;
    private Label? _lblAudioDevicesSection;
    private Label? _lblInputMicrophone;
    private Label? _lblOutput1;
    private Label? _lblOutput2;
    private Label? _lblSessionControlsSection;
    private Label? _lblMicLevelSection;
    private Label? _lblVadSection;
    private Label? _lblSilenceDuration;
    private Label? _lblTranslationHistory;
    private Label? _lblGoogleSection;
    private Label? _lblConnectionStatus;
    private Label? _lblDisplayLanguage;
    private ComboBox cmbInterpreterEngine = null!;
    private Label lblEngineTitle = null!;
    private Label lblEngineDescription = null!;
    private Label lblGeminiSection = null!;
    private Label lblGeminiApiKey = null!;
    private TextBox txtGeminiApiKey = null!;
    private Button btnTestGemini = null!;
    private Label lblGeminiStatus = null!;
    private Button btnSettings = null!;
    private Button btnExportHistory = null!;

    public FormMain()
    {
        InitializeComponent();
        LoadSavedConfiguration();
        var vocabulary = SpeechVocabularySettings.Load(ResolveVocabularyPath(), _logger);
        _googlePipeline = new GoogleTranslatePipeline(_logger, _settings.SpeechRecognition, vocabulary);
        _geminiClient = new GeminiApiClient(_settings.Gemini, _logger);
        _geminiLiveSession = new GeminiLiveSession(_settings.Gemini, _logger);
        _googleStreamingSttService = new GoogleStreamingSttService(_googlePipeline, _logger);
        _deepgramSttService = new DeepgramSttService(_settings.Deepgram, _logger);
        var geminiEngine = new GeminiInterpreterEngine(_geminiClient);
        var googleCloudEngine = new GoogleCloudInterpreterEngine(_googlePipeline, _settings, _logger);
        var googleCloudSynthesis = new GoogleCloudSpeechSynthesisService(_googlePipeline);
        _engineFactory = new InterpreterEngineFactory(geminiEngine, googleCloudEngine, _geminiClient, googleCloudSynthesis);
        _audioService = new AudioService(_logger);
        _vad = new VoiceActivityDetector(_settings);
        _service = new InterpreterService(_audioService, _googlePipeline, _geminiClient, _geminiLiveSession, _googleStreamingSttService, _deepgramSttService, _engineFactory, _vad, _logger, _settings);
        BuildModernLayout();
        ConfigureAdvancedRealtimePreviewRendering();
        ApplySavedConfigurationToUi();
        ConfigureDisplayLanguageSelector();
        ConfigureInterpreterEngineSelector();
        ConfigureSpeechModelSelector();
        WireServiceEvents();
        ConfigureGrid();
        ApplyUiLanguage();
        ToggleSessionButtons(isRunning: false);
        WindowState = FormWindowState.Maximized;
        Resize += (_, _) => ApplyResponsiveLayout();
        Shown += (_, _) => ApplyResponsiveLayout();
        FormClosing += (_, _) => SaveCurrentConfiguration();
    }

    private void LoadSavedConfiguration()
    {
        _savedSettings = UserSettingsStore.Load(_logger);

        _settings.Gemini.ApiKey = _savedSettings.GeminiApiKey.Trim();

        if (Enum.TryParse<DisplayLanguage>(_savedSettings.DisplayLanguage, out var displayLanguage))
        {
            _displayLanguage = displayLanguage;
        }

        if (Enum.TryParse<InterpreterEngineType>(_savedSettings.EngineType, out var engineType))
        {
            _settings.EngineType = engineType;
        }

        if (Enum.TryParse<SttEngineType>(_savedSettings.SttEngineType, out var sttEngineType))
        {
            _settings.SttEngineType = sttEngineType;
        }

        if (!string.IsNullOrWhiteSpace(_savedSettings.SpeechRecognitionModel))
        {
            _settings.SpeechRecognition.Model = _savedSettings.SpeechRecognitionModel;
        }

        if (!_settings.Gemini.Model.Contains("live", StringComparison.OrdinalIgnoreCase))
        {
            _settings.Gemini.Model = "gemini-3.1-flash-live-preview";
        }

        if (_savedSettings.VadThreshold > 0)
        {
            _settings.VadThreshold = _savedSettings.VadThreshold;
        }

        if (_savedSettings.SilenceDurationMs > 0)
        {
            _settings.SilenceDurationMs = _savedSettings.SilenceDurationMs;
        }

        _settings.SuppressMicDuringHeadsetPlayback = _savedSettings.SuppressMicDuringHeadsetPlayback;
        _settings.RecognitionOnlyMode = _savedSettings.RecognitionOnlyMode;
        _settings.SpeechRecognition.SaveRecognitionAudioForDebug = _savedSettings.SaveRecognitionAudioForDebug;
        _settings.Deepgram.ApiKey = _savedSettings.DeepgramApiKey.Trim();
        _settings.Deepgram.Language = "multi";
    }

    private void ApplySavedConfigurationToUi()
    {
        txtGoogleCredentialPath.Text = _savedSettings.GoogleCredentialPath;
        txtGeminiApiKey.Text = _settings.Gemini.ApiKey;
    }

    private void SaveCurrentConfiguration()
    {
        var settings = new SavedAppSettings
        {
            GoogleCredentialPath = txtGoogleCredentialPath.Text.Trim(),
            GeminiApiKey = txtGeminiApiKey.Text.Trim(),
            DisplayLanguage = _displayLanguage.ToString(),
            EngineType = GetSelectedEngineType().ToString(),
            SttEngineType = _settings.SttEngineType.ToString(),
            SpeechRecognitionModel = _settings.SpeechRecognition.Model,
            DeepgramApiKey = _settings.Deepgram.ApiKey.Trim(),
            DeepgramLanguage = _settings.Deepgram.Language,
            InputDeviceId = (cmbInputDevice.SelectedItem as AudioDeviceInfo)?.Id ?? string.Empty,
            InputDeviceName = (cmbInputDevice.SelectedItem as AudioDeviceInfo)?.Name ?? string.Empty,
            Output1DeviceId = (cmbOutput1Device.SelectedItem as AudioDeviceInfo)?.Id ?? string.Empty,
            Output1DeviceName = (cmbOutput1Device.SelectedItem as AudioDeviceInfo)?.Name ?? string.Empty,
            Output2DeviceId = (cmbOutput2Device.SelectedItem as AudioDeviceInfo)?.Id ?? string.Empty,
            Output2DeviceName = (cmbOutput2Device.SelectedItem as AudioDeviceInfo)?.Name ?? string.Empty,
            VadThreshold = _settings.VadThreshold,
            SilenceDurationMs = _settings.SilenceDurationMs,
            SuppressMicDuringHeadsetPlayback = _settings.SuppressMicDuringHeadsetPlayback,
            RecognitionOnlyMode = _settings.RecognitionOnlyMode,
            SaveRecognitionAudioForDebug = _settings.SpeechRecognition.SaveRecognitionAudioForDebug
        };

        UserSettingsStore.Save(settings, _logger);
        _savedSettings = settings;
    }

    private void FormMain_Load(object? sender, EventArgs e)
    {
        RefreshDeviceLists();
        UpdateSettingsLabels();
        UpdateConfigurationReadinessStatus();
    }

    private async void btnBrowseCredential_Click(object? sender, EventArgs e)
    {
        using var dialog = new OpenFileDialog
        {
            Title = "Chọn tệp Service Account JSON của Google Cloud",
            Filter = "Tệp JSON (*.json)|*.json",
            CheckFileExists = true,
            Multiselect = false
        };

        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        txtGoogleCredentialPath.Text = dialog.FileName;
        SaveCurrentConfiguration();
        lblGoogleStatus.Text = "Đang kiểm tra...";
        lblGoogleStatus.ForeColor = Color.DarkGoldenrod;
        ToggleUiForBusy(true);

        try
        {
            await _service!.InitializeGoogleAsync(dialog.FileName);
            UpdateConfigurationReadinessStatus();
            lblGoogleStatus.Text = "✓ Đã kết nối";
            lblGoogleStatus.ForeColor = Color.ForestGreen;
        }
        catch (Exception ex)
        {
            lblGoogleStatus.Text = "Xác thực thất bại";
            lblGoogleStatus.ForeColor = Color.Firebrick;
            lblStatus.Text = "Xác thực Google Cloud thất bại. Vui lòng kiểm tra tệp xác thực và các API đã bật.";
            _logger.Error("Xác thực Google Cloud thất bại.", ex);
        }
        finally
        {
            ToggleUiForBusy(false);
        }
    }

    private async void btnTestGemini_Click(object? sender, EventArgs e)
    {
        if (string.IsNullOrWhiteSpace(txtGeminiApiKey.Text))
        {
            MessageBox.Show(this, "Vui lòng nhập API Key Gemini trước.", "Cần cấu hình", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        lblGeminiStatus.Text = "Đang kiểm tra...";
        lblGeminiStatus.ForeColor = Color.DarkGoldenrod;
        ToggleUiForBusy(true);

        try
        {
            await _service!.InitializeGeminiAsync(txtGeminiApiKey.Text);
            _settings.Gemini.ApiKey = txtGeminiApiKey.Text.Trim();
            SaveCurrentConfiguration();
            UpdateConfigurationReadinessStatus();
            lblGeminiStatus.Text = "✓ Đã kết nối Gemini Live 3.1 Flash";
            lblGeminiStatus.ForeColor = Color.ForestGreen;
        }
        catch (Exception ex)
        {
            lblGeminiStatus.Text = "Không thể kết nối Gemini Live 3.1 Flash";
            lblGeminiStatus.ForeColor = Color.Firebrick;
            lblStatus.Text = ToUserMessage(ex);
            _logger.Error("Kiểm tra Gemini thất bại.", ex);
        }
        finally
        {
            ToggleUiForBusy(false);
        }
    }

    private async void btnRefreshDevices_Click(object? sender, EventArgs e)
    {
        try
        {
            await _service!.RefreshDevicesAsync();
            RefreshDeviceLists();
            lblStatus.Text = "Đã làm mới danh sách thiết bị âm thanh.";
        }
        catch (Exception ex)
        {
            lblStatus.Text = ToUserMessage(ex);
            _logger.Error("Làm mới thiết bị âm thanh thất bại.", ex);
        }
    }

    private async void btnStart_Click(object? sender, EventArgs e)
    {
        if (!TryGetSelectedDevices(out var input, out var output1, out var output2))
        {
            return;
        }

        var engineType = GetSelectedEngineType();

        if (engineType is InterpreterEngineType.GoogleCloudPipeline
            or InterpreterEngineType.GoogleCloudHybridPipeline
            or InterpreterEngineType.GoogleCloudStreamingPipeline
            or InterpreterEngineType.GoogleCloudAdvancedHybridPipeline
            or InterpreterEngineType.GoogleCloudAdaptiveHybridPipeline
            or InterpreterEngineType.GoogleCloudPhysicalMuteHybridPipeline
            && (string.IsNullOrWhiteSpace(txtGoogleCredentialPath.Text) || !File.Exists(txtGoogleCredentialPath.Text)))
        {
            MessageBox.Show(this, "Google Cloud chưa được cấu hình.\n\nVui lòng chọn tệp Service Account trước khi bắt đầu.", "Cần cấu hình", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        if (engineType == InterpreterEngineType.Gemini25Pro && string.IsNullOrWhiteSpace(txtGeminiApiKey.Text))
        {
            MessageBox.Show(this, "Vui lòng nhập API Key Gemini trước khi bắt đầu.", "Cần cấu hình", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        if (engineType == InterpreterEngineType.GoogleCloudPipeline
            && _settings.SttEngineType == SttEngineType.DeepgramNova2
            && string.IsNullOrWhiteSpace(_settings.Deepgram.ApiKey))
        {
            MessageBox.Show(this, T("Vui lòng nhập API Key Deepgram trước khi bắt đầu.", "Enter the Deepgram API key before starting.", "시작하기 전에 Deepgram API 키를 입력하세요."), T("Cần cấu hình", "Configuration needed", "설정 필요"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        ToggleSessionButtons(isRunning: true);

        try
        {
            await _service!.StartSessionAsync(
                txtGoogleCredentialPath.Text,
                txtGeminiApiKey.Text,
                engineType,
                input,
                output1,
                output2);
        }
        catch (Exception ex)
        {
            ToggleSessionButtons(isRunning: false);
            var message = ToUserMessage(ex);
            lblStatus.Text = message;
            lblState.Text = GetStateDisplayName(InterpreterState.Error);
            _logger.Error("Không thể bắt đầu phiên dịch.", ex);
            MessageBox.Show(this, message, "Không thể bắt đầu", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private async void btnStop_Click(object? sender, EventArgs e)
    {
        ToggleUiForBusy(true);

        try
        {
            await _service!.StopSessionAsync();
        }
        finally
        {
            ToggleUiForBusy(false);
            ToggleSessionButtons(isRunning: false);
        }
    }

    private async void btnTestOutput1_Click(object? sender, EventArgs e)
    {
        if (cmbOutput1Device.SelectedItem is not AudioDeviceInfo output)
        {
            lblStatus.Text = "Vui lòng chọn loa phòng họp trước.";
            return;
        }

        await RunOutputTestAsync(() => _service!.TestOutput1Async(output, CancellationToken.None));
    }

    private async void btnTestOutput2_Click(object? sender, EventArgs e)
    {
        if (cmbOutput2Device.SelectedItem is not AudioDeviceInfo output)
        {
            lblStatus.Text = "Vui lòng chọn tai nghe quản lý Hàn Quốc trước.";
            return;
        }

        await RunOutputTestAsync(() => _service!.TestOutput2Async(output, CancellationToken.None));
    }

    private void btnExportHistory_Click(object? sender, EventArgs e)
    {
        if (dgvTranslations.Rows.Cast<DataGridViewRow>().All(row => row.IsNewRow))
        {
            MessageBox.Show(
                this,
                T("Chưa có lịch sử phiên dịch để xuất.", "There is no translation history to export.", "내보낼 통역 기록이 없습니다."),
                T("Xuất Excel", "Export Excel", "Excel 내보내기"),
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        using var dialog = new SaveFileDialog
        {
            Title = T("Lưu lịch sử phiên dịch", "Save translation history", "통역 기록 저장"),
            Filter = "Excel Workbook (*.xlsx)|*.xlsx",
            FileName = $"lich-su-phien-dich-{DateTime.Now:yyyyMMdd-HHmmss}.xlsx",
            AddExtension = true,
            DefaultExt = "xlsx",
            OverwritePrompt = true
        };

        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        try
        {
            ExportTranslationHistoryToExcel(dialog.FileName);
            lblStatus.Text = T("Đã xuất lịch sử phiên dịch ra Excel.", "Translation history exported to Excel.", "통역 기록을 Excel로 내보냈습니다.");
        }
        catch (Exception ex)
        {
            lblStatus.Text = T("Không thể xuất Excel.", "Unable to export Excel.", "Excel 내보내기에 실패했습니다.");
            _logger.Error("Khong the xuat lich su phien dich ra Excel.", ex);
            MessageBox.Show(this, ex.Message, lblStatus.Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void trkVadThreshold_ValueChanged(object? sender, EventArgs e)
    {
        _settings.VadThreshold = trkVadThreshold.Value / 1000.0;
        UpdateSettingsLabels();
    }

    private void nudSilenceDuration_ValueChanged(object? sender, EventArgs e)
    {
        _settings.SilenceDurationMs = (int)nudSilenceDuration.Value;
    }

    private void chkSuppressHeadset_CheckedChanged(object? sender, EventArgs e)
    {
        _settings.SuppressMicDuringHeadsetPlayback = chkSuppressHeadset.Checked;
    }

    private void chkRecognitionOnly_CheckedChanged(object? sender, EventArgs e)
    {
        _settings.RecognitionOnlyMode = chkRecognitionOnly.Checked;
    }

    private void chkSaveDebugAudio_CheckedChanged(object? sender, EventArgs e)
    {
        _settings.SpeechRecognition.SaveRecognitionAudioForDebug = chkSaveDebugAudio.Checked;
    }

    private void cmbSpeechModel_SelectedIndexChanged(object? sender, EventArgs e)
    {
        if (cmbSpeechModel.SelectedItem is string model)
        {
            _settings.SpeechRecognition.Model = model;
        }
    }

    private void cmbDisplayLanguage_SelectedIndexChanged(object? sender, EventArgs e)
    {
        if (cmbDisplayLanguage.SelectedItem is DisplayLanguageSelectionItem item)
        {
            _displayLanguage = item.Language;
            ApplyUiLanguage();
            SaveCurrentConfiguration();
            UpdateConfigurationReadinessStatus();
        }
    }

    private void cmbInterpreterEngine_SelectedIndexChanged(object? sender, EventArgs e)
    {
        _settings.EngineType = GetSelectedEngineType();
        if (cmbInterpreterEngine.SelectedItem is InterpreterEngineSelectionItem { SttEngineType: not null } item)
        {
            _settings.SttEngineType = item.SttEngineType.Value;
        }

        UpdateEngineUiState();
        SaveCurrentConfiguration();
        UpdateConfigurationReadinessStatus();
    }

    private void btnSettings_Click(object? sender, EventArgs e)
    {
        using var settingsForm = new FormSettings(
            txtGoogleCredentialPath.Text,
            txtGeminiApiKey.Text,
            _settings,
            _displayLanguage,
            () => _service!.EnumerateInputDevices(),
            () => _service!.EnumerateOutputDevices(),
            (cmbInputDevice.SelectedItem as AudioDeviceInfo)?.Id ?? _savedSettings.InputDeviceId,
            (cmbOutput1Device.SelectedItem as AudioDeviceInfo)?.Id ?? _savedSettings.Output1DeviceId,
            (cmbOutput2Device.SelectedItem as AudioDeviceInfo)?.Id ?? _savedSettings.Output2DeviceId,
            path => _service!.InitializeGoogleAsync(path),
            apiKey => _service!.InitializeGeminiAsync(apiKey),
            apiKey => _service!.InitializeDeepgramAsync(apiKey),
            device => _service!.TestOutput1Async(device, CancellationToken.None),
            device => _service!.TestOutput2Async(device, CancellationToken.None));

        if (settingsForm.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        txtGoogleCredentialPath.Text = settingsForm.GoogleCredentialPath;
        txtGeminiApiKey.Text = settingsForm.GeminiApiKey;
        _settings.Gemini.ApiKey = settingsForm.GeminiApiKey.Trim();
        _displayLanguage = settingsForm.DisplayLanguage;
        RefreshDeviceLists(
            settingsForm.SelectedInputDevice?.Id,
            settingsForm.SelectedOutput1Device?.Id,
            settingsForm.SelectedOutput2Device?.Id,
            settingsForm.SelectedInputDevice?.Name,
            settingsForm.SelectedOutput1Device?.Name,
            settingsForm.SelectedOutput2Device?.Name);
        SelectDevice(cmbInputDevice, settingsForm.SelectedInputDevice);
        SelectDevice(cmbOutput1Device, settingsForm.SelectedOutput1Device);
        SelectDevice(cmbOutput2Device, settingsForm.SelectedOutput2Device);
        SyncAdvancedControlsFromSettings();
        SelectCurrentInterpreterEngineItem();
        SaveCurrentConfiguration();
        ApplyUiLanguage();
        UpdateConfigurationReadinessStatus("Đã cập nhật cài đặt.");
        BeginInvoke(new Action(() => UpdateConfigurationReadinessStatus("Đã cập nhật cài đặt.")));
        lblStatus.Text = T("Đã cập nhật cài đặt.", "Settings updated.", "설정이 업데이트되었습니다.");
    }

    private async Task RunOutputTestAsync(Func<Task> test)
    {
        ToggleUiForBusy(true);

        try
        {
            lblStatus.Text = T("Đang kiểm tra thiết bị âm thanh đã chọn...", "Testing the selected audio device...", "선택한 오디오 장치를 테스트 중입니다...");
            await test();
            lblStatus.Text = T("Đã kiểm tra xong thiết bị âm thanh.", "Audio device test completed.", "오디오 장치 테스트가 완료되었습니다.");
        }
        catch (Exception ex)
        {
            lblStatus.Text = ToUserMessage(ex);
            _logger.Error("Kiểm tra thiết bị âm thanh thất bại.", ex);
        }
        finally
        {
            ToggleUiForBusy(false);
        }
    }

    private void WireServiceEvents()
    {
        _service!.StateChanged += (_, args) =>
        {
            UiThreadHelper.Run(this, () =>
            {
                lblState.Text = "● " + GetStateDisplayName(args.State);
                UpdateStateVisual(args.State);
            });
        };

        _service.StatusChanged += (_, message) =>
        {
            UiThreadHelper.Run(this, () => lblStatus.Text = message);
        };

        _service.MicrophoneLevelChanged += (_, level) =>
        {
            UiThreadHelper.Run(this, () =>
            {
                prgMicLevel.Value = Math.Clamp(level, 0, 100);
                lblMicLevel.Text = $"Microphone: {level}%";
                UpdateMicrophoneQuality(level);
                UpdateDashboardMicrophoneLevel(level, sessionActive: true);
            });
        };

        _service.TranslationCompleted += (_, result) =>
        {
            UiThreadHelper.Run(this, () => AddTranslationRow(result));
        };

        _service.QueueStatusChanged += (_, args) =>
        {
            UiThreadHelper.Run(this, () =>
            {
                var processing = args.ProcessingSequence.HasValue
                    ? T($"Đang xử lý: Câu {args.ProcessingSequence.Value}", $"Processing: Item {args.ProcessingSequence.Value}", $"처리 중: {args.ProcessingSequence.Value}번")
                    : T("Đang xử lý: -", "Processing: -", "처리 중: -");
                var pending = T($"Đang chờ: {args.PendingCount} câu", $"Pending: {args.PendingCount}", $"대기: {args.PendingCount}");
                var playback = args.PendingPlaybackCount > 0
                    ? "   " + T($"Chờ phát: {args.PendingPlaybackCount}", $"Playback: {args.PendingPlaybackCount}", $"재생 대기: {args.PendingPlaybackCount}")
                    : string.Empty;
                _lblQueueStatus.Text = $"{processing}   {pending}{playback}";
            });
        };

        _service.ContentPreviewChanged += (_, args) =>
        {
            if ((_settings.EngineType is InterpreterEngineType.GoogleCloudAdvancedHybridPipeline
                or InterpreterEngineType.GoogleCloudAdaptiveHybridPipeline
                or InterpreterEngineType.GoogleCloudPhysicalMuteHybridPipeline)
                && args.IsInterim
                && !args.IsTranslation)
            {
                QueueAdvancedRealtimePreview(args);
                return;
            }

            UiThreadHelper.Run(this, () => ApplyContentPreview(args));
        };
    }

    private void ConfigureGrid()
    {
        dgvTranslations.Columns.Clear();
        dgvTranslations.Columns.Add("Engine", T("Mô hình", "Engine", "모델"));
        dgvTranslations.Columns.Add("Time", T("Thời gian", "Time", "시간"));
        dgvTranslations.Columns.Add("Source", T("Ngôn ngữ gốc", "Source language", "원본 언어"));
        dgvTranslations.Columns.Add("OriginalText", T("Nội dung nhận dạng", "Recognized text", "인식된 내용"));
        dgvTranslations.Columns.Add("Target", T("Ngôn ngữ đích", "Target language", "대상 언어"));
        dgvTranslations.Columns.Add("TranslatedText", T("Nội dung dịch", "Translated text", "번역 내용"));
        dgvTranslations.Columns.Add("SttMs", T("Nhận dạng (ms)", "Recognition (ms)", "인식(ms)"));
        dgvTranslations.Columns.Add("TranslateMs", T("Dịch (ms)", "Translation (ms)", "번역(ms)"));
        dgvTranslations.Columns.Add("TtsMs", T("Tạo giọng nói (ms)", "Speech synthesis (ms)", "음성 생성(ms)"));
        dgvTranslations.Columns.Add("TotalMs", T("Tổng thời gian (ms)", "Total time (ms)", "총 시간(ms)"));
        dgvTranslations.Columns.Add("Status", T("Trạng thái", "Status", "상태"));
        dgvTranslations.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        dgvTranslations.Columns["Engine"].FillWeight = 75;
        dgvTranslations.Columns["Time"].FillWeight = 55;
        dgvTranslations.Columns["Time"].DisplayIndex = 0;
        dgvTranslations.Columns["Engine"].DisplayIndex = 1;
        dgvTranslations.Columns["Source"].FillWeight = 45;
        dgvTranslations.Columns["Target"].FillWeight = 45;
        dgvTranslations.Columns["SttMs"].FillWeight = 55;
        dgvTranslations.Columns["TranslateMs"].FillWeight = 70;
        dgvTranslations.Columns["TtsMs"].FillWeight = 55;
        dgvTranslations.Columns["TotalMs"].FillWeight = 60;
        dgvTranslations.Columns["SttMs"].Visible = false;
        dgvTranslations.Columns["TranslateMs"].Visible = false;
        dgvTranslations.Columns["TtsMs"].Visible = false;
        dgvTranslations.Columns["TotalMs"].HeaderText = T("Tổng thời gian", "Total time", "총 시간");
        dgvTranslations.RowHeadersVisible = false;
        dgvTranslations.AllowUserToAddRows = false;
        dgvTranslations.AllowUserToDeleteRows = false;
        dgvTranslations.ReadOnly = true;
        dgvTranslations.MultiSelect = false;
        dgvTranslations.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        dgvTranslations.BackgroundColor = UiTheme.CardBackground;
        dgvTranslations.BorderStyle = BorderStyle.None;
        dgvTranslations.GridColor = UiTheme.Border;
        dgvTranslations.EnableHeadersVisualStyles = false;
        dgvTranslations.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(249, 250, 251);
        dgvTranslations.ColumnHeadersDefaultCellStyle.ForeColor = UiTheme.TextPrimary;
        dgvTranslations.ColumnHeadersDefaultCellStyle.Font = UiTheme.StrongBodyFont;
        dgvTranslations.ColumnHeadersHeight = 36;
        dgvTranslations.DefaultCellStyle.Font = UiTheme.BodyFont;
        dgvTranslations.DefaultCellStyle.ForeColor = UiTheme.TextPrimary;
        dgvTranslations.DefaultCellStyle.SelectionBackColor = Color.FromArgb(224, 236, 252);
        dgvTranslations.DefaultCellStyle.SelectionForeColor = UiTheme.TextPrimary;
        dgvTranslations.DefaultCellStyle.WrapMode = DataGridViewTriState.True;
        dgvTranslations.RowTemplate.Height = 38;
        dgvTranslations.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.DisplayedCells;
    }

    private void ConfigureDisplayLanguageSelector()
    {
        cmbDisplayLanguage.Items.Clear();
        cmbDisplayLanguage.Items.Add(new DisplayLanguageSelectionItem(DisplayLanguage.Vietnamese, "Tiếng Việt"));
        cmbDisplayLanguage.Items.Add(new DisplayLanguageSelectionItem(DisplayLanguage.English, "English"));
        cmbDisplayLanguage.Items.Add(new DisplayLanguageSelectionItem(DisplayLanguage.Korean, "한국어"));
        cmbDisplayLanguage.SelectedItem = cmbDisplayLanguage.Items
            .OfType<DisplayLanguageSelectionItem>()
            .FirstOrDefault(item => item.Language == _displayLanguage)
            ?? cmbDisplayLanguage.Items[0];
    }

    private void CreateEngineAndGeminiControls()
    {
        foreach (Control control in Controls)
        {
            control.Top += 118;
        }

        lblEngineTitle = new Label
        {
            AutoSize = true,
            Font = new Font("Segoe UI", 9F, FontStyle.Bold),
            Location = new Point(24, 14),
            Text = "Mô hình phiên dịch"
        };
        Controls.Add(lblEngineTitle);

        cmbInterpreterEngine = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            FormattingEnabled = true,
            Location = new Point(24, 36),
            Size = new Size(245, 23)
        };
        cmbInterpreterEngine.SelectedIndexChanged += cmbInterpreterEngine_SelectedIndexChanged;
        Controls.Add(cmbInterpreterEngine);

        lblEngineDescription = new Label
        {
            AutoEllipsis = true,
            Location = new Point(286, 38),
            Size = new Size(503, 34),
            Text = "Gemini Live stream audio hai chiều, không qua Google STT/TTS."
        };
        Controls.Add(lblEngineDescription);

        btnSettings = new Button
        {
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
            Location = new Point(684, 34),
            Size = new Size(105, 28),
            Text = "Cài đặt"
        };
        btnSettings.Click += btnSettings_Click;
        Controls.Add(btnSettings);

        lblGeminiSection = new Label
        {
            AutoSize = true,
            Font = new Font("Segoe UI", 9F, FontStyle.Bold),
            Location = new Point(24, 70),
            Text = "Google AI Studio / Gemini"
        };
        Controls.Add(lblGeminiSection);

        lblGeminiApiKey = new Label
        {
            AutoSize = true,
            Location = new Point(24, 94),
            Text = "API Key Gemini"
        };
        Controls.Add(lblGeminiApiKey);

        txtGeminiApiKey = new TextBox
        {
            Location = new Point(126, 91),
            Size = new Size(310, 23),
            UseSystemPasswordChar = true
        };
        Controls.Add(txtGeminiApiKey);

        btnTestGemini = new Button
        {
            Location = new Point(450, 90),
            Size = new Size(145, 25),
            Text = "Kiểm tra kết nối"
        };
        btnTestGemini.Click += btnTestGemini_Click;
        Controls.Add(btnTestGemini);

        lblGeminiStatus = new Label
        {
            AutoSize = true,
            Location = new Point(610, 95),
            Text = "Chưa cấu hình"
        };
        Controls.Add(lblGeminiStatus);

        lblGeminiSection.Visible = false;
        lblGeminiApiKey.Visible = false;
        txtGeminiApiKey.Visible = false;
        btnTestGemini.Visible = false;
        lblGeminiStatus.Visible = false;
        txtGoogleCredentialPath.Visible = false;
        btnBrowseCredential.Visible = false;
        lblGoogleStatus.Visible = false;

        ClientSize = new Size(ClientSize.Width, ClientSize.Height + 118);
        MinimumSize = new Size(MinimumSize.Width, MinimumSize.Height + 118);
    }

    private void ConfigureInterpreterEngineSelector()
    {
        var currentEngine = _settings.EngineType;
        var currentSttEngine = _settings.SttEngineType;
        cmbInterpreterEngine.Items.Clear();
        cmbInterpreterEngine.Items.Add(new InterpreterEngineSelectionItem(InterpreterEngineType.Gemini25Pro, null, "1. Gemini Live"));
        cmbInterpreterEngine.Items.Add(new InterpreterEngineSelectionItem(InterpreterEngineType.GoogleCloudPipeline, SttEngineType.GoogleSpeechToText, "2. Speech + Translate + TTS"));
        cmbInterpreterEngine.Items.Add(new InterpreterEngineSelectionItem(InterpreterEngineType.GoogleCloudHybridPipeline, SttEngineType.GoogleSpeechToText, "3. Hybrid Speech + Async Queue"));
        cmbInterpreterEngine.Items.Add(new InterpreterEngineSelectionItem(InterpreterEngineType.GoogleCloudStreamingPipeline, SttEngineType.GoogleSpeechToText, "4. Streaming Speech + Translate + TTS"));
        cmbInterpreterEngine.Items.Add(new InterpreterEngineSelectionItem(InterpreterEngineType.GoogleCloudPipeline, SttEngineType.DeepgramNova2, "5. Deepgram + Translate + TTS"));
        cmbInterpreterEngine.Items.Add(new InterpreterEngineSelectionItem(InterpreterEngineType.GoogleCloudAdvancedHybridPipeline, SttEngineType.GoogleSpeechToText, "6. Hybrid nâng cao"));
        cmbInterpreterEngine.Items.Add(new InterpreterEngineSelectionItem(InterpreterEngineType.GoogleCloudAdaptiveHybridPipeline, SttEngineType.GoogleSpeechToText, "7. Hybrid thích ứng"));
        cmbInterpreterEngine.Items.Add(new InterpreterEngineSelectionItem(InterpreterEngineType.GoogleCloudPhysicalMuteHybridPipeline, SttEngineType.GoogleSpeechToText, "8. Hybrid chốt bằng micro"));
        _settings.EngineType = currentEngine;
        _settings.SttEngineType = currentSttEngine;
        cmbInterpreterEngine.SelectedItem = cmbInterpreterEngine.Items
            .Cast<InterpreterEngineSelectionItem>()
            .FirstOrDefault(item => item.EngineType == _settings.EngineType
                && (item.SttEngineType is null
                    || item.EngineType == InterpreterEngineType.GoogleCloudHybridPipeline
                    || item.EngineType == InterpreterEngineType.GoogleCloudStreamingPipeline
                    || item.EngineType == InterpreterEngineType.GoogleCloudAdvancedHybridPipeline
                    || item.EngineType == InterpreterEngineType.GoogleCloudAdaptiveHybridPipeline
                    || item.EngineType == InterpreterEngineType.GoogleCloudPhysicalMuteHybridPipeline
                    || item.SttEngineType == _settings.SttEngineType))
            ?? cmbInterpreterEngine.Items[0];
        UpdateEngineUiState();
    }

    private void ConfigureSpeechModelSelector()
    {
        cmbSpeechModel.Items.Clear();
        cmbSpeechModel.Items.Add("latest_long");
        cmbSpeechModel.Items.Add("default");
        cmbSpeechModel.SelectedItem = _settings.SpeechRecognition.Model;
        nudSilenceDuration.Minimum = 700;
        nudSilenceDuration.Maximum = 1200;
        nudSilenceDuration.Value = _settings.SilenceDurationMs;
    }

    private void SelectCurrentInterpreterEngineItem()
    {
        foreach (var item in cmbInterpreterEngine.Items.OfType<InterpreterEngineSelectionItem>())
        {
            if (item.EngineType == _settings.EngineType
                && (item.SttEngineType is null || item.SttEngineType == _settings.SttEngineType))
            {
                cmbInterpreterEngine.SelectedItem = item;
                return;
            }
        }
    }

    private void ApplyUiLanguage()
    {
        ApplyModernUiLanguage();
        if (cmbInterpreterEngine?.Items.Count > 0)
        {
            ConfigureInterpreterEngineSelector();
        }

        Text = T("Phiên dịch cuộc họp Việt - Hàn", "Vietnamese - Korean Meeting Interpreter", "베트남어 - 한국어 회의 통역");
        btnBrowseCredential.Text = T("Chọn tệp...", "Browse...", "파일 선택...");
        btnRefreshDevices.Text = T("Làm mới", "Refresh", "새로 고침");
        btnTestOutput1.Text = T("Kiểm tra", "Test", "테스트");
        btnTestOutput2.Text = T("Kiểm tra", "Test", "테스트");
        btnStart.Text = T("BẮT ĐẦU PHIÊN DỊCH", "START INTERPRETING", "통역 시작");
        btnStop.Text = T("DỪNG PHIÊN DỊCH", "STOP INTERPRETING", "통역 중지");
        btnSettings.Text = T("Cài đặt", "Settings", "설정");
        btnExportHistory.Text = T("Xuất Excel", "Export Excel", "Excel 내보내기");
        chkSuppressHeadset.Text = T("Tắt xử lý mic khi phát ra tai nghe", "Suppress mic during headset playback", "헤드셋 재생 중 마이크 처리 중지");
        chkRecognitionOnly.Text = T("Chế độ kiểm thử nhận dạng", "Recognition test mode", "인식 테스트 모드");
        chkSaveDebugAudio.Text = T("Lưu WAV nhận dạng", "Save recognition WAV", "인식 WAV 저장");
        lblSpeechModel.Text = T("Model STT", "STT model", "STT 모델");
        lblCurrentContentTitle.Text = T("NỘI DUNG PHIÊN DỊCH", "CURRENT TRANSLATION", "현재 통역");
        lblStatus.Text = T("Chọn thiết bị và nhấn Bắt đầu.", "Select devices and press Start.", "장치를 선택하고 시작을 누르세요.");

        if (lblGoogleStatus.ForeColor == Color.ForestGreen)
        {
            lblGoogleStatus.Text = T("✓ Đã kết nối", "✓ Connected", "✓ 연결됨");
        }
        else if (lblGoogleStatus.ForeColor == Color.Firebrick)
        {
            lblGoogleStatus.Text = T("Xác thực thất bại", "Authentication failed", "인증 실패");
        }
        else
        {
            lblGoogleStatus.Text = T("Chưa cấu hình", "Not configured", "미설정");
        }

        _lblCredentialFile!.Text = T("Tệp xác thực Google Cloud", "Google Cloud credential file", "Google Cloud 인증 파일");
        _lblAudioDevicesSection!.Text = T("Thiết bị âm thanh", "Audio devices", "오디오 장치");
        _lblInputMicrophone!.Text = T("Microphone phòng họp", "Meeting room microphone", "회의실 마이크");
        _lblOutput1!.Text = T("Loa phòng họp - phát bản dịch tiếng Việt", "Room speaker - Vietnamese translation", "회의실 스피커 - 베트남어 번역 재생");
        _lblOutput2!.Text = T("Tai nghe quản lý - phát bản dịch tiếng Hàn", "Manager headset - Korean translation", "관리자 헤드셋 - 한국어 번역 재생");
        _lblSessionControlsSection!.Text = T("Điều khiển phiên dịch", "Session controls", "통역 제어");
        _lblMicLevelSection!.Text = T("Mức âm thanh Microphone", "Microphone level", "마이크 음량");
        _lblVadSection!.Text = T("Phát hiện giọng nói", "Voice detection", "음성 감지");
        _lblSilenceDuration!.Text = T("Thời gian im lặng kết thúc câu (ms)", "End silence duration (ms)", "문장 종료 무음 시간(ms)");
        _lblTranslationHistory!.Text = T("Lịch sử phiên dịch", "Translation history", "통역 기록");
        _lblGoogleSection!.Text = T("Cấu hình Google Cloud", "Google Cloud setup", "Google Cloud 설정");
        _lblConnectionStatus!.Text = T("Trạng thái kết nối:", "Connection status:", "연결 상태:");
        _lblDisplayLanguage!.Text = T("Giao diện:", "Interface:", "화면 언어:");

        _lblCredentialFile!.Visible = false;
        _lblGoogleSection!.Visible = false;
        _lblConnectionStatus!.Visible = false;
        txtGoogleCredentialPath.Visible = false;
        btnBrowseCredential.Visible = false;
        lblGoogleStatus.Visible = false;

        lblState.Text = GetStateDisplayName(_service?.State ?? InterpreterState.Idle);
        lblMicLevel.Text = T("Microphone: 0%", "Microphone: 0%", "마이크: 0%");
        lblMicQuality.Text = T("Âm lượng: chờ nói", "Level: waiting", "음량: 대기");
        UpdateSettingsLabels();
        ConfigureGrid();
        UpdateConfigurationReadinessStatus();
    }

    private void AddStaticLabels()
    {
        _lblCredentialFile = new Label
        {
            AutoSize = true,
            Location = new Point(24, 36),
            Text = "Tệp xác thực Google Cloud"
        };
        Controls.Add(_lblCredentialFile);
        _lblAudioDevicesSection = new Label
        {
            AutoSize = true,
            Location = new Point(24, 89),
            Font = new Font("Segoe UI", 9F, FontStyle.Bold),
            Text = "Thiết bị âm thanh"
        };
        Controls.Add(_lblAudioDevicesSection);
        _lblInputMicrophone = new Label
        {
            AutoSize = true,
            Location = new Point(24, 105),
            Text = "Microphone phòng họp"
        };
        Controls.Add(_lblInputMicrophone);
        _lblOutput1 = new Label
        {
            AutoSize = true,
            Location = new Point(286, 105),
            Text = "Loa phòng họp - phát bản dịch tiếng Việt"
        };
        Controls.Add(_lblOutput1);
        _lblOutput2 = new Label
        {
            AutoSize = true,
            Location = new Point(544, 105),
            Text = "Tai nghe quản lý Hàn Quốc - phát bản dịch tiếng Hàn"
        };
        Controls.Add(_lblOutput2);
        _lblSessionControlsSection = new Label
        {
            AutoSize = true,
            Location = new Point(24, 201),
            Font = new Font("Segoe UI", 9F, FontStyle.Bold),
            Text = "Điều khiển phiên dịch"
        };
        Controls.Add(_lblSessionControlsSection);
        _lblMicLevelSection = new Label
        {
            AutoSize = true,
            Location = new Point(24, 263),
            Font = new Font("Segoe UI", 9F, FontStyle.Bold),
            Text = "Mức âm thanh Microphone"
        };
        Controls.Add(_lblMicLevelSection);
        _lblVadSection = new Label
        {
            AutoSize = true,
            Location = new Point(286, 263),
            Font = new Font("Segoe UI", 9F, FontStyle.Bold),
            Text = "Phát hiện giọng nói"
        };
        Controls.Add(_lblVadSection);
        _lblSilenceDuration = new Label
        {
            AutoSize = true,
            Location = new Point(544, 328),
            Text = "Thời gian im lặng kết thúc câu (ms)"
        };
        Controls.Add(_lblSilenceDuration);
        _lblTranslationHistory = new Label
        {
            AutoSize = true,
            Location = new Point(24, 534),
            Font = new Font("Segoe UI", 9F, FontStyle.Bold),
            Text = "Lịch sử phiên dịch"
        };
        Controls.Add(_lblTranslationHistory);
        _lblGoogleSection = new Label
        {
            AutoSize = true,
            Location = new Point(24, 17),
            Font = new Font("Segoe UI", 9F, FontStyle.Bold),
            Text = "Cấu hình Google Cloud"
        };
        Controls.Add(_lblGoogleSection);
        _lblConnectionStatus = new Label
        {
            AutoSize = true,
            Location = new Point(220, 17),
            Text = "Trạng thái kết nối:"
        };
        Controls.Add(_lblConnectionStatus);
        _lblDisplayLanguage = new Label
        {
            AutoSize = true,
            Location = new Point(560, 17),
            Text = "Giao diện:"
        };
        Controls.Add(_lblDisplayLanguage);
    }

    private void RefreshDeviceLists(
        string? preferredInputId = null,
        string? preferredOutput1Id = null,
        string? preferredOutput2Id = null,
        string? preferredInputName = null,
        string? preferredOutput1Name = null,
        string? preferredOutput2Name = null)
    {
        PreserveSelection(
            cmbInputDevice,
            _service!.EnumerateInputDevices(),
            preferredInputId ?? _savedSettings.InputDeviceId,
            preferredInputName ?? _savedSettings.InputDeviceName);
        var outputDevices = _service.EnumerateOutputDevices();
        PreserveSelection(
            cmbOutput1Device,
            outputDevices,
            preferredOutput1Id ?? _savedSettings.Output1DeviceId,
            preferredOutput1Name ?? _savedSettings.Output1DeviceName);
        PreserveSelection(
            cmbOutput2Device,
            outputDevices,
            preferredOutput2Id ?? _savedSettings.Output2DeviceId,
            preferredOutput2Name ?? _savedSettings.Output2DeviceName);
    }

    private static void PreserveSelection(
        ComboBox comboBox,
        IReadOnlyList<AudioDeviceInfo> devices,
        string preferredId = "",
        string preferredName = "")
    {
        var previousDevice = comboBox.SelectedItem as AudioDeviceInfo;
        var previousId = string.IsNullOrWhiteSpace(preferredId) ? previousDevice?.Id : preferredId;
        var previousName = string.IsNullOrWhiteSpace(preferredName) ? previousDevice?.Name : preferredName;
        comboBox.Items.Clear();

        foreach (var device in devices)
        {
            comboBox.Items.Add(device);
        }

        var selected = devices.FirstOrDefault(device => device.Id == previousId)
            ?? devices.FirstOrDefault(device => string.Equals(device.Name, previousName, StringComparison.OrdinalIgnoreCase))
            ?? devices.FirstOrDefault();
        if (selected is not null)
        {
            comboBox.SelectedItem = comboBox.Items.Cast<AudioDeviceInfo>().First(device => device.Id == selected.Id);
        }
    }

    private static void SelectDevice(ComboBox comboBox, AudioDeviceInfo? selectedDevice)
    {
        if (selectedDevice is null)
        {
            return;
        }

        var matchingDevice = comboBox.Items.OfType<AudioDeviceInfo>()
            .FirstOrDefault(device => device.Id == selectedDevice.Id);
        if (matchingDevice is not null)
        {
            comboBox.SelectedItem = matchingDevice;
        }
    }

    private bool TryGetSelectedDevices(
        out AudioDeviceInfo input,
        out AudioDeviceInfo output1,
        out AudioDeviceInfo output2)
    {
        input = cmbInputDevice.SelectedItem as AudioDeviceInfo ?? new AudioDeviceInfo();
        output1 = cmbOutput1Device.SelectedItem as AudioDeviceInfo ?? new AudioDeviceInfo();
        output2 = cmbOutput2Device.SelectedItem as AudioDeviceInfo ?? new AudioDeviceInfo();

        if (string.IsNullOrWhiteSpace(input.Id) || string.IsNullOrWhiteSpace(output1.Id) || string.IsNullOrWhiteSpace(output2.Id))
        {
            MessageBox.Show(this, "Vui lòng chọn Microphone phòng họp, loa phòng họp và tai nghe quản lý Hàn Quốc trước khi bắt đầu.", "Cần cấu hình", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }

        return true;
    }

    private void AddTranslationRow(TranslationResult result)
    {
        var status = result.Success ? "Thành công" : ToUserStatus(result.ErrorMessage);
        var displayOriginal = CommitTranslationToTranscript(result);
        dgvTranslations.Rows.Insert(
            0,
            GetEngineDisplayName(result.Engine),
            result.Timestamp.ToString("HH:mm:ss"),
            GetLanguageDisplayName(result.SourceLanguage),
            displayOriginal,
            GetLanguageDisplayName(result.TargetLanguage),
            result.TranslatedText,
            Math.Round(result.RecognitionMilliseconds),
            Math.Round(result.TranslationMilliseconds),
            Math.Round(result.SynthesisMilliseconds),
            FormatSeconds(result.TotalMilliseconds),
            status);
    }

    private void dgvTranslations_CellClick(object? sender, DataGridViewCellEventArgs e)
    {
        if (e.RowIndex < 0 || e.ColumnIndex < 0)
        {
            return;
        }

        var row = dgvTranslations.Rows[e.RowIndex];
        ShowTranslationHistoryRow(
            row.Cells["Source"].Value?.ToString() ?? T("Ngôn ngữ gốc", "Source", "원본"),
            row.Cells["OriginalText"].Value?.ToString() ?? string.Empty,
            row.Cells["Target"].Value?.ToString() ?? T("Ngôn ngữ đích", "Target", "대상"),
            row.Cells["TranslatedText"].Value?.ToString() ?? string.Empty,
            row.Cells["TotalMs"].Value?.ToString() ?? string.Empty);
    }

    private void ExportTranslationHistoryToExcel(string filePath)
    {
        if (File.Exists(filePath))
        {
            File.Delete(filePath);
        }

        using var archive = ZipFile.Open(filePath, ZipArchiveMode.Create);
        AddZipEntry(archive, "[Content_Types].xml", """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
              <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
              <Default Extension="xml" ContentType="application/xml"/>
              <Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/>
              <Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>
            </Types>
            """);
        AddZipEntry(archive, "_rels/.rels", """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
              <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/>
            </Relationships>
            """);
        AddZipEntry(archive, "xl/workbook.xml", """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
              <sheets>
                <sheet name="Lich su phien dich" sheetId="1" r:id="rId1"/>
              </sheets>
            </workbook>
            """);
        AddZipEntry(archive, "xl/_rels/workbook.xml.rels", """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
              <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/>
            </Relationships>
            """);
        AddZipEntry(archive, "xl/worksheets/sheet1.xml", BuildWorksheetXml());
    }

    private string BuildWorksheetXml()
    {
        var columns = dgvTranslations.Columns
            .Cast<DataGridViewColumn>()
            .OrderBy(column => column.DisplayIndex)
            .ToList();
        var rows = dgvTranslations.Rows
            .Cast<DataGridViewRow>()
            .Where(row => !row.IsNewRow)
            .ToList();

        var xml = new StringBuilder();
        xml.AppendLine("""<?xml version="1.0" encoding="UTF-8" standalone="yes"?>""");
        xml.AppendLine("""<worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">""");
        xml.AppendLine("""  <sheetViews><sheetView workbookViewId="0"/></sheetViews>""");
        xml.AppendLine("""  <sheetData>""");
        AppendExcelRow(xml, 1, columns.Select(column => column.HeaderText));

        for (var rowIndex = 0; rowIndex < rows.Count; rowIndex++)
        {
            AppendExcelRow(
                xml,
                rowIndex + 2,
                columns.Select(column => rows[rowIndex].Cells[column.Index].Value?.ToString() ?? string.Empty));
        }

        xml.AppendLine("""  </sheetData>""");
        xml.AppendLine("""</worksheet>""");
        return xml.ToString();
    }

    private static void AppendExcelRow(StringBuilder xml, int rowNumber, IEnumerable<string> values)
    {
        xml.Append("    <row r=\"");
        xml.Append(rowNumber);
        xml.AppendLine("\">");

        var columnIndex = 1;
        foreach (var value in values)
        {
            var cellReference = GetExcelColumnName(columnIndex) + rowNumber;
            xml.Append("      <c r=\"");
            xml.Append(cellReference);
            xml.AppendLine("\" t=\"inlineStr\"><is><t>" + EscapeXmlText(value) + "</t></is></c>");
            columnIndex++;
        }

        xml.AppendLine("    </row>");
    }

    private static void AddZipEntry(ZipArchive archive, string entryName, string content)
    {
        var entry = archive.CreateEntry(entryName, CompressionLevel.Fastest);
        using var stream = entry.Open();
        using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.Write(content);
    }

    private static string GetExcelColumnName(int columnIndex)
    {
        var name = string.Empty;
        while (columnIndex > 0)
        {
            columnIndex--;
            name = (char)('A' + columnIndex % 26) + name;
            columnIndex /= 26;
        }

        return name;
    }

    private static string EscapeXmlText(string value)
    {
        var sanitized = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            if (character is '\t' or '\n' or '\r' || character >= ' ' && character <= '\uD7FF' || character >= '\uE000')
            {
                sanitized.Append(character);
            }
        }

        return System.Security.SecurityElement.Escape(sanitized.ToString()) ?? string.Empty;
    }

    private void UpdateConfigurationReadinessStatus(string? readyMessage = null)
    {
        if (_service is not null && _service.State != InterpreterState.Idle)
        {
            return;
        }

        if (TryGetReadinessIssue(out var stateText, out var detailText))
        {
            SetDashboardStatus(stateText, detailText, UiTheme.Warning);
            return;
        }

        SetDashboardStatus(
            GetStateDisplayName(InterpreterState.Idle),
            readyMessage ?? T(
                "Đã đủ cấu hình. Chọn thiết bị và nhấn Bắt đầu.",
                "Configuration is complete. Select devices and press Start.",
                "설정이 완료되었습니다. 장치를 선택하고 시작을 누르세요."),
            UiTheme.Success);
    }

    private bool TryGetReadinessIssue(out string stateText, out string detailText)
    {
        var engineType = GetSelectedEngineType();

        if (engineType == InterpreterEngineType.Gemini25Pro && string.IsNullOrWhiteSpace(txtGeminiApiKey.Text))
        {
            stateText = T("Cần cấu hình", "Configuration needed", "설정 필요");
            detailText = T(
                "Chưa có API Key Gemini. Mở Cài đặt để nhập key.",
                "Missing Gemini API key. Open Settings and enter the key.",
                "Gemini API 키가 없습니다. 설정에서 입력하세요.");
            return true;
        }

        if (engineType is InterpreterEngineType.GoogleCloudPipeline
            or InterpreterEngineType.GoogleCloudHybridPipeline
            or InterpreterEngineType.GoogleCloudStreamingPipeline
            or InterpreterEngineType.GoogleCloudAdvancedHybridPipeline
            or InterpreterEngineType.GoogleCloudAdaptiveHybridPipeline
            or InterpreterEngineType.GoogleCloudPhysicalMuteHybridPipeline)
        {
            if (_settings.SttEngineType == SttEngineType.DeepgramNova2
                && engineType == InterpreterEngineType.GoogleCloudPipeline
                && string.IsNullOrWhiteSpace(_settings.Deepgram.ApiKey))
            {
                stateText = T("Cần cấu hình", "Configuration needed", "설정 필요");
                detailText = T(
                    "Chưa có API Key Deepgram. Mở Cài đặt để nhập key.",
                    "Missing Deepgram API key. Open Settings and enter the key.",
                    "Deepgram API 키가 없습니다. 설정에서 입력하세요.");
                return true;
            }

            var credentialPath = txtGoogleCredentialPath.Text.Trim();
            if (string.IsNullOrWhiteSpace(credentialPath))
            {
                stateText = T("Cần cấu hình", "Configuration needed", "설정 필요");
                detailText = T(
                    "Chưa chọn file Service Account JSON của Google Cloud.",
                    "Missing Google Cloud Service Account JSON file.",
                    "Google Cloud 서비스 계정 JSON 파일이 없습니다.");
                return true;
            }

            if (!File.Exists(credentialPath))
            {
                stateText = T("Cần cấu hình", "Configuration needed", "설정 필요");
                detailText = T(
                    "Không tìm thấy file Google Cloud đã lưu. Vui lòng chọn lại trong Cài đặt.",
                    "Saved Google Cloud credential file was not found. Select it again in Settings.",
                    "저장된 Google Cloud 인증 파일을 찾을 수 없습니다. 설정에서 다시 선택하세요.");
                return true;
            }
        }

        if (cmbInputDevice.SelectedItem is not AudioDeviceInfo
            || cmbOutput1Device.SelectedItem is not AudioDeviceInfo
            || cmbOutput2Device.SelectedItem is not AudioDeviceInfo)
        {
            stateText = T("Cần chọn thiết bị", "Select devices", "장치 선택 필요");
            detailText = T(
                "Chưa chọn đủ microphone, loa và tai nghe.",
                "Select microphone, speaker, and headset.",
                "마이크, 회의실 스피커, 헤드셋을 선택하세요.");
            return true;
        }

        stateText = string.Empty;
        detailText = string.Empty;
        return false;
    }

    private void ToggleSessionButtons(bool isRunning)
    {
        if (!isRunning)
        {
            ClearAdvancedRealtimePreviewQueue();
        }

        btnStart.Enabled = !isRunning;
        btnStop.Enabled = isRunning;
        btnStart.Visible = !isRunning;
        btnStop.Visible = isRunning;
        cmbInterpreterEngine.Enabled = !isRunning;
        cmbInputDevice.Enabled = !isRunning;
        cmbOutput1Device.Enabled = !isRunning;
        cmbOutput2Device.Enabled = !isRunning;
        btnSettings.Enabled = !isRunning;
        btnRefreshDevices.Enabled = !isRunning;
        UpdateDashboardMicrophoneLevel(0, sessionActive: isRunning);
        lblState.Text = isRunning
            ? "● " + GetStateDisplayName(InterpreterState.Listening)
            : "● " + GetStateDisplayName(InterpreterState.Idle);
        UpdateStateVisual(isRunning ? InterpreterState.Listening : InterpreterState.Idle);
        if (!isRunning)
        {
            if (IsHandleCreated)
            {
                BeginInvoke(new Action(() => UpdateConfigurationReadinessStatus()));
            }
            else
            {
                UpdateConfigurationReadinessStatus();
            }
        }
    }

    private void ToggleUiForBusy(bool busy)
    {
        btnBrowseCredential.Enabled = !busy;
        btnTestGemini.Enabled = !busy;
        btnRefreshDevices.Enabled = !busy;
        btnTestOutput1.Enabled = !busy;
        btnTestOutput2.Enabled = !busy;

        if (!busy)
        {
            UpdateEngineUiState();
        }
    }

    private InterpreterEngineType GetSelectedEngineType()
        => cmbInterpreterEngine.SelectedItem is InterpreterEngineSelectionItem item
            ? item.EngineType
            : _settings.EngineType;

    private SttEngineType GetSelectedSttEngineType()
        => cmbInterpreterEngine.SelectedItem is InterpreterEngineSelectionItem item && item.SttEngineType.HasValue
            ? item.SttEngineType.Value
            : _settings.SttEngineType;

    private void UpdateEngineUiState()
    {
        var engineType = GetSelectedEngineType();
        _settings.EngineType = engineType;
        if (engineType == InterpreterEngineType.GoogleCloudPipeline)
        {
            _settings.SttEngineType = GetSelectedSttEngineType();
        }
        else if (engineType is InterpreterEngineType.GoogleCloudHybridPipeline
            or InterpreterEngineType.GoogleCloudStreamingPipeline
            or InterpreterEngineType.GoogleCloudAdvancedHybridPipeline
            or InterpreterEngineType.GoogleCloudAdaptiveHybridPipeline
            or InterpreterEngineType.GoogleCloudPhysicalMuteHybridPipeline)
        {
            _settings.SttEngineType = SttEngineType.GoogleSpeechToText;
        }

        var useGemini = engineType == InterpreterEngineType.Gemini25Pro;
        lblEngineDescription.Text = useGemini
            ? T("Gemini Live stream audio hai chiều, không qua Google STT/TTS.", "Gemini Live streams two-way audio without Google STT/TTS.", "Gemini Live는 Google STT/TTS 없이 양방향 오디오를 스트리밍합니다.")
            : engineType == InterpreterEngineType.GoogleCloudPhysicalMuteHybridPipeline
                ? T("Hiện transcript liên tục; chỉ chốt câu và dịch khi phát hiện micro vật lý chuyển sang dữ liệu số 0.", "Shows a continuous transcript and only commits translation when the physical microphone changes to digital silence.", "실시간 자막을 표시하고 물리적 마이크가 디지털 무음으로 전환될 때만 문장을 확정하여 번역합니다.")
            : engineType == InterpreterEngineType.GoogleCloudAdaptiveHybridPipeline
                ? T("Một Streaming STT song ngữ, gom câu nhanh và chỉ kiểm tra batch khi kết quả không chắc chắn.", "One bilingual streaming STT, fast sentence aggregation, and selective batch verification for uncertain results.", "이중 언어 스트리밍 STT 하나로 빠르게 문장을 결합하고 불확실한 결과만 배치로 재확인합니다.")
            : engineType == InterpreterEngineType.GoogleCloudAdvancedHybridPipeline
                ? T("Hiện chữ trực tiếp khi đang nói, sau đó gom các đoạn ngắt quãng thành câu hoàn chỉnh rồi mới dịch.", "Shows live speech text, then combines interrupted fragments into a complete sentence before translating.", "말하는 동안 텍스트를 실시간으로 표시한 뒤, 끊어진 내용을 완전한 문장으로 합쳐 번역합니다.")
            : engineType == InterpreterEngineType.GoogleCloudHybridPipeline
                ? T("Nghe liên tục và xử lý các câu bất đồng bộ. Không cần chờ bản dịch câu trước hoàn tất mới nói tiếp.", "Listens continuously and processes utterances asynchronously. You can keep speaking while previous items are translated.", "계속 듣고 문장을 비동기로 처리합니다. 이전 번역이 끝나기 전에도 계속 말할 수 있습니다.")
            : engineType == InterpreterEngineType.GoogleCloudStreamingPipeline
                ? T("Streaming: gửi audio liên tục vào Google Streaming STT.", "Streaming: sends continuous audio to Google Streaming STT.", "Streaming: 오디오를 Google Streaming STT로 계속 전송합니다.")
            : _settings.SttEngineType == SttEngineType.DeepgramNova2
                ? T("Deepgram Nova-2 nhận dạng realtime → Google dịch/TTS.", "Deepgram Nova-2 realtime STT → Google Translate/TTS.", "Deepgram Nova-2 실시간 STT → Google 번역/TTS.")
                : T("Google Speech-to-Text → Dịch → Tạo giọng nói bằng Google Cloud.", "Google Speech-to-Text → Translate → Synthesize speech with Google Cloud.", "Google Speech-to-Text → Google 번역/TTS.");
    }

    private void ApplyResponsiveLayout()
    {
        ApplyModernResponsiveLayout();
    }

    private void UpdateSettingsLabels()
    {
        lblVadThreshold.Text = T(
            $"Ngưỡng giọng nói: {_settings.VadThreshold:0.000}",
            $"Voice threshold: {_settings.VadThreshold:0.000}",
            $"음성 임계값: {_settings.VadThreshold:0.000}");
    }

    private void UpdateMicrophoneQuality(int level)
    {
        if (level < 8)
        {
            lblMicQuality.Text = T("Âm lượng: quá nhỏ", "Level: too low", "음량: 너무 작음");
            lblMicQuality.ForeColor = Color.DarkGoldenrod;
            return;
        }

        if (level > 90)
        {
            lblMicQuality.Text = T("Âm lượng: quá lớn", "Level: too high", "음량: 너무 큼");
            lblMicQuality.ForeColor = Color.Firebrick;
            return;
        }

        lblMicQuality.Text = T("Âm lượng: tốt", "Level: good", "음량: 좋음");
        lblMicQuality.ForeColor = Color.ForestGreen;
    }

    private static string ResolveVocabularyPath()
    {
        var basePath = Path.Combine(AppContext.BaseDirectory, "speech-vocabulary.json");
        if (File.Exists(basePath))
        {
            return basePath;
        }

        return Path.Combine(Environment.CurrentDirectory, "speech-vocabulary.json");
    }

    private string GetStateDisplayName(InterpreterState state)
        => state switch
        {
            InterpreterState.Idle => T("Sẵn sàng", "Ready", "준비됨"),
            InterpreterState.Listening => T("Đang lắng nghe", "Listening", "듣는 중"),
            InterpreterState.SpeechDetected => T("Đang nhận giọng nói", "Speech detected", "음성 감지됨"),
            InterpreterState.ProcessingSpeech => T("Đang nhận dạng nội dung", "Recognizing speech", "음성 인식 중"),
            InterpreterState.Translating => T("Đang dịch", "Translating", "번역 중"),
            InterpreterState.Synthesizing => T("Đang tạo giọng nói", "Synthesizing speech", "음성 생성 중"),
            InterpreterState.Playing => T("Đang phát bản dịch", "Playing translation", "번역 재생 중"),
            InterpreterState.Error => T("Có lỗi", "Error", "오류"),
            _ => T("Không xác định", "Unknown", "알 수 없음")
        };

    private string GetLanguageDisplayName(SupportedLanguage language)
        => language switch
        {
            SupportedLanguage.Vietnamese => T("Tiếng Việt", "Vietnamese", "베트남어"),
            SupportedLanguage.Korean => T("Tiếng Hàn", "Korean", "한국어"),
            _ => T("Không xác định", "Unknown", "알 수 없음")
        };

    private string GetEngineDisplayName(InterpreterEngineType engineType)
        => engineType switch
        {
            InterpreterEngineType.Gemini25Pro => "1. Gemini Live 3.1 Flash",
            InterpreterEngineType.GoogleCloudPipeline => "2. Speech + Translate + TTS",
            InterpreterEngineType.GoogleCloudHybridPipeline => "3. Hybrid Speech + Async Queue",
            InterpreterEngineType.GoogleCloudStreamingPipeline => "4. Streaming Speech + Translate + TTS",
            InterpreterEngineType.GoogleCloudAdvancedHybridPipeline => "6. Hybrid nâng cao",
            InterpreterEngineType.GoogleCloudAdaptiveHybridPipeline => "7. Hybrid thích ứng",
            InterpreterEngineType.GoogleCloudPhysicalMuteHybridPipeline => "8. Hybrid chốt bằng micro",
            _ => T("Không xác định", "Unknown", "알 수 없음")
        };

    private string ToUserStatus(string? errorMessage)
    {
        if (string.IsNullOrWhiteSpace(errorMessage) || errorMessage.Contains("bỏ qua", StringComparison.OrdinalIgnoreCase))
        {
            return T("Đã bỏ qua", "Ignored", "무시됨");
        }

        if (errorMessage.Contains("lỗi", StringComparison.OrdinalIgnoreCase))
        {
            return T("Có lỗi", "Error", "오류");
        }

        return errorMessage;
    }

    private string ToUserMessage(Exception ex)
    {
        if (ex.Message.Contains("Gemini", StringComparison.OrdinalIgnoreCase))
        {
            return ex.Message;
        }

        if (ex.Message.Contains("Google Cloud", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("API", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("authentication", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("credential", StringComparison.OrdinalIgnoreCase))
        {
            return "Không thể kết nối đến dịch vụ Google Cloud.\n\nVui lòng kiểm tra tệp xác thực, các API đã bật và kết nối Internet.";
        }

        if (ex.Message.Contains("tai nghe", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("Output Device 2", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("Korean headset", StringComparison.OrdinalIgnoreCase))
        {
            return "Không tìm thấy tai nghe quản lý Hàn Quốc.\n\nVui lòng kết nối lại thiết bị và chọn \"Làm mới thiết bị\".";
        }

        if (ex.Message.Contains("loa phòng họp", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("Output Device 1", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("room speaker", StringComparison.OrdinalIgnoreCase))
        {
            return "Không tìm thấy loa phòng họp.\n\nHệ thống đã dừng phát âm thanh để tránh phát nhầm thiết bị.";
        }

        if (ex.Message.Contains("thiết bị", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("device", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("audio", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("microphone", StringComparison.OrdinalIgnoreCase))
        {
            return "Không tìm thấy thiết bị âm thanh đã chọn.\n\nVui lòng kiểm tra kết nối và làm mới danh sách thiết bị.";
        }

        return "Đã xảy ra lỗi trong quá trình xử lý. Vui lòng kiểm tra cấu hình và thử lại.";
    }

    private string T(string vi, string en, string ko)
        => _displayLanguage switch
        {
            DisplayLanguage.English => en,
            DisplayLanguage.Korean => ko,
            _ => vi
        };
}

public enum DisplayLanguage
{
    Vietnamese,
    English,
    Korean
}

public sealed class DisplayLanguageSelectionItem
{
    public DisplayLanguageSelectionItem(DisplayLanguage language, string displayName)
    {
        Language = language;
        DisplayName = displayName;
    }

    public DisplayLanguage Language { get; }

    private string DisplayName { get; }

    public override string ToString() => DisplayName;
}

public sealed class InterpreterEngineSelectionItem
{
    public InterpreterEngineSelectionItem(InterpreterEngineType engineType, SttEngineType? sttEngineType, string displayName)
    {
        EngineType = engineType;
        SttEngineType = sttEngineType;
        DisplayName = displayName;
    }

    public InterpreterEngineType EngineType { get; }

    public SttEngineType? SttEngineType { get; }

    private string DisplayName { get; }

    public override string ToString() => DisplayName;
}
