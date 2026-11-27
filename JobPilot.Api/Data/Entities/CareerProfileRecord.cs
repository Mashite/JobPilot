namespace JobPilot.Api.Data.Entities;

// JobPilot currently manages one local candidate. The profile and its evidence form a single versioned aggregate.
public sealed class CareerProfileRecord
{
    public int Id { get; set; }
    public string ProfileJson { get; set; } = "";
    public string Version { get; set; } = "";
}
