# Codex Pulse

一个 Windows 10/11 桌面小组件，把以下状态合并到同一个圆角面板：

- Codex 剩余用量、限额窗口和重置日期
- 实时上传、下载速度
- 全系统 CPU 使用率
- CPU Package 温度（由本机 Armoury Crate 硬件监控组件读取；传感器不可用时显示 `--°C`）
- 物理内存使用率

## 使用

1. 双击 `dist\CodexPulse.exe`。
2. 拖动小组件可改变位置；双击小组件恢复到任务栏右上方。
3. 右键小组件或托盘图标可刷新、隐藏、恢复位置、设置开机启动或退出。

Codex 用量每 20 秒扫描一次 `%USERPROFILE%\.codex\sessions` 中近期会话文件的
尾部，只解析 `token_count` 事件中的限额数据，不解析对话正文，也不上传任何
数据。Codex 产生一次新回复后，本地用量才会更新。

系统状态每秒采样一次。网速默认合计当前在线的物理以太网和 Wi-Fi 网卡，排除
回环、隧道、VPN 和常见虚拟网卡。

## 构建

```powershell
dotnet build .\CodexPulse.csproj -c Release
dotnet run --project .\tests\CodexPulse.SmokeTests\CodexPulse.SmokeTests.csproj -c Release
dotnet publish .\CodexPulse.csproj -c Release -r win-x64 --self-contained false `
  -p:PublishSingleFile=true -o .\dist
```
