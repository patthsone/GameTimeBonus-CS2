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
}

public partial class GameTimeBonus : BasePlugin
{
    public override string ModuleName => "GameTimeBonus";
    public override string ModuleVersion => "1.0.0";
    public override string ModuleAuthor => "PattHs";
    
    private PluginConfig? _config;
    private MySqlConnection? _dbConnection;
    private DateTime _lastBonusTime;
    private string _logFilePath = string.Empty;
    
    private Dictionary<string, DateTime> _playerJoinTime = new();
    private Dictionary<string, DateTime> _playerLastBonusTime = new();
    
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
        
        AddTimer(10.0f, BonusTimerCallback, TimerFlags.REPEAT);
        
        Log($"Plugin loaded. Bonus interval: {_config.Settings.BonusIntervalSeconds} seconds, Amount: {_config.Settings.BonusAmount}");
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
        string[] supportedLangs = { "ru", "en", "uk" };
        
        foreach (var l in supportedLangs)
        {
            var langPath = Path.Combine(langDirectory, $"{l}.json");
            if (File.Exists(langPath))
            {
                var langJson = File.ReadAllText(langPath);
                var langConfig = JsonSerializer.Deserialize<Dictionary<string, string>>(langJson);
                if (langConfig != null && langConfig.TryGetValue("BonusMessage", out var message))
                {
                    _bonusMessage = message;
                    Log($"Loaded language: {l}");
                    break;
                }
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
            
            Log("Connected to database successfully");
        }
        catch (Exception ex)
        {
            Log($"Database connection error: {ex.Message}");
        }
    }

    private HookResult OnPlayerConnectFull(EventPlayerConnectFull eventInfo, GameEventInfo _)
    {
        var player = eventInfo.Userid;
        if (player != null && player.IsValid && !player.IsBot && !player.IsHLTV)
        {
            var authId = player.AuthorizedSteamID;
            if (authId != null)
            {
                var steamId2 = authId.SteamId2;
                steamId2 = steamId2.Replace("STEAM_0:", "STEAM_1:");
                _playerJoinTime[steamId2] = DateTime.Now;
                _playerLastBonusTime[steamId2] = DateTime.Now;
                Log($"Player {player.PlayerName} (SteamID: {steamId2}) connected at {DateTime.Now}");
            }
        }
        return HookResult.Continue;
    }

    private HookResult OnPlayerDisconnect(EventPlayerDisconnect eventInfo, GameEventInfo _)
    {
        var player = eventInfo.Userid;
        if (player != null)
        {
            var authId = player.AuthorizedSteamID;
            if (authId != null)
            {
                var steamId2 = authId.SteamId2;
                steamId2 = steamId2.Replace("STEAM_0:", "STEAM_1:");
                _playerJoinTime.Remove(steamId2);
                _playerLastBonusTime.Remove(steamId2);
                Log($"Player {player.PlayerName} (SteamID: {steamId2}) disconnected");
            }
        }
        return HookResult.Continue;
    }

    private string GetSteamIdString(CCSPlayerController player)
    {
        if (player == null || !player.IsValid)
            return string.Empty;

        try
        {
            var authId = player.AuthorizedSteamID;
            if (authId != null)
            {
                string steamId = authId.SteamId2;
                Log($"Player {player.PlayerName} - SteamID: {steamId}");
                return steamId;
            }
            
            if (player.UserId != null)
            {
                var steamId3 = player.UserId.Value;
                long accountId = (long)((steamId3 >> 1) & 0x7FFFFFFFFFFFFFFF);
                string steamId2 = $"STEAM_1:0:{accountId}";
                Log($"Player {player.PlayerName} - SteamID (from UserId): {steamId2}");
                return steamId2;
            }

            Log($"Player {player.PlayerName} - No SteamID available (AuthorizedSteamID is null)");
        }
        catch (Exception ex)
        {
            Log($"Error getting SteamID: {ex.Message}");
        }

        return string.Empty;
    }

    private void BonusTimerCallback()
    {
        try
        {
            var players = Utilities.GetPlayers();
            
            if (players.Count == 0)
            {
                return;
            }

            GiveBonusToAllPlayers(players);
        }
        catch (Exception ex)
        {
            Log($"Timer error: {ex.Message}");
        }
    }

    private void GiveBonusToAllPlayers(List<CCSPlayerController> players)
    {
        if (_dbConnection == null || _config == null) return;

        int playersBonusCount = 0;

        var now = DateTime.Now;
        var minPlayTime = TimeSpan.FromSeconds(_config.Settings.BonusIntervalSeconds);

        foreach (var player in players)
        {
            if (player == null || !player.IsValid)
                continue;
            
            if (player.IsBot || player.IsHLTV)
                continue;

            var authId = player.AuthorizedSteamID;
            if (authId == null)
                continue;

            var steamId2 = authId.SteamId2;
            
            steamId2 = steamId2.Replace("STEAM_0:", "STEAM_1:");
            
            if (!_playerJoinTime.TryGetValue(steamId2, out var joinTime))
                continue;

            var playTime = now - joinTime;
            
            if (playTime < minPlayTime)
                continue;

            if (!_playerLastBonusTime.TryGetValue(steamId2, out var lastBonusTime))
            {
                _playerLastBonusTime[steamId2] = now;
                lastBonusTime = now;
            }

            var timeSinceLastBonus = (now - lastBonusTime).TotalSeconds;
            if (timeSinceLastBonus < _config.Settings.BonusIntervalSeconds)
                continue;

            try
            {
                var updateSql = "UPDATE lk SET cash = cash + @amount WHERE auth = @auth";
                
                using var cmd = new MySqlCommand(updateSql, _dbConnection);
                cmd.Parameters.AddWithValue("@amount", _config.Settings.BonusAmount);
                cmd.Parameters.AddWithValue("@auth", steamId2);
                
                var affected = cmd.ExecuteNonQuery();
                
                if (affected > 0)
                {
                    playersBonusCount++;
                    
                    _playerLastBonusTime[steamId2] = now;
                    
                    var message = _bonusMessage.Replace("{amount}", _config.Settings.BonusAmount.ToString());
                    player.PrintToChat(message);
                }
            }
            catch (Exception ex)
            {
                Log($"Error giving bonus to {steamId2}: {ex.Message}");
            }
        }

        if (playersBonusCount > 0)
        {
            Log($"Bonus given to {playersBonusCount} players");
        }
    }
}
