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

    /// <summary>
    /// Déplace l'arborescence fichier par fichier, pour libérer la source au fur et à mesure. Un fichier
    /// utilisé par un programme reste à sa place ; s'il a déjà été copié, la copie est retirée pour ne
    /// jamais laisser de doublon. Un fichier déjà présent à la destination n'est pas écrasé.
    /// </summary>
    public static (int Moved, long Bytes, int Skipped) MoveTree(string source, string target)
    {
        int moved = 0, skipped = 0;
        long bytes = 0;
        if (!Directory.Exists(source))
        {
            return (0, 0, 0);
        }

        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        Directory.CreateDirectory(target);
        foreach (var directory in Directory.EnumerateDirectories(source, "*", options).ToList())
        {
            Directory.CreateDirectory(Path.Combine(target, Path.GetRelativePath(source, directory)));
        }

        foreach (var file in new DirectoryInfo(source).EnumerateFiles("*", options).ToList())
        {
            var destination = Path.Combine(target, Path.GetRelativePath(source, file.FullName));
            if (File.Exists(destination))
            {
                skipped++;
                continue;
            }

            var length = file.Length;
            try
            {
                File.Move(file.FullName, destination);
                moved++;
                bytes += length;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Copié mais pas supprimé (fichier ouvert en lecture) : la copie est retirée.
                if (File.Exists(file.FullName) && File.Exists(destination))
                {
                    TryDelete(destination);
                }

                skipped++;
            }
        }

        DeleteEmptyDirectories(source);
        return (moved, bytes, skipped);
    }

    /// <summary>Taille totale des fichiers d'un dossier, sans suivre les liens.</summary>
    public static long SizeOf(string folder) => Measure([folder]).Bytes;

    /// <summary>Identifiant court et stable d'un chemin, envoyé à l'application à la place du chemin lui-même.</summary>
    public static string Id(string path) =>
        Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(path.ToUpperInvariant())))[..16];

    public static bool TryDelete(string path)
    {
        try
        {
            File.SetAttributes(path, FileAttributes.Normal);
            File.Delete(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
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
        foreach (var profile in StartupInventory.UserProfilePaths())
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

    internal static void DeleteEmptyDirectories(string root)
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

internal static class Drives
{
    public static string SystemRoot => Path.GetPathRoot(Environment.SystemDirectory)!;

    public static bool IsOnSystemDrive(string path) =>
        string.Equals(Path.GetPathRoot(Path.GetFullPath(path)), SystemRoot, StringComparison.OrdinalIgnoreCase);

    public static long FreeBytes(string root) => new DriveInfo(root).AvailableFreeSpace;

    /// <summary>
    /// Les autres disques fixes du PC, au format NTFS ou ReFS : ces formats gardent les droits d'accès
    /// de l'utilisateur et acceptent les fichiers de plus de 4 Go.
    /// </summary>
    public static IReadOnlyList<DriveInfo> OtherFixedDrives(long minimumFree) => DriveInfo.GetDrives()
        .Where(d =>
        {
            try
            {
                return d.DriveType == DriveType.Fixed
                    && d.IsReady
                    && !string.Equals(d.RootDirectory.FullName, SystemRoot, StringComparison.OrdinalIgnoreCase)
                    && d.DriveFormat is "NTFS" or "ReFS"
                    && d.AvailableFreeSpace >= minimumFree;
            }
            catch (IOException)
            {
                return false;
            }
        })
        .ToList();
}
