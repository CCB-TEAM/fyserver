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

- HTTP 与 WebSocket 共用同一端口（默认 `5231`，即 `portHttp`），WebSocket 直接向 HTTP 根路径发起升级请求；可在 `setting.json` 中修改（`portHttp` / `ip` / `bancheat` / `adminApiKey`），不存在时会自动生成。
- 后台位于同一 HTTP 端口的 `/admin-ui/`。首次启动需在服务器本机创建管理员账户，之后本机和远程访问都必须登录；`adminApiKey` 仅作为脚本调用 `/admin/api/*` 的兼容认证方式。
- 启动后控制台按 `C` 进入命令模式：`savedbss`（全量保存）、`savedbfo`（增量保存）、`reloadstore`（重载商店配置）、`clearusers`（清空用户）、`cm`（清空对局）、`exitall`（退出）
- 后台/无控制台环境下自动进入非交互模式，保持进程存活

# NativeAOT 发布

```bash
dotnet publish fyserver.csproj -c Release -r win-x64 --self-contained true
```

产物位于 `bin/Release/net10.0/win-x64/publish/`：`fyserver.exe`（约 20MB 原生可执行文件）+ `setting.json` + `config/` + `library/` + `wwwroot/`（静态后台页面），拷到目标机直接运行即可，无需安装 .NET 运行时。

**后台与 AOT 不冲突**：后台是纯静态 HTML/JS（`wwwroot/admin-ui`）+ JSON 接口（`/admin/api`），不含 Razor/MVC 运行时反射，因此 AOT 产物同样带完整后台。


# 目录结构

```
fyserver/
├── Program.cs                  # Host 引导：配置 → DI 注册 → 单 host（HTTP + WS 同端口）启动
├── Endpoints/                  # minimal API 分组（User/Player/Deck/Lobby/Match/AdminApi…）
├── Services/                   # 服务层（UserStore/FasterKv/MatchManager/WebSocketHub/Codec/Auth…）
├── Models/                     # DTO 与实体
├── wwwroot/admin-ui/           # 静态后台页面（/admin-ui，纯 HTML/JS，Material 主题）
├── Middleware/                 # 路径归一化、Content-Type 清理
├── wwwroot/admin-assets/       # 后台静态资源（自包含 CSS/JS，无 CDN 依赖）
└── Serialization/              # System.Text.Json 源生成上下文
```

# 已实现的 HTTP API

以下路径均监听 `setting.json` 的 `portHttp`。请求路径中的连续斜杠会自动归一化，因此 `/fp/`、`//fp` 等价。

## 会话与配置

| 方法 | 路径 | 说明 |
|---|---|---|
| `POST` | `/session` | 登录或自动创建用户；封禁用户返回 `403` |
| `GET` | `/` | 获取客户端配置与 API 地址 |
| `GET` | `/config` | 获取客户端基础配置 |
| `GET` | `/fp/` | 获取首页/公告配置 |

## 玩家、物品与商店

| 方法 | 路径 | 说明 |
|---|---|---|
| `GET` | `/store/` | 获取商店数据（旧客户端兼容） |
| `GET` | `/store/v2/` | 获取商店数据 |
| `POST` | `/store/txn` | 商店交易占位接口（旧客户端兼容） |
| `POST` | `/store/v2/txn` | 商店交易占位接口 |
| `GET` | `/entitlements/{id}` | 获取玩家权益 |
| `GET` | `/{a}/players/{player_id}/friends` | 获取好友与历史对手 |
| `PUT` / `DELETE` | `/players/{id}/heartbeat` | 玩家心跳 |
| `PUT` / `DELETE` | `/players/notifications/{id}` | 玩家通知状态 |
| `GET` | `/players/{id}/library` | 获取玩家卡牌库 |
| `GET` | `/players/{id}/librarynew` | 获取玩家卡牌库（新客户端兼容路径） |
| `GET` | `/items/{id}` | 获取玩家物品与装备 |
| `POST` | `/items/{id}` | 装备物品 |
| `GET` | `/items/decks/{id}` | 预制卡组详情占位接口 |
| `PUT` | `/crate/claim` | 领取军需箱奖励 |

## 卡组

| 方法 | 路径 | 说明 |
|---|---|---|
| `POST` | `/players/{id}/decks` | 创建卡组 |
| `PUT` | `/players/{player_id}/decks/{deck_id}` | 更新卡组编码/内容 |
| `PUT` | `/players/{player_id}/decks/` | 重命名、更换卡背或设为收藏 |
| `DELETE` | `/players/{player_id}/decks/{deck_id}` | 删除卡组 |

## 匹配与对局

| 方法 | 路径 | 说明 |
|---|---|---|
| `POST` | `/lobbyplayers` | 加入多人/战斗码匹配 |
| `POST` | `/singleplayerlobby` | 加入单人匹配 |
| `DELETE` | `/lobbyplayers` | 退出匹配队列 |
| `GET` | `/matches/v2` | 获取当前对局与起始数据 |
| `GET` | `/matches/v2/reconnect` | 获取断线重连数据 |
| `GET` | `/matches/v2/{id}` | 获取对局运行状态 |
| `PUT` | `/matches/v2/{id}/` | 提交对局状态动作 |
| `POST` / `PUT` | `/matches/v2/{id}/actions` | 获取或提交编码后的对局动作 |
| `POST` | `/matches/v2/{id}/mulligan` | 提交调度换牌 |
| `GET` | `/matches/v2/{id}/mulligan/{location}` | 获取指定侧调度结果 |
| `GET` | `/matches/v2/{id}/post` | 结算并离开对局 |

## 管理接口

> 管理接口已内置保护：`adminApiKey` 留空时仅允许 `127.0.0.1`/loopback 访问；配置 `adminApiKey` 后必须携带 `X-Admin-Key` 请求头或登录后的会话 Cookie。仍建议不要将管理接口直接暴露到公网。

| 方法 | 路径 | 说明 |
|---|---|---|
| `GET` | `/admin/api/session` | 当前会话状态（是否已授权 / 本机 / 是否已配置密钥） |
| `POST` | `/admin/api/setup` | 首次启动时在服务器本机创建管理员账户 |
| `POST` | `/admin/api/login` | 用管理员用户名、密码换取会话 Cookie |
| `POST` | `/admin/api/logout` | 退出登录（清除会话 Cookie） |
| `GET` | `/admin/api/stats` | 概览：用户/在线/对局/队列统计与运行信息 |
| `GET` | `/admin/api/users` | 用户列表（支持 `?q=` 搜索） |
| `GET` | `/admin/api/users/{id}` | 用户详情（含卡组与进行中对局） |
| `POST` | `/admin/api/users/{id}/ban` | 封禁用户并断开其 WebSocket |
| `POST` | `/admin/api/users/{id}/unban` | 解除封禁 |
| `POST` | `/admin/api/users/{id}/kick?reason=...` | 踢出在线用户（不封禁） |
| `DELETE` | `/admin/api/users/{id}` | 删除用户并断开其 WebSocket |
| `GET` | `/admin/api/matches` | 进行中的真人对局与各匹配队列 |
| `POST` | `/admin/api/matches/{id}/remove` | 强制移除一条对局 |
| `POST` | `/admin/api/queues/clear` | 清空全部匹配队列 |
| `POST` | `/admin/api/store/reload` | 热重载 `config/store.json` |
| `GET` | `/admin/api/content/{frontpage\|skirmish\|knockout}` | 内容条目列表 |
| `GET` | `/admin/api/content/{kind}/{id}` | 读取单条（含完整 JSON） |
| `POST` | `/admin/api/content/{kind}` | 新增条目（body: `name` / `startDate` / `endDate` / `raw`） |
| `POST` | `/admin/api/content/{kind}/{id}` | 更新条目 |
| `DELETE` | `/admin/api/content/{kind}/{id}` | 删除条目 |
# 后台管理

纯静态页面（`wwwroot/admin-ui/`）+ JSON 接口（`/admin/api/*`），自包含 Material 主题（`wwwroot/admin-ui/assets/admin.css`，无外部 CDN 依赖），**不依赖 Razor/MVC**，因此 AOT 与裁剪发布都能带后台。

入口：直接访问 `/admin-ui/` 即可（会 302 到 `index.html`；`/admin-ui/login` 同理）。注：旧的 Razor 后台地址 `/admin/*` 已随 Razor 移除而失效（404）。

鉴权：第一次启动时，控制台会打印初始化地址；必须从服务器本机在 `/admin-ui/login.html` 创建管理员用户名和密码。密码以 PBKDF2-SHA256 派生哈希保存于 `data/admin-auth.json`，不会保存明文。初始化后本机与远程均须登录，会话使用 HttpOnly、SameSite=Strict 的 7 天签名 Cookie。`setting.json` 的 `adminApiKey` 仍可通过 `X-Admin-Key` 用于自动化脚本，但不用于浏览器登录。

| 页面 | 说明 |
|---|---|
| `/admin-ui/`（等价 `/admin-ui/index.html`） | 概览：端口与地址、在线连接数、用户与封禁数、匹配队列明细、重载商店配置 |
| `/admin-ui/users.html` | 用户管理：搜索（ID / 用户名 / 昵称）、封禁 / 解封 / 踢下线 / 删除、用户详情与卡组 |
| `/admin-ui/matches.html` | 对局与匹配：进行中的真人对局、各队列等待玩家、移除对局 / 清空队列 |
| `/admin-ui/content.html` | 内容配置：首页公告（**带游戏内 SVG 实时预览**）、乱斗、淘汰赛，JSON 编辑 + 校验 + `.bak` 备份 |
| `/admin-ui/login`（等价 `/admin-ui/login.html`） | 首次创建管理员 / 管理员账户登录 |

内容配置落盘：`config/frontpage.json`、`config/skirmish.json`、`config/knockout.json`，结构统一为
`{"entries":[{id,name,start_date,end_date,…}]}`；frontpage 兼容客户端既有的 `elements`/`targeted` 与 camelCase `elementId`，
编辑页未建模的字段原样保留。预览按游戏客户端画布尺寸渲染（轮播 1540×770 / 侧栏按钮 614×307 / 弹窗 1232×564）。

后台操作复用与游戏 API 相同的服务层（`AdminUserService`）：封禁、踢出、删除都会向目标玩家的 WebSocket 发送 `channel: "disconnect"` 后关闭连接。
# WebSocket

WebSocket 与 HTTP 共用 `setting.json` 的 `portHttp` 端口：任意路径上的 WebSocket 升级请求都由同一管线处理（客户端配置中的 `websocketurl` 为 `ws://<ip>:<portHttp>/`，登录响应里由服务端下发）。支持 `ping`、`touchcard`、`emoji`、`notification` 通道；服务器主动踢出或封禁时发送 `channel: "disconnect"`，随后以 `PolicyViolation` 关闭连接。

# 相关项目

- [FyClient](https://github.com/CCB-TEAM/FyClient) —— 虚拟测试客户端，覆盖登录/卡组/匹配/对局/封禁全流程，用于对服务器做端到端验证
