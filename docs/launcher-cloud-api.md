# 启动器云端接口（房间大厅 / 进房票据 / 打洞与中继 / 好友聊天 / 云端整合包）

云端 fyserver 是“启动器服务器”：它管理启动器账号、好友、聊天和小助手，还负责房间大厅、整合包清单和打洞协调。
真正的游戏服务端是房主电脑上的嵌入版 fyserver。云端不转发游戏数据，只有打洞失败时由 frps 中继，并且限速。

所有接口都在 `/launcher` 下，请求体和响应都是 JSON，字段名用 camelCase。
需要登录的接口都带 `Authorization: Bearer <token>`，token 来自 `POST /launcher/login`。WebSocket 也可以用 `?token=` 传 token。
失败时返回 `{"ok":false,"message":"…"}` 和对应的 HTTP 状态码。

## 1. 控制通道 WebSocket

`GET /launcher/ws?token=<token>`（WebSocket）

- 每个账号只保留一条连接。新连接会顶掉旧连接，旧连接先收到 `{"type":"replaced"}` 再被关闭。
- 客户端可以随时发 `{"type":"ping"}`，服务端回 `{"type":"pong","at":…}`。建议每 25 秒发一次。
- 单条消息最大 64 KB。

**服务端推送：**

| type | 内容 | 时机 |
|---|---|---|
| `hello` | `accountId, username, onlineFriends:[id], unreadTotal, roomId` | 连接建立后 |
| `presence` | `accountId, online, roomId` | 好友上下线、进出房间 |
| `friends_changed` | `reason: request_received/accepted/removed, from:{accountId,username}` | 好友关系变化，收到后重新拉 `GET /launcher/friends` |
| `chat` | `message:{id,from,to,text,at,read}` | 收到私聊、小助手回复或后台消息 |
| `chat_ack` | `clientId, ok, message, error` | 回执自己通过 WS 发出的 chat |
| `room_invite` | `roomId, roomName, hasPassword, from:{accountId,username}` | 好友邀请你进房 |
| `room_updated` | `room`（同 §2 的房间对象） | 房间成员或设置变化（发给房间里每个人） |
| `room_closed` | `roomId, reason` | 房主关房、房主超时、管理员关闭、房主开了新房 |
| `kicked` | `roomId, message` | 你被房主移出 |
| `replaced` | `message` | 同账号在别处连上 |

**客户端可以发：**

- `{"type":"chat","to":<accountId>,"text":"…","clientId":"本地临时 id"}`：发私聊，`to=0` 是小助手。服务端回 `chat_ack`。
- `{"type":"read","with":<accountId>}`：把和这个人的对话标为已读。

## 2. 房间

一个房间最多 10 人，房主也算在内。房间只存在内存里，云端重启后房主的启动器要重新开房（拿到新的房间号）。

**房间对象：**

```json
{
  "id": "8MJ257", "name": "…", "description": "…",
  "host": {"id": 1, "username": "alice"},
  "visibility": "public | password | friends", "hasPassword": false,
  "maxPlayers": 4, "playerCount": 2,
  "members": [{"id": 1, "username": "alice", "role": "host", "joinedAt": "…"}],
  "mods": [ … ], "modpackId": "mp_… 或 null", "gameVersion": "…",
  "createdAt": "…", "joined": true
}
```

`mods` 由房主上报，原样保存（最多 200 项，64 KB），建议每项是 `{id, version, sha256, category}`。
加入者自己比对 `category=gameplay` 的模组。

| 方法 | 路径 | 说明 |
|---|---|---|
| GET | `/launcher/rooms/pubkey` | 票据公钥 `{alg:"ES256", kid, spki(base64 DER), pem, ticketLifetimeSeconds}`，不需要登录 |
| GET | `/launcher/rooms?q=` | 大厅列表：公开房、密码房，以及好友开的仅好友房。每项多一个 `hostIsFriend` |
| GET | `/launcher/rooms/mine` | 我当前所在的房间 `{room}`，可能是 null |
| POST | `/launcher/rooms` | 开房，请求体 `{name, description?, visibility?, password?, maxPlayers?(2–10), mods?, modpackId?, gameVersion?}`。一个账号同时只在一个房间里：开新房会先关掉自己原来的房，或者退出原来加入的房 |
| GET | `/launcher/rooms/{id}` | 房间详情。仅好友房对非好友返回 404 |
| PUT | `/launcher/rooms/{id}` | 房主修改，字段同开房，只传要改的字段 |
| POST | `/launcher/rooms/{id}/join` | 进房，请求体 `{password?}` |
| POST | `/launcher/rooms/{id}/heartbeat` | 心跳，同时续期票据 |
| POST | `/launcher/rooms/{id}/leave` | 离开。房主离开就是关房 |
| POST | `/launcher/rooms/{id}/kick` | 房主踢人，请求体 `{accountId}`。被踢的人不能再进这个房间 |
| POST | `/launcher/rooms/{id}/invite` | 邀请在线好友，请求体 `{accountId}`，对方收到 `room_invite` |
| DELETE | `/launcher/rooms/{id}` | 房主关房 |

开房、进房、心跳都返回同一个结构：

```json
{
  "ok": true, "message": "…", "room": { … }, "role": "host | member",
  "ticket": "<JWS>", "ticketExpiresAt": "…",
  "tunnel": {"role", "serverAddr", "serverPort", "p2pProxy": "room_<ID>_p2p", "relayProxy": "room_<ID>_relay", "sk"}
}
```

隧道没启用时 `tunnel` 是 null，这时只能本机或局域网联机。

**超时规则：**

- 房主 45 秒没心跳，房间自动关闭。
- 成员 90 秒没心跳，会被移出房间。
- 建议每 15 秒发一次心跳。

## 3. 进房票据

票据格式是 ES256 JWS compact，`header.payload.signature`，三段都用 base64url 编码。

- header：`{"alg":"ES256","typ":"JWT","kid":"<公钥 id>"}`
- payload：`{"rid":"房间号","aid":启动器账号 id,"name":"用户名","role":"host|member","iat":秒,"exp":秒}`
- 签名：对 `header.payload` 做 ECDSA P-256 / SHA-256 签名，格式是 IEEE P1363（r‖s，64 字节）。
- 有效期 15 分钟，每次心跳都会返回一张新票据。
- 签名私钥只在云端，文件是 `./data/room-signing-key.pem`，要随 data 目录一起备份。私钥丢了，旧票据全部失效。

嵌入版服务端用 `--ticket-pubkey <spki base64>` 拿到公钥，校验客户端在 `X-Fy-Ticket` 头里带来的票据。
校验内容：签名有效、`rid` 等于本房间、`exp` 没过期。玩家身份以 `aid` / `name` 为准。

Node 校验示例：

```js
const [h, p, s] = ticket.split('.');
const key = crypto.createPublicKey({ key: Buffer.from(spki, 'base64'), format: 'der', type: 'spki' });
crypto.verify('sha256', Buffer.from(h + '.' + p), { key, dsaEncoding: 'ieee-p1363' }, Buffer.from(s, 'base64url'));
```

## 4. 打洞与中继（frp）

做法是先用 xtcp 打洞直连，失败就回落到 stcp 中继。

- 云端机器上运行 frps，并配置 fyserver 的 HTTP 插件。
- 后台“启动器房间”页可以开关隧道，设置 frps 公网地址、端口和中继限速，并给出 frps.toml 示例。
- 插件密钥 `pluginKey` 自动生成，保存在 `./data/launcher-tunnel.json`。

**frps.toml（和 fyserver 在同一台机器上）：**

```toml
bindPort = 7000

[[httpPlugins]]
name = "fyserver-rooms"
addr = "127.0.0.1:<fyserver 端口>"
path = "/launcher/frp/plugin?key=<pluginKey>"
ops = ["Login", "NewProxy", "NewUserConn"]
```

**插件规则：**

- 插件接口只接受本机回环地址的调用，请求不能带 `X-Forwarded-For` 或 `Forwarded` 头，查询串里的 key 必须正确。
- `Login`：`metadatas.ticket` 必须是有效票据，而且账号此刻还在这个房间里。这里允许票据已过期，因为 frpc 断线重连时会沿用登录时的票据。被踢或退房后就登不上了。
- `NewProxy`：
  - 只有房主能注册代理，而且只能注册 `room_<ID>_p2p`（xtcp）和 `room_<ID>_relay`（stcp）这两个。
  - 两个代理的 `secretKey` 都必须等于房间的 `sk`。
  - stcp 代理会被强制加上服务端限速，`bandwidth_limit` 取后台的设置值，`bandwidth_limit_mode` 为 `server`。
- `NewUserConn` 一律拒绝，也就是不允许 tcp/http 这类公开代理。

frpc 不要设置 `user`，因为设置后代理名会被加上前缀，插件就对不上了。

**房主的 frpc.toml（两个代理都指向本机嵌入版服务端）：**

```toml
serverAddr = "<tunnel.serverAddr>"
serverPort = <tunnel.serverPort>
metadatas.ticket = "<房主票据>"

[[proxies]]
name = "room_<ID>_p2p"
type = "xtcp"
secretKey = "<tunnel.sk>"
localIP = "127.0.0.1"
localPort = 11450

[[proxies]]
name = "room_<ID>_relay"
type = "stcp"
secretKey = "<tunnel.sk>"
localIP = "127.0.0.1"
localPort = 11450
```

**加入者的 frpc.toml（本地网关把游戏流量转到 bindPort）：**

```toml
serverAddr = "<tunnel.serverAddr>"
serverPort = <tunnel.serverPort>
metadatas.ticket = "<成员票据>"

[[visitors]]
name = "room_<ID>_relay_visitor"
type = "stcp"
serverName = "room_<ID>_relay"
secretKey = "<tunnel.sk>"
bindAddr = "127.0.0.1"
bindPort = -1

[[visitors]]
name = "room_<ID>_p2p_visitor"
type = "xtcp"
serverName = "room_<ID>_p2p"
secretKey = "<tunnel.sk>"
bindAddr = "127.0.0.1"
bindPort = 11451
keepTunnelOpen = false
fallbackTo = "room_<ID>_relay_visitor"
fallbackTimeoutMs = 1500
```

`sk` 是每个房间随机生成的，只通过开房、进房、心跳的响应下发，启动器不要把它写进日志。

## 5. 好友与聊天（按启动器账号）

启动器的好友、聊天和游戏内的好友、聊天是两套独立的数据。启动器这边存在 `fy_launcher_friendships` / `fy_launcher_chat_messages` 两张表；用本地存储时存在 `./data/social/launcher.json`。
小助手的账号 id 是 0，永远排在好友列表第一个。

| 方法 | 路径 | 说明 |
|---|---|---|
| GET | `/launcher/friends` | `{friends:[{accountId, username, status: accepted/request_sent/request_received, online, roomId, unread, since, assistant?}]}` |
| POST | `/launcher/friends` | 请求体 `{username}` 或 `{accountId}`。对方已经申请过你时，这一步就是同意 |
| DELETE | `/launcher/friends/{id}` | 删除好友，或者拒绝、撤回好友申请 |
| GET | `/launcher/chat/{id}?before=<消息id>&limit=<1–200，默认 50>` | 聊天记录，按时间正序返回 `{messages, hasMore}` |
| POST | `/launcher/chat/{id}` | 请求体 `{text}`，返回 `{message}`。发给小助手时还会多一个 `reply` |
| POST | `/launcher/chat/{id}/read` | 标为已读 |

**限制：**

- 好友上限、单条消息长度、记录保留天数都沿用后台“好友与小助手”页的设置。
- 每个账号 10 秒内最多发 8 条消息。

**小助手指令：** `/帮助`、`/在线`、`/房间`。后台“启动器房间”页可以以小助手身份给某个账号发消息，也可以群发给所有在线账号。

## 6. 云端整合包

云端只保存整合包的清单，不保存任何文件。

| 方法 | 路径 | 说明 |
|---|---|---|
| GET | `/launcher/modpacks?mine=1&q=` | 我的整合包，加上所有人公开的整合包 |
| GET | `/launcher/modpacks/{id}` | 详情。私有的整合包只有作者能看；如果它是某个房间的 `modpackId`，房间成员也能看 |
| POST | `/launcher/modpacks` | 请求体 `{name, description?, visibility: public/private, mods:[{id, version?, sha256?, enabled?}]}` |
| PUT | `/launcher/modpacks/{id}` | 只传要改的字段 |
| DELETE | `/launcher/modpacks/{id}` | 删除 |

**规则：**

- `mods` 的顺序就是加载顺序。
- 只允许已经在模组索引里发布的模组，本地模组不能上传。
- `version` 和 `sha256` 没填时，自动记下当前已发布的版本。
- 每个账号最多 50 个整合包，每个整合包最多 200 个模组。

## 7. 后台接口（/admin/api/launcher-cloud）

| 方法 | 路径 | 权限 |
|---|---|---|
| GET | `/overview`：在线数、房间列表、好友统计、整合包列表 | players |
| POST | `/rooms/{id}/close` | players |
| POST | `/message`：`{accountId}` 或 `{all:true}`，加上 `message` | players |
| DELETE | `/modpacks/{id}` | players |
| GET / PUT | `/tunnel`：`{enabled, serverAddr, serverPort, relayBandwidthLimit}`，返回内容包含 pluginKey 和 frps 示例 | serverConfig |

## 8. 数据文件

| 文件 | 内容 |
|---|---|
| `data/room-signing-key.pem` | 票据签名私钥（PKCS8），只有本机可读 |
| `data/launcher-tunnel.json` | 隧道设置和 frps 插件密钥 |
| `data/launcher-modpacks.json` | 云端整合包清单 |
| `data/social/launcher.json` | 本地存储模式下的启动器好友和聊天（数据库模式下用 `fy_launcher_*` 表） |
