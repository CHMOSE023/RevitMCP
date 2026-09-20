using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Compat;
using RevitMCP.Tooling;

namespace RevitMCP.Addin.Tools
{
    public sealed class ReadScheduleInput
    {
        [McpParam("明细表的视图 ID。用 revit_list_views 查 viewType 为 Schedule 的视图", Required = true)]
        public string ScheduleId { get; set; }

        [McpParam("最多返回多少行，默认 200，上限 2000")]
        public int? MaxRows { get; set; }
    }

    public sealed class ReadScheduleOutput
    {
        [McpParam("明细表名")]
        public string Name { get; set; }

        [McpParam("表体总行数（不受 maxRows 影响）")]
        public int RowCount { get; set; }

        [McpParam("列数")]
        public int ColumnCount { get; set; }

        [McpParam("本次实际返回的行数")]
        public int Returned { get; set; }

        [McpParam("是否因 maxRows 而被截断")]
        public bool Truncated { get; set; }

        [McpParam("表体内容，按行。**第一行通常是列标题**，但这取决于明细表的设置，" +
                  "所以这里原样返回，不替你猜")]
        public List<List<string>> Rows { get; set; } = new List<List<string>>();
    }

    [McpTool("revit_read_schedule",
        Toolsets = new[] { Toolsets.Documentation },
        Title = "读取明细表",
        Description = "把一张明细表读成表格数据。明细表是 Revit 里现成的统计结果——" +
                      "门窗表、房间面积表、材料用量表都在这里，比自己遍历构件再算一遍可靠得多。" +
                      "行数可能很多，注意 maxRows 与 truncated。",
        ReadOnly = true,
        TimeoutSeconds = 60)]
    public sealed class ReadScheduleTool : RevitTool<ReadScheduleInput, ReadScheduleOutput>
    {
        private const int DefaultMaxRows = 200;
        private const int MaxRowLimit = 2000;

        /// <summary>单行列数的上限。列太多多半是明细表本身有问题，全读出来只会淹没模型。</summary>
        private const int MaxColumns = 60;

        public override ReadScheduleOutput Execute(
            ReadScheduleInput input, ToolExecutionContext<UIApplication> context)
        {
            var document = RequireDocument(context);

            var element = RequireElement(document, input.ScheduleId);
            var schedule = element as ViewSchedule;

            if (schedule == null)
                throw new ToolFailureException(McpDomainError.InvalidParameter,
                    "ID " + input.ScheduleId + " 不是明细表，而是「" +
                    (element.Category?.Name ?? element.GetType().Name) +
                    "」。用 revit_list_views 查 viewType 为 Schedule 的视图。");

            var maxRows = Math.Min(Math.Max(input.MaxRows ?? DefaultMaxRows, 1), MaxRowLimit);

            TableSectionData body;
            try
            {
                body = schedule.GetTableData().GetSectionData(SectionType.Body);
            }
            catch (Exception ex)
            {
                throw new ToolFailureException(McpDomainError.TransactionFailed,
                    "读取明细表数据失败：" + ex.Message);
            }

            if (body == null)
                throw new ToolFailureException(McpDomainError.TransactionFailed,
                    "明细表「" + SafeName(schedule) + "」没有表体数据。");

            var rowCount = body.NumberOfRows;
            var columnCount = Math.Min(body.NumberOfColumns, MaxColumns);

            var output = new ReadScheduleOutput
            {
                Name = SafeName(schedule),
                RowCount = rowCount,
                ColumnCount = body.NumberOfColumns,
                Truncated = rowCount > maxRows
            };

            if (body.NumberOfColumns > MaxColumns)
                context.Warnings.Add(
                    "明细表有 " + body.NumberOfColumns + " 列，只返回了前 " + MaxColumns + " 列。");

            var take = Math.Min(rowCount, maxRows);

            for (var row = 0; row < take; row++)
            {
                context.CancellationToken.ThrowIfCancellationRequested();
                ProgressTicker.Tick(context.Progress, row + 1, take, "已读取");

                var cells = new List<string>(columnCount);
                for (var column = 0; column < columnCount; column++)
                    cells.Add(CellText(schedule, row, column));

                output.Rows.Add(cells);
            }

            output.Returned = output.Rows.Count;

            if (output.RowCount == 0)
                context.Warnings.Add(
                    "明细表「" + output.Name + "」是空的。它的过滤条件可能没有匹配到任何构件。");

            return output;
        }

        /// <summary>
        /// 读一个单元格。个别单元格读不出来时返回 null 而不是让整张表失败——
        /// 一张几百行的表因为一个格子全军覆没，代价太大。
        /// </summary>
        private static string CellText(ViewSchedule schedule, int row, int column)
        {
            try { return schedule.GetCellText(SectionType.Body, row, column); }
            catch { return null; }
        }

        private static string SafeName(ViewSchedule schedule)
        {
            try { return schedule.Name; }
            catch { return null; }
        }
    }
}
