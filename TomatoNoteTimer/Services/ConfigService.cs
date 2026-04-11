using System;
using System.IO;
using System.Text;
using System.Text.Json;
using TomatoNoteTimer.Models;

namespace TomatoNoteTimer.Services;

public sealed class ConfigService
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true
    };

    private readonly string _rootDir;
    private readonly string _configDir;
    private readonly string _dataDir;
    private readonly string _audioDir;
    private readonly string _logsDir;

    private readonly string _appConfigPath;
    private readonly string _timerConfigPath;
    private readonly string _notesConfigPath;
    private readonly string _webTaskConfigPath;
    private readonly string _notesContentPath;
    private readonly string _runtimeStatePath;
    private readonly string _logPath;

    public ConfigService(string rootDir)
    {
        _rootDir = rootDir;
        _configDir = Path.Combine(rootDir, "config");
        _dataDir = Path.Combine(rootDir, "data");
        _audioDir = Path.Combine(rootDir, "audio");
        _logsDir = Path.Combine(rootDir, "logs");

        _appConfigPath = Path.Combine(_configDir, "app.json");
        _timerConfigPath = Path.Combine(_configDir, "timer.json");
        _notesConfigPath = Path.Combine(_configDir, "notes.json");
        _webTaskConfigPath = Path.Combine(_configDir, "web_tasks.json");
        _notesContentPath = Path.Combine(_dataDir, "notes_content.json");
        _runtimeStatePath = Path.Combine(_dataDir, "runtime_state.json");
        _logPath = Path.Combine(_logsDir, "app.log");
    }

    public string ConfigDirectory => _configDir;
    public string DataDirectory => _dataDir;
    public string AudioDirectory => _audioDir;
    public string LogsDirectory => _logsDir;
    public string LogPath => _logPath;

    public AppState Load()
    {
        EnsureLayout();

        var state = new AppState
        {
            App = LoadJsonFile(_appConfigPath, new AppSettings()),
            Timer = LoadJsonFile(_timerConfigPath, new TimerSettings()),
            Notes = LoadJsonFile(_notesConfigPath, new NotesSettings()),
            WebTask = LoadJsonFile(_webTaskConfigPath, new WebTaskSettings()),
            NotesContent = LoadJsonFile(_notesContentPath, new NotesContent()),
            Runtime = LoadJsonFile(_runtimeStatePath, new RuntimeState())
        };

        state.Normalize();
        Save(state);
        return state;
    }

    public void Save(AppState state)
    {
        state.Normalize();
        EnsureLayout();

        SaveJsonFile(_appConfigPath, state.App);
        SaveJsonFile(_timerConfigPath, state.Timer);
        SaveJsonFile(_notesConfigPath, state.Notes);
        SaveJsonFile(_webTaskConfigPath, state.WebTask);
        SaveJsonFile(_notesContentPath, state.NotesContent);
        SaveJsonFile(_runtimeStatePath, state.Runtime);
    }

    public void EnsureLayout()
    {
        Directory.CreateDirectory(_configDir);
        Directory.CreateDirectory(_dataDir);
        Directory.CreateDirectory(_audioDir);
        Directory.CreateDirectory(Path.Combine(_audioDir, "timer_loop"));
        Directory.CreateDirectory(Path.Combine(_audioDir, "countdown_end"));
        Directory.CreateDirectory(_logsDir);

        if (!File.Exists(_logPath))
        {
            File.WriteAllText(_logPath, string.Empty, Encoding.UTF8);
        }
    }

    public void AppendLog(string message)
    {
        var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}";
        File.AppendAllText(_logPath, line, Encoding.UTF8);
    }

    private T LoadJsonFile<T>(string path, T defaults) where T : class
    {
        if (!File.Exists(path))
        {
            return defaults;
        }

        string content = File.ReadAllText(path, Encoding.UTF8);
        if (string.IsNullOrWhiteSpace(content))
        {
            return defaults;
        }

        try
        {
            var value = JsonSerializer.Deserialize<T>(content, SerializerOptions);
            return value ?? defaults;
        }
        catch (JsonException)
        {
            BackupBrokenConfig(path);
            return defaults;
        }
    }

    private static void SaveJsonFile<T>(string path, T data) where T : class
    {
        string json = JsonSerializer.Serialize(data, SerializerOptions);
        File.WriteAllText(path, json, Encoding.UTF8);
    }

    private static void BackupBrokenConfig(string path)
    {
        string backupPath = $"{path}.{DateTime.Now:yyyyMMddHHmmss}.broken.json";
        File.Copy(path, backupPath, overwrite: true);
    }
}
