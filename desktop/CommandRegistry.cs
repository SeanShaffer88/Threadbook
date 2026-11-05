namespace Threadbook.Desktop;

internal sealed record PaletteCommand(string Id, string Label, string Group, Func<string?> DisabledReason, Func<bool> Execute);

internal sealed class CommandRegistry(Func<string?> privacy)
{
    private readonly Dictionary<string, PaletteCommand> entries = new(StringComparer.Ordinal);
    internal IEnumerable<PaletteCommand> Entries => entries.Values;
    internal void Add(PaletteCommand command) => entries.Add(command.Id, command);
    internal string? DisabledReason(PaletteCommand command) => privacy() ?? command.DisabledReason();
    internal bool TryExecute(PaletteCommand command)
    {
        if (DisabledReason(command) is not null) return false;
        return command.Execute();
    }
    internal bool TryExecute(string id) => entries.TryGetValue(id, out var command) && TryExecute(command);
    internal static IReadOnlyList<PaletteCommand> Search(IEnumerable<PaletteCommand> source, string query)
    {
        query = query.Trim();
        int Rank(string label) => label.Equals(query, StringComparison.OrdinalIgnoreCase) ? 0 : label.StartsWith(query, StringComparison.OrdinalIgnoreCase) ? 1 : label.Contains(query, StringComparison.OrdinalIgnoreCase) ? 2 : 3;
        return source.Select((command, order) => (command, order, rank: Rank(command.Label)))
            .Where(x => x.rank < 3).OrderBy(x => x.rank).ThenBy(x => x.order).Take(100).Select(x => x.command).ToArray();
    }
}
