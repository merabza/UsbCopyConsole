using System.Net.Http;
using AppCliTools.CliMenu;
using AppCliTools.CliParameters.CliMenuCommands;
using Microsoft.Extensions.Logging;
using ParametersManagement.LibParameters;
using UsbCopyConsole.Cruders;

namespace UsbCopyConsole.Menu.CreateNewProject;

// ReSharper disable once ClassNeverInstantiated.Global
public class CreateNewProjectFactoryStrategy : IMenuCommandFactoryStrategy
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<CreateNewProjectFactoryStrategy> _logger;
    private readonly IParametersManager _parametersManager;

    public CreateNewProjectFactoryStrategy(ILogger<CreateNewProjectFactoryStrategy> logger,
        IParametersManager parametersManager, IHttpClientFactory httpClientFactory)
    {
        _logger = logger;
        _parametersManager = parametersManager;
        _httpClientFactory = httpClientFactory;
    }

    public CliMenuCommand CreateMenuCommand()
    {
        var usbCopyConsoleProjectCruder =
            UsbCopyConsoleProjectCruder.Create(_logger, _httpClientFactory, _parametersManager);

        //ახალი პროექტის შექმნა
        return new NewItemCliMenuCommand(usbCopyConsoleProjectCruder, usbCopyConsoleProjectCruder.CrudNamePlural,
            $"New {usbCopyConsoleProjectCruder.CrudName}");
    }
}
