namespace Tahsil.Application.Common;

/// <summary>Whether an installation moves real money.</summary>
public enum TahsilMode
{
    /// <summary>Simulator and sandbox connections, time travel and a visible "Test mode" banner.</summary>
    Test,

    /// <summary>Real payments. Startup refuses settings that are only safe for testing.</summary>
    Live,
}
