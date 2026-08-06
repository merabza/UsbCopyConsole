using System;
using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace UsbCopyConsole.Receiver;

//სამუშაოს მიბმის ფაილის ({Destination}\.usbcopy\job.json) შენახვა/წაკითხვა/წაშლა
public static class ClientJobStore
{
    private const string BindingFileName = "job.json";

    private static string GetBindingPath(string destinationFolder)
    {
        return Path.Combine(destinationFolder, UsbCopyReceiverParameters.StagingFolderName, BindingFileName);
    }

    //ბრუნდება null, თუ მიბმა არ არსებობს ან სხვა სერვერს/პროექტს ეკუთვნის (ასეთი მიბმა იშლება)
    public static ClientJobBinding? TryLoad(UsbCopyReceiverParameters receiverParameters)
    {
        string bindingPath = GetBindingPath(receiverParameters.DestinationFolder);

        try
        {
            if (!File.Exists(bindingPath))
            {
                return null;
            }

            var binding = JsonSerializer.Deserialize<ClientJobBinding>(File.ReadAllText(bindingPath));
            if (binding is not null && !string.IsNullOrWhiteSpace(binding.JobId) &&
                string.Equals(binding.ServerAddress, receiverParameters.ServerAddress,
                    StringComparison.OrdinalIgnoreCase) && string.Equals(binding.RemoteProjectName,
                    receiverParameters.RemoteProjectName, StringComparison.Ordinal))
            {
                return binding;
            }

            File.Delete(bindingPath);
            return null;
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static void Save(ILogger logger, string destinationFolder, ClientJobBinding binding)
    {
        try
        {
            string bindingPath = GetBindingPath(destinationFolder);
            string? bindingDir = Path.GetDirectoryName(bindingPath);
            if (!string.IsNullOrEmpty(bindingDir))
            {
                Directory.CreateDirectory(bindingDir);
            }

            string tempPath = bindingPath + ".tmp";
            File.WriteAllText(tempPath, JsonSerializer.Serialize(binding));
            File.Move(tempPath, bindingPath, true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            //მიბმის შენახვის შეცდომა მიღებას არ აჩერებს — უბრალოდ ვერ გავაგრძელებთ იმავე სამუშაოს ხელახლა გაშვებისას
            logger.LogWarning(e, "Cannot save job binding in {DestinationFolder}", destinationFolder);
        }
    }

    public static void Delete(ILogger logger, string destinationFolder)
    {
        try
        {
            File.Delete(GetBindingPath(destinationFolder));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(e, "Cannot delete job binding in {DestinationFolder}", destinationFolder);
        }
    }
}
