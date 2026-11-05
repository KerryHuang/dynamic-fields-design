using System.Text.Json;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.FieldTypes;
using DynamicModules.Domain.ValueObjects;

namespace DynamicModules.DomainTests.FieldTypes;

internal static class HandlerTestKit
{
    public static JsonElement J(string json) => JsonDocument.Parse(json).RootElement.Clone();

    public static Field F(FieldType type, FieldConfig? config = null) =>
        new() { Id = 1, Key = "f_1", Type = type, Config = config ?? new FieldConfig() };

    public static FieldTypeContext Ctx(
        long[]? active = null, long[]? all = null, Dictionary<long, int>? seq = null,
        string? previous = null, params string[] users) =>
        new(
            (active ?? []).ToHashSet(),
            (all ?? active ?? []).ToHashSet(),
            seq ?? new Dictionary<long, int>(),
            previous is null ? null : J(previous),
            users.ToHashSet());

    public static FieldValueResult Run(FieldType type, string? json, FieldConfig? config = null, FieldTypeContext? ctx = null) =>
        FieldTypeRegistry.CreateDefault().Get(type)
            .Normalize(json is null ? null : J(json), F(type, config), ctx ?? Ctx());
}
