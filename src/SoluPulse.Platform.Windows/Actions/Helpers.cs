using System.Globalization;
using System.ServiceProcess;

namespace SoluPulse.Platform.Windows.Actions;

internal static class Services
{
    /// <summary>Arrête un service et indique s'il tournait, pour le relancer ensuite.</summary>
    public static async Task<bool> StopAsync(string name, CancellationToken cancellationToken)
    {
        using var service = new ServiceController(name);
        if (service.Status is ServiceControllerStatus.Stopped or ServiceControllerStatus.StopPending)
        {
            return false;
        }

        service.Stop();
        await WaitAsync(service, ServiceControllerStatus.Stopped, cancellationToken).ConfigureAwait(false);
        return true;
    }

    public static async Task StartAsync(string name, CancellationToken cancellationToken)
    {
        using var service = new ServiceController(name);
        if (service.Status is ServiceControllerStatus.Running or ServiceControllerStatus.StartPending)
        {
            return;
        }

        service.Start();
        await WaitAsync(service, ServiceControllerStatus.Running, cancellationToken).ConfigureAwait(false);
    }

    private static Task WaitAsync(ServiceController service, ServiceControllerStatus status, CancellationToken cancellationToken) =>
        Task.Run(() => service.WaitForStatus(status, TimeSpan.FromSeconds(60)), cancellationToken);
}

internal static class Files
{
    public static (int Count, long Bytes) Measure(IEnumerable<string> folders, TimeSpan? olderThan = null)
    {
        var limit = olderThan is { } age ? DateTime.UtcNow - age : DateTime.MaxValue;
        var count = 0;
        long bytes = 0;
        foreach (var file in Enumerate(folders))
        {
            if (file.LastWriteTimeUtc <= limit)
            {
                count++;
                bytes += file.Length;
            }
        }

        return (count, bytes);
    }

    /// <summary>Supprime les fichiers ; ceux qui sont en cours d'utilisation sont ignorés.</summary>
    public static (int Count, long Bytes, int Skipped) Delete(IEnumerable<string> folders, TimeSpan? olderThan = null)
    {
        var limit = olderThan is { } age ? DateTime.UtcNow - age : DateTime.MaxValue;
        int count = 0, skipped = 0;
        long bytes = 0;
        foreach (var file in Enumerate(folders).Where(f => f.LastWriteTimeUtc <= limit).ToList())
        {
            try
            {
                var length = file.Length;
                file.Attributes = FileAttributes.Normal;
                file.Delete();
                count++;
                bytes += length;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                skipped++;
            }
        }

        foreach (var folder in folders.Where(Directory.Exists))
        {
            DeleteEmptyDirectories(folder);
        }

        return (count, bytes, skipped);
    }

    public static string Size(long bytes) => bytes switch
    {
        >= 1L << 30 => string.Create(CultureInfo.GetCultureInfo("fr-FR"), $"{bytes / (double)(1L << 30):0.0} Go"),
        >= 1L << 20 => string.Create(CultureInfo.GetCultureInfo("fr-FR"), $"{bytes / (double)(1L << 20):0} Mo"),
        _ => string.Create(CultureInfo.GetCultureInfo("fr-FR"), $"{bytes / 1024d:0} Ko"),
    };

    /// <summary>Dossiers Temp de chaque profil utilisateur, plus celui de Windows.</summary>
    public static IEnumerable<string> TempFolders()
    {
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp");
        var usersRoot = Path.GetDirectoryName(Environment.GetFolderPath(Environment.SpecialFolder.CommonDocuments)) ?? @"C:\Users";
        if (!Directory.Exists(usersRoot))
        {
            yield break;
        }

        foreach (var profile in Directory.EnumerateDirectories(usersRoot))
        {
            var temp = Path.Combine(profile, "AppData", "Local", "Temp");
            if (Directory.Exists(temp))
            {
                yield return temp;
            }
        }
    }

    private static IEnumerable<FileInfo> Enumerate(IEnumerable<string> folders)
    {
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        foreach (var folder in folders.Where(Directory.Exists))
        {
            foreach (var file in new DirectoryInfo(folder).EnumerateFiles("*", options))
            {
                yield return file;
            }
        }
    }

    private static void DeleteEmptyDirectories(string root)
    {
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        foreach (var directory in Directory.EnumerateDirectories(root, "*", options).OrderByDescending(d => d.Length).ToList())
        {
            try
            {
                if (!Directory.EnumerateFileSystemEntries(directory).Any())
                {
                    Directory.Delete(directory);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Dossier en cours d'utilisation : ignoré.
            }
        }
    }
}
