using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;
using SystemTools.SystemToolsShared;
using UsbCopyServiceShared.Contracts;
using UsbCopyServiceShared.Contracts.V1.Routes;

namespace UsbCopyConsole.Receiver;

//სერვისიდან პაკეტების მიღება, შემოწმება და საწყისი მდგომარეობის აღდგენა დანიშნულების საქაღალდეში.
//კავშირის წყვეტისას თავად ცდილობს ხელახლა დაკავშირებას და სამუშაოს გაგრძელებას (ResumeJob), სანამ მომხმარებელი Esc-ით არ გააჩერებს
public sealed class PackageReceiver
{
    private const int CopyBufferSize = 81920;

    //შიდა ცდები ერთი პაკეტის ჩამოსატვირთად ერთ კავშირზე
    private const int MaxDownloadAttempts = 3;

    //რამდენი reconnect-ციკლის შემდეგ ჩაითვალოს ერთი და იმავე პაკეტის ჩავარდნა სისტემურად (ack=false)
    private const int MaxPackageFailureCycles = 3;

    //გაჩერებული ჩამოტვირთვის ამოცნობის ვადა
    private const int DownloadIdleTimeoutSeconds = 60;

    private static readonly int[] RetryDelaysSeconds = [5, 10, 20, 30];

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger _logger;

    //ჩავარდნების მთვლელი პაკეტის ინდექსის მიხედვით (PackageId ყოველ ხელახლა შეთავაზებაზე იცვლება)
    private readonly Dictionary<int, int> _packageIndexFailures = [];
    private readonly UsbCopyReceiverParameters _receiverParameters;

    private volatile bool _watcherStopRequested;

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
        //linkedCts ხელით იხურება finally-ში, მეთვალყურის დასრულების შემდეგ
        // ReSharper disable once DisposableConstructor
        // ReSharper disable once using
        var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bool watcherActive = !Console.IsInputRedirected;
        // ReSharper disable once using
        Task escapeWatcher = watcherActive ? RunEscapeWatcher(linkedCts) : Task.CompletedTask;
        if (watcherActive)
        {
            Console.WriteLine("Press Esc to stop receiving. Progress is saved and can be resumed later.");
        }

        try
        {
            var delayIndex = 0;
            while (true)
            {
                (EAttemptOutcome outcome, int packagesAcked) = await RunSingleAttemptAsync(linkedCts.Token);

                switch (outcome)
                {
                    case EAttemptOutcome.Completed:
                        CleanupAllTempArtifacts();
                        return true;
                    case EAttemptOutcome.Fatal:
                        //job.json და *.usbtmp რჩება — ხელახლა გაშვება იმავე ადგილიდან გააგრძელებს
                        CleanupZipStaging();
                        return false;
                    case EAttemptOutcome.Retry:
                        break;
                    default:
                        throw new SwitchExpressionException();
                }

                //პროგრესის შემთხვევაში დაყოვნება თავიდან იწყება
                if (packagesAcked > 0)
                {
                    delayIndex = 0;
                }

                int delaySeconds = RetryDelaysSeconds[Math.Min(delayIndex, RetryDelaysSeconds.Length - 1)];
                delayIndex++;

                CleanupZipStaging();
                Console.WriteLine($"Connection to service lost. Retrying in {delaySeconds}s (Esc to stop)...");
                await Task.Delay(TimeSpan.FromSeconds(delaySeconds), linkedCts.Token);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            //Esc-ით შეჩერება
            Console.WriteLine("Stopped by user. Run the command again to continue from the same point.");
            CleanupZipStaging();
            return false;
        }
        finally
        {
            _watcherStopRequested = true;
            try
            {
                await escapeWatcher;
            }
            catch (OperationCanceledException)
            {
                //გარე გაუქმებისას მეთვალყურის ამოცანა Canceled მდგომარეობით სრულდება — ეს შტატური დასასრულია
            }

            linkedCts.Dispose();
        }
    }

    //Esc კლავიშის მეთვალყურე: მიღების პროცესში ყველა კლავიშს შთანთქავს, რომ მენიუში არ გაჟონოს
    private Task RunEscapeWatcher(CancellationTokenSource linkedCts)
    {
        return Task.Run(async () =>
        {
            while (!_watcherStopRequested && !linkedCts.IsCancellationRequested)
            {
                try
                {
                    while (Console.KeyAvailable)
                    {
                        ConsoleKeyInfo keyInfo = Console.ReadKey(true);
                        if (keyInfo.Key != ConsoleKey.Escape)
                        {
                            continue;
                        }

                        Console.WriteLine();
                        Console.WriteLine("Stopping...");
                        await linkedCts.CancelAsync();
                        return;
                    }
                }
                catch (InvalidOperationException)
                {
                    //კონსოლის შეყვანა მიუწვდომელია — მეთვალყურის გარეშე ვაგრძელებთ
                    return;
                }

                await Task.Delay(150, linkedCts.Token);
            }
        }, linkedCts.Token);
    }

    //ერთი სრული ცდა: კავშირი, სამუშაოს გაგრძელება ან დაწყება, პაკეტების მიღება კავშირის დაკარგვამდე ან დასრულებამდე
    private async Task<(EAttemptOutcome Outcome, int PackagesAcked)> RunSingleAttemptAsync(
        CancellationToken cancellationToken)
    {
        var packagesAcked = 0;
        var channel = Channel.CreateUnbounded<ReceivedEvent>();

        HubConnection hubConnection = new HubConnectionBuilder().WithUrl(BuildHubAddress()).Build();
        try
        {
            RegisterHandlers(hubConnection, channel.Writer);

            try
            {
                await hubConnection.StartAsync(cancellationToken);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                _logger.LogWarning(e, "Cannot connect to service");
                Console.WriteLine($"Cannot connect to service: {e.Message}");
                return (EAttemptOutcome.Retry, 0);
            }

            string? jobId = null;

            ClientJobBinding? binding = ClientJobStore.TryLoad(_receiverParameters);
            if (binding is not null)
            {
                (jobId, EAttemptOutcome? resumeOutcome) = await TryResumeJob(hubConnection, binding, cancellationToken);
                if (resumeOutcome is not null)
                {
                    return (resumeOutcome.Value, 0);
                }
            }

            if (jobId is null)
            {
                (jobId, EAttemptOutcome? startOutcome) = await TryStartJob(hubConnection, cancellationToken);
                if (startOutcome is not null)
                {
                    return (startOutcome.Value, 0);
                }
            }

            EAttemptOutcome outcome = await ProcessEvents(hubConnection, channel.Reader, jobId!, () => packagesAcked++,
                cancellationToken);
            return (outcome, packagesAcked);
        }
        finally
        {
            await hubConnection.DisposeAsync();
        }
    }

    //არსებული სამუშაოს გაგრძელების მცდელობა job.json-ში შენახული იდენტიფიკატორით
    private async Task<(string? JobId, EAttemptOutcome? Outcome)> TryResumeJob(HubConnection hubConnection,
        ClientJobBinding binding, CancellationToken cancellationToken)
    {
        string resultJson;
        try
        {
            resultJson = await hubConnection.InvokeAsync<string>(UsbCopyHubEvents.ResumeJob, binding.JobId,
                cancellationToken);
        }
        catch (HubException e)
        {
            //სერვისმა ResumeJob ვერ დაამუშავა (მაგალითად, ძველი ვერსიაა) — ახალი სამუშაოთი გავაგრძელოთ
            _logger.LogWarning(e, "ResumeJob rejected by service, falling back to a new job");
            return (null, null);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _logger.LogWarning(e, "ResumeJob transport failure");
            return (null, EAttemptOutcome.Retry);
        }

        var result = JsonSerializer.Deserialize<ResumeJobResult>(resultJson);
        if (result is null)
        {
            return (null, EAttemptOutcome.Retry);
        }

        switch (result.Status)
        {
            case EResumeJobStatus.Attached:
                Console.WriteLine($"Resuming job {binding.JobId} on service side");
                return (binding.JobId, null);
            case EResumeJobStatus.NotFound:
                Console.WriteLine("Previous job is no longer available on service side, starting a new one");
                ClientJobStore.Delete(_logger, _receiverParameters.DestinationFolder);
                return (null, null);
            case EResumeJobStatus.Busy:
                Console.WriteLine(
                    $"Service is still holding the previous connection ({result.ErrorMessage}), will retry...");
                return (null, EAttemptOutcome.Retry);
            default:
                //Failed და ყველა უცნობი სტატუსი საბოლოო შეცდომაა
                StShared.WriteErrorLine($"Job resume failed on service side: {result.ErrorMessage}", true, _logger,
                    false);
                return (null, EAttemptOutcome.Fatal);
        }
    }

    //ახალი სამუშაოს დაწყება: existingFiles ყოველ ჯერზე თავიდან სკანირდება, რომ უკვე მიღებული ფაილები აღარ გამოგზავნოს
    private async Task<(string? JobId, EAttemptOutcome? Outcome)> TryStartJob(HubConnection hubConnection,
        CancellationToken cancellationToken)
    {
        string[] existingFiles = UsbCopyReceiverParameters.ScanExistingFiles(_receiverParameters.DestinationFolder);

        string jobId;
        try
        {
            jobId = await hubConnection.InvokeAsync<string>(UsbCopyHubEvents.StartJob,
                _receiverParameters.RemoteProjectName, existingFiles, cancellationToken);
        }
        catch (HubException e)
        {
            //სერვისის კონფიგურაციული შეცდომა (უცნობი პროექტი და ა.შ.) — ხელახლა ცდას აზრი არ აქვს
            StShared.WriteErrorLine($"StartJob failed on service side: {e.Message}", true, _logger, false);
            return (null, EAttemptOutcome.Fatal);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _logger.LogWarning(e, "StartJob transport failure");
            return (null, EAttemptOutcome.Retry);
        }

        Console.WriteLine($"Job {jobId} started on service side");

        ClientJobStore.Save(_logger, _receiverParameters.DestinationFolder,
            new ClientJobBinding
            {
                JobId = jobId,
                ServerAddress = _receiverParameters.ServerAddress,
                RemoteProjectName = _receiverParameters.RemoteProjectName,
                CreatedAtUtc = DateTime.UtcNow
            });

        return (jobId, null);
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

    private async Task<EAttemptOutcome> ProcessEvents(HubConnection hubConnection, ChannelReader<ReceivedEvent> reader,
        string jobId, Action onPackageAcked, CancellationToken cancellationToken)
    {
        await foreach (ReceivedEvent receivedEvent in reader.ReadAllAsync(cancellationToken))
        {
            switch (receivedEvent.Kind)
            {
                case EReceivedEventKind.PackageReady:
                    EPackageProcessResult processResult =
                        await ProcessPackage(hubConnection, jobId, receivedEvent.Payload, cancellationToken);
                    if (processResult == EPackageProcessResult.RetryTransport)
                    {
                        return EAttemptOutcome.Retry;
                    }

                    if (processResult == EPackageProcessResult.AckedOk)
                    {
                        onPackageAcked();
                    }

                    //AckedFailed: სერვისი JobFailed-ს გამოგზავნის — ველოდებით მას
                    break;
                case EReceivedEventKind.JobCompleted:
                    PrintSummary(receivedEvent.Payload);
                    ClientJobStore.Delete(_logger, _receiverParameters.DestinationFolder);
                    return EAttemptOutcome.Completed;
                case EReceivedEventKind.JobFailed:
                    StShared.WriteErrorLine($"Job failed on service side: {receivedEvent.Payload}", true, _logger,
                        false);
                    return EAttemptOutcome.Fatal;
                default:
                    //ConnectionClosed და ყველა უცნობი მოვლენა კავშირის დაკარგვად ითვლება
                    return EAttemptOutcome.Retry;
            }
        }

        //რიგი ტერმინალური მოვლენის გარეშე დაიცალა — კავშირი დაიკარგა
        return EAttemptOutcome.Retry;
    }

    private async Task<EPackageProcessResult> ProcessPackage(HubConnection hubConnection, string jobId,
        string? manifestJson, CancellationToken cancellationToken)
    {
        PackageManifest? manifest =
            manifestJson is null ? null : JsonSerializer.Deserialize<PackageManifest>(manifestJson);
        if (manifest is null)
        {
            return await SendAck(hubConnection, jobId, string.Empty, false, "Invalid package manifest",
                cancellationToken)
                ? EPackageProcessResult.AckedFailed
                : EPackageProcessResult.RetryTransport;
        }

        Console.WriteLine(
            $"Receiving package {manifest.PackageIndex + 1}/{manifest.PackagesTotal} ({manifest.PackageType}, {manifest.TransferSize} bytes)");

        string? error = null;
        var contextLost = false;
        for (var attempt = 1; attempt <= MaxDownloadAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                await DownloadAndRestore(manifest, cancellationToken);
                error = null;
                break;
            }
            catch (UsbCopyPartContextLostException e)
            {
                //ლოკალური მდგომარეობა შეუქცევადად დაკარგულია — ხელახლა ცდა უაზროა, პაკეტი მაშინვე უნდა უარვყოთ
                error = e.Message;
                contextLost = true;
                break;
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                error = e.Message;
                _logger.LogWarning(e, "Package {PackageId} attempt {Attempt} failed", manifest.PackageId, attempt);
            }
        }

        if (error is null)
        {
            _packageIndexFailures.Remove(manifest.PackageIndex);
            return await SendAck(hubConnection, jobId, manifest.PackageId, true, null, cancellationToken)
                ? EPackageProcessResult.AckedOk
                : EPackageProcessResult.RetryTransport;
        }

        //ტრანზიტული ჩავარდნა ჯერ reconnect-ით უნდა ვცადოთ — ack=false მთელ სამუშაოს კლავს;
        //მხოლოდ ერთსა და იმავე პაკეტზე განმეორებადი (ან შეუქცევადი) შეცდომა ითვლება სისტემურად
        int failureCycles = _packageIndexFailures.GetValueOrDefault(manifest.PackageIndex) + 1;
        _packageIndexFailures[manifest.PackageIndex] = failureCycles;

        if (!contextLost && failureCycles < MaxPackageFailureCycles)
        {
            StShared.WriteErrorLine(
                $"Package {manifest.PackageId} receive failed (cycle {failureCycles}/{MaxPackageFailureCycles}): {error}",
                true, _logger, false);
            return EPackageProcessResult.RetryTransport;
        }

        bool ackSent = await SendAck(hubConnection, jobId, manifest.PackageId, false, error, cancellationToken);
        StShared.WriteErrorLine($"Package {manifest.PackageId} receive failed: {error}", true, _logger, false);
        return ackSent ? EPackageProcessResult.AckedFailed : EPackageProcessResult.RetryTransport;
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
        await using var zipFileStream = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None,
            CopyBufferSize, true);
        await using (zipFileStream.ConfigureAwait(false))
        {
            await DownloadToStream(manifest, zipFileStream, cancellationToken);
        }

        await ZipFile.ExtractToDirectoryAsync(zipPath, _receiverParameters.DestinationFolder, true, cancellationToken);
        File.Delete(zipPath);
    }

    //საშუალო ფაილი: ჩამოტვირთვა დროებითი სახელით და საბოლოო სახელზე გადარქმევა.
    //თუ ფაილი (ან მისი დროებითი ვერსია) უკვე გვაქვს და ჰეშიც ემთხვევა, ხელახლა აღარ ვტვირთავთ
    private async Task RestoreWholeFile(PackageManifest manifest, CancellationToken cancellationToken)
    {
        (string targetPath, string tempPath) = GetTargetPaths(manifest);

        if (File.Exists(targetPath) && new FileInfo(targetPath).Length == manifest.TransferSize &&
            await TryHashRange(targetPath, 0, manifest.TransferSize, manifest.Sha256, cancellationToken))
        {
            Console.WriteLine($"File {manifest.RelativePath} is already present, download skipped");
            return;
        }

        if (File.Exists(tempPath) && new FileInfo(tempPath).Length == manifest.TransferSize &&
            await TryHashRange(tempPath, 0, manifest.TransferSize, manifest.Sha256, cancellationToken))
        {
            File.Move(tempPath, targetPath, true);
            Console.WriteLine($"File {manifest.RelativePath} was already downloaded, download skipped");
            return;
        }

        // ReSharper disable once using
        // ReSharper disable once DisposableConstructor
        await using var fileStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None,
            CopyBufferSize, true);
        await using (fileStream.ConfigureAwait(false))
        {
            await DownloadToStream(manifest, fileStream, cancellationToken);
        }

        File.Move(tempPath, targetPath, true);
    }

    //დიდი ფაილის ნაწილი: ჩაწერა დროებით ფაილში თავის ადგილზე; ბოლო ნაწილის შემდეგ ფაილი საბოლოო სახელს იღებს.
    //უკვე ჩაწერილი (ჰეშით დადასტურებული) ნაწილები ხელახლა აღარ იტვირთება — ეს არის გაგრძელების მთავარი მექანიზმი
    private async Task RestoreFilePart(PackageManifest manifest, CancellationToken cancellationToken)
    {
        if (manifest.FileSize is null || manifest.PartOffset is null || manifest.PartIndex is null ||
            manifest.PartsTotal is null)
        {
            throw new UsbCopyReceiverException($"Invalid FilePart manifest for package {manifest.PackageId}");
        }

        (string targetPath, string tempPath) = GetTargetPaths(manifest);
        bool isLastPart = manifest.PartIndex.Value == manifest.PartsTotal.Value - 1;

        if (File.Exists(tempPath))
        {
            if (new FileInfo(tempPath).Length == manifest.FileSize.Value && await TryHashRange(tempPath,
                    manifest.PartOffset.Value, manifest.TransferSize, manifest.Sha256, cancellationToken))
            {
                Console.WriteLine(
                    $"Part {manifest.PartIndex + 1}/{manifest.PartsTotal} of {manifest.RelativePath} is already present, download skipped");
            }
            else
            {
                await DownloadPartToTempFile(manifest, tempPath, cancellationToken);
            }

            //გადარქმევა მხოლოდ მაშინ, როცა დროებითი ფაილი ნამდვილად არსებობს — მზა ფაილს ცარიელი ვერსია არ უნდა გადაეწეროს
            if (isLastPart)
            {
                File.Move(tempPath, targetPath, true);
            }

            return;
        }

        //დროებითი ფაილი არ არსებობს: შესაძლოა ფაილი უკვე დასრულდა და გადაერქვა, დასტური კი დაიკარგა
        if (File.Exists(targetPath) && new FileInfo(targetPath).Length == manifest.FileSize.Value &&
            await TryHashRange(targetPath, manifest.PartOffset.Value, manifest.TransferSize, manifest.Sha256,
                cancellationToken))
        {
            Console.WriteLine(
                $"Part {manifest.PartIndex + 1}/{manifest.PartsTotal} of {manifest.RelativePath} is already finalized, download skipped");
            return;
        }

        if (manifest.PartIndex.Value > 0)
        {
            //წინა ნაწილები ლოკალურად აღარ გვაქვს — ამ სამუშაოში ფაილს ვეღარ ავაწყობთ
            throw new UsbCopyPartContextLostException(
                $"Previous parts of {manifest.RelativePath} are lost locally, the file must be resent in a new job");
        }

        await DownloadPartToTempFile(manifest, tempPath, cancellationToken);

        if (isLastPart)
        {
            File.Move(tempPath, targetPath, true);
        }
    }

    private async Task DownloadPartToTempFile(PackageManifest manifest, string tempPath,
        CancellationToken cancellationToken)
    {
        // ReSharper disable once using
        // ReSharper disable once DisposableConstructor
        await using var fileStream = new FileStream(tempPath, FileMode.OpenOrCreate, FileAccess.ReadWrite,
            FileShare.None, CopyBufferSize, true);
        await using (fileStream.ConfigureAwait(false))
        {
            if (fileStream.Length != manifest.FileSize!.Value)
            {
                fileStream.SetLength(manifest.FileSize.Value);
            }

            fileStream.Seek(manifest.PartOffset!.Value, SeekOrigin.Begin);
            await DownloadToStream(manifest, fileStream, cancellationToken);
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

    //ლოკალური ფაილის მონაკვეთის SHA256 შედარება მანიფესტისეულთან; ნებისმიერი პრობლემა უბრალოდ false-ს ნიშნავს
    private static async Task<bool> TryHashRange(string filePath, long offset, long length, string? expectedSha256,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(expectedSha256))
        {
            return false;
        }

        try
        {
            // ReSharper disable once using
            // ReSharper disable once DisposableConstructor
            await using var fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                CopyBufferSize, true);
            await using (fileStream.ConfigureAwait(false))
            {
                if (fileStream.Length < offset + length)
                {
                    return false;
                }

                fileStream.Seek(offset, SeekOrigin.Begin);

                // ReSharper disable once using
                using var incrementalHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[CopyBufferSize];
                long remaining = length;
                while (remaining > 0)
                {
                    var toRead = (int)Math.Min(buffer.Length, remaining);
                    int read = await fileStream.ReadAsync(buffer.AsMemory(0, toRead), cancellationToken);
                    if (read <= 0)
                    {
                        return false;
                    }

                    incrementalHash.AppendData(buffer, 0, read);
                    remaining -= read;
                }

                string sha256 = Convert.ToHexString(incrementalHash.GetHashAndReset());
                return string.Equals(sha256, expectedSha256, StringComparison.OrdinalIgnoreCase);
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return false;
        }
    }

    //პაკეტის ბაიტების ჩამოტვირთვა HTTP ნაკადით, ზომისა და SHA256-ის შემოწმებით.
    //საერთო ვადა უსასრულოა (დიდი პაკეტები ნელ ქსელზეც უნდა ჩამოვიდეს), მაგრამ გაჩერებული ნაკადი ვადით წყდება
    private async Task DownloadToStream(PackageManifest manifest, Stream targetStream,
        CancellationToken cancellationToken)
    {
        // ReSharper disable once using
        using HttpClient httpClient = _httpClientFactory.CreateClient();
        httpClient.Timeout = Timeout.InfiniteTimeSpan;

        // ReSharper disable once using
        // ReSharper disable once DisposableConstructor
        using var idleCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        idleCts.CancelAfter(TimeSpan.FromSeconds(DownloadIdleTimeoutSeconds));

        try
        {
            // ReSharper disable once using
            using HttpResponseMessage response = await httpClient.GetAsync(
                new Uri(BuildDownloadAddress(manifest.JobId, manifest.PackageId)),
                HttpCompletionOption.ResponseHeadersRead, idleCts.Token);

            if (!response.IsSuccessStatusCode)
            {
                throw new UsbCopyReceiverException($"Download failed with status code {(int)response.StatusCode}");
            }

            idleCts.CancelAfter(TimeSpan.FromSeconds(DownloadIdleTimeoutSeconds));

            // ReSharper disable once using
            await using Stream bodyStream = await response.Content.ReadAsStreamAsync(idleCts.Token);
            await using (bodyStream.ConfigureAwait(false))
            {
                // ReSharper disable once using
                using var incrementalHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[CopyBufferSize];
                long totalRead = 0;

                int read;
                while ((read = await bodyStream.ReadAsync(buffer.AsMemory(0, buffer.Length), idleCts.Token)) > 0)
                {
                    //ვადა ნულდება ყოველ წარმატებულ წაკითხვაზე — მხოლოდ სრული გაჩერება ითვლება პრობლემად
                    idleCts.CancelAfter(TimeSpan.FromSeconds(DownloadIdleTimeoutSeconds));

                    incrementalHash.AppendData(buffer, 0, read);
                    await targetStream.WriteAsync(buffer.AsMemory(0, read), idleCts.Token);
                    totalRead += read;
                }

                await targetStream.FlushAsync(idleCts.Token);

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
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new UsbCopyReceiverException($"Download stalled for more than {DownloadIdleTimeoutSeconds} seconds");
        }
    }

    //დასტურის გაგზავნა; ტრანსპორტის შეცდომა false-ს აბრუნებს, რომ ცდა reconnect-ით განმეორდეს
    private async Task<bool> SendAck(HubConnection hubConnection, string jobId, string packageId, bool ok,
        string? error, CancellationToken cancellationToken)
    {
        try
        {
            await hubConnection.InvokeAsync(UsbCopyHubEvents.AckPackage, jobId, packageId, ok, error,
                cancellationToken);
            return true;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _logger.LogWarning(e, "Cannot send ack for package {PackageId}", packageId);
            return false;
        }
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

    //ცდის ბოლოს: staging-ში დარჩენილი ნახევრად ჩამოტვირთული zip-ები ნაგავია, job.json და *.usbtmp კი პროგრესია და რჩება
    private void CleanupZipStaging()
    {
        try
        {
            string stagingDir = Path.Combine(_receiverParameters.DestinationFolder,
                UsbCopyReceiverParameters.StagingFolderName);
            if (!Directory.Exists(stagingDir))
            {
                return;
            }

            foreach (string zipFile in Directory.EnumerateFiles(stagingDir, "*.zip"))
            {
                File.Delete(zipFile);
            }
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "Cannot cleanup staging zip files");
        }
    }

    //წარმატებული დასრულების შემდეგ დროებითი კვალი აღარ გვჭირდება
    private void CleanupAllTempArtifacts()
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

    //პაკეტის დამუშავების შედეგი
    private enum EPackageProcessResult
    {
        AckedOk,
        AckedFailed,
        RetryTransport
    }
}
