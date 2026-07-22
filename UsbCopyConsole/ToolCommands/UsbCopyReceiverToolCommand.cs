using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AppCliTools.CliParameters;
using Microsoft.Extensions.Logging;
using ParametersManagement.LibParameters;
using SystemTools.SystemToolsShared;
using UsbCopyConsole.Receiver;

namespace UsbCopyConsole.ToolCommands;

public sealed class UsbCopyReceiverToolCommand : ToolCommand
{
    private const string ActionName = "Receive Files";
    private const string ActionDescription = "This will Receive Files from UsbCopy Service";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger _logger;
    private readonly bool _useConsole;

    // ReSharper disable once ConvertToPrimaryConstructor
    public UsbCopyReceiverToolCommand(ILogger logger, bool useConsole, IHttpClientFactory httpClientFactory,
        UsbCopyReceiverParameters usbCopyReceiverParameters, IParametersManager? parametersManager) : base(logger,
        ActionName, usbCopyReceiverParameters, parametersManager, ActionDescription)
    {
        _logger = logger;
        _useConsole = useConsole;
        _httpClientFactory = httpClientFactory;
    }

    private UsbCopyReceiverParameters UsbCopyReceiverParameters => (UsbCopyReceiverParameters)Par;

    protected override async ValueTask<bool> RunAction(CancellationToken cancellationToken = default)
    {
        try
        {
            var packageReceiver = new PackageReceiver(_logger, UsbCopyReceiverParameters, _httpClientFactory);
            return await packageReceiver.RunAsync(cancellationToken);
        }
        catch (Exception e)
        {
            StShared.WriteException(e, _useConsole);
            throw;
        }
    }
}
