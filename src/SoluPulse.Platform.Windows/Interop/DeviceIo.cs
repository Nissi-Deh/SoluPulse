using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace SoluPulse.Platform.Windows.Interop;

/// <summary>Page de santé NVMe (log page 02h), telle que définie par la norme NVMe.</summary>
public sealed record NvmeHealth(
    byte CriticalWarning,
    double TemperatureCelsius,
    byte AvailableSparePercent,
    byte AvailableSpareThresholdPercent,
    byte PercentageUsed,
    ulong PowerOnHours,
    ulong UnsafeShutdowns,
    ulong MediaErrors)
{
    public const int LogLength = 512;

    /// <summary>
    /// Bits de l'avertissement critique : 0 réserve épuisée, 1 température, 2 fiabilité dégradée,
    /// 3 lecture seule, 4 sauvegarde de la mémoire volatile en échec. Le bit de température s'allume un
    /// instant sous forte charge sans danger pour le disque : la chaleur a sa propre règle.
    /// </summary>
    public static bool IsCritical(byte criticalWarning) => (criticalWarning & 0b1_1101) != 0;

    /// <summary>
    /// Octet 0 : avertissement critique ; 1-2 : température en kelvins ; 3 : réserve ; 4 : seuil de
    /// réserve ; 5 : usure ; 128 : heures ; 144 : arrêts non sécurisés ; 160 : erreurs média. Les
    /// compteurs font 16 octets : les 8 premiers suffisent largement.
    /// </summary>
    public static NvmeHealth? Parse(ReadOnlySpan<byte> log)
    {
        if (log.Length < 176)
        {
            return null;
        }

        var kelvin = BinaryPrimitives.ReadUInt16LittleEndian(log[1..]);
        return new NvmeHealth(
            log[0],
            kelvin > 0 ? kelvin - 273 : double.NaN,
            log[3],
            log[4],
            log[5],
            BinaryPrimitives.ReadUInt64LittleEndian(log[128..]),
            BinaryPrimitives.ReadUInt64LittleEndian(log[144..]),
            BinaryPrimitives.ReadUInt64LittleEndian(log[160..]));
    }
}

/// <summary>
/// Requêtes directes aux disques et volumes. Les handles sont ouverts sans droit de lecture ni
/// d'écriture : ces requêtes ne lisent que des propriétés, jamais les données du disque.
/// </summary>
internal static unsafe partial class DeviceIo
{
    private const uint IoctlStorageQueryProperty = 0x002D1400;
    private const uint FsctlIsVolumeDirty = 0x00090078;
    private const uint ShareReadWrite = 3;
    private const uint OpenExisting = 3;
    private const int StorageDeviceProtocolSpecificProperty = 50;
    private const int ProtocolTypeNvme = 3;
    private const int NvmeDataTypeLogPage = 2;
    private const int NvmeLogPageHealth = 2;
    private const int ProtocolSpecificDataSize = 40;

    /// <summary>Lit la page de santé d'un disque NVMe ; <c>null</c> pour un disque SATA ou derrière un contrôleur RAID qui ne la transmet pas.</summary>
    public static NvmeHealth? ReadNvmeHealth(int diskNumber)
    {
        using var handle = Open($@"\\.\PhysicalDrive{diskNumber}");
        if (handle.IsInvalid)
        {
            return null;
        }

        // STORAGE_PROPERTY_QUERY (8 octets) suivi de STORAGE_PROTOCOL_SPECIFIC_DATA (40 octets) et de la page.
        var buffer = new byte[8 + ProtocolSpecificDataSize + NvmeHealth.LogLength];
        var span = buffer.AsSpan();
        BinaryPrimitives.WriteInt32LittleEndian(span, StorageDeviceProtocolSpecificProperty);
        BinaryPrimitives.WriteInt32LittleEndian(span[8..], ProtocolTypeNvme);
        BinaryPrimitives.WriteInt32LittleEndian(span[12..], NvmeDataTypeLogPage);
        BinaryPrimitives.WriteInt32LittleEndian(span[16..], NvmeLogPageHealth);
        BinaryPrimitives.WriteUInt32LittleEndian(span[20..], 0xFFFFFFFF);
        BinaryPrimitives.WriteInt32LittleEndian(span[24..], ProtocolSpecificDataSize);
        BinaryPrimitives.WriteInt32LittleEndian(span[28..], NvmeHealth.LogLength);

        if (!Control(handle, IoctlStorageQueryProperty, buffer, buffer, out var returned))
        {
            return null;
        }

        // En retour, STORAGE_PROTOCOL_DATA_DESCRIPTOR : 8 octets d'en-tête, puis la structure dont
        // ProtocolDataOffset (relatif à cette structure) indique où commence la page.
        var offset = 8 + BinaryPrimitives.ReadInt32LittleEndian(span[(8 + 16)..]);
        return offset > 0 && offset + NvmeHealth.LogLength <= Math.Min(returned, buffer.Length)
            ? NvmeHealth.Parse(span.Slice(offset, NvmeHealth.LogLength))
            : null;
    }

    /// <summary>
    /// Le volume est « sale » quand Windows a noté une incohérence du système de fichiers : une
    /// vérification du disque se lancera au prochain démarrage. <c>null</c> si la question échoue.
    /// </summary>
    public static bool? IsVolumeDirty(char driveLetter)
    {
        using var handle = Open($@"\\.\{driveLetter}:");
        if (handle.IsInvalid)
        {
            return null;
        }

        var output = new byte[4];
        return Control(handle, FsctlIsVolumeDirty, [], output, out _)
            ? (BinaryPrimitives.ReadUInt32LittleEndian(output) & 1) != 0
            : null;
    }

    private static SafeFileHandle Open(string path) => CreateFile(path, 0, ShareReadWrite, 0, OpenExisting, 0, 0);

    private static bool Control(SafeFileHandle handle, uint code, byte[] input, byte[] output, out int returned)
    {
        fixed (byte* inPtr = input)
        fixed (byte* outPtr = output)
        {
            return DeviceIoControl(handle, code, inPtr, input.Length, outPtr, output.Length, out returned, 0);
        }
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFile(string name, uint access, uint share, nint security, uint creation, uint flags, nint template);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeviceIoControl(SafeFileHandle device, uint code, byte* input, int inputLength, byte* output, int outputLength, out int returned, nint overlapped);
}
