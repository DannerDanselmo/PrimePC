using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;

namespace PrimePC.Drivers;

/// <summary>
/// Confere a assinatura digital (Authenticode) de um arquivo com a mesma verificação do Windows (WinVerifyTrust) e
/// exige que o certificado seja da organização esperada. Um instalador só é aberto se passar nas duas verificações.
/// </summary>
public static class AuthenticodeVerifier
{
    public static SignatureCheck Verify(string path, string expectedOrganization)
    {
        int status = WinVerifyTrustFile(path);
        if (status != 0)
            return new SignatureCheck(false, null, $"A assinatura digital do arquivo não é válida ({StatusText(status)}).");

        string? organization;
        try
        {
#pragma warning disable SYSLIB0057 // Lê o certificado do signatário já validado acima; não há substituto para arquivos assinados.
            using var signer = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
#pragma warning restore SYSLIB0057
            organization = signer.SubjectName.EnumerateRelativeDistinguishedNames()
                .Where(rdn => rdn.GetSingleElementType().Value == "2.5.4.10") // O = organização
                .Select(rdn => rdn.GetSingleElementValue())
                .FirstOrDefault();
        }
        catch (Exception ex)
        {
            return new SignatureCheck(false, null, $"Não foi possível ler o certificado do arquivo: {ex.Message}");
        }

        if (!string.Equals(organization, expectedOrganization, StringComparison.Ordinal))
            return new SignatureCheck(false, organization, $"O arquivo é assinado por \"{organization ?? "desconhecido"}\", e não por {expectedOrganization}.");
        return new SignatureCheck(true, organization, $"Assinatura digital válida de {organization}.");
    }

    private static string StatusText(int status) => unchecked((uint)status) switch
    {
        0x800B0100 => "o arquivo não tem assinatura",
        0x800B0003 => "o arquivo não é um executável reconhecido",
        0x80096010 => "o arquivo foi alterado depois de assinado",
        0x800B0109 => "o certificado não é de uma autoridade confiável",
        0x800B0101 => "o certificado expirou",
        0x800B010C => "o certificado foi revogado",
        0x80092003 => "o arquivo está incompleto ou corrompido",
        var s => $"0x{s:X8}",
    };

    // ---------------- WinVerifyTrust ----------------

    private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11D0-8CC2-00C04FC295EE");

    private static int WinVerifyTrustFile(string path)
    {
        IntPtr pathPtr = Marshal.StringToHGlobalUni(path);
        IntPtr fileInfo = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustFileInfo>());
        try
        {
            Marshal.StructureToPtr(new WinTrustFileInfo
            {
                cbStruct = (uint)Marshal.SizeOf<WinTrustFileInfo>(),
                pcwszFilePath = pathPtr,
            }, fileInfo, false);

            var data = new WinTrustData
            {
                cbStruct = (uint)Marshal.SizeOf<WinTrustData>(),
                dwUIChoice = 2, // WTD_UI_NONE
                fdwRevocationChecks = 0, // WTD_REVOKE_NONE: não depende de acesso à internet
                dwUnionChoice = 1, // WTD_CHOICE_FILE
                pFile = fileInfo,
                dwStateAction = 1, // WTD_STATEACTION_VERIFY
                dwProvFlags = 0x10, // WTD_REVOCATION_CHECK_NONE
            };
            var action = GenericVerifyV2;
            int result = WinVerifyTrust(new IntPtr(-1), ref action, ref data);
            data.dwStateAction = 2; // WTD_STATEACTION_CLOSE
            WinVerifyTrust(new IntPtr(-1), ref action, ref data);
            return result;
        }
        finally
        {
            Marshal.FreeHGlobal(fileInfo);
            Marshal.FreeHGlobal(pathPtr);
        }
    }

    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern int WinVerifyTrust(IntPtr hwnd, ref Guid actionId, ref WinTrustData data);

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustFileInfo
    {
        public uint cbStruct;
        public IntPtr pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustData
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public IntPtr pFile;
        public uint dwStateAction;
        public IntPtr hWVTStateData;
        public IntPtr pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
        public IntPtr pSignatureSettings;
    }
}
