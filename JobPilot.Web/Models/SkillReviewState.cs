namespace JobPilot.Web.Models;

internal sealed class SkillReviewState(IEnumerable<string> unmatchedSkills)
{
    private readonly string[] originalUnmatched = unmatchedSkills.Distinct(StringComparer.Ordinal).ToArray();
    private readonly HashSet<string> staged = new(StringComparer.Ordinal);
    private readonly HashSet<string> confirmed = new(StringComparer.Ordinal);

    public IEnumerable<string> Unmatched => originalUnmatched.Where(skill => !staged.Contains(skill) && !confirmed.Contains(skill));
    public IEnumerable<string> Staged => originalUnmatched.Where(staged.Contains);
    public IEnumerable<string> Confirmed => originalUnmatched.Where(confirmed.Contains);
    public bool HasStaged => staged.Count > 0;
    public bool HasConfirmed => confirmed.Count > 0;

    public bool Stage(string skill) =>
        originalUnmatched.Contains(skill, StringComparer.Ordinal) && !confirmed.Contains(skill) && staged.Add(skill);

    public bool Return(string skill)
    {
        staged.Remove(skill);
        return confirmed.Remove(skill);
    }

    public void Confirm()
    {
        confirmed.UnionWith(staged);
        staged.Clear();
    }
}
