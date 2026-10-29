using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Way2Do.Api.Data;
using Way2Do.Api.Domain;
using Way2Do.Api.Webhooks;

namespace Way2Do.Api.Sync;

public sealed class SyncService(AppDbContext db, TimeProvider clock)
{
    public const int MaxOperationsPerPush = 500;
    public const int DefaultPullLimit = 200;
    public const int MaxPullLimit = 1000;

    // Semente do hash que transforma o id do usuário na chave do lock consultivo do push.
    private const int UserLockSeed = 7_250_002;
    private const int MaxAttempts = 3;

    /// <summary>
    /// Aplica um lote de operações numa única transação. O lock consultivo por usuário serializa os pushes do mesmo
    /// usuário (usuários diferentes seguem em paralelo): assim a ordem do <c>change_seq</c> é a ordem de commit, e um
    /// pull entre dois commits nunca pula uma alteração (escopo, seção 5).
    /// </summary>
    public async Task<PushResponse> PushAsync(Guid userId, string userName, IReadOnlyList<JsonElement> operations,
        CancellationToken ct)
    {
        if (operations.Count == 0)
            return new PushResponse([]);

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await TryPushAsync(userId, userName, operations, ct);
            }
            catch (DbUpdateException e) when (attempt < MaxAttempts && e.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
            {
                // Outro usuário criou, ao mesmo tempo, uma tarefa com o mesmo id (locks de usuários diferentes não
                // se bloqueiam). Refaz o lote: agora a tarefa existe, é de outro dono e a operação é recusada.
                db.ChangeTracker.Clear();
            }
        }
    }

    private async Task<PushResponse> TryPushAsync(Guid userId, string userName, IReadOnlyList<JsonElement> operations,
        CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended({userId.ToString()}, {UserLockSeed}))", ct);
        var now = clock.GetUtcNow(); // depois do lock: o horário acompanha a ordem de commit

        var parsed = operations.Select(PushOperationReader.Read).ToList();
        var ids = parsed.Where(p => p.Error is null).Select(p => p.Operation!.TaskId).Distinct().ToList();
        var tasks = await db.Tasks.Where(t => ids.Contains(t.Id)).ToDictionaryAsync(t => t.Id, ct);

        var outcomes = new List<Outcome>(operations.Count);
        var touched = new Dictionary<Guid, Touched>();

        for (var i = 0; i < operations.Count; i++)
        {
            var (op, error) = parsed[i];
            if (error is not null)
            {
                var opId = op?.OpId ?? PushOperationReader.IdOrEmpty(operations[i], "opId");
                var taskId = op?.TaskId ?? PushOperationReader.IdOrEmpty(operations[i], "taskId");
                outcomes.Add(Outcome.Reject(opId, taskId, "operation", error, null));
                continue;
            }

            var task = tasks.GetValueOrDefault(op!.TaskId);

            // Tarefa de outro usuário: recusada sem nenhum dado dela na resposta.
            if (task is not null && task.UserId != userId)
            {
                outcomes.Add(Outcome.Reject(op.OpId, op.TaskId, "taskId", "tarefa não encontrada", null));
                continue;
            }

            var isNew = task is null;
            task ??= new TodoItem { Id = op.TaskId, UserId = userId, Title = "" };
            var deletedBefore = task.DeletedAt is not null;

            MergeResult merge;
            try
            {
                merge = TodoMerge.Apply(task, op.ToChange(), now);
            }
            catch (InvalidChangeException e)
            {
                // O merge valida tudo antes de escrever: a tarefa não foi alterada.
                outcomes.Add(Outcome.Reject(op.OpId, op.TaskId, e.Field, e.Reason, isNew ? null : task));
                continue;
            }

            if (merge.Changed)
            {
                if (isNew)
                {
                    db.Tasks.Add(task);
                    tasks[task.Id] = task;
                }
                touched[task.Id] = touched.TryGetValue(task.Id, out var seen)
                    ? seen with { ContentChanged = seen.ContentChanged || merge.ContentChanged }
                    : new Touched(task, isNew, deletedBefore, merge.ContentChanged);
            }
            outcomes.Add(Outcome.Accept(op.OpId, op.TaskId));
        }

        // Uma nova versão por tarefa alterada no lote, mesmo que várias operações tenham mexido nela.
        foreach (var t in touched.Values.Where(t => t.ContentChanged))
            t.Task.Version++;
        await db.SaveChangesAsync(ct); // change_seq vem do banco (default no INSERT, trigger no UPDATE)

        var events = touched.Values
            .Where(t => t.ContentChanged && !(t.IsNew && t.Task.DeletedAt is not null)) // criada e excluída no lote: o consumidor nunca a viu
            .Select(t => (t.Task, Type: t.IsNew ? OutboxWriter.Created
                : t.Task.DeletedAt is not null && !t.DeletedBefore ? OutboxWriter.Deleted
                : OutboxWriter.Updated))
            .ToList();
        await OutboxWriter.AddEventsAsync(db, userId, userName, events, now, ct);
        await db.SaveChangesAsync(ct);

        await transaction.CommitAsync(ct);

        // Recusas trazem o estado ao fim do lote (com versão e change_seq gravados), não um estado intermediário.
        // Uma tarefa nova recusada que nenhuma outra operação criou não existe no servidor: estado nulo.
        return new PushResponse(outcomes.Select(o => o.ToResult(tasks, userId)).ToList());
    }

    /// <summary>Alterações do usuário com <c>change_seq</c> maior que o cursor, em ordem, incluindo excluídas.</summary>
    public async Task<PullResponse> PullAsync(Guid userId, long since, int limit, CancellationToken ct)
    {
        var page = await db.Tasks
            .AsNoTracking()
            .Where(t => t.UserId == userId && t.ChangeSeq > since)
            .OrderBy(t => t.ChangeSeq)
            .Take(limit + 1)
            .ToListAsync(ct);

        var hasMore = page.Count > limit;
        if (hasMore)
            page.RemoveAt(page.Count - 1);
        var cursor = page.Count > 0 ? page[^1].ChangeSeq : since;
        return new PullResponse(page.Select(SyncTask.From).ToList(), cursor, hasMore);
    }

    private sealed record Touched(TodoItem Task, bool IsNew, bool DeletedBefore, bool ContentChanged);

    private sealed record Outcome(Guid OpId, Guid TaskId, bool Accepted, string? Field, string? Reason, bool HasServerState)
    {
        public static Outcome Accept(Guid opId, Guid taskId) => new(opId, taskId, true, null, null, false);

        public static Outcome Reject(Guid opId, Guid taskId, string field, string reason, TodoItem? existing) =>
            new(opId, taskId, false, field, reason, existing is not null);

        public PushResult ToResult(IReadOnlyDictionary<Guid, TodoItem> tasks, Guid userId)
        {
            if (Accepted)
                return new PushResult(OpId, TaskId, PushResult.Accepted, null, null, null);
            // Estado só de tarefa do próprio usuário (o dicionário também pode conter tarefas de outros, citadas no
            // lote): a que já existia ou a que outra operação do lote criou.
            var state = tasks.TryGetValue(TaskId, out var t) && t.UserId == userId && (HasServerState || t.Version > 0)
                ? SyncTask.From(t)
                : null;
            return new PushResult(OpId, TaskId, PushResult.Rejected, Field, Reason, state);
        }
    }
}
