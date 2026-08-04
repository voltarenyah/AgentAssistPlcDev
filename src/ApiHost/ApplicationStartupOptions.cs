public sealed record ApplicationStartupOptions(
    string Host,
    int Port,
    bool OpenBrowserOnStart,
    string? ShutdownToken = null)
{
    public string Url => $"http://{Host}:{Port}";

    public static ApplicationStartupOptions From(IConfiguration configuration, string environmentName)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var isProduction = string.Equals(environmentName, "Production", StringComparison.OrdinalIgnoreCase);
        var isTesting = string.Equals(environmentName, "Testing", StringComparison.OrdinalIgnoreCase);
        var host = isProduction
            ? "127.0.0.1"
            : configuration["Application:Host"] ?? "127.0.0.1";
        var port = configuration.GetValue("Application:Port", 5239);
        if (port is < 1 or > 65535)
            throw new InvalidOperationException("Application:Port must be between 1 and 65535.");

        return new(
            host,
            port,
            !isTesting && configuration.GetValue("Application:OpenBrowserOnStart", true),
            configuration["Application:ShutdownToken"]);
    }

    public static string PortInUseMessage(ApplicationStartupOptions options) => string.Join(
        Environment.NewLine,
        $"Automation Workbench could not start because port {options.Port} on {options.Host} is already in use.",
        string.Empty,
        "Close the other application or configure Application:Port to another loopback port.");

    public static bool IsAddressInUse(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current.Message.Contains("address already in use", StringComparison.OrdinalIgnoreCase)
                || current.Message.Contains("only one usage of each socket address", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}
