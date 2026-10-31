namespace Fyltrix.Tests;

public sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "FyltrixTests", Guid.NewGuid().ToString("N"));

    public TempDir() => Directory.CreateDirectory(Path);

    public string this[string relative] => System.IO.Path.Combine(Path, relative.Replace('/', '\\'));

    public string File(string relative, string content = "x")
    {
        var p = this[relative];
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(p)!);
        System.IO.File.WriteAllText(p, content);
        return p;
    }

    public string Dir(string relative)
    {
        var p = this[relative];
        Directory.CreateDirectory(p);
        return p;
    }

    public void Dispose()
    {
        try
        {
            // junctions first, so recursive delete never walks into their targets
            foreach (var d in Directory.EnumerateDirectories(Path, "*", SearchOption.AllDirectories).ToList())
                if ((System.IO.File.GetAttributes(d) & FileAttributes.ReparsePoint) != 0) Directory.Delete(d);
            Directory.Delete(Path, true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
