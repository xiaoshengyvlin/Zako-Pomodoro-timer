using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace TomatoNoteTimer.Models;

public sealed class AppState
{
    private const string DefaultPresetAudioFileName = "音乐预设.mp3";
    private const string DefaultNoteText = "人生若只如初见，何事秋风悲画扇";
    private const string DefaultNotesColor = "#FFB6C1";
    private const string DefaultCountdownColor = "#ADD8E6";

    public AppSettings App { get; set; } = new();
    public TimerSettings Timer { get; set; } = new();
    public NotesSettings Notes { get; set; } = new();
    public NotesContent NotesContent { get; set; } = new();
    public WebTaskSettings WebTask { get; set; } = new();
    public RuntimeState Runtime { get; set; } = new();

    public void Normalize()
    {
        App = App ?? new AppSettings();
        Timer = Timer ?? new TimerSettings();
        Notes = Notes ?? new NotesSettings();
        NotesContent = NotesContent ?? new NotesContent();
        WebTask = WebTask ?? new WebTaskSettings();
        Runtime = Runtime ?? new RuntimeState();

        if (Timer.DurationSeconds < 1)
        {
            if (Timer.DurationMinutes > 0)
            {
                Timer.DurationSeconds = Timer.DurationMinutes * 60;
            }
            else
            {
                Timer.DurationSeconds = 25 * 60;
            }
        }

        if (Timer.WorkDurationSeconds < 1)
        {
            Timer.WorkDurationSeconds = Timer.DurationSeconds;
        }

        if (Timer.WorkDurationSeconds < 1)
        {
            Timer.WorkDurationSeconds = 25 * 60;
        }

        Timer.WorkDurationSeconds = Math.Max(1, Timer.WorkDurationSeconds);
        Timer.DurationSeconds = Timer.WorkDurationSeconds;
        Timer.DurationMinutes = Math.Max(1, Timer.WorkDurationSeconds / 60);

        if (Timer.RestDurationSeconds < 1)
        {
            Timer.RestDurationSeconds = 5 * 60;
        }

        Timer.RestDurationSeconds = Math.Max(1, Timer.RestDurationSeconds);

        if (Timer.LoopCount < 1)
        {
            Timer.LoopCount = 1;
        }

        if (!Timer.EnableWorkSession && !Timer.EnableRestSession)
        {
            Timer.EnableWorkSession = true;
        }

        if (Notes.FontSize < 10)
        {
            Notes.FontSize = 24;
        }

        if (Notes.RotationSeconds < 2)
        {
            Notes.RotationSeconds = 8;
        }

        if (App.WindowWidth < 140)
        {
            App.WindowWidth = 380;
        }

        if (App.WindowHeight < 88)
        {
            App.WindowHeight = 210;
        }

        App.Hotkeys = App.Hotkeys ?? new HotkeySettings();
        App.Hotkeys.Normalize();

        // Backward compatibility: old versions stored opacity in [0,1].
        if (App.Transparency >= 0 && App.Transparency <= 1.0)
        {
            App.Transparency = (1.0 - App.Transparency) * 100.0;
        }

        if (App.Transparency < 0 || App.Transparency > 100)
        {
            App.Transparency = 20;
        }
        App.Transparency = Math.Round(App.Transparency, 0);

        // Migrate previous default profile to the new default profile.
        if (string.Equals(App.BackgroundColor, "#4C6D8C", System.StringComparison.OrdinalIgnoreCase) &&
            System.Math.Abs(App.Transparency - 18) < 0.1)
        {
            App.BackgroundColor = "#FFFFFF";
            App.Transparency = 20;
        }

        if (App.CountdownFontSize < 10)
        {
            App.CountdownFontSize = 30;
        }

        App.BackgroundEffect = NormalizeEffectMode(App.BackgroundEffect);
        App.BackgroundColor = NormalizeHexColor(App.BackgroundColor, "#FFFFFF");
        App.NotesTextColor = NormalizeHexColor(App.NotesTextColor, DefaultNotesColor);
        App.CountdownTextColor = NormalizeHexColor(App.CountdownTextColor, DefaultCountdownColor);
        if (string.IsNullOrWhiteSpace(App.CountdownFontFamily) ||
            string.Equals(App.CountdownFontFamily, "Microsoft YaHei UI", System.StringComparison.OrdinalIgnoreCase))
        {
            App.CountdownFontFamily = "KaiTi";
        }

        if (string.IsNullOrWhiteSpace(Notes.FontFamily) ||
            string.Equals(Notes.FontFamily, "Microsoft YaHei UI", System.StringComparison.OrdinalIgnoreCase))
        {
            Notes.FontFamily = "KaiTi";
        }

        if (!Timer.UseLoopAudio && !string.IsNullOrWhiteSpace(Timer.LoopAudioPath))
        {
            Timer.UseLoopAudio = true;
        }

        if (string.IsNullOrWhiteSpace(Timer.RestEndAudioPath) && !string.IsNullOrWhiteSpace(Timer.EndAudioPath))
        {
            Timer.RestEndAudioPath = Timer.EndAudioPath;
        }

        if (string.IsNullOrWhiteSpace(Timer.EndAudioPath))
        {
            Timer.EndAudioPath = DefaultPresetAudioFileName;
            Timer.UseCustomEndAudio = false;
        }

        if (string.IsNullOrWhiteSpace(Timer.RestEndAudioPath))
        {
            Timer.RestEndAudioPath = Timer.EndAudioPath;
            Timer.UseCustomRestEndAudio = false;
        }

        if (NotesContent.Segments is null || NotesContent.Segments.Count == 0)
        {
            NotesContent.Segments = new List<string> { DefaultNoteText };
        }

        if (NotesContent.CurrentIndex < 0 || NotesContent.CurrentIndex >= NotesContent.Segments.Count)
        {
            NotesContent.CurrentIndex = 0;
        }

        Runtime.CurrentPhase = Runtime.CurrentPhase?.Trim().ToLowerInvariant() switch
        {
            "rest" => "Rest",
            _ => "Work"
        };

        Runtime.CurrentCycle = Math.Max(1, Runtime.CurrentCycle);
        Runtime.TotalCycles = Math.Max(1, Runtime.TotalCycles);
        Runtime.PhaseIndex = Math.Max(0, Runtime.PhaseIndex);
    }

    private static string NormalizeEffectMode(string? mode)
    {
        return mode?.Trim().ToLowerInvariant() switch
        {
            "blur" => "Blur",
            "frosted" => "Frosted",
            "apple" => "Apple",
            _ => "Blur"
        };
    }

    private static string NormalizeHexColor(string? color, string fallback)
    {
        if (string.IsNullOrWhiteSpace(color))
        {
            return fallback;
        }

        string normalized = color.Trim();
        if (Regex.IsMatch(normalized, "^#[0-9a-fA-F]{6}$"))
        {
            return normalized.ToUpperInvariant();
        }

        if (Regex.IsMatch(normalized, "^#[0-9a-fA-F]{8}$"))
        {
            // #AARRGGBB -> #RRGGBB
            return $"#{normalized.Substring(3, 6).ToUpperInvariant()}";
        }

        return fallback;
    }
}

public sealed class AppSettings
{
    public bool TopMost { get; set; } = true;
    public bool FixedMode { get; set; }
    // 0 = fully opaque, 100 = fully transparent
    public double Transparency { get; set; } = 20;
    public int WindowWidth { get; set; } = 380;
    public int WindowHeight { get; set; } = 210;
    public bool MinimizeToTrayOnClose { get; set; } = true;
    public bool AutoStart { get; set; }
    public string BackgroundEffect { get; set; } = "Blur";
    public string BackgroundColor { get; set; } = "#FFFFFF";
    public string NotesTextColor { get; set; } = "#FFB6C1";
    public string CountdownTextColor { get; set; } = "#ADD8E6";
    public string CountdownFontFamily { get; set; } = "KaiTi";
    public double CountdownFontSize { get; set; } = 30;
    public bool SimpleMode { get; set; }
    public HotkeySettings Hotkeys { get; set; } = new();
}

public sealed class TimerSettings
{
    public bool EnableWorkSession { get; set; } = true;
    public bool EnableRestSession { get; set; } = true;
    public int WorkDurationSeconds { get; set; } = 25 * 60;
    public int RestDurationSeconds { get; set; } = 5 * 60;
    public int LoopCount { get; set; } = 1;
    public int DurationSeconds { get; set; } = 25 * 60;
    public int DurationMinutes { get; set; } = 25;
    public bool UseLoopAudio { get; set; }
    public bool EnableWorkEndAudio { get; set; } = true;
    public bool EnableRestEndAudio { get; set; } = true;
    public bool UseCustomEndAudio { get; set; }
    public bool UseCustomRestEndAudio { get; set; }
    public string LoopAudioPath { get; set; } = string.Empty;
    public string EndAudioPath { get; set; } = "音乐预设.mp3";
    public string RestEndAudioPath { get; set; } = "音乐预设.mp3";
}

public sealed class NotesSettings
{
    public string FontFamily { get; set; } = "KaiTi";
    public double FontSize { get; set; } = 24;
    public bool EnableRotation { get; set; }
    public int RotationSeconds { get; set; } = 8;
}

public sealed class NotesContent
{
    public List<string> Segments { get; set; } = new() { "人生若只如初见，何事秋风悲画扇" };
    public int CurrentIndex { get; set; }
}

public sealed class WebTaskSettings
{
    public bool EnableOnTimerComplete { get; set; } = true;
    public string Url { get; set; } = "https://github.com/xiaoshengyvlin/Zako-Pomodoro-timer";
}

public sealed class RuntimeState
{
    public int RemainingSeconds { get; set; }
    public string CurrentPhase { get; set; } = "Work";
    public int CurrentCycle { get; set; } = 1;
    public int TotalCycles { get; set; } = 1;
    public int PhaseIndex { get; set; }
    public bool SessionChainActive { get; set; }
}

public sealed class HotkeySettings
{
    public string StartTimer { get; set; } = string.Empty;
    public string PauseTimer { get; set; } = string.Empty;
    public string ResetTimer { get; set; } = string.Empty;
    public string ToggleSimpleMode { get; set; } = string.Empty;
    public string ToggleTopMost { get; set; } = string.Empty;
    public string ToggleFixedMode { get; set; } = string.Empty;

    public void Normalize()
    {
        StartTimer = NormalizeValue(StartTimer);
        PauseTimer = NormalizeValue(PauseTimer);
        ResetTimer = NormalizeValue(ResetTimer);
        ToggleSimpleMode = NormalizeValue(ToggleSimpleMode);
        ToggleTopMost = NormalizeValue(ToggleTopMost);
        ToggleFixedMode = NormalizeValue(ToggleFixedMode);
    }

    private static string NormalizeValue(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
    }
}
