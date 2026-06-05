using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;
using MySqlConnector;

namespace GameTimeBonus;

public class DatabaseConfig
{
    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 3306;
    public string Username { get; set; } = "root";
    public string Password { get; set; } = "";
    public string Name { get; set; } = "new";
}

public class SettingsConfig
{
    public int BonusIntervalSeconds { get; set; } = 600;
    public int BonusAmount { get; set; } = 1;
    public bool EnableLogging { get; set; } = true;
    public string Language { get; set; } = "ru";
    public int ReturnWindowSeconds { get; set; } = 120;
    public string BonusMessage { get; set; } = "[Бонус] Вам начислено {amount} руб.";
}

public class PluginConfig : BasePluginConfig
{
    public DatabaseConfig Database { get; set; } = new();
    public SettingsConfig Settings { get; set; } = new();
    public override int Version => 1;
}

public class PlayerTimeData
{
    public string SteamId { get; set; } = string.Empty;
    public int AccumulatedSeconds { get; set; } = 0;
    public bool IsActive { get; set; } = false;
    public DateTime? DisconnectTime { get; set; }
    public DateTime LastBonusTime { get; set; }
    public DateTime JoinTime { get; set; }
}

public class GameTimeBonus : BasePlugin, IPluginConfig<PluginConfig>
{
    public override string ModuleName => "GameTimeBonus";
    public override string ModuleVersion => "1.0.3";
    public override string ModuleAuthor => "PattHs";

    public required PluginConfig Config { get; set; }

    private Dictionary<string, PlayerTimeData> _playerData = new();
    private string _connectionString = string.Empty;
    private string _logFilePath = string.Empty;

    public void OnConfigParsed(PluginConfig config)
    {
    }

    public override void Load(bool hotReload)
    {
        var db = Config.Database;
        var builder = new MySqlConnectionStringBuilder
        {
            Server = db.Host,
            Port = (uint)db.Port,
            UserID = db.Username,
            Password = db.Password,
            Database = db.Name,
            Pooling = true,
            MinimumPoolSize = 1,
            MaximumPoolSize = 5,
            ConnectionIdleTimeout = 60,
            CharacterSet = "utf8mb4"
        };
        _connectionString = builder.ConnectionString;

        string logDir = Path.Combine(ModuleDirectory, "logs");
        Directory.CreateDirectory(logDir);
        _logFilePath = Path.Combine(logDir, "GameTimeBonus.log");

        RegisterEventHandler<EventPlayerConnectFull>(OnPlayerConnectFull);
        RegisterEventHandler<EventPlayerDisconnect>(OnPlayerDisconnect);
        RegisterEventHandler<EventPlayerTeam>(OnPlayerTeam);

        AddTimer(1.0f, SecondTimerCallback, TimerFlags.REPEAT);

        Log($"Plugin loaded. Interval: {Config.Settings.BonusIntervalSeconds}, Amount: {Config.Settings.BonusAmount}, ReturnWindow: {Config.Settings.ReturnWindowSeconds}");
    }

    public override void Unload(bool hotReload)
    {
    }

    private void Log(string message)
    {
        if (!Config.Settings.EnableLogging) return;
        string logMessage = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}";
        try
        {
            File.AppendAllText(_logFilePath, logMessage + Environment.NewLine);
        }
        catch { }
        Console.WriteLine(logMessage);
    }

    private string GetSteamId2(CCSPlayerController player)
    {
        if (player?.AuthorizedSteamID == null) return string.Empty;
        string steamId = player.AuthorizedSteamID.SteamId2;
        return steamId.Replace("STEAM_0:", "STEAM_1:");
    }

    private bool IsSpectator(CCSPlayerController player)
    {
        return player.Team == CsTeam.Spectator;
    }

    private HookResult OnPlayerConnectFull(EventPlayerConnectFull eventInfo, GameEventInfo _)
    {
        var player = eventInfo.Userid;
        if (player == null || !player.IsValid || player.IsBot || player.IsHLTV)
            return HookResult.Continue;

        string steamId = GetSteamId2(player);
        if (string.IsNullOrEmpty(steamId))
            return HookResult.Continue;

        if (_playerData.TryGetValue(steamId, out var existingData))
        {
            if (existingData.DisconnectTime.HasValue)
            {
                var timeSince = DateTime.Now - existingData.DisconnectTime.Value;
                if (timeSince.TotalSeconds <= Config.Settings.ReturnWindowSeconds)
                {
                    existingData.IsActive = !IsSpectator(player);
                    existingData.DisconnectTime = null;
                    Log($"{player.PlayerName} ({steamId}) returned within window. Resuming with {existingData.AccumulatedSeconds}s.");
                }
                else
                {
                    existingData.AccumulatedSeconds = 0;
                    existingData.IsActive = !IsSpectator(player);
                    existingData.DisconnectTime = null;
                    existingData.JoinTime = DateTime.Now;
                    Log($"{player.PlayerName} ({steamId}) returned after window. Time reset.");
                }
            }
            else
            {
                existingData.IsActive = !IsSpectator(player);
                existingData.JoinTime = DateTime.Now;
            }
        }
        else
        {
            _playerData[steamId] = new PlayerTimeData
            {
                SteamId = steamId,
                AccumulatedSeconds = 0,
                IsActive = !IsSpectator(player),
                DisconnectTime = null,
                LastBonusTime = DateTime.Now,
                JoinTime = DateTime.Now
            };
            Log($"New player {player.PlayerName} ({steamId}) connected.");
        }
        return HookResult.Continue;
    }

    private HookResult OnPlayerDisconnect(EventPlayerDisconnect eventInfo, GameEventInfo _)
    {
        var player = eventInfo.Userid;
        if (player == null) return HookResult.Continue;
        string steamId = GetSteamId2(player);
        if (string.IsNullOrEmpty(steamId)) return HookResult.Continue;

        if (_playerData.TryGetValue(steamId, out var data))
        {
            data.IsActive = false;
            data.DisconnectTime = DateTime.Now;
            Log($"{player.PlayerName} ({steamId}) disconnected. Time frozen at {data.AccumulatedSeconds}s.");
        }
        return HookResult.Continue;
    }

    private HookResult OnPlayerTeam(EventPlayerTeam eventInfo, GameEventInfo _)
    {
        var player = eventInfo.Userid;
        if (player == null || !player.IsValid) return HookResult.Continue;
        string steamId = GetSteamId2(player);
        if (string.IsNullOrEmpty(steamId)) return HookResult.Continue;

        if (_playerData.TryGetValue(steamId, out var data))
        {
            bool isSpec = IsSpectator(player);
            data.IsActive = !isSpec;
            Log($"{player.PlayerName} ({steamId}) team changed. Active: {data.IsActive}");
        }
        return HookResult.Continue;
    }

    private void SecondTimerCallback()
    {
        try
        {
            var players = Utilities.GetPlayers();

            foreach (var player in players)
            {
                if (player == null || !player.IsValid || player.IsBot || player.IsHLTV)
                    continue;

                string steamId = GetSteamId2(player);
                if (string.IsNullOrEmpty(steamId) || !_playerData.TryGetValue(steamId, out var data))
                    continue;

                if (!data.IsActive || IsSpectator(player))
                {
                    if (IsSpectator(player)) data.IsActive = false;
                    continue;
                }

                data.AccumulatedSeconds++;

                if (data.AccumulatedSeconds >= Config.Settings.BonusIntervalSeconds)
                {
                    _ = GiveBonusToPlayerAsync(player, steamId, data);
                    data.AccumulatedSeconds = 0;
                }
            }
        }
        catch (Exception ex)
        {
            Log($"Timer error: {ex.Message}");
        }
    }

    private async Task GiveBonusToPlayerAsync(CCSPlayerController player, string steamId, PlayerTimeData data)
    {
        try
        {
            await using var connection = new MySqlConnection(_connectionString);
            await connection.OpenAsync();

            var checkCmd = new MySqlCommand("SELECT 1 FROM lk WHERE auth = @auth", connection);
            checkCmd.Parameters.AddWithValue("@auth", steamId);
            var exists = await checkCmd.ExecuteScalarAsync();
            if (exists == null)
            {
                Log($"Player {player.PlayerName} ({steamId}) not found in LK database.");
                return;
            }

            var updateCmd = new MySqlCommand("UPDATE lk SET cash = cash + @amount WHERE auth = @auth", connection);
            updateCmd.Parameters.AddWithValue("@amount", Config.Settings.BonusAmount);
            updateCmd.Parameters.AddWithValue("@auth", steamId);
            int affected = await updateCmd.ExecuteNonQueryAsync();

            if (affected > 0)
            {
                data.LastBonusTime = DateTime.Now;
                string message = Config.Settings.BonusMessage.Replace("{amount}", Config.Settings.BonusAmount.ToString());
                player.PrintToChat(message);
                Log($"Bonus {Config.Settings.BonusAmount} given to {player.PlayerName} ({steamId}).");
            }
        }
        catch (Exception ex)
        {
            Log($"Bonus error for {player.PlayerName} ({steamId}): {ex.Message}");
        }
    }
}