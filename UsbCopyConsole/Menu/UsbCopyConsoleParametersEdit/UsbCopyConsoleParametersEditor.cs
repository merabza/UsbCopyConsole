using System.Net.Http;
using AppCliTools.CliParameters;
using AppCliTools.CliParameters.FieldEditors;
using AppCliTools.CliParametersApiClientsEdit;
using Microsoft.Extensions.Logging;
using ParametersManagement.LibApiClientParameters;
using ParametersManagement.LibParameters;
using UsbCopyConsole.Models;

namespace UsbCopyConsole.Menu.UsbCopyConsoleParametersEdit;

public sealed class UsbCopyConsoleParametersEditor : ParametersEditor
{
    public UsbCopyConsoleParametersEditor(IParameters parameters, IParametersManager parametersManager, ILogger logger,
        IHttpClientFactory httpClientFactory) : base("UsbCopyConsole Parameters Editor", parameters, parametersManager)
    {
        FieldEditors.Add(new FolderPathFieldEditor(nameof(UsbCopyConsoleParameters.LogFolder)));

        FieldEditors.Add(new DictionaryFieldEditor<ApiClientCruder, ApiClientSettings>(
            nameof(UsbCopyConsoleParameters.ApiClients),
            x => new ApiClientCruder(logger, httpClientFactory, parametersManager, x)));
    }
}
