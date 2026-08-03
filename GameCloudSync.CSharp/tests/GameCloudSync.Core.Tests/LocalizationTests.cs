using GameCloudSync.Core.Services;

namespace GameCloudSync.Core.Tests;

public class LocalizationTests
{
    [Fact]
    public void SwitchLanguage_ChangesStringsImmediately()
    {
        var old = Localization.Instance.Language;
        try
        {
            Localization.Instance.Language = Localization.English;
            Assert.Equal("⏸ Paused", Localization.T("Status.Paused"));
            Assert.Equal("Add Game", Localization.T("Btn.AddGame"));

            Localization.Instance.Language = Localization.Chinese;
            Assert.Equal("⏸ 已暂停", Localization.T("Status.Paused"));
            Assert.Equal("添加游戏", Localization.T("Btn.AddGame"));
        }
        finally
        {
            Localization.Instance.Language = old;
        }
    }

    [Fact]
    public void MissingKey_FallsBackToKey()
    {
        Assert.Equal("No.Such.Key", Localization.T("No.Such.Key"));
    }
}
