using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;

namespace RevitMCP.Addin.Compat
{
    /// <summary>
    /// 面定位构件的创建 API 在 2022 前后换代，差异只允许出现在这里。
    ///
    /// · 楼板：<c>Document.Create.NewFloor</c>（≤2021）→ <c>Floor.Create</c>（2022+，2023 起旧 API 已移除）
    /// · 天花：2022 才有 <c>Ceiling.Create</c>，在那之前 Revit API 根本不提供创建天花的入口
    /// · 屋顶：<c>NewFootPrintRoof</c> 各版本都在，不需要分支
    ///
    /// **[M10] 内环（洞口）的实现分两路**，因为 Revit 没给出一致的入口：
    ///
    /// · <c>Floor.Create</c>（2022+）和 <c>Ceiling.Create</c> 直接收多个 CurveLoop，洞是轮廓的一部分；
    /// · <c>NewFloor</c>（≤2021）和 <c>NewFootPrintRoof</c>（所有版本）只收一个轮廓，
    ///   洞只能建完之后用 <c>NewOpening</c> 单独开。
    ///
    /// 两条路的模型结果不完全一样：后者会在模型里多出 Opening 构件。
    /// 这个差异瞒不住也不该瞒——<see cref="CreateSurfaceResult.OpeningsCreated"/>
    /// 把它原样报给调用方，由工具层转成一条警告。
    /// </summary>
    public static class SurfaceCompat
    {
        /// <summary>
        /// 创建结果。除了构件本身，还要回答"洞是怎么开的"——
        /// 轮廓自带的洞和事后开的 Opening，在后续查询里是两种东西。
        /// </summary>
        public sealed class CreateSurfaceResult
        {
            public Element Element { get; set; }

            /// <summary>事后用 NewOpening 开出来的洞数。轮廓原生带洞时为 0。</summary>
            public int OpeningsCreated { get; set; }
        }

        /// <summary>本版本能不能创建天花。</summary>
        public static bool CanCreateCeiling
        {
#if REVIT2022_OR_GREATER
            get { return true; }
#else
            get { return false; }
#endif
        }

        public static CreateSurfaceResult CreateFloor(
            Document document, IList<Curve> boundary, IList<IList<Curve>> innerLoops,
            ElementType floorType, Level level, bool structural)
        {
#if REVIT2022_OR_GREATER
            var loops = new List<CurveLoop> { ToLoop(boundary) };
            foreach (var inner in Safe(innerLoops)) loops.Add(ToLoop(inner));

            return new CreateSurfaceResult
            {
                Element = Floor.Create(document, loops, floorType.Id, level.Id, structural, null, 0.0)
            };
#else
            var floor = document.Create.NewFloor(ToArray(boundary), (FloorType)floorType, level, structural);
            return new CreateSurfaceResult
            {
                Element = floor,
                OpeningsCreated = Punch(document, floor, innerLoops)
            };
#endif
        }

        /// <summary>
        /// 创建天花。2021 及更早的版本会抛 <see cref="NotSupportedException"/>——
        /// 调用方应先看 <see cref="CanCreateCeiling"/>，把"这个版本做不到"变成一条
        /// 说得清楚的工具错误，而不是一个异常。
        /// </summary>
        public static CreateSurfaceResult CreateCeiling(
            Document document, IList<Curve> boundary, IList<IList<Curve>> innerLoops,
            ElementType ceilingType, Level level)
        {
#if REVIT2022_OR_GREATER
            var loops = new List<CurveLoop> { ToLoop(boundary) };
            foreach (var inner in Safe(innerLoops)) loops.Add(ToLoop(inner));

            return new CreateSurfaceResult
            {
                Element = Ceiling.Create(document, loops, ceilingType.Id, level.Id)
            };
#else
            throw new NotSupportedException("Revit " + RevitVersionInfo.Year + " 的 API 不提供创建天花的入口。");
#endif
        }

        public static CreateSurfaceResult CreateRoof(
            Document document, IList<Curve> boundary, IList<IList<Curve>> innerLoops,
            RoofType roofType, Level level)
        {
            var roof = CreateRoofFootprint(document, boundary, roofType, level);

            // 屋顶在所有版本上都只能事后开洞：NewFootPrintRoof 只收一个轮廓，
            // 没有多环的重载
            return new CreateSurfaceResult
            {
                Element = roof,
                OpeningsCreated = Punch(document, roof, innerLoops)
            };
        }

        private static Element CreateRoofFootprint(
            Document document, IList<Curve> boundary, RoofType roofType, Level level)
        {
            // 这个 out 参数必须先 new 一个再传进去，否则 Revit 抛 ArgumentNullException
            // （"Value cannot be null."，不说是哪个参数）。
            //
            // 原因在 Revit API 是 C++/CLI 包装：签名里的 ModelCurveArray& 实际是
            // tracking reference，被调用方会先读传入的句柄再赋值，读到 null 就拒绝。
            // C# 的 out 语义说调用前不必初始化，但编译器也不会把已赋值的局部变量清零——
            // 所以先 new 一个确实管用。Revit SDK 的官方示例全都这么写，只是从不说为什么。
            var footprint = new ModelCurveArray();
            return document.Create.NewFootPrintRoof(ToArray(boundary), level, roofType, out footprint);
        }

        /// <summary>
        /// 事后在宿主面上开洞。
        ///
        /// <c>NewOpening</c> 的第三个参数是"是否垂直于面"——楼板、屋顶、天花的洞
        /// 都该是竖直贯通的，所以恒为 true。
        ///
        /// **前后各一次 Regenerate，两次都是必要的**（2026-09-17 在 Revit 2019 上实测）：
        ///
        /// · 前面那次：<c>NewFloor</c> 刚返回时楼板还没有几何，
        ///   直接拿它当宿主开洞，Revit 既不抛异常也不报失败，
        ///   而是到事务提交时才回滚，只留下一句"状态：RolledBack"——
        ///   调用方完全无从下手。
        ///
        /// · 后面那次：把开洞本身的失败**逼到事务里暴露**。不加它，
        ///   失败同样会拖到提交时才发作，那时 <see cref="McpFailurePreprocessor"/>
        ///   已经拿不到上下文，错误信息里说不出是哪一步出的问题。
        ///
        /// 开洞失败不吞：调用方要的是"一块带洞的板"，给回一块实心板
        /// 而不说，梯井就会在模型里悄悄消失。
        /// </summary>
        private static int Punch(Document document, Element host, IList<IList<Curve>> innerLoops)
        {
            if (host == null) return 0;

            var loops = Safe(innerLoops);
            if (loops.Count == 0) return 0;

            // 宿主的几何要先生成出来，否则它还不是一个能被开洞的面
            document.Regenerate();

            var punched = 0;

            foreach (var inner in loops)
            {
                Opening opening;
                try
                {
                    opening = document.Create.NewOpening(host, ToArray(inner), true);
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException(
                        "在构件 " + host.Id.ToProtocolString() + " 上开第 " + (punched + 1) +
                        " 个洞时失败：" + ex.Message +
                        "。请检查该内环是否完全落在外轮廓之内、且与其他内环不相交。", ex);
                }

                if (opening == null)
                    throw new InvalidOperationException(
                        "Revit 未能在构件 " + host.Id.ToProtocolString() + " 上开出第 " +
                        (punched + 1) + " 个洞，也没有报错。请检查内环是否落在外轮廓之内。");

                punched++;
            }

            // 让开洞的后果当场结算，而不是拖到提交时变成一句没有上下文的回滚
            document.Regenerate();

            return punched;
        }

        private static IList<IList<Curve>> Safe(IList<IList<Curve>> loops)
        {
            return loops ?? new List<IList<Curve>>();
        }

#if REVIT2022_OR_GREATER
        private static CurveLoop ToLoop(IList<Curve> boundary)
        {
            var loop = new CurveLoop();
            foreach (var curve in boundary) loop.Append(curve);
            return loop;
        }
#endif

        private static CurveArray ToArray(IList<Curve> boundary)
        {
            var array = new CurveArray();
            foreach (var curve in boundary) array.Append(curve);
            return array;
        }
    }
}
