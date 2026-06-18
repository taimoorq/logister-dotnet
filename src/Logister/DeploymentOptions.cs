namespace Logister;

public sealed class DeploymentOptions
{
    public string? Release { get; set; }
    public string? Environment { get; set; }
    public string? Repository { get; set; }
    public string? CommitSha { get; set; }
    public string? Branch { get; set; }
    public DateTimeOffset? DeployedAt { get; set; }
    public int? PullRequestNumber { get; set; }
    public string? PullRequestUrl { get; set; }
    public string? ReleaseTag { get; set; }
    public string? ReleaseUrl { get; set; }
    public string? CompareUrl { get; set; }
    public string? WorkflowRunUrl { get; set; }
    public string? DeploymentUrl { get; set; }
}
