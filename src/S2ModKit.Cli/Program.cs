using System.Globalization;
using S2ModKit.Cli;

var requestedUiLanguage = Environment.GetEnvironmentVariable("DOTNET_CLI_UI_LANGUAGE");
if (!string.IsNullOrWhiteSpace(requestedUiLanguage))
{
    var culture = CultureInfo.GetCultureInfo(requestedUiLanguage);
    CultureInfo.DefaultThreadCurrentUICulture = culture;
    CultureInfo.CurrentUICulture = culture;
}

return await S2ModKitCli.CreateDefault().RunAsync(args, Console.Out, Console.Error, CancellationToken.None);
