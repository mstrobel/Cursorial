using System.ComponentModel;

using Cursorial.UI.Xaml;

namespace Cursorial.Tests.UI.Xaml.XamlMatrix;

/// <summary>
/// ISupportInitialize handling in the runtime object-graph builder (matrix X1 activation): BeginInit/EndInit
/// bracket the member sets, and a fault EndInit raises never masks a member-assignment fault — the two are
/// combined into one <see cref="XamlParseException"/> with the member fault as the root-cause inner exception.
/// </summary>
public sealed class SupportInitializeTests : LoaderTestBase
{
    [Fact] // Members are applied between BeginInit and EndInit; each is called exactly once.
    public void ISupportInitialize_BracketsMemberSets()
    {
        var probe = Load<InitProbe>("<InitProbe Value=\"5\"/>");

        Assert.Equal(1, probe.BeginCount);
        Assert.Equal(1, probe.EndCount);
        Assert.Equal(5, probe.Value);
        Assert.True(probe.ValueSetWhileInitializing, "member sets must land between BeginInit and EndInit");
    }

    [Fact] // EndInit throws with clean member application → a positioned XamlParseException carrying the fault.
    public void EndInitFault_SurfacesAsPositionedXamlParseException()
    {
        var ex = Assert.Throws<XamlParseException>(() => Load("<EndInitFaultProbe Value=\"5\"/>"));

        Assert.Equal(XamlDiagnosticCodes.InitializationFailed, ex.Code);
        Assert.Contains("ENDINIT_BOOM", ex.Message);
        Assert.True(ex.Line > 0 && ex.Column > 0, "the failure must carry a 1-based line/column");

        var boom = Assert.IsType<InvalidOperationException>(ex.InnerException);
        Assert.Contains("ENDINIT_BOOM", boom.Message);
    }

    [Fact] // THE fix: an EndInit fault must not mask a member-assignment fault — both surface, member as root cause.
    public void EndInitFault_DoesNotMaskMemberFault()
    {
        var ex = Assert.Throws<XamlParseException>(() => Load("<EndInitFaultProbe Boom=\"x\"/>"));

        // Both faults are represented — the member fault's message leads, the EndInit fault is concatenated in.
        Assert.Contains("MEMBER_BOOM", ex.Message);
        Assert.Contains("ENDINIT_BOOM", ex.Message);

        // The member fault is the root cause: reachable (unwrapped from TargetInvocationException) as an inner.
        Assert.True(FindInner<InvalidOperationException>(ex, "MEMBER_BOOM"),
            "the member-assignment fault must be preserved as the root-cause inner exception");
    }

    [Fact] // A member fault with a clean EndInit propagates unchanged: no InitializationFailed wrap (EndInit still
    //       ran to balance BeginInit, but doesn't interfere) — identical to a type without ISupportInitialize.
    public void MemberFault_WithCleanEndInit_PropagatesUnchanged()
    {
        var ex = Assert.ThrowsAny<Exception>(() => Load("<InitProbe Boom=\"x\"/>"));

        Assert.False(ex is XamlParseException { Code: XamlDiagnosticCodes.InitializationFailed },
            "a clean EndInit must not rewrap the member fault as InitializationFailed");
        Assert.True(FindInner<InvalidOperationException>(ex, "MEMBER_BOOM"),
            "the member-assignment fault must be intact");
    }

    private static bool FindInner<T>(Exception? ex, string messageFragment)
        where T : Exception
    {
        for (var current = ex; current is not null; current = current.InnerException)
        {
            if (current is T && current.Message.Contains(messageFragment))
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>Base ISupportInitialize probe: counts BeginInit/EndInit, records whether member sets land inside the
/// init bracket, and exposes a set-only <c>Boom</c> member whose setter always throws (the member-fault arm).</summary>
public abstract class InitProbeBase : ISupportInitialize
{
    private int _value;

    public int BeginCount { get; private set; }

    public int EndCount { get; private set; }

    public bool Initializing { get; private set; }

    public bool ValueSetWhileInitializing { get; private set; }

    public int Value
    {
        get => _value;
        set
        {
            _value = value;
            if (Initializing)
            {
                ValueSetWhileInitializing = true;
            }
        }
    }

    /// <summary>A set-only member whose setter always throws — drives the member-assignment fault arm.</summary>
    public string Boom
    {
        set => throw new InvalidOperationException("MEMBER_BOOM:" + value);
    }

    public void BeginInit()
    {
        BeginCount++;
        Initializing = true;
    }

    public void EndInit()
    {
        EndCount++;
        Initializing = false;
        OnEndInit();
    }

    protected virtual void OnEndInit()
    {
    }
}

/// <summary>An ISupportInitialize probe whose EndInit never throws.</summary>
public sealed class InitProbe : InitProbeBase;

/// <summary>An ISupportInitialize probe whose EndInit always throws — the EndInit-fault arm.</summary>
public sealed class EndInitFaultProbe : InitProbeBase
{
    protected override void OnEndInit()
        => throw new InvalidOperationException("ENDINIT_BOOM");
}
