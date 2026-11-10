using System.Text.Json;
using System.Text.Json.Nodes;
using Npgsql;

namespace Godswar.LootTool;

/// <summary>
/// Connection and client paths for the tool. The operator types host, port,
/// database, user and password separately; the connection string is assembled
/// from those fields, and all of it is persisted next to the tool.
/// </summary>
internal sealed class LootToolSettings
{
    private const string DefaultHost = "127.0.0.1";
    private const int DefaultPort = 5432;
    private const string DefaultDatabase = "godswar_local";
    private const string DefaultUsername = "godswar";
    private const string DefaultPassword = "godswar_dev_password";
    private const string SettingsFileName = "loot-tool.settings.json";

    public string Host { get; set; } = DefaultHost;

    public int Port { get; set; } = DefaultPort;

    public string Database { get; set; } = DefaultDatabase;

    public string Username { get; set; } = DefaultUsername;

    public string Password { get; set; } = DefaultPassword;

    public string ClientRoot { get; set; } = string.Empty;

    public string BuildConnectionString() =>
        new NpgsqlConnectionStringBuilder
        {
            Host = string.IsNullOrWhiteSpace(Host) ? DefaultHost : Host.Trim(),
            Port = Port is > 0 and <= 65535 ? Port : DefaultPort,
            Database = string.IsNullOrWhiteSpace(Database)
                ? DefaultDatabase
                : Database.Trim(),
            Username = string.IsNullOrWhiteSpace(Username)
                ? DefaultUsername
                : Username.Trim(),
            Password = Password,
            Pooling = true
        }.ConnectionString;

    public LootToolSettings Clone() => new()
    {
        Host = Host,
        Port = Port,
        Database = Database,
        Username = Username,
        Password = Password,
        ClientRoot = ClientRoot
    };

    private static string SettingsPath =>
        Path.Combine(AppContext.BaseDirectory, SettingsFileName);

    public static LootToolSettings Load()
    {
        var settings = BuildDefault();
        try
        {
            if (!File.Exists(SettingsPath))
            {
                return settings;
            }

            var node = JsonNode.Parse(File.ReadAllText(SettingsPath)) as JsonObject;
            if (node is null)
            {
                return settings;
            }

            // Older revisions stored one connection string; keep reading it.
            if (node.TryGetPropertyValue("ConnectionString", out var legacy) &&
                legacy?.GetValue<string>() is { Length: > 0 } connectionString)
            {
                settings.ApplyConnectionString(connectionString);
            }

            settings.Host = ReadString(node, "Host", settings.Host);
            settings.Port = ReadInt(node, "Port", settings.Port);
            settings.Database = ReadString(node, "Database", settings.Database);
            settings.Username = ReadString(node, "Username", settings.Username);
            settings.Password = ReadString(node, "Password", settings.Password);
            settings.ClientRoot = ReadString(node, "ClientRoot", settings.ClientRoot);
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException)
        {
            // A corrupt settings file must not stop the tool from opening.
        }

        return settings;
    }

    public void Save()
    {
        try
        {
            File.WriteAllText(
                SettingsPath,
                JsonSerializer.Serialize(
                    this,
                    new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Persisting is a convenience; ignore a read-only install directory.
        }
    }

    /// <summary>
    /// Applies command-line overrides and persists them, so a launcher script can
    /// point the tool at another database or client without touching the UI.
    /// </summary>
    public static void ApplyOverrides(string? connectionString, string? clientRoot)
    {
        if (string.IsNullOrWhiteSpace(connectionString) &&
            string.IsNullOrWhiteSpace(clientRoot))
        {
            return;
        }

        var settings = Load();
        if (!string.IsNullOrWhiteSpace(connectionString))
        {
            settings.ApplyConnectionString(connectionString);
        }

        if (!string.IsNullOrWhiteSpace(clientRoot))
        {
            settings.ClientRoot = clientRoot;
        }

        settings.Save();
    }

    public void ApplyConnectionString(string connectionString)
    {
        try
        {
            var builder = new NpgsqlConnectionStringBuilder(connectionString);
            if (!string.IsNullOrWhiteSpace(builder.Host))
            {
                Host = builder.Host;
            }

            if (builder.Port > 0)
            {
                Port = builder.Port;
            }

            if (!string.IsNullOrWhiteSpace(builder.Database))
            {
                Database = builder.Database;
            }

            if (!string.IsNullOrWhiteSpace(builder.Username))
            {
                Username = builder.Username;
            }

            if (!string.IsNullOrEmpty(builder.Password))
            {
                Password = builder.Password;
            }
        }
        catch (ArgumentException)
        {
            // Keep the defaults when the supplied string is not a connection string.
        }
    }

    /// <summary>Repository root, located by walking up to GodswarServer.sln.</summary>
    public static string? FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        for (var depth = 0; depth < 8 && directory is not null; depth++)
        {
            if (File.Exists(Path.Combine(directory.FullName, "GodswarServer.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return null;
    }

    private static LootToolSettings BuildDefault()
    {
        var settings = new LootToolSettings();
        var root = FindRepositoryRoot();
        var appSettingsPath = root is null
            ? null
            : Path.Combine(root, "appsettings.json");
        if (appSettingsPath is null || !File.Exists(appSettingsPath))
        {
            return settings;
        }

        try
        {
            var node = JsonNode.Parse(File.ReadAllText(appSettingsPath)) as JsonObject;
            if (node?["storage"]?["postgresConnectionString"]?.GetValue<string>()
                is { Length: > 0 } configured)
            {
                settings.ApplyConnectionString(configured);
                // The checked-in file points at the historical database; this
                // tool targets the one the game server actually runs on.
                settings.Database = DefaultDatabase;
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException)
        {
            // Fall back to the built-in defaults.
        }

        return settings;
    }

    private static string ReadString(JsonObject node, string name, string fallback) =>
        node.TryGetPropertyValue(name, out var value) &&
        value?.GetValue<string>() is { Length: > 0 } text
            ? text
            : fallback;

    private static int ReadInt(JsonObject node, string name, int fallback) =>
        node.TryGetPropertyValue(name, out var value) &&
        int.TryParse(value?.ToString(), out var parsed)
            ? parsed
            : fallback;
}
