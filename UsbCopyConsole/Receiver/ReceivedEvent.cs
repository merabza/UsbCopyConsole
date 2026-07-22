namespace UsbCopyConsole.Receiver;

public enum EReceivedEventKind
{
    PackageReady,
    JobCompleted,
    JobFailed,
    ConnectionClosed
}

//სერვისიდან მიღებული შეტყობინება, რომელსაც ერთი დამმუშავებელი რიგით ამუშავებს
public sealed class ReceivedEvent
{
    // ReSharper disable once ConvertToPrimaryConstructor
    public ReceivedEvent(EReceivedEventKind kind, string? payload)
    {
        Kind = kind;
        Payload = payload;
    }

    public EReceivedEventKind Kind { get; }
    public string? Payload { get; }
}
