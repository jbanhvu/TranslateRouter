using System.Diagnostics;

namespace MeetingInterpreter.Updater;

public sealed class UpdaterForm : Form
{
    private static readonly TimeSpan ServerCheckTimeout = TimeSpan.FromSeconds(5);
    private static readonly StringComparer PathComparer = StringComparer.OrdinalIgnoreCase;
    private readonly UpdaterLogger _logger;
    private readonly Label _lblTitle = new();
    private readonly Label _lblStatus = new();
    private readonly ProgressBar _progressBar = new();

    public UpdaterForm(UpdaterLogger logger)
    {
        _logger = logger;
        Text = "Meeting Interpreter";
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(460, 150);
        Font = new Font("Segoe UI", 9F);
        BackColor = Color.White;
        ShowInTaskbar = true;

        _lblTitle.Text = "Meeting Interpreter";
        _lblTitle.Font = new Font("Segoe UI", 14F, FontStyle.Bold);
        _lblTitle.Location = new Point(24, 20);
        _lblTitle.Size = new Size(400, 30);
        Controls.Add(_lblTitle);

        _lblStatus.Text = "Đang kiểm tra phiên bản...";
        _lblStatus.Location = new Point(26, 62);
        _lblStatus.Size = new Size(408, 22);
        Controls.Add(_lblStatus);

        _progressBar.Location = new Point(28, 98);
        _progressBar.Size = new Size(404, 18);
        _progressBar.Style = ProgressBarStyle.Marquee;
        _progressBar.MarqueeAnimationSpeed = 20;
        Controls.Add(_progressBar);

        Shown += async (_, _) => await RunAsync();
    }

    private async Task RunAsync()
    {
        _logger.Info("Updater started");

        try
        {
            if (IsMainAppRunning())
            {
                _logger.Info("MeetingInterpreter is already running");
                MessageBox.Show(
                    this,
                    "Meeting Interpreter đang được sử dụng.",
                    "Meeting Interpreter",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                Close();
                return;
            }

            var localFolder = AppContext.BaseDirectory;
            var localExePath = Path.Combine(localFolder, UpdateConfiguration.MainExecutableName);
            var localVersionPath = Path.Combine(localFolder, UpdateConfiguration.VersionFileName);

            var localVersion = await ReadVersionFileAsync(localVersionPath, TimeSpan.FromSeconds(1)).ConfigureAwait(true)
                ?? new Version(0, 0, 0);
            _logger.Info($"Local version: {localVersion}");

            var serverVersionPath = Path.Combine(UpdateConfiguration.ServerPath, UpdateConfiguration.VersionFileName);
            var serverVersion = await ReadVersionFileAsync(serverVersionPath, ServerCheckTimeout).ConfigureAwait(true);
            if (serverVersion is null)
            {
                _logger.Info("Server unavailable");
                StartLocalOrShowError(localExePath, "Không tìm thấy MeetingInterpreter.exe và không thể kết nối tới máy chủ cập nhật.");
                return;
            }

            _logger.Info($"Server version: {serverVersion}");
            if (serverVersion.CompareTo(localVersion) <= 0 && File.Exists(localExePath))
            {
                StartMeetingInterpreter(localExePath);
                return;
            }

            if (serverVersion.CompareTo(localVersion) <= 0)
            {
                _logger.Info("Local executable missing; fresh deployment will be attempted");
            }
            else
            {
                _logger.Info("Update available");
                var answer = MessageBox.Show(
                    this,
                    $"Đã có phiên bản mới của Meeting Interpreter.{Environment.NewLine}{Environment.NewLine}" +
                    $"Phiên bản hiện tại: {localVersion}{Environment.NewLine}" +
                    $"Phiên bản mới: {serverVersion}{Environment.NewLine}{Environment.NewLine}" +
                    "Bạn có muốn cập nhật ngay không?",
                    "Cập nhật Meeting Interpreter",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Question);

                if (answer != DialogResult.Yes)
                {
                    _logger.Info("User declined");
                    StartLocalOrShowError(localExePath, "Không tìm thấy MeetingInterpreter.exe local.");
                    return;
                }

                _logger.Info("User accepted");
            }

            await UpdateFromServerAsync(localFolder, serverVersion).ConfigureAwait(true);
            StartMeetingInterpreter(localExePath);
        }
        catch (Exception ex)
        {
            _logger.Error("Updater failed", ex);
            StartLocalOrShowError(
                Path.Combine(AppContext.BaseDirectory, UpdateConfiguration.MainExecutableName),
                "Không tìm thấy MeetingInterpreter.exe và cập nhật thất bại.");
        }
    }

    private async Task UpdateFromServerAsync(string localFolder, Version serverVersion)
    {
        var tempFolder = Path.Combine(localFolder, "_update_temp");
        var backupFolder = Path.Combine(localFolder, "_update_backup");

        TryDeleteDirectory(tempFolder);
        TryDeleteDirectory(backupFolder);
        Directory.CreateDirectory(tempFolder);

        try
        {
            SetProgress("Đang tải gói cập nhật...", ProgressBarStyle.Marquee);
            _logger.Info("Copy temp started");
            await Task.Run(() => CopyDirectory(UpdateConfiguration.ServerPath, tempFolder, skipVersionFile: false)).ConfigureAwait(true);
            _logger.Info("Copy temp completed");

            ValidateTempPackage(tempFolder, serverVersion);

            SetProgress("Đang sao lưu phiên bản hiện tại...", ProgressBarStyle.Marquee);
            Directory.CreateDirectory(backupFolder);
            BackupFilesToBeReplaced(tempFolder, localFolder, backupFolder);
            _logger.Info("Backup completed");

            try
            {
                SetProgress("Đang cập nhật phần mềm...", ProgressBarStyle.Continuous);
                _logger.Info("Replace started");
                ReplaceFromTemp(tempFolder, localFolder, copyVersionLast: true);
                _logger.Info("Replace completed");
                _logger.Info("Version updated");
                TryDeleteDirectory(tempFolder);
                TryDeleteDirectory(backupFolder);
                SetProgress("Cập nhật hoàn tất. Đang mở Meeting Interpreter...", ProgressBarStyle.Marquee);
            }
            catch (Exception ex)
            {
                _logger.Error("Replace failed", ex);
                Rollback(localFolder, backupFolder);
                throw;
            }
        }
        catch
        {
            TryDeleteDirectory(tempFolder);
            throw;
        }
    }

    private static async Task<Version?> ReadVersionFileAsync(string path, TimeSpan timeout)
    {
        try
        {
            var text = await Task.Run(() => File.ReadAllText(path).Trim()).WaitAsync(timeout).ConfigureAwait(false);
            return Version.TryParse(text, out var version) ? version : null;
        }
        catch
        {
            return null;
        }
    }

    private static void ValidateTempPackage(string tempFolder, Version serverVersion)
    {
        var tempExe = Path.Combine(tempFolder, UpdateConfiguration.MainExecutableName);
        var tempVersionPath = Path.Combine(tempFolder, UpdateConfiguration.VersionFileName);
        if (!File.Exists(tempExe) || !File.Exists(tempVersionPath))
        {
            throw new InvalidOperationException("Gói cập nhật thiếu MeetingInterpreter.exe hoặc Version.txt.");
        }

        var tempVersionText = File.ReadAllText(tempVersionPath).Trim();
        if (!Version.TryParse(tempVersionText, out var tempVersion) || tempVersion != serverVersion)
        {
            throw new InvalidOperationException("Version.txt trên server đang thay đổi. Bỏ qua cập nhật lần này.");
        }
    }

    private void BackupFilesToBeReplaced(string sourceFolder, string localFolder, string backupFolder)
    {
        foreach (var sourceFile in Directory.EnumerateFiles(sourceFolder, "*", SearchOption.AllDirectories))
        {
            if (ShouldSkipCopy(sourceFile, sourceFolder))
            {
                continue;
            }

            var relativePath = Path.GetRelativePath(sourceFolder, sourceFile);
            var localFile = Path.Combine(localFolder, relativePath);
            if (!File.Exists(localFile))
            {
                continue;
            }

            var backupFile = Path.Combine(backupFolder, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(backupFile)!);
            File.Copy(localFile, backupFile, overwrite: true);
        }
    }

    private void ReplaceFromTemp(string tempFolder, string localFolder, bool copyVersionLast)
    {
        var files = Directory
            .EnumerateFiles(tempFolder, "*", SearchOption.AllDirectories)
            .Where(file => !ShouldSkipCopy(file, tempFolder))
            .OrderBy(file => IsVersionFile(file, tempFolder) && copyVersionLast ? 1 : 0)
            .ToList();

        _progressBar.Maximum = Math.Max(files.Count, 1);
        _progressBar.Value = 0;

        for (var index = 0; index < files.Count; index++)
        {
            var sourceFile = files[index];
            var relativePath = Path.GetRelativePath(tempFolder, sourceFile);
            var targetFile = Path.Combine(localFolder, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(targetFile)!);
            File.Copy(sourceFile, targetFile, overwrite: true);
            _progressBar.Value = Math.Min(index + 1, _progressBar.Maximum);
            _lblStatus.Text = $"Đang sao chép tệp {index + 1}/{files.Count}";
            Application.DoEvents();
        }
    }

    private void Rollback(string localFolder, string backupFolder)
    {
        _logger.Info("Rollback started");
        if (!Directory.Exists(backupFolder))
        {
            return;
        }

        CopyDirectory(backupFolder, localFolder, skipVersionFile: false);
        _logger.Info("Rollback completed");
    }

    private static void CopyDirectory(string sourceFolder, string targetFolder, bool skipVersionFile)
    {
        foreach (var directory in Directory.EnumerateDirectories(sourceFolder, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(sourceFolder, directory);
            if (IsProtectedDirectory(relativePath))
            {
                continue;
            }

            Directory.CreateDirectory(Path.Combine(targetFolder, relativePath));
        }

        foreach (var sourceFile in Directory.EnumerateFiles(sourceFolder, "*", SearchOption.AllDirectories))
        {
            if (ShouldSkipCopy(sourceFile, sourceFolder))
            {
                continue;
            }

            if (skipVersionFile && IsVersionFile(sourceFile, sourceFolder))
            {
                continue;
            }

            var relativePath = Path.GetRelativePath(sourceFolder, sourceFile);
            var targetFile = Path.Combine(targetFolder, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(targetFile)!);
            File.Copy(sourceFile, targetFile, overwrite: true);
        }
    }

    private static bool ShouldSkipCopy(string filePath, string rootFolder)
    {
        var relativePath = Path.GetRelativePath(rootFolder, filePath);
        var fileName = Path.GetFileName(filePath);

        if (string.Equals(fileName, UpdateConfiguration.UpdaterExecutableName, StringComparison.OrdinalIgnoreCase)
            || fileName.StartsWith("Update.", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (IsProtectedDirectory(Path.GetDirectoryName(relativePath) ?? string.Empty))
        {
            return true;
        }

        return IsProtectedFile(relativePath);
    }

    private static bool IsVersionFile(string filePath, string rootFolder)
        => string.Equals(
            Path.GetRelativePath(rootFolder, filePath),
            UpdateConfiguration.VersionFileName,
            StringComparison.OrdinalIgnoreCase);

    private static bool IsProtectedDirectory(string relativeDirectory)
    {
        if (string.IsNullOrWhiteSpace(relativeDirectory) || relativeDirectory == ".")
        {
            return false;
        }

        var firstSegment = relativeDirectory
            .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .FirstOrDefault() ?? string.Empty;

        return PathComparer.Equals(firstSegment, "Logs")
            || PathComparer.Equals(firstSegment, "DebugAudio")
            || PathComparer.Equals(firstSegment, "_update_temp")
            || PathComparer.Equals(firstSegment, "_update_backup");
    }

    private static bool IsProtectedFile(string relativePath)
    {
        var fileName = Path.GetFileName(relativePath);
        if (PathComparer.Equals(fileName, "settings.json")
            || PathComparer.Equals(fileName, "user-settings.json")
            || PathComparer.Equals(fileName, "credentials.json")
            || PathComparer.Equals(fileName, "credential.json")
            || PathComparer.Equals(fileName, "service-account.json")
            || fileName.EndsWith(".key", StringComparison.OrdinalIgnoreCase)
            || fileName.EndsWith(".pem", StringComparison.OrdinalIgnoreCase)
            || fileName.EndsWith(".p12", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
            && !PathComparer.Equals(fileName, "MeetingInterpreter.deps.json")
            && !PathComparer.Equals(fileName, "MeetingInterpreter.runtimeconfig.json")
            && !PathComparer.Equals(fileName, "Update.deps.json")
            && !PathComparer.Equals(fileName, "Update.runtimeconfig.json")
            && !PathComparer.Equals(fileName, "speech-vocabulary.json"))
        {
            return true;
        }

        return false;
    }

    private void StartLocalOrShowError(string localExePath, string errorMessage)
    {
        if (File.Exists(localExePath))
        {
            StartMeetingInterpreter(localExePath);
            return;
        }

        MessageBox.Show(this, errorMessage, "Meeting Interpreter", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        Close();
    }

    private void StartMeetingInterpreter(string mainExePath)
    {
        _logger.Info("MeetingInterpreter started");
        Process.Start(new ProcessStartInfo
        {
            FileName = mainExePath,
            Arguments = "--skip-updater",
            WorkingDirectory = AppContext.BaseDirectory,
            UseShellExecute = true
        });

        Close();
    }

    private static bool IsMainAppRunning()
    {
        var currentProcessId = Environment.ProcessId;
        return Process.GetProcessesByName(Path.GetFileNameWithoutExtension(UpdateConfiguration.MainExecutableName))
            .Any(process =>
            {
                using (process)
                {
                    return process.Id != currentProcessId;
                }
            });
    }

    private void SetProgress(string status, ProgressBarStyle style)
    {
        _lblStatus.Text = status;
        _progressBar.Style = style;
        if (style == ProgressBarStyle.Marquee)
        {
            _progressBar.MarqueeAnimationSpeed = 20;
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
        }
    }
}
