using System.Collections.Concurrent;
using System.Text.Json;
using AgentDesk.Core.Abstractions;
using AgentDesk.Core.Domain;

namespace AgentDesk.Infrastructure.InMemory;

/// <summary>
/// A process-local document store that behaves like a Cosmos DB container for tests and offline development: documents
/// are stored as JSON (so callers never share mutable instances), and every write gets a new ETag.
/// </summary>
public sealed class InMemoryDocumentStore
{
    private readonly ConcurrentDictionary<(string Container, string Tenant, string Id), string> _docs = new();
    private readonly Lock _gate = new();

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public T? Get<T>(string container, string tenantId, string id) where T : TenantDocument =>
        _docs.TryGetValue((container, tenantId, id), out var json) ? JsonSerializer.Deserialize<T>(json, Json) : null;

    public T Create<T>(string container, T doc) where T : TenantDocument
    {
        lock (_gate)
        {
            var key = (container, doc.TenantId, doc.Id);
            if (_docs.ContainsKey(key)) throw new DuplicateException($"A document with id '{doc.Id}' already exists.");
            return Write(key, doc);
        }
    }

    public T Replace<T>(string container, T doc, string? ifMatch) where T : TenantDocument
    {
        lock (_gate)
        {
            var key = (container, doc.TenantId, doc.Id);
            if (!_docs.TryGetValue(key, out var json)) throw new NotFoundException(doc.DocType, doc.Id);
            if (ifMatch is not null)
            {
                var current = JsonSerializer.Deserialize<StoredEtag>(json, Json);
                if (current?.ETag != ifMatch) throw new ConcurrencyConflictException();
            }
            return Write(key, doc);
        }
    }

    public T Upsert<T>(string container, T doc) where T : TenantDocument
    {
        lock (_gate) return Write((container, doc.TenantId, doc.Id), doc);
    }

    public TResult Update<T, TResult>(string container, string tenantId, string id, Func<T?, (T Doc, TResult Result)> update)
        where T : TenantDocument
    {
        lock (_gate)
        {
            var (doc, result) = update(Get<T>(container, tenantId, id));
            Write((container, tenantId, id), doc);
            return result;
        }
    }

    public IEnumerable<T> All<T>(string container, string? tenantId, string docType) where T : TenantDocument
    {
        foreach (var ((c, tenant, _), json) in _docs)
        {
            if (c != container || (tenantId is not null && tenant != tenantId)) continue;
            using var parsed = JsonDocument.Parse(json);
            if (parsed.RootElement.TryGetProperty("docType", out var type) && type.GetString() == docType)
                yield return JsonSerializer.Deserialize<T>(json, Json)!;
        }
    }

    private T Write<T>((string, string, string) key, T doc) where T : TenantDocument
    {
        doc.ETag = $"\"{Guid.NewGuid():N}\"";
        var json = JsonSerializer.Serialize(doc, doc.GetType(), Json);
        _docs[key] = json;
        return (T)JsonSerializer.Deserialize(json, doc.GetType(), Json)!;
    }

    private sealed record StoredEtag(
        [property: System.Text.Json.Serialization.JsonPropertyName("_etag")] string? ETag);
}

/// <summary>Offset-based continuation tokens for in-memory paging.</summary>
internal static class InMemoryPaging
{
    public static Page<T> Page<T>(IEnumerable<T> ordered, int pageSize, string? token)
    {
        var offset = int.TryParse(token, out var o) && o > 0 ? o : 0;
        var items = ordered.Skip(offset).Take(pageSize + 1).ToList();
        var more = items.Count > pageSize;
        if (more) items.RemoveAt(items.Count - 1);
        return new Page<T>(items, more ? (offset + pageSize).ToString() : null);
    }
}
