using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SoluPulse.Desktop.Services;

namespace SoluPulse.Desktop.ViewModels;

/// <summary>
/// Page « Tests du matériel » : écran (pixels morts), clavier, haut-parleurs gauche et droite, micro,
/// caméra et vitesse du disque. Rien n'est envoyé ni enregistré : le son du micro est analysé en mémoire.
/// </summary>
public sealed partial class DeviceTestsViewModel(AgentClient client) : PageViewModel
{
    public override string Title => "Tests du matériel";

    [ObservableProperty]
    private string _speakerResult = "";

    [ObservableProperty]
    private string _microphoneResult = "";

    [ObservableProperty]
    private int _microphoneLevel;

    [ObservableProperty]
    private string _diskResult = "";

    [ObservableProperty]
    private string _diskStatus = "Écrit puis relit un fichier de 512 Mo, environ 10 à 60 secondes. Le fichier est supprimé à la fin.";

    [ObservableProperty]
    private string _cameraResult = "";

    /// <summary>Ouvre la fenêtre de test d'écran ou de clavier ; fournie par la vue.</summary>
    public Func<string, Task>? OpenTestWindow { get; set; }

    public override Task RefreshAsync() => Task.CompletedTask;

    [RelayCommand]
    private Task TestScreenAsync() => OpenTestWindow?.Invoke("ecran") ?? Task.CompletedTask;

    [RelayCommand]
    private Task TestKeyboardAsync() => OpenTestWindow?.Invoke("clavier") ?? Task.CompletedTask;

    [RelayCommand]
    private Task TestSpeakersAsync() => RunAsync(async () =>
    {
        SpeakerResult = "Écoutez : un son à gauche…";
        await SpeakerTest.PlayAsync(left: true, CancellationToken.None);
        SpeakerResult = "… puis un son à droite.";
        await SpeakerTest.PlayAsync(left: false, CancellationToken.None);
        SpeakerResult = "Vous avez entendu un son grave à gauche, puis un son aigu à droite ? Sinon, vérifiez le volume, le périphérique de sortie choisi dans Windows, ou le pilote audio.";
    });

    [RelayCommand]
    private Task TestMicrophoneAsync() => RunAsync(async () =>
    {
        MicrophoneLevel = 0;
        MicrophoneResult = "Parlez normalement pendant 3 secondes…";
        var peak = await MicrophoneTest.RecordPeakAsync(CancellationToken.None);
        MicrophoneLevel = peak;
        MicrophoneResult = peak switch
        {
            < 2 => "Aucun son capté : le micro est coupé, bloqué dans Paramètres > Confidentialité > Microphone, ou en panne.",
            < 10 => $"Son très faible ({peak} %) : rapprochez-vous, ou montez le volume du micro dans les paramètres de son de Windows.",
            > 95 => $"Son saturé ({peak} %) : baissez le volume du micro pour éviter une voix déformée.",
            _ => $"Le micro fonctionne (niveau maximal {peak} %).",
        };
    });

    [RelayCommand]
    private void TestCamera()
    {
        Process.Start(new ProcessStartInfo("microsoft.windows.camera:") { UseShellExecute = true })?.Dispose();
        CameraResult = "L'application Caméra de Windows s'ouvre : si l'image reste noire, vérifiez le cache de la caméra et l'accès dans Paramètres > Confidentialité > Caméra.";
    }

    [RelayCommand]
    private Task TestDiskAsync() => RunAsync(async () =>
    {
        DiskResult = "";
        var metrics = await client.Api.GetLiveMetricsAsync(AgentClient.Empty);
        var type = metrics.Metrics.FirstOrDefault(m => m.Metric == "disque.systeme.type")?.LastValue switch
        {
            1 => "HDD",
            2 => "SSD",
            3 => "NVMe",
            _ => "",
        };
        var progress = new Progress<string>(text => DiskStatus = text);
        var result = await DiskSpeedTest.RunAsync(type, progress, CancellationToken.None);
        DiskResult = $"Lecture : {result.ReadMBps:0} Mo/s · Écriture : {result.WriteMBps:0} Mo/s";
        DiskStatus = result.Verdict;
    });
}
