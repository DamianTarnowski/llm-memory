using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Json;

namespace Memory.Secrets;

public sealed class JsonFileOptions
{
    public required string Path { get; init; }
    public bool Optional { get; init; } = true;
    public bool ReloadOnChange { get; init; } = true;
}

/// <summary>
/// Thin wrapper around <see cref="JsonConfigurationSource"/> so the chain has
/// uniform shape (every layer is an <see cref="ISecretConnector"/>). Useful as
/// the lowest-priority fallback in the chain — local appsettings.Local.json or a
/// machine-shared secrets.json that an operator hand-maintains.
/// </summary>
public sealed class JsonFileConnector(JsonFileOptions options) : ISecretConnector
{
    public string Name => $"json:{options.Path}";

    public IConfigurationProvider Build(IConfigurationBuilder builder)
    {
        var inner = new JsonConfigurationSource
        {
            Path = options.Path,
            Optional = options.Optional,
            ReloadOnChange = options.ReloadOnChange,
            FileProvider = builder.GetFileProvider(),
        };
        inner.ResolveFileProvider();
        return inner.Build(builder);
    }
}
