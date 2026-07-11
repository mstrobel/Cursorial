using Cursorial.UI;
using Cursorial.UI.Controls;

namespace Cursorial.Tests.UI.Styling;

/// <summary>
/// The read-only tooling surface over pseudo-classes: interaction-backed names and registered
/// property mappings (what the designer's selector completion and quick docs enumerate).
/// </summary>
public class PseudoClassSurfaceTests
{
    [Fact]
    public void Interaction_names_enumerate_publicly()
    {
        Assert.Contains(":pointerover", InteractionPseudoClasses.Names);
        Assert.Contains(":disabled", InteractionPseudoClasses.Names);
        Assert.True(InteractionPseudoClasses.TryGetState(":pointerover", out var state));
        Assert.Equal(InteractionState.PointerOver, state);
    }

    [Fact]
    public void Snapshot_exposes_registered_mappings_with_vocabulary()
    {
        // Force registration (static ctor).
        _ = new CalendarButton();

        var snapshot = PseudoClassMapping.Snapshot();
        var today = Assert.Single(snapshot, m => m.OwnerType == typeof(CalendarButton) && m.PseudoClasses.Contains(":today"));
        Assert.Equal("IsToday", today.Property.Name);
    }
}
