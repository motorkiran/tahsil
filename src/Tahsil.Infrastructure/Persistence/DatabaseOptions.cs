namespace Tahsil.Infrastructure.Persistence;

/// <summary>
/// Database settings from the <c>Tahsil:Database</c> configuration section.
/// The connection string itself is read from <c>ConnectionStrings:Tahsil</c>.
/// </summary>
public sealed class DatabaseOptions
{
    public const string SectionName = "Tahsil:Database";

    public const string ConnectionStringName = "Tahsil";

    /// <summary>
    /// Applies pending migrations when the server starts. Disable it when this runs as a separate deployment step
    /// with the <c>migrate</c> command.
    /// </summary>
    public bool MigrateOnStartup { get; set; } = true;
}
