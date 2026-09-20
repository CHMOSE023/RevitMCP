using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;

namespace RevitMCP.Addin.Diagnostics
{
    /// <summary>一次查询得到的改动集合。</summary>
    public sealed class ChangeSet
    {
        public string Token;
        public List<long> Added = new List<long>();
        public List<long> Modified = new List<long>();
        public List<long> Deleted = new List<long>();

        /// <summary>调用方给的 token 已经太旧、被环形缓冲挤掉了。此时的结果不完整。</summary>
        public bool Overflowed;

        /// <summary>调用方没给 token，这次只是建立基线。</summary>
        public bool IsBaseline;
    }

    /// <summary>
    /// 跟踪模型改动。
    ///
    /// 订阅 <c>Application.DocumentChanged</c>，把每一次事务的增删改按文档记进一条流水，
    /// 每条带一个单调递增的序号。调用方拿着上次的序号回来问"从那以后改了什么"。
    ///
    /// **刻意不做全模型快照。** 那需要把整个模型序列化落盘，代价随模型大小线性增长，
    /// 而绝大多数场景要的只是"我上次看过之后有什么变了"——
    /// 这个问题 Revit 的事件已经直接回答了，不需要自己存一份模型。
    /// 真要基线数据，用 revit_query_elements 拿，那是它的本职。
    ///
    /// 容量有限（每个文档最多记 <see cref="MaxEntries"/> 条），满了就丢最旧的，
    /// 并让基于被丢掉序号的查询明确返回"结果不完整"——
    /// 悄悄少给几条改动，会让依赖它做增量同步的调用方一直错下去。
    /// </summary>
    public sealed class ChangeTracker : IDisposable
    {
        /// <summary>每个文档保留多少条改动记录。约相当于几十次大批量操作的量。</summary>
        private const int MaxEntries = 20000;

        private readonly object _lock = new object();
        private readonly Dictionary<string, DocumentLog> _logs =
            new Dictionary<string, DocumentLog>(StringComparer.OrdinalIgnoreCase);

        private Autodesk.Revit.ApplicationServices.ControlledApplication _application;

        public void Attach(Autodesk.Revit.ApplicationServices.ControlledApplication application)
        {
            if (application == null) return;

            _application = application;
            _application.DocumentChanged += OnDocumentChanged;
        }

        public void Dispose()
        {
            if (_application == null) return;

            try { _application.DocumentChanged -= OnDocumentChanged; }
            catch { /* Revit 正在退出时解绑可能失败，无所谓 */ }

            _application = null;
        }

        private void OnDocumentChanged(object sender, DocumentChangedEventArgs args)
        {
            // 事件处理里绝不能抛：异常会冒到 Revit 的事务提交路径上，
            // 表现成用户操作莫名失败，而且完全看不出和本插件有关
            try
            {
                var key = DocumentRef.KeyOf(args.GetDocument());
                if (key == null) return;

                lock (_lock)
                {
                    DocumentLog log;
                    if (!_logs.TryGetValue(key, out log))
                    {
                        log = new DocumentLog();
                        _logs[key] = log;
                    }

                    log.Record(args);
                }
            }
            catch { /* 记录改动失败不该影响用户的任何操作 */ }
        }

        /// <summary>
        /// 查询改动。<paramref name="sinceToken"/> 为空表示只建立基线。
        /// </summary>
        public ChangeSet Query(Document document, string sinceToken)
        {
            var key = DocumentRef.KeyOf(document);

            lock (_lock)
            {
                DocumentLog log;
                if (!_logs.TryGetValue(key ?? string.Empty, out log))
                {
                    // 这个文档还没有任何改动被记下来。建一条空流水，
                    // 让调用方拿到一个可用的 token 而不是一个 null
                    log = new DocumentLog();
                    if (key != null) _logs[key] = log;
                }

                return log.Query(key, sinceToken);
            }
        }

        // ==================== 每文档的改动流水 ====================

        private sealed class DocumentLog
        {
            private readonly Queue<Entry> _entries = new Queue<Entry>();
            private long _sequence;

            /// <summary>已经被挤出缓冲的最大序号。基于它或更早的 token 都不完整。</summary>
            private long _discardedThrough;

            public void Record(DocumentChangedEventArgs args)
            {
                Append(args.GetAddedElementIds(), ChangeKind.Added);
                Append(args.GetModifiedElementIds(), ChangeKind.Modified);
                Append(args.GetDeletedElementIds(), ChangeKind.Deleted);
            }

            private void Append(ICollection<ElementId> ids, ChangeKind kind)
            {
                if (ids == null) return;

                foreach (var id in ids)
                {
                    if (id == null) continue;

                    _entries.Enqueue(new Entry
                    {
                        Sequence = ++_sequence,
                        Id = Compat.ElementIdCompat.GetValue(id),
                        Kind = kind
                    });

                    while (_entries.Count > MaxEntries)
                    {
                        var dropped = _entries.Dequeue();
                        _discardedThrough = dropped.Sequence;
                    }
                }
            }

            public ChangeSet Query(string documentKey, string sinceToken)
            {
                var result = new ChangeSet { Token = Token.Format(documentKey, _sequence) };

                if (string.IsNullOrWhiteSpace(sinceToken))
                {
                    result.IsBaseline = true;
                    return result;
                }

                long since;
                if (!Token.TryParse(sinceToken, documentKey, out since))
                    throw new ToolTokenException(
                        "token \"" + sinceToken + "\" 不是这个文档的有效 token。" +
                        "它可能来自另一个文档，或者格式不对。" +
                        "省略 since 重新取一个基线 token。");

                if (since > _sequence)
                    throw new ToolTokenException(
                        "token 指向的是一个还没发生的改动（可能是文档被关闭后重新打开过）。" +
                        "省略 since 重新取一个基线 token。");

                result.Overflowed = since < _discardedThrough;

                // 同一个构件在这段时间里可能被改了很多次，也可能先建后删。
                // 按"最终状态"归类：建了又删的不该出现在 added 里，
                // 否则调用方会去查一个已经不存在的 ID
                var states = new Dictionary<long, ChangeKind>();

                foreach (var entry in _entries)
                {
                    if (entry.Sequence <= since) continue;

                    ChangeKind existing;
                    if (!states.TryGetValue(entry.Id, out existing))
                    {
                        states[entry.Id] = entry.Kind;
                        continue;
                    }

                    states[entry.Id] = Merge(existing, entry.Kind);
                }

                foreach (var pair in states)
                {
                    switch (pair.Value)
                    {
                        case ChangeKind.Added: result.Added.Add(pair.Key); break;
                        case ChangeKind.Modified: result.Modified.Add(pair.Key); break;
                        case ChangeKind.Deleted: result.Deleted.Add(pair.Key); break;
                    }
                }

                result.Added.Sort();
                result.Modified.Sort();
                result.Deleted.Sort();

                return result;
            }

            /// <summary>
            /// 把同一个构件的多次改动合并成一个最终状态。
            /// 规则只有三条，但每一条都对应一个真实场景：
            /// 建了又改 → 还是新建；建了又删 → 当它没发生过（记为 None）；改了又删 → 删除。
            /// </summary>
            private static ChangeKind Merge(ChangeKind existing, ChangeKind latest)
            {
                if (latest == ChangeKind.Deleted)
                    return existing == ChangeKind.Added ? ChangeKind.None : ChangeKind.Deleted;

                if (existing == ChangeKind.Added) return ChangeKind.Added;
                if (existing == ChangeKind.None) return latest;

                return existing == ChangeKind.Deleted ? ChangeKind.Deleted : latest;
            }
        }

        private enum ChangeKind { None, Added, Modified, Deleted }

        private struct Entry
        {
            public long Sequence;
            public long Id;
            public ChangeKind Kind;
        }

        /// <summary>
        /// token 的格式：文档 key 的短哈希 + 序号。
        /// 带上文档标识，是为了让"拿 A 文档的 token 去查 B 文档"当场失败，
        /// 而不是返回一份看起来合理、实际毫不相干的改动清单。
        /// </summary>
        private static class Token
        {
            public static string Format(string documentKey, long sequence)
            {
                return Hash(documentKey) + "-" + sequence.ToString(CultureInfo.InvariantCulture);
            }

            public static bool TryParse(string token, string documentKey, out long sequence)
            {
                sequence = 0;

                var separator = token.LastIndexOf('-');
                if (separator <= 0) return false;

                if (!string.Equals(token.Substring(0, separator), Hash(documentKey), StringComparison.Ordinal))
                    return false;

                return long.TryParse(token.Substring(separator + 1), NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out sequence);
            }

            private static string Hash(string value)
            {
                // 只用来区分文档，不承担任何安全职责，所以一个便宜的 FNV 就够
                unchecked
                {
                    var hash = 2166136261;
                    foreach (var c in value ?? string.Empty)
                    {
                        hash ^= c;
                        hash *= 16777619;
                    }

                    return hash.ToString("x8", CultureInfo.InvariantCulture);
                }
            }
        }
    }

    /// <summary>token 不可用。工具层会把它翻译成 INVALID_PARAMETER。</summary>
    public sealed class ToolTokenException : Exception
    {
        public ToolTokenException(string message) : base(message) { }
    }
}
