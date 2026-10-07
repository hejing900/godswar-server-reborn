using System.Runtime.InteropServices;

namespace Godswar.LootTool;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        // Applied for both paths so a launcher script can point the tool at
        // another database or client, self test included.
        LootToolSettings.ApplyOverrides(
            ReadOption(args, "--connection-string"),
            ReadOption(args, "--client-root"));

        if (args.Any(static arg =>
                arg.Equals("--selftest", StringComparison.OrdinalIgnoreCase)))
        {
            // A WinExe has no console of its own; borrow the parent's so the
            // report is visible when the tool is launched from a terminal.
            AttachConsole(-1);
            return SelfTest.RunAsync().GetAwaiter().GetResult();
        }

        if (args.Any(static arg =>
                arg.Equals("--pet-check", StringComparison.OrdinalIgnoreCase)))
        {
            var publish = Array.FindIndex(args, static arg =>
                    arg.Equals("--pet-check", StringComparison.OrdinalIgnoreCase)) switch
            {
                var index when index >= 0 && index + 1 < args.Length =>
                    args[index + 1].Equals("publish", StringComparison.OrdinalIgnoreCase),
                _ => false
            };
            AttachConsole(-1);
            return PetCheck.RunAsync(publish).GetAwaiter().GetResult();
        }

        if (args.Any(static arg =>
                arg.Equals("--npc-patch", StringComparison.OrdinalIgnoreCase)))
        {
            AttachConsole(-1);
            var root = ReadOption(args, "--npc-patch") ??
                       ReadOption(args, "--client-root") ??
                       LootToolSettings.Load().ClientRoot;
            return RunNpcPatchAsync(root).GetAwaiter().GetResult();
        }

        if (args.Any(static arg =>
                arg.Equals("--help", StringComparison.OrdinalIgnoreCase) ||
                arg.Equals("-h", StringComparison.OrdinalIgnoreCase)))
        {
            AttachConsole(-1);
            Console.WriteLine("Godswar GM 工具（角色与标记点 + 刷怪 + 导出/导入 + NPC 对话 + 农场 + 掉落表 + 任务奖励 + 宠物档位）");
            Console.WriteLine("  （无参数）                              打开图形界面");
            Console.WriteLine("  --selftest                              只跑各页数据层自检（含补丁生成），不开窗口");
            Console.WriteLine("  --npc-patch \"D:\\Godswar Origin\"         把库里配好的多级 NPC 对话生成成客户端补丁（改前备份）");
            Console.WriteLine("  --pet-check                             只跑宠物数据层只读检查，不开窗口");
            Console.WriteLine("  --connection-string \"Host=...;Database=...\"  覆盖数据库连接串并记住");
            Console.WriteLine("  --client-root \"D:\\Godswar Origin\"        覆盖客户端目录并记住");
            return 0;
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
        return 0;
    }

    /// <summary>
    /// 无界面地把库里配好的多级 NPC 对话生成成客户端补丁。
    /// </summary>
    /// <remarks>
    /// 只读库、只写客户端目录下的三个文件（改前自动备份）。没有启用中的对话时，
    /// 仍然会写一份语法完整的空实现，这样已经打过补丁的客户端不会调用到不存在的函数。
    /// </remarks>
    private static async Task<int> RunNpcPatchAsync(string clientRoot)
    {
        var settings = LootToolSettings.Load();
        Console.WriteLine($"客户端目录：{clientRoot}");
        await using var store = new NpcDialogueStore();
        try
        {
            store.Connect(settings.BuildConnectionString());
            if (!await store.HasSchemaAsync())
            {
                Console.WriteLine($"[失败] {NpcDialogueStore.MissingSchemaMessage(store.DatabaseName)}");
                return 1;
            }

            var trees = await store.LoadTreesAsync();
            var enabled = trees.Count(static tree => tree.Dialogue.Enabled);
            Console.WriteLine($"库里对话 {trees.Count} 棵（启用 {enabled} 棵）。");
            var report = ClientNpcPatch.Apply(clientRoot, trees);
            Console.WriteLine(report.Summary);
            Console.WriteLine(report.BackupSummary);
            Console.WriteLine($"生成的文件：{report.LuaPath}");
            return 0;
        }
        catch (Exception error)
        {
            Console.WriteLine($"[失败] {error.Message}");
            return 1;
        }
    }

    private static string? ReadOption(string[] args, string name)
    {
        for (var index = 0; index < args.Length - 1; index++)
        {
            if (args[index].Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return args[index + 1];
            }
        }

        return null;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int processId);
}
