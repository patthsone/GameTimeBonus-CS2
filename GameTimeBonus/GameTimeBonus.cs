using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;
using MySqlConnector;

namespace GameTimeBonus;

public sealed class DatabaseConfig
{
    [JsonPropertyName("Host")]
    public string Host { get; set; } = "localhost";

    [JsonPropertyName("Port")]
    public uint Port { get; set; } = 3306;

    [JsonPropertyName("Username")]
    public string Username { get; set; } = "root";

    [JsonPropertyName("Password")]
    public string Password { get; set; } = "";

    [JsonPropertyName("Name")]
    public string Name { get; set; } = "new";

    [JsonPropertyName("Table")]
    public string Table { get; set; } = "lk";
}

public sealed class SettingsConfig
{
    [JsonPropertyName("BonusIntervalSeconds")]
    public int BonusIntervalSeconds { get; set; } = 600;

    [JsonPropertyName("BonusAmount")]
    public decimal BonusAmount { get; set; } = 1;

    [JsonPropertyName("ReturnWindowSeconds")]
    public int ReturnWindowSeconds { get; set; } = 120;

    [JsonPropertyName("CountSpectators")]
    public bool CountSpectators { get; set; } = false;

    [JsonPropertyName("EnableLogging")]
    public bool EnableLogging { get; set; } = true;
}

public sealed class PluginConfig : BasePluginConfig
{
    [JsonPropertyName("ConfigVersion")]
    public override int Version { get; set; } = 2;

    [JsonPropertyName("Database")]
    public DatabaseConfig Database { get; set; } = new();

    [JsonPropertyName("Settings")]
    public SettingsConfig Settings { get; set; } = new();
}

public sealed class PlayerTimeData
{
    public int AccumulatedSeconds { get; set; }
    public bool IsActive { get; set; }
    public bool IsGrantingBonus { get; set; }
    public DateTime? DisconnectTime { get; set; }
}

public sealed class GameTimeBonus : BasePlugin, IPluginConfig<PluginConfig>
{
    public override string ModuleName => "GameTimeBonus";
    public override string ModuleVersion => "1.0.4";
    public override string ModuleAuthor => "PattHs";
    public override string ModuleDescription => "Gives LK cash bonus for active playtime.";

    public PluginConfig Config { get; set; } = null!;

    private readonly Dictionary<string, PlayerTimeData> _playerData = new();
    private string _connectionString = "";
    private string _tableName = "lk";

    public void OnConfigParsed(PluginConfig config)
    {
        Config = config;
    }

    public override void Load(bool hotReload)
    {
        BuildDatabaseSettings();

        RegisterEventHandler<EventPlayerConnectFull>(OnPlayerConnectFull);
        RegisterEventHandler<EventPlayerDisconnect>(OnPlayerDisconnect);
        RegisterEventHandler<EventPlayerTeam>(OnPlayerTeam);

        AddTimer(1.0f, SecondTimerCallback, TimerFlags.REPEAT);

        Log($"Loaded. Interval={Config.Settings.BonusIntervalSeconds}s, Amount={Config.Settings.BonusAmount}, ReturnWindow={Config.Settings.ReturnWindowSeconds}s");
    }

    public override void Unload(bool hotReload)
    {
        _playerData.Clear();
    }

    private void BuildDatabaseSettings()
    {
        var db = Config.Database;

        _tableName = NormalizeTableName(db.Table);

        var builder = new MySqlConnectionStringBuilder
        {
            Server = db.Host,
            Port = db.Port,
            UserID = db.Username,
            Password = db.Password,
            Database = db.Name,
            Pooling = true,
            MinimumPoolSize = 0,
            MaximumPoolSize = 10,
            ConnectionIdleTimeout = 60,
            ConnectionLifeTime = 300,
            ConnectionTimeout = 5,
            DefaultCommandTimeout = 10,
            CharacterSet = "utf8mb4"
        };

        _connectionString = builder.ConnectionString;
    }

    private static string NormalizeTableName(string tableName)
    {
        if (string.IsNullOrWhiteSpace(tableName))
            return "lk";

        foreach (var c in tableName)
        {
            if (!char.IsLetterOrDigit(c) && c != '_')
                return "lk";
        }

        return tableName;
    }

    private void Log(string message)
    {
        if (!Config.Settings.EnableLogging)
            return;

        Logger.LogInformation("[GameTimeBonus] {Message}", message);
    }

    private static string GetSteamId2(CCSPlayerController player)
    {
        if (player.AuthorizedSteamID == null)
            return "";

        return player.AuthorizedSteamID.SteamId2.Replace("STEAM_0:", "STEAM_1:");
    }

    private bool CanCountTime(CCSPlayerController player)
    {
        if (player == null || !player.IsValid || player.IsBot || player.IsHLTV)
            return false;

        if (!Config.Settings.CountSpectators && player.Team == CsTeam.Spectator)
            return false;

        return true;
    }

    private HookResult OnPlayerConnectFull(EventPlayerConnectFull eventInfo, GameEventInfo info)
    {
        var player = eventInfo.Userid;

        if (player == null || !player.IsValid || player.IsBot || player.IsHLTV)
            return HookResult.Continue;

        var steamId = GetSteamId2(player);

        if (string.IsNullOrWhiteSpace(steamId))
            return HookResult.Continue;

        var active = CanCountTime(player);

        if (_playerData.TryGetValue(steamId, out var data))
        {
            if (data.DisconnectTime.HasValue)
            {
                var offlineSeconds = (DateTime.UtcNow - data.DisconnectTime.Value).TotalSeconds;

                if (offlineSeconds > Config.Settings.ReturnWindowSeconds)
                    data.AccumulatedSeconds = 0;

                data.DisconnectTime = null;
            }

            data.IsActive = active;
            data.IsGrantingBonus = false;
        }
        else
        {
            _playerData[steamId] = new PlayerTimeData
            {
                AccumulatedSeconds = 0,
                IsActive = active,
                IsGrantingBonus = false,
                DisconnectTime = null
            };
        }

        Log($"{player.PlayerName} ({steamId}) connected. Active={active}");

        return HookResult.Continue;
    }

    private HookResult OnPlayerDisconnect(EventPlayerDisconnect eventInfo, GameEventInfo info)
    {
        var player = eventInfo.Userid;

        if (player == null)
            return HookResult.Continue;

        var steamId = GetSteamId2(player);

        if (string.IsNullOrWhiteSpace(steamId))
            return HookResult.Continue;

        if (_playerData.TryGetValue(steamId, out var data))
        {
            data.IsActive = false;
            data.DisconnectTime = DateTime.UtcNow;
            data.IsGrantingBonus = false;

            Log($"{player.PlayerName} ({steamId}) disconnected. Saved={data.AccumulatedSeconds}s");
        }

        return HookResult.Continue;
    }

    private HookResult OnPlayerTeam(EventPlayerTeam eventInfo, GameEventInfo info)
    {
        var player = eventInfo.Userid;

        if (player == null || !player.IsValid || player.IsBot || player.IsHLTV)
            return HookResult.Continue;

        var steamId = GetSteamId2(player);

        if (string.IsNullOrWhiteSpace(steamId))
            return HookResult.Continue;

        if (_playerData.TryGetValue(steamId, out var data))
        {
            data.IsActive = CanCountTime(player);
            Log($"{player.PlayerName} ({steamId}) team changed. Active={data.IsActive}");
        }

        return HookResult.Continue;
    }

    private void SecondTimerCallback()
    {
        foreach (var player in Utilities.GetPlayers())
        {
            if (player == null || !player.IsValid || player.IsBot || player.IsHLTV)
                continue;

            var steamId = GetSteamId2(player);

            if (string.IsNullOrWhiteSpace(steamId))
                continue;

            if (!_playerData.TryGetValue(steamId, out var data))
                continue;

            data.IsActive = CanCountTime(player);

            if (!data.IsActive || data.IsGrantingBonus)
                continue;

            data.AccumulatedSeconds++;

            if (data.AccumulatedSeconds < Config.Settings.BonusIntervalSeconds)
                continue;

            data.IsGrantingBonus = true;

            var playerName = player.PlayerName;
            _ = GiveBonusAsync(player, steamId, playerName, data);
        }
    }

    private async Task GiveBonusAsync(CCSPlayerController player, string steamId, string playerName, PlayerTimeData data)
    {
        var success = false;
        var notRegistered = false;
        Exception? error = null;

        try
        {
            await using var connection = new MySqlConnection(_connectionString);
            await connection.OpenAsync();

            await using var command = connection.CreateCommand();
            command.CommandText = $"UPDATE `{_tableName}` SET cash = cash + @amount WHERE auth = @auth";
            command.Parameters.AddWithValue("@amount", Config.Settings.BonusAmount);
            command.Parameters.AddWithValue("@auth", steamId);

            var affected = await command.ExecuteNonQueryAsync();

            if (affected > 0)
                success = true;
            else
                notRegistered = true;
        }
        catch (Exception ex)
        {
            error = ex;
        }

        Server.NextFrame(() =>
        {
            data.IsGrantingBonus = false;

            if (error != null)
            {
                Log($"Bonus error for {playerName} ({steamId}): {error.Message}");
                return;
            }

            if (notRegistered)
            {
                data.AccumulatedSeconds = 0;
                Log($"{playerName} ({steamId}) not found in LK database.");
                return;
            }

            if (!success)
                return;

            data.AccumulatedSeconds = 0;

            if (player.IsValid)
                player.PrintToChat(Localizer["bonus.message", Config.Settings.BonusAmount]);

            Log($"Bonus {Config.Settings.BonusAmount} given to {playerName} ({steamId}).");
        });
    }
}
