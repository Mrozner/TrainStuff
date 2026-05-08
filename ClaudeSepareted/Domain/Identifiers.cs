namespace ClaudeSepareted.Domain
{
    /// <summary>
    /// Strongly typed IDs to prevent primitive obsession and domain leakage between
    /// physical hardware sensors (SubSections) and logical routing blocks (Sections).
    /// </summary>
    public readonly record struct SectionId(int Value);

    public readonly record struct SubSectionId(int Value);
}
