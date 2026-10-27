namespace Rosella.Server;

/// <summary>
/// A global handler that sends its own post-bind events instead of the
/// canned stub set — used by wl_seat, whose capabilities depend on the
/// devices the compositor found.
/// </summary>
public interface IBindEvents
{
    /// <summary>Runs right after the global is bound and its object exists.</summary>
    void SendBindEvents(ClientSession session, ServerObject bound);
}
