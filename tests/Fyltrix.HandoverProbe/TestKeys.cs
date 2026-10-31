using System;
using System.IO;
using System.Security.Cryptography;

internal static class TestKeys
{
    internal static int Create(string path)
    {
        path = Path.GetFullPath(path);
        if (!Path.GetFileName(path).StartsWith("Fyltrix-UpdateKey-", StringComparison.Ordinal) || Directory.Exists(path)) return 2;
        for (var ancestor = Directory.GetParent(path); ancestor != null; ancestor = ancestor.Parent)
            if (Directory.Exists(Path.Combine(ancestor.FullName, ".git")) || File.Exists(Path.Combine(ancestor.FullName, ".git"))) return 2;
        Directory.CreateDirectory(path);
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        File.WriteAllText(Path.Combine(path, "private-test.pem"), key.ExportPkcs8PrivateKeyPem());
        File.WriteAllText(Path.Combine(path, "public-test.txt"), Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()));
        return 0;
    }
}
