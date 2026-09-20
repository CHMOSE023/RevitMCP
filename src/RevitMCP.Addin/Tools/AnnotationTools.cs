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
    /// <summary>
    /// 本文件的失败构造：把下标前缀写成 <c>annotations[i]</c>，而不是通用的 elements[i]。
    ///
    /// 报错里出现的数组名必须是调用方真的传过的那个，否则它会去找一个不存在的参数。
    /// </summary>
    internal static class AnnotationFail
    {
        public static ToolFailureException At(int index, string code, string message)
        {
            return CreateSupport.Failure(index, code, message, "annotations", "整批未创建");
        }
    }

    // ==================== 创建注释 ====================

    public sealed class AnnotationSpec
    {
        [McpParam("注释种类：textNote（文字）、tag（标记）、dimension（尺寸标注）、" +
                  "revisionCloud（修订云线）", Required = true,
                  AllowedValues = new[] { "textNote", "tag", "dimension", "revisionCloud" })]
        public string Kind { get; set; }

        [McpParam("注释放在哪个视图里。省略则用活动视图。" +
                  "注释是视图专属的——放错视图等于白放，所以建议显式指定")]
        public string ViewId { get; set; }

        [McpParam("注释类型 ID（文字类型 / 标记族类型 / 尺寸标注类型）。" +
                  "省略则用该类别的默认类型。用 revit_list_types 查，" +
                  "文字查 OST_TextNotes、尺寸查 OST_Dimensions")]
        public string TypeId { get; set; }

        [McpParam("放置点，毫米。textNote 与 tag 必填")]
        public Point3D Position { get; set; }

        [McpParam("文字内容。kind 为 textNote 时必填")]
        public string Text { get; set; }

        [McpParam("文字框宽度，毫米。仅 textNote 有效，省略则不换行")]
        public double? WidthMm { get; set; }

        [McpParam("要标记的构件 ID。kind 为 tag 时必填。ElementId 与 uniqueId 两种写法都接受")]
        public string ElementId { get; set; }

        [McpParam("标记是否带引线，默认 false。仅 tag 有效")]
        public bool? AddLeader { get; set; }

        [McpParam("标记方向：horizontal（默认）、vertical。仅 tag 有效",
                  AllowedValues = new[] { "horizontal", "vertical" })]
        public string Orientation { get; set; }

        [McpParam("要标注的两个构件 ID。kind 为 dimension 时必填，至少两个。ElementId 与 uniqueId 两种写法都接受")]
        public List<string> ReferenceIds { get; set; }

        [McpParam("尺寸线的位置与方向，毫米。kind 为 dimension 时必填——" +
                  "标注数值量的是这条线方向上的距离")]
        public LocationLine DimensionLine { get; set; }

        [McpParam("云线的边界点，毫米，至少 3 个点，自动闭合。kind 为 revisionCloud 时必填")]
        public List<Point3D> Boundary { get; set; }

        [McpParam("云线归属的修订 ID，来自 revit_list_revisions。" +
                  "kind 为 revisionCloud 时必填")]
        public string RevisionId { get; set; }
    }

    public sealed class CreateAnnotationsInput
    {
        [McpParam("要创建的注释，一次调用可建多个", Required = true)]
        public List<AnnotationSpec> Annotations { get; set; }

        [McpParam("数量超过单次上限时，带上 true 表示确认后再执行")]
        public bool? Confirm { get; set; }
    }

    public sealed class CreatedAnnotation
    {
        [McpParam("对应 annotations 数组中的下标，从 0 起")]
        public int Index { get; set; }

        [McpParam("新建注释的 ID")]
        public string Id { get; set; }

        [McpParam("注释种类")]
        public string Kind { get; set; }

        [McpParam("所在视图名")]
        public string View { get; set; }

        [McpParam("使用的类型名")]
        public string Type { get; set; }

        [McpParam("标注读数。仅 dimension 有值，带单位，由 Revit 按项目显示单位格式化")]
        public string Value { get; set; }
    }

    public sealed class CreateAnnotationsOutput : IReportsAffectedElements
    {
        [McpParam("成功创建的注释数")]
        public int Created { get; set; }

        [McpParam("新建的注释，顺序与入参一致")]
        public List<CreatedAnnotation> Annotations { get; set; } = new List<CreatedAnnotation>();

        int IReportsAffectedElements.AffectedElements => Created;
    }

    /// <summary>
    /// 批量创建文字、标记、尺寸标注、修订云线。
    ///
    /// 四种东西合成一个工具，是因为它们共享同一组约束：都只存在于某一个视图里、
    /// 都需要一个注释族类型、都用视图坐标定位。分成四个工具会把这组共性
    /// 在四处重复表达一遍，而它们各自的差异其实只有"定位靠什么"这一条。
    /// </summary>
    [McpTool("revit_create_annotations",
        Title = "创建注释",
        Description = "批量创建文字、标记、尺寸标注或修订云线。坐标一律用毫米。" +
                      "注释是**视图专属**的：只在 viewId 指定的那个视图里看得到，" +
                      "换个视图就没有了。省略 viewId 会用活动视图，建议显式指定。" +
                      "标记需要项目里已载入对应类别的标记族，否则会创建失败。" +
                      "**尺寸标注建完一定要核对回执里的 value**：" +
                      "Revit 会接受方向不合理的组合（比如拿水平尺寸线去量两道相互垂直的轴网），" +
                      "不报错，但给出的读数毫无意义——量两道竖向轴网的间距，尺寸线要是水平的。" +
                      "整批要么全部建成、要么一个都不建，且在撤销栈里只占一步。",
        Destructive = false,
        TimeoutSeconds = 120)]
    public sealed class CreateAnnotationsTool : RevitTool<CreateAnnotationsInput, CreateAnnotationsOutput>
    {
        public override CreateAnnotationsOutput Execute(
            CreateAnnotationsInput input, ToolExecutionContext<UIApplication> context)
        {
            var document = RequireDocument(context);
            RequireBatch(input.Annotations, input.Confirm, context, "创建");

            var output = new CreateAnnotationsOutput();
            var total = input.Annotations.Count;

            for (var index = 0; index < total; index++)
            {
                context.CancellationToken.ThrowIfCancellationRequested();

                var spec = input.Annotations[index];
                if (spec == null)
                    throw AnnotationFail.At(index, McpDomainError.InvalidParameter, "该项为 null。");

                output.Annotations.Add(CreateOne(document, context, spec, index));
                ProgressTicker.Tick(context.Progress, index + 1, total, "已创建");
            }

            output.Created = output.Annotations.Count;
            return output;
        }

        private static CreatedAnnotation CreateOne(
            Document document, ToolExecutionContext<UIApplication> context,
            AnnotationSpec spec, int index)
        {
            var kind = (spec.Kind ?? string.Empty).Trim().ToLowerInvariant();
            var view = AnnotationSupport.ResolveView(document, context, spec.ViewId, index);

            var result = new CreatedAnnotation
            {
                Index = index,
                Kind = kind,
                View = AnnotationSupport.SafeName(view)
            };

            switch (kind)
            {
                case "textnote":
                    CreateTextNote(document, context, spec, index, view, result);
                    break;

                case "tag":
                    CreateTag(document, context, spec, index, view, result);
                    break;

                case "dimension":
                    CreateDimension(document, context, spec, index, view, result);
                    break;

                case "revisioncloud":
                    CreateRevisionCloud(document, spec, index, view, result);
                    break;

                default:
                    throw AnnotationFail.At(index, McpDomainError.InvalidParameter,
                        "无法识别的 kind \"" + spec.Kind +
                        "\"。可用值：textNote、tag、dimension、revisionCloud。");
            }

            return result;
        }

        // ==================== 文字 ====================

        private static void CreateTextNote(
            Document document, ToolExecutionContext<UIApplication> context,
            AnnotationSpec spec, int index, View view, CreatedAnnotation result)
        {
            if (string.IsNullOrEmpty(spec.Text))
                throw AnnotationFail.At(index, McpDomainError.InvalidParameter,
                    "kind 为 textNote 时必须给 text。");

            var origin = AnnotationSupport.RequirePosition(spec.Position, index, "textNote");
            var type = AnnotationSupport.ResolveAnnotationType<TextNoteType>(
                document, context, spec.TypeId, BuiltInCategory.OST_TextNotes, index, "文字");

            TextNote note;
            try
            {
                if (spec.WidthMm != null)
                {
                    var width = Units.ToFeet(spec.WidthMm.Value);

                    // Revit 对文字框宽度有下限，给得太小会抛一句很难懂的话。
                    // 提前问一次它的下限，把这变成一条说得清的参数错误
                    var minimum = TextNote.GetMinimumAllowedWidth(document, type.Id);
                    var maximum = TextNote.GetMaximumAllowedWidth(document, type.Id);

                    if (width < minimum || width > maximum)
                        throw AnnotationFail.At(index, McpDomainError.InvalidParameter,
                            "widthMm " + Format(spec.WidthMm.Value) + " 超出该文字类型允许的范围（" +
                            Format(Units.FromFeet(minimum)) + " ~ " +
                            Format(Units.FromFeet(maximum)) + " 毫米）。");

                    note = TextNote.Create(document, view.Id, origin, width, spec.Text, type.Id);
                }
                else
                {
                    note = TextNote.Create(document, view.Id, origin, spec.Text, type.Id);
                }
            }
            catch (ToolFailureException) { throw; }
            catch (Exception ex)
            {
                throw AnnotationFail.At(index, McpDomainError.TransactionFailed,
                    "Revit 拒绝创建文字：" + ex.Message);
            }

            result.Id = note.Id.ToProtocolString();
            result.Type = AnnotationSupport.SafeName(type);
        }

        // ==================== 标记 ====================

        private static void CreateTag(
            Document document, ToolExecutionContext<UIApplication> context,
            AnnotationSpec spec, int index, View view, CreatedAnnotation result)
        {
            if (string.IsNullOrWhiteSpace(spec.ElementId))
                throw AnnotationFail.At(index, McpDomainError.InvalidParameter,
                    "kind 为 tag 时必须给 elementId（要标记哪个构件）。");

            var target = CreateSupport.RequireElement(document, spec.ElementId, index);
            var position = AnnotationSupport.RequirePosition(spec.Position, index, "tag");
            var orientation = AnnotationSupport.ParseOrientation(spec.Orientation, index);

            var tag = AnnotationSupport.CreateTag(
                document, view, target, position, spec.AddLeader ?? false, orientation, index);

            // 标记族类型在创建之后才能改——Create 不接受类型参数的那个重载是按类别选默认族的
            if (!string.IsNullOrWhiteSpace(spec.TypeId))
            {
                var type = ChangeTypeTool.RequireType(document, spec.TypeId, "typeId");
                try { tag.ChangeTypeId(type.Id); }
                catch (Exception ex)
                {
                    context.Warnings.Add(
                        "annotations[" + index + "]：标记已创建，但换成指定的标记类型失败：" +
                        ex.Message + "。标记族必须与被标记构件的类别匹配。");
                }
            }

            result.Id = tag.Id.ToProtocolString();
            result.Type = AnnotationSupport.TypeNameOf(document, tag);
        }

        // ==================== 尺寸标注 ====================

        private static void CreateDimension(
            Document document, ToolExecutionContext<UIApplication> context,
            AnnotationSpec spec, int index, View view, CreatedAnnotation result)
        {
            if (spec.ReferenceIds == null || spec.ReferenceIds.Count < 2)
                throw AnnotationFail.At(index, McpDomainError.InvalidParameter,
                    "kind 为 dimension 时必须给至少两个 referenceIds——尺寸标注量的是两个东西之间的距离。");

            var line = CreateSupport.RequireLine(spec.DimensionLine, index);
            var direction = (line.GetEndPoint(1) - line.GetEndPoint(0)).Normalize();

            var references = new ReferenceArray();
            foreach (var rawId in spec.ReferenceIds)
            {
                var element = CreateSupport.RequireElement(document, rawId, index);
                references.Append(
                    AnnotationSupport.ResolveDimensionReference(element, view, direction, index, context));
            }

            Dimension dimension;
            try
            {
                dimension = string.IsNullOrWhiteSpace(spec.TypeId)
                    ? document.Create.NewDimension(view, line, references)
                    : document.Create.NewDimension(
                        view, line, references,
                        RequireDimensionType(document, spec.TypeId, index));
            }
            catch (Exception ex)
            {
                throw AnnotationFail.At(index, McpDomainError.TransactionFailed,
                    "Revit 拒绝创建尺寸标注：" + ex.Message +
                    "。最常见的原因是这些构件在该视图里不可见，" +
                    "或者尺寸线的方向与它们之间的距离方向不一致——" +
                    "量两道竖向轴网之间的距离，尺寸线要是水平的。");
            }

            if (dimension == null)
                throw AnnotationFail.At(index, McpDomainError.TransactionFailed,
                    "Revit 未能创建尺寸标注，但也没有报错。请确认被标注的构件在该视图里可见。");

            result.Id = dimension.Id.ToProtocolString();
            result.Type = AnnotationSupport.TypeNameOf(document, dimension);

            try { result.Value = dimension.ValueString; }
            catch { /* 多段标注读不到单一读数，不影响创建本身 */ }
        }

        private static DimensionType RequireDimensionType(Document document, string rawId, int index)
        {
            var type = ChangeTypeTool.RequireType(document, rawId, "typeId") as DimensionType;

            if (type == null)
                throw AnnotationFail.At(index, McpDomainError.InvalidParameter,
                    "typeId " + rawId + " 不是尺寸标注类型。用 revit_list_types 查 OST_Dimensions。");

            return type;
        }

        // ==================== 修订云线 ====================

        private static void CreateRevisionCloud(
            Document document, AnnotationSpec spec, int index, View view, CreatedAnnotation result)
        {
            if (spec.Boundary == null || spec.Boundary.Count < 3)
                throw AnnotationFail.At(index, McpDomainError.InvalidParameter,
                    "kind 为 revisionCloud 时必须给至少 3 个 boundary 点。");

            if (string.IsNullOrWhiteSpace(spec.RevisionId))
                throw AnnotationFail.At(index, McpDomainError.InvalidParameter,
                    "kind 为 revisionCloud 时必须给 revisionId。用 revit_list_revisions 查；" +
                    "项目里一个修订都没有的话，需要用户先在 Revit 的「图纸发布/修订」里新建一个。");

            var revision = CreateSupport.RequireElement(document, spec.RevisionId, index) as Revision;
            if (revision == null)
                throw AnnotationFail.At(index, McpDomainError.InvalidParameter,
                    "revisionId " + spec.RevisionId + " 不是修订。用 revit_list_revisions 取 ID。");

            var curves = AnnotationSupport.BuildClosedLoop(spec.Boundary, index);

            RevisionCloud cloud;
            try
            {
                cloud = RevisionCloud.Create(document, view, revision.Id, curves);
            }
            catch (Exception ex)
            {
                throw AnnotationFail.At(index, McpDomainError.TransactionFailed,
                    "Revit 拒绝创建修订云线：" + ex.Message);
            }

            result.Id = cloud.Id.ToProtocolString();
            result.Type = AnnotationSupport.SafeName(revision);
        }

        private static string Format(double value)
        {
            return value.ToString("0.###", CultureInfo.InvariantCulture);
        }
    }

    // ==================== 批量标记 ====================

    public sealed class TagAllInput
    {
        [McpParam("要标记的构件类别，如 OST_Doors、OST_Windows、OST_Rooms。" +
                  "可省略 OST_ 前缀", Required = true)]
        public string Category { get; set; }

        [McpParam("在哪个视图里标记。省略则用活动视图")]
        public string ViewId { get; set; }

        [McpParam("标记族类型 ID。省略则用该类别的默认标记族。" +
                  "用 revit_list_types 查对应的标记类别，如 OST_DoorTags")]
        public string TypeId { get; set; }

        [McpParam("标记是否带引线，默认 false")]
        public bool? AddLeader { get; set; }

        [McpParam("标记方向：horizontal（默认）、vertical",
                  AllowedValues = new[] { "horizontal", "vertical" })]
        public string Orientation { get; set; }

        [McpParam("跳过已经有标记的构件，默认 true。" +
                  "设为 false 会给已标记的构件再加一个，通常不是想要的结果")]
        public bool? SkipTagged { get; set; }

        [McpParam("影响构件数超过上限时，带上 true 表示确认后再执行")]
        public bool? Confirm { get; set; }
    }

    public sealed class TagAllOutput : IReportsAffectedElements
    {
        [McpParam("视图名")]
        public string View { get; set; }

        [McpParam("类别名")]
        public string Category { get; set; }

        [McpParam("该视图里这个类别的构件总数")]
        public int Candidates { get; set; }

        [McpParam("本次新建的标记数")]
        public int Tagged { get; set; }

        [McpParam("因已有标记而跳过的构件数")]
        public int AlreadyTagged { get; set; }

        [McpParam("因没有定位点而无法标记的构件数——它们需要手工处理")]
        public int Skipped { get; set; }

        [McpParam("新建的标记 ID")]
        public List<string> TagIds { get; set; } = new List<string>();

        int IReportsAffectedElements.AffectedElements => Tagged;
    }

    /// <summary>
    /// 把一个视图里某类别的构件全部打上标记——Revit「按类别全部标记」那个命令。
    /// 出图阶段最省事的一步，也是最容易一次生成几百个图元的一步，所以要过规模闸。
    /// </summary>
    [McpTool("revit_tag_all_in_view",
        Title = "在视图中批量标记",
        Description = "把指定视图里某个类别的构件全部打上标记，对应 Revit 的「按类别全部标记」。" +
                      "默认跳过已经有标记的构件。" +
                      "需要项目里已载入对应类别的标记族——没载入会整批失败并说明原因。" +
                      "整批要么全成、要么一个都不建，且在撤销栈里只占一步。",
        Destructive = false,
        TimeoutSeconds = 180)]
    public sealed class TagAllInViewTool : RevitTool<TagAllInput, TagAllOutput>
    {
        public override TagAllOutput Execute(TagAllInput input, ToolExecutionContext<UIApplication> context)
        {
            var document = RequireDocument(context);
            var view = AnnotationSupport.ResolveView(document, context, input.ViewId, -1);
            var category = ParseCategory(input.Category);
            var orientation = AnnotationSupport.ParseOrientation(input.Orientation, -1);
            var skipTagged = input.SkipTagged ?? true;

            var candidates = new FilteredElementCollector(document, view.Id)
                .OfCategory(category)
                .WhereElementIsNotElementType()
                .ToList();

            var output = new TagAllOutput
            {
                View = AnnotationSupport.SafeName(view),
                Category = category.ToString(),
                Candidates = candidates.Count
            };

            if (candidates.Count == 0)
            {
                context.Warnings.Add(
                    "视图「" + output.View + "」里没有 " + category + " 类别的构件，什么都没做。" +
                    "先用 revit_query_elements 带 activeViewOnly 确认该视图里有什么。");
                return output;
            }

            var tagged = skipTagged ? AnnotationSupport.AlreadyTaggedIn(document, view) : null;

            GuardScale(candidates.Count, input.Confirm, context, "标记",
                "否则请换一个范围更小的视图，或改用 revit_create_annotations 逐个标记。");

            ElementType tagType = null;
            if (!string.IsNullOrWhiteSpace(input.TypeId))
                tagType = ChangeTypeTool.RequireType(document, input.TypeId, "typeId");

            var total = candidates.Count;

            for (var index = 0; index < total; index++)
            {
                context.CancellationToken.ThrowIfCancellationRequested();

                var element = candidates[index];

                if (tagged != null && tagged.Contains(element.Id.GetValue()))
                {
                    output.AlreadyTagged++;
                    continue;
                }

                var position = AnnotationSupport.AnchorOf(element);
                if (position == null)
                {
                    // 没有定位点的构件（如某些系统族的子部件）标不了。
                    // 数出来让模型知道有多少漏网之鱼，而不是悄悄少标几个
                    output.Skipped++;
                    continue;
                }

                var tag = AnnotationSupport.CreateTag(
                    document, view, element, position, input.AddLeader ?? false, orientation, -1);

                if (tagType != null)
                {
                    try { tag.ChangeTypeId(tagType.Id); }
                    catch (Exception ex)
                    {
                        throw new ToolFailureException(McpDomainError.InvalidParameter,
                            "标记类型「" + SafeTypeName(tagType) + "」用不到 " + category +
                            " 上：" + ex.Message + "（整批未创建）。标记族必须与被标记构件的类别匹配。");
                    }
                }

                output.Tagged++;
                output.TagIds.Add(tag.Id.ToProtocolString());

                ProgressTicker.Tick(context.Progress, index + 1, total, "已标记");
            }

            if (output.Skipped > 0)
                context.Warnings.Add(
                    output.Skipped + " 个构件没有可用的定位点，未能标记，需要在 Revit 里手工处理。");

            return output;
        }

        private static string SafeTypeName(Element element)
        {
            try { return element?.Name; }
            catch { return null; }
        }
    }

    // ==================== 修订清单 ====================

    public sealed class ListRevisionsInput
    {
        [McpParam("要查询的文档 ID，来自 revit_list_documents。省略则用当前活动文档")]
        public string DocumentId { get; set; }
    }

    public sealed class RevisionInfo
    {
        [McpParam("修订 ID。创建修订云线时用它")]
        public string Id { get; set; }

        [McpParam("修订序号（Revit 里的「序列」）")]
        public int Sequence { get; set; }

        [McpParam("修订编号，图纸上显示的那个")]
        public string Number { get; set; }

        [McpParam("修订日期")]
        public string Date { get; set; }

        [McpParam("修订说明")]
        public string Description { get; set; }

        [McpParam("发布人")]
        public string IssuedBy { get; set; }

        [McpParam("审核人")]
        public string IssuedTo { get; set; }

        [McpParam("是否已发布。已发布的修订不能再改")]
        public bool Issued { get; set; }

        [McpParam("这个修订下有多少条云线")]
        public int CloudCount { get; set; }
    }

    public sealed class ListRevisionsOutput
    {
        [McpParam("修订总数")]
        public int Total { get; set; }

        [McpParam("修订列表，按序列排序")]
        public List<RevisionInfo> Revisions { get; set; } = new List<RevisionInfo>();
    }

    [McpTool("revit_list_revisions",
        Title = "列出修订",
        Description = "列出项目里的修订序列及其编号、日期、说明、发布状态。" +
                      "创建修订云线需要先有一个修订——ID 从这里取。" +
                      "已发布（issued）的修订不能再挂新的云线。",
        ReadOnly = true,
        TimeoutSeconds = 60)]
    public sealed class ListRevisionsTool : RevitTool<ListRevisionsInput, ListRevisionsOutput>
    {
        public override ListRevisionsOutput Execute(
            ListRevisionsInput input, ToolExecutionContext<UIApplication> context)
        {
            var document = ResolveDocument(context, input.DocumentId);

            // 云线数先一次性数完，避免逐个修订去 collect
            var cloudCounts = new Dictionary<long, int>();
            foreach (var cloud in new FilteredElementCollector(document)
                         .OfCategory(BuiltInCategory.OST_RevisionClouds)
                         .WhereElementIsNotElementType()
                         .OfType<RevisionCloud>())
            {
                ElementId revisionId;
                try { revisionId = cloud.RevisionId; }
                catch { continue; }

                if (revisionId == null || revisionId == ElementId.InvalidElementId) continue;

                var key = revisionId.GetValue();
                cloudCounts.TryGetValue(key, out var count);
                cloudCounts[key] = count + 1;
            }

            var output = new ListRevisionsOutput();

            foreach (var revision in new FilteredElementCollector(document)
                         .OfCategory(BuiltInCategory.OST_Revisions)
                         .WhereElementIsNotElementType()
                         .OfType<Revision>())
            {
                context.CancellationToken.ThrowIfCancellationRequested();

                var info = new RevisionInfo
                {
                    Id = revision.Id.ToProtocolString(),
                    Sequence = revision.SequenceNumber,
                    Date = revision.RevisionDate,
                    Description = revision.Description,
                    IssuedBy = revision.IssuedBy,
                    IssuedTo = revision.IssuedTo,
                    Issued = revision.Issued
                };

                // RevisionNumber 在部分版本/编号方式下会抛，拿不到就留空
                try { info.Number = revision.RevisionNumber; }
                catch { info.Number = null; }

                if (cloudCounts.TryGetValue(revision.Id.GetValue(), out var clouds))
                    info.CloudCount = clouds;

                output.Revisions.Add(info);
            }

            output.Revisions = output.Revisions.OrderBy(r => r.Sequence).ToList();
            output.Total = output.Revisions.Count;
            return output;
        }
    }
}
