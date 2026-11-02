using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace Lunar.Conversion.Spike;

internal static class DocxExperiment
{
    internal static void Create(string workspace)
    {
        var path = Path.Combine(workspace, "fixture-layout.docx");
        var png = Path.Combine(workspace, "fixture-alpha-\u00e9-\u6d4b\u8bd5.png");
        var imageHash = Program.Hash(png);
        using (var package = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document))
        {
            var main = package.AddMainDocumentPart();
            var body = new W.Body();
            main.Document = new W.Document(body);
            var styles = main.AddNewPart<StyleDefinitionsPart>();
            styles.Styles = new W.Styles(new W.DocDefaults(new W.RunPropertiesDefault(
                new W.RunPropertiesBaseStyle(new W.RunFonts { Ascii = "Arial", HighAnsi = "Arial" }, new W.FontSize { Val = "22" }))));
            body.Append(Paragraph("Lunar conversion fixture", 40, true));
            body.Append(Paragraph("Synthetic page one tests a table, transparent image and fixed Letter page geometry."));
            var table = new W.Table(new W.TableProperties(new W.TableWidth { Type = W.TableWidthUnitValues.Dxa, Width = "9360" },
                new W.TableBorders(new W.TopBorder { Val = W.BorderValues.Single, Size = 4 }, new W.LeftBorder { Val = W.BorderValues.Single, Size = 4 },
                    new W.BottomBorder { Val = W.BorderValues.Single, Size = 4 }, new W.RightBorder { Val = W.BorderValues.Single, Size = 4 },
                    new W.InsideHorizontalBorder { Val = W.BorderValues.Single, Size = 4 }, new W.InsideVerticalBorder { Val = W.BorderValues.Single, Size = 4 })),
                new W.TableGrid(new W.GridColumn { Width = "4680" }, new W.GridColumn { Width = "4680" }));
            foreach (var cells in new[] { new[] { "Item", "Expected" }, new[] { "Image", "Embedded pixels only" }, new[] { "Page break", "Second page retained" } })
                table.Append(new W.TableRow(cells.Select(text => new W.TableCell(new W.TableCellProperties(
                    new W.TableCellWidth { Type = W.TableWidthUnitValues.Dxa, Width = "4680" }), Paragraph(text))).ToArray()));
            body.Append(table, Paragraph("Image embedding does not create editable text or perform OCR."));
            var part = main.AddImagePart(ImagePartType.Png);
            using (var input = File.OpenRead(png)) part.FeedData(input);
            var id = main.GetIdOfPart(part);
            var drawing = new W.Drawing();
            drawing.InnerXml = $"""
                <wp:inline xmlns:wp="http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing" distT="0" distB="0" distL="0" distR="0">
                  <wp:extent cx="5943600" cy="2971800"/><wp:docPr id="1" name="Synthetic color blocks" descr="Four color quadrants separated by transparent pixels"/>
                  <wp:cNvGraphicFramePr/><a:graphic xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main">
                    <a:graphicData uri="http://schemas.openxmlformats.org/drawingml/2006/picture">
                      <pic:pic xmlns:pic="http://schemas.openxmlformats.org/drawingml/2006/picture">
                        <pic:nvPicPr><pic:cNvPr id="0" name="Synthetic fixture"/><pic:cNvPicPr/></pic:nvPicPr>
                        <pic:blipFill><a:blip xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships" r:embed="{id}"/><a:stretch><a:fillRect/></a:stretch></pic:blipFill>
                        <pic:spPr><a:xfrm><a:off x="0" y="0"/><a:ext cx="5943600" cy="2971800"/></a:xfrm><a:prstGeom prst="rect"><a:avLst/></a:prstGeom></pic:spPr>
                      </pic:pic>
                    </a:graphicData>
                  </a:graphic>
                </wp:inline>
                """;
            body.Append(new W.Paragraph(new W.Run(drawing)), new W.Paragraph(new W.Run(new W.Break { Type = W.BreakValues.Page })));
            body.Append(Paragraph("Second page checkpoint", 32, true), Paragraph("Explicit pagination and text must survive rendering. No fonts, macros, remote relationships or external content are embedded."));
            body.Append(new W.SectionProperties(new W.PageSize { Width = 12240, Height = 15840 },
                new W.PageMargin { Top = 1440, Bottom = 1440, Left = 1440, Right = 1440, Header = 720, Footer = 720, Gutter = 0 }));
            main.Document.Save();
        }
        using var read = WordprocessingDocument.Open(path, false);
        var validation = new OpenXmlValidator().Validate(read).ToArray();
        if (validation.Length != 0)
            Program.Record("DOCX-SchemaDiagnostics", "FAIL", validation.Take(8).Select(e => new
            { e.Id, Node = e.Node?.LocalName, e.Description }).ToArray());
        Program.Check(validation.Length == 0, "DOCX schema validation failed");
        var images = read.MainDocumentPart!.ImageParts.ToArray();
        Program.Check(images.Length == 1, "Embedded image count differs");
        using var embedded = images[0].GetStream(FileMode.Open, FileAccess.Read);
        using var copy = new MemoryStream(); embedded.CopyTo(copy);
        var embeddedHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(copy.ToArray())).ToLowerInvariant();
        Program.Check(embeddedHash == imageHash && Program.Hash(png) == imageHash, "Embedded/source image differs");
        Program.Check(!read.MainDocumentPart.ExternalRelationships.Any(), "Unexpected external relationship");
        Program.Record("Image-DOCX-Embedding", "PASS", new { SchemaErrors = validation.Length, ImageParts = 1,
            EmbeddedBytesExact = true, OriginalPngUnchanged = true, ExternalRelationships = 0,
            Boundary = "Structure/embedding proof only; layout requires renderer and PNG review" });
    }

    private static W.Paragraph Paragraph(string text, int halfPoints = 22, bool bold = false)
    {
        var properties = new W.RunProperties();
        if (bold) properties.Append(new W.Bold());
        properties.Append(new W.FontSize { Val = halfPoints.ToString(System.Globalization.CultureInfo.InvariantCulture) });
        return new W.Paragraph(new W.ParagraphProperties(new W.SpacingBetweenLines { After = "180" }), new W.Run(properties, new W.Text(text)));
    }
}
