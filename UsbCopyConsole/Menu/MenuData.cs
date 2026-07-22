using System.Collections.Generic;
using UsbCopyConsole.Menu.CreateNewProject;
using UsbCopyConsole.Menu.ProjectsList;
using UsbCopyConsole.Menu.UsbCopyConsoleParametersEdit;

namespace UsbCopyConsole.Menu;

public static class MenuData
{
    public static List<string> MainMenuCommandFactoryStrategyNames { get; } =
    [
        //ძირითადი პარამეტრების რედაქტირება
        nameof(UsbCopyConsoleParametersEditorListCliMenuCommandFactoryStrategy),
        //ახალი პროექტის შექმნა
        nameof(CreateNewProjectFactoryStrategy),
        //პროექტების ჩამონათვალი
        nameof(ProjectsListFactoryStrategy)
    ];
}
