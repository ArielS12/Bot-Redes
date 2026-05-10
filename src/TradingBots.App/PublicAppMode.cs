namespace TradingBots.App;

/// <summary>
/// Cuando es true, la API no exige JWT y la UI asume sesion siempre disponible (sin login obligatorio).
/// Revertir a false y descomentar bloques en Program.cs para volver a proteger endpoints.
/// </summary>
public static class PublicAppMode
{
    /// <summary>Poner en false y restaurar JWT en Program.cs para volver a proteger la API.</summary>
    public static bool Enabled { get; set; } = true;
}
