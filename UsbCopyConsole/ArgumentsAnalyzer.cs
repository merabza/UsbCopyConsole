using System.CommandLine;
using System.CommandLine.Help;
using System.Threading.Tasks;

namespace UsbCopyConsole;

//პროგრამისთვის გადმოცემული ბრძანებათა სტრიქონის არგუმენტების გაანალიზება
public sealed class ArgumentsAnalyzer
{
    private readonly RootCommand _rootCommand;
    private readonly Option<string?> _useOption;
    private ParseResult? _parseResult;

    public ArgumentsAnalyzer()
    {
        _useOption = new Option<string?>("--use", "-u")
        {
            Description = "File name for use as parameters json."
        };

        _rootCommand = new RootCommand("CrawlerConsole")
        {
            _useOption
        };
    }

    public string? ParametersFileName { get; private set; }
    public int ExitCode { get; private set; }

    //აბრუნებს true-ს, თუ პროგრამის მუშაობა უნდა გაგრძელდეს.
    //false-ის შემთხვევაში პროგრამა ExitCode-ით უნდა დასრულდეს
    public async ValueTask<bool> Analysis(string[] args)
    {
        ParseResult parseResult = _rootCommand.Parse(args);
        _parseResult = parseResult;

        //პარსინგის შეცდომები, --help და --version თვითონ System.CommandLine-მა დაამუშაოს
        if (parseResult.Errors.Count > 0 || parseResult.Action is not null)
        {
            ExitCode = await parseResult.InvokeAsync();
            return false;
        }

        string? parametersFileName = parseResult.GetValue(_useOption);
        ParametersFileName = string.IsNullOrWhiteSpace(parametersFileName) ? null : parametersFileName;

        return true;

    }

    //პარამეტრების გამოყენების ინსტრუქციის გამოტანა
    public void ShowHelp()
    {
        if (_parseResult is null)
        {
            return;
        }

        var helpAction = new HelpAction();
        helpAction.Invoke(_parseResult);
    }
}
