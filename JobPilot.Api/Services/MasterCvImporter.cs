using System.Text.RegularExpressions;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using JobPilot.Api.Models;

namespace JobPilot.Api.Services;

public sealed class MasterCvImporter(SkillNormalizer normalizer)
{
    private static readonly Regex Dates = new(
        @"(?<start>(?:[A-Za-z]{3,12}\s+)?(?:19|20)\d{2})\s*(?:[–—-]|to)\s*(?<end>Present|Current|(?:[A-Za-z]{3,12}\s+)?(?:19|20)\d{2})",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex Role = new(
        @"\b(engineer|developer|architect|lead(?:er)?|manager|analyst|consultant|specialist|director|programmer|designer)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex Uncertain = new(
        @"\b(no|not|without|planned|learning|aspiring|interested in|unverified|familiarity|exposure to)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly string[] HeadingSeparators = [" — ", " – ", " - ", " | "];
    private static readonly Dictionary<string, string> TechnicalSubsections = new(StringComparer.OrdinalIgnoreCase)
    {
        ["programming & frameworks"] = "Programming & Frameworks",
        ["databases"] = "Databases",
        ["industrial & communication technologies"] = "Industrial & Communication Technologies",
        ["integration"] = "Integration",
        ["application & service technologies"] = "Application & Service Technologies",
        ["development & collaboration"] = "Development & Collaboration"
    };
    private static readonly Regex NaturalLanguage = new(
        @"^(?:English|Turkish|German|French|Arabic|Spanish|Italian|Portuguese|Russian|Dutch)(?:\s*\([\p{L}\s-]+\))?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly (string Category, string[] Names)[] Technologies =
    [
        ("Programming languages", ["C#", "C Sharp", "Java", "JavaScript", "TypeScript", "Python", "C++", "SQL"]),
        (".NET frameworks", ["ASP.NET Core", "ASP.NET MVC", "ASP.NET", ".NET Framework", ".NET Core", ".NET", "Entity Framework Core", "EF Core", "Entity Framework", "WCF", "Windows Forms", "WPF"]),
        ("Web development", ["REST APIs", "Web API", "Web APIs", "JSP", "HTML", "CSS", "React", "Blazor", "OpenLayers", "Google Maps"]),
        ("Database technologies", ["SQL Server", "MSSQL", "Oracle", "PostgreSQL", "MySQL", "Database design", "Jasper Reports"]),
        ("Software architecture", ["Software architecture", "Application architecture", "Software design patterns", "Microservices"]),
        ("Design and modeling", ["UML", "Object-oriented design", "Data modeling"]),
        ("Industrial automation", ["Manufacturing Execution Systems", "MES", "Andon", "PLC integration", "Industrial automation"]),
        ("Communication protocols", ["Modbus", "TCP/IP", "SOAP", "GPS", "GSM", "OPC UA"]),
        ("Enterprise integration", ["ERP integration", "SAP integration", "Enterprise integration", "System integration", "ERP", "SAP"]),
        ("Background processing", ["Windows services", "Background services", "Background processing", "Background data processing"]),
        ("Development tools", ["Visual Studio", "Git", "Jira"]),
        ("DevOps and deployment", ["Azure DevOps", "DevOps", "CI/CD", "Docker", "Kubernetes", "AWS", "Azure", "Production support"]),
        ("Other professional competencies", ["Factory process analysis", "Customer implementations", "Warehouse management", "Project management"])
    ];

    private static readonly IReadOnlyDictionary<string, string> TechnologyCategories = Technologies
        .SelectMany(group => group.Names.Select(name => (Name: name, group.Category)))
        .ToDictionary(entry => entry.Name, entry => entry.Category, StringComparer.OrdinalIgnoreCase);
    private static readonly Regex NamedTechnology = new(
        @"(?<![\w.#])(?:\.NET(?: Framework| Core)?\s+\d+(?:[./]\d+)*|" +
        string.Join("|", TechnologyCategories.Keys.OrderByDescending(key => key.Length).Select(Regex.Escape)) + @")(?![\w#])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public CandidateProfile Parse(Stream source)
    {
        using var document = WordprocessingDocument.Open(source, false, new OpenSettings { MaxCharactersInPart = 4_000_000 });
        var main = document.MainDocumentPart ?? throw new InvalidDataException("The DOCX contains no document.");
        var body = main.Document?.Body ?? throw new InvalidDataException("The DOCX contains no body.");
        var blocks = ReadBlocks(body).ToArray();
        if (blocks.Length == 0)
            throw new InvalidDataException("The DOCX contains no readable text.");

        var profile = new CandidateProfile { EducationDetails = [], ProjectDetails = [] };
        var sections = SplitSections(blocks);
        foreach (var section in sections)
        {
            var needsReview = false;
            switch (section.Kind)
            {
                case "identity":
                    ReadIdentity(profile, section.Lines);
                    needsReview = section.Lines.Count > 2 || string.IsNullOrWhiteSpace(profile.FullName);
                    break;
                case "summary":
                    profile.Summary = string.Join("\n\n", section.Lines);
                    AddEvidence(profile, section, null);
                    break;
                case "strengths":
                    for (var index = 0; index < section.Lines.Count; index++)
                    {
                        var line = section.Lines[index];
                        profile.CoreStrengths.Add(WithIndentation(line, section.BulletDepths.GetValueOrDefault(index)));
                        AddDocumentedEvidence(profile, line, "Professional competencies", line, section.Heading);
                    }
                    break;
                case "skills":
                    needsReview = !ReadTechnicalProfile(profile, section);
                    break;
                case "themes":
                    foreach (var index in section.BoldLines.Order())
                    {
                        var title = section.Lines[index];
                        AddDocumentedEvidence(profile, title, "Professional competencies", title, section.Heading);
                    }
                    break;
                case "philosophy":
                    break;
                case "languages":
                    profile.Languages.AddRange(section.Lines.SelectMany(SplitItems));
                    break;
                case "employment":
                    needsReview = !ReadEmployment(profile, section);
                    break;
                case "education":
                    needsReview = !ReadEducation(profile, section);
                    break;
                case "certifications":
                    needsReview = !ReadCertifications(profile, section.Lines);
                    break;
                case "projects":
                    needsReview = !ReadProjects(profile, section.Lines);
                    break;
                default:
                    needsReview = true;
                    AddEvidence(profile, section, null);
                    break;
            }
            profile.ImportReviewSections.Add(new(section.Heading,
                section.Lines.Select((line, index) => WithIndentation(line, section.BulletDepths.GetValueOrDefault(index))).ToList(),
                needsReview, section.Kind));
        }

        foreach (var part in main.HeaderParts.Select(part => part.Header).OfType<DocumentFormat.OpenXml.OpenXmlElement>()
            .Concat(main.FooterParts.Select(part => part.Footer).OfType<DocumentFormat.OpenXml.OpenXmlElement>()))
        {
            var lines = ReadBlocks(part).Select(block => block.Text).ToList();
            if (lines.Count > 0)
                profile.ImportReviewSections.Add(new("Header or footer", lines, true));
        }
        return profile;
    }

    private static IEnumerable<Block> ReadBlocks(DocumentFormat.OpenXml.OpenXmlElement container)
    {
        foreach (var child in container.ChildElements)
        {
            if (child is Paragraph paragraph)
            {
                var text = ReadParagraph(paragraph);
                if (text.Length > 0)
                {
                    var headingLevel = ReadHeadingLevel(paragraph);
                    var textRuns = paragraph.Elements<Run>().Where(run => !string.IsNullOrWhiteSpace(run.InnerText)).ToList();
                    var visuallyBold = textRuns.Count > 0 && textRuns.All(run => run.RunProperties?.Bold is { } bold &&
                        bold.Val?.Value != false);
                    var depth = paragraph.ParagraphProperties?.NumberingProperties is { } numbering
                        ? numbering.NumberingLevelReference?.Val?.Value ?? 0 : (int?)null;
                    yield return new(text, headingLevel, visuallyBold, depth);
                }
            }
            else if (child is Table table)
            {
                foreach (var row in table.Elements<TableRow>())
                {
                    var text = string.Join(" | ", row.Elements<TableCell>()
                        .Select(cell => string.Join(" / ", cell.Descendants<Paragraph>().Select(ReadParagraph))));
                    if (!string.IsNullOrWhiteSpace(text))
                        yield return new(text, null, false, null);
                }
            }
            else if (child.HasChildren)
            {
                foreach (var block in ReadBlocks(child))
                    yield return block;
            }
        }
    }

    private static int? ReadHeadingLevel(Paragraph paragraph)
    {
        if (paragraph.ParagraphProperties?.OutlineLevel?.Val?.Value is >= 0 and < 9 and var outline)
            return outline + 1;
        var style = paragraph.ParagraphProperties?.ParagraphStyleId?.Val?.Value;
        if (style?.StartsWith("Heading", StringComparison.OrdinalIgnoreCase) == true)
            return int.TryParse(style["Heading".Length..].Trim(), out var level) ? level : 2;
        return null;
    }

    private static string ReadParagraph(Paragraph paragraph) => string.Concat(paragraph.Descendants()
        .Where(element => element is Text or TabChar or Break)
        .Select(element => element switch
        {
            Text text => text.Text,
            Break line when line.Type?.Value is null || line.Type.Value == BreakValues.TextWrapping => "\n",
            _ => " "
        })).Trim();

    private static List<Section> SplitSections(IEnumerable<Block> blocks)
    {
        var sections = new List<Section> { new("Identity and contact", "identity", []) };
        foreach (var (text, headingLevel, visuallyBold, bulletDepth) in blocks)
        {
            var key = HeadingKey(text);
            var kind = key switch
            {
                "professional profile" or "professional summary" or "summary" or "profile" => "summary",
                "core strengths" or "core strength" or "core competencies" => "strengths",
                "technical skills" or "technical expertise" or "technical summary" or "skills" or "technical profile" => "skills",
                "professional experience" or "work experience" or "employment history" or "experience" => "employment",
                "education" or "education & training" => "education",
                "languages" or "language skills" => "languages",
                "certifications" or "professional certifications" => "certifications",
                "projects" or "selected projects" => "projects",
                "personal information" => "identity",
                "professional themes" => "themes",
                "engineering philosophy" => "philosophy",
                _ => null
            };
            if (sections[^1].Kind == "employment" && kind is "projects" && headingLevel != 1)
                kind = null;
            if (kind is not null)
                sections.Add(new(text, kind, []));
            else if (sections[^1].Kind is not ("identity" or "employment" or "skills" or "summary" or "themes" or "philosophy") &&
                bulletDepth is null && (headingLevel is not null || (
                text.Length is >= 6 and <= 56 && text.All(character => !char.IsLetter(character) || char.IsUpper(character)))) &&
                text.Length <= 120)
                sections.Add(new(text, "review", []));
            else
            {
                if (visuallyBold || headingLevel is not null)
                    sections[^1].BoldLines.Add(sections[^1].Lines.Count);
                if (bulletDepth is int level)
                    sections[^1].BulletDepths[sections[^1].Lines.Count] = level;
                sections[^1].Lines.Add(text);
            }
        }
        return sections;
    }

    private static void ReadIdentity(CandidateProfile profile, List<string> lines)
    {
        foreach (var text in lines)
        {
            if (text.StartsWith("Location:", StringComparison.OrdinalIgnoreCase))
                profile.Location = text["Location:".Length..].Trim();
            else if (text.StartsWith("Languages:", StringComparison.OrdinalIgnoreCase))
                profile.Languages.AddRange(SplitItems(text["Languages:".Length..]));
            else if (profile.FullName.Length == 0 && Regex.IsMatch(text, @"^[\p{L}\p{M}\p{Zs}.'’-]{3,90}$") && text.Contains(' '))
                profile.FullName = text;
            else if (profile.ProfessionalTitle.Length == 0 && Role.IsMatch(text) && text.Length <= 250 && !text.Contains('@'))
                profile.ProfessionalTitle = text;
            else if (profile.Location is null && profile.ProfessionalTitle.Length > 0 && text.Length <= 120)
            {
                var parts = text.Split(['|', '•', '·'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                profile.Location = parts.FirstOrDefault(part => Regex.IsMatch(part,
                    @"^[\p{L}\p{M} '-]+(?:,\s*[\p{L}\p{M} '-]+){1,3}(?:\s+\([\p{L}\p{M} '-]+\))?$"));
                profile.Languages.AddRange(parts.Where(part => NaturalLanguage.IsMatch(part)));
            }
        }
    }

    private bool ReadTechnicalProfile(CandidateProfile profile, Section section)
    {
        var category = "Technical skills";
        var complete = section.Lines.Count > 0;
        for (var index = 0; index < section.Lines.Count; index++)
        {
            var line = section.Lines[index];
            var parts = line.Split(':', 2, StringSplitOptions.TrimEntries);
            if (TechnicalSubsections.TryGetValue(HeadingKey(parts[0]), out var recognized))
            {
                category = recognized;
                if (parts.Length == 1)
                    continue;
                line = parts[1];
            }
            else if (section.BoldLines.Contains(index))
            {
                category = "Technical skills";
                complete = false;
                continue;
            }

            // Keep explanatory prose in the source review instead of promoting it to skills.
            var names = SplitTechnologyList(line).ToList();
            if (names.Count == 0 || names.Any(name => !CandidateProfileService.IsValidSkill(name) ||
                name.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length > 10))
            {
                complete = false;
                continue;
            }
            foreach (var name in names)
                AddDocumentedEvidence(profile, name, category, line, section.Heading);
        }
        return complete;
    }

    private bool ReadEmployment(CandidateProfile profile, Section section)
    {
        WorkExperience? current = null;
        ProfileProject? project = null;
        var projectReferences = new List<string>();
        var subsection = EmploymentSubsection.Responsibilities;
        var subsectionHeading = "";
        var unassigned = false;
        for (var index = 0; index < section.Lines.Count; index++)
        {
            var text = section.Lines[index];
            if (text.Length <= 200 && index + 2 < section.Lines.Count && Role.IsMatch(section.Lines[index + 1]) &&
                section.Lines[index + 1].Length <= 250 && Dates.Match(section.Lines[index + 2]) is { Success: true } dates &&
                dates.Value == section.Lines[index + 2].Trim('|', ' ', '(', ')'))
            {
                CompleteProjectReferences(profile, current, projectReferences);
                var employer = text.Split(HeadingSeparators, 2, StringSplitOptions.TrimEntries);
                current = new WorkExperience
                {
                    Company = employer[0],
                    Location = employer.Length == 2 ? employer[1] : null,
                    Position = section.Lines[index + 1],
                    StartDate = dates.Groups["start"].Value,
                    EndDate = dates.Groups["end"].Value,
                    IsCurrentPosition = HeadingKey(dates.Groups["end"].Value) is "present" or "current"
                };
                profile.Experiences.Add(current);
                project = null;
                subsection = EmploymentSubsection.Responsibilities;
                subsectionHeading = "";
                index += 2;
                continue;
            }
            if (current is null)
            {
                unassigned = true;
                continue;
            }
            var inline = text.Split(':', 2, StringSplitOptions.TrimEntries);
            var key = HeadingKey(inline[0]);
            if (key is "core technologies" or "used technologies")
            {
                subsection = EmploymentSubsection.Technologies;
                project = null;
                if (inline.Length == 2)
                    ReadEmployerTechnologies(profile, current, inline[1]);
                continue;
            }
            if (key is "projects" or "major projects" or "major projects included" or "key projects" or "selected projects")
            {
                subsection = EmploymentSubsection.ProjectReferences;
                continue;
            }
            var heading = section.BoldLines.Contains(index) && text.Length <= 160 && !".!?".Contains(text[^1]);
            if (subsection == EmploymentSubsection.ProjectReferences && section.BulletDepths.ContainsKey(index) && !heading)
            {
                projectReferences.Add(text);
                continue;
            }
            var labeledProject = key == "project" && inline.Length == 2 && inline[1].Length > 0;
            if (labeledProject || (heading && (IsNamedProjectHeading(text) ||
                projectReferences.Any(reference => AreSameProject(reference, text)))))
            {
                var name = labeledProject ? inline[1] : text;
                project = GetProject(profile, current.Company, name);
                subsection = EmploymentSubsection.Responsibilities;
                subsectionHeading = "";
                continue;
            }
            if (heading)
            {
                subsection = key is "business impact" or "achievements" or "results" or "outcomes"
                    ? EmploymentSubsection.Achievements : EmploymentSubsection.Responsibilities;
                subsectionHeading = text;
                if (project is not null && subsection == EmploymentSubsection.Responsibilities)
                    AppendProjectDescription(project, text);
                continue;
            }
            if (subsection == EmploymentSubsection.Technologies)
            {
                ReadEmployerTechnologies(profile, current, text);
                continue;
            }
            if (subsection == EmploymentSubsection.ProjectReferences)
                subsection = EmploymentSubsection.Responsibilities;

            if (subsection == EmploymentSubsection.Achievements)
            {
                current.Achievements.Add(text);
                project?.Achievements.Add(text);
            }
            else
            {
                current.Responsibilities.Add(text);
                if (project is not null)
                    AppendProjectDescription(project, text);
            }
            if (Dates.IsMatch(text) && text.Length < 80)
                unassigned = true;
            AddLineEvidence(profile, text, subsectionHeading.Length == 0 ? section.Heading : subsectionHeading,
                current.Company, project?.Name);
        }
        CompleteProjectReferences(profile, current, projectReferences);
        return profile.Experiences.Count > 0 && !unassigned;
    }

    private void ReadEmployerTechnologies(CandidateProfile profile, WorkExperience role, string line)
    {
        var seen = role.Technologies.Select(normalizer.Normalize).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var name in SplitTechnologyList(line).Where(CandidateProfileService.IsValidSkill))
        {
            if (!seen.Add(normalizer.Normalize(name)))
                continue;
            role.Technologies.Add(name);
            AddDocumentedEvidence(profile, name, "Used technologies", line, "Core Technologies", role.Company);
        }
    }

    private static bool IsNamedProjectHeading(string text)
    {
        var parts = text.Split(HeadingSeparators, 2, StringSplitOptions.TrimEntries);
        return parts.Length == 2 && parts[0].Length is > 0 and <= 48 && parts[1].Length > 0;
    }

    internal static bool AreSameProject(string first, string second)
    {
        var left = Regex.Matches(HeadingKey(first), @"[\p{L}\p{N}]+").Select(match => match.Value).ToHashSet();
        var right = Regex.Matches(HeadingKey(second), @"[\p{L}\p{N}]+").Select(match => match.Value).ToHashSet();
        string[] generic = ["web", "service", "platform", "system", "application", "project", "of", "the"];
        left.ExceptWith(generic);
        right.ExceptWith(generic);
        return HeadingKey(first) == HeadingKey(second) || (left.Count >= 2 && right.Count >= 2 &&
            (left.IsSubsetOf(right) || right.IsSubsetOf(left)));
    }

    private static ProfileProject GetProject(CandidateProfile profile, string employer, string name)
    {
        var existing = profile.ProjectDetails!.FirstOrDefault(project =>
            string.Equals(project.Employer, employer, StringComparison.OrdinalIgnoreCase) && AreSameProject(project.Name, name));
        if (existing is not null)
            return existing;
        var project = new ProfileProject { Name = name, Employer = employer };
        profile.ProjectDetails!.Add(project);
        return project;
    }

    private static void CompleteProjectReferences(CandidateProfile profile, WorkExperience? role, List<string> references)
    {
        if (role is not null)
        {
            foreach (var reference in references)
                GetProject(profile, role.Company, reference);
        }
        references.Clear();
    }

    private static void AppendProjectDescription(ProfileProject project, string text) =>
        project.Description = string.IsNullOrWhiteSpace(project.Description) ? text : project.Description + "\n" + text;

    private enum EmploymentSubsection { Responsibilities, Achievements, Technologies, ProjectReferences }

    private bool ReadEducation(CandidateProfile profile, Section section)
    {
        var lines = section.Lines;
        var complete = true;
        for (var index = 0; index < lines.Count; index++)
        {
            if (index + 1 >= lines.Count || !Regex.IsMatch(lines[index], @"\b(bachelor|master|degree|diploma|doctor|b\.?s\.?c\.?)\b",
                RegexOptions.IgnoreCase) || Dates.Match(lines[index + 1]) is not { Success: true } dates)
            {
                complete = false;
                continue;
            }
            var institution = lines[index + 1].Remove(dates.Index, dates.Length).Trim(' ', '—', '–', '-', '|', ',', '(', ')');
            profile.EducationDetails!.Add(new ProfileEducation
            {
                Degree = lines[index], Institution = institution,
                GraduationDate = dates.Groups["end"].Value, Details = dates.Value
            });
            if (CandidateProfileService.IsValidSkill(lines[index]))
                profile.CareerEvidence.Add(new CareerEvidence(Guid.NewGuid(), lines[index], normalizer.Normalize(lines[index]),
                    "education", EvidenceClassification.ExplicitlyDocumented, EvidenceVerification.Unverified, null, null,
                    $"{lines[index]} · {lines[index + 1]}", section.Heading, DateTimeOffset.UtcNow));
            index++;
        }
        return complete && profile.EducationDetails!.Count > 0;
    }

    private static bool ReadCertifications(CandidateProfile profile, List<string> lines)
    {
        var complete = true;
        foreach (var line in lines)
        {
            var columns = line.Split('|', StringSplitOptions.TrimEntries);
            if (columns is not [var name, var issuer, var date, ..] || columns.Length > 4 ||
                Uncertain.IsMatch(line) || !Regex.IsMatch(date, @"\b(?:19|20)\d{2}\b"))
            {
                complete = false;
                continue;
            }
            profile.Certifications.Add(new ProfileCertification { Name = name, Issuer = issuer, Date = date,
                Url = columns.Length == 4 ? columns[3] : null });
        }
        return complete && profile.Certifications.Count > 0;
    }

    private static bool ReadProjects(CandidateProfile profile, List<string> lines)
    {
        ProfileProject? project = null;
        var complete = true;
        foreach (var line in lines)
        {
            if (line.StartsWith("Project:", StringComparison.OrdinalIgnoreCase))
            {
                project = new ProfileProject { Name = line["Project:".Length..].Trim() };
                profile.ProjectDetails!.Add(project);
            }
            else if (project is not null)
                project.Description = string.Join("\n", new[] { project.Description, line }.Where(value => !string.IsNullOrEmpty(value)));
            else
                complete = false;
        }
        return complete && profile.ProjectDetails!.Count > 0;
    }

    private void AddEvidence(CandidateProfile profile, Section section, string? employer)
    {
        foreach (var line in section.Lines)
            AddLineEvidence(profile, line, section.Heading, employer, null);
    }

    private void AddLineEvidence(CandidateProfile profile, string line, string section, string? employer, string? project)
    {
        if (Uncertain.IsMatch(line))
            return;
        var evidence = NamedTechnology.Matches(line).Select(match =>
            (match.Value, TechnologyCategories.GetValueOrDefault(match.Value, ".NET frameworks"))).ToList();
        foreach (var (name, category) in evidence.DistinctBy(item => normalizer.Normalize(item.Item1)))
        {
            if (!CandidateProfileService.IsValidSkill(name))
                continue;
            var classification = name.Equals(".NET", StringComparison.OrdinalIgnoreCase)
                ? EvidenceClassification.RelatedOrTransferable : EvidenceClassification.ExplicitlyDocumented;
            profile.CareerEvidence.Add(new CareerEvidence(Guid.NewGuid(), name, normalizer.Normalize(name), category,
                classification, EvidenceVerification.Unverified, employer, project, line, section,
                DateTimeOffset.UtcNow));
            if (normalizer.Normalize(name) == "software architecture" &&
                !line.Contains("software design patterns", StringComparison.OrdinalIgnoreCase))
                profile.CareerEvidence.Add(new CareerEvidence(Guid.NewGuid(), "Software design patterns", "software design patterns",
                    "Software architecture", EvidenceClassification.RelatedOrTransferable, EvidenceVerification.Unverified,
                    employer, project, line, section, DateTimeOffset.UtcNow));
        }
    }

    private static string[] SplitItems(string line) =>
        line.Split([',', ';', '|', '•'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    private static IEnumerable<string> SplitTechnologyList(string line)
    {
        var depth = 0;
        var start = 0;
        for (var index = 0; index <= line.Length; index++)
        {
            if (index == line.Length || (depth == 0 && line[index] is ',' or ';' or '\n' or '•'))
            {
                var name = line[start..index].Trim().TrimStart('•').Trim();
                if (name.Length > 0)
                    yield return name;
                start = index + 1;
            }
            else if (line[index] == '(')
                depth++;
            else if (line[index] == ')' && depth > 0)
                depth--;
        }
    }

    private void AddDocumentedEvidence(CandidateProfile profile, string name, string category, string excerpt,
        string section, string? employer = null)
    {
        if (!CandidateProfileService.IsValidSkill(name) || Uncertain.IsMatch(excerpt))
            return;
        var classification = name.Equals(".NET", StringComparison.OrdinalIgnoreCase)
            ? EvidenceClassification.RelatedOrTransferable : EvidenceClassification.ExplicitlyDocumented;
        profile.CareerEvidence.Add(new(Guid.NewGuid(), name, normalizer.Normalize(name), category, classification,
            EvidenceVerification.Unverified, employer, null, excerpt, section, DateTimeOffset.UtcNow));
    }

    private static string HeadingKey(string value) =>
        Regex.Replace(value.Trim().TrimEnd(':'), @"\s+", " ").ToLowerInvariant().Replace(" and ", " & ");

    private static string WithIndentation(string line, int depth) => new string(' ', Math.Clamp(depth, 0, 8) * 2) + line;

    private sealed record Block(string Text, int? HeadingLevel, bool VisuallyBold, int? BulletDepth);
    private sealed record Section(string Heading, string Kind, List<string> Lines)
    {
        public HashSet<int> BoldLines { get; } = [];
        public Dictionary<int, int> BulletDepths { get; } = [];
    }
}
