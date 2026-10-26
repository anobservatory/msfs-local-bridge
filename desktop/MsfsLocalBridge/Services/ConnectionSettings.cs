using System.IO;
using System.Text.Json;

namespace MsfsLocalBridge.Services;

internal sealed class ConnectionSettings
{
    private readonly string _path;
    public bool AllowLanAccess { get; private set; }

    public ConnectionSettings(string path)
    {
        _path = path;
        try
        {
            AllowLanAccess = JsonSerializer.Deserialize<SavedSettings>(File.ReadAllText(path))?.AllowLanAccess ?? false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Missing or invalid settings default to local access only.
        }
    }

    public void Save(bool allowLanAccess)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temporaryPath = _path + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(new SavedSettings(allowLanAccess)));
        File.Move(temporaryPath, _path, overwrite: true);
        AllowLanAccess = allowLanAccess;
    }

    private sealed record SavedSettings(bool AllowLanAccess);
}
