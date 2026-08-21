namespace Cursorial.Gallery.Infrastructure;

public sealed record Described<T>(T Value, string Description)
{
    public override string ToString() => Description;
}