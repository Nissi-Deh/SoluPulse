using System.Buffers.Binary;
using System.Net;
using SoluPulse.Desktop.Services;
using SoluPulse.Desktop.ViewModels;

namespace SoluPulse.Desktop.Tests;

public sealed class ToolsTests
{
    [Theory]
    [InlineData(2_412_000, 1, "2,4 GHz")]
    [InlineData(2_437_000, 6, "2,4 GHz")]
    [InlineData(2_484_000, 14, "2,4 GHz")]
    [InlineData(5_805_000, 161, "5 GHz")]
    [InlineData(5_955_000, 1, "6 GHz")]
    public void ConvertsWifiFrequenciesToChannels(int kilohertz, int channel, string band) =>
        Assert.Equal((channel, band), WifiScanner.Channel(kilohertz));

    [Fact]
    public void RecommendsTheLeastCrowdedNonOverlappingChannel()
    {
        WifiNetwork[] networks =
        [
            new("Voisin 1", 1, "2,4 GHz", 90, false),
            new("Voisin 2", 2, "2,4 GHz", 80, false),
            new("Voisin 3", 6, "2,4 GHz", 70, false),
            new("Voisin 4", 11, "2,4 GHz", 20, false),
            new("Box", 6, "2,4 GHz", 95, true),
        ];

        Assert.Equal(11, WifiScanner.Recommend(networks));
        Assert.Null(WifiScanner.Recommend([new("Box", 36, "5 GHz", 90, true)]));
    }

    [Fact]
    public void SpeedTestVolumesAdaptToTheConnection()
    {
        Assert.Equal(1_000_000, SpeedTest.Size(0.5, 1_000_000, 25_000_000));
        Assert.Equal(6_250_000, SpeedTest.Size(10, 1_000_000, 25_000_000));
        Assert.Equal(25_000_000, SpeedTest.Size(500, 1_000_000, 25_000_000));
        Assert.Equal(8, SpeedTest.Mbps(1_000_000, TimeSpan.FromSeconds(1)));
    }

    [Theory]
    [InlineData(0.6, "très lente")]
    [InlineData(3, "lente")]
    [InlineData(12, "correcte")]
    [InlineData(80, "Bonne")]
    public void ExplainsTheSpeedInPlainWords(double download, string word) =>
        Assert.Contains(word, NetworkViewModel.Verdict(new SpeedResult(download, 1, 40, 0)), StringComparison.Ordinal);

    [Fact]
    public void RecognisesPrivateAddressesAndTheBox()
    {
        Assert.Equal("Box ou routeur", LanScanner.Kind([0x50, 0xC7, 0xBF, 1, 2, 3], gateway: true));
        Assert.Equal("Téléphone ou tablette (adresse privée)", LanScanner.Kind([0x5A, 0x11, 0x22, 1, 2, 3], gateway: false));
        Assert.Equal("TP-Link", LanScanner.Kind([0x50, 0xC7, 0xBF, 1, 2, 3], gateway: false));
        Assert.Equal("Fabricant inconnu", LanScanner.Kind([0x00, 0x12, 0x34, 1, 2, 3], gateway: false));
    }

    [Fact]
    public void ScansAtMostTheNeighbourhoodOfThePc()
    {
        var hosts = LanScanner.Hosts(IPAddress.Parse("10.90.111.14"), IPAddress.Parse("255.255.0.0")).ToList();

        Assert.Equal(253, hosts.Count);
        Assert.DoesNotContain(IPAddress.Parse("10.90.111.14"), hosts);
        Assert.True(LanScanner.SameNetwork(IPAddress.Parse("192.168.1.20"), IPAddress.Parse("192.168.1.5"), IPAddress.Parse("255.255.255.0")));
        Assert.False(LanScanner.SameNetwork(IPAddress.Parse("192.168.2.20"), IPAddress.Parse("192.168.1.5"), IPAddress.Parse("255.255.255.0")));
    }

    [Fact]
    public void DevicesAreRecognisedByASaltedFingerprint()
    {
        byte[] mac = [0x00, 0x11, 0x22, 0x33, 0x44, 0x55];

        Assert.Equal(LanScanner.Hash(mac, "sel"), LanScanner.Hash(mac, "sel"));
        Assert.NotEqual(LanScanner.Hash(mac, "sel"), LanScanner.Hash(mac, "autre"));
        Assert.DoesNotContain("001122", LanScanner.Hash(mac, "sel"), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheSpeakerToneOnlyPlaysOnOneSide()
    {
        var wav = SpeakerTest.Tone(left: true, 660, TimeSpan.FromSeconds(0.1));

        Assert.Equal("RIFF"u8.ToArray(), wav[..4]);
        var right = Enumerable.Range(0, (wav.Length - 44) / 4).Select(i => BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(44 + (i * 4) + 2))).ToList();
        var left = Enumerable.Range(0, (wav.Length - 44) / 4).Select(i => BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(44 + (i * 4)))).ToList();
        Assert.All(right, s => Assert.Equal(0, s));
        Assert.Contains(left, s => s != 0);
    }

    [Fact]
    public void MeasuresTheMicrophonePeak()
    {
        var pcm = new byte[8];
        BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(2), short.MinValue / 2);

        Assert.Equal(50, MicrophoneTest.Peak(pcm));
        Assert.Equal(0, MicrophoneTest.Peak(new byte[8]));
    }

    [Theory]
    [InlineData("NVMe", 1900, 900, true)]
    [InlineData("NVMe", 450, 400, false)]
    [InlineData("SSD", 520, 480, true)]
    [InlineData("HDD", 40, 30, false)]
    public void JudgesDiskSpeedAgainstItsType(string type, double read, double write, bool normal) =>
        Assert.Equal(normal, DiskSpeedTest.Verdict(type, read, write).StartsWith("Vitesse normale", StringComparison.Ordinal));

    [Fact]
    public void ForecastsAMonthOrNothing()
    {
        Assert.Null(ComponentCards.Forecast(null, 1825));
        Assert.Null(ComponentCards.Forecast(1825, 1825));
        Assert.Equal("dès maintenant", ComponentCards.Forecast(0, 1825));
        Assert.StartsWith("vers ", ComponentCards.Forecast(60, 1825), StringComparison.Ordinal);
    }
}
