using Dokument.Domain.Tags;

namespace Dokument.Domain.Filing;

/// <summary>
/// A person's own filing rule (docs/design.md §13.6): when one of their files gets <see cref="Tag"/>, move it to
/// <see cref="Folder"/>. Rules are ordered, and the first that matches a file wins. One rule per tag.
/// </summary>
public sealed class FilingRule
{
    public const int MaxPerPerson = 100;

    public Guid Id { get; init; } = Guid.CreateVersion7();
    public required Guid OwnerId { get; init; }

    public required TagName Tag
    {
        get;
        set
        {
            field = value;
            TagKey = value.Key;
        }
    }

    public string TagKey { get; private set; } = "";
    public required FolderTemplate Folder { get; set; }

    /// <summary>The rule's place in the person's list, from 0. The first that matches wins.</summary>
    public int Position { get; set; }
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Since when the rule files files that get its tag: when it was made, turned on, or given another tag. Files that
    /// had the tag before then stay where they are, unless the person asks to file them too.
    /// </summary>
    public DateTimeOffset ActiveSince { get; set; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// A file that a filing rule moved (docs/design.md §13.6), one row per file. A rule moves a file once by itself; if its
/// owner moves it back or elsewhere, or undoes the move, it stays there. A run (<see cref="FilingRun"/>) can move it
/// again, and then updates this row.
/// </summary>
public sealed class NodeFiling
{
    public required Guid NodeId { get; init; }

    /// <summary>The rule that moved it, until the rule is deleted.</summary>
    public Guid? RuleId { get; set; }

    /// <summary>The tag that matched, kept for the message even after the rule is gone.</summary>
    public required string Tag { get; set; }

    /// <summary>Where it was, null for the top of My files.</summary>
    public Guid? FromFolderId { get; set; }
    public Guid? ToFolderId { get; set; }
    public DateTimeOffset FiledAt { get; set; }
    public DateTimeOffset? UndoneAt { get; set; }

    /// <summary>
    /// The rule matched but didn't move the file, because the folder would hide it from people it is shared with. A
    /// rule doesn't try again by itself; a run does.
    /// </summary>
    public bool Skipped { get; set; }

    /// <summary>The run that filed it last, if it was a run (§13.6). Its files are announced and undone together.</summary>
    public Guid? RunId { get; set; }

    /// <summary>Whether the file is somewhere its owner put it: they moved it after it was filed, or pressed Undo.</summary>
    public bool PlacedByOwner(Guid? parentId) =>
        !Skipped && (UndoneAt is not null || parentId != ToFolderId);
}

public enum FilingRunStatus
{
    Queued,
    Running,
    Done,
    Stopped,
}

/// <summary>
/// A person asked to run their filing rules over files they already have (docs/design.md §13.6): all their rules or one,
/// over all their files or some. Run by one <c>filing-run</c> job, and undone as a whole.
/// </summary>
public sealed class FilingRun
{
    /// <summary>How long a finished run can still be undone from Settings.</summary>
    public static readonly TimeSpan UndoWindow = TimeSpan.FromDays(1);

    public Guid Id { get; init; } = Guid.CreateVersion7();
    public required Guid OwnerId { get; init; }

    /// <summary>The one rule to run, or null for every rule that is on.</summary>
    public Guid? RuleId { get; init; }

    /// <summary>The files and folders (with everything in them) to run on, or null for all the person's files.</summary>
    public Guid[]? NodeIds { get; init; }

    /// <summary>Also move files their owner put somewhere after a rule filed them, or moved back with Undo.</summary>
    public bool IncludePlaced { get; init; }

    public FilingRunStatus Status { get; set; }

    /// <summary>Files the run will move, once it has looked.</summary>
    public int Total { get; set; }

    /// <summary>Files it has gone through of <see cref="Total"/>.</summary>
    public int Done { get; set; }

    /// <summary>Files it moved.</summary>
    public int Moved { get; set; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? FinishedAt { get; set; }
    public DateTimeOffset? UndoneAt { get; set; }

    /// <summary>The person asked it to stop; it stops after the file it is on.</summary>
    public bool StopRequested { get; set; }

    public bool Active => Status is FilingRunStatus.Queued or FilingRunStatus.Running;
}
