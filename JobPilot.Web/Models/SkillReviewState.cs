namespace JobPilot.Web.Models;

internal sealed class SkillReviewState(IEnumerable<string> unmatchedSkills)
{
    private readonly List<string> originalUnmatched = unmatchedSkills.Distinct(StringComparer.Ordinal).ToList();
    private readonly HashSet<string> staged = new(StringComparer.Ordinal);
    private readonly HashSet<string> confirmed = new(StringComparer.Ordinal);
    private readonly HashSet<string> saved = new(StringComparer.Ordinal);
    private readonly HashSet<string> related = new(StringComparer.Ordinal);
    private readonly HashSet<string> missing = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> evidence = new(StringComparer.Ordinal);

    public IEnumerable<string> Unmatched => originalUnmatched.Where(skill => !staged.Contains(skill) && !confirmed.Contains(skill));
    public IEnumerable<string> Staged => originalUnmatched.Where(staged.Contains);
    public IEnumerable<string> Confirmed => originalUnmatched.Where(confirmed.Contains);
    public IEnumerable<string> Related => originalUnmatched.Where(related.Contains);
    public IEnumerable<string> Missing => originalUnmatched.Where(missing.Contains);
    public IEnumerable<string> ResumeSkillHints => originalUnmatched.Where(skill => confirmed.Contains(skill) || saved.Contains(skill));
    public bool HasStaged => staged.Count > 0;
    public bool HasConfirmed => confirmed.Count > 0;
    public bool HasOverrides => HasConfirmed || related.Count > 0 || missing.Count > 0;

    public void Include(IEnumerable<string> skills)
    {
        foreach (var skill in skills)
        {
            if (!originalUnmatched.Contains(skill, StringComparer.Ordinal))
                originalUnmatched.Add(skill);
        }
    }

    public void Restore(IEnumerable<string> matched, IEnumerable<string> partial, IEnumerable<string> absent,
        IReadOnlyDictionary<string, string> notes)
    {
        Include(matched.Concat(partial).Concat(absent));
        confirmed.UnionWith(matched);
        related.UnionWith(partial);
        missing.UnionWith(absent);
        foreach (var (skill, note) in notes)
            evidence[skill] = note;
    }

    public bool Stage(string skill) =>
        originalUnmatched.Contains(skill, StringComparer.Ordinal) && !confirmed.Contains(skill) && staged.Add(skill);

    public bool Return(string skill)
    {
        staged.Remove(skill);
        evidence.Remove(skill);
        return confirmed.Remove(skill) | related.Remove(skill) | missing.Remove(skill);
    }

    public string? EvidenceFor(string skill) => evidence.GetValueOrDefault(skill);

    public void SetEvidence(string skill, string note)
    {
        if (staged.Contains(skill))
            evidence[skill] = note;
    }

    public Dictionary<string, string> EvidenceFor(IEnumerable<string> skills) => skills
        .Where(skill => !string.IsNullOrWhiteSpace(EvidenceFor(skill)))
        .ToDictionary(skill => skill, skill => EvidenceFor(skill)!.Trim(), StringComparer.Ordinal);

    public void AcceptSavedMatches(IEnumerable<string> matchedSkills)
    {
        foreach (var skill in matchedSkills)
        {
            if (confirmed.Remove(skill))
                saved.Add(skill);
        }
    }

    public void Confirm()
    {
        related.ExceptWith(staged);
        missing.ExceptWith(staged);
        confirmed.UnionWith(staged);
        staged.Clear();
    }

    public bool MarkRelated(string skill)
    {
        if (!staged.Contains(skill) || string.IsNullOrWhiteSpace(EvidenceFor(skill)))
            return false;
        staged.Remove(skill);
        missing.Remove(skill);
        return related.Add(skill);
    }

    public void MarkMissing(string skill)
    {
        Return(skill);
        if (originalUnmatched.Contains(skill, StringComparer.Ordinal))
            missing.Add(skill);
    }
}
