using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace SoluPulse.Desktop.Services;

/// <summary>Son de test des haut-parleurs : un bip à gauche, puis à droite (WAV en mémoire, API winmm).</summary>
public static class SpeakerTest
{
    private const int SampleRate = 44_100;
    private const uint SndMemory = 0x0004;
    private const uint SndSync = 0x0000;

    public static Task PlayAsync(bool left, CancellationToken cancellationToken) => Task.Run(() =>
    {
        var wav = Tone(left, frequency: left ? 660 : 880, TimeSpan.FromSeconds(1.2));
        if (!PlaySound(wav, 0, SndMemory | SndSync))
        {
            throw new InvalidOperationException("Windows n'a pas pu jouer le son : aucun haut-parleur n'est peut-être branché.");
        }
    }, cancellationToken);

    /// <summary>WAV stéréo 16 bits : le son sur un seul canal, avec une montée et une descente douces.</summary>
    internal static byte[] Tone(bool left, double frequency, TimeSpan duration)
    {
        var samples = (int)(SampleRate * duration.TotalSeconds);
        var data = samples * 4;
        var wav = new byte[44 + data];
        var span = wav.AsSpan();
        "RIFF"u8.CopyTo(span);
        BinaryPrimitives.WriteInt32LittleEndian(span[4..], 36 + data);
        "WAVEfmt "u8.CopyTo(span[8..]);
        BinaryPrimitives.WriteInt32LittleEndian(span[16..], 16);
        BinaryPrimitives.WriteInt16LittleEndian(span[20..], 1);
        BinaryPrimitives.WriteInt16LittleEndian(span[22..], 2);
        BinaryPrimitives.WriteInt32LittleEndian(span[24..], SampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(span[28..], SampleRate * 4);
        BinaryPrimitives.WriteInt16LittleEndian(span[32..], 4);
        BinaryPrimitives.WriteInt16LittleEndian(span[34..], 16);
        "data"u8.CopyTo(span[36..]);
        BinaryPrimitives.WriteInt32LittleEndian(span[40..], data);
        for (var i = 0; i < samples; i++)
        {
            var envelope = Math.Min(1, Math.Min(i, samples - i) / (SampleRate * 0.05));
            var value = (short)(Math.Sin(2 * Math.PI * frequency * i / SampleRate) * 12_000 * envelope);
            var offset = 44 + (i * 4) + (left ? 0 : 2);
            BinaryPrimitives.WriteInt16LittleEndian(span[offset..], value);
        }

        return wav;
    }

    [DllImport("winmm.dll", EntryPoint = "PlaySoundW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PlaySound(byte[] sound, nint module, uint flags);
}

/// <summary>Niveau du micro : 3 secondes d'enregistrement mono 16 kHz (API waveIn), rien n'est gardé.</summary>
public static class MicrophoneTest
{
    private const int SampleRate = 16_000;
    private const int Seconds = 3;
    private const int WaveMapper = -1;
    private const int HeaderSize = 48;

    /// <summary>Le pic de niveau atteint, de 0 à 100 %.</summary>
    public static Task<int> RecordPeakAsync(CancellationToken cancellationToken) => Task.Run(async () =>
    {
        var format = new byte[18];
        BinaryPrimitives.WriteInt16LittleEndian(format, 1);
        BinaryPrimitives.WriteInt16LittleEndian(format.AsSpan(2), 1);
        BinaryPrimitives.WriteInt32LittleEndian(format.AsSpan(4), SampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(format.AsSpan(8), SampleRate * 2);
        BinaryPrimitives.WriteInt16LittleEndian(format.AsSpan(12), 2);
        BinaryPrimitives.WriteInt16LittleEndian(format.AsSpan(14), 16);

        if (waveInOpen(out var device, WaveMapper, format, 0, 0, 0) != 0)
        {
            throw new InvalidOperationException("Aucun micro disponible, ou l'accès au micro est refusé dans Paramètres > Confidentialité > Microphone.");
        }

        var length = SampleRate * 2 * Seconds;
        var buffer = Marshal.AllocHGlobal(length);
        var header = Marshal.AllocHGlobal(HeaderSize);
        try
        {
            // WAVEHDR : adresse du tampon, taille, octets enregistrés, données utilisateur, indicateurs…
            for (var i = 0; i < HeaderSize; i++)
            {
                Marshal.WriteByte(header, i, 0);
            }

            Marshal.WriteIntPtr(header, buffer);
            Marshal.WriteInt32(header, 8, length);
            Check(waveInPrepareHeader(device, header, HeaderSize));
            Check(waveInAddBuffer(device, header, HeaderSize));
            Check(waveInStart(device));
            await Task.Delay(TimeSpan.FromSeconds(Seconds + 0.3), cancellationToken).ConfigureAwait(false);
            _ = waveInStop(device);
            _ = waveInReset(device);

            var recorded = Math.Min(length, Marshal.ReadInt32(header, 12));
            var samples = new byte[recorded];
            Marshal.Copy(buffer, samples, 0, recorded);
            _ = waveInUnprepareHeader(device, header, HeaderSize);
            return Peak(samples);
        }
        finally
        {
            _ = waveInClose(device);
            Marshal.FreeHGlobal(header);
            Marshal.FreeHGlobal(buffer);
        }
    }, cancellationToken);

    internal static int Peak(ReadOnlySpan<byte> pcm16)
    {
        var peak = 0;
        for (var i = 0; i + 1 < pcm16.Length; i += 2)
        {
            peak = Math.Max(peak, Math.Abs((int)BinaryPrimitives.ReadInt16LittleEndian(pcm16[i..])));
        }

        return (int)Math.Round(peak * 100.0 / short.MaxValue);
    }

    private static void Check(int result)
    {
        if (result != 0)
        {
            throw new InvalidOperationException($"Le micro n'a pas pu enregistrer (code {result}).");
        }
    }

    [DllImport("winmm.dll")]
    private static extern int waveInOpen(out nint device, int deviceId, byte[] format, nint callback, nint instance, int flags);

    [DllImport("winmm.dll")]
    private static extern int waveInPrepareHeader(nint device, nint header, int size);

    [DllImport("winmm.dll")]
    private static extern int waveInUnprepareHeader(nint device, nint header, int size);

    [DllImport("winmm.dll")]
    private static extern int waveInAddBuffer(nint device, nint header, int size);

    [DllImport("winmm.dll")]
    private static extern int waveInStart(nint device);

    [DllImport("winmm.dll")]
    private static extern int waveInStop(nint device);

    [DllImport("winmm.dll")]
    private static extern int waveInReset(nint device);

    [DllImport("winmm.dll")]
    private static extern int waveInClose(nint device);
}

public sealed record DiskSpeedResult(double WriteMBps, double ReadMBps, string Verdict);

/// <summary>
/// Vitesse séquentielle du disque de Windows : écriture puis lecture d'un fichier de 512 Mo dans le
/// dossier temporaire, sans le cache de Windows (accès direct). Le fichier est supprimé à la fin.
/// </summary>
public static class DiskSpeedTest
{
    private const long FileSize = 512L * 1024 * 1024;
    private const int Block = 4 * 1024 * 1024;
    private const FileOptions NoBuffering = (FileOptions)0x20000000;

    public static Task<DiskSpeedResult> RunAsync(string diskType, IProgress<string> progress, CancellationToken cancellationToken) => Task.Run(() =>
    {
        var folder = Path.GetTempPath();
        if (new DriveInfo(Path.GetPathRoot(folder)!).AvailableFreeSpace < FileSize * 3)
        {
            throw new InvalidOperationException("Pas assez d'espace libre sur le disque pour le test (1,5 Go nécessaires).");
        }

        var path = Path.Combine(folder, $"solupulse-test-{Guid.NewGuid():N}.bin");
        unsafe
        {
            var memory = NativeMemory.AlignedAlloc(Block, 4096);
            try
            {
                var span = new Span<byte>(memory, Block);
                Random.Shared.NextBytes(span);
                progress.Report("Écriture de 512 Mo…");
                double write, read;
                using (var handle = File.OpenHandle(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, NoBuffering | FileOptions.WriteThrough))
                {
                    write = Measure(handle, span, isWrite: true, cancellationToken);
                }

                progress.Report("Lecture de 512 Mo…");
                using (var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.None, NoBuffering | FileOptions.SequentialScan))
                {
                    read = Measure(handle, span, isWrite: false, cancellationToken);
                }

                return new DiskSpeedResult(Math.Round(write), Math.Round(read), Verdict(diskType, read, write));
            }
            finally
            {
                NativeMemory.AlignedFree(memory);
                File.Delete(path);
            }
        }
    }, cancellationToken);

    private static double Measure(SafeFileHandle handle, Span<byte> block, bool isWrite, CancellationToken cancellationToken)
    {
        var watch = Stopwatch.StartNew();
        for (long offset = 0; offset < FileSize; offset += Block)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (isWrite)
            {
                RandomAccess.Write(handle, block, offset);
            }
            else if (RandomAccess.Read(handle, block, offset) == 0)
            {
                break;
            }
        }

        return FileSize / 1_048_576.0 / watch.Elapsed.TotalSeconds;
    }

    /// <summary>Comparaison aux vitesses habituelles : disque dur 80 à 200 Mo/s, SSD SATA vers 500, NVMe 1 500 et plus.</summary>
    internal static string Verdict(string diskType, double read, double write)
    {
        var (expected, label) = diskType switch
        {
            "NVMe" => (1_000, "un SSD NVMe"),
            "SSD" => (300, "un SSD SATA"),
            "HDD" => (70, "un disque dur"),
            _ => (0, "ce disque"),
        };

        if (expected == 0)
        {
            return $"Lecture {read:0} Mo/s, écriture {write:0} Mo/s.";
        }

        return read >= expected && write >= expected / 3
            ? $"Vitesse normale pour {label}."
            : $"Vitesse anormalement faible pour {label} (attendu au moins {expected} Mo/s en lecture). Le disque est peut-être presque plein, trop chaud, en fin de vie, ou branché sur un port lent.";
    }
}
