using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Way2Do.Api.Data;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public const string ChangeSeqSequence = "change_seq";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public DbSet<User> Users => Set<User>();
    public DbSet<TodoItem> Tasks => Set<TodoItem>();
    public DbSet<OutboxEvent> OutboxEvents => Set<OutboxEvent>();
    public DbSet<WebhookSubscription> WebhookSubscriptions => Set<WebhookSubscription>();
    public DbSet<WebhookDelivery> WebhookDeliveries => Set<WebhookDelivery>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.HasSequence<long>(ChangeSeqSequence);

        model.Entity<User>(e =>
        {
            e.ToTable("users");
            e.Property(u => u.UserName).HasMaxLength(64);
            e.HasIndex(u => u.UserName).IsUnique();
        });

        model.Entity<TodoItem>(e =>
        {
            e.ToTable("tasks");
            e.Property(t => t.Id).ValueGeneratedNever();
            e.Property(t => t.Title).HasMaxLength(200);
            e.Property(t => t.Description).HasMaxLength(4000);
            e.Property(t => t.Priority).HasConversion(LowercaseEnum<TodoPriority>()).HasMaxLength(16);
            e.Property(t => t.Status).HasConversion(LowercaseEnum<TodoStatus>()).HasMaxLength(16);
            e.Property(t => t.Tags).HasColumnType("jsonb").HasConversion(JsonConverter<Dictionary<string, TagState>>(), DictionaryComparer<TagState>());
            e.Property(t => t.FieldTimestamps).HasColumnType("jsonb").HasConversion(JsonConverter<Dictionary<string, DateTimeOffset>>(), DictionaryComparer<DateTimeOffset>());
            // Novo valor da sequência a cada INSERT (default) e a cada UPDATE (trigger tasks_next_change_seq, na
            // migration). Fica no banco para nenhum caminho de escrita esquecer de avançar o cursor do pull.
            e.Property(t => t.ChangeSeq)
                .HasDefaultValueSql($"nextval('{ChangeSeqSequence}')")
                .ValueGeneratedOnAddOrUpdate();
            e.HasOne<User>().WithMany().HasForeignKey(t => t.UserId).OnDelete(DeleteBehavior.Cascade);
            // Cursor do pull: "tarefas do usuário com change_seq maior que X".
            e.HasIndex(t => new { t.UserId, t.ChangeSeq });
        });

        model.Entity<OutboxEvent>(e =>
        {
            e.ToTable("outbox_events");
            e.Property(o => o.Type).HasMaxLength(32);
            e.Property(o => o.Payload).HasColumnType("jsonb");
            e.HasOne<User>().WithMany().HasForeignKey(o => o.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<WebhookSubscription>(e =>
        {
            e.ToTable("webhook_subscriptions");
            e.Property(s => s.Url).HasMaxLength(2048);
            e.Property(s => s.DashboardUrl).HasMaxLength(2048);
            e.HasOne<User>().WithMany().HasForeignKey(s => s.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(s => new { s.UserId, s.Url }).IsUnique();
            // Busca do worker: retratos agendados cujo horário chegou.
            e.HasIndex(s => s.ResyncDueAt).HasFilter("resync_due_at IS NOT NULL");
        });

        model.Entity<WebhookDelivery>(e =>
        {
            e.ToTable("webhook_deliveries");
            e.Property(d => d.Status).HasConversion(LowercaseEnum<DeliveryStatus>()).HasMaxLength(16);
            e.Property(d => d.LastError).HasMaxLength(1000);
            e.HasOne<OutboxEvent>().WithMany().HasForeignKey(d => d.EventId).OnDelete(DeleteBehavior.Cascade);
            // Remover a assinatura cancela as entregas pendentes dela.
            e.HasOne<WebhookSubscription>().WithMany().HasForeignKey(d => d.SubscriptionId).OnDelete(DeleteBehavior.Cascade);
            // Busca do worker: entregas pendentes cujo horário de tentativa já chegou.
            e.HasIndex(d => new { d.Status, d.NextAttemptAt });
        });

        ApplySnakeCaseNames(model);
    }

    private static ValueConverter<TEnum, string> LowercaseEnum<TEnum>() where TEnum : struct, Enum =>
        new(v => v.ToString().ToLowerInvariant(), v => Enum.Parse<TEnum>(v, true));

    private static ValueConverter<T, string> JsonConverter<T>() where T : new() =>
        new(v => JsonSerializer.Serialize(v, Json), v => JsonSerializer.Deserialize<T>(v, Json) ?? new T());

    // Compara pelo conteúdo (chave a chave), independente da ordem: o jsonb do Postgres reordena as chaves ao gravar.
    private static ValueComparer<Dictionary<string, TValue>> DictionaryComparer<TValue>() =>
        new(
            (a, b) => DictionariesEqual(a, b),
            v => v.Count,
            v => new Dictionary<string, TValue>(v));

    private static bool DictionariesEqual<TValue>(Dictionary<string, TValue>? a, Dictionary<string, TValue>? b)
    {
        if (ReferenceEquals(a, b))
            return true;
        if (a is null || b is null || a.Count != b.Count)
            return false;
        foreach (var (key, value) in a)
        {
            if (!b.TryGetValue(key, out var other) || !EqualityComparer<TValue>.Default.Equals(value, other))
                return false;
        }
        return true;
    }

    // Colunas, chaves e índices em snake_case, sem depender de biblioteca de convenções de nome.
    private static void ApplySnakeCaseNames(ModelBuilder model)
    {
        foreach (var entity in model.Model.GetEntityTypes())
        {
            foreach (var property in entity.GetProperties())
                property.SetColumnName(ToSnakeCase(property.Name));
            foreach (var key in entity.GetKeys())
                key.SetName(ToSnakeCase(key.GetName()!));
            foreach (var fk in entity.GetForeignKeys())
                fk.SetConstraintName(ToSnakeCase(fk.GetConstraintName()!));
            foreach (var index in entity.GetIndexes())
                index.SetDatabaseName(ToSnakeCase(index.GetDatabaseName()!));
        }
    }

    internal static string ToSnakeCase(string name)
    {
        var sb = new StringBuilder(name.Length + 8);
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (char.IsUpper(c))
            {
                if (i > 0 && name[i - 1] != '_' && (char.IsLower(name[i - 1]) || (i + 1 < name.Length && char.IsLower(name[i + 1]))))
                    sb.Append('_');
                sb.Append(char.ToLowerInvariant(c));
            }
            else
            {
                sb.Append(c);
            }
        }
        return sb.ToString();
    }
}
