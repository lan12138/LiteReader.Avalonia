// LiteReader · Avalonia 跨平台版 —— 语言模式识别
// 作者：hujie  创建：2026-09-18
//
// 为什么需要它（对照 LiteReader.cpp 的 Lang 枚举 + detectLang）：
//   有两处行为必须按语言区分，否则会出错或明显变差：
//   1) 括号匹配 / 彩虹括号：SQL 的 BEGIN / END / CASE 是「块括号」，要参与嵌套深度与配对高亮；
//      而在 C# 里 `case` 只是普通关键字（我们的关键字表大小写敏感，所以只有全大写 CASE 会命中）。
//   2) 代码补全的候选来源：在 .sql 文件里提示 C# 关键字纯属噪音，反之亦然。
//
// 为什么不做完整的「逐语言关键字表 + 逐语言着色」：
//   着色用的关键字集保持合并（所有语言并成一张表）。合并集对判色已经足够，
//   而拆成逐语言表意味着要维护 6~8 份词表、并在 ScanLine 里按语言分支 ——
//   改动面和回归风险都不小，收益只有「个别同形词的判色更准」。权衡下来不值。
//   （LiteReader.cpp 是逐语言表，但那是从零写就如此，不是从合并集改过去的。）
//
// 识别方式：只看文件扩展名。不做内容嗅探 —— 内容嗅探的准确率提升有限，
//   却要在每次载入时多扫一遍文件，且行为难以解释（用户改了扩展名会困惑）。

namespace LiteReader.Models;

public enum LanguageMode
{
    /// <summary>无语言（纯文本 / 日志 / 配置）：不做块括号，补全只给文档内的函数名。</summary>
    Plain,

    /// <summary>C 族：C / C++ / C# / Java / JS / TS / Go / Rust / CSS / JSON …</summary>
    CLike,

    /// <summary>SQL：BEGIN / END / CASE 当块括号。</summary>
    Sql,

    /// <summary>Python：三引号块字符串已在着色器里处理；这里主要影响补全词表。</summary>
    Python,

    /// <summary>标记语言：HTML / XML / XAML / ASPX。标签已由着色器处理，暂不给关键字补全。</summary>
    Markup,
}

public static class LanguageDetect
{
    /// <summary>按扩展名判定。未知扩展名一律 Plain —— 宁可少给功能，也不要给错的。</summary>
    public static LanguageMode FromPath(string? path)
    {
        if (string.IsNullOrEmpty(path)) return LanguageMode.Plain;

        // 只看扩展名，不碰文件系统（DocumentModel.FilePath 在 Save 之前可能还不存在）
        string ext = Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".sql" or ".ddl" or ".dml" or ".pks" or ".pkb" => LanguageMode.Sql,
            ".py" or ".pyw" or ".pyi" => LanguageMode.Python,

            ".html" or ".htm" or ".xhtml" or ".xml" or ".xaml" or ".axaml" or ".aspx"
                or ".ascx" or ".cshtml" or ".vbhtml" or ".svg" or ".xsd" or ".xsl" or ".config"
                or ".csproj" or ".props" or ".targets" or ".resx" or ".plist" => LanguageMode.Markup,

            ".cs" or ".c" or ".h" or ".cpp" or ".cc" or ".cxx" or ".hpp" or ".hh" or ".hxx"
                or ".java" or ".kt" or ".kts" or ".js" or ".mjs" or ".cjs" or ".jsx"
                or ".ts" or ".tsx" or ".go" or ".rs" or ".css" or ".scss" or ".less"
                or ".php" or ".swift" or ".m" or ".mm" or ".json" or ".jsonc" => LanguageMode.CLike,

            _ => LanguageMode.Plain,
        };
    }

    public static string DisplayName(LanguageMode mode) => mode switch
    {
        LanguageMode.Sql => "SQL",
        LanguageMode.Python => "Python",
        LanguageMode.Markup => "XML",
        LanguageMode.CLike => "C 族",
        _ => "纯文本",
    };
}
