namespace Cursorial.Rendering;

public static class Extensions
{
    extension(CellBuffer target)
    {
        public Size Size => target.Dimensions;
        public Rect Bounds => new(0, 0, target.Dimensions);
    }

    extension(CellBufferView target)
    {
        public Size Size => target.Dimensions;
    }
}