using SystemTools.SystemToolsShared;

namespace UsbCopyConsole.Models;

public sealed class UsbCopyConsoleProjectModel : ItemData
{
    //ლოკალური საქაღალდე, სადაც უნდა შეინახოს მიღებული ფაილები
    public string? LocalPath { get; set; }

    //სერვისის მისამართის და გასაღების ჩანაწერის სახელი ApiClients ლექსიკონში
    public string? ApiClientName { get; set; }

    //პროექტის სახელი სერვისის მხარეს
    public string? RemoteProjectName { get; set; }
}
