using System;
using System.Collections.Generic;

/// <summary>
/// One completed "Run Agentic Refactor" request, captured for the session history page.
/// </summary>
public record HistoryEntry(
    DateTime Timestamp,
    string PatternDescription,
    string OriginalCode,
    string ModernizedCode,
    string Explanation,
    string Severity,
    bool BuildSucceeded
);

/// <summary>
/// Tracks modernization requests for display on the History page. Registered as Singleton (see
/// Program.cs for why) so it survives the full page reloads that Home/History navigation now
/// triggers - a Scoped/per-circuit lifetime would lose its entries on every navigation.
/// </summary>
public class ModernizationHistoryService
{
    private readonly List<HistoryEntry> _entries = new();
    private readonly object _lock = new();

    public IReadOnlyList<HistoryEntry> Entries
    {
        get { lock (_lock) { return _entries.ToArray(); } }
    }

    public event Action? Changed;

    public void Add(HistoryEntry entry)
    {
        lock (_lock)
        {
            _entries.Insert(0, entry); // newest first
        }
        Changed?.Invoke();
    }

    public void Clear()
    {
        lock (_lock)
        {
            _entries.Clear();
        }
        Changed?.Invoke();
    }
}
