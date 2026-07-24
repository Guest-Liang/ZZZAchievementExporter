# ZZZae

ZZZae 是 Windows x64 下的绝区零国服成就导出工具。

## 使用指南

1. 完全退出正在运行的绝区零。
2. 双击 `ZZZae.exe`，同意 Windows 管理员权限提示。
3. 正常登录并进入游戏。ZZZae 识别到完整成就响应后，会立即写两个文件并退出；游戏会继续运行。
4. 等待时可按 `Ctrl+C` 取消导出。

输出文件位于启动 ZZZae 时的目录。

- `ZZZae-full-日期时间.json`：完整备份，保留服务端返回的全部成就记录、完成时间、未知字段和原始包；
- `ZZZae-liyin-日期时间.json`：可导入 Liyin，只包含成就 ID。

文件名以及完整备份中的 `captured_at`、`finish_time_utc8` 都使用 UTC+8，并带有 `+08:00` 偏移。`finish_timestamp` 和 Liyin 的 `export_timestamp` 是 Unix 时间戳，本身与时区无关。

## 兼容性

游戏路径目前从以下注册表项读取：

```text
HKCU\Software\miHoYo\HYP\1_1\nap_cn\GameInstallPath
```

## 致谢与许可证

项目设计参考了 [Yae](https://github.com/HolographicHat/Yae)。感谢[HolographicHat](https://github.com/HolographicHat) 与 Yae 项目贡献者提供的实现思路。

绝区零成就元数据取自[绝区零 Liyin](https://github.com/Ticca-Liyin/zzz.liyin.space)。

本仓库采用 GNU GPL v3，详见 [`LICENSE`](LICENSE)。
