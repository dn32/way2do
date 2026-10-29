using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Way2Do.Api.Data;

namespace Way2Do.Api.Tests;

[Collection(PostgresCollection.Name)]
public sealed class DataTests(PostgresFixture postgres)
{
    private async Task<(AsyncServiceScope Scope, AppDbContext Db, Guid UserId)> OpenAsync()
    {
        postgres.SharedApi.CreateClient(); // garante migrations e seed aplicados
        var scope = postgres.SharedApi.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var userId = await db.Users.Where(u => u.UserName == "ana").Select(u => u.Id).SingleAsync();
        return (scope, db, userId);
    }

    private static TodoItem NewTask(Guid userId, string title) => new()
    {
        Id = Guid.NewGuid(),
        UserId = userId,
        Title = title,
        CreatedAt = DateTimeOffset.UtcNow,
        Tags = new() { ["obra"] = new TagState(true, DateTimeOffset.UtcNow) },
        FieldTimestamps = new() { ["title"] = DateTimeOffset.UtcNow },
    };

    [Fact]
    public async Task Change_seq_avanca_no_insert_e_em_todo_update()
    {
        var (scope, db, userId) = await OpenAsync();
        await using var _ = scope;

        var task = NewTask(userId, "Comprar EPI");
        db.Tasks.Add(task);
        await db.SaveChangesAsync();
        var afterInsert = task.ChangeSeq;

        task.Title = "Comprar EPI para a equipe";
        await db.SaveChangesAsync();
        var afterUpdate = task.ChangeSeq;

        Assert.True(afterInsert > 0);
        Assert.True(afterUpdate > afterInsert, $"change_seq não avançou no UPDATE ({afterInsert} → {afterUpdate})");
        var stored = await db.Tasks.AsNoTracking().Where(t => t.Id == task.Id).Select(t => t.ChangeSeq).SingleAsync();
        Assert.Equal(afterUpdate, stored);
    }

    [Fact]
    public async Task Dicionario_com_mesmo_conteudo_nao_gera_update_e_alteracao_de_tag_gera()
    {
        var (scope, db, userId) = await OpenAsync();
        await using var _ = scope;
        var task = NewTask(userId, "Revisar orçamento");
        db.Tasks.Add(task);
        await db.SaveChangesAsync();

        // Mesmo conteúdo, instância nova: não é alteração.
        task.Tags = new Dictionary<string, TagState>(task.Tags);
        Assert.False(db.ChangeTracker.HasChanges());

        // Alteração dentro do dicionário: é detectada e gravada.
        task.Tags["cliente"] = new TagState(true, DateTimeOffset.UtcNow);
        Assert.True(db.ChangeTracker.HasChanges());
        await db.SaveChangesAsync();

        var stored = await db.Tasks.AsNoTracking().SingleAsync(t => t.Id == task.Id);
        Assert.Equal(["cliente", "obra"], stored.Tags.Keys.Order());
    }
}
