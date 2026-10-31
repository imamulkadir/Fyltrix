using Fyltrix.Core;

namespace Fyltrix.Tests;

public class FileTypeNameTests
{
    [Theory]
    [InlineData(@"C:\a\report.pdf", "Microsoft Edge PDF Document", "PDF")]
    [InlineData(@"C:\a\budget.xlsx", "Microsoft Excel Worksheet", "Excel")]
    [InlineData(@"C:\a\old.XLS", "Microsoft Excel 97-2003 Worksheet", "Excel")]
    [InlineData(@"C:\a\letter.docx", "Microsoft Word Document", "Word")]
    [InlineData(@"C:\a\deck.pptx", "Microsoft PowerPoint Presentation", "PowerPoint")]
    [InlineData(@"C:\a\notes.txt", "Text Document", "Text")]
    [InlineData(@"C:\a\README.md", "Markdown Source File", "Markdown")]
    [InlineData(@"C:\a\data.json", "JSON Source File", "JSON")]
    [InlineData(@"C:\a\isins.csv", "Microsoft Excel Comma Separated Values File", "CSV")]
    [InlineData(@"C:\a\app.ts", "TS File", "TypeScript")]
    [InlineData(@"C:\a\setup.exe", "Application", "Application")]
    [InlineData(@"C:\a\TRC.lnk", "Shortcut", "Shortcut")]
    [InlineData(@"C:\a\photo.JPG", "JPG File", "JPEG")]
    [InlineData(@"C:\a\film.mkv", "MKV File", "MKV")]
    [InlineData(@"C:\a\pack.zip", "Compressed (zipped) Folder", "ZIP")]
    public void Common_types_get_short_plain_names(string path, string shell, string expected) =>
        Assert.Equal(expected, FileTypeNames.Short(path, false, shell));

    [Theory]
    [InlineData(@"C:\a\x.drawio", "DRAWIO File", "DRAWIO")]
    [InlineData(@"C:\a\x.pbix", "Microsoft Power BI Desktop Document", "Power BI Desktop")]
    [InlineData(@"C:\a\x.blend", "Blender File", "Blender")]
    [InlineData(@"C:\a\x.qqq", "", "QQQ")]
    [InlineData(@"C:\a\Makefile", "File", "File")]
    [InlineData(@"C:\a\noext", "", "File")]
    public void Other_types_lose_vendor_names_and_Document_or_File(string path, string shell, string expected) =>
        Assert.Equal(expected, FileTypeNames.Short(path, false, shell));

    [Fact]
    public void Folders_are_Folder() => Assert.Equal("Folder", FileTypeNames.Short(@"C:\a\Project", true, "File folder"));
}
