using System.Collections.Generic;
using System.Net.Http;
using AppCliTools.CliParameters;
using AppCliTools.CliParameters.FieldEditors;
using AppCliTools.CliParametersApiClientsEdit.FieldEditors;
using Microsoft.Extensions.Logging;
using ParametersManagement.LibParameters;
using UsbCopyConsole.Models;

namespace UsbCopyConsole.Cruders;

public sealed class UsbCopyConsoleProjectCruder : ParCruder<UsbCopyConsoleProjectModel>
{
    private UsbCopyConsoleProjectCruder(ILogger logger, IHttpClientFactory httpClientFactory,
        IParametersManager parametersManager, Dictionary<string, UsbCopyConsoleProjectModel> currentValuesDictionary) :
        base(parametersManager, currentValuesDictionary, "Project", "Projects")
    {
        FieldEditors.Add(new FolderPathFieldEditor(nameof(UsbCopyConsoleProjectModel.LocalPath)));
        FieldEditors.Add(new ApiClientNameFieldEditor(nameof(UsbCopyConsoleProjectModel.ApiClientName), logger,
            httpClientFactory, parametersManager));
        FieldEditors.Add(new TextFieldEditor(nameof(UsbCopyConsoleProjectModel.RemoteProjectName)));
    }

    public static UsbCopyConsoleProjectCruder Create(ILogger logger, IHttpClientFactory httpClientFactory,
        IParametersManager parametersManager)
    {
        var parameters = (UsbCopyConsoleParameters)parametersManager.Parameters;
        return new UsbCopyConsoleProjectCruder(logger, httpClientFactory, parametersManager, parameters.Projects);
    }
}
