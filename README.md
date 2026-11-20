# DeepSeek 自动聊天回复

FF14 国服 Dalamud 插件。调用 DeepSeek 或其他 OpenAI 兼容服务自动回复玩家聊天消息，并发回原频道。支持小队（含跨服）、团队、私聊、说话、呼喊、喊话、部队、新人频道、PvP 战队，以及通讯贝和跨服通讯贝各 1–8 号频道。

## 编译与加载

项目面向 XIVLauncherCN 6.8.0-2 当前发布轨道的国服 Dalamud 15.0.4.0（API 15）和 .NET 10。重新编译需准备 .NET 10 或更高版本 SDK，以及从国服启动器更新取得的 API 15 Dalamud 文件。

```powershell
$env:DALAMUD_HOME = 'C:\你的国服 Dalamud 目录'
dotnet build .\DeepSeekAutoReply\DeepSeekAutoReply.csproj -c Release
```

`DALAMUD_HOME` 指向包含 `Dalamud.dll` 和 `FFXIVClientStructs.dll` 的目录。编译后，在游戏里打开 `/xlsettings`，在实验性设置的开发插件位置中添加生成的 `DeepSeekAutoReply.dll`，然后在插件安装器中启用开发插件。DLL 和清单位于 `DeepSeekAutoReply/bin/Release/`，同目录下的 `DeepSeekAutoReply/latest.zip` 包含可解压加载的文件。

## 配置自动回复

1. 输入 `/dsreply` 打开设置。
2. 填写 **API 地址**、**Token**、**模型 ID**，并选择 **AI 协议**。默认地址是 `https://api.deepseek.com`，默认模型是 `deepseek-flash`。Token 填原始密钥，模型 ID 填服务商提供的值。
3. 在**自动回复频道**中选择需要回复的频道，也可用「全选」或「全关」。新安装时默认开启小队、团队和私聊。升级保留原有选择，新增频道默认关闭。
4. 在**系统提示词**里填写你希望的回复风格，可使用多行。也可调整冷却时间和回复长度。点击保存后生效。

**Chat Completions** 请求 `/chat/completions`，**Responses** 请求 `/responses`。填写基础地址（如 `https://api.deepseek.com` 或带 `/v1` 的兼容地址）即可自动补全。也可填写完整端点，切换协议时插件会更新末尾端点。旧配置默认沿用 Chat Completions。两种协议都可用 `/dstest` 测试。[DeepSeek Responses 文档](https://api-docs.deepseek.com/api/create-response) · [OpenAI Responses 指南](https://developers.openai.com/api/docs/guides/migrate-to-responses)

插件跳过当前角色自己发出的消息。说话、呼喊和喊话的回复会在原地图和实例内提交。Token 保存在本机 Dalamud 插件配置文件中。接口失败时可在 `/xllog` 查看错误，日志省略密钥和聊天内容。

## 查询游戏资料

插件使用 Dalamud 解码的 `ItemPayload`、`QuestPayload` 和 `StatusPayload` 读取道具、任务和状态的 ID 与客户端名称。原始聊天宏可补充名称、成就和情感动作。消息被改写时，插件会重新解码原始载荷。启用「Garland Tools 查询」后，插件会匹配游戏数据表中的技能名称，AI 可根据上下文决定是否查询。查询会把游戏内容名称或 ID 发送到 Garland Tools 中文数据库。Chat Completions 和 Responses 都可使用。[Garland Tools 中文数据库](https://garlandtools.cn/db/)

读取道具名称时，插件把链接中的 `RawItemId` 交给 `ItemUtil.GetItemName`。该接口会处理 HQ、收藏品和任务道具，并为简体中文任务道具读取 `EventItem.Singular`。普通道具的说明、品级和基础属性可按 `ItemId` 从 `IDataManager.GetExcelSheet<Item>()` 读取。[ItemUtil 源码](https://github.com/ottercorp/Dalamud/blob/15.0.4.0/Dalamud/Utility/ItemUtil.cs) · [IDataManager 源码](https://github.com/ottercorp/Dalamud/blob/15.0.4.0/Dalamud/Plugin/Services/IDataManager.cs)

保存设置后，可输入 `/dstest 查一下轻量级全息投影手铠（ID 43543）怎么用`。开启日志时，在 `/xllog` 的 Debug 级别搜索 `Garland Tools`，可查看查询次数、未匹配次数及末次数据库 HTTP 状态。

输入 `/dsitem 43543` 可单独检查游戏客户端返回的名称。要检查真实聊天链接，请让队友发送一条「展示道具」，在 `/xllog` 的 Debug 级别搜索「游戏链接识别」，查看解码数量、载荷类型、ID 和名称是否读到。`/dstest` 用于测试文本和 AI 请求。

## 多轮对话

缓存按游戏发送者昵称和频道区分。获取到服务器名时，会用 `昵称@服务器` 区分同名角色。私聊、不同公开频道和 `/dstest` 各自保留上下文。插件请求 API 时会带上该会话的历史和系统提示词，只缓存已提交给游戏客户端的回复或已显示的测试回复。

默认每个会话缓存最近 1000 轮，每轮包含一条来信和一条回复。在 `/dsreply` 可设置 0–1000 轮。旧版默认 6 轮升级为 1000 轮，其余已保存的设置保留。设为 0 并保存会关闭并清空缓存。

历史存放在 Dalamud 插件配置目录下的 `conversation-cache.sqlite3`，按当前游戏角色隔离，登出或重启后仍可使用。「清空对话缓存」会清除数据库内所有角色的历史。更换已保存的 API 配置（含协议）或系统提示词也会清空历史。每个角色最多保留 128 个最近更新的会话。第三方模型的上下文上限由供应商决定，可按需调低轮数。

测试小队回复时，保存开启「小队（含跨服）」的配置，请队友发一条 `/p` 消息。打开 `/xllog` 并显示 Debug 级别，即可查看 `[Party]` 或 `[CrossParty]` 的请求、队伍状态和提交结果。

保存 API 配置后，在游戏里输入 `/dstest`，或输入 `/dstest 大肥鱼在吗？` 来指定测试文本。测试用已保存的系统提示词请求模型，把回复或错误显示在自己的聊天框里。连续输入两次可测试多轮回复。打开 `/xllog` 的 Debug 级别，搜索 `SQLite` 可查看数据库路径、读取轮数和写入或裁剪结果，搜索 `[dstest]` 可查看 HTTP 状态和响应诊断。日志省略昵称、消息正文和 Token。

在 `/dsreply` 中可以关闭「启用插件日志」，保存后停止在 `/xllog` 写入此插件的调试和警告日志。`/dstest` 仍会在本地聊天框显示结果。

[DalamudPluginsCN-Dev 开发文档](https://github.com/gamous/DalamudPluginsCN-Dev#开发-contribution) · [DeepSeek API 文档](https://api-docs.deepseek.com/)
