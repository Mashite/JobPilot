
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using JobPilot.Api.Models.Resume;

namespace JobPilot.Api.Services;

public class WordDocumentService
{
    public byte[] Generate(GeneratedResume resume)
    {
        using var stream = new MemoryStream();

        using (var document = WordprocessingDocument.Create(
            stream,
            WordprocessingDocumentType.Document,
            true))
        {
            var mainPart = document.AddMainDocumentPart();

            mainPart.Document = new Document(
                new Body());

            var body = mainPart.Document.Body!;

            // Candidate name
            AddParagraph(body, resume.FullName,
                bold: true, fontSize: "32");

            // Professional title
            AddParagraph(body, resume.ProfessionalTitle,
                fontSize: "22");

            AddParagraph(body, "");

            // Summary
            AddHeading(body, "PROFESSIONAL SUMMARY");
            AddParagraph(body, resume.ProfessionalSummary);

            // Technical skills
            AddHeading(body, "TECHNICAL SKILLS");

            AddParagraph(
                body,
                string.Join(" | ", resume.TechnicalSkills));

            // Experience
            AddHeading(body, "PROFESSIONAL EXPERIENCE");

            foreach (var experience in resume.Experiences)
            {
                AddParagraph(
                    body,
                    $"{experience.Position} — {experience.Company}",
                    bold: true);

                AddParagraph(
                    body,
                    $"{experience.StartDate} – {experience.EndDate}");

                foreach (var highlight in experience.Highlights)
                {
                    AddBullet(body, highlight);
                }
            }

            // Projects
            if (resume.Projects.Count > 0)
            {
                AddHeading(body, "PROJECTS");

                foreach (var project in resume.Projects)
                {
                    AddBullet(body, project);
                }
            }

            // Education
            if (resume.Education.Count > 0)
            {
                AddHeading(body, "EDUCATION");

                foreach (var education in resume.Education)
                {
                    AddParagraph(body, education);
                }
            }

            mainPart.Document.Save();
        }

        return stream.ToArray();
    }

    private static void AddHeading(
        Body body,
        string text)
    {
        AddParagraph(body, text,
            bold: true, fontSize: "24");
    }

    private static void AddBullet(
        Body body,
        string text)
    {
        // Simple text bullet for first implementation.
        AddParagraph(body, $"• {text}");
    }

    private static void AddParagraph(
        Body body,
        string text,
        bool bold = false,
        string fontSize = "21")
    {
        var runProperties = new RunProperties(
            new RunFonts
            {
                Ascii = "Cambria",
                HighAnsi = "Cambria"
            },
            new FontSize { Val = fontSize });

        if (bold)
            runProperties.Append(new Bold());

        var run = new Run(
            runProperties,
            new Text(text ?? "")
            {
                Space = SpaceProcessingModeValues.Preserve
            });

        var paragraph = new Paragraph(run);

        body.Append(paragraph);
    }
}
