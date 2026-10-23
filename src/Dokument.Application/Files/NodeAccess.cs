using Dokument.Domain.Files;

namespace Dokument.Application.Files;

/// <summary>
/// Who may see and change a node (docs/design.md §9). Until sharing lands in M4 only the owner may; this is the one
/// predicate that grants will extend, and search will reuse it.
/// </summary>
public static class NodeAccess
{
    public static IQueryable<Node> VisibleTo(this IQueryable<Node> nodes, Actor actor) =>
        nodes.Where(n => n.OwnerId == actor.UserId);
}
