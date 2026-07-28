# ZZZae

ZZZae 是 Windows x64 下的绝区零国服成就导出工具。

## 免责声明与风险提示
>[!Warning]
>本项目是与米哈游及《绝区零》官方无关的第三方开源工具，仅供个人成就数据备份与迁移使用。工具运行时会向游戏进程加载临时 Hook；此类行为可能违反游戏用户协议、运营规则或被反作弊系统识别，并可能导致账号警告、限制、封禁，以及数据或其他损失。
>
>使用者应在使用前自行了解并遵守所在地法律法规、游戏用户协议及相关规则，自行判断和承担全部风险。项目作者及贡献者不对因下载、安装、运行、修改或传播本工具而产生的账号处罚、封禁、数据丢失、财产损失或其他直接、间接损失承担责任。若无法接受上述风险，请勿使用本工具。

## 使用指南

1. 完全退出正在运行的绝区零。
2. 双击 `ZZZae.exe`。
3. 用上下箭头选择“从注册表读取”“手动指定游戏路径”或“退出 ZZZae”，按 Enter 确认。手动指定时可以把游戏目录或 `ZenlessZoneZero.exe` 拖入窗口；注册表或手动路径无效时，程序会显示具体原因，按 Enter 返回菜单重新选择；选择“退出”则直接正常退出。
4. 游戏路径确认后，同意 Windows 管理员权限提示。管理员实例会自动继续导出，不需要再次选择路径。
5. 正常登录并进入游戏。ZZZae 识别到完整成就响应后，会关闭本次启动的游戏。
6. 用上下箭头选择导出格式。
7. 看到“当前成就导出成功”后按 Enter 退出 ZZZae。等待成就数据时可按 `Ctrl+C` 取消。

可以在终端中指定游戏目录或游戏 EXE（路径含空格时必须保留引号）：

```powershell
.\ZZZae.exe --game "D:\Games\ZenlessZoneZero Game"
.\ZZZae.exe --game "D:\Games\ZenlessZoneZero Game\ZenlessZoneZero.exe"
```

两种写法任选一种。`--game` 只替代游戏路径定位，程序仍会检查 `version_info` 的国服正式渠道标记和 `GameAssembly.dll`，不会跳过兼容性检查。

输出文件位于启动 ZZZae 时的目录。每次运行只生成所选的一种文件：

- `ZZZae-achievements-日期时间.json`：成就数据备份，保留服务端返回的全部成就记录、完成时间，以及每条成就内尚未解释的原始 varint 字段，推荐用于长期备份和后续分析；
- `ZZZae-liyin-日期时间.json`：可导入 Liyin，只包含有完成证据的成就 ID；
- `ZZZae-uiaf-日期时间.json`：按绝区零 UIAF v1.2 提案生成的非官方实验格式，包含服务端实际返回的全部成就记录。

程序会在 `ZZZae.exe` 同目录追加写入 `ZZZae.log`。如果导出失败，请先检查日志内容，再将其提供给开发者排查；日志不记录网络包或成就记录内容，但可能包含本机游戏路径、系统信息和异常详情。

## 兼容性

从以下注册表项读取游戏路径：

```text
HKCU\Software\miHoYo\HYP\1_1\nap_cn\GameInstallPath
```

无参数启动时，程序会显示“注册表读取 / 手动指定 / 退出”选择菜单；注册表不存在或路径失效时，默认选择手动指定。也可以使用上述 `--game` 参数跳过菜单。ZZZae 不会扫描磁盘或修改注册表。

## 致谢与许可证

项目设计参考了 [Yae](https://github.com/HolographicHat/Yae)。感谢[HolographicHat](https://github.com/HolographicHat) 与 Yae 项目贡献者提供的实现思路

绝区零成就元数据[src\ZZZae.Protocol\Metadata\AchievementInfo.json](src\ZZZae.Protocol\Metadata\AchievementInfo.json)取自[zzz.liyin.space](https://github.com/Ticca-Liyin/zzz.liyin.space)

实验性成就交换格式参考 [UIGF-org 的 UIAF v1.1](https://uigf.org/zh/standards/uiaf.html) 和 UIGF 的多游戏分组思路；该引用不代表[提案](https://github.com/orgs/UIGF-org/discussions/18)已经成为正式规范

本仓库采用 GNU GPL v3，详见 [`LICENSE`](LICENSE)
