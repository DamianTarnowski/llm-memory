using Memory.Cli;

if (args.Length == 0 || args[0] is "help" or "--help" or "-h")
{
    HelpCommand.Print();
    return 0;
}

return args[0] switch
{
    "init" => await InitCommand.RunAsync(args[1..]).ConfigureAwait(false),
    "api-key" => await ApiKeyCommand.RunAsync(args[1..]).ConfigureAwait(false),
    "backup" => await BackupCommand.RunAsync(args[1..]).ConfigureAwait(false),
    "chat" => await ChatCommand.RunAsync(args[1..]).ConfigureAwait(false),
    "tenants" => await TenantsCommand.RunAsync(args[1..]).ConfigureAwait(false),
    _ => HelpCommand.Unknown(args[0]),
};
