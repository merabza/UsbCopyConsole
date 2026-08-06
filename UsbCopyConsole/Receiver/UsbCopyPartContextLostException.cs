using System;

namespace UsbCopyConsole.Receiver;

//დიდი ფაილის წინა ნაწილები ლოკალურად დაკარგულია — ამ სამუშაოში ფაილის სწორად აწყობა ვეღარ მოხერხდება,
//პაკეტი დაუყოვნებლივ უნდა უარვყოთ, რომ ფაილი ახალმა სამუშაომ თავიდან გამოგზავნოს
public sealed class UsbCopyPartContextLostException : Exception
{
    // ReSharper disable once ConvertToPrimaryConstructor
    public UsbCopyPartContextLostException(string message) : base(message)
    {
    }
}
