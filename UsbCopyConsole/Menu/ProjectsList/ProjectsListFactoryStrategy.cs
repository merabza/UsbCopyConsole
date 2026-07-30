using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using AppCliTools.CliMenu;
using Microsoft.Extensions.Logging;
using ParametersManagement.LibParameters;
using UsbCopyConsole.Models;

namespace UsbCopyConsole.Menu.ProjectsList;

public class ProjectsListFactoryStrategy : IMenuCommandListFactoryStrategy
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<ProjectsListFactoryStrategy> _logger;
    private readonly IParametersManager _parametersManager;

    public ProjectsListFactoryStrategy(IParametersManager parametersManager,
        ILogger<ProjectsListFactoryStrategy> logger, IHttpClientFactory httpClientFactory)
    {
        _parametersManager = parametersManager;
        _logger = logger;
        _httpClientFactory = httpClientFactory;
    }

    public List<CliMenuCommand> CreateMenuCommandsList()
    {
        var parameters = (UsbCopyConsoleParameters)_parametersManager.Parameters;
        //პროექტების ჩამონათვალი
        return
        [
            .. parameters.Projects.OrderBy(o => o.Key).Select(kvp =>
                new UsbCopyConsoleProjectSubMenuCommand(_logger, _httpClientFactory, _parametersManager, kvp.Key))
        ];
    }
}
