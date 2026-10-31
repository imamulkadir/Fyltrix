using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Fyltrix.Updates;

// Explicit owner/developer commands. Not referenced by or shipped with the WPF application.
try
{
    if (args.Length == 3 && args[0] == "create-signing-key")
    {
        if (!OperatingSystem.IsWindows()) throw new InvalidOperationException("Windows signing ACLs are required.");
        Console.WriteLine(JsonSerializer.Serialize(SigningKeySetup.Create(args[1], args[2])));
        return 0;
    }
    if (args.Length == 3 && args[0] == "download")
    {
        var current = ReleaseVersion.Parse(args[1]) ?? throw new ArgumentException("Invalid current version.");
        using var http = GitHubReleaseClient.CreateHttpClient();
        var client = new GitHubReleaseClient(http);
        var check = await client.CheckAsync(current);
        if (check.Status != CheckStatus.Available)
        {
            Console.WriteLine(JsonSerializer.Serialize(new { check = check.Status.ToString(), staged = false, installed = false }));
            return check.Status is CheckStatus.NoRelease or CheckStatus.UpToDate ? 0 : 2;
        }
        var result = await new UpdateDownloader(client).DownloadAsync(check.Release!, current, ReleasePolicy.ZipName(check.Release!.Version.Text), args[2]);
        Console.WriteLine(JsonSerializer.Serialize(new { download = result.Failure.ToString(), verification = result.Verification.ToString(),
            directory = result.Staged?.Directory, installed = false }));
        return result.Failure == DownloadFailure.None ? 0 : 2;
    }
    if (args.Length == 8 && args[0] == "stage-local")
    {
        var current = ReleaseVersion.Parse(args[1]) ?? throw new ArgumentException("Invalid current version.");
        var release = ReleaseInfo.Parse(await ReadSmall(args[2], ReleasePolicy.MaxMetadataBytes)) ?? throw new ArgumentException("Invalid metadata.");
        var manifest = await ReadSmall(args[3], ReleasePolicy.MaxManifestBytes);
        var signature = await ReadSmall(args[4], ReleasePolicy.MaxSignatureBytes);
        using var http = GitHubReleaseClient.CreateHttpClient();
        var result = await new UpdateDownloader(new GitHubReleaseClient(http)).StageLocalAsync(release, current, manifest, signature,
            args[5], await ReadKey(args[6]), args[7]);
        Console.WriteLine(JsonSerializer.Serialize(new { download = result.Failure.ToString(), verification = result.Verification.ToString(),
            directory = result.Staged?.Directory, installed = false, trust = "Explicit offline test/owner key; not production pinning" }));
        return result.Failure == DownloadFailure.None ? 0 : 2;
    }
    if (args.Length is 3 or 4 && args[0] == "prepare")
    {
        string[]? testKeys = args.Length == 4 ? [await ReadKey(args[3])] : null;
        var stage = await UpdateHandover.LoadStageAsync(args[1], testKeys) ?? throw new ArgumentException("Untrusted or invalid staged package.");
        var result = await UpdateHandover.PrepareAsync(stage, args[2]);
        Console.WriteLine(JsonSerializer.Serialize(new { handover = result.Failure.ToString(), request = result.Preparation?.RequestPath, installed = false }));
        return result.Failure == HandoverFailure.None ? 0 : 2;
    }
    if (args.Length is 2 or 3 && args[0] == "wait")
    {
        string[]? testKeys = args.Length == 3 ? [await ReadKey(args[2])] : null;
        var result = await UpdateHandover.WaitForReadyAsync(args[1], explicitTestKeys: testKeys);
        using var ready = result.Ready;
        Console.WriteLine(JsonSerializer.Serialize(new { handover = result.Failure.ToString(), ready = ready != null, installed = false }));
        return result.Failure == HandoverFailure.None ? 0 : 2;
    }
    if (args.Length is 2 or 3 && args[0] == "check")
    {
        var current = ReleaseVersion.Parse(args[1]) ?? throw new ArgumentException("Invalid current version.");
        ReleaseCheck result;
        ManifestVerification? verification = null;
        if (args.Length == 3)
        {
            var release = ReleaseInfo.Parse(await ReadSmall(args[2], ReleasePolicy.MaxMetadataBytes));
            result = release == null ? new(CheckStatus.InvalidMetadata) : new(
                release.Version.CompareTo(current) > 0 ? CheckStatus.Available : CheckStatus.UpToDate, release);
        }
        else
        {
            using var http = GitHubReleaseClient.CreateHttpClient();
            var client = new GitHubReleaseClient(http);
            result = await client.CheckAsync(current);
            if (result.Status == CheckStatus.Available) verification = await client.VerifyManifestAsync(result.Release!);
        }
        Console.WriteLine(JsonSerializer.Serialize(new { status = result.Status.ToString(), version = result.Release?.Version.Text,
            page = result.Release?.Page.AbsoluteUri, result.ETag, result.RetryAfter,
            verification = verification?.Failure.ToString() ?? "NotPerformed", installed = false }));
        return result.Status is CheckStatus.Available or CheckStatus.UpToDate or CheckStatus.NoRelease ? 0 : 2;
    }
    if (args.Length == 6 && args[0] == "verify")
    {
        var release = ReleaseInfo.Parse(await ReadSmall(args[1], ReleasePolicy.MaxMetadataBytes));
        if (release == null) throw new ArgumentException("Invalid release metadata.");
        var manifest = await ReadSmall(args[2], ReleasePolicy.MaxManifestBytes);
        var signature = await ReadSmall(args[3], ReleasePolicy.MaxSignatureBytes);
        var publicKey = await ReadKey(args[5]);
        var verified = SignedManifest.Verify(release, manifest, signature, [publicKey]);
        var failure = verified.Failure;
        if (verified.Verified != null)
            failure = await PackageVerifier.VerifyAsync(verified.Verified, Path.GetFileName(args[4]), args[4]);
        Console.WriteLine(JsonSerializer.Serialize(new { verification = failure.ToString(), installed = false,
            trust = "Explicit owner-supplied test key; not application pinning" }));
        return failure == VerificationFailure.None ? 0 : 2;
    }
    if (args.Length >= 6 && args[0] is "sign" or "sign-release")
    {
        var version = ReleaseVersion.Parse(args[1]) ?? throw new ArgumentException("Invalid release version.");
        if (version.IsPrerelease) throw new ArgumentException("Only stable releases are supported by this stage.");
        // Never accept release private keys inside any checkout or modify an existing output directory.
        var privateFile = Path.GetFullPath(args[2]);
        for (var parent = Directory.GetParent(privateFile); parent != null; parent = parent.Parent)
            if (Directory.Exists(Path.Combine(parent.FullName, ".git")) || File.Exists(Path.Combine(parent.FullName, ".git")))
                throw new ArgumentException("Keep the private signing key outside Git checkouts.");
        var publicKey = await ReadKey(args[3]);
        if (args[0] == "sign-release" && !ReleasePolicy.PinnedPublicKeys.Contains(publicKey, StringComparer.Ordinal))
        {
            Console.Error.WriteLine("Release signing refused: the public key is not pinned in this application build.");
            return 2;
        }
        using var key = ECDsa.Create();
        key.ImportFromPem(Encoding.UTF8.GetString(await ReadSmall(privateFile, 16 * 1024)));
        if (!SignedManifest.IsP256(key) || Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()) != publicKey)
            throw new ArgumentException("The P-256 private key must match the explicitly supplied public key.");
        string output = Path.GetFullPath(args[4]);
        if (Directory.Exists(output) || File.Exists(output)) throw new ArgumentException("Output already exists.");
        var files = new List<object>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in args[5..])
        {
            string name = Path.GetFileName(path);
            if (!ReleasePolicy.IsPackage(name, version.Text) || !names.Add(name)) throw new ArgumentException("Unsupported or duplicate package name.");
            await using var stream = File.OpenRead(path);
            if (stream.Length is <= 0 or > ReleasePolicy.MaxPackageBytes) throw new ArgumentException("Invalid package size.");
            files.Add(new { name, size = stream.Length, sha256 = Convert.ToHexString(await SHA256.HashDataAsync(stream)).ToLowerInvariant() });
        }
        byte[] manifest = JsonSerializer.SerializeToUtf8Bytes(new { schemaVersion = 1, product = "Fyltrix", version = version.Text, files });
        byte[] signature = key.SignData(manifest, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        if (!key.VerifyData(manifest, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
            throw new CryptographicException("Signature self-check failed.");
        Directory.CreateDirectory(output);
        await using (var target = new FileStream(Path.Combine(output, ReleasePolicy.ManifestName), FileMode.CreateNew)) await target.WriteAsync(manifest);
        await using (var target = new FileStream(Path.Combine(output, ReleasePolicy.SignatureName), FileMode.CreateNew))
            await target.WriteAsync(Encoding.ASCII.GetBytes(Convert.ToBase64String(signature)));
        Console.WriteLine("Signed manifest created locally. No upload, release, or installation performed.");
        return 0;
    }
    Console.Error.WriteLine("Commands:\n  check <installed-version> [local-release.json]\n  verify <release.json> <manifest.json> <signature> <package> <public-key.txt>\n  sign <version> <private-key.pem> <public-key.txt> <new-output-dir> <package> [package]\n  sign-release <version> <private-key.pem> <pinned-public-key.txt> <new-output-dir> <package> [package]\n  download <installed-version> <staging-root>\n  stage-local <installed-version> <release.json> <manifest> <signature> <package> <public-key.txt> <staging-root>\n  prepare <staging-directory> <layout-root> [test-public-key.txt]\n  wait <handover.json> [test-public-key.txt]");
    Console.Error.WriteLine("  create-signing-key <new-primary-directory> <new-backup-directory> (Windows owner setup only)");
    return 1;
}
catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or CryptographicException or HttpRequestException or InvalidOperationException or System.Security.SecurityException)
{
    // Do not dump file contents, private-key material, signed CDN query strings, or personal paths.
    Console.Error.WriteLine("Command failed: " + ex.GetType().Name + ". Check inputs and the documented limits.");
    return 2;
}

static async Task<byte[]> ReadSmall(string path, int limit)
{
    await using var file = File.OpenRead(path);
    return await GitHubReleaseClient.ReadBoundedAsync(file, limit, CancellationToken.None) ?? throw new ArgumentException("Input exceeds its size limit.");
}
static async Task<string> ReadKey(string path) => Encoding.ASCII.GetString(await ReadSmall(path, 4096)).Trim();
