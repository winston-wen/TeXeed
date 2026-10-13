using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace TeXeed.App.AI;

internal sealed class AiAccount
{
    public string ClientId { get; set; } = "";
    public string Subject { get; set; } = "";
    public string Email { get; set; } = "";
    public string AccessToken { get; set; } = "";
    public string RefreshToken { get; set; } = "";
    public string IdToken { get; set; } = "";
    public string Scope { get; set; } = "";
    public DateTimeOffset ExpiresAt { get; set; }
    public override string ToString() => $"{Email} · {ClientId[^Math.Min(6, ClientId.Length)..]}";
}
internal sealed class AiCredentials
{
    public string HostId { get; set; } = "urn:uuid:" + Guid.NewGuid();
    public string? ActiveClient { get; set; }
    public string ApiKey { get; set; } = "";
    public bool UseApiKey { get; set; }
    public string Model { get; set; } = "";
    public List<AiAccount> Accounts { get; set; } = [];
    [System.Text.Json.Serialization.JsonIgnore]
    public string ConnectionIdentity => UseApiKey ? "key:" + AiStorage.Hash(ApiKey) : "chatgpt:" + ActiveClient + ":" + Active?.Subject;
    [System.Text.Json.Serialization.JsonIgnore]
    public AiAccount? Active => Accounts.FirstOrDefault(a => a.ClientId == ActiveClient);
    private static string Location => Path.Combine(AiStorage.Home, "credentials.dpapi");
    internal static AiCredentials Load() => File.Exists(Location)
        ? JsonSerializer.Deserialize<AiCredentials>(Protect(File.ReadAllBytes(Location), false)) ?? new() : new();
    internal void Save()
    {
        Directory.CreateDirectory(AiStorage.Home);
        var data = JsonSerializer.SerializeToUtf8Bytes(this);
        var temp = Location + "." + Guid.NewGuid().ToString("N");
        try { File.WriteAllBytes(temp, Protect(data, true)); File.Move(temp, Location, true); }
        finally { CryptographicOperations.ZeroMemory(data); if (File.Exists(temp)) File.Delete(temp); }
    }
    // DPAPI CurrentUser. No machine-wide protection and no plaintext fallback.
    private static byte[] Protect(byte[] bytes, bool encrypt)
    {
        var input = new Blob { Size = bytes.Length, Data = Marshal.AllocHGlobal(bytes.Length) };
        Blob output = default;
        try
        {
            Marshal.Copy(bytes, 0, input.Data, bytes.Length);
            var success = encrypt ? CryptProtectData(ref input, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output)
                : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output);
            if (!success) throw new CryptographicException("无法访问 Windows 加密凭据存储。");
            var result = new byte[output.Size]; Marshal.Copy(output.Data, result, 0, result.Length); return result;
        }
        finally { Marshal.Copy(new byte[bytes.Length], 0, input.Data, bytes.Length); Marshal.FreeHGlobal(input.Data); if (output.Data != IntPtr.Zero) LocalFree(output.Data); }
    }
    [StructLayout(LayoutKind.Sequential)] private struct Blob { public int Size; public IntPtr Data; }
    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool CryptProtectData(ref Blob input, string? description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);
    [DllImport("crypt32.dll", SetLastError = true)] private static extern bool CryptUnprotectData(ref Blob input, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
    internal static async Task<FileStream> LockAsync(CancellationToken token)
    {
        Directory.CreateDirectory(AiStorage.Home);
        while (true)
        {
            token.ThrowIfCancellationRequested();
            try { return new FileStream(Path.Combine(AiStorage.Home, "credentials.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) { await Task.Delay(150, token); }
        }
    }
}
