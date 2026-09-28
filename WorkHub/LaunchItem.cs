namespace WorkHub;

/// <summary>One program/shortcut the hub can auto-launch at startup.</summary>
public sealed class LaunchItem
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public string Arguments { get; set; } = "";
    public bool RunAsAdmin { get; set; }
    public bool Enabled { get; set; } = true;

    public LaunchItem Clone() => new()
    {
        Name = Name,
        Path = Path,
        Arguments = Arguments,
        RunAsAdmin = RunAsAdmin,
        Enabled = Enabled,
    };
}
