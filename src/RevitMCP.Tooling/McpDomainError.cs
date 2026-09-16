namespace RevitMCP.Tooling
{
    /// <summary>
    /// 领域错误码。放进工具结果的文本里（isError: true），而不是 JSON-RPC 错误：
    /// 模型看得见才能自我纠正，包成 JSON-RPC 错误客户端会当成传输故障。
    /// 前缀固定，便于模型稳定识别。
    /// </summary>
    public static class McpDomainError
    {
        /// <summary>主线程被占用，工作尚未开始——可以确定模型未被触碰。</summary>
        public const string RevitBusy = "REVIT_BUSY";

        /// <summary>已开始执行但未在超时内完成——模型可能已被部分修改。</summary>
        public const string Timeout = "TIMEOUT";

        public const string NoActiveDocument = "NO_ACTIVE_DOC";
        public const string WriteDisabled = "WRITE_DISABLED";
        public const string ElementNotFound = "ELEMENT_NOT_FOUND";
        public const string InvalidParameter = "INVALID_PARAMETER";
        public const string TransactionFailed = "TRANSACTION_FAILED";

        /// <summary>
        /// 影响面超过阈值，需要模型显式带上 confirm: true 再来一次。
        /// 单独成码而不是并入 INVALID_PARAMETER：模型看到它就知道
        /// "参数没写错，只是这一步需要确认"，不会去瞎改别的参数。
        /// </summary>
        public const string ConfirmationRequired = "CONFIRMATION_REQUIRED";
        public const string ServerStopped = "SERVER_STOPPED";

        public static string Format(string code, string message) => code + ": " + message;
    }
}
