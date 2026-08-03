using System;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;
using SystemTools.SystemToolsShared;
using UsbCopyServiceShared.Contracts;
using UsbCopyServiceShared.Contracts.V1.Routes;

namespace UsbCopyConsole.Receiver;

//სერვისიდან პაკეტების მიღება, შემოწმება და საწყისი მდგომარეობის აღდგენა დანიშნულების საქაღალდეში
public sealed class PackageReceiver
{
    private const int CopyBufferSize = 81920;
    private const int MaxDownloadAttempts = 3;

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger _logger;
    private readonly UsbCopyReceiverParameters _receiverParameters;

    // ReSharper disable once ConvertToPrimaryConstructor
    public PackageReceiver(ILogger logger, UsbCopyReceiverParameters receiverParameters,
        IHttpClientFactory httpClientFactory)
    {
        _logger = logger;
        _receiverParameters = receiverParameters;
        _httpClientFactory = httpClientFactory;
    }

    public async Task<bool> RunAsync(CancellationToken cancellationToken = default)
    {
        Channel<ReceivedEvent> channel = Channel.CreateUnbounded<ReceivedEvent>();

        HubConnection hubConnection = new HubConnectionBuilder().WithUrl(BuildHubAddress()).Build();
        try
        {
            RegisterHandlers(hubConnection, channel.Writer);

            await hubConnection.StartAsync(cancellationToken);

            string jobId = await hubConnection.InvokeAsync<string>(UsbCopyHubEvents.StartJob,
                _receiverParameters.RemoteProjectName, _receiverParameters.ExistingFiles, cancellationToken);
            Console.WriteLine($"Job {jobId} started on service side");

            return await ProcessEvents(hubConnection, channel.Reader, jobId, cancellationToken);
        }
        finally
        {
            await hubConnection.DisposeAsync();
            CleanupTempArtifacts();
        }
    }

    private void RegisterHandlers(HubConnection hubConnection, ChannelWriter<ReceivedEvent> writer)
    {
        //ჰენდლერები სინქრონულია და მხოლოდ რიგში წერენ — ჰაბის მეთოდების გამოძახება მხოლოდ რიგის დამმუშავებლიდან ხდება
        hubConnection.On<string>(UsbCopyHubEvents.ReceiveProgress, Console.WriteLine);

        hubConnection.On<string>(UsbCopyHubEvents.ReceivePackageReady,
            manifestJson => writer.TryWrite(new ReceivedEvent(EReceivedEventKind.PackageReady, manifestJson)));

        hubConnection.On<string>(UsbCopyHubEvents.ReceiveJobCompleted, summaryJson =>
        {
            writer.TryWrite(new ReceivedEvent(EReceivedEventKind.JobCompleted, summaryJson));
            writer.TryComplete();
        });

        hubConnection.On<string>(UsbCopyHubEvents.ReceiveJobFailed, errorMessage =>
        {
            writer.TryWrite(new ReceivedEvent(EReceivedEventKind.JobFailed, errorMessage));
            writer.TryComplete();
        });

        hubConnection.Closed += _ =>
        {
            writer.TryWrite(new ReceivedEvent(EReceivedEventKind.ConnectionClosed, null));
            writer.TryComplete();
            return Task.CompletedTask;
        };
    }

    private async Task<bool> ProcessEvents(HubConnection hubConnection, ChannelReader<ReceivedEvent> reader,
        string jobId, CancellationToken cancellationToken)
    {
        var success = false;

        await foreach (ReceivedEvent receivedEvent in reader.ReadAllAsync(cancellationToken))
        {
            switch (receivedEvent.Kind)
            {
                case EReceivedEventKind.PackageReady:
                    if (!await ProcessPackage(hubConnection, jobId, receivedEvent.Payload, cancellationToken))
                    {
                        return false;
                    }

                    break;
                case EReceivedEventKind.JobCompleted:
                    PrintSummary(receivedEvent.Payload);
                    success = true;
                    break;
                case EReceivedEventKind.JobFailed:
                    StShared.WriteErrorLine($"Job failed on service side: {receivedEvent.Payload}", true, _logger);
                    break;
                case EReceivedEventKind.ConnectionClosed:
                    if (!success)
                    {
                        StShared.WriteErrorLine("Connection to service lost", true, _logger);
                    }

                    break;
            }
        }

        return success;
    }

    private async Task<bool> ProcessPackage(HubConnection hubConnection, string jobId, string? manifestJson,
        CancellationToken cancellationToken)
    {
        PackageManifest? manifest = manifestJson is null ? null : JsonSerializer.Deserialize<PackageManifest>(manifestJson);
        if (manifest is null)
        {
            await SendAck(hubConnection, jobId, string.Empty, false, "Invalid package manifest", cancellationToken);
            return false;
        }

        Console.WriteLine(
            $"Receiving package {manifest.PackageIndex + 1}/{manifest.PackagesTotal} ({manifest.PackageType}, {manifest.TransferSize} bytes)");

        string? error = null;
        for (var attempt = 1; attempt <= MaxDownloadAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                await DownloadAndRestore(manifest, cancellationToken);
                error = null;
                break;
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                error = e.Message;
                _logger.LogWarning(e, "Package {PackageId} attempt {Attempt} failed", manifest.PackageId, attempt);
            }
        }

        bool ok = error is null;
        await SendAck(hubConnection, jobId, manifest.PackageId, ok, error, cancellationToken);

        if (!ok)
        {
            StShared.WriteErrorLine($"Package {manifest.PackageId} receive failed: {error}", true, _logger);
        }

        return ok;
    }

    private Task DownloadAndRestore(PackageManifest manifest, CancellationToken cancellationToken)
    {
        return manifest.PackageType switch
        {
            EPackageType.Archive => RestoreArchive(manifest, cancellationToken),
            EPackageType.WholeFile => RestoreWholeFile(manifest, cancellationToken),
            EPackageType.FilePart => RestoreFilePart(manifest, cancellationToken),
            _ => throw new UsbCopyReceiverException($"Unknown package type {manifest.PackageType}")
        };
    }

    //პატარა ფაილების არქივი: ჩამოტვირთვა staging საქაღალდეში, შემოწმება, განარქივება დანიშნულების ძირში
    private async Task RestoreArchive(PackageManifest manifest, CancellationToken cancellationToken)
    {
        string stagingDir = Path.Combine(_receiverParameters.DestinationFolder,
            UsbCopyReceiverParameters.StagingFolderName);
        Directory.CreateDirectory(stagingDir);

        string zipPath = Path.Combine(stagingDir, manifest.PackageId + ".zip");

        // ReSharper disable once using
        // ReSharper disable once DisposableConstructor
        await using var zipFileStream = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None, CopyBufferSize,
            true);
        await using (zipFileStream.ConfigureAwait(false))
        {
            await DownloadToStream(manifest, zipFileStream, cancellationToken);
        }

        await ZipFile.ExtractToDirectoryAsync(zipPath, _receiverParameters.DestinationFolder, true, cancellationToken);
        File.Delete(zipPath);
    }

    //საშუალო ფაილი: ჩამოტვირთვა დროებითი სახელით და საბოლოო სახელზე გადარქმევა
    private async Task RestoreWholeFile(PackageManifest manifest, CancellationToken cancellationToken)
    {
        (string targetPath, string tempPath) = GetTargetPaths(manifest);

        // ReSharper disable once using
        // ReSharper disable once DisposableConstructor
        await using var fileStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, CopyBufferSize,
            true);
        await using (fileStream.ConfigureAwait(false))
        {
            await DownloadToStream(manifest, fileStream, cancellationToken);
        }

        File.Move(tempPath, targetPath, true);
    }

    //დიდი ფაილის ნაწილი: ჩაწერა დროებით ფაილში თავის ადგილზე; ბოლო ნაწილის შემდეგ ფაილი საბოლოო სახელს იღებს
    private async Task RestoreFilePart(PackageManifest manifest, CancellationToken cancellationToken)
    {
        if (manifest.FileSize is null || manifest.PartOffset is null || manifest.PartIndex is null ||
            manifest.PartsTotal is null)
        {
            throw new UsbCopyReceiverException($"Invalid FilePart manifest for package {manifest.PackageId}");
        }

        (string targetPath, string tempPath) = GetTargetPaths(manifest);

        // ReSharper disable once using
        // ReSharper disable once DisposableConstructor
        await using var fileStream = new FileStream(tempPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None,
            CopyBufferSize, true);
        await using (fileStream.ConfigureAwait(false))
        {
            if (fileStream.Length != manifest.FileSize.Value)
            {
                fileStream.SetLength(manifest.FileSize.Value);
            }

            fileStream.Seek(manifest.PartOffset.Value, SeekOrigin.Begin);
            await DownloadToStream(manifest, fileStream, cancellationToken);
        }

        if (manifest.PartIndex.Value == manifest.PartsTotal.Value - 1)
        {
            File.Move(tempPath, targetPath, true);
        }
    }

    private (string TargetPath, string TempPath) GetTargetPaths(PackageManifest manifest)
    {
        if (string.IsNullOrWhiteSpace(manifest.RelativePath))
        {
            throw new UsbCopyReceiverException($"RelativePath is missing in manifest for package {manifest.PackageId}");
        }

        string relativePath = manifest.RelativePath.Replace('/', Path.DirectorySeparatorChar);
        string targetPath = Path.GetFullPath(Path.Combine(_receiverParameters.DestinationFolder, relativePath));

        //დაცვა: ფაილი დანიშნულების საქაღალდის გარეთ არ უნდა აღმოჩნდეს
        if (!targetPath.StartsWith(Path.GetFullPath(_receiverParameters.DestinationFolder),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new UsbCopyReceiverException($"Invalid relative path {manifest.RelativePath}");
        }

        string? targetDir = Path.GetDirectoryName(targetPath);
        if (!string.IsNullOrEmpty(targetDir))
        {
            Directory.CreateDirectory(targetDir);
        }

        return (targetPath, targetPath + UsbCopyReceiverParameters.TempFileExtension);
    }

    //პაკეტის ბაიტების ჩამოტვირთვა HTTP ნაკადით, ზომისა და SHA256-ის შემოწმებით
    private async Task DownloadToStream(PackageManifest manifest, Stream targetStream,
        CancellationToken cancellationToken)
    {
        // ReSharper disable once using
        using HttpClient httpClient = _httpClientFactory.CreateClient();
        httpClient.Timeout = Timeout.InfiniteTimeSpan;

        // ReSharper disable once using
        using HttpResponseMessage response = await httpClient.GetAsync(
            new Uri(BuildDownloadAddress(manifest.JobId, manifest.PackageId)),
            HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new UsbCopyReceiverException($"Download failed with status code {(int)response.StatusCode}");
        }

        // ReSharper disable once using
        await using Stream bodyStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using (bodyStream.ConfigureAwait(false))
        {
            // ReSharper disable once using
            using var incrementalHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[CopyBufferSize];
            long totalRead = 0;

            int read;
            while ((read = await bodyStream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)) > 0)
            {
                incrementalHash.AppendData(buffer, 0, read);
                await targetStream.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                totalRead += read;
            }

            await targetStream.FlushAsync(cancellationToken);

            if (totalRead != manifest.TransferSize)
            {
                throw new UsbCopyReceiverException(
                    $"Downloaded size {totalRead} does not match expected {manifest.TransferSize}");
            }

            string sha256 = Convert.ToHexString(incrementalHash.GetHashAndReset());
            if (!string.Equals(sha256, manifest.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new UsbCopyReceiverException("Downloaded content hash does not match manifest");
            }
        }
    }

    private static Task SendAck(HubConnection hubConnection, string jobId, string packageId, bool ok, string? error,
        CancellationToken cancellationToken)
    {
        return hubConnection.InvokeAsync(UsbCopyHubEvents.AckPackage, jobId, packageId, ok, error, cancellationToken);
    }

    private static void PrintSummary(string? summaryJson)
    {
        JobSummary? summary = summaryJson is null ? null : JsonSerializer.Deserialize<JobSummary>(summaryJson);
        if (summary is null)
        {
            Console.WriteLine("Job completed");
            return;
        }

        Console.WriteLine("Job completed:");
        Console.WriteLine($"  Files: {summary.FilesTotal} (skipped existing: {summary.FilesSkipped})");
        Console.WriteLine($"  Bytes: {summary.BytesOriginal} original, {summary.BytesTransferred} transferred");
        Console.WriteLine($"  Packages: {summary.PackagesTotal}");
        Console.WriteLine($"  Elapsed: {summary.ElapsedSeconds:F1} seconds");
    }

    private string BuildHubAddress()
    {
        return _receiverParameters.ServerAddress + UsbCopyApiRoutes.UsbCopyRoute.UsbCopyBase +
               UsbCopyApiRoutes.UsbCopyRoute.Hub + BuildApiKeyQuery();
    }

    private string BuildDownloadAddress(string jobId, string packageId)
    {
        return _receiverParameters.ServerAddress + UsbCopyApiRoutes.UsbCopyRoute.UsbCopyBase +
               UsbCopyApiRoutes.UsbCopyRoute.Download + "/" + jobId + "/" + packageId + BuildApiKeyQuery();
    }

    private string BuildApiKeyQuery()
    {
        return string.IsNullOrWhiteSpace(_receiverParameters.ApiKey)
            ? string.Empty
            : "?apikey=" + Uri.EscapeDataString(_receiverParameters.ApiKey);
    }

    //წარუმატებლობის შემთხვევაში დარჩენილი დროებითი ფაილების წაშლა
    private void CleanupTempArtifacts()
    {
        try
        {
            string stagingDir = Path.Combine(_receiverParameters.DestinationFolder,
                UsbCopyReceiverParameters.StagingFolderName);
            if (Directory.Exists(stagingDir))
            {
                Directory.Delete(stagingDir, true);
            }

            foreach (string tempFile in Directory.EnumerateFiles(_receiverParameters.DestinationFolder,
                         "*" + UsbCopyReceiverParameters.TempFileExtension, SearchOption.AllDirectories))
            {
                File.Delete(tempFile);
            }
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "Cannot cleanup temporary receiver artifacts");
        }
    }
}
