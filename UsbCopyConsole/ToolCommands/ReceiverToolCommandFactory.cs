using System.Net.Http;
using Microsoft.Extensions.Logging;
using ParametersManagement.LibParameters;
using SystemTools.SystemToolsShared;
using UsbCopyConsole.Models;
using UsbCopyConsole.Receiver;

namespace UsbCopyConsole.ToolCommands;

public static class ReceiverToolCommandFactory
{
    public static IToolCommand? Create(ILogger logger, IHttpClientFactory httpClientFactory, string projectName,
        IParametersManager parametersManager)
    {
        var usbCopyConsoleParameters = (UsbCopyConsoleParameters)parametersManager.Parameters;

        var usbCopyReceiverParameters =
            UsbCopyReceiverParameters.Create(logger, usbCopyConsoleParameters, projectName);
        if (usbCopyReceiverParameters is not null)
        {
            return new UsbCopyReceiverToolCommand(logger, true, httpClientFactory, usbCopyReceiverParameters,
                parametersManager);
        }

        StShared.WriteErrorLine("UsbCopyReceiverParameters does not created", true);
        return null;
    }
}
