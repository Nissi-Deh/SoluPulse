using System.Diagnostics;
using System.Text.Json;
using SoluPulse.Core.Audit;
using SoluPulse.Core.Corrections;
using SoluPulse.Core.Updates;
using SoluPulse.Platform.Windows;
using SoluPulse.Platform.Windows.Interop;

namespace SoluPulse.Agent.Monitoring;

public sealed record UpdateState(ReleaseInfo? Available, string ListsDescription, string? LastError);

/// <summary>
/// Une fois par jour : nouvelle version de SoluPulse sur GitHub, et nouvelles listes de détection signées.
/// Aucune donnée n'est envoyée : seules deux pages publiques sont lues. L'installation automatique n'est
/// permise que pour un installeur signé par l'éditeur attendu ; sinon l'utilisateur est seulement prévenu.
/// </summary>
public sealed class UpdateService(
    IHttpClientFactory httpFactory,
    IAuditLog audit,
    TimeProvider time,
    ILogger<UpdateService> logger) : BackgroundService
{
    public const string Repository = "Nissi-Deh/SoluPulse";

    /// <summary>Éditeur exigé dans la signature de l'installeur pour l'installer sans intervention.</summary>
    public const string TrustedPublisher = "SignPath Foundation";

    private static readonly string Folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "SoluPulse");
    private static readonly Uri ListsUrl = new($"https://raw.githubusercontent.com/{Repository}/main/data/{ThreatLists.FileName}");
    private static readonly Uri ReleasesUrl = new($"https://api.github.com/repos/{Repository}/releases?per_page=10");

    private UpdateState _state = new(null, Describe(ThreatLists.Current), null);

    public UpdateState State => Volatile.Read(ref _state);

    public static Version Current => typeof(UpdateService).Assembly.GetName().Version ?? new Version(0, 0);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        LoadCachedLists();
        try
        {
            await Task.Delay(TimeSpan.FromMinutes(15), time, stoppingToken).ConfigureAwait(false);
            using var timer = new PeriodicTimer(TimeSpan.FromHours(24), time);
            do
            {
                await CheckAsync(stoppingToken).ConfigureAwait(false);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            // Arrêt du service.
        }
    }

    public async Task CheckAsync(CancellationToken cancellationToken)
    {
        string? error = null;
        try
        {
            await UpdateListsAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            error = "listes de détection injoignables";
            logger.LogInformation("Listes de détection injoignables : {Message}", ex.Message);
        }

        ReleaseInfo? release = null;
        try
        {
            release = await LatestReleaseAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or IOException)
        {
            error = "GitHub injoignable (réseau bloqué ?)";
            logger.LogInformation("Vérification des mises à jour impossible : {Message}", ex.Message);
        }

        Volatile.Write(ref _state, new UpdateState(release is { } r && r.Version > Current ? r : null, Describe(ThreatLists.Current), error));
    }

    /// <summary>Télécharge l'installeur, exige une signature valide de l'éditeur attendu, puis lance l'installation.</summary>
    public async Task<(bool Started, string Message)> InstallAsync(CallerIdentity caller, CancellationToken cancellationToken)
    {
        if (State.Available is not { MsiUrl: { } url } release)
        {
            return (false, "Aucune nouvelle version à installer.");
        }

        var folder = Path.Combine(Folder, "maj");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, $"SoluPulse-{release.Version}-x64.msi");
        using (var http = Client())
        {
            await using var source = await http.GetStreamAsync(new Uri(url), cancellationToken).ConfigureAwait(false);
            await using var target = File.Create(path);
            await source.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
        }

        var signature = Authenticode.Check(path);
        var publisher = Authenticode.Publisher(path);
        if (signature != SignatureState.Valide || publisher is null || !publisher.Contains(TrustedPublisher, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(path);
            await AuditAsync(caller, release, "Refusée", $"installeur non signé par {TrustedPublisher}").ConfigureAwait(false);
            return (false, $"L'installeur de la version {release.Version} n'est pas signé par {TrustedPublisher} : SoluPulse ne l'installe pas lui-même. Téléchargez-le depuis la page de la version.");
        }

        Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "msiexec.exe"))
        {
            ArgumentList = { "/i", path, "/qn", "/norestart" },
            UseShellExecute = false,
            CreateNoWindow = true,
        })?.Dispose();
        await AuditAsync(caller, release, "Lancée", path).ConfigureAwait(false);
        return (true, $"Installation de la version {release.Version} lancée : SoluPulse redémarre dans une minute.");
    }

    private async Task<ReleaseInfo?> LatestReleaseAsync(CancellationToken cancellationToken)
    {
        using var http = Client();
        using var request = new HttpRequestMessage(HttpMethod.Get, ReleasesUrl);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        return ReleaseFeed.Pick(document.RootElement);
    }

    private async Task UpdateListsAsync(CancellationToken cancellationToken)
    {
        using var http = Client();
        var content = await http.GetByteArrayAsync(ListsUrl, cancellationToken).ConfigureAwait(false);
        var signature = await http.GetStringAsync(new Uri(ListsUrl + ".sig"), cancellationToken).ConfigureAwait(false);
        if (ThreatLists.TryApply(content, signature, ThreatLists.PublicKeyPem, out var reason))
        {
            Directory.CreateDirectory(Folder);
            await File.WriteAllBytesAsync(Path.Combine(Folder, ThreatLists.FileName), content, cancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(Path.Combine(Folder, ThreatLists.SignatureName), signature, cancellationToken).ConfigureAwait(false);
            logger.LogInformation("Nouvelles listes de détection : {Reason}.", reason);
        }
        else
        {
            logger.LogInformation("Listes de détection inchangées : {Reason}.", reason);
        }
    }

    /// <summary>Au démarrage : la dernière liste reçue, de nouveau vérifiée.</summary>
    private void LoadCachedLists()
    {
        var file = Path.Combine(Folder, ThreatLists.FileName);
        var signature = Path.Combine(Folder, ThreatLists.SignatureName);
        try
        {
            if (File.Exists(file) && File.Exists(signature)
                && !ThreatLists.TryApply(File.ReadAllBytes(file), File.ReadAllText(signature), ThreatLists.PublicKeyPem, out var reason))
            {
                logger.LogInformation("Liste de détection en cache ignorée : {Reason}.", reason);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Liste de détection en cache illisible.");
        }

        Volatile.Write(ref _state, State with { ListsDescription = Describe(ThreatLists.Current) });
    }

    private HttpClient Client()
    {
        var http = httpFactory.CreateClient("mises-a-jour");
        http.Timeout = TimeSpan.FromSeconds(60);
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"SoluPulse/{Current.ToString(3)}");
        return http;
    }

    private Task AuditAsync(CallerIdentity caller, ReleaseInfo release, string result, string details)
    {
        var now = time.GetUtcNow();
        return audit.AppendAsync(new AuditEntry(now, "Mise à jour de SoluPulse", null, null, null, "Utilisateur", caller.UserName, now, result, $"{release.Version} : {details}"), CancellationToken.None);
    }

    private static string Describe(ThreatListData lists) =>
        $"Listes de détection : version {lists.Version} du {lists.Date.ToString("d MMMM yyyy", System.Globalization.CultureInfo.GetCultureInfo("fr-FR"))}.";
}
