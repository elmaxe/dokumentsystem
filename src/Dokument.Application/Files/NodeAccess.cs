using Dokument.Domain.Files;

namespace Dokument.Application.Files;

/// <summary>
/// Who may see and change a node (docs/design.md §9): its owner, and the people it or a folder above it is shared with,
/// as far as their highest role goes. This is the one predicate browsing, search and every change go through.
/// </summary>
public static class NodeAccess
{
    /// <summary>Nodes the person owns or that are shared with them, in or out of the trash.</summary>
    public static IQueryable<Node> VisibleTo(this IQueryable<Node> nodes, Actor actor) =>
        nodes.With(actor, Access.View);

    public static IQueryable<Node> EditableBy(this IQueryable<Node> nodes, Actor actor) =>
        nodes.With(actor, Access.Edit);

    public static IQueryable<Node> OwnedBy(this IQueryable<Node> nodes, Actor actor) =>
        nodes.Where(n => n.OwnerId == actor.UserId);

    public static IQueryable<Node> With(this IQueryable<Node> nodes, Actor actor, Access access) =>
        access == Access.Own
            ? nodes.OwnedBy(actor)
            : nodes.Where(n =>
                n.OwnerId == actor.UserId || SharedRole(actor.UserId, n.Path) >= (int)access
            );

    /// <summary>
    /// The highest role the shares on a node or the folders above it give a person: 0 for none, then the
    /// <see cref="Domain.Sharing.ShareRole"/> numbers. Runs in the database as <c>shared_role(user, path)</c>.
    /// </summary>
    public static int SharedRole(Guid userId, string path) =>
        throw new NotSupportedException("Only for use in database queries.");
}

/// <summary>What someone may do with a node, from least to most.</summary>
public enum Access
{
    View = 1,
    Edit = 2,
    Own = 3,
}
