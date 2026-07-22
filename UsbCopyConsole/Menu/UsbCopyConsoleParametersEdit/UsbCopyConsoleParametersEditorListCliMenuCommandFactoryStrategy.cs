using System.Net.Http;
using AppCliTools.CliMenu;
using AppCliTools.CliParameters.CliMenuCommands;
using Microsoft.Extensions.Logging;
using ParametersManagement.LibParameters;
using UsbCopyConsole.Models;

namespace UsbCopyConsole.Menu.UsbCopyConsoleParametersEdit;

// ReSharper disable once ClassNeverInstantiated.Global
public class UsbCopyConsoleParametersEditorListCliMenuCommandFactoryStrategy : IMenuCommandFactoryStrategy
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<UsbCopyConsoleParametersEditorListCliMenuCommandFactoryStrategy> _logger;
    private readonly IParametersManager _parametersManager;

    public UsbCopyConsoleParametersEditorListCliMenuCommandFactoryStrategy(
        ILogger<UsbCopyConsoleParametersEditorListCliMenuCommandFactoryStrategy> logger,
        IParametersManager parametersManager, IHttpClientFactory httpClientFactory)
    {
        _logger = logger;
        _parametersManager = parametersManager;
        _httpClientFactory = httpClientFactory;
    }

    public CliMenuCommand CreateMenuCommand()
    {
        var parameters = (UsbCopyConsoleParameters)_parametersManager.Parameters;

        var usbCopyConsoleParametersEditor =
            new UsbCopyConsoleParametersEditor(parameters, _parametersManager, _logger, _httpClientFactory);
        return new ParametersEditorListCliMenuCommand(usbCopyConsoleParametersEditor);
    }
}
