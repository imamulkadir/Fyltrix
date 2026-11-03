using System.Security.Cryptography;
using Fyltrix.Core.Pdf;
using PdfSharp.Pdf;

namespace Fyltrix.Tests;

public class PdfCommentTests
{
    internal static string Fixture(TempDir temp, string name, string text, string author = "Fixture author", string subtype = "Text", int page = 1, bool rich = false)
    {
        using var document = new PdfDocument();
        for (int i = 0; i < 3; i++) document.AddPage();
        var annotation = new PdfDictionary(document);
        annotation.Elements.SetName("/Type", "/Annot");
        annotation.Elements.SetName("/Subtype", "/" + subtype);
        annotation.Elements.SetString(rich ? "/RC" : "/Contents", text);
        annotation.Elements.SetString("/T", author);
        annotation.Elements["/Rect"] = new PdfRectangle(new PdfSharp.Drawing.XRect(60, 600, 20, 20));
        document.Internals.AddObject(annotation);
        var annotations = new PdfArray(document);
        annotations.Elements.Add(annotation.Reference!);
        document.Pages[page-1].Elements["/Annots"] = annotations;
        string path = temp[name];
        document.Save(path);
        return path;
    }

    [Theory]
    [InlineData("Text", "Sticky note")] [InlineData("Highlight", "Highlight")]
    [InlineData("FreeText", "Free text")] [InlineData("Ink", "Ink")]
    [InlineData("Unrecognized", "Unrecognized (other annotation)")]
    public void Reads_comment_metadata_from_original_dictionaries_without_modifying_pdf(string subtype, string label)
    {
        using var temp = new TempDir();
        string path = Fixture(temp,"comment.pdf","A real comment\nSecond line","Author",subtype,2);
        var hash = SHA256.HashData(File.ReadAllBytes(path));
        var result = PdfService.ReadComments(path);
        var comment = Assert.Single(result.Items);
        Assert.Equal(2,comment.Page);
        Assert.Equal("Author",comment.Author);
        Assert.Equal(label,comment.Type);
        Assert.Equal("A real comment\nSecond line",comment.Text);
        Assert.Null(comment.Modified);
        Assert.Equal(3,result.PageHeights.Count);
        Assert.Equal(hash,SHA256.HashData(File.ReadAllBytes(path)));
    }

    [Fact]
    public void Rich_text_is_plain_text_and_external_entities_are_not_resolved()
    {
        using var temp = new TempDir();
        var rich = Fixture(temp,"rich.pdf","<body><p>Hello &amp; welcome</p><p>Second paragraph</p></body>",rich:true);
        Assert.Equal("Hello & welcome\nSecond paragraph",Assert.Single(PdfService.ReadComments(rich).Items).Text);
        temp.File("secret.txt","MUST NOT READ THIS FILE");
        var malicious = Fixture(temp,"unsafe.pdf",$"<!DOCTYPE body [<!ENTITY x SYSTEM '{new Uri(temp["secret.txt"]).AbsoluteUri}'>]><body><p>&x;</p></body>",rich:true);
        string text = Assert.Single(PdfService.ReadComments(malicious).Items).Text;
        Assert.DoesNotContain("MUST NOT READ",text);
        Assert.Contains("safely",text);
    }

    [Fact]
    public void Empty_comments_and_popup_records_are_not_fabricated_and_text_is_bounded()
    {
        using var temp = new TempDir();
        Assert.Empty(PdfService.ReadComments(Fixture(temp,"empty.pdf","",author:"")).Items);
        Assert.Empty(PdfService.ReadComments(Fixture(temp,"popup.pdf","parent owns the comment",subtype:"Popup")).Items);
        var result = PdfService.ReadComments(Fixture(temp,"long.pdf",new string('x',120000),author:""));
        Assert.Contains("truncated",Assert.Single(result.Items).Text);
        Assert.True(result.Items[0].Text.Length < 101000);
        Assert.DoesNotContain("Fixture author",result.Items[0].Heading);
    }
}
