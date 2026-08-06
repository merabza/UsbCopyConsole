using System;

namespace UsbCopyConsole.Receiver;

//მიმდინარე სამუშაოს მიბმა დანიშნულების საქაღალდესთან — ინახება დისკზე, რომ წყვეტის შემდეგ ResumeJob შევძლოთ
public sealed class ClientJobBinding
{
    public string JobId { get; set; } = string.Empty;

    public string ServerAddress { get; set; } = string.Empty;

    public string RemoteProjectName { get; set; } = string.Empty;

    public DateTime CreatedAtUtc { get; set; }
}
