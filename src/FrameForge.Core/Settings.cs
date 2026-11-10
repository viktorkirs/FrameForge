using System.Text.Json;

namespace FrameForge.Core;

public static class AppPaths
{
    public static string DataDirectory
    {
        get
        {
            string exeDirectory = AppContext.BaseDirectory;
            if (File.Exists(Path.Combine(exeDirectory, "portable.marker")))
                return Path.Combine(exeDirectory, "data");

            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FrameForge");
        }
    }

    public static string SettingsFile => Path.Combine(DataDirectory, "settings.json");

    public static string LogFile => Path.Combine(DataDirectory, "logs", "frameforge.log");

    public static string DefaultOutputDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "FrameForge");

    public static string ResolveOutputDirectory(string? configured)
    {
        string path = string.IsNullOrWhiteSpace(configured) ? DefaultOutputDirectory : configured;
        Directory.CreateDirectory(path);
        return path;
    }

    public static string CreateRecordingPath(string directory)
    {
        Directory.CreateDirectory(directory);
        string stamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
        string candidate = Path.Combine(directory, $"Recording_{stamp}.mp4");
        int suffix = 2;
        while (File.Exists(candidate))
        {
            candidate = Path.Combine(directory, $"Recording_{stamp}_{suffix}.mp4");
            suffix++;
        }

        return candidate;
    }

    public static string CreateScreenshotPath(string directory)
    {
        Directory.CreateDirectory(directory);
        string stamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
        string candidate = Path.Combine(directory, $"Screenshot_{stamp}.png");
        int suffix = 2;
        while (File.Exists(candidate))
        {
            candidate = Path.Combine(directory, $"Screenshot_{stamp}_{suffix}.png");
            suffix++;
        }

        return candidate;
    }
}

public static class SettingsStore
{
    static readonly object Gate = new();
    static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static AppSettings Load()
    {
        lock (Gate)
            return LoadCore();
    }

    static AppSettings LoadCore()
    {
        try
        {
            if (!File.Exists(AppPaths.SettingsFile))
                return new AppSettings();

            using var stream = new FileStream(AppPaths.SettingsFile, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            string json = reader.ReadToEnd();
            AppSettings settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
            settings.Normalize();
            return settings;
        }
        catch
        {
            return new AppSettings();
        }
    }

    public static void Save(AppSettings settings)
    {
        lock (Gate)
        {
            settings.Normalize();
            Directory.CreateDirectory(AppPaths.DataDirectory);
            string json = JsonSerializer.Serialize(settings, JsonOptions);
            string temporary = AppPaths.SettingsFile + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporary, json);
                if (File.Exists(AppPaths.SettingsFile))
                    File.Replace(temporary, AppPaths.SettingsFile, null);
                else
                    File.Move(temporary, AppPaths.SettingsFile);
            }
            finally
            {
                if (File.Exists(temporary))
                    File.Delete(temporary);
            }
        }
    }
}
