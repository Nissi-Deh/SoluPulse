using SoluPulse.Desktop.Services;

namespace SoluPulse.Desktop.Tests;

public sealed class AppToolsTests
{
    private const string FrenchOutput = """
           -    \    |
        Nom                                 ID                         Version   Disponible Source
        -------------------------------------------------------------------------------------------
        7-Zip 24.08 (x64)                   7zip.7zip                  24.08     25.01      winget
        Microsoft Visual C++ v14 Redist…    Microsoft.VCRedist.2015+.x64 14.51   14.52      winget
        Python Launcher                     Python.Launcher            < 3.11.8  3.15.2     winget
        3 mises à niveau disponibles.
        """;

    [Fact]
    public void ReadsTheWingetTableWhateverTheLanguage()
    {
        var upgrades = Winget.Parse(FrenchOutput);

        Assert.Equal(3, upgrades.Count);
        Assert.Equal(new WingetUpgrade("7-Zip 24.08 (x64)", "7zip.7zip", "24.08", "25.01"), upgrades[0]);
        Assert.Equal("Python.Launcher", upgrades[2].Id);
        Assert.Empty(Winget.Parse("Aucune mise à niveau disponible."));
    }

    [Fact]
    public void WindowsInstallerUninstallsInsteadOfRepairing()
    {
        Assert.Equal(("msiexec.exe", "/X{23170F69-40C1-2702-2408-000001000000}"), Uninstaller.Split("MsiExec.exe /I{23170F69-40C1-2702-2408-000001000000}"));
        Assert.Equal(("msiexec.exe", "/X{23170F69-40C1-2702-2408-000001000000}"), Uninstaller.Split("MsiExec.exe /X{23170F69-40C1-2702-2408-000001000000}"));
    }

    [Fact]
    public void OnlyExistingProgramsAreLaunchedToUninstall()
    {
        var notepad = Path.Combine(Environment.SystemDirectory, "notepad.exe");

        Assert.Equal((notepad, "/S"), Uninstaller.Split($"\"{notepad}\" /S"));
        Assert.Null(Uninstaller.Split(@"""C:\N'existe pas\uninstall.exe"" /S"));
        Assert.Null(Uninstaller.Split("uninstall.exe /S"));
        Assert.Null(Uninstaller.Split(""));
    }
}
