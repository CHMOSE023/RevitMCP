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
    /// </summary>
    public static class SurfaceCompat
    {
        /// <summary>本版本能不能创建天花。</summary>
        public static bool CanCreateCeiling
        {
#if REVIT2022_OR_GREATER
            get { return true; }
#else
            get { return false; }
#endif
        }

        public static Element CreateFloor(
            Document document, IList<Curve> boundary, ElementType floorType, Level level, bool structural)
        {
#if REVIT2022_OR_GREATER
            var loops = new List<CurveLoop> { ToLoop(boundary) };
            return Floor.Create(document, loops, floorType.Id, level.Id, structural, null, 0.0);
#else
            return document.Create.NewFloor(ToArray(boundary), (FloorType)floorType, level, structural);
#endif
        }

        /// <summary>
        /// 创建天花。2021 及更早的版本会抛 <see cref="NotSupportedException"/>——
        /// 调用方应先看 <see cref="CanCreateCeiling"/>，把"这个版本做不到"变成一条
        /// 说得清楚的工具错误，而不是一个异常。
        /// </summary>
        public static Element CreateCeiling(
            Document document, IList<Curve> boundary, ElementType ceilingType, Level level)
        {
#if REVIT2022_OR_GREATER
            var loops = new List<CurveLoop> { ToLoop(boundary) };
            return Ceiling.Create(document, loops, ceilingType.Id, level.Id);
#else
            throw new NotSupportedException("Revit " + RevitVersionInfo.Year + " 的 API 不提供创建天花的入口。");
#endif
        }

        public static Element CreateRoof(
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
