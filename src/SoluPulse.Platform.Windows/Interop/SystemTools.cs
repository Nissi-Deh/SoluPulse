using System.Diagnostics;
using System.Globalization;
using System.Management;
using System.Text;

namespace SoluPulse.Platform.Windows.Interop;

public sealed record ToolResult(int ExitCode, string Output, bool TimedOut)
{
    public bool Succeeded => !TimedOut && ExitCode == 0;
}

/// <summary>
/// Lance un outil Windows de System32 avec des arguments fixes passés un par un (cahier des charges
/// §6.1) : aucun interpréteur de commandes, aucune chaîne construite à partir d'une saisie libre.
/// </summary>
public static class SystemTools
{
    private static readonly HashSet<string> AllowedTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "sfc.exe", "dism.exe", "defrag.exe", "fsutil.exe", "ipconfig.exe", "netsh.exe", "powercfg.exe", "reagentc.exe", "pnputil.exe",
    };

    static SystemTools()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public static async Task<ToolResult> RunAsync(string tool, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (!AllowedTools.Contains(tool))
        {
            throw new InvalidOperationException($"Outil non autorisé : {tool}");
        }

        var startInfo = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, tool))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = ConsoleEncoding,
            StandardErrorEncoding = ConsoleEncoding,
            WorkingDirectory = Environment.SystemDirectory,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException($"Impossible de lancer {tool}.");
        var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var error = process.StandardError.ReadToEndAsync(cancellationToken);
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(timeoutSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            process.Kill(entireProcessTree: true);
            return new ToolResult(-1, "Délai dépassé.", TimedOut: true);
        }

        var text = (await output.ConfigureAwait(false)) + (await error.ConfigureAwait(false));
        return new ToolResult(process.ExitCode, text.Replace("\0", string.Empty, StringComparison.Ordinal).Trim(), TimedOut: false);
    }

    /// <summary>Les outils console écrivent dans la page de code OEM (850 en français).</summary>
    private static Encoding ConsoleEncoding =>
        Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage);
}

public static class Wmi
{
    public static IReadOnlyList<ManagementBaseObject> Query(string scope, string query, TimeSpan? timeout = null)
    {
        var options = new System.Management.EnumerationOptions { Timeout = timeout ?? TimeSpan.FromSeconds(30), ReturnImmediately = false };
        using var searcher = new ManagementObjectSearcher(new ManagementScope(scope), new ObjectQuery(query), options);
        using var results = searcher.Get();
        return results.Cast<ManagementBaseObject>().ToList();
    }

    public static ManagementBaseObject? QueryFirst(string scope, string query)
    {
        var rows = Query(scope, query);
        return rows.Count > 0 ? rows[0] : null;
    }

    public static T? Get<T>(ManagementBaseObject item, string property)
    {
        var value = item[property];
        if (value is null)
        {
            return default;
        }

        return value is T typed ? typed : (T)Convert.ChangeType(value, typeof(T), CultureInfo.InvariantCulture);
    }
}
