namespace RevitMCP.Addin.Compat
{
    /// <summary>
    /// 编译期锁定的目标 Revit 版本。符号由 Directory.Build.props 依配置名注入。
    /// 新增版本时，这里和 Directory.Build.props 的版本矩阵必须同步修改。
    /// </summary>
    public static class RevitVersionInfo
    {
#if REVIT2019
        public const int Year = 2019;
#elif REVIT2020
        public const int Year = 2020;
#elif REVIT2021
        public const int Year = 2021;
#elif REVIT2022
        public const int Year = 2022;
#elif REVIT2023
        public const int Year = 2023;
#elif REVIT2024
        public const int Year = 2024;
#else
#error 未定义 Revit 版本符号。请使用 "Debug R24" 一类的配置名构建，而不是裸的 Debug/Release。
#endif

        /// <summary>本次构建所针对的 CLR 版本，仅用于日志与排错。</summary>
        public const string TargetFramework = Year >= 2021 ? "net48" : "net47";
    }
}
