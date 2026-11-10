using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DSPiConsole.Settings;

/// <summary>
/// Default in-memory implementation of <see cref="IPendingChangeTracker"/>.
/// Backed by a <see cref="Dictionary{TKey,TValue}"/> keyed on
/// <see cref="PendingChange.Key"/>, ordered by stage order (preserved
/// via a sidecar list).
///
/// <para>
/// Apply order: topological sort on <see cref="PendingChange.DependsOn"/>.
/// The current implementation is O(n²) — fine for the dozens of edits
/// a user might stage in one session. If a dependency cycle is detected
/// the involved changes are still attempted, in stage order; cycles
/// can't legally occur with the current set of dependencies (output
/// type → BCK pin → MCK pin is acyclic) but defensive code is cheap.
/// </para>
/// </summary>
internal sealed class PendingChangeTracker : IPendingChangeTracker
{
    // Key → change. Dictionary gives O(1) lookup for restage / discard.
    private readonly Dictionary<string, PendingChange> _byKey = new();
    // Stage order. Used to deterministically iterate Pending and to
    // break ties during topological sort. Kept in sync with _byKey.
    private readonly List<string> _order = new();

    public int Count => _byKey.Count;

    public IReadOnlyCollection<PendingChange> Pending =>
        _order.Select(k => _byKey[k]).ToList();

    public event EventHandler? Changed;

    public void Stage(PendingChange change)
    {
        if (_byKey.TryGetValue(change.Key, out var existing))
        {
            // Re-stage. Preserve the original OldDisplay (the
            // pre-edit value the user started from) but take the new
            // NewDisplay and Apply. Same-key existence implies the
            // entry is already in _order, so no list update needed.
            _byKey[change.Key] = change with { OldDisplay = existing.OldDisplay };
        }
        else
        {
            _byKey[change.Key] = change;
            _order.Add(change.Key);
        }

        // Optimisation: if the user re-edited a field back to its
        // original value, drop the staged change entirely so the
        // InfoBar doesn't show a phantom "change" with old == new.
        var stored = _byKey[change.Key];
        if (stored.OldDisplay == stored.NewDisplay)
        {
            _byKey.Remove(change.Key);
            _order.Remove(change.Key);
        }

        RaiseChanged();
    }

    public bool Discard(string key)
    {
        if (!_byKey.Remove(key)) return false;
        _order.Remove(key);
        RaiseChanged();
        return true;
    }

    public void DiscardAll()
    {
        if (_byKey.Count == 0) return;
        _byKey.Clear();
        _order.Clear();
        RaiseChanged();
    }

    public async Task<ApplyReport> ApplyAllAsync(CancellationToken ct = default)
    {
        if (_byKey.Count == 0) return new ApplyReport(0, 0, Array.Empty<(PendingChange, string)>());

        // Snapshot at the start — any restage during Apply lands in
        // the buffer but doesn't affect THIS pass.
        var batch = _order.Select(k => _byKey[k]).ToList();
        var ordered = TopoSort(batch);

        var failures = new List<(PendingChange, string)>();
        int applied = 0;

        foreach (var change in ordered)
        {
            ct.ThrowIfCancellationRequested();

            byte status;
            try
            {
                status = await change.Apply().ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                failures.Add((change, ex.Message));
                continue;
            }

            if (status == 0)
            {
                // Success — drop from the tracker. Done immediately
                // (not at end of batch) so a re-staged change of the
                // same field during Apply behaves correctly.
                _byKey.Remove(change.Key);
                _order.Remove(change.Key);
                applied++;
            }
            else
            {
                failures.Add((change, status == ViewModels.MainViewModel.LockRefusedStatus
                    ? Usb.Transport.LinkLockNotice.Message
                    : $"firmware status 0x{status:X2}"));
            }
        }

        RaiseChanged();
        return new ApplyReport(applied, failures.Count, failures);
    }

    /// <summary>
    /// Stable topological sort over <see cref="PendingChange.DependsOn"/>.
    /// Changes with unsatisfied dependencies wait; tie-breaks by
    /// original stage order. Cycles (which shouldn't occur in practice)
    /// fall back to stage order for the cycle's members.
    /// </summary>
    private static List<PendingChange> TopoSort(IReadOnlyList<PendingChange> batch)
    {
        var byKey = batch.ToDictionary(c => c.Key);
        var result = new List<PendingChange>(batch.Count);
        var seen = new HashSet<string>();

        // Iteratively pick the next change whose deps are all satisfied
        // (either already in result or not in the batch — external deps
        // are treated as already-met).
        var pending = new List<PendingChange>(batch);
        while (pending.Count > 0)
        {
            int picked = -1;
            for (int i = 0; i < pending.Count; i++)
            {
                var c = pending[i];
                bool depsOk = c.DependsOn == null || c.DependsOn.All(
                    dep => !byKey.ContainsKey(dep) || seen.Contains(dep));
                if (depsOk)
                {
                    picked = i;
                    break;
                }
            }

            if (picked < 0)
            {
                // Cycle (or unsatisfiable dep). Fall back to stage order
                // for the remainder — better to attempt everything than
                // silently drop changes.
                result.AddRange(pending);
                break;
            }

            var chosen = pending[picked];
            pending.RemoveAt(picked);
            result.Add(chosen);
            seen.Add(chosen.Key);
        }

        return result;
    }

    private void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);
}
