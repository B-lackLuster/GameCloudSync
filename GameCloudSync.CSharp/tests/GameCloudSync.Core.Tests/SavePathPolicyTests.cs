using GameCloudSync.Core.Util;

namespace GameCloudSync.Core.Tests;

public class SavePathPolicyTests
{
    private static readonly string[] Profile = [@"C:\Users\Me", @"C:\Users\Me\Documents"];

    private static SavePathError Check(string? path, string cloudRoot = "", string game = "Game",
        IEnumerable<string>? protectedFolders = null) =>
        SavePathPolicy.Validate(path, game, cloudRoot, protectedFolders ?? Profile);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void EmptyPath_IsRejected(string? path)
    {
        Assert.Equal(SavePathError.Empty, Check(path));
    }

    [Theory]
    [InlineData(@"Saves\Game")]
    [InlineData(@".\Saves")]
    [InlineData(@"\Saves")]
    [InlineData(@"C:Saves")]
    [InlineData(@"..\..\Saves")]
    public void RelativePath_IsRejected(string path)
    {
        Assert.Equal(SavePathError.NotAbsolute, Check(path));
    }

    [Theory]
    [InlineData(@"C:\Games\a*b")]
    [InlineData(@"C:\Games\a?b")]
    [InlineData("C:\\Games\\a<b")]
    [InlineData("C:\\Games\\a|b")]
    public void PathWithCharactersWindowsForbids_IsRejected(string path)
    {
        Assert.Equal(SavePathError.Invalid, Check(path));
    }

    [Theory]
    [InlineData(@"C:\")]
    [InlineData(@"D:\")]
    [InlineData(@"d:/")]
    [InlineData(@"\\server\share")]
    [InlineData(@"\\server\share\")]
    public void WholeDriveOrShare_IsRejected(string path)
    {
        Assert.Equal(SavePathError.DriveRoot, Check(path));
    }

    [Theory]
    [InlineData(@"D:\Games\Elden Ring\Saves")]
    [InlineData(@"D:\Games\Elden Ring\Saves\")]
    [InlineData(@"D:/Games/Elden Ring/Saves")]
    [InlineData(@"\\nas\games\saves")]
    [InlineData(@"C:\Users\Me\Documents\My Games\Foo")]
    [InlineData(@"C:\Users\Me\AppData\Local\Foo\Saved")]
    [InlineData(@"C:\Users\Meow\Saves")]
    public void SpecificSaveFolder_IsAccepted(string path)
    {
        Assert.Equal(SavePathError.None, Check(path));
    }

    [Theory]
    [InlineData(@"C:\Users\Me")]
    [InlineData(@"c:\users\me\")]
    [InlineData(@"C:\Users\Me\Documents")]
    [InlineData(@"C:\Users")]
    public void ProtectedFolderOrItsParent_IsTooBroad(string path)
    {
        Assert.Equal(SavePathError.TooBroad, Check(path));
    }

    [Fact]
    public void BackupFolderOfTheGame_OrAnythingContainingIt_IsRejected()
    {
        const string cloud = @"D:\OneDrive\GameBackups";
        Assert.Equal(SavePathError.ContainsBackupFolder, Check(cloud, cloud));
        Assert.Equal(SavePathError.ContainsBackupFolder, Check(@"D:\OneDrive", cloud));
        Assert.Equal(SavePathError.ContainsBackupFolder, Check(@"D:\OneDrive\GameBackups\Game", cloud));
        Assert.Equal(SavePathError.ContainsBackupFolder, Check(@"d:\onedrive\gamebackups\GAME\", cloud));
    }

    [Fact]
    public void SaveFolderBesideTheBackups_IsFine()
    {
        const string cloud = @"D:\OneDrive\GameBackups";
        Assert.Equal(SavePathError.None, Check(@"D:\OneDrive\Documents\My Games\Game", cloud));
        Assert.Equal(SavePathError.None, Check(@"D:\OneDrive\GameBackups\Other\Saves", cloud));
        Assert.Equal(SavePathError.None, Check(@"D:\OneDrive\GameBackupsOld", cloud));
    }

    [Fact]
    public void WithoutAnOneDriveRoot_NothingIsCheckedAgainstIt()
    {
        Assert.Equal(SavePathError.None, Check(@"D:\OneDrive", ""));
    }

    [Fact]
    public void DefaultFolders_RefuseTheProfileDocumentsAndWindows_ButNotTheirSubfolders()
    {
        void AssertBroad(Environment.SpecialFolder folder)
        {
            var path = Environment.GetFolderPath(folder);
            if (path.Length == 0)
                return;
            Assert.Equal(SavePathError.TooBroad, SavePathPolicy.Validate(path, "Game", ""));
        }

        AssertBroad(Environment.SpecialFolder.UserProfile);
        AssertBroad(Environment.SpecialFolder.MyDocuments);
        AssertBroad(Environment.SpecialFolder.DesktopDirectory);
        AssertBroad(Environment.SpecialFolder.ApplicationData);
        AssertBroad(Environment.SpecialFolder.LocalApplicationData);
        AssertBroad(Environment.SpecialFolder.ProgramFiles);
        AssertBroad(Environment.SpecialFolder.Windows);

        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        if (documents.Length > 0)
            Assert.Equal(SavePathError.None,
                SavePathPolicy.Validate(Path.Combine(documents, "My Games", "Foo"), "Game", ""));
    }

    [Fact]
    public void TheAppsOwnFolder_AndItsParents_AreRefused()
    {
        var appDir = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
        Assert.Equal(SavePathError.TooBroad, SavePathPolicy.Validate(appDir, "Game", ""));
        Assert.Equal(SavePathError.TooBroad, SavePathPolicy.Validate(Path.GetDirectoryName(appDir)!, "Game", ""));
    }

    [Fact]
    public void Describe_SaysSomethingForEveryError()
    {
        foreach (var error in Enum.GetValues<SavePathError>().Where(e => e != SavePathError.None))
            Assert.NotEmpty(SavePathPolicy.Describe(error));
        Assert.Empty(SavePathPolicy.Describe(SavePathError.None));
    }
}
