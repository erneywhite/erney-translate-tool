using System.IO;
using ErneyTranslateTool.Data;
using Serilog;

namespace ErneyTranslateTool.Tests;

public class AppSettingsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ett-settings-" + Guid.NewGuid().ToString("N"));
    private readonly ILogger _log = new LoggerConfiguration().CreateLogger();

    public AppSettingsTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Save_KeepsPreviousFileAsBackup_AndLoadFallsBackToIt()
    {
        var first = new AppSettings(_dir, _log);
        first.Config.TargetLanguage = "DE";
        first.Save();
        first.Config.TargetLanguage = "FR";
        first.Save(); // previous file ("DE") becomes settings.json.bak

        // A crash mid-write used to leave this behind.
        File.WriteAllText(Path.Combine(_dir, "settings.json"), "{ \"TargetLang");

        var reloaded = new AppSettings(_dir, _log);
        reloaded.Load();
        Assert.Equal("DE", reloaded.Config.TargetLanguage);
    }

    [Fact]
    public void Save_LeavesNoTempFile()
    {
        var s = new AppSettings(_dir, _log);
        s.Save();
        s.Save();
        Assert.False(File.Exists(Path.Combine(_dir, "settings.json.tmp")));
        Assert.True(File.Exists(Path.Combine(_dir, "settings.json")));
    }

    [Fact]
    public void SetKey_SameValueTwice_DoesNotChangeCiphertext()
    {
        var s = new AppSettings(_dir, _log);
        s.SetGroqKey("gsk_test_value");
        var cipher = s.Config.EncryptedGroqKey;
        s.SetGroqKey("gsk_test_value");
        Assert.Equal(cipher, s.Config.EncryptedGroqKey);
        Assert.Equal("gsk_test_value", s.GetGroqKey());
        s.SetGroqKey("");
        Assert.Null(s.Config.EncryptedGroqKey);
    }
}
