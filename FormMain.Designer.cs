namespace MeetingInterpreter;

partial class FormMain
{
    private System.ComponentModel.IContainer components = null!;
    private TextBox txtGoogleCredentialPath = null!;
    private Button btnBrowseCredential = null!;
    private Label lblGoogleStatus = null!;
    private ComboBox cmbDisplayLanguage = null!;
    private ComboBox cmbInputDevice = null!;
    private ComboBox cmbOutput1Device = null!;
    private ComboBox cmbOutput2Device = null!;
    private Button btnRefreshDevices = null!;
    private Button btnTestOutput1 = null!;
    private Button btnTestOutput2 = null!;
    private Button btnStart = null!;
    private Button btnStop = null!;
    private Label lblState = null!;
    private Label lblStatus = null!;
    private ProgressBar prgMicLevel = null!;
    private Label lblMicLevel = null!;
    private Label lblMicQuality = null!;
    private TrackBar trkVadThreshold = null!;
    private Label lblVadThreshold = null!;
    private NumericUpDown nudSilenceDuration = null!;
    private CheckBox chkSuppressHeadset = null!;
    private CheckBox chkRecognitionOnly = null!;
    private CheckBox chkSaveDebugAudio = null!;
    private ComboBox cmbSpeechModel = null!;
    private Label lblSpeechModel = null!;
    private Label lblCurrentContentTitle = null!;
    private TextBox txtCurrentContent = null!;
    private DataGridView dgvTranslations = null!;

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _service?.Dispose();
            components?.Dispose();
        }

        base.Dispose(disposing);
    }

    private void InitializeComponent()
    {
        components = new System.ComponentModel.Container();
        txtGoogleCredentialPath = new TextBox();
        btnBrowseCredential = new Button();
        lblGoogleStatus = new Label();
        cmbDisplayLanguage = new ComboBox();
        cmbInputDevice = new ComboBox();
        cmbOutput1Device = new ComboBox();
        cmbOutput2Device = new ComboBox();
        btnRefreshDevices = new Button();
        btnTestOutput1 = new Button();
        btnTestOutput2 = new Button();
        btnStart = new Button();
        btnStop = new Button();
        lblState = new Label();
        lblStatus = new Label();
        prgMicLevel = new ProgressBar();
        lblMicLevel = new Label();
        lblMicQuality = new Label();
        trkVadThreshold = new TrackBar();
        lblVadThreshold = new Label();
        nudSilenceDuration = new NumericUpDown();
        chkSuppressHeadset = new CheckBox();
        chkRecognitionOnly = new CheckBox();
        chkSaveDebugAudio = new CheckBox();
        cmbSpeechModel = new ComboBox();
        lblSpeechModel = new Label();
        lblCurrentContentTitle = new Label();
        txtCurrentContent = new TextBox();
        dgvTranslations = new DataGridView();
        ((System.ComponentModel.ISupportInitialize)trkVadThreshold).BeginInit();
        ((System.ComponentModel.ISupportInitialize)nudSilenceDuration).BeginInit();
        ((System.ComponentModel.ISupportInitialize)dgvTranslations).BeginInit();
        SuspendLayout();
        // 
        // txtGoogleCredentialPath
        // 
        txtGoogleCredentialPath.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        txtGoogleCredentialPath.Location = new Point(24, 56);
        txtGoogleCredentialPath.Name = "txtGoogleCredentialPath";
        txtGoogleCredentialPath.ReadOnly = true;
        txtGoogleCredentialPath.Size = new Size(650, 23);
        txtGoogleCredentialPath.TabIndex = 0;
        // 
        // btnBrowseCredential
        // 
        btnBrowseCredential.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        btnBrowseCredential.Location = new Point(689, 55);
        btnBrowseCredential.Name = "btnBrowseCredential";
        btnBrowseCredential.Size = new Size(100, 25);
        btnBrowseCredential.TabIndex = 1;
        btnBrowseCredential.Text = "Chọn tệp...";
        btnBrowseCredential.UseVisualStyleBackColor = true;
        btnBrowseCredential.Click += btnBrowseCredential_Click;
        // 
        // lblGoogleStatus
        // 
        lblGoogleStatus.AutoSize = true;
        lblGoogleStatus.Location = new Point(350, 17);
        lblGoogleStatus.Name = "lblGoogleStatus";
        lblGoogleStatus.Size = new Size(92, 15);
        lblGoogleStatus.TabIndex = 2;
        lblGoogleStatus.Text = "Chưa cấu hình";
        // 
        // cmbDisplayLanguage
        // 
        cmbDisplayLanguage.DropDownStyle = ComboBoxStyle.DropDownList;
        cmbDisplayLanguage.FormattingEnabled = true;
        cmbDisplayLanguage.Location = new Point(677, 14);
        cmbDisplayLanguage.Name = "cmbDisplayLanguage";
        cmbDisplayLanguage.Size = new Size(112, 23);
        cmbDisplayLanguage.TabIndex = 24;
        cmbDisplayLanguage.SelectedIndexChanged += cmbDisplayLanguage_SelectedIndexChanged;
        // 
        // cmbInputDevice
        // 
        cmbInputDevice.DropDownStyle = ComboBoxStyle.DropDownList;
        cmbInputDevice.FormattingEnabled = true;
        cmbInputDevice.Location = new Point(24, 126);
        cmbInputDevice.Name = "cmbInputDevice";
        cmbInputDevice.Size = new Size(245, 23);
        cmbInputDevice.TabIndex = 3;
        // 
        // cmbOutput1Device
        // 
        cmbOutput1Device.DropDownStyle = ComboBoxStyle.DropDownList;
        cmbOutput1Device.FormattingEnabled = true;
        cmbOutput1Device.Location = new Point(286, 126);
        cmbOutput1Device.Name = "cmbOutput1Device";
        cmbOutput1Device.Size = new Size(245, 23);
        cmbOutput1Device.TabIndex = 4;
        // 
        // cmbOutput2Device
        // 
        cmbOutput2Device.DropDownStyle = ComboBoxStyle.DropDownList;
        cmbOutput2Device.FormattingEnabled = true;
        cmbOutput2Device.Location = new Point(544, 126);
        cmbOutput2Device.Name = "cmbOutput2Device";
        cmbOutput2Device.Size = new Size(245, 23);
        cmbOutput2Device.TabIndex = 5;
        // 
        // btnRefreshDevices
        // 
        btnRefreshDevices.Location = new Point(24, 164);
        btnRefreshDevices.Name = "btnRefreshDevices";
        btnRefreshDevices.Size = new Size(145, 28);
        btnRefreshDevices.TabIndex = 6;
        btnRefreshDevices.Text = "Làm mới thiết bị";
        btnRefreshDevices.UseVisualStyleBackColor = true;
        btnRefreshDevices.Click += btnRefreshDevices_Click;
        // 
        // btnTestOutput1
        // 
        btnTestOutput1.Location = new Point(286, 164);
        btnTestOutput1.Name = "btnTestOutput1";
        btnTestOutput1.Size = new Size(190, 28);
        btnTestOutput1.TabIndex = 7;
        btnTestOutput1.Text = "Kiểm tra loa phòng họp";
        btnTestOutput1.UseVisualStyleBackColor = true;
        btnTestOutput1.Click += btnTestOutput1_Click;
        // 
        // btnTestOutput2
        // 
        btnTestOutput2.Location = new Point(544, 164);
        btnTestOutput2.Name = "btnTestOutput2";
        btnTestOutput2.Size = new Size(190, 28);
        btnTestOutput2.TabIndex = 8;
        btnTestOutput2.Text = "Kiểm tra tai nghe quản lý";
        btnTestOutput2.UseVisualStyleBackColor = true;
        btnTestOutput2.Click += btnTestOutput2_Click;
        // 
        // btnStart
        // 
        btnStart.Location = new Point(24, 223);
        btnStart.Name = "btnStart";
        btnStart.Size = new Size(100, 34);
        btnStart.TabIndex = 9;
        btnStart.Text = "Bắt đầu";
        btnStart.UseVisualStyleBackColor = true;
        btnStart.Click += btnStart_Click;
        // 
        // btnStop
        // 
        btnStop.Enabled = false;
        btnStop.Location = new Point(134, 223);
        btnStop.Name = "btnStop";
        btnStop.Size = new Size(100, 34);
        btnStop.TabIndex = 10;
        btnStop.Text = "Dừng";
        btnStop.UseVisualStyleBackColor = true;
        btnStop.Click += btnStop_Click;
        // lblState
        // 
        lblState.AutoSize = false;
        lblState.Font = new Font("Segoe UI", 12F, FontStyle.Bold);
        lblState.Location = new Point(286, 228);
        lblState.Name = "lblState";
        lblState.Size = new Size(260, 26);
        lblState.TabIndex = 11;
        lblState.Text = "Sẵn sàng";
        lblState.TextAlign = ContentAlignment.MiddleLeft;
        // 
        // lblStatus
        // 
        lblStatus.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        lblStatus.AutoEllipsis = true;
        lblStatus.Location = new Point(286, 258);
        lblStatus.Name = "lblStatus";
        lblStatus.Size = new Size(503, 34);
        lblStatus.TabIndex = 12;
        lblStatus.Text = "Chọn tệp xác thực và thiết bị âm thanh.";
        // 
        // prgMicLevel
        // 
        prgMicLevel.Location = new Point(24, 308);
        prgMicLevel.Name = "prgMicLevel";
        prgMicLevel.Size = new Size(245, 18);
        prgMicLevel.TabIndex = 13;
        // 
        // lblMicLevel
        // 
        lblMicLevel.AutoSize = true;
        lblMicLevel.Location = new Point(24, 285);
        lblMicLevel.Name = "lblMicLevel";
        lblMicLevel.Size = new Size(110, 15);
        lblMicLevel.TabIndex = 14;
        lblMicLevel.Text = "Microphone: 0%";
        // 
        // lblMicQuality
        // 
        lblMicQuality.AutoSize = true;
        lblMicQuality.Location = new Point(24, 331);
        lblMicQuality.Name = "lblMicQuality";
        lblMicQuality.Size = new Size(100, 15);
        lblMicQuality.TabIndex = 25;
        lblMicQuality.Text = "Am luong: Cho noi";
        // 
        // trkVadThreshold
        // 
        trkVadThreshold.Location = new Point(286, 297);
        trkVadThreshold.Maximum = 100;
        trkVadThreshold.Minimum = 1;
        trkVadThreshold.Name = "trkVadThreshold";
        trkVadThreshold.Size = new Size(220, 45);
        trkVadThreshold.TabIndex = 15;
        trkVadThreshold.Value = 25;
        trkVadThreshold.ValueChanged += trkVadThreshold_ValueChanged;
        // 
        // lblVadThreshold
        // 
        lblVadThreshold.AutoSize = true;
        lblVadThreshold.Location = new Point(286, 279);
        lblVadThreshold.Name = "lblVadThreshold";
        lblVadThreshold.Size = new Size(138, 15);
        lblVadThreshold.TabIndex = 16;
        lblVadThreshold.Text = "Ngưỡng giọng nói: 0.025";
        // 
        // nudSilenceDuration
        // 
        nudSilenceDuration.Increment = new decimal(new int[] { 50, 0, 0, 0 });
        nudSilenceDuration.Location = new Point(544, 303);
        nudSilenceDuration.Maximum = new decimal(new int[] { 3000, 0, 0, 0 });
        nudSilenceDuration.Minimum = new decimal(new int[] { 200, 0, 0, 0 });
        nudSilenceDuration.Name = "nudSilenceDuration";
        nudSilenceDuration.Size = new Size(120, 23);
        nudSilenceDuration.TabIndex = 17;
        nudSilenceDuration.Value = new decimal(new int[] { 1000, 0, 0, 0 });
        nudSilenceDuration.ValueChanged += nudSilenceDuration_ValueChanged;
        // 
        // chkSuppressHeadset
        // 
        chkSuppressHeadset.AutoSize = true;
        chkSuppressHeadset.Location = new Point(544, 278);
        chkSuppressHeadset.Name = "chkSuppressHeadset";
        chkSuppressHeadset.Size = new Size(240, 19);
        chkSuppressHeadset.TabIndex = 18;
        chkSuppressHeadset.Text = "Tắt xử lý mic khi phát ra tai nghe";
        chkSuppressHeadset.UseVisualStyleBackColor = true;
        chkSuppressHeadset.CheckedChanged += chkSuppressHeadset_CheckedChanged;
        // 
        // chkRecognitionOnly
        // 
        chkRecognitionOnly.AutoSize = true;
        chkRecognitionOnly.Location = new Point(286, 345);
        chkRecognitionOnly.Name = "chkRecognitionOnly";
        chkRecognitionOnly.Size = new Size(180, 19);
        chkRecognitionOnly.TabIndex = 26;
        chkRecognitionOnly.Text = "Che do kiem thu nhan dang";
        chkRecognitionOnly.UseVisualStyleBackColor = true;
        chkRecognitionOnly.CheckedChanged += chkRecognitionOnly_CheckedChanged;
        // 
        // chkSaveDebugAudio
        // 
        chkSaveDebugAudio.AutoSize = true;
        chkSaveDebugAudio.Location = new Point(544, 357);
        chkSaveDebugAudio.Name = "chkSaveDebugAudio";
        chkSaveDebugAudio.Size = new Size(132, 19);
        chkSaveDebugAudio.TabIndex = 27;
        chkSaveDebugAudio.Text = "Luu WAV nhan dang";
        chkSaveDebugAudio.UseVisualStyleBackColor = true;
        chkSaveDebugAudio.CheckedChanged += chkSaveDebugAudio_CheckedChanged;
        // 
        // cmbSpeechModel
        // 
        cmbSpeechModel.DropDownStyle = ComboBoxStyle.DropDownList;
        cmbSpeechModel.FormattingEnabled = true;
        cmbSpeechModel.Location = new Point(668, 326);
        cmbSpeechModel.Name = "cmbSpeechModel";
        cmbSpeechModel.Size = new Size(121, 23);
        cmbSpeechModel.TabIndex = 28;
        cmbSpeechModel.SelectedIndexChanged += cmbSpeechModel_SelectedIndexChanged;
        // 
        // lblSpeechModel
        // 
        lblSpeechModel.AutoSize = true;
        lblSpeechModel.Location = new Point(544, 330);
        lblSpeechModel.Name = "lblSpeechModel";
        lblSpeechModel.Size = new Size(61, 15);
        lblSpeechModel.TabIndex = 29;
        lblSpeechModel.Text = "Model STT";
        // 
        // lblCurrentContentTitle
        // 
        lblCurrentContentTitle.AutoSize = true;
        lblCurrentContentTitle.Font = new Font("Segoe UI", 10F, FontStyle.Bold);
        lblCurrentContentTitle.Location = new Point(24, 395);
        lblCurrentContentTitle.Name = "lblCurrentContentTitle";
        lblCurrentContentTitle.Size = new Size(157, 19);
        lblCurrentContentTitle.TabIndex = 19;
        lblCurrentContentTitle.Text = "Nội dung đang xử lý";
        // 
        // txtCurrentContent
        // 
        txtCurrentContent.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        txtCurrentContent.Font = new Font("Segoe UI", 12F);
        txtCurrentContent.Location = new Point(24, 418);
        txtCurrentContent.Multiline = true;
        txtCurrentContent.Name = "txtCurrentContent";
        txtCurrentContent.ReadOnly = true;
        txtCurrentContent.ScrollBars = ScrollBars.Vertical;
        txtCurrentContent.Size = new Size(765, 100);
        txtCurrentContent.TabIndex = 20;
        // 
        // dgvTranslations
        // 
        dgvTranslations.AllowUserToAddRows = false;
        dgvTranslations.AllowUserToDeleteRows = false;
        dgvTranslations.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        dgvTranslations.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize;
        dgvTranslations.Location = new Point(24, 554);
        dgvTranslations.Name = "dgvTranslations";
        dgvTranslations.ReadOnly = true;
        dgvTranslations.RowHeadersVisible = false;
        dgvTranslations.Size = new Size(765, 241);
        dgvTranslations.TabIndex = 21;
        dgvTranslations.CellClick += dgvTranslations_CellClick;
        // 
        // FormMain
        // 
        AutoScaleDimensions = new SizeF(7F, 15F);
        AutoScaleMode = AutoScaleMode.Font;
        ClientSize = new Size(814, 817);
        Controls.Add(dgvTranslations);
        Controls.Add(txtCurrentContent);
        Controls.Add(lblCurrentContentTitle);
        Controls.Add(lblSpeechModel);
        Controls.Add(cmbSpeechModel);
        Controls.Add(chkSaveDebugAudio);
        Controls.Add(chkRecognitionOnly);
        Controls.Add(chkSuppressHeadset);
        Controls.Add(nudSilenceDuration);
        Controls.Add(lblVadThreshold);
        Controls.Add(trkVadThreshold);
        Controls.Add(lblMicQuality);
        Controls.Add(lblMicLevel);
        Controls.Add(prgMicLevel);
        Controls.Add(lblStatus);
        Controls.Add(lblState);
        Controls.Add(btnStop);
        Controls.Add(btnStart);
        Controls.Add(btnTestOutput2);
        Controls.Add(btnTestOutput1);
        Controls.Add(btnRefreshDevices);
        Controls.Add(cmbOutput2Device);
        Controls.Add(cmbOutput1Device);
        Controls.Add(cmbInputDevice);
        Controls.Add(cmbDisplayLanguage);
        Controls.Add(lblGoogleStatus);
        Controls.Add(btnBrowseCredential);
        Controls.Add(txtGoogleCredentialPath);
        MinimumSize = new Size(830, 856);
        Name = "FormMain";
        Text = "Phiên dịch cuộc họp Việt - Hàn";
        Load += FormMain_Load;
        ((System.ComponentModel.ISupportInitialize)trkVadThreshold).EndInit();
        ((System.ComponentModel.ISupportInitialize)nudSilenceDuration).EndInit();
        ((System.ComponentModel.ISupportInitialize)dgvTranslations).EndInit();
        ResumeLayout(false);
        PerformLayout();
    }
}
