using SoluPulse.Core.Corrections;

namespace SoluPulse.Platform.Windows.Actions;

internal static class Family
{
    public const string Cleanup = "Nettoyage et optimisation";
    public const string Space = "Espace disque";
    public const string Repair = "Réparations système";
    public const string Network = "Réseau";
    public const string Security = "Sécurité";
}

/// <summary>Fichiers temporaires de Windows et des utilisateurs, vieux de plus d'un jour.</summary>
public sealed class CleanTempAction : SystemActionBase
{
    private static readonly TimeSpan MinimumAge = TimeSpan.FromDays(1);

    public override ActionDescriptor Descriptor { get; } = new(
        ActionIds.CleanTemp, Family.Cleanup,
        "Supprimer les fichiers temporaires",
        "Supprime les fichiers temporaires de plus d'un jour laissés par Windows et les programmes. Les fichiers en cours d'utilisation sont ignorés.",
        ["Parcourir les dossiers Temp de Windows et des utilisateurs", "Supprimer les fichiers de plus de 24 heures"],
        RiskLevel.Faible, Reversible: false, RequiresReboot: false, TimeSpan.FromMinutes(2), Automatic: true);

    public override Task<ActionEstimate> EstimateAsync(ActionContext context, CancellationToken cancellationToken) => Task.Run(() =>
    {
        var (count, bytes) = Files.Measure(Files.TempFolders(), MinimumAge);
        return new ActionEstimate($"Environ {Files.Size(bytes)} libérés ({count} fichiers).");
    }, cancellationToken);

    public override Task<ActionOutcome> ExecuteAsync(ActionContext context, CancellationToken cancellationToken) => Task.Run(() =>
    {
        var (count, bytes, skipped) = Files.Delete(Files.TempFolders(), MinimumAge);
        return ActionOutcome.Ok($"{count} fichiers supprimés, {Files.Size(bytes)} libérés. {skipped} fichiers en cours d'utilisation ignorés.");
    }, cancellationToken);
}

/// <summary>Cache de téléchargement d'un service Windows, vidé service arrêté puis relancé.</summary>
public abstract class ServiceCacheCleanupAction(string serviceName, string folder) : SystemActionBase
{
    public override Task<ActionEstimate> EstimateAsync(ActionContext context, CancellationToken cancellationToken) => Task.Run(() =>
    {
        var (count, bytes) = Files.Measure([folder]);
        return new ActionEstimate($"Environ {Files.Size(bytes)} libérés ({count} fichiers).");
    }, cancellationToken);

    public override async Task<ActionOutcome> ExecuteAsync(ActionContext context, CancellationToken cancellationToken)
    {
        var wasRunning = await Services.StopAsync(serviceName, cancellationToken).ConfigureAwait(false);
        try
        {
            var (count, bytes, _) = await Task.Run(() => Files.Delete([folder]), cancellationToken).ConfigureAwait(false);
            return ActionOutcome.Ok($"{count} fichiers supprimés, {Files.Size(bytes)} libérés.");
        }
        finally
        {
            if (wasRunning)
            {
                await Services.StartAsync(serviceName, CancellationToken.None).ConfigureAwait(false);
            }
        }
    }
}

public sealed class CleanWindowsUpdateCacheAction() : ServiceCacheCleanupAction(
    "wuauserv", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "SoftwareDistribution", "Download"))
{
    public override ActionDescriptor Descriptor { get; } = new(
        ActionIds.CleanWindowsUpdateCache, Family.Cleanup,
        "Vider le cache de Windows Update",
        "Supprime les fichiers de mises à jour déjà téléchargés. Windows les retéléchargera si besoin.",
        ["Arrêter le service Windows Update", "Supprimer le contenu de SoftwareDistribution\\Download", "Relancer le service"],
        RiskLevel.Faible, Reversible: false, RequiresReboot: false, TimeSpan.FromMinutes(2));
}

public sealed class CleanDeliveryOptimizationCacheAction() : ServiceCacheCleanupAction(
    "DoSvc", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), @"ServiceProfiles\NetworkService\AppData\Local\Microsoft\Windows\DeliveryOptimization\Cache"))
{
    public override ActionDescriptor Descriptor { get; } = new(
        ActionIds.CleanDeliveryOptimizationCache, Family.Cleanup,
        "Vider le cache d'optimisation de la distribution",
        "Supprime les morceaux de mises à jour gardés pour être partagés avec d'autres PC.",
        ["Arrêter le service d'optimisation de la distribution", "Supprimer son cache", "Relancer le service"],
        RiskLevel.Faible, Reversible: false, RequiresReboot: false, TimeSpan.FromMinutes(1), Automatic: true);
}

/// <summary>Vide la corbeille de l'utilisateur qui valide, sur tous les disques fixes.</summary>
public sealed class EmptyRecycleBinAction : SystemActionBase
{
    public override ActionDescriptor Descriptor { get; } = new(
        ActionIds.EmptyRecycleBin, Family.Cleanup,
        "Vider la corbeille",
        "Supprime définitivement les fichiers de votre corbeille. Ils ne pourront pas être récupérés.",
        ["Supprimer le contenu de votre corbeille sur chaque disque"],
        RiskLevel.Faible, Reversible: false, RequiresReboot: false, TimeSpan.FromMinutes(1));

    public override Task<ActionEstimate> EstimateAsync(ActionContext context, CancellationToken cancellationToken) => Task.Run(() =>
    {
        var (count, bytes) = Files.Measure(Folders(context.Caller.UserSid));
        return new ActionEstimate($"{count} éléments, {Files.Size(bytes)}.");
    }, cancellationToken);

    public override Task<ActionOutcome> ExecuteAsync(ActionContext context, CancellationToken cancellationToken) => Task.Run(() =>
    {
        var (count, bytes, skipped) = Files.Delete(Folders(context.Caller.UserSid));
        return ActionOutcome.Ok($"Corbeille vidée : {count} fichiers, {Files.Size(bytes)} libérés." + (skipped > 0 ? $" {skipped} fichiers n'ont pas pu être supprimés." : ""));
    }, cancellationToken);

    private static IEnumerable<string> Folders(string sid)
    {
        if (!StartupInventory.IsUserSid(sid))
        {
            throw new ActionParameterException("Utilisateur inconnu.");
        }

        return DriveInfo.GetDrives()
            .Where(d => d.DriveType == DriveType.Fixed && d.IsReady)
            .Select(d => Path.Combine(d.RootDirectory.FullName, "$Recycle.Bin", sid));
    }
}
