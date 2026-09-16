using MeetingInterpreter.Services;

const int BufferBytes = 1600; // 50 ms of 16 kHz, 16-bit mono PCM.

var detector = new DigitalSilenceDetector(
    sampleRate: 16000,
    muteDetectionMilliseconds: 350,
    resumeDetectionMilliseconds: 100);
var zero = new byte[BufferBytes];
var analogNoise = CreateConstantPcmBuffer(sample: 2);
var speech = CreateConstantPcmBuffer(sample: 32);

for (var index = 0; index < 20; index++)
{
    AssertEqual(DigitalSilenceTransition.None, detector.ProcessBuffer(analogNoise, analogNoise.Length));
}
AssertEqual(false, detector.IsPhysicallyMuted);

detector.Reset();
for (var index = 0; index < 6; index++)
{
    AssertEqual(DigitalSilenceTransition.None, detector.ProcessBuffer(zero, zero.Length));
}
AssertEqual(DigitalSilenceTransition.Muted, detector.ProcessBuffer(zero, zero.Length));
AssertEqual(true, detector.IsPhysicallyMuted);

AssertEqual(DigitalSilenceTransition.None, detector.ProcessBuffer(speech, speech.Length));
AssertEqual(DigitalSilenceTransition.Resumed, detector.ProcessBuffer(speech, speech.Length));
AssertEqual(false, detector.IsPhysicallyMuted);

Console.WriteLine("PASS: analog noise ignored; digital silence detected at 350 ms; signal resumed at 100 ms.");

static byte[] CreateConstantPcmBuffer(short sample)
{
    var buffer = new byte[BufferBytes];
    for (var offset = 0; offset < buffer.Length; offset += 2)
    {
        buffer[offset] = (byte)(sample & 0xff);
        buffer[offset + 1] = (byte)((sample >> 8) & 0xff);
    }

    return buffer;
}

static void AssertEqual<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
    {
        throw new InvalidOperationException($"Expected {expected}, got {actual}.");
    }
}
