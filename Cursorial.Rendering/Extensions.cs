using Cursorial.Output;

namespace Cursorial.Rendering;

public static class Extensions
{
    extension(CellBuffer target)
    {
        public Size Size => target.Dimensions;
        public Rect Bounds => new(0, 0, target.Dimensions);
        
        public void Clear(in Cell blankCell)
        {
            target.Clear();
            target.Fill(blankCell);
        }

        public void Clear(in Style blankStyle)
        {
            Clear(target, Cell.Blank with { Style = blankStyle });
        }
    }

    extension(CellBufferView target)
    {
        public Size Size => target.Dimensions;

        public void Clear(in Cell blankCell)
        {
            target.Clear();
            target.Fill(blankCell);
        }

        public void Clear(in Style blankStyle)
        {
            Clear(target, Cell.Blank with { Style = blankStyle });
        }
    }
}