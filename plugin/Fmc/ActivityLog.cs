using System;
using System.Collections.Generic;
using System.Threading;

namespace revit_mcp_plugin.Fmc
{
    /// <summary>One MCP call as the Formcraft Browser shows it.</summary>
    public sealed class ActivityEntry
    {
        public DateTime Time { get; set; }
        public string Method { get; set; }
        public bool Ok { get; set; }
        public long Milliseconds { get; set; }
        public string Error { get; set; }
    }

    /// <summary>
    /// Recent MCP calls and connected clients. Written from the socket threads, read by the pane's timer,
    /// so everything goes through one lock; the pane only redraws when Version changes.
    /// </summary>
    public static class ActivityLog
    {
        private const int Capacity = 100;
        private static readonly object _gate = new object();
        private static readonly LinkedList<ActivityEntry> _entries = new LinkedList<ActivityEntry>();
        private static int _clients;
        private static long _version;

        public static int Clients => Volatile.Read(ref _clients);
        public static long Version => Interlocked.Read(ref _version);

        public static void ClientConnected()
        {
            Interlocked.Increment(ref _clients);
            Interlocked.Increment(ref _version);
        }

        public static void ClientDisconnected()
        {
            Interlocked.Decrement(ref _clients);
            Interlocked.Increment(ref _version);
        }

        public static void Add(string method, bool ok, long milliseconds, string error = null)
        {
            lock (_gate)
            {
                _entries.AddFirst(new ActivityEntry
                {
                    Time = DateTime.Now,
                    Method = string.IsNullOrEmpty(method) ? "(invalid request)" : method,
                    Ok = ok,
                    Milliseconds = milliseconds,
                    Error = error,
                });
                while (_entries.Count > Capacity) _entries.RemoveLast();
            }
            Interlocked.Increment(ref _version);
        }

        public static List<ActivityEntry> Snapshot()
        {
            lock (_gate) return new List<ActivityEntry>(_entries);
        }

        public static void Clear()
        {
            lock (_gate) _entries.Clear();
            Interlocked.Increment(ref _version);
        }
    }
}
