using fyserver.Services;

namespace fyserver;

public static class Command
{
    private static readonly TextWriter OriginalOut = Console.Out;
    private static readonly TextWriter NullWriter = TextWriter.Null;

    /// <summary>启动控制台命令循环。服务经参数注入，避免全局静态状态。</summary>
    public static void StartCommandLoop(UserStoreService users, StoreConfigService storeConfig, MatchManagerService matches)
    {
        Console.WriteLine("服务器已完整启动，按下C进入命令模式");

        // 非交互模式（无控制台/输入被重定向，如后台或作为服务运行）：保持进程存活直到被外部终止。
        if (Console.IsInputRedirected)
        {
            Console.WriteLine("非交互模式：跳过命令循环");
            Thread.Sleep(Timeout.Infinite);
            return;
        }

        while (true)
        {
            if (Console.ReadKey().Key == ConsoleKey.C)
            {
                while (true)
                {
                    TempWriteLine("\n请输入命令：");
                    string command = Console.ReadLine()?.Trim().ToLower() ?? "";
                    switch (command)
                    {
                        case "reloadstore":
                            storeConfig.Reload();
                            TempWriteLine("商店配置已重新加载。");
                            break;
                        case "cm":
                            matches.MatchedPairs.Clear();
                            TempWriteLine("清空。");
                            break;
                        case "savedbfo":
                            if (File.Exists("./YCDR"))
                            {
                                users.RecordIncremental();
                                TempWriteLine("已增量保存。");
                            }
                            else
                            {
                                TempWriteLine("未曾全量保存过, 请调用savedbss进行一次全量保存");
                            }
                            break;
                        case "savedbss":
                            users.RecordFull();
                            TempWriteLine("已全量保存。");
                            break;
                        case "clearusers":
                            users.ClearAll();
                            TempWriteLine("所有玩家记录已清除；后台、商店和内容配置保留。");
                            break;
                        case "help":
                            TempWriteLine("可用命令：reloadstore, clearusers, help, savedbfo, savedbss, exit, exitall");
                            break;
                        case "exitall":
                            TempWriteLine("行");
                            return;
                        case "exit":
                            TempWriteLine("行");
                            goto resume;
                        default:
                            TempWriteLine("未知命令。输入 'help' 获取可用命令列表。");
                            break;
                    }
                }
            resume:
                Console.SetOut(OriginalOut);
            }
        }
    }

    public static void TempWriteLine(string message)
    {
        Console.SetOut(OriginalOut);
        Console.WriteLine(message);
        Console.SetOut(NullWriter);
    }
}
