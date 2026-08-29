namespace MeetingInterpreter.Models;

public sealed class AudioDeviceInfo
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public int DeviceNumber { get; set; }

    public override string ToString() => Name;
}
