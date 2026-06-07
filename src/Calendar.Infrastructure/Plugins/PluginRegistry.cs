using System.Collections.Concurrent;
using System.Collections.Immutable;
using Calendar.Application.Plugins;
using Calendar.Plugin.Abstractions;

namespace Calendar.Infrastructure.Plugins;

/// <summary>
/// The capability index (PLUGIN-HOST.md §2.2): a <see cref="Capability"/> → registrations map plus an
/// id → registration map. Mutations swap immutable lists atomically so in-flight
/// <see cref="ForCapability"/> enumerations are never torn. Only <see cref="PluginState.Running"/>
/// registrations are returned to capability callers.
/// </summary>
public sealed class PluginRegistry : IPluginRegistry
{
    private readonly ConcurrentDictionary<Capability, ImmutableList<PluginRegistration>> _byCapability = new();
    private readonly ConcurrentDictionary<string, PluginRegistration> _byId = new(StringComparer.Ordinal);
    private readonly object _writeLock = new();

    public IReadOnlyList<PluginRegistration> ForCapability(Capability capability) =>
        _byCapability.TryGetValue(capability, out var list)
            ? list.Where(r => r.State == PluginState.Running).ToArray()
            : Array.Empty<PluginRegistration>();

    public bool TryGet(string pluginId, out PluginRegistration registration) =>
        _byId.TryGetValue(pluginId, out registration!);

    public IReadOnlyList<PluginRegistration> All() => _byId.Values.ToArray();

    public IReadOnlyDictionary<Capability, IReadOnlyList<string>> Snapshot()
    {
        var result = new Dictionary<Capability, IReadOnlyList<string>>();
        foreach (var (capability, list) in _byCapability)
        {
            var ids = list.Where(r => r.State == PluginState.Running).Select(r => r.Id).ToArray();
            if (ids.Length > 0)
                result[capability] = ids;
        }
        return result;
    }

    public void Register(PluginRegistration registration)
    {
        // Serialize writes so the id-map and capability-lists stay consistent across a replace.
        lock (_writeLock)
        {
            if (_byId.TryGetValue(registration.Id, out var existing))
                RemoveFromCapabilities(existing);

            _byId[registration.Id] = registration;
            foreach (var capability in registration.Capabilities)
            {
                _byCapability.AddOrUpdate(
                    capability,
                    _ => ImmutableList.Create(registration),
                    (_, list) => list.Add(registration));
            }
        }
    }

    public void Remove(string pluginId)
    {
        lock (_writeLock)
        {
            if (!_byId.TryRemove(pluginId, out var existing))
                return;
            RemoveFromCapabilities(existing);
        }
    }

    private void RemoveFromCapabilities(PluginRegistration registration)
    {
        foreach (var capability in registration.Capabilities)
        {
            if (_byCapability.TryGetValue(capability, out var list))
            {
                var updated = list.RemoveAll(r => r.Id == registration.Id);
                if (updated.IsEmpty)
                    _byCapability.TryRemove(capability, out _);
                else
                    _byCapability[capability] = updated;
            }
        }
    }
}
