namespace Rosella.Server;

/// <summary>
/// Hook for the compositor to supply real handlers for bound globals; any
/// interface left null keeps the generic stub behaviour.
/// </summary>
public interface IGlobalHandlerFactory
{
    IRequestHandler? Create(string interfaceName);
}
