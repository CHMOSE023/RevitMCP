using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace RevitMCP.Addin.Compat
{
    /// <summary>
    /// 标记与被标记构件的关联在 2022 换了 API：一个标记从只能指向一个构件
    /// 变成可以指向多个，<c>TaggedLocalElementId</c> 随之被 <c>GetTaggedLocalElementIds()</c> 取代。
    /// 差异只允许出现在这里。
    /// </summary>
    public static class TagCompat
    {
        /// <summary>这个标记指向了哪些构件。取不到时返回空集合。</summary>
        public static IEnumerable<ElementId> TaggedElementIds(IndependentTag tag)
        {
            if (tag == null) return Enumerable.Empty<ElementId>();

            try
            {
#if REVIT2022_OR_GREATER
                return tag.GetTaggedLocalElementIds();
#else
                var id = tag.TaggedLocalElementId;
                return id == null || id == ElementId.InvalidElementId
                    ? Enumerable.Empty<ElementId>()
                    : new[] { id };
#endif
            }
            catch
            {
                // 指向链接模型里构件的标记读不出本地 ID，这属于正常情况而非错误
                return Enumerable.Empty<ElementId>();
            }
        }
    }
}
