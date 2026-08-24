using System.Text.Json;
using Dapper;
using Stashographer.Data;
using Stashographer.Data.Entities;

namespace Stashographer.Services.Inventory;

/// <summary>Persistence and deterministic inventory matching for recipes and other BOMs.</summary>
public sealed class BomService(
    IDbConnectionFactory db,
    InventoryService inventory,
    AttributeNameService attributeNames)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<List<BomDefinition>> GetAllAsync(CancellationToken ct = default)
    {
        using var conn = await db.OpenAsync(ct);
        var definitions = await conn.QueryAsync<BomDefinition>("""
            SELECT d.Id, d.Name, d.Kind, d.Description, d.OutputQuantity, d.OutputUnit,
                   d.CreatedAt, d.UpdatedAt, COUNT(r.Id) AS RequirementCount
            FROM BomDefinitions d
            LEFT JOIN BomRequirements r ON r.BomDefinitionId = d.Id
            GROUP BY d.Id
            ORDER BY d.Kind, d.Name COLLATE NOCASE;
            """);
        return definitions.ToList();
    }

    public async Task<BomDefinition?> GetAsync(int id, CancellationToken ct = default)
    {
        using var conn = await db.OpenAsync(ct);
        var definition = await conn.QuerySingleOrDefaultAsync<BomDefinition>("""
            SELECT Id, Name, Kind, Description, OutputQuantity, OutputUnit, CreatedAt, UpdatedAt
            FROM BomDefinitions WHERE Id = @id;
            """, new { id });
        if (definition is null) return null;

        var rows = (await conn.QueryAsync<RequirementRow>("""
            SELECT Id, BomDefinitionId, Name, Quantity, Unit, IsOptional, MatchMode, MatchItemKindId,
                   MatchText, RequiredAttributesJson, SortOrder
            FROM BomRequirements
            WHERE BomDefinitionId = @id
            ORDER BY SortOrder, Id;
            """, new { id })).ToList();
        var candidateRows = await conn.QueryAsync<CandidateRow>("""
            SELECT c.RequirementId, c.ItemId
            FROM BomRequirementCandidates c
            JOIN BomRequirements r ON r.Id = c.RequirementId
            WHERE r.BomDefinitionId = @id
            ORDER BY c.RequirementId, c.ItemId;
            """, new { id });
        var candidates = candidateRows
            .GroupBy(x => x.RequirementId)
            .ToDictionary(x => x.Key, x => x.Select(y => y.ItemId).ToList());
        definition.Requirements = rows.Select(row => Map(row, candidates.GetValueOrDefault(row.Id) ?? [])).ToList();
        definition.RequirementCount = definition.Requirements.Count;
        return definition;
    }

    public async Task<BomDefinition> SaveDefinitionAsync(
        BomDefinition definition, CancellationToken ct = default)
    {
        PrepareDefinition(definition);
        var now = DateTimeOffset.UtcNow;
        using var conn = await db.OpenAsync(ct);
        if (definition.Id == 0)
        {
            definition.CreatedAt = now;
            definition.UpdatedAt = now;
            definition.Id = await conn.ExecuteScalarAsync<int>("""
                INSERT INTO BomDefinitions
                    (Name, Kind, Description, OutputQuantity, OutputUnit, CreatedAt, UpdatedAt)
                VALUES (@Name, @Kind, @Description, @OutputQuantity, @OutputUnit, @CreatedAt, @UpdatedAt);
                SELECT last_insert_rowid();
                """, definition);
        }
        else
        {
            definition.UpdatedAt = now;
            var changed = await conn.ExecuteAsync("""
                UPDATE BomDefinitions SET Name=@Name, Kind=@Kind, Description=@Description,
                    OutputQuantity=@OutputQuantity, OutputUnit=@OutputUnit, UpdatedAt=@UpdatedAt
                WHERE Id=@Id;
                """, definition);
            if (changed == 0) throw new InvalidOperationException("The recipe or build no longer exists.");
        }
        return definition;
    }

    public async Task<BomRequirement> SaveRequirementAsync(
        BomRequirement requirement, CancellationToken ct = default)
    {
        await PrepareRequirementAsync(requirement, ct);

        using var conn = await db.OpenAsync(ct);
        using var tx = conn.BeginTransaction();
        if (requirement.Id == 0)
        {
            if (requirement.SortOrder == 0)
                requirement.SortOrder = (await conn.ExecuteScalarAsync<int?>("""
                    SELECT MAX(SortOrder) FROM BomRequirements WHERE BomDefinitionId = @BomDefinitionId;
                    """, requirement, tx) ?? 0) + 1;
            requirement.Id = await conn.ExecuteScalarAsync<int>("""
                INSERT INTO BomRequirements
                    (BomDefinitionId, Name, Quantity, Unit, IsOptional, MatchMode, MatchItemKindId,
                     MatchText, RequiredAttributesJson, SortOrder)
                VALUES (@BomDefinitionId, @Name, @Quantity, @Unit, @IsOptional, @MatchMode, @MatchItemKindId,
                        @MatchText, @RequiredAttributesJson, @SortOrder);
                SELECT last_insert_rowid();
                """, ToParameters(requirement), tx);
        }
        else
        {
            var changed = await conn.ExecuteAsync("""
                UPDATE BomRequirements SET Name=@Name, Quantity=@Quantity, Unit=@Unit,
                    IsOptional=@IsOptional, MatchMode=@MatchMode, MatchItemKindId=@MatchItemKindId, MatchText=@MatchText,
                    RequiredAttributesJson=@RequiredAttributesJson, SortOrder=@SortOrder
                WHERE Id=@Id AND BomDefinitionId=@BomDefinitionId;
                """, ToParameters(requirement), tx);
            if (changed == 0) throw new InvalidOperationException("The requirement no longer exists.");
            await conn.ExecuteAsync(
                "DELETE FROM BomRequirementCandidates WHERE RequirementId = @id;",
                new { id = requirement.Id }, tx);
        }

        foreach (var itemId in requirement.CandidateItemIds)
            await conn.ExecuteAsync("""
                INSERT INTO BomRequirementCandidates (RequirementId, ItemId)
                VALUES (@requirementId, @itemId);
                """, new { requirementId = requirement.Id, itemId }, tx);
        await conn.ExecuteAsync(
            "UPDATE BomDefinitions SET UpdatedAt = @now WHERE Id = @id;",
            new { now = DateTimeOffset.UtcNow, id = requirement.BomDefinitionId }, tx);
        tx.Commit();
        return requirement;
    }

    /// <summary>Atomically creates a reviewed definition and all of its requirements.</summary>
    public async Task<BomDefinition> CreateWithRequirementsAsync(
        BomDefinition definition, IReadOnlyList<BomRequirement> requirements,
        CancellationToken ct = default)
    {
        if (definition.Id != 0) throw new InvalidOperationException("The generated draft has already been saved.");
        PrepareDefinition(definition);
        foreach (var requirement in requirements) await PrepareRequirementAsync(requirement, ct);

        var now = DateTimeOffset.UtcNow;
        definition.CreatedAt = now;
        definition.UpdatedAt = now;
        using var conn = await db.OpenAsync(ct);
        using var tx = conn.BeginTransaction();
        definition.Id = await conn.ExecuteScalarAsync<int>("""
            INSERT INTO BomDefinitions
                (Name, Kind, Description, OutputQuantity, OutputUnit, CreatedAt, UpdatedAt)
            VALUES (@Name, @Kind, @Description, @OutputQuantity, @OutputUnit, @CreatedAt, @UpdatedAt);
            SELECT last_insert_rowid();
            """, definition, tx);
        for (var index = 0; index < requirements.Count; index++)
        {
            var requirement = requirements[index];
            requirement.BomDefinitionId = definition.Id;
            requirement.SortOrder = index + 1;
            requirement.Id = await conn.ExecuteScalarAsync<int>("""
                INSERT INTO BomRequirements
                    (BomDefinitionId, Name, Quantity, Unit, IsOptional, MatchMode, MatchItemKindId,
                     MatchText, RequiredAttributesJson, SortOrder)
                VALUES (@BomDefinitionId, @Name, @Quantity, @Unit, @IsOptional, @MatchMode, @MatchItemKindId,
                        @MatchText, @RequiredAttributesJson, @SortOrder);
                SELECT last_insert_rowid();
                """, ToParameters(requirement), tx);
            foreach (var itemId in requirement.CandidateItemIds)
                await conn.ExecuteAsync("""
                    INSERT INTO BomRequirementCandidates (RequirementId, ItemId)
                    VALUES (@requirementId, @itemId);
                    """, new { requirementId = requirement.Id, itemId }, tx);
        }
        tx.Commit();
        definition.Requirements = requirements.ToList();
        definition.RequirementCount = requirements.Count;
        return definition;
    }

    public async Task DeleteDefinitionAsync(int id, CancellationToken ct = default)
    {
        using var conn = await db.OpenAsync(ct);
        await conn.ExecuteAsync("DELETE FROM BomDefinitions WHERE Id = @id;", new { id });
    }

    public async Task DeleteRequirementAsync(int id, CancellationToken ct = default)
    {
        using var conn = await db.OpenAsync(ct);
        await conn.ExecuteAsync("DELETE FROM BomRequirements WHERE Id = @id;", new { id });
    }

    public async Task<BomEvaluation?> EvaluateAsync(int id, CancellationToken ct = default)
    {
        var definition = await GetAsync(id, ct);
        if (definition is null) return null;
        var active = (await inventory.QueryAsync(new ItemQuery(), ct))
            .Where(item => item.Quantity > 0)
            .ToList();
        var evaluations = definition.Requirements.Select(requirement =>
        {
            var matches = active.Where(item => Matches(requirement, item)).ToList();
            var available = matches.Sum(item => item.Quantity);
            return new BomRequirementAvailability(
                requirement, matches, available,
                available >= requirement.Quantity);
        }).ToList();
        return new BomEvaluation(definition, evaluations, CanAllocate(evaluations));
    }

    /// <summary>
    /// Produces an exact lot-level allocation for a requested recipe output. Required
    /// ingredients are allocated globally so interchangeable requirements cannot double-count
    /// stock. Among valid allocations, earlier expiries are preferred.
    /// </summary>
    public async Task<BomAllocation?> GetAllocationAsync(
        int id, decimal outputQuantity, CancellationToken ct = default)
    {
        if (outputQuantity <= 0)
            throw new InvalidOperationException("Meal output quantity must be greater than zero.");
        var definition = await GetAsync(id, ct);
        if (definition is null) return null;
        var active = (await inventory.QueryAsync(new ItemQuery(), ct))
            .Where(item => item.Quantity > 0 && !item.IsCheckedOut)
            .ToList();
        return Allocate(definition, outputQuantity, active);
    }

    internal static BomAllocation Allocate(
        BomDefinition definition, decimal outputQuantity, IReadOnlyList<Item> activeItems)
    {
        if (definition.OutputQuantity <= 0)
            throw new InvalidOperationException("The recipe output quantity is invalid.");
        if (outputQuantity <= 0)
            throw new InvalidOperationException("Meal output quantity must be greater than zero.");

        var requirements = definition.Requirements.Where(requirement => !requirement.IsOptional).ToList();
        if (requirements.Count == 0)
            return new BomAllocation(definition, outputQuantity, [],
            [
                new BomRequirementShortfall(
                    0, "Recipe requirements", 1, 0, null)
            ]);

        var items = activeItems.Where(item => item.Quantity > 0 && !item.IsCheckedOut)
            .OrderBy(item => item.ExpiryDate is null)
            .ThenBy(item => item.ExpiryDate)
            .ThenBy(item => item.Id)
            .ToList();
        var source = 0;
        var itemOffset = 1;
        var requirementOffset = itemOffset + items.Count;
        var sink = requirementOffset + requirements.Count;
        var graph = Enumerable.Range(0, sink + 1).Select(_ => new List<FlowEdge>()).ToArray();
        var tracked = new Dictionary<(int ItemIndex, int RequirementIndex), FlowEdge>();
        var scale = outputQuantity / definition.OutputQuantity;
        var demands = requirements.Select(requirement => requirement.Quantity * scale).ToArray();

        for (var itemIndex = 0; itemIndex < items.Count; itemIndex++)
        {
            AddEdge(graph, source, itemOffset + itemIndex, items[itemIndex].Quantity, itemIndex + 1);
            for (var requirementIndex = 0; requirementIndex < requirements.Count; requirementIndex++)
            {
                if (!Matches(requirements[requirementIndex], items[itemIndex])) continue;
                tracked[(itemIndex, requirementIndex)] = AddEdge(
                    graph,
                    itemOffset + itemIndex,
                    requirementOffset + requirementIndex,
                    items[itemIndex].Quantity,
                    0);
            }
        }
        for (var requirementIndex = 0; requirementIndex < requirements.Count; requirementIndex++)
            AddEdge(graph, requirementOffset + requirementIndex, sink, demands[requirementIndex], 0);

        var targetFlow = demands.Sum();
        var flow = 0m;
        while (flow < targetFlow
               && TryFindCheapestPath(graph, source, sink, out var parents))
        {
            var amount = targetFlow - flow;
            for (var node = sink; node != source;)
            {
                var parent = parents[node];
                amount = Math.Min(amount, graph[parent.From][parent.EdgeIndex].Capacity);
                node = parent.From;
            }
            for (var node = sink; node != source;)
            {
                var parent = parents[node];
                var edge = graph[parent.From][parent.EdgeIndex];
                edge.Capacity -= amount;
                graph[node][edge.ReverseIndex].Capacity += amount;
                node = parent.From;
            }
            flow += amount;
        }

        var lines = new List<BomAllocationLine>();
        var allocatedByRequirement = new decimal[requirements.Count];
        foreach (var ((itemIndex, requirementIndex), edge) in tracked)
        {
            var allocated = edge.OriginalCapacity - edge.Capacity;
            if (allocated <= 0) continue;
            var item = items[itemIndex];
            var requirement = requirements[requirementIndex];
            allocatedByRequirement[requirementIndex] += allocated;
            lines.Add(new BomAllocationLine(
                requirement.Id,
                requirement.Name,
                item.Id,
                item.Name,
                allocated,
                requirement.Unit ?? item.Unit,
                item.ExpiryDate));
        }
        lines = lines
            .OrderBy(line => line.ExpiryDate is null)
            .ThenBy(line => line.ExpiryDate)
            .ThenBy(line => line.RequirementName)
            .ThenBy(line => line.ItemId)
            .ToList();
        var shortfalls = requirements.Select((requirement, index) =>
                new BomRequirementShortfall(
                    requirement.Id,
                    requirement.Name,
                    demands[index],
                    allocatedByRequirement[index],
                    requirement.Unit))
            .Where(shortfall => shortfall.AllocatedQuantity < shortfall.RequiredQuantity)
            .ToList();
        return new BomAllocation(definition, outputQuantity, lines, shortfalls);
    }

    internal static bool Matches(BomRequirement requirement, Item item)
    {
        if (!UnitMatches(requirement.Unit, item.Unit)) return false;
        if (requirement.MatchMode == BomMatchMode.ExplicitCandidates)
            return requirement.CandidateItemIds.Contains(item.Id);
        if (requirement.MatchItemKindId is { } kindId && item.ItemKindId != kindId) return false;

        var selector = Clean(requirement.MatchText) ?? requirement.Name;
        var words = selector.Split((char[]?)null,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var searchable = InventoryService.NormalizeName(
            item.Name + " " + string.Join(' ', item.Attributes.Values));
        if (words.Length > 0 && words.Any(word => !searchable.Contains(
                InventoryService.NormalizeName(word), StringComparison.Ordinal))) return false;

        if (requirement.RequiredAttributes.Count == 0) return true;
        var canonical = AttributeNameService.Canonicalize(
            item.Attributes, requirement.RequiredAttributes.Keys.ToList());
        return requirement.RequiredAttributes.All(required =>
            canonical.TryGetValue(required.Key, out var actual)
            && InventoryService.NormalizeName(actual) == InventoryService.NormalizeName(required.Value));
    }

    private static bool CanAllocate(IReadOnlyList<BomRequirementAvailability> evaluations)
    {
        var required = evaluations.Where(x => !x.Requirement.IsOptional).ToList();
        if (required.Count == 0) return true;
        var items = required.SelectMany(x => x.MatchingItems).DistinctBy(x => x.Id).ToList();
        var source = 0;
        var itemOffset = 1;
        var requirementOffset = itemOffset + items.Count;
        var sink = requirementOffset + required.Count;
        var capacity = new decimal[sink + 1, sink + 1];
        for (var i = 0; i < items.Count; i++) capacity[source, itemOffset + i] = items[i].Quantity;
        var totalDemand = required.Sum(x => x.Requirement.Quantity);
        for (var r = 0; r < required.Count; r++)
        {
            capacity[requirementOffset + r, sink] = required[r].Requirement.Quantity;
            foreach (var match in required[r].MatchingItems)
            {
                var i = items.FindIndex(item => item.Id == match.Id);
                capacity[itemOffset + i, requirementOffset + r] = totalDemand;
            }
        }

        decimal flow = 0;
        while (TryFindPath(capacity, source, sink, out var path))
        {
            var amount = path.Zip(path.Skip(1), (from, to) => capacity[from, to]).Min();
            foreach (var (from, to) in path.Zip(path.Skip(1)))
            {
                capacity[from, to] -= amount;
                capacity[to, from] += amount;
            }
            flow += amount;
        }
        return flow >= totalDemand;
    }

    private static bool TryFindPath(decimal[,] capacity, int source, int sink, out List<int> path)
    {
        var parent = Enumerable.Repeat(-1, capacity.GetLength(0)).ToArray();
        var queue = new Queue<int>();
        queue.Enqueue(source);
        parent[source] = source;
        while (queue.Count > 0 && parent[sink] < 0)
        {
            var from = queue.Dequeue();
            for (var to = 0; to < capacity.GetLength(0); to++)
            {
                if (parent[to] >= 0 || capacity[from, to] <= 0) continue;
                parent[to] = from;
                queue.Enqueue(to);
            }
        }
        path = [];
        if (parent[sink] < 0) return false;
        for (var node = sink; node != source; node = parent[node]) path.Add(node);
        path.Add(source);
        path.Reverse();
        return true;
    }

    private static FlowEdge AddEdge(
        IReadOnlyList<List<FlowEdge>> graph, int from, int to, decimal capacity, int cost)
    {
        var forward = new FlowEdge(to, graph[to].Count, capacity, capacity, cost);
        var reverse = new FlowEdge(from, graph[from].Count, 0, 0, -cost);
        graph[from].Add(forward);
        graph[to].Add(reverse);
        return forward;
    }

    private static bool TryFindCheapestPath(
        IReadOnlyList<List<FlowEdge>> graph,
        int source,
        int sink,
        out FlowParent[] parents)
    {
        const long infinity = long.MaxValue / 4;
        var distances = Enumerable.Repeat(infinity, graph.Count).ToArray();
        parents = Enumerable.Repeat(new FlowParent(-1, -1), graph.Count).ToArray();
        distances[source] = 0;
        for (var pass = 0; pass < graph.Count - 1; pass++)
        {
            var changed = false;
            for (var from = 0; from < graph.Count; from++)
            {
                if (distances[from] == infinity) continue;
                for (var edgeIndex = 0; edgeIndex < graph[from].Count; edgeIndex++)
                {
                    var edge = graph[from][edgeIndex];
                    if (edge.Capacity <= 0 || edge.To == source) continue;
                    var candidate = distances[from] + edge.Cost;
                    if (candidate >= distances[edge.To]) continue;
                    distances[edge.To] = candidate;
                    parents[edge.To] = new FlowParent(from, edgeIndex);
                    changed = true;
                }
            }
            if (!changed) break;
        }
        return parents[sink].From >= 0;
    }

    private static bool UnitMatches(string? required, string? available)
    {
        var need = NormalizeUnit(required);
        if (need.Length == 0) return true;
        var have = NormalizeUnit(available);
        return need == (have.Length == 0 ? "each" : have);
    }

    private static string NormalizeUnit(string? value)
    {
        var unit = InventoryService.NormalizeName(value);
        return unit is "" ? string.Empty
            : unit is "each" or "ea" or "item" or "items" or "unit" or "units" or "pc" or "pcs" or "piece" or "pieces"
                ? "each"
                : unit;
    }

    private static object ToParameters(BomRequirement requirement) => new
    {
        requirement.Id,
        requirement.BomDefinitionId,
        requirement.Name,
        requirement.Quantity,
        requirement.Unit,
        requirement.IsOptional,
        requirement.MatchMode,
        requirement.MatchItemKindId,
        requirement.MatchText,
        RequiredAttributesJson = JsonSerializer.Serialize(requirement.RequiredAttributes, Json),
        requirement.SortOrder
    };

    private static void PrepareDefinition(BomDefinition definition)
    {
        definition.Name = definition.Name.Trim();
        if (definition.Name.Length == 0)
            throw new InvalidOperationException("Enter a name for the recipe or build.");
        if (definition.OutputQuantity <= 0)
            throw new InvalidOperationException("Output quantity must be greater than zero.");
        definition.Description = Clean(definition.Description);
        definition.OutputUnit = Clean(definition.OutputUnit);
    }

    private async Task PrepareRequirementAsync(BomRequirement requirement, CancellationToken ct)
    {
        requirement.Name = requirement.Name.Trim();
        if (requirement.Name.Length == 0) throw new InvalidOperationException("Enter a requirement name.");
        if (requirement.Quantity <= 0)
            throw new InvalidOperationException("Required quantity must be greater than zero.");
        requirement.Unit = Clean(requirement.Unit);
        requirement.MatchText = Clean(requirement.MatchText) ?? requirement.Name;
        requirement.RequiredAttributes = await attributeNames.CanonicalizeAsync(
            requirement.RequiredAttributes, requirement.MatchItemKindId, ct: ct);
        requirement.CandidateItemIds = requirement.CandidateItemIds.Distinct().Order().ToList();
    }

    private static BomRequirement Map(RequirementRow row, List<int> candidates) => new()
    {
        Id = row.Id,
        BomDefinitionId = row.BomDefinitionId,
        Name = row.Name,
        Quantity = row.Quantity,
        Unit = row.Unit,
        IsOptional = row.IsOptional,
        MatchMode = row.MatchMode,
        MatchItemKindId = row.MatchItemKindId,
        MatchText = row.MatchText,
        RequiredAttributes = DeserializeAttributes(row.RequiredAttributesJson),
        CandidateItemIds = candidates,
        SortOrder = row.SortOrder
    };

    private static Dictionary<string, string> DeserializeAttributes(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return new();
        try { return JsonSerializer.Deserialize<Dictionary<string, string>>(value, Json) ?? new(); }
        catch (JsonException) { return new(); }
    }

    private static string? Clean(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private sealed class RequirementRow
    {
        public int Id { get; set; }
        public int BomDefinitionId { get; set; }
        public string Name { get; set; } = string.Empty;
        public decimal Quantity { get; set; }
        public string? Unit { get; set; }
        public bool IsOptional { get; set; }
        public BomMatchMode MatchMode { get; set; }
        public int? MatchItemKindId { get; set; }
        public string? MatchText { get; set; }
        public string RequiredAttributesJson { get; set; } = "{}";
        public int SortOrder { get; set; }
    }

    private sealed class CandidateRow
    {
        public int RequirementId { get; set; }
        public int ItemId { get; set; }
    }

    private sealed class FlowEdge(
        int to, int reverseIndex, decimal capacity, decimal originalCapacity, int cost)
    {
        public int To { get; } = to;
        public int ReverseIndex { get; } = reverseIndex;
        public decimal Capacity { get; set; } = capacity;
        public decimal OriginalCapacity { get; } = originalCapacity;
        public int Cost { get; } = cost;
    }

    private readonly record struct FlowParent(int From, int EdgeIndex);
}
