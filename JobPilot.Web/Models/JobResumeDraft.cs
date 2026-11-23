namespace JobPilot.Web.Models;

internal sealed class JobResumeDraft
{
    public GeneratedResume? Resume { get; private set; }
    public bool IsEditing { get; private set; }

    public void Open(GeneratedResume resume)
    {
        Resume = resume;
        IsEditing = true;
    }

    public void Review() => IsEditing = false;

    public void Reset()
    {
        Resume = null;
        IsEditing = false;
    }
}
