using System.Reflection.Emit;

namespace fyserver
{
    public static class Command
    {
        static readonly TextWriter originalOut = Console.Out;
        static readonly TextWriter nullWriter = TextWriter.Null;
        public static void StartCommandLoop()
        {
            Console.WriteLine("服务器已完整启动，按下C进入命令模式");
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
                                GlobalState.ReloadStoreConfig();
                                TempWriteLine("商店配置已重新加载。");
                                break;
                                /*
                            case "reloadmsng":
                                GlobalState.ReloadMiniSitNGoConfig();
                                TempWriteLine("乱斗配置已重新加载。");
                                break;
                                */
                            case "cm":
                               config.appconfig.MatchedPairs.Clear();
                                TempWriteLine("清空。");
                                break;
                            case "savedbfo":
                                if (File.Exists("./YCDR"))
                                {
                                    GlobalState.users.Record1();
                                    TempWriteLine("已增量保存。");
                                }
                                else{
                                    TempWriteLine("未曾全量保存过, 请调用savedbss进行一次全量保存");
                                }
                                    break;
                            case "savedbss":
                                GlobalState.users.Record2();
                                TempWriteLine("已全量保存。");
                                break;
                            case "clearusers":
                                if(File.Exists("./YCDR"))
                                    File.Delete("./YCDR");
                                GlobalState.users._db.Clear();
                                TempWriteLine("所有用户数据已清除。");
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
                    Console.SetOut(originalOut);
                }
            }
        }
        public static void TempWriteLine(string message)
        {
            Console.SetOut(originalOut);
            Console.WriteLine(message);
            Console.SetOut(nullWriter);
        }
    }
}
