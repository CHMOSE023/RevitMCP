using System;
using Autodesk.Revit.DB;
using RevitMCP.Addin.Compat;

namespace RevitMCP.Addin.Tools
{
    /// <summary>
    /// 构件在协议里怎么被指代。
    ///
    /// 两种写法都接受：
    /// · <b>ElementId</b>（"225318"）——短、读着顺，但**只在这一个文档的这一次会话里有效**。
    ///   用户切换文档、或者关掉 Revit 再打开，同一个数字会指向完全不同的东西，
    ///   甚至指向不存在的构件。
    /// · <b>UniqueId</b>（"c0326e0e-…-0000d2d5"）——Revit 自己维护的 GUID，
    ///   跨会话、跨文档稳定，导出 IFC 之后也能对应回来。
    ///
    /// 之所以两种都收：短 ID 在一次会话内部用着方便，而任何要跨会话留存的东西
    /// （审计报告里的问题清单、交付物里的构件索引）必须用 UniqueId，
    /// 否则隔一天拿出来就是一串指向错误构件的数字——**而且它看起来完全正常**。
    ///
    /// 全项目所有接受构件 ID 的地方都走这里，所以两种写法在哪儿都能用。
    /// </summary>
    internal static class ElementRef
    {
        /// <summary>
        /// 解析一个构件引用。找不到或格式不对时返回 null，并通过
        /// <paramref name="problem"/> 给出**能直接照着改**的原因。
        /// </summary>
        public static Element Resolve(Document document, string raw, out string problem)
        {
            problem = null;

            if (string.IsNullOrWhiteSpace(raw))
            {
                problem = "构件 ID 不能为空。可以给 ElementId（如 \"225318\"）" +
                          "或 UniqueId（如 \"c0326e0e-473d-4952-b8ec-f23696541f41-0000d2d5\"）。";
                return null;
            }

            var text = raw.Trim();

            // 纯数字一律当 ElementId。UniqueId 里必有连字符，两者不会混淆
            ElementId elementId;
            if (ElementIdCompat.TryParse(text, out elementId))
            {
                var byId = document.GetElement(elementId);

                if (byId == null)
                    problem = "这个文档里不存在 ID 为 " + text + " 的构件。" +
                              "ElementId 只在单个文档的单次会话内有效——" +
                              "如果这个 ID 是从别的文档、或者上一次打开 Revit 时取得的，它在这里不成立。" +
                              "重新查一次，或改用回执里的 uniqueId（那个跨会话稳定）。";

                return byId;
            }

            if (LooksLikeUniqueId(text))
            {
                Element byUniqueId;
                try { byUniqueId = document.GetElement(text); }
                catch { byUniqueId = null; }

                if (byUniqueId == null)
                    problem = "这个文档里不存在 UniqueId 为 " + text + " 的构件。" +
                              "UniqueId 跨会话稳定，但**不跨文档**——" +
                              "它属于哪个模型，就只在哪个模型里有效。";

                return byUniqueId;
            }

            problem = "构件 ID \"" + raw + "\" 的格式无法识别。" +
                      "可以给 ElementId（十进制整数，如 \"225318\"）" +
                      "或 UniqueId（GUID 形式，如 \"c0326e0e-473d-4952-b8ec-f23696541f41-0000d2d5\"）。";
            return null;
        }

        /// <summary>
        /// 这串东西看着像不像 UniqueId。
        ///
        /// Revit 的 UniqueId 是「GUID + 连字符 + 8 位十六进制」，
        /// 但不同版本、不同来源（链接模型里的构件）格式略有出入，
        /// 所以只做一个宽松判断：够长、带连字符。真假交给 Revit 去认——
        /// 在这里写一个严格的正则，只会在某天拒掉一个本来有效的 ID。
        /// </summary>
        private static bool LooksLikeUniqueId(string text)
        {
            return text.Length >= 36 && text.IndexOf('-') > 0;
        }

        /// <summary>
        /// 构件的 UniqueId。取不到时返回 null——
        /// 极少数内部元素没有 UniqueId，不值得为此让整个查询失败。
        /// </summary>
        public static string UniqueIdOf(Element element)
        {
            try
            {
                var id = element?.UniqueId;
                return string.IsNullOrEmpty(id) ? null : id;
            }
            catch { return null; }
        }
    }
}
