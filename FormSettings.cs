using MeetingInterpreter.Controls;
using MeetingInterpreter.Models;

namespace MeetingInterpreter;

public sealed class FormSettings : Form
{
    private readonly InterpreterSettings _settings;
    private readonly Func<string, Task> _testGoogleAsync;
    private readonly Func<string, Task> _testGeminiAsync;
    private readonly Func<string, Task> _testDeepgramAsync;
    private readonly TextBox _txtGoogleCredentialPath = new();
    private readonly TextBox _txtGeminiApiKey = new();
    private readonly TextBox _txtDeepgramApiKey = new();
    private readonly Label _lblGoogleStatus = new();
    private readonly Label _lblGeminiStatus = new();
    private readonly Label _lblDeepgramStatus = new();
    private readonly Button _btnSave = new();
    private readonly ComboBox _cmbDisplayLanguage = new();
    private readonly ComboBox _cmbSttEngine = new();
    private readonly ComboBox _cmbSpeechModel = new();
    private readonly TrackBar _trkVadThreshold = new();
    private readonly Label _lblVadValue = new();
    private readonly NumericUpDown _nudSilenceDuration = new();
    private readonly CheckBox _chkSuppressHeadset = new();
    private readonly CheckBox _chkRecognitionOnly = new();
    private readonly CheckBox _chkSaveDebugAudio = new();

    public FormSettings(
        string googleCredentialPath,
        string geminiApiKey,
        InterpreterSettings settings,
        DisplayLanguage displayLanguage,
        Func<string, Task> testGoogleAsync,
        Func<string, Task> testGeminiAsync,
        Func<string, Task> testDeepgramAsync)
    {
        _settings = settings;
        _testGoogleAsync = testGoogleAsync;
        _testGeminiAsync = testGeminiAsync;
        _testDeepgramAsync = testDeepgramAsync;
        GoogleCredentialPath = googleCredentialPath;
        GeminiApiKey = geminiApiKey;
        DisplayLanguage = displayLanguage;

        Text = T("Cài đặt", "Settings", "설정");
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(760, 520);
        Font = UiTheme.BodyFont;
        BackColor = UiTheme.Background;

        BuildUi();
        LoadValues();
    }

    public string GoogleCredentialPath { get; private set; }

    public string GeminiApiKey { get; private set; }

    public DisplayLanguage DisplayLanguage { get; private set; }

    private void BuildUi()
    {
        var title = new Label
        {
            Text = T("Cài đặt", "Settings", "설정"),
            Font = UiTheme.AppTitleFont,
            ForeColor = UiTheme.TextPrimary,
            AutoSize = true,
            Location = new Point(24, 18)
        };
        Controls.Add(title);

        var tabs = new TabControl
        {
            Location = new Point(24, 70),
            Size = new Size(712, 380),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
        };
        Controls.Add(tabs);

        tabs.TabPages.Add(BuildGeneralTab());
        tabs.TabPages.Add(BuildGeminiTab());
        tabs.TabPages.Add(BuildDeepgramTab());
        tabs.TabPages.Add(BuildGoogleTab());
        tabs.TabPages.Add(BuildAudioTab());
        tabs.TabPages.Add(BuildAdvancedTab());

        _btnSave.Text = T("Lưu", "Save", "저장");
        _btnSave.Location = new Point(548, 468);
        _btnSave.Size = new Size(88, 32);
        _btnSave.Click += (_, _) => SaveAndClose();
        UiTheme.StylePrimaryButton(_btnSave);
        _btnSave.Height = 32;
        Controls.Add(_btnSave);

        var btnCancel = new Button
        {
            Text = T("Đóng", "Close", "닫기"),
            Location = new Point(648, 468),
            Size = new Size(88, 32)
        };
        UiTheme.StyleSecondaryButton(btnCancel);
        btnCancel.Click += (_, _) => DialogResult = DialogResult.Cancel;
        Controls.Add(btnCancel);
    }

    private TabPage BuildGeneralTab()
    {
        var page = CreateTab(T("Phiên dịch", "Interpreter", "통역"));
        AddLabel(page, T("Ngôn ngữ giao diện", "Interface language", "화면 언어"), 24, 28);
        _cmbDisplayLanguage.Location = new Point(210, 24);
        _cmbDisplayLanguage.Size = new Size(180, 28);
        UiTheme.StyleCombo(_cmbDisplayLanguage);
        _cmbDisplayLanguage.Items.Add(new DisplayLanguageSelectionItem(DisplayLanguage.Vietnamese, "Tiếng Việt"));
        _cmbDisplayLanguage.Items.Add(new DisplayLanguageSelectionItem(DisplayLanguage.English, "English"));
        _cmbDisplayLanguage.Items.Add(new DisplayLanguageSelectionItem(DisplayLanguage.Korean, "한국어"));
        page.Controls.Add(_cmbDisplayLanguage);

        AddLabel(page, T("Engine STT đầu vào", "Input STT engine", "입력 STT 엔진"), 24, 78);
        _cmbSttEngine.Location = new Point(210, 74);
        _cmbSttEngine.Size = new Size(220, 28);
        UiTheme.StyleCombo(_cmbSttEngine);
        _cmbSttEngine.Items.Add(new SttEngineSelectionItem(SttEngineType.GoogleSpeechToText, "Google Speech-to-Text"));
        _cmbSttEngine.Items.Add(new SttEngineSelectionItem(SttEngineType.DeepgramNova2, "Deepgram (Nova-2)"));
        page.Controls.Add(_cmbSttEngine);

        AddLabel(page, T("Model STT Google", "Google STT model", "Google STT 모델"), 24, 128);
        _cmbSpeechModel.Location = new Point(210, 124);
        _cmbSpeechModel.Size = new Size(180, 28);
        UiTheme.StyleCombo(_cmbSpeechModel);
        _cmbSpeechModel.Items.Add("latest_long");
        _cmbSpeechModel.Items.Add("default");
        page.Controls.Add(_cmbSpeechModel);
        return page;
    }

    private TabPage BuildGeminiTab()
    {
        var page = CreateTab("Gemini");
        AddLabel(page, "API Key", 24, 30);
        _txtGeminiApiKey.Location = new Point(130, 26);
        _txtGeminiApiKey.Size = new Size(390, 25);
        _txtGeminiApiKey.UseSystemPasswordChar = true;
        page.Controls.Add(_txtGeminiApiKey);

        var btnTestGemini = new Button
        {
            Text = T("Kiểm tra kết nối", "Test connection", "연결 테스트"),
            Location = new Point(536, 25),
            Size = new Size(138, 28)
        };
        UiTheme.StyleSecondaryButton(btnTestGemini);
        btnTestGemini.Click += async (_, _) => await TestGeminiAsync();
        page.Controls.Add(btnTestGemini);

        AddLabel(page, "Model", 24, 78);
        AddValue(page, _settings.Gemini.Model, 130, 78);
        _lblGeminiStatus.Location = new Point(130, 116);
        _lblGeminiStatus.AutoSize = true;
        page.Controls.Add(_lblGeminiStatus);
        return page;
    }

    private TabPage BuildDeepgramTab()
    {
        var page = CreateTab("Deepgram");
        AddLabel(page, "API Key", 24, 30);
        _txtDeepgramApiKey.Location = new Point(130, 26);
        _txtDeepgramApiKey.Size = new Size(390, 25);
        _txtDeepgramApiKey.UseSystemPasswordChar = true;
        page.Controls.Add(_txtDeepgramApiKey);

        var btnTestDeepgram = new Button
        {
            Text = T("Kiểm tra kết nối", "Test connection", "연결 테스트"),
            Location = new Point(536, 25),
            Size = new Size(138, 28)
        };
        UiTheme.StyleSecondaryButton(btnTestDeepgram);
        btnTestDeepgram.Click += async (_, _) => await TestDeepgramAsync();
        page.Controls.Add(btnTestDeepgram);

        AddLabel(page, "Model", 24, 78);
        AddValue(page, _settings.Deepgram.Model, 130, 78);
        AddLabel(page, T("Ngôn ngữ", "Language", "언어"), 24, 118);
        AddValue(page, "multi", 130, 118);
        _lblDeepgramStatus.Location = new Point(130, 156);
        _lblDeepgramStatus.AutoSize = true;
        page.Controls.Add(_lblDeepgramStatus);
        return page;
    }

    private TabPage BuildGoogleTab()
    {
        var page = CreateTab("Google Cloud");
        AddLabel(page, T("Service Account", "Service Account", "서비스 계정"), 24, 30);
        _txtGoogleCredentialPath.Location = new Point(150, 26);
        _txtGoogleCredentialPath.Size = new Size(370, 25);
        _txtGoogleCredentialPath.ReadOnly = true;
        page.Controls.Add(_txtGoogleCredentialPath);

        var btnBrowseGoogle = new Button
        {
            Text = T("Chọn tệp...", "Browse...", "파일 선택..."),
            Location = new Point(536, 25),
            Size = new Size(88, 28)
        };
        UiTheme.StyleSecondaryButton(btnBrowseGoogle);
        btnBrowseGoogle.Click += (_, _) => BrowseGoogleCredential();
        page.Controls.Add(btnBrowseGoogle);

        var btnTestGoogle = new Button
        {
            Text = T("Kiểm tra", "Test", "테스트"),
            Location = new Point(632, 25),
            Size = new Size(76, 28)
        };
        UiTheme.StyleSecondaryButton(btnTestGoogle);
        btnTestGoogle.Click += async (_, _) => await TestGoogleAsync();
        page.Controls.Add(btnTestGoogle);

        _lblGoogleStatus.Location = new Point(150, 70);
        _lblGoogleStatus.AutoSize = true;
        page.Controls.Add(_lblGoogleStatus);
        return page;
    }

    private TabPage BuildAudioTab()
    {
        var page = CreateTab(T("Âm thanh", "Audio", "오디오"));
        _chkSuppressHeadset.Text = T("Tắt xử lý mic khi đang phát ra tai nghe quản lý", "Suppress mic while manager headset is playing", "관리자 헤드셋 재생 중 마이크 처리 중지");
        _chkSuppressHeadset.Location = new Point(24, 30);
        _chkSuppressHeadset.AutoSize = true;
        page.Controls.Add(_chkSuppressHeadset);
        AddValue(page, T("Thiết bị microphone, loa phòng họp và tai nghe được chọn ở màn hình chính.", "Microphone, room speaker, and headset are selected on the main screen.", "마이크, 회의실 스피커 및 헤드셋은 메인 화면에서 선택합니다."), 24, 75, 600);
        return page;
    }

    private TabPage BuildAdvancedTab()
    {
        var page = CreateTab(T("Nâng cao", "Advanced", "고급"));
        AddLabel(page, T("Ngưỡng phát hiện giọng nói", "Voice detection threshold", "음성 감지 임계값"), 24, 30);
        _trkVadThreshold.Location = new Point(220, 24);
        _trkVadThreshold.Minimum = 1;
        _trkVadThreshold.Maximum = 100;
        _trkVadThreshold.TickFrequency = 10;
        _trkVadThreshold.Size = new Size(280, 45);
        _trkVadThreshold.ValueChanged += (_, _) => UpdateVadValue();
        page.Controls.Add(_trkVadThreshold);

        _lblVadValue.Location = new Point(520, 32);
        _lblVadValue.Size = new Size(80, 22);
        page.Controls.Add(_lblVadValue);

        AddLabel(page, T("Thời gian im lặng kết thúc câu", "End silence duration", "문장 종료 무음 시간"), 24, 90);
        _nudSilenceDuration.Location = new Point(250, 86);
        _nudSilenceDuration.Minimum = 700;
        _nudSilenceDuration.Maximum = 1200;
        _nudSilenceDuration.Increment = 50;
        _nudSilenceDuration.Size = new Size(110, 25);
        page.Controls.Add(_nudSilenceDuration);
        AddValue(page, "ms", 368, 90, 40);

        _chkRecognitionOnly.Text = T("Chế độ chỉ kiểm thử nhận dạng", "Recognition test mode only", "인식 테스트 전용 모드");
        _chkRecognitionOnly.Location = new Point(24, 145);
        _chkRecognitionOnly.AutoSize = true;
        page.Controls.Add(_chkRecognitionOnly);

        _chkSaveDebugAudio.Text = T("Lưu WAV nhận dạng", "Save recognition WAV", "인식 WAV 저장");
        _chkSaveDebugAudio.Location = new Point(24, 180);
        _chkSaveDebugAudio.AutoSize = true;
        page.Controls.Add(_chkSaveDebugAudio);
        return page;
    }

    private void LoadValues()
    {
        _txtGoogleCredentialPath.Text = GoogleCredentialPath;
        _txtGeminiApiKey.Text = GeminiApiKey;
        _txtDeepgramApiKey.Text = _settings.Deepgram.ApiKey;
        _cmbSpeechModel.SelectedItem = _settings.SpeechRecognition.Model;
        _trkVadThreshold.Value = Math.Clamp((int)Math.Round(_settings.VadThreshold * 1000), _trkVadThreshold.Minimum, _trkVadThreshold.Maximum);
        _nudSilenceDuration.Value = Math.Clamp(_settings.SilenceDurationMs, (int)_nudSilenceDuration.Minimum, (int)_nudSilenceDuration.Maximum);
        _chkSuppressHeadset.Checked = _settings.SuppressMicDuringHeadsetPlayback;
        _chkRecognitionOnly.Checked = _settings.RecognitionOnlyMode;
        _chkSaveDebugAudio.Checked = _settings.SpeechRecognition.SaveRecognitionAudioForDebug;

        foreach (var item in _cmbDisplayLanguage.Items.OfType<DisplayLanguageSelectionItem>())
        {
            if (item.Language == DisplayLanguage)
            {
                _cmbDisplayLanguage.SelectedItem = item;
                break;
            }
        }

        foreach (var item in _cmbSttEngine.Items.OfType<SttEngineSelectionItem>())
        {
            if (item.EngineType == _settings.SttEngineType)
            {
                _cmbSttEngine.SelectedItem = item;
                break;
            }
        }

        _lblGeminiStatus.Text = T("Chưa cấu hình", "Not configured", "설정 안 됨");
        _lblGoogleStatus.Text = T("Chưa cấu hình", "Not configured", "설정 안 됨");
        _lblDeepgramStatus.Text = T("Chưa cấu hình", "Not configured", "설정 안 됨");
        UpdateVadValue();
    }

    private void BrowseGoogleCredential()
    {
        using var dialog = new OpenFileDialog
        {
            Title = T("Chọn tệp Service Account JSON của Google Cloud", "Select Google Cloud Service Account JSON", "Google Cloud 서비스 계정 JSON 선택"),
            Filter = T("Tệp JSON (*.json)|*.json", "JSON files (*.json)|*.json", "JSON 파일 (*.json)|*.json"),
            CheckFileExists = true,
            Multiselect = false
        };

        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            _txtGoogleCredentialPath.Text = dialog.FileName;
        }
    }

    private async Task TestGeminiAsync()
    {
        if (string.IsNullOrWhiteSpace(_txtGeminiApiKey.Text))
        {
            MessageBox.Show(this, T("Vui lòng nhập API Key Gemini trước.", "Enter the Gemini API key first.", "먼저 Gemini API 키를 입력하세요."), T("Cần cấu hình", "Configuration needed", "설정 필요"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        await RunTestAsync(
            _lblGeminiStatus,
            () => _testGeminiAsync(_txtGeminiApiKey.Text),
            T("Đã kết nối Gemini Live 3.1 Flash", "Gemini Live 3.1 Flash connected", "Gemini Live 3.1 Flash 연결됨"),
            T("Không thể kết nối Gemini Live 3.1 Flash", "Unable to connect Gemini Live 3.1 Flash", "Gemini Live 3.1 Flash에 연결할 수 없습니다"));
    }

    private async Task TestGoogleAsync()
    {
        if (string.IsNullOrWhiteSpace(_txtGoogleCredentialPath.Text) || !File.Exists(_txtGoogleCredentialPath.Text))
        {
            MessageBox.Show(this, T("Vui lòng chọn tệp Service Account JSON trước.", "Select the Service Account JSON file first.", "먼저 서비스 계정 JSON 파일을 선택하세요."), T("Cần cấu hình", "Configuration needed", "설정 필요"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        await RunTestAsync(
            _lblGoogleStatus,
            () => _testGoogleAsync(_txtGoogleCredentialPath.Text),
            T("Đã kết nối Google Cloud", "Google Cloud connected", "Google Cloud 연결됨"),
            T("Không thể kết nối Google Cloud", "Unable to connect Google Cloud", "Google Cloud에 연결할 수 없습니다"));
    }

    private async Task TestDeepgramAsync()
    {
        if (string.IsNullOrWhiteSpace(_txtDeepgramApiKey.Text))
        {
            MessageBox.Show(this, T("Vui lòng nhập API Key Deepgram trước.", "Enter the Deepgram API key first.", "먼저 Deepgram API 키를 입력하세요."), T("Cần cấu hình", "Configuration needed", "설정 필요"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        await RunTestAsync(
            _lblDeepgramStatus,
            () => _testDeepgramAsync(_txtDeepgramApiKey.Text),
            T("Đã kết nối Deepgram", "Deepgram connected", "Deepgram 연결됨"),
            T("Không thể kết nối Deepgram", "Unable to connect Deepgram", "Deepgram에 연결할 수 없습니다"));
    }

    private async Task RunTestAsync(Label statusLabel, Func<Task> test, string successText, string failureText)
    {
        statusLabel.Text = T("Đang kiểm tra...", "Testing...", "테스트 중...");
        statusLabel.ForeColor = UiTheme.Warning;
        _btnSave.Enabled = false;

        try
        {
            await test();
            statusLabel.Text = "● " + successText;
            statusLabel.ForeColor = UiTheme.Success;
        }
        catch (Exception ex)
        {
            statusLabel.Text = "● " + failureText;
            statusLabel.ForeColor = UiTheme.Error;
            MessageBox.Show(this, ex.Message, failureText, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            _btnSave.Enabled = true;
        }
    }

    private void SaveAndClose()
    {
        GoogleCredentialPath = _txtGoogleCredentialPath.Text;
        GeminiApiKey = _txtGeminiApiKey.Text;
        _settings.Gemini.ApiKey = GeminiApiKey.Trim();
        _settings.Deepgram.ApiKey = _txtDeepgramApiKey.Text.Trim();
        _settings.Deepgram.Language = "multi";
        if (_cmbSttEngine.SelectedItem is SttEngineSelectionItem sttItem)
        {
            _settings.SttEngineType = sttItem.EngineType;
        }

        _settings.SpeechRecognition.Model = _cmbSpeechModel.SelectedItem?.ToString() ?? _settings.SpeechRecognition.Model;
        _settings.VadThreshold = _trkVadThreshold.Value / 1000.0;
        _settings.SilenceDurationMs = (int)_nudSilenceDuration.Value;
        _settings.SuppressMicDuringHeadsetPlayback = _chkSuppressHeadset.Checked;
        _settings.RecognitionOnlyMode = _chkRecognitionOnly.Checked;
        _settings.SpeechRecognition.SaveRecognitionAudioForDebug = _chkSaveDebugAudio.Checked;
        if (_cmbDisplayLanguage.SelectedItem is DisplayLanguageSelectionItem languageItem)
        {
            DisplayLanguage = languageItem.Language;
        }

        DialogResult = DialogResult.OK;
    }

    private void UpdateVadValue()
        => _lblVadValue.Text = $"{_trkVadThreshold.Value / 1000.0:0.000}";

    private string T(string vi, string en, string ko)
        => DisplayLanguage switch
        {
            MeetingInterpreter.DisplayLanguage.English => en,
            MeetingInterpreter.DisplayLanguage.Korean => ko,
            _ => vi
        };

    private static TabPage CreateTab(string title)
        => new(title)
        {
            BackColor = Color.White,
            Padding = new Padding(18)
        };

    private static void AddLabel(Control parent, string text, int x, int y)
    {
        parent.Controls.Add(new Label
        {
            Text = text,
            ForeColor = UiTheme.TextPrimary,
            Font = UiTheme.StrongBodyFont,
            AutoSize = true,
            Location = new Point(x, y)
        });
    }

    private static void AddValue(Control parent, string text, int x, int y, int width = 360)
    {
        parent.Controls.Add(new Label
        {
            Text = text,
            ForeColor = UiTheme.TextSecondary,
            Font = UiTheme.BodyFont,
            Location = new Point(x, y),
            Size = new Size(width, 24)
        });
    }
}

public sealed class SttEngineSelectionItem
{
    public SttEngineSelectionItem(SttEngineType engineType, string displayName)
    {
        EngineType = engineType;
        DisplayName = displayName;
    }

    public SttEngineType EngineType { get; }

    private string DisplayName { get; }

    public override string ToString() => DisplayName;
}
