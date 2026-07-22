using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AppCliTools.CliMenu;
using Microsoft.Extensions.Logging;
using ParametersManagement.LibParameters;
using SystemTools.SystemToolsShared;
using UsbCopyConsole.ToolCommands;

namespace UsbCopyConsole.Commands;

public sealed class ReceiveFilesCliMenuCommand : CliMenuCommand
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger _logger;
    private readonly IParametersManager _parametersManager;
    private readonly string _projectName;

    // ReSharper disable once ConvertToPrimaryConstructor
    public ReceiveFilesCliMenuCommand(ILogger logger, IHttpClientFactory httpClientFactory, string projectName,
        IParametersManager parametersManager) : base("ReceiveFiles", EMenuAction.Reload)
    {
        _logger = logger;
        _httpClientFactory = httpClientFactory;
        _projectName = projectName;
        _parametersManager = parametersManager;
    }

    protected override async ValueTask<bool> RunBody(CancellationToken cancellationToken = default)
    {
        IToolCommand? toolCommand =
            ReceiverToolCommandFactory.Create(_logger, _httpClientFactory, _projectName, _parametersManager);

        if (toolCommand?.Par != null)
        {
            return await toolCommand.Run(cancellationToken);
        }

        Console.WriteLine("Parameters not loaded. Tool not started.");
        StShared.Pause();
        return false;
    }
}
