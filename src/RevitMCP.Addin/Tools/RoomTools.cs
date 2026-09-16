using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Compat;
using RevitMCP.Tooling;

namespace RevitMCP.Addin.Tools
{
    // ==================== 房间清单 ====================

    public sealed class ListRoomsInput
    {
        [McpParam("按房间名或编号过滤（不区分大小写的子串匹配）")]
        public string NameContains { get; set; }

        [McpParam("只看该标高上的房间，来自 revit_list_levels")]
        public string LevelId { get; set; }

        [McpParam("true 时把未放置的房间也列出来，默认 false")]
        public bool? IncludeUnplaced { get; set; }

        [McpParam("true 时返回每个房间的边界点。数据量大，只在需要几何时开，默认 false")]
        public bool? IncludeBoundary { get; set; }

        [McpParam("最多返回多少个房间，默认 100，上限 500")]
        public int? Limit { get; set; }
    }

    public sealed class RoomBoundaryLoop
    {
        [McpParam("这一环的顶点，按顺序首尾相接。z 省略——房间的竖向位置由标高决定")]
        public List<Point3D> Points { get; set; } = new List<Point3D>();

        [McpParam("顶点是否因数量过多而被截断")]
        public bool Truncated { get; set; }
    }

    public sealed class RoomInfo
    {
        [McpParam("房间 ID")]
        public string Id { get; set; }

        [McpParam("房间名称")]
        public string Name { get; set; }

        [McpParam("房间编号")]
        public string Number { get; set; }

        [McpParam("面积，平方米。为 0 表示房间没有被围合起来")]
        public double AreaSqm { get; set; }

        [McpParam("周长，毫米")]
        public double PerimeterMm { get; set; }

        [McpParam("体积，立方米。项目未开启体积计算时为 null")]
        public double? VolumeCbm { get; set; }

        [McpParam("所在标高 ID")]
        public string LevelId { get; set; }

        [McpParam("所在标高名")]
        public string Level { get; set; }

        [McpParam("房间点的位置，毫米。未放置的房间为 null")]
        public Point3D Location { get; set; }

        [McpParam("房间上边界的高度，毫米")]
        public double UnboundedHeightMm { get; set; }

        [McpParam("是否已放置到模型中。未放置的房间只存在于明细表里")]
        public bool IsPlaced { get; set; }

        [McpParam("是否被围合。false 表示这个房间四周没有闭合的边界，面积算不出来")]
        public bool IsBounded { get; set; }

        [McpParam("边界环，第一个是外环，其余是洞。未请求 includeBoundary 时为 null")]
        public List<RoomBoundaryLoop> Boundary { get; set; }
    }

    public sealed class ListRoomsOutput
    {
        [McpParam("匹配到的房间总数（不受 limit 影响）")]
        public int Total { get; set; }

        [McpParam("本次实际返回的条数")]
        public int Returned { get; set; }

        [McpParam("是否因 limit 而被截断")]
        public bool Truncated { get; set; }

        [McpParam("其中没有被围合的房间数（面积为 0）")]
        public int Unbounded { get; set; }

        [McpParam("匹配房间的面积合计，平方米")]
        public double TotalAreaSqm { get; set; }

        [McpParam("房间列表，按标高与编号排列")]
        public List<RoomInfo> Rooms { get; set; } = new List<RoomInfo>();
    }

    [McpTool("revit_list_rooms",
        Title = "列出房间",
        Description = "列出模型中的房间及其名称、编号、面积（平方米）、周长、标高。" +
                      "面积为 0 意味着房间四周没有闭合边界——这是房间最常见的问题，" +
                      "输出里的 unbounded 会直接告诉你有几个。" +
                      "需要房间形状时加 includeBoundary，但数据量大，不需要就别开。",
        ReadOnly = true,
        TimeoutSeconds = 60)]
    public sealed class ListRoomsTool : RevitTool<ListRoomsInput, ListRoomsOutput>
    {
        private const int DefaultLimit = 100;
        private const int MaxLimit = 500;

        public override ListRoomsOutput Execute(ListRoomsInput input, ToolExecutionContext<UIApplication> context)
        {
            var document = RequireDocument(context);
            var limit = Math.Min(Math.Max(input.Limit ?? DefaultLimit, 1), MaxLimit);

            ElementId levelFilter = null;
            if (!string.IsNullOrWhiteSpace(input.LevelId))
            {
                var element = RequireElement(document, input.LevelId);
                if (!(element is Level))
                    throw new ToolFailureException(McpDomainError.InvalidParameter,
                        "levelId " + input.LevelId + " 不是标高。用 revit_list_levels 取标高 ID。");
                levelFilter = element.Id;
            }

            var rooms = new List<RoomInfo>();

            foreach (var element in new FilteredElementCollector(document)
                         .OfCategory(BuiltInCategory.OST_Rooms)
                         .WhereElementIsNotElementType())
            {
                context.CancellationToken.ThrowIfCancellationRequested();

                var room = element as Room;
                if (room == null) continue;

                var placed = IsPlaced(room);
                if (!placed && input.IncludeUnplaced != true) continue;

                var levelId = SafeLevelId(room);
                if (levelFilter != null && (levelId == null || levelId != levelFilter)) continue;

                var name = ReadString(room, BuiltInParameter.ROOM_NAME);
                var number = ReadString(room, BuiltInParameter.ROOM_NUMBER);
                if (!Matches(input.NameContains, name, number)) continue;

                rooms.Add(Describe(document, room, name, number, levelId, placed,
                    input.IncludeBoundary == true));
            }

            rooms = rooms.OrderBy(r => r.Level, StringComparer.CurrentCulture)
                         .ThenBy(r => r.Number, StringComparer.CurrentCulture)
                         .ToList();

            var output = new ListRoomsOutput
            {
                Total = rooms.Count,
                Truncated = rooms.Count > limit,
                Unbounded = rooms.Count(r => !r.IsBounded),
                TotalAreaSqm = Math.Round(rooms.Sum(r => r.AreaSqm), 3),
                Rooms = rooms.Take(limit).ToList()
            };

            output.Returned = output.Rooms.Count;

            if (output.Unbounded > 0)
                context.Warnings.Add(
                    "有 " + output.Unbounded + " 个房间没有被围合（面积为 0）。" +
                    "常见原因是墙没有接上、或者缺少房间分隔线——" +
                    "可以用 revit_get_warnings 看看 Revit 是否也记了相关警告。");

            return output;
        }

        private static RoomInfo Describe(
            Document document, Room room, string name, string number,
            ElementId levelId, bool placed, bool includeBoundary)
        {
            var areaSqm = Units.SquareMeters(SafeDouble(() => room.Area));

            var info = new RoomInfo
            {
                Id = room.Id.ToProtocolString(),
                Name = name,
                Number = number,
                AreaSqm = areaSqm,
                PerimeterMm = Units.Round(Units.FromFeet(SafeDouble(() => room.Perimeter))),
                VolumeCbm = ReadVolume(room),
                LevelId = levelId?.ToProtocolString(),
                Level = SafeName(levelId == null ? null : document.GetElement(levelId)),
                Location = ReadLocation(room),
                UnboundedHeightMm = Units.Round(Units.FromFeet(SafeDouble(() => room.UnboundedHeight))),
                IsPlaced = placed,

                // 放置了不等于围上了：Revit 允许一个房间点孤零零地待在没有墙的地方，
                // 它照样是个房间对象，只是面积算不出来。这两件事必须分开报
                IsBounded = areaSqm > 0
            };

            if (includeBoundary) info.Boundary = ReadBoundary(room);

            return info;
        }

        /// <summary>
        /// 读房间边界。第一个环是外轮廓，后面的是洞（柱子、内天井之类）。
        /// 未围合的房间没有边界，返回空列表而不是 null——"问了但没有"和"没问"是两回事。
        /// </summary>
        private static List<RoomBoundaryLoop> ReadBoundary(Room room)
        {
            const int maxPointsPerLoop = 200;
            var loops = new List<RoomBoundaryLoop>();

            IList<IList<BoundarySegment>> segments;
            try
            {
                segments = room.GetBoundarySegments(new SpatialElementBoundaryOptions());
            }
            catch
            {
                return loops;
            }

            if (segments == null) return loops;

            foreach (var loop in segments)
            {
                var points = new RoomBoundaryLoop();

                foreach (var segment in loop)
                {
                    if (points.Points.Count >= maxPointsPerLoop) { points.Truncated = true; break; }

                    Curve curve;
                    try { curve = segment.GetCurve(); }
                    catch { continue; }
                    if (curve == null) continue;

                    // 只取每段的起点：相邻两段首尾相接，取全了等于每个点记两遍
                    var start = curve.GetEndPoint(0);
                    points.Points.Add(new Point3D
                    {
                        X = Units.Round(Units.FromFeet(start.X)),
                        Y = Units.Round(Units.FromFeet(start.Y))
                    });
                }

                if (points.Points.Count > 0) loops.Add(points);
            }

            return loops;
        }

        private static double? ReadVolume(Room room)
        {
            // 体积只在项目开启了"面积和体积计算"时才有值，否则恒为 0。
            // 把 0 原样报出去会让模型以为房间是空的
            var volume = SafeDouble(() => room.Volume);
            return volume > 0 ? Units.CubicMeters(volume) : (double?)null;
        }

        private static Point3D ReadLocation(Room room)
        {
            try
            {
                var point = (room.Location as LocationPoint)?.Point;
                if (point == null) return null;

                return new Point3D
                {
                    X = Units.Round(Units.FromFeet(point.X)),
                    Y = Units.Round(Units.FromFeet(point.Y)),
                    Z = Units.Round(Units.FromFeet(point.Z))
                };
            }
            catch
            {
                return null;
            }
        }

        internal static bool IsPlaced(Room room)
        {
            try { return room.Location != null; }
            catch { return false; }
        }

        internal static ElementId SafeLevelId(Room room)
        {
            try { return room.LevelId; }
            catch { return null; }
        }

        internal static string ReadString(Element element, BuiltInParameter id)
        {
            try
            {
                var parameter = element.get_Parameter(id);
                return parameter?.AsString();
            }
            catch
            {
                return null;
            }
        }

        private static double SafeDouble(Func<double> read)
        {
            try { return read(); }
            catch { return 0; }
        }

        private static string SafeName(Element element)
        {
            try { return element?.Name; }
            catch { return null; }
        }

        private static bool Matches(string needle, string name, string number)
        {
            if (string.IsNullOrWhiteSpace(needle)) return true;

            return (name != null && name.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0)
                   || (number != null && number.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0);
        }
    }

    // ==================== 创建房间 ====================

    public sealed class RoomSpec
    {
        [McpParam("房间点的位置，毫米。这个点必须落在被墙围合的区域里", Required = true)]
        public Point3D LocationPoint { get; set; }

        [McpParam("标高 ID，来自 revit_list_levels。省略则用活动视图所在标高")]
        public string LevelId { get; set; }

        [McpParam("房间名称。省略则用 Revit 的默认名")]
        public string Name { get; set; }

        [McpParam("房间编号。省略则用 Revit 自动编号")]
        public string Number { get; set; }
    }

    public sealed class CreateRoomsInput
    {
        [McpParam("要创建的房间，一次调用可建多个", Required = true)]
        public List<RoomSpec> Elements { get; set; }

        [McpParam("数量超过单次上限时，带上 true 表示确认后再执行")]
        public bool? Confirm { get; set; }
    }

    public sealed class CreatedRoom
    {
        [McpParam("对应 elements 数组中的下标，从 0 起")]
        public int Index { get; set; }

        [McpParam("新建房间的 ID")]
        public string Id { get; set; }

        [McpParam("房间名称")]
        public string Name { get; set; }

        [McpParam("房间编号")]
        public string Number { get; set; }

        [McpParam("面积，平方米。为 0 表示这个点周围没有闭合的墙")]
        public double AreaSqm { get; set; }

        [McpParam("是否被围合")]
        public bool IsBounded { get; set; }

        [McpParam("所在标高名")]
        public string Level { get; set; }
    }

    public sealed class CreateRoomsOutput : IReportsAffectedElements
    {
        [McpParam("成功创建的房间数")]
        public int Created { get; set; }

        [McpParam("其中没有被围合的房间数")]
        public int Unbounded { get; set; }

        [McpParam("新建的房间，顺序与入参一致")]
        public List<CreatedRoom> Elements { get; set; } = new List<CreatedRoom>();

        int IReportsAffectedElements.AffectedElements => Created;
    }

    [McpTool("revit_create_rooms",
        Title = "创建房间",
        Description = "在指定点上批量创建房间。坐标用毫米，点必须落在被墙围合的区域里。" +
                      "整批要么全部建成、要么一个都不建，且在撤销栈里只占一步。" +
                      "回执里的 areaSqm 是关键：**它为 0 说明那个点周围的墙没有围成闭合区域**，" +
                      "房间虽然建出来了却没有面积。建完请核对这个字段，别只看有没有报错。",
        TimeoutSeconds = 120)]
    public sealed class CreateRoomsTool : RevitTool<CreateRoomsInput, CreateRoomsOutput>
    {
        public override CreateRoomsOutput Execute(
            CreateRoomsInput input, ToolExecutionContext<UIApplication> context)
        {
            var document = RequireDocument(context);
            RequireBatch(input.Elements, input.Confirm, context, "创建");

            var output = new CreateRoomsOutput();
            var total = input.Elements.Count;

            for (var index = 0; index < total; index++)
            {
                context.CancellationToken.ThrowIfCancellationRequested();

                var spec = input.Elements[index];
                if (spec == null)
                    throw CreateSupport.Failure(index, McpDomainError.InvalidParameter, "该项为 null。");

                output.Elements.Add(CreateOne(document, context, spec, index));
                ProgressTicker.Tick(context.Progress, index + 1, total, "已创建");
            }

            output.Created = output.Elements.Count;
            output.Unbounded = output.Elements.Count(r => !r.IsBounded);

            if (output.Unbounded > 0)
                context.Warnings.Add(
                    output.Unbounded + " 个房间没有围合起来，面积为 0。" +
                    "这些点周围的墙没有形成闭合区域——检查墙有没有接上，" +
                    "或者用房间分隔线补上缺口。房间对象本身已经建好了，补上边界后面积会自动算出来。");

            return output;
        }

        private static CreatedRoom CreateOne(
            Document document, ToolExecutionContext<UIApplication> context,
            RoomSpec spec, int index)
        {
            if (spec.LocationPoint == null)
                throw CreateSupport.Failure(index, McpDomainError.InvalidParameter, "缺少 locationPoint。");

            var level = CreateSupport.ResolveLevel(document, context, spec.LevelId, index);
            var uv = new UV(Units.ToFeet(spec.LocationPoint.X), Units.ToFeet(spec.LocationPoint.Y));

            Room room;
            try
            {
                room = document.Create.NewRoom(level, uv);
            }
            catch (Exception ex)
            {
                throw CreateSupport.Failure(index, McpDomainError.TransactionFailed,
                    "Revit 拒绝在 " + spec.LocationPoint + " 创建房间：" + ex.Message);
            }

            if (room == null)
                throw CreateSupport.Failure(index, McpDomainError.TransactionFailed,
                    "Revit 未能在 " + spec.LocationPoint + " 创建房间，但也没有报错。" +
                    "该标高可能不允许放置房间。");

            if (!string.IsNullOrEmpty(spec.Name)) TrySet(room, BuiltInParameter.ROOM_NAME, spec.Name, context, index);
            if (!string.IsNullOrEmpty(spec.Number)) TrySet(room, BuiltInParameter.ROOM_NUMBER, spec.Number, context, index);

            // 面积要在设完参数之后读：Revit 需要一次重算才知道这个点有没有被围上
            document.Regenerate();

            var areaSqm = Units.SquareMeters(SafeArea(room));

            return new CreatedRoom
            {
                Index = index,
                Id = room.Id.ToProtocolString(),
                Name = ListRoomsTool.ReadString(room, BuiltInParameter.ROOM_NAME),
                Number = ListRoomsTool.ReadString(room, BuiltInParameter.ROOM_NUMBER),
                AreaSqm = areaSqm,
                IsBounded = areaSqm > 0,
                Level = CreateSupport.SafeName(level)
            };
        }

        /// <summary>
        /// 设房间名/编号。编号重复时 Revit 会拒绝——这不该让整批回滚，
        /// 但必须说出来：模型以为编号设成了 "101"，实际还是自动编号，
        /// 后面按编号找房间就会找不到。
        /// </summary>
        private static void TrySet(
            Room room, BuiltInParameter id, string value,
            ToolExecutionContext<UIApplication> context, int index)
        {
            try
            {
                var parameter = room.get_Parameter(id);
                if (parameter != null && !parameter.IsReadOnly && parameter.Set(value)) return;
            }
            catch { /* 落到下面的警告 */ }

            var what = id == BuiltInParameter.ROOM_NUMBER ? "编号" : "名称";
            CreateSupport.Once(context,
                "elements[" + index + "]：房间" + what + "没能设成「" + value + "」" +
                (id == BuiltInParameter.ROOM_NUMBER ? "（编号可能与已有房间重复）" : string.Empty) +
                "，用的是 Revit 的默认值。");
        }

        private static double SafeArea(Room room)
        {
            try { return room.Area; }
            catch { return 0; }
        }
    }
}
