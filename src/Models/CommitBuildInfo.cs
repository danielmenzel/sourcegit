namespace SourceGit.Models
{
    public class CommitBuildInfo
    {
        public BuildStatus Status { get; set; } = BuildStatus.Unknown;
        public string Description { get; set; } = string.Empty;
        public string Url { get; set; } = string.Empty;

        public string StatusSymbol => Status switch
        {
            BuildStatus.Success => "✔",
            BuildStatus.Failure => "✘",
            BuildStatus.InProgress => "▶",
            BuildStatus.Stopped => "⏹",
            BuildStatus.Unstable => "⚠",
            _ => "?",
        };
    }
}
