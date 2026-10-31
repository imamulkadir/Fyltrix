using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Fyltrix.Core.Templates;

/// <summary>An entry in the New menu. Built-ins can be hidden or reordered; custom ones can be added, edited and removed.</summary>
public sealed class FileTemplate
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    /// <summary>Includes the dot, e.g. ".md".</summary>
    public string Extension { get; set; } = "";
    public string Category { get; set; } = "General";
    /// <summary>Default file name without extension, e.g. "New Text Document".</summary>
    public string BaseName { get; set; } = "";
    /// <summary>Text content. {{name}} becomes the file name, {{identifier}} a code-safe version of it.</summary>
    public string? Content { get; set; }
    /// <summary>Embedded binary template (Office documents), e.g. "blank.docx".</summary>
    public string? Resource { get; set; }
    public bool Enabled { get; set; } = true;
    public bool IsBuiltIn { get; set; }
    public int Order { get; set; }
    public bool Utf8Bom { get; set; }
    public bool UnixNewlines { get; set; }

    /// <summary>True when the name must be known before the file is written (content depends on it).</summary>
    public bool NeedsNameFirst => Content != null && (Content.Contains("{{name}}") || Content.Contains("{{identifier}}"));

    public string DefaultFileName => (string.IsNullOrWhiteSpace(BaseName) ? "New " + Name : BaseName) + Extension;

    public FileTemplate Clone() => (FileTemplate)MemberwiseClone();
}

/// <summary>A file type Windows registered under HKCR\.ext\…\ShellNew (Explorer's New menu).</summary>
public sealed record ShellNewEntry(string Extension, string Name, string? TemplateFile, byte[]? Data, bool NullFile);

/// <summary>Built-in templates, the user's template list (%AppData%\FileLens\templates.json) and file creation.</summary>
public static class FileTemplates
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    public static string FilePath => Path.Combine(AppSettings.Folder, "templates.json");

    public static List<FileTemplate> BuiltIns()
    {
        var list = new List<FileTemplate>();
        int order = 0;
        void Add(string id, string category, string name, string ext, string baseName, string? content, string? resource = null, bool bom = false, bool lf = false) =>
            list.Add(new FileTemplate
            {
                Id = id, Category = category, Name = name, Extension = ext, BaseName = baseName, Content = content, Resource = resource,
                IsBuiltIn = true, Order = order++, Utf8Bom = bom, UnixNewlines = lf,
            });

        Add("txt", "General", "Text document", ".txt", "New Text Document", "");
        Add("md", "General", "Markdown", ".md", "New Markdown", "# Title\n\n");
        Add("csv", "General", "CSV", ".csv", "New CSV", "column1,column2\n", bom: true);
        Add("json", "General", "JSON", ".json", "New JSON", "{\n  \n}\n");
        Add("xml", "General", "XML", ".xml", "New XML", "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<root>\n</root>\n");
        Add("yaml", "General", "YAML", ".yaml", "New YAML", "# YAML\nkey: value\n", lf: true);
        Add("ini", "General", "INI settings", ".ini", "New Settings", "[section]\nkey=value\n");
        Add("log", "General", "Log file", ".log", "New Log", "");

        Add("html", "Web", "HTML page", ".html", "index",
            "<!doctype html>\n<html lang=\"en\">\n<head>\n  <meta charset=\"utf-8\">\n  <meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n  <title>{{name}}</title>\n</head>\n<body>\n\n</body>\n</html>\n");
        Add("css", "Web", "CSS stylesheet", ".css", "styles", "/* styles */\n");
        Add("js", "Web", "JavaScript", ".js", "script", "'use strict';\n\n");
        Add("jsx", "Web", "React component (JSX)", ".jsx", "Component",
            "export default function {{identifier}}() {\n  return (\n    <div>\n      {{identifier}}\n    </div>\n  );\n}\n", lf: true);
        Add("svg", "Web", "SVG image", ".svg", "New Image",
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"100\" height=\"100\" viewBox=\"0 0 100 100\">\n</svg>\n");

        Add("py", "Programming", "Python script", ".py", "script",
            "#!/usr/bin/env python3\n\n\ndef main():\n    pass\n\n\nif __name__ == \"__main__\":\n    main()\n", lf: true);
        Add("cs", "Programming", "C# class", ".cs", "Class1", "namespace MyApp;\n\npublic class {{identifier}}\n{\n}\n");
        Add("ps1", "Programming", "PowerShell script", ".ps1", "script", "[CmdletBinding()]\nparam()\n\n", bom: true);
        Add("sh", "Programming", "Shell script", ".sh", "script", "#!/usr/bin/env bash\nset -euo pipefail\n\n", lf: true);
        Add("sql", "Programming", "SQL script", ".sql", "query", "-- SQL\n\n");

        Add("docx", "Office", "Word document", ".docx", "New Microsoft Word Document", null, "blank.docx");
        Add("xlsx", "Office", "Excel workbook", ".xlsx", "New Microsoft Excel Worksheet", null, "blank.xlsx");
        Add("pptx", "Office", "PowerPoint presentation", ".pptx", "New Microsoft PowerPoint Presentation", null, "blank.pptx");
        return list;
    }

    /// <summary>The user's list: saved state merged with the current built-ins (new built-ins appear, removed ones vanish).</summary>
    public static List<FileTemplate> Load()
    {
        var builtIns = BuiltIns();
        List<FileTemplate>? saved = null;
        try
        {
            if (File.Exists(FilePath)) saved = JsonSerializer.Deserialize<List<FileTemplate>>(File.ReadAllText(FilePath), Json);
        }
        catch (JsonException) { saved = null; }
        catch (IOException) { saved = null; }
        if (saved == null) return builtIns;

        var result = new List<FileTemplate>();
        var byId = builtIns.ToDictionary(b => b.Id);
        foreach (var s in saved)
        {
            if (s.IsBuiltIn)
            {
                if (!byId.Remove(s.Id, out var b)) continue;
                b.Enabled = s.Enabled;
                b.Order = s.Order;
                b.Category = string.IsNullOrWhiteSpace(s.Category) ? b.Category : s.Category;
                result.Add(b);
            }
            else if (IsValidCustom(s)) result.Add(s);
        }
        int next = result.Count == 0 ? 0 : result.Max(r => r.Order) + 1;
        foreach (var b in byId.Values) { b.Order = next++; result.Add(b); }
        return result.OrderBy(t => t.Order).ToList();
    }

    public static void Save(IEnumerable<FileTemplate> templates)
    {
        Directory.CreateDirectory(AppSettings.Folder);
        var list = templates.Select((t, i) => { var c = t.Clone(); c.Order = i; if (c.IsBuiltIn) { c.Content = null; c.Resource = null; } return c; }).ToList();
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(list, Json));
        File.Move(tmp, FilePath, overwrite: true);
    }

    public static void RestoreDefaults()
    {
        if (File.Exists(FilePath)) File.Delete(FilePath);
    }

    private static bool IsValidCustom(FileTemplate t) => ValidateCustom(t) == null;

    /// <summary>Checks a user-defined template. Returns an error message or null.</summary>
    public static string? ValidateCustom(FileTemplate t)
    {
        if (string.IsNullOrWhiteSpace(t.Name)) return "Give the template a name.";
        if (!Regex.IsMatch(t.Extension ?? "", @"^\.[^\\/:*?""<>|\s.][^\\/:*?""<>|\s]{0,30}$")) return "The extension must look like .ext (no spaces or special characters).";
        var baseName = string.IsNullOrWhiteSpace(t.BaseName) ? "New " + t.Name : t.BaseName;
        return FileOps.ValidateName(baseName + t.Extension);
    }

    /// <summary>Turns a file name into an identifier for code templates: "my-button.jsx" → "MyButton".</summary>
    public static string ToIdentifier(string stem)
    {
        var parts = Regex.Split(stem, @"[^A-Za-z0-9]+").Where(p => p.Length > 0).ToList();
        var id = string.Concat(parts.Select(p => char.ToUpperInvariant(p[0]) + p.Substring(1)));
        if (id.Length == 0) return "Component";
        return char.IsDigit(id[0]) ? "_" + id : id;
    }

    public static byte[] Render(FileTemplate t, string fileName)
    {
        if (t.Resource != null)
        {
            using var s = typeof(FileTemplates).Assembly.GetManifestResourceStream("Templates/" + t.Resource)
                          ?? throw new FileNotFoundException($"Built-in template '{t.Resource}' is missing from the application.");
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            return ms.ToArray();
        }
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var text = (t.Content ?? "").Replace("\r\n", "\n")
            .Replace("{{name}}", stem)
            .Replace("{{identifier}}", ToIdentifier(stem));
        if (!t.UnixNewlines) text = text.Replace("\n", "\r\n");
        var bytes = Encoding.UTF8.GetBytes(text);
        return t.Utf8Bom ? Encoding.UTF8.GetPreamble().Concat(bytes).ToArray() : bytes;
    }

    /// <summary>Creates the file in <paramref name="dir"/>. Never overwrites: a "(2)" suffix is added when needed.</summary>
    public static string Create(string dir, FileTemplate t, string? fileName = null)
    {
        var name = fileName ?? t.DefaultFileName;
        var err = FileOps.ValidateName(name);
        if (err != null) throw new IOException(err);
        var path = FileOps.UniqueName(dir, name);
        var bytes = Render(t, Path.GetFileName(path));
        using (var fs = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None)) fs.Write(bytes);
        return path;
    }

    // ------------------------------------------------------------------ Windows ShellNew discovery

    /// <summary>
    /// File types Windows offers in Explorer's New menu (HKCR\.ext[\ProgID]\ShellNew). Extensions Fyltrix already
    /// has templates for are left out so the menu has no duplicates. Read-only; never writes to the registry.
    /// </summary>
    public static List<ShellNewEntry> DiscoverShellNew(IEnumerable<string> skipExtensions)
    {
        var skip = new HashSet<string>(skipExtensions, StringComparer.OrdinalIgnoreCase) { ".lnk", ".library-ms", ".contact", ".url", ".zip", ".bmp" };
        var result = new List<ShellNewEntry>();
        try
        {
            foreach (var ext in Registry.ClassesRoot.GetSubKeyNames())
            {
                if (!ext.StartsWith('.') || skip.Contains(ext)) continue;
                using var extKey = Registry.ClassesRoot.OpenSubKey(ext);
                if (extKey == null) continue;
                var progId = extKey.GetValue(null) as string;
                RegistryKey? shellNew = null;
                if (!string.IsNullOrEmpty(progId)) shellNew = extKey.OpenSubKey(progId + "\\ShellNew");
                shellNew ??= extKey.OpenSubKey("ShellNew");
                if (shellNew == null) continue;
                using (shellNew)
                {
                    if (shellNew.GetValue("Command") != null && shellNew.GetValue("NullFile") == null && shellNew.GetValue("FileName") == null && shellNew.GetValue("Data") == null)
                        continue; // runs a program (wizard): not something we can do safely
                    var templateFile = shellNew.GetValue("FileName") as string;
                    if (templateFile != null) templateFile = ResolveShellNewTemplate(Environment.ExpandEnvironmentVariables(templateFile));
                    byte[]? data = shellNew.GetValue("Data") switch { byte[] b => b, string s => Encoding.Default.GetBytes(s), _ => null };
                    bool nullFile = shellNew.GetValue("NullFile") != null;
                    if (templateFile == null && data == null && !nullFile) continue;
                    var name = DisplayName(progId, ext, shellNew.GetValue("ItemName") as string);
                    result.Add(new ShellNewEntry(ext, name, templateFile, data, nullFile));
                }
            }
        }
        catch (System.Security.SecurityException) { }
        catch (UnauthorizedAccessException) { }
        return result.GroupBy(r => r.Extension, StringComparer.OrdinalIgnoreCase).Select(g => g.First())
            .OrderBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private static string? ResolveShellNewTemplate(string file)
    {
        if (Path.IsPathFullyQualified(file)) return File.Exists(file) ? file : null;
        foreach (var dir in new[]
                 {
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "ShellNew"),
                     Environment.GetFolderPath(Environment.SpecialFolder.Templates),
                     Environment.GetFolderPath(Environment.SpecialFolder.CommonTemplates),
                 })
        {
            var p = Path.Combine(dir, file);
            if (File.Exists(p)) return p;
        }
        return null;
    }

    private static string DisplayName(string? progId, string ext, string? itemName)
    {
        if (!string.IsNullOrEmpty(itemName) && !itemName.StartsWith('@')) return itemName;
        var type = ShellInfo.TypeName("x" + ext, false);
        if (!string.IsNullOrWhiteSpace(type)) return type;
        if (!string.IsNullOrEmpty(progId))
        {
            using var k = Registry.ClassesRoot.OpenSubKey(progId);
            if (k?.GetValue(null) is string friendly && friendly.Length > 0) return friendly;
        }
        return ext.TrimStart('.').ToUpperInvariant() + " file";
    }

    /// <summary>Creates a file the way Explorer would for a registered ShellNew entry.</summary>
    public static string CreateFromShellNew(string dir, ShellNewEntry e)
    {
        var path = FileOps.UniqueName(dir, "New " + e.Name + e.Extension);
        if (e.TemplateFile != null) File.Copy(e.TemplateFile, path, overwrite: false);
        else
        {
            using var fs = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            if (e.Data != null) fs.Write(e.Data);
        }
        return path;
    }
}
