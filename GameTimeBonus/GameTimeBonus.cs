using System;
using System.IO;
using System.Text.Json;
using System.Collections.Generic;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;
using CounterStrikeSharp.API.Modules.Timers;
using MySqlConnector;

namespace GameTimeBonus;

public class PluginConfig
{
    public int Version { get; set; } = 1;
    public DatabaseConfig Database { get; set; } = new();
    public SettingsConfig Settings { get; set; } = new();
}

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

public partial class GameTimeBonus : BasePlugin
{
    public override string ModuleName => "GameTimeBonus";
    public override string ModuleVersion => "1.0.1";
    public override string ModuleAuthor => "PattHs";
    
    private PluginConfig? _config;
    private MySqlConnection? _dbConnection;
    private DateTime _lastBonusTime;
    private string _logFilePath = string.Empty;
    
    private Dictionary<string, PlayerTimeData> _playerData = new();
    
    private string _bonusMessage = "[Бонус] Вам начислено {amount} руб.";

    public override void Load(bool hotReload)
    {
        SetupLogging();
        LoadConfig();
        LoadTranslations();
        
        if (_config == null)
        {
            Log("Failed to load config!");
            return;
        }

        ConnectToDatabase();
        
        if (_dbConnection == null)
        {
            Log("Failed to connect to database!");
            return;
        }

        _lastBonusTime = DateTime.Now;
        
        RegisterEventHandler<EventPlayerConnectFull>(OnPlayerConnectFull);
        RegisterEventHandler<EventPlayerDisconnect>(OnPlayerDisconnect);
        RegisterEventHandler<EventPlayerTeam>(OnPlayerTeam);
        AddTimer(1.0f, SecondTimerCallback, TimerFlags.REPEAT);
        
        Log($"Plugin loaded. Bonus interval: {_config.Settings.BonusIntervalSeconds} seconds, Amount: {_config.Settings.BonusAmount}, Return window: {_config.Settings.ReturnWindowSeconds} seconds");
    }

    public override void Unload(bool hotReload)
    {
        _dbConnection?.Close();
        _dbConnection?.Dispose();
    }

    private void SetupLogging()
    {
        string gameDir = Server.GameDirectory;
        string logDir;
        
        if (gameDir.Contains("csgo"))
        {
            logDir = Path.Combine(gameDir, "addons", "counterstrikesharp", "logs");
        }
        else
        {
            logDir = Path.Combine(gameDir, "csgo", "addons", "counterstrikesharp", "logs");
        }
        
        Directory.CreateDirectory(logDir);
        _logFilePath = Path.Combine(logDir, "GameTimeBonus.log");
    }

    private void Log(string message)
    {
        string logMessage = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}";
        
        try
        {
            File.AppendAllText(_logFilePath, logMessage + Environment.NewLine);
        }
        catch { }
        
        Console.WriteLine(logMessage);
    }

    private void LoadConfig()
    {
        string gameDir = Server.GameDirectory;
        string configDirectory;
        
        if (gameDir.Contains("csgo"))
        {
            configDirectory = Path.Combine(gameDir, "addons", "counterstrikesharp", "configs", "plugins", "GameTimeBonus");
        }
        else
        {
            string csgoPath = Path.Combine(gameDir, "csgo", "addons", "counterstrikesharp", "configs", "plugins", "GameTimeBonus");
            string defaultPath = Path.Combine(gameDir, "addons", "counterstrikesharp", "configs", "plugins", "GameTimeBonus");
            
            if (Directory.Exists(Path.GetDirectoryName(Path.GetDirectoryName(csgoPath))))
                configDirectory = csgoPath;
            else
                configDirectory = defaultPath;
        }
        
        Directory.CreateDirectory(configDirectory);
        
        var configPath = Path.Combine(configDirectory, "GameTimeBonus.json");
        
        if (File.Exists(configPath))
        {
            var json = File.ReadAllText(configPath);
            _config = JsonSerializer.Deserialize<PluginConfig>(json);
        }
        else
        {
            _config = new PluginConfig();
            var json = JsonSerializer.Serialize(_config, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(configPath, json);
            
            Log($"Created config at: {configPath}");
        }
    }

    private void LoadTranslations()
    {
        var langDirectory = Path.Combine(ModuleDirectory, "lang");
        string selectedLang = _config?.Settings.Language ?? "ru";
        
        var langPath = Path.Combine(langDirectory, $"{selectedLang}.json");
        if (File.Exists(langPath))
        {
            var langJson = File.ReadAllText(langPath);
            var langConfig = JsonSerializer.Deserialize<Dictionary<string, string>>(langJson);
            if (langConfig != null && langConfig.TryGetValue("BonusMessage", out var message))
            {
                _bonusMessage = message;
                Log($"Loaded language: {selectedLang}");
            }
        }
    }

    private void ConnectToDatabase()
    {
        try
        {
            var builder = new MySqlConnectionStringBuilder
            {
                Server = _config!.Database.Host,
                Port = (uint)_config.Database.Port,
                UserID = _config.Database.Username,
                Password = _config.Database.Password,
                Database = _config.Database.Name
            };

            _dbConnection = new MySqlConnection(builder.ConnectionString);
            _dbConnection.Open();
            
            using (var charsetCmd = new MySqlCommand("SET NAMES utf8mb3", _dbConnection))
            {
                charsetCmd.ExecuteNonQuery();
            }
            
            Log("Connected to database successfully");
        }
        catch (Exception ex)
        {
            Log($"Database connection error: {ex.Message}");
        }
    }

    private string GetSteamId2(CCSPlayerController player)
    {
        if (player == null || !player.IsValid)
            return string.Empty;

        try
        {
            var authId = player.AuthorizedSteamID;
            if (authId != null)
            {
                string steamId = CleanSteamId(authId.SteamId2);
                steamId = steamId.Replace("STEAM_0:", "STEAM_1:");
                return steamId;
            }
            
            if (player.UserId != null)
            {
                var steamId3 = player.UserId.Value;
                long accountId = (long)((steamId3 >> 1) & 0x7FFFFFFFFFFFFFFF);
                return CleanSteamId($"STEAM_1:0:{accountId}");
            }
        }
        catch (Exception ex)
        {
            Log($"Error getting SteamID: {ex.Message}");
        }

        return string.Empty;
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

        var steamId2 = GetSteamId2(player);
        if (string.IsNullOrEmpty(steamId2))
            return HookResult.Continue;

        if (_playerData.TryGetValue(steamId2, out var existingData))
        {
            if (existingData.DisconnectTime.HasValue)
            {
                var timeSinceDisconnect = DateTime.Now - existingData.DisconnectTime.Value;
                
                if (timeSinceDisconnect.TotalSeconds <= _config!.Settings.ReturnWindowSeconds)
                {
                    existingData.IsActive = !IsSpectator(player);
                    existingData.DisconnectTime = null;
                    Log($"Player {player.PlayerName} ({steamId2}) returned within {_config.Settings.ReturnWindowSeconds}s. Resuming with {existingData.AccumulatedSeconds}s accumulated.");
                }
                else
                {
                    existingData.AccumulatedSeconds = 0;
                    existingData.IsActive = !IsSpectator(player);
                    existingData.DisconnectTime = null;
                    existingData.JoinTime = DateTime.Now;
                    Log($"Player {player.PlayerName} ({steamId2}) returned after {_config.Settings.ReturnWindowSeconds}s. Time reset.");
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
            _playerData[steamId2] = new PlayerTimeData
            {
                SteamId = steamId2,
                AccumulatedSeconds = 0,
                IsActive = !IsSpectator(player),
                DisconnectTime = null,
                LastBonusTime = DateTime.Now,
                JoinTime = DateTime.Now
            };
            Log($"New player {player.PlayerName} ({steamId2}) connected");
        }

        return HookResult.Continue;
    }

    private HookResult OnPlayerDisconnect(EventPlayerDisconnect eventInfo, GameEventInfo _)
    {
        var player = eventInfo.Userid;
        if (player == null)
            return HookResult.Continue;

        var steamId2 = GetSteamId2(player);
        if (string.IsNullOrEmpty(steamId2))
            return HookResult.Continue;

        if (_playerData.TryGetValue(steamId2, out var playerData))
        {
            playerData.IsActive = false;
            playerData.DisconnectTime = DateTime.Now;
            Log($"Player {player.PlayerName} ({steamId2}) disconnected. Time frozen at {playerData.AccumulatedSeconds}s. Has {_config!.Settings.ReturnWindowSeconds}s to return.");
        }

        return HookResult.Continue;
    }

    private HookResult OnPlayerTeam(EventPlayerTeam eventInfo, GameEventInfo _)
    {
        var player = eventInfo.Userid;
        if (player == null || !player.IsValid)
            return HookResult.Continue;

        var steamId2 = GetSteamId2(player);
        if (string.IsNullOrEmpty(steamId2))
            return HookResult.Continue;

        if (_playerData.TryGetValue(steamId2, out var playerData))
        {
            var newTeam = eventInfo.Team;
            bool wasSpectator = IsSpectator(player);
            
            playerData.IsActive = !wasSpectator;
            
            if (wasSpectator)
            {
                Log($"Player {player.PlayerName} ({steamId2}) is now Spectator. Time accumulation paused.");
            }
            else
            {
                Log($"Player {player.PlayerName} ({steamId2}) joined team {newTeam}. Time accumulation resumed.");
            }
        }

        return HookResult.Continue;
    }

    private void SecondTimerCallback()
    {
        try
        {
            var now = DateTime.Now;
            var players = Utilities.GetPlayers();
            
            foreach (var player in players)
            {
                if (player == null || !player.IsValid)
                    continue;
                
                if (player.IsBot || player.IsHLTV)
                    continue;

                var steamId2 = GetSteamId2(player);
                if (string.IsNullOrEmpty(steamId2))
                    continue;

                if (!_playerData.TryGetValue(steamId2, out var playerData))
                    continue;

                if (!playerData.IsActive)
                    continue;

                if (IsSpectator(player))
                {
                    playerData.IsActive = false;
                    continue;
                }

                playerData.AccumulatedSeconds++;

                if (playerData.AccumulatedSeconds >= _config!.Settings.BonusIntervalSeconds)
                {
                    GiveBonusToPlayer(player, steamId2, playerData);
                    
                    playerData.AccumulatedSeconds = 0;
                }
            }
        }
        catch (Exception ex)
        {
            Log($"Second timer error: {ex.Message}");
        }
    }

    private void GiveBonusToPlayer(CCSPlayerController player, string steamId2, PlayerTimeData playerData)
    {
        if (_dbConnection == null || _config == null)
            return;

        try
        {
            var checkSql = "SELECT 1 FROM lk WHERE auth = @auth";
            using (var checkCmd = new MySqlCommand(checkSql, _dbConnection))
            {
                checkCmd.Parameters.AddWithValue("@auth", steamId2);
                var exists = checkCmd.ExecuteScalar();
                
                if (exists == null)
                {
                    Log($"Player {player.PlayerName} ({steamId2}) not found in LK database. Skipping bonus.");
                    return;
                }
            }

            var updateSql = "UPDATE lk SET cash = cash + @amount WHERE auth = @auth";
            using var cmd = new MySqlCommand(updateSql, _dbConnection);
            cmd.Parameters.AddWithValue("@amount", _config.Settings.BonusAmount);
            cmd.Parameters.AddWithValue("@auth", steamId2);
            
            var affected = cmd.ExecuteNonQuery();
            
            if (affected > 0)
            {
                playerData.LastBonusTime = DateTime.Now;
                
                Log($"Bonus given to {player.PlayerName} ({steamId2}). Total accumulated time reset.");
                
                var message = _bonusMessage.Replace("{amount}", _config.Settings.BonusAmount.ToString());
                player.PrintToChat(message);
            }
        }
        catch (Exception ex)
        {
            Log($"Error giving bonus to {player.PlayerName} ({steamId2}): {ex.Message}");
        }
    }

    private string CleanSteamId(string steamId)
    {
        if (string.IsNullOrEmpty(steamId))
            return string.Empty;
        
        var cleaned = new System.Text.StringBuilder();
        foreach (char c in steamId)
        {
            if (c > 31 && c != 127)
                cleaned.Append(c);
        }
        return cleaned.ToString();
    }
}
