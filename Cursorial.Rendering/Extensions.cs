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
            if (blankCell.Style.IsDefault) return;
            target.Fill(blankCell);
        }

        public void Clear(in Style blankStyle)
        {
            Clear(target, Cell.Blank with { Style = blankStyle });
        }

        public void ClearCells(in Rect rect, in Cell blankCell)
        {
            target.ClearCells(rect);
            if (blankCell.Style.IsDefault) return;
            target.Fill(rect, blankCell);
        }

        public void ClearCells(in Rect rect, in Style blankStyle)
        {
            ClearCells(target, rect, Cell.Blank with { Style = blankStyle });
        }
    }

    extension(CellBufferView target)
    {
        public Size Size => target.Dimensions;

        public void Clear(in Cell blankCell)
        {
            target.Clear();
            if (blankCell.Style.IsDefault) return;
            target.Fill(blankCell);
        }

        public void Clear(in Style blankStyle)
        {
            Clear(target, Cell.Blank with { Style = blankStyle });
        }
        
        public void ClearCells(in Rect rect, in Cell blankCell)
        {
            target.View(rect).Clear(blankCell);
        }

        public void ClearCells(in Rect rect, in Style blankStyle)
        {
            target.View(rect).Clear(blankStyle);
        }
    }
}