using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace Fyltrix.Launcher;

/// <summary>Shell identity only; installer ownership, updater trust and profile identities are unchanged.</summary>
public sealed record WindowsAppIdentity(string Id, string Launcher, string DisplayName)
{
    public const string ProductionId = "Fyltrix.Desktop";
    public const string SandboxId = "Fyltrix.Desktop.Sandbox";
    public string RelaunchCommand => "\"" + Launcher + "\"";
    public string IconResource => Launcher + ",0";

    public static WindowsAppIdentity ForRoot(string root, bool development = false)
    {
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var launcher = Path.Combine(root, "Fyltrix.exe");
        if (!development)
        {
            // Bounded read; classification is cosmetic and grants no update/install authority.
            try
            {
                using var stream = File.OpenRead(Path.Combine(root, ".fyltrix-install.ini"));
                var bytes = new byte[4097];
                int length = stream.ReadAtLeast(bytes, bytes.Length, false);
                if (length <= 4096)
                {
                    string section = "";
                    var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (string line in Encoding.UTF8.GetString(bytes, 0, length).TrimStart('\uFEFF').Split('\n'))
                    {
                        string text = line.Trim();
                        if (text.StartsWith('[')) { section = text; continue; }
                        int equal = text.IndexOf('=');
                        if (section.Equals("[Installation]", StringComparison.OrdinalIgnoreCase) && equal > 0)
                            if (!values.TryAdd(text[..equal].Trim(), text[(equal + 1)..].Trim())) { values.Clear(); break; }
                    }
                    if (values.GetValueOrDefault("Schema") == "1")
                    {
                        if (values.GetValueOrDefault("AppId") == "Fyltrix.PerUser" && values.GetValueOrDefault("Mode") == "Production")
                            return new(ProductionId, launcher, "Fyltrix");
                        if (values.GetValueOrDefault("AppId") == "Fyltrix.Sandbox.PerUser" && values.GetValueOrDefault("Mode") == "Sandbox")
                            return new(SandboxId, launcher, "Fyltrix Installer Sandbox");
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        // Same copy groups together across payload updates; independent portable/development copies stay separate.
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(root.ToUpperInvariant())))[..16];
        return new("Fyltrix.Desktop." + (development ? "Development." : "Portable.") + hash, launcher,
            development ? "Fyltrix Development" : "Fyltrix Portable");
    }

    public static WindowsAppIdentity ForPayload(string directory, bool development = false) =>
        ForRoot(UpdateProtocol.PayloadRoot(directory) ?? directory, development);

    public int SetProcess() => SetCurrentProcessExplicitAppUserModelID(Id);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string appId);
}
