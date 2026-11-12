using System.Runtime.InteropServices;

namespace SoluPulse.Platform.Windows.Interop;

public enum SignatureState
{
    Valide,
    NonSigne,
    Invalide,
    Introuvable,
}

/// <summary>
/// Vérifie la signature numérique intégrée à un programme (WinVerifyTrust), sans interface et sans
/// contacter Internet. Les fichiers de Windows signés par catalogue apparaissent « non signés » :
/// l'appelant les écarte d'abord par leur emplacement.
/// </summary>
public static partial class Authenticode
{
    private const uint UiNone = 2;
    private const uint RevokeNone = 0;
    private const uint ChoiceFile = 1;
    private const uint StateVerify = 1;
    private const uint StateClose = 2;
    private const uint CacheOnlyUrlRetrieval = 0x1000;
    private const int NoSignature = unchecked((int)0x800B0100);
    private const int SubjectFormUnknown = unchecked((int)0x800B0003);
    private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    public static SignatureState Check(string path)
    {
        if (!File.Exists(path))
        {
            return SignatureState.Introuvable;
        }

        var pathPointer = Marshal.StringToHGlobalUni(path);
        var fileInfo = new FileInfoData { Size = (uint)Marshal.SizeOf<FileInfoData>(), FilePath = pathPointer };
        var filePointer = Marshal.AllocHGlobal(Marshal.SizeOf<FileInfoData>());
        try
        {
            Marshal.StructureToPtr(fileInfo, filePointer, false);
            var data = new TrustData
            {
                Size = (uint)Marshal.SizeOf<TrustData>(),
                UiChoice = UiNone,
                RevocationChecks = RevokeNone,
                UnionChoice = ChoiceFile,
                File = filePointer,
                StateAction = StateVerify,
                ProviderFlags = CacheOnlyUrlRetrieval,
            };
            var result = WinVerifyTrust(-1, GenericVerifyV2, ref data);
            data.StateAction = StateClose;
            _ = WinVerifyTrust(-1, GenericVerifyV2, ref data);
            return result switch
            {
                0 => SignatureState.Valide,
                NoSignature or SubjectFormUnknown => SignatureState.NonSigne,
                _ => SignatureState.Invalide,
            };
        }
        finally
        {
            Marshal.FreeHGlobal(filePointer);
            Marshal.FreeHGlobal(pathPointer);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileInfoData
    {
        public uint Size;
        public nint FilePath;
        public nint File;
        public nint KnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TrustData
    {
        public uint Size;
        public nint PolicyCallbackData;
        public nint SipClientData;
        public uint UiChoice;
        public uint RevocationChecks;
        public uint UnionChoice;
        public nint File;
        public uint StateAction;
        public nint StateData;
        public nint UrlReference;
        public uint ProviderFlags;
        public uint UiContext;
        public nint SignatureSettings;
    }

    [LibraryImport("wintrust.dll")]
    private static partial int WinVerifyTrust(nint window, in Guid action, ref TrustData data);
}

