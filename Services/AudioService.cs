using MeetingInterpreter.Models;
using NAudio.Wave;

namespace MeetingInterpreter.Services;

public sealed class AudioService : IDisposable
{
    private readonly AppLogger _logger;
    private readonly object _playbackSyncRoot = new();
    private WaveInEvent? _waveIn;
    private WaveOutEvent? _activeOutput;
    private TaskCompletionSource? _activePlaybackCompletion;
    private WaveOutEvent? _streamingOutput;
    private BufferedWaveProvider? _streamingProvider;

    public AudioService(AppLogger logger)
    {
        _logger = logger;
    }

    public event EventHandler<WaveInEventArgs>? AudioDataAvailable;

    public IReadOnlyList<AudioDeviceInfo> EnumerateInputDevices()
    {
        var devices = new List<AudioDeviceInfo>();

        for (var i = 0; i < WaveIn.DeviceCount; i++)
        {
            var capabilities = WaveIn.GetCapabilities(i);
            devices.Add(new AudioDeviceInfo
            {
                DeviceNumber = i,
                Name = capabilities.ProductName,
                Id = $"wavein:{i}:{capabilities.ProductGuid}:{capabilities.ProductName}"
            });
        }

        return devices;
    }

    public IReadOnlyList<AudioDeviceInfo> EnumerateOutputDevices()
    {
        var devices = new List<AudioDeviceInfo>();

        for (var i = 0; i < WaveOut.DeviceCount; i++)
        {
            var capabilities = WaveOut.GetCapabilities(i);
            devices.Add(new AudioDeviceInfo
            {
                DeviceNumber = i,
                Name = capabilities.ProductName,
                Id = $"waveout:{i}:{capabilities.ProductGuid}:{capabilities.ProductName}"
            });
        }

        return devices;
    }

    public void StartCapture(AudioDeviceInfo inputDevice)
    {
        StopCapture();

        _waveIn = new WaveInEvent
        {
            DeviceNumber = inputDevice.DeviceNumber,
            WaveFormat = new WaveFormat(rate: 16000, bits: 16, channels: 1),
            BufferMilliseconds = 50
        };

        _waveIn.DataAvailable += OnDataAvailable;
        _waveIn.StartRecording();
        _logger.Info($"Đã chọn microphone: {inputDevice.Name}");
    }

    public void StopCapture()
    {
        if (_waveIn is null)
        {
            return;
        }

        try
        {
            _waveIn.DataAvailable -= OnDataAvailable;
            _waveIn.StopRecording();
        }
        finally
        {
            _waveIn.Dispose();
            _waveIn = null;
        }
    }

    public async Task PlayAudioAsync(
        byte[] audioData,
        AudioDeviceInfo outputDevice,
        CancellationToken cancellationToken)
    {
        if (!EnumerateOutputDevices().Any(device => device.DeviceNumber == outputDevice.DeviceNumber && device.Id == outputDevice.Id))
        {
            throw new InvalidOperationException("Không tìm thấy thiết bị âm thanh đã chọn.");
        }

        using var memoryStream = new MemoryStream(audioData, writable: false);
        using var waveStream = CreateWaveStream(memoryStream);
        using var output = new WaveOutEvent
        {
            DeviceNumber = outputDevice.DeviceNumber
        };

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        output.PlaybackStopped += (_, args) =>
        {
            if (args.Exception is not null)
            {
                completion.TrySetException(args.Exception);
            }
            else
            {
                completion.TrySetResult();
            }
        };

        using var registration = cancellationToken.Register(() =>
        {
            output.Stop();
            completion.TrySetCanceled(cancellationToken);
        });

        lock (_playbackSyncRoot)
        {
            _activeOutput = output;
            _activePlaybackCompletion = completion;
        }

        try
        {
            output.Init(waveStream);
            _logger.Info($"Bắt đầu phát âm thanh: {outputDevice.Name}");
            output.Play();
            await completion.Task.ConfigureAwait(false);
            _logger.Info($"Phát âm thanh hoàn tất: {outputDevice.Name}");
        }
        finally
        {
            lock (_playbackSyncRoot)
            {
                if (ReferenceEquals(_activeOutput, output))
                {
                    _activeOutput = null;
                    _activePlaybackCompletion = null;
                }
            }
        }
    }

    public void StopActivePlayback()
    {
        lock (_playbackSyncRoot)
        {
            _activeOutput?.Stop();
            _activePlaybackCompletion?.TrySetCanceled();
        }

        StopPcmStreamPlayback();
    }

    public void StartPcmStreamPlayback(AudioDeviceInfo outputDevice, int sampleRate)
    {
        if (!EnumerateOutputDevices().Any(device => device.DeviceNumber == outputDevice.DeviceNumber && device.Id == outputDevice.Id))
        {
            throw new InvalidOperationException("Không tìm thấy thiết bị âm thanh đã chọn.");
        }

        StopPcmStreamPlayback();

        lock (_playbackSyncRoot)
        {
            _streamingProvider = new BufferedWaveProvider(new WaveFormat(sampleRate, 16, 1))
            {
                BufferDuration = TimeSpan.FromSeconds(8),
                DiscardOnBufferOverflow = true
            };
            _streamingOutput = new WaveOutEvent
            {
                DeviceNumber = outputDevice.DeviceNumber
            };
            _streamingOutput.Init(_streamingProvider);
            _streamingOutput.Play();
        }

        _logger.Info($"Bắt đầu phát audio streaming: {outputDevice.Name}");
    }

    public void AddPcmPlaybackData(byte[] audioData)
    {
        lock (_playbackSyncRoot)
        {
            _streamingProvider?.AddSamples(audioData, 0, audioData.Length);
        }
    }

    public void StopPcmStreamPlayback()
    {
        lock (_playbackSyncRoot)
        {
            _streamingOutput?.Stop();
            _streamingOutput?.Dispose();
            _streamingOutput = null;
            _streamingProvider = null;
        }
    }

    public async Task PlayToneAsync(AudioDeviceInfo outputDevice, CancellationToken cancellationToken)
    {
        var audioData = CreateToneWave();
        await PlayAudioAsync(audioData, outputDevice, cancellationToken).ConfigureAwait(false);
    }

    public void Dispose()
    {
        StopActivePlayback();
        StopCapture();
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
        => AudioDataAvailable?.Invoke(this, e);

    private static WaveStream CreateWaveStream(Stream stream)
    {
        try
        {
            return new WaveFileReader(stream);
        }
        catch (FormatException)
        {
            stream.Position = 0;
            return new RawSourceWaveStream(stream, new WaveFormat(rate: 16000, bits: 16, channels: 1));
        }
    }

    private static byte[] CreateToneWave()
    {
        const int sampleRate = 16000;
        const int durationMs = 700;
        const int samples = sampleRate * durationMs / 1000;
        var format = new WaveFormat(sampleRate, 16, 1);

        using var memoryStream = new MemoryStream();
        using (var writer = new WaveFileWriter(memoryStream, format))
        {
            for (var i = 0; i < samples; i++)
            {
                var value = (short)(Math.Sin(2 * Math.PI * 880 * i / sampleRate) * short.MaxValue * 0.22);
                writer.WriteByte((byte)(value & 0xff));
                writer.WriteByte((byte)((value >> 8) & 0xff));
            }
        }

        return memoryStream.ToArray();
    }
}
