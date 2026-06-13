using Cursorial.UI;

namespace ChipVendorAlpha
{
    /// <summary>One of the two registry-known <c>Chip</c> element types (S18 — default-resolver ambiguity).</summary>
    public class Chip : UIElement
    {
        /// <summary>Registration marker so the type is registry-known (SD1).</summary>
        public static readonly StyledProperty<int> AlphaP = UIProperty.Register<Chip, int>("AlphaP");
    }
}

namespace ChipVendorBeta
{
    /// <summary>The second registry-known <c>Chip</c> element type (S18).</summary>
    public class Chip : UIElement
    {
        /// <summary>Registration marker so the type is registry-known (SD1).</summary>
        public static readonly StyledProperty<int> BetaP = UIProperty.Register<Chip, int>("BetaP");
    }
}
