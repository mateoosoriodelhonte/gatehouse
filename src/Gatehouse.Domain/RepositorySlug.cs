namespace Gatehouse.Domain;

public sealed record RepositorySlug(string Owner, string Name)
{
    public override string ToString() => $"{Owner}/{Name}";
}
