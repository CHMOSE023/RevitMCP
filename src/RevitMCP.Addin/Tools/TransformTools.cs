using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Compat;
using RevitMCP.Tooling;

namespace RevitMCP.Addin.Tools
{
    // ==================== 共用形状 ====================

    public sealed class RotationSpec
    {
        [McpParam("旋转轴经过的点，毫米", Required = true)]
        public Point3D Origin { get; set; }

        [McpParam("旋转轴方向：z（默认，绕竖直轴转，平面图里看到的那种旋转）、x 或 y。" +
                  "也可以给 custom 并填 axisDirection",
                  AllowedValues = new[] { "z", "x", "y", "custom" })]
        public string Axis { get; set; }

        [McpParam("自定义轴方向向量。仅 axis 为 custom 时使用，不需要归一化")]
        public Point3D AxisDirection { get; set; }

        [McpParam("旋转角度，度。逆时针为正（从轴的正方向往回看）", Required = true)]
        public double Degrees { get; set; }
    }

    public sealed class MirrorSpec
    {
        [McpParam("镜像面经过的点，毫米", Required = true)]
        public Point3D Origin { get; set; }

        [McpParam("镜像面的法线方向。填 x 表示以 YZ 平面为镜（左右翻）、y 表示以 XZ 平面为镜、" +
                  "z 表示以水平面为镜（上下翻）。也可以给 custom 并填 normalDirection", Required = true,
                  AllowedValues = new[] { "x", "y", "z", "custom" })]
        public string Normal { get; set; }

        [McpParam("自定义法线向量。仅 normal 为 custom 时使用，不需要归一化")]
        public Point3D NormalDirection { get; set; }
    }

    public sealed class TransformElementsInput
    {
        [McpParam("要操作的构件 ID 列表，来自 revit_query_elements 或 revit_get_selection。ElementId 与 uniqueId 两种写法都接受", Required = true)]
        public List<string> ElementIds { get; set; }

        [McpParam("操作类型：move（平移）、copy（复制并平移）、rotate（旋转）、mirror（镜像）", Required = true,
                  AllowedValues = new[] { "move", "copy", "rotate", "mirror" })]
        public string Operation { get; set; }

        [McpParam("平移向量，毫米。move 与 copy 必填。copy 时给零向量表示原位复制")]
        public Point3D Translation { get; set; }

        [McpParam("旋转参数。operation 为 rotate 时必填")]
        public RotationSpec Rotation { get; set; }

        [McpParam("镜像参数。operation 为 mirror 时必填")]
        public MirrorSpec Mirror { get; set; }

        [McpParam("镜像时保留原构件（即镜像出一份副本），默认 true。" +
                  "false 表示把原构件翻过去，不留副本")]
        public bool? KeepOriginal { get; set; }

        [McpParam("影响构件数超过上限时，带上 true 表示确认后再执行")]
        public bool? Confirm { get; set; }
    }

    public sealed class TransformedElement
    {
        [McpParam("原构件 ID")]
        public string Id { get; set; }

        [McpParam("构件名称")]
        public string Name { get; set; }

        [McpParam("所属类别")]
        public string Category { get; set; }

        [McpParam("新生成的构件 ID。copy 与保留原件的 mirror 才有，move/rotate 为 null")]
        public string NewId { get; set; }
    }

    public sealed class TransformElementsOutput : IReportsAffectedElements
    {
        [McpParam("实际执行的操作")]
        public string Operation { get; set; }

        [McpParam("被操作的构件数")]
        public int Affected { get; set; }

        [McpParam("新生成的构件数。move 与 rotate 恒为 0")]
        public int CreatedCount { get; set; }

        [McpParam("逐个构件的结果")]
        public List<TransformedElement> Elements { get; set; } = new List<TransformedElement>();

        int IReportsAffectedElements.AffectedElements => Affected;
    }

    /// <summary>
    /// 平移 / 复制 / 旋转 / 镜像。
    ///
    /// 四种操作合成一个工具，是因为它们在 Revit 里是同一件事的四个面：
    /// <c>ElementTransformUtils</c> 对整批构件施加一个变换。拆成四个工具，
    /// 模型要多记四个名字，而它们的入参、约束、失败模式完全一致。
    /// </summary>
    [McpTool("revit_transform_elements",
        Toolsets = new[] { Toolsets.Authoring },
        Title = "平移/复制/旋转/镜像构件",
        Description = "对一批构件做平移、复制、旋转或镜像。坐标与距离一律用毫米，角度用度。" +
                      "整批要么全部成功、要么全部不动。" +
                      "被钉住（pinned）的构件不能移动——先用 revit_set_elements_pinned 解钉。" +
                      "依附于宿主的构件（门窗）会跟着宿主走，单独移动它们通常会被 Revit 拒绝。",
        TimeoutSeconds = 120)]
    public sealed class TransformElementsTool : RevitTool<TransformElementsInput, TransformElementsOutput>
    {
        public override TransformElementsOutput Execute(
            TransformElementsInput input, ToolExecutionContext<UIApplication> context)
        {
            var document = RequireDocument(context);

            if (input.ElementIds == null || input.ElementIds.Count == 0)
                throw new ToolFailureException(McpDomainError.InvalidParameter,
                    "elementIds 不能为空，至少要给一个构件。");

            var operation = (input.Operation ?? string.Empty).Trim().ToLowerInvariant();
            if (operation.Length == 0)
                throw new ToolFailureException(McpDomainError.InvalidParameter,
                    "operation 不能为空。可用值：move、copy、rotate、mirror。");

            GuardScale(input.ElementIds.Count, input.Confirm, context, OperationVerb(operation),
                "否则请分批调用，每批不超过 " + context.MaxElementsPerWrite + " 个。");

            // 先把 ID 全部解析出来再动手。少解析一个就动手，等于把"全有全无"的承诺
            // 交给 Revit 的执行顺序去碰运气
            var elements = Resolve(document, input.ElementIds);
            var ids = elements.Select(e => e.Id).ToList();

            var output = new TransformElementsOutput { Operation = operation };

            switch (operation)
            {
                case "move":
                    Move(document, ids, RequireTranslation(input, "move"));
                    Fill(output, elements, null);
                    break;

                case "copy":
                    var copied = Copy(document, ids, RequireTranslation(input, "copy"));
                    Fill(output, elements, copied);
                    break;

                case "rotate":
                    Rotate(document, ids, input.Rotation);
                    Fill(output, elements, null);
                    break;

                case "mirror":
                    var keep = input.KeepOriginal ?? true;
                    var mirrored = Mirror(document, ids, input.Mirror, keep, context);
                    Fill(output, elements, keep ? mirrored : null);
                    break;

                default:
                    throw new ToolFailureException(McpDomainError.InvalidParameter,
                        "无法识别的 operation \"" + input.Operation +
                        "\"。可用值：move（平移）、copy（复制）、rotate（旋转）、mirror（镜像）。");
            }

            output.Affected = output.Elements.Count;
            output.CreatedCount = output.Elements.Count(e => e.NewId != null);
            return output;
        }

        // ==================== 四种操作 ====================

        private static void Move(Document document, IList<ElementId> ids, XYZ translation)
        {
            try
            {
                ElementTransformUtils.MoveElements(document, ids, translation);
            }
            catch (Exception ex)
            {
                throw Rejected("平移", ex);
            }
        }

        private static IList<ElementId> Copy(Document document, IList<ElementId> ids, XYZ translation)
        {
            try
            {
                return ElementTransformUtils.CopyElements(document, ids, translation).ToList();
            }
            catch (Exception ex)
            {
                throw Rejected("复制", ex);
            }
        }

        private static void Rotate(Document document, IList<ElementId> ids, RotationSpec spec)
        {
            if (spec == null)
                throw new ToolFailureException(McpDomainError.InvalidParameter,
                    "operation 为 rotate 时必须给 rotation。");

            if (spec.Origin == null)
                throw new ToolFailureException(McpDomainError.InvalidParameter,
                    "rotation.origin 不能为空——旋转需要一个轴心点。");

            if (Math.Abs(spec.Degrees) < 1e-9)
                throw new ToolFailureException(McpDomainError.InvalidParameter,
                    "rotation.degrees 为 0，这次调用不会有任何效果。");

            var direction = ResolveDirection(spec.Axis, spec.AxisDirection, "rotation.axis", XYZ.BasisZ);
            var axis = Line.CreateUnbound(spec.Origin.ToXyz(), direction);
            var radians = spec.Degrees * Math.PI / 180.0;

            try
            {
                ElementTransformUtils.RotateElements(document, ids, axis, radians);
            }
            catch (Exception ex)
            {
                throw Rejected("旋转", ex);
            }
        }

        private static IList<ElementId> Mirror(
            Document document, IList<ElementId> ids, MirrorSpec spec, bool keepOriginal,
            ToolExecutionContext<UIApplication> context)
        {
            if (spec == null)
                throw new ToolFailureException(McpDomainError.InvalidParameter,
                    "operation 为 mirror 时必须给 mirror。");

            if (spec.Origin == null)
                throw new ToolFailureException(McpDomainError.InvalidParameter,
                    "mirror.origin 不能为空——镜像需要一个镜面上的点。");

            // Revit 对哪些构件能镜像有自己的一套规则，提前问一次，
            // 比让事务跑一半再报一句晦涩的异常要好
            if (!CanMirror(document, ids))
                throw new ToolFailureException(McpDomainError.InvalidParameter,
                    "这批构件里有 Revit 不允许镜像的（常见的是房间、标记、部分系统族实例）。" +
                    "请缩小范围后重试。");

            var normal = ResolveDirection(spec.Normal, spec.NormalDirection, "mirror.normal", null);
            var plane = Plane.CreateByNormalAndOrigin(normal, spec.Origin.ToXyz());

            try
            {
                var created = ElementTransformUtils
                    .MirrorElements(document, ids, plane, keepOriginal)
                    .ToList();

                // 保留原件时应当每个构件都镜出一份。数量对不上说明 Revit 悄悄跳过了一些，
                // 说出来比让模型以为全成了要好
                if (keepOriginal && created.Count != ids.Count)
                    context.Warnings.Add(
                        "请求镜像 " + ids.Count + " 个构件，Revit 实际生成了 " + created.Count +
                        " 个副本——有构件被 Revit 跳过了。");

                return created;
            }
            catch (Exception ex)
            {
                throw Rejected("镜像", ex);
            }
        }

        private static bool CanMirror(Document document, IList<ElementId> ids)
        {
            try { return ElementTransformUtils.CanMirrorElements(document, ids); }
            catch { return true; }   // 问不出来就不拦，让 Revit 自己判断
        }

        // ==================== 辅助 ====================

        private static XYZ RequireTranslation(TransformElementsInput input, string operation)
        {
            if (input.Translation == null)
                throw new ToolFailureException(McpDomainError.InvalidParameter,
                    "operation 为 " + operation + " 时必须给 translation（平移向量，毫米）。");

            var vector = input.Translation.ToXyz();

            // 零向量的平移是个无操作，但零向量的复制是"原位复制"，是有意义的
            if (operation == "move" && vector.GetLength() < 1e-9)
                throw new ToolFailureException(McpDomainError.InvalidParameter,
                    "translation 是零向量，这次平移不会有任何效果。");

            return vector;
        }

        /// <summary>
        /// 把 x/y/z/custom 解析成方向向量。
        /// <paramref name="fallback"/> 为 null 表示该参数必填。
        /// </summary>
        private static XYZ ResolveDirection(string keyword, Point3D custom, string fieldName, XYZ fallback)
        {
            var text = (keyword ?? string.Empty).Trim().ToLowerInvariant();

            if (text.Length == 0)
            {
                if (fallback != null) return fallback;

                throw new ToolFailureException(McpDomainError.InvalidParameter,
                    fieldName + " 不能为空。可用值：x、y、z 或 custom。");
            }

            switch (text)
            {
                case "x": return XYZ.BasisX;
                case "y": return XYZ.BasisY;
                case "z": return XYZ.BasisZ;
                case "custom": break;
                default:
                    throw new ToolFailureException(McpDomainError.InvalidParameter,
                        "无法识别的 " + fieldName + " \"" + keyword + "\"。可用值：x、y、z、custom。");
            }

            if (custom == null)
                throw new ToolFailureException(McpDomainError.InvalidParameter,
                    fieldName + " 为 custom 时必须给出对应的方向向量。");

            // 方向向量不涉及位置，不需要单位换算——只要方向对就行
            var vector = new XYZ(custom.X, custom.Y, custom.Z ?? 0);

            if (vector.GetLength() < 1e-9)
                throw new ToolFailureException(McpDomainError.InvalidParameter,
                    fieldName + " 给出的是零向量，无法确定方向。");

            return vector.Normalize();
        }

        private static List<Element> Resolve(Document document, IList<string> rawIds)
        {
            var elements = new List<Element>(rawIds.Count);
            var seen = new HashSet<long>();

            foreach (var rawId in rawIds)
            {
                var element = RequireElement(document, rawId);

                // 同一个 ID 传两次，MoveElements 会把它平移两次。
                // 去重而不是报错：重复通常是调用方拼接列表时的意外，不是意图
                if (!seen.Add(element.Id.GetValue())) continue;

                elements.Add(element);
            }

            return elements;
        }

        private static void Fill(
            TransformElementsOutput output, IList<Element> elements, IList<ElementId> created)
        {
            for (var index = 0; index < elements.Count; index++)
            {
                var element = elements[index];

                output.Elements.Add(new TransformedElement
                {
                    Id = element.Id.ToProtocolString(),
                    Name = SafeName(element),
                    Category = element.Category?.Name,

                    // Revit 保证返回的新 ID 与输入顺序一一对应；数量对不上时宁可不给，
                    // 也不能给出一个对错位的映射——那比没有更糟
                    NewId = created != null && created.Count == elements.Count
                        ? created[index].ToProtocolString()
                        : null
                });
            }
        }

        private static string OperationVerb(string operation)
        {
            switch (operation)
            {
                case "move": return "平移";
                case "copy": return "复制";
                case "rotate": return "旋转";
                case "mirror": return "镜像";
                default: return "操作";
            }
        }

        private static ToolFailureException Rejected(string verb, Exception ex)
        {
            return new ToolFailureException(McpDomainError.TransactionFailed,
                "Revit 拒绝" + verb + "这批构件：" + ex.Message +
                "（整批未改动）。常见原因：构件被钉住、依附于宿主、或处于被约束的组内。");
        }

        private static string SafeName(Element element)
        {
            try { return element.Name; }
            catch { return null; }
        }
    }
}
