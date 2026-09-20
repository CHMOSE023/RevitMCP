using System;
using System.Collections.Generic;
using System.Linq;

namespace RevitMCP.Tooling
{
    /// <summary>
    /// 一次工具调用走到了哪一步。
    ///
    /// <see cref="Unknown"/> 是这套机制存在的**理由**，不是兜底：
    /// 超时、Revit 崩溃、进程被关，都会让一次已经开始执行的调用失去下文。
    /// 此前调用方对此毫无办法，最常见的两种反应都是错的——
    /// 直接重试（模型里多出一整套重复构件，不报错、不产生警告）、
    /// 或当作失败继续（后续构件挂在一批不存在的宿主上）。
    /// </summary>
    public enum OperationState
    {
        Queued,
        Running,

        /// <summary>事务已提交，改动在模型里。</summary>
        Committed,

        /// <summary>事务已回滚，模型原样。工具自己抛错、提交前被取消都算这个。</summary>
        RolledBack,

        /// <summary>没走到事务就失败：参数错、写保护、目标文档不符。</summary>
        Failed,

        /// <summary>排队期间被取消，主线程根本没碰它。</summary>
        Cancelled,

        /// <summary>执行中失联：超时，或进程重启后来查一条它不认识的操作。</summary>
        Unknown
    }

    public sealed class OperationRecord
    {
        public string OperationId { get; set; }
        public string RequestKey { get; set; }

        /// <summary>工具名 + requestKey + 目标文档 + 规范化参数 的指纹。同键异参靠它识别。</summary>
        public string IdentityHash { get; set; }

        public string ToolName { get; set; }
        public string DocumentKey { get; set; }

        public DateTime QueuedAtUtc { get; set; }
        public DateTime? StartedAtUtc { get; set; }
        public DateTime? EndedAtUtc { get; set; }

        public OperationState State { get; set; }
        public string ErrorCode { get; set; }
        public string ErrorMessage { get; set; }

        /// <summary>成功时的 structuredContent 原文，超过上限时为 null。</summary>
        public string ResultJson { get; set; }

        public List<string> AffectedElementIds { get; set; } = new List<string>();

        /// <summary>事务回滚不掉的副作用：落盘、导出写出去的文件。重放时**不重新执行**，但也不假装它没发生。</summary>
        public List<string> SideEffects { get; set; } = new List<string>();

        public bool IsTerminal =>
            State != OperationState.Queued && State != OperationState.Running;
    }

    /// <summary>
    /// 操作日志：记住每次调用走到哪一步、结果是什么，供幂等重放与状态查询。
    ///
    /// **刻意只放在内存里。** 理由不是省事：崩溃恰好发生在 Commit() 内部时，
    /// 磁盘上的记录和内存里的一样说不出它到底落没落盘——
    /// 模型才是唯一的事实来源。一份"看起来权威、其实可能撒谎"的持久化日志比没有更危险。
    /// 因此契约里明写：进程重启后这里是空的，查旧 ID 一律回 Unknown + 查证指引。
    ///
    /// 容量与时效都有上限，满了先淘汰最旧的——一次建模会话几十到上百次写调用，
    /// 200 条足够覆盖"超时之后回头查证"这个真实场景。
    /// </summary>
    public sealed class OperationJournal
    {
        private readonly object _gate = new object();
        private readonly LinkedList<OperationRecord> _records = new LinkedList<OperationRecord>();
        private readonly Dictionary<string, LinkedListNode<OperationRecord>> _byId =
            new Dictionary<string, LinkedListNode<OperationRecord>>(StringComparer.Ordinal);
        private readonly Func<DateTime> _clock;
        private readonly Random _random = new Random();

        public OperationJournal(int capacity = 200, TimeSpan? retention = null, Func<DateTime> clock = null)
        {
            Capacity = Math.Max(1, capacity);
            Retention = retention ?? TimeSpan.FromHours(24);
            _clock = clock ?? (() => DateTime.UtcNow);
        }

        public int Capacity { get; }

        public TimeSpan Retention { get; }

        public int Count
        {
            get { lock (_gate) return _records.Count; }
        }

        /// <summary>登记一次新调用。返回的记录已经进了日志，状态是 Queued。</summary>
        public OperationRecord Begin(string toolName, string requestKey, string identityHash, string documentKey)
        {
            lock (_gate)
            {
                Evict();

                var record = new OperationRecord
                {
                    OperationId = NewId(),
                    RequestKey = requestKey,
                    IdentityHash = identityHash,
                    ToolName = toolName,
                    DocumentKey = documentKey,
                    QueuedAtUtc = _clock(),
                    State = OperationState.Queued
                };

                var node = _records.AddLast(record);
                _byId[record.OperationId] = node;

                return record;
            }
        }

        public void MarkRunning(OperationRecord record)
        {
            if (record == null) return;

            lock (_gate)
            {
                if (record.IsTerminal) return;
                record.State = OperationState.Running;
                record.StartedAtUtc = _clock();
            }
        }

        /// <summary>
        /// 落终态。**已经是终态的不再改写**——
        /// 一次调用先被判定为超时（Unknown）、随后主线程才真的跑完，
        /// 这时把 Unknown 改成 Committed 是对的（我们确实知道了结果），
        /// 但反过来不行：committed 之后再收到取消，状态仍然是 committed。
        /// </summary>
        public void Complete(OperationRecord record, OperationState state,
            string errorCode = null, string errorMessage = null, string resultJson = null)
        {
            if (record == null) return;

            lock (_gate)
            {
                if (record.State == OperationState.Committed) return;

                record.State = state;
                record.EndedAtUtc = _clock();
                record.ErrorCode = errorCode;
                record.ErrorMessage = Trim(errorMessage, MaxErrorChars);
                record.ResultJson = resultJson != null && resultJson.Length <= MaxResultChars
                    ? resultJson
                    : null;
            }
        }

        public OperationRecord FindById(string operationId)
        {
            if (string.IsNullOrWhiteSpace(operationId)) return null;

            lock (_gate)
            {
                Evict();

                LinkedListNode<OperationRecord> node;
                return _byId.TryGetValue(operationId.Trim(), out node) ? node.Value : null;
            }
        }

        /// <summary>按幂等键找最近一条。键相同但内容不同也会被找到——那正是要报冲突的情形。</summary>
        public OperationRecord FindByRequestKey(string requestKey)
        {
            if (string.IsNullOrWhiteSpace(requestKey)) return null;

            var wanted = requestKey.Trim();

            lock (_gate)
            {
                Evict();

                return _records.LastOrDefault(
                    r => string.Equals(r.RequestKey, wanted, StringComparison.Ordinal));
            }
        }

        /// <summary>淘汰：先按时效，再按容量。两者都是"最旧的先走"。</summary>
        private void Evict()
        {
            var cutoff = _clock() - Retention;

            while (_records.Count > 0 && _records.First.Value.QueuedAtUtc < cutoff)
                RemoveFirst();

            while (_records.Count > Capacity)
                RemoveFirst();
        }

        private void RemoveFirst()
        {
            var node = _records.First;
            _records.RemoveFirst();
            _byId.Remove(node.Value.OperationId);
        }

        private string NewId()
        {
            var bytes = new byte[9];
            _random.NextBytes(bytes);

            return "op_" + Convert.ToBase64String(bytes)
                .Replace('+', '-').Replace('/', '_').TrimEnd('=');
        }

        private static string Trim(string text, int max) =>
            text != null && text.Length > max ? text.Substring(0, max) + "…" : text;

        /// <summary>
        /// 回执原文的保留上限。超了只留 affectedElementIds 与计数——
        /// 重放的价值在"别重复建"，不在"一字不差地拿回那份 JSON"。
        /// </summary>
        public const int MaxResultChars = 64 * 1024;

        private const int MaxErrorChars = 2000;
    }
}
