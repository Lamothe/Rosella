namespace Rosella.Server;

/// <summary>A global advertised on the registry.</summary>
public sealed record Global(uint Name, string Interface, uint Version);
