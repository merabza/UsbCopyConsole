using System;

namespace UsbCopyConsole.Receiver;

//პაკეტის მიღების/აღდგენის შეცდომა, რომელზეც ხელახლა ცდა აზრიანია
public sealed class UsbCopyReceiverException : Exception
{
    public UsbCopyReceiverException()
    {
    }

    // ReSharper disable once ConvertToPrimaryConstructor
    public UsbCopyReceiverException(string message) : base(message)
    {
    }

    public UsbCopyReceiverException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
