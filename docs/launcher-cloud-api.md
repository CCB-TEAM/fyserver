# 启动器云端接口（房间大厅 / 进房票据 / 打洞与中继 / 好友聊天 / 云端整合包）

云端 fyserver 是“启动器服务器”：它管理启动器账号、好友、聊天和小助手，还负责房间大厅、整合包清单和打洞协调。
真正的游戏服务端是房主电脑上的嵌入版 fyserver。成员的游戏连接经云端中继转给房主，中继复用游戏端口，按房间限速。不需要 frp。

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
  "tunnel": {"role", "relay": true, "hostOnline": 房主启动器此刻是否连着中继}
}
```

中继关闭时 `tunnel` 是 null，这时只能本机测试。

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

## 4. 中继（启动器内置隧道）

三种连接都是对云端游戏端口的 HTTP/1.1 GET，带下面三个头。云端校验通过后回 `101 Switching Protocols`，之后连接就是原始字节流：

```
Connection: Upgrade
Upgrade: fy-relay
X-Fy-Ticket: <进房票据>
```

| 路径 | 谁连 | 升级后的内容 |
|------|------|------|
| `/launcher/relay/host` | 房主，进房后一直保持 | 云端按行发 `open <连接号>`，每 10 秒发一次 `ping` |
| `/launcher/relay/connect` | 成员，每条游戏连接一条 | 接通后直通房主的嵌入版服务端 |
| `/launcher/relay/accept/<连接号>` | 房主，收到 `open` 后 | 另一头是对应成员的连接，房主把它接到本机嵌入版服务端 |

规则：

- 票据必须有效，没过期，账号此刻还在这个房间里，身份也要对：`host` 和 `accept` 只给房主，`connect` 只给成员。
- 房主没连着中继时，`connect` 返回 503；房主 10 秒内没接上，返回 504。这两种情况都不升级。
- 每个房间最多 64 条中继连接，超出返回 429。
- 云端每 10 秒核对一次成员身份。被踢、退房或房间关闭后，相关连接会断开。房主换了新的控制连接，旧的会被关掉。
- 两个方向共用每个房间的限速，默认 512KB/s，在后台设置。
- 成员那边，本地网关照常在每个 HTTP 请求上加 `X-Fy-Ticket`。房主的嵌入版服务端靠它识别成员，所以中继不改动内容。
- 中继关闭时开房、进房响应里的 `tunnel` 为 null。

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
| GET / PUT | `/tunnel`：`{enabled, relayBandwidthLimit}`，返回内容另含 `stats: {hosts, connections}` | serverConfig |

## 8. 数据文件

| 文件 | 内容 |
|---|---|
| `data/room-signing-key.pem` | 票据签名私钥（PKCS8），只有本机可读 |
| `data/launcher-relay.json` | 中继开关和限速（没有这个文件时默认开启，512KB/s） |
| `data/launcher-modpacks.json` | 云端整合包清单 |
| `data/social/launcher.json` | 本地存储模式下的启动器好友和聊天（数据库模式下用 `fy_launcher_*` 表） |
