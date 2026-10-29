using Way2Do.Api.Data;

namespace Way2Do.Api.Domain;

/// <summary>
/// Ordenação padrão da listagem (escopo, seção 4): pendentes antes de concluídas; dentro de cada grupo, urgentes
/// primeiro; depois vencimento mais próximo (sem vencimento no fim), prioridade (alta → baixa), criação mais antiga
/// e, por fim, o id (ordem determinística). Casos em <c>tests/fixtures/ordenacao.json</c>.
/// </summary>
public static class TodoOrdering
{
    /// <summary>Ordena calculando a urgência uma vez por tarefa (não a cada comparação).</summary>
    public static IReadOnlyList<TodoItem> Sort(IEnumerable<TodoItem> tasks) =>
        tasks.Select(t => new Entry(t, UrgencyRule.IsUrgent(t.Title, t.Description)))
            .Order(EntryComparer.Instance)
            .Select(e => e.Task)
            .ToList();

    private readonly record struct Entry(TodoItem Task, bool Urgent);

    private sealed class EntryComparer : IComparer<Entry>
    {
        public static readonly EntryComparer Instance = new();

        public int Compare(Entry x, Entry y)
        {
            var (a, b) = (x.Task, y.Task);

            var byStatus = StatusRank(a.Status).CompareTo(StatusRank(b.Status));
            if (byStatus != 0)
                return byStatus;

            var byUrgency = y.Urgent.CompareTo(x.Urgent); // urgente (true) primeiro
            if (byUrgency != 0)
                return byUrgency;

            var byDue = (a.DueDate, b.DueDate) switch
            {
                (null, null) => 0,
                (null, _) => 1,
                (_, null) => -1,
                var (d1, d2) => d1.Value.CompareTo(d2.Value),
            };
            if (byDue != 0)
                return byDue;

            var byPriority = PriorityRank(a.Priority).CompareTo(PriorityRank(b.Priority));
            if (byPriority != 0)
                return byPriority;

            var byCreation = a.CreatedAt.CompareTo(b.CreatedAt);
            if (byCreation != 0)
                return byCreation;

            // Texto do id em minúsculas, ordem de código: igual ao frontend (Guid.CompareTo usa outra ordem de bytes).
            return string.CompareOrdinal(a.Id.ToString("D"), b.Id.ToString("D"));
        }

        private static int StatusRank(TodoStatus status) => status == TodoStatus.Pending ? 0 : 1;

        private static int PriorityRank(TodoPriority priority) => priority switch
        {
            TodoPriority.High => 0,
            TodoPriority.Medium => 1,
            _ => 2,
        };
    }
}
