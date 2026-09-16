using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace RevitMCP.Addin
{
    /// <summary>
    /// 打开的文档怎么指代。
    ///
    /// 一个 Revit 实例可以同时打开多个项目，而 <c>Document</c> 没有 ElementId 这样的天然标识。
    /// 已保存的用路径，未保存的退到标题——和文档切换检测用的是同一套 Key（见 ServerHost），
    /// 两处必须一致，否则"当前文档"和"我要查的文档"会指向不同的东西。
    /// </summary>
    internal static class DocumentRef
    {
        /// <summary>文档的稳定标识。取不到时返回 null。</summary>
        public static string KeyOf(Document document)
        {
            if (document == null) return null;

            string path;
            try { path = document.PathName; }
            catch { path = null; }

            if (!string.IsNullOrEmpty(path)) return path;

            try { return document.Title; }
            catch { return null; }
        }

        /// <summary>
        /// 遍历这个 Revit 实例里打开的所有文档。
        ///
        /// 默认跳过链接模型：它们是被别的项目引用进来的，不是用户"打开"的文档，
        /// 把它们当成独立模型去审计会得出莫名其妙的结论。
        /// </summary>
        public static IEnumerable<Document> Opened(UIApplication application, bool includeLinked = false)
        {
            DocumentSet documents;
            try { documents = application?.Application?.Documents; }
            catch { yield break; }

            if (documents == null) yield break;

            foreach (Document document in documents)
            {
                if (document == null) continue;

                bool linked;
                try { linked = document.IsLinked; }
                catch { linked = false; }

                if (linked && !includeLinked) continue;

                yield return document;
            }
        }

        public static Document ActiveOf(UIApplication application)
        {
            try { return application?.ActiveUIDocument?.Document; }
            catch { return null; }
        }

        public static bool SameAs(Document a, Document b)
        {
            if (a == null || b == null) return false;

            var keyA = KeyOf(a);
            var keyB = KeyOf(b);

            return keyA != null && string.Equals(keyA, keyB, StringComparison.OrdinalIgnoreCase);
        }
    }
}
