using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Dwell.Services;

internal static class UpdateSignature
{
    private const uint NoSignature = 0x800b0100;
    private static readonly Guid AuthenticodePolicy = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    public static void Check(string installer)
    {
        var current = Environment.ProcessPath;
        var publisherKey = "Dwell.exe".Equals(Path.GetFileName(current), StringComparison.OrdinalIgnoreCase)
            ? ReadPublisherKey(current!) : null;
        var status = VerifyTrust(installer);
        if (status == NoSignature && publisherKey is null) return; // Migration from builds published before signing.
        if (status != 0) throw new InvalidDataException($"Windows rejected the installer signature (0x{status:x8}).");
        var installerKey = ReadPublisherKey(installer);
        if (installerKey is null || publisherKey is not null && !CryptographicOperations.FixedTimeEquals(publisherKey, installerKey))
            throw new InvalidDataException("The installer was signed by a different publisher.");
    }

    private static byte[]? ReadPublisherKey(string path)
    {
        try
        {
#pragma warning disable SYSLIB0057 // X509CertificateLoader cannot extract certificates embedded in an executable.
            using var signer = X509Certificate.CreateFromSignedFile(path);
#pragma warning restore SYSLIB0057
            return SHA256.HashData(signer.GetPublicKey());
        }
        catch (CryptographicException) { return null; }
    }

    private static uint VerifyTrust(string path)
    {
        var file = new TrustFile { Size = (uint)Marshal.SizeOf<TrustFile>(), Path = path };
        var filePointer = Marshal.AllocHGlobal((int)file.Size);
        Marshal.StructureToPtr(file, filePointer, false);
        try
        {
            var policy = AuthenticodePolicy;
            var data = new TrustData
            {
                Size = (uint)Marshal.SizeOf<TrustData>(), UiChoice = 2, UnionChoice = 1, FileInfo = filePointer,
                StateAction = 1, ProviderFlags = 0x80
            };
            var status = WinVerifyTrust(-1, ref policy, ref data);
            data.StateAction = 2;
            WinVerifyTrust(-1, ref policy, ref data);
            return status;
        }
        finally
        {
            Marshal.DestroyStructure<TrustFile>(filePointer);
            Marshal.FreeHGlobal(filePointer);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct TrustFile { public uint Size; public string Path; public nint File, KnownSubject; }

    [StructLayout(LayoutKind.Sequential)]
    private struct TrustData
    {
        public uint Size;
        public nint PolicyCallbackData, SipClientData;
        public uint UiChoice, RevocationChecks, UnionChoice;
        public nint FileInfo;
        public uint StateAction;
        public nint StateData, UrlReference;
        public uint ProviderFlags, UiContext;
        public nint SignatureSettings;
    }

    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern uint WinVerifyTrust(nint window, ref Guid action, ref TrustData data);
}
