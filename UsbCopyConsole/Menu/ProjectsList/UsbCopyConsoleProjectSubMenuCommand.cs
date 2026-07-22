using System;
using System.Globalization;
using System.Net.Http;
using AppCliTools.CliMenu;
using AppCliTools.CliMenu.CliMenuCommands;
using AppCliTools.CliParameters.CliMenuCommands;
using AppCliTools.LibDataInput;
using Microsoft.Extensions.Logging;
using ParametersManagement.LibParameters;
using UsbCopyConsole.Commands;
using UsbCopyConsole.Cruders;
using UsbCopyConsole.Models;

namespace UsbCopyConsole.Menu.ProjectsList;

public sealed class UsbCopyConsoleProjectSubMenuCommand : CliMenuCommand
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger _logger;
    private readonly IParametersManager _parametersManager;

    private readonly string _projectName;

    // ReSharper disable once ConvertToPrimaryConstructor
    public UsbCopyConsoleProjectSubMenuCommand(ILogger logger, IHttpClientFactory httpClientFactory,
        IParametersManager parametersManager, string projectName) : base(projectName, EMenuAction.LoadSubMenu)
    {
        _logger = logger;
        _httpClientFactory = httpClientFactory;
        _parametersManager = parametersManager;
        _projectName = projectName;
    }

    public override CliMenuSet GetSubMenu()
    {
        var projectSubMenuSet = new CliMenuSet($"Project => {_projectName}");

        var parameters = (UsbCopyConsoleParameters)_parametersManager.Parameters;

        //პროექტის წაშლა
        var deleteProjectCommand = new DeleteUsbCopyConsoleProjectCommand(_parametersManager, _projectName);
        projectSubMenuSet.AddMenuItem(deleteProjectCommand);

        //პროექტის პარამეტრი
        var projectCruder = UsbCopyConsoleProjectCruder.Create(_logger, _httpClientFactory, _parametersManager);
        var editCommand = new EditItemAllFieldsInSequenceCliMenuCommand(projectCruder, _projectName);
        projectSubMenuSet.AddMenuItem(editCommand);

        projectCruder.FillDetailsSubMenu(projectSubMenuSet, _projectName);

        UsbCopyConsoleProjectModel? project = parameters.GetProject(_projectName);

        if (project != null)
        {
            projectSubMenuSet.AddMenuItem(new ReceiveFilesCliMenuCommand(_logger, _httpClientFactory, _projectName,
                _parametersManager));
        }

        //მთავარ მენიუში გასვლა
        string key = ConsoleKey.Escape.Value().ToLower(CultureInfo.CurrentCulture);
        projectSubMenuSet.AddMenuItem(key, new ExitToMainMenuCliMenuCommand("Exit to Main menu", null), key.Length);

        return projectSubMenuSet;
    }
}
