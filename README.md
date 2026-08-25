# fyserver
A simple game server written in C# using .NET 10.0.
>**核心声明：非盈利性**
本项目完全免费，且严禁用于任何商业或营利性目的。其设计初衷仅为个人学习、技术研究与娱乐交流，不涉及任何形式的收费、商业化运营或经济利益往来。

>**使用与责任须知**
使用者需自行承担因使用本项目而产生的一切风险与后果。作者不对任何直接或间接的损失、数据问题或法律纠纷负责。

>**法律合规要求**
用户应确保其使用行为完全符合所在地法律法规。任何违反法律或平台政策的行为，责任由用户自行承担。若本项目涉及侵权或违规，相关平台有权对其下架处理。

>**支持与能力限制**
作者不提供任何形式的技术支持、维护或盈利性指导。使用者应具备必要的技术能力，并独立解决遇到的问题。

>**版权声明**
本项目一切著作权和最终解释权归 1939 Games 所有。
本项目的任何使用、复制、修改、分发或衍生作品的创建，必须遵守上述声明，并且不得以任何形式用于商业目的。
严禁将本项目用于任何商业用途，包括但不限于销售、租赁、授权、分发或以任何形式盈利。任何违反此声明的行为将被视为侵权，并可能导致法律诉讼。
本项目严禁用于任何形式的游戏私服搭建，部署，运营或相关活动。任何违反此声明的行为将被视为侵权，本项目作者概不负责。

# 当前功能
- [x] 基础框架搭建
- [x] 解密对战
- [x] 登录
- [x] 名字
- [x] 卡组
- [x] FP
- [x] 自定义Setting
- [x] 商店
- [x] 调度
- [x] 全牌
- [x] 部分物件（无头像，桌饰）
# 暂未实现功能：
- [ ] 排行榜
- [ ] 成就系统
- [ ] 好友系统
- [ ] 军需箱
- [ ] 清理对局
- [ ] 更多物件
- [ ] 兑换码
- [ ] 乱斗模式 （comming soon）
- [ ] 佩戴
- [ ] 抽卡
- [ ] 世锦赛
- [ ] jjc
- [ ] 商店购买
- [ ] 特色玩法
- [ ] 人机
- [ ] 战区
- [ ] 更多功能
- [ ] 更多物件 （comming soon）
- [ ] 预制卡组
- [ ] 商店盈利 （永远不会实现！）

---

# 技术栈

- .NET 10 / ASP.NET Core（minimal API）
- FASTER（`Microsoft.FASTER.Core`）—— 用户数据持久化（`user:username:*` / `user:id:*` 双索引）
- System.Text.Json **全量源生成**（`FyJsonContext` / `ConfigJsonContext` / `StoreJsonContext`），零反射，NativeAOT 兼容
- 消息编解码为**纯 C# 实现**（Base64 + XOR、查表密钥），无任何原生 DLL 依赖

# 快速开始

```bash
dotnet build FYServer.sln
dotnet run --project fyserver.csproj
```

- HTTP 端口默认 `5231`、WebSocket 端口默认 `5232`，可在 `setting.json` 中修改（`portHttp` / `portWs` / `ip` / `bancheat`），不存在时会自动生成
- 启动后控制台按 `C` 进入命令模式：`savedbss`（全量保存）、`savedbfo`（增量保存）、`reloadstore`（重载商店配置）、`clearusers`（清空用户）、`cm`（清空对局）、`exitall`（退出）
- 后台/无控制台环境下自动进入非交互模式，保持进程存活

# NativeAOT 发布

```bash
dotnet publish fyserver.csproj -c Release -r win-x64 --self-contained true
```

产物位于 `bin/Release/net10.0/win-x64/publish/`：`fyserver.exe`（约 20MB 原生可执行文件）+ `setting.json` + `config/` + `library/`，拷到目标机直接运行即可，无需安装 .NET 运行时。

# 目录结构

```
fyserver/
├── Program.cs                  # Host 引导：配置 → DI 注册 → HTTP/WS 双 host 启动
├── Endpoints/                  # minimal API 分组（User/Player/Deck/Lobby/Match/Admin）
├── Services/                   # 服务层（UserStore/FasterKv/MatchManager/WebSocketHub/Codec/Auth…）
├── Models/                     # DTO 与实体
├── Middleware/                 # 路径归一化、Content-Type 清理
└── Serialization/              # System.Text.Json 源生成上下文
```

# 相关项目

- [FyClient](https://github.com/CCB-TEAM/FyClient) —— 虚拟测试客户端，覆盖登录/卡组/匹配/对局全流程，用于对服务器做端到端验证