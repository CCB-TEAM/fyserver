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
- [x] 商店购买（金币/钻石、购买次数与卡包入库）
- [x] 调度
- [x] 全牌
- [x] 部分物件（无头像，桌饰）
- [x] 静态管理后台（多账号、权限、审计与配置管理）
# 暂未实现功能：
- [ ] 排行榜
- [ ] 成就系统
- [ ] 好友系统
- [ ] 军需箱
- [x] 清理对局（管理后台）
- [ ] 更多物件
- [x] 兑换码
- [ ] 乱斗模式 （comming soon）
- [x] 佩戴
- [ ] 抽卡
- [ ] 世锦赛
- [ ] jjc
- [ ] 特色玩法
- [x] 人机（基础单人匹配）
- [ ] 战区
- [ ] 更多功能
- [ ] 更多物件 （comming soon）
- [ ] 预制卡组
- [ ] 商店盈利 （永远不会实现！）

---

# 技术栈

- .NET 10 / ASP.NET Core（minimal API）
- Vue 3 + Vite 8（后台界面；ASP.NET Core 托管构建后的静态资源）
- FASTER（`Microsoft.FASTER.Core`）—— 用户数据持久化（`user:username:*` / `user:id:*` 双索引）
- System.Text.Json **全量源生成**（`FyJsonContext` / `ConfigJsonContext` / `StoreJsonContext`），零反射，NativeAOT 兼容
- 消息编解码为**纯 C# 实现**（Base64 + XOR、查表密钥），无任何原生 DLL 依赖

# 快速开始

```bash
dotnet build FYServer.slnx
dotnet run --project fyserver.csproj
```

`dotnet build` 和 `dotnet publish` 会先在 `AdminUi/` 执行 Vue/Vite 构建，再将生成文件作为 ASP.NET 静态资源复制到输出目录。首次构建需要 Node.js 和 npm，依赖由 `npm ci` 安装。若只修改后台页面，可在 `AdminUi/` 中运行 `npm run build`；Vue 组件和页面逻辑位于 `AdminUi/src/`，`wwwroot/admin-ui/` 是生成结果。前端开发服务器可用 `npm run dev` 启动，默认监听 `127.0.0.1:5173`，并将 `/admin/api` 代理至仓库当前使用的 `127.0.0.1:1145`。如果后端端口不同，可设置 `FYSERVER_DEV_ORIGIN`，例如 `http://127.0.0.1:5231`。

- HTTP 与 WebSocket 共用同一端口（默认 `5231`，即 `portHttp`），WebSocket 直接向 HTTP 根路径发起升级请求；可在 `setting.json` 中修改（`portHttp` / `ip` / `bancheat` / `adminApiKey`），不存在时会自动生成。
- 后台位于同一 HTTP 端口的 `/admin-ui/`。首次启动需在服务器本机创建 Owner 账户，之后本机和远程访问都必须登录；后台接口不再接受 `adminApiKey` 作为账号权限的替代凭据。
- 启动后控制台按 `C` 进入命令模式：`savedbss`（全量保存）、`savedbfo`（增量保存）、`reloadstore`（重载商店配置）、`clearusers`（清空用户）、`cm`（清空对局）、`exitall`（退出）
- 后台/无控制台环境下自动进入非交互模式，保持进程存活

# NativeAOT 发布

```bash
dotnet publish fyserver.csproj -c Release -r win-x64 --self-contained true
```

产物位于 `bin/Release/net10.0/win-x64/publish/`：`fyserver.exe`（约 24 MB 原生可执行文件）+ `setting.json` + `config/` + `library/` + `wwwroot/`（静态后台页面），拷到目标机直接运行即可，无需安装 .NET 运行时。原生 PDB 仅用于调试，不是运行必需文件。

**后台与 AOT 不冲突**：Vite 生成的 HTML/CSS/JS 位于 `wwwroot/admin-ui`，ASP.NET Core 托管这些静态产物与 `/admin/api` JSON 接口；没有 Razor/MVC 运行时反射，因此 AOT 产物同样带完整后台。


# 目录结构

```
fyserver/
├── Program.cs                  # Host 引导：配置 → DI 注册 → 单 host（HTTP + WS 同端口）启动
├── Endpoints/                  # minimal API 分组（User/Player/Deck/Lobby/Match/AdminApi…）
├── Services/                   # 服务层（UserStore/FasterKv/MatchManager/WebSocketHub/Codec/Auth…）
├── Models/                     # DTO 与实体
├── Middleware/                 # 路径归一化、Content-Type 清理
├── Serialization/              # System.Text.Json 源生成上下文
├── AdminUi/                    # Vite 后台源码、页面入口与 npm 依赖
└── wwwroot/admin-ui/           # Vite 生成的静态页面与资源，由 ASP.NET 托管
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
| `POST` | `/store/txn` | 使用金币或钻石购买商店商品（旧客户端兼容） |
| `POST` | `/store/v2/txn` | 使用金币或钻石购买商店商品 |
| `GET` | `/players/{id}/resources` | 获取玩家金币、钻石和尘 |
| `GET` | `/players/{id}/packs` | 获取玩家卡包 |
| `PUT` | `/players/{id}` | 修改玩家公开昵称 |
| `POST` | `/players/{id}/friends` | 兼容旧客户端的首次命名请求 |
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
| `PUT` | `/matches/v2/replay` | 按 `match_id` 与 `pov_side` 载入已保存对局回放 |
| `GET` | `/matches/v2/{id}` | 获取对局运行状态 |
| `PUT` | `/matches/v2/{id}/` | 提交对局状态动作 |
| `POST` / `PUT` | `/matches/v2/{id}/actions` | 获取或提交编码后的对局动作 |
| `POST` | `/matches/v2/{id}/mulligan` | 提交调度换牌 |
| `GET` | `/matches/v2/{id}/mulligan/{location}` | 获取指定侧调度结果 |
| `GET` | `/matches/v2/{id}/post` | 结算并离开对局 |

## 管理接口

> 管理接口已内置账号权限保护：首次设置仅允许从本机完成，之后所有 `/admin/api/*` 写操作都需要对应后台账号权限和登录 Cookie；`X-Admin-Key` 不再绕过权限。仍建议不要将管理接口直接暴露到公网。

| 方法 | 路径 | 说明 |
|---|---|---|
| `GET` | `/admin/api/session` | 当前会话状态（是否已授权 / 本机 / 是否已配置密钥） |
| `POST` | `/admin/api/setup` | 首次启动时在服务器本机创建管理员账户 |
| `POST` | `/admin/api/login` | 用管理员用户名、密码换取会话 Cookie |
| `POST` | `/admin/api/logout` | 退出登录（清除会话 Cookie） |
| `GET` | `/admin/api/accounts` | 后台账号列表及在线状态 |
| `GET` | `/admin/api/accounts/{id}` | 后台账号详情 |
| `GET` | `/admin/api/accounts/{id}/actions` | 后台账号操作历史 |
| `GET` | `/admin/api/accounts/{id}/logins` | 后台账号登录与 IP 历史 |
| `POST` | `/admin/api/accounts` | 创建后台账号并分配权限 |
| `PUT` / `DELETE` | `/admin/api/accounts/{id}` | 更新或删除后台账号 |
| `POST` | `/admin/api/accounts/{id}/reset-password` | 重置后台账号密码 |
| `GET` | `/admin/api/audit-logs` | 读取最近的后台操作审计 |
| `GET` | `/admin/api/server-config` | 读取客户端 `/session` 的 `server_options` 模板 |
| `PUT` | `/admin/api/server-config` | 校验并保存模板，自动备份 `.bak`，下次客户端登录生效 |
| `PUT` | `/admin/api/server-config/item` | 新增或更新单项配置及自定义注释 |
| `DELETE` | `/admin/api/server-config/item/{key}` | 删除单项配置（`websocketurl` 除外） |
| `PUT` | `/admin/api/server-config/item/{key}/enabled` | 设置单项发送开关；关闭不删除配置值 |
| `GET` | `/admin/api/system-settings` | 读取监听及对外地址的当前运行值和已保存值 |
| `PUT` | `/admin/api/system-settings` | 校验并保存网络地址至 `setting.json`，备份 `.bak`，重启后生效 |
| `GET` | `/admin/api/stats` | 概览：用户/在线/对局/队列统计与运行信息 |
| `GET` | `/admin/api/users` | 用户列表（支持 `?q=` 搜索） |
| `GET` | `/admin/api/users/{id}` | 用户详情（含卡组与进行中对局） |
| `PUT` | `/admin/api/users/{id}/profile` | 修改用户公开资料 |
| `PUT` | `/admin/api/users/{id}/wallet` | 修改用户金币、钻石和尘 |
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
| `GET` | `/admin/api/content/{kind}/export` | 导出原始配置 JSON |
| `POST` | `/admin/api/content/{kind}/import` | 导入并校验完整 JSON，备份旧文件 |
| `PUT` | `/admin/api/content/frontpage/{id}/published` | 快捷发布或下线首页内容 |
# 对局观战与回放

对局起始信息、已接受的动作和结束结果会写入配置的玩家数据库。MySQL/PostgreSQL 模式使用 `fy_match_history` 与 `fy_match_events` 表；本地 FASTER 模式使用 `data/match-history/` 下的原子 JSON 文档。PostgreSQL 模式启动或首次配置数据库时会自动创建表和索引。对局 ID 会同时避开仍在运行的对局和已归档历史 ID。

对局 ID 为随机六位数字（100000–999998），创建时检查运行中对局和已持久化历史记录以避免复用；它不是递增序号。后台“对局监控”页只显示实时对局与匹配队列；“对局管理”页可按状态、玩家名称或 ID 筛选和分页浏览持久化对局，查看开局快照及分页动作，并删除单条已结束/已中止记录。FASTER 本地玩家库模式同样支持观战与回放：实时观战从运行时对局读取，回放快照/动作从 `data/match-history/` 读取。

| 方法 | 路径 | 说明 |
|---|---|---|
| `GET` | `/admin/api/matches/history/storage` | 持久化记录数、动作数与数据体积 |
| `GET` | `/admin/api/matches/history?page=1&pageSize=25&status=all&playerId=...` | 后台分页查询持久化对局 |
| `GET` | `/admin/api/matches/history/{id}` | 对局摘要与完整开局快照 |
| `GET` | `/admin/api/matches/history/{id}/actions?afterActionId=0&limit=250` | 按游标读取已持久化动作 |
| `DELETE` | `/admin/api/matches/history/{id}` | 删除一条历史记录及其全部动作（需要对局管理权限） |

只读数据接口默认开放，供游戏端观战及未来官网使用：

| 方法 | 路径 | 说明 |
|---|---|---|
| `GET` | `/spectate/matches` | 进行中的对局摘要 |
| `GET` | `/spectate/matches/{id}` | 观战起始快照 |
| `GET` | `/spectate/matches/{id}/actions?afterActionId=0&limit=500` | 实时动作游标分页 |
| `GET` | `/replays?limit=50&playerId=...` | 最近已结束对局，可按玩家筛选 |
| `GET` | `/replays/{id}` | 已结束对局摘要与起始快照 |
| `GET` | `/replays/{id}/actions?afterActionId=0&limit=500` | 回放动作游标分页 |

普通对局动作轮询现在按请求游标读取，不再清空共享动作列表；对局操作接口也会检查玩家是否为本局参赛者。系统设置中的“对局历史保留”可选按时间（默认保留 30 天）或按数量，并设置每周 UTC 清理时间；仅清理已结束/中止对局，相关事件通过外键级联删除。官网应在 Next.js 服务端使用只读 PostgreSQL 账号或读取 fyserver 只读接口，不能让浏览器直连数据库。该数据表为官网的公开只读视图，记录包含双方牌组/动作数据。

# 后台管理

后台由 Vue 3 + Vite 多页面入口构建，产物在 `wwwroot/admin-ui/`；ASP.NET Core 托管静态文件并提供 `/admin/api/*` JSON 接口。页面外壳和组件在 `AdminUi/src/`，已有页面的业务控制器在 `AdminUi/src/legacy/` 逐步迁入响应式组件。样式和脚本自包含，无外部 CDN 依赖，**不依赖 Razor/MVC**，因此 AOT 与裁剪发布都能带后台。

入口：直接访问 `/admin-ui/` 即可（会 302 到 `index.html`；`/admin-ui/login` 同理）。注：旧的 Razor 后台地址 `/admin/*` 已随 Razor 移除而失效（404）。

鉴权：第一次启动时，控制台会打印初始化地址；必须从服务器本机在 `/admin-ui/login.html` 创建 Owner 账户。旧版单管理员账户会自动迁移为 Owner，并保留 `.bak` 备份。密码以 PBKDF2-SHA256 派生哈希保存于 `data/admin-auth.json`，不会保存明文。初始化后本机与远程均须登录；会话使用 HttpOnly、SameSite=Strict 的 7 天签名 Cookie。Owner 可在“后台用户”中创建账号及配置玩家、内容、对局、服务器配置、系统设置和权限管理权限。新账号默认只读；后台写操作记录到 `data/admin-audit.jsonl`，登录成功和失败记录到 `data/admin-logins.jsonl`。后台用户列表按最近两分钟有效会话活动显示在线状态；账号详情、操作历史、登录与 IP 历史分别位于独立页面。`setting.json` 的 `adminApiKey` 仅为旧配置字段，后台接口不再使用它。

| 页面 | 说明 |
|---|---|
| `/admin-ui/`（等价 `/admin-ui/index.html`） | 概览：端口与地址、在线连接数、用户与封禁数、匹配队列明细、重载商店配置 |
| `/admin-ui/users.html` | 用户管理：搜索（ID / 用户名 / 昵称）、封禁 / 解封 / 踢下线 / 删除、用户详情与卡组；详情显示登录 IP、设备及近期对局 |
| `/admin-ui/matches.html` | 对局与匹配：进行中的真人对局、各队列等待玩家、移除对局 / 清空队列 |
| `/admin-ui/content.html` | 内容配置：首页公告（**带游戏内 SVG 实时预览**）、乱斗、淘汰赛，JSON 编辑 + 校验 + `.bak` 备份 |
| `/admin-ui/server-config.html` | 管理客户端 `server_options`，支持镜像注释、配置项筛选、左侧编辑/删除、新增和逐项启用/关闭（关闭不删除值） |
| `/admin-ui/server-config-edit.html` | 独立的服务器配置新增 / 编辑页，按 `string`、`int`、`double`、`json` 校验并保存；镜像项和自定义项都可编辑注释 |
| `/admin-ui/system-settings.html` | 系统设置：监听 IP / 端口、客户端对外 IP / 域名及端口；显示待重启差异 |
| `/admin-ui/accounts.html` | 后台账号管理：创建账号、在线状态和权限概览 |
| `/admin-ui/account-detail.html` | 后台账号详情、权限、启停及密码管理 |
| `/admin-ui/account-actions.html` | 指定后台账号的操作审计历史 |
| `/admin-ui/account-logins.html` | 指定后台账号的登录与 IP 历史 |
| `/admin-ui/login`（等价 `/admin-ui/login.html`） | 首次创建管理员 / 管理员账户登录 |

服务器配置值保存在 `config/serverOptions.json`，发送开关保存在 `config/serverOptions.flags.json`，自定义注释保存在 `config/serverOptions.comments.json`。镜像原始说明由 `config/serverOptions.schema.json` 提供；自定义注释只影响后台展示，不会进入游戏客户端的 `server_options`。镜像页面截断的六项默认值以禁用的占位值保留，填入完整值后才能启用。

宿主网络设置独立保存在 `setting.json`：`listenIp`/`portHttp` 控制实际监听，`ip`/`publicPortHttp` 控制返回给客户端的 HTTP 与 WebSocket 地址。默认监听 `0.0.0.0`，默认对外 IP 为 `127.0.0.1`，对外端口沿用 `portHttp`。后台保存不会中断当前连接，重启后生效；若要让其他设备连接，应在后台把对外 IP 改为该设备可访问的服务器地址，并按需配置端口映射、防火墙或反向代理。

内容配置落盘：`config/frontpage.json` 保持客户端原生的 `elements`/`targeted` 和 camelCase 字段；`config/skirmish.json`、`config/knockout.json` 使用 `entries` 数组。后台支持导入/导出、日历、状态筛选、定时发布和快捷发布开关；frontpage 的常用字段可通过表单编辑，图片可填写图床 URL 或上传 PNG/JPEG/WebP/GIF（每张最多 5 MB，存于 `wwwroot/admin-ui/uploads/`）。乱斗表单参考镜像后台，支持多语言说明、奖励、基础规则、黑名单、随机牌组、卡牌数量限制与主要回合/部署效果。完整 JSON 编辑仍保留，未被表单修改的字段原样保留，保存前备份 `.bak`。`/fp/` 仅下发生效且已发布的普通条目。镜像的定向规则引擎尚未接入，因此定向条目虽可编辑保存，但不会下发给玩家。首页预览按游戏客户端画布尺寸渲染（轮播 1540×770 / 侧栏按钮 614×307 / 弹窗 1232×564）。

后台操作复用与游戏 API 相同的服务层（`AdminUserService`）：封禁、踢出、删除都会向目标玩家的 WebSocket 发送 `channel: "disconnect"` 后关闭连接。
# WebSocket

WebSocket 与 HTTP 共用 `setting.json` 的 `portHttp` 端口：任意路径上的 WebSocket 升级请求都由同一管线处理（客户端配置中的 `websocketurl` 为 `ws://<ip>:<portHttp>/`，登录响应里由服务端下发）。支持 `ping`、`touchcard`、`emoji`、`notification` 通道；服务器主动踢出或封禁时发送 `channel: "disconnect"`，随后以 `PolicyViolation` 关闭连接。

# 相关项目

- [FyClient](https://github.com/CCB-TEAM/FyClient) —— 虚拟测试客户端，覆盖登录/卡组/匹配/对局/封禁全流程，用于对服务器做端到端验证
