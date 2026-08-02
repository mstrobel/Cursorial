namespace Cursorial.Gallery.ViewModels;

/// <summary>The Event Routing page: interactive ancestor/owner scenarios whose mouse-down / key-down routes
/// are visualized node by node — element types, template boundaries, and the popup→owner bridge hops.</summary>
public sealed class EventRoutingViewModel : PageViewModel
{
    public override string Title => "Event Routing";

    public override string Summary => "Interact with the scenarios; the route each mouse/key down will take is rendered live.";

    public override bool IsContentScrollable => false;
}
