using System.Security.Cryptography;
using System.Text;

namespace ButtonFly.Core;

public sealed class SettingsStore
{
    public string FilePath { get; }
    public string BackupPath => FilePath + ".bak";
    public SettingsStore(string directory) { FilePath = Path.Combine(directory, "settings.json"); }
    public string? Fingerprint() => File.Exists(FilePath) ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(FilePath))) : null;
    public Configuration Load(out string? warning)
    {
        warning = null;
        if (!File.Exists(FilePath)) return Configuration.CreateDefault();
        try { return ConfigJson.Parse(File.ReadAllText(FilePath, Encoding.UTF8)); }
        catch (Exception e) when (e is IOException or System.Text.Json.JsonException or InvalidDataException)
        {
            warning = "Could not load settings: " + e.Message;
            if (File.Exists(BackupPath))
            {
                try { var recovered = ConfigJson.Parse(File.ReadAllText(BackupPath, Encoding.UTF8)); warning += "\nUsing the backup."; return recovered; }
                catch (Exception backupError) when (backupError is IOException or System.Text.Json.JsonException or InvalidDataException) { }
            }
            warning += "\nUsing the default menu. The original file will not change until you save.";
            return Configuration.CreateDefault();
        }
    }
    public void Save(Configuration config, string? expectedFingerprint, bool checkConflict = true)
    {
        ConfigJson.Validate(config);
        if (checkConflict && Fingerprint() != expectedFingerprint) throw new IOException("The settings file changed while editing. Cancel and open it again.");
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        string temporary = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, ConfigJson.Serialize(config), new UTF8Encoding(false));
            if (File.Exists(FilePath))
            {
                // Do not replace a good backup with a corrupt external edit.
                bool valid;
                try { ConfigJson.Parse(File.ReadAllText(FilePath)); valid = true; }
                catch (Exception e) when (e is System.Text.Json.JsonException or InvalidDataException) { valid = false; }
                if (valid) File.Replace(temporary, FilePath, BackupPath);
                else
                {
                    File.Copy(FilePath, FilePath + ".invalid-" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff"), false);
                    File.Move(temporary, FilePath, true);
                }
            }
            else File.Move(temporary, FilePath);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
