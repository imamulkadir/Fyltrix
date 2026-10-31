// Regenerates and schema-validates src/Fyltrix/Templates/blank.{docx,xlsx,pptx}:
//   dotnet run --project tools/OfficeTemplates -- src/Fyltrix/Templates
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using W = DocumentFormat.OpenXml.Wordprocessing;
using S = DocumentFormat.OpenXml.Spreadsheet;
using P = DocumentFormat.OpenXml.Presentation;
using D = DocumentFormat.OpenXml.Drawing;

var outDir = args.Length > 0 ? args[0] : "out";
Directory.CreateDirectory(outDir);
var docx = Path.Combine(outDir, "blank.docx");
var xlsx = Path.Combine(outDir, "blank.xlsx");
var pptx = Path.Combine(outDir, "blank.pptx");
foreach (var f in new[] { docx, xlsx, pptx }) if (File.Exists(f)) File.Delete(f);

// ---------------- Word ----------------
using (var doc = WordprocessingDocument.Create(docx, WordprocessingDocumentType.Document))
{
    var main = doc.AddMainDocumentPart();
    main.Document = new W.Document(new W.Body(
        new W.Paragraph(),
        new W.SectionProperties(
            new W.PageSize { Width = 12240U, Height = 15840U },
            new W.PageMargin { Top = 1440, Right = 1440U, Bottom = 1440, Left = 1440U, Header = 720U, Footer = 720U, Gutter = 0U })));
    var styles = main.AddNewPart<StyleDefinitionsPart>();
    styles.Styles = new W.Styles(
        new W.DocDefaults(
            new W.RunPropertiesDefault(new W.RunPropertiesBaseStyle(
                new W.RunFonts { Ascii = "Calibri", HighAnsi = "Calibri", EastAsia = "Calibri", ComplexScript = "Calibri" },
                new W.FontSize { Val = "22" }, new W.FontSizeComplexScript { Val = "22" },
                new W.Languages { Val = "en-US", EastAsia = "en-US", Bidi = "ar-SA" })),
            new W.ParagraphPropertiesDefault(new W.ParagraphPropertiesBaseStyle(new W.SpacingBetweenLines { After = "160", Line = "259", LineRule = W.LineSpacingRuleValues.Auto }))),
        new W.Style(new W.StyleName { Val = "Normal" }, new W.PrimaryStyle()) { Type = W.StyleValues.Paragraph, StyleId = "Normal", Default = true });
}

// ---------------- Excel ----------------
using (var doc = SpreadsheetDocument.Create(xlsx, SpreadsheetDocumentType.Workbook))
{
    var wb = doc.AddWorkbookPart();
    wb.Workbook = new S.Workbook();
    var ws = wb.AddNewPart<WorksheetPart>();
    ws.Worksheet = new S.Worksheet(new S.SheetData());
    var st = wb.AddNewPart<WorkbookStylesPart>();
    st.Stylesheet = new S.Stylesheet(
        new S.Fonts(new S.Font(new S.FontSize { Val = 11 }, new S.Color { Theme = 1U }, new S.FontName { Val = "Calibri" }, new S.FontFamilyNumbering { Val = 2 })) { Count = 1U },
        new S.Fills(new S.Fill(new S.PatternFill { PatternType = S.PatternValues.None }), new S.Fill(new S.PatternFill { PatternType = S.PatternValues.Gray125 })) { Count = 2U },
        new S.Borders(new S.Border(new S.LeftBorder(), new S.RightBorder(), new S.TopBorder(), new S.BottomBorder(), new S.DiagonalBorder())) { Count = 1U },
        new S.CellStyleFormats(new S.CellFormat { NumberFormatId = 0U, FontId = 0U, FillId = 0U, BorderId = 0U }) { Count = 1U },
        new S.CellFormats(new S.CellFormat { NumberFormatId = 0U, FontId = 0U, FillId = 0U, BorderId = 0U, FormatId = 0U }) { Count = 1U },
        new S.CellStyles(new S.CellStyle { Name = "Normal", FormatId = 0U, BuiltinId = 0U }) { Count = 1U });
    wb.Workbook.AppendChild(new S.Sheets(new S.Sheet { Id = wb.GetIdOfPart(ws), SheetId = 1U, Name = "Sheet1" }));
}

// ---------------- PowerPoint ----------------
using (var doc = PresentationDocument.Create(pptx, PresentationDocumentType.Presentation))
{
    var presPart = doc.AddPresentationPart();
    presPart.Presentation = new P.Presentation();
    BuildPresentation(presPart);
}

int problems = 0;
foreach (var (file, open) in new (string, Func<string, OpenXmlPackage>)[]
{
    (docx, f => WordprocessingDocument.Open(f, false)),
    (xlsx, f => SpreadsheetDocument.Open(f, false)),
    (pptx, f => PresentationDocument.Open(f, false)),
})
{
    using var pkg = open(file);
    var errors = new OpenXmlValidator(FileFormatVersions.Office2016).Validate(pkg).ToList();
    Console.WriteLine($"{Path.GetFileName(file)}: {new FileInfo(file).Length} bytes, {errors.Count} validation errors");
    foreach (var e in errors) Console.WriteLine($"   {e.Part?.Uri} {e.Path?.XPath}: {e.Description}");
    problems += errors.Count;
}
return problems == 0 ? 0 : 1;

static void BuildPresentation(PresentationPart presPart)
{
    var slideMasterIdList = new P.SlideMasterIdList(new P.SlideMasterId { Id = 2147483648U, RelationshipId = "rId1" });
    var slideIdList = new P.SlideIdList(new P.SlideId { Id = 256U, RelationshipId = "rId2" });
    var slideSize = new P.SlideSize { Cx = 12192000, Cy = 6858000 };
    var notesSize = new P.NotesSize { Cx = 6858000, Cy = 9144000 };
    var defaultTextStyle = new P.DefaultTextStyle();
    presPart.Presentation.Append(slideMasterIdList, slideIdList, slideSize, notesSize, defaultTextStyle);

    var slidePart = presPart.AddNewPart<SlidePart>("rId2");
    slidePart.Slide = new P.Slide(
        new P.CommonSlideData(new P.ShapeTree(
            new P.NonVisualGroupShapeProperties(
                new P.NonVisualDrawingProperties { Id = 1U, Name = "" },
                new P.NonVisualGroupShapeDrawingProperties(),
                new P.ApplicationNonVisualDrawingProperties()),
            new P.GroupShapeProperties(new D.TransformGroup()),
            TitleShape(2U, "Title 1", P.PlaceholderValues.CenteredTitle, null))),
        new P.ColorMapOverride(new D.MasterColorMapping()));

    var layoutPart = slidePart.AddNewPart<SlideLayoutPart>("rId1");
    layoutPart.SlideLayout = new P.SlideLayout(
        new P.CommonSlideData(new P.ShapeTree(
            new P.NonVisualGroupShapeProperties(
                new P.NonVisualDrawingProperties { Id = 1U, Name = "" },
                new P.NonVisualGroupShapeDrawingProperties(),
                new P.ApplicationNonVisualDrawingProperties()),
            new P.GroupShapeProperties(new D.TransformGroup()),
            TitleShape(2U, "Title 1", P.PlaceholderValues.CenteredTitle, null))) { Name = "Title Slide" },
        new P.ColorMapOverride(new D.MasterColorMapping())) { Type = P.SlideLayoutValues.Title, Preserve = true };

    var masterPart = layoutPart.AddNewPart<SlideMasterPart>("rId1");
    masterPart.SlideMaster = new P.SlideMaster(
        new P.CommonSlideData(new P.ShapeTree(
            new P.NonVisualGroupShapeProperties(
                new P.NonVisualDrawingProperties { Id = 1U, Name = "" },
                new P.NonVisualGroupShapeDrawingProperties(),
                new P.ApplicationNonVisualDrawingProperties()),
            new P.GroupShapeProperties(new D.TransformGroup()),
            TitleShape(2U, "Title Placeholder 1", P.PlaceholderValues.Title,
                new D.Transform2D(new D.Offset { X = 838200, Y = 365125 }, new D.Extents { Cx = 10515600, Cy = 1325563 })))),
        new P.ColorMap
        {
            Background1 = D.ColorSchemeIndexValues.Light1, Text1 = D.ColorSchemeIndexValues.Dark1,
            Background2 = D.ColorSchemeIndexValues.Light2, Text2 = D.ColorSchemeIndexValues.Dark2,
            Accent1 = D.ColorSchemeIndexValues.Accent1, Accent2 = D.ColorSchemeIndexValues.Accent2, Accent3 = D.ColorSchemeIndexValues.Accent3,
            Accent4 = D.ColorSchemeIndexValues.Accent4, Accent5 = D.ColorSchemeIndexValues.Accent5, Accent6 = D.ColorSchemeIndexValues.Accent6,
            Hyperlink = D.ColorSchemeIndexValues.Hyperlink, FollowedHyperlink = D.ColorSchemeIndexValues.FollowedHyperlink,
        },
        new P.SlideLayoutIdList(new P.SlideLayoutId { Id = 2147483649U, RelationshipId = "rId1" }),
        new P.TextStyles(new P.TitleStyle(), new P.BodyStyle(), new P.OtherStyle()));
    masterPart.AddPart(layoutPart, "rId1");
    presPart.AddPart(masterPart, "rId1");

    var themePart = masterPart.AddNewPart<ThemePart>("rId5");
    themePart.Theme = BuildTheme();
    presPart.AddPart(themePart, "rId5");
}

static P.Shape TitleShape(uint id, string name, P.PlaceholderValues type, D.Transform2D? xfrm)
{
    var spPr = new P.ShapeProperties();
    if (xfrm != null) spPr.Append(xfrm);
    return new P.Shape(
        new P.NonVisualShapeProperties(
            new P.NonVisualDrawingProperties { Id = id, Name = name },
            new P.NonVisualShapeDrawingProperties(new D.ShapeLocks { NoGrouping = true }),
            new P.ApplicationNonVisualDrawingProperties(new P.PlaceholderShape { Type = type })),
        spPr,
        new P.TextBody(new D.BodyProperties(), new D.ListStyle(), new D.Paragraph(new D.EndParagraphRunProperties { Language = "en-US" })));
}

static D.Theme BuildTheme()
{
    var colors = new D.ColorScheme(
        new D.Dark1Color(new D.SystemColor { Val = D.SystemColorValues.WindowText, LastColor = "000000" }),
        new D.Light1Color(new D.SystemColor { Val = D.SystemColorValues.Window, LastColor = "FFFFFF" }),
        new D.Dark2Color(new D.RgbColorModelHex { Val = "0E2841" }),
        new D.Light2Color(new D.RgbColorModelHex { Val = "E8E8E8" }),
        new D.Accent1Color(new D.RgbColorModelHex { Val = "156082" }),
        new D.Accent2Color(new D.RgbColorModelHex { Val = "E97132" }),
        new D.Accent3Color(new D.RgbColorModelHex { Val = "196B24" }),
        new D.Accent4Color(new D.RgbColorModelHex { Val = "0F9ED5" }),
        new D.Accent5Color(new D.RgbColorModelHex { Val = "A02B93" }),
        new D.Accent6Color(new D.RgbColorModelHex { Val = "4EA72E" }),
        new D.Hyperlink(new D.RgbColorModelHex { Val = "467886" }),
        new D.FollowedHyperlinkColor(new D.RgbColorModelHex { Val = "96607D" })) { Name = "Office" };
    var fonts = new D.FontScheme(
        new D.MajorFont(new D.LatinFont { Typeface = "Calibri Light" }, new D.EastAsianFont { Typeface = "" }, new D.ComplexScriptFont { Typeface = "" }),
        new D.MinorFont(new D.LatinFont { Typeface = "Calibri" }, new D.EastAsianFont { Typeface = "" }, new D.ComplexScriptFont { Typeface = "" })) { Name = "Office" };
    var fmt = new D.FormatScheme(
        new D.FillStyleList(
            new D.SolidFill(new D.SchemeColor { Val = D.SchemeColorValues.PhColor }),
            new D.SolidFill(new D.SchemeColor { Val = D.SchemeColorValues.PhColor }),
            new D.SolidFill(new D.SchemeColor { Val = D.SchemeColorValues.PhColor })),
        new D.LineStyleList(
            new D.Outline(new D.SolidFill(new D.SchemeColor { Val = D.SchemeColorValues.PhColor })) { Width = 6350 },
            new D.Outline(new D.SolidFill(new D.SchemeColor { Val = D.SchemeColorValues.PhColor })) { Width = 12700 },
            new D.Outline(new D.SolidFill(new D.SchemeColor { Val = D.SchemeColorValues.PhColor })) { Width = 19050 }),
        new D.EffectStyleList(
            new D.EffectStyle(new D.EffectList()),
            new D.EffectStyle(new D.EffectList()),
            new D.EffectStyle(new D.EffectList())),
        new D.BackgroundFillStyleList(
            new D.SolidFill(new D.SchemeColor { Val = D.SchemeColorValues.PhColor }),
            new D.SolidFill(new D.SchemeColor { Val = D.SchemeColorValues.PhColor }),
            new D.SolidFill(new D.SchemeColor { Val = D.SchemeColorValues.PhColor }))) { Name = "Office" };
    return new D.Theme(new D.ThemeElements(colors, fonts, fmt)) { Name = "Office Theme" };
}
