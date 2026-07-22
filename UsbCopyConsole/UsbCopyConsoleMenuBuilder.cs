using System;
using AppCliTools.CliMenu;
using AppCliTools.CliTools.Services.MenuBuilder;
using UsbCopyConsole.Menu;

namespace UsbCopyConsole;

public class UsbCopyConsoleMenuBuilder : IMenuBuilder
{
    private readonly IServiceProvider _serviceProvider;

    // ReSharper disable once ConvertToPrimaryConstructor
    public UsbCopyConsoleMenuBuilder(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    public CliMenuSet? BuildMainMenu()
    {
        //მთავარი მენიუს ჩატვირთვა
        return CliMenuSetFactory.CreateMenuSet("Main Menu", MenuData.MainMenuCommandFactoryStrategyNames,
            _serviceProvider, true);
    }
}
