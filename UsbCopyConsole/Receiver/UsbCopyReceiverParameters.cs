using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using AppCliTools.LibDataInput;
using Microsoft.Extensions.Logging;
using ParametersManagement.LibApiClientParameters;
using ParametersManagement.LibParameters;
using SystemTools.SystemToolsShared;
using UsbCopyConsole.Models;

namespace UsbCopyConsole.Receiver;

public sealed class UsbCopyReceiverParameters : IParameters
{
    //დროებითი ფაილების საქაღალდე დანიშნულების შიგნით და დაუსრულებელი ფაილების გაფართოება
    public const string StagingFolderName = ".usbcopy";
    public const string TempFileExtension = ".usbtmp";

    private const string FolderMask = "yyyyMMddHHmmss";

    //არაინტერაქტიული კონსტრუქტორი (მათ შორის ავტომატური ტესტებისთვის)
    // ReSharper disable once ConvertToPrimaryConstructor
    private UsbCopyReceiverParameters(string serverAddress, string? apiKey, string remoteProjectName,
        string destinationFolder)
    {
        ServerAddress = serverAddress;
        ApiKey = apiKey;
        RemoteProjectName = remoteProjectName;
        DestinationFolder = destinationFolder;
    }

    public string ServerAddress { get; }
    public string? ApiKey { get; }
    public string RemoteProjectName { get; }
    public string DestinationFolder { get; }

    public bool CheckBeforeSave()
    {
        return true;
    }

    internal static UsbCopyReceiverParameters? Create(ILogger logger, UsbCopyConsoleParameters parameters,
        string projectName)
    {
        UsbCopyConsoleProjectModel project = parameters.GetProjectRequired(projectName);

        //Check LocalPath
        if (string.IsNullOrWhiteSpace(project.LocalPath))
        {
            StShared.WriteErrorLine($"LocalPath does not specified for Project {projectName}", true, logger);
            return null;
        }

        //Check ApiClient
        if (string.IsNullOrWhiteSpace(project.ApiClientName))
        {
            StShared.WriteErrorLine($"ApiClientName does not specified for Project {projectName}", true, logger);
            return null;
        }

        ApiClientSettings? apiClientSettings = parameters.ApiClients.GetValueOrDefault(project.ApiClientName);
        if (apiClientSettings is null)
        {
            StShared.WriteErrorLine($"Api Client {project.ApiClientName} not found", true, logger);
            return null;
        }

        if (string.IsNullOrWhiteSpace(apiClientSettings.Server))
        {
            StShared.WriteErrorLine($"Server does not specified for Api Client {project.ApiClientName}", true, logger);
            return null;
        }

        string remoteProjectName = string.IsNullOrWhiteSpace(project.RemoteProjectName)
            ? projectName
            : project.RemoteProjectName;

        string? localPathChecked = FileStat.CreateFolderIfNotExists(project.LocalPath, true, logger);
        if (localPathChecked == null)
        {
            StShared.WriteErrorLine($"local path {project.LocalPath} can not be created", true, logger);
            return null;
        }

        string? mainFolder = ChooseDestinationFolder(logger, project.LocalPath);
        if (mainFolder is null)
        {
            return null;
        }

        return new UsbCopyReceiverParameters(apiClientSettings.Server.TrimEnd('/'), apiClientSettings.ApiKey,
            remoteProjectName, mainFolder);
    }

    //დანიშნულების ქვესაქაღალდის არჩევა: ბოლო არსებულის გაგრძელება დადასტურებით ან ახლის შექმნა მიმდინარე დროით
    private static string? ChooseDestinationFolder(ILogger logger, string localPath)
    {
        List<BuFileInfo> buFileInfos = [];

        foreach (string folderPath in Directory.GetDirectories(localPath))
        {
            string folderName = Path.GetFileName(folderPath);
            (DateTime dateTimeByDigits, string? pattern) = folderName.GetDateTimeAndPatternByDigits(FolderMask);
            if (pattern is not null)
            {
                buFileInfos.Add(new BuFileInfo(folderName, dateTimeByDigits));
            }
        }

        BuFileInfo? lastFolderName = null;
        if (buFileInfos.Count > 0)
        {
            lastFolderName = buFileInfos.MaxBy(ob => ob.FileDateTime);
        }

        if (lastFolderName != null &&
            !Inputer.InputBool($"Continue with existing folder {lastFolderName.FileName}", false, false))
        {
            lastFolderName = null;
        }

        string mainFolderName = lastFolderName is null
            ? DateTime.Now.ToString(FolderMask, CultureInfo.InvariantCulture)
            : lastFolderName.FileName;
        string mainFolderFullPath = Path.Combine(localPath, mainFolderName);

        string? mainFolder = FileStat.CreateFolderIfNotExists(mainFolderFullPath, true, logger);
        if (mainFolder is null)
        {
            StShared.WriteErrorLine($"Main folder path {mainFolderFullPath} can not be created", true, logger);
        }

        return mainFolder;
    }

    //დანიშნულების საქაღალდეში უკვე არსებული ფაილების სია ('/' გამყოფით), რომ სერვისმა ისინი აღარ გადმოგზავნოს.
    //internal-ია, რადგან ყოველი ახალი StartJob-ის წინ სია თავიდან უნდა აიგოს
    internal static string[] ScanExistingFiles(string destinationFolder)
    {
        List<string> result =
        [
            .. from filePath in Directory.EnumerateFiles(destinationFolder, "*", SearchOption.AllDirectories)
            select Path.GetRelativePath(destinationFolder, filePath)
            into relativePath
            where !relativePath.StartsWith(StagingFolderName + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            where !relativePath.EndsWith(TempFileExtension, StringComparison.OrdinalIgnoreCase)
            select relativePath.Replace(Path.DirectorySeparatorChar, '/')
        ];

        return [.. result];
    }
}
