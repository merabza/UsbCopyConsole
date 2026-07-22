using AppCliTools.CliMenu;
using AppCliTools.CliTools.Services.MenuBuilder;
using Microsoft.Extensions.DependencyInjection;
using ParametersManagement.LibParameters;
using ParametersManagement.LibParameters.DependencyInjection;
using Serilog.Events;
using SystemTools.SerilogStuff.DependencyInjection;
using SystemTools.SystemToolsShared.DependencyInjection;
using UsbCopyConsole.Menu.ProjectsList;
using UsbCopyConsole.Menu.UsbCopyConsoleParametersEdit;
using UsbCopyConsole.Models;

namespace UsbCopyConsole.DependencyInjection;

public static class UsbCopyConsoleServices
{
    public static IServiceCollection AddServices(this IServiceCollection services, string appName,
        UsbCopyConsoleParameters par, string parametersFileName)
    {
        // @formatter:off
        services
            .AddSerilogLoggerService(LogEventLevel.Information, appName, par.LogFolder)
            .AddTransientAllStrategies<IMenuCommandListFactoryStrategy>(
                typeof(ProjectsListFactoryStrategy).Assembly)
            .AddSingleton<IMenuBuilder, UsbCopyConsoleMenuBuilder>()
            .AddTransientAllStrategies<IMenuCommandFactoryStrategy>(
                typeof(UsbCopyConsoleParametersEditorListCliMenuCommandFactoryStrategy).Assembly)
            .AddApplication(x =>
            {
                x.AppName = appName;
            })
            .AddMainParametersManager<ParametersManager>(x =>
            {
                x.ParametersFileName = parametersFileName;
                x.Par = par;
            })
            .AddHttpClient()
            ;

        // @formatter:on

        return services;
    }
}
