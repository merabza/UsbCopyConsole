using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AppCliTools.CliMenu;
using AppCliTools.LibDataInput;
using ParametersManagement.LibParameters;
using SystemTools.SystemToolsShared;
using UsbCopyConsole.Models;

namespace UsbCopyConsole.Commands;

public sealed class DeleteUsbCopyConsoleProjectCommand : CliMenuCommand
{
    private readonly IParametersManager _parametersManager;
    private readonly string _projectName;

    // ReSharper disable once ConvertToPrimaryConstructor
    public DeleteUsbCopyConsoleProjectCommand(IParametersManager parametersManager, string projectName) : base(
        "Delete Project", EMenuAction.LevelUp, EMenuAction.Reload, projectName)
    {
        _parametersManager = parametersManager;
        _projectName = projectName;
    }

    protected override async ValueTask<bool> RunBody(CancellationToken cancellationToken = default)
    {
        var parameters = (UsbCopyConsoleParameters)_parametersManager.Parameters;

        Dictionary<string, UsbCopyConsoleProjectModel> projects = parameters.Projects;
        if (!projects.ContainsKey(_projectName))
        {
            StShared.WriteErrorLine($"Project {_projectName} not found", true);
            return false;
        }

        if (!Inputer.InputBool($"This will Delete Project {_projectName}. are you sure?", false, false))
        {
            return false;
        }

        projects.Remove(_projectName);
        await _parametersManager.Save(parameters, $"Project {_projectName} Deleted", null, cancellationToken);

        return true;
    }
}
