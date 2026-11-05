using System.Management;
using SoluPulse.Core.Corrections;

namespace SoluPulse.Platform.Windows;

/// <summary>
/// Points de restauration via la classe WMI SystemRestore (EF-COR-03). Windows n'en crée pas plus d'un
/// par 24 heures par défaut : dans ce cas l'appel réussit sans en créer un nouveau, et le point récent
/// reste disponible. La sauvegarde propre à chaque action reste donc nécessaire.
/// </summary>
public sealed class RestorePointService : IRestorePointService
{
    private const uint ModifySettings = 12;
    private const uint BeginSystemChange = 100;

    public Task<RestorePointResult> CreateAsync(string description, CancellationToken cancellationToken) => Task.Run(() =>
    {
        try
        {
            using var restore = new ManagementClass(new ManagementScope(@"\\.\root\default"), new ManagementPath("SystemRestore"), null);
            using var parameters = restore.GetMethodParameters("CreateRestorePoint");
            parameters["Description"] = description.Length > 64 ? description[..64] : description;
            parameters["RestorePointType"] = ModifySettings;
            parameters["EventType"] = BeginSystemChange;
            using var output = restore.InvokeMethod("CreateRestorePoint", parameters, null);
            var code = Convert.ToUInt32(output?["ReturnValue"] ?? 1u, System.Globalization.CultureInfo.InvariantCulture);
            return code switch
            {
                0 => new RestorePointResult(true, "Point de restauration créé."),
                1058 => new RestorePointResult(false, "la protection du système est désactivée sur ce PC"),
                _ => new RestorePointResult(false, $"code d'erreur {code}"),
            };
        }
        catch (ManagementException ex)
        {
            return new RestorePointResult(false, ex.Message);
        }
    }, cancellationToken);
}
