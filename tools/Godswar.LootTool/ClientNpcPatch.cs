using System.Text;

namespace Godswar.LootTool;

/// <summary>一次生成客户端补丁的结果。</summary>
internal sealed record NpcPatchReport(
    string LuaPath,
    bool LuaChanged,
    string DispatchPath,
    bool DispatchChanged,
    string LoadPath,
    bool LoadChanged,
    IReadOnlyList<string> Backups,
    int Dialogues,
    int Pages,
    int Buttons)
{
    public string Summary =>
        $"对话 {Dialogues} 棵｜页 {Pages} 个｜按钮 {Buttons} 个｜" +
        $"Lua {(LuaChanged ? "已更新" : "无变化")}、" +
        $"分派 {(DispatchChanged ? "已插入" : "已是最新")}、" +
        $"加载表 {(LoadChanged ? "已登记" : "已登记")}";

    public string BackupSummary => Backups.Count == 0
        ? "（没有改动，未产生备份）"
        : "备份：" + string.Join("、", Backups.Select(Path.GetFileName));
}

/// <summary>
/// 把工具里配好的多级 NPC 对话生成成**客户端补丁**。
/// </summary>
/// <remarks>
/// <para>
/// 为什么必须打补丁：客户端渲染对话文字用的是它自己的 Lua
/// （<c>Localization/en_us/UI/XML/NpcFun/NpcFun.lua</c> 的 <c>Set_NpcFun_Text</c>），
/// 它是一个按功能号分派的 <c>if/elseif</c> 链，**没有 else**，服务端只发数字，
/// 发一个没人处理的号就什么都不显示。所以"自由文本"只能在客户端落地。
/// </para>
/// <para>
/// 补丁做三件事，全部幂等、改前先备份：
/// <list type="number">
/// <item>写 <c>NpcFun/NpcFunGM.lua</c>：一个 <c>NpcFunGMTOOL_SetText</c>，
/// 按 <c>Index</c>(页) / <c>SubID</c>(条目) 用**字面字符串**调 <c>SetText</c>。</item>
/// <item>在 <c>NpcFun/NpcFun.lua</c> 的 <c>Set_NpcFun_Text</c> 顶部插入一个
/// 认领功能号 85 的分支（原链一个字都不动）。</item>
/// <item>在 <c>NpcFunLoad.xml</c> 末尾登记新脚本（只登记一次）。</item>
/// </list>
/// </para>
/// <para>
/// 补丁依赖的、已经对着真实文件核实过的约定：
/// <c>Set_NpcFun_Text(Type,Index,BtnID,SubID)</c> 里 <c>Type</c> 是功能号、
/// <c>Index</c> 是页号、<c>SubID</c> 是条目号；正文条目的 SubID 沿用客户端既有的
/// <c>101</c>（见 <c>NpcFunFallback.lua</c> 的 <c>{1,101,TEXT,...}</c>）；按钮画在
/// <c>FirstWin_Button&lt;BtnID&gt;</c> 上，一页最多 12 个；终点页必须自己调
/// <c>NPCFUN:EndMessage(true)</c>，而且**不能和按钮同页**（否则窗口一闪而过）。
/// </para>
/// <para>
/// <b>尚未核实</b>（已写进文档，不在代码里假装知道）：引擎给 <c>BtnID</c> 赋值的方式，
/// 以及点击按钮时回传给服务端的到底是槽位还是别的值。补丁按"一页内 SubID = 槽位 =
/// 位置"生成，并对每个按钮显式 <c>SetPosition</c>（客户端默认会把所有按钮叠在
/// (25,135)）。这一条需要在游戏里点一次才能定论。
/// </para>
/// </remarks>
internal static class ClientNpcPatch
{
    /// <summary>认领的功能号：客户端常量表用 0..120，85/86 既没定义也没被任何已发布 NPC 用过。</summary>
    public const int FunctionFlag = NpcDialogueStore.DefaultFunctionFlag;

    /// <summary>生成的窗口名，决定客户端那两个全局函数叫 <c>NpcFunGMTOOL_*</c>。</summary>
    public const string WindowName = "GMTOOL";

    public const string GeneratedLuaFileName = "NpcFunGM.lua";

    private const string BeginMarker = "-- >>> GM 工具生成：重跑会整段覆盖，勿手工编辑";
    private const string EndMarker = "-- <<< GM 工具生成结束";

    /// <summary>客户端默认按钮位置；不显式摆位的话 12 个按钮会全叠在这里。</summary>
    private const int FirstButtonX = 25;

    private const int FirstButtonY = 135;

    private const int ButtonSpacing = 20;

    public static string RelativeLuaPath =>
        $"./Localization/en_us/UI/XML/NpcFun/{GeneratedLuaFileName}";

    public static string DispatchRelativePath =>
        Path.Combine("Localization", "en_us", "UI", "XML", "NpcFun", "NpcFun.lua");

    public static string LoadRelativePath =>
        Path.Combine("Localization", "en_us", "UI", "XML", "NpcFunLoad.xml");

    /// <summary>生成 <c>NpcFunGM.lua</c> 的全部内容。</summary>
    public static string GenerateLua(IReadOnlyList<GmNpcTree> trees)
    {
        ArgumentNullException.ThrowIfNull(trees);
        var enabled = trees.Where(static tree => tree.Dialogue.Enabled)
            .OrderBy(static tree => tree.Dialogue.DialogueKey, StringComparer.Ordinal)
            .ToList();

        // 页号就是客户端认的 Index，所以必须全局唯一：两棵树都用第 1 页的话，
        // 后来那棵会覆盖前一棵的窗口内容。
        var duplicated = enabled
            .SelectMany(tree => tree.Pages.Select(page => (tree.Dialogue.DialogueKey, page.PageIndex)))
            .GroupBy(static row => row.PageIndex)
            .Where(static group => group.Count() > 1)
            .Select(static group => group.Key)
            .OrderBy(static page => page)
            .ToList();
        if (duplicated.Count > 0)
        {
            throw new InvalidOperationException(
                $"页号必须全局唯一（客户端只按 Index 找内容），第 " +
                $"{string.Join("、", duplicated)} 页被多棵对话用了。" +
                "给每棵对话分一段自己的页号区间（例如 1001-1099、1101-1199）。");
        }

        var builder = new StringBuilder();
        builder.AppendLine("-- 由 Godswar GM 工具（tools\\Godswar.LootTool）生成");
        builder.AppendLine($"-- 生成时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}｜功能号 Type = {FunctionFlag}");
        builder.AppendLine("-- 这个文件每次生成都会被整段覆盖：手工改动会在下次生成时丢失。");
        builder.AppendLine(BeginMarker);
        builder.AppendLine();
        builder.AppendLine($"NPC_FLAG_SYS_GMTOOL = {FunctionFlag}");
        builder.AppendLine();
        builder.AppendLine("local win = UIAPI:GetElement(\"FirstWin\")");
        builder.AppendLine("local textlis = {}");
        builder.AppendLine();
        builder.AppendLine("function NpcFunGMTOOL_SetUI(Type,Index)");
        builder.AppendLine("\tFirstWin_ButtonA1:Visible(true)");
        builder.AppendLine("\tFirstWin_ButtonA2:Visible(true)");
        builder.AppendLine("\twin:Visible(true)");
        builder.AppendLine("end");
        builder.AppendLine();
        builder.AppendLine("function NpcFunGMTOOL_SetText(Type,Index,BtnID,SubID)");

        var firstPage = true;
        foreach (var tree in enabled)
        {
            var buttons = tree.Buttons.ToLookup(static button => button.PageIndex);
            foreach (var page in tree.Pages.OrderBy(static page => page.PageIndex))
            {
                var pageButtons = buttons[page.PageIndex]
                    .OrderBy(static button => button.Slot)
                    .ToList();
                var keyword = firstPage ? "\tif" : "\telseif";
                firstPage = false;
                builder.AppendLine($"{keyword} Index == {page.PageIndex} then  -- " +
                                   $"{tree.Dialogue.DialogueKey}｜{tree.Dialogue.DisplayName}");
                builder.AppendLine($"\t\tif SubID == {NpcDialogueStore.BodySubId(page.PageIndex)} then");
                builder.AppendLine($"\t\t\tFirstWin_Text1:SetText({LuaString(page.BodyText)})");
                builder.AppendLine("\t\t\tFirstWin_Text1:Visible(true)");
                if (pageButtons.Count == 0)
                {
                    // 没有按钮 = 终点页：必须自己收尾，否则窗口不会关。
                    builder.AppendLine("\t\t\tNPCFUN:EndMessage(true)");
                    builder.AppendLine("\t\t\tNPCFUN:NeedMessage(false)");
                }

                foreach (var button in pageButtons)
                {
                    builder.AppendLine(
                        $"\t\telseif SubID == {NpcDialogueStore.ButtonSubId(page.PageIndex, button.Slot)} then");
                    builder.AppendLine("\t\t\tlocal Button = win:GetChild(\"FirstWin_Button\" .. BtnID)");
                    builder.AppendLine($"\t\t\tButton:SetText({LuaString(button.Label)})");
                    builder.AppendLine("\t\t\tButton:Visible(true)");
                    builder.AppendLine(
                        $"\t\t\tButton:SetPosition({FirstButtonX}," +
                        $"{FirstButtonY + (ButtonSpacing * (button.Slot - 1))})");
                }

                builder.AppendLine("\t\tend");
            }
        }

        if (firstPage)
        {
            // 一棵可用对话都没有：仍然要生成一个语法完整的空实现，
            // 否则补丁会让 Set_NpcFun_Text 调用到一个不存在的函数。
            builder.AppendLine("\tif false then");
        }

        // 页之间是 elseif 链，所以整条链只在最后关一次；每页内部那个 SubID 的 if
        // 才是各自关各自的。
        builder.AppendLine("\tend");
        builder.AppendLine("end");
        builder.AppendLine(EndMarker);
        return builder.ToString();
    }

    /// <summary>把补丁落到客户端目录；返回改了哪些文件、备份在哪。</summary>
    public static NpcPatchReport Apply(
        string clientRoot,
        IReadOnlyList<GmNpcTree> trees,
        bool makeBackups = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientRoot);
        if (!Directory.Exists(clientRoot))
        {
            throw new InvalidOperationException($"客户端目录不存在：{clientRoot}");
        }

        var luaPath = Path.Combine(
            clientRoot,
            "Localization", "en_us", "UI", "XML", "NpcFun", GeneratedLuaFileName);
        var dispatchPath = Path.Combine(clientRoot, DispatchRelativePath);
        var loadPath = Path.Combine(clientRoot, LoadRelativePath);
        foreach (var path in (string[])[dispatchPath, loadPath])
        {
            if (!File.Exists(path))
            {
                throw new InvalidOperationException(
                    $"客户端里找不到 {Path.GetFileName(path)}（{path}），" +
                    "客户端目录选错了，或者这不是这个版本的客户端。");
            }
        }

        var backups = new List<string>();
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var lua = GenerateLua(trees);

        var luaChanged = WriteIfChanged(luaPath, lua, encoding: new UTF8Encoding(false));
        var dispatchChanged = PatchDispatch(
            dispatchPath,
            stamp,
            makeBackups,
            backups);
        var loadChanged = PatchLoadFile(loadPath, stamp, makeBackups, backups);

        var enabled = trees.Count(static tree => tree.Dialogue.Enabled);
        return new NpcPatchReport(
            luaPath,
            luaChanged,
            dispatchPath,
            dispatchChanged,
            loadPath,
            loadChanged,
            backups,
            enabled,
            trees.Where(static tree => tree.Dialogue.Enabled).Sum(static tree => tree.Pages.Count),
            trees.Where(static tree => tree.Dialogue.Enabled).Sum(static tree => tree.Buttons.Count));
    }

    /// <summary>在 <c>Set_NpcFun_Text</c> 顶部插入 GM 分支；已存在就不动。</summary>
    private static bool PatchDispatch(
        string path,
        string stamp,
        bool makeBackups,
        List<string> backups)
    {
        var text = File.ReadAllText(path);
        if (text.Contains("NPC_FLAG_SYS_GMTOOL", StringComparison.Ordinal))
        {
            return false;
        }

        const string anchor = "function Set_NpcFun_Text(Type,Index,BtnID,SubID)";
        var index = text.IndexOf(anchor, StringComparison.Ordinal);
        if (index < 0)
        {
            throw new InvalidOperationException(
                "在 NpcFun.lua 里找不到 Set_NpcFun_Text 的定义，" +
                "客户端版本对不上，补丁没有应用。");
        }

        var insertAt = text.IndexOf('\n', index);
        if (insertAt < 0)
        {
            throw new InvalidOperationException("Set_NpcFun_Text 的定义被截断了。");
        }

        var branch = new StringBuilder();
        branch.Append('\n');
        branch.Append($"\t-- GM 工具生成（{stamp}）：只加这一个分支，原来的分派链一个字都没动\n");
        branch.Append("\tif Type == NPC_FLAG_SYS_GMTOOL then\n");
        branch.Append("\t\tNpcFunGMTOOL_SetText(Type,Index,BtnID,SubID)\n");
        branch.Append("\t\treturn\n");
        branch.Append("\tend\n");

        if (makeBackups)
        {
            backups.Add(WriteBackup(path, stamp));
        }

        File.WriteAllText(
            path,
            text.Insert(insertAt + 1, branch.ToString()),
            new UTF8Encoding(false));
        return true;
    }

    /// <summary>在 <c>NpcFunLoad.xml</c> 的 <c>&lt;/UIConfig&gt;</c> 前登记新脚本；已存在就不动。</summary>
    private static bool PatchLoadFile(
        string path,
        string stamp,
        bool makeBackups,
        List<string> backups)
    {
        var text = File.ReadAllText(path);
        if (text.Contains(GeneratedLuaFileName, StringComparison.Ordinal))
        {
            return false;
        }

        var index = text.LastIndexOf("</UIConfig>", StringComparison.Ordinal);
        if (index < 0)
        {
            throw new InvalidOperationException("NpcFunLoad.xml 里没有 </UIConfig>，补丁没有应用。");
        }

        if (makeBackups)
        {
            backups.Add(WriteBackup(path, stamp));
        }

        var line = $"  <!-- GM 工具生成（{stamp}） -->\r\n" +
                   $"  <Script File=\"{RelativeLuaPath}\" />\r\n";
        File.WriteAllText(
            path,
            text.Insert(index, line),
            new UTF8Encoding(false));
        return true;
    }

    private static bool WriteIfChanged(string path, string content, Encoding encoding)
    {
        if (File.Exists(path) &&
            string.Equals(File.ReadAllText(path), content, StringComparison.Ordinal))
        {
            return false;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content, encoding);
        return true;
    }

    private static string WriteBackup(string path, string stamp)
    {
        var backup = $"{path}.gm-backup-{stamp}";
        File.Copy(path, backup, overwrite: true);
        return backup;
    }

    /// <summary>
    /// 把操作者写的文字变成合法的 Lua 双引号字符串。
    /// </summary>
    /// <remarks>
    /// 转义 <c>\ " 换行 回车</c>；其余字符（含中文）原样保留，因为客户端读的是 UTF-8。
    /// 换行转成 <c>\n</c>，Lua 的字符串转义认得。
    /// </remarks>
    private static string LuaString(string? text)
    {
        var value = text ?? string.Empty;
        var builder = new StringBuilder(value.Length + 2);
        builder.Append('"');
        foreach (var character in value)
        {
            switch (character)
            {
                case '\\':
                    builder.Append("\\\\");
                    break;
                case '"':
                    builder.Append("\\\"");
                    break;
                case '\n':
                    builder.Append("\\n");
                    break;
                case '\r':
                    break;
                default:
                    builder.Append(character);
                    break;
            }
        }

        builder.Append('"');
        return builder.ToString();
    }
}
