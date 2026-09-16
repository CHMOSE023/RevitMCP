using System;
using System.Collections.Generic;

namespace RevitMCP.Tooling
{
    /// <summary>
    /// 一次写工具调用的作用域信息。写作用域的实现据此命名事务、回填警告。
    /// </summary>
    public sealed class WriteScopeInfo
    {
        public WriteScopeInfo(string toolName, IList<string> warnings)
        {
            ToolName = toolName ?? string.Empty;
            Warnings = warnings ?? new List<string>();
        }

        /// <summary>工具名。用于事务命名，让用户在撤销栈里认得出这一步是谁做的。</summary>
        public string ToolName { get; }

        /// <summary>
        /// 被抑制的警告。实现必须把吞掉的每一条都写进来——
        /// 静默吞警告比弹模态框更危险：模型和用户都不会知道模型被动过什么。
        /// </summary>
        public IList<string> Warnings { get; }
    }

    /// <summary>
    /// 包裹写工具执行的作用域：开事务、装失败预处理器、拦对话框、异常回滚。
    ///
    /// 定义在这一层而不是 Addin，是为了让管线的"只读工具不开事务、写工具开事务"
    /// 这条分支能脱离 Revit 测试——它一旦错了，代价是用户模型被改坏。
    /// 真实实现见 <c>RevitMCP.Addin.Execution.RevitWriteScope</c>。
    /// </summary>
    public interface IWriteScope<TContext>
    {
        /// <summary>
        /// 在写作用域内执行 <paramref name="work"/>。
        /// 已在主线程且具备 API context。work 抛异常时实现必须回滚并让异常继续向上传播。
        /// </summary>
        TResult Run<TResult>(TContext host, WriteScopeInfo info, Func<TResult> work);
    }

    /// <summary>
    /// 不做任何事的写作用域：直接执行。
    /// 管线未注入写作用域时的兜底——用于只有只读工具的场景与单元测试。
    /// </summary>
    public sealed class PassthroughWriteScope<TContext> : IWriteScope<TContext>
    {
        public TResult Run<TResult>(TContext host, WriteScopeInfo info, Func<TResult> work) => work();
    }
}
